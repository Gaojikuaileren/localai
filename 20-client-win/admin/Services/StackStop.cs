// V14 裁定⑤ -- 托盘右键「关闭」= **关栈入口**。
//
// ★★ 承的是 D102 裁定④:「**关栈是人的动作,不是推断**」——
//   跨机空闲阈值 / 副机在线名单 / 定时巡检当时被整个撤掉,理由就是**它们在替人做判断**。
//   ⇒ 托盘右键那一下**就是那个"人的动作"**。本文件只负责把判据摆到人面前,不替人决定。
//
// ★ V9 已经做好了判据本身:`10-core/gateway/gateway.py:849` 的 `safe_to_stop_stack()`
//   —— 它**只回答**「现在关会不会切断别人」,**自己不关任何东西**。
//   在此之前它**有判据、没有入口**;这条入口就是那个缺口。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ 如实交代一件没做完的事(DEBT · V14 → V16)
//
//  `safe_to_stop_stack()` 今天**没有 HTTP 路由** —— 本车道实测:全仓只有它的定义
//  与 test_gpu_broker.py 里的直接调用,`gateway.py` 里没有任何路由暴露它。
//  而 `10-core/gateway/**` 是 **V16 车道正在动的禁区**,本车道不碰。
//
//  ⇒ 于是这里的选择是:**如实说读不到,并且仍然把决定权交给人**。
//    · **不**猜一个"应该没人在用"然后替人关掉 —— 那正是 D102 撤掉的那种推断;
//    · **不**因为读不到就禁用这个入口 —— 那等于把 D102 留下的空位继续空着;
//    · ⇒ 弹窗如实写「**读不到副机在不在用**」,由人决定。
//      这与 D99 裁定④同一条规矩:**置灰但不说原因等于骗人,而给个错的原因更坏**。
// ══════════════════════════════════════════════════════════════════════════════

// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ V22(D115)· 如实记一件**本文件此前根本没做**的事:**它没有关栈。**
//
//  在此之前托盘右键「关闭」那一下走的是:
//    `QueryAsync()` → 弹确认框 → 请客户端优雅退出 → **管理端自己 Shutdown()**。
//  网关与 lan-edge **一个都没停**,原地继续跑。也就是说「关掉整套 AI 栈」这句话
//  在实机上是假的:框弹了、人点了确定、然后什么都没发生。
//
//  ★ 形状与起栈那条**一模一样**,而且是本项目第**五**次:
//    判据写好了(`QueryAsync` / `ConfirmText`,还配了自检)、文案写好了、入口接上了,
//    **而真正动手的那一步压根不存在**。
//    ⇒ 「调了 terminate 就算」不算数,可这里连 terminate 都没有。
//
//  ★★ V22 补的是 `StopAsync`,而且**停完要验**(边界③):
//    8080 / 8442 真的不通了、没有孤儿 llama-server、显存回落 —— 验不过就如实说还剩什么。
// ══════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using HostSetup = LocalAI.Client.Services.HostSetup;

namespace LocalAI.Admin.Services;

/// <summary>关栈之前问一句「现在关会不会切断别人」的结果。</summary>
/// <param name="Known">判据**读到了没有**。false = 读不到 —— 那时 <paramref name="Blocking"/> 无意义。</param>
/// <param name="Safe">读到了、而且现在关不会切断别人。</param>
/// <param name="Why">给人看的说明。★ 读不到时必须说"读不到",不许写成"没人在用"。</param>
public sealed record StopVerdict(bool Known, bool Safe, string Why);

public static class StackStop
{
    /// <summary>
    /// 中枢那条判据的路由 —— **今天还不存在**,等 V16 在网关那侧开出来。
    /// ★ 名字先定下来放这儿,是为了让"缺的是哪一条"具体、可搜、可交接,
    ///   而不是留一句"以后再说"。
    /// </summary>
    public const string SafeToStopRoute = "/v1/stack/safe-to-stop";

    /// <summary>
    /// 问一次「现在关栈会不会切断别人」。
    /// <para>★★ 今天必然返回 <c>Known=false</c> —— 见文件头那段 DEBT。
    /// 这**不是**占位实现:它如实表达了"判据在中枢、而入口还没开",
    /// 而调用方(托盘关闭)据此弹的是「读不到,仍要关吗」而不是「没人在用,关吧」。</para>
    /// </summary>
    public static Task<StopVerdict> QueryAsync()
        => Task.FromResult(new StopVerdict(
            Known: false, Safe: false,
            Why: "读不到副机在不在用 —— 中枢那条判据(safe_to_stop_stack)今天还没有对外的路由,"
               + $"计划开在 {SafeToStopRoute}。\n"
               + "★ 这里不替你猜:猜【应该没人用】然后替你关掉,正是 D102 撤掉的那种推断。"));

    /// <summary>
    /// 关栈之前给人看的那句话。★ 三种处境要说三句**不同**的话 ——
    /// 把"读不到"和"没人在用"合成一句,就是给一个**错的**理由。
    /// </summary>
    /// <remarks>
    /// ★★ 「读不到」这句话由**本函数自己**说,不靠调用方的 Why 带出来。
    ///   第一版把它交给了 Why —— 而 Why 是外面传进来的:换一个调用方、或者哪天 Why 改了措辞,
    ///   弹窗就会退化成一句光秃秃的「要关掉整套 AI 栈吗?」,把「我不知道副机在不在用」这件事**吞掉**。
    ///   自检当场抓到了这一条(喂一个不含「读不到」的 Why,断言红)。
    ///   ⇒ 承重的措辞不能寄存在别人手里。
    /// </remarks>
    public static string ConfirmText(StopVerdict v) => v switch
    {
        { Known: false } => "要关掉整套 AI 栈吗?\n\n"
                          + "★ 现在【读不到】副机在不在用 —— 下面是原因,请你自己判断:\n"
                          + v.Why,
        { Safe: false } => "副机正在用,仍要关吗?\n\n" + v.Why,
        _ => "现在关不会切断别人。要关掉整套 AI 栈吗?\n\n" + v.Why,
    };

    // ════════════════════════════════════════════════════════════════════
    //  真的把栈停掉(V22)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>关栈干了什么、还剩什么。★ <paramref name="AllGone"/> 是**验出来的**,不是"调过 Kill 了"。</summary>
    /// <param name="AllGone">停干净了没有 —— 判据是**端口不通 + 没有孤儿后端**,不是调用成功。</param>
    /// <param name="Did">逐条:动了谁。</param>
    /// <param name="Left">逐条:还剩谁(空表才算干净)。</param>
    /// <param name="Untouched">逐条:**有意没动**的(认领的后端),以及为什么。</param>
    public sealed record StopReport(bool AllGone, IReadOnlyList<string> Did,
                                    IReadOnlyList<string> Left, IReadOnlyList<string> Untouched)
    {
        public string ToText()
        {
            var s = new System.Text.StringBuilder();
            s.AppendLine(AllGone ? "整套 AI 栈已经停掉了(已验)。" : "★ 没有完全停干净 —— 下面是还剩的东西。");
            if (Did.Count > 0) { s.AppendLine(); s.AppendLine("停掉了:"); foreach (var d in Did) s.AppendLine("  · " + d); }
            if (Untouched.Count > 0)
            {
                s.AppendLine(); s.AppendLine("【有意没动】:");
                foreach (var u in Untouched) s.AppendLine("  · " + u);
            }
            if (Left.Count > 0) { s.AppendLine(); s.AppendLine("★ 还在跑:"); foreach (var l in Left) s.AppendLine("  · " + l); }
            return s.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// 把栈停掉。★★★ 边界(用户明划,不可让步):**停我们起的那些,
    /// `adopt_running()` 认领的一个都不动**。
    ///
    /// <para>★ 顺序有讲究:先 Edge(断掉对外入口)→ 再网关(它一停就不会再按需装载新后端)
    /// → 最后收拾后端。反过来的话,刚杀完后端网关又给你装一个回来。</para>
    ///
    /// <para>★★ 网关用 <c>Kill(entireProcessTree: true)</c>,这一条**同时就是那条边界**:
    /// 进程树只包含**它自己生出来的**后端 —— 也就是我们这套栈起的那些。
    /// `adopt_running()` 认领的后端是别人起的,结构上**不是**网关的子孙,
    /// 所以这个调用**够不着它们**。边界不是靠一个 if 守的,是靠进程树本身。</para>
    ///
    /// <para>★★★ 停完**要验**(边界③):端口真的不通了、没有孤儿后端。
    /// 「调了 terminate 就算」不算 —— 本项目已经栽过一次(`loader.shutdown()` 零调用点)。</para>
    /// </summary>
    public static async Task<StopReport> StopAsync()
    {
        var did = new List<string>();
        var left = new List<string>();
        var untouched = new List<string>();

        // ★ 显存:停之前先读一次。★★ 它是**佐证**,不是判据 ——
        //   判据是端口与进程(下面第⑤步)。显存读不到时不影响结论,只是少一句佐证。
        var vramBefore = VramUsedMb();

        // ── ① 记下认领的那一批,并说清楚为什么不动它们 ────────────────
        var adopted = StackOwnership.AdoptedBackendPids();
        var hasSnapshot = StackOwnership.HasBackendSnapshot();
        if (adopted.Count > 0)
            untouched.Add($"起栈之前就在跑的 {adopted.Count} 个 {StackOwnership.BackendProcName}"
                          + $"(PID {string.Join("、", adopted)})—— 那是网关 adopt_running() 认领的那一批,"
                          + "不是我们起的,**不动**。");

        var (ledgerGw, ledgerEdge) = StackOwnership.Owned();
        var (handleGw, handleEdge) = HostProvision.StartedHandles;

        // ── ② Edge:先断对外入口 ──────────────────────────────────────
        var edge = Alive(handleEdge) ?? ledgerEdge ?? ByName("localai-lan-edge").FirstOrDefault();
        if (edge is not null) Stop(edge, "LAN Edge(localai-lan-edge)", did, tree: true);
        else did.Add("LAN Edge:没找到在跑的进程(可能本来就没起)。");

        // ── ③ 网关:连同它生出来的后端一起(进程树 = 我们起的那些)────
        var gw = Alive(handleGw) ?? ledgerGw ?? GatewayByPort();
        if (gw is not null) Stop(gw, $"统一入口网关(PID {gw.Id},连同它起的后端)", did, tree: true);
        else did.Add($"网关:没找到在跑的进程(127.0.0.1:{HostSetup.GatewayPort} 上也没人在听)。");

        // ── ④ 收尾:被甩掉的后端(改过父进程的那种)────────────────────
        if (!hasSnapshot)
        {
            // ★ 没拍过快照 ⇒ 分不出哪些是我们的。**一个都不动**,并且说出来 ——
            //   猜"应该是这几个吧"然后杀掉,代价是杀掉用户正在用的进程。
            var live = StackOwnership.LiveBackends();
            if (live.Count > 0)
                untouched.Add($"机器上还有 {live.Count} 个 {StackOwnership.BackendProcName},"
                              + "而这一轮**没有起栈记录**(管理端重启过,或者栈是手工起的)—— "
                              + "分不出哪些是我们起的、哪些是你自己开着的,所以**一个都没动**。"
                              + "要停的话请自己确认后再停。");
        }
        else
        {
            foreach (var b in StackOwnership.OursToStopBackends())
            {
                if (b.HasExited) continue;
                Stop(b, $"{StackOwnership.BackendProcName}(PID {b.Id},起栈后出现 ⇒ 是这套栈起的)", did, tree: true);
            }
        }

        // ── ⑤ 验:★ 不看有没有调过 Kill,看**端口通不通、进程还在不在** ──
        //   给一点时间让端口真的释放 —— 进程没了到端口回收之间有一小段。
        var gone = false;
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(400);
            var stack = await HostProvision.ProbeStackAsync();
            if (stack.Gateway.Outcome == SetupOutcome.Failed && stack.Edge.Outcome == SetupOutcome.Failed)
            { gone = true; break; }
        }
        if (!gone)
        {
            var s = await HostProvision.ProbeStackAsync();
            if (s.Gateway.Outcome != SetupOutcome.Failed)
                left.Add($"网关还在应答 http://127.0.0.1:{HostSetup.GatewayPort}/health");
            if (s.Edge.Outcome != SetupOutcome.Failed)
                left.Add($"中枢的回环管理面还在应答 127.0.0.1:{HubAdmin.AdminPort}");
        }

        // ★ 孤儿后端:停完还剩的、且**不在认领名单里**的 —— 那就是真的漏了
        var adoptedSet = adopted.ToHashSet();
        var orphans = StackOwnership.LiveBackends().Where(p => !adoptedSet.Contains(p.Id)).ToList();
        if (hasSnapshot && orphans.Count > 0)
            left.Add($"还有 {orphans.Count} 个孤儿 {StackOwnership.BackendProcName}"
                     + $"(PID {string.Join("、", orphans.Select(p => p.Id))})—— 它们不在认领名单里,"
                     + "本该跟着网关一起走。");

        // ★ 显存佐证:后端真的走了的话它应该回落。读不到就不说 —— 不编。
        var vramAfter = VramUsedMb();
        if (vramBefore is { } b0 && vramAfter is { } a1)
            did.Add($"显存:{b0} MiB → {a1} MiB"
                    + (a1 < b0 ? $"(回落了 {b0 - a1} MiB)" : "(没有回落 —— 多半是还有后端占着,或者别的程序在用)"));

        if (left.Count == 0) StackOwnership.Clear();   // ★ 停干净了才清账,没停干净留着好查
        StackBoot.Forget();                            // ★ 界面别再显示上一次起栈的绿灯

        return new StopReport(left.Count == 0, did, left, untouched);
    }

    /// <summary>现在用了多少显存(MiB)。★ 读不到返回 null —— **不返回 0**:
    /// 0 会被读成"显存空了",而那正是我们要证明的那件事,不能由一次失败的读取来"证明"。</summary>
    static int? VramUsedMb()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi.exe",
                "--query-gpu=memory.used --format=csv,noheader,nounits")
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var first = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return int.TryParse(first, out var mb) ? mb : null;
        }
        catch { return null; }   // 没有 N 卡 / 没装驱动 —— 那不是错误,只是没有这句佐证
    }

    /// <summary>停一个进程,并**等它真的没了**。★ 不等的话下一步的"验"会验到一个正在退出的进程。</summary>
    static void Stop(Process p, string label, List<string> did, bool tree)
    {
        try
        {
            var pid = p.Id;
            p.Kill(entireProcessTree: tree);
            // ★ 等它真的退出。等不到就如实记 —— 不许写成"已停掉"。
            if (p.WaitForExit(8000)) did.Add(label + ":已停。");
            else did.Add(label + $":★ 发过停止信号,但 8 秒内它没有退出(PID {pid})。");
        }
        catch (InvalidOperationException) { did.Add(label + ":它已经不在了。"); }
        catch (Exception ex) { did.Add(label + ":没能停掉 —— " + ex.GetType().Name + ": " + ex.Message); }
    }

    static Process? Alive(Process? p)
    {
        try { return p is not null && !p.HasExited ? p : null; }
        catch { return null; }
    }

    static List<Process> ByName(string name)
    {
        try { return Process.GetProcessesByName(name).ToList(); }
        catch { return new List<Process>(); }
    }

    /// <summary>
    /// 没有账本时,靠**谁在 8080 上听**把网关认出来。
    /// <para>★ 这条判据是精确的:网关就是"在 127.0.0.1:8080 上听的那个进程" —— 全仓都拨它。
    /// ★★ 但仍然**再验一次进程名**:万一网关没起,那个口上坐着的是别的服务,
    /// 认错了就等于替用户杀掉一个无关进程。宁可认不出来。</para>
    /// </summary>
    static Process? GatewayByPort()
    {
        try
        {
            var psi = new ProcessStartInfo("netstat.exe", "-ano -p TCP")
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
            };
            using var np = Process.Start(psi);
            if (np is null) return null;
            var text = np.StandardOutput.ReadToEnd();
            np.WaitForExit(5000);
            var want = $"127.0.0.1:{HostSetup.GatewayPort}";
            foreach (var line in text.Split('\n'))
            {
                if (!line.Contains(want, StringComparison.Ordinal)) continue;
                if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !int.TryParse(parts[^1], out var pid)) continue;
                var p = Process.GetProcessById(pid);
                // ★ 网关是 uvicorn,跑在 python 里。不是 python 就**不认** —— 见上面第二段。
                return p.ProcessName.Contains("python", StringComparison.OrdinalIgnoreCase) ? p : null;
            }
        }
        catch { /* 认不出来就是没有 —— 那时上面会如实说"没找到" */ }
        return null;
    }
}
