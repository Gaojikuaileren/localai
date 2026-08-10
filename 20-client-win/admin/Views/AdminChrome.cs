// V29 -- 管理端的自绘标题栏(实机反馈④「管理端窗口顶部栏也要风格统一」)。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★ 先核了客户端**是怎么做的**,再照着落 —— 车道任务写明「别自己发明第三种」。
//    客户端(`app/MainWindow.xaml:12-17`)逐字:
//      「自绘标题栏:用 WindowChrome,而不是 WindowStyle=None + AllowsTransparency。
//        后者会让整窗走 layered window 合成,显存/性能成本比它想替代的毛玻璃还高
//        (设计 §7 显存约束);WindowChrome 则保留系统的拖动、双击最大化、Aero Snap、
//        投影与调整边框。」
//    ⇒ 管理端照抄这条判断,连同这些数:38 高 · 46×38 的窗口键 · ResizeBorder 6 ·
//      CaptionHeight 0 · 关闭键 hover 用恒定风险红。
//
//  ★★ 两处**故意不一样**,各有理由,写在这儿免得下次被"统一"掉:
//    · 客户端标题栏右边还有连接状态点 / 日历 / 消息栏 —— 管理端没有那些东西,
//      硬摆上去就是三个永远不动的假控件;
//    · 关闭键的提示文字是「关闭(留在托盘)」而**不是**「关闭」:管理端的 ×
//      按裁定第 6 条只缩托盘,真正关闭只在托盘右键那一条路上(`RealCloseAsync`)。
//      ★ 这一句是**说实话**,不是装饰:点 × 之后进程还活着,不写清楚就是骗人。
//
//  ★★★ 那句重复的标题怎么处理(车道任务里的第二问):
//    客户端 2026-08-03 的用户裁定是「品牌块已移除:任务栏/托盘已有图标与名字,
//    窗口里不再重复」。⇒ 这条自绘标题栏**不放任何标题文字**。
//    于是 `AdminWindow.cs` 原来那两处重复(原生标题栏 + 正文 `Ui.Title`)自动只剩一处 ——
//    `Window.Title` 留着不动,它是**任务栏与 Alt+Tab** 认这个窗口的名字,不是界面上的重复。
//
//  ★ 圆角:客户端在 `SourceInitialized` 里调 `WindowCorners.Apply(this, skin)`(DWM 属性,
//    跟皮肤走)。管理端 link 了**同一份** `WindowCorners.cs`,不另写一套。
// ══════════════════════════════════════════════════════════════════════════════

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;
using LocalAI.Client.Views;

namespace LocalAI.Admin.Views;

public static class AdminChrome
{
    /// <summary>标题栏高度。★ 与客户端 `MainWindow.xaml:72` 的 `RowDefinition Height="38"` 同一个数。</summary>
    public const double BarHeight = 38;

    // ══════════════════════════════════════════════════════════════════════════
    //  ★★ 三个窗口键的**名字**。自检**按身份**找它们,不按显示文案去猜。
    //    ★ 这条手法是从托盘那边搬来的(`App.TrayCloseItemName` 的注释逐字):
    //      「按文案找的断言会在改文案那天红,而它本来要守的是『这条路通不通』」。
    //    ★★ V29b 补:在此之前这一整个文件**零判据** —— 把它整个删掉、
    //      `AdminWindow` 改回 `Content = Ui.Page(_body)`,build 过、自检全绿、门禁全过。
    //      判据见 `admin/SelftestChrome.cs`。
    // ══════════════════════════════════════════════════════════════════════════
    internal const string MinButtonName = "ChromeMinimize";
    internal const string MaxButtonName = "ChromeMaximizeRestore";
    internal const string CloseButtonName = "ChromeClose";

    /// <summary>
    /// 给窗口装上自绘标题栏,并把 <paramref name="body"/> 放到它下面。
    /// ★ 调用方**不再自己设 `Content`** —— 顶栏与正文的排布归这里一处管。
    /// </summary>
    public static void Apply(Window w, UIElement body)
    {
        WindowChrome.SetWindowChrome(w, new WindowChrome
        {
            CaptionHeight = 0,                          // ★ 系统不再把顶部当标题栏,拖动/双击自己接
            ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });

        // ── 三个窗口键 ────────────────────────────────────────────────────
        var min = CaptionButton(MinButtonName, IconName.Minimize, "最小化", () => w.WindowState = WindowState.Minimized);
        var max = CaptionButton(MaxButtonName, IconName.Maximize, "最大化", () => ToggleMaximize(w));
        // ★ 提示文字说的是**真实后果**:管理端的 × 只缩托盘(裁定第 6 条)。
        //   ★★ 这句话里的「托盘」两个字是被断言钉住的 —— 见 `SelftestChrome`:
        //     写成光秃秃的「关闭」就是骗人,因为点完进程还活着。
        var close = CaptionButton(CloseButtonName, IconName.Close, "关闭(留在托盘)", w.Close, danger: true);

        void SyncMax()
        {
            var maxed = w.WindowState == WindowState.Maximized;
            SetGlyph(max, maxed ? IconName.Restore : IconName.Maximize);
            max.ToolTip = maxed ? "向下还原" : "最大化";
        }
        w.StateChanged += (_, _) => SyncMax();
        SyncMax();

        // ★ 换肤时**不必**自己重画字形:`Icons.Make` 建的那个 host 会登记进 Icons 的弱引用表,
        //   `ThemeManager.SkinChanged` 一来它自己 `RefreshAll`(Icons.cs)。在这里再画一遍
        //   等于第二个刷新路径 —— 而两条路径漂了不会有任何东西红。
        //   ★ 要跟着换的只有窗口圆角:它是 DWM 属性,不在 Icons 那张表里。
        ThemeManager.SkinChanged += () => TryRoundCorners(w);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(min);
        buttons.Children.Add(max);
        buttons.Children.Add(close);

        // ── 拖动区:整条空白都可以拖(与客户端一样,CaptionHeight=0 所以自己接)──
        //  ★★ 底色用 `BgNav` 而**不是** `Brushes.Transparent`。两个理由,第二个才是承重的:
        //    ① 看起来一模一样 —— 它盖在同样是 BgNav 的标题栏上;
        //    ② `Brushes.Transparent` 是**颜色字面量**,而管理端有一条断言禁着它
        //       (客户端自检那条「颜色一律取自令牌」)。★ V29 第一版就是写的 Transparent,
        //       **被那条断言当场抓住** —— 而它抓得对:走令牌的这个版本换肤时跟着变,
        //       写死的那个不会。★ 关键是**有底色才接得到鼠标**(null 不参与命中测试),
        //       换成令牌之后这一条仍然成立。
        var drag = new Border();
        drag.Dyn(Border.BackgroundProperty, "BgNav");
        drag.MouseLeftButtonDown += (_, e) => OnDrag(w, e);

        var barGrid = new Grid();
        barGrid.Children.Add(drag);
        barGrid.Children.Add(buttons);

        var bar = new Border
        {
            Height = BarHeight,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = barGrid,
        };
        bar.Dyn(Border.BackgroundProperty, "BgNav").Dyn(Border.BorderBrushProperty, "Border");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(BarHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(bar, 0); root.Children.Add(bar);
        Grid.SetRow(body, 1); root.Children.Add(body);
        w.Content = root;

        // 圆角跟皮肤(与客户端同一份 WindowCorners.cs)。★ 句柄要等 SourceInitialized 才有。
        w.SourceInitialized += (_, _) => TryRoundCorners(w);
    }

    static void TryRoundCorners(Window w)
    {
        // ★ 失败不当回事:圆角是外观,拿不到句柄/不是 Win11 都只是方角,不该拖垮启动。
        try { WindowCorners.Apply(w, ThemeManager.Current); } catch { }
    }

    static void ToggleMaximize(Window w) =>
        w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>拖动 + 双击最大化。★ 与客户端 `MainWindow.xaml.cs:458` 同一套手感(含"最大化时先还原再跟手")。</summary>
    static void OnDrag(Window w, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(w); return; }
        if (w.WindowState == WindowState.Maximized)
        {
            var pct = e.GetPosition(w).X / w.ActualWidth;
            w.WindowState = WindowState.Normal;
            w.Left = Math.Max(0, w.PointToScreen(e.GetPosition(w)).X - w.RestoreBounds.Width * pct);
            w.Top = 0;
        }
        try { w.DragMove(); } catch { /* 鼠标已抬起时会抛,忽略 */ }
    }

    // ── 窗口键 ────────────────────────────────────────────────────────────
    //  ★ 为什么是 Border 而不是 Button:`Theme/Controls.xaml` 里有一条**隐式** Button 样式
    //    (圆角 + 内边距 + 焦点框),窗口键套上去就不是窗口键了;客户端在 XAML 里是用一条
    //    **keyed 样式**整条换掉模板解决的,而那条样式在 `MainWindow.xaml` 的 `Window.Resources` 里 ——
    //    本车道的边界够不着它(`app/**` 是禁区)。
    //  ★★ Border + 手工 hover 正是本仓给图标按钮用的写法(`Ui.PlusButton` 同款),
    //    不是新发明;而且客户端那三个键本来就 `Focusable=False` / `IsTabStop=False`,
    //    做成 Border 不丢任何键盘可达性 —— 它本来就没有。
    static Border CaptionButton(string name, IconName icon, string tip, Action onClick, bool danger = false)
    {
        var b = new Border
        {
            Name = name,          // ★ 自检按身份找它,不按显示文案猜
            Width = 46, Height = BarHeight,
            Cursor = Cursors.Hand,
            ToolTip = tip,
        };
        // ★ 常态底色 = 标题栏自己那个令牌(看起来就是"没有底色"),而**有底色才接得到鼠标** ——
        //   `Background = null` 不参与命中测试,整个 46×38 会失灵。理由同上面拖动区那一段。
        b.Dyn(Border.BackgroundProperty, "BgNav");
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        SetGlyph(b, icon);
        b.MouseEnter += (_, _) =>
        {
            // ★ 关闭键 hover 用**恒定风险红**(设计 §7:风险语义色三皮肤禁改),其余用 BgHover。
            //   与客户端 `CloseButton` 那条样式同一个令牌。
            if (danger) { b.Dyn(Border.BackgroundProperty, "RiskDanger"); TintGlyph(b, "FgOnAccent"); }
            else b.Dyn(Border.BackgroundProperty, "BgHover");
        };
        b.MouseLeave += (_, _) =>
        {
            b.Dyn(Border.BackgroundProperty, "BgNav");   // 回到常态 = 标题栏底色
            TintGlyph(b, "FgSecondary");
        };
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    static void SetGlyph(Border b, IconName icon)
    {
        var g = Icons.Make(icon, 13, "FgSecondary");
        g.HorizontalAlignment = HorizontalAlignment.Center;
        g.VerticalAlignment = VerticalAlignment.Center;
        g.IsHitTestVisible = false;      // ★ 让命中落在整个 46×38 上,而不是只有那 13px 的字形
        b.Child = g;
    }

    static void TintGlyph(Border b, string token)
    {
        if (b.Child is FrameworkElement fe) Icons.SetForeground(fe, token);
    }
}
