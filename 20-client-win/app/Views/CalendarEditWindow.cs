// P3c -- 日历编辑窗口。用户裁定:编辑日历要【进入独立的编辑窗口】,不在小面板里就地改;
// 编辑好了自动 push(未来);AI 也能编辑(与手动编辑写同一个数据模型 CalendarPanel.Events)。
//
// ★ 现在数据源未接入 Apple 家庭共享日历,所以这里可以填、但**保存时如实拒绝**并说明原因 ——
//   设计 §4.5 / 状态矩阵 §8 明令:不得显示伪造日程,也不得伪造"同步成功"。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.I18n;

namespace LocalAI.Client.Views;

public sealed class CalendarEditWindow : Window
{
    public static void Show(Window? owner)
    {
        var w = new CalendarEditWindow { Owner = owner };
        w.ShowDialog();
    }

    CalendarEditWindow()
    {
        Title = "编辑日程";
        Width = 520; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        SetResourceReference(BackgroundProperty, "BgWindow");
        SetResourceReference(ForegroundProperty, "FgPrimary");
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI, Yu Gothic UI, Meiryo, sans-serif");

        var title = new TextBox { Margin = new Thickness(0, 4, 0, 12), Padding = new Thickness(8, 6, 8, 6) };
        var date = new DatePicker { SelectedDate = DateTime.Today, Margin = new Thickness(0, 4, 0, 12) };
        var start = new TextBox { Text = "09:00", Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 12), Padding = new Thickness(8, 6, 8, 6) };
        var end = new TextBox { Text = "10:00", Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 12), Padding = new Thickness(8, 6, 8, 6) };

        // 归属:双成员家庭(D45)。改与对方相关的日程只能发邀请/建议,不能直接改。
        var owner = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 12) };
        foreach (var o in new[] { "我", "对方", "双方" }) owner.Items.Add(o);
        owner.SelectedIndex = 0;

        // 可见范围(设计 §2 三层,与敏感度是两条独立轴)
        var scope = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 12) };
        scope.Items.Add(Strings.Get("visibility.family"));
        scope.Items.Add(Strings.Get("visibility.personal"));
        scope.Items.Add(Strings.Get("visibility.only_me"));
        scope.SelectedIndex = 0;

        var status = Ui.Body("");
        status.Margin = new Thickness(0, 8, 0, 0);

        var save = Ui.Primary("保存", (_, _) =>
        {
            // 如实拒绝 —— 不假装保存成功,也不写进本地假装同步
            status.Text = "日历尚未连接,暂时无法保存。接入 Apple 家庭共享日历后,这里保存的日程会自动同步。";
            status.SetResourceReference(TextBlock.ForegroundProperty, "RiskWarning");
        });
        var cancel = Ui.Secondary(Strings.Get("common.cancel"), (_, _) => Close());
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(save);
        var gap = new Border { Width = 10 };
        buttons.Children.Add(gap);
        buttons.Children.Add(cancel);

        var form = Ui.Stack(
            Ui.Title("编辑日程"),
            Ui.Body("标题"), title,
            Ui.Body("日期"), date,
            Ui.Body("开始"), start,
            Ui.Body("结束"), end,
            Ui.Body("归属成员"), owner,
            Ui.Body("可见范围"), scope,
            buttons,
            status,
            Ui.Caption("接入后:改与对方相关的日程只能发【邀请 / 修改建议】,由对方接受;普通家庭事项双方均可确认。" +
                       "AI 也通过同一个入口编辑,遵守同样的可见范围规则。")
        );
        form.Margin = new Thickness(24);

        Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
