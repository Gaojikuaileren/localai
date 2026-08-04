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

    readonly List<SyncItem> _pending = new();
    readonly List<(string kind, string id, string byDevice)> _conflicts = new();
    readonly object _gate = new();

    /// <summary>收到远端数据时回调(kind, 记录数组)。宿主负责合并进各自的 Center。</summary>
    public event Action<string, IReadOnlyList<JsonElement>>? Remote;
    public event Action? Changed;

    public SyncClient(HubClient hub) => _hub = hub;

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
        Changed?.Invoke();
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

    /// <summary>把待推队列推上去。★ 成功才出队 —— 失败留着下次,不静默丢。</summary>
    public async Task<SyncPushResult?> FlushAsync(CancellationToken ct = default)
    {
        List<SyncItem> batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return null;
            batch = _pending.ToList();
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
                Changed?.Invoke();
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
            Changed?.Invoke();
            return res;
        }
        catch (Exception ex)
        {
            // ★ 推不上去**不出队** —— 连上之后补推。期间界面显示「未同步」。
            LastError = ex.Message;
            Changed?.Invoke();
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
                    Changed?.Invoke();
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }
                await Transport.OpenStream(_hub.Profile, ep, "/v1/sync/events", OnLine, ct);
                Link = SyncLink.Reconnecting;
                LastError = "中枢关闭了同步流";
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Link = ex.Message.Contains("被拒") ? SyncLink.Refused : SyncLink.Reconnecting;
                LastError = ex.Message;
            }
            Changed?.Invoke();
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
            // ★ 一连上就把攒着的补推上去 —— 否则离线期间的改动要等下一次改动才走
            _ = Task.Run(() => FlushAsync());
        }
        if (line.StartsWith("data: ", StringComparison.Ordinal))
            Absorb(line[6..]);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    void Absorb(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            if (r.TryGetProperty("generation", out var g) && g.ValueKind == JsonValueKind.Number)
                Generation = g.GetInt64();
            if (!r.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
            foreach (var kind in new[] { "sessions", "todos", "messages" })
            {
                if (!data.TryGetProperty(kind, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                var list = arr.EnumerateArray().ToList();
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
        Changed?.Invoke();
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
        return "";
    }

    public void Dispose() => Stop();
}
