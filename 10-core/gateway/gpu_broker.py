"""P4-S3 · GPU Broker 骨架:单写者 · 世代号 · 只读快照 · 1 Hz 采样。

★ 本片【只读】。没有任何变更端点 —— 预留、装载、租约都属于 S4。
  先把"谁是权威、快照长什么样、世代号怎么涨"立起来,再谈让别人改它。

形状由 2026-08-04 的两条用户裁定确定(见 decision-packets/p4-broker-shape-2026-08-04.md):

  ① **租约不挺过中枢重启** ⇒ 状态是**进程内**的,不落 PG。
     理由:租约的全部意义就是「持有者死了就过期」,而重启意味着每个持有者都已经死了。
     代价已记账:世代号重启后从 0 重来 —— 而客户端拿旧世代号提交会得到 409 + 全量快照,
     与"世代号对不上"完全同一条路径,本来就要实现。

  ② **Broker 直接进 gateway**,不新起进程。
     ⇒ 由此得到一条**硬约束,写在这里以免以后被违反**:
       ★★ 锁只保护状态转换,**绝不跨 await 网络 I/O**。
          这个进程同时持有 300 秒的流式聊天连接;在锁内 await 网络 = 把聊天卡死。
       ★★ 采样器是独立 task,**它崩了必须看得见**:异常被吞掉的采样器 =
          快照永远停在旧值,而调用方以为它是新的 —— 那是本项目最恨的静默失效。
          故:采样失败会写进快照的 sampler_error / stale 字段,不是记个日志了事。

D37 四件套在本片的落点(其余留给 S4):
  · 单一权威 + 副本 → 本模块是唯一权威;客户端拿到的是**快照副本**,只读;
  · 世代号        → generation,与状态变更在**同一把锁下** +1;
  · 推送非轮询     → S5(本片只给拉取端点,先把快照定形);
  · 预留先于装载   → S4。
"""

from __future__ import annotations

import asyncio
import secrets
import time
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple

# ★ 判定内核复用 gpu-broker/vram_gate.py —— §8.1 规则 18「预览与准入必须是同一段代码」。
#   Broker 与 CLI 必须共用同一份 evaluate();这里只做状态,不重写任何算术。
_VRAM_GATE_ERR: Optional[str] = None
try:  # pragma: no cover - 导入形态由部署布局决定
    import sys
    from pathlib import Path

    _GATE_DIR = Path(__file__).resolve().parents[1] / "gpu-broker"
    if str(_GATE_DIR) not in sys.path:
        sys.path.insert(0, str(_GATE_DIR))
    import vram_gate  # type: ignore
except Exception as _e:  # noqa: BLE001
    vram_gate = None  # type: ignore
    _VRAM_GATE_ERR = f"{type(_e).__name__}: {_e}"


SAMPLE_INTERVAL_S = 1.0

# 快照被认为"陈旧"的门槛。★ 不设这条,一个死掉的采样器会让快照永远看着是新的。
STALE_AFTER_S = 5.0


# ══════════════════════════════════════════════════════════════════════
#  P4-S4b · 租约账本
#
#  ★ kind 是【两根正交的轴】,不是一套枚举(2026-08-04 裁定)。
#    §8.1.7 的 WARM/MAINTENANCE 问的是「能不能被自动驱逐」(生命周期);
#    §8.1.6 的 blocking_set 问的是「有没有人在等」(阻塞性)。
#    两套并存于相邻两节且互不映射 —— 因为它们本来就问不同的问题。
#    合成一套必然丢信息,所以这里建两列。
#
#  ★★ 新增 kind 的默认必须是【不可驱逐】。理由与本项目其余 fail-closed 同源:
#    「加一个新值,默认落哪边」—— 默认可驱逐意味着新用途会被静默地抢掉资源。
#    不变式 I1(标注"一字不改")只允许**拒发新租约**、不允许撤销已发的;
#    可驱逐性是 §8.1.7 开的口子,必须逐 kind 显式声明,不能继承。
# ══════════════════════════════════════════════════════════════════════

BLOCKING_NONE = "NONE"                 # 没人等(后台/常驻)
BLOCKING_USER = "USER_BLOCKING"        # 用户正盯着等结果
BLOCKING_ASYNC = "USER_ASYNC"          # 用户发起但不盯着
BLOCKING_RESIDENT = "RESIDENT_TASKED"  # 常驻组件被派了活


@dataclass(frozen=True)
class LeaseKind:
    name: str
    evictable: bool          # 生命周期轴:能否被自动驱逐(§8.1.7)
    blocking: str            # 阻塞性轴(§8.1.6)
    exclusive: bool = False  # 独占:持有期间拒发一切新租约(B10⑥ 重标定用)
    note: str = ""


# ★ 闭集。未登记的 kind 一律拒绝 —— 这是准入白名单在租约上的同款手法:
#   集合成员判定,漏了会响亮报错,而"算术"能被凑过去。
LEASE_KINDS: Dict[str, LeaseKind] = {
    "client_session": LeaseKind(
        "client_session", evictable=False, blocking=BLOCKING_USER,
        note="客户端会话。人在用,驱逐它等于把人的界面打空"),
    "model_ref":      LeaseKind(
        "model_ref", evictable=True, blocking=BLOCKING_ASYNC,
        note="模型引用计数。§8.1.7 允许空闲时自动卸载 —— 这是显式开的口子"),
    "pet_presence":   LeaseKind(
        "pet_presence", evictable=False, blocking=BLOCKING_RESIDENT,
        note="桌宠在场。peak=0.0 但语义是独占的一份在场证明(D40 唯一实体)"),
    "agent_task":     LeaseKind(
        "agent_task", evictable=False, blocking=BLOCKING_ASYNC,
        note="D65 追加:一次 agent 任务 = N 次模型调用。规则是排队或降级,不抢占"),
    "recalibration":  LeaseKind(
        "recalibration", evictable=False, blocking=BLOCKING_USER, exclusive=True,
        note="B10⑥ 重标定。独占:期间拒发一切新租约,否则测出来的数不作数"),
}

DEFAULT_TTL_S = 60.0

# 续租/释放的结果 —— ★ 必须是**可区分**的返回值,不能都用 False。
#   「不是持有者」与「已过期」的处置完全不同:前者要自隐(你手上那份是旧的),
#   后者可以重新申请。混成一个 False,调用方只能猜,而猜错的方向是重试 = 双持有。
LEASE_OK = "OK"
LEASE_NOT_HOLDER = "NOT_HOLDER"   # lease_id/fence_token 对不上 —— 立刻自隐,别重试
LEASE_EXPIRED = "EXPIRED"         # 确实是你,但已过期 —— 可以重新申请
LEASE_UNKNOWN_KIND = "UNKNOWN_KIND"
LEASE_EXCLUSIVE_HELD = "EXCLUSIVE_HELD"


@dataclass
class Lease:
    lease_id: str
    fence_token: str
    kind: str
    holder: str
    components: List[str]
    granted_at: float
    expires_at: float

    def to_json(self) -> Dict:
        k = LEASE_KINDS[self.kind]
        return {
            "lease_id": self.lease_id,
            "kind": self.kind,
            "holder": self.holder,
            "components": list(self.components),
            "granted_at": self.granted_at,
            "expires_at": self.expires_at,
            "evictable": k.evictable,
            "blocking": k.blocking,
            "exclusive": k.exclusive,
        }


@dataclass(frozen=True)
class Snapshot:
    """给外面看的**副本**。frozen —— 拿到它的人改不了权威状态。"""
    generation: int
    committed: List[str]                  # 中枢认为"现在装着"的组件(S3 阶段恒为空:还没有装载端点)
    free_gib: Optional[float]             # NVML 实测可用;None = 这一次没读到
    total_gib: float
    vram_budget: float
    desktop_floor: float
    sampled_at: Optional[float]           # 单调时钟;None = 从未成功采样
    age_s: Optional[float]
    stale: bool
    sampler_error: Optional[str]
    # ★ 非 AI 占用只能**算术反推**,不能点名:本机实测 nvidia-smi --query-compute-apps
    #   对全部进程的 used_memory 都是 [N/A](WDDM 不暴露逐进程显存)。
    #   字段名带 _inferred 后缀,快照里也标 inferred=True —— 不让调用方把它当成实测。
    non_ai_used_gib_inferred: Optional[float]
    # P4-S4b:当前租约(拒绝信息要含【占用者】—— 谁持有、何时拿的、是否可驱逐)
    leases: Tuple[Dict, ...] = ()
    reserved: Tuple[str, ...] = ()
    inferred: bool = True

    def to_json(self) -> Dict:
        return {
            "generation": self.generation,
            "committed": list(self.committed),
            # ★ 已被租约占下但尚未装载的组件 —— 喂给闸的 reserved 集合(见 vram_gate 三集合)
            "reserved": list(self.reserved),
            "leases": [dict(l) for l in self.leases],
            "vram": {
                "free_gib": self.free_gib,
                "total_gib": self.total_gib,
                "vram_budget": self.vram_budget,
                "desktop_floor": self.desktop_floor,
                # ★ 明确标注这是推算值,不是实测。§8.1 界面诚实规则。
                "non_ai_used_gib_inferred": self.non_ai_used_gib_inferred,
                "non_ai_is_inferred": True,
                "non_ai_note": "桌面/游戏等非 AI 占用由 total - free - Σpeak(committed) 反推;"
                               "WDDM 不暴露逐进程显存,结构上说不出占用者的名字。",
            },
            "sampled_at": self.sampled_at,
            "age_s": self.age_s,
            "stale": self.stale,
            "sampler_error": self.sampler_error,
        }


class Broker:
    """单写者。**所有**状态变更都必须经过 `_lock`,且世代号在同一把锁下 +1。"""

    def __init__(self, cfg=None):
        self._lock = asyncio.Lock()
        self._generation = 0
        self._committed: List[str] = []
        self._free: Optional[float] = None
        self._sampled_at: Optional[float] = None
        self._sampler_error: Optional[str] = None
        self._task: Optional[asyncio.Task] = None
        self._cfg = cfg
        self._leases: Dict[str, Lease] = {}

    # ── 配置 ──────────────────────────────────────────────────────
    @property
    def cfg(self):
        if self._cfg is None:
            if vram_gate is None:
                raise RuntimeError(f"vram_gate 不可用:{_VRAM_GATE_ERR}")
            self._cfg = vram_gate.load_config()
        return self._cfg

    # ── 采样 ──────────────────────────────────────────────────────
    def _sample_once(self) -> None:
        """采一次 NVML。★ 同步函数 —— 调用方负责放到 executor,不在锁内跑。"""
        try:
            if vram_gate is None:
                raise RuntimeError(f"vram_gate 不可用:{_VRAM_GATE_ERR}")
            free = vram_gate.nvml_free_gib()
            if free is None:
                # ★ 读不到不是"保持上一次的值" —— 那会让快照静默变旧。
                #   free 置 None,并记下原因;stale 由 age 计算,两者互不掩盖。
                self._free = None
                self._sampler_error = "nvml_free_gib() 返回 None(nvidia-smi 失败)"
            else:
                self._free = free
                self._sampler_error = None
            self._sampled_at = time.monotonic()
        except Exception as e:  # noqa: BLE001
            # ★ 采样器崩了必须【看得见】:写进快照,不是记个日志了事。
            self._free = None
            self._sampler_error = f"{type(e).__name__}: {e}"
            self._sampled_at = time.monotonic()

    async def _sampler_loop(self) -> None:
        while True:
            try:
                # ★ 不在锁内做 I/O(见模块头硬约束)。nvml_free_gib 会起子进程,
                #   在锁里跑会把整个网关(含 300s 流式聊天)卡住。
                await asyncio.get_running_loop().run_in_executor(None, self._sample_once)
            except asyncio.CancelledError:
                raise
            except Exception as e:  # noqa: BLE001
                self._sampler_error = f"sampler_loop: {type(e).__name__}: {e}"
            await asyncio.sleep(SAMPLE_INTERVAL_S)

    def start(self) -> None:
        if self._task is None or self._task.done():
            self._task = asyncio.get_running_loop().create_task(self._sampler_loop())

    async def stop(self) -> None:
        if self._task is not None:
            self._task.cancel()
            try:
                await self._task
            except (asyncio.CancelledError, Exception):  # noqa: BLE001
                pass
            self._task = None

    # ── 只读快照 ───────────────────────────────────────────────────
    def snapshot(self) -> Snapshot:
        """★ 不取锁:所有字段都是原子读的标量/不可变列表副本,而取锁会让只读端点
        排在状态变更后面 —— 只读路径永远不该被写者阻塞。
        世代号与 committed 的一致性由**写侧**保证(两者在同一把锁下一起改)。"""
        cfg = self.cfg
        now = time.monotonic()
        age = None if self._sampled_at is None else round(now - self._sampled_at, 3)
        stale = (age is None) or (age > STALE_AFTER_S)
        committed = list(self._committed)
        free = self._free
        non_ai = None
        if free is not None:
            ai_now = sum(cfg.peak(c) for c in committed if c in cfg.components)
            non_ai = round(cfg.budget.total_vram - free - ai_now, 2)
        _now = time.monotonic()
        _live = [l for l in self._leases.values() if l.expires_at > _now]
        return Snapshot(
            leases=tuple(l.to_json() for l in _live),
            reserved=tuple(self.reserved_components()),
            generation=self._generation,
            committed=committed,
            free_gib=free,
            total_gib=cfg.budget.total_vram,
            vram_budget=cfg.budget.vram_budget,
            desktop_floor=cfg.budget.desktop_floor,
            sampled_at=self._sampled_at,
            age_s=age,
            stale=stale,
            sampler_error=self._sampler_error,
            non_ai_used_gib_inferred=non_ai,
        )

    # ── 写侧(S3 只留一个内部入口,供测试与将来的 S4 用)──────────────
    async def _set_committed(self, ids: List[str]) -> int:
        """唯一的状态变更入口。★ 世代号与状态在**同一把锁下**一起改 ——
        分开改会出现「世代号涨了但状态没改」或反之,而客户端正是靠世代号判断要不要重取。"""
        async with self._lock:
            self._committed = list(ids)
            self._generation += 1
            return self._generation

    # ══════════════════════════════════════════════════════════════
    #  P4-S4b · 租约
    #
    #  ★★ 过期是**惰性**的,在锁下求值,**不设收割线程** ——
    #     收割线程 = 第二个写者 = 双持有从侧门回来。这条是本节最重要的结构决定。
    #
    #  ★★ 时间纪律(D37 clock_timestamp 在进程内的等价物):
    #     任何时间值**不得在拿到锁之前捕获**,也**不得跨两条租约复用**。
    #     PG 里 now() 的危害是"整个事务共用一个时间戳";进程内的等价危害是
    #     "在 handler 顶部取一次 t 然后在循环里复用" —— 形状一模一样。
    #     故:每处需要时间的地方都在锁内现取 time.monotonic()。
    # ══════════════════════════════════════════════════════════════

    def _sweep_expired_locked(self) -> None:
        """★ 必须在锁内调用。惰性过期:每次进锁顺手扫一遍。"""
        now = time.monotonic()          # ★ 锁内现取,不复用外面的时间
        dead = [lid for lid, l in self._leases.items() if l.expires_at <= now]
        for lid in dead:
            del self._leases[lid]

    async def grant(self, kind: str, holder: str, components: List[str],
                    ttl_s: float = DEFAULT_TTL_S) -> Tuple[str, Optional[Lease]]:
        """发一份租约。返回 (状态, 租约)。"""
        async with self._lock:
            if kind not in LEASE_KINDS:
                # ★ 未登记 kind fail-closed —— 与准入白名单同款:集合成员判定,漏了响亮报错。
                return LEASE_UNKNOWN_KIND, None
            self._sweep_expired_locked()
            # 独占租约在场时,拒发一切新租约(B10⑥:重标定期间测出来的数才作数)
            for l in self._leases.values():
                if LEASE_KINDS[l.kind].exclusive:
                    return LEASE_EXCLUSIVE_HELD, None
            if LEASE_KINDS[kind].exclusive and self._leases:
                # 反向:要发独占租约,但已有别的租约在场
                return LEASE_EXCLUSIVE_HELD, None
            now = time.monotonic()      # ★ 锁内现取,且这一条租约专用
            lease = Lease(
                # ★ fence_token 每次全新、不复用、**不由 holder 推导** ——
                #   能从 holder 推出来的 token 等于没有 token。
                lease_id=secrets.token_hex(8),
                fence_token=secrets.token_hex(16),
                kind=kind, holder=holder, components=list(components),
                granted_at=now, expires_at=now + ttl_s,
            )
            self._leases[lease.lease_id] = lease
            self._generation += 1       # ★ 租约变化也是状态变化,同一把锁下涨号
            return LEASE_OK, lease

    async def renew(self, lease_id: str, fence_token: str,
                    ttl_s: float = DEFAULT_TTL_S) -> str:
        """续租 = **条件写**。★ 对不上返回 NOT_HOLDER,绝不当成"重新发放"。"""
        async with self._lock:
            l = self._leases.get(lease_id)
            if l is None:
                # 可能已过期被扫掉,也可能从来不存在 —— 对调用方是同一件事:你手上那份不作数了。
                return LEASE_EXPIRED
            if not secrets.compare_digest(l.fence_token, fence_token):
                # ★ 这是条件写不匹配。调用方必须**立刻自隐**,不得重试 —— 重试就是双持有。
                return LEASE_NOT_HOLDER
            now = time.monotonic()      # ★ 锁内现取
            if l.expires_at <= now:
                del self._leases[lease_id]
                return LEASE_EXPIRED
            l.expires_at = now + ttl_s
            return LEASE_OK

    async def release(self, lease_id: str, fence_token: str) -> str:
        async with self._lock:
            l = self._leases.get(lease_id)
            if l is None:
                return LEASE_EXPIRED
            if not secrets.compare_digest(l.fence_token, fence_token):
                return LEASE_NOT_HOLDER
            del self._leases[lease_id]
            self._generation += 1
            return LEASE_OK

    async def active_leases(self) -> List[Lease]:
        async with self._lock:
            self._sweep_expired_locked()
            return list(self._leases.values())

    def reserved_components(self) -> List[str]:
        """当前被租约占下、但**尚未装载**的组件 —— 喂给闸的 `reserved` 那一集合。

        ★ 只读路径不取锁(与 snapshot 同理)。这里做的是惰性判断而非清理:
          过期条目在下一次进锁时才被真正删掉,而它们在这里已经不算数。
        """
        now = time.monotonic()
        out = []
        for l in self._leases.values():
            if l.expires_at <= now:
                continue
            for c in l.components:
                if c not in self._committed and c not in out:
                    out.append(c)
        return out


# 进程内单例 —— 「单一权威」的字面落点。
BROKER = Broker()
