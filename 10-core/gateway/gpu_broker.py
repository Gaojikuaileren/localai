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


# ══════════════════════════════════════════════════════════════════════
#  P4-S7 · 四个集合 · 状态机 · 不变式 I2/I3/I4 · RECONCILE_WATCH
#
#  方案书 §8.1(行 1546-1604)原文。四个集合是**不变式的锚点**:
#    intended_resident_set    你在主机面板勾选并点了确定的   —— 只有主机变更面能写
#    committed_resident_set   Broker 事务成功后写入的实际目标 —— committed := intended
#    actual_resident_set      租约账本 + NVML 反映的**真实**驻留
#    permitted_on_demand_set  授权「允许在需要时申请」的按需槽 —— 只有主机变更面能写
#
#  ★ permitted_on_demand 与 intended 是**两个字段,不合并** ——
#    合并会让 intended 里出现永远不参与 I2 判定的成员,三元组语义就脏了。
# ══════════════════════════════════════════════════════════════════════

STATE_STARTING      = "STARTING"
STATE_READY         = "READY"
STATE_STAGING       = "STAGING"
STATE_PRECHECK      = "PRECHECK"
STATE_APPLYING      = "APPLYING"
STATE_RECONCILING   = "RECONCILING"
STATE_DEGRADED_SAFE = "DEGRADED_SAFE"

ALL_STATES = (STATE_STARTING, STATE_READY, STATE_STAGING, STATE_PRECHECK,
              STATE_APPLYING, STATE_RECONCILING, STATE_DEGRADED_SAFE)

# ── P4-S8:合法转换【白名单】────────────────────────────────────────
# ★★ 这是白名单不是黑名单 —— 没列进来的转换一律拒绝(失败关闭)。
#    加一个新状态,默认落在"谁也到不了它、它哪儿也去不了",而不是"畅通无阻"。
#    反向全表断言盯着这张表:ALL_STATES 里每一个都必须在这里登记。
ALLOWED_TRANSITIONS: Dict[str, frozenset] = {
    STATE_STARTING:      frozenset({STATE_READY, STATE_RECONCILING}),
    STATE_READY:         frozenset({STATE_STAGING, STATE_RECONCILING}),
    STATE_STAGING:       frozenset({STATE_PRECHECK, STATE_READY}),     # 取消 → 回 READY
    STATE_PRECHECK:      frozenset({STATE_APPLYING, STATE_STAGING}),   # 不过 → 回编辑态
    STATE_APPLYING:      frozenset({STATE_READY, STATE_RECONCILING}),
    STATE_RECONCILING:   frozenset({STATE_READY, STATE_DEGRADED_SAFE}),
    # ★ DEGRADED_SAFE 是**终态**。唯一出口是 set_power 的显式电源循环(人的动作)——
    #   自动恢复就是"自动触发",D10 存活下来的那一句明令禁止。
    STATE_DEGRADED_SAFE: frozenset({STATE_STARTING}),
}

# 仍然对外提供服务的状态。★ RECONCILING 必须在内 —— 一个 worker 掉线不该把还活着的也判死。
SERVING_STATES = frozenset({STATE_READY, STATE_STAGING, STATE_PRECHECK,
                            STATE_APPLYING, STATE_RECONCILING})

# ★★ 能【发起】一次事务的状态。必须含 STAGING —— 预检不过的落点就是 STAGING,
#   只收 READY 会让"改完再点一次确定"永远得到 busy(2026-08-04 S9 实测撞出)。
#   反过来也别放宽:PRECHECK/APPLYING 表示已有一笔在跑,RECONCILING/DEGRADED_SAFE/STARTING
#   表示还没站稳 —— 那几个进来就是双写者。
ACCEPTS_TRANSACTION = frozenset({STATE_READY, STATE_STAGING})

BLOCKING_SET = frozenset({BLOCKING_USER, BLOCKING_ASYNC, BLOCKING_RESIDENT})

DRAIN_WINDOW_S          = 5.0    # 方案书 §8.1.6:先给 5 秒排空窗口
RECLAIM_TIMEOUT_S       = 10.0   # 行 1507:超时 10 s 报 vram_not_reclaimed
RECLAIM_TOLERANCE_GIB   = 0.2    # 行 1507:free 回升到预期 ±0.2 GiB
ADMISSION_GUARD_WINDOW_S = 5.0   # 行 1623:5 s 内
ADMISSION_GUARD_DROP_GIB = 1.0   # 行 1623:降幅 > 1.0 GiB
FREE_HISTORY_MAX        = 32     # 1 Hz 采样,够覆盖 5 s 窗口且不无界增长


class IllegalTransition(RuntimeError):
    """非法状态转换。★ 抛异常而不是静默忽略 —— 忽略会让状态机看着有约束、实际没有。"""


@dataclass
class ApplyResult:
    ok: bool
    code: str            # "" | busy | needs_user_choice | gate_* | loader_absent |
                         # vram_not_reclaimed | load_failed_rolled_back | rollback_failed
    state: str
    message: str = ""
    blocking: List[str] = field(default_factory=list)

    def to_json(self) -> Dict:
        return {"ok": self.ok, "code": self.code, "state": self.state,
                "message": self.message, "blocking": list(self.blocking)}

ALL_STATES = (STATE_STARTING, STATE_READY, STATE_STAGING, STATE_PRECHECK,
              STATE_APPLYING, STATE_RECONCILING, STATE_DEGRADED_SAFE)

# I2 不等时,state 必须是这三个之一(方案书行 1566)
I2_TOLERATED_STATES = (STATE_STARTING, STATE_RECONCILING, STATE_DEGRADED_SAFE)


@dataclass(frozen=True)
class InvariantReport:
    invariant: str          # I2 / I3 / I4
    holds: bool
    detail: str
    # ★★ 置信度必须如实标注,否则这就是个假检测器。
    #   "structural"    —— 判据的输入是结构性可观测的
    #   "self_reported" —— 输入来自 Broker 自己的账本,**不是独立观测**
    confidence: str = "structural"

    def to_json(self) -> Dict:
        return {"invariant": self.invariant, "holds": self.holds,
                "detail": self.detail, "confidence": self.confidence}


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
    # ★★★ 2026-08-05 审计:装载器**接不上**的原因原来一个字都没留下。
    #   网关启动时 `attach_loader(ModelLoader())` 外面包着 `except: pass`,
    #   于是构造失败(比如配置里少了 model_rel)之后:网关照常起、每次事务都退
    #   `loader_absent`、而那条消息当时还写着「装载器尚未实现」——
    #   运维读到的是"意料之中",真相却是"接线断了,去修"。
    #   ⇒ 与 sampler_error 同款:失败的**原因**必须能被看见。
    #     loader_error=None 有两种含义,靠 loader_present 区分:接上了 / 压根没试过。
    # ★ 这两个字段带默认值,所以必须排在**所有无默认值字段之后** ——
    #   第一版塞在 non_ai_used_gib_inferred 前面,整个 gpu_broker 模块当场 import 不了
    #   (TypeError: non-default argument follows default argument),由门禁抓出。
    loader_error: Optional[str] = None
    loader_present: bool = False
    # P4-S4b:当前租约(拒绝信息要含【占用者】—— 谁持有、何时拿的、是否可驱逐)
    leases: Tuple[Dict, ...] = ()
    reserved: Tuple[str, ...] = ()
    # P4-S7:四个集合 + 状态 + 不变式检测结果
    intended: Tuple[str, ...] = ()
    permitted_on_demand: Tuple[str, ...] = ()
    actual: Tuple[str, ...] = ()
    state: str = STATE_STARTING
    power_on: bool = True
    invariants: Tuple[Dict, ...] = ()
    inferred: bool = True

    def to_json(self) -> Dict:
        return {
            "generation": self.generation,
            "committed": list(self.committed),
            # ★ 已被租约占下但尚未装载的组件 —— 喂给闸的 reserved 集合(见 vram_gate 三集合)
            "reserved": list(self.reserved),
            "leases": [dict(l) for l in self.leases],
            # ★ 四个集合是不变式的锚点(§8.1 行 1546)。permitted_on_demand 与 intended
            #   是**两个字段不合并** —— 合并会让 intended 里出现永远不参与 I2 判定的成员。
            "sets": {
                "intended_resident": list(self.intended),
                "committed_resident": list(self.committed),
                "actual_resident": list(self.actual),
                "permitted_on_demand": list(self.permitted_on_demand),
            },
            "state": self.state,
            "power_on": self.power_on,
            # ★ RECONCILE_WATCH 的结果:**只报告不修复**。每条自带 confidence ——
            #   actual 今天不是独立观测(无装载器 + WDDM 不暴露逐进程显存),
            #   所以 I2/I3 标 self_reported。不标就是个假检测器。
            "invariants": [dict(i) for i in self.invariants],
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
            # ★ 装载器在不在、接不上的话为什么 —— 见 loader_error 字段处的说明
            "loader_present": self.loader_present,
            "loader_error": self.loader_error,
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
        # P4-S5:变更通知。★ 订阅者【等事件】,不是轮询 —— D37 ② 明写「推送非轮询」。
        #   每个订阅者持一个自己的 Event;状态变更时在锁内一次性全部 set。
        #   set() 是非阻塞的,放在锁里安全(不违反"锁内不得跨 await I/O")。
        self._waiters: List[asyncio.Event] = []

        # ── P4-S7:四个集合 + 状态 + 电源轴 ──────────────────────
        self._intended: List[str] = []            # 只有主机变更面能写
        self._permitted_on_demand: List[str] = []  # 只有主机变更面能写(与 intended 分开)
        self._state: str = STATE_STARTING
        self._power_on: bool = True                # I4 的电源轴,与意图轴分离
        self._last_watch: Tuple[InvariantReport, ...] = ()

        # ── P4-S8:事务 ──────────────────────────────────────────
        # ★★★ 装载器【默认缺席】。这不是"待填的 TODO",是**判据本身**:
        #     None ⇒ apply_intended 在预检阶段失败关闭,绝不到达 READY。
        #     若给它一个空实现,每次事务都会"成功"而显存里什么都没有 ——
        #     那是比"3 个装了 2 个仍报 READY"更坏的版本。
        self._loader = None
        # ★ 接装载器时**失败的原因**。None 不等于"没问题" —— 它只是"没人试过或试成了",
        #   要连着 `self._loader is not None` 一起看。见 Snapshot.loader_error。
        self._loader_error: Optional[str] = None
        # ★ 装载器的观测结果缓存。actual_resident 是**同步**属性(快照、不变式都在读它),
        #   而探活是 async I/O —— 不能在同步路径里跑。
        #   ⇒ 由采样循环刷新;None = 还没探过,此时退回账本并保持 self_reported。
        self._actual_cache: Optional[List[str]] = None
        self._free_history: List[Tuple[float, Optional[float]]] = []

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
        finally:
            # P4-S8:admission_guard 要的降幅历史。★ 读失败也要入历史(记 None),
            #   否则"采不到"会被降幅规则当成"没变化",故障反而更安静。
            self._free_history.append((self._sampled_at, self._free))
            if len(self._free_history) > FREE_HISTORY_MAX:
                del self._free_history[:-FREE_HISTORY_MAX]

    async def _sampler_loop(self) -> None:
        while True:
            try:
                # ★ 不在锁内做 I/O(见模块头硬约束)。nvml_free_gib 会起子进程,
                #   在锁里跑会把整个网关(含 300s 流式聊天)卡住。
                await asyncio.get_running_loop().run_in_executor(None, self._sample_once)
                # ── P4-S14:刷新【独立观测】—— actual_resident 的事实源 ──
                #   ★ 探活是 async I/O,而 actual_resident 是同步属性(快照与不变式都在读它),
                #     所以在这里刷缓存。探活失败**不当成"什么都没装"** ——
                #     那会让 I2 立刻判违反,而真相只是"这一轮没探到"。保留上一次的观测。
                if self._loader is not None:
                    try:
                        self._actual_cache = await self._loader.running()
                    except Exception as e:                   # noqa: BLE001
                        self._sampler_error = f"loader_probe: {type(e).__name__}: {e}"
                # ── RECONCILE_WATCH(P4-S7)★★ 只报告,不修复 ──
                #   修复即"自动触发",而那正是 D10 存活下来的那半条明令禁止的。
                #   这里只把结果记下来放进快照;要不要动手,是人的决定。
                self._last_watch = self.check_invariants()
            except asyncio.CancelledError:
                raise
            except Exception as e:  # noqa: BLE001
                self._sampler_error = f"sampler_loop: {type(e).__name__}: {e}"
            await asyncio.sleep(SAMPLE_INTERVAL_S)

    def start(self) -> None:
        if self._task is None or self._task.done():
            self._task = asyncio.get_running_loop().create_task(self._sampler_loop())

    def attach_loader(self, loader) -> None:
        """接上装载器(P4-S14)。★ 只此一处 —— 装载器是唯一能动系统进程的东西。"""
        self._loader = loader
        self._loader_error = None

    def note_loader_unavailable(self, reason: str) -> None:
        """记下**接不上的原因**。★ 这不是日志,是快照里的一个字段。

        ★★ 为什么非要有这个方法:接不上时事务照样失败关闭(那部分本来就对),
          但失败关闭**不解释原因**的话,`loader_absent` 这个码就退化成了
          「反正装不了」—— 而它至少有两种成因,下一步动作完全相反:
            · 这个 Broker 实例有意没接装载器(测试里)   ⇒ 什么都不用做
            · 生产启动时 ModelLoader() 构造抛了        ⇒ 去修配置/装载器
          分不清这两种,就等于把一个真缺陷伪装成了预期行为。
        """
        self._loader = None
        self._loader_error = reason

    async def adopt_running(self) -> List[str]:
        """认领已经在跑的后端(中枢重启后的孤儿)。★ 以**现实**为准,不以账本为准。

        ★★ 不认领的后果二选一,都很坏:以为没装 → 再起一个 → 端口冲突;
          以为装了 → 账本与现实分家。
        ★ 认领之后 committed 直接对齐现实 —— 这不是"信任账本",恰恰相反:
          是**让账本去追现实**。
        """
        if self._loader is None:
            return []
        found = await self._loader.adopt()
        if found:
            async with self._lock:
                for cid in found:
                    if cid not in self._committed:
                        self._committed.append(cid)
                self._generation += 1
                self._notify_locked()
            self._actual_cache = await self._loader.running()
        return found

    async def finish_startup(self) -> str:
        """P4-S9:`STARTING` 的出口。★ 此前**没有任何代码**能离开 STARTING ——
        于是 `apply_intended` 永远返回 `busy`,整条事务路径从来走不到。

        ★★ 放行条件**就是 I2 的后件**:`actual == committed`。
          这不是巧合而是设计 —— `READY` 的全部含义就是"账面上装着的,真的装着了"。
          若开机装载没完成就宣布 READY,I2 会立刻被违反,
          而那正是方案书行 1594 点名要禁的「3 个装了 2 个却仍报 READY」。
          ⇒ **不给"启动阶段先放过"这条例外**:例外一开,I2 在最需要它的那一刻恰好不管用。

        ★★★ 诚实边界,必须写在这里而不是藏进注释:
          **今天这个判据是恒真式。** `actual_resident` 就是 `_committed` 本身
          (见该 property 的说明:没有装载器 + WDDM 不暴露逐进程显存),
          于是 `actual == committed` 等价于 `committed == committed`。
          ⇒ 它**今天不是一个检测器**,是一个**为 P5 预留的形状**。
          这样写的价值只有一个:P5 接上装载器、`actual` 变成独立观测的那一刻,
          它**立刻**就是承重的,不需要有人记得回来补。
          测试里钉住了这条恒真性 —— 等它不再恒真时那条断言会红,提醒把它当真检测器复核一遍。
          走 RECONCILING 那条分支的用例用**注入独立 actual** 的方式真跑过,不留"从没执行过的分支"。
        """
        async with self._lock:
            if self._state != STATE_STARTING:
                return self._state
            actual, committed = set(self.actual_resident), set(self._committed)
            if actual == committed:
                await self._transition(STATE_READY, "开机装载完成(actual == committed)")
            else:
                # 装载没齐 —— 不宣布 READY。按 §8.1 行 1606:RECONCILING **仍然提供服务**,
                # 按 actual 那一份对外说话,而不是把还活着的一并判死。
                await self._transition(STATE_RECONCILING,
                                       f"开机装载未齐:actual={sorted(actual)} ⊊ committed={sorted(committed)}")
            return self._state

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
            intended=tuple(self._intended),
            permitted_on_demand=tuple(self._permitted_on_demand),
            actual=tuple(self.actual_resident),
            state=self._state,
            power_on=self._power_on,
            invariants=tuple(r.to_json() for r in (self._last_watch or self.check_invariants())),
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
            loader_error=self._loader_error,
            loader_present=self._loader is not None,
            non_ai_used_gib_inferred=non_ai,
        )

    # ── 写侧(S3 只留一个内部入口,供测试与将来的 S4 用)──────────────
    async def _set_committed(self, ids: List[str]) -> int:
        """唯一的状态变更入口。★ 世代号与状态在**同一把锁下**一起改 ——
        分开改会出现「世代号涨了但状态没改」或反之,而客户端正是靠世代号判断要不要重取。"""
        async with self._lock:
            self._committed = list(ids)
            self._generation += 1
            self._notify_locked()          # ★ 与 +1 在同一把锁里 —— 见 _notify_locked 的说明
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

    # ── P4-S5 · 变更通知(推送,不是轮询)──────────────────────────
    def _notify_locked(self) -> None:
        """★ 必须在锁内调用,且必须与世代号 +1 在**同一把锁**里 ——
        否则会出现「通知发出去了但世代号还没涨」,订阅者取到的快照与它以为的不符。
        `Event.set()` 非阻塞,放锁内不违反「锁内不得跨 await I/O」。"""
        for w in self._waiters:
            w.set()
        self._waiters = []

    async def wait_for_change(self, since_generation: int, timeout: float = 15.0) -> bool:
        """等到世代号离开 `since_generation`。返回 True=有变更,False=超时(该发心跳了)。

        ★ 先在锁内比一次:变更可能在调用方读快照与开始等待之间就发生了 ——
          不比这一次就会漏掉一整个世代(经典的丢失唤醒)。
        """
        ev = asyncio.Event()
        async with self._lock:
            if self._generation != since_generation:
                return True                      # 已经变了,不必等
            self._waiters.append(ev)
        try:
            await asyncio.wait_for(ev.wait(), timeout=timeout)
            return True
        except asyncio.TimeoutError:
            async with self._lock:
                if ev in self._waiters:
                    self._waiters.remove(ev)     # ★ 超时要摘掉自己,否则 _waiters 无界增长
            return False

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
            self._notify_locked()
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
            self._notify_locked()
            return LEASE_OK

    async def active_leases(self) -> List[Lease]:
        async with self._lock:
            self._sweep_expired_locked()
            return list(self._leases.values())

    # ══════════════════════════════════════════════════════════════
    #  P4-S7 · 不变式检测器
    #
    #  ★★ 「没有检测器的不变式等于没有」(方案书行 1588)。
    #     而方案书行 1593-1594 说得更具体:全文今天**没有任何机制能发现
    #     「3 个装了 2 个却仍报 READY」** —— 那正是 I2 本来要禁止的事。
    #
    #  ★★ RECONCILE_WATCH **只报告,不修复** —— 修复即"自动触发",
    #     而那正是 D10 存活下来的那半条明令禁止的。
    # ══════════════════════════════════════════════════════════════

    @property
    def actual_resident(self) -> List[str]:
        """真实驻留集合。

        ★★★ 2026-08-05(P4-S14):**这里终于变成独立观测了。**

        S7 当时如实写着(原文保留,因为它记录了为什么曾经不能):
          「今天它不是独立观测,而是 Broker 自己的账本(committed)。原因有二,
            都是结构性的:① 装载器还不存在,没有"谁真的把模型装进去了"这个事实源;
            ② WDDM 不暴露逐进程显存(本机实测 --query-compute-apps 全部是 [N/A])。
            ⇒ I2 的置信度只能标 self_reported,**不标就是个假检测器** ——
              用自己的账本跟自己的账本比,永远相等。」

        原因①**已经消失**:装载器(model_loader.py)会起停真的后端进程,
        「哪些组件真的装着」= **哪些后端进程活着且 /health 回 2xx** ——
        那是一个与账本**完全无关**的事实源。
        ⇒ I2/I3 的置信度随之从 self_reported 升为 observed;
          S7 里那条**钉住恒真性**的断言会红,而那正是它被写下来的目的。

        ★ 原因②仍然成立:我们知道「哪几个进程活着」,但**仍然不知道每个占了多少显存**。
          所以非 AI 占用依旧只能算术反推、依旧标 inferred。**这一半没有解决,不得声称解决。**

        ★ 装载器缺席时**退回账本**并保持 self_reported —— 不是"假装还是观测的"。
        """
        if self._loader is None:
            return list(self._committed)
        cached = self._actual_cache
        return list(cached) if cached is not None else list(self._committed)

    def check_invariants(self) -> Tuple[InvariantReport, ...]:
        """求值 I2 / I3 / I4。★ 纯读,不改任何状态 —— 检测器不许有副作用。"""
        actual = set(self.actual_resident)
        committed = set(self._committed)
        intended = set(self._intended)
        permitted = set(self._permitted_on_demand)
        out: List[InvariantReport] = []

        # ── I2 · 该在的都在 ──────────────────────────────────
        #  ★★ 必须保留【蕴含】形态:state == READY ⟹ actual == committed。
        #     DEGRADED_SAFE 不是 READY ⇒ 前件为假 ⇒ I2 自动成立。
        #     写成双条件会造出「永久违反不变式、告警无法消解」的状态,
        #     进而逼着系统去自动改写锚点 —— 而那恰好违反「不做自动触发」。
        if self._state == STATE_READY:
            ok = (actual == committed)
            detail = ("READY 且 actual == committed" if ok else
                      f"READY 却不等:缺 {sorted(committed - actual)} 多 {sorted(actual - committed)}"
                      f" ⇒ state 必须是 {I2_TOLERATED_STATES} 之一,且 UI 须常驻显示差异")
        else:
            ok = True
            detail = f"state={self._state} 不是 READY ⇒ 前件为假,I2 自动成立(这是设计,不是放水)"
        # ★ 置信度跟着事实源走:有装载器 ⇒ actual 是**独立观测**(observed);
        #   没有 ⇒ 退回账本,仍然是 self_reported。**不许固定写死其中一个。**
        _conf = "observed" if self._loader is not None else "self_reported"
        out.append(InvariantReport("I2", ok, detail, confidence=_conf))

        # ── I3 · 在的都该在(★ 无状态前件,任何状态下恒成立)──
        #  这条才是接住「某个 bug 装了不该装的」的那一条。
        #  它是准入白名单的**运行期**版本:白名单只在申请那一刻把关。
        stray = sorted(actual - (committed | permitted))
        out.append(InvariantReport(
            "I3", not stray,
            ("没有不该在的组件" if not stray else
             f"★ 出现了既不在 committed 也不在 permitted_on_demand 的驻留:{stray} —— §9.3 告警"),
            # ★★ 与 I2 同一个 _conf:它们**读同一个 actual**,同一个事实源。
            #   一个标 observed 一个标 self_reported 会让人以为它们的可信度不同 ——
            #   2026-08-05 我改 I2 时漏了这一行,被新写的一条断言当场抓到。
            confidence=_conf))

        # ── I4 · 电源轴与意图轴分离 ──────────────────────────
        #  power == off ⟹ actual == ∅ ∧ intended 不变。
        #  ★ ON/OFF 总开关**不写 intended** —— 否则一次 OFF 就吞掉用户的勾选,
        #    等价于系统改写了用户配置;ON 回来时按 intended 重新装载,不需要重勾。
        if not self._power_on:
            ok4 = (len(actual) == 0)
            d4 = ("power=off 且 actual 为空" if ok4 else
                  f"★ power=off 却仍有驻留:{sorted(actual)}")
        else:
            ok4 = True
            d4 = f"power=on ⇒ 前件为假,I4 自动成立(intended 现有 {len(intended)} 项,未被电源轴改写)"
        out.append(InvariantReport("I4", ok4, d4, confidence="structural"))

        return tuple(out)

    async def set_power(self, on: bool) -> int:
        """电源轴。★ I4:**绝不触碰 intended** —— 关机不该吞掉用户的勾选。"""
        async with self._lock:
            self._power_on = on
            if not on:
                self._committed = []          # 关电 ⇒ 实际驻留清空
                # ★ self._intended 一个字都不动 —— 这就是 I4 的全部要点
            else:
                # ★ P4-S8:DEGRADED_SAFE 的**唯一**出口 —— 人手动重开电源轴。
                #   放在 set_power 里而不是任何采样/巡检里,是为了钉死"没有自动恢复"。
                if self._state == STATE_DEGRADED_SAFE:
                    self._state = STATE_STARTING
            self._generation += 1
            self._notify_locked()
            return self._generation

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

    # ══════════════════════════════════════════════════════════════
    #  P4-S8 · 「确定 = 一次事务」(方案书 §8.1「确定 = 一次事务」+ §8.1.6)
    #
    #  ★★★ 本节写于 S8,当时最大的诚实边界是:**装载器不存在**。
    #      (原文说"这是 P5 的活" —— 那句话有两处错,留着是因为它记录了当时的判断:
    #       ① 装载器 S14 就落地了,见 model_loader.py;② P5 是语音 v1,不是装载器。
    #       ★ 给一件事起个方案书里没有的名字,等于把它从任何清单里摘出去 —— 已记入
    #         00-docs/ASSERTION-PITFALLS.md。)
    #      下面这条**规定本身没有过期**:装载器缺席时事务必须失败关闭。
    #      于是 S8 有一个极容易掉进去的坑:把 `load()` 写成空实现,
    #      于是每次事务都"成功",状态机一路走到 READY,四个集合全部相等,
    #      I2 永远绿 —— 而显存里【一个字节都没有】。
    #      那正是方案书行 1594 点名的「3 个装了 2 个却仍报 READY」的更坏版本。
    #  ⇒ 规定:`self._loader is None` 时,事务**在预检阶段就失败关闭**,
    #      落 `loader_absent`,**绝不允许进入 APPLYING,更不允许到达 READY**。
    #      失败必须长得和成功不一样 —— 这是本项目的第一戒律。
    # ══════════════════════════════════════════════════════════════

    async def _transition(self, to: str, why: str) -> None:
        """★ 必须在锁内调用。所有状态改写的**唯一**入口。

        转换合法性查 `ALLOWED_TRANSITIONS` 白名单 —— **没列进来的一律拒绝**。
        加一个新状态,默认落在"谁也到不了它、它哪儿也去不了",而不是"畅通无阻"。
        """
        frm = self._state
        if frm == to:
            return
        allowed = ALLOWED_TRANSITIONS.get(frm)
        if allowed is None:
            raise IllegalTransition(f"源状态 {frm} 未登记在 ALLOWED_TRANSITIONS —— 拒绝(失败关闭)")
        if to not in allowed:
            raise IllegalTransition(f"{frm} ─X→ {to} 不是合法转换({why})")
        self._state = to
        self._generation += 1
        self._notify_locked()

    def blocking_leases(self) -> List[Lease]:
        """`blocking_set = {USER_BLOCKING, USER_ASYNC, RESIDENT_TASKED}`(方案书 §8.1.6)。

        ★ 主语已由「降档」改为「变更驻留集合」(D25)—— 档位取消后原文这条会**字面失效**,
          而它保护的正是"一键套用推荐组合"这条最容易静默杀掉运行中任务的路径。
        """
        now = time.monotonic()
        return [l for l in self._leases.values()
                if l.expires_at > now and LEASE_KINDS[l.kind].blocking in BLOCKING_SET]

    def failure_landing(self) -> Dict[str, object]:
        """失败落点(方案书行 1615-1621 · D24 排查带出,原文完全没定义过)。

        规定:驱逐可驱逐租约(原文的 `WARM` / `MAINTENANCE` 类)后仍不足时,
        **失败落在 AI 侧**(拒新申请 / DEGRADED_SAFE),**不得由桌面承担分配失败**。

        ★★ 诚实边界:这是**策略**,不是保证。WDDM **不按优先级驱逐** ——
           已经发生的驱逐落在谁头上,本项目控制不了。
           本函数**永远不会**返回 "desktop",但那只说明我们没有主动把失败推给桌面,
           **不等于**桌面不会被挤。任何 UI 文案不得据此声称"保证桌面不被挤"。
        """
        now = time.monotonic()
        evictable, pinned = [], []
        for l in self._leases.values():
            if l.expires_at <= now:
                continue
            (evictable if LEASE_KINDS[l.kind].evictable else pinned).append(l.lease_id)
        return {
            "evict_first": sorted(evictable),   # 先驱逐这些(WARM / MAINTENANCE 类)
            "then_lands_on": "ai",              # ★ 恒为 ai —— 见上面的诚实边界
            "never": "desktop",
            "ai_actions": ["refuse_new_admission", "degraded_safe"],
            "pinned_not_evicted": sorted(pinned),
            "guarantee": False,                 # ★ 策略非保证,WDDM 不按优先级驱逐
        }

    def admission_guard(self) -> Optional[Dict[str, object]]:
        """通用降幅规则(方案书行 1623):`free_vram` 1 Hz 采样,**5 s 内降幅 > 1.0 GiB** → 触发。

        ★★ 全程**不看进程名**。原文的「检测独占全屏游戏」特例已删除 ——
           这台机器上比游戏更常见的显存大户是 UE5 与多标签 Chrome。
           进程名**仅用于生成告知文案,不产生策略动作** ⇒ 本函数不接收也不读取任何进程名。
        """
        hist = [(t, f) for (t, f) in self._free_history if f is not None]
        if len(hist) < 2:
            return None
        now_t, now_f = hist[-1]
        window = [(t, f) for (t, f) in hist if now_t - t <= ADMISSION_GUARD_WINDOW_S]
        if len(window) < 2:
            return None
        peak = max(f for _, f in window)
        drop = round(peak - now_f, 4)
        if drop <= ADMISSION_GUARD_DROP_GIB:
            return None
        return {"drop_gib": drop, "window_s": ADMISSION_GUARD_WINDOW_S,
                "from_gib": peak, "to_gib": now_f,
                "action": "refuse_new_admission"}

    async def _await_reclaim(self, expect_free: float, *, timeout: Optional[float] = None,
                             poll: float = 1.0) -> Optional[str]:
        """卸载后轮询 NVML 直到 free 回升到预期 ±0.2 GiB(方案书行 1507)。

        返回 None = 已回收;返回字符串 = 错误码 `vram_not_reclaimed`。
        ★ 超时**不是**"大概回收了就算了" —— 显存没吐出来还硬装,撞的是物理墙。
        ★ timeout 走 None 而不是默认参数直接引用常量:默认参数在 def 那一刻就绑死了,
          测试改不动模块常量,断言就只能抄一份数字 —— 那份抄件跟真值分家的那天就是假断言。
        """
        timeout = RECLAIM_TIMEOUT_S if timeout is None else timeout
        deadline = time.monotonic() + timeout
        while True:
            await asyncio.get_running_loop().run_in_executor(None, self._sample_once)
            if self._free is not None and self._free >= expect_free - RECLAIM_TOLERANCE_GIB:
                return None
            if time.monotonic() >= deadline:
                return "vram_not_reclaimed"
            await asyncio.sleep(poll)

    async def apply_intended(self, requested: List[str], *,
                             permitted: Optional[List[str]] = None,
                             interrupt_running: bool = False) -> "ApplyResult":
        """「点确定」= 一次事务。READY → STAGING → PRECHECK → APPLYING → READY。

        方案书「确定 = 一次事务」四条,逐条落在下面并各有断言:
          1. **点确定时重新求值,不用预览时的快照** —— 挑组件要几十秒,期间桌面会变,
             「预览过、确定时不过」是**必然**会发生的。故这里现采 NVML、现跑三道闸。
          2. 预检不过 → 回编辑态(STAGING),**不卸载任何东西**。
          3. 装载中途失败 → 回滚上一个成功集合;回滚也失败 → DEGRADED_SAFE。
          4. 有任务在跑 → 先给 5 秒排空窗口,再让调用方选"优雅中断"或"等它跑完"。
        """
        # ── ① 进 STAGING。可从 READY 或 STAGING 进;别的状态说明有另一笔事务在跑 ──
        #   ★★ 2026-08-04 S9 实测补的一条:原来这里只收 READY,而**预检不过的落点是 STAGING**
        #     ⇒ 用户改完选择再点一次确定,得到的是 `busy`,**重试路径整条是断的**。
        #     S8 的测试每个 broker 只跑一次事务,恰好把它盖住了 —— 典型的"测试形状盲区":
        #     不是断言写错,是**根本没构造第二次**。现在补了连续两次的用例。
        #   ★ STAGING **不是锁**:它同时接受新事务、且在 SERVING_STATES 里照常提供服务。
        #     所以"面板开着没提交"不会卡住任何人,不需要给它加超时或取消端点 ——
        #     加了反而会在用户正在挑选时把他的选择清掉。
        async with self._lock:
            if self._state not in ACCEPTS_TRANSACTION:
                return ApplyResult(False, "busy", self._state,
                                   f"当前状态 {self._state} 不接受新事务(单写者)")
            await self._transition(STATE_STAGING, "点确定")

        # ── ② blocking_set:有人在等结果 → 5 秒排空窗口,再交给用户裁定 ──
        blockers = self.blocking_leases()
        if blockers and not interrupt_running:
            await asyncio.sleep(DRAIN_WINDOW_S)          # ★ 先给排空窗口,再问
            blockers = self.blocking_leases()
            if blockers:
                async with self._lock:
                    await self._transition(STATE_READY, "有任务在跑,交还用户裁定")
                return ApplyResult(False, "needs_user_choice", STATE_READY,
                                   "有任务在跑:请选『优雅中断』或『等它跑完』",
                                   blocking=[l.lease_id for l in blockers])

        # ── ③ PRECHECK:现采、现判 —— 不用预览快照 ──
        async with self._lock:
            await self._transition(STATE_PRECHECK, "整组预检")
        prev = list(self._committed)                      # 上一个成功集合(回滚锚点)
        await asyncio.get_running_loop().run_in_executor(None, self._sample_once)
        if self._free is None:
            return await self._back_to_staging("precheck_no_sample",
                                               f"取不到 NVML 读数:{self._sampler_error}")
        v = vram_gate.evaluate(list(requested), self.cfg, free=self._free,
                               resident=prev, reserved=self.reserved_components())
        if not v.ok:
            # ★★ 第 2 条:不过就回编辑态,**committed 一字未动** —— 断言直接比对 prev
            return await self._back_to_staging(f"gate_{v.gate}", v.message)

        # ★★★ 装载器缺席 → 在这里失败关闭。**不得**继续走到 APPLYING/READY。
        if self._loader is None:
            # ★ 2026-08-05 审计:这条消息原来写的是「装载器尚未实现(P5)」。
            #   两处都是假话 —— 装载器 S14 就实现了(model_loader.py),而 P5 是语音 v1。
            #   照着这条消息去查的人会得出"意料之中,等下个阶段"的结论,
            #   而真相是**接线断了**。⇒ 消息必须说出**这一次**是哪一种。
            why = (f"装载器接入失败:{self._loader_error}" if self._loader_error
                   else "这个 Broker 实例没有接装载器(生产路径在网关启动时接;"
                        "测试里有意不接,用来验失败关闭)")
            return await self._back_to_staging(
                "loader_absent",
                f"{why}。事务在此失败关闭 —— 若放行,状态机会报 READY "
                "而显存里一个字节都没有,那正是 I2 存在的理由所要禁止的事")

        # ── ④ APPLYING:一律先卸后装 ──
        async with self._lock:
            await self._transition(STATE_APPLYING, "预检通过")
        try:
            drop = [c for c in prev if c not in requested]
            if drop:
                expect = self._free + sum(self.cfg.peak(c) for c in drop if c in self.cfg.components)
                await self._loader.unload(drop)
                err = await self._await_reclaim(expect)
                if err:
                    return await self._to_reconciling(err, f"卸载 {drop} 后显存未回收")
            add = [c for c in requested if c not in prev]
            if add:
                await self._loader.load(add)
        except Exception as e:
            # ── 回滚到上一个成功集合;回滚也失败 → DEGRADED_SAFE ──
            try:
                await self._loader.load(prev)
            except Exception as e2:
                async with self._lock:
                    await self._transition(STATE_RECONCILING, "装载失败")
                    await self._transition(STATE_DEGRADED_SAFE, "回滚失败")
                    self._committed = []
                    self._generation += 1
                    self._notify_locked()
                return ApplyResult(False, "rollback_failed", STATE_DEGRADED_SAFE,
                                   f"装载失败({e})且回滚失败({e2})—— 等价 Off + 托盘红 + 不可忽略通知")
            async with self._lock:
                self._committed = prev
                await self._transition(STATE_RECONCILING, "装载失败,已回滚")
            return ApplyResult(False, "load_failed_rolled_back", STATE_RECONCILING, str(e))

        async with self._lock:
            self._committed = list(requested)
            self._intended = list(requested)
            if permitted is not None:
                self._permitted_on_demand = list(permitted)
            await self._transition(STATE_READY, "事务完成")
        return ApplyResult(True, "", STATE_READY, "已应用")

    async def _back_to_staging(self, code: str, msg: str) -> "ApplyResult":
        """预检不过的**唯一**落点。★ 此路径上没有任何一行碰 `_committed` ——
        方案书第 2 条「不卸载任何东西」的字面落实,断言按源码检查。"""
        async with self._lock:
            await self._transition(STATE_STAGING, "预检不过,回编辑态")
        return ApplyResult(False, code, STATE_STAGING, msg)

    async def _to_reconciling(self, code: str, msg: str) -> "ApplyResult":
        """RECONCILING:**必须继续按 actual_resident 提供服务**(方案书行 1606-1608)。

        ★ 否则一个语音 worker 掉线会连带把仍然可用的 LLM 也判成不可用 —— **比故障本身更糟**。
          复用 §8.1.4 既有的 `contract_changed`(默认放行 + 回带真实契约 + X-LocalAI-Contract 头)。
        """
        async with self._lock:
            await self._transition(STATE_RECONCILING, code)
        return ApplyResult(False, code, STATE_RECONCILING, msg)

    def serves_requests(self) -> bool:
        """哪些状态仍然对外提供服务。★ RECONCILING **必须**是 True —— 见 `_to_reconciling`。"""
        return self._state in SERVING_STATES


# 进程内单例 —— 「单一权威」的字面落点。
BROKER = Broker()
