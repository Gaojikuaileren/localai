// P4-S13 客户端半边 -- 内网同步(D86):家庭待办 + 共享会话。
//
// ★★ 起因是两条实测反馈(2026-08-05):
//   「我将副机的会话提升到共享,但是我主机这边看不见」
//   「我这边添加的共享家庭待办也无法在对方应用显示」
//   查清后都不是 bug —— 是**从来没做过**。D52 写着「真正的上传/同步要等中枢接入(P4+)」。
//
// ★★★ 本文件三条硬规矩:
//
//   ① **未同步必须看得见 —— 推和拉两个方向都算。** 主机不在线时本地照常改
//      (不能让人干不了活),但**必须标出来** —— 不标就是又一次「看着好了实际没有」,
//      而代价是:用户以为另一台也看得到,实际那边什么都没有。
//      ★★ V15 补上**拉**那一侧:欠着一次全量对齐时同样要说出来(见 StatusLine)。
//      此前这条只覆盖了推,于是"我这边少了对方的东西"在界面上**没有任何位置** ——
//      而副机冷启动看不到主机的共享会话,表现的正是这一格。
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

    // ══════════════════════════════════════════════════════════════════
    //  ★★★ 开流之前那次对齐的**期限**(V15 收工前对抗式复核抓到的自伤)。
    //
    //  V15 把「拉全量 + 补推」挪到了 `Transport.OpenStream` **之前**并且 `await` 它。
    //  而那两步走的是 `Transport.Send`,它用的 HttpClient **没有设 Timeout**
    //  ⇒ 吃 .NET 的默认值 **100 秒**(对照:`OpenStream` 自己显式设了 InfiniteTimeSpan,
    //    也就是说这份传输层里"设不设超时"本来就是逐处决定的,不是漏了一个全局值)。
    //
    //  ⇒ 「TCP 接得上、对端不答话」(主机网关正在起、edge 收了连接但上游是死的)时,
    //    每一轮重连都要先耗掉最多 100 秒才轮到开流 —— 这期间这台机器
    //    **既不是 Live、也不在中枢的在线名单里**。
    //  ★★ 那正好是实机记的那句「不是启动即连」的形状 —— 而它是 V15 **自己**引进来的:
    //    改之前 PullFullAsync 只被 `Task.Run(...)` 甩出去,卡 100 秒也拦不住任何东西。
    //
    //  ⇒ 给开流前那一步一个**自己的期限**:到点就放弃、去开流,标记留着由 OnLine 补。
    //    ★ 不是"拉不到就算了" —— `_needFullPull` 还立着,界面照样说「还没跟中枢对齐」。
    //  ★ 必须 **< StaleAfter**:对齐要是能比"判连接已死"还久,
    //    界面会在流都还没开出去的时候先说自己断了。下面有断言钉着这条大小关系。
    // ══════════════════════════════════════════════════════════════════
    public static readonly TimeSpan AlignDeadline = TimeSpan.FromSeconds(10);

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
        // ══════════════════════════════════════════════════════════════
        //  ★★★ 先**落成一份表**再进锁(V15 收工前对抗式复核抓到的第二处自伤)。
        //
        //  宿主注入的 FullSet 是**惰性**的:`Todos.SharedSnapshot()` 是
        //  `_items.Where(...).Select(...)`,`Chat.SharedSnapshot()` 是 `yield return` ——
        //  真正遍历各 Center 的那些 List 发生在**这里**,而这里跑在线程池线程上。
        //
        //  V15 之前这一步只由 OnLine 甩出去(第一帧到手之后),那时 OnStartup 早返回了。
        //  V15 把它挪到了**开机路上** ⇒ UI 线程此刻可能正在改同一批 List
        //  (App 的清示例、清过期待办都在起同步流之后)⇒ `List<T>` 枚举当场抛
        //  「集合已修改」。
        //
        //  ★★ 而更坏的是它**抛到哪儿**:一路冒到 RunAsync 的 catch,被判成
        //    `Link = Reconnecting`,界面显示「与中枢的同步连接断了」——
        //    **一件本地的事被报成了网络的事**,而人会照着这句话去查网络。
        //    失败要长得和成功不一样,但也**不能长得像另一种失败**。
        //  ⇒ 就地接住:这一轮不推,待推队列一条没动,下一次连上/下一次变更自然会补。
        //  ★ 只接 InvalidOperationException(那正是「集合已修改」的类型)——
        //    宽到 catch-all 会把宿主真正的 bug 一起吞掉。
        // ══════════════════════════════════════════════════════════════
        List<SyncItem>? all;
        try { all = FullSet?.Invoke()?.ToList(); }
        catch (InvalidOperationException ex)
        {
            LastError = "这次对齐没做完(本地列表正在变动):" + ex.Message
                        + " —— 待推的一条没丢,下次连上补";
            Notify();
            return;
        }
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

    /// <summary>
    /// 解析 <c>POST /v1/sync/push</c> 的 200 响应。
    /// <para>★★★ <c>CONTRACT:sync.push</c> —— 这半条与 <c>test_sync.py</c> 里钉顶层键集合
    /// <c>{accepted,total,results,generation}</c> 的那半条**成对**存在(D92)。
    /// 单独任何一条都抓不住 A1 那族缺陷:服务端那条只证明"我发的是这个形状",
    /// 这条只证明"这个形状我读得懂",**合起来**才证明这根线是通的。</para>
    /// <para>★ <c>results</c> 逐条读 —— 一批里有的收有的拒;读丢了会让被拒的那些
    /// 要么永远重推、要么静默消失。</para>
    /// </summary>
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
                // ══════════════════════════════════════════════════════════
                //  ★★★ V15 · D?:【上线 + 每一次重连】先拉一次全量,再开流。
                //
                //  在此之前,拉全量的**唯一**触发点是「收到一帧、发现断层或解析失败」——
                //  也就是说**对齐这件事整个建立在 SSE 那条流上**。于是有一格没人守:
                //  流没建起来、或者首帧没吃下(网关刚起来、中间那层把 SSE 缓冲住、
                //  Absorb 抛异常)时,这台机器一条共享数据都拉不到,而它
                //  **不报错、不掉线、界面照样说「已同步」**。
                //  ★ 而这条平的 GET 在很多"长连接建不起来"的条件下仍然能成 ——
                //    doctor/probe 当初挑它而不是 SSE,理由就是这个。
                //
                //  ★★ 顺序仍然是**先拉后推**(D86 墓碑):A 删掉一条共享待办、B 关着机 ——
                //    B 上线要是先推本地那份,删掉的东西就在 A 那边复活了。
                //    ⇒ 拉不成就**不推**,两个标记都留着,由 OnLine 在下一帧补
                //    (清掉标记等于假装对齐过了)。
                //  ★ 这里用 ct 版本;OnLine 那边的补救走无参版本 —— 两处调的是同一个方法。
                //  ★★ 但**必须带期限**(见 AlignDeadline):这一步 await 在开流之前,
                //    而它底下那个 HttpClient 的默认超时是 100 秒 —— 不设期限的话,
                //    一台"接得上但不答话"的主机能把这条流按住一分半钟,
                //    而那正是「不是启动即连」。到点就放弃去开流,标记留着让 OnLine 补。
                // ══════════════════════════════════════════════════════════
                _needFullPull = true;
                _pullFirst = true;
                using (var align = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    align.CancelAfter(AlignDeadline);
                    if (await PullFullAsync(align.Token)) { _pullFirst = false; await ReconcileAsync(align.Token); }
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
            // ★ V15:两个标记**不在这里点** —— 开流之前 RunAsync 已经点过并且已经拉过一次了。
            //   在这儿再点一次会让每次连上都白拉/白推第二遍(幂等,但纯是浪费)。
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
            //
            //  ★★★ V15 补的那半个条件:`&& !_needFullPull` ——【全量没吃下就不推】。
            //    此前这里是**无条件**推的,而 Absorb 的 catch 是真的会走到的
            //    (同文件上方那次 ObjectDisposedException 实机事故,每一条都被吞光)。
            //    于是「墓碑没落地」与「照样把本地那份推上去」同时发生,
            //    正好凑成上面这段注释怕的那一幕:对方删掉的东西在他那边复活。
            //    ⇒ 先拉后推这条纪律,此前只挡住了"顺序反了",没挡住"拉失败了"。
            // ══════════════════════════════════════════════════════════
            if (_pullFirst && !_needFullPull)
            {
                _pullFirst = false;
                _ = Task.Run(() => ReconcileAsync());
            }
        }
        // ══════════════════════════════════════════════════════════════
        //  ★★★ V15:补全量的触发点**挪到 data 分支外面**。
        //
        //  此前它写在 `if (line.StartsWith("data: "))` **里面** ⇒ 只有【下一帧数据】
        //  能触发它。而首帧解析失败之后,下一帧数据要等到**有人改东西**才会来:
        //  家里没人动的那段时间,心跳每 15 秒到一次、连接活着、界面说「已同步」,
        //  而本机一条共享数据都没有 —— **恢复动作依赖着那个已经坏掉的东西**。
        //  ⇒ 心跳也要能把它救回来。
        //  ★ 仍然**不起定时器**(D37 ② 推送非轮询):触发源还是"帧到达",
        //    只是不再挑帧的种类。没有帧就是真的断了,那条路由 RunAsync 的重连在管。
        // ══════════════════════════════════════════════════════════════
        if (_needFullPull) _ = Task.Run(() => PullFullAsync());
        Notify();
        return Task.CompletedTask;
    }

    /// <summary>连上之后还欠一次【推】—— 但要等全量吃下才做(先拉后推)。见 RunAsync / OnLine。</summary>
    volatile bool _pullFirst;

    /// <summary>
    /// 这一帧是不是**接在我手上这份之后**的?不是就说明中间漏了。
    ///
    /// <para>★★★ 判据用帧自带的 <c>since_rev</c>:服务端首帧发 <c>snapshot()</c>(全量,
    /// <c>since_rev=0</c>),之后每帧发 <c>snapshot(since_rev=它上次发到哪)</c> —— **增量**。
    /// 所以正常情况下,下一帧的 <c>since_rev</c> 恰好等于我手上的 <c>Generation</c>。
    /// 大于它,就说明中间有一帧我没吃到,而那批更新**再也不会重发**。</para>
    ///
    /// <para>★ 抽成静态纯函数是为了能被自检直接喂形状(<c>CONTRACT:sync.events.frame</c>)。</para>
    /// </summary>
    internal static bool FrameContinues(long haveGeneration, long frameSinceRev) =>
        frameSinceRev == 0                      // 全量帧:任何时候都能接
        || frameSinceRev <= haveGeneration;     // 增量帧:必须接在我手上这份之后

    /// <summary>★ 还欠一次全量(上线 / 重连 / 断层 / 解析失败都会点上它)。见 <see cref="PullFullAsync"/>。
    /// <para>★★ 它为真时**不许推**(先拉后推),而且 <see cref="StatusLine"/> 要说出来 ——
    /// 一条"我这边少了东西"的事实,界面上不能没有位置。</para></summary>
    volatile bool _needFullPull;

    /// <summary>
    /// 吃下一帧 <c>GET /v1/sync/events</c> 的 <c>data:</c>(也用于补全量)。
    /// <para>★★★ <c>CONTRACT:sync.events.frame</c> —— 与 <c>test_sync.py</c> 里钉
    /// <c>{generation,since_rev,data,counts,online}</c> 的那半条**成对**存在(D92)。</para>
    /// </summary>
    void Absorb(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            // ══════════════════════════════════════════════════════════
            //  ★★★ 断层检测(2026-08-06 · V6)。
            //
            //  此前这里什么都不查,而下面那个 catch 的注释写着
            //  「整帧丢掉,**下一帧会带全量**」—— **那句话是错的**:
            //  只有**首帧**是全量,之后每一帧都是 `snapshot(since_rev=…)` 增量。
            //  ⇒ 丢一帧 = 那批更新**永远补不回来**,而且没有任何东西会红:
            //    订阅还活着、generation 还在涨、界面显示"已同步"。
            //  ★ 这正是「重连能对齐」掩盖掉的那个洞 —— 重连走的是首帧全量,
            //    而**流没断、只是丢了一帧**的这条路径,此前没有任何恢复手段。
            // ══════════════════════════════════════════════════════════
            long since = r.TryGetProperty("since_rev", out var sr)
                         && sr.ValueKind == JsonValueKind.Number ? sr.GetInt64() : 0;
            if (!FrameContinues(Generation, since))
            {
                _needFullPull = true;
                LastError = $"同步流有断层(这一帧从 rev {since} 起,而本机停在 {Generation})——正在补一次全量";
            }
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
        catch
        {
            // ★★★ 半份解析出来的比没有更危险 ⇒ 整帧丢掉。
            //   但**丢掉之后必须补** —— 这里原本写的是「下一帧会带全量」,
            //   而那是**一句错话**:只有首帧是全量,之后都是增量(见上方断层检测)。
            //   ⇒ 标记要补一次全量,由 `PullFullAsync` 走 GET /v1/sync/snapshot 拉回来。
            _needFullPull = true;
            LastError = "有一帧同步数据解析失败,正在补一次全量";
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  ★★★ 拉全量 —— `GET /v1/sync/snapshot` 在客户端的落点
    //       (V6 接上,2026-08-06;V15 加上「上线/重连」这个触发点,2026-08-08)
    //
    //  【V6 当时的判断,以及它错在哪一格】
    //  V6 只把它接在**流内丢帧**上,理由是"重连那条路本来就没洞 —— `/v1/sync/events`
    //  首帧就是全量",接成重连也走它就是把同一条路重复一遍、断言恒绿。
    //  ★ 那句话对了一半:**服务端首帧确实是全量**。错的是从它推出的那一步 ——
    //    「首帧是全量」不等于「客户端拿到了全量」。中间还隔着三件会失败的事:
    //      ① 流根本没建起来(网关刚起、中间那层把 SSE 缓冲住)—— 一帧都没有;
    //      ② 首帧到了但 Absorb 抛了 —— 整帧丢掉,而**恢复动作原本要等下一帧数据**,
    //         那一帧要等到有人改东西才会来(见 OnLine 里挪出去的那段);
    //      ③ 首帧到了、吃下了,但那之后我们**照样把本地那份推了上去**(V15 补的条件)。
    //    这三格里,前两格的表现完全一样:连接活着、generation 有值、界面说「已同步」,
    //    而这台机器上一条共享数据都没有。**副机冷启动看不到主机的共享会话**就落在这儿。
    //  ⇒ 用户 2026-08-07 裁:**上线拉一次,断线重连也拉**。V15 照办。
    //  ★ 这不是把首帧全量重复一遍:它跑在**开流之前**,正是流建不起来的那一格里唯一
    //    还能跑的东西 —— 那一格恰恰是首帧永远盖不到的。
    //
    //  ★ 拉回来的一律是 since_rev=0 的**全量**:断层的时候我们并不知道漏了哪几条,
    //    "从我以为的位置续拉"会把漏掉的那段永远跳过去。
    //  ★ 三个触发点(上线/重连 · 断层 · 解析失败)指向**同一个**方法、喂**同一个** Absorb ——
    //    两份解析会漂,而漂的那天自检只盯着其中一份。
    // ══════════════════════════════════════════════════════════════════
    /// <summary>★ 单飞闸:三个触发点可能同时点它,并发拉两份全量纯属浪费。</summary>
    int _pulling;

    /// <summary>拉一次全量(<c>CONTRACT:sync.snapshot</c>)。
    /// ★ 触发点:上线 / 每次重连 / 断层 / 解析失败。**不做定时轮询**(D37 ② 推送非轮询)。</summary>
    public async Task<bool> PullFullAsync(CancellationToken ct = default)
    {
        if (_hub.Profile is null) return false;
        var ep = _hub.TryDial();
        if (ep is null) return false;
        // ★ 已经有一次在路上就直接回 false:调用方据此**不推**、并且**不清标记** ——
        //   在路上那次成了会自己清掉,没成的话标记留着下一帧再来。
        if (Interlocked.Exchange(ref _pulling, 1) == 1) return false;
        try
        {
            var (status, text) = await Transport.Send(_hub.Profile, ep, HttpMethod.Get,
                                                     "/v1/sync/snapshot", null, ct);
            if (status != 200)
            {
                // ★ 拉不回来要**留着标记**下次再试 —— 清掉标记等于假装对齐过了。
                LastError = $"拉全量失败({status})";
                Notify();
                return false;
            }
            // ★ 走的是**同一个** Absorb —— 两份解析会漂移,而漂的那天只盯着其中一份。
            //   全量帧的 since_rev=0,断层判据天然放行,不会自己触发自己。
            _needFullPull = false;
            Absorb(text);
            // ★★★ 回值判据是**标记有没有真的清掉**,不是"HTTP 200 拿到了"。
            //   Absorb 解析失败时会把标记重新点上(它自己那个 catch)——
            //   那种情况下回 true 就等于告诉调用方"对齐好了,可以推了",
            //   而本机手上根本没有那份全量,墓碑也没落地 ⇒ 一推就把删掉的东西送回去。
            if (_needFullPull) { Notify(); return false; }
            LastError = null;
            Notify();
            return true;
        }
        catch (Exception ex)
        {
            LastError = "拉全量失败:" + ex.Message;
            Notify();
            return false;
        }
        finally
        {
            // ★ 无论走哪条路都要放闸 —— 漏放一次,这台机器此后**再也拉不到全量**,
            //   而它不报错、不掉线(与本文件反复防的那类静默失效同款)。
            Interlocked.Exchange(ref _pulling, 0);
        }
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
        // ══════════════════════════════════════════════════════════════
        //  ★★★ V15:欠着一次全量对齐时**必须说出来**。
        //
        //  本文件规矩①「未同步必须看得见」此前只覆盖了**推**那一侧(PendingCount>0)。
        //  **拉**那一侧一个位置都没有:连接活着、队列是空的 ⇒ 这里一行字都不说,
        //  而这台机器上可能一条对方的东西都没有。那正是"看着好了实际没有"。
        //  ★ 措辞说的是**这台机器少了东西**,不是"同步中" —— 后者会让人以为马上就好。
        // ══════════════════════════════════════════════════════════════
        if (_needFullPull)
            return "★ 还没跟中枢对齐(拉全量没成)—— 这台机器上现在可能少了对方的改动";
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
