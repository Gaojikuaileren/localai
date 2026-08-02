// P3c -- 客户端本地存档(项目 / 会话 / 待办)。
//
// 用户裁定(2026-07-30):**沿用 D21/D22 的口径落盘为明文** —— 与记忆库、备份同一处理,
//   不引入客户端侧的密钥管理。落点 %LOCALAPPDATA%\LocalAI\client\(见 AppPaths.StateDir;与主机的 {state} 无关)(每用户、普通权限,见 AppPaths),
//   与 ${state}/secrets(强 ACL、排除备份)分开:这里存的是内容,不是凭据。
//
// 硬性约束(都在 selftest 里钉死):
//   ① **幽灵会话绝不落盘** —— 它的定义就是"不保留记录、不纳入记忆";落盘就等于毁约。
//   ② 已删除会话【连同 DeletedAt 一起存】,重启后 30 天窗口继续走完;启动时扫掉过期的。
//   ③ 写入必须【原子】:先写 .tmp 再改名替换,崩在中途也不会留下半个 JSON。
//   ④ 读取必须【容错】:存档损坏就当空档启动并把坏档改名留证,绝不让用户开不了应用。

using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAI.Client.Services;

public static class ClientStore
{
    static readonly JsonSerializerOptions J = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },   // 枚举存名字:存档可读,加枚举值也不会错位
    };

    public static string ChatPath => Path.Combine(AppPaths.StateDir, "chat.json");
    public static string ProjectsPath => Path.Combine(AppPaths.StateDir, "projects.json");
    public static string TodosPath => Path.Combine(AppPaths.StateDir, "todos.json");
    public static string CalendarPath => Path.Combine(AppPaths.StateDir, "calendar.json");
    /// <summary>天气缓存(只存读数与它的时间戳,不存坐标)。</summary>
    public static string WeatherPath => Path.Combine(AppPaths.StateDir, "weather.json");
    public static string MemoryPath => Path.Combine(AppPaths.StateDir, "memory.json");
    public static string NotesPath => Path.Combine(AppPaths.StateDir, "notes.json");
    /// <summary>翻译历史的【收藏】。★ 只存收藏的键,原文一直在会话里(不留两份真相)。</summary>
    public static string HistoryFavPath => Path.Combine(AppPaths.StateDir, "history-favorites.json");
    /// <summary>同传的本机偏好(语言方向、字幕开关、音色)。</summary>
    public static string InterpretPath => Path.Combine(AppPaths.StateDir, "interpret.json");
    public static string TranslationPath => Path.Combine(AppPaths.StateDir, "translation.json");

    /// <summary>本机是否已有存档(有则不再播种示例数据 —— 否则每次启动都冒出一堆"(示例)")。</summary>
    public static bool HasAnyStore()
        => File.Exists(ChatPath) || File.Exists(ProjectsPath) || File.Exists(TodosPath);

    /// <summary>原子写:先写临时文件再替换。中途崩溃只会留下 .tmp,原档完好。</summary>
    public static void Save<T>(string path, T data)
    {
        try
        {
            AppPaths.EnsureStateDir();
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, J));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // ★ 失败不能拖垮应用(下次变更还会再存),但也【不能无声】(审计 2026-08-02):
            //   盘满/文件被锁时,用户以为一切正常,实际每一次改动都没落盘。
            //   记住最后一次失败,存储页据此显示一行警告;成功后自动清掉。
            LastSaveError = $"{DateTime.Now:HH:mm} 写入 {Path.GetFileName(path)} 失败:{ex.GetType().Name}";
            return;
        }
        LastSaveError = null;   // 这次成功 -> 清掉旧警告
    }

    /// <summary>最近一次写盘失败(null = 一切正常)。存储页显示,不弹窗。</summary>
    public static string? LastSaveError { get; private set; }

    /// <summary>读取存档。文件不存在 -> default;损坏 -> 改名留证后当空档,绝不抛。</summary>
    public static T? Load<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), J);
        }
        catch
        {
            // 坏档留证(便于事后查),但不阻塞启动
            try { File.Move(path, path + ".corrupt", overwrite: true); } catch { }
            return default;
        }
    }
}
