// P3c -- 客户端本地状态位置。与主机的 {state}/identity 无关:这里只存**本设备**的配对档案与界面偏好。
// 默认 %LOCALAPPDATA%\LocalAI\client(每用户、普通权限可写;D46:客户端一律普通用户运行)。
// 可用环境变量 LOCALAI_CLIENT_STATE 覆盖(selftest 用临时目录,不碰真实档案)。

namespace LocalAI.Client.Services;

public static class AppPaths
{
    public const string StateEnvVar = "LOCALAI_CLIENT_STATE";

    public static string StateDir
    {
        get
        {
            var o = Environment.GetEnvironmentVariable(StateEnvVar);
            if (!string.IsNullOrWhiteSpace(o)) return o;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalAI", "client");
        }
    }

    public static string ProfilePath => Path.Combine(StateDir, "profile.json");
    public static string SettingsPath => Path.Combine(StateDir, "settings.json");

    public static void EnsureStateDir() => Directory.CreateDirectory(StateDir);

    // ────────────────────────────────────────────────────────────────────────
    //  V14:管理端的状态位置也定义在**这里**,而不是各定义一份。
    //
    //  ★ 两边都要用它:客户端要探"管理端在不在跑"(裁定第 1 条,起之前先看);
    //    管理端自己要拿它放锁文件。而本文件被**两个 csproj 同时编译**(csproj link)
    //    ⇒ 两边没法各持一份路径,也就不会出现"客户端探的锁文件和管理端放的不是同一个"
    //      那种缝 —— 那种缝的表现是**每次都起出第二个管理端**,而两边各自都"对"。
    //  ★ 与客户端目录**并列、不共用**:两个应用各有各的单实例锁,
    //    锁文件同名同目录会让它们互相把对方判成"已经在跑"。
    // ────────────────────────────────────────────────────────────────────────
    public const string AdminStateEnvVar = "LOCALAI_ADMIN_STATE";

    /// <summary>管理端的应用键 —— 进锁文件名与唤醒/退出事件名。客户端那边是 <c>Client</c>。</summary>
    public const string AdminAppKey = "Admin";

    public static string AdminStateDir
    {
        get
        {
            var o = Environment.GetEnvironmentVariable(AdminStateEnvVar);
            if (!string.IsNullOrWhiteSpace(o)) return o;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalAI", "admin");
        }
    }
}
