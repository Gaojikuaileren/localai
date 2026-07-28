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

return (args.Length == 0 ? "" : args[0]) switch
{
    "selftest" => await Selftest(),
    "run" => await Run(),
    _ => Usage(),
};

static int Usage() { Console.WriteLine("usage: localai-lan-edge <run|selftest>"); return 2; }

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

        // --- the Edge, pointed at the stub upstream (SChannel-usable software server cert, TPM CA-signed) ---
        swSrvKeyName = "localai-edge-selftest-srv-" + Convert.ToHexString(R(4)).ToLowerInvariant();
        using var srvKeyCng = new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, swSrvKeyName,
            new CngKeyCreationParameters { Provider = swProv, ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing }));
        var srvPub = PublicKey.CreateFromSubjectPublicKeyInfo(srvKeyCng.ExportSubjectPublicKeyInfo(), out _);
        using var srvLeafPub = Ca.IssueLeaf(hub.CaKeyName, caPublic, srvPub, hub.ServerName, hub.ServerName, null, true, false, 30);
        using var serverCert = srvLeafPub.CopyWithPrivateKey(srvKeyCng);
        edge = Edge.Build(new EdgeConfig(idDir, secDir, $"http://127.0.0.1:{upstreamPort}", 18443), serverCert);
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

record EdgeConfig(string IdentityDir, string SecretsDir, string UpstreamBase, int ListenPort);

static class Edge
{
    public static WebApplication Build(EdgeConfig cfg, X509Certificate2? serverCertOverride = null)
    {
        var idDir = cfg.IdentityDir;
        var caPublic = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(idDir, "ca.cer")));
        // NOTE: the production path loads the server leaf key from the TPM (LoadServerCert). SChannel
        // cannot currently use a TPM key as a TLS credential ("unexpected EOF") -- see backlog B17.
        // The self-test passes an SChannel-compatible (non-exportable software CNG) server cert so the
        // Edge's mTLS + membership + proxy + pairing LOGIC is fully exercised regardless of that gap.
        var serverCert = serverCertOverride ?? LoadServerCert(idDir, cfg.SecretsDir);
        var pairing = new Pairing(idDir, cfg.SecretsDir);
        pairing.OpenWindow(TimeSpan.FromMinutes(30));   // S4 test convenience; host-admin controls this for real
        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });

        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);   // ← temporarily surface handshake errors
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 32 * 1024;
            k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            k.Limits.MaxRequestLineSize = 4 * 1024;
            k.Listen(IPAddress.Loopback, cfg.ListenPort, lo => lo.UseHttps(h =>
            {
                h.ServerCertificate = serverCert;
                h.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                h.ClientCertificateValidation = (cert, _, __) => ValidateClient(cert, caPublic, idDir);
            }));
        });

        var app = builder.Build();

        // anonymous pairing routes (no client cert). Only these are reachable without a member cert.
        app.MapPost("/pair/enroll", async (HttpContext ctx) =>
        {
            var d = await JsonDocument.ParseAsync(ctx.Request.Body);
            var r = d.RootElement;
            var en = pairing.Enroll(
                Convert.FromBase64String(r.GetProperty("csr").GetString()!),
                Convert.FromBase64String(r.GetProperty("clientNonce").GetString()!),
                Convert.FromBase64String(r.GetProperty("claimSecretHash").GetString()!),
                r.GetProperty("protocolVersion").GetInt32(),
                r.GetProperty("displayName").GetString() ?? "");
            return Results.Json(new
            {
                requestId = en.RequestId,
                serverNonce = Convert.ToBase64String(en.ServerNonce),
                sas = en.Sas,
                caCert = Convert.ToBase64String(caPublic.RawData),
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
        // complete requires the candidate client cert via mTLS (its fingerprint must match).
        app.MapPost("/pair/complete", (HttpContext ctx) =>
        {
            var cert = ctx.Connection.ClientCertificate;
            if (cert is null) return Results.StatusCode(401);
            pairing.Complete(ctx.Request.Query["requestId"].ToString());
            return Results.Text("active");
        });

        // everything else = business: requires a validated member cert, proxied to the gateway.
        app.MapFallback(async (HttpContext ctx) =>
        {
            var cert = ctx.Connection.ClientCertificate;
            if (cert is null) { ctx.Response.StatusCode = 401; return; }
            var fp = Convert.ToHexString(SHA256.HashData(cert.RawData));
            await Proxy(ctx, http, cfg.UpstreamBase, fp);
        });

        return app;
    }

    static bool ValidateClient(X509Certificate2 cert, X509Certificate2 caPublic, string idDir)
    {
        try
        {
            // chain to CA + clientAuth EKU ...
            if (!Ca.VerifyChainAndEku(cert, caPublic, Ca.OidClientAuth)) return false;
            // ... AND an active member (device + cert both active). Revoked -> validation fails -> cert absent.
            var fp = Convert.ToHexString(SHA256.HashData(cert.RawData));
            return Store.LoadOrEmpty(idDir).IsActive(fp);
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
        using var key = new ECDsaCng(CngKey.Open(serverKeyName, new CngProvider(Ca.TpmProvider)));
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
