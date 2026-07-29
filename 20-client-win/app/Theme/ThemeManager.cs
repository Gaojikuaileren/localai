// P3c -- 换肤。设计 §7:皮肤在【设置 › 外观】里改,每台设备独立;切换不需要重启。
// 做法:App.Resources.MergedDictionaries 里固定第 0 位放皮肤字典,换肤 = 替换第 0 位,
// 所有用 DynamicResource 引用令牌的控件会自动重绘。Tokens.xaml 常驻第 1 位(禁改项在里面)。

using System.Windows;
using LocalAI.Client.Services;

namespace LocalAI.Client.Theme;

public static class ThemeManager
{
    const int SkinSlot = 0;

    static Uri UriOf(Skin s) => new($"pack://application:,,,/Theme/{s}.xaml", UriKind.Absolute);

    public static Skin Current { get; private set; } = Skin.Breeze;

    public static void Apply(Skin skin)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        var next = new ResourceDictionary { Source = UriOf(skin) };
        if (dicts.Count == 0) dicts.Add(next);
        else dicts[SkinSlot] = next;
        Current = skin;
    }

    /// <summary>启动时装载:皮肤在前(可换),Tokens 在后(常驻,含禁改的风险/范围语义色)。</summary>
    public static void Initialize(Skin skin)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        dicts.Clear();
        dicts.Add(new ResourceDictionary { Source = UriOf(skin) });                                  // slot 0
        dicts.Add(new ResourceDictionary { Source = new("pack://application:,,,/Theme/Tokens.xaml", UriKind.Absolute) });
        Current = skin;
    }
}
