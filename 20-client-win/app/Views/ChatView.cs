// P3c -- 聊天工作空间。用户裁定的形态:
//   · 会话列表【常驻右侧】(普通会话 或 当前项目的项目会话);
//   · 会话列表左缘、竖直居中有个【‹ 箭头】,点开【项目选择器抽屉】(田字文件夹);
//     选中项目后右侧会话列表变成该项目的会话;点抽屉外关闭,保留当前列表;
//   · 会话列表顶部:【+ 新建会话】与【返回普通会话】;
//   · 刚打开、还没有任何消息时,输入框【竖直居中】(像 GPT);开聊后输入框移到底部。
//
// ★ 诚实:AI 未接入(P4)。发送只记录消息 + 系统说明,不伪造回复(见 ChatCenter)。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class ChatView : UserControl
{
    static App TheApp => (App)Application.Current;

    readonly string _wsKey;  // 本视图所属工作空间(chat / translation / …)。会话与项目按空间隔离。
    string? _projectId;      // 当前项目上下文;null = 普通会话
    string? _sessionId;      // 当前打开的会话

    readonly TextBlock _ctxTitle = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly StackPanel _backBtnHost = new() { Orientation = Orientation.Horizontal };
    readonly ContentControl _ghostHost = new();   // 幽灵按钮:仅普通会话显示,且随幽灵状态换实线/虚线
    readonly ContentControl _newBtnHost = new();  // 新建会话按钮:只读项目(已删/已完成)下隐藏
    readonly DockPanel _actionsRow = new() { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
    bool _trashOpen;      // 已删除会话【覆盖板块】开着(覆盖普通会话列表,可返回)
    bool _wasEmptyState;  // 上一次会话区是"空态居中输入框"—— 用于居中→底部的滑动动画
    readonly Dictionary<string, int> _seenMsgCount = new();   // 会话 -> 已经出现过的消息条数(只给新增的播动画)
    readonly HashSet<string> _expandedBubbles = new();        // 被用户展开的超长消息(会话#序号)
    readonly StackPanel _sessions = new();
    readonly ContentControl _conv = new();   // 会话区(空态居中 / 有消息则底部输入)
    TextBox _input = new();
    readonly List<ChatAttachment> _pending = new();   // 待发送的附件引用(路径/剪贴板)
    string _draft = "";                                // 跨重建保留正在输入的文字
    readonly Dictionary<string, FrameworkElement> _sessionRows = new();   // id -> 行,供"震荡提醒"定位
    TextBlock? _trashLabel;   // 底部"已删除 (N)"入口的计数

    public ChatView(string workspaceKey = "chat")
    {
        _wsKey = workspaceKey;
        _ctxTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        _ctxTitle.FontWeight = FontWeights.SemiBold;

        // ---- 右:会话列表(常驻)。★ 标题独占上面一行(可换行显示两排,长项目名不再被截断);
        //      交互按钮(返回普通 / 幽灵 / 新建)放在标题【下面】一行(用户裁定)。
        _ctxTitle.TextWrapping = TextWrapping.Wrap;
        _ctxTitle.MaxHeight = 42;                      // 约两行
        _ctxTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _ctxTitle.Margin = new Thickness(2, 0, 2, 6);

        _newBtnHost.Content = Ui.PlusButton(NewSession, "新建会话");
        DockPanel.SetDock(_newBtnHost, Dock.Right);
        _actionsRow.Children.Add(_newBtnHost);
        DockPanel.SetDock(_ghostHost, Dock.Right);
        _actionsRow.Children.Add(_ghostHost);
        DockPanel.SetDock(_backBtnHost, Dock.Left);
        _actionsRow.Children.Add(_backBtnHost);

        var head = new StackPanel();
        head.Children.Add(_ctxTitle);
        head.Children.Add(_actionsRow);

        var sessDock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top);
        sessDock.Children.Add(head);
        var trash = TrashLink();
        DockPanel.SetDock(trash, Dock.Bottom);
        sessDock.Children.Add(trash);
        sessDock.Children.Add(new ScrollViewer
        {
            Content = _sessions,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough());
        var sessCard = Ui.Card(sessDock, new Thickness(10, 0, 0, 0));
        sessCard.Padding = new Thickness(10);   // 比默认 16 紧凑
        sessCard.Width = 208;                    // 收窄,别浪费右侧空间(用户反馈)

        // ---- 左缘箭头:拉开项目选择器 ----
        var arrow = ArrowButton();

        // ---- 布局:会话区 | 会话列表 | 箭头(在最右) ----
        //   用户裁定:项目列表按钮放在会话列表【右侧】,不夹在对话与会话之间。
        var grid = new Grid { Margin = new Thickness(14, 12, 14, 14) };   // 收紧:内嵌板块与边距别太大(用户反馈)
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_conv, 0);
        Grid.SetColumn(sessCard, 1);
        Grid.SetColumn(arrow, 2);
        grid.Children.Add(_conv);
        grid.Children.Add(sessCard);
        grid.Children.Add(arrow);
        Content = grid;

        BuildSessions();
        BuildConversation();
        TheApp.Chat.Changed += OnChatChanged;
        TheApp.Projects.Changed += UpdateContext;
        Unloaded += (_, _) => { TheApp.Chat.Changed -= OnChatChanged; TheApp.Projects.Changed -= UpdateContext; TheApp.Chat.PurgeGhosts(); };
    }

    void OnChatChanged() { BuildSessions(); BuildConversation(); }

    // 底部"已删除 (N)"入口
    FrameworkElement TrashLink()
    {
        _trashLabel = new TextBlock { Text = "已删除", VerticalAlignment = VerticalAlignment.Center };
        _trashLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _trashLabel.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border { Child = _trashLabel, Padding = new Thickness(8, 6, 8, 4), Margin = new Thickness(0, 4, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0, 1, 0, 0) };
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.MouseEnter += (_, _) => _trashLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        b.MouseLeave += (_, _) => _trashLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        b.MouseLeftButtonUp += (_, _) => OpenTrash();
        return b;
    }

    // ★ 用户裁定:已删除会话不再用浮窗,而是【覆盖普通会话列表的板块】——删多了也能舒服滚动浏览;
    //   顶部可返回普通会话列表。
    void OpenTrash() { _trashOpen = true; BuildSessions(); }
    void CloseTrash() { _trashOpen = false; BuildSessions(); }

    void BuildTrashBoard()
    {
        _actionsRow.Visibility = Visibility.Collapsed;   // 隐藏 新建/幽灵/返回项目
        _ctxTitle.Text = "已删除会话";
        _ctxTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        _ctxTitle.TextWrapping = TextWrapping.NoWrap;
        RefreshTrashCount();
        _sessions.Children.Clear();
        _sessionRows.Clear();

        _sessions.Children.Add(Chip("‹ 返回会话", "FgSecondary", CloseTrash));
        var items = TheApp.Chat.Deleted(_wsKey).ToList();
        if (items.Count == 0)
        {
            _sessions.Children.Add(Ui.Caption("没有已删除的会话。"));
            return;
        }
        _sessions.Children.Add(Ui.Caption($"保留 {ChatCenter.TrashRetentionDays} 天,过期自动清除(不可恢复)。"));
        foreach (var s in items) _sessions.Children.Add(TrashRow(s, BuildSessions));
        var clear = Ui.DangerFilled("全部清除", (_, _) => TheApp.Chat.ClearDeleted(_wsKey));
        clear.Height = 28;
        clear.Margin = new Thickness(0, 8, 0, 0);
        clear.HorizontalAlignment = HorizontalAlignment.Left;
        _sessions.Children.Add(clear);
    }

    FrameworkElement TrashRow(ChatSession s, Action refresh)
    {
        var title = new TextBlock { Text = s.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        var when = new TextBlock { Text = "删除于 " + (s.DeletedAt?.ToString("M月d日 HH:mm") ?? ""), Margin = new Thickness(0, 1, 0, 0) };
        when.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        when.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        col.Children.Add(title);
        col.Children.Add(when);

        var restore = Chip("恢复", "FgSecondary", () => { TheApp.Chat.Restore(s.SessionId); refresh(); });
        var purge = Chip("彻底删除", "RiskDanger", () => { TheApp.Chat.PurgeDeleted(s.SessionId); refresh(); });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(restore);
        actions.Children.Add(purge);

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 4, 0, 4) };
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        row.Children.Add(col);
        return row;
    }

    static FrameworkElement Chip(string text, string colorKey, Action onClick)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border { Child = t, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.BorderThickness = new Thickness(1);
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    public void SelectProject(string projectId)
    {
        _trashOpen = false;
        _projectId = projectId;
        // 已删除项目的会话都软删了,用 AllSessionsOf 才选得到(只读浏览);其余用 SessionsOf。
        var proj = TheApp.Projects.Find(projectId);
        _sessionId = (proj?.DeletedAt is not null ? TheApp.Chat.AllSessionsOf(projectId) : TheApp.Chat.SessionsOf(projectId))
            .FirstOrDefault()?.SessionId;
        TheApp.Chat.PurgeGhosts();
        BuildSessions();
        BuildConversation();
    }

    /// <summary>
    /// 回到普通会话。★ 用户裁定:从项目/幽灵/垃圾篓等任何地方回来,都落在【空会话】
    /// (输入框居中的新会话态),而不是自动跳进排第一的那条旧对话 —— 那样很突兀,
    /// 像是替用户决定"你接着聊这个"。想继续旧会话,右侧列表点一下即可。
    /// </summary>
    void ToNormal()
    {
        _trashOpen = false;
        _projectId = null;
        _sessionId = null;      // 不选中任何会话 = 空态
        TheApp.Chat.PurgeGhosts();
        BuildSessions();
        BuildConversation();
    }

    /// <summary>当前是否处在幽灵会话里。</summary>
    bool InGhost => _sessionId is { } sid && TheApp.Chat.Find(sid)?.Ghost == true;

    void UpdateContext()
    {
        var inProject = _projectId is not null;
        _ctxTitle.TextWrapping = TextWrapping.Wrap;   // 复位(垃圾篓板块曾设 NoWrap)
        _ctxTitle.Text = inProject ? "项目 · " + (TheApp.Projects.Find(_projectId!)?.Title ?? "项目") : "普通会话";
        // ★ 项目会话用【着重色】区分;普通会话用常规前景色(用户裁定)。
        _ctxTitle.SetResourceReference(TextBlock.ForegroundProperty, inProject ? "Accent" : "FgPrimary");
        _backBtnHost.Children.Clear();
        if (inProject) _backBtnHost.Children.Add(BackChip());

        // 幽灵按钮:★ 只在【普通会话】上下文显示(项目会话里不给);图标在幽灵中转【实线】,退出后回【虚线】。
        _ghostHost.Content = (_wsKey == "chat" && !inProject) ? GhostButton(InGhost) : null;
        // 只读项目(已删除 / 已完成)下不给"新建会话"(不能往里加会话)。
        _newBtnHost.Visibility = ReadOnly ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------------------------------------------------------- 会话列表
    void BuildSessions()
    {
        if (_trashOpen) { BuildTrashBoard(); return; }
        _actionsRow.Visibility = Visibility.Visible;
        UpdateContext();
        RefreshTrashCount();
        _sessions.Children.Clear();
        _sessionRows.Clear();
        // 已删除项目:它的会话都随项目软删了,要用 AllSessionsOf 才看得到(只读浏览)。
        var list = (_projectId is { } pid
            ? (ViewingDeletedProject ? TheApp.Chat.AllSessionsOf(pid) : TheApp.Chat.SessionsOf(pid))
            : TheApp.Chat.NormalSessions(_wsKey)).ToList();
        if (list.Count == 0)
        {
            _sessions.Children.Add(Ui.Caption(_projectId is null ? "还没有会话。点 + 新建。"
                : ReadOnly ? "这个项目下没有会话。" : "这个项目下还没有会话。点 + 新建。"));
            return;
        }
        foreach (var s in list) { var row = SessionRow(s); _sessionRows[s.SessionId] = row; _sessions.Children.Add(row); }
    }

    /// <summary>刷新底部"已删除 (N)"的计数(顺带清掉过期项)。</summary>
    void RefreshTrashCount()
    {
        if (_trashLabel is null) return;
        var n = TheApp.Chat.DeletedCount(_wsKey);
        _trashLabel.Text = n > 0 ? $"已删除 ({n})" : "已删除";
    }

    FrameworkElement SessionRow(ChatSession s)
    {
        var selected = s.SessionId == _sessionId;

        var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (s.Pinned)
        {
            var pinDot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            pinDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, selected ? "FgOnSelected" : "Accent");
            titleRow.Children.Add(pinDot);
        }
        var title = new TextBlock { Text = s.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        // ★ 选中态字色跟着底色走(墨白的 BgSelected 近黑,用 FgOnSelected 才不会黑底黑字)
        title.SetResourceReference(TextBlock.ForegroundProperty, selected ? "FgOnSelected" : "FgPrimary");
        titleRow.Children.Add(title);
        // ★ 共享标记:默认会话只在本机,已提升为共享的要一眼看出来(删它会影响所有设备)
        var meta = s.Shared ? s.LastActive.ToString("M月d日 HH:mm") + " · 共享" : s.LastActive.ToString("M月d日 HH:mm");
        var time = new TextBlock { Text = meta, Margin = new Thickness(0, 1, 0, 0) };
        time.SetResourceReference(TextBlock.ForegroundProperty, selected ? "FgOnSelected" : s.Shared ? "Accent" : "FgMuted");
        time.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        textCol.Children.Add(titleRow);
        textCol.Children.Add(time);

        // 三个点(左键拉菜单),取代右键
        var dots = SessionDots(s);
        dots.Opacity = selected ? 1 : 0;   // 平时隐藏,hover/选中显示

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(dots, Dock.Right);
        row.Children.Add(dots);
        row.Children.Add(textCol);

        var host = new Border { Child = row, Padding = new Thickness(9, 6, 4, 6), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand };
        host.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        host.Background = selected ? (Brush)FindResource("BgSelected") : Brushes.Transparent;
        host.MouseEnter += (_, _) => { if (!selected) host.SetResourceReference(Border.BackgroundProperty, "BgHover"); dots.Opacity = 1; };
        host.MouseLeave += (_, _) => { if (!selected) host.Background = Brushes.Transparent; if (!selected) dots.Opacity = 0; };
        host.MouseLeftButtonUp += (_, _) => { _sessionId = s.SessionId; TheApp.Chat.PurgeGhosts(); BuildSessions(); BuildConversation(); };
        return host;
    }

    FrameworkElement SessionDots(ChatSession s)
    {
        var d = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int k = 0; k < 3; k++)
        {
            var e = new System.Windows.Shapes.Ellipse { Width = 3, Height = 3, Margin = new Thickness(1.3, 0, 1.3, 0) };
            e.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, s.SessionId == _sessionId ? "FgOnSelected" : "FgSecondary");
            d.Children.Add(e);
        }
        var b = new Border { Child = d, Width = 26, Height = 26, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; MenuHost.Show(BuildSessionMenu(s, b), b); };
        return b;
    }

    ContextMenu BuildSessionMenu(ChatSession s, FrameworkElement anchor)
    {
        var m = new ContextMenu();

        var move = new MenuItem { Header = "移动到项目" };
        foreach (var p in TheApp.Projects.Ongoing(_wsKey))   // 只列本工作空间的项目
        {
            var mi = new MenuItem { Header = p.Title, IsChecked = s.ProjectId == p.ProjectId };
            var pid = p.ProjectId;
            mi.Click += (_, _) => TheApp.Chat.MoveToProject(s.SessionId, pid);
            move.Items.Add(mi);
        }
        if (move.Items.Count > 0) move.Items.Add(new Separator());
        var newP = new MenuItem { Header = "新建项目…(并移入)" };
        newP.Click += (_, _) => MoveToNewProject(s);
        move.Items.Add(newP);
        if (s.ProjectId is not null)
        {
            var det = new MenuItem { Header = "移出项目(变回普通会话)" };
            det.Click += (_, _) => TheApp.Chat.MoveToProject(s.SessionId, null);
            move.Items.Add(det);
        }
        m.Items.Add(move);

        // 发送到其它工作空间(不含当前)
        var toWs = new MenuItem { Header = "发送到工作空间" };
        foreach (var w in Workspaces.All)
        {
            if (w.Key == _wsKey) continue;
            var mi = new MenuItem { Header = I18n.Strings.Get(w.TitleKey) };
            var key = w.Key;
            mi.Click += (_, _) => { if (_sessionId == s.SessionId) _sessionId = null; TheApp.Chat.MoveSessionToWorkspace(s.SessionId, key); };
            toWs.Items.Add(mi);
        }
        m.Items.Add(toWs);

        var rename = new MenuItem { Header = "重命名会话…" };
        rename.Click += (_, _) => RenameSession(s, anchor);
        m.Items.Add(rename);

        var pin = new MenuItem { Header = s.Pinned ? "取消置顶" : "置顶会话", IsChecked = s.Pinned };
        pin.Click += (_, _) => TheApp.Chat.TogglePin(s.SessionId);
        m.Items.Add(pin);

        // ★ 提升为共享(用户裁定):会话默认【只在本机】;提升后全家设备可见,且【不可收回】。
        //   已共享的不再给这一项(没有"取消共享"—— 单向)。幽灵会话永远不给。
        if (ChatCenter.CanShare(s))
        {
            var share = new MenuItem { Header = "提升为共享…" };
            share.Click += (_, _) => ConfirmShare(s);
            m.Items.Add(share);
        }

        m.Items.Add(new Separator());
        // 删除 = 软删除进"已删除"(30 天可恢复),不弹确认(用户裁定)
        // 删除:普通会话不弹确认(用户裁定)。★ 但【共享会话】任何机器都能删,而删除会影响所有设备 ——
        //   这是对外可见的动作,必须先问一句(不是"确认删除",而是"你要替所有人删")。
        var del = new MenuItem { Header = s.Shared ? "删除共享会话…" : "删除会话" };
        del.Click += (_, _) =>
        {
            if (s.Shared)
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var ok = ConfirmDialog.Show("删除共享会话",
                        $"删除共享会话「{s.Title}」?\n\n这是共享会话 —— 删除会对【家里所有设备】生效,不只是这台。\n\n" +
                        $"会先进「已删除」,{ChatCenter.TrashRetentionDays} 天内可恢复。",
                        confirmText: "删除", danger: true);
                    if (!ok) return;
                    if (_sessionId == s.SessionId) _sessionId = null;
                    TheApp.Chat.Delete(s.SessionId);
                }), System.Windows.Threading.DispatcherPriority.Background);
                return;
            }
            if (_sessionId == s.SessionId) _sessionId = null;
            TheApp.Chat.Delete(s.SessionId);
        };
        m.Items.Add(del);
        return m;
    }

    /// <summary>
    /// 提升会话为共享的二次确认。★ 三件事必须说清楚(用户裁定 A/B):
    ///   ① 整段对话(含全部历史消息)一起上去 —— 否则对方看半截读不懂;
    ///   ② 家里其他设备都能看到;
    ///   ③ 【不可收回】—— 提升之后没有撤销。
    /// ★ 诚实:中枢未接入(P4)前只是【标记】,真正上传要等接入,这句也得写明。
    /// </summary>
    void ConfirmShare(ChatSession s)
    {
        var n = TheApp.Chat.MessagesOf(s.SessionId).Count();
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            var ok = ConfirmDialog.Show("提升为共享",
                $"把会话「{s.Title}」提升为共享?\n\n" +
                $"· 整段对话({n} 条消息)会一起共享,家里其他设备都能看到\n" +
                "· ★ 提升之后【无法收回】\n\n" +
                "(中枢尚未接入,现在只做标记;接入后会上传到主机。)",
                confirmText: "提升为共享", danger: true);
            if (ok) TheApp.Chat.ShareSession(s.SessionId);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    void MoveToNewProject(ChatSession s)
    {
        var folder = ProjectUi.PickFolder("为新项目选择文件夹");
        if (folder is null) return;
        var p = TheApp.Projects.Create(string.IsNullOrWhiteSpace(s.Title) ? "新项目" : s.Title, folder, null, s.Scope, _wsKey);
        TheApp.Chat.MoveToProject(s.SessionId, p.ProjectId);
        SelectProject(p.ProjectId);
        _sessionId = s.SessionId;
        BuildSessions();
        BuildConversation();
    }

    void RenameSession(ChatSession s, FrameworkElement anchor)
    {
        var box = new TextBox { Text = s.Title, Padding = new Thickness(8, 6, 8, 6), Width = 220 };
        void Save() { var v = box.Text.Trim(); if (v.Length > 0) TheApp.Chat.Rename(s.SessionId, v); Overlay.CloseActive(); }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Save(); } };
        var save = Ui.Primary("保存", (_, _) => Save());
        var body = Ui.Stack(box, new Border { Height = 8 }, save);
        Flyout.Show(anchor, "重命名会话", body, width: 260);
        box.Focus();
        box.SelectAll();
    }

    // ---------------------------------------------------------------- 会话区(空态居中 / 有消息底部输入)
    // 当前项目上下文(可能是已删除/已完成 → 只读浏览)。
    Services.Project? CurrentProject => _projectId is null ? null : TheApp.Projects.Find(_projectId);
    bool ViewingDeletedProject => CurrentProject?.DeletedAt is not null;
    bool ViewingCompletedProject => CurrentProject is { Status: Services.ProjectStatus.Done, DeletedAt: null };
    bool ReadOnly => ViewingDeletedProject || ViewingCompletedProject;

    // 输入区(含附件按钮 + 待发附件预览 + 输入框 + 发送),空态与有消息态共用。
    // ★ 空态(输入框居中):附件放在输入框【下方】,不把居中的输入框往上顶(用户裁定);
    //   有消息态(输入框在底):附件仍在输入框【上方】。
    /// <summary>
    /// 翻译空间的「查翻译」按钮。★ 放大镜要【居中且不被裁】:按钮自带内边距会把图标挤出去,
    /// 所以内边距清零、内容对齐居中,再用一个固定尺寸的容器裹住图标。
    /// 抽成静态是为了让渲染诊断(--wheeltest)画的就是这一个,不是复刻件。
    /// </summary>
    internal static Button SearchSendButton(Action onClick)
    {
        var mag = Icons.Make(IconName.Search, 17, "FgOnAccent");
        mag.HorizontalAlignment = HorizontalAlignment.Center;
        mag.VerticalAlignment = VerticalAlignment.Center;
        var magBox = new Grid { Width = 22, Height = 22 };
        magBox.Children.Add(mag);
        var b = Ui.Primary("", (_, _) => onClick());
        b.Content = magBox;
        b.Padding = new Thickness(0);
        b.HorizontalContentAlignment = HorizontalAlignment.Center;
        b.VerticalContentAlignment = VerticalAlignment.Center;
        b.Width = 46;
        return b;
    }

    FrameworkElement BuildInputArea(bool attachmentsBelow = false, bool searchIcon = false)
    {
        // ★ 输入框(用户反馈的三条一起修):
        //   ① 能换行 —— Shift+Enter 换行,单独 Enter 才发送(聊天的通用约定);
        //   ② 文字多了【自己长高】,但最多 3 行,再多就在框内滚动(不能无限顶掉会话区);
        //   ③ 能粘贴 —— 图片进附件栏,文本正常贴(见 OnInputPaste)。
        _input = new TextBox
        {
            Text = _draft,
            Padding = new Thickness(11, 9, 11, 9),
            VerticalContentAlignment = VerticalAlignment.Center,
            AcceptsReturn = true,                       // ① 允许换行(否则 Enter 根本进不来)
            TextWrapping = TextWrapping.Wrap,
            MaxLines = InputMaxLines,                   // ② 最多 3 行高
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,   // 超过就滚动
        };
        _input.TextChanged += (_, _) => _draft = _input.Text;
        _input.PreviewKeyDown += (_, e) =>
        {
            // ★ Ctrl+V 必须在【按键层】处理,不能只靠 DataObject.Pasting:
            //   剪贴板里【只有图片没有文本】时,TextBox 认为自己消费不了这个格式,
            //   于是 Paste 命令根本不可执行 —— 粘贴处理器压根不会被调用,
            //   表现就是"截图粘不进去"(用户实测反馈)。这里自己判、自己收。
            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                if (TryPasteAttachment()) { e.Handled = true; return; }
                return;   // 普通文本粘贴照常走 TextBox 自己的处理
            }
            if (e.Key != Key.Enter) return;
            // Shift/Ctrl + Enter = 换行;单独 Enter = 发送
            if ((Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0) return;
            e.Handled = true;
            SendCurrent();
        };
        // ★ 在输入框里直接【粘贴截图】(Ctrl+V):剪贴板是图片就收进附件栏,而不是当文本贴进去。
        //   用户裁定:去掉"+"菜单里的剪贴板项,改成这里粘贴。文本粘贴不受影响(仅在剪贴板是图片时拦截)。
        DataObject.AddPastingHandler(_input, OnInputPaste);
        // 翻译空间的发送 = 放大镜(是"查翻译"不是"发消息",用户裁定)
        Button send;

        if (searchIcon) send = SearchSendButton(SendCurrent);
        else send = Ui.Primary("发送", (_, _) => SendCurrent());
        send.Height = 40;
        // ★ 翻译空间:目标池空 = 不知道要翻成什么,发送禁用(用户裁定)
        if (searchIcon && TheApp.Translation.Targets.Count == 0)
        {
            send.IsEnabled = false;
            send.Opacity = 0.45;
        }
        var attach = AttachButton();

        var inputRow = new DockPanel { LastChildFill = true };
        // 输入框会随文字长高,两侧按钮【贴底】才不会被拉伸或错位
        send.VerticalAlignment = VerticalAlignment.Bottom;
        attach.VerticalAlignment = VerticalAlignment.Bottom;
        var sendWrap = new Border { Child = send, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        DockPanel.SetDock(sendWrap, Dock.Right);
        DockPanel.SetDock(attach, Dock.Left);
        inputRow.Children.Add(sendWrap);
        inputRow.Children.Add(attach);
        inputRow.Children.Add(_input);

        var area = new StackPanel();

        // ★ 会话太长的提醒:超过设置里的【整理阈值】就建议另开新会话。
        //   用户裁定:整理由 AI 做(未接入),但"这条会话该拆了"这件事【现在就能判断】,
        //   否则设置里那个阈值就是个不做事的摆设。开新会话是用户自己点,我们不替他动数据。
        var limit = TheApp.Settings.SummaryThresholdChars;
        if (limit > 0 && _sessionId is { } sid2 && TheApp.Chat.SizeOf(sid2) > limit)
        {
            var big = Ui.Caption($"这条会话已经很长(约 {TheApp.Chat.SizeOf(sid2):N0} 字)—— 建议点右上角 + 另开一条,聊起来更准也更快。");
            big.SetResourceReference(TextBlock.ForegroundProperty, "RiskWarning");
            big.Margin = new Thickness(2, 0, 2, 6);
            area.Children.Add(big);
        }

        // ★ 主机没开时如实说明(用户提问):本机会话照常可读可写,只是【AI 算不了】——
        //   AI 在主机上跑。发出去的消息会先记在本机,主机上线后才可能有回答。
        //   只在【已配对但连不上】时提示;没配对的情况左下角状态块已经常驻显示,不重复唠叨。
        if (TheApp.Hub.IsPaired && TheApp.Hub.State != HubState.Online)
        {
            var off = Ui.Caption("主机未开启 —— 消息会先记在本机;AI 在主机上运行,要等它上线才能回答。");
            off.SetResourceReference(TextBlock.ForegroundProperty, "RiskWarning");
            off.Margin = new Thickness(2, 0, 2, 6);
            area.Children.Add(off);
        }
        if (attachmentsBelow)
        {
            area.Children.Add(inputRow);
            if (_pending.Count > 0) { var s = PendingStrip(); s.Margin = new Thickness(0, 8, 0, 0); area.Children.Add(s); }
        }
        else
        {
            if (_pending.Count > 0) area.Children.Add(PendingStrip());
            area.Children.Add(inputRow);
        }
        return area;
    }

    void BuildConversation()
    {
        // ★ 翻译工作空间(用户裁定的排版):上方【主会话框】(输入框直接在底部,不居中;发送 = 放大镜),
        //   下方一排【程度竖条 / 目标池 / 语言池 / 学习笔记】。会话列表与项目抽屉外壳照旧。
        if (_wsKey == "translation") { _conv.Content = BuildTranslationLayout(); return; }
        // 其余工作空间:同样的会话/项目外壳,但中间是占位(功能待接入),不做假界面。
        if (_wsKey != "chat") { _conv.Content = PlaceholderCenter(); return; }

        // ★ 只读:选中的是【已删除项目】或【已完成项目】—— 只能浏览记录,输入区换成对应动作按钮。
        if (ReadOnly) { _conv.Content = BuildReadonlyProject(); return; }

        var isGhost = _sessionId is { } sid && TheApp.Chat.Find(sid)?.Ghost == true;
        var hasMsgs = _sessionId is not null && TheApp.Chat.MessagesOf(_sessionId).Any();
        FrameworkElement inner;
        if (!hasMsgs)
        {
            // 空态:输入框竖直居中(像 GPT)。附件放【下方】,幽灵提示【浮在顶部】——都不顶动居中框(用户裁定)。
            var inputArea = BuildInputArea(attachmentsBelow: true);
            var title = new TextBlock { Text = Greetings.ChatOpener(DateTime.Now), FontWeight = FontWeights.SemiBold, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center };
            title.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            var hint = Ui.Caption(_projectId is null
                ? "没选项目直接聊 = 普通会话。右侧箭头可选项目。"
                : "当前在项目「" + (TheApp.Projects.Find(_projectId!)?.Title ?? "") + "」下,新消息会归到该项目。");
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.TextAlignment = TextAlignment.Center;
            var box = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 640 };
            box.Children.Add(title);
            box.Children.Add(new Border { Height = 6 });
            box.Children.Add(hint);
            box.Children.Add(new Border { Height = 16 });
            inputArea.Width = 640;
            box.Children.Add(inputArea);
            inner = box;
        }
        else
        {
            var inputArea = BuildInputArea(attachmentsBelow: false);
            var msgs = new StackPanel();
            // ★ 新发出的消息要有出现动画(用户裁定):只给【这次新增的那几条】播,
            //   旧消息重建时不再重复动(否则每次刷新整屏乱跳)。
            // ★ 分层存储:更早的消息在温层(另存文件),平时不加载 —— 顶部给个"加载更早"入口。
            //   原文一直都在,只是不占内存/上下文(见 SessionArchive)。
            var older = TheApp.Chat.UnloadedArchivedCount(_sessionId!);
            if (older > 0)
            {
                var more = Chip($"↑ 加载更早的 {older} 条", "FgSecondary", () => TheApp.Chat.LoadArchived(_sessionId!));
                more.HorizontalAlignment = HorizontalAlignment.Center;
                more.Margin = new Thickness(0, 0, 0, 8);
                msgs.Children.Add(more);
            }

            var all = TheApp.Chat.MessagesOf(_sessionId!).ToList();
            var seenKey = _sessionId!;
            var seen = _seenMsgCount.TryGetValue(seenKey, out var n) ? n : all.Count;   // 首次进会话不animate
            for (int i = 0; i < all.Count; i++)
            {
                var bubble = Bubble(all[i], i);
                if (i >= seen) AnimateIn(bubble, delayMs: (i - seen) * 70);   // 用户消息 + 随后的系统说明依次浮现
                msgs.Children.Add(bubble);
            }
            _seenMsgCount[seenKey] = all.Count;
            var scroll = new ScrollViewer { Content = msgs, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            scroll.PassThrough();
            var dock = new DockPanel { LastChildFill = true };
            var inputWrap = new Border { Child = inputArea, Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(inputWrap, Dock.Bottom);
            dock.Children.Add(inputWrap);
            dock.Children.Add(scroll);
            inner = dock;

            // ★ 从"空会话居中输入框"变成"底部输入框"时给一段动画,而不是硬切(用户裁定):
            //   输入框从原来的居中位置【滑到底部】,消息区同时淡入。
            var slideFromCenter = _wasEmptyState;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                scroll.ScrollToEnd();
                if (!slideFromCenter) return;
                var h = _conv.ActualHeight;
                var startY = -Math.Max(0, (h - inputWrap.ActualHeight) / 2 - 10);   // 居中处相对底部的偏移
                if (startY >= -1) return;                                            // 高度还没算出来就别硬演
                // 慢一点、更丝滑:缓入缓出(EaseInOut),消息区稍晚淡入,避免"抢在输入框前面出现"
                var t = new TranslateTransform { Y = startY };
                inputWrap.RenderTransform = t;
                t.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(startY, 0, TimeSpan.FromMilliseconds(520))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
                scroll.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
                    { BeginTime = TimeSpan.FromMilliseconds(120), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        _wasEmptyState = !hasMsgs;
        _conv.Content = ConvShell(inner, isGhost, overlayBanner: !hasMsgs);
    }

    /// <summary>翻译空间:上方主会话框(板块)+ 下方语言池/程度/笔记一排。</summary>
    FrameworkElement BuildTranslationLayout()
    {
        var msgs = new StackPanel();
        var hasMsgs = _sessionId is not null && TheApp.Chat.MessagesOf(_sessionId).Any();
        if (hasMsgs)
        {
            var list = TheApp.Chat.MessagesOf(_sessionId!).ToList();
            for (int i = 0; i < list.Count; i++) msgs.Children.Add(Bubble(list[i], i));
        }
        else
        {
            // 空态也【不居中】—— 用户裁定:输入框始终在板块底部
            var hint = Ui.Body("输入要翻译的内容,按下放大镜。", muted: true);
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            var tip = Ui.Caption("翻成哪些语言由下面的【目标池】决定;详细程度由左边的竖条决定。");
            tip.HorizontalAlignment = HorizontalAlignment.Center;
            msgs.Children.Add(new Border { Height = 24 });
            msgs.Children.Add(hint);
            msgs.Children.Add(tip);
        }
        var scroll = new ScrollViewer { Content = msgs, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }.PassThrough();
        Dispatcher.BeginInvoke(new Action(() => scroll.ScrollToEnd()), System.Windows.Threading.DispatcherPriority.Loaded);

        var inputArea = BuildInputArea(attachmentsBelow: false, searchIcon: true);
        var convDock = new DockPanel { LastChildFill = true };
        var inputWrap = new Border { Child = inputArea, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(inputWrap, Dock.Bottom);
        convDock.Children.Add(inputWrap);
        convDock.Children.Add(scroll);

        var conv = Ui.Card(convDock, new Thickness(0));
        conv.Padding = new Thickness(12);

        var root = new DockPanel { LastChildFill = true };
        var bar = new TranslationBar { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(conv);
        return root;
    }

    // 只读浏览:已删除 / 已完成项目。灰化会话内容,底部换成对应的动作按钮。
    FrameworkElement BuildReadonlyProject()
    {
        var p = CurrentProject!;
        var deleted = ViewingDeletedProject;

        // 内容:选中会话则显示其记录(只读),否则提示从右侧选会话
        FrameworkElement body;
        if (_sessionId is not null && TheApp.Chat.MessagesOf(_sessionId).Any())
        {
            var msgs = new StackPanel();
            var ro = TheApp.Chat.MessagesOf(_sessionId!).ToList();
            for (int i = 0; i < ro.Count; i++) msgs.Children.Add(Bubble(ro[i], i));
            body = new ScrollViewer { Content = msgs, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }.PassThrough();
        }
        else
        {
            var t = Ui.Body("从右侧选择一个会话查看对话记录。", muted: true);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            body = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { t } };
        }
        body.Opacity = 0.7;   // 灰化:能浏览、不能编辑(用户裁定"变灰无法继续对话只可以浏览")

        var banner = Ui.Caption(deleted
            ? $"该项目在「已删除项目」中 · 仅供浏览({ProjectCenter.TrashRetentionDays} 天后自动清除)"
            : "该项目已完成 · 仅供浏览");
        banner.HorizontalAlignment = HorizontalAlignment.Center;
        banner.TextAlignment = TextAlignment.Center;
        banner.SetResourceReference(TextBlock.ForegroundProperty, deleted ? "RiskDanger" : "FgSecondary");

        var actions = deleted ? DeletedProjectActions(p) : CompletedProjectActions(p);

        var dock = new DockPanel { LastChildFill = true };
        var bWrap = new Border { Child = banner, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(bWrap, Dock.Top);
        var aWrap = new Border { Child = actions, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(aWrap, Dock.Bottom);
        dock.Children.Add(bWrap);
        dock.Children.Add(aWrap);
        dock.Children.Add(body);

        var card = new Border { Child = dock, Padding = new Thickness(12), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "BgSunken");   // 整块偏灰,提示只读
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        return card;
    }

    // 已删除项目:输入框换成【恢复此项目 / 彻底删除此项目】。
    FrameworkElement DeletedProjectActions(Services.Project p)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(Ui.Primary("恢复此项目", (_, _) =>
        {
            TheApp.Projects.RestoreProject(p.ProjectId);
            TheApp.Chat.RestoreProjectSessions(p.ProjectId);
            SelectProject(p.ProjectId);   // 恢复后即进行中,输入框恢复正常
        }));
        var purge = Ui.DangerFilled("彻底删除此项目", (_, _) =>
        {
            if (!ConfirmDialog.Show("彻底删除项目",
                    $"彻底删除「{p.Title}」及其所有会话?不可恢复。(仍不动磁盘上的文件夹)",
                    confirmText: "彻底删除", danger: true)) return;
            TheApp.Chat.PurgeProjectSessions(p.ProjectId);
            TheApp.Projects.PurgeProject(p.ProjectId);
            ToNormal();
        });
        purge.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(purge);
        return row;
    }

    // 已完成项目:输入框换成【继续此项目 / 开启此项目分支】。
    FrameworkElement CompletedProjectActions(Services.Project p)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(Ui.Primary("继续此项目", (_, _) =>
        {
            TheApp.Projects.SetStatus(p.ProjectId, Services.ProjectStatus.Active);   // 移回进行中
            SelectProject(p.ProjectId);   // 输入框恢复正常
        }));
        var branch = Ui.Secondary("开启此项目分支", (_, _) =>
        {
            var np = TheApp.Projects.Branch(p.ProjectId);   // 复制成【准备中】的新项目
            SelectProject(np.ProjectId);                    // 切到新项目,输入框恢复正常
        });
        branch.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(branch);
        return row;
    }

    // 折叠状态按【会话 + 序号】记 —— 消息本身没有 id,而重建时同一条的序号是稳定的
    string BubbleKey(ChatMessage m, int index) => $"{m.SessionId}#{index}";

    /// <summary>新消息浮现:从下方微微上移 + 淡入(缓出)。delayMs 让连着的几条依次出现。</summary>
    static void AnimateIn(FrameworkElement el, int delayMs)
    {
        var t = new TranslateTransform { Y = 10 };
        el.RenderTransform = t;
        el.Opacity = 0;
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        t.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260)) { BeginTime = begin, EasingFunction = ease });
        el.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = begin, EasingFunction = ease });
    }

    // 会话面板外壳:普通=实心卡;幽灵=虚线边框 + 提示(不保留记录、不纳入记忆)。
    // overlayBanner=true(空态):提示【浮】在顶部,不占布局、不顶动居中的输入框(用户裁定)。
    FrameworkElement ConvShell(FrameworkElement inner, bool ghost, bool overlayBanner)
    {
        if (!ghost) { var c = Ui.Card(inner, new Thickness(0)); c.Padding = new Thickness(12); return c; }

        var banner = Ui.Caption("幽灵会话 · 不保留记录、不纳入记忆");
        banner.HorizontalAlignment = HorizontalAlignment.Center;
        banner.TextAlignment = TextAlignment.Center;
        banner.SetResourceReference(TextBlock.ForegroundProperty, "Accent");

        FrameworkElement hostChild;
        if (overlayBanner)
        {
            hostChild = inner;   // 提示不进布局流,改为在外层 Grid 顶部浮放
        }
        else
        {
            var d = new DockPanel { LastChildFill = true };
            var bWrap = new Border { Child = banner, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(bWrap, Dock.Top);
            d.Children.Add(bWrap);
            d.Children.Add(inner);
            hostChild = d;
        }

        var host = new Border { Child = hostChild, Padding = new Thickness(12) };
        host.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        host.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        var r = TryFindResource("RadiusMd") is CornerRadius cr ? cr.TopLeft : 8;
        var dash = new System.Windows.Shapes.Rectangle
        {
            StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 }, Fill = Brushes.Transparent,
            RadiusX = r, RadiusY = r, IsHitTestVisible = false,
        };
        dash.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent");
        var g = new Grid();
        g.Children.Add(host);
        g.Children.Add(dash);
        if (overlayBanner)
        {
            // 顶部浮放的提示:不占布局(Grid 覆盖层),因此不会把居中输入框往下顶。
            var floatWrap = new Border { Child = banner, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0), IsHitTestVisible = false };
            g.Children.Add(floatWrap);
        }
        return g;
    }

    FrameworkElement PlaceholderCenter()
    {
        var def = Workspaces.All.FirstOrDefault(w => w.Key == _wsKey);
        var name = def is null ? _wsKey : I18n.Strings.Get(def.TitleKey);
        var t = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        var c = Ui.Caption("该工作空间的功能待其前置阶段接入;右侧的会话与项目管理已就绪。");
        c.HorizontalAlignment = HorizontalAlignment.Center;
        c.TextAlignment = TextAlignment.Center;
        var box = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 520 };
        box.Children.Add(t);
        box.Children.Add(new Border { Height = 8 });
        box.Children.Add(c);
        return Ui.Card(box, new Thickness(0));
    }

    /// <summary>
    /// 气泡里的正文。★ 用【只读 TextBox】而不是 TextBlock:WPF 的 TextBlock 不能选中,
    /// 对话内容就没法复制(用户反馈)。外观必须走 PlainTextBox 这套模板 —— 常规 TextBox 模板
    /// 把底色写死成 BgSunken(不是 TemplateBinding),在代码里设 Background 是【无效】的,
    /// 于是气泡里糊出一块灰底、配上气泡的白字就成了"灰底白字"(用户反馈)。
    /// 抽成静态是为了让渲染诊断画的就是这一个 —— 这种"只有画出来才看得见"的毛病,无头断言抓不到。
    /// </summary>
    internal static TextBox MessageText(bool user)
    {
        var tb = new TextBox { TextWrapping = TextWrapping.Wrap };
        tb.SetResourceReference(FrameworkElement.StyleProperty, "PlainTextBox");
        tb.SetResourceReference(TextBox.ForegroundProperty, user ? "FgOnAccent" : "FgPrimary");
        tb.SetResourceReference(TextBox.SelectionBrushProperty, user ? "FgOnAccent" : "Accent");
        return tb;
    }

    FrameworkElement Bubble(ChatMessage m, int index = -1)
    {
        if (m.Role == ChatRole.System)
        {
            var sys = Ui.Caption(m.Text);
            sys.HorizontalAlignment = HorizontalAlignment.Center;
            sys.TextAlignment = TextAlignment.Center;
            sys.Margin = new Thickness(0, 6, 0, 6);
            return sys;
        }
        var user = m.Role == ChatRole.User;
        var stack = new StackPanel();
        // 附件预览(图片缩略图 / 文件卡)—— 展示给用户看的"发过去的东西"
        if (m.Attachments is { Count: > 0 })
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, m.Text.Length > 0 ? 6 : 0) };
            foreach (var a in m.Attachments) wrap.Children.Add(AttachChip(a, onRemove: null));
            stack.Children.Add(wrap);
        }
        if (m.Text.Length > 0)
        {
            var tb = MessageText(user);

            // ★ 超长文本【默认折叠】(用户裁定):只显示前 N 行,点一下展开,再点收起。
            //   ——【只是显示折叠】。给 AI 的永远是全文(m.Text 一个字都没少),折叠不影响数据。
            var lines = m.Text.Split('\n');
            if (lines.Length > CollapseLines)
            {
                var key = BubbleKey(m, index);
                var expanded = _expandedBubbles.Contains(key);
                tb.Text = expanded ? m.Text : string.Join("\n", lines.Take(CollapseLines));
                stack.Children.Add(tb);

                var toggle = new TextBlock
                {
                    Text = expanded ? "收起" : $"展开全部({lines.Length} 行)",
                    Cursor = Cursors.Hand, Margin = new Thickness(0, 6, 0, 0),
                    TextDecorations = TextDecorations.Underline,
                };
                toggle.SetResourceReference(TextBlock.ForegroundProperty, user ? "FgOnAccent" : "FgSecondary");
                toggle.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
                toggle.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    if (!_expandedBubbles.Remove(key)) _expandedBubbles.Add(key);
                    BuildConversation();
                };
                stack.Children.Add(toggle);
            }
            else
            {
                tb.Text = m.Text;
                stack.Children.Add(tb);
            }
        }
        return BubbleShell(stack, user);
    }

    /// <summary>气泡外壳(自己发的靠右、强调色;AI 的靠左、沉底色)。与正文同理:渲染诊断画的就是它。</summary>
    internal static Border BubbleShell(UIElement content, bool user)
    {
        var bubble = new Border
        {
            Child = content, Padding = new Thickness(11, 8, 11, 8), MaxWidth = 560, Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        bubble.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        bubble.SetResourceReference(Border.BackgroundProperty, user ? "Accent" : "BgSunken");
        return bubble;
    }

    // ---------------------------------------------------------------- 动作
    void NewSession()
    {
        TheApp.Chat.PurgeGhosts();   // 切走时清掉幽灵会话
        // ★ 当前上下文里【已有空会话】就不重复建 —— 选中它并震荡提醒(用户裁定)。
        //   "上下文"按 _projectId 判定:普通会话/各项目各自算,互不影响。
        var inCtx = _projectId is { } pid ? TheApp.Chat.SessionsOf(pid) : TheApp.Chat.NormalSessions(_wsKey);
        var empty = inCtx.FirstOrDefault(s => !TheApp.Chat.MessagesOf(s.SessionId).Any());
        if (empty is not null)
        {
            _sessionId = empty.SessionId;
            BuildSessions();
            BuildConversation();
            if (_sessionRows.TryGetValue(empty.SessionId, out var row)) Ui.Shake(row);
            _input.Focus();
            return;
        }

        var scope = _projectId is null ? ProjectScope.Personal : (TheApp.Projects.Find(_projectId!)?.Scope ?? ProjectScope.Personal);
        var s2 = TheApp.Chat.NewSession(_projectId, _wsKey, scope);
        _sessionId = s2.SessionId;
        BuildSessions();
        BuildConversation();
        _input.Focus();
    }

    void SendCurrent()
    {
        var text = _input.Text;
        if (string.IsNullOrWhiteSpace(text) && _pending.Count == 0) return;   // 空且无附件不发
        if (_sessionId is null) NewSession();
        if (_sessionId is null) return;
        var atts = _pending.Count > 0 ? _pending.ToList() : null;
        // ★ 先清空本地待发状态,再发送 —— Chat.Send 会【同步】触发 Changed → 重建会话区;
        //   若此时 _pending 还没清,重建就会把【已经发出去】的附件又挂回输入框上(用户反馈的 bug)。
        _pending.Clear();
        _draft = "";
        TheApp.Chat.Send(_sessionId, text, atts);
    }

    // 附件上限与"上下文吃紧"阈值(用户裁定):最多 99 个;超过 5 个提示、且只展开显示前 5 个。
    /// <summary>输入框最多长到几行,再多就在框内滚动(用户裁定:3 行)。</summary>
    const int InputMaxLines = 3;

    /// <summary>消息超过这么多行就【默认折叠】显示(用户裁定:30 行)。★ 只折叠显示,给 AI 的仍是全文。</summary>
    const int CollapseLines = 30;

    const int MaxAttachments = 99;
    const int SoftAttachLimit = 5;
    static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    // "+"按钮:点开【选择文件(支持多选) / 选择文件夹】。★ 图片/文件已合并为一个"选择文件";
    //   剪贴板截图改成在输入框里 Ctrl+V(见 OnInputPaste)。只带路径,不真发内容。
    FrameworkElement AttachButton()
    {
        var t = new TextBlock { Text = "+", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        var b = new Border { Child = t, Width = 40, Height = 40, Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.BorderThickness = new Thickness(1);
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.ToolTip = "添加文件 / 文件夹(截图可直接在输入框粘贴)";

        var menu = new ContextMenu();
        var mFile = new MenuItem { Header = "选择文件…" };
        mFile.Click += (_, _) => PickFiles();
        var mFolder = new MenuItem { Header = "选择文件夹…" };
        mFolder.Click += (_, _) => PickChatFolder();
        menu.Items.Add(mFile);
        menu.Items.Add(mFolder);
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; MenuHost.Show(menu, b, System.Windows.Controls.Primitives.PlacementMode.Top); };
        return b;
    }

    static AttachKind KindOf(string path)
        => ImageExts.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()) ? AttachKind.Image : AttachKind.File;

    void PickFiles()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择文件", Multiselect = true, Filter = "所有文件|*.*",
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            AddPaths(dlg.FileNames);
        }
        catch (Exception ex) { ConfirmDialog.Show("打不开文件选择框", ex.Message, confirmText: "好", cancelText: "关闭"); }
    }

    void PickChatFolder()
    {
        var f = ProjectUi.PickFolder("选择要作为附件的文件夹");
        if (f is not null) AddPaths(new[] { f });
    }

    // 统一入库:文件按扩展名分图片/普通,目录记为文件夹;去重;超过 99 个截断并提示。
    void AddPaths(IEnumerable<string> paths)
    {
        var existing = _pending.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dropped = false;
        foreach (var raw in paths)
        {
            var path = raw?.Trim() ?? "";
            if (path.Length == 0 || !existing.Add(path)) continue;
            if (_pending.Count >= MaxAttachments) { dropped = true; break; }
            var isDir = System.IO.Directory.Exists(path);
            var kind = isDir ? AttachKind.Folder : KindOf(path);
            var display = isDir ? new System.IO.DirectoryInfo(path).Name : System.IO.Path.GetFileName(path);
            _pending.Add(new ChatAttachment(kind, path, display));
        }
        BuildConversation();
        if (dropped)
            ConfirmDialog.Show("附件已达上限", $"一次最多挂载 {MaxAttachments} 个附件,多出的已略过。", confirmText: "好", cancelText: "关闭");
    }

    // 在输入框里粘贴:剪贴板是图片就收进附件栏(而不是把图片/乱码贴成文本);纯文本粘贴照常。
    /// <summary>
    /// Ctrl+V:如果剪贴板里是【文件】或【图片】,就收进附件栏并返回 true(这次按键由我们消化)。
    /// 纯文本返回 false,交给 TextBox 自己粘贴。规则见 ClipboardIntent.Decide(可单测)。
    /// </summary>
    bool TryPasteAttachment()
    {
        try
        {
            var intent = ClipboardIntent.Decide(
                hasFiles: Clipboard.ContainsFileDropList(),
                hasImage: Clipboard.ContainsImage(),
                hasText: Clipboard.ContainsText());

            switch (intent)
            {
                case ClipboardIntent.Kind.Files:
                    var files = Clipboard.GetFileDropList().Cast<string?>().Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();
                    if (files.Count == 0) return false;
                    AddPaths(files);
                    return true;
                case ClipboardIntent.Kind.Image:
                    AddClipboardImage();
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            // 剪贴板被别的进程占用会抛 —— 如实说一声,别静默失败让人以为"粘不进去"
            ConfirmDialog.Show("粘贴失败", ex.Message, confirmText: "好", cancelText: "关闭");
            return true;
        }
    }

    void OnInputPaste(object sender, DataObjectPastingEventArgs e)
    {
        try
        {
            var d = e.SourceDataObject;

            // ① 从资源管理器复制的【文件】-> 直接进附件栏(按路径,不读内容)
            if (d?.GetDataPresent(DataFormats.FileDrop) == true &&
                d.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                e.CancelCommand();
                // ★ 延后执行:粘贴处理器里直接重建界面会打断这次输入事件
                Dispatcher.BeginInvoke(new Action(() => AddPaths(files)), System.Windows.Threading.DispatcherPriority.Background);
                return;
            }

            // ② 截图(剪贴板位图)-> 进附件栏。有文本时按文本贴,不抢。
            var hasImage = d?.GetDataPresent(DataFormats.Bitmap) == true || Clipboard.ContainsImage();
            var hasText = d?.GetDataPresent(DataFormats.UnicodeText) == true;
            if (!hasImage || hasText) return;
            e.CancelCommand();
            Dispatcher.BeginInvoke(new Action(AddClipboardImage), System.Windows.Threading.DispatcherPriority.Background);
        }
        catch { /* 粘贴出错不该影响输入 */ }
    }

    void AddClipboardImage()
    {
        try
        {
            var raw = Clipboard.GetImage();
            if (raw is null) return;
            // ★ 剪贴板截图的 alpha 常常整条是 0(DIB 没有真 alpha)—— 直接存 png 会得到一张
            //   【完全透明】的图:附件挂上了、预览却是空白(用户反馈)。这里先把它修成不透明。
            var img = ClipboardImageFix.Normalize(raw);
            // 落一份预览 png 到本机临时目录(仅供显示 + 给 AI 一个可读路径;不通过网络发送内容)
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "localai-clip-" + Guid.NewGuid().ToString("N")[..8] + ".png");
            using (var fs = System.IO.File.Create(tmp))
            {
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                enc.Save(fs);
            }
            if (_pending.Count < MaxAttachments)
                _pending.Add(new ChatAttachment(AttachKind.Clipboard, tmp, "粘贴的截图"));
            BuildConversation();
        }
        catch (Exception ex) { ConfirmDialog.Show("粘贴截图失败", ex.Message, confirmText: "好", cancelText: "关闭"); }
    }

    FrameworkElement PendingStrip()
    {
        var box = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        // 顶行:「附件 X 个」+ 紧跟其右的【清空】(用户裁定:清空放计数右边;去掉橙黄"上下文吃紧"提醒)。
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4), VerticalAlignment = VerticalAlignment.Center };
        var count = new TextBlock { Text = $"附件 {_pending.Count} 个", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        count.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        count.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        head.Children.Add(count);
        head.Children.Add(Chip("清空", "RiskDanger", () => { _pending.Clear(); BuildConversation(); }));
        box.Children.Add(head);

        // 只展开前 5 个;其余折叠成一个"+N"卡(避免占满输入区)。
        var wrap = new WrapPanel();
        var shown = Math.Min(_pending.Count, SoftAttachLimit);
        for (int k = 0; k < shown; k++)
        {
            var item = _pending[k];   // 按对象移除,不按下标(下标闭包越界曾致闪退)
            wrap.Children.Add(AttachChip(item, onRemove: () => { _pending.Remove(item); BuildConversation(); }));
        }
        if (_pending.Count > shown)
            wrap.Children.Add(MoreChip(_pending.Count - shown));
        box.Children.Add(wrap);
        return box;
    }

    // 折叠卡:显示"还有 N 个",点它一次性移除被折叠的那些(留下展开的前 5 个)。
    FrameworkElement MoreChip(int more)
    {
        var t = new TextBlock { Text = $"+{more}", FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        var card = new Border { Child = t, Width = 84, Height = 84, Margin = new Thickness(0, 0, 8, 0), ToolTip = $"另有 {more} 个附件未展开" };
        card.SetResourceReference(Border.BackgroundProperty, "BgSunken");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.BorderThickness = new Thickness(1);
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        return card;
    }

    // 附件小卡:图片显缩略图;文件夹/PDF/其它文件显对应图标 + 名;右上角 × 可移除。
    FrameworkElement AttachChip(ChatAttachment a, Action? onRemove)
    {
        FrameworkElement inner;
        // 缩略图读不出来时【不要留一个空白方块】——那看起来就是"预览坏了"却什么也没说;
        // 退回图标 + 名字,至少让人知道附件在、只是画不出预览。
        var thumb = a.IsImage ? Thumb(a.Path, 120) : null;
        if (thumb is not null)
        {
            inner = new Image { Source = thumb, Stretch = Stretch.UniformToFill, Width = 84, Height = 84 };
        }
        else
        {
            var icon = a.Kind == AttachKind.Folder ? IconName.Folder
                     : IsPdf(a.Path) ? IconName.Pdf
                     : IconName.File;
            var ic = Icons.Make(icon, 18, "FgSecondary");
            var nm = new TextBlock { Text = a.Display, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 120, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            nm.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            nm.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6) };
            row.Children.Add(ic); row.Children.Add(nm);
            inner = row;
        }
        var card = new Border { Child = inner, Margin = new Thickness(0, 0, 8, 0), Padding = thumb is not null ? new Thickness(0) : new Thickness(4), ClipToBounds = true };
        card.SetResourceReference(Border.BackgroundProperty, "BgSunken");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.BorderThickness = new Thickness(1);
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        card.ToolTip = a.Kind == AttachKind.Clipboard ? "粘贴的截图(发送的是读取指令,不是内容)" : a.Path;

        if (onRemove is null) return card;
        var x = new TextBlock { Text = "×", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        x.SetResourceReference(TextBlock.ForegroundProperty, "FgOnAccent");
        var xb = new Border { Child = x, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0), Cursor = Cursors.Hand };
        xb.SetResourceReference(Border.BackgroundProperty, "Accent");
        xb.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        xb.MouseLeftButtonUp += (_, e) => { e.Handled = true; onRemove(); };
        var g = new Grid();
        g.Children.Add(card);
        g.Children.Add(xb);
        return g;
    }

    static bool IsPdf(string path) => System.IO.Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    static System.Windows.Media.ImageSource? Thumb(string path, int decodeWidth)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit();
            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;   // 立即读,别锁文件
            bi.DecodePixelWidth = decodeWidth;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- 小控件
    // 会话列表【右侧】的长条按钮,拉开项目选择器。箭头用 Stretch=Uniform 归一,视觉居中。
    FrameworkElement ArrowButton()
    {
        var chev = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M7 1 L1 7 L7 13"),
            Stretch = Stretch.Uniform, Width = 6, Height = 13, StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        chev.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "FgSecondary");
        // ★ 用户裁定:不要高度居中的小按钮,而是【整条竖直的窄把手】——
        //   视觉上像"右边露出一小节没完全展开的面板",点到任意处都拉开项目抽屉。
        //   注意:这只是【视觉暗示】,本体就是一个按钮(不是真的把抽屉内容漏在这里,
        //   否则改窗口大小会露馅)。左侧圆角 + 面板底色,像一块被右缘截断的面板边。
        var b = new Border
        {
            Child = chev, Width = 16, Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Stretch,   // 撑满整个会话区高度
            Cursor = Cursors.Hand, CornerRadius = new CornerRadius(8, 0, 0, 8),
            BorderThickness = new Thickness(1, 1, 0, 1),      // 右缘不描边 = 像延伸出画面
            ToolTip = "项目 · 点击展开",
        };
        b.SetResourceReference(Border.BackgroundProperty, "BgSunken");   // 面板感底色,非透明
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgSunken");
        b.MouseLeftButtonUp += (_, _) => OpenPicker();
        return b;
    }

    void OpenPicker()
    {
        // 用户裁定:选【项目】不关抽屉(让用户确认选了哪个);选【普通会话】直接收起抽屉。
        var picker = new ProjectPickerView(_wsKey, _projectId,
            onPick: SelectProject,
            onNormal: () => { ToNormal(); Overlay.CloseActive(); });
        (Application.Current.MainWindow as MainWindow)?.OpenSideDrawer("项目", picker, IconName.Folder);
    }

    // 幽灵会话按钮:虚线小圈 = 未进入;【实线】= 正在幽灵会话中(再按一次退出,用户裁定)。
    FrameworkElement GhostButton(bool active)
    {
        var ring = new System.Windows.Shapes.Ellipse
        {
            Width = 13, Height = 13, StrokeThickness = active ? 1.8 : 1.4,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        if (!active) ring.StrokeDashArray = new DoubleCollection { 2, 1.6 };   // 未进入 = 虚线
        // 进入态底色是 BgSelected(墨白近黑),圈色走 FgOnSelected 才不会黑底黑圈(统一高亮规则)
        ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, active ? "FgOnSelected" : "FgSecondary");

        var b = new Border
        {
            Child = ring, Width = 26, Height = 26, Margin = new Thickness(0, 0, 4, 0),
            Cursor = Cursors.Hand, Background = Brushes.Transparent,
            ToolTip = active ? "退出幽灵会话(回到普通会话)" : "幽灵会话:不保留记录、不纳入记忆",
        };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        if (active) b.SetResourceReference(Border.BackgroundProperty, "BgSelected");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => { if (active) b.SetResourceReference(Border.BackgroundProperty, "BgSelected"); else b.Background = Brushes.Transparent; };
        b.MouseLeftButtonUp += (_, _) => ToggleGhost();
        return b;
    }

    /// <summary>进入 / 退出幽灵会话。退出即抹除该会话并回到普通会话(可退出 —— 用户反馈"按下之后无法退出")。</summary>
    void ToggleGhost()
    {
        // 退出幽灵 -> 回普通会话的空态(ToNormal 已经保证落在空会话上)
        if (InGhost) { ToNormal(); _input.Focus(); return; }
        var g = TheApp.Chat.NewGhostSession(_wsKey);
        _sessionId = g.SessionId;
        BuildSessions();
        BuildConversation();
        _input.Focus();
    }

    FrameworkElement BackChip()
    {
        var t = new TextBlock { Text = "‹ 普通", VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border { Child = t, Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.BorderThickness = new Thickness(1);
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => ToNormal();
        return b;
    }
}
