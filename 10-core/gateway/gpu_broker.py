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
import time
from dataclasses import dataclass, field
from typing import Dict, List, Optional

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
    inferred: bool = True

    def to_json(self) -> Dict:
        return {
            "generation": self.generation,
            "committed": list(self.committed),
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
        return Snapshot(
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


# 进程内单例 —— 「单一权威」的字面落点。
BROKER = Broker()
