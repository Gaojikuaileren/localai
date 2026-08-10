// V14/V21 -- 管理端主窗口。★ 整窗用的是**客户端那 704 行界面原语**(csproj link,不是复制):
//   Ui.Page / Ui.Panel / Ui.Card / Ui.Body / Ui.Primary ... 一个都没有重写。
//
// ★★ 颜色**一律走令牌**(SetResourceReference / Ui.Dyn),本文件里不许出现任何颜色字面量。
//   两个 exe 各自渲染,漂了不会有任何东西红 —— 所以这条靠断言钉(见客户端 Selftest 的 V14 段)。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ V21:3100 行搬进来之后,这个窗口从「一张卡」变成「有分页的面板」。
//
//  四页,分别对应 V10 §0 用户裁定第 2 条点名要搬的那几样:
//    · 主机中枢   —— 设备角色判断 · 发身份 · 起关栈 · **已配对设备与配对批准**
//    · 模型       —— 组件/模型(勾组件 = 对显存的单侧权威决定)
//    · 记忆库     —— 记忆的浏览与编辑(副机上结构性地没有这一页)
//    · 客户端与栈 —— 原来那张卡:主机客户端的开关 + 关栈入口
//
//  ★ 分页控件不是新造的:用的是同一套 `Ui.Secondary` 按钮 + 一个内容宿主,
//    与客户端左栏的做法同源。管理端不该长出第二套导航语汇。
// ══════════════════════════════════════════════════════════════════════════════

using System.Windows;
using System.Windows.Controls;
using LocalAI.Admin.Services;
using LocalAI.Admin.Views;
using LocalAI.Client.Theme;
using LocalAI.Client.Views;

namespace LocalAI.Admin;

public sealed class AdminWindow : Window
{
    readonly StackPanel _body = new();
    readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
    readonly ContentControl _page = new();

    /// <summary>四页的键。★ 与下面 `Render` 里的 switch 一一对应 —— 加一页要两处一起改,
    /// 而漏改的表现是"点了没反应",所以 `default` 分支**明说**是哪个键没接上,不静默。</summary>
    static readonly (string Key, string Title)[] Pages =
    {
        ("hub", "主机中枢"),
        ("model", "模型"),
        ("memory", "记忆库"),
        ("client", "客户端与栈"),
    };

    /// <summary>四页的键。★ V29 放出来只为一件事:让滚轮那条护栏能**逐页**普查
    /// (`SelftestWheel.WheelSweep`)。写死一份键列表在自检里的话,加了第五页而忘了加进去,
    /// 那一页就静默地不在普查范围内 —— 而那正是这条护栏要防的形状。</summary>
    internal static IEnumerable<string> PageKeys => Pages.Select(p => p.Key);

    /// <summary>整窗**唯一**那个页面滚动容器(`Ui.Page` 返回的那个)。
    /// ★ V29 起 `Content` 是「自绘标题栏 + 正文」的 Grid,不再直接是它 ——
    /// 滚轮那条护栏要读它的 `VerticalOffset`,靠 `(ScrollViewer)Content` 强转会当场炸。</summary>
    internal ScrollViewer PageHost => _pageHost;
    ScrollViewer _pageHost = null!;

    string _active = "hub";

    /// <summary>★ 每一页**建一次就留着**:`HostHubView` 里有轮询与配对窗口状态,
    /// 每次切页都重建会让「离开本页就关窗」那道闸反复开关。</summary>
    readonly Dictionary<string, UIElement> _cache = new(StringComparer.Ordinal);

    public AdminWindow()
    {
        // ★★ 滚轮让路(实机反馈①)。**排在建内容之前**:`AdminScroll` 是靠类处理器
        //   在每个 ScrollViewer 第一次排版时装的,晚于内容建出来就会漏掉第一批。
        //   ★ 放在这里而不是只放在 `App.OnStartup`:自检建的是这个窗口、不跑 OnStartup,
        //     两处分家的话「自检里绿、真程序里没接上」是可能的 —— 而那种绿最坏。
        //   ★ 幂等(见 AdminScroll.Install)。
        Views.AdminScroll.Install();

        // ★ `Title` 留着:它是**任务栏与 Alt+Tab** 认这个窗口的名字。
        //   界面上不再重复它 —— 自绘标题栏里不放标题文字(与客户端 2026-08-03 的裁定一致,
        //   见 `AdminChrome.cs` 文件头),所以正文里 `Ui.Title("主机管理端")` 那一处是唯一的一处。
        Title = "本地 AI · 主机管理端";
        Width = 1040; Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this.Dyn(BackgroundProperty, "BgWindow");
        this.Dyn(FontFamilyProperty, "FontUI");
        this.Dyn(FontSizeProperty, "FontBody");
        // ★ 顶部栏风格统一(实机反馈④):自绘标题栏 + 正文,由 `AdminChrome` 一处排布。
        _pageHost = Ui.Page(_body);
        Views.AdminChrome.Apply(this, _pageHost);
        Refresh();
    }

    /// <summary>把当前状态重画一遍。★ 每一格都只说**读到的**,读不到就说读不到。</summary>
    public void Refresh()
    {
        _body.Children.Clear();
        _body.Children.Add(Ui.Title("主机管理端"));
        _body.Children.Add(Ui.Body(
            "这台是主机。中枢身份、组件与模型、存储与记忆、起关栈都在这里管。", muted: true));

        _tabs.Children.Clear();
        foreach (var (key, title) in Pages)
        {
            var k = key;
            var b = _active == k ? Ui.Primary(title, (_, _) => Go(k)) : Ui.Secondary(title, (_, _) => Go(k));
            b.Margin = new Thickness(0, 0, 8, 0);
            _tabs.Children.Add(b);
        }
        _body.Children.Add(_tabs);
        _body.Children.Add(_page);
        Render();
    }

    void Go(string key)
    {
        if (_active == key) return;
        _active = key;
        Refresh();
    }

    /// <summary>切页。★ V29 的测试缝:与用户点分页按钮**走同一条路**(`Go`),
    /// 不是另建一个视图 —— 另建的那种"测过了"什么也不说明(ASSERTION-PITFALLS 第 14 条)。</summary>
    internal void GoTo(string key) => Go(key);

    void Render()
    {
        if (!_cache.TryGetValue(_active, out var view))
        {
            view = _active switch
            {
                "hub" => new HostHubView(),
                "model" => new ModelsView(),
                "memory" => new MemoryView(),
                "client" => ClientAndStackCard(),
                // ★ 不静默:加了一页却忘了接上来,这里会**明说是哪个键**,
                //   而不是渲染一片空白让人以为界面坏了。
                _ => Ui.Card(Ui.Stack(
                        Ui.Subtitle("这一页没有接上"),
                        Ui.Body($"页键 `{_active}` 在 Pages 里登记了,但 Render 里没有对应分支。", muted: true))),
            };
            _cache[_active] = view;
        }
        _page.Content = view;
    }

    // ---------------------------------------------------------------- 客户端与栈(V14 原来那张卡)
    UIElement ClientAndStackCard()
    {
        var running = ClientLink.IsClientRunning();
        var clientBody = Ui.Stack(
            Ui.Body(running ? "主机客户端:正在运行。" : "主机客户端:没有在运行。"),
            Ui.Caption(running
                ? "关闭管理端(托盘右键 → 关闭)时会请它优雅退出 —— 走它自己的八步善后,不强杀。"
                : "可以从这里把它打开;它也会在开机时自启到托盘。"));

        var stack = new StackPanel();
        stack.Children.Add(Ui.Panel("主机客户端", clientBody, IconName.Devices));

        var act = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 12) };
        if (!running)
            act.Children.Add(Ui.Primary("打开主机客户端", (_, _) =>
            {
                // ★★ 实机反馈③:**成功不弹窗** —— 客户端窗口本身就是成功的证据,
                //   而重画之后上面那行会变成「主机客户端:正在运行。」,证据在界面上留着。
                //   ★ 失败**不静音**:原因存下来,下面那一行会把它印出来(不是弹窗)。
                //     判词:跑不了和跑过了必须长得不一样。
                var (ok, why) = ClientLink.StartClient(tray: false);
                _clientStartError = ok ? null : "没能打开:" + why;
                _cache.Remove("client");
                Refresh();
            }));
        act.Children.Add(Ui.Secondary("刷新", (_, _) => { _cache.Remove("client"); Refresh(); }));
        stack.Children.Add(act);
        // ★ 失败那一行**排在按钮下面、留在原地** —— 不自动消失,下一次点成功了才清掉。
        if (_clientStartError is { Length: > 0 } err) stack.Children.Add(AdminNotice.Failure(err));

        // ── 关栈那一格(裁定⑤)──────────────────────────────────────
        stack.Children.Add(Ui.Panel("关掉整套 AI 栈",
            Ui.Stack(
                Ui.Body("关栈是人的动作,不是推断(D102 裁定④)。"),
                Ui.Caption("入口在托盘右键 → 关闭:那一下会先问一句「现在关会不会切断别人」,"
                         + "把判据摆给你看,再由你决定。")),
            IconName.Settings));

        // ── 起栈那一格(V21:客户端不再起栈,入口只有这一个)─────────
        stack.Children.Add(Ui.Panel("启动网关与 LAN Edge",
            Ui.Stack(
                Ui.Body("★★ 全仓【唯一】的起栈入口在管理端(V10 §7:两个 exe 都想起 Edge ⇒ 只留一个)。"),
                Ui.Caption("客户端在主机上开机时会把管理端拉起来;起栈的进度与失败原因在「主机中枢」那一页。"),
                Ui.Secondary("去「主机中枢」看", (_, _) => Go("hub"))),
            IconName.Model));

        return stack;
    }

    /// <summary>上一次「打开主机客户端」失败的原因;成功或没点过是 <c>null</c>。
    /// ★ 存成字段是因为点完要 `Refresh()` 重画整张卡 —— 不存的话那一行当场被自己刷掉,
    /// 表现与"没弹窗也没提示"一模一样,等于失败又被静音了一次。</summary>
    string? _clientStartError;
}
