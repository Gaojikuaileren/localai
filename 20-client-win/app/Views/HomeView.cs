// P3c -- 主页(= 今天)。用户裁定的布局:
//   ┌──────────────┬──────────────┬────────┐
//   │ 简报          │ 待办与家务    │        │
//   ├──────────────┴──────────────┤ 日历    │
//   │ 天气三城(各带当地时间)       │        │
//   ├─────────────────────────────┴────────┤
//   │ 正在进行的项目(田字格,可滚动)        │
//   └──────────────────────────────────────┘
//
// 关键裁定:
//   · 简报与待办【并列】;
//   · 正在进行的项目占【下方整个板块】,过多可滚动;
//   · 项目不是一条条列表,而是【田字格长方块】,点击直达对应工作空间的对应项目;
//   · 时间【并入天气板块】—— 每个地区一个时间,删掉多出来的纽约;
//   · 板块随窗口缩放,一页显示完。
//   · 正在运行的任务 -> 外壳底部横条 + 全局抽屉,不在主页占板块。
//
// 数据未接入(天气等出境白名单 / 日历等 Apple 接入):曲线与逐小时用**无数据基线**渲染,
// 明确可见布局但绝不画假数字冒充实时(设计 §4.1 / 状态矩阵 §8)。

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
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    App TheApp => (App)Application.Current;

    public HomeView()
    {
        _greeting.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        var root = new Grid { Margin = new Thickness(24, 16, 24, 18) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                          // 问候
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                          // 简报 | 待办
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.15, GridUnitType.Star), MinHeight = 210 }); // 天气(含时间)
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 150 });    // 项目田字格

        // ① 问候
        _greeting.Margin = new Thickness(2, 0, 0, 10);
        Grid.SetRow(_greeting, 0); Grid.SetColumn(_greeting, 0);
        root.Children.Add(_greeting);

        // ② 简报 | 待办(并列)
        var pair = TwoUp(
            Ui.Panel(Strings.Get("today.briefing"),
                Ui.Stack(Ui.Body("今天还没有简报。", muted: true),
                         Ui.Caption("每天第一次打开时生成;每人每天只主动展示一次。个人简报只给本人。")),
                IconName.Chat, new Thickness(0, 0, 8, 12)),
            Ui.Panel("待办与家务",
                Ui.Stack(Ui.Body("还没有待办。", muted: true),
                         Ui.Caption("「提醒我…」建个人待办;「提醒我们…」建家庭事务。")),
                IconName.Member, new Thickness(8, 0, 0, 12)));
        pair.Margin = new Thickness(0, 0, 16, 0);
        Grid.SetRow(pair, 1); Grid.SetColumn(pair, 0);
        root.Children.Add(pair);

        // ③ 天气三城(每城自带当地时间)
        var weather = new UniformGrid { Rows = 1, Columns = Cities.Length, Margin = new Thickness(0, 0, 16, 12) };
        for (int i = 0; i < Cities.Length; i++) weather.Children.Add(CityCard(i));
        Grid.SetRow(weather, 2); Grid.SetColumn(weather, 0);
        root.Children.Add(weather);

        // ④ 正在进行的项目:占满下方整宽,田字格,可滚动
        var projects = Ui.Panel(Strings.Get("project.resume"), ProjectGrid(), IconName.Tasks, new Thickness(0, 0, 16, 0));
        Grid.SetRow(projects, 3); Grid.SetColumn(projects, 0);
        root.Children.Add(projects);

        // 右栏:日历(跨全部行)
        var cal = Ui.Panel("日历", new CalendarPanel(CalendarPanel.Mode.TwoWeeks), IconName.Calendar, new Thickness(0));
        cal.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetRow(cal, 0); Grid.SetRowSpan(cal, 4); Grid.SetColumn(cal, 1);
        root.Children.Add(cal);

        Content = root;

        UpdateClocks();
        _timer.Tick += (_, _) => UpdateClocks();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();   // 视图切走就停表,别在后台空转
    }

    // ---------------------------------------------------------------- 城市卡:天气 + 当地时间
    Border CityCard(int i)
    {
        var (city, tag, _) = Cities[i];

        // 当地时间贴在卡片右上 —— 时间与天气归到同一个地区,不再单独占一条(用户裁定)
        _cityTime[i] = new TextBlock { Text = "—", FontSize = 16, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Right };
        _cityTime[i].SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        _cityMeta[i] = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
        _cityMeta[i].SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _cityMeta[i].SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var timeCol = Ui.Stack(_cityTime[i], _cityMeta[i]);
        timeCol.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(timeCol, Dock.Right);

        var temp = new TextBlock { Text = "—°", FontSize = 34, FontWeight = FontWeights.Light, VerticalAlignment = VerticalAlignment.Center };
        temp.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");

        var topRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
        topRow.Children.Add(timeCol);
        topRow.Children.Add(temp);

        var st = new TextBlock { Text = "天气未接入" };
        st.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        st.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var hl = new TextBlock { Text = "最高 —  最低 —   降水 —", Margin = new Thickness(0, 1, 0, 0) };
        hl.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        hl.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        // 今日气温曲线(无数据 -> 平直虚线,不画假起伏)
        var curve = new Grid { MinHeight = 44, Margin = new Thickness(0, 10, 0, 0) };
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

        // 逐小时天气状态
        var hourly = new UniformGrid { Rows = 1, Columns = 6, Margin = new Thickness(0, 8, 0, 0) };
        var h0 = DateTime.Now.Hour;
        for (int k = 0; k < 6; k++)
        {
            var hr = new TextBlock { Text = $"{(h0 + k * 3) % 24:00}", HorizontalAlignment = HorizontalAlignment.Center };
            hr.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            hr.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            var ic = new TextBlock { Text = "—", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 1) };
            ic.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            var tp = new TextBlock { Text = "—°", HorizontalAlignment = HorizontalAlignment.Center };
            tp.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            tp.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            hourly.Children.Add(Ui.Stack(hr, ic, tp));
        }

        var inner = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(topRow, Dock.Top); inner.Children.Add(topRow);
        DockPanel.SetDock(st, Dock.Top); inner.Children.Add(st);
        DockPanel.SetDock(hl, Dock.Top); inner.Children.Add(hl);
        DockPanel.SetDock(hourly, Dock.Bottom); inner.Children.Add(hourly);
        inner.Children.Add(curve);

        var title = string.IsNullOrEmpty(tag) ? city : $"{city} · {tag}";
        return Ui.Panel(title, inner, IconName.Weather, new Thickness(0, 0, 12, 0));
    }

    // ---------------------------------------------------------------- 项目田字格
    UIElement ProjectGrid()
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };

        void Rebuild()
        {
            wrap.Children.Clear();
            var items = TheApp.Projects.Recent().ToList();
            if (items.Count == 0)
            {
                wrap.Children.Add(Ui.Stack(
                    Ui.Body("还没有正在进行的项目。", muted: true),
                    Ui.Caption("接入后这里以方块列出可恢复的工作(对话 / 资产 / 课件草稿),点方块直达那个项目。只列你自己的 + 家庭的。")));
                return;
            }
            foreach (var p in items) wrap.Children.Add(ProjectTile(p));
        }

        Rebuild();
        // ★ 订阅必须配对退订:主页每次导航/换语言都会新建,不退订的话旧实例永远挂在事件上
        //   (与 Icons 那处同一类泄漏)。
        TheApp.Projects.Changed += Rebuild;
        Unloaded += (_, _) => TheApp.Projects.Changed -= Rebuild;

        return new ScrollViewer
        {
            Content = wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,   // 项目过多可滚动
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
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
        icon.Margin = new Thickness(0, 0, 0, 8);

        var title = new TextBlock { Text = p.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 38 };
        title.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        var sub = new TextBlock { Text = p.Subtitle, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        sub.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        // 可见范围标识(设计 §4.3:每项显示范围;§7:该标识皮肤禁改)
        var scopeText = p.Scope switch
        {
            ProjectScope.Family => Strings.Get("visibility.family"),
            ProjectScope.Personal => Strings.Get("visibility.personal"),
            _ => Strings.Get("visibility.only_me"),
        };
        var scopeKey = p.Scope switch
        {
            ProjectScope.Family => "ScopeFamily",
            ProjectScope.Personal => "ScopePersonal",
            _ => "ScopeOnlyMe",
        };
        var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, scopeKey);
        var scopeLabel = new TextBlock { Text = scopeText, VerticalAlignment = VerticalAlignment.Center };
        scopeLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        scopeLabel.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var scopeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        scopeRow.Children.Add(dot); scopeRow.Children.Add(scopeLabel);

        var body = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(icon, Dock.Top); body.Children.Add(icon);
        DockPanel.SetDock(title, Dock.Top); body.Children.Add(title);
        DockPanel.SetDock(sub, Dock.Top); body.Children.Add(sub);
        DockPanel.SetDock(scopeRow, Dock.Bottom); body.Children.Add(scopeRow);

        var tile = new Border
        {
            Child = body,
            Width = 190, Height = 132,
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
        // 点方块 -> 直达对应工作空间的对应项目(深链,不用先进空间再找)
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
