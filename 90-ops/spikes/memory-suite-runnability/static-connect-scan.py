"""记忆套件「能不能安全地跑」的**静态**判定 —— 一次性勘察,不进门禁。

服务对象:00-docs/decision-packets/memory-suite-runnability-2026-08-06.md

★★ 本脚本的全部意义在于:**它自己不跑任何被测脚本。**
   `10-core/memory/test_*.py` 是「import 即执行」的形态 —— 真跑起来若连上库,
   可能对**真实记忆库**产生写入。所以判据顺序必须是:
     ① 先静态读 import 与模块级语句,确认没有任何连库路径;
     ② 只有确认了的才允许跑;
     ③ 拿不准 ⇒ **不跑**,并如实写「未跑,因为无法静态排除写入」。
   「跑不了」和「跑过了」必须长得不一样,而「没敢跑」也是一种如实。

做法:在 10-core/memory 里建一张**模块级 import 图**,
先标出「本身就含连库/发网络请求代码」的模块(种子),再沿 import 边传播,
最后按「测试文件是否(传递地)依赖任一连库模块」给出判定。

★ 判定刻意偏保守:传递依赖上只要碰到一个连库模块就判 NEEDS-BACKEND,
  哪怕那条路径在运行时未必被走到 —— 宁可少跑,不可误写生产库。

跑:python static-connect-scan.py
"""
import ast
import sys
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# 路径运行期推导 —— 代码里不写死盘符(§11.1 路径契约)
REPO = Path(__file__).resolve().parents[3]
MEM = REPO / "10-core" / "memory"

# ── 种子判据:这些调用意味着「会连外部后端」 ──────────────────────────
#  psycopg.*  → PostgreSQL
#  httpx / requests / urllib → Qdrant / embedding 服务
CONNECT_MARKERS = (
    "psycopg.connect", "psycopg.Connection", "psycopg.AsyncConnection",
    "httpx.", "requests.", "urllib.request", "socket.",
)
CONNECT_IMPORTS = {"psycopg", "httpx", "requests", "urllib", "socket"}


def module_name(p: Path) -> str:
    return p.stem


def parse(p: Path):
    try:
        return ast.parse(p.read_text(encoding="utf-8"), filename=str(p))
    except Exception as e:
        print(f"  ! 解析失败 {p.name}: {e}")
        return None


def local_imports(tree, known: set) -> set:
    """只取【本目录内】的 import(记忆模块之间的边)。"""
    out = set()
    for n in ast.walk(tree):
        if isinstance(n, ast.Import):
            for a in n.names:
                base = a.name.split(".")[0]
                if base in known:
                    out.add(base)
        elif isinstance(n, ast.ImportFrom):
            if n.module:
                base = n.module.split(".")[0]
                if base in known:
                    out.add(base)
            # from . import x / from tainted import y 都会落在上面
    return out


def external_connect_imports(tree) -> set:
    out = set()
    for n in ast.walk(tree):
        if isinstance(n, ast.Import):
            for a in n.names:
                if a.name.split(".")[0] in CONNECT_IMPORTS:
                    out.add(a.name.split(".")[0])
        elif isinstance(n, ast.ImportFrom):
            if n.module and n.module.split(".")[0] in CONNECT_IMPORTS:
                out.add(n.module.split(".")[0])
    return out


def has_connect_call(src: str) -> list:
    return [m for m in CONNECT_MARKERS if m in src]


def module_level_call_count(tree) -> int:
    """模块级(不在 def/class 里)的语句里有多少个函数调用 —— 「import 即执行」的量度。"""
    cnt = 0
    for node in tree.body:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef,
                             ast.Import, ast.ImportFrom)):
            continue
        for sub in ast.walk(node):
            if isinstance(sub, ast.Call):
                cnt += 1
    return cnt


# ── 扫 ────────────────────────────────────────────────────────────────
py_files = sorted(MEM.glob("*.py"))
known = {module_name(p) for p in py_files}

info = {}
for p in py_files:
    tree = parse(p)
    if tree is None:
        continue
    src = p.read_text(encoding="utf-8")
    info[module_name(p)] = {
        "path": p,
        "local": local_imports(tree, known),
        "ext": external_connect_imports(tree),
        "markers": has_connect_call(src),
        "mlcalls": module_level_call_count(tree),
        "is_test": p.name.startswith("test_"),
    }

# 种子:自己就含连库代码的模块
seeds = {m for m, d in info.items() if d["markers"] or d["ext"]}

print("=" * 78)
print("① 种子:模块自身就含连库/网络代码")
print("=" * 78)
for m in sorted(seeds):
    d = info[m]
    ext = ",".join(sorted(d["ext"])) or "-"
    print(f"  {m:<22} ext={ext:<24} markers={d['markers'][:3]}")

# 传播:谁(传递地)依赖了种子
def taints(m, stack=()):
    """返回 m 传递依赖到的种子集合。"""
    if m in stack:
        return set()
    d = info.get(m)
    if d is None:
        return set()
    got = set()
    if m in seeds:
        got.add(m)
    for dep in d["local"]:
        got |= taints(dep, stack + (m,))
    return got

print()
print("=" * 78)
print("② 测试文件判定(保守:传递依赖上碰到任一连库模块即 NEEDS-BACKEND)")
print("=" * 78)
print(f"  {'文件':<32}{'判定':<18}{'模块级调用':<12}{'为什么'}")
pure, needs = [], []
for m in sorted(info):
    d = info[m]
    if not d["is_test"]:
        continue
    t = taints(m)
    if t:
        needs.append((m, sorted(t)))
        why = "经 " + " / ".join(sorted(t))
        print(f"  {d['path'].name:<32}{'NEEDS-BACKEND':<18}{d['mlcalls']:<12}{why}")
    else:
        pure.append(m)
        print(f"  {d['path'].name:<32}{'PURE(可跑)':<18}{d['mlcalls']:<12}只依赖纯模块")

print()
print("=" * 78)
print("③ 小结")
print("=" * 78)
print(f"  测试文件总数        : {len(pure) + len(needs)}")
print(f"  静态判定为 PURE     : {len(pure)}  → {[info[m]['path'].name for m in pure]}")
print(f"  静态判定 NEEDS-BACKEND: {len(needs)}")
print()
print("  ★ PURE 的含义:**静态上**找不到任何连库路径 ⇒ 允许跑。")
print("    NEEDS-BACKEND 的含义:**不许跑**(可能写真实记忆库),不是「跑了会失败」。")
print("    ⇒ 这两类在报告里必须分开写:一类有数字,一类写「未跑,因为无法静态排除写入」。")
print()
print("  ★ 本脚本没有执行任何被测脚本 —— 它只 ast.parse。")
