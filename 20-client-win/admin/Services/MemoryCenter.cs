// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ V21(2026-08-08)· 用户裁定:记忆库**搬干净** —— 连同 `memory.json`
//    迁到 `%LOCALAPPDATA%\LocalAI\admin\`,**不留在 `client\` 下**。
//    用户原话:「搬就搬干净,不留一个名字骗人的路径。」
//
//  ★★ 依据(V10 §0.4 用户裁定):**副机上不能编辑记忆,也不能浏览**。
//    拆分之后这条从「按成员过滤」升级成「**结构上没有入口**」——
//    过滤要每个入口都记得过,而没有入口不需要任何人记得。
//
//  ★★★ 但有一句**必须写死**,否则它一定会被实现反(V10 §6):
//    「副机看不到记忆」**≠**「副机上的 AI 没有记忆」。
//    副机上与 AI 对话时,AI **照常使用记忆** —— 模型在主机上跑、检索在主机上做,
//    记忆从不离开主机。把它实现成「副机上的 AI 不带记忆」会把一台副机降级成
//    一个失忆的终端,而用户要的恰恰相反(D45 双成员家庭操作系统)。
//
//  ★ 客户端那边:`MemoryPath` 连同读写点**全部删掉,一个都不留**。
//    留一个「只读」也不行 —— 那还是两个进程碰同一个文件(纪律③),
//    而下一个人会顺手把它改成可写。
// ══════════════════════════════════════════════════════════════════════════════
// P3c -- 记忆库(会话/项目摘要的落点)与它的管理。
//
// 用户裁定(2026-07-30):
//   · 摘要【必须由 AI 生成】;默认 AI 自己判断何时整理,也可在设置里改成【手动触发】。
//   · 摘要【进记忆库】;记忆可被用户手动预览、删减、置顶。
//   · 【永不删原文】—— 除非用户在设置里显式点"删除归档原文"。
//   · 家庭范围的记忆进主机共享、个人范围的留本机(★ 中枢未接入前一律留本机,见下)。
//
// ★ 诚实边界:AI 未接入(P4),所以【现在不会有任何记忆产生】。这里先把结构、管理、清理规则做实,
//   界面如实显示"尚未产生记忆";绝不用本地规则拼一段假摘要冒充 AI 的产物。
//
// 结构:一条记忆 = 一个条目(独立可删、可预览、可追溯来源),而不是一个大 blob ——
//   否则"手动删减 + 摘要预览 + 点回原文"都做不干净。

using LocalAI.Client.Services;

namespace LocalAI.Admin.Services;

/// <summary>记忆的类型。★ Preference/Fact 是最值钱的,自动清理【永不】动它们,只清 Summary。</summary>
public enum MemoryKind { Summary, Fact, Preference }

public sealed record MemoryEntry(
    string Id,
    string Title,
    string Body,                       // 摘要正文(AI 生成;未接入前不会有)
    MemoryKind Kind,
    ProjectScope Scope,                // D45:家庭 / 个人 / 仅本人
    string? OwnerMemberId,
    string? SourceProjectId,           // 来自哪个项目(可空 = 普通会话)
    IReadOnlyList<string>? SourceSessionIds,   // 覆盖了哪些会话 —— 供"点回原文"
    DateTime CreatedAt,
    DateTime? LastUsedAt = null,       // 最近被 AI 用到(用于"长期没用到就清理")
    bool Pinned = false,               // ★ 置顶的永不自动清理
    bool SourceOriginalsDeleted = false,   // 原文已被用户删除 —— 界面据此说明,避免点回去是死链
    bool EditedByHuman = false,            // ★ 被人手改过 —— 它不再是 AI 写的那份,下游要知道
    DateTime? EditedAt = null);

public sealed class MemoryCenter
{
    readonly List<MemoryEntry> _items = new();

    public IReadOnlyList<MemoryEntry> Items => _items;

    public event Action? Changed;

    public static string NewId() => "mem-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>可见的记忆(D45:别人的个人记忆绝不出现)。最近创建的在前,置顶的更靠前。</summary>
    public IEnumerable<MemoryEntry> Visible()
        => _items.Where(m => MemberContext.CanSee(m.Scope, m.OwnerMemberId))
                 .OrderByDescending(m => m.Pinned).ThenByDescending(m => m.CreatedAt);

    public MemoryEntry? Find(string id) => _items.FirstOrDefault(x => x.Id == id);

    public void Add(MemoryEntry m)
    {
        _items.Add(string.IsNullOrWhiteSpace(m.OwnerMemberId) ? m with { OwnerMemberId = MemberContext.Current } : m);
        Changed?.Invoke();
    }

    public void Remove(string id) { if (_items.RemoveAll(x => x.Id == id) > 0) Changed?.Invoke(); }

    /// <summary>
    /// 改一条记忆的标题与正文(P3c 判据里的「编辑」)。
    ///
    /// ★ 只让改【标题与正文】,不让改类型/范围/来源:
    ///   · 范围(家庭/个人/仅本人)改了等于改可见性,那是权限动作,不该混在编辑框里悄悄发生;
    ///   · 来源是溯源链的锚,改了就再也说不清这条摘要是从哪儿来的 —— 而「每条可溯源」是 P3a 的验收硬线。
    /// ★ 改过就【打上人工修改的标记】:AI 之后拿这条去用时,得知道它已经不是自己写的那份。
    ///   不做这个标记的话,人改完的内容会以"AI 摘要"的身份进 prompt —— 那是在骗下游。
    /// </summary>
    public bool EditText(string id, string title, string body)
    {
        var i = _items.FindIndex(x => x.Id == id);
        if (i < 0) return false;
        title = (title ?? "").Trim();
        if (title.Length == 0) return false;              // 标题空了列表里就成了一条没名字的东西
        var old = _items[i];
        if (old.Title == title && old.Body == (body ?? "")) return false;   // 没变就不写、不广播
        _items[i] = old with { Title = title, Body = body ?? "", EditedByHuman = true, EditedAt = DateTime.Now };
        Changed?.Invoke();
        return true;
    }

    public void RemoveMany(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        if (set.Count == 0) return;
        if (_items.RemoveAll(x => set.Contains(x.Id)) > 0) Changed?.Invoke();
    }

    public void TogglePin(string id)
    {
        var i = _items.FindIndex(x => x.Id == id);
        if (i >= 0) { _items[i] = _items[i] with { Pinned = !_items[i].Pinned }; Changed?.Invoke(); }
    }

    /// <summary>某会话的原文被删除后,把引用它的记忆标注出来(避免以后点回原文是死链)。</summary>
    public void MarkOriginalsDeleted(IEnumerable<string> sessionIds)
    {
        var set = sessionIds.ToHashSet();
        var any = false;
        for (int i = 0; i < _items.Count; i++)
        {
            var m = _items[i];
            if (m.SourceOriginalsDeleted || m.SourceSessionIds is null) continue;
            if (m.SourceSessionIds.Any(set.Contains)) { _items[i] = m with { SourceOriginalsDeleted = true }; any = true; }
        }
        if (any) Changed?.Invoke();
    }

    /// <summary>记忆库占用的大致字节数(正文 + 标题,按 UTF-8 估)。</summary>
    public long Bytes() => _items.Sum(m => (long)System.Text.Encoding.UTF8.GetByteCount(m.Title + m.Body));

    // ---------------------------------------------------------------- 自动清理(默认全关 + 先预演)
    /// <summary>
    /// 预演自动清理:按"多少天没被用到"和"总量上限"算出【将要删哪些】,但不动数据。
    /// ★ 三条硬规则:置顶的不动;Preference/Fact 不动(只清 Summary);先预演、确认后才删。
    /// days &lt;= 0 与 maxBytes &lt;= 0 分别表示该条规则关闭。
    /// </summary>
    public List<MemoryEntry> PlanAutoClean(int days, long maxBytes, DateTime? asOf = null)
    {
        var now = asOf ?? DateTime.Now;
        var plan = new List<MemoryEntry>();
        bool Cleanable(MemoryEntry m) => !m.Pinned && m.Kind == MemoryKind.Summary;

        if (days > 0)
        {
            var cut = now.AddDays(-days);
            // 没被用过的按创建时间算 —— 否则"从没用过"的永远清不掉
            plan.AddRange(_items.Where(m => Cleanable(m) && (m.LastUsedAt ?? m.CreatedAt) < cut));
        }

        if (maxBytes > 0 && Bytes() > maxBytes)
        {
            // 超量:从最旧的开始清,直到降到上限以内(已在 plan 里的不重复算)
            var remaining = Bytes() - plan.Sum(m => (long)System.Text.Encoding.UTF8.GetByteCount(m.Title + m.Body));
            foreach (var m in _items.Where(Cleanable).Except(plan).OrderBy(m => m.LastUsedAt ?? m.CreatedAt))
            {
                if (remaining <= maxBytes) break;
                plan.Add(m);
                remaining -= System.Text.Encoding.UTF8.GetByteCount(m.Title + m.Body);
            }
        }
        return plan;
    }

    /// <summary>执行清理(通常传 PlanAutoClean 的结果 —— 用户确认过的那份)。</summary>
    public int ApplyClean(IEnumerable<MemoryEntry> plan)
    {
        var ids = plan.Select(m => m.Id).ToList();
        var before = _items.Count;
        RemoveMany(ids);
        return before - _items.Count;
    }

    // ---------------------------------------------------------------- 存档(明文,D50 口径)
    public List<MemoryEntry> Export() => _items.ToList();

    public void Import(List<MemoryEntry>? items)
    {
        if (items is null) return;
        _items.Clear();
        _items.AddRange(items.Select(m => string.IsNullOrWhiteSpace(m.OwnerMemberId)
            ? m with { OwnerMemberId = MemberContext.LocalMemberId } : m));
        Changed?.Invoke();
    }
}
