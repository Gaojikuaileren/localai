// P3c -- 本机占用统计与清理。设置里的「存储与清理」板块用它。
//
// ★ 这里的数字全部是【真读文件算出来的】,不估、不编。拿不到就显示 0 并说明。
//
// 用户裁定(2026-07-30):一个【一键清爽】按钮,下面用勾选决定它执行哪些动作:
//   · 清理缓存        —— 安全,默认勾
//   · 整理摘要        —— 安全(只增不减);★ AI 未接入前不做任何事,如实说明
//   · 自动清理记忆库  —— 按设置的规则,先预演再删,默认不勾
//   · 删除归档原文    —— ★ 不可逆,默认【不勾】;勾了要单独确认并列出将删什么
//     (对应决议:永不删原文,除非用户在设置里显式点)

using System.IO;

namespace LocalAI.Client.Services;

public static class StorageUsage
{
    /// <summary>一项占用。Bytes = -1 表示"这项还没有(未接入/无目录)",界面显示"—"而不是 0。</summary>
    public sealed record Item(string Label, long Bytes, string Note);

    /// <summary>
    /// 可读大小。★ 用 InvariantCulture:本机是德语区(小数点是逗号),而界面语言是中/英/日 ——
    /// 不定死就会在中文界面里冒出"1,5 KB"。这类"跟着系统区域走味"的问题本项目已经踩过一次。
    /// </summary>
    public static string Human(long bytes)
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        if (bytes < 0) return "—";
        if (bytes < 1024) return bytes.ToString(c) + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", c) + " KB";
        return (bytes / 1024.0 / 1024.0).ToString("0.00", c) + " MB";
    }

    static long FileBytes(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; }
    }

    static long DirBytes(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { return 0; }
    }

    /// <summary>归档原文的存放目录(会话原文分层存储的"温"层)。接入分层存储前可能不存在。</summary>
    public static string ArchiveDir => Path.Combine(AppPaths.StateDir, "archive");

    /// <summary>缓存文件:我们自己落到临时目录的剪贴板预览图 + 坏档留证。</summary>
    public static IEnumerable<string> CacheFiles()
    {
        var list = new List<string>();
        try { list.AddRange(Directory.EnumerateFiles(Path.GetTempPath(), "localai-clip-*.png")); } catch { }
        try { list.AddRange(Directory.EnumerateFiles(AppPaths.StateDir, "*.corrupt")); } catch { }
        try { list.AddRange(Directory.EnumerateFiles(AppPaths.StateDir, "*.tmp")); } catch { }
        return list;
    }

    public static long CacheBytes()
    {
        long n = 0;
        foreach (var f in CacheFiles())
        {
            try { n += new FileInfo(f).Length; } catch { }
        }
        return n;
    }

    /// <summary>清理缓存。返回 (删掉的文件数, 释放的字节数)。删不掉的跳过,不抛。</summary>
    public static (int Files, long Bytes) ClearCache()
    {
        int files = 0; long bytes = 0;
        foreach (var f in CacheFiles().ToList())
        {
            try
            {
                var len = new FileInfo(f).Length;
                File.Delete(f);
                files++; bytes += len;
            }
            catch { /* 正被占用就跳过,下次再说 */ }
        }
        return (files, bytes);
    }

    /// <summary>四项占用一览(会话原文 / 归档原文 / 记忆库 / 缓存)。memoryBytes 由 MemoryCenter 给。</summary>
    public static List<Item> Snapshot(long memoryBytes)
    {
        var archive = DirBytes(ArchiveDir);
        return new List<Item>
        {
            new("会话原文", FileBytes(ClientStore.ChatPath), "所有会话与消息(永不自动删)"),
            new("归档原文", Directory.Exists(ArchiveDir) ? archive : -1,
                Directory.Exists(ArchiveDir) ? "已归档的早期消息(可手动删除)" : "尚无归档(分层存储待接入)"),
            new("记忆库", memoryBytes, "AI 生成的摘要与事实"),
            new("缓存", CacheBytes(), "剪贴板预览图、临时与损坏文件(随时可清)"),
        };
    }
}
