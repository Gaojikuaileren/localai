// P3c -- 本客户端是哪一版。
//
// ★ 版本戳由 `90-ops/build-client.ps1` 在发布时烧进程序集(InformationalVersion),
//   形如 `20260803-2018+4e5af32`(构建时刻 + 提交短号;工作树不干净会带 `.dirty`)。
//   直接在开发树里跑(dotnet run / Debug)时它不存在 —— 这时**如实说"开发构建"**,
//   不编一个版本号出来。装出来的版本号会让"两边版本对不对得上"这件事失去意义。
//
// ★ 三层版本,别混为一谈:
//   ① **协议版本**(HubClient.ClientProtocol):决定两边能不能对话。★ 它已经被
//      **写进配对的六词推导**里(identity 的 SAS transcript 含 protoVer)—— 两边协议版本不同,
//      六个词直接对不上,配不成。这是结构性的,不靠谁去检查。
//   ② **客户端版本戳**(本文件):同一协议下的不同构建。它**不影响能不能连**,
//      只影响"两边功能是不是同一套"。所以是**提示**,不是拦截。
//   ③ **中枢版本**:中枢自报的协议版本(见 HubClient.HubProtocol)。

using System.Reflection;

namespace LocalAI.Client.Services;

public static class BuildInfo
{
    /// <summary>版本戳。发布产物里是 `yyyyMMdd-HHmm+<sha>`;开发树里是 null。</summary>
    public static string? Stamp { get; } = Read();

    /// <summary>给界面用的一句话 —— 开发树里如实说"开发构建",不编号。</summary>
    public static string Display => Stamp ?? "开发构建(未经 build-client.ps1 打包)";

    static string? Read()
    {
        try
        {
            var v = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(v)) return null;
            // SDK 会在 InformationalVersion 后面自动追加 `+<commit>`;我们自己烧的戳里也有 `+`。
            // 认不出我们那种形状(开头是 8 位日期)就当没有 —— 免得把 SDK 的默认值当成版本戳。
            var head = v.Split('+')[0];
            return head.Length >= 8 && head[..8].All(char.IsDigit) ? v : null;
        }
        catch { return null; }
    }
}
