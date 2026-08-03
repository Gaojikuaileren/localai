// P3b -- localai-client-transport: the (headless) client the 2nd PC runs to pair with the hub and
// make business calls over mTLS. LocalAI, decision D43/D44 (client key = non-exportable software CNG).
//
// pairing (client side):
//   bootstrap-enroll over TLS accepting the server cert UNVERIFIED (trust comes from the six-word SAS,
//   not the chain) while CAPTURING the server leaf; then build the SAS transcript INDEPENDENTLY from
//   what we received (CA, captured server leaf, our CSR/nonces) and compare with the host's. After the
//   human confirms the six words match, poll status -> claim (challenge signed by our key) -> complete
//   (mTLS with the candidate). Then business calls use CustomRootTrust(received CA) + our device cert.

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LocalAI.Identity;

namespace LocalAI.ClientTransport;

public sealed class ClientProfile
{
    public string EdgeUrl { get; set; } = "";
    public string HubId { get; set; } = "";
    public string KeyName { get; set; } = "";
    public string CaCertB64 { get; set; } = "";
    public string DeviceCertB64 { get; set; } = "";
    // Where to dial the hub ("ip:port"). Persisted so a paired client can reconnect on its own at
    // startup -- pairing happens once, never again (P3c). Empty on profiles written before P3c.
    public string Dial { get; set; } = "";
}

public static class Transport
{
    /// <summary>本客户端的版本戳(由宿主在启动时设一次;不设就是 "unknown")。见客户端的 BuildInfo。</summary>
    public static string ClientVersion { get; set; } = "unknown";

    static CngProvider SwProv => new(Ca.TlsKeyProvider);   // non-exportable software CNG (B17/D44)
    static readonly JsonSerializerOptions J = new() { WriteIndented = true };

    static X509Certificate2 Cert(byte[] der) => X509CertificateLoader.LoadCertificate(der);

    static HttpClient Boot(IPEndPoint dial, Action<byte[]> captureLeaf)
    {
        var h = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        h.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
        {
            if (cert is not null) captureLeaf(cert.GetRawCertData());   // capture; trust is via SAS, not the chain
            return true;
        };
        h.ConnectCallback = async (_, ct) => { var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true }; await s.ConnectAsync(dial, ct); return new NetworkStream(s, true); };
        return new HttpClient(h);
    }

    static HttpClient Trusted(IPEndPoint dial, X509Certificate2 ca, X509Certificate2? clientCert)
    {
        var h = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        h.SslOptions.CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        h.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(ca);
        if (clientCert is not null) h.SslOptions.ClientCertificates = new X509CertificateCollection { clientCert };
        h.ConnectCallback = async (_, ct) => { var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true }; await s.ConnectAsync(dial, ct); return new NetworkStream(s, true); };
        return new HttpClient(h);
    }

    static async Task<JsonElement> Post(HttpClient c, string url, object body)
    {
        using var r = await c.PostAsync(url, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        var s = await r.Content.ReadAsStringAsync();
        // ★ 非 2xx 时把服务端的 error 原样带出来(2026-07-31 审计):配对窗口默认关闭(D48),
        //   第二台 PC 第一次点"开始配对"几乎必然撞上 403 {"error":"pairing window is closed"};
        //   原来直接 JsonDocument.Parse 一个没有 requestId 的错误体 → 下游拿不到字段,以一句无意义的
        //   KeyNotFound/格式异常收场。现在抛出带原因的异常,让界面能如实说"请先在主机上 open 配对窗口"。
        if (!r.IsSuccessStatusCode)
        {
            var msg = s;
            try { if (JsonDocument.Parse(s).RootElement.TryGetProperty("error", out var e)) msg = e.GetString() ?? s; } catch { }
            throw new InvalidOperationException($"{(int)r.StatusCode} {msg}");
        }
        return JsonDocument.Parse(s).RootElement;
    }

    // onSas(reqId, sixWords): the caller shows the words and returns once the host has approved.
    public static async Task<ClientProfile> Pair(string edgeUrl, IPEndPoint dial, string stateDir, string displayName, Func<string, string[], Task> onSas)
    {
        Directory.CreateDirectory(stateDir);
        // 重新配对前先清掉上一次的私钥:每次配对都新建随机 KeyName,不清理的话每点一次
        // 「重新配对」就在 CNG 里多留一把无人引用、却仍可签名的孤儿密钥(审查发现 [10])。
        try
        {
            var old = Path.Combine(stateDir, "profile.json");
            if (File.Exists(old) &&
                JsonSerializer.Deserialize<ClientProfile>(File.ReadAllText(old)) is { KeyName.Length: > 0 } prev)
                DeleteKey(prev.KeyName);
        }
        catch { /* 旧档案损坏不该挡住重新配对 */ }

        var keyName = "localai-client-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
        using var ecdsa = new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, keyName,
            new CngKeyCreationParameters { Provider = SwProv, ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing }));
        var csr = new CertificateRequest("CN=client", ecdsa, HashAlgorithmName.SHA256).CreateSigningRequest();
        var clientCsrSpkiSha = SHA256.HashData(ecdsa.ExportSubjectPublicKeyInfo());
        var claimSecret = RandomNumberGenerator.GetBytes(32);
        var claimSecretHash = SHA256.HashData(claimSecret);
        var clientNonce = RandomNumberGenerator.GetBytes(32);

        // bootstrap enroll (capture server leaf, unverified)
        byte[]? serverLeaf = null;
        JsonElement en;
        using (var boot = Boot(dial, d => serverLeaf = d))
            // ★ clientVersion 是【自报的、未被六词覆盖的】信息 —— 与 displayName 同级:只作显示,
            //   不做任何判断,永不进 prompt。服务端忽略不认识的字段,所以多带这一个不会影响老主机。
            //
            // ★★ protocolVersion 同样是【自报】的 —— 别把它误读成"被六个词拦住了"。
            //   它确实进了 SAS transcript(Sas.cs 的 `protocol_version` 一项),但**两边用的是同一个值**:
            //   主机端 `Pairing.Enroll` 直接拿请求体里这个数去推导,自己不校验。
            //   ∴ 客户端协议版本变了也不会让六个词对不上 —— 六词拦的是中间人
            //   (hub_id / CA / 叶子证书 / 双方随机数),不是版本。
            //   真正在查协议版本的是**连上之后**那一步:HubClient.NoteProtocol 拿
            //   `X-LocalAI-Protocol` 响应头比对,不一致就置 ProtocolMismatch、不当在线。
            //   让**配对那一刻**也 fail-closed(主机拒接不支持的 protoVer)属于 core 车道,
            //   已写入 00-docs/decision-packets/client-version-visibility-2026-08-03.md。
            en = await Post(boot, edgeUrl + "/pair/enroll", new { csr = Convert.ToBase64String(csr), clientNonce = Convert.ToBase64String(clientNonce), claimSecretHash = Convert.ToBase64String(claimSecretHash), protocolVersion = 1, displayName, clientVersion = ClientVersion });
        if (serverLeaf is null) throw new InvalidOperationException("did not capture server leaf");

        var reqId = en.GetProperty("requestId").GetString()!;
        var serverNonce = Convert.FromBase64String(en.GetProperty("serverNonce").GetString()!);
        var hostSas = en.GetProperty("sas").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var caDer = Convert.FromBase64String(en.GetProperty("caCert").GetString()!);
        var hubId = en.GetProperty("hubId").GetString()!;
        var caPublic = Cert(caDer);

        // build the SAS transcript INDEPENDENTLY and compare (MITM-swapped CA/leaf/nonce -> mismatch)
        var t = new PairTranscript(1, hubId, SHA256.HashData(caDer), SHA256.HashData(caPublic.PublicKey.ExportSubjectPublicKeyInfo()),
                                   SHA256.HashData(serverLeaf), clientCsrSpkiSha, claimSecretHash, clientNonce, serverNonce, Convert.FromHexString(reqId));
        var sas = Sas.Derive(t).words;
        if (!sas.SequenceEqual(hostSas)) throw new InvalidOperationException("SAS mismatch -- possible MITM; aborting pairing");

        await onSas(reqId, sas);   // human compares the six words; host approves out of band

        using var cli = Trusted(dial, caPublic, null);
        // 3 min: enough for the operator to compare the six words across two screens and type `approve`.
        // ★★ 这里原来是 180 秒,而主机侧 Pairing 的 ExpiresAt 是 **5 分钟**(identity/Pairing.cs)。
        //   两个截止时间各说各话的后果实测会发生:界面要求人"走到那台电脑前把六个词逐字对一遍",
        //   操作员在第 3~5 分钟之间回来点批准 —— 主机 200 成功、建了设备记录和候选证书,
        //   而副机早已抛 TimeoutException 退出、永远不会去 claim ⇒ 主机的设备列表里多一条
        //   provisioning 幽灵,两台机器对同一件事的说法相反。
        //   ⇒ 对齐到主机侧的过期时间(略多一点点,让主机先判过期,由它给出权威结论)。
        const long ApprovalWaitMs = 5 * 60_000 + 10_000;
        JsonElement st; var deadline = Environment.TickCount64 + ApprovalWaitMs;
        while (true)
        {
            st = await Post(cli, edgeUrl + "/pair/status", new { requestId = reqId, claimSecret = Convert.ToBase64String(claimSecret) });
            var s = st.GetProperty("status").GetString();
            if (s is "approved" or "certificate_issued") break;
            if (s is "denied" or "expired") throw new InvalidOperationException("pairing " + s);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("approval timed out");
            await Task.Delay(400);
        }
        var claimNonce = Convert.FromBase64String(st.GetProperty("claimNonce").GetString()!);
        var candSha = st.GetProperty("candidateSha256").GetString()!;

        var challenge = Pairing.BuildChallenge(Convert.FromHexString(reqId), claimNonce, Convert.FromHexString(candSha));
        var cl = await Post(cli, edgeUrl + "/pair/claim", new { requestId = reqId, claimSecret = Convert.ToBase64String(claimSecret), challengeSig = Convert.ToBase64String(ecdsa.SignData(challenge, HashAlgorithmName.SHA256)) });
        var candidate = Cert(Convert.FromBase64String(cl.GetProperty("candidateCert").GetString()!));
        if (!Ca.VerifyChainAndEku(candidate, caPublic, Ca.OidClientAuth)) throw new InvalidOperationException("candidate cert does not chain to CA");

        using (var clientCert = candidate.CopyWithPrivateKey(ecdsa))
        using (var mtls = Trusted(dial, caPublic, clientCert))
        using (var rc = await mtls.PostAsync(edgeUrl + "/pair/complete?requestId=" + reqId, new StringContent("")))
            if ((int)rc.StatusCode != 200) throw new InvalidOperationException("complete failed: " + (int)rc.StatusCode);

        var profile = new ClientProfile { EdgeUrl = edgeUrl, HubId = hubId, KeyName = keyName, CaCertB64 = Convert.ToBase64String(caDer), DeviceCertB64 = Convert.ToBase64String(candidate.RawData), Dial = dial.ToString() };
        File.WriteAllText(Path.Combine(stateDir, "profile.json"), JsonSerializer.Serialize(profile, J));
        return profile;
    }

    public static Task<(int status, string body)> Call(ClientProfile p, IPEndPoint dial, string path)
        => Send(p, dial, HttpMethod.Get, path, null);

    /// <summary>
    /// 用已保存的设备证书发任意 mTLS 请求(P3c:管理 API 的批准/解除是 POST,会话结束也是 POST)。
    /// 不抛 HTTP 状态异常 —— 401/403 是有意义的业务答复(已被解除 / 权限不足),交给调用方判读。
    /// </summary>
    public static async Task<(int status, string body)> Send(ClientProfile p, IPEndPoint dial,
        HttpMethod method, string path, object? body, CancellationToken ct = default)
    {
        var caPublic = Cert(Convert.FromBase64String(p.CaCertB64));
        var candidate = Cert(Convert.FromBase64String(p.DeviceCertB64));
        using var key = new ECDsaCng(CngKey.Open(p.KeyName, SwProv));
        using var clientCert = candidate.CopyWithPrivateKey(key);
        using var cli = Trusted(dial, caPublic, clientCert);
        using var req = new HttpRequestMessage(method, p.EdgeUrl + path);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var r = await cli.SendAsync(req, ct);
        return ((int)r.StatusCode, await r.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// 同 Send,但把【响应头】也带回来 —— 协议版本协商要读 X-LocalAI-Protocol(D45 / P3c 判据项)。
    /// ★ 另开一个重载而不是改 Send 的返回类型:改了所有调用方都得跟着改,
    ///   而它们大多数根本不关心头。
    /// </summary>
    public static async Task<(int status, string body, Dictionary<string, string> headers)> SendWithHeaders(
        ClientProfile p, IPEndPoint dial, HttpMethod method, string path, object? body, CancellationToken ct = default)
    {
        var caPublic = Cert(Convert.FromBase64String(p.CaCertB64));
        var candidate = Cert(Convert.FromBase64String(p.DeviceCertB64));
        using var key = new ECDsaCng(CngKey.Open(p.KeyName, SwProv));
        using var clientCert = candidate.CopyWithPrivateKey(key);
        using var cli = Trusted(dial, caPublic, clientCert);
        using var req = new HttpRequestMessage(method, p.EdgeUrl + path);
        // 本机也报自己的版本:中枢将来想按版本分流时不必再猜
        req.Headers.TryAddWithoutValidation("X-LocalAI-Protocol", "1");
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var r = await cli.SendAsync(req, ct);
        var hs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in r.Headers) hs[h.Key] = string.Join(",", h.Value);
        return ((int)r.StatusCode, await r.Content.ReadAsStringAsync(ct), hs);
    }

    public static void DeleteKey(string keyName) { try { if (CngKey.Exists(keyName, SwProv)) CngKey.Open(keyName, SwProv).Delete(); } catch { } }
}
