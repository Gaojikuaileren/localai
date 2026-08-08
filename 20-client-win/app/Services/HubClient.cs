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
// LocalCertExpired / LocalProfileUnusable(D89 §1.6 + 决议包 §2.2):**本机这一侧**的两种坏法。
// 五种症状全是"连不上",而处置各不相同 —— 此前这两格是空的,实际归宿都是 Offline =「中枢没开机」,
// 于是用户一趟趟跑去重启一个完全正常的中枢。
// ★ 两者**不许合并**:一个是"证书到期了"(重配要搭上一把本来有用的私钥),
//   一个是"私钥/档案已经没了"(重配不毁掉任何还有用的东西)。代价不同,话就得分开说。
public enum HubState { NotPaired, Connecting, Online, Offline, Revoked, CertExpired, Unauthorized, ProtocolMismatch, HubServerError, HubIdentityChanged,
                       LocalCertExpired, LocalProfileUnusable }

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

    // ════════════════════════════════════════════════════════════════════════
    //  ★★ `ThisMachineIsHub()` 已删(V19 · 2026-08-08)—— 它的最后一个调用方没了。
    //
    //  它是个启发式:配对的拨号地址指向本机(回环或本机某个网卡 IP)就算主机。
    //  自己的注释一直写着「**仅用于状态显示,不做权限判定**」,而 V13 决定档位时
    //  也白纸黑字**拒绝**用它(见 `IsHostMachine` 上面那段)。
    //  它唯一还活着的地方是左下角那一格的 `guessHub` 回退。
    //
    //  ⇒ V19 把那一格改读 `App.Boot?.Role.IsHost` 之后,它**零调用方**。
    //    而 `DecideRole` 的 `ConfiguredHubResolvesLocal` 拿的是**同一个输入**
    //    (`Profile.Dial`)、走的是 `HostSetup.ResolvesToThisMachine` —— 那一份严格更好:
    //      · IPv6 不会被 `Split(':')[0]` 切坏(`::1` 会被切成空串,那条是自检当场抓出来的);
    //      · 把「解析不出来」(null / 拿不准)与「解析到别人」(false / 明确不是)**分开**,
    //        而这个启发式把两者都返回 false —— 一次 DNS 抖动就能让主机被判成副机。
    //  ⇒ 所以删掉它**不丢任何判据**,只是不再留一份更差的同义实现在旁边等人误用。
    //    ★ 留着的代价不是 14 行:是下一个人看到两个名字相近的判据,挑了错的那个。
    // ════════════════════════════════════════════════════════════════════════

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

    // ════════════════════════════════════════════════════════════════════
    //  ★★★ V13 · 主机的业务调用走【回环网关】(D?)—— 2026-08-08
    //
    //  ══ 实机症状(2026-08-07,两台真 PC)══
    //    主机上组件面板点「确定」→「这台设备不能做这个操作」;敲字不起模型。
    //    而面板上那句「只能在主机上改」当天是一句**没有任何设备能满足**的话。
    //
    //  ══ 根因(复核过,与协调层给的版本有一处出入,如实记下)══
    //    客户端的每一次业务调用都是 `Transport.Send(profile, dial, …)` ⇒ 打 lan-edge 的
    //    mTLS 业务口。lan-edge 会**注入验证过的证书指纹**,而网关的 `gpu_principal`
    //    一见指纹就【封顶】`lan-device`(gateway.py:581)。
    //    ⇒ 主机上的客户端**从来就是 `lan-device`**,不是 V12 才变成的:
    //      V12 之前 `run` 把业务口绑在**回环**上,客户端拨 `127.0.0.1:8443` ——
    //      那仍然是 lan-edge,仍然带指纹,仍然封顶 lan-device。
    //    ⇒ 所以 `run → run-lan`(V12 改动 A)**不是**这个症状的成因,它只换了拨号地址;
    //      单靠 V12 改动 B(`change_resident` 从 lan-device 拿掉)就足以让主机点不动确定。
    //      ★ 这一条要如实写:把 A 也算成成因,会让人以为"把绑定改回回环就好了" ——
    //        改回去之后主机**依旧**是 lan-device,依旧点不动,而自配对那条链会重新断掉。
    //
    //  ══ 修法 ══
    //    已判定为主机的这台,业务调用**不再绕一圈经 lan-edge**,直接打本机回环网关
    //    `127.0.0.1:8080`。网关的 `classify_caller` 判据是:回环连接 → 查拥有该 socket 的
    //    进程 → 取 owner → 查 `config/caller-accounts.toml` 的 allowlist(含机主账户)
    //    ⇒ 得 `trusted-local`,那正是规格里「只有主机变更面能写 intended_resident_set」
    //    的那个"主机变更面"。★ 副机**一行都不受影响**:它判不成主机,照旧走 lan-edge。
    //
    //  ★★ 这不是临时脚手架:将来的管理端要走的也是这条回环路。
    //  ★★ **绝不**给 `lan-device` 加回 `change_resident` —— 那会把副机的口子一起开回去。
    // ════════════════════════════════════════════════════════════════════

    /// <summary>业务调用的落点。★ 两条路的**信任来源完全不同**,所以它们是两个值而不是一个开关。</summary>
    public enum BusinessPath
    {
        /// <summary>主机:直连本机回环网关(明文 HTTP,无 mTLS、无证书指纹)。身份来自 **OS 账户**。</summary>
        HostLoopback,
        /// <summary>副机:经 LAN Edge 的 mTLS 业务口。身份来自**证书指纹经成员表反查**。</summary>
        LanEdge,
    }

    /// <summary>一次业务调用该打到哪儿。★ <c>Why</c> 是**必须**说得出来的 —— 见 <see cref="RouteNote"/>。</summary>
    public sealed record BusinessTarget(BusinessPath Path, IPEndPoint EndPoint, string Why)
    {
        public bool ViaEdge => Path == BusinessPath.LanEdge;
        public override string ToString() => $"{Path} {EndPoint}";
    }

    /// <summary>
    /// 业务调用落点的**纯函数**判据。喂什么答什么,不碰 IO、不看档案 —— 这样两个方向
    /// (主机 → 回环 / 副机 → Edge)才能各测一次,而只测一个方向等于没测。
    /// </summary>
    /// <param name="isHostMachine">D36 角色判定的结论(<see cref="HostSetup.DecideRole"/>)。
    /// ★ 判不出来传 false —— 但**别把这条读成"误判成主机只是打一个没人听的口"**:
    /// 同一个 <c>RoleVerdict</c> 还是 <c>MayStartStack</c> 的前件,它会让这台**真去起一套栈**
    /// (`App.xaml.cs` 的 `EnsureStackAsync`)。所以判错成主机的代价是"起一套不该起的栈"
    /// **加上**"业务口打到一个自己刚起的、身份不对的网关"。fail-closed 的方向仍然是 false,
    /// 但理由是前者,不是后者。</param>
    /// <param name="pairedDial">配对档案里的拨号地址;没配过对传 null。</param>
    /// <param name="loopbackPort">回环网关端口(<see cref="HostSetup.GatewayPort"/>)。</param>
    /// <param name="demotedWhy">
    /// ★★★ 非 null = **回环那条路已被实测证明不适用于当前这个 OS 账户**,退回 Edge。
    ///
    /// <para>它修的是一个粒度错配:<paramref name="isHostMachine"/> 是**整机**事实,
    /// 而回环那一端的档位是按**登录的 Windows 账户**判的
    /// (`gateway.py` 查 `config/caller-accounts.toml` 的 allowlist)。
    /// 主机上一个**不在 allowlist 里**的账户(该文件里明文记着访客账户 `Alle`,
    /// 还给"第二位家庭成员"预留了位置)走回环拿到的是 `unregistered-local` ——
    /// 它只有 `{read}`,**比它原来经 Edge 拿到的 `lan-device`(`{read, lease}`)还少一个 `lease`**
    /// ⇒ 「意图即起」和 `client_session` 租约会一起没掉。那是 V13 引入的回归,不是修复。</para>
    /// <para>★ 判据只能来自**服务端如实回带的 `error.tier`**(见 <see cref="NoteLoopbackDenial"/>)——
    /// 客户端没有别的办法知道自己在网关眼里是谁:allowlist 是服务端配置,客户端不一定有那个文件。</para>
    /// <para>★ 只有**有 Edge 可退**时降级才成立;没配过对就无路可退,那时如实说,不假装换了路。</para>
    /// </param>
    public static BusinessTarget? DecideBusinessTarget(bool isHostMachine, IPEndPoint? pairedDial,
                                                       int loopbackPort, string? demotedWhy = null)
    {
        if (isHostMachine)
        {
            if (demotedWhy is { Length: > 0 } && pairedDial is not null)
                return new BusinessTarget(
                    BusinessPath.LanEdge, pairedDial,
                    $"这台是主机,但**当前这个 Windows 账户在网关眼里不是机主** —— {demotedWhy}。"
                    + $"已退回 LAN Edge 的 mTLS 业务口 {pairedDial}(档位 lan-device,与 V13 之前一致)。"
                    + "★ 想让它拿主机档:把这个账户加进 config/caller-accounts.toml 的 trusted_local(要走决议)。");
            return new BusinessTarget(
                BusinessPath.HostLoopback, new IPEndPoint(IPAddress.Loopback, loopbackPort),
                $"这台已判定为中枢主机 ⇒ 业务调用直接打本机回环网关 127.0.0.1:{loopbackPort},"
                + "由网关按 OS 账户判成 trusted-local(绕开 lan-edge 那道会把档位封顶 lan-device 的指纹)"
                + (demotedWhy is { Length: > 0 }
                   ? $"。★★ 注意:{demotedWhy},本该退回 Edge —— 但这台**没有配对地址,无路可退**。"
                   : ""));
        }
        if (pairedDial is null) return null;
        return new BusinessTarget(
            BusinessPath.LanEdge, pairedDial,
            $"这台不是主机 ⇒ 业务调用照旧经 LAN Edge 的 mTLS 业务口 {pairedDial}(档位 lan-device)");
    }

    /// <summary>
    /// D36 角色判定的结论。★ 默认 <c>false</c> 且**只由开机分流写入** ——
    /// 判据本身在 <see cref="HostSetup.DecideRole"/>,这里只是把它记下来给拨号用。
    /// <para>★★ 不用 <c>ThisMachineIsHub</c> 那个看拨号地址的启发式:
    /// 它自己的注释就写着「仅用于状态显示,不做权限判定」—— 而这里决定的是
    /// 这次请求会拿到哪个档位,正是它明说自己不该管的那件事。
    /// ★ 那个方法已于 V19(2026-08-08)删除 —— 最后一个调用方(左下角那一格)改读角色判定了。
    /// 这段理由**留着**:它记的是「为什么不能走那条路」,而理由不随实现消失
    /// —— 删掉理由,下一个人会把同样的启发式再写一遍。</para>
    /// </summary>
    public bool IsHostMachine { get; private set; }

    /// <summary>凭什么说这台是/不是主机(<see cref="HostSetup.RoleVerdict.Why"/> 原文)。</summary>
    public string RoleWhy { get; private set; } = "开机角色判定还没跑完 —— 按【副机】走(fail-closed)";

    /// <summary>开机分流判完角色后回填。★ 唯一写入点;在起任何一条流**之前**调。</summary>
    public void NoteRole(bool isHost, string why)
    {
        var changed = IsHostMachine != isHost;
        IsHostMachine = isHost;
        RoleWhy = string.IsNullOrWhiteSpace(why) ? RoleWhy : why;
        // ★★ 必须包住:这是**开机路上**的一次同步派发,而订阅方是界面
        //   (`MainWindow` 那条 `Hub.Changed += … Dispatcher.Invoke(…)`)。
        //   一个订阅者抛异常,异常会顺着这里冒回 `StartAfterBootDecision`,
        //   于是它后面的 `Gpu.Start()` / `Lease.Start()` **一条都起不来** ——
        //   而症状是"界面在、但显存和租约永远没有动静",查起来完全指不到这一行。
        //   ★ 与 `App.RaiseBootChanged` 同款处理,理由逐字一样。
        if (changed) { try { Changed?.Invoke(); } catch { } }
    }

    /// <summary>
    /// 回环那条路**已被实测证明不适用于当前 OS 账户**的理由;null = 没有这回事。
    /// ★ 只由 <see cref="NoteLoopbackDenial"/> 写入,而它的判据只有一个:
    ///   服务端在 401/403 里如实回带的 <c>error.tier</c>。
    /// </summary>
    public string? LoopbackDemotedWhy { get; private set; }

    /// <summary>
    /// 看一眼回环那条路的应答,决定要不要**永久降级到 Edge**(本次运行内)。
    ///
    /// <para>★ 触发条件写得**尽量窄**:只有服务端明说了 <c>error.tier</c>、而且那个档位
    /// **不是** <c>trusted-local</c> 时才降。机主账户永远拿 <c>trusted-local</c>,
    /// 所以它一次都不会踩到这里;而 <c>denied_param</c> / <c>denied_quota</c>
    /// (ttl 超上限、太快了)同样带 tier=trusted-local ⇒ **不会**被误判成"账户不对"。</para>
    /// <para>★ 只降不升:降级之后本次运行不再回头试 —— 账户在一次运行里不会变,
    /// 而反复试会让每一次业务调用都先撞一发 403。要复位就重开客户端。</para>
    /// </summary>
    /// <returns>这次是否**刚刚**发生降级(调用方据此决定要不要重发一遍)。</returns>
    bool NoteLoopbackDenial(int status, string body)
    {
        if (LoopbackDemotedWhy is not null) return false;
        if (status is not (401 or 403)) return false;
        string? tier = null;
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error", out var e)
                && e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("tier", out var t) && t.ValueKind == JsonValueKind.String)
                tier = t.GetString();
        }
        catch { return false; }        // ★ 读不出 tier 就**不降级**:降错了会静默退回旧档位
        if (string.IsNullOrEmpty(tier) || tier == "trusted-local") return false;
        LoopbackDemotedWhy = $"网关回话说这条回环连接的档位是 `{tier}`,不是 `trusted-local`"
                           + "(这个 Windows 账户不在 config/caller-accounts.toml 的 trusted_local 里)";
        try { Changed?.Invoke(); } catch { }
        return true;
    }

    /// <summary>这次业务调用打到哪儿。null = 既不是主机、也没有配对地址 ⇒ 无处可打。</summary>
    public BusinessTarget? BusinessRoute()
        => DecideBusinessTarget(IsHostMachine, TryDial(), HostSetup.GatewayPort, LoopbackDemotedWhy);

    /// <summary>界面/自检要的一句话:现在走哪条路、凭什么。</summary>
    public string RouteNote =>
        BusinessRoute() is { } t ? t.Why : "还没有可用的业务通道(没判成主机,也没有配对地址)";

    // ── 配对通道(Edge 那条)单独的健康 ──────────────────────────────
    /// <summary>
    /// 配对通道(`Profile.Dial` 上那条 mTLS)现在**坏在哪**;null = 好的 / 没测过。
    ///
    /// <para>★★★ 为什么主机上必须**单独**有这一格(V13 引入的缺口,2026-08-08 对抗式复核抓出):
    /// V13 之后主机的业务调用走回环,于是 <see cref="ProbeAsync"/> 不再碰 <c>Profile.Dial</c> ——
    /// 而那个地址**仍然是**聊天(`ChatClient`)、内网同步(`SyncClient`)与
    /// **90 天一次的设备证书续签**唯一的拨号目标。</para>
    /// <para>后果很具体:在「设备」页把地址改错一位,`SetDial` 写盘后那句
    /// 「立刻验一次」验的是回环网关 ⇒ 拿到 200 ⇒ 顶栏刷绿。**填错与填对长得一模一样**,
    /// 而聊天/同步/续签此后全部打向一个不存在的地址。</para>
    /// <para>★ 它**不合并进** <see cref="State"/>:那一格说的是"业务通道通不通"
    /// (面板、显存、租约靠它),两者可以一真一假,合并会让其中一件被另一件盖住。</para>
    /// </summary>
    public string? PairingChannelError { get; private set; }

    /// <summary>配对通道那一格给人看的一句话。</summary>
    public string PairingChannelNote =>
        PairingChannelError is { Length: > 0 } e
            ? $"配对通道({Profile?.Dial})连不上:{e} —— 聊天、内网同步、设备证书续签走的都是它。"
            : Profile is null ? "还没配过对" : $"配对通道({Profile.Dial})正常";

    /// <summary>
    /// 验一次**配对通道**(Edge 那条 mTLS),只写 <see cref="PairingChannelError"/>,**不碰** <see cref="State"/>。
    /// <para>★ 主机上业务走回环之后,这条是「我还是不是有效成员 / 地址对不对 / 证书还好不好」
    /// 唯一还问得到的地方。副机上业务通道本身就是它,不必重复问。</para>
    /// </summary>
    public async Task CheckPairingChannelAsync(CancellationToken ct = default)
    {
        if (Profile is null || TryDial() is not { } ep) { PairingChannelError = null; return; }
        try
        {
            var (status, body) = await Transport.Send(Profile, ep, HttpMethod.Get, "/v1/models", null, ct);
            PairingChannelError = status switch
            {
                401 when LooksRevoked(body) => "本设备已被主机解除,需要重新配对",
                401 => "中枢在这条通道上拒绝了这次请求(401)—— 可能是权限或网关策略,先别重新配对",
                >= 500 => $"中枢应答了,但返回 {status} —— 不是连不上,请看中枢日志",
                _ => null,
            };
        }
        catch (Exception ex)
        {
            // ★ 归因**全部**复用 Edge 那条既有的五分类 —— 这条路上它是对的(握手真的用本机证书)。
            PairingChannelError = ClassifyTlsFailure(ex, Profile) switch
            {
                HubState.CertExpired          => TlsFailure.Explain(TlsFailureKind.ServerCertExpired),
                HubState.HubIdentityChanged   => TlsFailure.Explain(TlsFailureKind.HubIdentityChanged),
                HubState.LocalCertExpired     => TlsFailure.Explain(TlsFailureKind.LocalDeviceCertExpired),
                HubState.LocalProfileUnusable => TlsFailure.Explain(TlsFailureKind.LocalProfileUnusable),
                _ => ex.Message,
            };
        }
    }

    // ── 回环网关的明文 HTTP 通道 ────────────────────────────────────────
    //  ★ UseProxy=false 是**承重**的,与网关自己 `trust_env=False` 同一条理由:
    //    系统代理会把一条本该只走回环的业务调用(带着整段会话)改道送到一个我们不控制的端点。
    //    回环不需要代理,关掉它零损失。
    //  ★ Timeout 设无限,由 CancellationToken 决定生命周期 —— SSE 是无限长的响应,
    //    默认 100 秒会把推送流掐断,而症状是"订阅了却每 100 秒断一次",很像服务端在踢人。
    static readonly HttpClient LoopHttp =
        new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>回环网关的基址。★ 每次现算 —— 端口来自 <see cref="HostSetup.GatewayPort"/> 那**一个**数。</summary>
    static string LoopBase(IPEndPoint ep) => $"http://{ep.Address}:{ep.Port}";

    /// <summary>
    /// 单次(非流式)回环调用的超时。
    ///
    /// <para>★★ 为什么必须显式给一个:<see cref="LoopHttp"/> 的 <c>Timeout</c> 是**无限**的
    /// (SSE 要它,默认 100 秒会把推送流每 100 秒掐一次)。而无限超时用在**单次**调用上
    /// 是一条真缺口:网关接了连接却不回话时,<c>ProbeAsync → CallAsync</c> 会**永远挂住**,
    /// State 停在 <c>Connecting</c> —— 界面一直转圈,而"转圈"这个症状说不出任何原因。
    /// 这与 <c>TryRenewDeviceCertAsync</c> 当年那个缺口是同一形状。</para>
    /// <para>★ 取 100 秒是**对齐 Edge 那条路**(<c>HttpClient</c> 的默认值),
    /// 让两条路在"等多久算等不到"上给出同一个答案 —— 两条路超时不一样的话,
    /// 同一次故障在主机和副机上会表现成两种病。</para>
    /// </summary>
    public static readonly TimeSpan LoopUnaryTimeout = TimeSpan.FromSeconds(100);

    /// <summary>
    /// 组一条打回环网关的请求。
    ///
    /// <para>★★★ **一个 <c>X-LocalAI-*</c> 头都不带**,这一条是承重的:
    /// 走 lan-edge 时,Edge 会先**剥掉客户端自带的 X-LocalAI-\*** 再写入它自己验过的指纹
    /// (`lan-edge/Program.cs:1593,1598`)。回环这条路上**没有那个剥离者** ——
    /// 客户端带什么,网关就原样看到什么。带一个 `X-LocalAI-Cert-Sha256` 过去,
    /// `gpu_principal` 会当场把自己封顶回 `lan-device`(或者指纹认不出 ⇒
    /// `remote-unauthenticated`),把这次修的东西原地退回去。
    /// ⇒ 所以这里连 `X-LocalAI-Protocol` 也不发:与其记住"哪些头是安全的",
    /// 不如让这条路上**根本没有**这个前缀的头。</para>
    /// </summary>
    static HttpRequestMessage LoopRequest(IPEndPoint ep, HttpMethod method, string path, object? body)
    {
        var req = new HttpRequestMessage(method, LoopBase(ep) + path);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body),
                                            System.Text.Encoding.UTF8, "application/json");
        return req;
    }

    /// <summary>回环版的 <c>SendWithHeaders</c> —— 形状与它逐字一致,好让 <see cref="CallAsync"/> 只有一处分叉。</summary>
    static async Task<(int status, string body, Dictionary<string, string> headers)> LoopWithHeadersAsync(
        IPEndPoint ep, HttpMethod method, string path, object? body, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LoopUnaryTimeout);
        using var req = LoopRequest(ep, method, path, body);
        using var r = await LoopHttp.SendAsync(req, cts.Token);
        var hs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in r.Headers) hs[h.Key] = string.Join(",", h.Value);
        return ((int)r.StatusCode, await r.Content.ReadAsStringAsync(cts.Token), hs);
    }

    /// <summary>
    /// 发一次业务调用(自动选路)。不抛 HTTP 状态异常 —— 401/403 是有意义的答复,交调用方判读。
    /// <para>★ 连不上时**抛**,与 <c>Transport.Send</c> 同款:那是"够不着中枢",归因要往上走。</para>
    /// </summary>
    public async Task<(int status, string body)> SendBusinessAsync(
        HttpMethod method, string path, object? body, CancellationToken ct = default)
    {
        var t = BusinessRoute()
                ?? throw new InvalidOperationException("尚未配对,而且这台也没被判成主机 —— 无处可发");
        if (t.ViaEdge)
            return await Transport.Send(Profile!, t.EndPoint, method, path, body, ct);
        var (status, text, _) = await LoopWithHeadersAsync(t.EndPoint, method, path, body, ct);
        // ★★ 回环拿到 401/403 且服务端点名了一个**不是 trusted-local** 的档位
        //   ⇒ 这个 Windows 账户不是机主 ⇒ 降级到 Edge,并把**这一次**原样重发一遍。
        //   重发是必要的:不重发的话,降级后的第一次调用仍然以那个 403 收场,
        //   而调用方会把它当成业务失败去报给用户(「这台设备不能做这个操作」),
        //   —— 那正是 V13 要消灭的那句话,只是换了个原因。
        if (NoteLoopbackDenial(status, text) && BusinessRoute() is { ViaEdge: true } t2)
            return await Transport.Send(Profile!, t2.EndPoint, method, path, body, ct);
        return (status, text);
    }

    /// <summary>
    /// 订阅一条 SSE(自动选路)。★ 回环那半边逐字对齐 <c>Transport.OpenStream</c> 的三条纪律:
    /// <c>ResponseHeadersRead</c>(否则等一条**无限长**的响应体读完 = 永远收不到帧)、
    /// 无限超时(由 ct 决定生命周期)、**非 2xx 先读正文再抛**(丢掉正文会把"后端没起"
    /// 退化成"连不上",而这两件事的下一步完全不同)。
    /// </summary>
    public async Task OpenBusinessStreamAsync(string path, Func<string, Task> onLine,
                                              CancellationToken ct, HttpMethod? method = null,
                                              object? body = null,
                                              Action<int, string>? onNonSuccess = null)
    {
        var t = BusinessRoute()
                ?? throw new InvalidOperationException("尚未配对,而且这台也没被判成主机 —— 无处可订阅");
        if (t.ViaEdge)
        {
            await Transport.OpenStream(Profile!, t.EndPoint, path, onLine, ct, method, body, onNonSuccess);
            return;
        }
        using var req = LoopRequest(t.EndPoint, method ?? HttpMethod.Get, path, body);
        req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        using var r = await LoopHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!r.IsSuccessStatusCode)
        {
            var raw = await r.Content.ReadAsStringAsync(ct);
            // ★★ 推送流也要认降级信号,否则「非机主账户」那一格会**永远重连不上**:
            //   订阅被 403 拒 → 抛 → 退避重连 → 还是同一条回环路 → 再 403…… 一个死循环,
            //   而界面只会说「中枢的推送流报错」。降级之后下一轮重连自然走 Edge。
            NoteLoopbackDenial((int)r.StatusCode, raw);
            onNonSuccess?.Invoke((int)r.StatusCode, raw);
            throw new HttpRequestException($"流式请求被拒:{(int)r.StatusCode}");
        }
        using var stream = await r.Content.ReadAsStreamAsync(ct);
        using var rd = new StreamReader(stream, System.Text.Encoding.UTF8);
        while (!ct.IsCancellationRequested)
        {
            var line = await rd.ReadLineAsync(ct);
            if (line is null) break;          // 服务端关流 —— 交给调用方决定要不要重连
            await onLine(line);
        }
    }

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

    /// <summary>
    /// 发一次业务调用。这也是"我还是不是有效成员"的探针。
    /// ★ 走哪条路由 <see cref="BusinessRoute"/> 定 —— 主机打回环网关,副机走 Edge。
    /// </summary>
    public async Task<(int status, string body)> CallAsync(string path)
    {
        var route = BusinessRoute()
                    ?? throw new InvalidOperationException("尚未配对");
        try
        {
            var r = route.ViaEdge
                ? await Transport.SendWithHeaders(Profile!, route.EndPoint, HttpMethod.Get, path, null)
                : await LoopWithHeadersAsync(route.EndPoint, HttpMethod.Get, path, null);
            // ★ 与 SendBusinessAsync 同一条降级判据(见 NoteLoopbackDenial)——
            //   两处各写一份判据必然漂开,所以判据只有那一个函数,这里只负责重发。
            if (!route.ViaEdge && NoteLoopbackDenial(r.status, r.body)
                && BusinessRoute() is { ViaEdge: true } r2)
            {
                route = r2;
                r = await Transport.SendWithHeaders(Profile!, r2.EndPoint, HttpMethod.Get, path, null);
            }
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
        catch (Exception ex) when (!route.ViaEdge)
        {
            // ══════════════════════════════════════════════════════════════
            //  ★★★ 回环这条路上**一条 TLS 归因都不许跑**。
            //
            //  `TlsFailure.Classify` 的第一步是查【本机设备证书这个确定的本地事实】——
            //  那在 Edge 那条路上是对的(握手确实要用它),但在回环上它与失败毫无因果:
            //  网关没起来会被归成「本机证书过期,请重新配对」,而重新配对**先删本机私钥**。
            //  ⇒ 为一件"网关没起"的事销毁一个完好的身份。分支必须在这里就岔开。
            //
            //  ★ 这条路上的失败只有一种意思:**本机回环网关没应答**。说清它,并说清
            //    下一步不是重新配对、不是查防火墙(回环不过防火墙)、不是查副机。
            // ══════════════════════════════════════════════════════════════
            State = HubState.Offline;
            LastError = $"这台是中枢主机,业务口是回环网关 {route.EndPoint},而它没有应答:{ex.Message}"
                      + " —— 这**不是**配对/证书/防火墙的问题(回环不过防火墙),是**网关没起来**。"
                      + "看「设备」页那行起栈结果,或 " + HostSetup.EdgeLogPath;
            throw;
        }
        catch (Exception ex)
        {
            // 五种坏法症状相同("连不上")、处置各不相同 -> 必须分开报,否则用户会瞎折腾。
            State = ClassifyTlsFailure(ex, Profile) ?? HubState.Offline;
            // ★★ 文案**全部来自 transport 的 TlsFailure.Explain** —— 不在这里另写一份。
            //   判据与说法必须同源:它们一旦分家,归因改了而话没改(或反过来)的那一天
            //   不会有任何东西变红,而用户看到的是一句与结论对不上的建议。
            LastError = State switch
            {
                HubState.CertExpired          => TlsFailure.Explain(TlsFailureKind.ServerCertExpired),
                HubState.HubIdentityChanged   => TlsFailure.Explain(TlsFailureKind.HubIdentityChanged),
                // ★ 本机设备证书过期:处置是「重新配对」—— 续签路由那时已经够不着了(实测)。
                HubState.LocalCertExpired     => TlsFailure.Explain(TlsFailureKind.LocalDeviceCertExpired),
                // ★ 本机材料不可用:再拼上**是三种坏法里的哪一种**,尤其"私钥不在了"要点明
                //   它按设计拷不过来也找不回来,否则用户会一直找。
                HubState.LocalProfileUnusable => TlsFailure.Explain(TlsFailureKind.LocalProfileUnusable)
                                                 + " " + TlsFailure.ExplainLocal(TlsFailure.CheckLocalMaterials(Profile)),
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
        if (BusinessRoute() is null) return;
        try
        {
            // ★★ 必须与 LeaseKeeper 走**同一条路**:中枢按 `principal_device` 匹配持有者,
            //   回环得 "local"、经 Edge 得 device_id —— 两条路各发一半,退出时就一条也放不掉。
            //   (这正是审计 B3「同一台机器在两个面上叫两个名字」那个形状,不能再造一个。)
            await SendBusinessAsync(HttpMethod.Post, "/v1/session/end",
                                    new { reason = "client-exit", device = Environment.MachineName }, ct);
        }
        catch (Exception ex) { LastError = "结束会话通知失败(不影响退出): " + ex.Message; }
    }

    /// <summary>主机是否明确表达了"这台设备被吊销了"。没有明确信号一律不认定(fail-safe 偏保守)。</summary>
    static bool LooksRevoked(string body) =>
        body.Contains("lan_device_unknown", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("revoked", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// TLS 失败的五分类 —— **整个判据都在 transport 的 <see cref="TlsFailure.Classify"/> 里**,
    /// 这里只做枚举映射。决议包 cert-lifecycle-2026-08-05 §2.3。
    ///
    /// ★★ 为什么这里一行判据都不许自己写(这一条是承重的):
    ///   老实现靠**异常 Message 里的英文单词**认因,而实测有两处结构性失灵 ——
    ///   ① 「本机设备证书过期」的异常链是 IOException -> Win32Exception,消息是**本地化**的,
    ///      英文针一条都对不上,于是掉进 Offline =「中枢没开机」;
    ///   ② 「主机重铸身份」时 .NET 发的是 `RemoteCertificateNameMismatch, RemoteCertificateChainErrors`,
    ///      里面**一个链状态词都没有**,只认 UntrustedRoot/PartialChain 的判据全部落空。
    ///   `TlsFailure.Classify` 先查**本机证书这个确定的本地事实**,再去解读会漂移的异常文本 ——
    ///   顺序是承重的,而且它被 transport selftest 用**实测原文**钉着。两边各写一份必然漂开。
    ///
    /// ★★★ 老代码那条兜底 `if (e is AuthenticationException) return HubIdentityChanged;` **已删**:
    ///   实测拨到一个跑普通 HTTP 的地址(路由器/NAS 管理页,或 DHCP 把旧地址分给了别人)时,
    ///   异常是 `AuthenticationException: Cannot determine the frame size...`,兜底会判成
    ///   「必须重新配对」—— 而重新配对**先删本机私钥**。为一个填错地址的问题销毁有效身份。
    ///   ★ 但**不能只删不换**:重铸身份那一格原来正是靠这条兜底答对的。名字不匹配的分支
    ///   已在 `TlsFailure.Classify` 里(②),所以这里直接调用它即可 —— 见决议包 §2.3 的显著警告。
    ///
    /// ★ 判不出来返回 null,由调用方归到普通的"连不上" —— **别猜**。
    /// </summary>
    static HubState? ClassifyTlsFailure(Exception ex, ClientProfile? profile) =>
        TlsFailure.Classify(ex, profile, DateTimeOffset.UtcNow) switch
        {
            TlsFailureKind.LocalProfileUnusable   => HubState.LocalProfileUnusable,
            TlsFailureKind.LocalDeviceCertExpired => HubState.LocalCertExpired,
            TlsFailureKind.ServerCertExpired      => HubState.CertExpired,
            TlsFailureKind.HubIdentityChanged     => HubState.HubIdentityChanged,
            _ => null,                              // ★ 判不出来就【别猜】
        };

    /// <summary>
    /// 本机设备证书的**提前**告警。null = 不该打扰用户。
    ///
    /// ★★ 这是「过期**之前**就要看得见」的落点。此前整套归因都发生在**握手失败之后** ——
    ///   而那时已经晚了:实测证书一旦真过期,续签路由就够不着了(lan-edge selftest 甲2),
    ///   自愈的窗口恰好在**还连得上**的那段时间里,也就是这个属性唯一有机会说话的时候。
    /// ★ RenewDue 段不出声(系统正在正常自愈);Critical / 已过期才出声。判据在 CertLifecycle,
    ///   两侧共用同一份,免得"主机以为还早、客户端以为该急了"。
    /// ★ 时间单独开一个可注入的重载 —— 断言不许读真实时钟(ASSERTION-PITFALLS 第 5 条)。
    /// </summary>
    public string? CertWarning => CertWarningAt(DateTimeOffset.UtcNow);

    /// <summary><see cref="CertWarning"/> 的可注入时间版本(断言用这个)。</summary>
    public string? CertWarningAt(DateTimeOffset now) => TlsFailure.WarnLocalCert(Profile, now);

    /// <summary>
    /// 轻量连通性探测:启动时用,判断中枢在不在线 / 本机是否仍是成员。
    ///
    /// ★★ 连之前先**自愈一次**:设备证书进入续签窗口(或上一次续签崩在半路)就把它做完。
    ///   这是 `Transport.RenewDeviceCertIfDue` 在客户端里**唯一**的调用点 ——
    ///   在它接上之前,那套续签代码是随包发布的死代码,而实机上设备证书 90 天一到就只能重新配对。
    /// </summary>
    public async Task<HubState> ProbeAsync()
    {
        if (Profile is null) { State = HubState.NotPaired; return State; }
        State = HubState.Connecting;
        await TryRenewDeviceCertAsync();
        try { await CallAsync("/v1/models"); }
        catch { /* State 已在 CallAsync 里置为 Offline */ }
        // ══════════════════════════════════════════════════════════════════
        //  ★★★ V13 缺口的补丁(2026-08-08 对抗式复核抓出):
        //  业务通道走回环之后,这次探测就**再也没碰过 `Profile.Dial`** ——
        //  而那个地址仍然是聊天 / 内网同步 / 设备证书续签唯一的拨号目标。
        //  于是「改错地址」与「改对地址」在界面上长得一模一样(顶栏照样刷绿)。
        //  ⇒ 主机上**额外**验一次配对通道,结果单独放 PairingChannelError,不去盖 State。
        //  ★ 副机不做:它的业务通道**就是**配对通道,再问一遍是白花一次握手。
        // ══════════════════════════════════════════════════════════════════
        if (BusinessRoute() is { ViaEdge: false })
            try { await CheckPairingChannelAsync(); } catch { /* best-effort,绝不顶掉探测 */ }
        else PairingChannelError = null;
        Changed?.Invoke();
        return State;
    }

    /// <summary>
    /// 到点就续一次设备证书。**best-effort,绝不把探测顶掉**。
    ///
    /// ★★ 为什么必须包 try/catch(决议包 §2.6 给的片段里没有,那是个真缺口):
    ///   `RenewDeviceCertIfDue` 内部走的是 `Transport.Send`,中枢不在线时它**抛**。
    ///   不接住的话,`ProbeAsync` 会在调 `/v1/models` **之前**就抛出去,
    ///   于是 State 永远停在 Connecting,归因一格都跑不到 —— 「中枢没开机」这个最常见的情形
    ///   反而变成了转圈。★ 续签失败**不是**一个要报给用户的状态:证书还没到期,
    ///   下一次探测还会再试;真到了该喊的时候由 CertWarning 出声。
    /// </summary>
    async Task TryRenewDeviceCertAsync()
    {
        if (Profile is null || TryDial() is not { } ep) return;
        try
        {
            await Transport.RenewDeviceCertIfDue(Profile, ep, AppPaths.StateDir, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) { LastError = "设备证书自动续签这次没成:" + ex.Message; }
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

    /// <summary>
    /// 吊销一台设备(走局域网口 —— 按 D37/D48 这条**结构上**永远 404,见本类顶部那段说明)。
    /// ★ 返回值形状不变(界面按它写的),但应答体现在**真的被核对了** ——
    ///   与 <c>HubAdmin.RevokeAsync</c> 共用同一处解析。此前两个调用方都不看应答体,
    ///   一次失败的吊销与一次成功的吊销在界面上长得一模一样。
    /// </summary>
    public async Task<(int status, string body)> RevokeDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        if (Profile is null) throw new InvalidOperationException("尚未配对");
        var r = await Transport.Send(Profile, Dial(), HttpMethod.Post,
                                     "/admin/devices/revoke", new { deviceId }, ct);
        if (r.status == 200)
        {
            var (ok, gen, why) = HubAdmin.ParseRevokeBody(r.body);
            LastError = ok ? null : why;
            if (ok && gen <= 0) LastError = "吊销应答里的 generation 不是正数 —— 这次写盘可能没生效";
        }
        return r;
    }

    /// <summary>
    /// 把 <c>/admin/devices</c> 的正文解析成显示用的设备表。
    ///
    /// ★★ 2026-08-06(V4):这里**曾经是第二份解析器** —— 与 <c>HubAdmin.DevicesAsync</c>
    ///   各自解析同一个形状,而 DevicesView 的两条路径分别调它们(:912 与 :1366)。
    ///   两份代码解析同一个形状 ⇒ 服务端改一个键名**只会有一处被发现**,
    ///   另一处安静地退化成"设备名全空",看起来像"主机上没有别的设备"。
    ///   ⇒ 现在它**委派**给唯一那一处,自己只做投影(AdminDevice → HubDevice)。
    /// ★ 形状不认识时**抛**,不返回空表:空表在界面上会被写成「没有别的设备」,
    ///   而那是一句**看起来很有信息量的假答案**(与 PendingAsync 的 ok 位同一条纪律)。
    /// </summary>
    public static List<HubDevice> ParseDevices(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var (ok, list, why) = HubAdmin.ParseDevices(doc.RootElement);
        if (!ok) throw new FormatException(why ?? "设备表的形状与登记的契约对不上");
        return list.Select(d => new HubDevice(d.DeviceId, d.DisplayName, d.Status)).ToList();
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
