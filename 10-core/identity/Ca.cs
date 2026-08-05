// P3b S2 -- LocalAI hub CA + certificate issuance (the "signer" core). LocalAI, decision D43.
// CA private key is NON-EXPORTABLE in the TPM (D43 S0.7). Leaves are issued with SERVER-generated
// extensions only; a CSR's own extensions are ignored (packet §6.1). Proof-of-possession on the CSR
// is verified before issuance.
//
// Under 精简优先 (D43 S0.10) keys are CurrentUser-scoped TPM containers; the signer-service-account
// ACL isolation is P3b.2. The non-exportability property already holds now.

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LocalAI.Identity;

public static class Ca
{
    public const string TpmProvider = "Microsoft Platform Crypto Provider";
    public const string SoftwareProvider = "Microsoft Software Key Storage Provider";
    // ★ B17/D44: TLS leaf keys (server + client) use the software KSP -- SChannel cannot use TPM keys
    //   as TLS credentials on this platform. They are still ExportPolicy=None (non-exportable). The CA
    //   key stays in the TPM (CngKey.Create in CreateCa below).
    public const string TlsKeyProvider = SoftwareProvider;
    public const string OidServerAuth = "1.3.6.1.5.5.7.3.1";
    public const string OidClientAuth = "1.3.6.1.5.5.7.3.2";

    static CngProvider Prov => new(TpmProvider);

    public static bool CaExists(string keyName) => CngKey.Exists(keyName, Prov);

    // provider-agnostic: the key may live in the TPM (CA) or the software KSP (TLS leaves).
    public static void DeleteKey(string keyName)
    {
        foreach (var p in new[] { new CngProvider(TpmProvider), new CngProvider(SoftwareProvider) })
            if (CngKey.Exists(keyName, p)) { CngKey.Open(keyName, p).Delete(); return; }
    }

    public static X509Certificate2 PublicOf(X509Certificate2 cert)
        => X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));

    // Create a persistent, non-exportable P-256 CA key in the TPM and a self-signed CA cert.
    // Fail-closed: refuses to overwrite an existing key of the same name (packet §4.1 conflict rule).
    public static X509Certificate2 CreateCa(string keyName, string cn, int years)
    {
        if (CngKey.Exists(keyName, Prov))
            throw new InvalidOperationException("CA key already exists (fail-closed, no overwrite): " + keyName);
        var p = new CngKeyCreationParameters
        {
            Provider = Prov,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
        };
        var key = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, p);
        using var ecdsa = new ECDsaCng(key);
        var req = new CertificateRequest("CN=" + cn, ecdsa, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(years));
    }

    // Issue a leaf against a subject public key. ALL extensions are generated here; nothing from a
    // CSR is copied. The CA key is opened from the TPM only to sign.
    public static X509Certificate2 IssueLeaf(string caKeyName, X509Certificate2 caCert, PublicKey subjectPublicKey,
        string subjectCn, string? dnsSan, string? uriSan, bool serverAuth, bool clientAuth, int days)
        => IssueLeafWindow(caKeyName, caCert, subjectPublicKey, subjectCn, dnsSan, uriSan, serverAuth, clientAuth,
                           DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(days));

    /// <summary>
    /// 同 <see cref="IssueLeaf"/>,但**有效期窗口由调用方给定**。
    ///
    /// ★ 为什么需要它(而不是只留 `days`):证书生命周期的断言必须能造出
    ///   「昨天就过期了」「三天后到期」这种**样本**。按 ASSERTION-PITFALLS 第 5 条,
    ///   这类外部量要【注入】而不是去等真实时间 —— 否则"到期前 N 天自动续"这条
    ///   要等 80 天才能测一次,等于测不了。
    /// ★ 它**不是**旁路开关:签发策略(服务端生成全部扩展、CSR 自带扩展一律忽略、
    ///   PoP 已在 PublicKeyFromCsr 验过)一个字没松。CLI 上也没有任何命令能到达它。
    /// </summary>
    public static X509Certificate2 IssueLeafWindow(string caKeyName, X509Certificate2 caCert, PublicKey subjectPublicKey,
        string subjectCn, string? dnsSan, string? uriSan, bool serverAuth, bool clientAuth,
        DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        var req = new CertificateRequest(new X500DistinguishedName("CN=" + subjectCn),
                                         subjectPublicKey, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var eku = new OidCollection();
        if (serverAuth) eku.Add(new Oid(OidServerAuth));
        if (clientAuth) eku.Add(new Oid(OidClientAuth));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, true));
        var san = new SubjectAlternativeNameBuilder();
        bool anySan = false;
        if (dnsSan is not null) { san.AddDnsName(dnsSan); anySan = true; }
        if (uriSan is not null) { san.AddUri(new Uri(uriSan)); anySan = true; }
        if (anySan) req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(caCert, true, false));

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        using var caKey = new ECDsaCng(CngKey.Open(caKeyName, Prov));
        var gen = X509SignatureGenerator.CreateForECDsa(caKey);
        return req.Create(caCert.SubjectName, gen, notBefore, notAfter, serial);
    }

    // Load a PKCS#10 CSR and VERIFY proof-of-possession (the signature over the CSR). Returns the
    // subject public key. Throws if the signature is invalid.
    public static PublicKey PublicKeyFromCsr(byte[] csrDer)
    {
        var csr = CertificateRequest.LoadSigningRequest(
            csrDer, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default);
        return csr.PublicKey;
    }

    /// <param name="at">
    /// 按哪个时刻校验有效期。★ 默认 null = **此刻**,生产路径的行为一个字没变
    /// (ValidateClient 必须按当下判,否则过期证书就能进来)。
    /// 只有测试会传值 —— 用来校验一张"60 天后才生效"的续签证书,
    /// 否则它会因为 NotTimeValid 被判失败,而那是测试把时间点定错了、不是证书有问题。
    /// </param>
    public static bool VerifyChainAndEku(X509Certificate2 leaf, X509Certificate2 caPublic, string requiredEku,
                                         DateTimeOffset? at = null)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.CustomTrustStore.Add(caPublic);
        if (at is not null) chain.ChainPolicy.VerificationTime = at.Value.UtcDateTime;
        if (!chain.Build(leaf)) return false;
        foreach (var ext in leaf.Extensions)
            if (ext is X509EnhancedKeyUsageExtension e)
                foreach (var o in e.EnhancedKeyUsages)
                    if (o.Value == requiredEku) return true;
        return false;
    }

    // SubjectAltName is a SEQUENCE OF GeneralName; a URI is [6] IMPLICIT IA5String. The framework's
    // X509SubjectAlternativeNameExtension only enumerates DNS/IP, so parse the URI GeneralName here.
    public static bool HasUriSan(X509Certificate2 leaf, string uri)
    {
        var raw = leaf.Extensions["2.5.29.17"];
        if (raw is null) return false;
        try
        {
            var seq = new AsnReader(raw.RawData, AsnEncodingRules.DER).ReadSequence();
            var uriTag = new Asn1Tag(TagClass.ContextSpecific, 6);
            while (seq.HasData)
            {
                var tag = seq.PeekTag();
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 6)
                {
                    if (seq.ReadCharacterString(UniversalTagNumber.IA5String, uriTag) == uri) return true;
                }
                else seq.ReadEncodedValue();
            }
        }
        catch { }
        return false;
    }
}
