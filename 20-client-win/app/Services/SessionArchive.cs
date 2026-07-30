// P3c -- 会话原文的【分层存储】(温层)。
//
// 讨论结论(2026-07-30):"会话太多"真正紧张的不是磁盘,而是内存与上下文 —— 所以【不压缩】,而是分层:
//   热层:最近 N 条原文,一直在内存、随 chat.json 落盘;
//   温层:更早的原文,★【按会话分文件】存在 {state}/client/archive/<sessionId>.json,
//        平时不加载,用户点"加载更早"才读;
//   冷层:摘要(记忆库),很小,一直在。
//
// ★ 关键纪律:归档【不是删除】—— 原文一直在,只是换了个文件放。
//   唯一会真正消失的路径,是用户在设置里显式点"删除归档原文"(D52 / 永不删原文的例外)。
// ★ 幽灵会话永远不进这里(它压根不落盘)。

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAI.Client.Services;

public static class SessionArchive
{
    static readonly JsonSerializerOptions J = new()
    {
        WriteIndented = false,   // 温层是"放着不看"的,不需要好看,省空间
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Dir => StorageUsage.ArchiveDir;

    static string FileFor(string sessionId) => Path.Combine(Dir, sessionId + ".json");

    /// <summary>某会话已归档的消息条数(不读全文,只为界面显示"加载更早的 N 条")。</summary>
    public static int Count(string sessionId) => Load(sessionId).Count;

    public static List<ChatMessage> Load(string sessionId)
    {
        try
        {
            var f = FileFor(sessionId);
            if (!File.Exists(f)) return new List<ChatMessage>();
            return JsonSerializer.Deserialize<List<ChatMessage>>(File.ReadAllText(f), J) ?? new List<ChatMessage>();
        }
        catch { return new List<ChatMessage>(); }   // 坏档不阻塞:当作没有归档
    }

    /// <summary>把更早的消息追加进温层(与已有的按时间合并)。原子写。</summary>
    public static void Append(string sessionId, IEnumerable<ChatMessage> older)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var all = Load(sessionId);
            all.AddRange(older);
            all = all.OrderBy(m => m.At).ToList();
            var f = FileFor(sessionId);
            var tmp = f + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, J));
            File.Move(tmp, f, overwrite: true);
        }
        catch { /* 归档失败不该影响使用:消息仍在热层,下次再归 */ }
    }

    /// <summary>温层里有归档的会话 id —— 删除归档原文时用来标注记忆"原文已删除"。</summary>
    public static List<string> ArchivedSessionIds()
    {
        try
        {
            if (!Directory.Exists(Dir)) return new List<string>();
            return Directory.EnumerateFiles(Dir, "*.json").Select(Path.GetFileNameWithoutExtension).Where(x => x is not null).Select(x => x!).ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>删除某会话的归档(会话被彻底删除时一并清掉,避免留下孤儿文件)。</summary>
    public static void Delete(string sessionId)
    {
        try { var f = FileFor(sessionId); if (File.Exists(f)) File.Delete(f); } catch { }
    }
}
