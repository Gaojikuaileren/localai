// P3c -- Apple 日历的【自动拉取】(用户要求 2026-07-31)。
//
// ★★ 这个文件最重要的东西不是定时器,是【熔断】:
//   iCloud 按【用户名】节流,反复认证失败会从 401 升级成 403,再继续会把用户
//   【真实的 Apple ID 锁掉】(得去 iforgot.apple.com 重置)。
//   而"自动"正是最危险的形态 —— 手动失败用户会停下来看,自动失败没人看着,
//   它能安安静静撞上一整夜。
//
//   所以规则写死:一旦某次拉取是【认证失败】,立刻把自动拉取【关掉】并记下原因,
//   等用户去重新填密码。绝不退避重试、绝不"过一会儿再试试" —— 那都是在赌用户的账号。
//
// 其余失败(网络不通、超时、某个日历取不到)不熔断:那些重试无害,下个周期自然会好。

using System.Windows.Threading;

namespace LocalAI.Client.Services;

public static class AppleAutoSync
{
    static readonly DispatcherTimer Timer = new();
    static Func<AppSettings>? _settings;
    static Func<string>? _owner;

    /// <summary>自动拉取被熔断的原因(null = 没熔断)。界面据此如实显示为什么停了。</summary>
    public static string? TrippedReason { get; private set; }

    /// <summary>状态变了(跑完一次 / 被熔断)—— 界面据此刷新。</summary>
    public static event Action? Changed;

    /// <summary>最近一次自动拉取的结果说明(给界面显示)。</summary>
    public static string? LastMessage { get; private set; }

    /// <summary>
    /// 装上定时器。★ 只在 App 启动时调一次。
    /// 间隔取自设置;不到点不发请求,窗口开着与否都照跑(它是后台同步,不是界面刷新)。
    /// </summary>
    public static void Install(Func<AppSettings> settings, Func<string> owner)
    {
        _settings = settings;
        _owner = owner;
        Timer.Tick += async (_, _) => await TickAsync();
        Apply();
    }

    /// <summary>按当前设置重新装表(开/关、改间隔后调用)。</summary>
    public static void Apply()
    {
        if (_settings is null) return;
        var s = _settings();
        Timer.Stop();
        if (!s.AppleAutoPull || TrippedReason is not null) { Changed?.Invoke(); return; }

        // ★ 下限 15 分钟:日历不是秒级数据,拉太勤只是白白骚扰 Apple、也更容易撞上节流。
        var mins = Math.Max(15, s.AppleAutoPullMinutes);
        Timer.Interval = TimeSpan.FromMinutes(mins);
        Timer.Start();
        Changed?.Invoke();
    }

    /// <summary>用户重新填过密码之后:清掉熔断,允许再自动跑。</summary>
    public static void ResetTrip()
    {
        TrippedReason = null;
        Apply();
    }

    /// <summary>把熔断状态也一并清掉(断开连接时用)。</summary>
    public static void Stop()
    {
        Timer.Stop();
        TrippedReason = null;
        LastMessage = null;
        Changed?.Invoke();
    }

    static async Task TickAsync()
    {
        if (_settings is null || _owner is null) return;
        var s = _settings();
        if (!s.AppleAutoPull || TrippedReason is not null) { Timer.Stop(); return; }
        if (AppleCalendarSync.Busy) return;                 // 手动同步正在跑 -> 这一轮跳过
        if (s.AppleCalendarUrls.Count == 0) return;         // 没选日历 -> 无事可做,也不用报错

        var r = await AppleCalendarSync.PullAsync(s, _owner(), "家庭");
        LastMessage = $"{DateTime.Now:HH:mm} 自动拉取:{r.Message}";

        // ★★ 认证失败 = 立刻熔断。见文件头:自动重试会把用户的 Apple ID 打进锁定。
        if (r.AuthFailed)
        {
            TrippedReason = "上次自动拉取时 Apple 拒绝了认证,已【自动停止】以免账号被锁。"
                          + "请重新填写专用密码后再打开。";
            s.AppleAutoPull = false;
            s.Save();
            Timer.Stop();
        }
        Changed?.Invoke();
    }
}
