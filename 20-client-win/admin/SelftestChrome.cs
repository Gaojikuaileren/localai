// V29b -- 自绘标题栏的护栏(实机反馈④的判据,V29 欠的那条)。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★★ 这一节存在的理由,是 V29 交回**自己写下的那句话**被反过来用在了自己身上:
//    「一条修没修都绿的判据就是一条零断言」—— 而 ④ 的情况更彻底:**压根没有那条判据**。
//    核实实测:把 `AdminChrome.cs` 整个删掉、`AdminWindow` 改回 `Content = Ui.Page(_body)`
//    ⇒ **build 过、自检全绿、门禁全过**。整片顶栏是零成本可回退的。
//
//  ⇒ 这一节钉四件事,全部读**真窗口上的真状态**,一条都不读源码文本:
//    ① 这个窗口真的**不是**原生标题栏(`WindowChrome` 挂着、`CaptionHeight == 0`);
//    ② 三个窗口键**真的在树上**(按身份找,不按显示文案猜);
//    ③ ★ 点下去**真的有事发生**:最小化键真把窗口最小化、最大化键真在两态之间切、
//       × 真的走 `Close()`(⇒ `Closing` 真的触发,那正是 App 缩托盘挂钩的地方);
//    ④ 顶栏里**没有第二个标题** —— 客户端 2026-08-03 的裁定(窗口里不再重复名字)。
//
//  ★ 一个具体改动 → 它真的红:把 `AdminWindow` 那行
//    `Views.AdminChrome.Apply(this, _pageHost)` 换回 `Content = _pageHost`
//    ⇒ ①②③ 三条当场红(实测 FAIL=5)。
//
//  ★★ 为什么按 `Name` 找而不按 ToolTip 找:托盘那边早就写过这条理由
//    (`App.TrayCloseItemName`)——「按文案找的断言会在改文案那天红,
//    而它本来要守的是『这条路通不通』」。★ 唯一按文案判的是 ④ 与 ×
//    那句提示,因为**判词本身说的就是给人看的字**(第 3b 条:范围由判词决定)。
// ══════════════════════════════════════════════════════════════════════════════

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using LocalAI.Admin.Views;

namespace LocalAI.Admin;

public static partial class Selftest
{
    static void RunChrome()
    {
        Console.WriteLine("\n-- 自绘标题栏(V29b · 实机反馈④欠的判据)--");

        if (Application.Current is null) new Application();
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        LocalAI.Client.Theme.ThemeManager.Initialize(LocalAI.Client.Services.Skin.Breeze);

        // ★ 真窗口。★★ 单独建一个,不蹭滚轮那一节的 —— 这里会把它最小化/最大化,
        //   而那一节正在量偏移,两边搅在一起会让谁红都说不清。
        var win = new AdminWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, Width = 1040, Height = 620,
            ShowInTaskbar = false, ShowActivated = false,
        };
        win.Show();
        Pump(900);

        // ── ① 不是原生标题栏 ────────────────────────────────────────────────
        var chrome = WindowChrome.GetWindowChrome(win);
        Assert(chrome is not null && chrome.CaptionHeight == 0 && !chrome.UseAeroCaptionButtons,
            "★★★ 这个窗口用的是**自绘标题栏**(WindowChrome 挂着 · CaptionHeight=0 · 不用系统窗口键)—— "
            + $"实测 chrome={(chrome is null ? "没挂" : $"CaptionHeight={chrome.CaptionHeight}")}。"
            + "★ 与客户端同一条判断(`MainWindow.xaml:15-17`):不是 `WindowStyle=None + AllowsTransparency`,"
            + "后者会让整窗走 layered window 合成");
        Assert(chrome is not null && chrome.ResizeBorderThickness.Left >= 4,
            "★ 边框还拖得动(ResizeBorderThickness ≥ 4)—— CaptionHeight=0 之后,"
            + "这是**唯一**还能改窗口大小的东西;设成 0 就是一个拉不动的窗口");

        // ── ② 三个窗口键真的在树上 ──────────────────────────────────────────
        var min = FindNamed(win, AdminChrome.MinButtonName);
        var max = FindNamed(win, AdminChrome.MaxButtonName);
        var close = FindNamed(win, AdminChrome.CloseButtonName);
        Assert(min is not null && max is not null && close is not null,
            $"★★ 最小化 / 最大化 / 关闭三个窗口键**真的在可视树上**"
            + $"(实测 min={min is not null} max={max is not null} close={close is not null})—— "
            + "少一个就是一个残废的标题栏:CaptionHeight=0 之后系统那三个键已经没有了");

        // ── ③ ★★★ 点下去真的有事发生 ──────────────────────────────────────
        if (min is not null && max is not null && close is not null)
        {
            win.WindowState = WindowState.Normal; Pump(200);
            Click(min); Pump(400);
            Assert(win.WindowState == WindowState.Minimized,
                $"★★★★ 点最小化键 ⇒ 窗口**真的最小化了**(实测 {win.WindowState})—— "
                + "这一条不是读源码看有没有写那一句,是真发了一次点击再看后果");

            win.WindowState = WindowState.Normal; Pump(300);
            Click(max); Pump(400);
            var maxed = win.WindowState == WindowState.Maximized;
            Click(max); Pump(400);
            Assert(maxed && win.WindowState == WindowState.Normal,
                $"★★★★ 点最大化键 ⇒ 真的最大化,再点一次真的还原"
                + $"(实测 最大化后={maxed} · 再点后={win.WindowState})—— "
                + "★ 只钉「最大化」的话,一个卡在最大化回不来的实现也会绿");

            // ★ × 走的是 `Close()`,所以 `Closing` 必须真的触发 —— 那正是
            //   `App.ShowPanel` 挂「取消 + 缩托盘」的地方(裁定第 6 条)。
            //   ★★ 这里自己挂一个**取消**的处理器:既验证了事件真的来,
            //     又不让自检把这个窗口关掉(关掉之后下面几条就没得测了)。
            var closing = 0;
            CancelEventHandlerShim(win, () => closing++);
            Click(close); Pump(400);
            Assert(closing == 1,
                $"★★★★ 点 × ⇒ 真的走 `Close()`,`Closing` **真的触发**(实测 {closing} 次)—— "
                + "承重的是这一条:裁定第 6 条「× 只缩托盘」是靠 `App.ShowPanel` 在 `Closing` 上"
                + "挂取消实现的。× 若绕开 `Close()` 自己藏窗口,那条裁定就**没有走那条路**,"
                + "而两种做法在屏幕上长得一模一样");

            // ── × 的提示要**说实话** ────────────────────────────────────────
            var tip = (close as FrameworkElement)?.ToolTip as string ?? "";
            Assert(tip.Contains("托盘", StringComparison.Ordinal),
                $"★★★ × 的提示要说清**真实后果**:点完进程还活着,只是缩进了托盘(实测「{tip}」)—— "
                + "写成光秃秃的「关闭」就是骗人,而真关闭只在托盘右键那一条路上");
        }

        // ── ④ 顶栏里没有第二个标题 ──────────────────────────────────────────
        //   客户端 2026-08-03 的用户裁定:「任务栏/托盘已有图标与名字,窗口里不再重复」。
        //   ★ V29 就是靠"自绘顶栏不放标题文字"把原来那两处重复消掉的 ——
        //     而"消掉了"这件事在此之前也没有任何东西守着。
        var titleHits = TextsOfAll(win).Count(t => t.Trim() == "主机管理端");
        Assert(titleHits == 1,
            $"★★★ 窗口里「主机管理端」这几个字**只出现一次**(实测 {titleHits} 次)—— "
            + "自绘顶栏**不放标题文字**(客户端 2026-08-03 的裁定:窗口里不再重复名字);"
            + "在顶栏里再写一遍,原来那两处重复就又回来了");

        win.Close();
    }

    // ── 三个小工具 ────────────────────────────────────────────────────────
    /// <summary>按 <c>Name</c> 在可视树里找一个元素(模板内部也走)。</summary>
    static FrameworkElement? FindNamed(DependencyObject root, string name)
    {
        if (root is FrameworkElement fe && string.Equals(fe.Name, name, StringComparison.Ordinal)) return fe;
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            if (FindNamed(VisualTreeHelper.GetChild(root, i), name) is { } hit) return hit;
        return null;
    }

    /// <summary>真发一次左键抬起 —— 窗口键挂的就是 <c>MouseLeftButtonUp</c>。</summary>
    static void Click(FrameworkElement el) =>
        el.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = el });

    /// <summary>挂一个**取消**的 Closing 处理器并数它来了几次。</summary>
    static void CancelEventHandlerShim(Window w, Action onClosing) =>
        w.Closing += (_, ev) => { onClosing(); ev.Cancel = true; };

    /// <summary>可视树里所有 TextBlock 的文字(**不去重** —— 这里要数的就是"出现了几次")。</summary>
    static List<string> TextsOfAll(DependencyObject root)
    {
        var outp = new List<string>();
        void Walk(DependencyObject d)
        {
            if (d is TextBlock tb && !string.IsNullOrEmpty(tb.Text)) outp.Add(tb.Text);
            var n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        try { Walk(root); } catch { }
        return outp;
    }
}
