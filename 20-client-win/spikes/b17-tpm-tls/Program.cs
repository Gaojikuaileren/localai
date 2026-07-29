// B17 spike -- can a TPM-backed key be used as a Kestrel TLS *server* credential?
// S4 found CngKey.Create(TPM) + CopyWithPrivateKey -> "unexpected EOF". This tries variants to find
// a working recipe (so D35/D43 "keys in TPM" can be honored for mTLS), else confirms it's infeasible.
//
// Each variant: make a TPM key, build a self-signed server cert, start Kestrel loopback HTTPS, and
// try one client GET (server cert accepted blindly -- we only care whether the HANDSHAKE completes).

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;

const string TpmProv = "Microsoft Platform Crypto Provider";
const string SAN = "b17-test.local";
int port = 18500;
var prov = new CngProvider(TpmProv);

// thumbprint mode: load a cert already in CurrentUser\My (e.g. made by New-SelfSignedCertificate with
// the Platform Crypto Provider) and test whether Kestrel can serve TLS with it.
if (args.Length > 0)
{
    using var st = new X509Store(StoreName.My, StoreLocation.CurrentUser); st.Open(OpenFlags.ReadOnly);
    var f = st.Certificates.Find(X509FindType.FindByThumbprint, args[0], false);
    if (f.Count == 0) { Console.WriteLine("cert not found: " + args[0]); return; }
    Console.WriteLine("PCP New-SelfSignedCertificate -> " + await TryServe(f[0]));
    return;
}

async Task<string> TryServe(X509Certificate2 serverCert)
{
    var b = WebApplication.CreateBuilder();
    b.Logging.ClearProviders();
    b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port, lo => lo.UseHttps(h => h.ServerCertificate = serverCert)));
    var app = b.Build();
    app.MapGet("/", () => "ok");
    await app.StartAsync();
    try
    {
        var handler = new SocketsHttpHandler { UseProxy = false };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        handler.ConnectCallback = async (_, ct) => { var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true }; await s.ConnectAsync(IPAddress.Loopback, port, ct); return new NetworkStream(s, true); };
        using var client = new HttpClient(handler);
        using var r = await client.GetAsync($"https://{SAN}:{port}/");
        return "OK " + (int)r.StatusCode;
    }
    catch (Exception ex) { return "FAIL " + ex.GetType().Name + (ex.InnerException is { } i ? " / " + i.GetType().Name + ": " + i.Message : ": " + ex.Message); }
    finally { await app.StopAsync(); port++; }
}

X509Certificate2 SelfSigned(CngKey key)
{
    using var ec = new ECDsaCng(key);
    var req = new CertificateRequest("CN=" + SAN, ec, HashAlgorithmName.SHA256);
    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
    req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
    var eku = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));
    var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(SAN);
    req.CertificateExtensions.Add(san.Build());
    return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
}

CngKey MakeKey(string name, CngKeyUsages usage)
{
    if (CngKey.Exists(name, prov)) CngKey.Open(name, prov).Delete();
    return CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters { Provider = prov, ExportPolicy = CngExportPolicies.None, KeyUsage = usage });
}

X509Certificate2 ViaStore(X509Certificate2 c)
{
    using var s = new X509Store(StoreName.My, StoreLocation.CurrentUser); s.Open(OpenFlags.ReadWrite);
    s.Add(c);
    return s.Certificates.Find(X509FindType.FindByThumbprint, c.Thumbprint, false)[0];
}
void StoreRemove(string thumb) { try { using var s = new X509Store(StoreName.My, StoreLocation.CurrentUser); s.Open(OpenFlags.ReadWrite); foreach (var c in s.Certificates.Find(X509FindType.FindByThumbprint, thumb, false)) s.Remove(c); } catch { } }

var variants = new (string name, CngKeyUsages usage, bool store)[]
{
    ("AllUsages + store round-trip", CngKeyUsages.AllUsages, true),
    ("AllUsages + direct CopyWithPrivateKey", CngKeyUsages.AllUsages, false),
    ("Signing + store round-trip", CngKeyUsages.Signing, true),
};

Console.WriteLine("B17 · TPM key as Kestrel TLS server credential:\n");
foreach (var (name, usage, store) in variants)
{
    string kn = "b17-tpm-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
    CngKey? key = null; string? thumb = null;
    try
    {
        key = MakeKey(kn, usage);
        var selfSigned = SelfSigned(key);
        thumb = selfSigned.Thumbprint;
        var serverCert = store ? ViaStore(selfSigned) : selfSigned;
        var result = await TryServe(serverCert);
        Console.WriteLine($"  [{result[..2].TrimEnd()}]  {name}\n        -> {result}");
    }
    catch (Exception ex) { Console.WriteLine($"  [ERR] {name} -> {ex.GetType().Name}: {ex.Message}"); }
    finally { if (store && thumb is not null) StoreRemove(thumb); if (key is not null) { try { key.Delete(); } catch { } } }
}
