// P3c -- 连接与设备。用户要求:「配对流程在客户端内也要有个一键自动化的,不然现在开这开那太麻烦了。
// 列出已配对的PC以及解除按钮,一键匹配。」「不要每次开启就要配对一次,而是一开始配对一次之后就记住。」
//
// ★ 安全边界(不可让步):把配对搬进界面,只改变**六个词显示在哪里**,不改变安全性质 ——
//   六词仍由两端各自独立推导、仍需**人工逐词比对**、仍需**主机侧批准**。
//   界面绝不代替人做比对,也不提供"跳过比对"的快捷方式。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.I18n;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

/// <summary>
/// 这台电脑在这套装置里是什么角色。★ 四种,一种都不许合并:
///   · Unknown     —— 还没探完。**不猜**,界面如实说"正在确认"。
///   · Host        —— 回环管理面答话了且 hubId 对得上。这是唯一的【肯定证据】。
///   · HostHubDown —— 探不到管理面,但本机装着主机端程序 ⇒ 多半是主机,只是 Edge 没起。
///                    2026-08-03 就是栽在把这一种说成"这台不是主机"上。
///   · Client      —— 探不到,也没有主机端程序。
/// </summary>
public enum HostRole { Unknown, Host, HostHubDown, Client }

public sealed class DevicesView : UserControl
{
    App TheApp => (App)Application.Current;

    /// <summary>配对窗口一次开多久(分钟)。★ 这是【主机侧】的上限,由中枢自己到点失效 ——
    /// 和客户端在不在、页面在哪、进程有没有崩,全都无关。这是最后一道闸。</summary>
    public const int WindowMinutes = 10;

    /// <summary>拉起 Edge 后等它应答多久(秒)。★ 到点就如实说"没等到",不无限转圈。</summary>
    public const int StartEdgeWaitSeconds = 30;

    // ★ 这里原来有个 GraceSeconds(收起时的宽限)。审计之后删掉了:它存在的前提是
    //   "客户端替中枢记账、所以要小心别掐断对方",而现在账只有一份、在中枢那边,
    //   窗口的寿命由中枢的分钟上限兜底 —— 宽限这一层没有存在的理由了。

    HostRole _role = HostRole.Unknown;

    /// <summary>配对在途 —— 防止连点发出两条 enroll(两组六个词会互相盖掉)。</summary>
    bool _pairing;

    StackPanel? _devList;
    StackPanel? _addPanel;
    TextBlock? _addStatus;
    Button? _addToggle;
    System.Windows.Threading.DispatcherTimer? _pendTimer;
    readonly HashSet<string> _popped = new(StringComparer.Ordinal);
    bool _dialogOpen;

    readonly StackPanel _root = new();
    readonly TextBlock _sasBlock;
    readonly TextBlock _hint;
    readonly Border _sasCard;

    readonly bool _embedded;

    // embedded = true:并入"设置"页时用 —— 去掉外层滚动/页边距/大标题,直接作为一段内容插进去。
    public DevicesView(bool embedded = false)
    {
        _embedded = embedded;
        _sasBlock = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10),
        };
        _sasBlock.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        _hint = Ui.Body("");
        _sasCard = Ui.Card(Ui.Stack(
            Ui.Subtitle(Strings.Get("pairing.six_words")),
            _sasBlock,
            Ui.Body(Strings.Get("pairing.compare_hint"), muted: true),
            _hint));
        _sasCard.Visibility = Visibility.Collapsed;

        if (_embedded)
        {
            // 并入设置:不要自己的滚动条(外层设置页已经有),不要页边距,直接就是内容栈
            Content = _root;
            _root.Margin = new Thickness(0);
        }
        else
        {
            Content = new ScrollViewer
            {
                Content = _root,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            _root.Margin = new Thickness(28, 24, 28, 24);
            _root.MaxWidth = 980;
            _root.HorizontalAlignment = HorizontalAlignment.Left;
        }

        // ★ 第 ① 道闸的另一半:离开这一页(切到别的设置分节、关窗口、重建界面)也要关掉配对窗口。
        //   不挂这个的话,"展开着就走人"会把窗口一直留到中枢侧的分钟上限才关。
        // ★ 第二道真闸:离开这一页就关窗。判据用【中枢自报的】那一位,不用本地布尔。
        Unloaded += (_, _) => { if (TheApp.HubAdmin.PairingWindowOpen) _ = CloseWindowAsync(quiet: true); };
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue && TheApp.HubAdmin.PairingWindowOpen) _ = CloseWindowAsync(quiet: true);
        };

        Build();
    }

    /// <summary>
    /// ★ 按角色分流:主机和副机在这一页要做的事根本不同,摆同一套界面只会让两边都费解。
    ///   · 主机:它就是中枢,不该出现"填一个中枢地址去配到别人家"的框;
    ///     它要看的是【谁连进来了】和【怎么再加一台】。
    ///   · 副机:它要看的是【同一网络下有哪些中枢】,挑一个配上去。
    /// ★ 角色没探出来之前【什么都不猜】—— 如实说"正在确认",探完再画。
    /// </summary>
    void Build()
    {
        _root.Children.Clear();
        // 独立页才画大标题;并入设置时由设置页的分节小标题领起,避免重复标题
        if (!_embedded) _root.Children.Add(Ui.Title(Strings.Get("devices.title")));
        switch (_role)
        {
            case HostRole.Unknown:
                _root.Children.Add(ProbingCard());
                _ = ProbeRoleAsync();
                break;

            case HostRole.Host:
                _root.Children.Add(HostSelfCard());
                _root.Children.Add(_sasCard);
                _root.Children.Add(HostDevicesCard());
                break;

            case HostRole.HostHubDown:
                _root.Children.Add(HubDownCard());
                break;

            default:   // Client
                _root.Children.Add(TheApp.Hub.IsPaired ? PairedCard() : ClientPairCard());
                _root.Children.Add(_sasCard);
                break;
        }
    }

    UIElement ProbingCard() => Ui.Card(Ui.Stack(
        Ui.Subtitle("正在确认这台电脑的角色…"),
        Ui.Body("在 ping 本机的回环管理面(127.0.0.1:" + Services.HubAdmin.AdminPort + ")。", muted: true),
        Ui.Caption("★ 拿到肯定证据才下结论 —— 这一页在探完之前不显示任何猜测。")));

    /// <summary>
    /// 探角色。★ 顺序有讲究:先看【肯定证据】(管理面答话且 hubId 一致),
    /// 拿不到再看【线索】(本机有没有主机端程序),两样都没有才认定是副机。
    /// 把第二档漏掉就会在"主机但 Edge 没启动"时说成"这台不是主机" —— 那正是要修的那个坑。
    /// </summary>
    async Task ProbeRoleAsync()
    {
        var admin = TheApp.HubAdmin;
        try { await admin.ProbeAsync(TheApp.Hub.Profile?.HubId); }
        catch { /* 探不到就是没证据,不是错误 */ }
        var role = admin.LastProbe == Services.AdminProbeResult.Ok ? HostRole.Host
                 : Services.HubAdmin.HostToolsDir() is not null ? HostRole.HostHubDown
                 : HostRole.Client;
        Dispatcher.Invoke(() =>
        {
            _role = role;
            Build();
            (Application.Current.MainWindow as MainWindow)?.RefreshStatus();
        });
    }

    /// <summary>手动重探一次(换了状态 —— 比如刚把 Edge 起起来 —— 用它,不用重开客户端)。</summary>
    UIElement RecheckRow() => Ui.Secondary("角色检测", (_, _) => { _role = HostRole.Unknown; Build(); });

    /// <summary>
    /// 本机多半就是主机,但中枢没起来。
    ///
    /// ★★ 这张卡【不摆按钮】。用户裁定:「无痛丝滑,不需要跑任何命令栏和设置」——
    ///   那么"铸身份""起中枢"这些就是内部步骤,不是让人做选择的地方。进这一页就自己做完。
    /// ★ 唯一会冒出按钮的情形只有两种,而且都是**真的需要人**:
    ///   ① 防火墙规则不在 —— 那一步要系统授权框,绝不能不问自取地弹;
    ///   ② 上面哪一步失败了 —— 给一次重试,并且把真实原因摆出来。
    /// </summary>
    UIElement HubDownCard()
    {
        _setupLines = new StackPanel();
        _setupActions = new StackPanel();
        var card = Ui.Card(Ui.Stack(
            Ui.Subtitle("正在把这台准备成中枢主机…"),
            // ★ 先把【观察到的事】说出来,再说我要做什么 —— 顺序反了就成了"我说了算"
            Ui.Body("中枢没在这台机器上运行。本机装着主机端程序,所以这台应该就是主机。", muted: true),
            _setupLines,
            _setupActions,
            new Border { Height = 10 },
            // ★ 用户要求保留:换了状态(比如手动起了中枢)时不用重开客户端。
            //   它【只重探】,不启动任何东西 —— 按钮必须只做它名字说的那件事(自检钉着)。
            RecheckRow()));
        _ = AutoSetupAsync();
        return card;
    }

    StackPanel? _setupLines;
    StackPanel? _setupActions;

    void Line(string text, bool muted = false) => Dispatcher.Invoke(() =>
        _setupLines?.Children.Add(muted ? Ui.Caption(text) : Ui.Body(text)));

    void ResetLines() => Dispatcher.Invoke(() =>
    {
        _setupLines?.Children.Clear();
        _setupActions?.Children.Clear();
    });

    void Action(UIElement e) => Dispatcher.Invoke(() => _setupActions?.Children.Add(e));

    /// <summary>
    /// 自动把这台装成主机。★ 顺序:身份 → (防火墙,只在缺的时候才问)→ 起中枢。
    /// ★ 每一步都把【实际结果】写出来,失败就停在那儿说真原因,不往下堆新错误。
    /// </summary>
    async Task AutoSetupAsync()
    {
        ResetLines();
        try
        {
            // ---- ① 身份 ----
            // ★★ 铸身份【不是内部步骤】—— 它会在这台机器上新建一个中枢,而且不可回退
            //   (要撤只能跑破坏性的 重置并铸身份.cmd,所有已配对设备全部失效)。
            //   而走到这张卡的判据只是【旁边有个 host 目录】—— 那是线索不是判据:
            //   把主机上整个 dist 拷到第二台电脑就会满足它。不问就铸 = 网段里悄悄多出一个中枢。
            //   ⇒ 已经有身份就静默继续(那才是真·内部步骤);没有就停下来问。
            Line("① 中枢身份:正在检查…", muted: true);
            if (!await Services.HostSetup.IdentityExistsAsync())
            {
                ResetLines();
                Line("这台机器还没有中枢身份。");
                Line("★ 建一个中枢是【不可回退】的:之后要撤只能把身份删掉重铸,那会让所有已配对的电脑全部失效。"
                     + "所以这一步要你点一下,我不替你决定。", muted: true);
                Action(Ui.Primary("在这台上建中枢(我确认这台是主机)", async (_, _) => await MintThenContinueAsync()));
                Action(Ui.Secondary("这台其实是副机", (_, _) =>
                {
                    // ★ 出口:否则 Build() 在 HostHubDown 下只渲染这张卡,这台电脑【结构上】再也走不到配对
                    _role = HostRole.Client;
                    Build();
                }));
                return;
            }
            var id = await Services.HostSetup.EnsureIdentityAsync();
            ResetLines();
            Line(id.Outcome == Services.SetupOutcome.Failed
                ? "① 中枢身份:没弄成 —— " + id.Detail
                : "① 中枢身份:" + (id.Outcome == Services.SetupOutcome.Skipped ? "本来就有" : "已铸好"));
            if (id.Outcome == Services.SetupOutcome.Failed) { Retry(); return; }

            await ContinueAfterIdentityAsync();
        }
        catch (Exception ex)
        {
            // ★ 这是 fire-and-forget 调的 —— 不兜住的话界面就停在"正在…",而没人知道为什么
            ResetLines();
            Line("准备过程出错(" + ex.GetType().Name + "):" + ex.Message);
            Retry();
        }
    }

    /// <summary>身份就绪之后的两步:防火墙(只在缺的时候才问)→ 起中枢。</summary>
    async Task ContinueAfterIdentityAsync()
    {
        try
        {
            // ---- ② 防火墙:★ 只在【规则不在】时才出按钮,因为它要弹系统授权框 ----
            if (await Services.HostSetup.FirewallRuleExistsAsync())
            {
                Line("② 防火墙 8443:本来就放行了");
            }
            else
            {
                Line("② 防火墙 8443:还没放行 —— 副机会连不上这台。");
                Line("★ 这一步要弹一次系统授权框,所以不替你点。", muted: true);
                var fwStatus = NewStatus();
                Action(Ui.Primary("放行防火墙 8443(需要一次系统授权)", (_, _) => BuildNicPicker(_setupActions!, fwStatus)));
                Action(Ui.Secondary("先跳过(只在本机用)", async (_, _) => await StartEdgeStepAsync()));
                return;
            }

            // ---- ③ 起中枢 ----
            await StartEdgeStepAsync();
        }
        catch (Exception ex)
        {
            // ★ 这是 fire-and-forget 调的 —— 不兜住的话界面就停在"正在…",而没人知道为什么
            ResetLines();
            Line("准备过程出错(" + ex.GetType().Name + "):" + ex.Message);
            Retry();
        }
    }

    // ★★ 这里原来是 `readonly TextBlock _setupStatus = Ui.Body("")` —— 一个**跨 Build 共享**的控件。
    //   两个后果都真出现了:BuildNicPicker 会 Children.Clear() 把它从可视树上摘走,
    //   之后所有状态文字都写进一个看不见的控件(界面永久静默,而"公用网络"那句最有用的解释
    //   恰恰就是没人看得见的那句);再把同一个控件重新 Add 又会抛
    //   InvalidOperationException(元素已有父级),catch 里的 ResetLines() 顺手把唯一能推进的
    //   按钮也清掉,此后这个视图里不可恢复。
    //   ⇒ 每次要用就【新建一个】,谁用谁负责把它挂进树里。
    TextBlock NewStatus()
    {
        var t = Ui.Body("");
        Action(t);
        return t;
    }

    /// <summary>
    /// 起中枢。★ bindIp 非空时【直接调 localai-lan-edge.exe run-lan &lt;ip&gt;】,不走 启动Edge.cmd ——
    ///   那个 .cmd 把绑定地址**写死**成一台开发机的 192.168.178.61:换台机器、或这台换一次
    ///   DHCP 租约/改用 Wi-Fi,它绑的就是一个不存在的地址,而"无痛丝滑"这条主线在第二台电脑上
    ///   从来没成立过。而我们手里【已经有正确答案】—— 用户刚在网卡选择里挑过。
    /// ★ 拿不到 IP 时才退回 .cmd(总比什么都不做强),并如实说明它绑的是脚本里写死的那个地址。
    /// </summary>
    async Task StartEdgeStepAsync(string? bindIp = null)
    {
        var dir = Services.HubAdmin.HostToolsDir();
        var exe = dir is null ? null : Path.Combine(dir, "localai-lan-edge.exe");
        if (bindIp is { Length: > 0 } && exe is not null && File.Exists(exe))
        {
            Line($"③ 中枢:正在启动(绑定 {bindIp}:{Services.HubAdmin.EdgePort})…");
            await StartEdgeAsync(exe, NewStatus(), $"run-lan {bindIp}");
            return;
        }
        var cmd = Services.HubAdmin.StartEdgeCmd();
        if (cmd is null) { Line("③ 中枢:找不到中枢程序,也找不到 启动Edge.cmd"); Retry(); return; }
        Line("③ 中枢:正在启动…");
        Line("★ 没拿到本机网卡地址,只能跑 启动Edge.cmd —— 它绑的是脚本里写死的那个地址,"
             + "换过网段/换过机器的话会绑不上。", muted: true);
        await StartEdgeAsync(cmd, NewStatus());
    }

    /// <summary>用户明确确认之后才铸身份,然后接着往下走。</summary>
    async Task MintThenContinueAsync()
    {
        ResetLines();
        Line("① 中枢身份:正在铸造…", muted: true);
        var id = await Services.HostSetup.EnsureIdentityAsync();
        ResetLines();
        Line(id.Outcome == Services.SetupOutcome.Failed
            ? "① 中枢身份:没弄成 —— " + id.Detail
            : "① 中枢身份:已铸好");
        if (id.Outcome == Services.SetupOutcome.Failed) { Retry(); return; }
        await ContinueAfterIdentityAsync();
    }

    /// <summary>失败之后给一次重试 —— 而且只有一个按钮,别让人在一堆选项里猜。</summary>
    void Retry() => Action(Ui.Secondary("重试", (_, _) => { _ = AutoSetupAsync(); }));

    /// <summary>
    /// 防火墙规则要绑在【某一张网卡】上,所以先让人选一张 —— ★ 不替他挑:
    /// 本机常有虚拟机的仅主机网卡(如 192.168.56.x),放行在那上面等于没放行,而且看起来是成功的。
    /// 只有一张时不啰嗦,直接用。
    /// </summary>
    void BuildNicPicker(StackPanel host, TextBlock status)
    {
        var nics = Services.HostSetup.LocalNics();
        if (nics.Count == 0)
        {
            host.Children.Add(Ui.Caption("没找到启用中的网卡 —— 先把网络连上。"));
            return;
        }
        if (nics.Count == 1)
        {
            // ★ 只有一张网卡就不问 —— 没有可选的东西时弹选择框只是在浪费一次点击
            _ = SetupHostAsync(nics[0].Alias, nics[0].Ip, status);
            return;
        }
        // ★ 画在【自己的子面板】里,不去 Clear 整个动作区 —— 那会把状态行一起摘走,界面从此静默
        var box = new StackPanel();
        box.Children.Add(Ui.Body("这台有多张网卡,防火墙规则要放在【副机能看见的那一张】上,请选一张:"));
        box.Children.Add(Ui.Caption("★ 192.168.56.x 之类通常是虚拟机的仅主机网卡 —— 放在那上面副机看不见,"
                                    + "而且界面会显示成功,那是最难查的一种失败。"));
        foreach (var (alias, ip) in nics)
        {
            var a = alias; var i = ip;
            var b = Ui.Secondary($"{a} · {i}", (_, _) => { box.Visibility = Visibility.Collapsed; _ = SetupHostAsync(a, i, status); });
            b.Margin = new Thickness(0, 4, 0, 0);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            box.Children.Add(b);
        }
        host.Children.Add(box);
    }

    /// <summary>
    /// 按顺序把这台装成主机。★ 一步失败就停 —— 后面几步在前一步没成的前提下做了也白做,
    /// 而且会用一串新错误盖住真正的原因。
    /// </summary>
    async Task SetupHostAsync(string nicAlias, string nicIp, TextBlock status)
    {
        void Say(string s) => Dispatcher.Invoke(() => status.Text = s);
        string Mark(Services.SetupStep st) => st.Outcome switch
        {
            Services.SetupOutcome.Ok => "完成",
            Services.SetupOutcome.Skipped => "本来就好的",
            _ => "没成",
        };

        Say("① 中枢身份:正在检查/铸造…");
        var id = await Services.HostSetup.EnsureIdentityAsync();
        Say($"① 中枢身份:{Mark(id)} —— {id.Detail}");
        if (id.Outcome == Services.SetupOutcome.Failed) return;

        var script = Services.HostSetup.FirewallScript();
        var dir = Services.HubAdmin.HostToolsDir();
        if (script is null || dir is null)
        {
            Say($"① 中枢身份:{Mark(id)}。② 防火墙:找不到 lan-firewall.ps1 —— "
                + "请手动放行 8443(用管理员 PowerShell 跑 90-ops/lan/lan-firewall.ps1),或先跳过、只在本机用。");
            return;
        }
        Say($"① 中枢身份:{Mark(id)}。② 防火墙:马上会弹一次管理员授权框…");
        var fw = await Services.HostSetup.EnsureFirewallAsync(nicAlias, script, Path.Combine(dir, "localai-lan-edge.exe"));
        Say($"① 身份:{Mark(id)}。② 防火墙:{Mark(fw)} —— {fw.Detail}");
        // ★ 防火墙没成也【继续】起 Edge:本机自己用是通的,只是副机连不上。
        //   直接中止会让人以为整套都废了 —— 那不是实情。
        var cmd = Services.HubAdmin.StartEdgeCmd();
        if (cmd is null) { Say($"① 身份:{Mark(id)}。② 防火墙:{Mark(fw)}。③ 找不到 启动Edge.cmd。"); return; }
        await StartEdgeStepAsync(nicIp);
    }

    /// <summary>
    /// 替用户把中枢启起来。
    ///
    /// ★★ 这里【不再】预判"我是不是管理员"。同日实测推翻了那个判据:
    ///   本机 EnableLUA=0(UAC 关闭),桌面 explorer 本身就是 High,身份也是在 High 下铸的,
    ///   两把密钥在 High 进程里 CngKey.Open 都成功 —— 拿"是不是管理员"当门槛,
    ///   会在这种机器上把一个本来能起来的中枢永远挡住,而且给的理由是假的。
    ///   ⇒ 直接试着起,让中枢**自己**说话;失败时把人指向它自己的窗口,那里才有真实原因。
    ///   (见 decision-packets/integrity-guard-asks-wrong-question-2026-08-03.md)
    /// ★★ 拉起 ≠ 起来了:只有【回环管理面真的答话】才算数。在那之前一律说"正在等它应答",
    ///   绝不因为 Process.Start 没抛异常就宣布成功 —— 那是今天反复在修的那类谎。
    /// </summary>
    async Task StartEdgeAsync(string cmd, TextBlock status, string? args = null)
    {
        void Say(string s) => Dispatcher.Invoke(() => status.Text = s);

        // ★★ 先看中枢是不是【已经在跑了】。不看的话会去起第二个,而第二个必然撞 "address already in use",
        //   在黑窗口里吐一整屏 Kestrel 异常栈 —— 用户看到那一堆,根本读不出"你已经开着一个了"。
        //   (2026-08-04 实测:用户屏幕上就是这么两个窗口,一个好的、一个一屏堆栈。)
        var admin0 = TheApp.HubAdmin;
        if (await admin0.ProbeAsync(TheApp.Hub.Profile?.HubId) && admin0.LastProbe == Services.AdminProbeResult.Ok)
        {
            Dispatcher.Invoke(() =>
            {
                status.Text = "中枢已经在这台机器上跑着了 —— 不用再起一个。";
                _role = HostRole.Host;
                Build();
                (Application.Current.MainWindow as MainWindow)?.RefreshStatus();
            });
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = cmd,
                Arguments = args ?? "",
                // ★ UseShellExecute:让它开自己的控制台窗口 —— Edge 会把「拨号 …:8443」那行打在里面,
                //   出问题时那个窗口就是唯一的现场。藏起来等于把证据丢掉。
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(cmd) ?? "",
            });
        }
        catch (Exception ex)
        {
            Say("没能拉起启动脚本(" + ex.GetType().Name + ":" + ex.Message + ")—— 请自己双击:" + cmd);
            return;
        }

        Say("已经拉起启动脚本,正在等中枢应答…");
        var admin = TheApp.HubAdmin;
        for (int i = 0; i < StartEdgeWaitSeconds; i++)
        {
            await Task.Delay(1000);
            bool ok;
            try { ok = await admin.ProbeAsync(TheApp.Hub.Profile?.HubId); } catch { ok = false; }
            if (ok && admin.LastProbe == Services.AdminProbeResult.Ok)
            {
                Dispatcher.Invoke(() =>
                {
                    _role = HostRole.Host;
                    Build();
                    (Application.Current.MainWindow as MainWindow)?.RefreshStatus();
                });
                return;
            }
            Say($"已经拉起启动脚本,正在等中枢应答…({i + 1}/{StartEdgeWaitSeconds} 秒)");
        }
        // ★ 到点还没应答:如实说"没等到",并指向刚刚弹出来的那个窗口 —— 那里有真正的原因
        Say($"{StartEdgeWaitSeconds} 秒内没等到中枢应答。请看刚弹出来的那个黑色窗口 —— "
            + "它里面就是失败原因(常见:端口被占、绑的网卡地址已经不存在了、或当前身份打不开中枢密钥)。"
            + "处理完点「角色检测」。");
    }




    async Task StartPairing(string dial, string displayName, TextBlock status)
    {
        var host = dial.Split(':')[0];
        try
        {
            // hubId 未知时先用 IP 做 SNI 会导致证书名不匹配;P3b 的客户端在 enroll 阶段
            // **不校验**服务器证书(信任来自六词),所以这里用 https://<ip>:<port> 即可,
            // 配对完成后 profile 里保存的 EdgeUrl 就是它,后续 mTLS 走自定义根信任。
            var edgeUrl = $"https://{host}:{dial.Split(':')[1]}";
            await TheApp.Hub.PairAsync(edgeUrl, dial, displayName, (reqId, sas) =>
            {
                Dispatcher.Invoke(() =>
                {
                    _sasBlock.Text = string.Join("   ", sas);
                    _hint.Text = Strings.Get("pairing.waiting");
                    _sasCard.Visibility = Visibility.Visible;
                    status.Text = Strings.Get("pairing.waiting");
                });
                return Task.CompletedTask;
            });
            Dispatcher.Invoke(() =>
            {
                status.Text = Strings.Get("pairing.success");
                _sasCard.Visibility = Visibility.Collapsed;
                Build();
                (Application.Current.MainWindow as MainWindow)?.RefreshStatus();
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                _sasCard.Visibility = Visibility.Collapsed;
                status.Text = ex.Message.Contains("SAS mismatch") ? Strings.Get("pairing.mismatch")
                            : ex is TimeoutException ? Strings.Get("pairing.timeout")
                            // ★ 配对窗口默认关闭(D48):这是第一次配对最常撞上的一条,给出可执行的下一步
                            //   而不是干巴巴的异常(2026-07-31 审计)。
                            : ex.Message.Contains("pairing window is closed")
                                // ★ 文案跟着流程走:现在开窗的正路是【主机上展开「添加一台新电脑」】,
                                //   不再是去 Edge 窗口里敲 open(那是命令行时代的说法,留着会把人支到黑框里)。
                                ? "主机侧的配对窗口是关着的。请到主机那台电脑上打开客户端的同一页,"
                                  + "展开「＋ 添加一台新电脑」—— 展开就会开窗,然后回来再点一次「开始配对」。"
                            : "配对失败:" + ex.Message;
            });
        }
    }

    // ---------------------------------------------------------------- 已配对:本机状态 + 解除本机
    UIElement PairedCard()
    {
        var p = TheApp.Hub.Profile!;
        var unpair = Ui.Danger("解除本机配对", (_, _) =>
        {
            if (!ConfirmDialog.Show("解除本机配对",
                    "解除后本机将无法访问中枢,需要重新配对才能恢复。\n\n注意:这只清除本机凭据;主机侧的成员条目请在主机上一并解除。",
                    confirmText: "解除配对", danger: true)) return;
            TheApp.Hub.UnpairLocal();
            Build();
            (Application.Current.MainWindow as MainWindow)?.RefreshStatus();
        });

        return Ui.Card(Ui.Stack(
            Ui.Subtitle(Strings.Get("devices.this_device") + " · " + Environment.MachineName),
            // ★ 显示 hub_id:同一个网络里可能有【不止一个主机】(合租、邻居、或自己装了两台),
            //   只看 IP 分不清连的是哪一个 —— hub_id 是它的唯一身份(证书链也绑在它上面)。
            Ui.Body($"已连主机:{(string.IsNullOrWhiteSpace(p.HubId) ? "(旧档案未记录)" : p.HubId)}"),
            Ui.Caption("一台客户端只属于一个主机;要换主机得先解除配对再重新配对。"),
            Ui.Body($"中枢:{p.EdgeUrl}"),
            Ui.Body($"连接地址:{(string.IsNullOrWhiteSpace(p.Dial) ? "(旧档案未记录)" : p.Dial)}", muted: true),
            // ★ 换网段就能改地址,不必重新配对(重新配对会删掉本机私钥,把有效身份亲手销毁)。
            //   自动发现随 P3b.2 的 DNS-SD 补上 —— 到时候它填的也是这一个字段。
            ChangeDialRow(p),
            Ui.Body($"协议:{TheApp.Hub.ProtocolNote}", muted: true),
            Ui.Body($"本机客户端:{Services.BuildInfo.Display}", muted: true),
            // ★ 版本分两层,而且两层都【不靠六个词】—— 六词拦的是中间人,不是版本。
            //   这里曾经写过"协议版本不一致六词就对不上",那是错的:主机直接拿客户端自报的
            //   protocolVersion 去推导 SAS,两边用的是同一个值,自然永远一致。
            Ui.Caption("★ 协议版本在【连上之后】才查:中枢每次响应都带自报的版本号,对不上就直接判成"
                       + "「协议对不上」、不当作在线(不会拿可能误解的格式去解正文)。"),
            Ui.Caption("★ 但【配对那一刻】不查:协议版本是客户端自报、主机照单全收的,"
                       + "六个词不会因此对不上 —— 它们拦的是中间人,不是版本。"),
            Ui.Caption("★ 客户端构建戳只是提示:同一协议下的不同构建完全可以互通,"
                       + "把它升成硬拦 = 每发一版就必须两台同时升,否则整套停摆。"),
            Ui.Body($"状态:{Strings.Get(TheApp.Hub.State switch {
                HubState.Online => "status.online",
                HubState.Connecting => "status.connecting",
                HubState.Revoked => "status.revoked",
                HubState.CertExpired => "status.cert_expired",   // ★ 就在“解除本机配对”红按钮上方:说清“是证书过期、别解除”最要紧
                HubState.Unauthorized => "status.unauthorized",
                HubState.HubServerError => "status.hub_error",       // ★ 中枢在,是它内部出错 —— 别读成"连不上"
                HubState.ProtocolMismatch => "status.proto_mismatch",
                HubState.HubIdentityChanged => "status.hub_changed",
                _ => "status.offline" })}"),
            // ★ 状态行只有一个词,处置办法在 LastError 里 —— 不显示出来等于没说
            TheApp.Hub.LastError is { Length: > 0 } lastWhy ? Ui.Caption(lastWhy) : new Border { Height = 0 },
            new Border { Height = 12 },
            // ★ 副机会在这一页找"其它电脑在哪" —— 说清它【结构上】就到不了,不是"主机还没升级",
            //   否则人会一直等一个不会来的版本。
            Ui.Caption("★ 配对审批与设备管理只在主机那台上 —— 按 D37 / D48,管理接口只开在主机本地的回环口,"
                       + "局域网这条路结构上就到不了,不是版本问题。"),
            Ui.Caption("已记住这次配对 —— 以后启动会自动连接,不会再要求配对。"),
            new Border { Height = 12 },
            unpair
        ));
    }

    /// <summary>改连接地址的一行:一个输入框 + 一个保存。改完立刻探测一次,别让人自己猜通没通。</summary>
    UIElement ChangeDialRow(LocalAI.ClientTransport.ClientProfile p)
    {
        var box = new TextBox { Text = p.Dial, MinWidth = 180, Padding = new Thickness(6, 4, 6, 4),
                                VerticalAlignment = VerticalAlignment.Center };
        var save = Ui.Secondary("改地址", async (_, _) =>
        {
            if (!TheApp.Hub.SetDial(box.Text))
            {
                ConfirmDialog.Show("这个地址不对", TheApp.Hub.LastError ?? "地址要写成 ip:port。",
                                   confirmText: "好", cancelText: "关闭");
                return;
            }
            await TheApp.Hub.ProbeAsync();     // 立刻验一次,免得人以为改完就好了
            Build();
            (Application.Current.MainWindow as MainWindow)?.RefreshStatus();
        });
        save.Margin = new Thickness(8, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(box);
        row.Children.Add(save);
        // ★ 更好的办法:别让人去查 IP —— 按 hub_id 在局域网里把它找回来。
        //   身份从来不是"地址",而是配对时钉住的那套证书;换了地址仍然是同一个中枢。
        var find = Ui.Secondary("在局域网里找回它", async (_, _) =>
        {
            var note = Ui.Caption("正在扫本网段…");
            if (find_host is not null) { find_host.Children.Clear(); find_host.Children.Add(note); }
            var ok = await TheApp.Hub.RediscoverAsync();
            if (ok) await TheApp.Hub.ProbeAsync();
            Dispatcher.Invoke(() =>
            {
                if (ok) { Build(); (Application.Current.MainWindow as MainWindow)?.RefreshStatus(); }
                else if (find_host is not null)
                {
                    find_host.Children.Clear();
                    // ★ 这是已配对卡上唯一一个看起来能修连接的按钮,人会先点它 —— 远早于滚到第三张卡。
                    //   所以这条失败路径必须和另外两条(SelfPairAsync / HubDownCard)说同一套话,
                    //   否则它会把人支去查路由器/网段/防火墙,而真正要做的只是把本机的 Edge 起起来。
                    var hd = Services.HubAdmin.HostToolsDir();
                    find_host.Children.Add(Ui.Caption(hd is not null
                        ? "没找到:" + (TheApp.Hub.LastError ?? "未知原因")
                          + " —— 而本机装着主机端程序,多半是这台的 Edge 还没启动。去双击 "
                          + (Services.HubAdmin.StartEdgeCmd() ?? Path.Combine(hd, "启动Edge.cmd"))
                          + "。"
                        : "没找到:" + (TheApp.Hub.LastError ?? "未知原因")
                          + " —— 确认主机那台的 Edge 起着、两台在同一网段、防火墙放行了 8443。"));
                }
            });
        });
        find.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(find);

        var wrap = new StackPanel();
        wrap.Children.Add(row);
        find_host = new StackPanel();
        wrap.Children.Add(find_host);
        wrap.Children.Add(Ui.Caption("换了路由器/网段:点【在局域网里找回它】就行,或者手改地址 —— "
                                    + "两条路都【不动证书与配对】,不必重新配对。"));
        return wrap;
    }


    // ================================================================ 主机侧
    /// <summary>
    /// 「这台电脑上的中枢」+ 本机自己的连接。
    ///
    /// ★ 主机这台【也必须配对】,这一点不能含糊:业务口只绑在网卡 IP 上
    ///   (`k.Listen(cfg.Bind, 8443)`),回环上只有管理面。所以本机客户端要聊天,
    ///   同样得有成员证书,同样得走一次 enroll+批准。
    /// ★ 但它可以【一次点击走完】:本机客户端手里有回环管理面(开窗、批准都归它),
    ///   能自己把这条流程跑完,不用人填地址、不用人比六个词 —— 理由见 SelfPairAsync。
    /// </summary>
    UIElement HostSelfCard()
    {
        var admin = TheApp.HubAdmin;
        var stack = Ui.Stack(
            Ui.Subtitle("这台电脑就是中枢主机"),
            Ui.Body($"hub {admin.HubId}", muted: true),
            Ui.Caption("★ 判据是【肯定证据】:本机回环管理面答话了,而且它自报的 hub id 与本机档案一致。"),
            Ui.Caption("★ 本机的连接地址是探出来的,不用你填 —— 而且这里【不能】填 127.0.0.1:"
                       + "业务口只绑在网卡 IP 上(run-lan 那个参数),回环上只有管理面。"));

        if (TheApp.Hub.IsPaired)
        {
            stack.Children.Add(new Border { Height = 10 });
            stack.Children.Add(Ui.Body("本机客户端:已配对(" + (TheApp.Hub.Profile?.Dial ?? "?") + ")"));
            // ★ 主机换了 IP(换租约/以太网改 Wi-Fi/换了绑定网卡)时,它自己那台的 Dial 就失效了。
            //   以前这张卡上没有输入框、没有「找回它」、连解除都没有 ——
            //   最会用这套东西的那台电脑反而最没救,只能去手改 profile.json。
            //   ChangeDialRow 只依赖 Profile,和角色无关,直接复用。
            if (TheApp.Hub.Profile is { } hp) stack.Children.Add(ChangeDialRow(hp));
            stack.Children.Add(Ui.Body($"状态:{Strings.Get(TheApp.Hub.State switch {
                HubState.Online => "status.online",
                HubState.Connecting => "status.connecting",
                HubState.Revoked => "status.revoked",
                HubState.CertExpired => "status.cert_expired",
                HubState.Unauthorized => "status.unauthorized",
                HubState.HubServerError => "status.hub_error",
                HubState.ProtocolMismatch => "status.proto_mismatch",
                _ => "status.offline" })}", muted: true));
            if (TheApp.Hub.LastError is { Length: > 0 } lw) stack.Children.Add(Ui.Caption(lw));
        }
        else
        {
            var st = Ui.Body("");
            stack.Children.Add(new Border { Height = 10 });
            stack.Children.Add(Ui.Body("本机客户端还没有配对 —— 主机自己这台也需要成员证书才能用中枢。"));
            stack.Children.Add(Ui.Caption("★ 这一步不需要你填地址、也不需要比六个词 —— 理由见下面那行。"));
            stack.Children.Add(Ui.Primary("完成本机配对", async (_, _) => await SelfPairAsync(st)));
            stack.Children.Add(st);
            stack.Children.Add(Ui.Caption("★ 为什么本机不用比六个词:六个词防的是【两台机器之间】的中间人。"
                                          + "本机走的是回环管理面 —— 能连上回环的人已经在这台机器上了,没有中间人可防。"));
            stack.Children.Add(Ui.Caption("★ 代价说清楚:这一步会把配对窗口开【几秒】,那几秒局域网上的 8443 也接受请求;"
                                          + "拿到本机自己那条请求后立刻关掉。(更好的做法是走一条只在回环上的通道,"
                                          + "那要中枢侧加,已写进决议包。)"));
        }

        stack.Children.Add(new Border { Height = 10 });
        stack.Children.Add(RecheckRow());
        return Ui.Card(stack);
    }

    /// <summary>
    /// 本机自配对:开窗 → enroll → 【自己批准】→ 关窗,一次点击走完。
    ///
    /// ★★ 为什么可以不比六个词(这一段必须写清楚,否则后人会以为我们放松了准入):
    ///   六个词在这套装置里管两件事 —— ① 挡中间人;② 让批准的人确认"这条请求是我那台机器发的"。
    ///   ①:客户端本来就会独立算一遍 SAS 并和主机返回的比,对不上就中止 —— 那一层是自动的,仍在。
    ///   ②:这里批准的动作走的是**回环管理面**,而它的门禁就是"端口 + 回环"。
    ///      能调它的人已经在这台机器上了,他本来就能批准任何请求 —— 让他再比一次自己写的词,
    ///      不增加任何安全性,只增加一步。
    /// ★★ 但有一条硬前提:必须【当场重探一次】管理面并确认 hubId 仍然一致才批准。
    ///   不能拿几分钟前的探测结果当通行证 —— 那期间中枢可能换了、Edge 可能重起过。
    /// ★ 只批准【本机这一条】:按 enroll 拿到的 requestId 精确批,不碰队列里其它任何请求。
    /// </summary>
    async Task SelfPairAsync(TextBlock status)
    {
        var admin = TheApp.HubAdmin;
        void Say(string s) => Dispatcher.Invoke(() => status.Text = s);

        Say("正在确认本机就是主机…");
        if (!await admin.ProbeAsync(TheApp.Hub.Profile?.HubId) || admin.LastProbe != Services.AdminProbeResult.Ok)
        {
            Say("本机配对中止:回环管理面没答话(" + (admin.LastError ?? "原因不明") + ")—— 不拿旧结论当通行证。");
            return;
        }

        Say("正在找本机中枢在哪个地址上听…");
        var dials = await Services.HubAdmin.DiscoverEdgeDialsAsync(admin.HubId);
        if (dials.Count == 0)
        {
            Say("本机就是主机(管理面答话了,Edge 正在跑),但本机当前的网卡地址上都没人在 8443 上听 —— "
                + "说明 Edge 绑在了另一个地址上。本机当前网卡:" + string.Join("、", Services.HubAdmin.LocalIPv4List()));
            return;
        }
        if (dials.Count > 1)
        {
            // ★ 不替他挑:里面可能有只有本机看得见的仅主机网段,选错了会被写进配对档案
            Say($"本机有 {dials.Count} 个地址都在 8443 上应答({string.Join("、", dials)})—— "
                + "请到下面「添加一台新电脑」里手动选一个,别让我替你挑。");
            return;
        }

        var dial = dials[0];
        Say("正在配对(会把配对窗口开几秒)…");
        var (wst, wbody) = await admin.WindowAsync(true, 1);   // ★ 最短:1 分钟
        if (wst != 200) { Say($"没能打开配对窗口({wst} {wbody})—— 本机配对中止。"); return; }
        try
        {
            var edgeUrl = $"https://{dial.Split(':')[0]}:{dial.Split(':')[1]}";
            string? myReq = null;
            await TheApp.Hub.PairAsync(edgeUrl, dial, Environment.MachineName, async (reqId, sas) =>
            {
                myReq = reqId;
                // 六个词照样显示出来 —— 不比对不等于不告诉你发生了什么
                Dispatcher.Invoke(() =>
                {
                    _sasBlock.Text = string.Join("   ", sas);
                    _hint.Text = "本机自己批准中(同机走回环,无需人工比对)";
                    _sasCard.Visibility = Visibility.Visible;
                });
                Say("正在自己批准这一条…");
                var (ast, abody) = await admin.ApproveAsync(reqId);
                if (ast != 200) Say($"自动批准失败({ast} {abody})");
            });
            Dispatcher.Invoke(() =>
            {
                _sasCard.Visibility = Visibility.Collapsed;
                status.Text = "本机配对完成。";
                Build();
                (Application.Current.MainWindow as MainWindow)?.RefreshStatus();
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => { _sasCard.Visibility = Visibility.Collapsed; status.Text = "本机配对失败:" + ex.Message; });
        }
        finally
        {
            // ★ 无论成败都关窗 —— 开着的窗口是暴露面,不能靠"正常路径会关"来保证
            try { await admin.WindowAsync(false); } catch { }
        }
    }

    /// <summary>「已配对的电脑」+「添加一台新电脑」。后者默认收起 —— 展开才开窗。</summary>
    UIElement HostDevicesCard()
    {
        _devList = new StackPanel();
        _addPanel = new StackPanel { Visibility = Visibility.Collapsed };
        _addStatus = Ui.Caption("");

        // ★ 「可见即开」只对这一块生效,而它默认收起 ⇒ 只有主机、没有副机的人永远不会无意中开窗。
        //   展开这个动作本身就是明确意图,不用再去别处找开关。
        _addToggle = Ui.Secondary("＋ 添加一台新电脑", async (_, _) => await ToggleAddAsync());

        var stack = Ui.Stack(
            Ui.Subtitle("已配对的电脑"),
            _devList,
            new Border { Height = 12 },
            _addToggle,
            _addStatus,
            _addPanel);

        _ = LoadDevicesAsync();
        return Ui.Card(stack);
    }

    async Task LoadDevicesAsync()
    {
        var admin = TheApp.HubAdmin;
        var (ok, devices) = await admin.DevicesAsync();
        var why = ok ? null : admin.LastError;
        Dispatcher.Invoke(() =>
        {
            if (_devList is null) return;
            _devList.Children.Clear();
            if (!ok)
            {
                // ★ 取不到 ≠ 一台都没有。写成"没有别的电脑"会让人把在册的机器再配一次。
                _devList.Children.Add(Ui.Caption("没能取到设备列表(" + (why ?? "原因不明")
                                                 + ")—— 这【不等于】没有别的电脑在册,别据此重复配对。"));
                return;
            }
            // ★ provisioning = 批准了但对方没来领证(常见于两边截止时间不一致那一档)。
            //   混在"已配对"里会让人以为配好了,而它其实是个没走完的半截 —— 要标出来。
            var live = devices.Where(d => d.Status != "revoked").ToList();
            if (live.Count == 0)
            {
                _devList.Children.Add(Ui.Caption("还没有别的电脑配对进来。"));
                // ★ 说清"在线/离线"这一档现在给不出来,别让人以为列表里的都在线
                _devList.Children.Add(Ui.Caption("★ 这里只列【在册】的电脑。「现在在不在线」中枢侧还没透出来"
                                                 + "(设备记录里有 LastSeenAt,管理面还没带上它)—— 已写进决议包。"));
                return;
            }
            foreach (var d in live) _devList.Children.Add(DeviceRow(d));
            _devList.Children.Add(Ui.Caption("★ 这里列的是【在册】,不是【在线】。「现在连着没有」中枢侧还没透出来 —— 已写进决议包。"));
        });
    }


    // ================================================================ 配对窗口:只有一个所有者
    // ★ 用户的顾虑:「只有主机没有副机,岂不是窗口永远开着关不了?」
    //   原来的答案是三道闸(收起关 / 宽限 90 秒 / 中枢分钟上限)。审计指出那是**替中枢记账**:
    //   `_addExpanded` 与 `_graceUntil` 是客户端自己编的一份状态,而中枢早就在
    //   /admin/ping 与 /admin/pairing/pending 里自报 pairingWindowOpen —— HubAdmin 一直解析并存着它,
    //   全仓从没读过。两份账一定会对不上,而且真的对不上了:
    //     · 批准/拒绝/解除任一按钮触发 Build() 重建控件,而 _addExpanded 不复位
    //       ⇒ 窗口还开着,界面却显示收起,按钮说的和做的相反;
    //     · 窗口到点被中枢自己关了,界面还写着"已打开",两台屏幕互相指着对方。
    //   ⇒ 删掉本地那份账。渲染只读 admin.PairingWindowOpen,Build() 重建就没有状态可对不上。
    //   剩下的是**两道真闸**:中枢自己的分钟上限(与客户端死活无关)+ 离开这一页时关窗。
    //   「收起就关」退化成一个动作,不再需要本地布尔去记它。

    async Task ToggleAddAsync()
    {
        var admin = TheApp.HubAdmin;
        if (admin.PairingWindowOpen) { await CloseWindowAsync(); return; }

        var (st, body) = await admin.WindowAsync(true, WindowMinutes);
        if (st == 200) await admin.PendingAsync();      // 立刻回读一次,让 PairingWindowOpen 是真的
        Dispatcher.Invoke(() =>
        {
            if (_addStatus is not null)
                _addStatus.Text = st == 200
                    ? $"配对窗口已打开,最多 {WindowMinutes} 分钟后由中枢自己关掉;收起这一块或离开本页也会关。"
                    : $"没能打开配对窗口({st} {body})—— 副机现在配不进来。";
            RenderAddSection();
        });
        StartPendPolling();
    }

    async Task CloseWindowAsync(bool quiet = false)
    {
        StopPendPolling();
        try { await TheApp.HubAdmin.WindowAsync(false); } catch { /* 关不掉也有中枢侧的分钟上限兜底 */ }
        try { await TheApp.HubAdmin.PendingAsync(); } catch { }   // 回读真实状态
        if (quiet) return;
        Dispatcher.Invoke(() =>
        {
            if (_addStatus is not null) _addStatus.Text = "配对窗口已关闭。";
            RenderAddSection();
        });
    }

    /// <summary>
    /// 按【中枢自报的】窗口状态渲染这一块。★ 不看任何本地布尔 ——
    /// 这就是"两份账对不上"这一类 bug 的根治办法:只有一份账,而且在中枢那边。
    /// </summary>
    void RenderAddSection()
    {
        var open = TheApp.HubAdmin.PairingWindowOpen;
        if (_addToggle is not null) _addToggle.Content = open ? "－ 收起(收起就关掉配对窗口)" : "＋ 添加一台新电脑";
        if (_addPanel is not null) _addPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    void StartPendPolling()
    {
        if (_pendTimer is not null) return;
        _pendTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pendTimer.Tick += async (_, _) => await PollPendingAsync();
        _pendTimer.Start();
    }

    void StopPendPolling() { _pendTimer?.Stop(); _pendTimer = null; }

    async Task PollPendingAsync()
    {
        var admin = TheApp.HubAdmin;
        var (ok, pend) = await admin.PendingAsync();

        Dispatcher.Invoke(() =>
        {
            // ★ 每一轮都按中枢自报的状态重画开关 —— 窗口被中枢到点关掉时,界面立刻跟上,
            //   不会再出现"界面写着已打开、其实早关了"这种两台屏幕互相指着对方的情形。
            RenderAddSection();
            if (_addPanel is null) return;
            _addPanel.Children.Clear();
            _addPanel.Children.Add(Ui.Body("等待副机来配对"));
            if (!ok)
            {
                _addPanel.Children.Add(Ui.Caption("没能从管理面取到待批准列表(" + (admin.LastError ?? "原因不明")
                                                  + ")—— 这【不等于】没有请求。先别让对方重新配对。"));
                return;
            }
            if (pend.Count == 0)
            {
                _addPanel.Children.Add(Ui.Caption("现在没有等待批准的请求。到那台新电脑上打开客户端,"
                                                  + "在同一页里选中这台主机、点「开始配对」。"));
                return;
            }
            foreach (var p in pend) _addPanel.Children.Add(PendingRow(p));
        });

        // ★★ 这里原来是"新请求一到就自动弹框"。审计指出:enroll 是**匿名**的,
        //   弹窗因此变成一个【局域网上任何人都能触发的动作】,由对方的到达时机决定你屏幕上跳出什么。
        //   ⇒ 改成不自动弹:待批准的只进列表(PendingRow 上本来就有批准/拒绝),
        //     由你主动点某一条才弹确认。准入的节奏归你,不归发起方。
    }

    /// <summary>
    /// 有新请求进来时弹一次。
    ///
    /// ★★ 这一步【不能一键化】,也不许提供"跳过比对":
    ///   客户端自己已经会独立算一遍 SAS 和主机返回的比(对不上就中止)—— 中间人那一层是自动挡住的。
    ///   六个词在这里管的是**另一件事**:确认【你批准的这条请求就是你那台机器发的】。
    ///   局域网上任何人都能往待批准队列里塞一条,而 displayName 是**自报**的,可以写成"Zori 的笔记本"。
    ///   没有这一比,弹窗就退化成"来了个请求,点批准" —— 准入就交给了谁先按弹窗。
    /// ★ 所以按钮文字本身就是那句断言(「逐字一样」),不是一个中性的"确定"。
    /// </summary>
    async Task ShowApprovalDialogAsync(PendingPair p)
    {
        _dialogOpen = true;
        try
        {
            // ★ displayName 是**自报**的,而且服务端目前不限长(core 那半已写进决议包)。
            //   客户端这一侧先自保:截断 + 剔掉控制字符 —— 任何来自网络的文本都不该能决定窗口尺寸。
            var safeName = SafeDisplayName(p.DisplayName);
            var yes = ConfirmDialog.Show(
                $"「{safeName}」请求配对",
                "这台请求配对的电脑上应该显示着同样的六个词:\n\n"
                + "    " + string.Join("   ", p.Sas) + "\n\n"
                + "请走到那台电脑前,把六个词【逐字】对一遍。\n"
                + "★ 设备名是对方自报的,可以随便写 —— 能证明「这条请求是你发的」的只有这六个词。\n"
                + $"(还剩 {p.SecondsLeft} 秒过期)",
                confirmText: "六个词逐字一样 —— 批准",
                cancelText: "不一样 / 先不批",
                danger: false);
            // ★★ 要读返回值。以前两处都是 `await ApproveAsync(...)` 丢掉结果:
            //   请求过期(主机侧 5 分钟)时 Approve 回 409,而界面一个字都不说,那一行只是悄悄消失 ——
            //   人点了批准、什么反馈都没有,连失败都不知道。
            var (rst, rbody) = yes
                ? await TheApp.HubAdmin.ApproveAsync(p.RequestId)
                : await TheApp.HubAdmin.DenyAsync(p.RequestId);
            if (rst != 200)
                Dispatcher.Invoke(() => ConfirmDialog.Show(
                    yes ? "没能批准" : "没能拒绝",
                    rst == 409
                        ? "这条请求已经过期或已被处理了。请让对方在那台电脑上重新点一次「开始配对」。"
                          + Environment.NewLine + Environment.NewLine + "(中枢原话:" + rbody + ")"
                        : $"中枢回了 {rst}。" + Environment.NewLine + Environment.NewLine + rbody,
                    confirmText: "知道了", cancelText: "关闭"));
        }
        finally { _dialogOpen = false; }
        await PollPendingAsync();
    }

    // ================================================================ 副机侧
    /// <summary>
    /// 副机:列出同一网络下的中枢,挑一个,点开始配对。
    /// ★ 主机没开配对窗口时【也列得出来】—— 发现走的是 TLS 握手读证书名,和窗口开不开无关。
    ///   那时点开始配对会拿到 403,照实说"那台的配对窗口没开",并告诉他去主机上做哪一步。
    /// </summary>
    UIElement ClientPairCard()
    {
        var name = new TextBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 12), Text = Environment.MachineName };
        name.SetResourceReference(TextBox.ForegroundProperty, "FgPrimary");
        var status = Ui.Body("");
        var list = new StackPanel();
        var note = Ui.Caption("正在找同一网络下的中枢…");
        string? picked = null;

        // ★★ 在途闸:连点两次会发【两条】enroll,各带一把新密钥、各有一组六个词。
        //   而六词卡是共用的,后返回的那条会把先返回的盖掉 ⇒ 主机弹窗上那六个词
        //   在副机屏幕上根本不存在,人就没得可比 —— 唯一能证明"这条请求是我发的"的东西没了。
        //   副机侧还会留下一把无人引用的孤儿 CNG 密钥(配对没走完就没写档案,下次清理不到它)。
        Button? goRef = null;
        var go = Ui.Primary(Strings.Get("pairing.start"), async (_, _) =>
        {
            if (_pairing) return;                       // 忽略重复点击,不排队
            if (picked is null) { status.Text = "请先在上面选一个中枢。"; return; }
            _pairing = true;
            if (goRef is not null) goRef.IsEnabled = false;
            try
            {
                status.Text = Strings.Get("status.connecting");
                await StartPairing(picked, name.Text.Trim(), status);
            }
            finally
            {
                _pairing = false;
                if (goRef is not null) goRef.IsEnabled = true;
            }
        });
        goRef = go;

        _ = ScanForHubsAsync(list, note, d => { picked = d; status.Text = "已选:" + d; });

        return Ui.Card(Ui.Stack(
            Ui.Subtitle("连接到家里的中枢"),
            Ui.Body("没探到本机在当中枢 —— 下面是同一网络下找到的中枢。选一个,点「开始配对」。", muted: true),
            // ★ 措辞是「没探到」而不是那句更顺口的断言 —— 手里只有两条线索叠加(没有中枢应答、
            //   也没装主机端程序),不是证明。断言那句话正是 2026-08-03 坑到人的写法。
            Ui.Caption("★ 这是【没探到】,不是证明:本机既没有中枢在应答、也没装主机端程序。"
                       + "如果中枢其实就跑在这台上,点下面「重新检测」。"),
            note,
            list,
            new Border { Height = 8 },
            Ui.Body(Strings.Get("pairing.device_name")), name,
            go,
            new Border { Height = 8 },
            status,
            Ui.Caption("★ 找到它不等于信任它:接下来两边屏幕上的六个词必须逐字一致,再由主机批准。"),
            new Border { Height = 8 },
            RecheckRow()));
    }

    async Task ScanForHubsAsync(StackPanel list, TextBlock note, Action<string> onPick)
    {
        try
        {
            var scan = await Services.HubDiscovery.ScanAsync();
            Dispatcher.Invoke(() =>
            {
                list.Children.Clear();
                if (scan.Hits.Count == 0)
                {
                    // ★ 「没找到」有四种,下一步完全不同 —— 由 ScanExplain 统一说清楚。
                    //   尤其"本机没有可用 IPv4"那一种:出路是【去接网线】,不是手填(手填也连不上);
                    //   以前它和"掩码太宽"混成一句,界面会印出一个空括号的假结论。
                    note.Text = Services.HubClient.ScanExplain(scan, "中枢");
                    if (!scan.NoUsableV4) list.Children.Add(ManualDialRow(onPick));
                    return;
                }
                // ★ 全仓规矩:找到多个绝不替用户挑(合租、邻居、自己装了两台都是正常情况)
                var skipTail = "";
                if (scan.TooWide.Count > 0) skipTail += $"(另有 {string.Join("、", scan.TooWide)} 因掩码宽于 /24 没扫)";
                if (scan.TinySubnet.Count > 0) skipTail += $"({string.Join("、", scan.TinySubnet)} 是 /31、/32,没有别的主机可扫)";
                note.Text = $"找到 {scan.Hits.Count} 个中枢 —— 请自己挑一个" + skipTail + ":";
                foreach (var h in scan.Hits)
                {
                    var b = Ui.Secondary($"{h.HubId} · {h.Dial}", (_, _) => onPick(h.Dial));
                    b.Margin = new Thickness(0, 4, 0, 0);
                    b.HorizontalAlignment = HorizontalAlignment.Left;
                    list.Children.Add(b);
                }
                list.Children.Add(ManualDialRow(onPick));
            });
        }
        catch (Exception ex)
        {
            // ★ fire-and-forget:不兜住的话提示行会永远停在"正在找…"
            Dispatcher.Invoke(() => note.Text = "查找失败(" + ex.GetType().Name + ")—— 请手填地址。");
        }
    }

    /// <summary>
    /// 把自报的显示名收拾干净再往界面上放:剔掉控制字符与换行,截到 48 字。
    /// ★ 理由不是好看:~32 KB 的名字能把批准框撑到屏幕外,按钮就点不到了。
    ///   任何来自网络的文本都不该能决定窗口尺寸。
    /// </summary>
    static string SafeDisplayName(string? raw)
    {
        var t = new string((raw ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (t.Length == 0) return "(没有名字)";
        return t.Length <= 48 ? t : t[..48] + "…";
    }

    /// <summary>手填入口 —— 自动查找覆盖不到的网络(跨网段、掩码宽于 /24)只能靠它。</summary>
    UIElement ManualDialRow(Action<string> onPick)
    {
        var box = new TextBox { Width = 240, Text = "", VerticalAlignment = VerticalAlignment.Center };
        box.SetResourceReference(TextBox.ForegroundProperty, "FgPrimary");
        var use = Ui.Secondary("用这个地址", (_, _) => { var v = box.Text.Trim(); if (v.Length > 0) onPick(v); });
        use.Margin = new Thickness(8, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(box);
        row.Children.Add(use);
        var wrap = new StackPanel();
        wrap.Children.Add(Ui.Caption("找不到?照主机 Edge 窗口里那行手填,形如 192.168.178.61:8443"));
        wrap.Children.Add(row);
        return wrap;
    }



    // ★ 这里原来有个 `string? admin_err;`:全仓只写、从没读过 —— 一个【看起来有、其实没有】的错误通道。
    //   取数失败现在由 PendingAsync/DevicesAsync 的 ok 位如实带出来并显示,这个字段没有存在的理由。
    StackPanel? find_host;

    /// <summary>
    /// 一条待批准的请求。★ 六个词要与对方屏幕上【逐字一致】才能按批准 ——
    /// 界面只把两边的词摆出来,**绝不代人比对**,也不提供"跳过"。
    /// </summary>
    UIElement PendingRow(PendingPair p)
    {
        var box = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        box.Children.Add(Ui.Body(p.DisplayName.Length > 0 ? p.DisplayName : "(未自报名)"));
        var words = new TextBlock
        {
            Text = string.Join("  ", p.Sas),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 18, Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap,
        };
        words.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        box.Children.Add(words);
        box.Children.Add(Ui.Caption($"这六个词必须与那台电脑屏幕上显示的【逐字一致】。剩余 {Math.Max(0, p.SecondsLeft)} 秒。"));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var deny = Ui.Danger("拒绝", async (_, _) =>
        {
            var (dst, dbody) = await TheApp.HubAdmin.DenyAsync(p.RequestId);
            if (dst != 200) ConfirmDialog.Show("没能拒绝", $"中枢回了 {dst}:{dbody}", confirmText: "知道了", cancelText: "关闭");
            Build();
        });
        var ok = Ui.Primary("词一致,批准", async (_, _) =>
        {
            if (!ConfirmDialog.Show("批准这台电脑",
                    "确认那台电脑屏幕上显示的六个词与这里【逐字一致】吗?\n\n" + string.Join("  ", p.Sas)
                    + "\n\n★ 不一致就意味着中间有人 —— 这时候必须点取消。",
                    confirmText: "逐字核对过了,批准", cancelText: "取消")) return;
            var (ast2, abody2) = await TheApp.HubAdmin.ApproveAsync(p.RequestId);
            if (ast2 != 200)
                ConfirmDialog.Show("没能批准",
                    ast2 == 409
                        ? "这条请求已经过期或已被处理了。请让对方重新点一次「开始配对」。"
                          + Environment.NewLine + Environment.NewLine + "(中枢原话:" + abody2 + ")"
                        : $"中枢回了 {ast2}。" + Environment.NewLine + Environment.NewLine + abody2,
                    confirmText: "知道了", cancelText: "关闭");
            Build();
        });
        ok.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(ok);
        row.Children.Add(deny);
        box.Children.Add(row);

        var card = new Border { Child = box, Padding = new Thickness(10), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BorderBrushProperty, "Accent");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        return card;
    }

    UIElement DeviceRow(AdminDevice d)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6), LastChildFill = true };
        var revoke = Ui.Danger(Strings.Get("devices.revoke"), async (_, _) =>
        {
            if (!ConfirmDialog.Show(Strings.Get("devices.revoke"),
                    Strings.Get("devices.revoke_confirm", ("device", d.DisplayName)),
                    confirmText: Strings.Get("devices.revoke"), danger: true)) return;
            await TheApp.HubAdmin.RevokeAsync(d.DeviceId);
            Build();
        });
        DockPanel.SetDock(revoke, Dock.Right);
        row.Children.Add(revoke);
        // 自报名可能含恶意内容:只作显示(WPF 文本节点已转义),永不进 prompt。
        // ★ 同名设备很常见(实机就有两条 SENIORBIRDS)—— 必须带上证书指纹短码,只按名字分不开。
        var col = new StackPanel();
        col.Children.Add(Ui.Body($"{d.DisplayName}   ·   {d.Status}"));
        col.Children.Add(Ui.Caption("指纹 " + (d.CertShort ?? "(无活动证书)") + "   ·   " + d.DeviceId));
        row.Children.Add(col);
        return row;
    }

    void RenderDevices(StackPanel list, string json)
    {
        List<HubDevice> devices;
        try { devices = HubClient.ParseDevices(json); }
        catch (Exception ex) { list.Children.Add(Ui.Body("设备列表解析失败:" + ex.Message, muted: true)); return; }

        TheApp.Hub.CacheDevices(devices);   // 真拿到了才缓存 —— 项目"文件夹所在机器"复用这份
        var others = devices.Where(d => d.Status != "revoked").ToList();
        if (others.Count == 0) { list.Children.Add(Ui.Body(Strings.Get("devices.empty"), muted: true)); return; }

        foreach (var d in others)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6), LastChildFill = true };
            var revoke = Ui.Danger(Strings.Get("devices.revoke"), async (_, _) =>
            {
                if (!ConfirmDialog.Show(Strings.Get("devices.revoke"),
                        Strings.Get("devices.revoke_confirm", ("device", d.DisplayName)),
                        confirmText: Strings.Get("devices.revoke"), danger: true)) return;
                await TheApp.Hub.RevokeDeviceAsync(d.DeviceId);
                Build();
            });
            DockPanel.SetDock(revoke, Dock.Right);
            row.Children.Add(revoke);
            // 自报名可能含恶意内容:只作显示,已由 WPF 文本节点转义,永不进 prompt(Store.cs 注释同款纪律)
            row.Children.Add(Ui.Body($"{d.DisplayName}   ·   {d.Status}"));
            list.Children.Add(row);
        }
    }
}
