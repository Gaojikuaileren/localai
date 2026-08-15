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
// ★ 诚实(★★ 2026-08-15 · V38 更正:这一段此前是 P3c 那一版的话,**不带时态**,
//   而采集与识别早在 D104 就接上了 —— 就在本文件下面那一整段「按住说话」
//   ★ 用段名不用行号:另一条车道正在同一文件里加注释,行号写下来当天就会过期):
//     · 采集 ✅ 已接(D104)· 识别 ✅ 已接(D104,走本机 127.0.0.1:18085)
//     · 合成 🟡 只有服务端,客户端半边没有 · 虚拟麦 ❌ 未接(按 D105 不属于 P5 v1)
//     · 同传本体 ❌ 未接 —— 下面的 `PipelineReady` 恒为 false,界面如实说明,
//       不做假开关、不伪造字幕。★ 按住说话与同传是**两件事**,别因为这一行把它们读成一件。

namespace LocalAI.Client.Services;

public enum TranslationMode
{
    /// <summary>文字翻译(已完成)。</summary>
    Text,
    /// <summary>同声传译。</summary>
    Interpret,
    /// <summary>第四个场景 —— 用户说想好了但还没讲,位置留着(2026-08-02)。</summary>
    Reserved,
    /// <summary>文件翻译(第三场景,用户裁定 2026-08-02,D59):PDF/PNG/JPG 进,同排版出。</summary>
    FileTrans,
    /// <summary>多语言表(第四场景,用户裁定 2026-08-02,D60):开发者的 i18n 键值表翻译与导出。</summary>
    I18n,
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

    /// <summary>
    /// 用哪个声音说话。null = 通用音色;将来可指向"我的声音"(设置里注册)。
    ///
    /// <para>★★ V37(2026-08-15)**登记一条本车道没被派到、但顺手查实了的**:
    /// 这一条与它的 setter `SetVoice`(见下)今天是**假开关** ——
    /// `SetVoice` **全仓零调用**(只有它自己的定义),`VoiceId` 除本行外也只出现在
    /// `Snapshot` 的存取往返里(`Export` / `Import`)。
    /// ⇒ 它**存得下、会跨重启记住,却没有任何界面能设它、也没有任何代码读它去干活**。
    /// 那正是 D84 **裁定三**点名的形状(`DECISIONS.md:3751-3753`:
    /// 「一个存得下、却谁也不看的偏好就是**假开关**」),
    /// 与上面 `RequiredModels` 是同一个病 —— 只是这一条**没有跨车道问题**。</para>
    ///
    /// <para>★ **为什么本车道只登记不删**(不是忘了):
    /// ⒜ 派工单只点了三件事,删它属于**自行扩范围**;
    /// ⒝ 它在 `Snapshot` 这条**记录**里(`:352` / `:355` / `:364`),
    ///   删字段会改掉已经写进用户盘上的存档形状 —— 那是**行为改动**,不是清理死代码,
    ///   该单独判、单独测;
    /// ⒞ 上面那句"将来可指向我的声音"落不落地,是 P5 语音的**产品裁定**,不该由清理顺手替用户定。
    /// ⇒ 交回单里点名,等派工。</para>
    /// </summary>
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

    // ★ 空串 = 【清空这个坑】(审计 2026-08-02):原先校验把 "" 当无效语言码静默吞掉,
    //   于是"从坑里拖出去"是死代码 —— 两坑一满语言池又整体禁用,方向从此永远改不了。
    public void SetMyLang(string code) { if (MyLang != code && (code.Length == 0 || Languages.Find(code) is not null)) { MyLang = code; Changed?.Invoke(); } }
    public void SetTheirLang(string code) { if (TheirLang != code && (code.Length == 0 || Languages.Find(code) is not null)) { TheirLang = code; Changed?.Invoke(); } }

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
        => !DirectionReady ? "先把语言方向的两个坑填上 —— 从语言池拖进来。"
         // ★ 同语言方向拦下(审计 2026-08-02):坑到坑一拖能造出「中文↔中文」,
         //   那不是一场同传,是一场复读 —— 建出来的记录也没意义。
         : MyLang == TheirLang ? "两边是同一种语言 —— 先把其中一个换掉。"
         : "";

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

    /// <summary>
    /// ★★★ 【死代码 · 零读取方 · 三个名字是编的】—— V37(2026-08-15)查清并如实标注,
    /// **今天没删**,原因在最下面。在删掉之前,任何人都不要照着它写东西。
    ///
    /// <para>原来这里写的是「同传模式需要的模型清单(切进来时由 GPU Broker 装卸,
    /// 聊天模型明确不在内)」。**那句话每一截都不成立**:</para>
    ///
    /// <para>① **没有任何生产代码读它**。全仓读取方只有**一处**,是
    /// `Selftest.cs:1488-1490` 那**一条** `Assert` —— 它在那里面读了**两次**
    /// (`.Length` 在 `:1488`、`.Contains("chat")` 在 `:1489`)。
    /// 一条断言在替它作证,而它谁也不驱动。所谓「由 GPU Broker 装卸」
    /// **没有对应代码**:Broker 那条路走的是网关别名 + 显存组件 id,从来不经过这里。</para>
    ///
    /// <para>② **三个名字全仓零匹配,而且不是拼错 —— 是编的**:
    /// · `translate` —— **仓里根本没有翻译模型这个东西**。`registry.toml` 十个别名、
    ///   `vram-budget.toml` 九个组件里都没有;翻译是**由 LLM 做的**;
    /// · `asr-streaming` / `tts-voice` —— ASR 与 TTS **不是两个独立组件**,
    ///   两者都住在 `speech.lite` / `speech.full` 里(Piper 走 CPU,peak 计 0)。
    /// ⇒ 真实词汇只有两套:网关别名(`assistant.voice` …)与显存组件 id
    ///   (`speech.lite` · `llm.assistant.8b@8k` …),这三个名字**一套都对不上**。
    /// ★ 「改对」这个动作**技术上做得到**(改内容能编过,不像删符号那样编不过)——
    ///   初稿写成「『改对』这个选项不存在」,那是话说过了,这里更正。
    ///   它不被采用是因为**另外两条**:⒜ 改完 `Length == 3` 当场判红 ⇒ 门禁 FAIL≠0,
    ///   而那条断言在别的车道手里;⒝ **改对了也还是错的形状** —— D84 裁定二说
    ///   客户端**根本不该持有这份清单**。⇒ 不存在的不是"改对"这个动作,
    ///   而是"改对之后它就对了"这个结果。</para>
    ///
    /// <para>③ **来路**:它们是 2026-07-31 骨架提交(`0f8388e`)当天现编的占位词,
    /// 那时唯一数据源(`vram-budget.toml` 的 display / 组件 id)**还不存在**,此后没人回来对账。
    /// ★ 这正是 D84 删掉的那个形状(`ModelCatalog` 的 `chat.8b`/`speech`/`image`,
    ///   「跟网关别名与显存组件 id 一个都对不上」),只是**它长在另一个文件里,所以活下来了**。</para>
    ///
    /// <para>★★ 按 D84 该怎么处置,已经是明的:**裁定二**「客户端不得维护第二份组件清单」
    /// (目录由中枢 `GET /v1/gpu/components` 下发)+ **裁定三**「没人读的一律删掉,
    /// 不留着 —— 不是注释掉留着以后参考」。⇒ **结论是删,不是改**。</para>
    ///
    /// <para>★★★ 今天没删的**唯一**原因是**跨车道**:删掉这个符号会让 `Selftest.cs:1488`
    /// **编译不过**(不是红测 —— 是整个 app 编不出来,别的车道一起卡),
    /// 而 `Selftest*.cs` 归 V36。⇒ 删除必须是**一个提交里的两文件原子改动**,已交给 V36。</para>
    ///
    /// <para>★★★★ 给 V36 的**实质提醒**(那条断言不能只改数字):它今天验的是「长度 3
    /// 且不含 `chat`」,而**后半截钉的是一条与仓库事实相反的性质** ——
    /// 同传的翻译那一步**就得靠 LLM**。
    /// ⇒ 「同传清单里没有聊天模型」这句话本身就不该再钉。而且 `Contains("chat")` 是**空判据**:
    /// 真实 id 里永远不会有哪一条**恰好等于** `"chat"` 这个字符串,这一半从来没拦下过任何东西。</para>
    ///
    /// <para>★ 上面那条结论要**换一个证据**来撑(初稿举的那个不够硬,如实换掉):
    /// 初稿举的是 `registry.toml:68` 的 `assistant.voice`(「语音场景钉 8K」)。
    /// 可那个别名**零生产调用点** —— 全仓只有 `registry.toml:68` 的声明、
    /// `vram-budget.toml:113` 的一句备注、`test_component_bridge.py` 三处测试;
    /// `20-client-win` 里**没有任何地方请求过它**,也没有任何东西把它系到同传上。
    /// ⇒ 拿一个没人调的声明当主证,和被它钉的那条断言是同一个毛病。
    /// **真正在跑翻译流量的别名是 `assistant.fast`**:`app/Services/ChatClient.cs:149`
    /// (`model = "assistant.fast"`)· `Views/ChatView.cs:1201`(「聊天/翻译/回信三个场景**都走
    /// assistant.fast**」)· `90-ops/localai-model-replacement-eval/cases.jsonl` 里五条
    /// `"category":"translation"` 也全挂在它上面。
    /// ⇒ 结论不变(翻译靠 LLM、且那就是聊天那个模型),但**证据换成活的这条**。</para>
    /// </summary>
    public static readonly string[] RequiredModels = { "asr-streaming", "translate", "tts-voice" };

    // ═════════════════════════════════════════════════════════════════════════
    //  D?(P5 语音 v1)· 【按住说话】—— 半双工,一次按住 = 一段话
    // ═════════════════════════════════════════════════════════════════════════
    //
    //  ★★ 它与上面那套「同传」是**两件事**,不许混:
    //    · 同传 = 连续、双向、经过翻译与合成 —— `PipelineReady` 今天仍恒为 false;
    //    · 按住说话 = 半双工、单向、只做 ASR —— 这一条**今天真的能用**(本机)。
    //    把两者混成一个开关,会让"同传还没接"这句话把一个已经能用的功能也盖掉。
    //
    //  ★★★ 架构底线在这里的落法(**结构性,不是 try-catch**):
    //    `AudioCapture` 与 `SpeechClient` 是**两个互不认识的对象**,本状态类持有它们两个,
    //    但它们之间**没有任何引用**。⇒ 转写失败时录音这条路一个字节都没受影响:
    //    `PttLastWav` 仍然握着那段音频,界面据此如实说「录到了,但这次没转成字」。
    //    ⇒ 用户的麦克风**在代码路径上**就不依赖语音服务,而不是靠某个 catch 兜住。

    readonly AudioCapture _capture = new();
    readonly SpeechClient _speech = new();

    /// <summary>正在按住(录音中)。</summary>
    public bool PttRecording => _capture.Recording;

    /// <summary>已经按住了多久(秒)—— 界面显示,让人知道它真的在录。</summary>
    public double PttSeconds => _capture.Seconds;

    /// <summary>上一次按住说话的转写结果。null = 还没有 / 这次没转成。</summary>
    public AsrResult? PttResult { get; private set; }

    /// <summary>
    /// 上一次录到的那段音频。★ **转写失败也留着** —— 它是"你的话没丢"的凭据,
    /// 也是将来"重试转写"的原料。录音成功而转写失败时,界面靠它说实话。
    /// </summary>
    public byte[]? PttLastWav { get; private set; }

    /// <summary>上一次的失败原因(录音的或转写的)。空 = 没有失败。</summary>
    public string PttError { get; private set; } = "";

    /// <summary>正在转写(界面显示转圈,并且此时不该再开始下一次按住)。</summary>
    public bool PttTranscribing { get; private set; }

    /// <summary>
    /// 这段转写能不能直通记忆写入。★ 判据只有一条:**服务端给的 provenance**。
    ///   来源档位由**通道**决定(回环 / 已认证 LAN 设备),不由客户端自报。
    /// </summary>
    public bool PttMayWriteMemory => SpeechClient.MayWriteMemory(PttResult);

    /// <summary>按下:开始录。返回空串 = 开始了;否则是原因(直接给用户看)。</summary>
    public string PttPress()
    {
        if (PttTranscribing) return "上一段还在转写,稍等一下。";
        PttResult = null;
        PttError = "";
        var why = _capture.Start();          // ★ 这一步**完全不碰网络**
        if (why.Length > 0) PttError = why;
        Changed?.Invoke();
        return why;
    }

    /// <summary>
    /// 松开:停止录音,然后**才**去转写。
    ///
    /// ★ 两步之间是**顺序**关系,不是嵌套:录音先收尾并把字节拿在手上(PttLastWav),
    ///   之后转写成不成都不再影响它。这就是"麦克风不可失败"在这一层的样子。
    /// </summary>
    public async Task PttReleaseAsync(CancellationToken ct = default)
    {
        var wav = _capture.StopAndTakeWav();   // ★ 录音这条路到此为止,已经交付
        PttLastWav = wav;
        Changed?.Invoke();
        if (wav is null)
        {
            // ★ 一个采样都没录到 —— 与"录到了但没听清"是两件事,分开说。
            if (PttError.Length == 0) PttError = "没有录到声音 —— 按住的时间太短,或者麦克风没有拾到音。";
            Changed?.Invoke();
            return;
        }
        PttTranscribing = true;
        Changed?.Invoke();
        try
        {
            PttResult = await _speech.TranscribeAsync(wav, ct);
            if (PttResult is null)
            {
                // ★★ 说清"录到了、只是没转成" —— 否则用户会以为自己白说了一遍。
                PttError = (_speech.LastError ?? "转写失败") + " ★ 你的话已经录下来了,没有丢。";
                // ★★★ V30:转写失败 ⇒ **重新探一次** /health,把就绪闸的账刷新。
                //   ★ 为什么是"重新探"而不是直接把闸判成未启用:失败有两种,下一步完全不同 ——
                //     503「还在装模型」应当是【正在启用中】(等一下就好),
                //     连不上则是【未启用】(服务根本没起)。拿失败本身去猜,必然把两者说成一件事。
                //   ★★ 只在失败这条路上多一次请求;成功时闸本来就已经是就绪了,不必再问。
                _ = ProbeSpeechAsync(CancellationToken.None);
            }
        }
        finally
        {
            PttTranscribing = false;
            Changed?.Invoke();
        }
    }

    /// <summary>探一次本机语音服务(界面进页面时调一次,用来如实说明可用性)。</summary>
    public Task<SpeechHealth?> PttHealthAsync(CancellationToken ct = default) => _speech.HealthAsync(ct);

    // ══════════════════════════════════════════════════════════════════════
    //  ★★★ V30:把语音服务的就绪**报给共享的就绪闸**(ModelReadiness)。
    //
    //  ★ 为什么由本类报,而不是让闸自己去探:`SpeechClient` 的持有者只有本类一个
    //    (纪律:一份数据一个持有者)。闸再开一个客户端 = 第二个探针 + 第二套口径,
    //    而两套口径迟早会岔开,岔开时**不会有任何东西红**。
    //  ★★ 除了显式探一次,**每一次真实的转写也在报**:成功 = 它此刻确实起着;
    //    失败 = 把 SpeechClient 给的原因原样带过去。真实流量比定期轮询更准,也不多花一次请求。
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>共享的就绪闸。★ 由 App 注入(与 `Gpu.Tasks = Tasks` 同一手法),可以为 null。</summary>
    public ModelReadiness? Readiness { get; set; }

    /// <summary>
    /// 探一次本机语音服务**并把结果报给就绪闸**。★ 界面进页面时调一次。
    /// <para>★ 与 <see cref="PttHealthAsync"/> 分开留着:那个是"我要这个值",
    /// 这个是"顺便让闸也知道"。合成一个会让任何一次取值都产生副作用。</para>
    /// </summary>
    public async Task ProbeSpeechAsync(CancellationToken ct = default)
    {
        var h = await _speech.HealthAsync(ct);
        Readiness?.NoteSpeechHealth(h, _speech.LastError);
    }

    /// <summary>
    /// 按住说话今天**在这台机器上**能不能用。
    /// ★ v1 只连本机回环:主机上可用;副机要等网关把 /v1/speech/* 代理过去(本轮未做)。
    ///   界面据此如实说明,不摆一个按下去没反应的按钮。
    /// </summary>
    public static bool PushToTalkIsLocalOnly => true;

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
