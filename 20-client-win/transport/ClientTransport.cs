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
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
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
            en = await Post(boot, edgeUrl + "/pair/enroll", new { csr = Convert.ToBase64String(csr), clientNonce = Convert.ToBase64String(clientNonce), claimSecretHash = Convert.ToBase64String(claimSecretHash), protocolVersion = 1, displayName });
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
        JsonElement st; var deadline = Environment.TickCount64 + 180_000;
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

    public static void DeleteKey(string keyName) { try { if (CngKey.Exists(keyName, SwProv)) CngKey.Open(keyName, SwProv).Delete(); } catch { } }
}
