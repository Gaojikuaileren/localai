// P3b S1 / Spike 9 -- anonymous /pair entry resource bounds, loopback only.
// LocalAI, decision D43 (S1). The anonymous pairing entry must reject oversize/abusive requests
// at the Kestrel layer before any app work. Packet §5.2 caps: request line 4 KiB, headers 16 KiB,
// pair body 32 KiB. Proves those limits are enforced (small ok; oversize body/header rejected).

using System.Net;
using System.Security.Cryptography.X509Certificates;
using LocalAI.Spikes;
using Microsoft.AspNetCore.Server.Kestrel.Https;

const int PORT = 18447;
const string SERVER_SAN = "localai-test.local";
const int BODY_CAP = 32 * 1024;      // 32 KiB
const int HEADERS_CAP = 16 * 1024;   // 16 KiB
const int LINE_CAP = 4 * 1024;       // 4 KiB

int pass = 0, fail = 0;
void Assert(bool cond, string msg)
{
    if (cond) { pass++; Console.WriteLine("  PASS  " + msg); }
    else { fail++; Console.WriteLine("  FAIL  " + msg); }
}

using var ca = SpikeTls.CreateCa("LocalAI Test CA");
var caPublic = SpikeTls.PublicOf(ca);
var serverCert = SpikeTls.CreateLeaf(ca, SERVER_SAN, SERVER_SAN, serverAuth: true, clientAuth: false);

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = BODY_CAP;
    k.Limits.MaxRequestHeadersTotalSize = HEADERS_CAP;
    k.Limits.MaxRequestLineSize = LINE_CAP;
    k.Listen(IPAddress.Loopback, PORT, lo => lo.UseHttps(h =>
    {
        h.ServerCertificate = serverCert;
        // /pair is anonymous: server TLS only, no client cert required
        h.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        h.ClientCertificateValidation = SpikeTls.ClientValidator(caPublic);
    }));
});

var app = builder.Build();
app.MapPost("/pair/enroll", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    return Results.Text("ok:" + body.Length);
});

await app.StartAsync();
Console.WriteLine("kestrel up on 127.0.0.1:" + PORT + " (SAN=" + SERVER_SAN + ")");

string url = $"https://{SERVER_SAN}:{PORT}/pair/enroll";

// returns "HTTP <code>" on a response, or "throw:<Type>" if the transport rejected it
async Task<string> Post(int bodyBytes, (string name, int valueBytes)? extraHeader)
{
    using var client = SpikeTls.MakeMtlsClient(PORT, caPublic, null);
    var req = new HttpRequestMessage(HttpMethod.Post, url)
    {
        Content = new ByteArrayContent(new byte[bodyBytes])
    };
    if (extraHeader is { } h)
        req.Headers.TryAddWithoutValidation(h.name, new string('x', h.valueBytes));
    try
    {
        using var r = await client.SendAsync(req);
        return "HTTP " + (int)r.StatusCode;
    }
    catch (Exception ex) { return "throw:" + ex.GetType().Name; }
}

static bool Rejected(string r, int code) => r == "HTTP " + code || r.StartsWith("throw:");

try
{
    // 1. small body within cap -> 200
    var r1 = await Post(1024, null);
    Assert(r1 == "HTTP 200", "body within cap -> 200 (" + r1 + ")");

    // 2. body over cap -> 413 (or transport reset)
    var r2 = await Post(BODY_CAP + 16 * 1024, null);
    Assert(Rejected(r2, 413), "oversize body -> rejected (" + r2 + ")");

    // 3. single header over the total-headers cap -> 431 (or transport reset)
    var r3 = await Post(256, ("X-Big", HEADERS_CAP + 8 * 1024));
    Assert(Rejected(r3, 431), "oversize header -> rejected (" + r3 + ")");

    Console.WriteLine($"  info  caps: body={BODY_CAP} headers={HEADERS_CAP} line={LINE_CAP} (bytes)");
}
finally
{
    await app.StopAsync();
    SpikeTls.DeleteKey(serverCert);
}

Console.WriteLine();
Console.WriteLine($"S1-Spike9 result: PASS={pass} FAIL={fail}");
Environment.Exit(fail > 0 ? 1 : 0);
