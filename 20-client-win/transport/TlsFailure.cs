// D? · TLS 失败的【四分类】—— 四种症状都是"连不上",处置各不相同。
//
// ─────────────────────────────────────────────────────────────────────────────
// 2026-08-05 实测:原来那套判据在【本机设备证书过期】这一格上是**空的**
// ─────────────────────────────────────────────────────────────────────────────
// 四例实机复现(在场证据,不是推断):
//
//   | 情形                     | 客户端拿到的异常链                                             |
//   |--------------------------|----------------------------------------------------------------|
//   | 有效设备证书             | HTTP 200                                                       |
//   | **本机设备证书过期**     | HttpRequestException -> IOException -> Win32Exception          |
//   |                          |   「证书链是由不受信任的颁发机构颁发的。」                     |
//   | 不带证书                 | HTTP 401                                                       |
//   | 主机服务器证书过期       | AuthenticationException:「...certificate chain: NotTimeValid」 |
//
// HubClient.ClassifyTlsFailure 原来的两轮判据(找 UntrustedRoot/PartialChain/expired/
// NotTimeValid 这些**英文词**,再兜底找 AuthenticationException)对第二行**一条都命中不了**:
//   ① 异常链里根本没有 AuthenticationException —— TLS 1.3 下服务端的 alert 在首次读时才到,
//      包成 IOException,兜底那一轮扑空;
//   ② Win32 层那句话是**本地化**的(本机是中文),英文针对不上。
//      ★ 而且换英文机器也不行:英文原文是 "The certificate chain was issued by an authority
//        that is not trusted.",里面没有 "UntrustedRoot" 这个词。
//   ⇒ 返回 null ⇒ 调用方归到 Offline ⇒ 界面说「中枢没开机」。
//      用户会一趟趟跑去主机重启 Edge、查防火墙 —— 而主机完全正常。
//
// ★ 更坏的一层:Windows 把这次失败报成「不受信任的颁发机构」,而真正的原因是**时间**
//   (服务端 X509Chain 判的是 NotTimeValid)。所以就算有人把针本地化了,
//   它也会把这次失败判成 HubIdentityChanged =「必须重新配对」—— 而重新配对会删掉本机私钥。
//   **照着这条错误线索走,结局是亲手销毁一个只需要续签的身份。**
//
// ─────────────────────────────────────────────────────────────────────────────
// ★★ 因此本分类器【不靠异常文本认本机证书过期】
// ─────────────────────────────────────────────────────────────────────────────
// 本机设备证书**就在本机档案里**,它的 NotAfter 是一个可以直接读出来的事实。
// 拿一个确定的本地事实,去换一句会随 .NET 版本、TLS 版本、系统语言漂移的错误消息,
// 是没有道理的。⇒ 第一步先看本机证书,只有本机证书没问题时才去解读异常文本。
//
// ★ 这也顺带满足了「过期**之前**就要看得见」:同一个本地事实,配合 CertLifecycle 的相位,
//   在还能连上的时候就能提前告警,而不是等握手失败之后再来归因。

using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using LocalAI.Identity;

namespace LocalAI.ClientTransport;

/// <summary>连不上的四种根因。★ 四种的处置**互不相同**,任何两种合并都会把人支错方向。</summary>
public enum TlsFailureKind
{
    /// <summary>判不出来。★ 调用方应归到普通的"连不上",**不要**猜一个具体原因。</summary>
    Unknown,
    /// <summary>主机的服务器证书过期 ⇒ 在主机上 <c>renew-server</c>,**不必**重新配对。</summary>
    ServerCertExpired,
    /// <summary>★ 本机的设备证书过期 ⇒ 走设备证书续签,**同样不必**重新配对。这一格原先是空的。</summary>
    LocalDeviceCertExpired,
    /// <summary>链不到配对时钉住的那个 CA ⇒ 对面不是你配对的中枢,**重新配对是唯一出路**。</summary>
    HubIdentityChanged,
}

public static class TlsFailure
{
    /// <summary>从档案里读出本机设备证书的到期时间。档案坏/没配对时返回 null。</summary>
    public static DateTimeOffset? LocalCertNotAfter(ClientProfile? p)
    {
        if (string.IsNullOrWhiteSpace(p?.DeviceCertB64)) return null;
        try
        {
            using var c = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(p!.DeviceCertB64));
            return c.NotAfter;
        }
        catch { return null; }
    }

    /// <summary>
    /// 本机设备证书此刻处在哪一段。★ 这是「过期**之前**就看得见」的那一半 ——
    /// 不需要等任何一次失败,连着的时候就能算。
    /// </summary>
    public static CertPhase? LocalCertPhase(ClientProfile? p, DateTimeOffset now)
    {
        var na = LocalCertNotAfter(p);
        return na is null ? null : CertLifecycle.Phase(na.Value, now, CertLifecycle.DeviceCertDays);
    }

    /// <summary>
    /// 判定一次连接失败的根因。
    ///
    /// ★ 顺序是承重的:**先查本机证书这个确定事实**,再去解读会漂移的异常文本。
    ///   反过来的话,本机证书过期那一次会被异常里那句"不受信任的颁发机构"
    ///   抢先判成 HubIdentityChanged =「必须重新配对」,而那会删掉本机私钥。
    /// </summary>
    /// <param name="ex">连接失败抛出的异常(可为 null:纯做本机体检时)。</param>
    /// <param name="profile">本机配对档案 —— 设备证书就在里面。</param>
    /// <param name="now">注入的当前时间。</param>
    public static TlsFailureKind Classify(Exception? ex, ClientProfile? profile, DateTimeOffset now)
    {
        // ① 本机设备证书已经过期?这是本地事实,不受语言、.NET 版本、TLS 版本影响。
        if (LocalCertNotAfter(profile) is { } notAfter && notAfter <= now)
            return TlsFailureKind.LocalDeviceCertExpired;

        if (ex is null) return TlsFailureKind.Unknown;

        // ② 服务器证书过期。这一句是 **.NET 自己拼的**(不是 Win32 的),所以恒为英文、
        //    且带明确的 NotTimeValid —— 实测形如
        //    "The remote certificate is invalid because of errors in the certificate chain: NotTimeValid"。
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var m = e.Message ?? "";
            if (m.Contains("NotTimeValid", StringComparison.OrdinalIgnoreCase))
                return TlsFailureKind.ServerCertExpired;
        }

        // ③ 链不到钉住的 CA。同样是 .NET 自拼的链状态词。
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var m = e.Message ?? "";
            if (m.Contains("UntrustedRoot", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("PartialChain", StringComparison.OrdinalIgnoreCase))
                return TlsFailureKind.HubIdentityChanged;
        }

        // ④ 只知道"TLS 没握成",但不知道是哪一种 ⇒ **别下结论**。
        // ★ 这里【不再】把裸的 AuthenticationException 兜底成 HubIdentityChanged:
        //   那个兜底原本是想接住"链不通",实测却接不住任何一种真实情形,
        //   反而有把别的失败误判成"必须重新配对"的风险 —— 而那条建议是破坏性的。
        //   宁可说"不知道",也不要给一个会让人删掉私钥的假结论。
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is AuthenticationException) return TlsFailureKind.Unknown;
        return TlsFailureKind.Unknown;
    }

    /// <summary>四种根因各自的处置文案。★ 每一句都要说清**下一步做什么**,而不只是描述症状。</summary>
    public static string Explain(TlsFailureKind kind) => kind switch
    {
        TlsFailureKind.ServerCertExpired =>
            "主机的服务器证书已过期 —— 在主机上续签(localai-identity renew-server)即可,不必重新配对。",
        TlsFailureKind.LocalDeviceCertExpired =>
            "本机的设备证书已过期 —— 本机会自动续签;若一直没成功,请确认中枢在线。"
            + "★ 不要点「重新配对」:那会删掉本机私钥,把一个只需要续签的身份亲手销毁。",
        TlsFailureKind.HubIdentityChanged =>
            "连上了,但对面的证书链不到你配对时钉住的那个中枢 —— 可能是主机重铸了身份,"
            + "或者这个地址上是另一台机器。这种情况【必须重新配对】。",
        _ => "连不上中枢(原因未能判定)。先确认中枢已开机、地址正确。",
    };
}
