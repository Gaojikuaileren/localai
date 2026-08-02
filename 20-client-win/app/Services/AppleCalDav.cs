// P3c -- iCloud CalDAV 客户端(只读拉取)。
//
// 范围裁定(用户 2026-07-31):这一版【只读】—— 只 PROPFIND/REPORT,
// 【没有】PUT/DELETE。写回 Apple 是不可逆的:一旦 UID 或分组判断有偏差,
// 就是在用户真实的 iCloud 日历里制造重复或乱改。所以那条路等这条验证过再开。
//
// 通路:HTTPS + CalDAV(RFC 4791)。Windows 上没有 Apple 日历应用,这是唯一可行的正路。
// 鉴权:Apple ID + 【专用密码】(见 AppleCredentials —— 开了两步验证后账号密码会被直接拒)。
//
// ★★ 隐私口径(这套系统的底线是"数据不出家门",这里要说清楚):
//   只读拉取意味着【我们不上传任何本机内容】—— 出去的只有认证头与查询请求,
//   回来的是你自己在 iCloud 上已有的日程。方向是单向流入,不是把家里的数据送出去。

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace LocalAI.Client.Services;

/// <param name="Url">日历集合的绝对 URL。</param>
/// <param name="DisplayName">给人看的日历名。</param>
/// <param name="ColorHex">Apple 给的日历颜色(#RRGGBB;没给就是 null)。
/// ★ 用户裁定 2026-07-31:日程分类的颜色【读自 Apple】,不是我们自己配的。
/// 拿不到就交给 CalendarGroups 按名字算一个【稳定】色,而不是随便挑。</param>
public sealed record AppleCalendar(string Url, string DisplayName, string? ColorHex = null);

public static class AppleCalDav
{
    /// <summary>iCloud 的 CalDAV 入口。★ 发现过程中服务端会给出【带编号的主机】(pNN-caldav.icloud.com),
    /// 后续请求必须跟着走 —— 一直打这个入口会拿不到数据。所以下面一律用服务端返回的绝对 URL。</summary>
    public const string Root = "https://caldav.icloud.com";

    const string DavNs = "DAV:";
    const string CalNs = "urn:ietf:params:xml:ns:caldav";
    /// <summary>Apple 私有命名空间 —— 日历颜色 calendar-color 就在这里。</summary>
    const string AppleIcalNs = "http://apple.com/ns/ical/";

    static HttpClient NewClient(string appleId, string appPassword)
    {
        // ★★ 【必须关掉自动重定向】—— 这是 .NET 的一个会让人找半天的行为:
        //   HttpClient 在【跨主机重定向时会按设计丢弃 Authorization 头】(防凭据泄露给第三方主机)。
        //   而 iCloud 认证后正是要把你转到分区主机 pNN-caldav.icloud.com ——
        //   头一丢就变成裸奔请求 -> 401,而且看起来像"密码错",极难归因。
        //   做法:关自动重定向,自己逐跳重新挂凭据(见 SendFollowingAsync)。
        //
        //   ★ 同时【关掉环境代理】—— 这套系统的底线是数据不出家门,
        //   一个环境变量就能把请求改道到别处是不可接受的(与中枢侧 trust_env 同一条纪律)。
        var h = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
        };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(45) };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{appleId}:{appPassword}"));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        // 有些服务端对没有 UA 的请求不友好;给一个老实的标识,不冒充别人。
        c.DefaultRequestHeaders.UserAgent.ParseAdd("LocalAI-Client/1.0 (CalDAV; read-only)");
        return c;
    }

    /// <summary>
    /// 发请求并【自己跟随重定向】,每一跳都重新挂上凭据与请求体。
    /// ★ 不能靠 HttpClient 自动重定向:它跨主机时会丢掉 Authorization(见 NewClient 的说明)。
    /// ★ 只跟随到 icloud.com 下的主机 —— 凭据绝不往别家域名上送。
    /// </summary>
    static async Task<(HttpStatusCode code, string body)> SendFollowingAsync(
        HttpClient c, string method, string url, string? xmlBody, int? depth, CancellationToken ct, int maxHops = 5)
    {
        for (var hop = 0; ; hop++)
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), url);
            if (xmlBody is not null)
                req.Content = new StringContent(xmlBody, Encoding.UTF8, "text/xml");   // Apple 自己用的就是 text/xml
            if (depth is { } d) req.Headers.Add("Depth", d.ToString());

            var resp = await c.SendAsync(req, ct);
            var code = resp.StatusCode;

            // 3xx:自己跟 —— 下一跳会重建请求,凭据与 body 都在
            if ((int)code is >= 300 and < 400 && resp.Headers.Location is { } loc && hop < maxHops)
            {
                var next = loc.IsAbsoluteUri ? loc : new Uri(new Uri(url), loc);
                resp.Dispose();
                if (!IsIcloudHost(next.Host))
                    return (HttpStatusCode.BadGateway, "");   // 不往非 iCloud 主机送凭据
                url = next.ToString();
                continue;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
            return (code, body);
        }
    }

    /// <summary>只允许把凭据送往 icloud.com 下的主机(含分区主机 pNN-caldav.icloud.com)。</summary>
    static bool IsIcloudHost(string host)
        => host.Equals("icloud.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".icloud.com", StringComparison.OrdinalIgnoreCase);

    static async Task<(HttpStatusCode code, string body)> PropfindAsync(
        HttpClient c, string url, int depth, string xmlBody, CancellationToken ct)
    {
        return await SendFollowingAsync(c, "PROPFIND", url, xmlBody, depth, ct);
    }

    /// <summary>
    /// 发现流程:入口 -> 当前用户主体(principal) -> 日历主目录(calendar-home-set) -> 列出日历。
    /// 返回可读的失败原因(已抹去敏感信息)。
    /// </summary>
    public static async Task<(bool ok, string message, List<AppleCalendar> calendars, List<AppleCalendar> reminders)> DiscoverAsync(
        string appleId, string appPassword, CancellationToken ct = default)
    {
        var cals = new List<AppleCalendar>();
        var todos = new List<AppleCalendar>();
        try
        {
            using var c = NewClient(appleId, appPassword);

            // ① 当前用户主体
            var (c1, b1) = await PropfindAsync(c, Root, 0,
                $"<d:propfind xmlns:d=\"{DavNs}\"><d:prop><d:current-user-principal/></d:prop></d:propfind>", ct);
            // ★★ 401 与 403 必须分开 —— 这不是文案讲究,是安全问题:
            //   iCloud 按【用户名】节流,反复认证失败会从 401 升级成 403,
            //   再继续会把用户【真实的 Apple ID 锁掉】(得去 iforgot.apple.com 重置)。
            //   所以:401 = 凭据错,停下来让人改;403 = 已被节流,必须硬性停。
            //   ★ 这条链路上【没有任何自动重试/定时重试】,全部由用户按钮驱动 —— 存心如此。
            // ★ 401 无法区分三种原因(Apple 没定义可区分的错误码),所以【一次列全】而不是猜。
            //   其中第三条最容易被当成"程序坏了":主密码一改,Apple 会静默撤销所有专用密码。
            if (c1 == HttpStatusCode.Unauthorized)
                return (false, "Apple 拒绝了这组账号密码(401)。可能是下面三种之一:" +
                               "\n① 专用密码填错了(请原样粘贴,连字符不要去掉);" +
                               "\n② 填成了 Apple ID 主密码 —— 必须用【专用密码】;" +
                               "\n③ 专用密码已失效 —— 改过/重置过 Apple ID 主密码的话,Apple 会把所有专用密码全部撤销,需要重新生成。" +
                               "\n⚠ 别反复试 —— 多次失败会被 Apple 锁账号。", cals, todos);
            if (c1 == HttpStatusCode.Forbidden)
                return (false, "Apple 暂时拒绝了这个账号的请求(403)。★ 这通常是前面失败次数太多被临时节流了。" +
                               "请【停下来】隔一段时间再试,并先确认专用密码是对的 —— " +
                               "继续重试可能导致 Apple ID 被锁。", cals, todos);
            if ((int)c1 >= 400)
                return (false, $"连接 iCloud 失败(HTTP {(int)c1})。", cals, todos);

            var principal = FirstHref(b1, "current-user-principal");
            if (principal is null) return (false, "连上了,但没能从响应里找到账号主体(current-user-principal)。", cals, todos);
            var principalUrl = Absolute(Root, principal);

            // ② 日历主目录
            var (c2, b2) = await PropfindAsync(c, principalUrl, 0,
                $"<d:propfind xmlns:d=\"{DavNs}\" xmlns:c=\"{CalNs}\"><d:prop><c:calendar-home-set/></d:prop></d:propfind>", ct);
            if ((int)c2 >= 400) return (false, $"取日历主目录失败(HTTP {(int)c2})。", cals, todos);

            var home = FirstHref(b2, "calendar-home-set");
            if (home is null) return (false, "没找到日历主目录(calendar-home-set)。", cals, todos);
            var homeUrl = Absolute(principalUrl, home);

            // ③ 列出日历集合
            var (c3, b3) = await PropfindAsync(c, homeUrl, 1,
                $"<d:propfind xmlns:d=\"{DavNs}\" xmlns:c=\"{CalNs}\" xmlns:i=\"{AppleIcalNs}\"><d:prop>" +
                "<d:resourcetype/><d:displayname/><c:supported-calendar-component-set/>" +
                "<i:calendar-color/>" +          // ★ Apple 私有属性:日历颜色(#RRGGBBAA)
                "</d:prop></d:propfind>", ct);
            if ((int)c3 >= 400) return (false, $"列出日历失败(HTTP {(int)c3})。", cals, todos);

            foreach (var (href, name, isCal, comps, color) in ParseCollections(b3))
            {
                if (!isCal) continue;
                // ★ 日历(VEVENT)与提醒事项(VTODO)在 iCloud 里就是【两类集合】,
                //   靠 supported-calendar-component-set 区分。分开带出去,让用户各自勾选 ——
                //   混在一起的话"日历"里会冒出一堆待办。
                var url = Absolute(homeUrl, href);
                var disp = string.IsNullOrWhiteSpace(name) ? "(未命名)" : name;
                var hex = CalendarGroups.Normalize(color);
                if (comps.Contains("VTODO") && !comps.Contains("VEVENT"))
                    todos.Add(new AppleCalendar(url, disp, hex));
                else if (comps.Count == 0 || comps.Contains("VEVENT"))
                    cals.Add(new AppleCalendar(url, disp, hex));
            }
            if (cals.Count == 0 && todos.Count == 0)
                return (false, "连上了,但没有找到任何日历或提醒事项清单。", cals, todos);
            return (true, $"已连接,找到 {cals.Count} 个日历、{todos.Count} 个提醒事项清单。", cals, todos);
        }
        catch (TaskCanceledException)
        {
            return (false, "连接 iCloud 超时。", cals, todos);
        }
        catch (Exception ex)
        {
            // ★ 抹掉可能夹带的认证头/密码再交出去 —— 异常消息会被显示,也可能落进 crash.log
            return (false, "连接出错:" + AppleCredentials.Redact(ex.Message), cals, todos);
        }
    }

    /// <summary>
    /// 拉取某个日历在 [from, to) 区间内的日程。
    /// ★ 限定区间而不是全量:一份用了十年的日历可能有上万条,全量拉既慢又没必要。
    /// </summary>
    public static async Task<(bool ok, string message, List<Views.CalendarEvent> events, int skipped)> FetchAsync(
        string appleId, string appPassword, AppleCalendar cal, DateTime from, DateTime to,
        string owner, string scope, CancellationToken ct = default)
    {
        var evs = new List<Views.CalendarEvent>();
        try
        {
            using var c = NewClient(appleId, appPassword);
            var body =
                $"<c:calendar-query xmlns:d=\"{DavNs}\" xmlns:c=\"{CalNs}\">" +
                "<d:prop><d:getetag/><c:calendar-data/></d:prop>" +
                "<c:filter><c:comp-filter name=\"VCALENDAR\">" +
                "<c:comp-filter name=\"VEVENT\">" +
                $"<c:time-range start=\"{Stamp(from)}\" end=\"{Stamp(to)}\"/>" +
                "</c:comp-filter></c:comp-filter></c:filter></c:calendar-query>";

            using var req = new HttpRequestMessage(new HttpMethod("REPORT"), cal.Url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/xml"),
            };
            req.Headers.Add("Depth", "1");
            using var resp = await c.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if ((int)resp.StatusCode >= 400)
                return (false, $"拉取「{cal.DisplayName}」失败(HTTP {(int)resp.StatusCode})。", evs, 0);

            var skipped = 0;
            foreach (var ics in ParseCalendarData(text))
            {
                // ★ 把【这个日历的名字】一并交下去 —— 它就是这批日程的分类
                var (part, sk) = ICalParser.ParseEvents(ics, owner, scope, cal.DisplayName);
                evs.AddRange(part);
                skipped += sk;
            }
            return (true, $"「{cal.DisplayName}」取到 {evs.Count} 条。", evs, skipped);
        }
        catch (TaskCanceledException)
        {
            return (false, $"拉取「{cal.DisplayName}」超时。", evs, 0);
        }
        catch (Exception ex)
        {
            return (false, "拉取出错:" + AppleCredentials.Redact(ex.Message), evs, 0);
        }
    }

    // ★★ 原 FetchTodosAsync(拉取提醒事项清单里的 VTODO)已删除(2026-08-02,D57):
    //   待办改成【纯本机】、不接任何外部源。
    //   ★ 上面 DiscoverAsync 里【区分 VTODO 集合】那一段要留着 —— 它现在的用处是
    //     把提醒事项清单【排除】在日历勾选列表之外,否则用户会在日历里看到"购物清单"
    //     这种条目,勾了却永远拉不到日程。

    static string Stamp(DateTime t) => t.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");

    // ---------------------------------------------------------------- XML 解析
    /// <summary>取某个属性下的第一个 href。命名空间不敏感 —— 服务端用什么前缀都认。</summary>
    public static string? FirstHref(string xml, string propLocalName)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var node = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == propLocalName);
            var href = node?.Descendants().FirstOrDefault(e => e.Name.LocalName == "href");
            return string.IsNullOrWhiteSpace(href?.Value) ? null : href!.Value.Trim();
        }
        catch { return null; }
    }

    /// <summary>把 multistatus 里每个 response 拆成(href, 显示名, 是否日历, 支持的组件, 颜色)。</summary>
    public static List<(string href, string name, bool isCalendar, HashSet<string> comps, string? color)> ParseCollections(string xml)
    {
        var outp = new List<(string, string, bool, HashSet<string>, string?)>();
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var r in doc.Descendants().Where(e => e.Name.LocalName == "response"))
            {
                var href = r.Elements().FirstOrDefault(e => e.Name.LocalName == "href")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(href)) continue;
                var name = r.Descendants().FirstOrDefault(e => e.Name.LocalName == "displayname")?.Value?.Trim() ?? "";
                var isCal = r.Descendants().Any(e => e.Name.LocalName == "calendar");
                var comps = r.Descendants()
                             .Where(e => e.Name.LocalName == "comp")
                             .Select(e => (e.Attribute("name")?.Value ?? "").ToUpperInvariant())
                             .Where(v => v.Length > 0)
                             .ToHashSet();
                var color = r.Descendants().FirstOrDefault(e => e.Name.LocalName == "calendar-color")?.Value?.Trim();
                outp.Add((href!, name, isCal, comps, string.IsNullOrWhiteSpace(color) ? null : color));
            }
        }
        catch { }
        return outp;
    }

    /// <summary>把 REPORT 响应里所有 calendar-data 的正文取出来(每条是一份 .ics)。</summary>
    public static List<string> ParseCalendarData(string xml)
    {
        var outp = new List<string>();
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var d in doc.Descendants().Where(e => e.Name.LocalName == "calendar-data"))
                if (!string.IsNullOrWhiteSpace(d.Value)) outp.Add(d.Value);
        }
        catch { }
        return outp;
    }

    /// <summary>把可能是相对路径的 href 变成绝对 URL(iCloud 会把后续请求指到带编号的主机上)。</summary>
    public static string Absolute(string baseUrl, string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs)) return abs.ToString();
        return Uri.TryCreate(new Uri(baseUrl), href, out var joined) ? joined.ToString() : href;
    }
}
