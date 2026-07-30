// P3c -- 待办与家务的数据模型。
//
// 设计 §4:主页「待办与家务」板块。两类:
//   · 待办(Personal)—— "提醒我…" 建的个人事项;
//   · 家务(Chore)  —— "提醒我们…" 建的家庭事务。
// 归属(Owner)与可见范围(Scope)沿用 D45 两成员家庭口径。
//
// ★ 与日历不同:日历是【镜像 Apple 家庭共享日历】,本地改不了、故保存如实拒绝;
//   待办/家务是【中枢自有数据】,手动新增/编辑/删除【当场生效】,不是伪造。
//   目前存在内存里(与项目、日历示例同一处理),【跨设备同步与落盘持久化】随中枢接入(P4+)启用 ——
//   界面上如实说明,不谎称已跨设备同步。
//
// 标题 / 备注是自由文本:仅作显示,永不进 prompt(与设备自报名同一纪律)。

namespace LocalAI.Client.Services;

public enum TodoKind { Personal, Chore }              // 待办 / 家务
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
    DateTime? CompletedAt = null)
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

    /// <summary>新增。Id 为空则自动生成;返回最终 Id。</summary>
    public string Add(TodoItem t)
    {
        var it = string.IsNullOrEmpty(t.Id) ? t with { Id = NewId() } : t;
        _items.Add(it);
        Changed?.Invoke();
        return it.Id;
    }

    public void Update(TodoItem t)
    {
        var i = _items.FindIndex(x => x.Id == t.Id);
        if (i < 0) return;
        _items[i] = t;
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        if (_items.RemoveAll(x => x.Id == id) > 0) Changed?.Invoke();
    }

    /// <summary>勾选 / 取消勾选完成 —— 提醒事项那一下点圈。完成时打上时间戳,取消则清掉。</summary>
    public void Toggle(string id)
    {
        var i = _items.FindIndex(x => x.Id == id);
        if (i < 0) return;
        var done = !_items[i].Done;
        _items[i] = _items[i] with { Done = done, CompletedAt = done ? DateTime.Now : null };
        Changed?.Invoke();
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    // ---------------------------------------------------------------- 存档(明文,见 ClientStore)
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
