// P3c -- 日历。用户裁定的形态:
//   · 默认【当前周 + 下一周】每日详细日程;可切【整月】(有日程的日子标一个点,尺寸不变);
//   · 可【滚动】,并有【回到当日】;
//   · 点【单个日期】-> 当日时间线【浮窗】;
//   · 点【编辑】-> 编辑【当前选中日期】的日程,同样是【浮窗】(不是独立窗口)。
//   · AI 与手动编辑写同一份数据模型。
//
// ★ 硬约束不变(设计 §4.5 / 状态矩阵 §8):数据源尚未接入 Apple 家庭共享日历,
//   所以没有任何日程 —— 一律显示"无日程",绝不伪造日程或伪造同步成功;
//   编辑浮窗可以填,但保存时如实拒绝并说明原因。

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LocalAI.Client.I18n;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed record CalendarEvent(DateTime Start, DateTime End, string Title, string Owner, string Scope);

public sealed class CalendarPanel : UserControl
{
    public enum Mode { TwoWeeks, Month }

    static readonly CultureInfo Zh = new("zh-CN");

    /// <summary>日程数据源。未接入 -> 空。AI 与编辑浮窗将来往这里写。</summary>
    public static List<CalendarEvent> Events { get; } = new();

    Mode _mode;
    DateTime _selected = DateTime.Today;
    DateTime _anchor = DateTime.Today;      // 两周视图的起点周 / 整月视图的月份
    readonly StackPanel _root = new();
    ScrollViewer? _scroll;

    public CalendarPanel(Mode mode = Mode.TwoWeeks, bool expanded = false)
    {
        _mode = mode;
        Content = _root;
        Build();
    }

    void Build()
    {
        _root.Children.Clear();

        // ---- 头:日期 + 回到当日 + 视图切换 + 编辑 ----
        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };

        var label = new TextBlock
        {
            Text = _mode == Mode.Month ? _anchor.ToString("yyyy年 M月", Zh) : _selected.ToString("M月 d日 dddd", Zh),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        DockPanel.SetDock(label, Dock.Left);
        head.Children.Add(label);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        // 回到当日 —— 滚远了要能一键回来
        if (_selected.Date != DateTime.Today || _anchor.Date != DateTime.Today)
            actions.Children.Add(SmallButton("今日", () => { _selected = _anchor = DateTime.Today; Build(); }));
        actions.Children.Add(SmallButton(_mode == Mode.TwoWeeks ? "整月" : "两周", () =>
        {
            _mode = _mode == Mode.TwoWeeks ? Mode.Month : Mode.TwoWeeks;
            Build();
        }));
        actions.Children.Add(SmallButton("编辑", () => OpenEditFlyout(_selected)));
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        _root.Children.Add(head);

        // ---- 主体(可滚动)----
        _scroll = new ScrollViewer
        {
            Content = _mode == Mode.TwoWeeks ? TwoWeeksList() : MonthGrid(),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _root.Children.Add(_scroll);

        var notice = Ui.Caption(Strings.Get("calendar.not_connected"));
        notice.Margin = new Thickness(0, 8, 0, 0);
        _root.Children.Add(notice);
    }

    static Button SmallButton(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text, Height = 24, Padding = new Thickness(9, 0, 9, 0),
            Margin = new Thickness(5, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(1), Background = Brushes.Transparent, FontSize = 11.5,
        };
        b.SetResourceReference(Button.BorderBrushProperty, "Border");
        b.SetResourceReference(Button.ForegroundProperty, "FgSecondary");
        b.Click += (_, _) => onClick();
        return b;
    }

    // ---------------------------------------------------------------- 两周:每日详细日程
    UIElement TwoWeeksList()
    {
        var monday = _anchor.AddDays(-(((int)_anchor.DayOfWeek + 6) % 7));
        var list = new StackPanel();

        for (int i = 0; i < 14; i++)
        {
            var day = monday.AddDays(i);
            if (i == 0 || i == 7) list.Children.Add(WeekLabel(i == 0 ? "本周" : "下周", i == 0));

            var isToday = day == DateTime.Today;
            var isSelected = day == _selected.Date;

            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1, 0, 1) };

            var dateBox = new Border { Width = 40, Padding = new Thickness(0, 3, 0, 3), Margin = new Thickness(0, 0, 10, 0) };
            dateBox.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            if (isToday) dateBox.SetResourceReference(Border.BackgroundProperty, "Accent");
            else if (isSelected) dateBox.SetResourceReference(Border.BackgroundProperty, "BgSunken");

            var dnum = new TextBlock { Text = day.Day.ToString(), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12.5, FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal };
            dnum.SetResourceReference(TextBlock.ForegroundProperty, isToday ? "FgOnAccent" : "FgPrimary");
            var dwk = new TextBlock { Text = day.ToString("ddd", Zh), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 10 };
            dwk.SetResourceReference(TextBlock.ForegroundProperty, isToday ? "FgOnAccent" : "FgMuted");
            dateBox.Child = Ui.Stack(dnum, dwk);
            DockPanel.SetDock(dateBox, Dock.Left);
            row.Children.Add(dateBox);

            var evts = Events.Where(e => e.Start.Date == day).OrderBy(e => e.Start).ToList();
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (evts.Count == 0)
            {
                var none = new TextBlock { Text = "无日程" };
                none.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                none.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
                col.Children.Add(none);
            }
            else foreach (var ev in evts.Take(3))
            {
                var line = new TextBlock { Text = $"{ev.Start:HH:mm}  {ev.Title}", TextTrimming = TextTrimming.CharacterEllipsis };
                line.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                line.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
                col.Children.Add(line);
            }
            row.Children.Add(col);

            // 整行可点 -> 当日时间线浮窗
            var hit = new Border { Child = row, Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(2) };
            hit.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            var captured = day;
            hit.MouseLeftButtonUp += (s, _) => { _selected = captured; Build(); OpenDayFlyout((FrameworkElement)s, captured); };
            list.Children.Add(hit);

            if (i < 13)
            {
                var sep = new Border { Height = 1, Margin = new Thickness(52, 1, 0, 1), Opacity = 0.55 };
                sep.SetResourceReference(Border.BackgroundProperty, "Border");
                list.Children.Add(sep);
            }
        }
        return list;
    }

    static TextBlock WeekLabel(string text, bool first)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, first ? 0 : 10, 0, 4), FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        return t;
    }

    // ---------------------------------------------------------------- 整月:只用点标记
    UIElement MonthGrid()
    {
        var panel = new StackPanel();

        var nav = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        var prev = SmallButton("‹", () => { _anchor = _anchor.AddMonths(-1); Build(); });
        var next = SmallButton("›", () => { _anchor = _anchor.AddMonths(1); Build(); });
        prev.Margin = new Thickness(0); DockPanel.SetDock(prev, Dock.Left); nav.Children.Add(prev);
        DockPanel.SetDock(next, Dock.Right); nav.Children.Add(next);
        panel.Children.Add(nav);

        var header = new UniformGrid { Rows = 1, Columns = 7, Margin = new Thickness(0, 0, 0, 4) };
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

        for (int i = 0; i < lead; i++) grid.Children.Add(new Border { Height = 34 });
        for (int d = 1; d <= days; d++)
        {
            var day = new DateTime(_anchor.Year, _anchor.Month, d);
            var isToday = day == DateTime.Today;
            var isSelected = day == _selected.Date;
            var has = Events.Any(e => e.Start.Date == day);

            var num = new TextBlock { Text = d.ToString(), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12, FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal };
            num.SetResourceReference(TextBlock.ForegroundProperty, isToday ? "FgOnAccent" : "FgSecondary");

            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 4, Height = 4, Margin = new Thickness(0, 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = has ? Visibility.Visible : Visibility.Hidden,
            };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, isToday ? "FgOnAccent" : "Accent");

            var cell = new Border { Height = 34, Margin = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand, Child = Ui.Stack(num, dot) };
            cell.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            if (isToday) cell.SetResourceReference(Border.BackgroundProperty, "Accent");
            else if (isSelected) cell.SetResourceReference(Border.BackgroundProperty, "BgSunken");
            var captured = day;
            cell.MouseLeftButtonUp += (s, _) => { _selected = captured; Build(); OpenDayFlyout((FrameworkElement)s, captured); };
            grid.Children.Add(cell);
        }
        panel.Children.Add(grid);
        panel.Children.Add(Ui.Caption("有日程的日子标一个点;点日期看当日时间线"));
        return panel;
    }

    // ---------------------------------------------------------------- 浮窗:当日时间线
    void OpenDayFlyout(FrameworkElement anchor, DateTime day)
    {
        var body = new StackPanel();
        var evts = Events.Where(e => e.Start.Date == day).OrderBy(e => e.Start).ToList();

        if (evts.Count == 0)
        {
            body.Children.Add(Ui.Body("这一天没有日程。", muted: true));
        }
        else foreach (var ev in evts)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), LastChildFill = true };
            var time = new TextBlock { Text = $"{ev.Start:HH:mm}", Width = 46 };
            time.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            time.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            DockPanel.SetDock(time, Dock.Left);
            row.Children.Add(time);
            row.Children.Add(Ui.Body(ev.Title));
            body.Children.Add(row);
        }

        var edit = Ui.Secondary("编辑这一天", (_, _) => { Flyout.CloseAll(); OpenEditFlyout(day); });
        edit.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(edit);

        Flyout.Show(anchor, day.ToString("M月 d日 dddd", Zh), body, width: 300);
    }

    // ---------------------------------------------------------------- 浮窗:编辑选中日期的日程
    void OpenEditFlyout(DateTime day)
    {
        var body = CalendarEditor.Build(day, onSaved: () => { Flyout.CloseAll(); Build(); });
        Flyout.Show(this, day.ToString("M月 d日", Zh) + " · 编辑日程", body, width: 340);
    }
}
