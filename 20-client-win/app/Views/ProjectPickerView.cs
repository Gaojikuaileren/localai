// P3c -- 项目选择器(装进右侧抽屉,由聊天里的左箭头拉开)。用户裁定:
//   · 顶部:新增项目(按下用【项目编辑器】取代当前网格,建完回网格)+ 返回普通会话;
//   · 项目以【田字形文件夹图标】排布;当前选中的项目着重色;
//   · 每个项目一个【三个点】按钮拉出菜单(在文件夹打开 / 改状态 / AI 权限 / 编辑重定向路径);
//   · 点某项目 = 进入其项目会话(onPick);点抽屉外 = 关闭(由抽屉遮罩统一处理),保留当前会话列表。
//
// ★ 放在抽屉(非 Popup):选文件夹会弹系统对话框,若用 StaysOpen=false 的浮窗会被焦点转移关掉。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class ProjectPickerView : UserControl
{
    static App TheApp => (App)Application.Current;

    readonly string? _current;
    readonly Action<string> _onPick;
    readonly Action _onNormal;
    readonly StackPanel _root = new();
    readonly ContentControl _body = new();
    bool _editing;

    public ProjectPickerView(string? current, Action<string> onPick, Action onNormal)
    {
        _current = current;
        _onPick = onPick;
        _onNormal = onNormal;

        // 顶部:返回普通会话 + 新增项目
        var add = Ui.PlusButton(() => ShowEditor(null), "新建项目");
        var normal = Chip("‹ 普通会话", _onNormal);
        var top = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(add, Dock.Right);
        DockPanel.SetDock(normal, Dock.Left);
        top.Children.Add(add);
        top.Children.Add(normal);

        _root.Children.Add(top);
        _root.Children.Add(_body);
        Content = new ScrollViewer
        {
            Content = _root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough();

        ShowGrid();
        Loaded += (_, _) => TheApp.Projects.Changed += OnChanged;
        Unloaded += (_, _) => TheApp.Projects.Changed -= OnChanged;
    }

    void OnChanged() { if (!_editing) ShowGrid(); }

    void ShowGrid()
    {
        _editing = false;
        var items = TheApp.Projects.Ongoing().ToList();
        if (items.Count == 0)
        {
            _body.Content = Ui.Stack(
                Ui.Body("还没有进行中的项目。", muted: true),
                Ui.Caption("点右上角 + 新建;已完成的在主页「项目库」。"));
            return;
        }
        var grid = new UniformGrid { Columns = 2 };
        foreach (var p in items) grid.Children.Add(FolderTile(p));
        _body.Content = grid;
    }

    void ShowEditor(Project? existing)
    {
        _editing = true;
        _body.Content = ProjectEditor.Build(existing, onDone: ShowGrid);
    }

    FrameworkElement FolderTile(Project p)
    {
        var folder = Icons.Make(IconName.Folder, 34, _current == p.ProjectId ? "Accent" : "FgSecondary");
        folder.HorizontalAlignment = HorizontalAlignment.Left;

        var name = new TextBlock { Text = p.Title, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, Margin = new Thickness(0, 6, 0, 0) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        name.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var body = new StackPanel();
        body.Children.Add(folder);
        body.Children.Add(name);
        body.Children.Add(ProjectUi.StatusChip(p.Status));

        // 三个点(右上角),点开菜单;编辑项目 -> 在本抽屉内用编辑器取代网格
        var dots = ProjectUi.DotsButton(p, () => ShowEditor(p));
        dots.HorizontalAlignment = HorizontalAlignment.Right;
        dots.VerticalAlignment = VerticalAlignment.Top;

        var overlay = new Grid();
        overlay.Children.Add(body);
        overlay.Children.Add(dots);

        var tile = new Border
        {
            Child = overlay,
            Height = 108,
            Padding = new Thickness(12),
            Margin = new Thickness(4),
            BorderThickness = new Thickness(_current == p.ProjectId ? 2 : 1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        tile.SetResourceReference(Border.BackgroundProperty, _current == p.ProjectId ? "BgSelected" : "BgSurface");
        tile.SetResourceReference(Border.BorderBrushProperty, _current == p.ProjectId ? "Accent" : "Border");
        tile.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        tile.MouseEnter += (_, _) => { if (_current != p.ProjectId) tile.SetResourceReference(Border.BackgroundProperty, "BgHover"); };
        tile.MouseLeave += (_, _) => { if (_current != p.ProjectId) tile.SetResourceReference(Border.BackgroundProperty, "BgSurface"); };
        tile.MouseLeftButtonUp += (_, _) => _onPick(p.ProjectId);
        return tile;
    }

    static FrameworkElement Chip(string text, Action onClick)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border { Child = t, Padding = new Thickness(8, 4, 8, 4), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => onClick();
        return b;
    }
}
