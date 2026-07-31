// P3c -- 统一日历组件。主页板块与顶栏日历浮窗【用同一套逻辑】。交互参考 MiniCal。
//
// 用户裁定的形态:
//   · 周排布:一行 7 天、共【两行】—— 第一行本周(正常),第二行下周(【灰色】);
//     翻页前进/后退一周,于是第二行会被提到第一行。
//   · 【只有周排布】在下方就地列出选中日的日程,占满多出来的纵向空间;
//     【月排布点日期则弹浮窗】显示当天日程 + 新增;当天没有日程时浮窗里只有"新增日程"。
//   · 点某条日程 -> 编辑那一条。【新增】按钮不放右上角:月排布在当日浮窗里,周排布在日程列表下方。
//   · 「今日」按钮放在【月份标签右侧】,不挤在翻页键那一堆里(否则翻页键会随它出现/消失位移)。
//   · 周/月两种排布【面板尺寸一样大】。
//
// ★ 数据源尚未接入 Apple 家庭共享日历(设计 §4.5 / 状态矩阵 §8):
//   编辑/新增可以填,但保存时如实拒绝并说明,绝不伪造日程或伪造同步成功。

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LocalAI.Client.I18n;

namespace LocalAI.Client.Views;

public sealed class CalendarView : UserControl
{
    public enum Mode { Week, Month }

    static readonly CultureInfo Zh = new("zh-CN");

    /// <summary>
    /// 面板固定高度 —— 周/月排布【共用同一尺寸】(用户裁定)。
    /// 以能完整容纳月历为准:标题 28 + 星期表头 18 + 6 行 × 30 + 余量。
    /// 周排布只用两行日期,剩下的纵向空间正好给"选中日的日程"列表。
    /// </summary>
    public const double PanelHeight = 268;

    /// <summary>
    /// 【只有月历】时的高度(主页那个"月历 + 时间轴"合并板块用)。
    /// = 顶栏 34 + 星期表头 15 + 6 行 × 40。★ 月排布【恒画 6 行】(见 MonthGrid):
    /// 5 行月和 6 行月若高度不同,下面的时间轴会随翻月上下跳 40px。
    /// </summary>
    public const double MonthOnlyHeight = 34 + 15 + 6 * (MonthCellHeight + SpanRowHeight + DotsRowHeight);

    // ★★★ 下面这几个是【宿主配置】属性,一律写成"改了就重建",不能用自动属性。
    //   踩过的坑:new CalendarView(Mode.Month) { HideDayArea = true, LeftGutter = 44 } ——
    //   C# 的对象初始化器是在【构造函数跑完之后】才赋值的,而构造函数末尾已经 Rebuild() 过一次了,
    //   于是这几个开关对那次(也是唯一一次)重建【完全没赶上】,界面上看起来像"设了没用"。
    //   现在 setter 自己补一次重建;值没变则不重建,免得白白重画。

    /// <summary>
    /// 月历七列左边要空出的宽度 —— 给下方时间轴的时刻刻度列让位,
    /// 这样上下两块的【七列是对齐的】(用户裁定 2026-07-31:"上面日历的周与下面时间轴的日期周没对齐")。
    /// 0 = 不留(顶栏浮窗那种独立用法)。
    /// </summary>
    public double LeftGutter
    {
        get => _leftGutter;
        set { if (Math.Abs(_leftGutter - value) < 0.01) return; _leftGutter = value; Rebuild(); }
    }
    double _leftGutter;

    /// <summary>
    /// 藏掉下方的「当日日期 + 新增按钮 + 当日日程列表」整块。
    /// 主页合并板块里由时间轴负责"那天几点干什么",这一块纯属重复,而且把高度吃掉了
    /// (用户裁定:"左下角的日期很多多余且显示不全""月视图下原本没有新建日程的按钮,现在多出来了")。
    /// </summary>
    public bool HideDayArea
    {
        get => _hideDayArea;
        set { if (_hideDayArea == value) return; _hideDayArea = value; Rebuild(); }
    }
    bool _hideDayArea;

    // 收紧日期区,把腾出的纵向空间让给周排布下方的当日日程表(用户裁定)
    const double MonthCellHeight = 28;
    const double WeekCellHeight = 32;

    // ★ 全天线【只占一行,高度统一】(用户裁定):
    //   ① 行数若随日程条数变化,日期区高度就会浮动,下方日程表位置跟着上下跳;
    //   ② 多条线分行画会"一上一下",看着杂乱。
    //   所以恒定预留【一行】:某天有几条全天日程都只画一条线(内容在当日浮窗/下方列表里看)。
    const int SpanRowsReserved = 1;
    const double SpanRowHeight = 5;      // 线高 3 + 下留白 2(上留白 0,紧贴数字)
    const double DotsRowHeight = 7;      // 圆点 5 + 下留白 2

    /// <summary>
    /// 定时(非全天)日程超过这个数量时,不再逐个画点,而是画一个【实心三角形】(用户裁定)。
    /// 理由:点再多也数不清,而且会挤破格宽;一个三角形明确表示"这天很满",细节看日程列表。
    /// </summary>
    const int DotsMaxBeforeTriangle = 4;

    /// <summary>全天线在【真正的起止日】那一端内缩的量 —— 让人看出日程从哪天起、到哪天止。</summary>
    const double EndInset = 15;

    Mode _mode;
    DateTime _anchor;                      // 周排布 = 第一行所在周的周一;月排布 = 所在月
    DateTime _selected = DateTime.Today;
    bool _animating;

    readonly TextBlock _label = new();
    readonly Border _labelButton;
    readonly StackPanel _leftActions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _rightActions = new() { Orientation = Orientation.Horizontal };
    readonly ContentControl _body = new();
    readonly StackPanel _dayList = new();
    readonly ScrollViewer _dayScroll;
    readonly DockPanel _dayArea;
    readonly Button _addButton;
    readonly TextBlock _dayTitle;
    /// <summary>日期 -> 那一天的格子。★ 每次 Rebuild 都会换新元素,浮窗必须挂在【重建后】的那个上。</summary>
    readonly Dictionary<DateTime, FrameworkElement> _dayCells = new();

    public CalendarView(Mode mode)
    {
        _mode = mode;
        _anchor = mode == Mode.Week ? StartOfWeek(DateTime.Today) : DateTime.Today;

        _label.FontWeight = FontWeights.SemiBold;
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        // 年月标签要【看起来像按钮】,否则用户不知道能点(用户反馈)。边框 + hover 底色 + 下拉箭头。
        var caret = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L8,0 L4,5 Z"),
            Margin = new Thickness(7, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        caret.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "FgMuted");
        var labelRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        labelRow.Children.Add(_label);
        labelRow.Children.Add(caret);
        _labelButton = new Border
        {
            Child = labelRow,
            Padding = new Thickness(8, 3, 8, 3),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent,
            ToolTip = "选择年份 / 月份",
        };
        _labelButton.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        _labelButton.SetResourceReference(Border.BorderBrushProperty, "Border");
        _labelButton.MouseEnter += (_, _) => _labelButton.SetResourceReference(Border.BackgroundProperty, "BgHover");
        _labelButton.MouseLeave += (_, _) => _labelButton.Background = Brushes.Transparent;
        // 浮层开着时的"第一次点击只关闭"由窗口层统一拦截(MainWindow.PreviewMouseDown),
        // 这里不再各写一遍。
        _labelButton.MouseLeftButtonUp += (s, _) => OpenMonthPicker((FrameworkElement)s);

        // 左:月份标签 +「今日」—— 紧跟标签,不与翻页键混在一起
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_labelButton);
        left.Children.Add(_leftActions);

        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(left, Dock.Left); head.Children.Add(left);
        DockPanel.SetDock(_rightActions, Dock.Right); head.Children.Add(_rightActions);

        _dayScroll = new ScrollViewer
        {
            Content = _dayList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough();

        // 周排布下方区域:第一行 =「选中日期」+ 右侧「新增日程」(与左侧日期同一行、等高);
        // 第二行 = 当日日程列表。用户裁定:新增按钮在选择日期的右方,尺寸与左方日期匹配。
        _dayTitle = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _dayTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        _dayTitle.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        _addButton = CompactAdd(() => OpenEditor(_selected, null));
        DockPanel.SetDock(_addButton, Dock.Right);

        var dayHead = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
        dayHead.Children.Add(_addButton);
        dayHead.Children.Add(_dayTitle);

        _dayArea = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(dayHead, Dock.Top);
        _dayArea.Children.Add(dayHead);
        _dayArea.Children.Add(_dayScroll);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        DockPanel.SetDock(_body, Dock.Top); root.Children.Add(_body);
        root.Children.Add(_dayArea);
        Content = root;

        Rebuild();

        // 日程数据变更 -> 自动重建(不再依赖"播种早于建窗口"的时序)
        CalendarData.Changed += Rebuild;
        Unloaded += (_, _) => CalendarData.Changed -= Rebuild;
    }

    // ---------------------------------------------------------------- 构建
    /// <summary>选中的日期变了 —— 宿主据此把下方时间轴聚焦到同一周。</summary>
    public Action<DateTime>? SelectionChanged;

    /// <summary>用【日历自己的那个编辑抽屉】打开某条 —— 时间轴点击时调它,保证两边共用一套。</summary>
    public void OpenEditorFor(CalendarEvent ev) => OpenEditor(ev.Start.Date, ev);

    /// <summary>
    /// 下方时间轴翻到了别的周 —— 月历也跟过去,免得上下两块各说各的周(用户反馈)。
    /// ★ 跨月的那一周按【周四归属】定月(ISO 周的惯例):7/27–8/2 这一周的周四是 7/30,
    ///   所以它算七月的周 —— 若按周一或周日定,同一周会在两个月之间来回横跳。
    /// </summary>
    public void FocusWeekStart(DateTime weekStart)
    {
        var keepDow = ((int)_selected.DayOfWeek + 6) % 7;   // 保持"星期几"不变,跨周更连贯
        // notify: false —— 这一路是【时间轴通知过来的】,再回传就绕成环了
        SelectDay(weekStart.AddDays(keepDow), notify: false);
        var pivot = weekStart.AddDays(3);
        if (pivot.Year != _anchor.Year || pivot.Month != _anchor.Month) _anchor = pivot;
        Rebuild();
    }

    /// <summary>在【指定时刻】新建 —— 时间轴空白处双击走这里，用的仍是日历自己那个编辑抽屉。</summary>
    public void OpenEditorAt(DateTime when)
    {
        SelectDay(when.Date, notify: false);   // 时间轴已经在那一周了,不必回传
        OpenEditor(when.Date, null, when.TimeOfDay);
        Rebuild();
    }

    internal void Rebuild()
    {
        _label.Text = _mode == Mode.Month ? _anchor.ToString("yyyy年 M月", Zh) : WeekRangeLabel();

        RefreshTodayButton();

        // 右:翻页 · 周/月切换。位置固定,不随「今日」出现而位移。
        _rightActions.Children.Clear();
        _rightActions.Children.Add(Btn("‹", () => Page(-1)));
        _rightActions.Children.Add(Btn("›", () => Page(1)));
        var modeBtn = Btn(_mode == Mode.Month ? "周" : "月", () =>
        {
            _mode = _mode == Mode.Month ? Mode.Week : Mode.Month;
            _anchor = _mode == Mode.Week ? StartOfWeek(_selected) : _selected;
            Rebuild();
        });
        if (HideModeSwitch) modeBtn.Visibility = Visibility.Collapsed;
        _rightActions.Children.Add(modeBtn);
        // ★ 右上角【不放】新增按钮(用户裁定):月排布的新增在当日浮窗里,周排布的在日程列表下方。

        _dayCells.Clear();     // ★ 旧元素在这一刻作废,浮窗只能挂在下面新建的那批上
        _body.Content = _mode == Mode.Week ? WeekRows() : MonthGrid();

        // ★ 两种排布都在下方列当日日程(用户 2026-07-31 推翻前让):
        //   月排布原来是"点日期直接开新增抽屉" —— 那是把【查看】当成了【新建】,
        //   想看看那天有什么安排反而做不到。现在点日期 = 选中并列出当天,
        //   新建走旁边那个「+」—— 两件事各走各的入口。
        // ★ 主页已取消周排布(月历 + 时间轴已经把它要表达的都表达了)—— 模式切换键不再显示。
        // ★ 主页合并板块里【整块藏掉】：那里的下半是时间轴，
        //   "那天几点干什么"已经画出来了，再摆一行日期 + 一个新增按钮既重复又吃高度。
        _dayArea.Visibility = HideDayArea ? Visibility.Collapsed : Visibility.Visible;
        if (HideDayArea) return;
        RebuildDayList();
    }

    /// <summary>宿主可以藏掉【月/周】切换键(主页合并板块里只要月排布)。</summary>
    public bool HideModeSwitch
    {
        get => _hideModeSwitch;
        set { if (_hideModeSwitch == value) return; _hideModeSwitch = value; Rebuild(); }
    }
    bool _hideModeSwitch;

    /// <summary>
    /// ★★ 唯一允许改 _selected 的地方。所有改视野的入口都要经过它。
    /// 踩过的坑:翻月只改了 _anchor,_selected 留在旧月那一天 ——
    ///   于是在 8 月的页面上点「+ 新增日程」,抽屉标题写着"7月 31日",存下去也真的落在 7/31。
    ///   而定时日程的编辑器里【没有日期字段】(只有时:分两列转盘,日期转盘只在勾了"全天"时才出现),
    ///   用户在编辑器里根本无从纠正 —— 这是静默把数据写错,不是显示问题。
    ///   同一个陈旧值还会让下方时间轴停在旧的那一周、让「周/月」切换把视野弹回旧月。
    /// </summary>
    void SelectDay(DateTime day, bool notify = true)
    {
        _selected = day.Date;
        if (notify) SelectionChanged?.Invoke(_selected);
    }

    /// <summary>
    /// 翻页/跳月之后把选中日【带进新视野】:优先保留"几号",那个月没有这一号就取当月最后一天;
    /// 今天若正好在新视野里则取今天(最符合直觉)。
    /// </summary>
    void CarrySelectionInto(DateTime anchor)
    {
        if (_mode == Mode.Week)
        {
            var keepDow = ((int)_selected.DayOfWeek + 6) % 7;
            SelectDay(anchor.Date.AddDays(keepDow));
            return;
        }
        if (DateTime.Today.Year == anchor.Year && DateTime.Today.Month == anchor.Month) { SelectDay(DateTime.Today); return; }
        var days = DateTime.DaysInMonth(anchor.Year, anchor.Month);
        SelectDay(new DateTime(anchor.Year, anchor.Month, Math.Min(_selected.Day, days)));
    }

    /// <summary>「回到今日」紧跟月份标签,仅当视野里看不到今天时出现。</summary>
    void RefreshTodayButton()
    {
        _leftActions.Children.Clear();
        if (ShowsToday()) return;
        var today = Btn("回到今日", () =>
        {
            _anchor = _mode == Mode.Month ? DateTime.Today : StartOfWeek(DateTime.Today);
            SelectDay(DateTime.Today);
            Rebuild();
        });
        today.Margin = new Thickness(10, 0, 0, 0);
        _leftActions.Children.Add(today);
    }

    string WeekRangeLabel()
    {
        var end = _anchor.AddDays(13);   // 两行 = 14 天
        return _anchor.Month == end.Month ? _anchor.ToString("yyyy年 M月", Zh)
                                          : $"{_anchor:M月d日} – {end:M月d日}";
    }

    bool ShowsToday()
        => _mode == Mode.Month
            ? _anchor.Year == DateTime.Today.Year && _anchor.Month == DateTime.Today.Month
            : DateTime.Today >= _anchor && DateTime.Today < _anchor.AddDays(14);

    // 周排布每次走【一周】—— 于是第二行(下周)被提到第一行
    void Step(int dir)
    {
        _anchor = _mode == Mode.Month ? _anchor.AddMonths(dir) : _anchor.AddDays(dir * 7);
        CarrySelectionInto(_anchor);   // ★ 选中日必须跟进新视野,否则「+ 新增日程」会建到上一个视野那天
    }

    /// <summary>
    /// 翻页 = 【横向滑动】,让人看清前后关系(硬切分不清方向)。
    /// 性能:只【多建一页】—— 新页与旧页短暂并存,动画结束立刻丢弃旧页。
    /// 不做多周预加载:日历一页只有几十个轻量元素,构建极快,预加载的收益抵不上常驻内存。
    /// </summary>
    void Page(int dir)
    {
        if (_animating) return;   // 动画中忽略连点,避免堆出多层残影
        _animating = true;

        var outgoing = _body.Content as UIElement;
        Step(dir);
        _label.Text = _mode == Mode.Month ? _anchor.ToString("yyyy年 M月", Zh) : WeekRangeLabel();
        RefreshTodayButton();
        var incoming = _mode == Mode.Week ? WeekRows() : MonthGrid();

        if (outgoing is null) { _body.Content = incoming; _animating = false; AfterPage(); return; }

        // ★ WPF 的元素只能有一个父级:outgoing 此刻还挂在 _body 上,
        //   直接 host.Children.Add(outgoing) 会抛异常 —— 这正是"切换下一页闪退"的原因。
        //   必须先把它从 _body 摘下来,再放进动画容器。
        _body.Content = null;

        var host = new Grid { ClipToBounds = true };
        var outT = new TranslateTransform();
        var inT = new TranslateTransform();
        outgoing.RenderTransform = outT;
        incoming.RenderTransform = inT;
        host.Children.Add(outgoing);
        host.Children.Add(incoming);
        _body.Content = host;

        // 翻页方向:【上下】滑动(用户裁定)。往后翻 = 旧页上移出、新页自下方升入。
        var dist = Math.Max(90, _body.ActualHeight > 0 ? _body.ActualHeight : 120);
        inT.Y = dir > 0 ? dist : -dist;

        var dur = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        outT.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, dir > 0 ? -dist : dist, dur) { EasingFunction = ease });
        var slideIn = new DoubleAnimation(inT.Y, 0, dur) { EasingFunction = ease };
        slideIn.Completed += (_, _) =>
        {
            // 同理:先把 incoming 从 host 摘下来,再挂到 _body,否则又是"两个父级"
            host.Children.Clear();
            _body.Content = null;
            incoming.RenderTransform = null;
            _body.Content = incoming;
            _animating = false;
            AfterPage();
        };
        inT.BeginAnimation(TranslateTransform.YProperty, slideIn);
    }

    /// <summary>翻页动画收尾:两种排布都要刷新下方当日日程区。</summary>
    void AfterPage()
    {
        _dayArea.Visibility = HideDayArea ? Visibility.Collapsed : Visibility.Visible;
        RebuildDayList();
    }

    static DateTime StartOfWeek(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));   // 周一起始

    /// <summary>紧凑的「新增日程」按钮 —— 高度与旁边的日期文字行匹配,不喧宾夺主。</summary>
    static Button CompactAdd(Action onClick)
    {
        var b = new Button
        {
            Content = "+ 新增日程", Height = 20, Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(10, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(1), Background = Brushes.Transparent,
            FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center,
        };
        b.SetResourceReference(Button.BorderBrushProperty, "Border");
        b.SetResourceReference(Button.ForegroundProperty, "FgSecondary");
        b.Click += (_, _) => onClick();
        return b;
    }

    static Button Btn(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text, Height = 22, MinWidth = 24, Padding = new Thickness(7, 0, 7, 0),
            Margin = new Thickness(4, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(1), Background = Brushes.Transparent, FontSize = 11,
        };
        b.SetResourceReference(Button.BorderBrushProperty, "Border");
        b.SetResourceReference(Button.ForegroundProperty, "FgSecondary");
        b.Click += (_, _) => onClick();
        return b;
    }

    static UniformGrid WeekdayHeader()
    {
        var header = new UniformGrid { Rows = 1, Columns = 7, Margin = new Thickness(0, 0, 0, 1) };
        foreach (var d in new[] { "一", "二", "三", "四", "五", "六", "日" })
        {
            var t = new TextBlock { Text = d, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 10.5 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            header.Children.Add(t);
        }
        return header;
    }

    // ---------------------------------------------------------------- 周 / 月 的横带
    // ★ 格内垂直顺序(用户裁定):【日期数字】→(留白)→【全天日程的线】→【定时日程的圆点】
    //   全天线必须在圆点【上方】,而不是压在最底下 —— 否则看起来像脚注,也分不清和圆点的关系。
    // 实现:一周做成一个 7 列 Grid,行结构 = 数字行 / 每条全天线各一行 / 圆点行。
    //   全天线用 ColumnSpan 贯穿多格(与日期格同宽);
    //   每列一个跨全部行的背景块承载"今天/选中"高亮与点击,线与圆点浮在它上面。

    UIElement WeekRows()
    {
        var panel = new StackPanel();
        panel.Children.Add(WeekdayHeader());
        // 第一行 = 本周(正常);第二行 = 下周(灰)
        for (int row = 0; row < 2; row++)
        {
            var start = _anchor.AddDays(row * 7);
            panel.Children.Add(Band(start, WeekCellHeight, _ => row == 1, showWeekday: false));
        }
        return panel;
    }

    /// <summary>月排布:整格月历,按周成带(前后补齐灰日,网格不残缺)。</summary>
    UIElement MonthGrid()
    {
        var panel = new StackPanel();
        panel.Children.Add(WeekdayHeader());

        var first = new DateTime(_anchor.Year, _anchor.Month, 1);
        var lead = ((int)first.DayOfWeek + 6) % 7;
        var days = DateTime.DaysInMonth(_anchor.Year, _anchor.Month);
        var gridStart = first.AddDays(-lead);

        // ★ 【恒画 6 行】而不是算出来几行就画几行：
        //   5 行月与 6 行月差一整行(40px)，翻月时下方的时间轴会跟着上下跳。
        //   多出来的那一行是下月的灰日，本来就要画。
        for (int w = 0; w < 6; w++)
            panel.Children.Add(MonthWeekBand(gridStart.AddDays(w * 7), first, days));

        // ★★ 七列左边留出与下方时间轴【同宽】的刻度列 ——
        //   不留的话月历的周一在最左，而时间轴的周一在 44px 之后，
        //   上下两块的同一天对不上(用户反馈"日历的周与时间轴的周没对齐")。
        if (_leftGutter > 0) panel.Margin = new Thickness(_leftGutter, 0, 0, 0);
        return panel;
    }

    /// <summary>月排布里的一周:非本月的日子置灰。</summary>
    UIElement MonthWeekBand(DateTime weekStart, DateTime monthFirst, int daysInMonth)
        => Band(weekStart, MonthCellHeight,
                d => d < monthFirst || d >= monthFirst.AddDays(daysInMonth), showWeekday: false);

    /// <summary>一周的分层横带。</summary>
    Border? _weekStrip;

    UIElement Band(DateTime weekStart, double numberHeight, Func<DateTime, bool> isDim, bool showWeekday)
    {
        var spans = CalendarData.SpansIn(weekStart, 7);

        var grid = new Grid();
        for (int i = 0; i < 7; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ★★ 下方时间轴正在显示的那一周,在月历上用【次要着重色】整排标出来(用户裁定 2026-07-31)。
        //   不变式:时间轴显示的永远是【选中日所在的那一周】(见 HomeView 的双向接线),
        //   所以这里直接按 _selected 判断,不用再从时间轴取一次状态 —— 少一条会不同步的线。
        //   用次要色而不是着重色:着重色已经归"今天"和"选中日"了,三者同色就分不清谁是谁。
        if (weekStart.Date == StartOfWeek(_selected).Date)
        {
            var strip = new Border { Margin = new Thickness(0, 0.5, 0, 0.5), Opacity = 0.16 };
            strip.SetResourceReference(Border.BackgroundProperty, "AccentSecondary");
            strip.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            Grid.SetColumn(strip, 0); Grid.SetColumnSpan(strip, 7);
            Grid.SetRow(strip, 0);
            Panel.SetZIndex(strip, -1);
            grid.Children.Add(strip);
            _weekStrip = strip;      // 行数要等 RowDefinitions 建完才知道,下面补 RowSpan
        }

        // 行结构【恒定】:数字行 + 固定 SpanRowsReserved 条线行 + 圆点行。
        // 全部用绝对高度,不用 Auto —— Auto 会随内容有无而变,正是位置浮动的根源。
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(numberHeight) });
        for (int r = 0; r < SpanRowsReserved; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SpanRowHeight) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DotsRowHeight) });
        var dotsRow = grid.RowDefinitions.Count - 1;
        var totalRows = grid.RowDefinitions.Count;

        for (int i = 0; i < 7; i++)
        {
            var day = weekStart.AddDays(i);
            var dim = isDim(day);
            var isToday = day.Date == DateTime.Today;
            var isSelected = day.Date == _selected.Date;

            // ① 背景块:跨全部行,承载高亮与点击(线与圆点浮在它上面)
            var bg = new Border
            {
                Margin = new Thickness(1.5, 0.5, 1.5, 0.5),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            bg.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            if (isToday) { bg.SetResourceReference(Border.BackgroundProperty, "Accent"); bg.BorderBrush = Brushes.Transparent; }
            else if (isSelected) { bg.SetResourceReference(Border.BackgroundProperty, "BgSunken"); bg.SetResourceReference(Border.BorderBrushProperty, "BorderStrong"); }
            else { bg.Background = Brushes.Transparent; bg.BorderBrush = Brushes.Transparent; }
            if (_weekStrip is not null) { Grid.SetRowSpan(_weekStrip, totalRows); _weekStrip = null; }
            Grid.SetColumn(bg, i); Grid.SetRow(bg, 0); Grid.SetRowSpan(bg, totalRows);
            Panel.SetZIndex(bg, 0);
            var captured = day.Date;
            bg.MouseLeftButtonUp += (_, _) => OnDayClicked(captured);
            grid.Children.Add(bg);
            _dayCells[captured] = bg;

            // ② 日期数字(必要时带星期)
            // 数字底部对齐 -> 全天线紧贴在数字下方(用户裁定)
            var numStack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
            if (showWeekday)
            {
                var wk = new TextBlock { Text = day.ToString("ddd", Zh), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 10 };
                wk.SetResourceReference(TextBlock.ForegroundProperty, isToday ? "FgOnAccent" : "FgMuted");
                numStack.Children.Add(wk);
            }
            var num = new TextBlock
            {
                Text = day.Day.ToString(),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = numberHeight >= 40 ? 14.5 : 12.5,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
            };
            num.SetResourceReference(TextBlock.ForegroundProperty, isToday ? "FgOnAccent" : dim ? "FgMuted" : "FgPrimary");
            numStack.Children.Add(num);
            Grid.SetColumn(numStack, i); Grid.SetRow(numStack, 0);
            Panel.SetZIndex(numStack, 1);
            grid.Children.Add(numStack);

            // ③ 圆点:只统计【定时】日程(全天已由线表示,再点一次是重复)
            var timed = CalendarData.TimedOn(day).ToList();
            var dots = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            if (timed.Count > DotsMaxBeforeTriangle)
            {
                // 超过阈值:一个实心三角形代替一排点(用户裁定)
                var tri = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M0,0 L9,0 L4.5,6 Z"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = dim ? 0.55 : 1,
                };
                tri.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, isToday ? "FgOnAccent" : "Accent");
                dots.Children.Add(tri);
            }
            else foreach (var _ in timed)
            {
                var d = new System.Windows.Shapes.Ellipse
                {
                    Width = 3.5, Height = 3.5, Margin = new Thickness(1.2, 0, 1.2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = dim ? 0.55 : 1,
                };
                d.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, isToday ? "FgOnAccent" : "Accent");
                dots.Children.Add(d);
            }
            Grid.SetColumn(dots, i); Grid.SetRow(dots, dotsRow);
            Panel.SetZIndex(dots, 1);
            grid.Children.Add(dots);
        }

        // ④ 全天线:在数字与圆点【之间】,统一画在【同一行】。
        //    多条全天日程重叠时不分行 —— 按"哪些天被覆盖"合并成连续线段,
        //    于是每一天最多只有一条线、高度完全一致(用户裁定)。
        foreach (var (col, span, clipStart, clipEnd) in MergeSpans(spans))
        {
            var dim = isDim(weekStart.AddDays(col));
            var bar = SpanBar(clipStart, clipEnd, dim);
            Grid.SetColumn(bar, col); Grid.SetColumnSpan(bar, span); Grid.SetRow(bar, 1);
            Panel.SetZIndex(bar, 1);
            grid.Children.Add(bar);
        }

        return grid;
    }

    /// <summary>
    /// 把一周内的多条全天日程合并成【互不重叠的连续线段】。
    /// 目的:同一天有多个全天日程时只画一条线,且所有线在同一行、高度一致 ——
    /// 分行画会一上一下,也会让日期区高度随条数浮动。
    /// 合并后每段的两端各自判断是否被周界裁断(用于决定收不收圆角)。
    /// </summary>
    static List<(int Col, int Span, bool ClipStart, bool ClipEnd)> MergeSpans(
        List<(CalendarEvent Ev, int Col, int Span, bool ClipStart, bool ClipEnd)> spans)
    {
        // 先把"哪些列被覆盖"以及"该列是否续前/续后"摊平到 7 格
        var covered = new bool[7];
        var contPrev = new bool[7];
        var contNext = new bool[7];
        foreach (var s in spans)
            for (int i = 0; i < s.Span; i++)
            {
                var col = s.Col + i;
                if (col is < 0 or > 6) continue;
                covered[col] = true;
                if (i == 0 && s.ClipStart) contPrev[col] = true;
                if (i == s.Span - 1 && s.ClipEnd) contNext[col] = true;
            }

        var result = new List<(int, int, bool, bool)>();
        int c0 = 0;
        while (c0 < 7)
        {
            if (!covered[c0]) { c0++; continue; }
            var c1 = c0;
            while (c1 + 1 < 7 && covered[c1 + 1]) c1++;
            result.Add((c0, c1 - c0 + 1, contPrev[c0], contNext[c1]));
            c0 = c1 + 1;
        }
        return result;
    }

    /// <summary>
    /// 全天/跨天的线。★ 只画线、不写内容(用户裁定)—— 写标题就得给十几像素行高,
    /// 几条全天日程就把周排布顶高、把下方当日日程挤得显示不全。内容由当日浮窗 / 下方列表展示。
    /// 不可点击:它横跨多天,点它无从判断指的是哪天,且会绕过"先选日期"这条统一路径。
    /// 被区间裁断的一端不收圆角,表示"还在继续"。
    /// </summary>
    static Border SpanBar(bool clipStart, bool clipEnd, bool dim)
    {
        var bar = new Border
        {
            Height = 3,
            // 贴近日期数字底部(用户裁定):上留白几乎为 0,下方留一点与圆点分开。
            // ★ 左右内缩(用户裁定):真正的起始日左侧、结束日右侧各缩进一截,
            //   这样能看出日程从哪天开始、到哪天结束。被周界裁断的一端【不缩】——
            //   它要贯通到格边,视觉上才表示"还在继续"。
            Margin = new Thickness(clipStart ? 0 : EndInset, 0, clipEnd ? 0 : EndInset, 2),
            Opacity = dim ? 0.5 : 1,
            IsHitTestVisible = false,             // 点击穿透到背景块 -> 仍能选中当天
            CornerRadius = new CornerRadius(clipStart ? 0 : 2, clipEnd ? 0 : 2, clipEnd ? 0 : 2, clipStart ? 0 : 2),
        };
        // ★ 用【次着重色】而不是着重色:同一格里既有当日日程的点、又有这条跨天线,
        //   两者同色就分不出今天有事和这几天都在这件事里。
        //   次着重色的职责正是同一类东西的另一种,不表示状态、也不表示危险。
        bar.SetResourceReference(Border.BackgroundProperty, "AccentSecondary");
        return bar;
    }

    /// <summary>点某一天:选中它;月排布另外弹当日浮窗(周排布下方已就地显示)。</summary>
    void OnDayClicked(DateTime day)
    {
        var reopen = _mode == Mode.Month;
        SelectDay(day);   // 下方时间轴跟着聚焦到这一周
        // ★ 点到上/下月的灰日【不跳月】(用户裁定):视图不在手底下突然换月。
        // ★★ 点日期 = 【查看当天日程】,不再直接开新增抽屉(用户 2026-07-31 报的 bug):
        //   日历最常用的动作是"看看那天有什么",把它接成新建,查看反而无路可走。
        //   新建仍在 —— 当日标题右边那个「+」。
        Rebuild();

        // ★★ 月排布【弹当日浮窗】(用户裁定 2026-07-31 恢复):里面是当天日程的预览 + 一个新建入口。
        //   注意这是"预览 + 入口",不是"直接开新增抽屉" —— 后者正是之前报过的那个 bug。
        // ★ 必须在 Rebuild【之后】再取格子:重建把旧元素整批换掉了,
        //   挂在已经离开可视树的元素上,浮窗会定位到左上角或者干脆不出来。
        if (reopen && _dayCells.TryGetValue(day.Date, out var cell) && cell.IsVisible)
            ShowDayFlyout(day, cell);
    }

    /// <summary>当日浮窗:列出那天的日程(点一条 = 编辑),底部一个「新增日程」。</summary>
    void ShowDayFlyout(DateTime day, FrameworkElement anchor)
    {
        var body = new StackPanel();
        var evts = CalendarData.On(day).ToList();
        if (evts.Count == 0)
        {
            var none = new TextBlock { Text = "这一天还没有日程。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 2, 8) };
            none.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            none.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            body.Children.Add(none);
        }
        else
        {
            var list = new StackPanel();
            foreach (var ev in evts) list.Children.Add(EventRow(ev, compact: true));
            body.Children.Add(new ScrollViewer
            {
                Content = list,
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 0, 0, 6),
            }.PassThrough());
        }

        var add = CompactAdd(() => { Overlay.CloseActive(); OpenEditor(day, null); });
        add.Margin = new Thickness(0);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(add);

        Flyout.Show(anchor, day.ToString("M月d日 dddd", Zh), body, width: 240);
    }

    // ---------------------------------------------------------------- 周排布:下方就地列出
    void RebuildDayList()
    {
        _dayList.Children.Clear();

        // 打开即显示【今天】的日程 —— 之前那道"点了才显示"的门是我误把 bug 报告当成需求加的,
        // 真正的成因是示例数据晚于窗口构建(见 App.OnStartup 的播种顺序)。
        _addButton.Visibility = Visibility.Visible;

        _dayTitle.Text = _selected.ToString("M月d日 dddd", Zh);

        // ★ 月排布下【不再列文字版当日日程】(2026-07-31 合并时间轴之后):
        //   同一批日程下面的时间轴已经画出来了,再列一遍既重复、又把高度吃掉。
        //   标题与「+ 新增日程」保留 —— 它们是入口,不是重复内容。
        if (_mode == Mode.Month) return;

        var evts = CalendarData.On(_selected).ToList();
        if (evts.Count == 0)
        {
            var none = new TextBlock { Text = "无日程 · " + Strings.Get("calendar.not_connected"), TextWrapping = TextWrapping.Wrap };
            none.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            none.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            _dayList.Children.Add(none);
        }
        else foreach (var ev in evts) _dayList.Children.Add(EventRow(ev, compact: true));
    }

    /// <summary>渲染诊断用:一条日程的纯视觉(无点击),便于离屏截图核对 AI 星标。</summary>
    public static Border EventRowPreview(CalendarEvent ev)
    {
        var hit = new Border { Child = EventVisual(ev, compact: true), Padding = new Thickness(5, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left };
        hit.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        return hit;
    }

    /// <summary>一条日程行的视觉(时间 + 标题 + AI 星标)。EventRow 与渲染诊断共用,避免走样。</summary>
    static StackPanel EventVisual(CalendarEvent ev, bool compact)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        // 全天日程显示「全天」,而不是 00:00 —— 全天没有起始时刻,显示 00:00 是把
        // 内部表示当成了用户可见语义(用户裁定)。跨天的再补上天数范围。
        var timeText = ev.AllDay
            ? (ev.IsMultiDay ? $"全天 · {ev.DayCount} 天" : "全天")
            : ev.Start.ToString("HH:mm");
        var time = new TextBlock
        {
            Text = timeText,
            Width = ev.AllDay && ev.IsMultiDay ? 68 : 42,
            VerticalAlignment = VerticalAlignment.Center,
        };
        time.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        time.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        row.Children.Add(time);

        var t = new TextBlock
        {
            Text = ev.Title, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = compact ? 190 : 220,   // 过长才截断,不占满整行
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        t.SetResourceReference(TextBlock.FontSizeProperty, compact ? "FontCaption" : "FontBody");
        row.Children.Add(t);

        // ★ AI 建立的日程带小星标(与待办同一套标记),一眼区分手动/AI 建立(用户裁定)
        if (ev.CreatedByAi)
        {
            var ai = Theme.Icons.Make(Theme.IconName.Ai, 12, "Accent");
            ai.Margin = new Thickness(6, 0, 0, 0);
            ai.VerticalAlignment = VerticalAlignment.Center;
            ai.ToolTip = "由 AI 建立";
            row.Children.Add(ai);
        }
        return row;
    }

    /// <summary>一条日程。点它 = 编辑这一条(用户裁定)。</summary>
    Border EventRow(CalendarEvent ev, bool compact)
    {
        // ★ 命中区【贴合内容】—— 之前用 DockPanel 填满整行,右侧一大片空白也成了按钮,
        //   鼠标划过老远就高亮,很不舒服(用户反馈)。改成横向堆叠 + 左对齐。
        var hit = new Border
        {
            Child = EventVisual(ev, compact), Padding = new Thickness(5, 3, 8, 3),
            Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,   // 宽度贴合内容
        };
        hit.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        hit.MouseEnter += (_, _) => hit.SetResourceReference(Border.BackgroundProperty, "BgHover");
        hit.MouseLeave += (_, _) => hit.Background = Brushes.Transparent;
        hit.MouseLeftButtonUp += (_, _) => { Overlay.CloseActive(); OpenEditor(ev.Start.Date, ev); };
        return hit;
    }

    // ---------------------------------------------------------------- 年 / 月 选择浮窗
    // 点左上角年月标签打开。翻页键适合走一两格,跨年跨月就该直接挑。
    void OpenMonthPicker(FrameworkElement anchor)
    {
        var body = new StackPanel();
        var year = _anchor.Year;

        var yearRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
        var yearLabel = new TextBlock { Text = year + " 年", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        yearLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        var monthGrid = new UniformGrid { Columns = 4 };

        void FillMonths()
        {
            yearLabel.Text = year + " 年";
            monthGrid.Children.Clear();
            for (int m = 1; m <= 12; m++)
            {
                var isCurrent = m == _anchor.Month && year == _anchor.Year;
                var isThisMonth = m == DateTime.Today.Month && year == DateTime.Today.Year;
                var t = new TextBlock { Text = m + " 月", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                t.SetResourceReference(TextBlock.ForegroundProperty, isCurrent ? "FgOnAccent" : isThisMonth ? "Accent" : "FgPrimary");
                var cell = new Border { Child = t, Height = 34, Margin = new Thickness(2), Cursor = System.Windows.Input.Cursors.Hand };
                cell.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
                if (isCurrent) cell.SetResourceReference(Border.BackgroundProperty, "Accent");
                else cell.Background = Brushes.Transparent;
                var capturedMonth = m; var capturedYear = year;
                cell.MouseEnter += (_, _) => { if (!isCurrent) cell.SetResourceReference(Border.BackgroundProperty, "BgHover"); };
                cell.MouseLeave += (_, _) => { if (!isCurrent) cell.Background = Brushes.Transparent; };
                cell.MouseLeftButtonUp += (_, _) =>
                {
                    // 选定年月:月排布跳到该月;周排布跳到该月第一周
                    var target = new DateTime(capturedYear, capturedMonth, 1);
                    _anchor = _mode == Mode.Month ? target : StartOfWeek(target);
                    SelectDay(target);
                    Overlay.CloseActive();
                    Rebuild();
                };
                monthGrid.Children.Add(cell);
            }
        }

        var prevY = Btn("‹", () => { year--; FillMonths(); });
        var nextY = Btn("›", () => { year++; FillMonths(); });
        prevY.Margin = new Thickness(0); DockPanel.SetDock(prevY, Dock.Left); yearRow.Children.Add(prevY);
        DockPanel.SetDock(nextY, Dock.Right); yearRow.Children.Add(nextY);
        yearRow.Children.Add(yearLabel);

        FillMonths();
        body.Children.Add(yearRow);
        body.Children.Add(monthGrid);

        Flyout.Show(anchor, "选择年份 / 月份", body, width: 260);
    }

    /// <summary>existing 为 null = 新增;否则 = 编辑那一条。</summary>
    /// <summary>
    /// 打开日程编辑。★ 用【右侧全高抽屉】而不是浮窗(用户裁定):字段有九项,
    /// 浮窗放不下会变成套娃滚动;抽屉一页能显示完。
    /// </summary>
    void OpenEditor(DateTime day, CalendarEvent? existing, TimeSpan? startAt = null)
    {
        var title = day.ToString("M月 d日", Zh) + (existing is null ? " · 新增日程" : " · 编辑日程");
        var body = CalendarEditor.Build(day, existing, onSaved: () => { Overlay.CloseActive(); Rebuild(); }, presetStart: startAt);
        (Application.Current.MainWindow as MainWindow)?.OpenSideDrawer(title, body, Theme.IconName.Calendar);
    }
}
