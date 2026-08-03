// P3c -- 聊天工作空间【第二功能:回信】(用户裁定 2026-08-03,D61)。
//   上方三块:来信(可空)/ 我想回复的内容 / 生成结果;下方设置条(载体/语气/语言/称谓/地址/联系/问候/祝福)。
//   设置【跟随会话】:回到该会话这些设置还在;新进来 = 全默认。
//
// ★ 诚实边界:AI 引擎(P4)未接。「生成」现在做的是【格式装配】—— 把你写的内容
//   按载体排进正确的信件格式(称呼/正文/结尾/署名/日期/地址),复制即用,这是真的;
//   润色、扩写、按语气改写要等引擎 —— 界面如实说明,不伪造 AI 文笔。
// ★ 回信会话与同传/文件翻译同款:不能搬走,点开自动切到回信场景。

namespace LocalAI.Client.Services;

/// <summary>载体:邮件 / 纸质信件 / 短消息(滑条三档)。</summary>
public enum ReplyMedium { Email, Paper, Message }
/// <summary>语气:朋友 / 普通 / 正式 / 行政(滑条四档)。</summary>
public enum ReplyTone { Friend, Normal, Formal, Official }

/// <summary>一封回信的全部状态(跟随会话落盘)。</summary>
public sealed class ReplyDoc
{
    public ReplyMedium Medium { get; set; } = ReplyMedium.Email;
    public ReplyTone Tone { get; set; } = ReplyTone.Normal;
    public string Language { get; set; } = "zh";
    public string TheirName { get; set; } = "";
    public string TheirAddress { get; set; } = "";
    public string TheirContact { get; set; } = "";
    public string SignDate { get; set; } = "";       // 只用于纸质信件;空 = 生成当天
    public int GreetingIndex { get; set; }
    public int ClosingIndex { get; set; }
    public string Incoming { get; set; } = "";       // 来信(可空)
    public string Draft { get; set; } = "";          // 我想回复的内容
    public string Result { get; set; } = "";         // 生成结果(装配产物)
}

/// <summary>我方信息 —— 【常驻模板】(用户裁定 2026-08-03):很少改,跨会话共享,不随会话走。</summary>
public sealed class ReplyProfile
{
    public string MyName { get; set; } = "";
    public string MyAddress { get; set; } = "";
    public string MyContact { get; set; } = "";
}

public sealed class ReplySave
{
    public ReplyProfile Profile { get; set; } = new();
    public Dictionary<string, ReplyDoc> Docs { get; set; } = new();
}

public sealed class ReplyState
{
    public event Action? Changed;
    public ReplyProfile Profile { get; private set; } = new();
    public event Action<string>? FocusSession;

    /// <summary>聊天工作空间当前是不是在【回信】场景(切换器/点开回信会话驱动)。</summary>
    public bool SceneReply { get; private set; }
    public void SetScene(bool reply) { if (SceneReply != reply) { SceneReply = reply; Changed?.Invoke(); } }

    readonly Dictionary<string, ReplyDoc> _docs = new(StringComparer.Ordinal);
    ReplyDoc _scratch = new();

    public string? SessionId { get; private set; }
    public void SetSession(string? sid) { if (SessionId != sid) { SessionId = sid; Changed?.Invoke(); } }

    public ReplyDoc Doc => SessionId is { } sid
        ? (_docs.TryGetValue(sid, out var d) ? d : _docs[sid] = new ReplyDoc())
        : _scratch;

    /// <summary>第一笔真实编辑时建会话(设置要跟随会话,没有会话就没有"跟随")。</summary>
    public void EnsureSession()
    {
        if (SessionId is not null) return;
        var app = (LocalAI.Client.App)System.Windows.Application.Current;
        var sess = app.Chat.NewSession(null, "chat", ProjectScope.Personal,
            $"回信 · {DateTime.Now:M月d日 HH:mm}", replyLetter: true);
        _docs[sess.SessionId] = _scratch;
        _scratch = new ReplyDoc();
        SessionId = sess.SessionId;
        FocusSession?.Invoke(sess.SessionId);
    }

    public void Touch() => Changed?.Invoke();

    // ---------------------------------------------------------------- 问候/祝福预设(按语气取)
    public static string[] GreetingsFor(ReplyTone t) => t switch
    {
        ReplyTone.Friend => new[] { "(不加问候)", "嗨,", "好久不见!", "见字如面。" },
        ReplyTone.Normal => new[] { "(不加问候)", "你好!", "展信佳。", "近来可好?" },
        ReplyTone.Formal => new[] { "(不加问候)", "您好!", "展信安好。", "谨启者:" },
        _ => new[] { "(不加问候)", "您好!", "敬启者:", "兹复函如下:" },
    };

    public static string[] ClosingsFor(ReplyTone t) => t switch
    {
        ReplyTone.Friend => new[] { "(不加祝福)", "祝好!", "盼复!", "保重!" },
        ReplyTone.Normal => new[] { "(不加祝福)", "祝一切顺利!", "顺祝安康!", "盼早日回复。" },
        ReplyTone.Formal => new[] { "(不加祝福)", "此致敬礼!", "顺颂时祺!", "敬盼回复。" },
        _ => new[] { "(不加祝福)", "此致敬礼!", "特此函复。", "顺颂公祺!" },
    };

    // ---------------------------------------------------------------- 格式装配(现在就真干活的部分)
    /// <summary>
    /// 把设置 + 想说的内容装配成【复制即用】的成品格式。
    /// ★ 这不是 AI:正文就是用户写的原文,只负责把称呼/结尾/署名/日期/地址排对位置。
    /// </summary>
    public static string Compose(ReplyDoc d, ReplyProfile me)
    {
        var greet0 = GreetingsFor(d.Tone).ElementAtOrDefault(d.GreetingIndex) ?? "";
        var close0 = ClosingsFor(d.Tone).ElementAtOrDefault(d.ClosingIndex) ?? "";
        var greet = greet0.StartsWith("(") ? "" : greet0;
        var close = close0.StartsWith("(") ? "" : close0;
        var call = d.TheirName.Trim().Length > 0 ? d.TheirName.Trim() + ":" : "";
        var body = d.Draft.Trim();
        var sb = new System.Text.StringBuilder();

        switch (d.Medium)
        {
            case ReplyMedium.Message:
                // 短消息:紧凑 —— 问候并进首行,不排地址不排日期
                if (greet.Length > 0) sb.Append(greet).Append(' ');
                sb.AppendLine(body);
                if (close.Length > 0) sb.AppendLine(close);
                if (me.MyName.Trim().Length > 0) sb.Append("—— ").Append(me.MyName.Trim());
                break;

            case ReplyMedium.Email:
                if (call.Length > 0) sb.AppendLine(call);
                if (greet.Length > 0) sb.AppendLine(greet);
                sb.AppendLine();
                sb.AppendLine(body);
                sb.AppendLine();
                if (close.Length > 0) sb.AppendLine(close);
                sb.AppendLine();
                if (me.MyName.Trim().Length > 0) sb.AppendLine(me.MyName.Trim());
                if (me.MyContact.Trim().Length > 0) sb.AppendLine(me.MyContact.Trim());
                break;

            default:   // Paper:完整信件格式 —— 地址块 / 称呼 / 正文(段首缩进)/ 祝福 / 右对齐署名+日期
                if (d.TheirAddress.Trim().Length > 0) sb.AppendLine(d.TheirAddress.Trim());
                if (d.TheirAddress.Trim().Length > 0) sb.AppendLine();
                if (call.Length > 0) sb.AppendLine(call);
                if (greet.Length > 0) sb.AppendLine("    " + greet);
                sb.AppendLine();
                foreach (var para in body.Split('\n'))
                    sb.AppendLine(para.Trim().Length > 0 ? "    " + para.Trim() : "");
                sb.AppendLine();
                if (close.Length > 0) sb.AppendLine(close);
                sb.AppendLine();
                var sign = me.MyName.Trim().Length > 0 ? me.MyName.Trim() : "";
                var date = d.SignDate.Trim().Length > 0 ? d.SignDate.Trim() : DateTime.Now.ToString("yyyy年M月d日");
                sb.AppendLine((sign + "  " + date).PadLeft(36));   // 右侧署名 + 日期(纸质专属)
                if (me.MyAddress.Trim().Length > 0 || me.MyContact.Trim().Length > 0)
                {
                    sb.AppendLine();
                    if (me.MyAddress.Trim().Length > 0) sb.AppendLine(me.MyAddress.Trim());
                    if (me.MyContact.Trim().Length > 0) sb.AppendLine(me.MyContact.Trim());
                }
                break;
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    // ---------------------------------------------------------------- 存档
    public ReplySave Export() => new() { Profile = Profile, Docs = new(_docs) };
    public void Import(ReplySave? d)
    {
        if (d is null) return;
        Profile = d.Profile ?? new();
        _docs.Clear();
        foreach (var kv in d.Docs ?? new()) if (kv.Value is not null) _docs[kv.Key] = kv.Value;
        Changed?.Invoke();
    }
}
