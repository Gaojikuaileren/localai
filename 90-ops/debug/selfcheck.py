r"""★ 调试工具自己的自检 —— 「工具坏了排查会更麻烦」的直接对策。

跑:  python 90-ops\debug\selfcheck.py

用户要求(2026-08-05):
> 「debug 工具一定要稳定简单方便,不然到时候 debug 工具出问题了排查会很麻烦」

★★★ 这条担心是对的,而且它有个**特别阴的形态**:
   工具不是"崩掉"(那还好,一眼看得见),而是**在系统坏了的时候报一切正常** ——
   于是你顺着工具的结论去别处找,而问题就在它刚说"✔"的那一环。

⇒ 本文件验四件事,每件都不需要系统在跑:
  ① **每个工具都能被导入且不崩**(哪怕系统全停);
  ② **零项目依赖** —— 工具不 import 项目代码。项目坏了的时候工具还得能跑,
     那正是最需要它的时刻;
  ③ **退出码语义** —— 1 = 系统有问题,2 = 工具自己坏了。混在一起就等于没分;
  ④ **只读的那两个真的只读** —— 源码里不得出现写文件/起进程的动作。
"""
from __future__ import annotations

import ast
import io
import re
import sys
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:                                            # noqa: BLE001
    pass

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]

TOOLS = ["doctor.py", "scan_fake.py", "probe_switch.py", "probe_sync.py"]
READ_ONLY = ["doctor.py", "scan_fake.py"]      # 这两个承诺一个字节都不写

_p = _f = 0


def check(name: str, cond: bool, extra: str = "") -> None:
    global _p, _f
    if cond:
        _p += 1
    else:
        _f += 1
        print(f"  ✘ {name}" + (f"   {extra}" if extra else ""))


#  ── 写盘动作识别器(下面第 ④ 组用它,第 ⑦ 组反过来验它) ──
_UNAMBIGUOUS = ("write_text", "write_bytes", "Popen", "mkdir", "touch",
                "unlink", "rmtree", "makedirs", "remove")


def writes_in(tree: ast.AST) -> list:
    """列出这棵语法树里所有【会写盘/起进程】的调用。"""
    out = []
    for n in ast.walk(tree):
        if not isinstance(n, ast.Call):
            continue
        fn = n.func
        nm = getattr(fn, "attr", None) or getattr(fn, "id", None)
        if nm in _UNAMBIGUOUS:
            out.append(nm)                        # 这些名字只有一个意思,直接算
        elif nm in ("rename", "replace"):
            # ★★ `os.replace(a,b)` 会写盘;`"x".replace(a,b)` 一个字节都不写 ——
            #    同名不同物。第一版只看方法名,于是 scan_fake 里那句
            #    `str(p.relative_to(REPO)).replace("\\", "/")` 被报成"违反只读"。
            #  ★ 但**不能**因此把 replace 整条删掉:那是 fail-open ——
            #    真有人写 `p.replace(q)`(Path 的原子改名)时就没人拦了。
            #    ⇒ 只在**能认出接收者是字符串**时才放行,认不出一律算写。
            #      (误报的代价 = 多看一眼;漏报的代价 = 承诺失效。不对称,所以偏严。)
            recv = getattr(fn, "value", None)
            is_str = (
                (isinstance(recv, ast.Constant) and isinstance(recv.value, str))
                or isinstance(recv, ast.JoinedStr)                     # f"..."
                or (isinstance(recv, ast.Call)
                    and getattr(recv.func, "id", None) == "str")       # str(...)
            )
            if not is_str:
                out.append(nm)
        elif nm == "open" and len(n.args) > 1:
            m = n.args[1]
            if isinstance(m, ast.Constant) and any(c in str(m.value) for c in "wax+"):
                out.append("open(w)")
    return out


print("=" * 78)
print("  调试工具自检   ★ 不需要系统在跑 —— 这正是重点")
print("=" * 78)

# ── ① 每个工具语法正确、能被解析 ──
for t in TOOLS:
    p = HERE / t
    check(f"{t} 存在", p.exists())
    if not p.exists():
        continue
    src = p.read_text(encoding="utf-8")
    try:
        tree = ast.parse(src)
        check(f"{t} 语法正确", True)
    except SyntaxError as e:
        check(f"{t} 语法正确", False, f"{e}")
        continue

    # ── ② 零项目依赖:不 import 项目代码 ──
    proj_mods = {"gateway", "gpu_broker", "gpu_policy", "sync_store", "sync_policy",
                 "vram_gate", "model_loader", "e1_detector", "caller_identity"}
    imported = set()
    for n in ast.walk(tree):
        if isinstance(n, ast.Import):
            imported |= {a.name.split(".")[0] for a in n.names}
        elif isinstance(n, ast.ImportFrom) and n.module:
            imported.add(n.module.split(".")[0])
    check(f"★★ {t} 不 import 项目代码(项目坏了工具还得能跑)",
          not (imported & proj_mods), f"import 了 {sorted(imported & proj_mods)}")

    # ── ③ 退出码语义:1 = 系统坏了 / 2 = 工具坏了 ──
    if t != "selfcheck.py":
        has2 = "sys.exit(2)" in src or "return 2" in src
        check(f"★ {t} 区分「工具自己坏了」(退出码 2)", has2,
              "混成一个退出码 = 你分不清该修工具还是修系统")

    # ── ④ 只读的那两个真的只读 ──
    if t in READ_ONLY:
        writes = writes_in(tree)
        check(f"★★ {t} 承诺只读 —— 源码里没有写文件/起进程的动作",
              not writes, f"出现了 {sorted(set(writes))}")

# ── ⑤ 一键移除:生产代码不得引用 debug 目录 ──
refs = []
for d in ("10-core", "20-client-win"):
    for p in (REPO / d).rglob("*"):
        if p.suffix.lower() not in (".py", ".cs", ".ps1") or "bin" in p.parts or "obj" in p.parts:
            continue
        try:
            s = p.read_text(encoding="utf-8", errors="replace")
        except Exception:                                    # noqa: BLE001
            continue
        if re.search(r"90-ops[/\\]debug|debug[/\\](doctor|scan_fake|probe_)", s):
            refs.append(str(p.relative_to(REPO)))
check("★★★ 生产代码**不引用** 90-ops/debug —— 「一键移除」才成立",
      not refs, f"引用了:{refs}")

# ── ⑥ README 说清怎么移除 ──
rd = HERE / "README.md"
check("README 在", rd.exists())
if rd.exists():
    s = rd.read_text(encoding="utf-8")
    check("README 写明一键移除的命令", "git rm -r 90-ops/debug" in s)
    check("★ 并说明为什么这条要靠断言而不是自觉", "顺手被生产代码用一下" in s)

# ── ⑦ ★★★ 反过来验第 ④ 组的识别器:两个分支都必须钉住 ──
#   第 ④ 组现在是**全绿**的。但"全绿"有两种成因:
#     (a) 两个工具真的没写盘;
#     (b) 识别器认不出写盘动作了。
#   这两种在终端上长得**一模一样** —— 而 (b) 恰恰是这套工具存在的理由。
#   ⇒ 喂它一段**确定会写盘**的代码,它必须报;喂一段**确定不写**的,它必须不报。
#     只钉一边等于没钉:只验"能报"就挡不住误报泛滥,只验"不报"就挡不住漏报。
_MUST_FLAG = [
    ("os.replace(a, b)",              "os 原子改名 —— 真的会动盘"),
    ("p.write_text('x')",             "写文件"),
    ("open('f', 'w')",                "以写模式打开"),
    ("open('f', 'a')",                "追加模式 —— 一样是写"),
    ("subprocess.Popen(['x'])",       "起进程"),
    ("Path('d').mkdir()",             "建目录"),
    ("f.unlink()",                    "删文件"),
]
_MUST_NOT_FLAG = [
    ("str(p).replace('\\\\', '/')",   "str(...) 上的 replace —— 不写盘"),
    ("'a-b'.replace('-', '_')",       "字面量上的 replace"),
    ("open('f')",                     "只读打开"),
    ("open('f', 'r')",                "显式只读"),
    ("subprocess.run(['nvidia-smi'])", "查询型子进程(只读,允许)"),
    ("p.read_text()",                 "读文件"),
]
for snippet, why in _MUST_FLAG:
    check(f"★ 识别器**认得出**写盘:{snippet}", bool(writes_in(ast.parse(snippet))), why)
for snippet, why in _MUST_NOT_FLAG:
    got = writes_in(ast.parse(snippet))
    check(f"★ 识别器**不误报**:{snippet}", not got, f"{why},却报了 {got}")


# ── ⑧ ★★★ 反过来验 scan_fake 的「散文不是代码」过滤器 ──
#   这个过滤器是为了压噪声加的,而**压噪声的东西天生会往漏报的方向漂**:
#   放宽一点,报的条数就少一点,看起来"越来越干净" —— 而它可能只是瞎了。
#   ⇒ 同样两边都钉:散文里的必须被排除,真代码里的必须**仍然**被抓。
sys.path.insert(0, str(HERE))
try:
    import scan_fake                                          # noqa: E402
except Exception as e:                                        # noqa: BLE001
    check("★★★ scan_fake 能被导入(导不进来就什么都验不了)", False, f"{e}")
    scan_fake = None
if scan_fake is not None:
    _PROSE_CASES = [
        # (源码片段, 目标子串, 是不是散文)
        ('x = "a"\n# check("旧的", True)\n',        'True',      True,  "注释里引用旧断言"),
        ('"""本该只走 127.0.0.1:8080 回环"""\n',    '127.0.0.1', True,  "docstring 里的说明"),
        ("'''讲解 127.0.0.1:8080'''\n",             '127.0.0.1', True,  "单引号三引号块"),
        ('check("真的恒真", True)\n',                'True',      False, "★ 真代码,必须仍被抓"),
        ('r = get("http://127.0.0.1:8080/x")\n',    '127.0.0.1', False, "★ 真连接,必须仍被抓"),
    ]
    for _src, _needle, _want_prose, _why in _PROSE_CASES:
        _spans = scan_fake.prose_spans(_src)
        _got = scan_fake.is_prose(_src, _src.index(_needle), _spans)
        check(f"★ 散文过滤器:{_why}", _got is _want_prose,
              f"期望 is_prose={_want_prose} 实得 {_got}")

print("-" * 78)
print(f"  === 调试工具自检:{_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
