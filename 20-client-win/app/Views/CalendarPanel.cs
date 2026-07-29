// P3c -- 日历。用户裁定的形态:
//   · 默认看【当前周 + 下一周】的**每日详细日程**(主页那个尺寸放得下);
//   · 可切换到【整月】视图 —— 整月只在有日程的日子标一个点,尺寸保持不变;
//   · 可【手动编辑】,编辑走独立的日历编辑窗口;编辑完自动 push(未来);
//   · AI 也能编辑(同一份数据源,走同样的写入口)。
//
// ★ 设计 §4.5 / 状态矩阵 §8 的硬约束仍然成立:日历数据源【尚未接入】Apple 家庭共享日历,
//   所以本轮**没有任何日程数据** —— 每日一律显示"无日程",绝不伪造日程或伪造同步成功。
//   编辑窗口可以打开、可以填,但保存时明确告知"未连接,暂不能保存",不做假成功。

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LocalAI.Client.I18n;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

/// <summary>一条日程。数据源接入前恒为空集合;结构先立住,AI 与手动编辑写同一个模型。</summary>
public sealed record CalendarEvent(DateTime Start, DateTime End, string Title, string Owner, string Scope);

public sealed class CalendarPanel : UserControl
{
    public enum Mode { TwoWeeks, Month }

    static readonly CultureInfo Zh = new("zh-CN");

    Mode _mode;
    readonly bool _expanded;
    readonly StackPanel _root = new();
    readonly ContentControl _body = new();

    /// <summary>日程数据源。未接入 -> 空。AI 与编辑窗口将来往这里写。</summary>
    public static List<CalendarEvent> Events { get; } = new();

    public CalendarPanel(Mode mode = Mode.TwoWeeks, bool expanded = false)
    {
        _mode = mode;
        _expanded = expanded;
        Content = _root;
        Build();
    }

    void Build()
    {
        _root.Children.Clear();

        // 头:月份 + 视图切换 + 编辑
        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };

        var month = new TextBlock
        {
            Text = DateTime.Today.ToString("M月 d日 dddd", Zh),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        month.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        DockPanel.SetDock(month, Dock.Left);
        head.Children.Add(month);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(ToggleButton(_mode == Mode.TwoWeeks ? "整月" : "两周", () =>
        {
            _mode = _mode == Mode.TwoWeeks ? Mode.Month : Mode.TwoWeeks;
            Build();
        }));
        actions.Children.Add(ToggleButton("编辑", () => CalendarEditWindow.Show(Window.GetWindow(this))));
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);

        _root.Children.Add(head);

        _body.Content = _mode == Mode.TwoWeeks ? TwoWeeksView() : MonthView();
        _root.Children.Add(_body);

        var notice = Ui.Caption(Strings.Get("calendar.not_connected"));
        notice.Margin = new Thickness(0, 10, 0, 0);
        _root.Children.Add(notice);
    }

    static Button ToggleButton(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Height = 24,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            FontSize = 11.5,
        };
        b.SetResourceReference(Button.BorderBrushProperty, "Border");
        b.SetResourceReference(Button.ForegroundProperty, "FgSecondary");
        b.Click += (_, _) => onClick();
        return b;
    }

    // ---------------------------------------------------------------- 两周:每日详细日程
    UIElement TwoWeeksView()
    {
        var today = DateTime.Today;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));   // 本周一

        var list = new StackPanel();
        for (int i = 0; i < 14; i++)
        {
            var day = monday.AddDays(i);
            var isToday = day == today;
            var isNextWeek = i >= 7;

            // 周分隔
            if (i == 0 || i == 7)
            {
                var lbl = new TextBlock
                {
                    Text = i == 0 ? "本周" : "下周",
                    Margin = new Thickness(0, i == 0 ? 0 : 10, 0, 4),
                    FontWeight = FontWeights.SemiBold,
                };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                lbl.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
                list.Children.Add(lbl);
            }

            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1, 0, 1) };

            // 日期块(今天高亮)
            var dateBox = new Border { Width = 42, Padding = new Thickness(0, 3, 0, 3), Margin = new Thickness(0, 0, 10, 0) };
            dateBox.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            if (isToday) dateBox.SetResourceReference(Border.BackgroundProperty, "Accent");
            var dnum = new TextBlock
            {
                Text = day.ToString("d日", Zh),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                FontSize = 12,
            };
            dnum.SetResourceReference(TextBlock.ForegroundProperty, isToday ? "FgOnAccent" : "FgPrimary");
            var dwk = new TextBlock { Text = day.ToString("ddd", Zh), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 10 };
            dwk.SetResourceReference(TextBlock.ForegroundProperty, isToday ? "FgOnAccent" : "FgMuted");
            dateBox.Child = Ui.Stack(dnum, dwk);
            DockPanel.SetDock(dateBox, Dock.Left);
            row.Children.Add(dateBox);

            // 当日日程(未接入 -> 恒为空,如实写"无日程",不编造)
            var evts = Events.Where(e => e.Start.Date == day).OrderBy(e => e.Start).ToList();
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (evts.Count == 0)
            {
                var none = new TextBlock { Text = "无日程" };
                none.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                none.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
                col.Children.Add(none);
            }
            else foreach (var ev in evts)
            {
                var line = new TextBlock { Text = $"{ev.Start:HH:mm}  {ev.Title}", TextTrimming = TextTrimming.CharacterEllipsis };
                line.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                line.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
                col.Children.Add(line);
            }
            row.Children.Add(col);
            list.Children.Add(row);

            if (i < 13)
            {
                var sep = new Border { Height = 1, Margin = new Thickness(52, 2, 0, 2), Opacity = 0.6 };
                sep.SetResourceReference(Border.BackgroundProperty, "Border");
                list.Children.Add(sep);
            }
        }

        return new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = _expanded ? ScrollBarVisibility.Auto : ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    // ---------------------------------------------------------------- 整月:只用点标记有日程的日子
    UIElement MonthView()
    {
        var today = DateTime.Today;
        var panel = new StackPanel();

        var header = new UniformGrid { Rows = 1, Columns = 7, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var d in new[] { "一", "二", "三", "四", "五", "六", "日" })
        {
            var t = new TextBlock { Text = d, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 10.5 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            header.Children.Add(t);
        }
        panel.Children.Add(header);

        var first = new DateTime(today.Year, today.Month, 1);
        var lead = ((int)first.DayOfWeek + 6) % 7;
        var days = DateTime.DaysInMonth(today.Year, today.Month);
        var grid = new UniformGrid { Columns = 7 };

        for (int i = 0; i < lead; i++) grid.Children.Add(new Border { Height = 34 });
        for (int d = 1; d <= days; d++)
        {
            var day = new DateTime(today.Year, today.Month, d);
            var isToday = d == today.Day;
            var has = Events.Any(e => e.Start.Date == day);

            var num = new TextBlock
            {
                Text = d.ToString(),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 12,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
            };
            num.SetResourceReference(TextBlock.ForegroundProperty, isToday ? "FgOnAccent" : "FgSecondary");

            // 有日程 = 一个点(整月视图不展开内容,保持尺寸)
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 4, Height = 4, Margin = new Thickness(0, 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = has ? Visibility.Visible : Visibility.Hidden,
            };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, isToday ? "FgOnAccent" : "Accent");

            var cellStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            cellStack.Children.Add(num);
            cellStack.Children.Add(dot);

            var b = new Border { Height = 34, Child = cellStack, Margin = new Thickness(1) };
            b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            if (isToday) b.SetResourceReference(Border.BackgroundProperty, "Accent");
            grid.Children.Add(b);
        }
        panel.Children.Add(grid);

        var legend = Ui.Caption("· 有日程的日子标一个点;点「两周」看每日详细日程");
        legend.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(legend);
        return panel;
    }
}
