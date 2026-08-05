// P3b -- localai-client-transport CLI.
//   selftest              spin up a scratch hub + minimal Edge (loopback) and run the full client flow
//   pair  <edge-url> <ip:port> <state-dir>   pair this device (prints the six-word SAS to compare)
//   call  <state-dir> <ip:port> <path>       make an mTLS business call with the saved profile

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LocalAI.ClientTransport;
using LocalAI.Identity;
using Microsoft.AspNetCore.Server.Kestrel.Https;

return await ((args.Length == 0 ? "" : args[0]) switch
{
    "selftest" => Selftest(),
    "pair" => Pair(args),
    "call" => Call(args),
    _ => Task.FromResult(Usage()),
});

static int Usage()
{
    Console.WriteLine("usage: localai-client-transport <selftest | pair <edge-url> <ip:port> <state-dir> | call <state-dir> <ip:port> <path>>");
    return 2;
}

static async Task<int> Pair(string[] a)
{
    if (a.Length < 4) return Usage();
    var dial = ParseEp(a[2]);
    var profile = await Transport.Pair(a[1], dial, a[3], Environment.MachineName, (reqId, sas) =>
    {
        Console.WriteLine("\n  在主机上核对这六个词,一致再批准这台设备:\n    " + string.Join("  ", sas) + "\n");
        Console.WriteLine("  (等待主机批准…)");
        return Task.CompletedTask;
    });
    Console.WriteLine("paired. profile saved to " + a[3]);
    return 0;
}

static async Task<int> Call(string[] a)
{
    if (a.Length < 4) return Usage();
    var profile = JsonSerializer.Deserialize<ClientProfile>(File.ReadAllText(Path.Combine(a[1], "profile.json")))!;
    var (status, body) = await Transport.Call(profile, ParseEp(a[2]), a[3]);
    Console.WriteLine(status + " " + body);
    return status == 200 ? 0 : 1;
}

static IPEndPoint ParseEp(string s) { var p = s.Split(':'); return new IPEndPoint(IPAddress.Parse(p[0]), int.Parse(p[1])); }

// ---------------------------------------------------------------- selftest
static async Task<int> Selftest()
{
    int pass = 0, fail = 0;
    void Assert(bool c, string m) { if (c) { pass++; Console.WriteLine("  PASS  " + m); } else { fail++; Console.WriteLine("  FAIL  " + m); } }

    var root = Path.Combine(Path.GetTempPath(), "localai-cli-transport-" + Guid.NewGuid().ToString("N")[..8]);
    var idDir = Path.Combine(root, "identity");
    var secDir = Path.Combine(root, "secrets");
    var stateDir = Path.Combine(root, "client");
    const int PORT = 18446;
    string? caKey = null, srvKey = null, clientKeyName = null, serverThumb = null;
    WebApplication? edge = null;
    try
    {
        var hub = Identity.Init(idDir, secDir);
        caKey = hub.CaKeyName; srvKey = hub.ServerKeyName;
        serverThumb = JsonDocument.Parse(File.ReadAllText(Path.Combine(secDir, "identity-locators.json"))).RootElement.GetProperty("server_thumbprint").GetString();
        var caPublic = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(idDir, "ca.cer")));
        var pairing = new Pairing(idDir, secDir);
        pairing.OpenWindow(TimeSpan.FromMinutes(30));

        edge = BuildTestEdge(idDir, secDir, pairing, caPublic, PORT);
        await edge.StartAsync();

        var edgeUrl = $"https://{hub.ServerName}:{PORT}";
        var dial = new IPEndPoint(IPAddress.Loopback, PORT);

        string[]? shownSas = null;
        var profile = await Transport.Pair(edgeUrl, dial, stateDir, "2nd-PC-test", (reqId, sas) =>
        {
            shownSas = sas;             // client showed the six words
            pairing.Approve(reqId);     // host-admin approves out of band (SAS matched)
            return Task.CompletedTask;
        });

        Assert(shownSas is { Length: 6 }, "client showed a six-word SAS to compare");
        Assert(!string.IsNullOrEmpty(profile.DeviceCertB64), "pairing produced a device profile + cert");
        Assert(File.Exists(Path.Combine(stateDir, "profile.json")), "profile.json persisted");

        var (status, body) = await Transport.Call(profile, dial, "/v1/models");
        Assert(status == 200 && body == "ok", "business call over mTLS as active member -> 200 (" + status + ")");

        // ══════════════════════════════════════════════════════════════════════
        //  B16 · 词表版本必须能上线路 —— 否则换表当天每次配对都会被指控为中间人攻击
        // ══════════════════════════════════════════════════════════════════════
        {
            // 主机必须在 enroll 应答里报出自己的词表版本。★ 没有这个字段,客户端就**无法区分**
            //   【两端词表版本不同】(索引一样、词不一样)与【真的有人在中间捣鬼】——
            //   而 Transport.Pair 原先对两者只有一种说法:"possible MITM; aborting pairing"。
            using var probe = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            });
            using var k3 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var body3 = JsonSerializer.Serialize(new
            {
                csr = Convert.ToBase64String(new CertificateRequest("CN=probe", k3, HashAlgorithmName.SHA256).CreateSigningRequest()),
                clientNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                claimSecretHash = Convert.ToBase64String(SHA256.HashData(RandomNumberGenerator.GetBytes(32))),
                protocolVersion = 1,
                displayName = "wordlist-probe",
            });
            using var pr = await probe.PostAsync($"https://127.0.0.1:{PORT}/pair/enroll",
                                                 new StringContent(body3, System.Text.Encoding.UTF8, "application/json"));
            var pd = JsonDocument.Parse(await pr.Content.ReadAsStringAsync()).RootElement;
            Assert(pd.TryGetProperty("sasWordlistVersion", out var wlv) && wlv.GetString() == Wordlist.Version,
                   "★★ /pair/enroll 应答里带着主机的 SAS 词表版本(" + Wordlist.Version + ")—— 这是客户端区分"
                   + "『版本不同』与『真的可疑』的唯一依据");

            var note = Wordlist.VersionMismatchNote("localai-sas-wordlist-v0-" + "placeholder", Wordlist.Version);
            // ★ 针拼出来写(ASSERTION-PITFALLS 第 1 条):否则这行断言自己的字面量会被扫描类守卫抓成"违例"。
            foreach (var forbidden in new[] { "中间" + "人", "攻" + "击", "MI" + "TM" })
                Assert(!note.Contains(forbidden),
                       $"★★ 版本不一致的文案里**不出现**「{forbidden}」—— 版本落后与被攻击的处置完全相反,"
                       + "把前者说成后者会让人不敢再配对,也永远想不到去更新客户端");
            Assert(note.Contains("版本"), "★ 版本不一致的文案明确说出这是版本问题");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  D? 丙 · TLS 失败四分类 —— 补上【本机设备证书过期】这一格(此前是空的)
        // ══════════════════════════════════════════════════════════════════════
        {
            var t0 = DateTimeOffset.UtcNow;

            // ★★ 本机证书过期【不靠异常文本】判 —— 靠档案里那张证书自己的 NotAfter。
            //   2026-08-05 实测:设备证书过期时客户端拿到的是
            //   IOException -> Win32Exception「证书链是由不受信任的颁发机构颁发的」(本地化),
            //   异常链里连 AuthenticationException 都没有 ⇒ 任何基于英文针的判据都扑空 ⇒ 归到 Offline
            //   ⇒ 界面说「中枢没开机」,用户跑去重启一个没病的中枢。
            var expiredProfile = new ClientProfile { CaCertB64 = profile.CaCertB64, KeyName = profile.KeyName, Dial = profile.Dial, EdgeUrl = profile.EdgeUrl, HubId = profile.HubId, DeviceCertB64 = profile.DeviceCertB64 };
            // 造一张过期的设备证书塞进档案(同一个 CA 签,窗口显式给定)
            var caFull = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(profile.CaCertB64));
            var locJ = JsonDocument.Parse(File.ReadAllText(Path.Combine(secDir, "identity-locators.json"))).RootElement;
            using (var k = new ECDsaCng(CngKey.Open(profile.KeyName, new CngProvider(Ca.TlsKeyProvider))))
            {
                var pub = PublicKey.CreateFromSubjectPublicKeyInfo(k.ExportSubjectPublicKeyInfo(), out _);
                using var dead = Ca.IssueLeafWindow(locJ.GetProperty("ca_key_name").GetString()!, caFull, pub,
                    "device-dead", null, "urn:localai:device:dead", false, true,
                    t0.AddDays(-100), t0.AddDays(-1));
                expiredProfile.DeviceCertB64 = Convert.ToBase64String(dead.RawData);
            }

            // 这就是实测到的那条异常链的形状(没有 AuthenticationException、消息是本地化的 Win32 文本)
            var realShape = new HttpRequestException("An error occurred while sending the request.",
                                new IOException("The decryption operation failed, see inner exception.",
                                    new System.ComponentModel.Win32Exception(-2146893019, "证书链是由不受信任的颁发机构颁发的。")));

            Assert(TlsFailure.Classify(realShape, expiredProfile, t0) == TlsFailureKind.LocalDeviceCertExpired,
                   "★★★ 【本机设备证书过期】被判成 LocalDeviceCertExpired —— 这一格此前是空的,"
                   + "旧判据会把它归成 Offline =「中枢没开机」,把人支去重启一个没病的中枢");

            // ★ 同一条异常链,若本机证书**没有**过期,就不许再冒充这个结论
            Assert(TlsFailure.Classify(realShape, profile, t0) != TlsFailureKind.LocalDeviceCertExpired,
                   "★★ 反向:本机证书没过期时,同一条异常**不会**被判成本机证书过期(判据不是恒真的)");

            // 主机服务器证书过期:这一句是 .NET 自己拼的,恒为英文、带 NotTimeValid
            var srvExpired = new HttpRequestException("The SSL connection could not be established.",
                                new System.Security.Authentication.AuthenticationException(
                                    "The remote certificate is invalid because of errors in the certificate chain: NotTimeValid"));
            Assert(TlsFailure.Classify(srvExpired, profile, t0) == TlsFailureKind.ServerCertExpired,
                   "★ 【主机服务器证书过期】仍判 ServerCertExpired(D49 那条路径没被改坏)");

            // ★★★ 下面每一条针都是 2026-08-06 **实测抓到的原文**,不是手写的近似句。
            //   原先这里写的是 "The remote certificate is invalid: UntrustedRoot" —— 一句 .NET
            //   **从来不会发出**的话。断言拿一个虚构的输入喂给判据,于是它测的是一个不存在的世界:
            //   判据在真实消息上失灵,而这条断言照样绿。
            //   ⇒ 凡是"判据要认某段外部文本"的断言,针必须来自**实测输出**,不能凭印象写。
            static Exception Wrap(string msg) => new HttpRequestException(
                "The SSL connection could not be established, see inner exception.",
                new System.Security.Authentication.AuthenticationException(msg));

            // 形状一:只有链错误 —— 消息里**带**链状态词
            Assert(TlsFailure.Classify(Wrap("The remote certificate is invalid because of errors in the certificate chain: PartialChain"), profile, t0)
                   == TlsFailureKind.HubIdentityChanged,
                   "★ 链不到钉住的 CA(PartialChain)-> HubIdentityChanged"
                   + " —— ★ 注意是 PartialChain 而**不是** UntrustedRoot:签发者不在 CustomTrustStore 时是前者");
            Assert(TlsFailure.Classify(Wrap("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot"), profile, t0)
                   == TlsFailureKind.HubIdentityChanged,
                   "★ 对方出示自签名证书(UntrustedRoot)-> HubIdentityChanged");

            // ★★★ 形状二:**还有名字不匹配** —— 消息里一个链状态词都没有。
            //   这是主机重铸身份时**唯一**会出现的形状(Identity.Init 换 GUID ⇒ 换 CA 也换 SAN),
            //   也就是 HubIdentityChanged 这一态**存在的理由**。
            //   ⇒ 只认 UntrustedRoot/PartialChain 的判据在真正需要它的那一刻会全部落空。
            Assert(TlsFailure.Classify(Wrap("The remote certificate is invalid according to the validation procedure: RemoteCertificateNameMismatch, RemoteCertificateChainErrors"), profile, t0)
                   == TlsFailureKind.HubIdentityChanged,
                   "★★★ 【主机重铸了身份】(名字不匹配 + 链不上,消息里**没有**任何链状态词)"
                   + " -> HubIdentityChanged。这一行红 = 用户在真的换了中枢时会看到「中枢没开机」");
            Assert(TlsFailure.Classify(Wrap("The remote certificate is invalid according to the validation procedure: RemoteCertificateNameMismatch"), profile, t0)
                   == TlsFailureKind.HubIdentityChanged,
                   "★★ 只有名字不匹配(链是好的:陈旧档案 / EdgeUrl 还是 ip 形式)-> HubIdentityChanged");

            // ★★ 排序:两个词同时出现时,**身份问题压过过期**。
            //   判成 ServerCertExpired 的话,文案会说"在主机上续签即可,不必重新配对" ——
            //   而那张叶证书根本链不到钉住的 CA,续签多少次都没用。
            Assert(TlsFailure.Classify(Wrap("The remote certificate is invalid because of errors in the certificate chain: NotTimeValid, PartialChain"), profile, t0)
                   == TlsFailureKind.HubIdentityChanged,
                   "★★ 同时带 NotTimeValid 与 PartialChain 时判 **HubIdentityChanged** 而不是过期"
                   + " —— 链都不通了,续签解决不了");

            // ★★ 不许再把任意握手失败兜底成"必须重新配对"。
            //   实测:拨到的 IP 上现在跑着普通 HTTP 服务时就是这一条。
            Assert(TlsFailure.Classify(Wrap("Cannot determine the frame size or a corrupted frame was received."), profile, t0)
                   == TlsFailureKind.Unknown,
                   "★★★ 拨到一个非 TLS 服务(地址被别人占了)-> Unknown,**不得**判成"
                   + "「必须重新配对」—— 重新配对会先删掉本机私钥,为一个填错地址的问题销毁有效身份");

            // ★★ 判不出来时**不猜**。裸的 AuthenticationException 不再兜底成 HubIdentityChanged ——
            //   那个兜底给出的建议是"必须重新配对",而重新配对会删掉本机私钥。
            Assert(TlsFailure.Classify(new System.Security.Authentication.AuthenticationException("Authentication failed."), profile, t0)
                   == TlsFailureKind.Unknown,
                   "★★ 认不出的 TLS 失败判 Unknown,**不再**兜底成『必须重新配对』(那条建议是破坏性的)");

            // 四种处置文案必须各不相同,否则分开归因就白做了
            var texts = new[] { TlsFailureKind.ServerCertExpired, TlsFailureKind.LocalDeviceCertExpired,
                                TlsFailureKind.HubIdentityChanged, TlsFailureKind.Unknown }
                        .Select(TlsFailure.Explain).ToArray();
            Assert(texts.Distinct().Count() == 4, "★ 四种根因的处置文案互不相同");
            Assert(TlsFailure.Explain(TlsFailureKind.LocalDeviceCertExpired).Contains("不要点"),
                   "★★ 本机证书过期的文案明确劝阻【重新配对】—— 那会删掉私钥,销毁一个只需续签的身份");

            // ★ 过期【之前】就看得见(不必等握手失败)
            Assert(TlsFailure.LocalCertPhase(profile, t0) == CertPhase.Healthy, "刚配对:本机证书 Healthy");
            Assert(TlsFailure.LocalCertPhase(profile, t0.AddDays(85)) == CertPhase.Critical,
                   "★★ 到期前 5 天:Critical —— **握手还好着的时候**就看得见,不是失败之后才归因");
            Assert(TlsFailure.LocalCertPhase(profile, t0.AddDays(65)) == CertPhase.RenewDue,
                   "★ 到期前 25 天:RenewDue(自动续签动手,但**不打扰用户**)");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  D? 丁 · 设备证书续签:客户端驱动器(端到端)
        // ══════════════════════════════════════════════════════════════════════
        {
            var oldCert = profile.DeviceCertB64;

            // 还没到窗口 -> 不动手
            Assert(await Transport.RenewDeviceCertIfDue(profile, dial, stateDir, DateTimeOffset.UtcNow)
                   == Transport.RenewOutcome.NotDue, "未进入续签窗口:客户端不动手");
            Assert(profile.DeviceCertB64 == oldCert, "★ 不动手就是真的一个字节都没改");

            // 进入窗口(注入"65 天后")-> 续一次
            var due = DateTimeOffset.UtcNow.AddDays(65);
            Assert(await Transport.RenewDeviceCertIfDue(profile, dial, stateDir, due) == Transport.RenewOutcome.Renewed,
                   "★★ 进入续签窗口:客户端自动续签成功(全程**不经过配对流程**)");
            Assert(profile.DeviceCertB64 != oldCert, "档案里的设备证书已换成新的");
            Assert(profile.PendingDeviceCertB64.Length == 0 && profile.PendingRenewalId.Length == 0,
                   "★ 完成之后 Pending 已清空(没有留半截状态)");

            var reloaded = JsonSerializer.Deserialize<ClientProfile>(File.ReadAllText(Path.Combine(stateDir, "profile.json")))!;
            Assert(reloaded.DeviceCertB64 == profile.DeviceCertB64, "★ 新证书已落盘(重启后仍然是它)");

            // 新证书立刻可用
            var (s2, b2) = await Transport.Call(profile, dial, "/v1/models");
            Assert(s2 == 200 && b2 == "ok", "★★ 续签后用**新证书**做业务调用 -> 200 (" + s2 + ")");

            // ★★ 崩溃重入:模拟"候选已签出、还没确认"就崩了
            {
                var crashed = JsonSerializer.Deserialize<ClientProfile>(File.ReadAllText(Path.Combine(stateDir, "profile.json")))!;
                var csr2 = default(byte[]);
                using (var k = new ECDsaCng(CngKey.Open(crashed.KeyName, new CngProvider(Ca.TlsKeyProvider))))
                    csr2 = new CertificateRequest("CN=client", k, HashAlgorithmName.SHA256).CreateSigningRequest();
                var (es, eb) = await Transport.Send(crashed, dial, HttpMethod.Post, "/identity/renew/enroll",
                                                    new { csr = Convert.ToBase64String(csr2) });
                Assert(es == 200, "重入用例:先签出一张候选证书 (" + es + ")");
                var ed = JsonDocument.Parse(eb).RootElement;
                crashed.PendingDeviceCertB64 = ed.GetProperty("candidateCert").GetString()!;
                crashed.PendingRenewalId = ed.GetProperty("renewalId").GetString()!;
                File.WriteAllText(Path.Combine(stateDir, "profile.json"), JsonSerializer.Serialize(crashed));
                // ★ 此刻档案里两张都在:旧的还 active、新的挂在 Pending 上 —— 这正是崩溃现场的样子
                Assert((await Transport.Call(crashed, dial, "/v1/models")).status == 200,
                       "★★ 崩在'签出候选、未确认'之间:**旧证书仍然可用**(设备没有掉线)");

                var outcome = await Transport.RenewDeviceCertIfDue(crashed, dial, stateDir, DateTimeOffset.UtcNow);
                Assert(outcome == Transport.RenewOutcome.ResumedAndCompleted,
                       "★★★ 重入:发现 Pending 就**把上一次做完**,而不是重新签一张(否则每崩一次多一行 candidate)");
                Assert(crashed.PendingDeviceCertB64.Length == 0, "★ 重入完成后 Pending 清空");
                Assert((await Transport.Call(crashed, dial, "/v1/models")).status == 200, "★ 重入完成后新证书可用");
                profile = crashed;
            }
        }

        // sanity: a revoked device is refused
        var store = Store.LoadOrEmpty(idDir);
        store.RevokeDevice(store.Devices[0].DeviceId);
        store.Save(idDir);
        int rc;
        try { (rc, _) = await Transport.Call(profile, dial, "/v1/models"); } catch { rc = -1; }
        Assert(rc != 200, "after revoke, the same profile is refused (" + rc + ")");
    }
    finally
    {
        if (edge is not null) await edge.StopAsync();
        if (caKey is not null) Ca.DeleteKey(caKey);
        if (srvKey is not null) Ca.DeleteKey(srvKey);
        if (clientKeyName is not null) Transport.DeleteKey(clientKeyName);
        try { foreach (var f in Directory.Exists(stateDir) ? Directory.GetFiles(stateDir) : Array.Empty<string>()) { } } catch { }
        // client key name is embedded in the profile; delete it too
        try { var pf = Path.Combine(stateDir, "profile.json"); if (File.Exists(pf)) Transport.DeleteKey(JsonSerializer.Deserialize<ClientProfile>(File.ReadAllText(pf))!.KeyName); } catch { }
        if (serverThumb is not null) { try { using var s = new X509Store(StoreName.My, StoreLocation.CurrentUser); s.Open(OpenFlags.ReadWrite); foreach (var c in s.Certificates.Find(X509FindType.FindByThumbprint, serverThumb, false)) s.Remove(c); } catch { } }
        try { Directory.Delete(root, true); } catch { }
    }
    Console.WriteLine($"\nclient-transport selftest: PASS={pass} FAIL={fail}");
    return fail > 0 ? 1 : 0;
}

// minimal in-process Edge for the selftest: mTLS (chain+EKU) + /pair routes + a business echo.
static WebApplication BuildTestEdge(string idDir, string secDir, Pairing pairing, X509Certificate2 caPublic, int port)
{
    // server cert with the identity's software key (materialized via store, per B17/D44)
    var pub = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(idDir, "server.cer")));
    var loc = JsonDocument.Parse(File.ReadAllText(Path.Combine(secDir, "identity-locators.json"))).RootElement;
    using (var key = new ECDsaCng(CngKey.Open(loc.GetProperty("server_key_name").GetString()!, new CngProvider(loc.GetProperty("server_provider").GetString()!))))
    using (var wk = pub.CopyWithPrivateKey(key))
    using (var st = new X509Store(StoreName.My, StoreLocation.CurrentUser)) { st.Open(OpenFlags.ReadWrite); st.Add(wk); }
    using var store0 = new X509Store(StoreName.My, StoreLocation.CurrentUser); store0.Open(OpenFlags.ReadOnly);
    var serverCert = store0.Certificates.Find(X509FindType.FindByThumbprint, pub.Thumbprint, false)[0];

    var b = WebApplication.CreateBuilder();
    b.Logging.ClearProviders();
    b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port, lo => lo.UseHttps(h =>
    {
        h.ServerCertificate = serverCert;
        h.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        h.ClientCertificateValidation = (cert, _, __) => Ca.VerifyChainAndEku(cert, caPublic, Ca.OidClientAuth);
    })));
    var app = b.Build();
    app.MapPost("/pair/enroll", async (HttpContext ctx) =>
    {
        var r = (await JsonDocument.ParseAsync(ctx.Request.Body)).RootElement;
        var en = pairing.Enroll(Convert.FromBase64String(r.GetProperty("csr").GetString()!), Convert.FromBase64String(r.GetProperty("clientNonce").GetString()!), Convert.FromBase64String(r.GetProperty("claimSecretHash").GetString()!), r.GetProperty("protocolVersion").GetInt32(), r.GetProperty("displayName").GetString() ?? "");
        return Results.Json(new { requestId = en.RequestId, serverNonce = Convert.ToBase64String(en.ServerNonce), sas = en.Sas, caCert = Convert.ToBase64String(caPublic.RawData), hubId = pairing.HubId, sasWordlistVersion = Wordlist.Version });
    });
    app.MapPost("/pair/status", async (HttpContext ctx) =>
    {
        var r = (await JsonDocument.ParseAsync(ctx.Request.Body)).RootElement;
        var s = pairing.Status(r.GetProperty("requestId").GetString()!, Convert.FromBase64String(r.GetProperty("claimSecret").GetString()!));
        return Results.Json(new { status = s.Status, claimNonce = s.ClaimNonce is null ? null : Convert.ToBase64String(s.ClaimNonce), candidateSha256 = s.CandidateSha256 });
    });
    app.MapPost("/pair/claim", async (HttpContext ctx) =>
    {
        var r = (await JsonDocument.ParseAsync(ctx.Request.Body)).RootElement;
        using var cand = pairing.Claim(r.GetProperty("requestId").GetString()!, Convert.FromBase64String(r.GetProperty("claimSecret").GetString()!), Convert.FromBase64String(r.GetProperty("challengeSig").GetString()!));
        return Results.Json(new { candidateCert = Convert.ToBase64String(cand.RawData) });
    });
    app.MapPost("/pair/complete", (HttpContext ctx) =>
    {
        var cert = ctx.Connection.ClientCertificate;
        if (cert is null) return Results.StatusCode(401);
        pairing.Complete(ctx.Request.Query["requestId"].ToString(), Convert.ToHexString(SHA256.HashData(cert.RawData)));
        return Results.Text("active");
    });
    // D? 设备证书续签两条路由(与 lan-edge 生产路由同形状,让客户端驱动器能被端到端测到)
    var renewal = new Renewal(idDir, secDir);
    app.MapPost("/identity/renew/enroll", async (HttpContext ctx) =>
    {
        var cert = ctx.Connection.ClientCertificate;
        if (cert is null) return Results.StatusCode(401);
        var r = (await JsonDocument.ParseAsync(ctx.Request.Body)).RootElement;
        try
        {
            var res = renewal.Enroll(Convert.ToHexString(SHA256.HashData(cert.RawData)),
                                     Convert.FromBase64String(r.GetProperty("csr").GetString()!), DateTimeOffset.UtcNow);
            return Results.Json(new { renewalId = res.RenewalId, candidateCert = Convert.ToBase64String(res.CandidateDer), candidateSha256 = res.CandidateSha256, notAfter = res.NotAfter.ToString("O") });
        }
        catch (UnauthorizedAccessException) { return Results.Json(new { error = new { type = "lan_device_unknown" } }, statusCode: 401); }
    });
    app.MapPost("/identity/renew/complete", (HttpContext ctx) =>
    {
        var cert = ctx.Connection.ClientCertificate;
        if (cert is null) return Results.StatusCode(401);
        try { return Results.Json(new { ok = true, changed = renewal.Complete(ctx.Request.Query["renewalId"].ToString(), Convert.ToHexString(SHA256.HashData(cert.RawData))) }); }
        catch (UnauthorizedAccessException) { return Results.StatusCode(401); }
    });

    app.MapFallback((HttpContext ctx) =>
    {
        var cert = ctx.Connection.ClientCertificate;
        if (cert is null) return Results.StatusCode(401);
        return Store.LoadOrEmpty(idDir).IsActive(Convert.ToHexString(SHA256.HashData(cert.RawData))) ? Results.Text("ok") : Results.StatusCode(401);
    });
    return app;
}
