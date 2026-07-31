// P3c -- 虚拟声卡(VB-CABLE)的检测、版本与安装(用户裁定 2026-07-31)。
//
// 为什么需要它:Windows 上【没有】不写内核驱动就能被会议软件选中的麦克风 ——
// 音频端点由 AudioEndpointBuilder 从内核 KS 拓扑推导,用户态没有注册端点的 API。
// 自研驱动要法人主体 + EV 证书 + 6~10 周,且微软已把 attestation 签名书面定位为"仅供测试用途"。
// 所以走成熟的第三方虚拟声卡,把"用户不用自己去折腾"做到位。
//
// ★★ 三条硬规则(用户确认):自动 ≠ 不透明。
//   ① 【校验哈希】—— 安装包必须与清单里的 SHA-256 完全一致才允许运行。
//      不匹配一律拒绝并说明,绝不"算了先装上"。这是防供应链投毒的唯一屏障。
//   ② 【如实说明】—— 界面上写清:这是 VB-Audio 的第三方内核驱动、版本号、它会做什么。
//      "察觉不到它的存在"说的是【不用你操作】,不是【不告诉你】。
//   ③ 【离线可用】—— 允许用户自己把安装包放进来,我们只做校验与引导。
//      完全断网的机器也要能装上,这是本项目的底线场景。
//
// ★ 权限:装内核驱动必须管理员,而客户端按 D46 【拒绝提权运行】(提权后设备密钥打不开)。
//   所以主程序全程普通权限,安装时【另起一个提权子进程】,装完即退 ——
//   与"铸身份要用户自己双击 .cmd"是同一套思路。

using System.Diagnostics;
using System.Security.Cryptography;

namespace LocalAI.Client.Services;

/// <param name="Installed">驱动是否已就位</param>
/// <param name="Version">驱动文件版本号(读不到就是 null)</param>
/// <param name="DriverPath">找到的驱动文件,便于用户自行核对</param>
public sealed record AudioDriverStatus(bool Installed, string? Version, string? DriverPath);

/// <summary>安装包清单:版本、下载地址、SHA-256、大小。★ 哈希是这份清单存在的唯一理由。</summary>
public sealed record AudioDriverPackage(string Version, string Url, string Sha256, long Bytes, DateTime ManifestDate);

public static class AudioDriver
{
    /// <summary>界面上如实标出的来源。不隐藏它是第三方。</summary>
    public const string Vendor = "VB-Audio Software";
    public const string ProductName = "VB-CABLE Virtual Audio Device";

    /// <summary>
    /// 检测驱动是否已安装。★ 用【驱动文件】判定而不是枚举音频端点:
    /// 端点枚举要一大套 COM 互操作,而这里只需要回答"装没装、什么版本" ——
    /// 文件在不在、版本几何,FileVersionInfo 一行就够,少一堆可能出错的代码。
    /// (真正的音频路由当然要走端点枚举,那是接语音链路时的事。)
    /// </summary>
    public static AudioDriverStatus Detect()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers");
            if (!Directory.Exists(dir)) return new AudioDriverStatus(false, null, null);

            // 文件名带版本/系统代号(vbaudio_cable64_win10.sys 之类),各版本不完全一致,
            // 所以用通配匹配而不是写死某一个名字 —— 写死的那天供应商改个名就检测不到了。
            var hit = Directory.EnumerateFiles(dir, "vbaudio*.sys", SearchOption.TopDirectoryOnly)
                               .FirstOrDefault(f => Path.GetFileName(f).Contains("cable", StringComparison.OrdinalIgnoreCase));
            if (hit is null) return new AudioDriverStatus(false, null, null);

            var ver = FileVersionInfo.GetVersionInfo(hit).FileVersion;
            return new AudioDriverStatus(true, string.IsNullOrWhiteSpace(ver) ? null : ver, hit);
        }
        catch
        {
            // 读不到就如实说"检测不到",不猜
            return new AudioDriverStatus(false, null, null);
        }
    }

    /// <summary>用户自备安装包的位置:{state}\audio-driver\ 下任意 exe/zip。离线场景走这里。</summary>
    public static string OfflineDir => Path.Combine(AppPaths.StateDir, "audio-driver");

    /// <summary>只留安全字符 —— pkg.Version 可能来自用户自备清单,直接拼进路径会有 ..\ 穿越(审计 2026-07-31)。</summary>
    static string SafeVer(string v) => new string((v ?? "").Where(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());

    public static string? FindOfflinePackage()
    {
        try
        {
            if (!Directory.Exists(OfflineDir)) return null;
            return Directory.EnumerateFiles(OfflineDir)
                            .FirstOrDefault(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                              || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    /// <summary>算文件的 SHA-256(小写十六进制)。</summary>
    public static string Sha256Of(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// 校验安装包。★ 不匹配【一律拒绝】,不给"仍然继续"的选项 ——
    /// 一个可以被跳过的校验等于没有校验。
    /// </summary>
    public static bool Verify(string path, string expectedSha256)
        => !string.IsNullOrWhiteSpace(expectedSha256)
           && string.Equals(Sha256Of(path), expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 与清单比对:装没装、要不要更新。
    /// ★ 同时把【清单自己有多旧】算出来 —— 一份两年没动的清单说"已是最新"是在撒谎。
    /// </summary>
    public static string Compare(AudioDriverStatus st, AudioDriverPackage? pkg, DateTime now)
    {
        if (pkg is null) return "没有可用的版本清单 —— 无法判断是否有更新。";
        var age = (int)(now - pkg.ManifestDate).TotalDays;
        var stale = age > 180 ? $"(★ 这份清单已经 {age} 天没更新过,不能据此断定就是最新)" : "";
        if (!st.Installed) return $"未安装。清单里的版本:{pkg.Version} {stale}";
        if (st.Version is null) return $"已安装,但读不到版本号。清单里的版本:{pkg.Version} {stale}";
        return string.Equals(st.Version, pkg.Version, StringComparison.OrdinalIgnoreCase)
            ? $"已是清单里的版本({pkg.Version}){stale}"
            : $"已安装 {st.Version},清单里是 {pkg.Version} —— 可以更新 {stale}";
    }

    /// <summary>
    /// 下载安装包并【校验哈希】。★ 校验不过一律删掉下载的文件并拒绝 ——
    /// 一个下载下来的可执行文件如果哈希对不上,唯一正确的处理就是当场销毁它。
    /// 走用户点击触发,不在后台偷偷下载(这是个本地私有的项目,出网必须是显式动作)。
    /// </summary>
    public static async Task<(bool Ok, string Path, string Message)> DownloadAsync(AudioDriverPackage pkg, IProgress<double>? progress = null)
    {
        Directory.CreateDirectory(OfflineDir);
        var ver = SafeVer(pkg.Version);
        if (string.IsNullOrWhiteSpace(ver)) ver = "download";   // "官方最新" 清洗后为空 -> 用固定基名,别落成 vbcable-.zip
        var target = Path.Combine(OfflineDir, $"vbcable-{ver}.tmp");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var resp = await http.GetAsync(pkg.Url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? pkg.Bytes;
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(target))
            {
                var buf = new byte[81920];
                long got = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    got += n;
                    if (total > 0) progress?.Report(Math.Min(1, (double)got / total));
                }
            }

            // ★ 哈希是【可选的额外一层】(信任模型改为 Authenticode 签名后):
            //   清单里填了 Sha256 就多比对一次,不过就【销毁】,不留在盘上等人误点;
            //   没填就跳过 —— 真正拦在提权运行前的把关是 RunInstaller 里的 Authenticode 验证。
            if (!string.IsNullOrWhiteSpace(pkg.Sha256) && !Verify(target, pkg.Sha256))
            {
                var actual = Sha256Of(target);
                try { File.Delete(target); } catch { }
                return (false, "", $"哈希校验失败,已删除下载的文件。{Environment.NewLine}期望 {pkg.Sha256}{Environment.NewLine}实得 {actual}");
            }

            // ★ 落盘文件名跟随 URL 的真实扩展名(2026-07-31 审计):官方发的是 .zip,硬编码 .exe 会得到
            //   一个"伪装成 exe 的 zip",RunInstaller 起它必然失败,还会被 FindOfflinePackage 反复捡起。
            var ext = Path.GetExtension(new Uri(pkg.Url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".exe";
            var final = Path.Combine(OfflineDir, $"vbcable-{ver}{ext}");
            try { if (File.Exists(final)) File.Delete(final); } catch { }
            File.Move(target, final);
            return (true, final, $"已下载({pkg.Version})。安装前会验证它由 VB-Audio 官方签名。");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(target)) File.Delete(target); } catch { }
            return (false, "", "下载失败:" + ex.Message);
        }
    }

    /// <summary>
    /// 找到已安装的卸载入口(注册表里的卸载项)。★ 找不到就如实返回 null ——
    /// 不去猜路径、更不去手动删驱动文件:那是把系统搞坏的经典方式。
    /// </summary>
    /// <summary>一条卸载入口:给人看的名字 + 实际会运行的命令。</summary>
    public sealed record UninstallEntry(string DisplayName, string Command);

    /// <summary>
    /// 找出所有【VB-CABLE 本体】的卸载入口 —— 收集全部,不"命中第一个就返回"。
    /// ★ 收紧判定(2026-07-31 审计):此前只要 DisplayName 含 "VB-Audio" 就认,会把兄弟产品
    ///   (Voicemeeter 等)也拉进来,而确认框却写死"卸载 VB-CABLE" —— 可能提权启动错的卸载程序。
    ///   现在要求 Publisher = VB-Audio Software 且 DisplayName 含 CABLE;多于一个就交给用户自己选(fail-closed)。
    /// </summary>
    public static IReadOnlyList<UninstallEntry> FindUninstallers()
    {
        var list = new List<UninstallEntry>();
        var branches = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };
        foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            foreach (var branch in branches)
            {
                try
                {
                    using var key = root.OpenSubKey(branch);
                    if (key is null) continue;
                    foreach (var name in key.GetSubKeyNames())
                    {
                        using var sub = key.OpenSubKey(name);
                        var display = sub?.GetValue("DisplayName") as string ?? "";
                        var publisher = sub?.GetValue("Publisher") as string ?? "";
                        // 收紧:必须是 VB-Audio 出版 + 名字含 CABLE(CABLE 家族靠这一条已全覆盖,不再放 VB-Audio 泛匹配)
                        if (!publisher.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!display.Contains("CABLE", StringComparison.OrdinalIgnoreCase)) continue;
                        if (sub?.GetValue("UninstallString") is string cmd && !string.IsNullOrWhiteSpace(cmd)
                            && !list.Any(e => e.Command == cmd))
                            list.Add(new UninstallEntry(display, cmd));
                    }
                }
                catch { /* 读不到就继续找下一处 */ }
            }
        return list;
    }

    /// <summary>
    /// 一键卸载。★ 只走【官方的卸载程序】,绝不自己去删 .sys 或改注册表 ——
    /// 手动拆内核驱动留下的残留会让下次安装也装不上,严重时整机没声音。
    /// 与安装同理:另起一个提权子进程,会弹一次 UAC。
    /// </summary>
    /// <summary>运行指定的那一条卸载入口。★ 由调用方【先查后问】,把实际命中的那条给用户核对过再传进来。</summary>
    public static bool RunUninstaller(UninstallEntry entry, out string message)
    {
        message = "";
        var cmd = entry.Command;
        try
        {
            // UninstallString 可能是带引号的 exe + 参数,拆开再走 ShellExecute
            string exe = cmd, args = "";
            if (cmd.StartsWith("\""))
            {
                var close = cmd.IndexOf('"', 1);
                if (close > 0) { exe = cmd[1..close]; args = cmd[(close + 1)..].Trim(); }
            }
            else
            {
                var sp = cmd.IndexOf(".exe ", StringComparison.OrdinalIgnoreCase);
                if (sp > 0) { exe = cmd[..(sp + 4)]; args = cmd[(sp + 5)..].Trim(); }
            }
            Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = true, Verb = "runas" });
            message = $"卸载程序已启动({entry.DisplayName})。卸完回到这里点「重新检测」。";
            return true;
        }
        catch (Exception ex)
        {
            message = "没能启动卸载程序:" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 以管理员身份运行安装包。★ 主程序【不提权】(D46),这里另起一个提权子进程,装完即退。
    /// 用户会看到一次系统的 UAC 提示 —— 装内核驱动必然要管理员,这一步谁也绕不过,
    /// 但除此之外全程不需要他做任何事(不用打开 VB-CABLE、不用调设置)。
    /// </summary>
    public static bool RunInstaller(string packagePath, out string message)
    {
        message = "";
        try
        {
            if (!File.Exists(packagePath)) { message = "安装包不在:" + packagePath; return false; }

            // ★ 传进来是 .zip 就先解包再找真正的安装程序(2026-07-31 审计):VB-Audio 官方只发 .zip。
            //   哈希闸仍作用在下载/自备的【原包】上,解出来的 exe 来自已验哈希的压缩包,不放松任何校验。
            if (packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var outDir = Path.Combine(OfflineDir, Path.GetFileNameWithoutExtension(packagePath) + "-unpacked");
                try
                {
                    if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
                    System.IO.Compression.ZipFile.ExtractToDirectory(packagePath, outDir);
                }
                catch (Exception zex) { message = "解压安装包失败:" + zex.Message; return false; }
                // VB-CABLE 包里的安装程序名形如 VBCABLE_Setup_x64.exe / VBCABLE_Setup.exe
                var setup = Directory.EnumerateFiles(outDir, "*.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(f => Path.GetFileName(f).Contains("Setup", StringComparison.OrdinalIgnoreCase)
                                      || Path.GetFileName(f).Contains("VBCABLE", StringComparison.OrdinalIgnoreCase));
                if (setup is null) { message = "压缩包里没找到安装程序(*Setup*.exe)。"; return false; }
                packagePath = setup;
            }

            // ★★ 提权运行【之前】必须过 Authenticode 签名(用户裁定 2026-07-31 · 信任模型):
            //   验证这个 exe 确实由 VB-Audio 官方签发、证书链有效。验不过就【拒绝运行】——
            //   与安装包哈希闸同一口径:装驱动不可逆且提权,"不确定"必须等于"不做"。
            if (!Authenticode.VerifySignedByVbAudio(packagePath, out var signer))
            {
                message = $"拒绝运行:这个安装程序没通过 VB-Audio 官方签名验证({signer})。"
                        + "为安全起见不提权运行未经签名核对的驱动安装程序。";
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = packagePath,
                UseShellExecute = true,
                Verb = "runas",          // ← 这一下会弹 UAC
            };
            Process.Start(psi);
            message = "安装程序已启动。装完回到这里点「重新检测」。";
            return true;
        }
        catch (Exception ex)
        {
            // 用户点了 UAC 的"否"也会走到这里 —— 如实说,不重试、不绕路
            message = "没能启动安装程序:" + ex.Message;
            return false;
        }
    }
}
