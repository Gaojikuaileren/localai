// V23 -- 关栈那一半的**行为**判据。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ 为什么这个文件必须存在:在它之前,`StackStop.StopAsync`(89 行,含 Kill)
//    **全仓零断言** —— `StopAsync` / `StopReport` / `AllGone` / `ToText` /
//    `OursToStopBackends` 在任何 selftest、pytest、ps1 里都没有引用,
//    唯一的非定义引用是 `App.xaml.cs` 里那一行生产调用。
//
//  ⇒ 把 `StopAsync` 整段换成 `return new StopReport(true, [], [], [])`,
//    **一条判据都不会红**,而弹窗会照样打出「整套 AI 栈已经停掉了(已验)」。
//    验收④(托盘 → 关闭 → 栈真的没了)当时**不是被跳过 —— 它不存在**:
//    零 PASS、零 FAIL、零 SKIP。
//
//  ★★ 判据的形状是有意选的:**真起几个替身进程,再看它们死没死**。
//    扫源码判不出「这个按钮点下去到底做没做事」(ASSERTION-PITFALLS 第 14 条),
//    而「调过 Kill 了」也不算 —— 本项目已经栽过一次(`loader.shutdown()` 零调用点)。
//
//  ★★★ 替身用的是系统自带的 `ping.exe` 改个名字。我们要的只是
//    「一个活着的、名字叫 X 的进程」—— 名字正是被测代码用来认人的东西。
// ══════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LocalAI.Admin.Services;
using LocalAI.Client.Services;

namespace LocalAI.Admin;

public static partial class Selftest
{
    // ★ 这两个名字在 `HostSetup` / `HubAdmin` 里是字面量,而 `HostSetup.cs` 在**客户端目录**
    //   (本车道的禁区)⇒ 没法把它们提成共用常量。
    //   ⇒ 这里各写一份,但**下面配了元断言**:设了它之后 `HostSetup.GatewayPort` /
    //     `HubAdmin.AdminPort` 真的跟着变。名字漂了那条元断言当场红,
    //     而不是让整段悄悄打到真的 8080 上去(那时这些断言测的是「你机器上现在跑没跑栈」)。
    const string GatewayPortEnvVar = "LOCALAI_GATEWAY_PORT";
    const string AdminPortEnvVar = "LOCALAI_ADMIN_PORT";

    // ────────────────────────────────────────────────────────── 关栈:归属与误杀
    static void RunStackStop()
    {
        Console.WriteLine("\n-- 关栈(真起替身进程) --");

        var tmp = Path.Combine(Path.GetTempPath(), "v23-stop-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        var savedAdminState = Environment.GetEnvironmentVariable(AppPaths.AdminStateEnvVar);
        var savedGwPort = Environment.GetEnvironmentVariable(GatewayPortEnvVar);
        var savedAdminPort = Environment.GetEnvironmentVariable(AdminPortEnvVar);
        var decoys = new List<Process>();

        try
        {
            // ── 元断言:先证明这一段**测的不是你的真环境** ────────────────
            Environment.SetEnvironmentVariable(AppPaths.AdminStateEnvVar, Path.Combine(tmp, "admin-state"));
            Assert(StackOwnership.LedgerPath.StartsWith(tmp, StringComparison.OrdinalIgnoreCase),
                "★★ 元断言:这一段用的是**临时账本** —— 它一旦指回你真实的 stack-owned.json,"
                + "下面每一条都会在你的机器上动真进程");

            var gwPort = FreeLoopbackPort();
            var adminPort = FreeLoopbackPort();
            Environment.SetEnvironmentVariable(GatewayPortEnvVar, gwPort.ToString());
            Environment.SetEnvironmentVariable(AdminPortEnvVar, adminPort.ToString());
            Assert(HostSetup.GatewayPort == gwPort && HubAdmin.AdminPort == adminPort,
                $"★★ 元断言:{GatewayPortEnvVar} / {AdminPortEnvVar} 真的改得动那两个端口 —— "
                + "改不动的话下面的「停干净了」测的是**你机器上现在跑没跑栈**,与被测代码无关");

            // ═══════════════════════════════════════════════════════════════
            //  ① 账本里记着的 ⇒ **真的停掉**(这一条就是「StopAsync 换成空实现必红」那一条)
            // ═══════════════════════════════════════════════════════════════
            StackOwnership.Clear();
            var owned = StartDecoy(Path.Combine(tmp, "owned"), "localai-v23-gw-decoy", out var whyOwned);
            if (owned is null)
            {
                Skip("★★★ 「账本里记着的进程,关栈真的把它停掉」", "起不了替身进程:" + whyOwned);
            }
            else
            {
                decoys.Add(owned);
                StackOwnership.NoteStarted(StackOwnership.Component.Gateway, owned);
                var (ledgerGw, _) = StackOwnership.Owned();
                Assert(ledgerGw is not null && ledgerGw.Id == owned.Id,
                    "★ 元断言:账本认得出刚记下的那个进程(认不出的话下面那条会因为**假理由**而绿)");

                var rep = StackStop.StopAsync().GetAwaiter().GetResult();

                Assert(WaitGone(owned, 8000),
                    "★★★ 账本里记着的网关 —— 关栈**真的把它停掉了**。"
                    + "★ 判据是【那个进程没了】,不是【调过 Kill 了】:本项目已经栽过一次"
                    + "(`loader.shutdown()` 零调用点),而「调过了」与「做到了」在日志里长得一样");
                Assert(rep.Did.Any(d => d.Contains(owned.Id.ToString(), StringComparison.Ordinal)),
                    "★★ 而且报告里**逐条说了动过谁**(带 PID)—— 一句「已停掉」不足以让人复核");
                Assert(rep.AllGone,
                    "★ 停完之后那两个口都探不到人 ⇒ 判定停干净了(判据是端口,不是调用成功)");
            }

            // ═══════════════════════════════════════════════════════════════
            //  ② 没有归属账的**同名** Edge ⇒ 一个字都不许动
            //     ★ 这一条钉的是 V22 那行 `ByName("localai-lan-edge").FirstOrDefault()`:
            //       用户手工跑 `90-ops/start-stack.ps1` 起的 Edge 会被它杀掉。
            // ═══════════════════════════════════════════════════════════════
            StackOwnership.Clear();
            var already = SafeByName(StackOwnership.EdgeProcName);
            if (already.Count > 0)
            {
                // ★ 机器上真的有 Edge 在跑 ⇒ 起了替身也分不出「它没死」是因为判据对
                //   还是因为被测代码挑中了另一个。如实跳过,不做一条测不到东西的断言。
                Skip($"★★★ 「没有归属账就不动同名 {StackOwnership.EdgeProcName}」",
                     $"机器上已经有 {already.Count} 个真的 {StackOwnership.EdgeProcName} 在跑"
                     + $"(PID {string.Join("、", already.Select(p => p.Id))})—— "
                     + "替身与真身同名,这一趟分不出被测代码挑中的是哪一个。"
                     + "★ 关掉那些再跑一次这条才算数");
            }
            else
            {
                var stray = StartDecoy(Path.Combine(tmp, "stray"), StackOwnership.EdgeProcName, out var whyStray);
                if (stray is null)
                {
                    Skip($"★★★ 「没有归属账就不动同名 {StackOwnership.EdgeProcName}」", "起不了替身进程:" + whyStray);
                }
                else
                {
                    decoys.Add(stray);
                    Assert(HostProvision.StartedHandles is (null, null),
                        "★ 元断言:本进程没有起过栈 ⇒ 归属只可能来自账本,而账本刚清空了");
                    var rep = StackStop.StopAsync().GetAwaiter().GetResult();

                    Assert(!Gone(stray),
                        $"★★★ 机器上有个 {StackOwnership.EdgeProcName},而账本里没有它 ⇒ **一个字都不许动**。"
                        + "★ 用户手工跑 90-ops\\start-stack.ps1 起的 Edge 就长这样 —— "
                        + "「名字对得上」不是「是我们起的」");
                    Assert(rep.Unattributed.Any(u => u.Contains(stray.Id.ToString(), StringComparison.Ordinal)),
                        "★★ 而且要**如实说出来**,写进【认不出归属】那张单子(不是【有意没动】那张)——"
                        + "只有前者会在关掉自己之前弹给人看。"
                        + "★ 不说的话人会以为关栈已经把它带走了(D99 裁定④:置灰不说原因等于骗人)");
                    Assert(!rep.Did.Any(d => d.Contains(stray.Id.ToString(), StringComparison.Ordinal)),
                        "★ 它不许出现在【停掉了】那一栏里");
                }
            }

            // ═══════════════════════════════════════════════════════════════
            //  ③ 8080 上坐着**别人的 python** ⇒ 不许按端口猜一个杀
            //     ★ 这一条复现的就是用户手上那个洞:V22 的 GatewayByPort() 见到
            //       「127.0.0.1:8080 LISTENING + 进程名含 python」就 Kill(整棵进程树)。
            //       用户自己的 http.server / Flask / uvicorn / Jupyter 满足同样的条件。
            // ═══════════════════════════════════════════════════════════════
            StackOwnership.Clear();
            var pyPort = FreeLoopbackPort();
            Environment.SetEnvironmentVariable(GatewayPortEnvVar, pyPort.ToString());
            var py = StartPythonListener(pyPort, out var whyPy);
            if (py is null)
            {
                Skip("★★★ 「网关口上坐着别人的 python,不许按端口猜一个杀」", whyPy);
            }
            else
            {
                decoys.Add(py);
                Assert(py.ProcessName.Contains("python", StringComparison.OrdinalIgnoreCase),
                    "★ 元断言:替身的进程名真的含 python —— 不含的话它躲开了被测判据,这条测了个寂寞");
                var rep = StackStop.StopAsync().GetAwaiter().GetResult();

                Assert(!Gone(py),
                    $"★★★ 127.0.0.1:{pyPort} 上有个别人的 python 在听,而账本里没有它 ⇒ **不许动它**。"
                    + "★ 这是本轮唯一一条会**损坏用户东西**的缺陷:Kill(entireProcessTree: true) "
                    + "会连着它的整棵子进程树一起带走");
                Assert(rep.Unattributed.Any(u => u.Contains(py.Id.ToString(), StringComparison.Ordinal)),
                    "★★ 并且**如实说不知道**:那个口上有人在听、我们没有它的归属账、所以没动 —— "
                    + "而不是安静地跳过(安静跳过与「那儿本来就没人」长得一模一样)");
                // ★★★ 「不动手」只是一半 —— 另一半是**说**。这一条钉的就是那另一半:
                //   端口探不到人 ⇒ AllGone=true ⇒ 管理端会**关掉自己**,
                //   而 ToText() 那句「整套 AI 栈已经停掉了(已验)」在这个处境下是**假的**。
                Assert(!rep.ToText().Contains("整套 AI 栈已经停掉了", StringComparison.Ordinal),
                    "★★★ 有东西还在跑(只是我们不敢动它)时,**不许**打出「整套 AI 栈已经停掉了(已验)」——"
                    + "那是弹窗里唯一让人相信栈没了的那句话");
                Assert(rep.ToText().Contains("认不出归属", StringComparison.Ordinal),
                    "★★ 报告里要有【认不出归属,没敢动】这一栏 —— "
                    + "把它混进【有意没动】里,人就分不出「规矩如此」和「这一次的意外」");
            }
            Environment.SetEnvironmentVariable(GatewayPortEnvVar, gwPort.ToString());

            // ═══════════════════════════════════════════════════════════════
            //  ④ 陈旧快照 ⇒ 后端一个都不认领
            //     ★ 这一条钉的是 StackOwnership 文件头那句**假注释**曾经掩护的洞:
            //       「管理端重启过…账本就是空的」是假的 —— 账本是磁盘文件,
            //       只有停干净时才会被 Clear()。
            // ═══════════════════════════════════════════════════════════════
            {
                var stale = Path.Combine(tmp, "stale-state");
                Environment.SetEnvironmentVariable(AppPaths.AdminStateEnvVar, stale);
                Directory.CreateDirectory(stale);
                var old = DateTime.UtcNow.AddDays(-3).Ticks;
                File.WriteAllText(StackOwnership.LedgerPath,
                    "{\"backendsBeforeStart\":[999001,999002],\"snapshotTaken\":true,"
                    + "\"snapshotUtcTicks\":" + old + "}");

                Assert(!StackOwnership.BackendSnapshotUsable(false, out var whyStale),
                    "★★★ 三天前那份快照 + **认不出我们的网关** ⇒ 快照【不作数】。"
                    + "★ 这正是用户会踩的那一路:上次没停干净 → 账本留着 → 下次开机用户自己起了 "
                    + "llama-server → 它「不在旧快照里」⇒ 旧代码把它判成我们的并杀掉");
                Assert(whyStale.Contains("认不出", StringComparison.Ordinal),
                    "★★ 而且理由要说的是【认不出网关】—— 给一个错的理由比不给更坏(D99 裁定④)");
                Assert(StackOwnership.OursToStopBackends(false).Count == 0,
                    "★★★ ⇒「该我们停的后端」是**空表**:用户手工起的 llama-server 不会被当成我们的");
                Assert(StackOwnership.BackendSnapshotUsable(true, out _),
                    "★ 元断言:**同一份账本**,网关认得出来时快照【是】作数的 —— "
                    + "不然上面三条可能是因为别的原因恒真,红绿都是假的");

                // 旧版本写的账本(没有快照时刻)⇒ 也不作数
                File.WriteAllText(StackOwnership.LedgerPath,
                    "{\"backendsBeforeStart\":[],\"snapshotTaken\":true}");
                Assert(!StackOwnership.BackendSnapshotUsable(true, out var whyNoTime)
                       && whyNoTime.Contains("时刻", StringComparison.Ordinal),
                    "★★ 旧版本写下的账本(没有快照时刻)⇒ 不作数 —— "
                    + "「这份快照是这一轮的还是上一轮遗留的」在那种账本里根本没有记录");

                // 启动时刻这道筛子:两个方向都走一遍
                var backend = StartDecoy(Path.Combine(tmp, "backend"), StackOwnership.BackendProcName, out var whyB);
                if (backend is null)
                {
                    Skip("★★ 「起栈前就在跑的后端不认领」那道启动时刻筛子", "起不了替身进程:" + whyB);
                }
                else
                {
                    decoys.Add(backend);
                    // (a) 快照拍在替身**之后** ⇒ 它是"起栈前就有的" ⇒ 不认领
                    File.WriteAllText(StackOwnership.LedgerPath,
                        "{\"backendsBeforeStart\":[],\"snapshotTaken\":true,\"snapshotUtcTicks\":"
                        + DateTime.UtcNow.AddMinutes(5).Ticks + "}");
                    Assert(!StackOwnership.OursToStopBackends(true).Any(p => p.Id == backend.Id),
                        "★★★ 启动**早于**那张快照的后端 ⇒ 它是起栈前就在跑的,**不认领**。"
                        + "★ 在此之前这道筛子根本不存在:只要 snapshotTaken 是 true,"
                        + "任何不在旧 PID 名单里的 llama-server 都会被判成我们的");
                    // (b) 快照拍在替身**之前** ⇒ 它是起栈后冒出来的 ⇒ 认领
                    File.WriteAllText(StackOwnership.LedgerPath,
                        "{\"backendsBeforeStart\":[],\"snapshotTaken\":true,\"snapshotUtcTicks\":"
                        + DateTime.UtcNow.AddMinutes(-5).Ticks + "}");
                    Assert(StackOwnership.OursToStopBackends(true).Any(p => p.Id == backend.Id),
                        "★★ 反向:启动**晚于**快照的后端**要**认领 —— "
                        + "只验一个方向的话,一个恒返回空表的实现也会全绿");
                }
            }

            // ═══════════════════════════════════════════════════════════════
            //  ⑤ 报告的话:没停干净时不许说「已经停掉了」
            // ═══════════════════════════════════════════════════════════════
            var notClean = new StackStop.StopReport(false, new[] { "动过 X" },
                                                    new[] { "网关还在应答" },
                                                    Array.Empty<string>(), Array.Empty<string>());
            Assert(!notClean.ToText().Contains("已经停掉了", StringComparison.Ordinal),
                "★★★ 没停干净时**不许**打出「整套 AI 栈已经停掉了(已验)」—— "
                + "那句话是弹窗里唯一让人相信栈没了的证据");
            Assert(notClean.ToText().Contains("网关还在应答", StringComparison.Ordinal),
                "★★ 没停干净要把**还剩什么**摆出来,而不是只说一句失败");
            var clean = new StackStop.StopReport(true, new[] { "动过 X" },
                                                 Array.Empty<string>(), Array.Empty<string>(),
                                                 Array.Empty<string>());
            Assert(clean.ToText().Contains("整套 AI 栈已经停掉了", StringComparison.Ordinal),
                "★ **真的**全停了(什么都没剩、也没有认不出归属的)才说「整套 AI 栈已经停掉了」");
            // ★ 三个标题两两不同 —— 合并任意两个,就等于把三种处境里的一种抹掉
            var onlyAdopted = new StackStop.StopReport(true, new[] { "动过 X" }, Array.Empty<string>(),
                                                       new[] { "认领的那批,不动" }, Array.Empty<string>());
            var unknownLeft = new StackStop.StopReport(true, new[] { "动过 X" }, Array.Empty<string>(),
                                                       Array.Empty<string>(), new[] { "8080 上有人在听,没动" });
            string Head(StackStop.StopReport r) => r.ToText().Split('\n')[0];
            Assert(Head(clean) != Head(onlyAdopted) && Head(onlyAdopted) != Head(unknownLeft)
                   && Head(clean) != Head(unknownLeft),
                "★★★ 三种结局要说三句**不同**的话:全停了 / 我们那些停了但认领的还在 / "
                + "有东西还在跑而我们不敢动它。★ 合成一句就是给一个**错的**理由(D99 裁定④)");
            Assert(!Head(unknownLeft).Contains("整套 AI 栈已经停掉了", StringComparison.Ordinal),
                "★★★ 尤其是最后那种:那时说「整套 AI 栈已经停掉了(已验)」是**假话** —— "
                + "它下面就跟着一张「还在跑」的单子");
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine("  FAIL  关栈段自身抛异常: " + ex);
        }
        finally
        {
            foreach (var d in decoys) { try { if (!d.HasExited) d.Kill(entireProcessTree: true); } catch { } }
            Environment.SetEnvironmentVariable(AppPaths.AdminStateEnvVar, savedAdminState);
            Environment.SetEnvironmentVariable(GatewayPortEnvVar, savedGwPort);
            Environment.SetEnvironmentVariable(AdminPortEnvVar, savedAdminPort);
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ④c 生命周期:托盘右键 →「关闭」→ 管理端真的退出 → 栈真的没了
    //
    //  ★★★ 为什么跑在**子进程**里,而不是像原型那样在本进程里:
    //    `SelftestLiveViews` 已经 `new Application()` 了一个,而
    //    「一个 AppDomain 只许有一个 System.Windows.Application」——
    //    在本进程里再 `new App(...)` 会当场抛。
    //    ★★ 更要命的是反过来:`App.Shutdown()` 会把**本线程的 Dispatcher** 一起关掉,
    //      而 `RunLiveViews` 的 403 行全靠 `Dispatcher.PushFrame` —— 谁先跑都会踩死另一边。
    //    ⇒ 起一个自己的子进程(`--selftest-lifecycle`),它有干净的 Application 与 Dispatcher。
    //  ★ 顺带得到两件事:子进程挂死不会拖垮整份自检(父进程有超时),
    //    而且它走的是**真的启动路径**(Program → new App → InitializeComponent → Run)。
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>子进程认这个开关。</summary>
    internal const string LifecycleArg = "--selftest-lifecycle";

    /// <summary>子进程把逐条结果写到这个环境变量指的文件里。</summary>
    const string LifecycleOutEnvVar = "LOCALAI_ADMIN_LIFECYCLE_OUT";

    const int LifecycleTimeoutSeconds = 150;

    /// <summary>子进程里那个看门狗:点了「关闭」之后多久还没退出,就算这条路没通。</summary>
    const int LifecycleWatchdogSeconds = 60;

    static void RunLifecycle()
    {
        Console.WriteLine("\n-- 生命周期(托盘右键真关闭,子进程) --");

        var self = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(self) || !File.Exists(self))
        {
            Owed("★★★ 托盘右键「关闭」→ 管理端退出 → 栈真的没了",
                 "拿不到管理端自己的 exe 路径,起不了那个子进程");
            return;
        }

        var tmp = Path.Combine(Path.GetTempPath(), "v23-life-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        var outFile = Path.Combine(tmp, "lifecycle.txt");
        Process? child = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = self,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(self)!,
            };
            psi.ArgumentList.Add(LifecycleArg);
            psi.Environment[LifecycleOutEnvVar] = outFile;
            // ★ 状态目录与端口全部指到临时的 —— 自检**绝不碰真实档案**,也绝不打真的 8080。
            psi.Environment[AppPaths.AdminStateEnvVar] = Path.Combine(tmp, "admin-state");
            psi.Environment[AppPaths.StateEnvVar] = Path.Combine(tmp, "client-state");
            psi.Environment[GatewayPortEnvVar] = FreeLoopbackPort().ToString();
            psi.Environment[AdminPortEnvVar] = FreeLoopbackPort().ToString();
            // ★★ 自检闸:子进程 `OnStartup` 里有那条自动起栈的生产调用点,
            //   不拦的话一次自检会在用户机器上真起一套栈,而且没人收走它。
            psi.Environment[StackBoot.NoAutoStackEnvVar] = "1";
            // ★ 哨兵变量必须**摘掉**:子进程也是 localai-admin,继承了它就会把父进程的
            //   哨兵文件覆盖成自己的 —— 门禁读到的就是子进程那一份。
            psi.Environment[SentinelEnvVar] = "";

            child = Process.Start(psi);
            if (child is null)
            {
                Owed("★★★ 托盘右键「关闭」→ 管理端退出 → 栈真的没了", "子进程起不来");
                return;
            }
            if (!child.WaitForExit(LifecycleTimeoutSeconds * 1000))
            {
                try { child.Kill(entireProcessTree: true); } catch { }
                Owed("★★★ 托盘右键「关闭」→ 管理端退出 → 栈真的没了",
                     $"子进程 {LifecycleTimeoutSeconds} 秒内没有退出 —— "
                     + "多半是那条路上有个模态框把它挡住了,或者 Run() 根本没返回");
                return;
            }

            var lines = File.Exists(outFile)
                ? File.ReadAllLines(outFile).Where(l => l.Length > 0).ToArray()
                : Array.Empty<string>();
            if (lines.Length == 0)
            {
                Owed("★★★ 托盘右键「关闭」→ 管理端退出 → 栈真的没了",
                     $"子进程退出了(退出码 {child.ExitCode})但**一条结果都没写出来** —— "
                     + "等于没验。★ 这与「跑完全绿」在退出码上长得一模一样,所以判据看的是结果文件");
                return;
            }

            // ★ 逐条搬回本进程的账上 —— 子进程里的 PASS/FAIL 必须计进总数,
            //   否则「跑过了」这件事在哨兵里看不见。
            foreach (var line in lines)
            {
                if (line.StartsWith("PASS ", StringComparison.Ordinal)) Assert(true, line[5..]);
                else if (line.StartsWith("FAIL ", StringComparison.Ordinal)) Assert(false, line[5..]);
                else if (line.StartsWith("OWED ", StringComparison.Ordinal)) Owed(line[5..], "见子进程输出");
                else if (line.StartsWith("SKIP ", StringComparison.Ordinal)) Skip(line[5..], "见子进程输出");
                else Console.WriteLine("  (子进程说)" + line);
            }
            // ★ LIFE=1 的条件要**紧**:子进程真的做出了断言,**而且**没有欠着的。
            //   只看「有没有 PASS/FAIL 行」的话,一个在 `new App` 处炸掉、
            //   但前面两条元断言已经绿了的子进程也会把 LIFE 记成 1 —— 那是把没验到说成验到了。
            _lifeVerified = lines.Any(l => l.StartsWith("PASS ", StringComparison.Ordinal)
                                        || l.StartsWith("FAIL ", StringComparison.Ordinal))
                         && !lines.Any(l => l.StartsWith("OWED ", StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            Owed("★★★ 托盘右键「关闭」→ 管理端退出 → 栈真的没了",
                 "起/等子进程时出错:" + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { if (child is not null && !child.HasExited) child.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>
    /// 子进程里跑的那一段。★ 它是**真的**管理端:new App → InitializeComponent → Run,
    /// 托盘菜单里那一项是**点下去的**,不是"源码里写着有这么一项"。
    /// </summary>
    internal static int RunLifecycleChild()
    {
        var outFile = Environment.GetEnvironmentVariable(LifecycleOutEnvVar);
        var lines = new List<string>();
        void Ok(bool cond, string what)
        {
            lines.Add((cond ? "PASS " : "FAIL ") + what.Replace('\n', ' ').Replace('\r', ' '));
            Console.WriteLine("  " + (cond ? "PASS  " : "FAIL  ") + what);
        }
        void Cant(string what, string why)
        {
            lines.Add("OWED " + (what + " —— " + why).Replace('\n', ' ').Replace('\r', ' '));
            Console.WriteLine("  OWED  " + what + " —— " + why);
        }

        var tmp = Path.Combine(Path.GetTempPath(), "v23-lifechild-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        var decoys = new List<Process>();
        try
        {
            // ── 起三个替身:两个记进账本(该死)、一个不记(该活)────────
            var gw = StartDecoy(Path.Combine(tmp, "gw"), "localai-v23-gw-decoy", out var w1);
            var edge = StartDecoy(Path.Combine(tmp, "edge"), StackOwnership.EdgeProcName, out var w2);
            var bystander = StartDecoy(Path.Combine(tmp, "by"), "localai-v23-bystander", out var w3);
            // ★★ 第四个替身叫 `llama-server`,**不记账** —— 它让「有东西还在跑而我们认不出归属」
            //   这个处境**确定地**发生(没有起栈快照 ⇒ 后端一个都不认领 ⇒ 进 Unattributed),
            //   于是下面那条「关掉自己之前先说一声」才有确定的靶可打。
            //   ★ 不这么安排的话,那条判据的成败取决于**你这台机器上现在有没有 llama-server**
            //     —— 一条会随环境漂的断言(ASSERTION-PITFALLS 第 5 条)。
            var backend = StartDecoy(Path.Combine(tmp, "be"), StackOwnership.BackendProcName, out var w4);
            if (gw is null || edge is null || bystander is null || backend is null)
            {
                Cant("④c 托盘右键「关闭」",
                     "起不了替身进程(" + string.Join(" / ", new[] { w1, w2, w3, w4 }.Where(w => w.Length > 0)) + ")");
                return WriteLifecycleOut(outFile, lines, decoys, tmp);
            }
            decoys.AddRange(new[] { gw, edge, bystander, backend });
            StackOwnership.Clear();
            StackOwnership.NoteStarted(StackOwnership.Component.Gateway, gw);
            StackOwnership.NoteStarted(StackOwnership.Component.Edge, edge);
            Ok(StackOwnership.Owned() is ({ } og, { } oe) && og.Id == gw.Id && oe.Id == edge.Id,
                "4-0 元断言:账本里记着这一趟要停的那两个替身(记不上的话下面「栈没了」会因假理由而绿)");

            using var instance = InstanceLock.Acquire(AdminPaths.StateDir, AdminPaths.AppKey);
            Ok(instance.IsFirst,
                "4-1 元断言:临时状态目录里拿得到管理端那把锁(拿不到就不是干净起动)");

            var app = new App(instance, startHidden: true);
            app.InitializeComponent();
            // ★ 只替换「问人」与「告诉人」这两环(见 App.xaml.cs 那段测试缝的说明);
            //   被测的那条路 —— 请客户端退出、关栈、验、收托盘、Shutdown —— 一个字都没改。
            app.ConfirmClose = _ => true;
            var blocked = new List<string>();
            app.ReportCloseBlocked = t => blocked.Add(t);
            // ★★ 这一环**必须**也替掉:它是 V23 新加的、在「关成了但有东西没敢动」时弹的那个框。
            //   不替的话,凡是这台机器上恰好有个 llama-server 在跑,自检子进程就会**卡在模态框上**
            //   直到看门狗 —— 而红的理由会是「托盘那条路没通」,一句假话。
            var notices = new List<string>();
            app.ReportCloseNotice = t => notices.Add(t);

            var exited = false;
            app.Exit += (_, _) => exited = true;
            var watchdogFired = false;
            var watchdog = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromSeconds(LifecycleWatchdogSeconds),
                System.Windows.Threading.DispatcherPriority.Send,
                (_, _) => { watchdogFired = true; app.Shutdown(); }, app.Dispatcher);
            watchdog.Start();

            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // ④a 起得来
                    Ok(app.TrayIcon is { Visible: true },
                        "4a-1 起来之后托盘图标**真的建出来了且可见**(不是源码里写着 Visible = true)");
                    Ok(app.Panel is null,
                        "4a-2 带 --tray 起动【不弹窗、不抢焦点】—— 面板窗口此刻压根还没建");

                    // ④b 点 × = 缩托盘(裁定第 6 条)
                    app.ShowPanel();
                    var win = app.Panel;
                    Ok(win is { IsVisible: true }, "4b-1 打开面板那条路真的把窗口显示出来了");
                    win?.Close();
                    Ok(win is not null && !win.IsVisible, "4b-2 点 × 之后窗口真的隐藏了 —— 缩托盘");
                    Ok(ReferenceEquals(app.Panel, win),
                        "4b-3 点 × 只是隐藏,窗口对象没被销毁(销毁了下次打开会是另一个窗口)");
                    Ok(!exited, "4b-4 点 × 之后应用还活着 —— 关窗口不等于退应用(裁定第 6 条)");
                    Ok(app.TrayIcon is { Visible: true },
                        "4b-5 缩托盘之后托盘图标仍在(图标没了就等于把唯一入口也收走了)");
                    app.ShowPanel();
                    Ok(app.Panel is { IsVisible: true }, "4b-6 缩进托盘之后还能再打开 —— 缩托盘不等于消失");
                    app.Panel?.Close();

                    // ④c 托盘右键 →「关闭」
                    var menu = app.TrayIcon?.ContextMenuStrip;
                    Ok(menu is not null && menu.Items.Count > 0, "4c-1 托盘右键菜单真的挂上了");
                    var closeItem = menu?.Items[App.TrayCloseItemName];
                    Ok(closeItem is not null, "4c-2 菜单里有【关闭】那一项(按名字找,不按显示文案猜)");
                    Ok(closeItem?.Text == "关闭", "4c-3 那一项在界面上写的就是「关闭」");
                    if (closeItem is null) { app.Shutdown(); return; }

                    // ★★★ 正题:点它。
                    closeItem.PerformClick();
                }
                catch (Exception ex)
                {
                    Ok(false, "4x 生命周期段自身抛异常: " + ex.GetType().Name + ": " + ex.Message);
                    try { app.Shutdown(); } catch { }
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // ★ Run() 只有在真的 Shutdown 之后才返回 —— 这一行本身就是判据的一半。
            app.Run();
            watchdog.Stop();

            Ok(!watchdogFired,
                $"4c-4 托盘右键【关闭】在 {LifecycleWatchdogSeconds} 秒内真的把管理端关掉了"
                + "(靠看门狗兜底 = 那条路没通)");
            Ok(exited, "4c-5 Application 真的退出了(Run 返回 + Exit 事件都发生过)");
            Ok(app.TrayIcon is null || !app.TrayIcon.Visible,
                "4c-6 关掉之后托盘图标真的收走了 —— 留一个点不动的图标等于没关干净");
            Ok(blocked.Count == 0,
                "4c-7 这一趟没有走到「关不成」那条岔路(走到了说明探针把不存在的客户端看成在跑)");
            // ★★★ 「不动手」只是一半 —— 另一半是**说**。这两条钉的就是那另一半。
            //   ★ 靶是确定的:上面那个不记账的 `llama-server` 替身保证了 Unattributed 非空,
            //     而机器上**多几个真的 llama-server 也不影响** —— 它们汇总进同一条消息。
            Ok(notices.Count == 1,
                $"4c-14 ★★★ 关掉自己**之前**,把「还在跑、但我们认不出归属所以没动」那些**说了**"
                + $"(实得 {notices.Count} 条通知)。★ 不说的话:他点了「关闭」、管理端安静消失、"
                + "而他自己那个进程还在跑 —— 他会以为关栈根本没用,而真相是我们**有意**没动它");
            Ok(notices.Count > 0 && notices[0].Contains("认不出归属", StringComparison.Ordinal),
                "4c-15 ★★ 那句话要说清是【认不出归属】,不是含糊的「有东西没关」——"
                + "给个错的理由比不给更坏(D99 裁定④)");
            Ok(!Gone(backend),
                "4c-16 ★★★ 而那个没记账的 `llama-server` **还活着** —— "
                + "没有起栈快照时后端一个都不认领,这一条同时钉住了陈旧快照那个洞");

            // ★★★ 验收④ 的正题:**栈真的没了**,而且是从 LastStopReport 读出来的
            var rep = app.LastStopReport;
            Ok(rep is not null,
                "4c-8 关栈**真的跑过**(LastStopReport 不是 null)—— null = 那一步压根没走到");
            Ok(rep is { AllGone: true },
                "4c-9 关栈报告说【停干净了】,而它的判据是端口探不到人,不是「调过 Kill 了」");
            Ok(rep is not null && rep.Did.Count > 0,
                "4c-10 报告里**逐条说了动过谁** —— 一个空的 Did 意味着 StopAsync 什么都没做"
                + "(把它换成 `return new StopReport(true, [], [], [])` 时红的就是这一条)");
            Ok(WaitGone(gw, 8000),
                "4c-11 ★★★ 账本里那个**网关**替身,托盘那一下之后真的没了 —— 验收④的正题");
            Ok(WaitGone(edge, 8000),
                "4c-12 ★★★ 账本里那个 **Edge** 替身也真的没了");
            Ok(!Gone(bystander),
                "4c-13 ★★ 而**没记进账本**的那个旁观者还活着 —— 关栈只动我们起的那些,"
                + "边界不是靠一句注释守的");
        }
        catch (Exception ex)
        {
            // ★ 如实写「没验到」并说清为什么 —— 不编一条源码文本判据冒充它。
            //   门禁看 LIFE=0 会判红:没验到不许安静地混过那道闸。
            Cant("④ 生命周期三条(启动 / 缩托盘 / 托盘右键真关闭)在本进程里【没验到】",
                 ex.GetType().Name + ": " + ex.Message
                 + " (多半是这个环境没有可用的桌面 / window station:托盘图标与 WPF 窗口都要它)");
        }
        return WriteLifecycleOut(outFile, lines, decoys, tmp);
    }

    static int WriteLifecycleOut(string? outFile, List<string> lines, List<Process> decoys, string tmp)
    {
        foreach (var d in decoys) { try { if (!d.HasExited) d.Kill(entireProcessTree: true); } catch { } }
        try { Directory.Delete(tmp, true); } catch { }
        if (!string.IsNullOrWhiteSpace(outFile))
        {
            try { File.WriteAllLines(outFile, lines); }
            catch (Exception ex) { Console.WriteLine("  !!  写不出生命周期结果:" + ex.Message); }
        }
        // ★ 退出码只是**顺带**:父进程认的是结果文件(退出码分不出「全绿」与「根本没跑」)。
        return lines.Any(l => l.StartsWith("FAIL ", StringComparison.Ordinal)
                           || l.StartsWith("OWED ", StringComparison.Ordinal)) ? 1 : 0;
    }

    // ────────────────────────────────────────────────────────── 小工具
    /// <summary>
    /// 起一个名字由你定的**替身进程**:拿系统自带的 `ping.exe` 改个名字。
    /// ★ 被测代码认人靠的就是进程名与 PID,而替身给得出这两样;
    ///   它自己 ping 本机若干次后会自己退出,自检崩了也不会留下一个常驻进程。
    /// </summary>
    static Process? StartDecoy(string dir, string procName, out string why)
    {
        why = "";
        try
        {
            var ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");
            if (!File.Exists(ping)) { why = "找不到 " + ping; return null; }
            Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, procName + ".exe");
            File.Copy(ping, exe, true);
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("-n"); psi.ArgumentList.Add("240");
            psi.ArgumentList.Add("127.0.0.1");
            var p = Process.Start(psi);
            if (p is null) { why = "Process.Start 返回 null"; return null; }
            // ★ 把 stdout 抽干:不抽的话管道满了 ping 会阻塞在写上 —— 那仍然是活的,
            //   但"为什么活着"就变成了另一个原因,不如让它干干净净地跑。
            p.OutputDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
            return p;
        }
        catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; return null; }
    }

    /// <summary>
    /// 在指定端口上起一个**别人的** python 监听者 —— 复现「用户自己在 8080 上跑东西」。
    /// ★ 它 accept 完立刻 close:不这么写的话,关栈那一步的 /health 探活会一直等到超时,
    ///   这一段就会慢上几十秒,而慢的原因与被测代码无关。
    /// </summary>
    static Process? StartPythonListener(int port, out string why)
    {
        why = "";
        var py = HostProvision.LocateGateway().Python ?? WhichOnPath("python.exe");
        if (py is null)
        {
            why = "这台机器上找不到 python(网关的 venv 与 PATH 都没有)—— "
                + "★ 这一条【没测到】:它复现的是用户在网关口上跑自己东西那一路";
            return null;
        }
        var script = "import socket,os,threading\n"
                   + "threading.Timer(240, lambda: os._exit(0)).start()\n"
                   + "s=socket.socket()\n"
                   + "s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)\n"
                   + $"s.bind(('127.0.0.1',{port}))\n"
                   + "s.listen(8)\n"
                   + "while True:\n"
                   + "    c,_=s.accept()\n"
                   + "    c.close()\n";
        try
        {
            var psi = new ProcessStartInfo(py) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
            var p = Process.Start(psi);
            if (p is null) { why = "起不了 python"; return null; }
            // 等它真的把口占上 —— 占不上就不是这一条要测的场景了,如实说
            for (var i = 0; i < 50; i++)
            {
                if (p.HasExited) break;
                try
                {
                    using var probe = new TcpClient();
                    probe.Connect(IPAddress.Loopback, port);
                    return p;
                }
                catch { Thread.Sleep(100); }
            }
            why = $"python 起了但 5 秒内没在 127.0.0.1:{port} 上听起来"
                + (p.HasExited ? $"(它已经退了,退出码 {p.ExitCode} —— 多半是 Store 的那个 python 占位程序)" : "");
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; return null; }
    }

    static string? WhichOnPath(string exe)
    {
        try
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var p = Path.Combine(dir.Trim(), exe);
                if (File.Exists(p)) return p;
            }
        }
        catch { }
        return null;
    }

    /// <summary>随便要一个现在没人用的回环端口。</summary>
    static int FreeLoopbackPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>它没了没有。★ 问不出来当作**还在** —— 与「不动它」同一个方向。</summary>
    static bool Gone(Process p)
    {
        try { return p.HasExited; } catch { return false; }
    }

    /// <summary>等它真的没了。★ 杀完到进程真正消失之间有一小段,不等的话会验到一个正在退出的进程。</summary>
    static bool WaitGone(Process p, int ms)
    {
        try { return p.WaitForExit(ms); } catch { return Gone(p); }
    }

    static List<Process> SafeByName(string name)
    {
        try { return Process.GetProcessesByName(name).ToList(); }
        catch { return new List<Process>(); }
    }
}
