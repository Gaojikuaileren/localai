// P3c -- 主页(= 今天)。用户裁定的布局:
//   · 板块跟随窗口大小自动缩放,【一页显示完】,不滚动(星号行/列按比例分配剩余空间)。
//   · 日历放【右上角】;因此顶栏的全局日历按钮在主页隐藏(见 MainWindow.Navigate)。
//   · 天气是重头戏:每城要有【一天气温变化曲线】+【逐小时天气状态】。
//   · 时间压成【窄条】—— 之前占比过大。四地仍等位(§4.1:纽约无天气,分层后它在时间条里平等)。
//   · 正在运行的任务 -> 外壳底部横条 + 全局抽屉,不在主页占板块。
//
// 数据仍未接入(等出境白名单):曲线与逐小时用**无数据基线**渲染 —— 虚线 + "—",
// 明确可见布局但绝不画出假数字冒充实时(设计 §4.1 / 状态矩阵 §8)。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LocalAI.Client.I18n;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class HomeView : UserControl
{
    static readonly (string City, string Tag)[] WeatherCities = { ("科隆", "家"), ("武汉", ""), ("札幌", "") };

    static readonly (string City, string Tz)[] Clocks =
    {
        ("科隆", "W. Europe Standard Time"),
        ("武汉", "China Standard Time"),
        ("札幌", "Tokyo Standard Time"),
        ("纽约", "Eastern Standard Time"),
    };

    readonly TextBlock _greeting = new() { FontWeight = FontWeights.SemiBold, FontSize = 24 };
    readonly TextBlock[] _time = new TextBlock[Clocks.Length];
    readonly TextBlock[] _meta = new TextBlock[Clocks.Length];
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public HomeView()
    {
        _greeting.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        // 一页布局:两列(主区 * / 日历栏 Auto),四行。行高用星号 -> 随窗口高度按比例伸缩。
        var root = new Grid { Margin = new Thickness(24, 18, 24, 18) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                    // 问候
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 200 });  // 天气(主角)
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                    // 时间(窄条)
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.9, GridUnitType.Star), MinHeight = 130 }); // 项目 / 待办

        // ① 问候 + 今日简报(简报是独立板块,与其它并列板块同格式)
        var greetWrap = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        _greeting.Margin = new Thickness(2, 0, 0, 10);
        greetWrap.Children.Add(_greeting);
        greetWrap.Children.Add(Ui.Panel(Strings.Get("today.briefing"),
            Ui.Stack(Ui.Body("今天还没有简报。", muted: true),
                     Ui.Caption("接入后,应用每天第一次打开时生成个人简报(每人每天只主动展示一次;个人简报只给本人,家庭简报只含家庭范围内容)。")),
            IconName.Chat, new Thickness(0, 0, 0, 12)));
        Grid.SetRow(greetWrap, 0); Grid.SetColumn(greetWrap, 0);
        root.Children.Add(greetWrap);

        // ② 天气三城(主角:曲线 + 逐小时)
        var weather = new UniformGrid { Rows = 1, Columns = WeatherCities.Length, Margin = new Thickness(0, 0, 16, 12) };
        foreach (var (c, tag) in WeatherCities) weather.Children.Add(WeatherCard(c, tag));
        Grid.SetRow(weather, 1); Grid.SetColumn(weather, 0);
        root.Children.Add(weather);

        // ③ 时间窄条
        var time = TimeStrip();
        Grid.SetRow(time, 2); Grid.SetColumn(time, 0);
        root.Children.Add(time);

        // ④ 项目 / 待办
        var bottom = new Grid { Margin = new Thickness(0, 12, 16, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // 并列板块一律走统一的 Ui.Panel(同标题栏高度、同内边距、同圆角)——用户裁定:同格式同大小
        var proj = Ui.Panel(Strings.Get("project.resume"),
            Ui.Stack(Ui.Body("还没有正在进行的项目。", muted: true),
                     Ui.Caption("接入后跨空间列出可恢复的工作(对话 / 资产 / 课件草稿),点开直达。只列你自己的 + 家庭的。")),
            IconName.Tasks, new Thickness(0, 0, 8, 0));
        var todo = Ui.Panel("待办与家务",
            Ui.Stack(Ui.Body("还没有待办。", muted: true),
                     Ui.Caption("「提醒我…」建个人待办;「提醒我们…」建家庭事务;给对方派活自动标负责人。")),
            IconName.Member, new Thickness(8, 0, 0, 0));
        Grid.SetColumn(proj, 0); Grid.SetColumn(todo, 1);
        bottom.Children.Add(proj); bottom.Children.Add(todo);
        Grid.SetRow(bottom, 3); Grid.SetColumn(bottom, 0);
        root.Children.Add(bottom);

        // 右上角:日历(跨全部行,贴右)
        var cal = Ui.Panel("日历", new CalendarPanel(CalendarPanel.Mode.TwoWeeks), IconName.Calendar, new Thickness(0));
        cal.VerticalAlignment = VerticalAlignment.Stretch;   // 占满右栏高度 -> 两周详情放得下
        Grid.SetRow(cal, 0); Grid.SetRowSpan(cal, 4); Grid.SetColumn(cal, 1);
        root.Children.Add(cal);

        Content = root;

        UpdateClocks();
        _timer.Tick += (_, _) => UpdateClocks();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    // ---------------------------------------------------------------- 天气卡
    Border WeatherCard(string city, string tag)
    {
        // 当前:大温度 + 状态 + 最高最低/降水
        var now = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 8, 0, 0) };
        var temp = new TextBlock { Text = "—°", FontSize = 40, FontWeight = FontWeights.Light, VerticalAlignment = VerticalAlignment.Center };
        temp.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        DockPanel.SetDock(temp, Dock.Left);
        now.Children.Add(temp);

        var side = new StackPanel { Margin = new Thickness(12, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var st = new TextBlock { Text = "未接入" };
        st.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        st.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var hl = new TextBlock { Text = "最高 —  最低 —", Margin = new Thickness(0, 2, 0, 0) };
        hl.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        hl.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var rain = new TextBlock { Text = "降水 —" };
        rain.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        rain.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        side.Children.Add(st); side.Children.Add(hl); side.Children.Add(rain);
        now.Children.Add(side);

        // 一天气温变化曲线(数据未接入 -> 虚线基线 + 无刻度值)
        var curveLabel = new TextBlock { Text = "今日气温", Margin = new Thickness(0, 12, 0, 4) };
        curveLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        curveLabel.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var curve = new Grid { MinHeight = 54 };   // 高度随卡片伸缩(星号行给的空间)
        var baseline = new System.Windows.Shapes.Path   // 全限定:与 System.IO.Path 撞名
        {
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 4 },
            Stretch = Stretch.Fill,
            Data = Geometry.Parse("M0,10 L100,10"),   // 无数据 = 平直虚线,不画假起伏
            VerticalAlignment = VerticalAlignment.Center,
        };
        baseline.SetResourceReference(Shape.StrokeProperty, "Border");
        curve.Children.Add(baseline);
        var noData = new TextBlock { Text = "曲线待接入", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        noData.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        noData.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        curve.Children.Add(noData);

        // 逐小时天气状态
        var hourly = new UniformGrid { Rows = 1, Columns = 6, Margin = new Thickness(0, 10, 0, 0) };
        var h0 = DateTime.Now.Hour;
        for (int i = 0; i < 6; i++)
        {
            var hr = new TextBlock { Text = $"{(h0 + i * 3) % 24:00}时", HorizontalAlignment = HorizontalAlignment.Center };
            hr.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            hr.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            var ic = new TextBlock { Text = "—", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2), FontSize = 13 };
            ic.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            var tp = new TextBlock { Text = "—°", HorizontalAlignment = HorizontalAlignment.Center };
            tp.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            tp.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            hourly.Children.Add(Ui.Stack(hr, ic, tp));
        }

        var inner = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(now, Dock.Top); inner.Children.Add(now);
        DockPanel.SetDock(hourly, Dock.Bottom); inner.Children.Add(hourly);
        DockPanel.SetDock(curveLabel, Dock.Top); inner.Children.Add(curveLabel);
        inner.Children.Add(curve);   // 填满剩余 -> 窗口越高曲线区越高

        // 三张天气卡走同一个 Ui.Panel:标题栏、内边距、圆角与其它并列板块完全一致
        var title = string.IsNullOrEmpty(tag) ? city : $"{city} · {tag}";
        return Ui.Panel(title, inner, IconName.Weather, new Thickness(0, 0, 12, 0));
    }

    // ---------------------------------------------------------------- 时间窄条
    UIElement TimeStrip()
    {
        var grid = new UniformGrid { Rows = 1, Columns = Clocks.Length };
        for (int i = 0; i < Clocks.Length; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };

            var city = new TextBlock { Text = Clocks[i].City, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            city.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            city.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

            _time[i] = new TextBlock { Text = "—", FontSize = 15.5, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            _time[i].SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

            _meta[i] = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 0, 0) };
            _meta[i].SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            _meta[i].SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

            row.Children.Add(city); row.Children.Add(_time[i]); row.Children.Add(_meta[i]);
            grid.Children.Add(row);
        }

        // 用户裁定:不要"时间"这个标题,只要图标,且图标与信息【在同一列(同一行内)】,整体更扁。
        var rowAll = new DockPanel { LastChildFill = true };
        var clockIcon = Icons.Make(IconName.Clock, 16, "FgMuted");
        clockIcon.Margin = new Thickness(0, 0, 14, 0);
        clockIcon.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(clockIcon, Dock.Left);
        rowAll.Children.Add(clockIcon);
        rowAll.Children.Add(grid);

        var card = new Border { Child = rowAll, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 16, 0), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        return card;
    }

    void UpdateClocks()
    {
        var hour = DateTime.Now.Hour;
        _greeting.Text = hour < 5 ? "夜深了" : hour < 11 ? "早上好" : hour < 14 ? "中午好" : hour < 18 ? "下午好" : "晚上好";

        var localOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        for (int i = 0; i < Clocks.Length; i++)
        {
            try
            {
                var z = TimeZoneInfo.FindSystemTimeZoneById(Clocks[i].Tz);
                var t = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, z);
                _time[i].Text = t.ToString("HH:mm");
                var diff = (z.GetUtcOffset(DateTime.UtcNow) - localOffset).TotalHours;
                var diffText = Math.Abs(diff) < 0.01 ? "本地" : diff > 0 ? $"+{diff:0.#}h" : $"{diff:0.#}h";
                _meta[i].Text = $"{(t.Hour is >= 6 and < 18 ? "昼" : "夜")} · {diffText}";
            }
            catch { _time[i].Text = "—"; _meta[i].Text = ""; }
        }
    }
}
