// P3c -- 键盘焦点纪律(用户裁定 2026-07-30,第二轮收紧)。
//
// 规则(最终版):
//   · Tab 是一个【二态开关】:聚焦当前页面的 AI 交流输入框 ⇄ 什么都不聚焦。
//     当前页面没有那个输入框 -> 恒为"什么都不聚焦"。
//     ★ 不再在多个输入框之间循环 —— 用户裁定"目前只有输入框才有聚焦需求"。
//       代价明说:笔记/日程/待办编辑器里的多个输入格之间只能用鼠标点,Tab 不再串场。
//   · 回车只触发【发送】,不触发任何别的按钮。
//
// ★★ 为什么不靠"给每种控件设 IsTabStop=False":那是打地鼠,而且注定漏。
//   WPF 里 Control 的 Focusable 默认【就是 true】—— 于是 ContentControl 这种纯粹的
//   "板块容器"(会话区宿主、抽屉宿主、导航宿主…)天生就是 Tab 停靠点,而它们根本不是控件,
//   只是个装东西的框。用户实测:按钮和复选框都关掉之后,Tab 仍然停在这些板块上。
//   把每个可能的 Control 子类都列一遍 = 今天列全了,明天新加一个 ListBox 又漏。
//   所以改成【正面执行】:自己接管 Tab,只认一个落点。
//
// 怎么认出"那个输入框":用附加属性显式标记(IsChatInput),不靠猜类型、也不靠"树里第一个 TextBox"。
//   界面是代码建的、输入框每次重建都是新对象,标记跟着控件走最可靠。
//
// ★ Tab 可以在窗口的隧道事件里拦,方向键【不可以】:
//   TextBox 的 AcceptsTab 是 false,Tab 在输入框里本来就只用于导航,拦掉不损失任何东西;
//   而方向键在输入框里是移光标/跨行的唯一手段,隧道层一拦就把输入框废了(见 MainWindow 注释)。

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LocalAI.Client.Views;

public static class FocusPolicy
{
    /// <summary>标记"这是当前页面的 AI 交流输入框" —— Tab 唯一认的落点。</summary>
    public static readonly DependencyProperty IsChatInputProperty =
        DependencyProperty.RegisterAttached("IsChatInput", typeof(bool), typeof(FocusPolicy),
            new PropertyMetadata(false));

    public static void SetIsChatInput(DependencyObject el, bool value) => el.SetValue(IsChatInputProperty, value);
    public static bool GetIsChatInput(DependencyObject el) => (bool)el.GetValue(IsChatInputProperty);

    /// <summary>
    /// 在子树里找那个被标记的输入框。找不到 = 当前页面没有输入框。
    /// 不可见/禁用的分支整枝跳过 —— 藏起来的输入框不该被 Tab 到。
    /// </summary>
    public static FrameworkElement? FindChatInput(DependencyObject? scope)
    {
        if (scope is null) return null;
        // ★ 用【声明的】Visibility 而不是 IsVisible:IsVisible 还要求元素真的连到窗口并渲染出来,
        //   离屏的树上它恒为 false —— 那样整棵树会被当场剪掉(自检里就是这么炸过一次)。
        if (scope is UIElement { Visibility: not Visibility.Visible }) return null;
        if (scope is UIElement { IsEnabled: false }) return null;
        if (scope is FrameworkElement fe && GetIsChatInput(fe)) return fe;

        var n = VisualTreeHelper.GetChildrenCount(scope);
        for (int i = 0; i < n; i++)
            if (FindChatInput(VisualTreeHelper.GetChild(scope, i)) is { } hit) return hit;
        return null;
    }

    /// <summary>
    /// 算一次 Tab 的结果:已经聚焦在那个输入框上 -> 取消聚焦(null);否则 -> 聚焦它。
    /// 输入框不存在 -> 恒为 null。抽成纯函数,好让无头自检直接验这个开关。
    /// </summary>
    public static FrameworkElement? Toggle(FrameworkElement? chatInput, object? focused)
        => chatInput is null || ReferenceEquals(chatInput, focused) ? null : chatInput;

    /// <summary>
    /// 处理一次 Tab:在【聚焦 AI 输入框】与【什么都不聚焦】之间切换,可以一直来回按。
    /// </summary>
    /// <param name="park">"什么都不聚焦"时把焦点停到哪(零尺寸的停车位元素)</param>
    public static void HandleTab(DependencyObject? scope, IInputElement? park)
    {
        var input = FindChatInput(scope);
        var target = Toggle(input, Keyboard.FocusedElement);
        if (target is not null) { target.Focus(); return; }
        Park(scope, park);
    }

    /// <summary>
    /// 真正地"什么都不聚焦"= 把焦点停到一个【确定的空元素】上。
    /// ★★ 不能只调 Keyboard.ClearFocus():WPF 会在下一次输入把键盘焦点还给焦点范围里
    ///   记着的那个元素(也就是输入框)。用户实测的两个症状都是它:
    ///   ① 输入框看着没选中却照样能打字、还能回车发出去;
    ///   ② 再按多少次 Tab 也回不到输入框 —— 因为焦点其实一直在输入框上,
    ///      开关每次都判定"已经在上面了",于是又去清一遍,来回都在同一边。
    ///   停到一个零尺寸、不参与 Tab 的元素上,状态才是确定且可判定的。
    /// </summary>
    public static void Park(DependencyObject? scope, IInputElement? park)
    {
        if (scope is not null) FocusManager.SetFocusedElement(scope, park);
        if (park is not null) Keyboard.Focus(park);
        else Keyboard.ClearFocus();
    }
}
