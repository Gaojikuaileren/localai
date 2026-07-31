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
    // ★★ 浮层是【一摞】而不是一个(2026-07-31 修 bug):
    //   在抽屉里点"年月选择"会把整个抽屉关掉 —— 因为浮窗登记时把上一个(抽屉)关了,
    //   而浮窗的锚点就在那个抽屉里,于是两个一起消失,看起来就是"点了就关了"。
    //   现在:抽屉之间仍然【互相替换】(Register),浮窗【叠在上面】(Push),
    //   关闭永远只关最上面那一个。
    static readonly List<Action> _stack = new();

    static Action? _close
    {
        get => _stack.Count > 0 ? _stack[^1] : null;
        set
        {
            _stack.Clear();
            if (value is not null) _stack.Add(value);
        }
    }

    /// <summary>当前是否有浮层(抽屉或浮窗)开着。</summary>
    public static bool IsOpen => _stack.Count > 0;

    /// <summary>
    /// 叠一层上去,【不关掉】下面那层 —— 浮窗从抽屉里弹出来时走这条。
    /// </summary>
    public static void Push(Action close) => _stack.Add(close);

    /// <summary>
    /// 登记一个已打开的浮层及其关闭方法。会先关掉上一个 —— 保证全局只有一个。
    /// </summary>
    public static void Register(Action close)
    {
        // 关掉【整摞】,再放这一个 —— 抽屉之间是互相替换的关系
        var previous = _stack.ToList();
        _stack.Clear();         // 先清空,避免 previous() 内部回调再次触发关闭造成递归
        for (int i = previous.Count - 1; i >= 0; i--) previous[i].Invoke();
        _stack.Add(close);
    }

    /// <summary>浮层自己关闭时(例如 WPF Popup 因点击外部而关)回调,用于清账。</summary>
    public static void Unregister(Action close)
    {
        // ★ 用 Equals 而非 ReferenceEquals:每次 `Register(Foo)` 都会新建一个委托实例,
        //   引用比较必然失败(实测:清账不生效,留下失效的关闭回调,后续点击被误消费)。
        //   委托的 Equals 按"目标 + 方法"比较,正是我们要的语义。
        // 从摞里摘掉这一层(不一定在最上面 —— 比如浮窗自己因为点了外部而关)
        for (int i = _stack.Count - 1; i >= 0; i--)
            if (Equals(_stack[i], close)) { _stack.RemoveAt(i); return; }
    }

    /// <summary>关闭当前浮层(若有)。</summary>
    /// <summary>关掉【最上面】那一层。抽屉里开着浮窗时,第一下只关浮窗。</summary>
    public static void CloseActive()
    {
        if (_stack.Count == 0) return;
        var c = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        c.Invoke();
    }

    /// <summary>关掉所有层(退出/切页时用)。</summary>
    public static void CloseAll()
    {
        var all = _stack.ToList();
        _stack.Clear();
        for (int i = all.Count - 1; i >= 0; i--) all[i].Invoke();
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
