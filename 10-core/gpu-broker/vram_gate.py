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
class Calibration:
    """标定指纹(B10)。★ P4-S6 之前这一整段被 load_config **静默丢弃** ——
    Config 里连承载它的字段都没有,而 toml 的注释写着「P4 起由 vram_gate 在启动期比对」。
    读了不用比不读更危险:它会让人以为标定状态已经在把关。"""
    gpu_name: str = ""
    gpu_total_vram: float = 0.0
    driver: str = ""
    measured_at: str = ""
    # ★ 以下两项【不是】2026-07-27 那次测量时记下来的,是 2026-08-04 补录的。
    #   ⇒ 它们只能检测「08-04 之后」的换卡;07-27~08-04 之间若换过卡,我们不知道,
    #     而且**不能假装知道** —— 把今天的值追认为当时的测量条件,就是一次伪装成"补录"的捏造。
    #     (与显示指纹那半边同一条纪律,见 display_calibrated。)
    gpu_uuid: str = ""
    vbios: str = ""
    uuid_recorded_at: str = ""
    # 显示指纹:P1-A7 那轮没有记录分辨率/显示器数量,**不可回填**。
    display_calibrated: bool = False


@dataclass
class Config:
    budget: Budget
    components: Dict[str, dict]
    presets: Dict[str, dict] = field(default_factory=dict)
    calibration: Calibration = field(default_factory=Calibration)

    def peak(self, cid: str) -> float:
        return float(self.components[cid]["peak"])


def load_config(path: Path = CONFIG_PATH) -> Config:
    with open(path, "rb") as f:
        raw = tomllib.load(f)
    b = raw["budget"]
    c = raw.get("calibration", {})
    return Config(
        budget=Budget(
            total_vram=float(b["total_vram"]),
            desktop_floor=float(b["desktop_floor"]),
            safety_margin=float(b["safety_margin"]),
            calibrated=str(b.get("calibrated", "false")),
        ),
        components=raw.get("components", {}),
        presets=raw.get("presets", {}),
        calibration=Calibration(
            gpu_name=str(c.get("gpu_name", "")),
            gpu_total_vram=float(c.get("gpu_total_vram", 0.0) or 0.0),
            driver=str(c.get("driver", "")),
            measured_at=str(c.get("measured_at", "")),
            gpu_uuid=str(c.get("gpu_uuid", "")),
            vbios=str(c.get("vbios", "")),
            uuid_recorded_at=str(c.get("uuid_recorded_at", "")),
            display_calibrated=bool(c.get("display_calibrated", False)),
        ),
    )


# ── B10 · 硬件指纹(GPU 半边)────────────────────────────────────
#
#  ★★ 口径必须与【写入端】同源。实测:同一张卡
#       nvidia-smi → driver 610.62
#       WMI        → driver 32.0.16.1062
#     toml 记的是 nvidia-smi 那套。若比对端改用 WMI,会得到**永远不相等 → 永远拒绝启动**
#     —— 一个 fail-closed 机制最难查的失效方式(它看起来像"硬件真的变了")。
#     故本函数写死走 nvidia-smi,并有断言钉住。

def probe_gpu_identity() -> Optional[Dict[str, str]]:
    """实测 GPU 身份。取不到返回 None —— 调用方 fail-closed。"""
    try:
        out = subprocess.run(
            ["nvidia-smi",
             "--query-gpu=name,memory.total,driver_version,uuid,vbios_version",
             "--format=csv,noheader,nounits"],
            capture_output=True, text=True, timeout=10, check=True,
        ).stdout.strip().splitlines()[0]
        parts = [p.strip() for p in out.split(",")]
        if len(parts) < 5:
            return None
        return {"name": parts[0], "total_mib": parts[1], "driver": parts[2],
                "uuid": parts[3], "vbios": parts[4]}
    except Exception:
        return None


def check_calibration(cfg: Optional[Config] = None,
                      probe: Optional[Dict[str, str]] = None) -> "Verdict":
    """B10:启动期比对 GPU 指纹。不一致 → 拒绝,并**说清要重测什么**。

    ★ 只比【当时真的记下来了】的那几项。gpu_uuid / vbios 是 2026-08-04 补录的,
      只有在 toml 里存了值时才参与比对 —— 不能拿今天的值去反推当年测的是哪张卡。

    ★ B10② 的分工:GPU 指纹不符 → 重测**组件 peak 里与硬件相关的部分**(主要是 CUDA context,P1-A1);
      显示指纹不符 → 重测 **desktop_floor**(A7)。两者驱动不同的重测项,因为可移植性不同。
    """
    cfg = cfg or load_config()
    cal = cfg.calibration
    if probe is None:
        probe = probe_gpu_identity()
    if probe is None:
        return Verdict(
            ok=False, gate="calibration",
            message=("标定比对:读不到 GPU 身份(nvidia-smi 失败)。\n"
                     "  → 拒绝。不能在『不知道这是哪张卡』的前提下沿用一套实测数字。"),
        )
    if not cal.gpu_name:
        return Verdict(
            ok=False, gate="calibration",
            message=("标定比对:config/vram-budget.toml 缺 [calibration].gpu_name。\n"
                     "  → 拒绝。没有基准就无从比对,而『无从比对』不等于『一致』。"),
        )

    diffs = []
    if probe["name"] != cal.gpu_name:
        diffs.append(f"型号:记录 {cal.gpu_name} → 实测 {probe['name']}")
    try:
        live_total = round(int(probe["total_mib"]) / 1024.0, 2)
        if abs(live_total - round(cal.gpu_total_vram, 2)) > 0.02:
            diffs.append(f"总显存:记录 {cal.gpu_total_vram} GiB → 实测 {live_total} GiB")
    except Exception:                                        # noqa: BLE001
        diffs.append(f"总显存:实测值解析不了({probe['total_mib']})")
    if cal.driver and probe["driver"] != cal.driver:
        diffs.append(f"驱动:记录 {cal.driver} → 实测 {probe['driver']}(nvidia-smi 口径)")
    # ★ 只有存了值才比 —— 见函数头
    if cal.gpu_uuid and probe["uuid"] != cal.gpu_uuid:
        diffs.append(f"GPU UUID:记录 {cal.gpu_uuid} → 实测 {probe['uuid']}(**换了另一张同型号卡**)")
    if cal.vbios and probe["vbios"] != cal.vbios:
        diffs.append(f"VBIOS:记录 {cal.vbios} → 实测 {probe['vbios']}")

    if diffs:
        return Verdict(
            ok=False, gate="calibration",
            message=("标定比对:硬件与标定时**不一致**,拒绝启动。\n  "
                     + "\n  ".join(diffs)
                     + f"\n  (标定于 {cal.measured_at or '未记录'})\n"
                       "  → 要重测的是【组件 peak 里与硬件相关的部分】,主要是 CUDA context(P1-A1);\n"
                       "    desktop_floor 由**显示配置**决定,换卡不换显示器时它大体可移植(B10②)。\n"
                       "  ★ 确认硬件就该是这样 ⇒ 更新 config/vram-budget.toml 的 [calibration] 并重测,\n"
                       "    不要只改指纹放行 —— 那等于把一套别的卡上的实测数字当成本机的。"),
        )
    return Verdict(ok=True, gate="calibration",
                   message=f"标定比对:GPU 指纹一致({cal.gpu_name} · 驱动 {cal.driver} · 标定于 {cal.measured_at})")


def diagnose_budget(cfg: Optional[Config] = None) -> Optional[str]:
    """B10④:预算连**最小**的组件都装不下时,要报「GPU 路线不成立」而不是逐个静默拒绝。

    ★ 症状很具体:换成 8GB 卡后 vram_budget = 8 − 6.6 − 0.8 = 0.60,
      于是勾什么都被拒、而且每次给的都是同一条正确但无用的建议(「取消组件或改桌面预留」)。
      人会以为是自己选错了组合,而真相是这条路线在这台机器上不成立。
    """
    cfg = cfg or load_config()
    if not cfg.components:
        return None
    smallest = min(cfg.peak(c) for c in cfg.components)
    who = min(cfg.components, key=lambda c: cfg.peak(c))
    if cfg.budget.vram_budget < smallest:
        return (f"★ GPU 路线在这台机器上不成立:vram_budget 只有 {cfg.budget.vram_budget:.2f} GiB,"
                f"而**最小**的组件 {who} 也要 {smallest:.2f} GiB。\n"
                f"  总显存 {cfg.budget.total_vram:.2f} − 桌面预留 {cfg.budget.desktop_floor:.2f}"
                f" − 安全余量 {cfg.budget.safety_margin:.2f} = {cfg.budget.vram_budget:.2f}。\n"
                "  → 这不是「选错了组合」,是**没有任何组合能成立**。要么换卡,要么把桌面预留降下来\n"
                "    (但 desktop_floor 是实测值,降它意味着接受桌面卡顿),要么这台机器只跑 CPU 档。")
    return None


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
             skip_dynamic: bool = False,
             resident: Optional[List[str]] = None,
             reserved: Optional[List[str]] = None) -> Verdict:
    """对【申请后的完整 AI 驻留集合】跑三层检查。

    component_ids 是「这次操作之后应当驻留的全部组件」,不是增量 ——
    §8.1 静态闸的定义就是 Σpeak(申请后的驻留集合)。

    ── 三集合(2026-08-04 · P4-S4a 裁定)────────────────────────────────
    判据的全部关键是:**哪些显存已经被 NVML free 反映了**。

      resident(=loaded) 已装载   → 物理上已占,**已经**从 free 里扣掉了 ⇒ 再减一次就是重复计
      reserved          已批准未装载 → **还没**占,free 里看得见 ⇒ 不显式减的话,
                                       别人会把这块"空闲"再批给自己 —— 那正是 D37 ④ 要关的竞态
      incoming          本次新占     → 还没占 ⇒ 必须减

    ⇒ 动态闸 = `free − incoming − Σpeak(reserved \\ loaded \\ 本次请求集) ≥ safety_margin`

    ★★ 最后那个**双重差集**是承重的,不是洁癖:已经算进 incoming 的那些预留
      **不能再减第二次**。把 `loaded ∪ reserved` 一股脑塞进旧的单一 `resident` 参数,
      会让每个"已批准但尚未装载"的组件被扣两遍(既被排除在 incoming 外、又还没出现在
      NVML free 里)⇒ 闸误判成"更空",**两个客户端双双获批** ——
      D37 ④ 要关的那个竞态,会从关它的那个参数里重新打开。本函数用差集在结构上堵死它。

    ★ 静态闸不受影响:它取 Σpeak(申请后的完整集合),管的是**预算**,与物理占用无关(D37 原式)。
    ★ 向后兼容:不传 reserved 时行为与 2026-07-31 版逐字节相同(reserved 为空集,差集为空)。
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
        # ★★ 动态闸只算【本次要新占】的部分(2026-07-31 审计):
        #   已驻留组件的显存【已经被 NVML free 扣掉了】,再把它们算进 incoming 等于重复计一遍,
        #   会误拒本来装得下的申请,还错误归因到"物理墙、改桌面预留没用"。
        #   cold-start(resident 为空)时 incoming == total,行为逐字节不变(38 条现有测试全过)。
        resident = resident or []
        pending = [c for c in component_ids if c not in resident]
        incoming = round(sum(cfg.peak(c) for c in pending), 4)
        # ★★ 别人已预留、但【尚未装载】、且【不在本次请求集里】的那部分 ——
        #   它还占着 free 的名额却没占物理显存。双重差集见函数头:
        #   `- loaded` 排除已装载(已在 free 里扣过);`- component_ids` 排除已算进 incoming 的。
        _res = reserved or []
        others_reserved_ids = [c for c in _res if c not in resident and c not in component_ids]
        others_reserved = round(sum(cfg.peak(c) for c in others_reserved_ids if c in cfg.components), 4)
        margin_after = round(free - incoming - others_reserved, 4)
        if margin_after < cfg.budget.safety_margin:
            # ★ 被别人的【预留】挡住,与被桌面占用挡住,是两种完全不同的处境 ——
            #   前者要等/协商,后者要关程序。文案混在一起会把人支去关浏览器,而真正占着的是另一台客户端。
            res_note = ""
            if others_reserved > 0:
                res_note = (f"其中 {others_reserved:.2f} 是**别处已批准但尚未装载**的预留"
                            f"({'、'.join(others_reserved_ids)})—— 它还没占物理显存,但名额已被占下。\n"
                            f"  → 这一部分**关程序没用**:要么等它装完/释放,要么让它先撤销预留。\n")
            if resident:
                ai_now = round(sum(cfg.peak(c) for c in resident if c in cfg.components), 2)
                desktop_now = round(cfg.budget.total_vram - free - ai_now, 2)
                return Verdict(
                    ok=False, gate="dynamic", requested=component_ids,
                    total_peak=total, vram_budget=budget, free=free,
                    message=(f"动态闸:此刻只有 {free:.2f} GiB 可用。"
                             f"桌面等正占约 {desktop_now:.2f}(推算),中枢已驻留 {ai_now}"
                             f"({'、'.join(resident)}),本次要新增 {incoming:.2f}，"
                             f"装完只剩 {margin_after:.2f} < 安全余量 {cfg.budget.safety_margin:.2f}。\n"
                             + res_note +
                             f"  → 卸掉某个已驻留组件,**或**关掉占显存的程序(浏览器/游戏/UE5)。"),
                )
            desktop_now = round(cfg.budget.total_vram - free, 2)
            return Verdict(
                ok=False, gate="dynamic", requested=component_ids,
                total_peak=total, vram_budget=budget, free=free,
                message=(f"动态闸:此刻只有 {free:.2f} GiB 可用(桌面等正占 {desktop_now:.2f})，"
                         f"这组要 {incoming:.2f}，装完只剩 {margin_after:.2f} < 安全余量 {cfg.budget.safety_margin:.2f}。\n"
                         + res_note +
                         f"  → **改桌面预留没用** —— 撞的是物理墙。关掉占显存的程序(浏览器/游戏/UE5)再试。"),
            )

    # ★ 成功文案的"装完剩"要按【本次新占】算,不按 total —— 否则复用场景下已驻留部分被重复减,
    #   会显示一个吓人的负数(明明装得下)。cold-start 时 incoming == total,数字不变。
    _resident = resident or []
    _incoming = round(sum(cfg.peak(c) for c in component_ids if c not in _resident), 4)
    return Verdict(
        ok=True, requested=component_ids, total_peak=total, vram_budget=budget,
        free=free, headroom=(round(budget - total, 4)),
        message=(f"通过:Σpeak {total:.2f} ≤ vram_budget {budget:.2f}"
                 f"(余 {budget - total:.2f})"
                 + (f"；实测可用 {free:.2f}，本次新占 {_incoming:.2f}，装完剩 {round(free - _incoming, 2):.2f}"
                    if free is not None else "")),
    )


_UNSET = object()


def preview(component_ids: List[str], cfg: Optional[Config] = None,
            free=_UNSET, verdict: Optional[Verdict] = None) -> str:
    """三段预览(§8.1)。★ 缺一段就是在骗人 —— 只显示「装得下」是不够的:
    桌面占用是**波动**的,你需要知道自己离墙有多远,而不是知道此刻没撞墙。

    ★★ 2026-08-04:新增 free / verdict 两个可注入参数。原实现自己采一次 NVML,
      而 `__main__` 随后又调 `evaluate()` 采第二次 —— **打印出来的判定与退出码来自两次
      独立采样**,中间桌面占用一变,二者就能互相矛盾(§8.1 规则 18「预览与准入必须是同一段
      代码」当时只满足一半)。现在调用方可以把同一次采样与同一个 Verdict 传进来。

    ★ 判定符用 ASCII 的 [OK]/[XX],不用 ✓/✗:承重的那个 token 不该依赖控制台编码 ——
      cp936 打不出 U+2713,而这正是本文件唯一的生产入口一直崩在的地方(见 __main__)。
    """
    cfg = cfg or load_config()
    if free is _UNSET:
        free = nvml_free_gib()
    if verdict is None:
        verdict = evaluate(component_ids, cfg, free=free)
    v = verdict
    known = [c for c in component_ids if c in cfg.components]
    unknown = [c for c in component_ids if c not in cfg.components]
    total = round(sum(cfg.peak(c) for c in known), 4)
    # ★ 未登记组件不再被静默过滤掉:那会让①段显示一个偏小的合计、甚至标成"装得下",
    #   而准入白名单其实已经拒了它 —— 预览与准入在**恰好是白名单存在理由**的那个输入上
    #   给出相反结论。现在显式标出来。
    seg1 = "① 已选组件   " + "  ".join(f"{c} {cfg.peak(c):.2f}" for c in known) + f"   = {total:.2f}"
    if unknown:
        seg1 += "   ⟨未登记: " + "、".join(unknown) + "⟩"
    lines = [
        seg1,
        f"② 桌面预留   desktop_floor {cfg.budget.desktop_floor:.2f}  →  vram_budget = {cfg.budget.vram_budget:.2f}"
        + (f"   [标定: {cfg.budget.calibrated}]" if cfg.budget.calibrated != "true" else ""),
        f"③ 此刻可用   NVML free {free:.2f}" if free is not None else "③ 此刻可用   (读不到)",
        "-" * 62,
        ("  可以确定 [OK]  " + v.message) if v.ok else ("  不能确定 [XX]\n  " + v.message.replace("\n", "\n  ")),
    ]
    return "\n".join(lines)


# ── CLI ───────────────────────────────────────────────────────────
#  退出码是【三态】的,不是两态。★★ 2026-08-04:这是本次修复的核心。
#
#  原实现只有 0/1,于是「闸判定为拒」与「闸自己崩了」在退出码这一位上**完全不可分辨**,
#  而 start-stack.ps1 只看退出码。实测后果(自 2026-07-28 集成起一直如此):
#    在没有 PYTHONIOENCODING 的干净 PowerShell(cp936)里,连**通过**的路径也会在
#    `print(preview(...))` 处抛 UnicodeEncodeError —— 因为判定符是 U+2713 ✓,GBK 里没有。
#    stdout 为空、退出码 1 ⇒ start-stack 打印「拒绝启动」。
#    ⇒ 这台机器上要么起不了栈,要么每次靠 -Force,而 **-Force 把三道闸整个跳过**。
#      那道「无 Broker 期显存过渡措施」因此实际上一天都没生效过。
#
#  形状与 2026-08-04 早些时候修掉的出包门禁同源:**失败与成功长得一模一样**。
EXIT_OK      = 0   # 判定:通过
EXIT_REFUSED = 1   # 判定:拒绝(三道闸之一说不行)—— 这是闸在正常工作
EXIT_BROKEN  = 2   # 闸【没能跑起来】:配置坏了 / 解析失败 / 自身异常。调用方不得当成"被拒"

def main(argv: List[str]) -> int:
    """CLI 主体。★ 抽成函数是为了让「闸崩了 → 退出码 2」这条**可测** ——
    留在 `if __name__` 里的话,只能靠篡改真实配置文件来触发异常路径。"""
    import sys

    # ★ 双保险之一:显式把 stdout 拉到 utf-8。判定符已改 ASCII(双保险之二),
    #   但组件 id、note、中文文案仍可能超出 cp936;errors='replace' 保证**永远打得出字**。
    #   宁可看到一个 '?',也不要让一道安全闸因为控制台编码而变成"拒绝"。
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    try:
        args = [a for a in argv if not a.startswith("-")]
        cfg = load_config()

        # ── B10 · 启动期标定门禁(P4-S6)────────────────────────────
        #  ★ 退出码用 2(EXIT_BROKEN)而不是 1(EXIT_REFUSED),这不是措辞问题:
        #    1 是「闸看了你的申请,说不行」—— 可以被 -Force 覆盖(你明知故犯);
        #    2 是「闸的**基准本身**不作数」—— 不可覆盖。硬件换了还沿用旧的实测数字,
        #    强行放行得到的每个判定都是无意义的,那比不放行更危险。
        _cal = check_calibration(cfg)
        if not _cal.ok:
            print("[标定门禁] " + _cal.message, file=sys.stderr)
            return EXIT_BROKEN

        # ── B10④ · 预算连最小组件都装不下 → 报「路线不成立」而非逐个静默拒绝 ──
        _diag = diagnose_budget(cfg)
        if _diag:
            print("[预算诊断] " + _diag, file=sys.stderr)
            return EXIT_BROKEN

        if not args:
            print("可用组件:")
            for cid, c in sorted(cfg.components.items(), key=lambda kv: kv[1]["peak"]):
                print(f"  {cid:32} {float(c['peak']):5.2f}  {c.get('note','')}")
            print(f"\nvram_budget = {cfg.budget.total_vram} - {cfg.budget.desktop_floor}"
                  f" - {cfg.budget.safety_margin} = {cfg.budget.vram_budget:.2f} GiB")
            print("\n推荐组合:")
            for name, p in cfg.presets.items():
                ids = p["components"]
                # ★ 预设里引用未登记组件时不再静默过滤:那会打出一个偏小的合计并可能标 [OK],
                #   而真去申请会被准入白名单拒 —— 又一处"预览与准入结论相反"。
                bad = [i for i in ids if i not in cfg.components]
                tot = sum(cfg.peak(i) for i in ids if i in cfg.components)
                mark = "[XX]" if bad else ("[OK]" if tot <= cfg.budget.vram_budget else "[XX]")
                tail = ("  <未登记: " + "、".join(bad) + ">") if bad else ""
                print(f"  {p['label']:8} {tot:5.2f}  {mark}  {'、'.join(ids)}{tail}")
            return EXIT_OK

        # ★ 只采样一次 NVML,预览与退出码共用同一个 Verdict —— 见 preview() 的说明。
        free = nvml_free_gib()
        v = evaluate(args, cfg, free=free)
        print(preview(args, cfg, free=free, verdict=v))
        return EXIT_OK if v.ok else EXIT_REFUSED

    except Exception as e:                                   # noqa: BLE001
        # ★ 闸自己坏了 —— 必须与"被拒"分开,否则调用方会把一次崩溃当成一次正常拒绝,
        #   而"正常拒绝"是可以用 -Force 覆盖的。崩溃不可以。
        import traceback
        print(f"[闸自身异常] {type(e).__name__}: {e}", file=sys.stderr)
        traceback.print_exc()
        print("  -> 这【不是】判定为拒,是显存闸没能跑起来。不得据此 -Force 强行启动:"
              "没拿到判定就装 = §12.3 禁止的静默降级。", file=sys.stderr)
        return EXIT_BROKEN


if __name__ == "__main__":
    import sys
    sys.exit(main(sys.argv[1:]))
