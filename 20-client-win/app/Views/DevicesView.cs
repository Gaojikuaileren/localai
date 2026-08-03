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

public sealed class DevicesView : UserControl
{
    App TheApp => (App)Application.Current;

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

        Build();
    }

    void Build()
    {
        _root.Children.Clear();
        // 独立页才画大标题;并入设置时由设置页的分节小标题领起,避免重复标题
        if (!_embedded) _root.Children.Add(Ui.Title(Strings.Get("devices.title")));
        _root.Children.Add(TheApp.Hub.IsPaired ? PairedCard() : PairCard());
        _root.Children.Add(_sasCard);
        _root.Children.Add(RemoteDevicesCard());
    }

    // ---------------------------------------------------------------- 未配对:一键配对
    UIElement PairCard()
    {
        var addr = new TextBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 12), Text = "" };
        addr.SetResourceReference(TextBox.ForegroundProperty, "FgPrimary");
        var name = new TextBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 12), Text = Environment.MachineName };
        name.SetResourceReference(TextBox.ForegroundProperty, "FgPrimary");

        var status = Ui.Body("");
        var go = Ui.Primary(Strings.Get("pairing.start"), async (_, _) =>
        {
            var dial = addr.Text.Trim();
            if (string.IsNullOrWhiteSpace(dial)) { status.Text = "请填写中枢地址,例如 192.168.178.61:8443"; return; }
            // 证书 SAN 是 localai-<hub>.local,但拨号走 IP。EdgeUrl 用主机名让 TLS 校验通过,
            // 实际 TCP 连接由 ConnectCallback 定向到这个 IP(P3b 既有做法)。
            status.Text = Strings.Get("status.connecting");
            await StartPairing(dial, name.Text.Trim(), status);
        });

        return Ui.Card(Ui.Stack(
            Ui.Subtitle(Strings.Get("pairing.title")),
            Ui.Body("本机还没有和中枢配对。填写中枢地址后点一次「开始配对」即可,配对成功后会【永久记住】,以后开机自动连接。", muted: true),
            new Border { Height = 10 },
            Ui.Body(Strings.Get("pairing.hub_address")), addr,
            Ui.Body(Strings.Get("pairing.device_name")), name,
            go,
            new Border { Height = 8 },
            status,
            Ui.Caption("提示:中枢地址形如 192.168.178.61:8443 —— 主机启动 Edge 时会把它打印在窗口里。")
        ));
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
                                ? "主机侧的配对窗口是关着的。请到主机那台电脑的 Edge 窗口里输入 open,再回来点「开始配对」。"
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
            Ui.Body($"状态:{Strings.Get(TheApp.Hub.State switch {
                HubState.Online => "status.online",
                HubState.Connecting => "status.connecting",
                HubState.Revoked => "status.revoked",
                HubState.CertExpired => "status.cert_expired",   // ★ 就在“解除本机配对”红按钮上方:说清“是证书过期、别解除”最要紧
                HubState.Unauthorized => "status.unauthorized",
                _ => "status.offline" })}"),
            new Border { Height = 12 },
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
        var wrap = new StackPanel();
        wrap.Children.Add(row);
        wrap.Children.Add(Ui.Caption("换了路由器/网段就在这里改 —— 证书与配对原样保留,不用重新配对。"));
        return wrap;
    }

    // ---------------------------------------------------------------- 其它已配对的电脑(需主机管理 API)
    // ================= P3c S4:管理面(仅主机本地回环)=================
    // ★ 这张卡有两副面孔,取决于【这台是不是主机】,而"是不是"靠 ping 回环管理面拿肯定证据,
    //   不靠猜拨号地址(见 HubAdmin 的说明)。
    //   · 是主机 → 配对审批(六个词 + 倒计时 + 批准/拒绝)+ 设备列表与解除,全走 127.0.0.1;
    //   · 不是   → 如实说这条路【结构上】走不通(不是版本问题),别让人等一个不会来的版本。
    UIElement RemoteDevicesCard()
    {
        var list = new StackPanel();
        var card = Ui.Card(Ui.Stack(
            Ui.Subtitle("配对审批与设备管理"),
            list));

        if (!TheApp.Hub.IsPaired)
        {
            list.Children.Add(Ui.Body("配对之后才能查看家庭里的其它电脑。", muted: true));
            list.Children.Add(Ui.Caption("★ 主机自己那台除外 —— 主机上的客户端不必先配对也能开管理面(配对审批本身归它管)。"));
        }

        list.Children.Add(Ui.Body("正在探测主机管理面…", muted: true));
        _ = LoadAdminPanel(list);
        return card;
    }

    async Task LoadAdminPanel(StackPanel list)
    {
        var admin = TheApp.HubAdmin;
        var ok = await admin.ProbeAsync(TheApp.Hub.Profile?.HubId);
        List<PendingPair> pending = new();
        List<AdminDevice> devices = new();
        if (ok)
        {
            try { pending = await admin.PendingAsync(); devices = await admin.DevicesAsync(); }
            catch (Exception ex) { admin_err = ex.Message; }
        }

        Dispatcher.Invoke(() =>
        {
            list.Children.Clear();
            if (!ok)
            {
                // ★ 结构性走不通,说清楚 ——「管理接口只开在主机本地的回环口」是 D37/D48 的设计,
                //   不是"主机还没升级"。把结构性的走不通说成"暂时还没有",会让人一直等。
                list.Children.Add(Ui.Body("这台不是主机 —— 配对审批与设备管理只能在主机上操作。", muted: true));
                list.Children.Add(Ui.Caption("按 D37 / D48,管理接口只开在主机本地的回环口(127.0.0.1:"
                                             + Services.HubAdmin.AdminPort + "),局域网那条路结构上就到不了 —— 不是版本问题。"));
                if (admin.LastError is { Length: > 0 } why) list.Children.Add(Ui.Caption("探测结果:" + why));
                return;
            }

            list.Children.Add(Ui.Body($"本机就是主机(hub {admin.HubId})—— 管理面已连上。"));

            // ---- 配对窗口:显式开关 ----
            var winRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
            winRow.Children.Add(Ui.Body(admin.PairingWindowOpen ? "配对窗口:开着" : "配对窗口:关着"));
            var toggle = admin.PairingWindowOpen
                ? Ui.Secondary("关掉窗口", async (_, _) => { await admin.WindowAsync(false); Build(); })
                : Ui.Secondary("开 10 分钟", async (_, _) => { await admin.WindowAsync(true, 10); Build(); });
            toggle.Margin = new Thickness(10, 0, 0, 0);
            winRow.Children.Add(toggle);
            list.Children.Add(winRow);
            list.Children.Add(Ui.Caption("★ 窗口【不随开机自动打开】—— 开机自启 + 无条件开窗 = 每次开机在局域网上敞开一个无人值守的准入窗口。"));

            // ---- 待批准的配对请求(S4 的正题)----
            list.Children.Add(new Border { Height = 10 });
            list.Children.Add(Ui.Body("待批准的配对请求"));
            if (pending.Count == 0)
                list.Children.Add(Ui.Caption(admin.PairingWindowOpen ? "现在没有等待批准的请求。" : "窗口关着,不会有新请求进来。"));
            foreach (var p in pending) list.Children.Add(PendingRow(p));

            // ---- 设备列表 ----
            list.Children.Add(new Border { Height = 10 });
            list.Children.Add(Ui.Body("已配对的电脑"));
            var live = devices.Where(d => d.Status != "revoked").ToList();
            if (live.Count == 0) list.Children.Add(Ui.Caption("还没有别的电脑配对进来。"));
            foreach (var d in live) list.Children.Add(DeviceRow(d));
        });
    }

    string? admin_err;

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
        var deny = Ui.Danger("拒绝", async (_, _) => { await TheApp.HubAdmin.DenyAsync(p.RequestId); Build(); });
        var ok = Ui.Primary("词一致,批准", async (_, _) =>
        {
            if (!ConfirmDialog.Show("批准这台电脑",
                    "确认那台电脑屏幕上显示的六个词与这里【逐字一致】吗?\n\n" + string.Join("  ", p.Sas)
                    + "\n\n★ 不一致就意味着中间有人 —— 这时候必须点取消。",
                    confirmText: "逐字核对过了,批准", cancelText: "取消")) return;
            await TheApp.HubAdmin.ApproveAsync(p.RequestId);
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
