// P3c -- 项目相关的共用小工具:状态标签/颜色、选文件夹、项目行、右键"在 Explorer 打开"。
//   ChatView / ProjectDrawerView / ProjectLibraryView / HomeView 共用,避免各写一套走样。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.Services;
using WinForms = System.Windows.Forms;

namespace LocalAI.Client.Views;

public static class ProjectUi
{
    /// <summary>状态 → (显示名, 颜色令牌)。准备中=琥珀 · 进行中=安全绿 · 已完成=淡。</summary>
    public static (string Label, string BrushKey) Status(ProjectStatus s) => s switch
    {
        ProjectStatus.Preparing => ("准备中", "RiskWarning"),
        ProjectStatus.Active => ("进行中", "RiskSafe"),
        _ => ("已完成", "FgMuted"),
    };

    /// <summary>可见范围显示名。</summary>
    public static string ScopeLabel(ProjectScope s) => s switch
    {
        ProjectScope.Family => "家庭",
        ProjectScope.OnlyMe => "仅本人",
        _ => "个人",
    };

    /// <summary>可见范围一行解释。</summary>
    public static string ScopeHint(ProjectScope s) => s switch
    {
        ProjectScope.Family => "家庭:同一网络里其它 PC 的客户端可见、可操作。",
        ProjectScope.OnlyMe => "仅本人:只有你本人可见(即便同机另一位成员也看不到)。",
        _ => "个人:只在本机显示,不共享到其它 PC。",
    };

    /// <summary>AI 权限的显示名。</summary>
    public static string AiLabel(AiPermission a) => a switch
    {
        AiPermission.ReadOnly => "只读",
        AiPermission.Edit => "可改文件",
        _ => "需批准",
    };

    /// <summary>AI 权限的一行解释(编辑时展示,让人清楚给了什么)。</summary>
    public static string AiHint(AiPermission a) => a switch
    {
        AiPermission.ReadOnly => "AI 只能读取项目内容,不改动任何文件。",
        AiPermission.Edit => "AI 可直接改项目文件夹里的文件(接入后须配操作历史,可回滚)。",
        _ => "AI 可提议修改,但每次改动都要你批准后才生效。",
    };

    /// <summary>让用户选一个文件夹(项目实际目录 / 附件目录)。取消返回 null。</summary>
    public static string? PickFolder(string description)
    {
        // ★ 整段 try:文件夹对话框走 Windows 外壳(COM),个别机器/单文件发布下可能抛;
        //   配合全局兜底,别再让"选择附件文件夹"闪退(用户反馈)。
        try
        {
            using var d = new WinForms.FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
            return d.ShowDialog() == WinForms.DialogResult.OK ? d.SelectedPath : null;
        }
        catch (Exception ex)
        {
            ConfirmDialog.Show("打不开文件夹选择框", ex.Message, confirmText: "好", cancelText: "关闭");
            return null;
        }
    }

    static ProjectCenter Projects => ((LocalAI.Client.App)Application.Current).Projects;
    static ChatCenter Chat => ((LocalAI.Client.App)Application.Current).Chat;

    // ★ 菜单开着时点方块,应该【只关菜单】,不该顺势点进项目(用户反馈)。
    //   成因:关菜单发生在鼠标【按下】,而方块的动作挂在【松开】上 —— 松开那下就落到方块上了。
    //   办法:记下菜单关闭的时刻;方块在极短时间内(300ms)收到的点击一律忽略。
    /// <summary>菜单开着或刚关掉 —— 这一次点击只用于关菜单,调用方应直接 return(统一由 MenuHost 判)。</summary>
    public static bool JustClosedMenu() => MenuHost.SwallowClick;

    /// <summary>
    /// 项目的下拉菜单(用户裁定:不用右键,改成【三个点】按钮拉出这个菜单)。
    ///   在文件夹打开 · 置顶 · 改状态 · 发送到工作空间 · AI 权限 · 编辑(重定向路径) · 删除项目。
    /// </summary>
    public static ContextMenu BuildMenu(Project p, Action onEdit, FrameworkElement? anchor = null, Action? onNavigate = null)
    {
        // ★ 已删除项目的菜单【与正常项目不同】(用户裁定):它不该有"删除/改状态/发送空间"这些,
        //   只该有【还原】与【彻底删除】(以及看一眼文件夹)。
        if (p.DeletedAt is not null) return BuildDeletedMenu(p, anchor, onNavigate);

        var m = new ContextMenu();

        var open = new MenuItem { Header = "在文件夹中打开" };
        open.Click += (_, _) =>
        {
            if (!ProjectCenter.OpenInExplorer(p.FolderPath))
                ConfirmDialog.Show("打不开文件夹",
                    string.IsNullOrWhiteSpace(p.FolderPath) ? "该项目还没有设置文件夹。" : $"打不开文件夹:\n{p.FolderPath}\n(可能已被移动或删除)",
                    confirmText: "好", cancelText: "关闭");
        };
        m.Items.Add(open);

        // 置顶(项目列表里也能置顶 —— 用户裁定)
        var pin = new MenuItem { Header = p.Pinned ? "取消置顶" : "置顶项目", IsChecked = p.Pinned };
        pin.Click += (_, _) => Projects.TogglePin(p.ProjectId);
        m.Items.Add(pin);

        m.Items.Add(new Separator());
        foreach (var st in new[] { ProjectStatus.Preparing, ProjectStatus.Active, ProjectStatus.Done })
        {
            var (label, _) = Status(st);
            var mi = new MenuItem { Header = "标记为 " + label, IsChecked = p.Status == st };
            var captured = st;
            mi.Click += (_, _) => { Projects.SetStatus(p.ProjectId, captured); onNavigate?.Invoke(); };
            m.Items.Add(mi);
        }

        m.Items.Add(new Separator());
        // 可见范围:家庭 = 同网其它 PC 共享可见可操作;个人 = 只在本机显示(用户裁定)。
        // ★ 只给这两项 —— "个人"与"仅本人"对项目来说是重复的(用户反馈),仅本人保留在枚举里供会话/D45 用。
        var vis = new MenuItem { Header = "可见范围" };
        foreach (var sc in new[] { ProjectScope.Family, ProjectScope.Personal })
        {
            var mi = new MenuItem { Header = ScopeLabel(sc), IsChecked = p.Scope == sc, ToolTip = ScopeHint(sc) };
            var captured = sc;
            mi.Click += (_, _) => Projects.SetScope(p.ProjectId, captured);
            vis.Items.Add(mi);
        }
        m.Items.Add(vis);

        var ai = new MenuItem { Header = "AI 权限" };
        foreach (var perm in new[] { AiPermission.ReadOnly, AiPermission.Ask, AiPermission.Edit })
        {
            var mi = new MenuItem { Header = AiLabel(perm), IsChecked = p.Ai == perm, ToolTip = AiHint(perm) };
            var captured = perm;
            mi.Click += (_, _) => Projects.SetAiPermission(p.ProjectId, captured);
            ai.Items.Add(mi);
        }
        m.Items.Add(ai);

        // 发送到别的工作空间(项目不跨空间共享;会话跟着走)
        var toWs = new MenuItem { Header = "发送到工作空间" };
        foreach (var w in Workspaces.All)
        {
            if (w.Key == p.WorkspaceKey) continue;
            var mi = new MenuItem { Header = I18n.Strings.Get(w.TitleKey) };
            var key = w.Key;
            mi.Click += (_, _) => { Projects.MoveToWorkspace(p.ProjectId, key); Chat.SetSessionsWorkspace(p.ProjectId, key); };
            toWs.Items.Add(mi);
        }
        m.Items.Add(toWs);

        m.Items.Add(new Separator());
        var edit = new MenuItem { Header = "编辑项目 / 重定向路径…" };
        edit.Click += (_, _) => onEdit();
        m.Items.Add(edit);

        var del = new MenuItem { Header = "删除项目…" };
        del.Click += (_, _) => ConfirmDelete(p, anchor);
        m.Items.Add(del);
        return m;
    }

    /// <summary>
    /// 【已删除项目】的菜单 —— 与正常项目完全不同(用户裁定):
    /// 只有 还原项目 / 彻底删除项目(以及看一眼文件夹)。没有"删除项目"、不给改状态/发送空间。
    /// </summary>
    static ContextMenu BuildDeletedMenu(Project p, FrameworkElement? anchor, Action? onNavigate)
    {
        var m = new ContextMenu();

        var open = new MenuItem { Header = "在文件夹中打开" };
        open.Click += (_, _) =>
        {
            if (!ProjectCenter.OpenInExplorer(p.FolderPath))
                ConfirmDialog.Show("打不开文件夹",
                    string.IsNullOrWhiteSpace(p.FolderPath) ? "该项目还没有设置文件夹。" : $"打不开文件夹:\n{p.FolderPath}",
                    confirmText: "好", cancelText: "关闭");
        };
        m.Items.Add(open);
        m.Items.Add(new Separator());

        var restore = new MenuItem { Header = "还原项目" };
        restore.Click += (_, _) => { Projects.RestoreProject(p.ProjectId); Chat.RestoreProjectSessions(p.ProjectId); onNavigate?.Invoke(); };
        m.Items.Add(restore);

        var purge = new MenuItem { Header = "彻底删除项目…" };
        purge.Click += (_, _) => ConfirmPurge(p, anchor, onNavigate);
        m.Items.Add(purge);
        return m;
    }

    /// <summary>彻底删除的二次确认(不可恢复)。★ 仍然不动磁盘文件夹。</summary>
    public static void ConfirmPurge(Project p, FrameworkElement? anchor, Action? onNavigate = null)
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            var ok = ConfirmDialog.Show("彻底删除项目",
                $"彻底删除「{p.Title}」及其所有会话?\n\n不可恢复。(仍不会删除磁盘上的项目文件夹)",
                confirmText: "彻底删除", danger: true);
            if (!ok) return;
            Chat.PurgeProjectSessions(p.ProjectId);
            Projects.PurgeProject(p.ProjectId);
            onNavigate?.Invoke();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    // 删除项目的【二次确认】。★ 只删客户端里的项目与其会话,【不动磁盘文件夹】;会话进"已删除"(30 天可恢复)。
    // ★ 用独立模态窗(ConfirmDialog)而不是浮窗:浮窗要登记 Overlay,会把抽屉一起关掉、锚点随之消失,
    //   结果"点了删除没反应"(用户两次反馈的真正成因)。延到菜单关闭后再弹,避免菜单关闭抢焦点。
    static void ConfirmDelete(Project p, FrameworkElement? anchor)
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            var ok = ConfirmDialog.Show("删除项目",
                $"删除项目「{p.Title}」?\n\n不会删除磁盘上的项目文件夹;项目连同它的所有会话一起进「已删除项目」" +
                $"({ProjectCenter.TrashRetentionDays} 天内可恢复,过期自动清除)。",
                confirmText: "删除项目", danger: true);
            if (!ok) return;
            Chat.DeleteProjectSessions(p.ProjectId);   // 会话跟随项目进垃圾篓(保留 ProjectId)
            Projects.Delete(p.ProjectId);              // 软删除项目
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// 主页项目方块的【精简菜单】(用户裁定):只有 置顶/取消置顶 + 在文件夹中打开。
    /// 更细的设置(状态、可见范围、AI 权限、发送空间、编辑、删除)请到对应工作空间的项目抽屉里做 ——
    /// 主页是"回到刚才那件事"的入口,不该承担项目管理。
    /// </summary>
    public static ContextMenu BuildHomeMenu(Project p)
    {
        var m = new ContextMenu();
        var pin = new MenuItem { Header = p.Pinned ? "取消置顶" : "置顶项目", IsChecked = p.Pinned };
        pin.Click += (_, _) => Projects.TogglePin(p.ProjectId);
        m.Items.Add(pin);

        var open = new MenuItem { Header = "在文件夹中打开" };
        open.Click += (_, _) =>
        {
            if (!ProjectCenter.OpenInExplorer(p.FolderPath))
                ConfirmDialog.Show("打不开文件夹",
                    string.IsNullOrWhiteSpace(p.FolderPath) ? "该项目还没有设置文件夹。" : $"打不开文件夹:\n{p.FolderPath}",
                    confirmText: "好", cancelText: "关闭");
        };
        m.Items.Add(open);
        return m;
    }

    /// <summary>三个点按钮:左键点开菜单(替代右键)。homeMenu=true 用主页的精简菜单。</summary>
    public static FrameworkElement DotsButton(Project p, Action onEdit, Action? onNavigate = null, bool homeMenu = false)
    {
        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int k = 0; k < 3; k++)
        {
            var e = new System.Windows.Shapes.Ellipse { Width = 3, Height = 3, Margin = new Thickness(1.3, 0, 1.3, 0) };
            e.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "FgSecondary");
            dots.Children.Add(e);
        }
        // 命中区放大到 34×30(用户反馈"按钮太小,一点就点进项目了")
        var b = new Border { Child = dots, Width = 34, Height = 30, Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        // 按下就拦住:方块的动作挂在松开上,这里吃掉按下才不会"顺带点进项目"
        b.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var menu = homeMenu ? BuildHomeMenu(p) : BuildMenu(p, onEdit, b, onNavigate);
            MenuHost.Show(menu, b);   // 统一出口:开/关状态由 MenuHost 记,点外部由主窗口一次性拦掉
        };
        return b;
    }

    /// <summary>状态小圆点 + 文字,用于项目行/方块。fgKey 非空时文字用它(选中态传 FgOnSelected 才不黑底黑字)。</summary>
    public static StackPanel StatusChip(ProjectStatus s, string? fgKey = null)
    {
        var (label, key) = Status(s);
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, fgKey ?? key);
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, fgKey ?? "FgMuted");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(dot);
        row.Children.Add(t);
        return row;
    }
}
