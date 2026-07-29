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
using System.Windows.Media.Animation;
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
    bool _animating;

    readonly TextBlock _label = new();
    readonly Border _labelButton;
    readonly StackPanel _leftActions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _rightActions = new() { Orientation = Orientation.Horizontal };
    readonly ContentControl _body = new();
    readonly StackPanel _dayList = new();
    readonly ScrollViewer _dayScroll;
    readonly DockPanel _dayArea;
    readonly Button _addButton;

    public CalendarView(Mode mode)
    {
        _mode = mode;
        _anchor = mode == Mode.Week ? StartOfWeek(DateTime.Today) : DateTime.Today;

        _label.FontWeight = FontWeights.SemiBold;
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        // 年月标签要【看起来像按钮】,否则用户不知道能点(用户反馈)。边框 + hover 底色 + 下拉箭头。
        var caret = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L8,0 L4,5 Z"),
            Margin = new Thickness(7, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        caret.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "FgMuted");
        var labelRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        labelRow.Children.Add(_label);
        labelRow.Children.Add(caret);
        _labelButton = new Border
        {
            Child = labelRow,
            Padding = new Thickness(8, 3, 8, 3),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent,
            ToolTip = "选择年份 / 月份",
        };
        _labelButton.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        _labelButton.SetResourceReference(Border.BorderBrushProperty, "Border");
        _labelButton.MouseEnter += (_, _) => _labelButton.SetResourceReference(Border.BackgroundProperty, "BgHover");
        _labelButton.MouseLeave += (_, _) => _labelButton.Background = Brushes.Transparent;
        _labelButton.MouseLeftButtonUp += (s, _) => OpenMonthPicker((FrameworkElement)s);

        // 左:月份标签 +「今日」—— 紧跟标签,不与翻页键混在一起
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_labelButton);
        left.Children.Add(_leftActions);

        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(left, Dock.Left); head.Children.Add(left);
        DockPanel.SetDock(_rightActions, Dock.Right); head.Children.Add(_rightActions);

        _dayScroll = new ScrollViewer
        {
            Content = _dayList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough();

        // 周排布下方区域:左边是当日日程列表,右边是"新增日程"—— 二者【并列】(用户裁定),
        // 而不是把新增按到列表末尾。
        _dayArea = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 8, 0, 0) };
        _addButton = Ui.Secondary("+ 新增日程", (_, _) => OpenEditor(_selected, null));
        _addButton.Height = 26;
        _addButton.FontSize = 11.5;
        _addButton.VerticalAlignment = VerticalAlignment.Top;
        _addButton.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(_addButton, Dock.Right);
        _dayArea.Children.Add(_addButton);
        _dayArea.Children.Add(_dayScroll);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        DockPanel.SetDock(_body, Dock.Top); root.Children.Add(_body);
        root.Children.Add(_dayArea);
        Content = root;

        Rebuild();
    }

    // ---------------------------------------------------------------- 构建
    void Rebuild()
    {
        _label.Text = _mode == Mode.Month ? _anchor.ToString("yyyy年 M月", Zh) : WeekRangeLabel();

        RefreshTodayButton();

        // 右:翻页 · 周/月切换。位置固定,不随「今日」出现而位移。
        _rightActions.Children.Clear();
        _rightActions.Children.Add(Btn("‹", () => Page(-1)));
        _rightActions.Children.Add(Btn("›", () => Page(1)));
        _rightActions.Children.Add(Btn(_mode == Mode.Month ? "周" : "月", () =>
        {
            _mode = _mode == Mode.Month ? Mode.Week : Mode.Month;
            _anchor = _mode == Mode.Week ? StartOfWeek(_selected) : _selected;
            Rebuild();
        }));
        // ★ 右上角【不放】新增按钮(用户裁定):月排布的新增在当日浮窗里,周排布的在日程列表下方。

        _body.Content = _mode == Mode.Week ? WeekRows() : MonthGrid();

        // ★ 只有周排布在下方列当日日程;月排布靠点日期弹浮窗(用户裁定)
        _dayArea.Visibility = _mode == Mode.Week ? Visibility.Visible : Visibility.Collapsed;
        if (_mode == Mode.Week) RebuildDayList();
    }

    /// <summary>「回到今日」紧跟月份标签,仅当视野里看不到今天时出现。</summary>
    void RefreshTodayButton()
    {
        _leftActions.Children.Clear();
        if (ShowsToday()) return;
        var today = Btn("回到今日", () =>
        {
            _selected = DateTime.Today;
            _anchor = _mode == Mode.Month ? DateTime.Today : StartOfWeek(DateTime.Today);
            Rebuild();
        });
        today.Margin = new Thickness(10, 0, 0, 0);
        _leftActions.Children.Add(today);
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

    /// <summary>
    /// 翻页 = 【横向滑动】,让人看清前后关系(硬切分不清方向)。
    /// 性能:只【多建一页】—— 新页与旧页短暂并存,动画结束立刻丢弃旧页。
    /// 不做多周预加载:日历一页只有几十个轻量元素,构建极快,预加载的收益抵不上常驻内存。
    /// </summary>
    void Page(int dir)
    {
        if (_animating) return;   // 动画中忽略连点,避免堆出多层残影
        _animating = true;

        var outgoing = _body.Content as UIElement;
        Step(dir);
        _label.Text = _mode == Mode.Month ? _anchor.ToString("yyyy年 M月", Zh) : WeekRangeLabel();
        RefreshTodayButton();
        var incoming = _mode == Mode.Week ? WeekRows() : MonthGrid();

        if (outgoing is null) { _body.Content = incoming; _animating = false; AfterPage(); return; }

        var host = new Grid { ClipToBounds = true };
        var outT = new TranslateTransform();
        var inT = new TranslateTransform();
        outgoing.RenderTransform = outT;
        incoming.RenderTransform = inT;
        host.Children.Add(outgoing);
        host.Children.Add(incoming);
        _body.Content = host;

        var dist = Math.Max(240, ActualWidth > 0 ? ActualWidth : 320);
        inT.X = dir > 0 ? dist : -dist;

        var dur = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        outT.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, dir > 0 ? -dist : dist, dur) { EasingFunction = ease });
        var slideIn = new DoubleAnimation(inT.X, 0, dur) { EasingFunction = ease };
        slideIn.Completed += (_, _) =>
        {
            host.Children.Clear();          // 丢弃旧页,只留新页
            incoming.RenderTransform = null;
            _body.Content = incoming;
            _animating = false;
            AfterPage();
        };
        inT.BeginAnimation(TranslateTransform.XProperty, slideIn);
    }

    /// <summary>翻页动画收尾:周排布要刷新下方当日日程区。</summary>
    void AfterPage()
    {
        _dayArea.Visibility = _mode == Mode.Week ? Visibility.Visible : Visibility.Collapsed;
        if (_mode == Mode.Week) RebuildDayList();
    }

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
        cell.MouseLeftButtonUp += (_, _) =>
        {
            _selected = captured;
            var wasMonth = _mode == Mode.Month;
            if (wasMonth && captured.Month != _anchor.Month) _anchor = captured;
            Rebuild();
            // ★ 月排布没有下方日程区,点日期弹浮窗显示当天日程 + 新增(用户裁定)。
            //   注意:Rebuild() 已经把刚被点的那个格子换成新对象了,拿它当锚点会定位失败
            //   (这正是"月视图点日期弹不出浮窗"的原因)。锚到本视图 + 在鼠标处弹出。
            if (wasMonth) OpenDayFlyout(captured);
        };
        return cell;
    }

    // ---------------------------------------------------------------- 月排布:当日浮窗
    void OpenDayFlyout(DateTime day)
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

        Flyout.ShowAtMouse(this, day.ToString("M月 d日 dddd", Zh), body, width: 300);
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

    // ---------------------------------------------------------------- 年 / 月 选择浮窗
    // 点左上角年月标签打开。翻页键适合走一两格,跨年跨月就该直接挑。
    void OpenMonthPicker(FrameworkElement anchor)
    {
        var body = new StackPanel();
        var year = _anchor.Year;

        var yearRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
        var yearLabel = new TextBlock { Text = year + " 年", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        yearLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        var monthGrid = new UniformGrid { Columns = 4 };

        void FillMonths()
        {
            yearLabel.Text = year + " 年";
            monthGrid.Children.Clear();
            for (int m = 1; m <= 12; m++)
            {
                var isCurrent = m == _anchor.Month && year == _anchor.Year;
                var isThisMonth = m == DateTime.Today.Month && year == DateTime.Today.Year;
                var t = new TextBlock { Text = m + " 月", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                t.SetResourceReference(TextBlock.ForegroundProperty, isCurrent ? "FgOnAccent" : isThisMonth ? "Accent" : "FgPrimary");
                var cell = new Border { Child = t, Height = 34, Margin = new Thickness(2), Cursor = System.Windows.Input.Cursors.Hand };
                cell.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
                if (isCurrent) cell.SetResourceReference(Border.BackgroundProperty, "Accent");
                else cell.Background = Brushes.Transparent;
                var capturedMonth = m; var capturedYear = year;
                cell.MouseEnter += (_, _) => { if (!isCurrent) cell.SetResourceReference(Border.BackgroundProperty, "BgHover"); };
                cell.MouseLeave += (_, _) => { if (!isCurrent) cell.Background = Brushes.Transparent; };
                cell.MouseLeftButtonUp += (_, _) =>
                {
                    // 选定年月:月排布跳到该月;周排布跳到该月第一周
                    var target = new DateTime(capturedYear, capturedMonth, 1);
                    _anchor = _mode == Mode.Month ? target : StartOfWeek(target);
                    _selected = target;
                    Flyout.CloseAll();
                    Rebuild();
                };
                monthGrid.Children.Add(cell);
            }
        }

        var prevY = Btn("‹", () => { year--; FillMonths(); });
        var nextY = Btn("›", () => { year++; FillMonths(); });
        prevY.Margin = new Thickness(0); DockPanel.SetDock(prevY, Dock.Left); yearRow.Children.Add(prevY);
        DockPanel.SetDock(nextY, Dock.Right); yearRow.Children.Add(nextY);
        yearRow.Children.Add(yearLabel);

        FillMonths();
        body.Children.Add(yearRow);
        body.Children.Add(monthGrid);

        Flyout.Show(anchor, "选择年份 / 月份", body, width: 260);
    }

    /// <summary>existing 为 null = 新增;否则 = 编辑那一条。</summary>
    void OpenEditor(DateTime day, CalendarEvent? existing)
        => Flyout.ShowAtMouse(this,   // 在鼠标边弹出(用户裁定)
                       day.ToString("M月 d日", Zh) + (existing is null ? " · 新增日程" : " · 编辑日程"),
                       CalendarEditor.Build(day, existing, onSaved: () => { Flyout.CloseAll(); Rebuild(); }),
                       width: 340);
}
