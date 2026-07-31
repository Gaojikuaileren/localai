// P3c -- 周时间轴(用户裁定 2026-07-31)。
//
// 形态:竖轴是一天里的时刻,横轴是一周七天 —— 与上方月历的那一周对应,且【七列左右对齐】
//   (月历也留同样宽的左侧刻度列,两块看起来是同一张网格的上下两半)。
//
// 竖轴的可视域是【-1 点 ~ 25 点】而不是 0~24(用户裁定):
//   缩到最小时把前一天的最后一小时和次日的第一小时也露出来,
//   于是【跨零点的日程】可以一眼看全,也能直接把结束边拖过 24 点拖出来一条跨天的。
//
// 手势(用户裁定,互不打架):
//   · 滚轮 = 缩放,以光标所在时刻为锚点;表格内【任意像素】都能滚(容器都铺了透明底,否则收不到事件)
//   · 左键在空白处上下拖 = 平移(看别的时段)
//   · 左键在日程的上/下边拖 = 改开始/结束时间,颗粒【半小时】
//   · 左键点日程中间 = 打开【与日历共用的那个编辑抽屉】
//   · 空白处双击 = 在那个半小时上新建日程
//
// ★★ 一条贯穿始终的纪律:这里【不存任何日程数据】。
//   画的时候现读 CalendarData,改的时候直接改 CalendarData —— 两边永远是同一份。
//   一旦在这里缓存一份"时间轴自己的日程",就会出现"日历改了时间轴没跟上"的经典割裂。

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class WeekTimeline : UserControl
{
    static readonly CultureInfo Zh = new("zh-CN");

    /// <summary>竖轴可视域的上端 = 前一天 23 点(用户裁定:好让跨零点的日程看得全)。</summary>
    public const double DayMin = -1;
    /// <summary>竖轴可视域的下端 = 次日 1 点。</summary>
    public const double DayMax = 25;

    /// <summary>放到最大时能看到的小时数(用户裁定:最多放到 6 小时,再放就没意义了)。</summary>
    public const double MinHours = 6;
    /// <summary>缩到最小时能看到的小时数 = 整个可视域(26 小时)。</summary>
    public const double MaxHours = DayMax - DayMin;
    /// <summary>默认常态:只显示 6 小时。</summary>
    public const double DefaultHours = 6;

    /// <summary>拖动改时间的颗粒度(用户裁定:半小时)。</summary>
    public const double SnapHours = 0.5;

    /// <summary>左侧时刻刻度列的宽度。★ 月历也要留同样一列,两块的七列才对得齐。</summary>
    public const double GutterWidth = 44;

    const double HeadHeight = 22;       // 顶部星期几那一行
    const double AllDayRowHeight = 18;  // 全天条带里的一行
    const int AllDayMaxRows = 3;        // 最多摞三行,再多就在末行标"还有 N 条"
    const double TextOutsideBelow = 20; // 条比这还矮就把标题挪到条【外面】去(用户裁定)
    const double EdgeLinesAbove = 10;   // 条比这还矮就不画起止线了(线会把整条填满)

    DateTime _weekStart;                // 本周一
    double _hours = DefaultHours;       // 当前可见小时数
    double _top = 8;                    // 顶部对应的时刻(小时,可为负)

    readonly Grid _head = new();
    readonly Grid _allDay = new();      // 全天日程的条带(这一周没有全天日程就整条塌掉)
    readonly Canvas _canvas = new() { ClipToBounds = true, Background = Brushes.Transparent };
    readonly Canvas _gutter = new() { Width = GutterWidth, ClipToBounds = true, Background = Brushes.Transparent };
    readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center };

    /// <summary>点日程要打开编辑器 —— 由宿主提供,保证与日历【共用同一个】编辑抽屉。</summary>
    public Action<CalendarEvent>? OnEditEvent;

    /// <summary>空白处双击 -> 在那个时刻新建。同样由宿主接到日历自己的编辑抽屉上。</summary>
    public Action<DateTime>? OnCreateAt;

    /// <summary>周变了(翻周/回到今天)—— 宿主据此把上方月历也翻过去,免得上下说的不是同一周。</summary>
    public Action<DateTime>? WeekChanged;

    public WeekTimeline()
    {
        _weekStart = StartOfWeek(DateTime.Today);
        _top = Math.Clamp(DateTime.Now.Hour - 1, DayMin, DayMax - _hours);

        _label.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        _label.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        // 顶栏:‹ 周区间 › ……… 今
        // ★ 加减号【已移除】(用户裁定):缩放归滚轮,少两个没人点的按钮。
        var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Step("‹", "上一周", () => GoWeek(_weekStart.AddDays(-7))));
        left.Children.Add(_label);
        left.Children.Add(Step("›", "下一周", () => GoWeek(_weekStart.AddDays(7))));
        DockPanel.SetDock(left, Dock.Left); bar.Children.Add(left);

        var todayBtn = Step("今", "回到本周 · 当前时段", () =>
        {
            _hours = DefaultHours;
            _top = Math.Clamp(DateTime.Now.Hour - 1, DayMin, DayMax - _hours);
            GoWeek(StartOfWeek(DateTime.Today));
        });
        DockPanel.SetDock(todayBtn, Dock.Right); bar.Children.Add(todayBtn);

        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_gutter, Dock.Left);
        body.Children.Add(_gutter);
        body.Children.Add(_canvas);

        var root = new DockPanel { LastChildFill = true, Background = Brushes.Transparent };
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        DockPanel.SetDock(_head, Dock.Top); root.Children.Add(_head);
        DockPanel.SetDock(_allDay, Dock.Top); root.Children.Add(_allDay);
        root.Children.Add(body);
        Content = root;

        // 上手提示:这三个手势都没有可见的按钮,不说一声没人猜得到
        _canvas.ToolTip = "滚轮 = 缩放 · 按住上下拖 = 平移 · 空白处双击 = 在那个时刻新建 · 拖日程的上下边 = 改时间";

        // ★ 滚轮缩放。以【光标所在时刻】为锚点 —— 否则缩放时内容会从指尖溜走。
        // ★★ 表格内任意像素都要能滚(用户反馈"只有可交互元素才能缩放"):
        //   成因是 Canvas/Grid 的 Background 默认为 null = 【不参与命中测试】,
        //   鼠标事件根本不从那里发出,自然也冒泡不到这里。上面几个容器都铺了透明底就好了。
        MouseWheel += (_, e) =>
        {
            e.Handled = true;       // 别让整页的 ScrollViewer 也跟着滚
            var f = e.Delta > 0 ? 1 / 1.2 : 1.2;
            var anchor = _canvas.ActualHeight > 0 ? e.GetPosition(_canvas).Y / _canvas.ActualHeight : 0.5;
            Zoom(f, Math.Clamp(anchor, 0, 1));
        };

        // 左键在空白处:单击拖 = 平移;双击 = 在那个半小时上新建
        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount >= 2)
            {
                EndPan();
                var p = e.GetPosition(_canvas);
                var when = AtPoint(p);
                if (when is { } w) OnCreateAt?.Invoke(w);
                return;
            }
            if (_canvas.ActualHeight <= 0) return;
            _pan = new Pan(e.GetPosition(_canvas).Y, _top);
            _canvas.CaptureMouse();
            _canvas.Cursor = Cursors.ScrollNS;
        };

        SizeChanged += (_, _) => Rebuild();
        Loaded += (_, _) => { CalendarData.Changed += OnDataChanged; Rebuild(); };
        // ★ 订阅必须成对 —— 静态事件只加不减会把已卸载的视图一直吊着(WPF-PITFALLS 第 7 条)
        Unloaded += (_, _) => { CalendarData.Changed -= OnDataChanged; EndPan(); EndResize(); };
    }

    /// <summary>
    /// 直接指定可见的时间窗。供渲染诊断与将来的"跳到某时段"用 ——
    /// 参数一律走与滑轮同一套夹取,不会因为外部传了谱儿上的值而画出域外的东西。
    /// </summary>
    public void SetVisibleRange(double topHour, double hours)
    {
        _hours = Math.Clamp(hours, MinHours, MaxHours);
        _top = ClampTop(topHour);
        Rebuild();
    }

    /// <summary>把上方月历选中的那一天所在周同步过来 —— 两块要说的是同一周。</summary>
    public void FocusWeekOf(DateTime day)
    {
        var w = StartOfWeek(day);
        if (w == _weekStart) return;
        _weekStart = w;
        Rebuild();
    }

    void GoWeek(DateTime weekStart)
    {
        _weekStart = weekStart;
        Rebuild();
        WeekChanged?.Invoke(_weekStart);   // ★ 反向告诉月历,否则上下两块会各说各的周
    }

    /// <summary>
    /// 数据变了要重画 —— 但【正在拖】的时候不重画。
    /// 拖动本身每动一下就 Update 一次日程,Update 又发 Changed;若在此重建,
    /// 手底下那个 Border 会被换掉,而拖动状态记的是旧对象 —— 这正是"拖着拖着就脱手"的成因。
    /// 拖完在 EndResize 里补一次全量重建。
    /// </summary>
    void OnDataChanged()
    {
        if (_resize is not null) return;
        Rebuild();
    }

    static DateTime StartOfWeek(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));   // 周一起始

    /// <param name="anchor">0..1,画布上要保持不动的那个纵向位置。</param>
    void Zoom(double factor, double anchor)
    {
        var hourAt = _top + _hours * anchor;                 // 锚点处对应的时刻
        var next = Math.Clamp(_hours * factor, MinHours, MaxHours);
        if (Math.Abs(next - _hours) < 0.001) return;
        _hours = next;
        _top = ClampTop(hourAt - _hours * anchor);
        Rebuild();
    }

    double ClampTop(double t) => Math.Clamp(t, DayMin, Math.Max(DayMin, DayMax - _hours));

    FrameworkElement Step(string glyph, string tip, Action onClick)
    {
        var t = new TextBlock { Text = glyph, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        t.IsHitTestVisible = false;                          // 命中交给外面那块(项目一贯做法)
        var hit = new Grid { Width = 24, Height = 20, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip };
        hit.Children.Add(t);
        hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return hit;
    }

    // ---------------------------------------------------------------- 坐标换算
    /// <summary>某天的某一刻 = 从那天 0 点算起的小时数(可以 &lt;0 或 &gt;24 —— 跨天的日程正靠这个画出来)。</summary>
    static double HoursFrom(DateTime day, DateTime t) => (t - day.Date).TotalHours;

    double YAt(double hour, double h) => (hour - _top) / _hours * h;

    /// <summary>画布上的一点 -> 那一天的那个半小时(超出七列或可视域则为 null)。</summary>
    DateTime? AtPoint(Point p)
    {
        var w = _canvas.ActualWidth; var h = _canvas.ActualHeight;
        if (w <= 0 || h <= 0) return null;
        var col = (int)(p.X / (w / 7));
        if (col < 0 || col > 6) return null;
        var hour = _top + p.Y / h * _hours;
        hour = Math.Round(hour / SnapHours) * SnapHours;
        return _weekStart.AddDays(col).Date.AddHours(hour);
    }

    // ---------------------------------------------------------------- 绘制
    void Rebuild()
    {
        BuildHead();
        BuildAllDay();
        BuildGutter();
        BuildBody();
        _label.Text = $"{_weekStart:M月d日}–{_weekStart.AddDays(6):M月d日}";
    }

    /// <summary>平移时只重画竖向的部分 —— 星期几那一行不会变,没必要每帧重建。</summary>
    void RebuildVertical()
    {
        BuildGutter();
        BuildBody();
    }

    void BuildHead()
    {
        _head.Children.Clear();
        _head.ColumnDefinitions.Clear();
        _head.Height = HeadHeight;
        _head.Background = Brushes.Transparent;
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
    /// 【全天】条带:横跨它覆盖的那几天,点一下就编辑那一条。
    /// ★ 为什么非有不可:时间轴画的是 TimedOn(定时日程),全天日程根本不在里面;
    ///   月历左下角那份当日列表又已按用户要求拿掉 —— 不补这条,全天日程就【看不见也点不着】。
    /// ★ 这一周没有全天日程就整条塌掉,不白占纵向空间。
    /// </summary>
    void BuildAllDay()
    {
        _allDay.Children.Clear();
        _allDay.ColumnDefinitions.Clear();
        _allDay.RowDefinitions.Clear();

        var spans = CalendarData.SpansIn(_weekStart, 7);
        if (spans.Count == 0) { _allDay.Visibility = Visibility.Collapsed; _allDay.Height = 0; return; }
        _allDay.Visibility = Visibility.Visible;
        _allDay.Background = Brushes.Transparent;

        _allDay.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        for (int i = 0; i < 7; i++) _allDay.ColumnDefinitions.Add(new ColumnDefinition());

        // 贪心分行:同一行里列区间不相交才放得下
        var rowEnd = new List<int>();      // 每行已占到第几列(不含)
        var placed = new List<(CalendarEvent Ev, int Col, int Span, bool ClipStart, bool ClipEnd, int Row)>();
        int overflow = 0;
        foreach (var sp in spans)
        {
            int r = 0;
            while (r < rowEnd.Count && sp.Col < rowEnd[r]) r++;
            if (r >= AllDayMaxRows) { overflow++; continue; }
            if (r == rowEnd.Count) rowEnd.Add(sp.Col + sp.Span); else rowEnd[r] = sp.Col + sp.Span;
            placed.Add((sp.Ev, sp.Col, sp.Span, sp.ClipStart, sp.ClipEnd, r));
        }

        var rows = Math.Max(1, rowEnd.Count);
        for (int r = 0; r < rows; r++) _allDay.RowDefinitions.Add(new RowDefinition { Height = new GridLength(AllDayRowHeight) });
        _allDay.Height = rows * AllDayRowHeight + 2;

        var tag = new TextBlock
        {
            Text = "全天",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 1, 0, 0),
        };
        tag.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        tag.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        Grid.SetColumn(tag, 0); Grid.SetRow(tag, 0);
        _allDay.Children.Add(tag);

        foreach (var (ev, col, span, clipStart, clipEnd, row) in placed)
        {
            var t = new TextBlock
            {
                Text = ev.Title,
                Margin = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgOnAccent");
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

            var chip = new Border
            {
                Child = t,
                Height = AllDayRowHeight - 3,
                Margin = new Thickness(clipStart ? 0 : 2, 0, clipEnd ? 0 : 2, 3),
                Cursor = Cursors.Hand,
                ToolTip = ev.Title + (ev.IsMultiDay ? $" · {ev.DayCount} 天" : " · 全天"),
            };
            chip.SetResourceReference(Border.BackgroundProperty, "AccentSecondary");
            chip.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            var captured = ev;
            chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; OnEditEvent?.Invoke(captured); };

            Grid.SetColumn(chip, col + 1); Grid.SetColumnSpan(chip, span); Grid.SetRow(chip, row);
            _allDay.Children.Add(chip);
        }

        // ★ 放不下的【如实说有几条】—— 悄悄少画几条会让人以为那几天真的没安排
        if (overflow > 0)
        {
            var more = new TextBlock
            {
                Text = $"还有 {overflow} 条全天日程",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            };
            more.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            more.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            Grid.SetColumn(more, 1); Grid.SetColumnSpan(more, 7); Grid.SetRow(more, rows - 1);
            _allDay.Children.Add(more);
        }
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

            // 0~24 之外的那两小时是【前一天 / 次日】—— 淡一档,和当天区分开
            var outside = ih < 0 || ih > 24;
            var text = ih == 24 ? "24:00" : (((ih % 24) + 24) % 24).ToString("00") + ":00";
            var t = new TextBlock { Text = text, Opacity = outside ? 0.5 : 1 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            // ★ 上下都要【夹住】。标签是骑在刻度线上居中的(y-8):
            //   最上面那条常常正好落在 y=0(上半截被裁),最下面那条落在 y=h(下半截被裁,
            //   用户报的"底部的 0 点被吃掉了一半"就是它)。夹进可视区最多偏 8px。
            Canvas.SetTop(t, Math.Clamp(y - 8, 0, Math.Max(0, h - 15)));
            Canvas.SetLeft(t, 4);
            _gutter.Children.Add(t);
        }
    }

    void BuildBody()
    {
        _canvas.Children.Clear();
        var w = _canvas.ActualWidth;
        var h = _canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var colW = w / 7;

        // ① 夜晚压暗(用户裁定 2026-07-31)。日出日落是【本地算】出来的,不出网(见 SunClock)。
        //   ★ 逐日算 —— 一周里日出日落每天都在挪,画出来是一条缓缓斜下去的界,而不是一条直线。
        //   ★ 坐标不认识就【整块不画】:宁可没有昼夜,也不画一个猜的 6:00–18:00。
        if (Places.CoordOf(Places.Current()) is { } coord)
        {
            for (int i = 0; i < 7; i++)
            {
                var d = _weekStart.AddDays(i);
                var sun = SunClock.ForDay(coord.Lat, coord.Lon, d, TimeZoneInfo.Local.GetUtcOffset(d).TotalHours);
                // 夜 = 这一天日出之前 + 日落之后。可视域是 -1~25,所以两头都要画到域外去。
                foreach (var (from, to) in new[] { (DayMin, sun.Sunrise), (sun.Sunset, DayMax) })
                {
                    if (to <= from) continue;
                    var y1 = YAt(from, h); var y2 = YAt(to, h);
                    if (y2 <= 0 || y1 >= h) continue;
                    var night = new System.Windows.Shapes.Rectangle
                    {
                        Width = colW,
                        Height = Math.Min(h, y2) - Math.Max(0, y1),
                        Opacity = 0.5,
                        IsHitTestVisible = false,
                    };
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
            {
                Width = w,
                Height = Math.Min(h, y2) - Math.Max(0, y1),
                Opacity = 0.55,
            };
            band.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "BgSunken");
            Canvas.SetLeft(band, 0);
            Canvas.SetTop(band, Math.Max(0, y1));
            _canvas.Children.Add(band);
        }

        // ③ 今天整列着重(用户裁定)—— 先铺底,别盖住日程
        for (int i = 0; i < 7; i++)
        {
            if (_weekStart.AddDays(i).Date != DateTime.Today) continue;
            var band = new System.Windows.Shapes.Rectangle { Width = colW, Height = h, Opacity = 0.10 };
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
                X1 = 0, X2 = w, Y1 = y, Y2 = y, StrokeThickness = 1,
                Opacity = ih == 0 || ih == 24 ? 1 : 0.5,      // 零点那两条实一点 —— 它是"换天"的界
            };
            line.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, ih == 0 || ih == 24 ? "BorderStrong" : "Border");
            _canvas.Children.Add(line);
        }
        for (int i = 1; i < 7; i++)
        {
            var line = new System.Windows.Shapes.Line { X1 = i * colW, X2 = i * colW, Y1 = 0, Y2 = h, StrokeThickness = 1, Opacity = 0.5 };
            line.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Border");
            _canvas.Children.Add(line);
        }

        // ⑤ 日程块 —— ★ 现读 CalendarData,不缓存
        for (int i = 0; i < 7; i++)
        {
            var day = _weekStart.AddDays(i);
            foreach (var (ev, col, total) in LayOut(CalendarData.TimedOn(day).ToList()))
            {
                var s = HoursFrom(day, ev.Start);
                var e = HoursFrom(day, ev.End);
                if (e <= _top || s >= _top + _hours) continue;        // 完全在视野外,不必建元素

                var slotW = Math.Max(8, (colW - 6) / total);          // ★ 重叠的几条【平分这一天的宽度】
                var x = i * colW + 3 + col * slotW;
                AddEventBlock(ev, x, YAt(s, h), YAt(e, h), slotW - (total > 1 ? 2 : 0), h);
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
    /// 同一天里【互相重叠】的日程各占一列,平分这一天的宽度(用户裁定)。
    /// 做法:先按开始时刻切成一个个"重叠簇",簇内贪心分列 ——
    /// 同一簇里的所有条共用一个列数,于是它们左右边对得齐,不会一条宽一条窄。
    /// </summary>
    static List<(CalendarEvent Ev, int Col, int Total)> LayOut(List<CalendarEvent> evs)
    {
        var res = new List<(CalendarEvent, int, int)>();
        int i = 0;
        while (i < evs.Count)
        {
            int j = i;
            var clusterEnd = evs[i].End;
            while (j + 1 < evs.Count && evs[j + 1].Start < clusterEnd)
            {
                j++;
                if (evs[j].End > clusterEnd) clusterEnd = evs[j].End;
            }

            var colEnd = new List<DateTime>();
            var assign = new int[j - i + 1];
            for (int k = i; k <= j; k++)
            {
                int c = 0;
                while (c < colEnd.Count && evs[k].Start < colEnd[c]) c++;
                if (c == colEnd.Count) colEnd.Add(evs[k].End); else colEnd[c] = evs[k].End;
                assign[k - i] = c;
            }
            for (int k = i; k <= j; k++) res.Add((evs[k], assign[k - i], colEnd.Count));
            i = j + 1;
        }
        return res;
    }

    /// <summary>
    /// 一条日程:方块 + 起止两条反色线。上下各留一条【改时间的边】,中间点开编辑。
    /// ★ 太矮的条把标题放到条【外面】(上方)—— 否则 12px 高的条里塞不下字,只能看到半截。
    /// ★ 拖动改的是 CalendarData 里那一条 —— 时间轴与日历是同一份数据的两种画法。
    /// </summary>
    void AddEventBlock(CalendarEvent ev, double x, double yTop, double yBottom, double width, double h)
    {
        var height = Math.Max(3, yBottom - yTop);
        var w = Math.Max(10, width);

        // ★★ 先把内层组装好再交给 Border —— 不要先 Child = a 再改挂到别处。
        //   "元素已有另一个逻辑父级"在这个项目里撞过五次,而且是在构造期抛(= 程序打不开)。
        var inner = new Grid();

        var textInside = height >= TextOutsideBelow;
        if (textInside)
        {
            var t = new TextBlock
            {
                Text = ev.Title,
                Margin = new Thickness(5, 3, 4, 3),
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Top,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgOnAccent");
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            inner.Children.Add(t);
        }

        // 起止两条【着重色的反色】线(用户裁定)—— 明确标出这条从哪儿起、到哪儿止。
        // 太矮就不画:两条 2px 的线会把一条 5px 的方块整个填满,反而看不出边界。
        if (height >= EdgeLinesAbove)
        {
            foreach (var atTop in new[] { true, false })
            {
                var edge = new System.Windows.Shapes.Rectangle
                {
                    Height = 2,
                    Opacity = 0.9,
                    VerticalAlignment = atTop ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                    IsHitTestVisible = false,
                };
                edge.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "FgOnAccent");
                inner.Children.Add(edge);
            }
        }

        var box = new Border
        {
            Child = inner,
            Width = w,
            Height = height,
            Cursor = Cursors.Hand,
            ToolTip = $"{ev.Start:HH:mm}–{ev.End:HH:mm}  {ev.Title}"
                      + (ev.End.Date > ev.Start.Date ? "(跨天)" : ""),
        };
        box.SetResourceReference(Border.BackgroundProperty, "Accent");
        box.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");

        // 太矮 -> 标题挪到条【上方】,用着重色写(条本身就是它的色块)
        if (!textInside)
        {
            var outside = new TextBlock
            {
                Text = ev.Title,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false,
                Width = w,
            };
            outside.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            outside.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            Canvas.SetLeft(outside, x);
            Canvas.SetTop(outside, Math.Max(0, yTop - 15));
            _canvas.Children.Add(outside);
        }

        var grab = Math.Min(6, Math.Max(2, height / 3));   // 条很矮时"改时间的边"也要按比例收窄
        box.MouseMove += (_, e) =>
        {
            if (_resize is not null) return;
            var y = e.GetPosition(box).Y;
            box.Cursor = y <= grab || y >= box.Height - grab ? Cursors.SizeNS : Cursors.Hand;
        };
        // ★ Down 一律吃掉:否则它会冒泡到画布,顺手起一次平移(点日程时画面跟着晃)。
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            var y = e.GetPosition(box).Y;
            if (y > grab && y < box.Height - grab) return;      // 中间 -> 留给 Up 时的点击编辑
            _resize = new Resize(ev, y <= grab, e.GetPosition(_canvas).Y);
            _canvas.CaptureMouse();
        };
        box.MouseLeftButtonUp += (_, e) =>
        {
            if (_resize is not null) return;
            e.Handled = true;
            OnEditEvent?.Invoke(ev);
        };

        Canvas.SetLeft(box, x);
        Canvas.SetTop(box, yTop);
        _canvas.Children.Add(box);
    }

    // ---------------------------------------------------------------- 拖动:平移 与 改时间
    sealed record Resize(CalendarEvent Ev, bool IsStart, double FromY);
    sealed record Pan(double FromY, double Top0);
    Resize? _resize;
    Pan? _pan;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = _canvas.ActualHeight;
        if (h <= 0) return;

        if (_pan is { } pan)
        {
            var dy = e.GetPosition(_canvas).Y - pan.FromY;
            var next = ClampTop(pan.Top0 - dy / h * _hours);
            if (Math.Abs(next - _top) < 0.0001) return;
            _top = next;
            RebuildVertical();
            return;
        }

        if (_resize is not { } rz) return;

        var dh = (e.GetPosition(_canvas).Y - rz.FromY) / h * _hours;      // 位移换算成小时
        var snapped = Math.Round(dh / SnapHours) * SnapHours;             // ★ 半小时颗粒(用户裁定)
        if (Math.Abs(snapped) < 0.001) return;

        var ev = rz.Ev;
        var start = ev.Start;
        var end = ev.End;
        if (rz.IsStart) start = start.AddHours(snapped);
        else end = end.AddHours(snapped);

        // ★ 开始不许离开它自己那一天:TimedOn 是按 Start.Date 归日的,
        //   把开始拖过 0 点这条日程就整根跳到隔壁列去了 —— 那不是"跨天",是"换了一天"。
        //   要一条 23:00→次日 01:00 的,应当在 23:00 那天建、把【结束】往下拖。
        if (rz.IsStart && (start.Date != ev.Start.Date)) return;
        // 结束可以越过 24 点 —— 这正是跨天日程的建法;但不许超出可视域下端(次日 1 点)。
        if (!rz.IsStart && HoursFrom(ev.Start, end) > DayMax) return;
        // 最小长度 = 一个颗粒
        if (end - start < TimeSpan.FromHours(SnapHours)) return;

        CalendarData.Update(ev with { Start = start, End = end });   // 按 Id 更新那一条 —— 日历会同步看到
        _resize = rz with
        {
            Ev = CalendarData.Events.FirstOrDefault(x => x.Id == ev.Id) ?? ev,
            FromY = e.GetPosition(_canvas).Y,
        };
        RebuildVertical();     // ★ 自己重画。OnDataChanged 在拖动期间是主动让路的。
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        EndPan();
        EndResize();
    }

    // ★ 捕获丢失也要收尾(拖到窗口外松手、Alt+Tab、右键中断都会走这条),
    //   否则 _pan/_resize 永远挂着,鼠标一动画面就自己跑。
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndPan();
        EndResize();
    }

    void EndPan()
    {
        if (_pan is null) return;
        _pan = null;
        _canvas.Cursor = null;
        if (_canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
    }

    void EndResize()
    {
        if (_resize is null) return;
        _resize = null;
        if (_canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
        Rebuild();       // 拖动期间挡掉的那些 Changed,在这里一次补上
    }
}
