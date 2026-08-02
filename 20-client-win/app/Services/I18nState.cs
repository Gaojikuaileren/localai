// P3c -- 翻译第四场景【多语言表】(用户裁定 2026-08-02,D60):
//   给 AI 辅助的游戏/应用开发者做多语言系统:导入键值表(JSON),翻成多语言对照,
//   导出「一源两出」:对照表 strings.i18n.json(真相源,AI 读上下文用)+ 每语言平铺 json(引擎直接吃)。
//
// ★ 语言【不限量】(用户裁定):不复用目标池(那边限 3)。源语言一个 + 目标语言 chips 无上限。
// ★ 三条硬规则(「AI 直接读不出错」的真正含义):
//   ① 严格合法 JSON(UTF-8 无 BOM、无尾逗号);② 键序排序输出(diff 干净);
//   ③ 占位符校验 —— {0}/{name}/%s 必须在译文里原样存活,坏的拒绝导出。
// ★ 诚实:翻译引擎未接(P4)。译文列现在由人工/外部 AI 粘贴填入 —— 工具本身已可用;
//   「翻译缺失项」按钮按下如实说引擎未接。

using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAI.Client.Services;

/// <summary>一条词条:键 + 源文 + 各语言译文(langCode -> text)。</summary>
public sealed record I18nEntry(string Key, string Source, Dictionary<string, string> Trans);

public sealed class I18nDoc
{
    public string SourceLang { get; set; } = "zh";
    /// <summary>导入的 JSON 文件路径(界面顶部显示,可选中复制;手建的表为空)。</summary>
    public string? SourcePath { get; set; }
    public List<string> TargetLangs { get; set; } = new();          // ★ 不限量
    public List<I18nEntry> Entries { get; set; } = new();
}

public sealed class I18nState
{
    public event Action? Changed;
    /// <summary>导入/首次编辑时新建了会话 -> 请界面选中它(与 FileTransState 同款)。</summary>
    public event Action<string>? FocusSession;

    // ★ 会话化(D60 八补,用户裁定):一个 JSON 文件对应一个【JSON 译表会话】。
    //   Docs 按会话存;没绑会话时先落在草稿上,一旦发生真实编辑就当场建会话并把草稿挂过去。
    readonly Dictionary<string, I18nDoc> _docs = new(StringComparer.Ordinal);
    I18nDoc _scratch = new();

    public string? SessionId { get; private set; }
    public void SetSession(string? sid)
    {
        if (SessionId == sid) return;
        SessionId = sid;
        RawMode = false;   // 换会话不该带着上一个的源码视图
        Changed?.Invoke();
    }

    public I18nDoc Doc => SessionId is { } sid
        ? (_docs.TryGetValue(sid, out var d) ? d : _docs[sid] = new I18nDoc())
        : _scratch;

    /// <summary>第一笔真实编辑(导入/加词条/加语言)时建会话 —— 编辑必须有会话记录(用户裁定)。</summary>
    void EnsureSession(string? titleHint = null)
    {
        if (SessionId is not null) return;
        var app = (LocalAI.Client.App)System.Windows.Application.Current;
        var sess = app.Chat.NewSession(null, "translation", ProjectScope.Personal,
            $"JSON 译表 · {titleHint ?? "未命名"} · {DateTime.Now:M月d日 HH:mm}", i18nTable: true);
        _docs[sess.SessionId] = _scratch;
        _scratch = new I18nDoc();
        SessionId = sess.SessionId;
        FocusSession?.Invoke(sess.SessionId);
    }
    public string? SelectedKey { get; private set; }
    public void SelectKey(string? k) { if (SelectedKey != k) { SelectedKey = k; Changed?.Invoke(); } }
    public void Touch() => Changed?.Invoke();

    public void AddLang(string code)
    {
        EnsureSession();
        if (string.IsNullOrWhiteSpace(code) || code == Doc.SourceLang || Doc.TargetLangs.Contains(code)) return;
        Doc.TargetLangs.Add(code); Changed?.Invoke();
    }
    public void RemoveLang(string code) { if (Doc.TargetLangs.Remove(code)) Changed?.Invoke(); }

    /// <summary>手建词条(空表也能开工,用户裁定 2026-08-03):键非空且唯一;保持键序。</summary>
    public bool AddEntry(string key)
    {
        EnsureSession(key);
        key = key.Trim();
        if (key.Length == 0 || Doc.Entries.Any(e => e.Key == key)) return false;
        Doc.Entries.Add(new I18nEntry(key, "", new()));
        Doc.Entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        Changed?.Invoke();
        return true;
    }

    /// <summary>改键名(用户裁定 2026-08-03:键也可编辑)。空/重复返回 false,什么都不动。</summary>
    public bool RenameKey(string oldKey, string newKey)
    {
        newKey = newKey.Trim();
        var i = Doc.Entries.FindIndex(e => e.Key == oldKey);
        if (i < 0 || newKey.Length == 0 || Doc.Entries.Any(e => e.Key == newKey)) return false;
        Doc.Entries[i] = Doc.Entries[i] with { Key = newKey };
        Doc.Entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        Changed?.Invoke();
        return true;
    }

    public void SetSourceLang(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || Doc.SourceLang == code) return;
        Doc.TargetLangs.Remove(code);   // 不能既是源又是目标
        Doc.SourceLang = code;
        Changed?.Invoke();
    }

    /// <summary>全球使用者占比(含二语,占全球人口,粗粒度)。★ 静态数据(用户裁定),不联网、不装实时。</summary>
    static readonly Dictionary<string, double> _pct = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = 18.0, ["zh"] = 17.0, ["hi"] = 7.5, ["es"] = 7.0, ["ar"] = 5.0, ["fr"] = 4.0,
        ["bn"] = 3.5, ["pt"] = 3.4, ["ru"] = 3.2, ["ur"] = 3.0, ["id"] = 2.5, ["de"] = 1.7,
        ["ja"] = 1.5, ["vi"] = 1.1, ["tr"] = 1.1, ["ko"] = 1.0, ["it"] = 0.8, ["th"] = 0.8,
        ["pl"] = 0.5, ["nl"] = 0.4, ["uk"] = 0.5, ["el"] = 0.2, ["sv"] = 0.2, ["cs"] = 0.2,
    };
    public static double PercentValue(string code) => _pct.TryGetValue(code, out var v) ? v : -1;
    public static string PercentOf(string code)
        => _pct.TryGetValue(code, out var v) ? v.ToString("0.#") + "%" : "—";

    /// <summary>
    /// 「复制 Prompt」的用途(用户更正 2026-08-03):给【项目里的开发 AI】的建表指令 ——
    /// "本项目开始多语言开发,请把需要翻译的字符提取成这个格式的 JSON 表"。
    /// 不是"翻译这张表"(翻译是本地引擎/翻译缺失项的活)。已有词条时附上,要求增量不改旧键。
    /// </summary>
    public string PromptText()
    {
        var head =
            "本项目将开始多语言(i18n)开发。请扫描本项目,把所有【用户可见】的字符串(UI 文案、按钮、提示、错误信息等)提取出来,制作成一个 JSON 词条表。只输出 JSON,不要任何解释文字。格式:\n"
          + "{ \"键名\": { \"@src\": \"" + Doc.SourceLang + "\", \"" + Doc.SourceLang + "\": \"源文\" } }\n"
          + "硬规则:\n"
          + "1. 严格合法 JSON(UTF-8,双引号,无尾逗号,无注释);\n"
          + "2. 键名用稳定的语义命名(如 menu.start、error.network),不用序号,不重复;\n"
          + "3. 源文里的变量一律写成占位符({name}、{0}、%s 等),原样保留;\n"
          + "4. 只收用户可见的文案 —— 日志、代码常量、开发者注释不要收;\n"
          + "5. 目标语言不用填,留给后续流程。\n"
          + "源语言:" + Doc.SourceLang;
        if (Doc.Entries.Count > 0)
            head += "\n已有词条表如下 —— 【增量补充】,不要改动或删除已有键:\n" + ToTableJson();
        return head;
    }

    /// <summary>导入键值 JSON(平铺 {"key":"文案"} 或对照表)。返回读入条数,-1 = 解析失败。</summary>
    public int ImportJson(string json)
    {
        EnsureSession();
        try
        {
            using var d = JsonDocument.Parse(json);
            var list = new List<I18nEntry>();
            foreach (var pj in d.RootElement.EnumerateObject())
            {
                if (pj.Value.ValueKind == JsonValueKind.String)
                    list.Add(new I18nEntry(pj.Name, pj.Value.GetString() ?? "", new()));
                else if (pj.Value.ValueKind == JsonValueKind.Object)
                {
                    var src = ""; var tr = new Dictionary<string, string>();
                    foreach (var q in pj.Value.EnumerateObject())
                        if (q.Name == "@src" || q.Name == Doc.SourceLang) src = q.Value.GetString() ?? src;
                        else if (q.Value.ValueKind == JsonValueKind.String) tr[q.Name] = q.Value.GetString() ?? "";
                    list.Add(new I18nEntry(pj.Name, src, tr));
                }
            }
            if (list.Count == 0) return 0;
            Doc.Entries = list.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();   // 键序稳定
            Changed?.Invoke();
            return list.Count;
        }
        catch { return -1; }
    }

    /// <summary>占位符提取:{xx} / %s %d / %1$s。译文必须原样带全 —— 本地化最常见的炸点。</summary>
    public static string[] Placeholders(string s)
        => Regex.Matches(s, @"\{[^{}]*\}|%\d+\$[sd]|%[sd]").Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>这条译文占位符是否完好。空译文不算坏(是"缺",不是"错")。</summary>
    public static bool PlaceholdersOk(string source, string trans)
        => trans.Length == 0 || Placeholders(source).SequenceEqual(Placeholders(trans));

    /// <summary>某键的完成度:(已填且占位符完好, 目标语言数)。</summary>
    public (int Done, int Total) Progress(I18nEntry e)
        => (Doc.TargetLangs.Count(l => e.Trans.TryGetValue(l, out var t) && t.Length > 0 && PlaceholdersOk(e.Source, t)),
            Doc.TargetLangs.Count);

    /// <summary>
    /// 导出【单个完整 JSON】(用户裁定 2026-08-03,推翻先前的一源两出):
    /// 就是对照表本体 —— 所有语言都在里面,给 AI 直接读。占位符坏的仍【拒绝导出】。
    /// </summary>
    public (bool Ok, string Msg) Export(string filePath)
    {
        var bad = new List<string>();
        foreach (var e in Doc.Entries)
            foreach (var l in Doc.TargetLangs)
                if (e.Trans.TryGetValue(l, out var t) && !PlaceholdersOk(e.Source, t))
                    bad.Add($"{e.Key} [{l}]");
        if (bad.Count > 0)
            return (false, "占位符校验不过,拒绝导出(AI 读了会错):\n" + string.Join("、", bad.Take(8)) + (bad.Count > 8 ? $" 等 {bad.Count} 处" : ""));
        try
        {
            System.IO.File.WriteAllText(filePath, ToTableJson());   // UTF-8 无 BOM
            return (true, "已导出 -> " + filePath);
        }
        catch (Exception ex) { return (false, "导出失败:" + ex.Message); }
    }

    /// <summary>源码视图(底条按钮开/应用,面板显示编辑器)。</summary>
    public bool RawMode { get; private set; }
    public string RawText { get; set; } = "";
    public void SetRawMode(bool on) { if (RawMode != on) { RawMode = on; Changed?.Invoke(); } }

    /// <summary>本地引擎(P4)翻 UI 词条的用词纪律(给引擎,不进复制 Prompt):长度贴近源文,达意优先。</summary>
    public const string UiLengthRule =
        "译文长度尽量与源文相近(词条用于界面,过长会挤坏排版);但达意永远优先,宁可换更短说法,不生造词、不砍含义。";

    /// <summary>当前表序列化成对照 JSON(与导出①同形)—— 进源码视图时的种子。</summary>
    public string ToTableJson()
    {
        var table = new SortedDictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var e in Doc.Entries)
        {
            var row = new Dictionary<string, string> { ["@src"] = Doc.SourceLang, [Doc.SourceLang] = e.Source };
            foreach (var l in Doc.TargetLangs) if (e.Trans.TryGetValue(l, out var t) && t.Length > 0) row[l] = t;
            table[e.Key] = row;
        }
        return System.Text.Json.JsonSerializer.Serialize(table, new System.Text.Json.JsonSerializerOptions
        { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>给界面看的一句状态(导入/导出/应用的结果)。</summary>
    public string StatusLine { get; private set; } = "";
    public bool StatusWarn { get; private set; }
    public void SetStatus(string s, bool warn = false) { StatusLine = s; StatusWarn = warn; Changed?.Invoke(); }

    public Dictionary<string, I18nDoc> ExportDocs() => new(_docs);
    public void Import(Dictionary<string, I18nDoc>? d)
    {
        if (d is null) return;
        _docs.Clear();
        foreach (var kv in d) if (kv.Value is not null) _docs[kv.Key] = kv.Value;
        Changed?.Invoke();
    }
}
