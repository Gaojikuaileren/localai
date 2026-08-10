// V30 -- 【模型就绪闸】的护栏。
//
// ══════════════════════════════════════════════════════════════════════════════
//  元断言:**凡会发起模型调用的入口,都必须挂在同一个就绪闸上。**
//
//  ★★★ 为什么必须是一条**元**断言,而不是「给聊天加一条断言」:
//    用户裁定的原话是「**不仅仅是聊天功能,其他所有功能都是一样的,
//    按需的模型没起来就禁用**」。这句话的对象是**全集**,不是某一个入口。
//    ⇒ 逐个入口写断言,只能证明"今天这几个是对的";而明天新加的第四个入口
//      不会让任何东西变红 —— 那正是这条裁定最可能被悄悄破坏的方式。
//
//  ⇒ 判据取【全表对照】,与网关 `_check_component_bridge` 同一手法:
//     ① 从**编译集**里扫出所有真实的模型调用点(不是从注释、不是从文档);
//     ② 每一个都必须在 `ModelReadiness.CallSites` 里登记 —— 没登记 ⇒ 红;
//     ③ 每一条登记也必须还能在代码里找到 —— 发霉的登记 ⇒ 也红;
//     ④ 每条登记点名的那个 `GatedIn` 文件,必须真的读闸(`TheApp.Ready.Gate(`)。
//     ★ ③不可少:只有②的话,登记表会变成一张**只增不减**的赦免名单 ——
//       把入口删掉而登记还在,下一个人会以为那儿还挂着闸。
//
//  ══════ 本仓踩过的坑,这里逐条堵上 ════════════════════════════════════════
//   · **注释不算**(踩过三次):扫描前把 `//` 与 `/* */` 剔掉 ——
//     否则本文件头上这段**解释性**注释里的方法名,会被当成"存在一个调用点"的证据,
//     判据当场自我抵消。
//   · **零命中要判红**:提取器坏掉的那天,"一个调用点都没扫到"与"每个都挂了闸"
//     在输出里逐字相同。下面有一条元断言钉着扫出来的条数。
//   · **不用 grep -c 数个数**:每一条逐条解析、逐条给判词。
//   · **按 csproj 的编译集建表,不按目录**:`GpuWire.cs` 这类文件被两个工程编。
//   · **发布产物旁边没有源码**:那一趟整段 SKIP(第 11 条)并把理由印出来 ——
//     但只在源码根**真的找不到**时;找得到却抽不出东西,是红,不是跳过。
//   · **异常自己兜住**:抛出去会把 `Selftest.Run` 后面两千多条断言一起带走,
//     汇总变成「客户端自检没跑起来」——「红得理由是假的」换个位置又发生一次。
// ══════════════════════════════════════════════════════════════════════════════

using System.Text.RegularExpressions;
using LocalAI.Client.Services;

namespace LocalAI.Client;

public static class SelftestModelGate
{
    /// <summary>
    /// 由 <c>Selftest.Run()</c> 调一行。<paramref name="assert"/> 就是那边的局部 <c>Assert</c> ——
    /// PASS/FAIL 折进**同一对计数器**。★ 异常在这里自己兜住(理由见文件头)。
    /// </summary>
    public static void Run(Action<bool, string> assert)
    {
        Console.WriteLine("\n-- 模型就绪闸(会发起模型调用的入口,都得挂在同一个闸上)--");
        try
        {
            Behaviour(assert);      // ★ 先测判据本身:它是下面每一条的地基
            Wiring(assert);
        }
        catch (Exception ex)
        {
            assert(false,
                "★★★ 模型就绪闸护栏**自己炸了**:" + ex.GetType().Name + ": " + ex.Message
                + " —— 这一条红的意思是【判据没跑成】,**不是**【闸没问题】。");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  第一组:判据本体的行为。★ 喂形状进去,对答案 —— 与界面用的是**同一段代码**。
    // ══════════════════════════════════════════════════════════════════════
    static void Behaviour(Action<bool, string> assert)
    {
        // ---- 用户裁定点名的那两句文案,逐字钉住 ----
        assert(ModelReadiness.NotStartedText == "模型未启用",
            "★★ 气泡文案「模型未启用」逐字与裁定一致 —— 用户点名了这句话,改它要先改裁定");
        assert(ModelReadiness.StartingText == "正在启用中,请稍等",
            "★★ 气泡文案「正在启用中,请稍等」逐字与裁定一致");

        var ok = new IntentOutcome(true, "OK", ModelReadiness.ChatAlias, "llm.assistant.8b@8k", "", "transient");
        var noGpu = new IntentOutcome(true, "no_gpu_needed", ModelReadiness.ChatAlias, "", "", "");
        var bad = new IntentOutcome(false, "NOT_PERMITTED", ModelReadiness.ChatAlias, "llm.assistant.8b@8k", "", "");

        // ---- ① 没有中枢:压过一切,连"装着呢"也压 ----
        assert(ModelReadiness.Decide(paired: false, online: true, inFlight: true, last: ok,
                                     residentByCatalog: true).State == ModelReadyState.NotStarted,
            "★★★ 没配对 ⇒ 未启用 —— 连「中枢装着呢」都压过去:那份快照不可能是这台中枢的");
        assert(ModelReadiness.Decide(true, online: false, false, ok, true).State == ModelReadyState.NotStarted,
            "★★ 主机不在线 ⇒ 未启用(AI 在主机上跑,它得在线)");

        // ---- ② 中枢此刻真的装着 ⇒ 就绪,而且**不许**先闪一下"正在启用中" ----
        var residentWhileAsking = ModelReadiness.Decide(true, true, inFlight: true, last: null,
                                                       residentByCatalog: true);
        assert(residentWhileAsking.State == ModelReadyState.Ready,
            "★★★ 模型本来就在跑时,第一次敲字发出的那条意图**不许**把按钮先灰一下 —— "
            + "那是一次凭空的、假的「正在启用中」(证据②必须压过④)");

        // ---- ③ 上一次成功 + 快照说它不在了 = 被卸了 ⇒ 禁用 ----
        var unloaded = ModelReadiness.Decide(true, true, false, ok, residentByCatalog: false);
        assert(unloaded.State == ModelReadyState.NotStarted,
            "★★★★ 用户点名的那一格:「如果模型被卸了,也要同时禁用」");
        assert(unloaded.Why.Contains("被卸下") && unloaded.Why.Contains("打字"),
            "★★ 被卸的理由要说清**是被卸的**(不是从没起过)+ **下一步怎么办** —— "
            + "置灰而不说原因等于骗人,而给个错的原因比不给更坏");

        // ---- ★★★ 三值的那一格:false 与 null **绝不能**合并 ----
        assert(ModelReadiness.Decide(true, true, false, ok, residentByCatalog: null).State == ModelReadyState.Ready,
            "★★★ 快照读不到(null)≠ 明确没装(false):读不到时**保留中枢说过的那句「装上了」**。"
            + "合并的话,推送流每次重连(退避最长 30 秒)都会把发送键灭一次,"
            + "而那是一句我们**编出来的**「模型没起来」");

        // ---- ④ 正在路上 ----
        assert(ModelReadiness.Decide(true, true, inFlight: true, null, null).State == ModelReadyState.Starting,
            "★★ 意图在路上、又没有别的证据 ⇒ 正在启用中");
        assert(ModelReadiness.Decide(true, true, true, null, null).Headline == ModelReadiness.StartingText,
            "★ 「正在启用中」那一格给的就是裁定里那句气泡文案");

        // ---- ⑤ 失败:理由用中枢给的那一句 ----
        var failed = ModelReadiness.Decide(true, true, false, bad, null);
        assert(failed.State == ModelReadyState.NotStarted && failed.Why.Contains("允许按需装载"),
            "★★ 起不来时说**中枢给的那一句**(NOT_PERMITTED ⇒ 「去勾允许按需装载」),不是我们编的「起不来」");

        // ---- ⑥ 不占显存的别名:恒就绪,没有"被卸"这回事 ----
        assert(ModelReadiness.Decide(true, true, false, noGpu, residentByCatalog: false).State == ModelReadyState.Ready,
            "★★ no_gpu_needed 的别名不占显存 ⇒ 快照里找不到它是**正常**的,不该判成被卸");

        // ---- ⑦ 什么都没发生 ----
        var fresh = ModelReadiness.Decide(true, true, false, null, null);
        assert(fresh.State == ModelReadyState.NotStarted && fresh.Why.Contains("打字"),
            "★ 还没开始时说清「打个字就会自动开始」—— 否则用户不知道怎么让它起来");

        // ---- 「正在启用中」**不算**能用 ----
        assert(!ModelReadiness.Decide(true, true, true, null, null).CanUse,
            "★★★ 「正在启用中」不算能用 —— 那正是裁定点名要挡住的那一格"
            + "(「还没完全启用成功的时候……应该灰掉发送按钮并禁用」)");

        // ══════ 本机语音面:同一个闸,另一个证据源 ══════
        assert(ModelReadiness.IsLocalPlane(ModelReadiness.SpeechAsr)
               && !ModelReadiness.IsLocalPlane(ModelReadiness.ChatAlias),
            "★★ 语音走本机面(local:)、聊天走中枢面 —— 两者的证据源完全不同");
        assert(!ModelReadiness.DecideLocalSpeech(probed: false, null, "").CanUse,
            "★ 还没探过 ⇒ 不能用(「没探过」与「探过、没起来」是两件事,都不许当成能用)");
        var down = ModelReadiness.DecideLocalSpeech(true, null, "连不上本机语音服务(127.0.0.1:18085)");
        assert(down.State == ModelReadyState.NotStarted && down.Why.Contains("18085"),
            "★★ 连不上时把**探到的那句原因**原样给出去(端口都在里面),不换成一句笼统的「不可用」");
        assert(ModelReadiness.DecideLocalSpeech(true,
                   new SpeechHealth(false, "loopback", false, false, "asr 正在装"), "").State == ModelReadyState.Starting,
            "★★★ 服务起来了、模型还在装 ⇒ **正在启用中** —— 服务端自己就是这么说的"
            + "(未就绪时 /v1/speech/asr 回 503「还在装模型」)");
        assert(ModelReadiness.DecideLocalSpeech(true,
                   new SpeechHealth(true, "loopback", false, true, ""), "").State == ModelReadyState.Starting,
            "★★ 服务 ready 但 **asr 没装** ⇒ 仍然不能按住说话:这颗按钮要的是 ASR,不是 TTS");
        assert(ModelReadiness.DecideLocalSpeech(true,
                   new SpeechHealth(true, "loopback", true, true, ""), "").CanUse,
            "★ 服务 ready 且 asr 装好了 ⇒ 能用");

        // ---- ModelGate.Bubble:标题 + 理由,理由为空时不留孤零零的换行 ----
        assert(new ModelGate(ModelReadyState.Ready, "", "").Bubble == "",
            "★ 就绪时气泡是空的 —— 没有坏消息就不该占地方");
        assert(new ModelGate(ModelReadyState.NotStarted, "甲", "乙").Bubble == "甲\n乙",
            "★ 气泡 = 短标题 + 具体理由(两行)。合成一句的话,短的那半会被长理由挤没");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  第二组:接线。★ 全表对照 —— 这一组才是那条**元**断言。
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// **会真的把活儿交给模型**的调用点。★ 闭集 —— 新增一种必须显式加进来。
    ///
    /// <para>★★ 前面那个 <c>\.</c> 是判据的一部分,不是写法:它要的是**调用点**,
    /// 而不是**声明**。少了它,`ChatCenter.cs` 里那句
    /// <c>public async Task&lt;ChatOutcome&gt; SendAndAskAsync(</c>(方法自己的定义)
    /// 也会被扫成一个"没挂闸的入口" —— 而给一个方法的定义挂界面闸是讲不通的,
    /// 于是人只能往登记表里塞一条假条目。**一张掺了假的表,下一个人就不会再信它。**
    /// (第一版就是这么错的,扫出 8 处、5 处是假的。)</para>
    ///
    /// <para>★ 同一个 <c>\.</c> 顺带挡住 <c>ReadAsStreamAsync</c> / <c>OpenBusinessStreamAsync</c>:
    /// 它们的点后面跟的是别的名字,与本闭集对不上。</para>
    /// </summary>
    const string ModelCallPattern =
        @"\.(StreamAsync|SendAndAskAsync|TranscribeAsync|SynthesizeAsync)\s*\(";

    /// <summary>闸的读法。★ 登记里点名的 <c>GatedIn</c> 文件必须真的出现它。</summary>
    const string GateRead = "TheApp.Ready.Gate(";

    static void Wiring(Action<bool, string> assert)
    {
        var root = ClientSourceRoot();
        if (root is null)
        {
            // ★ 发布产物旁边没有源码 —— 不是错误(第 11 条)。但要把跳过的事实说出来:
            //   不说的话,「跳过了」与「全绿」在输出里长得一模一样。
            Console.WriteLine("  SKIP  模型就绪闸接线:找不到客户端源码根(发布产物形态)—— "
                            + "本次【一个入口都没查过】,别把这趟的 PASS 读成「闸都挂好了」");
            return;
        }

        var files = CompileSet(root, Path.Combine(root, "localai-client.csproj"))
            // ★ 自检文件自己不算:本文件里就写着那几个方法名(上面那个正则、还有判词),
            //   算进来会让判据把**自己**当成一个没挂闸的模型入口。
            .Where(p => !Path.GetFileName(p).StartsWith("Selftest", StringComparison.Ordinal))
            .ToList();

        assert(files.Count >= 60,
            $"★★ 元断言:客户端编译集解析出来了({files.Count} 个源文件)—— "
            + "解析不出来的话,下面每一条都是在对着空表打分");

        // ---- 扫真实调用点 ----
        var hits = new List<(string File, string Call, int Line)>();
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            var src = CodeOnly(File.ReadAllText(f));   // ★ 注释剔掉 —— 本仓踩过三次
            foreach (Match m in Regex.Matches(src, ModelCallPattern))
                hits.Add((name, m.Groups[1].Value, LineOf(src, m.Index)));
        }

        // ★★★ 零命中要判红:提取器坏掉那天,「一个都没扫到」与「一个都没错」输出逐字相同。
        assert(hits.Count >= 3,
            $"★★★ 元断言:真的扫到了模型调用点(实得 {hits.Count} 处)—— "
            + "**零命中要判红**:扫描器死掉的那天,"
            + "「一个入口都没扫到」与「每个入口都挂了闸」在输出里长得一模一样");

        Console.WriteLine($"  (编译集 {files.Count} 个源文件;扫到 {hits.Count} 处模型调用点;"
                        + $"登记表 {ModelReadiness.CallSites.Length} 条)");

        // ---- ② 每个真实调用点都得有登记 ----
        var unregistered = new List<string>();
        var usedReg = new HashSet<int>();
        foreach (var h in hits)
        {
            var i = Array.FindIndex(ModelReadiness.CallSites,
                        c => c.File == h.File && c.Call.Contains(h.Call, StringComparison.Ordinal));
            if (i >= 0) { usedReg.Add(i); continue; }
            unregistered.Add($"{h.File}:{h.Line} 调用了 {h.Call}");
        }
        assert(unregistered.Count == 0,
            "★★★★ **凡会发起模型调用的入口,都登记在 ModelReadiness.CallSites 里**"
            + $"(扫到 {hits.Count} 处,登记表 {ModelReadiness.CallSites.Length} 条)"
            + (unregistered.Count > 0
                ? "\n        —— 下面这 " + unregistered.Count + " 处**没有登记,也就没有挂闸**:\n        "
                  + string.Join("\n        ", unregistered)
                  + "\n        ★ 用户裁定:「不仅仅是聊天功能,其他所有功能都是一样的,"
                  + "按需的模型没起来就禁用」—— 新入口要么挂上闸并登记,要么它就是这条裁定的一个缺口。"
                : ""));

        // ---- ③ 登记表不许发霉:每一条都要还能在代码里找到 ----
        var stale = new List<string>();
        for (int i = 0; i < ModelReadiness.CallSites.Length; i++)
            if (!usedReg.Contains(i))
                stale.Add($"{ModelReadiness.CallSites[i].File} / {ModelReadiness.CallSites[i].Call}");
        assert(stale.Count == 0,
            "★★★ 登记表里**没有发霉的条目**(每一条都还能在编译集里找到对应的调用点)"
            + (stale.Count > 0
                ? "\n        —— 下面这 " + stale.Count + " 条登记着一个**已经不存在**的调用点:\n        "
                  + string.Join("\n        ", stale)
                  + "\n        ★ 只有②的话,这张表会变成一张只增不减的赦免名单:"
                  + "入口删了而登记还在,下一个人会以为那儿还挂着闸。"
                : ""));

        // ---- ④ 登记点名的 GatedIn 文件,必须真的读闸 ----
        var notGated = new List<string>();
        foreach (var c in ModelReadiness.CallSites)
        {
            var p = files.FirstOrDefault(f => Path.GetFileName(f) == c.GatedIn);
            if (p is null) { notGated.Add($"{c.GatedIn}(登记里点名了它,而编译集里没有这个文件)"); continue; }
            if (!CodeOnly(File.ReadAllText(p)).Contains(GateRead, StringComparison.Ordinal))
                notGated.Add($"{c.GatedIn}(它是 {c.File} 那个入口的闸,却一次都没读 {GateRead})");
        }
        assert(notGated.Count == 0,
            "★★★★ 每条登记点名的那个界面,**真的读了就绪闸**"
            + (notGated.Count > 0 ? "\n        —— " + string.Join("\n        ", notGated) : "")
            + " —— 登记只是一句声明,读闸才是那件事本身");

        // ---- ⑤ 每条登记都写了理由(一张没有理由的表,下一个人只会照抄)----
        var noWhy = ModelReadiness.CallSites.Where(c => c.Why.Length < 20).Select(c => c.File).ToList();
        assert(noWhy.Count == 0,
            "★ 每条登记都写清了**为什么**" + (noWhy.Count > 0 ? ":" + string.Join("、", noWhy) + " 没写" : ""));

        // ══════════════════════════════════════════════════════════════════
        //  反向钉:三处**只有源码看得见**的缺陷。行为断言与编译都抓不到它们。
        // ══════════════════════════════════════════════════════════════════
        var cv = Read(files, "ChatView.cs");
        if (cv is not null)
        {
            // ★★★ 两条路一个判据 —— 这个仓栽过一次:目标池空着时按钮是灰的,**回车却照发**。
            var send = Slice(cv, "void SendCurrent()", "TheApp.Chat.Send(");
            assert(send is not null && send.Contains("ModelGateNow().CanUse"),
                "★★★★ 发送键与**回车**走同一条前置条件:闸判在 SendCurrent 里,不是只判在按钮上。"
                + "只灰按钮而回车照发,那种禁用纯属做样子(本仓已经栽过一次)");

            // ★★★ 禁用的控件收不到鼠标事件 ⇒ 外层容器必须是**可命中**的,
            //   而没有画刷的 Border 在命中测试里是个洞 —— 少这一句,气泡永远弹不出来。
            var wrap = Slice(cv, "var sendWrap = new Border", "DockPanel.SetDock(sendWrap");
            assert(wrap is not null && wrap.Contains("Background = Brushes.Transparent"),
                "★★★ 发送键外面那层容器有 Background —— 没有画刷的 Border 在命中测试里是**透明的洞**,"
                + "鼠标会径直穿过去。少这一句,「点禁用的发送键弹气泡」那条裁定就是空的");

            // ★★ 绝不许「看着灰但其实是启用的」:真禁用的是按钮本身。
            assert(CodeOnly(cv).Contains("_sendBtn.IsEnabled = ok;"),
                "★★★ 按钮是**真禁用**(IsEnabled=false),不是只把透明度调低 —— "
                + "「看着灰但其实是启用的」点下去会真的发出去");

            // ★ 闸只在**答案变了**时响,所以界面可以老老实实接它;
            //   直接接 Gpu.Changed 会把输入框每秒重建一次(V20-② 那条纪律仍然成立)。
            assert(cv.Contains("TheApp.Ready.Changed += OnReadyChanged")
                   && !CodeOnly(cv).Contains("TheApp.Gpu.Changed +="),
                "★★★ 聊天界面接的是就绪闸(边沿),**不是** Gpu.Changed(每秒一帧)—— "
                + "后者会把输入框每秒重建一次,打字当场被打断");
        }

        var gpu = Read(files, "HubGpu.cs");
        if (gpu is not null)
        {
            // ★★★★ 会真的卡死的那一条:落地信号必须**无条件**响。
            var note = Slice(gpu, "public void NoteIntent(string alias)", "记下最后一次意图的结果");
            assert(note is not null && note.Contains("RaiseIntentSettled(alias, res)"),
                "★★★★ 意图落地时**无条件**报一次 —— 与 SetLastIntent 那条「说法变了才广播」是两条信号。"
                + "合并会造出一个真的会卡死的闸:模型被卸→重发意图→中枢又回 code=OK("
                + "与上次逐字相同)⇒ 去重把它整条吃掉 ⇒ 闸永远停在「正在启用中」,而模型其实已经好了");
            var setLast = Slice(gpu, "void SetLastIntent(IntentOutcome res)", "最后一次意图的**说法**变了");
            assert(setLast is not null && !setLast.Contains("IntentSettled"),
                "★★★ 落地信号**不许**挂在 SetLastIntent 里 —— 那个方法带着「说法变了才广播」的去重,"
                + "挂进去就等于把无条件信号又变成有条件的(上面那条卡死原样复现)");
            assert(CodeOnly(gpu).Contains("ForgetIntentCooldown"),
                "★★ 被卸之后要能重新起:去抖冷却(20 秒)的前提是「它已经起来了,别再问」,"
                + "前提没了冷却就变成一道把人关在门外的闸 —— 按钮灰着,而 20 秒里没有任何人在试着修好它");
        }

        var td = Read(files, "TaskDrawerView.cs");
        if (td is not null)
        {
            // ★★★ 用户裁定⑥:模型装载不画进度条,而且**在形上**与真任务分得开。
            var running = Slice(td, "if (!t.IsPaused)", "// ★ 暂停不画进度条");
            assert(running is not null && running.Contains("t.IsModelLoad"),
                "★★★★ 模型装载那一条**不走进度条那条渲染路径** —— "
                + "判词:一个不知道自己进度的进度条,是在假装它知道(它的 Progress 恒为 -1)");
            assert(CodeOnly(td).Contains("TheApp.Ready.Gate("),
                "★★★ 抽屉里那条模型的状态取自**同一个就绪闸**,不在这儿另判一次 —— "
                + "两套口径岔开的那天,发送键亮着而抽屉里还写着「正在启用中」,且没有任何东西会红");
        }

        var tc = Read(files, "TaskCenter.cs");
        assert(tc is null || CodeOnly(tc).Contains("public bool IsModelLoad"),
            "★★ 「这条是模型装载」是个**显式字段**,不是去嗅 WorkspaceKey==\"model\" —— "
            + "嗅探问的是一个**相关**的问题,不是那个问题;将来有真任务也带别名时它会误判,而不会红");
    }

    // ────────────────────────────────────────────────────────── 小工具
    static string? Read(List<string> files, string name)
    {
        var p = files.FirstOrDefault(f => Path.GetFileName(f) == name);
        return p is null ? null : File.ReadAllText(p);
    }

    static int LineOf(string src, int index) => src.Take(index).Count(c => c == '\n') + 1;

    static string? Slice(string src, string from, string to)
    {
        var a = src.IndexOf(from, StringComparison.Ordinal);
        if (a < 0) return null;
        var b = src.IndexOf(to, a, StringComparison.Ordinal);
        return b < 0 ? null : src[a..b];
    }

    /// <summary>
    /// 只留下**真会执行**的代码:字符串字面量与注释一律剔掉。
    ///
    /// <para>★ **注释不算**(本仓踩过三次):一句**解释某个东西已经没了**的注释,
    /// 会被判据当成"它还在"的证据,判据当场自我抵消。</para>
    ///
    /// <para>★★ **字面量也不算**,而这一条是本轮当场吃到的:
    /// `ModelReadiness.CallSites` 那张登记表里写着 <c>"ChatClient.StreamAsync("</c> 这类
    /// **字符串**,不剔掉的话扫描器会把**登记表自己**当成三个没挂闸的调用点 ——
    /// 判据把自己的登记表告了。</para>
    ///
    /// <para>★ 顺序:先逐字字符串、再普通字符串、再块注释、最后行注释 ——
    /// 反过来的话,字符串里的 <c>//</c> 会把那一行后半截当注释吃掉。
    /// ★ 普通字符串的正则**不跨行**,所以注释里一个落单的引号最多影响它自己那一行;
    /// 而扫描器真被搞坏时,上面「零命中要判红」那条会红,不会静默。</para>
    /// </summary>
    static string CodeOnly(string src)
    {
        src = Regex.Replace(src, @"@""(?:[^""]|"""")*""", " ");      // 逐字字符串 @"..."
        src = Regex.Replace(src, @"""(?:\\.|[^""\\\n])*""", " ");    // 普通字符串 "..."
        src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(src, @"//[^\n]*", " ");
    }

    /// <summary>
    /// 客户端源码根。★ 判据与 <c>Selftest.ClientSourceRoot()</c> 同款(多个锚点必须同时在):
    /// 只认一个锚点的话,`%TEMP%` 里别的会话留下的一份陈旧文件会被当成源码根 ——
    /// 2026-08-08 出包闸上真的发生过。
    /// </summary>
    static string? ClientSourceRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Selftest.cs"))
                && File.Exists(Path.Combine(dir, "localai-client.csproj"))
                && File.Exists(Path.Combine(dir, "Services", "ModelReadiness.cs")))
                return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    /// <summary>一个 exe **真正会编译**的 .cs 全集 = 隐式 glob + csproj 里逐条 Compile Include。</summary>
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
}
