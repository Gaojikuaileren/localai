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
        using var d = new WinForms.FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
        return d.ShowDialog() == WinForms.DialogResult.OK ? d.SelectedPath : null;
    }

    static ProjectCenter Projects => ((LocalAI.Client.App)Application.Current).Projects;
    static ChatCenter Chat => ((LocalAI.Client.App)Application.Current).Chat;

    /// <summary>
    /// 项目的下拉菜单(用户裁定:不用右键,改成【三个点】按钮拉出这个菜单)。
    ///   在文件夹打开 · 置顶 · 改状态 · 发送到工作空间 · AI 权限 · 编辑(重定向路径) · 删除项目。
    /// </summary>
    public static ContextMenu BuildMenu(Project p, Action onEdit, FrameworkElement? anchor = null)
    {
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
            mi.Click += (_, _) => Projects.SetStatus(p.ProjectId, captured);
            m.Items.Add(mi);
        }

        m.Items.Add(new Separator());
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

    // 删除项目的【二次确认】。★ 只删客户端里的项目与其会话,【不动磁盘文件夹】;会话进"已删除"(30 天可恢复)。
    // ★ 用独立模态窗(ConfirmDialog)而不是浮窗:浮窗要登记 Overlay,会把抽屉一起关掉、锚点随之消失,
    //   结果"点了删除没反应"(用户两次反馈的真正成因)。延到菜单关闭后再弹,避免菜单关闭抢焦点。
    static void ConfirmDelete(Project p, FrameworkElement? anchor)
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            var ok = ConfirmDialog.Show("删除项目",
                $"删除项目「{p.Title}」?\n\n不会删除磁盘上的项目文件夹;只从客户端移除这个项目及其所有会话" +
                $"(会话进「已删除」,{ChatCenter.TrashRetentionDays} 天内可恢复)。",
                confirmText: "删除项目", danger: true);
            if (!ok) return;
            Chat.DeleteProjectSessions(p.ProjectId);
            Projects.Delete(p.ProjectId);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>三个点按钮:左键点开上面的菜单(替代右键)。</summary>
    public static FrameworkElement DotsButton(Project p, Action onEdit)
    {
        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int k = 0; k < 3; k++)
        {
            var e = new System.Windows.Shapes.Ellipse { Width = 3, Height = 3, Margin = new Thickness(1.3, 0, 1.3, 0) };
            e.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "FgSecondary");
            dots.Children.Add(e);
        }
        var b = new Border { Child = dots, Width = 26, Height = 22, Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var menu = BuildMenu(p, onEdit, b);
            menu.PlacementTarget = b;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        return b;
    }

    /// <summary>状态小圆点 + 文字,用于项目行/方块。</summary>
    public static StackPanel StatusChip(ProjectStatus s)
    {
        var (label, key) = Status(s);
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(dot);
        row.Children.Add(t);
        return row;
    }
}
