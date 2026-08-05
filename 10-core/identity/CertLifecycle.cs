// D? · 证书生命周期的【唯一判据】—— 主机侧(服务器叶证书)与客户端侧(设备证书)共用这一份。
//
// ★ 为什么必须是同一份、且是【纯函数】:
//   这个判断有两个执行者(lan-edge 的轮换器 / 客户端的轮换器),而它们分处两个进程、
//   两个语言层、两条车道。一旦各写各的阈值,就会出现「主机以为还早、客户端以为该急了」
//   这种两边说法相反的状态 —— 本项目已经在配对超时上踩过一模一样的形状
//   (客户端 180 秒 vs 主机 5 分钟,见 ClientTransport 里那段注释:主机建了记录、
//   副机早已超时退出,两台机器对同一件事的说法相反)。
//
// ★ 时间【由调用方传进来】,这里绝不读 DateTimeOffset.UtcNow ——
//   ASSERTION-PITFALLS 第 5 条:会随环境漂移的断言训练人去用 --no-verify。
//   纯函数 + 注入的 now = 「到期前 10 天会自动续」这条可以在 3 毫秒内测完,
//   而不是等 80 天。

namespace LocalAI.Identity;

/// <summary>一张证书此刻处在生命周期的哪一段。★ 四段的**处置各不相同**,所以不能合并。</summary>
public enum CertPhase
{
    /// <summary>还早。什么都不用做。</summary>
    Healthy,
    /// <summary>进入自动续签窗口。轮换器该动手了;此时证书**仍然完全可用**,用户不该看到任何告警。</summary>
    RenewDue,
    /// <summary>快到期了而且还没续上 —— 自动续签要么没跑、要么一直失败。★ 这时必须让人看见。</summary>
    Critical,
    /// <summary>已经过期。连接必然失败。</summary>
    Expired,
}

public static class CertLifecycle
{
    /// <summary>服务器叶证书的签发期(D49:30 天)。</summary>
    public const int ServerCertDays = 30;

    /// <summary>设备证书的签发期(Pairing.Approve 用的就是它)。</summary>
    public const int DeviceCertDays = 90;

    /// <summary>
    /// 到期前多少天开始自动续签。
    ///
    /// ★ 为什么是"证书寿命的三分之一"而不是一个写死的天数:
    ///   服务器证书 30 天、设备证书 90 天,同一个绝对天数对两者意义完全不同。
    ///   取三分之一 ⇒ 服务器 10 天、设备 30 天,两者都留出**至少两次**重试机会
    ///   (轮换器每天试一次),而不是"最后一天才想起来"。
    /// ★ D49 原本给的是 status 里 &lt;10 天的**提示**,那条依赖"有人记得去看";
    ///   这里把同一个 10 天变成**动手**的时刻。
    /// </summary>
    public static int RenewBeforeDays(int certLifetimeDays) => Math.Max(1, certLifetimeDays / 3);

    /// <summary>
    /// 「快到期了还没续上」的告警线:到期前多少天开始**惊动用户**。
    /// ★ 它必须**显著晚于** RenewBeforeDays —— 否则自动续签刚进入窗口、还没来得及跑第一次,
    ///   用户就已经收到一条"证书快过期了"的告警,而系统其实正在正常工作。
    ///   那种"正常运转也报警"的告警会在两周内被学会忽略,于是真出事那次也没人看。
    /// </summary>
    public static int AlarmBeforeDays(int certLifetimeDays) => Math.Max(1, certLifetimeDays / 10);

    /// <summary>
    /// 判定一张证书此刻处于哪一段。<paramref name="now"/> 必须由调用方给,便于注入。
    /// </summary>
    public static CertPhase Phase(DateTimeOffset notAfter, DateTimeOffset now, int certLifetimeDays)
    {
        var left = (notAfter - now).TotalDays;
        if (left <= 0) return CertPhase.Expired;
        if (left <= AlarmBeforeDays(certLifetimeDays)) return CertPhase.Critical;
        if (left <= RenewBeforeDays(certLifetimeDays)) return CertPhase.RenewDue;
        return CertPhase.Healthy;
    }

    /// <summary>轮换器该不该动手。RenewDue 与之后的每一段都该动手 —— 包括**已经过期**那一段。</summary>
    /// <remarks>
    /// ★ 过期了也要续:过期不是"来不及了",服务器证书过期后 <c>renew-server</c> 照样能签出新的
    /// (CA 十年有效、私钥还在)。若把 Expired 排除在"该动手"之外,系统会在最需要自愈的那一刻
    /// **恰好停手** —— 而那正是用户唯一会真正受影响的时刻。
    /// </remarks>
    public static bool ShouldRenew(CertPhase p) => p is CertPhase.RenewDue or CertPhase.Critical or CertPhase.Expired;

    /// <summary>要不要让用户看见。Healthy / RenewDue 都**不**打扰用户 —— 后者是系统正在正常自愈。</summary>
    public static bool ShouldAlarm(CertPhase p) => p is CertPhase.Critical or CertPhase.Expired;

    /// <summary>剩余天数(可为负)。界面与 CLI 都用它,免得各写各的四舍五入。</summary>
    public static double DaysLeft(DateTimeOffset notAfter, DateTimeOffset now) => (notAfter - now).TotalDays;
}
