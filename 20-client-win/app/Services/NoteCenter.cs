// P3c -- 学习笔记(翻译工作空间的收藏)。
//
// 用户裁定(2026-07-30):
//   · AI 的翻译回复右侧有【收藏】按钮,按下存进学习笔记;
//   · 笔记【按语言分类】;★ 一次翻成多语言时,要【拆开】分别存进各自目标语言的笔记里;
//   · 可删除,也可以【在格式内编辑】—— 所以笔记必须是【结构化字段】,不是一坨自由文本;
//     字段由当时的"翻译程度"档位决定(见 TranslationLevels.FieldsOf)。
//
// ★ 诚实:译文/读音/例句/逐词详解都要 AI 生成(P4 未接入)。在那之前笔记可以【手动新建与编辑】,
//   但不会凭空冒出 AI 写的内容。

namespace LocalAI.Client.Services;

/// <summary>逐词详解里的一条。</summary>
public sealed record WordGloss(string Word, string Reading, string Pos, string Meaning);

/// <summary>
/// 一条学习笔记 = 一个【目标语言】下的一条翻译结果。多语言翻译会拆成多条(每种语言一条)。
/// 字段按 Level 决定填哪些;没有的留空,界面按档位显示。
/// </summary>
public sealed record StudyNote(
    string Id,
    string Lang,                  // ★ 目标语言码 —— 笔记按它分类
    string SourceText,            // 原文
    string? SourceLang,           // 原文语种(检测或 AI 判定;可空)
    string Translation,           // 译文
    TranslationLevel Level,
    string? Reading = null,       // 读音标注
    string? ExampleSource = null, // 例句(目标语言)
    string? ExampleTranslation = null,
    string? ExampleReading = null,
    IReadOnlyList<WordGloss>? Words = null,   // 逐词详解
    DateTime? CreatedAt = null,
    bool CreatedByAi = false);    // 是 AI 生成后收藏的,还是手动建的

public sealed class NoteCenter
{
    readonly List<StudyNote> _items = new();

    public IReadOnlyList<StudyNote> Items => _items;

    public event Action? Changed;

    public static string NewId() => "note-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>笔记里出现过的语言(按条数多的在前)—— 界面据此分类。</summary>
    public IEnumerable<(string Lang, int Count)> LanguagesUsed()
        => _items.GroupBy(n => n.Lang).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2);

    public IEnumerable<StudyNote> Of(string lang)
        => _items.Where(n => n.Lang == lang).OrderByDescending(n => n.CreatedAt ?? DateTime.MinValue);

    public int Count(string lang) => _items.Count(n => n.Lang == lang);

    public string Add(StudyNote n)
    {
        var it = n with
        {
            Id = string.IsNullOrEmpty(n.Id) ? NewId() : n.Id,
            CreatedAt = n.CreatedAt ?? DateTime.Now,
        };
        _items.Add(it);
        Changed?.Invoke();
        return it.Id;
    }

    /// <summary>
    /// ★ 收藏一次【多语言】翻译:按目标语言拆成多条分别存(用户裁定)。返回新建的条数。
    /// 每条只带它自己那门语言的内容 —— 这样按语言浏览时不会混进别的语言。
    /// </summary>
    public int AddSplit(IEnumerable<StudyNote> perLanguage)
    {
        var n = 0;
        foreach (var note in perLanguage)
        {
            if (string.IsNullOrWhiteSpace(note.Lang) || string.IsNullOrWhiteSpace(note.Translation)) continue;
            Add(note);
            n++;
        }
        return n;
    }

    public void Update(StudyNote n)
    {
        var i = _items.FindIndex(x => x.Id == n.Id);
        if (i < 0) return;
        _items[i] = n;
        Changed?.Invoke();
    }

    public void Remove(string id) { if (_items.RemoveAll(x => x.Id == id) > 0) Changed?.Invoke(); }

    public void RemoveMany(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        if (set.Count > 0 && _items.RemoveAll(x => set.Contains(x.Id)) > 0) Changed?.Invoke();
    }

    // ---------------------------------------------------------------- 存档(明文,D50 口径)
    public List<StudyNote> Export() => _items.ToList();

    public void Import(List<StudyNote>? items)
    {
        if (items is null) return;
        _items.Clear();
        _items.AddRange(items);
        Changed?.Invoke();
    }
}
