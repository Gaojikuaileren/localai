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

# ── ⑨ ★★★ 扫描器的**覆盖面**:.ps1 / .json / .toml 的过期文案(2026-08-06)──
#
#  ★ 起因(第二个实例,而且**已经出厂了**):`90-ops\build-client.ps1` 的
#    【已知边界(如实说)】here-string 里写着「AI 模型尚未接入(P4)」,
#    而模型早已接入。那段话进了**每一份出厂包的安装说明** —— 用户唯一会读的那份文本。
#    客户端 .cs 里的同一句话被断言钉着、被抓住了;而扫描器看不见 .ps1,
#    于是**同一句谎话在没人看的那一侧发了出去**。
#
#  ★★ 这里**只喂合成字符串,一条都不断言真文件的内容**。
#    ASSERTION-PITFALLS 第 5 条:一条会因为「那句话终于被改对了」而变红的断言,
#    测的就不是它自称在测的东西 —— 而这个扫描器**存在的目的**正是促成那次修改。
#    钉真文件等于让"修好了"和"坏了"长得一样红。
#
#  ★★★ 两个方向都钉。**只钉一边等于没钉**:
#    只钉"能报" ⇒ 一个"什么都报"的扫描器也能全绿,而噪声太大的扫描器没人看;
#    只钉"不误报" ⇒ 一个"什么都不报"的扫描器也能全绿,那正是 08-06 撞过的那次
#    (`test_route.py` 被判成"需后端",只因为它的**禁止字符串清单**里字面写着 `requests.`)。
if scan_fake is not None:
    _PS1_SHIPPED = ('$notes = @"\n【已知边界】\n· AI 模型尚未接入(P4):不会有回答。\n"@\n')
    # ★★★ 两条反向用例里**必须带引号**。第一版没带,于是它们是绿的 ——
    #   但绿的原因是「这一行没有引号 ⇒ 不像字符串字面量 ⇒ 跳过」,
    #   **跟块注释/行注释识别一点关系都没有**。红测当场戳穿了它:
    #   把块注释的正则改成永不匹配,这两条**照样全绿**(实测)。
    #   ⇒ 那是一条"为了别的理由而绿"的假断言 —— 本项目最恨的形状,而它出现在
    #     一条专门用来防假断言的自检里。带上引号之后,唯一能排除它的就只剩
    #     `_in(blocks, …)` / 行首判据本身,这条用例才真的在测它自称在测的东西。
    _PS1_BLOCKC = ('<#\n  说明:那句 "尚未接入" 已经删掉了,别再加回来。\n#>\nWrite-Host "ok"\n')
    _PS1_LINEC = ('# 说明:界面上那句 "尚未接入" 已经不在了\nWrite-Host "ok"\n')
    _STALE_CASES = [
        # (后缀, 源码, 期望报几条以上, 说明)
        (".ps1", _PS1_SHIPPED, True,
         "★★★ 正向:here-string 里的出厂文本必须报 —— 那是发给用户的那份字"),
        (".ps1", _PS1_BLOCKC, False,
         "★★ 反向:`<# … #>` 块注释里的说明不报(守卫撞在解释它所守之物的文本上,已踩 9 次)"),
        (".ps1", _PS1_LINEC, False,
         "★★ 反向:`#` 行注释里的说明不报"),
        (".json", '{ "usage.pending": { "zh-CN": "待接入" } }\n', True,
         "★ 正向:.json 里的界面文案必须报(strings.json 此前完全在盲区里)"),
        (".toml", "note = '这一段暂未启用'\n", True,
         "★ 正向:.toml 里的值必须报"),
        (".cs", 'Assert(!CodeOnly(s).Contains("尚未接入"), "那句话必须已经不在了");\n', False,
         "★★ 反向:守着这句话【不在了】的断言消息不报 —— 它是守卫,不是谎话本身"),
    ]
    for _sfx, _src, _want, _why in _STALE_CASES:
        _hits = scan_fake.stale_hits(_sfx, _src)
        check(f"过期文案扫描:{_why}", bool(_hits) is _want,
              f"期望{'报' if _want else '不报'},实得 {len(_hits)} 条:{_hits[:1]}")

    # ★★ here-string 与块注释的**归类**也要对:两者都在 .ps1 里、长得很像,
    #   混了的话"出厂文本"这个标签就成了随口一说 —— 而它正是让人一眼看出严重性的那个词。
    _h = scan_fake.stale_hits(".ps1", _PS1_SHIPPED)
    check("★★ here-string 命中被标成【出厂文本】而不是普通字符串",
          bool(_h) and "here-string" in _h[0][2], f"实得 {_h[:1]}")

    # ★★★ 钉住那个 **fail-open 后门已经被拿掉**,并且拿掉之后扫描器**不撞自己**。
    #   这两条必须一起钉:只钉前者,针写成字面量时它会报自己 8 次,
    #   下一个人为了消噪声会把后门加回来 —— 那就绕了一整圈回到原点。
    check("★★★ `debug` 不在 SKIP 里 —— 假防护扫描器必须扫得到调试工具箱**包括它自己**"
          "(D88:工具自己也要被测)",
          "debug" not in scan_fake.SKIP, f"SKIP={sorted(scan_fake.SKIP)}")
    check("★★★ 拿掉后门之后,扫描器**不报自己的针清单** —— 针是拼出来的,不是字面量。"
          "这条一红就说明有人把 `_STALE_WORDS` 写回了字面量",
          not [w for _, w, _ in
               (lambda: (scan_fake.found.clear(), scan_fake.scan_stale_claims(),
                         scan_fake.found)[-1])()
               if w.startswith("90-ops/debug/scan_fake.py")],
          "scan_fake.py 报了自己")
    scan_fake.found.clear()          # ★ 还回去:上面那条借用了模块级的 found

    check("★ `config` 在 CODE_DIRS 里 —— 全仓 6 个 .toml 有 5 个在那儿,"
          "不加它就只是把后缀写进表里好看",
          "config" in scan_fake.CODE_DIRS, f"{scan_fake.CODE_DIRS}")

    # ★★★ **够得着**这件事要单独钉。
    #   上面那几条合成用例是直接调 `stale_hits(".ps1", …)` 的 —— 它们**绕过了**
    #   `STALE_EXTS` 与 `CODE_DIRS`。⇒ 有人把 `.ps1` 从后缀表里删掉,
    #   合成用例照样全绿,而 build-client.ps1 从此不再被扫。**判据会静默瞎掉。**
    #
    # ★★★ 期望值写成**独立的字面量**,再和 `STALE_EXTS` 做集合相等 —— 反向全表。
    #   第一版是 `for _ext in scan_fake.STALE_EXTS:` —— **拿被检查的那张表当遍历源**,
    #   于是删掉 `.ps1` 时循环少转一圈,一条都不红(红测实测:EXITCODE=0,全绿)。
    #   那正是 ASSERTION-PITFALLS 3b:手写名单只能当**期望值**,不能当**遍历源**;
    #   两者的区别就是"少一项会红"和"少一项被跳过"。
    _EXPECT_EXTS = {".cs", ".py", ".ps1", ".json", ".toml"}
    check("★★★ 过期文案扫描的后缀表**逐个对得上**(少一个 = 一整类文件从此不被扫,"
          "而那不会让任何东西变红 —— 所以只能在这里拦)",
          set(scan_fake.STALE_EXTS) == _EXPECT_EXTS,
          f"实得 {sorted(scan_fake.STALE_EXTS)} 期望 {sorted(_EXPECT_EXTS)}")
    #   ⇒ 再走一遍真正的 `sources()`,只问「够不够得着」,**不问文件里写了什么** ——
    #     所以它不会因为某句话被改对了而变红(那是第 5 条坑)。
    for _ext in sorted(_EXPECT_EXTS):
        _n = sum(1 for _ in scan_fake.sources((_ext,)))
        check(f"★★ 扫描器**够得着** {_ext} 文件(零命中判红:够不着与全清白在终端上长得一样)",
              _n > 0, f"{_ext} 实测扫到 {_n} 个")

print("-" * 78)
print(f"  === 调试工具自检:{_p} PASS · {_f} FAIL ===")
sys.exit(1 if _f else 0)
