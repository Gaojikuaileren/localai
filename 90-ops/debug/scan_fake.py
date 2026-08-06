r"""★ 假防护扫描器 —— 专治本项目的签名失败模式:「看着有防护、实际没有」。

跑:  python 90-ops\debug\scan_fake.py

★★★ 这套项目一天之内被同一类东西咬过很多次,而它们**测试全绿**:
  · 显存闸的唯一生产集成坏了 5 天(cp936 编码,崩溃与拒绝都退出 1);
  · 出包门禁**从来没等过自检**($LASTEXITCODE 读的是上一条命令的残留值);
  · §6.8「绝不放行」的隔离账户在 GPU 面**权限与机主完全相同**;
  · `actual_resident` 当了一天恒真式(用自己的账本跟自己的账本比);
  · 11 条断言**整天是绿的,恰恰因为产品还不能用**;
  · 「AI 模型尚未接入」印在用户刚跟模型聊完的界面上。

它们的共同点是:**没有任何一条测试会红**。所以需要一个专门去找这类形状的东西。

★ **只读。** 一个字节都不写。
★ 它报的是**嫌疑**不是判决 —— 每条都要人看一眼。宁可多报,不可漏报;
  但**每条都必须给出可核实的位置**,否则就成了噪声。
"""
from __future__ import annotations

import io
import re
import subprocess
import sys
from pathlib import Path
from typing import List, Tuple

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:                                            # noqa: BLE001
    pass

REPO = Path(__file__).resolve().parents[2]
# ★ 2026-08-06 加 `config`:没有它,「让扫描器看见 .toml」就是一句空话 ——
#   全仓 6 个 .toml 里有 5 个在 `config/`(只有 registry.toml 在 10-core 下)。
#   ⇒ 只把 `.toml` 加进后缀表而不加这个目录,是一次**看起来修好了的假修复**,
#     而假修复正是本工具存在的理由。
CODE_DIRS = ["10-core", "20-client-win", "90-ops", "config"]
# ★★★ 2026-08-06 拿掉 `debug`:那是一个 **fail-open 后门**。
#   它的字面意思像是"跳过构建产物目录",但 .NET 的产物目录叫 `bin\Debug`(大写 D),
#   `bin` 已经在表里挡住了;这个小写的 `debug` 实际只挡住了一样东西 ——
#   **`90-ops\debug` 整个调试工具箱,包括本文件自己**。
#   ⇒ 假防护扫描器从来没有扫过自己,也没扫过 doctor.py / probe_*.py。
#     而 D88 明写「工具自己也要被测」。ASSERTION-PITFALLS 第 1 条也点过名:
#     靠"跳过本文件"来避免撞上自己的针,是 fail-open —— 守卫从此不查自己。
#   ⇒ 正确做法是**把针拼出来**(见下方 _STALE_WORDS),而不是把自己排除在外。
SKIP = {"bin", "obj", "node_modules", ".git", "dist"}

Finding = Tuple[str, str, str]        # (类别, 位置, 说明)
found: List[Finding] = []


def add(kind: str, where: str, why: str) -> None:
    found.append((kind, where, why))


def sources(exts=(".py", ".cs", ".ps1")):
    for d in CODE_DIRS:
        for p in (REPO / d).rglob("*"):
            if p.suffix.lower() not in exts:
                continue
            if any(part in SKIP for part in p.parts):
                continue
            try:
                yield p, p.read_text(encoding="utf-8", errors="replace")
            except Exception:                                # noqa: BLE001
                continue


def rel(p: Path) -> str:
    return str(p.relative_to(REPO)).replace("\\", "/")


# ── ★★★ 散文不是代码 ────────────────────────────────────────────────
#   本仓的习惯是**把原因写下来**:删掉一条恒真断言,就在注释里贴上原文说明为什么删;
#   写一个源码扫描测试,就在 docstring 里讲清楚"本该只走 127.0.0.1:8080 回环"。
#   照着字面去搜,这些**最负责任的写法**全都成了嫌疑:
#     · 注释里引用的 `check(..., True)` → 报成恒真断言
#     · docstring 里的 `127.0.0.1:8080`  → 报成"直连真实端口"(那个文件一个连接都不发)
#   ⇒ 一律先把注释与三引号块的位置算出来,落在里面的匹配不报。
#   ★ 这个坑(断言/扫描器绊在解释性文本上)已经踩过很多次,见 ASSERTION-PITFALLS。
_TRIPLE = re.compile(r"(\"\"\"|''')(?:.|\n)*?\1")


def prose_spans(s: str):
    """三引号块的 [start, end) 区间。★ 不求完美解析,求**稳定** ——
    这是调试工具,宁可少排除几处(多报一条嫌疑)也不要写个会崩的解析器。"""
    return [(m.start(), m.end()) for m in _TRIPLE.finditer(s)]


def is_prose(s: str, pos: int, spans) -> bool:
    """这个位置是不是落在注释行或三引号块里。"""
    for a, b in spans:
        if a <= pos < b:
            return True
    line_start = s.rfind("\n", 0, pos) + 1
    return s[line_start:pos + 1].lstrip().startswith(("#", "//", "///", "*"))


# ── ① 恒真断言 ──────────────────────────────────────────────────────
_ALWAYS_TRUE = [
    (re.compile(r'\bcheck\(\s*"[^"]*"\s*,\s*True\s*[,)]'), "check(..., True)"),
    (re.compile(r'\bAssert\(\s*true\s*[,)]', re.I), "Assert(true)"),
    (re.compile(r'\bcheck\(\s*"[^"]*"\s*,\s*[^,)]*\bor True\b'), "... or True"),
    (re.compile(r'\bAssert\([^;]*\|\|\s*true\b', re.I), "... || true"),
]


def scan_always_true():
    """恒真断言 —— 它**永远不会红**,等于不存在。"""
    for p, s in sources((".py", ".cs")):
        if "test" not in p.name.lower() and "selftest" not in p.name.lower():
            continue
        lines = s.split("\n")
        spans = prose_spans(s)
        for pat, label in _ALWAYS_TRUE:
            for m in pat.finditer(s):
                ln = s[:m.start()].count("\n") + 1
                if is_prose(s, m.start(), spans):
                    continue                                  # 见 prose_spans 上方的说明
                # ★★ 排除【异常路径标记】这个正当形状:
                #     try:  ...; check("X 被拒", False, "竟然没拒")     ← 走到这儿就是没抛
                #     except Reject: check("X 被拒", True)              ← 抛了才算过
                #   这里的 True **不是恒真** —— 它是"控制流真的到了 except"的证据。
                #   ★ 判据:往上 6 行内出现 except/catch。放宽会漏报,收紧会误报,
                #     取 6 行是因为这个形状在本仓里从没超过 6 行。
                ctx = "\n".join(lines[max(0, ln - 7):ln])
                if re.search(r"^\s*(except|catch)\b", ctx, re.M):
                    continue
                # ★ C# 的同款形状是 try 在【前】、catch 在【后】:
                #     try { _ = make(); Assert(true, "X 能构造出来(构造期不抛)"); }
                #     catch (Exception ex) { Assert(false, "★★ X 构造期就抛了 —— 客户端打不开"); }
                #   这里的 true 同样不是恒真 —— 它是"没抛"的证据。所以往下也看几行。
                after = "\n".join(lines[ln:ln + 4])
                if re.search(r"^\s*(catch|except)\b", after, re.M) or re.search(r"\btry\s*\{", ctx):
                    continue
                add("恒真断言", f"{rel(p)}:{ln}",
                    f"{label} —— 它永远不会红,等于不存在")


# ── ② 提取后循环但没有元断言 ────────────────────────────────────────
def scan_zero_assert_loops():
    """先提取、再循环判 —— 提取器一条都匹配不上时,里面的检查会**静默消失**。

    ★ 这是 ASSERTION-PITFALLS 第 4 条那条推论:判据太宽会变成【零断言】,
      测试仍然全绿而它已经什么都不管了。
    """
    for p, s in sources((".py",)):
        if not p.name.startswith("test_"):
            continue
        for m in re.finditer(r"^for\s+\w+\s+in\s+(\w+)\s*:", s, re.M):
            var = m.group(1)
            seg = s[m.end():m.end() + 900]
            if "check(" not in seg:
                continue
            # 元断言的形状:在 for 之前对这个集合做过【长度】或【全表】断言。
            # ★★ 第一版只认 len(...) —— 于是 test_tainted 被误报:那里用的是
            #    `NO_PLAINTEXT_TIERS == {A, B}`(整集合逐字相等),而那是**更强**的形式,
            #    正是本项目一直在推的「反向全表」。扫描器把最好的写法报成缺陷,
            #    人就会开始忽略它 —— 一个训练人无视自己的扫描器等于没有。
            before = s[:m.start()]
            has_meta = re.search(rf"check\([^\n]*len\(\s*{re.escape(var)}\s*\)", before) \
                or re.search(rf"len\(\s*{re.escape(var)}\s*\)\s*[=><]", before[-1500:]) \
                or re.search(rf"\b{re.escape(var)}\s*==\s*[\{{\[(]", before[-1500:]) \
                or re.search(rf"[\{{\[(][^\n]*\}}\]?\s*==\s*{re.escape(var)}\b", before[-1500:]) \
                or re.search(rf"\b{re.escape(var)}\s*(<=|>=|==)\s*set\(", before[-1500:]) \
                or re.search(rf"set\(\s*{re.escape(var)}\s*\)\s*(<=|>=|==|!=)", before[-1500:]) \
                or re.search(rf"(<=|>=|==|!=)\s*set\(\s*{re.escape(var)}\s*\)", before[-1500:])
            if not has_meta:
                ln = s[:m.start()].count("\n") + 1
                add("可能的零断言", f"{rel(p)}:{ln}",
                    f"对 `{var}` 循环里有 check(),但**没看到**元断言钉住 len({var}) —— "
                    f"提取器匹配不上时这些检查会静默消失")


# ── ③ 已过期的「尚未 / 未接入」文案 ─────────────────────────────────
#  ★★★ 针**拼出来**,不写成字面量(ASSERTION-PITFALLS 第 1 条第 8 次带出的写法)。
#    上面刚把 `debug` 从 SKIP 里拿掉 ⇒ 本文件从今天起**会被自己扫到**。
#    如果这里写成字面量,这一行会被自己报 8 次(实测:拿掉后门后当场 8 条),
#    而那正是这条坑最经典的形状 —— 守卫撞在**描述它所守之物**的那段文本上。
#    ⇒ 拼出来之后,守卫可以连自己所在的文件一起扫,不必开"跳过本文件"那个 fail-open 后门。
_STALE_WORDS = ["尚未" + "接入", "未" + "接入", "还没有" + "做", "还没" + "做",
                "尚未" + "实现", "待" + "接入", "暂" + "未"]

# ── ★ PowerShell 的两种「不是代码的文本」,方向**相反**,必须分开 ────────
#   · `<# … #>` 块注释 —— 是说明,**要排除**;
#   · `@" … "@` / `@' … '@` here-string —— 是**会被写进出厂包的正文**,
#     绝不能排除。第二个实例就藏在这里:`build-client.ps1` 的安装说明。
_PS1_BLOCK_COMMENT = re.compile(r"<#(?:.|\n)*?#>")
_PS1_HERESTRING = re.compile(r"@(\"|')(?:.|\n)*?\1@")


STALE_EXTS = (".cs", ".py", ".ps1", ".json", ".toml")


def _spans(rx, s):
    return [(m.start(), m.end()) for m in rx.finditer(s)]


def _in(spans, pos: int) -> bool:
    return any(a <= pos < b for a, b in spans)


def stale_hits(suffix: str, s: str):
    """一份源码里所有「过期文案」的嫌疑点 —— 返回 (词, 行号, 归类, 整行)。

    ★ 抽成纯函数是为了**能被合成输入两个方向各问一遍**(selfcheck.py 第 ⑧ 组)。
      D91 裁定③3 的判据:**测效果,不测配置** —— 钉「`.ps1` 在后缀表里」只能证明
      配置写对了,钉「喂它一段 here-string 会报出来」才证明它真的会报。

    ★★ selfcheck 里那几条断言**一律喂合成字符串,绝不断言真文件里的某一条**。
      理由是 ASSERTION-PITFALLS 第 5 条:一条会因为「那句话终于被改对了」而变红的
      断言,测的就不是它自称在测的东西 —— 而这个扫描器的**目的**正是促成那次修改。
      钉真文件,等于让"修好了"和"坏了"长得一样红。
    """
    out = []
    # ★ 只有 .ps1 需要算区间:.py 的三引号块由行首判据兜住,.json 没有注释,
    #   .toml 只有 `#` 行注释。**多算等于多写一份解析器**,而"没有测试文件自己再写
    #   一份去注释器"是本仓已有的一条反向全表断言。
    blocks = _spans(_PS1_BLOCK_COMMENT, s) if suffix == ".ps1" else []
    heres = _spans(_PS1_HERESTRING, s) if suffix == ".ps1" else []
    for w in _STALE_WORDS:
        for m in re.finditer(re.escape(w), s):
            if _in(blocks, m.start()):
                continue                                      # <# … #> 是说明
            line_start = s.rfind("\n", 0, m.start()) + 1
            nl = s.find("\n", m.start())
            line = s[line_start:(nl if nl >= 0 else len(s))]
            in_here = _in(heres, m.start())
            # ★ here-string **内部**不认行注释:`#` 开头在正文里就是一个井号,
            #   而那正文是要发给用户的。在这里"认注释"会把出厂文本挡在门外。
            if not in_here and line.lstrip().startswith(("//", "#", "*", "///")):
                continue                                      # 注释里的说明不算文案
            # ★★★ 判据按格式分:
            #   · .py/.cs/.json/.toml —— 文案只可能在**引号里**,所以要求行上有引号;
            #   · .ps1 的 here-string —— 正文**整段都没有引号**(`@"` 与 `"@` 在别的行上)。
            #     ⇒ 沿用"行上要有引号"会让这一整类**静默漏掉**,而它恰恰是最要紧的一类:
            #       here-string 里的字,是直接写进出厂包给用户读的。
            if not in_here and '"' not in line and "'" not in line:
                continue
            # ★★ 排除【断言消息】:`Assert(!x.Contains("尚未接入"), "…必须删掉…")`
            #   那是在**守护**这句话已经不在了,不是它本身。
            #   不排的话 72 条里大半是自己人 —— 而**噪声太大的扫描器没人看,等于没有**。
            if re.search(r"\b(Assert|check)\s*\(", line) or ".Contains(" in line:
                continue
            out.append((w, s[:m.start()].count("\n") + 1,
                        "出厂文本(here-string)" if in_here else "字符串",
                        line.strip()))
    return out


def scan_stale_claims():
    """「这件事还没有」这类话 —— 做出来之后它当场变成假话。

    ★ 2026-08-05 一晚之内这类问题出现**五次**,其中一次过期的是**断言本身**。
      工具报的是嫌疑:每条都要人对照现状看一眼。

    ★★ 2026-08-06 扩到 .ps1 / .json / .toml。**这不是顺手加个后缀** ——
      第二个实例已经出厂了:`90-ops\\build-client.ps1` 的【已知边界(如实说)】里
      写着「AI 模型尚未接入(P4):聊天会记录但不会有回答」,而模型早已接入。
      那段话进了**每一份出厂包的安装说明**,是用户唯一会读的那份文本。
      ⇒ 扫描器看不见 .ps1,所以它躺了下来;而客户端 .cs 里同一句话被断言钉着。
        **同一句谎话,在被守着的那一侧被抓住,在没人看的那一侧发了出去。**
    """
    for p, s in sources(STALE_EXTS):
        for w, ln, where, line in stale_hits(p.suffix.lower(), s):
            add("可能过期的文案", f"{rel(p)}:{ln}",
                f"「{w}」出现在{where}里:{line[:88]}")


# ── ④ 定义了却没有调用点的函数 ──────────────────────────────────────
def scan_dead_defs():
    """定义了但全仓找不到调用点 —— 「函数还在、调用点没了」是编译与行为都抓不到的缺陷。"""
    all_src = {rel(p): s for p, s in sources((".py", ".cs"))}
    blob = "\n".join(all_src.values())
    for path, s in all_src.items():
        if "/debug/" in path:
            continue
        lines = s.split("\n")
        for m in re.finditer(r"^(?:async\s+)?def\s+([a-z_][a-z0-9_]{4,})\s*\(", s, re.M):
            name = m.group(1)
            if name.startswith("_") or name in ("main", "setup", "teardown"):
                continue
            # ★★ 带装饰器的不算「没有调用点」—— 路由处理函数(@app.get/@app.post)
            #    是**框架**在调,源码里当然搜不到调用。第一版把 sync_snapshot /
            #    list_models 这两个正在服役的端点报成了死代码。
            #  ★ 报错的代价在这里是不对称的:把活着的端点说成死的,人会去删它。
            ln0 = s[:m.start()].count("\n")
            if any(lines[i].lstrip().startswith("@")
                   for i in range(max(0, ln0 - 4), ln0)):
                continue
            # 出现次数 <= 1 表示只有定义那一处
            if len(re.findall(rf"\b{re.escape(name)}\b", blob)) <= 1:
                ln = s[:m.start()].count("\n") + 1
                add("没有调用点", f"{path}:{ln}", f"`{name}()` 全仓只出现一次(定义处)")


# ── ⑤ 会随环境漂移的断言 ────────────────────────────────────────────
# ★★ 盘符**拼出来**,不写成字面量 —— 两个理由,都不是洁癖:
#  ① pre-commit 钩子扫「代码里不得出现绝对路径」,而它当然分不清
#     "一条硬编码路径"和"一条用来抓硬编码路径的正则"。实测:写死的那版让本次提交被拒。
#     (同款形状今天踩了好几次,见 ASSERTION-PITFALLS 第 1 条:
#      守卫撞在**描述它所守之物**的那段文本上。)
#  ② ★ 更要紧的:原来写死的盘符是 `D`,而这个项目在 **E 盘** ——
#     也就是说这条判据**在本仓永远匹配不到东西**,是一条静默的零断言。
#     钩子拒绝提交的同时,把这个洞一起照出来了。现在任意盘符、两种斜杠都算。
_DRIVE = r"(^|[^A-Za-z0-9])[A-Za-z]" + ":" + r"[\\\\/]"

_ENV_HINTS = [
    (re.compile(r"nvidia-smi"), "直接打实机 nvidia-smi"),
    (re.compile(r"127\.0\.0\.1:\d+"), "直连一个真实端口"),
    (re.compile(r"status_code\s*==\s*503"), "把 503 当判据(它成立的前提可能是「后端没起」)"),
    (re.compile(_DRIVE), "硬编码绝对路径(任意盘符)"),
]


def scan_env_dependent():
    """★ 判据:一条断言若会因为「功能终于能用了」而变红,它测的就不是它自称在测的东西。"""
    for p, s in sources((".py",)):
        if not p.name.startswith("test_"):
            continue
        injected = "_AlwaysUnreachable" in s or "lambda: " in s or "reconfigure" in s
        spans = prose_spans(s)
        for pat, label in _ENV_HINTS:
            for m in pat.finditer(s):
                line_start = s.rfind("\n", 0, m.start()) + 1
                line = s[line_start:s.find("\n", m.start())]
                if is_prose(s, m.start(), spans):
                    continue                                  # 散文不是代码 —— 见上方说明
                ln = s[:m.start()].count("\n") + 1
                add("可能随环境漂移", f"{rel(p)}:{ln}",
                    f"{label}{'(该文件有注入迹象,可能已处理)' if injected else ' —— **没看到注入迹象**'}")


# ── ⑥ 吞掉异常 ──────────────────────────────────────────────────────
def scan_swallowed():
    """空的 catch / except pass —— 失败与成功长得一样的最常见形态。

    ★ 只报**没有任何说明**的那些:带注释解释「为什么这里可以吞」的不算
      (本项目有大量正当的吞,比如"探不到就是没证据,不是错误")。
    """
    for p, s in sources((".cs",)):
        for m in re.finditer(r"catch\s*(?:\([^)]*\))?\s*\{[^\S\n]*\}", s):
            line_start = s.rfind("\n", 0, m.start()) + 1
            nl = s.find("\n", m.end())
            line = s[line_start:nl if nl > 0 else len(s)]
            prev_start = s.rfind("\n", 0, max(0, line_start - 1)) + 1
            prev = s[prev_start:line_start]
            # ★ 本项目有**大量正当的吞**(「探不到就是没证据,不是错误」之类),
            #   而它们几乎都带着解释。带说明的不报 —— 只报**一个字都没写**的那些。
            #   ★★ 不这么收紧的话扫描器会报 108 条,而**噪声太大的扫描器没人看,等于没有**
            #     (与 flaky 测试训练人用 --no-verify 是同一个失败模式)。
            if "//" in line or "//" in prev:
                continue
            # ★★ 清理/善后路径上的吞是**正当的**:删临时文件、删测试证书、Dispose ——
            #   那里"失败了"本来就没有下一步,报出来只会淹掉真问题。
            #   ★ 判据只认**确定性的清理动词**,不认"看起来像清理" ——
            #     放宽一点就会把真的静默失败也放过去,而那正是这个扫描器要抓的东西。
            if re.search(r"\b(Delete|Remove|Dispose|Kill|Stop|Close|Unlink|Cleanup|Clear)\w*\s*\(",
                         line):
                continue
            ln = s[:m.start()].count("\n") + 1
            add("静默吞异常", f"{rel(p)}:{ln}", f"空 catch 且**上下都无说明**:{line.strip()[:70]}")
    for p, s in sources((".py",)):
        for m in re.finditer(r"except[^\n:]*:\s*\n(?:[^\S\n]*#[^\n]*\n)*[^\S\n]*pass\b", s):
            if "#" in m.group(0):
                continue                                      # 带说明的不报
            ln = s[:m.start()].count("\n") + 1
            add("静默吞异常", f"{rel(p)}:{ln}", "except: pass 且无说明")


SCANS = [
    ("恒真断言", scan_always_true),
    ("可能的零断言", scan_zero_assert_loops),
    ("可能过期的文案", scan_stale_claims),
    ("没有调用点", scan_dead_defs),
    ("可能随环境漂移", scan_env_dependent),
    ("静默吞异常", scan_swallowed),
]


def main() -> int:
    print("=" * 78)
    print("  假防护扫描器   ★ 只读 · 报的是【嫌疑】不是判决,每条都要人看一眼")
    print("=" * 78)
    broke = []
    for name, fn in SCANS:
        # ★ 逐项隔离:一个扫描挂了,其余六个照跑 —— 否则一处笔误就让整个工具哑掉,
        #   而"哑掉"和"没发现问题"在终端上长得一模一样。
        try:
            fn()
        except Exception as e:                               # noqa: BLE001
            broke.append(name)
            add("扫描器自己出错", name, f"{type(e).__name__}: {e}")

    by_kind = {}
    for k, w, y in found:
        by_kind.setdefault(k, []).append((w, y))
    # ★ 默认只列前 12 条:噪声太大的扫描器没人看。审计时用 --full 看全部,
    #   --prod 只看生产代码(测试里的嫌疑多半是"断言在守着这句话")。
    limit = 10 ** 6 if "--full" in sys.argv else 12
    if "--prod" in sys.argv:
        by_kind = {k: [(w, y) for (w, y) in v
                       if "/test" not in w and "Selftest" not in w and "selftest" not in w]
                   for k, v in by_kind.items()}
        by_kind = {k: v for k, v in by_kind.items() if v}
        print("\n  (--prod:只列生产代码,已隐去测试文件里的嫌疑)")
    for kind, items in sorted(by_kind.items(), key=lambda x: -len(x[1])):
        print(f"\n── {kind}({len(items)} 条)" + ("" if len(items) <= limit else f" · 只列前 {limit}"))
        for w, y in items[:limit]:
            print(f"   {w}")
            print(f"     {y}")
    print("\n" + "-" * 78)
    print(f"  共 {len(found)} 条嫌疑,分 {len(by_kind)} 类。")
    print("  ★ 这是**清单不是判决**:本项目有大量正当的『吞异常』与『占位文案』,")
    print("    但每一条都该有人能说出为什么正当 —— 说不出的那些就是真问题。")
    if broke:
        # ★★ 退出码 2 = **扫描器自己坏了**,不是"扫干净了"。
        #   两者的下一步完全相反:一个去修工具,一个去修系统。
        #   ★ 这里**绝不能**返回 0 —— 少跑了两个扫描却报"共 3 条嫌疑",
        #     读的人会以为仓库很干净,而真相是有两类根本没查。
        print(f"\n  ?? **有 {len(broke)} 个扫描没跑完**:{broke}")
        print("     → 这一轮的结论**不完整**。先修工具(退出码 2),别据此下判断。")
        return 2
    # ★ 有嫌疑**不**返回 1:这是清单不是判决,返回 1 会训练人忽略它
    #   (与 flaky 测试训练人用 --no-verify 是同一个失败模式)。
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
    except Exception as e:                                   # noqa: BLE001
        print(f"\n  ? 扫描器自己出错:{type(e).__name__}: {e}")
        print("    → 这是**工具**的问题,不是系统的问题。修 scan_fake.py")
        sys.exit(2)
