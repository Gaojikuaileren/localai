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

/// <summary>一条待办 / 家务。Due 为空=无截止;DueHasTime=false 表示只到"某天",不含具体时刻。</summary>
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
    string Scope = "家庭")
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

    /// <summary>勾选 / 取消勾选完成 —— 提醒事项那一下点圈。</summary>
    public void Toggle(string id)
    {
        var i = _items.FindIndex(x => x.Id == id);
        if (i < 0) return;
        _items[i] = _items[i] with { Done = !_items[i].Done };
        Changed?.Invoke();
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// 排序口径:未完成在前、已完成沉底;各自先按截止时间(无截止排后),再按标题。
    /// kind 给定则只取该类。
    /// </summary>
    public IEnumerable<TodoItem> Ordered(TodoKind? kind = null)
        => _items.Where(t => kind is null || t.Kind == kind)
                 .OrderBy(t => t.Done)
                 .ThenBy(t => t.Due ?? DateTime.MaxValue)
                 .ThenBy(t => t.Title, StringComparer.CurrentCulture);
}
