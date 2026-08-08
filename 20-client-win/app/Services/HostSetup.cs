// P3c / V21 -- 这台电脑的**角色判据**,以及它推出来的开机分流。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ V21(2026-08-08):本文件**不是**整块留下来的,而是拆剩的**判据那一半**。
//
//  搬走的是**动作**那一半(铸身份 · 防火墙 · 起网关与 Edge),现在住在
//  `admin/Services/HostProvision.cs`。留下的是**判据**:
//    `RoleVerdict` / `RoleEvidence` / `DecideRole` / `DetectRoleAsync` /
//    `ResolvesToThisMachine` / `DecideBoot` / `DetectBootAsync` /
//    `GatewayPort` / `GatewayUpAsync` / `LocalNics` / `IdentityExistsAsync`。
//
//  ★★ 为什么**必须**留(迁移地图 §1,整份地图里最硬的一条):
//    这是全客户端**唯一**能回答「我该拨回环网关还是拨 LAN Edge」的东西 ——
//      `App.xaml.cs → Hub.NoteRole → HubClient.IsHostMachine → BusinessRoute → DecideBusinessTarget`
//    整块搬走 ⇒ `IsHostMachine` 恒为 false(fail-closed)⇒ **主机上的客户端**走 LAN Edge、
//    拿 `lan-device` 档 ⇒ 组件面板点「确定」又吃「这台设备不能做这个操作」,
//    **而且一条断言都不会红**(V13 那批断言里的 `isHost` 是自检自己喂的 ——
//    ASSERTION-PITFALLS 第 13 条的原样复发)。
//
//  ★ 管理端用 `<Compile Link>` 编**同一份**本文件 —— 不是复制。
//    `HostProvision.EnsureGatewayAsync` 直接调下面的 `GatewayPort` / `GatewayUpAsync`,
//    于是「客户端拨到哪儿」与「管理端起在哪儿」结构上不可能漂。
//
//  ★★★ 客户端里**不再有任何起栈入口**:`EnsureStackAsync` 与
//    `BootDecision.MayStartStack` 一并删除,`BootRoute.HostStartStack` 从"我去起"
//    改成"**已请管理端去起**"(V10 §7:两个 exe 都想起 Edge ⇒ 只保留管理端这一个入口)。
//    这条由元断言「客户端源码里不得出现单侧权威动作」钉住,并已红测。
// ══════════════════════════════════════════════════════════════════════════════
//
// ★ 一条边界原样留着(它约束的是**这个文件里剩下的东西**):
//   `IdentityExistsAsync` 只**问**身份在不在(`localai-identity status`,只读),
//   **铸**那一步已经搬走了。问与铸分开,正是「两侧协议 ⇒ 留;单侧权威 ⇒ 搬」落在同一个类上的样子。

using System.Diagnostics;

namespace LocalAI.Client.Services;

public static class HostSetup
{
    // ---------------------------------------------------------------- 身份(只问,不铸)
    /// <summary>
    /// 本机已经有中枢身份了吗。★ 用来把"静默继续"和"要先问一句"分开 ——
    /// 铸身份不可回退,不能因为"旁边有个 host 目录"这条线索就替人做了。
    /// 判据用 `localai-identity status` 的退出码:0 = 有,非 0 = 没有。
    /// </summary>
    /// ★★ 必须是 async。写成同步的那一版用 `.GetAwaiter().GetResult()` 等一个 async 方法,
    ///   而它是从 UI 线程调的 —— 里面 await 的续体要回 UI 线程,而 UI 线程正卡在 GetResult 上,
    ///   **整个程序当场死锁**(2026-08-04 实机卡死,就是这一行)。
    ///   ⇒ sync-over-async 在 UI 线程上永远是错的。要么全程 await,要么先 Task.Run 把它挪出去。
    public static async Task<bool> IdentityExistsAsync()
    {
        var dir = AdminApp.HostToolsDir();
        if (dir is null) return false;
        var exe = Path.Combine(dir, "localai-identity.exe");
        if (!File.Exists(exe)) return false;
        try
        {
            var (code, _) = await ProcRun.RunCapturedAsync(exe, "status", dir);
            return code == 0;
        }
        catch { return false; }
    }

    // ★★ `EnsureIdentityAsync`(铸身份)· `EnsureFirewallAsync` / `FirewallRuleExistsAsync` /
    //   `FirewallScript`(防火墙)**已搬进 `admin/Services/HostProvision.cs`**(V21)。
    //   它们是单侧权威动作:副机上一条都跑不到,而铸身份还是**不可回退**的。
    //   ⇒ 客户端里一行都不留(注释掉 / `#if false` 都不行 —— 元断言钉着)。

    // ---------------------------------------------------------------- 本机网卡(副机选从哪个网络找)
    /// <summary>
    /// 本机启用中的非回环 IPv4 网卡:(别名, IP)。★ 防火墙脚本要的是**别名**(如 "以太网"),
    /// 而 Edge 绑的是 **IP** —— 两者必须来自同一张网卡,否则规则放行的是另一张卡。
    /// </summary>
    public static List<(string Alias, string Ip)> LocalNics()
    {
        var outp = new List<(string, string)>();
        System.Net.NetworkInformation.NetworkInterface[] nics;
        try { nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return outp; }
        foreach (var n in nics)
        {
            if (n.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            System.Net.NetworkInformation.UnicastIPAddressInformationCollection addrs;
            try { addrs = n.GetIPProperties().UnicastAddresses; }
            catch { continue; }
            foreach (var a in addrs)
            {
                if (a.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                var s = a.Address.ToString();
                if (s.StartsWith("169.254.", StringComparison.Ordinal)) continue;   // APIPA
                outp.Add((n.Name, s));
            }
        }
        return outp;
    }

    // ════════════════════════════════════════════════════════════════════
    //  V9 · D36 主机自识别 —— **不依赖配对信息**(D?)
    //
    //  ★★★ 为什么非要有这一条:今天判角色的唯一判据是
    //    `HubClient.ThisMachineIsHub()` —— 「配对的拨号地址指向本机」。
    //    **没配对过就没有拨号地址** ⇒ 全新安装时它答不出来,而"答不出来"被读成"不是主机"
    //    ⇒ 主机上的客户端永远等不到有人告诉它"你该起栈"。
    //    D36 原文给的判据本来就不依赖配对:
    //      「谁是主机不再需要判定,**它是一个安装事实**」
    //      「主机 = 持有 CA 私钥与成员表的那一台」
    //      「客户端唯一需要的配置是中枢在哪(一个主机名)。**该地址解析到本机回环 → 这台就是主机**」
    //
    //  ★★ fail-closed,而且方向是**有意**选的:
    //    判错的两个方向代价不对称 —— 副机误判成主机会去起一套**不该起的**栈
    //    (两台机器同时起 Edge 抢 8443、两个 Broker 各写各的账本);
    //    主机误判成副机只是退回今天的手动状态。⇒ **拿不准一律判"不是主机"**,并说清为什么拿不准。
    //
    //  ★ 判据是**纯函数**(下面 DecideRole),所有 IO 在外面做完再喂进来 ——
    //    否则"主机上判成主机 / 副机上判成不是主机"这两个方向没法在自检里各测一次,
    //    而只测一个方向等于没测。
    // ════════════════════════════════════════════════════════════════════

    /// <summary>角色判定的结果。★ `IsHost` 为真**必须**有 `Why` 说清凭什么;为假也要说清为什么拿不准。</summary>
    public sealed record RoleVerdict(bool IsHost, string Why, RoleEvidence Evidence);

    /// <summary>判定用到的三条互相独立的证据。★ 全部由调用方采集,判据本身不碰 IO。</summary>
    /// <param name="AdminAppPresent">客户端旁边**装没装管理端程序**(`..\admin\localai-admin.exe`,D36「安装事实」)。
    /// <para>★★ 裁定②(2026-08-07)把这条从「旁边有没有 `..\host\localai-lan-edge.exe`」改成了
    /// 「装没装管理端」。指向物换了,判据的**形状**没换 —— 仍然只问「**装没装**」,不问「**跑没跑**」:
    /// 后者会死锁,主机第一次启动时管理端当然还没跑(见 <see cref="AdminApp.AdminAppPath"/>)。</para>
    /// <para>★★★ 意义:**副机不起栈从此是结构性的** —— 副机上根本没有那个程序,
    /// 不是"判断出来不该起",是"没有东西可起"(D48「用够不着代替判断」推到底)。</para></param>
    /// <param name="IdentityExists">本机有没有铸好的中枢身份(D36「持有 CA 私钥的那一台」)。</param>
    /// <param name="ConfiguredHubHost">已知的中枢主机名/地址;不知道就传 null。**不要求配过对**。</param>
    /// <param name="ConfiguredHubResolvesLocal">上面那个地址解析到本机(回环或本机网卡 IP)了吗。
    /// null = 没得解析或解析失败 —— ★ 它与 false 不是一回事,必须分开。</param>
    public sealed record RoleEvidence(bool AdminAppPresent, bool IdentityExists,
                                      string? ConfiguredHubHost, bool? ConfiguredHubResolvesLocal);

    /// <summary>
    /// D36 主机自识别。**纯函数** —— 喂什么答什么,不碰 IO、不看配对档案。
    /// </summary>
    public static RoleVerdict DecideRole(RoleEvidence ev)
    {
        // ── ★★★ 先看**否定**证据:地址明确指向别人 ⇒ 无论旁边放着什么都不是主机。
        //   这一条排在最前面是承重的:一台副机上如果有人整包拷来了 host 目录
        //   (装错、或者从主机拷贝了整个文件夹),下面那条"安装事实"会说它是主机 ——
        //   而它其实配对到另一台。**两条证据打架时,一律取"不是主机"。**
        if (ev.ConfiguredHubResolvesLocal == false)
            return new RoleVerdict(false,
                $"中枢地址 `{ev.ConfiguredHubHost}` 解析到的不是本机 —— 这台是副机。"
                + (ev.AdminAppPresent
                   ? "★ 但这台上装着管理端:两条证据打架时一律判【不是主机】,"
                     + "否则两台机器会同时起 Edge 抢 8443、两个 Broker 各写各的账本。"
                     + "(★ 这正是**整包拷到另一台机器**的形状 —— 管理端也跟着拷过去了,"
                     + "只有'配对地址指向别人'这条否定证据能拦住它。)"
                   : ""),
                ev);

        // ── ★ 肯定证据 ①:D36 的主机名判据(地址解析到本机)。
        if (ev.ConfiguredHubResolvesLocal == true)
            return new RoleVerdict(true,
                $"中枢地址 `{ev.ConfiguredHubHost}` 解析到本机 —— 按 D36,这台就是主机。", ev);

        // ── ★ 肯定证据 ②:D36 的「安装事实」。地址无从解析时才轮到它。
        //   **两条都要**:光有工具目录不算(可能是拷过来的),必须真的**铸过身份**
        //   —— 那才是 D36 说的「持有 CA 私钥与成员表的那一台」。
        if (ev.AdminAppPresent && ev.IdentityExists)
            return new RoleVerdict(true,
                "本机铸过中枢身份、且**装着管理端** —— 按 D36「主机 = 持有 CA 私钥与成员表的那一台」,"
                + "这台就是主机(没有配置中枢地址,走的是安装事实这条)。", ev);

        // ── 其余一律判"不是主机",并**说清是哪一种拿不准**。
        if (ev.AdminAppPresent && !ev.IdentityExists)
            return new RoleVerdict(false,
                "这台装着管理端,但**还没有铸过中枢身份** —— 光装着程序不等于是主机"
                + "(可能是整包拷过来的)。铸完身份再判一次就会认出来。", ev);
        return new RoleVerdict(false,
            "没有中枢地址可解析,这台也没装管理端 —— **拿不准,按规矩判【不是主机】**。"
            + "副机误判成主机会去起一套不该起的栈,而主机误判成副机只是退回手动;两个方向代价不对称。",
            ev);
    }

    /// <summary>
    /// 采集证据并判一次。★ 所有 IO 都在这儿,判据本身在 <see cref="DecideRole"/>。
    /// </summary>
    /// <param name="knownHubHost">已知的中枢主机名/地址(可为 null)。★ 允许传配对档案里的地址,
    /// 但那只是**其中一个**来源 —— 判据本身不要求配过对。</param>
    public static async Task<RoleVerdict> DetectRoleAsync(string? knownHubHost)
    {
        // ★★ 裁定②:角色判据的指向物从「主机工具目录」换成「**管理端装没装**」。
        //   ★ 换的是**指向物**,不是形状 —— 仍然只问"装没装"(文件在不在),不问"跑没跑"。
        //   ★ `HostToolsDir()` 本身没动:起 Edge 那条路(:485 一带)问的确实是主机工具目录,
        //     那是另一件事。这里只换**角色证据**这一处。
        var adminApp = AdminApp.AdminAppPath();
        var toolsPresent = adminApp is not null;
        var identity = toolsPresent && await IdentityExistsAsync();
        bool? resolvesLocal = null;
        if (!string.IsNullOrWhiteSpace(knownHubHost))
        {
            // ★ 解析失败与"解析到别人"必须分开:前者是 null(拿不准),后者是 false(明确不是)。
            //   合成一个的话,一次 DNS 抖动会让主机在下次启动时被判成副机 —— 而那正是漂移。
            resolvesLocal = await Task.Run(() => ResolvesToThisMachine(knownHubHost!));
        }
        return DecideRole(new RoleEvidence(toolsPresent, identity, knownHubHost, resolvesLocal));
    }

    // ---------------------------------------------------------------- 管理端(裁定第 1、3 条)
    /// <summary>
    /// 裁定第 1 条:**主机客户端启动 ⇒ 起管理端并隐藏到托盘**(不弹窗、不抢焦点)。
    /// 裁定第 3 条:**副机客户端启动 ⇒ 不起管理端**。
    ///
    /// <para>★★ 第 3 条在这里是**双保险**:即使 <paramref name="isHost"/> 判错了,
    /// 副机上也**根本没有那个 exe**(裁定②把角色判据的指向物换成了"管理端装没装")——
    /// 也就是说,副机不起管理端是**结构性**的,不是靠这一行 if 挡住的。
    /// 一条靠判断挡住的路,判断写错就通了;一条结构上不存在的路,写错也通不了。</para>
    ///
    /// <para>★ 只在它**没在跑**时起:起第二个的那一刻单实例锁会让它自己安静退出,
    /// 而用户看到的是"点了没反应"。判据用锁文件(跨进程、跨会话、零特权)。</para>
    ///
    /// <para>★ 返回 (起了没有, 说明) —— **不起**有好几种原因,把它们分开说清,
    /// 而不是笼统地返回 false(那会让界面没法解释发生了什么)。</para>
    /// </summary>
    public static (bool Started, string Why) EnsureAdminAppRunning(bool isHost)
    {
        if (!isHost) return (false, "这台不是主机 —— 副机不起管理端(裁定第 3 条)。");
        var exe = AdminApp.AdminAppPath();
        if (exe is null) return (false, "这台没装管理端程序(`..\\admin\\localai-admin.exe` 不在)。");
        if (InstanceLock.IsRunning(AppPaths.AdminStateDir, AppPaths.AdminAppKey))
            return (false, "管理端已经在跑了。");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            psi.ArgumentList.Add("--tray");   // ★ 隐藏到托盘:不弹窗、不抢焦点
            Process.Start(psi);
            return (true, "已把管理端起到托盘。");
        }
        catch (Exception ex) { return (false, "管理端起不来:" + ex.Message); }
    }

    /// <summary>
    /// 裁定第 5 条用:设置里那个「打开管理端面板」按钮显不显示。
    ///
    /// <para>★★ 判据**只问「这台装没装管理端」**,不带任何角色判断(用户裁定 2026-08-08)。
    /// 这是通用客户端里**唯一被允许的例外** —— 而它之所以被允许,正是因为它问的是
    /// **安装事实**、不是运行期角色分叉:副机上根本没有那个 exe,判据自然为假,
    /// 不需要谁去「判断」它该不该看见(D48「用够不着代替判断」)。</para>
    ///
    /// <para>★ 判据是「装没装」而不是「跑没跑」:管理端没在跑时按钮**仍然要在**,
    /// 点它就是把管理端起起来。问「跑没跑」会死锁(理由见 <see cref="AdminApp.AdminAppPath"/>)。</para>
    ///
    /// <para>★★ 如实记下**取舍**:原来这里还合取一个 <c>isHost</c>。摘掉之后,
    /// 「整包拷到另一台机器」的那台副机上这颗按钮**会出现**(管理端跟着拷过去了)。
    /// 用户 2026-08-08 裁定摘掉 —— 纪律②优先:客户端里不留运行期角色分支。
    /// ⇒ 挡那条路的责任**全部**落到两处**没有**被削弱的地方:
    ///   ① <see cref="EnsureAdminAppRunning"/> 仍然带 isHost —— **开机自动拉起**那条路不许放开,
    ///      否则那台副机会自己起管理端、进而起 Edge 抢 8443、两个 Broker 各写各的账本;
    ///   ② <see cref="DecideRole"/> 的否定证据:中枢地址解析到别人 ⇒ 立刻判不是主机。
    /// ★ 这两处一旦被削弱,这颗按钮就成了那条路的入口 —— 改它们之前请先读这一段。</para>
    /// </summary>
    public static bool AdminPanelButtonVisible()
        => AdminApp.AdminAppPath() is not null;

    /// <summary>
    /// 打开管理端面板(裁定第 5 条那个按钮)。没在跑就起一个**带界面**的,
    /// 已经在跑就发唤醒信号让它把窗口显示出来。
    /// ★ 注意与第 1 条的区别:那条起的是 `--tray`(隐藏),这条起的是**正常显示**。
    /// </summary>
    public static (bool Ok, string Why) OpenAdminPanel()
    {
        var exe = AdminApp.AdminAppPath();
        if (exe is null) return (false, "这台没装管理端程序。");
        try
        {
            if (InstanceLock.IsRunning(AppPaths.AdminStateDir, AppPaths.AdminAppKey))
            {
                // 已经在跑(多半在托盘里)⇒ 叫醒它的窗口,而不是再起一个
                using var wake = InstanceLock.Acquire(AppPaths.AdminStateDir, AppPaths.AdminAppKey);
                wake.SignalExisting();
                return (true, "已让管理端把面板显示出来。");
            }
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            Process.Start(psi);      // ★ 不带 --tray:这一次要正常显示界面
            return (true, "已打开管理端面板。");
        }
        catch (Exception ex) { return (false, "打不开:" + ex.Message); }
    }

    /// <summary>
    /// 这个主机名/地址解析到本机吗。null = **解析不了**(不是"不是本机")。
    /// ★ 开成 public 是诚实的测试缝:回环那几个字面量能确定性地测,DNS 那部分不能。
    /// </summary>
    public static bool? ResolvesToThisMachine(string hostOrDial)
    {
        var raw = hostOrDial.Split('/').Last().Trim();
        // ★★ 只有"恰好一个冒号"才当 host:port 切 —— IPv6 里冒号是地址的一部分,
        //   无条件 `Split(':')[0]` 会把 `::1` 切成空串。**这条是自检当场抓出来的**:
        //   我先写了断言再写实现,`::1` 那一格立刻红。
        var host = raw.Count(c => c == ':') == 1 ? raw.Split(':')[0].Trim() : raw;
        if (host.StartsWith('[') && host.Contains(']'))                // [::1]:8443 形式
            host = host[1..host.IndexOf(']')];
        if (host.Length == 0) return null;
        if (host is "127.0.0.1" or "localhost" or "::1") return true;
        try
        {
            var mine = System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                          .Select(a => a.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var them = System.Net.Dns.GetHostAddresses(host);
            if (them.Length == 0) return null;              // 解析不出地址 ⇒ 拿不准
            foreach (var a in them)
            {
                if (System.Net.IPAddress.IsLoopback(a)) return true;
                if (mine.Contains(a.ToString())) return true;
            }
            return false;                                   // 解析出来了,而且都不是本机 ⇒ 明确不是
        }
        catch { return null; }                              // 解析炸了 ⇒ 拿不准,**不是**"不是本机"
    }

    // ════════════════════════════════════════════════════════════════════
    //  ★★★ V21:**起栈整段已搬进 `admin/Services/HostProvision.cs`**。
    //
    //  搬走的:`StackResult` · `EnsureStackAsync` · `EnsureGatewayAsync` · `EnsureEdgeAsync` ·
    //         `EdgeUpAsync` · `LocateGateway` · `LocateEdge` · `PickBindIp` · `EdgeLogPath` ·
    //         `ExitNote` · `WaitUntilAsync` · `_edgeProc`。
    //  ⇒ **客户端源码里不再有任何起栈入口**(V10 §7:两个 exe 都想起 Edge ⇒ 只留管理端一个)。
    //
    //  ★★ 留下的只有下面两条,而且它们**不是**起栈,是**拨号判据**:
    //    · `GatewayPort` —— 主机客户端业务调用的落点(`HubClient.DecideBusinessTarget`);
    //    · `GatewayUpAsync` —— 开机分流问「本机的栈起了没」的那一问(只读探测)。
    //  ★ 管理端 `<Compile Link>` 编同一份本文件,`HostProvision.EnsureGatewayAsync` 直接调这两条
    //    ⇒ 「客户端拨到哪儿」与「管理端起在哪儿」结构上不可能漂成两个数。
    // ════════════════════════════════════════════════════════════════════

    /// <summary>网关默认端口(与 `90-ops/start-stack.ps1`、README 一致)。</summary>
    public const int DefaultGatewayPort = 8080;

    /// <summary>
    /// 回环网关端口。★ 可被 `LOCALAI_GATEWAY_PORT` 覆盖(与管理端的 <c>HubAdmin.AdminPort</c> 同款)。
    ///
    /// <para>★★ 2026-08-08(D?)从 <c>const</c> 改成属性,理由不是"将来可能要换端口",
    /// 而是**这个数在本进程里有了第三个消费者**:起网关的那一行、探 <c>/health</c> 的那一行,
    /// 以及【主机客户端业务调用的拨号】(<c>HubClient.DecideBusinessTarget</c>)。
    /// 三处各写一个字面量的话,自检里换一个假网关只换得掉其中一个 —— 另外两处仍打真 8080,
    /// 于是那条断言测的是「这台机器现在有没有跑网关」,而不是「客户端拨到哪儿去了」。
    /// 一条**永远绿或永远红、与被测代码无关**的断言,比没有断言更坏。</para>
    /// <para>★★★ **如实说清它盖不住什么**(2026-08-08 对抗式复核):这**不是**全仓
    /// 「网关端口的唯一来源」。至少还有两处写死 8080,而且都在**别的进程/别的语言**里:
    /// `10-core/lan-edge/Program.cs` 的上游地址 <c>"http://127.0.0.1:8080"</c>,
    /// 与 `90-ops/start-stack.ps1`。它们**不跟这个环境变量走**。
    /// ⇒ 改这个变量只改得动"客户端这一侧的三处";真要换端口,那两处得一起改。
    /// 把它说成"唯一来源"会让下一个人以为改一处就够了。</para>
    /// </summary>
    public static int GatewayPort
    {
        get
        {
            var s = Environment.GetEnvironmentVariable("LOCALAI_GATEWAY_PORT");
            return int.TryParse(s, out var n) && n is > 0 and < 65536 ? n : DefaultGatewayPort;
        }
    }

    /// <summary>网关在不在(探 /health,**不看进程**)。</summary>
    public static async Task<bool> GatewayUpAsync(int timeoutMs = 1500)
    {
        try
        {
            // ★ UseProxy=false:与 `HubClient.LoopHttp` 逐字对齐。自检里有一条断言说
            //   「探 /health 与业务拨号读同一个数」,而如果这一个走系统代理、那一个不走,
            //   在配了代理的机器上「探到了网关」与「业务打得到网关」会**一真一假** ——
            //   那条断言就成了一句在开发机上恒真、在真实环境里说谎的话。
            using var c = new HttpClient(new SocketsHttpHandler { UseProxy = false })
                { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            var r = await c.GetAsync($"http://127.0.0.1:{GatewayPort}/health");
            return (int)r.StatusCode is >= 200 and < 300;
        }
        catch { return false; }
    }

    // ════════════════════════════════════════════════════════════════════
    //  V9 · 开机就分流(D?)—— 用户裁定 2026-08-07:
    //    「开启应用就该开始判断电脑角色以及配对,不应该只在我点进设置滑到下面去才开始;
    //      如果一开始不判断角色,也没办法让主机自动起栈。」
    //
    //  ★★★ 此前这条链断在**没人问**:`App.OnStartup` 直接起三条流去连中枢,
    //    连不上就显示 Offline **结束**。而 `HostSetup` 的唯一调用方是
    //    `Views/DevicesView.cs`(设置深处)—— 启动路径零调用。
    // ════════════════════════════════════════════════════════════════════

    /// <summary>开机三问的答案 + 该走哪条路。</summary>
    public enum BootRoute
    {
        /// <summary>主机,但还没铸身份 ⇒ 引导铸身份。</summary>
        HostNeedsIdentity,
        /// <summary>
        /// 主机,有身份,栈没起 ⇒ **已请管理端去起**。
        /// <para>★★★ V21 起,这条路**不再由客户端动手** —— 起栈的唯一入口是
        /// `admin/Services/HostProvision.EnsureStackAsync`(V10 §7:两个 exe 都想起 Edge
        /// ⇒ 只保留管理端这一个入口)。客户端这一侧只剩两件事:
        ///   ① `EnsureAdminAppRunning` 把管理端拉起来(裁定第 1 条);
        ///   ② 把这一行 Headline 显示出来,让人知道现在在等什么。
        /// ★ 枚举成员**留着**是有理由的:它是一个**诊断结论**,不是一个动作许可。
        ///   删掉它,界面就只能在"主机 · 就绪"和"副机"之间二选一,而"栈还没起"
        ///   这个真实处境会没地方说 —— 那正是本仓最恨的"失败与成功长得一样"。</para>
        /// </summary>
        HostStartStack,
        /// <summary>主机,一切就绪。</summary>
        HostReady,
        /// <summary>副机,没配过对 ⇒ 引导配对。</summary>
        ClientNeedsPairing,
        /// <summary>副机,配过对但连不上 ⇒ 归因(V1 那五种)。</summary>
        ClientHubUnreachable,
        /// <summary>副机,一切就绪。</summary>
        ClientReady,
    }

    /// <summary>
    /// 开机分流的结论。★ `Headline` 是界面上那一行,**每条路都有**,不许静默。
    ///
    /// <para>★★★ V21:`MayStartStack` **已删**。它当年是「起栈的许可位」,而客户端
    /// 现在**根本没有起栈这个动作**可许可 —— 留着一个没有被许可对象的许可位,
    /// 是在邀请下一个人把起栈再写回来。</para>
    ///
    /// <para>★★ 删它的**同一次改动**里必须处理一条寄生断言(迁移地图 §1.1 点名):
    /// 自检里那条「角色要喂在起栈之前」写的是
    /// <c>iRole &lt; (code.IndexOf("MayStartStack") is var i &amp;&amp; i >= 0 ? i : int.MaxValue)</c> ——
    /// 名字一删,`IndexOf` 返回 -1 ⇒ 取 `int.MaxValue` ⇒ 这条断言**永远为真、与被测代码再无关系**。
    /// 它不会红、不会有人发现,**而它守的正是那件事**。
    /// ⇒ 已改写成正向判据「客户端源码里不存在任何起栈入口」,并当场红测过
    /// (手工把 `EnsureStackAsync` 加回客户端 ⇒ 必须红)。</para>
    /// </summary>
    public sealed record BootDecision(BootRoute Route, RoleVerdict Role, string Headline);

    /// <summary>
    /// 开机三问 → 分流。**纯函数**,喂什么答什么。
    /// </summary>
    /// <param name="role">① 我是主机吗(<see cref="DecideRole"/> 的结果)。</param>
    /// <param name="isPaired">② 这台配过对吗。</param>
    /// <param name="hubReachable">③ 中枢在不在。</param>
    /// <param name="identityExists">主机分支要用:身份铸了没。</param>
    public static BootDecision DecideBoot(RoleVerdict role, bool isPaired,
                                          bool hubReachable, bool identityExists)
    {
        if (role.IsHost)
        {
            if (!identityExists)
                return new BootDecision(BootRoute.HostNeedsIdentity, role,
                    "这台是中枢主机,但**还没有铸身份** —— 先铸一次身份,副机才配得上来。");
            if (!hubReachable)
                return new BootDecision(BootRoute.HostStartStack, role,
                    "这台是中枢主机,身份已就位,**栈还没起** —— 已请管理端启动网关与 LAN Edge。"
                    + "★ 起栈归管理端(它是这台上唯一有那个入口的程序);进度看管理端面板。");
            return new BootDecision(BootRoute.HostReady, role, "");
        }
        // ★★★ 以下**全部**是副机分支。★ V21 起这句话对**两边**都成立了:
        //   整个客户端(不只是副机分支)一行起栈代码都没有 —— 元断言钉着,红测过。
        if (!isPaired)
            return new BootDecision(BootRoute.ClientNeedsPairing, role,
                "这台是副机,**还没配过对** —— 去「设备」里配对到中枢主机。" + WhyNotHost(role));
        if (!hubReachable)
            return new BootDecision(BootRoute.ClientHubUnreachable, role,
                "已配对,但现在**连不上中枢** —— 多半是主机没开机;具体归因见「设备」页。");
        return new BootDecision(BootRoute.ClientReady, role, "");
    }

    /// <summary>把"为什么判成不是主机"带到界面上 —— 拿不准的理由必须说得出来。</summary>
    static string WhyNotHost(RoleVerdict role) =>
        role.IsHost ? "" : "(角色判定:" + role.Why + ")";

    /// <summary>采集三问的答案并分流。★ 全程 async,**绝不** sync-over-async(会死锁,见本文件顶部)。</summary>
    public static async Task<BootDecision> DetectBootAsync(string? knownHubHost, bool isPaired)
    {
        var role = await DetectRoleAsync(knownHubHost);
        var identity = role.Evidence.IdentityExists;
        // ★ 「中枢在不在」对主机与副机是**两个不同的问题**:
        //   主机问的是"本机的栈起了没"(探回环 8080);
        //   副机问的是"够不够得着主机"(那条归因 V1 已经做完,在 HubClient 里)。
        //   这里只答主机那一面;副机那面交给现有的 ProbeAsync,不重复实现一套。
        var reachable = role.IsHost ? await GatewayUpAsync() : isPaired;
        return DecideBoot(role, isPaired, reachable, identity);
    }

    // ---------------------------------------------------------------- 工具
    // ★★ `RunCapturedAsync` 已搬进 `Services/ProcRun.cs`(V19 · 2026-08-08)。
    //   它原来是本类的**私有**方法,而管理端拆分会把本类切成两半:
    //   `IdentityExistsAsync` 留客户端(角色证据),`EnsureIdentityAsync` 搬管理端 ——
    //   **两半都用它**。留在这儿的话,切分那天最省事的写法就是复制一份过去,
    //   而两份漂了**不会有任何东西红**。⇒ 提成一个文件、两个 csproj 编同一份。
    //   理由与判据都写在 `ProcRun.cs` 顶部。
    //
    // ★ `ReadAndDelete` / `Trim` 跟着防火墙与铸身份一起搬进 `HostProvision.cs` ——
    //   它们**只**被那两段用,留在这儿会变成没人调的死代码。
}
