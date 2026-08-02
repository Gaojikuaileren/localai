// P3c -- 周时间轴(用户裁定 2026-07-31)。
//
// 形态:竖轴是一天里的时刻,横轴是一周七天 —— 与上方月历的那一周对应,且【七列左右对齐】
//   (月历也留同样宽的左侧刻度列,两块看起来是同一张网格的上下两半)。
//
// 竖轴的可视域是【-1 点 ~ 25 点】而不是 0~24(用户裁定):
//   缩到最小时把前一天的最后一小时和次日的第一小时也露出来,跨零点的日程一眼看得全;
//   跨天的日程还会【在隔壁那一天续画】—— 那才是"跨天"该有的样子。
//
// 手势(用户裁定 2026-07-31,按【区域】分工,互不打架):
//   ┌ 左侧【刻度列】= 时间尺:滚轮缩放 · 左键上下拖也是缩放
//   └ 右侧【表格】  = 内容:  滚轮上下滑 · 左键在空白处上下拖也是上下滑
//   · 日程块:上边拖 = 改开始 · 下边拖 = 改结束 · 中间拖 = 【整体位移】(可跨天列)
//     三者一律【半小时】颗粒,连夹取的边界也落在半小时上。
//   · 点一下(没拖动)= 打开与日历共用的编辑抽屉。
//   ★ 新建日程已按用户要求改走【板块标题栏那个「+」】,这里不再接双击。
//
// ★★ 一条贯穿始终的纪律:这里【不存任何日程数据】。
//   画的时候现读 CalendarData,改的时候直接改 CalendarData —— 两边永远是同一份。

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class WeekTimeline : UserControl
{
    static readonly CultureInfo Zh = new("zh-CN");

    /// <summary>竖轴可视域的上端 = 前一天 23 点(好让跨零点的日程看得全)。</summary>
    public const double DayMin = -1;
    /// <summary>竖轴可视域的下端 = 次日 1 点。</summary>
    public const double DayMax = 25;

    /// <summary>放到最大时能看到的小时数(用户裁定:最多放到 6 小时)。</summary>
    public const double MinHours = 6;
    /// <summary>缩到最小时能看到的小时数 = 整个可视域。</summary>
    public const double MaxHours = DayMax - DayMin;
    /// <summary>默认常态:早上八点到晚上十点(用户裁定 2026-08-01)。</summary>
    public const double DefaultTop = 8;
    public const double DefaultHours = 22 - DefaultTop;

    /// <summary>改时间的颗粒度(用户裁定:半小时。夹取的边界也要落在这个格子上)。</summary>
    public const double SnapHours = 0.5;

    /// <summary>左侧时刻刻度列的宽度。★ 月历也要留同样一列,两块的七列才对得齐。</summary>
    public const double GutterWidth = 44;

    const double HeadHeight = 22;

    // 全天条带【常驻】(用户裁定):上一行放"塞不进条里的名字",下一行是条本身。
    // 高度恒定 —— 有没有全天日程都占这么多,否则刻度尺会一周一个样。
    // ★★ 这里有一个绕不过去的几何冲突,记一笔免得以后又绕回来:
    //   「多条共享宽度」与「跨天的条要连成一根」【互相不兼容】——
    //   横向分道之后,一条跨三天的日程在每一天只占那一天的 1/N 宽,三天的槽位并不相邻,
    //   连不成一根,只能画成三个小方块,看着像三件事而不是一件。
    //   所以按【是不是跨天】分成两行:
    //     · 上面一行 = 跨天的,整条连通(这一行才读得出"从哪天到哪天");
    //     · 下面一行 = 只占一天的,同一天有几条就【共享那一天的宽度】(用户要的就是这个)。
    //   两行都是固定高 -> 整条带常驻、高度恒定。
    const double AllDayBarHeight = 17;   // 条高 —— 减去下留白后要能完整装下一行 FontCaption
    const int AllDayRows = 2;
    // ★ 两行【贴在一起】(用户裁定 2026-07-31:"两个全天之间的间隔过大了,可以不要间隔")。
    //   原来两行之间夹着一条"名字行" —— 现在名字与定时日程同一套规矩:摆在条的右边、同高,
    //   不再单占一行,那条间隔自然就没了。
    const double AllDayStripHeight = AllDayRows * AllDayBarHeight + 3;
    const int AllDayMaxLanes = 4;

    // 表头 + 全天条带合成一块。自上而下:表头 / 跨天条 / 名字行 / 单日条。
    const double TopBlockHeight = HeadHeight + AllDayStripHeight;
    const double AllDayRowTop = HeadHeight;                                  // 第一行紧贴表头下沿

    const double TextOutsideBelow = 20;   // 比这还矮的条:标题挪到条外
    const double TextNeedsWidth = 52;     // 比这还窄的条:标题挪到条外(用户裁定:省略号什么都看不见)
    const double EdgeLinesAbove = 10;     // 比这还矮就不画起止线(线会把整条填满)
    const double DragThreshold = 3;       // 挪动超过这么多像素才算"拖",否则算"点一下"
    const double LabelLine = 14;          // 外置标题一行的高度(叠加避让用)
    const double WrapInsideAbove = 34;    // 窄但高过它的条：名字换行写在条里，而不是挪到外面

    DateTime _weekStart;
    double _hours = DefaultHours;
    double _top = 8;

    readonly Grid _head = new();
    readonly StackPanel _navCell = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _allDay = new() { Height = TopBlockHeight, ClipToBounds = true };
    readonly Canvas _allDayCanvas = new() { ClipToBounds = true, Background = Brushes.Transparent };
    TextBlock? _allDayMore;   // 左侧"全天"下面那个 "+N"(点击列出是哪几条 —— ToolTip 全局关着)
    List<string> _hiddenAllDay = new();   // 这一周没画出来的全天日程标题
    readonly Canvas _canvas = new() { ClipToBounds = true, Background = Brushes.Transparent };
    readonly Canvas _gutter = new() { Width = GutterWidth, ClipToBounds = true, Background = Brushes.Transparent, Cursor = Cursors.SizeNS };

    /// <summary>点日程要打开编辑器 —— 由宿主提供,保证与日历【共用同一个】编辑抽屉。</summary>
    public Action<CalendarEvent>? OnEditEvent;

    /// <summary>在某个时刻新建(目前无人触发 —— 新建走板块标题栏的「+」;接口留着供将来的快捷方式)。</summary>
    public Action<DateTime>? OnCreateAt;

    /// <summary>周变了 —— 宿主据此把上方月历也翻过去,免得上下说的不是同一周。</summary>
    public Action<DateTime>? WeekChanged;

    /// <summary>跨过了午夜 —— 宿主据此把月历也刷一遍(否则"今天"高亮还停在昨天)。</summary>
    public Action? DayRolled;

    /// <summary>
    /// 按了「现在」—— 宿主据此把月历的【选中日】也挪回今天。
    /// ★ 不能只靠 WeekChanged:那条路径是"保持星期几不变地换周",
    ///   按「现在」却回到了本周的周三 —— 人要的是今天。
    /// </summary>
    public Action? TodayRequested;

    /// <summary>当前显示的是哪一周(周一)。上方月历据此把那一排标出来。</summary>
    public DateTime CurrentWeekStart => _weekStart;

    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };
    DateTime _tickDay = DateTime.Today;

    public WeekTimeline()
    {
        _weekStart = StartOfWeek(DateTime.Today);
        _top = ClampTop(DefaultTop);

        // ★★ 顶栏那行【整行去掉】(用户裁定 2026-07-31):
        //   "7月20日–7月26日"这个标签是多余的 —— 横轴上每一格都写着日期了,
        //   而为了一行文字单占一行高,在这个只有 300px 的板块里太奢侈。
        //   ‹ 今 › 三个键收进【刻度列正上方那一格】(星期几那一行的第 0 列),那里本来就是空的。
        // ★ 「今」左右的 ‹ › 已按用户要求去掉 —— 换周走上面那张月历(点哪一天,下面就跟到哪一周)。
        _navCell.Children.Add(NavKey("现在", "回到本周的此刻(保持当前缩放)", () =>
        {
            // ★ 【保持当前缩放】(用户裁定)—— 不再把 _hours 复位成默认值。
            //   把此刻放在视野中间,比顶在最上面更容易一眼找到。
            _top = ClampTop(DateTime.Now.TimeOfDay.TotalHours - _hours / 2);
            _weekStart = StartOfWeek(DateTime.Today);
            Rebuild();
            TodayRequested?.Invoke();      // 月历的选中日也回到今天
        }));

        // ★★ 全天条带也要【让开左侧刻度列】—— 否则它的七列比下面的表格向左偏 44px，
        //   一条周四到周六的全天日程会看起来像是周三到周五的。
        // ★ 左侧那一小块:上行写"全天",下行在有没画出来的时候写 "+N"。
        //   之前把"还有 N 条全天"钉在画布右端 —— 它会盖在周日那一格的日期与条上。
        // ★ 解释走【点击弹层】而不是 ToolTip(审计 2026-08-02):全局 ToolTip 已按裁定关闭,
        //   挂在它上面的解释一条也弹不出来 —— 又退回当初被用户点名的"这个 +1 是什么"。
        _allDayMore = new TextBlock { Text = "", Visibility = Visibility.Collapsed, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        _allDayMore.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_hiddenAllDay.Count == 0) return;
            var m = new ContextMenu();
            m.Items.Add(new MenuItem { Header = "这一周没画出来的全天日程(上方月历里仍看得到):", IsEnabled = false });
            foreach (var nm in _hiddenAllDay) m.Items.Add(new MenuItem { Header = nm, IsEnabled = false });
            MenuHost.Show(m, _allDayMore);
        };
        _allDayMore.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _allDayMore.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var tagText = new TextBlock { Text = "全天", IsHitTestVisible = false };
        tagText.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        tagText.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var allDayTag = new StackPanel
        {
            Width = GutterWidth,
            Margin = new Thickness(4, AllDayRowTop + 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        allDayTag.Children.Add(tagText);
        allDayTag.Children.Add(_allDayMore);
        var allDayRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(allDayTag, Dock.Left);
        allDayRow.Children.Add(allDayTag);
        allDayRow.Children.Add(_allDayCanvas);

        // ★★ 表头与全天条带【叠在一起】:全天条在下层,周几/几号浮在上层 ——
        //   于是一条跨天的全天日程看起来像一条横幅,把它覆盖的那几天【连日期一起】框了进去。
        //   表头设成不参与命中测试,好让底下那条横幅仍然点得着、悬停得出提示。
        _head.IsHitTestVisible = false;

        // ★★ 导航键必须【放在表头外面】—— 之前它挂在 _head 里,而 _head 刚被设成
        //   IsHitTestVisible = false,于是连它一起失灵了(用户报的"「今」按钮不起作用")。
        //   IsHitTestVisible 是往下传染的,子元素想单独恢复也恢复不了,只能挪出来。
        _navCell.HorizontalAlignment = HorizontalAlignment.Left;
        _navCell.VerticalAlignment = VerticalAlignment.Top;
        _navCell.Width = GutterWidth;
        _navCell.Height = HeadHeight;

        var topBlock = new Grid { Height = TopBlockHeight };
        topBlock.Children.Add(allDayRow);
        topBlock.Children.Add(_head);
        topBlock.Children.Add(_navCell);
        _allDay.Child = topBlock;
        _allDay.Background = Brushes.Transparent;

        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_gutter, Dock.Left);
        body.Children.Add(_gutter);
        body.Children.Add(_canvas);

        var root = new DockPanel { LastChildFill = true, Background = Brushes.Transparent };
        DockPanel.SetDock(_allDay, Dock.Top); root.Children.Add(_allDay);
        root.Children.Add(body);
        Content = root;

        // ---------------- 手势(用户裁定 2026-07-31 第四版:两边【同一套】)----------------
        //   · 滚轮   = 上下滑(刻度列上也一样)
        //   · 左键拖 = 缩放(刻度列、表格空白处都一样)
        //   · 单击日程 = 编辑;拖日程上下边 = 改起止;拖中间 = 整体挤动
        // ★ 提示必须与实际手势一致 —— "双击空白处新建"已按用户要求取消,再写在这里就是在教一个不存在的操作。
        var tip = "滚轮 = 上下滑 · 按住拖 = 缩放"
                  + " · 单击日程 = 编辑 · 拖日程上下边 = 改起止、拖中间 = 整体挪动(半小时一格)";
        _gutter.ToolTip = tip;
        _canvas.ToolTip = tip;

        _gutter.MouseWheel += (_, e) => e.Handled = WheelPan(e.Delta);
        _canvas.MouseWheel += (_, e) => e.Handled = WheelPan(e.Delta);

        _gutter.MouseLeftButtonDown += (_, e) =>
        {
            if (_gutter.ActualHeight <= 0) return;
            e.Handled = true;
            BeginScale(e.GetPosition(_gutter).Y, _gutter.ActualHeight, _gutter);
        };
        // ★ 表格空白处:【单击 = 新建】(用户裁定 2026-07-31),按住拖 = 缩放。
        //   两者靠"有没有真的挪动过"区分 —— 与日程块那套"点=编辑、拖=改时间"是同一条规矩。
        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            // ★★ 只有【真的按在画布本身】才起手缩放。
            //   之前是无条件吃掉 + CaptureMouse ——
            //   画布里那些外置标题(lb)是 _canvas 的子元素,按下会冒泡上来被吃掉、捕获被抢走;
            //   而 CaptureMode.Element 下松手的事件直接投给捕获元素,【根本不经过 lb】,
            //   于是"外置标题点一下能编辑"这条裁定在实现上是死的(悬停有提示、光标是小手,点了没反应)。
            //   放行非画布来源之后,将来往画布里放的任何可点元素都不会再被吃掉。
            if (e.OriginalSource is not Canvas) return;
            e.Handled = true;
            if (_canvas.ActualHeight <= 0) return;
            // ★ 【点空白新建】已按用户要求取消 —— 新建只走板块标题栏那个「+」。
            //   空白处只剩一件事:按住拖 = 缩放。
            BeginScale(e.GetPosition(_canvas).Y, _canvas.ActualHeight, _canvas);
        };

        SizeChanged += (_, _) => Rebuild();
        _tick.Tick += OnTick;
        Loaded += (_, _) =>
        {
            CalendarData.Changed += OnDataChanged;
            CalendarGroups.Changed += OnDataChanged;   // 分类颜色变了也要重画
            Rebuild(); SyncTick();
        };
        // ★ 订阅必须成对 —— 静态事件只加不减会把已卸载的视图一直吊着
        Unloaded += (_, _) =>
        {
            CalendarData.Changed -= OnDataChanged;
            CalendarGroups.Changed -= OnDataChanged;
            _tick.Stop(); EndScale(); EndEventDrag();
        };
        IsVisibleChanged += (_, _) => SyncTick();
        IsEnabledChanged += (_, _) => SyncTick();
    }

    // ---------------------------------------------------------------- 对外
    /// <summary>把上方月历选中的那一天所在周同步过来 —— 两块要说的是同一周。</summary>
    public void FocusWeekOf(DateTime day)
    {
        var w = StartOfWeek(day);
        if (w == _weekStart) return;
        _weekStart = w;
        Rebuild();
    }

    /// <summary>直接指定可见的时间窗(渲染诊断与将来的"跳到某时段"用)。</summary>
    public void SetVisibleRange(double topHour, double hours)
    {
        _hours = Math.Clamp(hours, MinHours, MaxHours);
        _top = ClampTop(topHour);
        Rebuild();
    }

    void GoWeek(DateTime weekStart)
    {
        _weekStart = weekStart;
        Rebuild();
        WeekChanged?.Invoke(_weekStart);
    }

    /// <summary>
    /// 数据变了要重画 —— 但【正在拖】的时候不重画。
    /// 拖动本身每动一下就 Update 一次日程,Update 又发 Changed;若在此重建,
    /// 手底下那个 Border 会被换掉,而拖动状态记的是旧对象 —— 那正是"拖着拖着就脱手"。
    /// </summary>
    void OnDataChanged()
    {
        if (_evDrag is not null) return;
        Rebuild();
    }

    void SyncTick() { if (IsVisible && IsEnabled) _tick.Start(); else _tick.Stop(); }

    /// <summary>滚轮 = 上下滑。到顶/到底就【不吞】,把事件还给整页去滚。</summary>
    bool WheelPan(int delta)
    {
        // ★ 正在拖日程时不平移:平移会重画整块、把手底下那个 dg.Box 换掉,
        //   拖动状态记的还是旧对象 —— 表现就是"滚一下就脱手,再动没反应"。
        if (_evDrag is not null) return true;
        var step = Math.Max(0.5, _hours / 6.0);      // 一格约走可视范围的六分之一
        return PanTo(_top + (delta > 0 ? -step : step));
    }

    /// <summary>按住拖 = 缩放。锚点取按下那一点 —— 手底下的时刻在缩放中保持不动。</summary>
    void BeginScale(double y, double height, UIElement src)
    {
        var anchor = height > 0 ? Math.Clamp(y / height, 0, 1) : 0.5;
        _scale = new ScaleDrag(y, _hours, anchor, _top + _hours * anchor, src, false);
        src.CaptureMouse();
    }

    void OnTick(object? sender, EventArgs e)
    {
        if (_evDrag is not null || _scale is not null) return;
        if (DateTime.Today != _tickDay) { _tickDay = DateTime.Today; Rebuild(); DayRolled?.Invoke(); return; }
        RebuildVertical();
    }

    static DateTime StartOfWeek(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    // ---------------------------------------------------------------- 缩放 / 平移
    double ClampTop(double t) => Math.Clamp(t, DayMin, Math.Max(DayMin, DayMax - _hours));

    /// <returns>真的动了才 true —— 调用方据此决定要不要吞掉滚轮事件。</returns>
    bool Zoom(double factor, double anchor)
    {
        var hourAt = _top + _hours * anchor;
        var next = Math.Clamp(_hours * factor, MinHours, MaxHours);
        var nextTop = Math.Clamp(hourAt - next * anchor, DayMin, Math.Max(DayMin, DayMax - next));
        if (Math.Abs(next - _hours) < 0.001 && Math.Abs(nextTop - _top) < 0.001) return false;
        _hours = next; _top = nextTop;
        Rebuild();
        return true;
    }

    bool PanTo(double top)
    {
        var next = ClampTop(top);
        if (Math.Abs(next - _top) < 0.0001) return false;
        _top = next;
        RebuildVertical();
        return true;
    }

    /// <summary>
    /// 刻度列上方那一格里的小键。★ 要【看起来像按钮】(用户裁定):
    /// 有边框、有 hover 底色 —— 否则一个光秃秃的"今"字没人知道它能点。
    /// </summary>
    FrameworkElement NavKey(string glyph, string tip, Action onClick)
    {
        var t = new TextBlock { Text = glyph, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var hit = new Border
        {
            Child = t,
            Width = GutterWidth - 6, Height = 17,
            Margin = new Thickness(0, 0, 1, 0),
            BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tip,
        };
        hit.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        hit.SetResourceReference(Border.BorderBrushProperty, "Border");
        hit.MouseEnter += (_, _) => hit.SetResourceReference(Border.BackgroundProperty, "BgHover");
        hit.MouseLeave += (_, _) => hit.Background = Brushes.Transparent;
        hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return hit;
    }

    // ---------------------------------------------------------------- 坐标换算
    /// <summary>某天的某一刻 = 从那天 0 点算起的小时数(可 &lt;0 或 &gt;24 —— 跨天正靠这个画出来)。</summary>
    static double HoursFrom(DateTime day, DateTime t) => (t - day.Date).TotalHours;

    double YAt(double hour, double h) => (hour - _top) / _hours * h;

    static double Snap(double hours) => Math.Round(hours / SnapHours) * SnapHours;
    static double SnapUp(double hours) => Math.Ceiling(hours / SnapHours - 1e-9) * SnapHours;
    static double SnapDown(double hours) => Math.Floor(hours / SnapHours + 1e-9) * SnapHours;

    DateTime? AtPoint(Point p)
    {
        var w = _canvas.ActualWidth; var h = _canvas.ActualHeight;
        if (w <= 0 || h <= 0) return null;
        var col = (int)(p.X / (w / 7));
        if (col is < 0 or > 6) return null;
        return _weekStart.AddDays(col).Date.AddHours(Snap(_top + p.Y / h * _hours));
    }

    // ---------------------------------------------------------------- 绘制
    void Rebuild()
    {
        BuildHead();
        BuildAllDay();
        BuildGutter();
        BuildBody();
    }

    /// <summary>平移/改时间时只重画竖向的部分 —— 星期几与全天条带不会变。</summary>
    void RebuildVertical() { BuildGutter(); BuildBody(); }

    void BuildHead()
    {
        _head.Children.Clear();
        _head.ColumnDefinitions.Clear();
        _head.Height = HeadHeight;
        _head.VerticalAlignment = VerticalAlignment.Top;
        _head.Background = null;      // ★ 透出底下那条全天横幅
        _head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        for (int i = 0; i < 7; i++) _head.ColumnDefinitions.Add(new ColumnDefinition());

        for (int i = 0; i < 7; i++)
        {
            var d = _weekStart.AddDays(i);
            var isToday = d.Date == DateTime.Today;
            var t = new TextBlock
            {
                Text = d.ToString("ddd", Zh).Replace("星期", "") + " " + d.Day,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, isToday ? "Accent" : "FgMuted");
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            Grid.SetColumn(t, i + 1);
            _head.Children.Add(t);
        }
    }

    /// <summary>
    /// 【全天】条带:常驻固定高;同一天有几条就把这一天的宽度分成几份(用户裁定:共享宽度)。
    /// ★ 为什么非有不可:时间轴画的是 TimedOn(定时日程),全天日程根本不在里面 ——
    ///   不补这条,全天日程就看不见也点不着。
    /// ★ 分母取【整周的最大并发数】:逐日各算各的话,同一条跨天的横条会一天宽一天窄,看着像断了。
    /// </summary>
    void BuildAllDay()
    {
        _allDayCanvas.Children.Clear();
        var w = _allDayCanvas.ActualWidth;
        if (w <= 0) return;
        var colW = w / 7;

        var spans = CalendarData.SpansIn(_weekStart, 7);
        if (spans.Count == 0) return;

        // ★★ 两行【都能放任何一条】(不再是"上行只给跨天、下行只给单日")——
        //   之前跨天的只有一行,两条跨天日程一重叠就有一条被挤掉,于是常常冒出那个「+1」。
        //   现在按"先长后短"贪心塞进两行:长的先占,短的补空。
        //   单日的还可以【在同一格里共享宽度】—— 那是横向分道与跨天连通唯一能共存的地方
        //   (一条跨三天的若也横向分道,三段槽位并不相邻,连不成一根)。
        var ordered = spans.OrderByDescending(x => x.Span).ThenBy(x => x.Col).ToList();
        var blocked = new bool[AllDayRows, 7];     // 被跨天条占死的格
        var singles = new List<(CalendarEvent Ev, int Col, int Row, bool ClipStart, bool ClipEnd)>();
        var placedMulti = new List<(CalendarEvent Ev, int Col, int Span, bool ClipStart, bool ClipEnd, int Row)>();
        var hiddenNames = new List<string>();

        foreach (var sp in ordered)
        {
            if (sp.Col is < 0 or > 6) continue;
            int row = -1;
            for (int r = 0; r < AllDayRows && row < 0; r++)
            {
                var free = true;
                for (int k = 0; k < sp.Span && free; k++)
                {
                    var c = sp.Col + k;
                    if (c is < 0 or > 6) continue;
                    // 跨天的要整段干净;单日的允许与同格的其它单日共享宽度
                    if (blocked[r, c]) free = false;
                }
                if (free) row = r;
            }
            if (row < 0) { hiddenNames.Add(sp.Ev.Title); continue; }

            if (sp.Span > 1)
            {
                for (int k = 0; k < sp.Span; k++)
                    if (sp.Col + k is >= 0 and <= 6) blocked[row, sp.Col + k] = true;
                placedMulti.Add((sp.Ev, sp.Col, sp.Span, sp.ClipStart, sp.ClipEnd, row));
            }
            else singles.Add((sp.Ev, sp.Col, row, sp.ClipStart, sp.ClipEnd));
        }

        foreach (var (ev, col, span, clipStart, clipEnd, row) in placedMulti)
        {
            var x = col * colW + (clipStart ? 0 : 2);
            var bw = Math.Max(6, span * colW - (clipStart ? 0 : 2) - (clipEnd ? 0 : 2));
            // ★ 只有【贴着表头那一行】才在表头上铺横幅 —— 第二行离表头隔着一条,铺了反而对不上。
            AddAllDayChip(ev, x, AllDayRowTop + row * AllDayBarHeight, bw, 4, coverHeader: row == 0,
                          clipStart: clipStart, clipEnd: clipEnd);
        }

        // 同一(行, 格)里的单日条共享那一格的宽度
        var perCell = new Dictionary<(int Row, int Col), int>();
        foreach (var it in singles)
        {
            perCell.TryGetValue((it.Row, it.Col), out var n);
            perCell[(it.Row, it.Col)] = n + 1;
        }
        var used = new Dictionary<(int Row, int Col), int>();
        foreach (var (ev, col, row, cs, ce) in singles)
        {
            var lanes = Math.Clamp(perCell[(row, col)], 1, AllDayMaxLanes);
            used.TryGetValue((row, col), out var k);
            if (k >= lanes) { hiddenNames.Add(ev.Title); continue; }
            used[(row, col)] = k + 1;
            var slotW = colW / lanes;
            AddAllDayChip(ev, col * colW + k * slotW + 2, AllDayRowTop + row * AllDayBarHeight,
                          Math.Max(6, slotW - 4), 4, coverHeader: row == 0,
                          clipStart: cs, clipEnd: ce);
        }

        // ★ 真的没画出来的【如实说】。★★ 原来只写一个「+1」—— 用户直接问"这个 +1 是什么",
        //   一个要人猜的标记等于没标。改成一句白话,悬停再列出是哪几条。
        _hiddenAllDay = hiddenNames;
        if (_allDayMore is not null)
        {
            var any = hiddenNames.Count > 0;
            _allDayMore.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            _allDayMore.Text = any ? $"+{hiddenNames.Count}" : "";
        }
    }

    /// <summary>
    /// 一条全天日程的横条。塞不下名字就把名字放到条【上方那一行】——
    /// 与定时日程的窄条同一套规矩(用户裁定:省略成一个"…"等于什么都没显示)。
    /// </summary>
    /// <param name="clipStart">这一端是被【周界】裁断的(日程从上周延续过来)—— 那一端不收圆角。</param>
    void AddAllDayChip(CalendarEvent ev, double x, double y, double bw, double slotGap, bool coverHeader,
                       bool clipStart = false, bool clipEnd = false)
    {
        var back = CalendarGroups.ColorOf(ev.CalendarGroup);

        // ★ 贴着表头那一行:在表头上再铺一层同色淡底,与下面的实心条连成一整根横幅,
        //   把它覆盖的那几天连"周四 30 / 周五 31"一起框进去(用户裁定)。
        //   用淡底而不是实心:日期文字要照旧读得清,不必为它改字色。
        if (coverHeader)
        {
            var banner = new Border
            {
                Width = bw,
                Height = HeadHeight,
                Background = new SolidColorBrush(Color.FromArgb(0x33, back.R, back.G, back.B)),
                IsHitTestVisible = false,
            };
            banner.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            Canvas.SetLeft(banner, x);
            Canvas.SetTop(banner, 0);
            _allDayCanvas.Children.Add(banner);
        }

        var fits = bw >= TextNeedsWidth;
        var chip = new Border
        {
            Height = AllDayBarHeight - 2,
            Width = bw,
            Background = new SolidColorBrush(back),
            Cursor = Cursors.Hand,
            ToolTip = ev.Title + (ev.IsMultiDay ? $" · {ev.DayCount} 天" : " · 全天")
                      + (string.IsNullOrWhiteSpace(ev.CalendarGroup) ? "" : $"  [{ev.CalendarGroup}]"),
        };
        // ★ 与定时日程、与上方月历那条全天线同一套视觉语言:
        //   平口 = "还没完,隔壁那周/那天接着";圆角 = "就到这儿"。
        //   不传这两个标志的话,一条从上周延续过来的会画成四角全圆的小方块 ——
        //   看起来就是"周一新起的一件事",而月历里同一条是平口的,上下两块给出相反的读法。
        chip.CornerRadius = new CornerRadius(clipStart ? 0 : 5, clipEnd ? 0 : 5, clipEnd ? 0 : 5, clipStart ? 0 : 5);
        if (fits)
        {
            var t = new TextBlock
            {
                Text = ev.Title,
                Margin = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false,
                Foreground = new SolidColorBrush(CalendarGroups.TextOn(back)),
            };
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            chip.Child = t;
        }
        var captured = ev;
        chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; OnEditEvent?.Invoke(captured); };
        Canvas.SetLeft(chip, x);
        Canvas.SetTop(chip, y);
        _allDayCanvas.Children.Add(chip);

        if (fits) return;

        // ★★ 塞不下就把名字摆在条【右边、同高】—— 与定时日程的窄条同一套规矩。
        //   不再单占一行(那正是两行之间那条多余间隔的来历),也不会因为位置被占就消失。
        // ★★ 名字【不抢点击】也【不越栏】:
        //   之前它可命中、宽度又放到整个画布右沿 —— 同一格里两条全天时,
        //   左边那条的名字盖在右边那条身上,点右边那条弹出的却是左边那条的抽屉。
        //   现在:不参与命中(点击一律落回它自己那条),宽度收到本栏右沿;
        //   本栏右边摆不下就盖在自己头上(与定时日程同一套回退),而不是消失。
        var slotRight = x + bw + slotGap;
        var roomR = slotRight - (x + bw + 3);
        var lx = roomR >= 16 ? x + bw + 3 : x + 2;
        var lb = new TextBlock
        {
            Text = ev.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(CalendarGroups.OnSurface(back)),
            IsHitTestVisible = false,
            MaxWidth = Math.Max(16, slotRight - lx),
        };
        lb.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        Canvas.SetLeft(lb, lx);
        Canvas.SetTop(lb, y + 1);
        Panel.SetZIndex(lb, 8);
        _allDayCanvas.Children.Add(lb);
    }

    int TickStep => _hours <= 8 ? 1 : _hours <= 16 ? 2 : 3;

    void BuildGutter()
    {
        _gutter.Children.Clear();
        var h = _canvas.ActualHeight;
        if (h <= 0) return;

        var step = TickStep;
        for (double hr = Math.Ceiling(_top); hr <= _top + _hours + 0.001; hr += 1)
        {
            var ih = (int)hr;
            if (((ih % step) + step) % step != 0) continue;
            var y = YAt(hr, h);
            var outside = ih < 0 || ih > 24;
            var text = ih == 24 ? "24:00" : (((ih % 24) + 24) % 24).ToString("00") + ":00";
            var t = new TextBlock { Text = text, Opacity = outside ? 0.5 : 1, IsHitTestVisible = false };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            // ★ 上下都要夹住:标签骑在刻度线上居中(y-8),最上/最下那条会被裁掉半截。
            Canvas.SetTop(t, Math.Clamp(y - 8, 0, Math.Max(0, h - 15)));
            Canvas.SetLeft(t, 4);
            _gutter.Children.Add(t);
        }
    }

    /// <summary>一条日程在某一天要画的那一截。</summary>
    /// <param name="IsFirst">这一截包含【开始时刻】(= 起始那天)。只有它能拖顶边、能整体挪。</param>
    /// <param name="IsLast">这一截包含【结束时刻】(= 结束那天)。只有它能拖底边。</param>
    readonly record struct Seg(CalendarEvent Ev, double S, double E, bool IsFirst, bool IsLast);

    /// <summary>真正结束在哪一天 —— ★ 只转发模型上的唯一口径,别在这里再定义一份。</summary>
    static DateTime LastDayOf(CalendarEvent ev) => ev.LastDay;

    /// <summary>
    /// 某一天要画的所有条:自己起始的 + 【前面几天跨过来的】。
    /// ★ 往回找【整周】而不是只找前一天 —— 一条跨三天的定时日程,中间那天既不是它的起始日、
    ///   也不是结束日,只看前一天就会把它整段漏掉,那一天看起来就是空的。
    /// </summary>
    List<Seg> ItemsFor(DateTime day)
    {
        // ★ 直接按区间筛,而不是"往回找 N 天" —— 回看窗口一旦短于日程长度,
        //   超出那几天就整段不画(看着像那天没有这条日程)。顺带把每天 8 次全表扫描降成 1 次。
        var outp = new List<Seg>();
        foreach (var ev in CalendarData.Events)
        {
            if (ev.AllDay) continue;
            if (ev.Start.Date > day.Date || LastDayOf(ev) < day.Date) continue;
            outp.Add(new Seg(ev, HoursFrom(day, ev.Start), HoursFrom(day, ev.End),
                             IsFirst: ev.Start.Date == day.Date,
                             IsLast: LastDayOf(ev) == day.Date));
        }
        return outp.OrderBy(x => x.S).ThenBy(x => x.Ev.Id).ToList();
    }

    void BuildBody()
    {
        _canvas.Children.Clear();
        var w = _canvas.ActualWidth;
        var h = _canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var colW = w / 7;

        // ① 夜晚压暗。日出日落是【本地算】的,不出网(见 SunClock);坐标认不出就整块不画。
        if (Places.CoordOf(Places.Current()) is { } coord)
        {
            for (int i = 0; i < 7; i++)
            {
                var d = _weekStart.AddDays(i);
                var sun = SunClock.ForDay(coord.Lat, coord.Lon, d, TimeZoneInfo.Local.GetUtcOffset(d).TotalHours);
                foreach (var (from, to) in new[] { (DayMin, sun.Sunrise), (sun.Sunset, DayMax) })
                {
                    if (to <= from) continue;
                    var y1 = YAt(from, h); var y2 = YAt(to, h);
                    if (y2 <= 0 || y1 >= h) continue;
                    var night = new System.Windows.Shapes.Rectangle
                    { Width = colW, Height = Math.Min(h, y2) - Math.Max(0, y1), Opacity = 0.5, IsHitTestVisible = false };
                    night.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "BgSunken");
                    Canvas.SetLeft(night, i * colW);
                    Canvas.SetTop(night, Math.Max(0, y1));
                    _canvas.Children.Add(night);
                }
            }
        }

        // ② 0 点之前 / 24 点之后那两条带子再压一档 —— 一眼看出"这里已经不是这一天了"
        foreach (var (from, to) in new[] { (DayMin, 0.0), (24.0, DayMax) })
        {
            var y1 = YAt(from, h); var y2 = YAt(to, h);
            if (y2 <= 0 || y1 >= h) continue;
            var band = new System.Windows.Shapes.Rectangle
            { Width = w, Height = Math.Min(h, y2) - Math.Max(0, y1), Opacity = 0.55, IsHitTestVisible = false };
            band.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "BgSunken");
            Canvas.SetLeft(band, 0);
            Canvas.SetTop(band, Math.Max(0, y1));
            _canvas.Children.Add(band);
        }

        // ③ 今天整列着重
        for (int i = 0; i < 7; i++)
        {
            if (_weekStart.AddDays(i).Date != DateTime.Today) continue;
            var band = new System.Windows.Shapes.Rectangle { Width = colW, Height = h, Opacity = 0.10, IsHitTestVisible = false };
            band.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
            Canvas.SetLeft(band, i * colW);
            Canvas.SetTop(band, 0);
            _canvas.Children.Add(band);
        }

        // ④ 横向时刻线 + 竖向分日线
        var step = TickStep;
        for (double hr = Math.Ceiling(_top); hr <= _top + _hours + 0.001; hr += 1)
        {
            var ih = (int)hr;
            if (((ih % step) + step) % step != 0) continue;
            var y = YAt(hr, h);
            var line = new System.Windows.Shapes.Line
            {
                X1 = 0, X2 = w, Y1 = y, Y2 = y, StrokeThickness = 1, IsHitTestVisible = false,
                Opacity = ih == 0 || ih == 24 ? 1 : 0.5,
            };
            line.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, ih == 0 || ih == 24 ? "BorderStrong" : "Border");
            _canvas.Children.Add(line);
        }
        for (int i = 1; i < 7; i++)
        {
            var line = new System.Windows.Shapes.Line
            { X1 = i * colW, X2 = i * colW, Y1 = 0, Y2 = h, StrokeThickness = 1, Opacity = 0.5, IsHitTestVisible = false };
            line.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Border");
            _canvas.Children.Add(line);
        }

        // ⑤ 日程块 —— 现读 CalendarData,不缓存
        for (int i = 0; i < 7; i++)
        {
            var day = _weekStart.AddDays(i);
            var items = ItemsFor(day);
            // ★★ 避让清单先【把这一天所有日程块的位置全算出来】,再开始画。
            //   只靠"边画边攻入清单"不够:先画的那条根本不知道右边还会来一条,
            //   于是它的名字照样会被后来那条压住(默认 14 小时视野下特别明显)。
            var laid = LayOut(items);
            var placed = new List<Rect>();
            var draw = new List<(Seg It, double X, double W, double Y0, double Y1)>();
            foreach (var (idx, col, total) in laid)
            {
                var it = items[idx];
                if (it.E <= _top || it.S >= _top + _hours) continue;
                var slotW = Math.Max(8, (colW - 6) / total);
                var x = i * colW + 3 + col * slotW;
                var y0 = YAt(it.S, h); var y1 = YAt(it.E, h);
                var bw = slotW - (total > 1 ? 2 : 0);
                draw.Add((it, x, bw, y0, y1));
                placed.Add(new Rect(x, y0, Math.Max(10, bw), Math.Max(3, y1 - y0)));
            }
            foreach (var (it, x, bw, y0, y1) in draw)
                AddEventBlock(it, day, x, y0, y1, bw, placed, i * colW, (i + 1) * colW, h);
        }

        // ⑥ 此刻的红线(只在本周画)
        if (DateTime.Today >= _weekStart && DateTime.Today <= _weekStart.AddDays(6))
        {
            var nowH = DateTime.Now.TimeOfDay.TotalHours;
            if (nowH >= _top && nowH <= _top + _hours)
            {
                var y = YAt(nowH, h);
                var now = new System.Windows.Shapes.Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, StrokeThickness = 1.5, IsHitTestVisible = false };
                now.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "RiskDanger");
                _canvas.Children.Add(now);
            }
        }
    }

    /// <summary>
    /// 同一天里【互相重叠】的条各占一列,平分这一天的宽度(用户裁定)。
    /// 先按开始时刻切成"重叠簇",簇内贪心分列 —— 同簇共用列数,于是左右边对得齐。
    /// </summary>
    static List<(int Index, int Col, int Total)> LayOut(List<Seg> items)
    {
        var res = new List<(int, int, int)>();
        int i = 0;
        while (i < items.Count)
        {
            int j = i;
            var clusterEnd = items[i].E;
            while (j + 1 < items.Count && items[j + 1].S < clusterEnd)
            {
                j++;
                if (items[j].E > clusterEnd) clusterEnd = items[j].E;
            }
            var colEnd = new List<double>();
            var assign = new int[j - i + 1];
            for (int k = i; k <= j; k++)
            {
                int c = 0;
                while (c < colEnd.Count && items[k].S < colEnd[c]) c++;
                if (c == colEnd.Count) colEnd.Add(items[k].E); else colEnd[c] = items[k].E;
                assign[k - i] = c;
            }
            for (int k = i; k <= j; k++) res.Add((k, assign[k - i], colEnd.Count));
            i = j + 1;
        }
        return res;
    }

    /// <summary>
    /// 一条日程:方块 + 起止两条反色线。
    /// ★ 太矮【或】太窄的条,标题放到条【外面】(上方)—— 省略成一个"…"等于什么都没显示(用户裁定)。
    ///   外置标题之间互相避让:同一天里已经占住的位置会被往上让一行。
    /// </summary>
    void AddEventBlock(Seg seg, DateTime segDay, double x, double yTop, double yBottom, double width,
                       List<Rect> placedLabels, double colLeft, double colRight, double h)
    {
        var ev = seg.Ev;
        var isTail = !seg.IsFirst;
        var height = Math.Max(3, yBottom - yTop);
        var w = Math.Max(10, width);
        // ★★ 定时日程用【描边框】，全天日程用【实心色块】(用户裁定 2026-07-31)。
        //   好处不只是好看：描边不会把底下的昼夜带与"今天"那列盖死，
        //   一眼就能看出一条日程是白天还是夜里。
        var back = CalendarGroups.ColorOf(ev.CalendarGroup);
        var onBack = back;                                   // 描边框里的字就用分类色本色
        var fill = Color.FromArgb(0x22, back.R, back.G, back.B);   // 极淡的同色底，只为了让块还是个块
        // ★★ 三档：
        //   ① 够宽够高 -> 名字写在条里，单行；
        //   ② 窄但【够高】 -> 仍写在条里，但【换行】—— 一条两小时高、只有 40px 宽的，
        //     把名字挪到外面反而无处可放(上方往往就是别的日程色块)，换行写里面反而看得清；
        //   ③ 矮条 -> 名字放到条外上方。
        var wideEnough = w >= TextNeedsWidth;
        var tallEnough = height >= TextOutsideBelow;
        var wrapInside = !wideEnough && height >= WrapInsideAbove;
        var textInside = tallEnough && (wideEnough || wrapInside);

        // ★★ 先把内层组装好再交给 Border —— 不要先 Child = a 再改挂到别处("元素已有另一个逻辑父级")。
        var inner = new Grid();
        if (textInside)
        {
            var t = new TextBlock
            {
                Text = ev.Title,
                // ★ 块的顶部被裁到可视区之上时,标题要跟着下来 ——
                //   否则跨天的那几段(顶在 -2 点、甚至更早)字直接被裁掉,
                //   第二天那一截只剩一个光秃的描边框,不悬停就不知道是什么。
                Margin = new Thickness(4, Math.Max(3, -yTop + 3), 3, 3),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = wrapInside ? TextWrapping.Wrap : TextWrapping.NoWrap,
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = new SolidColorBrush(onBack),
            };
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            inner.Children.Add(t);
        }
        // 起止两条线：描边框下它们就是【上下两条加粗的边】——
        // 仍然明确标出"从哪儿起、到哪儿止",而且不再需要额外元素。
        var edgeThick = height >= EdgeLinesAbove ? 2.0 : 1.0;

        var box = new Border
        {
            Child = inner,
            Width = w,
            Height = height,
            Background = new SolidColorBrush(fill),
            BorderBrush = new SolidColorBrush(CalendarGroups.OnSurface(back)),
            BorderThickness = new Thickness(1, edgeThick, 1, edgeThick),
            Opacity = isTail ? 0.75 : 1,          // 续画的那一半淡一点 —— 它的"主体"在隔壁那天
            Cursor = Cursors.SizeAll,
            ToolTip = $"{ev.Start:M月d日 HH:mm} – " + (LastDayOf(ev) > ev.Start.Date ? $"{ev.End:M月d日 HH:mm}" : $"{ev.End:HH:mm}")
                      + $"  {ev.Title}"
                      + (LastDayOf(ev) > ev.Start.Date ? $"(跨 {(LastDayOf(ev) - ev.Start.Date).Days + 1} 天)" : "")
                      + (string.IsNullOrWhiteSpace(ev.CalendarGroup) ? "" : $"  [{ev.CalendarGroup}]"),
        };
        // ★ 被切断的那一端【不收圆角】—— 与月历里那条全天线同一条视觉语言:
        //   平口 = "还没完,隔壁那天接着";圆角 = "就到这儿"。
        box.CornerRadius = new CornerRadius(seg.IsFirst ? 3 : 0, seg.IsFirst ? 3 : 0,
                                            seg.IsLast ? 3 : 0, seg.IsLast ? 3 : 0);

        if (!textInside)
        {
            // 外置标题:优先摆在条的正上方;被占了就往上让一行,最多让三行,再不行就不画(有 ToolTip 兜)
            // ★ 外置标题本身也要【悬停出全名、点一下能编辑】(用户裁定)——
            //   它往往比那条细细的色块好碰得多。
            var lb = new TextBlock
            {
                Text = ev.Title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = Math.Max(30, colRight - x),
                Foreground = new SolidColorBrush(CalendarGroups.OnSurface(back)),
                Background = Brushes.Transparent,      // 透明但可命中，否则 ToolTip 不会弹
                Cursor = Cursors.Hand,
                ToolTip = box.ToolTip,
            };
            var capturedLb = ev;
            lb.MouseLeftButtonUp += (_, le) => { le.Handled = true; OnEditEvent?.Invoke(capturedLb); };
            lb.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            lb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var lw = Math.Min(lb.DesiredSize.Width, lb.MaxWidth);

            // ★★ 这里前后错了两次,结论写清楚以免再绕:
            //   ① "撞上了就往上让一行" -> 一堆窄条挨着时,标题被顶到离自己两三行远,
            //      中间还夹着别人的标题,认不出主(用户:"离自己原本块越来越远");
            //   ② "位置被占就不画" -> 名字直接没了(用户:"这种情况就直接消失了")。
            //   所以现在:【永远画,且永远与条同高】—— 同一个 y 是最硬的归属提示。
            //   优先摆在条的右边;右边挤不下就盖在条自己头上向右溢出。
            //   宁可两个名字在横向上碰一点,也不让它飘走或者消失。
            // ★★ 右边那个位置【不能落在别人的色块上】——
            //   落上去的话,那个名字看起来就像是那一条的(归属整个反了)。
            //   挤不开就盖在【自己】头上 —— 宁可截断成一两个字,也不让它认错主。
            var right = x + w + 3;
            var bandTop = yTop + (yBottom - yTop) / 2 - LabelLine / 2;
            var rightBusy = placedLabels.Any(o =>
                Math.Abs(o.Left - x) > 0.5 &&                       // 不算它自己那个块
                o.Right > right && o.Left < colRight &&
                o.Top < bandTop + LabelLine && o.Bottom > bandTop);
            var roomRight = colRight - right;
            var lx = (roomRight >= 16 && !rightBusy) ? right : x + 2;
            var lyMid = yTop + (yBottom - yTop) / 2 - LabelLine / 2;   // 与条垂直居中

            // ★★ 宽度收到【右边第一个已占位置】之前。不收的话,同一高度上的
            //   几个名字会直接叠在一起(默认 14 小时视野下尤其明显)。
            //   收了之后最差也只是截断成省略号 —— 但【仍然贴着自己那一条、仍然一定会画】,
            //   前面错过的两版(往上让得飘走 / 被占就不画)都不会回来。
            var limit = colRight;
            foreach (var o in placedLabels)
                if (o.Left > lx + 1 && o.Top < lyMid + LabelLine && o.Bottom > lyMid)
                    limit = Math.Min(limit, o.Left - 2);
            lb.MaxWidth = Math.Max(14, limit - lx);
            Canvas.SetLeft(lb, lx);
            Canvas.SetTop(lb, Math.Clamp(lyMid, 0, Math.Max(0, h - LabelLine)));
            Panel.SetZIndex(lb, 5);      // 标题浮在所有色块之上
            _canvas.Children.Add(lb);
            placedLabels.Add(new Rect(lx, lyMid, lb.MaxWidth, LabelLine));
            Panel.SetZIndex(lb, 6);
        }

        var grab = Math.Min(6, Math.Max(2, height / 3));
        box.MouseMove += (_, e) =>
        {
            if (_evDrag is not null) return;
            var y = e.GetPosition(box).Y;
            box.Cursor = y <= grab || y >= box.Height - grab ? Cursors.SizeNS : Cursors.SizeAll;
        };
        // ★ Down 一律吃掉:否则会冒泡到画布,顺手起一次平移(点日程时画面跟着晃)。
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            var y = e.GetPosition(box).Y;
            var mode = y <= grab ? DragMode.Start : y >= box.Height - grab ? DragMode.End : DragMode.Move;
            // ★★ 一条跨天日程被切成好几段,每一段只对【它真的持有的那一端】负责:
            //   · 顶边 = 开始时刻 -> 只有起始那段能拖;
            //   · 底边 = 结束时刻 -> 只有末段能拖;
            //   · 整体挪动 -> 只有起始那段能做(在中间段挪,人分不清动的是哪一头)。
            //   够不着的那些,一律退化成"点了一下" -> 打开编辑抽屉,在那里改是明确的。
            if (mode == DragMode.Start && !seg.IsFirst) mode = DragMode.Move;
            if (mode == DragMode.End && !seg.IsLast) mode = DragMode.Move;
            if (mode == DragMode.Move && !seg.IsFirst)
            {
                // 只登记一次"点了一下",不进入拖动
                _evDrag = new EventDrag(ev, DragMode.Move, e.GetPosition(_canvas), box, false, ev.Start, ev.End, segDay, CanDrag: false);
                _canvas.CaptureMouse();
                return;
            }
            _evDrag = new EventDrag(ev, mode, e.GetPosition(_canvas), box, false, ev.Start, ev.End, segDay);
            _canvas.CaptureMouse();
        };
        // ★ 这里【不挂】MouseLeftButtonUp:按下时鼠标已被 _canvas 捕获,
        //   松手的事件走的是捕获目标那条路,根本到不了这个 Border。
        //   点击开编辑统一在 EndEventDrag 里判(见那里的说明)。

        Canvas.SetLeft(box, x);
        Canvas.SetTop(box, yTop);
        _canvas.Children.Add(box);
    }

    // ---------------------------------------------------------------- 拖动
    enum DragMode { Start, End, Move }

    /// <summary>
    /// ★★ 【绝对】口径:Ev0 与 From 记的都是按下那一刻的原始值,全程不更新。
    /// 增量口径(每帧算 dy、取整、施加、再把基准挪到当前位置)有两个硬伤:
    ///   ① 不足半个颗粒时 return 且不更新基准 -> 余量被吞,慢拖时误差同号累加(实测跑得比光标快近一倍);
    ///   ② 越界守卫也 return -> 拖过头的位移积在基准里,回拖要先"还"完才动,就是死区。
    /// 绝对口径下两条都不存在,而且吸附是对【绝对时刻】做的 ——
    /// 9:07 这种脏时刻会一次性归到 9:00/9:30,这才是"半小时颗粒"该有的效果。
    /// </summary>
    /// <param name="Box">被拖的那个方块 —— 拖动期间【直接挪它】,不重建。</param>
    /// <param name="Start">预览中的开始/结束(还没写进 CalendarData)。</param>
    /// <param name="CanDrag">false = 这一段够不着它要改的那一端,只当"点了一下"。</param>
    /// <param name="SegDay">被拖的【那一段】画在哪一天的列上 —— 预览的纵向原点要用它。</param>
    sealed record EventDrag(CalendarEvent Ev0, DragMode Mode, Point From, Border Box,
                            bool Moved, DateTime Start, DateTime End, DateTime SegDay, bool CanDrag = true);
    sealed record ScaleDrag(double FromY, double Hours0, double Anchor, double HourAtAnchor, UIElement Src, bool Moved);

    EventDrag? _evDrag;
    ScaleDrag? _scale;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = _canvas.ActualHeight;
        if (h <= 0) return;

        // 左侧刻度列:上下拖 = 缩放(往下拉 = 把尺拉长 = 放大)
        if (_scale is { } sc)
        {
            var dy = e.GetPosition(sc.Src).Y - sc.FromY;
            if (!sc.Moved)
            {
                if (Math.Abs(dy) < DragThreshold) return;    // 还没真的挪动 -> 仍然算"点一下"
                _scale = sc = sc with { Moved = true };
            }
            var factor = Math.Pow(2, -dy / 220.0);          // 拖 220px 约缩放一倍
            var next = Math.Clamp(sc.Hours0 * factor, MinHours, MaxHours);
            if (Math.Abs(next - _hours) < 0.001) return;
            _hours = next;
            _top = ClampTop(sc.HourAtAnchor - _hours * sc.Anchor);
            Rebuild();
            return;
        }

        if (_evDrag is not { } dg) return;

        var cur = e.GetPosition(_canvas);
        if (!dg.Moved)
        {
            if (Math.Abs(cur.Y - dg.From.Y) < DragThreshold && Math.Abs(cur.X - dg.From.X) < DragThreshold) return;
            if (!dg.CanDrag) return;      // 中间段/末段的"整体挪动":登记了但不真的动
            _evDrag = dg = dg with { Moved = true };
        }

        var ev0 = dg.Ev0;
        var day = ev0.Start.Date;
        var moved = (cur.Y - dg.From.Y) / h * _hours;
        var s0 = HoursFrom(day, ev0.Start);
        var e0 = HoursFrom(day, ev0.End);

        DateTime start, end;
        switch (dg.Mode)
        {
            case DragMode.Start:
                {
                    // ★ 夹取的【边界本身也落在半小时上】—— 否则顶到下限时会得到 9:07 这种脏时刻
                    //   (用户报的"现在可以到 15 分钟"正是这么来的)。
                    // ★ 先算上界再夹:结束早于 00:30 的短日程(编辑器 5 分钟颗粒建得出、Apple 也常见)
                    //   会让上界算成负数,Math.Clamp(min > max) 直接抛 ArgumentException ——
                    //   表现是鼠标一动就弹一次"出错了",按着不放就一直弹。
                    var hiStart = SnapDown(e0 - SnapHours);
                    var t = hiStart <= 0 ? 0 : Math.Clamp(Snap(s0 + moved), 0, hiStart);
                    start = day.AddHours(t); end = ev0.End;
                    break;
                }
            case DragMode.End:
                {
                    // ★ 上限不再是 DayMax:跨天日程本来就可以跨好几天。
                    //   只保证"结束在开始之后至少一格",其余交给用户。
                    var t = Math.Max(SnapUp(s0 + SnapHours), Snap(e0 + moved));
                    start = ev0.Start; end = day.AddHours(t);
                    break;
                }
            default:
                {
                    // 整体位移:竖向改时刻,横向换一天(用户裁定"左键拖动是整体位移")
                    var dur = e0 - s0;
                    // ★★ 上界只管【开始时刻留在这一天里】,不管整条塞不塞得进可视域。
                    //   之前写的是 min(23.5, DayMax - dur) —— DayMax(25)是【竖轴可视域】,
                    //   拿它当"时长预算"是口径错用:一条 22:00→次日 02:00 的(dur=4)上界算出来是 21,
                    //   而它自己的起点就是 22 —— 一进拖动就被夹到 21:00,整条往前跳一小时,而且再也拖不回去;
                    //   dur≥25 时上界被压成 0,开始时刻被钉死在 00:00。松手还会写进 CalendarData(静默改数据)。
                    //   与紧邻的 End 分支口径也正相反 —— 那边刚写明"跨天日程本来就可以跨好几天"。
                    var t = Math.Clamp(Snap(s0 + moved), 0, 24 - SnapHours);
                    var colW = _canvas.ActualWidth / 7;
                    var dayShift = colW > 0 ? (int)Math.Round((cur.X - dg.From.X) / colW) : 0;
                    var newDay = day.AddDays(dayShift);
                    // 只能落在本周的七天里 —— 拖出去的话不知道该翻到哪一周,反而容易误操作
                    if (newDay < _weekStart) newDay = _weekStart;
                    if (newDay > _weekStart.AddDays(6)) newDay = _weekStart.AddDays(6);
                    start = newDay.AddHours(t); end = start.AddHours(dur);
                    break;
                }
        }

        if (dg.Start == start && dg.End == end) return;
        _evDrag = dg with { Start = start, End = end };

        // ★★ 【只挪这一个方块,不重建】—— 抖动的根就在"每动一下就把整块重画一遍":
        //   ① 重画会把手底下这个 Border 销毁重建;
        //   ② 一旦拖到与别的日程重叠,重叠簇的列数就变了,方块会当场横向缩一半、
        //      拖回来又弹回全宽 —— 在边界附近来回蹦,就是用户看到的抖;
        //   ③ 每帧还顺带触发 CalendarData.Changed,把上面那张月历也整个重建一遍。
        //   现在拖动期间只改这一个方块的位置/高度,数据在松手时提交一次。
        // ★★ 预览的纵向原点要用【这一段所在那一列的日期】,不是日程开始那天。
        //   跨天日程的尾段画在结束那天的列上,两者差整整 24×N 小时 ——
        //   用开始那天当原点的话,手一动方块就跳出可视区、还被拉成几十小时高,
        //   全程看不见自己在改什么(松手重建才恢复)。
        //   整体位移例外:那时方块本来就要换列,用新的 start.Date 才对。
        var org = dg.Mode == DragMode.Move ? start.Date : dg.SegDay;
        var y0 = YAt(HoursFrom(org, start), h);
        var y1 = YAt(HoursFrom(org, end), h);
        Canvas.SetTop(dg.Box, y0);
        dg.Box.Height = Math.Max(3, y1 - y0);
        if (dg.Mode == DragMode.Move && _canvas.ActualWidth > 0)
        {
            var colW = _canvas.ActualWidth / 7;
            var dayIdx = (int)Math.Round((start.Date - _weekStart).TotalDays);
            Canvas.SetLeft(dg.Box, dayIdx * colW + 3);
        }
        dg.Box.ToolTip = $"{start:M月d日 HH:mm} – {end:HH:mm}";
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        EndScale();
        // ★★ 【无条件】收尾。之前写的是"只有真的拖动过才收尾" ——
        //   于是点一下日程块(没拖动)时 _evDrag 永远留着,方块从此死死跟着鼠标走,
        //   而且编辑抽屉也打不开。这两件事是同一个漏洞的两面。
        EndEventDrag();
    }

    // ★ 捕获丢失也要收尾(拖到窗口外松手 / Alt+Tab / 右键中断),否则拖拽状态永远挂着。
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndScale(); EndEventDrag();
    }

    void EndScale()
    {
        if (_scale is not { } sc) return;
        _scale = null;
        if (sc.Src.IsMouseCaptured) sc.Src.ReleaseMouseCapture();
    }

    void EndEventDrag()
    {
        if (_evDrag is not { } dg) return;
        _evDrag = null;
        if (_canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
        // ★ 没拖动 = 【点了一下】-> 打开编辑抽屉(用户裁定)。
        if (!dg.Moved) { OnEditEvent?.Invoke(dg.Ev0); return; }

        // ★ 数据在这里【提交一次】。拖动全程只是把方块挪给人看。
        var live = CalendarData.Events.FirstOrDefault(x => x.Id == dg.Ev0.Id);
        if (live is not null && (live.Start != dg.Start || live.End != dg.End))
            CalendarData.Update(live with { Start = dg.Start, End = dg.End });   // 自带 Changed -> 整块重建
        else
            Rebuild();
    }
}
