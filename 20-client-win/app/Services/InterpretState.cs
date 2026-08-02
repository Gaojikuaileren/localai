// P3c -- 翻译工作空间的【三个场景】与同声传译的状态(用户裁定 2026-07-31)。
//
// 场景切换放在会话板块左上角:文字翻译 / 同声传译 / (第三个先留空)。
//
// 同传与文字翻译的语言模型不同,这点要写清楚:
//   文字翻译 = 一个【目标池】(最多 3),翻成池里除输入语言以外的全部;
//   同声传译 = 一对【固定方向】—— 我说的语言 -> 对方的语言。
//   固定方向是用户的设计判断,理由成立:省掉热路径上的语种检测,既降延迟,
//   也避免半句话被判错语种后整句翻歪。
//
// ★★ 架构底线(先写在这里,免得实现时被"顺手"破坏):
//   一旦我们成为用户的麦克风,【我们崩了 = 用户在会议里静音,而且他不知道】。
//   所以透传必须是一条【独立常驻、不经过任何模型】的线,AI 只是可选地往里注入;
//   管线一出错立刻退回透传,绝不静音。
//   —— 同传可以失败,用户的麦克风不可以。
//
// ★ 诚实:语音链路(采集/识别/合成/虚拟麦克风)尚未接入,界面如实说明,不做假开关、不伪造字幕。

namespace LocalAI.Client.Services;

public enum TranslationMode
{
    /// <summary>文字翻译(已完成)。</summary>
    Text,
    /// <summary>同声传译。</summary>
    Interpret,
    /// <summary>第三个场景 —— 尚未确定做什么(用户裁定先留空)。</summary>
    Reserved,
}

public sealed class InterpretState
{
    public event Action? Changed;

    public TranslationMode Mode { get; private set; } = TranslationMode.Text;

    /// <summary>
    /// 我说话用的语言。★ 初始【为空】—— 界面上是两个"拖入"的空坑,和目标池一个样子。
    /// 不预设一个看似合理的默认(比如中/英):那会让人以为已经设好了,而方向恰恰是同传里
    /// 最不能猜错的东西 —— 猜错就是整场翻反。拖过一次之后记住,下次沿用。
    /// </summary>
    public string MyLang { get; private set; } = "";
    /// <summary>
    /// 对方的语言 —— 我的话翻成它送出去;对方的话【只转成字幕】,不合成语音(用户裁定 2026-07-31)。
    /// ★ 为什么不给对方也配一路合成语音:会议里对方的原声一直在响,再叠一层机器声
    ///   等于两个人同时说话 —— 既盖住原声的语气,也让人分不清哪句是真的。
    ///   字幕是叠加,语音是覆盖;只有我这一侧【必须】变成语音,因为对方听不懂我的语言。
    /// 同样初始为空。
    /// </summary>
    public string TheirLang { get; private set; } = "";

    /// <summary>两端都设好了才谈得上开始同传。</summary>
    public bool DirectionReady => Languages.Find(MyLang) is not null && Languages.Find(TheirLang) is not null;

    /// <summary>
    /// 实时翻译输出总开关。★ 关 = 我们只做【透传】(把真麦克风原样送过去),
    /// 开 = 用同传的合成语音取代原麦克风。无论开关,那条透传线都一直在。
    /// </summary>
    public bool SpeakTranslation { get; private set; }

    /// <summary>对方声音的实时字幕 —— 对方这一侧【只有】这个,没有语音输出。</summary>
    public bool Subtitles { get; private set; } = true;

    /// <summary>用哪个声音说话。null = 通用音色;将来可指向"我的声音"(设置里注册)。</summary>
    public string? VoiceId { get; private set; }

    /// <summary>我方麦克风(端点 ID)。空 = 跟随系统默认。</summary>
    public string? InputDeviceId { get; private set; }
    /// <summary>音频输出设备(端点 ID)。我的译文语音送到这里(同传时送进虚拟声卡);空 = 跟随系统默认。</summary>
    public string? OutputDeviceId { get; private set; }

    /// <summary>
    /// 当前同传的实时延迟(秒)。★ null = 还没在跑 —— 界面显示"—"而不是 0:
    /// 一个写着 0.0s 的读数会让人以为"零延迟",那是这套系统里最不可能的事。
    /// </summary>
    public double? LatencySeconds { get; private set; }

    public void SetInputDevice(string? id) { if (InputDeviceId != id) { InputDeviceId = id; Changed?.Invoke(); } }
    public void SetOutputDevice(string? id) { if (OutputDeviceId != id) { OutputDeviceId = id; Changed?.Invoke(); } }
    public void ReportLatency(double? seconds) { LatencySeconds = seconds; Changed?.Invoke(); }

    public void SetMode(TranslationMode m)
    {
        if (Mode == m) return;
        Mode = m;
        // ★ 离开同传界面 = 这一场结束。否则会留下一个"还在进行中"却【看不见】的状态:
        //   人已经在文字翻译那边了,同传却还标着进行中,回来才发现它一直挂着。
        if (m != TranslationMode.Interpret && Running)
        {
            Running = false; StartedAt = null; RunningSessionId = null; LatencySeconds = null;
        }
        Changed?.Invoke();
    }

    public void SetMyLang(string code) { if (MyLang != code && Languages.Find(code) is not null) { MyLang = code; Changed?.Invoke(); } }
    public void SetTheirLang(string code) { if (TheirLang != code && Languages.Find(code) is not null) { TheirLang = code; Changed?.Invoke(); } }

    /// <summary>把两端对调 —— 换人说话时最常按的一个键。</summary>
    public void SwapLangs() { (MyLang, TheirLang) = (TheirLang, MyLang); Changed?.Invoke(); }

    public void SetSpeakTranslation(bool on) { if (SpeakTranslation != on) { SpeakTranslation = on; Changed?.Invoke(); } }
    public void SetSubtitles(bool on) { if (Subtitles != on) { Subtitles = on; Changed?.Invoke(); } }
    public void SetVoice(string? id) { if (VoiceId != id) { VoiceId = id; Changed?.Invoke(); } }

    /// <summary>
    /// 同传这条链路现在能不能真的跑起来。★ 语音模型未接入(P4)之前恒为 false ——
    /// 界面据此如实说明,而不是给一个按下去没反应的开关。
    /// </summary>
    public static bool PipelineReady => false;

    // ---------------------------------------------------------------- 一场同传的开始与结束
    // ★★ 用户裁定 2026-08-02:进同传页面【不自动开始】。
    //   原先一切进来就是同传界面,既不知道从哪儿开始,也没有边界感 ——
    //   "我只是点进来看看"和"我现在要开会了"必须是两件事。
    //
    // ★★ 口径(这一条最容易做成假开关,写清楚):
    //   「开始」指的是【这一场会话开始了】—— 建一条同传记录、锁定语言方向、解锁设置、开始计时。
    //   这些都是真的。它【不等于】引擎在跑:采集/识别/翻译/合成那一整套要等 P4。
    //   所以开始之后界面必须【当场写明】还没有转写(见 TranslationBar 的进行中提示),
    //   否则用户会对着一个安静的面板等半天,以为是坏了。

    /// <summary>这一场同传【已经开始】。★ 不进存档:重启之后不该还"在进行中"。</summary>
    public bool Running { get; private set; }

    /// <summary>这一场从什么时候开始(界面显示时长)。没在进行就是 null。</summary>
    public DateTime? StartedAt { get; private set; }

    /// <summary>这一场对应的同传会话 id —— 开始时建,结束后留成记录。</summary>
    public string? RunningSessionId { get; private set; }

    /// <summary>
    /// 现在能不能开始。返回空串 = 可以;否则是【不能开始的原因】(直接拿去给用户看)。
    /// ★ 语言方向是【硬前置】:方向猜错就是整场翻反 —— 这是同传里最不能猜的东西。
    /// ★ 引擎没接入【不拦】开始:那拦的是转写,不是这一场会话。混为一谈的话,
    ///   在 P4 之前这个按钮永远按不动,用户连自己设定的流程都走不通。
    /// </summary>
    public string WhyCannotStart()
        => DirectionReady ? "" : "先把语言方向的两个坑填上 —— 从语言池拖进来。";

    /// <summary>开始一场同传。返回空串 = 已开始;否则是不能开始的原因(此时什么也没变)。</summary>
    public string Start(string? sessionId)
    {
        if (Running) return "";
        // ★ 只能从同传界面开始 —— 在别的模式下"开始一场同传"没有意义,
        //   而且会造出一个界面上根本看不见的进行中状态。
        if (Mode != TranslationMode.Interpret) return "先切到同声传译界面再开始。";
        var why = WhyCannotStart();
        if (why.Length > 0) return why;
        Running = true;
        StartedAt = DateTime.Now;
        RunningSessionId = sessionId;
        Changed?.Invoke();
        return "";
    }

    /// <summary>结束这一场。会话记录保留 —— 结束的是"正在进行",不是把记录删掉。</summary>
    public void Stop()
    {
        if (!Running) return;
        Running = false;
        StartedAt = null;
        RunningSessionId = null;
        LatencySeconds = null;   // 没在跑就不该还挂着上一场的读数
        Changed?.Invoke();
    }

    /// <summary>同传模式需要的模型清单(切进来时由 GPU Broker 装卸,聊天模型明确不在内)。</summary>
    public static readonly string[] RequiredModels = { "asr-streaming", "translate", "tts-voice" };

    // ---------------------------------------------------------------- 存档(本机偏好)
    public sealed record Snapshot(string MyLang, string TheirLang, bool SpeakTranslation, bool Subtitles, string? VoiceId,
                                 string? InputDeviceId = null, string? OutputDeviceId = null);

    public Snapshot Export() => new(MyLang, TheirLang, SpeakTranslation, Subtitles, VoiceId, InputDeviceId, OutputDeviceId);

    public void Import(Snapshot? s)
    {
        if (s is null) return;
        // ★ 语言方向【沿用上次退出时的设定】(用户裁定):拖一次就记住,不用每次开会重设。
        if (Languages.Find(s.MyLang) is not null) MyLang = s.MyLang;
        if (Languages.Find(s.TheirLang) is not null) TheirLang = s.TheirLang;
        Subtitles = s.Subtitles;
        VoiceId = s.VoiceId;
        InputDeviceId = s.InputDeviceId;
        OutputDeviceId = s.OutputDeviceId;
        // ★ 【不】恢复 SpeakTranslation:"用合成语音取代我的麦克风"这种事,
        //   每次开会都该由用户当场确认一次,不能因为上次开着就自动接管。
        SpeakTranslation = false;
        Changed?.Invoke();
    }
}
