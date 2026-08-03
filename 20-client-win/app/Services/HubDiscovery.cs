// P3c -- 局域网里【自己找到中枢】(客户端单侧,主机零改动)。
//
// 用户要的是「两边都开着就该一键连上」。做法有三条,这里选第三条:
//   A · mDNS / DNS-SD —— 标准做法,但 **D43 已把它推迟到 P3b.2**(精简优先),而且它要在主机上
//       多开一个会应答任何人的广播服务:局域网里任何设备一问就知道"这儿有个 LocalAI 中枢"。
//   B · 自研 UDP 广播发现 —— 同上,仍是给主机新开一个监听面,还得自己写协议、自己审。
//   C · **客户端扫自己所在的 /24**(采纳)—— 主机**一行代码都不用改、一个新端口都不用开**,
//       它本来就在 8443 上听着。发现完全发生在客户端这一侧。
//
// ★★ 最要紧的一条:**发现不建立信任**。
//   这里做的全部事情是「把地址找出来」。到底连不连、认不认,仍然由后面两道关决定:
//     · 首次配对 —— **六个词**必须两边逐字一致(而且词是从协议版本+双方随机数+CSR 指纹推出来的,
//       两边有任何一处对不上,词就对不上);
//     · 之后每次连接 —— mTLS 对着**配对时钉住的那个 CA**校验。
//   所以就算局域网里有人假装成中枢来应答,他也只能骗到"地址栏里多了一行",骗不到任何一次连接。
//   ⇒ 正因为如此,这里的 TLS 握手【故意不校验证书】:我们只是想读一下对方证书上的名字,
//     这一步**不做任何信任判断**。别把它误读成"我们接受任意证书"。
//
// ★ 如实的边界(界面要照着说,不许含糊):
//   · 只扫**掩码不宽于 /24** 的网卡,而且按**真实掩码**算范围(不是拿前三段拼个 /24)。
//     曾经两头都说错:掩码比 /24 宽(/22、/16 这类企业网)时整张网卡被**静默跳过**、一个地址都不扫,
//     界面却照旧给出"确认在同一网段"之类的原因 —— 而"同一网段"用户一查确实成立,于是白折腾;
//     掩码比 /24 窄(/25、/26)时反而扫到了**包住它的那个 /24**,大半地址经默认网关发去隔壁子网,
//     那才是真正像端口扫描的形状。现在两头都按真实掩码来,并且**把跳过了谁如实回报给界面**;
//   · 跨网段、跨 VLAN 找不到 —— 那时手填地址;
//   · 主机防火墙拦着 8443 就找不到(P3b 的 lan-firewall.ps1 负责放行);
//   · 找到多个中枢是**正常情况**(合租 / 邻居 / 自己装了两台),这时【绝不替用户挑一个】。

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace LocalAI.Client.Services;

/// <summary>扫到的一个中枢。HubId 来自它服务器证书上的名字 `localai-&lt;hubid&gt;.local`。</summary>
public sealed record FoundHub(string Ip, int Port, string HubId, string ServerName, string CertSha256)
{
    public string Dial => $"{Ip}:{Port}";
}

/// <summary>
/// 一次扫描的完整结果。★ 带上 Scanned/SkippedTooWide 是因为「没找到」有两种,
/// 而它们的下一步完全不同:扫过了没找到 → 查 Edge / 网段 / 防火墙;
/// 压根没扫 → 这条路结构上就走不通,只能手填。不回报就只能瞎猜。
/// </summary>
public sealed record ScanResult(List<FoundHub> Hits, List<string> Scanned, List<string> SkippedTooWide);

public static class HubDiscovery
{
    public const int EdgePort = 8443;

    /// <summary>
    /// 把配对档案里的 hub_id 换算成**证书名里那个短号**。
    ///
    /// ★★ 两处用的【不是同一个字符串】,不换算就永远比不上:
    ///   · 配对档案 / `/admin/ping` 给的是 `hub_id` —— 一个 **UUID**(identity 的 hub.json 里那个);
    ///   · 服务器证书名是 `localai-&lt;hub_id_short&gt;.local`,short = UUID **前 80 位**的小写 base32(16 字符)。
    ///   拿 UUID 直接去比 HubIdFromServerName 的结果,恒不相等 ——「在局域网里找回它」会永远失败,
    ///   而且失败得毫无线索(界面只会说"局域网里没找到这个中枢")。
    /// ★ 已经是短号形状就原样返回;认不出就返回 null —— 宁可不比,也不瞎比。
    /// </summary>
    public static string? ShortHubId(string? hubId)
    {
        if (string.IsNullOrWhiteSpace(hubId)) return null;
        var t = hubId.Trim();
        if (Guid.TryParse(t, out var g)) return LocalAI.Identity.HubId.Short(g);
        // base32(RFC 4648 小写):a-z 与 2-7,恰好 16 位
        if (t.Length == 16 && t.All(c => (c >= 'a' && c <= 'z') || (c >= '2' && c <= '7'))) return t;
        return null;
    }

    /// <summary>并发上限。扫 254 个地址,不限并发会把网卡和防火墙日志淹了。</summary>
    const int MaxParallel = 64;

    /// <summary>
    /// 扫本机各网卡所在的 /24,找出所有在 8443 上应答、且证书名形如 `localai-*.local` 的主机。
    /// </summary>
    /// <param name="connectTimeoutMs">单个地址的 TCP 连接超时。局域网内 300ms 足够;给慢网络留到 600。</param>
    public static async Task<ScanResult> ScanAsync(int connectTimeoutMs = 300, CancellationToken ct = default)
    {
        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scanned = new List<string>();
        var skipped = new List<string>();
        foreach (var (ip, mask) in LocalV4WithMask())
        {
            // ★ 只处理 /24 及更窄的:再宽就是几万个地址,扫它既慢又像在做端口扫描。
            //   ★★ 但【跳过了要说】—— 静默跳过时界面会给出一串根本不是原因的原因。
            if (mask < 24) { skipped.Add($"{ip}/{mask}"); continue; }
            var hosts = HostsOf(ip, mask).ToList();
            if (hosts.Count == 0) { skipped.Add($"{ip}/{mask}"); continue; }   // /31、/32 上没有别的主机
            scanned.Add($"{Network(ip, mask)}/{mask}");
            foreach (var t in hosts) if (seen.Add(t)) targets.Add(t);
        }

        var found = new List<FoundHub>();
        var gate = new SemaphoreSlim(MaxParallel);
        var tasks = targets.Select(async t =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var hub = await ProbeOneAsync(t, EdgePort, connectTimeoutMs, ct);
                if (hub is not null) lock (found) found.Add(hub);
            }
            catch { /* 单个地址探不通是常态,不是错误 */ }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        return new ScanResult(found.OrderBy(h => h.Ip, StringComparer.Ordinal).ToList(), scanned, skipped);
    }

    /// <summary>
    /// 按**真实掩码**列出这个子网里的可用主机地址(去掉网络号与广播地址)。
    /// ★ 开成 public 是【诚实的测试缝】:扫描范围对不对只能靠逐个地址核,搜字符串核不出来。
    /// </summary>
    public static IEnumerable<string> HostsOf(string ip, int mask)
    {
        if (!IPAddress.TryParse(ip, out var parsed)) yield break;
        var b = parsed.GetAddressBytes();
        if (b.Length != 4) yield break;
        uint addr = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        uint m = mask == 0 ? 0u : uint.MaxValue << (32 - mask);
        uint net = addr & m, bcast = net | ~m;
        for (uint a = net + 1; a < bcast; a++)
            yield return $"{(a >> 24) & 255}.{(a >> 16) & 255}.{(a >> 8) & 255}.{a & 255}";
    }

    /// <summary>这个地址所在子网的网络号(只为把"扫了哪个网段"如实说出来)。同样是测试缝。</summary>
    public static string Network(string ip, int mask)
    {
        if (!IPAddress.TryParse(ip, out var parsed)) return ip;
        var b = parsed.GetAddressBytes();
        uint addr = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        uint m = mask == 0 ? 0u : uint.MaxValue << (32 - mask);
        uint net = addr & m;
        return $"{(net >> 24) & 255}.{(net >> 16) & 255}.{(net >> 8) & 255}.{net & 255}";
    }

    /// <summary>
    /// 探一个地址:TCP 通 → TLS 握手 → 读证书上的名字。
    /// ★ 证书**不校验**(见文件头):这一步只读名字,不做信任判断。
    /// </summary>
    public static async Task<FoundHub?> ProbeOneAsync(string ip, int port, int timeoutMs, CancellationToken ct = default)
    {
        using var tcp = new TcpClient();
        var connect = tcp.ConnectAsync(ip, port, ct).AsTask();
        if (await Task.WhenAny(connect, Task.Delay(timeoutMs, ct)) != connect || !tcp.Connected) return null;
        await connect;

        X509Certificate2? cert = null;
        try
        {
            using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                // ★★ 恒 true —— 这里【不做信任判断】,只为把对方证书拿到手读个名字。
                //   真正的信任在两处:配对的六个词、以及之后 mTLS 对钉住的 CA 的校验。
                userCertificateValidationCallback: (_, c, _, _) =>
                {
                    if (c is not null) cert = new X509Certificate2(c);
                    return true;
                });
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs * 3);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = ip,
                // 不带客户端证书:发现阶段我们可能还没有身份(第一次配对就是这种情形)
                RemoteCertificateValidationCallback = (_, c, _, _) =>
                {
                    if (c is not null) cert = new X509Certificate2(c);
                    return true;
                },
            }, cts.Token);
        }
        catch { /* 握手失败:多半不是我们的中枢(或者是别的 TLS 服务)——不算错误 */ }

        if (cert is null) return null;
        var name = cert.GetNameInfo(X509NameType.DnsName, forIssuer: false) ?? "";
        var hubId = HubIdFromServerName(name);
        var thumb = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.RawData));
        cert.Dispose();
        return hubId is null ? null : new FoundHub(ip, port, hubId, name, thumb);
    }

    /// <summary>
    /// 从证书名里取 hub_id。命名来自 identity:`localai-&lt;hubShort&gt;.local`。
    /// 认不出这个形状就返回 null —— 那说明 8443 上蹲着的是别的东西,不是我们的中枢。
    /// </summary>
    public static string? HubIdFromServerName(string? serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName)) return null;
        const string pre = "localai-", suf = ".local";
        if (!serverName.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return null;
        if (!serverName.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) return null;
        var id = serverName[pre.Length..^suf.Length];
        return id.Length == 0 ? null : id;
    }

    /// <summary>本机的 IPv4 与掩码位数(跳过回环、未启用的网卡与 APIPA 自封地址)。</summary>
    static IEnumerable<(string Ip, int MaskBits)> LocalV4WithMask()
    {
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
                if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var s = a.Address.ToString();
                if (s.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                yield return (s, a.PrefixLength);
            }
        }
    }
}
