// P3c -- 项目库(右侧抽屉):列出【已完成】的项目。可右键在 Explorer 打开,或改回进行中/准备中(捞回)。
//   从主页项目板块右上角、或项目抽屉的"项目库 ›"进入。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class ProjectLibraryView : UserControl
{
    static App TheApp => (App)Application.Current;
    readonly StackPanel _root = new();

    public ProjectLibraryView()
    {
        Content = new ScrollViewer
        {
            Content = _root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough();
        _root.Margin = new Thickness(4, 2, 4, 12);

        Build();
        Loaded += (_, _) => TheApp.Projects.Changed += Build;
        Unloaded += (_, _) => TheApp.Projects.Changed -= Build;
    }

    void Build()
    {
        _root.Children.Clear();
        var done = TheApp.Projects.Completed().ToList();
        if (done.Count == 0)
        {
            _root.Children.Add(Ui.Body("还没有已完成的项目。", muted: true));
            _root.Children.Add(Ui.Caption("在项目上右键 →「标记为 已完成」,它就会进到这里。"));
            return;
        }
        _root.Children.Add(Ui.Caption("已完成的项目归档在这里。右键可在文件夹中打开,或捞回进行中/准备中。"));
        foreach (var p in done) _root.Children.Add(Row(p));
    }

    FrameworkElement Row(Project p)
    {
        var icon = Icons.Make(IconName.Tasks, 16, "FgMuted");
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 9, 0);

        var title = new TextBlock { Text = p.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1, 0, 1) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(title);

        var host = new Border { Child = row, Padding = new Thickness(8, 8, 8, 8), Background = Brushes.Transparent };
        host.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        host.MouseEnter += (_, _) => host.SetResourceReference(Border.BackgroundProperty, "BgHover");
        host.MouseLeave += (_, _) => host.Background = Brushes.Transparent;

        var m = new ContextMenu();
        var open = new MenuItem { Header = "在文件夹中打开" };
        open.Click += (_, _) => { if (!ProjectCenter.OpenInExplorer(p.FolderPath)) MessageBox.Show(string.IsNullOrWhiteSpace(p.FolderPath) ? "该项目没有设置文件夹。" : $"打不开文件夹:\n{p.FolderPath}", "本地 AI 中枢", MessageBoxButton.OK, MessageBoxImage.Information); };
        m.Items.Add(open);
        m.Items.Add(new Separator());
        var back = new MenuItem { Header = "捞回 · 标记为进行中" };
        back.Click += (_, _) => TheApp.Projects.SetStatus(p.ProjectId, ProjectStatus.Active);
        m.Items.Add(back);
        var prep = new MenuItem { Header = "捞回 · 标记为准备中" };
        prep.Click += (_, _) => TheApp.Projects.SetStatus(p.ProjectId, ProjectStatus.Preparing);
        m.Items.Add(prep);
        host.ContextMenu = m;
        return host;
    }
}
