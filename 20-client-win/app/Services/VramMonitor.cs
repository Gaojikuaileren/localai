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
//  ⇒ 新口径:
//    · 有中枢数据(HubGpu 的 SSE 推送,新鲜)      → Source=Hub,标题「中枢显存」
//    · 拿不到中枢数据但本机有 N 卡                 → Source=LocalFallback,
//      标题明写「本机显卡(不是中枢的)」—— **绝不**用本机数字冒充中枢数字
//    · 两边都没有                                   → Source=None,整条隐藏
//  ★ 退回本机不是"降级到差一点的同类数据",而是【换了一个被测对象】。
//    所以它不能只是精度差一点,必须在界面上改名字。
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
    /// <summary>中枢的显卡 —— 这才是 AI 用的那块,预算口径也是它。</summary>
    Hub,
    /// <summary>★ 本机显卡。只在拿不到中枢数据时使用,且**必须**在界面上写明。</summary>
    LocalFallback,
    /// <summary>两边都没有。整条隐藏,不用 0 冒充"很空闲"。</summary>
    None,
}

public sealed record VramSnapshot(
    double TotalGiB,
    double ModelReservedGiB,   // 启用的模型 max 占用(浅蓝)
    double DesktopUsedGiB,     // 当前桌面/其它占用(蓝)
    bool Available,            // 读不到 GPU 时为 false -> 界面隐藏该条,不显示 0 冒充
    string? Note = null,
    VramSource Source = VramSource.None,
    bool DesktopIsInferred = false,
    string? HubState = null)
{
    /// <summary>标题栏那一行。★ 本机回退时**必须**带上"不是中枢的",否则就是一次静默换源。</summary>
    public string Title => Source switch
    {
        VramSource.Hub => "中枢显存",
        VramSource.LocalFallback => "本机显卡(不是中枢的)",
        _ => "显存",
    };

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
    double _totalGiB;
    bool _nvmlOk;
    bool _triedInit;
    bool _smiDead;   // ★ nvidia-smi 也读不到 -> 不再重试(与 _triedInit 对称;否则无 N 卡机器每 2 秒起一次进程)

    public VramSnapshot Last { get; private set; } = new(0, 0, 0, false, "尚未读取");

    public VramMonitor()
    {
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.Zero, Interval);
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
    /// 中枢状态的来源。★ 由宿主在启动时注入 —— 不在这里 new 一个,
    /// 否则会出现两条各自订阅的推送流,而"哪一条是权威"就没有答案了。
    /// </summary>
    public HubGpu? Hub { get; set; }

    VramSnapshot Read()
    {
        // ── ① 首选:中枢。这才是 AI 真正用的那块显卡,预算口径也是它 ──
        var hub = Hub;
        if (hub is not null && hub.HasFreshData && hub.Snapshot is { } hs && hs.TotalGiB > 0)
        {
            // ★ 模型段来自中枢的 committed 集合,客户端不自己算 peak(数字只有一份权威)。
            var modelHub = VramBudget.PeakSumGiB(hs.Committed);
            // ★ 桌面段是**推算**的:total - free - 模型。WDDM 不暴露逐进程显存,
            //   说不出占用者是谁 —— DesktopIsInferred=true 让界面必须如实标注。
            var usedHub = hs.FreeGiB is { } f ? Math.Max(0, hs.TotalGiB - f) : (double?)null;
            if (usedHub is { } u)
                return new VramSnapshot(hs.TotalGiB, modelHub, Math.Max(0, u - modelHub), true,
                                        hs.SamplerError, VramSource.Hub, true, hs.State);
            // 中枢连着,但它自己这一轮没采到 NVML ⇒ 如实说"中枢读不到",
            // ★ 不退回本机:那会把"中枢的采样器坏了"显示成"一切正常",两种情况必须长得不一样。
            return new VramSnapshot(hs.TotalGiB, modelHub, 0, false,
                                    hs.SamplerError ?? "中枢这一轮没读到显存",
                                    VramSource.Hub, true, hs.State);
        }

        // ── ② 回退:本机显卡。★ 这是**换了被测对象**,不是精度差一点 ──
        //   所以 Source=LocalFallback,标题会变成「本机显卡(不是中枢的)」。
        if (!_triedInit) { _triedInit = true; _nvmlOk = TryInitNvml(); }

        double usedGiB, totalGiB;
        if (_nvmlOk && TryReadNvml(out usedGiB, out totalGiB)) { /* ok */ }
        else if (_smiDead || !TryReadSmi(out usedGiB, out totalGiB))
        {
            _smiDead = true;   // ★ 一次读不到就死心(无 N 卡机器不该每次 Tick 都去 Process.Start)
            return new VramSnapshot(0, 0, 0, false, "读不到 GPU 显存(无 NVIDIA 驱动或不可用)",
                                    VramSource.None);
        }

        _totalGiB = totalGiB;

        // ★ 本机回退路径下模型段恒为 0,而且这是真话:经中枢装载的模型不在这台机器上,
        //   本机这块卡上的占用全部来自本机的桌面程序。
        return new VramSnapshot(totalGiB, 0, usedGiB, true,
                                "拿不到中枢数据,这里显示的是本机这台机器的显卡",
                                VramSource.LocalFallback);
    }

    // ---------------------------------------------------------------- NVML
    const string Nvml = "nvml.dll";
    [DllImport(Nvml, EntryPoint = "nvmlInit_v2")] static extern int NvmlInit();
    [DllImport(Nvml, EntryPoint = "nvmlShutdown")] static extern int NvmlShutdown();
    [DllImport(Nvml, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] static extern int NvmlGetHandle(uint index, out IntPtr device);
    [DllImport(Nvml, EntryPoint = "nvmlDeviceGetMemoryInfo")] static extern int NvmlGetMemory(IntPtr device, out NvmlMemory mem);

    [StructLayout(LayoutKind.Sequential)]
    struct NvmlMemory { public ulong Total; public ulong Free; public ulong Used; }

    static bool TryInitNvml() { try { return NvmlInit() == 0; } catch { return false; } }

    static bool TryReadNvml(out double usedGiB, out double totalGiB)
    {
        usedGiB = totalGiB = 0;
        try
        {
            if (NvmlGetHandle(0, out var dev) != 0) return false;
            if (NvmlGetMemory(dev, out var m) != 0) return false;
            const double G = 1024.0 * 1024 * 1024;
            usedGiB = m.Used / G;
            totalGiB = m.Total / G;
            return totalGiB > 0;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- 降级:nvidia-smi
    // 只在 NVML 不可用时才走,且同样 2 秒一次 —— 开进程较贵,但总比没有强。
    static bool TryReadSmi(out double usedGiB, out double totalGiB)
    {
        usedGiB = totalGiB = 0;
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi",
                "--query-gpu=memory.used,memory.total --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return false;
            var line = p.StandardOutput.ReadLine();
            p.WaitForExit(2000);
            if (line is null) return false;
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) return false;
            usedGiB = double.Parse(parts[0]) / 1024.0;    // MiB -> GiB
            totalGiB = double.Parse(parts[1]) / 1024.0;
            return totalGiB > 0;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _timer.Dispose();
        try { if (_nvmlOk) NvmlShutdown(); } catch { }
    }
}
