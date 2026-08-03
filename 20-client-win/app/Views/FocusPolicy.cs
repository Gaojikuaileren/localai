// P3c -- 键盘焦点纪律(用户裁定 2026-07-30,第二轮收紧)。
//
// 规则(最终版):
//   · Tab 在【登记过的输入框】之间转圈;页面上只有一个(或一个都没有)时,
//     退化成原来那个【二态开关】:聚焦它 ⇄ 什么都不聚焦。
//     ★ 2026-08-03 用户裁定(回信功能):"tab 就可以在不同的输入框内切换聚焦"。
//       这修正了 07-30 那条"不再循环"的裁定 —— 当时的前提是"每页只有一个输入框",
//       而回信页一口气摆了十二个。两条裁定并不矛盾:圈里只有一个时,行为与旧版逐字相同。
//   · 点【输入框以外】的地方 = 取消聚焦(2026-08-03 裁定)。挂在窗口隧道层,见 MainWindow。
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

using System.Linq;
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
    /// 【Tab 圈】里的次序。★ 为什么不用 WPF 自带的 TabIndex:
    ///   本 App 已经把 KeyboardNavigation 整个关掉(MainWindow),Tab 又在隧道层被吞了 ——
    ///   TabIndex 永远没人读,留着就是一个死设定。
    /// ★★ 也不能靠【树序】:回信页把设置条(屏幕下方)先 Add、会话卡(屏幕上方)后 Add,
    ///   按树序走出来的 Tab 是自下而上的 —— 所以次序必须是显式数字。
    /// </summary>
    public static readonly DependencyProperty TabOrderProperty =
        DependencyProperty.RegisterAttached("TabOrder", typeof(int), typeof(FocusPolicy), new PropertyMetadata(0));

    public static void SetTabOrder(DependencyObject el, int value) => el.SetValue(TabOrderProperty, value);
    public static int GetTabOrder(DependencyObject el) => (int)el.GetValue(TabOrderProperty);

    /// <summary>
    /// 收集子树里【登记过的】输入框(TabOrder > 0 或 IsChatInput),按 TabOrder 排序。
    /// 不可见/禁用的分支整枝跳过 —— 与 FindChatInput 同一套规矩:
    /// 署名日期只在纸质载体下在树上、生成中输入框会被禁用 ——
    /// 所以圈【每次按键现算】,不能缓存。
    /// </summary>
    public static List<FrameworkElement> Ring(DependencyObject? scope)
    {
        var found = new List<(int Order, int Seq, FrameworkElement El)>();
        Walk(scope);
        return found.OrderBy(x => x.Order).ThenBy(x => x.Seq).Select(x => x.El).ToList();

        void Walk(DependencyObject? node)
        {
            if (node is null) return;
            if (node is UIElement { Visibility: not Visibility.Visible }) return;
            if (node is UIElement { IsEnabled: false }) return;
            if (node is FrameworkElement fe && (GetTabOrder(fe) > 0 || GetIsChatInput(fe)))
                found.Add((GetTabOrder(fe) > 0 ? GetTabOrder(fe) : int.MaxValue, found.Count, fe));
            var n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
    }

    /// <summary>圈里的下一个(抽成纯函数,好让无头自检直接验顺序)。</summary>
    public static FrameworkElement? Next(IReadOnlyList<FrameworkElement> ring, object? focused, bool back)
    {
        if (ring.Count == 0) return null;
        var i = -1;
        for (int k = 0; k < ring.Count; k++) if (ReferenceEquals(ring[k], focused)) { i = k; break; }
        if (i < 0) return ring[0];
        return ring[(i + (back ? -1 : 1) + ring.Count) % ring.Count];
    }

    /// <summary>
    /// 这个节点是不是落在【输入控件】里。★ 按【类型白名单】而不是按 Focusable:
    /// Control 的 Focusable 默认就是 true,ContentControl 这种纯板块容器会全部误判 ——
    /// 那正是本文件开头否掉的打地鼠路线。ComboBox 必须在名单里(否则点开下拉就丢焦点),
    /// PasswordBox 不是 TextBoxBase 的子类,得单列。
    /// </summary>
    public static bool IsInsideInput(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is System.Windows.Controls.Primitives.TextBoxBase
                     or System.Windows.Controls.ComboBox
                     or System.Windows.Controls.PasswordBox) return true;
            // ★ 视觉树走到头就换逻辑树:下拉选项住在独立的 Popup 里,
            //   视觉父链到 PopupRoot 就断了 —— 而逻辑父链能一路回到那个 ComboBox。
            var vp = node is Visual v ? VisualTreeHelper.GetParent(v) : null;
            node = vp ?? LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>
    /// 算一次 Tab 的结果:已经聚焦在那个输入框上 -> 取消聚焦(null);否则 -> 聚焦它。
    /// 输入框不存在 -> 恒为 null。抽成纯函数,好让无头自检直接验这个开关。
    /// </summary>
    public static FrameworkElement? Toggle(FrameworkElement? chatInput, object? focused)
        => chatInput is null || ReferenceEquals(chatInput, focused) ? null : chatInput;

    /// <summary>
    /// 处理一次 Tab。圈里只有一个(或没有)-> 二态开关(与 07-30 版逐字相同);
    /// 圈里有好几个 -> 在它们之间转(2026-08-03 裁定)。Shift+Tab 往回转。
    /// </summary>
    /// <param name="park">"什么都不聚焦"时把焦点停到哪(零尺寸的停车位元素)</param>
    public static void HandleTab(DependencyObject? scope, IInputElement? park, bool back = false)
    {
        var ring = Ring(scope);
        if (ring.Count > 1)
        {
            // ★ 圈内循环,不再自己插一个"什么都不聚焦"的档 ——
            //   取消聚焦有【点空白处】这个手段(同一批裁定),不必占着 Tab 的一个档。
            Next(ring, Keyboard.FocusedElement, back)?.Focus();
            return;
        }
        var input = ring.Count == 1 ? ring[0] : FindChatInput(scope);
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
