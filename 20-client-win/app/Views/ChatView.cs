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
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class ChatView : UserControl
{
    static App TheApp => (App)Application.Current;

    string? _projectId;      // 当前项目上下文;null = 普通会话
    string? _sessionId;      // 当前打开的会话

    readonly TextBlock _ctxTitle = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly StackPanel _backBtnHost = new() { Orientation = Orientation.Horizontal };
    readonly StackPanel _sessions = new();
    readonly ContentControl _conv = new();   // 会话区(空态居中 / 有消息则底部输入)
    TextBox _input = new();
    readonly List<ChatAttachment> _pending = new();   // 待发送的附件引用(路径/剪贴板)
    string _draft = "";                                // 跨重建保留正在输入的文字

    public ChatView()
    {
        _ctxTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        // ---- 右:会话列表(常驻)----
        var newBtn = Ui.PlusButton(NewSession, "新建会话");
        var head = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 0, 2, 6) };
        DockPanel.SetDock(newBtn, Dock.Right);
        DockPanel.SetDock(_backBtnHost, Dock.Right);
        head.Children.Add(newBtn);
        head.Children.Add(_backBtnHost);
        head.Children.Add(_ctxTitle);

        var sessDock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top);
        sessDock.Children.Add(head);
        sessDock.Children.Add(new ScrollViewer
        {
            Content = _sessions,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough());
        var sessCard = Ui.Card(sessDock, new Thickness(0));
        sessCard.Width = 250;

        // ---- 左缘箭头:拉开项目选择器 ----
        var arrow = ArrowButton();

        // ---- 布局:会话区 | 箭头 | 会话列表 ----
        var grid = new Grid { Margin = new Thickness(24, 18, 24, 18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_conv, 0);
        Grid.SetColumn(arrow, 1);
        Grid.SetColumn(sessCard, 2);
        grid.Children.Add(_conv);
        grid.Children.Add(arrow);
        grid.Children.Add(sessCard);
        Content = grid;

        BuildSessions();
        BuildConversation();
        TheApp.Chat.Changed += OnChatChanged;
        TheApp.Projects.Changed += UpdateContext;
        Unloaded += (_, _) => { TheApp.Chat.Changed -= OnChatChanged; TheApp.Projects.Changed -= UpdateContext; };
    }

    void OnChatChanged() { BuildSessions(); BuildConversation(); }

    public void SelectProject(string projectId)
    {
        _projectId = projectId;
        _sessionId = TheApp.Chat.SessionsOf(projectId).FirstOrDefault()?.SessionId;
        BuildSessions();
        BuildConversation();
    }

    void ToNormal()
    {
        _projectId = null;
        _sessionId = TheApp.Chat.NormalSessions().FirstOrDefault()?.SessionId;
        BuildSessions();
        BuildConversation();
    }

    void UpdateContext()
    {
        _ctxTitle.Text = _projectId is { } pid ? "项目 · " + (TheApp.Projects.Find(pid)?.Title ?? "项目") : "普通会话";
        _backBtnHost.Children.Clear();
        if (_projectId is not null) _backBtnHost.Children.Add(BackChip());
    }

    // ---------------------------------------------------------------- 会话列表
    void BuildSessions()
    {
        UpdateContext();
        _sessions.Children.Clear();
        var list = (_projectId is { } pid ? TheApp.Chat.SessionsOf(pid) : TheApp.Chat.NormalSessions()).ToList();
        if (list.Count == 0)
        {
            _sessions.Children.Add(Ui.Caption(_projectId is null ? "还没有会话。点 + 新建。" : "这个项目下还没有会话。点 + 新建。"));
            return;
        }
        foreach (var s in list) _sessions.Children.Add(SessionRow(s));
    }

    FrameworkElement SessionRow(ChatSession s)
    {
        var selected = s.SessionId == _sessionId;
        var title = new TextBlock { Text = s.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        // ★ 选中态字色跟着底色走(墨白的 BgSelected 近黑,用 FgOnSelected 才不会黑底黑字)
        title.SetResourceReference(TextBlock.ForegroundProperty, selected ? "FgOnSelected" : "FgPrimary");
        var time = new TextBlock { Text = s.LastActive.ToString("M月d日 HH:mm"), Margin = new Thickness(0, 1, 0, 0) };
        time.SetResourceReference(TextBlock.ForegroundProperty, selected ? "FgOnSelected" : "FgMuted");
        time.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var col = new StackPanel();
        col.Children.Add(title);
        col.Children.Add(time);

        var host = new Border { Child = col, Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand };
        host.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        host.Background = selected ? (Brush)FindResource("BgSelected") : Brushes.Transparent;
        host.MouseEnter += (_, _) => { if (!selected) host.SetResourceReference(Border.BackgroundProperty, "BgHover"); };
        host.MouseLeave += (_, _) => { if (!selected) host.Background = Brushes.Transparent; };
        host.MouseLeftButtonUp += (_, _) => { _sessionId = s.SessionId; BuildSessions(); BuildConversation(); };
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
            mi.Click += (_, _) => TheApp.Chat.MoveToProject(s.SessionId, pid);
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
        _sessionId = s.SessionId;
        SelectProject(p.ProjectId);
        _sessionId = s.SessionId;
        BuildSessions();
        BuildConversation();
    }

    // ---------------------------------------------------------------- 会话区(空态居中 / 有消息底部输入)
    // 输入区(含附件按钮 + 待发附件预览 + 输入框 + 发送),空态与有消息态共用
    FrameworkElement BuildInputArea()
    {
        _input = new TextBox { Text = _draft, Padding = new Thickness(11, 9, 11, 9), VerticalContentAlignment = VerticalAlignment.Center };
        _input.TextChanged += (_, _) => _draft = _input.Text;
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; SendCurrent(); } };
        var send = Ui.Primary("发送", (_, _) => SendCurrent());
        send.Height = 40;
        var attach = AttachButton();

        var inputRow = new DockPanel { LastChildFill = true };
        var sendWrap = new Border { Child = send, Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(sendWrap, Dock.Right);
        DockPanel.SetDock(attach, Dock.Left);
        inputRow.Children.Add(sendWrap);
        inputRow.Children.Add(attach);
        inputRow.Children.Add(_input);

        var area = new StackPanel();
        if (_pending.Count > 0) area.Children.Add(PendingStrip());
        area.Children.Add(inputRow);
        return area;
    }

    void BuildConversation()
    {
        var inputArea = BuildInputArea();
        var hasMsgs = _sessionId is not null && TheApp.Chat.MessagesOf(_sessionId).Any();
        if (!hasMsgs)
        {
            // 空态:输入框竖直居中(像 GPT)
            var title = new TextBlock { Text = "开始新的对话", FontWeight = FontWeights.SemiBold, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center };
            title.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            var hint = Ui.Caption(_projectId is null
                ? "没选项目直接聊 = 普通会话。左侧 ‹ 箭头可选项目。"
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
            _conv.Content = Ui.Card(box, new Thickness(0));
        }
        else
        {
            var msgs = new StackPanel();
            foreach (var m in TheApp.Chat.MessagesOf(_sessionId!)) msgs.Children.Add(Bubble(m));
            var scroll = new ScrollViewer { Content = msgs, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            scroll.PassThrough();
            var dock = new DockPanel { LastChildFill = true };
            var inputWrap = new Border { Child = inputArea, Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(inputWrap, Dock.Bottom);
            dock.Children.Add(inputWrap);
            dock.Children.Add(scroll);
            _conv.Content = Ui.Card(dock, new Thickness(0));
            Dispatcher.BeginInvoke(new Action(() => scroll.ScrollToEnd()), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    FrameworkElement Bubble(ChatMessage m)
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
            var tb = new TextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap };
            tb.SetResourceReference(TextBlock.ForegroundProperty, user ? "FgOnAccent" : "FgPrimary");
            stack.Children.Add(tb);
        }
        var bubble = new Border
        {
            Child = stack, Padding = new Thickness(11, 8, 11, 8), MaxWidth = 560, Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        bubble.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        bubble.SetResourceReference(Border.BackgroundProperty, user ? "Accent" : "BgSunken");
        return bubble;
    }

    // ---------------------------------------------------------------- 动作
    void NewSession()
    {
        var scope = _projectId is null ? ProjectScope.Personal : (TheApp.Projects.Find(_projectId!)?.Scope ?? ProjectScope.Personal);
        var s = TheApp.Chat.NewSession(_projectId, scope);
        _sessionId = s.SessionId;
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
        if (TheApp.Chat.Send(_sessionId, text, atts))
        {
            _draft = "";
            _pending.Clear();   // Changed -> 重建;draft/pending 已清
        }
    }

    // 附件按钮:点开菜单 —— 选择文件 / 选择图片 / 粘贴剪贴板截图。★ 只带路径/剪贴板指令,不真发内容。
    FrameworkElement AttachButton()
    {
        var t = new TextBlock { Text = "＋", VerticalAlignment = VerticalAlignment.Center, FontSize = 16 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        var b = new Border { Child = t, Width = 40, Height = 40, Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.BorderThickness = new Thickness(1);
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.ToolTip = "添加附件 / 图片 / 剪贴板截图";

        var menu = new ContextMenu();
        var mFile = new MenuItem { Header = "选择文件…" };
        mFile.Click += (_, _) => PickFile(false);
        var mImg = new MenuItem { Header = "选择图片…" };
        mImg.Click += (_, _) => PickFile(true);
        var mClip = new MenuItem { Header = "粘贴剪贴板截图" };
        mClip.Click += (_, _) => PasteClipboard();
        menu.Items.Add(mFile);
        menu.Items.Add(mImg);
        menu.Items.Add(mClip);
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; menu.PlacementTarget = b; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top; menu.IsOpen = true; };
        return b;
    }

    void PickFile(bool imageOnly)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = imageOnly ? "选择图片" : "选择文件",
            Filter = imageOnly ? "图片|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|所有文件|*.*" : "所有文件|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        var name = System.IO.Path.GetFileName(dlg.FileName);
        _pending.Add(new ChatAttachment(imageOnly ? AttachKind.Image : AttachKind.File, dlg.FileName, name));
        BuildConversation();
    }

    void PasteClipboard()
    {
        var img = Clipboard.GetImage();
        if (img is null) { MessageBox.Show("剪贴板里没有图片。先截个图再试。", "本地 AI 中枢", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            // 落一份预览 png 到本机临时目录(仅供显示 + 给 AI 一个可读路径;不通过网络发送内容)
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "localai-clip-" + Guid.NewGuid().ToString("N")[..8] + ".png");
            using (var fs = System.IO.File.Create(tmp))
            {
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                enc.Save(fs);
            }
            _pending.Add(new ChatAttachment(AttachKind.Clipboard, tmp, "剪贴板截图"));
            BuildConversation();
        }
        catch (Exception ex) { MessageBox.Show("读取剪贴板图片失败:" + ex.Message, "本地 AI 中枢", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    FrameworkElement PendingStrip()
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        for (int k = 0; k < _pending.Count; k++)
        {
            var a = _pending[k];
            var idx = k;
            var chip = AttachChip(a, onRemove: () => { _pending.RemoveAt(idx); BuildConversation(); });
            wrap.Children.Add(chip);
        }
        return wrap;
    }

    // 附件小卡:图片显缩略图,文件显图标+名;右上角 × 可移除(仅待发列表用)
    FrameworkElement AttachChip(ChatAttachment a, Action? onRemove)
    {
        FrameworkElement inner;
        if (a.IsImage)
        {
            inner = new Image { Source = Thumb(a.Path, 120), Stretch = Stretch.UniformToFill, Width = 84, Height = 84 };
        }
        else
        {
            var ic = Icons.Make(IconName.Folder, 18, "FgSecondary");
            var nm = new TextBlock { Text = a.Display, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 120, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            nm.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            nm.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) };
            row.Children.Add(ic); row.Children.Add(nm);
            inner = row;
        }
        var card = new Border { Child = inner, Margin = new Thickness(0, 0, 8, 0), Padding = a.IsImage ? new Thickness(0) : new Thickness(4), ClipToBounds = true };
        card.SetResourceReference(Border.BackgroundProperty, "BgSunken");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.BorderThickness = new Thickness(1);
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        card.ToolTip = a.Kind == AttachKind.Clipboard ? "剪贴板截图(发送的是读取指令,不是内容)" : a.Path;

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
    FrameworkElement ArrowButton()
    {
        var chev = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M13 5 L7 11 L13 17"),
            StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        chev.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "FgSecondary");
        var b = new Border { Child = chev, Width = 22, Height = 46, Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = "选择项目" };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.BorderThickness = new Thickness(1);
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => OpenPicker();
        return b;
    }

    void OpenPicker()
    {
        var picker = new ProjectPickerView(_projectId,
            onPick: pid => { Overlay.CloseActive(); SelectProject(pid); },
            onNormal: () => { Overlay.CloseActive(); ToNormal(); });
        (Application.Current.MainWindow as MainWindow)?.OpenSideDrawer("项目", picker, IconName.Folder);
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
