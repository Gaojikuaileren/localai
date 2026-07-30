// P3c -- 同声传译的会话板块(骨架)。
//
// ★ 诚实第一:语音链路(采集 / 识别 / 合成 / 虚拟麦克风)都还没接入,
//   所以这里【不放能按下去却什么都不做的开关】,也不伪造字幕。
//   位置先摆好、规则先写死,等模型与驱动定了往里填。
//
// 版面:
//   顶部  —— 两条电平(我 / 对方)。★ 它不是装饰:一旦我们成了用户的麦克风,
//            "声音还在流动"必须能被【看见】,而不是靠一个绿灯说"已开启"。
//   中间  —— 对方声音的实时字幕(可关)。
//   底部  —— 两个开关:实时翻译输出 / 字幕;以及当前的透传状态。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class InterpretPanel : UserControl
{
    static App TheApp => (App)Application.Current;

    readonly StackPanel _subtitles = new();

    public InterpretPanel()
    {
        var dock = new DockPanel { LastChildFill = true };

        var meters = Meters();
        DockPanel.SetDock(meters, Dock.Top);
        dock.Children.Add(meters);

        var controls = Controls();
        DockPanel.SetDock(controls, Dock.Bottom);
        dock.Children.Add(controls);

        dock.Children.Add(SubtitleArea());

        Content = dock;
        Refresh();
        Loaded += (_, _) => TheApp.Interpret.Changed += Refresh;
        Unloaded += (_, _) => TheApp.Interpret.Changed -= Refresh;
    }

    // ---------------------------------------------------------------- 电平
    FrameworkElement Meters()
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var mine = Meter("我(麦克风)");
        var theirs = Meter("对方(会议音频)");
        Grid.SetColumn(mine, 0);
        Grid.SetColumn(theirs, 2);
        row.Children.Add(mine);
        row.Children.Add(theirs);
        return row;
    }

    /// <summary>
    /// 一条电平。★ 现在没有数据源,所以画的是【空槽】并注明"未接入" ——
    /// 不给一个会动的假动画:那正好会骗过"声音还在流动吗"这个最该被诚实回答的问题。
    /// </summary>
    static FrameworkElement Meter(string title)
    {
        var t = Ui.Caption(title);
        var track = new Border { Height = 6, Margin = new Thickness(0, 4, 0, 0) };
        track.SetResourceReference(Border.BackgroundProperty, "BgSunken");
        track.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");

        var box = new StackPanel();
        box.Children.Add(t);
        box.Children.Add(track);
        return box;
    }

    // ---------------------------------------------------------------- 字幕
    FrameworkElement SubtitleArea()
    {
        var scroll = new ScrollViewer
        {
            Content = _subtitles,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough();
        return scroll;
    }

    // ---------------------------------------------------------------- 开关
    FrameworkElement Controls()
    {
        var speak = Ui.Secondary("实时翻译输出", (_, _) =>
            TheApp.Interpret.SetSpeakTranslation(!TheApp.Interpret.SpeakTranslation));
        var subs = Ui.Secondary("字幕", (_, _) =>
            TheApp.Interpret.SetSubtitles(!TheApp.Interpret.Subtitles));
        _speakBtn = speak;
        _subsBtn = subs;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(speak);
        row.Children.Add(new Border { Width = 8 });
        row.Children.Add(subs);

        var box = new StackPanel();
        box.Children.Add(row);
        box.Children.Add(_passthrough);
        return box;
    }

    Button? _speakBtn, _subsBtn;
    readonly TextBlock _passthrough = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    void Refresh()
    {
        var st = TheApp.Interpret;

        _subtitles.Children.Clear();
        if (!InterpretState.PipelineReady)
        {
            var head = Ui.Body("语音链路尚未接入。", muted: true);
            head.HorizontalAlignment = HorizontalAlignment.Center;
            head.Margin = new Thickness(0, 24, 0, 6);
            _subtitles.Children.Add(head);

            foreach (var line in new[]
                     {
                         "位置与规则已经定好,等语音模型与虚拟麦克风就绪后填内容:",
                         $"· 我说【{Languages.NameOf(st.MyLang)}】-> 对方听到【{Languages.NameOf(st.TheirLang)}】;",
                         "· 对方的声音只从会议软件那个进程取,不会录进系统里其它声音;",
                         "· 麦克风与会议音频【两路分开】,不混流 —— 谁在说话是确定的,不靠事后分离。",
                     })
            {
                var c = Ui.Caption(line);
                c.HorizontalAlignment = HorizontalAlignment.Center;
                c.TextAlignment = TextAlignment.Center;
                _subtitles.Children.Add(c);
            }
        }

        if (_speakBtn is not null) _speakBtn.Content = st.SpeakTranslation ? "实时翻译输出:开" : "实时翻译输出:关";
        if (_subsBtn is not null) _subsBtn.Content = st.Subtitles ? "字幕:开" : "字幕:关";

        // ★ 无论开关在哪一边,透传都在 —— 这条要一直写在用户眼前,
        //   因为它是"我们崩了会不会让你在会议里静音"的答案。
        _passthrough.Text = st.SpeakTranslation
            ? "会议里听到的是同传的合成语音。★ 同传一旦出错会立刻退回你的原声,不会静音。"
            : "会议里听到的是你的原声(我们只做透传)。打开上面的开关才用同传取代它。";
        _passthrough.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _passthrough.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
    }
}
