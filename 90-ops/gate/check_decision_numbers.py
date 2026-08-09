r"""决议包里写了 D 号,而 DECISIONS.md 里没有 —— 判红。

跑:  python 90-ops\gate\check_decision_numbers.py

★★★ 起因(2026-08-09,真事):
  V22 的提交信息与决议包在 08-09 就写着 **D115**,而它**一直没有落进 DECISIONS.md**。
  发现它的不是任何判据 —— 是 **V23 在写自己的包时顺手 grep 了一下**,
  记下「`grep -n "D115" 00-docs/DECISIONS.md` 零行」。
  ⇒ **今天没有任何东西**会在「包里写了号、中央文档里没有」时变红。
  D 号协议(D82)只管「并入那一刻取号、取完立刻提交」,而**没有人去核那一步做没做**。

★★ 为什么这个漏特别贵:
  D 号是**跨文档的唯一引用**。包里、代码注释里、报错文案里都会写「见 D115」,
  而中央文档里没有它 —— 于是每一处引用都指向一个**空洞**,
  并且**每一处都读起来像是有依据的**。

判据(反向:从包出发,不是从 DECISIONS 出发):
  ① 扫 00-docs/decision-packets/*.md 里出现的 D<数字>;
  ② 与 DECISIONS.md 里**编号标题**(`^## ... · D<n>`)解出的集合比;
  ③ 包里有、标题集合里没有 ⇒ **判红**。

★ 为什么第 ② 步只认【标题】而不是 grep 整份文件:
  DECISIONS.md 的正文里会**引用**别的号(「见 D82」),也会**引述**包里的话。
  拿"整份文件里出现过这个字符串"当判据,一条被随口提过的号就能骗过它 ——
  那正是本仓第一戒律的形状:看着有防护、实际没有。

★ 豁免:包里可以写一行 `DRAFT-D: D118 D119` 声明"这些号还是草案、尚未并入"。
  **豁免必须写在包里、看得见** —— 看不见的豁免和没有闸是一回事。
  锚点 `DRAFT-D:` 是 ASCII(第 8 条:机器要读的那几个字符必须是 ASCII,
  中文和标点在 cp936 控制台里会乱码)。

退出码:0 = 过 · 1 = 有号对不上 · 2 = 工具自己坏了(第 ③ 组的语义)
"""
from __future__ import annotations

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
DECISIONS = REPO / "00-docs" / "DECISIONS.md"
PACKETS = REPO / "00-docs" / "decision-packets"

# ★ 只认编号标题。`^## … · D<n>` —— 与 D82「D 编号以已提交为准」那条 grep 判据同源。
#   ★★ 不许锚到行尾:本文件的抬头有**两种**形状 ——
#     `## 2026-08-08 · D114`(号独占行尾,9 条)
#     `## 2026-07-26 · D23 凭证不进记忆库…`(号后面还有标题,104 条)
#   第一版写成 `\bD(\d+)\s*$`,只认得第二种之外的那 9 条 + 巧合的两条 = **11 条**,
#   而元断言(≥20)当场把它拦住了。★ 记一笔:**那条元断言不是装饰** ——
#   没有它,这个正则会让 100 多个已落号被判成"缺失",而报出来的每一行都读起来像真的。
_HEADING = re.compile(r"^##[^\n]*?·\s*D(\d+)\b", re.M)
# ★ 包里的引用:D 后面紧跟数字。`\b` 防住 `D115①` 之外的东西被截断 —— 序号后缀不算数字。
_REF = re.compile(r"\bD(\d+)")
# ★ 豁免行:ASCII 锚点 + 一串 D 号
#   ★★ 这里**有意不用 `\s*`**:写成 `DRAFT-D:` + 反斜杠-s 之后,
#     本仓第 ① 段那道绝对路径闸会把 `D:` + `\` 读成一个盘符路径,**当场误拦这个文件**。
#     (2026-08-09 真的拦了一次。)⇒ 用 `[ \t]*`,把反斜杠从冒号后面挪开。
#   ★ 记一笔:**一个判据的写法可以踩到另一个判据** —— 而两边都没错,
#     错的是"我以为只有我在读这一行"。
_DRAFT = re.compile(r"DRAFT-D:[ \t]*([^\r\n]*)")

# ══════════════════════════════════════════════════════════════════════════════
#  ★★★ 存量欠债表 —— **只许变短**(与 D95 那张契约欠债表同一手法)
#
#  2026-08-09 这条闸第一次跑起来,当场捞出 **16 个**包里引用、而 DECISIONS.md 里
#  一个抬头都没有的号。其中 **D66–D75 是整整一段** ——
#  它们 2026-08-03 就在「两层 MCP 决议包」里取了号,**从来没落进中央文档**;
#  而 DECISIONS.md 自己的正文第 3136 行还在写「与两层 MCP 决议包(D66–D75)的对接」。
#  ⇒ **每一处引用都指向一个空洞,而每一处都读起来像是有依据的。**
#  (这正是同一天补落 D115 时要防的那件事,只是它大了十倍、早了六天。)
#
#  ★ 为什么记在这里、而不是让闸一直红:
#    一条**永久红**的闸会训练人绕过它 —— D82 已经因此失效过两条,D117 刚把这条裁死。
#    ⇒ 存量冻在这张表里,**新增的一个都跑不掉**;而这张表**只许变短**,
#      每跑一次都把它印出来,想装看不见都不行。
#
#  ★★ 这张表**不是豁免**,是欠债:
#    · 豁免(`DRAFT-D:`)写在**包**里,意思是「这号还是草案,故意没落」;
#    · 欠债写在**这里**,意思是「这号该落而没落,是账,不是设计」。
#    两者语义不同,不许互相顶替。
#
#  ⇒ 清掉的办法:把对应决议并入 DECISIONS.md 并取号,然后从这张表里删掉那个数。
# ══════════════════════════════════════════════════════════════════════════════
_KNOWN_MISSING = {
    2, 4, 6, 7, 10, 14,          # 早期号:包里引用,而本文件的抬头从 D14 才开始
    66, 67, 68, 69, 70, 71, 72, 73, 74, 75,   # ★ 「两层 MCP 决议包」整段,2026-08-03 取号未落
}
_EXPECTED_DEBT = 16              # ★ 只许变短。变大 = 又漏了一个,当场红。

_p = _f = 0


def check(name: str, ok: bool, extra: str = "") -> None:
    global _p, _f
    if ok:
        _p += 1
    else:
        _f += 1
        print(f"  X {name}" + (f"   {extra}" if extra else ""))


def main() -> int:
    if not DECISIONS.exists():
        print(f"  ! 找不到 {DECISIONS} —— 工具自己坏了,不是判据不过")
        return 2
    if not PACKETS.is_dir():
        print(f"  ! 找不到 {PACKETS} —— 工具自己坏了")
        return 2

    doc = DECISIONS.read_text(encoding="utf-8")
    numbered = {int(n) for n in _HEADING.findall(doc)}

    # ★★ 零命中判红(第 10 条同族):正则写坏时它会安安静静地把**每一个**号都判成"没落",
    #   或者反过来把集合弄空而恰好没有包引用任何号 ⇒ 全绿。两个方向都要挡。
    check("★★ 元断言:从 DECISIONS.md 解出了编号标题(解不出 = 正则坏了,不是文件空了)",
          len(numbered) >= 20, f"只解出 {len(numbered)} 个")
    if len(numbered) < 20:
        print("  ⇒ 判据自己坏了,不往下比 —— 拿一个空集合去比,会把每一个号都判成缺失。")
        return 2

    files = sorted(PACKETS.glob("*.md"))
    check("★★ 元断言:扫到了决议包(扫到 0 份 = 路径写错,而 0 份天然全绿)",
          len(files) >= 5, f"只扫到 {len(files)} 份")
    if len(files) < 5:
        return 2

    missing: list[tuple[str, int]] = []
    drafted_total: set[int] = set()

    for f in files:
        txt = f.read_text(encoding="utf-8", errors="replace")

        drafts: set[int] = set()
        for line in _DRAFT.findall(txt):
            drafts.update(int(n) for n in _REF.findall(line))
        drafted_total |= drafts

        refs = {int(n) for n in _REF.findall(txt)}
        for n in sorted(refs - numbered - drafts - _KNOWN_MISSING):
            missing.append((f.name, n))

    # ★ 存量欠债:把还在缺的那些数出来。**只许变短**。
    still_owed = sorted(n for n in _KNOWN_MISSING if n not in numbered)
    cleared = sorted(n for n in _KNOWN_MISSING if n in numbered)

    print(f"\n  扫了 {len(files)} 份决议包 · DECISIONS.md 里有 {len(numbered)} 个编号标题"
          f" · 声明为草案的 {len(drafted_total)} 个")
    print(f"  ★ 存量欠债(包里引用、中央文档里没有抬头):**{len(still_owed)} / {_EXPECTED_DEBT}** —— "
          + (" ".join(f"D{n}" for n in still_owed) if still_owed else "已清空"))
    if cleared:
        print(f"  ✓ 已清掉 {len(cleared)} 个:" + " ".join(f"D{n}" for n in cleared)
              + "  ⇒ 请把它们从 _KNOWN_MISSING 里删掉(这张表只许变短)")

    check("★★★ 决议包里引用的每一个 D 号,DECISIONS.md 里都有对应的编号标题"
          "(存量欠债除外 —— 它冻在 _KNOWN_MISSING 里,只许变短)",
          not missing,
          f"{len(missing)} 处对不上")

    # ★★ 只许变短:欠债**变大**就是又漏了一个,当场红。
    #   ★ 变小**不判红**,但要求把表改对 —— 一张不更新的欠债表迟早变成装饰(D95 那条的教训)。
    check("★★ 存量欠债只许变短(变大 = 又有号没落进中央文档)",
          len(still_owed) <= _EXPECTED_DEBT,
          f"实测 {len(still_owed)} 个 > 期望 {_EXPECTED_DEBT} 个")

    if missing:
        print()
        print("  ── 对不上的(包 → 号)──")
        for name, n in missing:
            print(f"      {name}  →  D{n}")
        print()
        print("  ★ 两条出路,选一条:")
        print("    ① 它真的该有号 ⇒ 由第 0 条车道并入 DECISIONS.md 并取号(D82:取号在并入那一刻);")
        print("    ② 它还是草案 ⇒ 在那份包里写一行**看得见的**豁免:")
        print("         DRAFT-D: D118 D119")
        print("       ★ 豁免要写在包里,不要写在这个脚本里 —— 写在脚本里的豁免没人看得见,")
        print("         而看不见的豁免和没有闸是一回事。")

    print(f"\n{'-' * 78}")
    print(f"  === decision-numbers: {_p} PASS · {_f} FAIL ===")
    return 1 if _f else 0


if __name__ == "__main__":
    sys.exit(main())
