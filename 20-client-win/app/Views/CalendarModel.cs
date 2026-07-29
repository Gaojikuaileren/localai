// P3c -- 日程数据模型。字段集按用户裁定:
//   标题 · 开始 · 结束(默认 +1 小时) · iCloud 日历组(留待接入) · 地点(纯字符) · 链接 · 备注
//   · 全天开关;全天日程可【跨天】,跨天在界面上用一条贯穿多格的长条表示。
//
// ★ 数据源尚未接入 Apple 家庭共享日历(设计 §4.5 / 状态矩阵 §8):
//   模型先立住(AI 与手动编辑写同一个),但保存一律如实拒绝,绝不伪造日程或同步成功。
//
// 可见范围(Scope)与归属(Owner)沿用 D45 的两成员家庭口径。
// Location / Url / Notes 都是【自由文本】:仅作显示,永不进 prompt(与设备自报名同一纪律)。

namespace LocalAI.Client.Views;

public sealed record CalendarEvent(
    DateTime Start,
    DateTime End,
    string Title,
    string Owner,
    string Scope,
    bool AllDay = false,
    string? CalendarGroup = null,   // iCloud 日历组(接入后由服务端给出可选集合)
    string? Location = null,        // 仅字符,不做地理解析
    string? Url = null,
    string? Notes = null)
{
    /// <summary>跨天判定:全天日程按【日期区间】算,定时日程只属于起始那天。</summary>
    public DateTime FirstDay => Start.Date;
    public DateTime LastDay => AllDay ? End.Date : Start.Date;
    public int DayCount => (LastDay - FirstDay).Days + 1;
    public bool IsMultiDay => DayCount > 1;

    /// <summary>这一天是否落在本日程的区间内。</summary>
    public bool Covers(DateTime day) => day.Date >= FirstDay && day.Date <= LastDay;
}

public static class CalendarData
{
    public static List<CalendarEvent> Events { get; } = new();

    /// <summary>
    /// 日程集合变更时触发。★ 有了它,界面就不依赖"播种一定早于建窗口"这种时序假设 ——
    /// 将来 AI 或编辑器写入日程也能自动刷新(这类时序耦合正是"开启时读不出日程"的成因)。
    /// </summary>
    public static event Action? Changed;

    public static void NotifyChanged() => Changed?.Invoke();

    public static void Add(CalendarEvent e) { Events.Add(e); NotifyChanged(); }
    public static void Remove(CalendarEvent e) { if (Events.Remove(e)) NotifyChanged(); }

    /// <summary>iCloud 日历组。接入前给一组占位;接入后由服务端下发真实分组。</summary>
    public static readonly string[] Groups = { "家庭", "个人", "工作", "(未分组)" };

    /// <summary>某天的定时日程(不含全天/跨天条,那些走 SpansIn 单独画长条)。</summary>
    public static IEnumerable<CalendarEvent> TimedOn(DateTime day)
        => Events.Where(e => !e.AllDay && e.Start.Date == day.Date).OrderBy(e => e.Start);

    /// <summary>某天的全部日程(全天 + 定时),用于列表展示与"有无日程"的标点。</summary>
    public static IEnumerable<CalendarEvent> On(DateTime day)
        => Events.Where(e => e.Covers(day))
                 .OrderByDescending(e => e.AllDay)     // 全天排在前面
                 .ThenBy(e => e.Start);

    /// <summary>
    /// 计算落在 [rangeStart, rangeStart+dayCount) 这一段里的【全天/跨天】日程,
    /// 返回它在这一段里的起始列与跨列数 —— 界面据此画一条贯穿多格的长条。
    /// 日程若延伸到区间之外,会被裁到区间边界(并标出是否续前/续后)。
    /// </summary>
    public static List<(CalendarEvent Ev, int Col, int Span, bool ClipStart, bool ClipEnd)> SpansIn(
        DateTime rangeStart, int dayCount)
    {
        var result = new List<(CalendarEvent, int, int, bool, bool)>();
        var rangeEnd = rangeStart.Date.AddDays(dayCount - 1);

        foreach (var e in Events.Where(x => x.AllDay).OrderBy(x => x.FirstDay).ThenByDescending(x => x.DayCount))
        {
            if (e.LastDay < rangeStart.Date || e.FirstDay > rangeEnd) continue;
            var from = e.FirstDay < rangeStart.Date ? rangeStart.Date : e.FirstDay;
            var to = e.LastDay > rangeEnd ? rangeEnd : e.LastDay;
            var col = (from - rangeStart.Date).Days;
            var span = (to - from).Days + 1;
            // ★ 括号不可省:元组里写 `a < b, c > d` 会被 C# 解析成泛型参数列表 `a<b, c>`,
            //   报出一串莫名其妙的"变量被当作类型使用"。
            result.Add((e, col, span, (e.FirstDay < rangeStart.Date), (e.LastDay > rangeEnd)));
        }
        return result;
    }
}
