// P3b S2.2 -- hub identity initialization. Mints hub_id + CA (TPM) + server leaf (TPM key), writes
// PUBLIC materials to {state}/identity and TPM key LOCATORS to {state}/secrets (D43 S0.7/S0.8).
// Fail-closed: refuses to overwrite an existing identity (packet §4.1 / §7.1).

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace LocalAI.Identity;

public sealed record HubInfo(Guid HubId, string HubShort, string ServerName, string CaKeyName, string ServerKeyName);

public static class Identity
{
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static HubInfo Init(string identityDir, string secretsDir)
    {
        var hubJson = Path.Combine(identityDir, "hub.json");
        if (File.Exists(hubJson))
            throw new InvalidOperationException("identity already initialized (fail-closed, no overwrite): " + hubJson);

        Directory.CreateDirectory(identityDir);
        Directory.CreateDirectory(secretsDir);

        var hubId = Guid.NewGuid();
        var hubShort = HubId.Short(hubId);
        var caKeyName = "localai-ca-" + hubShort;
        var serverKeyName = "localai-server-" + hubShort;
        var serverName = $"localai-{hubShort}.local";

        using var caCert = Ca.CreateCa(caKeyName, "LocalAI Hub CA " + hubShort, years: 10);

        // server leaf key: non-exportable software CNG key (SChannel-usable; B17/D44). Persistent,
        // used by Kestrel in S4/S5. The CA key (above) stays in the TPM.
        var srvParams = new CngKeyCreationParameters
        {
            Provider = new CngProvider(Ca.TlsKeyProvider),
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
        };
        using var serverKey = new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, serverKeyName, srvParams));
        var serverPub = PublicKey.CreateFromSubjectPublicKeyInfo(serverKey.ExportSubjectPublicKeyInfo(), out _);
        using var serverLeaf = Ca.IssueLeaf(caKeyName, caCert, serverPub, serverName,
                                            dnsSan: serverName, uriSan: null, serverAuth: true, clientAuth: false, days: 30);

        // public materials -> {state}/identity
        File.WriteAllBytes(Path.Combine(identityDir, "ca.cer"), caCert.Export(X509ContentType.Cert));
        File.WriteAllBytes(Path.Combine(identityDir, "server.cer"), serverLeaf.Export(X509ContentType.Cert));
        File.WriteAllText(hubJson, JsonSerializer.Serialize(new
        {
            hub_id = hubId,
            hub_id_short = hubShort,
            server_name = serverName,
            created = DateTimeOffset.UtcNow.ToString("O"),
        }, Opt));

        // TPM key locators (NOT the private keys) -> {state}/secrets
        File.WriteAllText(Path.Combine(secretsDir, "identity-locators.json"), JsonSerializer.Serialize(new
        {
            ca_provider = Ca.TpmProvider,           // CA key: TPM (non-exportable)
            server_provider = Ca.TlsKeyProvider,    // server leaf key: software KSP (non-exportable; B17/D44)
            ca_key_name = caKeyName,
            server_key_name = serverKeyName,
            ca_thumbprint = caCert.Thumbprint,
            server_thumbprint = serverLeaf.Thumbprint,
        }, Opt));

        // empty membership store (generation 0)
        new Store().Save(identityDir);

        return new HubInfo(hubId, hubShort, serverName, caKeyName, serverKeyName);
    }

    public static bool IsInitialized(string identityDir) => File.Exists(Path.Combine(identityDir, "hub.json"));

    /// <summary>服务器叶证书的到期时间(用于到期预警)。</summary>
    public static DateTimeOffset ServerCertExpiry(string identityDir)
    {
        using var c = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(identityDir, "server.cer")));
        return c.NotAfter;
    }

    /// <summary>
    /// P3c -- 续签服务器叶证书。**同一把服务器密钥、同一个 CA、同一个 hub_id、同一个 server_name**,
    /// 只换有效期。因此:CA 不动 → 客户端保存的 CA 根不变 → **所有设备无需重新配对**。
    ///
    /// 为什么必须有它:叶证书只有 30 天(见 Init),到期后客户端 TLS 握手直接失败,
    /// 而症状只是"连不上",极难归因。没有续签手段的话唯一出路是重铸 hub = 全家重新配对,
    /// 那会直接摧毁「配对一次就永久记住」。(P3c 审查发现 [2])
    ///
    /// 副作用:CurrentUser\My 里那份旧证书必须删掉 —— Edge 用 thumbprint 查找(LoadServerCert),
    /// 留着旧的会让它继续用过期证书。
    /// </summary>
    public static (string serverName, DateTimeOffset notAfter, string thumbprint) RenewServerCert(
        string identityDir, string secretsDir, int days = 30)
    {
        var hubJson = Path.Combine(identityDir, "hub.json");
        if (!File.Exists(hubJson)) throw new InvalidOperationException("no hub identity: " + hubJson);

        var hub = JsonDocument.Parse(File.ReadAllText(hubJson)).RootElement;
        var serverName = hub.GetProperty("server_name").GetString()!;

        var locPath = Path.Combine(secretsDir, "identity-locators.json");
        var loc = JsonDocument.Parse(File.ReadAllText(locPath)).RootElement;
        var caKeyName = loc.GetProperty("ca_key_name").GetString()!;
        var serverKeyName = loc.GetProperty("server_key_name").GetString()!;
        var serverProvider = loc.TryGetProperty("server_provider", out var sp) ? sp.GetString()! : Ca.TlsKeyProvider;
        var caProvider = loc.TryGetProperty("ca_provider", out var cp) ? cp.GetString()! : Ca.TpmProvider;
        var caThumb = loc.GetProperty("ca_thumbprint").GetString()!;

        using var caCert = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(identityDir, "ca.cer")));
        using var oldServer = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(identityDir, "server.cer")));
        var oldThumb = oldServer.Thumbprint;

        // 复用既有服务器密钥(软件 KSP,B17/D44):不换密钥 = 不触碰任何已建立的信任。
        using var serverKey = new ECDsaCng(CngKey.Open(serverKeyName, new CngProvider(serverProvider)));
        var serverPub = PublicKey.CreateFromSubjectPublicKeyInfo(serverKey.ExportSubjectPublicKeyInfo(), out _);
        using var leaf = Ca.IssueLeaf(caKeyName, caCert, serverPub, serverName,
                                      dnsSan: serverName, uriSan: null, serverAuth: true, clientAuth: false, days: days);

        File.WriteAllBytes(Path.Combine(identityDir, "server.cer"), leaf.Export(X509ContentType.Cert));
        File.WriteAllText(locPath, JsonSerializer.Serialize(new
        {
            ca_provider = caProvider,
            server_provider = serverProvider,
            ca_key_name = caKeyName,
            server_key_name = serverKeyName,
            ca_thumbprint = caThumb,
            server_thumbprint = leaf.Thumbprint,
        }, Opt));

        // 清掉 CurrentUser\My 里的旧证书,否则 Edge 会继续按旧 thumbprint 找到过期的那张。
        try
        {
            using var st = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            st.Open(OpenFlags.ReadWrite);
            foreach (var c in st.Certificates.Find(X509FindType.FindByThumbprint, oldThumb, false)) st.Remove(c);
        }
        catch { /* 清不掉不致命:新证书 thumbprint 不同,LoadServerCert 会自行物化新的那张 */ }

        return (serverName, leaf.NotAfter, leaf.Thumbprint);
    }
}
