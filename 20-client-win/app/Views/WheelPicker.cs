// P3c -- 竖直滚轮选择器(时间 / 日期)。
//
// 用户裁定:
//   · 非全天的开始/结束【不用输入框敲数字】,而是【竖直滚轮】滚动选择;
//   · 时间最小单位【5 分钟】;
//   · 【不做无限循环】—— 有头有尾,滚到边界就停;
//   · 滚动要有【动画】,不是硬切;
//   · 全天勾选后的起止日期用同一套外观(原生 DatePicker 是系统风,与整体脱节)。
//
// ★ 实现从 ListBox + ScrollIntoView 改成【自绘 + 位移动画】:
//   ListBox 的 ScrollIntoView 是瞬间跳到位的,做不出轮盘的滑动感(用户反馈"硬切")。
//   现在是一列 TextBlock 叠在裁剪容器里,靠 TranslateTransform 动画滑到目标行,
//   中间一行有高亮带,选中项加粗、离中心越远越淡 —— 才像转盘。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LocalAI.Client.Views;

/// <summary>一列可滚动的候选值。多列并排即构成"时:分"或"年/月/日"。</summary>
public sealed class WheelColumn : Border
{
    public const double RowHeight = 26;
    const int VisibleRows = 3;                    // 上一个 / 当前 / 下一个
    const double Duration = 150;                  // 单步滑动时长(ms)

    readonly IReadOnlyList<string> _items;
    readonly List<TextBlock> _labels = new();
    readonly TranslateTransform _slide = new();

    int _index;

    public event Action? SelectionChanged;

    public WheelColumn(IReadOnlyList<string> items, int selectedIndex, double width)
    {
        _items = items;
        _index = Math.Clamp(selectedIndex, 0, Math.Max(0, items.Count - 1));

        var stack = new StackPanel { RenderTransform = _slide };
        for (int i = 0; i < items.Count; i++)
        {
            var t = new TextBlock
            {
                Text = items[i],
                Height = RowHeight,
                TextAlignment = TextAlignment.Center,
                Padding = new Thickness(0, 4, 0, 0),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,   // 有底色才接得到点击
            };
            var captured = i;
            t.MouseLeftButtonUp += (_, e) => { e.Handled = true; SetIndex(captured); };
            _labels.Add(t);
            stack.Children.Add(t);
        }

        // 中间行的高亮带 —— 让人看出"选中的是中间这一格"
        var band = new Border
        {
            Height = RowHeight,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0),
            IsHitTestVisible = false,
        };
        band.SetResourceReference(BackgroundProperty, "BgSelected");
        band.SetResourceReference(CornerRadiusProperty, "RadiusSm");
        band.Opacity = 0.55;

        var layers = new Grid { ClipToBounds = true, Height = RowHeight * VisibleRows };
        layers.Children.Add(band);
        layers.Children.Add(stack);

        Child = layers;
        Width = width;
        BorderThickness = new Thickness(1);
        SnapsToDevicePixels = true;
        this.Dyn(BackgroundProperty, "BgSunken")
            .Dyn(BorderBrushProperty, "Border")
            .Dyn(CornerRadiusProperty, "RadiusSm");

        // 滚轮换一格(有头有尾,到边界即停)
        PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            SetIndex(_index + (e.Delta > 0 ? -1 : 1));
        };

        _slide.Y = OffsetFor(_index);   // 初始位置直接就位,不做入场动画
        Restyle();
    }

    public int SelectedIndex
    {
        get => _index;
        set => SetIndex(value);
    }

    /// <summary>选中项应居中:中间那一行的偏移。</summary>
    static double OffsetFor(int index) => (VisibleRows - 1) / 2.0 * RowHeight - index * RowHeight;

    void SetIndex(int next)
    {
        // ★ 有头有尾:越界就停在边界,不循环(用户裁定)
        next = Math.Clamp(next, 0, Math.Max(0, _items.Count - 1));
        if (next == _index) return;
        _index = next;

        // ★ 动画滑到目标行(而不是瞬间跳过去)
        var anim = new DoubleAnimation(OffsetFor(_index), TimeSpan.FromMilliseconds(Duration))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _slide.BeginAnimation(TranslateTransform.YProperty, anim);

        Restyle();
        SelectionChanged?.Invoke();
    }

    /// <summary>选中项加粗、离中心越远越淡 —— 转盘的纵深感。</summary>
    void Restyle()
    {
        for (int i = 0; i < _labels.Count; i++)
        {
            var d = Math.Abs(i - _index);
            var t = _labels[i];
            t.FontWeight = d == 0 ? FontWeights.SemiBold : FontWeights.Normal;
            t.Opacity = d == 0 ? 1.0 : d == 1 ? 0.55 : 0.3;
            t.SetResourceReference(TextBlock.ForegroundProperty, d == 0 ? "FgPrimary" : "FgSecondary");
        }
    }
}

public static class WheelPicker
{
    /// <summary>时间的最小单位(用户裁定:5 分钟)。</summary>
    public const int MinuteStep = 5;

    static readonly string[] Hours = Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToArray();
    static readonly string[] Minutes = Enumerable.Range(0, 60 / MinuteStep).Select(i => (i * MinuteStep).ToString("00")).ToArray();

    /// <summary>把任意时刻对齐到 5 分钟粒度(就近取整),并夹在 00:00–23:55 之间。</summary>
    public static TimeSpan Snap(TimeSpan t)
    {
        var total = (int)Math.Round(t.TotalMinutes / MinuteStep) * MinuteStep;
        total = Math.Clamp(total, 0, 24 * 60 - MinuteStep);
        return TimeSpan.FromMinutes(total);
    }

    /// <summary>时:分 两列滚轮。返回容器,并给出可直接改值的句柄。</summary>
    public static (FrameworkElement Element, Action<TimeSpan> Set) Time(TimeSpan initial, Action<TimeSpan> onChanged)
    {
        var snapped = Snap(initial);
        var hCol = new WheelColumn(Hours, snapped.Hours, 54);
        var mCol = new WheelColumn(Minutes, snapped.Minutes / MinuteStep, 54);

        var quiet = false;
        void Raise() { if (!quiet) onChanged(new TimeSpan(hCol.SelectedIndex, mCol.SelectedIndex * MinuteStep, 0)); }
        hCol.SelectionChanged += Raise;
        mCol.SelectionChanged += Raise;

        void Set(TimeSpan v)
        {
            // 外部改值(如开始时刻带动结束)不该再回调,否则会互相触发
            quiet = true;
            var s = Snap(v);
            hCol.SelectedIndex = s.Hours;
            mCol.SelectedIndex = s.Minutes / MinuteStep;
            quiet = false;
        }

        return (Row(hCol, Sep(":"), mCol), Set);
    }

    /// <summary>年 / 月 / 日 三列滚轮 —— 与时间滚轮同一外观。</summary>
    public static FrameworkElement Date(DateTime initial, Action<DateTime> onChanged)
    {
        var years = Enumerable.Range(DateTime.Today.Year - 1, 6).Select(y => y.ToString()).ToArray();
        var months = Enumerable.Range(1, 12).Select(m => m.ToString("00")).ToArray();
        var days = Enumerable.Range(1, 31).Select(d => d.ToString("00")).ToArray();

        var yIdx = Math.Max(0, Array.IndexOf(years, initial.Year.ToString()));
        var yCol = new WheelColumn(years, yIdx, 62);
        var mCol = new WheelColumn(months, initial.Month - 1, 48);
        var dCol = new WheelColumn(days, initial.Day - 1, 48);

        void Raise()
        {
            var y = int.Parse(years[yCol.SelectedIndex]);
            var m = mCol.SelectedIndex + 1;
            // 日列固定 31 项(避免换月重建整列打断动画);超出当月天数时夹回最后一天
            var maxDay = DateTime.DaysInMonth(y, m);
            if (dCol.SelectedIndex + 1 > maxDay) dCol.SelectedIndex = maxDay - 1;
            onChanged(new DateTime(y, m, Math.Min(dCol.SelectedIndex + 1, maxDay)));
        }
        yCol.SelectionChanged += Raise;
        mCol.SelectionChanged += Raise;
        dCol.SelectionChanged += Raise;

        return Row(yCol, Sep("/"), mCol, Sep("/"), dCol);
    }

    static TextBlock Sep(string text)
    {
        var t = new TextBlock
        {
            Text = text, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 5, 0),
        };
        t.Dyn(TextBlock.ForegroundProperty, "FgMuted");
        return t;
    }

    static FrameworkElement Row(params FrameworkElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }
}
