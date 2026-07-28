// P3b S2 -- localai-identity CLI. For S2.1 the only command is `selftest`, which exercises the
// CA + issuance core against throwaway TPM keys (created and deleted, no residue). `init` / store /
// pairing state machine arrive in S2.2/S2.3.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LocalAI.Identity;

if (args.Length == 0 || args[0] != "selftest")
{
    Console.WriteLine("usage: localai-identity selftest");
    return 2;
}

int pass = 0, fail = 0;
void Assert(bool c, string m)
{
    if (c) { pass++; Console.WriteLine("  PASS  " + m); }
    else { fail++; Console.WriteLine("  FAIL  " + m); }
}

var prov = new CngProvider(Ca.TpmProvider);
string suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
string caKeyName = "localai-selftest-ca-" + suffix;
string clientKeyName = "localai-selftest-client-" + suffix;
string hubShort = "k7m4q2dp7n6r5v2x";              // fixed 16-char base32 stand-in for the test
string deviceId = Guid.NewGuid().ToString();
string serverName = $"localai-{hubShort}.local";

X509Certificate2? caCert = null;
try
{
    // --- CA in the TPM ---
    caCert = Ca.CreateCa(caKeyName, "LocalAI Hub CA " + hubShort, years: 10);
    Assert(Ca.CaExists(caKeyName), "CA key created in TPM (non-exportable)");
    var caBc = caCert.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
    Assert(caBc is { CertificateAuthority: true, HasPathLengthConstraint: true, PathLengthConstraint: 0 },
           "CA cert: BasicConstraints CA=true, pathLen=0");

    // fail-closed: creating the same CA again must refuse (no silent overwrite)
    bool refused = false;
    try { using var _ = Ca.CreateCa(caKeyName, "dup", 10); } catch (InvalidOperationException) { refused = true; }
    Assert(refused, "re-creating an existing CA is refused (fail-closed)");

    var caPublic = Ca.PublicOf(caCert);

    // --- server leaf (server key ephemeral here; TPM storage of it is an S2.2 init concern) ---
    using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var serverPub = PublicKey.CreateFromSubjectPublicKeyInfo(serverKey.ExportSubjectPublicKeyInfo(), out _);
    using var serverLeaf = Ca.IssueLeaf(caKeyName, caCert, serverPub, serverName,
                                        dnsSan: serverName, uriSan: null, serverAuth: true, clientAuth: false, days: 30);
    Assert(Ca.VerifyChainAndEku(serverLeaf, caPublic, Ca.OidServerAuth), "server leaf chains to CA + has serverAuth EKU");
    Assert(serverLeaf.GetNameInfo(X509NameType.DnsName, false) == serverName, "server leaf DNS SAN = " + serverName);
    Assert(!Ca.VerifyChainAndEku(serverLeaf, caPublic, Ca.OidClientAuth), "server leaf does NOT carry clientAuth EKU");

    // --- client: TPM key + CSR carrying a MALICIOUS injected extension (CA=true) ---
    var ckParams = new CngKeyCreationParameters
    {
        Provider = prov,
        ExportPolicy = CngExportPolicies.None,
        KeyUsage = CngKeyUsages.Signing,
    };
    using var clientEcdsa = new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, clientKeyName, ckParams));
    var csrReq = new CertificateRequest("CN=whatever-the-client-claims", clientEcdsa, HashAlgorithmName.SHA256);
    csrReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true)); // attacker wants CA=true
    var csrDer = csrReq.CreateSigningRequest();

    var csrPub = Ca.PublicKeyFromCsr(csrDer);   // verifies proof-of-possession
    Assert(true, "client CSR loaded + proof-of-possession verified");

    string uri = "urn:localai:device:" + deviceId;
    using var clientLeaf = Ca.IssueLeaf(caKeyName, caCert, csrPub, "device-" + deviceId[..8],
                                        dnsSan: null, uriSan: uri, serverAuth: false, clientAuth: true, days: 90);
    Assert(Ca.VerifyChainAndEku(clientLeaf, caPublic, Ca.OidClientAuth), "client leaf chains to CA + has clientAuth EKU");
    Assert(Ca.HasUriSan(clientLeaf, uri), "client leaf URI SAN = " + uri);
    var leafBc = clientLeaf.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
    Assert(leafBc is { CertificateAuthority: false },
           "server-generated extensions win: injected CA=true was IGNORED (leaf CA=false)");

    // proof-of-possession must reject a tampered CSR
    var bad = (byte[])csrDer.Clone();
    bad[^1] ^= 0xFF;
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

Console.WriteLine();
Console.WriteLine($"S2.1 identity selftest: PASS={pass} FAIL={fail}");
return fail > 0 ? 1 : 0;
