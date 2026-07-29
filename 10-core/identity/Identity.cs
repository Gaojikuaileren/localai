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
}
