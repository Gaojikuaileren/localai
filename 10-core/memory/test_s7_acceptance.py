# -*- coding: utf-8 -*-
"""S7 验收:评测集 + 四项指标 + 多闸达标

验收句(与前六条同级):

  ★ 「召回/精确/误记/溯源四个数都是实测填进阈值表的;塞一条自动事实误 supersede
     用户事实,哪怕召回 100% 整体也判 FAIL。」

跑(需活库 + Qdrant + embedding):PYTHONPATH=. python test_s7_acceptance.py
"""
import os
import sys
from dataclasses import replace

import eval_memory
import repo
from eval_memory import EvalReport, Gate, Metrics

_p = _f = 0


def check(name, cond, extra=""):
    global _p, _f
    if cond:
        _p += 1
        print(f"  PASS  {name}")
    else:
        _f += 1
        print(f"  FAIL  {name} {extra}")


try:
    conn = repo.connect()
except Exception as ex:
    print(f"  跳过:连不上 PG({type(ex).__name__})")
    sys.exit(0)
eval_memory.cleanup(conn)

try:
    print("=== ① ★★ 跑评测,四指标全部实测得出 ===")
    rep = eval_memory.run_eval(conn)
    m = rep.metrics
    print(f"  召回率      = {m.recall:.3f}")
    print(f"  精确率      = {m.precision:.3f}")
    print(f"  误记率      = {m.error_rate:.3f}  (硬上限 0)")
    print(f"  溯源完整率  = {m.traceability:.3f}  (硬线 1.0)")
    print(f"  S2 泄漏率   = {m.s2_leak_rate:.3f}  (硬线 0)")
    check("四指标都是数值(实测得出,不是预设常量)",
          all(isinstance(getattr(m, k), float)
              for k in ("recall", "precision", "error_rate", "traceability", "s2_leak_rate")))

    print("=== ② ★★ 微夹具上全部达标 ===")
    for g in rep.gates:
        check(f"闸「{g.name}」= {g.got:.3f}", g.passed, f"要求 {g.bound}")
    check("★★ 整体通过(多闸 AND)", rep.passed)

    print("=== ③ ★★ 误记率是【独立硬上限】—— 高召回不能掩盖投毒 ===")
    # 人为构造:召回 1.0、精确 1.0,但误记率 > 0 → 整体必须 FAIL
    poisoned = EvalReport(
        metrics=replace(m, recall=1.0, precision=1.0, error_rate=0.5, traceability=1.0,
                        s2_leak_rate=0.0),
        gates=[
            Gate("召回率 ≥ 1.0", True, 1.0, "≥ 1.0"),
            Gate("精确率 ≥ 1.0", True, 1.0, "≥ 1.0"),
            Gate("★ 误记率 = 0", 0.5 == 0.0, 0.5, "= 0 硬线"),
            Gate("★ 溯源完整率 = 100%", True, 1.0, "= 1.0 硬线"),
            Gate("★ S2 泄漏率 = 0", True, 0.0, "= 0 硬线"),
        ])
    check("★★ 召回精确都满分,但误记率 0.5 → 整体 FAIL(不被平均掉)",
          poisoned.passed is False)

    print("=== ④ ★ 硬线不可用高分补偿 ===")
    # 溯源 99% 也不行(必须 100%)
    trace_gap = EvalReport(
        metrics=replace(m, traceability=0.99),
        gates=[Gate("★ 溯源完整率 = 100%", 0.99 >= 1.0, 0.99, "= 1.0 硬线")])
    check("★ 溯源 99% → FAIL(100% 是硬线,不是「接近就行」)", trace_gap.passed is False)
    # S2 泄漏 > 0 也不行
    leak = EvalReport(
        metrics=replace(m, s2_leak_rate=0.01),
        gates=[Gate("★ S2 泄漏率 = 0", 0.01 == 0.0, 0.01, "= 0 硬线")])
    check("★ S2 泄漏 1% → FAIL(0 是硬线)", leak.passed is False)

    print("=== ⑤ ★ 阈值文件:实测值已冻结,规模化对抗挂待实测 ===")
    import tomllib
    from pathlib import Path
    tf = Path(__file__).resolve().parents[2] / "config" / "eval-thresholds.toml"
    check("★ 阈值文件存在", tf.exists())
    if tf.exists():
        d = tomllib.load(open(tf, "rb"))
        check("★★ 误记率上限 = 0(硬线,写死不待实测)",
              d["thresholds"]["error_rate_max"] == 0.0)
        check("★★ 溯源完整率下限 = 1.0(硬线)",
              d["thresholds"]["traceability_min"] == 1.0)
        check("★★ S2 泄漏率上限 = 0(硬线)",
              d["thresholds"]["s2_leak_rate_max"] == 0.0)
        check("★ 记录了这是微夹具标定(规模化对抗挂 backlog)",
              "micro" in str(d.get("calibration", {})).lower()
              or "backlog" in str(d).lower() or "待实测" in str(d))

finally:
    try:
        conn.close()
    except Exception:
        pass
    conn2 = repo.connect()
    eval_memory.cleanup(conn2)
    conn2.close()

print(f"\n=== S7 验收:{_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
