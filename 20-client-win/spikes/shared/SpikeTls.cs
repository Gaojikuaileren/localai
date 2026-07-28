// Shared TLS/mTLS helpers for P3b S1 loopback spikes (LocalAI, D43 S1).
// Ephemeral in-memory certs; the TPM-backed client key was proven in Spike 1.
// Linked into each spike project via <Compile Include="..\shared\SpikeTls.cs" />.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LocalAI.Spikes;

public static class SpikeTls
{
    public const string OidServerAuth = "1.3.6.1.5.5.7.3.1";
    public const string OidClientAuth = "1.3.6.1.5.5.7.3.2";

    public static X509Certificate2 CreateCa(string cn)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=" + cn, key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    }

    // serverAuth/clientAuth pick the EKU. Key is persisted (Exportable) because SChannel cannot use
    // EphemeralKeySet server keys on Windows; call DeleteKey in a finally to remove the container.
    public static X509Certificate2 CreateLeaf(X509Certificate2 ca, string cn, string? dnsSan,
                                              bool serverAuth, bool clientAuth,
                                              DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=" + cn, key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var eku = new OidCollection();
        if (serverAuth) eku.Add(new Oid(OidServerAuth));
        if (clientAuth) eku.Add(new Oid(OidClientAuth));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, true));
        if (dnsSan != null)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(dnsSan);
            req.CertificateExtensions.Add(san.Build());
        }
        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        using var signed = req.Create(ca, notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-5),
                                      notAfter ?? DateTimeOffset.UtcNow.AddHours(1), serial);
        var withKey = signed.CopyWithPrivateKey(key);
        var pfx = withKey.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }

    public static X509Certificate2 PublicOf(X509Certificate2 cert)
        => X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));

    public static bool HasEku(X509Certificate2 cert, string oid)
    {
        foreach (var ext in cert.Extensions)
            if (ext is X509EnhancedKeyUsageExtension eku)
                foreach (var o in eku.EnhancedKeyUsages)
                    if (o.Value == oid) return true;
        return false;
    }

    public static void DeleteKey(X509Certificate2 cert)
    {
        try
        {
            using var ec = cert.GetECDsaPrivateKey();
            if (ec is ECDsaCng cng) cng.Key.Delete();
        }
        catch { /* best effort cleanup of the transient key container */ }
    }

    // Server-side client-cert validation: chain to OUR CA + clientAuth EKU.
    public static Func<X509Certificate2, X509Chain?, SslPolicyErrors, bool> ClientValidator(X509Certificate2 caPublic)
        => (cert, _, __) =>
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.CustomTrustStore.Add(caPublic);
            return chain.Build(cert) && HasEku(cert, OidClientAuth);
        };

    // Client: custom root trust (only our CA), dial loopback, no proxy, no redirect.
    public static HttpClient MakeMtlsClient(int port, X509Certificate2 trustedCa, X509Certificate2? clientCert)
    {
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
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
            await s.ConnectAsync(IPAddress.Loopback, port, ct);
            return new NetworkStream(s, ownsSocket: true);
        };
        return new HttpClient(handler);
    }
}
