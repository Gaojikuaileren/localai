// P3c -- 下拉菜单的统一出口与"点外部只关菜单"的全局纪律。
//
// 用户裁定(2026-07-30):浮层(抽屉/浮窗)与菜单开着时,点背后的任何按钮都应该
//   【只关闭这个浮层/菜单】,不应该顺带把那个按钮也按了。
//
// ★ 为什么需要这个类,而不是各处自己判:
//   ContextMenu 是独立的 Popup 窗口,StaysOpen=false 时 WPF 会在鼠标【按下】那一刻关掉它,
//   而界面上的按钮多数挂在【松开】上 —— 于是"按下关菜单、松开点按钮",一次点击干了两件事。
//   靠每个按钮自己判会漏(按钮遍布主页/抽屉/会话列表,补不全),所以:
//     ① 所有菜单统一走 Show() 打开,由这里记录"开着 / 刚关掉";
//     ② 主窗口的 PreviewMouseDown 一次性拦掉这次点击(见 MainWindow 构造函数)。
//   浮窗与抽屉走 Overlay,那条路本来就在主窗口拦;两者合起来覆盖全部浮层。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace LocalAI.Client.Views;

public static class MenuHost
{
    static int _openCount;
    static DateTime _closedAt = DateTime.MinValue;

    /// <summary>当前有菜单开着。</summary>
    public static bool IsOpen => _openCount > 0;

    /// <summary>
    /// 这一次点击应当【只用于关菜单】:菜单还开着,或刚刚(300ms 内)被这一下关掉。
    /// 主窗口据此把事件吞掉;个别按钮也可再判一次做双保险。
    /// </summary>
    public static bool SwallowClick => IsOpen || (DateTime.Now - _closedAt).TotalMilliseconds < 300;

    /// <summary>统一打开菜单:登记开关状态后再弹。</summary>
    public static void Show(ContextMenu menu, FrameworkElement target, PlacementMode placement = PlacementMode.Bottom)
    {
        Track(menu);
        menu.PlacementTarget = target;
        menu.Placement = placement;
        menu.IsOpen = true;
    }

    /// <summary>只登记不弹(菜单由别处打开时用)。</summary>
    public static void Track(ContextMenu menu)
    {
        menu.Opened += OnOpened;
        menu.Closed += OnClosed;
    }

    static void OnOpened(object? sender, RoutedEventArgs e) => _openCount++;

    static void OnClosed(object? sender, RoutedEventArgs e)
    {
        if (_openCount > 0) _openCount--;
        _closedAt = DateTime.Now;
        if (sender is ContextMenu m) { m.Opened -= OnOpened; m.Closed -= OnClosed; }   // 菜单是一次性建的,别留悬挂订阅
    }
}
