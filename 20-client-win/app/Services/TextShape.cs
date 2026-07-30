// P3c -- 待翻译内容的【形态】,以及它对翻译程度的约束(用户裁定 2026-07-30)。
//
// 四种形态:
//   单词 / 短句 —— 现在这套四档格式正合适;
//   长文本      —— 【禁用语法与例句两档】,自动回退到直译;
//   带格式的长文本(用户附件文件)—— 同长文本,另外要求保留原文的段落/列表结构。
//
// 为什么长文本要禁掉那两档:
//   「例句」是给一个词或一句话另造一个例子 —— 对一整篇文章没有意义,也没法只造一个;
//   「语法」要逐句讲时态与变位 —— 一篇文章逐句讲完,产出比原文还长,读的人根本用不了。
//   这不是"做不到",是"做出来没人要"。所以在【界面上就禁掉】,而不是让 AI 硬答一版废话。
//
// 长文本 + 词解 的语义要改:
//   不是逐词标读音/词根(一篇文章逐词标 = 一堵墙),而是【挑出有价值的重点词】,
//   在译文【末尾】单独列一张表。正文保持干净,生词另起一块 —— 这才是长文本该有的学习姿势。
//
// ★ 纯函数 + 无 UI 依赖:形态判定与档位约束都能在无头自检里逐条钉死。

namespace LocalAI.Client.Services;

public enum TextShape
{
    /// <summary>一个词(或一个紧凑的词组)。</summary>
    Word,
    /// <summary>一句话到几句话。</summary>
    Phrase,
    /// <summary>长文本。</summary>
    LongText,
    /// <summary>用户附件里的长文本 —— 自带段落/列表等结构,翻译时要保留。</summary>
    FormattedLongText,
}

public static class TextShapes
{
    /// <summary>超过这个长度算长文本。中文一屏大约就是这个量级。</summary>
    public const int LongThreshold = 220;
    /// <summary>不超过这个长度、且没有句子分隔,算"一个词"。</summary>
    public const int WordThreshold = 24;

    /// <summary>
    /// 判定形态。<paramref name="fromAttachment"/> = 内容来自用户附件文件 ——
    /// 那种情况【一律】按带格式的长文本处理,不看长度:附件哪怕只有两行,
    /// 它的段落/项目符号也是用户自己排的,不该被翻译顺手抹平。
    /// </summary>
    public static TextShape Classify(string? text, bool fromAttachment = false)
    {
        var t = (text ?? "").Trim();
        if (fromAttachment) return TextShape.FormattedLongText;
        if (t.Length == 0) return TextShape.Word;

        // 明显的多段/列表结构 -> 当带格式的长文本
        if (t.Contains('\n') && t.Length > WordThreshold) return TextShape.FormattedLongText;
        if (t.Length > LongThreshold) return TextShape.LongText;

        var hasBreak = t.IndexOfAny(new[] { ' ', '　', ',', ',', '。', '.', '!', '!', '?', '?', ';', ';' }) >= 0;
        return t.Length <= WordThreshold && !hasBreak ? TextShape.Word : TextShape.Phrase;
    }

    public static bool IsLong(TextShape s) => s is TextShape.LongText or TextShape.FormattedLongText;

    /// <summary>这一档在这种形态下能不能用。</summary>
    public static bool Allows(TextShape shape, TranslationLevel level)
        => !IsLong(shape) || level is TranslationLevel.Plain or TranslationLevel.Reading;

    /// <summary>
    /// 实际生效的档位:长文本下,语法/例句【自动回退到直译】(用户裁定)。
    /// 词解保留 —— 但它的含义在长文本下不同(见 FieldsFor)。
    /// </summary>
    public static TranslationLevel Effective(TranslationLevel requested, TextShape shape)
        => Allows(shape, requested) ? requested : TranslationLevel.Plain;

    /// <summary>
    /// 该形态 + 该档位要求 AI 产出哪些字段。接入后作为 prompt 契约,现在作为笔记/展示的格式约束。
    /// ★ 长文本 + 词解 = 译文 + 【末尾】的重点词表,不是逐词标注。
    /// </summary>
    public static string[] FieldsFor(TranslationLevel requested, TextShape shape, string? langCode = null)
    {
        var level = Effective(requested, shape);
        if (!IsLong(shape)) return TranslationLevels.FieldsOf(level, langCode);

        var fields = new List<string> { "译文" };
        if (shape == TextShape.FormattedLongText) fields.Add("保留原文结构");
        if (level == TranslationLevel.Reading) fields.Add("重点词表(附于译文末尾)");
        return fields.ToArray();
    }

    /// <summary>界面上如实告诉用户这次会按什么来 —— 不合适就说清为什么,不闷声改档。</summary>
    public static string? Explain(TranslationLevel requested, TextShape shape)
    {
        if (!IsLong(shape)) return null;
        var structural = shape == TextShape.FormattedLongText ? "(并保留原文的段落结构)" : "";
        return requested switch
        {
            TranslationLevel.Grammar or TranslationLevel.Example =>
                $"这是一段长文本 —— 逐句讲语法或另造例句对整篇没有意义,已按【直译】处理{structural}。",
            TranslationLevel.Reading =>
                $"这是一段长文本 —— 不逐词标注,改为在译文末尾单独列出重点词{structural}。",
            _ => structural.Length > 0 ? "附件内容:翻译时保留原文的段落结构。" : null,
        };
    }
}
