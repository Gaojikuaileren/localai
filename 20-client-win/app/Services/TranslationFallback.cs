// P3c -- 目标池算不出目标时的【级联兜底】(用户裁定 2026-07-30)。
//
// 触发条件:按"翻成池内除输入语言以外的全部"算出来是【空集】。
//   最常见的形态就是池里只有一个语言,而你输入的正是它。
//
// 级联(依次判断,命中即止):
//   ① 母语 ≠ 输入语言        -> 翻成【母语】,并把母语加进目标池;
//   ② 母语 = 输入语言,但不是英语 -> 翻成【英语】,并把英语加进目标池;
//   ③ 输入 = 目标 = 母语 = 英语  -> 【在对话里问】要翻成什么,
//      把语言池里的语言做成按钮;用户点一个、或者直接回一句语言名之后,
//      按钮置灰,选中的语言进目标池,然后开始翻译。
//
// ★ 为什么抽成纯函数:四层分支,每层都有"加不加进池""翻给谁"两件事,
//   写在界面里必然绕晕,而且无头自检验不了。这里给定输入就能算出结论,一条条钉死。
//
// ★ 语种判不出来(拉丁字母)时【根本不该走到这里】—— 那时我们不知道输入是不是池内那一个,
//   宁可交给 AI 判,也不要凭猜测启动兜底(与 Languages.Detect 同一条纪律)。

namespace LocalAI.Client.Services;

public enum FallbackKind
{
    /// <summary>不需要兜底:本来就有可翻的目标。</summary>
    None,
    /// <summary>翻成母语。</summary>
    Native,
    /// <summary>翻成英语。</summary>
    English,
    /// <summary>在对话里问用户要翻成什么。</summary>
    Ask,
}

/// <param name="Kind">这次该怎么办</param>
/// <param name="AddToPool">要加进目标池的语言码(Ask 时为 null —— 等用户选完才知道)</param>
/// <param name="Options">Ask 时给用户的候选语言(来自语言池,已去掉输入语言与已在目标池的)</param>
public sealed record TranslationFallback(FallbackKind Kind, string? AddToPool, IReadOnlyList<string> Options)
{
    public static readonly TranslationFallback None = new(FallbackKind.None, null, Array.Empty<string>());
}

public static class TranslationFallbacks
{
    public const string English = "en";

    /// <summary>
    /// 算这次该怎么兜底。
    /// </summary>
    /// <param name="targets">按规则算出的目标语言(空 = 需要兜底)</param>
    /// <param name="inputLang">输入语种;null = 没判出来(拉丁字母)-> 一律不兜底</param>
    /// <param name="nativeLang">使用者母语</param>
    /// <param name="pool">语言池(设置里那份),用于 Ask 时列候选</param>
    /// <param name="currentTargets">当前目标池,用于把已选的从候选里去掉</param>
    public static TranslationFallback Resolve(
        IReadOnlyList<string> targets,
        string? inputLang,
        string? nativeLang,
        IReadOnlyList<string> pool,
        IReadOnlyList<string> currentTargets)
    {
        if (targets.Count > 0) return TranslationFallback.None;      // 本来就有目标
        if (string.IsNullOrEmpty(inputLang)) return TranslationFallback.None;   // 语种没判出来 -> 不猜

        // ① 母语和输入语言不同 -> 翻成母语
        if (!string.IsNullOrEmpty(nativeLang) && nativeLang != inputLang)
            return new TranslationFallback(FallbackKind.Native, nativeLang, Array.Empty<string>());

        // ② 母语就是输入语言,但不是英语 -> 翻成英语
        if (inputLang != English)
            return new TranslationFallback(FallbackKind.English, English, Array.Empty<string>());

        // ③ 输入 = 目标 = 母语 = 英语 -> 问用户
        var options = pool
            .Where(c => c != inputLang && !currentTargets.Contains(c))
            .Distinct()
            .ToList();
        return new TranslationFallback(FallbackKind.Ask, null, options);
    }

    /// <summary>兜底方案该怎么跟用户讲(界面把它作为一条系统说明写进会话)。</summary>
    public static string Explain(TranslationFallback f, string inputLang) => f.Kind switch
    {
        FallbackKind.Native =>
            $"目标池里只有{Languages.NameOf(inputLang)},和你输入的是同一种 —— 已按母语翻成{Languages.NameOf(f.AddToPool!)},并把它加进了目标池。",
        FallbackKind.English =>
            $"目标池里只有{Languages.NameOf(inputLang)},而它也是你的母语 —— 已翻成英语,并把英语加进了目标池。",
        FallbackKind.Ask =>
            "输入、目标池和你的母语都是英语 —— 要翻成什么语言?点下面的按钮,或者直接回一句语言名。",
        _ => "",
    };
}
