// P3c -- 上下拨的开关(用户裁定 2026-07-31)。
//
// 形态:一根竖直的圆角槽 + 一个圆钮,钮在【上=开、下=关】之间【滑动】过去,不是硬跳。
// 名字写在开关【下方】,一列一个,从左往右排。
//
// ★ 为什么自己写而不是用 WPF 的 CheckBox 改模板:
//   这个控件将来要给不同皮肤换样子(用户明确要求"为未来皮肤预留"),
//   所以尺寸、圆角、配色全部走【资源令牌】,皮肤换一张色表就跟着变;
//   而 CheckBox 的模板还得连带处理它自带的一堆状态(三态、焦点框、内容呈现),
//   在一个只有开/关两态的东西上是净负担。
//
// ★ 焦点纪律:它是纯鼠标件 —— 不可聚焦、不进 Tab 序(见 FocusPolicy)。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LocalAI.Client.Views;

public sealed class ToggleSwitch : UserControl
{
    // 尺寸集中在这里 —— 皮肤要改观感,改这几个数 + 令牌就够
    const double TrackW = 30;
    const double TrackH = 52;
    const double Knob = 22;
    const double Inset = 4;

    // ★ 紧凑档(用户裁定 2026-07-31):同传设置那一格里两个开关并排,
    //   窗口缩到最小时原尺寸会把右边的设备选择挤出去 —— 那一格是【不给滚动条】的,
    //   挤出去就是真的看不见了。所以给一个窄档,而不是靠"窗口别缩太小"。
    const double TrackWc = 24;
    const double TrackHc = 40;
    const double Knobc = 17;

    readonly Border _track;
    readonly Border _knob;
    readonly TranslateTransform _slide = new();
    readonly Action<bool> _onChanged;
    bool _on;

    readonly bool _enabled;

    /// <param name="enabled">
    /// false = 灰掉、拨不动。★ 用在"前提没满足"的场合(比如虚拟声卡没装,
    /// 译文语音根本送不进会议软件)—— 给一个能拨却不生效的开关就是骗人。
    /// </param>
    readonly double _tw, _th, _kn;

    public ToggleSwitch(string label, bool on, Action<bool> onChanged, bool enabled = true, bool compact = false)
    {
        _on = on;
        _onChanged = onChanged;
        _enabled = enabled;
        _tw = compact ? TrackWc : TrackW;
        _th = compact ? TrackHc : TrackH;
        _kn = compact ? Knobc : Knob;

        _knob = new Border
        {
            Width = _kn, Height = _kn,
            CornerRadius = new CornerRadius(_kn / 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Inset, 0, 0),
            RenderTransform = _slide,
        };

        _track = new Border
        {
            Width = _tw, Height = _th,
            CornerRadius = new CornerRadius(_tw / 2),
            BorderThickness = new Thickness(1),
            Child = _knob,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        // ★ 整根槽都是命中区(不是只有那个圆钮)—— 圆钮才 22px,只让它可点会经常按空。
        _track.Background = Brushes.Transparent;
        _track.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (_enabled) Set(!_on, animate: true); };
        if (!enabled) _track.Cursor = Cursors.Arrow;

        var text = new TextBlock
        {
            Text = label,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, compact ? 4 : 6, 0, 0),
            MaxWidth = compact ? 58 : 72,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        text.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var box = new StackPanel { Margin = new Thickness(0, 0, compact ? 10 : 18, 0), Opacity = enabled ? 1 : 0.4 };
        box.Children.Add(_track);
        box.Children.Add(text);
        Content = box;

        Apply(animate: false);
    }

    public void Set(bool on, bool animate)
    {
        if (_on == on) { Apply(animate: false); return; }
        _on = on;
        Apply(animate);
        _onChanged(_on);
    }

    void Apply(bool animate)
    {
        // 开 = 钮在上;关 = 钮滑到底
        var to = _on ? 0 : _th - _kn - Inset * 2;
        if (animate)
        {
            _slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(to, TimeSpan.FromMilliseconds(180))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else
        {
            _slide.BeginAnimation(TranslateTransform.YProperty, null);   // 先撤动画,否则它会一直按着旧值
            _slide.Y = to;
        }

        _track.SetResourceReference(Border.BackgroundProperty, _on ? "Accent" : "BgSunken");
        _track.SetResourceReference(Border.BorderBrushProperty, _on ? "Accent" : "Border");
        _knob.SetResourceReference(Border.BackgroundProperty, _on ? "FgOnAccent" : "BgSurface");
        _knob.SetResourceReference(Border.BorderBrushProperty, "Border");
        _knob.BorderThickness = new Thickness(_on ? 0 : 1);
    }
}
