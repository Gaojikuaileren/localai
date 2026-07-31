// P3c -- 日出 / 日落时刻(用户裁定 2026-07-31:时间轴上把夜晚那段压暗,一眼分出昼夜)。
//
// ★★ 这是【算】出来的,不是【拉】出来的 —— 一个字节都不出网。
//   天气服务还没接,而且按设计 §4.1 天气也只走白名单端点;日出日落纯粹是天文几何:
//   给定纬度、经度、日期、时区偏移就能直接算,精度约 ±1 分钟,足够画一条昼夜分界。
//   用 NOAA 太阳位置算法(与 NOAA Solar Calculator 同一套公式)。
//
// ★ 算不出来就【什么也不画】:
//   · 城市坐标不认识(用户自己加的城市)-> null
//   · 极昼 / 极夜(高纬度的夏冬)-> 明确返回"全白天"或"全黑夜",不是 null
//   绝不拿一个猜的 6:00/18:00 顶上 —— 那是在界面上编一个读数。

namespace LocalAI.Client.Services;

/// <summary>某一天的昼夜。Sunrise/Sunset 是【当天 0 点起算的小时数】(与时间轴的竖轴同一把尺)。</summary>
public readonly record struct SunDay(double Sunrise, double Sunset)
{
    /// <summary>极昼:整天都是白天。</summary>
    public static SunDay AllDay => new(0, 24);
    /// <summary>极夜:整天都是黑夜。</summary>
    public static SunDay AllNight => new(0, 0);
}

public static class SunClock
{
    const double ZenithDeg = 90.833;    // 官方日出日落定义:太阳中心在地平线下 0.833°(含大气折射与日面半径)

    /// <summary>
    /// 算某一天的日出日落。
    /// </summary>
    /// <param name="latDeg">纬度,北正。</param>
    /// <param name="lonDeg">经度,东正。</param>
    /// <param name="day">当地日期。</param>
    /// <param name="utcOffsetHours">当地相对 UTC 的偏移(含夏令时)。</param>
    public static SunDay ForDay(double latDeg, double lonDeg, DateTime day, double utcOffsetHours)
    {
        var n = day.DayOfYear;
        // 一年中的角位置(弧度)。取当天正午作代表 —— 日内变化对日出日落的影响远小于 1 分钟。
        var gamma = 2 * Math.PI / (IsLeap(day.Year) ? 366.0 : 365.0) * (n - 1 + 0.5);

        // 时差方程(分钟)与赤纬(弧度)
        var eqTime = 229.18 * (0.000075
            + 0.001868 * Math.Cos(gamma) - 0.032077 * Math.Sin(gamma)
            - 0.014615 * Math.Cos(2 * gamma) - 0.040849 * Math.Sin(2 * gamma));
        var decl = 0.006918
            - 0.399912 * Math.Cos(gamma) + 0.070257 * Math.Sin(gamma)
            - 0.006758 * Math.Cos(2 * gamma) + 0.000907 * Math.Sin(2 * gamma)
            - 0.002697 * Math.Cos(3 * gamma) + 0.00148 * Math.Sin(3 * gamma);

        // 太阳正午(当地时,分钟)
        var solarNoon = 720 - 4 * lonDeg - eqTime + utcOffsetHours * 60;

        var lat = latDeg * Math.PI / 180;
        var cosHa = Math.Cos(ZenithDeg * Math.PI / 180) / (Math.Cos(lat) * Math.Cos(decl))
                    - Math.Tan(lat) * Math.Tan(decl);

        // ★ 高纬度会解不出来 —— 那不是"算错了",是那天真的不日出或不日落。
        if (cosHa > 1) return SunDay.AllNight;    // 极夜
        if (cosHa < -1) return SunDay.AllDay;     // 极昼

        var haMin = 4 * (Math.Acos(cosHa) * 180 / Math.PI);   // 时角换算成分钟
        return new SunDay((solarNoon - haMin) / 60.0, (solarNoon + haMin) / 60.0);
    }

    static bool IsLeap(int y) => DateTime.IsLeapYear(y);
}
