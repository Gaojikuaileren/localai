// P3c -- 天气板块的地点表。
//
// 用户裁定:
//   · 第一格【不是写死的科隆】,而是【当前所在地】,标记为「当前」(不是「家」);
//   · 第一格【不可拖动】;其余城市可拖拽改顺序;
//   · 可拖动的城市右下角有小角标提示。
//
// ★ 「当前所在地」怎么来 —— 这里选的是【系统时区推断】:
//   完全离线、不出网、不碰 IP,与设计 §4.1 的出境策略一致
//   (天气只走固定白名单端点、只发预配置坐标、不发文字地址)。
//   代价是精度只到"时区级"(能说出大区/代表城市,不是真正的定位)。
//   IP 定位能到城市级但要把 IP 发给第三方,与上述策略冲突,故不默认启用;
//   Windows 定位 API 需要用户显式授权,留待用户裁定。
//   —— 所以这里的当前地是【推断值】,界面上不宣称它是精确定位。

using System.Text.Json;

namespace LocalAI.Client.Services;

/// <summary>一个天气地点。IsCurrent 的那一个固定排在首位且不可拖动。</summary>
public sealed record Place(string City, string TimeZoneId, bool IsCurrent = false);

public static class Places
{
    /// <summary>
    /// 几个已知城市的坐标(纬度北正 / 经度东正)—— 【只用来本地算日出日落】,不出网、不发给任何人。
    /// 这些是公开的地理常识,写死在这里正是为了避免为了一个日出时刻去做地理编码请求。
    /// ★ 表里没有的城市 -> CoordOf 返回 null -> 界面上就【不画昼夜】,不拿一个猜的顶上。
    /// </summary>
    static readonly Dictionary<string, (double Lat, double Lon)> CityCoord = new(StringComparer.Ordinal)
    {
        ["科隆"] = (50.94, 6.96),
        ["华沙"] = (52.23, 21.01),
        ["巴黎"] = (48.86, 2.35),
        ["伦敦"] = (51.51, -0.13),
        ["武汉"] = (30.59, 114.30),
        ["东京"] = (35.68, 139.69),
        ["札幌"] = (43.06, 141.35),
        ["首尔"] = (37.57, 126.98),
        ["纽约"] = (40.71, -74.01),
        ["洛杉矶"] = (34.05, -118.24),
        ["芝加哥"] = (41.88, -87.63),
        ["新加坡"] = (1.35, 103.82),
        ["悉尼"] = (-33.87, 151.21),
    };

    /// <summary>
    /// 一个地点的坐标:先按城市名认,认不出再按时区取该时区的代表城市。
    /// 两条都不中就返回 null —— 调用方据此【不显示昼夜】,而不是编一个。
    /// </summary>
    public static (double Lat, double Lon)? CoordOf(Place p)
    {
        if (CityCoord.TryGetValue(p.City, out var c)) return c;
        if (ZoneCity.TryGetValue(p.TimeZoneId, out var rep) && CityCoord.TryGetValue(rep, out var c2)) return c2;
        return null;
    }

    /// <summary>Windows 时区 → 该时区的代表城市。只覆盖用户关心的几个大区 + 常见时区。</summary>
    static readonly Dictionary<string, string> ZoneCity = new(StringComparer.OrdinalIgnoreCase)
    {
        ["W. Europe Standard Time"] = "科隆",
        ["Central European Standard Time"] = "华沙",
        ["Romance Standard Time"] = "巴黎",
        ["GMT Standard Time"] = "伦敦",
        ["China Standard Time"] = "武汉",
        ["Tokyo Standard Time"] = "东京",
        ["Korea Standard Time"] = "首尔",
        ["Eastern Standard Time"] = "纽约",
        ["Pacific Standard Time"] = "洛杉矶",
        ["Central Standard Time"] = "芝加哥",
        ["Singapore Standard Time"] = "新加坡",
        ["AUS Eastern Standard Time"] = "悉尼",
    };

    /// <summary>
    /// 由系统时区推断当前所在地。推断不出就退回时区的显示名 —— 如实给个大区名,不假装知道城市。
    /// </summary>
    public static Place Current()
    {
        var tz = TimeZoneInfo.Local;
        var city = ZoneCity.TryGetValue(tz.Id, out var c) ? c : ShortZoneName(tz);
        return new Place(city, tz.Id, IsCurrent: true);
    }

    static string ShortZoneName(TimeZoneInfo tz)
    {
        // 显示名形如 "(UTC+01:00) 阿姆斯特丹、柏林、伯尔尼、罗马、斯德哥尔摩、维也纳"
        var name = tz.DisplayName;
        var i = name.IndexOf(')');
        if (i >= 0 && i + 1 < name.Length) name = name[(i + 1)..].Trim();
        var comma = name.IndexOfAny(new[] { '、', ',', ',' });
        return comma > 0 ? name[..comma].Trim() : name;
    }

    /// <summary>默认的其它城市(可拖拽排序)。当前地若与其中之一重合,会在合并时去重。</summary>
    static readonly Place[] Defaults =
    {
        new("武汉", "China Standard Time"),
        new("札幌", "Tokyo Standard Time"),
    };

    /// <summary>
    /// 完整地点列表:第 0 项固定是当前所在地,其后是用户排好序的城市。
    /// 顺序持久化在设置里(每台设备各自的偏好)。
    /// </summary>
    public static List<Place> Load(AppSettings settings)
    {
        var list = new List<Place> { Current() };

        var saved = ParseOrder(settings.WeatherCityOrder);
        var pool = saved.Count > 0 ? saved : Defaults.ToList();

        foreach (var p in pool)
        {
            // 与当前地重名则跳过 —— 同一个城市不该出现两格
            if (string.Equals(p.City, list[0].City, StringComparison.Ordinal)) continue;
            list.Add(p with { IsCurrent = false });
        }
        return list;
    }

    /// <summary>保存可拖动部分的顺序(第 0 项当前地不参与,它固定在首位)。</summary>
    public static void SaveOrder(AppSettings settings, IEnumerable<Place> draggable)
    {
        settings.WeatherCityOrder = JsonSerializer.Serialize(
            draggable.Select(p => new[] { p.City, p.TimeZoneId }).ToList());
        settings.Save();
    }

    static List<Place> ParseOrder(string? json)
    {
        var result = new List<Place>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            var arr = JsonSerializer.Deserialize<List<string[]>>(json);
            if (arr is null) return result;
            foreach (var pair in arr)
                if (pair.Length >= 2 && !string.IsNullOrWhiteSpace(pair[0]))
                    result.Add(new Place(pair[0], pair[1]));
        }
        catch { /* 配置损坏 -> 回到默认,不让用户开不了主页 */ }
        return result;
    }
}
