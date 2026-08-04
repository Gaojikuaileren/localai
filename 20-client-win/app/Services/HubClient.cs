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
// ProtocolMismatch 也单列一态:症状是"连不上",但处置是【去更新某一端】,
// 与"中枢没开机"和"证书过期"都不同。混进 Offline 会让人一直去重启中枢。
// HubServerError 也单列:症状是"用不了",但中枢明明在 —— 处置是【看中枢日志】,
// 不是重启 Edge / 查防火墙 / 重新配对。混进 Offline 会把人支去做整整一趟无用功。
// HubIdentityChanged:链不到【配对时钉住的那个 CA】。症状和"证书过期"一样(都是 TLS 握手失败),
// 但处置**正好相反**:过期要在主机上续签、不必重配;链不通则意味着对面不是你配对的那个中枢
// (主机重铸了身份、或你拨到了别人家),那时**重新配对才是唯一出路**。
// 以前这两种全塌进 CertExpired,而界面还加粗写着"不需要重新配对" —— 把唯一的出路否掉了。
public enum HubState { NotPaired, Connecting, Online, Offline, Revoked, CertExpired, Unauthorized, ProtocolMismatch, HubServerError, HubIdentityChanged }

/// <summary>主机成员库里的一台设备。DisplayName 是设备**自报**的,只作显示、永不进 prompt。</summary>
public sealed record HubDevice(string DeviceId, string DisplayName, string Status);

public sealed class HubClient
{
    static readonly JsonSerializerOptions J = new() { WriteIndented = true };

    public ClientProfile? Profile { get; private set; }

    HubState _state = HubState.NotPaired;
    /// <summary>连接状态。★ 换值时广播 Changed —— 顶栏据此刷新(连上后改显 token 速率,见 RefreshStatus)。</summary>
    public HubState State
    {
        get => _state;
        private set { if (_state != value) { _state = value; Changed?.Invoke(); } }
    }
    /// <summary>状态变化(探测/配对/调用中被解除等任一路径)。界面订阅它刷新顶栏与托盘。</summary>
    public event Action? Changed;

    public string? LastError { get; private set; }

    // ---------------------------------------------------------------- 协议版本协商(D45,P3c 判据项)
    /// <summary>本客户端说的协议版本。改动线上格式(会话/配对/管理接口的形状)时 +1。</summary>
    public const int ClientProtocol = 1;

    /// <summary>响应头名:两边都用它报自己的版本。请求头同名 —— 中枢想按版本分流时不必再猜。</summary>
    public const string ProtocolHeader = "X-LocalAI-Protocol";

    /// <summary>
    /// 中枢自报的协议版本。null = 【它没报】。
    ///
    /// ★ 「没报」与「报了但对不上」是两件事,界面必须分开说:
    ///   · 对不上 → 明确拒绝,并说清该更新哪一端(D45:「主机 v5,你 v3,请更新」);
    ///   · 没报   → 现役中枢还没加这个头(网关侧那半行改动归网关那条线),
    ///     这时【不假装协商过】,状态栏如实写「中枢未声明协议版本」。
    ///     不因此拒连:那会把今天能用的装置直接停掉,而我们并没有证据说它不兼容。
    /// </summary>
    public int? HubProtocol { get; private set; }

    /// <summary>
    /// 有没有【真的读到过一次中枢的响应头】。★ 存在的理由:HubProtocol 的 null 本来同时表示两件事 ——
    /// 「连上了但它没报版本」和「压根没连上过」。Edge 没启动时是后者,而界面只会说前者,
    /// 读起来像"中枢版本太旧",人就去查中枢是不是该重编了 —— 它连一个字节都没回过。
    /// </summary>
    bool _protocolObserved;

    /// <summary>协商结论。给界面用一句话说清楚现在是哪种情形。</summary>
    public string ProtocolNote => HubProtocol is null
        ? (_protocolObserved
            ? $"中枢未声明协议版本(本机 v{ClientProtocol})—— 未协商"
            : $"还没和中枢通过话,协议版本无从谈起(本机 v{ClientProtocol})")
        : HubProtocol == ClientProtocol
            ? $"协议 v{ClientProtocol} 一致"
            : HubProtocol > ClientProtocol
                ? $"主机 v{HubProtocol},你 v{ClientProtocol} —— 请更新【客户端】"
                : $"你 v{ClientProtocol},主机 v{HubProtocol} —— 请更新【中枢】";

    /// <summary>记下中枢自报的版本;不一致就置 ProtocolMismatch(拒绝当作正常在线)。</summary>
    void NoteProtocol(IReadOnlyDictionary<string, string>? headers)
    {
        int? v = null;
        if (headers is not null && headers.TryGetValue(ProtocolHeader, out var raw) && int.TryParse(raw, out var n)) v = n;
        // ★ 走到这儿就说明【确实收到过一次响应】—— 这之后 HubProtocol 的 null 才是"它没报"
        _protocolObserved = true;
        HubProtocol = v;
        if (v is { } got && got != ClientProtocol)
        {
            LastError = ProtocolNote;
            State = HubState.ProtocolMismatch;
        }
    }

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

    /// <summary>
    /// 拨号端点。★ 没有档案 / 档案里没地址 → 返回 null 而**不抛** ——
    /// 订阅方(HubGpu 的推送流)要能安静地等着用户完成配对,那不是错误状态。
    /// 业务调用仍然走会抛的 Dial():那些路径上"没配对"确实是调用方的错。
    /// </summary>
    public IPEndPoint? TryDial()
    {
        try { return string.IsNullOrWhiteSpace(Profile?.Dial) ? null : ParseDial(Profile!.Dial); }
        catch { return null; }
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
            var r = await Transport.SendWithHeaders(Profile, Dial(), HttpMethod.Get, path, null);
            NoteProtocol(r.headers);
            // ★ 协议对不上就到此为止:不能先把它当成 Online 再去解读正文 ——
            //   两边对格式的理解不一致时,解出来的东西本身就不可信。
            if (State == HubState.ProtocolMismatch) return (r.status, r.body);
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
            else if (r.status >= 500)
            {
                // ★ 走到这一行意味着:TCP 通了、mTLS 拿钉住的 CA 校验过了、响应连头带正文都读到了。
                //   能确定的事恰恰相反 —— 中枢【在】,是它这次请求内部出错了。
                //   判成 Offline 会让人去做"连不上"该做的事:跑到主机看 Edge 起没起、查防火墙、
                //   改拨号地址、甚至解除重配(那会删掉本机私钥)。真正该做的是去看中枢日志。
                State = HubState.HubServerError;
                LastError = $"中枢应答了,但返回 {r.status} —— 不是连不上,是中枢内部出错,请看中枢日志。";
            }
            else
            {
                State = HubState.Online;
                LastError = null;
            }
            return (r.status, r.body);
        }
        catch (Exception ex)
        {
            // 证书过期与"中枢没开机"症状相同、处置不同 -> 必须分开报,否则用户会瞎折腾。
            State = ClassifyTlsFailure(ex) ?? HubState.Offline;
            LastError = State switch
            {
                HubState.CertExpired =>
                    "主机证书已过期 —— 在主机上续签(localai-identity renew-server)即可,不必重新配对。",
                HubState.HubIdentityChanged =>
                    "连上了,但对面的证书链不到你配对时钉住的那个中枢 —— "
                    + "可能是主机重铸了身份,或者这个地址上是另一台机器。这种情况【必须重新配对】。",
                _ => ex.Message,
            };
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
    /// <summary>
    /// TLS 失败分两类,处置正好相反,所以必须分开:
    ///   · CertExpired        —— 服务器证书过期:在主机上续签即可,**不必**重新配对;
    ///   · HubIdentityChanged —— 链不到配对时钉住的那个 CA:对面不是你配对的那个中枢
    ///     (主机重铸了身份、或拨错了地方),这时**重新配对是唯一出路**。
    ///
    /// ★★ 以前这里是 `e is AuthenticationException → true`,把**所有** TLS 失败都判成"过期",
    ///   而界面加粗写着"不需要重新配对" —— 恰好把唯一的出路否掉了。
    ///   而且拿异常 Message 里的英文单词当判据本身就不可靠(随 .NET 版本和语言变)。
    /// ★ 判不出来时【不猜过期】:返回 null,由调用方归到普通的连不上,别给一个假的结论。
    /// </summary>
    static HubState? ClassifyTlsFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Security.Cryptography.X509Certificates.X509ChainStatusFlags) break;
            var m = e.Message ?? "";
            // 链不到钉住的 CA:这是"换了中枢",不是"过期"
            if (m.Contains("UntrustedRoot", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("PartialChain", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("RevocationStatusUnknown", StringComparison.OrdinalIgnoreCase))
                return HubState.HubIdentityChanged;
            if (m.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("NotTimeValid", StringComparison.OrdinalIgnoreCase))
                return HubState.CertExpired;
        }
        // ★ 只知道"TLS 没握成",但不知道是哪一种 —— 那就【别下结论】
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is System.Security.Authentication.AuthenticationException) return HubState.HubIdentityChanged;
        return null;
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
    /// <summary>
    /// 改【连接地址】(ip:port),不动身份。
    ///
    /// ★ 为什么必须有这个入口(P3c 判据③ 的可落地版本):
    ///   判据原话是「换路由器后自动重新发现且无需重新配对」,而 D43 把 DNS-SD 自动发现推迟到了 P3b.2
    ///   ——「自动发现」这半句在本阶段结构上不可能达成。但它真正要保护的东西是
    ///   **换网段不该逼人重新配对**:重新配对会删掉本机私钥,把一个完全有效的身份亲手销毁。
    ///   所以本阶段交付的是【手改地址】:证书、CA、密钥、hub_id 全部原样,只换拨号目标。
    ///   自动发现随 P3b.2 补上时,它填的也是这一个字段。
    /// ★ 只认 ip:port,不认主机名 —— 拨号要用 IPEndPoint,收主机名会在"连不上"时多出一层
    ///   "是解析失败还是对方没开机"的歧义。
    /// </summary>
    public bool SetDial(string dial)
    {
        if (Profile is null) return false;
        dial = (dial ?? "").Trim();
        if (dial.Length == 0) return false;
        try { ParseDial(dial); } catch { LastError = "地址要写成 ip:port,例如 192.168.1.20:8443"; return false; }
        if (Profile.Dial == dial) return true;
        Profile.Dial = dial;
        try { File.WriteAllText(AppPaths.ProfilePath, JsonSerializer.Serialize(Profile, J)); }
        catch (Exception ex) { LastError = "写配对档案失败: " + ex.Message; return false; }
        State = HubState.Offline;      // 换了目标 -> 状态待重新探测,不许沿用上一处的"在线"
        LastError = null;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 连不上时,在局域网里【按 hub_id 把它找回来】并更新拨号地址。
    ///
    /// ★ 为什么这件事是安全的:身份从来不是"地址",而是**配对时钉住的那套证书**。
    ///   我们只接受证书名里 hub_id 与本机档案**完全一致**的那一台;换了地址仍然是同一个中枢,
    ///   而冒名者拿不到那个 hub_id 对应的 CA 签名 —— 就算它把 hub_id 抄过去,
    ///   之后的 mTLS 也会在校验链的那一步失败。所以这一步不放松任何一条信任。
    /// ★ 找到多台同 hub_id(不该发生)时【不猜】:返回 false,由界面让人自己选。
    /// </summary>
    public async Task<bool> RediscoverAsync(CancellationToken ct = default)
    {
        if (Profile is null || string.IsNullOrWhiteSpace(Profile.HubId)) return false;
        // ★★ 配对档案里的 HubId 是 **UUID**,而证书名里是它的 **16 位短号** —— 直接比永远不相等,
        //   这个按钮会永远失败而且毫无线索。必须先换算(见 HubDiscovery.ShortHubId)。
        var want = HubDiscovery.ShortHubId(Profile.HubId);
        if (want is null) { LastError = $"认不出本机档案里的 hub id 形状({Profile.HubId})—— 没法比对,请手填地址"; return false; }
        var scan = await HubDiscovery.ScanAsync(ct: ct);
        var mine = scan.Hits.Where(h => string.Equals(h.HubId, want, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count != 1)
        {
            LastError = mine.Count > 1
                ? "找到多台同 id 的中枢,请手动选"
                : ScanExplain(scan, "这个中枢");
            return false;
        }
        return SetDial(mine[0].Dial);
    }

    /// <summary>
    /// 把一次扫描【为什么没找到】说清楚。★ 四种情形的下一步完全不同,混着说就会把人支错方向。
    /// </summary>
    public static string ScanExplain(ScanResult scan, string what)
    {
        if (scan.NoUsableV4)
            // ★ 这一种的出路是【去接网线】,不是手填 —— 手填也连不上
            return "本机现在没有可用的局域网地址(网卡没连上 / 没拿到 DHCP / 只有 IPv6)—— "
                 + "先把网络连上;这种情况手填地址也连不上。";
        if (scan.ScannedNothing)
        {
            var why = new List<string>();
            if (scan.TooWide.Count > 0) why.Add($"网卡掩码宽于 /24({string.Join("、", scan.TooWide)}),自动查找结构上覆盖不到");
            if (scan.TinySubnet.Count > 0) why.Add($"网卡是 {string.Join("、", scan.TinySubnet)}(VPN 常见),这个子网里没有别的主机可扫");
            return "一个网段都没扫 —— " + (why.Count > 0 ? string.Join(";", why) : "本机没有可扫的网段")
                 + "。请照主机 Edge 窗口里那行「拨号 …:8443」手填。";
        }
        var tail = "";
        if (scan.TooWide.Count > 0) tail += $";另有 {string.Join("、", scan.TooWide)} 因掩码宽于 /24 没扫";
        if (scan.TinySubnet.Count > 0) tail += $";{string.Join("、", scan.TinySubnet)} 是 /31、/32,没有别的主机可扫";
        return $"扫过 {string.Join("、", scan.Scanned)} 都没找到{what}{tail}。";
    }

    public void UnpairLocal()
    {
        var keyName = Profile?.KeyName;
        try { if (File.Exists(AppPaths.ProfilePath)) File.Delete(AppPaths.ProfilePath); } catch { }
        if (!string.IsNullOrWhiteSpace(keyName)) Transport.DeleteKey(keyName!);
        Profile = null;
        State = HubState.NotPaired;
    }
}
