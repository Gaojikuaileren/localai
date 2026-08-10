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
using System.Windows.Media;
using LocalAI.Client.Services;
using LocalAI.Client.Views;

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
            Behaviour(assert);          // ★ 先测判据本身:它是下面每一条的地基
            InFlightBookkeeping(assert);// ★ A②:闸会不会离开 Starting
            TaskRow(assert);            // ★ ⑥ 两处呈现(横条 + 抽屉)
            Interaction(assert);    // ★ ⑦ 气泡按钮:**驱动真界面**,不是查词元
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

        // ══════════════════════════════════════════════════════════════════
        //  ★★★★ A①:中枢说「正在装」时,闸**不许**说它被卸了。
        //    `gpu_broker.py` 在同组件已有装载在途时回 200 + ALREADY_RESIDENT + plane="loading",
        //    而 ParseIntent 把它判成 Ok:true ⇒ 若不读 plane,就会落进「上次成功 + 现在不在驻留集」
        //    那一支,输出「模型刚才起来过,现在已经被卸下了……打字就会重新启用它」。
        //    ★ 每个字都是假的(它从没起来过,正在头一次装),而用户照做只会撞上 20 秒去抖。
        // ══════════════════════════════════════════════════════════════════
        var loading = new IntentOutcome(true, "ALREADY_RESIDENT", ModelReadiness.ChatAlias,
                                        "llm.assistant.8b@8k", "正在装载中", "loading");
        var g载 = ModelReadiness.Decide(true, true, false, loading, residentByCatalog: false);
        assert(g载.State == ModelReadyState.Starting,
            "★★★★ 中枢说 plane=\"loading\" ⇒ 闸是【正在启用中】,**不是**「已经被卸下了」。"
            + "后者在第一次装载(几 GiB 权重进显存,常常超过 20 秒去抖窗口)时**每个字都是假的**,"
            + "而它还会叫用户去打字 —— 那只会撞上去抖,什么都不会发生");
        assert(!g载.Why.Contains("被卸下"),
            "★★★ 「正在装」那一格的理由里**不许**出现「被卸下」—— 归错因比不给原因更坏");

        // ★★ 同一族的第二格:装载**刚成功**时,快照还是上一帧(不含它)——
        //   那一帧比这次意图更旧,没资格说"它不在"。判据在 ResidentOf,这里钉住它的语义。
        assert(ModelReadiness.Decide(true, true, false, ok, residentByCatalog: null).State
               == ModelReadyState.Ready,
            "★★★ 比意图更旧的那一帧快照 ⇒ 传进来的是 null(说不出),而不是 false(它不在)。"
            + "**旧的观察也是没观察** —— 拿它判会在装载成功后的一两秒里说「已经被卸下了」");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  A②:闸**会离开** Starting。
    //
    //  ★★★★ 这一条上一轮零判据:删掉 `ModelReadiness.cs` 里那句 `if (r.InFlight > 0) r.InFlight--;`
    //    ⇒ 闸永远 Starting、发送键**永远灰**、整个聊天功能死掉 —— 而自检全绿。
    //  ★ 走的是真事件口(`HubGpu` 的两个自检缝,它们和生产路径是**同一对** Raise 方法),
    //    不是直接去改字段。
    // ══════════════════════════════════════════════════════════════════════
    static void InFlightBookkeeping(Action<bool, string> assert)
    {
        var app = (App)System.Windows.Application.Current;
        var readiness = new ModelReadiness(app.Gpu, app.Hub);
        const string a = "selftest.inflight";

        assert(!readiness.HasIntentInFlightForSelftest(a),
            "★ 起点:没发过意图 ⇒ 没有在途");

        app.Gpu.StartIntentForSelftest(a);
        assert(readiness.HasIntentInFlightForSelftest(a),
            "★★ 意图发出去了 ⇒ 记成在途(闸据此进入「正在启用中」)");

        app.Gpu.SettleIntentForSelftest(a, new IntentOutcome(true, "OK", a, "c", "", "transient"));
        assert(!readiness.HasIntentInFlightForSelftest(a),
            "★★★★ 意图落地 ⇒ 在途**必须清掉**。删掉 `InFlight--` 那一行时这条要红 —— "
            + "否则闸永远停在「正在启用中」、发送键永远灰,而聊天功能整个死掉");

        // ★ 落地比发出多一次也不许把计数压到负数(负数会让下一次真的在途被吃掉)。
        app.Gpu.SettleIntentForSelftest(a, new IntentOutcome(true, "OK", a, "c", "", "transient"));
        app.Gpu.StartIntentForSelftest(a);
        assert(readiness.HasIntentInFlightForSelftest(a),
            "★★ 多余的落地不许把计数压成负数 —— 压负了之后,下一次真的在途会被它抵消掉");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ⑥ 一条任务长什么样。★★ 验的是 `TaskRowLook.For` 这个**纯函数本身** ——
    //    底部横条与任务抽屉调的都是它,所以这一组同时守住了两处。
    // ══════════════════════════════════════════════════════════════════════
    static void TaskRow(Action<bool, string> assert)
    {
        var model = new RunningTask { Title = "按需模型:assistant.fast", Progress = -1, IsModelLoad = true };
        var real = new RunningTask { Title = "正在计算", Progress = 0.4 };

        var starting = ModelReadiness.Decide(true, true, true, null, null);
        var ready = ModelReadiness.Decide(true, true, false, null, residentByCatalog: true);

        // ★★★★ 用户裁定那一条:模型装载**不画进度条**。
        assert(!TaskRowLook.For(model, starting).ShowProgressBar,
            "★★★★ 模型装载**不画进度条**(用户裁定:「底部的任务进行栏…现在有个正在载入中的"
            + "进度条,不对,修」)。判词:一个不知道自己进度的进度条,是在假装它知道");
        assert(!TaskRowLook.For(model, ready).ShowProgressBar,
            "★★ 装完了也不画 —— 它从头到尾都不是一件有进度的活");
        assert(TaskRowLook.For(real, ready).ShowProgressBar,
            "★★★ 反向:**真任务照旧画进度条** —— 用户明说这个功能本身「很好」,"
            + "只改模型那一类的呈现,别顺手把它删了");

        // ★★★ 文字态:两处必须是同一句(横条与抽屉画的是同一条任务)。
        assert(TaskRowLook.For(model, starting).StateText == "正在启用中"
               && TaskRowLook.For(model, ready).StateText == "已启用"
               && TaskRowLook.For(model, ModelReadiness.Decide(true, true, false, null, null)).StateText == "未启用",
            "★★★ 模型那一行的文字态取自就绪闸的 StateLabel(未启用 / 正在启用中 / 已启用)");
        assert(TaskRowLook.For(real, ready).StateText == real.PercentText,
            "★★ 真任务的那一格仍然是它自己的百分比 —— 与模型就绪毫无关系");

        // ★ 暂停的真任务不画进度条(一条停着的进度条会让人以为它还在动)——旧纪律,别被本轮改掉。
        var paused = new RunningTask { Title = "被让位的活", Progress = 0.4 };
        paused.State = TaskState.Paused;
        assert(!TaskRowLook.For(paused, ready).ShowProgressBar,
            "★★ 暂停的任务也不画进度条(旧纪律:停着的进度条会被读成还在动)");

        // ★★ 状态点:三态**真的是三种颜色**。写成一个颜色的话,「形上分得开」只剩文字那一半。
        var c1 = ((SolidColorBrush)Views.TaskDrawerView.StateDotOf(ModelReadyState.Ready).Background).Color;
        var c2 = ((SolidColorBrush)Views.TaskDrawerView.StateDotOf(ModelReadyState.Starting).Background).Color;
        var c3 = ((SolidColorBrush)Views.TaskDrawerView.StateDotOf(ModelReadyState.NotStarted).Background).Color;
        assert(c1 != c2 && c2 != c3 && c1 != c3,
            "★★ 状态点三态三色互不相同 —— 删掉取色那一段、三格同一个颜色时这条要红");
        assert(Views.TaskDrawerView.StateDotOf(ModelReadyState.Ready).Width > 0,
            "★ 状态点真的有大小(整个方法被删掉时这条会红)");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ⑦ 气泡上的「复制 / 引用」。★★★ 这一组**驱动真界面**:离屏挂载 → 排版 →
    //    发真的鼠标事件 → 读剪贴板与输入框。
    //  ★ 上一轮这个功能**一条断言都没有**:把 WithBubbleActions 的函数体换成
    //    `return shell;`,两颗按钮全消失而自检全绿。查词元也挡不住那种改法。
    // ══════════════════════════════════════════════════════════════════════
    static void Interaction(Action<bool, string> assert)
    {
        Theme.ThemeManager.Initialize(Skin.Breeze);   // 气泡用 FindResource,缺字典会抛
        var app = (App)System.Windows.Application.Current;
        var cv = new Views.ChatView("chat");
        var host = new System.Windows.Controls.Grid { Width = 900, Height = 620 };
        host.Children.Add(cv);
        host.Measure(new System.Windows.Size(900, 620));
        host.Arrange(new System.Windows.Rect(0, 0, 900, 620));
        host.UpdateLayout();

        // ★ 造一条**超过折叠阈值**的消息:这样"复制的是原文还是屏幕上那份"才分得开 ——
        //   屏幕上只画前 30 行,而复制必须给全文。
        var sess = app.Chat.NewSession(null, "chat");
        var full = string.Join("\n", Enumerable.Range(1, 45).Select(i => $"第 {i} 行"));
        app.Chat.SeedMessage(sess.SessionId, Services.ChatRole.Assistant, full, DateTime.Now);
        cv.OpenSession(sess);
        host.UpdateLayout();

        var slots = Views.ChatView.ActionSlotsIn(cv);
        assert(slots.Count >= 1,
            $"★★★ 元断言:真界面里找得到气泡动作槽(实得 {slots.Count} 个)—— "
            + "找不到就说明按钮那条路整条没挂上,而「零个槽」与「每个槽都对」在输出里长得一模一样");
        if (slots.Count == 0) return;

        var slot = slots[0];
        // ① **按需建**:没碰过之前,槽里什么都没有。
        assert(slot.Content is null,
            "★★★ 悬停之前槽是空的 —— 「hover/焦点时才建」这条路子的**全部理由**就在这儿。"
            + "改成建界面时就造出来,这条要红");

        // ② 发一次真的 MouseEnter(不是调内部方法)——走的就是用户鼠标走的那条路。
        var row = System.Windows.Media.VisualTreeHelper.GetParent(slot) as System.Windows.UIElement;
        row?.RaiseEvent(new System.Windows.Input.MouseEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, 0)
        { RoutedEvent = System.Windows.UIElement.MouseEnterEvent });
        host.UpdateLayout();

        var col = slot.Content as System.Windows.Controls.StackPanel;
        assert(col is not null && col.Children.Count == 2,
            $"★★★★ 悬停之后长出**两颗**按钮(复制 + 引用),实得 "
            + (col?.Children.Count.ToString() ?? "槽还是空的")
            + " —— 把 WithBubbleActions 换成 `return shell;` 时这条要红");
        if (col is null || col.Children.Count < 2) return;

        // ★★ 按钮上写的是**文字**,不是字形:本 App 全局关掉了 ToolTip,
        //   靠 ToolTip 解释自己的字形按钮 = 用户看到两个不认识的符号且无从问起。
        var labels = col.Children.OfType<System.Windows.Controls.Border>()
                        .Select(b => (b.Child as System.Windows.Controls.TextBlock)?.Text ?? "")
                        .ToList();
        assert(labels.Contains("复制") && labels.Contains("引用"),
            "★★★★ 两颗按钮上写的是**文字**(复制 / 引用),不是字形 —— "
            + "这个 App 在 Theme/Controls.xaml 里**全局关掉了 ToolTip**(还有一条断言守着它已关),"
            + $"字形按钮在这里就是两个没人认识、也问不出来的符号。实得:[{string.Join(", ", labels)}]");

        // ③ 复制:点**真的按钮**,读**真的剪贴板**。
        var clipWhy = "";
        try { System.Windows.Clipboard.SetText("selftest-probe"); }
        catch (Exception ex) { clipWhy = ex.GetType().Name + ": " + ex.Message; }
        if (clipWhy.Length > 0)
        {
            // ★ 把**原因**印出来:一句光秃秃的 SKIP 与"验过了"在输出里差别太小。
            //   ★★ 这一格跳过**不留缺口**:下面「引用」那条同样吃 WithBubbleActions 的
            //     同一个 text 参数,而它断言输入框里出现了第 45 行 ——
            //     「给的是原文而不是折叠后的 30 行」由那一条**行为地**证明了。
            Console.WriteLine("  SKIP  剪贴板在这个进程里打不开(" + clipWhy + ")—— "
                            + "「复制到剪贴板」这一格本次没验过;"
                            + "但「用的是原文全文」由下面那条【引用】的断言证明着,不是缺口");
        }
        else
        {
            Click(col.Children[0]);
            assert(System.Windows.Clipboard.GetText() == full,
                "★★★★ 复制给出的是**原文全文**(45 行),不是屏幕上那份被折叠到 30 行的。"
                + "折叠是显示,不是数据 —— 用户要的是「这条消息」,不是「我此刻看见的这几行」");
        }

        // ④ 引用:先打一半草稿,再点引用 —— 草稿必须**还在**。
        cv.InputForSelftest.Text = "我本来打的字";
        Click(col.Children[1]);
        var after = cv.InputForSelftest.Text;
        assert(after.StartsWith("我本来打的字", StringComparison.Ordinal),
            "★★★ 引用是**追加**不是覆盖 —— 覆盖等于把人正在写的东西吃掉");
        assert(after.Contains("\n> 第 1 行") && after.Contains("> 第 45 行"),
            "★★★ 引用进去的是 markdown 引用记号 + **全文**(45 行都在)—— "
            + "模型认得这个记号,人也认得");
    }

    /// <summary>发一次真的左键松开。★ 走按钮自己挂的那个处理器,不是绕过去直接调回调。</summary>
    static void Click(object child)
    {
        if (child is not System.Windows.UIElement el) return;
        el.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        { RoutedEvent = System.Windows.UIElement.MouseLeftButtonUpEvent });
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
        //  反向钉:**只有源码看得见**的那些接线。
        //
        //  ★★★★ 一律走 `CodeOnly()`(注释与字面量都剔掉),**不许对原始文本 Contains**。
        //    `ASSERTION-PITFALLS` 第 21 条(2026-08-09 · V26 立的)记的正是这个形状:
        //    **正向断言被注释喂绿** —— 把代码删掉、在同一区间留一句含那个词元的注释,断言照样全绿。
        //    ★ 上一轮我在这个文件里两套写法并存(:293/:318 用 CodeOnly,:280/:287/:299/:309/:328
        //      对原始文本 Contains),而后者每一条都能被一句注释喂绿。已逐条改齐。
        // ══════════════════════════════════════════════════════════════════
        var cv = Read(files, "ChatView.cs");
        if (cv is not null)
        {
            var cvCode = CodeOnly(cv);
            // ★★★ 两条路一个判据 —— 这个仓栽过一次:目标池空着时按钮是灰的,**回车却照发**。
            var send = Slice(cvCode, "void SendCurrent()", "TheApp.Chat.Send(");
            assert(send is not null && send.Contains("ModelGateNow().CanUse"),
                "★★★★ 发送键与**回车**走同一条前置条件:闸判在 SendCurrent 里,不是只判在按钮上。"
                + "只灰按钮而回车照发,那种禁用纯属做样子(本仓已经栽过一次)");

            // ★★★ 禁用的控件收不到鼠标事件 ⇒ 外层容器必须是**可命中**的,
            //   而没有画刷的 Border 在命中测试里是个洞 —— 少这一句,气泡永远弹不出来。
            var wrap = Slice(cvCode, "var sendWrap = new Border", "DockPanel.SetDock(sendWrap");
            assert(wrap is not null && wrap.Contains("Background = Brushes.Transparent"),
                "★★★ 发送键外面那层容器有 Background —— 没有画刷的 Border 在命中测试里是**透明的洞**,"
                + "鼠标会径直穿过去。少这一句,「点禁用的发送键弹气泡」那条裁定就是空的");

            // ★★ 绝不许「看着灰但其实是启用的」:真禁用的是按钮本身。
            assert(cvCode.Contains("_sendBtn.IsEnabled = ok;"),
                "★★★ 按钮是**真禁用**(IsEnabled=false),不是只把透明度调低 —— "
                + "「看着灰但其实是启用的」点下去会真的发出去");

            // ★ 闸只在**答案变了**时响,所以界面可以老老实实接它;
            //   直接接 Gpu.Changed 会把输入框每秒重建一次(V20-② 那条纪律仍然成立)。
            assert(cvCode.Contains("TheApp.Ready.Changed += OnReadyChanged")
                   && !cvCode.Contains("TheApp.Gpu.Changed +="),
                "★★★ 聊天界面接的是就绪闸(边沿),**不是** Gpu.Changed(每秒一帧)—— "
                + "后者会把输入框每秒重建一次,打字当场被打断");
        }

        var gpu = Read(files, "HubGpu.cs");
        if (gpu is not null)
        {
            var gpuCode = CodeOnly(gpu);
            // ★★★★ 会真的卡死的那一条:落地信号必须**无条件**响。
            //   ★ 切片切的是 **CodeOnly 之后**的文本 —— 切原文的话,
            //     把 RaiseIntentSettled 删掉、在 NoteIntent 里留一句提到它的注释就能喂绿。
            var note = Slice(gpuCode, "public void NoteIntent(string alias)", "void SetLastIntent");
            assert(note is not null && note.Contains("RaiseIntentSettled(alias, res)"),
                "★★★★ 意图落地时**无条件**报一次 —— 与 SetLastIntent 那条「说法变了才广播」是两条信号。"
                + "合并会造出一个真的会卡死的闸:模型被卸→重发意图→中枢又回 code=OK("
                + "与上次逐字相同)⇒ 去重把它整条吃掉 ⇒ 闸永远停在「正在启用中」,而模型其实已经好了");
            var setLast = Slice(gpuCode, "void SetLastIntent(IntentOutcome res)", "public event Action? IntentChanged");
            assert(setLast is not null && !setLast.Contains("IntentSettled"),
                "★★★ 落地信号**不许**挂在 SetLastIntent 里 —— 那个方法带着「说法变了才广播」的去重,"
                + "挂进去就等于把无条件信号又变成有条件的(上面那条卡死原样复现)");
            assert(gpuCode.Contains("public void ForgetIntentCooldown"),
                "★★ 被卸之后要能重新起:去抖冷却(20 秒)的前提是「它已经起来了,别再问」,"
                + "前提没了冷却就变成一道把人关在门外的闸 —— 按钮灰着,而 20 秒里没有任何人在试着修好它");
        }

        // ══════════════════════════════════════════════════════════════════
        //  ⑥ 两处呈现都必须走**同一个** TaskRowLook。
        //  ★★★ 「不画进度条」那件事本身由 TaskRow() 那组**行为断言**守着(验的是纯函数的返回值,
        //    注释喂不绿);这里只钉**接线**:两处都得真的去调它,而不是各判各的。
        //  ★ 上一轮这儿只有一条「`t.IsModelLoad` 这个词出现在 !IsPaused 那段里」——
        //    写成 `if (t.IsModelLoad) bar.IsIndeterminate = true;` 同样保绿,
        //    它**根本没验模型那一支里没有 bar**。
        // ══════════════════════════════════════════════════════════════════
        foreach (var (name, why) in new[]
        {
            ("TaskDrawerView.cs", "任务抽屉"),
            ("MainWindow.xaml.cs", "底部任务横条(**用户裁定原文点名的就是它**)"),
        })
        {
            var src = Read(files, name);
            if (src is null) { assert(false, $"★ 元断言:编译集里找得到 {name}"); continue; }
            assert(CodeOnly(src).Contains("TaskRowLook.For("),
                $"★★★★ {why}走 `TaskRowLook.For(...)` —— 两处各判一次的那天,"
                + "横条说「正在载入」而抽屉说「已启用」,**同一条任务在两个界面互相打脸**,"
                + "而不会有任何东西红(2026-08-10 实机反馈就是这个形状)");
        }

        var mw = Read(files, "MainWindow.xaml.cs");
        if (mw is not null)
        {
            var mwCode = CodeOnly(mw);
            // ★ 结束锚点用 `OnToggleTaskDrawer` 而不是 `RotateTask` —— 后者在文件里排在
            //   ShowTask **前面**,拿它当终点会切出 null,断言当场红得理由是假的(自己刚踩了一次)。
            var show = Slice(mwCode, "void ShowTask(RunningTask t", "void OnToggleTaskDrawer");
            assert(show is not null, "★ 元断言:切得到 ShowTask 的正文(锚点还在)");
            assert(show is not null && show.Contains("TaskBarProgress.Visibility"),
                "★★★ 横条要真的**把进度条藏起来**(不是只把它设成 0)—— "
                + "一条停在 0 的进度条会被读成「有个任务卡住了」,那比不显示更坏");
            assert(mwCode.Contains("TheApp.Ready.Changed"),
                "★★★ 横条接了就绪闸 —— 少这一条订阅,它会永远停在「正在启用中」,"
                + "而抽屉(它自己接了闸)显示「已启用」");
        }

        var tc = Read(files, "TaskCenter.cs");
        assert(tc is null || CodeOnly(tc).Contains("public bool IsModelLoad"),
            "★★ 「这条是模型装载」是个**显式字段**,不是去嗅 WorkspaceKey==「model」—— "
            + "嗅探问的是一个**相关**的问题,不是那个问题;将来有真任务也带别名时它会误判,而不会红");

        var mr = Read(files, "ModelReadiness.cs");
        assert(mr is null || CodeOnly(mr).Contains("void RetireModelTask"),
            "★★★ 模型装完/被卸之后,那条任务要**撤掉**(暂停的除外 —— D87③ 的「再开」靠它)。"
            + "`RegisterIntentTask` 只 Add 不 Remove:少了这条,横条会一直轮播一件早就结束的事");

        var ip = Read(files, "InterpretPanel.cs");
        if (ip is not null)
        {
            var ipCode = CodeOnly(ip);
            assert(ipCode.Contains("ProbeSpeechAsync") && ipCode.Contains("_reprobe"),
                "★★★★ 语音面**闸红了之后还有人在探**。上一轮这条是死锁:重探的唯一触发是"
                + "「转写失败之后」,而转写要按钮可按、按钮可按要闸就绪 ⇒ 闸一红就再也探不了,"
                + "必须切走再切回 —— 而旁边的注释白纸黑字写着「不是等用户切走再切回来」");
            assert(!ipCode.Contains("ChatView.BubbleShell(body, fromMe))"),
                "★★★ 同传的气泡不许绕过 `WithBubbleActions` 直接用 BubbleShell —— "
                + "绕过去的那一版,转写气泡一颗按钮都没有,而**转写恰恰是最该能复制的东西**");
            assert(!ipCode.Contains("ToolTipService.SetShowOnDisabled"),
                "★ 反向钉:别再用 ToolTip 当解释 —— 本 App 全局关掉了它(见 Theme/Controls.xaml)");
        }

        var tdNoTip = Read(files, "TaskDrawerView.cs");
        assert(tdNoTip is null || !CodeOnly(tdNoTip).Contains("ToolTipService.SetShowOnDisabled"),
            "★★★ `ToolTipService.SetShowOnDisabled` 在本 App 里是**空操作**(ToolTip 被全局关掉,"
            + "一个像素都不画)⇒ 不许留着它冒充「已修好按钮自己解释自己」。"
            + "多一行代码宣称某件事被修好了,比那件事没修更坏");
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
