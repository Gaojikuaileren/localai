// P4-S13 客户端半边 -- 内网同步(D86):家庭待办 + 共享会话。
//
// ★★ 起因是两条实测反馈(2026-08-05):
//   「我将副机的会话提升到共享,但是我主机这边看不见」
//   「我这边添加的共享家庭待办也无法在对方应用显示」
//   查清后都不是 bug —— 是**从来没做过**。D52 写着「真正的上传/同步要等中枢接入(P4+)」。
//
// ★★★ 本文件三条硬规矩:
//
//   ① **未同步必须看得见。** 主机不在线时本地照常改(不能让人干不了活),
//      但**必须标出来** —— 不标就是又一次「看着好了实际没有」,
//      而这一次的代价是:用户以为另一台也看得到,实际那边什么都没有。
//
//   ② **只推该推的。** 范围判据客户端也过一遍,不是因为不信服务端,
//      而是**少发一次就少一次出错机会** —— 服务端那道是兜底,不是唯一一道。
//      ★ 但**判据以服务端为准**:服务端拒收时如实记下来,不当成"推成功了"。
//
//   ③ **收到的远端数据不静默覆盖本地。** 冲突时服务端已经保留了被覆盖的那一版(D86 裁定③),
//      客户端要把「这条被另一台改过」显示出来。

using System.Net;
using System.Text.Json;
using LocalAI.ClientTransport;

namespace LocalAI.Client.Services;

/// <summary>一条待推的变更。★ kind 只认服务端登记过的三种。</summary>
public sealed record SyncItem(string Kind, object Record);

/// <summary>一次推送的结局(逐条)。★ 逐条 —— 一批里有的收有的拒。</summary>
public sealed record SyncPushResult(int Accepted, int Total,
                                    IReadOnlyList<(string kind, string id, bool ok, string why)> Items,
                                    bool Superseded, string? Error);

public enum SyncLink { NeverConnected, Live, Reconnecting, Refused, NotPaired }

public sealed class SyncClient : IDisposable
{
    /// <summary>超过这么久没收到任何帧(含心跳)即判连接已死。服务端心跳 15 秒。</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(40);

    readonly HubClient _hub;
    CancellationTokenSource? _cts;
    Task? _loop;

    public SyncLink Link { get; private set; } = SyncLink.NeverConnected;
    public string? LastError { get; private set; }
    public DateTime LastFrameAt { get; private set; } = DateTime.MinValue;
    public long Generation { get; private set; }

    /// <summary>★ 有没有一份【可信且新鲜】的连接。界面只在这条为真时才敢说"已同步"。</summary>
    public bool IsLive =>
        Link == SyncLink.Live && (DateTime.UtcNow - LastFrameAt) < StaleAfter;

    /// <summary>★★ 还没推上去的变更条数。>0 时界面必须显示「未同步」。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>被另一台改过的记录(kind, id) —— 界面据此提示,不静默覆盖。</summary>
    public IReadOnlyList<(string kind, string id, string byDevice)> Conflicts => _conflicts;

    readonly List<string> _online = new();

    /// <summary>
    /// 此刻在线的设备名(含自己)。★ 判据是「中枢那边有没有一条活着的订阅」,
    /// 不是"最近推过东西" —— 后者是过去式,一台关了机的机器还会"在线"好几分钟,
    /// 而**在线状态错了比没有更坏**:用户会以为东西已经同步过去了。
    /// </summary>
    public IReadOnlyList<string> Online { get { lock (_gate) return _online.ToList(); } }

    /// <summary>除自己之外还有谁在线 —— 界面直接显示这个。</summary>
    public IReadOnlyList<string> Peers
    {
        get
        {
            lock (_gate)
                return _online.Where(x => !x.Equals(Environment.MachineName,
                                                    StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    readonly List<SyncItem> _pending = new();
    readonly List<(string kind, string id, string byDevice)> _conflicts = new();
    readonly object _gate = new();

    /// <summary>收到远端数据时回调(kind, 记录数组)。宿主负责合并进各自的 Center。</summary>
    public event Action<string, IReadOnlyList<JsonElement>>? Remote;
    public event Action? Changed;

    // ══════════════════════════════════════════════════════════════════
    //  ★★★ 2026-08-05 实机修复:此前只有「追增量」,没有「对齐」。
    //
    //  用户报「共享会话和家庭待办仍然无法双边共享」。查中枢的同步存档:
    //  **两台真机一条记录都没有**(只有测试夹具 PC-B 的),而本机存档里
    //  确实躺着 1 条 scope=家庭 的待办和 1 个 Shared=true 的会话。
    //
    //  根因:推送**只在变更那一刻**触发(Add/Update -> PushIfShared),
    //  而待推队列是**纯内存**的。于是:
    //    · 建它的时候中枢连不上 -> 进队列 -> 关掉 App -> **队列没了**
    //    · 或者它建于 S13(同步落地)之前 -> 从来没有"变更"过 -> 永远不会被推
    //  此后**再也没有任何东西会重推它** —— 因为系统里根本没有"对齐"这个动作。
    //
    //  ⇒ 连上的那一刻,把**当前全部合格数据**重新推一遍(不只是队列)。
    //    重推是安全的:服务端对同内容重推明确回 superseded=false(不算冲突),
    //    这一点 S13 就验过。★ 代价是幂等的一次全量,换来的是**系统能自愈**。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 当前**全部**该同步的记录(家庭待办 + 共享会话及其消息)。由宿主注入。
    /// ★ 不在这里去翻各 Center —— SyncClient 不该知道待办和会话长什么样。
    /// </summary>
    public Func<IEnumerable<SyncItem>>? FullSet { get; set; }

    /// <summary>
    /// 单次最多推多少条。★ **必须与服务端 sync_policy 的 max_batch 一致**。
    /// 超了服务端整批拒(denied_param),而被拒的那批**一条都不会出队** ——
    /// 于是它会永远重推、永远失败,表现为"同步一直转圈但什么都没发生"。
    /// 对齐一个消息多的共享会话正好会撞上这条。
    /// </summary>
    public const int MaxPerPush = 200;

    public SyncClient(HubClient hub) => _hub = hub;

    // ══════════════════════════════════════════════════════════════════
    //  ★★★ 2026-08-05 实机抓到的**真正**根因(比"没有对齐"更致命)。
    //
    //  症状:网关重启后 25 秒内收到 2 次 GET /v1/gpu/events,
    //  而 GET /v1/sync/events **一次都没有** —— 订阅循环根本没在跑。
    //
    //  crash.log 里躺着答案:
    //    InvalidOperationException: 调用线程无法访问此对象,因为另一个线程拥有该对象。
    //      at TextBlock.set_Text
    //      at HomeView.RefreshSyncLine()
    //      at SyncClient.FlushAsync()
    //
    //  `Changed` 是在**后台线程**上抬起的,而订阅者(主页那行同步状态)直接写了
    //  WPF 的 TextBlock.Text ⇒ 抛。而 RunAsync 里那句 `Changed?.Invoke()`
    //  **在 try/catch 之外** ⇒ 异常掀翻整个 while 循环,`_loop` 就此结束,
    //  **没有任何东西会重启它**。于是同步流永久死掉,而推送(各自 Task.Run)还偶尔能成。
    //
    //  ★ 对照 HubGpu:它一直有 `void Notify() { try { Changed?.Invoke(); } catch { } }`。
    //    **GPU 有这道护栏、同步没有** —— 这就是"GPU 流活着、同步流死了"的全部原因。
    //
    //  ⇒ 两头都修:这里加同款护栏(一个订阅者抛异常,不该让整条同步流永久死掉);
    //    HomeView 那边改成切回 UI 线程再写(那才是真正写错的地方)。
    //  ★ 只修一头都不够:光修界面,下一个订阅者还会踩;光加护栏,界面照样刷不出来。
    // ══════════════════════════════════════════════════════════════════
    void Notify()
    {
        try { Changed?.Invoke(); }
        catch { /* 订阅者自己的问题不该拖垮同步 —— 与 HubGpu.Notify 同款 */ }
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

    // ── 推 ──────────────────────────────────────────────────────────
    /// <summary>
    /// 排一条变更,立刻尝试推。★ D86 裁定②:提升为共享的那一刻就同步,内容更新也实时。
    /// ★ 推不上去**不丢** —— 进待推队列,连上之后补推;期间界面显示「未同步」。
    /// </summary>
    public void Enqueue(SyncItem item)
    {
        lock (_gate)
        {
            // 同一条记录只留最后一版 —— 连推十次改动,补推时推一条就够
            _pending.RemoveAll(x => x.Kind == item.Kind && IdOf(x.Record) == IdOf(item.Record));
            _pending.Add(item);
        }
        Notify();
        _ = Task.Run(() => FlushAsync());
    }

    static string IdOf(object rec)
    {
        try
        {
            var p = rec.GetType().GetProperty("id") ?? rec.GetType().GetProperty("Id");
            return p?.GetValue(rec)?.ToString() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 把**当前全部合格数据**重新排队并推上去。★ 连上的那一刻调 —— 见上面那段说明。
    /// 幂等:服务端对同内容重推回 superseded=false,不算冲突。
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var all = FullSet?.Invoke();
        if (all is not null)
        {
            lock (_gate)
                foreach (var it in all)
                {
                    // 与 Enqueue 同款去重:同一条记录只留最后一版
                    _pending.RemoveAll(x => x.Kind == it.Kind && IdOf(x.Record) == IdOf(it.Record));
                    _pending.Add(it);
                }
        }
        // ★ 循环推到推不动为止:一次只走 MaxPerPush 条,而对齐很可能不止一批。
        //   出队的条件仍然是"服务端明确处理过",所以推不上去的不会被误清。
        for (int round = 0; round < 64; round++)
        {
            int before;
            lock (_gate) before = _pending.Count;
            if (before == 0) return;
            var r = await FlushAsync(ct);
            int after;
            lock (_gate) after = _pending.Count;
            // ★ 一轮下来一条都没少 ⇒ 推不动了(网络断 / 被拒)。
            //   继续转只会空烧 —— 退出去等下一次连上再来。
            if (r is null || after >= before) return;
        }
    }

    /// <summary>把待推队列推上去(单批,最多 MaxPerPush 条)。★ 成功才出队 —— 失败留着下次,不静默丢。</summary>
    public async Task<SyncPushResult?> FlushAsync(CancellationToken ct = default)
    {
        List<SyncItem> batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return null;
            // ★★ 必须切批:服务端 max_batch=200,超了**整批**拒(denied_param),
            //   而被拒的那批一条都不出队 ⇒ 永远重推、永远失败。见 MaxPerPush 的说明。
            batch = _pending.Take(MaxPerPush).ToList();
        }
        if (_hub.Profile is null) return null;
        var ep = _hub.TryDial();
        if (ep is null) return null;

        try
        {
            var body = new
            {
                device = Environment.MachineName,
                items = batch.Select(b => new { kind = b.Kind, record = b.Record }).ToArray(),
            };
            var (status, text) = await Transport.Send(_hub.Profile, ep, HttpMethod.Post,
                                                     "/v1/sync/push", body, ct);
            if (status != 200)
            {
                LastError = $"推送被拒({status})";
                Notify();
                return new SyncPushResult(0, batch.Count, Array.Empty<(string, string, bool, string)>(),
                                          false, LastError);
            }
            var res = ParsePush(text);
            lock (_gate)
            {
                // ★★ 只把**服务端明确处理过**的那些出队(收了 or 按范围拒收 —— 两者都不必再推)。
                //   拒收的也出队:它按设计就不该同步,留着会永远重推。
                //   ★ 但拒收要**记下来**,不当成"推成功了"(见 SyncPushResult.Items)。
                foreach (var (k, id, _, _) in res.Items)
                    _pending.RemoveAll(x => x.Kind == k && IdOf(x.Record) == id);
            }
            LastError = null;
            Notify();
            return res;
        }
        catch (Exception ex)
        {
            // ★ 推不上去**不出队** —— 连上之后补推。期间界面显示「未同步」。
            LastError = ex.Message;
            Notify();
            return null;
        }
    }

    public static SyncPushResult ParsePush(string json)
    {
        var items = new List<(string, string, bool, string)>();
        bool sup = false;
        int acc = 0, total = 0;
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            acc = r.TryGetProperty("accepted", out var a) ? a.GetInt32() : 0;
            total = r.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
            if (r.TryGetProperty("results", out var rs) && rs.ValueKind == JsonValueKind.Array)
                foreach (var x in rs.EnumerateArray())
                {
                    var ok = x.TryGetProperty("ok", out var o) && o.GetBoolean();
                    var why = x.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "";
                    if (x.TryGetProperty("superseded", out var s) && s.ValueKind == JsonValueKind.True)
                        sup = true;
                    items.Add((Str(x, "kind"), Str(x, "id"), ok, why));
                }
        }
        catch { /* 解析不了就当没收到 —— 不假装成功 */ }
        return new SyncPushResult(acc, total, items, sup, null);
    }

    static string Str(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    // ── 订阅 ────────────────────────────────────────────────────────
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
                    Link = SyncLink.NotPaired;
                    Notify();
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }
                // ★ 报上自己的名字 —— 中枢据此维护在线名单,另一台才知道我们在不在。
                //   判据是"这条订阅活着",所以断线的那一刻对方就会看到我们掉线。
                await Transport.OpenStream(
                    _hub.Profile, ep,
                    "/v1/sync/events?device=" + Uri.EscapeDataString(Environment.MachineName),
                    OnLine, ct);
                Link = SyncLink.Reconnecting;
                LastError = "中枢关闭了同步流";
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Link = ex.Message.Contains("被拒") ? SyncLink.Refused : SyncLink.Reconnecting;
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
        // ★ 心跳也刷新 —— 它正是用来区分"没变化"和"连接死了"的那一手。
        LastFrameAt = DateTime.UtcNow;
        if (Link != SyncLink.Live)
        {
            Link = SyncLink.Live;
            LastError = null;
            // ★★★ 一连上就**对齐**,但必须**先拉后推**(见下面 _pullFirst 的说明)。
            _pullFirst = true;
        }
        if (line.StartsWith("data: ", StringComparison.Ordinal))
        {
            Absorb(line[6..]);
            // ══════════════════════════════════════════════════════════
            //  ★★★ 先拉后推(2026-08-05 用户实测带出来的第三条)。
            //
            //  用户原话:「客户端启动时也要校验一遍同步,不然一台机器关机,
            //            另外一台更新了很多就无法同步了。」
            //
            //  ★ 而顺序**不能反**:连上就先推的话,会出现这一幕 ——
            //    A 删掉一条共享待办 → B 一直关着机,不知道这件事 →
            //    B 开机,先把本地那份推上去 → **删掉的东西在 A 那边复活了**。
            //  ⇒ 必须先吃完中枢那一帧全量(里面带着 A 的墓碑),把删除落到本地,
            //    然后再推 —— 那时它已经不在 SharedSnapshot 里了,自然不会复活。
            //  ★ 判据挂在【第一帧 data 到手之后】,不是"连上之后":
            //    连上但还没收到全量的那一瞬间推,和先推没有区别。
            // ══════════════════════════════════════════════════════════
            if (_pullFirst)
            {
                _pullFirst = false;
                _ = Task.Run(() => ReconcileAsync());
            }
        }
        Notify();
        return Task.CompletedTask;
    }

    /// <summary>连上之后还欠一次对齐 —— 但要等**第一帧全量吃完**才做。见 OnLine。</summary>
    volatile bool _pullFirst;

    void Absorb(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            if (r.TryGetProperty("generation", out var g) && g.ValueKind == JsonValueKind.Number)
                Generation = g.GetInt64();
            // ★★ 在线名单(2026-08-05):中枢按"有没有活着的订阅"判,断线当场就摘。
            //   ★ 一帧里没有 online 字段时**不清空** —— 那多半是老版本的中枢,
            //     清空会让界面显示"全都离线",而那是假的。没有消息 ≠ 都不在。
            if (r.TryGetProperty("online", out var on) && on.ValueKind == JsonValueKind.Array)
            {
                var names = on.EnumerateArray()
                              .Where(e => e.ValueKind == JsonValueKind.String)
                              .Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
                lock (_gate) { _online.Clear(); _online.AddRange(names); }
            }
            if (!r.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
            foreach (var kind in new[] { "sessions", "todos", "messages" })
            {
                if (!data.TryGetProperty(kind, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                // ══════════════════════════════════════════════════════════
                //  ★★★ 2026-08-05 实机抓到:必须 Clone()。
                //
                //  JsonElement 只在**它所属的 JsonDocument 活着时**有效,而上面那句
                //  `using var d = JsonDocument.Parse(json)` 在本方法返回时就把它释放了。
                //  接手方 AbsorbRemote 用的是 `Dispatcher.BeginInvoke` —— **异步**,
                //  等它真正在 UI 线程跑起来时,文档早没了 ⇒ 每一条都抛
                //  ObjectDisposedException,而那边逐条 `catch { }` 把它们**静默吞光**。
                //
                //  表现:订阅流建起来了、帧也收到了、generation 一路在涨,
                //  而本地一条记录都不会多。实测:推了两条诊断待办,盯 30 秒纹丝不动。
                //  ⇒ Clone() 出来的元素脱离文档,之后怎么用都安全。
                // ══════════════════════════════════════════════════════════
                var list = arr.EnumerateArray().Select(e => e.Clone()).ToList();
                if (list.Count == 0) continue;
                // ★ 记下哪些是别的机器写的 —— 界面据此提示「这条被另一台改过」
                foreach (var x in list)
                {
                    var dev = Str(x, "device");
                    if (dev.Length > 0 && !dev.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    {
                        var id = Str(x, "id");
                        lock (_gate)
                        {
                            _conflicts.RemoveAll(c => c.kind == kind && c.id == id);
                            _conflicts.Add((kind, id, dev));
                            if (_conflicts.Count > 64) _conflicts.RemoveAt(0);
                        }
                    }
                }
                Remote?.Invoke(kind, list);
            }
        }
        catch { /* 半份解析出来的比没有更危险 —— 整帧丢掉,下一帧会带全量 */ }
    }

    /// <summary>用过就清 —— 提示是"这次有变化",不是永久状态。</summary>
    public void ClearConflicts()
    {
        lock (_gate) _conflicts.Clear();
        Notify();
    }

    /// <summary>界面上那一行状态。★ 没有坏消息时返回空 —— 一切正常就不该占一行。</summary>
    public string StatusLine()
    {
        if (Link == SyncLink.NotPaired) return "";      // 没配对是另一件事,别在这儿说
        if (PendingCount > 0 && !IsLive)
            return $"★ 有 {PendingCount} 项还没同步到中枢(主机不在线)—— 别的设备现在看不到";
        if (PendingCount > 0) return $"正在同步 {PendingCount} 项…";
        if (!IsLive && Link != SyncLink.NeverConnected)
            return "★ 与中枢的同步连接断了 —— 现在的改动只在这台机器上";
        // ★★ 一切正常时**只在"只有自己在线"时说话**(2026-08-05 用户要求实时在线状态)。
        //   ★ 不常驻显示"某某在线":常驻的绿字会被当成背景噪声,真出事那天就没人看了
        //     (这条纪律与上面几行同源)。而"只有你一台在"是**会影响判断**的事实 ——
        //     你现在改的东西,对面要等它开机才看得到。
        var peers = Peers;
        if (IsLive && peers.Count == 0)
            return "现在只有这台设备在线 —— 改动会存着,对方开机后自动同步过去";
        return "";
    }

    public void Dispose() => Stop();
}
