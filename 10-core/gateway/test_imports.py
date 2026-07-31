"""冒烟:网关的每个模块都必须 import 得动。纯 assert,无 pytest:python test_imports.py

★ 这个文件为什么存在(2026-07-31):
  `e4_egress.py` 从未入库,而 `gateway.py` 顶上有一句 `import e4_egress` ——
  于是整个网关起不来、6 个测试文件里 4 个连收集阶段都过不去、start-stack.ps1 必失败。
  这个状态存活了一整天没被任何东西发现,因为**没有一条测试验证过"网关能被导入"**:
  剩下能跑的两个测试文件恰好都不 import gateway。

  一整类"整模块缺失"的故障,靠业务测试是抓不到的 —— 业务测试自己也一起崩了,
  而崩掉的测试在"最后一行写着 PASS 数"这种土办法下,看起来只是没输出。
  所以单独留这一条:它只做一件事,而且是所有别的测试成立的前提。
"""

import importlib
import pathlib
import sys

HERE = pathlib.Path(__file__).parent
sys.path.insert(0, str(HERE))

_pass = _fail = 0


def check(name, cond, extra=""):
    global _pass, _fail
    if cond:
        _pass += 1
    else:
        _fail += 1
        print(f"  FAIL  {name}{(' — ' + extra) if extra else ''}")


# 扫目录,不写死清单 —— 写死的清单迟早会漏掉新加的模块,那正是这次要防的事。
mods = sorted(p.stem for p in HERE.glob("*.py")
              if not p.stem.startswith("test_") and p.stem != "__init__")

check("网关目录里有模块", len(mods) > 0)
for m in mods:
    try:
        importlib.import_module(m)
        check(f"import {m}", True)
    except Exception as ex:      # noqa: BLE001
        check(f"import {m}", False, f"{type(ex).__name__}: {ex}")

print(f"=== 模块冒烟:{_pass} PASS · {_fail} FAIL ===")
raise SystemExit(1 if _fail else 0)
