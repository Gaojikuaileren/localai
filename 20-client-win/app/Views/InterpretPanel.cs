// P3c -- 同声传译的会话板块(骨架)。
//
// 版面(用户裁定 2026-07-31):板块本身要【干净】——
//   左上角  场景切换(由 ChatView 放,不在这里);
//   左边缘  对方的音量(竖条);右边缘  我方的音量(竖条)
//           ★ 左右分边与气泡一致:对方在左、我在右 —— 同一套空间隐喻,不用另学一遍;
//   中间    对话气泡(与聊天空间同一个 BubbleShell,所以左右分边、可选中复制全都一致);
//   底部    一条独立的、略高的半透明灰色横条 —— 字幕在这里【逐字生成】,
//           攒成完整句子之后【动画飞到上方的气泡里】。
//
// ★★ 为什么字幕要先落在底部、成句后再飞上去,而不是直接往气泡里追加:
//   同传的字幕是【还在变的】—— 识别会改口、断句会重来。把没定稿的文字直接写进对话记录,
//   等于让记录里出现过一句从没说过的话。
//   底部横条是"正在听",气泡是"已经定稿";中间那一下飞行,正是"这句从此不再变了"的可见交代。
//
// ★ 诚实:语音链路(采集/识别/合成/虚拟麦克风)尚未接入 ——
//   电平画【空槽】,不给会动的假动画:那正好会骗过"声音还在流动吗"这个
//   最该被诚实回答的问题;字幕区如实说明,不伪造文字。

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

    const double MeterWidth = 6;
    const double SubtitleHeight = 62;

    readonly StackPanel _messages = new();
    readonly TextBlock _subtitle = new();
    readonly Border _subtitleBar;
    readonly Border _myLevel, _theirLevel;

    public InterpretPanel()
    {
        _myLevel = Meter();
        _theirLevel = Meter();
        _subtitleBar = SubtitleBar();

        var dock = new DockPanel { LastChildFill = true };

        // 底部:字幕横条(独立、略高、半透明灰)
        DockPanel.SetDock(_subtitleBar, Dock.Bottom);
        dock.Children.Add(_subtitleBar);

        // 左右边缘:两条竖直电平。★ 对方在左、我在右 —— 与气泡的左右分边完全一致。
        var theirs = MeterColumn(_theirLevel, "对方");
        DockPanel.SetDock(theirs, Dock.Left);
        dock.Children.Add(theirs);

        var mine = MeterColumn(_myLevel, "我");
        DockPanel.SetDock(mine, Dock.Right);
        dock.Children.Add(mine);

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

    // ---------------------------------------------------------------- 竖直电平
    /// <summary>
    /// 一条竖直电平槽。★ 现在没有数据源,所以是【空槽】——
    /// 一旦我们成了用户的麦克风,"声音还在流动"必须能被看见;
    /// 那时这里是实时电平,而不是一个说"已开启"的绿灯。
    /// </summary>
    static Border Meter()
    {
        var b = new Border { Width = MeterWidth, VerticalAlignment = VerticalAlignment.Stretch };
        b.SetResourceReference(Border.BackgroundProperty, "BgSunken");
        b.CornerRadius = new CornerRadius(MeterWidth / 2);
        return b;
    }

    static FrameworkElement MeterColumn(Border meter, string who)
    {
        var t = Ui.Caption(who);
        t.TextAlignment = TextAlignment.Center;
        t.Margin = new Thickness(0, 6, 0, 0);

        var dock = new DockPanel { LastChildFill = true, Width = 26, Margin = new Thickness(2, 0, 2, 0) };
        DockPanel.SetDock(t, Dock.Bottom);
        dock.Children.Add(t);
        dock.Children.Add(meter);
        return dock;
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

        _subtitle.Text = "";
    }

    void Refresh()
    {
        var st = TheApp.Interpret;

        // 字幕开关关掉 -> 整条收起,不留一条空槽占地方
        _subtitleBar.Visibility = st.Subtitles ? Visibility.Visible : Visibility.Collapsed;

        if (InterpretState.PipelineReady) return;

        // ★ 未接入:如实说明,不伪造字幕
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
}
