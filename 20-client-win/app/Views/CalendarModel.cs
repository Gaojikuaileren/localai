// P3c -- 日程数据模型。字段集按用户裁定:
//   标题 · 开始 · 结束(默认 +1 小时) · iCloud 日历组(留待接入) · 地点(纯字符) · 链接 · 备注
//   · 全天开关;全天日程可【跨天】,跨天在界面上用一条贯穿多格的长条表示。
//
// ★ 本地优先(2026-07-30 用户裁定):没接入 Apple 也能【新增/编辑/显示/持久化】日程 ——
//   日历现在是本机自有数据(与待办同),明文落盘(D50 / calendar.json)。
//   接入 Apple 家庭共享日历后走【双向增量合并】,不是全局覆盖(见 CalendarData.MergeIn / LocalOnly):
//     · 已有的不重复加;没有的才加;绝不用空日程覆盖已有;本地独有的反向推给 Apple。
//   AI 与手动编辑写同一个模型;接入前不伪造"同步成功",只在本机生效。
//
// 可见范围(Scope)与归属(Owner)沿用 D45 的两成员家庭口径。
// Location / Url / Notes 都是【自由文本】:仅作显示,永不进 prompt(与设备自报名同一纪律)。

namespace LocalAI.Client.Views;

public sealed record CalendarEvent(
    DateTime Start,
    DateTime End,
    string Title,
    string Owner,
    string Scope,
    bool AllDay = false,
    string? CalendarGroup = null,   // iCloud 日历组(接入后由服务端给出可选集合)
    string? Location = null,        // 仅字符,不做地理解析
    string? Url = null,
    string? Notes = null,
    bool CreatedByAi = false,   // ★ 是否由 AI 建立(界面用小标记区分手动/AI 创建,用户裁定)
    string? Id = null,          // 本地稳定 id(CalendarData.Add 自动补;供编辑/删除定位)
    string Source = "local",    // 来源:local(本机建) / apple(从家庭共享日历同步来)
    string? ExternalId = null)  // Apple 那边的 UID(同步后回填,用于合并去重的首选判据)
{
    /// <summary>跨天判定:全天日程按【日期区间】算,定时日程只属于起始那天。</summary>
    public DateTime FirstDay => Start.Date;
    /// <summary>
    /// 真正结束在哪一天。
    /// ★★ 定时日程也可以跨天(编辑器里的「结束日」偏移)——
    ///   原来这里写死 "定时的只属于起始那天",于是一条 7/31 22:00→8/1 03:00 的日程:
    ///   下半的时间轴在 8/1 画着它(它有自己的算法),上半月历的 8/1 格里却既没圆点也没线,
    ///   点 8/1 看当日列表也查无此条 —— 同一块板块的上下两半各说各的。
    ///   用 AddTicks(-1):结束恰好是次日 00:00 的算结束在起始那天(不多占一格)。
    /// </summary>
    public DateTime LastDay => AllDay ? End.Date : (End > Start ? End.AddTicks(-1).Date : Start.Date);
    public int DayCount => (LastDay - FirstDay).Days + 1;
    public bool IsMultiDay => DayCount > 1;

    /// <summary>这一天是否落在本日程的区间内。</summary>
    public bool Covers(DateTime day) => day.Date >= FirstDay && day.Date <= LastDay;
}

public static class CalendarData
{
    public static List<CalendarEvent> Events { get; } = new();

    /// <summary>
    /// 日程集合变更时触发。★ 有了它,界面就不依赖"播种一定早于建窗口"这种时序假设 ——
    /// 将来 AI 或编辑器写入日程也能自动刷新(这类时序耦合正是"开启时读不出日程"的成因)。
    /// </summary>
    public static event Action? Changed;

    public static void NotifyChanged() => Changed?.Invoke();

    public static string NewId() => "ev-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>新增。没带 Id 就补一个稳定 Id(供后续编辑/删除定位)。</summary>
    public static void Add(CalendarEvent e)
    {
        if (string.IsNullOrEmpty(e.Id)) e = e with { Id = NewId() };
        Events.Add(e);
        NotifyChanged();
    }

    /// <summary>按 Id 更新一条(编辑保存走这里)。</summary>
    public static void Update(CalendarEvent e)
    {
        var i = string.IsNullOrEmpty(e.Id) ? -1 : Events.FindIndex(x => x.Id == e.Id);
        if (i >= 0) { Events[i] = e; NotifyChanged(); }
        else Add(e);   // 没找到(理论上不该)就当新增,别默默丢
    }

    /// <summary>删除:优先按 Id,其次按值(兼容没 Id 的旧数据)。</summary>
    public static void Remove(CalendarEvent e)
    {
        var removed = !string.IsNullOrEmpty(e.Id)
            ? Events.RemoveAll(x => x.Id == e.Id) > 0
            : Events.Remove(e);
        if (removed) NotifyChanged();
    }

    // ---------------------------------------------------------------- 存档(明文,见 ClientStore)
    public static List<CalendarEvent> Export() => Events.ToList();

    public static void Import(List<CalendarEvent>? items)
    {
        if (items is null) return;
        Events.Clear();
        foreach (var e in items) Events.Add(string.IsNullOrEmpty(e.Id) ? e with { Id = NewId() } : e);
        NotifyChanged();
    }

    // ---------------------------------------------------------------- 与 Apple 家庭共享日历的【增量合并】
    // 用户裁定(2026-07-30):接入 Apple 后【不是全局覆盖】,而是双向增量:
    //   · 已有的不重复加;没有的才加;★ 绝不用空日程覆盖已有;
    //   · 本地独有的(Apple 没有)反向推给 Apple(见 LocalOnly)。
    // Apple 未接入前:这些是【纯函数 + 数据操作】,已被 selftest 钉死;真正的拉取/推送等接入时接上。

    /// <summary>空日程:没标题的不参与合并、也永不用来覆盖(用户明确要求)。</summary>
    public static bool IsBlank(CalendarEvent e) => string.IsNullOrWhiteSpace(e.Title);

    /// <summary>内容签名:没有 Apple UID 时,按"起止+全天+标题"判断是否同一条。</summary>
    public static string ContentKey(CalendarEvent e)
        => $"{e.Start:o}|{e.End:o}|{e.AllDay}|{(e.Title ?? "").Trim()}";

    /// <summary>去重判据:有 Apple UID 用 UID,否则用内容签名。</summary>
    public static string Identity(CalendarEvent e)
        => !string.IsNullOrEmpty(e.ExternalId) ? "x:" + e.ExternalId : "c:" + ContentKey(e);

    /// <summary>
    /// 把 incoming 增量并入 existing:只加【没有的】,不重复加、不覆盖已有、不并入空日程。
    /// 返回实际新增的条数。纯函数(直接改 existing 列表),便于测试与复用。
    /// </summary>
    public static int MergeInto(List<CalendarEvent> existing, IEnumerable<CalendarEvent> incoming)
        => MergeInto(existing, incoming, out _);

    /// <param name="refreshed">
    /// 【已存在但被刷新过的】条数 —— 与 added 分开报,界面才能如实说"新增 N 条、更新 M 条"。
    /// </param>
    public static int MergeInto(List<CalendarEvent> existing, IEnumerable<CalendarEvent> incoming, out int refreshed)
    {
        // Identity -> 在 existing 里的下标(要就地更新,不能只记"见过")
        var at = new Dictionary<string, int>();
        for (int i = 0; i < existing.Count; i++) at[Identity(existing[i])] = i;

        var added = 0;
        refreshed = 0;
        foreach (var inc in incoming)
        {
            if (IsBlank(inc)) continue;              // ★ 空日程不并入(不覆盖已有)
            var key = Identity(inc);
            if (at.TryGetValue(key, out var idx))
            {
                // ★★ 已存在的【不再一律跳过】。原来"已存在 -> 跳过"把 Apple 那边的后续改动整个冻住了:
                //   在 iCloud 里把一条日程挪到别的日历(或给日历改名/改色),再同步 ——
                //   本机这条永远停在旧分类、旧颜色,用户在界面上【没有任何办法】修好它。
                //   现在只对【确实来自 Apple、且靠 UID 命中】的条目,把 Apple 才是权威的那几个字段刷新一遍;
                //   本机的 Id / CreatedByAi / 可见范围一律保留,本机自建的日程(没有 UID)完全不碰。
                var cur = existing[idx];
                if (cur.Source != "apple" || string.IsNullOrEmpty(cur.ExternalId)) continue;
                var next = cur with
                {
                    Start = inc.Start,
                    End = inc.End,
                    Title = inc.Title,
                    AllDay = inc.AllDay,
                    CalendarGroup = inc.CalendarGroup,
                    Location = inc.Location,
                    Url = inc.Url,
                    Notes = inc.Notes,
                };
                if (next != cur) { existing[idx] = next; refreshed++; }
                continue;
            }
            var withId = string.IsNullOrEmpty(inc.Id) ? inc with { Id = NewId() } : inc;
            at[key] = existing.Count;
            existing.Add(withId);
            added++;
        }
        return added;
    }

    /// <summary>本地独有(remote 里没有)的日程 —— 接入后要反向推给 Apple 的那些。</summary>
    public static List<CalendarEvent> LocalOnly(IEnumerable<CalendarEvent> local, IEnumerable<CalendarEvent> remote)
    {
        var rkeys = remote.Select(Identity).ToHashSet();
        return local.Where(e => !IsBlank(e) && !rkeys.Contains(Identity(e))).ToList();
    }

    /// <summary>接入后:把 Apple 拉来的日程合并进本机(不覆盖、不重复)。返回新增条数。</summary>
    public static int MergeIn(IEnumerable<CalendarEvent> incoming) => MergeIn(incoming, out _);

    public static int MergeIn(IEnumerable<CalendarEvent> incoming, out int refreshed)
    {
        var n = MergeInto(Events, incoming, out refreshed);
        if (n > 0 || refreshed > 0) NotifyChanged();
        return n;
    }

    /// <summary>
    /// 日程分类(= iCloud 的日历)。★ 用户裁定 2026-07-31:接上 Apple 后这里就是【Apple 的日历清单】。
    /// 实现搬到 Services.CalendarGroups(那里还管颜色);这里保留同名入口,免得调用方到处改。
    /// 没接 Apple 时是本地占位分类,CalendarGroups.FromApple 会如实说明它不是真的。
    /// </summary>
    public static string[] Groups => Services.CalendarGroups.Names;

    /// <summary>某天的定时日程(不含全天/跨天条,那些走 SpansIn 单独画长条)。</summary>
    public static IEnumerable<CalendarEvent> TimedOn(DateTime day)
        => Events.Where(e => !e.AllDay && e.Start.Date == day.Date).OrderBy(e => e.Start);

    /// <summary>某天的全部日程(全天 + 定时),用于列表展示与"有无日程"的标点。</summary>
    public static IEnumerable<CalendarEvent> On(DateTime day)
        => Events.Where(e => e.Covers(day))
                 .OrderByDescending(e => e.AllDay)     // 全天排在前面
                 .ThenBy(e => e.Start);

    /// <summary>
    /// 计算落在 [rangeStart, rangeStart+dayCount) 这一段里的【全天/跨天】日程,
    /// 返回它在这一段里的起始列与跨列数 —— 界面据此画一条贯穿多格的长条。
    /// 日程若延伸到区间之外,会被裁到区间边界(并标出是否续前/续后)。
    /// </summary>
    public static List<(CalendarEvent Ev, int Col, int Span, bool ClipStart, bool ClipEnd)> SpansIn(
        DateTime rangeStart, int dayCount)
    {
        var result = new List<(CalendarEvent, int, int, bool, bool)>();
        var rangeEnd = rangeStart.Date.AddDays(dayCount - 1);

        foreach (var e in Events.Where(x => x.AllDay).OrderBy(x => x.FirstDay).ThenByDescending(x => x.DayCount))
        {
            if (e.LastDay < rangeStart.Date || e.FirstDay > rangeEnd) continue;
            var from = e.FirstDay < rangeStart.Date ? rangeStart.Date : e.FirstDay;
            var to = e.LastDay > rangeEnd ? rangeEnd : e.LastDay;
            var col = (from - rangeStart.Date).Days;
            var span = (to - from).Days + 1;
            // ★ 括号不可省:元组里写 `a < b, c > d` 会被 C# 解析成泛型参数列表 `a<b, c>`,
            //   报出一串莫名其妙的"变量被当作类型使用"。
            result.Add((e, col, span, (e.FirstDay < rangeStart.Date), (e.LastDay > rangeEnd)));
        }
        return result;
    }
}

/// <summary>供自检调用的只读工具(不参与界面逻辑)。</summary>
public static class CalendarViewTestHooks
{
    /// <summary>周一起始的那一周的第一天 —— 与 CalendarView 内部同一口径。</summary>
    public static DateTime StartOfWeek(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));
}
