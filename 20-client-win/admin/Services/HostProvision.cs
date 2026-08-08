// V21 -- 把这台**装成主机**的那一半:铸身份 · 防火墙 · 起网关与 LAN Edge。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ 本文件是 `app/Services/HostSetup.cs` 搬过来的那一半(迁移地图 §1)。
//
//  地图那一条是整份地图里最硬的:**`HostSetup` 不能整块搬**。
//  留在客户端的是**角色判据**(`DecideRole` / `DetectRoleAsync` / `ResolvesToThisMachine` /
//  `DecideBoot` / `DetectBootAsync` / `GatewayPort` / `GatewayUpAsync` / `LocalNics` /
//  `IdentityExistsAsync`)—— 它是全客户端**唯一**能回答「我该拨回环网关还是拨 LAN Edge」的东西
//  (`App.xaml.cs → Hub.NoteRole → IsHostMachine → BusinessRoute → DecideBusinessTarget`)。
//  整块搬走 ⇒ `IsHostMachine` 恒为 false ⇒ 主机上的客户端走 LAN Edge、拿 `lan-device` 档
//  ⇒ 组件面板点「确定」又吃「这台设备不能做这个操作」—— **而且一条断言都不会红**
//  (V13 那批断言里的 `isHost` 是自检自己喂的,ASSERTION-PITFALLS 第 13 条)。
//
//  ⇒ 搬过来的是**动作**那一半,留下的是**判据**那一半。管理端用 `<Compile Link>` 编同一份
//    `HostSetup.cs`,所以下面照常调 `HostSetup.GatewayPort` / `HostSetup.GatewayUpAsync`——
//    **不是复制,是同一个文件**。
// ══════════════════════════════════════════════════════════════════════════════
//
// ★ 三条不可让步的边界(原样从 HostSetup.cs 带过来,一个字没改):
//   ① **绝不**调 `重置并铸身份.cmd` —— 它开头就 `del /q {state}\identity\*`,那是破坏性的,
//      会让所有已配对设备失效。这里只调 `localai-identity.exe init`,
//      而 init 本身是 fail-closed 的(已存在就拒绝覆盖并返回 1)。
//   ② 需要管理员的**只有防火墙**一步,而且**只把那一个脚本**提权;
//      identity / Edge / 网关**绝不**放进提权进程 —— 它们一旦继承 High,身份就毁了。
//      ★★ 管理端自己也**不提权**(见 `Program.cs` 的 D46 护栏),所以这条边界原样成立。
//   ③ **做完要验**:不能因为进程退出码是 0、或者用户点了 UAC,就宣布"成功"。
//      身份看目录、防火墙看规则在不在、栈看端口答不答话。

using System.Diagnostics;
using LocalAI.Client.Services;
using AdminApp = LocalAI.Client.Services.AdminApp;
using HostSetup = LocalAI.Client.Services.HostSetup;

namespace LocalAI.Admin.Services;

/// <summary>一步的结果。Ok=确实做成了(而且**验过**);Skipped=本来就是好的;Failed=没做成。</summary>
public sealed record SetupStep(string Name, SetupOutcome Outcome, string Detail);

public enum SetupOutcome { Ok, Skipped, Failed }

public static class HostProvision
{
    /// <summary>防火墙规则名。★ 必须与 `90-ops/lan/lan-firewall.ps1` 的 `$RuleName` 一致 ——
    /// 对不上的表现是"明明加好了却总说没加"。</summary>
    public const string FirewallRuleName = "LocalAI-LAN-Edge";

    public const int EdgePort = 8443;

    // ---------------------------------------------------------------- 身份
    /// <summary>
    /// 确保本机有中枢身份。已经有就是 Skipped(**不是**错误,更不去覆盖)。
    /// ★ 调的是 `localai-identity.exe init`,不是 `重置并铸身份.cmd`(后者会先删掉身份)。
    ///
    /// <para>★★ 「有没有身份」那一问(<see cref="HostSetup.IdentityExistsAsync"/>)**留在客户端** ——
    /// 它是角色判据的证据之一。**铸**这个动作在这儿,**问**那个动作在那儿:
    /// 这正是「两侧协议 ⇒ 客户端留;单侧权威 ⇒ 搬」那条线落在同一个类上的样子。</para>
    /// </summary>
    public static async Task<SetupStep> EnsureIdentityAsync()
    {
        const string name = "中枢身份";
        // ★★ 这里【不】预判"我是不是管理员"(实测推翻:UAC 关闭的机器上一切都是 High,
        //   身份也就是在 High 下铸的、在 High 下打得开)。拿它当门槛会把健康机器判成不能用。
        //   真正要防的是「在 A 等级铸、将来在 B 等级用」—— 那要把铸造时的等级记下来再比,
        //   属中枢侧的改动,已写进 integrity-guard-asks-wrong-question-2026-08-03.md。
        //   在那之前:直接跑 init,它自己 fail-closed(已存在就拒绝覆盖),失败原因原样带回来。

        var dir = AdminApp.HostToolsDir();
        if (dir is null) return new SetupStep(name, SetupOutcome.Failed, "本机没有主机端程序目录,没法铸身份。");
        var exe = Path.Combine(dir, "localai-identity.exe");
        if (!File.Exists(exe)) return new SetupStep(name, SetupOutcome.Failed, "找不到 " + exe);

        var (code, output) = await ProcRun.RunCapturedAsync(exe, "init", dir);
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
        var (code, output) = await ProcRun.RunCapturedAsync("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"if (Get-NetFirewallRule -DisplayName '"
            + FirewallRuleName + "' -ErrorAction SilentlyContinue) { 'YES' } else { 'NO' }\"", null);
        return code == 0 && output.Contains("YES", StringComparison.Ordinal);
    }

    /// <summary>
    /// 找防火墙脚本。★ 两处都找,而且**找不到就返回 null**,绝不拼一个路径出来让 Process.Start 去炸:
    ///   ① 主机端目录里(出包时应当把它一并放进去 —— 那才是不依赖仓库布局的正路);
    ///   ② 退一步:仓库布局 `<repo>/90-ops/lan/`。
    /// <para>★ 相对路径的基准从「客户端 exe」换成了「**管理端 exe**」—— 两者在出包里并排
    /// (`dist\client\` 与 `dist\admin\`),所以往上两级仍然是仓库根,层数一个都没变。</para>
    /// </summary>
    public static string? FirewallScript()
    {
        var dir = AdminApp.HostToolsDir();
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

    // ════════════════════════════════════════════════════════════════════
    //  主机自动起栈
    //
    //  ★★★ 与 D87① 的关系,别读错(决议包里有完整版):
    //    D87① 裁的是「**不做开机预热**」—— 那说的是**模型**。
    //    **网关不是被预热的东西,它是决定要不要预热的那个东西本身**(D83:Broker 就住在网关里)。
    //    ⇒ 这里起 gateway 与 lan-edge,**绝不起 llama-server**:
    //      按需装载(S14/S16-b)已经落地,静态起会和 transient 平面打架。
    //
    //  ★★★ V21:起栈的入口从此**只有管理端这一个**(V10 §7「两个 exe 都想起 Edge ⇒
    //    只保留管理端这一个入口」)。客户端那边 `MayStartStack` 与 `EnsureStackAsync` 一起删掉了,
    //    客户端源码里**不再有任何起栈入口** —— 这条由元断言钉住并红测过。
    //
    //  ★★★ 边界③在这儿尤其要紧:**不能因为进程退出码是 0 就宣布成功**。
    //    uvicorn 起不来时进程可能活着但端口没开;Edge 绑不上网卡时也一样。
    //    ⇒ 起完**去探**:网关探 `/health`,Edge 探回环管理面。探不到就如实说是哪一步。
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

    /// <summary>
    /// Edge 在不在。★ 探的是**回环管理面**(127.0.0.1:8442),不是 8443。
    ///
    /// <para>★★★ 2026-08-07 改的判据,理由是原来那一条**问错了问题**:
    /// 它探 `127.0.0.1:8443`,而那个口在 `run` 模式下确实开着 ⇒ 自动起栈报「已起并探到 8443」,
    /// 界面上一路绿灯 —— 可本机客户端接下来要用的两样东西**一样都没有**:
    ///   ① 管理面(8442)没绑 ⇒ 角色探测判 `HostHubDown`;
    ///   ② 业务口只在回环上 ⇒ `DiscoverEdgeDialsAsync` 逐张网卡去找 8443(它**跳过回环**),
    ///      一个都找不到。
    /// 于是"起栈成功"这句话与客户端能不能用**完全无关** —— 典型的「失败与成功长得一样」。</para>
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
    /// <para>★★★ 这一条**必然会在发布安装上找不到**:`dist/host/` 里只有 `localai-lan-edge.exe`
    /// 与 `localai-identity.exe`,**没有网关** —— 网关是 `10-core/gateway` 的 Python 源码 +
    /// `<AI 根>\venvs\gateway` 那个虚拟环境,两者都只存在于**仓库**里。
    /// ⇒ 「意图即起」在引导层断掉的根因不是没人写代码,是**网关根本没有随包发布**。
    /// 见 `decision-packets/admin-app-packaging-2026-08-08.md`。</para>
    /// </summary>
    public static (string? Python, string DirOrWhy) LocateGateway()
    {
        try
        {
            var here = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrWhiteSpace(here)) return (null, "拿不到管理端自己的路径");
            // 仓库形态:dist\admin\ → ..\..\10-core\gateway(与 dist\client\ 同一层深度)
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
        var dir = AdminApp.HostToolsDir();
        if (dir is null) return null;
        var exe = Path.Combine(dir, "localai-lan-edge.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>
    /// 把栈拉起来。★ **全仓唯一的起栈入口** —— 客户端里一行都没有。
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
        var name = $"统一入口网关 :{HostSetup.GatewayPort}";
        // ★ 探活与端口都走**客户端那份** HostSetup(csproj link 的同一个文件),不另存一个数:
        //   客户端拨号(`HubClient.DecideBusinessTarget`)读的就是它。两处各写一个字面量的话,
        //   自检里换一个假网关只换得掉其中一个。
        if (await HostSetup.GatewayUpAsync()) return new SetupStep(name, SetupOutcome.Skipped, "本来就在跑");
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
                Arguments = "-m uvicorn gateway:app --host 127.0.0.1 --port " + HostSetup.GatewayPort,
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
        if (await WaitUntilAsync(() => HostSetup.GatewayUpAsync(), 20_000))
            return new SetupStep(name, SetupOutcome.Ok, "已起并探到 /health");
        return new SetupStep(name, SetupOutcome.Failed,
            $"进程起了,但 20 秒内探不到 http://127.0.0.1:{HostSetup.GatewayPort}/health —— "
            + "**不当作成功**(端口被占?venv 缺依赖?)");
    }

    // ════════════════════════════════════════════════════════════════════
    //  ★★★ 绑哪个地址。自动起栈从 `run` 改成 `run-lan <ip>` 之后才需要这一步。
    //
    //  ★ 为什么必须选一个**网卡**地址、而不能继续用 `run` 的回环:见 EnsureEdgeAsync 上方。
    //  ★ 为什么这不算"替用户猜":这里问的是**操作系统自己**会用哪个源地址出网 ——
    //    UDP 的 Connect **不发任何数据包**,它只让内核按路由表做一次源地址选择,
    //    读 LocalEndPoint 就是内核给的答案。那是一次查询,不是一次猜测。
    //  ★ 查不出来时【不硬选】:只有在候选**唯一**时才继续,否则如实报「有几个、分别是什么」
    //    并让这一步失败。多网卡上随手挑一个会把一个错地址写进配对档案,
    //    而那比"没起来"难查得多。
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
            + "★ 请到「主机中枢」那一页里手动选一张网卡。";
        return null;
    }

    /// <summary>
    /// 起 LAN Edge。★ 用 `run-lan &lt;ip&gt;`,**不是** `run`。
    ///
    /// <para>★★★ 2026-08-07。原来这里是 `run`,注释的理由是
    /// 「对外监听要配合防火墙那一步,不能顺手在自动起栈里替用户开对外的门」。
    /// 那个顾虑本身没错,但它挡住的东西**挡错了地方**,代价是整条自配对链断掉:</para>
    /// <list type="number">
    ///   <item>`run` 走 `Program.cs` 的 `Run()`,它建 `EdgeConfig(…, 8443)` —— **不传 AdminPort**,
    ///     而那个形参默认 0,`if (cfg.AdminPort &gt; 0)` 于是不成立 ⇒ **回环管理面根本没绑**。
    ///     没有管理面,角色探测只能判 `HostHubDown`,而**整个管理端**都够不着它自己的中枢。</item>
    ///   <item>就算把管理面补上也还不够:`run` 的业务口绑在回环,而 `DiscoverEdgeDialsAsync`
    ///     是**逐张网卡**去找 8443 的(它明确跳过回环)⇒ 主机自己那台客户端会停在
    ///     「网卡地址上都没人在 8443 上听」。**两道闸,补一道不通。**</item>
    /// </list>
    ///
    /// <para>★★ 而「不替用户开对外的门」这条顾虑,`run-lan` 并**没有**违反:</para>
    /// <list type="bullet">
    ///   <item>**管理面仍然只绑回环** —— `Program.cs` 里是写死的 `k.Listen(IPAddress.Loopback,
    ///     cfg.AdminPort)`,外加每条 `/admin/*` 路由自己再查一次「端口 + 回环」。
    ///     D48「管理面只绑回环」**一个字都没动**;`run-lan` 放到网卡上的是**业务口**(8443,mTLS)。</item>
    ///   <item>**绑上 ≠ 够得着** —— 防火墙那一步(<see cref="EnsureFirewallAsync"/>)是分开的。
    ///     规则不在时,Windows 默认的入站阻止让 8443 对局域网**仍然不可达**,
    ///     所以不存在"监听着却没人护着"的窗口。真正把门打开的仍然是用户点那次系统授权框。</item>
    ///   <item>`run-lan` 还顺带带上了 `OpenPairingWindowOnStart: false`。而 `run` **没有** ——
    ///     它走的是那个形参的默认值 `true` ⇒ **开机自启会自动敞开 30 分钟准入窗口**,
    ///     恰恰是 D48 裁定 2 明令禁止的那件事。</item>
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
                //  ② ③ stdout/stderr 收进日志 —— 审计 B 级那条「起栈进程的输出没有落点」。
                //    `CreateNoWindow` 藏掉了黑框,而那个黑框本来担着**唯一能看到失败原因**的活。
                //    藏窗口的前提是先给失败找到别的去处,否则就是把错误藏起来。
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
            //   (管理端是后台常驻的,中枢本来也该比这个方法活得久。)
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
    /// 中枢自己打印的话落在哪。★ 两条起栈路径(自动起栈与「主机中枢」那一页的手动起)
    /// 写**同一个文件** —— 两处写两个日志的话,人找错文件会得出"它什么都没说"的结论。
    /// </summary>
    public static string EdgeLogPath => Path.Combine(Path.GetTempPath(), "localai-edge.log");

    /// <summary>
    /// 起栈失败时,把子进程**已经退出**这件事和它的退出码翻译成人话。
    ///
    /// <para>★ `lan-edge` 已经把关键信息(密钥打不开 / 端口被占)**编进了退出码**,
    /// 那份信号捡回来不要钱。</para>
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
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (await probe()) return true;
            await Task.Delay(400);
        }
        return false;
    }

    // ---------------------------------------------------------------- 工具
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
