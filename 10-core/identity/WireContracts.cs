// D? · 跨进程响应契约的【唯一登记表】—— 服务端与客户端**编译同一份**。
//
// ═══════════════════════════════════════════════════════════════════════════════
// ★★★ 这个文件存在的理由是审计 A1,而 A1 不是"忘了写断言",是"两边都写了、缝还在"
// ═══════════════════════════════════════════════════════════════════════════════
// A1 的形状:服务端把 lease_id 放在 body["lease"]["lease_id"],客户端在**顶层**找。
//   · 服务端有断言,测的是「顶层有哪些键」—— 绿;
//   · 客户端有断言,测的是「拿这个形状能不能解析」—— 也绿,因为它喂的是**自己造的**形状;
//   · **中间那条缝谁也没看** ⇒ 租约一次都没持住过,而副作用全交付了。
//
// ⇒ 两边各自都绿,恰恰是这类缺陷的**典型表现**,不是它的反证。
//   两份手抄的期望值永远会在某一天分家,而分家那天**两边都不会红** ——
//   服务端照自己那份绿,客户端照自己那份也绿。
//
// ★ 所以这里给的不是"文档",是**一份被两侧同时编译的常量**:
//   · 服务端断言:实际响应的顶层键集合 == 这里登记的那一组(反向全表,多一个少一个都红);
//   · 客户端断言:拿**照这份登记造出来的**形状去喂真正的解析器,必须解得出目标字段。
//   改了服务端而没改这里 ⇒ 服务端断言当场红;改了这里而客户端解析器没跟上 ⇒ 客户端断言红。
//   **没有"两边都绿而缝是坏的"那种组合了** —— 期望值只有一份,它没法跟自己分家。
//
// ★ 为什么放在 10-core/identity:三个 csproj(lan-edge / transport / 客户端)都已经链这个目录,
//   而它是本仓唯一一个**服务端与客户端都编译**的目录。放别处就得在两侧各留一份副本 —— 那正是病根。
//
// ★ 登记表只登记【顶层键】,不登记类型与嵌套细节:
//   顶层键是"字段搬了家 / 改了名 / 少发了一个"这类缝隙缺陷的**充分**信号,
//   而把整个 schema 抄一遍会让这份表变成需要维护的第二实现 —— 那是另一种分家。

namespace LocalAI.Identity;

/// <summary>
/// 本车道所有跨进程响应的顶层键集合。★ 每加一条路由,这里就要多一项 —— 见 <see cref="All"/>
/// 那条反向全表断言(它是这份表的"没漏登记"守卫)。
/// </summary>
public static class WireContracts
{
    /// <summary><c>GET /admin/ping</c> 的顶层键。回环管理面,客户端 HubAdmin.ProbeAsync 读它。</summary>
    public static readonly string[] AdminPing = { "ok", "hubId", "pairingWindowOpen", "serverCert" };

    /// <summary>
    /// <c>/admin/ping</c> 里 <c>serverCert</c> 子对象的键。
    /// ★★ 这一条尤其要钉:它**曾经完全没有读取方**,而 lan-edge 那行注释写着「主机界面据此报警」。
    ///   一个吐出来却没人读的状态,和没有这个状态是一回事 —— 轮换器 fail-closed 的最后一段路。
    /// </summary>
    public static readonly string[] AdminPingServerCert =
        { "notAfter", "daysLeft", "phase", "consecutiveFailures", "lastError", "needsAttention" };

    /// <summary><c>POST /identity/renew/enroll</c> 的顶层键。客户端 RenewDeviceCertIfDue 读它。</summary>
    public static readonly string[] RenewEnroll = { "renewalId", "candidateCert", "candidateSha256", "notAfter" };

    /// <summary><c>POST /identity/renew/complete</c> 的顶层键。</summary>
    public static readonly string[] RenewComplete = { "ok", "changed" };

    /// <summary>
    /// 全部契约。★ 这是**元断言**的遍历源:两侧的自检都照它逐条核对,
    /// 于是"新加了一条路由却忘了写成对断言"会当场红 —— 而不是静默少测一条。
    /// (ASSERTION-PITFALLS 第 3b 条:判词说"每一个"时,判据就不许是手写名单。
    ///  这里手写的是**期望值**,遍历源是这张表本身 —— 两者的区别就是"新增会红"和"新增被跳过"。)
    /// </summary>
    public static readonly (string Name, string[] Keys)[] All =
    {
        ("GET /admin/ping", AdminPing),
        ("GET /admin/ping .serverCert", AdminPingServerCert),
        ("POST /identity/renew/enroll", RenewEnroll),
        ("POST /identity/renew/complete", RenewComplete),
    };

    /// <summary>
    /// 一个响应的顶层键集合是否**正好**等于登记的那一组。
    /// ★ 用集合相等,不用"包含" —— 「包含」放过了"多发了一个键"和"改了名还留着旧的",
    ///   而这两种正是字段搬家时的实际形状。
    /// </summary>
    public static bool KeysMatch(IEnumerable<string> actual, string[] expected)
        => new HashSet<string>(actual, StringComparer.Ordinal)
           .SetEquals(new HashSet<string>(expected, StringComparer.Ordinal));

    /// <summary>失败时给人看的那句话 —— 必须说出**实际**是什么,不能只说"对不上"。</summary>
    public static string Describe(IEnumerable<string> actual, string[] expected)
        => $"实际 [{string.Join(", ", actual.OrderBy(x => x, StringComparer.Ordinal))}]"
         + $" / 登记 [{string.Join(", ", expected.OrderBy(x => x, StringComparer.Ordinal))}]";
}
