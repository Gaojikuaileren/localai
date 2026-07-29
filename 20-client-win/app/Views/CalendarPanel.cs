// P3c -- 日历面板。两处复用:主页右上角(compact)与全局抽屉(完整)。
//
// ★ 设计 §4.5 / 状态矩阵 §8 的硬性约束:日历【只做占位】,禁止实现本地日历或伪造同步。
//   占位必须明写「Apple 家庭共享日历:计划接入,目前未连接」。
//   本月网格是**真实日期**(本地时钟算得出,不算伪造);日程一栏明确写未连接,不显示任何假日程。

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LocalAI.Client.I18n;

namespace LocalAI.Client.Views;

public sealed class CalendarPanel : UserControl
{
    public CalendarPanel(bool compact)
    {
        var today = DateTime.Today;

        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
        var month = new TextBlock { Text = today.ToString("yyyy 年 M 月", new CultureInfo("zh-CN")), FontWeight = FontWeights.SemiBold };
        month.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        month.SetResourceReference(TextBlock.FontSizeProperty, compact ? "FontBody" : "FontSubtitle");
        DockPanel.SetDock(month, Dock.Left);
        head.Children.Add(month);

        var content = Ui.Stack(head, MonthGrid(today, compact));

        // 未连接提示 —— 固定文案,任何情况下都不得显示假日程或假同步成功
        var notice = Ui.Caption(Strings.Get("calendar.not_connected"));
        notice.Margin = new Thickness(0, 10, 0, 0);
        content.Children.Add(notice);

        if (!compact)
        {
            content.Children.Add(new Border { Height = 14 });
            content.Children.Add(Ui.Subtitle("今天的日程"));
            content.Children.Add(Ui.Body("尚未连接日历,这里暂时没有内容。", muted: true));
            content.Children.Add(Ui.Caption("接入后将读取家庭安全管理员 Apple 账户里的一份家庭共享日历,用颜色/成员标记区分双方;" +
                                            "改与对方相关的日程只能发邀请或修改建议。"));
        }

        Content = content;
    }

    static UIElement MonthGrid(DateTime today, bool compact)
    {
        var cell = compact ? 26.0 : 34.0;
        var font = compact ? 10.5 : 12.0;

        var panel = new StackPanel();

        // 周几表头(周一起始,符合中文/德国习惯)
        var header = new UniformGrid { Rows = 1, Columns = 7 };
        foreach (var d in new[] { "一", "二", "三", "四", "五", "六", "日" })
        {
            var t = new TextBlock { Text = d, HorizontalAlignment = HorizontalAlignment.Center, FontSize = font - 0.5 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            header.Children.Add(t);
        }
        panel.Children.Add(header);

        var first = new DateTime(today.Year, today.Month, 1);
        var lead = ((int)first.DayOfWeek + 6) % 7;               // 周一 = 0
        var days = DateTime.DaysInMonth(today.Year, today.Month);
        var grid = new UniformGrid { Columns = 7, Margin = new Thickness(0, 4, 0, 0) };

        for (int i = 0; i < lead; i++) grid.Children.Add(new Border { Height = cell });
        for (int day = 1; day <= days; day++)
        {
            var isToday = day == today.Day;
            var num = new TextBlock
            {
                Text = day.ToString(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = font,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
            };
            num.SetResourceReference(TextBlock.ForegroundProperty, isToday ? "FgOnAccent" : "FgSecondary");

            var b = new Border { Height = cell, Child = num, Margin = new Thickness(1) };
            b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            if (isToday) b.SetResourceReference(Border.BackgroundProperty, "Accent");
            grid.Children.Add(b);
        }
        panel.Children.Add(grid);
        return panel;
    }
}
