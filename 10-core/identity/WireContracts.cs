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

    // ══════════════════════════════════════════════════════════════════════════
    //  D?(V4 · 契约欠债 · 证书/配对切片)—— 把欠债表里那 13 条补齐
    // ══════════════════════════════════════════════════════════════════════════
    //  ★ 上一轮(D93)只登记了 4 条:两条 /admin/ping 相关 + 两条续签。
    //    欠债表(90-ops/gate/check_contract_pairs.py)点名这一族还欠 13 条,
    //    并逐条给了消费者的 file:line 与"读错了会怎样"。**以那张表为准**,这里只补形状。
    //
    //  ★★ 表里点出两处**单字段断言冒充成对**的,一并升级成键集合:
    //    · /pair/enroll —— 原来只钉了 `sasWordlistVersion` 一个字段;
    //    · /identity/renew/complete —— 原来只钉了 `changed` 一个字段。
    //    单字段挡不住**别的键**漂移,而 A1 恰恰是"别的键搬了家"。

    /// <summary><c>GET /admin/devices</c> 顶层。消费者 HubAdmin.DevicesAsync / HubClient.ParseDevices。</summary>
    public static readonly string[] AdminDevices = { "devices", "members", "generation" };

    /// <summary>
    /// <c>/admin/devices</c> 里 <c>devices[i]</c> 的键。★★ **这一条才是承重的**:
    /// 客户端读的是**数组元素里的**字段,而 A1 的病灶正是"字段藏在下一层"。
    /// 只钉顶层 {devices, members, generation} 的话,`deviceId` 改名照样全绿。
    /// </summary>
    public static readonly string[] AdminDevicesItem =
        { "deviceId", "displayName", "status", "approvedAt", "defaultMemberId", "certSha256Short" };

    /// <summary><c>GET /admin/pairing/pending</c> 顶层。消费者 HubAdmin.PendingAsync。</summary>
    public static readonly string[] AdminPending = { "pairingWindowOpen", "pending" };

    /// <summary><c>/admin/pairing/pending</c> 里 <c>pending[i]</c> 的键(六个词就在这一层)。</summary>
    public static readonly string[] AdminPendingItem = { "requestId", "displayName", "sas", "secondsLeft" };

    /// <summary><c>POST /admin/devices/revoke</c> 200 顶层。<c>generation</c> 是吊销真的落盘的凭据。</summary>
    public static readonly string[] AdminRevoke = { "ok", "generation" };

    /// <summary><c>POST /admin/pairing/approve</c> 200 顶层。</summary>
    public static readonly string[] AdminApprove = { "ok" };

    /// <summary><c>POST /admin/pairing/deny</c> 200 顶层。</summary>
    public static readonly string[] AdminDeny = { "ok" };

    /// <summary>
    /// 批准/拒绝的 <b>409</b> 顶层键。★ 失败分支也要钉:界面靠 <c>error</c> 说清"为什么没批成",
    /// 而这条路径最常见的失败是"这条请求已经不是 pending 了"(过期/已批过)——
    /// 读不出 error 就只剩一个光秃秃的 409,人会以为是中枢坏了。
    /// </summary>
    public static readonly string[] AdminApproveDeny409 = { "ok", "error" };

    /// <summary><c>POST /admin/pairing/window</c> 200 顶层。开窗后必须回**当前**窗口状态。</summary>
    public static readonly string[] AdminWindow = { "ok", "pairingWindowOpen" };

    /// <summary>
    /// <c>POST /pair/enroll</c> 200 顶层 —— **六个词与 CA 都在这一条里**。
    /// ★ 原来只钉了 <c>sasWordlistVersion</c> 一个字段(transport/Program.cs)。
    ///   那挡得住"版本字段没了",挡不住 <c>caCert</c> 或 <c>serverNonce</c> 改名 ——
    ///   而后两者一旦漂,SAS transcript 就对不上,客户端会把它报成**中间人攻击**。
    /// </summary>
    public static readonly string[] PairEnroll =
        { "requestId", "serverNonce", "sas", "caCert", "hubId", "sasWordlistVersion" };

    /// <summary>
    /// <c>POST /pair/status</c> 顶层 —— **三个键在每一种状态下都在**,只是后两个的**值**可能是 null。
    ///
    /// ★★★ 这条的失败分支比 happy path 更要紧(欠债表点名):
    ///   客户端轮询它,只在 <c>approved</c>/<c>certificate_issued</c> 时跳出循环,
    ///   随后**无条件** <c>claimNonce.GetString()!</c>。若哪天服务端在别的状态下也提前给出
    ///   这两个字段、或把跳出条件那几个字符串改了,客户端要么 NRE、要么拿 null 去算 challenge ——
    ///   两种都表现为「配对走不完」,而主机侧那条设备记录**永远停在 provisioning**
    ///   (2026-08-04 实测过一次:图形界面这条配对路径从来没走完过)。
    /// ⇒ 所以成对断言两侧都要覆盖 pending / denied / expired,不只 approved。
    /// </summary>
    public static readonly string[] PairStatus = { "status", "claimNonce", "candidateSha256" };

    /// <summary>客户端**跳出轮询**的那两个状态。★ 与 ClientTransport.Pair 里那一行是同一份口径。</summary>
    public static readonly string[] PairStatusProceed = { "approved", "certificate_issued" };

    /// <summary>客户端**立刻放弃**的那两个状态(再等下去也不会变)。</summary>
    public static readonly string[] PairStatusAbort = { "denied", "expired" };

    /// <summary><c>POST /pair/claim</c> 200 顶层。</summary>
    public static readonly string[] PairClaim = { "candidateCert" };

    /// <summary>
    /// <c>POST /pair/complete</c> 的应答**不是 JSON**,是一行纯文本 <c>active</c>。
    /// ★ 如实登记成文本契约,不假装它有键集合 —— 给它编一个空键集合会让
    ///   "顶层键恰好是这一组"变成恒真(空集恒等于空集),那是一条永远不会红的断言。
    /// </summary>
    public const string PairCompleteBody = "active";

    /// <summary>一条跨进程契约的登记项。<paramref name="Keys"/> 为空且 <paramref name="TextBody"/> 非空 = 文本契约。</summary>
    public sealed record Contract(string Cid, string Name, string[] Keys, string? TextBody = null);

    /// <summary>
    /// 全部契约。★ 这是**元断言**的遍历源:两侧的自检都照它逐条核对,
    /// 于是"新加了一条路由却忘了写成对断言"会当场红 —— 而不是静默少测一条。
    /// (ASSERTION-PITFALLS 第 3b 条:判词说"每一个"时,判据就不许是手写名单。
    ///  这里手写的是**期望值**,遍历源是这张表本身 —— 两者的区别就是"新增会红"和"新增被跳过"。)
    ///
    /// ★★ <c>Cid</c> 是 **ASCII 契约号**,与 90-ops 那张欠债表用的是**同一个字符串**:
    ///   服务端半边(lan-edge 自检)与客户端半边(transport/客户端自检)都把它写进断言消息,
    ///   欠债表据此判"两半在不在"。用同一个锚点,就不会出现"我以为钉的是这处、他钉的是那处"。
    ///   ★ 必须是 ASCII:它要在 cp936 的钩子环境里被读(ASSERTION-PITFALLS 第 8 条)。
    /// </summary>
    public static readonly Contract[] All =
    {
        new("CONTRACT:cert.admin.ping",            "GET /admin/ping",                    AdminPing),
        new("CONTRACT:cert.admin.ping.servercert", "GET /admin/ping .serverCert",        AdminPingServerCert),
        new("CONTRACT:cert.admin.devices",         "GET /admin/devices",                 AdminDevices),
        new("CONTRACT:cert.admin.devices.item",    "GET /admin/devices .devices[i]",     AdminDevicesItem),
        new("CONTRACT:cert.admin.pending",         "GET /admin/pairing/pending",         AdminPending),
        new("CONTRACT:cert.admin.pending.item",    "GET /admin/pairing/pending .pending[i]", AdminPendingItem),
        new("CONTRACT:cert.admin.revoke",          "POST /admin/devices/revoke",         AdminRevoke),
        new("CONTRACT:cert.admin.approve",         "POST /admin/pairing/approve",        AdminApprove),
        new("CONTRACT:cert.admin.deny",            "POST /admin/pairing/deny",           AdminDeny),
        new("CONTRACT:cert.admin.approvedeny.409", "POST /admin/pairing/{approve,deny} 409", AdminApproveDeny409),
        new("CONTRACT:cert.admin.window",          "POST /admin/pairing/window",         AdminWindow),
        new("CONTRACT:cert.pair.enroll",           "POST /pair/enroll",                  PairEnroll),
        new("CONTRACT:cert.pair.status",           "POST /pair/status",                  PairStatus),
        new("CONTRACT:cert.pair.claim",            "POST /pair/claim",                   PairClaim),
        new("CONTRACT:cert.pair.complete",         "POST /pair/complete (文本)",          Array.Empty<string>(), PairCompleteBody),
        new("CONTRACT:cert.renew.enroll",          "POST /identity/renew/enroll",        RenewEnroll),
        new("CONTRACT:cert.renew.complete",        "POST /identity/renew/complete",      RenewComplete),
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
