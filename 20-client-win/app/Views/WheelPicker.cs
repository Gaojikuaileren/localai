// P3c -- 竖直滚轮选择器(时间 / 日期)。
//
// 用户裁定:
//   · 非全天的开始/结束【不用输入框敲数字】,而是【竖直滚轮】滚动选择;
//   · 时间最小单位【5 分钟】;
//   · 【不做无限循环】—— 有头有尾,滚到边界就停;
//   · 滚动要有【动画】,不是硬切;
//   · 鼠标停在转盘上滚动时,滚的是【转盘自己】,不带动整页(用户反馈);
//   · 全天勾选后的起止日期用同一套外观(原生 DatePicker 是系统风,与整体脱节);
//   · 整体要【窄】—— 两个日期转盘要能并排塞进右侧抽屉,不能超宽被裁掉(用户反馈)。
//
// ★ 实现:一列 TextBlock 叠在裁剪容器里,靠 TranslateTransform 动画滑到目标行,
//   中间一行有高亮带,选中项加粗、离中心越远越淡 —— 才像转盘。
//   ListBox.ScrollIntoView 是瞬间跳到位的,做不出滑动感,已弃用。
//
// ★ 滚轮为什么曾经滚不动:编辑抽屉外层套了带 PassThrough 的 ScrollViewer,
//   它的 PreviewMouseWheel 在【隧道阶段】先于转盘触发;抽屉压缩到不再滚动后,
//   它"两个方向都滚不动"→ 把每个滚轮事件都吞掉并上抛,于是永远传不到转盘。
//   修法在 Wheel.PassThrough:发现滚轮落在转盘里就让路(见 IsInsideWheel)。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LocalAI.Client.Views;

/// <summary>一列可滚动的候选值。多列并排即构成"时:分"或"年/月/日"。</summary>
public sealed class WheelColumn : Border
{
    public const double RowHeight = 28;
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

        // ★ 关键:滑动列必须放进 Canvas,不能直接塞进定高的 Grid。
        //   若直接塞进 84px 高的格子,而列内容有 12~31 行(远高于 84),WPF 会给它
        //   自动加一个【布局裁剪】,只留最上面 84px —— 而且这个裁剪发生在 RenderTransform
        //   位移【之前】。于是选中项索引一大(如 7 月=6、29 日=28),位移把仅剩的头三行
        //   顶出视野,整列就变空白(诊断 PNG 实拍到:大索引列全空)。
        //   Canvas 会按子元素的【完整所需尺寸】排布、不加布局裁剪,位移后再由外层 Grid 裁剪。
        var stack = new StackPanel { RenderTransform = _slide, Width = width - 2 };
        for (int i = 0; i < items.Count; i++)
        {
            var t = new TextBlock
            {
                Text = items[i],
                Height = RowHeight,
                TextAlignment = TextAlignment.Center,
                Padding = new Thickness(0, 5, 0, 0),
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

        var canvas = new Canvas();          // 不约束子元素尺寸、不加布局裁剪
        canvas.Children.Add(stack);         // Canvas.Left/Top 默认 0

        var layers = new Grid { ClipToBounds = true, Height = RowHeight * VisibleRows };
        layers.Children.Add(band);
        layers.Children.Add(canvas);

        Child = layers;
        Width = width;
        BorderThickness = new Thickness(1);
        SnapsToDevicePixels = true;
        this.Dyn(BackgroundProperty, "BgSunken")
            .Dyn(BorderBrushProperty, "Border")
            .Dyn(CornerRadiusProperty, "RadiusSm");

        // 滚轮换一格(有头有尾,到边界即停)。挂在隧道阶段并消费,
        // 外层 ScrollViewer 因此不会再滚动 —— 前提是它先"让路"(见 Wheel.PassThrough)。
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

    /// <summary>
    /// 某个元素是否落在转盘内部。★ 外层带 PassThrough 的 ScrollViewer 用它来判断
    /// "该不该抢这次滚轮" —— 落在转盘里就让路,别把事件上抛(否则转盘永远收不到)。
    /// </summary>
    public static bool IsInsideWheel(DependencyObject? o)
    {
        while (o is not null)
        {
            if (o is WheelColumn) return true;
            o = o is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(o)
                : LogicalTreeHelper.GetParent(o);
        }
        return false;
    }
}

public static class WheelPicker
{
    /// <summary>时间的最小单位(用户裁定:5 分钟)。</summary>
    public const int MinuteStep = 5;

    // 窄一点的列宽:两个日期转盘要能并排塞进抽屉(用户反馈"设计超宽了")。
    const double HourW = 46, MinW = 46, YearW = 54, MonW = 40, DayW = 40;

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
        var hCol = new WheelColumn(Hours, snapped.Hours, HourW);
        var mCol = new WheelColumn(Minutes, snapped.Minutes / MinuteStep, MinW);

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
        var yCol = new WheelColumn(years, yIdx, YearW);
        var mCol = new WheelColumn(months, initial.Month - 1, MonW);
        var dCol = new WheelColumn(days, initial.Day - 1, DayW);

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
            Margin = new Thickness(3, 0, 3, 0),
        };
        t.Dyn(TextBlock.ForegroundProperty, "FgMuted");
        return t;
    }

    static FrameworkElement Row(params FrameworkElement[] children)
    {
        // 靠左排,别让转盘在 Star 列里被拉宽 —— 宽度只由列宽之和决定
        var p = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 4),
        };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }
}
