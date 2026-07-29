// P3c -- 外壳导航。设计 §3:主页 + 工作空间组 + 系统组;左导航可收起。
// 条件渲染两条(设计 §3 脚注,安全相关,皮肤禁改):
//   · 投资研究:仅指定成员 + 指定端 -> 不满足时**整行不存在**(不是灰掉,是渲染树里没有)。
//   · 主机管理:仅主机端 + 家庭安全管理员 -> 副机端即使管理员也不显示。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.I18n;
using LocalAI.Client.Services;
using LocalAI.Client.Views;

namespace LocalAI.Client;

public sealed record NavItem(string Key, string TitleKey, Func<UserControl> Build);

public partial class MainWindow : Window
{
    readonly List<(NavItem item, Button button)> _nav = new();
    bool _collapsed;
    string _currentKey = "";

    App TheApp => (App)Application.Current;

    public MainWindow()
    {
        InitializeComponent();
        BuildNav();
        Navigate("home");
        RefreshStatus();
        RefreshMember();
    }

    void BuildNav()
    {
        NavPanel.Children.Clear();
        _nav.Clear();

        AddItem(new NavItem("home", "nav.home", () => new HomeView()));

        AddGroupLabel(Strings.Get("nav.workspaces"));
        AddItem(new NavItem("chat", "nav.chat", () => new PlaceholderView("nav.chat")));
        AddItem(new NavItem("assets", "nav.assets", () => new PlaceholderView("nav.assets")));
        AddItem(new NavItem("translation", "nav.translation", () => new PlaceholderView("nav.translation")));
        AddItem(new NavItem("courses", "nav.courses", () => new PlaceholderView("nav.courses")));
        AddItem(new NavItem("computer", "nav.computer_control", () => new PlaceholderView("nav.computer_control")));
        // 投资研究:D42 §7/B4 只做隐藏占位。当前无"指定成员+指定端"配置 -> 整行不渲染。
        if (ShouldShowInvestment()) AddItem(new NavItem("investment", "nav.investment", () => new PlaceholderView("nav.investment")));

        AddGroupLabel(Strings.Get("nav.system"));
        AddItem(new NavItem("extensions", "nav.extensions", () => new PlaceholderView("nav.extensions")));
        AddItem(new NavItem("settings", "nav.settings", () => new SettingsView()));
        // 主机管理 = 配对与设备管理的所在地。副机端也要能配对,所以这里显示的是"连接与设备";
        // 真正的主机专属项(仅主机端 + 管理员)在该视图内部再判定。
        AddItem(new NavItem("devices", "devices.title", () => new DevicesView()));
    }

    static bool ShouldShowInvestment() => false;   // P3c 只做隐藏占位:任何人任何端都不显示(D42 §7/B4)

    void AddGroupLabel(string text)
    {
        NavPanel.Children.Add(new TextBlock
        {
            Text = text,
            Margin = new Thickness(10, 16, 10, 6),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("FgMuted"),
        });
    }

    void AddItem(NavItem item)
    {
        var b = new Button
        {
            Content = Strings.Get(item.TitleKey),
            Tag = item.Key,
            Height = 36,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (Brush)FindResource("FgPrimary"),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        b.Click += (_, _) => Navigate(item.Key);
        NavPanel.Children.Add(b);
        _nav.Add((item, b));
    }

    public void Navigate(string key)
    {
        var hit = _nav.FirstOrDefault(n => n.item.Key == key);
        if (hit.item is null) return;
        _currentKey = key;
        ContentHost.Content = hit.item.Build();
        PageTitle.Text = Strings.Get(hit.item.TitleKey);

        foreach (var (item, btn) in _nav)
        {
            var on = item.Key == key;
            btn.Background = on ? (Brush)FindResource("BgSelected") : Brushes.Transparent;
            btn.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    void OnToggleNav(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        NavColumn.Width = new GridLength(_collapsed ? 56 : 240);
        BrandText.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseButton.Content = _collapsed ? "»" : "«";
        foreach (var (item, btn) in _nav)
        {
            // 收起时只留首字,保留 ToolTip 说明全名(无障碍:不能只靠视觉)
            btn.Content = _collapsed ? Strings.Get(item.TitleKey)[..1] : Strings.Get(item.TitleKey);
            btn.ToolTip = _collapsed ? Strings.Get(item.TitleKey) : null;
            btn.HorizontalContentAlignment = _collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        }
        foreach (var c in NavPanel.Children) if (c is TextBlock t) t.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    public void RefreshStatus()
    {
        var (key, brushKey) = TheApp.Hub.State switch
        {
            HubState.Online => ("status.online", "RiskSafe"),
            HubState.Connecting => ("status.connecting", "FgMuted"),
            HubState.NotPaired => ("status.not_paired", "RiskWarning"),
            HubState.Revoked => ("status.revoked", "RiskDanger"),
            _ => ("status.offline", "FgMuted"),
        };
        StatusText.Text = Strings.Get(key);
        StatusDot.Fill = (Brush)FindResource(brushKey);
    }

    public void RefreshMember()
    {
        // D45:设备默认成员只是**猜测**,不是认证。文案必须让人一眼能纠正,且不暗示已验明身份。
        // ★ 显示名只用主机下发后缓存的那份;客户端本地绝不持有"我是谁"的权威值
        //   (铁律:主体只来自成员表 —— gateway.py:227)。没有则显示占位,不猜。
        var name = string.IsNullOrWhiteSpace(TheApp.Settings.CachedMemberDisplayName)
                   ? "—" : TheApp.Settings.CachedMemberDisplayName!;
        MemberText.Text = Strings.Get("member.current_is", ("m", name));
        MemberHint.Text = Strings.Get("member.correct");
    }
}
