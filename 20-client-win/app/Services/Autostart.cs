// P3c -- 开机自启。用户要求「开机自启 + 关窗口保持后台任务栏图标」。
//
// ★ D46(TPM 密钥绑定创建者完整性等级)在这里是硬约束:客户端持有设备证书私钥(CNG 软密钥),
//   必须**始终以普通用户 / Medium Integrity** 运行。因此自启只用:
//     HKCU\Software\Microsoft\Windows\CurrentVersion\Run
//   —— 当前用户级、**不需要提权**、登录时以普通完整性启动。
//   明确**不用**:HKLM Run(需管理员、且可能以其它上下文启动)、
//                任务计划程序的「使用最高权限运行」(会是 High Integrity,密钥就打不开了)。
//   这也符合项目纪律:创建服务/改系统级设置属于用户亲自执行的范畴,而 HKCU 自己的键不属于。

using Microsoft.Win32;

namespace LocalAI.Client.Services;

public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "LocalAI";

    // 允许 selftest 指向一个假的注册表子键,避免污染真实的启动项。
    public static string KeyPath { get; set; } = RunKey;

    public static string ExePath =>
        Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;

    // 带引号,防路径含空格被截断;--tray 让自启时直接进托盘而不弹窗打扰登录。
    static string CommandLine => $"\"{ExePath}\" --tray";

    // 任务管理器「启动应用」里禁用一项时,Windows 并不删 Run 键,而是在
    // Explorer\StartupApproved\Run 写一条 REG_BINARY(首字节为**偶数**=启用,奇数=已禁用)。
    // 只读 Run 键会让开关显示"开"而实际不启动,用户会以为程序坏了反复折腾(审查发现 [13])。
    const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>用户是否在「任务管理器 › 启动应用」里把本项禁用了。</summary>
    public static bool IsBlockedByWindows()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(StartupApprovedKey);
            return k?.GetValue(ValueName) is byte[] { Length: > 0 } b && (b[0] & 1) == 1;
        }
        catch { return false; }
    }

    /// <summary>Run 键里有登记。注意这不等于"开机真的会启动",还要看 <see cref="IsBlockedByWindows"/>。</summary>
    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
        return k?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
    }

    /// <summary>开机是否真的会自启 = 已登记 且 未被 Windows 启动项管理禁用。</summary>
    public static bool IsEffective() => IsEnabled() && !IsBlockedByWindows();

    /// <summary>当前注册的自启命令是否指向**本 exe**。exe 移动/更新后会变成 false,应重写。</summary>
    public static bool IsCurrent()
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
        return k?.GetValue(ValueName) is string s &&
               s.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void Enable()
    {
        using var k = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
                      ?? throw new InvalidOperationException("cannot open HKCU Run key");
        k.SetValue(ValueName, CommandLine, RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (k?.GetValue(ValueName) is not null) k.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static void Set(bool enabled) { if (enabled) Enable(); else Disable(); }
}
