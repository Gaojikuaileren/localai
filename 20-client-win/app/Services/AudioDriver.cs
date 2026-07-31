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
        var target = Path.Combine(OfflineDir, $"vbcable-{pkg.Version}.tmp");
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

            // ★ 校验:不过就【销毁】,不留在盘上等人误点
            if (!Verify(target, pkg.Sha256))
            {
                var actual = Sha256Of(target);
                try { File.Delete(target); } catch { }
                return (false, "", $"哈希校验失败,已删除下载的文件。{Environment.NewLine}期望 {pkg.Sha256}{Environment.NewLine}实得 {actual}");
            }

            var final = Path.Combine(OfflineDir, $"vbcable-{pkg.Version}.exe");
            try { if (File.Exists(final)) File.Delete(final); } catch { }
            File.Move(target, final);
            return (true, final, $"已下载并通过校验({pkg.Version})。");
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
    public static string? FindUninstaller()
    {
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
                        if (!display.Contains("VB-CABLE", StringComparison.OrdinalIgnoreCase)
                            && !display.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)) continue;
                        if (sub?.GetValue("UninstallString") is string cmd && !string.IsNullOrWhiteSpace(cmd)) return cmd;
                    }
                }
                catch { /* 读不到就继续找下一处 */ }
            }
        return null;
    }

    /// <summary>
    /// 一键卸载。★ 只走【官方的卸载程序】,绝不自己去删 .sys 或改注册表 ——
    /// 手动拆内核驱动留下的残留会让下次安装也装不上,严重时整机没声音。
    /// 与安装同理:另起一个提权子进程,会弹一次 UAC。
    /// </summary>
    public static bool RunUninstaller(out string message)
    {
        message = "";
        var cmd = FindUninstaller();
        if (cmd is null)
        {
            message = "找不到官方卸载入口 —— 请在「设置 › 应用」里卸载。"
                    + "我们不会自己去删驱动文件:手动拆内核驱动的残留会让下次装不上。";
            return false;
        }
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
            message = "卸载程序已启动。卸完回到这里点「重新检测」。";
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
