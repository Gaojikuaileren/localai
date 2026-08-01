// P3c -- 项目抽屉(由会话列表右侧的竖直把手拉开)。
//
// ★ 层级模型(2026-07-30 用户裁定,统一梳理):抽屉里同一时刻只处在【一个页面】,
//   页面切换时【顶部说明区】与【内容区】一起换,返回一律回到"进行中"网格:
//
//     进行中(默认)  ── 顶部:项目会话(未选 -> "选择一个项目";已选 -> "已选择「X」")
//        ├─ 已完成项目  ── 顶部:解释"已完成项目是什么 + 能做什么"
//        ├─ 已删除项目  ── 顶部:解释保留期 + 【多选彻底删除 / 全部彻底删除】
//        └─ 编辑/新建   ── 顶部:隐藏说明,专心填表;完成后回"进行中"
//
// ★ 左上角只有【一个】返回键,按层级逐级后退(用户裁定):
//     已完成/已删除项目 → 进行中项目 → 普通会话
//   说明框里不再单独放"返回项目"chip —— 返回只有一个入口,不会两处并存。
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
    readonly ContentControl _backHost = new();  // 左上角返回键:按当前页面逐级后退
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

        // 顶部固定条:左边是【按层级逐级后退】的返回键,右边是新建项目(任何页面都在)。
        //   层级(用户裁定):已完成/已删除项目 → 进行中项目 → 普通会话。
        var add = Ui.PlusButton(() => ShowEditor(null), "新建项目");
        var top = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(add, Dock.Right);
        DockPanel.SetDock(_backHost, Dock.Left);
        top.Children.Add(add);
        top.Children.Add(_backHost);

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
        UpdateBack();

        // 顶部:项目会话说明(常驻,未选也在 —— 不随选中与否挤动排版)
        FrameworkElement hint = _current is { } cur && TheApp.Projects.Find(cur) is { } proj
            ? Ui.Stack(Ui.Body($"已选择「{proj.Title}」"),
                       Ui.Caption("右侧新建/发送即【基于该项目开始会话】。"))
            : Ui.Stack(Ui.Body("选择一个项目"),
                       Ui.Caption("选中项目 = 基于它开会话;普通会话点左上「‹ 普通会话」。"));
        _header.Content = PageHeader("项目会话", HeaderState.Ongoing, hint);

        var items = TheApp.Projects.Ongoing(_wsKey, includeJustCompleted: true).ToList();   // 只有这里要含"刚完成还在 3 秒宽限"的(有巡检表播动画)
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
        UpdateBack();

        var items = TheApp.Projects.Completed(_wsKey).ToList();
        _header.Content = PageHeader("已完成项目", HeaderState.Done,
            Ui.Caption("本空间已收尾的项目。选中可只读浏览;会话区可继续或开分支。"));

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
        UpdateBack();

        var items = TheApp.Projects.DeletedProjects().ToList();
        _picked.RemoveWhere(id => !items.Any(x => x.ProjectId == id));   // 清掉已消失的选中项

        _header.Content = PageHeader("已删除项目", HeaderState.Trash,
            Ui.Stack(
                Ui.Caption($"所有工作空间共享,保留 {ProjectCenter.TrashRetentionDays} 天后自动清除。选中可只读浏览。"),
                DeletedActionRow(items)));

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

    /// <summary>
    /// 各页面顶部说明框:★ 尺寸【统一】(固定高),这样切页时下面的项目方块位置不动、好对齐(用户裁定);
    /// 描边按页面区分,跟随皮肤令牌:进行中=RiskSafe(绿) · 已完成=FgMuted(淡) · 已删除=RiskDanger(红)。
    /// </summary>
    const double HeaderHeight = 132;

    /// <summary>
    /// 左上角返回键 —— ★ 按层级【逐级后退】(用户裁定,取代原来固定的"返回普通会话"):
    ///   已完成 / 已删除项目 → 进行中项目 → 普通会话。
    /// 编辑页也回到"进行中"(相当于取消)。
    /// </summary>
    void UpdateBack()
    {
        _backHost.Content = _page == Page.Grid
            ? Chip("‹ 普通会话", () => { _current = null; ShowGrid(); _onNormal(); })
            : Chip("‹ 返回项目", ShowGrid);
    }

    /// <summary>说明框的三种状态。配色由【各皮肤自己定义】(State*Border / State*Fill),不在这里写死颜色。</summary>
    internal enum HeaderState { Ongoing, Done, Trash }

    internal static Border PageHeader(string title, HeaderState state, UIElement content)
    {
        // ★ 左右各留 4:与下方项目方块的外边距(Tile 的 Margin=4)对齐,说明框与方块左右缘齐平。
        var card = Ui.Panel(title, content, IconName.Folder, new Thickness(4, 0, 4, 8), compact: true);
        card.Height = HeaderHeight;                 // 统一高度 -> 各页方块起始位置一致
        card.BorderThickness = new Thickness(2.5);  // 描边够粗才分得清三种页面(用户反馈 1.4 太窄)
        var (borderKey, fillKey) = state switch
        {
            HeaderState.Done => ("StateDoneBorder", "StateDoneFill"),
            HeaderState.Trash => ("StateTrashBorder", "StateTrashFill"),
            _ => ("StateOngoingBorder", "StateOngoingFill"),
        };
        card.SetResourceReference(Border.BorderBrushProperty, borderKey);
        card.SetResourceReference(Border.BackgroundProperty, fillKey);
        return card;
    }

    // ---------------------------------------------------------------- 页面:编辑 / 新建
    void ShowEditor(Project? existing)
    {
        _page = Page.Editor;
        _graceTimer.Stop();
        UpdateBack();
        _header.Content = null;   // 填表时不要说明框抢空间
        _body.Content = ProjectEditor.Build(existing, onDone: ShowGrid, workspaceKey: _wsKey, onJump: JumpTo);
    }

    /// <summary>
    /// 转跳到既有项目(新建时发现同路径已有项目):按它现在在哪个板块,切到那一页并选中它。
    /// 已删除 / 已完成的也照样能跳过去(只读浏览),用户不会"建不了又找不到"。
    /// </summary>
    void JumpTo(Project p)
    {
        _current = p.ProjectId;
        _onPick(p.ProjectId);
        if (p.DeletedAt is not null) ShowDeletedBoard();
        else if (p.Status == ProjectStatus.Done) ShowCompletedBoard();
        else ShowGrid();
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

        // 底行:一般显示状态;★【已删除】页改显示【来自哪个工作空间】——
        //   垃圾桶是所有工作空间共用的,不标出处就分不清这项目原本属于谁(用户裁定)。
        FrameworkElement bottom = page == Page.Deleted
            ? OriginLabel(p, sel)
            : ProjectUi.StatusChip(p.Status, sel ? "FgOnSelected" : null);
        // ★ 同时挂在多个工作空间时,把标签也摆在这一行 —— 让人看出"这是一个项目,不是两份"。
        if (page != Page.Deleted && ProjectUi.SpaceTags(p, sel ? "FgOnSelected" : "FgMuted") is { } tags)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            bottom.Margin = new Thickness(0, 0, 6, 0);
            line.Children.Add(bottom);
            line.Children.Add(tags);
            bottom = line;
        }
        bottom.Margin = new Thickness(0, 0, 30, 0);   // 给右下角的三点让位
        var body = new StackPanel();
        body.Children.Add(folder);
        body.Children.Add(name);
        body.Children.Add(bottom);

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
            if (ProjectUi.JustClosedMenu()) return;   // 菜单刚关 -> 这一下只用于关菜单
            _current = p.ProjectId;
            _onPick(p.ProjectId);
            switch (_page) { case Page.Completed: ShowCompletedBoard(); break; case Page.Deleted: ShowDeletedBoard(); break; default: ShowGrid(); break; }
        };
        _tiles[p.ProjectId] = tile;
        return tile;
    }

    /// <summary>
    /// 【已删除项目】方块底行:标出它来自哪个工作空间 —— 回收站跨空间共用,不标出处就分不清。
    /// 用该工作空间自己的图标 + 名字,和左导航一眼对得上。
    /// </summary>
    internal static FrameworkElement OriginLabelPreview(Project p) => OriginLabel(p, false);

    static FrameworkElement OriginLabel(Project p, bool sel)
    {
        // 已删除项目只标【主工作空间】—— 回收站要回答的是"它原本在哪",不是它挂过几个标签
        var def = Workspaces.All.FirstOrDefault(w => w.Key == p.PrimarySpace);
        var fg = sel ? "FgOnSelected" : "FgMuted";
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var ic = Icons.Make(def?.Icon ?? IconName.Chat, 12, fg);
        ic.Margin = new Thickness(0, 0, 5, 0);
        ic.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(ic);
        var t = new TextBlock
        {
            Text = def is null ? p.PrimarySpace : I18n.Strings.Get(def.TitleKey),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, fg);
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        row.Children.Add(t);
        return row;
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
            Width = 30, Height = 30, Child = pin,   // 命中区放大(用户反馈太小)
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
            Opacity = p.Pinned ? 1 : 0,
            ToolTip = p.Pinned ? "取消置顶" : "置顶",
        };
        btn.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        btn.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;   // 吃掉按下,避免松开落到方块上
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
