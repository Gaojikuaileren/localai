"""显存闸 —— 无 Broker 期的过渡措施(§8.1 三层检查)

P2–P5 是「无 Broker 期」:GPU 变更**不上网关**(§8.1 空窗期整体 not_provisioned),
只有本机脚本 + 托盘。本模块就是那个本机脚本用的闸,并且是 P4 GPU Broker 的判定内核 ——
到 P4 时 Broker 直接复用它,而不是另写一套(§8.1「预览与准入必须是同一段代码」)。

三层检查防的不是同一件事(§8.1.1):

    准入白名单(集合)  bug · 重复 spawn · 泄漏租约 · 未授权组件
    静态闸(算术)      「上次不小心勾多了」· 跨会话持久 · 给桌面留地方
    动态闸(实时)      此刻真的装不下 · 桌面临时涨了

★ bug 那一类必须靠白名单,不能靠闸:闸是**算术**,可以凑数凑过去;白名单是**集合**,
  漏了会响亮报错。这是「漏 payload filter 是沉默的,漏 collection 句柄是响亮的」
  在显存侧的同一条道理。

★★ 两种撞墙【绝不可合并】成一句「显存不足」(§8.1 明写有害):
    撞 vram_budget → 你该取消组件**或改桌面预留**
    撞实时可用     → **改预留没用**,得关掉占显存的程序
  给错建议 = 让人白折腾一轮。
"""
from __future__ import annotations

import subprocess
import tomllib
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, List, Optional

CONFIG_PATH = Path(__file__).resolve().parents[2] / "config" / "vram-budget.toml"


# ── 配置 ──────────────────────────────────────────────────────────
@dataclass
class Budget:
    total_vram: float
    desktop_floor: float
    safety_margin: float
    calibrated: str

    @property
    def vram_budget(self) -> float:
        """导出值,不单独设置(§8.1)。"""
        return round(self.total_vram - self.desktop_floor - self.safety_margin, 4)


@dataclass
class Config:
    budget: Budget
    components: Dict[str, dict]
    presets: Dict[str, dict] = field(default_factory=dict)

    def peak(self, cid: str) -> float:
        return float(self.components[cid]["peak"])


def load_config(path: Path = CONFIG_PATH) -> Config:
    with open(path, "rb") as f:
        raw = tomllib.load(f)
    b = raw["budget"]
    return Config(
        budget=Budget(
            total_vram=float(b["total_vram"]),
            desktop_floor=float(b["desktop_floor"]),
            safety_margin=float(b["safety_margin"]),
            calibrated=str(b.get("calibrated", "false")),
        ),
        components=raw.get("components", {}),
        presets=raw.get("presets", {}),
    )


# ── 实测可用显存 ──────────────────────────────────────────────────
def nvml_free_gib() -> Optional[float]:
    """NVML 实测空闲显存(GiB)。取不到返回 None —— 调用方必须据此 fail-closed。"""
    try:
        out = subprocess.run(
            ["nvidia-smi", "--query-gpu=memory.free", "--format=csv,noheader,nounits"],
            capture_output=True, text=True, timeout=10, check=True,
        ).stdout.strip().splitlines()[0]
        return round(int(out) / 1024.0, 4)
    except Exception:
        return None


# ── 判定 ──────────────────────────────────────────────────────────
@dataclass
class Verdict:
    ok: bool
    gate: str = ""            # admission | static | dynamic | ""
    message: str = ""
    requested: List[str] = field(default_factory=list)
    total_peak: float = 0.0
    vram_budget: float = 0.0
    free: Optional[float] = None
    headroom: Optional[float] = None    # 通过时:离墙还有多远(用实际 peak 算,不是 budget)


def evaluate(component_ids: List[str],
             cfg: Optional[Config] = None,
             free: Optional[float] = None,
             skip_dynamic: bool = False) -> Verdict:
    """对【申请后的完整 AI 驻留集合】跑三层检查。

    component_ids 是「这次操作之后应当驻留的全部组件」,不是增量 ——
    §8.1 静态闸的定义就是 Σpeak(申请后的驻留集合)。
    """
    cfg = cfg or load_config()
    budget = cfg.budget.vram_budget

    # ---- 1. 准入白名单(集合成员,不是算术)----
    unknown = [c for c in component_ids if c not in cfg.components]
    if unknown:
        return Verdict(
            ok=False, gate="admission", requested=component_ids, vram_budget=budget,
            message=("准入白名单:未登记的组件 " + "、".join(unknown) +
                     "。\n  已登记:" + "、".join(sorted(cfg.components)) +
                     "\n  (组件必须先进 config/vram-budget.toml 才能申请 —— "
                     "白名单是集合,漏了会响亮报错,而闸是算术、能被凑过去。)"),
        )

    total = round(sum(cfg.peak(c) for c in component_ids), 4)

    # ---- 2. 静态闸(算术)----
    if total > budget:
        over = round(total - budget, 2)
        allows = round(cfg.budget.total_vram - total - cfg.budget.safety_margin, 2)
        return Verdict(
            ok=False, gate="static", requested=component_ids,
            total_peak=total, vram_budget=budget,
            message=(f"静态闸:这组 Σpeak {total:.2f} > vram_budget {budget:.2f}，超 {over:.2f} GiB。\n"
                     f"  你设的桌面预留 {cfg.budget.desktop_floor:.2f}，这组只够留 {allows:.2f}。\n"
                     f"  → 取消组件，**或**改桌面预留(config/vram-budget.toml 的 desktop_floor)。"),
        )

    # ---- 3. 动态闸(实时)----
    if not skip_dynamic:
        if free is None:
            free = nvml_free_gib()
        if free is None:
            return Verdict(
                ok=False, gate="dynamic", requested=component_ids,
                total_peak=total, vram_budget=budget,
                message=("动态闸:读不到 NVML 实测可用显存(nvidia-smi 失败)。\n"
                         "  → 拒绝执行。宁可不装,不做「不知道装不装得下就先装」——"
                         "那正是 §12.3 禁止的静默降级。"),
            )
        margin_after = round(free - total, 4)
        if margin_after < cfg.budget.safety_margin:
            desktop_now = round(cfg.budget.total_vram - free, 2)
            return Verdict(
                ok=False, gate="dynamic", requested=component_ids,
                total_peak=total, vram_budget=budget, free=free,
                message=(f"动态闸:此刻只有 {free:.2f} GiB 可用(桌面等正占 {desktop_now:.2f})，"
                         f"这组要 {total:.2f}，装完只剩 {margin_after:.2f} < 安全余量 {cfg.budget.safety_margin:.2f}。\n"
                         f"  → **改桌面预留没用** —— 撞的是物理墙。关掉占显存的程序(浏览器/游戏/UE5)再试。"),
            )

    return Verdict(
        ok=True, requested=component_ids, total_peak=total, vram_budget=budget,
        free=free, headroom=(round(budget - total, 4)),
        message=(f"通过:Σpeak {total:.2f} ≤ vram_budget {budget:.2f}"
                 f"(余 {budget - total:.2f})"
                 + (f"；实测可用 {free:.2f}，装完剩 {free - total:.2f}" if free is not None else "")),
    )


def preview(component_ids: List[str], cfg: Optional[Config] = None) -> str:
    """三段预览(§8.1)。★ 缺一段就是在骗人 —— 只显示「装得下」是不够的:
    桌面占用是**波动**的,你需要知道自己离墙有多远,而不是知道此刻没撞墙。"""
    cfg = cfg or load_config()
    free = nvml_free_gib()
    total = round(sum(cfg.peak(c) for c in component_ids if c in cfg.components), 4)
    v = evaluate(component_ids, cfg, free=free)
    lines = [
        "① 已选组件   " + "  ".join(f"{c} {cfg.peak(c):.2f}" for c in component_ids if c in cfg.components)
        + f"   = {total:.2f}",
        f"② 桌面预留   desktop_floor {cfg.budget.desktop_floor:.2f}  →  vram_budget = {cfg.budget.vram_budget:.2f}"
        + (f"   [标定: {cfg.budget.calibrated}]" if cfg.budget.calibrated != "true" else ""),
        f"③ 此刻可用   NVML free {free:.2f}" if free is not None else "③ 此刻可用   (读不到)",
        "─" * 62,
        ("  可以确定 ✓  " + v.message) if v.ok else ("  不能确定 ✗\n  " + v.message.replace("\n", "\n  ")),
    ]
    return "\n".join(lines)


if __name__ == "__main__":
    import sys
    args = [a for a in sys.argv[1:] if not a.startswith("-")]
    if not args:
        cfg = load_config()
        print("可用组件:")
        for cid, c in sorted(cfg.components.items(), key=lambda kv: kv[1]["peak"]):
            print(f"  {cid:32} {float(c['peak']):5.2f}  {c.get('note','')}")
        print(f"\nvram_budget = {cfg.budget.total_vram} − {cfg.budget.desktop_floor}"
              f" − {cfg.budget.safety_margin} = {cfg.budget.vram_budget:.2f} GiB")
        print("\n推荐组合:")
        for name, p in cfg.presets.items():
            ids = p["components"]
            tot = sum(cfg.peak(i) for i in ids if i in cfg.components)
            print(f"  {p['label']:8} {tot:5.2f}  {'✓' if tot <= cfg.budget.vram_budget else '✗'}  {'、'.join(ids)}")
        sys.exit(0)
    print(preview(args))
    sys.exit(0 if evaluate(args).ok else 1)
