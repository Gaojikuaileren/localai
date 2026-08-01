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

    /// <summary>折叠城市那一行的高度(= 摘要行的高度;卡片收起时正好只露出它)。</summary>
    public const double CollapsedCityHeight = 34;

    /// <summary>
    /// 天气整块的【固定总高】。★ 用户裁定:不管展开的是哪一个,三者始终在同一个固定高度的框内 ——
    /// 高度会变的话下方板块会跟着上下跳,而且鼠标底下的东西一直在挪。
    /// </summary>
    public const double WeatherStackHeight = 300;

    /// <summary>卡片间距(比原来的 12 略紧,好把展开高留够)。</summary>
    const double CityGap = 10;

    /// <summary>板块之间的标准间隔(日历/天气 与下方"正在进行的项目"之间)。</summary>
    const double PanelGap = 12;

    /// <summary>展开态卡片的最矮可读高度 —— 低于它就不再压缩,改让整块可以滚。</summary>
    const double ExpandedCityMin = 150;

    /// <summary>地点数不超过它时,固定高度一定装得下 -> 直接禁掉滚动,免得动画中途闪滚动条。</summary>
    const int WeatherFitsCount = 4;

    /// <summary>
    /// 展开态的卡片高度 = 总高 - 其余各张折叠行(含间隔),倒推保证总和恒定。
    /// ★★ 【必须按实际张数算】。原来写死的是"减两张",只在正好 3 个地点时成立 ——
    ///   而地点数是会变的:Places.Load 会把与当前所在地重名的那个去重(系统时区改成中国标准时间
    ///   就只剩 2 张),用户也可以自己加城市。
    ///   算错的后果不是"差一点点":张数少了栈底空一截(刚做的"日历底部与天气栈对齐"就对到了空白上),
    ///   张数多了第 4 张起整张落在 300 的框外,被 ClipToBounds 裁得一点不剩 —— 看不见、碰不到、
    ///   于是永远展不开。
    /// ★ 下限 ExpandedCityMin 也是必须的:张数一多这个式子会算成负数,
    ///   而 detail.Height 拿到负值会【在构造期抛】—— 就是 WPF-PITFALLS 第 2 条那种"自检全过、程序打不开"。
    /// </summary>
    double ExpandedCityHeight
    {
        get
        {
            var n = Math.Max(1, _places.Count);
            // ★★ 最后一张卡【不留下边距】(见 WeatherCard),所以这里不再减那一道。
            //   之前每张都带下边距:末尾那 10px 是空白,于是【札幌看得见的底边】
            //   比 300px 的框底高出 10px —— 日历下沿对的是框底,看起来就是对不齐。
            return Math.Max(ExpandedCityMin, WeatherStackHeight - (n - 1) * (CollapsedCityHeight + CityGap));
        }
    }

    readonly TextBlock _greeting = new() { FontWeight = FontWeights.SemiBold, FontSize = 30, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _greetingSub = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    readonly StackPanel _greetingBox = new() { HorizontalAlignment = HorizontalAlignment.Left };
    TextBlock[] _cityTime = Array.Empty<TextBlock>();
    TextBlock[] _cityMeta = Array.Empty<TextBlock>();
    TextBlock[] _cityTemp = Array.Empty<TextBlock>();      // 展开卡里的大温度
    TextBlock[] _citySky = Array.Empty<TextBlock>();       // 展开卡里的天气状况
    TextBlock[] _cityHiLo = Array.Empty<TextBlock>();      // 最高/最低/降水
    TextBlock[] _cityStamp = Array.Empty<TextBlock>();     // ★ 这份读数是什么时候的(过期要如实说)
    ContentControl[] _cityIcon = Array.Empty<ContentControl>();   // 展开卡里的天气图标
    ContentControl[] _miniIcon = Array.Empty<ContentControl>();   // 摘要行左侧那个图标
    Grid[] _cityCurve = Array.Empty<Grid>();               // 今日气温曲线
    UniformGrid[] _cityHourly = Array.Empty<UniformGrid>();

    // ★ 天气改为【一张展开 + 其余折叠】(用户裁定 2026-07-31):
    //   三张卡横排太占宽,而多数时候只关心当前所在地。折叠的那两个仍显示
    //   地名/时间/最高最低 —— 折叠是"收起细节",不是"藏起来"。
    //   悬停到折叠行 -> 它展开、其余收起;鼠标离开整块 -> 恢复默认(第 0 个 = 当前所在地)。
    readonly StackPanel _weatherStack = new();
    Border[] _cityCards = Array.Empty<Border>();          // 每座城一张卡,高度在折叠/展开之间【动画】
    TranslateTransform[] _shifts = Array.Empty<TranslateTransform>();   // 拖拽期的位移(挤开/跟手)
    TextBlock[] _miniTime = Array.Empty<TextBlock>();
    TextBlock[] _miniSky = Array.Empty<TextBlock>();     // 当前气候(晴/多云/阴…)
    TextBlock[] _miniTemp = Array.Empty<TextBlock>();    // 折叠行的当前气温
    TextBlock[] _miniPart = Array.Empty<TextBlock>();    // 时段词(早上/下午/晚上…)
    TextBlock[] _miniName = Array.Empty<TextBlock>();    // 城市名(只有展开那张才补「· 当前」)
    FrameworkElement[] _miniBar = Array.Empty<FrameworkElement>();   // 温度滑条
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
        // ★★ 这两行的高度【不能只靠里面那一块擑着】——
        //   日历是跨这两行的,而行高各自由待办/天气钉住。在"扩展 › 主页板块"里关掉其中一个,
        //   那一行就只剩日历自己的一半在撞 —— 于是另一块被垂直居中、下沿不齐,
        //   时间轴也塔到 MinHeight。给两行补上与块内容无关的下限。
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = CalendarView.PanelHeight + 62 });   // 日历 | 待办
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = WeatherStackHeight + PanelGap });   // 天气
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
            // ★★ 日历(月排布)+ 周时间轴【合并成一个大板块】(用户裁定 2026-07-31):
            //   上半 = 月历,左右切【月】;下半 = 那一周的时间轴,左右切【周】。
            //   于是"哪一天"由上面选,"那天几点干什么"由下面看 —— 两件事各有各的横轴,不再挤在一起。
            //   周排布因此从主页取消(月历 + 时间轴已经把它要表达的都表达了)。
            var calView = new CalendarView(CalendarView.Mode.Month)
            {
                // ★ 高度取【只有月历】的那个 —— 之前用 PanelHeight(268) 是含下方当日区的尺寸,
                //   而合并板块里当日区已经交给时间轴了;再用 268 就是月历被压矮
                //   -> 最后一行裁掉一截(用户说的"日历的构造超高了、显示不全")。
                Height = CalendarView.MonthOnlyHeight,
                HideModeSwitch = true,
                HideDayArea = true,
                HideWeekdayHeader = true,   // 与下方时间轴共用中间那一行「周一 27…」
                HideTodayButton = true,     // 下方时间轴那个「现在」已经干这件事了
                LeftGutter = WeekTimeline.GutterWidth,   // 七列与下方时间轴对齐
            };
            var timeline = new WeekTimeline { MinHeight = 150 };
            // ★ 点时间轴里的日程 -> 走【日历自己的那个编辑抽屉】,不另造一套(用户裁定)
            timeline.OnEditEvent = ev => calView.OpenEditorFor(ev);
            // ★★ 新建的入口:时间轴空白处【双击】就在那个半小时上建。
            //   月历左下角那个「+ 新增日程」已按用户要求拿掉,
            //   但【不能把新建路径一并拿掉】—— 换成了不占界面的手势。
            timeline.OnCreateAt = when => calView.OpenEditorAt(when);
            // 上面选中哪天 -> 下面聚焦那一周(两块说的是同一周)
            calView.SelectionChanged = d => timeline.FocusWeekOf(d);
            // ★ 反向也要跟:在时间轴上翻周、月历却还停在旧月 —— 那就是上下两块各说各的周。
            timeline.WeekChanged = ws => calView.FocusWeekStart(ws);
            timeline.TodayRequested = () => calView.FocusToday();   // 按「现在」-> 选中日也回今天
            timeline.DayRolled = () => calView.Rebuild();   // 跨零点：月历的"今天"也要挪过去

            var calStack = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(calView, Dock.Top);
            calStack.Children.Add(calView);
            calStack.Children.Add(timeline);
            calPanel = Ui.Panel("日历", calStack, IconName.Calendar, new Thickness(0, 0, _todoVisible ? 12 : 0, 12),
                iconAction: () => (Application.Current.MainWindow as MainWindow)?.OpenAppleSyncSettings(),
                iconActionTip: "与 Apple 家庭共享日历同步的设置",
                // ★ 新建日程的入口在【板块标题栏】(用户裁定):
                //   点日期只是选中,新建得有自己的按钮 —— 两件事各走各的入口。
                headerAction: CalendarHeaderActions(calView));
            // ★ 日历【跨行 1-2】(用户裁定 2026-07-31):天气改成一展开+两折叠后挪到了右列,
            //   左侧因此空出一整块 —— 日历往下延伸与它对齐,时间轴才有地方放。
            // ★ 底部与【武汉】那一行持平(用户裁定)—— 而不是一直拉到末尾的札幌。
            //   做法:跨两行,但下边距留出【最后一条折叠行 + 间隔】的高度。
            //   下方"正在进行的项目"与上方的间隔因此不变(它跟的是行 2 的底,不是日历的底)。
            // ★★ 2026-07-31 用户改口:【日历板块高度 = 待办 + 一个展开的天气 + 两个折叠的天气】,
            //   也就是下沿与【整个天气栈】齐,而不是停在倒数第二行。
            //   既然天气栈总高恒定(WeatherStackHeight),两边留【同样的】下边距就自然对齐。
            // ★ 下边距不能是 0 —— 那样下面的「正在进行的项目」会贴上来,间隔就没了(用户反馈)。
            //   与天气宿主取同一个 PanelGap,两者下沿仍然齐平。
            // ★ 右边距要与【右列到底存不存在】同一个判据 —— 只关待办、留着天气时,
            //   日历的右描边会直接贴到天气卡的左描边上。
            calPanel.Margin = new Thickness(0, 0, (_todoVisible || _weatherVisible) ? 12 : 0, PanelGap);
            calPanel.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetRow(calPanel, 1); Grid.SetColumn(calPanel, 0); Grid.SetRowSpan(calPanel, 2);
            // ★ 只有右列【真的空了】(待办与天气都不显示)才让日历横跨两列;
            //   否则会盖住同在右列的天气块。
            if (!_todoVisible && !_weatherVisible) Grid.SetColumnSpan(calPanel, 2);
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
            // ★★ 固定总高的宿主:不管展开谁,整块高度恒定(用户裁定)。ClipToBounds 让收起的细节被裁掉,
            //   于是"折叠"就是卡片变矮、摘要行留在原处 —— 而不是换掉一个元素。
            // ★ 地点多到 300px 装不下时【可以滚】—— 否则超出的那几张会被 ClipToBounds 裁掉,
            //   看不见也碰不到。三个地点以内根本不会出现滚动条,现状不受影响。
            // ★★ 滚动条【只在真的装不下时才给】(用户反馈:在几个城市之间快速移动鼠标会闪出一条滑条)。
            //   成因:折叠/展开走的是高度动画,动画中途总高会短暂超出 300px 一点点,
            //   Auto 的滚动条就闪一下 —— 那条"滑条"其实是滚动条,不是什么控件。
            //   地点数在容得下的范围内就直接禁掉滚动;超出了才给,那是审计要求的逃生口。
            var weatherFits = _places.Count <= WeatherFitsCount;
            var weatherScroll = new ScrollViewer
            {
                Content = _weatherStack,
                VerticalScrollBarVisibility = weatherFits ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            }.PassThrough();
            var weatherHost = new Border
            {
                Child = weatherScroll,
                Height = WeatherStackHeight,
                Margin = new Thickness(0, 0, 0, PanelGap),   // ★ 与日历同一个下边距 -> 下沿齐平 + 与项目板块留出间隔
                ClipToBounds = true,
                // ★ 铺一层透明底:卡与卡之间那道 10px 的缝原来【不参与命中测试】,
                //   光标停在缝里 220ms 就被当成"离开了天气板块",展开的卡啪地跳回第 0 张。
                //   人明明还在板块上 —— 这正是用户之前报过的那种"乱跳"的同一个形状。
                Background = System.Windows.Media.Brushes.Transparent,
                // ★★ 数据来源署名【已按用户裁定去掉】(2026-08-01,连悬停提示也不要)。
                //   依据:Open-Meteo 免费接口是 CC BY 4.0,而 CC BY 的署名义务是在【对外分发/公开】
                //   时才触发的 —— 这个客户端是自家自用、不对外分发的,纯私人使用不构成 Share。
                //   ★ 一旦这个客户端要分发给家庭以外的人(或公开发布),这一行【必须加回来】。
                //   署名字串故意保留在 I18n 词表里(键:weather · source_credit),到时直接接回来就行。
            };
            // ★ 鼠标真的离开整块才恢复默认 —— 【延迟一拍再确认】:
            //   卡片变高变矮时,光标底下的元素会短暂易主,WPF 会瞬时抛一次 MouseLeave;
            //   立刻响应的话就会"在武汉上面动一下,啪地跳回科隆"(用户反馈的那个跳)。
            weatherHost.MouseLeave += (_, _) => ScheduleWeatherReset(weatherHost);
            weatherHost.MouseEnter += (_, _) => _weatherReset.Stop();
            Grid.SetRow(weatherHost, 2); Grid.SetColumn(weatherHost, 1);
            // ★ 日历隐藏时天气挪到左列 —— 否则它孤悬在右下角,
            //   第二行左侧三分之二是一整片空白(待办那行已经横跨整宽了)。
            if (!_calVisible) Grid.SetColumn(weatherHost, 0);
            _root.Children.Add(weatherHost);
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
        Loaded += (_, _) =>
        {
            RelayoutContinuous(); RelayoutDiscrete(); HookWindowVisibility(); SyncClockTimer();
            Services.Weather.Changed += RefreshWeatherUi;
            RefreshWeatherUi();          // 先把缓存里那份画出来(带它自己的时间戳)
            PullWeather();               // 再去取新的;取不到就维持上面那份
            _weatherTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop(); UnhookWindowVisibility();
            Services.Weather.Changed -= RefreshWeatherUi;
            _weatherTimer.Stop();
        };
        _weatherTimer.Tick += (_, _) => PullWeather();
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
        // ★★ 右列宽度【不能再按地点数算】:天气早已改成竖排,一列到底,
        //   宽度与"有几个城市"没关系了;按张数算会让整页比例随时区变(2 张时右列宽到 1/2)。
        //   取内容宽的三分之一,与原来 3 张时的结果几乎一样,但从此稳定。
        // ★★ 而且【待办隐藏 ≠ 右列可以归零】—— 天气也在这一列。
        //   归零的话天气整块被压成 0 宽、彻底消失,日历再跨列盖上去,
        //   四个板块的显隐组合里这一种直接坏掉。只有两块都不显示时才归零。
        if (_todoVisible || _weatherVisible)
            _todoColumn.Width = new GridLength(Math.Max(240, contentW / 3.0 - WeatherGap));
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

    /// <summary>日历板块标题栏右侧:【+ 新建日程】+ 【立即同步】。</summary>
    FrameworkElement CalendarHeaderActions(CalendarView calView)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Ui.PlusButton(calView.NewEventOnSelected, "在选中的那一天新增日程"));
        // ★ SyncNowButton 在【没配 Apple 账号时返回 null】—— 直接 Add 会抛
        //   ArgumentNullException,而且是在构造期抛:整个客户端都打不开。
        //   (这一条是被【视图构造冒烟】当场拦下的 —— 正是它存在的理由。)
        if (SyncNowButton(calView) is { } sync)
        {
            row.Children.Add(new Border { Width = 6 });
            row.Children.Add(sync);
        }
        return row;
    }

    // ---------------------------------------------------------------- 天气区(可拖拽排序)
    void BuildWeather()
    {
        _weatherStack.Children.Clear();
        var n = _places.Count;
        _cityTime = new TextBlock[n];
        _cityMeta = new TextBlock[n];
        _cityTemp = new TextBlock[n];
        _citySky = new TextBlock[n];
        _cityHiLo = new TextBlock[n];
        _cityStamp = new TextBlock[n];
        _cityIcon = new ContentControl[n];
        _miniIcon = new ContentControl[n];
        _cityCurve = new Grid[n];
        _cityHourly = new UniformGrid[n];
        _cityCards = new Border[n];
        _shifts = new TranslateTransform[n];
        _miniTime = new TextBlock[n];
        _miniSky = new TextBlock[n];
        _miniTemp = new TextBlock[n];
        _miniPart = new TextBlock[n];
        _miniName = new TextBlock[n];
        _miniBar = new FrameworkElement[n];

        for (int i = 0; i < n; i++)
        {
            var idx = i;
            _cityCards[i] = WeatherCard(i);
            // 悬停这张卡 -> 展开它。★ 挂在【整张卡】上而不是只挂摘要行:
            //   卡片长高之后鼠标多半落在卡身上,只挂摘要行的话一移动就失去焦点。
            _cityCards[i].MouseEnter += (_, _) =>
            {
                if (_draggingCity) return;      // ★ 拖拽期间别抢焦点 —— 否则卡片一边被拖一边变高
                _weatherReset.Stop();
                SetWeatherFocus(idx, animate: true);
            };
            _weatherStack.Children.Add(_cityCards[i]);
        }
        SetWeatherFocus(_weatherFocus < n ? _weatherFocus : 0, animate: false);
        UpdateClocks();
        // ★ 刚建好就把读数刷上去 —— 不能只靠 Loaded:
        //   拖拽排序后会重建这一批卡片(那时 Loaded 不会再来),
        //   离屏渲染诊断里 Loaded 也根本不触发 —— 那就变成"图上永远是空态,等于没验"。
        RefreshWeatherUi();
        RelayoutDiscrete();
    }

    /// <summary>
    /// 一张城市卡:上面是【始终可见】的摘要行,下面是细节。
    /// ★ 折叠 = 把卡片高度动画到摘要行的高度,细节被 ClipToBounds 裁掉 ——
    ///   不是换掉一个元素。这样摘要行【始终待在原处】,鼠标不会因为元素易主而乱跳。
    /// </summary>
    Border WeatherCard(int i)
    {
        var body = new StackPanel();
        body.Children.Add(MiniRow(i));
        var detail = CityDetail(i);
        detail.Height = ExpandedCityHeight - CollapsedCityHeight;
        body.Children.Add(detail);

        var card = new Border
        {
            Child = body,
            Height = CollapsedCityHeight,
            // ★ 末尾那张不留下边距 —— 它看得见的底边就是整块的底边,
            //   日历下沿才真的与它齐(用户:"武汉的底部就是天气板块的底部")。
            Margin = new Thickness(0, 0, 0, i == _places.Count - 1 ? 0 : CityGap),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        card.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");

        // 拖拽用的位移(挤开/跟手都靠它,不动真实布局 -> 动画干净、不触发重排)
        var shift = new TranslateTransform();
        card.RenderTransform = shift;
        _shifts[i] = shift;

        // ★ 只有【右下角这块手柄】起手拖动 —— 不是整张卡(用户裁定,与旧版同一条规矩)。
        //   第 0 张 =「当前所在地」固定不动,不给手柄。
        if (i > 0)
        {
            var grip = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M2 8 H10 M2 5 H10 M2 11 H10"),
                StrokeThickness = 1.2,
                Width = 12, Height = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Opacity = 0,                                   // 平时隐身,hover 卡片才浮出来
            };
            grip.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "FgMuted");

            var gripZone = new Grid
            {
                Width = 22, Height = 22,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = System.Windows.Media.Brushes.Transparent,   // 透明但可命中
                Cursor = System.Windows.Input.Cursors.SizeNS,
            };
            gripZone.Children.Add(grip);
            card.MouseEnter += (_, _) => grip.Opacity = 1;
            card.MouseLeave += (_, _) => grip.Opacity = 0;

            var index = i;
            gripZone.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; BeginCityDrag(index, e); };

            // 手柄要浮在卡片内容之上 -> 内容与手柄叠在一个 Grid 里。
            // ★★ 先【断开】再挂:body 此刻已经是 card.Child,直接 stackHost.Children.Add(body)
            //   会抛「元素已有另一个逻辑父级」—— 这个项目里已经撞过好几次了,
            //   而且它在构造期抛,效果就是【整个程序打不开】。
            card.Child = null;
            var stackHost = new Grid();
            stackHost.Children.Add(body);
            stackHost.Children.Add(gripZone);
            card.Child = stackHost;
        }
        return card;
    }

    /// <summary>
    /// 切换展开哪一个。animate=true 时走高度动画(用户要"丝滑切换")。
    /// ★ 三张卡高度之和恒等于 WeatherStackHeight —— 展开一张、其余收起,总高不变。
    /// </summary>
    void SetWeatherFocus(int i, bool animate)
    {
        if (_cityCards.Length == 0) return;
        // ★ 允许 i = -1 表示【谁都不展开】—— 拖拽期先把三张都收起来,
        //   高度一致后"挪到第几位"才是一道简单的整数题。只有越界才归 0。
        if (i >= _cityCards.Length) i = 0;
        if (i < -1) i = 0;
        if (_weatherFocus == i && animate) return;      // 已经是它了,别重复起动画
        _weatherFocus = i;

        for (int k = 0; k < _cityCards.Length; k++)
        {
            var to = k == i ? ExpandedCityHeight : CollapsedCityHeight;
            if (!animate)
            {
                _cityCards[k].BeginAnimation(FrameworkElement.HeightProperty, null);
                _cityCards[k].Height = to;
                continue;
            }
            // ★ 缓入缓出(用户裁定):只有 EaseOut 的话起手是满速的,看着很呆。
            //   EaseInOut 两头都慢 —— 像一扇门被推开而不是被弹开。时长也稍拉长一点。
            var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            };
            _cityCards[k].BeginAnimation(FrameworkElement.HeightProperty, anim);
        }
    }

    // ---------------------------------------------------------------- 城市排序:右下角手柄纵向拖拽
    // ★★ 拖起来的那一刻把三张【全部收起】—— 高度一致之后,"挪到第几位"才是一道简单的整数题,
    //   挤开动画也才干净。高矮不一时拖拽的落点算法会变得又难写又难猜。
    // ★ 只动 TranslateTransform,不动真实布局 —— 动画期间不触发重排,松手才提交顺序。

    int? _dragIndex;
    int _dragTarget;
    double _dragFromY;
    double _dragHomeTop;        // 全收起之后被拖那张的布局位置(位移都相对它算)
    bool _draggingCity;

    double CityRowStride => CollapsedCityHeight + CityGap;

    void BeginCityDrag(int index, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (index <= 0 || index >= _cityCards.Length) return;   // 首格锁定
        _dragIndex = index;
        _dragTarget = index;
        _draggingCity = true;
        _weatherReset.Stop();

        // ★★ 这里是"拖拽不跟鼠标"的根因,记一笔:
        //   原来收起是走【260ms 高度动画】的。动画期间每一帧卡片的【布局位置】都在变,
        //   而位移 shift.Y = dy 是相对布局位置算的 —— 基准一直在动,画面自然跟不上手;
        //   等动画停下来,基准又已经整体上移了一截,于是卡片永久地和光标错开。
        //   改成【立即收起 + 当场把布局跑完】,基准从起手那一刻就是死的,dy 就是真位移。
        SetWeatherFocus(-1, animate: false);
        _weatherStack.UpdateLayout();

        // ★★ 光收起还不够 —— 手柄在【展开卡的右下角】,而卡一收起只剩 34px,
        //   抓点在卡内的位置凭空上移了一大截(展开高 - 折叠高,约 180px)。
        //   若位移仍从 0 起算,卡片就会停在光标上方一大截 —— 用户报的"不跟手"
        //   在最常见的那条路径(拖展开卡下面那张)上会原样保留。
        //   做法:直接按【光标的绝对位置】反推卡片该在哪 —— 让手柄(卡片底部)贴着光标。
        //   全程绝对口径,不做增量累加,所以永远不会越拖越偏。
        _dragFromY = e.GetPosition(_weatherStack).Y;
        _dragHomeTop = index * CityRowStride;                   // 全收起之后第 index 张的位置

        Panel.SetZIndex(_cityCards[index], 10);                 // 被拖的那张浮在最上
        _cityCards[index].Opacity = 0.94;

        _weatherStack.CaptureMouse();
        _weatherStack.MouseMove += OnCityDragMove;
        _weatherStack.MouseLeftButtonUp += OnCityDragEnd;
        _weatherStack.LostMouseCapture += OnCityDragLost;
    }

    void OnCityDragMove(object? sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragIndex is not int from) return;
        // ★ 绝对口径:卡片底部(手柄所在处)贴着光标 -> 卡顶 = 光标 - 折叠高 + 手柄半高。
        //   位移 = 该在的位置 - 全收起后的布局位置。
        var cursorY = e.GetPosition(_weatherStack).Y;
        var wantTop = cursorY - CollapsedCityHeight + 11;       // 11 = 手柄区 22px 的一半
        var dy = wantTop - _dragHomeTop;

        // ★ 夹在板块内(用户反馈"会拖到板块以外去,超上或者超下"):
        //   所有卡此刻都是折叠高,所以第 k 位的位置就是 k * 行距 —— 能挪到的范围
        //   就是 [第 1 位, 最后一位] 换算成的位移。首格锁定,所以上限是第 1 位而不是第 0 位。
        var minDy = (1 - from) * CityRowStride;
        var maxDy = (_cityCards.Length - 1 - from) * CityRowStride;
        dy = Math.Clamp(dy, minDy, maxDy);

        _shifts[from].Y = dy;                                   // 被拖的跟手(不用动画,要贴着鼠标)

        // 落点 = 位移换算成"挪过几行"。首格锁定,所以下限是 1。
        var target = Math.Clamp(from + (int)Math.Round(dy / CityRowStride), 1, _cityCards.Length - 1);
        if (target == _dragTarget) return;
        _dragTarget = target;

        // 其余卡片让位:被跨过的往回挪一格,其它归位 —— 都走动画(用户要"挤开重新排列"的动画)
        for (int k = 1; k < _cityCards.Length; k++)
        {
            if (k == from) continue;
            double to = 0;
            if (from < target && k > from && k <= target) to = -CityRowStride;
            else if (from > target && k >= target && k < from) to = CityRowStride;
            AnimateShift(k, to);
        }
    }

    void OnCityDragEnd(object? sender, System.Windows.Input.MouseButtonEventArgs e) => FinishCityDrag(commit: true);
    void OnCityDragLost(object? sender, System.Windows.Input.MouseEventArgs e) => FinishCityDrag(commit: false);

    void FinishCityDrag(bool commit)
    {
        if (_dragIndex is not int from) return;
        _weatherStack.MouseMove -= OnCityDragMove;
        _weatherStack.MouseLeftButtonUp -= OnCityDragEnd;
        _weatherStack.LostMouseCapture -= OnCityDragLost;
        _weatherStack.ReleaseMouseCapture();

        Panel.SetZIndex(_cityCards[from], 0);
        _cityCards[from].Opacity = 1;
        _dragIndex = null;
        _draggingCity = false;

        var to = _dragTarget;
        // 位移清零 —— 真实顺序由下面的 MovePlace 重建,位移只是拖拽期的假象
        for (int k = 0; k < _shifts.Length; k++)
        {
            _shifts[k].BeginAnimation(TranslateTransform.YProperty, null);
            _shifts[k].Y = 0;
        }

        if (commit && to != from) MovePlace(from, to);          // 落盘顺序 + 重建
        else SetWeatherFocus(0, animate: true);
    }

    void AnimateShift(int index, double toY)
    {
        if (index < 0 || index >= _shifts.Length) return;
        var anim = new DoubleAnimation(toY, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        _shifts[index].BeginAnimation(TranslateTransform.YProperty, anim);
    }

    /// <summary>鼠标离开整块之后延迟恢复默认 —— 见 weatherHost.MouseLeave 处的说明。</summary>
    readonly DispatcherTimer _weatherReset = new() { Interval = TimeSpan.FromMilliseconds(220) };
    void ScheduleWeatherReset(FrameworkElement host)
    {
        _weatherReset.Stop();
        void Tick(object? _, EventArgs __)
        {
            _weatherReset.Tick -= Tick;
            _weatherReset.Stop();
            // ★ 到点再【实地确认】鼠标是不是真的还在外面 —— 只信一次瞬时事件会误判
            if (!host.IsMouseOver) SetWeatherFocus(0, animate: true);
        }
        _weatherReset.Tick += Tick;
        _weatherReset.Start();
    }

    /// <summary>
    /// 摘要行:城市 · 当前 | 气候 | 温度滑条(左=今日最低,右=最高,滑块=此刻) ······ 时间(最右)。
    /// ★ 折叠时露出的就是它;展开时它是卡片顶行 —— 所以"时间在右上角"与"折叠时在最右"是同一件事。
    /// ★★ 天气【尚未接入】:低/高/此刻都没有真实数字,所以滑条画成【无数据态】(空槽、无滑块)、
    ///   温度与气候显示「—」。绝不给一个位置随便的滑块 —— 那会让人以为读数是真的。
    /// </summary>
    FrameworkElement MiniRow(int i)
    {
        var place = _places[i];
        // ★ 城市名后面【不再加「· 当前」】(用户裁定 2026-07-31):
        //   右侧那一列已经写着「本地」了,同一件事说两遍。
        _miniName[i] = new TextBlock
        {
            Text = place.City,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
        };
        var name = _miniName[i];
        name.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        name.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        // 当前气候(晴 / 多云 / 阴 …)—— 未接入时是「—」
        _miniSky[i] = new TextBlock { Text = "—", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        _miniSky[i].SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        _miniSky[i].SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        // ★ 折叠行也要看得到气温(用户裁定)—— 没取到就是"—°",不编一个
        _miniTemp[i] = new TextBlock
        {
            Text = "—°",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            FontWeight = FontWeights.SemiBold,
        };
        _miniTemp[i].SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        _miniTemp[i].SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        // 时段词(早上/上午/中午/下午/晚上/夜晚)—— 用户裁定:放在时间左边,
        // 比原来的"昼/夜"具体得多。与问候语共用同一套划分(见 Greetings.PartOfDay)。
        _miniPart[i] = new TextBlock { Text = "", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        _miniPart[i].SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _miniPart[i].SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        // 时间 —— 放在最右(展开时它就是卡片的右上角)
        _miniTime[i] = new TextBlock { Text = "—", VerticalAlignment = VerticalAlignment.Center };
        _miniTime[i].SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _miniTime[i].SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // ★ 图标随实况变(晴/多云/雨/雪…);还没取到就用通用那朵云 —— 不拿太阳冒充
        _miniIcon[i] = new ContentControl { Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        _miniIcon[i].Content = Icons.Make(IconName.Weather, 13, "FgSecondary");
        left.Children.Add(_miniIcon[i]);
        left.Children.Add(new Border { Width = 6 });
        left.Children.Add(name);
        left.Children.Add(_miniTemp[i]);
        left.Children.Add(_miniSky[i]);

        // ★ 温度滑条【已移除】(用户裁定 2026-07-31:"天气不应该有滑块")。
        //   天气未接入时它本就是一条空槽,接入后也不是一个可拖的控件 ——
        //   长得像滑块却不能拖,本身就是个误导。
        var row = new DockPanel { LastChildFill = false, Height = CollapsedCityHeight, Margin = new Thickness(12, 0, 12, 0) };
        DockPanel.SetDock(left, Dock.Left); row.Children.Add(left);
        DockPanel.SetDock(_miniTime[i], Dock.Right); row.Children.Add(_miniTime[i]);
        DockPanel.SetDock(_miniPart[i], Dock.Right); row.Children.Add(_miniPart[i]);
        return row;
    }

    /// <summary>
    /// 温度滑条:左端 = 今日最低,右端 = 最高,滑块 = 此刻。
    /// ★ 三个值任缺其一 -> 画【无数据态】:空槽 + 两端显示「—」+ 不画滑块。
    ///   给一个位置是猜的滑块,比不画更糟 —— 那是在伪造一个读数。
    /// </summary>
    FrameworkElement TempBar(int i, double? low, double? high, double? now)
    {
        var has = low is { } lo && high is { } hi && now is { } nw && hi > lo;

        var loText = new TextBlock { Text = low is { } l ? $"{l:0}°" : "—", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
        loText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        loText.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var hiText = new TextBlock { Text = high is { } h2 ? $"{h2:0}°" : "—", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 10, 0) };
        hiText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        hiText.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var track = new Border { Height = 4, VerticalAlignment = VerticalAlignment.Center, MinWidth = 40 };
        track.SetResourceReference(Border.BackgroundProperty, "BgSunken");
        track.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");

        var lane = new Grid();
        lane.Children.Add(track);

        if (has)
        {
            var frac = Math.Clamp((now!.Value - low!.Value) / (high!.Value - low.Value), 0, 1);
            var knob = new System.Windows.Shapes.Ellipse
            {
                Width = 9, Height = 9,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            knob.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
            // 按比例定位:等布局出来后再摆(宽度这时还不知道)
            lane.SizeChanged += (_, _) =>
                knob.Margin = new Thickness(Math.Max(0, (lane.ActualWidth - 9) * frac), 0, 0, 0);
            lane.Children.Add(knob);
        }

        var bar = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(loText, Dock.Left); bar.Children.Add(loText);
        DockPanel.SetDock(hiText, Dock.Right); bar.Children.Add(hiText);
        bar.Children.Add(lane);
        bar.ToolTip = has ? null : "天气尚未接入 —— 最低/此刻/最高都还没有真实数据";
        return bar;
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
    /// <summary>
    /// 一张城市卡的【细节部分】(温度/状态/曲线/逐小时)。
    /// ★ 不再自带 Ui.Panel 外壳与标题 —— 外壳归 WeatherCard,城市名在摘要行里。
    /// 折叠时它被 ClipToBounds 裁掉,而不是被换掉。
    /// </summary>
    FrameworkElement CityDetail(int i)
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

        // ★ 大时间【不进可视树】:摘要行已经显示时间了。
        //   保留字段是因为 UpdateClocks 仍写它(写一个不在树上的控件无害),
        //   这样时钟逻辑不用为版面变动而改。
        var timeCol = Ui.Stack(_cityMeta[i]);
        timeCol.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(timeCol, Dock.Right);

        // ★★ 无数据态【不要】写成 30px 的「—°」:那么大的破折号加一个小圈,
        //   在界面上看着不像"没有读数",而像字体渲染坏了(实测截图里就是这个效果)。
        //   接上之后这里才变成 30px 的「18°」;现在先用小一号的一句白话占着位子。
        //   卡片高度是固定的,所以将来换成大号数字也不会顶动版面。
        var temp = new TextBlock { Text = "暂无读数", FontSize = 19, FontWeight = FontWeights.Light, VerticalAlignment = VerticalAlignment.Center };
        temp.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _cityTemp[i] = temp;

        _cityIcon[i] = new ContentControl { Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Focusable = false };

        var topRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 2) };
        topRow.Children.Add(timeCol);
        DockPanel.SetDock(_cityIcon[i], Dock.Left); topRow.Children.Add(_cityIcon[i]);
        topRow.Children.Add(temp);

        var st = new TextBlock { Text = "还没取到天气", TextTrimming = TextTrimming.CharacterEllipsis };
        st.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        st.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        _citySky[i] = st;
        var hl = new TextBlock { Text = "最高 —  最低 —  降水 —", TextTrimming = TextTrimming.CharacterEllipsis };
        hl.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        hl.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        _cityHiLo[i] = hl;

        // ★★ 这一行是【诚实口径】的落点:读数是什么时候取的、是不是已经过期。
        //   设计 §8 第 6 条:断网 -> 缓存 + updated_at + stale,【无假实时】。
        //   平时它写来源署名(Open-Meteo 要求署名),过期时改写"显示上次 HH:mm"。
        // 初始为空并收起 —— 它只在【读数过期】时出现(见 RefreshWeatherUi)
        _cityStamp[i] = new TextBlock { Text = "", TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed };
        _cityStamp[i].SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _cityStamp[i].SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

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
        _cityCurve[i] = curve;
        // ★ 尺寸变了重画 —— 只在这里挂一次(每座城各自一份,互不影响)
        var curveCity = place.City;
        curve.SizeChanged += (_, _) => DrawCurve(curve, Services.Weather.For(curveCity));

        // ★ 底部留白:来源那一行收起之后,逐小时这排就成了卡里最下面的东西,
        //   只靠 inner 那 10px 底边距显得贴底(用户反馈"太贴边了")。
        _cityHourly[i] = new UniformGrid { Rows = 1, Margin = new Thickness(0, 6, 0, 8) };
        SetHourly(_cityHourly[i], 6);

        var inner = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(topRow, Dock.Top); inner.Children.Add(topRow);
        DockPanel.SetDock(st, Dock.Top); inner.Children.Add(st);
        DockPanel.SetDock(hl, Dock.Top); inner.Children.Add(hl);
        DockPanel.SetDock(_cityStamp[i], Dock.Bottom); inner.Children.Add(_cityStamp[i]);
        DockPanel.SetDock(_cityHourly[i], Dock.Bottom); inner.Children.Add(_cityHourly[i]);
        inner.Children.Add(curve);

        inner.Margin = new Thickness(12, 0, 12, 10);
        return inner;
    }

    // ★ 拖拽换位整段【已删】(2026-07-31 改成固定总高 + 高度动画之后):
    //   卡片不再有拖拽把手,那套按 dx 算位移的机器已无人调用 ——
    //   留着只会让下一个人以为它还在工作。要改顺序去【设置 › 地点】。



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
            var ic = new ContentControl { Width = 13, Height = 13, HorizontalAlignment = HorizontalAlignment.Center, Focusable = false };
            var tp = new TextBlock { Text = "—°", HorizontalAlignment = HorizontalAlignment.Center };
            tp.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            tp.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            grid.Children.Add(Ui.Stack(hr, ic, tp));
        }
    }

    // ---------------------------------------------------------------- 天气读数
    readonly DispatcherTimer _weatherTimer = new() { Interval = Services.Weather.RefreshEvery };

    /// <summary>
    /// 把缓存里的读数刷到界面上。
    /// ★★ 诚实口径(设计 §8 第 6 条):
    ///   · 从来没取到过 -> "暂无读数",一个数字也不编;
    ///   · 取到过但已过期 -> 照旧显示,但底下那行【改写成"显示上次 HH:mm"】,绝不当成实时;
    ///   · 认不出的天气代码 -> 如实写"未知天气(代码 N)",不硬塞成"晴"。
    /// </summary>
    void RefreshWeatherUi()
    {
        if (!_weatherVisible || _cityTemp.Length != _places.Count) return;
        for (int i = 0; i < _places.Count; i++)
        {
            var w = Services.Weather.For(_places[i].City);
            var known = Services.Places.CoordOf(_places[i]) is not null;

            if (_citySky[i] is { } sky)
                sky.Text = w?.WeatherCode is not null ? (Services.Weather.Describe(w.WeatherCode) ?? "—")
                         : known ? "还没取到天气" : "这个城市还没有坐标 —— 取不了天气";
            if (_miniTemp[i] is { } mtp) mtp.Text = w?.TempC is not null ? $"{w.TempC:0}°" : "—°";
            if (_miniSky[i] is { } msky)
                msky.Text = w?.WeatherCode is not null ? (Services.Weather.Describe(w.WeatherCode) ?? "—") : "—";

            if (_cityTemp[i] is { } tp)
            {
                var hasTemp = w?.TempC is not null;
                tp.Text = hasTemp ? $"{w!.TempC:0}°" : "暂无读数";
                tp.FontSize = hasTemp ? 30 : 19;
            }
            if (_cityHiLo[i] is { } hl)
            {
                // ★ 当前没下雨就接一句【几天后有雨】(用户裁定)。
                //   预报窗口里真的没有就明说"未来 N 天无雨" —— 不含糊其辞。
                var rain = Rain(w?.PrecipMm);
                var outlook = (w?.PrecipMm ?? 0) < Services.Weather.RainThresholdMm
                    ? Services.Weather.RainOutlook(w) : null;
                hl.Text = $"最高 {Num(w?.HighC)}  最低 {Num(w?.LowC)}  降水 {rain}"
                        + (outlook is null ? "" : " · " + outlook);
            }

            if (_cityStamp[i] is { } stamp)
            {
                // ★★ 过期就把"上次是什么时候"摆到台面上(诚实口径,不能省)。
                //   没过期时这一行【收起来】—— 数据来源改挂在悬停提示里。
                //   ★ 来源不能直接删掉:Open-Meteo 的免费接口是 CC BY 4.0,署名是许可要求。
                var stale = w?.UpdatedAt is not null && w.IsStale;
                stamp.Text = stale ? $"暂时取不到 · 显示上次 {w!.UpdatedAt:HH:mm}" : "";
                stamp.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
            }
            // 图标随实况;认不出的代码【不画图标】(与 Describe 同一口径,不拿太阳冒充)
            var night = IsNightAt(_places[i]);
            var wx = Icons.ForWeather(w?.WeatherCode, night);
            if (_cityIcon[i] is { } bigIcon)
                bigIcon.Content = wx is { } n1 ? Icons.Make(n1, 26, "FgSecondary") : null;
            if (_miniIcon[i] is { } smIcon)
                smIcon.Content = Icons.Make(wx ?? IconName.Weather, 13, "FgSecondary");

            if (i < _cityHourly.Length && _cityHourly[i] is { } grid) FillHourly(grid, w, _places[i]);
            if (i < _cityCurve.Length && _cityCurve[i] is { } cv) DrawCurve(cv, w);
        }
    }

    /// <summary>
    /// 今日气温曲线。★ 点【不够】就不画线,并如实写"数据不足" ——
    ///   两三个点连出来的折线看着像模像样,却什么也说明不了。
    /// ★ 纵向自适应到这一段的真实最高/最低,并把两端标出来 —— 否则一条没有刻度的线读不出量级。
    /// </summary>
    static void DrawCurve(Grid host, Services.WeatherNow? w)
    {
        // 只留那条虚线基线与"待接入"文字,其余(上一次画的线)清掉
        for (int k = host.Children.Count - 1; k >= 2; k--) host.Children.RemoveAt(k);
        var hint = host.Children.Count > 1 ? host.Children[1] as TextBlock : null;

        var pts = w?.Hours?.Where(x => x.TempC is not null && x.At >= DateTime.Now.AddHours(-1))
                          .Take(24).ToList();
        if (pts is null || pts.Count < 4)
        {
            if (hint is not null) { hint.Text = "今日气温曲线数据不足"; hint.Visibility = Visibility.Visible; }
            return;
        }
        if (hint is not null) hint.Visibility = Visibility.Collapsed;

        var lo = pts.Min(x => x.TempC!.Value);
        var hi = pts.Max(x => x.TempC!.Value);
        var span = Math.Max(0.5, hi - lo);            // 全平的一段也不能除以 0

        var poly = new System.Windows.Shapes.Polyline
        {
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
            Stretch = Stretch.None,
        };
        poly.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent");

        void Rebuild(object? _, SizeChangedEventArgs? __)
        {
            var wpx = host.ActualWidth; var hpx = host.ActualHeight;
            if (wpx <= 0 || hpx <= 0) return;
            // ★ 上下留足夹道:原来只留 3px,峰值就贴在格子上沿(看着就是"超了"),
        //   而且正好与顶部那个最高温标注叠在一起。
            const double padY = 9;
            // ★ 左侧留出标注的位置 —— 线与字不再挤在同一块地方。
            const double padL = 28;
            var usableW = Math.Max(1, wpx - padL);
            var usableH = Math.Max(1, hpx - 2 * padY);
            var pc = new PointCollection();
            for (int k = 0; k < pts.Count; k++)
            {
                var x = padL + (pts.Count == 1 ? usableW / 2 : k * usableW / (pts.Count - 1));
                var y = hpx - padY - (pts[k].TempC!.Value - lo) / span * usableH;
                pc.Add(new Point(x, y));
            }
            poly.Points = pc;
        }
        // ★★ 这里【不】挂 SizeChanged。
        //   DrawCurve 每次刷新都会被调一次,在里面挂事件就是"挂一次多一个";
        //   而方法组每次都是新的委托实例, -= 根本摸不到上一个——
        //   只加不减(WPF-PITFALLS 第 10 条同一形状)。尺寸变了重画的句柄在【构造时】挂一次(见 CityDetail)。
        Rebuild(null, null);
        host.Children.Add(poly);

        // 两端的量级标注 —— 没有刻度的曲线读不出高低
        var loT = new TextBlock { Text = $"{lo:0}°", VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
        var hiT = new TextBlock { Text = $"{hi:0}°", VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
        foreach (var t in new[] { loT, hiT })
        {
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            host.Children.Add(t);
        }
    }

    /// <summary>
    /// 那座城【此刻】是不是夜里 —— 日出日落本地算(SunClock),不出网。
    /// ★ 要用【那座城自己的当地时间】比,不是本机时间:
    ///   本地是深夜时札幌可能已经早上了 —— 那一格该画太阳而不是月亮。
    /// ★ 坐标认不出就当白天(宁可不分,不瞎猜)。
    /// </summary>
    static bool IsNightAt(Services.Place p, DateTime? atLocal = null)
    {
        if (Services.Places.CoordOf(p) is not { } c) return false;
        try
        {
            var z = TimeZoneInfo.FindSystemTimeZoneById(p.TimeZoneId);
            var now = atLocal ?? TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, z).DateTime;
            var sun = Services.SunClock.ForDay(c.Lat, c.Lon, now.Date, z.GetUtcOffset(now).TotalHours);
            var h = now.TimeOfDay.TotalHours;
            return h < sun.Sunrise || h >= sun.Sunset;
        }
        catch { return false; }
    }

    static string Num(double? v) => v is null ? "—" : $"{v:0}°";
    // ★ 小数点强制用【点】—— 系统区域是德国时 CurrentCulture 会给出 "0,2mm",
    //   在中文界面里看着像错字(渲染诊断的图上当场看到的)。
    static string Rain(double? v) => v is null ? "—" : v.Value <= 0.05 ? "无"
        : v.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "mm";

    /// <summary>把逐小时那一排填上真实读数;没有就保持"—"。</summary>
    static void FillHourly(UniformGrid grid, Services.WeatherNow? w, Services.Place place)
    {
        var step = Layout.HourlyStepHours(grid.Columns);
        var now = DateTime.Now;
        for (int k = 0; k < grid.Children.Count; k++)
        {
            if (grid.Children[k] is not Panel cell || cell.Children.Count < 3) continue;
            var want = now.AddHours(k * step);
            var hit = w?.Hours?.FirstOrDefault(x => Math.Abs((x.At - want).TotalMinutes) <= 30);
            if (cell.Children[0] is TextBlock hr) hr.Text = want.ToString("HH");
            // ★ 中间那格换成真的天气图形;认不出/没数据就留一条短横,不拿太阳冒充
            // ★ 逐小时也各算各的昼夜 —— 晚上 21 点那格的"晴"同样该是月亮
            if (cell.Children[1] is ContentControl ic)
                ic.Content = Icons.ForWeather(hit?.Code, IsNightAt(place, want)) is { } nm
                    ? Icons.Make(nm, 13, "FgSecondary")
                    : null;
            if (cell.Children[2] is TextBlock tp)
                tp.Text = hit?.TempC is not null ? $"{hit.TempC:0}°" : "—°";
        }
    }

    /// <summary>
    /// 拉一遍所有地点。★ 只在【有网】时拉;认不出坐标的直接跳过(不拿别处的坐标顶替)。
    /// ★ 失败不动缓存 —— 界面会继续显示上一次那份并标出它的时间。
    /// </summary>
    async void PullWeather()
    {
        if (!_weatherVisible) return;
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return;
        foreach (var p in _places.ToList())
        {
            if (Services.Places.CoordOf(p) is not { } c) continue;
            await Services.Weather.PullAsync(p.City, c.Lat, c.Lon);
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
            // ★ 刷新图标的几何已保证"包围盒 = 以圆心为心的正方形"(见 Icons 里的说明),
            //   所以绕中心转就是绕圆心转。
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
        // ★ 主页把所有工作空间【一起列出来】—— 这个面板是跨空间汇总的,
        //   只画主标签的话,多空间项目看上去就像"只属于其中一个"(用户裁定 2026-08-01)。
        var icon = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var k in p.Spaces)
        {
            var one = Icons.Make(ProjectUi.SpaceIcon(k), 18, "FgSecondary");
            one.Margin = new Thickness(0, 0, 6, 0);
            one.ToolTip = ProjectUi.SpaceName(k);
            icon.Children.Add(one);
        }

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
            // 挂了多个空间时进【主标签】那个 —— 总得挑一个,挑它最初所在的那个最不意外
            (Application.Current.MainWindow as MainWindow)?.NavigateToProject(p.PrimarySpace, p.ProjectId);
        };
        tile.ToolTip = $"{p.Title}\n{p.Subtitle}\n最近打开:{p.LastOpened:M月d日 HH:mm}"
                     + (p.Spaces.Count > 1 ? $"\n同时挂在:{ProjectUi.SpacesText(p)}(同一个项目)" : "");
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
                // ★ 时段词只留【摘要行右上角】那一份(用户裁定:"右侧有两个晚上")——
                //   这里只说时差(本地 / +7h),不再把早上晚上重复一遍。
                _cityMeta[i].Text = diffText;
                if (i < _miniPart.Length && _miniPart[i] is { } mp) mp.Text = Greetings.PartOfDay(t.Hour);
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
