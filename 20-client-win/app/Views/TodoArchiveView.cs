// P3c -- 「已完成」抽屉。待办板块右下角的入口点开它,列出所有已完成事项。
//   · 点某条左侧的实心圆圈 = 取消完成,该条立刻【回到待办板块】(用户裁定:随时可加回);
//   · 点整行 = 打开它的编辑抽屉;
//   · 【批量删除】:勾选多条一次删,或"全选/清空已完成"(用户裁定);
//   · 【自动清理】:可设"自动删除超过 X 天的已完成",0 = 不自动删(默认,保守:不替用户丢东西)。
//
// 自身订阅 TodoCenter.Changed,取消完成后本抽屉与主板块都会即时刷新。

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
    readonly HashSet<string> _selected = new();   // 批量删除的勾选集
    bool _selecting;                               // 是否处于多选模式

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

        // 顶部:自动清理设置(不管有没有条目都显示 —— 它是个偏好)
        _list.Children.Add(AutoPurgeRow());
        _list.Children.Add(new Border { Height = 8 });

        if (items.Count == 0)
        {
            _list.Children.Add(Ui.Body("还没有已完成事项。", muted: true));
            _list.Children.Add(Ui.Caption("在待办事项里点圆圈勾选完成,停留 3 秒后会自动归档到这里。"));
            return;
        }

        // 工具条:多选 / 全选 / 删除所选 / 清空
        _list.Children.Add(Toolbar(items));
        _list.Children.Add(new Border { Height = 4 });

        foreach (var t in items) _list.Children.Add(Row(t));
    }

    FrameworkElement Row(TodoItem t)
    {
        var row = TodoList.Row(t,
            () => TheApp.Todos.Toggle(t.Id),                    // 取消完成 -> 回到待办
            () => OpenEditor(t));
        if (!_selecting) return row;

        // 多选模式:行首加勾选框(不影响正常模式的排布)
        var cb = new CheckBox { IsChecked = _selected.Contains(t.Id), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
        cb.Checked += (_, _) => { _selected.Add(t.Id); RefreshToolbarOnly(); };
        cb.Unchecked += (_, _) => { _selected.Remove(t.Id); RefreshToolbarOnly(); };
        var d = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(cb, Dock.Left);
        d.Children.Add(cb);
        d.Children.Add(row);
        return d;
    }

    // 勾选变化只需要刷新工具条的计数,不必整表重建(重建会丢掉勾选焦点)
    void RefreshToolbarOnly() => Build();

    FrameworkElement Toolbar(List<TodoItem> items)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal };

        if (!_selecting)
        {
            bar.Children.Add(Chip("批量删除", "FgSecondary", () => { _selecting = true; _selected.Clear(); Build(); }));
            bar.Children.Add(Chip($"清空已完成 ({items.Count})", "RiskDanger", () =>
            {
                if (!ConfirmDialog.Show("清空已完成",
                        $"删除全部 {items.Count} 条已完成事项?此操作不可撤销。",
                        confirmText: "全部删除", danger: true)) return;
                TheApp.Todos.ClearCompleted();
            }));
            return bar;
        }

        var all = items.Count > 0 && _selected.Count == items.Count;
        bar.Children.Add(Chip(all ? "取消全选" : "全选", "FgSecondary", () =>
        {
            _selected.Clear();
            if (!all) foreach (var t in items) _selected.Add(t.Id);
            Build();
        }));
        bar.Children.Add(Chip($"删除所选 ({_selected.Count})", _selected.Count > 0 ? "RiskDanger" : "FgMuted", () =>
        {
            if (_selected.Count == 0) return;
            if (!ConfirmDialog.Show("删除所选",
                    $"删除选中的 {_selected.Count} 条已完成事项?此操作不可撤销。",
                    confirmText: "删除", danger: true)) return;
            TheApp.Todos.RemoveMany(_selected);
            _selected.Clear();
            _selecting = false;
        }));
        bar.Children.Add(Chip("退出多选", "FgMuted", () => { _selecting = false; _selected.Clear(); Build(); }));
        return bar;
    }

    // 自动清理:0 = 不自动删(默认)。改完立刻按新设置清一次,让用户马上看到效果。
    FrameworkElement AutoPurgeRow()
    {
        var s = TheApp.Settings;
        var options = new (string Label, int Days)[]
        {
            ("不自动删除", 0), ("7 天后", 7), ("30 天后", 30), ("90 天后", 90),
        };
        var combo = new ComboBox { Width = 130, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (label, _) in options) combo.Items.Add(label);
        var idx = Array.FindIndex(options, o => o.Days == s.TodoAutoPurgeDays);
        combo.SelectedIndex = idx < 0 ? 0 : idx;
        combo.SelectionChanged += (_, _) =>
        {
            var days = options[Math.Max(0, combo.SelectedIndex)].Days;
            if (days == s.TodoAutoPurgeDays) return;
            s.TodoAutoPurgeDays = days;
            s.Save();
            TheApp.Todos.PurgeCompletedOlderThan(days);   // 改完立刻生效一次
        };

        return Ui.Panel("自动清理",
            Ui.Stack(Ui.Caption("自动删除完成时间超过所选天数的事项(启动时与改动时各清一次)。"), combo),
            IconName.Clock, new Thickness(0, 0, 0, 4), compact: true);
    }

    static FrameworkElement Chip(string text, string colorKey, Action onClick)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border { Child = t, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    static void OpenEditor(TodoItem t)
        => (Application.Current.MainWindow as MainWindow)?.OpenSideDrawer("编辑待办事项", TodoEditor.Build(t), IconName.Member);
}
