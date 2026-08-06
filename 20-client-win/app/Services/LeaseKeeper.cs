// P4-S16b -- 客户端持有一份 client_session 租约,并周期续租。
//
// ══════════════════════════════════════════════════════════════════════
//  ★★★ 这个文件存在的唯一理由:让「全网有没有人在用」成为一个**可以为假**的判据。
//
//  在它之前,客户端**一份租约都不持**(全仓搜 /v1/gpu/lease 的客户端调用 = 0)。
//  于是中枢那边 `_last_activity_at` 几乎不刷新 ⇒ 「空闲了 N 秒」只会一直涨,
//  拿它去卸载会把**正在打字的人**卸掉。
//
//  ★★ 2026-08-06 留痕更正(审计 A1②):这段原文还写着
//     「· blocking_leases() 恒为空 ⇒ 「没人在跑」恒真」,并把补上它算作本类的功劳之一。
//     **那是错的,而且方向反了。** 本类持的是 components 为空的租约(见下文 :23),
//     它声明「有人在」而不是「有活在跑」—— 按同日的裁定,
//     `blocking_leases()` 已明确要求租约**点名了组件**,所以本类的租约
//     **本来就不该、今天也确实不会**出现在 blocking_leases() 里。
//     真让它非空,后果是荒谬的:**用户自己**改驻留集合会被**用户自己**的会话挡住。
//  ⇒ 本类修的是**空闲计时器**那一半恒真式,不是 blocking 那一半。
//    blocking 那一半要靠**点名了组件**的租约(agent_task / model_ref),那是另一条线。
//  ★ 那正是用户 2026-08-05 特别补的那条裁定(「回退计时器是主机和副机共享的一个」)
//    所要防的事:主机看副机像空闲,副机看主机也像空闲,于是谁都可以把对方的模型卸了。
//
//  ⇒ 本类补上那一半:**任何一台客户端活着,中枢就知道有人在用。**
// ══════════════════════════════════════════════════════════════════════
//
// ★ 为什么是 client_session 而不是 model_ref:
//   client_session 在 LEASE_KINDS 里是 evictable=false + BLOCKING_USER ——
//   语义正是「人在用,驱逐它等于把人的界面打空」。而 model_ref 是可驱逐的引用计数,
//   那是"这个模型被谁引着"的账,不是"有人坐在这儿"的账。两者不能混。
//
// ★★ 本类**不申请任何组件**(components 传空):它声明的是"有人在",不是"我要用哪个模型"。
//   要用哪个模型是另一件事(意图),归 D87 裁定后的那条路径。
//   混在一起的话,一个只是开着窗口没干活的客户端会把模型钉住不放。

using LocalAI.ClientTransport;

namespace LocalAI.Client.Services;

/// <summary>租约的当前状态。★ 三态分开 —— 它们的下一步完全不同。</summary>
public enum LeaseState
{
    /// <summary>还没拿到(没配对 / 连不上 / 被拒)。</summary>
    None,
    /// <summary>持有中,且续租正常。</summary>
    Held,
    /// <summary>★ 拿到过但**凭据对不上**了 —— 必须立刻自隐,绝不重试(重试就是双持有)。</summary>
    Fenced,
}

public sealed class LeaseKeeper : IDisposable
{
    /// <summary>租约时长。★ 续租间隔取它的三分之一 —— 丢一次续租还有两次机会。</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(90);

    /// <summary>
    /// 续租间隔。★ 不取 Ttl/2:那样丢一次就只剩一次机会,而网络抖动经常是成对出现的。
    /// 取三分之一 = 连丢两次才会真的过期,而连丢两次本身就说明链路真的断了。
    /// </summary>
    public static TimeSpan RenewEvery => TimeSpan.FromSeconds(Ttl.TotalSeconds / 3);

    readonly HubClient _hub;
    CancellationTokenSource? _cts;
    Task? _loop;

    public LeaseState State { get; private set; } = LeaseState.None;
    public string? LastError { get; private set; }
    public string? LeaseId { get; private set; }
    string? _fence;

    public event Action? Changed;

    public LeaseKeeper(HubClient hub) => _hub = hub;

    void Notify()
    {
        // ★ 与 HubGpu/SyncClient 同款护栏:一个订阅者抛异常不该掀翻整个循环。
        //   2026-08-05 实测过那个后果 —— 同步流被一次跨线程 UI 访问永久掀翻。
        try { Changed?.Invoke(); } catch { }
    }

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
        var backoff = TimeSpan.FromSeconds(3);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (LeaseId is null)
                {
                    if (await AcquireAsync(ct)) backoff = TimeSpan.FromSeconds(3);
                }
                else
                {
                    await RenewAsync(ct);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)                                  // noqa
            {
                LastError = ex.Message;
                Notify();
            }
            // ★ 没租约时用退避重试;有租约时按固定节奏续 —— 两者节奏不同是有意的:
            //   拿不到租约多半是中枢不在,狂敲没有意义;而续租是心跳,节奏必须稳。
            var wait = LeaseId is null ? backoff : RenewEvery;
            try { await Task.Delay(wait, ct); } catch { return; }
            if (LeaseId is null)
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
        }
    }

    async Task<bool> AcquireAsync(CancellationToken ct)
    {
        // ★★ Fenced 之后**不再重新申请**:凭据对不上意味着中枢那边已经把这份租约给了别人
        //   (或我们的状态已经不可信)。自动重新申请 = 换个 id 继续双持有。
        //   ⇒ 停在这里,等下一次 Start()(即用户重开客户端)。
        if (State == LeaseState.Fenced) return false;
        var ep = _hub.TryDial();
        if (_hub.Profile is null || ep is null) return false;
        // ★ if_generation 必填(与 lease 端点的既有规矩一致)—— 先取一次快照。
        var (gs, gb) = await Transport.Send(_hub.Profile, ep, HttpMethod.Get, "/v1/gpu/snapshot", null, ct);
        if (gs != 200) { LastError = $"取快照失败({gs})"; Notify(); return false; }
        long gen;
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(gb);
            gen = d.RootElement.TryGetProperty("generation", out var g) ? g.GetInt64() : 0;
        }
        catch { LastError = "快照读不懂"; Notify(); return false; }

        var (st, body) = await Transport.Send(
            _hub.Profile, ep, HttpMethod.Post, "/v1/gpu/lease",
            new
            {
                if_generation = gen,
                kind = "client_session",
                holder = Environment.MachineName,
                components = Array.Empty<string>(),   // ★ 声明"有人在",不点组件(见文件头)
                ttl_s = Ttl.TotalSeconds,
            }, ct);
        if (st != 200)
        {
            LastError = $"申请租约被拒({st})";
            Notify();
            return false;
        }
        if (!TryParseGrant(body, out var lid, out var fence))
        {
            // ★★★ 审计 A1:解析失败 **≠ 没拿到**。
            //   走到这一行时,中枢那边 grant 是**真成功**的 —— 它已经记下了一份
            //   `client_session`(evictable=false + BLOCKING_USER),而我们记不住它的 id,
            //   于是**没有任何人能续它、也没有任何人会释放它**,它要在中枢挂满整个 TTL。
            //   续租每 30 秒试一次 ⇒ 稳态并存约 3 份**没人认领的幽灵租约**。
            //   ⇒ 拿到了却记不住,就必须当场还回去。
            await ReleaseByHolderAsync(ep, "lease-parse-failed", ct);
            LeaseId = null; _fence = null;
            LastError = "中枢给的租约解析不出 lease_id / fence_token —— 已把刚拿到的那份放掉";
            Notify();
            return false;
        }
        LeaseId = lid; _fence = fence;
        State = LeaseState.Held;
        LastError = null;
        Notify();
        return true;
    }

    /// <summary>
    /// 从 <c>POST /v1/gpu/lease</c> 的 200 响应里解析出 lease_id 与 fence_token。
    /// <para>
    /// ★★★ 审计 A1 的病灶就在这里。服务端 (<c>gateway.py</c> 的 <c>grant_lease</c>) 回的是
    /// <c>{"status","lease":{…},"fence_token","generation"}</c> ——
    /// <b>lease_id 在 lease 子对象里,顶层没有它</b>;而 fence_token <b>恰好</b>在顶层。
    /// 此前这里两个都只在顶层找 ⇒ LeaseId 恒 null ⇒ AcquireAsync <b>恒返回 false</b>。
    /// </para>
    /// <para>
    /// ★ 只有 lease_id 落空、fence_token 拿得到 —— 这正是它一直没被发现的原因:
    /// 两边各自都绿(服务端发得出租约,客户端解析不抛异常),断的是**中间那根线**。
    /// </para>
    /// <para>
    /// ★★ 抽成 <c>static</c> 是为了让自检能拿**服务端真实形状的字面量**直接喂它 ——
    /// 那半条断言与 <c>test_gpu_broker.py</c> 里钉顶层键集合的那半条**成对**存在。
    /// 单独任何一条都抓不住这一族 bug:服务端那条只证明"我发的是这个形状",
    /// 客户端这条只证明"这个形状我读得懂",**合起来**才证明这根线是通的。
    /// </para>
    /// </summary>
    internal static bool TryParseGrant(string json, out string? leaseId, out string? fence)
    {
        leaseId = null; fence = null;
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(json);
            var root = d.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            // 兼容一层 result 包装(别的端点有这个形状)。
            var r = root.TryGetProperty("result", out var res)
                    && res.ValueKind == System.Text.Json.JsonValueKind.Object ? res : root;

            var hasLease = r.TryGetProperty("lease", out var lease)
                           && lease.ValueKind == System.Text.Json.JsonValueKind.Object;

            // ★ 契约的那条路:lease 子对象。
            if (hasLease && lease.TryGetProperty("lease_id", out var li))
                leaseId = li.GetString();
            // ★ 顶层兜底**不是契约的一部分** —— 契约由 test_gpu_broker 的顶层键集合断言钉死。
            //   留它只是为了万一形状再变时不至于当场瞎;它绿不代表线是通的。
            if (leaseId is null && r.TryGetProperty("lease_id", out var li2))
                leaseId = li2.GetString();

            // fence_token 走顶层(服务端单独挂出来的,Lease.to_json() 里【没有】它)。
            if (r.TryGetProperty("fence_token", out var ft)) fence = ft.GetString();
            if (fence is null && hasLease && lease.TryGetProperty("fence_token", out var ft2))
                fence = ft2.GetString();
        }
        catch { leaseId = null; fence = null; }
        // ★ 拿不到 fence_token 就**不算拿到租约** —— 没有它续不了租,
        //   而一份续不了的租约会在 TTL 之后静默消失,那时中枢会以为没人在用。
        return !string.IsNullOrEmpty(leaseId) && !string.IsNullOrEmpty(fence);
    }

    /// <summary>
    /// 按 holder 放掉租约。★ 用在「拿到了却记不住」这条路上 ——
    /// 那时我们手里没有 lease_id/fence_token,而中枢的 <c>release()</c> 两者都要,
    /// 所以只能走 <c>/v1/session/end</c>(它按 holder 匹配,正是为这种情况准备的;
    /// 且归 <c>read</c> 档,不吃用户的变更配额)。
    /// <para>
    /// ★ 副作用如实记账:它会放掉**本机名下的全部**租约。今天客户端只持
    /// <c>client_session</c> 一种,所以这正好把之前积下的幽灵一并扫掉;
    /// 但同一台机器上开两个客户端实例时,会连带放掉另一个实例那份 ——
    /// 那一个的续租会拿到 410 并在下一轮重新申请,**不会双持有**。
    /// </para>
    /// </summary>
    async Task ReleaseByHolderAsync(System.Net.IPEndPoint ep, string reason, CancellationToken ct)
    {
        try
        {
            await Transport.Send(_hub.Profile!, ep, HttpMethod.Post, "/v1/session/end",
                                 new { reason, device = Environment.MachineName }, ct);
        }
        catch
        {
            // 尽力而为:放不掉也只能等 TTL 到期。但**绝不能**因此掀翻申请路径 ——
            // 那会把"少还一份租约"升级成"客户端连租约都申请不了"。
        }
    }

    async Task RenewAsync(CancellationToken ct)
    {
        var ep = _hub.TryDial();
        if (_hub.Profile is null || ep is null) return;
        var (st, _) = await Transport.Send(
            _hub.Profile, ep, HttpMethod.Post, "/v1/gpu/lease/renew",
            new { lease_id = LeaseId, fence_token = _fence, holder = Environment.MachineName,
                  ttl_s = Ttl.TotalSeconds }, ct);
        if (st == 200) { State = LeaseState.Held; LastError = null; Notify(); return; }
        if (st == 409)
        {
            // ★★★ 条件写不匹配 —— **立刻自隐,绝不重试**。重试就是双持有。
            State = LeaseState.Fenced;
            LeaseId = null; _fence = null;
            LastError = "租约凭据对不上 —— 已停止持有(重试会造成双持有)";
            Notify();
            return;
        }
        // 410(那份已经不在了)与其它错误:丢掉本地那份,下一轮重新申请。
        LeaseId = null; _fence = null;
        State = LeaseState.None;
        LastError = $"续租失败({st})";
        Notify();
    }

    public void Dispose() => Stop();
}
