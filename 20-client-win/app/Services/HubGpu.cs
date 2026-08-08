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
        "not_paired" => "还没有配对到中枢,所以没法先把模型起起来。到「设备」里完成配对。",
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
                // ★ V13:路由由 HubClient 定 —— 主机打回环网关,副机走 Edge。
                //   这里**不再自己拼 (Profile, dial)**:两处各判一次路由,迟早会岔开。
                if (_hub.BusinessRoute() is null)
                {
                    // 还没配对、也没判成主机 —— 不是错误,只是没有中枢可订阅。
                    Link = HubGpuLink.NeverConnected;
                    Notify();
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }
                await _hub.OpenBusinessStreamAsync("/v1/gpu/events", OnLine, ct);
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

    /// <summary>
    /// 上一行 <c>event:</c> 报的帧类型。★ SSE 是**逐行**协议:`event:` 和 `data:` 是两行,
    /// 不记住上一行就没法知道这一行的 data 是哪一种帧。
    /// </summary>
    string _sseEvent = "";

    /// <summary>
    /// 中枢在推送流里**显式报错**的那一帧(<c>event: error</c>)带来的消息。
    /// ★ 与「帧读不懂」分开:前者是中枢说"我这边出事了",后者是我们读不懂它说什么。
    /// </summary>
    public string? HubStreamError { get; private set; }

    /// <summary>data 帧里承载快照的三种事件名。★ 闭集 —— 新增一种必须显式加进来。</summary>
    internal static readonly string[] SnapshotEvents = { "snapshot", "update", "keepalive" };

    Task OnLine(string line)
    {
        // ══════════════════════════════════════════════════════════════
        //  ★★★ 2026-08-06(契约欠债 `CONTRACT:gpu.events.frame`):记住 `event:` 那一行。
        //
        //  服务端的推送流有**四种**帧:snapshot / update / keepalive(都带完整快照)
        //  与 **error**(`{"type","message"}` —— 中枢那边显式写着
        //  「推送流崩了要**说出来**,不能静默断开」)。
        //  而此前客户端**只看 `data:`**:error 帧的 data 拿去 TryParseSnapshot 必然失败
        //  ⇒ 被记成「中枢发来的帧读不懂(版本可能对不上)」。
        //  ★ 中枢把原因说了出来,客户端把它翻译成了一句**指向别处**的猜测 ——
        //    这正是这条契约要防的形状:不是没收到,是收到了却读错了它的种类。
        // ══════════════════════════════════════════════════════════════
        if (line.StartsWith("event: ", StringComparison.Ordinal))
        {
            _sseEvent = line[7..].Trim();
            return Task.CompletedTask;      // event: 行本身不带数据,不刷新任何时间戳
        }
        if (line.StartsWith("data: ", StringComparison.Ordinal)
            && string.Equals(_sseEvent, "error", StringComparison.Ordinal))
        {
            HubStreamError = ParseStreamError(line[6..]);
            // ★ 中枢自报出错 ⇒ **不是** Live。但理由要用它给的那一句,不是我们编的。
            Link = HubGpuLink.Reconnecting;
            LastError = "中枢的推送流报错:" + (HubStreamError ?? "(它没说原因)");
            _sseEvent = "";
            Notify();
            return Task.CompletedTask;
        }
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
            HubStreamError = null;           // 又收到好数据了 ⇒ 上一次的中枢报错翻篇
            _sseEvent = "";
            // ★★ D87③:让位通知随**每一帧**过来(它挂满 TTL),这里只在指纹变化时动手。
            //   放在这条路径上是有意的:主机与副机走的是同一条 SSE ⇒ 两边都会收到,
            //   而 D87③ 点名要防的正是「只在主机上弹」。
            OnPressure(parsed.Pressure);
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

    // ── 组件目录 ────────────────────────────────────────────────────
    /// <summary>
    /// 上一次成功取到的组件目录。★ 任务抽屉靠它算「显存够不够」——
    /// 快照里**没有** peak 与 safety_margin,那两样只在目录里。
    /// <para>★ 取不到就是 null,调用方必须据此说"读不到",**不许拿旧值冒充**。</para>
    /// </summary>
    public GpuCatalog? LastCatalog { get; private set; }

    /// <summary>取组件目录。★ 目录由中枢下发 —— 客户端**不得**自己维护一份清单。</summary>
    public async Task<GpuCatalog?> FetchCatalogAsync(CancellationToken ct = default)
    {
        var (status, body) = await _hub.CallAsync("/v1/gpu/components");
        if (status != 200) { LastError = $"取组件目录失败({status})"; return null; }
        var cat = ParseCatalog(body);
        // ★ 只有解析成功才更新缓存:半份/读不懂的目录**不能**盖掉上一份好的,
        //   但也不能让它冒充新的 —— ParseCatalog 解析失败返回 null,这里就不动缓存。
        if (cat is not null) LastCatalog = cat;
        return cat;
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

    // ── 「点确定」= 一次事务 ────────────────────────────────────────
    /// <summary>
    /// 提交一次驻留集合变更。★ 必带 if_generation —— 挑组件要几十秒,期间桌面会变,
    /// 「预览过、确定时不过」是**必然**会发生的,世代号是两边唯一能对上账的东西。
    /// </summary>
    /// <param name="permittedOnDemand">
    /// 「允许按需装载」的授权集合。★★ <c>null</c> 与空数组**不是一回事**:
    /// null = 这次不动授权;空数组 = 撤销全部授权。合并的话,任何一次普通变更
    /// 都会**静默清空**用户的按需授权(服务端那半边同款判据)。
    /// ★ 只有主机档能写它(审计 B6);副机传了会拿到 403 + dimension=tool。
    /// </param>
    public async Task<ApplyOutcome> ApplyAsync(IReadOnlyList<string> ids, long ifGeneration,
                                               bool interruptRunning,
                                               IReadOnlyList<string>? permittedOnDemand = null,
                                               CancellationToken ct = default)
    {
        // ══════════════════════════════════════════════════════════════════
        //  ★★★ V13(D?):这一次调用是整条修复的**落点**。
        //  主机上它必须打回环网关 ⇒ 档位 trusted-local ⇒ `change_resident` 放行;
        //  经 Edge 的话 lan-edge 会注入指纹,网关把档位封顶 lan-device ⇒ 403 denied_action,
        //  界面上就是实机报的那句「这台设备不能做这个操作」。
        //  ★ 副机仍然走 Edge、仍然是 lan-device、仍然改不动 —— 那是规格要的。
        // ══════════════════════════════════════════════════════════════════
        if (_hub.BusinessRoute() is null)
            return new ApplyOutcome(false, "not_paired", "尚未配对", "", Array.Empty<BlockingLease>(), 0);
        // ★ 省略 ≠ 空集合 —— 所以这里也**分两个载荷**,而不是给一个默认值。
        object payload = permittedOnDemand is null
            ? new { if_generation = ifGeneration, components = ids, interrupt_running = interruptRunning }
            : new { if_generation = ifGeneration, components = ids, interrupt_running = interruptRunning,
                    permitted_on_demand = permittedOnDemand };
        var (status, body) = await _hub.SendBusinessAsync(HttpMethod.Post, "/v1/gpu/intended", payload, ct);
        return ParseOutcome(status, body);
    }

    // ══════════════════════════════════════════════════════════════════
    //  P4-S16b · 「意图即起」(D87①)—— 在对应功能里开始输入的那一刻起模型
    //
    //  ★★ 客户端**只点别名不点组件**(§8.1「换模型时客户端一行都不用改」)。
    //    别名 → 组件的桥在服务端;这里连组件 id 都不该出现。
    //  ★★ 去抖:输入是每敲一个字符触发一次,而这是一次真的网络请求 + 可能的装载。
    //    ⇒ 同一个别名在 IntentCooldown 内只发一次。★ 冷却窗口**远小于**中枢那边的
    //    租约 TTL,否则会出现"还在打字但租约已经过期"的窗口。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>同一别名两次意图之间的最小间隔。★ 必须显著小于中枢的租约 TTL。</summary>
    public static readonly TimeSpan IntentCooldown = TimeSpan.FromSeconds(20);

    readonly Dictionary<string, DateTime> _lastIntent = new(StringComparer.Ordinal);
    readonly object _intentLock = new();

    /// <summary>最后一次意图的结果 —— 界面据此显示"正在为你启动…"或那句"要先授权"。</summary>
    public IntentOutcome? LastIntent { get; private set; }

    // ══════════════════════════════════════════════════════════════════
    //  ★★★ D87③(2026-08-07 用户裁定):压力让位 → 任务暂停 → 可再开
    //
    //  用户裁定原文:「AI,让,任务暂停,并弹提示。然后在任务进度里面可以再开,
    //  然后启动需要的模型,前提是显存允许的情况,不然开始按钮是不可用的。」
    //
    //  ★ 这里是**任务进度**(TaskCenter)与 GPU 面之间的那根线。
    //    在它之前 TaskCenter 的生产写入点是 0 —— 见 TaskCenter 文件头。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>任务中心。★ 由 App 注入(与 `Vram.Hub = Gpu` 同一手法),可以为 null。</summary>
    public TaskCenter? Tasks { get; set; }

    /// <summary>
    /// 把一次成功的意图登记成一条**真实任务**。★ 这是 `TaskCenter` 的第一个生产写入点。
    /// <para>
    /// ★★ 只登记**按需装载起来的**那些(<c>plane == "transient"</c>)——
    /// 它们是唯一会被显存压力让掉的东西(D90:`committed` 一个字节都不许自动改)。
    /// 常驻组件不会被让,给它建一条"可能被暂停"的任务是**误导**。
    /// </para>
    /// <para>★ 同一个别名只留一条:意图每 20 秒可能来一次,而它们是同一件事。</para>
    /// </summary>
    void RegisterIntentTask(string alias, IntentOutcome? res)
    {
        if (Tasks is null || res is null || !res.Ok) return;
        if (!string.Equals(res.Plane, "transient", StringComparison.Ordinal)) return;
        var existing = Tasks.Tasks.FirstOrDefault(t => t.ResumeAlias == alias);
        if (existing is not null)
        {
            // ★ 已经有了:只更新它依赖的组件(中枢可能挑了 any_of 里的另一个)。
            existing.NeedsComponents = new[] { res.Component };
            return;
        }
        var t = new RunningTask
        {
            Title = $"按需模型:{alias}",
            Detail = $"{res.Component} · 按需装载(空闲会自动卸,显存紧张会让位)",
            WorkspaceKey = "model",
            Progress = -1,                       // 不确定进度:它不是一件有终点的活
            ResumeAlias = alias,
            NeedsComponents = new[] { res.Component },
        };
        Tasks.Tasks.Add(t);
    }

    /// <summary>收到一条**新的**让位通知。★ 界面据它弹提示(裁定里的「并弹提示」)。</summary>
    public event Action<PressureNotice>? PressureYielded;

    /// <summary>最近一条让位通知(界面可以随时读,不必等事件)。</summary>
    public PressureNotice? LastPressure { get; private set; }

    //: 已经处理过的通知指纹。★ 快照每秒推一帧,而通知会**挂满 TTL** ——
    //  不去重的话同一条通知会被当成新的处理几百次(每次都弹一下提示)。
    string _lastPressureKey = "";

    /// <summary>
    /// 快照到手后处理让位通知。★ 只在**指纹变化**时动手。
    /// <para>★★ 指纹用「组件 + 让位前的可用显存」而不是时间戳:中枢那个时刻是
    /// **单调钟**,客户端拿它当 id 没问题,但它不在通知里 —— 而这两样已经足够区分两次让位。</para>
    /// </summary>
    void OnPressure(GpuPressure? p)
    {
        var n = p?.Notice;
        if (n is null) { _lastPressureKey = ""; return; }
        var key = string.Join("|", n.Components) + "#" + (n.FreeGiBBefore?.ToString("0.000") ?? "-");
        if (key == _lastPressureKey) return;       // 同一条通知,已经处理过
        _lastPressureKey = key;
        LastPressure = n;
        // ★ 把受影响的任务转成**暂停**(不是失败)——理由用中枢给的那一句。
        try { Tasks?.PauseForPressure(n.AffectedLeaseIds, n.Components, n.Describe()); } catch { }
        try { PressureYielded?.Invoke(n); } catch { }
    }

    /// <summary>
    /// 「再开」:重新申请这条任务需要的模型。
    /// <para>★ 走的是与「意图即起」**同一条**端点 —— 恢复不是一条新语义,
    /// 它就是"我现在又要用它了"。★★ 成功才把任务转回 Running;
    /// 失败时**任务留在暂停态**并把中枢给的理由显示出来 —— 不能假装它恢复了。</para>
    /// </summary>
    public async Task<IntentOutcome> ResumeTaskAsync(RunningTask task, CancellationToken ct = default)
    {
        var res = await RequestIntentAsync(task.ResumeAlias, ct);
        if (res.Ok) Tasks?.Resume(task.Id);
        else task.PausedReason = res.Advice is { Length: > 0 } a ? a : res.Message;
        Notify();
        return res;
    }

    /// <summary>
    /// 声明一次「我要用这个功能」。★ 即发即忘:**绝不阻塞输入**。
    /// 调用方(输入框)只管说"有人在这里打字",拿不拿得到模型是另一件事。
    /// </summary>
    public void NoteIntent(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return;
        lock (_intentLock)
        {
            if (_lastIntent.TryGetValue(alias, out var last)
                && DateTime.UtcNow - last < IntentCooldown) return;
            _lastIntent[alias] = DateTime.UtcNow;
        }
        // ══════════════════════════════════════════════════════════════════
        //  ★ 不 await:意图是"顺手说一声",不是用户在等的操作。
        //    一次起不来的模型**不该把输入框掀翻** —— 这条意图仍然成立,一个字没改。
        //
        //  ★★★ V20-②(D?):但「不掀翻」**不等于**「不说」。
        //    改之前这里是 `catch { }`,而 `LastIntent` **一个读者都没有**
        //    (它自己的文档注释写着「界面据此显示…」—— 那句话是假的)。
        //    ⇒ 于是「模型没起来」这件事在客户端**完全静默**:
        //      用户打完字按发送,才由 chat 那一路以 503 的形式撞出来,
        //      而那时它说的是"后端没起",不是"你还没授权按需装载"。
        //
        //  ★★ 更坏的一层:抛异常时连 `LastIntent` 都不写 —— 它会停在**上一次的成功**上。
        //    一个过期的成功比一个空值糟得多:任何读它的人都会显示"好着呢"。
        //    ⇒ 失败也写,写成一条**它自己的失败码**。
        // ══════════════════════════════════════════════════════════════════
        _ = Task.Run(async () =>
        {
            IntentOutcome res;
            try
            {
                res = await RequestIntentAsync(alias);
            }
            catch (Exception ex)
            {
                // ★ 连问都没问到(网络/证书/中枢没起)。★ 这**不是** backend_unavailable:
                //   那一种是"问到了,后端没应答";这一种是"这句话根本没送出去"。下一步不同。
                res = new IntentOutcome(false, "intent_unreachable", alias, "",
                                        $"{ex.GetType().Name}: {ex.Message}", "");
            }
            SetLastIntent(res);
            RegisterIntentTask(alias, res);
            Notify();
        });
    }

    /// <summary>自检用:走**同一个** SetLastIntent —— 自检里再写一遍去重逻辑就是第二套口径。</summary>
    internal void SetLastIntentForSelftest(IntentOutcome res) => SetLastIntent(res);

    /// <summary>
    /// 记下最后一次意图的结果,并在【说法真的变了】时广播。
    /// <para>★ 只在变化时广播:意图每 20 秒可能来一次,而"还是那句话"不是新闻。
    /// 每轮重复同一句的后果不是更透明,是**训练人忽略它**(D85 第 5 条)。</para>
    /// </summary>
    void SetLastIntent(IntentOutcome res)
    {
        var before = LastIntent;
        LastIntent = res;
        if (before is not null && before.Code == res.Code && before.Advice == res.Advice) return;
        try { IntentChanged?.Invoke(); } catch { }
    }

    /// <summary>
    /// 最后一次意图的**说法**变了(不是每次意图都触发 —— 见 <see cref="SetLastIntent"/>)。
    /// <para>★★ 与 <see cref="Changed"/> 分开是有代价考虑的:<c>Changed</c> 跟着显存快照
    /// 每秒一帧,聊天界面拿它当重建信号会把输入框每秒重建一次(打字当场被打断)。
    /// 这条**只在有新话要说时**响,所以界面可以老老实实接它。</para>
    /// </summary>
    public event Action? IntentChanged;

    /// <summary>发一次意图并解析结果。★ 抽成公开方法是为了让自检能直接喂形状。</summary>
    public async Task<IntentOutcome> RequestIntentAsync(string alias, CancellationToken ct = default)
    {
        // ★ V13:与「点确定」同一条路由 —— 敲字起模型这一格也归它管(实机第二格)。
        if (_hub.BusinessRoute() is null)
            return new IntentOutcome(false, "not_paired", alias, "", "尚未配对", "");
        var (status, body) = await _hub.SendBusinessAsync(HttpMethod.Post, "/v1/gpu/intent",
                                                          new { alias }, ct);
        return ParseIntent(status, body);
    }

    /// <summary>
    /// 解析 <c>POST /v1/gpu/intent</c> 的响应。
    /// <para>★★★ <c>CONTRACT:gpu.intent</c> —— 这半条断言与 <c>test_gpu_broker.py</c> 里
    /// 钉顶层键集合的那半条**成对**存在(D92 硬前置)。单独任何一条都抓不住 A1 那族缺陷:
    /// 服务端那条只证明"我发的是这个形状",客户端这条只证明"这个形状我读得懂",
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

    public void Dispose() => Stop();
}
