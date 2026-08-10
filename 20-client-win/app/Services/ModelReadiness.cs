// V30 -- 【模型就绪闸】。全客户端**唯一**一处回答「这个功能的模型现在能不能用」。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ 用户裁定(2026-08-09)原文:
//
//    「在对话窗口中输入文字就启用模型,但是当模型还没完全启用成功的时候以及模型没有启用的时候,
//      应该灰掉发送按钮并禁用,当启用完毕再亮起,如果模型被卸了,也要同时禁用……
//      **不仅仅是聊天功能,其他所有功能都是一样的,按需的模型没起来就禁用。**」
//
//  ★★ 最后那句是本文件存在的**全部理由**。它说的不是「聊天要加个判断」,
//    而是「这件事对每个入口都成立」——⇒ 判断只能有**一处**。
//    按视图各写一遍是本仓反复出现的那个形状(两份解析器 · 两份显存口径 ·
//    三份铺消息列表 …… 每一次的后果都一样:**改了一处,另一处悄悄退化,而没有任何东西会红**)。
//
//  ⇒ 所有入口订阅**同一个源**;新增入口没挂上来时,`SelftestModelGate` 的元断言判红。
//
//  ══════ 三态 + 一次回退,不是两态 ════════════════════════════════════════════
//    · NotStarted 未启用    —— 还没开始装 / 装失败了 / **装过又被卸了**
//    · Starting   正在启用中 —— 已经在起了,还没起完
//    · Ready      已启用    —— 现在真的能用
//  ★ 「被卸了」**不是第四态**,它回落到「未启用」—— 对使用者来说这两件事的下一步
//    完全一样(再打个字就会重新起)。多一个态只会让每个调用方都要多写一个分支。
//    ★★ 但**理由**分得开:文案会说清是"从来没起"还是"起过又被卸了"。
//    置灰而不说原因等于骗人,而给一个**错的**原因比不给更坏(本仓两条老判词)。
//
//  ══════ 两个面,同一个闸 ══════════════════════════════════════════════════
//   ① 中枢 GPU 面 —— `assistant.fast` 这类**别名**,就绪由中枢的意图应答 + 快照说了算;
//   ② 本机语音面 —— `local:` 开头,是 127.0.0.1:18085 那个独立进程,
//      就绪由**它自己的 /health** 说了算,与中枢的显存毫无关系。
//   ★★★ 两个面**证据源完全不同,而闸只有一个**。这正是本设计要证明的那件事:
//     共享的是【谁能用、怎么说】这套语义,不是【去哪儿问】。
//     反过来做(把语音硬塞给中枢的别名表)会立刻说假话 —— 它根本不在 registry.toml 里。
//
//  ══════ 证据的优先级(为什么是这个顺序)════════════════════════════════════
//   ① 没配对 / 主机不在线   → 未启用。根本没有中枢可问,后面每一条都无从谈起。
//   ② **中枢此刻真的装着它** → 已启用。这是最硬的证据 —— 它压过下面所有推断,
//      也是"用户勾了常驻、从没打过字"那一格唯一能判对的依据。
//   ③ 上一次意图**成功**    → 已启用(除非②反过来证明它已经被卸)。
//   ④ 正有一次意图在路上    → 正在启用中。
//   ⑤ 上一次意图**失败**    → 未启用,理由用**中枢给的那一句**(IntentOutcome.Advice)。
//   ⑥ 什么都没有            → 未启用,「打个字就会自动开始」。
//   ★ ②排在③④前面是有意的:模型本来就在跑时,第一次敲字会发一条意图,
//     若让④先命中,按钮会**先灰一下**再亮 —— 那是一次凭空的、假的"正在启用中"。
//
//  ══════ 别名 → 组件那座桥,仍然在中枢 ════════════════════════════════════
//   §8.1「客户端只点别名不点组件」。本类**没有**任何 alias→component 的硬编码:
//     · 组件名从 `IntentOutcome.Component` 来 —— 那是**中枢自己挑完告诉我们的**;
//     · 别名表从 `GpuCatalog.Components[].Aliases` 来 —— 那是中枢下发的 `aliases_by_component`。
//   ⇒ 换模型时这里仍然一行都不用改。读回中枢说过的话,不等于自己维护一份清单。
//
//  ══════ 1 Hz 的噪声在这里被吸收 ══════════════════════════════════════════
//   `HubGpu.Changed` 跟着显存快照**每秒一帧**。ChatView 文件头记着:拿它当刷新信号
//   会把输入框每秒重建一次,打字当场被打断。
//   ⇒ 本类接住那一帧,但只在**闸的答案真的变了**时才广播 `Changed`(边沿,不是电平)。
//     这正是"共享闸"比"每个视图各接一次"多出来的第二个好处:噪声只吸收一次。
// ══════════════════════════════════════════════════════════════════════════════

namespace LocalAI.Client.Services;

/// <summary>就绪三态。★ 「被卸了」回落到 <see cref="NotStarted"/> —— 见文件头。</summary>
public enum ModelReadyState
{
    /// <summary>未启用:没起过 / 起失败了 / 起过又被卸了。</summary>
    NotStarted,
    /// <summary>正在启用中:已经在起了,还没起完。</summary>
    Starting,
    /// <summary>已启用:现在真的能用。</summary>
    Ready,
}

/// <summary>
/// 一个别名此刻的闸。★ <c>Headline</c> 是用户裁定里点名的那两句气泡文案之一;
/// <c>Why</c> 是**具体**理由(中枢给的那一句,或我们说得出的那一句)。
/// <para>★★ 两者分开是有意的:气泡上那一行要短到能一眼读完,而"为什么"往往很长
/// (「这个模型还没有被授权按需装载。请到主机的…」)。合成一句的话,短的那半会被挤没。</para>
/// </summary>
public sealed record ModelGate(ModelReadyState State, string Headline, string Why)
{
    /// <summary>能不能用。★★ 只有 <see cref="ModelReadyState.Ready"/> 算数 ——
    /// 「正在启用中」**不算**能用:那正是用户点名要挡住的那一格。</summary>
    public bool CanUse => State == ModelReadyState.Ready;

    /// <summary>气泡里显示的全文(标题 + 理由)。★ 理由为空时不留一个孤零零的换行。</summary>
    public string Bubble => Why is { Length: > 0 } ? Headline + "\n" + Why : Headline;
}

/// <summary>
/// 模型就绪闸。★ 全进程**一个实例**(<c>App.Ready</c>),所有入口读它。
/// <para>★★ <see cref="Changed"/> **可能在后台线程上响**(意图的结果是在 Task.Run 里落地的)——
/// 订阅方必须自己切回 UI 线程,与 <see cref="HubGpu.IntentChanged"/> 同一条纪律。</para>
/// </summary>
public sealed class ModelReadiness
{
    // ── 别名常量:调用方**不许**自己写字符串 ──────────────────────────────
    /// <summary>聊天/翻译/回信共用的助手别名。★ 与 <c>ChatClient.StreamAsync</c> 用的是同一个。</summary>
    public const string ChatAlias = "assistant.fast";

    /// <summary>
    /// 本机语音服务的 ASR。★★ <c>local:</c> 前缀是**如实标注**,不是命名风格:
    /// 它**不在** <c>10-core/gateway/registry.toml</c> 的别名表里,也不占中枢的显存 ——
    /// 把它写成一个像 `speech.asr` 的名字,会让下一个人拿它去问中枢,而中枢会回 unknown_alias。
    /// </summary>
    public const string SpeechAsr = "local:speech.asr";

    /// <summary>这个别名走的是本机面(不问中枢)吗。</summary>
    internal static bool IsLocalPlane(string alias) => alias.StartsWith("local:", StringComparison.Ordinal);

    readonly HubGpu _gpu;
    readonly HubClient _hub;
    readonly object _lock = new();

    /// <summary>每个别名此刻的账。★ 只记**事实**,不记结论 —— 结论由 <see cref="Gate"/> 现算。</summary>
    sealed class Rec
    {
        /// <summary>有一次意图正在路上(NoteIntent 发了,结果还没回来)。</summary>
        public int InFlight;
        /// <summary>上一次意图的结果(本别名的,不是全局那个 LastIntent)。</summary>
        public IntentOutcome? Last;
        /// <summary>上一次广播出去的闸 —— 边沿判据(见文件头)。</summary>
        public ModelGate? Announced;
    }

    readonly Dictionary<string, Rec> _recs = new(StringComparer.Ordinal);

    // ── 本机语音面的证据 ──────────────────────────────────────────────────
    /// <summary>上一次探到的本机语音服务健康。null = 没探到(连不上 / 形状读不懂)。</summary>
    SpeechHealth? _speech;
    /// <summary>探过没有。★ 与 <c>_speech == null</c> **不是一回事**:没探过 ≠ 探过但没起来。</summary>
    bool _speechProbed;
    /// <summary>上一次探失败的原因(照实转给用户)。</summary>
    string _speechError = "";

    public ModelReadiness(HubGpu gpu, HubClient hub)
    {
        _gpu = gpu;
        _hub = hub;
        gpu.IntentStarted += OnIntentStarted;
        gpu.IntentSettled += OnIntentSettled;
        // ★ 每秒一帧的那条。本类吸收它 —— 见文件头「1 Hz 的噪声在这里被吸收」。
        gpu.Changed += Recheck;
        // ★ 配对/在线状态变了也要重算:①那一格直接取决于它。
        hub.Changed += Recheck;
    }

    /// <summary>闸的答案变了(**只在真的变了时**响)。★ 可能在后台线程 —— 订阅方自己切回 UI。</summary>
    public event Action? Changed;

    /// <summary>
    /// 这个别名现在能不能用。★ 现算,不缓存结论 —— 缓存结论就会有"结论过期了但没人重算"的窗口,
    /// 而那正是本仓最恨的形状(一个曾经为真的答案没跟着改)。
    /// </summary>
    public ModelGate Gate(string alias)
    {
        var rec = RecOf(alias);
        var g = Compute(alias, rec);
        lock (_lock) rec.Announced = g;      // 现读现记:下一次 Recheck 才知道"变没变"
        return g;
    }

    /// <summary>能不能用的那半句。★ 只是 <see cref="Gate"/> 的糖,判据只有一处。</summary>
    public bool CanUse(string alias) => Gate(alias).CanUse;

    Rec RecOf(string alias)
    {
        lock (_lock)
        {
            if (!_recs.TryGetValue(alias, out var r)) _recs[alias] = r = new Rec();
            return r;
        }
    }

    // ── 事实的入口 ────────────────────────────────────────────────────────
    void OnIntentStarted(string alias)
    {
        var r = RecOf(alias);
        lock (_lock) r.InFlight++;
        Recheck();
    }

    void OnIntentSettled(string alias, IntentOutcome res)
    {
        var r = RecOf(alias);
        lock (_lock)
        {
            if (r.InFlight > 0) r.InFlight--;
            r.Last = res;
        }
        Recheck();
    }

    /// <summary>
    /// 本机语音服务探了一次的结果。★ 由 <c>InterpretState</c> 报进来 ——
    /// <c>SpeechClient</c> 的持有者只有它一个(纪律:一份数据一个持有者),
    /// 本类**不自己再开一个客户端**去探,那就是第二个探针、第二套口径。
    /// </summary>
    /// <param name="health">探到的健康;null = 没探到。</param>
    /// <param name="error">没探到时的原因(照实显示给用户)。</param>
    public void NoteSpeechHealth(SpeechHealth? health, string? error)
    {
        lock (_lock)
        {
            _speech = health;
            _speechProbed = true;
            _speechError = error ?? "";
        }
        Recheck();
    }

    /// <summary>
    /// 重算所有**被人问过的**别名,只在有一条真的变了时广播一次。
    /// <para>★★★ 这里是"每秒一帧"与"界面刷新"之间的那道闸。电平信号进来,边沿信号出去。
    /// 去掉它的话,每一个订阅方都会每秒被叫醒一次 —— 输入框每秒重建、打字当场被打断,
    /// 而那正是 `ChatView` 文件头点名要防的事。</para>
    /// </summary>
    void Recheck()
    {
        var changed = false;
        List<string> aliases;
        lock (_lock) aliases = _recs.Keys.ToList();
        foreach (var a in aliases)
        {
            var rec = RecOf(a);
            var now = Compute(a, rec);
            bool fellOutOfReady;
            lock (_lock)
            {
                if (rec.Announced == now) continue;   // record 值相等 = 三个字段都一样
                fellOutOfReady = rec.Announced?.State == ModelReadyState.Ready
                                 && now.State != ModelReadyState.Ready;
                rec.Announced = now;
            }
            // ★★★ 刚从「已启用」掉下来(多半是被卸了)⇒ 把去抖冷却清掉。
            //   ★ 不清的话:按钮当场灰掉,而**长达 20 秒**没有任何人在试着把它修好 ——
            //     用户打字、什么都不发生、按钮一直灰着。那是最坏的一种"禁用"。
            //   ★★ 挂在**边沿**上而不是每次重算:清冷却是个动作,不是一个查询的副产物;
            //     每帧都清等于把去抖整个废掉(而去抖挡的是每敲一个字符发一次网络请求)。
            if (fellOutOfReady && !IsLocalPlane(a)) _gpu.ForgetIntentCooldown(a);
            changed = true;
        }
        if (!changed) return;
        // ★ 与 HubGpu.Notify 同款护栏:订阅方抛异常不该反噬发布方。
        try { Changed?.Invoke(); } catch { }
    }

    // ── 判据本体(中枢面)─────────────────────────────────────────────────
    /// <summary>
    /// 算一次闸(中枢 GPU 面)。★ 抽成 <c>internal static</c> 是为了让自检**直接喂形状**给它 ——
    /// 界面上用的那段判据,和断言验的那段判据必须是**同一段代码**
    /// (与 <c>TaskDrawerView.CanResume</c> 同一手法)。
    /// </summary>
    /// <param name="paired">配对了没有。</param>
    /// <param name="online">主机在线没有。</param>
    /// <param name="inFlight">有意图在路上没有。</param>
    /// <param name="last">上一次意图的结果(本别名的)。</param>
    /// <param name="residentByCatalog">
    /// 中枢此刻**真的装着**服务这个别名的组件吗。★ 三值:
    /// true = 装着 · false = 明确没装 · null = **说不出**(目录/快照读不到)。
    /// ★★ false 与 null 绝不合并:前者是"我看过了,它不在";后者是"我没看到"。
    /// 把 null 当 false 会在推送流抖一下时把发送键灭掉,而那是一句**假的**"模型没起来"。
    /// </param>
    internal static ModelGate Decide(bool paired, bool online, bool inFlight,
                                     IntentOutcome? last, bool? residentByCatalog)
    {
        // ① 根本没有中枢可问。★ 与输入框上方那两行说的是同一件事,所以理由照抄它们的口径。
        if (!paired)
            return new ModelGate(ModelReadyState.NotStarted, NotStartedText,
                "还没有配对到中枢 —— AI 在主机上运行,没有中枢就没有模型可启用。"
                + "到「设置」最下面的「已配对的电脑」里点「开始寻找主机」。");
        if (!online)
            return new ModelGate(ModelReadyState.NotStarted, NotStartedText,
                "主机未开启 —— AI 在主机上运行,它得在线才起得来。");

        // ② 最硬的证据:中枢此刻真的装着它。★ 压过下面所有推断(见文件头的顺序说明)。
        if (residentByCatalog == true)
            return new ModelGate(ModelReadyState.Ready, "", "");

        // ③ 上一次意图成功 —— 除非②刚刚**明确**说它已经不在了。
        if (last is { Ok: true })
        {
            // ★ 这个别名不占显存(比如常驻助手跑 CPU)⇒ 没有"被卸"这回事,恒为就绪。
            if (last.Code == "no_gpu_needed")
                return new ModelGate(ModelReadyState.Ready, "", "");
            if (residentByCatalog == false)
                // ★★★ 用户点名的那一格:「如果模型被卸了,也要同时禁用」。
                //   ★ 理由必须说清是**被卸**而不是从没起过 —— 两者的心理预期完全不同:
                //     前者是"刚才还好好的",人会以为是自己弄坏了什么。
                return new ModelGate(ModelReadyState.NotStarted, NotStartedText,
                    "模型刚才起来过,现在已经被卸下了(空闲会自动卸,显存紧张时也会让位给别的程序)。"
                    + "★ 在输入框里打字就会重新启用它。");
            // ★ residentByCatalog == null:目录/快照读不到。**保留中枢说过的那句"装上了"**。
            //   ★★ 这一条是有意的,理由写清楚:退回"未启用"会让推送流每次重连(退避最长 30 秒)
            //     都把发送键灭一次,而那是一句**编出来的**"模型没起来" —— 我们并不知道它没起。
            //     反过来,万一它真被卸了,代价只是这一次发送撞上 503,而那条路径已经有分因处置
            //     (ChatView 的 probeBackend 会再问一次中枢,顺带把它装回来)。
            //     ⇒ 两种错里选代价小、且**不说假话**的那一种。
            return new ModelGate(ModelReadyState.Ready, "", "");
        }

        // ④ 正在路上。★ 排在③后面:模型本来就在跑时不该先灰一下再亮。
        if (inFlight)
            return new ModelGate(ModelReadyState.Starting, StartingText,
                "已经在为你启动模型了,通常几秒到十几秒。");

        // ⑤ 上一次意图失败 —— 理由用**中枢给的那一句**,不是我们编的。
        if (last is { Ok: false })
            return new ModelGate(ModelReadyState.NotStarted, NotStartedText,
                last.Advice is { Length: > 0 } a ? a : last.Message);

        // ⑥ 什么都还没发生。
        return new ModelGate(ModelReadyState.NotStarted, NotStartedText,
            "模型还没有启用 —— 在输入框里打字就会自动开始启用。");
    }

    // ── 判据本体(本机语音面)──────────────────────────────────────────────
    /// <summary>
    /// 算一次闸(本机语音服务)。★ 同样抽成 <c>internal static</c> 供自检直接喂形状。
    /// <para>★★★ 这里**没有**「显存」「中枢」「别名」任何一个概念 —— 它问的是
    /// 另一个进程的 <c>/health</c>。而它吐出来的是**同一种** <see cref="ModelGate"/>:
    /// 调用方(按住说话那颗按钮)不需要知道自己问的是哪个面。</para>
    /// </summary>
    /// <param name="probed">探过没有。★ 与 <paramref name="health"/> 为 null **不是一回事**。</param>
    internal static ModelGate DecideLocalSpeech(bool probed, SpeechHealth? health, string error)
    {
        if (!probed)
            return new ModelGate(ModelReadyState.NotStarted, NotStartedText,
                "还没有探到本机语音服务的状态 —— 打开这一页会自动探一次。");
        if (health is null)
            return new ModelGate(ModelReadyState.NotStarted, NotStartedText,
                error is { Length: > 0 } ? error
                : "连不上本机语音服务 —— 它是主机上的一个独立进程(回环 127.0.0.1),得先起来。");
        // ★ 服务起来了、模型还在装。★★ 这**正是**「正在启用中」那一格:
        //   服务端自己就是这么说的(未就绪时 /v1/speech/asr 回 503「还在装模型」)。
        if (!health.Ready || !health.AsrLoaded)
            return new ModelGate(ModelReadyState.Starting, StartingText,
                health.Detail is { Length: > 0 } d
                    ? "本机语音服务已启动,识别模型还在装:" + d
                    : "本机语音服务已启动,识别模型还在装。");
        return new ModelGate(ModelReadyState.Ready, "", "");
    }

    /// <summary>气泡上那两句(用户裁定原文点名的文案)。★ 常量化:两处以上要用,漂了不会有人发现。</summary>
    public const string NotStartedText = "模型未启用";
    public const string StartingText = "正在启用中,请稍等";

    ModelGate Compute(string alias, Rec rec)
    {
        if (IsLocalPlane(alias))
        {
            lock (_lock) return DecideLocalSpeech(_speechProbed, _speech, _speechError);
        }
        bool inFlight;
        IntentOutcome? last;
        lock (_lock) { inFlight = rec.InFlight > 0; last = rec.Last; }
        return Decide(_hub.IsPaired, _hub.State == HubState.Online, inFlight, last, ResidentOf(alias, last));
    }

    /// <summary>
    /// 中枢此刻装着服务这个别名的组件吗。★ 三值 —— null = **说不出**(见 <see cref="Decide"/> 的参数说明)。
    /// <para>★★ 两条证据都来自**中枢自己说过的话**,本类没有任何 alias→component 的硬编码:
    /// ① 目录里的 <c>aliases_by_component</c>(中枢下发的桥);
    /// ② 上一次意图里中枢**自己挑完告诉我们**的那个组件名。</para>
    /// <para>★ 快照不新鲜就返回 null 而不是 false:「我没看到」不等于「它不在」。</para>
    /// </summary>
    bool? ResidentOf(string alias, IntentOutcome? last)
    {
        if (!_gpu.HasFreshData) return null;                  // 快照读不到 ⇒ 说不出
        var resident = TokenBudget.ResidentOf(_gpu.Snapshot);  // ★ 并集算法只在那一处
        EnsureCatalog();
        // ① 目录里查:哪些组件登记着这个别名。
        if (_gpu.LastCatalog is { } cat)
        {
            var serving = cat.Components.Where(c => c.Aliases.Contains(alias, StringComparer.Ordinal)).ToList();
            if (serving.Count > 0)
                return serving.Any(c => resident.Contains(c.Id, StringComparer.Ordinal));
        }
        // ② 没有目录时退到"中枢上次挑的那个组件"。★ 只有它非空才说得出话。
        if (last is { Ok: true, Component.Length: > 0 })
            return resident.Contains(last.Component, StringComparer.Ordinal);
        return null;
    }

    /// <summary>上一次去取组件目录的时刻(节流用)。</summary>
    DateTime _catalogAskedAt = DateTime.MinValue;
    /// <summary>目录取不到就再等这么久 —— 中枢刚起来那阵会连着失败几次,别贴上去打。</summary>
    static readonly TimeSpan CatalogRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 手上没有组件目录就去取一次(即发即忘、节流)。
    ///
    /// <para>★★★ 为什么需要它:目录里那张 <c>aliases_by_component</c> 是**证据②**的全部来源,
    /// 而在此之前全客户端只有任务抽屉在"有暂停任务时"取过它 —— 也就是说,平时它是空的。
    /// 空的后果很具体:用户把模型勾成了**常驻**、它此刻真的在跑,而闸因为看不见这座桥
    /// 只能说「未启用」,发送键要等他敲第一个字、意图回来之后才亮。
    /// 那是一句**假话**——模型明明已经起来了。</para>
    ///
    /// <para>★ 取不到就算了(<c>LastCatalog</c> 留 null ⇒ 证据②说不出 ⇒ 退到证据③)。
    /// 这里咽下异常是**有边界**的:它不会让闸说出任何一句假话,只会让它少一条证据;
    /// 而"够不着中枢"这件事本身有它自己的落点(GPU 推送流的状态、输入框上方那两行),
    /// 不该由这儿再喊一遍 —— 同一件事说两遍会读成两个问题。</para>
    /// </summary>
    void EnsureCatalog()
    {
        if (_gpu.LastCatalog is not null) return;
        if (_hub.State != HubState.Online) return;      // 中枢都不在,取什么
        lock (_lock)
        {
            if (DateTime.UtcNow - _catalogAskedAt < CatalogRetryAfter) return;
            _catalogAskedAt = DateTime.UtcNow;
        }
        _ = Task.Run(async () =>
        {
            try { await _gpu.FetchCatalogAsync(); }
            catch { /* ★ 见上面那段:少一条证据,不多一句假话 */ }
            Recheck();                                   // 取到了就让闸当场重算一次
        });
    }

    // ── 登记表:凡会发起模型调用的入口,都在这儿有一条 ──────────────────────
    /// <summary>
    /// 一个**会发起模型调用**的入口。★ 这张表是 <c>SelftestModelGate</c> 元断言的一半 ——
    /// 另一半是从编译集里**扫出来的**真实调用点。两边对不上就判红:
    /// 新增了入口没登记 → 红;登记了却已经不存在 → 也红(表只许跟着代码改,不许发霉)。
    /// </summary>
    /// <param name="File">调用点所在的源文件名(不含目录)。</param>
    /// <param name="Call">调用点的特征串(元断言就是拿它去编译集里扫的)。</param>
    /// <param name="Alias">这个入口要的别名。</param>
    /// <param name="GatedIn">闸挂在哪个文件里 —— 那个文件必须真的读 <c>TheApp.Ready.Gate(</c>。</param>
    /// <param name="Why">为什么是这样。★ 每条都要写 —— 一张没有理由的表,下一个人只会照抄。</param>
    public sealed record CallSite(string File, string Call, string Alias, string GatedIn, string Why);

    /// <summary>
    /// 全客户端**会发起模型调用**的入口全表。★★ 新增一个入口就得在这里加一条,
    /// 否则 <c>SelftestModelGate</c> 当场判红 —— 那正是用户裁定里
    /// 「其他所有功能都是一样的」这句话唯一能被**强制**执行的形态。
    /// </summary>
    public static readonly CallSite[] CallSites =
    {
        new("ChatCenter.cs", "ChatClient.StreamAsync(", ChatAlias, "ChatView.cs",
            "真正把话发给模型的那一次。★ 闸挂在调用它的界面(ChatView)上而不是这里:"
            + "ChatCenter 是状态层,它没有按钮可以灰,也不该知道有没有按钮。"),
        new("ChatView.cs", "TheApp.Chat.SendAndAskAsync(", ChatAlias, "ChatView.cs",
            "聊天/翻译/回信三个场景共用的发送路径(同一个 ChatView,spec 不同)。"
            + "★ 发送键与 Enter 走**同一条**前置条件 —— 只灰按钮而回车照发,那种禁用纯属做样子。"),
        new("InterpretState.cs", "_speech.TranscribeAsync(", SpeechAsr, "InterpretPanel.cs",
            "按住说话(PTT)的转写。★★ 它走的是**本机语音面**:127.0.0.1:18085 那个独立进程,"
            + "就绪由它自己的 /health 说了算,与中枢的显存无关。"
            + "⇒ 同一个闸、**另一个证据源** —— 那正是「共享闸」要证明的事。"),
    };
}
