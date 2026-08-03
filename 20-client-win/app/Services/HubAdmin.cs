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

    static string Base => $"http://127.0.0.1:{AdminPort}";

    /// <summary>上一次探测的结果 —— 界面据此决定显示"管理面"还是"这台不是主机"。</summary>
    public bool Available { get; private set; }

    /// <summary>上一次探测的**分类**。界面据此决定说哪一句 —— 见 AdminProbeResult 的说明。</summary>
    public AdminProbeResult LastProbe { get; private set; } = AdminProbeResult.Unknown;
    public string? HubId { get; private set; }
    public bool PairingWindowOpen { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>
    /// 探测回环管理面。★ 只有【连得上】且【hubId 与本机档案一致】才算可用。
    /// 未配对时没有档案可比,这时只要连得上就认 —— 那正是"主机第一次自己配自己"的场景。
    /// </summary>
    public async Task<bool> ProbeAsync(string? expectHubId)
    {
        Available = false; HubId = null; LastError = null; LastProbe = AdminProbeResult.Unknown;
        try
        {
            using var r = await Http.GetAsync(Base + "/admin/ping");
            if (!r.IsSuccessStatusCode)
            {
                LastProbe = AdminProbeResult.HttpError;
                LastError = $"管理面回了 {(int)r.StatusCode}";
                return false;
            }
            var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            HubId = j.TryGetProperty("hubId", out var h) ? h.GetString() : null;
            PairingWindowOpen = j.TryGetProperty("pairingWindowOpen", out var w) && w.GetBoolean();
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

    async Task<(int status, string body)> Call(HttpMethod m, string path, object? body = null)
    {
        using var req = new HttpRequestMessage(m, Base + path);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var r = await Http.SendAsync(req);
        return ((int)r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    // ---------------------------------------------------------------- 配对(S4 的正题)
    /// <summary>待批准的配对请求。到点(SecondsLeft ≤ 0)的由主机侧自己失效,界面只管别再显示。</summary>
    public async Task<List<PendingPair>> PendingAsync()
    {
        var (st, body) = await Call(HttpMethod.Get, "/admin/pairing/pending");
        var list = new List<PendingPair>();
        if (st != 200) { LastError = $"取待批准列表失败({st})"; return list; }
        var j = JsonDocument.Parse(body).RootElement;
        PairingWindowOpen = j.TryGetProperty("pairingWindowOpen", out var w) && w.GetBoolean();
        if (!j.TryGetProperty("pending", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var p in arr.EnumerateArray())
        {
            var sas = new List<string>();
            if (p.TryGetProperty("sas", out var s) && s.ValueKind == JsonValueKind.Array)
                foreach (var x in s.EnumerateArray()) if (x.GetString() is { } t) sas.Add(t);
            list.Add(new PendingPair(
                p.GetProperty("requestId").GetString() ?? "",
                p.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "",
                sas.ToArray(),
                p.TryGetProperty("secondsLeft", out var sl) ? sl.GetInt32() : 0));
        }
        return list;
    }

    public Task<(int status, string body)> ApproveAsync(string requestId)
        => Call(HttpMethod.Post, "/admin/pairing/approve", new { requestId });

    public Task<(int status, string body)> DenyAsync(string requestId)
        => Call(HttpMethod.Post, "/admin/pairing/deny", new { requestId });

    /// <summary>
    /// 开/关配对窗口。★ 窗口【不随启动自动打开】(主机侧审查结论:开机自启 + 无条件开窗
    /// = 每次开机在局域网上敞开一个无人值守的准入窗口)。所以这里是显式动作,且有分钟数上限。
    /// </summary>
    public Task<(int status, string body)> WindowAsync(bool open, int minutes = 10)
        => Call(HttpMethod.Post, "/admin/pairing/window", new { open, minutes });

    // ---------------------------------------------------------------- 设备
    public async Task<List<AdminDevice>> DevicesAsync()
    {
        var (st, body) = await Call(HttpMethod.Get, "/admin/devices");
        var list = new List<AdminDevice>();
        if (st != 200) { LastError = $"取设备列表失败({st})"; return list; }
        var j = JsonDocument.Parse(body).RootElement;
        if (!j.TryGetProperty("devices", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var d in arr.EnumerateArray())
            list.Add(new AdminDevice(
                d.GetProperty("deviceId").GetString() ?? "",
                d.TryGetProperty("displayName", out var n) ? n.GetString() ?? "" : "",
                d.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "",
                d.TryGetProperty("certSha256Short", out var f) ? f.GetString() : null));
        return list;
    }

    public Task<(int status, string body)> RevokeAsync(string deviceId)
        => Call(HttpMethod.Post, "/admin/devices/revoke", new { deviceId });

    // ---------------------------------------------------------------- 本机的中枢在哪个地址上听
    /// <summary>业务口(LAN Edge)的端口。★ 与 lan-edge `run-lan` 里的 `8443` 一致。</summary>
    public const int EdgePort = 8443;

    /// <summary>
    /// 本机就是主机时,**自己探出**中枢的拨号地址,不必让人手填。
    ///
    /// ★ 为什么不能填 `127.0.0.1:8443`:`run-lan &lt;ip&gt;` 把业务口**只绑在那张网卡的 IP 上**
    ///   (`k.Listen(cfg.Bind, 8443, …)`),回环上只有管理面(8442)。
    ///   往 127.0.0.1:8443 拨是连不上的 —— 这正是「主机上也要填一个看起来很奇怪的局域网 IP」的由来。
    /// ★ 所以:枚举本机的 IPv4,挨个试 8443 能不能连上,谁应答就是它。
    ///   拿的是**肯定证据**(TCP 连得上),不是"哪个 IP 看着像"。
    /// ★ 探不到就返回 null —— 让界面如实说"没探到,请照 Edge 窗口里那行填",绝不猜一个填进去。
    /// </summary>
    public static async Task<string?> DiscoverEdgeDialAsync(int timeoutMs = 400)
    {
        foreach (var ip in LocalIPv4())
        {
            try
            {
                using var sock = new System.Net.Sockets.TcpClient();
                var connect = sock.ConnectAsync(ip, EdgePort);
                if (await Task.WhenAny(connect, Task.Delay(timeoutMs)) == connect && sock.Connected)
                    return $"{ip}:{EdgePort}";
            }
            catch { /* 这张网卡不通就换下一张 —— 探测失败不是错误 */ }
        }
        return null;
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
        try
        {
            // ★ 单文件发布下 Environment.ProcessPath 才是真正的 exe 路径(BaseDirectory 可能指向解包目录)
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return null;
            var dir = Path.GetDirectoryName(exe);
            if (dir is null) return null;
            var host = Path.GetFullPath(Path.Combine(dir, "..", "host"));
            return File.Exists(Path.Combine(host, "localai-lan-edge.exe")) ? host : null;
        }
        catch { return null; }   // 路径拿不到就是没这条线索,不是错误
    }

    /// <summary>主机端的启动脚本(存在才返回)。界面用它把"去点哪个文件"直接说出来。</summary>
    public static string? StartEdgeCmd()
    {
        var d = HostToolsDir();
        if (d is null) return null;
        var p = Path.Combine(d, "启动Edge.cmd");
        return File.Exists(p) ? p : null;
    }

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
