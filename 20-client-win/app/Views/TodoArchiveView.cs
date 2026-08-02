// P3c -- 「已完成」抽屉。待办板块右下角的入口点开它,列出所有已完成事项。
//   · 点某条左侧的实心圆圈 = 取消完成,该条立刻【回到待办板块】(用户裁定:随时可加回);
//   · 点整行 = 打开它的编辑抽屉;
//   · 顶部两件事(用户裁定,2026-07-30 收敛):
//       ① 【保留期】:自动删除超过 X 天的已完成(0 = 不自动删,默认);
//       ② 【立即删除全部已完成】一个按钮 —— 不再做"选择性批量删除"。
//
// ★ 保留期用【分段小按钮】而不是下拉框:下拉框的弹出层会盖住下方内容,
//   选项一点常常"穿透"点到背后的按钮(用户反馈)。分段按钮没有弹出层,structurally 不会穿透。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class TodoArchiveView : UserControl
{
    static App TheApp => (App)Application.Current;
    readonly StackPanel _list = new();

    public TodoArchiveView()
    {
        Content = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough();

        Build();
        Loaded += (_, _) => TheApp.Todos.Changed += Build;
        Unloaded += (_, _) => TheApp.Todos.Changed -= Build;
    }

    void Build()
    {
        _list.Children.Clear();
        var items = TheApp.Todos.Completed().ToList();

        // ★★ 如实说清待办的性质(D57):它是【纯本机】数据 —— 不与任何服务同步,
        //   也【不会自愈】。日历丢了能从 iCloud 再拉一次,待办不能。
        //   这句话必须摆在用户看得见的地方:不说,换电脑那天才发现就太晚了。
        _list.Children.Add(Ui.Caption(
            "待办与家务只存在这台电脑上 —— 不与 iPhone 的提醒事项或任何云服务同步。"
            + "换电脑、重装系统或硬盘损坏都不会自动带走它们。"));
        _list.Children.Add(new Border { Height = 8 });

        // ① 保留期(分段按钮,无弹出层)
        _list.Children.Add(RetentionRow());

        // ② 立即删除全部已完成
        if (items.Count > 0)
        {
            var clear = Ui.DangerFilled($"立即删除全部已完成 ({items.Count})", (_, _) =>
            {
                if (!ConfirmDialog.Show("删除全部已完成",
                        $"删除全部 {items.Count} 条已完成事项?此操作不可撤销。",
                        confirmText: "全部删除", danger: true)) return;
                TheApp.Todos.ClearCompleted();
            });
            clear.HorizontalAlignment = HorizontalAlignment.Left;
            clear.Margin = new Thickness(0, 10, 0, 6);
            _list.Children.Add(clear);
        }

        _list.Children.Add(new Border { Height = 4 });

        if (items.Count == 0)
        {
            _list.Children.Add(Ui.Body("还没有已完成事项。", muted: true));
            _list.Children.Add(Ui.Caption("在待办事项里点圆圈勾选完成,停留 3 秒后会自动归档到这里。"));
            return;
        }

        foreach (var t in items)
            _list.Children.Add(TodoList.Row(t,
                () => TheApp.Todos.Toggle(t.Id),      // 取消完成 -> 回到待办
                () => OpenEditor(t)));
    }

    // 保留期:分段小按钮。选中的高亮;点一下即写设置并立刻按新值清一次。
    FrameworkElement RetentionRow()
    {
        var s = TheApp.Settings;
        var options = new (string Label, int Days)[]
        {
            ("不自动删除", 0), ("7 天", 7), ("30 天", 30), ("90 天", 90),
        };

        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, days) in options)
        {
            var on = s.TodoAutoPurgeDays == days;
            var d = days;
            seg.Children.Add(SegChip(label, on, () =>
            {
                if (s.TodoAutoPurgeDays == d) return;
                s.TodoAutoPurgeDays = d;
                s.Save();
                TheApp.Todos.PurgeCompletedOlderThan(d);   // 立刻生效一次
                Build();                                    // 重画:高亮换到新选项 + 列表可能变少
            }));
        }

        return Ui.Panel("自动清理",
            Ui.Stack(Ui.Caption("自动删除完成时间超过所选天数的事项(启动时与改动时各清一次)。"), seg),
            IconName.Clock, new Thickness(0, 0, 0, 4), compact: true);
    }

    // 分段按钮的一个格:选中 = 强调底 + 反色字;未选 = 描边。
    static FrameworkElement SegChip(string text, bool selected, Action onClick)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, selected ? "FgOnAccent" : "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border
        {
            Child = t, Padding = new Thickness(11, 5, 11, 5), Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand, BorderThickness = new Thickness(1),
        };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.SetResourceReference(Border.BackgroundProperty, selected ? "Accent" : "BgSurface");
        b.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "Border");
        if (!selected)
        {
            b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
            b.MouseLeave += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        }
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    static void OpenEditor(TodoItem t)
        => (Application.Current.MainWindow as MainWindow)?.OpenSideDrawer("编辑待办事项", TodoEditor.Build(t), IconName.Member);
}
