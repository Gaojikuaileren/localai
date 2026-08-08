// V14b -- 管理端自检。★ 第一轮欠的那一条:管理端**编得过 ≠ 跑得起来**,
// 而出包门禁此前对它一无所知(build-client.ps1 里零命中 admin)。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ 本文件的分工,以及为什么它不能只有结构断言
//
//  结构断言(读源码、查接线)能证明「这一行写在那儿」,**不能**证明「它真的会跑」。
//  ASSERTION-PITFALLS 第 12/13 条整整两节记的就是这件事:4197 条全绿,而真机上开不起来。
//  ⇒ 本文件里最要紧的一节是【live】那一节:它**真的起一个客户端进程**,
//    **真的**让管理端发一次「请你优雅退出」,然后去读客户端自己写的善后日志,
//    断言那八步**逐条跑过了**。那是裁定第 7 条唯一算数的证据。
//
//  ★ 证据来自 ShutdownCoordinator 自己写的 `%TEMP%\localai-shutdown.log`
//    —— 它**不依赖调用方传 log 回调**(那条路当初就忘了传、静默了几个月,见该文件注释),
//    每一步写一行 `  ok      <名字> (Nms)`。⇒ 这是一份现成的、进程外可读的实跑痕迹。
// ══════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using LocalAI.Admin.Services;
using LocalAI.Client.Services;

namespace LocalAI.Admin;

public static partial class Selftest
{
    static int _pass, _fail, _skip;

    /// <summary>环境变量:出包门禁用它指定「自检结果哨兵」的落点。★ 与客户端**同名同格式** ——
    /// build-client.ps1 的 Invoke-GateSelftest 是一份被两个 exe 共用的判据,格式分家它就认不得。</summary>
    public const string SentinelEnvVar = "LOCALAI_SELFTEST_SENTINEL";

    static void Assert(bool ok, string what)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + what); }
        else { _fail++; Console.WriteLine("  FAIL  " + what); }
    }

    static void Skip(string what, string why)
    {
        _skip++;
        Console.WriteLine("  SKIP  " + what + " —— " + why);
    }

    public static int Run()
    {
        _pass = _fail = _skip = 0;
        Console.WriteLine("=== 主机管理端 selftest ===");

        try
        {
            RunPure();
            RunWiring();
            RunMoved();     // ★ V21:跟着 3100 行一起搬过来的那批(admin/SelftestMoved.cs)
            RunLiveViews(); // ★ V21:那两页**真的点开用**(admin/SelftestLiveViews.cs)
            RunLive();
        }
        catch (Exception ex) { _fail++; Console.WriteLine("  FAIL  自检自身抛异常: " + ex); }

        Console.WriteLine($"\n主机管理端 selftest: PASS={_pass} FAIL={_fail}" + (_skip > 0 ? $"  SKIP={_skip}" : ""));
        if (_skip > 0)
            Console.WriteLine("  ★ SKIP 不是 PASS —— 上面每条都写了为什么跳过,不要把它读成通过。");
        WriteSentinel();
        return _fail > 0 ? 1 : 0;
    }

    // ────────────────────────────────────────────── 纯逻辑(不碰进程、不碰界面)
    static void RunPure()
    {
        Console.WriteLine("\n-- 纯逻辑 --");

        // 路径与应用键:定义只有一份(在客户端 AppPaths 里),管理端只是转名字。
        // ★ 两边各定义一份的那天,表现是**每次都起出第二个管理端**,而两边各自都"对"。
        Assert(AdminPaths.AppKey == AppPaths.AdminAppKey, "★ 管理端应用键就是 AppPaths 里那一个(定义只有一份)");
        Assert(AdminPaths.StateDir == AppPaths.AdminStateDir, "★ 管理端状态目录就是 AppPaths 里那一个");
        Assert(!string.Equals(AdminPaths.StateDir, AppPaths.StateDir, StringComparison.OrdinalIgnoreCase),
            "★★ 管理端与客户端的状态目录【不是同一个】—— 同一个的话两把单实例锁会互相把对方判成已在跑");

        // 关栈判据的三种处境要说三句**不同**的话(D99 裁定④:给个错的理由比不给更坏)
        var unknown = StackStop.ConfirmText(new StopVerdict(Known: false, Safe: false, Why: "W"));
        var unsafe_ = StackStop.ConfirmText(new StopVerdict(Known: true, Safe: false, Why: "W"));
        var safe = StackStop.ConfirmText(new StopVerdict(Known: true, Safe: true, Why: "W"));
        Assert(unknown != unsafe_ && unsafe_ != safe && unknown != safe,
            "★★ 关栈确认文案三种处境各不相同 —— 把【读不到】和【没人在用】合成一句就是给个错理由");
        Assert(unknown.Contains("读不到"), "★ 读不到时如实说读不到(不写成【没人在用】)");
        Assert(unsafe_.Contains("副机正在用"), "★ 副机在用时问「副机正在用,仍要关吗」");
        Assert(!safe.Contains("副机正在用"), "★ 安全时不吓唬人");

        // 今天必然读不到 —— 中枢那条路由还没开(DEBT,交接 V16)。
        // ★ 这条断言是【故意】钉住"今天读不到"的:哪天 V16 把路由开出来,它会红,
        //   而那正是提醒接手的人回来把这里接上,并把这条断言改成真正的往返测试。
        var v = StackStop.QueryAsync().GetAwaiter().GetResult();
        Assert(!v.Known,
            "★ 今天关栈判据读不到(safe_to_stop_stack 还没有 HTTP 路由,10-core/gateway 是别人的车道)"
            + " —— ★ 这条哪天变红,就是该回来把 " + StackStop.SafeToStopRoute + " 接上的信号");

        // 客户端 exe 的定位:纯路径逻辑,两个方向都走一遍
        Assert(ClientLink.ClientExePathNextTo(null) is null && ClientLink.ClientExePathNextTo("  ") is null,
            "★ 拿不到自己的路径时判【找不到客户端】,不抛");
        var tmp = Path.Combine(Path.GetTempPath(), "v14b-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var adminDir = Path.Combine(tmp, "admin");
            Directory.CreateDirectory(adminDir);
            Assert(ClientLink.ClientExePathNextTo(adminDir) is null, "★ 旁边没有 client 目录 ⇒ 判找不到");
            var clientDir = Path.Combine(tmp, "client");
            Directory.CreateDirectory(clientDir);
            Assert(ClientLink.ClientExePathNextTo(adminDir) is null, "★ 只有空目录、没有 exe ⇒ 仍然判找不到");
            var fake = Path.Combine(clientDir, "localai-client.exe");
            File.WriteAllText(fake, "x");
            Assert(string.Equals(ClientLink.ClientExePathNextTo(adminDir), Path.GetFullPath(fake),
                                 StringComparison.OrdinalIgnoreCase),
                "★ exe 在 ⇒ 找得到(判据是【装没装】,与它跑没跑无关)");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }

        // 单实例:排他按用户(锁文件),与客户端同一份实现
        var lockDir = Path.Combine(Path.GetTempPath(), "v14b-lock-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (var first = InstanceLock.Acquire(lockDir, "Probe"))
            {
                Assert(first.IsFirst, "第一个实例拿得到锁");
                Assert(InstanceLock.IsRunning(lockDir, "Probe"), "★ 持锁期间 IsRunning 为真");
                using var second = InstanceLock.Acquire(lockDir, "Probe");
                Assert(!second.IsFirst, "★★ 第二个实例拿不到锁");
            }
            Assert(!InstanceLock.IsRunning(lockDir, "Probe"), "★ 释放后 IsRunning 转假(DeleteOnClose,不留僵尸锁)");
        }
        finally { try { Directory.Delete(lockDir, true); } catch { } }
    }

    // ────────────────────────────────────────────── 接线(结构判据;读不到源码就跳过)
    static void RunWiring()
    {
        Console.WriteLine("\n-- 接线 --");
        var app = TryReadSource("App.xaml.cs");
        if (app is null)
        {
            // ★ 出厂产物旁边没有源码 —— 这是**设计如此**,不判红(ASSERTION-PITFALLS 第 11 条)。
            //   但要如实计一条 SKIP,而不是无声无息地少几条 PASS。
            Skip("接线结构断言", "旁边没有源码(出厂产物那一趟);这类判据只在仓库里跑得了");
            return;
        }

        var code = NoComments(app);

        // 裁定第 6 条:点 × 缩托盘,真正关闭只能托盘右键
        Assert(code.Contains("ev.Cancel = true"), "★★ 点 × 取消关闭(缩到托盘),不退出");
        Assert(code.Contains("RealCloseAsync"), "★ 托盘菜单里有真正关闭的入口");

        // 裁定第 7 条:关闭时请客户端优雅退出,而且**这里一个停进程 API 都不许有**
        var link = TryReadSource(Path.Combine("Services", "ClientLink.cs"));
        if (link is not null)
        {
            var lcode = NoComments(link);
            foreach (var banned in new[] { ".Kill(", "TerminateProcess", "taskkill" })
                Assert(!lcode.Contains(banned),
                    $"★★★ 管理端不许出现停进程 API({banned})—— 强杀会跳过八步里的 end-session,"
                    + "把租约挂满整个 TTL,副机会被判成【有人在用】而关不掉栈;"
                    + "而且 D106 钉住的那张八步表会从此守不到真正会跑的那条路");
            Assert(lcode.Contains("SignalQuit"), "★ 关客户端走的是【发信号】,由客户端自己跑它既有的退出路径");
        }

        // 裁定第 2 条:双击图标不启动客户端
        var prog = TryReadSource("Program.cs");
        if (prog is not null)
            Assert(!NoComments(prog).Contains("StartClient"),
                "★★ 启动路径里不许起客户端 —— 裁定第 2 条:双击管理端图标【不启动客户端】");

        // 裁定③:皮肤靠监听文件,不走契约
        Assert(code.Contains("FileSystemWatcher"), "★ 皮肤同步靠监听 Settings 文件");
        Assert(code.Contains("Renamed"),
            "★★ 既听 Changed 也听 Renamed —— 客户端存设置是写 .tmp 再原子替换,"
            + "只听 Changed 会漏掉真正落地的那一次");
    }

    // ────────────────────────────────────────────── live:真起一个客户端,真让它优雅退出
    static void RunLive()
    {
        Console.WriteLine("\n-- live(真起进程) --");

        var clientExe = LocateClientExe();
        if (clientExe is null)
        {
            Skip("★★★ 八步优雅退出的实跑验证",
                 "旁边找不到 localai-client.exe(出包时它应当在 ..\\client\\)。"
                 + "★ 这一条是裁定第 7 条唯一算数的证据,跳过 = 那条没被验过");
            return;
        }

        var tmp = Path.Combine(Path.GetTempPath(), "v14b-live-" + Guid.NewGuid().ToString("N")[..8]);
        var clientState = Path.Combine(tmp, "client");
        Directory.CreateDirectory(clientState);
        var shutdownLog = Path.Combine(Path.GetTempPath(), "localai-shutdown.log");
        try { File.Delete(shutdownLog); } catch { }

        // ★★ 本进程自己的 LOCALAI_CLIENT_STATE 也要指过去。
        //   第一版只给**子进程**设了它,而 ClientLink.IsClientRunning / RequestClientQuitAsync
        //   读的是 `AppPaths.StateDir` —— 那是**本进程**解出来的路径,仍然指着真实客户端目录。
        //   结果:客户端明明起来了(我另外用绝对路径探过,PASS),而 RequestClientQuitAsync
        //   却答"客户端本来就没在跑",一个信号都没发出去,善后日志自然是空的。
        //   ⇒ 自检当场抓到的一条真缺陷:**判据的两侧要看同一个目录**。
        var savedClientState = Environment.GetEnvironmentVariable(AppPaths.StateEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.StateEnvVar, clientState);

        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = clientExe,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(clientExe)!,
            };
            psi.ArgumentList.Add("--tray");
            // ★ 状态目录指到临时目录:自检**绝不碰真实档案**(与客户端自检同一条纪律)。
            psi.Environment[AppPaths.StateEnvVar] = clientState;
            p = Process.Start(psi);
            Assert(p is not null, "★ 起得动客户端进程");
            if (p is null) return;

            // 等它把单实例锁拿住(= 它真的启动完了,不是刚 fork 出来)
            var upDeadline = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < upDeadline && !InstanceLock.IsRunning(clientState, SingleInstance.AppKey))
                Thread.Sleep(250);
            var up = InstanceLock.IsRunning(clientState, SingleInstance.AppKey);
            Assert(up, "★★ 客户端起来了(锁文件在)—— 这一条同时证明【管理端未起时客户端仍可启动】");
            if (!up) return;

            // ★★★ 正题:管理端请它优雅退出,并**等它真的退了**
            var (stopped, why) = ClientLink.RequestClientQuitAsync(TimeSpan.FromSeconds(45))
                                           .GetAwaiter().GetResult();
            Assert(stopped, "★★★ 管理端发出【请你优雅退出】之后,客户端确实退了。说明:" + why);

            // ★★ 证据:客户端自己写的善后日志里,八步要**逐条**出现
            //   —— 这才是"走完八步"的证据。只看"进程没了"分不出优雅退出和崩溃。
            var trace = ReadIfExists(shutdownLog);
            Assert(trace is not null, "★★ 客户端留下了善后日志(没有日志 = 没有可查的证据,等于没验)");
            if (trace is not null)
            {
                foreach (var step in EightSteps)
                    Assert(trace.Contains(step), $"★★★ 八步之一跑过了:{step}");
                Assert(trace.Contains("shutdown cleanup done"), "★★ 善后跑到了最后一行(不是中途被掐断)");
            }
        }
        finally
        {
            // ★ 兜底强杀只是**自检的清理**,不是产品行为:live 段若中途红了,不能留一个进程在那儿。
            //   产品那条路(ClientLink)里一个停进程 API 都没有,并且有断言钉着。
            try { if (p is not null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            Environment.SetEnvironmentVariable(AppPaths.StateEnvVar, savedClientState);
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>
    /// D106 裁定②逐字钉住的那八步。★ 这里**照抄**是有意的:管理端这一侧独立持有一份期望值,
    /// 与客户端 App.xaml.cs 的注册顺序对拍。两边分家的那天,这一节会红。
    /// </summary>
    static readonly string[] EightSteps =
    {
        "stop-gpu-stream", "stop-lease-keeper", "stop-sync-stream", "save-client-stores",
        "end-session+release-vram", "save-settings", "stop-vram-monitor", "dispose-tray",
    };

    static string? LocateClientExe()
    {
        // ① 出包形态:dist\admin\ 旁边就是 dist\client\
        if (ClientLink.ClientExePath() is { } packed) return packed;
        // ② 仓库形态:从自己往上找 20-client-win,再进客户端的构建输出
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var cand = Path.Combine(dir, "app", "bin", "Release",
                                    "net9.0-windows10.0.19041.0", "win-x64", "localai-client.exe");
            if (File.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    static string? ReadIfExists(string p)
    {
        try { return File.Exists(p) ? File.ReadAllText(p) : null; } catch { return null; }
    }

    /// <summary>读源码(仓库里才有)。出厂产物旁边没有源码 -> null,调用方**跳过**而不是判红。</summary>
    static string? TryReadSource(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var p = Path.Combine(dir, relative);
            if (File.Exists(p)) { try { return File.ReadAllText(p); } catch { return null; } }
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    /// <summary>只去注释、**保留字符串** —— 查文案/接线用它。剥字符串的那种会让判据恒真(第 3c 条)。</summary>
    static string NoComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '/')
            { while (i < src.Length && src[i] != '\n') i++; sb.Append('\n'); continue; }
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '*')
            { i += 2; while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) i++; i++; continue; }
            sb.Append(src[i]);
        }
        return sb.ToString();
    }

    /// <summary>哨兵:格式与客户端**逐字一致**(门禁那条 `PASS=(\d+)\s+FAIL=(\d+)` 是共用的)。</summary>
    static void WriteSentinel()
    {
        var path = Environment.GetEnvironmentVariable(SentinelEnvVar);
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.WriteAllText(path, $"PASS={_pass} FAIL={_fail} SKIP={_skip}\n"); }
        catch (Exception ex)
        { Console.WriteLine($"  (哨兵写入失败,门禁会因此判红:{ex.GetType().Name}: {ex.Message})"); }
    }
}
