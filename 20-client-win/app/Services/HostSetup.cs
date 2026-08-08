// P3c -- 把主机上那几条命令收进客户端(用户定的目标:「用户不跑任何命令栏和设置,程序可以跑」)。
//
// ★ 为什么这不只是"图省事":
//   CA 私钥**绑定创建它时的完整性等级**,而客户端和中枢将来都要在**同一个**等级上用它。
//   由客户端一手包办"铸"和"用",两边天然同级 —— 人自己开终端跑则可能这次普通、下次提权,
//   铸出一个将来打不开、而且**不可回退**的身份(只能重置重铸,所有已配对设备全部重配)。
//   所以这一条是把一个不可回退的错误【从可能变成不可能】。
//
// ★★ 2026-08-03 更正:早先这里写的是"客户端恒为普通用户,所以必然正确"。**那是错的** ——
//   UAC 关闭的机器(EnableLUA=0)上桌面 shell 自己就是 High,根本不存在普通身份的进程。
//   要紧的从来不是"是不是普通用户",而是**铸的时候和用的时候是不是同一个等级**。
//
// ★★ 三条不可让步的边界:
//   ① **绝不**调 `重置并铸身份.cmd` —— 它开头就 `del /q {state}\identity\*`,那是破坏性的,
//      会让所有已配对设备失效。客户端只调 `localai-identity.exe init`,
//      而 init 本身是 fail-closed 的(已存在就拒绝覆盖并返回 1)。
//   ② 需要管理员的**只有防火墙**一步,而且**只把那一个脚本**提权;
//      identity / Edge / 网关**绝不**放进提权进程 —— 它们一旦继承 High,身份就毁了。
//   ③ **做完要验**:不能因为进程退出码是 0、或者用户点了 UAC,就宣布"成功"。
//      身份看目录、防火墙看规则在不在。今天一整天修的都是这一类谎。

using System.Diagnostics;

namespace LocalAI.Client.Services;

/// <summary>一步的结果。Ok=确实做成了(而且**验过**);Skipped=本来就是好的;Failed=没做成。</summary>
public sealed record SetupStep(string Name, SetupOutcome Outcome, string Detail);

public enum SetupOutcome { Ok, Skipped, Failed }

public static class HostSetup
{
    /// <summary>防火墙规则名。★ 必须与 `90-ops/lan/lan-firewall.ps1` 的 `$RuleName` 一致 ——
    /// 对不上的表现是"明明加好了却总说没加"。</summary>
    public const string FirewallRuleName = "LocalAI-LAN-Edge";

    public const int EdgePort = 8443;

    // ---------------------------------------------------------------- 身份
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
        var dir = HubAdmin.HostToolsDir();
        if (dir is null) return false;
        var exe = Path.Combine(dir, "localai-identity.exe");
        if (!File.Exists(exe)) return false;
        try
        {
            var (code, _) = await RunCapturedAsync(exe, "status", dir);
            return code == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// 确保本机有中枢身份。已经有就是 Skipped(**不是**错误,更不去覆盖)。
    /// ★ 调的是 `localai-identity.exe init`,不是 `重置并铸身份.cmd`(后者会先删掉身份)。
    /// </summary>
    public static async Task<SetupStep> EnsureIdentityAsync()
    {
        const string name = "中枢身份";
        // ★★ 这里【不再】预判"我是不是管理员"(同日实测推翻:UAC 关闭的机器上一切都是 High,
        //   身份也就是在 High 下铸的、在 High 下打得开)。拿它当门槛会把健康机器判成不能用。
        //   真正要防的是「在 A 等级铸、将来在 B 等级用」—— 那要把铸造时的等级记下来再比,
        //   属中枢侧的改动,已写进 integrity-guard-asks-wrong-question-2026-08-03.md。
        //   在那之前:直接跑 init,它自己 fail-closed(已存在就拒绝覆盖),失败原因原样带回来。

        var dir = HubAdmin.HostToolsDir();
        if (dir is null) return new SetupStep(name, SetupOutcome.Failed, "本机没有主机端程序目录,没法铸身份。");
        var exe = Path.Combine(dir, "localai-identity.exe");
        if (!File.Exists(exe)) return new SetupStep(name, SetupOutcome.Failed, "找不到 " + exe);

        var (code, output) = await RunCapturedAsync(exe, "init", dir);
        // ★ init 是 fail-closed 的:已存在就返回 1 并说 "already exists"。那是【好事】,不是失败。
        if (output.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return new SetupStep(name, SetupOutcome.Skipped, "本机已经有中枢身份了,没有覆盖它。");
        if (code == 0)
            return new SetupStep(name, SetupOutcome.Ok, Trim(output));
        return new SetupStep(name, SetupOutcome.Failed, $"localai-identity init 退出码 {code}:{Trim(output)}");
    }

    // ---------------------------------------------------------------- 防火墙(唯一需要提权的一步)
    /// <summary>
    /// 放行 8443。★ 这是**唯一**需要管理员的一步,所以【只把这一个脚本】提权,做完就退。
    /// ★ 用户点"否"是**正常路径**,不是错误 —— 如实说"没放行,副机会连不上",并给出手动办法。
    /// ★★ 做完【要验】:回来查规则在不在(查询不需要管理员)。
    ///   只凭"UAC 点过了 / 进程退出码是 0"就宣布成功,是在替用户假设一件没看过的事。
    /// </summary>
    public static async Task<SetupStep> EnsureFirewallAsync(string interfaceAlias, string repoScript, string edgeExe)
    {
        const string name = "防火墙放行 8443";
        if (await FirewallRuleExistsAsync())
            return new SetupStep(name, SetupOutcome.Skipped, $"规则「{FirewallRuleName}」已经在了。");

        if (!File.Exists(repoScript)) return new SetupStep(name, SetupOutcome.Failed, "找不到 " + repoScript);
        if (!File.Exists(edgeExe)) return new SetupStep(name, SetupOutcome.Failed, "找不到 " + edgeExe);

        var log = Path.Combine(Path.GetTempPath(), "localai-firewall-" + Guid.NewGuid().ToString("N") + ".log");
        // -Command 而不是 -File:要把输出重定向到文件才拿得到失败原因(提权进程的 stdout 接不到)
        var cmd = $"& '{repoScript.Replace("'", "''")}' -InterfaceAlias '{interfaceAlias.Replace("'", "''")}' "
                + $"-Program '{edgeExe.Replace("'", "''")}' *> '{log.Replace("'", "''")}'; exit $LASTEXITCODE";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{cmd.Replace("\"", "\\\"")}\"",
                UseShellExecute = true,
                Verb = "runas",           // ★ 只有这一个进程提权
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p is null) return new SetupStep(name, SetupOutcome.Failed, "没能启动提权进程。");
            await p.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 用户在 UAC 上点了"否" —— 这是正常路径
            return new SetupStep(name, SetupOutcome.Failed,
                "你拒绝了管理员授权,所以 8443 没有放行 —— 副机会连不上这台。"
                + "想手动做:用管理员 PowerShell 跑 90-ops\\lan\\lan-firewall.ps1。");
        }

        var why = ReadAndDelete(log);
        // ★★ 验:规则真的在了才算成功
        if (await FirewallRuleExistsAsync())
            return new SetupStep(name, SetupOutcome.Ok, Trim(why));
        return new SetupStep(name, SetupOutcome.Failed,
            (why.Length > 0 ? Trim(why) : "脚本没有留下输出")
            + " —— 规则「" + FirewallRuleName + "」仍然不在。"
            + "常见原因:这张网卡被 Windows 归类成【公用网络】,脚本会拒绝(去设置里把它改成专用网络)。");
    }

    /// <summary>查规则在不在。★ 查询不需要管理员 —— 所以"验"这一步永远做得到,没有借口不做。</summary>
    public static async Task<bool> FirewallRuleExistsAsync()
    {
        var (code, output) = await RunCapturedAsync("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"if (Get-NetFirewallRule -DisplayName '"
            + FirewallRuleName + "' -ErrorAction SilentlyContinue) { 'YES' } else { 'NO' }\"", null);
        return code == 0 && output.Contains("YES", StringComparison.Ordinal);
    }

    /// <summary>
    /// 找防火墙脚本。★ 两处都找,而且**找不到就返回 null**,绝不拼一个路径出来让 Process.Start 去炸:
    ///   ① 主机端目录里(出包时应当把它一并放进去 —— 那才是不依赖仓库布局的正路);
    ///   ② 退一步:仓库布局 `<repo>/90-ops/lan/`,相对客户端 exe 是 `..\..\90-ops\lan\`。
    /// ★ ② 只在开发机/主机上成立;装到别处的副机上找不到是**正常**的 —— 副机本来也不需要它。
    /// </summary>
    public static string? FirewallScript()
    {
        var dir = HubAdmin.HostToolsDir();
        if (dir is not null)
        {
            var inPack = Path.Combine(dir, "lan-firewall.ps1");
            if (File.Exists(inPack)) return inPack;
        }
        try
        {
            var exe = Environment.ProcessPath;
            var d = exe is null ? null : Path.GetDirectoryName(exe);
            if (d is not null)
            {
                var repo = Path.GetFullPath(Path.Combine(d, "..", "..", "90-ops", "lan", "lan-firewall.ps1"));
                if (File.Exists(repo)) return repo;
            }
        }
        catch { /* 拼不出路径就是没有,不是错误 */ }
        return null;
    }

    // ---------------------------------------------------------------- 本机网卡(防火墙脚本要 alias)
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
    /// 后者会死锁,主机第一次启动时管理端当然还没跑(见 <see cref="HubAdmin.AdminAppPath"/>)。</para>
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
        var adminApp = HubAdmin.AdminAppPath();
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
        var exe = HubAdmin.AdminAppPath();
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
    /// 裁定第 5 条用:主机客户端的设置里要有「打开管理端面板」按钮,**副机没有**。
    /// ★ 判据是「**装没装**」而不是「跑没跑」—— 与角色判据同一条形状:
    ///   管理端没在跑时这个按钮**仍然要在**,点它就是把管理端起起来。
    /// </summary>
    public static bool AdminPanelButtonVisible(bool isHost)
        => isHost && HubAdmin.AdminAppPath() is not null;

    /// <summary>
    /// 打开管理端面板(裁定第 5 条那个按钮)。没在跑就起一个**带界面**的,
    /// 已经在跑就发唤醒信号让它把窗口显示出来。
    /// ★ 注意与第 1 条的区别:那条起的是 `--tray`(隐藏),这条起的是**正常显示**。
    /// </summary>
    public static (bool Ok, string Why) OpenAdminPanel()
    {
        var exe = HubAdmin.AdminAppPath();
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
    //  V9 · 主机自动起栈(D?)
    //
    //  ★★★ 与 D87① 的关系,别读错(决议包里有完整版):
    //    D87① 裁的是「**不做开机预热**」—— 那说的是**模型**。
    //    **网关不是被预热的东西,它是决定要不要预热的那个东西本身**(D83:Broker 就住在网关里)。
    //    把两者归成一类会得出"什么都不许自动起"这个错误结论,而那正是今天这个缺口的由来。
    //    ⇒ 这里起 gateway 与 lan-edge,**绝不起 llama-server**:
    //      按需装载(S14/S16-b)已经落地,静态起会和 transient 平面打架。
    //
    //  ★★ 不提权(HostSetup 第②条边界)。客户端自己是普通启动的,由它拉起 gateway/Edge
    //    **天然同级**。防火墙那步已单独处理(EnsureFirewallAsync),起栈不需要管理员。
    //
    //  ★★★ 第③条边界在这儿尤其要紧:**不能因为进程退出码是 0 就宣布成功**。
    //    uvicorn 起不来时进程可能活着但端口没开;Edge 绑不上网卡时也一样。
    //    ⇒ 起完**去探**:网关探 `/health`,Edge 探 8443 能不能连上。探不到就如实说是哪一步。
    // ════════════════════════════════════════════════════════════════════

    /// <summary>一套栈的启动结果。★ 逐个组件给结论 —— 「起了一半」是最坏的中间态,必须看得见。</summary>
    public sealed record StackResult(SetupStep Gateway, SetupStep Edge)
    {
        /// <summary>两个都活着才算成。</summary>
        public bool AllUp => Gateway.Outcome != SetupOutcome.Failed
                             && Edge.Outcome != SetupOutcome.Failed;

        /// <summary>★ 半套状态:一个起来了、另一个没有。界面必须把这句显示出来。</summary>
        public bool HalfUp => !AllUp
                              && (Gateway.Outcome != SetupOutcome.Failed
                                  || Edge.Outcome != SetupOutcome.Failed);
    }

    /// <summary>网关默认端口(与 `90-ops/start-stack.ps1`、README 一致)。</summary>
    public const int DefaultGatewayPort = 8080;

    /// <summary>
    /// 回环网关端口。★ 可被 `LOCALAI_GATEWAY_PORT` 覆盖(与 <c>HubAdmin.AdminPort</c> 同款)。
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

    /// <summary>
    /// Edge 在不在。★ 探的是**回环管理面**(127.0.0.1:8442),不是 8443。
    ///
    /// <para>★★★ 2026-08-07(D?)改的判据,理由是原来那一条**问错了问题**:
    /// 它探 `127.0.0.1:8443`,而那个口在 `run` 模式下确实开着 ⇒ 自动起栈报「已起并探到 8443」,
    /// 界面上一路绿灯 —— 可本机客户端接下来要用的两样东西**一样都没有**:
    ///   ① 管理面(8442)没绑 ⇒ `ProbeRoleAsync` 判 `HostHubDown`,这台电脑在自己的界面上
    ///      被说成「不是主机」,只渲染 `HubDownCard`;
    ///   ② 业务口只在回环上 ⇒ `DiscoverEdgeDialsAsync` 逐张网卡去找 8443(它**跳过回环**),
    ///      一个都找不到。
    /// 于是"起栈成功"这句话与客户端能不能用**完全无关** —— 典型的「失败与成功长得一样」。</para>
    ///
    /// <para>★ 换成管理面之后这句话才承重:管理面在**两种**模式下都只绑回环
    /// (`Program.cs` 里 `k.Listen(IPAddress.Loopback, cfg.AdminPort)` 是写死的),
    /// 它答话 = 角色能判出来 = 自配对够得着。这正是我们需要它回答的那个问题。</para>
    ///
    /// <para>★ 仍然只做 TCP 连接、不做 HTTP:这一层要回答的是「进程起来了没」,
    /// 「是不是**我们这个**中枢」由 <c>HubAdmin.ProbeAsync</c> 比 hubId 去答,不在这里下结论。</para>
    /// </summary>
    public static async Task<bool> EdgeUpAsync(int timeoutMs = 1500)
    {
        try
        {
            using var t = new System.Net.Sockets.TcpClient();
            var connect = t.ConnectAsync(System.Net.IPAddress.Loopback, HubAdmin.AdminPort);
            var done = await Task.WhenAny(connect, Task.Delay(timeoutMs));
            return done == connect && t.Connected;
        }
        catch { return false; }
    }

    /// <summary>
    /// 网关的启动器在哪。返回 (python, 工作目录);找不到则 (null, 说明为什么找不到)。
    /// <para>★★★ 这一条**必然会在发布安装上找不到**,而那是本车道最要紧的一条发现:
    /// `dist/host/` 里只有 `localai-lan-edge.exe` 与 `localai-identity.exe`,**没有网关** ——
    /// 网关是 `10-core/gateway` 的 Python 源码 + `<AI 根>\venvs\gateway` 那个虚拟环境,
    /// 两者都只存在于**仓库**里。⇒ 「意图即起」在引导层断掉的根因不是没人写代码,
    /// 是**网关根本没有随包发布**。见决议包。</para>
    /// </summary>
    public static (string? Python, string DirOrWhy) LocateGateway()
    {
        try
        {
            var here = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrWhiteSpace(here)) return (null, "拿不到客户端自己的路径");
            // 仓库形态:dist\client\ → ..\..\10-core\gateway
            var gwDir = Path.GetFullPath(Path.Combine(here, "..", "..", "10-core", "gateway"));
            if (!File.Exists(Path.Combine(gwDir, "gateway.py")))
                return (null, $"找不到网关源码({gwDir})—— ★ 网关**没有随包发布**,"
                            + "它只存在于仓库里(dist\\host 里只有 lan-edge 与 identity)");
            // 解释器:从 paths.toml 的 models 根推出 <AI 根>\venvs\gateway
            //  ★ 这一步**沿用了 start-stack.ps1 的推导**(models 的父目录 = AI 根),
            //    而 D91 明确说过那是**一次猜测**。这里照抄是为了不引入第二套口径;
            //    真要修该在 paths.toml 里给网关也登记一个 venv 键 —— 已写进决议包。
            var toml = Path.GetFullPath(Path.Combine(here, "..", "..", "config", "paths.toml"));
            if (!File.Exists(toml)) return (null, $"找不到 config\\paths.toml({toml})");
            var m = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(toml), @"^\s*models\s*=\s*'([^']+)'",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            if (!m.Success) return (null, "paths.toml 里没有 models 键,推不出 AI 根");
            var aiRoot = Path.GetDirectoryName(m.Groups[1].Value.TrimEnd('\\'));
            if (string.IsNullOrWhiteSpace(aiRoot)) return (null, "从 models 推不出 AI 根");
            var py = Path.Combine(aiRoot, "venvs", "gateway", "Scripts", "python.exe");
            if (!File.Exists(py)) return (null, $"找不到网关的虚拟环境({py})");
            return (py, gwDir);
        }
        catch (Exception ex) { return (null, ex.GetType().Name + ": " + ex.Message); }
    }

    /// <summary>Edge 的 exe 在哪(就在 `..\host` 里);找不到则返回 null。</summary>
    public static string? LocateEdge()
    {
        var dir = HubAdmin.HostToolsDir();
        if (dir is null) return null;
        var exe = Path.Combine(dir, "localai-lan-edge.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>
    /// 主机上把栈拉起来。★ **只在判定为主机时调** —— 副机一行都不该走到这儿。
    /// <para>幂等:已经在跑的直接 Skipped。可重试:失败不留半套**状态**
    /// (起不来的那个不会被记成起来了),而"起了一半"会由 <see cref="StackResult.HalfUp"/> 明说。</para>
    /// </summary>
    public static async Task<StackResult> EnsureStackAsync()
    {
        var gw = await EnsureGatewayAsync();
        var edge = await EnsureEdgeAsync();
        return new StackResult(gw, edge);
    }

    static async Task<SetupStep> EnsureGatewayAsync()
    {
        const string name = "统一入口网关 :8080";
        if (await GatewayUpAsync()) return new SetupStep(name, SetupOutcome.Skipped, "本来就在跑");
        var (py, dirOrWhy) = LocateGateway();
        if (py is null)
            return new SetupStep(name, SetupOutcome.Failed, dirOrWhy);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = py,
                // ★ 与 start-stack.ps1 逐字一致 —— 两处不一样的话,"手动起得来、自动起不来"
                //   会变成一个查不出根因的问题。
                Arguments = "-m uvicorn gateway:app --host 127.0.0.1 --port " + GatewayPort,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = dirOrWhy,
            };
            using var p = Process.Start(psi);
            if (p is null) return new SetupStep(name, SetupOutcome.Failed, "进程没起来");
        }
        catch (Exception ex)
        {
            return new SetupStep(name, SetupOutcome.Failed, ex.GetType().Name + ": " + ex.Message);
        }
        // ★★★ 边界③:**不看退出码,去探**。uvicorn 起不来时进程可能还活着而端口没开。
        if (await WaitUntilAsync(() => GatewayUpAsync(), 20_000))
            return new SetupStep(name, SetupOutcome.Ok, "已起并探到 /health");
        return new SetupStep(name, SetupOutcome.Failed,
            "进程起了,但 20 秒内探不到 http://127.0.0.1:8080/health —— "
            + "**不当作成功**(端口被占?venv 缺依赖?)");
    }

    // ════════════════════════════════════════════════════════════════════
    //  ★★★ 绑哪个地址(D?)。自动起栈从 `run` 改成 `run-lan <ip>` 之后才需要这一步。
    //
    //  ★ 为什么必须选一个**网卡**地址、而不能继续用 `run` 的回环:见 EnsureEdgeAsync 上方。
    //  ★ 为什么这不算"替用户猜":这里问的是**操作系统自己**会用哪个源地址出网 ——
    //    UDP 的 Connect **不发任何数据包**,它只让内核按路由表做一次源地址选择,
    //    读 LocalEndPoint 就是内核给的答案。那是一次查询,不是一次猜测。
    //  ★ 查不出来时【不硬选】:只有在候选**唯一**时才继续,否则如实报「有几个、分别是什么」
    //    并让这一步失败。多网卡上随手挑一个会把一个错地址写进配对档案,
    //    而那比"没起来"难查得多(V1 那批归因就是这么来的)。
    // ════════════════════════════════════════════════════════════════════
    /// <summary>选一个用于对外监听的本机 IPv4。选不出来返回 null,<paramref name="why"/> 说清为什么。</summary>
    public static string? PickBindIp(out string why)
    {
        var locals = HubAdmin.LocalIPv4List();
        if (locals.Count == 0)
        {
            why = "本机没有可用的 IPv4 网卡地址(网卡都没启用,或只剩 169.254.* 自封地址)—— "
                + "中枢没法对外监听。★ 先把网线/Wi-Fi 接上再试。";
            return null;
        }
        // ① 问内核:要出网的话你会用哪个源地址。
        //   目标用 192.0.2.1(RFC 5737 TEST-NET-1,专供文档举例的保留地址)——
        //   选它是为了让人一眼看出**我们并没有真的去连谁**;换成任何公网地址效果一样,
        //   但会让读代码的人以为这里在拨号。
        try
        {
            using var s = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            s.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.0.2.1"), 9));
            if (s.LocalEndPoint is System.Net.IPEndPoint ep)
            {
                var pick = ep.Address.ToString();
                // ★ 必须落在我们自己那张表里才采信 —— 内核可能选中一张我们有意跳过的网卡
                //   (回环 / APIPA / 没启用)。对不上就当这一步没答上,往下走 ②。
                if (locals.Contains(pick)) { why = $"按本机路由表选出的出网地址({pick})"; return pick; }
            }
        }
        catch { /* 没有默认路由(纯隔离网段)时会抛 —— 那不是错误,往下走 ② */ }

        // ② 只有一张网卡就没有可猜的。
        if (locals.Count == 1) { why = $"本机只有这一个网卡地址({locals[0]})"; return locals[0]; }

        // ③ 多个候选而路由表又没给出答案 ⇒ **不替他挑**。
        why = $"本机有 {locals.Count} 个网卡地址({string.Join("、", locals)}),"
            + "而路由表没能指出该用哪一个 —— **不替你挑**:挑错会把一个连不上的地址写进配对档案。"
            + "★ 请到「设备」页里手动选一张网卡。";
        return null;
    }

    /// <summary>
    /// 起 LAN Edge。★ 用 `run-lan &lt;ip&gt;`,**不是** `run`。
    ///
    /// <para>★★★ 2026-08-07(D?)。原来这里是 `run`,注释的理由是
    /// 「对外监听要配合防火墙那一步,不能顺手在自动起栈里替用户开对外的门」。
    /// 那个顾虑本身没错,但它挡住的东西**挡错了地方**,代价是整条自配对链断掉:</para>
    /// <list type="number">
    ///   <item>`run` 走 `Program.cs` 的 `Run()`,它建 `EdgeConfig(…, 8443)` —— **不传 AdminPort**,
    ///     而那个形参默认 0,`if (cfg.AdminPort &gt; 0)` 于是不成立 ⇒ **回环管理面根本没绑**。
    ///     没有管理面,`ProbeRoleAsync` 只能判 `HostHubDown`,`Build()` 只渲染 `HubDownCard`,
    ///     而 `SelfPairAsync` 唯一的调用点在 `HostSelfCard` 里 ⇒ 用户裁定的「主机上的客户端
    ///     也要自配对」在这条路上**结构上永不触发**。</item>
    ///   <item>就算把管理面补上也还不够:`run` 的业务口绑在回环,而 `DiscoverEdgeDialsAsync`
    ///     是**逐张网卡**去找 8443 的(它明确跳过回环)⇒ 自配对会停在
    ///     「网卡地址上都没人在 8443 上听」。**两道闸,补一道不通。**</item>
    /// </list>
    ///
    /// <para>★★ 而「不替用户开对外的门」这条顾虑,`run-lan` 并**没有**违反:</para>
    /// <list type="bullet">
    ///   <item>**管理面仍然只绑回环** —— `Program.cs` 里是写死的 `k.Listen(IPAddress.Loopback,
    ///     cfg.AdminPort)`,外加每条 `/admin/*` 路由自己再查一次「端口 + 回环」。
    ///     D48「管理面只绑回环」**一个字都没动**;`run-lan` 放到网卡上的是**业务口**(8443,mTLS)。</item>
    ///   <item>**绑上 ≠ 够得着** —— 防火墙那一步(`EnsureFirewallAsync`)本车道一行都没碰。
    ///     规则不在时,Windows 默认的入站阻止让 8443 对局域网**仍然不可达**,
    ///     所以不存在"监听着却没人护着"的窗口。这正是 `lan-edge` 自己在 `RunLan` 上方
    ///     写的那段话。真正把门打开的仍然是用户点那次系统授权框。</item>
    ///   <item>`run-lan` 还顺带带上了 `OpenPairingWindowOnStart: false`。而 `run` **没有** ——
    ///     它走的是那个形参的默认值 `true` ⇒ **开机自启会自动敞开 30 分钟准入窗口**,
    ///     恰恰是审计发现 [3] 明令禁止的那件事。改用 `run-lan` 把这个洞一并带上了。
    ///     (自配对不受影响:`SelfPairAsync` 自己开一个 1 分钟的窗口,用完就关。)</item>
    /// </list>
    /// </summary>
    static async Task<SetupStep> EnsureEdgeAsync()
    {
        const string name = "LAN Edge :8443";
        if (await EdgeUpAsync()) return new SetupStep(name, SetupOutcome.Skipped, "本来就在跑");
        var exe = LocateEdge();
        if (exe is null)
            return new SetupStep(name, SetupOutcome.Failed,
                "找不到 `..\\host\\localai-lan-edge.exe` —— 这台没有主机工具目录");

        // ★ 选不出地址就【停在这里】,不退回 `run`。退回去会"成功"——
        //   而那个成功正是这次要消灭的东西(起了,客户端却用不了,还没人看得出来)。
        var ip = PickBindIp(out var whyIp);
        if (ip is null) return new SetupStep(name, SetupOutcome.Failed, "选不出要绑的网卡地址:" + whyIp);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "run-lan " + ip,
                UseShellExecute = false,
                CreateNoWindow = true,
                // ══════════════════════════════════════════════════════════
                //  ★★★ 这三个 Redirect **不是**为了好看,少一个就出真事故:
                //
                //  ① `RedirectStandardInput` —— **承重**。`run-lan` 末尾有个命令台
                //    REPL(`list / approve / open / quit`),它 `Console.ReadLine()` 读到
                //    null 就 `break` ⇒ **中枢打完「已监听」当场退出**(2026-08-04 实测撞过)。
                //    中枢那边为此加了「`Console.IsInputRedirected` ⇒ 不进 REPL,安静地一直跑」,
                //    而那句判据只有在**我们真的重定向了 stdin** 时才为真。
                //    ★★ 原来这里用的 `run` 走 `Run()`,那条路**没有 REPL**,所以不重定向也没事 ——
                //      换成 `run-lan` 之后不补这一条,就会换来一个"起来了、几秒后自己没了"的中枢。
                //  ② ③ stdout/stderr 收进日志 —— 审计 B 级那条「起栈进程的输出没有落点」。
                //    `CreateNoWindow` 藏掉了黑框,而那个黑框本来担着**唯一能看到失败原因**的活。
                //    藏窗口的前提是先给失败找到别的去处,否则就是把错误藏起来。
                //  ★ 这一套是 `Views/DevicesView.StartEdgeAsync` 已经在跑的做法,逐字对齐 ——
                //    两处不一样的话,"手动起得来、自动起不来"会变成查不出根因的问题。
                // ══════════════════════════════════════════════════════════
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            var proc = Process.Start(psi);
            if (proc is null) return new SetupStep(name, SetupOutcome.Failed, "进程没起来");
            // ★ 存成静态字段而不是 `using var`:管道一旦重定向就**必须有人一直抽**,
            //   不抽,子进程写满约 4 KiB 缓冲就卡死。把 Process 留住,读取回调才活着。
            //   (客户端是长驻的,中枢本来也该比这个方法活得久。)
            _edgeProc = proc;
            try
            {
                var sw = new StreamWriter(EdgeLogPath, append: true) { AutoFlush = true };
                sw.WriteLine($"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} 自动起栈: \"{exe}\" run-lan {ip}({whyIp})---");
                proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sw) sw.WriteLine(e.Data); };
                proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sw) sw.WriteLine(e.Data); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
            catch { /* ★ 日志写不了**不算起栈失败** —— 中枢已经起来了,别因为记不了账就说它没起 */ }
        }
        catch (Exception ex)
        {
            return new SetupStep(name, SetupOutcome.Failed, ex.GetType().Name + ": " + ex.Message);
        }
        // ★★★ 边界③:**不看退出码,去探**。Edge 起不来时进程可能还活着而端口没开。
        //   ★ 但探的是**管理面**(见 EdgeUpAsync)—— 探 8443 的那一版对客户端毫无意义。
        if (await WaitUntilAsync(() => EdgeUpAsync(), 20_000))
            return new SetupStep(name, SetupOutcome.Ok,
                $"已起(业务口 {ip}:{EdgePort},管理面 127.0.0.1:{HubAdmin.AdminPort})并探到管理面。{whyIp}");
        return new SetupStep(name, SetupOutcome.Failed,
            $"进程起了,但 20 秒内连不上回环管理面 127.0.0.1:{HubAdmin.AdminPort} —— **不当作成功**。"
            + ExitNote(_edgeProc) + " 中枢自己打印的话在:" + EdgeLogPath);
    }

    /// <summary>自动起栈拉起来的那个中枢进程。★ 留住它,重定向的管道才有人抽(见 EnsureEdgeAsync)。</summary>
    static Process? _edgeProc;

    /// <summary>
    /// 中枢自己打印的话落在哪。★ 与 <c>Views/DevicesView.StartEdgeAsync</c> **同一个文件** ——
    /// 两条起栈路径写两个日志的话,人找错文件会得出"它什么都没说"的结论。
    /// </summary>
    public static string EdgeLogPath => Path.Combine(Path.GetTempPath(), "localai-edge.log");

    /// <summary>
    /// 起栈失败时,把子进程**已经退出**这件事和它的退出码翻译成人话。
    ///
    /// <para>★ 这是审计 B 级「自动起栈的进程输出今天完全没有落点」那一条的**便宜那一半**:
    /// `CreateNoWindow` 且不 Redirect ⇒ 那几行关键的诊断(密钥打不开 / 端口被占)全打进了
    /// 一个没有窗口的控制台,谁也看不到。而 `lan-edge` 已经把同样的信息**编进了退出码**,
    /// 那份信号今天被 `EnsureEdgeAsync` 整个丢掉了 —— 捡回来不要钱。</para>
    ///
    /// <para>★★ 为什么不干脆重定向 stdout/stderr:那要么得有人**持续**抽干管道
    /// (不抽,子进程写满 ~4KB 缓冲就**卡死**),要么套一层 `cmd /c … &gt; log`(多一个进程,
    /// 且返回的 Process 就不再是 Edge 本身了)。两条都不是顺手能做对的,已写进决议包。</para>
    /// </summary>
    static string ExitNote(Process? p)
    {
        try
        {
            if (p is null) return "(没拿到子进程句柄,读不到它的退出状态。)";
            if (!p.HasExited)
                return "(进程**还活着**,只是端口没开 —— 多半是它卡在某一步,"
                     + "或者绑的地址不对。手动双击 `localai-lan-edge.exe` 能看到它到底说了什么。)";
            // ★ 这几个数字来自 `10-core/lan-edge/Program.cs` 的 RunLan;
            //   对不上的表现是"给了一句自信而错误的原因",比不给还坏 ⇒ 未登记的码**如实说不认识**。
            return " 子进程**已经退出**,退出码 " + p.ExitCode + p.ExitCode switch
            {
                1 => ":这台还没有中枢身份(要先 `localai-identity init`)。",
                2 => ":lan-edge 说命令行不对 —— 我们传的绑定地址它不认。",
                3 => ":**打不开身份密钥(CA)**。★ TPM/CNG 用户密钥绑定【铸造时】的完整性等级 —— "
                   + "这套身份多半是用另一个等级(普通/管理员)铸的。",
                4 => $":{EdgePort} 已被占用 —— 多半是中枢**已经在跑了**(另一个窗口),那就不用再开一个。",
                _ => "(这个码没有登记过,我不知道它是什么意思 —— 手动双击那个 exe 看它自己怎么说)。",
            };
        }
        catch (Exception ex) { return "(读不到子进程的退出状态:" + ex.Message + ")"; }
    }

    /// <summary>轮询直到条件为真或超时。★ 起进程到端口可用之间必然有一段,不等就会误判成失败。</summary>
    static async Task<bool> WaitUntilAsync(Func<Task<bool>> probe, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (await probe()) return true;
            await Task.Delay(400);
        }
        return false;
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
        /// <summary>主机,有身份,栈没起 ⇒ **自动起栈**。</summary>
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

    /// <summary>开机分流的结论。★ `Headline` 是界面上那一行,**每条路都有**,不许静默。</summary>
    public sealed record BootDecision(BootRoute Route, RoleVerdict Role, string Headline)
    {
        /// <summary>★★★ 只有这一条为真时才允许碰起栈。副机路径**结构上**取不到它。</summary>
        public bool MayStartStack => Route == BootRoute.HostStartStack;
    }

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
                    "这台是中枢主机,身份已就位,**栈还没起** —— 正在自动启动网关与 LAN Edge…");
            return new BootDecision(BootRoute.HostReady, role, "");
        }
        // ★★★ 以下**全部**是副机分支。这里一行起栈代码都没有,而且拿不到 MayStartStack。
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
    /// <summary>跑一个进程并把 stdout+stderr 都收回来。★ 不提权 —— 这里跑的都是必须普通用户跑的东西。</summary>
    static async Task<(int code, string output)> RunCapturedAsync(string exe, string args, string? workDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,            // ★ false 才能重定向
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workDir ?? "",
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, "进程没起来");
            var so = await p.StandardOutput.ReadToEndAsync();
            var se = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, (so + "\n" + se).Trim());
        }
        catch (Exception ex) { return (-1, ex.GetType().Name + ": " + ex.Message); }
    }

    static string ReadAndDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            var t = File.ReadAllText(path);
            try { File.Delete(path); } catch { }
            return t;
        }
        catch { return ""; }
    }

    /// <summary>把多行输出压成一行给界面用;太长就截断(界面不是日志窗口)。</summary>
    static string Trim(string s)
    {
        var one = string.Join(" / ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                      .Select(x => x.Trim()).Where(x => x.Length > 0));
        return one.Length <= 400 ? one : one[..400] + "…";
    }
}
