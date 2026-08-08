// 跑一个进程、把 stdout+stderr 都收回来。**客户端与管理端编译同一份**。
//
// ★★★ 为什么它是一个独立文件,而不是留在 HostSetup 里当私有方法(V19 · 2026-08-08):
//
//   管理端拆分会把 `HostSetup` **切成两半**:
//     · `IdentityExistsAsync`(「本机有没有中枢身份」)—— **留在客户端**,
//       因为 `DecideRole` 拿它当角色证据,而角色判据决定业务调用拨回环还是拨 Edge(V13);
//     · `EnsureIdentityAsync`(「铸一次身份」)—— **搬进管理端**。
//   两个都用这一段。而它当时是 `HostSetup` 的**私有**方法 ——
//   一切两半,最省事的写法就是**复制一份过去**。
//
//   ★★ 复制的代价不是"多了 24 行",是**没有任何东西会红**:
//     两份从复制那一刻起内容一样,从此各改各的。哪天一边改了超时、改了编码、
//     改了 `WorkingDirectory` 的空串兜底,另一边**不会红、不会警告、不会有人发现** ——
//     而症状会是"同一条命令在客户端跑得出、在管理端跑不出",
//     那种缺陷要从两份长得几乎一样的源码里对出来。
//   ⇒ 提成一个文件,由两个 csproj `<Compile Link>` 编**同一份**(同 `InstanceLock.cs` 手法,
//     同 D93 裁定④ `WireContracts.cs` 由三个 csproj 编一份)。
//     ★ 判据不写成「两份内容相同」—— 那会被复制粘贴骗过。判据是
//       「管理端 csproj 里这一条必须是 `..\app\` 开头的 link」,见 Selftest 里那组。
//     ★★ 而且**链了之后再复制一份进 admin/ 会编译失败**(同 namespace 同类名 ⇒ CS0101)——
//       那一层是结构性的,不靠判断挡住。
//
// ★ 命名保留 `RunCapturedAsync`:决议包、迁移地图与交接里都是这个名字,
//   改名会让那些文字与代码对不上,而对不上的文档下一个人只能重新考古。

using System.Diagnostics;

namespace LocalAI.Client.Services;

/// <summary>起子进程并收全部输出的那一小段。★ 客户端与管理端**编译同一份**。</summary>
public static class ProcRun
{
    /// <summary>
    /// 跑一个进程并把 stdout+stderr 都收回来。
    /// ★ **不提权** —— 这里跑的都是必须普通用户跑的东西(CA 私钥绑定创建时的完整性等级,
    ///   见 <see cref="HostSetup"/> 顶部那三条边界)。要提权的只有防火墙那一步,它走另一条路。
    /// ★ 失败不抛:回 `(-1, 原因)`。调用方要的是"为什么没成",不是一个异常栈。
    /// </summary>
    public static async Task<(int code, string output)> RunCapturedAsync(string exe, string args, string? workDir)
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
}
