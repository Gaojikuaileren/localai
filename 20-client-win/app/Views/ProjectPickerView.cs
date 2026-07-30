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

    readonly string _wsKey;
    string? _current;   // 当前选中项目;选后不关抽屉、只高亮,让用户确认(用户裁定)
    readonly Action<string> _onPick;
    readonly Action _onNormal;
    readonly StackPanel _root = new();
    readonly ContentControl _body = new();
    bool _editing;

    public ProjectPickerView(string workspaceKey, string? current, Action<string> onPick, Action onNormal)
    {
        _wsKey = workspaceKey;
        _current = current;
        _onPick = onPick;
        _onNormal = onNormal;

        // 顶部:返回普通会话 + 新增项目
        var add = Ui.PlusButton(() => ShowEditor(null), "新建项目");
        var normal = Chip("‹ 普通会话", () => { _current = null; ShowGrid(); _onNormal(); });
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
        var items = TheApp.Projects.Ongoing(_wsKey).ToList();   // 只看本工作空间的项目

        var panel = new StackPanel();
        // ★ 选中某项目后给出文字提示:选择不关抽屉,用这句告诉用户"接下来就是基于它开会话"(用户裁定)。
        if (_current is not null)
        {
            var proj = TheApp.Projects.Find(_current);
            var hint = Ui.Panel("已选择项目",
                Ui.Stack(
                    Ui.Body($"「{proj?.Title ?? "项目"}」", muted: false),
                    Ui.Caption("已切到该项目会话 —— 右侧新建/发送即【基于该项目开始会话】。选好后点空白处或关闭按钮收起。")),
                IconName.Folder, new Thickness(0, 0, 0, 8), compact: true);
            panel.Children.Add(hint);
        }

        if (items.Count == 0)
        {
            panel.Children.Add(Ui.Body("还没有进行中的项目。", muted: true));
            panel.Children.Add(Ui.Caption("点右上角 + 新建;已完成的在主页「项目库」。"));
        }
        else
        {
            var grid = new UniformGrid { Columns = 2 };
            foreach (var p in items) grid.Children.Add(FolderTile(p));
            panel.Children.Add(grid);
        }
        _body.Content = panel;
    }

    void ShowEditor(Project? existing)
    {
        _editing = true;
        _body.Content = ProjectEditor.Build(existing, onDone: ShowGrid, workspaceKey: _wsKey);
    }

    FrameworkElement FolderTile(Project p)
    {
        // ★ 墨白皮肤统一高亮规则:选中态底色是 BgSelected(近黑),前景一律走 FgOnSelected(白),
        //   否则就是"黑底黑字看不清"(用户反馈)。图标/标题/置顶点/状态字都照此。
        var sel = _current == p.ProjectId;
        var folder = Icons.Make(IconName.Folder, 34, sel ? "FgOnSelected" : "FgSecondary");
        folder.HorizontalAlignment = HorizontalAlignment.Left;

        var name = new TextBlock { Text = p.Title, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, Margin = new Thickness(0, 6, 0, 0) };
        name.SetResourceReference(TextBlock.ForegroundProperty, sel ? "FgOnSelected" : "FgPrimary");
        name.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var body = new StackPanel();
        body.Children.Add(folder);
        body.Children.Add(name);
        var footer = new StackPanel { Orientation = Orientation.Horizontal };
        if (p.Pinned)
        {
            // 置顶标记:小圆点(置顶的项目排最前,见 ProjectCenter.Ongoing)
            var pinDot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            pinDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, sel ? "FgOnSelected" : "Accent");
            footer.Children.Add(pinDot);
        }
        footer.Children.Add(ProjectUi.StatusChip(p.Status, sel ? "FgOnSelected" : null));
        body.Children.Add(footer);

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
        // 选中后不关抽屉:只切上下文 + 重画高亮,让用户确认选的是哪个;关闭由用户点关闭/点外部
        tile.MouseLeftButtonUp += (_, _) => { _current = p.ProjectId; ShowGrid(); _onPick(p.ProjectId); };
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
