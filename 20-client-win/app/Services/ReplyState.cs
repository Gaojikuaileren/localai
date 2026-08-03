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
/// <summary>语气三档(用户裁定 2026-08-03):熟人 / 礼貌 / 行政 —— 四档里"普通/正式"区分不明显,砍掉一档。</summary>
public enum ReplyTone { Casual, Polite, Official }

/// <summary>一封回信的全部状态(跟随会话落盘)。</summary>
public sealed class ReplyDoc
{
    public ReplyMedium Medium { get; set; } = ReplyMedium.Email;
    public ReplyTone Tone { get; set; } = ReplyTone.Polite;
    public string Language { get; set; } = "zh";
    public string TheirName { get; set; } = "";
    public string TheirAddress { get; set; } = "";
    /// <summary>对方邮编 + 地区(与地址是【融合的两栏】:上行街道,下行右侧邮编地区)。</summary>
    public string TheirPostal { get; set; } = "";
    public string TheirContact { get; set; } = "";
    /// <summary>署名日期:只用于纸质信件,空 = 生成当天。
    /// ★ UI 摆在【我方信息】卡里(署名是我方的事,用户裁定 2026-08-03),
    ///   但数据仍随【这封信】走 —— 日期是每封信各自的,不能做成跨信共享的模板值。</summary>
    public string SignDate { get; set; } = "";
    public int GreetingIndex { get; set; }
    public int ClosingIndex { get; set; }
    public string Incoming { get; set; } = "";       // 来信(可空)
    public string Draft { get; set; } = "";          // 我想回复的内容
    public string Result { get; set; } = "";         // 生成结果(装配产物)
    /// <summary>这封信的历次生成记录(最新在后)。会话进不进列表就看它有没有记录。</summary>
    public List<ReplyRecord> Records { get; set; } = new();
    /// <summary>当前选中回看的记录;null = 正在写新的一条。</summary>
    public string? SelectedRecordId { get; set; }
}

/// <summary>
/// 一条【对话记录】(用户裁定 2026-08-03):按下「生成」才产生 —— 把当时的输入与设置整份存下来,
/// 连同产出。选中它可回看/继续编辑;编辑后再生成【追加新记录】,不覆盖旧的。
/// </summary>
public sealed class ReplyRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime At { get; set; } = DateTime.Now;
    /// <summary>还在生成中(AI 接入后是真的等待;此时输入框禁用,见 ReplyPanel)。</summary>
    public bool Generating { get; set; }
    public string Draft { get; set; } = "";
    public string Incoming { get; set; } = "";
    public string Result { get; set; } = "";
    public ReplyMedium Medium { get; set; }
    public ReplyTone Tone { get; set; }
    public string Language { get; set; } = "zh";
    public string TheirName { get; set; } = "";
    public string TheirAddress { get; set; } = "";
    public string TheirPostal { get; set; } = "";
    public string TheirContact { get; set; } = "";
    public string SignDate { get; set; } = "";
}

/// <summary>我方信息 —— 【常驻模板】(用户裁定 2026-08-03):很少改,跨会话共享,不随会话走。</summary>
public sealed class ReplyProfile
{
    public string MyName { get; set; } = "";
    public string MyAddress { get; set; } = "";
    /// <summary>我方邮编 + 地区(同上,融合两栏的下半)。</summary>
    public string MyPostal { get; set; } = "";
    public string MyContact { get; set; } = "";
    /// <summary>自定义问候/祝福(用户裁定 2026-08-03:可自行添加)—— 跨会话共用,跟着模板走。</summary>
    public List<string> CustomGreetings { get; set; } = new();
    public List<string> CustomClosings { get; set; } = new();
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

    /// <summary>
    /// 建会话 —— ★【只在按下「生成」时调用】(用户裁定 2026-08-03):
    ///   有了第一条对话记录,这条会话才进右侧会话列表;在那之前随便写随便调,不留一条空会话。
    /// </summary>
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

    // ---------------------------------------------------------------- 问候/祝福预设
    // ★ 跟随【语言 + 语气】(用户裁定 2026-08-03):写德语信却给中文"此致敬礼"是错的。
    //   目录里没有的语言退回英文一套 —— 如实用通行说法,不硬造。
    //   用户可自定义追加(存 Profile.CustomGreetings/CustomClosings,跨会话共用)。
    public const string NoGreeting = "(不加问候)";
    public const string NoClosing = "(不加祝福)";

    static string[] BuiltinGreetings(string lang, ReplyTone t) => lang switch
    {
        "zh" => t switch
        {
            ReplyTone.Casual => new[] { "嗨,", "好久不见!", "见字如面。" },
            ReplyTone.Polite => new[] { "你好!", "您好!", "展信佳。" },
            _ => new[] { "您好!", "敬启者:", "兹复函如下:" },
        },
        "ja" => t switch
        {
            ReplyTone.Casual => new[] { "やあ、", "お久しぶりです!", "こんにちは。" },
            ReplyTone.Polite => new[] { "こんにちは。", "お世話になっております。", "お元気ですか。" },
            _ => new[] { "拝啓", "謹啓", "平素より大変お世話になっております。" },
        },
        "de" => t switch
        {
            ReplyTone.Casual => new[] { "Hi,", "Hallo!", "Lange nicht gehört!" },
            ReplyTone.Polite => new[] { "Hallo,", "Guten Tag,", "Liebe Grüße vorab," },
            _ => new[] { "Sehr geehrte Damen und Herren,", "Sehr geehrte/r Frau/Herr,", "Bezugnehmend auf Ihr Schreiben," },
        },
        "fr" => t switch
        {
            ReplyTone.Casual => new[] { "Salut,", "Coucou !", "Ça fait longtemps !" },
            ReplyTone.Polite => new[] { "Bonjour,", "Cher/Chère,", "J'espère que vous allez bien." },
            _ => new[] { "Madame, Monsieur,", "Suite à votre courrier,", "Veuillez trouver ci-après notre réponse." },
        },
        _ => t switch      // en 及未收录语言:用通行英文说法
        {
            ReplyTone.Casual => new[] { "Hi,", "Hey!", "Long time no see!" },
            ReplyTone.Polite => new[] { "Hello,", "Dear ,", "I hope this finds you well." },
            _ => new[] { "Dear Sir or Madam,", "To whom it may concern,", "In reply to your letter," },
        },
    };

    static string[] BuiltinClosings(string lang, ReplyTone t) => lang switch
    {
        "zh" => t switch
        {
            ReplyTone.Casual => new[] { "祝好!", "盼复!", "保重!" },
            ReplyTone.Polite => new[] { "祝一切顺利!", "顺祝安康!", "盼早日回复。" },
            _ => new[] { "此致敬礼!", "特此函复。", "顺颂公祺!" },
        },
        "ja" => t switch
        {
            ReplyTone.Casual => new[] { "それでは!", "またね。", "お元気で。" },
            ReplyTone.Polite => new[] { "よろしくお願いいたします。", "ご返信お待ちしております。", "ご自愛ください。" },
            _ => new[] { "敬具", "謹白", "何卒よろしくお願い申し上げます。" },
        },
        "de" => t switch
        {
            ReplyTone.Casual => new[] { "Liebe Grüße", "Bis bald!", "Mach's gut!" },
            ReplyTone.Polite => new[] { "Viele Grüße", "Beste Grüße", "Ich freue mich auf Ihre Antwort." },
            _ => new[] { "Mit freundlichen Grüßen", "Hochachtungsvoll", "Für Rückfragen stehe ich zur Verfügung." },
        },
        "fr" => t switch
        {
            ReplyTone.Casual => new[] { "À bientôt !", "Bises,", "Prends soin de toi !" },
            ReplyTone.Polite => new[] { "Cordialement,", "Bien à vous,", "Dans l'attente de votre réponse," },
            _ => new[] { "Veuillez agréer mes salutations distinguées.", "Respectueusement,", "Je vous prie d'agréer l'expression de ma considération." },
        },
        _ => t switch
        {
            ReplyTone.Casual => new[] { "Cheers!", "Take care!", "Talk soon!" },
            ReplyTone.Polite => new[] { "Best regards,", "Kind regards,", "Looking forward to your reply." },
            _ => new[] { "Yours faithfully,", "Yours sincerely,", "Respectfully," },
        },
    };

    /// <summary>
    /// 署名日期的【唯一书写格式】—— 与 Compose 里"空就用当天"那一支的格式一模一样,
    /// 于是"选了今天"与"留空"排出来的字面完全相同,不会因为两处各写各的而对不上。
    /// </summary>
    public const string SignDateFormat = "yyyy年M月d日";

    /// <summary>把存着的署名日期解析回来。解析不出(老存档里可能是随手敲的字)就当没有。</summary>
    public static DateTime? ParseSignDate(string? s)
        => DateTime.TryParseExact((s ?? "").Trim(), SignDateFormat,
                                  System.Globalization.CultureInfo.InvariantCulture,
                                  System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    /// <summary>问候清单 = (不加) + 内置(按语言×语气) + 用户自定义。</summary>
    public string[] Greetings(string lang, ReplyTone t)
        => new[] { NoGreeting }.Concat(BuiltinGreetings(lang, t)).Concat(Profile.CustomGreetings).ToArray();

    public string[] Closings(string lang, ReplyTone t)
        => new[] { NoClosing }.Concat(BuiltinClosings(lang, t)).Concat(Profile.CustomClosings).ToArray();

    /// <summary>
    /// 自定义追加(跨会话共用)。重复/空不加;返回它在当前清单里的下标(-1 = 没加成)。
    /// ★ 下标从【自定义那一段】里算,不用 Array.IndexOf 全表找(复核 2026-08-03):
    ///   与内置重名时全表找命中的是内置那一条,于是"刚加完选中的是内置、新加的那条从没被选中过"。
    /// </summary>
    public int AddCustom(bool greeting, string text, string lang, ReplyTone t)
    {
        text = text.Trim();
        if (text.Length == 0) return -1;
        var list = greeting ? Profile.CustomGreetings : Profile.CustomClosings;
        var j = list.IndexOf(text);
        if (j < 0) { list.Add(text); j = list.Count - 1; }
        Changed?.Invoke();
        return CustomBase + j;
    }

    /// <summary>这个【下标】指的是不是用户自己加的那一段(内置的不给删)。</summary>
    public static bool IsCustomIndex(int index) => index >= CustomBase;

    // ---------------------------------------------------------------- 格式装配(现在就真干活的部分)
    /// <summary>
    /// 把设置 + 想说的内容装配成【复制即用】的成品格式。
    /// ★ 这不是 AI:正文就是用户写的原文,只负责把称呼/结尾/署名/日期/地址排对位置。
    /// </summary>
    /// <summary>空值一律换成 [方括号] 占位 —— 产出是可直接找替换的模板,而不是悄悄少一行。</summary>
    static string Or(string? v, string slot) => string.IsNullOrWhiteSpace(v) ? slot : v!.Trim();

    public string Compose(ReplyDoc d, ReplyProfile me)
    {
        var greet0 = Greetings(d.Language, d.Tone).ElementAtOrDefault(d.GreetingIndex) ?? "";
        var close0 = Closings(d.Language, d.Tone).ElementAtOrDefault(d.ClosingIndex) ?? "";
        var greet = greet0 == NoGreeting ? "" : greet0;
        var close = close0 == NoClosing ? "" : close0;
        var call = Or(d.TheirName, "[对方称呼]") + ":";
        var body = d.Draft.Trim();
        var sb = new System.Text.StringBuilder();

        switch (d.Medium)
        {
            case ReplyMedium.Message:
                // 短消息:紧凑 —— 问候并进首行,不排地址不排日期
                if (greet.Length > 0) sb.Append(greet).Append(' ');
                sb.AppendLine(body);
                if (close.Length > 0) sb.AppendLine(close);
                sb.Append("—— ").Append(Or(me.MyName, "[我的署名]"));
                break;

            case ReplyMedium.Email:
                if (call.Length > 0) sb.AppendLine(call);
                if (greet.Length > 0) sb.AppendLine(greet);
                sb.AppendLine();
                sb.AppendLine(body);
                sb.AppendLine();
                if (close.Length > 0) sb.AppendLine(close);
                sb.AppendLine();
                sb.AppendLine(Or(me.MyName, "[我的署名]"));
                sb.AppendLine(Or(me.MyContact, "[我的联系方式]"));
                break;

            default:   // Paper:完整信件格式 —— 地址块 / 称呼 / 正文(段首缩进)/ 祝福 / 右对齐署名+日期
                sb.AppendLine(Or(d.TheirAddress, "[对方地址]"));
                if (d.TheirPostal.Trim().Length > 0) sb.AppendLine(d.TheirPostal.Trim());
                sb.AppendLine();
                if (call.Length > 0) sb.AppendLine(call);
                if (greet.Length > 0) sb.AppendLine("    " + greet);
                sb.AppendLine();
                foreach (var para in body.Split('\n'))
                    sb.AppendLine(para.Trim().Length > 0 ? "    " + para.Trim() : "");
                sb.AppendLine();
                if (close.Length > 0) sb.AppendLine(close);
                sb.AppendLine();
                var sign = Or(me.MyName, "[我的署名]");
                var date = d.SignDate.Trim().Length > 0 ? d.SignDate.Trim() : DateTime.Now.ToString("yyyy年M月d日");
                sb.AppendLine((sign + "  " + date).PadLeft(36));   // 右侧署名 + 日期(纸质专属)
                sb.AppendLine();
                sb.AppendLine(Or(me.MyAddress, "[我的地址]"));
                if (me.MyPostal.Trim().Length > 0) sb.AppendLine(me.MyPostal.Trim());
                sb.AppendLine(Or(me.MyContact, "[我的联系方式]"));
                break;
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// 按下「生成」:先落一条【生成中】的记录(输入与设置整份快照),再把产出写回去。
    /// ★ 永远【追加】—— 选中旧记录改完再生成,得到的是新的一条,旧的原样保留(用户裁定)。
    /// ★ 装配是同步的,所以 Generating 立刻转 false;AI 接入(P4)后这里就是真的等待窗口。
    /// </summary>
    public ReplyRecord Generate()
    {
        EnsureSession();                 // 有记录才进会话列表 —— 就是这一刻
        var d = Doc;
        var rec = new ReplyRecord
        {
            Generating = true,
            Draft = d.Draft, Incoming = d.Incoming,
            Medium = d.Medium, Tone = d.Tone, Language = d.Language,
            TheirName = d.TheirName, TheirAddress = d.TheirAddress,
            TheirPostal = d.TheirPostal, TheirContact = d.TheirContact, SignDate = d.SignDate,
        };
        d.Records.Add(rec);
        d.SelectedRecordId = rec.Id;
        Changed?.Invoke();               // 先让界面看到"正在生成"

        rec.Result = Compose(d, Profile);
        rec.Generating = false;
        d.Result = rec.Result;
        Changed?.Invoke();
        return rec;
    }

    /// <summary>删掉一条自定义问候/祝福(内置的删不掉 —— 那不是用户加的)。</summary>
    public bool RemoveCustom(bool greeting, string text)
    {
        var list = greeting ? Profile.CustomGreetings : Profile.CustomClosings;
        var j = list.IndexOf(text);
        if (j < 0) return false;      // 内置的删不掉 —— 那不是用户加的
        list.RemoveAt(j);

        // ★★ 存档里存的是【下标】而不是文字,所以删掉一条就必须把【所有会话】的下标重新对齐 ——
        //   不这么做,下标排在它后面的那些信会静默地换成另一句问候/祝福:
        //   内容被改了,却没有任何地方说一声。这是"诚实"那条纪律管得着的事。
        //   绝对下标 = 1(不加)+ 3(内置:每个语言×语气恒为三条)+ 它在自定义清单里的位置;
        //   自定义清单是跨语言共用的一份,所以这个换算与当前语言无关。
        var gone = CustomBase + j;
        foreach (var doc in _docs.Values.Append(_scratch))
        {
            var idx = greeting ? doc.GreetingIndex : doc.ClosingIndex;
            if (idx == gone) idx = 0;          // 正指着被删的那条 -> 退回「不加」
            else if (idx > gone) idx--;        // 排在它后面的整体前移一格
            else continue;
            if (greeting) doc.GreetingIndex = idx; else doc.ClosingIndex = idx;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>自定义条目在清单里的起始绝对下标:1 个「不加」+ 3 条内置。</summary>
    public const int CustomBase = 4;

    public bool IsCustom(bool greeting, string text)
        => (greeting ? Profile.CustomGreetings : Profile.CustomClosings).Contains(text);

    /// <summary>选中一条记录回看:把它的输入与设置读回编辑区(不动常驻模板)。</summary>
    public void SelectRecord(string? id)
    {
        var d = Doc;
        d.SelectedRecordId = id;
        if (d.Records.FirstOrDefault(r => r.Id == id) is { } r)
        {
            d.Draft = r.Draft; d.Incoming = r.Incoming; d.Result = r.Result;
            d.Medium = r.Medium; d.Tone = r.Tone; d.Language = r.Language;
            d.TheirName = r.TheirName; d.TheirAddress = r.TheirAddress;
            d.TheirPostal = r.TheirPostal; d.TheirContact = r.TheirContact; d.SignDate = r.SignDate;
        }
        Changed?.Invoke();
    }

    /// <summary>当前是否有记录正在生成 —— 界面据此禁用输入(用户裁定)。</summary>
    public bool Busy => Doc.Records.Any(r => r.Generating);

    // ---------------------------------------------------------------- 存档
    /// <summary>
    /// ★ 幽灵会话的文档不落盘(用户裁定 2026-08-03)。双保险的另一半是
    /// ChatCenter.GhostsPurged 广播后的 Drop —— 自动存盘是防抖的(任何 Changed 都会排一次写盘),
    /// 打字途中就会写一次,光靠"退出时删掉"守不住。
    /// </summary>
    public Func<string, bool>? IsGhostSession { get; set; }

    /// <summary>
    /// 会话没了(删除或幽灵被抹),它的文档一起清掉。
    /// ★ 顺手把指着它的 SessionId 断开(复核 2026-08-03):Doc 这个取值器是【取不到就新建一格】,
    ///   而 Drop 之后紧接着的 Changed 会让面板去读 Doc —— 刚删掉的键当场复活,
    ///   且此时 Chat.Find(它) 已经是 null、幽灵过滤器认不出它,这条空记录就这么进了存档。
    ///   两层"双保险"在这个次序下会同时失效,断开引用才是闭合的写法。
    /// </summary>
    public void Drop(string sessionId)
    {
        if (SessionId == sessionId) SessionId = null;
        if (_docs.Remove(sessionId)) Changed?.Invoke();
    }

    public ReplySave Export() => new()
    {
        Profile = Profile,
        Docs = _docs.Where(kv => !(IsGhostSession?.Invoke(kv.Key) ?? false)).ToDictionary(kv => kv.Key, kv => kv.Value),
    };
    public void Import(ReplySave? d)
    {
        if (d is null) return;
        Profile = d.Profile ?? new();
        _docs.Clear();
        foreach (var kv in d.Docs ?? new()) if (kv.Value is not null) _docs[kv.Key] = kv.Value;
        Changed?.Invoke();
    }
}
