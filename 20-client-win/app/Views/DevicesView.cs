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
            Ui.Body($"连接地址:{(string.IsNullOrWhiteSpace(p.Dial) ? "(旧档案未记录,建议重新配对)" : p.Dial)}", muted: true),
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

    // ---------------------------------------------------------------- 其它已配对的电脑(需主机管理 API)
    UIElement RemoteDevicesCard()
    {
        var list = new StackPanel();
        var card = Ui.Card(Ui.Stack(
            Ui.Subtitle("其它已配对的电脑"),
            list));

        if (!TheApp.Hub.IsPaired)
        {
            list.Children.Add(Ui.Body("配对之后才能查看家庭里的其它电脑。", muted: true));
            return card;
        }

        list.Children.Add(Ui.Body("正在读取…", muted: true));
        _ = LoadRemoteDevices(list);
        return card;
    }

    async Task LoadRemoteDevices(StackPanel list)
    {
        try
        {
            var (status, body) = await TheApp.Hub.ListDevicesRawAsync();
            Dispatcher.Invoke(() =>
            {
                list.Children.Clear();
                if (status == 404)
                {
                    // ★ 这里的 404 【不是】"主机还没升级"(那是原来写的,是个假原因,审计 2026-07-31 确认)——
                    //   按 D37/D48,/admin/* 只挂在主机本地的回环口上,局域网 mTLS 口对它一律 404,
                    //   连存在性都不暴露。也就是说:从别的机器走这条路【永远】拿不到,升级也没用。
                    //   把结构性的"走不通"说成"暂时还没有",会让人一直等一个不会来的版本。
                    list.Children.Add(Ui.Body("设备管理只能在主机那台电脑上操作。", muted: true));
                    list.Children.Add(Ui.Caption("按 D37 / D48,管理接口只开在主机本地的回环口,局域网这条路结构上就到不了 —— 不是版本问题。要解除设备,请到主机上操作。"));
                    return;
                }
                if (status == 403)
                {
                    list.Children.Add(Ui.Body("只有家庭安全管理员可以管理设备。", muted: true));
                    return;
                }
                if (status != 200) { list.Children.Add(Ui.Body($"读取失败({status})。", muted: true)); return; }
                RenderDevices(list, body);
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                list.Children.Clear();
                list.Children.Add(Ui.Body("连不上中枢,暂时看不到设备列表。", muted: true));
                list.Children.Add(Ui.Caption(ex.Message));
            });
        }
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
