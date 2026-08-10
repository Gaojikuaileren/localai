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
    "tool":   "ROUTE_TIERS 登记路由 + 本表的 action"
              "(read/lease/change_resident/unload_all/permit_on_demand)",
    "param":  "本表:lease kind 白名单 · ttl 上限 · 组件数上限 · ★ 空集合单列一维",
    "quota":  "本表:变更桶与租约桶**分开**计(QUOTA_BUCKETS)· 并发租约数上限"
              "(进程内计数,与 Broker 同一进程)",
}

# 动作维(「工具」维在 GPU 面的粒度)。★ 反向全表:新增动作必须登记进每一档。
#
# ★★★ 2026-08-06 审计 B6 补 `permit_on_demand`:写 `permitted_on_demand_set`
#   (「哪些模型允许被自动装卸」的那份授权)。方案书 §8.1.7 与本仓多处注释都写着
#   「**只有主机变更面能写**」,而**代码里一道闸都没有** —— 实测副机可以给自己发这份授权。
#   ⇒ 它必须是【另一个动作】,而不是 change_resident 的一个字段:
#     和「空集合 = 卸掉全部」同一条理由(§6.2「参数决定它是安全还是灾难」),
#     长得一模一样的一次 POST,多带一个数组就变成了"授权系统自己动显存"。
ACTIONS: Tuple[str, ...] = ("read", "lease", "change_resident", "unload_all",
                            "permit_on_demand")

# ══════════════════════════════════════════════════════════════════════
#  ★★★ 额度桶归属(2026-08-06 审计 B4)。
#
#  **实测的病**:`/v1/gpu/lease/renew` 归 `lease` 档,而 `lease` 与 `change_resident`
#  **共用同一个桶、同一个上限**(`changes_per_min`)。于是:
#    lan-device 连续 20 次续租(心跳)之后,用户点一次确定 → `denied_quota`,
#    实跑第 21 次判红。而钉住"续租不吃变更配额"的那条断言查的是
#    **源码里有没有 `change_resident` 这个词** —— 恒绿(ASSERTION-PITFALLS 第 9 条:
#    判据问的是"我是什么身份",不是"我做不做得到")。
#
#  ⇒ 桶按**动作类别**分。"不吃变更配额"从此是一句**可以为假**的话:
#    把续租打满,再来一次 change_resident,它必须还能过 —— 那才是这句话的判据。
#  ★ 反向全表:ACTIONS 里每一个都必须登记在这里(空串 = 不计额度),漏一个判红。
# ══════════════════════════════════════════════════════════════════════
QUOTA_BUCKET_CHANGE = "change"
QUOTA_BUCKET_LEASE = "lease"

QUOTA_BUCKETS: Dict[str, str] = {
    "read":              "",                    # 读不限流 —— 限流读会让界面反而看不见发生了什么
    "lease":             QUOTA_BUCKET_LEASE,    # ★ 心跳节奏,与用户的变更配额**互不相干**
    "change_resident":   QUOTA_BUCKET_CHANGE,
    "unload_all":        QUOTA_BUCKET_CHANGE,   # 与变更同桶:它就是变更的极端取值
    "permit_on_demand":  QUOTA_BUCKET_CHANGE,   # 授权也是一次人的动作,同属变更节奏
}


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
    changes_per_min: int              # 变更桶(change_resident / unload_all / permit_on_demand)
    max_leases: int                   # 同时持有的租约上限
    # ★★ 租约桶**单独**一个上限(审计 B4)。它必须远大于变更上限:
    #   续租是心跳(TTL/3 一次),而变更是人按确定。两者共用一个数,
    #   就是让"客户端活着"这件事把用户的配额吃光 —— 实跑第 21 次判红。
    #   ★ 默认给 0 而不是"跟着 changes_per_min":漏填要落在**拒绝**那一边。
    leases_per_min: int = 0
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
    leases_per_min=0,
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
        # ★ 租约桶按心跳节奏定:TTL/3 一次 ⇒ 一台约 2 次/分。给 120 是**给多台留余量**,
        #   而不是"随便给个大数":它仍然封顶,一个跑飞的客户端还是会被拦住。
        leases_per_min=120,
        why="屏幕前就是机主(账户在 caller-accounts.toml 的 allowlist 里)。"
            "唯一能『卸掉全部』的档位 —— 那是把整台中枢清空,应当由主机侧的人做。"
            "★ 也是唯一能写『按需授权槽』的档位(§8.1.7:只有主机变更面能写)——"
            "那份授权的意思是「允许系统在我没同意的情况下动这些模型的显存」,"
            "而副机上的人看不到主机屏幕,不该替机主签这个字。",
    ),
    "lan-device": Caps(
        tier="lan-device",
        # ══════════════════════════════════════════════════════════════
        #  ★★★ 2026-08-07(D?):`change_resident` **从这一档拿掉**。
        #
        #  此前这里是 `{"read", "lease", "change_resident"}`,注释写着
        #  「可改驻留集合(否则副机上刚做出来的组件面板会变成只读,是产品回退)」。
        #  **那条注释把产品愿望写成了权限判据**,而方案书 §「四个集合」表(行 1550)
        #  对 `intended_resident_set` 的「谁能写」一栏写的是:**只有主机变更面**。
        #
        #  ★ 08-06 审计 B6 补的那道闸**只补了一半**:它拦住了 `permitted_on_demand`
        #    (第二张表行 1553,同样是「只有主机变更面」),却让写 `intended` 本身
        #    继续放行 —— 两行规格一模一样,闸只装了一道。实测确认:`lan-device` 身份
        #    POST /v1/gpu/intended 带非空 components,**过**。
        #
        #  ★★ 产品代价【真实存在,如实记下】:副机上的组件勾选面板变为只读。
        #    但它**不是**「副机什么都动不了」——`lease` 留着,于是 D87① 的
        #    「意图即起」(POST /v1/gpu/intent,归 lease 档)在副机上**照常工作**:
        #    副机上的人开始用某个功能,模型仍会为他起来。变成只读的只有
        #    「替机主改写常驻清单」这一件 —— 而那正是规格要求由主机屏幕前的人做的事。
        # ══════════════════════════════════════════════════════════════
        actions=frozenset({"read", "lease"}),
        # ★ 参数维:独占型租约不给副机 —— 它会让整台中枢在这期间拒发一切新租约,
        #   而副机上的人看不到主机屏幕,判断不了现在能不能冻。
        lease_kinds=_ALL_LEASE_KINDS - _EXCLUSIVE_KINDS,
        max_ttl_s=900.0, max_components=16,
        # ★ 变更桶归零:这一档在【工具】维就已经没有任何落在 change 桶里的动作了
        #   (change_resident / unload_all / permit_on_demand 一个都没有)。
        #   留着 20 会是一个**永远用不到、却看起来像是给了权限**的数字;
        #   而将来谁把某个变更动作加回这一档,它会先撞上额度 0 而被拒 ——
        #   与 leases_per_min 默认 0 同一条纪律:漏填落在【拒绝】那一边。
        changes_per_min=0, max_leases=8,
        leases_per_min=60,
        why="已配对的局域网设备(证书指纹经成员表反查)。§6.3 的 trusted-lan 一档。"
            "★★★ **不能写驻留集合** —— 方案书四集合表:`intended_resident_set` 的"
            "「谁能写」是【只有主机变更面】。改常驻清单的意思是「替机主决定这台机器"
            "常年拿多少显存干什么」,而副机上的人看不到主机屏幕、也不承担后果。"
            "★ 副机不是没得动:`lease` 还在 ⇒「意图即起」照常 —— 他要用的功能仍会为他起来,"
            "只是不落进 committed。"
            "★『空集合 = 卸掉全部』与『写按需授权槽』本来就各自被单列挡住;"
            "现在连普通变更也挡住了,三者归位到同一条规格上。",
    ),
    "unregistered-local": Caps(
        tier="unregistered-local",
        actions=frozenset({"read"}),
        lease_kinds=frozenset(), max_ttl_s=0.0, max_components=0,
        changes_per_min=0, max_leases=0,
        why="D30『降档不断连』:本机但不在账户 allowlist 里(实测本机就有两个外部 AI 沙箱账户)。"
            "读得到状态,改不动 —— 改 GPU 状态比聊天高一个量级,不该跟着『不断连』一起放行。",
    ),
    # ★★★★ V32b(用户裁定 2026-08-10):「**没查出来**」从 unregistered-local 里拆出来。
    #   能力与 unregistered-local **逐字相同** —— 本次**不改任何权限**。
    #   ★ 为什么不趁机收紧:被降到这一档的**可能就是机主自己**(一次端口表/WMI 抖动即可),
    #     收紧等于让一次瞬时故障把机主本人锁在门外,而他看不到任何原因。
    #   ★★ 为什么不趁机放宽:那就等于宣布"认不出人也当自己人",D30 修正推翻的正是这条。
    #   ⇒ 这一档该算出境 sink 还是本地 sink 是 **D81 待裁 1**;
    #     `sink-axis-change-list-2026-08-06.md` 明写「不得先于待裁 1 动手」。
    #     本车道只做**拆分 + 可观测**,把那一刀留给裁定。
    #   ★ 拆分本身就是那条裁定的前置:不拆就裁 = 埋一颗「机主偶发失能且无提示」的雷。
    "identity-unresolved": Caps(
        tier="identity-unresolved",
        actions=frozenset({"read"}),
        lease_kinds=frozenset(), max_ttl_s=0.0, max_components=0,
        changes_per_min=0, max_leases=0,
        why="认人链断了(端口表抖动 / WMI 超时 / 进程已退出),**不是**「这个人没登记」。"
            "★ 能力与 unregistered-local 逐字相同:本次只拆分与留痕,不改权限(D81 待裁 1)。"
            "★★ 与它的**区别**在别处:每一次落到这一档都会被计数并写进 "
            "identity_unresolved.jsonl(带断在哪一环),而 unregistered-local 是个稳定事实,不需要告警。",
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
#   ★ 桶按 (tier, holder, bucket) 分:
#     · holder 那一维 —— 一个副机刷爆自己的额度,不该把主机也拖下水。
#       ★★ 而 holder 现在**只来自服务端解析**(审计 B1/B3)。此前它是客户端自报的,
#         于是"每换一个名字就是一个新桶",额度维形同虚设 ——
#         实测:同一 holder 打 22 次第 21 次被拒;换成 PC-A-0…24 打 25 次 **25/25 全过**。
#     · bucket 那一维 —— 见 QUOTA_BUCKETS 上方那段(审计 B4)。
_WINDOW_S = 60.0
_hits: Dict[Tuple[str, str, str], List[float]] = {}


def _sweep(key: Tuple[str, str, str], now: float) -> List[float]:
    xs = [t for t in _hits.get(key, ()) if now - t < _WINDOW_S]
    _hits[key] = xs
    return xs


def bucket_of(action: str) -> str:
    """动作 → 额度桶名。★ 表外动作落**变更桶**(最严的那一个),不是"不计额度"。

    ★ 这条是 fail-closed 的方向:加一个新动作却忘了登记,它会被算进最严的桶而被拦住,
      而不是获得一条免额度的通道。反向全表另有一条断言要求逐个登记。
    """
    return QUOTA_BUCKETS.get(action, QUOTA_BUCKET_CHANGE)


def bucket_cap(tier: str, bucket: str) -> int:
    """某个桶在某一档上的每分钟上限。"""
    caps = caps_for(tier)
    return caps.leases_per_min if bucket == QUOTA_BUCKET_LEASE else caps.changes_per_min


def quota_state(tier: str, holder: str = "",
                action: str = "change_resident") -> Tuple[int, int]:
    """(本窗口已用, 上限)。★ 只读,不计数 —— 供响应体如实回报。

    ★ 必须带 action:两个桶的数不一样,不带的话回给用户的"已用 N/上限 M"
      有一半机会说的是另一个桶的账 —— 那比不说更坏(他会照着一个假数去等)。
    """
    now = time.monotonic()
    b = bucket_of(action)
    return len(_sweep((tier, holder, b), now)), bucket_cap(tier, b)


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
    #   ★★ 桶按动作类别分(审计 B4)。续租(lease)与点确定(change_resident)
    #     **不在同一个桶里**,所以"续租不吃变更配额"这句话才可以为假、才检得出来。
    bucket = bucket_of(action)
    if bucket:
        now = time.monotonic()
        key = (tier, holder, bucket)
        cap = bucket_cap(tier, bucket)
        used = _sweep(key, now)
        if len(used) >= cap:
            _what = "续租/申请租约" if bucket == QUOTA_BUCKET_LEASE else "变更"
            return Decision(False, "denied_quota",
                            f"每分钟最多 {cap} 次{_what},本窗口已用 {len(used)}。"
                            "★ 这不是权限不够,是**太快了** —— 等一分钟即可,不必去申请提权",
                            dimension="quota",
                            # ★ 桶名如实回带:不带的话,撞了租约桶的人会去看变更上限,
                            #   两个数对不上,而他没有任何办法知道是两个桶。
                            detail={"per_min": cap, "used": len(used), "bucket": bucket})
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
