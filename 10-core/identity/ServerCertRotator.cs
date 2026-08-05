// D? · 服务器证书【自动轮换】状态机(P3b.2 推迟项)。
//
// ─────────────────────────────────────────────────────────────────────────────
// 为什么 D49 还不够
// ─────────────────────────────────────────────────────────────────────────────
// D49 交付的是:手动命令 `renew-server` + `status` 里 <10 天的提示。
// 那两样加起来的意思是 **「要有人记得去看 status」**。
// 而本项目的判词是:**靠人记得的护栏不是护栏**。
// 实机证据:D49 于 2026-07-29 裁定,证书 2026-08-28 到期;到 2026-08-05 勘察时
// `server.cer` 的 LastWriteTime 仍是 07-29 —— 七天里**没有任何人跑过一次 status**。
//
// ─────────────────────────────────────────────────────────────────────────────
// ★★ 崩溃重入:靠「不存半套状态」而不是靠「把半套状态修好」
// ─────────────────────────────────────────────────────────────────────────────
// 本轮换器**没有任何自己的持久状态** —— 没有"上次续到哪儿了"的进度文件、
// 没有"正在续签中"的标志位。每一跳都重新读 `server.cer` 自己的 NotAfter 来决定要不要动手。
//
// 这不是省事,是**唯一**能让"崩溃后重入不留半套状态"成立的形状:
// 只要存在第二份状态,它和证书本身就有对不上的可能,而对不上的那一刻恰好是崩溃之后 ——
// 也就是没人盯着的时候。反过来,证书自己就是进度:它的 NotAfter 变新了就是续成功了,
// 没变就是没续成。**没有第三种可能,所以没有半套状态可留。**
//
// 崩在 RenewServerCert 中途的三个点,逐个走一遍:
//   ① 签出了新证书但还没写 server.cer ⇒ 磁盘上还是旧的 ⇒ 下一跳照常判该续,重签一张。
//      代价:CA 多签了一张没人引用的证书(不进成员表、不进任何信任链),无害。
//   ② 写了 server.cer 但还没更新 locators 的 server_thumbprint ⇒ **不影响加载**:
//      LoadServerCert 是拿 server.cer 自己的 thumbprint 去找的,locators 里那个字段只作记录。
//      下一跳读到新的 NotAfter,判 Healthy,不再动手。
//   ③ 没删掉 CurrentUser\My 里的旧证书 ⇒ D49 点名的那个坑,但它在这里**结构上无害**:
//      查找键是新 thumbprint,旧的那张躺着也不会被选中。(D49 原文担心的是覆盖式更新,
//      这里每次是新 thumbprint,不是同名覆盖。)
//
// ─────────────────────────────────────────────────────────────────────────────
// ★★ fail-closed:轮换器自己坏了,必须【响】
// ─────────────────────────────────────────────────────────────────────────────
// 最危险的失败不是"续签失败",是"续签失败了而没人知道" —— 那会一路静默滑到证书过期,
// 症状变成实测过的那个:客户端显示「中枢没开机」,用户跑去重启一个没病的中枢。
// 所以:失败**不吞**,累计失败次数与最后一条错误都留在 Status 里,由 Edge 打横幅、
// 由 /admin/ping 吐出去。**绝不静默退回手动** —— 退回手动等于退回 D49 那个"要有人记得"的状态,
// 而那正是本状态机要消灭的东西。

namespace LocalAI.Identity;

public enum RotationOutcome
{
    /// <summary>还没到续签窗口,什么都没做。</summary>
    NotDue,
    /// <summary>这一跳真的续签成功了。</summary>
    Renewed,
    /// <summary>该续但续失败了。★ 这个值**必须**被调用方看见并喊出来。</summary>
    Failed,
}

/// <summary>轮换器对外可观测的全部状态。Edge 的横幅与 /admin/ping 都读它。</summary>
public sealed record RotationStatus(
    DateTimeOffset NotAfter,
    double DaysLeft,
    CertPhase Phase,
    int ConsecutiveFailures,
    string? LastError,
    DateTimeOffset? LastRenewedAt)
{
    /// <summary>要不要惊动人。★ 「该续而续不动」比「快到期」更值得响 —— 后者系统还在自愈,前者没有。</summary>
    public bool NeedsAttention => ConsecutiveFailures > 0 || CertLifecycle.ShouldAlarm(Phase);
}

/// <summary>
/// 服务器叶证书的自动轮换。**时间与续签动作都由外部注入**,所以整台状态机可以在
/// 毫秒内被测完,不必等 20 天(ASSERTION-PITFALLS 第 5 条:注入,不要读真实环境)。
/// </summary>
public sealed class ServerCertRotator
{
    readonly Func<DateTimeOffset> _readNotAfter;
    readonly Action _renew;
    readonly object _gate = new();

    int _fails;
    string? _lastError;
    DateTimeOffset? _lastRenewedAt;

    /// <param name="readNotAfter">读当前 server.cer 的到期时间。每一跳都重读 —— 这就是"进度"。</param>
    /// <param name="renew">执行一次续签(生产里是 Identity.RenewServerCert)。</param>
    public ServerCertRotator(Func<DateTimeOffset> readNotAfter, Action renew)
    {
        _readNotAfter = readNotAfter;
        _renew = renew;
    }

    /// <summary>
    /// 连续失败多少次之后,认定"自动轮换这条路已经走不通了",必须由人介入。
    /// ★ 到达之后**依然继续重试** —— 只是把 Status 喊得更大声。
    ///   停止重试等于静默退回手动,而没有任何东西保证那个"人"会出现。
    /// </summary>
    public const int FailuresBeforeLoud = 3;

    /// <summary>跑一跳。返回这一跳做了什么。<paramref name="now"/> 注入。</summary>
    public RotationOutcome Tick(DateTimeOffset now)
    {
        lock (_gate)
        {
            DateTimeOffset notAfter;
            try { notAfter = _readNotAfter(); }
            catch (Exception ex)
            {
                // 连证书都读不出来 —— 这比续签失败更糟,同样必须响,绝不当成 NotDue。
                _fails++;
                _lastError = "读不到服务器证书到期时间: " + ex.Message;
                return RotationOutcome.Failed;
            }

            var phase = CertLifecycle.Phase(notAfter, now, CertLifecycle.ServerCertDays);
            if (!CertLifecycle.ShouldRenew(phase)) return RotationOutcome.NotDue;

            try
            {
                _renew();
                // ★ 复核:续完之后**重新读一遍磁盘**,确认到期时间真的往后走了。
                //   不复核的话,一个"没抛异常但其实什么也没做"的续签实现会被记成成功,
                //   而那正是本项目最恨的假绿 —— 它会一路静默滑到证书真的过期。
                var after = _readNotAfter();
                if (after <= notAfter)
                {
                    _fails++;
                    _lastError = $"续签没有抛错,但到期时间没有前进({notAfter:u} -> {after:u})—— 当作失败处理";
                    return RotationOutcome.Failed;
                }
                _fails = 0;
                _lastError = null;
                _lastRenewedAt = now;
                return RotationOutcome.Renewed;
            }
            catch (Exception ex)
            {
                _fails++;
                _lastError = ex.Message;
                return RotationOutcome.Failed;   // ★ 不吞、不降级、不"下次再说"
            }
        }
    }

    public RotationStatus Status(DateTimeOffset now)
    {
        lock (_gate)
        {
            DateTimeOffset notAfter;
            try { notAfter = _readNotAfter(); }
            catch { return new RotationStatus(default, double.NaN, CertPhase.Expired, Math.Max(_fails, 1), _lastError ?? "读不到证书", _lastRenewedAt); }
            return new RotationStatus(notAfter, CertLifecycle.DaysLeft(notAfter, now),
                                      CertLifecycle.Phase(notAfter, now, CertLifecycle.ServerCertDays),
                                      _fails, _lastError, _lastRenewedAt);
        }
    }

    /// <summary>给人看的一行字。★ 解析用的判据一律 ASCII(ASSERTION-PITFALLS 第 8 条),所以带上 ROTATE= 前缀。</summary>
    public static string Banner(RotationStatus s) =>
        $"ROTATE={s.Phase} days_left={s.DaysLeft:F1} fails={s.ConsecutiveFailures}"
        + (s.LastError is null ? "" : $" last_error={s.LastError}");
}
