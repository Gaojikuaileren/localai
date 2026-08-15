// P3c -- 同声传译的会话板块(骨架)。
//
// 版面(用户裁定 2026-07-31):板块本身要【干净】——
//   左上角  场景切换(由 ChatView 放,不在这里);
//   中间    对话气泡(与聊天空间同一个 BubbleShell,所以左右分边、可选中复制全都一致);
//   底部    一条独立的、略高的半透明灰色横条 —— 字幕在这里【逐字生成】,
//           攒成完整句子之后【动画飞到上方的气泡里】。
//
// ★★ 为什么字幕要先落在底部、成句后再飞上去,而不是直接往气泡里追加:
//   同传的字幕是【还在变的】—— 识别会改口、断句会重来。把没定稿的文字直接写进对话记录,
//   等于让记录里出现过一句从没说过的话。
//   底部横条是"正在听",气泡是"已经定稿";中间那一下飞行,正是"这句从此不再变了"的可见交代。
//
// ★ 音量条【不在这里】——(用户裁定)挪到了「同传设置」那一格:
//   会议中要盯的是对话内容,仪表需要时瞟一眼就够,不该常占版面。
//
// ★ 诚实(★★ 2026-08-15 · V38 更正:这一段此前是 P3c 那一版的话,**不带时态**,
//   而它就长在本文件第一屏 —— 采集与识别其实早在 D104 就接上了,
//   就在下面 `_pttBar` / `PushToTalkBar()` 那一族(★ 用符号名不用行号:行号会过期)):
//
//   逐段的今天(每一段都注明是哪一版的话,别再留不带时态的留痕):
//     · 采集    ✅ 已接(D104)—— `AudioCapture`,按住说话那条;
//     · 识别    ✅ 已接(D104)—— `SpeechClient` → 本机 127.0.0.1:18085;
//                 ★★ 「已接」= 有代码、有调用点;**不等于跑过**。D104 自己写着
//                   `AudioCapture.Start()` 这条路径**没有被任何自动断言执行过**
//                   (要真设备 + 人按住),那一格至今没被划掉 ⇒ 【码】✅【调】✅【机】❌。
//                 ★ 但结果今天的**唯一去处**是下面那一行状态文字,不进聊天、不落记录;
//     · 合成    🟡 服务端有(`10-core/speech/server.py` 的 /v1/speech/tts),
//                 **客户端半边没有** —— `SpeechClient` 只实作了 ASR 那一半,没有 TtsAsync;
//                 ★ 措辞留神:本 app **有**播放代码(`Services/Sfx.cs` 放提示音),
//                   缺的是**把 tts 回来的 `audio_b64` 播出去**那条路。
//                   ★★ 范围要写准(V38 第一版把这句写成「全仓……没有第二处」,**那是假的**,
//                     对抗式复核当场抓出来):本客户端 C# 里读 `audio_b64` 的**只有两处,
//                     且都不是播放** —— `SpeechClient.cs:57` 那行键名常量,与自检里拿它
//                     跟 `contracts.json` 对拍的那条(`Selftest.cs:6809` 一带)。
//                   ★ 而**仓库另一侧真的有一个消费者**:网关 `speech_proxy.py:88` 逐字
//                     `obj["audio_b64"]` 把它转发出去 ⇒ 将来改这个键名,**那边要一起改**;
//     · 虚拟麦  ❌ 未接,而且按 D105 **不属于 P5 v1**(它是同传那条线上的东西);
//     · 同传本体 ❌ 未接 —— `InterpretState.PipelineReady` 恒为 false,字幕区照实说明。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class InterpretPanel : UserControl
{
    static App TheApp => (App)Application.Current;

    const double SubtitleHeight = 62;

    readonly StackPanel _messages = new();
    readonly TextBlock _subtitle = new();
    readonly Border _subtitleBar;

    // ── D?(P5 语音 v1)· 按住说话 ──────────────────────────────────────────
    readonly Border _pttBar;
    readonly Button _pttButton = new();
    readonly TextBlock _pttStatus = new();

    /// <summary>当前打开的同传会话 —— 它的转写就是上方的气泡。null = 没选会话。</summary>
    readonly string? _sessionId;

    public InterpretPanel(string? sessionId = null)
    {
        _reprobe.Tick += (_, _) => OnReprobeTick();
        _sessionId = sessionId;
        _subtitleBar = SubtitleBar();
        _pttBar = PushToTalkBar();

        var dock = new DockPanel { LastChildFill = true };

        // ★ 按住说话摆在最下面(最靠近手)。它是这一页今天**唯一真的能用**的语音功能,
        //   而上面那条字幕说的是「同传还没接」—— 两件事,别让后者把前者盖掉。
        DockPanel.SetDock(_pttBar, Dock.Bottom);
        dock.Children.Add(_pttBar);

        // 底部:字幕横条(独立、略高、半透明灰)
        DockPanel.SetDock(_subtitleBar, Dock.Bottom);
        dock.Children.Add(_subtitleBar);

        // ★ 音量条已挪到【同传设置】那一格(用户裁定):
        //   主会话板块只留对话与字幕,越干净越好 —— 会议中要盯的是内容,不是仪表。
        // 中间:对话
        var scroll = new ScrollViewer
        {
            Content = _messages,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough();
        dock.Children.Add(scroll);

        Content = dock;
        Refresh();
        Loaded += (_, _) =>
        {
            TheApp.Interpret.Changed += Refresh;
            // ★★ V30:就绪闸也要接 —— 语音服务装完模型的那一刻按钮要自己亮起来,
            //   而不是等用户切走再切回来。闸只在**答案真的变了**时响,接它不会有噪声。
            TheApp.Ready.Changed += OnReadyChanged;
            // ★ 进页面探一次本机语音服务。★★ 这是 `PttHealthAsync` 这条路**第一个真实调用点** ——
            //   在它之前那个方法零调用,于是这一页从来没真的知道过语音服务起没起。
            _ = TheApp.Interpret.ProbeSpeechAsync();
            _reprobe.Start();
        };
        Unloaded += (_, _) =>
        {
            TheApp.Interpret.Changed -= Refresh;
            TheApp.Ready.Changed -= OnReadyChanged;
            _reprobe.Stop();
        };
    }

    // ---------------------------------------------------------------- 底部字幕横条
    Border SubtitleBar()
    {
        _subtitle.TextWrapping = TextWrapping.Wrap;
        _subtitle.VerticalAlignment = VerticalAlignment.Center;
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        var b = new Border
        {
            Child = _subtitle,
            Height = SubtitleHeight,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 10, 0, 0),
            // ★ 半透明的灰:它是"正在听"的暂存区,不该和已定稿的气泡一样实 ——
            //   浓淡本身就在说"这段还会变"。
            Opacity = 0.72,
        };
        b.SetResourceReference(Border.BackgroundProperty, "BgSunken");
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        return b;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  D?(P5 语音 v1)· 按住说话 —— 半双工:一次按住 = 一段话
    // ═════════════════════════════════════════════════════════════════════════
    //  ★ 为什么是「按住」而不是「点一下开始/再点结束」:半双工的边界必须**由手决定**。
    //    点开点关那种形状,人会忘了关 —— 而忘了关的麦克风,正是这一页最不能出的事。
    //  ★★ 界面对失败的说法分两层(它们的下一步完全不同):
    //      · 录音就没开起来  -> 说清是**哪一步**(没有输入设备 / 权限)
    //      · 录到了但没转成  -> **明说"你的话已经录下来了,没有丢"**
    //    把两者混成一句「失败了」,用户会以为自己白说了一遍。
    Border PushToTalkBar()
    {
        _pttButton.Content = "按住说话";
        _pttButton.Padding = new Thickness(18, 8, 18, 8);
        _pttButton.MinWidth = 120;
        // ★ 按下就录、松开就停 —— 用 Preview 事件,免得被内部模板吞掉。
        _pttButton.PreviewMouseLeftButtonDown += (_, _) => TheApp.Interpret.PttPress();
        _pttButton.PreviewMouseLeftButtonUp += async (_, _) => await TheApp.Interpret.PttReleaseAsync();
        // ★ 鼠标移出按钮也算松开:否则按住拖走会留下一个**一直在录**的麦克风。
        _pttButton.MouseLeave += async (_, _) =>
        {
            if (TheApp.Interpret.PttRecording) await TheApp.Interpret.PttReleaseAsync();
        };

        _pttStatus.TextWrapping = TextWrapping.Wrap;
        _pttStatus.VerticalAlignment = VerticalAlignment.Center;
        _pttStatus.Margin = new Thickness(12, 0, 0, 0);
        _pttStatus.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_pttButton, Dock.Left);
        row.Children.Add(_pttButton);
        row.Children.Add(_pttStatus);

        var b = new Border { Child = row, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 8, 0, 0) };
        b.SetResourceReference(Border.BackgroundProperty, "BgSunken");
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        return b;
    }

    /// <summary>按住说话那一条的刷新。★ 每一种状态都要说得出**下一步做什么**。</summary>
    void RefreshPtt()
    {
        var st = TheApp.Interpret;
        // ══════════════════════════════════════════════════════════════════
        //  ★★★ V30(用户裁定「不仅仅是聊天功能,其他所有功能都是一样的」):
        //    按住说话也挂在**同一个就绪闸**上 —— 只是它问的是**另一个面**
        //    (本机 127.0.0.1:18085 的 /health,不是中枢的显存)。
        //  ★ 这一格是这套设计成不成立的检验:两个证据源完全不同,而这里读到的
        //    是**同一种** ModelGate,这颗按钮不需要知道自己问的是哪个面。
        //  ★★ 顺带修掉一处旧的静默:`PttHealthAsync` 此前**一个调用点都没有** ——
        //    这一页从来没真的探过语音服务,底下那句"本机语音服务(回环)"是无条件印的,
        //    服务没起时它照样那么说。现在由闸来说,而闸是探出来的。
        // ══════════════════════════════════════════════════════════════════
        var gate = TheApp.Ready.Gate(ModelReadiness.SpeechAsr);
        // ★★★★ `|| st.PttRecording` 不是可有可无:**正在录的时候绝不许禁用这颗按钮**。
        //   松开与移出都是挂在按钮上的事件(PreviewMouseLeftButtonUp / MouseLeave),
        //   而**禁用的控件收不到鼠标事件** —— 录音途中闸一变(比如一次健康重探失败)
        //   把按钮灰掉,那两个事件就再也不来了 ⇒ **麦克风一直开着停不下来**。
        //   ★ 本文件头写着:「忘了关的麦克风,正是这一页最不能出的事」。
        //   ⇒ 闸只决定**能不能开始**;已经开始的那一段,由手决定何时结束。
        _pttButton.IsEnabled = !st.PttTranscribing && (st.PttRecording || gate.CanUse);
        _pttButton.Opacity = _pttButton.IsEnabled ? 1 : 0.45;
        _pttButton.Content = st.PttRecording ? $"正在录…{st.PttSeconds:0.0}s(松开结束)" : "按住说话";

        if (st.PttTranscribing) { _pttStatus.Text = "正在转写…"; return; }
        if (st.PttError.Length > 0) { _pttStatus.Text = st.PttError; return; }
        // ★ 模型没起来 ⇒ 按钮已经灰了,这一行**必须**说清为什么、以及下一步。
        //   ★★ 这里不用气泡而用常驻的一行:气泡要点一下才看得见,而这一行本来就在按钮旁边 ——
        //     一个够不着的解释,和没有解释是一回事。
        if (!gate.CanUse) { _pttStatus.Text = gate.Bubble.Replace("\n", " —— "); return; }
        if (st.PttResult is { } r)
        {
            // ★ 把 provenance 如实显示出来:它决定这段话能不能进记忆库,
            //   而那是用户有权知道的一件事(而不是我们替他悄悄决定)。
            _pttStatus.Text = r.Text.Length > 0
                ? $"「{r.Text}」({r.Language} · {r.DurationS:0.0}s)"
                  + (st.PttMayWriteMemory ? " · 可直通记忆" : " · 来源不可信,不写记忆")
                : "没听清 —— 再试一次(录到的音频还在)。";
            return;
        }
        _pttStatus.Text = InterpretState.PushToTalkIsLocalOnly
            ? "按住说话:本机语音服务(回环)。★ 副机上还用不了 —— 网关代理那一段还没接。"
            : "按住说话。";
    }

    /// <summary>
    /// 就绪闸的答案变了 -> **只刷按住说话那一条**,不重建整页。
    /// ★★ 闸可能在后台线程上响(探针跑在 Task 上)⇒ 切回 UI 线程再动控件。
    /// </summary>
    void OnReadyChanged() => Dispatcher.BeginInvoke(new Action(RefreshPtt));

    // ══════════════════════════════════════════════════════════════════════
    //  ★★★★ V30b:闸红了之后**必须还有人在探**,否则它永远红着。
    //
    //  上一轮的接线是个**死锁**,而且旁边那句注释白纸黑字把它说反了:
    //    · `NoteSpeechHealth` 的唯一上游是 `ProbeSpeechAsync`;
    //    · 而它只在 ① 进页面 ② **转写失败之后** 被调;
    //    · 转写要按钮可按 → 按钮可按要 `gate.CanUse` → 闸红了就转写不了 →
    //      **于是再也不会有第②种探测**。⇒ 闸一红就锁死,必须切走再切回。
    //  ★ 而 Loaded 那段注释写着「语音服务装完模型的那一刻按钮要自己亮起来,
    //    **而不是等用户切走再切回来**」—— 那句话在上一轮是假的。现在这条定时器让它成真。
    //
    //  ★★ 与中枢面上的 `ForgetIntentCooldown` 是**同一族**缺陷:
    //    "闸把修好它自己的那条路也一起关掉了"。中枢面修了,本机面上一轮漏了。
    //  ★ 只在**闸没就绪**时探:好了就停表 —— 一个已经能用的服务不需要每 5 秒被问一次。
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>闸没就绪时的重探间隔。★ 短到人不会觉得卡住,长到不会变成轮询风暴。</summary>
    static readonly TimeSpan ReprobeEvery = TimeSpan.FromSeconds(5);

    readonly System.Windows.Threading.DispatcherTimer _reprobe = new() { Interval = ReprobeEvery };

    void OnReprobeTick()
    {
        // ★ 已经能用了就别再问 —— 停表而不是空转,省得每 5 秒一次无谓的回环请求。
        if (TheApp.Ready.Gate(ModelReadiness.SpeechAsr).CanUse) return;
        _ = TheApp.Interpret.ProbeSpeechAsync();
    }

    /// <summary>字幕逐字生长(识别侧每来一小段就调一次)。★ 还没定稿,只待在底部横条里。</summary>
    public void AppendSubtitle(string partial) => _subtitle.Text += partial;

    /// <summary>
    /// 一句定稿了:把它作为气泡落到上方,并演一段【从字幕条飞上去】的动画。
    /// ★ 这个动画不是装饰 —— 它是"这句话从此不再变了"的可见交代。
    ///   接入语音后由识别侧在断句时调用。
    /// </summary>
    public void CommitSubtitle(string text, bool fromMe)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var body = ChatView.MessageText(fromMe);
        body.Text = text;
        // ★★★ V30b:走**同一个**挂动作的出口。此前这里直接调 BubbleShell,
        //   绕过了 WithBubbleActions ⇒ 转写气泡一颗按钮都没有 —— 而**转写就是拿来抄的**,
        //   它恰恰是全 App 最该能一键复制的东西。
        // ★ onQuote 传 null:这一页没有输入框,给一颗按下去什么都不会发生的按钮比不给更坏。
        var bubble = ChatView.WithBubbleActions(ChatView.BubbleShell(body, fromMe), text, fromMe, null);
        _messages.Children.Add(bubble);

        var lift = new TranslateTransform { Y = SubtitleHeight + 10 };
        bubble.RenderTransform = lift;
        lift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(lift.Y, 0, TimeSpan.FromMilliseconds(260))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));

        if (_sessionId is not null)
            TheApp.Chat.SeedMessage(_sessionId, fromMe ? ChatRole.User : ChatRole.Assistant, text, DateTime.Now);

        _subtitle.Text = "";
    }

    void Refresh()
    {
        var st = TheApp.Interpret;
        LoadTranscript();

        // 字幕开关关掉 -> 整条收起,不留一条空槽占地方
        _subtitleBar.Visibility = st.Subtitles ? Visibility.Visible : Visibility.Collapsed;

        // ★ 按住说话**不受** PipelineReady 影响 —— 那个开关说的是「同传」,
        //   而按住说话今天真的能用。放在 return 之前刷,免得被同传那条早退盖掉。
        RefreshPtt();

        if (InterpretState.PipelineReady) return;

        // ★ 未接入:如实说明,不伪造字幕(已有的转写照常显示 —— 那是记录,不是伪造的实时输出)
        // ★★ 2026-08-15(V38)把「语音链路」改成「同声传译」:这句话是**用户看得见**的,
        //   而「语音链路尚未接入」今天已经**半句是假的** —— 采集与识别 D104 就接上了,
        //   下面那条「按住说话」现在就能用。说成整条链都没接,会让人以为语音全不能用。
        //   ★ 未改的是判据本身(仍由 `PipelineReady` 把门),只把话说准。
        _subtitle.Text = st.DirectionReady
            ? "同声传译尚未接入 —— 接上之后,对方说的话会在这里逐字出现,成句后飞到上面。"
            : "先把语言方向的两个坑填上(从语言池拖过去),再开始同传。";
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");

        if (_messages.Children.Count == 0)
        {
            var hint = Ui.Caption("对方的话在左边、我的话在右边 —— 和聊天里一样。");
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.Margin = new Thickness(0, 24, 0, 0);
            _messages.Children.Add(hint);
        }
    }

    /// <summary>
    /// 把这条会话已有的转写铺出来。
    /// ★ 每次都【重建】而不是增量补:同一个元素不能有两个逻辑父级 ——
    ///   留着旧气泡再往里塞,迟早撞上那个异常(这个项目里已经撞过好几次了)。
    /// </summary>
    void LoadTranscript()
    {
        _messages.Children.Clear();
        if (_sessionId is null) return;
        foreach (var m in TheApp.Chat.MessagesOf(_sessionId))
        {
            var fromMe = m.Role == ChatRole.User;
            var body = ChatView.MessageText(fromMe);
            body.Text = m.Text;
            // ★ 与 CommitSubtitle 同一条:走挂了动作的出口,别绕过去(理由见那边)。
            _messages.Children.Add(
                ChatView.WithBubbleActions(ChatView.BubbleShell(body, fromMe), m.Text, fromMe, null));
        }
    }
}
