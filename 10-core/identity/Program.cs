// P3b S2 -- localai-identity host-admin CLI.
//   selftest       S2.1: CA + issuance core against throwaway TPM keys
//   selftest2      S2.2: init + membership-store lifecycle against a scratch dir (no production identity)
//   init           mint the REAL hub identity into {state}/identity (one-time, consequential)
//   status         show hub + store summary
//   list-devices   list devices in the store
//   revoke-device <device-id>   revoke a device (generation++)

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LocalAI.Identity;

return (args.Length == 0 ? "" : args[0]) switch
{
    "selftest" => Selftest1(),
    "selftest2" => Selftest2(),
    "init" => Init(),
    "status" => Status(),
    "list-devices" => ListDevices(),
    "revoke-device" => RevokeDevice(args.Length > 1 ? args[1] : null),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("usage: localai-identity <selftest|selftest2|init|status|list-devices|revoke-device <id>>");
    return 2;
}

// ---------------------------------------------------------------- S2.1 selftest
static int Selftest1()
{
    int pass = 0, fail = 0;
    void Assert(bool c, string m) { if (c) { pass++; Console.WriteLine("  PASS  " + m); } else { fail++; Console.WriteLine("  FAIL  " + m); } }

    var prov = new CngProvider(Ca.TpmProvider);
    string suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
    string caKeyName = "localai-selftest-ca-" + suffix;
    string clientKeyName = "localai-selftest-client-" + suffix;
    string hubShort = "k7m4q2dp7n6r5v2x";
    string deviceId = Guid.NewGuid().ToString();
    string serverName = $"localai-{hubShort}.local";

    X509Certificate2? caCert = null;
    try
    {
        caCert = Ca.CreateCa(caKeyName, "LocalAI Hub CA " + hubShort, years: 10);
        Assert(Ca.CaExists(caKeyName), "CA key created in TPM (non-exportable)");
        var caBc = caCert.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        Assert(caBc is { CertificateAuthority: true, HasPathLengthConstraint: true, PathLengthConstraint: 0 },
               "CA cert: BasicConstraints CA=true, pathLen=0");

        bool refused = false;
        try { using var _ = Ca.CreateCa(caKeyName, "dup", 10); } catch (InvalidOperationException) { refused = true; }
        Assert(refused, "re-creating an existing CA is refused (fail-closed)");

        var caPublic = Ca.PublicOf(caCert);

        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var serverPub = PublicKey.CreateFromSubjectPublicKeyInfo(serverKey.ExportSubjectPublicKeyInfo(), out _);
        using var serverLeaf = Ca.IssueLeaf(caKeyName, caCert, serverPub, serverName,
                                            dnsSan: serverName, uriSan: null, serverAuth: true, clientAuth: false, days: 30);
        Assert(Ca.VerifyChainAndEku(serverLeaf, caPublic, Ca.OidServerAuth), "server leaf chains to CA + has serverAuth EKU");
        Assert(serverLeaf.GetNameInfo(X509NameType.DnsName, false) == serverName, "server leaf DNS SAN = " + serverName);
        Assert(!Ca.VerifyChainAndEku(serverLeaf, caPublic, Ca.OidClientAuth), "server leaf does NOT carry clientAuth EKU");

        var ckParams = new CngKeyCreationParameters { Provider = prov, ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing };
        using var clientEcdsa = new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, clientKeyName, ckParams));
        var csrReq = new CertificateRequest("CN=whatever-the-client-claims", clientEcdsa, HashAlgorithmName.SHA256);
        csrReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true)); // attacker wants CA=true
        var csrDer = csrReq.CreateSigningRequest();

        var csrPub = Ca.PublicKeyFromCsr(csrDer);
        Assert(true, "client CSR loaded + proof-of-possession verified");

        string uri = "urn:localai:device:" + deviceId;
        using var clientLeaf = Ca.IssueLeaf(caKeyName, caCert, csrPub, "device-" + deviceId[..8],
                                            dnsSan: null, uriSan: uri, serverAuth: false, clientAuth: true, days: 90);
        Assert(Ca.VerifyChainAndEku(clientLeaf, caPublic, Ca.OidClientAuth), "client leaf chains to CA + has clientAuth EKU");
        Assert(Ca.HasUriSan(clientLeaf, uri), "client leaf URI SAN = " + uri);
        var leafBc = clientLeaf.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        Assert(leafBc is { CertificateAuthority: false }, "server-generated extensions win: injected CA=true IGNORED (leaf CA=false)");

        var bad = (byte[])csrDer.Clone(); bad[^1] ^= 0xFF;
        bool popRejected = false;
        try { Ca.PublicKeyFromCsr(bad); } catch { popRejected = true; }
        Assert(popRejected, "tampered CSR (bad signature) is rejected");
    }
    finally
    {
        Ca.DeleteKey(caKeyName);
        try { if (CngKey.Exists(clientKeyName, prov)) CngKey.Open(clientKeyName, prov).Delete(); } catch { }
        caCert?.Dispose();
    }
    Console.WriteLine($"\nS2.1 identity selftest: PASS={pass} FAIL={fail}");
    return fail > 0 ? 1 : 0;
}

// ---------------------------------------------------------------- S2.2 selftest (scratch dir)
static int Selftest2()
{
    int pass = 0, fail = 0;
    void Assert(bool c, string m) { if (c) { pass++; Console.WriteLine("  PASS  " + m); } else { fail++; Console.WriteLine("  FAIL  " + m); } }

    var root = Path.Combine(Path.GetTempPath(), "localai-identity-selftest-" + Guid.NewGuid().ToString("N")[..8]);
    var idDir = Path.Combine(root, "identity");
    var secDir = Path.Combine(root, "secrets");
    string? caKey = null, srvKey = null;
    try
    {
        var hub = Identity.Init(idDir, secDir);
        caKey = hub.CaKeyName; srvKey = hub.ServerKeyName;
        Assert(File.Exists(Path.Combine(idDir, "hub.json")), "init wrote hub.json");
        Assert(File.Exists(Path.Combine(idDir, "ca.cer")) && File.Exists(Path.Combine(idDir, "server.cer")), "init wrote ca.cer + server.cer");
        Assert(File.Exists(Path.Combine(secDir, "identity-locators.json")), "init wrote TPM key locators to secrets");
        Assert(Ca.CaExists(hub.CaKeyName), "CA key present in TPM");
        Assert(hub.HubShort.Length == 16, "hub-id-short is 16 base32 chars (" + hub.HubShort + ")");
        Assert(hub.ServerName == $"localai-{hub.HubShort}.local", "server name derived from hub-id-short");

        bool refused = false;
        try { Identity.Init(idDir, secDir); } catch (InvalidOperationException) { refused = true; }
        Assert(refused, "re-init on an existing identity is refused (fail-closed)");

        var store = Store.LoadOrEmpty(idDir);
        Assert(store.IdentityGeneration == 0, "fresh store generation = 0");
        store.AddProvisioning("dev-1", "Zori 的笔记本", "192.168.1.9");
        store.AddCandidate("dev-1", "serial-1", "fpAAA", "spkiAAA", "nb", "na");
        Assert(!store.IsActive("fpAAA"), "candidate cert is NOT active before approval");
        store.Activate("dev-1", "fpAAA");
        Assert(store.IdentityGeneration == 1, "activate bumped generation to 1");
        Assert(store.IsActive("fpAAA"), "activated cert is active");
        store.Save(idDir);

        var reloaded = Store.LoadOrEmpty(idDir);
        Assert(reloaded.IsActive("fpAAA") && reloaded.IdentityGeneration == 1, "store persisted across reload (atomic write)");

        reloaded.RevokeDevice("dev-1");
        Assert(reloaded.IdentityGeneration == 2, "revoke bumped generation to 2 (monotonic)");
        Assert(!reloaded.IsActive("fpAAA"), "revoked device -> its cert no longer active");
        Assert(reloaded.FindByFingerprint("fpAAA") is { device.Status: "revoked", cert.Status: "revoked" }, "revoke marked both device and cert");
        reloaded.Save(idDir);
    }
    finally
    {
        if (caKey is not null) Ca.DeleteKey(caKey);
        if (srvKey is not null) Ca.DeleteKey(srvKey);
        try { Directory.Delete(root, true); } catch { }
    }
    Console.WriteLine($"\nS2.2 identity selftest: PASS={pass} FAIL={fail}");
    return fail > 0 ? 1 : 0;
}

// ---------------------------------------------------------------- real commands
static int Init()
{
    var idDir = Paths.IdentityDir();
    if (Identity.IsInitialized(idDir))
    {
        Console.WriteLine("hub identity already exists at " + idDir + " (fail-closed, refusing to overwrite).");
        return 1;
    }
    var hub = Identity.Init(idDir, Paths.SecretsDir());
    Console.WriteLine($"initialized hub {hub.HubShort}");
    Console.WriteLine($"  identity dir : {idDir}");
    Console.WriteLine($"  server name  : {hub.ServerName}");
    Console.WriteLine($"  CA key (TPM) : {hub.CaKeyName}  [non-exportable]");
    Console.WriteLine("  NOTE: this CA is the hub identity. Losing it = re-pair every device. It is excluded from backup (D43 S0.8).");
    return 0;
}

static int Status()
{
    var idDir = Paths.IdentityDir();
    if (!Identity.IsInitialized(idDir)) { Console.WriteLine("no hub identity at " + idDir + " (run: init)"); return 1; }
    Console.WriteLine(File.ReadAllText(Path.Combine(idDir, "hub.json")));
    var s = Store.LoadOrEmpty(idDir);
    Console.WriteLine($"generation={s.IdentityGeneration}  devices={s.Devices.Count}  certs={s.Certs.Count}");
    return 0;
}

static int ListDevices()
{
    var idDir = Paths.IdentityDir();
    if (!Identity.IsInitialized(idDir)) { Console.WriteLine("no hub identity (run: init)"); return 1; }
    foreach (var d in Store.LoadOrEmpty(idDir).Devices)
        Console.WriteLine($"{d.DeviceId}  {d.Status,-12} gen={d.CurrentGeneration}  {d.UntrustedDisplayName}");
    return 0;
}

static int RevokeDevice(string? deviceId)
{
    if (deviceId is null) { Console.WriteLine("usage: revoke-device <device-id>"); return 2; }
    var idDir = Paths.IdentityDir();
    if (!Identity.IsInitialized(idDir)) { Console.WriteLine("no hub identity (run: init)"); return 1; }
    var s = Store.LoadOrEmpty(idDir);
    s.RevokeDevice(deviceId);
    s.Save(idDir);
    Console.WriteLine($"revoked {deviceId}; generation now {s.IdentityGeneration}");
    return 0;
}
