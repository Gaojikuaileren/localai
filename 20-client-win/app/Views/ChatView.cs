// P3c -- 聊天工作空间。左:会话列表(普通会话 / 当前项目的会话);右:消息 + 输入。
//   顶部:当前上下文(普通会话 / 项目名)+「项目」按钮(开项目抽屉)+「新建会话」。
//
// 用户裁定:
//   · 选中项目后开的会话属于该项目;没选项目直接聊 = 普通会话。
//   · 会话可右键【移动到项目】;普通会话可【升级为项目】(选文件夹建项目并归入)。
//
// ★ 诚实:AI 未接入(P4)。发送只记录消息 + 一条系统说明,不伪造回复(见 ChatCenter)。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class ChatView : UserControl
{
    static App TheApp => (App)Application.Current;

    string? _projectId;      // 当前项目上下文;null = 普通会话
    string? _sessionId;      // 当前打开的会话

    readonly TextBlock _context = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
    readonly StackPanel _sessions = new();
    readonly StackPanel _messages = new();
    readonly ScrollViewer _msgScroll = new();
    readonly TextBox _input = new();

    public ChatView()
    {
        _context.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        // ---- 左:会话栏 ----
        var newBtn = Ui.PlusButton(NewSession, "新建会话");
        var projBtn = SmallButton("项目", () => (Application.Current.MainWindow as MainWindow)?.OpenProjectDrawer());

        var leftHead = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 0, 2, 8) };
        DockPanel.SetDock(newBtn, Dock.Right);
        DockPanel.SetDock(projBtn, Dock.Right);
        leftHead.Children.Add(newBtn);
        leftHead.Children.Add(projBtn);
        leftHead.Children.Add(_context);

        var left = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(leftHead, Dock.Top);
        left.Children.Add(leftHead);
        left.Children.Add(new ScrollViewer
        {
            Content = _sessions,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough());

        var leftCard = Ui.Card(left, new Thickness(0, 0, 0, 0));
        leftCard.Width = 250;

        // ---- 右:消息 + 输入 ----
        _msgScroll.Content = _messages;
        _msgScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _msgScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _msgScroll.PassThrough();

        _input.AcceptsReturn = false;
        _input.Padding = new Thickness(9, 8, 9, 8);
        _input.VerticalContentAlignment = VerticalAlignment.Center;
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; SendCurrent(); } };
        var send = Ui.Primary("发送", (_, _) => SendCurrent());
        send.Height = 38;
        var inputRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(send, Dock.Right);
        var sendWrap = new Border { Child = send, Margin = new Thickness(10, 0, 0, 0) };
        inputRow.Children.Add(sendWrap);
        inputRow.Children.Add(_input);

        var right = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(inputRow, Dock.Bottom);
        right.Children.Add(inputRow);
        right.Children.Add(_msgScroll);
        var rightCard = Ui.Card(right, new Thickness(0));

        var grid = new Grid { Margin = new Thickness(24, 18, 24, 18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(leftCard, 0);
        Grid.SetColumn(rightCard, 1);
        grid.Children.Add(leftCard);
        grid.Children.Add(rightCard);
        Content = grid;

        BuildSessions();
        BuildMessages();
        TheApp.Chat.Changed += OnChatChanged;
        TheApp.Projects.Changed += UpdateContext;
        Unloaded += (_, _) => { TheApp.Chat.Changed -= OnChatChanged; TheApp.Projects.Changed -= UpdateContext; };
    }

    void OnChatChanged() { BuildSessions(); BuildMessages(); }

    /// <summary>从项目抽屉/主页深链进来:切到该项目上下文。</summary>
    public void SelectProject(string projectId)
    {
        _projectId = projectId;
        _sessionId = TheApp.Chat.SessionsOf(projectId).FirstOrDefault()?.SessionId;
        BuildSessions();
        BuildMessages();
    }

    void UpdateContext()
    {
        if (_projectId is { } pid)
        {
            var name = TheApp.Projects.Find(pid)?.Title ?? "项目";
            _context.Text = "项目 · " + name;
        }
        else _context.Text = "普通会话";
    }

    // ---------------------------------------------------------------- 会话列表
    void BuildSessions()
    {
        UpdateContext();
        _sessions.Children.Clear();

        // 项目上下文有"回到普通会话"的入口
        if (_projectId is not null)
            _sessions.Children.Add(BackToNormalRow());

        var list = (_projectId is { } pid ? TheApp.Chat.SessionsOf(pid) : TheApp.Chat.NormalSessions()).ToList();
        if (list.Count == 0)
        {
            _sessions.Children.Add(Ui.Caption(_projectId is null ? "还没有会话。点 + 新建。" : "这个项目下还没有会话。点 + 新建。"));
            return;
        }
        foreach (var s in list) _sessions.Children.Add(SessionRow(s));
    }

    FrameworkElement BackToNormalRow()
    {
        var t = new TextBlock { Text = "‹ 回到普通会话", VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border { Child = t, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 4), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => { _projectId = null; _sessionId = TheApp.Chat.NormalSessions().FirstOrDefault()?.SessionId; BuildSessions(); BuildMessages(); };
        return b;
    }

    FrameworkElement SessionRow(ChatSession s)
    {
        var title = new TextBlock { Text = s.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        var time = new TextBlock { Text = s.LastActive.ToString("M月d日 HH:mm"), Margin = new Thickness(0, 1, 0, 0) };
        time.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        time.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var col = new StackPanel();
        col.Children.Add(title);
        col.Children.Add(time);

        var host = new Border { Child = col, Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand };
        host.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        host.Background = s.SessionId == _sessionId ? (Brush)FindResource("BgSelected") : Brushes.Transparent;
        host.MouseEnter += (_, _) => { if (s.SessionId != _sessionId) host.SetResourceReference(Border.BackgroundProperty, "BgHover"); };
        host.MouseLeave += (_, _) => { if (s.SessionId != _sessionId) host.Background = Brushes.Transparent; };
        host.MouseLeftButtonUp += (_, _) => { _sessionId = s.SessionId; BuildSessions(); BuildMessages(); };
        host.ContextMenu = SessionMenu(s);
        return host;
    }

    ContextMenu SessionMenu(ChatSession s)
    {
        var m = new ContextMenu();
        var move = new MenuItem { Header = "移动到项目" };
        foreach (var p in TheApp.Projects.Items.Where(x => x.Status != ProjectStatus.Done))
        {
            var mi = new MenuItem { Header = p.Title, IsChecked = s.ProjectId == p.ProjectId };
            var pid = p.ProjectId;
            mi.Click += (_, _) => { TheApp.Chat.MoveToProject(s.SessionId, pid); };
            move.Items.Add(mi);
        }
        if (move.Items.Count == 0) move.Items.Add(new MenuItem { Header = "(还没有项目)", IsEnabled = false });
        m.Items.Add(move);

        if (s.ProjectId is null)
        {
            var up = new MenuItem { Header = "升级为项目…" };
            up.Click += (_, _) => UpgradeToProject(s);
            m.Items.Add(up);
        }
        else
        {
            var det = new MenuItem { Header = "移出项目(变回普通会话)" };
            det.Click += (_, _) => TheApp.Chat.MoveToProject(s.SessionId, null);
            m.Items.Add(det);
        }
        return m;
    }

    void UpgradeToProject(ChatSession s)
    {
        var folder = ProjectUi.PickFolder("为新项目选择文件夹");
        if (folder is null) return;
        var p = TheApp.Projects.Create(string.IsNullOrWhiteSpace(s.Title) ? "新项目" : s.Title, folder, null, s.Scope);
        TheApp.Chat.MoveToProject(s.SessionId, p.ProjectId);
        SelectProject(p.ProjectId);
        _sessionId = s.SessionId;
        BuildSessions();
        BuildMessages();
    }

    // ---------------------------------------------------------------- 消息区
    void BuildMessages()
    {
        _messages.Children.Clear();
        if (_sessionId is null)
        {
            _messages.Children.Add(Ui.Body("新建会话开始聊天。", muted: true));
            _messages.Children.Add(Ui.Caption("没选项目直接聊 = 普通会话;选了项目再聊,会归到该项目下。"));
            return;
        }
        foreach (var msg in TheApp.Chat.MessagesOf(_sessionId)) _messages.Children.Add(Bubble(msg));
        // 滚到底
        Dispatcher.BeginInvoke(new Action(() => _msgScroll.ScrollToEnd()), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    FrameworkElement Bubble(ChatMessage m)
    {
        if (m.Role == ChatRole.System)
        {
            var sys = Ui.Caption(m.Text);
            sys.HorizontalAlignment = HorizontalAlignment.Center;
            sys.Margin = new Thickness(0, 6, 0, 6);
            sys.TextAlignment = TextAlignment.Center;
            return sys;
        }
        var user = m.Role == ChatRole.User;
        var tb = new TextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap };
        tb.SetResourceReference(TextBlock.ForegroundProperty, user ? "FgOnAccent" : "FgPrimary");
        var bubble = new Border
        {
            Child = tb, Padding = new Thickness(11, 8, 11, 8), MaxWidth = 520,
            Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        bubble.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        bubble.SetResourceReference(Border.BackgroundProperty, user ? "Accent" : "BgSunken");
        return bubble;
    }

    // ---------------------------------------------------------------- 动作
    void NewSession()
    {
        var s = TheApp.Chat.NewSession(_projectId, _projectId is null ? ProjectScope.Personal : (TheApp.Projects.Find(_projectId!)?.Scope ?? ProjectScope.Personal));
        _sessionId = s.SessionId;
        BuildSessions();
        BuildMessages();
        _input.Focus();
    }

    void SendCurrent()
    {
        var text = _input.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_sessionId is null) NewSession();   // 没会话就先建一个(按当前项目上下文)
        if (_sessionId is not null && TheApp.Chat.Send(_sessionId, text)) _input.Clear();
    }

    static FrameworkElement SmallButton(string text, Action onClick)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border { Child = t, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.BorderThickness = new Thickness(1);
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => onClick();
        return b;
    }
}
