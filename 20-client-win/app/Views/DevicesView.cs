// P3c / V21 -- 连接与设备:**敲门那一侧**。
//
// 用户要求:「配对流程在客户端内也要有个一键自动化的,不然现在开这开那太麻烦了。
// 列出已配对的PC以及解除按钮,一键匹配。」「不要每次开启就要配对一次,而是一开始配对一次之后就记住。」
//
// ★ 安全边界(不可让步):把配对搬进界面,只改变**六个词显示在哪里**,不改变安全性质 ——
//   六词仍由两端各自独立推导、仍需**人工逐词比对**、仍需**主机侧批准**。
//   界面绝不代替人做比对,也不提供「跳过比对」的快捷方式。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ V21:主机侧那一半已搬进 `admin/Views/HostHubView.cs`。**按成员名切,不按行号。**
//
//  留下的是 V10 §2.3 逐项点名的「客户端**必须**保留的那一侧」:
//    ① 发起侧:`KnockAsync`(敲门)· `ScanForHubsAsync`(找中枢)· `ClientPairCard`;
//    ② **六词显示**:`StartPairing` 里那三行 —— SAS 必须在**这台的屏幕上**显示,
//       人才能两屏比对。这是 P3b 安全根基(D47),**搬走就等于取消它**。
//       ★ 它落在 V10 §2.2「124–597 ⇒ 管理端」那个旧基线区间里 ——
//         照行号切会把它一刀切走,而且不会有任何东西红。这就是为什么按成员切。
//    ③ 三类失败分因(D93 那五种)全部留这儿:它们解释的是**这台机器连不上**,
//       而副机上没有管理端可问;
//    ④ `SetDial`(改地址)· `RediscoverAsync`(按 hub_id 找回)· 证书续签与告警。
//
//  ★★ 纪律②:通用客户端主副机同一个 exe ⇒ **这一页里不再有任何「主机上才显示」的分支**。
//    `HostRole` 枚举、`ProbeRoleAsync`、`_role` 三者一起走了 ——
//    此前「副机不显示主机卡」靠的是**运行期**探测(探不到 8442 就藏起来),
//    现在是**代码不在**。
//  ★★★ 于是主机上的这个客户端与副机上的**长得完全一样**:它也敲门、也显示六个词,
//    也等对面批准 —— 只不过对面就在同一张桌子上(管理端面板)。
// ══════════════════════════════════════════════════════════════════════════════

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.I18n;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public sealed class DevicesView : UserControl
{
    App TheApp => (App)Application.Current;

    // ★★ `WindowMinutes` / `StartEdgeWaitSeconds` / `GraceSeconds` 都是**主机侧**的数,
    //   已随配对窗口与起栈一起搬进 `admin/Views/HostHubView.cs`。
    //   客户端这一侧从来不开窗、不起栈,留着这两个常量只会让人以为它还管着什么。

    /// <summary>配对在途 —— 防止连点发出两条 enroll(两组六个词会互相盖掉)。</summary>
    bool _pairing;


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

        // ★★ 这里原来挂着两个「离开本页就关掉配对窗口」的钩子。它们跟着配对窗口一起搬走了 ——
        //   开窗/关窗是**主机侧**的动作(`POST /admin/pairing/window`),客户端结构上够不着那个口。
        //   ⇒ 那道闸现在挂在 `admin/Views/HostHubView` 的同两个事件上,一个字都没少。

        Build();
    }

    /// <summary>
    /// 这一页只有两态:**配过了** / **还没配**。
    ///
    /// <para>★★★ V21:此前这里是一个四分支的 `switch (_role)` —— 那是**运行期角色分叉**,
    /// 纪律②明禁。今天不是「把主机那两支藏起来」,是**它们不在这个 exe 里**。
    /// 判据也跟着变了:从「探本机回环管理面答不答话」变成「这台配过对没有」——
    /// 后者是本机自己的事实,不需要向任何人打听。</para>
    ///
    /// <para>★ 主机上跑的也是这一份代码,走的也是这两态。主机与副机的差别不在这一页里,
    /// 而在**那台机器上装没装管理端**(`AdminApp.AdminAppPath()`)—— 一个安装事实。</para>
    /// </summary>
    void Build()
    {
        _root.Children.Clear();
        // 独立页才画大标题;并入设置时由设置页的分节小标题领起,避免重复标题
        if (!_embedded) _root.Children.Add(Ui.Title(Strings.Get("devices.title")));
        _root.Children.Add(TheApp.Hub.IsPaired ? PairedCard() : ClientPairCard());
        _root.Children.Add(_sasCard);
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
                                // ★★★ V24 修两处指错人的:
                                //   ① 原文说「打开**客户端**的同一页」—— 错。「＋ 添加一台新电脑」这个控件
                                //      在 localai-client 这个 exe 里**根本不存在**,它在**管理端**的「主机中枢」页
                                //      (admin/Views/HostHubView.cs 的 `_addToggle`)。照原文做,人在主机的客户端上
                                //      翻到底也找不到那个东西。
                                //   ② 原文让人回来点「开始配对」—— 那颗按钮**已经被删了**(提交 fb59d4e 改成了
                                //      「开始寻找主机」,同一提交删了一句配套文案、漏掉了这一句)。
                                ? "主机侧的配对窗口是关着的。请到主机那台电脑上打开【管理端】"
                                  + "(它的托盘菜单里有「打开管理端面板」,客户端设置里也有一颗同名的),"
                                  + "在管理端里进「主机中枢」那一页、展开「＋ 添加一台新电脑」—— 展开就会开窗;"
                                  + "然后回到这台再点一次「开始寻找主机」,重新选中它。"
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
                HubState.CertExpired => "status.cert_expired",   // ★ 就在“解除本机配对”红按钮上方:说清“是【主机】证书过期、别解除”最要紧
                HubState.Unauthorized => "status.unauthorized",
                HubState.HubServerError => "status.hub_error",       // ★ 中枢在,是它内部出错 —— 别读成"连不上"
                HubState.ProtocolMismatch => "status.proto_mismatch",
                HubState.HubIdentityChanged => "status.hub_changed",
                // ★★ 本机这一侧的两态。这张卡上就有那个红色的「解除本机配对」按钮,
                //   所以这两格尤其要说对:一格是"过期了,只能重新配对"(下面 LastError 会说清代价),
                //   一格是"材料没了,重配不毁掉任何还有用的东西"。判成"未连接"会让人去重启一台没病的中枢。
                HubState.LocalCertExpired => "status.local_cert_expired",
                HubState.LocalProfileUnusable => "status.local_unusable",
                _ => "status.offline" })}"),
            // ★★ 过期【之前】的提醒:此刻客户端**还是在线的**,所以它不会出现在上面那个状态词里。
            //   不在这里露一次,用户就只能等到过期之后才知道 —— 而那时续签路径已经够不着了。
            TheApp.Hub.CertWarning is { Length: > 0 } certWarn ? Ui.Caption(certWarn) : new Border { Height = 0 },
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
            // ══════════════════════════════════════════════════════════════
            //  ★★★ V13(D?)之后这一行**必须**看配对通道那一格,不能只看 State。
            //
            //  主机上业务调用已经改走回环网关 ⇒ `ProbeAsync` 里那次 `/v1/models`
            //  打的是 127.0.0.1,**与刚填进去的地址完全无关**,永远 200 ⇒ State=Online。
            //  而这个框改的 `Profile.Dial` 仍然是聊天、内网同步、**90 天一次的设备证书续签**
            //  唯一的拨号目标。⇒ 只看 State 的话,「填错一位」与「填对」长得一模一样,
            //  人拿着一个绿点走开,三样东西在背后静默地打向一个不存在的地址。
            //  ★ 这条不是理论:2026-08-08 的对抗式复核就是照着这条路把它走出来的。
            // ══════════════════════════════════════════════════════════════
            if (TheApp.Hub.PairingChannelError is { Length: > 0 })
                ConfirmDialog.Show("地址存下了,但这条通道连不上",
                    TheApp.Hub.PairingChannelNote
                    + "\n\n★ 中枢本身可能是好的(这台是主机时,面板和显存走的是另一条回环通道,"
                    + "所以顶栏可能仍然显示已连接)—— 但上面那三样东西走的是你刚填的这个地址。",
                    confirmText: "知道了", cancelText: "关闭");
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
                    //   所以这条失败路径必须说一句**可执行的下一步**,
                    //   否则它会把人支去查路由器/网段/防火墙,而真正要做的可能只是把本机的中枢起起来。
                    // ★★ V21:判据从「旁边有没有 host 工具目录」换成「**这台装没装管理端**」——
                    //   起中枢那件事现在只有管理端做得到(客户端里连那个入口都没有了),
                    //   所以指路要指到**管理端**去,而不是让人自己去双击一个 .cmd。
                    //   ★ `StartEdgeCmd()` 跟着 `HubAdmin` 搬走了,这一句因此也**不再提那个 .cmd** ——
                    //     留着它会把人指向一条这个程序已经不负责的路。
                    var hasAdmin = Services.AdminApp.AdminAppPath() is not null;
                    find_host.Children.Add(Ui.Caption(hasAdmin
                        ? "没找到:" + (TheApp.Hub.LastError ?? "未知原因")
                          + " —— 而这台装着管理端,多半是本机的中枢还没起来。"
                          + "到设置最下面点「打开管理端面板」→「主机中枢」,那一页会把它起起来。"
                        : "没找到:" + (TheApp.Hub.LastError ?? "未知原因")
                          + " —— 确认主机那台的中枢起着、两台在同一网段、防火墙放行了 8443。"));
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


    // ================================================================ 副机侧
    /// <summary>
    /// 副机:列出同一网络下的中枢,挑一个,点开始配对。
    /// ★ 主机没开配对窗口时【也列得出来】—— 发现走的是 TLS 握手读证书名,和窗口开不开无关。
    ///   那时点开始配对会拿到 403,照实说"那台的配对窗口没开",并告诉他去主机上做哪一步。
    /// </summary>
    /// <summary>
    /// 副机 · 未配对。★ 用户终版规格:这一屏**只允许有**「开始寻找主机」+ 网络选择(仅多网)+ 角色检测。
    ///   按下按钮 → 发一次敲门广播(按需,不是每 5 秒一次)→ 等主机在它那边批准。
    /// ★★ V24:批准键叫「词一致,批准」,在**管理端**的「主机中枢」页上
    ///   (admin/Views/HostHubView.cs 的 `PendingRow`);拒绝键叫「拒绝」。
    ///   **从来没有过一个叫「接受」的东西** —— 这一页原先三处都这么写,全是凭空指名。
    /// ★ 敲门协议要中枢侧配合(UDP 监听 + 敲门列表 + accept/reject),core 还没落地 ——
    ///   所以这里【如实降级】:发不出去就说"这台中枢还不支持敲门",并给出旧的手动路径,
    ///   绝不假装功能已经有了。
    /// </summary>
    UIElement ClientPairCard()
    {
        var status = Ui.Body("");
        var extra = new StackPanel();

        var find = Ui.Primary("开始寻找主机", async (_, _) =>
        {
            if (_pairing) return;
            _pairing = true;
            try { await KnockAsync(status, extra); }
            finally { _pairing = false; }
        });

        var stack = Ui.Stack(
            Ui.Subtitle("连接到家里的中枢"),
            Ui.Body("这台还没有配对。点一下「开始寻找主机」、从找到的中枢里选一个;"
                    + "然后到主机那台的管理端「主机中枢」页上,逐字核对六个词再按「词一致,批准」。", muted: true),
            find,
            status,
            extra);

        // ★ 网络选择只在【多网】时出现;只有一个就自动用它、不显示按钮(用户裁定)
        var nics = Services.HostSetup.LocalNics();
        if (nics.Count > 1)
        {
            stack.Children.Add(new Border { Height = 8 });
            stack.Children.Add(Ui.Body("从哪个网络找:", muted: true));
            foreach (var (alias, ip) in nics)
            {
                var a = alias; var i = ip;
                var b = Ui.Secondary($"{a} · {i}" + (_pickedNic == i ? "  ✓" : ""), (_, _) => { _pickedNic = i; Build(); });
                b.Margin = new Thickness(0, 4, 0, 0);
                b.HorizontalAlignment = HorizontalAlignment.Left;
                stack.Children.Add(b);
            }
        }
        else if (nics.Count == 1) _pickedNic = nics[0].Ip;

        // ★★ 这里原来有一颗「角色检测」按钮(`RecheckRow`),它把 `_role` 打回 Unknown 再重探。
        //   `_role` 已经不存在了(纪律②),所以那颗按钮**没有可按的东西** ——
        //   ⇒ 跟着删掉,不留一颗点了不做事的按钮(那比没有按钮更坏)。
        //   ★ 它原来的用处「换了状态不用重开客户端」由上面那颗「开始寻找主机」承下来:
        //     那一下就是重新扫一遍局域网,而这一页现在只关心「配没配上」。
        //   ★ 主机这台若要重新检测**中枢**的状态,那是管理端面板上的「重新检测」——
        //     它问的是回环管理面,而那个口客户端结构上够不着。
        return Ui.Card(stack);
    }

    /// <summary>选中的网卡 IP(单网卡时自动落定,界面上不出现选择按钮)。</summary>
    string? _pickedNic;

    /// <summary>
    /// 敲一次门。★ 中枢侧协议(UDP 广播 + /admin/pairing/knocks + accept/reject)还没落地,
    /// 所以这里先做【如实降级】:扫一遍局域网,找到中枢就告诉用户"去主机的管理端上批准";
    /// 一台都找不到就把真实原因说清楚(四种情形由 ScanExplain 分开说)。
    /// ★ 绝不假装敲门已经发出去了 —— 那会让人在副机这边干等一个不会来的响应。
    /// </summary>
    async Task KnockAsync(TextBlock status, StackPanel extra)
    {
        Dispatcher.Invoke(() => { status.Text = "正在找同一网络下的中枢…"; extra.Children.Clear(); });
        Services.ScanResult scan;
        try { scan = await Services.HubDiscovery.ScanAsync(); }
        catch (Exception ex) { Dispatcher.Invoke(() => status.Text = "查找失败(" + ex.GetType().Name + ")"); return; }

        Dispatcher.Invoke(() =>
        {
            if (scan.Hits.Count == 0)
            {
                status.Text = Services.HubClient.ScanExplain(scan, "中枢");
                if (!scan.NoUsableV4) extra.Children.Add(ManualDialRow(d => { _pickedDial = d; Build(); }));
                return;
            }
            // ★ 敲门协议未落地时的实情:我们只能"找到"它,没法让它在主机屏幕上冒出来。
            //   说清楚现在要人做什么,而不是显示一个假的"已通知主机"。
            // ★★ V24:原文结尾是"并在主机那台上按接受" —— 主机那台上**没有任何叫「接受」的东西**。
            status.Text = $"找到 {scan.Hits.Count} 个中枢。";
            extra.Children.Add(Ui.Caption("★ 敲门广播需要中枢侧配合(还没上线)—— 现在请先在下面选一个中枢开始配对,"
                                          + "再到主机那台的管理端「主机中枢」页上按「词一致,批准」。"));
            foreach (var h in scan.Hits)
            {
                var hit = h;
                var b = Ui.Secondary($"{hit.HubId} · {hit.Dial}", async (_, _) =>
                {
                    status.Text = Strings.Get("status.connecting");
                    await StartPairing(hit.Dial, Environment.MachineName, status);
                });
                b.Margin = new Thickness(0, 4, 0, 0);
                b.HorizontalAlignment = HorizontalAlignment.Left;
                extra.Children.Add(b);
            }
        });
    }

    string? _pickedDial;

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



}
