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

    /// <summary>我说话用的语言。</summary>
    public string MyLang { get; private set; } = "zh";
    /// <summary>对方的语言 —— 我的话翻成它,对方的话从它翻回来。</summary>
    public string TheirLang { get; private set; } = "en";

    /// <summary>
    /// 实时翻译输出总开关。★ 关 = 我们只做【透传】(把真麦克风原样送过去),
    /// 开 = 用同传的合成语音取代原麦克风。无论开关,那条透传线都一直在。
    /// </summary>
    public bool SpeakTranslation { get; private set; }

    /// <summary>对方声音的实时字幕。</summary>
    public bool Subtitles { get; private set; } = true;

    /// <summary>用哪个声音说话。null = 通用音色;将来可指向"我的声音"(设置里注册)。</summary>
    public string? VoiceId { get; private set; }

    public void SetMode(TranslationMode m) { if (Mode != m) { Mode = m; Changed?.Invoke(); } }

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

    /// <summary>同传模式需要的模型清单(切进来时由 GPU Broker 装卸,聊天模型明确不在内)。</summary>
    public static readonly string[] RequiredModels = { "asr-streaming", "translate", "tts-voice" };

    // ---------------------------------------------------------------- 存档(本机偏好)
    public sealed record Snapshot(string MyLang, string TheirLang, bool SpeakTranslation, bool Subtitles, string? VoiceId);

    public Snapshot Export() => new(MyLang, TheirLang, SpeakTranslation, Subtitles, VoiceId);

    public void Import(Snapshot? s)
    {
        if (s is null) return;
        if (Languages.Find(s.MyLang) is not null) MyLang = s.MyLang;
        if (Languages.Find(s.TheirLang) is not null) TheirLang = s.TheirLang;
        Subtitles = s.Subtitles;
        VoiceId = s.VoiceId;
        // ★ 【不】恢复 SpeakTranslation:"用合成语音取代我的麦克风"这种事,
        //   每次开会都该由用户当场确认一次,不能因为上次开着就自动接管。
        SpeakTranslation = false;
        Changed?.Invoke();
    }
}
