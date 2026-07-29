// P3c -- 本设备的界面偏好。设计文档 §7:**每台设备独立选皮肤**,所以这些存在本机,不同步到中枢。
// 语言同理:UI 语言跟随设备设置,不随说话者切换(D45/设计 §6.1)。

using System.Text.Json;

namespace LocalAI.Client.Services;

public enum Skin { Breeze, Ink, Warm }        // 微风(默认) / 墨白 / 暖萌 —— 设计文档 §7
public enum ThemeMode { System, Light, Dark }
public enum Density { Comfortable, Compact }

public sealed class AppSettings
{
    public string Language { get; set; } = "zh-CN";      // zh-CN | en-US | ja-JP
    public Skin Skin { get; set; } = Skin.Breeze;
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public Density Density { get; set; } = Density.Comfortable;
    public bool Autostart { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;   // 用户要求:关窗口 = 留在托盘

    // ★ 这里【故意不存】当前成员/默认成员。
    // 铁律(gateway.py:227):「主体只来自成员表;客户端自报的 device_id / tier 一律忽略」。
    // 本文件是普通用户可用记事本改的 JSON;若可见范围判定读它,两成员共用一台机器时
    // 另一位改一行就能看「仅本人」—— D45 就成了演出。
    // 成员真相在主机的 Store.Device.DefaultMemberId,由 Edge 按证书指纹反查后下发,
    // 客户端只缓存**显示名**用于渲染(见 MemberDisplayCache),且任何权限判定都不读缓存。
    /// <summary>仅供界面渲染的成员显示名缓存 —— **不是**身份,不可用于任何可见范围/权限判定。</summary>
    public string? CachedMemberDisplayName { get; set; }

    /// <summary>
    /// 天气板块里【可拖动城市】的顺序(JSON:[[城市,时区], ...])。
    /// 第一格是"当前所在地",由系统时区推断、固定在首位,不参与这个顺序。
    /// 与皮肤/语言同理:每台设备各自的偏好,不同步到中枢。
    /// </summary>
    public string? WeatherCityOrder { get; set; }

    static readonly JsonSerializerOptions J = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsPath)) ?? new AppSettings();
        }
        catch { /* 配置损坏 -> 回到默认值,不让用户开不了应用 */ }
        return new AppSettings();
    }

    public void Save()
    {
        AppPaths.EnsureStateDir();
        var tmp = AppPaths.SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, J));
        File.Move(tmp, AppPaths.SettingsPath, overwrite: true);   // 原子替换,防写一半断电留下坏文件
    }
}
