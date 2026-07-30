// P3c -- 下拉菜单的统一出口与"点外部只关菜单"的全局纪律。
//
// 用户裁定(2026-07-30):浮层(抽屉/浮窗)与菜单开着时,点背后的任何按钮都应该
//   【只关闭这个浮层/菜单】,不应该顺带把那个按钮也按了。
//
// ★ 为什么需要这个类,而不是各处自己判:
//   ContextMenu 是独立的 Popup 窗口,StaysOpen=false 时 WPF 会在鼠标【按下】那一刻关掉它,
//   而界面上的按钮多数挂在【松开】上 —— 于是"按下关菜单、松开点按钮",一次点击干了两件事。
//   靠每个按钮自己判会漏(按钮遍布主页/抽屉/会话列表,补不全),所以:
//     ① 所有菜单统一走 Show() 打开,由这里记录状态;
//     ② 主窗口的 PreviewMouseDown 一次性拦掉这次点击(见 MainWindow 构造函数)。
//   浮窗与抽屉走 Overlay,那条路本来就在主窗口拦;两者合起来覆盖全部浮层。
//
// ★★ 2026-07-30 事故与改法:原先用一个【计数器】记"开着几个菜单",Opened 加一、Closed 减一。
//   只要有一次 Closed 没来,计数就永远回不到 0 —— 于是主窗口把【此后每一次】鼠标按下都吞掉,
//   整个界面点不动(用户实测:除了挂在 MouseLeftButtonUp 上的"+"附件键,其余全死)。
//   而 Closed 没来是真会发生的:菜单项被点中 -> 回调重建了界面(比如选完文件就重建输入区)
//   -> 菜单挂靠的按钮已从可视树上摘掉,弹窗成了孤儿,那次 Closed 就可能永远不来。
//   所以现在【不记数】,改成每次去问菜单自己"你还开着吗",并顺手把孤儿弹窗关掉:
//   状态是查出来的,不是攒出来的 —— 攒出来的状态一旦错一次就永远错下去。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace LocalAI.Client.Views;

public static class MenuHost
{
    // 弱引用:菜单是随界面建随界面弃的,这里不该把它们钉在内存里
    static readonly List<WeakReference<ContextMenu>> _tracked = new();
    static DateTime _closedAt = DateTime.MinValue;

    /// <summary>
    /// 当前真有菜单开着。★ 每次都【实地查验】而不是读计数:
    /// 顺手清掉已回收的、已关闭的,以及"还标着开、但挂靠按钮已经不在可视树上"的孤儿弹窗。
    /// </summary>
    public static bool IsOpen
    {
        get
        {
            var any = false;
            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                if (!_tracked[i].TryGetTarget(out var m)) { _tracked.RemoveAt(i); continue; }
                if (!m.IsOpen) { _tracked.RemoveAt(i); continue; }
                // 孤儿:菜单还标着开,但它挂靠的按钮已经被界面重建摘掉了。
                // 不能让它一直挡着全局点击 —— 就地关掉并清账。
                if (m.PlacementTarget is FrameworkElement fe && !fe.IsLoaded)
                {
                    try { m.IsOpen = false; } catch { /* 已经在关的路上就别管了 */ }
                    _tracked.RemoveAt(i);
                    continue;
                }
                any = true;
            }
            return any;
        }
    }

    /// <summary>
    /// 这一次点击应当【只用于关菜单】:菜单还开着,或刚刚(300ms 内)被这一下关掉。
    /// 主窗口据此把事件吞掉;个别按钮也可再判一次做双保险。
    /// </summary>
    public static bool SwallowClick => IsOpen || (DateTime.Now - _closedAt).TotalMilliseconds < 300;

    /// <summary>统一打开菜单:登记后再弹。</summary>
    public static void Show(ContextMenu menu, FrameworkElement target, PlacementMode placement = PlacementMode.Bottom)
    {
        Track(menu);
        menu.PlacementTarget = target;
        menu.Placement = placement;
        menu.IsOpen = true;
    }

    /// <summary>只登记不弹(菜单由别处打开时用)。同一个菜单重复登记只算一次。</summary>
    public static void Track(ContextMenu menu)
    {
        foreach (var w in _tracked)
            if (w.TryGetTarget(out var m) && ReferenceEquals(m, menu)) return;   // "+"这类复用同一个菜单实例的,别重复挂
        _tracked.Add(new WeakReference<ContextMenu>(menu));
        menu.Closed += OnClosed;
    }

    // Closed 只用来记"刚关掉"这个时刻(给 300ms 宽限用)。
    // ★ 它【不再承担记账】—— 正是因为它不保证会来。
    static void OnClosed(object? sender, RoutedEventArgs e) => _closedAt = DateTime.Now;

    /// <summary>兜底:强行清账并关掉所有还开着的菜单(Esc / 出异常后自救用)。</summary>
    public static void CloseAll()
    {
        foreach (var w in _tracked)
            if (w.TryGetTarget(out var m) && m.IsOpen)
                try { m.IsOpen = false; } catch { }
        _tracked.Clear();
        _closedAt = DateTime.MinValue;   // 别让宽限期把紧接着的那次点击也吞了
    }
}
