// V21 -- 管理端**自己的** settings.json。纪律③在这里的落点。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ 纪律③(每个 json 只能有一个写者)裁的就是这一格:
//    **`settings.json` 拆两份**;共享的皮肤由**客户端**写、管理端**只读监听**。
//
//  为什么非拆不可:`ModelsView` 搬到管理端之后,它写的两项
//  (`ModelStorePath` · `AutoUnloadIdle`)会跟着变成**管理端在写**。
//  如果继续写客户端那份 `%LOCALAPPDATA%\LocalAI\client\settings.json`,那个文件就有了
//  **两个进程各自的写者**,而两边都是「读进内存 → 改一个字段 → 整份写回」——
//  于是**后写的那个会把对方在这期间改的字段整个盖掉**。
//  ★ 这不是理论:客户端在同一份对象上存着皮肤、语言、聊天偏好几十项,
//    管理端只要为了存一个模型路径把整份写回去,就可能把用户刚换的皮肤打回去。
//    而且**不会有任何东西红** —— 两边各自都"成功保存了"。
//
//  ⇒ 落点:`%LOCALAPPDATA%\LocalAI\admin\settings.json`(`AppPaths.AdminStateDir`)。
//
//  ★★ 皮肤的方向**没有变,也不许变**:管理端仍然**读**客户端那份并监听它的变化
//    (`App.xaml.cs` 的 `ReadSkin` / `WatchSettings`)—— 那是**读**,不是写。
//    `AdminPaths.cs` 顶部那句原样成立:「读别人的目录是对的,写才不对。」
//
//  ★★★ 如实交代一次**迁移带来的行为变化**:
//    `ModelStorePath` 与 `AutoUnloadIdle` 这两项过去存在客户端那份里。
//    拆开之后,**旧值不会自动跟过来** —— 第一次打开管理端的「模型」页会看到空的存放路径。
//    ⇒ 下面 `Current` 的初始化做了一次**一次性读迁**:管理端自己那份还不存在时,
//      从客户端那份把这两项**读**过来当初值(只读客户端,写只写自己这份)。
//      这不是"两个写者",是一次性的读取。
// ══════════════════════════════════════════════════════════════════════════════

using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAI.Client.Services;

namespace LocalAI.Admin.Services;

public static class AdminSettings
{
    static readonly JsonSerializerOptions J = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>管理端自己那份设置的落点。★ 与客户端那份**不是同一个文件**。</summary>
    public static string Path_ => System.IO.Path.Combine(AdminPaths.StateDir, "settings.json");

    static AppSettings? _current;

    /// <summary>
    /// 管理端进程内**唯一**的那份设置对象。
    /// ★ 与客户端 `App.Settings` 同类型(`AppSettings` 两个 csproj 编同一份),
    ///   所以搬过来的界面代码一个字都不用改;变的只有**它落在哪个文件**。
    /// </summary>
    public static AppSettings Current => _current ??= Load();

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path_)) ?? new AppSettings();
        }
        catch { /* 坏档当空档,下面走一次读迁 —— 绝不让管理端因为一份设置开不了 */ }

        // ★ 一次性读迁(见文件头最后一段):管理端自己那份还不存在 ⇒ 从客户端那份把
        //   **这一页会用到的两项**读过来当初值。★ 只读,不回写客户端那份。
        var seeded = new AppSettings();
        try
        {
            var fromClient = AppSettings.Load();
            seeded.ModelStorePath = fromClient.ModelStorePath;
            seeded.AutoUnloadIdle = fromClient.AutoUnloadIdle;
        }
        catch { /* 客户端那份读不到就用默认值 —— 那只是初值,不是数据丢失 */ }
        return seeded;
    }

    /// <summary>
    /// 存回**管理端自己那份**。★ 原子写:先写 .tmp 再改名替换,崩在中途不会留下半个 JSON
    /// (与客户端 `AppSettings.Save` 同一条纪律)。
    /// </summary>
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AdminPaths.StateDir);
            var tmp = Path_ + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, J));
            File.Move(tmp, Path_, overwrite: true);
        }
        catch { /* 存不上不该拖垮界面;下一次改动还会再存一遍 */ }
    }
}
