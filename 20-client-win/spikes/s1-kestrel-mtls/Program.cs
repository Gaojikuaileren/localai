// P3b S1 / Spike 2 -- Kestrel AllowCertificate + custom-root mTLS, loopback only.
// LocalAI, decision D43 (S1). No firewall, binds 127.0.0.1 only.
//
// Proves the server-side mTLS mechanics the design rests on:
//   * business routes require a client cert that chains to OUR CA and carries clientAuth EKU
//   * the pairing route group (/pair/*) is reachable with NO client cert (AllowCertificate)
//   * a client cert from a DIFFERENT CA is rejected (treated as no cert -> business 401)
//   * the client validates the SERVER against our CA + hostname (wrong SAN -> handshake fails)
//   * system proxy / redirects cannot move the destination (UseProxy=false, AllowAutoRedirect=false)
//
// All certs are ephemeral in-memory (TPM-backed client key was proven separately in Spike 1).

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;

const int PORT = 18443;
const string SERVER_SAN = "localai-test.local";
const string OID_SERVER_AUTH = "1.3.6.1.5.5.7.3.1";
const string OID_CLIENT_AUTH = "1.3.6.1.5.5.7.3.2";

int pass = 0, fail = 0;
void Assert(bool cond, string msg)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + msg); }
    else { fail++; Console.WriteLine("  FAIL  " + msg); }
}

// ---- certificate factory -------------------------------------------------
static X509Certificate2 CreateCa(string cn)
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var req = new CertificateRequest("CN=" + cn, key, HashAlgorithmName.SHA256);
    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
    req.CertificateExtensions.Add(new X509KeyUsageExtension(
        X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
    return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
}

static X509Certificate2 CreateLeaf(X509Certificate2 ca, string cn, string? dnsSan,
                                   bool serverAuth, bool clientAuth)
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var req = new CertificateRequest("CN=" + cn, key, HashAlgorithmName.SHA256);
    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
    req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
    var eku = new OidCollection();
    if (serverAuth) eku.Add(new Oid(OID_SERVER_AUTH));
    if (clientAuth) eku.Add(new Oid(OID_CLIENT_AUTH));
    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, true));
    if (dnsSan != null)
    {
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsSan);
        req.CertificateExtensions.Add(san.Build());
    }
    var serial = new byte[16];
    RandomNumberGenerator.Fill(serial);
    using var signed = req.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-5),
                                  DateTimeOffset.UtcNow.AddHours(1), serial);
    var withKey = signed.CopyWithPrivateKey(key);
    // round-trip through PKCS#12 so SslStream (SChannel) can use the key reliably.
    var pfx = withKey.Export(X509ContentType.Pfx);
    // Persisted key set: SChannel (Kestrel server auth) cannot use EphemeralKeySet keys on
    // Windows -> handshake dies with "unexpected EOF". The transient container is deleted in finally.
    return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
}

static void DeleteKey(X509Certificate2 cert)
{
    try
    {
        using var ec = cert.GetECDsaPrivateKey();
        if (ec is ECDsaCng cng) cng.Key.Delete();
    }
    catch { /* best effort cleanup of the transient key container */ }
}

static bool HasEku(X509Certificate2 cert, string oid)
{
    foreach (var ext in cert.Extensions)
        if (ext is X509EnhancedKeyUsageExtension eku)
            foreach (var o in eku.EnhancedKeyUsages)
                if (o.Value == oid) return true;
    return false;
}

using var ca = CreateCa("LocalAI Test CA");
using var rogueCa = CreateCa("Rogue CA");
var caPublic = X509CertificateLoader.LoadCertificate(ca.Export(X509ContentType.Cert));

var serverCert = CreateLeaf(ca, SERVER_SAN, SERVER_SAN, serverAuth: true, clientAuth: false);
var clientCert = CreateLeaf(ca, "device-A", null, serverAuth: false, clientAuth: true);
var rogueClient = CreateLeaf(rogueCa, "attacker", null, serverAuth: false, clientAuth: true);

// ---- server-side client-cert validation: chain to OUR CA + clientAuth EKU ----
bool ValidateClientCert(X509Certificate2 cert, X509Chain? _, SslPolicyErrors __)
{
    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    chain.ChainPolicy.CustomTrustStore.Add(caPublic);
    var built = chain.Build(cert);
    return built && HasEku(cert, OID_CLIENT_AUTH);
}

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(IPAddress.Loopback, PORT, lo =>
    {
        lo.UseHttps(h =>
        {
            h.ServerCertificate = serverCert;
            h.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            h.ClientCertificateValidation = ValidateClientCert;
        });
    });
});

var app = builder.Build();
// pairing route group: reachable WITHOUT a client cert
app.MapGet("/pair/ping", () => Results.Text("pair-ok"));
// business route: requires a validated client cert (present => it passed ValidateClientCert)
app.MapGet("/api/ping", (HttpContext ctx) =>
{
    var c = ctx.Connection.ClientCertificate;
    return c is null ? Results.StatusCode(401) : Results.Text("api-ok:" + c.Subject);
});

await app.StartAsync();
Console.WriteLine("kestrel up on 127.0.0.1:" + PORT + " (SAN=" + SERVER_SAN + ")");

// ---- client factory: custom root trust + dial loopback, keep .local as SNI/host ----
HttpClient MakeClient(X509Certificate2? clientCert, X509Certificate2 trustedCa)
{
    var handler = new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
    };
    handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
    {
        TrustMode = X509ChainTrustMode.CustomRootTrust,
        RevocationMode = X509RevocationMode.NoCheck,
    };
    handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(trustedCa);
    if (clientCert is not null)
        handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCert };
    handler.ConnectCallback = async (ctx, ct) =>
    {
        var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await s.ConnectAsync(IPAddress.Loopback, PORT, ct);
        return new NetworkStream(s, ownsSocket: true);
    };
    return new HttpClient(handler);
}

async Task<(int status, string body)> Call(string host, string path, X509Certificate2? clientCert,
                                           X509Certificate2? trustedCa = null)
{
    using var client = MakeClient(clientCert, trustedCa ?? caPublic);
    var resp = await client.GetAsync($"https://{host}:{PORT}{path}");
    var body = await resp.Content.ReadAsStringAsync();
    return ((int)resp.StatusCode, body);
}

try
{
    // 1. valid client cert -> business route 200
    var r1 = await Call(SERVER_SAN, "/api/ping", clientCert);
    Assert(r1.status == 200 && r1.body.StartsWith("api-ok"), "valid client cert -> /api/ping 200 (" + r1.status + ")");

    // 2a. no client cert -> business route 401
    var r2 = await Call(SERVER_SAN, "/api/ping", null);
    Assert(r2.status == 401, "no client cert -> /api/ping 401 (" + r2.status + ")");

    // 2b. no client cert -> pairing route 200 (AllowCertificate exception)
    var r3 = await Call(SERVER_SAN, "/pair/ping", null);
    Assert(r3.status == 200 && r3.body == "pair-ok", "no client cert -> /pair/ping 200 (" + r3.status + ")");

    // 3. rogue (wrong-CA) client cert -> rejected from business.
    //    Per packet §7.2 a wrong-CA cert fails at the TLS layer (no HTTP status: SChannel returns
    //    SEC_E_UNTRUSTED_ROOT); a cert-less request instead reaches the app and gets 401. Either
    //    way it must NOT reach the business handler.
    bool rogueRejected; string how;
    try
    {
        var r4 = await Call(SERVER_SAN, "/api/ping", rogueClient);
        rogueRejected = r4.status != 200; how = "HTTP " + r4.status;
    }
    catch (Exception ex) when (ex is HttpRequestException or AuthenticationException or IOException)
    {
        rogueRejected = true; how = "TLS handshake rejected";
    }
    Assert(rogueRejected, "wrong-CA client cert -> business rejected (" + how + ")");

    // 4. client rejects wrong server hostname (SAN mismatch) -> handshake throws
    bool threw = false;
    try { await Call("wrong.local", "/pair/ping", null); }
    catch (Exception ex) when (ex is HttpRequestException or AuthenticationException or IOException) { threw = true; }
    Assert(threw, "wrong server SAN (wrong.local) -> client refuses handshake");

    // 5. client rejects server signed by a DIFFERENT CA (trust only rogueCa) -> handshake throws
    bool threw2 = false;
    try
    {
        var rogueCaPublic = X509CertificateLoader.LoadCertificate(rogueCa.Export(X509ContentType.Cert));
        await Call(SERVER_SAN, "/pair/ping", null, trustedCa: rogueCaPublic);
    }
    catch (Exception ex) when (ex is HttpRequestException or AuthenticationException or IOException) { threw2 = true; }
    Assert(threw2, "server not chaining to trusted CA -> client refuses handshake");
}
finally
{
    await app.StopAsync();
    DeleteKey(serverCert);
    DeleteKey(clientCert);
    DeleteKey(rogueClient);
}

Console.WriteLine();
Console.WriteLine($"S1-Spike2 result: PASS={pass} FAIL={fail}");
Environment.Exit(fail > 0 ? 1 : 0);
