// P3c -- 统一日历组件。主页板块与顶栏日历浮窗【用同一套逻辑】。交互参考 MiniCal。
//
// 用户裁定的形态:
//   · 周排布:一行 7 天、共【两行】—— 第一行本周(正常),第二行下周(【灰色】);
//     翻页前进/后退一周,于是第二行会被提到第一行。
//   · 【只有周排布】在下方就地列出选中日的日程,占满多出来的纵向空间;
//     【月排布点日期则弹浮窗】显示当天日程 + 新增;当天没有日程时浮窗里只有"新增日程"。
//   · 点某条日程 -> 编辑那一条。【新增】按钮不放右上角:月排布在当日浮窗里,周排布在日程列表下方。
//   · 「今日」按钮放在【月份标签右侧】,不挤在翻页键那一堆里(否则翻页键会随它出现/消失位移)。
//   · 周/月两种排布【面板尺寸一样大】。
//
// ★ 数据源尚未接入 Apple 家庭共享日历(设计 §4.5 / 状态矩阵 §8):
//   编辑/新增可以填,但保存时如实拒绝并说明,绝不伪造日程或伪造同步成功。

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

    /// <summary>
    /// 面板固定高度 —— 周/月排布【共用同一尺寸】(用户裁定)。
    /// 以能完整容纳月历为准:标题 28 + 星期表头 18 + 6 行 × 30 + 余量。
    /// 周排布只用两行日期,剩下的纵向空间正好给"选中日的日程"列表。
    /// </summary>
    public const double PanelHeight = 268;

    const double MonthCellHeight = 30;
    const double WeekCellHeight = 46;

    Mode _mode;
    DateTime _anchor;                      // 周排布 = 第一行所在周的周一;月排布 = 所在月
    DateTime _selected = DateTime.Today;

    readonly TextBlock _label = new();
    readonly StackPanel _leftActions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _rightActions = new() { Orientation = Orientation.Horizontal };
    readonly ContentControl _body = new();
    readonly StackPanel _dayList = new();
    readonly ScrollViewer _dayScroll;

    public CalendarView(Mode mode)
    {
        _mode = mode;
        _anchor = mode == Mode.Week ? StartOfWeek(DateTime.Today) : DateTime.Today;

        _label.FontWeight = FontWeights.SemiBold;
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        // 左:月份标签 +「今日」—— 紧跟标签,不与翻页键混在一起
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_label);
        left.Children.Add(_leftActions);

        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(left, Dock.Left); head.Children.Add(left);
        DockPanel.SetDock(_rightActions, Dock.Right); head.Children.Add(_rightActions);

        _dayScroll = new ScrollViewer
        {
            Content = _dayList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 8, 0, 0),
        }.PassThrough();

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        DockPanel.SetDock(_body, Dock.Top); root.Children.Add(_body);
        root.Children.Add(_dayScroll);
        Content = root;

        Rebuild();
    }

    // ---------------------------------------------------------------- 构建
    void Rebuild()
    {
        _label.Text = _mode == Mode.Month ? _anchor.ToString("yyyy年 M月", Zh) : WeekRangeLabel();

        // 左:「今日」紧跟月份标签(仅当视野里看不到今天时出现)
        _leftActions.Children.Clear();
        if (!ShowsToday())
        {
            var today = Btn("今日", () =>
            {
                _selected = DateTime.Today;
                _anchor = _mode == Mode.Month ? DateTime.Today : StartOfWeek(DateTime.Today);
                Rebuild();
            });
            today.Margin = new Thickness(10, 0, 0, 0);
            _leftActions.Children.Add(today);
        }

        // 右:翻页 · 周/月切换。位置固定,不随「今日」出现而位移。
        _rightActions.Children.Clear();
        _rightActions.Children.Add(Btn("‹", () => { Step(-1); Rebuild(); }));
        _rightActions.Children.Add(Btn("›", () => { Step(1); Rebuild(); }));
        _rightActions.Children.Add(Btn(_mode == Mode.Month ? "周" : "月", () =>
        {
            _mode = _mode == Mode.Month ? Mode.Week : Mode.Month;
            _anchor = _mode == Mode.Week ? StartOfWeek(_selected) : _selected;
            Rebuild();
        }));
        // ★ 右上角【不放】新增按钮(用户裁定):月排布的新增在当日浮窗里,周排布的在日程列表下方。

        _body.Content = _mode == Mode.Week ? WeekRows() : MonthGrid();

        // ★ 只有周排布在下方列当日日程;月排布靠点日期弹浮窗(用户裁定)
        _dayScroll.Visibility = _mode == Mode.Week ? Visibility.Visible : Visibility.Collapsed;
        if (_mode == Mode.Week) RebuildDayList();
    }

    string WeekRangeLabel()
    {
        var end = _anchor.AddDays(13);   // 两行 = 14 天
        return _anchor.Month == end.Month ? _anchor.ToString("yyyy年 M月", Zh)
                                          : $"{_anchor:M月d日} – {end:M月d日}";
    }

    bool ShowsToday()
        => _mode == Mode.Month
            ? _anchor.Year == DateTime.Today.Year && _anchor.Month == DateTime.Today.Month
            : DateTime.Today >= _anchor && DateTime.Today < _anchor.AddDays(14);

    // 周排布每次走【一周】—— 于是第二行(下周)被提到第一行
    void Step(int dir) => _anchor = _mode == Mode.Month ? _anchor.AddMonths(dir) : _anchor.AddDays(dir * 7);

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

    static UniformGrid WeekdayHeader()
    {
        var header = new UniformGrid { Rows = 1, Columns = 7, Margin = new Thickness(0, 0, 0, 2) };
        foreach (var d in new[] { "一", "二", "三", "四", "五", "六", "日" })
        {
            var t = new TextBlock { Text = d, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 10.5 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            header.Children.Add(t);
        }
        return header;
    }

    // ---------------------------------------------------------------- 周排布:两行 × 7 天
    UIElement WeekRows()
    {
        var panel = new StackPanel();
        panel.Children.Add(WeekdayHeader());
        // 第一行 = 本周(正常);第二行 = 下周(灰)
        for (int row = 0; row < 2; row++)
        {
            var grid = new UniformGrid { Rows = 1, Columns = 7 };
            for (int i = 0; i < 7; i++)
                grid.Children.Add(DayCell(_anchor.AddDays(row * 7 + i), WeekCellHeight, dim: row == 1));
            panel.Children.Add(grid);
        }
        return panel;
    }

    // ---------------------------------------------------------------- 月排布
    UIElement MonthGrid()
    {
        var panel = new StackPanel();
        panel.Children.Add(WeekdayHeader());

        var first = new DateTime(_anchor.Year, _anchor.Month, 1);
        var lead = ((int)first.DayOfWeek + 6) % 7;
        var days = DateTime.DaysInMonth(_anchor.Year, _anchor.Month);
        var grid = new UniformGrid { Columns = 7 };

        for (int i = lead; i > 0; i--) grid.Children.Add(DayCell(first.AddDays(-i), MonthCellHeight, dim: true));
        for (int d = 1; d <= days; d++) grid.Children.Add(DayCell(new DateTime(_anchor.Year, _anchor.Month, d), MonthCellHeight));
        var tail = (7 - (lead + days) % 7) % 7;
        for (int i = 1; i <= tail; i++) grid.Children.Add(DayCell(first.AddDays(days + i - 1), MonthCellHeight, dim: true));

        panel.Children.Add(grid);
        return panel;
    }

    // ---------------------------------------------------------------- 单个日期格
    Border DayCell(DateTime day, double height, bool dim = false)
    {
        var isToday = day.Date == DateTime.Today;
        var isSelected = day.Date == _selected.Date;
        var evts = CalendarData.On(day).ToList();

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var num = new TextBlock
        {
            Text = day.Day.ToString(),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = height >= 40 ? 14.5 : 12.5,
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
                var d = new System.Windows.Shapes.Ellipse { Width = 3.5, Height = 3.5, Margin = new Thickness(1.2, 0, 1.2, 0), Opacity = dim ? 0.55 : 1 };
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
        cell.MouseLeftButtonUp += (s, _) =>
        {
            _selected = captured;
            var wasMonth = _mode == Mode.Month;
            // 月排布点到上/下月的灰日 -> 跟着翻到那个月
            if (wasMonth && captured.Month != _anchor.Month) _anchor = captured;
            Rebuild();
            // ★ 月排布没有下方日程区,所以点日期弹浮窗显示当天日程 + 新增(用户裁定)
            if (wasMonth) OpenDayFlyout((FrameworkElement)s, captured);
        };
        return cell;
    }

    // ---------------------------------------------------------------- 月排布:当日浮窗
    void OpenDayFlyout(FrameworkElement anchor, DateTime day)
    {
        var body = new StackPanel();
        var evts = CalendarData.On(day).ToList();

        if (evts.Count == 0)
        {
            // 没有日程时,浮窗里【只有】新增(用户裁定)——不显示"无日程"之类的空行
            var addOnly = Ui.Primary("+ 新增日程", (_, _) => { Flyout.CloseAll(); OpenEditor(day, null); });
            body.Children.Add(addOnly);
        }
        else
        {
            foreach (var ev in evts) body.Children.Add(EventRow(ev, compact: false));
            var add = Ui.Secondary("+ 新增日程", (_, _) => { Flyout.CloseAll(); OpenEditor(day, null); });
            add.Margin = new Thickness(0, 10, 0, 0);
            body.Children.Add(add);
        }

        Flyout.Show(anchor, day.ToString("M月 d日 dddd", Zh), body, width: 300);
    }

    // ---------------------------------------------------------------- 周排布:下方就地列出
    void RebuildDayList()
    {
        _dayList.Children.Clear();

        var title = new TextBlock { Text = _selected.ToString("M月d日 dddd", Zh), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        title.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        _dayList.Children.Add(title);

        var evts = CalendarData.On(_selected).ToList();
        if (evts.Count == 0)
        {
            var none = new TextBlock { Text = "无日程 · " + Strings.Get("calendar.not_connected"), TextWrapping = TextWrapping.Wrap };
            none.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            none.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            _dayList.Children.Add(none);
        }
        else foreach (var ev in evts) _dayList.Children.Add(EventRow(ev, compact: true));

        // 新增按钮在【日程下方】(用户裁定)。无论当天有没有日程都在。
        var add = Ui.Secondary("+ 新增日程", (_, _) => OpenEditor(_selected, null));
        add.Margin = new Thickness(0, 8, 0, 0);
        add.Height = 26;
        add.FontSize = 11.5;
        _dayList.Children.Add(add);
    }

    /// <summary>一条日程。点它 = 编辑这一条(用户裁定)。</summary>
    Border EventRow(CalendarEvent ev, bool compact)
    {
        var row = new DockPanel { LastChildFill = true };
        var time = new TextBlock { Text = ev.Start.ToString("HH:mm"), Width = 44, VerticalAlignment = VerticalAlignment.Center };
        time.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        time.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        DockPanel.SetDock(time, Dock.Left);
        row.Children.Add(time);

        var t = new TextBlock { Text = ev.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        t.SetResourceReference(TextBlock.FontSizeProperty, compact ? "FontCaption" : "FontBody");
        row.Children.Add(t);

        var hit = new Border { Child = row, Padding = new Thickness(4, 3, 4, 3), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
        hit.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        hit.MouseEnter += (_, _) => hit.SetResourceReference(Border.BackgroundProperty, "BgHover");
        hit.MouseLeave += (_, _) => hit.Background = Brushes.Transparent;
        hit.MouseLeftButtonUp += (_, _) => { Flyout.CloseAll(); OpenEditor(ev.Start.Date, ev); };
        return hit;
    }

    /// <summary>existing 为 null = 新增;否则 = 编辑那一条。</summary>
    void OpenEditor(DateTime day, CalendarEvent? existing)
        => Flyout.Show(this,
                       day.ToString("M月 d日", Zh) + (existing is null ? " · 新增日程" : " · 编辑日程"),
                       CalendarEditor.Build(day, existing, onSaved: () => { Flyout.CloseAll(); Rebuild(); }),
                       width: 340);
}
