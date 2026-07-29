// P3c -- 三语文案查表。设计取舍:不用 WPF 的 .resx/卫星程序集,而是一个内嵌 JSON + 运行时查表。
// 理由:① 术语表本身就是一张 key × 三语的表,JSON 与它一一对应,改文案不用碰代码;
//      ② 运行时切语言不需要重启(resx 卫星程序集切 UICulture 后已建好的界面不会自动重刷);
//      ③ 缺键要能**显眼地暴露**而不是静默回退成英文 —— 见 Get()。

using System.Reflection;
using System.Text.Json;

namespace LocalAI.Client.I18n;

public static class Strings
{
    public static readonly string[] Languages = { "zh-CN", "en-US", "ja-JP" };

    static readonly Dictionary<string, Dictionary<string, string>> Table = Load();
    static string _lang = "zh-CN";

    /// <summary>语言切换时触发。界面据此**就地重建**文案,不需要重启(见 MainWindow / App 的订阅)。</summary>
    public static event Action? LanguageChanged;

    public static string Language
    {
        get => _lang;
        set
        {
            var next = Languages.Contains(value) ? value : "zh-CN";
            if (next == _lang) return;
            _lang = next;
            LanguageChanged?.Invoke();
        }
    }

    static Dictionary<string, Dictionary<string, string>> Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        // 资源名 = <RootNamespace>.<路径以点分隔>
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("strings.json", StringComparison.OrdinalIgnoreCase));
        if (name is null) return new();
        using var s = asm.GetManifestResourceStream(name)!;
        using var doc = JsonDocument.Parse(s);
        var t = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Name.StartsWith('_') || p.Value.ValueKind != JsonValueKind.Object) continue;   // _note 等元数据
            var per = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var l in p.Value.EnumerateObject())
                if (l.Value.ValueKind == JsonValueKind.String) per[l.Name] = l.Value.GetString()!;
            t[p.Name] = per;
        }
        return t;
    }

    /// <summary>
    /// 取文案。缺键返回 "⟦key⟧" —— 故意刺眼:漏翻译要在界面上一眼看见,
    /// 而不是静默显示成另一种语言让人以为没问题。
    /// </summary>
    public static string Get(string key)
    {
        if (Table.TryGetValue(key, out var per))
        {
            if (per.TryGetValue(_lang, out var v)) return v;
            if (per.TryGetValue("zh-CN", out var zh)) return zh;   // 有键但缺某语言 -> 回退中文(基准语言)
        }
        return "⟦" + key + "⟧";
    }

    /// <summary>取文案并替换 {占位符}。占位符必须原样保留在译文里(术语表规则 5)。</summary>
    public static string Get(string key, params (string name, string value)[] args)
    {
        var s = Get(key);
        foreach (var (n, v) in args) s = s.Replace("{" + n + "}", v);
        return s;
    }

    /// <summary>自检用:所有键在三语里是否齐全。</summary>
    public static (int keys, List<string> missing) Audit()
    {
        var missing = new List<string>();
        foreach (var (k, per) in Table)
            foreach (var l in Languages)
                if (!per.ContainsKey(l)) missing.Add($"{k}/{l}");
        return (Table.Count, missing);
    }
}
