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
}
