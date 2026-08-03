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
    public static bool IdentityExists()
    {
        var dir = HubAdmin.HostToolsDir();
        if (dir is null) return false;
        var exe = Path.Combine(dir, "localai-identity.exe");
        if (!File.Exists(exe)) return false;
        try
        {
            var (code, _) = RunCapturedAsync(exe, "status", dir).GetAwaiter().GetResult();
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
