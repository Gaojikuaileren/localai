// P3c -- 项目抽屉(右侧)。列出进行中 / 准备中的项目;可新建(选文件夹 + 可选附件夹)、改状态、
//   点项目进入其项目聊天、右键在 Explorer 打开、以及打开"项目库"看已完成。
//   对应主页的项目板块(同一份 ProjectCenter 数据)。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class ProjectDrawerView : UserControl
{
    static App TheApp => (App)Application.Current;
    readonly StackPanel _root = new();
    bool _showCreate;

    public ProjectDrawerView()
    {
        Content = new ScrollViewer
        {
            Content = _root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough();
        _root.Margin = new Thickness(4, 0, 4, 12);

        Build();
        Loaded += (_, _) => TheApp.Projects.Changed += Build;
        Unloaded += (_, _) => TheApp.Projects.Changed -= Build;
    }

    void Build()
    {
        _root.Children.Clear();

        // 顶部动作:项目库(已完成)+ 新建
        var lib = LinkButton("项目库 ›", () => (Application.Current.MainWindow as MainWindow)?.OpenProjectLibrary());
        var add = Ui.PlusButton(() => { _showCreate = !_showCreate; Build(); }, "新建项目");
        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(6, 2, 2, 8) };
        DockPanel.SetDock(add, Dock.Right);
        DockPanel.SetDock(lib, Dock.Right);
        head.Children.Add(add);
        head.Children.Add(lib);
        _root.Children.Add(head);

        if (_showCreate) _root.Children.Add(CreateForm());

        AddGroup("进行中", TheApp.Projects.Items.Where(p => p.Status == ProjectStatus.Active));
        AddGroup("准备中", TheApp.Projects.Items.Where(p => p.Status == ProjectStatus.Preparing));

        if (!TheApp.Projects.Items.Any(p => p.Status != ProjectStatus.Done))
            _root.Children.Add(Ui.Caption("还没有进行中的项目。点右上角 + 新建,或在普通会话里让 AI 建。"));
    }

    void AddGroup(string title, IEnumerable<Project> items)
    {
        var list = items.OrderByDescending(p => p.Pinned).ThenByDescending(p => p.LastOpened).ToList();
        if (list.Count == 0) return;
        var t = new TextBlock { Text = title, Margin = new Thickness(6, 10, 0, 4), FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        _root.Children.Add(t);
        foreach (var p in list) _root.Children.Add(ProjectRow(p));
    }

    FrameworkElement ProjectRow(Project p)
    {
        var icon = Icons.Make(IconName.Tasks, 16, "FgSecondary");
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 9, 0);

        var title = new TextBlock { Text = p.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        var n = TheApp.Chat.SessionsOf(p.ProjectId).Count();
        var sub = new TextBlock { Text = n > 0 ? $"{n} 个会话" : "暂无会话", VerticalAlignment = VerticalAlignment.Center };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        sub.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var line1 = new StackPanel { Orientation = Orientation.Horizontal };
        line1.Children.Add(title);
        textCol.Children.Add(line1);
        textCol.Children.Add(sub);

        var chip = ProjectUi.StatusChip(p.Status);
        chip.VerticalAlignment = VerticalAlignment.Center;

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1, 0, 1) };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(chip, Dock.Right);
        row.Children.Add(icon);
        row.Children.Add(chip);
        row.Children.Add(textCol);

        var host = new Border
        {
            Child = row, Padding = new Thickness(8, 8, 8, 8), Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent,
        };
        host.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        host.MouseEnter += (_, _) => host.SetResourceReference(Border.BackgroundProperty, "BgHover");
        host.MouseLeave += (_, _) => host.Background = Brushes.Transparent;
        host.MouseLeftButtonUp += (_, _) => (Application.Current.MainWindow as MainWindow)?.OpenProjectInChat(p.ProjectId);
        host.ContextMenu = ProjectMenu(p);
        return host;
    }

    ContextMenu ProjectMenu(Project p)
    {
        var m = new ContextMenu();
        var open = new MenuItem { Header = "在文件夹中打开" };
        open.Click += (_, _) =>
        {
            if (!ProjectCenter.OpenInExplorer(p.FolderPath))
                MessageBox.Show(string.IsNullOrWhiteSpace(p.FolderPath) ? "该项目还没有设置文件夹。" : $"打不开文件夹:\n{p.FolderPath}",
                    "本地 AI 中枢", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        m.Items.Add(open);
        m.Items.Add(new Separator());
        foreach (var st in new[] { ProjectStatus.Preparing, ProjectStatus.Active, ProjectStatus.Done })
        {
            var (label, _) = ProjectUi.Status(st);
            var mi = new MenuItem { Header = "标记为 " + label, IsChecked = p.Status == st };
            var captured = st;
            mi.Click += (_, _) => TheApp.Projects.SetStatus(p.ProjectId, captured);
            m.Items.Add(mi);
        }
        m.Items.Add(new Separator());
        var ai = new MenuItem { Header = "AI 权限" };
        foreach (var perm in new[] { AiPermission.ReadOnly, AiPermission.Ask, AiPermission.Edit })
        {
            var mi = new MenuItem { Header = ProjectUi.AiLabel(perm), IsChecked = p.Ai == perm, ToolTip = ProjectUi.AiHint(perm) };
            var captured = perm;
            mi.Click += (_, _) => TheApp.Projects.SetAiPermission(p.ProjectId, captured);
            ai.Items.Add(mi);
        }
        m.Items.Add(ai);
        return m;
    }

    // 新建项目表单(内联,+ 展开)
    FrameworkElement CreateForm()
    {
        var name = new TextBox { Margin = new Thickness(0, 2, 0, 8), Padding = new Thickness(8, 5, 8, 5) };

        string? folder = null, attach = null;
        var folderLabel = FieldLabel("未选择(必填)");
        var attachLabel = FieldLabel("未选择(可选)");

        var aiPerm = new ComboBox { Margin = new Thickness(0, 2, 0, 4) };
        foreach (var perm in new[] { AiPermission.ReadOnly, AiPermission.Ask, AiPermission.Edit }) aiPerm.Items.Add(ProjectUi.AiLabel(perm));
        aiPerm.SelectedIndex = 1;   // 默认"需批准"
        var aiHint = FieldLabel(ProjectUi.AiHint(AiPermission.Ask));
        aiPerm.SelectionChanged += (_, _) => aiHint.Text = ProjectUi.AiHint((AiPermission)Math.Max(0, aiPerm.SelectedIndex));

        var pickFolder = Ui.Secondary("选择文件夹", (_, _) =>
        {
            var f = ProjectUi.PickFolder("选择项目的文件夹");
            if (f is not null) { folder = f; folderLabel.Text = f; }
        });
        var pickAttach = Ui.Secondary("选择附件文件夹", (_, _) =>
        {
            var f = ProjectUi.PickFolder("选择附件文件夹(可选)");
            if (f is not null) { attach = f; attachLabel.Text = f; }
        });

        var create = Ui.Primary("创建项目", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { name.Focus(); return; }
            if (folder is null) { MessageBox.Show("请先为项目选择一个文件夹。", "本地 AI 中枢", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var p = TheApp.Projects.Create(name.Text.Trim(), folder, attach, ProjectScope.Personal);
            TheApp.Projects.SetAiPermission(p.ProjectId, (AiPermission)Math.Max(0, aiPerm.SelectedIndex));
            _showCreate = false;
            Build();
            (Application.Current.MainWindow as MainWindow)?.OpenProjectInChat(p.ProjectId);
        });

        var card = Ui.Panel("新建项目",
            Ui.Stack(
                Ui.Caption("项目名"), name,
                Ui.Caption("项目文件夹(必选)"), pickFolder, folderLabel,
                new Border { Height = 6 },
                Ui.Caption("附件文件夹(可选)"), pickAttach, attachLabel,
                new Border { Height = 6 },
                Ui.Caption("AI 权限"), aiPerm, aiHint,
                new Border { Height = 8 },
                create,
                new Border { Height = 4 },
                Ui.Caption("建好后,这个项目下的会话会归到它名下;也可以把普通会话移动过来。")),
            IconName.Tasks, new Thickness(0, 0, 0, 10), compact: true);
        return card;
    }

    static TextBlock FieldLabel(string text)
    {
        var t = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        return t;
    }

    static FrameworkElement LinkButton(string text, Action onClick)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border { Child = t, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => onClick();
        return b;
    }
}
