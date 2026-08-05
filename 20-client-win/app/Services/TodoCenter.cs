// P3c -- 待办与家务的数据模型。
//
// 设计 §4:主页「待办与家务」板块。两类:
//   · 待办(Personal)—— "提醒我…" 建的个人事项;
//   · 家务(Chore)  —— "提醒我们…" 建的家庭事务。
// 归属(Owner)与可见范围(Scope)沿用 D45 两成员家庭口径。
//
// ★★ 待办是【纯本机数据】—— 不连任何外部服务、不同步任何数据(用户裁定 2026-08-02,D57)。
//   与日历正相反:日历镜像 Apple 家庭共享日历、本地改不了;待办完全归本机,
//   手动新增/编辑/删除当场生效,落盘在 %LOCALAPPDATA%\LocalAI\client	odos.json。
//
//   ★ 为什么不接 iPhone 的提醒事项:Apple 只给了 EventKit 一条官方通道,而它必须跑在
//     Apple 设备上 —— CalDAV 自 2019 年起对 iCloud 提醒事项关闭,也没有任何远程接口。
//     要么让用户装东西,要么靠 iOS 快捷指令做尽力而为的推送;两条都被裁定不做(D57)。
//   ★ 所以这里【不留】任何"以后会同步"的接口:没有 Source/ExternalId,没有 MergeIn。
//     留着它们就是在暗示一个不会兑现的承诺 —— 下一个人会以为只差接上最后一根线。
//   ★ 直接后果(必须在界面上说清):这份数据只在这台电脑上,不会自愈。见 D57。
//
// 标题 / 备注是自由文本:仅作显示,永不进 prompt(与设备自报名同一纪律)。

namespace LocalAI.Client.Services;

public enum TodoKind { Personal, Chore, Shopping }    // 待办 / 家务 / 采购清单(仿提醒事项的分类)
public enum TodoPriority { None, Low, Medium, High }  // 无 / 低 / 中 / 高(仿提醒事项)

/// <summary>
/// 一条待办事项。Due 为空=无截止;DueHasTime=false 表示只到"某天",不含具体时刻。
/// CompletedAt = 勾选完成的时刻(用于"完成后停留 3 秒再归档"的宽限判定)。
/// </summary>
public sealed record TodoItem(
    string Id,
    string Title,
    TodoKind Kind,
    bool Done = false,
    DateTime? Due = null,
    bool DueHasTime = false,
    bool Flagged = false,
    TodoPriority Priority = TodoPriority.None,
    string? Notes = null,
    string Owner = "我",
    string Scope = "家庭",
    DateTime? CompletedAt = null,
    bool CreatedByAi = false)   // ★ 是否由 AI 建立(界面用小标记区分手动/AI 创建,用户裁定)
{
    /// <summary>已逾期:有截止、未完成、且截止在此刻之前。</summary>
    public bool IsOverdue => Due is { } d && !Done && d < DateTime.Now;
}

public sealed class TodoCenter
{
    readonly List<TodoItem> _items = new();

    public IReadOnlyList<TodoItem> Items => _items;

    /// <summary>
    /// 变更即触发 —— 界面据此刷新,不依赖"播种早于建窗口"这种时序假设
    /// (这类耦合正是"开启读不出数据、点一下才有"的成因,见日历同款注释)。
    /// </summary>
    public event Action? Changed;

    // ══════════════════════════════════════════════════════════════
    //  P4-S13(D86):家庭待办走内网同步。
    //
    //  ★★ 只推 Scope == 家庭 的。个人待办**继续只在本机** ——
    //    D52「默认只在本机」那条原则没变,变的是"家庭"那一档终于真的共享了。
    //  ★ 客户端这道判据不是唯一一道:服务端还会再判一次(sync_store.in_scope)。
    //    这里过一遍只是**少发一次就少一次出错机会** —— 把私人东西推到另一台机器
    //    是**不可撤销**的错误,不能只靠一处把关。
    // ══════════════════════════════════════════════════════════════

    /// <summary>同步器。★ 由宿主注入 —— 不在这里 new,否则会出现两条各自订阅的流。</summary>
    public SyncClient? Sync { get; set; }

    // ══════════════════════════════════════════════════════════════════
    //  ★★ 2026-08-05 审计发现:Scope 存的是【界面文案】,不是数据键。
    //
    //  TodoEditor 填进去的是 `Strings.Get("visibility.family")`,而词表里:
    //      zh-CN "家庭"  ·  en-US "Family"  ·  ja-JP "家族"
    //  判据原来写死 `t.Scope == "家庭"` ⇒ **英文/日文界面下建的家庭待办
    //  静默地永远不同步**;而且换一次语言,已存的待办就永远卡在旧语言的字符串上。
    //
    //  ⇒ ① 判据认全部三种写法;② **上线时一律写规范值**,不把界面文案送上网 ——
    //    服务端的范围闸也是拿 "家庭" 比的,送 "Family" 上去会被判 out_of_scope。
    //  ★ 诚实边界:这只修好了【同步】。彻底的做法是 Scope 存语言无关的键、
    //    显示时再翻译 —— 那要迁移已有存档,记在 STATE 的待办里,**没有假装已解决**。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>「家庭」这一档在三语界面下的全部写法。★ 新增语言必须同步加进来。</summary>
    static readonly string[] FamilyAliases = { "家庭", "Family", "家族" };

    /// <summary>上线的规范值。★ 与服务端 sync_store.in_scope 的判据一致。</summary>
    public const string WireFamily = "家庭";

    /// <summary>这个 Scope 是不是「家庭」档 —— 不管界面是什么语言。</summary>
    public static bool IsFamily(string? scope) =>
        scope is not null && FamilyAliases.Contains(scope, StringComparer.Ordinal);

    /// <summary>该不该同步这一条。★ 判据只看 Scope,不看别的(别的字段一变就漏推)。</summary>
    public static bool ShouldSync(TodoItem t) => IsFamily(t.Scope);

    /// <summary>
    /// 一条待办的**上线形态**。★ 只此一处 —— 增量推与全量对齐必须共用它,
    /// 两处各写一份迟早会漂(本项目已经栽过好几次)。
    /// </summary>
    public static SyncItem ToSyncItem(TodoItem t) => new("todos", new
    {
        id = t.Id, title = t.Title, kind = t.Kind.ToString(), done = t.Done,
        due = t.Due?.ToString("o"), due_has_time = t.DueHasTime, flagged = t.Flagged,
        priority = t.Priority.ToString(), notes = t.Notes,
        owner = t.Owner, scope = WireFamily,      // ★ 规范值,不送界面文案
        completed_at = t.CompletedAt?.ToString("o"), created_by_ai = t.CreatedByAi,
    });

    /// <summary>当前**全部**该同步的待办。★ 连上中枢时用来对齐(见 SyncClient.ReconcileAsync)。</summary>
    public IEnumerable<SyncItem> SharedSnapshot() => _items.Where(ShouldSync).Select(ToSyncItem);

    /// <summary>
    /// 删除一条家庭待办的**上线形态**(墓碑)。★ 删除必须是一条会传播的记录 ——
    /// 只是本地把它拿掉的话,另一台一对齐就又把它推回来了(用户实测:「删除时还是没法同步删除」)。
    /// </summary>
    public static SyncItem ToTombstone(string id) => new("todos", new { id, deleted = true });

    /// <summary>收到远端的删除。★ 本地也删掉,并且**不再**把它算进 SharedSnapshot。</summary>
    public bool AbsorbRemoteDelete(string id)
    {
        var i = _items.FindIndex(x => x.Id == id);
        if (i < 0) return false;
        _items.RemoveAt(i);
        Changed?.Invoke();
        return true;
    }

    void PushIfShared(TodoItem t)
    {
        if (Sync is null || !ShouldSync(t)) return;
        Sync.Enqueue(ToSyncItem(t));
    }

    /// <summary>新增。Id 为空则自动生成;返回最终 Id。</summary>
    public string Add(TodoItem t)
    {
        var it = string.IsNullOrEmpty(t.Id) ? t with { Id = NewId() } : t;
        _items.Add(it);
        PushIfShared(it);
        Changed?.Invoke();
        return it.Id;
    }

    public void Update(TodoItem t)
    {
        var i = _items.FindIndex(x => x.Id == t.Id);
        if (i < 0) return;
        var was = _items[i];
        _items[i] = t;
        PushIfShared(t);
        // ★ 从「家庭」改成「个人」时:它已经在中枢上了,而现在不该再共享。
        //   ★★ 但**不删中枢那份** —— 删了会让另一台机器上的条目凭空消失,
        //     而那台机器的用户没做过任何事。这条留作**待裁**,如实记在这里,
        //     不假装已经处理好了。(降级共享与 D52「共享不可收回」是同一类问题。)
        if (ShouldSync(was) && !ShouldSync(t)) DowngradedWhileShared.Add(t.Id);
        Changed?.Invoke();
    }

    /// <summary>曾经共享、后来被改成个人的待办 id。★ 中枢上那份**还在** —— 见 Update 里的说明。</summary>
    public readonly List<string> DowngradedWhileShared = new();

    public void Remove(string id)
    {
        // ★★ 先看它是不是家庭待办 —— 拿掉之后就问不出来了。
        //   只有共享过的才推墓碑:个人待办从来没上去过,推墓碑只会被服务端拒。
        var wasShared = _items.FirstOrDefault(x => x.Id == id) is { } t && ShouldSync(t);
        if (_items.RemoveAll(x => x.Id == id) > 0)
        {
            if (wasShared) Sync?.Enqueue(ToTombstone(id));   // 删除也要同步(用户实测)
            Changed?.Invoke();
        }
    }

    /// <summary>合并一条来自中枢的家庭待办。★ 返回是否真的变了(没变就不刷界面)。</summary>
    public bool AbsorbRemote(TodoItem t)
    {
        if (!ShouldSync(t)) return false;          // ★ 中枢不该发来个人的;真发来了也不收
        var i = _items.FindIndex(x => x.Id == t.Id);
        if (i < 0) { _items.Add(t); Changed?.Invoke(); return true; }
        if (_items[i] == t) return false;          // 一模一样就别刷
        _items[i] = t;
        Changed?.Invoke();
        return true;
    }

    /// <summary>勾选 / 取消勾选完成 —— 提醒事项那一下点圈。完成时打上时间戳,取消则清掉。</summary>
    public void Toggle(string id)
    {
        var i = _items.FindIndex(x => x.Id == id);
        if (i < 0) return;
        var done = !_items[i].Done;
        _items[i] = _items[i] with { Done = done, CompletedAt = done ? DateTime.Now : null };
        PushIfShared(_items[i]);       // ★ 勾选也是内容变更,家庭待办要实时同步(D86 裁定②)
        Changed?.Invoke();
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    // ---------------------------------------------------------------- 存档(明文,见 ClientStore)
    // ★★ 这里【故意没有】增量合并(原 MergeInto / MergeIn / Identity / ContentKey,2026-08-02 删除)。
    //   那一套是为"从 Apple 提醒事项导入并去重"造的,而待办已裁定为纯本机、不接任何外部源(D57)。
    //   留一套没有数据源的合并层,等于摆着一个"只差接上最后一根线"的假象。


    public List<TodoItem> Export() => _items.ToList();

    public void Import(List<TodoItem>? items)
    {
        if (items is null) return;
        _items.Clear();
        _items.AddRange(items);
        Changed?.Invoke();
    }

    /// <summary>勾完成后先停留几秒再归档到"已完成"抽屉(用户裁定:3 秒)。</summary>
    public const double ArchiveGraceSeconds = 3;

    static bool InGrace(TodoItem t, DateTime now)
        => t.Done && t.CompletedAt is { } c && (now - c).TotalSeconds < ArchiveGraceSeconds;

    /// <summary>
    /// 主板块要显示的:未完成 + 刚勾选还在宽限期内的。
    /// 排序(用户裁定):按截止时间升序 —— 逾期/最近到期在最前,无截止的排最后;同截止按标题。
    /// </summary>
    public IEnumerable<TodoItem> Active(DateTime? asOf = null)
    {
        var now = asOf ?? DateTime.Now;
        return _items.Where(t => !t.Done || InGrace(t, now))
                     .OrderBy(t => t.Due ?? DateTime.MaxValue)
                     .ThenBy(t => t.Title, StringComparer.CurrentCulture);
    }

    /// <summary>立即删除全部已完成(用户裁定:不做选择性批量删除,只保留"一键删全部")。</summary>
    public void ClearCompleted()
    {
        if (_items.RemoveAll(x => x.Done) > 0) Changed?.Invoke();
    }

    /// <summary>
    /// 自动清理:删掉【完成时间】早于 days 天的已完成事项。days &lt;= 0 表示不自动清理。
    /// asOf 供测试注入。返回删掉的条数。
    /// </summary>
    public int PurgeCompletedOlderThan(int days, DateTime? asOf = null)
    {
        if (days <= 0) return 0;                       // 0 = 关闭自动清理(默认)
        var cutoff = (asOf ?? DateTime.Now).AddDays(-days);
        // 只清【有完成时间且早于阈值】的;没有时间戳的不动(宁可留着也不误删)
        var n = _items.RemoveAll(x => x.Done && x.CompletedAt is { } c && c < cutoff);
        if (n > 0) Changed?.Invoke();
        return n;
    }

    /// <summary>已完成抽屉:全部已完成,最近完成在前。</summary>
    public IEnumerable<TodoItem> Completed()
        => _items.Where(t => t.Done)
                 .OrderByDescending(t => t.CompletedAt ?? DateTime.MinValue)
                 .ThenBy(t => t.Title, StringComparer.CurrentCulture);

    public int CompletedCount => _items.Count(t => t.Done);

    /// <summary>是否还有处于"完成后 3 秒宽限期"的项(界面据此决定要不要继续轮询刷新)。</summary>
    public bool HasGrace(DateTime? asOf = null)
    {
        var now = asOf ?? DateTime.Now;
        return _items.Any(t => InGrace(t, now));
    }
}
