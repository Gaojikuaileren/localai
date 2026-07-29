// P3c -- 日程编辑器的**内容**(装进浮窗,不是独立窗口 —— 用户裁定)。
// 编辑的始终是【当前选中日期】的日程;AI 与手动编辑写同一模型 CalendarPanel.Events。
//
// ★ 数据源未接入 Apple 家庭共享日历 -> 保存时**如实拒绝**并说明原因,
//   不伪造日程、不伪造同步成功(设计 §4.5 / 状态矩阵 §8)。

using System.Windows;
using System.Windows.Controls;
using LocalAI.Client.I18n;

namespace LocalAI.Client.Views;

public static class CalendarEditor
{
    public static UIElement Build(DateTime day, Action onSaved)
    {
        var title = new TextBox { Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(8, 5, 8, 5) };
        var start = new TextBox { Text = "09:00", Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(8, 5, 8, 5) };
        var end = new TextBox { Text = "10:00", Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(8, 5, 8, 5) };

        // 归属:双成员家庭(D45)。改与对方相关的日程只能发邀请/建议。
        var owner = new ComboBox { Margin = new Thickness(0, 4, 0, 10) };
        foreach (var o in new[] { "我", "对方", "双方" }) owner.Items.Add(o);
        owner.SelectedIndex = 0;

        // 可见范围(设计 §2 三层,与敏感度是两条独立轴)
        var scope = new ComboBox { Margin = new Thickness(0, 4, 0, 10) };
        scope.Items.Add(Strings.Get("visibility.family"));
        scope.Items.Add(Strings.Get("visibility.personal"));
        scope.Items.Add(Strings.Get("visibility.only_me"));
        scope.SelectedIndex = 0;

        var status = Ui.Caption("");
        status.Margin = new Thickness(0, 8, 0, 0);

        var save = Ui.Primary("保存", (_, _) =>
        {
            status.Text = "日历尚未连接,暂时无法保存。接入 Apple 家庭共享日历后,这里保存的日程会自动同步。";
            status.SetResourceReference(TextBlock.ForegroundProperty, "RiskWarning");
        });

        var times = new Grid();
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sw = Ui.Stack(Ui.Caption("开始"), start);
        var ew = Ui.Stack(Ui.Caption("结束"), end);
        Grid.SetColumn(sw, 0); Grid.SetColumn(ew, 2);
        times.Children.Add(sw); times.Children.Add(ew);

        return Ui.Stack(
            Ui.Caption("标题"), title,
            times,
            Ui.Caption("归属成员"), owner,
            Ui.Caption("可见范围"), scope,
            save,
            status,
            Ui.Caption("接入后:改与对方相关的日程只能发【邀请 / 修改建议】,由对方接受。AI 通过同一入口编辑,遵守同样的可见范围规则。")
        );
    }
}
