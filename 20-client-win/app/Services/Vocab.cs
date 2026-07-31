// P3c -- 界面用词表(用户裁定 2026-07-31)。
//
// 这套系统默认面向【家庭】,但同一套东西放到工作场景里,"家庭成员""家庭动态"就别扭。
// 所以给一个设置:把界面里表示「共用这台中枢的这群人」的那个词,从「家庭」整体换成「团队」。
//
// ★★ 只换【界面用词】,绝不换数据 —— 这是本文件最重要的一条纪律:
//   日历分组、待办可见范围这些字段【存的就是 "家庭" 这个字符串】(它是存储键,只是恰好是中文)。
//   如果连存储值一起换掉:老档案里 CalendarGroup="家庭" 就再也匹配不上分组表 ->
//   Array.IndexOf 返回 -1 -> 回落到第 0 项 -> 用户的日程被【静默改到别的分组】,那是数据损坏。
//   所以:存的永远是原词,显示时才过这一层。见 CalendarEditor 里"存原值、显示才 Apply"的写法。
//
// ★ 挂在哪:Strings.Get(覆盖 strings.json)与 Ui.Title/Subtitle/Body/Caption/Panel
//   (覆盖代码里硬编码的中文)。两处中央入口一钩,界面几乎全覆盖,不必去改几十个调用点。

namespace LocalAI.Client.Services;

/// <summary>界面里对「共用这台中枢的这群人」的称谓。</summary>
public enum OrgVocab
{
    /// <summary>家庭(默认)。</summary>
    Family,
    /// <summary>团队。</summary>
    Team,
}

public static class Vocab
{
    static OrgVocab _current = OrgVocab.Family;

    /// <summary>当前称谓。改动时广播 Changed —— 界面据此就地重建文案(与换语言同一条路)。</summary>
    public static OrgVocab Current
    {
        get => _current;
        set { if (_current != value) { _current = value; Changed?.Invoke(); } }
    }

    /// <summary>称谓变了 —— 订阅方重建界面文案。</summary>
    public static event Action? Changed;

    /// <summary>下拉里给人看的名字(它自己【不】过 Apply,否则"家庭"这一项会显示成"团队")。</summary>
    public static string LabelOf(OrgVocab v, string lang) => (v, lang) switch
    {
        (OrgVocab.Family, "en-US") => "Family",
        (OrgVocab.Family, "ja-JP") => "家族",
        (OrgVocab.Family, _) => "家庭",
        (_, "en-US") => "Team",
        (_, "ja-JP") => "チーム",
        _ => "团队",
    };

    /// <summary>
    /// 各语言里要替换的词对。★ 英文保留大小写两式(句首与句中都要能换)。
    /// </summary>
    static (string from, string to)[] PairsFor(string lang) => lang switch
    {
        "en-US" => new[] { ("Family", "Team"), ("family", "team") },
        "ja-JP" => new[] { ("家族", "チーム") },
        _ => new[] { ("家庭", "团队") },
    };

    /// <summary>
    /// ★ 这些是【专有名词】,里面的"家庭"不是我们的称谓,不能跟着换:
    ///   "Apple 家庭共享" 是苹果自己的功能名(Family Sharing / ファミリー共有)——
    ///   把它显示成"Apple 团队共享日历"是把一个真实产品名说错了,属于伪造信息。
    /// </summary>
    static readonly string[] Protected =
    {
        "Apple 家庭共享",
        "Apple Family",
        "Apple ファミリー共有",
    };

    /// <summary>把一段界面文案里的称谓换成当前设置的那个。默认(家庭)时原样返回,零开销。</summary>
    public static string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (_current == OrgVocab.Family) return text;      // 默认档:什么都不做

        // ★ 不用"哨兵字符"占位再还原(那要挑一个正常文案绝不会出现的字符,脆而难证):
        //   改成【按专有名词切段】—— 专有名词那几段原样保留,只在其余段落里替换。
        //   同一件事,少一个"哨兵会不会撞车"的假设。
        var pairs = PairsFor(I18n.Strings.Language);
        var sb = new System.Text.StringBuilder(text.Length + 8);
        foreach (var (seg, locked) in SplitKeepingProtected(text))
        {
            if (locked) { sb.Append(seg); continue; }
            var t = seg;
            foreach (var (from, to) in pairs) t = t.Replace(from, to, StringComparison.Ordinal);
            sb.Append(t);
        }
        return sb.ToString();
    }

    /// <summary>把文本切成若干段,标出哪些是"专有名词、不许动"的。</summary>
    static IEnumerable<(string seg, bool locked)> SplitKeepingProtected(string text)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            // 找从 pos 起【最靠前】的那个专有名词
            var bestAt = -1;
            var bestLen = 0;
            foreach (var p in Protected)
            {
                var at = text.IndexOf(p, pos, StringComparison.Ordinal);
                if (at >= 0 && (bestAt < 0 || at < bestAt)) { bestAt = at; bestLen = p.Length; }
            }
            if (bestAt < 0) { yield return (text[pos..], false); yield break; }
            if (bestAt > pos) yield return (text[pos..bestAt], false);
            yield return (text.Substring(bestAt, bestLen), true);
            pos = bestAt + bestLen;
        }
    }
}
