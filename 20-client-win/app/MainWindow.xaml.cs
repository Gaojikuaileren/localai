// P3c -- 外壳导航。设计 §3:主页 + 工作空间组 + 系统组;左导航可收起。
// 条件渲染两条(设计 §3 脚注,安全相关,皮肤禁改):
//   · 投资研究:仅指定成员 + 指定端 -> 不满足时**整行不存在**(不是灰掉,是渲染树里没有)。
//   · 主机管理:仅主机端 + 家庭安全管理员 -> 副机端即使管理员也不显示。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LocalAI.Client.I18n;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;
using LocalAI.Client.Views;

namespace LocalAI.Client;

public sealed record NavItem(string Key, string TitleKey, IconName Icon, Func<UserControl> Build);

public partial class MainWindow : Window
{
    readonly List<(NavItem item, Button button)> _nav = new();
    bool _collapsed;
    string _currentKey = "";

    App TheApp => (App)Application.Current;

    readonly DispatcherTimer _taskRotate = new() { Interval = TimeSpan.FromSeconds(4) };
    int _taskIndex;
    string? _drawerKind;   // "tasks" | "calendar" | null

    public MainWindow()
    {
        InitializeComponent();
        BuildNav();
        Navigate("home");
        RefreshStatus();
        RefreshMember();
        BuildChromeIcons();
        // 抽屉打开时按 Esc 也能关(遮罩已接管鼠标,键盘也给一条退路)
        PreviewKeyDown += (_, ke) => { if (ke.Key == System.Windows.Input.Key.Escape && Overlay.IsOpen) { Overlay.CloseActive(); ke.Handled = true; } };
        StateChanged += (_, _) => SyncMaxButton();
        SyncMaxButton();

        // 窗口圆角跟随皮肤(暖萌大 / 微风中 / 墨白小)。句柄要等 SourceInitialized 之后才有。
        SourceInitialized += (_, _) => { WindowCorners.Apply(this, TheApp.Settings.Skin); ApplySizeLimits(); };

        // 底部任务横条:有任务才出现,多任务时自动轮播
        TheApp.Tasks.Changed += () => Dispatcher.Invoke(RefreshTaskBar);
        _taskRotate.Tick += (_, _) => RotateTask();
        RefreshTaskBar();

        // 语言切换 -> 【就地重建】文案,不需要重启。
        // 做法说明:界面是代码构建的,文案在构造时取一次;所以最简洁可靠的方式是重建导航
        // 并重新进入当前页面(视图本来就是每次导航新建的,重建成本很低)。
        Strings.LanguageChanged += OnLanguageChanged;

        // 显存条:实时(2 秒)更新。★ 不可见就停表 —— 省电远比调长间隔有效。
        VramHost.Content = _vram;
        TheApp.Vram.Updated += s => Dispatcher.Invoke(() => _vram.Update(s));
        _vram.Update(TheApp.Vram.Last);
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) TheApp.Vram.Resume(); else TheApp.Vram.Pause(); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) TheApp.Vram.Pause(); else TheApp.Vram.Resume(); };
    }

    readonly VramBar _vram = new();

    void OnLanguageChanged()
    {
        var current = _currentKey;
        var wasCollapsed = _collapsed;
        Overlay.CloseActive();          // 抽屉里的文案也来自旧语言,先收起
        BuildNav();
        Navigate(current);      // 重新构建当前页 -> 新语言
        if (wasCollapsed) { _collapsed = false; OnToggleNav(this, new RoutedEventArgs()); }   // 保持收起状态
        BuildChromeIcons();
        RefreshStatus();
        RefreshMember();
        RefreshTaskBar();
    }

    /// <summary>
    /// 窗口尺寸上下限(用户裁定):最小 = 屏幕的【四分之一大小】(面积四分之一 = 宽高各一半;
    /// 2K→1280×720,HD→960×540),最大 = 全屏(工作区)。
    /// 把最小值提到这个量级后,极端紧凑档几乎不会触发,缩放时少了一整类跳变。
    /// </summary>
    void ApplySizeLimits()
    {
        var wa = SystemParameters.WorkArea;                 // 工作区 = 全屏减任务栏
        var screenW = SystemParameters.PrimaryScreenWidth;
        var screenH = SystemParameters.PrimaryScreenHeight;

        var (minW, minH) = Layout.MinWindowFor(screenW, screenH);
        // 保险:最小值不能超过工作区本身(小屏/缩放比例异常时)
        MinWidth = Math.Min(minW, wa.Width);
        MinHeight = Math.Min(minH, wa.Height);
        MaxWidth = wa.Width;
        MaxHeight = wa.Height;

        if (Width < MinWidth) Width = MinWidth;
        if (Height < MinHeight) Height = MinHeight;
        if (Width > MaxWidth) Width = MaxWidth;
        if (Height > MaxHeight) Height = MaxHeight;
    }

    // ---------------------------------------------------------------- 底部任务横条
    // 轮播手感(用户裁定):不是硬切换,而是【向上滑走 -> 换内容 -> 从下方滑入 -> 到位停留】。
    // 停留时长 = TaskDwell;滑动时长 = TaskSlide。单个任务不轮播(转来转去反而看不清)。
    static readonly TimeSpan TaskDwell = TimeSpan.FromSeconds(4.5);
    static readonly TimeSpan TaskSlide = TimeSpan.FromMilliseconds(260);
    const double SlideDistance = 26;

    public void RefreshTaskBar()
    {
        var tasks = TheApp.Tasks.Tasks;
        if (tasks.Count == 0)
        {
            TaskBar.Visibility = Visibility.Collapsed;   // 空闲自动隐藏,不留空横条
            _taskRotate.Stop();
            if (_drawerKind == "tasks") CloseDrawer();
            return;
        }

        TaskBar.Visibility = Visibility.Visible;
        _taskRotate.Interval = TaskDwell;
        // 多个任务才轮播;单个固定显示
        if (tasks.Count > 1) { if (!_taskRotate.IsEnabled) _taskRotate.Start(); }
        else { _taskRotate.Stop(); _taskIndex = 0; }

        ShowTask(tasks[_taskIndex % tasks.Count], tasks.Count, animate: false);
        if (_drawerKind == "tasks") TaskDrawerHost.Content = new TaskDrawerView();
    }

    /// <summary>轮到下一条:先把当前这条向上滑走,换完内容再从下方滑入。</summary>
    void RotateTask()
    {
        var tasks = TheApp.Tasks.Tasks;
        if (tasks.Count <= 1) return;

        var up = new DoubleAnimation(0, -SlideDistance, TaskSlide)
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        up.Completed += (_, _) =>
        {
            _taskIndex++;
            var list = TheApp.Tasks.Tasks;
            if (list.Count == 0) return;
            ShowTask(list[_taskIndex % list.Count], list.Count, animate: true);
        };
        TaskBarSlideT.BeginAnimation(TranslateTransform.YProperty, up);
    }

    void ShowTask(RunningTask t, int total, bool animate)
    {
        TaskBarTitle.Text = t.Title;
        TaskBarDetail.Text = t.Detail;
        TaskBarPercent.Text = t.PercentText;
        TaskBarCount.Text = total > 1 ? $"共 {total} 个" : "";
        if (t.Progress < 0) TaskBarProgress.IsIndeterminate = true;
        else { TaskBarProgress.IsIndeterminate = false; TaskBarProgress.Value = t.Progress; }

        if (!animate) { TaskBarSlideT.BeginAnimation(TranslateTransform.YProperty, null); TaskBarSlideT.Y = 0; return; }

        // 从下方滑入并停在原位
        var down = new DoubleAnimation(SlideDistance, 0, TaskSlide)
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        TaskBarSlideT.BeginAnimation(TranslateTransform.YProperty, down);
    }

    // ---------------------------------------------------------------- 全局面板(两个方向)
    // 抽屉挂在外壳上而不是某个视图里 —— 用户要求它在任何界面都能开。
    // 方向由用户裁定:日历从【右上角】拉开(锚在顶栏日历按钮下);任务从【下往上】升起(锚在底部横条上)。

    void OnToggleTaskDrawer(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_drawerKind == "tasks") CloseDrawer();
        else OpenTaskDrawer();
    }

    void OpenTaskDrawer()
    {
        Overlay.Register(CloseDrawer);   // 统一协调:先关掉别的浮层,并让 Esc/点外部也能关它
        _drawerKind = "tasks";
        TaskDrawerHost.Content = new TaskDrawerView();
        DrawerScrim.Visibility = Visibility.Visible;
        TaskDrawer.Visibility = Visibility.Visible;
        // 从下往上滑入
        var anim = new DoubleAnimation(TaskDrawer.MaxHeight, 0, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        TaskDrawerSlide.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    void OnOpenBriefing(object sender, RoutedEventArgs e)
    {
        if (Overlay.ConsumeClick()) return;   // 有浮窗开着 -> 这一次只关它
        if (_drawerKind == "briefing") { CloseDrawer(); return; }
        OpenRightDrawer("briefing", "消息栏", new BriefingDrawerView());
    }

    void OnOpenCalendar(object sender, RoutedEventArgs e)
    {
        if (Overlay.ConsumeClick()) return;   // 有浮窗开着 -> 这一次只关它
        if (_drawerKind == "calendar") { CloseDrawer(); return; }
        OpenRightDrawer("calendar", "日历", new CalendarView(CalendarView.Mode.Month));
    }

    /// <summary>右上角下拉抽屉(日历 / 消息栏共用一个容器,同时只开一个)。</summary>
    void OpenRightDrawer(string kind, string title, UserControl content)
    {
        Overlay.Register(CloseDrawer);
        _drawerKind = kind;
        RightDrawerTitle.Text = title;
        CalendarDrawerHost.Content = content;
        DrawerScrim.Visibility = Visibility.Visible;
        CalendarDrawer.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        CalendarDrawerScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    void OnCloseDrawer(object sender, RoutedEventArgs e) => Overlay.CloseActive();
    void OnCloseDrawer(object sender, System.Windows.Input.MouseButtonEventArgs e) => Overlay.CloseActive();

    void CloseDrawer()
    {
        if (_drawerKind is null) return;   // 已经关了,避免协调器回调重入
        _drawerKind = null;
        Overlay.Unregister(CloseDrawer);
        CalendarDrawer.Visibility = Visibility.Collapsed;
        TaskDrawer.Visibility = Visibility.Collapsed;
        DrawerScrim.Visibility = Visibility.Collapsed;
        CalendarDrawerHost.Content = null;
        TaskDrawerHost.Content = null;
    }

    // ---------------------------------------------------------------- 自绘标题栏
    // CaptionHeight=0 意味着系统不再把顶部当标题栏,拖动/双击最大化要自己接。

    void OnTitleBarDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }   // 双击最大化/还原(系统惯例)
        // 最大化状态下拖动应先还原再跟随鼠标(Windows 的标准手感)
        if (WindowState == WindowState.Maximized)
        {
            var mouseX = e.GetPosition(this).X;
            var pct = mouseX / ActualWidth;
            WindowState = WindowState.Normal;
            Left = Math.Max(0, PointToScreen(e.GetPosition(this)).X - RestoreBounds.Width * pct);
            Top = 0;
        }
        try { DragMove(); } catch { /* 极少数情况下鼠标已抬起,忽略 */ }
    }

    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void OnMaximizeRestore(object sender, RoutedEventArgs e) => ToggleMaximize();

    void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void SyncMaxButton()
    {
        var max = WindowState == WindowState.Maximized;
        MaxButton.Content = Icons.Make(max ? IconName.Restore : IconName.Maximize, 13, "FgSecondary");
        MaxButton.ToolTip = max ? "向下还原" : "最大化";
    }

    // 标题栏与导航的图标一次性装配(跟随皮肤)
    void BuildChromeIcons()
    {
        MinButton.Content = Icons.Make(IconName.Minimize, 13, "FgSecondary");
        CloseButton.Content = Icons.Make(IconName.Close, 13, "FgSecondary");
        CollapseButton.Content = Icons.Make(IconName.Menu, 15, "FgMuted");
        CalendarButton.Content = Icons.Make(IconName.Calendar, 17, "FgSecondary");
        BriefingButton.Content = Icons.Make(IconName.Chat, 17, "FgSecondary");
        TaskBarIcon.Content = Icons.Make(IconName.Tasks, 15, "Accent");
        TaskBarChevron.Content = Icons.Make(IconName.ChevronRight, 12, "FgMuted");
        CalendarDrawerClose.Content = Icons.Make(IconName.Close, 12, "FgMuted");
        TaskDrawerClose.Content = Icons.Make(IconName.Close, 12, "FgMuted");
    }

    // 走正常的 Closing 流程 —— App 会按设置决定「缩到托盘」还是「真退出」,
    // 这样自绘的 × 和系统的 Alt+F4 行为完全一致。
    void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    void BuildNav()
    {
        NavPanel.Children.Clear();
        _nav.Clear();

        AddItem(new NavItem("home", "nav.home", IconName.Home, () => new HomeView()));

        AddGroupLabel(Strings.Get("nav.workspaces"));
        AddItem(new NavItem("chat", "nav.chat", IconName.Chat, () => new PlaceholderView("nav.chat")));
        AddItem(new NavItem("assets", "nav.assets", IconName.Assets, () => new PlaceholderView("nav.assets")));
        AddItem(new NavItem("translation", "nav.translation", IconName.Translation, () => new PlaceholderView("nav.translation")));
        AddItem(new NavItem("courses", "nav.courses", IconName.Courses, () => new PlaceholderView("nav.courses")));
        AddItem(new NavItem("computer", "nav.computer_control", IconName.Computer, () => new PlaceholderView("nav.computer_control")));
        // 投资研究:D42 §7/B4 只做隐藏占位。当前无"指定成员+指定端"配置 -> 整行不渲染。
        if (ShouldShowInvestment()) AddItem(new NavItem("investment", "nav.investment", IconName.Investment, () => new PlaceholderView("nav.investment")));

        AddGroupLabel(Strings.Get("nav.system"));
        AddItem(new NavItem("extensions", "nav.extensions", IconName.Extensions, () => new PlaceholderView("nav.extensions")));
        AddItem(new NavItem("settings", "nav.settings", IconName.Settings, () => new SettingsView()));
        // 主机管理 = 配对与设备管理的所在地。副机端也要能配对,所以这里显示的是"连接与设备";
        // 真正的主机专属项(仅主机端 + 管理员)在该视图内部再判定。
        AddItem(new NavItem("devices", "devices.title", IconName.Devices, () => new DevicesView()));
    }

    static bool ShouldShowInvestment() => false;   // P3c 只做隐藏占位:任何人任何端都不显示(D42 §7/B4)

    void AddGroupLabel(string text)
    {
        NavPanel.Children.Add(new TextBlock
        {
            Text = text,
            Margin = new Thickness(10, 16, 10, 6),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("FgMuted"),
        });
    }

    void AddItem(NavItem item)
    {
        // 图标 + 文字。图标形状跟随皮肤(墨白线性 / 苹果线性 / 暖萌可爱),换肤自动重建。
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Icons.Make(item.Icon, 17, "FgSecondary");
        icon.Margin = new Thickness(2, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var label = new TextBlock { Text = Strings.Get(item.TitleKey), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(icon);
        row.Children.Add(label);

        var b = new Button
        {
            Content = row,
            Tag = item.Key,
            Height = 38,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (Brush)FindResource("FgPrimary"),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        b.Click += (_, _) => Navigate(item.Key);
        NavPanel.Children.Add(b);
        _nav.Add((item, b));
    }

    public void Navigate(string key)
    {
        var hit = _nav.FirstOrDefault(n => n.item.Key == key);
        if (hit.item is null) return;
        _currentKey = key;
        ContentHost.Content = hit.item.Build();
        // 主页右上角已经有日历板块了,顶栏就不再重复放按钮(用户裁定)。
        CalendarButton.Visibility = key == "home" ? Visibility.Collapsed : Visibility.Visible;

        foreach (var (item, btn) in _nav)
        {
            var on = item.Key == key;
            btn.Background = on ? (Brush)FindResource("BgSelected") : Brushes.Transparent;
            // ★ 选中态前景必须跟着背景走:墨白皮肤的 BgSelected 是近黑色,若前景仍用 FgPrimary(也是黑)
            //   就会黑底黑字看不清。各皮肤自己声明选中态前景(FgOnSelected)。图标同理。
            btn.Foreground = (Brush)FindResource(on ? "FgOnSelected" : "FgPrimary");
            btn.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            if (btn.Content is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is FrameworkElement ico)
                Icons.SetForeground(ico, on ? "FgOnSelected" : "FgSecondary");
        }
    }

    /// <summary>
    /// 从主页项目方块深链进来:切到对应工作空间,并把要打开的项目 id 交给它。
    /// 工作空间本身还是占位(功能等 P4/P6/P9),所以先切过去并显示"要打开哪个项目",
    /// 不假装已经打开了会话。
    /// </summary>
    public void NavigateToProject(string workspaceKey, string projectId)
    {
        Navigate(workspaceKey);
        if (ContentHost.Content is PlaceholderView ph) ph.ShowPendingProject(projectId);
    }

    void OnToggleNav(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        NavColumn.Width = new GridLength(_collapsed ? 58 : 240);
        // 品牌现在在自绘标题栏里(贯通全宽),收起导航不影响它。
        foreach (var (item, btn) in _nav)
        {
            // 收起时只留【图标】(比留首字清楚得多);ToolTip 补全名 —— 无障碍:不能只靠视觉。
            if (btn.Content is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock lbl)
            {
                lbl.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
                if (sp.Children[0] is FrameworkElement ic)
                    ic.Margin = _collapsed ? new Thickness(0) : new Thickness(2, 0, 10, 0);
                sp.HorizontalAlignment = _collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            }
            btn.ToolTip = _collapsed ? Strings.Get(item.TitleKey) : null;
            btn.HorizontalContentAlignment = _collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        }
        _vram.SetCollapsed(_collapsed);   // 收起 -> 环形+百分比;展开 -> 三段横条
        // 分组标题在收起状态没有宽度显示,整条隐藏
        foreach (var c in NavPanel.Children) if (c is TextBlock t) t.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    public void RefreshStatus()
    {
        var (key, brushKey) = TheApp.Hub.State switch
        {
            HubState.Online => ("status.online", "RiskSafe"),
            HubState.Connecting => ("status.connecting", "FgMuted"),
            HubState.NotPaired => ("status.not_paired", "RiskWarning"),
            HubState.Revoked => ("status.revoked", "RiskDanger"),
            _ => ("status.offline", "FgMuted"),
        };
        StatusText.Text = Strings.Get(key);
        StatusDot.Fill = (Brush)FindResource(brushKey);
    }

    public void RefreshMember()
    {
        // D45:设备默认成员只是**猜测**,不是认证。文案必须让人一眼能纠正,且不暗示已验明身份。
        // ★ 显示名只用主机下发后缓存的那份;客户端本地绝不持有"我是谁"的权威值
        //   (铁律:主体只来自成员表 —— gateway.py:227)。没有则显示占位,不猜。
        var name = string.IsNullOrWhiteSpace(TheApp.Settings.CachedMemberDisplayName)
                   ? "—" : TheApp.Settings.CachedMemberDisplayName!;
        MemberText.Text = Strings.Get("member.current_is", ("m", name));
        MemberHint.Text = Strings.Get("member.correct");
    }
}
