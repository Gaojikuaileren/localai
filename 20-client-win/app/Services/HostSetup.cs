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
    /// <param name="HostToolsPresent">客户端旁边有没有 `..\host\localai-lan-edge.exe`(D36「安装事实」)。</param>
    /// <param name="IdentityExists">本机有没有铸好的中枢身份(D36「持有 CA 私钥的那一台」)。</param>
    /// <param name="ConfiguredHubHost">已知的中枢主机名/地址;不知道就传 null。**不要求配过对**。</param>
    /// <param name="ConfiguredHubResolvesLocal">上面那个地址解析到本机(回环或本机网卡 IP)了吗。
    /// null = 没得解析或解析失败 —— ★ 它与 false 不是一回事,必须分开。</param>
    public sealed record RoleEvidence(bool HostToolsPresent, bool IdentityExists,
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
                + (ev.HostToolsPresent
                   ? "★ 但旁边有一份主机工具目录:两条证据打架时一律判【不是主机】,"
                     + "否则两台机器会同时起 Edge 抢 8443、两个 Broker 各写各的账本。"
                   : ""),
                ev);

        // ── ★ 肯定证据 ①:D36 的主机名判据(地址解析到本机)。
        if (ev.ConfiguredHubResolvesLocal == true)
            return new RoleVerdict(true,
                $"中枢地址 `{ev.ConfiguredHubHost}` 解析到本机 —— 按 D36,这台就是主机。", ev);

        // ── ★ 肯定证据 ②:D36 的「安装事实」。地址无从解析时才轮到它。
        //   **两条都要**:光有工具目录不算(可能是拷过来的),必须真的**铸过身份**
        //   —— 那才是 D36 说的「持有 CA 私钥与成员表的那一台」。
        if (ev.HostToolsPresent && ev.IdentityExists)
            return new RoleVerdict(true,
                "本机铸过中枢身份、且带着主机工具目录 —— 按 D36「主机 = 持有 CA 私钥与成员表的那一台」,"
                + "这台就是主机(没有配置中枢地址,走的是安装事实这条)。", ev);

        // ── 其余一律判"不是主机",并**说清是哪一种拿不准**。
        if (ev.HostToolsPresent && !ev.IdentityExists)
            return new RoleVerdict(false,
                "旁边有主机工具目录,但**还没有铸过中枢身份** —— 光有工具不等于是主机"
                + "(可能是整包拷过来的)。铸完身份再判一次就会认出来。", ev);
        return new RoleVerdict(false,
            "没有中枢地址可解析,旁边也没有主机工具目录 —— **拿不准,按规矩判【不是主机】**。"
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
        var dir = HubAdmin.HostToolsDir();
        var toolsPresent = dir is not null;
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

    public const int GatewayPort = 8080;

    /// <summary>网关在不在(探 /health,**不看进程**)。</summary>
    public static async Task<bool> GatewayUpAsync(int timeoutMs = 1500)
    {
        try
        {
            using var c = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            var r = await c.GetAsync($"http://127.0.0.1:{GatewayPort}/health");
            return (int)r.StatusCode is >= 200 and < 300;
        }
        catch { return false; }
    }

    /// <summary>Edge 在不在(只看 8443 能不能连上 —— TLS 握手要证书,这里不做)。</summary>
    public static async Task<bool> EdgeUpAsync(int timeoutMs = 1500)
    {
        try
        {
            using var t = new System.Net.Sockets.TcpClient();
            var connect = t.ConnectAsync(System.Net.IPAddress.Loopback, EdgePort);
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

    static async Task<SetupStep> EnsureEdgeAsync()
    {
        const string name = "LAN Edge :8443";
        if (await EdgeUpAsync()) return new SetupStep(name, SetupOutcome.Skipped, "本来就在跑");
        var exe = LocateEdge();
        if (exe is null)
            return new SetupStep(name, SetupOutcome.Failed,
                "找不到 `..\\host\\localai-lan-edge.exe` —— 这台没有主机工具目录");
        try
        {
            // ★ `run` 只绑回环;`run-lan <ip>` 才对外。这里用 `run` ——
            //   对外监听要配合防火墙那一步(EnsureFirewallAsync),是**另一次**有意的动作,
            //   不能顺手在自动起栈里替用户开对外的门。
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "run",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            using var p = Process.Start(psi);
            if (p is null) return new SetupStep(name, SetupOutcome.Failed, "进程没起来");
        }
        catch (Exception ex)
        {
            return new SetupStep(name, SetupOutcome.Failed, ex.GetType().Name + ": " + ex.Message);
        }
        if (await WaitUntilAsync(() => EdgeUpAsync(), 20_000))
            return new SetupStep(name, SetupOutcome.Ok, "已起并探到 8443");
        return new SetupStep(name, SetupOutcome.Failed,
            "进程起了,但 20 秒内连不上 8443 —— **不当作成功**(证书没铸?端口被占?)");
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
