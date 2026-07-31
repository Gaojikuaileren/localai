// P3c -- 与中枢的连接。用户要求:「不要每次开启就要配对一次,而是一开始配对一次之后就记住」。
//
// 因此:配对产物(profile.json,含 CA、设备证书、CNG 密钥名、**拨号地址**)持久化在本机;
// 之后每次启动**只读档案直接连**,不再走配对流程。只有 ① 从未配对 ② 用户主动解除 ③ 主机吊销了本机
// 这三种情况才需要(重新)配对。
//
// 复用 P3b 的 LocalAI.ClientTransport.Transport(链接源码,见 csproj),不重写 mTLS/CNG 逻辑。

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using LocalAI.ClientTransport;

namespace LocalAI.Client.Services;

// CertExpired 单列一态:证书过期的症状与"连不上"完全一样,但处置办法南辕北辙
// (一个要在主机上续签,一个是等中枢开机)。混为一谈会让用户去点「重新配对」而销毁有效身份。
public enum HubState { NotPaired, Connecting, Online, Offline, Revoked, CertExpired, Unauthorized }

/// <summary>主机成员库里的一台设备。DisplayName 是设备**自报**的,只作显示、永不进 prompt。</summary>
public sealed record HubDevice(string DeviceId, string DisplayName, string Status);

public sealed class HubClient
{
    static readonly JsonSerializerOptions J = new() { WriteIndented = true };

    public ClientProfile? Profile { get; private set; }
    public HubState State { get; private set; } = HubState.NotPaired;
    public string? LastError { get; private set; }

    public bool IsPaired => Profile is not null;

    /// <summary>
    /// 本机是否【就是中枢主机】—— 启发式:配对的拨号地址指向本机(回环或本机某个网卡 IP)。
    /// 主机端的客户端配对到 127.0.0.1/本机 IP;副机配对到主机的 LAN IP。仅用于状态显示,不做权限判定。
    /// </summary>
    public bool ThisMachineIsHub()
    {
        var dial = Profile?.Dial;
        if (string.IsNullOrWhiteSpace(dial)) return false;
        var host = dial.Split(':')[0].Trim();
        if (host is "127.0.0.1" or "localhost" or "::1") return true;
        try
        {
            foreach (var a in Dns.GetHostAddresses(Dns.GetHostName()))
                if (string.Equals(a.ToString(), host, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { /* DNS 解析不出就当不是主机 —— 状态显示而已,保守即可 */ }
        return false;
    }

    public HubClient() => Reload();

    /// <summary>从磁盘读回配对档案(启动时调用)。没有档案 = 从未配对。</summary>
    public void Reload()
    {
        try
        {
            if (File.Exists(AppPaths.ProfilePath))
            {
                Profile = JsonSerializer.Deserialize<ClientProfile>(File.ReadAllText(AppPaths.ProfilePath));
                State = Profile is null ? HubState.NotPaired : HubState.Offline;
                return;
            }
        }
        catch (Exception ex) { LastError = "读取配对档案失败: " + ex.Message; }
        Profile = null;
        State = HubState.NotPaired;
    }

    static IPEndPoint ParseDial(string s)
    {
        var i = s.LastIndexOf(':');
        if (i <= 0) throw new FormatException("拨号地址应形如 192.168.1.10:8443,收到: " + s);
        return new IPEndPoint(IPAddress.Parse(s[..i]), int.Parse(s[(i + 1)..]));
    }

    IPEndPoint Dial() => ParseDial(
        !string.IsNullOrWhiteSpace(Profile?.Dial) ? Profile!.Dial
        : throw new InvalidOperationException("配对档案里没有拨号地址(P3c 之前的旧档案),请重新配对"));

    /// <summary>
    /// 配对(一次性)。onSas 回调把六个词交给界面显示,由用户与主机屏幕**人工比对**后主机批准。
    /// ★ 六词带外比对是 P3b 的安全根基,界面化只是换了显示位置,**不得跳过**(D47)。
    /// </summary>
    public async Task PairAsync(string edgeUrl, string dial, string displayName, Func<string, string[], Task> onSas)
    {
        AppPaths.EnsureStateDir();
        var ep = ParseDial(dial);
        Profile = await Transport.Pair(edgeUrl, ep, AppPaths.StateDir, displayName, onSas);
        State = HubState.Online;
        LastError = null;
    }

    /// <summary>用已保存的档案发一次业务调用。这也是"我还是不是有效成员"的探针。</summary>
    public async Task<(int status, string body)> CallAsync(string path)
    {
        if (Profile is null) throw new InvalidOperationException("尚未配对");
        try
        {
            var r = await Transport.Call(Profile, Dial(), path);
            // ★ 401 有至少四种来源(未带客户端证书 / 非 active 成员 / 网关 remote-unauthenticated /
            //   网关 lan_device_unknown)。**不能**一律判成"已被解除" —— 那会引导用户去"重新配对",
            //   而重新配对会删掉本机私钥,把一个本来有效的身份亲手销毁,主机侧还留下幽灵条目。
            //   只有主机明确给出吊销语义(响应含 revoked/lan_device_unknown 标记)才置 Revoked。
            if (r.status == 401)
            {
                State = LooksRevoked(r.body) ? HubState.Revoked : HubState.Unauthorized;
                LastError = State == HubState.Revoked
                    ? "本设备已被主机解除,需要重新配对"
                    : "中枢拒绝了这次请求(401)。可能是权限或网关策略,不一定是被解除 —— 先别重新配对。";
            }
            else
            {
                State = r.status is >= 200 and < 500 ? HubState.Online : HubState.Offline;
                if (State == HubState.Online) LastError = null;
            }
            return r;
        }
        catch (Exception ex)
        {
            // 证书过期与"中枢没开机"症状相同、处置不同 -> 必须分开报,否则用户会瞎折腾。
            State = IsCertExpiry(ex) ? HubState.CertExpired : HubState.Offline;
            LastError = State == HubState.CertExpired
                ? "主机证书已过期,请在主机上续签(localai-identity renew-server);**不需要**重新配对。"
                : ex.Message;
            throw;
        }
    }

    /// <summary>
    /// 退出前通知中枢:本客户端的会话结束了,可以释放它占用的显存。
    /// ★ 语义:释放的是**本会话**的占用,不是"卸载所有模型"。别的成员可能正在用同一个模型,
    ///   真正卸载由主机侧在引用归零时决定(P4 租约的前身)。
    /// best-effort:主机不在线/尚未实现该端点都不该阻塞退出 —— 吞掉异常,只记 LastError。
    /// </summary>
    public async Task EndSessionAsync(CancellationToken ct = default)
    {
        if (Profile is null) return;
        try
        {
            await Transport.Send(Profile, Dial(), HttpMethod.Post, "/v1/session/end",
                                 new { reason = "client-exit", device = Environment.MachineName }, ct);
        }
        catch (Exception ex) { LastError = "结束会话通知失败(不影响退出): " + ex.Message; }
    }

    /// <summary>主机是否明确表达了"这台设备被吊销了"。没有明确信号一律不认定(fail-safe 偏保守)。</summary>
    static bool LooksRevoked(string body) =>
        body.Contains("lan_device_unknown", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("revoked", StringComparison.OrdinalIgnoreCase);

    /// <summary>异常链里是否有"证书过期/链无效"的痕迹。</summary>
    static bool IsCertExpiry(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Security.Authentication.AuthenticationException) return true;
            var m = e.Message;
            if (m.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("NotTimeValid", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("PartialChain", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>轻量连通性探测:启动时用,判断中枢在不在线 / 本机是否仍是成员。</summary>
    public async Task<HubState> ProbeAsync()
    {
        if (Profile is null) { State = HubState.NotPaired; return State; }
        State = HubState.Connecting;
        try { await CallAsync("/v1/models"); }
        catch { /* State 已在 CallAsync 里置为 Offline */ }
        return State;
    }

    // ---------------------------------------------------------------- 主机管理 API(P3c S2)
    // 客户端里「列出已配对的 PC + 解除」用的就是这组。非家庭安全管理员返回 403 ——
    // 由界面如实告知,不假装列表为空。
    //
    // ★★ 已知的结构性限制(审计 2026-07-31 确认,别再把它当成"主机没升级"):
    //   按 D37 / D48,/admin/* 只挂在主机【本地回环口】上;这里走的是局域网 mTLS 口,
    //   它对 /admin/* 一律 404 —— 连存在性都不暴露,这是有意为之。
    //   所以从别的机器调这组接口【永远】是 404,升级主机也不会变。
    //   要让远程真的能管设备,得另开一条"仅主机本地"的回环通道(未做),
    //   而不是继续往这个口上打。界面文案已改成说实话(见 DevicesView 的 404 分支)。

    /// <summary>取设备列表原始响应。返回 (状态码, 正文),不抛 HTTP 异常,便于界面分辨 404/403。</summary>
    public async Task<(int status, string body)> ListDevicesRawAsync(CancellationToken ct = default)
    {
        if (Profile is null) throw new InvalidOperationException("尚未配对");
        return await Transport.Send(Profile, Dial(), HttpMethod.Get, "/admin/devices", null, ct);
    }

    /// <summary>
    /// 最近一次【真的从主机拿到】的其它设备(已解除的不算)。★ 诚实:只有主机真给了才有内容;
    /// 没配对 / 连不上 / 没权限 时永远是空 —— 界面据此"只显示本机",不摆假的远程列表。
    /// 由设备页在成功拉取后调用 CacheDevices 填充。
    /// </summary>
    public IReadOnlyList<HubDevice> KnownDevices { get; private set; } = Array.Empty<HubDevice>();

    /// <summary>设备页拉到真实设备表后回填这里,供别处(如项目文件夹选机器)复用。</summary>
    public void CacheDevices(IEnumerable<HubDevice> devices)
        => KnownDevices = devices.Where(d => d.Status != "revoked").ToList();

    public async Task<(int status, string body)> RevokeDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        if (Profile is null) throw new InvalidOperationException("尚未配对");
        return await Transport.Send(Profile, Dial(), HttpMethod.Post,
                                    "/admin/devices/revoke", new { deviceId }, ct);
    }

    public static List<HubDevice> ParseDevices(string json)
    {
        var list = new List<HubDevice>();
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : doc.RootElement.TryGetProperty("devices", out var d) ? d
                : throw new FormatException("响应里没有设备数组");
        foreach (var e in arr.EnumerateArray())
            list.Add(new HubDevice(
                e.TryGetProperty("deviceId", out var i) ? i.GetString() ?? "" : "",
                e.TryGetProperty("displayName", out var n) ? n.GetString() ?? "" : "",
                e.TryGetProperty("status", out var s) ? s.GetString() ?? "" : ""));
        return list;
    }

    /// <summary>
    /// 解除本机与中枢的配对(本地侧)。删档案 + **删掉设备私钥**(留着就是一份无用但敏感的凭据)。
    /// 注意:这只解除本地;主机侧的成员条目要由主机端「解除」按钮吊销(两侧都做才干净)。
    /// </summary>
    public void UnpairLocal()
    {
        var keyName = Profile?.KeyName;
        try { if (File.Exists(AppPaths.ProfilePath)) File.Delete(AppPaths.ProfilePath); } catch { }
        if (!string.IsNullOrWhiteSpace(keyName)) Transport.DeleteKey(keyName!);
        Profile = null;
        State = HubState.NotPaired;
    }
}
