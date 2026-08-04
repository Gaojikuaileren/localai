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
    // ★ MinHeight 固定成最高那颗按钮(幽灵 26 > 新建 24):这一行的高度【不许随有没有按钮而变】。
    //   否则切工作空间/进出项目时,下面整个会话列表会跟着上下跳几像素(用户反馈)。
    readonly DockPanel _actionsRow = new() { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6), MinHeight = 26 };
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
        // 只有翻译空间要跟同传的进行态(开始选中新会话/被删即停)
        if (_wsKey == "translation")
        {
            TheApp.Interpret.Changed += OnInterpretChanged;
            TheApp.FileTrans.FocusSession += OnFileTransFocus;
            TheApp.I18n.FocusSession += OnFileTransFocus;   // 同一个动作:选中新建的场景会话
        }
        if (_wsKey == "chat")
        {
            TheApp.Reply.Changed += OnReplyChanged;
            TheApp.Reply.FocusSession += OnFileTransFocus;   // 回信会话建了就选中(D61)
        }
        // ★ 目标池一变,发送键就要跟着变(拖进第一个语言时按钮必须当场亮起来)。
        //   只刷按钮状态,不重建整个会话区 —— 重建会打断正在打的字。
        TheApp.Translation.Changed += RefreshSendEnabled;
        TheApp.History.JumpRequested += OnJumpToHistory;
        Unloaded += (_, _) =>
        {
            TheApp.Chat.Changed -= OnChatChanged;
            TheApp.Projects.Changed -= UpdateContext;
            TheApp.Translation.Changed -= RefreshSendEnabled;
            TheApp.History.JumpRequested -= OnJumpToHistory;
            if (_wsKey == "chat")
            {
                TheApp.Reply.Changed -= OnReplyChanged;
                TheApp.Reply.FocusSession -= OnFileTransFocus;
            }
            if (_wsKey == "translation")
            {
                TheApp.Interpret.Changed -= OnInterpretChanged;
                TheApp.FileTrans.FocusSession -= OnFileTransFocus;
                TheApp.I18n.FocusSession -= OnFileTransFocus;
            }
            // ★ 离开翻译空间 = 这一场同传结束(D58 的口径;审计 2026-08-02:
            //   原先只有空间内切模式会停,切去别的工作空间就留下一个看不见的进行中)。
            if (_wsKey == "translation" && TheApp.Interpret.Running) TheApp.Interpret.Stop();
            TheApp.Chat.PurgeGhosts();
        };
    }

    void OnChatChanged() { BuildSessions(); BuildConversation(); }

    /// <summary>
    /// 同传的进行态变了(开始/结束)—— 只有翻译空间关心。
    /// ★ 开始那一刻把新建的会话选中:按钮在 TranslationBar 里,它够不到本视图的 _sessionId;
    ///   不选中的话,列表里多出来一条,面板却还挂在旧会话上(审计 2026-08-02)。
    /// ★ 会话在进行中被删掉 -> 当场结束这一场,不留一个指着回收站的"进行中"。
    /// </summary>
    /// <summary>文件翻译面板导入时新建了会话 -> 选中它(面板自己够不到本视图的选中态)。</summary>
    void OnFileTransFocus(string sid)
    {
        _sessionId = sid;
        _projectId = null;
        BuildSessions();
        BuildConversation();
    }

    /// <summary>多语言场景:把当前选中的 JSON 译表会话绑给状态(没选中 = 草稿态,首笔编辑自建会话)。</summary>
    FrameworkElement BuildI18nPanel()
    {
        TheApp.I18n.SetSession(_sessionId is { } sid && TheApp.Chat.Find(sid)?.I18nTable == true ? sid : null);
        return new I18nPanel();
    }

    StackPanel? _chatSceneHead;   // 聊天空间顶部的「聊天/回信」切换条(回信场景里单独装配)
    bool _replyScene;   // 上一次画的是不是回信场景(场景没变就不整区重建,别打断打字)
    void OnReplyChanged()
    {
        if (TheApp.Reply.SceneReply == _replyScene) return;
        BuildConversation();
    }

    void OnInterpretChanged()
    {
        var st = TheApp.Interpret;
        if (st.Running && st.RunningSessionId is { } rid)
        {
            if (TheApp.Chat.Find(rid) is null or { DeletedAt: not null }) { st.Stop(); return; }
            if (_sessionId != rid) { _sessionId = rid; _projectId = null; }
        }
        BuildSessions();
        BuildConversation();
    }

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
        // ★ 不可恢复的动作要有二次确认(审计 2026-07-31):
        //   单条恢复/删除都在旁边,一次误点就把整篮子原文连同温层归档一起清掉 ——
        //   这是这一屏里唯一一个【没有任何回头路】的按钮,不该和其它按钮一样一点就走。
        var clear = Ui.DangerFilled("全部清除", (_, _) =>
        {
            var n = items.Count;
            if (!ConfirmDialog.Show("全部清除",
                    $"彻底删除这 {n} 条已删除的会话?\n\n连同它们归档到本机的对话原文一起清掉,【无法恢复】。",
                    confirmText: "彻底删除", danger: true)) return;
            TheApp.Chat.ClearDeleted(_wsKey);
        });
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
        // ★ 只自动打开【本空间】的会话(审计 2026-08-02):排第一的可能是跨空间的,
        //   替用户打开它 = JumpToOwnWorkspace 那三道护栏(隐藏空间/草稿/未知 key)整体被绕过,
        //   聊天内容还能直接写进翻译会话、混进翻译历史 —— 正是 D53 要防的那条污染路。
        //   没有本空间的会话就不自动开(空态),想开跨空间的,列表里点它、走跳转。
        _sessionId = (proj?.DeletedAt is not null ? TheApp.Chat.AllSessionsOf(projectId) : TheApp.Chat.SessionsOf(projectId))
            .FirstOrDefault(x => x.WorkspaceKey == _wsKey)?.SessionId;
        TheApp.Chat.PurgeGhosts();
        BuildSessions();
        BuildConversation();
    }

    /// <summary>
    /// 深链进来:打开【指定的那一条】会话(必要时先进它所在的项目上下文)。
    /// ★ 与 SelectProject 的区别:那边是"进项目,第一条会话我替你挑";
    ///   这边是"就打开你点的这条" —— 跨空间跳转必须用这个,否则点 A 打开 B,静默错开。
    /// </summary>
    public void OpenSession(string? projectId, string sessionId)
    {
        _trashOpen = false;
        _projectId = projectId;
        _sessionId = sessionId;
        TheApp.Chat.PurgeGhosts();
        ApplySessionScene(TheApp.Chat.Find(sessionId));
        BuildSessions();
        BuildConversation();
    }

    /// <summary>
    /// 把界面切到这条会话【自己所属的场景】。
    ///
    /// ★ 同一个工作空间里也分模块:翻译空间有【文字翻译】和【同声传译】两套界面,
    ///   会话自己记着是哪一种(ChatSession.Interpret)。点开就把界面切过去。
    /// ★★ 两个方向都要切。原先只做了"点同传记录 -> 切到同传"这一半;
    ///   反过来在同传界面点开一条文字会话时不切回去,fail-closed 那道闸会让同传面板
    ///   拿不到这条会话(interpSid 为 null),于是你点了它、屏幕上却什么也没打开。
    /// </summary>
    void ApplySessionScene(ChatSession? s)
    {
        if (s is null || !SpecFor(_wsKey).ModeSwitch) return;
        TheApp.Interpret.SetMode(s.Interpret ? TranslationMode.Interpret
                               : s.FileTrans ? TranslationMode.FileTrans
                               : s.I18nTable ? TranslationMode.I18n
                               : TranslationMode.Text);
    }

    /// <summary>
    /// 跨空间会话:不在本空间打开,而是【转到它自己的工作空间】去。
    ///
    /// ★ 为什么不能就地打开:会话的 WorkspaceKey 就是它的身份 ——
    ///   翻译历史按 WorkspaceKey 取、【不看 ProjectId】(ChatCenter.AllTranslationSessions),
    ///   所以在聊天界面里打开一条翻译会话、往里打聊天内容,那段内容会进翻译历史。
    ///   转过去之后,内容归属和界面/AI 行为就都对得上了。
    /// </summary>
    void JumpToOwnWorkspace(ChatSession s)
    {
        var name = ProjectUi.SpaceName(s.WorkspaceKey);

        // 认不出的 key(老存档里留着已经删掉的空间):跳过去只会静默什么都不发生,不如就地说清楚
        if (!Workspaces.Known(s.WorkspaceKey))
        {
            ConfirmDialog.Show("这条会话打不开",
                $"它记着的工作空间「{s.WorkspaceKey}」现在不存在了(多半是老存档留下的)。\n" +
                "可以在它的三点菜单里用「发送到工作空间」,把它挪到一个还在的空间。",
                confirmText: "知道了", cancelText: "关闭");
            return;
        }

        // 在扩展里关掉的空间:用户明说过"我不要这个",一次点击就把人送进去属于越权 —— 先问
        var draftLoss = !string.IsNullOrWhiteSpace(_draft) || _pending.Count > 0;
        if (!TheApp.Settings.IsWorkspaceVisible(s.WorkspaceKey))
        {
            // ★ 这个分支同样会重建页面丢草稿 —— 把损失写进同一个确认框(审计 2026-08-02:
            //   原先 else-if 让"前往并显示"把草稿确认整个跳过,确认完草稿被静默丢掉)。
            if (!ConfirmDialog.Show($"「{name}」已经在扩展里隐藏了",
                    $"这条会话属于「{name}」。要前往吗?前往会把它重新显示在左栏。"
                    + (draftLoss ? "\n★ 这边还没发出去的内容不会带过去。" : ""),
                    confirmText: "前往并显示", cancelText: "取消")) return;
            TheApp.Settings.SetWorkspaceVisible(s.WorkspaceKey, true);
            (Application.Current.MainWindow as MainWindow)?.RefreshNavRail();
        }
        // ★ 切走会重建整个页面,这边没发出去的草稿和待发附件【不会跟过去】—— 别默默丢
        else if (draftLoss
                 && !ConfirmDialog.Show("切到别的工作空间?",
                        $"这条会话属于「{name}」,点开会切过去。\n这边还没发出去的内容不会带过去。",
                        confirmText: "切过去", cancelText: "留在这里")) return;

        (Application.Current.MainWindow as MainWindow)?.NavigateToSession(s.WorkspaceKey, s.ProjectId, s.SessionId);
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
        // ★★ 每个工作空间都给(2026-08-01 用户裁定)—— 原先只有聊天有。
        //   判据是"这个空间给不给新建会话",而幽灵本来就是"建一条不留痕的会话":
        //   `+` 已经在所有空间都给了,幽灵不比它更假。少给的那些空间里,
        //   这一行还会因为少了个 26px 的按钮而比聊天矮 2px,列表整体跟着上下跳。
        _ghostHost.Content = inProject ? null : GhostButton(InGhost);
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
        // ★ 同传记录和普通会话排在同一个列表里(用户裁定),所以要有个标记告诉用户"这条不一样" ——
        //   用麦克风图标而不是加一行字:列表本来就窄,一个图标就够分辨,也不挤掉标题。
        if (s.Interpret || s.FileTrans || s.I18nTable || s.ReplyLetter)
        {
            var mk = Icons.Make(s.Interpret ? IconName.Mic : s.FileTrans ? IconName.File : s.I18nTable ? IconName.Extensions : IconName.Mail, 12, selected ? "FgOnSelected" : "Accent");
            mk.Margin = new Thickness(0, 0, 5, 0);
            mk.VerticalAlignment = VerticalAlignment.Center;
            titleRow.Children.Add(mk);
        }
        // ★ 跨空间会话(只会出现在项目会话列表里 —— 项目能同时挂多个工作空间,会话各归各的空间)。
        //   标记方式:第三个前缀图标,和置顶点、同传麦克风同一个位置,用该空间自己的图标。
        //   ★★【一个像素都不降】—— 这个库里"降透明度/换灰键"已经各有主人:
        //     只读浏览是 Opacity 0.7、禁用模板直接换 FgMuted、日历里的灰日明写着"点了不跳月"。
        //     而这条会话恰恰是【能点、点了会带你走】,拿灰去标它等于说反话。
        //     所以整行保持满不透明 + FgPrimary + 手型光标(本库里"能点"的唯一说法),只加图标。
        var foreign = s.WorkspaceKey != _wsKey;
        if (foreign)
        {
            var wsIcon = Icons.Make(ProjectUi.SpaceIcon(s.WorkspaceKey), 12, selected ? "FgOnSelected" : "Accent");
            wsIcon.Margin = new Thickness(0, 0, 5, 0);
            wsIcon.VerticalAlignment = VerticalAlignment.Center;
            titleRow.Children.Add(wsIcon);
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
        // ★ 已删除项目的只读浏览【不给三点菜单】(审计 2026-08-02):那套菜单按活会话设计,
        //   「移动到项目/新建项目并移入」会把软删的会话搬进活项目 —— 处处不可见的孤儿,
        //   30 天后随原项目清理一起被删,还把活项目的空间标签卡死。只读就是只读。
        if (ViewingDeletedProject)
        {
            var roRow = new DockPanel { LastChildFill = true };
            roRow.Children.Add(textCol);
            var roHost = new Border { Child = roRow, Padding = new Thickness(9, 6, 4, 6), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand };
            roHost.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            roHost.Background = selected ? (Brush)FindResource("BgSelected") : Brushes.Transparent;
            roHost.MouseLeftButtonUp += (_, _) => { _sessionId = s.SessionId; BuildSessions(); BuildConversation(); };
            return roHost;
        }
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
        // ★ 不属于本空间的:说清楚点了会发生什么。措辞是【陈述 + 后果】,
        //   不许出现"不可用/只读/无法打开"那类词 —— 那是灰化的语言,和实际行为相反。
        if (foreign)
            host.ToolTip = $"这条会话属于「{ProjectUi.SpaceName(s.WorkspaceKey)}」工作空间。\n"
                         + "点开会切到那边打开 —— 内容和界面都按那边来。";
        host.MouseLeftButtonUp += (_, _) =>
        {
            // 跨空间:转到它自己的空间去开,而不是在这儿打开(理由见 JumpToOwnWorkspace)
            if (foreign) { JumpToOwnWorkspace(s); return; }
            _sessionId = s.SessionId;
            TheApp.Chat.PurgeGhosts();
            // ★ 同一空间内也分模块(文字翻译 / 同声传译):点开就把界面切到这条会话自己那一套,
            //   两个方向都切 —— 详见 ApplySessionScene。
            ApplySessionScene(s);
            // 聊天空间同规:回信会话 <-> 普通聊天,点开双向切场景(D61)
            if (_wsKey == "chat") TheApp.Reply.SetScene(s.ReplyLetter);
            BuildSessions();
            BuildConversation();
        };
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

        // ★ 同传记录不能搬去项目、也不能送到别的工作空间 —— 它只有在同传界面里才讲得通。
        //   做法是【菜单里根本不出现】,而不是点了再报错:没有的选项比灰着的选项更省事。
        var movable = ChatCenter.CanMove(s);

        var move = new MenuItem { Header = "移动到项目" };
        // ★ 按【会话自己的空间】列,不是按当前视图的空间(审计 2026-08-01)——
        //   项目会话列表里会出现跨空间的会话;拿视图的空间去列,等于把一条翻译会话
        //   往聊天的项目里搬,而 MoveToProject 只改 ProjectId、不改 WorkspaceKey,搬完还是跨空间的。
        foreach (var p in TheApp.Projects.Ongoing(s.WorkspaceKey))
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
        if (movable) m.Items.Add(move);

        // 发送到其它工作空间。★ 两处修正(2026-08-01):
        //   ① 排除的是【会话自己】所在的空间,不是当前视图 —— 否则对一条跨空间会话,
        //      菜单里没有"送回本空间"(唯一想做的那个),却有一个送到它已经在的空间的死项(点了空转);
        //   ② 只列左栏里【真的显示着】的空间 —— 在扩展里关掉等于用户说过"我不要这个",
        //      往那儿送等于把东西塞进他看不见的地方。
        var toWs = new MenuItem { Header = "发送到工作空间" };
        foreach (var w in Workspaces.Visible(TheApp.Settings))
        {
            if (w.Key == s.WorkspaceKey) continue;
            var mi = new MenuItem { Header = I18n.Strings.Get(w.TitleKey) };
            var key = w.Key;
            mi.Click += (_, _) => { if (_sessionId == s.SessionId) _sessionId = null; TheApp.Chat.MoveSessionToWorkspace(s.SessionId, key); };
            toWs.Items.Add(mi);
        }
        if (movable) m.Items.Add(toWs);

        // ★ 引用该会话(用户裁定 2026-08-02):复制会话 ID,粘贴到别的会话里发送,
        //   让那边的 AI 去读这段会话。★ 诚实:AI 未接入(P4),现在只是把引用标记复制好;
        //   接入后 AI 才会真的解引用 —— 提示里写明,不装作已经生效。
        var cite = new MenuItem { Header = "引用该会话" };
        cite.Click += (_, _) =>
        {
            try { Clipboard.SetText($"[引用会话 {s.SessionId}]"); } catch { }
            ConfirmDialog.Show("已复制引用",
                $"已复制:[引用会话 {s.SessionId}]\n\n粘贴到任何能对话的会话里发送,AI 会去读这段会话。\n★ AI 尚未接入(P4)——接入后这个引用才会真的被读取。",
                confirmText: "好", cancelText: "关闭");
        };
        m.Items.Add(cite);

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
        // ★ 跨空间会话的删除要说一句去向(审计 2026-08-02):它落进【它自己空间】的垃圾篓,
        //   本空间的"已删除"计数纹丝不动 —— 一个字不说,观感就是"彻底消失了"。
        var delHeader = s.Shared ? "删除共享会话…"
                      : s.WorkspaceKey != _wsKey ? $"删除会话(进「{ProjectUi.SpaceName(s.WorkspaceKey)}」的已删除)"
                      : "删除会话";
        var del = new MenuItem { Header = delHeader };
        del.Click += (_, _) =>
        {
            if (s.Shared)
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var ok = ConfirmDialog.Show("删除共享会话",
                        // ★ 不能写"对家里所有设备生效"(审计 2026-07-31):共享目前只是
                        //   【本机的一个标记】,从来没上传过。把尚未发生的事写成确凿后果,
                        //   比不说更坏 —— 用户会以为别的机器上也没了,而实际上什么都没发生。
                        // ★★ 2026-08-05 改措辞:原文写的是「中枢尚未接入」。模型接入当晚,
                        //   用户刚跟中枢聊完天,再读到这句就会认为它是假的,于是相信共享已经生效
                        //   (实测反馈:「我把副机的会话提升到共享,主机这边看不见」)。
                        //   ⇒ 系统是**一件一件**接入的,笼统的「未接入」在第一件接上那刻全部失信。
                        //     必须**指名道姓**说清缺的是哪一件。
                        $"删除共享会话「{s.Title}」?\n\n这条标了共享,但★ 会话同步还没有做 —— 中枢上目前没有存放共享会话的地方,这个标记从来没上传过。所以删除【只影响这台机器】。\n\n" +
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
                $"· 整段对话({n} 条消息)会一起标为共享\n" +
                "· ★ 提升之后【无法收回】\n\n" +
                // ★★ 2026-08-05 改措辞(实测反馈:「副机提升到共享,主机这边看不见」)。
                //   原文写「中枢尚未接入,现在只做标记」—— 而模型接入之后用户刚跟中枢聊过天,
                //   这句话读起来就是假的,于是他合理地认为共享已经生效。
                //   ★ 现在把缺的那件事**指名道姓**写出来,并且把「别的设备能看到」
                //     从确凿后果降级为将来时 —— 它今天不成立。
                "★ 但会话同步还没有做:中枢上没有存放共享会话的地方。\n" +
                "现在它只是本机的一个标记,别的设备还看不到。\n" +
                "等会话同步做出来之后,标了共享的会自动上传。",
                confirmText: "提升为共享", danger: true);
            if (ok) TheApp.Chat.ShareSession(s.SessionId);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    void MoveToNewProject(ChatSession s)
    {
        var folder = ProjectUi.PickFolder("为新项目选择文件夹");
        if (folder is null) return;
        // ★ 项目建在【会话自己的】空间(审计 2026-08-02):对跨空间会话用 _wsKey 的话,
        //   会话被移进一个它自己空间里不存在的项目 —— 在那儿失去唯一入口。
        var p = TheApp.Projects.Create(string.IsNullOrWhiteSpace(s.Title) ? "新项目" : s.Title, folder, null, s.Scope, s.WorkspaceKey);
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
        // ★ Esc 必须在这里自己收:主窗口那条 Esc 总闸【够不到】这里 —— Flyout 的 Popup
        //   从未加入任何可视/逻辑树(PlacementTarget 只是定位提示,不建立父子关系),
        //   焦点在 box 里时按键的路由终点就是这个 Popup,压根不经过主窗口;
        //   而普通 Popup 又不像 ContextMenu 那样自带 Esc 关闭 —— 于是重命名浮窗按 Esc 关不掉。
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Save(); }
            else if (e.Key == Key.Escape) { e.Handled = true; Overlay.CloseActive(); }
        };
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

    FrameworkElement BuildInputArea(bool attachmentsBelow, ConvSpec spec)
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
        // ★ 标记它是【AI 交流输入框】—— Tab 唯一认的落点(用户第二轮裁定)。
        //   用显式标记而不是"树里第一个 TextBox":输入区每次重建都是新对象,标记跟着控件走最可靠。
        FocusPolicy.SetIsChatInput(_input, true);
        _input.TextChanged += (_, _) =>
        {
            _draft = _input.Text;
            // ★ 形态实时上报:长文本要把语法/例句两档灰掉,总不能等按了发送才告诉用户。
            //   附件里的内容一律按"带格式的长文本"算 —— 它的段落是用户自己排的。
            spec.OnDraftChanged?.Invoke(_draft, _pending.Any(a => a.Kind != AttachKind.Image));
        };
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
            // ★ Enter 必须问【同一条】前置条件:只把按钮灰掉是做样子 ——
            //   目标池空着时按钮是灰的,回车却照发(审查发现的实缺口)。两条路一个判据。
            if (_canSend is not null && !_canSend()) return;
            SendCurrent();
        };
        // ★ 在输入框里直接【粘贴截图】(Ctrl+V):剪贴板是图片就收进附件栏,而不是当文本贴进去。
        //   用户裁定:去掉"+"菜单里的剪贴板项,改成这里粘贴。文本粘贴不受影响(仅在剪贴板是图片时拦截)。
        DataObject.AddPastingHandler(_input, OnInputPaste);
        // 翻译空间的发送 = 放大镜(是"查翻译"不是"发消息",用户裁定)。
        // ★ 【长什么样】与【能不能按】是两件事,由 ConvSpec 分别给:
        //   此前它们被绑在同一个 bool 上,于是"按钮灰了但回车照发"——那个禁用纯属做样子。
        //   也因此这个共享的输入区不再直接摸 TheApp.Translation(分层泄漏)。
        var send = spec.SearchIcon ? SearchSendButton(SendCurrent) : Ui.Primary("发送", (_, _) => SendCurrent());
        send.Height = 40;
        // ★★ 发送能不能按是个【会变的条件】,不是建界面那一刻算一次就完事:
        //   进翻译空间时目标池是空的 -> 按钮灰掉,之后把语言拖进去按钮还是灰的(用户实测)。
        //   所以存【谓词】而不是当时的布尔答案,由 RefreshSendEnabled() 统一刷。
        _sendBtn = send;
        _canSend = spec.CanSend;
        _blockReason = spec.BlockReason;
        _fallback = spec.Fallback is null ? null : draft => spec.Fallback(this, draft);
        _answerPending = spec.Fallback is null ? null : AnswerPendingChoice;
        RefreshSendEnabled();
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

        // 发不出去的原因(翻译空间:目标池里只剩输入语言自己)
        if (_sendBlockedHint is { Length: > 0 } blocked)
        {
            var w = Ui.Caption(blocked);
            w.SetResourceReference(TextBlock.ForegroundProperty, "RiskDanger");
            w.Margin = new Thickness(0, 0, 0, 6);
            w.TextWrapping = TextWrapping.Wrap;
            area.Children.Add(w);
        }

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

        // ★★★ 2026-08-05:模型**已经接入**(P4-S11)。这里原来无条件印着
        //   「AI 模型尚未接入(P4)—— 消息会记在本机,现在还不会有回答。」
        //   接入当晚它就变成了**界面在说假话** —— 用户刚跟模型聊完,底下还写着它不会回答。
        //   ★ 这正是本项目最恨的形状,而且方向最坏:一句**曾经为真**的话没跟着改。
        //     (与 PDF 预览那次同款:接上了还留着那句『尚未接入』就是界面在骗人。)
        //
        //   ★ 新判据按【真实前提】分三层,每层只说自己那件事:
        //     · 没配对 → 根本没有中枢可问(这条仍然为真);
        //     · 配对了但主机不在线 → 中枢够不着;
        //     · 都正常 → **什么都不说**。没有坏消息就不该占一行 ——
        //       常驻的提示会被当成背景噪声,真出事那天也就没人看了。
        if (!TheApp.Hub.IsPaired)
        {
            var noHub = Ui.Caption("还没有配对到中枢 —— 消息会记在本机,但没有 AI 可问。到「设备」里完成配对。");
            noHub.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            noHub.Margin = new Thickness(2, 0, 2, 6);
            area.Children.Add(noHub);
        }
        else if (TheApp.Hub.State != HubState.Online)
        {
            var off = Ui.Caption("主机未开启 —— AI 在主机上运行,它得在线才答得了。");
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

    /// <summary>
    /// 把焦点交给输入框 —— 但只在它【真的在可视树上】的时候。
    /// ★ 占位工作空间(assets/courses/...)从来不建输入区,_input 是构造时那个从未进树的空控件,
    ///   对它 Focus() 只会静默返回 false,焦点落空 = 整页键盘敲不进任何地方(纪律里"没有输入框就不聚焦"
    ///   说的正是这种情况:不聚焦,而不是聚焦到一个不存在的东西上)。
    /// </summary>
    // 发送键与它的前置条件。★ 存谓词而不是存布尔:布尔是"当时的答案",谓词才是"问题本身"。
    Button? _sendBtn;
    Func<bool>? _canSend;
    Func<string, string?>? _blockReason;
    /// <summary>兜底级联:处理了就返回 true(这次输入不再当普通消息发出去)。</summary>
    Func<string, bool>? _fallback;
    /// <summary>把一句话当作"回答上一条语言提问";认出来并记上了就返回 true。</summary>
    Func<string, bool>? _answerPending;
    /// <summary>发不出去时的原因(显示在输入框上方)。目标池一变或下次成功发送就清掉。</summary>
    string? _sendBlockedHint;

    /// <summary>按当前条件刷新发送键的可用状态。目标池一变就会被叫到。</summary>
    void RefreshSendEnabled()
    {
        // 目标池变了 -> 之前那条"没有可翻目标"的解释可能已经不成立,别赖着不走
        if (_sendBlockedHint is not null && _blockReason?.Invoke(_draft) is null)
        {
            _sendBlockedHint = null;
            BuildConversation();
            return;
        }
        if (_sendBtn is null) return;
        var ok = _canSend is null || _canSend();
        _sendBtn.IsEnabled = ok;
        _sendBtn.Opacity = ok ? 1 : 0.45;
    }

    /// <summary>正要跳去的那条消息(跳完清空)。用稳定标识,归档来回之后仍指向同一条。</summary>
    string? _jumpToKey;
    /// <summary>这次重建是跳到某条历史引起的,别再滚到最末尾。</summary>
    bool _suppressScrollToEnd;

    /// <summary>
    /// 从翻译历史点一条 -> 选中它所在的会话、重建、滚到那条消息并闪一下。
    /// ★ 用消息的稳定标识而不是下标:温层归档来回一次,下标就全变了。
    /// </summary>
    void OnJumpToHistory(string sessionId, string key)
    {
        if (_wsKey != "translation") return;          // 只有翻译空间的历史,别的空间不该被它牵着走
        if (TheApp.Chat.Find(sessionId) is null) return;
        _sessionId = sessionId;
        _jumpToKey = key;
        BuildSessions();
        BuildConversation();
    }

    void FocusInputIfPresent()
    {
        if (_input.IsLoaded) _input.Focus();
    }

    void BuildConversation()
    {
        // ★ 焦点纪律(用户裁定):键盘焦点只归【可编辑的输入位】。会话区一重建,_input 就是一个
        //   全新的 TextBox(494 行),旧的那个被丢弃 —— 而 Chat.Send 是【同步】触发 Changed
        //   -> OnChatChanged -> 这里重建,所以每发一条消息焦点持有者就换一次。
        //   不接住的话:发完第一条就得重新点一下输入框才能发第二条。
        //   ★ 只在【焦点原本就在旧输入框里】时才还回去 —— 否则会从别处(比如抽屉里的编辑器)抢焦点。
        //   ★★ 2026-08-03 起还要认 _justSent:"点空白处取消聚焦"是隧道 + 按下,
        //     而发送键挂在 Click(松开)上 —— 焦点会先被停走,这里再嗅探就恒为 false,
        //     于是"发完第一条得重新点一下输入框"那个老 bug 会原样复发。用鼠标发送时靠意图,不靠嗅探。
        var refocus = _input.IsKeyboardFocusWithin || _justSent;
        _justSent = false;
        BuildConversationCore();
        if (refocus) Dispatcher.BeginInvoke(new Action(FocusInputIfPresent), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void BuildConversationCore()
    {
        // ★★ 顺序不能动:只读是【数据状态】,和在哪个工作空间无关,所以必须排在工作空间分流【之前】。
        //   排在后面出过事:翻译空间(以及其余占位空间)先 return 掉,只读判断永远轮不到 ——
        //   于是在已删除/已完成的项目里,输入框照样能编辑、回车照样把消息写进去
        //   (ChatCenter.Send 只按 sessionId 找会话,不看项目删没删);
        //   更糟的是该项目下若没有可见会话,发送还会顺手在【回收站里的项目】下新建一条会话。
        //   界面当时是自相矛盾的:右上角「新建会话」被隐藏了,却让你往删掉的会话里打字。
        if (ReadOnly) { _conv.Content = BuildReadonlyProject(); return; }

        // 未接入 AI 的工作空间:同样的会话/项目外壳,但中间是占位,不做假界面。
        // ★ 它说的是"这个空间还没有 AI 能力"(正确形态 = 没有输入框),不是"你还没开始聊",
        //   所以不能并进空态提示 —— 那会让界面在接入后继续对用户撒谎。
        // ★ 占位空间也走幽灵壳:幽灵按钮在每个空间都给(2026-08-01 裁定),
        //   按下去只有圈变实线、会话区纹丝不动 —— 那是按钮在说假话。
        if (_wsKey is not ("chat" or "translation"))
        {
            _conv.Content = ConvShell(PlaceholderCenter(), InGhost);
            return;
        }

        _conv.Content = BuildConvPanel(SpecFor(_wsKey));
    }

    /// <summary>
    /// 会话面板的【全部】工作空间差异都在这里。默认值 = 聊天空间的现状 ——
    /// new ConvSpec() 生成的界面必须与聊天空间逐像素相同。
    /// </summary>
    sealed record ConvSpec
    {
        /// <summary>空态形态:居中大标题(聊天,像 GPT)/ 输入框始终贴板块底部(翻译,用户裁定)。</summary>
        public bool HeroEmptyState { get; init; } = true;
        /// <summary>居中态内容盒宽度。★ 此前 MaxWidth 与 Width 各写了一遍 640。</summary>
        public double HeroWidth { get; init; } = 640;
        /// <summary>空态大标题。返回 null/空 = 不画标题层(翻译就没有)。</summary>
        public Func<string?> EmptyTitle { get; init; } = () => Greetings.ChatOpener(DateTime.Now);
        /// <summary>空态里【项目归属提示之后】追加的空间专属说明。</summary>
        public IReadOnlyList<string> EmptyNotes { get; init; } = Array.Empty<string>();
        /// <summary>发送键【长什么样】。只管外观,不管能不能发。</summary>
        public bool SearchIcon { get; init; }
        /// <summary>
        /// 发送的【前置条件】。null = 永远可发。
        /// ★ 与外观完全独立:此前两件事被绑在一个 bool 上,于是"按钮灰了但回车照发"。
        /// </summary>
        public Func<bool>? CanSend { get; init; }
        /// <summary>挂在会话卡【下面】的固定高度附属条。★ 用 Func:每次重建都要新建一个。</summary>
        public Func<FrameworkElement>? BottomAccessory { get; init; }
        /// <summary>
        /// 给定当前草稿,返回"这次为什么发不出去";没问题返回 null。
        /// ★ 与 CanSend 的区别:CanSend 只看得见静态条件(目标池空不空),
        ///   这条要看【输入内容】—— 比如目标池里只剩输入语言自己。
        ///   放在这里是为了让共享视图完全不知道"翻译"这回事。
        /// </summary>
        public Func<string, string?>? BlockReason { get; init; }
        /// <summary>
        /// 目标池算不出目标时的兜底级联。处理掉了就返回 true(这次输入不再当普通消息发出去)。
        /// 传 ChatView 进去是为了让它能写会话、刷界面 —— 规则本身在 TranslationFallbacks 里,
        /// 这里只是把"谁来执行"接上。
        /// </summary>
        public Func<ChatView, string, bool>? Fallback { get; init; }
        /// <summary>草稿变了(文本 + 是否挂着非图片附件)。翻译空间据此判断输入形态。</summary>
        public Action<string, bool>? OnDraftChanged { get; init; }
        /// <summary>这个空间左上角要不要放【场景切换】(目前只有翻译空间有三个场景)。</summary>
        public bool ModeSwitch { get; init; }
    }

    /// <summary>_wsKey -> ConvSpec 的【唯一】映射点。别处不再拿 _wsKey 和字面量比。</summary>
    // ★ 不再 static:BottomAccessory 要捕获本视图的 _sessionId
    ConvSpec SpecFor(string wsKey) => wsKey switch
    {
        "translation" => new ConvSpec
        {
            HeroEmptyState = false,          // 用户裁定:翻译的输入框始终贴板块底部,不居中
            EmptyTitle = () => null,
            EmptyNotes = new[]
            {
                "输入要翻译的内容,按下放大镜。",
                "翻成哪些语言由下面的【目标池】决定;详细程度由左边的竖条决定。",
            },
            SearchIcon = true,
            ModeSwitch = true,
            CanSend = () => ((App)Application.Current).Translation.Targets.Count > 0,
            BottomAccessory = () => new TranslationBar(
                // 选中的是同传会话时,「开始同传」在它里面继续而不是再建一条(用户裁定 2026-08-02)
                () => _sessionId is { } sid ? TheApp.Chat.Find(sid) : null),
            BlockReason = TranslationBlockReason,
            OnDraftChanged = (draft, hasFileAttachment) =>
                ((App)Application.Current).Translation.SetShape(TextShapes.Classify(draft, hasFileAttachment)),
            Fallback = (view, draft) => view.RunTranslationFallback(draft),
        },
        _ => new ConvSpec(),                 // 聊天:全默认
    };

    /// <summary>
    /// 翻译工作空间左上角的【三个场景】切换。★ 位置是用户指定的:会话板块左上角。
    /// 第三个先留空 —— 用户还没想好做什么,但入口先占住,免得将来加进来时又要挪版面。
    /// </summary>
    FrameworkElement ModeSwitcher()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var (mode, icon, name) in new[]
                 {
                     (TranslationMode.Text, IconName.Translation, "文字翻译"),
                     (TranslationMode.Interpret, IconName.Mic, "同声传译"),
                     (TranslationMode.FileTrans, IconName.File, "文件翻译"),
                     (TranslationMode.I18n, IconName.Extensions, "多语言"),
                 })
        {
            var on = TheApp.Interpret.Mode == mode;

            // ★ 只放图标,不放文字(用户裁定)。
            // ★★ 命中区是一个【透明的方块】,图标只是画在里面的东西 ——
            //   拿图标本身当按钮的话,点在笔画之间的空隙上就点不中(自绘图标是描边路径,
            //   中间是空的)。透明块把整个方形都变成可点区域,手感才对。
            var glyph = Icons.Make(icon, 17, on ? "FgOnAccent" : "FgSecondary");
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            glyph.IsHitTestVisible = false;                 // 命中交给外面那层透明块
            var hit = new Grid { Width = 30, Height = 26, Background = Brushes.Transparent };
            hit.Children.Add(glyph);

            var b = new Border
            {
                Child = hit, Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand, BorderThickness = new Thickness(1),
                ToolTip = name,
            };
            b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            if (on)
            {
                b.SetResourceReference(Border.BackgroundProperty, "Accent");
                b.SetResourceReference(Border.BorderBrushProperty, "Accent");
            }
            else
            {
                b.Background = Brushes.Transparent;
                b.SetResourceReference(Border.BorderBrushProperty, "Border");
                b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
                b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            }
            var captured = mode;
            b.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                // ★ 只有【真的换了档】才算"换了一次"(复核 2026-08-03):点已经高亮的那一枚,
                //   SetMode 自己会早退、场景一动没动,这时候把幽灵连内容一起抹掉是无妄之灾。
                var was = TheApp.Interpret.Mode;
                TheApp.Interpret.SetMode(captured);
                if (was == captured || !EndGhostOnSceneSwitch()) BuildConversation();
            };
            row.Children.Add(b);
        }
        return row;
    }

    /// <summary>唯一的会话面板。聊天与翻译都由它生成,差异全部走 ConvSpec。</summary>
    FrameworkElement BuildConvPanel(ConvSpec spec)
    {
        // ★★ 幽灵态要在【每一个场景】都看得见(用户裁定 2026-08-03):
        //   同传/文件翻译/多语表/回信 都是提前 return 的分支,以前用的是普通卡 ——
        //   手里真拿着一条幽灵会话,屏幕上却一点痕迹都没有。所以算在最前面。
        var isGhost = _sessionId is { } gsid && TheApp.Chat.Find(gsid)?.Ghost == true;
        // ★ 翻译空间的三个场景:切到同传时整个会话区换成同传面板(输入框那一套不适用)。
        if (spec.ModeSwitch)
        {
            var mode = TheApp.Interpret.Mode;
            if (mode != TranslationMode.Text)
            {
                var body = new DockPanel { LastChildFill = true };
                var head = ModeSwitcher();
                DockPanel.SetDock(head, Dock.Top);
                body.Children.Add(head);
                // ★ fail-closed(审计 2026-07-31):只有【真正的同传会话】才把转写交给 InterpretPanel。
                //   否则在同传场景下点开一条普通文字翻译会话,客户端自己那条
                //   "AI 未接入"系统说明会被当成【对方说的话】渲染到左边。
                // ★ 进行中优先绑【这一场】的会话(审计 2026-08-02):原先只看"当前打开的",
                //   开始那一刻面板可能还挂在旧会话上,转写到时候会进错地方。
                var interpSid = TheApp.Interpret.Running && TheApp.Interpret.RunningSessionId is { } rsid
                    ? rsid
                    : _sessionId is { } isid && TheApp.Chat.Find(isid)?.Interpret == true ? _sessionId : null;
                var ftSid = _sessionId is { } fsid && TheApp.Chat.Find(fsid)?.FileTrans == true ? _sessionId : null;
                body.Children.Add(mode == TranslationMode.Interpret ? new InterpretPanel(interpSid)
                    : mode == TranslationMode.FileTrans ? new FileTransPanel(ftSid)
                    : mode == TranslationMode.I18n ? BuildI18nPanel()
                    : (FrameworkElement)ReservedScenePlaceholder());
                var only = ConvShell(body, isGhost);
                if (spec.BottomAccessory is null) return only;
                var wrap = new DockPanel { LastChildFill = true };
                var acc = spec.BottomAccessory();
                acc.Margin = new Thickness(0, 10, 0, 0);
                DockPanel.SetDock(acc, Dock.Bottom);
                wrap.Children.Add(acc);
                wrap.Children.Add(only);
                return wrap;
            }
        }

        // ★ 聊天空间的第二功能【回信】(D61):场景开着就整区换成 回信面板 + 回信设置条,
        //   顶部给「聊天 / 回信」两枚切换 chip(与翻译空间的场景切换同一语法)。
        if (_wsKey == "chat")
        {
            _replyScene = TheApp.Reply.SceneReply;
            var chatHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            // ★ 图标切换(用户裁定 2026-08-03):与翻译工作区的场景切换同一语法同一位置。
            // ★★ 尺寸/间距/命中区一律拄 ModeSwitcher(上方 1050 行):两边长得不一样的话,
            //   切空间时那一排按钮会股骨头地变形。命中区是透明方块 —— 描边图标中间是空的,
            //   拿图标本身当按钮会点不中笔画之间的空隙。
            void SceneChip(IconName icon, string tip, bool on, Action click)
            {
                var glyph = Icons.Make(icon, 17, on ? "FgOnAccent" : "FgSecondary");
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                glyph.VerticalAlignment = VerticalAlignment.Center;
                glyph.IsHitTestVisible = false;
                var hit = new Grid { Width = 30, Height = 26, Background = Brushes.Transparent };
                hit.Children.Add(glyph);
                var c = new Border { Child = hit, Margin = new Thickness(0, 0, 6, 0),
                                     Cursor = Cursors.Hand, BorderThickness = new Thickness(1), ToolTip = tip };
                c.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
                if (on) { c.SetResourceReference(Border.BackgroundProperty, "Accent"); c.SetResourceReference(Border.BorderBrushProperty, "Accent"); }
                else
                {
                    c.Background = Brushes.Transparent;
                    c.SetResourceReference(Border.BorderBrushProperty, "Border");
                    c.MouseEnter += (_, _) => c.SetResourceReference(Border.BackgroundProperty, "BgHover");
                    c.MouseLeave += (_, _) => c.Background = Brushes.Transparent;
                }
                c.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    var was = TheApp.Reply.SceneReply;
                    click();
                    // 同上:点已经高亮的那一枚 = 什么都没换,不该抹幽灵
                    if (was != TheApp.Reply.SceneReply) EndGhostOnSceneSwitch();
                };
                chatHead.Children.Add(c);
            }
            SceneChip(IconName.Chat, "聊天", !_replyScene, () => TheApp.Reply.SetScene(false));
            SceneChip(IconName.Mail, "回信", _replyScene, () => TheApp.Reply.SetScene(true));
            if (_replyScene)
            {
                // 绑定当前选中的回信会话(没选中 = 草稿态,首笔编辑自建会话并选中)
                TheApp.Reply.SetSession(_sessionId is { } rsid2 && TheApp.Chat.Find(rsid2)?.ReplyLetter == true ? _sessionId : null);
                // ★ 与翻译空间同构(用户裁定 2026-08-03):会话卡框【切换条 + 四个内容板块】,
                //   用语/对方信息/我方信息 是下方【独立】的卡,不套在会话卡里。
                // ★★ 切换条必须在卡【内】(用户裁定 2026-08-03):它属于会话板块,不是页面级导航。
                //   翻译空间就是这么排的(上方 1109-1126):ConvCard 包的是【切换条 + 面板】整体,
                //   底部设置条才在卡外。留在卡外时它悬在页面底色上,和 ReplyBar 混成一层。
                var rBody = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(chatHead, Dock.Top);
                rBody.Children.Add(chatHead);
                rBody.Children.Add(new ReplyPanel());
                // 走 ConvShell 而不是裸 ConvCard:幽灵会话的虚线框与提示在这一场景也得有
                var rCard = ConvShell(rBody, isGhost);
                // DockPanel 顺序纪律:Dock 的先 Add、LastChildFill 的最后 Add —— 写反了卡不填充
                var rWrap = new DockPanel { LastChildFill = true };
                var rBar = new ReplyBar { Margin = new Thickness(0, 10, 0, 0) };
                DockPanel.SetDock(rBar, Dock.Bottom);
                rWrap.Children.Add(rBar);
                rWrap.Children.Add(rCard);
                return rWrap;
            }
            _chatSceneHead = chatHead;   // 聊天场景:切换条交给下面的常规装配挂在顶部
        }

        var hasMsgs = _sessionId is not null && TheApp.Chat.MessagesOf(_sessionId).Any();
        // ★ 居中态 = 【这个空间要居中】且【确实没有消息】。附件放哪、横幅浮不浮、
        //   滑动动画演不演,全都由它推出来 —— 此前是三处各写一遍,四种组合里两种是坏的。
        var heroNow = spec.HeroEmptyState && !hasMsgs;

        var inputArea = BuildInputArea(attachmentsBelow: heroNow, spec);
        FrameworkElement inner;

        if (!hasMsgs)
        {
            // ★ Stretch + MaxWidth 而不是 Center:取满可用宽度、到 HeroWidth 封顶,窄了自己缩。
            //   用 Center 的话盒子会按【最宽的那个子元素】收缩,输入行就缩成标题那么宽;
            //   用固定 Width 又会在窄窗口下撑破卡片、把发送键裁出去。两个坑都在渲染诊断里现过形。
            var box = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = spec.HeroWidth };
            if (heroNow) box.VerticalAlignment = VerticalAlignment.Center;   // 居中态才竖直居中
            else box.Margin = new Thickness(0, 24, 0, 0);

            if (spec.EmptyTitle() is { Length: > 0 } title)
            {
                var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center };
                t.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                box.Children.Add(t);
                box.Children.Add(new Border { Height = 6 });
            }

            // ★ 项目归属提示对【所有空间】都画:它讲的是外壳自己的状态(_projectId),与空间无关。
            //   此前只有聊天有 —— 于是在翻译空间可能在不知情的项目上下文里发请求,事后找不到。
            var hint = Ui.Caption(_projectId is null
                ? "没选项目直接聊 = 普通会话。右侧箭头可选项目。"
                : "当前在项目「" + (TheApp.Projects.Find(_projectId!)?.Title ?? "") + "」下,新消息会归到该项目。");
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.TextAlignment = TextAlignment.Center;
            box.Children.Add(hint);

            for (int i = 0; i < spec.EmptyNotes.Count; i++)
            {
                var note = i == 0 ? Ui.Body(spec.EmptyNotes[i], muted: true) : Ui.Caption(spec.EmptyNotes[i]);
                note.HorizontalAlignment = HorizontalAlignment.Center;
                note.TextAlignment = TextAlignment.Center;
                note.Margin = new Thickness(0, i == 0 ? 8 : 4, 0, 0);
                box.Children.Add(note);
            }

            if (heroNow)
            {
                box.Children.Add(new Border { Height = 16 });
                // ★ 只给【上限】不给固定宽度:写死 Width 的话窗口一窄就撑破卡片,
                //   发送键被裁在外面(渲染诊断当场看到;最小窗口尺寸下必现)。
                //   宽度由 box 的 MaxWidth 兜着,窄了自己缩。
                inputArea.HorizontalAlignment = HorizontalAlignment.Stretch;
                box.Children.Add(inputArea);
                inner = box;
            }
            else
            {
                // 贴底态:提示留在上方消息区的位置,输入框照常 Dock 到底
                inner = DockWithInput(MessageScroller(box), inputArea, slideFromCenter: false);
            }
        }
        else
        {
            var msgs = new StackPanel();
            FillMessages(msgs, _sessionId!, animate: true);
            inner = DockWithInput(MessageScroller(msgs), inputArea, slideFromCenter: _wasEmptyState);
        }

        if (spec.ModeSwitch)
        {
            var withModes = new DockPanel { LastChildFill = true };
            var head = ModeSwitcher();
            DockPanel.SetDock(head, Dock.Top);
            withModes.Children.Add(head);
            withModes.Children.Add(inner);
            inner = withModes;
        }
        // 聊天空间:顶部挂「聊天/回信」切换条(D61,与翻译的场景切换同一位置同一语法)
        if (_chatSceneHead is { } sceneHead)
        {
            _chatSceneHead = null;
            var withScene = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(sceneHead, Dock.Top);
            withScene.Children.Add(sceneHead);
            withScene.Children.Add(inner);
            inner = withScene;
        }

        _wasEmptyState = heroNow;   // ★ 不是 !hasMsgs:贴底态永远没有"从居中滑下来"这一说
        var card = ConvShell(inner, isGhost);
        if (spec.BottomAccessory is null) return card;

        var root = new DockPanel { LastChildFill = true };
        var bar = spec.BottomAccessory();
        bar.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(card);
        return root;
    }

    /// <summary>第三个场景的占位。★ 如实说"还没定",不摆一个像功能的空壳。</summary>
    static FrameworkElement ReservedScenePlaceholder()
    {
        var box = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var t = Ui.Body("第三个场景还没定。", muted: true);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        var c = Ui.Caption("入口先占住位置 —— 想好做什么再填,免得那时又要挪版面。");
        c.HorizontalAlignment = HorizontalAlignment.Center;
        box.Children.Add(t);
        box.Children.Add(c);
        return box;
    }

    /// <summary>消息区在上、输入框 Dock 到底;需要时演一段"从居中滑到底部"。</summary>
    FrameworkElement DockWithInput(ScrollViewer scroll, FrameworkElement inputArea, bool slideFromCenter)
    {
        var dock = new DockPanel { LastChildFill = true };
        var inputWrap = new Border { Child = inputArea, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(inputWrap, Dock.Bottom);
        dock.Children.Add(inputWrap);
        dock.Children.Add(scroll);

        var skipEnd = _suppressScrollToEnd;
        _suppressScrollToEnd = false;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!skipEnd) scroll.ScrollToEnd();   // ★ 必须排在下面那句 return 之前
            if (!slideFromCenter) return;
            // ★ 从"空会话居中输入框"变成"底部输入框"时给一段动画,而不是硬切(用户裁定):
            //   输入框从原来的居中位置【滑到底部】,消息区同时淡入。
            var h = _conv.ActualHeight;
            var startY = -Math.Max(0, (h - inputWrap.ActualHeight) / 2 - 10);
            if (startY >= -1) return;             // 高度还没算出来就别硬演
            var t = new TranslateTransform { Y = startY };
            inputWrap.RenderTransform = t;
            t.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(startY, 0, TimeSpan.FromMilliseconds(520))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
            scroll.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
                { BeginTime = TimeSpan.FromMilliseconds(120), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        }), System.Windows.Threading.DispatcherPriority.Loaded);
        return dock;
    }

    // 只读浏览:已删除 / 已完成项目。灰化会话内容,底部换成对应的动作按钮。
    /// <summary>
    /// 唯一的「铺消息列表」。★ 此前有【三份】各写各的(聊天 / 翻译 / 只读浏览),
    /// 于是归档入口、出现动画这些只长在聊天那一份上,另外两处悄悄退化 ——
    /// 用户看到的是"翻译空间的旧消息没了,而且没有任何按钮能取回"。
    ///
    /// animate=false 时不播动画、也【不写】_seenMsgCount:只读浏览不该改"看过几条"的账。
    /// </summary>
    void FillMessages(StackPanel msgs, string sessionId, bool animate)
    {
        // ★ 分层存储:更早的消息在温层(另存文件),平时不加载 —— 顶部给个"加载更早"入口。
        //   原文一直都在,只是不占内存/上下文(见 SessionArchive)。归档【不分工作空间】,
        //   所以这个入口也必须对所有空间都在。
        var older = TheApp.Chat.UnloadedArchivedCount(sessionId);
        if (older > 0)
        {
            var more = Chip($"↑ 加载更早的 {older} 条", "FgSecondary", () => TheApp.Chat.LoadArchived(sessionId));
            more.HorizontalAlignment = HorizontalAlignment.Center;
            more.Margin = new Thickness(0, 0, 0, 8);
            msgs.Children.Add(more);
        }

        var all = TheApp.Chat.MessagesOf(sessionId).ToList();
        // ★ 新发出的消息才播出现动画:只给【这次新增的那几条】,
        //   旧消息重建时不再重复动(否则每次刷新整屏乱跳)。
        var seen = animate && _seenMsgCount.TryGetValue(sessionId, out var n) ? n : all.Count;
        for (int i = 0; i < all.Count; i++)
        {
            var bubble = Bubble(all[i], i);
            if (animate && i >= seen) AnimateIn(bubble, delayMs: (i - seen) * 70);
            msgs.Children.Add(bubble);
        }
        if (animate) _seenMsgCount[sessionId] = all.Count;

        // 从翻译历史跳过来的:滚到那一条并闪一下,不然用户不知道自己落在哪
        if (_jumpToKey is { } want)
        {
            _jumpToKey = null;
            // ★ 跳转这一次【不要再滚到底】:DockWithInput 也排了一个 ScrollToEnd,
            //   两者同优先级、它排在后面,不压住的话跳过去又被拽回最末尾(用户实测)。
            _suppressScrollToEnd = true;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].StableKey != want) continue;
                var target = msgs.Children[msgs.Children.Count - all.Count + i] as FrameworkElement;
                if (target is null) break;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    target.BringIntoView();
                    // ★ 用装饰层画框,不动内容的透明度 —— 那种"闪一下"的写法会收在起点上
                    //   把元素永久按在 35% 不透明度(设置页那边实测过,看着就是变灰了)。
                    RevealHighlight.Show(target);
                }), System.Windows.Threading.DispatcherPriority.Loaded);
                break;
            }
        }
    }

    /// <summary>消息区的滚动壳。★ 必须 PassThrough:嵌套 ScrollViewer 会把滚轮吃掉。</summary>
    static ScrollViewer MessageScroller(UIElement msgs)
        => new ScrollViewer
        {
            Content = msgs,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough();

    /// <summary>
    /// 唯一的会话卡工厂。★ 内边距 12 此前在四个地方各写了一遍,还有一处漏写吃到 Ui.Card 的默认 16
    /// (切到占位工作空间时卡片内边距会跳一下)。sunken=true 给只读态:整块偏灰。
    /// </summary>
    static Border ConvCard(UIElement inner, bool sunken = false)
    {
        var c = Ui.Card(inner, new Thickness(0));
        c.Padding = new Thickness(12);
        if (sunken) c.SetResourceReference(Border.BackgroundProperty, "BgSunken");
        return c;
    }

    FrameworkElement BuildReadonlyProject()
    {
        var p = CurrentProject!;
        var deleted = ViewingDeletedProject;

        // 内容:选中会话则显示其记录(只读),否则提示从右侧选会话
        FrameworkElement body;
        if (_sessionId is not null && TheApp.Chat.MessagesOf(_sessionId).Any())
        {
            var msgs = new StackPanel();
            FillMessages(msgs, _sessionId!, animate: false);   // 只读浏览:不播动画,也不改"看过几条"的账
            var roScroll = MessageScroller(msgs);
            // ★ 打开就停在最后一条 —— 此前只读态整段没有 ScrollToEnd,一进来停在最顶上
            Dispatcher.BeginInvoke(new Action(() => roScroll.ScrollToEnd()), System.Windows.Threading.DispatcherPriority.Loaded);
            body = roScroll;
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

        return ConvCard(dock, sunken: true);   // 整块偏灰,提示只读
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
    // ★ 折叠状态的键走消息自己的稳定标识,【不能用下标】——
    //   "加载更早"会把所有下标整体后移,展开的那条就跳到别人身上了(见 ChatMessage.StableKey)。
    static string BubbleKey(ChatMessage m) => m.StableKey;

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

    /// <summary>
    /// 会话面板外壳:普通 = 实心卡;幽灵 = 虚线边框 + 一枚「不保留记录」的标。
    ///
    /// ★★ 进出幽灵态【一个像素都不许动】(用户裁定 2026-08-03:
    ///   "幽灵会话多出来的提示会挤开功能切换按钮和其他板块,不应该。原本的聊天板块也有轻微的布局跳跃")。
    ///   两处曾经在动:
    ///   ① 提示原本是 Dock.Top 的一行,占布局 —— 进幽灵就把下面所有东西整体推下去;
    ///      现在改成【压在上边框上的一枚标】(Grid 覆盖层 + 不参与命中),零布局代价。
    ///   ② 更隐蔽的那 1px:普通卡走 Ui.Card,BorderThickness = 1;幽灵壳原来没有边框 ——
    ///      内容盒因此左右各差 1px。虚线是画在覆盖层上的,所以这里补一圈【透明】边框把盒模型对齐。
    /// </summary>
    FrameworkElement ConvShell(FrameworkElement inner, bool ghost)
    {
        if (!ghost) return ConvCard(inner);

        var host = new Border
        {
            Child = inner, Padding = new Thickness(12),
            BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent,   // 见上:对齐盒模型
        };
        host.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        host.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");

        var r = TryFindResource("RadiusMd") is CornerRadius cr ? cr.TopLeft : 8;
        var dash = new System.Windows.Shapes.Rectangle
        {
            StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 }, Fill = Brushes.Transparent,
            RadiusX = r, RadiusY = r, IsHitTestVisible = false,
        };
        dash.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent");

        var banner = Ui.Caption("幽灵会话 · 不保留记录、不纳入记忆");
        banner.TextAlignment = TextAlignment.Center;
        banner.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        // 标压在上边框上(像图例):自己带一块与卡同色的底,把底下那截虚线盖掉;
        // 居中摆 —— 场景切换条在左上角,两者不会打架。
        var tag = new Border
        {
            Child = banner, Padding = new Thickness(8, 0, 8, 0), IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -7, 0, 0),
        };
        tag.SetResourceReference(Border.BackgroundProperty, "BgSurface");

        var g = new Grid();
        g.Children.Add(host);
        g.Children.Add(dash);
        g.Children.Add(tag);
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
        return ConvCard(box);   // ★ 此前这里漏写 Padding,吃 Ui.Card 的默认 16 —— 切到占位空间卡片会跳一下
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
            if (m.ChoiceOptions is not { Count: > 0 }) return sys;

            // ★ 带选项的提问(目前只有"翻成哪种语言"用到)。
            //   答过之后按钮【置灰但保留】——用户裁定 disable 掉,而不是让它消失:
            //   留着才看得出当时问了什么、选了哪个。
            var answered = m.ChoiceAnswer is { Length: > 0 };
            var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 6) };
            foreach (var code in m.ChoiceOptions)
            {
                var picked = answered && m.ChoiceAnswer == code;
                var btn = Chip(Languages.NameOf(code), picked ? "Accent" : "FgSecondary", () =>
                {
                    if (m.MessageId is { } mid) ApplyChoice(mid, code);
                });
                btn.Margin = new Thickness(0, 0, 6, 0);
                if (answered) { btn.IsEnabled = false; btn.Opacity = picked ? 0.85 : 0.4; }
                row.Children.Add(btn);
            }
            var box = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
            sys.Margin = new Thickness(0, 0, 0, 4);
            box.Children.Add(sys);
            box.Children.Add(row);
            return box;
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
                var key = BubbleKey(m);
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
        // ★★ 复用只在【本空间】的空会话上成立(审计 2026-08-01)。
        //   SessionsOf 按 ProjectId 取、不看工作空间,而项目可以同时挂在多个空间下。
        //   不加这一条,在翻译空间的项目里建了一条没打字的空会话之后,到聊天空间进同一个项目按 +,
        //   会直接复用【那条翻译空会话】,你打的聊天内容就落进它 ——
        //   翻译历史按 WorkspaceKey 取、不看 ProjectId,于是聊天内容成了翻译记录。
        //   这条污染路径【一次都不经过会话行】,光靠列表上的标记拦不住它。
        var empty = inCtx.FirstOrDefault(s => s.WorkspaceKey == _wsKey && !TheApp.Chat.MessagesOf(s.SessionId).Any());
        if (empty is not null)
        {
            _sessionId = empty.SessionId;
            BuildSessions();
            BuildConversation();
            if (_sessionRows.TryGetValue(empty.SessionId, out var row)) Ui.Shake(row);
            FocusInputIfPresent();
            return;
        }

        var scope = _projectId is null ? ProjectScope.Personal : (TheApp.Projects.Find(_projectId!)?.Scope ?? ProjectScope.Personal);
        var s2 = TheApp.Chat.NewSession(_projectId, _wsKey, scope);
        _sessionId = s2.SessionId;
        BuildSessions();
        BuildConversation();
        FocusInputIfPresent();
    }

    /// <summary>
    /// 翻译空间的出手前检查:目标池里只剩输入语言自己时,这次翻译【没有任何目标】。
    /// 返回一句该说的话;没问题返回 null。
    /// ★ 语种判不出来(拉丁字母)时不拦 —— 那时我们并不知道它是不是池内那一个,
    ///   宁可交给 AI 判,也不要凭猜测拦下用户一次正当的翻译(与 Languages.Detect 同一条纪律)。
    /// </summary>
    static string? TranslationBlockReason(string draft)
    {
        var st = ((App)Application.Current).Translation;
        if (st.Targets.Count == 0) return "目标池是空的 —— 先从右边把语言拖进来。";
        var plan = st.Plan(draft);
        if (!plan.NothingToDo) return null;
        var lang = plan.InputLang is { } c ? Languages.NameOf(c) : "这段文字";
        return $"目标池里只有{lang},而你输入的也是{lang} —— 没有可翻的目标语言。再拖一个语言进目标池。";
    }

    /// <summary>
    /// 跑一遍翻译兜底级联(规则见 TranslationFallbacks)。处理掉了返回 true。
    /// 三条出口:翻成母语 / 翻成英语 / 在对话里问 —— 前两条会把语言加进目标池并写一条说明,
    /// 第三条写一条带按钮的提问,等用户点或回话。
    /// </summary>
    bool RunTranslationFallback(string draft)
    {
        var st = TheApp.Translation;
        if (st.Targets.Count == 0) return false;          // 空池不是级联管的事
        var plan = st.Plan(draft);
        var f = TranslationFallbacks.Resolve(plan.Targets, plan.InputLang, TheApp.Settings.NativeLang,
                                             TheApp.Settings.TranslationPool, st.Targets);
        if (f.Kind == FallbackKind.None) return false;

        if (_sessionId is null) NewSession();
        if (_sessionId is null) return false;

        if (f.Kind == FallbackKind.Ask)
        {
            if (f.Options.Count == 0) return false;       // 没得选就别问,交回上面那条"发不出去"的解释
            _justSent = true;
            TheApp.Chat.Send(_sessionId, draft);          // 原文照常入会话,不然用户白打一遍
            TheApp.Chat.AskChoice(_sessionId, TranslationFallbacks.Explain(f, plan.InputLang!), f.Options);
            return true;
        }

        // 母语 / 英语:把它加进目标池,说明一句,然后照常把这次输入发出去
        if (f.AddToPool is { } add) st.AddTarget(add);
        TheApp.Chat.Send(_sessionId, draft);
        TheApp.Chat.AskChoice(_sessionId, TranslationFallbacks.Explain(f, plan.InputLang!), Array.Empty<string>());
        return true;
    }

    /// <summary>把一句话当作"回答上一条语言提问"。认出来并记上了返回 true。</summary>
    bool AnswerPendingChoice(string text)
    {
        if (_sessionId is null) return false;
        var q = TheApp.Chat.PendingChoice(_sessionId);
        if (q?.MessageId is not { } qid) return false;
        var code = Languages.ParseLanguage(text);
        if (code is null || !q.ChoiceOptions!.Contains(code)) return false;   // 认不出就当普通消息,不硬猜
        ApplyChoice(qid, code);
        return true;
    }

    /// <summary>记下选择:按钮置灰、语言进目标池,然后开始翻译。</summary>
    void ApplyChoice(string messageId, string code)
    {
        if (!TheApp.Chat.AnswerChoice(messageId, code)) return;
        TheApp.Translation.AddTarget(code);
        if (_sessionId is not null)
            TheApp.Chat.AskChoice(_sessionId, $"好 —— 已把{Languages.NameOf(code)}加进目标池,开始翻译。", Array.Empty<string>());
    }

    /// <summary>
    /// 刚刚用鼠标做了一件「做完还该待在输入框里」的事(发送 / 加附件 / 删附件)——
    /// 见 BuildConversation 里那段说明:焦点已被"点空白"停走,不能再靠嗅探当前焦点。
    /// ★ 必须【紧挨着真正会重建会话区的那一步】置位:置早了(比如放在 SendCurrent 第一行)
    ///   遇到空输入那条早退就永远清不掉,这面旗子会在很久以后的某次无关重建里把焦点抢走。
    /// </summary>
    bool _justSent;

    void SendCurrent()
    {
        var text = _input.Text;
        if (string.IsNullOrWhiteSpace(text) && _pending.Count == 0) return;   // 空且无附件不发

        // ★ 会话里有一条【还没答的语言提问】时,这次输入先当作"作答"处理:
        //   用户回一句语言名 -> 记上、按钮置灰、语言进目标池,然后这次输入不当消息发出去。
        if (_answerPending is { } answer && !string.IsNullOrWhiteSpace(text))
        {
            if (answer(text)) { _input.Clear(); _draft = ""; return; }
        }

        // ★ 翻译空间:目标池算不出目标时走【兜底级联】(用户裁定):
        //   母语 -> 英语 -> 在对话里问。它会自己把该加的语言加进目标池并写一条说明。
        if (_fallback?.Invoke(_draft) == true) { _input.Clear(); _draft = ""; return; }

        // 还剩一种发不出去:目标池整个是空的(那不是级联能兜的,得先拖个语言进来)
        if (_blockReason?.Invoke(_draft) is { } why)
        {
            _sendBlockedHint = why;
            BuildConversation();
            return;
        }
        _sendBlockedHint = null;
        if (_sessionId is null) NewSession();
        if (_sessionId is null) return;
        var atts = _pending.Count > 0 ? _pending.ToList() : null;
        // ★ 先清空本地待发状态,再发送 —— Chat.Send 会【同步】触发 Changed → 重建会话区;
        //   若此时 _pending 还没清,重建就会把【已经发出去】的附件又挂回输入框上(用户反馈的 bug)。
        _pending.Clear();
        _draft = "";
        _justSent = true;          // ★ 就置在这儿:上面每一条早退都不该留下这面旗子

        // ══════════════════════════════════════════════════════════════
        //  P4-S11:真的发给模型(流式)。
        //
        //  ★★ 两条路径,判据是【中枢在不在】,不是"接没接入":
        //    · 配对了 → 走真链路 SendAndAskAsync;失败逐种给不同的下一步(见 ChatOutcome.Advice)。
        //    · 没配对 → 仍走 Send 那条**诚实占位**路径(记消息 + 说明),绝不伪造回复。
        //  ★ 不在这里判"后端起没起" —— 那要发出去才知道,而猜一个"起了吧"再显示成功
        //    正是本项目最恨的形状。让它真的失败,再如实说该做什么。
        // ══════════════════════════════════════════════════════════════
        if (!TheApp.Hub.IsPaired)
        {
            TheApp.Chat.Send(_sessionId, text, atts);
            return;
        }

        var sid = _sessionId;
        // ★ 中枢当前驻留的组件 → 上下文窗口。读不到就由 TokenBudget 按最小档保守估(见那边的说明)。
        var committed = TheApp.Gpu.Snapshot?.Committed;
        _ = TheApp.Chat.SendAndAskAsync(
                sid, text, TheApp.Hub, committed, atts,
                // ★ onTick 在后台线程被调 —— 必须切回 UI 线程再刷,
                //   而且用 BeginInvoke(非阻塞):流式一秒能来几十帧,同步 Invoke 会把它自己堵死。
                onTick: () => Dispatcher.BeginInvoke(new Action(BuildConversation)))
            .ContinueWith(_ => Dispatcher.BeginInvoke(new Action(BuildConversation)),
                          TaskScheduler.Default);
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

        // ★ 菜单项里【不要当场】弹模态对话框:那会在菜单还没关完的时候把消息循环接管走,
        //   回来又整块重建输入区 —— 菜单挂靠的按钮没了,那次 Closed 就可能永远不来,
        //   于是"菜单还开着"的状态卡死,主窗口把此后每一次点击都吞掉(2026-07-30 实测点不动)。
        //   延到 Background 优先级执行:让菜单先关干净,再弹对话框。
        var menu = new ContextMenu();
        var mFile = new MenuItem { Header = "选择文件…" };
        mFile.Click += (_, _) => Dispatcher.BeginInvoke(new Action(PickFiles), System.Windows.Threading.DispatcherPriority.Background);
        var mFolder = new MenuItem { Header = "选择文件夹…" };
        mFolder.Click += (_, _) => Dispatcher.BeginInvoke(new Action(PickChatFolder), System.Windows.Threading.DispatcherPriority.Background);
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
        // ★ 与"鼠标发送"同一类(复核 2026-08-03 抓到的漏网):点「+」是点在一个 Border 上,
        //   隧道层先把焦点停走了,重建后再嗅探焦点恒为 false —— 表现就是
        //   "打半句话去加个附件,回来光标没了,得再点一次输入框"。记意图,不嗅探。
        _justSent = true;
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
            // ★★ 粘贴的截图落到【应用状态目录】而不是 %TEMP%(2026-07-31 审计,数据丢失):
            //   这张 png 是消息内容的【唯一副本】—— 路径写进 chat.json(D50)、历史气泡只从它渲染。
            //   放 %TEMP% 会同时落进两个删除范围:我们自己的「一键清爽·清理缓存」(通配 localai-clip-*.png,
            //   默认勾选、标称"安全")和 Windows 存储感知。任一清理一跑,已发消息里的截图就没了。
            //   放进 client\clips\ 与 chat.json 同处,既不在缓存通配范围,也不归 Windows 清理管。
            var clipDir = System.IO.Path.Combine(AppPaths.StateDir, "clips");
            System.IO.Directory.CreateDirectory(clipDir);
            var tmp = System.IO.Path.Combine(clipDir, "clip-" + Guid.NewGuid().ToString("N")[..8] + ".png");
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
        head.Children.Add(Chip("清空", "RiskDanger", () => { _pending.Clear(); _justSent = true; BuildConversation(); }));
        box.Children.Add(head);

        // 只展开前 5 个;其余折叠成一个"+N"卡(避免占满输入区)。
        var wrap = new WrapPanel();
        var shown = Math.Min(_pending.Count, SoftAttachLimit);
        for (int k = 0; k < shown; k++)
        {
            var item = _pending[k];   // 按对象移除,不按下标(下标闭包越界曾致闪退)
            wrap.Children.Add(AttachChip(item, onRemove: () => { _pending.Remove(item); _justSent = true; BuildConversation(); }));
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

    /// <summary>
    /// 换场景 = 换了一次 —— 幽灵的承诺只管【这一次】,所以切场景就把它抹了。
    /// ★ 不这么做的话:一条带着旧场景标记的幽灵会横跨场景活下去,
    ///   新场景根据标记判不出它是自己的(各场景都是 "Find(sid)?.XXX == true" 判据),
    ///   于是屏幕上是新场景、手里拿着旧场景的幽灵,幽灵按钮还亮着。
    /// </summary>
    /// <returns>真的抹了一条幽灵(并且已经重画过)= true。</returns>
    bool EndGhostOnSceneSwitch()
    {
        if (!InGhost) return false;
        TheApp.Chat.PurgeGhosts();
        _sessionId = null;
        BuildSessions();      // 内部会跑 UpdateContext,幽灵按钮才会从实线回虚线
        BuildConversation();  // ★ 必须重画:切场景那一下已经拿着旧会话画过一遍了,
        return true;          //   不重画就会把一条【已经不存在的】幽灵会话继续摆在屏幕上
    }

    /// <summary>进入 / 退出幽灵会话。退出即抹除该会话并回到普通会话(可退出 —— 用户反馈"按下之后无法退出")。</summary>
    /// <summary>internal:渲染诊断要走【真的那条路】进幽灵态 —— 不能靠 OpenSession(它开头就清幽灵)。</summary>
    internal void ToggleGhost()
    {
        // 退出幽灵 -> 回普通会话的空态(ToNormal 已经保证落在空会话上)
        if (InGhost) { ToNormal(); FocusInputIfPresent(); return; }
        // ★ 幽灵跟着【当前场景】建(用户裁定 2026-08-03:全功能都要幽灵会话)。
        //   以前它永远建一条普通文字会话,于是在同传里按幽灵会被踢回文字翻译。
        var m = TheApp.Interpret.Mode;
        var g = TheApp.Chat.NewGhostSession(_wsKey,
            interpret: _wsKey == "translation" && m == TranslationMode.Interpret,
            fileTrans: _wsKey == "translation" && m == TranslationMode.FileTrans,
            i18nTable: _wsKey == "translation" && m == TranslationMode.I18n,
            replyLetter: _wsKey == "chat" && TheApp.Reply.SceneReply);
        _sessionId = g.SessionId;
        ApplySessionScene(g);   // 幽灵已经带着当前场景的标记,这一步现在是原地不动
        BuildSessions();
        BuildConversation();
        FocusInputIfPresent();
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
