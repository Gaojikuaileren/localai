// P3b S1 / Spike 5 -- HTTP streaming (SSE) over the mTLS connection, loopback only.
// LocalAI, decision D43 (S1). Proves OpenAI-style token streaming survives the mTLS edge and
// arrives incrementally (not buffered), which voice and long tasks depend on -- and which the
// revocation-abort spike (7) will later kill mid-stream.

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Security.Cryptography.X509Certificates;
using LocalAI.Spikes;
using Microsoft.AspNetCore.Server.Kestrel.Https;

const int PORT = 18444;
const string SERVER_SAN = "localai-test.local";
const int CHUNKS = 6;
const int GAP_MS = 120;

int pass = 0, fail = 0;
void Assert(bool cond, string msg)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + msg); }
    else { fail++; Console.WriteLine("  FAIL  " + msg); }
}

using var ca = SpikeTls.CreateCa("LocalAI Test CA");
var caPublic = SpikeTls.PublicOf(ca);
var serverCert = SpikeTls.CreateLeaf(ca, SERVER_SAN, SERVER_SAN, serverAuth: true, clientAuth: false);
var clientCert = SpikeTls.CreateLeaf(ca, "device-A", null, serverAuth: false, clientAuth: true);

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
    if (ctx.Connection.ClientCertificate is null) { ctx.Response.StatusCode = 401; return; }
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    for (int i = 0; i < CHUNKS; i++)
    {
        await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"data: chunk-{i}\n\n"));
        await ctx.Response.Body.FlushAsync();
        await Task.Delay(GAP_MS);
    }
    await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
    await ctx.Response.Body.FlushAsync();
});

await app.StartAsync();
Console.WriteLine("kestrel up on 127.0.0.1:" + PORT + " (SAN=" + SERVER_SAN + ")");

try
{
    using var client = SpikeTls.MakeMtlsClient(PORT, caPublic, clientCert);
    var sw = Stopwatch.StartNew();
    // ResponseHeadersRead is what makes the body stream instead of buffering.
    using var resp = await client.GetAsync($"https://{SERVER_SAN}:{PORT}/api/stream",
                                           HttpCompletionOption.ResponseHeadersRead);
    Assert((int)resp.StatusCode == 200, "stream over mTLS -> 200 (" + (int)resp.StatusCode + ")");

    await using var stream = await resp.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(stream);
    var chunks = new List<string>();
    long firstMs = -1, lastChunkMs = 0;
    bool done = false;
    string? line;
    while ((line = await reader.ReadLineAsync()) is not null)
    {
        if (line.Length == 0) continue;
        var now = sw.ElapsedMilliseconds;
        if (firstMs < 0) firstMs = now;
        var payload = line.StartsWith("data: ") ? line.Substring(6) : line;
        if (payload == "[DONE]") { done = true; continue; }
        chunks.Add(payload);
        lastChunkMs = now;
    }
    var totalMs = sw.ElapsedMilliseconds;

    Assert(chunks.Count == CHUNKS, $"received all {CHUNKS} chunks ({chunks.Count})");
    bool ordered = true;
    for (int i = 0; i < chunks.Count; i++) if (chunks[i] != $"chunk-{i}") ordered = false;
    Assert(ordered, "chunks arrived in order");
    Assert(done, "terminal [DONE] received");
    // incremental: first chunk arrives well before the last, and the server-side gaps are preserved
    Assert(firstMs >= 0 && firstMs < GAP_MS * 3, $"first chunk arrives early ({firstMs} ms < {GAP_MS * 3})");
    Assert(lastChunkMs - firstMs >= GAP_MS * (CHUNKS - 2),
           $"stream spread over time, not buffered (span {lastChunkMs - firstMs} ms >= {GAP_MS * (CHUNKS - 2)})");
    Console.WriteLine($"  info  first={firstMs}ms last={lastChunkMs}ms total={totalMs}ms");
}
finally
{
    await app.StopAsync();
    SpikeTls.DeleteKey(serverCert);
    SpikeTls.DeleteKey(clientCert);
}

Console.WriteLine();
Console.WriteLine($"S1-Spike5 result: PASS={pass} FAIL={fail}");
Environment.Exit(fail > 0 ? 1 : 0);
