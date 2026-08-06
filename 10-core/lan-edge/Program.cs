// P3b S4 -- LocalAI LAN Edge. Terminates mTLS with the TPM server key, validates client certs
// against the project CA + membership store, proxies business requests to the loopback FastAPI
// gateway (injecting the verified cert fingerprint, stripping any client X-LocalAI-* header), and
// hosts the anonymous /pair/* routes wired to the S2 pairing service. LocalAI, decision D43.
//
// S4 binds LOOPBACK ONLY ("仍不开放 LAN"). Real LAN bind + firewall = S5. Under 精简优先 the Edge is
// one process holding the identity materials + pairing; the 3-service split is P3b.2.
//
//   run       start the Edge from {state}/identity against upstream 127.0.0.1:8080 (loopback)
//   selftest  self-contained integration test against a scratch identity + a stub upstream

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LocalAI.Identity;
using Microsoft.AspNetCore.Server.Kestrel.Https;

try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected/legacy console -- ignore */ }

return (args.Length == 0 ? "" : args[0]) switch
{
    "selftest" => await Selftest(),
    "client-e2e" => await ClientE2E(),
    "run" => await Run(),
    "run-lan" => await RunLan(args),
    "admin-e2e" => await AdminE2E(),
    _ => Usage(),
};

static int Usage() { Console.WriteLine("usage: localai-lan-edge <run | run-lan <bind-ip> | selftest | client-e2e>"); return 2; }

// ---------------------------------------------------------------- P3c S2: 管理面 E2E(仅回环)
// 要证明的核心性质:管理面【结构上】只有主机本机够得到 —— 副机即便持有有效成员证书,
// 从局域网口也访问不到 /admin/*(拿到 404,连存在性都不暴露)。这是 D37「管理操作仅主机本地」
// 在代码里的落实,也是把"批准新设备"搬进客户端界面之后仍保住"物理在场"的依据。
static async Task<int> AdminE2E()
{
    int pass = 0, fail = 0;
    void Assert(bool c, string m) { if (c) { pass++; Console.WriteLine("  PASS  " + m); } else { fail++; Console.WriteLine("  FAIL  " + m); } }

    var root = Path.Combine(Path.GetTempPath(), "localai-admin-" + Guid.NewGuid().ToString("N")[..8]);
    var idDir = Path.Combine(root, "identity");
    var secDir = Path.Combine(root, "secrets");
    const int TlsPort = 18453, AdminPort = 18452, UpPort = 18092;
    string? caKey = null, srvKey = null, cliKey = null, srvThumb = null;
    WebApplication? edge = null, upstream = null;
    var swProv = new CngProvider(Ca.TlsKeyProvider);
    byte[] R(int n) => RandomNumberGenerator.GetBytes(n);

    try
    {
        var hub = Identity.Init(idDir, secDir);
        caKey = hub.CaKeyName; srvKey = hub.ServerKeyName;
        srvThumb = JsonDocument.Parse(File.ReadAllText(Path.Combine(secDir, "identity-locators.json")))
                   .RootElement.GetProperty("server_thumbprint").GetString();
        var caPublic = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(idDir, "ca.cer")));
        var pairing = new Pairing(idDir, secDir);

        var ub = WebApplication.CreateBuilder(); ub.Logging.ClearProviders();
        ub.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, UpPort));
        upstream = ub.Build();
        upstream.Map("/{**r}", () => Results.Text("up"));
        await upstream.StartAsync();

        edge = Edge.Build(new EdgeConfig(idDir, secDir, $"http://127.0.0.1:{UpPort}", TlsPort, null, null,
                                         AdminPort: AdminPort, OpenPairingWindowOnStart: false),
                          pairingOverride: pairing);
        await edge.StartAsync();

        var admin = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{AdminPort}") };
        async Task<(int, JsonElement)> A(HttpMethod m, string p, object? b = null)
        {
            using var q = new HttpRequestMessage(m, p);
            if (b is not null) q.Content = new StringContent(JsonSerializer.Serialize(b), Encoding.UTF8, "application/json");
            using var r = await admin.SendAsync(q);
            var t = await r.Content.ReadAsStringAsync();
            return ((int)r.StatusCode, t.Length > 0 && t[0] is '{' or '[' ? JsonDocument.Parse(t).RootElement : default);
        }

        // ---- 管理面在回环上可用,且配对窗口**默认关闭** ----
        var (ps, pj) = await A(HttpMethod.Get, "/admin/ping");
        Assert(ps == 200 && pj.GetProperty("ok").GetBoolean(), "回环管理面可达 /admin/ping");
        Assert(!pj.GetProperty("pairingWindowOpen").GetBoolean(), "★ 启动时配对窗口**默认关闭**(自启不再自动敞开准入)");

        // ---- 窗口关着时,配对请求被拒 ----
        HttpClient Tls(X509Certificate2? cc)
        {
            var h = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
            h.SslOptions.CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
            h.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(caPublic);
            if (cc is not null) h.SslOptions.ClientCertificates = new X509CertificateCollection { cc };
            h.ConnectCallback = async (_, ct) => { var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true }; await s.ConnectAsync(IPAddress.Loopback, TlsPort, ct); return new NetworkStream(s, true); };
            return new HttpClient(h);
        }
        var tlsBase = $"https://{hub.ServerName}:{TlsPort}";

        cliKey = "localai-adminE2E-" + Convert.ToHexString(R(4)).ToLowerInvariant();
        using var cliEc = new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, cliKey,
            new CngKeyCreationParameters { Provider = swProv, ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing }));
        var csr = new CertificateRequest("CN=client", cliEc, HashAlgorithmName.SHA256).CreateSigningRequest();
        var claimSecret = R(32); var claimHash = SHA256.HashData(claimSecret); var cNonce = R(32);
        object EnrollBody() => new { csr = Convert.ToBase64String(csr), clientNonce = Convert.ToBase64String(cNonce), claimSecretHash = Convert.ToBase64String(claimHash), protocolVersion = 1, displayName = "2nd PC" };

        using (var plain = Tls(null))
        using (var r0 = await plain.PostAsync(tlsBase + "/pair/enroll", new StringContent(JsonSerializer.Serialize(EnrollBody()), Encoding.UTF8, "application/json")))
            Assert((int)r0.StatusCode == 403, "窗口关闭时 /pair/enroll 被拒(403),陌生机器塞不进队列");

        // ---- 用管理面显式开窗 ----
        var (ws, wj) = await A(HttpMethod.Post, "/admin/pairing/window", new { open = true, minutes = 5 });
        Assert(ws == 200 && wj.GetProperty("pairingWindowOpen").GetBoolean(), "可通过管理面显式打开配对窗口");

        // ---- 配对请求进队,管理面能看到六个词 ----
        JsonElement en;
        using (var plain = Tls(null))
        using (var r1 = await plain.PostAsync(tlsBase + "/pair/enroll", new StringContent(JsonSerializer.Serialize(EnrollBody()), Encoding.UTF8, "application/json")))
            en = JsonDocument.Parse(await r1.Content.ReadAsStringAsync()).RootElement;
        var reqId = en.GetProperty("requestId").GetString()!;
        var hostSas = en.GetProperty("sas").EnumerateArray().Select(x => x.GetString()!).ToArray();

        var (ls, lj) = await A(HttpMethod.Get, "/admin/pairing/pending");
        var pend = lj.GetProperty("pending").EnumerateArray().ToList();
        Assert(ls == 200 && pend.Count == 1, "管理面能列出待批准请求");
        Assert(pend[0].GetProperty("sas").EnumerateArray().Select(x => x.GetString()!).SequenceEqual(hostSas),
               "★ 待批列表里的六个词与客户端拿到的**一致**(界面据此人工比对)");
        Assert(pend[0].GetProperty("secondsLeft").GetInt32() is > 0 and <= 300, "待批请求带倒计时(到点自动消失,避免批到陈旧请求)");

        // ---- ★ 安全性质:从局域网口(TLS)访问 /admin 一律 404 ----
        using (var plain = Tls(null))
        using (var rx = await plain.GetAsync(tlsBase + "/admin/devices"))
            Assert((int)rx.StatusCode is 404 or 401, $"★ 局域网口访问 /admin/devices 被挡({(int)rx.StatusCode})");

        // ---- 批准 -> 领证 -> 完成 ----
        var (asx, _) = await A(HttpMethod.Post, "/admin/pairing/approve", new { requestId = reqId });
        Assert(asx == 200, "管理面可以批准配对请求");

        JsonElement st, cl;
        using (var plain = Tls(null))
        {
            using var r2 = await plain.PostAsync(tlsBase + "/pair/status", new StringContent(JsonSerializer.Serialize(new { requestId = reqId, claimSecret = Convert.ToBase64String(claimSecret) }), Encoding.UTF8, "application/json"));
            st = JsonDocument.Parse(await r2.Content.ReadAsStringAsync()).RootElement;
            var claimNonce = Convert.FromBase64String(st.GetProperty("claimNonce").GetString()!);
            var candSha = st.GetProperty("candidateSha256").GetString()!;
            var chal = Pairing.BuildChallenge(Convert.FromHexString(reqId), claimNonce, Convert.FromHexString(candSha));
            using var r3 = await plain.PostAsync(tlsBase + "/pair/claim", new StringContent(JsonSerializer.Serialize(new { requestId = reqId, claimSecret = Convert.ToBase64String(claimSecret), challengeSig = Convert.ToBase64String(cliEc.SignData(chal, HashAlgorithmName.SHA256)) }), Encoding.UTF8, "application/json"));
            cl = JsonDocument.Parse(await r3.Content.ReadAsStringAsync()).RootElement;
        }
        using var cand = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(cl.GetProperty("candidateCert").GetString()!));
        using (var withKey = cand.CopyWithPrivateKey(cliEc))
        using (var mtls = Tls(withKey))
        using (var r4 = await mtls.PostAsync(tlsBase + "/pair/complete?requestId=" + reqId, new StringContent("")))
            Assert((int)r4.StatusCode == 200, "配对完成(mTLS PoP)");

        // ---- 已配对成员**依然**访问不到管理面 ----
        using (var withKey = cand.CopyWithPrivateKey(cliEc))
        using (var mtls = Tls(withKey))
        using (var r5 = await mtls.GetAsync(tlsBase + "/admin/devices"))
            Assert((int)r5.StatusCode == 404,
                   $"★★ 持**有效成员证书**从局域网口访问 /admin/devices 仍被挡({(int)r5.StatusCode}) —— 副机结构上无法批准/吊销设备");

        // ---- 管理面列设备 / 解除 ----
        var (ds, dj) = await A(HttpMethod.Get, "/admin/devices");
        var devs = dj.GetProperty("devices").EnumerateArray().ToList();
        Assert(ds == 200 && devs.Count == 1 && devs[0].GetProperty("status").GetString() == "active", "管理面能列出已配对设备");
        Assert(devs[0].GetProperty("certSha256Short").GetString() is { Length: 8 }, "设备带证书指纹前 8 位(同名设备可区分,不靠自报名)");

        var devId = devs[0].GetProperty("deviceId").GetString()!;
        var (rs, _) = await A(HttpMethod.Post, "/admin/devices/revoke", new { deviceId = devId });
        Assert(rs == 200, "管理面能解除设备");

        using (var withKey = cand.CopyWithPrivateKey(cliEc))
        using (var mtls = Tls(withKey))
        using (var r6 = await mtls.GetAsync(tlsBase + "/v1/models"))
            Assert((int)r6.StatusCode == 401, "解除后该设备的业务调用立即 401(吊销即时生效)");
    }
    catch (Exception ex) { fail++; Console.WriteLine("  FAIL  自检抛异常: " + ex.Message); }
    finally
    {
        if (edge is not null) await edge.StopAsync();
        if (upstream is not null) await upstream.StopAsync();
        if (caKey is not null) Ca.DeleteKey(caKey);
        if (srvKey is not null) Ca.DeleteKey(srvKey);
        if (cliKey is not null) Ca.DeleteKey(cliKey);
        if (srvThumb is not null) { try { using var s = new X509Store(StoreName.My, StoreLocation.CurrentUser); s.Open(OpenFlags.ReadWrite); foreach (var c in s.Certificates.Find(X509FindType.FindByThumbprint, srvThumb, false)) s.Remove(c); } catch { } }
        try { Directory.Delete(root, true); } catch { }
    }

    Console.WriteLine($"\nP3c S2 管理面 E2E(仅回环): PASS={pass} FAIL={fail}");
    return fail > 0 ? 1 : 0;
}

static bool IsElevated()
{
    try
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
    catch { return false; }
}

// ★★ 2026-08-04:真正要回答的问题是【身份密钥这个进程打不打得开】,不是「我是不是管理员」。
//   后者只是前者的代理指标,而在 UAC 关闭的机器上(EnableLUA=0)它对管理员账户**恒为真**:
//   那种机器上桌面 explorer 自己就跑在 High,根本不存在普通身份的进程,
//   身份当初也就是在 High 下铸的、在 High 下打得开。
//   拿代理指标当门槛,等于把一台完全健康的机器判成不能用,而且给出的理由
//   (「密钥集不存在」)是假的 —— 实测该机两把密钥在 High 进程里 CngKey.Open 都成功。
//   ⇒ 直接试着打开 CA 私钥。打得开就放行(什么完整性等级都行);
//     打不开才是真正要拦的情形,那时理由也是真的。
//   见 00-docs/decision-packets/integrity-guard-asks-wrong-question-2026-08-03.md
static bool CaKeyUsable(string secretsDir, out string note)
{
    try
    {
        var locPath = Path.Combine(secretsDir, "identity-locators.json");
        if (!File.Exists(locPath)) { note = "找不到 " + locPath; return false; }
        var loc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(locPath)).RootElement;
        var caKeyName = loc.GetProperty("ca_key_name").GetString()!;
        var caProvider = loc.TryGetProperty("ca_provider", out var cp) ? cp.GetString()! : Ca.TpmProvider;
        var prov = new System.Security.Cryptography.CngProvider(caProvider);
        if (!System.Security.Cryptography.CngKey.Exists(caKeyName, prov))
        { note = $"当前身份下看不到 CA 密钥「{caKeyName}」(provider: {caProvider})"; return false; }
        using var k = System.Security.Cryptography.CngKey.Open(caKeyName, prov);
        note = $"CA 密钥「{caKeyName}」打得开";
        return true;
    }
    catch (Exception ex) { note = "打开 CA 密钥失败:" + ex.Message; return false; }
}

// S5: open the LAN by binding a selected NIC address (not loopback). The narrow firewall rule
// (lan-firewall.ps1, run by the user, elevated) must already be in place -- until then the OS default
// inbound block keeps the port unreachable, so there is no "listening but unprotected" window.
static async Task<int> RunLan(string[] a)
{
    if (a.Length < 2 || !IPAddress.TryParse(a[1], out var ip))
    { Console.WriteLine("usage: localai-lan-edge run-lan <bind-ip>   (e.g. 192.168.178.61)"); return 2; }
    var idDir = Paths.IdentityDir();
    var secDir = Paths.SecretsDir();
    if (!Identity.IsInitialized(idDir)) { Console.WriteLine("no hub identity (run: localai-identity init)"); return 1; }

    // ★★ 先问真问题:CA 私钥【这个进程】打不打得开。打得开就继续 —— 什么完整性等级都行。
    //   (原来这里查的是 IsInRole(Administrator)。那只是代理指标,UAC 关闭的机器上恒为真,
    //    会把一台密钥明明打得开的健康机器挡在门外,理由还是假的。见 CaKeyUsable 上方的说明。)
    if (!CaKeyUsable(secDir, out var keyNote))
    {
        Console.WriteLine("✗ 打不开身份密钥(CA),中枢无法启动。");
        Console.WriteLine("  " + keyNote);
        if (IsElevated())
        {
            Console.WriteLine("  ★ 本进程是【管理员】身份 —— 而 TPM/CNG 用户密钥绑定【铸造时】的完整性等级。");
            Console.WriteLine("    如果这套身份当初是用普通用户铸的,就要用普通用户跑。");
        }
        else
        {
            Console.WriteLine("  ★ 本进程是普通用户身份 —— 如果这套身份当初是用【管理员】铸的,那就要用管理员跑。");
        }
        return 3;
    }
    var serverName = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(idDir, "hub.json"))).RootElement.GetProperty("server_name").GetString();

    // Real bring-up needs a human gate: the operator compares the six words shown here against the 2nd PC
    // screen, then types `approve`. We own the Pairing instance so the console REPL and the HTTP routes
    // share the same state; OnEnroll pushes each incoming request's SAS to this console.
    var pairing = new Pairing(idDir, secDir);
    // ★ 不再启动即开窗(审查发现 [3])。配对窗口必须是一次**显式**动作:
    //   控制台输入 open,或客户端在主机上点「允许新设备加入」(走回环管理面)。
    //   否则开机自启的常驻 Edge = 每次开机自动敞开 30 分钟无人值守准入窗口。

    void OnEnroll(EnrollNotice n)
    {
        Console.WriteLine();
        Console.WriteLine("=== 新配对请求 =====================================");
        Console.WriteLine($"  请求 {n.RequestId[..8]}    设备名: {(string.IsNullOrEmpty(n.DisplayName) ? "(未命名)" : n.DisplayName)}");
        Console.WriteLine($"  六个词:  {string.Join("  ", n.Sas)}");
        Console.WriteLine($"  -> 在第二台 PC 屏幕上核对这六个词,一致则输入:  approve {n.RequestId[..8]}");
        Console.WriteLine("===================================================");
        Console.Write("> ");
    }

    const int AdminPort = 8442;   // 仅回环,供本机客户端做设备管理(D37:管理操作仅主机本地)
    var app = Edge.Build(new EdgeConfig(idDir, secDir, "http://127.0.0.1:8080", 8443, ip, OnEnroll,
                                        AdminPort: AdminPort, OpenPairingWindowOnStart: false),
                         pairingOverride: pairing);
    // ★★ 端口被占是【最常见】的一种启动失败(多半是中枢已经在跑了),
    //   而它本来会抛一整屏 Kestrel 的未捕获异常栈 —— 人在黑窗口里看到那一堆,
    //   根本读不出"你已经开着一个了"。用一句话说清楚,并给可执行的下一步。
    try
    {
        await app.StartAsync();
    }
    catch (IOException ex) when (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
    {
        Console.WriteLine($"✗ {ip}:8443 已经被占用 —— 中枢无法启动。");
        Console.WriteLine("  ★ 最常见的原因:中枢已经在跑了(另一个黑窗口)。那就不用再开一个。");
        Console.WriteLine("  否则:看看是谁占着 ——  netstat -ano | findstr :8443");
        return 4;
    }

    var expiry = Identity.ServerCertExpiry(idDir);
    var daysLeft = (expiry - DateTimeOffset.UtcNow).TotalDays;

    Console.WriteLine($"LAN Edge 已监听 {ip}:8443   ->  上游 127.0.0.1:8080");
    Console.WriteLine($"  证书名(SAN)   : {serverName}");
    Console.WriteLine($"  第二台 PC 连接 : https://{serverName}:8443   (拨号 {ip}:8443)");
    Console.WriteLine($"  管理面(仅本机): http://127.0.0.1:{AdminPort}  —— 客户端用它管理设备,局域网访问不到");
    Console.WriteLine($"  配对窗口       : **关闭**(要加新设备请先输入 open)");
    Console.WriteLine($"  服务器证书     : {expiry:yyyy-MM-dd} 到期(剩 {daysLeft:F0} 天)");
    if (daysLeft < 10)
        Console.WriteLine($"  ! 证书快到期 —— 执行 localai-identity renew-server 续签(**不需要重新配对**)");
    Console.WriteLine($"  ★ 端口不可达时,先用 lan-firewall.ps1(管理员)放行");
    Console.WriteLine();
    Console.WriteLine("命令:  list | approve <id> | deny <id> | open [分钟] | close | quit");
    Console.Write("> ");

    // ★★ 无窗口运行(客户端帮你拉起、看不到黑框)时,stdin 是空的。
    //   原来这里 `if (line is null) break;` 会让它【当场退出】——
    //   中枢刚打印完"已监听"就死了,而人什么都看不见。(2026-08-04 实测撞到过。)
    //   ⇒ 没有可用的 stdin 就【不进 REPL】,安静地一直跑下去;
    //     设备管理走客户端的回环管理面,那本来就是正路。
    if (Console.IsInputRedirected)
    {
        Console.WriteLine();
        Console.WriteLine("(无控制台输入 —— 命令台已关闭。请在客户端的【设备】页里管理配对与设备。)");
        Console.Out.Flush();
        await System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite);
        return 0;
    }

    while (true)
    {
        var line = Console.ReadLine();
        if (line is null) break;   // stdin closed
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { Console.Write("> "); continue; }
        try
        {
            switch (parts[0].ToLowerInvariant())
            {
                case "list": case "l":
                    var pend = pairing.ListPending();
                    if (pend.Count == 0) Console.WriteLine("  (无待处理请求)");
                    foreach (var p in pend)
                        Console.WriteLine($"  {p.RequestId[..8]}  [{p.Status,-18}]  {(string.IsNullOrEmpty(p.DisplayName) ? "(未命名)" : p.DisplayName)}   {string.Join(" ", p.Sas)}");
                    break;
                case "approve": case "a":
                    if (parts.Length < 2) { Console.WriteLine("  用法: approve <请求id前几位>"); break; }
                    var ar = pairing.ResolveByPrefix(parts[1]);
                    pairing.Approve(ar);
                    Console.WriteLine($"  已批准 {ar[..8]} — 等待第二台 PC 领证并完成…(稍后 list 应显示 active)");
                    break;
                case "deny": case "d":
                    if (parts.Length < 2) { Console.WriteLine("  用法: deny <请求id前几位>"); break; }
                    var dr = pairing.ResolveByPrefix(parts[1]);
                    pairing.Deny(dr);
                    Console.WriteLine($"  已拒绝 {dr[..8]}");
                    break;
                case "open":
                    var mins = parts.Length >= 2 && int.TryParse(parts[1], out var mm) ? mm : 30;
                    pairing.OpenWindow(TimeSpan.FromMinutes(mins));
                    Console.WriteLine($"  配对窗口已开启 {mins} 分钟");
                    break;
                case "close":
                    pairing.CloseWindow();
                    Console.WriteLine("  配对窗口已关闭(不再接受新请求)");
                    break;
                case "help": case "?":
                    Console.WriteLine("  list | approve <id> | deny <id> | open [分钟] | close | quit");
                    break;
                case "quit": case "q": case "exit":
                    Console.WriteLine("  正在停止 Edge…");
                    await app.StopAsync();
                    return 0;
                default:
                    Console.WriteLine("  未知命令。输入 help 查看。");
                    break;
            }
        }
        catch (Exception ex) { Console.WriteLine("  ! " + ex.Message); }
        Console.Write("> ");
    }
    await app.StopAsync();
    return 0;
}

// Full client-transport flow over HTTP: enroll -> (host approves) -> status -> claim -> complete (mTLS)
// -> business call. This is the reference implementation for the standalone client on the 2nd PC.
static async Task<int> ClientE2E()
{
    int pass = 0, fail = 0;
    void Assert(bool c, string m) { if (c) { pass++; Console.WriteLine("  PASS  " + m); } else { fail++; Console.WriteLine("  FAIL  " + m); } }
    byte[] R(int n) => RandomNumberGenerator.GetBytes(n);

    var root = Path.Combine(Path.GetTempPath(), "localai-cliE2E-" + Guid.NewGuid().ToString("N")[..8]);
    var idDir = Path.Combine(root, "identity");
    var secDir = Path.Combine(root, "secrets");
    var swProv = new CngProvider("Microsoft Software Key Storage Provider");
    string? caKey = null, srvKey = null, clientKeyName = null, serverThumb = null;
    WebApplication? edge = null, upstream = null;
    try
    {
        var hub = Identity.Init(idDir, secDir);
        caKey = hub.CaKeyName; srvKey = hub.ServerKeyName;
        serverThumb = JsonDocument.Parse(File.ReadAllText(Path.Combine(secDir, "identity-locators.json"))).RootElement.GetProperty("server_thumbprint").GetString();
        var caPublic = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(idDir, "ca.cer")));
        var pairing = new Pairing(idDir, secDir);
        pairing.OpenWindow(TimeSpan.FromMinutes(30));

        int upPort = 18082; string? seenFp = null;
        var ub = WebApplication.CreateBuilder(); ub.Logging.ClearProviders();
        ub.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, upPort));
        upstream = ub.Build();
        upstream.Map("/{**r}", (HttpContext c) => { seenFp = c.Request.Headers["X-LocalAI-Cert-Sha256"].ToString(); return Results.Text("up"); });
        // ★ P4-S5 前置实测:代理到底能不能【流式】直通。
        //   代理用了 ResponseHeadersRead(不缓冲整体),但随后是 CopyToAsync 到 Response.Body,
        //   而且显式 Remove("transfer-encoding") —— 「能流式」此前是从这两行**推断**出来的,
        //   从没测过。若其实不能,S5 的推送就只能退回轮询,而**轮询正是 D37 ② 点名的失效模式**。
        //   这个 stub 每 200ms 吐一帧,共 3 帧;客户端按到达时刻判断是"边到边发"还是"攒完一起发"。
        upstream.MapGet("/__sse_probe", async (HttpContext c) =>
        {
            c.Response.Headers["Content-Type"] = "text/event-stream";
            c.Response.Headers["Cache-Control"] = "no-cache";
            for (int i = 0; i < 3; i++)
            {
                await c.Response.WriteAsync($"data: frame{i}\n\n");
                await c.Response.Body.FlushAsync();
                await Task.Delay(200);
            }
        });
        await upstream.StartAsync();

        edge = Edge.Build(new EdgeConfig(idDir, secDir, $"http://127.0.0.1:{upPort}", 18444), pairingOverride: pairing);
        await edge.StartAsync();
        var baseUrl = $"https://{hub.ServerName}:18444";

        HttpClient Mk(X509Certificate2? cc)
        {
            var h = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
            h.SslOptions.CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
            h.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(caPublic);
            if (cc is not null) h.SslOptions.ClientCertificates = new X509CertificateCollection { cc };
            h.ConnectCallback = async (_, ct) => { var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true }; await s.ConnectAsync(IPAddress.Loopback, 18444, ct); return new NetworkStream(s, true); };
            return new HttpClient(h);
        }
        async Task<JsonElement> PostJson(HttpClient c, string path, object body)
        {
            using var r = await c.PostAsync(baseUrl + path, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
            return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        }

        // client material (software key + CSR + claim secret)
        clientKeyName = "localai-cliE2E-" + Convert.ToHexString(R(4)).ToLowerInvariant();
        using var clientEcdsa = new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, clientKeyName, new CngKeyCreationParameters { Provider = swProv, ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing }));
        var csr = new CertificateRequest("CN=client", clientEcdsa, HashAlgorithmName.SHA256).CreateSigningRequest();
        var clientCsrSpkiSha = SHA256.HashData(clientEcdsa.ExportSubjectPublicKeyInfo());
        var claimSecret = R(32); var claimSecretHash = SHA256.HashData(claimSecret); var clientNonce = R(32);

        // 1. enroll (HTTP, anonymous)
        using var plain = Mk(null);
        var en = await PostJson(plain, "/pair/enroll", new { csr = Convert.ToBase64String(csr), clientNonce = Convert.ToBase64String(clientNonce), claimSecretHash = Convert.ToBase64String(claimSecretHash), protocolVersion = 1, displayName = "2nd PC" });
        var reqId = en.GetProperty("requestId").GetString()!;
        var serverNonce = Convert.FromBase64String(en.GetProperty("serverNonce").GetString()!);
        var hostSas = en.GetProperty("sas").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert(reqId.Length == 32 && hostSas.Length == 6, "enroll (HTTP) -> request id + six-word SAS");

        // client independently derives the SAS -> must match (this is what the human compares)
        var clientSas = Sas.Derive(pairing.BuildTranscript(1, claimSecretHash, clientNonce, serverNonce, Convert.FromHexString(reqId), clientCsrSpkiSha)).words;
        Assert(clientSas.SequenceEqual(hostSas), "client and host six-word SAS match over HTTP");

        // 2. host-admin approves (out of band)
        pairing.Approve(reqId);

        // 3. status (HTTP)
        var st = await PostJson(plain, "/pair/status", new { requestId = reqId, claimSecret = Convert.ToBase64String(claimSecret) });
        Assert(st.GetProperty("status").GetString() == "approved", "status (HTTP) -> approved after host approval");
        var claimNonce = Convert.FromBase64String(st.GetProperty("claimNonce").GetString()!);
        var candSha = st.GetProperty("candidateSha256").GetString()!;

        // 4. claim (HTTP) -- challenge signed by the CSR key
        var challenge = Pairing.BuildChallenge(Convert.FromHexString(reqId), claimNonce, Convert.FromHexString(candSha));
        var cl = await PostJson(plain, "/pair/claim", new { requestId = reqId, claimSecret = Convert.ToBase64String(claimSecret), challengeSig = Convert.ToBase64String(clientEcdsa.SignData(challenge, HashAlgorithmName.SHA256)) });
        using var candidate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(cl.GetProperty("candidateCert").GetString()!));
        Assert(Ca.VerifyChainAndEku(candidate, caPublic, Ca.OidClientAuth), "claim (HTTP) -> candidate cert chaining to CA + clientAuth");

        // 5. complete (mTLS with the candidate) -- the final PoP; then 6. a business call as an active member
        using var clientCert = candidate.CopyWithPrivateKey(clientEcdsa);
        using var mtls = Mk(clientCert);
        using (var rc = await mtls.PostAsync(baseUrl + "/pair/complete?requestId=" + reqId, new StringContent("")))
            Assert((int)rc.StatusCode == 200 && (await rc.Content.ReadAsStringAsync()) == "active", "complete (mTLS candidate) -> active (" + (int)rc.StatusCode + ")");

        using (var rb = await mtls.GetAsync(baseUrl + "/v1/models"))
            Assert((int)rb.StatusCode == 200 && seenFp == Convert.ToHexString(SHA256.HashData(candidate.RawData)), "business call as active member -> proxied with verified fingerprint");

        // ── ★★ P4-S5 前置:代理能否流式直通(实测,不是推断)──────────────────
        //   判据:三帧每隔 200ms 由上游吐出。若代理是**流式**的,第一帧会在
        //   最后一帧之前明显到达(间隔 ≳ 300ms);若它把整个响应**缓冲**完再发,
        //   三帧会几乎同时到达(间隔 ≈ 0)。
        //   ★ 这条测的是**代理**,不是上游 —— 上游自己已经 FlushAsync 过了。
        {
            using var sreq = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/__sse_probe");
            using var sresp = await mtls.SendAsync(sreq, HttpCompletionOption.ResponseHeadersRead);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var arrivals = new List<long>();
            using (var stream = await sresp.Content.ReadAsStreamAsync())
            {
                var buf = new byte[256];
                int n;
                while ((n = await stream.ReadAsync(buf)) > 0)
                {
                    arrivals.Add(sw.ElapsedMilliseconds);
                    if (arrivals.Count >= 3) break;
                }
            }
            var spread = arrivals.Count >= 2 ? arrivals[^1] - arrivals[0] : 0;
            Assert((int)sresp.StatusCode == 200, "SSE 探针:状态码 200 (" + (int)sresp.StatusCode + ")");
            Assert(arrivals.Count >= 2, $"SSE 探针:收到多个数据块(实得 {arrivals.Count} 块)");
            // ★★ 承重的一条。它红,就意味着 S5 的推送不能走 lan-edge 直通,
            //   必须另想办法(而不是默默退回轮询 —— 那是 D37 ② 点名的失效模式)。
            Assert(spread >= 300,
                   $"★★ 代理是【流式】直通:首末块间隔 {spread}ms ≥ 300ms " +
                   "(若 ≈0 说明代理把整个响应缓冲完才发,S5 推送不能走这条路)");
        }
    }
    finally
    {
        if (edge is not null) await edge.StopAsync();
        if (upstream is not null) await upstream.StopAsync();
        if (caKey is not null) Ca.DeleteKey(caKey);
        if (srvKey is not null) Ca.DeleteKey(srvKey);
        if (clientKeyName is not null) { try { if (CngKey.Exists(clientKeyName, swProv)) CngKey.Open(clientKeyName, swProv).Delete(); } catch { } }
        if (serverThumb is not null) { try { using var s = new X509Store(StoreName.My, StoreLocation.CurrentUser); s.Open(OpenFlags.ReadWrite); foreach (var c in s.Certificates.Find(X509FindType.FindByThumbprint, serverThumb, false)) s.Remove(c); } catch { } }
        try { Directory.Delete(root, true); } catch { }
    }
    Console.WriteLine($"\nS4 client-transport E2E (full HTTP pairing): PASS={pass} FAIL={fail}");
    return fail > 0 ? 1 : 0;
}

static async Task<int> Run()
{
    var idDir = Paths.IdentityDir();
    var secDir = Paths.SecretsDir();
    if (!Identity.IsInitialized(idDir)) { Console.WriteLine("no hub identity (run: localai-identity init)"); return 1; }
    var app = Edge.Build(new EdgeConfig(idDir, secDir, "http://127.0.0.1:8080", 8443));
    Console.WriteLine("LAN Edge on https://127.0.0.1:8443 (loopback only; upstream 127.0.0.1:8080)");
    using var rotCts = new CancellationTokenSource();
    var rotLoop = RotationLoop(rotCts.Token);
    try { await app.RunAsync(); }
    finally { rotCts.Cancel(); try { await rotLoop; } catch (OperationCanceledException) { } }
    return 0;
}

// --------------------------------------------------------------------------- D? 服务器证书自动轮换循环
//
// ★★ 这个循环存在的理由,是 D49 只做了一半:它给了手动命令 + status 里 <10 天的提示,
//   而那两样加起来的意思是「**要有人记得去看**」。实机证据:D49 于 07-29 裁定,
//   到 08-05 勘察时 server.cer 的 LastWriteTime 仍是 07-29 —— 七天没人跑过一次 status。
//
// ★ fail-closed:每一跳的结果都打到控制台,失败**打红**并且**继续重试**。
//   停止重试 = 静默退回手动 = 退回那个"要有人记得"的状态,而这正是本循环要消灭的东西。
static async Task RotationLoop(CancellationToken ct)
{
    // ★ 一天一跳。续签窗口是 10 天(证书寿命的三分之一),所以在真正到期前
    //   **至少有 10 次**独立的尝试机会 —— 一两次网络抖动或 TPM 忙不会把这件事拖没。
    var period = TimeSpan.FromHours(24);
    while (!ct.IsCancellationRequested)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var r = Edge.Rotator;
            if (r is not null)
            {
                var outcome = r.Tick(now);
                var status = r.Status(now);
                Edge.LastRotation = status;
                if (outcome == RotationOutcome.Renewed)
                    Console.WriteLine($"[cert] 服务器证书已自动续签 -> {status.NotAfter:yyyy-MM-dd HH:mm}(已有连接未中断)");
                else if (outcome == RotationOutcome.Failed)
                {
                    // ★ 必须**响**。用 stderr + ASCII 前缀,便于任何一层日志抓取
                    //   (ASSERTION-PITFALLS 第 8 条:机器读的那几个字符必须是 ASCII)。
                    Console.Error.WriteLine("[cert] !! " + ServerCertRotator.Banner(status));
                    Console.Error.WriteLine("[cert] !! 自动续签失败,将继续重试。若一直失败,请在主机上手动执行:localai-identity renew-server");
                }
                else if (status.NeedsAttention)
                    Console.Error.WriteLine("[cert] ! " + ServerCertRotator.Banner(status));
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("[cert] !! 轮换循环自身出错(不影响服务,将重试): " + ex.Message); }
        try { await Task.Delay(period, ct); } catch (OperationCanceledException) { break; }
    }
}

// --------------------------------------------------------------------------- selftest
static async Task<int> Selftest()
{
    int pass = 0, fail = 0;
    void Assert(bool c, string m) { if (c) { pass++; Console.WriteLine("  PASS  " + m); } else { fail++; Console.WriteLine("  FAIL  " + m); } }
    byte[] R(int n) => RandomNumberGenerator.GetBytes(n);

    var root = Path.Combine(Path.GetTempPath(), "localai-edge-selftest-" + Guid.NewGuid().ToString("N")[..8]);
    var idDir = Path.Combine(root, "identity");
    var secDir = Path.Combine(root, "secrets");
    var prov = new CngProvider(Ca.TpmProvider);
    var swProv = new CngProvider("Microsoft Software Key Storage Provider");   // SChannel-usable, still non-exportable
    string? caKey = null, srvKey = null, clientKeyName = null, swSrvKeyName = null;
    WebApplication? edge = null, upstream = null;
    // 续签之后的**现役**设备证书(续签那一节填);null = 还没续过。
    X509Certificate2? currentCert = null;
    try
    {
        var hub = Identity.Init(idDir, secDir);
        caKey = hub.CaKeyName; srvKey = hub.ServerKeyName;
        var caPublic = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(idDir, "ca.cer")));

        // --- get an active paired client cert via the in-process pairing flow ---
        var pairing = new Pairing(idDir, secDir);
        pairing.OpenWindow(TimeSpan.FromMinutes(5));
        clientKeyName = "localai-edge-selftest-client-" + Convert.ToHexString(R(4)).ToLowerInvariant();
        var ckp = new CngKeyCreationParameters { Provider = swProv, ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing };
        using var clientEcdsa = new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, clientKeyName, ckp));
        var csr = new CertificateRequest("CN=client", clientEcdsa, HashAlgorithmName.SHA256).CreateSigningRequest();
        var claimSecret = R(32);
        var en = pairing.Enroll(csr, R(32), SHA256.HashData(claimSecret), 1, "edge-selftest");
        pairing.Approve(en.RequestId);
        var st = pairing.Status(en.RequestId, claimSecret);
        var challenge = Pairing.BuildChallenge(Convert.FromHexString(en.RequestId), st.ClaimNonce!, Convert.FromHexString(st.CandidateSha256!));
        using var candidate = pairing.Claim(en.RequestId, claimSecret, clientEcdsa.SignData(challenge, HashAlgorithmName.SHA256));
        pairing.Complete(en.RequestId);
        using var clientCert = candidate.CopyWithPrivateKey(clientEcdsa);   // paired cert + TPM key
        var clientFp = Convert.ToHexString(SHA256.HashData(candidate.RawData));
        var deviceId = Store.LoadOrEmpty(idDir).Devices[0].DeviceId;

        // --- stub upstream gateway: echoes what the Edge forwarded ---
        int upstreamPort = 18080;
        string? seenFp = null; string? seenSpoof = "UNSET";
        var ub = WebApplication.CreateBuilder();
        ub.Logging.ClearProviders();
        ub.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, upstreamPort));
        upstream = ub.Build();
        upstream.Map("/{**rest}", (HttpContext ctx) =>
        {
            seenFp = ctx.Request.Headers["X-LocalAI-Cert-Sha256"].ToString();
            seenSpoof = ctx.Request.Headers.TryGetValue("X-LocalAI-Client-Spoof", out var v) ? v.ToString() : null;
            return Results.Text("upstream-ok");
        });
        await upstream.StartAsync();

        // --- the Edge, using the identity's REAL server cert (software-KSP key per B17/D44 -> SChannel-usable) ---
        // ★ 这里带上 AdminPort:下面那条 /admin/ping 的**跨语言成对断言**要一个真的回环管理面。
        //   ★★ 为什么不放进 admin-e2e:门禁只跑 `lan-edge selftest` 这一条(run-tests.ps1 的 Args),
        //     放进 admin-e2e 就是写一条**没人跑的断言** —— ASSERTION-PITFALLS 第 10 条那种形状,
        //     而且它会躺在覆盖账里显得已被认真处置过。
        const int AdminPort = 18442;
        edge = Edge.Build(new EdgeConfig(idDir, secDir, $"http://127.0.0.1:{upstreamPort}", 18443, AdminPort: AdminPort));
        await edge.StartAsync();

        // client helper: custom root trust (CA), dial loopback, optional client cert
        HttpClient MkClient(X509Certificate2? cc)
        {
            var h = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
            h.SslOptions.CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
            h.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(caPublic);
            if (cc is not null) h.SslOptions.ClientCertificates = new X509CertificateCollection { cc };
            h.ConnectCallback = async (_, ct) => { var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true }; await s.ConnectAsync(IPAddress.Loopback, 18443, ct); return new NetworkStream(s, true); };
            return new HttpClient(h);
        }
        var baseUrl = $"https://{hub.ServerName}:18443";

        // 1. paired client -> business proxied; fingerprint injected; client spoof header stripped
        using (var c = MkClient(clientCert))
        {
            var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1/models");
            req.Headers.TryAddWithoutValidation("X-LocalAI-Client-Spoof", "attacker");
            req.Headers.TryAddWithoutValidation("X-LocalAI-Cert-Sha256", "SPOOFEDFINGERPRINT");
            using var r = await c.SendAsync(req);
            Assert((int)r.StatusCode == 200, "paired client -> business proxied 200 (" + (int)r.StatusCode + ")");
            Assert(seenFp == clientFp, "Edge injected the VERIFIED cert fingerprint upstream");
            Assert(seenFp != "SPOOFEDFINGERPRINT", "client-supplied X-LocalAI-Cert-Sha256 did NOT reach upstream (overwritten)");
            Assert(seenSpoof is null, "client-supplied X-LocalAI-* header was stripped");
        }

        // 2. no client cert -> business rejected
        using (var c = MkClient(null))
        {
            using var r = await c.GetAsync(baseUrl + "/v1/models");
            Assert((int)r.StatusCode == 401, "no client cert -> business 401 (" + (int)r.StatusCode + ")");
        }

        // 3. no client cert -> /pair/enroll reachable (anonymous)
        using (var c = MkClient(null))
        {
            using var k2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var csr2 = new CertificateRequest("CN=c2", k2, HashAlgorithmName.SHA256).CreateSigningRequest();
            var body = JsonSerializer.Serialize(new
            {
                csr = Convert.ToBase64String(csr2),
                clientNonce = Convert.ToBase64String(R(32)),
                claimSecretHash = Convert.ToBase64String(SHA256.HashData(R(32))),
                protocolVersion = 1,
                displayName = "second device",
            });
            using var r = await c.PostAsync(baseUrl + "/pair/enroll", new StringContent(body, Encoding.UTF8, "application/json"));
            Assert((int)r.StatusCode == 200, "no cert -> /pair/enroll reachable 200 (" + (int)r.StatusCode + ")");
            var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            Assert(doc.GetProperty("requestId").GetString()!.Length == 32 && doc.GetProperty("sas").GetArrayLength() == 6,
                   "/pair/enroll returns request id + six-word SAS");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  D? 甲 · 【过期】与【被吊销】谁先命中 —— 2026-08-05 勘察结论,钉成常驻断言
        // ══════════════════════════════════════════════════════════════════════
        //  勘察问的是「成员表 active 校验会不会先于证书过期把请求挡掉」。实测**正好相反**:
        //  证书有效期是在 **TLS 握手层**(ValidateClient -> X509Chain 判 NotTimeValid)判的,
        //  连接当场断,MapFallback 里那句 Store.IsActive **一次都跑不到**。
        //  ⇒ 过期先命中,而且它**连一个 HTTP 状态码都没有**;被吊销反而拿得到 401。
        //  两者可归因性差一个量级,这正是设备证书过期难查的结构性原因。
        {
            // 用同一个 CA 签一张【昨天就过期】的设备证书(窗口显式给定,见 Ca.IssueLeafWindow)
            var caCertFull = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(idDir, "ca.cer")));
            var locJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(secDir, "identity-locators.json"))).RootElement;
            var expKeyName = "localai-edge-expired-" + Convert.ToHexString(R(4)).ToLowerInvariant();
            using var expEc = new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, expKeyName,
                new CngKeyCreationParameters { Provider = swProv, ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing }));
            try
            {
                var expPub = PublicKey.CreateFromSubjectPublicKeyInfo(expEc.ExportSubjectPublicKeyInfo(), out _);
                using var expiredLeaf = Ca.IssueLeafWindow(locJson.GetProperty("ca_key_name").GetString()!, caCertFull, expPub,
                    "device-expired", null, "urn:localai:device:expired-test", false, true,
                    DateTimeOffset.UtcNow.AddDays(-100), DateTimeOffset.UtcNow.AddDays(-1));

                Assert(!Ca.VerifyChainAndEku(expiredLeaf, caPublic, Ca.OidClientAuth),
                       "★ 过期设备证书在**链校验**这一层就被判死(ValidateClient 用的就是它)");

                // 把它登记成 active 成员 —— 于是"成员表说它没问题",只有有效期是坏的。
                // ★ 这样才能证明:挡住它的**不是**成员表,而是 TLS 层的有效期。
                var expFp = Convert.ToHexString(SHA256.HashData(expiredLeaf.RawData));
                Store.Mutate(idDir, s =>
                {
                    s.AddProvisioning("expired-test", "EXPIREDPC", null);
                    s.AddCandidate("expired-test", expiredLeaf.SerialNumber, expFp, "spki",
                                   expiredLeaf.NotBefore.ToString("O"), expiredLeaf.NotAfter.ToString("O"));
                    s.Activate("expired-test", expFp);
                });
                Assert(Store.LoadOrEmpty(idDir).IsActive(expFp),
                       "★ 前提摆正:这张过期证书在成员表里是 **active** —— 所以下面挡住它的只可能是有效期");

                using var expiredWithKey = expiredLeaf.CopyWithPrivateKey(expEc);
                int? httpStatus = null; string exShape = "";
                try
                {
                    using var c = MkClient(expiredWithKey);
                    using var r = await c.GetAsync(baseUrl + "/v1/models");
                    httpStatus = (int)r.StatusCode;
                }
                catch (Exception ex)
                {
                    for (var e = ex; e is not null; e = e.InnerException) exShape += e.GetType().Name + "|";
                }
                // ★★ 承重:过期证书**根本没有 HTTP 状态码**。若哪天它变成 401,说明有效期校验
                //   被挪到了应用层 —— 那是个好消息(可归因性变好),但届时客户端的归因逻辑必须跟着改,
                //   所以这条断言要在那一刻**红给人看**,而不是默默通过。
                Assert(httpStatus is null,
                       $"★★ 【过期】设备证书:TLS 层就断了,**拿不到任何 HTTP 状态码**(异常链 {exShape})"
                       + " —— 这就是它比'被吊销'难归因一个量级的根源");
                Assert(!exShape.Contains("AuthenticationException"),
                       "★★ 异常链里**没有** AuthenticationException —— 客户端旧判据靠它兜底,故对这一格完全失灵");

                // ══════════════════════════════════════════════════════════════
                //  D? 甲2 · ★★★ 把上面那条和第 3 节【连起来】:过期之后用户还能不能自救?
                // ══════════════════════════════════════════════════════════════
                //  上面钉住了「过期证书死在 TLS 层」,第 3 节钉住了「不带证书能走匿名 /pair/enroll」。
                //  **两条各自都绿,而中间那条缝谁也没看** —— 那条缝就是"所以过期之后该怎么办",
                //  也正是客户端文案唯一的依据。这一段把它补上,三问三答:
                //
                //    ① 带着过期证书,连**续签**路由都够不着;
                //    ② 带着过期证书,连**匿名**的 /pair/enroll 也够不着
                //       ——「匿名」只在你**真的不出示证书**时成立,TLS 死在路由匹配之前;
                //    ③ 把证书摘掉,同一个中枢、同一秒,/pair/enroll 立刻 200 + 六个词。
                //
                //  ⇒ 承重结论:**设备证书过期之后,唯一的出路是重新配对,而且必须先摘掉那张证书。**
                //    ★ 由此 TlsFailure.Explain(LocalDeviceCertExpired) 里那句「本机会自动续签」
                //      在它**唯一会被显示的时刻**(判据就是 notAfter <= now)是假的,
                //      而「不要点重新配对」否掉的正是这里实测出来的唯一出路。见该文件的更正。
                //    ★ 这三条**同时**成立才有意义:只钉①会读成"续签坏了",只钉③会读成"随时能重配"。
                string EnrollJson(string who)
                {
                    using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    return JsonSerializer.Serialize(new
                    {
                        csr = Convert.ToBase64String(new CertificateRequest("CN=" + who, k, HashAlgorithmName.SHA256).CreateSigningRequest()),
                        clientNonce = Convert.ToBase64String(R(32)),
                        claimSecretHash = Convert.ToBase64String(SHA256.HashData(R(32))),
                        protocolVersion = 1,
                        displayName = who,
                    });
                }

                // ① 过期证书够不着续签路由 —— 续签本身要用这张证书握手
                int? renewStatus = null;
                try
                {
                    using var c = MkClient(expiredWithKey);
                    var rb = JsonSerializer.Serialize(new
                    {
                        csr = Convert.ToBase64String(new CertificateRequest("CN=client", expEc, HashAlgorithmName.SHA256).CreateSigningRequest()),
                    });
                    using var r = await c.PostAsync(baseUrl + "/identity/renew/enroll", new StringContent(rb, Encoding.UTF8, "application/json"));
                    renewStatus = (int)r.StatusCode;
                }
                catch { /* TLS 层就断 —— 正是要钉的形状 */ }
                Assert(renewStatus is null,
                       "★★★ 过期设备证书**够不着续签路由**(实得 " + (renewStatus?.ToString() ?? "连状态码都没有") + ")"
                       + " —— 续签要用这张证书握手,而它已经握不上了 ⇒ 「已过期了,本机会自动续签」是一句【结构上不可能兑现】的话");

                // ② 仍然出示过期证书时,连匿名入口也够不着
                int? pairWithExpired = null;
                try
                {
                    using var c = MkClient(expiredWithKey);
                    using var r = await c.PostAsync(baseUrl + "/pair/enroll",
                                                    new StringContent(EnrollJson("rescue-with-expired"), Encoding.UTF8, "application/json"));
                    pairWithExpired = (int)r.StatusCode;
                }
                catch { }
                Assert(pairWithExpired is null,
                       "★★★ **仍然出示**过期证书时,连匿名的 /pair/enroll 也够不着(实得 "
                       + (pairWithExpired?.ToString() ?? "连状态码都没有") + ")—— 「匿名」只在真的不出示证书时才成立");

                // ③ 摘掉证书 —— 同一个中枢、同一秒,自救路径立刻可达
                int? pairWithout = null; var sasLen = 0;
                try
                {
                    using var c = MkClient(null);
                    using var r = await c.PostAsync(baseUrl + "/pair/enroll",
                                                    new StringContent(EnrollJson("rescue-no-cert"), Encoding.UTF8, "application/json"));
                    pairWithout = (int)r.StatusCode;
                    if (r.IsSuccessStatusCode)
                        sasLen = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("sas").GetArrayLength();
                }
                catch { }
                Assert(pairWithout == 200 && sasLen == 6,
                       "★★★ 摘掉那张过期证书后,**同一个中枢、同一秒**,/pair/enroll 立刻 200 + 六个词(实得 "
                       + (pairWithout?.ToString() ?? "连状态码都没有") + " / " + sasLen + " 词)"
                       + " ⇒ 【过期之后唯一的自救路径 = 重新配对】—— 客户端文案不许否掉它");

                Store.Mutate(idDir, s => s.RevokeDevice("expired-test"));   // 收拾干净,别影响后面的断言
            }
            finally { Ca.DeleteKey(expKeyName); }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  D? 乙 · 设备证书续签两条路由(端到端,真 HTTP + 真 mTLS)
        // ══════════════════════════════════════════════════════════════════════
        {
            // ① enroll:用【当前 active 的旧证书】握手,交一份新 CSR(复用同一把私钥)
            var newCsr = new CertificateRequest("CN=client", clientEcdsa, HashAlgorithmName.SHA256).CreateSigningRequest();
            string renewalId, candB64;
            using (var c = MkClient(clientCert))
            {
                var body = JsonSerializer.Serialize(new { csr = Convert.ToBase64String(newCsr) });
                using var r = await c.PostAsync(baseUrl + "/identity/renew/enroll", new StringContent(body, Encoding.UTF8, "application/json"));
                Assert((int)r.StatusCode == 200, "续签 enroll(旧证书 mTLS)-> 200 (" + (int)r.StatusCode + ")");
                var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
                // ★★ D92 成对断言(服务端半边):顶层键集合**正好**等于登记表那一组。
                //   用集合相等而不是"包含" —— 「包含」放过"多发一个键"和"改了名还留着旧的",
                //   而那两种正是字段搬家的实际形状(A1 就是搬了家)。
                var enrollKeys = d.EnumerateObject().Select(x => x.Name).ToArray();
                Assert(WireContracts.KeysMatch(enrollKeys, WireContracts.RenewEnroll),
                       "★★ 成对断言/服务端 CONTRACT:cert.renew.enroll:顶层键 == 登记表("
                       + WireContracts.Describe(enrollKeys, WireContracts.RenewEnroll) + ")");
                renewalId = d.GetProperty("renewalId").GetString()!;
                candB64 = d.GetProperty("candidateCert").GetString()!;
            }
            using var newCert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(candB64));
            Assert(Ca.HasUriSan(newCert, "urn:localai:device:" + deviceId),
                   "★★ 续签出来的证书仍然钉着**同一个 device_id** —— 设备列表不会再长出一条");

            // ★★ 确认之前:旧证书仍然能用。这条顺序是"续签不会把自己续死"的全部保障。
            using (var c = MkClient(clientCert))
            {
                using var r = await c.GetAsync(baseUrl + "/v1/models");
                Assert((int)r.StatusCode == 200, "★★ 签出候选之后、确认之前:**旧证书照常可用**(" + (int)r.StatusCode + ")");
            }

            // ② complete:用【候选证书】握手 —— 这就是"新证书真的能用"的证据
            using var newWithKey = newCert.CopyWithPrivateKey(clientEcdsa);
            using (var c = MkClient(newWithKey))
            {
                using var r = await c.PostAsync(baseUrl + "/identity/renew/complete?renewalId=" + renewalId, new StringContent(""));
                Assert((int)r.StatusCode == 200, "续签 complete(候选证书 mTLS)-> 200 (" + (int)r.StatusCode + ")");
                var cKeys = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement
                            .EnumerateObject().Select(x => x.Name).ToArray();
                Assert(WireContracts.KeysMatch(cKeys, WireContracts.RenewComplete),
                       "★★ 成对断言/服务端 CONTRACT:cert.renew.complete:顶层键 == 登记表("
                       + WireContracts.Describe(cKeys, WireContracts.RenewComplete) + ")");
                // 幂等:成功响应丢了、客户端重试
                using var r2 = await c.PostAsync(baseUrl + "/identity/renew/complete?renewalId=" + renewalId, new StringContent(""));
                Assert((int)r2.StatusCode == 200 &&
                       JsonDocument.Parse(await r2.Content.ReadAsStringAsync()).RootElement.GetProperty("changed").GetBoolean() == false,
                       "★ complete 幂等重试仍 200,且如实报 changed=false");
            }

            // 切换之后:新证书可用、旧证书立刻失效
            using (var c = MkClient(newWithKey))
            {
                using var r = await c.GetAsync(baseUrl + "/v1/models");
                Assert((int)r.StatusCode == 200, "切换后:**新证书**可以做业务调用 (" + (int)r.StatusCode + ")");
            }
            using (var c = MkClient(clientCert))
            {
                int code;
                try { using var r = await c.GetAsync(baseUrl + "/v1/models"); code = (int)r.StatusCode; } catch { code = -1; }
                Assert(code != 200, "★★ 切换后:**旧证书立刻失效**(" + code + ")—— D49「漏删旧的会继续用」的设备侧等价物");
            }

            // ★★ fail-closed:拿一张**已经不是 active** 的证书去发起续签,必须被拒
            using (var c = MkClient(clientCert))
            {
                var body = JsonSerializer.Serialize(new { csr = Convert.ToBase64String(newCsr) });
                using var r = await c.PostAsync(baseUrl + "/identity/renew/enroll", new StringContent(body, Encoding.UTF8, "application/json"));
                Assert((int)r.StatusCode == 401,
                       "★★ 用已 superseded 的旧证书发起续签 -> 401(" + (int)r.StatusCode + ")"
                       + " —— 否则一张退休的证书能自己换新的,续签就成了绕过吊销的后门");
            }

            // 后面第 4 节要吊销 deviceId 并验证它连不上。★ 必须拿**现役**那张证书去试:
            //   旧证书此刻已 superseded,拿它去测"吊销生效了没有"会恒过 —— 那是一条假断言,
            //   它证明的是"退休证书不能用",而不是"吊销起作用了"。
            currentCert = newCert.CopyWithPrivateKey(clientEcdsa);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  D? 丙 · ★★ 跨语言【成对断言】的服务端半边(D92 硬前置)
        // ══════════════════════════════════════════════════════════════════════
        //  客户端半边在 20-client-win/app/Selftest.cs(拿这个形状喂 HubAdmin.ParseServerCert)。
        //  两侧**读同一份** WireContracts —— 期望值只有一份,它没法跟自己分家。
        //  ★ A1 的教训:两边各写各的、各自都绿,而中间那条缝谁也没看。
        var coveredContracts = new List<string> { "POST /identity/renew/enroll", "POST /identity/renew/complete" };
        {
            using var adminHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{AdminPort}") };
            using var pr = await adminHttp.GetAsync("/admin/ping");
            Assert((int)pr.StatusCode == 200, "回环管理面可达 /admin/ping(" + (int)pr.StatusCode + ")");
            var pj = JsonDocument.Parse(await pr.Content.ReadAsStringAsync()).RootElement;

            var topKeys = pj.EnumerateObject().Select(x => x.Name).ToArray();
            Assert(WireContracts.KeysMatch(topKeys, WireContracts.AdminPing),
                   "★★ 成对断言/服务端 CONTRACT:cert.admin.ping:/admin/ping 顶层键 == 登记表("
                   + WireContracts.Describe(topKeys, WireContracts.AdminPing) + ")");
            coveredContracts.Add("GET /admin/ping");

            // ★★★ serverCert 子对象。**这一格此前没有任何读取方** —— 而 lan-edge 那行注释
            //   写着「主机界面据此报警」。吐出来却没人读 = 轮换器 fail-closed 的最后一段路是断的。
            //   客户端侧的读取方(HubAdmin.ParseServerCert)与这条断言是同一次改动里加上的。
            // ★ 这里必须真的有一个轮换器:selftest 走的是生产路径(没传 serverCertOverride),
            //   所以 Rotator 装着,读的是这份**临时**身份的 server.cer —— 副作用不落到实机。
            Assert(pj.TryGetProperty("serverCert", out var sc) && sc.ValueKind == JsonValueKind.Object,
                   "★★ /admin/ping 真的吐出了 serverCert 子对象(轮换器装上了 —— 零命中不算通过)");
            if (sc.ValueKind == JsonValueKind.Object)
            {
                var scKeys = sc.EnumerateObject().Select(x => x.Name).ToArray();
                Assert(WireContracts.KeysMatch(scKeys, WireContracts.AdminPingServerCert),
                       "★★ 成对断言/服务端:/admin/ping .serverCert 顶层键 == 登记表("
                       + WireContracts.Describe(scKeys, WireContracts.AdminPingServerCert) + ")");
                coveredContracts.Add("GET /admin/ping .serverCert");
                // 反向:健康的新身份**不该**报 needsAttention —— 否则这条告警恒真,等于噪音
                Assert(sc.GetProperty("needsAttention").GetBoolean() == false,
                       "★ 反向:刚 init 的身份不报 needsAttention(恒真的告警两周内就会被学会忽略)");
                Assert(sc.GetProperty("daysLeft").GetDouble() > 0,
                       "★ 反向:刚 init 的身份 daysLeft > 0(判据不是恒红的)");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  D? 丁 · V4:欠债表里那 13 条的**服务端半边**(真 HTTP,不是在进程里调对象)
        // ══════════════════════════════════════════════════════════════════════
        //  ★ 为什么必须走真 HTTP:欠债表登记的是**跨进程响应契约**。
        //    在进程里调 `pairing.Enroll(...)` 拿到的是 C# 对象,它证明不了
        //    「序列化出去之后那个 JSON 长什么样」—— 而缝恰恰在序列化那一层。
        //  ★★ 顺带:配对四条路由的 HTTP 全流程**在此之前从没被端到端跑过**。
        //    2026-08-04 实测过一次「图形界面这条配对路径从来没走完过」
        //    (ClientTransport.Pair 里那段注释),而当时没有任何断言拦得住它。
        {
            using var admin2 = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{AdminPort}") };
            async Task<(int st, JsonElement j)> Adm(HttpMethod m, string p, object? b = null)
            {
                using var q = new HttpRequestMessage(m, p);
                if (b is not null) q.Content = new StringContent(JsonSerializer.Serialize(b), Encoding.UTF8, "application/json");
                using var r = await admin2.SendAsync(q);
                var t = await r.Content.ReadAsStringAsync();
                return ((int)r.StatusCode, t.Length > 0 && t[0] is '{' or '[' ? JsonDocument.Parse(t).RootElement : default);
            }
            // 一条契约钉一次:顶层键集合 == 登记表,并把契约号写进消息(欠债表按契约号找这一半)
            void PinKeys(string cid, JsonElement obj, string[] want)
            {
                var got = obj.EnumerateObject().Select(x => x.Name).ToArray();
                Assert(WireContracts.KeysMatch(got, want),
                       $"★★ 成对断言/服务端 {cid}:顶层键 == 登记表(" + WireContracts.Describe(got, want) + ")");
                coveredContracts.Add(WireContracts.All.First(c => c.Cid == cid).Name);
            }

            // ── ① 配对窗口:开窗要回**当前**状态,不是只回 ok ──────────────
            var (ws, wj) = await Adm(HttpMethod.Post, "/admin/pairing/window", new { open = true, minutes = 5 });
            Assert(ws == 200, "/admin/pairing/window -> 200 (" + ws + ")");
            PinKeys("CONTRACT:cert.admin.window", wj, WireContracts.AdminWindow);
            Assert(wj.GetProperty("pairingWindowOpen").GetBoolean(),
                   "★ 反向:开窗之后它如实回 true(只回 ok 的话,界面只能拿本地布尔替中枢记 —— Selftest.cs:5923 明令禁止)");

            // ── ② /pair/enroll:六个词与 CA 都在这一条里 ───────────────────
            string v4KeyName = "localai-edge-v4pair-" + Convert.ToHexString(R(4)).ToLowerInvariant();
            using var v4Ec = new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, v4KeyName,
                new CngKeyCreationParameters { Provider = swProv, ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing }));
            try
            {
                var v4Csr = new CertificateRequest("CN=client", v4Ec, HashAlgorithmName.SHA256).CreateSigningRequest();
                var v4Secret = R(32);
                string EnrollBody(string who) => JsonSerializer.Serialize(new
                {
                    csr = Convert.ToBase64String(v4Csr),
                    clientNonce = Convert.ToBase64String(R(32)),
                    claimSecretHash = Convert.ToBase64String(SHA256.HashData(v4Secret)),
                    protocolVersion = 1,
                    displayName = who,
                });

                JsonElement en2;
                using (var c = MkClient(null))
                using (var r = await c.PostAsync(baseUrl + "/pair/enroll", new StringContent(EnrollBody("v4-pair"), Encoding.UTF8, "application/json")))
                {
                    Assert((int)r.StatusCode == 200, "/pair/enroll -> 200 (" + (int)r.StatusCode + ")");
                    en2 = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
                }
                PinKeys("CONTRACT:cert.pair.enroll", en2, WireContracts.PairEnroll);
                var reqId2 = en2.GetProperty("requestId").GetString()!;

                // ── ③ ★★★ /pair/status 的【失败分支】—— 欠债表点名的那一条 ──────
                //  批准**之前**:三个键都在,但后两个的**值是 null**。
                //  客户端此刻绝不能跳出轮询 —— 跳出去就会对 null 调 GetString()!,
                //  要么 NRE 要么拿 null 去算 challenge,两种都表现为「配对走不完」,
                //  而主机侧那条设备记录**永远停在 provisioning**。
                async Task<JsonElement> Status()
                {
                    using var c = MkClient(null);
                    using var r = await c.PostAsync(baseUrl + "/pair/status",
                        new StringContent(JsonSerializer.Serialize(new { requestId = reqId2, claimSecret = Convert.ToBase64String(v4Secret) }),
                                          Encoding.UTF8, "application/json"));
                    Assert((int)r.StatusCode == 200, "/pair/status -> 200 (" + (int)r.StatusCode + ")");
                    return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
                }
                var stPending = await Status();
                PinKeys("CONTRACT:cert.pair.status", stPending, WireContracts.PairStatus);
                Assert(stPending.GetProperty("status").GetString() == "pending",
                       "★ 批准之前 status == pending");
                Assert(!WireContracts.PairStatusProceed.Contains(stPending.GetProperty("status").GetString()),
                       "★★★ 失败分支:pending **不在**客户端的跳出集合里 —— 跳出去就会对 null 调 GetString()!");
                Assert(stPending.GetProperty("claimNonce").ValueKind == JsonValueKind.Null
                       && stPending.GetProperty("candidateSha256").ValueKind == JsonValueKind.Null,
                       "★★★ 失败分支:pending 时那两个字段的**值是 null**(键在、值空)—— "
                       + "这正是「只钉顶层键集合」挡不住的那一层,所以两侧都要覆盖它");

                // ── ④ 批准 ⇒ 状态推进,两个字段这才有值 ────────────────────
                var (asx, aj) = await Adm(HttpMethod.Post, "/admin/pairing/approve", new { requestId = reqId2 });
                Assert(asx == 200, "/admin/pairing/approve -> 200 (" + asx + ")");
                PinKeys("CONTRACT:cert.admin.approve", aj, WireContracts.AdminApprove);

                var stOk = await Status();
                Assert(WireContracts.PairStatusProceed.Contains(stOk.GetProperty("status").GetString()),
                       "★★ 批准之后 status 落在客户端的**跳出集合**里(" + stOk.GetProperty("status").GetString() + ")");
                Assert(stOk.GetProperty("claimNonce").ValueKind == JsonValueKind.String
                       && stOk.GetProperty("candidateSha256").ValueKind == JsonValueKind.String,
                       "★★ 跳出的那一刻,claimNonce 与 candidateSha256 **必须**都已经是字符串");

                // ── ⑤ /pair/claim ⇒ 候选证书 ─────────────────────────────
                var claimNonce2 = Convert.FromBase64String(stOk.GetProperty("claimNonce").GetString()!);
                var candSha2 = stOk.GetProperty("candidateSha256").GetString()!;
                var chal = Pairing.BuildChallenge(Convert.FromHexString(reqId2), claimNonce2, Convert.FromHexString(candSha2));
                JsonElement cl2;
                using (var c = MkClient(null))
                using (var r = await c.PostAsync(baseUrl + "/pair/claim",
                    new StringContent(JsonSerializer.Serialize(new { requestId = reqId2, claimSecret = Convert.ToBase64String(v4Secret),
                                                                     challengeSig = Convert.ToBase64String(v4Ec.SignData(chal, HashAlgorithmName.SHA256)) }),
                                      Encoding.UTF8, "application/json")))
                {
                    Assert((int)r.StatusCode == 200, "/pair/claim -> 200 (" + (int)r.StatusCode + ")");
                    cl2 = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
                }
                PinKeys("CONTRACT:cert.pair.claim", cl2, WireContracts.PairClaim);

                // ── ⑥ /pair/complete ⇒ **文本**契约,不是 JSON ───────────────
                using var cand2 = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(cl2.GetProperty("candidateCert").GetString()!));
                using var cand2Key = cand2.CopyWithPrivateKey(v4Ec);
                using (var c = MkClient(cand2Key))
                using (var r = await c.PostAsync(baseUrl + "/pair/complete?requestId=" + reqId2, new StringContent("")))
                {
                    var body = (await r.Content.ReadAsStringAsync()).Trim();
                    Assert((int)r.StatusCode == 200, "/pair/complete -> 200 (" + (int)r.StatusCode + ")");
                    Assert(body == WireContracts.PairCompleteBody,
                           $"★★ 成对断言/服务端 CONTRACT:cert.pair.complete:应答是**文本** `{WireContracts.PairCompleteBody}`(实得 `{body}`)"
                           + " —— 如实按文本契约钉;给它编一个空键集合会让判据恒真");
                    coveredContracts.Add(WireContracts.All.First(c2 => c2.Cid == "CONTRACT:cert.pair.complete").Name);
                }

                // ── ⑦ 待批列表 + 拒绝 + 409(失败分支) ──────────────────────
                //  再发一条请求,拿它来验 pending 列表、deny、以及"对已终结的请求再批准" ⇒ 409。
                string reqId3;
                using (var c = MkClient(null))
                using (var r = await c.PostAsync(baseUrl + "/pair/enroll", new StringContent(EnrollBody("v4-deny"), Encoding.UTF8, "application/json")))
                    reqId3 = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("requestId").GetString()!;

                var (ls2, lj2) = await Adm(HttpMethod.Get, "/admin/pairing/pending");
                Assert(ls2 == 200, "/admin/pairing/pending -> 200 (" + ls2 + ")");
                PinKeys("CONTRACT:cert.admin.pending", lj2, WireContracts.AdminPending);
                var pendArr = lj2.GetProperty("pending").EnumerateArray().ToList();
                Assert(pendArr.Count > 0, "★ 反向:待批列表真的有一条(零条时下面那条元素断言会静默跳过 = 假绿)");
                if (pendArr.Count > 0)
                    PinKeys("CONTRACT:cert.admin.pending.item", pendArr[0], WireContracts.AdminPendingItem);

                var (ds2, dj2) = await Adm(HttpMethod.Post, "/admin/pairing/deny", new { requestId = reqId3 });
                Assert(ds2 == 200, "/admin/pairing/deny -> 200 (" + ds2 + ")");
                PinKeys("CONTRACT:cert.admin.deny", dj2, WireContracts.AdminDeny);

                // ★★ 失败分支:对一条**已经被拒**的请求再批准 ⇒ 409 且必须说清为什么。
                //   只回一个光秃秃的 409,界面就只能说"中枢拒绝了",人会以为中枢坏了。
                var (a409, j409) = await Adm(HttpMethod.Post, "/admin/pairing/approve", new { requestId = reqId3 });
                Assert(a409 == 409, "★ 对已终结的请求再批准 -> 409 (" + a409 + ")");
                PinKeys("CONTRACT:cert.admin.approvedeny.409", j409, WireContracts.AdminApproveDeny409);
                Assert(j409.GetProperty("ok").GetBoolean() == false && j409.GetProperty("error").GetString()!.Length > 0,
                       "★★ 409 里 ok=false 且 error 非空 —— 失败分支也要说得出原因");
            }
            finally { Ca.DeleteKey(v4KeyName); }

            // ── ⑧ /admin/devices(顶层 + 元素)与 /admin/devices/revoke ──────
            var (dvs, dvj) = await Adm(HttpMethod.Get, "/admin/devices");
            Assert(dvs == 200, "/admin/devices -> 200 (" + dvs + ")");
            PinKeys("CONTRACT:cert.admin.devices", dvj, WireContracts.AdminDevices);
            var devArr = dvj.GetProperty("devices").EnumerateArray().ToList();
            Assert(devArr.Count > 0, "★ 反向:设备表真的有条目(零条 ⇒ 下面那条元素断言静默跳过 = 假绿)");
            if (devArr.Count > 0)
                PinKeys("CONTRACT:cert.admin.devices.item", devArr[0], WireContracts.AdminDevicesItem);

            // 拿刚配好的那台去吊销 —— 不动 deviceId(第 4 节还要用它验"吊销即时生效")
            var victim = devArr.Select(d => d.GetProperty("deviceId").GetString()!)
                               .FirstOrDefault(id => id != deviceId);
            Assert(victim is not null, "★ 前提:表里有第二台设备可供吊销(否则下面那条会去动第 4 节要用的那台)");
            var (rvs, rvj) = await Adm(HttpMethod.Post, "/admin/devices/revoke", new { deviceId = victim ?? "none" });
            Assert(rvs == 200, "/admin/devices/revoke -> 200 (" + rvs + ")");
            PinKeys("CONTRACT:cert.admin.revoke", rvj, WireContracts.AdminRevoke);
            Assert(rvj.GetProperty("generation").GetInt32() > 0,
                   "★ generation 是吊销真的落盘的凭据(只回 ok 的话,客户端没法判断这次写成没成)");
        }

        // ★★ 元断言:登记表里的**每一条**都要在上面被核对过。
        //   新加一条路由却忘了写成对断言 ⇒ 这里当场红,而不是静默少测一条。
        //   (ASSERTION-PITFALLS 第 3b 条:判词说"每一个"时,遍历源必须是表本身,不是手写名单。
        //    这里手写的是**已覆盖清单**,遍历源是 WireContracts.All —— 新增一项会红。)
        var missing = WireContracts.All.Select(c => c.Name).Except(coveredContracts).ToArray();
        Assert(missing.Length == 0,
               "★★★ 元断言:WireContracts 登记的每一条契约都要有服务端成对断言 —— 缺:["
               + string.Join(", ", missing) + "]");
        Assert(coveredContracts.Count == WireContracts.All.Length,
               $"★ 元断言的两个方向:核对过 {coveredContracts.Count} 条 / 登记 {WireContracts.All.Length} 条"
               + "(核对数多于登记数 = 有人重复核对或表漏登记)");

        // 4. revoke the paired device -> its cert can no longer reach business
        var store = Store.LoadOrEmpty(idDir);
        store.RevokeDevice(deviceId);
        store.Save(idDir);
        using (var c = MkClient(currentCert ?? clientCert))
        {
            HttpStatusCode code;
            try { using var r = await c.GetAsync(baseUrl + "/v1/models"); code = r.StatusCode; }
            catch { code = HttpStatusCode.Unused; }   // TLS-layer rejection is also acceptable
            Assert(code != HttpStatusCode.OK, "revoked device -> business no longer reachable (" + code + ")");
        }
    }
    finally
    {
        if (edge is not null) await edge.StopAsync();
        if (upstream is not null) await upstream.StopAsync();
        if (caKey is not null) Ca.DeleteKey(caKey);
        if (srvKey is not null) Ca.DeleteKey(srvKey);
        foreach (var name in new[] { clientKeyName, swSrvKeyName })
            if (name is not null) { try { if (CngKey.Exists(name, swProv)) CngKey.Open(name, swProv).Delete(); } catch { } }
        // remove the server cert the Edge added to CurrentUser\My during the test (no residue)
        try
        {
            var locFile = Path.Combine(secDir, "identity-locators.json");
            if (File.Exists(locFile))
            {
                var thumb = JsonDocument.Parse(File.ReadAllText(locFile)).RootElement.GetProperty("server_thumbprint").GetString();
                using var st = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                st.Open(OpenFlags.ReadWrite);
                foreach (var c in st.Certificates.Find(X509FindType.FindByThumbprint, thumb, false)) st.Remove(c);
            }
        }
        catch { }
        try { Directory.Delete(root, true); } catch { }
    }
    Console.WriteLine($"\nS4 LAN Edge selftest: PASS={pass} FAIL={fail}");
    return fail > 0 ? 1 : 0;
}

record EdgeConfig(string IdentityDir, string SecretsDir, string UpstreamBase, int ListenPort, IPAddress? Bind = null,
                  Action<EnrollNotice>? OnEnroll = null, int AdminPort = 0,
                  // 启动时是否自动打开配对窗口。默认 true 保持既有(手工双击运行)行为;
                  // run-lan 传 false —— 开机自启的常驻 Edge 绝不该自动敞开准入窗口(审查发现 [3])。
                  bool OpenPairingWindowOnStart = true);
record EnrollNotice(string RequestId, string DisplayName, string[] Sas);

static class Edge
{
    /// <summary>
    /// 当前对外出示的服务器证书。★ 自动轮换续签之后**换这一个引用**,新握手立刻用上新证书,
    /// 已建立的连接不受影响(见 ServerCertificateSelector 那段)。volatile:采样线程与请求线程都碰它。
    /// </summary>
    public static volatile X509Certificate2? CurrentServerCert;

    /// <summary>
    /// 服务器证书自动轮换器(fail-closed)。★ 它**没有自己的持久状态** —— 每一跳重读 server.cer
    /// 的到期时间来决定要不要动手,所以崩在任何一步之后重入都不会留半套状态。
    /// 详见 identity/ServerCertRotator.cs 顶部。
    /// </summary>
    public static ServerCertRotator? Rotator;

    /// <summary>轮换器最近一次的可观测状态(/admin/ping 吐出去,主机界面据此报警)。</summary>
    public static RotationStatus? LastRotation;

    public static WebApplication Build(EdgeConfig cfg, X509Certificate2? serverCertOverride = null, Pairing? pairingOverride = null)
    {
        var idDir = cfg.IdentityDir;
        var caPublic = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(idDir, "ca.cer")));
        // NOTE: the production path loads the server leaf key from the TPM (LoadServerCert). SChannel
        // cannot currently use a TPM key as a TLS credential ("unexpected EOF") -- see backlog B17.
        // The self-test passes an SChannel-compatible (non-exportable software CNG) server cert so the
        // Edge's mTLS + membership + proxy + pairing LOGIC is fully exercised regardless of that gap.
        var serverCert = serverCertOverride ?? LoadServerCert(idDir, cfg.SecretsDir);
        CurrentServerCert = serverCert;

        // ★ 轮换器只在【生产路径】上装(没有 serverCertOverride 时)。自检传的是一张自己造的证书,
        //   给它装轮换器等于让自检去续签**实机**的身份 —— 那是把测试的副作用打到生产上。
        if (serverCertOverride is null)
            Rotator = new ServerCertRotator(
                readNotAfter: () => Identity.ServerCertExpiry(idDir),
                renew: () =>
                {
                    Identity.RenewServerCert(idDir, cfg.SecretsDir);
                    // ★ 续完立刻热换:重新物化一张带私钥的证书顶上去。不换的话续签等于没做 ——
                    //   磁盘上是新的,对外出示的还是旧的那张,直到有人重启 Edge。
                    CurrentServerCert = LoadServerCert(idDir, cfg.SecretsDir);
                });

        var pairing = pairingOverride ?? new Pairing(idDir, cfg.SecretsDir);
        if (pairingOverride is null && cfg.OpenPairingWindowOnStart) pairing.OpenWindow(TimeSpan.FromMinutes(30));
        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });

        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);   // ← temporarily surface handshake errors
        builder.WebHost.ConfigureKestrel(k =>
        {
            // ★★ 请求体上限【按面分流】(2026-07-31 审计),不再服务器级压到 32 KiB:
            //   32 KiB 是决策包 §5.2 给【匿名 /pair 面】的抗-DoS 上限,但它被写成了服务器级默认,
            //   于是把【业务面】的聊天转发也一起卡在 32 KiB —— 稍长一点的历史就被 Kestrel 空 413 挡掉。
            //   这里不设服务器级 body 上限(交给下面的中间件按路径给);header/请求行两条保持不变。
            k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            k.Limits.MaxRequestLineSize = 4 * 1024;
            k.Listen(cfg.Bind ?? IPAddress.Loopback, cfg.ListenPort, lo => lo.UseHttps(h =>
            {
                // ★★ 用 Selector 而不是把证书钉死在 ServerCertificate 上 —— 这是「续签中不中断已有连接」
                //   的**结构性**做法:Selector 在**每次新握手**时被调用,于是自动轮换换掉 CurrentServerCert
                //   之后,新连接立刻拿到新证书,而**已建立的 TLS 连接一条都不受影响**(它们的会话密钥
                //   早就协商好了,与证书对象无关)。
                //   钉死 ServerCertificate 的话,换证书的唯一办法是重启 Edge —— 那会掐断正在进行的
                //   聊天流(300 秒的流式连接),而 D49 当时给出的正是「请重启它以加载新证书」。
                h.ServerCertificateSelector = (_, _) => Edge.CurrentServerCert ?? serverCert;
                h.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                h.ClientCertificateValidation = (cert, _, __) => ValidateClient(cert, caPublic);
            }));

            // ★ P3c 管理面:**只绑回环**,明文 HTTP,不上局域网。
            // 依据 D37:「成员批准/吊销 · CA 私钥 · 备份 = 仅主机本地(IPC / 只绑回环)」,
            // 以及设计稿「主机管理:仅主机端 + 家庭安全管理员(副机端即使管理员也不显示)」。
            // 这样"批准新设备"在**结构上**只能由坐在主机前的人完成 —— 副机连不到这个端口,
            // 而不是靠一个可以被绕过的权限判断。同时保住了 P3b 的"物理在场"性质。
            if (cfg.AdminPort > 0)
                k.Listen(IPAddress.Loopback, cfg.AdminPort);
        });

        var app = builder.Build();

        // ★ 按面给请求体上限(2026-07-31 审计):匿名 /pair/* 仍是 32 KiB(抗-DoS,一点没削弱),
        //   业务面过了 mTLS + active 成员校验才够得到 8 MiB(聊天历史 / 将来多模态)。上限做成具名常量,不散魔数。
        const long PairBodyCap = 32 * 1024;           // 决策包 §5.2:匿名入口
        const long BusinessBodyCap = 8L * 1024 * 1024; // 业务面
        app.Use(async (ctx, next) =>
        {
            var f = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (f is { IsReadOnly: false })
                f.MaxRequestBodySize = ctx.Request.Path.StartsWithSegments("/pair") ? PairBodyCap : BusinessBodyCap;
            await next();
        });

        // anonymous pairing routes (no client cert). Only these are reachable without a member cert.
        app.MapPost("/pair/enroll", async (HttpContext ctx) =>
        {
            var d = await JsonDocument.ParseAsync(ctx.Request.Body);
            var r = d.RootElement;
            EnrollResult en;
            try
            {
                en = pairing.Enroll(
                    Convert.FromBase64String(r.GetProperty("csr").GetString()!),
                    Convert.FromBase64String(r.GetProperty("clientNonce").GetString()!),
                    Convert.FromBase64String(r.GetProperty("claimSecretHash").GetString()!),
                    r.GetProperty("protocolVersion").GetInt32(),
                    r.GetProperty("displayName").GetString() ?? "");
            }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
            // notify the host console (real bring-up) so the operator can compare the six words and approve
            cfg.OnEnroll?.Invoke(new EnrollNotice(en.RequestId, r.GetProperty("displayName").GetString() ?? "", en.Sas));
            return Results.Json(new
            {
                requestId = en.RequestId,
                serverNonce = Convert.ToBase64String(en.ServerNonce),
                sas = en.Sas,
                caCert = Convert.ToBase64String(caPublic.RawData),
                hubId = pairing.HubId,
                // ★ B16:报出主机用的 SAS 词表版本。客户端拿它区分【版本不同】与【真的可疑】——
                //   索引与词表无关,所以版本不一致时六个词必然对不上,而那**不是**中间人攻击。
                //   不报的话,换表当天每一次配对都会被客户端指控为攻击(见 Transport.Pair)。
                sasWordlistVersion = Wordlist.Version,
            });
        });
        app.MapPost("/pair/status", async (HttpContext ctx) =>
        {
            var r = (await JsonDocument.ParseAsync(ctx.Request.Body)).RootElement;
            var s = pairing.Status(r.GetProperty("requestId").GetString()!, Convert.FromBase64String(r.GetProperty("claimSecret").GetString()!));
            return Results.Json(new
            {
                status = s.Status,
                claimNonce = s.ClaimNonce is null ? null : Convert.ToBase64String(s.ClaimNonce),
                candidateSha256 = s.CandidateSha256,
            });
        });
        app.MapPost("/pair/claim", async (HttpContext ctx) =>
        {
            var r = (await JsonDocument.ParseAsync(ctx.Request.Body)).RootElement;
            using var cand = pairing.Claim(r.GetProperty("requestId").GetString()!,
                Convert.FromBase64String(r.GetProperty("claimSecret").GetString()!),
                Convert.FromBase64String(r.GetProperty("challengeSig").GetString()!));
            return Results.Json(new { candidateCert = Convert.ToBase64String(cand.RawData) });
        });
        // complete requires the candidate client cert via mTLS -- its fingerprint MUST be the candidate
        // for this request (the final proof-of-possession). A candidate is not yet an active member, so
        // this only works because TLS validation is chain+EKU (membership is enforced per-route).
        app.MapPost("/pair/complete", (HttpContext ctx) =>
        {
            var cert = ctx.Connection.ClientCertificate;
            if (cert is null) return Results.StatusCode(401);
            var fp = Convert.ToHexString(SHA256.HashData(cert.RawData));
            pairing.Complete(ctx.Request.Query["requestId"].ToString(), fp);   // verifies fp == candidate
            return Results.Text("active");
        });

        // ---------------------------------------------------------------- P3c 管理面(仅回环)
        // 双保险:除了 Kestrel 只在回环上监听这个端口之外,每条路由再自行确认请求确实来自
        // 管理端口 + 回环地址。任何一层失效都不至于把批准/吊销暴露到局域网。
        bool IsAdmin(HttpContext c) =>
            cfg.AdminPort > 0 &&
            c.Connection.LocalPort == cfg.AdminPort &&
            (c.Connection.RemoteIpAddress?.Equals(IPAddress.Loopback) == true ||
             c.Connection.RemoteIpAddress?.Equals(IPAddress.IPv6Loopback) == true);

        app.MapGet("/admin/ping", (HttpContext c) =>
        {
            if (!IsAdmin(c)) return Results.NotFound();
            // ★ 轮换状态**必须**从这里吐出去。fail-closed 的意思不是"失败就停",是"失败必须被看见" ——
            //   一个静默失败的轮换器会一路滑到证书过期,而那时的症状是实测过的那个:
            //   客户端显示「中枢没开机」,用户跑去重启一个没病的中枢。
            var rot = Rotator?.Status(DateTimeOffset.UtcNow) ?? LastRotation;
            return Results.Json(new
            {
                ok = true,
                hubId = pairing.HubId,
                pairingWindowOpen = pairing.WindowOpen,
                serverCert = rot is null ? null : new
                {
                    notAfter = rot.NotAfter.ToString("O"),
                    daysLeft = Math.Round(rot.DaysLeft, 1),
                    phase = rot.Phase.ToString(),
                    consecutiveFailures = rot.ConsecutiveFailures,
                    lastError = rot.LastError,
                    needsAttention = rot.NeedsAttention,
                },
            });
        });

        // 待批准的配对请求 + 六个词 + 剩余秒数(界面据此显示倒计时并到点让它消失)
        app.MapGet("/admin/pairing/pending", (HttpContext c) =>
        {
            if (!IsAdmin(c)) return Results.NotFound();
            var items = pairing.ListPendingDetailed()
                .Where(p => p.Status == "pending")
                .Select(p => new { requestId = p.RequestId, displayName = p.DisplayName, sas = p.Sas, secondsLeft = p.SecondsLeft });
            return Results.Json(new { pairingWindowOpen = pairing.WindowOpen, pending = items });
        });

        app.MapPost("/admin/pairing/approve", async (HttpContext c) =>
        {
            if (!IsAdmin(c)) return Results.NotFound();
            var r = (await JsonDocument.ParseAsync(c.Request.Body)).RootElement;
            try { pairing.Approve(r.GetProperty("requestId").GetString()!); return Results.Json(new { ok = true }); }
            catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 409); }
        });

        app.MapPost("/admin/pairing/deny", async (HttpContext c) =>
        {
            if (!IsAdmin(c)) return Results.NotFound();
            var r = (await JsonDocument.ParseAsync(c.Request.Body)).RootElement;
            try { pairing.Deny(r.GetProperty("requestId").GetString()!); return Results.Json(new { ok = true }); }
            catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 409); }
        });

        // 配对窗口:显式开关。★ 不再随启动自动打开(审查发现 [3]) ——
        // 开机自启 + 无条件开窗 = 每次开机在局域网上自动敞开一个无人值守的 30 分钟准入窗口。
        app.MapPost("/admin/pairing/window", async (HttpContext c) =>
        {
            if (!IsAdmin(c)) return Results.NotFound();
            var r = (await JsonDocument.ParseAsync(c.Request.Body)).RootElement;
            var open = r.TryGetProperty("open", out var o) && o.GetBoolean();
            var minutes = r.TryGetProperty("minutes", out var m) ? Math.Clamp(m.GetInt32(), 1, 60) : 10;
            if (open) pairing.OpenWindow(TimeSpan.FromMinutes(minutes)); else pairing.CloseWindow();
            return Results.Json(new { ok = true, pairingWindowOpen = pairing.WindowOpen });
        });

        app.MapGet("/admin/devices", (HttpContext c) =>
        {
            if (!IsAdmin(c)) return Results.NotFound();
            var s = Store.LoadOrEmpty(idDir);
            return Results.Json(new
            {
                devices = s.Devices.Select(d => new
                {
                    deviceId = d.DeviceId,
                    displayName = d.UntrustedDisplayName,   // 自报名:仅显示,永不进 prompt
                    status = d.Status,
                    approvedAt = d.ApprovedAt,
                    defaultMemberId = d.DefaultMemberId,
                    // 同名设备很常见(实机就有两条 SENIORBIRDS),界面必须能靠它区分,不能只按名字
                    certSha256Short = s.Certs.FirstOrDefault(x => x.DeviceId == d.DeviceId && x.Status == "active")?.CertSha256 is { Length: >= 8 } fp
                                      ? fp[..8] : null,
                }),
                members = s.Members.Select(m => new { memberId = m.MemberId, displayName = m.DisplayName, role = m.Role }),
                generation = s.IdentityGeneration,
            });
        });

        app.MapPost("/admin/devices/revoke", async (HttpContext c) =>
        {
            if (!IsAdmin(c)) return Results.NotFound();
            var r = (await JsonDocument.ParseAsync(c.Request.Body)).RootElement;
            try
            {
                var gen = Store.Mutate(idDir, s =>   // ★ 命名 Mutex 串行:吊销不能被并发配对写掉
                {
                    s.RevokeDevice(r.GetProperty("deviceId").GetString()!);
                    return s.IdentityGeneration;
                });
                return Results.Json(new { ok = true, generation = gen });
            }
            catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 409); }
        });

        // ---------------------------------------------------------------- D? 设备证书续签(局域网口,两条)
        //
        // ★ 这两条**不是**业务路由,所以放在 MapFallback 之前显式声明:
        //   业务面要求 IsActive,而续签的第二步(complete)恰恰是用一张**还没 active** 的候选证书打的
        //   —— 落进业务面会被 401 挡死。这与 /pair/complete 的处境完全一样(见上面那段注释)。
        //
        // ★ 两条都不需要人工批准、不需要六词:身份没有变(同一个 device_id、同一个 CA、同一把私钥),
        //   授权完全落在 mTLS 上。详见 identity/Renewal.cs 顶部。
        var renewal = new Renewal(idDir, cfg.SecretsDir);

        app.MapPost("/identity/renew/enroll", async (HttpContext ctx) =>
        {
            var cert = ctx.Connection.ClientCertificate;
            if (cert is null) return Results.Json(new { error = new { type = "client_cert_required" } }, statusCode: 401);
            var fp = Convert.ToHexString(SHA256.HashData(cert.RawData));
            var r = (await JsonDocument.ParseAsync(ctx.Request.Body)).RootElement;
            try
            {
                var res = renewal.Enroll(fp, Convert.FromBase64String(r.GetProperty("csr").GetString()!), DateTimeOffset.UtcNow);
                return Results.Json(new
                {
                    renewalId = res.RenewalId,
                    candidateCert = Convert.ToBase64String(res.CandidateDer),
                    candidateSha256 = res.CandidateSha256,
                    notAfter = res.NotAfter.ToString("O"),
                });
            }
            // ★ 401 用的是**业务面同一个词**(lan_device_unknown):旧证书已不 active 的含义
            //   与业务面那条一模一样,给两个词会让客户端多一条歧义分支。
            catch (UnauthorizedAccessException) { return Results.Json(new { error = new { type = "lan_device_unknown" } }, statusCode: 401); }
            catch (Exception ex) { return Results.Json(new { error = new { type = "renew_failed", detail = ex.Message } }, statusCode: 400); }
        });

        app.MapPost("/identity/renew/complete", (HttpContext ctx) =>
        {
            var cert = ctx.Connection.ClientCertificate;
            if (cert is null) return Results.Json(new { error = new { type = "client_cert_required" } }, statusCode: 401);
            var fp = Convert.ToHexString(SHA256.HashData(cert.RawData));
            try
            {
                // changed=false 表示"之前就完成过"—— 幂等重试,同样回 200。
                var changed = renewal.Complete(ctx.Request.Query["renewalId"].ToString(), fp);
                return Results.Json(new { ok = true, changed });
            }
            catch (UnauthorizedAccessException) { return Results.Json(new { error = new { type = "lan_device_unknown" } }, statusCode: 401); }
            catch (Exception ex) { return Results.Json(new { error = new { type = "renew_failed", detail = ex.Message } }, statusCode: 409); }
        });

        // everything else = business: requires an ACTIVE member cert, proxied to the gateway.
        app.MapFallback(async (HttpContext ctx) =>
        {
            // ★ 401 带上语义(2026-07-31 审计):此前是空体 401,客户端无法区分"被解除"和"连不上",
            //   于是统一显示成"主机未开启"(HubState.Revoked 成了死代码)。客户端已经在认
            //   "lan_device_unknown" 这个词,给出它即可让"被解除"如实到达界面。
            //   ★ 别把"没带证书"写成含 revoked 的词,否则客户端会把它误判成已解除。
            var cert = ctx.Connection.ClientCertificate;
            if (cert is null)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = new { type = "client_cert_required" } });
                return;
            }
            var fp = Convert.ToHexString(SHA256.HashData(cert.RawData));
            if (!Store.LoadOrEmpty(idDir).IsActive(fp))   // revoked/candidate -> 401
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = new { type = "lan_device_unknown" } });
                return;
            }
            await Proxy(ctx, http, cfg.UpstreamBase, fp);
        });

        return app;
    }

    static bool ValidateClient(X509Certificate2 cert, X509Certificate2 caPublic)
    {
        try
        {
            // TLS layer: accept any cert that chains to our CA + carries clientAuth EKU, so the cert is
            // available to routes. Membership (active member) is enforced on business routes; the candidate
            // match is enforced on /pair/complete. This lets a not-yet-active candidate complete pairing.
            return Ca.VerifyChainAndEku(cert, caPublic, Ca.OidClientAuth);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("VALIDATE-CLIENT-THREW: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    static X509Certificate2 LoadServerCert(string idDir, string secDir)
    {
        // SChannel server auth cannot use an in-memory CNG-key association (CopyWithPrivateKey alone ->
        // "unexpected EOF"); the cert must be materialized via the cert store so SChannel gets a real
        // credential handle to the TPM key. Idempotent: reuse if already present with its key.
        var pub = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(idDir, "server.cer")));
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, pub.Thumbprint, false);
        if (found.Count > 0 && found[0].HasPrivateKey) return found[0];
        var loc = JsonDocument.Parse(File.ReadAllText(Path.Combine(secDir, "identity-locators.json"))).RootElement;
        var serverKeyName = loc.GetProperty("server_key_name").GetString()!;
        var serverProvider = loc.TryGetProperty("server_provider", out var sp) ? sp.GetString()! : Ca.TlsKeyProvider;
        using var key = new ECDsaCng(CngKey.Open(serverKeyName, new CngProvider(serverProvider)));
        using var withKey = pub.CopyWithPrivateKey(key);
        store.Add(withKey);
        return store.Certificates.Find(X509FindType.FindByThumbprint, pub.Thumbprint, false)[0];
    }

    static async Task Proxy(HttpContext ctx, HttpClient http, string upstreamBase, string fp)
    {
        var target = upstreamBase.TrimEnd('/') + ctx.Request.Path + ctx.Request.QueryString;
        var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);
        if (ctx.Request.ContentLength is > 0) req.Content = new StreamContent(ctx.Request.Body);
        foreach (var h in ctx.Request.Headers)
        {
            var n = h.Key;
            if (n.StartsWith("X-LocalAI-", StringComparison.OrdinalIgnoreCase)) continue;  // strip client-provided identity headers
            if (n is "Host" or "Connection" or "Transfer-Encoding" or "Keep-Alive" or "Content-Length") continue;
            if (!req.Headers.TryAddWithoutValidation(n, (IEnumerable<string>)h.Value))
                req.Content?.Headers.TryAddWithoutValidation(n, (IEnumerable<string>)h.Value);
        }
        req.Headers.TryAddWithoutValidation("X-LocalAI-Cert-Sha256", fp);   // inject the VERIFIED fingerprint
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        ctx.Response.StatusCode = (int)resp.StatusCode;
        foreach (var h in resp.Headers) ctx.Response.Headers[h.Key] = h.Value.ToArray();
        foreach (var h in resp.Content.Headers) ctx.Response.Headers[h.Key] = h.Value.ToArray();
        ctx.Response.Headers.Remove("transfer-encoding");
        await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }
}
