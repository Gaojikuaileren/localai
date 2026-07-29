// P3c -- 统一日历组件。主页板块与顶栏日历浮窗【用同一套逻辑】(用户裁定:之前两处不一致)。
//
// 交互参考 MiniCal:
//   · 一个紧凑的月历网格,有日程的日子标点;
//   · 点某天 -> 该天的日程【就地列在下方】,不再弹二级浮窗;
//   · ‹ › 翻月,「今日」一键回到当天;
//   · 「编辑」编辑【当前选中日期】的日程。
//
// 两种排布共用同一份状态与同一套交互:
//   Week  — 日期以周横排(主页顶部板块用;高度固定)
//   Month — 完整月历网格(顶栏浮窗默认;主页也可切换过去)
//
// ★ 数据源仍未接入 Apple 家庭共享日历(设计 §4.5 / 状态矩阵 §8):
//   每天一律"无日程",绝不伪造日程,也不伪造同步成功。

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LocalAI.Client.I18n;

namespace LocalAI.Client.Views;

public sealed record CalendarEvent(DateTime Start, DateTime End, string Title, string Owner, string Scope);

/// <summary>日程数据源。未接入 -> 空。AI 与编辑浮窗将来往这里写(同一个入口)。</summary>
public static class CalendarData
{
    public static List<CalendarEvent> Events { get; } = new();
    public static IEnumerable<CalendarEvent> On(DateTime day)
        => Events.Where(e => e.Start.Date == day.Date).OrderBy(e => e.Start);
}

public sealed class CalendarView : UserControl
{
    public enum Mode { Week, Month }

    static readonly CultureInfo Zh = new("zh-CN");

    /// <summary>周排布固定高度(标题 + 一行日期 + 当日日程行)。</summary>
    public const double WeekModeHeight = 132;
    /// <summary>月排布所需高度(标题 + 星期表头 + 最多 6 行日期 + 当日日程行)。
    /// ★ 月历必须给够高度,否则会被裁掉最后一两行(用户反馈"按月显示不全")。</summary>
    public const double MonthModeHeight = 300;

    public static double HeightFor(Mode m) => m == Mode.Month ? MonthModeHeight : WeekModeHeight;

    /// <summary>排布切换时通知宿主调整高度(周/月所需高度不同)。</summary>
    public event Action<Mode>? ModeChanged;

    Mode _mode;
    DateTime _anchor = DateTime.Today;     // 周排布=所在周;月排布=所在月
    DateTime _selected = DateTime.Today;
    int _weekDays = 7;

    readonly TextBlock _label = new();
    readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal };
    readonly ContentControl _body = new();
    readonly StackPanel _dayList = new();
    readonly ScrollViewer _dayScroll;

    public CalendarView(Mode mode)
    {
        _mode = mode;

        _label.FontWeight = FontWeights.SemiBold;
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(_label, Dock.Left); head.Children.Add(_label);
        DockPanel.SetDock(_actions, Dock.Right); head.Children.Add(_actions);

        _dayScroll = new ScrollViewer
        {
            Content = _dayList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 6, 0, 0),
        }.PassThrough();

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        DockPanel.SetDock(_body, Dock.Top); root.Children.Add(_body);
        root.Children.Add(_dayScroll);     // 选中日的日程就地列在下方(MiniCal 逻辑)
        Content = root;

        // 周排布:宽度足够铺两周,否则一周 —— 高度不变
        SizeChanged += (_, _) =>
        {
            if (_mode != Mode.Week) return;
            var want = ActualWidth >= 640 ? 14 : 7;
            if (want != _weekDays) { _weekDays = want; Rebuild(); }
        };

        Rebuild();
    }

    // ---------------------------------------------------------------- 构建
    void Rebuild()
    {
        // 标题
        _label.Text = _mode == Mode.Month
            ? _anchor.ToString("yyyy年 M月", Zh)
            : RangeLabel();

        // 操作区:‹ › 翻页 · 今日 · 周/月切换 · 编辑
        _actions.Children.Clear();
        _actions.Children.Add(Btn("‹", () => { Step(-1); Rebuild(); }));
        _actions.Children.Add(Btn("›", () => { Step(1); Rebuild(); }));
        if (_selected.Date != DateTime.Today || !ShowsToday())
            _actions.Children.Add(Btn("今日", () => { _anchor = _selected = DateTime.Today; Rebuild(); }));
        _actions.Children.Add(Btn(_mode == Mode.Month ? "周" : "月", () =>
        {
            _mode = _mode == Mode.Month ? Mode.Week : Mode.Month;
            _anchor = _selected;
            Rebuild();
            ModeChanged?.Invoke(_mode);   // 宿主据此调整面板高度 —— 月历比周条高得多
        }));
        _actions.Children.Add(Btn("编辑", () => OpenEditFlyout(_selected)));

        _body.Content = _mode == Mode.Week ? WeekRow() : MonthGrid();
        RebuildDayList();
    }

    string RangeLabel()
    {
        var start = StartOfWeek(_anchor);
        var end = start.AddDays(_weekDays - 1);
        return start.Month == end.Month ? start.ToString("yyyy年 M月", Zh) : $"{start:M月d日} – {end:M月d日}";
    }

    bool ShowsToday()
    {
        if (_mode == Mode.Month) return _anchor.Year == DateTime.Today.Year && _anchor.Month == DateTime.Today.Month;
        var start = StartOfWeek(_anchor);
        return DateTime.Today >= start && DateTime.Today < start.AddDays(_weekDays);
    }

    void Step(int dir) => _anchor = _mode == Mode.Month ? _anchor.AddMonths(dir) : _anchor.AddDays(dir * _weekDays);

    static DateTime StartOfWeek(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));   // 周一起始

    static Button Btn(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text, Height = 22, MinWidth = 24, Padding = new Thickness(7, 0, 7, 0),
            Margin = new Thickness(4, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(1), Background = Brushes.Transparent, FontSize = 11,
        };
        b.SetResourceReference(Button.BorderBrushProperty, "Border");
        b.SetResourceReference(Button.ForegroundProperty, "FgSecondary");
        b.Click += (_, _) => onClick();
        return b;
    }

    // ---------------------------------------------------------------- 周排布(主页顶部)
    UIElement WeekRow()
    {
        var grid = new UniformGrid { Rows = 1, Columns = _weekDays };
        var start = StartOfWeek(_anchor);
        for (int i = 0; i < _weekDays; i++) grid.Children.Add(DayCell(start.AddDays(i), showWeekday: true, height: 52));
        return grid;
    }

    // ---------------------------------------------------------------- 月排布(MiniCal 式网格)
    UIElement MonthGrid()
    {
        var panel = new StackPanel();

        var header = new UniformGrid { Rows = 1, Columns = 7, Margin = new Thickness(0, 0, 0, 3) };
        foreach (var d in new[] { "一", "二", "三", "四", "五", "六", "日" })
        {
            var t = new TextBlock { Text = d, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 10.5 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            header.Children.Add(t);
        }
        panel.Children.Add(header);

        var first = new DateTime(_anchor.Year, _anchor.Month, 1);
        var lead = ((int)first.DayOfWeek + 6) % 7;
        var days = DateTime.DaysInMonth(_anchor.Year, _anchor.Month);
        var grid = new UniformGrid { Columns = 7 };

        // 前后补齐灰日,保证网格不残缺(MiniCal 也是整格月历)
        for (int i = lead; i > 0; i--) grid.Children.Add(DayCell(first.AddDays(-i), showWeekday: false, height: 32, dim: true));
        for (int d = 1; d <= days; d++) grid.Children.Add(DayCell(new DateTime(_anchor.Year, _anchor.Month, d), showWeekday: false, height: 32));
        var tail = (7 - (lead + days) % 7) % 7;
        for (int i = 1; i <= tail; i++) grid.Children.Add(DayCell(first.AddDays(days + i - 1), showWeekday: false, height: 32, dim: true));

        panel.Children.Add(grid);
        return panel;
    }

    // ---------------------------------------------------------------- 单个日期格
    Border DayCell(DateTime day, bool showWeekday, double height, bool dim = false)
    {
        var isToday = day.Date == DateTime.Today;
        var isSelected = day.Date == _selected.Date;
        var evts = CalendarData.On(day).ToList();

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        if (showWeekday)
        {
            var wk = new TextBlock { Text = day.ToString("ddd", Zh), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 10 };
            wk.SetResourceReference(TextBlock.ForegroundProperty, isToday ? "FgOnAccent" : "FgMuted");
            stack.Children.Add(wk);
        }

        var num = new TextBlock
        {
            Text = day.Day.ToString(),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = showWeekday ? 15 : 12.5,
            FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
        };
        num.SetResourceReference(TextBlock.ForegroundProperty,
            isToday ? "FgOnAccent" : dim ? "FgMuted" : "FgPrimary");
        stack.Children.Add(num);

        // 有日程标点;无日程放等高透明占位 —— 否则有无日程会导致行高跳动
        if (evts.Count > 0)
        {
            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) };
            foreach (var _ in evts.Take(3))
            {
                var d = new System.Windows.Shapes.Ellipse { Width = 3.5, Height = 3.5, Margin = new Thickness(1.2, 0, 1.2, 0) };
                d.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, isToday ? "FgOnAccent" : "Accent");
                dots.Children.Add(d);
            }
            stack.Children.Add(dots);
        }
        else stack.Children.Add(new Border { Height = 4.5, Margin = new Thickness(0, 1, 0, 0) });

        var cell = new Border
        {
            Child = stack, Height = height, Margin = new Thickness(1.5),
            Cursor = System.Windows.Input.Cursors.Hand, BorderThickness = new Thickness(1),
        };
        cell.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        if (isToday) { cell.SetResourceReference(Border.BackgroundProperty, "Accent"); cell.BorderBrush = Brushes.Transparent; }
        else if (isSelected) { cell.SetResourceReference(Border.BackgroundProperty, "BgSunken"); cell.SetResourceReference(Border.BorderBrushProperty, "BorderStrong"); }
        else { cell.Background = Brushes.Transparent; cell.BorderBrush = Brushes.Transparent; }

        var captured = day.Date;
        // MiniCal 逻辑:点日期 = 选中并在下方就地展开当天日程,不弹二级浮窗
        cell.MouseLeftButtonUp += (_, _) => { _selected = captured; if (_mode == Mode.Month && captured.Month != _anchor.Month) _anchor = captured; Rebuild(); };
        return cell;
    }

    // ---------------------------------------------------------------- 选中日的日程(就地列出)
    void RebuildDayList()
    {
        _dayList.Children.Clear();

        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 3) };
        var title = new TextBlock { Text = _selected.ToString("M月d日 dddd", Zh), FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        title.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        DockPanel.SetDock(title, Dock.Left);
        head.Children.Add(title);
        _dayList.Children.Add(head);

        var evts = CalendarData.On(_selected).ToList();
        if (evts.Count == 0)
        {
            var none = new TextBlock { Text = "无日程 · " + Strings.Get("calendar.not_connected"), TextWrapping = TextWrapping.Wrap };
            none.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            none.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            _dayList.Children.Add(none);
            return;
        }

        foreach (var ev in evts)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), LastChildFill = true };
            var time = new TextBlock { Text = ev.Start.ToString("HH:mm"), Width = 44 };
            time.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            time.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            DockPanel.SetDock(time, Dock.Left);
            row.Children.Add(time);
            var t = new TextBlock { Text = ev.Title, TextTrimming = TextTrimming.CharacterEllipsis };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            row.Children.Add(t);
            _dayList.Children.Add(row);
        }
    }

    void OpenEditFlyout(DateTime day)
        => Flyout.Show(this, day.ToString("M月 d日", Zh) + " · 编辑日程",
                       CalendarEditor.Build(day, onSaved: () => { Flyout.CloseAll(); Rebuild(); }), width: 340);
}
