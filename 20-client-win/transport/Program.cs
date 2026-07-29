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
        return Results.Json(new { requestId = en.RequestId, serverNonce = Convert.ToBase64String(en.ServerNonce), sas = en.Sas, caCert = Convert.ToBase64String(caPublic.RawData), hubId = pairing.HubId });
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
    app.MapFallback((HttpContext ctx) =>
    {
        var cert = ctx.Connection.ClientCertificate;
        if (cert is null) return Results.StatusCode(401);
        return Store.LoadOrEmpty(idDir).IsActive(Convert.ToHexString(SHA256.HashData(cert.RawData))) ? Results.Text("ok") : Results.StatusCode(401);
    });
    return app;
}
