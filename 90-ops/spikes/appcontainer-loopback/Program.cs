using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AcSpike;

internal static class Program
{
    private const string Ac1Name = "LocalAI.Spike.NoCaps";
    private const string Ac2Name = "LocalAI.Spike.Caps";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0) { Usage(); return 2; }

        return args[0] switch
        {
            "probe" => Probe.Run(args[1], args[2]),
            "run" => RunLocal(args.Contains("--keep"), exemptPhase: false,
                              selfExempt: args.Contains("--self-exempt"), lan: args.Contains("--lan")),
            "run-exempt" => RunLocal(keep: false, exemptPhase: true),
            "medium-probe" => MediumProbe(args.Length > 1 ? args[1] : null),
            "user-exempt-probe" => UserExemptProbe(),
            "profile-add" => ProfileAdd(),
            "profile-del" => ProfileDel(),
            "unix-check" => UnixCheck(),
            _ => Usage()
        };
    }

    private static int Usage()
    {
        Console.WriteLine("AcSpike <run|run-exempt|medium-probe|probe> ...");
        Console.WriteLine("  run           零提权:建 AppContainer、测回环/管道/文件/逃逸,再删掉 profile");
        Console.WriteLine("                  --lan  额外测「换成本机 LAN IP 绕不绕得过」。★ 会弹防火墙授权框,默认关");
        Console.WriteLine("  run-exempt    需管理员:在 run 的基础上加/撤回环豁免,验证豁免的粒度");
        Console.WriteLine("  medium-probe  从提权上下文造一个 Medium/deny-only-admins token,试着自己开豁免");
        return 2;
    }

    // =====================================================================
    //  主流程
    // =====================================================================

    /// <param name="lan">
    /// ★ 默认关。开了才会在 0.0.0.0 上绑一个监听去测「换成本机 LAN IP 能不能绕过回环隔离」。
    /// 关掉的理由:非回环绑定会让 Windows 弹「是否允许公共网络和专用网络访问此应用」——
    /// 用户双击时冒出一个防火墙弹窗是很坏的体验,而且不管点哪个都会留下一条持久规则
    /// (点「取消」留 Block 规则,点「允许」留 Allow 规则),那是本 spike 不该留下的机器状态。
    /// 这一格已经测过(容器连本机 LAN IP 同样被挡),不需要每次重跑。
    /// </param>
    private static int RunLocal(bool keep, bool exemptPhase, bool selfExempt = false, bool lan = false)
    {
        var report = new JsonObject();
        var me = Native.DescribeCurrentToken();
        report["launcher"] = new JsonObject
        {
            ["userName"] = me.UserName,
            ["userSid"] = me.UserSid,
            ["integrity"] = me.IntegrityLabel,
            ["administrators"] = me.AdministratorsState,
            ["pid"] = Environment.ProcessId,
            ["os"] = Environment.OSVersion.VersionString,
        };
        Banner($"launcher: {me.UserName} · integrity={me.IntegrityLabel} · Administrators={me.AdministratorsState}");
        if (me.IntegrityLabel == "High")
            Console.WriteLine("  ⚠ 本次是【提权】上下文。AppContainer 的网络隔离由 WFP 按 token 的 AppContainer SID 判,\n" +
                              "    与完整性等级无关,但结论仍应在普通用户上下文复测一次(D46 纪律)。");

        var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var work = Path.Combine(Path.GetTempPath(), "localai-acspike");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(outDir);
        foreach (var f in Directory.GetFiles(outDir)) { try { File.Delete(f); } catch { } }

        // ---- AppContainer profile ×2:一个零 capability,一个带网络 capability ----
        var caps = new[] { "S-1-15-3-1" /* internetClient */, "S-1-15-3-3" /* privateNetworkClientServer */ };
        var ac1 = CreateProfile(Ac1Name, Array.Empty<string>());
        var ac2 = CreateProfile(Ac2Name, caps);
        report["appContainers"] = new JsonObject
        {
            [Ac1Name] = new JsonObject { ["sid"] = ac1, ["capabilities"] = new JsonArray() },
            [Ac2Name] = new JsonObject { ["sid"] = ac2, ["capabilities"] = new JsonArray(caps.Select(c => (JsonNode)c).ToArray()) },
        };
        Console.WriteLine($"  {Ac1Name,-24} = {ac1}");
        Console.WriteLine($"  {Ac2Name,-24} = {ac2}");

        var acSids = new[] { ac1, ac2 }.Where(s => s != null).ToArray();
        GrantDir(exeDir, acSids, FileSystemRights.ReadAndExecute);
        GrantDir(outDir, acSids, FileSystemRights.Modify);

        // ---- 起监听:一个「有人听」、一个「没人听」、一个绑 0.0.0.0 ----
        // LOCALAI_TARGET_PORT:打进来一个【已经有人在听】的端口(比如真在跑的 llama-server:18081),
        // 此时不自己绑监听,直接打真家伙 —— 去掉「我用替身监听测的」这条保留意见。
        int? givenTarget = int.TryParse(Environment.GetEnvironmentVariable("LOCALAI_TARGET_PORT"), out var gp) ? gp : null;
        int portListen = givenTarget
                         ?? FirstFreePort(18081, 18081) ?? FirstFreePort(28081, 28090)
                         ?? throw new InvalidOperationException("找不到空闲端口");
        int portDead = FirstFreePort(28281, 28290) ?? throw new InvalidOperationException("找不到空闲端口");
        int portInbound = FirstFreePort(28381, 28390) ?? throw new InvalidOperationException("找不到空闲端口");
        var inboundMarker = Path.Combine(outDir, "inbound-listening.txt");

        if (givenTarget == null)
        {
            var lLoop = new TcpListener(IPAddress.Loopback, portListen);
            lLoop.Start();
            AcceptForever(lLoop);
        }
        else Console.WriteLine($"  ★ 用外部已有监听做靶子:127.0.0.1:{portListen}(不自己绑)");

        // ★ 只有 --lan 才绑 0.0.0.0。见 RunLocal 的 lan 参数说明:
        //   非回环绑定会弹防火墙授权框,而不管点哪个都会留下一条持久规则。
        int portAny = 0;
        string lanIp = null;
        if (lan)
        {
            portAny = FirstFreePort(28181, 28190) ?? throw new InvalidOperationException("找不到空闲端口");
            var lAny = new TcpListener(IPAddress.Any, portAny); lAny.Start();
            AcceptForever(lAny);
            lanIp = LanIPv4();
        }

        Console.WriteLine($"  listener 127.0.0.1:{portListen} · 空端口 {portDead}" +
                          (lan ? $" · listener 0.0.0.0:{portAny} · LAN IP {lanIp ?? "(none)"}"
                               : " · LAN IP 那一格已跳过(要测加 --lan;默认关是为了不弹防火墙框)"));
        report["ports"] = new JsonObject
        {
            ["loopbackListening"] = portListen,
            ["nothingListening"] = portDead,
            ["anyListening"] = lan ? portAny : null,
            ["lanIp"] = lanIp,
            ["lanProbeEnabled"] = lan,
            ["note18081"] = portListen == 18081 ? "真的绑在 18081(llama-server 的端口)" : "18081 当时被占,用了替代端口",
        };

        // ---- 命名管道 ×2:默认 DACL / 显式给 AppContainer SID 授权 ----
        const string pipeDefault = "LocalAI.Spike.DefaultDacl";
        const string pipeGranted = "LocalAI.Spike.AcGranted";
        var pipeLog = new JsonArray();
        var spawned = new List<int>();
        StartPipeServer(pipeDefault, null, pipeLog, spawned);
        StartPipeServer(pipeGranted, BuildSddl(me.UserSid, acSids), pipeLog, spawned);

        // ---- AF_UNIX ----
        // ★ AF_UNIX 的落点很讲究(实测,见 unix-check):
        //   在 {state} / {cache}\tmp / {code} / Windows 自己的 Temp 下 bind+connect 全通,
        //   而在**机主的 %TEMP%** 下 connect 报 WSAEINVAL(10022),且失败会留下一个删不掉的
        //   socket 文件,把该路径后续的 bind 也一起弄坏。
        //   outDir 就在机主 %TEMP% 里 ⇒ socket 默认放这儿测出来的「容器连不上」是**我的路径问题**,
        //   不是容器的性质。所以允许用 LOCALAI_SOCK_DIR 指到一个已实测可用的目录。
        var sockDir = Environment.GetEnvironmentVariable("LOCALAI_SOCK_DIR");
        if (!string.IsNullOrEmpty(sockDir))
        {
            Directory.CreateDirectory(sockDir);
            GrantDir(sockDir, acSids, FileSystemRights.Modify);   // 容器要够得着才谈得上「能不能连」
        }
        else sockDir = outDir;
        var sockPath = Path.Combine(sockDir, $"spike-{Environment.ProcessId}.sock");
        report["unixSocketDir"] = sockDir;
        StartUnixServer(sockPath);

        // ---- 子进程配置 ----
        var probeFiles = FileTargets();
        report["fileTargets"] = new JsonArray(probeFiles.Select(t =>
            (JsonNode)new JsonObject { ["name"] = t.Name, ["path"] = t.Path }).ToArray());

        JsonObject MakeCfg(bool withGrandchild, string gcOut, string selfExemptProfile = null)
        {
            var tcp = new JsonArray
            {
                new JsonObject { ["name"] = "loopback-有人听", ["host"] = "127.0.0.1", ["port"] = portListen },
                new JsonObject { ["name"] = "loopback-没人听", ["host"] = "127.0.0.1", ["port"] = portDead },
            };
            // 同一个目标再来一遍,给 30 秒 —— 为的是分清「被静默丢包」与「只是比 4 秒慢」。
            // 只主子进程做:孙进程也做的话,一轮要多花半分钟,且它不需要这个数。
            if (withGrandchild)
                tcp.Add(new JsonObject { ["name"] = "loopback-有人听-等30秒", ["host"] = "127.0.0.1", ["port"] = portListen, ["timeoutMs"] = 30000 });
            if (lan && lanIp != null)
                tcp.Add(new JsonObject { ["name"] = "本机LAN IP-有人听", ["host"] = lanIp, ["port"] = portAny });
            var cfg = new JsonObject
            {
                ["tcp"] = tcp,
                ["pipes"] = new JsonArray
                {
                    new JsonObject { ["name"] = "管道-默认DACL", ["pipe"] = pipeDefault },
                    new JsonObject { ["name"] = "管道-显式授权AC", ["pipe"] = pipeGranted },
                },
                ["unixSocket"] = sockPath,
                ["files"] = new JsonArray(probeFiles.Select(t =>
                    (JsonNode)new JsonObject { ["name"] = t.Name, ["path"] = t.Path }).ToArray()),
            };
            if (selfExemptProfile != null)
                cfg["selfExempt"] = new JsonObject
                {
                    ["profileName"] = selfExemptProfile,
                    ["host"] = "127.0.0.1",
                    ["port"] = portListen,
                };
            if (withGrandchild)
            {
                // 只有主子进程做「反方向」测试;孙进程不做,否则会和主子进程抢同一个端口
                cfg["inbound"] = new JsonObject
                {
                    ["port"] = portInbound,
                    ["marker"] = inboundMarker,
                    ["holdMs"] = 12000,
                };
                cfg["grandchild"] = new JsonObject
                {
                    ["exe"] = Path.Combine(exeDir, "AcSpike.exe"),
                    ["cfg"] = Path.Combine(outDir, "cfg-grandchild.json"),
                    ["out"] = gcOut,
                };
            }
            return cfg;
        }

        File.WriteAllText(Path.Combine(outDir, "cfg-grandchild.json"), MakeCfg(false, null).ToJsonString());
        var gcOut = Path.Combine(outDir, "res-grandchild.json");
        string WriteCfg(string name, string selfExemptProfile)
        {
            var p = Path.Combine(outDir, name);
            File.WriteAllText(p, MakeCfg(true, gcOut, selfExemptProfile).ToJsonString());
            return p;
        }
        var cfgControl = WriteCfg("cfg-control.json", selfExempt ? Ac1Name : null);
        var cfgA1 = WriteCfg("cfg-ac1.json", selfExempt ? Ac1Name : null);
        var cfgA2 = WriteCfg("cfg-ac2.json", selfExempt ? Ac2Name : null);

        if (selfExempt)
        {
            Console.WriteLine("  ⚠ --self-exempt:本轮会尝试改回环豁免列表(机器级状态)。收尾会 -c 清空并记账。");
            report["exemptListBeforeAll"] = Sh("CheckNetIsolation.exe", "LoopbackExempt -s");
        }

        // ---- 三个跑法:对照组(不进容器)/ 零 capability 容器 / 带 capability 容器 ----
        var runs = new JsonObject();
        runs["control-无容器"] = RunChild(exeDir, cfgControl, Path.Combine(outDir, "res-control.json"), null, spawned, portInbound, inboundMarker);
        if (selfExempt) report["exemptListAfterControl"] = Sh("CheckNetIsolation.exe", "LoopbackExempt -s");
        runs["appcontainer-零capability"] = RunChild(exeDir, cfgA1, Path.Combine(outDir, "res-ac1.json"), ac1, spawned, portInbound, inboundMarker);
        if (selfExempt) report["exemptListAfterAc1"] = Sh("CheckNetIsolation.exe", "LoopbackExempt -s");
        runs["appcontainer-带网络capability"] = RunChild(exeDir, cfgA2, Path.Combine(outDir, "res-ac2.json"), ac2, spawned, portInbound, inboundMarker);
        if (selfExempt) report["exemptListAfterAc2"] = Sh("CheckNetIsolation.exe", "LoopbackExempt -s");
        report["runs"] = runs;

        // ---- 可选:回环豁免阶段(需管理员) ----
        if (exemptPhase)
        {
            var ex = new JsonObject();
            // ★ 先记进场时的列表。收尾只能在「进场时是空的」时用 -c,否则会连别人的豁免一起清掉。
            var snap = Sh("CheckNetIsolation.exe", "LoopbackExempt -s");
            ex["listOnEntry"] = snap;
            bool entryListEmpty = !((string)snap["stdout"] ?? "").Contains("SID:");
            ex["entryListEmpty"] = entryListEmpty;

            ex["add"] = Sh("CheckNetIsolation.exe", $"LoopbackExempt -a -n={Ac1Name}");
            ex["listAfterAdd"] = Sh("CheckNetIsolation.exe", "LoopbackExempt -s");
            Thread.Sleep(1500);
            // ★ 这一跑是全场重点:豁免只认「哪个 AppContainer」,命令行里没有端口/地址参数。
            //   所以为了让 worker 能连网关而开的豁免,会把 18081 一起放开。
            ex["afterExempt"] = RunChild(exeDir, cfgA1, Path.Combine(outDir, "res-ac1-exempt.json"), ac1, spawned);

            if (entryListEmpty)
            {
                ex["cleanup"] = Sh("CheckNetIsolation.exe", "LoopbackExempt -c");
                ex["listAfterCleanup"] = Sh("CheckNetIsolation.exe", "LoopbackExempt -s");
                Thread.Sleep(1000);
                ex["afterRemoval"] = RunChild(exeDir, cfgA1, Path.Combine(outDir, "res-ac1-unexempt.json"), ac1, spawned);
            }
            else
            {
                ex["cleanup"] = "★ 跳过 -c:进场时列表里已经有别的条目,清空会连别人的一起清掉。" +
                                $"请手动撤掉 {Ac1Name} 这一条。";
            }
            report["loopbackExemptPhase"] = ex;
        }

        // 管道服务端是后台线程,连接记录可能比主流程晚一点落地 —— 等到不再增长为止,
        // 否则会出现「日志里少一条」而看不出来是时序问题。
        int last = -1;
        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(500);
            lock (pipeLog) { if (pipeLog.Count == last) break; last = pipeLog.Count; }
        }
        report["pipeServerObservations"] = pipeLog;
        report["unixServerError"] = UnixServerError;
        report["spawnedPids"] = new JsonArray(spawned.Select(p => (JsonNode)p).ToArray());

        // ---- 清理 ----
        if (selfExempt)
        {
            // 恢复到进场时的状态。★ 只在实测确认过「进场时列表是空的」时才能用 -c。
            report["exemptListCleared"] = Sh("CheckNetIsolation.exe", "LoopbackExempt -c");
            report["exemptListAfterCleanup"] = Sh("CheckNetIsolation.exe", "LoopbackExempt -s");
            report["appIsoProbeValueRemoved"] = Sh("reg.exe",
                @"delete HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\RestrictedServices\AppIso\FirewallRules /v LocalAI-Spike-Probe /f");
        }
        if (!keep)
        {
            report["cleanup"] = new JsonObject
            {
                [Ac1Name] = Native.DeleteAppContainerProfile(Ac1Name),
                [Ac2Name] = Native.DeleteAppContainerProfile(Ac2Name),
            };
        }

        var reportPath = Path.Combine(outDir, "report.json");
        File.WriteAllText(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Summarize(report);
        Console.WriteLine();
        Console.WriteLine("完整报告: " + reportPath);
        return 0;
    }

    // =====================================================================
    //  AppContainer
    // =====================================================================

    private static string CreateProfile(string name, string[] capabilitySids)
    {
        Native.DeleteAppContainerProfile(name);   // 幂等:上一轮残留先清掉

        var capArray = IntPtr.Zero;
        var capPtrs = new List<IntPtr>();
        try
        {
            if (capabilitySids.Length > 0)
            {
                int stride = Marshal.SizeOf<Native.SID_AND_ATTRIBUTES>();
                capArray = Marshal.AllocHGlobal(stride * capabilitySids.Length);
                for (int i = 0; i < capabilitySids.Length; i++)
                {
                    if (!Native.ConvertStringSidToSidW(capabilitySids[i], out var psid))
                        throw new InvalidOperationException("ConvertStringSidToSid failed for " + capabilitySids[i]);
                    capPtrs.Add(psid);
                    Marshal.StructureToPtr(new Native.SID_AND_ATTRIBUTES { Sid = psid, Attributes = Native.SE_GROUP_ENABLED },
                                           capArray + i * stride, false);
                }
            }

            int hr = Native.CreateAppContainerProfile(name, name, "LocalAI 一次性勘察容器",
                                                      capArray, capabilitySids.Length, out var acSid);
            if (hr != 0)
            {
                Console.WriteLine($"  ✗ CreateAppContainerProfile({name}) HRESULT=0x{hr:X8}");
                if (Native.DeriveAppContainerSidFromAppContainerName(name, out acSid) != 0) return null;
            }
            var s = new SecurityIdentifier(acSid).Value;
            Native.FreeSid(acSid);
            return s;
        }
        finally
        {
            foreach (var p in capPtrs) Native.LocalFree(p);
            if (capArray != IntPtr.Zero) Marshal.FreeHGlobal(capArray);
        }
    }

    private static void GrantDir(string dir, string[] sids, FileSystemRights rights)
    {
        var di = new DirectoryInfo(dir);
        var acl = di.GetAccessControl();
        foreach (var sid in sids)
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), rights,
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(acl);
    }

    private static JsonObject RunChild(string exeDir, string cfg, string outPath, string acSid, List<int> spawned,
                                       int inboundPort = 0, string inboundMarker = null)
    {
        var o = new JsonObject { ["appContainerSid"] = acSid };
        if (inboundMarker != null) { try { File.Delete(inboundMarker); } catch { } }
        var exe = Path.Combine(exeDir, "AcSpike.exe");
        var cmdline = $"\"{exe}\" probe \"{cfg}\" \"{outPath}\"";

        // .NET 的 apphost 要找运行时。DOTNET_ROOT 由运行时目录反推,不写死路径。
        var rt = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var dotnetRoot = Path.GetFullPath(Path.Combine(rt, "..", "..", ".."));
        Environment.SetEnvironmentVariable("DOTNET_ROOT", dotnetRoot);

        var si = new Native.STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
        uint flags = Native.EXTENDED_STARTUPINFO_PRESENT | 0x08000000 /* CREATE_NO_WINDOW */;

        IntPtr attrList = IntPtr.Zero;
        IntPtr capsBlob = IntPtr.Zero;
        IntPtr acSidPtr = IntPtr.Zero;
        try
        {
            if (acSid != null)
            {
                IntPtr size = IntPtr.Zero;
                Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attrList = Marshal.AllocHGlobal(size);
                if (!Native.InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
                    throw new InvalidOperationException("InitializeProcThreadAttributeList " + Marshal.GetLastWin32Error());

                if (!Native.ConvertStringSidToSidW(acSid, out acSidPtr))
                    throw new InvalidOperationException("ConvertStringSidToSid " + Marshal.GetLastWin32Error());

                var sc = new Native.SECURITY_CAPABILITIES
                {
                    AppContainerSid = acSidPtr,
                    Capabilities = IntPtr.Zero,
                    CapabilityCount = 0,
                    Reserved = 0
                };
                capsBlob = Marshal.AllocHGlobal(Marshal.SizeOf<Native.SECURITY_CAPABILITIES>());
                Marshal.StructureToPtr(sc, capsBlob, false);

                if (!Native.UpdateProcThreadAttribute(attrList, 0, Native.PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES,
                        capsBlob, (IntPtr)Marshal.SizeOf<Native.SECURITY_CAPABILITIES>(), IntPtr.Zero, IntPtr.Zero))
                    throw new InvalidOperationException("UpdateProcThreadAttribute " + Marshal.GetLastWin32Error());

                si.lpAttributeList = attrList;
            }

            if (!Native.CreateProcessW(null, cmdline, IntPtr.Zero, IntPtr.Zero, false, flags,
                                        IntPtr.Zero, exeDir, ref si, out var pi))
            {
                o["createProcessError"] = Marshal.GetLastWin32Error();
                return o;
            }
            spawned.Add(pi.dwProcessId);
            o["pid"] = pi.dwProcessId;

            // 反方向:等子进程报「我在听了」,然后从宿主(非容器)连进去
            Task<JsonObject> inboundTask = null;
            if (inboundPort > 0 && inboundMarker != null)
                inboundTask = Task.Run(() =>
                {
                    var r = new JsonObject { ["port"] = inboundPort };
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 40_000 && !File.Exists(inboundMarker)) Thread.Sleep(150);
                    if (!File.Exists(inboundMarker)) { r["result"] = "子进程始终没报在听"; return r; }
                    try
                    {
                        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        var ar = s.BeginConnect(IPAddress.Loopback, inboundPort, null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(5000)) { r["result"] = "timeout"; return r; }
                        s.EndConnect(ar);
                        r["result"] = "connected";
                    }
                    catch (SocketException se)
                    {
                        r["result"] = "failed";
                        r["socketError"] = se.SocketErrorCode.ToString();
                        r["winsockCode"] = se.NativeErrorCode;
                    }
                    return r;
                });

            Native.WaitForSingleObject(pi.hProcess, 120_000);
            if (inboundTask != null) o["hostConnectingIntoChild"] = inboundTask.Result;
            Native.GetExitCodeProcess(pi.hProcess, out var code);
            o["exitCode"] = code;
            Native.CloseHandle(pi.hThread);
            Native.CloseHandle(pi.hProcess);

            if (File.Exists(outPath)) o["result"] = JsonNode.Parse(File.ReadAllText(outPath));
            else o["result"] = "(子进程没写出结果文件)";
        }
        catch (Exception ex) { o["exception"] = ex.Message; }
        finally
        {
            if (attrList != IntPtr.Zero) { Native.DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            if (capsBlob != IntPtr.Zero) Marshal.FreeHGlobal(capsBlob);
            if (acSidPtr != IntPtr.Zero) Native.LocalFree(acSidPtr);
        }
        return o;
    }

    // =====================================================================
    //  服务端:TCP / 命名管道 / AF_UNIX
    // =====================================================================

    private static void AcceptForever(TcpListener l) => new Thread(() =>
    {
        while (true)
        {
            try { using var c = l.AcceptTcpClient(); c.Client.Send(Encoding.UTF8.GetBytes("HELLO")); }
            catch { return; }
        }
    }) { IsBackground = true }.Start();

    private static string BuildSddl(string ownerSid, string[] acSids)
    {
        var sb = new StringBuilder("D:(A;;GA;;;SY)(A;;GA;;;BA)");
        sb.Append($"(A;;GA;;;{ownerSid})");
        foreach (var s in acSids) sb.Append($"(A;;GA;;;{s})");
        return sb.ToString();
    }

    private static void StartPipeServer(string name, string sddl, JsonArray log, List<int> spawned) => new Thread(() =>
    {
        while (true)
        {
            IntPtr sa = IntPtr.Zero, sd = IntPtr.Zero;
            IntPtr h;
            try
            {
                if (sddl != null)
                {
                    var raw = new RawSecurityDescriptor(sddl);
                    var bin = new byte[raw.BinaryLength];
                    raw.GetBinaryForm(bin, 0);
                    sd = Marshal.AllocHGlobal(bin.Length);
                    Marshal.Copy(bin, 0, sd, bin.Length);
                    var attrs = new Native.SECURITY_ATTRIBUTES
                    {
                        nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(),
                        lpSecurityDescriptor = sd,
                        bInheritHandle = false
                    };
                    sa = Marshal.AllocHGlobal(attrs.nLength);
                    Marshal.StructureToPtr(attrs, sa, false);
                }

                h = Native.CreateNamedPipeW(@"\\.\pipe\" + name,
                        Native.PIPE_ACCESS_DUPLEX, Native.PIPE_TYPE_BYTE | Native.PIPE_WAIT,
                        16, 4096, 4096, 0, sa);
                if (h == new IntPtr(-1))
                {
                    lock (log) log.Add(new JsonObject { ["pipe"] = name, ["createError"] = Marshal.GetLastWin32Error() });
                    return;
                }
            }
            finally
            {
                if (sa != IntPtr.Zero) Marshal.FreeHGlobal(sa);
                if (sd != IntPtr.Zero) Marshal.FreeHGlobal(sd);
            }

            if (!Native.ConnectNamedPipe(h, IntPtr.Zero) && Marshal.GetLastWin32Error() != 535 /* ALREADY_CONNECTED */)
            {
                Native.CloseHandle(h);
                continue;
            }

            var entry = new JsonObject { ["pipe"] = name };
            try
            {
                var buf = new byte[512];
                if (Native.ReadFile(h, buf, buf.Length, out int read, IntPtr.Zero) && read > 0)
                    entry["clientSaid"] = Encoding.UTF8.GetString(buf, 0, read);

                // ★ D73 要的第一半:父子 PID
                if (Native.GetNamedPipeClientProcessId(h, out uint cpid))
                {
                    entry["clientPid"] = cpid;
                    entry["clientParentPid"] = ParentPid((int)cpid);
                    entry["clientIsDirectChildOfMe"] = ParentPid((int)cpid) == Environment.ProcessId;
                    lock (spawned) entry["clientIsOneOfMySpawns"] = spawned.Contains((int)cpid);
                }

                // ★ D73 要的第二半:SID
                if (Native.ImpersonateNamedPipeClient(h))
                {
                    try
                    {
                        var t = Native.DescribeThreadToken();
                        entry["clientUserSid"] = t.UserSid;
                        entry["clientUserName"] = t.UserName;
                        entry["clientIsAppContainer"] = t.IsAppContainer;
                        entry["clientAppContainerSid"] = t.AppContainerSid;
                        entry["clientIntegrity"] = t.IntegrityLabel;
                    }
                    catch (Exception ex) { entry["impersonateInspectError"] = ex.Message; }
                    finally { Native.RevertToSelf(); }
                }
                else entry["impersonateError"] = Marshal.GetLastWin32Error();

                var reply = Encoding.UTF8.GetBytes("server saw pid=" + entry["clientPid"] +
                                                   " appcontainer=" + entry["clientIsAppContainer"]);
                Native.WriteFile(h, reply, reply.Length, out _, IntPtr.Zero);
            }
            catch (Exception ex) { entry["serverError"] = ex.Message; }
            finally
            {
                lock (log) log.Add(entry);
                Native.DisconnectNamedPipe(h);
                Native.CloseHandle(h);
            }
        }
    }) { IsBackground = true }.Start();

    internal static string UnixServerError = null;

    private static void StartUnixServer(string path) => new Thread(() =>
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        try
        {
            using var srv = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            srv.Bind(new UnixDomainSocketEndPoint(path));
            srv.Listen(8);
            while (true)
            {
                using var c = srv.Accept();
                var buf = new byte[256];
                c.ReceiveTimeout = 2000;
                try { c.Receive(buf); } catch { }
                c.Send(Encoding.UTF8.GetBytes("UNIX-HELLO"));
            }
        }
        catch (Exception ex) { UnixServerError = ex.GetType().Name + ": " + ex.Message; }
    }) { IsBackground = true }.Start();

    // =====================================================================
    //  「普通用户能不能自己开豁免」—— 造一个 Medium / Administrators=deny-only 的 token 去试
    // =====================================================================

    /// <summary>
    /// 造一个「普通用户等效」的 token(Administrators 变只用于拒绝 + 剥掉全部特权 + 完整性降到 Medium),
    /// 用它去跑调用方给的那个 .cmd。用来回答:普通用户能不能自己打开回环豁免。
    /// </summary>
    private static int MediumProbe(string cmdPath)
    {
        if (cmdPath == null) { Console.WriteLine("用法: AcSpike medium-probe <要在受限 token 下跑的 .cmd 路径>"); return 2; }

        var me = Native.DescribeCurrentToken();
        Banner($"medium-probe · 当前 integrity={me.IntegrityLabel} · Administrators={me.AdministratorsState}");

        const uint TOKEN_ALL_ACCESS = 0xF01FF;
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), TOKEN_ALL_ACCESS, out var hMe))
        { Console.WriteLine("OpenProcessToken failed " + Marshal.GetLastWin32Error()); return 1; }

        Native.ConvertStringSidToSidW("S-1-5-32-544", out var adminSid);
        var disable = new[] { new Native.SID_AND_ATTRIBUTES { Sid = adminSid, Attributes = 0 } };
        // ★ 不用 DISABLE_MAX_PRIVILEGE:它会把 SeChangeNotifyPrivilege 一起剥掉,
        //   于是新进程连 DLL 都加载不起来(实测退出码 0xC0000142 STATUS_DLL_INIT_FAILED)。
        //   UAC 真正的过滤 token 是**保留** SeChangeNotifyPrivilege 的;
        //   决定 DACL 结果的是「Administrators 只用于拒绝」那一条,那才是要复现的东西。
        if (!Native.CreateRestrictedToken(hMe, 0, 1, disable, 0, IntPtr.Zero, 0, IntPtr.Zero,
                                          out var hRestricted))
        { Console.WriteLine("CreateRestrictedToken failed " + Marshal.GetLastWin32Error()); return 1; }

        // ★ CreateRestrictedToken 返回的句柄权限不够改完整性标签(上一版就死在这里,报 5)。
        //   先 Duplicate 出一个 TOKEN_ALL_ACCESS 的主令牌。
        if (!Native.DuplicateTokenEx(hRestricted, TOKEN_ALL_ACCESS, IntPtr.Zero,
                                     2 /*SecurityImpersonation*/, 1 /*TokenPrimary*/, out var hFull))
        { Console.WriteLine("DuplicateTokenEx failed " + Marshal.GetLastWin32Error()); return 1; }

        Native.ConvertStringSidToSidW("S-1-16-8192", out var medSid);
        var lbl = new Native.TOKEN_MANDATORY_LABEL
        { Label = new Native.SID_AND_ATTRIBUTES { Sid = medSid, Attributes = 0x20 /*SE_GROUP_INTEGRITY*/ } };
        var lblPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Native.TOKEN_MANDATORY_LABEL>());
        Marshal.StructureToPtr(lbl, lblPtr, false);
        bool loweredIntegrity = Native.SetTokenInformation(hFull, Native.TokenIntegrityLevel, lblPtr,
                                                           Marshal.SizeOf<Native.TOKEN_MANDATORY_LABEL>());
        Console.WriteLine(loweredIntegrity
            ? "  完整性已降到 Medium ✓"
            : "  ⚠ 降完整性失败 " + Marshal.GetLastWin32Error() + " —— 本轮只是「Administrators 只用于拒绝」,不是完整的普通用户");

        var si = new Native.STARTUPINFO();
        si.cb = Marshal.SizeOf<Native.STARTUPINFO>();
        // ★ 不给 lpDesktop 时,降级后的进程拿不到窗口站/桌面,启动即 0xC0000142(DLL_INIT_FAILED)。
        si.lpDesktop = @"winsta0\default";
        var cmd = $"cmd.exe /c \"{cmdPath}\"";
        Console.WriteLine("  跑: " + cmd);

        // ① 先用 CreateProcessAsUserW —— 受限 token 是自己主令牌的派生,不需要 SeAssignPrimaryToken。
        Native.PROCESS_INFORMATION pi;
        bool started = Native.CreateProcessAsUserW(hFull, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                                                  Native.CREATE_NEW_CONSOLE, IntPtr.Zero, null, ref si, out pi);
        if (started) Console.WriteLine("  起法: CreateProcessAsUserW ✓");
        else
        {
            Console.WriteLine($"  CreateProcessAsUserW 失败 Win32={Marshal.GetLastWin32Error()},退回 CreateProcessWithTokenW");
            // ② 退回旧路(本机一律 0xC0000142,留着是为了让失败可见而不是静默)
            started = Native.CreateProcessWithTokenW(hFull, 0, null, cmd,
                                                     Native.CREATE_NEW_CONSOLE, IntPtr.Zero, null, ref si, out pi);
            if (!started)
            {
                Console.WriteLine("  CreateProcessWithTokenW 也失败 Win32=" + Marshal.GetLastWin32Error());
                return 1;
            }
        }
        Native.WaitForSingleObject(pi.hProcess, 180_000);
        Native.GetExitCodeProcess(pi.hProcess, out var code);
        Console.WriteLine("  受限进程退出码 = " + code);
        Native.CloseHandle(pi.hThread); Native.CloseHandle(pi.hProcess);
        return 0;
    }

    /// <summary>
    /// ★ 这个模式**必须由用户在自己的上下文里双击运行**(D46:agent 的上下文可能被提权,
    /// 提权上下文下测出来的「能/不能」不代表普通用户)。
    /// 它回答一件事:普通用户不经 UAC,能不能自己把一个 AppContainer 加进回环豁免列表。
    /// 判据看**效果**(加完再读一次列表),不看退出码。
    /// </summary>
    private static int UserExemptProbe()
    {
        var me = Native.DescribeCurrentToken();
        Banner("普通用户能不能自己打开回环豁免");
        Console.WriteLine($"  身份        : {me.UserName}");
        Console.WriteLine($"  完整性等级  : {me.IntegrityLabel}   ({me.IntegritySid})");
        Console.WriteLine($"  Administrators: {me.AdministratorsState}");
        Console.WriteLine();
        if (me.IntegrityLabel == "High")
            Console.WriteLine("  ⚠⚠ 这是【提权】上下文 —— 本次结果**不能**用来回答「普通用户能不能」。\n" +
                              "      请在资源管理器里双击本 .cmd(不要用「以管理员身份运行」),再跑一次。\n");

        var sid = CreateProfile(Ac1Name, Array.Empty<string>());
        Console.WriteLine($"  建 AppContainer profile(本身不需要管理员)= {sid ?? "失败"}");
        if (sid == null) return 1;

        try
        {
            var before = Sh("CheckNetIsolation.exe", "LoopbackExempt -s");
            var beforeOut = (string)before["stdout"] ?? "";
            bool beforeEmpty = !beforeOut.Contains("SID:");
            bool beforeHasOurs = beforeOut.Contains(sid, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  加之前:列表是否为空={beforeEmpty} · 是否已含本容器={beforeHasOurs}");
            Console.WriteLine($"  【读】LoopbackExempt -s  exit={before["exitCode"]}");

            var add = Sh("CheckNetIsolation.exe", $"LoopbackExempt -a -n={Ac1Name}");
            Console.WriteLine($"  【写】LoopbackExempt -a  exit={add["exitCode"]}  输出=\"{add["stdout"]}\" {add["stderr"]}");

            var after = Sh("CheckNetIsolation.exe", "LoopbackExempt -s");
            bool afterHasOurs = ((string)after["stdout"] ?? "").Contains(sid, StringComparison.OrdinalIgnoreCase);

            Console.WriteLine();
            Console.WriteLine("  ── 结论(看效果,不看退出码)──");
            if (afterHasOurs && !beforeHasOurs)
                Console.WriteLine("  ★★ 普通用户【可以】自己打开回环豁免 —— 这道隔离对机主是纸的。");
            else if (!afterHasOurs)
                Console.WriteLine("  ★★ 普通用户【不能】打开回环豁免 —— 这道隔离需要一次提权才能拆掉。");
            else
                Console.WriteLine("  ?? 加之前列表里就已经有这一条了,这一轮判不出来。先清掉它再重跑。");

            // ---- 收尾:恢复进场时的状态 ----
            Console.WriteLine();
            if (afterHasOurs && !beforeHasOurs)
            {
                // ★ 先试 -d 删单条。实测它是好的 —— 但**必须用干净的命令行传参**:
                //   从 PowerShell 里直接敲 `CheckNetIsolation LoopbackExempt -d -n=X` 会被
                //   PowerShell 的参数解析弄坏,报「参数无效」,看起来像 Windows 不支持单条删除。
                //   -c(清空整张表)只作为兜底,且只在「进场时表是空的」时才敢用,
                //   否则会连别人的豁免一起清掉。
                var d1 = Sh("CheckNetIsolation.exe", $"LoopbackExempt -d -n={Ac1Name}");
                var stillThere = ((string)Sh("CheckNetIsolation.exe", "LoopbackExempt -s")["stdout"] ?? "")
                                 .Contains(sid, StringComparison.OrdinalIgnoreCase);
                Console.WriteLine($"  收尾:-d 单条删除 exit={d1["exitCode"]} \"{d1["stdout"]}\" → 还在吗={stillThere}");
                if (stillThere && beforeEmpty)
                {
                    Sh("CheckNetIsolation.exe", "LoopbackExempt -c");
                    Console.WriteLine("  收尾:进场时表是空的 ⇒ 用 -c 清空,已回到原状。");
                }
                else if (stillThere)
                {
                    Console.WriteLine("  ⚠ 收尾失败:进场时表里还有别的条目,不能用 -c(会清掉别人的)。\n" +
                                      $"    请手动撤掉这一条:{Ac1Name} / {sid}");
                }
            }
            else Console.WriteLine("  收尾:没有新增任何豁免,无需恢复。");
        }
        finally
        {
            Console.WriteLine($"  收尾:删掉勘察容器 HRESULT={Native.DeleteAppContainerProfile(Ac1Name)}");
        }
        return 0;
    }

    private static int ProfileAdd()
    {
        var me = Native.DescribeCurrentToken();
        var sid = CreateProfile(Ac1Name, Array.Empty<string>());
        Console.WriteLine($"integrity={me.IntegrityLabel} administrators={me.AdministratorsState}");
        Console.WriteLine($"CreateAppContainerProfile({Ac1Name}) = {sid ?? "失败"}");
        return sid == null ? 1 : 0;
    }

    private static int ProfileDel()
    {
        Console.WriteLine($"DeleteAppContainerProfile({Ac1Name}) HRESULT = {Native.DeleteAppContainerProfile(Ac1Name)}");
        Console.WriteLine($"DeleteAppContainerProfile({Ac2Name}) HRESULT = {Native.DeleteAppContainerProfile(Ac2Name)}");
        return 0;
    }

    /// <summary>
    /// AF_UNIX 到底是本机不支持,还是我这个测法有问题。
    /// ★ bind 单独测过是成功的 —— 所以这里必须把 **connect** 也测掉:
    ///   同进程内 bind + listen + accept + connect 全走一遍。
    ///   分不清「bind 成功但 connect 失败」时,不能说 AF_UNIX「未测」。
    /// </summary>
    private static int UnixCheck()
    {
        // ★ 矩阵:把「路径长度」与「路径里有空格」两个变量分开测。
        //   前一轮 3 条路径同时变了这两样,分不出是哪一个 —— 那样的数据不能当判据。
        var winTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        var spaceDir = Path.Combine(winTemp, "a b");
        Directory.CreateDirectory(spaceDir);
        var candidates = new List<string>
        {
            Path.Combine(winTemp, "u_short.sock"),                                  // 短 · 无空格
            Path.Combine(winTemp, new string('x', 40) + ".sock"),                   // 长 · 无空格
            Path.Combine(spaceDir, "u.sock"),                                       // 短 · 有空格
            Path.Combine(spaceDir, new string('y', 40) + ".sock"),                  // 长 · 有空格
            Path.Combine(Path.GetTempPath(), "u_usertemp.sock"),                    // 机主 Temp
        };
        // 额外目录由调用方给(如 {state} / {cache}\tmp —— B′ 真正会放 socket 的地方),
        // 分号分隔的**目录**列表;代码里不写死盘符(§11.1)。
        var extraDirs = Environment.GetEnvironmentVariable("LOCALAI_UNIX_DIRS");
        if (!string.IsNullOrEmpty(extraDirs))
            foreach (var d in extraDirs.Split(';', StringSplitOptions.RemoveEmptyEntries))
                candidates.Add(Path.Combine(d.Trim(), "u_probe.sock"));
        foreach (var p in candidates)
        {
            Console.WriteLine($"— 长度 {p.Length,3} · 含空格 {(p.Contains(' ') ? "是" : "否")} · {p}");
            try { Directory.CreateDirectory(Path.GetDirectoryName(p)); File.Delete(p); } catch { }
            Socket srv = null;
            try
            {
                srv = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                srv.Bind(new UnixDomainSocketEndPoint(p));
                srv.Listen(4);
                Console.WriteLine($"  BIND ok    len={p.Length,3}  {p}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  BIND FAIL  {Describe(ex)} len={p.Length,3}  {p}");
                srv?.Dispose();
                continue;
            }

            var accepted = new ManualResetEventSlim(false);
            string acceptErr = null;
            new Thread(() =>
            {
                try { using var c = srv.Accept(); c.Send(Encoding.UTF8.GetBytes("U-HELLO")); }
                catch (Exception ex) { acceptErr = Describe(ex); }
                finally { accepted.Set(); }
            }) { IsBackground = true }.Start();

            try
            {
                using var cli = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                cli.Connect(new UnixDomainSocketEndPoint(p));
                var buf = new byte[64];
                cli.ReceiveTimeout = 3000;
                int n = cli.Receive(buf);
                Console.WriteLine($"  CONNECT ok  收到 \"{Encoding.UTF8.GetString(buf, 0, n)}\"");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  CONNECT FAIL {Describe(ex)}");
            }
            accepted.Wait(2000);
            if (acceptErr != null) Console.WriteLine($"  ACCEPT 侧报错 {acceptErr}");

            srv.Dispose();
            try { File.Delete(p); Console.WriteLine("  socket 文件已删掉"); }
            catch (Exception ex) { Console.WriteLine($"  ★ socket 文件删不掉:{ex.GetType().Name}(退出后会成为残留)"); }
            Console.WriteLine();
        }
        return 0;

        static string Describe(Exception ex) => ex is SocketException se
            ? $"{se.SocketErrorCode}({se.NativeErrorCode})"
            : $"{ex.GetType().Name}: {ex.Message}";
    }

    private static void Banner(string s)
    {
        Console.WriteLine();
        Console.WriteLine("──────────────────────────────────────────────────────────────");
        Console.WriteLine(s);
        Console.WriteLine("──────────────────────────────────────────────────────────────");
    }

    private static int? FirstFreePort(int from, int to)
    {
        for (int p = from; p <= to; p++)
        {
            try { var l = new TcpListener(IPAddress.Loopback, p); l.Start(); l.Stop(); return p; }
            catch { }
        }
        return null;
    }

    private static string LanIPv4()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("10.255.255.255", 1);   // 不发包,只让路由表选出本机出口地址
            return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
        }
        catch { return null; }
    }

    private static int ParentPid(int pid)
    {
        var snap = Native.CreateToolhelp32Snapshot(0x00000002 /*TH32CS_SNAPPROCESS*/, 0);
        if (snap == new IntPtr(-1)) return -1;
        try
        {
            var pe = new Native.PROCESSENTRY32 { dwSize = Marshal.SizeOf<Native.PROCESSENTRY32>() };
            if (!Native.Process32FirstW(snap, ref pe)) return -1;
            do { if (pe.th32ProcessID == pid) return (int)pe.th32ParentProcessID; }
            while (Native.Process32NextW(snap, ref pe));
        }
        finally { Native.CloseHandle(snap); }
        return -1;
    }

    private static JsonObject Sh(string exe, string args)
    {
        var o = new JsonObject { ["cmd"] = exe + " " + args };
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            o["stdout"] = p.StandardOutput.ReadToEnd().Trim();
            o["stderr"] = p.StandardError.ReadToEnd().Trim();
            p.WaitForExit(30000);
            o["exitCode"] = p.ExitCode;
        }
        catch (Exception ex) { o["exception"] = ex.Message; }
        return o;
    }

    private record Target(string Name, string Path);

    private static List<Target> FileTargets()
    {
        // 路径全部运行期推导 —— 代码里不写死盘符(§11.1 路径契约)
        var list = new List<Target>();
        list.Add(new Target("机主 profile 根", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        list.Add(new Target("机主 Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        list.Add(new Target("LOCALAPPDATA", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        list.Add(new Target("LOCALAPPDATA\\LocalAI",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAI")));

        // 其余目标由调用方用 LOCALAI_PROBE 传进来:"名字=路径;名字=路径"
        // —— 代码里不写死任何盘符(§11.1 路径契约),真实路径由 config/paths.toml 的持有者给。
        var extra = Environment.GetEnvironmentVariable("LOCALAI_PROBE");
        if (!string.IsNullOrEmpty(extra))
            foreach (var pair in extra.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = pair.IndexOf('=');
                if (i > 0) list.Add(new Target(pair[..i].Trim(), pair[(i + 1)..].Trim()));
            }

        list.Add(new Target("System32(对照:应可读)", Environment.SystemDirectory));
        return list;
    }

    private static void Summarize(JsonObject report)
    {
        Banner("摘要");
        var runs = report["runs"].AsObject();
        foreach (var (name, run) in runs)
        {
            var res = run["result"] as JsonObject;
            Console.WriteLine();
            Console.WriteLine("### " + name);
            if (res == null) { Console.WriteLine("  (无结果) " + run.ToJsonString()); continue; }
            var t = res["token"] as JsonObject;
            if (t != null)
                Console.WriteLine($"  token: appcontainer={t["isAppContainer"]} integrity={t["integrity"]} admins={t["administrators"]} acSid={t["appContainerSid"]}");
            foreach (var p in res["tcp"]?.AsArray() ?? new JsonArray())
                Console.WriteLine($"  TCP  {p["name"],-22} {p["target"],-24} -> {p["result"]} {p["socketError"]} ({p["winsockCode"]})");
            foreach (var p in res["pipes"]?.AsArray() ?? new JsonArray())
                Console.WriteLine($"  PIPE {p["name"],-22} -> {p["result"]} {p["win32Name"]}");
            if (res["unixSocket"] is JsonObject u)
                Console.WriteLine($"  UNIX {"AF_UNIX",-22} -> {u["result"]} {u["socketError"]}");
            if (res["selfExempt"] is JsonObject se)
            {
                Console.WriteLine($"  ★ 自己给自己开豁免: CheckNetIsolation exit={se["CheckNetIsolation"]?["exitCode"]} out=\"{se["CheckNetIsolation"]?["stdout"]}\" " +
                                  $"launchFailed={se["CheckNetIsolation"]?["launchFailed"]}");
                Console.WriteLine($"     reg add AppIso exit={se["reg-add-AppIso"]?["exitCode"]} err=\"{se["reg-add-AppIso"]?["stderr"]}\" launchFailed={se["reg-add-AppIso"]?["launchFailed"]}");
                Console.WriteLine($"     开豁免前 -> {se["beforeRetry"]?["result"]} {se["beforeRetry"]?["socketError"]} · 开豁免后 -> {se["afterRetry"]?["result"]} {se["afterRetry"]?["socketError"]}");
            }
            if (res["inbound"] is JsonObject inb)
                Console.WriteLine($"  IN   {"容器内监听回环",-20} -> bind={inb["bind"]} {inb["socketError"]} · 收到宿主连接={inb["acceptedInboundConnection"]}");
            if (run["hostConnectingIntoChild"] is JsonObject hc)
                Console.WriteLine($"  IN   {"宿主连进子进程",-20} -> {hc["result"]} {hc["socketError"]} ({hc["winsockCode"]})");
            foreach (var p in res["files"]?.AsArray() ?? new JsonArray())
                Console.WriteLine($"  FILE {p["name"],-22} -> {p["result"]}");
            if (res["grandchild"] is JsonObject g)
            {
                var gt = (g["result"] as JsonObject)?["token"] as JsonObject;
                Console.WriteLine($"  孙进程 started={g["started"]} pid={g["pid"]} exit={g["exitCode"]}" +
                                  (gt != null ? $" · appcontainer={gt["isAppContainer"]} acSid={gt["appContainerSid"]}" : " · (无 token 信息)"));
                var gtcp = (g["result"] as JsonObject)?["tcp"]?.AsArray();
                if (gtcp != null)
                    foreach (var p in gtcp)
                        Console.WriteLine($"    孙 TCP {p["name"],-20} -> {p["result"]} {p["socketError"]} ({p["winsockCode"]})");
            }
        }

        Console.WriteLine();
        Console.WriteLine("### 管道服务端看到的对端");
        foreach (var e in report["pipeServerObservations"].AsArray())
            Console.WriteLine("  " + e.ToJsonString());
    }
}
