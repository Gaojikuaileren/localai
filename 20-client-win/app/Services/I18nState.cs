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
    public List<string> TargetLangs { get; set; } = new();          // ★ 不限量
    public List<I18nEntry> Entries { get; set; } = new();
}

public sealed class I18nState
{
    public event Action? Changed;
    public I18nDoc Doc { get; private set; } = new();
    public string? SelectedKey { get; private set; }
    public void SelectKey(string? k) { if (SelectedKey != k) { SelectedKey = k; Changed?.Invoke(); } }
    public void Touch() => Changed?.Invoke();

    public void AddLang(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code == Doc.SourceLang || Doc.TargetLangs.Contains(code)) return;
        Doc.TargetLangs.Add(code); Changed?.Invoke();
    }
    public void RemoveLang(string code) { if (Doc.TargetLangs.Remove(code)) Changed?.Invoke(); }

    /// <summary>手建词条(空表也能开工,用户裁定 2026-08-03):键非空且唯一;保持键序。</summary>
    public bool AddEntry(string key)
    {
        key = key.Trim();
        if (key.Length == 0 || Doc.Entries.Any(e => e.Key == key)) return false;
        Doc.Entries.Add(new I18nEntry(key, "", new()));
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

    /// <summary>给别的 AI 的格式说明(复制按钮用)—— 让对方产出我们能直接吃的对照 JSON。</summary>
    public string PromptText()
        => "请把下面的多语言词条表补全/翻译,并按【完全相同的 JSON 结构】返回,不要输出任何解释文字:\n"
         + "{ \"键名\": { \"@src\": \"" + Doc.SourceLang + "\", \"" + Doc.SourceLang + "\": \"源文\", \"en\": \"译文\", ... } }\n"
         + "硬规则:\n"
         + "1. 严格合法 JSON(UTF-8,双引号,无尾逗号,无注释);\n"
         + "2. 键名与 @src 原样保留,不增删改任何键;\n"
         + "3. 源文中的占位符({name}、{0}、%s、%1$s 等)必须在每种译文中【原样】出现;\n"
         + "4. 没把握的语言留空字符串 \"\",不要编造。\n"
         + "目标语言:" + string.Join("、", Doc.TargetLangs) + "\n以下是词条表:\n" + ToTableJson();

    /// <summary>导入键值 JSON(平铺 {"key":"文案"} 或对照表)。返回读入条数,-1 = 解析失败。</summary>
    public int ImportJson(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var list = new List<I18nEntry>();
            foreach (var p in d.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    list.Add(new I18nEntry(p.Name, p.Value.GetString() ?? "", new()));
                else if (p.Value.ValueKind == JsonValueKind.Object)
                {
                    var src = ""; var tr = new Dictionary<string, string>();
                    foreach (var q in p.Value.EnumerateObject())
                        if (q.Name == "@src" || q.Name == Doc.SourceLang) src = q.Value.GetString() ?? src;
                        else if (q.Value.ValueKind == JsonValueKind.String) tr[q.Name] = q.Value.GetString() ?? "";
                    list.Add(new I18nEntry(p.Name, src, tr));
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
    /// 导出「一源两出」到目录。占位符坏的【拒绝导出】并列出坏在哪(硬规则③)。
    /// 返回 (成功, 消息)。
    /// </summary>
    public (bool Ok, string Msg) Export(string dir)
    {
        var bad = new List<string>();
        foreach (var e in Doc.Entries)
            foreach (var l in Doc.TargetLangs)
                if (e.Trans.TryGetValue(l, out var t) && !PlaceholdersOk(e.Source, t))
                    bad.Add($"{e.Key} [{l}]");
        if (bad.Count > 0)
            return (false, "占位符校验不过,拒绝导出(AI/引擎读了会炸):\n" + string.Join("、", bad.Take(8)) + (bad.Count > 8 ? $" 等 {bad.Count} 处" : ""));
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            var opt = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            // ① 对照表(真相源)
            var table = new SortedDictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var e in Doc.Entries)
            {
                var row = new Dictionary<string, string> { ["@src"] = Doc.SourceLang, [Doc.SourceLang] = e.Source };
                foreach (var l in Doc.TargetLangs) if (e.Trans.TryGetValue(l, out var t) && t.Length > 0) row[l] = t;
                table[e.Key] = row;
            }
            // ★ UTF-8 无 BOM(硬规则①):File.WriteAllText 默认 UTF8 无 BOM
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "strings.i18n.json"), JsonSerializer.Serialize(table, opt));
            // ② 每语言平铺(引擎直接吃);源语言也出一份
            foreach (var l in Doc.TargetLangs.Prepend(Doc.SourceLang).Distinct())
            {
                var flat = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var e in Doc.Entries)
                {
                    var v = l == Doc.SourceLang ? e.Source : e.Trans.GetValueOrDefault(l, "");
                    if (v.Length > 0) flat[e.Key] = v;
                }
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, l + ".json"), JsonSerializer.Serialize(flat, opt));
            }
            return (true, $"已导出:对照表 + {Doc.TargetLangs.Count + 1} 个语言文件 -> {dir}");
        }
        catch (Exception ex) { return (false, "导出失败:" + ex.Message); }
    }

    /// <summary>源码视图(底条按钮开/应用,面板显示编辑器 —— 状态放这儿两边才对得上)。</summary>
    public bool RawMode { get; private set; }
    public string RawText { get; set; } = "";
    public void SetRawMode(bool on) { if (RawMode != on) { RawMode = on; Changed?.Invoke(); } }

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

    public I18nDoc ExportDoc() => Doc;
    public void Import(I18nDoc? d) { if (d is null) return; Doc = d; Changed?.Invoke(); }
}
