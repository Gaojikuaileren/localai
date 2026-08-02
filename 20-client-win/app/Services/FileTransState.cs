// P3c -- 翻译工作空间【第三场景:文件翻译】的状态(用户裁定 2026-08-02)。
//
// 设计:PDF / PNG / JPG 进,同样格式与排版出。
//   · 上方会话区分左右:左 = 原文件预览(导入/拖入),右 = 翻译结果实时预览 + 保存;
//   · 下方与同传共用 语言方向 + 语言池;右侧换成【工具栏】:
//     AI 自动标注(第一位)/ 创建标注框 / 撤回 / 实时预览开关(默认关)/ 开始翻译(实时开着则灰);
//   · 标注框 = 用户在原文件上圈出【要翻译的部分】,喂给 AI。
//
// ★ 诚实:翻译引擎未接入(P4)。现在【真的】:导入、预览(PNG/JPG)、画框、撤回、落盘;
//   【不做】:自动标注、翻译输出、保存结果 —— 界面如实说"引擎未接入",不伪造译文。
// ★ 文件翻译会话与同传会话同一条规矩:不能搬到项目/别的工作空间(内容只有在本场景里讲得通)。

namespace LocalAI.Client.Services;

/// <summary>一个标注框(归一化坐标 0..1,相对原图 —— 缩放窗口不跑偏)。</summary>
public sealed record MarkBox(double X, double Y, double W, double H);

/// <summary>一个文件翻译文档:原文件 + 标注框。译文输出等引擎(P4)。</summary>
public sealed record FileDoc(string Path, List<MarkBox> Boxes, string? Cache = null);

public sealed class FileTransState
{
    public event Action? Changed;

    /// <summary>支持的输入格式(用户裁定):PNG / JPG / PDF。</summary>
    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".pdf" };
    public static bool Supported(string path)
        => Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    // 会话 id -> 文档。会话删除时的清理由界面层触发(与同传同一口径:记录归 ChatCenter 管)。
    readonly Dictionary<string, FileDoc> _docs = new(StringComparer.Ordinal);

    /// <summary>标注框工具是否处于【正在画框】状态(工具栏切换;不落盘)。</summary>
    public bool BoxTool { get; private set; }
    public void SetBoxTool(bool on) { if (BoxTool != on) { BoxTool = on; Changed?.Invoke(); } }

    /// <summary>实时翻译预览。★ 默认关(用户裁定),且【不落盘】—— 每次进来都从关开始。</summary>
    public bool RealtimePreview { get; private set; }
    public void SetRealtimePreview(bool on) { if (RealtimePreview != on) { RealtimePreview = on; Changed?.Invoke(); } }

    /// <summary>引擎未接入(P4)之前恒 false —— 工具栏据此如实解释,不做假动作。</summary>
    public static bool EngineReady => false;

    public FileDoc? DocOf(string? sessionId)
        => sessionId is not null && _docs.TryGetValue(sessionId, out var d) ? d : null;

    /// <summary>缓存目录:导入时把源文件复制一份进来。</summary>
    public static string CacheDir => System.IO.Path.Combine(AppPaths.StateDir, "filetrans");

    // ★★ 导入即复制副本(裁定 2026-08-02,D59 四补):会话本身就是记录 ——
    //   用户导入一张图、回头清理了下载文件夹,这个会话不该跟着死(框指着空气、译文成孤儿)。
    //   剪贴板截图早已是同一条纪律(粘贴即落 clips\)。代价是占一份磁盘,如实认;
    //   源文件还在就优先用源(用户改了源文件能看到最新的),源没了退回副本并在界面说明。
    public void SetFile(string sessionId, string path)
    {
        string? cache = null;
        try
        {
            System.IO.Directory.CreateDirectory(CacheDir);
            cache = System.IO.Path.Combine(CacheDir, sessionId + System.IO.Path.GetExtension(path).ToLowerInvariant());
            System.IO.File.Copy(path, cache, overwrite: true);
        }
        catch { cache = null; }   // 复制失败就没有副本 —— 界面在源丢失时如实说"副本也没有"
        _docs[sessionId] = new FileDoc(path, _docs.TryGetValue(sessionId, out var old) ? old.Boxes : new List<MarkBox>(), cache);
        Changed?.Invoke();
    }

    /// <summary>真正可读的文件:源还在用源,没了退回导入时的副本;都没有返回 null。</summary>
    public static string? ReadablePath(FileDoc d)
        => System.IO.File.Exists(d.Path) ? d.Path
         : d.Cache is not null && System.IO.File.Exists(d.Cache) ? d.Cache : null;

    public void AddBox(string sessionId, MarkBox box)
    {
        if (!_docs.TryGetValue(sessionId, out var d)) return;
        d.Boxes.Add(box);
        Changed?.Invoke();
    }

    /// <summary>当前选中的框(下标;不落盘)。点选删除、清单高亮都认它。</summary>
    public int? SelectedBox { get; private set; }
    public void SelectBox(int? i) { if (SelectedBox != i) { SelectedBox = i; Changed?.Invoke(); } }

    /// <summary>删除指定框(点选删除/清单删除)。</summary>
    public bool RemoveBox(string sessionId, int index)
    {
        if (!_docs.TryGetValue(sessionId, out var d) || index < 0 || index >= d.Boxes.Count) return false;
        d.Boxes.RemoveAt(index);
        SelectedBox = null;   // 下标已经变了,留着旧选中会删错框
        Changed?.Invoke();
        return true;
    }

    /// <summary>改一个框(拖角标移动 / 拉边调大小,松手时提交)。</summary>
    public void UpdateBox(string sessionId, int index, MarkBox box)
    {
        if (!_docs.TryGetValue(sessionId, out var d) || index < 0 || index >= d.Boxes.Count) return;
        d.Boxes[index] = box;
        Changed?.Invoke();
    }

    /// <summary>清空全部框。</summary>
    public void ClearBoxes(string sessionId)
    {
        if (!_docs.TryGetValue(sessionId, out var d) || d.Boxes.Count == 0) return;
        d.Boxes.Clear();
        SelectedBox = null;
        Changed?.Invoke();
    }

    /// <summary>输出模式(用户裁定留位):替换原文排版 / 原文下加译文的双语对照。
    /// ★ 引擎未接入,这只是【偏好】—— 界面如实标注"接入后生效"。</summary>
    public bool BilingualOutput { get; private set; }
    public void SetBilingualOutput(bool on) { if (BilingualOutput != on) { BilingualOutput = on; Changed?.Invoke(); } }

    /// <summary>行政翻译(用户追加):证件/公文那种正式语体与固定套语;关 = 普通翻译。
    /// ★ 同样只是【偏好】,引擎接入(P4)后生效。</summary>
    public bool OfficialStyle { get; private set; }
    public void SetOfficialStyle(bool on) { if (OfficialStyle != on) { OfficialStyle = on; Changed?.Invoke(); } }

    /// <summary>撤回:去掉最后一个框。没有可撤的返回 false(按钮据此说"没有可撤回的")。</summary>
    public bool UndoBox(string sessionId)
    {
        if (!_docs.TryGetValue(sessionId, out var d) || d.Boxes.Count == 0) return false;
        d.Boxes.RemoveAt(d.Boxes.Count - 1);
        Changed?.Invoke();
        return true;
    }

    /// <summary>会话没了,它的文档【和缓存副本】一起清掉。</summary>
    public void Drop(string sessionId)
    {
        if (_docs.TryGetValue(sessionId, out var d) && d.Cache is not null)
            try { System.IO.File.Delete(d.Cache); } catch { }
        if (_docs.Remove(sessionId)) Changed?.Invoke();
    }

    // ---- 存档(文件路径 + 标注框;工具态/预览开关不落盘)----
    public Dictionary<string, FileDoc> Export() => new(_docs);
    public void Import(Dictionary<string, FileDoc>? saved)
    {
        if (saved is null) return;
        _docs.Clear();
        foreach (var kv in saved)
            if (kv.Value is { Path.Length: > 0 })
                _docs[kv.Key] = kv.Value with { Boxes = kv.Value.Boxes ?? new List<MarkBox>() };
        Changed?.Invoke();
    }
}
