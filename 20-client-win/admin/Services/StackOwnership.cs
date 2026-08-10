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
//    栈是用户手工跑 start-stack.ps1 起的、或者账本写不下去,账本里就没有那一条。
//    那时去猜"应该是这几个吧"然后杀掉,正是 D102 撤掉的那种推断,
//    而代价比起栈那边严重得多:猜错是**杀掉用户正在用的进程**。
// ══════════════════════════════════════════════════════════════════════════════
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★★ V23 · 如实更正上面那句话曾经写错的地方,以及它掩护过的那个洞
//
//  这段文字原本写的是「**管理端重启过**…账本就是空的」。**那是假的**,而且不是笔误:
//  账本是磁盘上的一个文件(`%AdminStateDir%\stack-owned.json`),唯一的删除者是
//  `Clear()`,而 `Clear()` 的唯一调用点在 `StackStop.StopAsync` 里,**只在停干净时才跑**。
//  ⇒ 管理端重启**不会**让账本变空;一次没停干净之后,`snapshotTaken=true` 与
//    那份旧的 `backendsBeforeStart` 会**活到下一次开机**。
//  ⇒ 那时用户自己手工起的 `llama-server` 因为「不在那份旧快照里」被判成「我们起的」而杀掉 ——
//    **正是本文件头声称要防的那种误杀,而它由上面那句错话掩护着**。
//    (ASSERTION-PITFALLS 第 1 条那个形状:判据撞在一句"解释它不会发生"的注释上。)
//
//  ★ 修法**不是**再加一句注释,是让快照**自己会失效**:
//    快照只有在「我们现在**还认得出**那个网关是自己起的」时才作数 —— 见
//    <see cref="BackendSnapshotUsable"/>。管理端重启、机器重启、网关换过一茬,
//    那条账都会 `Resolve()` 不出来 ⇒ 快照当场作废 ⇒ 后端**一个都不动**。
//  ★★ 为什么判据挂在**网关**上而不是挂在时间上:
//    「起栈之后冒出来的后端」这句话**只相对我们自己那个网关**才成立 ——
//    是它按需装载出来的。网关都认不出来了,「之后」这个词就没有参照物,
//    而一个没有参照物的时间差会把用户刚开的进程算成我们的。
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

    /// <summary>LAN Edge 的进程名(不含 .exe)。★ 定义只留一处 ——
    /// 关栈那边要拿它去**认**(不是去**杀**,见 <see cref="StackStop"/> 第②步),
    /// 自检也要拿它起一个同名的替身,两处各写一个字面量就会漂。</summary>
    public const string EdgeProcName = "localai-lan-edge";

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
        /// <summary>快照是**什么时候**拍的(UTC ticks)。★ V23 补 —— 在此之前
        /// 「这份快照是这一轮的还是上一轮遗留的」**在账本里根本没有记录**,
        /// 于是一份陈旧快照与一份刚拍的长得一模一样。0 = 旧版本写的账本 ⇒ 当作不可用。</summary>
        [JsonPropertyName("snapshotUtcTicks")] public long SnapshotUtcTicks { get; set; }
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
            // ★ 打上时刻:关栈那边要用它把「起栈之后才冒出来的」筛出来。
            //   ★★ 不打时刻的那一版里,这个筛子根本不存在 —— 只要 snapshotTaken 是 true,
            //     **任何**不在旧 PID 名单里的 llama-server 都会被判成我们的。
            l.SnapshotUtcTicks = DateTime.UtcNow.Ticks;
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

    /// <summary>
    /// 那张后端快照**现在作不作数**。★★★ 这是关栈会不会碰后端的**唯一**判据 ——
    /// 判据只留一份,免得调用方各自凑一个更松的出来。
    /// </summary>
    /// <param name="ownedGatewayProven">
    /// 调用方**证明得了**那个网关是自己起的吗(本进程的句柄还活着、或者账本里那条
    /// PID+进程名+启动时刻三样都对得上)。★ 这个值必须在**杀网关之前**算出来。
    /// </param>
    /// <param name="why">不作数时给人看的那句话。★ 作数时是空串。</param>
    /// <remarks>
    /// ★★★ 三道全都要过,而且每一道都是**为了不误杀**,不是为了严谨好看:
    /// ① 拍过快照 —— 没拍过就分不出谁是谁(原本就有这一道);
    /// ② 快照有时刻 —— 旧版本账本没有这个字段,那时「起栈之后」无从谈起;
    /// ③ **网关认得出来** —— 见文件头 V23 那段。这一道是新加的,它同时替掉了
    ///    「管理端重启过账本就空了」那句**假话**所许诺的保护。
    ///
    /// ★ 如实说清它**盖不住**什么(对抗式复核):同一次起栈会话里,用户在我们的网关
    ///   起来之后**手工**开的一个 `llama-server`,与网关按需装载出来的那一个
    ///   **在这份账里长得一模一样**(都不在快照名单里、启动时刻都晚于快照)。
    ///   要分开只能拿父进程比,而那要 WMI/NtQueryInformationProcess —— 本轮没做。
    ///   ⇒ 这个残余窗口写在这里,不假装已经关掉了。
    /// </remarks>
    public static bool BackendSnapshotUsable(bool ownedGatewayProven, out string why)
    {
        var l = Load();
        if (!l.SnapshotTaken)
        {
            why = "这一轮【没有起栈记录】(账本里没有「起栈之前有哪些后端」那张快照)";
            return false;
        }
        if (l.SnapshotUtcTicks <= 0)
        {
            why = "账本里那张快照【没有时刻】(是旧版本写下的)—— 分不出它是这一轮拍的还是上一轮遗留的";
            return false;
        }
        if (!ownedGatewayProven)
        {
            why = "【认不出那个网关是不是我们起的】(本进程的句柄没了,账本里那条也对不上)—— "
                + "而「起栈之后冒出来的后端」这句话只相对【我们自己那个网关】才成立;"
                + "网关认不出来,这句话就没有参照物";
            return false;
        }
        why = "";
        return true;
    }

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
    /// 现在该由我们停掉的后端 = 活着的 − 起栈前就有的 − **启动早于那张快照的**。
    /// ★ 快照不作数就返回**空表**(不知道 ⇒ 不动),由调用方把这件事说给人听
    /// (<see cref="BackendSnapshotUsable"/> 会给出那句话)。
    /// </summary>
    /// <param name="ownedGatewayProven">见 <see cref="BackendSnapshotUsable"/>。</param>
    public static List<Process> OursToStopBackends(bool ownedGatewayProven)
    {
        if (!BackendSnapshotUsable(ownedGatewayProven, out _)) return new List<Process>();
        var l = Load();
        var adopted = l.BackendsBeforeStart.ToHashSet();
        var since = new DateTime(l.SnapshotUtcTicks, DateTimeKind.Utc);
        return LiveBackends().Where(p => !adopted.Contains(p.Id) && StartedAfter(p, since)).ToList();
    }

    /// <summary>
    /// 这个进程是不是在 <paramref name="since"/> **之后**才起来的。
    /// ★★ 读不到启动时刻时返回 **false** —— 也就是「不动它」。方向是有意选的:
    ///   这个函数的返回值直接决定要不要杀,而"读不到"绝不能被当成"可以杀"。
    /// ★ 容差 2 秒:与 <see cref="Resolve"/> 那处同一个理由(StartTime 有毫秒级抖动),
    ///   而这里往**宽**了容 = 往「更可能判成我们的」那边偏,所以只给 2 秒,不给更多。
    /// </summary>
    static bool StartedAfter(Process p, DateTime since)
    {
        try { return p.StartTime.ToUniversalTime() >= since.AddSeconds(-2); }
        catch { return false; }
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
