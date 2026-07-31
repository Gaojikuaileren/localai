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
        => SetFromApple(calendars.Select(c => (c.Name, c.ColorHex, (string?)null)));

    /// <param name="calendars">(显示名, 颜色, 该日历的 URL —— 只用来给重名的做区分,不落进日程)。</param>
    public static void SetFromApple(IEnumerable<(string Name, string? ColorHex, string? Url)> calendars)
    {
        var raw = calendars.Where(c => !string.IsNullOrWhiteSpace(c.Name))
                           .Select(c => (Name: c.Name.Trim(), c.ColorHex, c.Url)).ToList();

        // ★★ iCloud 里【重名日历很常见】:自己的"家庭"与别人共享给你的"家庭",
        //   或者两个都没起名的都叫「(未命名)」。存储值是名字,重名就意味着:
        //   两个日历的日程共用第一个的颜色、下拉里出现两行一模一样的项、选哪个都一样。
        //   所以重名的要【去重成一个稳定且唯一的名字】—— 用该日历 URL 的稳定短码,
        //   不用序号:序号会随 iCloud 返回顺序变,今天的"家庭 (2)"明天可能变成另一个日历。
        var dupes = raw.GroupBy(x => x.Name, StringComparer.Ordinal)
                       .Where(g => g.Count() > 1)
                       .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

        var list = raw.Select(c => new CalGroup(
                          dupes.Contains(c.Name) ? c.Name + " · " + ShortTag(c.Url ?? c.Name) : c.Name,
                          Normalize(c.ColorHex), GroupSource.Apple))
                      .ToList();
        _current = list.Count > 0 ? list : LocalDefaults.ToList();
        Changed?.Invoke();
    }

    /// <summary>由 URL 算一个稳定的四位短码 —— 同一个日历每次都得到同一个,换台机器也一样。</summary>
    public static string ShortTag(string s)
    {
        int h = 17;
        foreach (var ch in s) h = unchecked(h * 31 + ch);
        return ((uint)h % 0x10000).ToString("x4");
    }

    /// <summary>
    /// 界面上该怎么如实介绍当前这张分类表 —— ★ FromApple 光有属性没人用等于没做。
    /// </summary>
    public static string SourceNote => FromApple
        ? "分类与颜色来自你的 iCloud 日历。"
        : "这些是本机分类;接上 Apple 之后会换成你真实的日历(颜色也一并跟过来)。";

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
    /// 一个颜色上面该用黑字还是白字。
    /// ★ 按 WCAG 相对亮度分别算两者的对比度、取大的那个 ——
    ///   原来那个 0.62 的粗亮度阈值偏高:Apple 的绿色日历(#34C759)会得到白字(约 2.2:1),
    ///   而给深字本可以到 7:1。
    /// </summary>
    public static Color TextOn(Color bg)
    {
        var dark = Color.FromRgb(0x1A, 0x1D, 0x21);
        return Contrast(bg, Colors.White) >= Contrast(bg, dark) ? Colors.White : dark;
    }

    /// <summary>WCAG 相对亮度(含 sRGB 逆伽马)。</summary>
    public static double Luminance(Color c)
    {
        static double Ch(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    /// <summary>两色的对比度(1..21)。</summary>
    public static double Contrast(Color a, Color b)
    {
        var la = Luminance(a); var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// 把分类色压到【在普通底色上读得出来】—— 保持色相,只降亮度。
    /// ★ 为什么需要它:定时日程是描边框,框里的标题就是分类色本色 ——
    ///   Apple 黄(#FFCC00)写在几乎等于白的淡黄底上约 1.5:1,基本读不出来,
    ///   1px 的黄描边同样几乎看不见,整块日程像消失了。
    /// </summary>
    public static Color OnSurface(Color c, bool darkTheme = false)
    {
        var bg = darkTheme ? Color.FromRgb(0x1A, 0x1D, 0x21) : Colors.White;
        var cur = c;
        // 每次向深(或向浅)走一小步,直到够 3:1 为止;最多走 24 步防死循环。
        for (int i = 0; i < 24 && Contrast(cur, bg) < 3.0; i++)
            cur = darkTheme
                ? Color.FromRgb(Up(cur.R), Up(cur.G), Up(cur.B))
                : Color.FromRgb(Down(cur.R), Down(cur.G), Down(cur.B));
        return cur;

        static byte Down(byte v) => (byte)Math.Max(0, v - 10);
        static byte Up(byte v) => (byte)Math.Min(255, v + 10);
    }
}
