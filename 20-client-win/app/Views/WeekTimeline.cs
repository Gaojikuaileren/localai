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
//   · 点一下(没拖动)= 打开与日历共用的编辑抽屉;空白处双击 = 在那个半小时上新建。
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
    /// <summary>默认常态:只显示 6 小时。</summary>
    public const double DefaultHours = 6;

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
    const double AllDayLabelHeight = 14;
    const double AllDayBarHeight = 15;
    const int AllDayRows = 2;
    const double AllDayStripHeight = AllDayLabelHeight + AllDayRows * AllDayBarHeight + 4;
    const int AllDayMaxLanes = 4;

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
    readonly Border _allDay = new() { Height = AllDayStripHeight, ClipToBounds = true };
    readonly Canvas _allDayCanvas = new() { ClipToBounds = true, Background = Brushes.Transparent };
    readonly Canvas _canvas = new() { ClipToBounds = true, Background = Brushes.Transparent };
    readonly Canvas _gutter = new() { Width = GutterWidth, ClipToBounds = true, Background = Brushes.Transparent, Cursor = Cursors.SizeNS };

    /// <summary>点日程要打开编辑器 —— 由宿主提供,保证与日历【共用同一个】编辑抽屉。</summary>
    public Action<CalendarEvent>? OnEditEvent;

    /// <summary>空白处双击 -> 在那个时刻新建。</summary>
    public Action<DateTime>? OnCreateAt;

    /// <summary>周变了 —— 宿主据此把上方月历也翻过去,免得上下说的不是同一周。</summary>
    public Action<DateTime>? WeekChanged;

    /// <summary>跨过了午夜 —— 宿主据此把月历也刷一遍(否则"今天"高亮还停在昨天)。</summary>
    public Action? DayRolled;

    /// <summary>当前显示的是哪一周(周一)。上方月历据此把那一排标出来。</summary>
    public DateTime CurrentWeekStart => _weekStart;

    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(30) };
    DateTime _tickDay = DateTime.Today;

    public WeekTimeline()
    {
        _weekStart = StartOfWeek(DateTime.Today);
        _top = ClampTop(DateTime.Now.Hour - 1);

        // ★★ 顶栏那行【整行去掉】(用户裁定 2026-07-31):
        //   "7月20日–7月26日"这个标签是多余的 —— 横轴上每一格都写着日期了,
        //   而为了一行文字单占一行高,在这个只有 300px 的板块里太奢侈。
        //   ‹ 今 › 三个键收进【刻度列正上方那一格】(星期几那一行的第 0 列),那里本来就是空的。
        // ★ 「今」左右的 ‹ › 已按用户要求去掉 —— 换周走上面那张月历(点哪一天,下面就跟到哪一周)。
        _navCell.Children.Add(NavKey("今", "回到本周的此刻(保持当前缩放)", () =>
        {
            // ★ 【保持当前缩放】(用户裁定)—— 不再把 _hours 复位成默认值。
            //   把此刻放在视野中间,比顶在最上面更容易一眼找到。
            _top = ClampTop(DateTime.Now.TimeOfDay.TotalHours - _hours / 2);
            GoWeek(StartOfWeek(DateTime.Today));
        }));

        // ★★ 全天条带也要【让开左侧刻度列】—— 否则它的七列比下面的表格向左偏 44px，
        //   一条周四到周六的全天日程会看起来像是周三到周五的。
        var allDayTag = new TextBlock
        {
            Text = "全天",
            Width = GutterWidth,
            Margin = new Thickness(4, AllDayLabelHeight + 2, 0, 0),
            IsHitTestVisible = false,
        };
        allDayTag.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        allDayTag.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var allDayRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(allDayTag, Dock.Left);
        allDayRow.Children.Add(allDayTag);
        allDayRow.Children.Add(_allDayCanvas);
        _allDay.Child = allDayRow;
        _allDay.Background = Brushes.Transparent;

        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_gutter, Dock.Left);
        body.Children.Add(_gutter);
        body.Children.Add(_canvas);

        var root = new DockPanel { LastChildFill = true, Background = Brushes.Transparent };
        DockPanel.SetDock(_head, Dock.Top); root.Children.Add(_head);
        DockPanel.SetDock(_allDay, Dock.Top); root.Children.Add(_allDay);
        root.Children.Add(body);
        Content = root;

        // ---------------- 手势(用户裁定 2026-07-31 第四版:两边【同一套】)----------------
        //   · 滚轮   = 上下滑(刻度列上也一样)
        //   · 左键拖 = 缩放(刻度列、表格空白处都一样)
        //   · 双击空白处 = 新建;拖日程 = 改时间/挪动;单击日程 = 编辑
        var tip = "滚轮 = 上下滑 · 按住拖 = 缩放 · 双击空白处 = 新建"
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
        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount >= 2)
            {
                EndScale();
                if (AtPoint(e.GetPosition(_canvas)) is { } w) OnCreateAt?.Invoke(w);
                return;
            }
            if (_canvas.ActualHeight <= 0) return;
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
        var step = Math.Max(0.5, _hours / 6.0);      // 一格约走可视范围的六分之一
        return PanTo(_top + (delta > 0 ? -step : step));
    }

    /// <summary>按住拖 = 缩放。锚点取按下那一点 —— 手底下的时刻在缩放中保持不动。</summary>
    void BeginScale(double y, double height, UIElement src)
    {
        var anchor = height > 0 ? Math.Clamp(y / height, 0, 1) : 0.5;
        _scale = new ScaleDrag(y, _hours, anchor, _top + _hours * anchor, src);
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
            Width = 26, Height = 16,
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
        _head.Background = Brushes.Transparent;
        _head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        for (int i = 0; i < 7; i++) _head.ColumnDefinitions.Add(new ColumnDefinition());

        // 第 0 格(刻度列正上方)放 ‹ 今 › —— 这一格本来就是空的
        Grid.SetColumn(_navCell, 0);
        _head.Children.Add(_navCell);

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

        var multi = spans.Where(x => x.Span > 1).ToList();
        var single = spans.Where(x => x.Span <= 1).ToList();
        var hiddenNames = new List<string>();

        // ---- 第一行:跨天的,整条连通 ----
        var rowEnd = -1;
        foreach (var sp in multi)
        {
            if (sp.Col <= rowEnd) { hiddenNames.Add(sp.Ev.Title); continue; }     // 这一行放不下了(重叠)
            rowEnd = sp.Col + sp.Span - 1;
            var x = sp.Col * colW + (sp.ClipStart ? 0 : 2);
            var bw = Math.Max(6, sp.Span * colW - (sp.ClipStart ? 0 : 2) - (sp.ClipEnd ? 0 : 2));
            AddAllDayChip(sp.Ev, x, AllDayLabelHeight, bw, sp.Span, colW, null);
        }

        // ---- 第二行:只占一天的,同一天【共享宽度】 ----
        var perDay = new int[7];
        foreach (var sp in single) if (sp.Col is >= 0 and < 7) perDay[sp.Col]++;
        var used = new int[7];
        var labelRight = new double[7];
        foreach (var sp in single)
        {
            if (sp.Col is < 0 or > 6) continue;
            var lanes = Math.Clamp(perDay[sp.Col], 1, AllDayMaxLanes);
            if (used[sp.Col] >= lanes) { hiddenNames.Add(sp.Ev.Title); continue; }
            var slotW = colW / lanes;
            var x = sp.Col * colW + used[sp.Col] * slotW + 2;
            used[sp.Col]++;
            AddAllDayChip(sp.Ev, x, AllDayLabelHeight + AllDayBarHeight, Math.Max(6, slotW - 4), 1, colW, labelRight);
        }

        // ★ 名字/条没能画出来的【如实说有几条】—— 悄悄少画会让人以为那几天真的没安排
        if (hiddenNames.Count > 0)
        {
            // ★ 不只说"还有几条"，悬停就【把名字列出来】—— 否则你知道漏了东西却不知道漏了什么。
            var more = new TextBlock
            {
                Text = $"+{hiddenNames.Count}",
                Background = Brushes.Transparent,
                ToolTip = "没画出来的全天日程：" + string.Join("、", hiddenNames),
            };
            more.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            more.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            more.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(more, Math.Max(0, w - more.DesiredSize.Width - 2));
            Canvas.SetTop(more, 0);
            _allDayCanvas.Children.Add(more);
        }
    }

    /// <summary>
    /// 一条全天日程的横条。塞不下名字就把名字放到条【上方那一行】——
    /// 与定时日程的窄条同一套规矩(用户裁定:省略成一个"…"等于什么都没显示)。
    /// </summary>
    void AddAllDayChip(CalendarEvent ev, double x, double y, double bw, int span, double colW, double[]? labelRight)
    {
        var back = CalendarGroups.ColorOf(ev.CalendarGroup);
        var fits = bw >= TextNeedsWidth;
        var chip = new Border
        {
            Height = AllDayBarHeight - 3,
            Width = bw,
            Background = new SolidColorBrush(back),
            Cursor = Cursors.Hand,
            ToolTip = ev.Title + (ev.IsMultiDay ? $" · {ev.DayCount} 天" : " · 全天")
                      + (string.IsNullOrWhiteSpace(ev.CalendarGroup) ? "" : $"  [{ev.CalendarGroup}]"),
        };
        chip.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
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
        Canvas.SetTop(chip, y + 1);
        _allDayCanvas.Children.Add(chip);

        if (fits || labelRight is null) return;
        var col = Math.Clamp((int)(x / colW), 0, 6);
        if (x < labelRight[col]) return;                 // 这一行已经被占了 -> 让 hidden 去如实计数
        var lb = new TextBlock
        {
            Text = ev.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = Math.Max(24, (col + span) * colW - x),
            Foreground = new SolidColorBrush(back),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = chip.ToolTip,
        };
        lb.MouseLeftButtonUp += (_, le) => { le.Handled = true; OnEditEvent?.Invoke(captured); };
        lb.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        lb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(lb, x);
        Canvas.SetTop(lb, 0);
        _allDayCanvas.Children.Add(lb);
        labelRight[col] = x + Math.Min(lb.DesiredSize.Width, lb.MaxWidth) + 6;
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

    /// <summary>某一天要画的所有条:自己的定时日程 + 【前一天越过 24 点续过来的】。</summary>
    List<(CalendarEvent Ev, double S, double E, bool IsTail)> ItemsFor(DateTime day)
    {
        var outp = new List<(CalendarEvent, double, double, bool)>();
        foreach (var ev in CalendarData.TimedOn(day))
            outp.Add((ev, HoursFrom(day, ev.Start), HoursFrom(day, ev.End), false));
        // ★ 跨天日程在【隔壁那一天】也要有(用户裁定 2026-07-31)——
        //   一条 23:00→次日 01:00 的,昨天那列画到 25 点,今天这列还得从 -1 点续到 01:00。
        //   不这么画的话,从今天看过去那条日程就凭空消失了。
        var prev = day.Date.AddDays(-1);
        foreach (var ev in CalendarData.TimedOn(prev))
        {
            if (HoursFrom(prev, ev.End) <= 24) continue;
            outp.Add((ev, HoursFrom(day, ev.Start), HoursFrom(day, ev.End), true));
        }
        return outp.OrderBy(x => x.Item2).ToList();
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
            // ★ 避让清单里既要有【已放下的外置标题】,也要有【已画出的日程块】——
            //   只避标题的话,标题会落到上一条日程的色块上,同色叠同色等于看不见(实测)。
            var placed = new List<Rect>();
            foreach (var (idx, col, total) in LayOut(items))
            {
                var it = items[idx];
                if (it.E <= _top || it.S >= _top + _hours) continue;
                var slotW = Math.Max(8, (colW - 6) / total);
                var x = i * colW + 3 + col * slotW;
                AddEventBlock(it.Ev, x, YAt(it.S, h), YAt(it.E, h), slotW - (total > 1 ? 2 : 0), it.IsTail, placed,
                              i * colW, (i + 1) * colW, h);
            }
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
    static List<(int Index, int Col, int Total)> LayOut(List<(CalendarEvent Ev, double S, double E, bool IsTail)> items)
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
    void AddEventBlock(CalendarEvent ev, double x, double yTop, double yBottom, double width,
                       bool isTail, List<Rect> placedLabels, double colLeft, double colRight, double h)
    {
        var height = Math.Max(3, yBottom - yTop);
        var w = Math.Max(10, width);
        var back = CalendarGroups.ColorOf(ev.CalendarGroup);
        var onBack = CalendarGroups.TextOn(back);
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
                Margin = new Thickness(4, 3, 3, 3),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = wrapInside ? TextWrapping.Wrap : TextWrapping.NoWrap,
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = new SolidColorBrush(onBack),
            };
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            inner.Children.Add(t);
        }
        if (height >= EdgeLinesAbove)
        {
            foreach (var atTop in new[] { true, false })
            {
                var edge = new System.Windows.Shapes.Rectangle
                {
                    Height = 2, Opacity = 0.9, IsHitTestVisible = false,
                    VerticalAlignment = atTop ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                    Fill = new SolidColorBrush(onBack),
                };
                inner.Children.Add(edge);
            }
        }

        var box = new Border
        {
            Child = inner,
            Width = w,
            Height = height,
            Background = new SolidColorBrush(back),
            Opacity = isTail ? 0.75 : 1,          // 续画的那一半淡一点 —— 它的"主体"在隔壁那天
            Cursor = Cursors.SizeAll,
            ToolTip = $"{ev.Start:M月d日 HH:mm} – {ev.End:HH:mm}  {ev.Title}"
                      + (ev.End.Date > ev.Start.Date ? "(跨天)" : "")
                      + (string.IsNullOrWhiteSpace(ev.CalendarGroup) ? "" : $"  [{ev.CalendarGroup}]"),
        };
        box.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");

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
                Foreground = new SolidColorBrush(back),
                Background = Brushes.Transparent,      // 透明但可命中，否则 ToolTip 不会弹
                Cursor = Cursors.Hand,
                ToolTip = box.ToolTip,
            };
            var capturedLb = ev;
            lb.MouseLeftButtonUp += (_, le) => { le.Handled = true; OnEditEvent?.Invoke(capturedLb); };
            lb.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            lb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var lw = Math.Min(lb.DesiredSize.Width, lb.MaxWidth);
            double ly = yTop - LabelLine - 1;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var r = new Rect(x, ly, lw, LabelLine);
                if (!placedLabels.Any(o => o.IntersectsWith(r))) break;
                ly -= LabelLine;
            }
            var rect = new Rect(x, ly, lw, LabelLine);
            if (!placedLabels.Any(o => o.IntersectsWith(rect)) && ly > -LabelLine && ly < h)
            {
                placedLabels.Add(rect);
                Canvas.SetLeft(lb, x);
                Canvas.SetTop(lb, Math.Max(0, ly));
                _canvas.Children.Add(lb);
            }
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
            // ★★ 续画的那半截(跨零点之后落在隔壁那天的部分)【底边可以拖】——
            //   它的底边就是这条日程真正的结束时刻,拖它拉长是最自然的做法
            //   (用户反馈:跨天日程建不出来,就是因为这半截整个不接受拖动)。
            //   但它的【顶边】不是开始时刻(那在隔壁那天),拖了会让人搞不清动的是哪一头 ——
            //   顶边与中间一律当成"点了一下",交给编辑抽屉。
            if (isTail && mode != DragMode.End) mode = DragMode.Move;
            if (isTail && mode == DragMode.Move) { _evDrag = new EventDrag(ev, DragMode.Move, e.GetPosition(_canvas), box, false, ev.Start, ev.End); _canvas.CaptureMouse(); return; }
            _evDrag = new EventDrag(ev, mode, e.GetPosition(_canvas), box, false, ev.Start, ev.End);
            _canvas.CaptureMouse();
        };
        // ★ 这里【不挂】MouseLeftButtonUp:按下时鼠标已被 _canvas 捕获,
        //   松手的事件走的是捕获目标那条路,根本到不了这个 Border。
        //   点击开编辑统一在 EndEventDrag 里判(见那里的说明)。

        Canvas.SetLeft(box, x);
        Canvas.SetTop(box, yTop);
        _canvas.Children.Add(box);
        placedLabels.Add(new Rect(x, yTop, w, height));   // 后面的外置标题要绕开这个色块
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
    sealed record EventDrag(CalendarEvent Ev0, DragMode Mode, Point From, Border Box,
                            bool Moved, DateTime Start, DateTime End);
    sealed record ScaleDrag(double FromY, double Hours0, double Anchor, double HourAtAnchor, UIElement Src);

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
                    var t = Math.Clamp(Snap(s0 + moved), 0, SnapDown(e0 - SnapHours));
                    start = day.AddHours(t); end = ev0.End;
                    break;
                }
            case DragMode.End:
                {
                    var t = Math.Clamp(Snap(e0 + moved), SnapUp(s0 + SnapHours), DayMax);
                    start = ev0.Start; end = day.AddHours(t);
                    break;
                }
            default:
                {
                    // 整体位移:竖向改时刻,横向换一天(用户裁定"左键拖动是整体位移")
                    var dur = e0 - s0;
                    var t = Math.Clamp(Snap(s0 + moved), 0, Math.Max(0, SnapDown(Math.Min(24 - SnapHours, DayMax - dur))));
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
        var y0 = YAt(HoursFrom(start.Date, start), h);
        var y1 = YAt(HoursFrom(start.Date, end), h);
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
