// P3b S1 / Spike 7 -- ConnectionContext index + revoke-aborts-stream + generation/freshness
// fail-closed, loopback only. LocalAI, decision D43 (S1, hardening S0.9).
//
// Proves the revocation core the whole membership design rests on:
//   * a device streaming; revoking it aborts its live stream within the SLO (D43: <= 2s in-flight)
//   * a new request from a revoked cert -> 401 immediately (per-request active+generation check)
//   * revoking device A does NOT affect device B's stream
//   * a stale membership snapshot -> 503 fail-closed (freshness bound; D43 S0.9: F <= revoke SLO)
//   * revocation bumps the monotonic generation

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LocalAI.Spikes;
using Microsoft.AspNetCore.Server.Kestrel.Https;

const int PORT = 18445;
const string SERVER_SAN = "localai-test.local";
const long FRESH_MS = 1000;   // freshness bound F (<= revoke SLO per D43 S0.9)
const long ABORT_SLO_MS = 2000;

int pass = 0, fail = 0;
void Assert(bool cond, string msg)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + msg); }
    else { fail++; Console.WriteLine("  FAIL  " + msg); }
}
static string Fp(X509Certificate2 c) => Convert.ToHexString(c.GetCertHash(HashAlgorithmName.SHA256));

using var ca = SpikeTls.CreateCa("LocalAI Test CA");
var caPublic = SpikeTls.PublicOf(ca);
var serverCert = SpikeTls.CreateLeaf(ca, SERVER_SAN, SERVER_SAN, serverAuth: true, clientAuth: false);
var clientA = SpikeTls.CreateLeaf(ca, "device-A", null, serverAuth: false, clientAuth: true);
var clientB = SpikeTls.CreateLeaf(ca, "device-B", null, serverAuth: false, clientAuth: true);

var membership = new Membership();
var live = new LiveStreams();
membership.SetActive(Fp(clientA));
membership.SetActive(Fp(clientB));
long gen0 = membership.Generation;

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(IPAddress.Loopback, PORT, lo => lo.UseHttps(h =>
    {
        h.ServerCertificate = serverCert;
        h.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        h.ClientCertificateValidation = SpikeTls.ClientValidator(caPublic);
    }));
});

var app = builder.Build();
app.MapGet("/api/stream", async (HttpContext ctx) =>
{
    var cert = ctx.Connection.ClientCertificate;
    if (cert is null) { ctx.Response.StatusCode = 401; return; }
    var fp = Fp(cert);

    // per-request, fail-closed checks (both done at Edge and gateway in the real system)
    if (!membership.IsFresh(FRESH_MS)) { ctx.Response.StatusCode = 503; return; }   // stale snapshot
    if (!membership.IsActive(fp)) { ctx.Response.StatusCode = 401; return; }        // revoked / unknown

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
    var id = live.Register(fp, cts, ctx);
    try
    {
        ctx.Response.Headers.ContentType = "text/event-stream";
        for (int i = 0; i < 200; i++)
        {
            cts.Token.ThrowIfCancellationRequested();
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"data: chunk-{i}\n\n"), cts.Token);
            await ctx.Response.Body.FlushAsync(cts.Token);
            await Task.Delay(80, cts.Token);
        }
    }
    catch (OperationCanceledException) { ctx.Abort(); }   // revoked mid-stream -> hard reset
    finally { live.Unregister(fp, id); }
});

await app.StartAsync();
Console.WriteLine("kestrel up on 127.0.0.1:" + PORT + " (SAN=" + SERVER_SAN + ")");

string url = $"https://{SERVER_SAN}:{PORT}/api/stream";
var fpA = Fp(clientA);

try
{
    // ---- Test A: revoke mid-stream aborts A's live stream within SLO ----
    var sw = Stopwatch.StartNew();
    long abortMs = -1;
    var clientAConn = SpikeTls.MakeMtlsClient(PORT, caPublic, clientA);
    var readA = Task.Run(async () =>
    {
        try
        {
            using var resp = await clientAConn.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            await using var s = await resp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(s);
            while (await reader.ReadLineAsync() is not null) { /* consume until aborted */ }
        }
        catch { /* connection reset on abort */ }
        abortMs = sw.ElapsedMilliseconds;
    });
    await Task.Delay(400);                       // let A stream for a bit
    long revokeMs = sw.ElapsedMilliseconds;
    int aborted = 0;
    // registry event: revoke A, then abort its live streams
    membership.Revoke(fpA);
    aborted = live.AbortAll(fpA);
    await readA;
    clientAConn.Dispose();
    Assert(aborted >= 1, $"revoke found and cancelled A's live stream ({aborted})");
    Assert(abortMs - revokeMs < ABORT_SLO_MS, $"live stream aborted within SLO ({abortMs - revokeMs} ms < {ABORT_SLO_MS})");

    // ---- Test E: generation bumped monotonically ----
    Assert(membership.Generation > gen0, $"generation advanced on revoke ({gen0} -> {membership.Generation})");

    // ---- Test B: new request from revoked A -> 401 ----
    using (var cA2 = SpikeTls.MakeMtlsClient(PORT, caPublic, clientA))
    using (var r = await cA2.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        Assert((int)r.StatusCode == 401, "revoked cert new request -> 401 (" + (int)r.StatusCode + ")");

    // ---- Test C: device B unaffected -- still streams ----
    using (var cB = SpikeTls.MakeMtlsClient(PORT, caPublic, clientB))
    using (var r = await cB.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
    {
        int got = 0;
        if ((int)r.StatusCode == 200)
        {
            await using var s = await r.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(s);
            // SSE frames are "data: ...\n\n" (data line + blank separator); count non-empty data lines.
            for (int i = 0; i < 8 && got < 2; i++) { var l = await reader.ReadLineAsync(); if (l is { Length: > 0 }) got++; }
        }
        Assert((int)r.StatusCode == 200 && got >= 2, $"device B unaffected by A's revoke (status {(int)r.StatusCode}, {got} chunks)");
    }

    // ---- Test D: stale snapshot -> 503 fail-closed ----
    membership.ForceStale(FRESH_MS + 500);
    using (var cB2 = SpikeTls.MakeMtlsClient(PORT, caPublic, clientB))
    using (var r = await cB2.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        Assert((int)r.StatusCode == 503, "stale membership snapshot -> 503 fail-closed (" + (int)r.StatusCode + ")");
}
finally
{
    await app.StopAsync();
    SpikeTls.DeleteKey(serverCert);
    SpikeTls.DeleteKey(clientA);
    SpikeTls.DeleteKey(clientB);
}

Console.WriteLine();
Console.WriteLine($"S1-Spike7 result: PASS={pass} FAIL={fail}");
Environment.Exit(fail > 0 ? 1 : 0);
