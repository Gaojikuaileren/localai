// P3c -- 竖直滚轮选择器(时间 / 日期)。
//
// 用户裁定:
//   · 非全天的开始/结束【不用输入框敲数字】,而是【竖直滚轮】滚动选择;
//   · 时间最小单位【5 分钟】;
//   · 【不做无限循环】—— 有头有尾,滚到边界就停。
//   · 全天勾选后的开始/结束日期也用同一套外观(此前用的是原生 DatePicker,风格不统一)。
//
// 实现:ListBox + 自定义项模板 + SnapsToDevicePixels;选中项居中高亮。
// 不用 WPF 的 DatePicker/系统控件 —— 它模板复杂且外观是系统风,与整体脱节。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LocalAI.Client.Views;

/// <summary>一列可滚动的候选值。多列并排即构成"时:分"或"年 月 日"。</summary>
public sealed class WheelColumn : Border
{
    readonly ListBox _list = new();

    public event Action? SelectionChanged;

    public WheelColumn(IReadOnlyList<string> items, int selectedIndex, double width, double visibleRows = 3)
    {
        _list.ItemsSource = items;
        _list.SelectedIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, items.Count - 1));
        _list.Width = width;
        _list.BorderThickness = new Thickness(0);
        _list.Background = Brushes.Transparent;
        _list.HorizontalContentAlignment = HorizontalAlignment.Center;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.ItemContainerStyle = ItemStyle();
        // 3 行可见 ≈ 上一个 / 当前 / 下一个,滚轮的手感来源
        _list.Height = RowHeight * visibleRows;
        _list.SelectionChanged += (_, _) =>
        {
            SelectionChanged?.Invoke();
            _list.ScrollIntoView(_list.SelectedItem);
        };
        // 滚轮直接换选中项(而不是只滚动视图)—— 这才像"轮盘"
        _list.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            var next = _list.SelectedIndex + (e.Delta > 0 ? -1 : 1);
            // ★ 有头有尾:到边界就停,不循环(用户裁定)
            if (next < 0 || next >= items.Count) return;
            _list.SelectedIndex = next;
        };

        Child = _list;
        Padding = new Thickness(0, 2, 0, 2);
        BorderThickness = new Thickness(1);
        SnapsToDevicePixels = true;
        this.Dyn(BackgroundProperty, "BgSunken")
            .Dyn(BorderBrushProperty, "Border")
            .Dyn(CornerRadiusProperty, "RadiusSm");
    }

    public const double RowHeight = 26;

    public int SelectedIndex
    {
        get => _list.SelectedIndex;
        set => _list.SelectedIndex = Math.Max(0, value);
    }

    public string? SelectedText => _list.SelectedItem as string;

    /// <summary>装载完成后把选中项滚到可见位置(构造时布局还没跑,滚不动)。</summary>
    public void ScrollToSelection()
    {
        if (_list.SelectedItem is not null) _list.ScrollIntoView(_list.SelectedItem);
    }

    static Style ItemStyle()
    {
        var st = new Style(typeof(ListBoxItem));
        st.Setters.Add(new Setter(FrameworkElement.HeightProperty, RowHeight));
        st.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        st.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        st.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        st.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));

        // 模板:选中时填强调色、圆角;未选中透明,hover 给底色
        var tpl = new ControlTemplate(typeof(ListBoxItem));
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.MarginProperty, new Thickness(3, 1, 3, 1));
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        tpl.VisualTree = bd;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BgHover"), "Bd"));
        tpl.Triggers.Add(hover);

        var sel = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        sel.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Accent"), "Bd"));
        sel.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("FgOnAccent")));
        sel.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        tpl.Triggers.Add(sel);

        st.Setters.Add(new Setter(Control.TemplateProperty, tpl));
        return st;
    }
}

public static class WheelPicker
{
    /// <summary>时间的最小单位(用户裁定:5 分钟)。</summary>
    public const int MinuteStep = 5;

    static readonly string[] Hours = Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToArray();
    static readonly string[] Minutes = Enumerable.Range(0, 60 / MinuteStep).Select(i => (i * MinuteStep).ToString("00")).ToArray();

    /// <summary>把任意时刻对齐到 5 分钟粒度(就近取整)。</summary>
    public static TimeSpan Snap(TimeSpan t)
    {
        var total = (int)Math.Round(t.TotalMinutes / MinuteStep) * MinuteStep;
        total = Math.Clamp(total, 0, 24 * 60 - MinuteStep);
        return TimeSpan.FromMinutes(total);
    }

    /// <summary>时:分 两列滚轮。onChanged 给出对齐后的时刻。</summary>
    public static FrameworkElement Time(TimeSpan initial, Action<TimeSpan> onChanged)
    {
        var snapped = Snap(initial);
        var hCol = new WheelColumn(Hours, snapped.Hours, 52);
        var mCol = new WheelColumn(Minutes, snapped.Minutes / MinuteStep, 52);

        void Raise() => onChanged(new TimeSpan(hCol.SelectedIndex, mCol.SelectedIndex * MinuteStep, 0));
        hCol.SelectionChanged += Raise;
        mCol.SelectionChanged += Raise;

        return Row(new FrameworkElement[] { hCol, Sep(":"), mCol }, hCol, mCol);
    }

    /// <summary>年 / 月 / 日 三列滚轮 —— 与时间滚轮同一外观(全天模式下用它选起止日期)。</summary>
    public static FrameworkElement Date(DateTime initial, Action<DateTime> onChanged)
    {
        var years = Enumerable.Range(DateTime.Today.Year - 1, 6).Select(y => y.ToString()).ToArray();
        var months = Enumerable.Range(1, 12).Select(m => m.ToString("00")).ToArray();

        var yCol = new WheelColumn(years, Array.IndexOf(years, initial.Year.ToString()), 62);
        var mCol = new WheelColumn(months, initial.Month - 1, 46);
        WheelColumn dCol = null!;
        var dayHost = new ContentControl();

        void BuildDays(int keepDay)
        {
            var y = int.Parse(years[Math.Max(0, yCol.SelectedIndex)]);
            var m = mCol.SelectedIndex + 1;
            var n = DateTime.DaysInMonth(y, m);
            var days = Enumerable.Range(1, n).Select(d => d.ToString("00")).ToArray();
            dCol = new WheelColumn(days, Math.Clamp(keepDay - 1, 0, n - 1), 46);
            dCol.SelectionChanged += Raise;
            dayHost.Content = dCol;
            dCol.ScrollToSelection();
        }

        void Raise()
        {
            var y = int.Parse(years[Math.Max(0, yCol.SelectedIndex)]);
            var m = mCol.SelectedIndex + 1;
            var d = Math.Clamp(dCol.SelectedIndex + 1, 1, DateTime.DaysInMonth(y, m));
            onChanged(new DateTime(y, m, d));
        }

        // 换年/月要重算这个月有多少天(2 月 30 日这种不能出现)
        yCol.SelectionChanged += () => { BuildDays(dCol.SelectedIndex + 1); Raise(); };
        mCol.SelectionChanged += () => { BuildDays(dCol.SelectedIndex + 1); Raise(); };
        BuildDays(initial.Day);

        return Row(new FrameworkElement[] { yCol, Sep("/"), mCol, Sep("/"), dayHost }, yCol, mCol);
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

    static FrameworkElement Row(FrameworkElement[] children, params WheelColumn[] toScroll)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        foreach (var c in children) p.Children.Add(c);
        // 布局跑完之后再滚到选中项 —— 构造期滚不动
        p.Loaded += (_, _) => { foreach (var w in toScroll) w.ScrollToSelection(); };
        return p;
    }
}
