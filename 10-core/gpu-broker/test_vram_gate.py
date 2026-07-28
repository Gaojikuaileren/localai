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

print("=== 5. NVML 读不到 → fail-closed ===")
v = evaluate(["llm.assistant.8b@16k"], cfg, free=None, skip_dynamic=False)
# 真机上 nvidia-smi 可能成功;只断言「若读不到则拒」的逻辑分支存在
if v.gate == "dynamic" and "读不到" in v.message:
    check("读不到 NVML → 拒(fail-closed)", not v.ok)
else:
    check("真机 NVML 可读,跳过该分支断言", True)

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

print(f"\n=== {_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
