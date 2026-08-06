// P4-S16b -- 客户端持有一份 client_session 租约,并周期续租。
//
// ══════════════════════════════════════════════════════════════════════
//  ★★★ 这个文件存在的唯一理由:让「全网有没有人在用」成为一个**可以为假**的判据。
//
//  在它之前,客户端**一份租约都不持**(全仓搜 /v1/gpu/lease 的客户端调用 = 0)。
//  于是中枢那边:
//    · blocking_leases() 恒为空  ⇒ 「没人在跑」恒真
//    · _last_activity_at 几乎不刷新 ⇒ 「空闲了 N 秒」只会一直涨
//  ⇒ 按需装载的三条放行条件里有两条是**恒真式**,拿它们去卸载,
//    会把**正在打字的人**卸掉。
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
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(body);
            var r = d.RootElement.TryGetProperty("result", out var res) ? res : d.RootElement;
            LeaseId = r.TryGetProperty("lease_id", out var li) ? li.GetString() : null;
            _fence = r.TryGetProperty("fence_token", out var ft) ? ft.GetString() : null;
        }
        catch { LeaseId = null; _fence = null; }
        if (LeaseId is null || _fence is null)
        {
            // ★ 拿不到 fence_token 就**不算拿到租约** —— 没有它续不了租,
            //   而一份续不了的租约会在 TTL 之后静默消失,那时中枢会以为没人在用。
            LeaseId = null; _fence = null;
            LastError = "中枢没给 lease_id / fence_token";
            Notify();
            return false;
        }
        State = LeaseState.Held;
        LastError = null;
        Notify();
        return true;
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
