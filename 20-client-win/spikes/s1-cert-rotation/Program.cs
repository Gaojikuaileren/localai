// P3b S1 / Spike 8 -- ServerCertificateSelector hot-swap of the server leaf, loopback only.
// LocalAI, decision D43 (S1). Proves packet §6.2: under the SAME CA + SAN, a new server leaf can
// be swapped in for NEW connections without re-pairing, because the client trusts the CA + name,
// not a pinned leaf. New connections see the new leaf; both validate.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using LocalAI.Spikes;
using Microsoft.AspNetCore.Server.Kestrel.Https;

const int PORT = 18446;
const string SERVER_SAN = "localai-test.local";

int pass = 0, fail = 0;
void Assert(bool cond, string msg)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + msg); }
    else { fail++; Console.WriteLine("  FAIL  " + msg); }
}

using var ca = SpikeTls.CreateCa("LocalAI Test CA");
var caPublic = SpikeTls.PublicOf(ca);
// two server leaves under the SAME CA + SAN, different keys/serials
var certA = SpikeTls.CreateLeaf(ca, SERVER_SAN + " A", SERVER_SAN, serverAuth: true, clientAuth: false);
var certB = SpikeTls.CreateLeaf(ca, SERVER_SAN + " B", SERVER_SAN, serverAuth: true, clientAuth: false);
var clientCert = SpikeTls.CreateLeaf(ca, "device-A", null, serverAuth: false, clientAuth: true);

// the active leaf; ServerCertificateSelector reads it atomically per new connection
X509Certificate2 active = certA;

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(IPAddress.Loopback, PORT, lo => lo.UseHttps(h =>
    {
        h.ServerCertificateSelector = (_, _) => Volatile.Read(ref active);
        h.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        h.ClientCertificateValidation = SpikeTls.ClientValidator(caPublic);
    }));
});

var app = builder.Build();
app.MapGet("/api/ping", (HttpContext ctx) =>
    ctx.Connection.ClientCertificate is null ? Results.StatusCode(401) : Results.Text("ok"));

await app.StartAsync();
Console.WriteLine("kestrel up on 127.0.0.1:" + PORT + " (SAN=" + SERVER_SAN + ")");

// client that captures the server leaf thumbprint it actually receives on the wire, while doing
// its own custom-root + hostname validation (can't combine with CertificateChainPolicy).
async Task<(int status, string? thumb)> Call()
{
    string? thumb = null;
    var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
    handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCert };
    handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
    {
        using var c = new X509Certificate2(cert!);
        thumb = c.Thumbprint;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.CustomTrustStore.Add(caPublic);
        var chainOk = chain.Build(c);
        var nameOk = (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0;
        return chainOk && nameOk;
    };
    handler.ConnectCallback = async (_, ct) =>
    {
        var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await s.ConnectAsync(IPAddress.Loopback, PORT, ct);
        return new NetworkStream(s, ownsSocket: true);
    };
    using var client = new HttpClient(handler);
    using var r = await client.GetAsync($"https://{SERVER_SAN}:{PORT}/api/ping");
    return ((int)r.StatusCode, thumb);
}

try
{
    var r1 = await Call();
    Assert(r1.status == 200 && r1.thumb == certA.Thumbprint, "before swap: new connection served leaf A + 200");

    // hot swap the active leaf (new connections only)
    Volatile.Write(ref active, certB);

    var r2 = await Call();
    Assert(r2.status == 200 && r2.thumb == certB.Thumbprint, "after swap: new connection served leaf B + 200");

    Assert(certA.Thumbprint != certB.Thumbprint, "the two leaves are genuinely different");
    Assert(r1.thumb != r2.thumb, "client observed the leaf change on a new connection (no re-pairing)");
}
finally
{
    await app.StopAsync();
    SpikeTls.DeleteKey(certA);
    SpikeTls.DeleteKey(certB);
    SpikeTls.DeleteKey(clientCert);
}

Console.WriteLine();
Console.WriteLine($"S1-Spike8 result: PASS={pass} FAIL={fail}");
Environment.Exit(fail > 0 ? 1 : 0);
