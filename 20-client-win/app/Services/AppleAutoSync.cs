// P3c -- Apple 的【自动拉取】(日历 + 提醒事项)。用户要求 2026-07-31。
//
// ★★ 这个文件最重要的东西不是定时器,是【什么时候不要跑】。三道闸,由重到轻:
//
//  ① 认证失败 -> 硬熔断,直接把开关关掉。
//     iCloud 按【用户名】节流,反复认证失败会把用户【真实的 Apple ID 锁掉】
//     (得去 iforgot.apple.com 重置)。而"自动"正是最危险的形态 —— 手动失败用户会停下来看,
//     自动失败没人看着,它能安安静静撞一整夜。所以一次就停,等用户重填密码。
//
//  ② 没网 -> 连试都不试。
//     断网时每 30 分钟发一次注定失败的请求,除了耗电和刷失败记录没有任何用。
//     网络恢复时系统会通知我们(NetworkChange),那时再自然继续 —— 不需要靠"定时重试"去发现。
//
//  ③ 连续多次连接失败(网在、但连不上 Apple)-> 软暂停。
//     Apple 侧故障/被限流/DNS 出问题时,继续按固定节奏敲没有意义。
//     暂停后由【网络变化】或【用户手动同步成功】来解除 —— 不自己偷偷重启。
//
// 一句话:只有在【有网 + 认证有效 + 最近能连上】时,自动拉取才是活的。

using System.Net.NetworkInformation;
using System.Windows.Threading;

namespace LocalAI.Client.Services;

public static class AppleAutoSync
{
    static readonly DispatcherTimer Timer = new();
    static Func<AppSettings>? _settings;
    static Func<string>? _owner;

    /// <summary>连续连接失败次数(非认证类)。到阈值就软暂停,别再空转。</summary>
    static int _consecutiveFailures;
    const int SuspendAfter = 3;

    /// <summary>自动拉取被【硬熔断】的原因(认证失败)。null = 没熔断。</summary>
    public static string? TrippedReason { get; private set; }

    /// <summary>被【软暂停】的原因(没网 / 连不上)。会自动恢复,不需要用户操作。</summary>
    public static string? SuspendedReason { get; private set; }

    /// <summary>状态变了 —— 界面据此刷新。</summary>
    public static event Action? Changed;

    /// <summary>最近一次自动拉取的结果说明。</summary>
    public static string? LastMessage { get; private set; }

    /// <summary>现在有没有网(只做本机链路判断,不发请求)。</summary>
    public static bool NetworkUp => NetworkInterface.GetIsNetworkAvailable();

    public static void Install(Func<AppSettings> settings, Func<string> owner)
    {
        _settings = settings;
        _owner = owner;
        Timer.Tick += async (_, _) => await TickAsync();

        // ★ 网络恢复时【自然继续】—— 而不是靠定时重试去"发现"网络回来了。
        NetworkChange.NetworkAvailabilityChanged += (_, e) =>
        {
            if (!e.IsAvailable) return;
            if (SuspendedReason is null) return;
            SuspendedReason = null;
            _consecutiveFailures = 0;
            Apply();
        };
        Apply();
    }

    /// <summary>按当前设置重新装表(开/关、改间隔、解除暂停后调用)。</summary>
    public static void Apply()
    {
        if (_settings is null) return;
        var s = _settings();
        Timer.Stop();

        // 硬熔断 / 关着 / 软暂停 -> 表不跑
        if (!s.AppleAutoPull || TrippedReason is not null || SuspendedReason is not null)
        {
            Changed?.Invoke();
            return;
        }

        // ★ 下限 15 分钟:日历不是秒级数据,拉太勤只是白白骚扰 Apple、也更容易撞上节流。
        var mins = Math.Max(15, s.AppleAutoPullMinutes);
        Timer.Interval = TimeSpan.FromMinutes(mins);
        Timer.Start();
        Changed?.Invoke();
    }

    /// <summary>用户重新填过密码之后:清掉熔断与暂停,允许再自动跑。</summary>
    public static void ResetTrip()
    {
        TrippedReason = null;
        SuspendedReason = null;
        _consecutiveFailures = 0;
        Apply();
    }

    /// <summary>断开连接:停表并清掉所有状态。</summary>
    public static void Stop()
    {
        Timer.Stop();
        TrippedReason = null;
        SuspendedReason = null;
        LastMessage = null;
        _consecutiveFailures = 0;
        Changed?.Invoke();
    }

    /// <summary>手动同步成功时调用 —— 说明链路是通的,解除软暂停。</summary>
    public static void NoteManualSuccess()
    {
        _consecutiveFailures = 0;
        if (SuspendedReason is null) return;
        SuspendedReason = null;
        Apply();
    }

    static async Task TickAsync()
    {
        if (_settings is null || _owner is null) return;
        var s = _settings();
        if (!s.AppleAutoPull || TrippedReason is not null || SuspendedReason is not null) { Timer.Stop(); return; }
        if (AppleCalendarSync.Busy) return;                 // 手动同步正在跑 -> 这一轮跳过

        // 没选任何东西 -> 无事可做(不算失败,也不报错)
        if (s.AppleCalendarUrls.Count == 0) return;

        // ★ ② 没网就【连试都不试】—— 断网时发注定失败的请求毫无意义
        if (!NetworkUp)
        {
            SuspendedReason = "当前没有网络,自动拉取已暂停 —— 联网后会自动继续。";
            Timer.Stop();
            Changed?.Invoke();
            return;
        }

        var r = await AppleCalendarSync.PullAsync(s, _owner(), "家庭");
        LastMessage = $"{DateTime.Now:HH:mm} 自动拉取:{r.Message}";

        // ★ ① 认证失败 = 硬熔断(见文件头:自动重试会把用户的 Apple ID 打进锁定)
        if (r.AuthFailed)
        {
            TrippedReason = "上次自动拉取时 Apple 拒绝了认证,已【自动停止】以免账号被锁。"
                          + "请重新填写专用密码后再打开。";
            s.AppleAutoPull = false;
            s.Save();
            Timer.Stop();
            Changed?.Invoke();
            return;
        }

        // ★ ③ 连续连接失败 -> 软暂停,别再按固定节奏空转
        if (!r.Ok)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= SuspendAfter)
            {
                SuspendedReason = $"连续 {_consecutiveFailures} 次没能连上 Apple,自动拉取已暂停 —— "
                                + "网络恢复、或你手动同步成功一次之后会继续。";
                Timer.Stop();
            }
        }
        else _consecutiveFailures = 0;

        Changed?.Invoke();
    }
}
