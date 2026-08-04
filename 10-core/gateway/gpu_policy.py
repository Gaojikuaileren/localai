"""P4-S10 · 权限档位六元组,落在 GPU 面上。

方案书 §14 行 2287 把「★ 权限档位六元组」从 P2 移进 P4,理由是 D37 给了它
**第一个真实客户:谁能改 GPU 状态**。§6.2 定义六个维度:

    (用户 / 设备 / Agent / 工具 / 参数 / 额度)

★★★ §6.2 的原话必须抄在这里,因为它是本文件存在的全部理由:
    「**参数级尤其关键**:同一个「写文件」工具,**路径参数决定它是安全还是灾难**。
      六元组里如果只实现了前四维,**等于没有实现**。」

────────────────────────────────────────────────────────────────────
★★ 开工前实测到的状态(2026-08-05):GPU 面**每个端点只有一行档位检查**,

    if classify_caller(request) == "remote-unauthenticated": return 401

于是除了「远程未认证」以外,**所有档位权限完全相同** —— 实跑确认:

  | 档位                   | 读快照 | 卸掉全部 | 独占租约 ttl=10^9 |
  |------------------------|--------|----------|-------------------|
  | trusted-local          | 200    | 放行     | 放行              |
  | unregistered-local     | 200    | 放行     | 放行              |
  | **denied-account**     | 200    | **放行** | **放行**          |
  | lan-edge               | 200    | 放行     | 放行              |
  | remote-unauthenticated | 401    | 401      | 401               |

`denied-account` 就是 §6.8 明文写着「**绝不放行**」的 `ai-asset` / `ai-exec`。
`chat_completions` 里对它有一条 403,而 GPU 面**一行都没有** ——
两条路径各写各的档位判断,漏掉一条没有任何东西会发现。
⇒ 本文件把档位能力做成**一张表**,并断言 GPU 面**不得再有散落的 classify_caller 比较**。
   这就是方案书「权限在服务端**按档位挂载**,而非运行时判断」在 HTTP 面上的等价形态:
   能力来自表,不来自散在各处的 if。
────────────────────────────────────────────────────────────────────
"""
from __future__ import annotations

import time
from dataclasses import dataclass, field
from typing import Dict, FrozenSet, List, Optional, Tuple

import gpu_broker

# ★ 六个维度逐维登记。反向全表盯着它:少答一维就判红,而不是"默认按没限制处理"。
DIMENSIONS: Tuple[str, ...] = ("user", "device", "agent", "tool", "param", "quota")

#: 每一维在 GPU 面上**具体落在哪里**。写成数据而不是注释,是为了能被断言检查。
DIMENSION_IMPL: Dict[str, str] = {
    "user":   "caller_identity.account_from_request → classify_caller(账户 allowlist,D30)",
    "device": "证书指纹 → 成员表反查 → 封顶 lan-device(主体不认客户端自报)",
    # ★ Agent 维今天的答案不是"没做",是**结构上不存在 agent 主体**:
    #   P4-S1 已断言 `gpu.*` 永不进入任何 Agent 工具池(赶在工具池存在之前写的)。
    #   所以这一维是【已答】,答案是"不可能有 agent 拿到 GPU 工具"。
    "agent":  "P4-S1 架构断言:gpu.* 永不进入任何 Agent 工具池 ⇒ 不存在 agent 主体",
    "tool":   "ROUTE_TIERS 登记路由 + 本表的 action(read/lease/change_resident/unload_all)",
    "param":  "本表:lease kind 白名单 · ttl 上限 · 组件数上限 · ★ 空集合单列一维",
    "quota":  "本表:每分钟变更次数 · 并发租约数上限(进程内计数,与 Broker 同一进程)",
}

# 动作维(「工具」维在 GPU 面的粒度)。★ 反向全表:新增动作必须登记进每一档。
ACTIONS: Tuple[str, ...] = ("read", "lease", "change_resident", "unload_all")


@dataclass(frozen=True)
class Caps:
    """一个档位在 GPU 面上的全部能力。**六维里的后三维都在这里。**"""
    tier: str
    # ── 工具维 ──
    actions: FrozenSet[str]
    # ── 参数维 ★ ──
    lease_kinds: FrozenSet[str]       # 允许申请哪几种租约
    max_ttl_s: float                  # ttl 上限 —— 不封顶等于"永不过期的租约"
    max_components: int               # 一次请求最多点名几个组件
    # ── 额度维 ★ ──
    changes_per_min: int              # 变更类请求的每分钟上限
    max_leases: int                   # 同时持有的租约上限
    why: str = ""                     # 这一档为什么是这样 —— 写给下一个改它的人

    def allows(self, action: str) -> bool:
        return action in self.actions


# ══════════════════════════════════════════════════════════════════════
#  档位能力表
#
#  ★★ 这是**白名单**。表外的档位 → `DENY_ALL`,不是"按最宽处理"。
#     新增一个档位而不登记,反向全表断言会判红(见 test_gpu_policy.py)。
#  ★ `lan-device` 不是 classify_caller 的返回值,是**证书指纹解析出来的封顶档** ——
#    它必须在表里,否则副机的每一次请求都会落进 DENY_ALL。
# ══════════════════════════════════════════════════════════════════════

DENY_ALL = Caps(
    tier="<unknown>", actions=frozenset(), lease_kinds=frozenset(),
    max_ttl_s=0.0, max_components=0, changes_per_min=0, max_leases=0,
    why="表外档位一律拒绝 —— 加一个新档位,默认落在【什么都不能做】那一边",
)

_ALL_LEASE_KINDS = frozenset(gpu_broker.LEASE_KINDS)
#: 独占型租约:一旦发出,期间拒发一切新租约。★ 副机拿到它 = 冻住整台中枢。
_EXCLUSIVE_KINDS = frozenset(
    k for k, v in gpu_broker.LEASE_KINDS.items() if getattr(v, "exclusive", False))

TIER_CAPS: Dict[str, Caps] = {
    "trusted-local": Caps(
        tier="trusted-local",
        actions=frozenset(ACTIONS),
        lease_kinds=_ALL_LEASE_KINDS,
        max_ttl_s=1800.0, max_components=16,
        changes_per_min=60, max_leases=16,
        why="屏幕前就是机主(账户在 caller-accounts.toml 的 allowlist 里)。"
            "唯一能『卸掉全部』的档位 —— 那是把整台中枢清空,应当由主机侧的人做。",
    ),
    "lan-device": Caps(
        tier="lan-device",
        # ★ 可改驻留集合(否则副机上刚做出来的组件面板会变成只读,是产品回退),
        #   但**不许『卸掉全部』** —— 见 max/why。
        actions=frozenset({"read", "lease", "change_resident"}),
        # ★ 参数维:独占型租约不给副机 —— 它会让整台中枢在这期间拒发一切新租约,
        #   而副机上的人看不到主机屏幕,判断不了现在能不能冻。
        lease_kinds=_ALL_LEASE_KINDS - _EXCLUSIVE_KINDS,
        max_ttl_s=900.0, max_components=16,
        changes_per_min=20, max_leases=8,
        why="已配对的局域网设备(证书指纹经成员表反查)。§6.3 的 trusted-lan 一档。"
            "★『空集合 = 卸掉全部』被单独挡住:那是一次能让整台中枢空掉的动作,"
            "而它和一次普通变更**长得一模一样**,只差参数 —— 这正是 §6.2 说"
            "『参数决定它是安全还是灾难』的字面情形。",
    ),
    "unregistered-local": Caps(
        tier="unregistered-local",
        actions=frozenset({"read"}),
        lease_kinds=frozenset(), max_ttl_s=0.0, max_components=0,
        changes_per_min=0, max_leases=0,
        why="D30『降档不断连』:本机但不在账户 allowlist 里(实测本机就有两个外部 AI 沙箱账户)。"
            "读得到状态,改不动 —— 改 GPU 状态比聊天高一个量级,不该跟着『不断连』一起放行。",
    ),
    "lan-edge": Caps(
        tier="lan-edge",
        actions=frozenset(),
        lease_kinds=frozenset(), max_ttl_s=0.0, max_components=0,
        changes_per_min=0, max_leases=0,
        why="★ 代理进程档,**非业务档**。带指纹的请求会被解析成 lan-device;"
            "不带指纹却打到 GPU 面 = 代理进程自己在改中枢状态,那不该发生。"
            "与 E1_OVERRIDE_ALLOWED_TIERS 同一条纵深防御纪律:代理永不落业务档。",
    ),
    "denied-account": Caps(
        tier="denied-account",
        actions=frozenset(),
        lease_kinds=frozenset(), max_ttl_s=0.0, max_components=0,
        changes_per_min=0, max_leases=0,
        why="§6.8 明文『绝不放行』的 ai-asset / ai-exec。★ 2026-08-05 之前 GPU 面**放行它** —— "
            "chat 那条路径有 403,GPU 面一行都没有,两处各写各的判断,漏掉一条没人发现。",
    ),
    "remote-unauthenticated": Caps(
        tier="remote-unauthenticated",
        actions=frozenset(),
        lease_kinds=frozenset(), max_ttl_s=0.0, max_components=0,
        changes_per_min=0, max_leases=0,
        why="身份不可解析(含 ::1)。fail-closed。",
    ),
}


def caps_for(tier: str) -> Caps:
    """★ 表外一律 DENY_ALL。这条是本文件的 fail-closed 支点。"""
    return TIER_CAPS.get(tier, DENY_ALL)


# ── 额度维:进程内令牌桶 ────────────────────────────────────────────
#   ★ 与 Broker 同进程、同单写者,所以计数是一致的(不需要跨进程协调)。
#   ★ 桶按 (tier, holder) 分:一个副机刷爆自己的额度,不该把主机也拖下水。
_WINDOW_S = 60.0
_hits: Dict[Tuple[str, str], List[float]] = {}


def _sweep(key: Tuple[str, str], now: float) -> List[float]:
    xs = [t for t in _hits.get(key, ()) if now - t < _WINDOW_S]
    _hits[key] = xs
    return xs


def quota_state(tier: str, holder: str = "") -> Tuple[int, int]:
    """(本窗口已用, 上限)。★ 只读,不计数 —— 供响应体如实回报。"""
    now = time.monotonic()
    return len(_sweep((tier, holder), now)), caps_for(tier).changes_per_min


def reset_quota() -> None:
    """★ 只给测试用。生产里没有任何调用点 —— 断言会检查这一点:
    一个能被业务代码调用的『清空额度』就等于额度维没有实现。"""
    _hits.clear()


@dataclass
class Decision:
    ok: bool
    code: str = ""          # denied_tier | denied_action | denied_param | denied_quota
    message: str = ""
    dimension: str = ""     # ★ 拒绝时必须点名是**哪一维**拦的
    detail: Dict = field(default_factory=dict)

    def to_json(self) -> Dict:
        return {"ok": self.ok, "code": self.code, "message": self.message,
                "dimension": self.dimension, "detail": dict(self.detail)}


def check(tier: str, action: str, *, components: Optional[List[str]] = None,
          lease_kind: str = "", ttl_s: Optional[float] = None,
          holder: str = "", count_quota: bool = True) -> Decision:
    """六元组的**唯一**判定入口。

    ★★ 拒绝时必须点名是哪一维拦的(`dimension`)。合并成一句「权限不足」会让人
       去改错的东西 —— 撞额度的人会去申请提权,而他其实只要等一分钟。
       这和 §8.1 那条「两种撞墙必须分开说」是同一条纪律。
    """
    caps = caps_for(tier)

    # ── ① 档位维(用户 / 设备已在调用前解析成 tier)──
    if not caps.actions:
        return Decision(False, "denied_tier",
                        f"档位 {tier} 在 GPU 面上没有任何权限。{caps.why}",
                        dimension="user" if tier == "denied-account" else "device",
                        detail={"tier": tier})

    # ── ② 工具维 ──
    if action not in ACTIONS:
        # ★ 未登记的动作 fail-closed —— 与准入白名单同款:漏了要响亮报错,不是默认放行
        return Decision(False, "denied_action", f"未登记的动作 {action}",
                        dimension="tool", detail={"action": action})
    if not caps.allows(action):
        return Decision(False, "denied_action",
                        f"档位 {tier} 不能做 {action}。{caps.why}",
                        dimension="tool", detail={"tier": tier, "action": action,
                                                  "allowed": sorted(caps.actions)})

    # ── ③ 参数维 ★ 六元组里最要紧的一维 ──
    comps = components or []
    if len(comps) > caps.max_components:
        return Decision(False, "denied_param",
                        f"一次最多点名 {caps.max_components} 个组件,收到 {len(comps)}",
                        dimension="param", detail={"max": caps.max_components, "got": len(comps)})
    if lease_kind:
        if lease_kind not in caps.lease_kinds:
            excl = lease_kind in _EXCLUSIVE_KINDS
            return Decision(False, "denied_param",
                            f"档位 {tier} 不能申请 {lease_kind} 类租约"
                            + ("(它是**独占**的:期间中枢拒发一切新租约,"
                               "而你看不到主机屏幕,判断不了现在能不能冻)" if excl else ""),
                            dimension="param",
                            detail={"kind": lease_kind, "allowed": sorted(caps.lease_kinds)})
    if ttl_s is not None and ttl_s > caps.max_ttl_s:
        return Decision(False, "denied_param",
                        f"ttl 上限 {caps.max_ttl_s:.0f} 秒,收到 {ttl_s:.0f} —— "
                        "不封顶等于一份**永不过期**的租约,而租约的全部意义就是会过期",
                        dimension="param", detail={"max_ttl_s": caps.max_ttl_s, "got": ttl_s})

    # ── ④ 额度维 ★ ──
    if action in ("lease", "change_resident", "unload_all"):
        now = time.monotonic()
        key = (tier, holder)
        used = _sweep(key, now)
        if len(used) >= caps.changes_per_min:
            return Decision(False, "denied_quota",
                            f"每分钟最多 {caps.changes_per_min} 次变更,本窗口已用 {len(used)}。"
                            "★ 这不是权限不够,是**太快了** —— 等一分钟即可,不必去申请提权",
                            dimension="quota",
                            detail={"per_min": caps.changes_per_min, "used": len(used)})
        if count_quota:
            used.append(now)

    return Decision(True, detail={"tier": tier, "action": action})


def resolve_action(components: Optional[List[str]], is_change: bool) -> str:
    """把一次请求映射到动作。

    ★★★ 这个函数是「参数决定它是安全还是灾难」的**字面落点**:
      `components == []` 与 `components == [x]` 走的是同一个端点、同一段代码,
      HTTP 上长得一模一样 —— 但前者的意思是**卸掉全部**。
      所以它必须被映射成**另一个动作**,而不是同一个动作的一个取值。
    """
    if not is_change:
        return "read"
    return "unload_all" if not components else "change_resident"
