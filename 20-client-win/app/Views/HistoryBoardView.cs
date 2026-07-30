// P3c -- 「全部历史」抽屉:翻译历史的完整列表,每条后面一个星标可收藏。
//
// ★ 历史不另存原文,它是会话消息的视图(见 Services/TranslationHistory)。
//   所以这里删不掉历史 —— 要删就去删那条会话,免得出现"历史还在、原文没了"的两份真相。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class HistoryBoardView : UserControl
{
    static App TheApp => (App)Application.Current;

    readonly StackPanel _list = new();
    bool _favoritesOnly;

    public HistoryBoardView(bool favoritesOnly = false)
    {
        _favoritesOnly = favoritesOnly;
        var filter = Ui.Stack();
        Content = Ui.Page(Build());
        Loaded += (_, _) => TheApp.History.Changed += Refresh;
        Unloaded += (_, _) => TheApp.History.Changed -= Refresh;
    }

    FrameworkElement Build()
    {
        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
        var toggle = Ui.Secondary(_favoritesOnly ? "显示全部" : "只看收藏", (_, _) =>
        {
            _favoritesOnly = !_favoritesOnly;
            Refresh();
            if (Content is FrameworkElement) Content = Ui.Page(Build());
        });
        DockPanel.SetDock(toggle, Dock.Right);
        head.Children.Add(toggle);

        var box = new StackPanel();
        box.Children.Add(head);
        box.Children.Add(Ui.Caption("点一条跳回它在会话里的位置;星标收藏。★ 历史就是会话本身 —— 这里不提供删除,要删请删那条会话。"));
        box.Children.Add(_list);
        Refresh();
        return box;
    }

    void Refresh()
    {
        _list.Children.Clear();
        var items = TheApp.History.Latest(500, _favoritesOnly);
        if (items.Count == 0)
        {
            _list.Children.Add(Ui.Caption(_favoritesOnly ? "还没有收藏。" : "还没有翻译记录。"));
            return;
        }
        foreach (var e in items) _list.Children.Add(Row(e));
    }

    FrameworkElement Row(HistoryEntry e) => HistoryRow(e, showTime: true);

    /// <summary>
    /// 一条历史。点正文跳回原位;点星标收藏。
    /// 抽成静态是为了让下半条的预览板块与这里画的是同一个 —— 观感不会各走各的。
    /// </summary>
    public static FrameworkElement HistoryRow(HistoryEntry e, bool showTime)
    {
        var text = new TextBlock
        {
            Text = e.Text.Replace(Environment.NewLine, " ").Replace("\n", " "),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        text.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var star = StarButton(e);

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(star, Dock.Right);
        row.Children.Add(star);

        if (showTime)
        {
            var when = Ui.Caption(e.At.ToString("M月d日 HH:mm"));
            when.VerticalAlignment = VerticalAlignment.Center;
            when.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(when, Dock.Right);
            row.Children.Add(when);
        }
        row.Children.Add(text);

        var b = new Border
        {
            Child = row, Padding = new Thickness(8, 6, 6, 6), Cursor = Cursors.Hand,
            Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 2),
        };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; TheApp.History.Jump(e.SessionId, e.Key); };
        return b;
    }

    static FrameworkElement StarButton(HistoryEntry e)
    {
        var icon = Icons.Make(IconName.Star, 14, e.Favorite ? "Accent" : "FgMuted");
        var b = new Border
        {
            Child = icon, Padding = new Thickness(5), Cursor = Cursors.Hand,
            Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = e.Favorite ? "取消收藏" : "收藏",
        };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.Opacity = e.Favorite ? 1 : 0.7;
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        // ★ 星标不能顺带把整行的"跳转"也点了 —— 收下这次事件
        b.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; TheApp.History.ToggleFavorite(e.Key); };
        return b;
    }
}
