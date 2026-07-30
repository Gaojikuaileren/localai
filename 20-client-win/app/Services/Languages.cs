// P3c -- 翻译工作空间的语言表与语种检测。
//
// 用户裁定(2026-07-30):
//   · 常用语言池 → 拖拽到【目标语言池】(上限 3);
//   · 输入什么语言,就翻译成目标池里【除它以外】的所有语言;
//   · 若输入的语种【不在池中】:先检测它是什么语,把它加进目标池,再翻成池内其它语言。
//
// ★ 诚实的语种检测:这里只做【字符集判断】—— 汉字/假名/谚文/西里尔/拉丁 是可以确定的。
//   但【拉丁字母内部】(英语 vs 德语 vs 法语…)靠字符集分不清:
//     - 出现 ä/ö/ü/ß 之类只能算"线索",不是证据(英文引文里也可能出现);
//   所以拉丁文本一律返回 Unknown,由【AI 接入后】判定 —— 绝不用蹩脚规则瞎猜一个语种,
//   因为猜错的后果是"翻译成了你没要的语言",比不猜更糟。

using System.Globalization;

namespace LocalAI.Client.Services;

public sealed record Lang(string Code, string Name, string Native);

public static class Languages
{
    /// <summary>常用语言池的默认内容(用户可在界面上增删)。</summary>
    public static readonly Lang[] Common =
    {
        new("zh", "中文", "中文"),
        new("ja", "日语", "日本語"),
        new("en", "英语", "English"),
        new("de", "德语", "Deutsch"),
        new("ko", "韩语", "한국어"),
        new("fr", "法语", "Français"),
        new("es", "西班牙语", "Español"),
        new("ru", "俄语", "Русский"),
    };

    public static Lang? Find(string code) => Common.FirstOrDefault(l => l.Code == code);

    public static string NameOf(string code) => Find(code)?.Name ?? code;

    /// <summary>目标语言池上限(用户裁定:最多 3 个)。</summary>
    public const int MaxTargets = 3;

    /// <summary>
    /// 按【字符集】检测语种。只在能确定时返回语言码,否则返回 null(交给 AI 判定)。
    /// ★ 拉丁字母一律返回 null —— 见文件头的说明。
    /// </summary>
    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        bool hasHan = false, hasKana = false, hasHangul = false, hasCyrillic = false, hasLatin = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsDigit(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch)) continue;
            if (ch >= 0x3040 && ch <= 0x30FF) hasKana = true;              // 平假名 + 片假名
            else if (ch >= 0xAC00 && ch <= 0xD7AF) hasHangul = true;       // 谚文音节
            else if (ch >= 0x4E00 && ch <= 0x9FFF) hasHan = true;          // 汉字(中日共用)
            else if (ch >= 0x0400 && ch <= 0x04FF) hasCyrillic = true;     // 西里尔
            else if (char.IsLetter(ch) && ch < 0x0250) hasLatin = true;    // 拉丁(含带变音符的)
        }

        if (hasHangul) return "ko";
        if (hasKana) return "ja";      // ★ 有假名就是日语(汉字中日共用,假名是决定性证据)
        if (hasHan) return "zh";       // 只有汉字没有假名 -> 中文
        if (hasCyrillic) return "ru";
        _ = hasLatin;                  // ★ 拉丁字母内部分不清,交给 AI —— 不瞎猜
        return null;
    }

    /// <summary>
    /// 算出这次要翻成哪些语言:目标池里【除输入语种以外】的全部。
    /// 输入语种不在池中(或检测不出)时,由调用方决定是否先把它加进池(见 TranslationState)。
    /// </summary>
    public static List<string> TargetsFor(IEnumerable<string> pool, string? inputLang)
        => pool.Where(c => inputLang is null || c != inputLang).Distinct().ToList();
}

/// <summary>
/// 翻译详细程度(用户裁定的四档,由简到全)。★ 每一档对应一个【固定格式】——
/// 学习笔记就是按这个格式存的,所以格式必须是数据结构,而不是一段自由文本。
/// </summary>
public enum TranslationLevel
{
    /// <summary>只给译文 —— 最快。</summary>
    Plain = 0,
    /// <summary>译文 + 读音标注(假名/罗马音/拼音/音标)。</summary>
    Pronunciation = 1,
    /// <summary>译文 + 读音 + 一个例句(例句也带读音)。</summary>
    Example = 2,
    /// <summary>译文 + 读音 + 例句 + 逐词详解(词性/释义/用法)。</summary>
    Detailed = 3,
}

public static class TranslationLevels
{
    public static readonly (TranslationLevel Level, string Name, string Desc)[] All =
    {
        (TranslationLevel.Plain,         "精简", "只给译文,最快"),
        (TranslationLevel.Pronunciation, "带读音", "译文 + 读音标注"),
        (TranslationLevel.Example,       "带例句", "译文 + 读音 + 一个例句"),
        (TranslationLevel.Detailed,      "详解",   "译文 + 读音 + 例句 + 逐词详解"),
    };

    public static string NameOf(TranslationLevel l) => All.First(x => x.Level == l).Name;
    public static string DescOf(TranslationLevel l) => All.First(x => x.Level == l).Desc;

    /// <summary>该档位要求 AI 产出哪些字段 —— 接入后作为 prompt 契约,现在作为笔记的格式约束。</summary>
    public static string[] FieldsOf(TranslationLevel l) => l switch
    {
        TranslationLevel.Plain => new[] { "译文" },
        TranslationLevel.Pronunciation => new[] { "译文", "读音" },
        TranslationLevel.Example => new[] { "译文", "读音", "例句" },
        _ => new[] { "译文", "读音", "例句", "逐词详解" },
    };
}
