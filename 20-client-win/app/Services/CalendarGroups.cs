// P3c -- 日程分类(日历组)与它们的颜色。用户裁定 2026-07-31:
//   · 新建日程的归类要与【Apple 同步来的日历】一致;
//   · 每个分类有自己的颜色,颜色也【读自 Apple】。
//
// ★★ 诚实口径 —— 这里最容易出的错是"假装知道":
//   · 没接 Apple 时,给的是【本地占位分类】(家庭/个人/工作/未分组),颜色也是本地配的。
//     Source 会如实标成 Local,界面据此可以说明"这些是本机分类,接上 Apple 后会换成你真实的日历"。
//   · 接上之后用 Apple 给的名字与 calendar-color;Apple 没给颜色的那个日历,
//     就退回本地按名字算出来的稳定色 —— 而不是随便挑一个,更不是每次启动换一个。
//   · 【绝不】把 Apple 的分类和本地占位混在一张表里让人分不清哪个是真的。
//
// ★ 存储值不做界面用词替换(与 Vocab 的既有纪律一致):
//   存的一直是 Apple 给的原名 / 本地原词,显示时才过 Vocab.Apply。
//   否则老档案里 CalendarGroup="家庭" 会匹配不上分组表。

using System.Windows.Media;

namespace LocalAI.Client.Services;

public enum GroupSource { Local, Apple }

/// <param name="Name">存储值 = 显示名的原文(Apple 的 displayname,或本地占位词)。</param>
/// <param name="ColorHex">#RRGGBB。null = Apple 没给,按名字算一个稳定色。</param>
public sealed record CalGroup(string Name, string? ColorHex, GroupSource Source);

public static class CalendarGroups
{
    /// <summary>没接 Apple 时的本地占位分类。★ 这几个词是【存储值】,永不随界面用词表变化。</summary>
    static readonly CalGroup[] LocalDefaults =
    {
        new("家庭", "#3B7DD8", GroupSource.Local),
        new("个人", "#2FA37C", GroupSource.Local),
        new("工作", "#C8792F", GroupSource.Local),
        new("(未分组)", "#8A8F98", GroupSource.Local),
    };

    static List<CalGroup> _current = LocalDefaults.ToList();

    /// <summary>分类表变了(接上 Apple / 刷新日历列表 / 断开)—— 界面据此重画颜色与下拉项。</summary>
    public static event Action? Changed;

    public static IReadOnlyList<CalGroup> All => _current;

    /// <summary>当前这张表是不是【真的来自 Apple】—— 界面要如实说明,不许含糊。</summary>
    public static bool FromApple => _current.Count > 0 && _current[0].Source == GroupSource.Apple;

    /// <summary>下拉框用的存储值序列。</summary>
    public static string[] Names => _current.Select(g => g.Name).ToArray();

    /// <summary>
    /// 用 Apple 那边的日历清单替换分类表。传空集合 = 回到本地占位(比如用户断开了连接)。
    /// </summary>
    public static void SetFromApple(IEnumerable<(string Name, string? ColorHex)> calendars)
    {
        var list = calendars
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => new CalGroup(c.Name.Trim(), Normalize(c.ColorHex), GroupSource.Apple))
            .ToList();
        _current = list.Count > 0 ? list : LocalDefaults.ToList();
        Changed?.Invoke();
    }

    /// <summary>某个分类的颜色。认不出的分类(比如老档案里的词)也给一个【稳定】色,不留空。</summary>
    public static Color ColorOf(string? group)
    {
        var hex = _current.FirstOrDefault(g => string.Equals(g.Name, group, StringComparison.Ordinal))?.ColorHex;
        if (hex is not null && TryParse(hex, out var c)) return c;
        return StableColor(group ?? "");
    }

    public static Brush BrushOf(string? group) => new SolidColorBrush(ColorOf(group)) { Opacity = 1 };

    /// <summary>
    /// Apple 给的是 #RRGGBBAA(八位,末两位是透明度)。统一收成 #RRGGBB ——
    /// 透明度由界面自己决定,拿别人的 alpha 会让同一批色块深浅不一。
    /// </summary>
    public static string? Normalize(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var h = hex.Trim();
        if (!h.StartsWith("#")) h = "#" + h;
        if (h.Length == 9) h = h[..7];          // #RRGGBBAA -> #RRGGBB
        if (h.Length == 4)                       // #RGB -> #RRGGBB
            h = "#" + h[1] + h[1] + h[2] + h[2] + h[3] + h[3];
        return h.Length == 7 && h[1..].All(Uri.IsHexDigit) ? h.ToUpperInvariant() : null;
    }

    static bool TryParse(string hex, out Color c)
    {
        c = Colors.Gray;
        try { c = (Color)ColorConverter.ConvertFromString(hex); return true; }
        catch { return false; }
    }

    /// <summary>
    /// 按名字算一个稳定的颜色 —— 同一个名字每次都得到同一个色(不用 GetHashCode:
    /// .NET 的字符串哈希每次进程启动都不同,那会让颜色每次开机都变)。
    /// </summary>
    public static Color StableColor(string name)
    {
        int h = 17;
        foreach (var ch in name) h = unchecked(h * 31 + ch);
        var hue = ((h % 360) + 360) % 360;
        return FromHsl(hue, 0.52, 0.52);
    }

    static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = l - c / 2;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    /// <summary>
    /// 一个颜色上面该用黑字还是白字 —— 按感知亮度选,免得深蓝底上写黑字。
    /// </summary>
    public static Color TextOn(Color bg)
    {
        var lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
        return lum > 0.62 ? Color.FromRgb(0x1A, 0x1D, 0x21) : Colors.White;
    }
}
