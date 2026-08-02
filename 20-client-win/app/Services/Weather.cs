// P3c -- 天气接入(设计 §4.1 / D39 / 状态矩阵 §8 第 6 条)。
//
// ★★ 出境纪律(这一条比功能本身重要,写在最前面):
//   · 【固定白名单端点】= api.open-meteo.com,别的一律不发(见 Host 常量与 UseProxy=false);
//     Open-Meteo 是设计里已经点名的来源(见 strings.json 的 weather.source_credit),不另选。
//   · 【只发坐标,不发文字地址】—— 请求里没有城市名、没有时区名、没有任何账号标识;
//     坐标本身还【就地取整到 0.1°】(约 11km),够天气用,但不足以指到街区。
//   · 不带 API key、不带 Cookie、不带 UA 指纹(只留一个朴素的产品 UA)。
//   · 不重试轰炸:失败就退回缓存并如实说明,不做指数退避把请求量放大。
//
// ★★ 诚实纪律(状态矩阵 §8 第 6 条:天气离线 -> 缓存 + updated_at + stale,【无假实时】):
//   · 拿不到就显示【上一次成功的那份 + 它的时间】,并标成 stale —— 绝不把旧数据当新的;
//   · 从来没成功过就是"暂无读数",不编一个数;
//   · 缓存落盘,重启后仍然如实标着它是什么时候取的。

using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAI.Client.Services;

/// <summary>某地某一刻的天气读数。全部字段可空 —— 拿不到就是拿不到,不填默认值。</summary>
public sealed record WeatherNow(
    double? TempC,
    double? HighC,
    double? LowC,
    double? PrecipMm,
    int? WeatherCode,
    DateTime? UpdatedAt,
    List<WeatherHour>? Hours = null,
    List<WeatherDay>? Days = null)
{
    /// <summary>这份读数是不是已经过期(超过 StaleAfter)。界面据此如实标注。</summary>
    public bool IsStale => UpdatedAt is null || DateTime.Now - UpdatedAt.Value > Weather.StaleAfter;
}

public sealed record WeatherHour(DateTime At, double? TempC, int? Code);

/// <summary>某一天的汇总(目前只用降水合计:当前没下雨时,要说得出"几天后有雨")。</summary>
public sealed record WeatherDay(DateTime Date, double? PrecipMm);

public static class Weather
{
    /// <summary>★ 唯一允许的出站主机。改这里等于改出境策略 —— 要过裁定。</summary>
    public const string Host = "api.open-meteo.com";

    /// <summary>超过这么久就把读数标成 stale(界面写"显示上次 HH:mm")。</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    /// <summary>自动刷新间隔。天气不是秒级信息,拉太勤只是白白多出境。</summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(30);

    /// <summary>缓存变了(拉取成功或失败后回退)—— 界面据此重画。</summary>
    public static event Action? Changed;

    // 城市名 -> 最近一次成功的读数。★ 键用城市名(与 Places 一致),不存坐标。
    static readonly Dictionary<string, WeatherNow> _cache = new(StringComparer.Ordinal);

    /// <summary>取某地的读数。没有就返回 null —— 调用方据此显示"暂无读数",不要编。</summary>
    public static WeatherNow? For(string city) => _cache.TryGetValue(city, out var w) ? w : null;

    public static IReadOnlyDictionary<string, WeatherNow> Snapshot() => _cache;

    /// <summary>存档恢复(重启后仍能显示上次那份 + 它的时间)。</summary>
    public static void Import(Dictionary<string, WeatherNow>? saved)
    {
        if (saved is null) return;
        _cache.Clear();
        foreach (var kv in saved) _cache[kv.Key] = kv.Value;
        Changed?.Invoke();
    }

    public static Dictionary<string, WeatherNow> Export() => new(_cache);

    /// <summary>
    /// 拉一个地点。成功则更新缓存;失败【不动缓存】,原样返回失败原因。
    /// </summary>
    public static async Task<(bool Ok, string Message)> PullAsync(
        string city, double lat, double lon, CancellationToken ct = default)
    {
        try
        {
            // ★ 坐标就地取整到 0.1°(约 11km):天气用不着更精确,而更精确就是在多交代自己的位置。
            var qlat = Math.Round(lat, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var qlon = Math.Round(lon, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var url = $"https://{Host}/v1/forecast?latitude={qlat}&longitude={qlon}"
                    + "&current=temperature_2m,precipitation,weather_code"
                    + "&daily=temperature_2m_max,temperature_2m_min,precipitation_sum"
                    + "&hourly=temperature_2m,weather_code"
                    // ★ 拉 7 天 —— 当前不下雨时要回答"几天后有雨"。
                    + "&forecast_days=7&timezone=auto";

            using var h = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("LocalAI-Client/1.0 (weather; coords-only)");

            using var resp = await c.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, $"天气服务返回 HTTP {(int)resp.StatusCode}。");

            var body = await resp.Content.ReadAsStringAsync(ct);
            var parsed = Parse(body);
            if (parsed is null) return (false, "天气数据看不懂(格式与预期不符)。");

            _cache[city] = parsed with { UpdatedAt = DateTime.Now };
            Changed?.Invoke();
            return (true, "已更新。");
        }
        catch (TaskCanceledException) { return (false, "连接天气服务超时。"); }
        catch (HttpRequestException ex) { return (false, "连不上天气服务:" + ex.Message); }
        catch (Exception ex) { return (false, "取天气出错:" + ex.GetType().Name); }
    }

    /// <summary>
    /// 解析 Open-Meteo 的应答。★ 任何一段缺失都只让【那一项】为 null,不整份丢掉 ——
    /// 有多少说多少,比"要么全有要么全无"对用户有用得多。
    /// </summary>
    public static WeatherNow? Parse(string json)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<OmResponse>(json);
            if (doc is null) return null;

            var days = new List<WeatherDay>();
            if (doc.Daily?.Time is { } dts)
                for (int i = 0; i < dts.Count; i++)
                    if (DateTime.TryParse(dts[i], out var dd))
                        days.Add(new WeatherDay(dd.Date,
                            doc.Daily.Precip is { } ps && i < ps.Count ? ps[i] : null));

            var hours = new List<WeatherHour>();
            if (doc.Hourly?.Time is { } ts && doc.Hourly.Temperature is { } temps)
                for (int i = 0; i < ts.Count && i < temps.Count; i++)
                    if (DateTime.TryParse(ts[i], out var at))
                        hours.Add(new WeatherHour(at, temps[i],
                            doc.Hourly.Code is { } cs && i < cs.Count ? cs[i] : null));

            return new WeatherNow(
                TempC: doc.Current?.Temperature,
                HighC: doc.Daily?.Max is { Count: > 0 } mx ? mx[0] : null,
                LowC: doc.Daily?.Min is { Count: > 0 } mn ? mn[0] : null,
                PrecipMm: doc.Current?.Precipitation,
                WeatherCode: doc.Current?.Code,
                UpdatedAt: null,
                Hours: hours.Count > 0 ? hours : null,
                Days: days.Count > 0 ? days : null);
        }
        catch { return null; }
    }

    /// <summary>今天算不算"有雨"的门槛(毫米)。低于它的痕量降水说"有雨"是夸大。</summary>
    public const double RainThresholdMm = 0.2;

    /// <summary>
    /// 当前没在下雨时,回答"几天后有雨"。
    /// ★ 诚实口径:预报窗口里【真的没有】就明说"未来 N 天无雨",而不是含糊其辞或者干脆不说;
    ///   压根没有逐日数据就返回 null —— 那时界面只写"无",不假装知道以后的事。
    /// </summary>
    /// <param name="cityToday">那座城【当地】的今天。★ 不传就用本机的 —— 但对与本机隔着
    /// 日界线的城市,"明天有雨"会整体错一天(审计 2026-08-02),调用方应传城市当地日期。</param>
    public static string? RainOutlook(WeatherNow? w, DateTime? cityToday = null)
    {
        var days = w?.Days;
        if (days is null || days.Count == 0) return null;
        var today = (cityToday ?? DateTime.Today).Date;
        var future = days.Where(d => d.Date > today).OrderBy(d => d.Date).ToList();
        if (future.Count == 0) return null;

        var hit = future.FirstOrDefault(d => d.PrecipMm is { } mm && mm >= RainThresholdMm);
        if (hit is null) return $"未来 {future.Count} 天无雨";
        var n = (hit.Date - today).Days;
        return n switch { 1 => "明天有雨", 2 => "后天有雨", _ => $"{n} 天后有雨" };
    }

    /// <summary>
    /// WMO 天气代码 -> 一句中文。★ 认不出的代码【如实说"未知"】,不硬塞成"晴"。
    /// </summary>
    public static string? Describe(int? code) => code switch
    {
        null => null,
        0 => "晴",
        1 => "大致晴朗",
        2 => "多云",
        3 => "阴",
        45 or 48 => "雾",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 => "冻毛毛雨",
        61 or 63 or 65 => "雨",
        66 or 67 => "冻雨",
        71 or 73 or 75 => "雪",
        77 => "霰",
        80 or 81 or 82 => "阵雨",
        85 or 86 => "阵雪",
        95 => "雷阵雨",
        96 or 99 => "雷阵雨伴冰雹",
        _ => "未知天气(代码 " + code + ")",
    };

    // ---- Open-Meteo 的应答结构(只取用得上的几项)----
    sealed class OmResponse
    {
        [JsonPropertyName("current")] public OmCurrent? Current { get; set; }
        [JsonPropertyName("daily")] public OmDaily? Daily { get; set; }
        [JsonPropertyName("hourly")] public OmHourly? Hourly { get; set; }
    }

    sealed class OmCurrent
    {
        [JsonPropertyName("temperature_2m")] public double? Temperature { get; set; }
        [JsonPropertyName("precipitation")] public double? Precipitation { get; set; }
        [JsonPropertyName("weather_code")] public int? Code { get; set; }
    }

    sealed class OmDaily
    {
        [JsonPropertyName("time")] public List<string>? Time { get; set; }
        [JsonPropertyName("temperature_2m_max")] public List<double?>? Max { get; set; }
        [JsonPropertyName("temperature_2m_min")] public List<double?>? Min { get; set; }
        [JsonPropertyName("precipitation_sum")] public List<double?>? Precip { get; set; }
    }

    sealed class OmHourly
    {
        [JsonPropertyName("time")] public List<string>? Time { get; set; }
        [JsonPropertyName("temperature_2m")] public List<double?>? Temperature { get; set; }
        [JsonPropertyName("weather_code")] public List<int?>? Code { get; set; }
    }
}
