// P3c -- 主页顶部的日历条(用户裁定):
//   · 放在【最上面的板块】,与待办并列;日历占宽多,待办占少;
//   · 【高度固定】—— 日期以【周横排】,一行铺开,不做纵向月历;
//   · 点某天 -> 当日时间线浮窗;点编辑 -> 编辑当天日程的浮窗(与右侧抽屉里的日历同一套交互)。
//
// ★ 数据源仍未接入 Apple 家庭共享日历(设计 §4.5 / 状态矩阵 §8):
//   每天一律显示"无日程"标记,绝不伪造日程,也不伪造同步成功。

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LocalAI.Client.I18n;

namespace LocalAI.Client.Views;

public sealed class CalendarStrip : UserControl
{
    static readonly CultureInfo Zh = new("zh-CN");

    /// <summary>固定高度 —— 用户要求日历高度固定,不随内容或窗口拉伸。</summary>
    public const double StripHeight = 92;

    readonly UniformGrid _days = new() { Rows = 1 };
    readonly TextBlock _label = new();
    DateTime _weekStart;
    DateTime _selected = DateTime.Today;
    int _daysShown = 7;

    public CalendarStrip()
    {
        _weekStart = StartOfWeek(DateTime.Today);

        _label.FontWeight = FontWeights.SemiBold;
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(_label, Dock.Left);
        head.Children.Add(_label);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Btn("‹", () => { _weekStart = _weekStart.AddDays(-7); Rebuild(); }));
        actions.Children.Add(Btn("今日", () => { _weekStart = StartOfWeek(DateTime.Today); _selected = DateTime.Today; Rebuild(); }));
        actions.Children.Add(Btn("›", () => { _weekStart = _weekStart.AddDays(7); Rebuild(); }));
        actions.Children.Add(Btn("编辑", () => OpenEditFlyout(_selected)));
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(_days);
        Content = root;

        // 宽度足够时铺满两周,否则一周 —— 但高度始终不变
        SizeChanged += (_, _) =>
        {
            var want = ActualWidth >= 720 ? 14 : 7;
            if (want != _daysShown) { _daysShown = want; Rebuild(); }
        };

        Rebuild();
    }

    static DateTime StartOfWeek(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));   // 周一起始

    static Button Btn(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text, Height = 22, MinWidth = 26, Padding = new Thickness(7, 0, 7, 0),
            Margin = new Thickness(4, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(1), Background = Brushes.Transparent, FontSize = 11,
        };
        b.SetResourceReference(Button.BorderBrushProperty, "Border");
        b.SetResourceReference(Button.ForegroundProperty, "FgSecondary");
        b.Click += (_, _) => onClick();
        return b;
    }

    void Rebuild()
    {
        var last = _weekStart.AddDays(_daysShown - 1);
        _label.Text = _weekStart.Month == last.Month
            ? _weekStart.ToString("yyyy年 M月", Zh)
            : $"{_weekStart:M月} – {last:M月}";

        _days.Columns = _daysShown;
        _days.Children.Clear();

        for (int i = 0; i < _daysShown; i++)
        {
            var day = _weekStart.AddDays(i);
            var isToday = day == DateTime.Today;
            var isSelected = day == _selected.Date;
            var evts = CalendarPanel.Events.Where(e => e.Start.Date == day).OrderBy(e => e.Start).ToList();

            var wk = new TextBlock { Text = day.ToString("ddd", Zh), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 10.5 };
            wk.SetResourceReference(TextBlock.ForegroundProperty, isToday ? "FgOnAccent" : "FgMuted");

            var num = new TextBlock
            {
                Text = day.Day.ToString(),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 16,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                Margin = new Thickness(0, 1, 0, 2),
            };
            num.SetResourceReference(TextBlock.ForegroundProperty, isToday ? "FgOnAccent" : "FgPrimary");

            // 日程指示:有则点,没有则一条极淡的横线(占位等高,避免有无日程时高度跳动)
            UIElement marker;
            if (evts.Count == 0)
            {
                var dash = new Border { Width = 10, Height = 2, Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center };
                dash.SetResourceReference(Border.BackgroundProperty, "Border");
                marker = dash;
            }
            else
            {
                var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                foreach (var _ in evts.Take(3))
                {
                    var d = new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, Margin = new Thickness(1.5, 0, 1.5, 0) };
                    d.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, isToday ? "FgOnAccent" : "Accent");
                    dots.Children.Add(d);
                }
                marker = dots;
            }

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(wk);
            stack.Children.Add(num);
            stack.Children.Add(marker);

            var cell = new Border
            {
                Child = stack,
                Margin = new Thickness(2),
                Padding = new Thickness(0, 5, 0, 5),
                Cursor = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(1),
            };
            cell.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            if (isToday) { cell.SetResourceReference(Border.BackgroundProperty, "Accent"); cell.BorderBrush = Brushes.Transparent; }
            else if (isSelected) { cell.SetResourceReference(Border.BackgroundProperty, "BgSunken"); cell.SetResourceReference(Border.BorderBrushProperty, "BorderStrong"); }
            else { cell.Background = Brushes.Transparent; cell.BorderBrush = Brushes.Transparent; }

            var captured = day;
            cell.MouseLeftButtonUp += (s, _) => { _selected = captured; Rebuild(); OpenDayFlyout((FrameworkElement)s, captured); };
            _days.Children.Add(cell);
        }
    }

    void OpenDayFlyout(FrameworkElement anchor, DateTime day)
    {
        var body = new StackPanel();
        var evts = CalendarPanel.Events.Where(e => e.Start.Date == day).OrderBy(e => e.Start).ToList();

        if (evts.Count == 0) body.Children.Add(Ui.Body("这一天没有日程。", muted: true));
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

        body.Children.Add(Ui.Caption(Strings.Get("calendar.not_connected")));
        var edit = Ui.Secondary("编辑这一天", (_, _) => { Flyout.CloseAll(); OpenEditFlyout(day); });
        edit.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(edit);

        Flyout.Show(anchor, day.ToString("M月 d日 dddd", Zh), body, width: 300);
    }

    void OpenEditFlyout(DateTime day)
        => Flyout.Show(this, day.ToString("M月 d日", Zh) + " · 编辑日程",
                       CalendarEditor.Build(day, onSaved: () => { Flyout.CloseAll(); Rebuild(); }), width: 340);
}
