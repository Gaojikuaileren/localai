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

    // The identity keys (CA in TPM) live in the user's key-isolation context. A UAC-elevated (high
    // integrity) process cannot open them -> `approve` fails with "Keyset does not exist" even though
    // TLS (software key) still works. init runs as the normal user, so the Edge must too. Refuse early
    // with a clear message rather than let the operator hit the cryptic failure at approve time.
    if (IsElevated())
    {
        Console.WriteLine("✗ 检测到以【管理员】身份运行 —— 本程序不能用管理员跑。");
        Console.WriteLine("  身份密钥(CA)在你普通用户的 TPM 上下文里,管理员进程访问会报「密钥集不存在」。");
        Console.WriteLine("  请用【普通】PowerShell,或直接双击  dist\\host\\启动Edge.cmd 。");
        Console.WriteLine("  (开放端口的 lan-firewall.ps1 才需要管理员,且只需一次,已完成。)");
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
    await app.StartAsync();

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
    await app.RunAsync();
    return 0;
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
        edge = Edge.Build(new EdgeConfig(idDir, secDir, $"http://127.0.0.1:{upstreamPort}", 18443));
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

        // 4. revoke the paired device -> its cert can no longer reach business
        var store = Store.LoadOrEmpty(idDir);
        store.RevokeDevice(deviceId);
        store.Save(idDir);
        using (var c = MkClient(clientCert))
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
    public static WebApplication Build(EdgeConfig cfg, X509Certificate2? serverCertOverride = null, Pairing? pairingOverride = null)
    {
        var idDir = cfg.IdentityDir;
        var caPublic = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(idDir, "ca.cer")));
        // NOTE: the production path loads the server leaf key from the TPM (LoadServerCert). SChannel
        // cannot currently use a TPM key as a TLS credential ("unexpected EOF") -- see backlog B17.
        // The self-test passes an SChannel-compatible (non-exportable software CNG) server cert so the
        // Edge's mTLS + membership + proxy + pairing LOGIC is fully exercised regardless of that gap.
        var serverCert = serverCertOverride ?? LoadServerCert(idDir, cfg.SecretsDir);
        var pairing = pairingOverride ?? new Pairing(idDir, cfg.SecretsDir);
        if (pairingOverride is null && cfg.OpenPairingWindowOnStart) pairing.OpenWindow(TimeSpan.FromMinutes(30));
        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });

        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);   // ← temporarily surface handshake errors
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 32 * 1024;
            k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            k.Limits.MaxRequestLineSize = 4 * 1024;
            k.Listen(cfg.Bind ?? IPAddress.Loopback, cfg.ListenPort, lo => lo.UseHttps(h =>
            {
                h.ServerCertificate = serverCert;
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
            !IsAdmin(c) ? Results.NotFound() : Results.Json(new { ok = true, hubId = pairing.HubId, pairingWindowOpen = pairing.WindowOpen }));

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
                var s = Store.LoadOrEmpty(idDir);
                s.RevokeDevice(r.GetProperty("deviceId").GetString()!);
                s.Save(idDir);
                return Results.Json(new { ok = true, generation = s.IdentityGeneration });
            }
            catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 409); }
        });

        // everything else = business: requires an ACTIVE member cert, proxied to the gateway.
        app.MapFallback(async (HttpContext ctx) =>
        {
            var cert = ctx.Connection.ClientCertificate;
            if (cert is null) { ctx.Response.StatusCode = 401; return; }
            var fp = Convert.ToHexString(SHA256.HashData(cert.RawData));
            if (!Store.LoadOrEmpty(idDir).IsActive(fp)) { ctx.Response.StatusCode = 401; return; }  // revoked/candidate -> 401
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
