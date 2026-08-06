// P3c S4 -- 主机本地【管理面】通道(D37/D48:管理操作仅主机本地)。
//
// 为什么单独一条通道:
//   · 业务口(LAN Edge 的 mTLS :8443)对 `/admin/*` 一律 **404** —— 连存在性都不暴露,这是有意为之。
//     从副机怎么调都是 404,升级主机也不会变(见 HubClient 里那段"已知的结构性限制")。
//   · 管理面只挂在主机的**回环口**上,并且路由自己还要再确认一次
//     「请求确实来自管理端口 + 回环地址」(lan-edge Program.cs 的 IsAdmin 双保险)。
//   ⇒ 所以客户端要管设备/批配对,只有一条路:**跑在主机上的那个客户端,走 127.0.0.1**。
//
// ★ 「本机是不是主机」不靠猜(HubClient.ThisMachineIsHub 那个启发式只用于状态显示):
//   这里直接 **ping 一下回环管理面**,并核对它自报的 hubId 与本机配对档案里的 HubId 一致。
//   拿到肯定证据才认。这比"拨号地址看着像本机"强:同一台机器上可能跑着另一个中枢,那时 hubId 就对不上。
//
// ★★ 但探测失败【不等于】"这台不是主机" —— 这条曾经写反,并且真的坑到人了(2026-08-03):
//   主机那台本身持有中枢身份,只是 lan-edge 没启动,8442 上自然没人听。当时界面直接说
//   「这台不是主机」,人就去怀疑"我是不是配错机器了",而唯一要做的只是把 Edge 起起来。
//   ⇒ 所以这里把失败**分类**(AdminProbeResult),由界面去说【实际观察到的是什么】,
//     绝不替它塌缩成一个证明不了的结论。
//
// ★ 不做 mTLS:管理面的门禁是**端口 + 回环**,不是证书。在回环上再套一层客户端证书
//   既不增加安全性(能连回环就已经在这台机器上了),又会把"主机自己管自己"绑死在
//   "必须先配对成功"上 —— 而配对界面本身就归它管,那会变成鸡生蛋。

using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace LocalAI.Client.Services;

/// <summary>
/// 探测回环管理面的结果分类。★ 存在的理由:界面必须说【观察到的事】,
/// 而"没应答"有好几种完全不同的处置办法,塌缩成一句话就会把人支去做无用功。
/// </summary>
public enum AdminProbeResult
{
    /// <summary>连上了,而且 hubId 对得上 —— 本机确实正在当主机。</summary>
    Ok,
    /// <summary>没人在回环管理口上听。中枢没在这台机器上跑 —— ★ 这【不等于】这台不是主机。</summary>
    NotListening,
    /// <summary>连上了但没在超时内答复。</summary>
    Timeout,
    /// <summary>连上了,但它自报的 hubId 不是本机配对的那个(同机跑着另一个中枢)。</summary>
    WrongHub,
    /// <summary>连上了,但答了个错误码 —— 多半是两边版本对不上或路由变了。</summary>
    HttpError,
    /// <summary>其它(异常已记在 LastError 里)。</summary>
    Unknown,
}

/// <summary>一条待批准的配对请求(六个词要与对方屏幕上逐字一致才批)。</summary>
public sealed record PendingPair(string RequestId, string DisplayName, string[] Sas, int SecondsLeft);

/// <summary>主机成员库里的一台设备(比 HubDevice 多了指纹短码 —— 同名设备很常见,只按名字分不开)。</summary>
public sealed record AdminDevice(string DeviceId, string DisplayName, string Status, string? CertShort);

/// <summary>
/// 主机侧**服务器证书自动轮换**的可观测状态,来自 <c>/admin/ping</c> 的 <c>serverCert</c>。
///
/// ★★ 这个类型存在的全部理由:在它之前,`serverCert` 这个字段**全仓没有任何读取方** ——
///   而 lan-edge 那一行的注释写着「主机界面据此报警」。轮换器 fail-closed 的最后一段路
///   (状态已经吐出来了)就断在这里:**吐出来而没人读,等于没响**。
///   另一条通道(stderr 的 `[cert] !!` 横幅)只落在那个控制台窗口里,
///   而用户平时看的是这个 WPF 客户端 —— 两条通道都到不了人眼前,轮换失败就会一路静默滑到过期。
///
/// ★ `NeedsAttention` **由主机算好后直接吐出**,客户端不自己再推一遍:
///   判据(连续失败次数 / 相位)在主机那边,重算一份就是给"两边说法相反"留门
///   (CertLifecycle 顶部那段注释说的就是这件事)。
/// </summary>
public sealed record ServerCertStatus(double DaysLeft, string Phase, int ConsecutiveFailures,
                                      string? LastError, bool NeedsAttention);

public sealed class HubAdmin
{
    /// <summary>
    /// 回环管理端口。★ 必须与 lan-edge 的 `AdminPort` 常量一致(`10-core/lan-edge/Program.cs`:
    /// `run-lan` 用 8442)。两边对不上的表现是"主机上也说不是主机" —— 所以这里把默认值写死并注明出处,
    /// 同时允许用环境变量覆盖,免得改端口要重编客户端。
    /// </summary>
    public const int DefaultAdminPort = 8442;

    public static int AdminPort
    {
        get
        {
            var s = Environment.GetEnvironmentVariable("LOCALAI_ADMIN_PORT");
            return int.TryParse(s, out var n) && n is > 0 and < 65536 ? n : DefaultAdminPort;
        }
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>
    /// 管理面的令牌。★ 现在的门禁只有"端口 + 回环",而【能连回环】的不止"坐在主机前的人" ——
    /// 浏览器里的一个网页、沙箱应用、同机的其它用户会话都满足。所以那句
    /// 「结构上只有坐在主机前的人能批准」并不成立(审计发现,已写进决议包请 core 补令牌)。
    /// ★ 这一侧先做好:令牌文件存在就带上自定义头(自定义头跨源发不出去,预检必失败)。
    ///   文件不存在就不带 —— 中枢还没升级时照常能用,不假装有一层不存在的保护。
    /// </summary>
    static string? AdminToken()
    {
        try
        {
            var p = Environment.GetEnvironmentVariable("LOCALAI_ADMIN_TOKEN_FILE");
            if (string.IsNullOrWhiteSpace(p))
            {
                var st = Environment.GetEnvironmentVariable("LOCALAI_STATE_DIR");
                if (string.IsNullOrWhiteSpace(st)) return null;
                p = Path.Combine(st, "secrets", "admin-token");
            }
            return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>给一个请求带上管理面令牌(有才带)。</summary>
    static void Stamp(HttpRequestMessage req)
    {
        if (AdminToken() is { Length: > 0 } t) req.Headers.TryAddWithoutValidation("X-LocalAI-Admin", t);
    }

    static string Base => $"http://127.0.0.1:{AdminPort}";

    /// <summary>上一次探测的结果 —— 界面据此决定显示"管理面"还是"这台不是主机"。</summary>
    public bool Available { get; private set; }

    /// <summary>上一次探测的**分类**。界面据此决定说哪一句 —— 见 AdminProbeResult 的说明。</summary>
    public AdminProbeResult LastProbe { get; private set; } = AdminProbeResult.Unknown;
    public string? HubId { get; private set; }
    public bool PairingWindowOpen { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>
    /// 主机侧服务器证书轮换的最新状态。null = 这次 ping 里主机没报(老中枢,或轮换器没装上)。
    /// ★ 「没报」与「报了说一切正常」是两件事,所以用 null 区分,不塞一个假的"健康"进去。
    /// </summary>
    public ServerCertStatus? ServerCert { get; private set; }

    /// <summary>
    /// 主机侧证书要不要惊动人 —— 界面直接读这一句。null = 不必打扰。
    /// ★ 措辞里必须**同时**有剩余天数和该做什么:只说"需要注意"的告警等于没说。
    /// </summary>
    public string? ServerCertWarning => ServerCert is { NeedsAttention: true } s
        ? $"主机的服务器证书还有 {s.DaysLeft:0.#} 天到期,自动续签"
          + (s.ConsecutiveFailures > 0 ? $"**已连续失败 {s.ConsecutiveFailures} 次**" : "尚未把它续上")
          + (string.IsNullOrWhiteSpace(s.LastError) ? "" : $"(最后一次的错误:{s.LastError})")
          + " —— 请在主机上执行 localai-identity renew-server。"
          + "★ 不必重新配对:CA 不变,已配对的设备全部照常有效。"
        : null;

    /// <summary>
    /// 探测回环管理面。★ 只有【连得上】且【hubId 与本机档案一致】才算可用。
    /// 未配对时没有档案可比,这时只要连得上就认 —— 那正是"主机第一次自己配自己"的场景。
    /// </summary>
    public async Task<bool> ProbeAsync(string? expectHubId)
    {
        Available = false; HubId = null; LastError = null; ServerCert = null; LastProbe = AdminProbeResult.Unknown;
        try
        {
            using var ping = new HttpRequestMessage(HttpMethod.Get, Base + "/admin/ping");
            Stamp(ping);
            using var r = await Http.SendAsync(ping);
            if (!r.IsSuccessStatusCode)
            {
                LastProbe = AdminProbeResult.HttpError;
                LastError = $"管理面回了 {(int)r.StatusCode}";
                return false;
            }
            var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            // ★ 顶层解析走 ParsePing 那一处(与断言喂的是同一个函数),不在这里另写一遍。
            var (pok, pid, pwin, pwhy) = ParsePing(j);
            if (!pok) { LastProbe = AdminProbeResult.HttpError; LastError = pwhy; return false; }
            HubId = pid;
            PairingWindowOpen = pwin;
            ServerCert = ParseServerCert(j);
            if (!string.IsNullOrWhiteSpace(expectHubId) && !string.Equals(HubId, expectHubId, StringComparison.Ordinal))
            {
                // 连得上但不是【我们这个】中枢 —— 同机跑着另一个中枢时会这样。如实说,不糊弄过去。
                LastProbe = AdminProbeResult.WrongHub;
                LastError = $"这台机器上的管理面属于另一个中枢({HubId}),不是本机配对的那个";
                return false;
            }
            Available = true;
            LastProbe = AdminProbeResult.Ok;
            return true;
        }
        catch (Exception ex)
        {
            // ★ 只说观察到的:连接被拒 = 没人在听。这句话【到此为止】——
            //   是不是主机、要不要去启动 Edge,由界面结合别的线索去说,这里不下结论。
            var refused = ex is HttpRequestException
                          && ex.InnerException is System.Net.Sockets.SocketException se
                          && se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused;
            if (refused)
            {
                LastProbe = AdminProbeResult.NotListening;
                LastError = $"127.0.0.1:{AdminPort} 上没有人听 —— 中枢没在这台机器上运行";
            }
            else if (ex is TaskCanceledException or OperationCanceledException)
            {
                LastProbe = AdminProbeResult.Timeout;
                LastError = $"127.0.0.1:{AdminPort} 在 {(int)Http.Timeout.TotalSeconds} 秒内没有答复";
            }
            else
            {
                LastProbe = AdminProbeResult.Unknown;
                LastError = ex.Message;
            }
            return false;
        }
    }

    /// <summary>
    /// 从 <c>/admin/ping</c> 的响应里取出 <c>serverCert</c>。**这就是 D92 那条成对断言的客户端半边** ——
    /// 主机侧钉「这个子对象有哪些顶层键」,这里钉「拿那个形状能不能解析出目标字段」。
    ///
    /// ★★ A1 就是这条缝漏出来的:服务端把 `lease_id` 放在 `body["lease"]["lease_id"]`,
    ///   客户端在**顶层**找;两边各自都有断言、各自都绿,**中间那条缝谁也没看**。
    ///   所以这个方法被单独提出来、做成 <c>internal static</c>:断言能拿一段**真的 JSON 原文**喂它,
    ///   而不是去测一个仿造的解析器(测仿造品的话,真解析器改坏了也不会红)。
    ///
    /// ★ 主机没报(老中枢 / 轮换器没装)时返回 null —— 不编一个"健康"出来。
    /// ★ 键缺一个就整条判 null:半份状态比没有状态更坏(会显示一个可信但错误的天数)。
    /// </summary>
    internal static ServerCertStatus? ParseServerCert(JsonElement ping)
    {
        if (!ping.TryGetProperty("serverCert", out var sc) || sc.ValueKind != JsonValueKind.Object) return null;
        try
        {
            // ★★ 拿**登记表**核对键集合,而不是只挑自己要用的那几个键。
            //   只挑要用的会放过"服务端把字段搬了家、顺手改了别的键名"这一整类改动 ——
            //   而那正是 A1 的形状。认不出的形状一律判 null:**半份状态比没有状态更坏**,
            //   它会在界面上显示一个可信但错误的天数,而人会照着它决定要不要动手。
            if (!LocalAI.Identity.WireContracts.KeysMatch(
                    sc.EnumerateObject().Select(x => x.Name), LocalAI.Identity.WireContracts.AdminPingServerCert))
                return null;
            if (!sc.TryGetProperty("daysLeft", out var d) || !sc.TryGetProperty("phase", out var ph)
                || !sc.TryGetProperty("consecutiveFailures", out var cf)
                || !sc.TryGetProperty("needsAttention", out var na)) return null;
            return new ServerCertStatus(
                d.GetDouble(),
                ph.GetString() ?? "",
                cf.GetInt32(),
                sc.TryGetProperty("lastError", out var le) && le.ValueKind == JsonValueKind.String ? le.GetString() : null,
                na.GetBoolean());
        }
        catch { return null; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  D?(V4)· /admin/devices 与 /admin/devices/revoke 的**唯一**解析处
    // ══════════════════════════════════════════════════════════════════════════
    //  ★★ 这两条在欠债表里被单独点了名,而且点的**不是**"欠一条断言",是一个缺陷:
    //    · /admin/devices 有**两个**解析器 —— `HubAdmin.DevicesAsync` 与
    //      `HubClient.ParseDevices`,分别被 DevicesView 的两条路径调用(:912 与 :1366)。
    //      两份代码解析同一个形状 ⇒ **服务端改一个键名,只会有一处被发现**,
    //      而另一处会安静地退化成"设备名全空 / 列表全空",看起来像"主机上没有别的设备"。
    //    · /admin/devices/revoke 有**两个调用方**(:1327 与 :1381),而**两个都不看应答体** ——
    //      于是一次失败的吊销与一次成功的吊销在界面上长得一模一样。
    //      对"解除设备"这种动作,静默失败的代价是:人以为那台机器已经被踢掉了,其实它还连得上。
    //  ⇒ 解析收拢到这里一处,两边都调它;形状核对走 WireContracts,与服务端同一份登记表。

    /// <summary>
    /// 解析 <c>GET /admin/devices</c>。**全客户端唯一的一处** —— <c>HubClient.ParseDevices</c> 也走它。
    /// ★ 顶层与**元素**两层都核对:客户端真正要用的字段(deviceId/displayName/status/certSha256Short)
    ///   全在元素那一层,而 A1 的病灶正是"字段藏在下一层"。只钉顶层等于没钉。
    /// </summary>
    internal static (bool ok, List<AdminDevice> list, string? why) ParseDevices(JsonElement j)
    {
        var list = new List<AdminDevice>();
        if (!LocalAI.Identity.WireContracts.KeysMatch(
                j.EnumerateObject().Select(x => x.Name), LocalAI.Identity.WireContracts.AdminDevices))
            return (false, list, "设备表的顶层键与登记的契约对不上("
                    + LocalAI.Identity.WireContracts.Describe(
                        j.EnumerateObject().Select(x => x.Name), LocalAI.Identity.WireContracts.AdminDevices) + ")");
        if (!j.TryGetProperty("devices", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return (false, list, "设备表里没有 devices 数组");
        foreach (var d in arr.EnumerateArray())
        {
            if (!LocalAI.Identity.WireContracts.KeysMatch(
                    d.EnumerateObject().Select(x => x.Name), LocalAI.Identity.WireContracts.AdminDevicesItem))
                // ★ 认不出的元素**整条判失败**,不挑着能读的字段拼一个出来 ——
                //   拼出来的那一条会显示成一台"名字是空的设备",而人分不清它是漂移还是真的没名字。
                return (false, list, "设备条目的键与登记的契约对不上("
                        + LocalAI.Identity.WireContracts.Describe(
                            d.EnumerateObject().Select(x => x.Name), LocalAI.Identity.WireContracts.AdminDevicesItem) + ")");
            list.Add(new AdminDevice(
                d.GetProperty("deviceId").GetString() ?? "",
                d.GetProperty("displayName").GetString() ?? "",
                d.GetProperty("status").GetString() ?? "",
                d.GetProperty("certSha256Short").ValueKind == JsonValueKind.String
                    ? d.GetProperty("certSha256Short").GetString() : null));
        }
        return (true, list, null);
    }

    /// <summary>
    /// 解析 <c>POST /admin/devices/revoke</c> 的 200 应答。**两个调用方共用这一处**。
    /// ★ <c>generation</c> 是"这次吊销真的落盘了"的凭据 —— 只看 HTTP 200 是不够的:
    ///   200 只说明路由跑到了,而 <c>Store.Mutate</c> 的结果就在这个数字里。
    /// </summary>
    internal static (bool ok, int generation, string? why) ParseRevoke(JsonElement j)
    {
        if (!LocalAI.Identity.WireContracts.KeysMatch(
                j.EnumerateObject().Select(x => x.Name), LocalAI.Identity.WireContracts.AdminRevoke))
            return (false, 0, "吊销应答的顶层键与登记的契约对不上("
                    + LocalAI.Identity.WireContracts.Describe(
                        j.EnumerateObject().Select(x => x.Name), LocalAI.Identity.WireContracts.AdminRevoke) + ")");
        if (!j.GetProperty("ok").GetBoolean()) return (false, 0, "中枢说这次吊销没成功(ok=false)");
        return (true, j.GetProperty("generation").GetInt32(), null);
    }

    /// <summary>把一段应答正文喂给 <see cref="ParseRevoke"/>;正文不是 JSON 时如实报,不当成功。</summary>
    internal static (bool ok, int generation, string? why) ParseRevokeBody(string body)
    {
        try { return ParseRevoke(JsonDocument.Parse(body).RootElement); }
        catch (Exception ex) { return (false, 0, "吊销应答读不懂:" + ex.Message); }
    }

    async Task<(int status, string body)> Call(HttpMethod m, string path, object? body = null)
    {
        using var req = new HttpRequestMessage(m, Base + path);
        Stamp(req);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var r = await Http.SendAsync(req);
        return ((int)r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    // ---------------------------------------------------------------- 配对(S4 的正题)
    /// <summary>
    /// 待批准的配对请求。到点(SecondsLeft ≤ 0)的由主机侧自己失效,界面只管别再显示。
    ///
    /// ★★ 返回 (ok, list) 而不是光返回 list:取失败时列表**也是空的**,
    ///   而"空"在界面上会被写成「现在没有等待批准的请求」—— 副机那边正卡着等批准,
    ///   人看到这句就断定请求没发过来,回去把配对重来一遍。重配会删掉副机私钥,
    ///   还在主机侧留下幽灵条目。所以这两件事**必须**在类型上就分开,不能靠调用方记得看 LastError。
    /// </summary>
    public async Task<(bool ok, List<PendingPair> list)> PendingAsync()
    {
        var list = new List<PendingPair>();
        int st; string body;
        try { (st, body) = await Call(HttpMethod.Get, "/admin/pairing/pending"); }
        catch (Exception ex) { LastError = "取待批准列表失败:" + ex.Message; return (false, list); }
        if (st != 200) { LastError = $"取待批准列表失败({st})"; return (false, list); }
        JsonElement j;
        try { j = JsonDocument.Parse(body).RootElement; }
        catch (Exception ex) { LastError = "待批准列表读不懂:" + ex.Message; return (false, list); }
        var (pok, plist, pwhy, pwin) = ParsePending(j);
        if (!pok) { LastError = pwhy; return (false, list); }
        PairingWindowOpen = pwin;
        return (true, plist);
    }

    /// <summary>
    /// 解析 <c>GET /admin/pairing/pending</c>。顶层与**元素**两层都核对 ——
    /// ★ 六个词就在元素那一层,而它们是整套配对安全的根基:
    ///   `sas` 一旦漂成别的键名,界面会显示**空的六个词**,而人会以为"还没生成",
    ///   于是去点重来 —— 那会在主机侧留下幽灵条目。
    /// </summary>
    internal static (bool ok, List<PendingPair> list, string? why, bool windowOpen) ParsePending(JsonElement j)
    {
        var list = new List<PendingPair>();
        if (!LocalAI.Identity.WireContracts.KeysMatch(
                j.EnumerateObject().Select(x => x.Name), LocalAI.Identity.WireContracts.AdminPending))
            return (false, list, "待批准列表的顶层键与登记的契约对不上("
                    + LocalAI.Identity.WireContracts.Describe(
                        j.EnumerateObject().Select(x => x.Name), LocalAI.Identity.WireContracts.AdminPending) + ")", false);
        var win = j.GetProperty("pairingWindowOpen").GetBoolean();
        if (!j.TryGetProperty("pending", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return (false, list, "待批准列表里没有 pending 数组", win);
        foreach (var p in arr.EnumerateArray())
        {
            if (!LocalAI.Identity.WireContracts.KeysMatch(
                    p.EnumerateObject().Select(x => x.Name), LocalAI.Identity.WireContracts.AdminPendingItem))
                return (false, list, "待批准条目的键与登记的契约对不上("
                        + LocalAI.Identity.WireContracts.Describe(
                            p.EnumerateObject().Select(x => x.Name), LocalAI.Identity.WireContracts.AdminPendingItem) + ")", win);
            var sas = new List<string>();
            if (p.GetProperty("sas").ValueKind == JsonValueKind.Array)
                foreach (var x in p.GetProperty("sas").EnumerateArray()) if (x.GetString() is { } t) sas.Add(t);
            list.Add(new PendingPair(
                p.GetProperty("requestId").GetString() ?? "",
                p.GetProperty("displayName").GetString() ?? "",
                sas.ToArray(),
                p.GetProperty("secondsLeft").GetInt32()));
        }
        return (true, list, null, win);
    }

    /// <summary>
    /// 解析 <c>/admin/ping</c> 的顶层。★ <c>pairingWindowOpen</c> 从这里来 ——
    /// 读不出来就只能退回"拿本地布尔替中枢记配对窗口开没开",而那是 Selftest.cs 明令禁止的
    /// (本地布尔与中枢的真实状态一旦分家,界面会显示一个敞开着、实际已经关掉的窗口,反之亦然)。
    /// </summary>
    internal static (bool ok, string? hubId, bool windowOpen, string? why) ParsePing(JsonElement j)
    {
        if (!LocalAI.Identity.WireContracts.KeysMatch(
                j.EnumerateObject().Select(x => x.Name), LocalAI.Identity.WireContracts.AdminPing))
            return (false, null, false, "/admin/ping 的顶层键与登记的契约对不上("
                    + LocalAI.Identity.WireContracts.Describe(
                        j.EnumerateObject().Select(x => x.Name), LocalAI.Identity.WireContracts.AdminPing) + ")");
        return (true, j.GetProperty("hubId").GetString(), j.GetProperty("pairingWindowOpen").GetBoolean(), null);
    }

    /// <summary>
    /// 解析批准/拒绝的应答 —— **200 与 409 都走这一处**。
    /// ★ 409 才是常见分支(请求过期了、已经批过了),而它的 <c>error</c> 是界面唯一能说的原因。
    ///   只看状态码的话,界面只能写"中枢拒绝了",人会以为中枢坏了、去重启一个没病的中枢。
    /// </summary>
    internal static (bool ok, string? error, string? why) ParseAck(JsonElement j)
    {
        var keys = j.EnumerateObject().Select(x => x.Name).ToArray();
        var ok200 = LocalAI.Identity.WireContracts.KeysMatch(keys, LocalAI.Identity.WireContracts.AdminApprove);
        var ok409 = LocalAI.Identity.WireContracts.KeysMatch(keys, LocalAI.Identity.WireContracts.AdminApproveDeny409);
        if (!ok200 && !ok409)
            return (false, null, "批准/拒绝应答的顶层键既不是 200 那组也不是 409 那组("
                    + LocalAI.Identity.WireContracts.Describe(keys, LocalAI.Identity.WireContracts.AdminApproveDeny409) + ")");
        var okFlag = j.GetProperty("ok").GetBoolean();
        return (okFlag, ok409 ? j.GetProperty("error").GetString() : null, null);
    }

    /// <summary>解析 <c>POST /admin/pairing/window</c> 的应答,并回带**中枢自报的**窗口状态。</summary>
    internal static (bool ok, bool windowOpen, string? why) ParseWindow(JsonElement j)
    {
        if (!LocalAI.Identity.WireContracts.KeysMatch(
                j.EnumerateObject().Select(x => x.Name), LocalAI.Identity.WireContracts.AdminWindow))
            return (false, false, "开关窗应答的顶层键与登记的契约对不上("
                    + LocalAI.Identity.WireContracts.Describe(
                        j.EnumerateObject().Select(x => x.Name), LocalAI.Identity.WireContracts.AdminWindow) + ")");
        return (j.GetProperty("ok").GetBoolean(), j.GetProperty("pairingWindowOpen").GetBoolean(), null);
    }

    /// <summary>批准一条配对请求。★ 应答体走 <see cref="ParseAck"/>,409 的原因记进 LastError。</summary>
    public async Task<(int status, string body)> ApproveAsync(string requestId)
        => await AckCall("/admin/pairing/approve", requestId);

    /// <summary>拒绝一条配对请求。同上。</summary>
    public async Task<(int status, string body)> DenyAsync(string requestId)
        => await AckCall("/admin/pairing/deny", requestId);

    async Task<(int status, string body)> AckCall(string path, string requestId)
    {
        var r = await Call(HttpMethod.Post, path, new { requestId });
        try
        {
            var (ok, err, why) = ParseAck(JsonDocument.Parse(r.body).RootElement);
            LastError = why ?? (ok ? null : (err ?? "中枢没说原因"));
        }
        catch (Exception ex) { LastError = "应答读不懂:" + ex.Message; }
        return r;
    }

    /// <summary>
    /// 开/关配对窗口。★ 窗口【不随启动自动打开】(主机侧审查结论:开机自启 + 无条件开窗
    /// = 每次开机在局域网上敞开一个无人值守的准入窗口)。所以这里是显式动作,且有分钟数上限。
    /// ★★ 应答里带**中枢自报的**当前窗口状态,这里立刻把它记下 ——
    ///   否则只能拿本地布尔猜,而那正是 Selftest.cs 明令禁止的那件事。
    /// </summary>
    public async Task<(int status, string body)> WindowAsync(bool open, int minutes = 10)
    {
        var r = await Call(HttpMethod.Post, "/admin/pairing/window", new { open, minutes });
        if (r.status == 200)
            try
            {
                var (ok, win, why) = ParseWindow(JsonDocument.Parse(r.body).RootElement);
                if (why is not null) LastError = why; else if (ok) PairingWindowOpen = win;
            }
            catch (Exception ex) { LastError = "开关窗应答读不懂:" + ex.Message; }
        return r;
    }

    // ---------------------------------------------------------------- 设备
    /// <summary>已在册的设备。★ 同 PendingAsync:返回 (ok, list),不让"取不到"伪装成"一台都没有"。</summary>
    public async Task<(bool ok, List<AdminDevice> list)> DevicesAsync()
    {
        var list = new List<AdminDevice>();
        int st; string body;
        try { (st, body) = await Call(HttpMethod.Get, "/admin/devices"); }
        catch (Exception ex) { LastError = "取设备列表失败:" + ex.Message; return (false, list); }
        if (st != 200) { LastError = $"取设备列表失败({st})"; return (false, list); }
        JsonElement j;
        try { j = JsonDocument.Parse(body).RootElement; }
        catch (Exception ex) { LastError = "设备列表读不懂:" + ex.Message; return (false, list); }
        // ★ 解析走**唯一**那一处(见 ParseDevices 上面那段):此前这里和 HubClient.ParseDevices
        //   各写了一份,服务端改一个键名只会有一处被发现。
        var (ok, parsed, why) = ParseDevices(j);
        if (!ok) { LastError = why; return (false, list); }
        return (true, parsed);
    }

    /// <summary>
    /// 吊销一台设备。★ 返回值保持 (status, body) 不变(界面那两处按它写的),
    /// 但**应答体现在真的被核对了** —— 形状不对或 ok=false 时记进 <see cref="LastError"/>。
    /// 此前两个调用方都不看应答体,一次失败的吊销与一次成功的吊销在界面上长得一模一样。
    /// </summary>
    public async Task<(int status, string body)> RevokeAsync(string deviceId)
    {
        var r = await Call(HttpMethod.Post, "/admin/devices/revoke", new { deviceId });
        if (r.status == 200)
        {
            var (ok, gen, why) = ParseRevokeBody(r.body);
            LastError = ok ? null : why;
            if (ok && gen <= 0) LastError = "吊销应答里的 generation 不是正数 —— 这次写盘可能没生效";
        }
        return r;
    }

    // ---------------------------------------------------------------- 本机的中枢在哪个地址上听
    /// <summary>业务口(LAN Edge)的端口。★ 与 lan-edge `run-lan` 里的 `8443` 一致。</summary>
    public const int EdgePort = 8443;

    /// <summary>
    /// 本机就是主机时,**自己探出**中枢的拨号地址,不必让人手填。
    ///
    /// ★ 为什么不能填 `127.0.0.1:8443`:`run-lan &lt;ip&gt;` 把业务口**只绑在那张网卡的 IP 上**
    ///   (`k.Listen(cfg.Bind, 8443, …)`),回环上只有管理面(8442)。
    ///   往 127.0.0.1:8443 拨是连不上的 —— 这正是「主机上也要填一个看起来很奇怪的局域网 IP」的由来。
    ///
    /// ★★ "TCP 连得上"**不是**肯定证据 —— 这句话以前就写在这儿,是错的。它只证明
    ///   「这个地址的 8443 上有个监听者」:8443 是最常见的备用 HTTPS 口,本机可能有别的东西占着
    ///   (VirtualBox 之类的端口转发就常绑主机端口)。所以每个应答地址还要走一次 TLS 握手、
    ///   读证书名,要求 hub_id 与本机这个中枢一致 —— 那才是肯定证据。
    /// ★★ 而且**不替用户挑**:本机常有不止一张网卡(如 VirtualBox 的 192.168.56.1 仅主机适配器),
    ///   撞上第一个能连的就 return,等于静默选了一个**只有本机看得见**的地址,
    ///   还会被写进配对档案、被抄到副机上 —— 副机永远连不上,而人只会去查网线和路由器。
    ///   全仓的规矩是"找到多个绝不替用户挑",这里以前是唯一的例外。
    /// ★ 一个都探不到就返回空表 —— 界面如实说"没探到",绝不猜一个填进去。
    /// </summary>
    public static async Task<List<string>> DiscoverEdgeDialsAsync(string? expectHubId, int timeoutMs = 400)
    {
        var want = HubDiscovery.ShortHubId(expectHubId);
        var hits = new List<string>();
        foreach (var ip in LocalIPv4())
        {
            try
            {
                using var sock = new System.Net.Sockets.TcpClient();
                var connect = sock.ConnectAsync(ip, EdgePort);
                if (await Task.WhenAny(connect, Task.Delay(timeoutMs)) != connect || !sock.Connected) continue;
            }
            catch { continue; }   // 这张网卡不通就换下一张 —— 探测失败不是错误

            // ★ 连得上还不够:读证书名,认出是我们这个中枢才算数
            FoundHub? probed = null;
            try { probed = await HubDiscovery.ProbeOneAsync(ip, EdgePort, timeoutMs); }
            catch { /* 握手失败:8443 上蹲着的多半是别的东西 */ }
            if (probed is null) continue;
            if (want is not null && !string.Equals(probed.HubId, want, StringComparison.OrdinalIgnoreCase)) continue;
            hits.Add($"{ip}:{EdgePort}");
        }
        return hits;
    }

    // ---------------------------------------------------------------- 本机【能不能】当主机(线索,不是判据)
    /// <summary>
    /// 本机上有没有主机端程序。★ 这是一条**线索**,不是判据 ——
    /// 它回答的是「这台**能不能**当主机」,不是「这台**是不是正在**当主机」;
    /// 后者只有回环管理面答话才算数(见 ProbeAsync)。
    ///
    /// 依据出包布局:`dist\client\localai-client.exe` 与 `dist\host\localai-lan-edge.exe` 并排。
    /// 副机上只装 client-pack、没有 host 目录 ⇒ 找不到就说明这台多半真的不是主机。
    /// 开发树里跑(bin\Debug\…)也找不到 —— 那时就当没有这条线索,界面照样能说清楚。
    /// </summary>
    public static string? HostToolsDir()
    {
        // ★ 单文件发布下 Environment.ProcessPath 才是真正的 exe 路径(BaseDirectory 可能指向解包目录)
        try { return HostToolsDirNextTo(Path.GetDirectoryName(Environment.ProcessPath)); }
        catch { return null; }   // 路径拿不到就是没这条线索,不是错误
    }

    /// <summary>
    /// 上面那条的纯逻辑部分:给定客户端 exe 所在目录,看它旁边有没有 `..\host\localai-lan-edge.exe`。
    /// ★ 抽出来是为了能**确定性地**测:直接断言 HostToolsDir() 的返回值等于在断言
    ///   「自检此刻跑在哪个目录下」—— 那个断言在 dist\client 里会红、在 dist\client-pack 里会绿,
    ///   两边都不说明代码对不对。(这个坑本次真踩了一回。)
    /// </summary>
    public static string? HostToolsDirNextTo(string? clientExeDir)
    {
        if (string.IsNullOrWhiteSpace(clientExeDir)) return null;
        var host = Path.GetFullPath(Path.Combine(clientExeDir, "..", "host"));
        return File.Exists(Path.Combine(host, "localai-lan-edge.exe")) ? host : null;
    }

    /// <summary>主机端的启动脚本(存在才返回)。界面用它把"去点哪个文件"直接说出来。</summary>
    public static string? StartEdgeCmd()
    {
        var d = HostToolsDir();
        if (d is null) return null;
        var p = Path.Combine(d, "启动Edge.cmd");
        return File.Exists(p) ? p : null;
    }

    /// <summary>本机当前启用的非回环 IPv4。★ 界面要把它摆出来供人和 Edge 窗口里那行对照。</summary>
    public static List<string> LocalIPv4List() => LocalIPv4().ToList();

    /// <summary>本机的 IPv4(跳过回环与未启用的网卡)。</summary>
    static IEnumerable<string> LocalIPv4()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        System.Net.NetworkInformation.NetworkInterface[] nics;
        try { nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces(); }
        catch { yield break; }
        foreach (var n in nics)
        {
            if (n.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            System.Net.NetworkInformation.UnicastIPAddressInformationCollection addrs;
            try { addrs = n.GetIPProperties().UnicastAddresses; }
            catch { continue; }
            foreach (var a in addrs)
            {
                if (a.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                var s = a.Address.ToString();
                if (s.StartsWith("169.254.", StringComparison.Ordinal)) continue;   // APIPA:没拿到 DHCP 的自封地址
                if (seen.Add(s)) yield return s;
            }
        }
    }
}
