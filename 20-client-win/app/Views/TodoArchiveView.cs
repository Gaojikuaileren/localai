// P3c -- 「已完成」抽屉。待办板块右下角的入口点开它,列出所有已完成事项。
//   点某条左侧的实心圆圈 = 取消完成,该条立刻【回到待办板块】(用户裁定:随时可加回)。
//   点整行 = 打开它的编辑抽屉。
//
// 自身订阅 TodoCenter.Changed,取消完成后本抽屉与主板块都会即时刷新。

using System.Windows;
using System.Windows.Controls;
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
        if (items.Count == 0)
        {
            _list.Children.Add(Ui.Body("还没有已完成事项。", muted: true));
            _list.Children.Add(Ui.Caption("在待办事项里点圆圈勾选完成,停留 3 秒后会自动归档到这里。"));
            return;
        }
        _list.Children.Add(Ui.Caption("点左侧圆圈可取消完成,该项会回到待办事项。"));
        foreach (var t in items)
            _list.Children.Add(TodoList.Row(t,
                () => TheApp.Todos.Toggle(t.Id),                    // 取消完成 -> 回到待办
                () => OpenEditor(t)));
    }

    static void OpenEditor(TodoItem t)
        => (Application.Current.MainWindow as MainWindow)?.OpenSideDrawer("编辑待办事项", TodoEditor.Build(t), IconName.Member);
}
