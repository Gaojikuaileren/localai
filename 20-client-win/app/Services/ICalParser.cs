// P3c -- iCalendar(RFC 5545)解析:把 CalDAV 拉回来的 .ics 文本变成 CalendarEvent。
//
// 只做【读】。范围裁定(用户 2026-07-31):这一版只读拉取,不写回 Apple ——
// 所以这里没有、也不该有"生成 ics"的对应物,免得给出一个没被验证过的写路径。
//
// ★★ 三个最容易错的地方,都在这里钉死了:
//
//  ① 全天日程的 DTEND 是【不含】那一天的(exclusive):
//     DTSTART;VALUE=DATE:20260731 + DTEND;VALUE=DATE:20260802 表示 7/31 与 8/1 两天,
//     【不含】8/2。而我们的 CalendarEvent 用的是【含】末日的口径(见 LastDay / Covers)。
//     不减这一天,每条全天日程都会凭空多出一天 —— 而且是那种"看着像对的"错。
//
//  ② 折行(line folding):RFC 5545 规定长行可以折,续行以【空格或制表符】开头。
//     不先合并折行,长标题会被从中间截断、甚至把 SUMMARY 的后半截当成新属性。
//
//  ③ 转义:正文里的 \, \; \n \\ 是转义序列,不是字面字符。不还原的话,
//     标题里的逗号会显示成"\,",备注里的换行会显示成字面的"\n"。

using System.Globalization;
using System.Text;

namespace LocalAI.Client.Services;

public static class ICalParser
{
    /// <summary>
    /// 解析一段 .ics 文本里的所有 VEVENT。
    /// ★ 解析不出来的单条【跳过而不是抛】—— 一条坏日程不该让整次同步失败;
    ///   但跳了几条要能被调用方看见(见返回的 skipped)。
    /// </summary>
    /// <param name="calendarName">
    /// 这批日程来自哪个 iCloud 日历 —— 存进 CalendarGroup。
    /// ★★ 之前这一项根本没填:从 Apple 拉下来的日程全部是【无分类】,
    ///   于是它们在界面上全是同一个颜色、也对不上任何一个日历(用户反馈"分类不对")。
    /// </param>
    public static (List<Views.CalendarEvent> events, int skipped) ParseEvents(
        string ics, string owner, string scope, string? calendarName = null)
    {
        var list = new List<Views.CalendarEvent>();
        var skipped = 0;
        if (string.IsNullOrWhiteSpace(ics)) return (list, 0);

        var lines = Unfold(ics);
        List<(string name, Dictionary<string, string> parms, string value)>? cur = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                cur = new List<(string, Dictionary<string, string>, string)>();
                continue;
            }
            if (line.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (cur is not null)
                {
                    var ev = Build(cur, owner, scope, calendarName);
                    if (ev is not null) list.Add(ev); else skipped++;
                }
                cur = null;
                continue;
            }
            if (cur is null) continue;                  // VEVENT 之外的东西(VTIMEZONE 等)这一版不管
            if (TryParseLine(line, out var p)) cur.Add(p);
        }
        return (list, skipped);
    }

    /// <summary>
    /// 解析一段 .ics 里的所有 VTODO(Apple 提醒事项)。
    /// ★ 与 VEVENT 共用折行/转义/时间那三套 —— 那几个坑两边一模一样。
    /// 只取能对上号的字段:标题 / 截止 / 完成 / 优先级 / 备注 / UID。
    /// </summary>
    /// <param name="notices">
    /// 这个清单里有几条是 Apple 的【已升级】公告(不是用户的待办)。
    /// ★★ 必须与 "skipped" 分开计:前者意味着【拿不到】(这条路永远拿不到),
    ///   后者只是【这几条读不懂】。混在一起的话界面就只能说"没东西",
    ///   而真相是"拿不到" —— 后者才是用户真正需要知道的那句话。
    /// </param>
    public static (List<TodoItem> todos, int skipped, int notices) ParseTodos(string ics, TodoKind kind)
    {
        var list = new List<TodoItem>();
        var skipped = 0;
        var notices = 0;
        if (string.IsNullOrWhiteSpace(ics)) return (list, 0, 0);

        var lines = Unfold(ics);
        List<(string name, Dictionary<string, string> parms, string value)>? cur = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("BEGIN:VTODO", StringComparison.OrdinalIgnoreCase)) { cur = new(); continue; }
            if (line.StartsWith("END:VTODO", StringComparison.OrdinalIgnoreCase))
            {
                if (cur is not null)
                {
                    // ★ 先看是不是 Apple 的「已升级」公告 —— 那不是待办,不能当任务导入
                    string? sm = null, de = null;
                    foreach (var (nm, _, vl) in cur)
                    {
                        if (nm.Equals("SUMMARY", StringComparison.OrdinalIgnoreCase)) sm = Unescape(vl);
                        else if (nm.Equals("DESCRIPTION", StringComparison.OrdinalIgnoreCase)) de = Unescape(vl);
                    }
                    if (AppleReminderNotice.IsUpgradeNotice(sm, de)) { notices++; cur = null; continue; }

                    var t = BuildTodo(cur, kind);
                    if (t is not null) list.Add(t); else skipped++;
                }
                cur = null;
                continue;
            }
            if (cur is null) continue;
            if (TryParseLine(line, out var pr)) cur.Add(pr);
        }
        return (list, skipped, notices);
    }

    static TodoItem? BuildTodo(
        List<(string name, Dictionary<string, string> parms, string value)> props, TodoKind kind)
    {
        string? uid = null, summary = null, notes = null, status = null;
        DateTime? due = null;
        var dueHasTime = false;
        var priority = TodoPriority.None;
        var completed = false;

        foreach (var (name, parms, value) in props)
        {
            switch (name.ToUpperInvariant())
            {
                case "UID": uid = value.Trim(); break;
                case "SUMMARY": summary = Unescape(value); break;
                case "DESCRIPTION": notes = Unescape(value); break;
                case "STATUS": status = value.Trim().ToUpperInvariant(); break;
                case "COMPLETED": completed = true; break;   // 有这个属性就是已完成
                case "DUE":
                    if (ParseDate(value, parms) is { } d) { due = d.when; dueHasTime = !d.allDay; }
                    break;
                case "PRIORITY":
                    // RFC 5545:1-4 高 / 5 中 / 6-9 低;0 = 未定。Apple 提醒事项用的就是 1/5/9。
                    if (int.TryParse(value.Trim(), out var pv) && pv > 0)
                        priority = pv <= 4 ? TodoPriority.High : pv == 5 ? TodoPriority.Medium : TodoPriority.Low;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(summary)) return null;   // 空条目不并入(同 TodoCenter.IsBlank)
        if (status == "COMPLETED") completed = true;

        return new TodoItem(
            Id: "", Title: summary!.Trim(), Kind: kind, Done: completed,
            Due: due, DueHasTime: dueHasTime, Priority: priority,
            Notes: NullIfBlank(notes),
            Source: "apple", ExternalId: NullIfBlank(uid));
    }

    // ---------------------------------------------------------------- 折行合并
    /// <summary>
    /// 把折行接回去。续行 = 以空格或制表符开头的行(RFC 5545 §3.1)。
    /// ★ 同时兼容 CRLF / LF / CR 三种换行 —— 服务端给什么都得能读。
    /// </summary>
    public static List<string> Unfold(string ics)
    {
        var raw = ics.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var outp = new List<string>();
        foreach (var r in raw)
        {
            if (r.Length > 0 && (r[0] == ' ' || r[0] == '\t') && outp.Count > 0)
                outp[^1] += r[1..];                      // 去掉那一个前导空白后接上
            else
                outp.Add(r);
        }
        return outp;
    }

    // ---------------------------------------------------------------- 单行拆解
    /// <summary>把 "NAME;PARAM=V:VALUE" 拆成三部分。冒号在参数的引号里时不算分隔符。</summary>
    static bool TryParseLine(string line, out (string name, Dictionary<string, string> parms, string value) result)
    {
        result = ("", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "");
        if (string.IsNullOrWhiteSpace(line)) return false;

        var inQuote = false;
        var colon = -1;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuote = !inQuote;
            else if (line[i] == ':' && !inQuote) { colon = i; break; }
        }
        if (colon < 0) return false;

        var head = line[..colon];
        var value = line[(colon + 1)..];

        var parts = SplitUnquoted(head, ';');
        if (parts.Count == 0) return false;
        var name = parts[0].Trim();
        var parms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < parts.Count; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq <= 0) continue;
            parms[parts[i][..eq].Trim()] = parts[i][(eq + 1)..].Trim().Trim('"');
        }
        result = (name, parms, value);
        return true;
    }

    static List<string> SplitUnquoted(string s, char sep)
    {
        var outp = new List<string>();
        var sb = new StringBuilder();
        var q = false;
        foreach (var c in s)
        {
            if (c == '"') { q = !q; sb.Append(c); }
            else if (c == sep && !q) { outp.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        outp.Add(sb.ToString());
        return outp;
    }

    /// <summary>还原 RFC 5545 的转义:\\n -> 换行、\\, \\; \\\\ -> 字面字符。</summary>
    public static string Unescape(string v)
    {
        if (string.IsNullOrEmpty(v)) return v ?? "";
        var sb = new StringBuilder(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] != '\\' || i + 1 >= v.Length) { sb.Append(v[i]); continue; }
            var n = v[++i];
            sb.Append(n switch { 'n' or 'N' => '\n', ',' => ',', ';' => ';', '\\' => '\\', _ => n });
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 时间
    /// <summary>
    /// 解析 DTSTART/DTEND 的值。返回(时间, 是否全天)。
    /// ★ 三种形态:
    ///   · VALUE=DATE 的 20260731            -> 全天,本地日期
    ///   · 带 Z 的 20260731T090000Z          -> UTC,转成本地时间显示
    ///   · 不带 Z 的 20260731T090000(可能带 TZID) -> 当作本地时间
    ///     (不带 TZID 数据库地解析各地时区是另一件大事;这一版按本地处理,并在界面如实说明。)
    /// </summary>
    public static (DateTime when, bool allDay)? ParseDate(string value, Dictionary<string, string> parms)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();

        var isDateOnly = parms.TryGetValue("VALUE", out var vt) && vt.Equals("DATE", StringComparison.OrdinalIgnoreCase);
        if (isDateOnly || (v.Length == 8 && !v.Contains('T')))
        {
            if (DateTime.TryParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var d))
                return (d.Date, true);
            return null;
        }

        if (v.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
        {
            if (DateTime.TryParseExact(v, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                                       DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var u))
                return (u.ToLocalTime(), false);
            return null;
        }

        if (DateTime.TryParseExact(v, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture,
                                   DateTimeStyles.None, out var l))
            return (l, false);
        return null;
    }

    // ---------------------------------------------------------------- 组装
    static Views.CalendarEvent? Build(
        List<(string name, Dictionary<string, string> parms, string value)> props, string owner, string scope,
        string? calendarName)
    {
        string? uid = null, summary = null, location = null, url = null, notes = null;
        (DateTime when, bool allDay)? start = null, end = null;

        foreach (var (name, parms, value) in props)
        {
            switch (name.ToUpperInvariant())
            {
                case "UID": uid = value.Trim(); break;
                case "SUMMARY": summary = Unescape(value); break;
                case "LOCATION": location = Unescape(value); break;
                case "URL": url = Unescape(value); break;
                case "DESCRIPTION": notes = Unescape(value); break;
                case "DTSTART": start = ParseDate(value, parms); break;
                case "DTEND": end = ParseDate(value, parms); break;
            }
        }

        if (start is null) return null;                       // 没有起始时间的条目无法安放 -> 跳过
        if (string.IsNullOrWhiteSpace(summary)) return null;  // 空日程不并入(与 CalendarData.IsBlank 同口径)

        var allDay = start.Value.allDay;
        var s = start.Value.when;
        DateTime e;
        if (end is null)
        {
            e = allDay ? s.Date : s;                          // 没给结束:全天=当天,定时=零长
        }
        else if (allDay)
        {
            // ★★ 全天的 DTEND 是【不含】的:20260731~20260802 表示 7/31 与 8/1 两天。
            //   我们的口径含末日,所以减一天。少了这一步每条全天日程都会多出一天。
            e = end.Value.when.Date.AddDays(-1);
            if (e < s.Date) e = s.Date;                       // 防御:异常数据不让 End < Start
        }
        else
        {
            e = end.Value.when;
        }

        return new Views.CalendarEvent(
            Start: s, End: e, Title: summary!.Trim(), Owner: owner, Scope: scope,
            AllDay: allDay,
            // ★ 分类 = 它所在的那个 iCloud 日历的名字。
            //   这个名字与 CalendarGroups 里那张表用的是同一个值(都是 displayname),
            //   颜色才对得上。
            CalendarGroup: NullIfBlank(calendarName),
            Location: NullIfBlank(location), Url: NullIfBlank(url), Notes: NullIfBlank(notes),
            Source: "apple", ExternalId: NullIfBlank(uid));
    }

    static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
