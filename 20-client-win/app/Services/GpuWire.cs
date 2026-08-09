// V21 -- GPU 面的【线上形状 + 解析】那一半。★ 两个 csproj 编译**同一份**(`<Compile Link>`)。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ 为什么把它单独切出来(V21 迁移的一条**必要**切分决定):
//
//  `ComponentPicker` / `ModelsView` 整块搬进管理端(V10 §2.1:勾组件 = 对显存的单侧权威决定)。
//  而它们要读组件目录、要提交 `POST /v1/gpu/intended` —— 也就是要一套**解析器**。
//
//  两条路都不能走:
//   ✗ 把 `HubGpu.cs` 整个链进管理端 —— 它要 `HubClient` → `ClientTransport` → 半个客户端
//     (`admin-app-phase2-prereqs-2026-08-08.md` §3 点名过:「不该靠"再链一个文件"解决」);
//   ✗ 在管理端另写一份解析 —— 那是**第二个解析器**,正是 V4 已经修过一次的那个缺陷
//     (`/admin/devices` 曾有两份解析器,服务端改一个键名只会有一处被发现)。
//
//  ⇒ 第三条:`HubGpu` 拆成 `partial`。**形状与解析**在这儿(纯静态、零传输依赖),
//    **活的连接**留在 `HubGpu.cs`(客户端独有)。调用点一个字都不用改 ——
//    `HubGpu.ParseCatalog(...)` 在两个工程里是**同一个方法**。
//
//  ★ 这与 `ProcRun.cs`(V19)· `WireContracts.cs`(D93 裁定④)是同一条手法:
//    一份代码,两个 csproj 编它,**不许复制**。复制的那天两份会漂,而漂了不会有任何东西红。
// ══════════════════════════════════════════════════════════════════════════════

using System.Text.Json;

namespace LocalAI.Client.Services;

/// <summary>中枢 GPU 快照(客户端副本)。字段与 gateway 的 Snapshot.to_json 一一对应。</summary>
public sealed record HubGpuSnapshot(
    long Generation,
    string State,
    double TotalGiB,
    double? FreeGiB,
    double VramBudget,
    double DesktopFloor,
    double? NonAiInferredGiB,
    IReadOnlyList<string> Committed,
    IReadOnlyList<string> Intended,
    bool Stale,
    string? SamplerError,
    // ★★ P4-S16b:**按需驻留**那一半。与 Committed 分成两个字段,**永不合并** ——
    //   「驻留」从此有两层含义(你勾的常驻 / 系统按你的授权临时装的),
    //   合并会让用户以为自己勾过它(D90 裁定③:D24「cap」那个亏)。
    IReadOnlyList<string> TransientResident,
    // ★★★ D87③:显存压力让位。**主机与副机都靠它知道刚才发生了什么** ——
    //   D87③ 原文点名要防的就是「只在主机上弹,副机那边任务凭空失败而人不知道为什么」。
    GpuPressure? Pressure)
{
    /// <summary>★ 非 AI 占用永远是**推算**值 —— WDDM 不暴露逐进程显存,说不出占用者的名字。</summary>
    public const string NonAiNote = "桌面/其它程序的占用是算出来的,说不出是哪个程序(系统不提供)";
}

/// <summary>
/// 显存压力态(D87③)。★ <c>Active</c> 与 <c>Notice</c> 是**两件事**,不合并:
/// Notice 说"刚才让了什么",Active 说"现在还紧不紧" —— 通知过期不等于压力解除。
/// </summary>
public sealed record GpuPressure(bool Active, double FloorGiB, PressureNotice? Notice);

/// <summary>
/// 一次让位通知。★ <c>Components</c> 是**被让掉的组件**,<c>AffectedLeaseIds</c> 是
/// 被它打断的租约 —— 客户端据后者把自己的任务转成**暂停**(不是失败)。
/// </summary>
public sealed record PressureNotice(
    string UnloadReason,
    IReadOnlyList<string> Components,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> AffectedLeaseIds,
    double? FreeGiBBefore,
    string Message)
{
    /// <summary>
    /// 给人看的一句话。★ 说清三件事:**是谁让的 · 让了什么 · 任务是暂停不是失败**。
    /// <para>★★ 「暂停不是失败」是这条裁定的核心 —— 失败是终点,暂停不是。
    /// 文案把它说反了,用户就会去重做一件本来只要点一下「再开」的事。</para>
    /// </summary>
    public string Describe()
    {
        var what = Components.Count > 0 ? string.Join("、", Components) : "(未点名组件)";
        var before = FreeGiBBefore is { } f ? $"(当时可用 {f:0.00} GiB)" : "";
        return $"别的程序需要显存{before},已让出:{what}。相关任务**已暂停**,不是失败 —— "
               + "可以在任务进度里再开。";
    }
}

/// <summary>目录里的一个组件。peak / display / note 全部由中枢下发,客户端不自己编。</summary>
public sealed record GpuComponent(
    string Id, string Display, string Kind, double PeakGiB, string Note,
    bool Intended, bool Committed, IReadOnlyList<string> Aliases,
    // ★ P4-S16b:这个组件有没有被授权按需装载 · 此刻是不是正按需装着。
    //   两件事分开:授权是**用户的意思**,按需装着是**当前事实**。
    bool PermittedOnDemand, bool TransientResident);

/// <summary>一次「意图即起」的结果(POST /v1/gpu/intent)。</summary>
public sealed record IntentOutcome(bool Ok, string Code, string Alias, string Component,
                                   string Message, string Plane)
{
    /// <summary>给用户看的一句话。★ 每种不成立的下一步**完全不同**,不合并成"起不来"。</summary>
    public string Advice => Code switch
    {
        "OK" => "",                       // 装上了 —— 没什么要说的
        "ALREADY_RESIDENT" => "",         // 本来就在跑
        "no_gpu_needed" => "",            // 这个功能不占显存(比如常驻助手跑 CPU)
        // ★★★ D90 裁定①的代价段,原样说给用户听:
        "NOT_PERMITTED" or "not_permitted" =>
            "这个模型还没有被授权按需装载。请到**主机**的「系统 › 模型」里,"
            + "给它勾上『允许按需装载』。★ 这一步不能省 —— 没有它,"
            + "系统就是在你没同意的情况下自己动显存。",
        "GATE" or "gate" => Message,      // 闸的理由本身就写好了该怎么办
        "LOADER_ABSENT" or "loader_absent" =>
            "中枢的装载器没有接上,这次没有真的装载。" + Message,
        "LOAD_FAILED" or "load_failed" => "模型起不来:" + Message,
        "unknown_alias" => "中枢不认识这个功能名 —— 客户端与中枢的版本可能对不上。",
        // ★ V20-②:意图**根本没送出去**(网络/证书/中枢没起)。
        //   与 LOAD_FAILED 有意分开:那一种是中枢试过了起不来,这一种是我们连问都没问到。
        // ★★ V24:原文「到『设备』里完成配对」指着一个**两个 exe 里都不存在**的页。
        //   ★ 而这个文件被 `admin/localai-admin.csproj` 用 `<Compile Link>` 编进**管理端**
        //     (这段 Advice 在 admin/Views/ComponentPicker.cs 里就被印出来)⇒ 只写客户端那条路
        //     会把管理端说错。两边的去处**名字不同**:客户端在设置里,管理端在「主机中枢」页。
        //   ⇒ 写成一句**两边都成立**的:共同的落点是那一节的小标题「已配对的电脑」,两个 exe 里都有它。
        "not_paired" => "还没有配对到中枢,所以没法先把模型起起来。到「已配对的电脑」那一节里完成配对 —— "
                        + "客户端在「设置」最下面,管理端在「主机中枢」那一页。",
        "intent_unreachable" =>
            "没能把「我要用模型」这句话送到中枢,所以这一次**不会**自动装载模型。"
            + "现在发出去也许还能成(如果它本来就在跑),但更可能等不到回答。原因:" + Message,
        _ => Message,
    };
}

public sealed record GpuCatalog(
    long Generation,
    IReadOnlyList<GpuComponent> Components,
    double VramBudget, double TotalGiB, double DesktopFloor, double? FreeGiB, double SafetyMargin);

/// <summary>一次「点确定」的结果。★ 每种失败保留自己的 code —— 下一步动作完全不同。</summary>
/// <summary>
/// 挡住这次变更的一条租约。★★ 2026-08-05 审计 C4:这里原来只有一个 lease_id
/// (secrets.token_hex(8)),界面直接拼成「正在跑:a3f9c1d2e8b74501」——
/// **说不出是谁在占**。而中枢那边 holder/kind/granted_at/evictable 全都有,
/// 只是拒绝的时候没带上。中枢自己的注释写着:「拒绝信息要含【占用者】——
/// 谁持有、何时拿的、是否可驱逐」。
/// </summary>
public sealed record BlockingLease(string LeaseId, string Kind, string Holder,
                                   double HeldSeconds, bool Evictable)
{
    /// <summary>给人看的一行。★ 说清三件事:什么在占 · 谁的 · 能不能被自动让开。
    /// <para>★★ 2026-08-06 审计 B1:这里的 <c>Holder</c> 从此是**中枢解析出来的**设备名
    /// (证书指纹 → 成员表),不再是对方自报的 <c>MachineName</c>。
    /// 那件事很具体:这一行会出现在「有任务正在跑」的对话框里,而看对话框的人
    /// 正要据此决定要不要打断 —— 自报等于**占用者的名字由被中断方自己填**。</para>
    /// </summary>
    public string Describe()
    {
        var who = string.IsNullOrWhiteSpace(Holder) ? "未署名" : Holder;
        // ★ 中枢没给时长(HeldSeconds < 0)就**不说** —— 编一个"已 0 秒"比不说更坏。
        var held = HeldSeconds < 0 ? ""
                   : HeldSeconds >= 60 ? $",已 {HeldSeconds / 60:0.#} 分钟"
                   : $",已 {HeldSeconds:0} 秒";
        return $"{Kind}({who}{held}{(Evictable ? "・可驱逐" : "・不可驱逐")})";
    }
}

public sealed record ApplyOutcome(bool Ok, string Code, string Message, string State,
                                  IReadOnlyList<BlockingLease> Blocking, long Generation)
{
    /// <summary>给用户看的一句话。★ 逐种失败给**不同**的下一步,不合并成"失败了"。</summary>
    public string Advice => Code switch
    {
        "" => "已应用。",
        // ★★★ 2026-08-06 审计 B2:这一句原来是
        //   「中枢还没有装载器(那是 P5)。这次变更没有生效 —— 显存里不会真的多出模型来。」
        //   **两处都是假话**:装载器 S14 就落地了,而 P5 是语音 v1。服务端那半边 S15 已经改掉,
        //   客户端这半边**漏了** —— 而用户看到的正是这一句(点确定失败时直接 SetStatus(Advice))。
        //   ⇒ 比服务端那次更坏:它出现在**用户点了确定之后**,而中枢其实是能装的。
        // ★★ 钉它的断言一直是绿的,因为它只查「没有生效」在不在 ——
        //   **只钉了诚实的那半句,放过了假的那半句**。已补一条反向断言。
        // ⇒ 不再自己编:服务端的 message 已经分清了「接线失败:{原因}」与「这台实例有意没接」,
        //   照搬它。客户端只补一句"没有生效"——那是这个 code 唯一恒真的部分。
        "loader_absent" =>
            (string.IsNullOrWhiteSpace(Message) ? "中枢的装载器没有接上。" : Message)
            + " 这次变更没有生效 —— 显存里不会真的多出模型来。",
        "needs_user_choice" =>
            "有任务正在跑。等它跑完,或者选『优雅中断』再来一次。",
        "busy" => "中枢正在处理另一次变更,稍后再试。",
        "generation_conflict" =>
            "你看到的状态已经不是最新的了(别处刚改过)。已经帮你取回最新状态,请复核后再确定。",
        "vram_not_reclaimed" =>
            "卸载后显存没有被释放。这是驱动层面的问题,重启中枢通常能恢复。",
        "load_failed_rolled_back" => "装载失败,已经回滚到上一次成功的组合。",
        "rollback_failed" =>
            "装载失败且回滚也失败,中枢已进入安全停用状态。需要在主机上重新开启。",
        // ★★ P4-S10 六元组:拒绝时中枢会点名是**哪一维**拦的,这里逐维给不同的下一步 ——
        //   合并成一句「权限不足」会让人去改错的东西:撞额度的只要等一分钟,
        //   而他会跑去申请提权。(与「两种撞墙必须分开说」同一条纪律。)
        "denied_quota" =>
            "变更太频繁了(每分钟有上限)。★ 这不是权限不够 —— 等一分钟再试即可,不必去要权限。",
        "denied_action" =>
            "这台设备不能做这个操作。★ 有两种最常见:①把组件**全部取消勾选** = 卸掉中枢上的全部模型;"
            + "②改『允许按需装载』的授权 —— 那是在授权系统自己动显存(D90),"
            + "只能在主机上做。两者都和一次普通变更长得一模一样,只差参数。",
        "denied_param" =>
            "请求里有个参数超出了这台设备的允许范围(比如租约时长上限、或独占型租约)。" + Message,
        "denied_tier" =>
            "这台设备/账户在 GPU 面上没有权限。" + Message,
        _ when Code.StartsWith("gate_") => Message,   // 闸的拒绝理由本身就写好了该怎么办
        _ => Message,
    };
}

public enum HubGpuLink { NeverConnected, Live, Reconnecting, Refused }


// ══════════════════════════════════════════════════════════════════════════════
//  以下是 `HubGpu` 的**解析那一半**(全部 static、零传输依赖)。
//  ★ 与 `HubGpu.cs` 里那一半是**同一个类**(partial)—— 调用点写的仍然是 `HubGpu.ParseCatalog(...)`,
//    所以两个工程读的是同一个方法,不存在「另一份解析器」。
// ══════════════════════════════════════════════════════════════════════════════

public sealed partial class HubGpu
{
    /// <summary>
    /// 解析 <c>event: error</c> 那一帧的 data(<c>{"type","message"}</c>)。
    /// ★ 读不出就返回 null —— **不编一句**。中枢没说清楚时,说"它没说原因"比替它编一个诚实。
    /// </summary>
    internal static string? ParseStreamError(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var type = Str(r, "type");
            var msg = Str(r, "message");
            if (string.IsNullOrEmpty(type) && string.IsNullOrEmpty(msg)) return null;
            return string.IsNullOrEmpty(type) ? msg : $"{type}: {msg}";
        }
        catch { return null; }
    }

    /// <summary>解析一帧快照。★ 解析失败返回 null 而不是抛 —— 但也**不保留半份**:
    /// 半份解析出来的快照比没有更危险(几个字段是新的、几个是旧的,而界面分不出来)。</summary>
    public static HubGpuSnapshot? TryParseSnapshot(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            var vram = r.GetProperty("vram");
            var sets = r.TryGetProperty("sets", out var st) ? st : default;
            return new HubGpuSnapshot(
                Generation: r.GetProperty("generation").GetInt64(),
                State: r.TryGetProperty("state", out var s) ? (s.GetString() ?? "") : "",
                TotalGiB: vram.GetProperty("total_gib").GetDouble(),
                FreeGiB: Num(vram, "free_gib"),
                VramBudget: vram.GetProperty("vram_budget").GetDouble(),
                DesktopFloor: vram.GetProperty("desktop_floor").GetDouble(),
                NonAiInferredGiB: Num(vram, "non_ai_used_gib_inferred"),
                Committed: Strs(r, "committed"),
                Intended: sets.ValueKind == JsonValueKind.Object ? Strs(sets, "intended_resident") : Array.Empty<string>(),
                Stale: r.TryGetProperty("stale", out var stale) && stale.GetBoolean(),
                SamplerError: r.TryGetProperty("sampler_error", out var se) && se.ValueKind == JsonValueKind.String
                              ? se.GetString() : null,
                // ★ P4-S16b:按需驻留那一半。★ 中枢没给(旧版)⇒ 空表,**不是**退回 Committed:
                //   退回去会让"系统临时装的"显示成"你勾的",正是要防的那种混淆。
                TransientResident: sets.ValueKind == JsonValueKind.Object
                                   ? Strs(sets, "transient_resident") : Array.Empty<string>(),
                // ★ D87③:中枢没给(旧版)⇒ null,**不是**造一个 Active=false 的空壳:
                //   空壳读起来像"中枢说了现在不紧",而真相是"这个中枢根本不报压力"。
                Pressure: ParsePressure(r));
        }
        catch { return null; }
    }

    /// <summary>
    /// 解析快照里的 <c>pressure</c> 段(D87③)。★ 段不在 ⇒ 返回 null(**不造空壳**)。
    /// <para>★★ <c>CONTRACT:gpu.snapshot</c> / <c>CONTRACT:gpu.events.frame</c> 的一部分:
    /// 服务端那半钉住 `pressure` 在顶层键集合里,这一半证明它读得懂。</para>
    /// </summary>
    internal static GpuPressure? ParsePressure(JsonElement root)
    {
        if (!root.TryGetProperty("pressure", out var p) || p.ValueKind != JsonValueKind.Object)
            return null;
        PressureNotice? notice = null;
        if (p.TryGetProperty("notice", out var n) && n.ValueKind == JsonValueKind.Object)
        {
            var ids = new List<string>();
            if (n.TryGetProperty("affected_leases", out var al) && al.ValueKind == JsonValueKind.Array)
                foreach (var l in al.EnumerateArray())
                    if (l.ValueKind == JsonValueKind.Object && Str(l, "lease_id") is { } lid)
                        ids.Add(lid);
            notice = new PressureNotice(
                UnloadReason: Str(n, "unload_reason") ?? "",
                Components: Strs(n, "components"),
                Kinds: Strs(n, "kinds"),
                AffectedLeaseIds: ids,
                FreeGiBBefore: Num(n, "free_gib_before"),
                Message: Str(n, "message") ?? "");
        }
        return new GpuPressure(
            Active: p.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True,
            FloorGiB: Num(p, "floor_gib") ?? 0.0,
            Notice: notice);
    }

    static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static IReadOnlyList<string> Strs(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var o = new List<string>();
        foreach (var it in a.EnumerateArray()) if (it.GetString() is { } s) o.Add(s);
        return o;
    }

    public static GpuCatalog? ParseCatalog(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            var b = r.GetProperty("budget");
            var aliasMap = r.TryGetProperty("aliases_by_component", out var am) ? am : default;
            var list = new List<GpuComponent>();
            foreach (var c in r.GetProperty("components").EnumerateArray())
            {
                var id = c.GetProperty("id").GetString() ?? "";
                var aliases = new List<string>();
                if (aliasMap.ValueKind == JsonValueKind.Object
                    && aliasMap.TryGetProperty(id, out var av) && av.ValueKind == JsonValueKind.Array)
                    foreach (var a in av.EnumerateArray()) if (a.GetString() is { } s) aliases.Add(s);
                list.Add(new GpuComponent(
                    Id: id,
                    // ★ display 若为空就用 id —— 没起名字的组件也必须出现在面板上,不能被吞掉
                    Display: Str(c, "display") is { Length: > 0 } dp ? dp : id,
                    Kind: Str(c, "kind") ?? "",
                    PeakGiB: c.GetProperty("peak_gib").GetDouble(),
                    Note: Str(c, "note") ?? "",
                    Intended: c.TryGetProperty("intended", out var i) && i.GetBoolean(),
                    Committed: c.TryGetProperty("committed", out var cm) && cm.GetBoolean(),
                    Aliases: aliases,
                    PermittedOnDemand: c.TryGetProperty("permitted_on_demand", out var po)
                                       && po.ValueKind == JsonValueKind.True,
                    TransientResident: c.TryGetProperty("transient_resident", out var tr)
                                       && tr.ValueKind == JsonValueKind.True));
            }
            return new GpuCatalog(
                Generation: r.GetProperty("generation").GetInt64(),
                Components: list,
                VramBudget: b.GetProperty("vram_budget").GetDouble(),
                TotalGiB: b.GetProperty("total_gib").GetDouble(),
                DesktopFloor: b.GetProperty("desktop_floor").GetDouble(),
                FreeGiB: Num(b, "free_gib"),
                SafetyMargin: b.GetProperty("safety_margin").GetDouble());
        }
        catch { return null; }
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// 解析 <c>POST /v1/gpu/intent</c> 的响应。
    /// <para>★★★ <c>CONTRACT:gpu.intent</c> —— 这半条断言与 <c>test_gpu_broker.py</c> 里
    /// 钉顶层键集合的那半条**成对**存在(D92 硬前置)。单独任何一条都抓不住 A1 那族缺陷:
    /// 服务端那条只证明「我发的是这个形状」,客户端这条只证明「这个形状我读得懂」,
    /// **合起来**才证明这根线是通的。</para>
    /// <para>★ 成功形状:<c>{"status","intent":{...},"lease":{...}|null,"fence_token","generation"}</c>
    /// —— 与 <c>/v1/gpu/lease</c> 有意保持一致,<b>intent 里才有 code/component</b>。</para>
    /// </summary>
    public static IntentOutcome ParseIntent(int status, string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            var r = d.RootElement;
            var hasIntent = r.TryGetProperty("intent", out var it)
                            && it.ValueKind == JsonValueKind.Object;
            var alias = hasIntent ? (Str(it, "alias") ?? "") : "";
            var comp = hasIntent ? (Str(it, "component") ?? "") : "";
            var plane = hasIntent ? (Str(it, "plane") ?? "") : "";
            var code = hasIntent ? (Str(it, "code") ?? "") : "";
            var msg = hasIntent ? (Str(it, "message") ?? "") : "";
            if (r.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.Object)
            {
                // ★ error.type 是权威的失败码(intent.code 在错误响应里也有,两者一致);
                //   取不到 intent 时至少还有它 —— 不能让失败退化成一句读不懂。
                if (string.IsNullOrEmpty(code)) code = Str(er, "type") ?? "";
                if (string.IsNullOrEmpty(msg)) msg = Str(er, "message") ?? "";
            }
            var ok = status == 200 && (code == "OK" || code == "ALREADY_RESIDENT"
                                       || code == "no_gpu_needed");
            return new IntentOutcome(ok, code, alias, comp, msg, plane);
        }
        catch
        {
            // ★ 读不懂**不能**当成成功。HTTP 状态是唯一还可信的东西。
            return new IntentOutcome(false, "unreadable_response", "", "",
                                     $"中枢回了 {status},但响应读不懂", "");
        }
    }

    public static ApplyOutcome ParseOutcome(int status, string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            var r = d.RootElement;
            // 成功路径:{"result": {...}, "snapshot": {...}}
            if (status == 200 && r.TryGetProperty("result", out var res))
                return new ApplyOutcome(true, "", Str(res, "message") ?? "已应用",
                                        Str(res, "state") ?? "", Array.Empty<BlockingLease>(),
                                        Gen(r));
            var code = r.TryGetProperty("error", out var er) ? (Str(er, "type") ?? "") : "";
            var msg = r.TryGetProperty("error", out var er2) ? (Str(er2, "message") ?? "") : body;
            // ★★ blocking 现在是**结构体数组**(审计 C4)。原来按字符串读,
            //   中枢改成对象之后 GetString() 会返回 null ⇒ **整个读空**,
            //   界面上那行"正在跑:…"会一声不响地消失 —— 又是"失败与成功长得一样"。
            var blocking = new List<BlockingLease>();
            if (r.TryGetProperty("result", out var res2) && res2.TryGetProperty("blocking", out var bl)
                && bl.ValueKind == JsonValueKind.Array)
                foreach (var x in bl.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.Object) continue;
                    // ★ 已持有多久由**中枢算好再给**(held_s):granted_at 是中枢进程内的
                    //   单调时钟,客户端拿自己的表去减是两个不可比的时钟,减出来是随机数。
                    //   ★ 中枢没给就是 -1,界面据此**不显示时长**而不是编一个 0。
                    var held = x.TryGetProperty("held_s", out var hs) && hs.ValueKind == JsonValueKind.Number
                               ? hs.GetDouble() : -1;
                    blocking.Add(new BlockingLease(
                        Str(x, "lease_id") ?? "",
                        Str(x, "kind") ?? "未知",
                        Str(x, "holder") ?? "",
                        held,
                        x.TryGetProperty("evictable", out var ev) && ev.ValueKind == JsonValueKind.True));
                }
            var state = r.TryGetProperty("result", out var res3) ? (Str(res3, "state") ?? "") : "";
            return new ApplyOutcome(false, code, msg, state, blocking, Gen(r));
        }
        catch
        {
            // ══════════════════════════════════════════════════════════
            //  ★★★ 2026-08-06 夜(契约欠债 `CONTRACT:gpu.intended`)更正这段注释:
            //  它原来写的是「解析不出来**不能**当成成功」,而下面这行在 200 时
            //  **恰恰把它当成了成功**。写断言的时候照着注释写,当场变红。
            //
            //  ⇒ 代码是对的,注释说错了。真正的规则是两句,而且第二句依赖服务端那一半:
            //    ① 响应体读不懂 ⇒ **HTTP 状态是唯一还可信的东西**,只能信它;
            //    ② 而信它之所以安全,是因为服务端钉死了「事务没成 **不得回 200**」——
            //       那条断言在 `gateway.py` 的 gpu_intended 里,并由 test_gpu_broker 钉着。
            //  ★ 这正是成对断言的意义:客户端这条回落的**正确性来自另一侧的约束**,
            //    单看这一侧永远说不清它对不对。所以两句必须一起写。
            //  ★ 非 200 时给 `unreadable_response` 而不是照抄失败码:我们并不知道
            //    它是哪一种失败,编一个具体的码会让人去做一件与真相无关的事。
            // ══════════════════════════════════════════════════════════
            return new ApplyOutcome(status == 200, status == 200 ? "" : "unreadable_response",
                                    $"中枢回了 {status},但响应读不懂", "", Array.Empty<BlockingLease>(), 0);
        }
    }

    static long Gen(JsonElement r) =>
        r.TryGetProperty("snapshot", out var sn) && sn.TryGetProperty("generation", out var g)
        && g.ValueKind == JsonValueKind.Number ? g.GetInt64() : 0;

}
