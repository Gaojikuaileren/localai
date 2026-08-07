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
// ★ 诚实:语音链路(采集/识别/合成/虚拟麦克风)尚未接入 —— 字幕区如实说明,不伪造文字。

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
        Loaded += (_, _) => TheApp.Interpret.Changed += Refresh;
        Unloaded += (_, _) => TheApp.Interpret.Changed -= Refresh;
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
        _pttButton.IsEnabled = !st.PttTranscribing;
        _pttButton.Content = st.PttRecording ? $"正在录…{st.PttSeconds:0.0}s(松开结束)" : "按住说话";

        if (st.PttTranscribing) { _pttStatus.Text = "正在转写…"; return; }
        if (st.PttError.Length > 0) { _pttStatus.Text = st.PttError; return; }
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
        var bubble = ChatView.BubbleShell(body, fromMe);
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
        _subtitle.Text = st.DirectionReady
            ? "语音链路尚未接入 —— 接上之后,对方说的话会在这里逐字出现,成句后飞到上面。"
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
            _messages.Children.Add(ChatView.BubbleShell(body, fromMe));
        }
    }
}
