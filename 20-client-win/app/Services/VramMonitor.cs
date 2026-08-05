// P3c -- 显存实时监视(左导航的显存条)。
//
// 取数方式:**直调 NVML**(nvml.dll P/Invoke),不是每次 shell 出 nvidia-smi。
//   理由:vram_gate.py 走 subprocess 是因为它一次性判定,几百毫秒无所谓;
//   而 UI 要持续轮询,每次开一个进程(~30–80ms + 进程创建开销)既费电又会在任务管理器里刷屏。
//   NVML 查询是进程内调用,单次 <1ms。读不到 NVML 时**降级**到 nvidia-smi(低频),再不行就隐藏条。
//
// 轮询频率:默认 2 秒。取舍 ——
//   · 显存变化是**事件驱动**(装/卸模型)叠加桌面的缓慢浮动,1 秒的视觉收益几乎为零;
//   · 2 秒既跟得上"模型刚装上"的变化,又把开销压到可忽略(每分钟 30 次进程内调用);
//   · ★ 更关键的省电手段是**不可见就不轮询** —— 窗口最小化/缩到托盘时停表(见 Pause/Resume)。
//     这比把间隔从 2 秒调到 5 秒有效得多。
//
// ══════════════════════════════════════════════════════════════════════
//  ★★★ P4-S9 更正(2026-08-04):数据源默认是【中枢】,不是本机。
//
//  在这之前本文件无条件直调本机 nvml 的 index 0。在主机上碰巧没错(本机就是中枢);
//  在**副机**上它显示的是副机自己那张卡,而标签只写「显存」——
//  用户会以为看的是中枢那 8.52 GiB 预算,实际看的是另一台机器的显卡。
//  两台机器的数字长得一模一样,**没有任何地方能看出来看错了**。这正是本项目最恨的形状。
//
//  ⇒ S9 当时的做法:退回本机,但把标题改成「本机显卡(不是中枢的)」。
//
// ══════════════════════════════════════════════════════════════════════
//  ★★★ 2026-08-05 用户裁定 + 实测:**本机回退整个拆掉**。
//
//  用户原话:「显存的显示,要显示的是主机的显存,副机要主机显存,主机要主机显存。」
//           「拿不到就显示主机未连接。」
//
//  ★★ 而实测发现 S9 那个"改标题"的修复**从来没接到界面上** ——
//    `VramSnapshot.Title` 全仓只有 Selftest 在读;`VramBar` 的标题是构造函数里
//    写死的 `_title.Text = "显存"`,`Update()` 一次都没碰过 `s.Title`。
//    ⇒ 副机上显示的一直是「显存」+ 副机自己那张卡的数字,**和 S9 之前一模一样**。
//    钉着 Title 的那几条断言测的是一个孤立的纯函数,所以它们一直是绿的。
//    (改在模型里、断言钉住了、文档写着已修,就是没接到视图上。)
//
//  ⇒ 现在的口径**只有三种,没有一种会显示别的机器的数字**:
//    · 主机数据新鲜                → Source=Hub,标题「主机显存」
//    · 连不上主机 / 没配对         → Source=HostUnreachable,标题「主机未连接」
//    · 连上了但主机自己没采到      → Source=HostNoReading,标题「主机显存读不到」
//  ★ 第三种**不能**说成"未连接" —— 它连着,坏的是主机上的采样器。
//    说成未连接会把人支去查网络,而问题在显卡那头。两者的下一步完全不同。
//  ★ 本机那张卡**一个字都不显示**了:它不是"差一点的同类数据",它是另一台机器。
// ══════════════════════════════════════════════════════════════════════
//
// 分段口径(用户裁定的三段)与本项目既有预算口径(config/vram-budget.toml)对齐:
//   · 启用的模型 max 占用 = 中枢 committed 集合的 peak 之和(中枢下发,客户端不自己算)。
//   · 当前桌面占用 = 实测 used 减去模型占用。★ 中枢来源下这是**推算**值
//     (WDDM 不暴露逐进程显存,说不出占用者名字),界面须如实标注。
//   · 未占用 = total - 上面两者。

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LocalAI.Client.Services;

/// <summary>
/// 这条显存数据【测的是哪台机器】。★ 它不是显示细节,是判读前提:
/// 同一个 "8.3 GiB" 在两台机器上意思完全不同,而数字本身分辨不出来。
/// </summary>
public enum VramSource
{
    /// <summary>主机(中枢)的显卡 —— 这才是 AI 用的那块,预算口径也是它。**唯一会显示数字的来源**。</summary>
    Hub,
    /// <summary>连不上主机(没配对 / 主机没开 / lan-edge 没起)。显示「主机未连接」,不显示任何数字。</summary>
    HostUnreachable,
    /// <summary>★ 连着主机,但主机自己这一轮没读到显存。**不是**未连接 —— 下一步该查主机的显卡,不是查网络。</summary>
    HostNoReading,
}

public sealed record VramSnapshot(
    double TotalGiB,
    double ModelReservedGiB,   // 启用的模型 max 占用(浅蓝)
    double DesktopUsedGiB,     // 当前桌面/其它占用(蓝)
    bool Available,            // 拿到主机数字了没有。★ 与 Source 是同一件事,见 HasNumbers
    string? Note = null,
    // ★ 默认值取 HostUnreachable(**不是** Hub):加一个新构造点、忘了传来源时,
    //   它落在"没有主机数据"这边 —— 落错边的代价是拿一份来路不明的数字当主机的。
    VramSource Source = VramSource.HostUnreachable,
    bool DesktopIsInferred = false,
    string? HubState = null)
{
    /// <summary>
    /// 标题栏那一行。★★ 这个属性**必须被界面读**(VramBar.Update 里)——
    /// 它曾经存在但没人渲染,于是"改标题"这个修复整整没有生效过。
    /// </summary>
    public string Title => Source switch
    {
        VramSource.Hub => "主机显存",
        VramSource.HostUnreachable => "主机未连接",
        VramSource.HostNoReading => "主机显存读不到",
        _ => "主机显存",
    };

    /// <summary>有没有可显示的数字。★ 只有主机数据新鲜时才有 —— 别的一律没有。</summary>
    public bool HasNumbers => Source == VramSource.Hub && Available && TotalGiB > 0;

    public double FreeGiB => Math.Max(0, TotalGiB - ModelReservedGiB - DesktopUsedGiB);
    /// <summary>实际占用比例(模型 + 桌面)。逼近 1 时界面转红。</summary>
    public double UsedRatio => TotalGiB <= 0 ? 0 : Math.Clamp((ModelReservedGiB + DesktopUsedGiB) / TotalGiB, 0, 1);
}

public sealed class VramMonitor : IDisposable
{
    /// <summary>轮询间隔。2 秒 = 跟得上模型装卸、又几乎不耗电(见文件头取舍)。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    /// <summary>占用超过这个比例就转红色系(逼近显存上限)。</summary>
    public const double DangerRatio = 0.90;
    public const double WarnRatio = 0.78;

    public event Action<VramSnapshot>? Updated;

    readonly System.Threading.Timer _timer;
    bool _paused;
    // ★ _totalGiB / _nvmlOk / _triedInit / _smiDead 已随本机读取路径一并删除(2026-08-05)。
    //   留着它们就是四个"从不使用的字段" —— 编译器会警告,而本项目把死代码当缺陷。
    //   ★ 其中 _totalGiB **在删之前就已经是死的**:只写不读,没有任何人用它。

    public VramSnapshot Last { get; private set; } =
        new(0, 0, 0, false, "还没有接上主机", VramSource.HostUnreachable);

    public VramMonitor()
    {
        // ★★ 起初**不采样**:VramMonitor 是 App 的字段初始化器,构造发生在 OnStartup 之前,
        //   而 Hub 要到 OnStartup 里才接上。dueTime 若是 Zero,第一帧必然在 Hub 还是 null 时跑,
        //   于是开机瞬间会闪一下「主机未连接」—— 那是一句**转瞬即逝的假话**,
        //   而转瞬即逝的假话最难查(用户看见了,你复现不了)。
        //   ⇒ 由 Hub 的 setter 启动这张表,见下面 Hub 属性。
        _timer = new System.Threading.Timer(_ => Tick(), null,
                                            System.Threading.Timeout.InfiniteTimeSpan, Interval);
    }

    /// <summary>窗口不可见时暂停 —— 省电的关键远大于调长间隔。</summary>
    public void Pause() => _paused = true;
    public void Resume() { _paused = false; Tick(); }

    void Tick()
    {
        if (_paused) return;
        try
        {
            var snap = Read();
            Last = snap;
            Updated?.Invoke(snap);
        }
        catch { /* 采样失败不该影响界面;下一次再试 */ }
    }

    /// <summary>
    /// 主机(中枢)状态的来源。★ 由宿主在启动时注入 —— 不在这里 new 一个,
    /// 否则会出现两条各自订阅的推送流,而"哪一条是权威"就没有答案了。
    /// ★★ 接上的那一刻才开始采样(见构造函数):在此之前采样只会得到"没有主机",
    ///   而那不是事实,只是还没接线。
    /// </summary>
    public HubGpu? Hub
    {
        get => _hub;
        set
        {
            _hub = value;
            if (value is null) return;
            // 主机那边一有变化就立刻重画(不必等下一个 2 秒),同时把周期表也起起来 ——
            // ★ 周期表不能省:「40 秒一帧都没来」是**没有事件**的那一种失败,
            //   纯事件驱动的话它永远不会被画出来。
            value.Changed += Tick;
            _timer.Change(TimeSpan.Zero, Interval);
        }
    }
    HubGpu? _hub;

    VramSnapshot Read()
    {
        // ── 只有一个来源:主机。拿不到就说拿不到,**绝不换一台机器的数字顶上** ──
        var hub = Hub;
        if (hub is null || !hub.HasFreshData || hub.Snapshot is not { } hs)
        {
            // ★ 标题统一是「主机未连接」(用户要的就是这一句),但**说明里分清是哪一种** ——
            //   没配对 / 主机没开 / 被拒,这三种的下一步完全不同。
            var why = hub?.Link switch
            {
                null => "还没有接上主机",
                HubGpuLink.NeverConnected => "还没有连上主机 —— 主机没开机?或主机上的 lan-edge 没起?",
                HubGpuLink.Reconnecting => "正在重连主机…",
                HubGpuLink.Refused => "主机拒绝了这台设备(证书被吊销了?)",
                _ => hub?.LastError ?? "连不上主机",
            };
            return new VramSnapshot(0, 0, 0, false, why, VramSource.HostUnreachable);
        }
        {
            // ★ 主机连着,但它报的总显存是 0 ⇒ 主机那台机器上读不到显卡。
            //   这**不是**"未连接" —— 网络是通的,该去查的是主机的驱动。
            if (hs.TotalGiB <= 0)
                return new VramSnapshot(0, 0, 0, false,
                                        hs.SamplerError ?? "主机上读不到显卡(驱动没装?)",
                                        VramSource.HostNoReading, true, hs.State);

            // ★ 模型段来自主机的 committed 集合,客户端不自己算 peak(数字只有一份权威)。
            // ★★ 必须收 unknown:认不出的组件 id 会被跳过,而跳过等于**把它当成 0 GiB** ——
            //   客户端装在仓库外时读不到 vram-budget.toml,整张 peak 表是空的,
            //   于是模型段静默算成 0,而条子照常显示"一切正常"。
            //   (ComponentPicker 早就在收这个了,只有显存条这一处漏了。)
            var unknown = new List<string>();
            var modelHub = VramBudget.PeakSumGiB(hs.Committed, unknown);
            _unknownComponents = unknown;
            // ★ 桌面段是**推算**的:total - free - 模型。WDDM 不暴露逐进程显存,
            //   说不出占用者是谁 —— DesktopIsInferred=true 让界面必须如实标注。
            var usedHub = hs.FreeGiB is { } f ? Math.Max(0, hs.TotalGiB - f) : (double?)null;
            if (usedHub is { } u)
                return new VramSnapshot(hs.TotalGiB, modelHub, Math.Max(0, u - modelHub), true,
                                        // ★ 认不出组件时**说出来**:模型段这时是偏低的,
                                        //   不说的话界面会显示"还很空",而实际可能已经满了。
                                        unknown.Count > 0
                                            ? $"有 {unknown.Count} 个组件认不出({string.Join("、", unknown.Take(3))}),模型段可能偏低"
                                            : hs.SamplerError,
                                        VramSource.Hub, true, hs.State);
            // 主机连着,但它自己这一轮没采到 NVML ⇒ 如实说是**主机读不到**。
            // ★ 这一种**不能**说成"未连接":它连着,坏的是主机上的采样器。
            //   说成未连接会把人支去查网络,而问题在显卡那头 —— 下一步完全不同。
            return new VramSnapshot(hs.TotalGiB, modelHub, 0, false,
                                    hs.SamplerError ?? "主机这一轮没读到显存",
                                    VramSource.HostNoReading, true, hs.State);
        }
    }

    // ★★ 本机 NVML / nvidia-smi 的读取路径**已整条删除**(2026-08-05)。
    //   它唯一的用途是"拿不到主机数据时退回显示本机这张卡",而用户裁定:
    //   显存永远显示主机的,拿不到就说主机未连接。
    //   ⇒ 留着那段代码就是留着一条随时会被接回去的错误路径,而它没有任何调用点 ——
    //     本项目把"定义了却没有调用点"当缺陷。删干净比留着注释掉更诚实。

    /// <summary>上一轮认不出的组件 id。★ 只用于诊断,界面已在 Note 里说过了。</summary>
    List<string> _unknownComponents = new();

    public void Dispose()
    {
        if (_hub is not null) _hub.Changed -= Tick;   // ★ 订了就要退,否则 Dispose 之后还在被回调
        _timer.Dispose();
    }
}
