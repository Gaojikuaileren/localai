"""显存闸测试。跑:python test_vram_gate.py

★ 这里全是算术,而算错的后果是 OOM 或白白装不下 —— 必须测,不能靠眼看。
   动态闸统一注入固定的 free 值,不依赖真实 GPU 状态(否则测试结果随桌面占用漂移)。
"""
import sys
from vram_gate import load_config, evaluate, Verdict

cfg = load_config()
_p = _f = 0


def check(name, cond, extra=""):
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        print(f"  FAIL: {name} {extra}")


B = cfg.budget
print("=== 预算算术(§8.1:vram_budget 是导出值)===")
check("total 15.92", abs(B.total_vram - 15.92) < 1e-9)
check("desktop_floor 6.6(P1-A7 实测 6.53 + 余量)", abs(B.desktop_floor - 6.6) < 1e-9)
check("vram_budget = 15.92-6.6-0.8 = 8.52", abs(B.vram_budget - 8.52) < 1e-9, f"实得 {B.vram_budget}")

print("=== 组件 peak 必须与 §8.1.2 实测一致 ===")
for cid, want in [
    ("llm.assistant.8b@16k", 5.92), ("llm.assistant.8b@32k", 7.19),
    ("llm.assistant.30b-a3b@32k", 11.9), ("speech.lite", 2.07),
    ("vlm.small", 4.35), ("comfyui.sdxl", 8.14),
]:
    check(f"{cid} = {want}", abs(cfg.peak(cid) - want) < 1e-9, f"实得 {cfg.peak(cid)}")

print("=== 1. 准入白名单(集合,不是算术)===")
v = evaluate(["llm.assistant.8b@16k", "llm.nonexistent"], cfg, free=15.0)
check("未登记组件被拒", not v.ok)
check("归因到 admission", v.gate == "admission", v.gate)
check("消息点名了未知组件", "llm.nonexistent" in v.message)
# ★ 白名单必须先于算术:即使 peak 加起来很小也要拒
v = evaluate(["llm.bogus"], cfg, free=15.0)
check("哪怕只申请一个未知组件也拒", not v.ok and v.gate == "admission")

print("=== 2. 静态闸(Σpeak ≤ vram_budget)===")
v = evaluate(["llm.assistant.8b@16k", "speech.lite"], cfg, free=15.0)   # 5.92+2.07=7.99
check("日常组合 7.99 ≤ 8.52 通过", v.ok, v.message)
check("total_peak 算对", abs(v.total_peak - 7.99) < 1e-9, f"{v.total_peak}")

v = evaluate(["llm.assistant.30b-a3b@32k"], cfg, free=15.0)             # 11.9 > 8.52
check("深度模式 11.9 > 8.52 被拒", not v.ok)
check("归因到 static", v.gate == "static", v.gate)
check("消息含两个数字", "11.90" in v.message and "8.52" in v.message, v.message)
check("★ 消息给出「这组只够留多少」", "只够留" in v.message)
check("★ 消息提示可改桌面预留", "改桌面预留" in v.message)

v = evaluate(["llm.assistant.8b@32k", "speech.lite"], cfg, free=15.0)   # 7.19+2.07=9.26
check("长上下文+语音 9.26 > 8.52 被拒", not v.ok and v.gate == "static")

print("=== 3. 动态闸(实时可用)===")
# 静态过但实时不够:日常组合 7.99,只给 8.0 可用 → 装完剩 0.01 < 0.8
v = evaluate(["llm.assistant.8b@16k", "speech.lite"], cfg, free=8.0)
check("静态过但实时不足 → 拒", not v.ok)
check("归因到 dynamic", v.gate == "dynamic", v.gate)
check("★ 明说改预留没用", "改桌面预留没用" in v.message, v.message)
check("★ 提示关程序", "关掉占显存的程序" in v.message)

v = evaluate(["llm.assistant.8b@16k", "speech.lite"], cfg, free=8.79)   # 8.79-7.99=0.80 恰好
check("恰好等于安全余量 → 通过", v.ok, v.message)
v = evaluate(["llm.assistant.8b@16k", "speech.lite"], cfg, free=8.78)   # 0.79 < 0.8
check("差 0.01 → 拒", not v.ok and v.gate == "dynamic")

print("=== 4. ★ 两种撞墙的建议必须不同(§8.1:合并成「显存不足」是有害的)===")
vs = evaluate(["llm.assistant.30b-a3b@32k"], cfg, free=15.0)    # 撞 budget
vd = evaluate(["llm.assistant.8b@16k"], cfg, free=6.0)          # 撞实时
check("静态闸说『改桌面预留』", "改桌面预留" in vs.message)
check("动态闸说『改桌面预留没用』", "改桌面预留没用" in vd.message)
check("两条消息不同", vs.message != vd.message)
check("两条都带具体数字", any(ch.isdigit() for ch in vs.message) and any(ch.isdigit() for ch in vd.message))

print("=== 5. NVML 读不到 → fail-closed(★ 判据已下移到第 11 组)===")
# ★★★ 2026-08-05:这一组原来长这样 ——
#     if v.gate == "dynamic" and "读不到" in v.message:
#         check("读不到 NVML → 拒(fail-closed)", not v.ok)
#     else:
#         check("真机 NVML 可读,跳过该分支断言", True)      ← **恒真**
#   这台机器上 nvidia-smi 一直是好的,所以它**永远走 else**,那条 `True` 从没红过、也不可能红。
#   ★ 更刺眼的是:第 11 组的注释早就把这件事写明白了
#     (「原第 5 组在本机总是走 else 分支,那条 fail-closed 从没被执行过」)——
#     **发现了、写下来了、却把恒真的那条留在原地**。
#   ⇒ 今天由一次恒真断言扫描重新抓出来,现在删掉。
#     (★ 不写扫描器的路径:那套调试工具可整目录移除,写了路径就成了死引用。)
#     真正的判据在第 11 组:那里**注入** nvml_free_gib = lambda: None,
#     于是 fail-closed 分支**真的被执行过**。
#   ★ 这里不留任何 check() —— 留一条"占位"的断言,下一个人会以为这一组还在守着什么。
print("    (本组无断言:见第 11 组的注入版本)")

print("=== 6. 余量必须用实际 peak 算,不能用 budget(§8.1「余量核算」)===")
v = evaluate(["llm.assistant.8b@16k"], cfg, free=15.0)          # 5.92
check("headroom = 8.52-5.92 = 2.60", abs(v.headroom - 2.60) < 1e-9, f"{v.headroom}")

print("=== 7. 空集合 ===")
v = evaluate([], cfg, free=15.0)
check("空集合通过且 peak=0", v.ok and v.total_peak == 0.0)

print("=== 8. 推荐组合的注释与实际算术一致 ===")
for name, p in cfg.presets.items():
    ids = p["components"]
    tot = round(sum(cfg.peak(i) for i in ids), 4)
    fits = tot <= B.vram_budget
    claimed_fit = "✓" in p.get("note", "")
    claimed_nofit = "✗" in p.get("note", "")
    if claimed_fit or claimed_nofit:
        check(f"preset {name} 注释与算术一致(Σ={tot})", fits == claimed_fit,
              f"算术 fits={fits} 注释={'✓' if claimed_fit else '✗'}")

# ══════════════════════════════════════════════════════════════════════
#  S0 · 闸复活(2026-08-04)
#
#  起因:实测发现 vram_gate 的【唯一生产集成】一直是坏的 —— 在没有 PYTHONIOENCODING
#  的干净 PowerShell(cp936)里,连**通过**的路径也会在 print(preview(...)) 抛
#  UnicodeEncodeError(判定符是 U+2713 ✓,GBK 里没有),stdout 为空、退出码 1,
#  而 start-stack.ps1 只看退出码 ⇒ 打印「拒绝启动」。
#  ⇒ 这台机器上要么起不了栈、要么靠 -Force,而 -Force 把三道闸整个跳过。
#
#  ★ 上面那 38 条断言全绿却没发现它 —— 因为它们是【进程内】跑的,
#    而坏掉的恰好是那条唯一的、跨进程的生产路径。这一节专治这个盲区。
# ══════════════════════════════════════════════════════════════════════
import os
import re
import subprocess
import tomllib
from pathlib import Path

import vram_gate

_HERE = Path(__file__).resolve().parent
_REPO = _HERE.parents[1]

print("=== 9. ★ 反向全表:组件表必须【逐字】等于期望的 9 项,且每项 peak 单独钉住 ===")
#   正向逐项测只守得住已知项;新增一个组件会**静默进入准入白名单并参与算术**,
#   而准入白名单正是"漏了会响亮报错"的那道闸 —— 它自己反而没人守。
EXPECTED_PEAKS = {
    "llm.assistant.8b@8k":       5.31,   # ★ 2026-08-04 由 5.0 更正(见 toml 内注释)
    "llm.assistant.8b@16k":      5.92,
    "llm.assistant.8b@32k":      7.19,
    "llm.assistant.30b-a3b@32k": 11.9,
    "speech.lite":               2.07,
    "speech.full":               4.05,
    "vlm.small":                 4.35,
    "comfyui.sdxl":              8.14,
    "comfyui.sdxl.lowvram":      7.8,
}
check("组件集合逐字相等(新增/删除组件必须改这里)",
      set(cfg.components) == set(EXPECTED_PEAKS),
      f"多出 {sorted(set(cfg.components) - set(EXPECTED_PEAKS))} 少了 {sorted(set(EXPECTED_PEAKS) - set(cfg.components))}")
for cid, want in EXPECTED_PEAKS.items():
    if cid in cfg.components:
        check(f"{cid} peak = {want}", abs(cfg.peak(cid) - want) < 1e-9, f"实得 {cfg.peak(cid)}")

print("=== 10. ★ 跨文档对拍:toml 的 peak 必须等于方案书 §8.1.2 NUM-1 表的同名行 ===")
#   NUM-1 自称「v2.2 唯一权威表」。两处各写一份数字,迟早会漂 —— 事实上已经漂了一处:
#   8b@8k 在 toml 里是 5.0,而那是 NUM-1 同行的【权重】列,不是 peak 列(peak 是 5.31)。
_plan = (_REPO / "00-docs" / "PROJECT_PLAN_v3.0.md").read_text(encoding="utf-8")
_num1 = {}
for m in re.finditer(r"^\|\s*\**`([a-z0-9._@\-]+)`\**[^|]*\|(.+)$", _plan, re.M):
    cid, rest = m.group(1), m.group(2)
    cells = [c.strip() for c in rest.split("|")]
    if len(cells) < 4:
        continue
    # peak 是第 4 个单元格(权重 | KV | CUDA ctx | peak)。
    # ★ 该列里有「旧值划掉 → 新值」的写法,例如 `~~11.0~~ → **8.14** ★实测`、
    #   `~~**3.2**~~ → **实测 4.35**`。取箭头【后】的那个数,取前面的会拿到已作废的估算值
    #   (第一版就栽在这儿:comfyui.sdxl 读成 11.0、vlm.small 读成 3.2,报了两条假红)。
    cell = cells[3]
    if "→" in cell:
        cell = cell.rsplit("→", 1)[1]
    mm = re.search(r"(\d+\.?\d*)", cell.replace("~~", ""))
    if mm and cid not in _num1:
        _num1[cid] = float(mm.group(1))
_checked = 0
for cid, peak in sorted(cfg.components.items()):
    if cid in _num1:
        _checked += 1
        check(f"{cid}:toml {cfg.peak(cid)} == NUM-1 {_num1[cid]}",
              abs(cfg.peak(cid) - _num1[cid]) < 1e-9,
              f"toml={cfg.peak(cid)} NUM-1={_num1[cid]} —— 两处数字漂了,先定哪个对再改")
check("对拍确实覆盖到了组件(否则正则没匹配上,这一组是空转)", _checked >= 6, f"只对上 {_checked} 个")

print("=== 11. ★ 动态闸 fail-closed 必须【真的被执行过】(此前是恒真断言)===")
#   原第 5 组在本机总是走 else 分支(nvidia-smi 能跑),那条 fail-closed 从没被执行过。
v = evaluate(["llm.assistant.8b@16k"], cfg, free=None, skip_dynamic=False,
             ) if False else None
_saved = vram_gate.nvml_free_gib
try:
    vram_gate.nvml_free_gib = lambda: None          # 模拟 nvidia-smi 挂了
    v = vram_gate.evaluate(["llm.assistant.8b@16k"], cfg)
    check("NVML 读不到 → 拒绝(不是放行)", not v.ok)
    check("归因到 dynamic", v.gate == "dynamic", v.gate)
    check("消息说清是读不到,不是装不下", "读不到" in v.message)
    check("消息点名这是拒绝执行", "拒绝执行" in v.message)
finally:
    vram_gate.nvml_free_gib = _saved

print("=== 12. ★ 退出码三态:通过 0 / 被拒 1 / 闸自己坏了 2 ===")
#   原来只有 0/1,于是「闸崩了」与「闸判定为拒」不可分辨,而后者可以被 -Force 覆盖、前者不能。
check("EXIT_OK/REFUSED/BROKEN 三个常量存在且互不相等",
      len({vram_gate.EXIT_OK, vram_gate.EXIT_REFUSED, vram_gate.EXIT_BROKEN}) == 3)
# ★★★ 2026-08-06:这一条原来直接跑 main(),也就是**拿真实机显存当判据** ——
#   于是它只在"此刻显存够"的时候是绿的。栈一起来(llama-server 占掉 6 GiB,
#   实测 free 从 11.4 掉到 6.06),闸正确地判拒,而这条断言变红,
#   报出来的却是"退出码不对" —— 判据和它自称在测的东西不是一回事。
#   ★ 这就是本仓第 5 条坑:**会随环境漂移的断言**。第 11 组早就用注入解决过同款问题,
#     这一条漏了。⇒ 注入固定的 free,三态各钉一次。
_saved_main = vram_gate.nvml_free_gib
try:
    vram_gate.nvml_free_gib = lambda: 64.0          # 够装
    check("main() 通过 → 0", vram_gate.main(["llm.assistant.8b@16k"]) == vram_gate.EXIT_OK)
    vram_gate.nvml_free_gib = lambda: 1.0           # 装不下(动态闸)
    check("★ main() 被拒 → 1(可以被 -Force 覆盖的那一种)",
          vram_gate.main(["llm.assistant.8b@16k"]) == vram_gate.EXIT_REFUSED)
    vram_gate.nvml_free_gib = lambda: None          # 读不到 NVML
    # ★ 这里第一版我写成了 EXIT_BROKEN,**错的**:BROKEN 的定义是「闸没能跑起来」
    #   (配置坏了 / 解析失败 / 自身异常,见 vram_gate.py:424)。
    #   而"读不到 NVML"是闸**跑起来了并决定拒绝** —— fail-closed 的拒绝,归 1。
    #   两者的区别正是这三态存在的理由:REFUSED 能被 -Force 覆盖,BROKEN 不能。
    check("★★ 读不到 NVML → 1(这是**拒绝**,不是闸坏了 —— 闸跑起来了,它选择不放行)",
          vram_gate.main(["llm.assistant.8b@16k"]) == vram_gate.EXIT_REFUSED)
finally:
    vram_gate.nvml_free_gib = _saved_main
check("main() 超预算 → 1",
      vram_gate.main(["llm.assistant.30b-a3b@32k", "comfyui.sdxl"]) == vram_gate.EXIT_REFUSED)
_saved_load = vram_gate.load_config
try:
    def _boom():
        raise RuntimeError("配置坏了")
    vram_gate.load_config = _boom
    check("main() 闸自身异常 → 2(不是 1)",
          vram_gate.main(["llm.assistant.8b@16k"]) == vram_gate.EXIT_BROKEN)
finally:
    vram_gate.load_config = _saved_load

print("=== 13. ★★ 跨进程:干净 cp936 控制台下,通过与拒绝【两条路径都要打得出字】 ===")
#   ★ 这一条必须以【子进程】方式跑,并显式删掉 PYTHONIOENCODING / PYTHONUTF8 ——
#     开发用的 shell 往往注入 utf-8,进程内断言会恒真通过,那本身就是一条假断言。
_env = {k: val for k, val in os.environ.items() if k not in ("PYTHONIOENCODING", "PYTHONUTF8")}


def _run_cli(*args, env=None):
    r = subprocess.run([sys.executable, str(_HERE / "vram_gate.py"), *args],
                       capture_output=True, env=env or _env, cwd=str(_HERE), timeout=60)
    return r.returncode, r.stdout.decode("utf-8", "replace"), r.stderr.decode("utf-8", "replace")


# ★★ 通过路径必须【确定性】。原来它直接打实机 NVML,判定随桌面占用漂移:
#   2026-08-04 20:1x 桌面吃到 7.5 GiB,闸如实拒绝 → 这条断言无辜变红,挡下了一次无关的提交。
#   一条随环境漂移的门禁断言不是"偶尔红一下"那么轻 —— 它训练人去用 --no-verify,
#   而本项目已被这个模式咬过一次(D24:-Force 一路跳过三道闸)。
#   本文件开头第 4 行自己就写着「动态闸统一注入固定的 free 值,不依赖真实 GPU 状态」——
#   第 13 节违反的是它自己声明的纪律。
#
# ⇒ 修法:通过路径改走【跨进程引导】,在子进程里注入 free 再调 main()。
#   ★ 不给 CLI 加 --free 之类的旁路开关 —— 那种开关迟早被用来绕闸。
#   ★ 先试过"PATH 里放桩 nvidia-smi.cmd",不成立:Windows 的 CreateProcess **不执行 .cmd**,
#     `nvidia-smi` 仍解析到 System32 的真 exe。这条弯路记在这里,省得下一个人再走。
#   ★ 换来的代价说清楚:这条不再经过 `if __name__ == "__main__"` 那一行。
#     编码修复本身在 main() 里(sys.stdout.reconfigure),仍被完整执行;
#     而真正的 __main__ 入口由下面【拒绝路径】与【无参数列表模式】两条覆盖 ——
#     那两条的判定与 free 无关,本来就是确定性的。
_BOOT = (
    "import sys; sys.path.insert(0, r'{here}');"
    "import vram_gate;"
    "vram_gate.nvml_free_gib = lambda: 14.65;"
    "sys.exit(vram_gate.main(sys.argv[1:]))"
).format(here=_HERE)


def _run_cli_fixed_free(*args):
    r = subprocess.run([sys.executable, "-c", _BOOT, *args],
                       capture_output=True, env=_env, cwd=str(_HERE), timeout=60)
    return r.returncode, r.stdout.decode("utf-8", "replace"), r.stderr.decode("utf-8", "replace")


# ★ 元断言:注入要是没生效,这一节就悄悄退回打实机 —— 又变随机红/随机绿。
#   所以先钉住输出里的 free 正好是注入值。
_rc0, _out0, _err0 = _run_cli_fixed_free("llm.assistant.8b@16k")
check("★★ free 注入确实生效(否则这一节退回打实机,又变 flaky)",
      "14.65" in _out0, f"rc={_rc0} out={_out0[:200]} err={_err0[:160]}")

_rc, _out, _err = _run_cli_fixed_free("llm.assistant.8b@16k", "speech.lite")
check("通过路径:退出码 0", _rc == 0, f"实得 {_rc};stderr={_err[:120]}")
check("通过路径:stdout 非空(修复前这里是空的)", len(_out.strip()) > 0)
check("通过路径:三段预览齐全", all(s in _out for s in ("① 已选组件", "② 桌面预留", "③ 此刻可用")))
check("通过路径:判定符是 ASCII 的 [OK]", "[OK]" in _out, _out[:120])
check("通过路径:没有 UnicodeEncodeError", "UnicodeEncodeError" not in _err, _err[:160])

_rc2, _out2, _err2 = _run_cli("llm.assistant.30b-a3b@32k", "comfyui.sdxl")
check("拒绝路径:退出码 1", _rc2 == 1, f"实得 {_rc2}")
check("拒绝路径:stdout 非空", len(_out2.strip()) > 0)
check("拒绝路径:判定符 [XX] 且给了归因", "[XX]" in _out2 and "静态闸" in _out2)
check("拒绝路径:没有 UnicodeEncodeError", "UnicodeEncodeError" not in _err2)

_rc3, _out3, _err3 = _run_cli()
check("无参数(列表模式):退出码 0 且列得出组件", _rc3 == 0 and "vram_budget" in _out3)

print("=== 14. 预览与准入必须是同一段代码(§8.1 规则 18)===")
#   原 __main__ 采样两次 NVML:preview 里一次、exit 判定里一次 —— 中间桌面一变就能互相矛盾。
_v = vram_gate.evaluate(["llm.assistant.8b@16k"], cfg, free=9.0)
_txt = vram_gate.preview(["llm.assistant.8b@16k"], cfg, free=9.0, verdict=_v)
check("preview 接受注入的 verdict,不再自己重算", "[OK]" in _txt if _v.ok else "[XX]" in _txt)
check("preview 用的是注入的 free,不重新采样", "9.00" in _txt, _txt[:200])

print("=== 15. 预览不得静默吞掉未登记组件(否则预览与准入结论相反)===")
_txt2 = vram_gate.preview(["llm.assistant.8b@16k", "llm.bogus"], cfg, free=15.0)
check("未登记组件在预览里被显式标出", "llm.bogus" in _txt2, _txt2[:200])
check("同一输入下准入闸确实拒绝",
      not vram_gate.evaluate(["llm.assistant.8b@16k", "llm.bogus"], cfg, free=15.0).ok)

print("=== 16. start-stack 必须把退出码 2 与 1 分开处理,且 -Force 不能覆盖 2 ===")
_ss = (_REPO / "90-ops" / "start-stack.ps1").read_text(encoding="utf-8")
check("start-stack 读的是三态退出码", "$gateCode" in _ss and "-eq 2" in _ss)
_seg2 = _ss.split("-eq 2", 1)[1].split("elseif", 1)[0] if "-eq 2" in _ss else ""
check("退出码 2 的分支里【没有】 -Force 逃生口", "$Force" not in _seg2, _seg2[:160])
check("退出码 2 的文案与「被拒」不同", "没能跑起来" in _seg2)

# ══════════════════════════════════════════════════════════════════════
#  S4a · 三集合动态闸(2026-08-04 裁定)
#
#  判据关键:哪些显存**已经被 NVML free 反映了**。
#    loaded   已装载   → free 里已扣掉 ⇒ 再减一次 = 重复计
#    reserved 已批未装 → free 里还看得见 ⇒ 不减的话别人会把它再批一次(D37 ④ 的竞态)
#    incoming 本次新占 → 必须减
#  ⇒ free − incoming − Σpeak(reserved \ loaded \ 本次请求集) ≥ safety_margin
# ══════════════════════════════════════════════════════════════════════
print("=== 17. 三集合:向后兼容(不传 reserved 时行为逐字节不变)===")
_a = evaluate(["llm.assistant.8b@16k"], cfg, free=9.0)
_b = evaluate(["llm.assistant.8b@16k"], cfg, free=9.0, reserved=[])
check("不传 reserved 与传空表结论相同", _a.ok == _b.ok and _a.message == _b.message)
check("冷启动仍按 incoming 算", _a.ok)

print("=== 18. ★ 别人的预留会占掉名额(不减它 = D37 ④ 的双批竞态)===")
#   free=9.0;我要 8b@16k(5.92);别人预留了 speech.full(4.05) 但还没装。
#   9.0 − 5.92 − 4.05 = -0.97 < 0.8 ⇒ 必须拒。
_v = evaluate(["llm.assistant.8b@16k"], cfg, free=9.0, reserved=["speech.full"])
check("别处的预留会把我挡住", not _v.ok, _v.message[:80])
check("归因到 dynamic", _v.gate == "dynamic", _v.gate)
check("★ 文案点名这是【预留】而不是桌面占用", "预留" in _v.message and "speech.full" in _v.message,
      _v.message[:160])
check("★ 文案明说这一部分关程序没用(处境不同,处置也不同)",
      "关程序没用" in _v.message, _v.message[:200])
#   同样的 free,没有别人的预留时应当通过 —— 证明差的就是那 4.05
_v2 = evaluate(["llm.assistant.8b@16k"], cfg, free=9.0, reserved=[])
check("没有别人的预留时同一请求通过", _v2.ok)

print("=== 19. ★★ 双重扣减陷阱:已算进 incoming 的预留【不得】再减第二次 ===")
#   这是本片最容易写错、且写错就是"两个客户端双双获批"的那一处。
#   场景:我自己先预留了 8b@16k,现在真去装它。
#     正确:incoming 已含 5.92,reserved 里的同一项必须被差集排除 ⇒ 只减一次。
#     错误:再减一遍 ⇒ 5.92×2 = 11.84,本来装得下也会被误拒。
_v3 = evaluate(["llm.assistant.8b@16k"], cfg, free=7.0, reserved=["llm.assistant.8b@16k"])
check("自己的预留不被重复扣减(7.0 − 5.92 = 1.08 ≥ 0.8 ⇒ 通过)", _v3.ok,
      _v3.message[:160])
#   反证:若真被扣两遍,7.0 − 11.84 < 0 必拒 —— 上面那条通过即证明没有重复扣。
_v4 = evaluate(["llm.assistant.8b@16k"], cfg, free=7.0,
               reserved=["llm.assistant.8b@16k", "speech.lite"])
check("只有【不在本次请求集里】的那部分预留被减(speech.lite 2.07:7.0−5.92−2.07<0.8 ⇒ 拒)",
      not _v4.ok)

print("=== 20. ★ 已装载的组件不得因为也在 reserved 里而被减 ===")
#   loaded 的显存已经从 free 里扣过了;它若同时出现在 reserved(还没来得及清理),
#   差集必须把它排除,否则又是一次重复计。
_v5 = evaluate(["llm.assistant.8b@16k", "speech.lite"], cfg, free=3.0,
               resident=["llm.assistant.8b@16k"], reserved=["llm.assistant.8b@16k"])
#   incoming = speech.lite 2.07;others_reserved 应为 0(8b@16k 既 loaded 又在请求集里)
#   3.0 − 2.07 = 0.93 ≥ 0.8 ⇒ 通过
check("既已装载又在预留表里的组件不被重复扣减(3.0−2.07=0.93 ⇒ 通过)", _v5.ok,
      _v5.message[:160])

print("=== 21. 源码级:差集必须是【双重】的,不能只排除一边 ===")
#   只排除 loaded 会让"自己的预留"被扣两遍;只排除请求集会让"已装载的预留"被扣两遍。
import inspect as _inspect
_ev = _inspect.getsource(vram_gate.evaluate)
check("差集排除了 loaded(resident)", "c not in resident" in _ev)
check("差集排除了本次请求集(component_ids)", "c not in component_ids" in _ev)
check("两者在同一个推导式里(而不是分两步、留下中间态)",
      "if c not in resident and c not in component_ids" in _ev,
      "分两步写容易在维护时丢掉一半")

# ══════════════════════════════════════════════════════════════════════
#  S6 · 硬件指纹(GPU 半边)+ B10④ 预算诊断
#
#  ★ P4-S6 之前:load_config 从不读 raw['calibration'](Config 里连字段都没有),
#    而 toml 注释写着「P4 起由 vram_gate 在启动期比对」。budget.calibrated 确实被读了,
#    但只在 preview 第②段当标签打印,**不门禁任何东西** ——
#    读了不用比不读更危险:它让人以为标定状态已经在把关。
# ══════════════════════════════════════════════════════════════════════
print("=== 22. [calibration] 真的被读进来了(此前整段被静默丢弃)===")
_cal = cfg.calibration
check("Config 有 calibration 字段", hasattr(cfg, "calibration"))
check("gpu_name 读到了", _cal.gpu_name == "NVIDIA GeForce RTX 5080", _cal.gpu_name)
check("gpu_total_vram 读到了", abs(_cal.gpu_total_vram - 15.92) < 1e-9)
check("driver 读到了", _cal.driver == "610.62", _cal.driver)
check("measured_at 读到了", _cal.measured_at == "2026-07-27", _cal.measured_at)

print("=== 23. ★★ 口径必须与写入端同源(nvidia-smi,不是 WMI)===")
#   实测同一张卡:nvidia-smi 报 610.62,WMI 报 32.0.16.1062。
#   若比对端改用 WMI,会得到【永远不相等 → 永远拒绝启动】——
#   一个 fail-closed 机制最难查的失效方式(它看起来像"硬件真的变了")。
_probe_src = _inspect.getsource(vram_gate.probe_gpu_identity)
check("★ 探测走 nvidia-smi", "nvidia-smi" in _probe_src)
check("★ 不走 WMI / CIM / Win32_VideoController",
      not any(w in _probe_src for w in ("Win32_VideoController", "Get-CimInstance", "wmi", "WMI")))
check("查询的字段与 toml 记的对得上",
      all(k in _probe_src for k in ("name", "memory.total", "driver_version", "uuid", "vbios_version")))

print("=== 24. 标定一致时通过;不一致时【拒绝并说清要重测什么】===")
_ok = vram_gate.check_calibration(cfg)
check("本机当前标定一致", _ok.ok, _ok.message[:120])
check("归因到 calibration", _ok.gate == "calibration")

_fake = {"name": "NVIDIA GeForce RTX 4090", "total_mib": "24564",
         "driver": "610.62", "uuid": _cal.gpu_uuid, "vbios": _cal.vbios}
_bad = vram_gate.check_calibration(cfg, probe=_fake)
check("★ 换了卡 → 拒绝", not _bad.ok)
check("消息点名型号变了", "型号" in _bad.message and "4090" in _bad.message)
check("消息点名总显存变了", "总显存" in _bad.message)
check("★ 说清要重测的是【组件 peak 里与硬件相关的部分】(B10②)",
      "CUDA context" in _bad.message or "P1-A1" in _bad.message)
check("★ 说清 desktop_floor 由显示配置决定、换卡不换显示器时可移植(B10②)",
      "desktop_floor" in _bad.message and "显示配置" in _bad.message)
check("★ 警告不要只改指纹放行", "不要只改指纹" in _bad.message)

_uuid_swap = {"name": _cal.gpu_name, "total_mib": "16303", "driver": _cal.driver,
              "uuid": "GPU-ffffffff-0000-0000-0000-000000000000", "vbios": _cal.vbios}
_bad2 = vram_gate.check_calibration(cfg, probe=_uuid_swap)
check("★ 同型号换另一张卡(仅 UUID 不同)→ 也拒绝", not _bad2.ok)
check("消息说明这是换了同型号的另一张卡", "同型号" in _bad2.message)

_no_probe = vram_gate.check_calibration(cfg, probe=None) if False else None
check("读不到 GPU 身份 → fail-closed(源码里有这条分支)",
      "读不到 GPU 身份" in _inspect.getsource(vram_gate.check_calibration))
check("★ 缺基准 ≠ 一致:没有 gpu_name 时拒绝",
      "无从比对" in _inspect.getsource(vram_gate.check_calibration))

print("=== 25. ★ UUID/VBIOS 是补录的,必须诚实标注且【只有存了值才比】 ===")
check("uuid_recorded_at 存在且晚于 measured_at",
      _cal.uuid_recorded_at == "2026-08-04" and _cal.uuid_recorded_at > _cal.measured_at,
      f"{_cal.uuid_recorded_at} vs {_cal.measured_at}")
_cc = _inspect.getsource(vram_gate.check_calibration)
check("★ 只有 toml 里存了值才参与比对(不能拿今天的值反推当年测的是哪张卡)",
      "if cal.gpu_uuid and" in _cc and "if cal.vbios and" in _cc)

print("=== 26. ★★ 显示指纹:明写【未标定】,且【不得】填今天的配置 ===")
check("display_calibrated 为 false", _cal.display_calibrated is False)
_toml_txt = (_REPO / "config" / "vram-budget.toml").read_text(encoding="utf-8")
check("★ toml 里写明不可回填", "不可回填" in _toml_txt)
check("★ toml 里明写必须拒绝『读今天的配置填进去』", "追认" in _toml_txt and "捏造" in _toml_txt)
check("★ 没有偷偷填入分辨率(那会把今天的配置追认为 A7 的测量条件)",
      "2560x1440" not in _toml_txt.split("display_note")[0].split("display_calibrated")[0]
      or "故意不填" in _toml_txt)
check("提到运行期改显示配置不被检测(不让人以为有运行期强制)",
      "运行期" in _toml_txt)

print("=== 27. B10④ 预算诊断:报「路线不成立」而不是逐个静默拒绝 ===")
check("当前预算正常,不报警", vram_gate.diagnose_budget(cfg) is None)
import copy as _copy
_small = _copy.deepcopy(cfg)
_small.budget.total_vram = 8.0          # 换成 8GB 卡:8 − 6.6 − 0.8 = 0.60
_msg = vram_gate.diagnose_budget(_small)
check("★ 8GB 卡 → 报「GPU 路线不成立」", _msg is not None and "不成立" in _msg, str(_msg)[:100])
check("消息给出具体数字(预算 vs 最小组件)", _msg and "0.60" in _msg and "GiB" in _msg)
check("★ 明说这不是『选错了组合』", _msg and "选错了组合" in _msg)
check("给出三条真实出路(换卡/降预留/只跑 CPU 档)",
      _msg and "换卡" in _msg and "CPU" in _msg)

print("=== 28. ★★ 标定不符 → 退出码 2(不可被 -Force 覆盖),不是 1 ===")
#   1 是「闸看了你的申请,说不行」—— 可以被 -Force 覆盖(你明知故犯);
#   2 是「闸的基准本身不作数」—— 硬件换了还沿用旧数字,强行放行得到的每个判定都无意义。
_main_src = _inspect.getsource(vram_gate.main)
check("main 里有标定门禁", "check_calibration" in _main_src)
check("★ 标定不符返回 EXIT_BROKEN(2)而非 EXIT_REFUSED(1)",
      "_cal.ok" in _main_src and "return EXIT_BROKEN" in _main_src.split("_cal.ok")[1][:200])
check("B10④ 诊断也在 main 里且同样返回 2",
      "diagnose_budget" in _main_src and "_diag" in _main_src)
check("门禁排在列表模式【之前】(基准不作数时,连列表都不该给出)",
      _main_src.index("check_calibration") < _main_src.index("if not args:"))

print(f"\n=== {_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
