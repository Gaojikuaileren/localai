// P3c -- 待办 / 家务编辑器(装进右侧全高抽屉,与日程编辑器同一套视觉语言)。
//   existing == null = 新建,否则 = 编辑那一条。
//
// 字段:标题 · 类型(待办/家务) · 截止(可选:日期,再可选具体时间,用竖直转盘) ·
//   旗标 · 优先级 · 归属成员 · 可见范围 · 备注。
//
// ★ 与日历编辑器不同:待办/家务是中枢自有数据,保存/删除【当场生效】(写进 TodoCenter),
//   不是伪造。跨设备同步与落盘随中枢接入启用 —— 末尾如实说明(见 TodoCenter 注释)。

using System.Windows;
using System.Windows.Controls;
using LocalAI.Client.I18n;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public static class TodoEditor
{
    static App TheApp => (App)Application.Current;

    public static UIElement Build(TodoItem? existing)
    {
        var title = Field(existing?.Title ?? "");

        // ---- 类型:待办 / 家务 ----
        var kinds = new[] { "待办", "家务", "采购清单" };
        var kind = new ComboBox { Margin = new Thickness(0, 2, 0, 6) };
        foreach (var k in kinds) kind.Items.Add(k);
        kind.SelectedIndex = existing?.Kind switch { TodoKind.Chore => 1, TodoKind.Shopping => 2, _ => 0 };

        // ---- 截止:可选。先"是否设截止",再"是否含具体时间";都用竖直转盘 ----
        var hasDue = new CheckBox { Content = "设置截止", IsChecked = existing?.Due is not null, Margin = new Thickness(0, 2, 0, 6) };
        var hasTime = new CheckBox { Content = "包含具体时间", IsChecked = existing?.DueHasTime == true, Margin = new Thickness(0, 2, 0, 6) };

        var dueDay = existing?.Due?.Date ?? DateTime.Today;
        var dueTime = existing?.DueHasTime == true
            ? WheelPicker.Snap(existing!.Due!.Value.TimeOfDay)
            : WheelPicker.CeilToStep(DateTime.Now.TimeOfDay);

        var dateRow = Ui.Stack(Ui.Caption("截止日期"), WheelPicker.Date(dueDay, d => dueDay = d));
        var timeRow = Ui.Stack(Ui.Caption("时间"), WheelPicker.Time(dueTime, v => dueTime = v).Element);

        void SyncDue()
        {
            var on = hasDue.IsChecked == true;
            dateRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            hasTime.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            timeRow.Visibility = on && hasTime.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        hasDue.Checked += (_, _) => SyncDue();
        hasDue.Unchecked += (_, _) => SyncDue();
        hasTime.Checked += (_, _) => SyncDue();
        hasTime.Unchecked += (_, _) => SyncDue();
        SyncDue();

        // ---- 旗标 + 优先级 ----
        var flag = new CheckBox { Content = "旗标(重点标记)", IsChecked = existing?.Flagged == true, Margin = new Thickness(0, 2, 0, 6) };

        var prios = new[] { "无", "低", "中", "高" };
        var priority = new ComboBox { Margin = new Thickness(0, 2, 0, 6) };
        foreach (var p in prios) priority.Items.Add(p);
        priority.SelectedIndex = (int)(existing?.Priority ?? TodoPriority.None);

        // ---- 归属与可见范围(D45)----
        var owners = new[] { "我", "对方", "双方" };
        var owner = new ComboBox { Margin = new Thickness(0, 2, 0, 6) };
        foreach (var o in owners) owner.Items.Add(o);
        owner.SelectedIndex = Math.Max(0, Array.IndexOf(owners, existing?.Owner ?? "我"));

        // ★ 同日程:"个人"与"仅本人"对待办来说是同一件事,不并排列两条(用户反馈:看着像重复)。
        var scopes = new[] { Strings.Get("visibility.family"), Strings.Get("visibility.personal") };
        var scope = new ComboBox { Margin = new Thickness(0, 2, 0, 6) };
        foreach (var sc in scopes) scope.Items.Add(sc);
        scope.SelectedIndex = Math.Max(0, Array.IndexOf(scopes, existing?.Scope ?? scopes[0]));

        var notes = Field(existing?.Notes ?? "");
        notes.AcceptsReturn = true;
        notes.TextWrapping = TextWrapping.Wrap;
        notes.MinHeight = 40;
        notes.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        var status = Ui.Caption("");
        status.Margin = new Thickness(0, 8, 0, 0);

        // ---- 保存 / 删除:当场写进 TodoCenter ----
        TodoItem Collect()
        {
            DateTime? due = null;
            var dueHasTime = false;
            if (hasDue.IsChecked == true)
            {
                if (hasTime.IsChecked == true) { due = dueDay.Date + dueTime; dueHasTime = true; }
                else due = dueDay.Date;
            }
            return new TodoItem(
                Id: existing?.Id ?? "",
                Title: string.IsNullOrWhiteSpace(title.Text) ? "(无标题)" : title.Text.Trim(),
                Kind: kind.SelectedIndex switch { 1 => TodoKind.Chore, 2 => TodoKind.Shopping, _ => TodoKind.Personal },
                Done: existing?.Done ?? false,
                Due: due,
                DueHasTime: dueHasTime,
                Flagged: flag.IsChecked == true,
                Priority: (TodoPriority)Math.Max(0, priority.SelectedIndex),
                Notes: string.IsNullOrWhiteSpace(notes.Text) ? null : notes.Text,
                Owner: owners[Math.Max(0, owner.SelectedIndex)],
                Scope: scopes[Math.Max(0, scope.SelectedIndex)],
                CompletedAt: existing?.CompletedAt,   // 保留完成时间戳,编辑不影响归档
                CreatedByAi: existing?.CreatedByAi ?? false);   // 保留"AI 建立"标记(编辑不抹掉出处)
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(Ui.Primary(existing is null ? "添加" : "保存", (_, _) =>
        {
            var item = Collect();
            if (existing is null) TheApp.Todos.Add(item);
            else TheApp.Todos.Update(item);
            Overlay.CloseActive();   // 存好就收起抽屉;列表由 TodoCenter.Changed 自动刷新
        }));
        if (existing is not null)
        {
            var del = Ui.Danger("删除", (_, _) => { TheApp.Todos.Remove(existing.Id); Overlay.CloseActive(); });
            del.Margin = new Thickness(10, 0, 0, 0);
            buttons.Children.Add(del);
        }

        var form = Ui.Stack(
            Ui.Panel("基本信息",
                Ui.Stack(Ui.Caption("标题"), title, Ui.Caption("类型"), kind),
                Theme.IconName.Member, new Thickness(0, 0, 0, 8), compact: true),

            Ui.Panel("提醒",
                Ui.Stack(hasDue, dateRow, hasTime, timeRow, flag),
                Theme.IconName.Clock, new Thickness(0, 0, 0, 8), compact: true),

            Ui.Panel("更多",
                Ui.Stack(Ui.Caption("优先级"), priority,
                         Ui.Caption("归属成员"), owner,
                         Ui.Caption("可见范围"), scope,
                         // ★★★ 2026-08-05 实测反馈:「我这边添加的共享家庭待办也无法在对方应用显示」。
                         //   ——「家庭」这个词是在**选范围的这一刻**给用户的,它读起来就是
                         //   "两台机器都看得见";而 D57 裁定待办是【纯本机】数据,一个字节都不同步。
                         //   ★ 那句「只存在这台电脑上」原来只在【归档页】说 —— 用户很少去的地方。
                         //     说明必须摆在**期望形成的地方**,不是摆在某个正确但没人看的角落。
                         //   ★ 这里不改「家庭」这个词本身:它是**归属**(谁的事),那个语义是对的;
                         //     错的是让人误以为它同时意味着"同步"。所以是**补一句**,不是改词。
                         Ui.Caption("★「家庭」是说这件事归谁 —— 不是同步范围。"
                                    + "待办只存在这台电脑上,别的设备看不到(D57:待办不与任何服务同步)。"),
                         Ui.Caption("备注"), notes),
                Theme.IconName.Tasks, new Thickness(0, 0, 0, 8), compact: true),

            buttons,
            status,
            Ui.Caption("新增/修改当场生效并保存在本机。★ 待办只存在这台电脑上,不与任何服务同步(见归档页说明)。")
        );

        return new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough();
    }

    static TextBox Field(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 2, 0, 6),
        Padding = new Thickness(8, 4, 8, 4),
    };
}
