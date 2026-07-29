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

    /// <summary>右键菜单:在 Explorer 里打开项目文件夹(未设/不存在则给提示)。挂到任意元素上。</summary>
    public static void AttachOpenFolder(FrameworkElement host, Project p)
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "在文件夹中打开" };
        open.Click += (_, _) =>
        {
            if (!ProjectCenter.OpenInExplorer(p.FolderPath))
                MessageBox.Show(
                    string.IsNullOrWhiteSpace(p.FolderPath) ? "该项目还没有设置文件夹。" : $"打不开文件夹:\n{p.FolderPath}\n(可能已被移动或删除)",
                    "本地 AI 中枢", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        menu.Items.Add(open);
        host.ContextMenu = menu;
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
