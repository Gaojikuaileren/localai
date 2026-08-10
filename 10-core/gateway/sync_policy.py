"""P4-S13 · 同步面的档位能力表(六元组在同步面的落点,D86)。

★ 为什么单开一份而不是塞进 `gpu_policy`:
  六元组的**档位词汇**(trusted-local / lan-device / …)是**通用**的,
  但**动作词汇**是**分面**的 —— GPU 面是 read/lease/change_resident/unload_all,
  同步面是 read/write。把同步动作塞进 `gpu_policy`,文件名就成了谎。
  ⇒ 档位表各自一份,但**用一条跨表断言把两边的档位集合绑死** ——
    新增一个档位只改一边,反向全表会判红。

★★ 同步面最要紧的一维仍然是**参数维**(§6.2:「参数决定它是安全还是灾难」):
  同一个 `POST /v1/sync/push`,推一条**家庭**待办是正常同步,
  推一条**个人**待办就是**把私人东西送到另一台机器上** —— 而两者长得一模一样。
  ⇒ 范围判据在服务端(`sync_store.in_scope`),而且**不可撤销的错误不能只靠客户端把关**。
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Dict, FrozenSet, Tuple

import gpu_policy
import sync_store

#: 同步面的动作。★ 反向全表:新增动作必须在每一档里有明确答案。
ACTIONS: Tuple[str, ...] = ("sync_read", "sync_write")


@dataclass(frozen=True)
class SyncCaps:
    tier: str
    actions: FrozenSet[str]
    #: 参数维:一次最多推几条(一次推一万条不是正常同步,是出错了或被滥用)
    max_batch: int
    #: 额度维:每分钟最多推几次
    pushes_per_min: int
    why: str = ""


DENY_ALL = SyncCaps("<unknown>", frozenset(), 0, 0,
                    "表外档位一律拒绝 —— 加一个新档位,默认落在【什么都不能做】那一边")

TIER_CAPS: Dict[str, SyncCaps] = {
    "trusted-local": SyncCaps(
        "trusted-local", frozenset(ACTIONS), max_batch=200, pushes_per_min=120,
        why="主机本机。共享数据的物理落点就在这台机器上。",
    ),
    "lan-device": SyncCaps(
        # ★ 副机**必须能写** —— 否则「副机提升为共享」这件事永远同步不过来,
        #   而那正是 D86 要解决的原始诉求。
        "lan-device", frozenset(ACTIONS), max_batch=200, pushes_per_min=120,
        why="已配对的局域网设备。★ 同步是**双向**的:副机上提升的共享会话要能推上来,"
            "所以它必须有 sync_write —— 这与 GPU 面不同(那边副机不能『卸掉全部』)。"
            "安全性由**范围判据**兜底:个人待办/未共享会话在服务端就被拒收。",
    ),
    "unregistered-local": SyncCaps(
        # ★ 与 GPU 面同一判据:D30「降档不断连」让它还能聊天,
        #   但**写共享数据**是另一个量级 —— 它能把两台机器上的共享内容改掉。
        "unregistered-local", frozenset({"sync_read"}), max_batch=0, pushes_per_min=0,
        why="D30 降档不断连:读得到,但写不了。写共享数据会影响另一台机器,"
            "不该跟着「不断连」一起放行。",
    ),
    # ★★★★ V32b:「没查出来」从 unregistered-local 里拆出来(理由见 gpu_policy 同名条目)。
    #   ★ 能力与 unregistered-local **逐字相同**:sync_read,不能写。本次不改任何权限。
    "identity-unresolved": SyncCaps(
        "identity-unresolved", frozenset({"sync_read"}), max_batch=0, pushes_per_min=0,
        why="认人链断了,不是「这个人没登记」。能力与 unregistered-local 逐字相同 —— "
            "本次只拆分与留痕(D81 待裁 1 未裁,不得先于它动手)。",
    ),
    "lan-edge": SyncCaps(
        "lan-edge", frozenset(), 0, 0,
        why="★ 代理进程档、非业务档。带指纹的请求会被解析成 lan-device;"
            "不带指纹却打到同步面 = 代理进程自己在改共享数据,那不该发生。",
    ),
    "denied-account": SyncCaps(
        "denied-account", frozenset(), 0, 0,
        why="§6.8 明文「绝不放行」的 ai-asset / ai-exec。"
            "★ 共享数据里有会话正文 —— 让隔离账户读到等于把对话内容漏给它。",
    ),
    "remote-unauthenticated": SyncCaps(
        "remote-unauthenticated", frozenset(), 0, 0,
        why="身份不可解析(含 ::1 —— 解析不出来就按远程处理)。fail-closed。"
            "★ 同步面比 GPU 面更该守住这条:共享数据里有**会话正文**,"
            "读一次就是把两台机器上的对话内容整个漏出去。",
    ),
}


def caps_for(tier: str) -> SyncCaps:
    """★ 表外一律 DENY_ALL —— 与 GPU 面同一条 fail-closed 支点。"""
    return TIER_CAPS.get(tier, DENY_ALL)


#: ★★ 跨表断言用:两个面的**档位集合必须一致**。
#:    新增一个档位只改一边 ⇒ 另一边把它落进 DENY_ALL ⇒ 那台设备会莫名其妙什么都做不了,
#:    而且**不会有任何东西报错** —— 正是本项目最恨的静默失效。
def tiers_match_gpu() -> Tuple[bool, set]:
    a, b = set(TIER_CAPS), set(gpu_policy.TIER_CAPS)
    return (a == b, a ^ b)


# ── 额度维:与 GPU 面同款进程内令牌桶,但**各记各的** ──────────────
#   ★ 不共用一个桶:同步和改 GPU 状态是两件事,一件刷爆不该把另一件也堵死。
import time as _time                                          # noqa: E402

_WINDOW_S = 60.0
_hits: Dict[Tuple[str, str], list] = {}


def reset_quota() -> None:
    """★ 只给测试用。断言检查生产代码里没有调用点 ——
    一个能被业务调用的『清空额度』就等于额度维没有实现。"""
    _hits.clear()


@dataclass
class Decision:
    ok: bool
    code: str = ""
    message: str = ""
    dimension: str = ""
    detail: Dict = None      # type: ignore[assignment]

    def to_json(self) -> Dict:
        return {"ok": self.ok, "code": self.code, "message": self.message,
                "dimension": self.dimension, "detail": dict(self.detail or {})}


def check(tier: str, action: str, *, batch: int = 0, holder: str = "",
          count_quota: bool = True) -> Decision:
    """同步面的唯一判定入口。★ 拒绝必须点名**是哪一维**拦的(与 GPU 面同一条纪律)。"""
    caps = caps_for(tier)
    if not caps.actions:
        return Decision(False, "denied_tier",
                        f"档位 {tier} 在同步面上没有任何权限。{caps.why}",
                        "user" if tier == "denied-account" else "device", {"tier": tier})
    if action not in ACTIONS:
        return Decision(False, "denied_action", f"未登记的动作 {action}", "tool", {"action": action})
    if action not in caps.actions:
        return Decision(False, "denied_action",
                        f"档位 {tier} 不能做 {action}。{caps.why}", "tool",
                        {"tier": tier, "action": action, "allowed": sorted(caps.actions)})
    # ── 参数维 ★ ──
    if batch > caps.max_batch:
        return Decision(False, "denied_param",
                        f"一次最多推 {caps.max_batch} 条,收到 {batch} —— "
                        "一次推这么多不是正常同步,是出错了或被滥用",
                        "param", {"max": caps.max_batch, "got": batch})
    # ── 额度维 ★ ──
    if action == "sync_write":
        now = _time.monotonic()
        key = (tier, holder)
        used = [t for t in _hits.get(key, ()) if now - t < _WINDOW_S]
        _hits[key] = used
        if len(used) >= caps.pushes_per_min:
            return Decision(False, "denied_quota",
                            f"每分钟最多推 {caps.pushes_per_min} 次,本窗口已用 {len(used)}。"
                            "★ 这不是权限不够,是**太快了**",
                            "quota", {"per_min": caps.pushes_per_min, "used": len(used)})
        if count_quota:
            used.append(now)
    return Decision(True, detail={"tier": tier, "action": action})
