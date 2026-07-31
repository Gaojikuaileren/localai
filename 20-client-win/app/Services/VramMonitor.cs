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
// 分段口径(用户裁定的三段)与本项目既有预算口径(config/vram-budget.toml)对齐:
//   · 启用的模型 max 占用 = 已启用组件的 peak 之和(实测值,唯一数据源是那个 toml)。
//     组件选择器要等 P4,所以现在恒为 0 —— 如实显示,不编造。
//   · 当前桌面占用 = NVML 实测 used 减去模型实际占用。P4 之前没有 broker 归因,
//     而此刻也确实没有经 broker 装载的模型,所以 used 即桌面/其它应用占用 —— 今天这是准确的。
//   · 未占用 = total - 上面两者。

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LocalAI.Client.Services;

public sealed record VramSnapshot(
    double TotalGiB,
    double ModelReservedGiB,   // 启用的模型 max 占用(浅蓝)
    double DesktopUsedGiB,     // 当前桌面/其它占用(蓝)
    bool Available,            // 读不到 GPU 时为 false -> 界面隐藏该条,不显示 0 冒充
    string? Note = null)
{
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

    VramSnapshot Read()
    {
        if (!_triedInit) { _triedInit = true; _nvmlOk = TryInitNvml(); }

        double usedGiB, totalGiB;
        if (_nvmlOk && TryReadNvml(out usedGiB, out totalGiB)) { /* ok */ }
        else if (_smiDead || !TryReadSmi(out usedGiB, out totalGiB))
        {
            _smiDead = true;   // ★ 一次读不到就死心(无 N 卡机器不该每次 Tick 都去 Process.Start)
            return new VramSnapshot(0, 0, 0, false, "读不到 GPU 显存(无 NVIDIA 驱动或不可用)");
        }

        _totalGiB = totalGiB;

        // 启用组件的 peak 之和。组件选择器是 P4;在那之前没有"已启用模型" -> 0(如实,不编造)。
        var modelReserved = VramBudget.EnabledModelsPeakGiB();

        // 归因:模型实际占用要等 broker(P4)。今天没有经 broker 装载的模型,故 used 即桌面/其它。
        var desktop = Math.Max(0, usedGiB - modelReserved);

        return new VramSnapshot(totalGiB, modelReserved, desktop, true);
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
