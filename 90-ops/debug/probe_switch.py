r"""模型切换实测 —— 逐组件装/卸,量耗时与显存,验三道闸与不变式。

跑:  python 90-ops\debug\probe_switch.py            # 只看不动(默认)
     python 90-ops\debug\probe_switch.py --write    # ★ 真的起停进程

★★★ 稳定性三条(用户要求 2026-08-05:「debug 工具一定要稳定简单方便,
   不然到时候 debug 工具出问题了排查会很麻烦」):
  ① **零项目依赖优先** —— 能走 HTTP 就不 import 项目代码。
     项目代码坏了的时候,工具还得能跑 —— 那正是最需要它的时刻。
  ② **逐项隔离** —— 一个组件探挂了不影响其余,失败就地记下继续。
  ③ **「工具坏了」与「系统坏了」分开** —— 退出码 2 vs 1。

★ 默认**只读**:不加 --write 一个进程都不起,只把「现在是什么样、闸会怎么判」列出来。
"""
from __future__ import annotations

import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import List, Optional

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:                                            # noqa: BLE001
    pass

REPO = Path(__file__).resolve().parents[2]
GW = "http://127.0.0.1:8080"
WRITE = "--write" in sys.argv
_tool_broke = False


def http(path: str, body=None, timeout=20.0):
    """★ 返回 (status, json_or_text)。永不抛 —— 探测器不该因为目标坏了而崩。"""
    try:
        req = urllib.request.Request(
            GW + path,
            data=json.dumps(body).encode() if body is not None else None,
            headers={"Content-Type": "application/json"} if body is not None else {})
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw = r.read().decode("utf-8", "replace")
            try:
                return r.status, json.loads(raw)
            except Exception:                                # noqa: BLE001
                return r.status, raw
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", "replace")
        try:
            return e.code, json.loads(raw)
        except Exception:                                    # noqa: BLE001
            return e.code, raw
    except Exception as e:                                   # noqa: BLE001
        return None, f"{type(e).__name__}: {e}"


def vram() -> Optional[float]:
    try:
        r = subprocess.run(["nvidia-smi", "--query-gpu=memory.free", "--format=csv,noheader,nounits"],
                           capture_output=True, text=True, timeout=8, check=True)
        return int(r.stdout.strip()) / 1024.0
    except Exception:                                        # noqa: BLE001
        return None


def main() -> int:
    global _tool_broke
    print("=" * 78)
    print(f"  模型切换实测   {'★★ --write:会真的起停进程' if WRITE else '只读(加 --write 才动)'}")
    print("=" * 78)

    st, snap = http("/v1/gpu/snapshot")
    if st != 200:
        print(f"  ✘ 网关不可达或拒绝({st}):{str(snap)[:90]}")
        print("    → 先跑 90-ops\\debug\\doctor.py 看是哪一环")
        return 1
    st2, cat = http("/v1/gpu/components")
    if st2 != 200:
        print(f"  ✘ 取不到组件目录({st2})")
        return 1

    comps = cat["components"]
    budget = cat["budget"]
    free0 = vram()
    print(f"  当前:state={snap['state']} committed={snap['committed']} "
          f"actual={snap.get('sets',{}).get('actual_resident')}")
    print(f"  预算:vram_budget={budget['vram_budget']} · 实测 free={free0}")
    print()

    # ── ① 闸会怎么判(纯读,把每个组件单独申请的结果列出来)──
    print("── ① 逐组件:单独申请时闸怎么判 " + "─" * 34)
    print(f"  {'组件':<28}{'peak':>7}  {'静态':<6}{'此刻':<6} 说明")
    for c in comps:
        peak = c["peak_gib"]
        static_ok = peak <= budget["vram_budget"]
        dyn_ok = (free0 is not None
                  and free0 - peak >= (budget.get("safety_margin") or 0.8))
        print(f"  {c['id']:<28}{peak:>7.2f}  "
              f"{'✔' if static_ok else '✘超预算':<6}"
              f"{'✔' if dyn_ok else '✘装不下':<6} "
              f"{'可启动' if c.get('kind') == 'llm' else '★ 启动方式未验证(装载器会拒)'}")

    if not WRITE:
        print("\n  (只读模式到此为止。加 --write 会真的装一次再卸掉)")
        return 0

    # ── ② 真的装一次再卸(★ 只挑一个能装下的 llm)──
    print("\n── ② 真装真卸 " + "─" * 50)
    target = next((c["id"] for c in comps
                   if c.get("kind") == "llm" and c["peak_gib"] <= budget["vram_budget"]
                   and free0 and free0 - c["peak_gib"] >= 0.8), None)
    if target is None:
        print("  ! 没有能装下的 llm 组件 —— 跳过(不是错误:显存可能正被别的程序占着)")
        return 0

    gen = snap["generation"]
    t0 = time.time()
    st3, res = http("/v1/gpu/intended",
                    {"if_generation": gen, "components": [target]}, timeout=240)
    dt = time.time() - t0
    free1 = vram()
    ok = st3 == 200
    print(f"  装 {target}:HTTP {st3} · {dt:.1f}s · free {free0:.2f} → "
          f"{free1:.2f}(差 {(free0 - free1) if free1 else 0:.2f})")
    if not ok:
        print(f"    理由:{json.dumps(res, ensure_ascii=False)[:180]}")

    st4, snap2 = http("/v1/gpu/snapshot")
    inv = {i["invariant"]: i for i in (snap2.get("invariants") or [])}
    print(f"  不变式:" + " · ".join(
        f"{k}={'✔' if v['holds'] else '✘'}({v['confidence']})" for k, v in sorted(inv.items())))
    bad = [k for k, v in inv.items() if not v["holds"]]
    if bad:
        print(f"    ★ **违反**:{bad} —— 账本与现实分家")

    # 卸回去(★ 恢复原状是这个工具的责任:它动了状态就得还回来)
    gen2 = snap2["generation"]
    t1 = time.time()
    st5, _ = http("/v1/gpu/intended",
                  {"if_generation": gen2, "components": snap["committed"]}, timeout=240)
    time.sleep(2)
    free2 = vram()
    print(f"  卸回原状:HTTP {st5} · {time.time()-t1:.1f}s · free → {free2:.2f}"
          f"{'  ✔ 回收了' if free2 and free0 and free2 >= free0 - 0.3 else '  ★ **没回收干净**'}")
    return 1 if (bad or not ok) else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
    except Exception as e:                                   # noqa: BLE001
        # ★ 退出码 2 = **工具自己坏了**,不是系统坏了。两者的下一步完全不同。
        print(f"\n  ? 探测器自己出错:{type(e).__name__}: {e}")
        print("    → 这是**工具**的问题。修 probe_switch.py,别去查系统")
        sys.exit(2)
