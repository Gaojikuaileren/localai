// P3c -- 浮层协调器。
//
// 重构缘由:此前有【两套独立的浮层系统】—— 外壳的抽屉(右上/底部)与 Flyout 浮窗 ——
// 规则各写一套且互不知情,导致:
//   · Esc 只能关抽屉、关不掉浮窗;
//   · 抽屉里打开的浮窗,在抽屉关闭后可能变成孤儿悬在屏幕上;
//   · "同时只开一个"分别成立,合起来却不成立(抽屉 + 浮窗可以同时在);
//   · 每加一个入口都要把"点开/再点关/被别人关"的规则重抄一遍,漏一处就是个 bug。
//
// 现在收敛成一条规则,所有浮层(抽屉与浮窗)共用:
//   ① 同一时刻【最多一个】浮层;
//   ② 打开新浮层前先关掉旧的;
//   ③ 浮层开着时,【任何触发按钮的第一次点击只负责关闭它】(用户裁定),不打开新的;
//   ④ Esc / 点浮层之外,一律关闭。
//
// 用法:浮层实现方在打开时调用 Register(closer),入口按钮在打开前先调用 ConsumeClick()。

namespace LocalAI.Client.Views;

public static class Overlay
{
    static Action? _close;

    /// <summary>当前是否有浮层(抽屉或浮窗)开着。</summary>
    public static bool IsOpen => _close is not null;

    /// <summary>
    /// 登记一个已打开的浮层及其关闭方法。会先关掉上一个 —— 保证全局只有一个。
    /// </summary>
    public static void Register(Action close)
    {
        var previous = _close;
        _close = null;          // 先清空,避免 previous() 内部回调再次触发关闭造成递归
        previous?.Invoke();
        _close = close;
    }

    /// <summary>浮层自己关闭时(例如 WPF Popup 因点击外部而关)回调,用于清账。</summary>
    public static void Unregister(Action close)
    {
        // ★ 用 Equals 而非 ReferenceEquals:每次 `Register(Foo)` 都会新建一个委托实例,
        //   引用比较必然失败(实测:清账不生效,留下失效的关闭回调,后续点击被误消费)。
        //   委托的 Equals 按"目标 + 方法"比较,正是我们要的语义。
        if (Equals(_close, close)) _close = null;
    }

    /// <summary>关闭当前浮层(若有)。</summary>
    public static void CloseActive()
    {
        var c = _close;
        _close = null;
        c?.Invoke();
    }

    /// <summary>
    /// 入口按钮在打开自己的浮层【之前】调用:
    /// 若已有浮层开着,这一次点击只负责关掉它并返回 true(调用方应直接 return)。
    /// </summary>
    public static bool ConsumeClick()
    {
        if (!IsOpen) return false;
        CloseActive();
        return true;
    }
}
