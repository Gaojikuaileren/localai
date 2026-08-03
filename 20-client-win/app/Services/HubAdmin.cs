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
//   拿到肯定证据才认 —— 否则(端口不通 / hubId 对不上)就如实说"这台不是主机"。
//   这比"拨号地址看着像本机"强:同一台机器上可能跑着另一个中枢,那时 hubId 就对不上。
//
// ★ 不做 mTLS:管理面的门禁是**端口 + 回环**,不是证书。在回环上再套一层客户端证书
//   既不增加安全性(能连回环就已经在这台机器上了),又会把"主机自己管自己"绑死在
//   "必须先配对成功"上 —— 而配对界面本身就归它管,那会变成鸡生蛋。

using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace LocalAI.Client.Services;

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
    public string? HubId { get; private set; }
    public bool PairingWindowOpen { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>
    /// 探测回环管理面。★ 只有【连得上】且【hubId 与本机档案一致】才算可用。
    /// 未配对时没有档案可比,这时只要连得上就认 —— 那正是"主机第一次自己配自己"的场景。
    /// </summary>
    public async Task<bool> ProbeAsync(string? expectHubId)
    {
        Available = false; HubId = null; LastError = null;
        try
        {
            using var r = await Http.GetAsync(Base + "/admin/ping");
            if (!r.IsSuccessStatusCode) { LastError = $"管理面回了 {(int)r.StatusCode}"; return false; }
            var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            HubId = j.TryGetProperty("hubId", out var h) ? h.GetString() : null;
            PairingWindowOpen = j.TryGetProperty("pairingWindowOpen", out var w) && w.GetBoolean();
            if (!string.IsNullOrWhiteSpace(expectHubId) && !string.Equals(HubId, expectHubId, StringComparison.Ordinal))
            {
                // 连得上但不是【我们这个】中枢 —— 同机跑着另一个中枢时会这样。如实说,不糊弄过去。
                LastError = $"这台机器上的管理面属于另一个中枢({HubId}),不是本机配对的那个";
                return false;
            }
            Available = true;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex is TaskCanceledException ? "管理面没响应(这台多半不是主机)" : ex.Message;
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
}
