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

    /// <summary>
    /// 读归档,并告诉调用方这份归档是【真的没有】还是【读坏了】。
    /// ★ 这两件事必须分开(审计 2026-07-31):Load 对两者都返回空表,
    ///   而 Append 拿空表加上新消息写回去 —— 一份只是暂时读不动的归档
    ///   (杀毒软件锁着、盘满写一半、同步工具插一脚)就被【静默整份覆盖】,
    ///   里面所有旧原文一次性消失。
    /// </summary>
    static (List<ChatMessage> msgs, bool corrupt) LoadChecked(string sessionId)
    {
        var f = FileFor(sessionId);
        if (!File.Exists(f)) return (new List<ChatMessage>(), false);
        try
        {
            var got = JsonSerializer.Deserialize<List<ChatMessage>>(File.ReadAllText(f), J);
            return got is null ? (new List<ChatMessage>(), true) : (got, false);
        }
        catch { return (new List<ChatMessage>(), true); }
    }

    /// <summary>
    /// 把更早的消息追加进温层(与已有的按时间合并)。原子写。
    /// ★★ 返回值必须被看(审计 2026-07-31 的高危):以前这里是 void +
    ///   "归档失败不该影响使用:消息仍在热层" —— 而调用方紧接着就把它们
    ///   从热层删了。那句注释描述的是一个没有人实现过的行为:
    ///   写盘失败 = 原文【永久静默丢失】。现在写不成就说写不成。
    /// </summary>
    /// <returns>true = 真的落盘了,调用方才可以从热层移除。</returns>
    public static bool Append(string sessionId, IEnumerable<ChatMessage> older)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var f = FileFor(sessionId);
            var (all, corrupt) = LoadChecked(sessionId);
            if (corrupt)
            {
                // ★ 坏档【不覆盖】—— 先攒到一边留作证据,再从头开一份。
                //   里面可能是可救的原文;就算真的救不回来,
                //   "文件还在但坏了" 也比 "一声不响地没了" 诚实得多。
                var bak = f + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Move(f, bak, overwrite: false); } catch { return false; }
            }
            all.AddRange(older);
            all = all.OrderBy(m => m.At).ToList();
            var tmp = f + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, J));
            File.Move(tmp, f, overwrite: true);
            return true;
        }
        catch { return false; }   // 没落盘 —— 调用方必须把消息留在热层
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
