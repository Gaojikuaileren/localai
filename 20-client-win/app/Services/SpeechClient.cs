// D?(P5 语音 v1)· speech 后端的**客户端消费者** —— 按住说话那条链路的下半截。
//
// ═════════════════════════════════════════════════════════════════════════════
//  ★★★ 它与 AudioCapture 的关系,就是那条架构底线本身
//
//  「同传可以失败,用户的麦克风不可以。」
//
//  · `AudioCapture` **不认识这个类**(它整个类里没有任何网络类型)——
//    采集是通路,本类是**消费者**。本类整个挂掉,录音照录。
//  · 反过来本类**不碰任何音频设备**:它只收一段已经录好的 WAV 字节。
//  ⇒ 两边谁也够不着谁。这不是 try-catch,是**代码路径上就到不了**。
//    已用断言钉死两个方向(Selftest 的「麦克风独立性」一节)。
//
// ═════════════════════════════════════════════════════════════════════════════
//  ★★ 跨进程契约(D92/D95):三条端点的顶层键集合登记在
//     `10-core/speech/contracts.json` —— **服务端、网关消费者、本类共读同一份**。
//     本文件里这几个 `Keys*` 常量是那份登记表在 C# 侧的**副本**,
//     而副本会分家 ⇒ 已用断言把它与 contracts.json **逐条对拍**(Selftest 那一节),
//     对不上当场红。⇒ 期望值实质上仍然只有一份。
//
//  ★ 认不出的形状一律返回 (null, why),**不挑着能读的字段拼一个出来** ——
//    拼出来的那一份会以一个可信的样子进到界面,而界面没有任何办法发现它是残的。
//
// ═════════════════════════════════════════════════════════════════════════════
//  ★ v1 的连接范围,如实写:**只连本机回环**(127.0.0.1:18085)。
//    · 主机上的客户端 ⇒ 回环 ⇒ speech 服务端判定 provenance = user_voice_asr;
//    · 副机(局域网)要用,得让网关把 /v1/speech/* 代理过去(lan-edge 会注入已验证指纹头)——
//      **那一段本轮没做**,所以副机上按住说话今天用不了,界面如实说明,不摆假开关。

using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace LocalAI.Client.Services;

/// <summary>ASR 的结果。<paramref name="Provenance"/> 是**服务端算出来的**,客户端只读不改。</summary>
public sealed record AsrResult(string Text, string Language, double DurationS, string Tier, string Provenance);

/// <summary>speech 后端的健康状态。<paramref name="Ready"/> = 模型装完了。</summary>
public sealed record SpeechHealth(bool Ready, string Tier, bool AsrLoaded, bool TtsLoaded, string Detail);

public sealed class SpeechClient
{
    /// <summary>本机 speech 后端的端口。★ 与 `10-core/speech/launch.toml` 的 `[service].port` 一致。</summary>
    public const int Port = 18085;

    /// <summary>
    /// 只有这个来源档位的转写才允许**直通记忆写入**。
    /// ★★ 它是**服务端**按连接算出来的(回环 / lan-edge 注入的已验证指纹头),
    ///   客户端**不许**自己填、也不许放宽 —— 那等于把记忆库的准入交给调用方。
    /// </summary>
    public const string ProvenanceTrusted = "user_voice_asr";

    // ── 契约:顶层键集合(与 10-core/speech/contracts.json 逐条对拍,见 Selftest)──
    public static readonly string[] KeysHealth = { "ok", "ready", "kind", "tier", "asr_loaded", "tts_loaded", "detail" };
    public static readonly string[] KeysAsr = { "text", "language", "duration_s", "tier", "provenance" };
    public static readonly string[] KeysTts = { "audio_b64", "sample_rate", "format", "voice", "frames" };

    /// <summary>契约号 —— 与服务端/网关消费者共用同一个锚点(ASCII)。</summary>
    public const string ContractHealth = "CONTRACT:speech.health";
    public const string ContractAsr = "CONTRACT:speech.asr";
    public const string ContractTts = "CONTRACT:speech.tts";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    static string Base => $"http://127.0.0.1:{Port}";

    /// <summary>上一次失败的原因(界面照实显示)。</summary>
    public string? LastError { get; private set; }

    // ── 形状核对 ──────────────────────────────────────────────────────────────
    /// <summary>顶层键集合是否**正好**等于登记的那一组。★ 集合相等,不是"包含"。</summary>
    internal static bool KeysMatch(JsonElement o, string[] want)
    {
        if (o.ValueKind != JsonValueKind.Object) return false;
        var got = o.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        return got.SetEquals(want);
    }

    internal static string Describe(JsonElement o, string[] want)
    {
        var got = o.ValueKind == JsonValueKind.Object
            ? o.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal)
            : Enumerable.Empty<string>();
        return $"实际 [{string.Join(", ", got)}] / 登记 [{string.Join(", ", want.OrderBy(x => x, StringComparer.Ordinal))}]";
    }

    /// <summary>解析 /health。★ 纯函数 —— 断言拿真 JSON 喂它,不测一个仿造的解析器。</summary>
    internal static (SpeechHealth? h, string? why) ParseHealth(JsonElement o)
        => !KeysMatch(o, KeysHealth)
            ? (null, ContractHealth + " 形状对不上(" + Describe(o, KeysHealth) + ")")
            : (new SpeechHealth(o.GetProperty("ready").GetBoolean(), o.GetProperty("tier").GetString() ?? "",
                                o.GetProperty("asr_loaded").GetBoolean(), o.GetProperty("tts_loaded").GetBoolean(),
                                o.GetProperty("detail").GetString() ?? ""), null);

    /// <summary>解析 ASR 应答。★ <c>provenance</c> 原样带出 —— 它决定这段话能不能进记忆库。</summary>
    internal static (AsrResult? r, string? why) ParseAsr(JsonElement o)
        => !KeysMatch(o, KeysAsr)
            ? (null, ContractAsr + " 形状对不上(" + Describe(o, KeysAsr) + ")")
            : (new AsrResult(o.GetProperty("text").GetString() ?? "", o.GetProperty("language").GetString() ?? "",
                             o.GetProperty("duration_s").GetDouble(), o.GetProperty("tier").GetString() ?? "",
                             o.GetProperty("provenance").GetString() ?? ""), null);

    /// <summary>
    /// 这段转写**能不能**直通记忆写入。
    /// ★★★ 判据只有一条:服务端给的 provenance 是不是可信档位。
    ///   **不补救、不放宽** —— 拿不到可信档位就是不能写,而不是"退一步记成低可信度":
    ///   记忆库里一条来源可疑的记录会被当成事实用下去。
    /// </summary>
    public static bool MayWriteMemory(AsrResult? r) => r is not null && r.Provenance == ProvenanceTrusted;

    // ── 调用 ──────────────────────────────────────────────────────────────────
    /// <summary>探一次 /health。★ 未就绪时服务端回 503 —— 那**不是**错误,是"还在装"。</summary>
    public async Task<SpeechHealth?> HealthAsync(CancellationToken ct = default)
    {
        LastError = null;
        try
        {
            using var r = await Http.GetAsync(Base + "/health", ct);
            var body = await r.Content.ReadAsStringAsync(ct);
            var (h, why) = ParseHealth(JsonDocument.Parse(body).RootElement);
            if (why is not null) { LastError = why; return null; }
            return h;
        }
        catch (Exception ex)
        {
            // ★ 说得出**是哪一种**够不着:没起来 与 起来了但答不对,处置不同。
            LastError = "连不上本机语音服务(127.0.0.1:" + Port + "):" + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 把一段 WAV 交给 ASR。★ 失败返回 null 并把原因留在 <see cref="LastError"/> ——
    /// **绝不返回一个空字符串冒充"没听清"**:那两件事的下一步完全不同。
    /// </summary>
    public async Task<AsrResult?> TranscribeAsync(byte[] wav, CancellationToken ct = default)
    {
        LastError = null;
        try
        {
            using var content = new ByteArrayContent(wav);
            using var r = await Http.PostAsync(Base + "/v1/speech/asr", content, ct);
            var body = await r.Content.ReadAsStringAsync(ct);
            if ((int)r.StatusCode == 503)
            {
                LastError = "语音服务还在装模型(/health 还没回 2xx)—— 稍等一下再试。";
                return null;
            }
            if (!r.IsSuccessStatusCode) { LastError = $"语音服务返回 {(int)r.StatusCode}:{body}"; return null; }
            var (res, why) = ParseAsr(JsonDocument.Parse(body).RootElement);
            if (why is not null) { LastError = why; return null; }
            return res;
        }
        catch (Exception ex)
        {
            LastError = "连不上本机语音服务(127.0.0.1:" + Port + "):" + ex.Message;
            return null;
        }
    }
}
