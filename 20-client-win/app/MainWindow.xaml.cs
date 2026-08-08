// P3c -- 外壳导航。设计 §3:主页 + 工作空间组 + 系统组;左导航可收起。
// 条件渲染两条(设计 §3 脚注,安全相关,皮肤禁改):
//   · 投资研究:仅指定成员 + 指定端 -> 不满足时**整行不存在**(不是灰掉,是渲染树里没有)。
//   · 主机管理:仅主机端 + 家庭安全管理员 -> 副机端即使管理员也不显示。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        // 浮层开着时按 Esc 关闭
        // ★★ 方向键不移动焦点(用户裁定)。这里是【焦点导航层】的开关,不是按键层 ——
        //   千万别改成在 PreviewKeyDown 里吞掉 Left/Right/Up/Down:那是【隧道】事件,
        //   主窗口先于输入框收到按键,一吞就把输入框里的左右移光标、上下跨行、Home/End
        //   全废掉了。DirectionalNavigation 只管"方向键要不要挪焦点",完全不碰 TextBox
        //   自己的按键处理(而且 TextBox 本来就把方向键标记为已处理,根本触发不到导航)。
        //   按钮/复选框设成不可聚焦之后方向键已无处可去,这条属于保险丝。
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.None);
        // ★★ Tab 导航【整棵树关死】。此前只靠拦 Tab 键 —— 用户实测焦点仍会跑到显存/token 那块上去,
        //   而追到底是哪个控件漏了是没有尽头的(Control 的 Focusable 默认就是 true,
        //   随便一个 ContentControl、一个内联模板的 Button 都算)。
        //   这一句把 WPF 自己的 Tab 导航整体停掉,焦点只由 FocusPolicy 驱动 —— 从机制上断掉,不再打地鼠。
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.None);

        // ★★ Tab 由我们自己接管:只在【可编辑的输入框】之间走(见 FocusPolicy)。
        //   为什么不靠给控件设 IsTabStop=False —— 那是打地鼠:Control 的 Focusable 默认就是 true,
        //   ContentControl 这类纯板块容器天生就是 Tab 停靠点,列不全。这里正面执行白名单。
        //   ★ Tab 是【二态开关】(用户第二轮裁定):聚焦 AI 交流输入框 ⇄ 什么都不聚焦。
        //     不再在多个输入框之间循环 —— 抽屉里的编辑器改用鼠标点格子。
        //   ★ Tab 可以在隧道层拦,方向键不可以(见下面那段)——TextBox 的 AcceptsTab 是 false,
        //     Tab 在输入框里本来就只用于导航,拦掉不损失任何东西。
        PreviewKeyDown += (_, ke) =>
        {
            if (ke.Key != Key.Tab) return;
            ke.Handled = true;
            // ★ 抽屉/浮窗开着时,背后那个聊天输入框是【被遮罩盖住】的 —— 不能把焦点交给它,
            //   否则打字进一个看不见的地方,回车还会直接把消息发出去(审计 2026-07-31)。
            //   这和"系统页盖着时停用底层页"是同一条规矩,只是遮罩这条当时漏了。
            if (Overlay.IsOpen) { FocusPolicy.Park(this, FocusPark); return; }
            FocusPolicy.HandleTab(this, FocusPark, back: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        };

        // ★ Esc 是【总闸】:浮层和菜单都归它管。以前只管浮层 —— 万一菜单状态卡住,
        //   界面点不动而 Esc 又救不了,用户就只能杀进程(实际发生过一次)。
        PreviewKeyDown += (_, ke) =>
        {
            if (ke.Key != System.Windows.Input.Key.Escape) return;
            // ★★ 下拉框开着时,Esc 属于下拉,与它开在哪一层无关(审计 2026-07-31):
            //   抽屉(新增日程/编辑项目/待办)里全是 ComboBox,而抽屉归 Overlay 管 ——
            //   不先让路的话,想收个下拉却把整个抽屉关掉,没保存的表单一起丢。
            //   查验范围必须是【整个窗口】而不只是覆盖层:下拉可能开在抽屉里。
            if (AnyDropDownOpen(this)) return;   // 不设 Handled,让 ComboBox 自己收
            if (Overlay.IsOpen) { Overlay.CloseActive(); ke.Handled = true; }
            if (MenuHost.IsOpen) { MenuHost.CloseAll(); ke.Handled = true; }
            // 系统页(设置/模型/扩展)盖着的时候,Esc 就是那个返回箭头 ——
            // ★ 排在最后:浮层/菜单开在系统页之上,先关它们,别一下退两层。
            // ★ 但下拉框开着时 Esc 归下拉 —— 用户按它是想"收起这个下拉",不是"整页退出"。
            //   ComboBox 的下拉既不在 Overlay 也不在 MenuHost 的账上,只能【实地查验】(与 MenuHost 同一套哲学:
            //   状态是查出来的,不是攒出来的)。这里不设 Handled、直接放行,让 ComboBox 自己去关。
            else if (!ke.Handled && _systemKey is not null) { CloseSystemPage(); ke.Handled = true; }
        };

        // ★ 全局拦截(用户裁定):浮层开着时,窗口里【任何】按钮的第一次点击都只负责关闭浮层,
        //   不触发那个按钮本身。此前是在各个入口按钮里各调一次 ConsumeClick —— 漏一个就穿透,
        //   而按钮遍布导航/设置/项目方块/任务条,不可能补全。改在窗口层一次拦掉。
        //   注意:浮窗是独立的 Popup 窗口,它内部的点击不会走到这里,所以浮窗自己的按钮照常可用;
        //   抽屉在本窗口内,故需放行落在抽屉内部的点击。
        PreviewMouseDown += (_, me) =>
        {
            // ★ 菜单(三个点/分类等)开着或刚被这一下关掉:同样只关菜单,吞掉这次点击。
            //   ContextMenu 是独立 Popup,WPF 在【按下】就把它关了,而按钮多挂在【松开】上,
            //   不吞的话一次点击会顺带按到背后的按钮(用户反馈)。统一在这里拦,不靠各按钮自己判。
            if (MenuHost.SwallowClick) { me.Handled = true; _swallowUp = true; return; }
            if (!Overlay.IsOpen) return;
            // ★★ 下拉框开着时整条拦截让路(审计 2026-07-31,抽屉里最要命的一条):
            //   ComboBox 的下拉是【独立的 Popup 窗口】,点选项时 OriginalSource 在那个窗口里,
            //   IsInsideDrawer 顺着本窗口的树找不到它 -> 判成"点在抽屉外面" -> 关掉整个抽屉。
            //   现象:在新增日程里点一下"重复方式"的某个选项,选没选上,抽屉没了,填的全丢。
            if (AnyDropDownOpen(this)) return;
            // ★ 浮窗内部照常操作 —— 它活在独立的 Popup 视觉树里,IsInsideDrawer 顺着本窗口的树找不到它,
            //   不特判的话点浮窗里的任何东西都会被当成"点在外面"(年月选择就是这么点不中的)。
            if (me.OriginalSource is DependencyObject fd && Flyout.IsInside(fd)) return;
            if (me.OriginalSource is DependencyObject d && IsInsideDrawer(d)) return;   // 抽屉内部照常操作
            Overlay.CloseActive();
            me.Handled = true;   // 吞掉这一次点击,不让它落到按钮上
            _swallowUp = true;
        };
        // ★ 连【松开】也要一起吞:项目里大量小按钮(Chip/返回箭头)挂在 MouseLeftButtonUp 上,
        //   而上面只吞了按下 —— 松开是另一个事件,照样会落到按钮身上。
        //   于是"浮层开着时第一次点击只关浮层"这条规矩,对这类按钮从来没生效过:
        //   一次点击既关了浮层又顺手把人退回了上一页。
        PreviewMouseUp += (_, me) => { if (_swallowUp) { _swallowUp = false; me.Handled = true; } };

        // ★★ 点【输入框以外】的地方 = 取消聚焦(用户裁定 2026-08-03)。
        //   为什么挂在窗口层:① Park 要的 FocusPark 是本窗口的元素,别的层够不着;
        //   ② 全窗口没有第二个焦点范围,SetFocusedElement 只能对 Window 设;
        //   ③ Tab、Esc、点击吞噬三条焦点/输入规矩已经全在这个构造函数里,再分一层就是两套规则互不知情。
        //   ★ 注册在上面那个"吞掉第一次点击"的处理器【之后】:C# 的 += 等价 handledEventsToo:false,
        //     那一下被吞掉时这里根本不会被调到 —— "一次点击只做一件事"自动成立。
        //   ★ 浮窗(Popup,独立 hwnd)里的点击放行 —— 回信页的「自定义问候」就是浮窗里的输入框,
        //     把焦点硬拽回主窗口会让它失去激活、整个关掉。
        //     ★★ 诚实说一句:这一支有可能根本走不到 —— 无父节点的 Popup,其隧道事件到
        //     Popup 本身就到头了(不像 ComboBox 的下拉 —— 那个长在模板里,路由能走到窗口,
        //     所以下拉那条守卫是真有用的)。留着是因为上面两处拦截各留了一份同样的特判,
        //     少这一份反而看着像漏了。
        PreviewMouseDown += (_, me) =>
        {
            if (me.OriginalSource is not DependencyObject d) return;
            // ★ 下拉框开着时整条让路(与上面那两处同一条理由):选项住在独立 Popup 里,
            //   先把焦点停走会把下拉当场关掉 —— 回信页那个【删自定义问候的 ×】就再也点不中了。
            if (AnyDropDownOpen(this)) return;
            if (Flyout.IsInside(d) || FocusPolicy.IsInsideInput(d)) return;
            // ★ 拿着焦点收快捷键的那一块(文件翻译面板的 Del / Ctrl+Z)不许被点别处顺手停掉 ——
            //   旧规矩下按钮全是 Focusable=False,点它们不夺焦点,那个面板一直吃得到键;
            //   新规矩把这个前提拆了,不补的话快捷键会【无声】失效,而提示条还在承诺它。
            if (FocusPolicy.FocusedKeepsFocus()) return;
            FocusPolicy.Park(this, FocusPark);
        };
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
        // ★ 界面用词(家庭/团队)换了 —— 跟换语言完全同类:文案就地重建,不用重启。
        //   复用同一条路径,免得两套重建逻辑各漏一半。
        Services.Vocab.Changed += OnLanguageChanged;

        // ★ 中枢连接状态一变就刷顶栏(启动探测连上、配对成功、调用中被解除等任一路径)——
        //   否则右上角会停在启动时的"尚未配对/未连接",连上后也不改显 token 速率。
        // ★ 也要刷左下角那一格:配对成功 / 改了拨号地址 / 被解除之后,"主还是副"就变了,
        //   以前只刷顶栏,这一格会一直停在上一轮的旧结论。
        TheApp.Hub.Changed += () => Dispatcher.Invoke(() => { RefreshStatus(); RefreshMember(); TheApp.UpdateTrayTooltip(); });

        // ★★★ V19:开机角色判定落定时也要刷左下角那一格。
        //   角色判定是**异步**的(App.StartAfterBootDecision 在后台跑),窗口先建好、
        //   `App.Boot` 那时还是 null。没有这一条订阅,那一格会**永远停在「判定中…」** ——
        //   而"永远停在中间态"和"判错了"一样是在给错信息,只是它看起来更无辜。
        //   ★ 这条订阅在 V19 之前不存在,因为那一格原来靠自己每 20 秒探一次回环管理面;
        //     摘掉那个探测(它是六个文件之外的漏网运行期主机分支)就必须补上这一条。
        App.BootChanged += () => Dispatcher.Invoke(RefreshMember);

        // 显存条:实时(2 秒)更新。★ 不可见就停表 —— 省电远比调长间隔有效。
        VramHost.Content = _vram;
        // ★ 用 BeginInvoke(非阻塞)而不是 Invoke:2026-08-05 起 VramMonitor 还会被
        //   HubGpu 的**推送线程**直接叫醒(主机显存要实时,不能等下一个 2 秒的表)。
        //   同步的 Invoke 会让 UI 一忙就把 SSE 读取线程一起堵住 —— 心跳读不到,
        //   连接就会被判"死了",而实际只是界面卡了一下。与聊天流式那边同一条理由。
        TheApp.Vram.Updated += s => Dispatcher.BeginInvoke(new Action(() => _vram.Update(s)));
        _vram.Update(TheApp.Vram.Last);
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) TheApp.Vram.Resume(); else TheApp.Vram.Pause(); RefreshTaskBar(); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) TheApp.Vram.Pause(); else TheApp.Vram.Resume(); RefreshTaskBar(); };
    }

    readonly VramBar _vram = new();

    /// <summary>刚吞掉一次"只关浮层"的按下,配对的松开也要一起吞(见构造函数里的说明)。</summary>
    bool _swallowUp;

    /// <summary>
    /// 这棵树里有没有【正开着的下拉框】—— 实地查验,不靠标志位。
    /// ★ 走可视树而不是从 Keyboard.FocusedElement 往上爬:焦点可能落在 Popup 里的 ComboBoxItem 上,
    ///   而可视树爬不过 Popup 边界 —— 从上往下找反而是稳的。只在按 Esc 时走一次,开销可忽略。
    /// </summary>
    static bool AnyDropDownOpen(DependencyObject? root)
    {
        if (root is null) return false;
        if (root is ComboBox { IsDropDownOpen: true }) return true;
        var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            if (AnyDropDownOpen(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) return true;
        return false;
    }

    void OnLanguageChanged()
    {
        var current = _currentKey;
        // ★ 盖在上面的系统页也要记住:换语言那一下正是【在设置页里】发生的 ——
        //   不记的话 Navigate(current) 会把覆盖层收掉、把底下那页拆了重建,
        //   人当场被踢回工作页,而且会话/滚动/草稿全丢。覆盖式导航要防的就是这件事。
        var sys = _systemKey;
        var wasCollapsed = _collapsed;
        Overlay.CloseActive();          // 抽屉里的文案也来自旧语言,先收起
        BuildNav();
        // ★ 必须【先收起覆盖层再 Navigate】(审计 2026-07-31 抓到的一条我自己造的回归):
        //   Navigate 里有一条守卫 —— "覆盖层盖着且目标就是底下那页 → 只收起,不重建"。
        //   在这里它会把 Navigate(current) 整个吃掉,于是底下那张工作页永远停在旧语言。
        //   先 Close 让 _systemKey 归零,守卫就不成立了,重建照常发生。
        CloseSystemPage();
        Navigate(current);      // 重新构建当前页 -> 新语言
        if (sys is not null) OpenSystemPage(sys);   // 再把系统页按新语言盖回来
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

        // 首次打开用"内容刚好放得下"的建议尺寸 —— 避免一开始就出现滚动条(用户裁定)。
        // 仍被最小尺寸与工作区夹住,小屏上不会超出。
        Width = Math.Clamp(Layout.PreferredWindowWidth, MinWidth, MaxWidth);
        Height = Math.Clamp(Layout.PreferredWindowHeight, MinHeight, MaxHeight);
    }

    /// <summary>该元素是否位于某个抽屉内部(抽屉内的点击应照常生效,不被"关闭浮层"吞掉)。</summary>
    bool IsInsideDrawer(DependencyObject node)
    {
        for (var n = node; n is not null; n = System.Windows.Media.VisualTreeHelper.GetParent(n))
            if (ReferenceEquals(n, CalendarDrawer) || ReferenceEquals(n, TaskDrawer) || ReferenceEquals(n, SideDrawer)) return true;
        return false;
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
            // ★ 用户裁定(2026-08-07):任务抽屉**没有任务也常驻**。
            //   原来这里是 Collapsed + 「有任务才出现」,而 TaskCenter 到 2026-08-06 才有
            //   第一个生产写入方 ⇒ 横条从来没出现过,用户的原话是"完全没有这个抽屉"。
            // ★★ 常驻**不等于**留一条空横条:空态必须**说清此刻没有任务**。
            //   留一段看不懂的空白,与"置灰但不说原因"是同一类毛病(D99 裁定④那条规矩)。
            // ★ 抽屉开着时**不再强关**:裁定要的正是"没有任务也打得开"。
            //   ——— 强关那一行还有一个副作用:任务跑完的瞬间会把用户正在看的抽屉抽走。
            TaskBar.Visibility = Visibility.Visible;
            _taskRotate.Stop();
            _taskIndex = 0;
            ShowNoTasks();
            if (_drawerKind == "tasks") TaskDrawerHost.Content = new TaskDrawerView();
            return;
        }

        TaskBar.Visibility = Visibility.Visible;
        TaskBarProgress.Visibility = Visibility.Visible;
        _taskRotate.Interval = TaskDwell;
        // 多个任务才轮播;单个固定显示。★ 不可见/最小化时停表(审计 2026-07-31)——
        //   与显存条同一条纪律(见 VramMonitor 头部)。暂停时【不重置 _taskIndex】,
        //   否则恢复可见时会跳回第一条。
        var canRotate = tasks.Count > 1 && IsVisible && WindowState != WindowState.Minimized;
        if (canRotate) { if (!_taskRotate.IsEnabled) _taskRotate.Start(); }
        else { _taskRotate.Stop(); if (tasks.Count <= 1) _taskIndex = 0; }

        ShowTask(tasks[_taskIndex % tasks.Count], tasks.Count, animate: false);
        if (_drawerKind == "tasks") TaskDrawerHost.Content = new TaskDrawerView();
    }

    /// <summary>
    /// 空态:横条常驻,但要**说清现在没有任务**。
    /// ★ 进度条整条藏掉 —— 一条停在 0 的进度条会被读成「有个任务卡住了」,
    ///   那比不显示更坏(它是在**伪造**一件没发生的事)。
    /// </summary>
    void ShowNoTasks()
    {
        TaskBarSlideT.BeginAnimation(TranslateTransform.YProperty, null);
        TaskBarSlideT.Y = 0;
        TaskBarTitle.Text = "没有正在进行的任务";
        TaskBarDetail.Text = "有任务时会显示在这里 · 点这条打开任务抽屉";
        TaskBarPercent.Text = "";
        TaskBarCount.Text = "";
        TaskBarProgress.IsIndeterminate = false;
        TaskBarProgress.Value = 0;
        TaskBarProgress.Visibility = Visibility.Collapsed;
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
        if (_drawerKind == "briefing") { CloseDrawer(); return; }
        OpenRightDrawer("briefing", "消息栏", new BriefingDrawerView());
    }

    void OnOpenCalendar(object sender, RoutedEventArgs e)
    {
        if (_drawerKind == "calendar") { CloseDrawer(); return; }
        // ★ 顶栏这个日历【只保留周排布】(用户裁定 2026-07-31):
        //   月排布归主页那个大板块(它下面还接着时间轴);这里是随手看一眼最近两周,
        //   两种排布都给反而让人不知道该用哪个。
        OpenRightDrawer("calendar", "日历", new CalendarView(CalendarView.Mode.Week) { HideModeSwitch = true });
    }

    /// <summary>
    /// 右侧边缘向左滑入的【全高抽屉】。用于字段多、浮窗放不下的表单(如日程编辑,用户裁定)。
    /// 走同一个浮层协调器 —— 会自动关掉别的浮层,Esc / 点外部也能关它。
    /// </summary>
    public void OpenSideDrawer(string title, UIElement content, IconName icon = IconName.Calendar)
    {
        Overlay.Register(CloseDrawer);
        _drawerKind = "side";
        SideDrawerTitle.Text = title;
        SideDrawerIcon.Content = Icons.Make(icon, 17, "FgSecondary");
        // 圆角跟随皮肤:只圆【左侧】两角(抽屉贴在窗口右缘,右侧应与窗口边平齐)
        var r = TryFindResource("RadiusMd") is CornerRadius cr ? cr.TopLeft : 8;
        SideDrawer.CornerRadius = new CornerRadius(r, 0, 0, r);
        SideDrawerHost.Content = content;
        DrawerScrim.Visibility = Visibility.Visible;
        SideDrawer.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(SideDrawer.Width, 0, TimeSpan.FromMilliseconds(200))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        SideDrawerSlide.BeginAnimation(TranslateTransform.XProperty, anim);
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
        SideDrawer.Visibility = Visibility.Collapsed;
        DrawerScrim.Visibility = Visibility.Collapsed;
        CalendarDrawerHost.Content = null;
        TaskDrawerHost.Content = null;
        SideDrawerHost.Content = null;
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
        SideDrawerClose.Content = Icons.Make(IconName.Close, 12, "FgMuted");
        TaskDrawerClose.Content = Icons.Make(IconName.Close, 12, "FgMuted");
    }

    // 走正常的 Closing 流程 —— App 会按设置决定「缩到托盘」还是「真退出」,
    // 这样自绘的 × 和系统的 Alt+F4 行为完全一致。
    void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    void BuildNav()
    {
        NavPanel.Children.Clear();
        NavSystemPanel.Children.Clear();
        _nav.Clear();

        // 上半区:主页 + 工作空间(可在"扩展"里选择显示哪些)
        AddItem(new NavItem("home", "nav.home", IconName.Home, () => new HomeView()), NavPanel);

        AddGroupLabel(Strings.Get("nav.workspaces"), NavPanel);
        foreach (var w in Workspaces.Ordered(TheApp.Settings))   // 顺序由"扩展"里拖动决定
        {
            var def = w;   // 闭包捕获
            // 所有工作空间共用同一套外壳(会话列表 + 项目抽屉);聊天有对话区,其余中间是占位。
            // ★ 在"扩展"里关掉的【照样登记】,只是不放进左栏 ——
            //   _nav 是 Navigate 的查表,登记漏了就等于这个键从此失效:
            //   人正待在某个工作空间时把它藏起来,之后换语言 Navigate(_currentKey) 会静默什么都不做。
            //   "不在左栏显示"和"这一页不存在"是两件事,别混。
            AddItem(new NavItem(def.Key, def.TitleKey, def.Icon, () => new ChatView(def.Key)), NavPanel,
                    visible: TheApp.Settings.IsWorkspaceVisible(def.Key));
        }

        // 下半区:系统项 —— 贴底(用户裁定)。设备/配对已并入设置,不再单列。
        AddGroupLabel(Strings.Get("nav.system"), NavSystemPanel);
        AddItem(new NavItem("model", "nav.model", IconName.Model, () => new ModelsView()), NavSystemPanel);
        AddItem(new NavItem("extensions", "nav.extensions", IconName.Extensions, () => new ExtensionsView()), NavSystemPanel);
        AddItem(new NavItem("settings", "nav.settings", IconName.Settings, () => new SettingsView()), NavSystemPanel);
    }

    /// <summary>扩展里改了工作空间显示后调用:重建导航栏并保持当前选中(不重建正在看的内容)。</summary>
    public void RefreshNavRail()
    {
        BuildNav();
        HighlightNav(ActiveKey);            // ★ 系统页盖着就高亮系统页,别指着底下那张看不见的页
        if (_collapsed) ApplyCollapsed();   // 保持收起态
    }

    void AddGroupLabel(string text, StackPanel target)
    {
        target.Children.Add(new TextBlock
        {
            Text = text,
            Margin = new Thickness(10, 16, 10, 6),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("FgMuted"),
        });
    }

    void AddItem(NavItem item, StackPanel target, bool visible = true)
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
        b.Click += (_, _) => Navigate(item.Key, fromNavBar: true);
        if (visible) target.Children.Add(b);
        _nav.Add((item, b));   // ★ 不显示也登记 —— 见 BuildNav 里的说明
    }

    /// <summary>
    /// 系统组的三页 —— 它们是【覆盖式】的,不替换底下正在工作的页面。
    /// ★ 键名照抄导航注册处(见 BuildNav):模型那页的键是 "model" 不是 "models" ——
    ///   第一版写错成复数,结果模型页绕过覆盖层、照旧把工作页拆了重建。
    /// </summary>
    static bool IsSystemPage(string key) => key is "settings" or "model" or "extensions";

    /// <summary>当前盖在上面的系统页(null = 没盖)。返回箭头据此回到底下那一页。</summary>
    string? _systemKey;

    /// <summary>
    /// ★ 从【导航栏】点进一个工作空间 = 回到它的第一个功能(用户裁定 2026-08-03):
    ///   翻译空间回文字翻译、聊天空间回聊天。此前它记着上次用的场景,于是"点翻译"可能
    ///   直接落进同传或多语表 —— 导航栏那一栏说的是空间的名字,不是上次停在哪。
    ///
    /// ★ 只挂在【导航按钮】上,不挂在 Navigate 里:
    ///   NavigateToSession / NavigateToProject 是"跳到这条会话自己的场景"(见它们的说明),
    ///   换语言那条 Navigate(_currentKey) 是原地重建 —— 这两类都不该被顺手重置。
    /// </summary>
    void ResetSceneOf(string key)
    {
        if (key == "translation") TheApp.Interpret.SetMode(Services.TranslationMode.Text);
        if (key == "chat") TheApp.Reply.SetScene(false);
    }

    /// <summary>
    /// 用户【眼前】是哪一页 —— 导航栏高亮认这个。
    /// ★ 不是 _currentKey:系统页盖着的时候,_currentKey 指的是底下那张看不见的页,
    ///   拿它去高亮等于左栏说"你在聊天"而屏幕上摆着扩展页 —— 界面在说假话。
    /// </summary>
    string ActiveKey => _systemKey ?? _currentKey;

    /// <param name="fromNavBar">
    /// 从【左栏那一排按钮】点进来的。★ 只有它才重置场景 —— 跳会话/跳项目/换语言重建都不该被顺手重置。
    /// ★★ 而且必须排在下面两条守卫【之后】(复核 2026-08-03):系统页盖着时点左栏里底下那一页自己
    ///   只是收起覆盖层、什么都不重建,这时候重置场景就等于"返回箭头原样返回、左栏同一项却把
    ///   正在进行的同传静默停掉" —— 同一个目的地两条路两种结果,那正是这条守卫要防的事。
    /// </param>
    public void Navigate(string key, bool fromNavBar = false)
    {
        var hit = _nav.FirstOrDefault(n => n.item.Key == key);
        if (hit.item is null) return;

        // ★ 系统页盖着时点左栏里【底下那一页自己】= 收起覆盖层回去,不是拆了重建。
        //   否则"返回箭头能保住会话/滚动/草稿,点左栏同一项却全丢"——
        //   同一个目的地两条路两种结果,那不是导航,是抽奖。
        if (_systemKey is not null && key == _currentKey && ContentHost.Content is not null)
        {
            CloseSystemPage();
            return;
        }

        // ★★ 系统页盖在工作页上,不销毁它(用户裁定):
        //   此前从聊天切到设置再切回来,ChatView 是【重建】的 ——
        //   选中的会话、滚动位置、正在打的字全没了,每次去改个设置都要重来一遍。
        //   现在底下那个实例一直活着,返回就是原样。
        if (IsSystemPage(key))
        {
            OpenSystemPage(key);
            return;
        }

        CloseSystemPage();
        if (fromNavBar) ResetSceneOf(key);   // 见上:排在两条守卫之后,只对"真的重建这一页"生效
        _currentKey = key;
        ContentHost.Content = hit.item.Build();
        // 主页右上角已经有日历板块了,顶栏就不再重复放按钮(用户裁定)。
        CalendarButton.Visibility = key == "home" ? Visibility.Collapsed : Visibility.Visible;
        HighlightNav(key);
    }

    /// <summary>把某个系统页盖上来。左上角一个返回箭头,点它回到底下那一页。</summary>
    /// <summary>覆盖层里的设置页 —— 三个"跳到某块设置"的入口都从这儿取。</summary>
    SettingsView? SettingsInOverlay()
    {
        if (SystemPageHost.Content is not DockPanel d) return null;
        foreach (var c in d.Children) if (c is SettingsView sv) return sv;
        return null;
    }

    public void OpenSystemPage(string key)
    {
        var hit = _nav.FirstOrDefault(n => n.item.Key == key);
        if (hit.item is null) return;

        // 已经盖着同一页就不重建 —— 免得从别处跳进来时把刚滚到的位置又冲掉
        if (_systemKey != key)
        {
            _systemKey = key;
            var back = BackChevron(CloseSystemPage);
            var head = new DockPanel { LastChildFill = false, Margin = new Thickness(20, 14, 20, 0) };
            DockPanel.SetDock(back, Dock.Left);
            head.Children.Add(back);

            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(head, Dock.Top);
            dock.Children.Add(head);
            dock.Children.Add(hit.item.Build());
            SystemPageHost.Content = dock;
        }
        SystemPageLayer.Visibility = Visibility.Visible;
        // ★ 顶栏日历按钮:平时在主页藏着(主页自己有日历板块),但系统页把主页整个盖住了 ——
        //   这时候再藏就等于没有任何入口。盖上就露出来。
        CalendarButton.Visibility = Visibility.Visible;
        // ★ 底下那页【停用】而不是隐藏:隐藏会让它重新走一遍布局/加载,滚动位置又没了 ——
        //   而保留它可用则更糟:Tab 会聚焦到被盖住的聊天输入框,打字打进一个看不见的地方
        //   (这类"不聚焦却能输入"的 bug 之前已经报过一次)。停用两件事一次挡掉。
        ContentHost.IsEnabled = false;
        HighlightNav(key);
    }

    /// <summary>收起系统页,回到底下那一页 —— 它一直都在,不用重建。</summary>
    public void CloseSystemPage()
    {
        if (_systemKey is null) return;
        _systemKey = null;
        SystemPageLayer.Visibility = Visibility.Collapsed;
        SystemPageHost.Content = null;
        ContentHost.IsEnabled = true;
        CalendarButton.Visibility = _currentKey == "home" ? Visibility.Collapsed : Visibility.Visible;
        HighlightNav(_currentKey);
    }

    /// <summary>左上角的返回箭头。★ 与项目抽屉那个「‹ 返回」同一个形状,不另造一种。</summary>
    static FrameworkElement BackChevron(Action onClick)
    {
        var t = new TextBlock { Text = "‹ 返回", VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border
        {
            Child = t, Padding = new Thickness(10, 5, 12, 5), Cursor = Cursors.Hand,
            Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(1),
        };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = System.Windows.Media.Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    // 仅刷新导航栏的选中高亮(不重建内容)——扩展里改显示项后复用它,别把正在看的页面也重建。
    void HighlightNav(string key)
    {
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
        // 所有工作空间现在都是 ChatView(会话/项目外壳),不再有 PlaceholderView 分支
        if (ContentHost.Content is ChatView cv) cv.SelectProject(projectId);
        TheApp.Projects.Touch(projectId);
    }

    /// <summary>
    /// 跳到某条会话【自己所属的工作空间】并打开它(项目会话列表里点跨空间会话时走这里)。
    ///
    /// ★ 为什么不能复用 NavigateToProject:它只吃 projectId,会话是 SelectProject 自己
    ///   `FirstOrDefault()` 替你挑的 —— 那样点 A 打开 B,静默错开,比不跳更坏。
    /// ★ 认不出的 key(老存档里留着已删掉的空间)一律【不跳】,由调用方就地说明;
    ///   在左栏藏起来的空间也不该闷头跳过去 —— 那两件事都在 ChatView 那边先问过了。
    /// </summary>
    public void NavigateToSession(string workspaceKey, string? projectId, string sessionId)
    {
        if (!Workspaces.Known(workspaceKey)) return;
        Navigate(workspaceKey);
        if (ContentHost.Content is ChatView cv) cv.OpenSession(projectId, sessionId);
        if (projectId is not null) TheApp.Projects.Touch(projectId);
    }

    /// <summary>右侧抽屉打开【项目编辑器】(新建/编辑重定向路径)。existing 为空 = 新建。</summary>
    public void OpenProjectEditor(Project? existing)
        => OpenSideDrawer(existing is null ? "新建项目" : "编辑项目", ProjectEditor.Build(existing, () => Overlay.CloseActive()), IconName.Folder);

    /// <summary>右侧抽屉打开【项目库】(已完成项目)。</summary>
    public void OpenProjectLibrary() => OpenSideDrawer("项目库", new ProjectLibraryView(), IconName.Tasks);

    /// <summary>翻译空间语言池旁的齿轮:跳到设置里的【翻译语言池】并高亮。</summary>
    public void OpenLanguagePoolSettings()
    {
        Overlay.CloseActive();
        Navigate("settings");
        SettingsInOverlay()?.RevealLanguagePool();
    }

    /// <summary>同传界面检测到没装虚拟声卡时点进来:跳到设置里的「声音驱动」并框出来。</summary>
    public void OpenAudioDriverSettings()
    {
        Overlay.CloseActive();
        Navigate("settings");
        SettingsInOverlay()?.RevealAudioDriver();
    }

    /// <summary>主页日历/待办的图标(hover 变齿轮)点进来:跳到设置里的「与 Apple 同步」并高亮那一块。</summary>
    public void OpenAppleSyncSettings()
    {
        Overlay.CloseActive();      // 可能有抽屉开着,先收起再跳页
        Navigate("settings");
        SettingsInOverlay()?.RevealAppleSync();
    }

    /// <summary>从项目抽屉/主页进入某项目的项目聊天:关抽屉 -> 切到聊天 -> 选中该项目上下文。</summary>
    public void OpenProjectInChat(string projectId)
    {
        Overlay.CloseActive();
        Navigate("chat");
        if (ContentHost.Content is ChatView cv) cv.SelectProject(projectId);
        TheApp.Projects.Touch(projectId);
    }

    void OnToggleNav(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        ApplyCollapsed();
    }

    void ApplyCollapsed()
    {
        NavColumn.Width = new GridLength(_collapsed ? 58 : 240);
        // 任务抽屉从底部升起,但要【避开左侧导航栏】(用户裁定):左缩进 = 当前导航宽。
        TaskDrawer.Margin = new Thickness(_collapsed ? 58 : 240, 0, 0, 0);
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
        // 分组标题在收起状态没有宽度显示,整条隐藏(上下两个面板都要处理)
        foreach (var c in NavPanel.Children) if (c is TextBlock t) t.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        foreach (var c in NavSystemPanel.Children) if (c is TextBlock t) t.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        RefreshMember();   // 状态块随收起/展开切换长短文案
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ★★★ `EnsureHostProbe` 已删(V19 · 2026-08-08)。
    //
    //  它每 20 秒 ping 一次**本机回环管理面**(`HubAdmin.ProbeAsync`),给左下角那一格
    //  拿「中枢正在这台上跑」的肯定证据。而 `HubAdmin` 整个要搬进管理端 ——
    //  ★ 这是 V10 §2.1 那六个文件**之外**的一处漏网运行期主机分支:
    //    地图上没有它,搬迁那天它会**当场编不过**(还算好),
    //    或者被顺手改成恒 false(那就是静默退化)。
    //
    //  ★★ 摘掉它**换来的不只是编得过** —— 纪律②说客户端里不留运行期角色分支,
    //    而"每 20 秒探一次回环管理面"正是一条。
    //  ★★★ 但代价要如实记下:客户端从此**没有**「中枢跑没跑」的活证据了。
    //    ⇒ 那一格的**口径**必须跟着改(见 RefreshMember):它现在答的是【角色】,
    //      不是【中枢在不在跑】。文案不跟着改就是给错原因,而本项目的判词是
    //      **给错原因的提示比不给更坏**。
    //    ⇒ 「中枢跑没跑」现在由右上角那颗状态点回答(它是真的连过),
    //      那一格的 ToolTip 里明说了去哪儿看。
    // ════════════════════════════════════════════════════════════════════════

    public void RefreshStatus()
    {
        // ★ 连上中枢后,顶栏右上角改显【当前预期 token 输出速率】(用户裁定 2026-07-31)——
        //   连接这件事已由绿点表达,这块地方留给更有用的读数。其它状态(尚未配对/证书过期/被解除/
        //   连不上)仍如实显示,那些是必须让用户看见的事,不能被一个速率盖掉。
        // ★★★ 本机设备证书快到期的告警要排在【在线】这一格**之前**。
        //   这一条是「过期**之前**就要看得见」唯一能落地的地方,而它反直觉:
        //   过期之前客户端**正是在线的** —— 告警只挂在断线那几格的话,它永远等到过期之后才出现,
        //   而那时续签路由已经够不着了(lan-edge selftest 甲2 实测),唯一的出路只剩重新配对。
        //   ⇒ 挡住这块地方的是那个 tok/s 读数;它有用,但比不上"你还有 N 天可以自愈"。
        //   ★ RenewDue 段不会走到这里(CertWarning 在那一段返回 null)—— 正常自愈不打扰用户。
        if (TheApp.Hub.CertWarning is { Length: > 0 })
        {
            StatusText.Text = Strings.Get("status.local_cert_expired");
            StatusDot.Fill = (Brush)FindResource("RiskDanger");
            StatusText.ToolTip = TheApp.Hub.CertWarning;
            return;
        }
        StatusText.ToolTip = null;

        if (TheApp.Hub.State == HubState.Online)
        {
            StatusText.Text = TokenUsage.ExpectedOutputRate is { } r
                ? $"≈ {r:0} tok/s"
                : Strings.Get("status.rate_pending");   // 还没量到出字速度 -> 显示待定,不编数字
            StatusDot.Fill = (Brush)FindResource("RiskSafe");
            return;
        }

        var (key, brushKey) = TheApp.Hub.State switch
        {
            HubState.Connecting => ("status.connecting", "FgMuted"),
            HubState.NotPaired => ("status.not_paired", "RiskWarning"),
            HubState.Revoked => ("status.revoked", "RiskDanger"),
            HubState.CertExpired => ("status.cert_expired", "RiskDanger"),   // ★ 证书过期≠连不上(处置不同)
            HubState.Unauthorized => ("status.unauthorized", "RiskWarning"),
            // ★ 这两态以前都掉进下面那条 _ => "未连接" —— 而它们恰恰证明【中枢在】,
            //   显示成"未连接"会把人支去重启 Edge / 查防火墙 / 改地址,整整一趟无用功。
            HubState.HubServerError => ("status.hub_error", "RiskDanger"),
            HubState.ProtocolMismatch => ("status.proto_mismatch", "RiskWarning"),
            // ★ 链不到钉住的 CA:处置与"主机证书过期"正好相反(那边不必重配,这边必须重配)
            HubState.HubIdentityChanged => ("status.hub_changed", "RiskDanger"),
            // ★★ 本机这一侧的两态。此前都掉进下面那条 _ => "未连接" —— 而"未连接"会把人支去
            //   重启中枢 / 查防火墙 / 改地址,那台中枢从头到尾一点毛病没有。
            HubState.LocalCertExpired => ("status.local_cert_expired", "RiskDanger"),
            HubState.LocalProfileUnusable => ("status.local_unusable", "RiskDanger"),
            _ => ("status.offline", "FgMuted"),
        };
        StatusText.Text = Strings.Get(key);
        StatusDot.Fill = (Brush)FindResource(brushKey);
    }

    // 左下角状态行:左=当前使用者,右=本机主副机 + 本周 token 用量(用户裁定)。
    public void RefreshMember()
    {
        var paired = TheApp.Hub.IsPaired;
        // ════════════════════════════════════════════════════════════════════
        //  ★★★ V19(2026-08-08)· 这一格**换了口径**,不只是换了数据源。
        //
        //  以前:`confirmedHub = HubAdmin.LastProbe == Ok` —— 探本机回环管理面答不答话。
        //        那答的是【中枢正在这台上跑吗】,而且是**活证据**(每 20 秒一探)。
        //  现在:`App.Boot?.Role.IsHost` —— D36 的角色判定(地址解析到本机,
        //        或者「装着管理端 + 铸过身份」这条安装事实)。
        //        那答的是【这台机器是不是主机】,是**安装事实**,不是运行状态。
        //
        //  ★★ 这两件事**不是一回事**,而且差别正好落在最容易骗到人的那一处:
        //     装着管理端、身份也在,但网关/Edge 没起来 —— 新口径写「主机」(对),
        //     而按旧口径读它的人会以为「中枢在跑」(错)。
        //     ⇒ 所以文案必须**明说它不代表中枢在跑**,并指出去哪儿看。
        //       只改数据源不改文案,就是拿新证据说旧结论 ——
        //       **给错原因的提示比不给更坏**(本项目判词)。
        //
        //  ★ 为什么非换不可:旧依据 `HubAdmin.ProbeAsync` 整个要搬进管理端。
        //    见上面 `EnsureHostProbe` 那段墓碑。
        //  ★ 「(推测)」那个后缀一并去掉:`RoleVerdict` 不是一个关于运行状态的猜测,
        //    它是一条**有依据的判定**,而依据(`Role.Why`)现在原样摆在 ToolTip 里 ——
        //    那比一个"推测/确认"的二值标签说得多。
        //    ★★ 保留弱化字色的**另一种**用法:判定还没落定时(`Boot` 还是 null)。
        //      那时说什么都是编的,所以如实写「判定中…」而不是先默认成副机。
        // ════════════════════════════════════════════════════════════════════
        var role = App.Boot?.Role;
        var deciding = role is null;
        var isHub = role?.IsHost == true;
        if (_collapsed)
            HostText.Text = !paired ? "—" : deciding ? "…" : isHub ? "主" : "副";   // 收起时只留一个字
        else
            HostText.Text = Strings.Get(!paired ? "status.role_unpaired"
                                        : deciding ? "status.role_deciding"
                                        : isHub ? "status.role_host" : "status.role_client");
        HostText.SetResourceReference(TextBlock.ForegroundProperty,
                                      deciding && paired ? "FgSecondary" : "FgPrimary");
        HostText.ToolTip = !paired
            ? null
            : (deciding
                ? "开机角色判定还在跑 —— 判完这一格就会写定。现在不显示结论,是因为这时候说什么都是编的。"
                : "本机角色:" + (isHub ? "主机" : "副机")
                  + Environment.NewLine + "依据:" + (role!.Why)
                  + Environment.NewLine + Environment.NewLine
                  + "★ 这一格说的是【这台机器的角色】,依据是安装事实"
                  + "(装没装管理端 · 铸没铸中枢身份 · 中枢地址解析到谁)。"
                  + Environment.NewLine
                  + "★★ 它【不代表中枢正在跑】—— 管理端装着、身份也在,而网关/Edge 没起来时,"
                  + "这一格照样写「主机」。想知道中枢跑没跑,看右上角那颗状态点(它是真的连过)。")
              // ★★ V13(D?):把**业务调用实际走哪条路**也摆出来。
              //   这两条路决定的是档位(回环 ⇒ trusted-local,能改驻留集合;
              //   经 Edge ⇒ lan-device,改不动),而 2026-08-07 那天之所以查了一整天,
              //   正是因为**屏幕上没有任何一处说得出这次请求是从哪条路发出去的**。
              + "\n\n业务通道:" + TheApp.Hub.RouteNote;

        // ---- 当前使用者(推测)----
        // ★ 用户裁定:这一格显示【推测的使用者身份】,不是连接状态 —— 连接状态只在右边 token 块里说,
        //   两处都写"未连接中枢"是重复。主机没连上就【沿用上次推测的缓存】。
        // ★ D45 铁律:仅用于显示,任何权限/可见范围判定都不读它(见 IdentityGuess 顶部说明)。
        var who = IdentityGuess.Current(TheApp.Hub, TheApp.Settings);
        MemberText.Text = who.DisplayName;
        // 已被主机确认 = 正常字色;还只是推测/缓存 = 弱化,别让人误以为身份已确认
        MemberText.SetResourceReference(TextBlock.ForegroundProperty, who.IsGuess ? "FgSecondary" : "FgPrimary");
        MemberText.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;   // 收起时只留头像
        MemberInitial.Text = FirstGlyph(who.DisplayName);
        MemberAvatar.SetResourceReference(Border.BackgroundProperty, who.IsGuess ? "BgSunken" : "BgSelected");
        // ★ 前景要跟着底色走(审计 2026-07-31):推测态把底色换成 BgSunken 了,
        //   而前景仍是 FgOnSelected(白)—— 在墨白皮肤下 BgSunken 近白底,白字直接消失。
        //   而推测态本就是常态(未接入中枢时没有真名字缓存)。FgSecondary 三皮肤都够看。
        MemberInitial.SetResourceReference(TextBlock.ForegroundProperty, who.IsGuess ? "FgSecondary" : "FgOnSelected");
        MemberBlock.ToolTip = $"当前使用者:{who.DisplayName}({who.SourceNote})";

        // ★ token 用量尚未接入 -> 如实标注"待接入",绝不编数字(见 TokenUsage)。
        TokenText.Text = TokenUsage.Connected
            ? $"{Strings.Get("usage.this_week")} {TokenUsage.Week:N0}"
            : $"{Strings.Get("usage.this_week")} · {Strings.Get("usage.pending")}";
        TokenText.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    // 头像里的首字:中文取末字(姓名习惯看名),拉丁取首字母大写;空则占位。
    static string FirstGlyph(string name)
    {
        var s = name.Trim();
        if (s.Length == 0) return "·";
        var first = s[0];
        // 中日韩:直接用首个字(名字通常两三个字,首字已够辨识)
        if (first >= 0x3400) return first.ToString();
        return char.ToUpperInvariant(first).ToString();
    }

    // 点状态块 -> 浮窗:今日 / 本周 / 本月 / 累计 的 token 用量表(未接入时全为"—" + 说明)。
    void OnOpenUsage(object sender, RoutedEventArgs e)
    {
        var table = new StackPanel();
        void Row(string label, long? val)
        {
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            l.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            var v = new TextBlock { Text = val is { } n ? n.ToString("N0") : "—", VerticalAlignment = VerticalAlignment.Center };
            v.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            var d = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 4, 0, 4) };
            DockPanel.SetDock(v, Dock.Right);
            d.Children.Add(v);
            d.Children.Add(l);
            table.Children.Add(d);
        }
        Row(Strings.Get("usage.day"), TokenUsage.Today);
        Row(Strings.Get("usage.week"), TokenUsage.Week);
        Row(Strings.Get("usage.month"), TokenUsage.Month);
        Row(Strings.Get("usage.total"), TokenUsage.Total);
        if (!TokenUsage.Connected)
        {
            table.Children.Add(new Border { Height = 6 });
            table.Children.Add(Ui.Caption(Strings.Get("usage.pending_note")));
        }
        Flyout.Show((FrameworkElement)sender, Strings.Get("usage.title"), table, width: 240);
    }
}
