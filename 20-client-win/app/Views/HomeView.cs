// P3c -- 主页(= 今天)。用户裁定的布局:
//   ┌────────────────────────────┬────────┐
//   │ 日历(周横排,固定高)        │ 待办    │   ← 最上面的板块;日历占宽多,待办占少
//   ├────────────────────────────┴────────┤
//   │ 天气三城(固定高,只占所需)          │
//   ├─────────────────────────────────────┤
//   │ 正在进行的项目(占满剩余,方块平分)   │
//   └─────────────────────────────────────┘
//
// 关键裁定:
//   · 简报【不在主页】,移到右侧消息栏抽屉(BriefingDrawerView);
//   · 日历放最上面、占宽多,日期【以周横排】、【高度固定】;待办在其右侧占宽少;
//   · 天气【固定高度】—— 只占所需信息的高度,不再随窗口拉伸吃掉版面;
//   · 项目占满剩余空间,方块平分整宽、可滚动。
//
// 响应式:能连续的连续插值、离散的带迟滞、拖拽期节流(见 Layout.cs 与 ScheduleRelayout)。
// 数据未接入(天气等出境白名单 / 日历等 Apple 接入):曲线与逐小时用无数据基线,
// 绝不画假数字冒充实时(设计 §4.1 / 状态矩阵 §8)。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LocalAI.Client.I18n;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class HomeView : UserControl
{
    // 地点表:第 0 项 = 当前所在地(系统时区推断,标「当前」,不可拖动);其后可拖拽排序。
    List<Place> _places = new();

    /// <summary>天气板块固定高度:标题 + 温度行 + 状态两行 + 曲线 + 逐小时,只占所需。</summary>
    const double WeatherHeight = 208;

    /// <summary>
    /// 天气卡之间的间距。★ 三张卡的外边距必须【完全相同】,否则宽度不等 ——
    /// UniformGrid 分的是等宽的格,但卡片自身边距不同就会导致实际宽度差一截
    /// (之前末格没有右边距,于是宽出 12px)。末尾多出的一段由容器的负右边距吸收。
    /// </summary>
    const double WeatherGap = 12;

    /// <summary>折叠城市那一行的高度。★ 两行折叠 + 间隔 = 让左侧日历板块往下延伸的那段空间。</summary>
    public const double CollapsedCityHeight = 34;

    readonly TextBlock _greeting = new() { FontWeight = FontWeights.SemiBold, FontSize = 30, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _greetingSub = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    readonly StackPanel _greetingBox = new() { HorizontalAlignment = HorizontalAlignment.Left };
    TextBlock[] _cityTime = Array.Empty<TextBlock>();
    TextBlock[] _cityMeta = Array.Empty<TextBlock>();
    UniformGrid[] _cityHourly = Array.Empty<UniformGrid>();

    // ★ 天气改为【一张展开 + 其余折叠】(用户裁定 2026-07-31):
    //   三张卡横排太占宽,而多数时候只关心当前所在地。折叠的那两个仍显示
    //   地名/时间/最高最低 —— 折叠是"收起细节",不是"藏起来"。
    //   悬停到折叠行 -> 它展开、其余收起;鼠标离开整块 -> 恢复默认(第 0 个 = 当前所在地)。
    readonly StackPanel _weatherStack = new();
    FrameworkElement[] _cityExpanded = Array.Empty<FrameworkElement>();
    FrameworkElement[] _cityCollapsed = Array.Empty<FrameworkElement>();
    TextBlock[] _miniTime = Array.Empty<TextBlock>();
    TextBlock[] _miniTemp = Array.Empty<TextBlock>();
    int _weatherFocus;                       // 当前展开的是第几个
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    readonly Grid _root = new();
    readonly ColumnDefinition _todoColumn = new();
    readonly UniformGrid _tiles = new();
    readonly StackPanel _todoList = new();
    readonly Border _todoPanel;
    // 完成后停留 3 秒再归档:宽限期内每 250ms 巡一次,到点把已完成项【向右划出】再移除
    readonly DispatcherTimer _todoGrace = new() { Interval = TimeSpan.FromMilliseconds(250) };
    TextBlock? _archiveLabel;
    readonly Dictionary<string, FrameworkElement> _todoRows = new();   // id -> 当前显示的行,供划出动画定位
    readonly HashSet<string> _todoAnimatingOut = new();               // 正在划出的行(动画期间不重复触发、不可交互)
    readonly ScrollViewer _pageScroll = new();

    App TheApp => (App)Application.Current;

    // 主页板块显隐(用户在"扩展 › 主页板块"里勾选)。构建时读取;隐藏的不占版面。
    readonly bool _calVisible, _todoVisible, _weatherVisible, _projectsVisible;

    public HomeView()
    {
        _todoFilter = TheApp.Settings.HomeTodoFilter;   // 读回上次选的分类
        _calVisible = TheApp.Settings.IsPanelVisible("calendar");
        _todoVisible = TheApp.Settings.IsPanelVisible("todo");
        _weatherVisible = TheApp.Settings.IsPanelVisible("weather");
        _projectsVisible = TheApp.Settings.IsPanelVisible("projects");

        _greeting.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        _greetingSub.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");

        _root.Margin = new Thickness(24, 14, 24, 18);
        // 用户裁定:待办要与下方天气板块【对齐】,日历占【两个天气板块 + 间隔】的宽度。
        //   做法:待办列宽 = 一个天气卡宽(在 RelayoutContinuous 里按窗口算),日历列吃掉剩余(星号)。
        //   于是待办正好压在最右那张天气卡上,日历正好等于其余两卡 + 中间那道 12px 间隔。
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // 日历(占剩余)
        _todoColumn.Width = new GridLength(1, GridUnitType.Star);                                             // 待办(启动后改为一卡宽 px)
        _root.ColumnDefinitions.Add(_todoColumn);
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                   // 问候
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                   // 日历 | 待办(固定高)
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                   // 天气(固定高)
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 130 }); // 项目(占满剩余)

        // ① 问候:大字号 + 上下更宽的留白 + 一句小助手问候;占约 1/3 宽(左侧)
        _greetingBox.Margin = new Thickness(2, 16, 0, 24);
        _greetingBox.Children.Add(_greeting);
        _greetingBox.Children.Add(_greetingSub);
        Grid.SetRow(_greetingBox, 0); Grid.SetColumnSpan(_greetingBox, 2);
        _root.Children.Add(_greetingBox);

        // ② 日历(周横排,固定高)| 待办(窄)。任一隐藏则另一块占满整行;都隐藏则整行塌陷。
        Border? calPanel = null;
        if (_calVisible)
        {
            // 与顶栏日历浮窗【同一个组件、同一套交互】(MiniCal 逻辑),这里用周排布、固定高
            var calView = new CalendarView(CalendarView.Mode.Week) { Height = CalendarView.PanelHeight };
            calPanel = Ui.Panel("日历", calView, IconName.Calendar, new Thickness(0, 0, _todoVisible ? 12 : 0, 12),
                iconAction: () => (Application.Current.MainWindow as MainWindow)?.OpenAppleSyncSettings(),
                iconActionTip: "与 Apple 家庭共享日历同步的设置",
                headerAction: SyncNowButton(calView));
            // ★ 日历【跨行 1-2】(用户裁定 2026-07-31):天气改成一展开+两折叠后挪到了右列,
            //   左侧因此空出一整块 —— 日历往下延伸与它对齐,时间轴才有地方放。
            Grid.SetRow(calPanel, 1); Grid.SetColumn(calPanel, 0); Grid.SetRowSpan(calPanel, 2);
            if (!_todoVisible) Grid.SetColumnSpan(calPanel, 2);   // 待办隐藏 -> 日历占满整行
            _root.Children.Add(calPanel);
        }

        _todoList.Margin = new Thickness(0, 2, 0, 0);
        var todoScroll = new ScrollViewer
        {
            Content = _todoList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough();

        // 底部一条:右下角「已完成 (N) ›」入口,点开右侧抽屉看已归档的
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 6, 0, 0), Margin = new Thickness(0, 6, 0, 0) };
        footer.SetResourceReference(Border.BorderBrushProperty, "Border");
        footer.Child = ArchiveButton();

        var todoBody = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom);
        todoBody.Children.Add(footer);
        todoBody.Children.Add(todoScroll);

        _todoPanel = Ui.Panel("待办事项", todoBody,
            IconName.Member, new Thickness(0, 0, 0, 12),
            headerAction: Ui.PlusButton(() => OpenTodoEditor(null), "新增待办事项"),
            // 图标 hover 变齿轮 -> 跳到"与 Apple 同步"设置(用户裁定)
            iconAction: () => (Application.Current.MainWindow as MainWindow)?.OpenAppleSyncSettings(),
            iconActionTip: "与 Apple 提醒事项同步的设置",
            titleAction: TodoFilterCaret());
        // 与日历等高 —— 两块并排,高度锁死才不会一高一矮(日历本体 + 标题行与内边距 ≈ 62)。
        _todoPanel.Height = CalendarView.PanelHeight + 62;
        if (_todoVisible)
        {
            BuildTodos();
            TheApp.Todos.Changed += BuildTodos;
            _todoGrace.Tick += (_, _) => SweepExpiredTodos();   // 宽限到点 -> 划出动画,不是整表重建
            Unloaded += (_, _) => { TheApp.Todos.Changed -= BuildTodos; _todoGrace.Stop(); };
            Grid.SetRow(_todoPanel, 1);
            Grid.SetColumn(_todoPanel, _calVisible ? 1 : 0);
            if (!_calVisible) Grid.SetColumnSpan(_todoPanel, 2);   // 日历隐藏 -> 待办占满整行
            _root.Children.Add(_todoPanel);
        }

        // ③ 天气:固定高度,只占所需。地点表可拖拽排序(首格锁定)
        _places = Places.Load(TheApp.Settings);   // 始终加载(供待办列宽对齐计算),但隐藏时不建 UI
        if (_weatherVisible)
        {
            BuildWeather();
            // ★ 鼠标离开整块 -> 恢复默认展开项(用户裁定)。挂在栈上而不是每张卡上,
            //   否则在卡与卡之间移动会反复触发"离开"。
            _weatherStack.MouseLeave += (_, _) => SetWeatherFocus(0);
            Grid.SetRow(_weatherStack, 2); Grid.SetColumn(_weatherStack, 1);
            _root.Children.Add(_weatherStack);
        }

        // ④ 项目方块:占满剩余,平分整宽,可滚动。隐藏则让该行不再占用剩余空间。
        if (_projectsVisible)
        {
            // ★ 顶端对齐 —— 否则 UniformGrid 会把仅有的一行拉伸到整个可用高度,
            //   方块看起来"纵向居中"。用户要的是【从上到下、从左到右】依次排布。
            _tiles.VerticalAlignment = VerticalAlignment.Top;
            _tiles.Columns = 4;
            // 右上角:打开【项目库】(已完成项目)
            var libBtn = ProjectLibraryButton();
            var projects = Ui.Panel(Strings.Get("project.resume"),
                new ScrollViewer
                {
                    Content = _tiles,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                }.PassThrough(),
                IconName.Tasks, new Thickness(0), headerAction: libBtn);
            Grid.SetRow(projects, 3); Grid.SetColumnSpan(projects, 2);
            _root.Children.Add(projects);

            BuildTiles();
            TheApp.Projects.Changed += BuildTiles;
            Unloaded += (_, _) => TheApp.Projects.Changed -= BuildTiles;
        }
        else
        {
            _root.RowDefinitions[3].Height = GridLength.Auto;   // 项目隐藏 -> 不再吃掉剩余空间
            _root.RowDefinitions[3].MinHeight = 0;
        }

        // 兜底逃生口:极端尺寸下允许整页纵向滚动 —— 宁可滚动也不裁切内容
        _pageScroll.Content = _root;
        _pageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _pageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Content = _pageScroll;

        SizeChanged += (_, _) => ScheduleRelayout();
        _resizeThrottle.Tick += (_, _) => { _resizeThrottle.Stop(); RelayoutDiscrete(); };

        UpdateClocks();
        _timer.Tick += (_, _) => UpdateClocks();
        Loaded += (_, _) => { RelayoutContinuous(); RelayoutDiscrete(); HookWindowVisibility(); SyncClockTimer(); };
        Unloaded += (_, _) => { _timer.Stop(); UnhookWindowVisibility(); };
        // ★ 被系统页(设置/模型/扩展)盖住时停表:那时主页整个不可见,
        //   秒针再走也没人看得到 —— 和显存条"不可见就停表"是同一条规矩(省电远比调长间隔有效)。
        //   盖住的做法是把宿主 IsEnabled 置 false(见 MainWindow.OpenSystemPage),所以认这个信号。
        IsEnabledChanged += (_, _) => SyncClockTimer();
    }

    // ★ 最小化/缩到托盘时也停表(审计 2026-07-31):那两种情况下 UserControl 的 IsVisible 仍为 true、
    //   Unloaded 也不触发(实测),所以光靠 IsEnabledChanged 收不住 —— 得盯【窗口】的状态。
    Window? _hostWin;
    void HookWindowVisibility()
    {
        _hostWin = Window.GetWindow(this);
        if (_hostWin is null) return;
        _hostWin.StateChanged += OnHostStateChanged;
        _hostWin.IsVisibleChanged += OnHostVisChanged;
    }
    void UnhookWindowVisibility()
    {
        if (_hostWin is null) return;
        _hostWin.StateChanged -= OnHostStateChanged;
        _hostWin.IsVisibleChanged -= OnHostVisChanged;
        _hostWin = null;
    }
    void OnHostStateChanged(object? s, EventArgs e) => SyncClockTimer();
    void OnHostVisChanged(object? s, DependencyPropertyChangedEventArgs e) => SyncClockTimer();

    /// <summary>秒针表:只在【主页真正可见】时走 —— 窗口可见且非最小化,且本控件启用(未被系统页盖住)。</summary>
    void SyncClockTimer()
    {
        var winVisible = _hostWin is null ? true : (_hostWin.IsVisible && _hostWin.WindowState != WindowState.Minimized);
        var shouldRun = IsEnabled && winVisible && IsLoaded;
        if (shouldRun) { if (!_timer.IsEnabled) _timer.Start(); }
        else _timer.Stop();
    }

    // ---------------------------------------------------------------- 随尺寸重排
    readonly DispatcherTimer _resizeThrottle = new() { Interval = TimeSpan.FromMilliseconds(60) };
    int _cols, _slots;
    int _tileCount;   // ★ BuildTiles 实际铺了几个方块 —— 列数上限只能是它(审计 2026-07-31)

    void ScheduleRelayout()
    {
        _resizeThrottle.Stop();
        _resizeThrottle.Start();   // 拖动停下 60ms 后才做需要重建子元素的活
        RelayoutContinuous();      // 连续量每帧跟手
    }

    void RelayoutContinuous()
    {
        if (ActualWidth <= 0) return;
        var contentW = ActualWidth - 48;                       // _root 左右各 24 外边距
        // 待办列 = 一个天气卡宽 ->与下方天气对齐;日历列吃剩余(= 两卡 + 间隔)。
        // 待办隐藏时列宽归 0(日历已 colspan 占满整行,列宽无所谓)。
        if (_todoVisible)
        {
            var n = Math.Max(1, _places.Count);
            var cardOuter = (contentW - (n - 1) * WeatherGap) / n;
            _todoColumn.Width = new GridLength(Math.Max(150, cardOuter));
        }
        else _todoColumn.Width = new GridLength(0);
        // 问候块占约 1/3 宽
        _greetingBox.Width = Math.Max(200, contentW / 3.0);
    }

    void RelayoutDiscrete()
    {
        var w = ActualWidth;
        if (w <= 0) return;

        // ★ 列数上限用【实际铺了几个方块】(Recent = 进行中),不是全部项目数 ——
        //   Items.Count 含已完成/已删/不可见,比方块多,于是最右一格是空白(审计 2026-07-31)。
        var cols = Layout.ProjectColumns(w - 8, _cols, Math.Max(1, _tileCount));
        if (cols != _cols) { _cols = cols; _tiles.Columns = cols; }

        // 天气隐藏时不建卡片(_cityHourly 为空),跳过逐小时重排,避免越界。
        if (!_weatherVisible || _cityHourly.Length != _places.Count) return;

        // 天气卡内可用宽度(减去卡片内边距与卡间距)
        var n = Math.Max(1, _places.Count);
        var cardW = (w - (n - 1) * WeatherGap) / n - 32;
        var slots = Layout.HourlySlots(cardW, _slots);
        if (slots != _slots)
        {
            _slots = slots;
            for (int i = 0; i < _places.Count; i++) SetHourly(_cityHourly[i], slots);
        }
    }

    // ---------------------------------------------------------------- 天气区(可拖拽排序)
    void BuildWeather()
    {
        _weatherStack.Children.Clear();
        _shifts.Clear();
        _hosts.Clear();
        var n = _places.Count;
        _cityTime = new TextBlock[n];
        _cityMeta = new TextBlock[n];
        _cityHourly = new UniformGrid[n];
        _cityExpanded = new FrameworkElement[n];
        _cityCollapsed = new FrameworkElement[n];
        _miniTime = new TextBlock[n];
        _miniTemp = new TextBlock[n];

        for (int i = 0; i < n; i++)
        {
            var idx = i;
            _cityExpanded[i] = CityCard(i);
            _cityExpanded[i].Height = WeatherHeight;
            _cityCollapsed[i] = CollapsedCity(i);
            // 悬停折叠行 -> 换成展开它(用户裁定)
            _cityCollapsed[i].MouseEnter += (_, _) => SetWeatherFocus(idx);
            _weatherStack.Children.Add(_cityExpanded[i]);
            _weatherStack.Children.Add(_cityCollapsed[i]);
        }
        SetWeatherFocus(_weatherFocus < n ? _weatherFocus : 0);
        UpdateClocks();
        RelayoutDiscrete();
    }

    /// <summary>切换展开哪一个:被选中的展开、其余折叠成窄行。靠可见性切换,不重建。</summary>
    void SetWeatherFocus(int i)
    {
        if (_cityExpanded.Length == 0) return;
        if (i < 0 || i >= _cityExpanded.Length) i = 0;
        _weatherFocus = i;
        for (int k = 0; k < _cityExpanded.Length; k++)
        {
            _cityExpanded[k].Visibility = k == i ? Visibility.Visible : Visibility.Collapsed;
            _cityCollapsed[k].Visibility = k == i ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>
    /// 折叠态的一行:地名 · 时间 · 最高/最低。
    /// ★ 折叠 = 收起细节,不是藏起来 —— 一眼仍能看到是哪座城、几点、冷热大概。
    /// </summary>
    FrameworkElement CollapsedCity(int i)
    {
        var place = _places[i];
        var name = new TextBlock
        {
            Text = place.IsCurrent ? place.City + " · 当前" : place.City,
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        name.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        _miniTime[i] = new TextBlock { Text = "—", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        _miniTime[i].SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _miniTime[i].SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        _miniTemp[i] = new TextBlock { Text = "最高 —  最低 —", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        _miniTemp[i].SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _miniTemp[i].SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Icons.Make(IconName.Weather, 13, "FgMuted"));
        var pad = new Border { Width = 6 };
        left.Children.Add(pad);
        left.Children.Add(name);
        left.Children.Add(_miniTime[i]);

        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(12, 0, 12, 0) };
        DockPanel.SetDock(left, Dock.Left); row.Children.Add(left);
        DockPanel.SetDock(_miniTemp[i], Dock.Right); row.Children.Add(_miniTemp[i]);

        var card = new Border
        {
            Child = row,
            Height = CollapsedCityHeight,
            Margin = new Thickness(0, 0, 0, WeatherGap),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        card.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        return card;
    }

    /// <summary>
    /// 拖拽换位。★ 第 0 格是"当前所在地",既不能被拖走也不能被插到它前面(用户裁定)。
    /// </summary>
    void MovePlace(int from, int to)
    {
        if (from <= 0 || to <= 0) return;                      // 首格锁定
        if (from == to || from >= _places.Count || to >= _places.Count) return;
        var p = _places[from];
        _places.RemoveAt(from);
        _places.Insert(to, p);
        Places.SaveOrder(TheApp.Settings, _places.Skip(1));    // 首格不参与持久化顺序
        BuildWeather();
    }

    // ---------------------------------------------------------------- 城市卡
    FrameworkElement CityCard(int i)
    {
        var place = _places[i];
        var city = place.City;
        // 首格标「当前」(不是「家」)—— 它是由系统时区推断出的当前所在地
        var tag = place.IsCurrent ? "当前" : "";
        var draggable = i > 0;

        _cityTime[i] = new TextBlock { Text = "—", FontSize = 15, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Right };
        _cityTime[i].SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        _cityMeta[i] = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
        _cityMeta[i].SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _cityMeta[i].SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var timeCol = Ui.Stack(_cityTime[i], _cityMeta[i]);
        timeCol.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(timeCol, Dock.Right);

        var temp = new TextBlock { Text = "—°", FontSize = 30, FontWeight = FontWeights.Light, VerticalAlignment = VerticalAlignment.Center };
        temp.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");

        var topRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 2) };
        topRow.Children.Add(timeCol);
        topRow.Children.Add(temp);

        var st = new TextBlock { Text = "天气未接入", TextTrimming = TextTrimming.CharacterEllipsis };
        st.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        st.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var hl = new TextBlock { Text = "最高 —  最低 —  降水 —", TextTrimming = TextTrimming.CharacterEllipsis };
        hl.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        hl.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        // 气温曲线:天气板块固定高,曲线也固定高(不再随窗口伸缩 -> 不会显示不全)
        var curve = new Grid { Height = 40, Margin = new Thickness(0, 6, 0, 0), ClipToBounds = true };
        var baseline = new System.Windows.Shapes.Path
        {
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 4 },
            Stretch = Stretch.Fill,
            Data = Geometry.Parse("M0,10 L100,10"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        baseline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Border");
        curve.Children.Add(baseline);
        var noData = new TextBlock { Text = "今日气温曲线待接入", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        noData.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        noData.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        curve.Children.Add(noData);

        _cityHourly[i] = new UniformGrid { Rows = 1, Margin = new Thickness(0, 6, 0, 0) };
        SetHourly(_cityHourly[i], 6);

        var inner = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(topRow, Dock.Top); inner.Children.Add(topRow);
        DockPanel.SetDock(st, Dock.Top); inner.Children.Add(st);
        DockPanel.SetDock(hl, Dock.Top); inner.Children.Add(hl);
        DockPanel.SetDock(_cityHourly[i], Dock.Bottom); inner.Children.Add(_cityHourly[i]);
        inner.Children.Add(curve);

        var title = string.IsNullOrEmpty(tag) ? city : $"{city} · {tag}";
        // 所有卡片【同一边距】-> 宽度完全相等(末尾多出的一段由容器负边距吸收)
        var card = Ui.Panel(title, inner, IconName.Weather, new Thickness(0, 0, WeatherGap, 0));

        if (!draggable)
        {
            // 首格 = 当前所在地,固定不动。不给拖动光标,也不放角标。
            return card;
        }

        // 右下角角标:提示这张卡可以拖(靠角标本身表达,不用 ToolTip)
        var grip = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M1,7 L7,1 M1,11 L11,1 M5,11 L11,5"),
            StrokeThickness = 1.5,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeStartLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 7, 5),
            IsHitTestVisible = false,
            Opacity = 0.55,
        };
        grip.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "FgSecondary");

        // ★ 只有【右下角这块手柄区】才起手拖动 —— 不是整块板块(用户裁定)。
        //   角标细、不好点,所以给它一个 30×30 的透明命中区兜住整个角。
        //   卡片自身的右外边距因"是否末格"而不同(12 或 0),命中区/角标都要把它算进来,
        //   否则末格的角落会偏出 12px。
        var gripZone = new Grid
        {
            Width = 30,
            Height = 30,
            Background = System.Windows.Media.Brushes.Transparent,   // 透明但可命中
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, WeatherGap, 0),
            Cursor = System.Windows.Input.Cursors.SizeAll,
        };
        gripZone.Children.Add(grip);

        var host = new Grid();
        host.Children.Add(card);
        host.Children.Add(gripZone);

        var shift = new TranslateTransform();
        host.RenderTransform = shift;
        _hosts[i] = host;
        _shifts[i] = shift;

        // ★ 横向拖拽换位【已停用】(2026-07-31 改成竖排折叠之后):
        //   那套拖拽算的是横向位移(dx),而现在只有一张展开、其余是折叠行 ——
        //   横拖无处可去。而且拖拽与悬停展开会打架。
        //   需要改顺序时去【设置 › 地点】改 —— 不在这里留一个拖不动的把手。
        gripZone.Visibility = Visibility.Collapsed;
        return host;
    }

    // ---------------------------------------------------------------- 拖拽排序(手动实现)
    // ★ 为什么不用 WPF 的 DragDrop.DoDragDrop:那是 OLE 拖放,它【根本不移动元素】,
    //   只换一个拖放光标 —— 所以"被拖的卡不跟随鼠标"是必然结果(用户反馈)。
    //   而且让位动画挂在 DragOver 上,该事件持续触发,动画被反复重启,看起来就"很抽"。
    // 现在改为自己捕获鼠标:
    //   · 被拖的卡【直接跟手】(逐帧设位移,不加动画,才不会有迟滞感);
    //   · 其它卡只在【目标位置改变时】才动一次动画(不再每次移动都重启);
    //   · 松手时提交新顺序。
    readonly Dictionary<int, Grid> _hosts = new();
    readonly Dictionary<int, TranslateTransform> _shifts = new();
    int? _dragIndex;
    int _dragTarget;
    Point _dragOrigin;

    void BeginDrag(int index, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragIndex = index;
        _dragTarget = index;
        _dragOrigin = e.GetPosition(_weatherStack);
        if (_hosts.TryGetValue(index, out var host))
        {
            Panel.SetZIndex(host, 10);          // 拖起来的卡浮在其它卡之上
            host.Opacity = 0.94;
        }
        _weatherStack.CaptureMouse();
        _weatherStack.MouseMove += OnDragMove;
        _weatherStack.MouseLeftButtonUp += OnDragEnd;
        _weatherStack.LostMouseCapture += OnDragLost;
    }

    void OnDragMove(object? sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragIndex is not int from) return;
        var dx = e.GetPosition(_weatherStack).X - _dragOrigin.X;

        // 被拖的卡逐帧跟手 —— 不用动画,否则永远追不上鼠标
        if (_shifts.TryGetValue(from, out var t))
        {
            t.BeginAnimation(TranslateTransform.XProperty, null);   // 清掉可能残留的动画
            t.X = dx;
        }

        // 目标位置 = 位移换算成整数格;只在【变化时】重排让位,避免动画被反复重启
        var step = ColumnWidth();
        var target = Math.Clamp(from + (int)Math.Round(dx / Math.Max(1, step)), 1, _places.Count - 1);
        if (target == _dragTarget) return;
        _dragTarget = target;
        ApplyGaps(from, target, step);
    }

    /// <summary>把"若放在 target"的结果预演出来:被跨过的卡朝相反方向让开一格。</summary>
    void ApplyGaps(int from, int target, double step)
    {
        for (int k = 1; k < _places.Count; k++)
        {
            if (k == from) continue;                       // 被拖的那张由鼠标控制
            double to = 0;
            if (from < target && k > from && k <= target) to = -step;
            else if (from > target && k >= target && k < from) to = step;
            AnimateShift(k, to);
        }
    }

    void OnDragEnd(object? sender, System.Windows.Input.MouseButtonEventArgs e) => FinishDrag(commit: true);
    void OnDragLost(object? sender, System.Windows.Input.MouseEventArgs e) => FinishDrag(commit: false);

    void FinishDrag(bool commit)
    {
        if (_dragIndex is not int from) return;
        var target = _dragTarget;

        _weatherStack.MouseMove -= OnDragMove;
        _weatherStack.MouseLeftButtonUp -= OnDragEnd;
        _weatherStack.LostMouseCapture -= OnDragLost;
        if (_weatherStack.IsMouseCaptured) _weatherStack.ReleaseMouseCapture();
        _dragIndex = null;

        var swap = commit && target != from;
        // ★ 松手要【滑过去】而不是瞬间弹过去(用户裁定):
        //   先把被拖的卡从当前手位平滑动画到目标格位,动画结束后才重建列表。
        //   重建时新卡就在目标位置、位移为 0 —— 与动画终点像素重合,所以看不到跳变。
        var settleTo = swap ? (target - from) * ColumnWidth() : 0;

        void Land()
        {
            if (_hosts.TryGetValue(from, out var h2)) { Panel.SetZIndex(h2, 0); h2.Opacity = 1; }
            if (swap) MovePlace(from, target);   // 重建天气区,位移归零
        }

        if (_shifts.TryGetValue(from, out var t))
        {
            var anim = new DoubleAnimation(settleTo, TimeSpan.FromMilliseconds(190))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            anim.Completed += (_, _) => Land();
            // 从当前手位起算:先把动画值锚在当前 X,避免从 0 开始跳一下
            t.BeginAnimation(TranslateTransform.XProperty, null);
            var fromX = t.X;
            anim.From = fromX;
            t.BeginAnimation(TranslateTransform.XProperty, anim);
        }
        else Land();

        // 其余卡同步滑回各自的最终位置(它们在拖动期间已经让位到那里了)
        if (!swap) for (int k = 0; k < _places.Count; k++) if (k != from) AnimateShift(k, 0);
    }

    void AnimateShift(int index, double toX)
    {
        if (!_shifts.TryGetValue(index, out var t)) return;
        var anim = new DoubleAnimation(toX, TimeSpan.FromMilliseconds(170))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        t.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    double ColumnWidth()
    {
        var n = Math.Max(1, _places.Count);
        return _weatherStack.ActualWidth > 0 ? _weatherStack.ActualWidth / n : 220;
    }

    static void SetHourly(UniformGrid grid, int slots)
    {
        if (grid.Columns == slots && grid.Children.Count == slots) return;
        grid.Columns = slots;
        grid.Children.Clear();
        var h0 = DateTime.Now.Hour;
        // 格子多 -> 间隔更细(1h/2h/3h)。多出来的宽度换成更细的时间粒度,而不是更空的格子。
        var step = Layout.HourlyStepHours(slots);
        for (int k = 0; k < slots; k++)
        {
            var hr = new TextBlock { Text = $"{(h0 + k * step) % 24:00}", HorizontalAlignment = HorizontalAlignment.Center };
            hr.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            hr.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            var ic = new TextBlock { Text = "—", HorizontalAlignment = HorizontalAlignment.Center };
            ic.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            var tp = new TextBlock { Text = "—°", HorizontalAlignment = HorizontalAlignment.Center };
            tp.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            tp.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            grid.Children.Add(Ui.Stack(hr, ic, tp));
        }
    }

    // ---------------------------------------------------------------- 待办与家务(仿提醒事项)
    /// <summary>
    /// 日历右上角的【立即拉取】按钮:一次把 Apple 的日历与提醒事项都拉下来(用户要求 2026-07-31)。
    /// ★ 图标本身不是按钮 —— 外面套一个透明的命中块(项目一贯做法:图标才十几像素,只让它可点会经常按空)。
    /// ★ 没连 Apple 时【不显示】:给一个点了只会说"还没连接"的按钮,不如不给。
    /// </summary>
    FrameworkElement? SyncNowButton(CalendarView calView)
    {
        if (Services.AppleCredentials.Load() is not { HasPassword: true }) return null;

        var glyph = Icons.Make(IconName.Refresh, 15, "FgSecondary");
        glyph.VerticalAlignment = VerticalAlignment.Center;
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.IsHitTestVisible = false;                     // 命中交给外面那块

        var hit = new Grid
        {
            Width = 26,
            Height = 24,
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "立即从 Apple 拉取日历与提醒事项",
        };
        hit.Children.Add(glyph);

        var busy = false;
        hit.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            if (busy) return;                               // 防连点:一次拉取跑完再说
            busy = true;
            var spin = new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
            { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
            var rot = new System.Windows.Media.RotateTransform();
            glyph.RenderTransformOrigin = new Point(0.5, 0.5);
            glyph.RenderTransform = rot;
            rot.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin);
            try
            {
                var r = await Services.AppleCalendarSync.PullAsync(
                    TheApp.Settings, Services.MemberContext.Current, "家庭");
                if (r.Ok) Services.AppleAutoSync.NoteManualSuccess();
                hit.ToolTip = r.Message;                    // 结果如实挂在提示上,不弹窗打断
                calView.Rebuild();
            }
            finally
            {
                rot.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
                glyph.RenderTransform = null;
                busy = false;
            }
        };
        return hit;
    }

    void OpenTodoEditor(TodoItem? existing)
    {
        var body = TodoEditor.Build(existing);
        (Application.Current.MainWindow as MainWindow)?.OpenSideDrawer(
            existing is null ? "新建待办事项" : "编辑待办事项", body, IconName.Member);
    }

    // ---------------------------------------------------------------- 待办分类(仿提醒事项)
    // 全部 / 今天 / 待办 / 家务 / 采购清单。选择存在本机偏好里,下次打开还是这个分类。
    string _todoFilter = "all";   // 由 Build() 从 AppSettings 读回

    static readonly (string Key, string Label)[] TodoFilters =
    {
        ("all", "全部"), ("today", "今天"), ("personal", "待办"), ("chore", "家务"), ("shopping", "采购清单"),
    };

    static string TodoFilterLabel(string key) => TodoFilters.FirstOrDefault(f => f.Key == key).Label ?? "全部";

    TextBlock? _todoFilterLabel;   // 标题右侧胶囊里的分类名(切换时就地改,不重建面板)

    IEnumerable<TodoItem> FilterTodos(IEnumerable<TodoItem> src) => _todoFilter switch
    {
        // 今天 = 有截止且【今天或更早】(逾期的当然也要看见)
        "today" => src.Where(t => t.Due is { } d && d.Date <= DateTime.Today),
        "personal" => src.Where(t => t.Kind == TodoKind.Personal),
        "chore" => src.Where(t => t.Kind == TodoKind.Chore),
        "shopping" => src.Where(t => t.Kind == TodoKind.Shopping),
        _ => src,
    };

    // 标题右侧的分类胶囊:显示当前分类 + 下拉箭头(仿提醒事项换清单)
    FrameworkElement TodoFilterCaret()
    {
        _todoFilterLabel = new TextBlock { Text = TodoFilterLabel(_todoFilter), VerticalAlignment = VerticalAlignment.Center };
        _todoFilterLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        _todoFilterLabel.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var caret = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L8,0 L4,5 Z"),
            Width = 8, Height = 5, Stretch = Stretch.Fill, IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 1, 0, 0),
        };
        caret.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "FgMuted");

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_todoFilterLabel);
        row.Children.Add(caret);

        var b = new Border
        {
            Child = row, Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(8, 1, 0, 0),
            Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(1), ToolTip = "切换分类(全部 / 今天 / 待办 / 家务 / 采购清单)",
        };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var m = new ContextMenu();
            foreach (var (key, label) in TodoFilters)
            {
                var mi = new MenuItem { Header = label, IsChecked = _todoFilter == key };
                var captured = key;
                mi.Click += (_, _) =>
                {
                    if (_todoFilter == captured) return;
                    _todoFilter = captured;
                    TheApp.Settings.HomeTodoFilter = captured;
                    TheApp.Settings.Save();
                    if (_todoFilterLabel is not null) _todoFilterLabel.Text = TodoFilterLabel(captured);
                    BuildTodos();   // 只重建列表,面板不动
                };
                m.Items.Add(mi);
            }
            MenuHost.Show(m, b);
        };
        return b;
    }

    void BuildTodos()
    {
        _todoList.Children.Clear();
        _todoRows.Clear();
        _todoAnimatingOut.Clear();   // 整表重建 -> 旧的划出动画作废(元素已被清掉)
        var items = FilterTodos(TheApp.Todos.Active()).ToList();
        if (items.Count == 0)
        {
            _todoList.Children.Add(Ui.Body(_todoFilter == "all" ? "没有待办事项了。" : $"「{TodoFilterLabel(_todoFilter)}」里没有事项。", muted: true));
            _todoList.Children.Add(Ui.Caption("点右上角 + 新建;点标题旁的箭头可切换分类。"));
        }
        else
        {
            foreach (var t in items)
            {
                var row = TodoList.Row(t, () => TheApp.Todos.Toggle(t.Id), () => OpenTodoEditor(t));
                _todoRows[t.Id] = row;
                _todoList.Children.Add(row);
            }
        }

        // 已完成计数刷新
        if (_archiveLabel is not null) _archiveLabel.Text = $"已完成 ({TheApp.Todos.CompletedCount})";

        // 还有处于 3 秒宽限期的项 -> 保持巡查,到点触发划出;否则停表
        if (TheApp.Todos.HasGrace()) { if (!_todoGrace.IsEnabled) _todoGrace.Start(); }
        else _todoGrace.Stop();
    }

    // 宽限到点的项:向右划出 + 淡出,划完再从列表移除(此时它已在"已完成"抽屉里)。
    void SweepExpiredTodos()
    {
        var now = DateTime.Now;
        foreach (var kv in _todoRows.ToList())
        {
            if (_todoAnimatingOut.Contains(kv.Key)) continue;
            var item = TheApp.Todos.Items.FirstOrDefault(x => x.Id == kv.Key);
            if (item is null) continue;
            if (item.Done && item.CompletedAt is { } c && (now - c).TotalSeconds >= TodoCenter.ArchiveGraceSeconds)
                AnimateTodoOut(kv.Key, kv.Value);
        }
        if (!TheApp.Todos.HasGrace() && _todoAnimatingOut.Count == 0) _todoGrace.Stop();
    }

    void AnimateTodoOut(string id, FrameworkElement row)
    {
        _todoAnimatingOut.Add(id);
        row.IsHitTestVisible = false;   // ★ 动画期间不可点击、不可交互(用户裁定)
        var tx = new TranslateTransform();
        row.RenderTransform = tx;

        var dist = row.ActualWidth > 0 ? row.ActualWidth + 24 : 340;
        var slide = new DoubleAnimation(0, dist, TimeSpan.FromMilliseconds(300))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        slide.Completed += (_, _) =>
        {
            _todoList.Children.Remove(row);
            _todoRows.Remove(id);
            _todoAnimatingOut.Remove(id);
            if (_todoList.Children.Count == 0) BuildTodos();     // 空了 -> 显示空态
            if (!TheApp.Todos.HasGrace() && _todoAnimatingOut.Count == 0) _todoGrace.Stop();
        };
        tx.BeginAnimation(TranslateTransform.XProperty, slide);
        row.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300)));
    }

    // 右下角「已完成 (N) ›」—— 点开右侧抽屉看已归档的
    FrameworkElement ArchiveButton()
    {
        _archiveLabel = new TextBlock { Text = "已完成 (0)", VerticalAlignment = VerticalAlignment.Center };
        _archiveLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        _archiveLabel.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var chev = Icons.Make(IconName.ChevronRight, 12, "FgMuted");
        chev.VerticalAlignment = VerticalAlignment.Center;
        chev.Margin = new Thickness(3, 0, 0, 0);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(_archiveLabel);
        row.Children.Add(chev);

        var b = new Border
        {
            Child = row, Padding = new Thickness(8, 4, 6, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = System.Windows.Media.Brushes.Transparent,
        };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = System.Windows.Media.Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => OpenTodoArchive();
        return b;
    }

    void OpenTodoArchive()
        => (Application.Current.MainWindow as MainWindow)?.OpenSideDrawer("已完成", new TodoArchiveView(), IconName.Tasks);

    // ---------------------------------------------------------------- 项目方块
    void BuildTiles()
    {
        _tiles.Children.Clear();
        var items = TheApp.Projects.Recent().ToList();
        _tileCount = items.Count;   // ★ 列数上限只能是实际铺出的方块数(见 RelayoutDiscrete)
        if (items.Count == 0)
        {
            _tiles.Columns = 1;
            _tiles.Children.Add(Ui.Stack(
                Ui.Body("还没有正在进行的项目。", muted: true),
                Ui.Caption("接入后这里以方块列出可恢复的工作(对话 / 资产 / 课件草稿),点方块直达那个项目。只列你自己的 + 家庭的。")));
            return;
        }
        foreach (var p in items) _tiles.Children.Add(ProjectTile(p));
        _cols = 0;
        RelayoutContinuous(); RelayoutDiscrete();
    }

    Border ProjectTile(Project p)
    {
        var icon = Icons.Make(p.WorkspaceKey switch
        {
            "chat" => IconName.Chat,
            "assets" => IconName.Assets,
            "translation" => IconName.Translation,
            "courses" => IconName.Courses,
            "computer" => IconName.Computer,
            _ => IconName.Tasks,
        }, 18, "FgSecondary");
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.Margin = new Thickness(0, 0, 0, 6);

        var title = new TextBlock { Text = p.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 36 };
        title.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        var sub = new TextBlock { Text = p.Subtitle, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        sub.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var (scopeText, scopeKey) = p.Scope switch
        {
            ProjectScope.Family => (Strings.Get("visibility.family"), "ScopeFamily"),
            ProjectScope.Personal => (Strings.Get("visibility.personal"), "ScopePersonal"),
            _ => (Strings.Get("visibility.only_me"), "ScopeOnlyMe"),
        };
        var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, scopeKey);
        var scopeLabel = new TextBlock { Text = scopeText, VerticalAlignment = VerticalAlignment.Center };
        scopeLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        scopeLabel.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        // 状态(进行中/准备中)小标签 + 可见范围 —— 让"分进行中/准备中"在方块上一眼可辨
        var statusChip = ProjectUi.StatusChip(p.Status);
        statusChip.Margin = new Thickness(0, 0, 10, 0);
        // 右边留出三点按钮的位置(它在右下角),状态/范围文字不会钻到它下面
        var scopeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 30, 0) };
        scopeRow.Children.Add(statusChip);
        scopeRow.Children.Add(dot); scopeRow.Children.Add(scopeLabel);

        // 标题给右上角的置顶按钮留出位置,长标题不会顶到 pin 图标下面
        title.Margin = new Thickness(0, 0, 22, 0);

        var body = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(icon, Dock.Top); body.Children.Add(icon);
        DockPanel.SetDock(scopeRow, Dock.Bottom); body.Children.Add(scopeRow);
        DockPanel.SetDock(title, Dock.Top); body.Children.Add(title);
        DockPanel.SetDock(sub, Dock.Top); body.Children.Add(sub);

        var tile = new Border
        {
            Height = 126,
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 12, 12),
            // 置顶态:描边更粗 + 强调色 —— 常驻标识,不靠 hover(用户裁定)
            BorderThickness = new Thickness(p.Pinned ? 2 : 1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        tile.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        tile.SetResourceReference(Border.BorderBrushProperty, p.Pinned ? "Accent" : "Border");
        tile.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");

        // ★ 统一布局(用户裁定):置顶 pin 在【右上角】,三个点在【右下角】—— 项目抽屉里也是这样。
        //   平时隐藏,鼠标移到方块上才显示(已置顶的 pin 常亮)。
        var pinBtn = PinButton(p);
        pinBtn.HorizontalAlignment = HorizontalAlignment.Right;
        pinBtn.VerticalAlignment = VerticalAlignment.Top;
        // 主页只给精简菜单(置顶 / 在文件夹中打开);详细设置去对应工作空间的项目抽屉(用户裁定)
        var dots = ProjectUi.DotsButton(p, () => { }, homeMenu: true);
        dots.Opacity = 0;
        dots.HorizontalAlignment = HorizontalAlignment.Right;
        dots.VerticalAlignment = VerticalAlignment.Bottom;
        var overlay = new Grid();
        overlay.Children.Add(body);
        overlay.Children.Add(pinBtn);
        overlay.Children.Add(dots);
        tile.Child = overlay;

        tile.MouseEnter += (_, _) => { tile.SetResourceReference(Border.BackgroundProperty, "BgHover"); pinBtn.Opacity = 1; dots.Opacity = 1; };
        tile.MouseLeave += (_, _) => { tile.SetResourceReference(Border.BackgroundProperty, "BgSurface"); pinBtn.Opacity = 0; dots.Opacity = 0; };
        tile.MouseLeftButtonUp += (_, _) =>
        {
            // 菜单刚被这一下点关掉 -> 只关菜单,不要顺势进项目(用户反馈)
            if (ProjectUi.JustClosedMenu()) return;
            TheApp.Projects.Touch(p.ProjectId);
            (Application.Current.MainWindow as MainWindow)?.NavigateToProject(p.WorkspaceKey, p.ProjectId);
        };
        tile.ToolTip = $"{p.Title}\n{p.Subtitle}\n最近打开:{p.LastOpened:M月d日 HH:mm}";
        return tile;
    }

    FrameworkElement ProjectLibraryButton()
    {
        var t = new TextBlock { Text = "项目库", VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var chev = Icons.Make(IconName.ChevronRight, 12, "FgMuted");
        chev.VerticalAlignment = VerticalAlignment.Center;
        chev.Margin = new Thickness(3, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(t); row.Children.Add(chev);
        var b = new Border { Child = row, Padding = new Thickness(8, 4, 6, 4), Cursor = System.Windows.Input.Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = System.Windows.Media.Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => (Application.Current.MainWindow as MainWindow)?.OpenProjectLibrary();
        return b;
    }

    // 方块右上角的置顶按钮。未置顶=空心 pin,置顶=实心强调色 pin;点击切换。
    FrameworkElement PinButton(Project p)
    {
        var pin = new System.Windows.Shapes.Path
        {
            // 水滴形 pin(小尺寸下最易读作"置顶/图钉")
            Data = Geometry.Parse("M8,1.6 C5.4,1.6 3.3,3.7 3.3,6.3 C3.3,9.8 8,14.4 8,14.4 C8,14.4 12.7,9.8 12.7,6.3 C12.7,3.7 10.6,1.6 8,1.6 Z"),
            Width = 15, Height = 15, Stretch = Stretch.Uniform, StrokeThickness = 1.4,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        pin.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, p.Pinned ? "Accent" : "FgSecondary");
        if (p.Pinned) pin.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");

        var btn = new Border
        {
            Width = 30, Height = 30,   // 命中区放大(用户反馈太小)
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Opacity = 0,                       // 平时隐藏,tile hover 时置 1
            Child = pin,
            ToolTip = p.Pinned ? "取消置顶" : "置顶",
        };
        btn.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        btn.MouseEnter += (_, _) => btn.SetResourceReference(Border.BackgroundProperty, "BgSunken");
        btn.MouseLeave += (_, _) => btn.Background = System.Windows.Media.Brushes.Transparent;
        btn.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;   // 吃掉按下,避免松开落到方块上
        btn.MouseLeftButtonUp += (_, e) => { e.Handled = true; TheApp.Projects.TogglePin(p.ProjectId); };
        return btn;
    }

    void UpdateClocks()
    {
        // ★ 问候语始终要刷(与天气是否显示无关)—— 放在天气早退守卫【之前】。
        var hour = DateTime.Now.Hour;
        _greeting.Text = Greetings.TitleFor(hour);
        _greetingSub.Text = Greetings.SubFor(DateTime.Now);   // 同一小时内稳定,不每秒乱跳

        // 天气隐藏(数组为空)或重建期间长度不一致 -> 跳过城市时钟,避免越界
        if (!_weatherVisible || _cityTime.Length != _places.Count) return;

        var localOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        for (int i = 0; i < _places.Count; i++)
        {
            try
            {
                var z = TimeZoneInfo.FindSystemTimeZoneById(_places[i].TimeZoneId);
                var t = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, z);
                _cityTime[i].Text = t.ToString("HH:mm");
                var diff = (z.GetUtcOffset(DateTime.UtcNow) - localOffset).TotalHours;
                var diffText = Math.Abs(diff) < 0.01 ? "本地" : diff > 0 ? $"+{diff:0.#}h" : $"{diff:0.#}h";
                _cityMeta[i].Text = $"{(t.Hour is >= 6 and < 18 ? "昼" : "夜")} · {diffText}";
                // 折叠行也要跑铟 —— 它虽然只显示摘要,但时间必须是真的
                if (i < _miniTime.Length && _miniTime[i] is { } mt) mt.Text = t.ToString("HH:mm");
                // ★ 最高/最低仍是 "—":天气未接入,不编数字(与展开态同一口径)
            }
            catch
            {
                _cityTime[i].Text = "—"; _cityMeta[i].Text = "";
                if (i < _miniTime.Length && _miniTime[i] is { } mt2) mt2.Text = "—";
            }
        }
    }
}
