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
    /// <summary>
    /// 全部支持的语言(设置里"添加语言"从这里选)。★ 常用语言池【默认只放中/日/英/德/韩】(用户裁定),
    /// 其余留在目录里,想用再到设置里加 —— 池子太长反而不好拖。
    /// </summary>
    public static readonly Lang[] Catalog =
    {
        new("zh", "中文", "中文"),
        new("ja", "日语", "日本語"),
        new("en", "英语", "English"),
        new("de", "德语", "Deutsch"),
        new("ko", "韩语", "한국어"),
        new("fr", "法语", "Français"),
        new("es", "西班牙语", "Español"),
        new("ru", "俄语", "Русский"),
        new("it", "意大利语", "Italiano"),
        new("pt", "葡萄牙语", "Português"),
    };

    /// <summary>默认的常用语言池(用户裁定:只要这五个)。</summary>
    public static readonly string[] DefaultPool = { "zh", "ja", "en", "de", "ko" };

    public static Lang? Find(string code) => Catalog.FirstOrDefault(l => l.Code == code);

    public static string NameOf(string code) => Find(code)?.Name ?? code;

    /// <summary>
    /// 该语言是否用拉丁字母书写。★ 决定"第二阶给读音还是给词根"(用户裁定):
    /// 英语德语这种拉丁文字标读音没意义,该给的是词源与构词。
    /// </summary>
    public static bool IsLatinScript(string code) => code switch
    {
        "zh" or "ja" or "ko" or "ru" => false,
        _ => true,
    };

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
/// <summary>
/// 翻译详细程度,逐级累加(用户裁定 2026-07-30 第三轮):
///   直译 → 读音/词根 → 例句 → 语法。
/// ★ 第二阶【随目标语言变】:中日韩这类非拉丁文字要的是【读音】(假名/拼音/罗马音);
///   英语德语这类拉丁文字不需要读音 —— 要的是【词根】(词源与构词),那才是学习价值所在。
/// </summary>
public enum TranslationLevel
{
    /// <summary>只给译文 —— 最快。</summary>
    Plain = 0,
    /// <summary>译文 + 读音(非拉丁语言)/ 词根(拉丁语言)。</summary>
    Reading = 1,
    /// <summary>再加一个例句。</summary>
    Example = 2,
    /// <summary>再加语法:时态、人称变位、格与词序等。</summary>
    Grammar = 3,
}

public static class TranslationLevels
{
    /// <summary>第二阶在【拉丁文字】语言下的叫法 —— 读音换成词根。</summary>
    public const string ReadingLabel = "读音";
    public const string RootLabel = "词根";

    public static readonly (TranslationLevel Level, string Name, string Desc)[] All =
    {
        (TranslationLevel.Plain,   "直译", "只给译文,最快"),
        (TranslationLevel.Reading, "读音", "译文 + 读音标注;拉丁文字语言(英/德…)不标读音,改给词根"),
        (TranslationLevel.Example, "例句", "再加一个例句"),
        (TranslationLevel.Grammar, "语法", "再加语法:时态、人称变位、格与词序等"),
    };

    public static string NameOf(TranslationLevel l) => All.First(x => x.Level == l).Name;
    public static string DescOf(TranslationLevel l) => All.First(x => x.Level == l).Desc;

    /// <summary>
    /// 第二阶对某个目标语言该叫什么:拉丁文字 → 词根,其余 → 读音。
    /// </summary>
    public static string SecondStageFor(string langCode) => Languages.IsLatinScript(langCode) ? RootLabel : ReadingLabel;

    /// <summary>
    /// 第二阶在【当前目标池】下的显示名。全是拉丁 → 词根;全非拉丁 → 读音;混着 → 两个都写。
    /// ★ 档位是全局一个,但它对每种语言的含义不同 —— 界面上如实写出来,别只写一个骗人。
    /// </summary>
    public static string SecondStageLabel(IEnumerable<string> targetCodes)
    {
        var codes = targetCodes.ToList();
        if (codes.Count == 0) return $"{ReadingLabel} / {RootLabel}";
        var latin = codes.Count(Languages.IsLatinScript);
        if (latin == 0) return ReadingLabel;
        if (latin == codes.Count) return RootLabel;
        return $"{ReadingLabel} / {RootLabel}";
    }

    /// <summary>
    /// 该档位要求 AI 对【某个目标语言】产出哪些字段 —— 接入后作为 prompt 契约,
    /// 现在作为笔记的格式约束。第二阶按语言在读音/词根之间切换。
    /// </summary>
    public static string[] FieldsOf(TranslationLevel l, string? langCode = null)
    {
        var second = langCode is null ? ReadingLabel : SecondStageFor(langCode);
        return l switch
        {
            TranslationLevel.Plain => new[] { "译文" },
            TranslationLevel.Reading => new[] { "译文", second },
            TranslationLevel.Example => new[] { "译文", second, "例句" },
            _ => new[] { "译文", second, "例句", "语法" },
        };
    }
}
