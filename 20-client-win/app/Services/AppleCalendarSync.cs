// P3c -- Apple 日历同步的编排层:把「凭据 → CalDAV 拉取 → 增量合并」串起来。
//
// ★ 只读拉取(用户裁定 2026-07-31):只把 iCloud 上有的合并进本机,【绝不写回 Apple】。
//   合并规则不在这里 —— 它在 CalendarData.MergeInto(D50 补充,已被 selftest 钉死):
//   已有的不重复加、同 UID 不覆盖、空日程不并入。这里只负责"取来、交给它、如实汇报"。

namespace LocalAI.Client.Services;

/// <param name="Ok">整次是否算成功(有任意一个日历拉成功即算,失败的单列)。</param>
/// <param name="Added">真正【新增】到本机的条数(合并后的净增,不是拉回来的条数)。</param>
/// <param name="Fetched">从 Apple 取到的条数。</param>
/// <param name="Skipped">解析不了而跳过的条数。</param>
/// <param name="Message">给人看的一句话。</param>
/// <param name="AuthFailed">
/// 这一次是因【认证】而失败(401/403 或密码解不开)。
/// ★★ 自动拉取必须据此【立即熔断】—— iCloud 按用户名节流,
///   定时反复撞 401 会把用户真实的 Apple ID 锁掉。这不是优化,是安全要求。
/// </param>
public sealed record AppleSyncResult(bool Ok, int Added, int Fetched, int Skipped, string Message, bool AuthFailed = false);

public static class AppleCalendarSync
{
    /// <summary>正在同步 —— 界面据此禁用按钮,避免并发两次拉取。</summary>
    public static bool Busy { get; private set; }

    /// <summary>
    /// 拉一次。★ 没配账号 / 没选日历 时【如实返回原因】而不是假装成功。
    /// </summary>
    public static async Task<AppleSyncResult> PullAsync(
        AppSettings settings, string owner, string scope, CancellationToken ct = default)
    {
        if (Busy) return new AppleSyncResult(false, 0, 0, 0, "上一次同步还在进行中。");

        var acct = AppleCredentials.Load();
        if (acct is null || !acct.HasPassword)
            return new AppleSyncResult(false, 0, 0, 0, "还没有连接 Apple 账号。");

        var pwd = AppleCredentials.Reveal();
        if (pwd is null)
            return new AppleSyncResult(false, 0, 0, 0,
                "读不出已保存的专用密码 —— 多半是换了 Windows 用户或把配置拷来的(密码按当前用户加密)。请重新填一次。",
                AuthFailed: true);

        if (settings.AppleCalendarUrls.Count == 0 && settings.AppleReminderUrls.Count == 0)
            return new AppleSyncResult(false, 0, 0, 0, "还没有选择要同步的日历或提醒事项。");

        Busy = true;
        try
        {
            // 先发现一次 —— 拿到日历的当前名字与 URL(用户可能在 iCloud 那边改过名/删过)
            var (dok, dmsg, cals, rems) = await AppleCalDav.DiscoverAsync(acct.AppleId, pwd, ct);
            // ★ 认证类失败要标出来 —— 自动拉取据此熔断,不能定时反复撞(会锁账号)。
            if (!dok) return new AppleSyncResult(false, 0, 0, 0, dmsg,
                AuthFailed: dmsg.Contains("401") || dmsg.Contains("403"));

            // ★★ 拉取时就把【分类表】一并刷新 —— 它才是权威。
            //   之前只有设置页手点"刷新清单"才更新:在 iCloud 里改个日历名或换个颜色之后,
            //   新拉回来的日程带的是新名字,颜色表却还是旧名字 —— 那批日程落到"认不出的分类",
            //   拿的是按名字算的颜色,与 Apple 那边毫无关系,而且不手动刷新就永远不会自愈。
            CalendarGroups.SetFromApple(cals.Select(c => (c.DisplayName, c.ColorHex)));
            settings.AppleCalendarList = cals
                .Select(c => c.Url + "|" + c.DisplayName + (c.ColorHex is null ? "" : "|" + c.ColorHex))
                .ToList();

            var wanted = cals.Where(c => settings.AppleCalendarUrls.Contains(c.Url)).ToList();
            var wantedTodos = rems.Where(c => settings.AppleReminderUrls.Contains(c.Url)).ToList();
            if (wanted.Count == 0 && wantedTodos.Count == 0)
                return new AppleSyncResult(false, 0, 0, 0,
                    "选中的日历/清单在 Apple 那边找不到了(可能被改名或删除)。请重新选择。");

            var from = DateTime.Today.AddDays(-Math.Abs(settings.AppleSyncPastDays));
            var to = DateTime.Today.AddDays(Math.Abs(settings.AppleSyncFutureDays));

            var all = new List<Views.CalendarEvent>();
            var fetched = 0; var skipped = 0;
            var failures = new List<string>();

            foreach (var cal in wanted)
            {
                ct.ThrowIfCancellationRequested();
                var (ok, msg, evs, sk) = await AppleCalDav.FetchAsync(
                    acct.AppleId, pwd, cal, from, to, owner, scope, ct);
                if (!ok) { failures.Add(msg); continue; }
                all.AddRange(evs);
                fetched += evs.Count;
                skipped += sk;
            }

            // —— 提醒事项(VTODO)—— 与日历同一趟、同一套合并规则
            var allTodos = new List<TodoItem>();
            foreach (var lst in wantedTodos)
            {
                ct.ThrowIfCancellationRequested();
                var (ok, msg, ts, sk, _) = await AppleCalDav.FetchTodosAsync(
                    acct.AppleId, pwd, lst, TodoKind.Personal, ct);
                if (!ok) { failures.Add(msg); continue; }
                allTodos.AddRange(ts);
                fetched += ts.Count;
                skipped += sk;
            }

            // ★ 一条都没成功 -> 如实说失败,不要因为"没报错"就显示成功
            if (all.Count == 0 && allTodos.Count == 0 && failures.Count > 0)
                return new AppleSyncResult(false, 0, 0, skipped, string.Join(" ", failures));

            // 交给已被钉死的合并层:不重复、不覆盖、不并入空条目
            var added = Views.CalendarData.MergeIn(all);
            var addedTodos = ((App)System.Windows.Application.Current).Todos.MergeIn(allTodos);
            added += addedTodos;

            settings.AppleLastSync = DateTime.Now;
            settings.Save();

            var extra = failures.Count > 0 ? $" 有 {failures.Count} 个日历没取成功。" : "";
            var skipNote = skipped > 0 ? $" 跳过 {skipped} 条读不懂的。" : "";
            return new AppleSyncResult(true, added, fetched, skipped,
                $"取到 {fetched} 条(日程 {all.Count} + 待办 {allTodos.Count}),新增 {added} 条" +
                $"(其余是本机已有的,未重复添加)。{skipNote}{extra}");
        }
        catch (OperationCanceledException)
        {
            return new AppleSyncResult(false, 0, 0, 0, "已取消。");
        }
        catch (Exception ex)
        {
            return new AppleSyncResult(false, 0, 0, 0, "同步出错:" + AppleCredentials.Redact(ex.Message));
        }
        finally { Busy = false; }
    }
}
