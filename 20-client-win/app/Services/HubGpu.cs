// P4-S9 -- 客户端持有的【中枢 GPU 状态副本】(D37「单一权威 + 副本」的副本那一半)。
//
// ★★ 这个文件存在的第一理由,是修掉一条实测发现的谎:
//   在这之前,左导航那条「显存」读的是 **本机** 显卡(VramMonitor 直调 nvml 的 index 0)。
//   在主机上碰巧没问题(本机就是中枢);而在**副机**上,它显示的是副机自己那张卡的占用,
//   标签却只写「显存」—— 用户会以为看的是中枢的 8.52 GiB 预算,实际看的是另一台机器。
//   ⇒ 本类把权威搬回中枢。拿不到中枢数据时**不许**悄悄退回本机数字冒充,
//     只能退回并**明说这是本机的卡**(见 VramSource)。
//
// ★★ 订阅而不是轮询(D37 ②)。走 GET /v1/gpu/events(SSE):
//   连上先收全量快照,之后每次世代号变化推一帧,15 秒没变化推一行心跳。
//   ★ 心跳是判据的一部分:没有心跳的话,一条**死掉**的长连接与一条"一直没变化"的
//     长连接在客户端看来一模一样 —— 那就是"失败与成功长得一样"。
//     所以这里显式记 LastFrameAt,超过 StaleAfter 没有任何帧(含心跳)即判 Live=false。
//
// ★ 断线重连用退避,但**重连期间不假装还活着**:Live 立刻转 false,界面据此改说法。

using System.Net;
using System.Text.Json;
using LocalAI.ClientTransport;

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
    string? SamplerError)
{
    /// <summary>★ 非 AI 占用永远是**推算**值 —— WDDM 不暴露逐进程显存,说不出占用者的名字。</summary>
    public const string NonAiNote = "桌面/其它程序的占用是算出来的,说不出是哪个程序(系统不提供)";
}

/// <summary>目录里的一个组件。peak / display / note 全部由中枢下发,客户端不自己编。</summary>
public sealed record GpuComponent(
    string Id, string Display, string Kind, double PeakGiB, string Note,
    bool Intended, bool Committed, IReadOnlyList<string> Aliases);

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
    /// <summary>给人看的一行。★ 说清三件事:什么在占 · 谁的 · 能不能被自动让开。</summary>
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
        "loader_absent" =>
            "中枢还没有装载器(那是 P5)。这次变更没有生效 —— 显存里不会真的多出模型来。",
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
            "这台设备不能做这个操作。★ 最常见的一种:把组件**全部取消勾选** = 卸掉中枢上的全部模型,"
            + "那只能在主机上做 —— 它和一次普通变更长得一模一样,只差参数。",
        "denied_param" =>
            "请求里有个参数超出了这台设备的允许范围(比如租约时长上限、或独占型租约)。" + Message,
        "denied_tier" =>
            "这台设备/账户在 GPU 面上没有权限。" + Message,
        _ when Code.StartsWith("gate_") => Message,   // 闸的拒绝理由本身就写好了该怎么办
        _ => Message,
    };
}

public enum HubGpuLink { NeverConnected, Live, Reconnecting, Refused }

public sealed class HubGpu : IDisposable
{
    /// <summary>超过这么久没收到**任何**帧(含心跳)即判定连接已死。服务端心跳是 15 秒。</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(40);

    readonly HubClient _hub;
    CancellationTokenSource? _cts;
    Task? _loop;

    public HubGpuSnapshot? Snapshot { get; private set; }
    public HubGpuLink Link { get; private set; } = HubGpuLink.NeverConnected;
    public string? LastError { get; private set; }
    public DateTime LastFrameAt { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// ★ 有没有一份【可信且新鲜】的中枢数据。界面只在这条为真时才敢显示主机显存的数字。
    /// ★★ 2026-08-05(审计 A1):判据从 LastFrameAt 换成 **LastDataAt** ——
    ///   前者**被心跳刷新**,于是"数据新鲜"这个判断一直被一个不带任何数字的东西喂着。
    ///   中枢现在心跳自带数据,所以正常情况下两者同步;换成 LastDataAt 是为了
    ///   **接上一个只发裸心跳的中枢时能看出来**,而不是继续显示一个冻住的数字。
    /// </summary>
    public bool HasFreshData =>
        Snapshot is not null && Link == HubGpuLink.Live
        && (DateTime.UtcNow - LastDataAt) < StaleAfter;

    public event Action? Changed;

    public HubGpu(HubClient hub) => _hub = hub;

    public void Start()
    {
        if (_loop is not null && !_loop.IsCompleted) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ep = _hub.TryDial();
                if (_hub.Profile is null || ep is null)
                {
                    // 还没配对 —— 不是错误,只是没有中枢可订阅。
                    Link = HubGpuLink.NeverConnected;
                    Notify();
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }
                await Transport.OpenStream(_hub.Profile, ep, "/v1/gpu/events", OnLine, ct);
                // 正常读到流尾 = 服务端关了 -> 走重连
                Link = HubGpuLink.Reconnecting;
                LastError = "中枢关闭了推送流";
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // ★ 401/403 与"连不上"不同:前者重连一万次也没用,别把它当网络抖动无限重试。
                Link = ex.Message.Contains("被拒") ? HubGpuLink.Refused : HubGpuLink.Reconnecting;
                LastError = ex.Message;
            }
            Notify();
            if (ct.IsCancellationRequested) return;
            try { await Task.Delay(backoff, ct); } catch { return; }
            backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
        }
    }

    Task OnLine(string line)
    {
        // ══════════════════════════════════════════════════════════════
        //  ★★★ 2026-08-05 修:LastFrameAt 原来在**解析之前**无条件刷新,
        //  而 Snapshot 只在解析成功时更新。于是中枢发来读不懂的帧时(双方版本对不上、
        //  字段改了名),客户端会进入一个**最坏的稳态**:
        //      Link=Live · LastFrameAt 一直是新的 · Snapshot 冻结在最后一帧好数据
        //  ⇒ HasFreshData 恒为真,界面**一直显示几小时前的数字,而且看不出来**。
        //  ★ 这条恰好会让「拿不到主机数据就说主机未连接」那个判据**永远不触发** ——
        //    一条坏在"永远说一切正常"上的新鲜度闸,比没有闸更糟。
        //  ⇒ 心跳照常刷新(它本来就是用来区分"没变化"和"连接死了"的);
        //    但 data: 帧**只有解析成功才算收到了数据**。
        // ══════════════════════════════════════════════════════════════
        if (line.StartsWith("data: ", StringComparison.Ordinal))
        {
            var parsed = TryParseSnapshot(line[6..]);
            if (parsed is null)
            {
                // ★ 不刷新时间戳、不置 Live —— 让它按正常路径过期,和"连接死了"同样对待。
                //   读不懂对方说什么,和听不见对方说话,对使用者是同一件事。
                Link = HubGpuLink.Reconnecting;
                LastError = "中枢发来的帧读不懂(客户端与中枢版本可能对不上)";
                Notify();
                return Task.CompletedTask;
            }
            Snapshot = parsed;
            LastDataAt = DateTime.UtcNow;    // ★ 只有**带数据**的帧刷新它 —— 裸心跳不算
        }
        LastFrameAt = DateTime.UtcNow;
        if (Link != HubGpuLink.Live) { Link = HubGpuLink.Live; LastError = null; }
        Notify();
        return Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════════
    //  ★★★ 2026-08-05 审计 A1:拆开「连接活着」与「数字新鲜」。
    //
    //  原来只有 LastFrameAt 一个时间戳,**心跳也刷新它**,而 HasFreshData
    //  = Live && LastFrameAt 在 40 秒内 ⇒ **心跳在喂"数据新鲜"这个判断**。
    //  于是:别的程序吃掉 4 GiB 显存(状态没变、世代号不涨)⇒ 一帧不发 ⇒
    //  快照冻结 ⇒ 而心跳让客户端一直认为自己那份是新鲜的。数字纹丝不动,且看不出来。
    //
    //  ⇒ 中枢那边已经改成**心跳自带数据**(event: keepalive + 完整快照),
    //    所以正常情况下这两个时间戳会一起走。这里仍然分开记,是为了
    //    **接上一个只发裸心跳的中枢时能看出来** —— 那正是这条 bug 的形状,
    //    而"看不出来"是它当初能活这么久的唯一原因。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>最后一次收到**带数据**的帧。★ 裸心跳不刷新它。</summary>
    public DateTime LastDataAt { get; private set; } = DateTime.MinValue;

    /// <summary>连接还活着吗(心跳也算)。★ 这只说明线通着,**不说明数字是新的**。</summary>
    public bool LinkAlive =>
        Link == HubGpuLink.Live && (DateTime.UtcNow - LastFrameAt) < StaleAfter;

    /// <summary>
    /// 手上这份数字有多旧(秒)—— 从**收到那一帧**算起。
    /// ★ 帧发出时它自己最多旧 1 秒(中枢 1 Hz 采样);中枢的采样器真死了会由
    ///   快照的 Stale / SamplerError 标出来,不靠这个数猜。
    /// </summary>
    public double DataAgeSeconds =>
        LastDataAt == DateTime.MinValue
            ? double.PositiveInfinity
            : (DateTime.UtcNow - LastDataAt).TotalSeconds;

    void Notify() { try { Changed?.Invoke(); } catch { } }

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
                              ? se.GetString() : null);
        }
        catch { return null; }
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

    // ── 组件目录 ────────────────────────────────────────────────────
    /// <summary>取组件目录。★ 目录由中枢下发 —— 客户端**不得**自己维护一份清单。</summary>
    public async Task<GpuCatalog?> FetchCatalogAsync(CancellationToken ct = default)
    {
        var (status, body) = await _hub.CallAsync("/v1/gpu/components");
        if (status != 200) { LastError = $"取组件目录失败({status})"; return null; }
        return ParseCatalog(body);
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
                    Aliases: aliases));
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

    // ── 「点确定」= 一次事务 ────────────────────────────────────────
    /// <summary>
    /// 提交一次驻留集合变更。★ 必带 if_generation —— 挑组件要几十秒,期间桌面会变,
    /// 「预览过、确定时不过」是**必然**会发生的,世代号是两边唯一能对上账的东西。
    /// </summary>
    public async Task<ApplyOutcome> ApplyAsync(IReadOnlyList<string> ids, long ifGeneration,
                                               bool interruptRunning, CancellationToken ct = default)
    {
        var ep = _hub.TryDial();
        if (_hub.Profile is null || ep is null)
            return new ApplyOutcome(false, "not_paired", "尚未配对", "", Array.Empty<BlockingLease>(), 0);
        var (status, body) = await Transport.Send(_hub.Profile, ep, HttpMethod.Post, "/v1/gpu/intended",
            new { if_generation = ifGeneration, components = ids, interrupt_running = interruptRunning }, ct);
        return ParseOutcome(status, body);
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
            // ★ 解析不出来**不能**当成成功。HTTP 状态是唯一还可信的东西。
            return new ApplyOutcome(status == 200, status == 200 ? "" : "unreadable_response",
                                    $"中枢回了 {status},但响应读不懂", "", Array.Empty<BlockingLease>(), 0);
        }
    }

    static long Gen(JsonElement r) =>
        r.TryGetProperty("snapshot", out var sn) && sn.TryGetProperty("generation", out var g)
        && g.ValueKind == JsonValueKind.Number ? g.GetInt64() : 0;

    public void Dispose() => Stop();
}
