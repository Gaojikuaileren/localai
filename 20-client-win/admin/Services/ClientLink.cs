// V14 -- 管理端与主机客户端之间的那根线。三件事:客户端在不在 / 起它 / 请它优雅退出。
//
// ★★★ 本文件里**一个停进程 API 都没有**,这是有意的、也是可被断言的:
//   裁定第 7 条要求管理端关闭时连带关掉客户端,而且**必须走客户端既有的八步优雅退出**。
//   `Process.Kill()` / `TerminateProcess` / `taskkill` 一旦出现在这里,
//   D106 裁定②钉住的那张八步表就守不到真正会跑的那条路了 —— 它守的是**那张表**,
//   不是"有没有收尾"。⇒ 这里只发信号,退不退、怎么退由客户端自己决定。
//
// ★ 承重的是八步里的第 5 步 `end-session+release-vram`:强杀会让中枢那边的租约挂满整个 TTL,
//   而 client_session 正是「有没有人在用」的判据 ⇒ 副机会被判成"有人在用"而关不掉栈。

using LocalAI.Client.Services;

namespace LocalAI.Admin.Services;

public static class ClientLink
{
    /// <summary>客户端的应用键(与 <see cref="SingleInstance.AppKey"/> 同一个常量,不另抄一份)。</summary>
    public static string ClientAppKey => SingleInstance.AppKey;

    /// <summary>客户端现在在不在跑。★ 探的是它的锁文件 —— 跨进程、跨会话、零特权。</summary>
    public static bool IsClientRunning() => InstanceLock.IsRunning(AppPaths.StateDir, ClientAppKey);

    /// <summary>
    /// 客户端程序在哪。出包布局:`dist\admin\localai-admin.exe` 与 `dist\client\localai-client.exe` 并排。
    /// ★ 与客户端那边找管理端的 <c>AdminAppPathNextTo</c> 互为镜像,判据同样是「**装没装**」。
    /// </summary>
    public static string? ClientExePathNextTo(string? adminExeDir)
    {
        if (string.IsNullOrWhiteSpace(adminExeDir)) return null;
        var exe = Path.GetFullPath(Path.Combine(adminExeDir, "..", "client", "localai-client.exe"));
        return File.Exists(exe) ? exe : null;
    }

    public static string? ClientExePath()
    {
        try { return ClientExePathNextTo(Path.GetDirectoryName(Environment.ProcessPath)); }
        catch { return null; }
    }

    /// <summary>
    /// 请客户端**优雅退出**,并等它真的退了。
    /// ★★ 返回 (退了没有, 说明)。**等不到就如实说没退** —— 不许因为"信号发出去了"就宣布成功。
    ///   这个项目今天修的一整类谎就是"进程退出码是 0 所以成功了"。
    /// </summary>
    public static async Task<(bool Stopped, string Why)> RequestClientQuitAsync(TimeSpan budget)
    {
        if (!IsClientRunning()) return (true, "客户端本来就没在跑。");
        var deadline = DateTime.UtcNow + budget;

        // ★★ 退出通道要**等它开**,不能一次发不出去就判失败。
        //   锁文件是在客户端的 `Program.Main` 里建的,而退出监听是在 `App.OnStartup` 里接的 ——
        //   两者之间有一段真实存在的窗口。而那段窗口恰恰覆盖了一个**正常时序**:
        //   管理端刚把客户端拉起来(裁定第 1 条),用户马上又去关管理端(裁定第 7 条)。
        //   ★ 这条是自检的 live 段当场抓到的:第一版在那段窗口里直接答"发不出信号",
        //     于是八步善后一步都没跑,而管理端还以为自己尽力了。
        var signalled = false;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsClientRunning()) return (true, "客户端在收到信号之前就自己退了。");
            if (InstanceLock.SignalQuit(ClientAppKey)) { signalled = true; break; }
            await Task.Delay(200);
        }
        if (!signalled)
            return (false, $"等了 {budget.TotalSeconds:0} 秒都没能把退出信号送进去 —— "
                         + "客户端的退出通道始终没开着。没有强杀它;请到客户端窗口里手动退出。");

        while (DateTime.UtcNow < deadline)
        {
            if (!IsClientRunning()) return (true, "客户端已经走完八步善后退出了。");
            await Task.Delay(200);
        }
        // ★ 到这里**不去强杀**。宁可如实说"它没退",也不把 D106 守着的那条路绕过去。
        return (false, $"等了 {budget.TotalSeconds:0} 秒,客户端仍在跑 —— "
                     + "没有强杀它(强杀会跳过 end-session,把租约挂满整个 TTL,"
                     + "副机会被判成'有人在用'而关不掉栈)。请到客户端窗口里手动退出。");
    }

    /// <summary>
    /// 起客户端。★ 只在**它没在跑**时起 —— 起第二个的那一刻,单实例锁会让它自己安静退出,
    /// 而用户看到的是"点了没反应"。
    /// </summary>
    public static (bool Started, string Why) StartClient(bool tray)
    {
        if (IsClientRunning()) return (false, "客户端已经在跑了。");
        var exe = ClientExePath();
        if (exe is null) return (false, "旁边没有 `..\\client\\localai-client.exe` —— 这台没装客户端。");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            if (tray) psi.ArgumentList.Add("--tray");
            System.Diagnostics.Process.Start(psi);
            return (true, tray ? "已把客户端起到托盘。" : "已打开客户端。");
        }
        catch (Exception ex) { return (false, "起不来:" + ex.Message); }
    }
}
