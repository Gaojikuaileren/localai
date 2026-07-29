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
using System.Windows.Threading;
using LocalAI.Client.I18n;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class HomeView : UserControl
{
    static readonly (string City, string Tag, string Tz)[] Cities =
    {
        ("科隆", "家", "W. Europe Standard Time"),
        ("武汉", "",   "China Standard Time"),
        ("札幌", "",   "Tokyo Standard Time"),
    };

    /// <summary>天气板块固定高度:标题 + 温度行 + 状态两行 + 曲线 + 逐小时,只占所需。</summary>
    const double WeatherHeight = 208;

    readonly TextBlock _greeting = new() { FontWeight = FontWeights.SemiBold, FontSize = 22 };
    readonly TextBlock[] _cityTime = new TextBlock[Cities.Length];
    readonly TextBlock[] _cityMeta = new TextBlock[Cities.Length];
    readonly UniformGrid[] _cityHourly = new UniformGrid[Cities.Length];
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    readonly Grid _root = new();
    readonly ColumnDefinition _todoColumn = new();
    readonly UniformGrid _tiles = new();
    readonly Border _todoPanel;
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

        _todoPanel = Ui.Panel("待办与家务",
            new ScrollViewer
            {
                Content = Ui.Stack(Ui.Body("还没有待办。", muted: true),
                                   Ui.Caption("「提醒我…」建个人待办;「提醒我们…」建家庭事务。")),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            }.PassThrough(),
            IconName.Member, new Thickness(0, 0, 0, 12));
        // 与日历等高 —— 两块并排,高度锁死才不会一高一矮
        // 与日历面板等高:日历面板 = 日历本体 + Ui.Panel 的标题行与内边距(约 62),
        // 之前按 +46 算,导致两块并排时高度差了十几像素。
        _todoPanel.Height = CalendarView.PanelHeight + 62;
        Grid.SetRow(_todoPanel, 1); Grid.SetColumn(_todoPanel, 1);
        _root.Children.Add(_todoPanel);

        // ③ 天气三城:固定高度,只占所需
        var weather = new UniformGrid { Rows = 1, Columns = Cities.Length, Height = WeatherHeight, Margin = new Thickness(0, 0, 0, 12) };
        for (int i = 0; i < Cities.Length; i++) weather.Children.Add(CityCard(i));
        Grid.SetRow(weather, 2); Grid.SetColumnSpan(weather, 2);
        _root.Children.Add(weather);

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
        var cardW = (w - (Cities.Length - 1) * 12) / Cities.Length - 32;
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
        return Ui.Panel(title, inner, IconName.Weather, new Thickness(0, 0, i < Cities.Length - 1 ? 12 : 0, 0));
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
