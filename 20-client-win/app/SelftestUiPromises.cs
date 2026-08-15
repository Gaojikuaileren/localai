// V24 -- 界面文案「指路」护栏。
//
// ══════════════════════════════════════════════════════════════════════════════
//  元断言:**凡界面文案里用「」指名一个控件或页面,那个东西必须存在。**
//
//  ★★★ 为什么这个文件必须存在 —— 它已经咬到用户了:
//    2026-08-07 实机反馈「副机需要打开设置下滑到底才可以开始连接」。
//    根因不是交互设计,是 `ChatView` 那句「到『设备』里完成配对」——
//    **客户端已经没有「设备」这一页了**(MainWindow.xaml.cs:528「设备/配对已并入设置」)。
//    人照着一句指向虚空的话找入口,只好把设置从头翻到底。
//    ★ 判词:**给错原因的提示比不给提示更坏。** 它把人引向错的方向,
//      而「没有提示」至少让人去问。
//
//  ★★ 同一族当天一共量出 9 处,没有一处会让任何断言变红:
//    · 「设备」页 —— 已经不存在(客户端 4 处 + 管理端 1 处);
//    · 「接受」  —— **从来没有过**;主机侧的键叫「词一致,批准」(拒绝键叫「拒绝」);
//    · 「开始配对」—— 曾经存在,`fb59d4e` 把它改成了「开始寻找主机」,
//                    同一提交删了一句配套文案、**漏掉另外三句**;
//    · 「＋ 添加一台新电脑」—— 存在,但**在另一个 exe 里**(管理端),而文案写的是「客户端」;
//    · 「创建标注框」—— 2026-08-02 已删,而弹窗里还让人「先用它手动圈」;
//    · 「设置 · 中枢」—— 方向对、名字屏幕上没有。
//    ⇒ 编译、行为、既有的两千多条结构断言,一个都抓不到这一族:
//      **一句字符串里指名的东西存不存在,只有把两边对起来才知道。**
//
//  ══════ 判据的形状(为什么是这一个)════════════════════════════════════════
//   ① 从两个 exe 的**编译集**里各抽一份「屏幕上真的会出现的词」;
//   ② 从同样的编译集里抽出所有 `动词 +「短语」` 的**指路**;
//   ③ 每一条指路的短语,必须落在**它自己那个 exe** 的词表里 ——
//      落在**另一个** exe 的词表里时,这句话必须**说清要去那个程序**
//      (客户端指管理端要出现「管理端」,反之要出现「客户端」)。
//   ★★ 两张表**分开**,不合并:合成一张的话,「＋ 添加一台新电脑」那一条会绿 ——
//      而它恰恰是「东西在,只是不在你手上这台程序里」的那种错,
//      是本族里**最难自己发现**的一种(人会一直翻,因为那个名字确实存在过)。
//   ★★★ 词表按 **csproj 的编译集** 建,不按目录:
//      `app\Services\HostSetup.cs` 与 `GpuWire.cs` 被 `localai-admin.csproj`
//      用 `<Compile Link>` 编进管理端 ⇒ **同一句话要在两个 exe 里都成立**。
//      按目录建表会把它们只算成客户端的,于是「一句话改完了事」把管理端说错了也不红。
//
//  ══════ 本仓踩过的坑,这里逐条堵上 ════════════════════════════════════════
//   · **不依赖 markdown / 排版**:判据读的是 C# 源码里的**字符串字面量**,
//     不是文档、不是注释、也不看缩进(ASSERTION-PITFALLS 第 14 条那一类)。
//   · **不用 grep -c 数个数**:每一条指路**逐条解析**并给出自己的判词;
//     数个数会命中别处同名的字面量,而个数对不上时你还不知道是哪一条。
//   · **注释不算**(踩过三次):扫描器把 `//` 与 `/* */` 全剔掉再找字面量 ——
//     否则「★『创建标注框』按钮已删」那句**解释它已经没了的注释**,
//     会被当成「这个控件存在」的证据,判据当场自我抵消。
//   · **零命中要判红**:零命中与全清白长得一模一样。下面有一条元断言钉着
//     「抽出来的指路条数」与「两张词表的大小」,提取器坏掉时红的是它,
//     而不是安静地宣布全仓无罪。
//   · **`+` 拼起来的一句话要先拼回去**:文案在源码里常写成
//     `"…到主机那台的管理端" + "「主机中枢」页上…"`。逐个字面量看的话,
//     跨 exe 那条判据会因为「管理端」和「主机中枢」不在同一个字面量里而误红。
//   · **发布产物里没有源码**:那一趟整段 SKIP(第 11 条),并把 SKIP 的理由印出来 ——
//     但**只在源码根真的找不到时**;找得到却抽不出东西,是红,不是跳过。
// ══════════════════════════════════════════════════════════════════════════════

using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAI.Client;

public static class SelftestUiPromises
{
    // ────────────────────────────────────────────────────────── 入口
    /// <summary>
    /// 由 <c>Selftest.Run()</c> 调一行。<paramref name="assert"/> 就是那边的局部 <c>Assert</c> ——
    /// PASS/FAIL 折进**同一对计数器**,红了就是 `PASS=n FAIL=m` 里真实的一条。
    ///
    /// <para>★★★ 异常在这里**自己兜住**,绝不抛出去:抛出去的话 `Selftest.Run` 的 try 会
    /// 提前结束,后面两千多条断言一条都不跑,汇总行变成「客户端自检没跑起来」——
    /// 那正是这道护栏要防的**「红得理由是假的」**换个位置又发生一次
    /// (2026-08-09 刚给管理端自检修掉同一个形状)。
    /// ⇒ 判据没跑成时,红的是**一条说清「判据自己炸了」的断言**,而不是整趟自检。</para>
    /// </summary>
    public static void Run(Action<bool, string> assert)
    {
        Console.WriteLine("\n-- 界面指路(「」里指名的控件/页面必须真的存在)--");
        try
        {
            RunCore(assert);
        }
        catch (Exception ex)
        {
            assert(false,
                "★★★ 界面指路护栏**自己炸了**:" + ex.GetType().Name + ": " + ex.Message
                + " —— 这一条红的意思是【判据没跑成】,**不是**【文案没问题】。"
                + "★ 兜住异常是有意的:让它抛出去会把后面两千多条断言一起带走,"
                + "汇总就成了「客户端自检没跑起来」,而真正的原因埋在这儿。");
        }
    }

    // ────────────────────────────────────────────────────────── 登记表(只许变短)
    /// <summary>
    /// **已知坏、但不在本车道**的指路。★ 登记不是豁免:
    /// 每一条都会在每次自检时印出来,而且**必须仍然坏着** —— 修好了却没从表里删掉,
    /// 下面那条「登记表只许变短」当场红。⇒ 这张表只能缩,不能悄悄长。
    /// </summary>
    static readonly (string File, string Phrase, string Why)[] KnownBroken =
    {
        // V24 车道禁区:`admin/**`(协调层另派)。三处都指着**客户端**里已经不存在的东西。
        ("HostHubView.cs", "设备",
         "admin/Views/HostHubView.cs:654 —— 「打开这台的客户端 →『设备』」,而客户端没有「设备」页了"),
        ("HostHubView.cs", "开始配对",
         "admin/Views/HostHubView.cs:829 与 :912 —— 让对方点「开始配对」,而那颗按钮 fb59d4e 起就叫「开始寻找主机」"),
        // `20-client-win/transport/**` 是另一条车道的地盘(V25 在动 HubClient 一线)。
        ("TlsFailure.cs", "重新配对",
         "20-client-win/transport/TlsFailure.cs:246 —— 「不要点『重新配对』」,而屏幕上没有这个键;"
         + "客户端那一侧真正的键叫「解除本机配对」(重配 = 先解除再配一次)"),
    };

    /// <summary>
    /// **不是本产品的控件**、或**由拼接得来**的短语。★ 同样只许变短:
    /// 登记了却已经不在文案里的,下面那条元断言会红,逼着人回来把表清干净。
    /// </summary>
    static readonly (string Phrase, string Why)[] Exempt =
    {
        ("任务管理器 › 启动应用", "Windows 自己的页面,不是本产品的控件(SettingsView 说自启被系统禁用时用)"),
        ("设置 › 应用",           "Windows 设置里的卸载入口,不是本产品的控件(SettingsView 卸载说明)"),
        ("标记为 已完成",         "由 `\"标记为 \" + label` 拼出(Views/ProjectUi.cs:212 的右键菜单项),"
                                + "拼件都在;整条字面量不存在是**写法**问题,不是指了个不存在的东西"),
    };

    // ────────────────────────────────────────────────────────── 主体
    static void RunCore(Action<bool, string> assert)
    {
        var clientRoot = ClientSourceRoot();
        if (clientRoot is null)
        {
            // ★ 发布产物旁边没有源码 —— 这**不是**错误(第 11 条)。但要把跳过的事实说出来:
            //   不说的话,「跳过了」与「全绿」在输出里长得一模一样。
            // ★★ V36:走 Selftest.Skip —— 此前这里是裸 Console.WriteLine,
            //   客户端哨兵又没有 SKIP 字段 ⇒ 这行字**两条门禁都看不见**(第 19 条那个形状)。
            Selftest.Skip("界面指路护栏",
                          "找不到客户端源码根(发布产物形态)—— "
                          + "本次【一条指路都没查过】,别把这趟的 PASS 读成「文案没问题」");
            return;
        }

        var adminRoot = Path.GetFullPath(Path.Combine(clientRoot, "..", "admin"));
        var adminProj = Path.Combine(adminRoot, "localai-admin.csproj");
        // ★ 客户端源码根在、管理端却不在 —— 这是**结构性意外**(仓库形态变了),不是发布形态。
        //   判红而不是跳过:跨 exe 那一半的判据没有管理端就是空的,而空表会让
        //   「＋ 添加一台新电脑」那类错**恰好绿**。
        if (!File.Exists(adminProj))
        {
            assert(false, "★★ 找到了客户端源码根却找不到管理端工程(" + adminProj + ")—— "
                        + "跨 exe 对表这一半没有第二张表就是空的,而空表会让「东西在另一个程序里」那类错恰好绿");
            return;
        }

        var clientFiles = CompileSet(clientRoot, Path.Combine(clientRoot, "localai-client.csproj"));
        var adminFiles = CompileSet(adminRoot, adminProj);

        // ★ 自检文件与诊断工具**不是界面**:它们的断言判词里满是「」,算进来会把判据变成噪声。
        //   ⇒ 两侧都剔掉(admin 那边的 Selftest*.cs 同理)。
        static bool IsUi(string p)
        {
            var n = Path.GetFileName(p);
            return !n.StartsWith("Selftest", StringComparison.Ordinal) && n != "WheelTest.cs";
        }
        clientFiles = clientFiles.Where(IsUi).ToList();
        adminFiles = adminFiles.Where(IsUi).ToList();

        var strings = LoadStrings(Path.Combine(clientRoot, "I18n", "strings.json"));
        var clientWords = ScreenWords(clientFiles, strings);
        var adminWords = ScreenWords(adminFiles, strings);

        Console.WriteLine($"  (编译集:客户端 {clientFiles.Count} 个源文件 / {clientWords.Count} 个屏幕词;"
                        + $"管理端 {adminFiles.Count} / {adminWords.Count})");

        // ═══════════════════════════════════════════════════════════════════
        //  元断言组:先证明**这套提取器今天还活着**。
        //  ★ 没有这一组,提取器坏掉的那天下面全部为空 ⇒ 一条红都没有 ⇒
        //    输出与「全仓文案都对」逐字相同。零命中与全清白长得一模一样。
        // ═══════════════════════════════════════════════════════════════════
        assert(clientFiles.Count >= 60 && adminFiles.Count >= 10,
            $"★★ 元断言:两个 exe 的编译集都解析出来了(客户端 {clientFiles.Count} · 管理端 {adminFiles.Count})—— "
            + "解析不出来的话下面每一条都是在对着空表打分");
        assert(clientWords.Count >= 500 && adminWords.Count >= 150,
            $"★★ 元断言:两张【屏幕词】表都不空(客户端 {clientWords.Count} · 管理端 {adminWords.Count})—— "
            + "空表会让**每一条**指路都判红,满表会让每一条都判绿,两头都是假消息");

        // ★ 扫描器的**行为**定标:拿三份合成源码跑一遍,证明它做的正是判词说的那三件事。
        //   ★★ 这一组不依赖本仓的任何内容 —— 它测的是判据本身,不是被判的对象。
        SelfCheckScanner(assert);

        // ═══════════════════════════════════════════════════════════════════
        //  正题:逐条指路对表
        // ═══════════════════════════════════════════════════════════════════
        var hits = new List<Pointer>();
        hits.AddRange(Pointers(clientFiles, "客户端"));
        hits.AddRange(Pointers(adminFiles, "管理端"));

        assert(hits.Count >= 20,
            $"★★★ 元断言:真的抽出了指路(实得 {hits.Count} 条)—— "
            + "**零命中要判红**:提取器坏掉的那一天,「一条都没抽到」与「一条都没错」"
            + "在输出里长得一模一样,而前者是判据死了、后者才是文案对了");

        var broken = new List<string>();          // 没登记的坏指路 —— 这就是要红的那些
        var usedKnown = new HashSet<int>();
        var usedExempt = new HashSet<int>();
        var okCount = 0;

        foreach (var h in hits)
        {
            var ei = Array.FindIndex(Exempt, e => e.Phrase == h.Phrase);
            if (ei >= 0) { usedExempt.Add(ei); continue; }

            var mine = h.Exe == "客户端" ? clientWords : adminWords;
            var other = h.Exe == "客户端" ? adminWords : clientWords;
            var otherName = h.Exe == "客户端" ? "管理端" : "客户端";

            string? why = null;
            if (mine.Contains(h.Phrase))
            {
                okCount++;
            }
            else if (other.Contains(h.Phrase))
            {
                // ★ 东西在,但**在另一个 exe 里**。这不必然是错 —— 跨机器指路本来就要这么写。
                //   要求只有一条:这句话得**说出去哪个程序**,否则人会在手上这台里一直翻。
                if (h.Sentence.Contains(otherName, StringComparison.Ordinal)) okCount++;
                else why = $"「{h.Phrase}」在{otherName}里,而这句话没说要去{otherName} —— "
                         + "人会在手上这台程序里一直找一个不在这儿的东西";
            }
            else
            {
                why = $"「{h.Phrase}」**两个 exe 里都没有** —— 界面指名了一个不存在的东西";
            }

            if (why is null) continue;

            var ki = Array.FindIndex(KnownBroken, k => k.File == h.File && k.Phrase == h.Phrase);
            if (ki >= 0) { usedKnown.Add(ki); continue; }
            broken.Add($"[{h.Exe}] {h.File}:{h.Line} {why}");
        }

        // ★★★ 本护栏的**判词那一条**。
        assert(broken.Count == 0,
            "★★★★ 界面文案里用「」指名的控件/页面,**每一个今天都真的存在**"
            + $"(查了 {hits.Count} 条指路,{okCount} 条对上)"
            + (broken.Count > 0
                ? "\n        —— 下面这 " + broken.Count + " 条指着不存在的东西:\n        "
                  + string.Join("\n        ", broken)
                  + "\n        ★ 判词:给错原因的提示比不给提示更坏。"
                  + "先确认「用户照这句话该走的那条路今天长什么样」,再改字面。"
                : ""));

        // ═══════════════════════════════════════════════════════════════════
        //  两张登记表:只许变短
        // ═══════════════════════════════════════════════════════════════════
        foreach (var (i, k) in KnownBroken.Select((k, i) => (i, k)))
            Console.WriteLine($"  !     已登记(不是绿灯,是欠债):「{k.Phrase}」 {k.Why}");

        var staleKnown = Enumerable.Range(0, KnownBroken.Length).Where(i => !usedKnown.Contains(i))
            .Select(i => KnownBroken[i].File + " 「" + KnownBroken[i].Phrase + "」").ToList();
        assert(staleKnown.Count == 0,
            "★★ 「已知坏」登记表**只许变短**:表里每一条今天都还坏着"
            + (staleKnown.Count > 0
                ? " —— 这些已经不坏了(或者文件/短语改了名),把它们从表里删掉:"
                  + string.Join("、", staleKnown)
                : ""));

        var staleExempt = Enumerable.Range(0, Exempt.Length).Where(i => !usedExempt.Contains(i))
            .Select(i => "「" + Exempt[i].Phrase + "」").ToList();
        assert(staleExempt.Count == 0,
            "★★ 豁免表**只许变短**:表里每一条今天都还被用着"
            + (staleExempt.Count > 0
                ? " —— 这些已经没有对应文案了,删掉它们(留着等于给后来的同名短语开后门):"
                  + string.Join("、", staleExempt)
                : ""));

        // ═══════════════════════════════════════════════════════════════════
        //  锚点:证明这条判据**真的会红**
        //  ★ 红测(交回里跑过):把 `Ui.Primary("开始寻找主机")` 改个名而**不动文案**
        //    ⇒ 它掉出客户端词表 ⇒ 指着它的那三句话立刻变成「两个 exe 里都没有」⇒ 上面那条红。
        //    这两条锚点是那次红测的**常驻替身**:锚点没了,红测就悄悄失效了。
        // ═══════════════════════════════════════════════════════════════════
        assert(clientWords.Contains("开始寻找主机")
               && hits.Any(h => h.Exe == "客户端" && h.Phrase == "开始寻找主机"),
            "★★★ 锚点:「开始寻找主机」既是客户端**真的按钮**、也**被文案指着** —— "
            + "这两半同时在,才说明「改按钮名字而不改文案 ⇒ 必红」这条路是通的");

        // ★ 跨 exe 那一半也要有活锚点,否则「两张表分开」这件事没有任何东西证明。
        assert(adminWords.Contains("主机中枢") && !clientWords.Contains("主机中枢")
               && hits.Any(h => h.Exe == "客户端" && h.Phrase == "主机中枢"),
            "★★★ 锚点:「主机中枢」**只在管理端**、而客户端的文案在指它 —— "
            + "两张表要是被合成一张,这一条的后半截当场为假;"
            + "而合表之后「＋ 添加一台新电脑」那一族的错会全部转绿");
    }

    // ────────────────────────────────────────────────────────── 扫描器定标
    /// <summary>
    /// 拿**合成源码**验扫描器的三件事。★ 不用本仓文件:那样测的是「今天的仓库长什么样」,
    /// 而这里要测的是「这套提取器做的是不是判词说的那件事」。
    /// </summary>
    static void SelfCheckScanner(Action<bool, string> assert)
    {
        // ① 注释里的指路**不算**。踩过三次的那个坑:解释「它已经被删了」的注释,
        //    会被当成「它还在」的证据,判据自我抵消。
        const string commented = "class X { // 到「幽灵页」里看看\n void F() { var a = 1; } }";
        assert(Pointers(new[] { ("c.cs", commented) }, "测").Count == 0,
            "★★ 定标:注释里的「」**不算**指路(踩过三次的那个坑:"
            + "解释『它已经删了』的注释会被读成『它还在』)");

        // ② 长句里出现的「X」**不算**「屏幕上有个叫 X 的东西」。
        //    这一条是整套判据的地基:换成 Contains 的话,
        //    「★ 配对审批与设备管理只在主机那台上…」这句话就会让「设备」页凭空存在。
        const string prose = "class X { void F() { Ui.Caption(\"★ 配对审批与设备管理只在主机那台上\"); } }";
        var proseWords = ScreenWordsFrom(new[] { ("p.cs", prose) }, new Dictionary<string, string>());
        assert(!proseWords.Contains("设备") && proseWords.Contains("★ 配对审批与设备管理只在主机那台上"),
            "★★★ 定标:词表按**整条字面量**建,不按 Contains —— "
            + "否则任何一句提到某个词的长文案,都会让那个词凭空『存在』,"
            + "而这正是「设备」页那一条**本该早就红**却没红的原因");

        // ③ `+` 拼起来的一句话要拼回去再判。跨 exe 那条判据靠它:
        //    「…管理端…」与「「主机中枢」…」常常分在两个字面量里。
        const string joined = "class X { void F() { Ui.Body(\"到管理端里进\" + \"「主机中枢」那一页\"); } }";
        var jp = Pointers(new[] { ("j.cs", joined) }, "测");
        assert(jp.Count == 1 && jp[0].Phrase == "主机中枢" && jp[0].Sentence.Contains("管理端"),
            "★★ 定标:`\"a\" + \"b\"` 要先拼回一句再判 —— "
            + "不拼的话,跨 exe 那条判据会因为「管理端」和短语不在同一个字面量里而**误红**");

        // ④ 单引号字符字面量里的引号不能把扫描器带沟里(`case '\"':` 这类解析代码里到处都是)。
        const string charlit = "class X { void F() { var q = '\\\"'; Ui.Body(\"点「开始寻找主机」\"); } }";
        var cp = Pointers(new[] { ("q.cs", charlit) }, "测");
        assert(cp.Count == 1 && cp[0].Phrase == "开始寻找主机",
            "★★ 定标:`'\\\"'` 这类字符字面量不会让扫描器失步 —— "
            + "失步之后它会把大段源码当成文案,而结果看起来只是「多了几条奇怪的指路」");

        // ⑤ ★★ 内插洞里嵌着的字符串**不能**把扫描器带沟里。DevicesView 里就有真货:
        //    `$"状态:{Strings.Get(… => "status.online" …)}"`。失步的方向是**把源码当文案**,
        //    于是词表变宽 —— 而词表一宽,该红的指路就转绿了。
        const string hole = "class X { void F() { Ui.Body($\"状态:{G(s switch { A => \"k.on\", _ => \"k.off\" })}\");"
                          + " Ui.Body(\"到「幽灵页」看\"); } }";
        var hp = Pointers(new[] { ("h.cs", hole) }, "测");
        var hw = ScreenWordsFrom(new[] { ("h.cs", hole) }, new Dictionary<string, string>());
        assert(hp.Count == 1 && hp[0].Phrase == "幽灵页" && !hw.Any(w => w.Contains("switch")),
            "★★★ 定标:内插串的洞整段跳过,扫描器不因洞里的引号失步 —— "
            + "失步之后它会把大段源码塞进屏幕词表,而**词表一宽,该红的指路全转绿**");

        // ⑥ ★★ 原始字符串字面量(`\"\"\"…\"\"\"`)。本仓真的有一条(Wordlist.cs 的 SAS 词表,
        //    编进客户端)—— 这一条最初是被一条元断言**红出来**的,修法是把扫描器教会。
        //    ★ 它的危害与 ⑤ 同向:引号奇偶一乱,后面的源码整段变成"文案"。
        const string rawlit = "class X { const string P = \"\"\"\n a \" b 到「幽灵页」\n\"\"\"; void F() { Ui.Body(\"点「开始寻找主机」\"); } }";
        var rp = Pointers(new[] { ("r.cs", rawlit) }, "测");
        assert(rp.Count == 2 && rp[1].Phrase == "开始寻找主机",
            "★★★ 定标:原始字符串字面量整条读掉,后面的文案照样认得出来 —— "
            + "不认得它的话扫描器从那里开始失步,而结果只是**安静地多绿几条**");
    }

    // ────────────────────────────────────────────────────────── 源码根 / 编译集
    /// <summary>
    /// 客户端源码根。★★★★ V36:**不再自带一份** —— 直接用 <c>Selftest.ClientSourceRoot()</c>。
    ///
    /// <para>★ 上一版这里写着「重写一份而不是复用,是因为那个方法是 `Selftest.cs` 的私有成员,
    /// 而 `Selftest.cs` 是本车道的禁区(V26 在追加)」—— 那个理由**当时成立**,
    /// 而它留下的后果一直没人算过:`Selftest.cs` 里那条「源码根锚点只许有一处」
    /// **只读它自己那个文件** ⇒ 报「1 处」判绿,而工程里真实是 **3 处**
    /// (本文件 + `SelftestModelGate.cs`)。**判据只盖住了它自称范围的一个文件**(第 3b 条)。
    /// V36 拥有全部 `Selftest*.cs`,禁区不再存在 ⇒ 三处收拢,并把那条判据的范围拓到整个编译集。</para>
    ///
    /// <para>★★ 顺带丢掉的第三锚点(`Views\DevicesView.cs`):锚点越多「找不到源码根」越容易发生,
    /// 而找不到的后果是**整段跳过** —— 那是 fail-open 的方向(第 11 条)。
    /// 解错树的那一半仍然守得住:下面两条元断言(编译集 ≥60 个源文件 · 屏幕词 ≥500)
    /// 在一棵不相干的树上必然红。</para>
    /// </summary>
    static string? ClientSourceRoot() => Selftest.ClientSourceRoot();

    /// <summary>
    /// 一个 exe **真正会编译**的 .cs 全集 = 工程目录下的隐式 glob + csproj 里逐条 `&lt;Compile Include&gt;`。
    /// ★ 必须按编译集,不能按目录:`app\Services\HostSetup.cs` / `GpuWire.cs` / `Views\Ui.cs` 等
    /// 被管理端 `&lt;Compile Link&gt;` 编了同一份 —— 按目录建表的话,它们里面那句话只被当成
    /// 客户端的,于是「一句话改完把管理端说错了」这种错不会红。
    /// </summary>
    static List<string> CompileSet(string projDir, string csprojPath)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(projDir, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(projDir, f);
            if (rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(f)) files.Add(f);
        }
        foreach (Match m in Regex.Matches(File.ReadAllText(csprojPath), "<Compile\\s+Include=\"([^\"]+\\.cs)\""))
        {
            var p = Path.GetFullPath(Path.Combine(projDir, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar)));
            if (File.Exists(p) && seen.Add(p)) files.Add(p);
        }
        return files;
    }

    static Dictionary<string, string> LoadStrings(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return map;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var kv in doc.RootElement.EnumerateObject())
            if (kv.Value.ValueKind == JsonValueKind.Object
                && kv.Value.TryGetProperty("zh-CN", out var zh) && zh.GetString() is { Length: > 0 } s)
                map[kv.Name] = s;
        return map;
    }

    // ────────────────────────────────────────────────────────── 屏幕词表
    /// <summary>
    /// 「这个 exe 的屏幕上真的会出现的词」。★ 口径**有意宽**:整条字面量、不超过 30 个字、
    /// 不含内插占位符的,一律算数(按钮、菜单项、小标题、页签、托盘项…… 在这个仓里
    /// 由十几种不同写法构造出来,逐种枚举必然漏,而**漏了就是误红**,误红的护栏会被人关掉)。
    /// <para>★★ 宽到这个程度仍然抓得住这一族,是因为出事的那些短语在源码里
    /// **一次都没有作为整条字面量出现过** —— 「设备」「接受」「开始配对」「创建标注框」
    /// 全都只活在**别的句子当中**或**只活在注释里**。⇒ 判据取「整条相等」,
    /// 而**不是** Contains:后者会让任何一句提到它的长文案把它变成「存在」。</para>
    /// <para>★ 另外收进 `Strings.Get("k")` / `NavItem(…, "k", …)` 在本编译集里**真的被引用**
    /// 的三语键(取 zh-CN)。只收被引用的:`strings.json` 里 `pairing.start`(=「开始配对」)
    /// 是一把**没有任何调用点的死钥匙**,把它算成「屏幕上有」的话,
    /// 「回来再点一次『开始配对』」那一条恰好绿 —— 而那正是要抓的错。</para>
    /// </summary>
    static HashSet<string> ScreenWords(IEnumerable<string> files, Dictionary<string, string> strings) =>
        ScreenWordsFrom(files.Select(f => (Path.GetFileName(f), File.ReadAllText(f))), strings);

    static HashSet<string> ScreenWordsFrom(IEnumerable<(string Name, string Src)> files,
                                           Dictionary<string, string> strings)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, src) in files)
        {
            foreach (var lit in Scan(src))
            {
                var t = lit.Text.Trim();
                if (t.Length is > 0 and <= 30 && !t.Contains('{')) words.Add(t);
            }
            // ★ 三语键**只要在本编译集里被写出来过**,它的 zh-CN 就会上屏 —— 不管取法是
            //   `Strings.Get("k")`、`NavItem(…, "k", …)`,还是 `switch` 里的一支
            //   (`HubState.Online => "status.online"` 之后整块交给 `Strings.Get`)。
            //   ⇒ 认「长得像键的字面量」而不是认某一种取法:逐种枚举必然漏,而漏了就是误红。
            // ★★ 反过来**不收** `strings.json` 里没被引用的键:`pairing.start`(=「开始配对」)
            //   是一把**零调用点的死钥匙**,收了它「再点一次『开始配对』」就恰好绿 —— 那正是要抓的错。
            var code = NoCommentLines(src);
            foreach (Match m in Regex.Matches(code, "\"([a-z][a-z0-9_]*(?:\\.[a-z0-9_]+)+)\""))
                if (strings.TryGetValue(m.Groups[1].Value, out var zh)) words.Add(zh.Trim());
        }
        return words;
    }

    static string NoCommentLines(string src) =>
        string.Join("\n", src.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    // ────────────────────────────────────────────────────────── 指路提取
    sealed record Pointer(string Exe, string File, int Line, string Phrase, string Sentence);

    /// <summary>
    /// 指路 = 「短语」**紧跟在一个动作词后面**。
    /// <para>★ 不是「所有的『』都得是控件名」:中文里「」也用来强调
    /// (「共用这台中枢的这群人」「每台设备」「协议对不上」)。判据只咬**祈使**那一种 ——
    /// 「点/按/展开/打开/进/去/到/回到/选/勾/见/用/在/→」加一个「」。
    /// 那是唯一一种「照着做」的句子,也是唯一一种指错了会把人支到虚空里的句子。</para>
    /// <para>★ 含 `{}` 的短语跳过:那是把数据填进去的占位符(「{s.Title}」是会话标题,
    /// 不是控件名),对它们对表只会得到噪声。</para>
    /// </summary>
    static readonly string[] Verbs =
        { "点一下", "点一次", "再点", "点开", "点", "按", "展开", "打开", "进", "去", "到", "回到",
          "选中", "选", "勾", "见", "用", "在", "→", "›" };

    static List<Pointer> Pointers(IEnumerable<string> files, string exe) =>
        Pointers(files.Select(f => (Path.GetFileName(f), File.ReadAllText(f))), exe);

    static List<Pointer> Pointers(IEnumerable<(string Name, string Src)> files, string exe)
    {
        var outp = new List<Pointer>();
        foreach (var (name, src) in files)
            foreach (var lit in Scan(src))
            {
                var t = lit.Text;
                var from = 0;
                while (true)
                {
                    var open = t.IndexOf('「', from);
                    if (open < 0) break;
                    var end = t.IndexOf('」', open + 1);
                    if (end < 0) break;
                    var phrase = t[(open + 1)..end];
                    from = end + 1;
                    if (phrase.Length is 0 or > 30 || phrase.Contains('{') || phrase.Contains('「')) continue;
                    // ★ 动作词要**紧挨着**开引号。隔了字就不是祈使句了
                    //   (「…的『记忆库』…」是在说一件东西,不是在让人去点它)。
                    if (Verbs.Any(v => t.AsSpan(0, open).EndsWith(v)))
                        outp.Add(new Pointer(exe, name, lit.Line, phrase, t));
                }
            }
        return outp;
    }

    // ────────────────────────────────────────────────────────── 字面量扫描器
    readonly record struct Lit(int Line, string Text);

    /// <summary>
    /// 从 C# 源码里抽出**字符串字面量**,并把 `"a" + "b"` 这种**拼在一起的一句话拼回去**。
    /// 注释(`//` 与 `/* */`)与字符字面量(`'x'`)全部剔掉 —— 它们不是屏幕上的东西。
    /// <para>★ 拼接只跨过空白、换行、注释与那个 `+`:中间一旦出现别的东西
    /// (`,` `)` `;` 或一个变量),就当成另一句话。这与人读到的**一模一样**。</para>
    /// </summary>
    static List<Lit> Scan(string src)
    {
        var outp = new List<Lit>();
        int i = 0, n = src.Length, line = 1;
        string? pend = null; int pendLine = 0;
        void Flush() { if (pend is not null) { outp.Add(new Lit(pendLine, pend)); pend = null; } }

        while (i < n)
        {
            var c = src[i];
            if (c == '\n') { line++; i++; continue; }
            if (c is ' ' or '\t' or '\r') { i++; continue; }
            if (c == '+' && pend is not null) { i++; continue; }
            if (c == '/' && i + 1 < n && src[i + 1] == '/')
            {
                var j = src.IndexOf('\n', i); i = j < 0 ? n : j; continue;
            }
            if (c == '/' && i + 1 < n && src[i + 1] == '*')
            {
                var j = src.IndexOf("*/", i, StringComparison.Ordinal);
                var stop = j < 0 ? n : j + 2;
                for (var k = i; k < stop; k++) if (src[k] == '\n') line++;
                i = stop; continue;
            }
            if (c == '\'')
            {
                var j = i + 1;
                while (j < n && src[j] != '\'') { if (src[j] == '\\') j++; j++; }
                i = j + 1; continue;
            }
            if (c is '$' or '@')
            {
                int k = i; bool verbatim = false, interp = false;
                while (k < n && (src[k] == '$' || src[k] == '@'))
                { if (src[k] == '@') verbatim = true; else interp = true; k++; }
                if (k < n && src[k] == '"')
                {
                    var start = line;
                    var text = IsRaw(src, k)
                        ? ReadRaw(src, k, ref line, out var nx)
                        : ReadString(src, k, verbatim, interp, ref line, out nx);
                    if (pend is null) { pend = text; pendLine = start; } else pend += text;
                    i = nx; continue;
                }
                Flush(); i++; continue;
            }
            if (c == '"')
            {
                var start = line;
                var text = IsRaw(src, i)
                    ? ReadRaw(src, i, ref line, out var next)
                    : ReadString(src, i, verbatim: false, interp: false, ref line, out next);
                if (pend is null) { pend = text; pendLine = start; } else pend += text;
                i = next; continue;
            }
            Flush(); i++;
        }
        Flush();
        return outp;
    }

    /// <summary>
    /// 读一条字面量。<paramref name="at"/> 指着开头那个引号;返回内容,<paramref name="next"/> 是收尾之后的下标。
    ///
    /// <para>★★★ 内插串(<c>$"…{X}…"</c>)里的**洞要整段跳过**,而不是碰到引号就收尾 ——
    /// 洞里合法地嵌着别的字符串。本仓真实存在这种写法:
    /// <c>Ui.Body($"状态:{Strings.Get(TheApp.Hub.State switch { HubState.Online => "status.online", … })}")</c>。
    /// 不跳洞的话扫描器会在这里**失步**:此后一段时间里「代码」和「文案」整体对调,
    /// 于是一堆源码片段混进屏幕词表(而词表变宽 = 该红的指路转绿,是最坏的方向),
    /// 真正的文案却被当成代码丢掉。★ 跳洞时留下一个 `{}` 占位,
    /// 「」里含 `{` 的短语照旧被丢掉(那是数据,不是控件名)。</para>
    /// </summary>
    static string ReadString(string src, int at, bool verbatim, bool interp, ref int line, out int next)
    {
        var sb = new System.Text.StringBuilder();
        var j = at + 1;
        while (j < src.Length)
        {
            var ch = src[j];
            if (interp && ch == '{')
            {
                if (j + 1 < src.Length && src[j + 1] == '{') { sb.Append('{'); j += 2; continue; }   // `{{` = 一个花括号
                j = SkipHole(src, j, ref line);
                sb.Append("{}");
                continue;
            }
            if (verbatim)
            {
                if (ch == '"')
                {
                    if (j + 1 < src.Length && src[j + 1] == '"') { sb.Append('"'); j += 2; continue; }
                    break;
                }
                if (ch == '\n') line++;
                sb.Append(ch); j++; continue;
            }
            if (ch == '\\')
            {
                // ★ 只还原对判据有意义的两个:换行(文案里真的用 \n 分段)与转义引号。
                //   其余(\t \\ \uXXXX)整体跳过即可 —— 它们不会出现在「」里。
                if (j + 1 < src.Length && src[j + 1] == 'n') sb.Append('\n');
                else if (j + 1 < src.Length && src[j + 1] == '"') sb.Append('"');
                j += 2; continue;
            }
            if (ch is '"' or '\n') break;
            sb.Append(ch); j++;
        }
        next = j + 1;
        return sb.ToString();
    }

    /// <summary>三个及以上连续引号 = C# 11 原始字符串字面量的开头。</summary>
    static bool IsRaw(string src, int at) =>
        at + 2 < src.Length && src[at] == '"' && src[at + 1] == '"' && src[at + 2] == '"';

    /// <summary>
    /// 读一条原始字符串字面量(`"""…"""`)。收尾的引号数与开头**一样多**。
    /// <para>★ 本仓真的有一条:`10-core\identity\Wordlist.cs` 里 SAS 词表那 140 行
    /// (它被 `localai-client.csproj` 编进客户端)。★★ 不认得它的后果不是"少读一段"——
    /// 是**扫描器从那里开始失步**:引号奇偶一乱,此后大段源码被当成文案塞进屏幕词表,
    /// 而**词表一宽,该红的指路就全转绿**。这一条最初是靠一条「编译集里不许有 \"\"\"」
    /// 的元断言**红出来**的 —— 那条红是对的,修法是把扫描器教会,不是把断言删掉。</para>
    /// </summary>
    static string ReadRaw(string src, int at, ref int line, out int next)
    {
        var q = 0;
        while (at + q < src.Length && src[at + q] == '"') q++;
        var close = new string('"', q);
        var j = src.IndexOf(close, at + q, StringComparison.Ordinal);
        var stop = j < 0 ? src.Length : j + q;
        for (var k = at; k < stop; k++) if (src[k] == '\n') line++;
        next = stop;
        return j < 0 ? "" : src[(at + q)..j];
    }

    /// <summary>从内插洞的 `{` 跳到配对 `}` 之后。洞里的 `{}` 会嵌套,还会嵌字符串与字符字面量。</summary>
    static int SkipHole(string src, int at, ref int line)
    {
        int depth = 0, j = at;
        while (j < src.Length)
        {
            var ch = src[j];
            if (ch == '\n') { line++; j++; continue; }
            if (ch == '"')
            {
                j++;
                while (j < src.Length && src[j] != '"') { if (src[j] == '\\') j++; if (j < src.Length && src[j] == '\n') line++; j++; }
                j++; continue;
            }
            if (ch == '\'')
            {
                j++;
                while (j < src.Length && src[j] != '\'') { if (src[j] == '\\') j++; j++; }
                j++; continue;
            }
            if (ch == '{') { depth++; j++; continue; }
            if (ch == '}') { depth--; j++; if (depth <= 0) return j; continue; }
            j++;
        }
        return j;
    }
}
