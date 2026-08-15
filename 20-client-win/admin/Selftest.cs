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
    static int _pass, _fail, _skip, _owed;
    static bool _lifeVerified;

    /// <summary>环境变量:出包门禁用它指定「自检结果哨兵」的落点。★ 与客户端**同名同格式** ——
    /// build-client.ps1 的 Invoke-GateSelftest 是一份被两个 exe 共用的判据,格式分家它就认不得。</summary>
    public const string SentinelEnvVar = "LOCALAI_SELFTEST_SENTINEL";

    static void Assert(bool ok, string what)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + what); }
        else { _fail++; Console.WriteLine("  FAIL  " + what); }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ★★★★ V23 · SKIP 分**两类**,而门禁只该对其中一类判红
    //
    //  V22 立了「把静默跳过换成看得见的 SKIP」这个做法,但没走完最后一步:
    //  `build-client.ps1` 的正则只认 `PASS=(\d+)\s+FAIL=(\d+)` ——
    //  ⇒ 那些「看得见的 SKIP」在**门禁里是隐形的**,谁也不会红,
    //    于是「登记成 SKIP」等于「登记给自己看」。
    //
    //  ★ 而反过来「SKIP 一律判红」会天天误报,误报会训练人绕过门禁(D82 已经因此失效过两条)。
    //    真正要分的是这一刀:
    //      · <see cref="Skip"/> —— **这个形态下本来就测不了**。发布产物旁边没有源码、
    //        机器上没装 python、8080 已经被真东西占着。恒常发生,判红=天天误报。
    //      · <see cref="Owed"/> —— **本该跑得了,却没跑成**。判据指错了文件、
    //        子进程挂死、生命周期那条一条结果都没写出来。它**不该发生**,发生了就该红。
    //  ⇒ 哨兵里两个数分开给(`SKIP=` / `OWED=`),门禁只对 `OWED>0` 与 `LIFE=0` 判红。
    //    这条口径的红测记在决议包里:临时把一条 Skip 改成 Owed ⇒ 门禁当场拒绝出包。
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>这个形态下**本来就测不了**。★ 不判红,但必须说得出自己没跑。</summary>
    static void Skip(string what, string why)
    {
        _skip++;
        Console.WriteLine("  SKIP  " + what + " —— " + why);
    }

    /// <summary>**本该跑得了却没跑成**。★ 计进 SKIP,同时计进 OWED —— 门禁看 OWED 判红。</summary>
    static void Owed(string what, string why)
    {
        _skip++; _owed++;
        Console.WriteLine("  OWED  " + what + " —— " + why);
    }

    public static int Run()
    {
        _pass = _fail = _skip = _owed = 0;
        _srcHit = _srcMiss = 0; _srcMissed.Clear(); _lifeVerified = false;
        Console.WriteLine("=== 主机管理端 selftest ===");

        try
        {
            RunPure();
            RunWiring();
            RunStackStop(); // ★ V23:关栈那一半的**行为**判据(admin/SelftestStackStop.cs)
            RunMoved();     // ★ V21:跟着 3100 行一起搬过来的那批(admin/SelftestMoved.cs)
            RunLiveViews(); // ★ V21:那两页**真的点开用**(admin/SelftestLiveViews.cs)
            RunWheel();     // ★ V29:滚轮真的全局可滚 + 四页普查(admin/SelftestWheel.cs)
            RunChrome();    // ★ V29b:自绘标题栏真的在、三个键真的管用(admin/SelftestChrome.cs)
            RunIcon();      // ★ V29:管理端图标真的在 exe 里、是红的、与客户端是镜像(admin/SelftestIcon.cs)
            RunOnDemand();  // ★ V29:「按需」默认全勾的两支各走一遍(admin/SelftestOnDemand.cs)
            RunLive();
            RunLifecycle(); // ★ V23:托盘右键「关闭」→ 栈真的没了(子进程,理由见那儿)
            RunAnchorTally();
        }
        catch (Exception ex) { _fail++; Console.WriteLine("  FAIL  自检自身抛异常: " + ex); }

        // ★★★ V36:`OWED=` / `LIFE=` **无条件打印**(哪怕是 0/1)。
        //   `run-tests.ps1` 读的是**控制台汇总行**,而"只在 >0 时才印"会让「字段是 0」
        //   与「这份 exe 根本没有这个字段」在它眼里长得一模一样 —— 那两件事的下一步完全相反
        //   (前者放行,后者该说"口径不明")。⇒ 字段恒在。
        Console.WriteLine($"\n主机管理端 selftest: PASS={_pass} FAIL={_fail}"
                          + (_skip > 0 ? $"  SKIP={_skip}" : "")
                          + $"  OWED={_owed}  LIFE={(_lifeVerified ? 1 : 0)}");
        if (_skip > 0)
            Console.WriteLine("  ★ SKIP 不是 PASS —— 上面每条都写了为什么跳过,不要把它读成通过。");
        if (_owed > 0)
            Console.WriteLine("  ★★ OWED = 【本该跑得了却没跑成】—— 出包门禁会因此判红(见 Owed 上面那段口径)。");
        Console.WriteLine(_lifeVerified
            ? "  OK  ④ 生命周期三条(启动 / 缩托盘 / 托盘右键真关闭 + 栈真的没了)是【真跑出来的】。"
            : "  !!  ④ 生命周期三条【没验到】—— 见上面那条 OWED。出包门禁会因此判红。");
        Console.WriteLine($"  口径:源码锚点命中 {_srcHit} 处 · 落空 {_srcMiss} 处"
                          + (AdminSourceRoot() is null ? "(源码根不在旁边 —— 出厂产物那一趟,设计如此)"
                                                       : "(源码根在旁边)"));
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

        // ══════════════════════════════════════════════════════════════════
        //  ★★★ V25:那条路由**开出来了**,这一格按它自己写的交代改掉。
        //
        //  上一版逐字写着「今天必然读不到 —— 中枢那条路由还没开(DEBT,交接 V16)」,
        //  并且说明「哪天路由开出来,它会红,那正是提醒接手的人把这里接上」。
        //  ★★ 实际不会红 —— 这一点必须记下来,它是这类"预约式红灯"的通病:
        //    自检环境里没有网关 ⇒ `QueryAsync` 走的是**拒连**那条路 ⇒ 仍然 `Known=false`
        //    ⇒ `!v.Known` 照样成立、照样绿,而它的**判词已经变成假的**
        //    (路由已经有了)。一条会说谎却不会红的断言,比没有断言更坏。
        //  ⇒ 改成一条**能为假**的:证明 `QueryAsync` 真的**发了一次请求**。
        // ══════════════════════════════════════════════════════════════════
        {
            // ★ 把网关端口临时指到一个**确定没人听**的口上,让"拒连"成为确定结果 ——
            //   不这样的话,开发机上恰好跑着网关时这条会翻面,成为一条看天吃饭的断言。
            var portSaved = Environment.GetEnvironmentVariable("LOCALAI_GATEWAY_PORT");
            try
            {
                Environment.SetEnvironmentVariable("LOCALAI_GATEWAY_PORT", "1");
                var v = StackStop.QueryAsync(timeoutMs: 1500).GetAwaiter().GetResult();
                Assert(!v.Known, "★ 问不到网关时判【读不到】—— fail-closed 的方向没变");
                Assert(v.Why.Contains("127.0.0.1:1" + StackStop.SafeToStopRoute),
                    "★★★ 而且理由里带着**它真的拨过的那个地址** —— 这一条证明 QueryAsync "
                    + "不再是 Task.FromResult(改回去当场红)。"
                    + "★ 只判 !v.Known 的写法测不出这个区别:那一版也是 !Known");
                Assert(v.Why.Contains("读不到"),
                    "★ 措辞仍旧带【读不到】—— ConfirmText 的三分支判据靠它");
            }
            finally { Environment.SetEnvironmentVariable("LOCALAI_GATEWAY_PORT", portSaved); }
            Assert(Environment.GetEnvironmentVariable("LOCALAI_GATEWAY_PORT") == portSaved,
                "★ 收尾:网关端口环境变量已还原(不还原会让后面每一条都拨到 1 口)");
        }

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
            foreach (var banned in BannedStopApis)
                Assert(!lcode.Contains(banned),
                    $"★★★ 关客户端那条路(ClientLink.cs)不许出现停进程 API({banned})—— "
                    + "强杀会跳过八步里的 end-session,把租约挂满整个 TTL,"
                    + "副机会被判成【有人在用】而关不掉栈;"
                    + "而且 D106 钉住的那张八步表会从此守不到真正会跑的那条路");
            Assert(lcode.Contains("SignalQuit"), "★ 关客户端走的是【发信号】,由客户端自己跑它既有的退出路径");
        }
        RunNoStrayKillApi();

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

    // ══════════════════════════════════════════════════════════════════════════
    //  ★★★★ V23 · 修一条 3b 违例 —— 而它守的正是本轮动的那块
    //
    //  上面那条断言的**判词**逐字是「★★★ **管理端**不许出现停进程 API(.Kill(…)」,
    //  而它的**判据**只读一条写死的路径:`Services/ClientLink.cs`。
    //  ⇒ ASSERTION-PITFALLS 第 3b 条点名禁止的写法(已踩 4 次),一般规则④:
    //    「判词里有范围词,判据里就不许有写死的文件名」。
    //  ★★ 而且它**已经在说谎**:`Services/StackStop.cs` 里今天真的有
    //    `p.Kill(entireProcessTree: true)`,而那条断言照常绿着。
    //
    //  ⇒ 修法选的是**把判据放大到整个 admin 目录 + 给一份具名例外**,而不是把判词收窄。
    //    理由:「管理端别处不许冒出停进程 API」**正是我们想要的性质** ——
    //    今天允许 Kill 的地方只该有一处(关栈执行体),多出第二处就该当场知道。
    //    把判词收窄到 ClientLink 会让这条性质从此没人守。
    //  ★ 例外表是**反向全表**:表里的文件必须**真的**含停进程 API,不含就说明它已经退役,
    //    那一行要删掉 —— 例外只许变短。
    // ══════════════════════════════════════════════════════════════════════════
    static readonly string[] BannedStopApis = { ".Kill(", "TerminateProcess", "taskkill" };

    /// <summary>允许出现停进程 API 的文件,以及**为什么**。★ 相对管理端源码根。</summary>
    static readonly Dictionary<string, string> KillApiExemptions = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"Services\StackStop.cs"] =
            "关栈的**执行体**(D115 / V22)。停我们起的那些靠的就是 Kill(entireProcessTree: true) —— "
          + "进程树本身就是那条边界。★ 它的安全性**不由**「不许出现 Kill」来守,"
          + "而由归属账本守(StackOwnership:句柄 + PID + 进程名 + 启动时刻),"
          + "并且由 SelftestStackStop.cs 里那三条**真起替身进程**的行为断言钉着。",
        ["Selftest.cs"] =
            "自检自己的清理:live 段真起了一个客户端进程,中途红了不能把它留在那儿。"
          + "★ 这是**自检的善后**,不是产品路径。",
        ["SelftestStackStop.cs"] =
            "同上 —— 关栈那一段起了几个替身进程,跑完要收干净。",
    };

    static void RunNoStrayKillApi()
    {
        var all = TryReadAllSources();
        if (all.Count == 0)
        {
            Skip("★★★ 「管理端别处不许出现停进程 API」(全目录)",
                 "读不到管理端源码根(出厂产物那一趟)—— 这类判据只在仓库里跑得了");
            return;
        }
        // ★ 元断言:真的扫到东西了。扫到 0 个文件时下面那条会**恒真**,
        //   而「一个都没扫到」与「全都干净」在结果上长得一模一样(第 10/13 条那个形状)。
        Assert(all.Count >= 10,
            $"★ 元断言:全目录扫描真的读到了源码(实得 {all.Count} 份 .cs)—— "
            + "读到 0 份的话下面那条是恒真的");

        // ★ **一条**断言盖住整张表,而不是每个文件每个令牌各一条:
        //   后者会打出几十行读起来像"违例报告"的 PASS,而那种输出会训练人不看它。
        //   违例的文件名写进失败讯息里 —— 红的时候才需要那份名单。
        var offenders = new List<string>();
        foreach (var (rel, text) in all)
        {
            if (KillApiExemptions.ContainsKey(rel)) continue;
            var code = NoComments(text);
            foreach (var banned in BannedStopApis)
                if (code.Contains(banned)) offenders.Add($"{rel} → {banned}");
        }
        Assert(offenders.Count == 0,
            $"★★★ 管理端里【只有具名例外那几处】可以停进程(扫了 {all.Count} 份 .cs,"
            + $"例外 {KillApiExemptions.Count} 份)"
            + (offenders.Count == 0 ? "" : ",而这些冒出来了:" + string.Join("、", offenders))
            + "。★ 关客户端必须走「发信号 + 它自己跑八步」(裁定第 7 条 / D106);"
            + "关栈必须先证明得了归属再动手(StackStop)。"
            + "★★ 真要在别处停进程,就把那个文件写进 KillApiExemptions 并写清理由 —— "
            + "让下一个人看得到这是**裁过的**,不是漏掉的");

        // ══════════════════════════════════════════════════════════════════
        //  ★★★ 自检闸只许**自检**去设。`LOCALAI_ADMIN_NO_AUTOSTACK=1` 会让
        //    `StackBoot.EnsureAsync` 拒绝起栈 —— 生产代码里一旦有人给它赋值,
        //    实机上的表现就是「管理端起来了、栈静悄悄没起」,而界面上一切正常。
        //  ★ 判据是全目录扫描 + 具名白名单(声明处 + 设它的那处自检),
        //    不是"我记得只有那两处"。
        // ══════════════════════════════════════════════════════════════════
        var gateSetters = all
            .Where(f => f.Text.Contains(StackBoot.NoAutoStackEnvVar, StringComparison.Ordinal)
                     || f.Text.Contains("NoAutoStackEnvVar", StringComparison.Ordinal))
            .Select(f => f.Rel)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert(gateSetters.SequenceEqual(new[] { "Selftest.cs", "SelftestStackStop.cs", @"Services\StackBoot.cs" },
                                         StringComparer.OrdinalIgnoreCase),
            "★★★ 自检闸(" + StackBoot.NoAutoStackEnvVar + ")只许出现在【声明它的那一处 + 自检】里,"
            + $"实得:{string.Join("、", gateSetters)}。"
            + "★ 它一旦被生产代码设上,实机的表现是【管理端起来了而栈没起】,"
            + "而界面上一个字都不会说 —— 那正是本项目反复栽的那个形状");

        // ★ 反向全表:例外只许变短。
        var byRel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rel0, text0) in all) byRel[rel0] = text0;
        foreach (var (rel, why) in KillApiExemptions)
        {
            if (!byRel.TryGetValue(rel, out var text))
            {
                Assert(false, $"★★ 例外表里的 `{rel}` **不在**管理端源码根底下了(搬走或删了)—— "
                              + "把那一行删掉。理由原文:" + why);
                continue;
            }
            var code = NoComments(text);
            Assert(BannedStopApis.Any(b => code.Contains(b)),
                $"★★ 例外表里的 `{rel}` 现在**一个停进程 API 都没有**了 —— "
                + "那条例外已经不成立,把它从 KillApiExemptions 里删掉。"
                + "★ 留着一条不再成立的例外,等于在那个文件上永久开一个口子");
        }
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

    // ══════════════════════════════════════════════════════════════════════════
    //  ★★★★ V23 · 锚点:从「往上找得到同名文件就采信」收紧成「只认管理端源码根」
    //
    //  原来这里是自己的一套向上找:从 `AppContext.BaseDirectory` 起逐级往上 8 层,
    //  **只要那一级下面有同名文件就采信**。它有两个后果,第二个更坏:
    //    ① 读不到 ⇒ 调用方一律 `if (src is not null)` ⇒ **整段静默跳过**(不计 PASS/FAIL/SKIP);
    //    ② ★★ **读到了、但读的是别人的文件**。这不是假想:客户端那份自检 2026-08-08
    //       在出包闸上真的撞过 —— 把 exe 拷到 `%TEMP%\...\client\` 再跑,而 `%TEMP%` 里
    //       躺着别的会话留下的陈旧 `Selftest.cs`,往上走三层就撞上了它,
    //       于是自检读着几个小时前的源码在下判断,**红得理由是假的**。
    //       客户端当时把这个病修了(`ClientSourceRoot()`),而**管理端这一份没跟着改** ——
    //       「一个修法漏改一处,缺陷就完整地留在那一处」,这就是那一处。
    //
    //  ★ 管理端这边的具体危险形状:本文件里有一批锚点指着 `Views/DevicesView.cs`、
    //    `Services/HostSetup.cs` 这类**客户端**的文件。它们今天恒为 null(往上翻不到 `app/`),
    //    但只要管理端 exe 的某个祖先目录里出现一个 `Views\DevicesView.cs`,
    //    那 58 条就会**静默地对着客户端那一半源码跑** —— 红绿都是假的,比 null 更坏。
    //  ⇒ 只认 `AdminSourceRoot()` 解出来的那一个根(要求 `Selftest.cs` 与
    //    `localai-admin.csproj` **同时**在同一级),一份孤零零的临时文件配不上这个条件。
    //  ★★ 两个锚点、不加第三个:锚点越多「找不到源码根」越容易发生,
    //    而找不到的后果是整段跳过 —— 那是 fail-open 的方向(第 11 条)。
    // ══════════════════════════════════════════════════════════════════════════
    static readonly string[] SourceRootAnchors = { "Selftest.cs", "localai-admin.csproj" };

    static int _srcHit, _srcMiss;

    /// <summary>这一趟**落空过的相对路径**(去重)。★ 用来把「哪几条静默不跑了」说出名字来。</summary>
    static readonly SortedSet<string> _srcMissed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 管理端源码根;找不到返回 null —— **发布产物那一趟就是 null,那不是错误**。
    /// ★ 本探测不计入命中/落空:它是判据的前提,不是被判的对象。
    /// </summary>
    static string? AdminSourceRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var allHere = true;
            foreach (var a in SourceRootAnchors)
                if (!File.Exists(Path.Combine(dir, a))) { allHere = false; break; }
            if (allHere) return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    /// <summary>读源码(仓库里才有)。出厂产物旁边没有源码 -> null,调用方**跳过**而不是判红。
    /// ★ 命中与落空都记账 —— 见上面那段。</summary>
    static string? TryReadSource(string relative)
    {
        var root = AdminSourceRoot();
        if (root is not null)
        {
            var p = Path.Combine(root, relative);
            if (File.Exists(p))
            {
                try { _srcHit++; return File.ReadAllText(p); }
                catch { }
            }
        }
        _srcMiss++;
        _srcMissed.Add(relative);
        return null;
    }

    /// <summary>
    /// 管理端源码根底下的**全部** .cs(不含 bin/obj)。
    /// ★ 判词里出现「全仓 / 每一个 / 凡是」时,判据必须由**它**遍历,
    ///   不许写死一条路径 —— ASSERTION-PITFALLS 第 3b 条一般规则④。
    /// </summary>
    static IReadOnlyList<(string Rel, string Text)> TryReadAllSources()
    {
        var root = AdminSourceRoot();
        if (root is null) return Array.Empty<(string, string)>();
        var outp = new List<(string, string)>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, f);
                if (rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                try { outp.Add((rel, File.ReadAllText(f))); } catch { }
            }
        }
        catch { }
        return outp;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ★★★ 「判据指错了文件」的**登记表** —— 这张表只许变短
    //
    //  下面这些相对路径在管理端源码根底下**根本不存在**(它们是客户端那一半的文件)。
    //  它们各自底下的断言因此一条都没跑过,而在 V22 之前**连一行 SKIP 都没有**。
    //  ⇒ 表里的:计一条**看得见的 SKIP**,并写清归谁处置;
    //    表外的:计 **OWED** —— 门禁判红。新出现一处指错,当天就会被拦下,
    //    而不是等到某次搬迁之后才有人发现。
    //  ★ 这正是第 3b 条许可的那种用法:手写名单只能当**期望值**(反向全表),
    //    不能当遍历源。遍历源是 `_srcMissed`,它是跑出来的。
    // ══════════════════════════════════════════════════════════════════════════
    static readonly Dictionary<string, string> KnownWrongAnchors = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"Views\DevicesView.cs"] =
            "V21 把这个文件**拆分**了(不是改名):`app/Views/DevicesView.cs` 今天仍在、491 行,"
          + "而管理端源码根底下没有它。⇒ 它底下那批断言读的是**客户端**的源码。"
          + "★ 重新指向后实测 PASS 166→210 / FAIL 8→33,那 25 条红要逐条裁"
          + "「该跟去客户端自检、还是本来就该撤」—— 协调层已裁:单开车道。",
        [@"Services\HubClient.cs"] =
            "`HubClient.cs` 在客户端,而且**没有**被 admin 的 csproj link 进来 ⇒ "
          + "它底下那两条测的是管理端不编译的代码。处置(移去客户端自检 / 撤掉)随上一条一起裁。",
        [@"Views\StorageView.cs"] =
            "存储那一页还没搬进管理端(V21 迁移的余量)。搬过来的那天这一条会自己消失。",
        [@"Views\ExtensionsView.cs"] =
            "扩展那一页还没搬进管理端(同上)。",
        ["MainWindow.xaml.cs"] =
            "管理端的窗口叫 `AdminWindow.cs`,没有 MainWindow —— 这是从客户端搬过来时带的旧锚点。",
    };

    /// <summary>
    /// 收尾:把「这一趟哪些锚点落空了」摊开。★ 出厂产物那一趟(源码根不在旁边)**整段不判** ——
    /// 那时全部落空是设计如此,判红就是天天误报。
    /// </summary>
    static void RunAnchorTally()
    {
        Console.WriteLine("\n-- 锚点对账 --");
        if (AdminSourceRoot() is null)
        {
            Skip("源码锚点对账", $"源码根不在旁边(出厂产物那一趟)—— {_srcMiss} 处全部落空是**设计如此**,"
                 + "这一趟没有「本该读得到却读不到」这回事");
            return;
        }

        Assert(_srcHit > 0,
            $"★★★ 源码根就在旁边、却【一次都没命中】—— 命中 {_srcHit} 次 · 落空 {_srcMiss} 次。"
            + "★ 那一格里落空数也说明不了什么:整片断言在静默不跑,而末尾那个 PASS 数照常涨");

        foreach (var missed in _srcMissed)
        {
            if (KnownWrongAnchors.TryGetValue(missed, out var why))
                Skip($"锚点 `{missed}` 底下那批断言", "判据指错了文件(**已登记**):" + why);
            else
                Owed($"锚点 `{missed}` 底下那批断言",
                     "源码根就在旁边,而这条路径**读不到** ⇒ 它底下的断言在静默不跑。"
                     + "★ 这是新出现的一处指错:要么把路径改对,要么写进 KnownWrongAnchors 并说清归谁");
        }

        // ★ 反向:表里有、而这一趟**没有**落空 ⇒ 那条登记已经过期。
        //   过期的登记是一句钉在原地的错话(第 16 条),而删掉它只要一行 —— 所以判红。
        foreach (var known in KnownWrongAnchors.Keys)
            Assert(_srcMissed.Contains(known),
                $"★★ 登记表里的 `{known}` 这一趟**没有落空** —— 它已经修好了,"
                + "把那一行从 KnownWrongAnchors 里删掉。★ 这张表只许变短:"
                + "留着一条不再成立的登记,就是把一句错话钉在原地");
    }

    /// <summary>
    /// 只去注释、**保留字符串** —— 查文案/接线用它。剥字符串的那种会让判据恒真(第 3c 条)。
    ///
    /// <para>★★★★★ V36 修:上一版**不认字符串字面量**,于是源码里一句
    /// <c>$"https://…"</c> 里的 <c>//</c> 被当成行注释起点 ⇒ 那一行的后半截
    /// **连同收尾的引号一起被吃掉**。两个后果:
    /// ① 真代码被当注释删了(本行之后的部分);
    /// ② ★★★ 引号奇偶从此错位 ⇒ 建立在本函数之上的 <see cref="SelftestMoved"/>.<c>CodeOnly</c>
    ///    (它先跑 NoComments 再剥字符串)从那一行往后**把代码当字符串剥掉** ⇒
    ///    <c>!CodeOnly(整份源码).Contains("X")</c> 这类**反向**断言**静默恒真**。</para>
    ///
    /// <para>★ 这是最坏的一种缺陷:一把**用来看清代码的尺**自己量错了,而错法是无声的。
    /// ⇒ 判据不是"以后小心",是 <c>SelftestMoved.SelfCheckCommentStrippers()</c> 那六条定标 ——
    /// 它们喂三份合成源码,两个方向都钉。V36 实测:修之前定标 ①④⑥ **红**,修之后全绿。</para>
    ///
    /// <para>★★ 顺带认了**字符字面量**:`'"'` 里那个双引号不是字符串起点。
    /// 本仓的自检文件里就有这种写法(SelftestMoved.cs 的 CodeOnly 自己),
    /// 而 <c>TryReadAllSources()</c> 会把它们一起读进来。</para>
    /// </summary>
    static string NoComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '/')
            { while (i < src.Length && src[i] != '\n') i++; sb.Append('\n'); continue; }
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '*')
            { i += 2; while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) i++; i++; continue; }
            // ★ 字符串字面量:**原样抄过去**(它里面的 `//` 不是注释)
            if (src[i] == '"')
            {
                bool verbatim = i > 0 && src[i - 1] == '@';
                sb.Append(src[i]); i++;
                while (i < src.Length)
                {
                    if (verbatim) { if (src[i] == '"') { if (i + 1 < src.Length && src[i + 1] == '"') { sb.Append(src[i]); i++; } else break; } }
                    else { if (src[i] == '\\') { sb.Append(src[i]); i++; } else if (src[i] == '"') break; }
                    if (i < src.Length) sb.Append(src[i]);
                    i++;
                }
                if (i < src.Length) sb.Append(src[i]);
                continue;
            }
            // ★ 字符字面量:`'"'` / `'\\'` / `'/'` 三种都会骗过上面几条
            if (src[i] == '\'')
            {
                sb.Append(src[i]); i++;
                while (i < src.Length && src[i] != '\'')
                {
                    if (src[i] == '\\') { sb.Append(src[i]); i++; if (i >= src.Length) break; }
                    sb.Append(src[i]); i++;
                }
                if (i < src.Length) sb.Append(src[i]);
                continue;
            }
            sb.Append(src[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 哨兵:**头一段**与客户端逐字一致(门禁那条 `PASS=(\d+)\s+FAIL=(\d+)` 是共用的),
    /// 追加的字段一律放在后面 —— 改格式时必须保住这一点。
    /// <para>★ V23 追加三个,每一个都是为了让「这个数在量什么」看得见:
    /// `SRCHIT`/`SRCMISS`(与客户端同名同义;在此之前管理端**没有**,门禁只能打印
    /// 「这份 exe 比本脚本旧,口径不明」)· `OWED`(本该跑而没跑,门禁判红)·
    /// `LIFE`(生命周期那条到底验没验到,门禁判红)。</para>
    /// </summary>
    static void WriteSentinel()
    {
        var path = Environment.GetEnvironmentVariable(SentinelEnvVar);
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            File.WriteAllText(path,
                $"PASS={_pass} FAIL={_fail} SRCHIT={_srcHit} SRCMISS={_srcMiss} "
                + $"SKIP={_skip} OWED={_owed} LIFE={(_lifeVerified ? 1 : 0)}\n");
        }
        catch (Exception ex)
        { Console.WriteLine($"  (哨兵写入失败,门禁会因此判红:{ex.GetType().Name}: {ex.Message})"); }
    }
}
