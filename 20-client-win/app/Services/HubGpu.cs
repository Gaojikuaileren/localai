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

public sealed partial class HubGpu : IDisposable
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
            // ★★★ V30(用户裁定):模型装载在任务栏里**要和真任务分开、而且不要进度条**。
            //   ★ 上面那句 `Progress = -1` 说的是"我不知道进度";这一句说的是
            //     "**别给我画进度条**" —— 两件事。此前只有前者,于是通用渲染路径
            //     把它变成一条 IsIndeterminate 的、来回跑的**假**进度条。
            IsModelLoad = true,
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
        // ★★★ V30:这一条**真的要发出去了** —— 就绪闸据它进入「正在启用中」。
        //   ★ 位置在去抖之后是判据的一部分:被去抖挡下的那些**没有**发起任何装载,
        //     给它们也报一次"开始了"会让闸在一个什么都没发生的时刻亮起"正在启用中"。
        RaiseIntentStarted(alias);
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
            // ★★★ V30:**无条件**报一次"这一条落地了"。见 RaiseIntentSettled 的说明 ——
            //   它与 SetLastIntent 的「变了才广播」是两条不同的信号,合并会把闸卡死。
            RaiseIntentSettled(alias, res);
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

    // ══════════════════════════════════════════════════════════════════════
    //  ★★★ V30:给【就绪闸】用的两条信号。与上面那条 `IntentChanged` **不能合并**。
    //
    //  `IntentChanged` 是给**文案**用的:「说法变了才响」——重复同一句是在训练人忽略它。
    //  而闸要的是**这一趟走完了没有**,那是另一个问题。合并会造出一个真的会卡死的闸:
    //
    //    模型装上了(code=OK)→ 空闲被卸 → 闸落回「未启用」→ 用户打字 → 意图重发
    //    → 中枢又回 code=OK,**与上一次逐字相同** ⇒ `IntentChanged` 不响
    //    ⇒ 闸永远停在「正在启用中」,而模型其实已经好了。
    //
    //  ★ 这不是假想:`SetLastIntent` 的去重判据就是 `Code == Code && Advice == Advice`,
    //    而「起来了」这件事**每次成功的说法都一样**——去重恰好把它全吃掉。
    //  ⇒ 落地信号**无条件**响,由订阅方自己决定要不要理。
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>一条意图**真的发出去了**(已过去抖)。★ 闸据它进入「正在启用中」。</summary>
    public event Action<string>? IntentStarted;

    /// <summary>
    /// 一条意图**落地了**(成功或失败都算)。★★ **无条件**响 —— 理由见上面那段。
    /// <para>★ 带上**请求时的别名**而不是只给 IntentOutcome:响应读不懂时
    /// (<c>unreadable_response</c>)里面的 alias 是空的,而闸必须知道这是哪一条落地了 ——
    /// 不然它会给一个别名记账、让另一个别名永远停在"正在启用中"。</para>
    /// </summary>
    public event Action<string, IntentOutcome>? IntentSettled;

    void RaiseIntentStarted(string alias)
    {
        // ★ 与 Notify() 同款护栏:订阅方抛异常不该反噬发布方(尤其这里在 UI 事件线上)。
        try { IntentStarted?.Invoke(alias); } catch { }
    }

    void RaiseIntentSettled(string alias, IntentOutcome res)
    {
        try { IntentSettled?.Invoke(alias, res); } catch { }
    }

    // ★★ 自检缝:两条**分开**,不能合成一个。
    //   合起来的话「发出去了」与「回来了」在一次调用里净抵消,
    //   而闸的 InFlight 记账正是要在这两点之间被观察 —— 合并等于把要测的那段直接跳过。
    /// <summary>自检用:走**同一个**广播口报"发出去了"。</summary>
    internal void StartIntentForSelftest(string alias) => RaiseIntentStarted(alias);

    /// <summary>自检用:走**同一个**广播口报"落地了"。</summary>
    internal void SettleIntentForSelftest(string alias, IntentOutcome res) => RaiseIntentSettled(alias, res);

    /// <summary>
    /// 忘掉这个别名的去抖冷却,让**下一次**意图立刻发得出去。
    /// <para>★★★ 只有一个调用场景,而它是必需的:模型**被卸掉之后**。
    /// 去抖(20 秒)的前提是"它已经起来了,别再问";这个前提一旦不成立,
    /// 冷却就变成了一道**把人关在门外**的闸 —— 按钮灰着,而且长达 20 秒里
    /// 没有任何人在试着把它修好。用户打了字却什么都不发生,那是最坏的一种"禁用"。</para>
    /// </summary>
    public void ForgetIntentCooldown(string alias)
    {
        lock (_intentLock) _lastIntent.Remove(alias);
    }

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

    public void Dispose() => Stop();
}
