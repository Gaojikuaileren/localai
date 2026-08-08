// P4-S11 -- 真的把消息发给模型(客户端侧最后一环)。
//
// ★★ 实测过的链路现状(2026-08-05):模型文件 ✅ · llama-server.exe ✅ ·
//   start-stack 会起 18081 ✅ · 网关 /v1/chat/completions ✅(流式路径已堵掉
//   「200 + 空 body」那个静默降级)· lan-edge 8443 ✅ 在跑 · mTLS 两台真机测过 ✅。
//   **断的只有这一环** —— ChatCenter.Send 根本不调那条路由,只记消息 + 一条诚实说明。
//
// ★★★ 本文件的三条硬规矩,都是"失败必须长得和成功不一样"的具体形态:
//
//   ① **绝不伪造回复。** 任何失败路径都不写 Assistant 消息 ——
//      写了就分不清"模型说的"和"客户端编的"。失败一律走 System 说明。
//
//   ② **失败原因逐种给不同的下一步。** 后端没起 / 别名装不下 / 权限不够 / 上游报错,
//      这四种的处置完全不同。网关已经在 503/502 里带了归因(哪个别名、什么类型),
//      **把它读出来**,而不是一律显示「连接失败」。
//
//   ③ **流式中途断了要留痕。** 已经吐出来的半截回答**保留**(那是模型真说的),
//      但必须在后面补一条系统说明写清"这条没说完"。
//      直接丢掉 = 用户以为没发生过;不说明 = 用户以为它就说这么多。

using System.Net;
using System.Text;
using System.Text.Json;
using LocalAI.ClientTransport;

namespace LocalAI.Client.Services;

/// <summary>一次对话请求的结局。★ 每种失败都保留自己的 code —— 下一步不同。</summary>
public sealed record ChatOutcome(bool Ok, string Code, string Message, string Partial = "")
{
    /// <summary>成功时的完整回答(失败时是已收到的半截)。★ 同一个字段,语义由 Ok 决定。</summary>
    public string Text() => Partial;

    /// <summary>给用户看的一句话 + 该做什么。★ 不合并成「失败了」。</summary>
    public string Advice => Code switch
    {
        "" => "",
        "not_paired" => "还没有配对到中枢。到「设备」里完成配对再试。",
        // ══════════════════════════════════════════════════════════════
        //  ★★★ V20-③(D?):这一句原来说的是
        //    「目前后端要在主机上手动启动:跑 90-ops\start-stack.ps1」。
        //
        //  它有**两层**错,而且第二层比第一层坏得多:
        //   ① 过期:那是「按需装载」落地之前的话。
        //   ② ★ **给的是错原因,而且那条建议现在会把人引向一个被明令禁止的动作**——
        //      D101 裁定② 原文:自动起栈「只起 gateway 与 lan-edge,**绝不起 llama-server**:
        //      按需装载(S14/S16-b)已经落地,静态起会和 transient 平面打架」。
        //      而 `start-stack.ps1` **恰恰会**静态起 llama-server(该脚本第 117 行)。
        //      ⇒ 照着这句话做,等于亲手制造一个与 transient 平面打架的后端。
        //
        //  ★ 「给错原因比不给更坏」是这个项目自己的判词(ChatCenter 里那条 2026-08-05 审计),
        //    而这一条正在犯。⇒ 这里**只说事实、不点原因**;
        //    真原因由 `BackendUnavailableAdvice` 拿**中枢的回答**分因(见下面)。
        // ══════════════════════════════════════════════════════════════
        "backend_unavailable" =>
            "中枢在,但模型后端没有应答。★ 这一句说不出是哪一种 —— "
            + "没授权按需装载 / 正在装载中 / 显存装不下 / 后端进程自己的问题,"
            + "四种的下一步完全不同,要问过中枢才知道是哪一种。",
        "backend_error" =>
            "模型后端应答了,但返回了错误。★ 不是连不上 —— 去主机看 upstream_problem.jsonl。",
        "denied_tier" or "denied_action" or "denied_param" =>
            "这台设备没有调用模型的权限。",
        "denied_quota" => "请求太频繁了(每分钟有上限)。等一会儿再试。",
        "hub_offline" => "连不上中枢。确认主机开着、lan-edge 在跑。",
        "protocol_mismatch" => "中枢与客户端的协议版本对不上,先更新其中一边。",
        "stream_broken" =>
            "★ 回答说到一半连接断了。上面那段是模型**真的说过**的,但它没说完。",
        "e1_blocked" =>
            "★ 这条消息里像是有凭证(密码/密钥/令牌),已在送进模型**之前**拦下 —— 没有发出去。",
        _ => Message,
    };
}

public static class ChatClient
{
    // ══════════════════════════════════════════════════════════════════
    //  ★★★ V20-③:「后端没应答」的**分因**——判据是**中枢自己的回答**,不是一句写死的话
    //
    //  链路上的事实(2026-08-08 查实,不是推断):
    //   · `/v1/chat/completions` **不做按需装载**:它解析别名就直接转发给 `backend`,
    //     连不上就 `httpx.RequestError` → 503 `backend_unavailable`(gateway.py 那段 except)。
    //   · 真正会把模型起起来的是 `/v1/gpu/intent`(D87①「意图即起」),
    //     而它的失败**已经分得很细**:`NOT_PERMITTED`(机主还没勾「允许按需装载」)、
    //     `GATE`(闸拦住 = 装不下)、`LOADER_ABSENT`、`LOAD_FAILED`,
    //     以及 `ALREADY_RESIDENT` + `plane:"loading"`(正在装)。
    //   ⇒ 所以「后端没应答」的**原因几乎总在 intent 那一侧**,而客户端本来就有一个
    //     解析得懂它的 `IntentOutcome.Advice` —— 缺的只是**去问一次**。
    //
    //  ★ 为什么是纯静态函数:它要能被自检逐种形状喂进来对答案。
    //    去问中枢那一下是 I/O,留在调用方(见 ChatCenter.SendAndAskAsync)。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 拿「再问一次意图」的结果,给出 <c>backend_unavailable</c> 这一次的**真实**下一步。
    /// <param name="probe">中枢对同一个别名的最新回答;null = 那次追问自己也没成功。</param>
    /// </summary>
    public static string BackendUnavailableAdvice(IntentOutcome? probe)
    {
        // ★ 追问都失败了 —— 就说追问失败,不要退回猜一个原因(猜错比不说坏)。
        if (probe is null)
            return "模型后端没有应答,而且**向中枢追问原因这一步自己也失败了** —— "
                   + "先确认主机在线、lan-edge 在跑;主机上的诊断在 upstream_problem.jsonl。";

        // ★ 中枢明说了起不来的理由 —— 原样用它那一句(IntentOutcome.Advice 已经逐种分好)。
        if (!probe.Ok)
            return "模型没有起来 —— " + (probe.Advice is { Length: > 0 } a ? a : probe.Message);

        // ★ 正在装:这不是失败,是**还没到**。说成失败会让人去改配置,而他只需要等几秒。
        if (string.Equals(probe.Plane, "loading", StringComparison.Ordinal))
            return "模型**正在装载**(中枢说它在起了)—— 第一次装要把几 GiB 权重读进显存。"
                   + "等一会儿再发一次,这一条不用改任何设置。";

        // ★ 这一次追问把它装上了 = 上次发送时它确实还没起来。下一步就是"再发一次"。
        if (probe.Code == "OK")
            return "刚刚已经为你把模型装上了(上一次发送时它还没起来)。**再发一次**就有回答了。";

        // ★★ 中枢说它在跑,后端却仍然不应答 ⇒ **这才是**「后端进程本身的问题」那一种,
        //   也是唯一一种该去主机侧查的。★ 而且明确写清**不要**去静态起它(D101②)。
        var who = probe.Component is { Length: > 0 } c ? c : probe.Alias;
        return $"中枢说模型**在跑**({who}),可后端仍然没有应答 ⇒ 问题在后端进程本身,"
               + "既不是授权、也不是显存。去主机看 upstream_problem.jsonl 里这一条的归因。"
               + "★ **不要**用 start-stack.ps1 去静态起它 —— 这个组件已经归按需装载管,"
               + "静态再起一个会和按需平面打架(D101②)。";
    }

    /// <summary>
    /// 发一次对话,流式。`onDelta` 每收到一段就回调一次(在后台线程,调用方负责切回 UI)。
    ///
    /// ★ 别名固定用 `assistant.fast` —— 具体落到哪个组件由**中枢的别名路由**决定(S2 的桥),
    ///   客户端不点名组件。这正是 §8.1「客户端换模型一行都不用改」的落点。
    /// </summary>
    public static async Task<ChatOutcome> StreamAsync(
        HubClient hub, IReadOnlyList<ChatMessage> history, string text,
        IEnumerable<string>? committedComponents, Func<string, Task> onDelta,
        CancellationToken ct = default)
    {
        if (hub.Profile is null) return new ChatOutcome(false, "not_paired", "尚未配对");
        var ep = hub.TryDial();
        if (ep is null) return new ChatOutcome(false, "not_paired", "配对档案里没有拨号地址");

        var plan = TokenBudget.Plan(history, text, committedComponents);
        var msgs = new List<object>();
        foreach (var m in plan.Included)
            msgs.Add(new { role = m.Role == ChatRole.User ? "user" : "assistant", content = m.Text });
        msgs.Add(new { role = "user", content = text });

        var body = new { model = "assistant.fast", stream = true, messages = msgs };

        var sb = new StringBuilder();
        int errStatus = 0;
        string errBody = "";
        try
        {
            await Transport.OpenStream(hub.Profile, ep, "/v1/chat/completions",
                async line => { await OnLine(line, sb, onDelta); }, ct,
                method: HttpMethod.Post, body: body,
                onNonSuccess: (st, raw) => { errStatus = st; errBody = raw; });
        }
        catch (OperationCanceledException)
        {
            return new ChatOutcome(false, "stream_broken", "已取消", sb.ToString());
        }
        catch (Exception ex)
        {
            if (errStatus != 0)
            {
                // ★ 网关回的是带归因的 JSON —— 读出 type,别退化成「连接失败」。
                var (code, msg) = ParseError(errStatus, errBody);
                return new ChatOutcome(false, code, msg, sb.ToString());
            }
            // ★ 连都没连上 vs 连上了中途断:两者的下一步不同,必须分开。
            return new ChatOutcome(false, sb.Length > 0 ? "stream_broken" : "hub_offline",
                                   ex.Message, sb.ToString());
        }

        if (sb.Length == 0)
            // ★ 流正常结束却一个字都没有 —— 不当成成功。
            //   那正是网关注释里提到过的「200 + 空 body」的另一种形态。
            return new ChatOutcome(false, "stream_broken", "上游没有返回任何内容", "");
        return new ChatOutcome(true, "", "", sb.ToString());
    }

    // ══════════════════════════════════════════════════════════════════
    //  ★★★ CONTRACT:chat.stream.frame —— 跨进程响应契约的**客户端那半边**(D92)
    //
    //  另一半在 `10-core/gateway/test_sync.py`(搜 `CONTRACT:chat.stream.frame`),
    //  它对着**真实端点**钉每一帧的形状。
    //
    //  ★★ 为什么这条特别要紧:它是**全项目最热的一条路径**,而且契约是
    //    「**每一帧**的形状」而不是一个响应体。帧的形状漂了 = 对话整条坏掉,
    //    而表现是**一个字都不出**、不是报错 —— 与"模型没在跑"长得一模一样,
    //    人会去查后端、查显存、查网络,唯独不会想到是解析。
    //
    //  ★ 抽成静态纯函数是为了让自检能**直接喂服务端的真实帧形状**。
    //    此前这段解析长在 `OnLine` 里、和 StringBuilder 与回调缠在一起,喂不进去 ——
    //    于是最热的这条路径反而是最没法被成对钉住的那条。
    // ══════════════════════════════════════════════════════════════════
    /// <summary>
    /// 从一帧 SSE 的 <c>data:</c> 载荷里取出增量文本。
    /// <para>返回 null = 这一帧没有可显示的内容(<c>[DONE]</c> / 空 / 解析不了 / 没有 delta)。</para>
    /// </summary>
    internal static string? ParseDeltaPayload(string payload)
    {
        payload = payload.Trim();
        if (payload.Length == 0 || payload == "[DONE]") return null;
        try
        {
            using var d = JsonDocument.Parse(payload);
            if (d.RootElement.TryGetProperty("choices", out var ch)
                && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0)
            {
                var c0 = ch[0];
                if (c0.TryGetProperty("delta", out var dl)
                    && dl.TryGetProperty("content", out var cv)
                    && cv.ValueKind == JsonValueKind.String)
                    return cv.GetString();
            }
        }
        catch
        {
            // ★ 解析不出来的帧**跳过而不是当成内容** —— 把一行 JSON 原文塞进回答里,
            //   用户会以为模型在胡言乱语,而实际是我们没解析。
            return null;
        }
        return null;
    }

    static async Task OnLine(string line, StringBuilder sb, Func<string, Task> onDelta)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return;
        // ★ 走抽出来的那个解析器 —— 自检喂的也是它。
        //   两份解析会漂移,而漂的那天自检只盯着其中一份。
        var delta = ParseDeltaPayload(line[5..]);
        if (string.IsNullOrEmpty(delta)) return;
        sb.Append(delta);
        await onDelta(delta);
    }

    /// <summary>把网关的错误响应翻译成 code。★ 网关已经带了归因,别丢掉它。</summary>
    public static (string code, string message) ParseError(int status, string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error", out var e))
            {
                var type = e.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
                var msg = e.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "";
                if (type.Length > 0) return (type, msg);
            }
        }
        catch { /* 非 JSON:退回按状态码分类 */ }
        return status switch
        {
            401 or 403 => ("denied_tier", $"被拒({status})"),
            429 => ("denied_quota", "太频繁"),
            502 => ("backend_error", "后端返回的不是合法响应"),
            503 => ("backend_unavailable", "后端未响应"),
            _ => ("hub_offline", $"中枢返回 {status}"),
        };
    }
}
