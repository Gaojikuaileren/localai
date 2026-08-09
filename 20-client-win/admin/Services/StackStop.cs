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

// ══════════════════════════════════════════════════════════════════════════════
//  ★★★★ V23 · 修 V22 在这里留下的**三条误杀**。用户裁定(2026-08-09)逐字:
//    「停我们起的那些,`adopt_running()` 认领的一个不动;
//      **判不出归属时如实说不知道、不动手**,不是按端口猜一个杀。」
//
//  被删掉的两条"猜":
//    ① `GatewayByPort()` —— 在 127.0.0.1:8080 LISTENING **且进程名含 python** 就
//       `Kill(entireProcessTree: true)`。没有 PID 比对、没有 StartTime、不过账本。
//       ★ 它上面还写着「这条判据是精确的…再验一次进程名可防误杀」——
//         而用户自己在 8080 上跑的 `http.server` / Flask / uvicorn / Jupyter
//         **进程名恰恰就是 python**。那道"二次校验"对这一场景等于零,
//         而它的措辞让人以为这里已经想过误杀了。⇒ 判据和那句话一起删掉。
//    ② `ByName("localai-lan-edge").FirstOrDefault()` —— 按名字杀。用户手工跑
//       `90-ops/start-stack.ps1` 起的 edge 会被杀,而那**不是我们起的**。
//
//  ★★ 换上的**不是**一个更聪明的猜法,是**如实说不知道**:
//    认不出归属时,把「谁在那个口上听 / 机器上有几个同名进程」写进 `StopReport.Unattributed`,
//    **并且由 `App.RealCloseAsync` 在关掉之前弹给人看**。代价是「管理端重启过就关不掉网关了」
//    —— 这个代价是用户明着认的,而它的反面是**替用户杀掉一个无关进程**,那没法撤销。
//
//  ★★★ 「不动手」只是一半 —— 另一半是**说**。第一版只做了前一半:
//    认不出归属 ⇒ 不动 ⇒ 端口探不到人 ⇒ `AllGone=true` ⇒ **管理端安静地关掉自己**,
//    而 `ToText()` 那句「整套 AI 栈已经停掉了(已验)」根本没人看见(它只在失败路径上被调用)。
//    ⇒ 用户点了「关闭」,屏幕上什么都没说,而他自己那个 python 还在 8080 上跑着。
//    那和 V22 的误杀是**同一条毛病的两面**:一个替他做了决定,一个瞒了他一件事。
//
//  ★★★ 第三条在 `StackOwnership`:陈旧快照(见该文件头 V23 那段)。
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
    /// <param name="Untouched">逐条:**有意没动**的(`adopt_running()` 认领的那一批),以及为什么。</param>
    /// <param name="Unattributed">
    /// 逐条:**认不出归属所以没动**的 —— 与 <paramref name="Untouched"/> 是**两件事**。
    /// <para>★★★ V23 把它单独拆出来,理由不是分类癖:
    /// 「认领的那批不动」是**用户早就知道的规矩**,每次关栈都会出现,拿它去弹窗就是噪音;
    /// 而「8080 上有个东西、我们认不出它是不是自己起的、所以没动」是**这一次的意外**,
    /// 用户不知道 —— 不说的话,他点了「关闭」、管理端安静退出,他会以为栈全停了。
    /// ⇒ 「不动手」只做了一半,另一半是**如实说**(用户裁定原话)。两者合成一个列表就说不清了。</para>
    /// </param>
    public sealed record StopReport(bool AllGone, IReadOnlyList<string> Did,
                                    IReadOnlyList<string> Left, IReadOnlyList<string> Untouched,
                                    IReadOnlyList<string> Unattributed)
    {
        public string ToText()
        {
            var s = new System.Text.StringBuilder();
            // ★★★ 标题分三种,不是两种(V23)。
            //   在此之前只要端口不通就打「整套 AI 栈已经停掉了(已验)」——
            //   而 V23 之后「有东西还在跑、只是我们不敢动它」变成了**常见结局**,
            //   那句话会和它下面那张「还在跑但没动」的单子当场自相矛盾。
            s.AppendLine(!AllGone ? "★ 没有完全停干净 —— 下面是还剩的东西。"
                       : Unattributed.Count > 0
                         ? "AI 栈的入口**都不通了(已验)**。★ 但下面这些**还在跑** —— "
                           + "我们认不出它们是不是自己起的,所以没动。请你自己看一眼。"
                       : Untouched.Count > 0
                         ? "我们起的那些都停掉了(已验:端口不通)。★ 下面是**有意没动**的那批。"
                         : "整套 AI 栈已经停掉了(已验)。");
            if (Did.Count > 0) { s.AppendLine(); s.AppendLine("停掉了:"); foreach (var d in Did) s.AppendLine("  · " + d); }
            if (Unattributed.Count > 0)
            {
                s.AppendLine(); s.AppendLine("★ 还在跑,但【我们认不出归属,没敢动】:");
                foreach (var u in Unattributed) s.AppendLine("  · " + u);
            }
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
    /// <para>★★★★ V23 补一句 —— 上面那段**只在我们认得出那个网关时才成立**。
    /// 「进程树就是边界」这句话有个前提:被杀的那棵树的根**得是我们起的那个网关**。
    /// V22 那一版靠「谁在 8080 上听 + 进程名像 python」去找那个根,于是这句漂亮话
    /// 反过来变成了误杀的放大器:认错了根,整棵树跟着陪葬。
    /// ⇒ 现在归属只由**句柄 + 账本**回答(<see cref="StackOwnership"/>),
    /// 认不出就**不动手、并如实说不知道**。边界是**两层**:先证明得了归属,再靠进程树。</para>
    ///
    /// <para>★★★ 停完**要验**(边界③):端口真的不通了、没有孤儿后端。
    /// 「调了 terminate 就算」不算 —— 本项目已经栽过一次(`loader.shutdown()` 零调用点)。</para>
    /// </summary>
    public static async Task<StopReport> StopAsync()
    {
        var did = new List<string>();
        var left = new List<string>();
        var untouched = new List<string>();
        // ★ 认不出归属的那些单独一张单子 —— 见 StopReport.Unattributed 的说明。
        var unattributed = new List<string>();

        // ★ 显存:停之前先读一次。★★ 它是**佐证**,不是判据 ——
        //   判据是端口与进程(下面第⑤步)。显存读不到时不影响结论,只是少一句佐证。
        var vramBefore = VramUsedMb();

        // ── ① 记下认领的那一批,并说清楚为什么不动它们 ────────────────
        var adopted = StackOwnership.AdoptedBackendPids();
        if (adopted.Count > 0)
            untouched.Add($"起栈之前就在跑的 {adopted.Count} 个 {StackOwnership.BackendProcName}"
                          + $"(PID {string.Join("、", adopted)})—— 那是网关 adopt_running() 认领的那一批,"
                          + "不是我们起的,**不动**。");

        var (ledgerGw, ledgerEdge) = StackOwnership.Owned();
        var (handleGw, handleEdge) = HostProvision.StartedHandles;

        // ★★★ 归属只认这两样,**没有第三样**:
        //   · 本进程起的那个句柄(还活着);
        //   · 账本里那条(PID + 进程名 + 启动时刻**三样都对得上**)。
        //   ⇒ 「谁在 8080 上听」「机器上有个同名进程」都**不是**归属证据(见文件头 V23 那段)。
        var gw = Alive(handleGw) ?? ledgerGw;
        var edge = Alive(handleEdge) ?? ledgerEdge;

        // ★ 这一位必须**在杀网关之前**取:杀完再问「网关认得出来吗」,答案会变成"不在了"。
        var ownedGatewayProven = gw is not null;

        // ── ② Edge:先断对外入口 ──────────────────────────────────────
        if (edge is not null) Stop(edge, $"LAN Edge(PID {edge.Id})", did, tree: true);
        else
        {
            var strays = ByName(StackOwnership.EdgeProcName).Where(p => !Exited(p)).ToList();
            if (strays.Count == 0)
                did.Add($"LAN Edge:机器上没有 {StackOwnership.EdgeProcName} 在跑(它多半本来就没起)。");
            else
                unattributed.Add($"机器上有 {strays.Count} 个 {StackOwnership.EdgeProcName}"
                              + $"(PID {string.Join("、", strays.Select(p => p.Id))}),"
                              + "而我们**没有它的归属账**(本进程没起过它,账本里那条也对不上)—— "
                              + "手工跑 start-stack.ps1 起的 Edge 就长这样。"
                              + "★ 分不出是不是我们起的,所以**没动它**;要停请自己确认后再停。");
        }

        // ── ③ 网关:连同它生出来的后端一起(进程树 = 我们起的那些)────
        if (gw is not null) Stop(gw, $"统一入口网关(PID {gw.Id},连同它起的后端)", did, tree: true);
        else
        {
            var onPort = WhoIsOnGatewayPort();
            if (onPort is null)
                did.Add($"网关:没有归属账,127.0.0.1:{HostSetup.GatewayPort} 上也没人在听 ——"
                        + "它多半本来就没起。");
            else
                unattributed.Add($"127.0.0.1:{HostSetup.GatewayPort} 上有人在听"
                              + $"(PID {onPort.Value.Pid} · {onPort.Value.Name}),"
                              + "而我们**没有它的归属账**(本进程没起过它,账本里那条也对不上)。"
                              + "★ 在这个口上听、进程名像 python 的**不一定是网关** —— "
                              + "你自己的 http.server / Flask / uvicorn / Jupyter 长得一模一样,"
                              + "而这里一旦认错就是连着子进程树一起杀掉。⇒ **没动它**。");
        }

        // ── ④ 收尾:被甩掉的后端(改过父进程的那种)────────────────────
        //   ★ 判据只有一处:StackOwnership.BackendSnapshotUsable(见那儿的三道)。
        var snapshotOk = StackOwnership.BackendSnapshotUsable(ownedGatewayProven, out var snapWhy);
        if (!snapshotOk)
        {
            // ★ 分不出哪些是我们的 ⇒ **一个都不动**,并且把"为什么分不出"说出来 ——
            //   猜"应该是这几个吧"然后杀掉,代价是杀掉用户正在用的进程。
            var live = StackOwnership.LiveBackends();
            if (live.Count > 0)
                unattributed.Add($"机器上还有 {live.Count} 个 {StackOwnership.BackendProcName}"
                              + $"(PID {string.Join("、", live.Select(p => p.Id))})—— {snapWhy}。"
                              + "⇒ 分不出哪些是我们起的、哪些是你自己开着的,所以**一个都没动**。"
                              + "要停的话请自己确认后再停。");
        }
        else
        {
            foreach (var b in StackOwnership.OursToStopBackends(ownedGatewayProven))
            {
                if (Exited(b)) continue;
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
        //   ★★ 只有快照作数时这句话才成立:快照不作数时「不在认领名单里」什么都不代表,
        //     那时它们已经在上面的 Untouched 里如实登记过了,再报一次"孤儿"是给个错的理由。
        var adoptedSet = adopted.ToHashSet();
        var orphans = StackOwnership.LiveBackends().Where(p => !adoptedSet.Contains(p.Id)).ToList();
        if (snapshotOk && orphans.Count > 0)
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

        return new StopReport(left.Count == 0, did, left, untouched, unattributed);
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

    /// <summary>进程没了没有。★ 问不出来时当作**还在**(不 continue 掉),
    /// 由 <see cref="Stop"/> 里的 try/catch 去如实记 —— 与"读不到就当它不在"相反的方向。</summary>
    static bool Exited(Process p)
    {
        try { return p.HasExited; }
        catch { return false; }
    }

    /// <summary>按名字列进程。★★ 本函数的返回值**不喂给 Kill** —— 它只用来
    /// 在报告里如实说「机器上有几个同名的、我们认不出归属」。名字不是归属证据。</summary>
    static List<Process> ByName(string name)
    {
        try { return Process.GetProcessesByName(name).ToList(); }
        catch { return new List<Process>(); }
    }

    /// <summary>
    /// 谁在网关那个口上听。★★★ **只用来说话,不用来动手** ——
    /// 返回的是 PID 与进程名,**不是一个可以 Kill 的 <see cref="Process"/>**,
    /// 这一点是有意的:V22 那一版返回 Process,而调用方转手就把它杀了。
    /// <para>★ 「在 127.0.0.1:8080 上听、进程名含 python」**不是**「它是我们起的网关」。
    /// 用户自己的 http.server / Flask / uvicorn / Jupyter 满足同样的条件,
    /// 而杀它是 `entireProcessTree: true` —— 连着它的子进程树一起。
    /// ⇒ 归属只由句柄与账本回答(见 <see cref="StopAsync"/> 第①段)。</para>
    /// </summary>
    static (int Pid, string Name)? WhoIsOnGatewayPort()
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
                string name;
                try { name = Process.GetProcessById(pid).ProcessName; }
                catch { name = "(读不到进程名)"; }
                return (pid, name);
            }
        }
        catch { /* 问不出来就说没问出来 —— 上面会照常走"没有归属账"那条路 */ }
        return null;
    }
}
