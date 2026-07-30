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
    readonly ContentControl _hint = new();   // 常驻提示(未选/已选),不随内容重排而挤动
    readonly ContentControl _body = new();
    bool _editing;
    bool _boarding;   // 正在看"已删除/已完成"覆盖板块(此时数据变更不自动回退到网格)

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
        _root.Children.Add(_hint);   // 常驻:未选项目也在,不挤动排版
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

    void OnChanged() { if (!_editing && !_boarding) ShowGrid(); }

    // 常驻提示:未选项目时告诉用户"选一个项目 = 基于它开会话";已选时提示已选哪个。★ 固定占位,不重排。
    void UpdateHint()
    {
        FrameworkElement content;
        if (_current is { } cur)
        {
            var proj = TheApp.Projects.Find(cur);
            content = Ui.Stack(
                Ui.Body($"已选择「{proj?.Title ?? "项目"}」", muted: false),
                Ui.Caption("右侧新建/发送即【基于该项目开始会话】。选好后点空白处或关闭按钮收起。"));
        }
        else
        {
            content = Ui.Stack(
                Ui.Body("选择一个项目", muted: false),
                Ui.Caption("选中某个项目 = 基于它开始项目会话;想聊普通会话点左上「‹ 普通会话」。"));
        }
        _hint.Content = Ui.Panel("项目会话", content, IconName.Folder, new Thickness(0, 0, 0, 8), compact: true);
    }

    void ShowGrid()
    {
        _editing = false;
        _boarding = false;
        UpdateHint();
        var items = TheApp.Projects.Ongoing(_wsKey).ToList();   // 只看本工作空间的项目

        var panel = new StackPanel();
        if (items.Count == 0)
        {
            panel.Children.Add(Ui.Body("还没有进行中的项目。", muted: true));
            panel.Children.Add(Ui.Caption("点右上角 + 新建;下面可看已完成/已删除的项目。"));
        }
        else
        {
            var grid = new UniformGrid { Columns = 2 };
            foreach (var p in items) grid.Children.Add(FolderTile(p));
            panel.Children.Add(grid);
        }

        // 底部:已完成 / 已删除项目 的入口(点开覆盖式板块)
        panel.Children.Add(new Border { Height = 8 });
        var completedN = TheApp.Projects.Completed(_wsKey).Count();
        var deletedN = TheApp.Projects.DeletedProjectsCount();
        panel.Children.Add(EntryRow($"已完成项目 ({completedN})", IconName.Folder, ShowCompletedBoard));
        panel.Children.Add(EntryRow($"已删除项目 ({deletedN})", IconName.Folder, ShowDeletedBoard));
        _body.Content = panel;
    }

    void ShowEditor(Project? existing)
    {
        _editing = true;
        _body.Content = ProjectEditor.Build(existing, onDone: ShowGrid, workspaceKey: _wsKey);
    }

    // 底部入口条(点开覆盖式板块)。
    FrameworkElement EntryRow(string text, IconName icon, Action onClick)
    {
        var ic = Icons.Make(icon, 16, "FgMuted");
        ic.VerticalAlignment = VerticalAlignment.Center;
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(ic); row.Children.Add(t);
        var b = new Border { Child = row, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 2, 0, 0), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0, 1, 0, 0) };
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    // ★ 覆盖式板块:已完成项目(按工作空间)。返回回到网格。选中 = 进只读浏览(会话区给"继续/分支"按钮)。
    void ShowCompletedBoard() => ShowBoard("已完成项目", TheApp.Projects.Completed(_wsKey).ToList(),
        "这个工作空间还没有已完成的项目。");

    // ★ 覆盖式板块:已删除项目(★ 跨工作空间共享一个垃圾篓)。选中 = 进只读浏览(会话区给"恢复/彻底删除")。
    void ShowDeletedBoard() => ShowBoard("已删除项目(所有工作空间共享)", TheApp.Projects.DeletedProjects().ToList(),
        $"没有已删除的项目。删除的项目在这里保留 {ProjectCenter.TrashRetentionDays} 天。");

    void ShowBoard(string title, List<Project> items, string emptyText)
    {
        _boarding = true;
        var panel = new StackPanel();
        var back = Chip("‹ 返回项目", ShowGrid);
        panel.Children.Add(back);
        panel.Children.Add(Ui.Panel(title,
            items.Count == 0
                ? Ui.Body(emptyText, muted: true)
                : BoardGrid(items),
            IconName.Folder, new Thickness(0, 8, 0, 0), compact: true));
        _body.Content = panel;
    }

    UIElement BoardGrid(List<Project> items)
    {
        var grid = new UniformGrid { Columns = 2 };
        foreach (var p in items) grid.Children.Add(FolderTile(p));   // 复用方块:选中即 onPick → 会话区只读浏览
        return grid;
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
        body.Children.Add(ProjectUi.StatusChip(p.Status, sel ? "FgOnSelected" : null));

        // 三个点(右上角);编辑项目 -> 在本抽屉内用编辑器取代网格。
        var dots = ProjectUi.DotsButton(p, () => ShowEditor(p));
        dots.HorizontalAlignment = HorizontalAlignment.Right;
        dots.VerticalAlignment = VerticalAlignment.Top;
        // 置顶按钮(左上,像主页):平时隐藏,hover 才显示;已置顶则常亮。
        var pinBtn = PinButton(p, sel);

        var overlay = new Grid();
        overlay.Children.Add(body);
        overlay.Children.Add(pinBtn);
        overlay.Children.Add(dots);

        var pinned = p.Pinned;
        var tile = new Border
        {
            Child = overlay,
            Height = 108,
            Padding = new Thickness(12),
            Margin = new Thickness(4),
            BorderThickness = new Thickness(sel || pinned ? 2 : 1),   // 置顶态描边更粗(与主页一致)
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        tile.SetResourceReference(Border.BackgroundProperty, sel ? "BgSelected" : "BgSurface");
        tile.SetResourceReference(Border.BorderBrushProperty, sel || pinned ? "Accent" : "Border");
        tile.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        tile.MouseEnter += (_, _) => { if (!sel) tile.SetResourceReference(Border.BackgroundProperty, "BgHover"); if (!pinned) pinBtn.Opacity = 1; };
        tile.MouseLeave += (_, _) => { if (!sel) tile.SetResourceReference(Border.BackgroundProperty, "BgSurface"); if (!pinned) pinBtn.Opacity = 0; };
        // 选中后不关抽屉:只切上下文 + 重画高亮,让用户确认选的是哪个;关闭由用户点关闭/点外部
        tile.MouseLeftButtonUp += (_, _) => { _current = p.ProjectId; ShowGrid(); _onPick(p.ProjectId); };
        return tile;
    }

    // 置顶按钮(水滴 pin,像主页):平时隐藏,tile hover 时显示;已置顶则常亮 + 强调色。
    FrameworkElement PinButton(Project p, bool sel)
    {
        var pin = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M8,1.6 C5.4,1.6 3.3,3.7 3.3,6.3 C3.3,9.8 8,14.4 8,14.4 C8,14.4 12.7,9.8 12.7,6.3 C12.7,3.7 10.6,1.6 8,1.6 Z"),
            Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.4,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        var strokeKey = p.Pinned ? (sel ? "FgOnSelected" : "Accent") : (sel ? "FgOnSelected" : "FgSecondary");
        pin.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, strokeKey);
        if (p.Pinned) pin.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, sel ? "FgOnSelected" : "Accent");

        var btn = new Border
        {
            Width = 22, Height = 22, Child = pin,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
            Opacity = p.Pinned ? 1 : 0,   // 已置顶常亮;未置顶靠 hover
            ToolTip = p.Pinned ? "取消置顶" : "置顶",
        };
        btn.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        btn.MouseLeftButtonUp += (_, e) => { e.Handled = true; TheApp.Projects.TogglePin(p.ProjectId); };
        return btn;
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
