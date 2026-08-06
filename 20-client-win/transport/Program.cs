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
    // V4:本节自己造的具名 CNG 密钥,收尾时逐个删掉(不留孤儿密钥 —— Pair() 已踩过一次)
    var keysToDelete = new List<string>();
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
        //  D? 戊 · 陈旧的 EdgeUrl 不得再让一台好主机看起来像"换了中枢"
        // ══════════════════════════════════════════════════════════════════════
        //  SetDial 只改 Profile.Dial,从不改 Profile.EdgeUrl;而 2026-08-04 之前写下的档案里
        //  EdgeUrl 是 https://<ip>:<port> 形式。URL 的主机名决定 TLS 主机名校验,而服务器证书
        //  的 SAN 是 localai-<hubShort>.local ⇒ 这类档案对**完全正确的**主机也会永久名字不匹配。
        //  ★ 归因修好之后这会变成一条**自信的错误结论**:「必须重新配对」—— 而重新配对删私钥。
        //  ⇒ 不再信任存的 EdgeUrl,改由钉住的 hub_id + 当前拨号端口现算(Transport.EdgeUrlFor)。
        {
            // ★★ 这就是那种坏档案的真实形状:EdgeUrl 是 ip 形式,其余一切正确。
            var stale = JsonSerializer.Deserialize<ClientProfile>(JsonSerializer.Serialize(profile))!;
            stale.EdgeUrl = $"https://127.0.0.1:{PORT}";
            // ★ 必须 try 起来:坏掉时 Transport.Call 是**抛**而不是返回状态码。
            //   不接住的话这一节会把整个套件**崩掉**,连汇总行都没有 —— 那比一条干净的红更难查。
            //   (第一次红测就是这样崩的,所以补上这层。)
            int ss; string sb;
            try { (ss, sb) = await Transport.Call(stale, dial, "/v1/models"); }
            catch (Exception ex) { ss = -1; sb = ex.GetBaseException().Message; }
            Assert(ss == 200 && sb == "ok",
                   "★★★ 档案里 EdgeUrl 还是 ip 形式(SetDial 改不到的那半边)时,业务调用**照常 200**("
                   + ss + " " + sb + ")—— 这一行红 = 一台好主机会被判成「必须重新配对」,而那会删掉本机私钥");

            // 端口跟着 Dial 走,不跟着存的那个字符串走
            Assert(Transport.EdgeUrlFor(stale, dial) == $"https://{hub.ServerName}:{PORT}",
                   "★ URL 由**钉住的 hub_id** 与当前拨号端口现算(" + Transport.EdgeUrlFor(stale, dial) + ")");
            Assert(Transport.EdgeUrlFor(stale, new IPEndPoint(IPAddress.Loopback, 9999)).EndsWith(":9999"),
                   "★ 换了拨号端口,URL 端口跟着变 —— 存的 EdgeUrl 再陈旧也不影响");

            // ★ 加固:改写档案里的 EdgeUrl 指向别处,也改不动主机名校验的目标
            var tampered = JsonSerializer.Deserialize<ClientProfile>(JsonSerializer.Serialize(profile))!;
            tampered.EdgeUrl = "https://evil.example.com:8443";
            Assert(Transport.EdgeUrlFor(tampered, dial) == $"https://{hub.ServerName}:{PORT}",
                   "★★ 篡改档案里的 EdgeUrl **改不动**期望的服务器名(它由钉住的 hub_id 推出)");

            // ★ 认不出 hub_id 形状的极旧档案 -> 退回存的那个,不抛、不猜
            var ancient = JsonSerializer.Deserialize<ClientProfile>(JsonSerializer.Serialize(profile))!;
            ancient.HubId = "not-a-guid";
            ancient.EdgeUrl = "https://legacy.local:1234";
            Assert(Transport.EdgeUrlFor(ancient, dial) == "https://legacy.local:1234",
                   "★ hub_id 认不出来时退回存的 EdgeUrl(维持原行为,不当场变成连不上)");
        }

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

            // ══════════════════════════════════════════════════════════════════
            //  V4 · CONTRACT:cert.pair.enroll —— **单字段升级成键集合**
            // ══════════════════════════════════════════════════════════════════
            //  ★★ 上面那条只钉了 `sasWordlistVersion` **一个字段**。欠债表点名说它
            //    「单字段断言挡不住别的键漂移」,而这条路由上"别的键"恰恰是承重的:
            //    `caCert` / `serverNonce` / `requestId` 任何一个改名,SAS transcript 就算不出来,
            //    而客户端会把那次失败报成**中间人攻击** —— 一个自信的、破坏性的错误结论。
            //  ⇒ 顶层键集合对着 WireContracts 钉死;它与 lan-edge 的服务端半边**读同一份**。
            var peKeys = pd.EnumerateObject().Select(x => x.Name).ToArray();
            Assert(WireContracts.KeysMatch(peKeys, WireContracts.PairEnroll),
                   "★★★ CONTRACT:cert.pair.enroll 客户端半边:顶层键 == 登记表("
                   + WireContracts.Describe(peKeys, WireContracts.PairEnroll) + ")");
            Assert(pd.GetProperty("caCert").GetString()!.Length > 0
                   && pd.GetProperty("serverNonce").GetString()!.Length > 0
                   && pd.GetProperty("requestId").GetString()!.Length == 32
                   && pd.GetProperty("sas").GetArrayLength() == 6,
                   "★★★ CONTRACT:cert.pair.enroll:拿这个形状**真的解得出** SAS transcript 要的每一样"
                   + "(caCert / serverNonce / requestId / 六个词)—— 少任何一样都会被报成中间人攻击");

            var note = Wordlist.VersionMismatchNote("localai-sas-wordlist-v0-" + "placeholder", Wordlist.Version);
            // ★ 针拼出来写(ASSERTION-PITFALLS 第 1 条):否则这行断言自己的字面量会被扫描类守卫抓成"违例"。
            foreach (var forbidden in new[] { "中间" + "人", "攻" + "击", "MI" + "TM" })
                Assert(!note.Contains(forbidden),
                       $"★★ 版本不一致的文案里**不出现**「{forbidden}」—— 版本落后与被攻击的处置完全相反,"
                       + "把前者说成后者会让人不敢再配对,也永远想不到去更新客户端");
            Assert(note.Contains("版本"), "★ 版本不一致的文案明确说出这是版本问题");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  V4 · 配对三条路由的客户端半边 —— ★ **失败分支**与 happy path 一样重要
        // ══════════════════════════════════════════════════════════════════════
        //  欠债表对 /pair/status 的注解是:「握手失败会让设备**永远停在 provisioning**」。
        //  它的形状陷阱在于:**三个键在每一种状态下都在**,只是批准之前后两个的**值是 null**。
        //  ⇒ 只钉顶层键集合是**不够**的 —— 那条断言在 pending 和 approved 上都会绿,
        //    而客户端一旦在 pending 时跳出轮询,就会对 null 调 GetString()!:
        //    要么 NRE、要么拿 null 去算 challenge,两种都表现为「配对走不完」,
        //    而主机侧那条设备记录**永远停在 provisioning**。
        //  ⇒ 所以这一节把「跳出集合」与「放弃集合」也做成登记表的一部分,两侧读同一份。
        {
            using var pc = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            });
            async Task<JsonElement> P(string path, object body)
            {
                using var r = await pc.PostAsync($"https://127.0.0.1:{PORT}{path}",
                    new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"));
                return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            }

            // ★★ 必须是**具名 CNG 密钥**,不能用 ECDsa.Create 的临时密钥:
            //   临时密钥做不了 SChannel 的 TLS 客户端凭据(下面 /pair/complete 那一步要用它握手),
            //   症状是一句没有下文的 "Authentication failed" —— Transport.Pair 用的也是具名密钥。
            var kNameV4 = "localai-t-v4-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            using var kv4 = new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, kNameV4,
                new CngKeyCreationParameters { Provider = new CngProvider(Ca.TlsKeyProvider),
                                               ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing }));
            keysToDelete.Add(kNameV4);
            var secretV4 = RandomNumberGenerator.GetBytes(32);
            var enV4 = await P("/pair/enroll", new
            {
                csr = Convert.ToBase64String(new CertificateRequest("CN=v4", kv4, HashAlgorithmName.SHA256).CreateSigningRequest()),
                clientNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                claimSecretHash = Convert.ToBase64String(SHA256.HashData(secretV4)),
                protocolVersion = 1,
                displayName = "v4-status",
            });
            var reqV4 = enV4.GetProperty("requestId").GetString()!;

            // ── ★★★ 失败分支:批准**之前** ────────────────────────────────
            var stPend = await P("/pair/status", new { requestId = reqV4, claimSecret = Convert.ToBase64String(secretV4) });
            var psKeys = stPend.EnumerateObject().Select(x => x.Name).ToArray();
            Assert(WireContracts.KeysMatch(psKeys, WireContracts.PairStatus),
                   "★★★ CONTRACT:cert.pair.status 客户端半边:顶层键 == 登记表("
                   + WireContracts.Describe(psKeys, WireContracts.PairStatus) + ")");
            var sPend = stPend.GetProperty("status").GetString()!;
            Assert(!WireContracts.PairStatusProceed.Contains(sPend),
                   $"★★★ 失败分支:`{sPend}` **不在**客户端的跳出集合里 —— 跳出去就会对 null 调 GetString()!");
            Assert(stPend.GetProperty("claimNonce").ValueKind == JsonValueKind.Null,
                   "★★★ 失败分支:批准之前 claimNonce 的**值是 null**(键在、值空)—— "
                   + "这正是「只钉顶层键集合」挡不住的那一层,所以两侧都要覆盖它");

            // ── 批准之后:两个字段这才有值,客户端才允许往下走 ──────────────
            pairing.Approve(reqV4);
            var stOkV4 = await P("/pair/status", new { requestId = reqV4, claimSecret = Convert.ToBase64String(secretV4) });
            Assert(WireContracts.PairStatusProceed.Contains(stOkV4.GetProperty("status").GetString()),
                   "★★ 批准之后 status 落在跳出集合里(" + stOkV4.GetProperty("status").GetString() + ")");
            Assert(stOkV4.GetProperty("claimNonce").ValueKind == JsonValueKind.String
                   && stOkV4.GetProperty("candidateSha256").ValueKind == JsonValueKind.String,
                   "★★★ CONTRACT:cert.pair.status:跳出那一刻两个字段**必须**都已经是字符串 —— "
                   + "这两条(前后各一)合起来才是完整的判据,只有后一条等于只测 happy path");

            // ── CONTRACT:cert.pair.claim ─────────────────────────────────
            var nonceV4 = Convert.FromBase64String(stOkV4.GetProperty("claimNonce").GetString()!);
            var cshaV4 = stOkV4.GetProperty("candidateSha256").GetString()!;
            var chalV4 = Pairing.BuildChallenge(Convert.FromHexString(reqV4), nonceV4, Convert.FromHexString(cshaV4));
            var clV4 = await P("/pair/claim", new
            {
                requestId = reqV4,
                claimSecret = Convert.ToBase64String(secretV4),
                challengeSig = Convert.ToBase64String(kv4.SignData(chalV4, HashAlgorithmName.SHA256)),
            });
            var pcKeys = clV4.EnumerateObject().Select(x => x.Name).ToArray();
            Assert(WireContracts.KeysMatch(pcKeys, WireContracts.PairClaim),
                   "★★★ CONTRACT:cert.pair.claim 客户端半边:顶层键 == 登记表("
                   + WireContracts.Describe(pcKeys, WireContracts.PairClaim) + ")");
            Assert(clV4.GetProperty("candidateCert").GetString()!.Length > 0,
                   "★★ CONTRACT:cert.pair.claim:候选证书真的解得出来");

            // ── CONTRACT:cert.pair.complete —— 应答是**文本**,不是 JSON ────
            using var candV4 = X509CertificateLoader.LoadCertificate(
                Convert.FromBase64String(clV4.GetProperty("candidateCert").GetString()!));
            using var candKeyV4 = candV4.CopyWithPrivateKey(kv4);
            var hcV4 = new SocketsHttpHandler { UseProxy = false };
            hcV4.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            hcV4.SslOptions.ClientCertificates = new X509CertificateCollection { candKeyV4 };
            using (var mt = new HttpClient(hcV4))
            using (var rcV4 = await mt.PostAsync($"https://127.0.0.1:{PORT}/pair/complete?requestId=" + reqV4, new StringContent("")))
            {
                var txt = (await rcV4.Content.ReadAsStringAsync()).Trim();
                Assert((int)rcV4.StatusCode == 200 && txt == WireContracts.PairCompleteBody,
                       $"★★★ CONTRACT:cert.pair.complete 客户端半边:应答是**文本** `{WireContracts.PairCompleteBody}`"
                       + $"(实得 {(int)rcV4.StatusCode} `{txt}`)—— 如实按文本契约钉,不给它编一个空键集合");
            }

            // ── 放弃集合:被拒之后客户端必须**立刻停手**,而不是一直轮询到超时 ──
            var enDenyV4 = await P("/pair/enroll", new
            {
                csr = Convert.ToBase64String(new CertificateRequest("CN=v4d", kv4, HashAlgorithmName.SHA256).CreateSigningRequest()),
                clientNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                claimSecretHash = Convert.ToBase64String(SHA256.HashData(secretV4)),
                protocolVersion = 1,
                displayName = "v4-deny",
            });
            var reqDenyV4 = enDenyV4.GetProperty("requestId").GetString()!;
            pairing.Deny(reqDenyV4);
            var stDeny = await P("/pair/status", new { requestId = reqDenyV4, claimSecret = Convert.ToBase64String(secretV4) });
            Assert(WireContracts.PairStatusAbort.Contains(stDeny.GetProperty("status").GetString()),
                   "★★★ 被拒之后 status 落在客户端的**放弃集合**里(" + stDeny.GetProperty("status").GetString() + ")"
                   + " —— 落不进去的话客户端会一直轮询到 5 分钟超时,而主机侧那条记录停在 provisioning");
            Assert(WireContracts.KeysMatch(stDeny.EnumerateObject().Select(x => x.Name), WireContracts.PairStatus),
                   "★★ 失败分支的顶层键集合与成功分支**是同一组**(所以只钉键集合分辨不出它们)");
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
            // ★★★ 2026-08-06 更正:这条原来钉的是「过期文案必须含『不要点』」。
            //   实测(lan-edge selftest 甲2)推翻了它的前提:过期之后续签路由**够不着**,
            //   所以那句劝阻否掉的是唯一的出路,而同一段文案还许诺了一件不可能发生的自动续签。
            //   ⇒ 判据反过来钉:过期这一格**不许**再劝阻重新配对、**不许**再许诺自动续签。
            //   那句劝阻仍然要有,只是搬到了**过期之前**(WarnLocalCert),见下面那一节。
            var expiredNow = TlsFailure.Explain(TlsFailureKind.LocalDeviceCertExpired);
            Assert(!expiredNow.Contains("不要点"),
                   "★★★ 【已经过期】这一格不许再写「不要点重新配对」—— 实测那时重新配对是唯一出路");
            Assert(!expiredNow.Contains("会自动续签"),
                   "★★★ 【已经过期】这一格不许再许诺「本机会自动续签」—— 续签要用这张证书握手,实测够不着");
            Assert(expiredNow.Contains("只能重新配对"),
                   "★★★ 【已经过期】必须明确指向重新配对(等下去不会自愈)");

            // ══════════════════════════════════════════════════════════════════
            //  ★ 第五种归因:本机配对材料已不可用 —— 处置与「设备证书过期」**正好相反**
            // ══════════════════════════════════════════════════════════════════
            {
                ClientProfile Clone() => JsonSerializer.Deserialize<ClientProfile>(JsonSerializer.Serialize(profile))!;

                // 健康档案:必须**不**触发(反向钉住,免得这条判据恒真)
                Assert(TlsFailure.CheckLocalMaterials(profile) == LocalMaterial.Ok,
                       "★ 反向:健康档案的材料体检结论是 Ok(判据不是恒真的)");
                Assert(TlsFailure.Classify(realShape, profile, t0) != TlsFailureKind.LocalProfileUnusable,
                       "★ 反向:健康档案**不会**被判成材料不可用");
                Assert(TlsFailure.CheckLocalMaterials(null) == LocalMaterial.Ok,
                       "★ 没有档案 = 从未配对,不是'材料坏了'(那是另一件事)");

                var truncated = Clone();
                truncated.CaCertB64 = Convert.ToBase64String(Convert.FromBase64String(profile.CaCertB64)[..20]);
                Assert(TlsFailure.CheckLocalMaterials(truncated) == LocalMaterial.CaCertUnreadable,
                       "★ CA 证书被截断 -> CaCertUnreadable");

                var notB64 = Clone(); notB64.CaCertB64 = "这不是 base64!!!";
                Assert(TlsFailure.CheckLocalMaterials(notB64) == LocalMaterial.CaCertUnreadable,
                       "★ CA 证书不是合法 base64 -> CaCertUnreadable(FormatException 也算材料坏)");

                var badDev = Clone();
                badDev.DeviceCertB64 = Convert.ToBase64String(Convert.FromBase64String(profile.DeviceCertB64)[..20]);
                Assert(TlsFailure.CheckLocalMaterials(badDev) == LocalMaterial.DeviceCertUnreadable,
                       "★ 设备证书被截断 -> DeviceCertUnreadable");

                var noKey = Clone(); noKey.KeyName = "localai-does-not-exist-" + Guid.NewGuid().ToString("N")[..8];
                Assert(TlsFailure.CheckLocalMaterials(noKey) == LocalMaterial.PrivateKeyMissing,
                       "★★ 私钥不在了 -> PrivateKeyMissing(重装系统 / 换 Windows 用户 / 拷贝 profile.json)");

                // ★★★ 这三种此前全都掉进 Offline =「中枢没开机」,而真正的出路是重新配对
                foreach (var (name, prof) in new[] { ("CA 损坏", truncated), ("设备证书损坏", badDev), ("私钥不在", noKey) })
                    Assert(TlsFailure.Classify(realShape, prof, t0) == TlsFailureKind.LocalProfileUnusable,
                           $"★★★ 【{name}】-> LocalProfileUnusable(此前掉进 Offline =「中枢没开机」,"
                           + "用户会一直去重启一个没病的中枢)");

                // ★★ 设备证书**损坏**必须判成材料不可用,而不是"过期" ——
                //    读不出来就读不出 NotAfter,①那一步会静默跳过,这正是它必须排在①之前的原因。
                Assert(TlsFailure.Classify(realShape, badDev, t0) != TlsFailureKind.LocalDeviceCertExpired,
                       "★★ 设备证书**读不出来**不等于「过期」—— 两者处置相反,不许合并");

                // ★★★ 两种"本机问题"的建议**正好相反**,必须逐字钉住 ——
                //    搞反的代价:要么白等一个永远不会自愈的档案,要么删掉一个只需续签的身份。
                var expiredText = TlsFailure.Explain(TlsFailureKind.LocalDeviceCertExpired);
                var unusableText = TlsFailure.Explain(TlsFailureKind.LocalProfileUnusable);
                // ★★★ 更正后两者的**动作**都是重新配对(实测:过期之后续签够不着),
                //   但**成因与代价**不同,必须还能分辨 —— 否则等于把两态合并,
                //   而 D89 §1.6 真正要保住的是"用户知道自己损失了什么、以及本来能不能避免"。
                Assert(unusableText.Contains("只能重新配对"),
                       "★★★ 【材料不可用】明确指向重新配对(已经没有可被毁掉的东西了)");
                Assert(expiredText.Contains("过期") && !expiredText.Contains("私钥已经不在"),
                       "★★★ 【设备证书过期】说的是过期,不是'私钥没了' —— 成因不同,用户要能对上自己刚做过什么");
                Assert(unusableText.Contains("没有可用的私钥或档案"),
                       "★★★ 【材料不可用】要点明本机已经没有可用的私钥/档案(所以重配不毁掉任何还有用的东西)");
                Assert(expiredText != unusableText, "★ 两种本机问题的文案不同");
                Assert(TlsFailure.ExplainLocal(LocalMaterial.PrivateKeyMissing).Contains("不可导出"),
                       "★ 私钥丢失的说法要点出'它按设计拷不过来也找不回来',否则用户会一直找");
                Assert(new[] { LocalMaterial.CaCertUnreadable, LocalMaterial.DeviceCertUnreadable, LocalMaterial.PrivateKeyMissing }
                       .Select(TlsFailure.ExplainLocal).Distinct().Count() == 3,
                       "★ 三种坏法的说法互不相同(成因不同,用户要能对上自己刚做过什么)");
            }

            // ★ 过期【之前】就看得见(不必等握手失败)
            Assert(TlsFailure.LocalCertPhase(profile, t0) == CertPhase.Healthy, "刚配对:本机证书 Healthy");
            Assert(TlsFailure.LocalCertPhase(profile, t0.AddDays(85)) == CertPhase.Critical,
                   "★★ 到期前 5 天:Critical —— **握手还好着的时候**就看得见,不是失败之后才归因");
            Assert(TlsFailure.LocalCertPhase(profile, t0.AddDays(65)) == CertPhase.RenewDue,
                   "★ 到期前 25 天:RenewDue(自动续签动手,但**不打扰用户**)");

            // ══════════════════════════════════════════════════════════════════
            //  ★★ WarnLocalCert:提醒必须**跟着相位走**,建议才不会说反
            // ══════════════════════════════════════════════════════════════════
            Assert(TlsFailure.WarnLocalCert(profile, t0) is null, "★ Healthy 段:不打扰用户");
            Assert(TlsFailure.WarnLocalCert(profile, t0.AddDays(65)) is null,
                   "★★ RenewDue 段:**不出声** —— 系统正在正常自愈,正常运转也报警的告警两周内就会被学会忽略");
            var warnCrit = TlsFailure.WarnLocalCert(profile, t0.AddDays(85));
            Assert(warnCrit is not null && warnCrit.Contains("不要点"),
                   "★★★ Critical 段(还没过期、私钥还在、续签还通)才是「不要点重新配对」该出现的地方");
            Assert(warnCrit is not null && warnCrit.Contains("过期【之前】"),
                   "★★★ Critical 段要给出**截止期限**:过期之后就只剩重新配对 —— 不说期限,提醒就只是噪音");
            var warnExp = TlsFailure.WarnLocalCert(profile, t0.AddDays(95));
            Assert(warnExp == TlsFailure.Explain(TlsFailureKind.LocalDeviceCertExpired),
                   "★★★ 已过期时,提醒与归因文案**是同一句** —— 两处说法不一致会让人以为还有别的路");
            Assert(warnExp is not null && !warnExp.Contains("不要点"),
                   "★★★ 已过期时**不许**再劝阻重新配对(承接上面那条更正:那时它是唯一出路)");
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

                // ══════════════════════════════════════════════════════════
                //  ★★★ D92 成对断言的**客户端半边**(续签两条路由)
                // ══════════════════════════════════════════════════════════
                //  ★ 这里钉的**不只是**"我这个测试服务器发了什么" —— 那样只测了自己。
                //    本套件的测试 Edge 是**同一个文件里的桩**,它与真的 lan-edge 是两份实现;
                //    桩漂了而真服务端没漂(或反过来),这个套件照样全绿,而生产是坏的。
                //    ⇒ 两边都对着 WireContracts 这**同一份**登记表核对:
                //      lan-edge 丙 节钉真服务端,这里钉桩 —— 期望值只有一份,没法跟自己分家。
                var eKeys = ed.EnumerateObject().Select(x => x.Name).ToArray();
                Assert(WireContracts.KeysMatch(eKeys, WireContracts.RenewEnroll),
                       "★★★ CONTRACT:cert.renew.enroll 客户端半边:测试 Edge 的 renew/enroll 形状 == 登记表"
                       + "(与真 lan-edge 同一份)(" + WireContracts.Describe(eKeys, WireContracts.RenewEnroll) + ")");
                // ★ 而且这个形状**真的解得出**客户端要用的那两个字段 —— 这是"能解析"那一半。
                Assert(ed.TryGetProperty("candidateCert", out var _cc) && _cc.GetString()!.Length > 0
                       && ed.TryGetProperty("renewalId", out var _ri) && _ri.GetString()!.Length > 0,
                       "★★★ CONTRACT:cert.renew.enroll:拿这个形状解得出 candidateCert 与 renewalId(A1 死的就是这一步)");

                crashed.PendingDeviceCertB64 = ed.GetProperty("candidateCert").GetString()!;
                crashed.PendingRenewalId = ed.GetProperty("renewalId").GetString()!;
                File.WriteAllText(Path.Combine(stateDir, "profile.json"), JsonSerializer.Serialize(crashed));
                // ★ 此刻档案里两张都在:旧的还 active、新的挂在 Pending 上 —— 这正是崩溃现场的样子
                Assert((await Transport.Call(crashed, dial, "/v1/models")).status == 200,
                       "★★ 崩在'签出候选、未确认'之间:**旧证书仍然可用**(设备没有掉线)");

                var renewalIdV4 = crashed.PendingRenewalId;   // 下面钉 complete 的形状要用(重入之后它会被清空)
                var outcome = await Transport.RenewDeviceCertIfDue(crashed, dial, stateDir, DateTimeOffset.UtcNow);
                Assert(outcome == Transport.RenewOutcome.ResumedAndCompleted,
                       "★★★ 重入:发现 Pending 就**把上一次做完**,而不是重新签一张(否则每崩一次多一行 candidate)");
                Assert(crashed.PendingDeviceCertB64.Length == 0, "★ 重入完成后 Pending 清空");
                Assert((await Transport.Call(crashed, dial, "/v1/models")).status == 200, "★ 重入完成后新证书可用");

                // ── CONTRACT:cert.renew.complete —— 欠债表点名的**单字段冒充成对** ──
                //  lan-edge 那边原来只钉了 `changed` 一个字段。单字段挡不住别的键漂移:
                //  `ok` 改名的话客户端照样看不出来,而它是"这次切换到底成没成"的唯一信号。
                //  ★ 这里用**幂等重试**再打一次同一个 renewalId(complete 设计上就是幂等的),
                //    既拿到了应答形状,又不改变上面那次重入的结果。
                {
                    var hRC = new SocketsHttpHandler { UseProxy = false };
                    hRC.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                    using var devCert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(crashed.DeviceCertB64));
                    using var devKey = new ECDsaCng(CngKey.Open(crashed.KeyName, new CngProvider(Ca.TlsKeyProvider)));
                    using var devWithKey = devCert.CopyWithPrivateKey(devKey);
                    hRC.SslOptions.ClientCertificates = new X509CertificateCollection { devWithKey };
                    using var mtRC = new HttpClient(hRC);
                    using var rcResp = await mtRC.PostAsync(
                        $"https://127.0.0.1:{PORT}/identity/renew/complete?renewalId=" + renewalIdV4, new StringContent(""));
                    var rcJson = JsonDocument.Parse(await rcResp.Content.ReadAsStringAsync()).RootElement;
                    var rcKeys = rcJson.EnumerateObject().Select(x => x.Name).ToArray();
                    Assert(WireContracts.KeysMatch(rcKeys, WireContracts.RenewComplete),
                           "★★★ CONTRACT:cert.renew.complete 客户端半边:顶层键 == 登记表("
                           + WireContracts.Describe(rcKeys, WireContracts.RenewComplete) + ")"
                           + " —— 此前只钉了 `changed` 一个字段,`ok` 改名照样全绿");
                    Assert(rcJson.GetProperty("ok").GetBoolean() && rcJson.GetProperty("changed").GetBoolean() == false,
                           "★★ CONTRACT:cert.renew.complete:两个字段都解得出,且幂等重试如实报 changed=false");
                }
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
        foreach (var k in keysToDelete) Ca.DeleteKey(k);
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
