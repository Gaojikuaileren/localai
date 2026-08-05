using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AcSpike;

/// <summary>
/// 被投进 AppContainer 里跑的那一半。它只观测,不改任何系统状态。
/// 结果写成 JSON 落到 --out 指定的文件(该目录已被 launcher 授权给 AppContainer SID 可写)。
/// </summary>
internal static class Probe
{
    internal static int Run(string cfgPath, string outPath)
    {
        var res = new JsonObject();
        try
        {
            var cfg = JsonNode.Parse(File.ReadAllText(cfgPath)).AsObject();

            // ① 我到底跑在什么 token 上 —— 这一条比后面所有结果都重要
            try
            {
                var t = Native.DescribeCurrentToken();
                res["token"] = new JsonObject
                {
                    ["userName"] = t.UserName,
                    ["userSid"] = t.UserSid,
                    ["isAppContainer"] = t.IsAppContainer,
                    ["appContainerSid"] = t.AppContainerSid,
                    ["integrity"] = t.IntegrityLabel,
                    ["integritySid"] = t.IntegritySid,
                    ["administrators"] = t.AdministratorsState,
                    ["capabilitySids"] = new JsonArray(t.CapabilitySids.Select(s => (JsonNode)s).ToArray()),
                    ["pid"] = Environment.ProcessId,
                };
            }
            catch (Exception ex) { res["tokenError"] = ex.ToString(); }

            // ② TCP —— 每一格都要能区分「被拒」与「没人听」
            var tcp = new JsonArray();
            foreach (var probe in cfg["tcp"].AsArray())
            {
                var o = probe.AsObject();
                int tmo = o["timeoutMs"] is JsonNode n ? (int)n : 4000;
                tcp.Add(TcpProbe((string)o["name"], (string)o["host"], (int)o["port"], tmo));
            }
            res["tcp"] = tcp;

            // ②b 反方向:容器里的进程能不能【监听】回环、宿主能不能连进来
            if (cfg["inbound"] is JsonObject inb)
                res["inbound"] = InboundProbe((int)inb["port"], (string)inb["marker"], (int)inb["holdMs"]);

            // ③ 命名管道 —— 待决 7 的可行性
            var pipes = new JsonArray();
            foreach (var probe in cfg["pipes"].AsArray())
            {
                var o = probe.AsObject();
                pipes.Add(PipeProbe((string)o["name"], (string)o["pipe"]));
            }
            res["pipes"] = pipes;

            // ④ AF_UNIX —— 管道之外的另一条本机传输(也是一条可能的旁路)
            if (cfg["unixSocket"] is JsonNode us && us.GetValue<string>() is string usPath && usPath.Length > 0)
                res["unixSocket"] = UnixProbe(usPath);

            // ⑤ 文件侧 —— AppContainer 自己挡不挡得住读机主数据
            var files = new JsonArray();
            foreach (var probe in cfg["files"].AsArray())
            {
                var o = probe.AsObject();
                files.Add(FileProbe((string)o["name"], (string)o["path"]));
            }
            res["files"] = files;

            // ⑤b ★★ 真正承重的一问:被关起来的进程能不能【自己给自己】开回环豁免?
            //     能 ⇒ 这道隔离对它本人无效,路线 C 不是遏制边界。
            if (cfg["selfExempt"] is JsonObject se)
                res["selfExempt"] = SelfExempt((string)se["profileName"], (string)se["host"], (int)se["port"]);

            // ⑥ 逃逸测试:容器里的进程再拉一个子进程,子进程还在容器里吗?
            if (cfg["grandchild"] is JsonObject gc)
                res["grandchild"] = SpawnGrandchild((string)gc["exe"], (string)gc["cfg"], (string)gc["out"]);
        }
        catch (Exception ex)
        {
            res["fatal"] = ex.ToString();
        }

        File.WriteAllText(outPath, res.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static JsonObject SelfExempt(string profileName, string host, int port)
    {
        var o = new JsonObject { ["profileName"] = profileName };
        o["beforeRetry"] = TcpProbe("自己开豁免之前", host, port, 4000);
        foreach (var (label, exe, args) in new[]
                 {
                     ("CheckNetIsolation", "CheckNetIsolation.exe", $"LoopbackExempt -a -n={profileName}"),
                     ("reg-add-AppIso", "reg.exe",
                      @"add HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\RestrictedServices\AppIso\FirewallRules /v LocalAI-Spike-Probe /t REG_SZ /d x /f"),
                 })
        {
            var r = new JsonObject();
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var oem = Native.OemEncodingOrNull();
                if (oem != null) { psi.StandardOutputEncoding = oem; psi.StandardErrorEncoding = oem; }
                using var p = Process.Start(psi);
                r["stdout"] = p.StandardOutput.ReadToEnd().Trim();
                r["stderr"] = p.StandardError.ReadToEnd().Trim();
                p.WaitForExit(30000);
                r["exitCode"] = p.HasExited ? p.ExitCode : -1;
            }
            catch (Exception ex) { r["launchFailed"] = ex.GetType().Name + ": " + ex.Message; }
            o[label] = r;
        }
        Thread.Sleep(2000);
        o["afterRetry"] = TcpProbe("自己开豁免之后", host, port, 8000);
        return o;
    }

    private static JsonObject InboundProbe(int port, string marker, int holdMs)
    {
        var o = new JsonObject { ["port"] = port };
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            o["bind"] = "ok";
            File.WriteAllText(marker, port.ToString());
            var sw = Stopwatch.StartNew();
            bool got = false;
            while (sw.ElapsedMilliseconds < holdMs)
            {
                if (l.Pending()) { using var c = l.AcceptTcpClient(); got = true; break; }
                Thread.Sleep(100);
            }
            o["acceptedInboundConnection"] = got;
            l.Stop();
        }
        catch (SocketException se)
        {
            o["bind"] = "failed";
            o["socketError"] = se.SocketErrorCode.ToString();
            o["winsockCode"] = se.NativeErrorCode;
            o["message"] = se.Message;
        }
        catch (Exception ex) { o["bind"] = "failed"; o["exception"] = ex.GetType().Name; o["message"] = ex.Message; }
        return o;
    }

    private static JsonObject TcpProbe(string name, string host, int port, int timeoutMs)
    {
        var o = new JsonObject { ["name"] = name, ["target"] = host + ":" + port, ["timeoutMs"] = timeoutMs };
        var sw = Stopwatch.StartNew();
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var ar = s.BeginConnect(IPAddress.Parse(host), port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                o["result"] = "timeout";
                o["ms"] = sw.ElapsedMilliseconds;
                return o;
            }
            s.EndConnect(ar);
            o["result"] = "connected";
        }
        catch (SocketException se)
        {
            o["result"] = "failed";
            o["socketError"] = se.SocketErrorCode.ToString();
            o["winsockCode"] = se.NativeErrorCode;
            o["message"] = se.Message;
        }
        catch (Exception ex)
        {
            o["result"] = "failed";
            o["exception"] = ex.GetType().Name;
            o["message"] = ex.Message;
        }
        o["ms"] = sw.ElapsedMilliseconds;
        return o;
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;

    private static JsonObject PipeProbe(string name, string pipeName)
    {
        var o = new JsonObject { ["name"] = name, ["pipe"] = @"\\.\pipe\" + pipeName };
        // 用 CreateFileW 直接开,是为了拿到未经封装的 Win32 错误码
        // (.NET 的 NamedPipeClientStream 会把 5 和 231 都翻成异常,分不出来)
        IntPtr h = Native.CreateFileW(@"\\.\pipe\" + pipeName, GENERIC_READ | GENERIC_WRITE,
                                      0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            o["result"] = "failed";
            o["win32Code"] = err;
            o["win32Name"] = err switch
            {
                2 => "ERROR_FILE_NOT_FOUND",
                5 => "ERROR_ACCESS_DENIED",
                231 => "ERROR_PIPE_BUSY",
                _ => "ERROR_" + err
            };
            return o;
        }
        try
        {
            var msg = Encoding.UTF8.GetBytes("PING from pid " + Environment.ProcessId);
            if (!Native.WriteFile(h, msg, msg.Length, out int written, IntPtr.Zero))
            {
                o["result"] = "opened-but-write-failed";
                o["win32Code"] = Marshal.GetLastWin32Error();
                return o;
            }
            var buf = new byte[256];
            if (Native.ReadFile(h, buf, buf.Length, out int read, IntPtr.Zero) && read > 0)
            {
                o["result"] = "roundtrip-ok";
                o["serverReply"] = Encoding.UTF8.GetString(buf, 0, read);
            }
            else
            {
                o["result"] = "opened-write-ok-no-reply";
                o["win32Code"] = Marshal.GetLastWin32Error();
            }
        }
        finally { Native.CloseHandle(h); }
        return o;
    }

    private static JsonObject UnixProbe(string path)
    {
        var o = new JsonObject { ["path"] = path };
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(path));
            var msg = Encoding.UTF8.GetBytes("PING-UNIX from pid " + Environment.ProcessId);
            s.Send(msg);
            var buf = new byte[256];
            s.ReceiveTimeout = 3000;
            int n = s.Receive(buf);
            o["result"] = "roundtrip-ok";
            o["serverReply"] = Encoding.UTF8.GetString(buf, 0, n);
        }
        catch (SocketException se)
        {
            o["result"] = "failed";
            o["socketError"] = se.SocketErrorCode.ToString();
            o["winsockCode"] = se.NativeErrorCode;
            o["message"] = se.Message;
        }
        catch (Exception ex)
        {
            o["result"] = "failed";
            o["exception"] = ex.GetType().Name;
            o["message"] = ex.Message;
        }
        return o;
    }

    private static JsonObject FileProbe(string name, string path)
    {
        var o = new JsonObject { ["name"] = name, ["path"] = path };
        // ★ 不能只信 Directory.Exists —— 它在「拒绝访问」时也返回 false,
        //   于是「被挡住」会被记成「不存在」。一定要真开一次,把异常类型记下来。
        o["directoryExists"] = Directory.Exists(path);
        o["fileExists"] = File.Exists(path);
        try
        {
            var entries = Directory.GetFileSystemEntries(path);
            o["result"] = "dir-listed";
            o["entryCount"] = entries.Length;
            return o;
        }
        catch (UnauthorizedAccessException ex)
        {
            o["result"] = "denied";
            o["exception"] = nameof(UnauthorizedAccessException);
            o["message"] = ex.Message;
            return o;
        }
        catch (DirectoryNotFoundException)
        {
            // 可能是文件而不是目录 —— 再按文件试一次
        }
        catch (Exception ex)
        {
            o["result"] = "failed";
            o["exception"] = ex.GetType().Name;
            o["hresult"] = ex.HResult;
            o["message"] = ex.Message;
            return o;
        }

        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[16];
            o["result"] = "file-read";
            o["bytesRead"] = fs.Read(buf, 0, buf.Length);
        }
        catch (Exception ex)
        {
            o["result"] = ex is UnauthorizedAccessException ? "denied" : "not-found";
            o["exception"] = ex.GetType().Name;
            o["message"] = ex.Message;
        }
        return o;
    }

    private static JsonObject SpawnGrandchild(string exe, string cfg, string outPath)
    {
        var o = new JsonObject();
        try
        {
            // ★ 先删。上一轮的结果文件留在原地时,「孙进程还没写完」会被读成
            //   「上一轮的答案」—— 一次读到旧快照就足以把结论整条弄反。
            try { File.Delete(outPath); } catch { }

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("probe");
            psi.ArgumentList.Add(cfg);
            psi.ArgumentList.Add(outPath);
            using var p = Process.Start(psi);
            o["started"] = true;
            o["pid"] = p.Id;
            o["exited"] = p.WaitForExit(120_000);
            if (p.HasExited) o["exitCode"] = p.ExitCode;
            if (!o["exited"].GetValue<bool>()) { o["result"] = "(孙进程超时未退出,结果不采信)"; return o; }
            o["result"] = File.Exists(outPath)
                ? JsonNode.Parse(File.ReadAllText(outPath))
                : "(孙进程没写出结果文件)";
        }
        catch (Exception ex)
        {
            o["started"] = false;
            o["exception"] = ex.GetType().Name;
            o["message"] = ex.Message;
        }
        return o;
    }
}
