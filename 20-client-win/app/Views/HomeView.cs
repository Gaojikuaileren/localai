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

    readonly TextBlock _greeting = new() { FontWeight = FontWeights.SemiBold, FontSize = 22 };
    TextBlock[] _cityTime = Array.Empty<TextBlock>();
    TextBlock[] _cityMeta = Array.Empty<TextBlock>();
    UniformGrid[] _cityHourly = Array.Empty<UniformGrid>();
    readonly UniformGrid _weatherGrid = new() { Rows = 1 };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    readonly Grid _root = new();
    readonly ColumnDefinition _todoColumn = new();
    readonly UniformGrid _tiles = new();
    readonly StackPanel _todoList = new();
    readonly Border _todoPanel;
    // 完成后停留 3 秒再归档:宽限期内每 400ms 刷一次,到点把已完成项从主板块刷走
    readonly DispatcherTimer _todoGrace = new() { Interval = TimeSpan.FromMilliseconds(400) };
    TextBlock? _archiveLabel;
    readonly ScrollViewer _pageScroll = new();

    App TheApp => (App)Application.Current;

    public HomeView()
    {
        _greeting.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        _root.Margin = new Thickness(24, 14, 24, 18);
        // 用户裁定:日历占【三分之二】,待办占三分之一
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });   // 日历 2/3
        _todoColumn.Width = new GridLength(1, GridUnitType.Star);                                             // 待办 1/3
        _root.ColumnDefinitions.Add(_todoColumn);
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                   // 问候
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                   // 日历 | 待办(固定高)
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                   // 天气(固定高)
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 130 }); // 项目(占满剩余)

        // ① 问候
        _greeting.Margin = new Thickness(2, 0, 0, 10);
        Grid.SetRow(_greeting, 0); Grid.SetColumnSpan(_greeting, 2);
        _root.Children.Add(_greeting);

        // ② 日历(周横排,固定高)| 待办(窄)
        // 与顶栏日历浮窗【同一个组件、同一套交互】(MiniCal 逻辑),这里用周排布、固定高
        // 周/月排布【共用同一尺寸】(用户裁定),所以高度固定、切换时不变
        var calView = new CalendarView(CalendarView.Mode.Week) { Height = CalendarView.PanelHeight };
        var calPanel = Ui.Panel("日历", calView, IconName.Calendar, new Thickness(0, 0, 12, 12));
        Grid.SetRow(calPanel, 1); Grid.SetColumn(calPanel, 0);
        _root.Children.Add(calPanel);

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
            headerAction: Ui.PlusButton(() => OpenTodoEditor(null), "新增待办事项"));
        BuildTodos();
        TheApp.Todos.Changed += BuildTodos;
        _todoGrace.Tick += (_, _) => BuildTodos();   // 宽限期内轮询,到点把已完成项刷走
        Unloaded += (_, _) => { TheApp.Todos.Changed -= BuildTodos; _todoGrace.Stop(); };
        // 与日历等高 —— 两块并排,高度锁死才不会一高一矮
        // 与日历面板等高:日历面板 = 日历本体 + Ui.Panel 的标题行与内边距(约 62),
        // 之前按 +46 算,导致两块并排时高度差了十几像素。
        _todoPanel.Height = CalendarView.PanelHeight + 62;
        Grid.SetRow(_todoPanel, 1); Grid.SetColumn(_todoPanel, 1);
        _root.Children.Add(_todoPanel);

        // ③ 天气:固定高度,只占所需。地点表可拖拽排序(首格锁定)
        _weatherGrid.Height = WeatherHeight;
        _weatherGrid.Margin = new Thickness(0, 0, -WeatherGap, 12);   // 吸收末格多出的右间距
        _places = Places.Load(TheApp.Settings);
        BuildWeather();
        Grid.SetRow(_weatherGrid, 2); Grid.SetColumnSpan(_weatherGrid, 2);
        _root.Children.Add(_weatherGrid);

        // ④ 项目方块:占满剩余,平分整宽,可滚动
        // ★ 顶端对齐 —— 否则 UniformGrid 会把仅有的一行拉伸到整个可用高度,
        //   方块看起来"纵向居中"。用户要的是【从上到下、从左到右】依次排布。
        _tiles.VerticalAlignment = VerticalAlignment.Top;
        _tiles.Columns = 4;
        var projects = Ui.Panel(Strings.Get("project.resume"),
            new ScrollViewer
            {
                Content = _tiles,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            }.PassThrough(),
            IconName.Tasks, new Thickness(0));
        Grid.SetRow(projects, 3); Grid.SetColumnSpan(projects, 2);
        _root.Children.Add(projects);

        // 兜底逃生口:极端尺寸下允许整页纵向滚动 —— 宁可滚动也不裁切内容
        _pageScroll.Content = _root;
        _pageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _pageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Content = _pageScroll;

        BuildTiles();
        TheApp.Projects.Changed += BuildTiles;
        Unloaded += (_, _) => TheApp.Projects.Changed -= BuildTiles;

        SizeChanged += (_, _) => ScheduleRelayout();
        _resizeThrottle.Tick += (_, _) => { _resizeThrottle.Stop(); RelayoutDiscrete(); };

        UpdateClocks();
        _timer.Tick += (_, _) => UpdateClocks();
        Loaded += (_, _) => { _timer.Start(); RelayoutContinuous(); RelayoutDiscrete(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    // ---------------------------------------------------------------- 随尺寸重排
    readonly DispatcherTimer _resizeThrottle = new() { Interval = TimeSpan.FromMilliseconds(60) };
    int _cols, _slots;

    void ScheduleRelayout()
    {
        _resizeThrottle.Stop();
        _resizeThrottle.Start();   // 拖动停下 60ms 后才做需要重建子元素的活
        RelayoutContinuous();      // 连续量每帧跟手
    }

    void RelayoutContinuous()
    {
        // 日历 2/3、待办 1/3 由 Grid 星号列直接分配,随窗口天然连续,无需手动插值。
    }

    void RelayoutDiscrete()
    {
        var w = ActualWidth;
        if (w <= 0) return;

        // 列数不超过项目数 —— 否则多出的空列就是右侧一块空白
        var cols = Layout.ProjectColumns(w - 8, _cols, TheApp.Projects.Items.Count);
        if (cols != _cols) { _cols = cols; _tiles.Columns = cols; }

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
        _weatherGrid.Children.Clear();
        _shifts.Clear();
        _hosts.Clear();
        _weatherGrid.Columns = _places.Count;
        _cityTime = new TextBlock[_places.Count];
        _cityMeta = new TextBlock[_places.Count];
        _cityHourly = new UniformGrid[_places.Count];
        for (int i = 0; i < _places.Count; i++) _weatherGrid.Children.Add(CityCard(i));
        UpdateClocks();
        RelayoutDiscrete();
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

        var index = i;
        gripZone.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; BeginDrag(index, e); };
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
        _dragOrigin = e.GetPosition(_weatherGrid);
        if (_hosts.TryGetValue(index, out var host))
        {
            Panel.SetZIndex(host, 10);          // 拖起来的卡浮在其它卡之上
            host.Opacity = 0.94;
        }
        _weatherGrid.CaptureMouse();
        _weatherGrid.MouseMove += OnDragMove;
        _weatherGrid.MouseLeftButtonUp += OnDragEnd;
        _weatherGrid.LostMouseCapture += OnDragLost;
    }

    void OnDragMove(object? sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragIndex is not int from) return;
        var dx = e.GetPosition(_weatherGrid).X - _dragOrigin.X;

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

        _weatherGrid.MouseMove -= OnDragMove;
        _weatherGrid.MouseLeftButtonUp -= OnDragEnd;
        _weatherGrid.LostMouseCapture -= OnDragLost;
        if (_weatherGrid.IsMouseCaptured) _weatherGrid.ReleaseMouseCapture();
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
        return _weatherGrid.ActualWidth > 0 ? _weatherGrid.ActualWidth / n : 220;
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
    void OpenTodoEditor(TodoItem? existing)
    {
        var body = TodoEditor.Build(existing);
        (Application.Current.MainWindow as MainWindow)?.OpenSideDrawer(
            existing is null ? "新建待办事项" : "编辑待办事项", body, IconName.Member);
    }

    void BuildTodos()
    {
        _todoList.Children.Clear();
        var items = TheApp.Todos.Active().ToList();
        if (items.Count == 0)
        {
            _todoList.Children.Add(Ui.Body("没有待办事项了。", muted: true));
            _todoList.Children.Add(Ui.Caption("点右上角 + 新建;或「提醒我…」建个人待办、「提醒我们…」建家庭事务。"));
        }
        else
        {
            foreach (var t in items)
                _todoList.Children.Add(TodoList.Row(t, () => TheApp.Todos.Toggle(t.Id), () => OpenTodoEditor(t)));
        }

        // 已完成计数刷新
        if (_archiveLabel is not null) _archiveLabel.Text = $"已完成 ({TheApp.Todos.CompletedCount})";

        // 还有处于 3 秒宽限期的项 -> 保持轮询,到点自动把它刷走;否则停表
        if (TheApp.Todos.HasGrace()) { if (!_todoGrace.IsEnabled) _todoGrace.Start(); }
        else _todoGrace.Stop();
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
        var scopeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        scopeRow.Children.Add(dot); scopeRow.Children.Add(scopeLabel);

        var body = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(icon, Dock.Top); body.Children.Add(icon);
        DockPanel.SetDock(scopeRow, Dock.Bottom); body.Children.Add(scopeRow);
        DockPanel.SetDock(title, Dock.Top); body.Children.Add(title);
        DockPanel.SetDock(sub, Dock.Top); body.Children.Add(sub);

        var tile = new Border
        {
            Child = body,
            Height = 126,
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 12, 12),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        tile.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        tile.SetResourceReference(Border.BorderBrushProperty, "Border");
        tile.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        tile.MouseEnter += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "BgHover");
        tile.MouseLeave += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        tile.MouseLeftButtonUp += (_, _) =>
        {
            TheApp.Projects.Touch(p.ProjectId);
            (Application.Current.MainWindow as MainWindow)?.NavigateToProject(p.WorkspaceKey, p.ProjectId);
        };
        tile.ToolTip = $"{p.Title}\n{p.Subtitle}\n最近打开:{p.LastOpened:M月d日 HH:mm}";
        return tile;
    }

    void UpdateClocks()
    {
        // 重建天气区时数组会被换掉,长度可能与地点表短暂不一致 -> 取交集,避免越界
        if (_cityTime.Length != _places.Count) return;

        var hour = DateTime.Now.Hour;
        _greeting.Text = hour < 5 ? "夜深了" : hour < 11 ? "早上好" : hour < 14 ? "中午好" : hour < 18 ? "下午好" : "晚上好";

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
            }
            catch { _cityTime[i].Text = "—"; _cityMeta[i].Text = ""; }
        }
    }
}
