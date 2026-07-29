// P3c -- 主页(= 今天)。布局:
//   ┌──────────────┬──────────────┬────────┐
//   │ 简报          │ 待办与家务    │        │
//   ├──────────────┴──────────────┤ 日历    │
//   │ 天气三城(各带当地时间)       │        │
//   ├─────────────────────────────┴────────┤
//   │ 正在进行的项目(方块平分整宽,可滚动) │
//   └──────────────────────────────────────┘
//
// ★ 响应式(用户提出的三个畸变风险,判据全部收敛在 Layout.cs 的纯函数里,可自动回归):
//   ① 项目方块用 UniformGrid【平分】可用宽度 —— 不是固定宽度靠 WrapPanel 排,
//      那样右侧必然剩一条空白。列数 = 可用宽度 / 理想宽度,随窗口变化实时重算。
//   ② 全屏与最小窗口都不畸变:按密度取舍 —— 曲线高度、逐小时格数、方块高度、日历栏宽度
//      逐级下调;极小窗口宁可**不显示**曲线/逐小时,也不显示"半截"的曲线。
//   ③ 简报/待办限高 + 内部滚动:一侧内容再长也不会把另一侧撑出大片留白,
//      更不会把下面的天气与项目挤变形。
//
// 数据未接入(天气等出境白名单 / 日历等 Apple 接入):曲线与逐小时用无数据基线,
// 绝不画假数字冒充实时(设计 §4.1 / 状态矩阵 §8)。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using LocalAI.Client.I18n;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class HomeView : UserControl
{
    // 三城 = 天气 + 各自当地时间(纽约已按裁定删除)
    static readonly (string City, string Tag, string Tz)[] Cities =
    {
        ("科隆", "家", "W. Europe Standard Time"),
        ("武汉", "",   "China Standard Time"),
        ("札幌", "",   "Tokyo Standard Time"),
    };

    readonly TextBlock _greeting = new() { FontWeight = FontWeights.SemiBold, FontSize = 23 };
    readonly TextBlock[] _cityTime = new TextBlock[Cities.Length];
    readonly TextBlock[] _cityMeta = new TextBlock[Cities.Length];
    readonly Grid[] _cityCurve = new Grid[Cities.Length];
    readonly UniformGrid[] _cityHourly = new UniformGrid[Cities.Length];
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    readonly Grid _root = new();
    readonly ColumnDefinition _calColumn = new();
    readonly UniformGrid _tiles = new();
    readonly Border _calendarPanel;
    readonly Border _briefPanel, _todoPanel;
    readonly ScrollViewer _pageScroll = new();


    App TheApp => (App)Application.Current;

    public HomeView()
    {
        _greeting.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        _root.Margin = new Thickness(24, 16, 24, 18);
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _calColumn.Width = new GridLength(Layout.CalendarWidth(1200));
        _root.ColumnDefinitions.Add(_calColumn);
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                          // 问候
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                          // 简报 | 待办
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.15, GridUnitType.Star), MinHeight = 150 });  // 天气
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });     // 项目

        // ① 问候
        _greeting.Margin = new Thickness(2, 0, 0, 10);
        Grid.SetRow(_greeting, 0); Grid.SetColumn(_greeting, 0);
        _root.Children.Add(_greeting);

        // ② 简报 | 待办 —— 限高 + 内部滚动(用户担心的"一长一短"在这里被挡住)
        _briefPanel = Ui.Panel(Strings.Get("today.briefing"),
            Scrollable(Ui.Stack(Ui.Body("今天还没有简报。", muted: true),
                                Ui.Caption("每天第一次打开时生成;每人每天只主动展示一次。个人简报只给本人。"))),
            IconName.Chat, new Thickness(0, 0, 8, 12));
        _todoPanel = Ui.Panel("待办与家务",
            Scrollable(Ui.Stack(Ui.Body("还没有待办。", muted: true),
                                Ui.Caption("「提醒我…」建个人待办;「提醒我们…」建家庭事务。"))),
            IconName.Member, new Thickness(8, 0, 0, 12));
        var pair = TwoUp(_briefPanel, _todoPanel);
        pair.Margin = new Thickness(0, 0, 16, 0);
        Grid.SetRow(pair, 1); Grid.SetColumn(pair, 0);
        _root.Children.Add(pair);

        // ③ 天气三城
        var weather = new UniformGrid { Rows = 1, Columns = Cities.Length, Margin = new Thickness(0, 0, 16, 12) };
        for (int i = 0; i < Cities.Length; i++) weather.Children.Add(CityCard(i));
        Grid.SetRow(weather, 2); Grid.SetColumn(weather, 0);
        _root.Children.Add(weather);

        // ④ 项目方块:UniformGrid 平分整宽
        _tiles.Columns = 4;
        var projects = Ui.Panel(Strings.Get("project.resume"),
            new ScrollViewer
            {
                Content = _tiles,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            IconName.Tasks, new Thickness(0, 0, 16, 0));
        Grid.SetRow(projects, 3); Grid.SetColumn(projects, 0);
        _root.Children.Add(projects);

        // 右栏:日历
        _calendarPanel = Ui.Panel("日历", new CalendarPanel(CalendarPanel.Mode.TwoWeeks), IconName.Calendar, new Thickness(0));
        _calendarPanel.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(_calendarPanel, 0); Grid.SetRowSpan(_calendarPanel, 4); Grid.SetColumn(_calendarPanel, 1);
        _root.Children.Add(_calendarPanel);

        // 兜底逃生口:窗口小到连 Tight 都放不下时,允许整页纵向滚动 —— 宁可滚动也不裁切内容。
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

    static ScrollViewer Scrollable(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    // ---------------------------------------------------------------- 随尺寸重排
    // 丝滑要点:① 连续量直接跟随尺寸(不分档);② 离散量带迟滞;
    //          ③ 拖动窗口时【节流】—— 每一像素都重排会卡,尤其逐小时格数变化要重建子元素。
    readonly DispatcherTimer _resizeThrottle = new() { Interval = TimeSpan.FromMilliseconds(60) };
    int _cols, _slots;

    void ScheduleRelayout()
    {
        _resizeThrottle.Stop();
        _resizeThrottle.Start();   // 拖动停下来 60ms 后才真正重排
        RelayoutContinuous();      // 连续量每帧都跟,视觉上完全跟手
    }

    /// <summary>连续量:每次尺寸变化都更新,平滑无跳变(不涉及重建子元素,开销极小)。</summary>
    void RelayoutContinuous()
    {
        var w = ActualWidth; var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var calW = Layout.CalendarWidth(w);
        _calColumn.Width = new GridLength(calW);
        _calendarPanel.Visibility = Visibility.Visible;

        var panelMax = Layout.PanelMaxHeight(h);
        _briefPanel.MaxHeight = panelMax;
        _todoPanel.MaxHeight = panelMax;

        var curveH = Layout.CurveHeight(h);
        var tileH = Layout.TileHeight(h);
        for (int i = 0; i < Cities.Length; i++) _cityCurve[i].Height = curveH;
        foreach (var c in _tiles.Children) if (c is FrameworkElement fe) fe.Height = tileH;
    }

    /// <summary>离散量:带迟滞,且只在拖动停下后执行(涉及重建子元素)。</summary>
    void RelayoutDiscrete()
    {
        var w = ActualWidth; var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var contentW = Math.Max(0, w - Layout.CalendarWidth(w) - 64);

        var cols = Layout.ProjectColumns(contentW, _cols);
        if (cols != _cols) { _cols = cols; _tiles.Columns = cols; }

        var cardW = contentW / Cities.Length;
        var slots = Layout.HourlySlots(cardW, _slots);
        if (slots != _slots)
        {
            _slots = slots;
            for (int i = 0; i < Cities.Length; i++) SetHourly(_cityHourly[i], slots);
        }
    }

    // ---------------------------------------------------------------- 城市卡
    Border CityCard(int i)
    {
        var (city, tag, _) = Cities[i];

        _cityTime[i] = new TextBlock { Text = "—", FontSize = 16, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Right };
        _cityTime[i].SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        _cityMeta[i] = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
        _cityMeta[i].SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _cityMeta[i].SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var timeCol = Ui.Stack(_cityTime[i], _cityMeta[i]);
        timeCol.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(timeCol, Dock.Right);

        var temp = new TextBlock { Text = "—°", FontSize = 32, FontWeight = FontWeights.Light, VerticalAlignment = VerticalAlignment.Center };
        temp.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");

        var topRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 3) };
        topRow.Children.Add(timeCol);
        topRow.Children.Add(temp);

        var st = new TextBlock { Text = "天气未接入", TextTrimming = TextTrimming.CharacterEllipsis };
        st.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        st.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var hl = new TextBlock { Text = "最高 —  最低 —  降水 —", TextTrimming = TextTrimming.CharacterEllipsis };
        hl.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        hl.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        // 气温曲线(高度由 Relayout 按密度设定;0 = 不显示,不显示半截)
        _cityCurve[i] = new Grid { Height = Layout.CurveHeight(800), Margin = new Thickness(0, 8, 0, 0), ClipToBounds = true };
        var baseline = new System.Windows.Shapes.Path
        {
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 4 },
            Stretch = Stretch.Fill,
            Data = Geometry.Parse("M0,10 L100,10"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        baseline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Border");
        _cityCurve[i].Children.Add(baseline);
        var noData = new TextBlock { Text = "今日气温曲线待接入", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        noData.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        noData.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        _cityCurve[i].Children.Add(noData);

        _cityHourly[i] = new UniformGrid { Rows = 1, Margin = new Thickness(0, 6, 0, 0) };
        SetHourly(_cityHourly[i], 6);

        var inner = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(topRow, Dock.Top); inner.Children.Add(topRow);
        DockPanel.SetDock(st, Dock.Top); inner.Children.Add(st);
        DockPanel.SetDock(hl, Dock.Top); inner.Children.Add(hl);
        DockPanel.SetDock(_cityHourly[i], Dock.Bottom); inner.Children.Add(_cityHourly[i]);
        inner.Children.Add(_cityCurve[i]);

        var title = string.IsNullOrEmpty(tag) ? city : $"{city} · {tag}";
        return Ui.Panel(title, inner, IconName.Weather, new Thickness(0, 0, 12, 0));
    }

    static void SetHourly(UniformGrid grid, int slots)
    {
        if (grid.Columns == slots && grid.Children.Count == slots) return;   // 无变化则不重建
        grid.Columns = slots;
        grid.Children.Clear();
        var h0 = DateTime.Now.Hour;
        var step = Math.Max(1, 18 / Math.Max(1, slots));
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
        _cols = 0;   // 重建后重新协商列数
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
            Height = Layout.TileHeight(800),
            // ★ 不设 Width:由 UniformGrid 平分可用宽度,方块自动拉伸填满,右侧不留空白
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

    static Grid TwoUp(UIElement left, UIElement right)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn((FrameworkElement)left, 0);
        Grid.SetColumn((FrameworkElement)right, 1);
        g.Children.Add(left); g.Children.Add(right);
        return g;
    }

    void UpdateClocks()
    {
        var hour = DateTime.Now.Hour;
        _greeting.Text = hour < 5 ? "夜深了" : hour < 11 ? "早上好" : hour < 14 ? "中午好" : hour < 18 ? "下午好" : "晚上好";

        var localOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        for (int i = 0; i < Cities.Length; i++)
        {
            try
            {
                var z = TimeZoneInfo.FindSystemTimeZoneById(Cities[i].Tz);
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
