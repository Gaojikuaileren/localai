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
#   ★★ 2026-08-11:排除**正则/字符集**里的那种 —— `D6[0-9]` 这种写法里的 `D6` 不是一个 D 号引用,
#     它是一句 grep 命令的字面量(实例:`worktree-split-2026-08-03.md` 里
#     「改号时必须一起改(`grep -rn 'D6[0-9]\|D7[0-9]' …`)」)。
#     旧版把它读成「引用了 D6 与 D7」,于是那两个号被当成欠债养了两天。
#   ⇒ 后面紧跟 `[` 的不算(那是字符集的开头)。
#   ★ 记一笔:**这条闸自己也会误读文本** —— 而误读出来的欠债与真欠债在表里长得一模一样。
_REF = re.compile(r"\bD(\d+)(?!\[)")
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
    2, 4, 10, 14,                # 早期号:包里引用,而本文件的抬头从 D14 才开始
    66, 67, 68, 69, 70, 71, 72, 73, 74, 75,   # ★ 「两层 MCP 决议包」整段
}
_EXPECTED_DEBT = 14              # ★ 只许变短。变大 = 又漏了一个,当场红。
#  ★★ 2026-08-11:**16 → 14** —— 清掉的两个(D6 · D7)**根本不是欠债,是本闸自己读错的**:
#    它们来自 `worktree-split-2026-08-03.md` 里一句 grep 命令的字面量 `'D6[0-9]\|D7[0-9]'`,
#    而旧提取器把它读成「引用了 D6 与 D7」。⇒ 修的是**提取器**,不是把它们从表里划掉了事。
#  ★★★ 而 D66–D75 那整段**不是"该落而没落"** —— 那份包的抬头逐字写着
#    「**⇒ 用户裁定:本包封存,不并入中央文档,等 P6 开工时再取用**」(2026-08-03)。
#    ⇒ **它们是【设计】,不是账。** 上一版这段注释把两者混成一句「该落而没落」,那句话是错的。
#    ★★★ 2026-08-11 更正:此处原写「真要清,应当在那份包里写一行 DRAFT-D 豁免」——**那是错的**。
#    上面 still_owed 那一行**只看 DECISIONS 的编号抬头**,与任何包里写不写豁免无关;
#    实测引用这批号的包有 21 份,逐份写豁免也动不了这个计数器一分。
#    ⇒ 这 14 个在今天的判据下**结构上清不动**。两条真出路:
#      ① 把那批决议真的并进本文件(**而用户 2026-08-03 明裁封存,不许并**);
#      ② 改本闸的欠债语义,把「封存号」与「欠账号」分成两栏 —— 那是一次判据改动,要单独裁。
#    ★ 一个听起来对、而实际做不到的解法,比不给解法更坏:下一个人会照着做,做完发现数字没动。

# ══════════════════════════════════════════════════════════════════════════════
#  ★★★ 第二半:**该取号而没取**(2026-08-11 加)
#
#  起因:2026-08-11 那天,**十条车道的裁定一个号都没落**,而这道闸报 **4 PASS · 0 FAIL**。
#  **为什么没抓到**:它扫的是决议包里的 `D<数字>` —— 而那些包写的是 **`D?`**,
#  **不是数字** ⇒ 它**扫不到**。
#  ⇒ 它此前只抓「取了号但没落地」,**抓不到「该取号而没取」**。
#  ★ 判词:**零命中与全清白长得一模一样。**
#
#  ★★ 判据:**该包已经进了 `main` 的树,而它的 Markdown 抬头里还挂着 `D?`** ⇒ 判红。
#
#  为什么「已并入」用「文件在不在 `main` 的树里」而不是分支名:
#    · 车道**在跑的时候**写 `D?` 是**对的**(D82:取号在并入那一刻)——
#      而车道自己新建的包**不在 main 的树里** ⇒ 绿。一合进来 ⇒ 该取号了,红。
#    · 它**在主工作树与车道工作树里都成立**,不依赖当前分支名 ——
#      而分支名在 detached HEAD(本仓真有过两个)或临时分支上会失灵。
#
#  为什么只看**抬头**、不 grep 全文:
#    今天真有好几份包在**讲协议本身**(「草案期一律写 `D?`,并入那刻取号」)——
#    grep 全文会把这些**最负责任的写法**判成欠号。
#    这与 DECISIONS 那侧「只认编号抬头」是同一条纪律(ASSERTION-PITFALLS 第 1 条)。
#
#  ★★★ 存量同样冻表、**只许变短**:2026-08-11 实测有 **17 份**已并入 main 的包
#    抬头还挂着 `D?`。让它当天永久红 = 训练人绕过它(D82 已因此失效两条,D117 裁死过这条)。
#    ★ 而**决议包不在第 0 条车道本轮的边界内**,没法逐份去加 `DRAFT-D:` 豁免 ——
#      这是**没做完的那一半**,如实冻在下面这张表里,不是"处理过了"。
# ══════════════════════════════════════════════════════════════════════════════
_HEADING_ANY = re.compile(r"^#{1,6} .*", re.M)
_QMARK = "D" + "?"               # ★ 拼出来:否则本文件自己就是它要找的东西(第 1 条,已踩 9 次)

# ★★★ 2026-08-11 扩宽 —— 起因是这道闸报「该取号而没取:**0**」的**同一天**,
#   `admin-app-split-2026-08-07.md` 正躺在 main 里,抬头逐字写着「**(未取号 · 提议)**」。
#   它从 2026-08-07 起就把理由写得很清楚(是提议不是裁定,用户没拍板)——
#   **而这道闸一个字也读不懂**,因为它只认 `D` + 问号那一种形状。
#   ⇒ 那份包**从来不在它的视野里**,于是「全清白」与「看不见」在那张表上长得一模一样。
#   ★ 这是「零命中不是不存在」在本仓的**第三次**实例(前两次:V33/方向 B · D6-D7 误读)。
#   ★★ 而这一次它伪装得最好:表上的数不是零命中,是 **0/17「已清空」** ——
#     一个刚刚兑现过承诺的数字,比空表更不容易让人起疑。
_ZH_DRAFTMARK = re.compile(r"(未取号|待取号|尚未取号|未编号|待编号|取号待定|决议草案)")
# ★★★ 2026-08-11 第二次扩宽 —— 而这一次捞出的是**一张假表**:
#   加上「取号待定 / 决议草案」之后,**8 份**从 2026-08-03/04 起就躺在 main 里、
#   抬头逐字写着「(决议草案 · 取号待定)」的包**第一次被看见**。
#   ⇒ 前一天宣布「该取号而没取:17 → **0**,只许变短这条纪律真的被兑现过一次」——
#     **那个 0 是假的。真实数是 8。** 表没有变短,是**取样口径把它们排除在外**。
#   ★ 判词:**一张假的欠债表比没有更坏** —— 没有表的人知道自己不知道;
#     而看着一张写着 0 的表的人,以为自己知道。
#   ★★ 它伪装得比零命中更好:**不是空表,是一个刚刚兑现过承诺的数字**。
#   ⇒ 处置照 D117:不让它恒红(恒红会训练人绕过)——**冻进下面的存量表,只许变短**。
# ★ 划掉的段不算:本仓纪律是「原文划掉不删」,于是**已经了结的旧待办仍带着这些字**。
#   (实例:`p3c-signoff-2026-08-04.md` §5.3 那条「worktree-split 仍待取号并入」,
#    2026-08-11 划掉并更正 —— 它描述的是**别的包**,而且那件事早在 08-04 当天就了结了。)
#   不剥掉它,这条扩宽第一次跑就会误报,而**误报会训练人绕过闸** —— D117 裁死过这条。
_STRIKE = re.compile(r"~~.*?~~", re.S)


_H1 = re.compile(r"^# .*", re.M)
_REALNUM = re.compile(r"\bD(\d+)\b")
# ★★★ 2026-08-15 —— **这一行自己刚坏过一次,而坏法是静默的**:
#   上一版把它写成 `re.compile(r"<退格符>D(\d+)<退格符>")` —— 两个 0x08 控制字符,
#   来源是写这行代码的脚本用了非 raw 的三引号字符串:那里 `反斜杠 b` **是合法转义**
#   (退格符),被吃掉了;而 `反斜杠 d` **不是**合法转义,原样留下 ——
#   于是 grep 出来长这样:`re.compile(r"D(\d+)")`,**肉眼几乎看不出少了两个词边界**。
#   后果:这条正则**恒不匹配** ⇒ 下面 `_h1_names_real_number` **恒 False**
#   ⇒ 「已处置 ⇒ 放行」那条判据**整条是死的**。
#   ★ 它这次的表现是**多报**(把刚取过号的 V36 误判成欠号),所以当场被发现。
#   ★★ 而如果方向反过来(恒 True),它会**静默放过每一份包** ——
#     那与「全清白」长得一模一样,**而我不会发现**。
#   ⇒ 与 D134 裁定②说的是同一件事:**尺子自己是坏的,而错法是静默的**。
#     那条讲的是去注释器,这条讲的是这道闸;**同一天,同一个形状。**
#   ⇒ 改判据的代码从此**用字节拼正则**,不经过任何非 raw 字符串。


def _self_declares_draft(txt: str) -> bool:
    """本包有没有【自陈未取号】—— 抬头里的中文形状,**或正文任意一处的 `D` + 问号**。

    ★★★ 2026-08-15 第三次扩宽 —— 而这一次的病,**这个文件自己的注释里刚记过**:
      下面 `_heading_marks_draft` 的 docstring 逐字写着「只看抬头、不 grep 全文」,
      理由是"讲协议本身的包会被误判"。那个理由**当时是对的**,而它挡住的东西比它防的多:
      **`D` + 问号写在抬头下面那几行元信息里,这道闸一个字也看不见。**
      实测:已并入 main 的包里 **26 份**是这个形状(V37 独立报了同一条)。
      · V37 写「取号:`D?`(草稿,未取)」在第 3 行 —— 闸看不见,静默放过;
      · V38 写「**本包不取号**(写 `D?`)」在第 6 行 —— 同样看不见;
      · 而 V36 恰好写在**第 1 行**,于是被抓到了。**三份包同一天并入,判据只看见其中一份。**
    ★ 判词:又是一次**零命中与全清白长得一模一样** ——
      而这次它躲过的不是别的判据,是**刚刚因为同一个病被扩宽过两次的这一条**。
    ⇒ 改法**不是**简单 grep 全文(那会把"讲协议本身"的包全判红),
      而是把问题换一个问法:**「这份包被处置过没有」** —— 见 `_h1_names_real_number`。
    """
    if _QMARK in txt:
        return True
    for h in _HEADING_ANY.findall(txt):
        if _ZH_DRAFTMARK.search(_STRIKE.sub("", h)):
            return True
    return False


def _h1_names_real_number(txt: str) -> bool:
    """**主标题**(`# ` 那一行)里指得出一个真号 ⇒ 这份包已经被处置过,放行。

    ★★ 只看主标题,不看小节标题:`## §2 依据 D65` 这种引用**极常见** ——
      拿"任何一个抬头里有 D 号"当"已处置",会漏掉一大片。
      实测(2026-08-15 离线对拍):按全部抬头算捞出 **15** 份,只按主标题算捞出 **17** 份,
      差的那两份正是 `v38-p5-first-batch` 与 `v31-persona-floor-and-host-pack` ——
      **它们的小节标题里有别人的号,而自己一个号都没有。**
    ★ 记一笔:**一个太宽的"已处置"判据,比没有判据更难发现** ——
      它不会报错,只会让该红的那几份安静地不出现。
    """
    return any(_REALNUM.search(_STRIKE.sub("", h)) for h in _H1.findall(txt))


def _heading_marks_draft(txt: str) -> bool:
    """抬头里有没有【自陈未取号】的标记 —— 两种形状:`D` + 问号,或中文「未取号/待取号」。

    ★ 只看抬头、不 grep 全文:今天真有好几份包在**讲协议本身**
      (「草案期一律写 D-问号,并入那刻取号」),grep 全文会把最负责任的写法判成欠号。
    """
    for h in _HEADING_ANY.findall(txt):
        h = _STRIKE.sub("", h)
        if _QMARK in h or _ZH_DRAFTMARK.search(h):
            return True
    return False

_KNOWN_UNNUMBERED = {
    "admin-app-packaging-2026-08-08.md",
    "admin-app-phase2-migration-map-2026-08-08.md",
    "admin-app-phase2-prereqs-2026-08-08.md",
    "admin-on-demand-default-grant-2026-08-09.md",
    "appcontainer-isolation-mcp-transport-2026-08-05.md",
    "egress-b-gates-impact-2026-08-06.md",
    "gateway-stopgate-and-attribution-2026-08-09.md",
    "host-loopback-business-route-2026-08-08.md",
    "memory-suite-runnability-2026-08-06.md",
    "out-landing-and-package-text-2026-08-10.md",
    "revived-assertions-2026-08-09.md",
    "revoke-inflight-streams-2026-08-10.md",
    "shared-data-topology-host-authoritative-2026-08-08.md",
    "stackstop-kill-safety-2026-08-09.md",
    "sync-snapshot-pull-on-connect-2026-08-08.md",
    "v20-chat-view-fixes-2026-08-08.md",
    "worktree-teardown-and-tidy-2026-08-09.md",
}
_KNOWN_UNNUMBERED |= {
    # ★★★ 2026-08-11 第二次扩宽捞出的 8 份 —— 抬头逐字「(决议草案 · 取号待定)」,
    #   自 2026-08-03/04 起在 main 里,而此前**任何一版判据都没看见过它们**。
    #   ★ 它们与上面那 17 份**性质不同**:那 17 份是"已清掉的记录",
    #     这 8 份是**真·还欠着**。合表只是为了让闸不恒红,**不表示它们被处理过**。
    "client-version-visibility-2026-08-03.md",
    "identity-elevation-guard-2026-08-03.md",
    "integrity-guard-asks-wrong-question-2026-08-03.md",
    "one-key-pairing-2026-08-03.md",
    "pairing-audit-core-items-2026-08-04.md",
    "pairing-ux-final-spec-2026-08-04.md",
    "selfpair-review-2026-08-04.md",
    "zero-terminal-onboarding-2026-08-03.md",
}
_EXPECTED_UNNUMBERED = 8         # ★★★ 2026-08-15:**25 → 8**,收到实测值。
#   为什么收:第三次扩宽后这个数先涨到 **26**(判据第一次看得见元信息行里的 D-问号),
#   逐份处置完回落到 **8**。★ 若把上界留在 25,它就**再也拦不住任何东西** ——
#   一张永远够用的上界,和没有上界是一回事。
#   ⇒ 收到 8:下一份混进来的会**同时**撞上「不许出现在表外」与这条上界,两层都红。
# ── 以下为上一版的理由,保留(它当时是对的)──
# _EXPECTED_UNNUMBERED = 25      # ★ 17 + 8。**这不是"表变长了"** ——
#   是前一版的取样口径把这 8 份排除在外,而它们一直在。**上界只许变短,而实测数今天是 8。**
#  ★★★ 2026-08-11:**这张表已经清空(17 → 0)**,而 `_KNOWN_UNNUMBERED` 里那 17 个名字
#    **有意留着不删** —— 它们现在是「曾经欠过、已经清掉」的记录,而不是豁免。
#    留着的代价是零(每份包的抬头都已指得出号或写了看得见的豁免,一份都不会再落进这张表);
#    ★ 而删掉的代价是:下一个人看不出这张表**曾经有过 17 份**,
#      也就看不出「只许变短」这条纪律**真的被兑现过一次**。
#    ⇒ 期望值 17 也不下调:它是**上界**,而实测 0 远在界内。
#    ★ 若将来真有新的落进来,它会**先撞上「不许出现在 _KNOWN_UNNUMBERED 之外」那条**,
#      而不是靠这个上界拦 —— 那条才是承重的。


def _in_main(rel: str) -> bool:
    """该文件在不在 `main` 的树里 —— 即「已并入」。读不出来一律当【不在】(fail-open 到不判红)。

    ★ 方向是有意选的:git 不可用时这条闸**不许**把每一份包都判红 ——
      一道在别人机器上恒红的闸,和没有闸的区别只是它还会消耗人的注意力。
    """
    import subprocess
    try:
        r = subprocess.run(["git", "cat-file", "-e", f"main:{rel}"],
                           cwd=str(REPO), capture_output=True, timeout=20)
        return r.returncode == 0
    except Exception:                                        # noqa: BLE001
        return False

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

    # ══════════════════════════════════════════════════════════════════════
    #  第二半:已并入 main、抬头却还挂着 D-问号 ⇒ 该取号而没取
    # ══════════════════════════════════════════════════════════════════════
    overdue: list[str] = []
    unnumbered_now: list[str] = []
    for f in files:
        txt = f.read_text(encoding="utf-8", errors="replace")
        if not _self_declares_draft(txt):     # ★ 抬头**或元信息行**里自陈未取号
            continue
        if _h1_names_real_number(txt):        # ★ 主标题指得出真号 ⇒ 已处置,放行
            continue
        if _DRAFT.search(txt):          # ★ 包里写了看得见的豁免 ⇒ 放行
            continue
        rel = f"00-docs/decision-packets/{f.name}"
        if not _in_main(rel):           # ★ 还在车道里 ⇒ 写 D-问号 是对的,不判红
            continue
        unnumbered_now.append(f.name)
        if f.name not in _KNOWN_UNNUMBERED:
            overdue.append(f.name)

    print(f"  ★ 该取号而没取(已并入 main、抬头仍挂着草案标记):"
          f"**{len(unnumbered_now)} / {_EXPECTED_UNNUMBERED}**（存量冻表,只许变短）")

    check("★★★ 已并入 main 的决议包,抬头里不许还挂着草案标记(存量除外)",
          not overdue,
          f"{len(overdue)} 份该取号了:" + " ".join(overdue))

    check("★★ 该取号而没取的存量只许变短(变大 = 又有一份合进来却没取号)",
          len(unnumbered_now) <= _EXPECTED_UNNUMBERED,
          f"实测 {len(unnumbered_now)} 份 > 期望 {_EXPECTED_UNNUMBERED} 份")

    # ★ 元断言:判据没有静默失灵。`_in_main` 恒 False(git 不可用)会让这条闸整段静默跳过,
    #   而那与"全清白"长得一样。
    #
    # ★★★ 2026-08-11 修:**上一版这条元断言写错了。**
    #   它写成「`unnumbered_now` 至少要有一份,否则判探测坏了」——
    #   而那天把 17 份全清成 **0** 之后,它**当场误红**。
    #   ⇒ 它把「探测器活着」和「探测器找到了东西」混成了一件事。
    #   ★ **零命中在这里恰恰是目标状态** —— 一条到达了目标的闸,不该因为"没东西可报"而红。
    #   (与「零命中不是不存在」是同一枚硬币的**另一面**:
    #    那一面说"找不到别当成没有",这一面说"找不到也别当成坏了" ——
    #    两面的共同点是:**要去问探测器本身,而不是数它的产出**。)
    #   ⇒ 改成拿一个**已知在 main 里**的文件去问 `_in_main`,与包的数目无关。
    _probe = "00-docs/DECISIONS.md"
    check("★★ 元断言:「已并入 main」这条探测确实工作(拿一个已知在 main 里的文件去问它)",
          _in_main(_probe),
          f"`git cat-file -e main:{_probe}` 答不出 ⇒ git 不可用,这条闸此刻是静默跳过的")

    if overdue:
        print()
        print("  ── 该取号了(已并入 main,抬头仍是草案)──")
        for n in overdue:
            print(f"      {n}")
        print("  ★ 两条出路:① 由第 0 条车道并入 DECISIONS.md 并取号(D82:取号在并入那一刻);")
        print("    ② 它确实还是草案 ⇒ 在**那份包里**写一行看得见的豁免:  DRAFT-D: <号或 none>")

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
