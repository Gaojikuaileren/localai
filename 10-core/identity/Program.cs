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
    "selftest3" => Selftest3(),
    "selftest4" => Selftest4(),
    "selftest5" => Selftest5(),
    "init" => Init(),
    "status" => Status(),
    "list-devices" => ListDevices(),
    "revoke-device" => RevokeDevice(args.Length > 1 ? args[1] : null),
    "renew-server" => RenewServer(),
    "list-members" => ListMembers(),
    "add-member" => AddMember(args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null),
    "set-device-member" => SetDeviceMember(args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("usage: localai-identity <selftest|selftest2|selftest3|selftest4|selftest5|init|status");
    Console.WriteLine("                        |renew-server|list-devices|revoke-device <id>");
    Console.WriteLine("                        |list-members|add-member <name> [admin|member]");
    Console.WriteLine("                        |set-device-member <device-id> <member-id|->>");
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
    Console.WriteLine($"generation={s.IdentityGeneration}  devices={s.Devices.Count}  certs={s.Certs.Count}  members={s.Members.Count}");

    // 服务器证书到期是一颗静默炸弹:过期后客户端只会显示"连不上",极难归因。
    // 所以每次 status 都把它摆在明面上,快到期就明确提示续签命令。
    var exp = Identity.ServerCertExpiry(idDir);
    var left = (exp - DateTimeOffset.UtcNow).TotalDays;
    Console.WriteLine($"server cert 到期: {exp:yyyy-MM-dd HH:mm} (剩 {left:F1} 天)");
    if (left < 0) Console.WriteLine("  ✗ 已过期 —— 客户端将无法连接。执行:  localai-identity renew-server");
    else if (left < 10) Console.WriteLine("  ! 快到期了。执行:  localai-identity renew-server  (不需要重新配对)");
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

// ---------------------------------------------------------------- P3c S1b selftest: 服务器证书续签
// 续签的**唯一目的**是:证书过期不能逼全家重新配对。所以断言的核心是"CA 与 hub 不变、
// 已签发的设备证书续签后仍然验得过"。(审查发现 [2])
static int Selftest5()
{
    int pass = 0, fail = 0;
    void Assert(bool c, string m) { if (c) { pass++; Console.WriteLine("  PASS  " + m); } else { fail++; Console.WriteLine("  FAIL  " + m); } }

    var root = Path.Combine(Path.GetTempPath(), "localai-renew-" + Guid.NewGuid().ToString("N")[..8]);
    var idDir = Path.Combine(root, "identity");
    var secDir = Path.Combine(root, "secrets");
    string? caKey = null, srvKey = null;
    var thumbs = new List<string>();
    try
    {
        var hub = Identity.Init(idDir, secDir);
        caKey = hub.CaKeyName; srvKey = hub.ServerKeyName;

        var caBefore = File.ReadAllBytes(Path.Combine(idDir, "ca.cer"));
        var srvBefore = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(idDir, "server.cer")));
        thumbs.Add(srvBefore.Thumbprint);
        var caPublic = X509CertificateLoader.LoadCertificate(caBefore);

        // 先签一张"设备证书",模拟一台已配对的电脑
        using var deviceKey = new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, "localai-selftest5-dev-" + Guid.NewGuid().ToString("N")[..6],
            new CngKeyCreationParameters { Provider = new CngProvider(Ca.TlsKeyProvider), ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing }));
        var devPub = PublicKey.CreateFromSubjectPublicKeyInfo(deviceKey.ExportSubjectPublicKeyInfo(), out _);
        using var devCert = Ca.IssueLeaf(hub.CaKeyName, X509CertificateLoader.LoadCertificate(caBefore), devPub,
                                         "device-test", null, "urn:localai:device:test", false, true, 90);
        Assert(Ca.VerifyChainAndEku(devCert, caPublic, Ca.OidClientAuth), "续签前:设备证书验得过");

        // 续签
        var (name, notAfter, thumb) = Identity.RenewServerCert(idDir, secDir, days: 60);
        thumbs.Add(thumb);
        var srvAfter = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(idDir, "server.cer")));

        Assert(name == hub.ServerName, "server_name 不变(证书 SAN 不变 -> 客户端 TLS 主机名校验仍通过)");
        Assert(File.ReadAllBytes(Path.Combine(idDir, "ca.cer")).SequenceEqual(caBefore), "**CA 证书一字未改**(客户端保存的根信任仍有效)");
        Assert(srvAfter.Thumbprint != srvBefore.Thumbprint, "服务器证书确实换了新的一张");
        Assert(notAfter > srvBefore.NotAfter, "新证书有效期更长");
        Assert(srvAfter.GetPublicKeyString() == srvBefore.GetPublicKeyString(), "**服务器公钥不变**(复用同一把密钥,没换身份)");
        Assert(Ca.VerifyChainAndEku(srvAfter, caPublic, Ca.OidServerAuth), "新服务器证书仍由同一个 CA 签出");

        // 最要紧的一条:已配对设备不受影响
        Assert(Ca.VerifyChainAndEku(devCert, caPublic, Ca.OidClientAuth), "★ 续签后:已配对设备的证书**依然有效**(无需重新配对)");

        var hubAfter = JsonDocument.Parse(File.ReadAllText(Path.Combine(idDir, "hub.json"))).RootElement;
        Assert(hubAfter.GetProperty("hub_id").GetString() == hub.HubId.ToString(), "hub_id 不变");

        var locAfter = JsonDocument.Parse(File.ReadAllText(Path.Combine(secDir, "identity-locators.json"))).RootElement;
        Assert(locAfter.GetProperty("server_thumbprint").GetString() == thumb, "locators 里的 server_thumbprint 已更新(Edge 才能找到新证书)");
        Assert(locAfter.GetProperty("server_key_name").GetString() == srvKey, "server_key_name 不变");
        Assert(locAfter.GetProperty("ca_key_name").GetString() == caKey, "ca_key_name 不变");

        var exp = Identity.ServerCertExpiry(idDir);
        Assert(Math.Abs((exp - notAfter).TotalSeconds) < 2, "ServerCertExpiry 读到的就是新到期时间(status 预警用它)");
    }
    catch (Exception ex) { fail++; Console.WriteLine("  FAIL  自检抛异常: " + ex.Message); }
    finally
    {
        if (caKey is not null) Ca.DeleteKey(caKey);
        if (srvKey is not null) Ca.DeleteKey(srvKey);
        try
        {
            using var st = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            st.Open(OpenFlags.ReadWrite);
            foreach (var t in thumbs) foreach (var c in st.Certificates.Find(X509FindType.FindByThumbprint, t, false)) st.Remove(c);
        }
        catch { }
        try { Directory.Delete(root, true); } catch { }
    }

    Console.WriteLine($"\nP3c 服务器证书续签 selftest: PASS={pass} FAIL={fail}");
    return fail > 0 ? 1 : 0;
}

// ---------------------------------------------------------------- P3c 服务器证书续签
static int RenewServer()
{
    var idDir = Paths.IdentityDir();
    var secDir = Paths.SecretsDir();
    if (!Identity.IsInitialized(idDir)) { Console.WriteLine("no hub identity (run: init)"); return 1; }

    var before = Identity.ServerCertExpiry(idDir);
    var (name, after, thumb) = Identity.RenewServerCert(idDir, secDir);
    Console.WriteLine($"服务器证书已续签  {name}");
    Console.WriteLine($"  原到期: {before:yyyy-MM-dd HH:mm}  ->  新到期: {after:yyyy-MM-dd HH:mm}");
    Console.WriteLine($"  指纹  : {thumb}");
    Console.WriteLine("  CA 与 hub 未变 -> **所有已配对设备无需重新配对**。");
    Console.WriteLine("  ★ 若 Edge 正在运行,请重启它以加载新证书。");
    return 0;
}

// ---------------------------------------------------------------- P3c/D45 成员层 CLI
static int ListMembers()
{
    var idDir = Paths.IdentityDir();
    if (!Identity.IsInitialized(idDir)) { Console.WriteLine("no hub identity (run: init)"); return 1; }
    var s = Store.LoadOrEmpty(idDir);
    if (s.Members.Count == 0) { Console.WriteLine("(no members yet -- add one: add-member <name>)"); return 0; }
    foreach (var m in s.Members)
    {
        var devs = s.Devices.Where(d => d.DefaultMemberId == m.MemberId).Select(d => d.UntrustedDisplayName);
        Console.WriteLine($"{m.MemberId}  {m.Role,-6}  {m.DisplayName}   默认设备: {(devs.Any() ? string.Join(", ", devs) : "-")}");
    }
    return 0;
}

static int AddMember(string? name, string? role)
{
    if (name is null) { Console.WriteLine("usage: add-member <display-name> [admin|member]"); return 2; }
    var idDir = Paths.IdentityDir();
    if (!Identity.IsInitialized(idDir)) { Console.WriteLine("no hub identity (run: init)"); return 1; }
    var s = Store.LoadOrEmpty(idDir);
    var m = s.AddMember(name, role);
    s.Save(idDir);
    Console.WriteLine($"added member {m.MemberId}  {m.Role}  {m.DisplayName}");
    return 0;
}

static int SetDeviceMember(string? deviceId, string? memberId)
{
    if (deviceId is null || memberId is null)
    { Console.WriteLine("usage: set-device-member <device-id> <member-id|->   ('-' clears)"); return 2; }
    var idDir = Paths.IdentityDir();
    if (!Identity.IsInitialized(idDir)) { Console.WriteLine("no hub identity (run: init)"); return 1; }
    var s = Store.LoadOrEmpty(idDir);
    s.SetDeviceDefaultMember(deviceId, memberId == "-" ? null : memberId);
    s.Save(idDir);
    Console.WriteLine($"device {deviceId} default member -> {(memberId == "-" ? "(none)" : memberId)}");
    return 0;
}

// ---------------------------------------------------------------- P3c S1 selftest: 成员层(scratch dir)
static int Selftest4()
{
    int pass = 0, fail = 0;
    void Assert(bool c, string m) { if (c) { pass++; Console.WriteLine("  PASS  " + m); } else { fail++; Console.WriteLine("  FAIL  " + m); } }

    var dir = Path.Combine(Path.GetTempPath(), "localai-members-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    try
    {
        var s = new Store();

        // 第一位成员自动成为家庭安全管理员(否则没人能管安全设置)
        var a = s.AddMember("A");
        Assert(a.Role == Store.RoleAdmin, "第一位成员自动成为家庭安全管理员");
        var b = s.AddMember("B");
        Assert(b.Role == Store.RoleMember, "第二位成员默认是普通成员(平权,但不管安全)");
        Assert(s.AdminCount == 1, "恰有一名家庭安全管理员");

        // fail-closed:最后一名管理员不可降级 / 不可删除
        bool demoted = true;
        try { s.SetMemberRole(a.MemberId, Store.RoleMember); } catch (InvalidOperationException) { demoted = false; }
        Assert(!demoted, "最后一名家庭安全管理员**不可降级**(fail-closed)");
        bool removed = true;
        try { s.RemoveMember(a.MemberId); } catch (InvalidOperationException) { removed = false; }
        Assert(!removed, "最后一名家庭安全管理员**不可删除**(fail-closed)");

        // 升 B 为管理员后,A 才可降级
        s.SetMemberRole(b.MemberId, Store.RoleAdmin);
        Assert(s.AdminCount == 2, "可以有第二名管理员");
        s.SetMemberRole(a.MemberId, Store.RoleMember);
        Assert(s.AdminCount == 1 && s.FindMember(a.MemberId)!.Role == Store.RoleMember, "有他人接任后原管理员可降级");

        // 设备 × 成员
        var d = s.AddProvisioning(Guid.NewGuid().ToString(), "SENIORBIRDS", null);
        Assert(d.DefaultMemberId is null, "新设备默认成员为空(未指定,不是猜)");
        s.SetDeviceDefaultMember(d.DeviceId, b.MemberId);
        Assert(s.Devices.First().DefaultMemberId == b.MemberId, "可以给设备指定默认成员");

        bool dangling = true;
        try { s.SetDeviceDefaultMember(d.DeviceId, Guid.NewGuid().ToString()); } catch (KeyNotFoundException) { dangling = false; }
        Assert(!dangling, "拒绝把设备指向不存在的成员(不留悬空引用)");

        // 删成员 -> 引用它的设备回到"未指定",而不是留悬空 id
        // 此刻 b 是唯一管理员,故必须先让 C 接任,才能降级并移除 b(即不变量本身在起作用)
        var c = s.AddMember("C");
        s.SetMemberRole(c.MemberId, Store.RoleAdmin);
        s.SetMemberRole(b.MemberId, Store.RoleMember);
        s.RemoveMember(b.MemberId);
        Assert(s.Devices.First().DefaultMemberId is null, "成员被删后,设备的默认成员回到未指定(无悬空引用)");

        // 往返持久化 + 与 P3b 设备准入互不干扰
        s.Save(dir);
        var r = Store.LoadOrEmpty(dir);
        Assert(r.Members.Count == s.Members.Count && r.AdminCount == 1, "成员表往返持久化");
        Assert(r.IdentityGeneration == s.IdentityGeneration, "成员操作**不动** IdentityGeneration(世代号只管设备准入)");

        // 旧 store.json(P3b 时代,无 Members 键)仍可读 -> 空成员表,不抛异常
        File.WriteAllText(Path.Combine(dir, "store.json"),
            "{\"IdentityGeneration\":7,\"SnapshotCreatedAt\":\"x\",\"Devices\":[],\"Certs\":[]}");
        var legacy = Store.LoadOrEmpty(dir);
        Assert(legacy.Members.Count == 0 && legacy.IdentityGeneration == 7, "旧 store.json(无 Members 键)向前兼容");
    }
    finally { try { Directory.Delete(dir, true); } catch { } }

    Console.WriteLine($"\nP3c S1 成员层 selftest: PASS={pass} FAIL={fail}");
    return fail > 0 ? 1 : 0;
}

// ---------------------------------------------------------------- S2.3 pairing selftest (scratch)
static int Selftest3()
{
    int pass = 0, fail = 0;
    void Assert(bool c, string m) { if (c) { pass++; Console.WriteLine("  PASS  " + m); } else { fail++; Console.WriteLine("  FAIL  " + m); } }
    byte[] R(int n) => RandomNumberGenerator.GetBytes(n);

    var root = Path.Combine(Path.GetTempPath(), "localai-pairing-selftest-" + Guid.NewGuid().ToString("N")[..8]);
    var idDir = Path.Combine(root, "identity");
    var secDir = Path.Combine(root, "secrets");
    var prov = new CngProvider(Ca.TpmProvider);
    string? caKey = null, srvKey = null, clientKeyName = null;
    try
    {
        var hub = Identity.Init(idDir, secDir);
        caKey = hub.CaKeyName; srvKey = hub.ServerKeyName;
        var pairing = new Pairing(idDir, secDir);
        var caPub = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(idDir, "ca.cer")));

        // SAS determinism + tamper sensitivity
        var t = pairing.BuildTranscript(1, R(32), R(32), R(32), R(16), R(32));
        var d1 = Sas.Derive(t);
        Assert(d1.words.Length == 6, "SAS is six words");
        Assert(d1.words.SequenceEqual(Sas.Derive(t).words), "SAS is deterministic (same transcript -> same words)");
        Assert(!Sas.Derive(t with { ServerNonce = R(32) }).words.SequenceEqual(d1.words), "changing a transcript field changes the SAS");
        Assert(d1.indices.All(i => i is >= 0 and < 2048), "SAS indices in [0,2048)");

        // client key (TPM) + CSR + claim secret
        clientKeyName = "localai-selftest-pairclient-" + Convert.ToHexString(R(4)).ToLowerInvariant();
        var ckp = new CngKeyCreationParameters { Provider = prov, ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing };
        using var clientEcdsa = new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, clientKeyName, ckp));
        var csrDer = new CertificateRequest("CN=client", clientEcdsa, HashAlgorithmName.SHA256).CreateSigningRequest();
        var clientCsrSpkiSha = SHA256.HashData(clientEcdsa.ExportSubjectPublicKeyInfo());
        var claimSecret = R(32);
        var claimSecretHash = SHA256.HashData(claimSecret);
        var clientNonce = R(32);

        // window closed -> enroll rejected
        bool closed = false;
        try { pairing.Enroll(csrDer, clientNonce, claimSecretHash, 1, "dev"); } catch (InvalidOperationException) { closed = true; }
        Assert(closed, "enroll rejected while the pairing window is closed");

        pairing.OpenWindow(TimeSpan.FromMinutes(5));
        var en = pairing.Enroll(csrDer, clientNonce, claimSecretHash, 1, "Zori 的笔记本");
        Assert(en.RequestId.Length == 32, "enroll returns a 128-bit request id");

        // client independently derives the same SAS; a MITM server-leaf swap makes it differ
        var clientSas = Sas.Derive(pairing.BuildTranscript(1, claimSecretHash, clientNonce, en.ServerNonce,
                                    Convert.FromHexString(en.RequestId), clientCsrSpkiSha));
        Assert(clientSas.words.SequenceEqual(en.Sas), "client and host derive the identical six-word SAS");
        var mitm = pairing.BuildTranscript(1, claimSecretHash, clientNonce, en.ServerNonce,
                                    Convert.FromHexString(en.RequestId), clientCsrSpkiSha) with { ServerLeafSha256 = R(32) };
        Assert(!Sas.Derive(mitm).words.SequenceEqual(en.Sas), "MITM server-leaf swap makes the SAS differ (client would catch it)");

        // approve (host)
        pairing.Approve(en.RequestId);
        Assert(pairing.StatusOf(en.RequestId) == "approved", "approve -> approved");
        var afterApprove = Store.LoadOrEmpty(idDir);
        Assert(afterApprove.Devices is [{ Status: "provisioning" }], "approve created exactly one provisioning device");
        Assert(afterApprove.IdentityGeneration == 0, "a candidate before complete does NOT bump the generation");
        var deviceId = afterApprove.Devices[0].DeviceId;

        // status: wrong secret rejected; right secret returns the challenge
        bool badSecret = false;
        try { pairing.Status(en.RequestId, R(32)); } catch (UnauthorizedAccessException) { badSecret = true; }
        Assert(badSecret, "status with the wrong claim secret is rejected");
        var st = pairing.Status(en.RequestId, claimSecret);
        Assert(st is { Status: "approved", ClaimNonce: not null, CandidateSha256: not null }, "status returns claim nonce + candidate hash");

        // claim: challenge signed by the CSR key
        var challenge = Pairing.BuildChallenge(Convert.FromHexString(en.RequestId), st.ClaimNonce!, Convert.FromHexString(st.CandidateSha256!));
        var sig = clientEcdsa.SignData(challenge, HashAlgorithmName.SHA256);
        using var cand = pairing.Claim(en.RequestId, claimSecret, sig);
        Assert(Ca.VerifyChainAndEku(cand, caPub, Ca.OidClientAuth), "claim returns a candidate chaining to CA + clientAuth EKU");
        Assert(Ca.HasUriSan(cand, "urn:localai:device:" + deviceId), "candidate URI SAN matches the issued device id");
        using var cand2 = pairing.Claim(en.RequestId, claimSecret, sig);
        Assert(cand.Thumbprint == cand2.Thumbprint, "claim is idempotent (same candidate on retry)");
        bool badSig = false;
        try { pairing.Claim(en.RequestId, claimSecret, R(64)); } catch (UnauthorizedAccessException) { badSig = true; }
        Assert(badSig, "claim with an invalid challenge signature is rejected");

        // complete -> active, generation bumps
        pairing.Complete(en.RequestId);
        Assert(pairing.StatusOf(en.RequestId) == "active", "complete -> active");
        var final = Store.LoadOrEmpty(idDir);
        Assert(final.IdentityGeneration == 1, "complete/activate bumped the generation to 1");
        Assert(final.IsActive(st.CandidateSha256!), "the activated device+cert is active in the store");

        // single approve: re-approving a non-pending request is refused
        bool reappr = false;
        try { pairing.Approve(en.RequestId); } catch (InvalidOperationException) { reappr = true; }
        Assert(reappr, "re-approving a non-pending request is refused (no bulk/re-approve)");

        // queue cap: MaxPending
        for (int i = 0; i < Pairing.MaxPending; i++)
        {
            using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var c = new CertificateRequest("CN=q", k, HashAlgorithmName.SHA256).CreateSigningRequest();
            pairing.Enroll(c, R(32), SHA256.HashData(R(32)), 1, "q" + i);
        }
        bool full = false;
        try
        {
            using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var c = new CertificateRequest("CN=q", k, HashAlgorithmName.SHA256).CreateSigningRequest();
            pairing.Enroll(c, R(32), SHA256.HashData(R(32)), 1, "overflow");
        }
        catch (InvalidOperationException) { full = true; }
        Assert(full, "pending queue is capped at " + Pairing.MaxPending);
    }
    finally
    {
        if (caKey is not null) Ca.DeleteKey(caKey);
        if (srvKey is not null) Ca.DeleteKey(srvKey);
        if (clientKeyName is not null) { try { if (CngKey.Exists(clientKeyName, prov)) CngKey.Open(clientKeyName, prov).Delete(); } catch { } }
        try { Directory.Delete(root, true); } catch { }
    }
    Console.WriteLine($"\nS2.3 pairing selftest: PASS={pass} FAIL={fail}");
    return fail > 0 ? 1 : 0;
}
