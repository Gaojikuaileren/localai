// D? · 设备证书续签状态机(P3b 决策包 §6.3「客户端续期网络协议」的落地,P3b.2 推迟项)。
//
// ─────────────────────────────────────────────────────────────────────────────
// 为什么这件事必须存在
// ─────────────────────────────────────────────────────────────────────────────
// 设备证书 90 天(Pairing.Approve 的 days: 90),而在此之前**全仓没有任何续签路径** ——
// D49 只做了服务器证书那一半。到期之后设备的唯一出路是「重新配对」,而重新配对会
// 删掉本机私钥(HubClient.UnpairLocal / Transport.Pair 开头那段),**亲手销毁一个
// 本来完全有效的身份**,主机侧还留一条幽灵设备记录。D49 原话是拿它形容服务器证书的,
// 但设备证书这一半一字不差地成立。
//
// ★ 实测(2026-08-05)设备证书过期的症状比服务器证书那一半**更难归因**:
//   过期的客户端证书在 **TLS 握手层**就被 ValidateClient 判死(X509Chain 判 NotTimeValid),
//   连接直接断 —— 于是它连一个 HTTP 状态码都没有,成员表那句 IsActive 一次都跑不到。
//   客户端拿到的是 Win32Exception「证书链是由不受信任的颁发机构颁发的」(还是本地化的),
//   最终落进 HubState.Offline = 「中枢没开机」。用户会一趟趟跑去主机重启 Edge,而主机没病。
//
// ─────────────────────────────────────────────────────────────────────────────
// 与「初次配对」的根本区别:**不经过配对流程,没有六词,没有人工批准**
// ─────────────────────────────────────────────────────────────────────────────
// 六词 SAS 要解决的是「我现在面对的这个中枢是不是真的那一个」—— 那是一个
// **首次建立信任**的问题。续签时信任【早就建好了】:同一个 device_id、同一个 CA、
// 同一把私钥。认证靠的是【旧证书的 mTLS】—— 能拿旧私钥握上手,就证明你还是你。
// 让续签走配对流程会:① 要人守在主机前批准(那正是"靠人记得的护栏不是护栏");
// ② 换 device_id,把设备列表越堆越长;③ 重新生成密钥,销毁既有身份。
//
// ★ 续签**不能提权、不能改身份**:device_id 由旧证书在成员表里的行**反查**得出,
//   不由客户端自报(项目纪律:自报值只作显示、永不作判据)。
//
// ─────────────────────────────────────────────────────────────────────────────
// 两条路由,不是四条(对 §6.3 的**有意偏离**,理由如下)
// ─────────────────────────────────────────────────────────────────────────────
// §6.3 设计了 enroll/status/claim/complete 四条。其中 status + claim 存在的前提是
// 「签发可能是异步的、要等」——而本实现里 registry 与 signer 同进程、签发是同步的,
// enroll 当场就能把候选证书给出去。硬留两条永远立刻返回 candidate_ready 的路由,
// 是在协议上伪造一个并不存在的等待状态。
// ⇒ 保留 enroll(旧证书 mTLS + 新 CSR PoP)与 complete(**候选证书** mTLS)两条。
//   complete 之所以不能省:它是**新证书真的能用**的证据。在拿到这个证据之前
//   绝不退休旧证书 —— 这条顺序是整个设计里唯一防住「续签把自己续死」的东西。
//
// ★ 与 §6.3 的另一处偏离:**复用同一把设备私钥**,不生成新密钥。
//   §6.2 原文是「生成新的 TPM key + CSR」。这里跟 D49 的服务器证书续签保持一致
//   (「复用同一把服务器密钥…不换密钥 = 不触碰任何已建立的信任」)。
//   代价如实记账:**私钥不轮换**。收益是崩溃重入的状态空间小一个量级 ——
//   不换密钥就没有"新密钥已建、档案还指着旧的"这一类半截状态,也不会留孤儿 CNG 密钥
//   (那个坑 Pair() 已经踩过一次,见它开头清理旧 KeyName 那段)。
//   这是**裁定**,不是疏漏;要改回密钥轮换须另立决议。

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace LocalAI.Identity;

public sealed record RenewEnrollResult(string RenewalId, byte[] CandidateDer, string CandidateSha256, DateTimeOffset NotAfter);

public sealed class Renewal
{
    /// <summary>
    /// 一次续签从签出候选到完成切换的时限。§6.3 给的是 30 分钟。
    /// ★ 超时之后候选证书作废(由 <see cref="Store.SweepStaleRenewalCandidates"/> 落地),
    ///   而**旧证书继续 active** —— 失败的续签不该让设备掉线。
    /// </summary>
    public static readonly TimeSpan RenewalTtl = TimeSpan.FromMinutes(30);

    readonly string _idDir;
    readonly string _caKeyName;
    readonly X509Certificate2 _caCert;
    readonly object _gate = new();
    // deviceId -> 这台设备当前那一次未完成的续签。★ 一台设备**最多一条**:
    // 不设上限的话,一个反复重试的客户端能把候选证书堆成第二个"同一台机器 6 条记录"。
    readonly Dictionary<string, (string RenewalId, string CandidateSha, byte[] CandidateDer, byte[] NewSpkiSha, DateTimeOffset At, DateTimeOffset NotAfter)> _live = new();

    public Renewal(string identityDir, string secretsDir)
    {
        _idDir = identityDir;
        _caCert = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(Path.Combine(identityDir, "ca.cer")));
        var loc = JsonDocument.Parse(File.ReadAllText(Path.Combine(secretsDir, "identity-locators.json"))).RootElement;
        _caKeyName = loc.GetProperty("ca_key_name").GetString()!;
    }

    /// <summary>
    /// 续签第一步。<paramref name="presentedCertSha256"/> 是 TLS 层**已验证**的旧证书指纹
    /// —— 它是这次调用的全部授权依据,调用方不得用任何自报值代替。
    /// </summary>
    /// <param name="now">注入的当前时间(测试要造"明天就过期的证书"这种样本)。</param>
    public RenewEnrollResult Enroll(string presentedCertSha256, byte[] newCsrDer, DateTimeOffset now)
    {
        lock (_gate)
        {
            var store = Store.LoadOrEmpty(_idDir);

            // ★★ fail-closed 的核心:只有**当前仍然 active** 的证书才能发起续签。
            //   已 revoked / 已 superseded / 还是 candidate 的一律不行 —— 否则
            //   一张被机主吊销的旧证书能靠续签给自己换一张新的,把"解除设备"整个架空。
            if (!store.IsActive(presentedCertSha256))
                throw new UnauthorizedAccessException("renewal requires a currently-active device certificate");

            var deviceId = store.DeviceIdOfCert(presentedCertSha256)
                           ?? throw new UnauthorizedAccessException("presented certificate is not in the membership store");

            var csrPub = Ca.PublicKeyFromCsr(newCsrDer);   // 验 PoP:证明发起方持有新公钥对应的私钥
            var newSpkiSha = SHA256.HashData(csrPub.ExportSubjectPublicKeyInfo());

            // ★ 幂等:同一台设备 + 同一个 CSR 且还在 TTL 内 ⇒ 返回**同一张**候选证书。
            //   不这么做的话,一个丢了响应正在重试的客户端每重试一次就多签一张证。
            if (_live.TryGetValue(deviceId, out var live) && now - live.At <= RenewalTtl &&
                CryptographicOperations.FixedTimeEquals(live.NewSpkiSha, newSpkiSha) &&
                store.Certs.Any(c => c.CertSha256 == live.CandidateSha && c.Status == "candidate"))
                // ★ 返回**同一张**证书的原始字节 —— 不是重新签一张"内容差不多"的。
                //   重签会换 serial 与指纹,于是客户端第二次拿到的 candidateSha 与第一次不同,
                //   而成员表里两张都躺着 candidate:幂等就成了假的。
                return new RenewEnrollResult(live.RenewalId, live.CandidateDer, live.CandidateSha, live.NotAfter);

            var renewalId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var uri = "urn:localai:device:" + deviceId;   // ★ 同一个 device_id —— 续签不换身份
            using var cert = Ca.IssueLeafWindow(_caKeyName, _caCert, csrPub, "device-" + deviceId[..8],
                                                dnsSan: null, uriSan: uri, serverAuth: false, clientAuth: true,
                                                notBefore: now.AddMinutes(-5),
                                                notAfter: now.AddDays(CertLifecycle.DeviceCertDays));
            var der = cert.RawData;
            var sha = Convert.ToHexString(SHA256.HashData(der));

            Store.Mutate(_idDir, s =>
            {
                // ★ 先把这台设备**上一次没走完**的候选作废,再建新的 —— 同上,防堆积。
                if (_live.TryGetValue(deviceId, out var prev))
                    foreach (var c in s.Certs.Where(x => x.CertSha256 == prev.CandidateSha && x.Status == "candidate"))
                        c.Status = "revoked";
                s.SweepStaleRenewalCandidates(RenewalTtl, now);
                s.AddCandidate(deviceId, cert.SerialNumber, sha, Convert.ToHexString(newSpkiSha),
                               cert.NotBefore.ToString("O"), cert.NotAfter.ToString("O"));
            });

            _live[deviceId] = (renewalId, sha, der, newSpkiSha, now, cert.NotAfter);
            return new RenewEnrollResult(renewalId, der, sha, cert.NotAfter);
        }
    }

    /// <summary>
    /// 续签第二步:用**新证书**握手来证明它真的能用,然后才原子地把旧证书退休。
    /// <paramref name="presentedCertSha256"/> 同样是 TLS 层已验证的指纹。
    /// </summary>
    /// <returns>true = 本次真的完成了切换;false = 之前已完成(幂等重入)。</returns>
    public bool Complete(string renewalId, string presentedCertSha256)
    {
        lock (_gate)
        {
            var store = Store.LoadOrEmpty(_idDir);
            var deviceId = store.DeviceIdOfCert(presentedCertSha256)
                           ?? throw new UnauthorizedAccessException("presented certificate is unknown");

            // ★ 出示的必须**正好是**这次续签签出的那一张。少了这一条,任何一张 candidate
            //   都能拿任意 renewalId 去激活自己 —— 那正是 /pair/complete 里同款的 PoP 绑定。
            if (_live.TryGetValue(deviceId, out var live))
            {
                if (!string.Equals(live.RenewalId, renewalId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(live.CandidateSha, presentedCertSha256, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("presented cert is not the candidate for this renewal");
            }
            // _live 里没有 = Edge 重启过(状态是进程内的)。此时**不拒绝**:
            // ★ 判据换成成员表里那张 candidate 行本身 —— 它是跨重启持久的、且同样只能由
            //   持有新私钥的人握手出示。拒绝的话,一次「续签成功但 Edge 恰好重启」会把
            //   客户端永久卡在"拿着一张永远激活不了的候选证"上,而它的旧证书正在走向过期。

            var changed = Store.Mutate(_idDir, s => s.CompleteRenewal(deviceId, presentedCertSha256));
            if (changed) _live.Remove(deviceId);
            return changed;
        }
    }

    /// <summary>本进程内还有几条未完成的续签(自检与 /admin 观测用)。</summary>
    public int LiveCount { get { lock (_gate) return _live.Count; } }

    // ★ 这里【没有】配对那种"挑战签名"(Pairing.BuildChallenge)。不是漏了,是不需要:
    //   §6.3 要求「初次配对与续期使用不同状态表、context 和路由授权,不能互相替代」——
    //   本实现用**两条独立路由 + 两张独立状态表**满足它,而两步的授权都落在 TLS 层:
    //     · enroll   要求出示【当前 active 的旧证书】;
    //     · complete 要求出示【正好是这次签出的那张候选证书】。
    //   复用同一把密钥之后,再加一层"用同一把私钥签个串"证明不了任何 mTLS 没证明的事,
    //   只会多一段看起来很安全、实则恒真的仪式 —— 那种东西比没有更坏。
}
