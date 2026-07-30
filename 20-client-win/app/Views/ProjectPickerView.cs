// P3c -- 项目抽屉(由会话列表右侧的竖直把手拉开)。
//
// ★ 层级模型(2026-07-30 用户裁定,统一梳理):抽屉里同一时刻只处在【一个页面】,
//   页面切换时【顶部说明区】与【内容区】一起换,返回一律回到"进行中"网格:
//
//     进行中(默认)  ── 顶部:项目会话(未选 -> "选择一个项目";已选 -> "已选择「X」")
//        ├─ 已完成项目  ── 顶部:解释"已完成项目是什么 + 能做什么" + 返回
//        ├─ 已删除项目  ── 顶部:解释保留期 + 【多选彻底删除 / 全部彻底删除】+ 返回
//        └─ 编辑/新建   ── 顶部:隐藏说明,专心填表;完成后回"进行中"
//
// 三种项目的【菜单不同】(见 ProjectUi):正常项目(全功能)/ 已删除项目(只有还原、彻底删除)。
// 已完成项目在这里改状态后会【自动回到进行中网格】,不用手动返回(用户反馈)。
//
// 方块布局与主页一致:pin 在【右上角】、三个点在【右下角】。
// 在"进行中"里把项目标记为已完成 -> 停留 3 秒再【向右划走】(与待办事项同一手感)。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class ProjectPickerView : UserControl
{
    static App TheApp => (App)Application.Current;

    enum Page { Grid, Completed, Deleted, Editor }

    readonly string _wsKey;
    string? _current;   // 当前选中项目;选后不关抽屉、只高亮,让用户确认(用户裁定)
    readonly Action<string> _onPick;
    readonly Action _onNormal;
    readonly StackPanel _root = new();
    readonly ContentControl _header = new();   // 顶部说明区:随页面切换
    readonly ContentControl _body = new();
    Page _page = Page.Grid;

    readonly HashSet<string> _picked = new();  // 已删除板块里的多选集合
    bool _multi;                                // 已删除板块:多选模式

    // 完成动画:标记完成后停留 3 秒再划走
    readonly DispatcherTimer _graceTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly Dictionary<string, FrameworkElement> _tiles = new();
    readonly HashSet<string> _sliding = new();

    public ProjectPickerView(string workspaceKey, string? current, Action<string> onPick, Action onNormal)
    {
        _wsKey = workspaceKey;
        _current = current;
        _onPick = onPick;
        _onNormal = onNormal;

        // 顶部固定条:返回普通会话 + 新建项目(任何页面都在)
        var add = Ui.PlusButton(() => ShowEditor(null), "新建项目");
        var normal = Chip("‹ 普通会话", () => { _current = null; ShowGrid(); _onNormal(); });
        var top = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(add, Dock.Right);
        DockPanel.SetDock(normal, Dock.Left);
        top.Children.Add(add);
        top.Children.Add(normal);

        _root.Children.Add(top);
        _root.Children.Add(_header);
        _root.Children.Add(_body);
        Content = new ScrollViewer
        {
            Content = _root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough();

        ShowGrid();
        _graceTimer.Tick += (_, _) => SweepCompleted();
        Loaded += (_, _) => TheApp.Projects.Changed += OnChanged;
        Unloaded += (_, _) => { TheApp.Projects.Changed -= OnChanged; _graceTimer.Stop(); };
    }

    // 数据变了就刷新【当前页面】(不要粗暴跳回网格 —— 那会把用户从板块里踢出去)
    void OnChanged()
    {
        switch (_page)
        {
            case Page.Grid: ShowGrid(); break;
            case Page.Completed: ShowCompletedBoard(); break;
            case Page.Deleted: ShowDeletedBoard(); break;
            // 编辑页不自动重建,否则用户填一半的表会被冲掉
        }
    }

    // ---------------------------------------------------------------- 页面:进行中(默认)
    void ShowGrid()
    {
        _page = Page.Grid;
        _multi = false;
        _picked.Clear();
        _tiles.Clear();

        // 顶部:项目会话说明(常驻,未选也在 —— 不随选中与否挤动排版)
        FrameworkElement hint = _current is { } cur && TheApp.Projects.Find(cur) is { } proj
            ? Ui.Stack(Ui.Body($"已选择「{proj.Title}」"),
                       Ui.Caption("右侧新建/发送即【基于该项目开始会话】。点空白处或关闭按钮收起抽屉。"))
            : Ui.Stack(Ui.Body("选择一个项目"),
                       Ui.Caption("选中某个项目 = 基于它开始项目会话;想聊普通会话点左上「‹ 普通会话」。"));
        _header.Content = Ui.Panel("项目会话", hint, IconName.Folder, new Thickness(0, 0, 0, 8), compact: true);

        var items = TheApp.Projects.Ongoing(_wsKey).ToList();   // 含"刚完成还在 3 秒宽限"的
        var panel = new StackPanel();
        if (items.Count == 0)
        {
            panel.Children.Add(Ui.Body("还没有进行中的项目。", muted: true));
            panel.Children.Add(Ui.Caption("点右上角 + 新建;下面可看已完成 / 已删除的项目。"));
        }
        else
        {
            var grid = new UniformGrid { Columns = 2 };
            foreach (var p in items) grid.Children.Add(Tile(p, Page.Grid));
            panel.Children.Add(grid);
        }

        panel.Children.Add(new Border { Height = 8 });
        panel.Children.Add(EntryRow($"已完成项目 ({TheApp.Projects.Completed(_wsKey).Count()})", ShowCompletedBoard));
        panel.Children.Add(EntryRow($"已删除项目 ({TheApp.Projects.DeletedProjectsCount()})", ShowDeletedBoard));
        _body.Content = panel;

        // 有"刚完成"的就开表,到点播划走动画
        if (TheApp.Projects.HasCompletionGrace(_wsKey)) _graceTimer.Start(); else _graceTimer.Stop();
    }

    // 宽限到点:把刚完成的方块【向右划走 + 淡出】,划完再重建(此时它已不在进行中列表里)
    void SweepCompleted()
    {
        if (_page != Page.Grid) { _graceTimer.Stop(); return; }
        var expired = TheApp.Projects.Items
            .Where(p => p.Status == ProjectStatus.Done && p.DeletedAt is null && p.CompletedAt is not null
                        && !ProjectCenter.IsCompletingNow(p) && _tiles.ContainsKey(p.ProjectId) && !_sliding.Contains(p.ProjectId))
            .ToList();
        foreach (var p in expired) SlideOut(p.ProjectId);
        if (!TheApp.Projects.HasCompletionGrace(_wsKey) && _sliding.Count == 0) { _graceTimer.Stop(); ShowGrid(); }
    }

    void SlideOut(string projectId)
    {
        if (!_tiles.TryGetValue(projectId, out var tile)) return;
        _sliding.Add(projectId);
        var t = new TranslateTransform();
        tile.RenderTransform = t;
        tile.IsHitTestVisible = false;   // 动画期间不可再交互(与待办一致)
        var slide = new DoubleAnimation(0, 260, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280));
        fade.Completed += (_, _) => { _sliding.Remove(projectId); if (_page == Page.Grid) ShowGrid(); };
        t.BeginAnimation(TranslateTransform.XProperty, slide);
        tile.BeginAnimation(OpacityProperty, fade);
    }

    // ---------------------------------------------------------------- 页面:已完成项目(按工作空间)
    void ShowCompletedBoard()
    {
        _page = Page.Completed;
        _multi = false;
        _picked.Clear();
        _tiles.Clear();
        _graceTimer.Stop();

        var items = TheApp.Projects.Completed(_wsKey).ToList();
        _header.Content = Ui.Panel("已完成项目",
            Ui.Stack(
                Ui.Caption("这里是本工作空间已收尾的项目。选中可【只读浏览】它的会话记录;"),
                Ui.Caption("在会话区可【继续此项目】(移回进行中)或【开启此项目分支】(复制成新的准备中项目)。"),
                BackRow()),
            IconName.Folder, new Thickness(0, 0, 0, 8), compact: true);

        var panel = new StackPanel();
        if (items.Count == 0) panel.Children.Add(Ui.Body("这个工作空间还没有已完成的项目。", muted: true));
        else
        {
            var grid = new UniformGrid { Columns = 2 };
            foreach (var p in items) grid.Children.Add(Tile(p, Page.Completed));
            panel.Children.Add(grid);
        }
        _body.Content = panel;
    }

    // ---------------------------------------------------------------- 页面:已删除项目(所有工作空间共享)
    void ShowDeletedBoard()
    {
        _page = Page.Deleted;
        _tiles.Clear();
        _graceTimer.Stop();

        var items = TheApp.Projects.DeletedProjects().ToList();
        _picked.RemoveWhere(id => !items.Any(x => x.ProjectId == id));   // 清掉已消失的选中项

        _header.Content = Ui.Panel("已删除项目",
            Ui.Stack(
                Ui.Caption($"所有工作空间共享一个回收站,保留 {ProjectCenter.TrashRetentionDays} 天后自动清除(不可恢复)。"),
                Ui.Caption("选中可【只读浏览】;在会话区可【恢复此项目】或【彻底删除】。"),
                DeletedActionRow(items)),
            IconName.Folder, new Thickness(0, 0, 0, 8), compact: true);

        var panel = new StackPanel();
        if (items.Count == 0) panel.Children.Add(Ui.Body("没有已删除的项目。", muted: true));
        else
        {
            var grid = new UniformGrid { Columns = 2 };
            foreach (var p in items) grid.Children.Add(Tile(p, Page.Deleted));
            panel.Children.Add(grid);
        }
        _body.Content = panel;
    }

    // 已删除板块的动作条:多选 / 彻底删除所选 / 全部彻底删除 / 返回
    FrameworkElement DeletedActionRow(List<Project> items)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(Chip("‹ 返回项目", ShowGrid));
        if (items.Count == 0) return row;

        if (!_multi)
        {
            row.Children.Add(Chip("多选", () => { _multi = true; _picked.Clear(); ShowDeletedBoard(); }));
            row.Children.Add(Chip($"全部彻底删除 ({items.Count})", () =>
            {
                if (!ConfirmDialog.Show("全部彻底删除",
                        $"彻底删除全部 {items.Count} 个已删除项目及其所有会话?\n\n不可恢复。(仍不会删除磁盘上的文件夹)",
                        confirmText: "全部彻底删除", danger: true)) return;
                foreach (var p in items) { TheApp.Chat.PurgeProjectSessions(p.ProjectId); TheApp.Projects.PurgeProject(p.ProjectId); }
                if (_current is { } c && !TheApp.Projects.Items.Any(x => x.ProjectId == c)) { _current = null; _onNormal(); }
                ShowDeletedBoard();
            }, "RiskDanger"));
            return row;
        }

        var all = _picked.Count == items.Count && items.Count > 0;
        row.Children.Add(Chip(all ? "取消全选" : "全选", () =>
        {
            _picked.Clear();
            if (!all) foreach (var p in items) _picked.Add(p.ProjectId);
            ShowDeletedBoard();
        }));
        row.Children.Add(Chip($"彻底删除所选 ({_picked.Count})", () =>
        {
            if (_picked.Count == 0) return;
            if (!ConfirmDialog.Show("彻底删除所选",
                    $"彻底删除选中的 {_picked.Count} 个项目及其所有会话?\n\n不可恢复。(仍不会删除磁盘上的文件夹)",
                    confirmText: "彻底删除", danger: true)) return;
            foreach (var id in _picked.ToList()) { TheApp.Chat.PurgeProjectSessions(id); TheApp.Projects.PurgeProject(id); }
            if (_current is { } c && !TheApp.Projects.Items.Any(x => x.ProjectId == c)) { _current = null; _onNormal(); }
            _picked.Clear();
            _multi = false;
            ShowDeletedBoard();
        }, _picked.Count > 0 ? "RiskDanger" : "FgMuted"));
        row.Children.Add(Chip("退出多选", () => { _multi = false; _picked.Clear(); ShowDeletedBoard(); }));
        return row;
    }

    FrameworkElement BackRow()
    {
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(Chip("‹ 返回项目", ShowGrid));
        return row;
    }

    // ---------------------------------------------------------------- 页面:编辑 / 新建
    void ShowEditor(Project? existing)
    {
        _page = Page.Editor;
        _graceTimer.Stop();
        _header.Content = null;   // 填表时不要说明框抢空间
        _body.Content = ProjectEditor.Build(existing, onDone: ShowGrid, workspaceKey: _wsKey);
    }

    // ---------------------------------------------------------------- 方块
    // 布局与主页一致:pin 在【右上角】,三个点在【右下角】(用户裁定)。
    FrameworkElement Tile(Project p, Page page)
    {
        var sel = _current == p.ProjectId;
        // ★ 墨白皮肤统一高亮规则:选中底色是 BgSelected(近黑),前景一律走 FgOnSelected(白)。
        var folder = Icons.Make(IconName.Folder, 30, sel ? "FgOnSelected" : "FgSecondary");
        folder.HorizontalAlignment = HorizontalAlignment.Left;

        var name = new TextBlock
        {
            Text = p.Title, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 34, Margin = new Thickness(0, 6, 26, 0),   // 右边给三点让位
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, sel ? "FgOnSelected" : "FgPrimary");
        name.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var status = ProjectUi.StatusChip(p.Status, sel ? "FgOnSelected" : null);
        status.Margin = new Thickness(0, 0, 30, 0);   // 给右下角的三点让位
        var body = new StackPanel();
        body.Children.Add(folder);
        body.Children.Add(name);
        body.Children.Add(status);

        var overlay = new Grid();
        overlay.Children.Add(body);

        var pinned = p.Pinned;
        FrameworkElement? pinBtn = null;
        if (page == Page.Grid)
        {
            // 右上角:置顶(平时隐藏,hover 显示;已置顶常亮)—— 与主页一致
            pinBtn = PinButton(p, sel);
            overlay.Children.Add(pinBtn);
        }
        else if (page == Page.Deleted && _multi)
        {
            // 已删除 + 多选:左上角勾选框
            var cb = new CheckBox { IsChecked = _picked.Contains(p.ProjectId), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            cb.Checked += (_, _) => { _picked.Add(p.ProjectId); ShowDeletedBoard(); };
            cb.Unchecked += (_, _) => { _picked.Remove(p.ProjectId); ShowDeletedBoard(); };
            overlay.Children.Add(cb);
        }

        // 右下角:三个点(用户裁定)。菜单按项目状态分流(正常 / 已删除),改状态后回到进行中网格。
        var dots = ProjectUi.DotsButton(p, () => ShowEditor(p), onNavigate: ShowGrid);
        dots.HorizontalAlignment = HorizontalAlignment.Right;
        dots.VerticalAlignment = VerticalAlignment.Bottom;
        overlay.Children.Add(dots);

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
        tile.MouseEnter += (_, _) => { if (!sel) tile.SetResourceReference(Border.BackgroundProperty, "BgHover"); if (pinBtn is not null && !pinned) pinBtn.Opacity = 1; };
        tile.MouseLeave += (_, _) => { if (!sel) tile.SetResourceReference(Border.BackgroundProperty, "BgSurface"); if (pinBtn is not null && !pinned) pinBtn.Opacity = 0; };
        // 选中后不关抽屉:只切上下文 + 重画高亮,让用户确认选的是哪个
        tile.MouseLeftButtonUp += (_, _) =>
        {
            _current = p.ProjectId;
            _onPick(p.ProjectId);
            switch (_page) { case Page.Completed: ShowCompletedBoard(); break; case Page.Deleted: ShowDeletedBoard(); break; default: ShowGrid(); break; }
        };
        _tiles[p.ProjectId] = tile;
        return tile;
    }

    // 置顶按钮(水滴 pin,与主页同款):右上角;平时隐藏,hover 显示;已置顶常亮 + 强调色。
    FrameworkElement PinButton(Project p, bool sel)
    {
        var pin = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M8,1.6 C5.4,1.6 3.3,3.7 3.3,6.3 C3.3,9.8 8,14.4 8,14.4 C8,14.4 12.7,9.8 12.7,6.3 C12.7,3.7 10.6,1.6 8,1.6 Z"),
            Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.4,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        var key = sel ? "FgOnSelected" : p.Pinned ? "Accent" : "FgSecondary";
        pin.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, key);
        if (p.Pinned) pin.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, sel ? "FgOnSelected" : "Accent");

        var btn = new Border
        {
            Width = 22, Height = 22, Child = pin,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
            Opacity = p.Pinned ? 1 : 0,
            ToolTip = p.Pinned ? "取消置顶" : "置顶",
        };
        btn.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        btn.MouseLeftButtonUp += (_, e) => { e.Handled = true; TheApp.Projects.TogglePin(p.ProjectId); };
        return btn;
    }

    // 底部入口条(进入覆盖式板块)
    FrameworkElement EntryRow(string text, Action onClick)
    {
        var ic = Icons.Make(IconName.Folder, 16, "FgMuted");
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

    static FrameworkElement Chip(string text, Action onClick, string colorKey = "FgSecondary")
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border { Child = t, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 6, 4), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }
}
