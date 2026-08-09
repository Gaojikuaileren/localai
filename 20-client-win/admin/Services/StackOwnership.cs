// V22 -- 「这套栈里,哪些进程是**我们起的**」的账本。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ 为什么必须有这一份账,而不能在关栈那一刻现问:
//
//  关栈的边界是用户明确划的:**停我们起的那些,`adopt_running()` 认领的不动**。
//  而「谁是我们起的」这个问题,**只有在起栈那一刻答得出来** ——
//  等到关栈时再去看,机器上一排 llama-server,哪个是网关按需装载起的、
//  哪个是用户自己早就开着后来被 `adopt_running()` 认领的,**长得一模一样**。
//
//  ⇒ 起栈前先拍一张快照:那时已经在跑的 llama-server = **不是我们的**(网关会去认领它们)。
//    之后冒出来的才是我们这套栈的。这不是推断,是一次**记账**。
//
//  ★★ 记不到账的时候【什么都不动,并且说出来】 ——
//    管理端重启过、或者栈是用户手工跑 start-stack.ps1 起的,账本就是空的。
//    那时去猜"应该是这几个吧"然后杀掉,正是 D102 撤掉的那种推断,
//    而代价比起栈那边严重得多:猜错是**杀掉用户正在用的进程**。
// ══════════════════════════════════════════════════════════════════════════════
//
// ★ PID 复用是真的会发生的,所以账本里存的**不只是 PID**:连 StartTime 一起存。
//   认领之前两样都要对得上(PID + 进程名 + 启动时刻),对不上就当那条账已经作废 ——
//   杀掉一个碰巧复用了同一个 PID 的无辜进程,是这份代码能犯的最严重的错。

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAI.Admin.Services;

/// <summary>账本里的一条:一个我们起过的进程。</summary>
/// <param name="Pid">进程号。★ 单独一个 PID **不足以**认领它 —— 见 <see cref="StackOwnership"/> 文件头。</param>
/// <param name="Name">进程名(不含 .exe)。认领时要对得上。</param>
/// <param name="StartedUtcTicks">启动时刻。★ 防 PID 复用的那一道。</param>
public sealed record OwnedProc(int Pid, string Name, long StartedUtcTicks);

public static class StackOwnership
{
    public enum Component { Gateway, Edge }

    /// <summary>按需装载起的后端进程叫什么。★ 与 `gpu_broker` 起的那个 exe 同名。</summary>
    public const string BackendProcName = "llama-server";

    /// <summary>账本落在哪。★ 与管理端其它状态同一个目录(纪律③:不写客户端那份)。</summary>
    public static string LedgerPath => Path.Combine(AdminPaths.StateDir, "stack-owned.json");

    sealed class Ledger
    {
        [JsonPropertyName("gateway")] public OwnedProc? Gateway { get; set; }
        [JsonPropertyName("edge")] public OwnedProc? Edge { get; set; }
        /// <summary>起栈**之前**就在跑的后端 PID。★ 这些是 `adopt_running()` 的那一批,**永远不动**。</summary>
        [JsonPropertyName("backendsBeforeStart")] public List<int> BackendsBeforeStart { get; set; } = new();
        /// <summary>有没有真的拍过那张快照。★ 与"快照是空的"是**两件事**:
        /// 前者表示我们不知道,后者表示起栈前确实一个后端都没有。</summary>
        [JsonPropertyName("snapshotTaken")] public bool SnapshotTaken { get; set; }
    }

    static readonly object _gate = new();

    static Ledger Load()
    {
        try
        {
            if (!File.Exists(LedgerPath)) return new Ledger();
            return JsonSerializer.Deserialize<Ledger>(File.ReadAllText(LedgerPath)) ?? new Ledger();
        }
        catch { return new Ledger(); }   // 读不出来就当没有账 —— 那会让关栈走"不知道就不动"那条路
    }

    static void Save(Ledger l)
    {
        try
        {
            Directory.CreateDirectory(AdminPaths.StateDir);
            File.WriteAllText(LedgerPath, JsonSerializer.Serialize(l, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 记不了账**不算起栈失败** —— 但关栈那边会因此走"不知道就不动",这是安全的方向 */ }
    }

    /// <summary>
    /// 起栈**之前**拍一张后端快照。★ 必须在 `EnsureStackAsync` 的第一行调 ——
    /// 晚一步,网关就已经把自己起的那些混进来了,这张快照就作废了(而且看不出来作废了)。
    /// </summary>
    public static void NoteBackendsBeforeStart()
    {
        lock (_gate)
        {
            var l = Load();
            l.BackendsBeforeStart = LiveBackends().Select(p => p.Id).ToList();
            l.SnapshotTaken = true;
            Save(l);
        }
    }

    /// <summary>记下"这个进程是我们起的"。★ 连 StartTime 一起记(防 PID 复用)。</summary>
    public static void NoteStarted(Component which, Process p)
    {
        try
        {
            var rec = new OwnedProc(p.Id, p.ProcessName, p.StartTime.ToUniversalTime().Ticks);
            lock (_gate)
            {
                var l = Load();
                if (which == Component.Gateway) l.Gateway = rec; else l.Edge = rec;
                Save(l);
            }
        }
        catch { /* 拿不到 StartTime(进程刚好退了)就不记 —— 宁可关栈时说"不知道" */ }
    }

    /// <summary>账本里的网关 / Edge。★ 只返回**现在还活着且对得上**的那些。</summary>
    public static (Process? Gateway, Process? Edge) Owned()
    {
        var l = Load();
        return (Resolve(l.Gateway), Resolve(l.Edge));
    }

    /// <summary>起栈前就在跑的那批后端 PID。★ 关栈**绝不碰**它们。</summary>
    public static IReadOnlyList<int> AdoptedBackendPids() => Load().BackendsBeforeStart;

    /// <summary>拍过快照没有。★ 没拍过 ⇒ 关栈时对后端**一个都不动**,并如实说为什么。</summary>
    public static bool HasBackendSnapshot() => Load().SnapshotTaken;

    /// <summary>
    /// 把一条账认领成一个真进程。★ 三样都要对得上才认:PID 存在 · 进程名一致 · 启动时刻一致。
    /// 对不上就返回 null —— 那意味着这条账已经作废(进程早退了,PID 被别人用了)。
    /// </summary>
    static Process? Resolve(OwnedProc? rec)
    {
        if (rec is null) return null;
        try
        {
            var p = Process.GetProcessById(rec.Pid);
            if (!string.Equals(p.ProcessName, rec.Name, StringComparison.OrdinalIgnoreCase)) return null;
            // ★ StartTime 有毫秒级抖动,不能用 == 比。差 2 秒以内当作同一个。
            var delta = Math.Abs(p.StartTime.ToUniversalTime().Ticks - rec.StartedUtcTicks);
            return delta <= TimeSpan.TicksPerSecond * 2 ? p : null;
        }
        catch { return null; }   // 进程不在了 —— 那正是我们想知道的
    }

    /// <summary>现在机器上所有的后端进程。</summary>
    public static List<Process> LiveBackends()
    {
        try { return Process.GetProcessesByName(BackendProcName).ToList(); }
        catch { return new List<Process>(); }
    }

    /// <summary>
    /// 现在该由我们停掉的后端 = 活着的 − 起栈前就有的。
    /// ★ 没拍过快照就返回**空表**(不知道 ⇒ 不动),由调用方把这件事说给人听。
    /// </summary>
    public static List<Process> OursToStopBackends()
    {
        var l = Load();
        if (!l.SnapshotTaken) return new List<Process>();
        var adopted = l.BackendsBeforeStart.ToHashSet();
        return LiveBackends().Where(p => !adopted.Contains(p.Id)).ToList();
    }

    /// <summary>关完栈把账清掉 —— 留着会在下一次关栈时认领到别人的 PID。</summary>
    public static void Clear()
    {
        lock (_gate)
        {
            try { if (File.Exists(LedgerPath)) File.Delete(LedgerPath); } catch { }
        }
    }
}
