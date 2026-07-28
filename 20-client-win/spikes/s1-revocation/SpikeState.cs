// In-process mock of the identity registry's membership snapshot + live-stream index,
// for P3b S1 / Spike 7. The real system splits these across registry/Edge/gateway; here they
// live in one process so the loopback spike can exercise revoke-aborts-stream and fail-closed.

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace LocalAI.Spikes;

// Membership truth: which cert fingerprints are active, a monotonic generation, and a freshness clock.
public sealed class Membership
{
    private readonly ConcurrentDictionary<string, bool> _active = new();
    private long _snapshotAt = Environment.TickCount64;
    private long _generation;

    public long Generation => Interlocked.Read(ref _generation);

    public void SetActive(string fp) { _active[fp] = true; Touch(); }
    public bool IsActive(string fp) => _active.TryGetValue(fp, out var a) && a;

    public void Revoke(string fp)
    {
        _active[fp] = false;
        Interlocked.Increment(ref _generation);
        Touch();
    }

    public bool IsFresh(long maxAgeMs) => Environment.TickCount64 - Interlocked.Read(ref _snapshotAt) <= maxAgeMs;
    public void ForceStale(long ageMs) => Interlocked.Exchange(ref _snapshotAt, Environment.TickCount64 - ageMs);
    private void Touch() => Interlocked.Exchange(ref _snapshotAt, Environment.TickCount64);
}

// Index of live streams by cert fingerprint, so a revoke can abort exactly that device's streams.
public sealed class LiveStreams
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, (CancellationTokenSource cts, HttpContext ctx)>> _byFp = new();

    public Guid Register(string fp, CancellationTokenSource cts, HttpContext ctx)
    {
        var id = Guid.NewGuid();
        _byFp.GetOrAdd(fp, _ => new()).TryAdd(id, (cts, ctx));
        return id;
    }

    public void Unregister(string fp, Guid id)
    {
        if (_byFp.TryGetValue(fp, out var m)) m.TryRemove(id, out _);
    }

    public int AbortAll(string fp)
    {
        int n = 0;
        if (_byFp.TryGetValue(fp, out var m))
            foreach (var kv in m) { try { kv.Value.cts.Cancel(); n++; } catch { } }
        return n;
    }
}
