// V29 -- 滚轮那条的护栏(实机反馈①)。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★★ 形状照 `WPF-PITFALLS.md` 第 14 条那条护栏来的:
//    「`client --selftest` 里有一条**真量偏移**的行为断言(读 `ScrollViewer.VerticalOffset`,
//      断言全程不出现 0)。★ 已做过反向验证:把定位改回去,那两条当场红。」
//    管理端要有**等价的**,而且**要能红** —— 用户当天点名的就是这一条。
//
//  ⇒ 主判据是一张**网格**:真窗口 · 逐点命中测试 · 逐点发一次滚轮 · 逐点读根偏移。
//    它测的正是用户那句话的字面意思 ——「鼠标放在什么板块上」滚不动。
//
//  ★★★ 这张网格**已经红过**,而且红出来的东西正是真凶(修之前的实测):
//
//    | 页       | 20 个点里滚不动的 |
//    |----------|------------------|
//    | 主机中枢 | 0                |
//    | **模型** | **12** ★         |
//    | 记忆库   | 0                |
//    | 客户端与栈 | 0              |
//
//    死因:`ModelsView.cs:83` 自己也调了 `Ui.Page(...)`,而 `Ui.Page` 返回的就是
//    一个 ScrollViewer ⇒ 窗口的页里又套了一个页,内层**一个像素都滚不动**
//    却把每个滚轮都吞掉(WPF-PITFALLS 第 4 条点名的「起手就在边界上」那一种)。
//    装上 `AdminScroll.Install()` 之后同一张网格:**四页 80 个点,0 个死的**。
//
//  ★★ 三处**实测**到的坑,写在这儿免得下一个人再踩(都是这次量出来的):
//    · `RaiseEvent(MouseWheelEvent)` **不会**触发 `PreviewMouseWheel` 处理器 ——
//      而 `Wheel.PassThrough` 挂的正是 Preview。只发冒泡事件的话,这条护栏
//      **修没修都绿**(第一版就是这样,一条零断言)。⇒ 见 <see cref="SendWheel"/>。
//    · 两次滚轮之间**必须 `UpdateLayout()`**:偏移是在 arrange 里才落到
//      `VerticalOffset` 上的,连发 12 次而不排版,效果只等于**一次**(实测 48px)。
//      不排版的话「让路了」与「让了一次就卡住」分不开。
//    · `Application` 默认 `ShutdownMode.OnLastWindowClose`:关掉最后一个窗口 =
//      整个 Application 关掉,之后所有窗口**都不再排版**(实测 ActualWidth 全是 0,
//      断言读到一串 0 —— 看着像"滚不动",其实是没排版)。⇒ 显式改成 OnExplicitShutdown。
//
//  ★ 顺带纠正 `SelftestLiveViews.cs` 里那段注释:「屏幕外窗口 Windows 不真渲染,
//    Loaded 照样不来」今天**复现不出来** —— 实测 `Left=-20000` 的窗口照样触发 Loaded、
//    照样真排版(ActualHeight=726)。那次的真凶多半就是上面那条 Application 提前关掉。
// ══════════════════════════════════════════════════════════════════════════════

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LocalAI.Admin.Services;
using LocalAI.Admin.Views;
using LocalAI.Client.Views;

namespace LocalAI.Admin;

public static partial class Selftest
{
    static void RunWheel()
    {
        Console.WriteLine("\n-- 滚轮全局可滚(V29 · 实机反馈①)--");

        // ── ① 结构:让路那份源码**编进了本程序集** ────────────────────────
        //   ★ 这一条只挡住「改成项目引用」那一支。**它挡不住复制**(见下面 ①b)。
        Assert(typeof(Wheel).Assembly == typeof(AdminScroll).Assembly,
            "★★ `Wheel.PassThrough` 与管理端**编在同一个程序集**里(不是引了个类库)—— "
            + "V21 搬 3100 行时漏的就是这一个。★ 这一条**不**证明它不是复制的,那一半在下面");
        WheelNotACopy();

        // ── WPF 前提 ─────────────────────────────────────────────────────
        if (Application.Current is null) new Application();
        // ★ 见文件头第三条:不改这个,下面每个窗口都不排版,而断言只会读到一串 0
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        LocalAI.Client.Theme.ThemeManager.Initialize(LocalAI.Client.Services.Skin.Breeze);

        WheelGrid();
        WheelInnerKeepsIt();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ①b **不是复制** —— V29 的判词写着这四个字,而当时**没有任何东西守着它**
    // ══════════════════════════════════════════════════════════════════════
    //  ★★★ 核实那一轮当场演示了这个洞:把 `app/Views/Wheel.cs` 复制成
    //    `admin/Views/Wheel.cs`(同 namespace)、再把 csproj 那一行删掉
    //    ⇒ 程序集判据**照样绿**(复制过来的那份也编在同一个程序集里)。
    //    ⇒ 判词比判据宽,而宽出来的那一半正是本仓最恨的那件事:两份漂了不会有东西红。
    //
    //  ⇒ 两条一起才关得住这个洞,而且是**两个方向**:
    //    · csproj 里那一行 link **在**(否则它是从别处编进来的);
    //    · admin/ 底下**没有任何文件**声明 `namespace LocalAI.Client.` ——
    //      那正是"复制一份过来"的签名(复制必然连 namespace 一起抄,
    //      改了 namespace 的话 `AdminScroll` 那句 `using LocalAI.Client.Views;` 就编不过)。
    //  ★ 发布产物旁边没有源码 ⇒ 这一节整段 **Skip**(第 11 条:那一趟本来就测不了)。
    static void WheelNotACopy()
    {
        var root = AdminSourceRoot();
        if (root is null)
        {
            Skip("★★ 「Wheel.cs 是 link 进来的,不是复制的」",
                 "这一趟旁边没有管理端源码根(发布产物形态)—— 判据要读 csproj 与 admin/*.cs,"
                 + "**这一条没跑**,不要读成「没有人复制过」");
            return;
        }

        var proj = Path.Combine(root, "localai-admin.csproj");
        var projText = File.Exists(proj) ? File.ReadAllText(proj) : "";
        Assert(projText.Contains(@"Include=""..\app\Views\Wheel.cs""", StringComparison.Ordinal),
            @"★★★ csproj 里那一行 `<Compile Include=""..\app\Views\Wheel.cs"">` **在** —— "
            + "它是「两个 csproj 编同一个文件」的唯一凭据。删掉它而在 admin 里放一份副本,"
            + "程序集那条判据**照样绿**(核实实测),所以必须单独钉这一行");

        // ★ admin/ 底下不许有客户端命名空间的**声明** —— 那是复制的签名。
        //  ★★ 判据必须是「**行首的** namespace 声明」,不是"文中出现过这几个字":
        //    第一版写成 `Contains("namespace LocalAI.Client.")` ⇒ 它把**自己**抓了
        //    (本文件的断言文案与那个搜索串里都写着这几个字,FAIL=1,而红得理由是假的)。
        //    ⇒ 这是 ASSERTION-PITFALLS 第 1 条那一族(已踩 10 次)。
        //    ★ 修法是**收紧判据**,不是把这段文字改写成绕开断言的样子 ——
        //      后者是把代价转嫁给下一个读代码的人,那条坑明文禁止。
        var nsDecl = new System.Text.RegularExpressions.Regex(
            @"(?m)^[ \t]*namespace[ \t]+LocalAI\.Client(?:\.[A-Za-z0-9_]+)*[ \t]*[;{]");
        var strays = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(f => nsDecl.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetFileName(f)).OrderBy(x => x).ToList();
        Assert(strays.Count == 0,
            $"★★★★ `admin/` 底下**没有任何文件**声明 `namespace LocalAI.Client.*`(实测 {strays.Count} 个"
            + (strays.Count > 0 ? ":" + string.Join("、", strays) : "")
            + ")—— 那是「把客户端那份复制过来」的签名。复制的那天两份就开始漂,"
            + "而漂了不会有任何东西红(裁定④ / D93 裁定④的全部理由)");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ② 主判据:四页 × 20 个点,一个都不许死
    // ══════════════════════════════════════════════════════════════════════
    static void WheelGrid()
    {
        // ★★ 建的是**真的那个窗口**,不是替身 —— 接线(`AdminScroll.Install()`)就在
        //   `AdminWindow` 的构造函数里,替身会把要测的那一步整个绕过去
        //   (ASSERTION-PITFALLS 第 14 条)。
        var win = new AdminWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,   // ★ 不改,CenterScreen 会把它拽回屏幕中间
            Left = -20000, Top = -20000,
            Width = 1040, Height = 380,                             // ★ 矮一点:四页都真的滚得起来
            ShowInTaskbar = false, ShowActivated = false,
        };
        win.Show();
        Pump(1500);

        var probed = 0;
        var dead = new List<string>();
        var thinPages = new List<string>();

        foreach (var key in AdminWindow.PageKeys)
        {
            win.GoTo(key);
            Pump(1200);
            var root = win.PageHost;

            // ★ 元断言的前置:这一页**本身**得滚得起来。滚不起来的页面上,
            //   下面每个点都会读到 0,而那读起来跟"滚轮死了"一模一样。
            if (root.ScrollableHeight < 49) { thinPages.Add($"{key}({root.ScrollableHeight:0}px)"); continue; }

            foreach (var y in GridY)
            foreach (var x in GridX)
            {
                if (VisualTreeHelper.HitTest(root, new Point(x, y))?.VisualHit is not UIElement el) continue;
                probed++;
                root.ScrollToVerticalOffset(0); win.UpdateLayout();
                SendWheel(el, -120);
                win.UpdateLayout();
                if (root.VerticalOffset < 1) dead.Add($"{key}({x},{y})→{el.GetType().Name}");
            }
        }

        // ★ 元断言:真的打到点了。0 个的话下面那条恒真 —— 而"网格打了个空"
        //   与"网格全过了"在断言上长得一模一样,那正是本仓最恨的那种绿。
        Assert(probed >= 40,
            $"★ 元断言:网格真的打到了 {probed} 个点(至少 40)—— "
            + $"太少说明窗口没排版{(thinPages.Count > 0 ? ";滚不起来的页:" + string.Join("、", thinPages) : "")}");
        Assert(thinPages.Count == 0,
            $"★ 元断言:四页都撑得比视口高(滚不起来的:{(thinPages.Count == 0 ? "无" : string.Join("、", thinPages))})—— "
            + "滚不起来的页在这张网格里是**测不到**的,不能算它过了");

        // ★★★★ 承重的那一条。它逐字就是用户那句「滚轮不是全局可以滚动上下的」的反面。
        Assert(dead.Count == 0,
            $"★★★★ **每一页、每一个板块上滚轮都推得动整页**(实测 {probed} 个点,死 {dead.Count} 个"
            + (dead.Count > 0 ? ":" + string.Join("、", dead.Take(8)) + (dead.Count > 8 ? "…" : "") : "")
            + ")—— ★ 这条红过:装 `AdminScroll.Install()` 之前,「模型」那一页 20 个点里死 12 个,"
            + "死因是 `ModelsView.cs:83` 的页中页(`Ui.Page` 返回的就是 ScrollViewer)");

        // ── 普查:除了「按规矩本来就不该装」的,不许有漏网的 ────────────────
        //   ★ 网格只覆盖它打得到的点;这一条覆盖**树里所有**滚动区,
        //     两条各补对方的洞:网格证明"现在能滚",普查证明"没有一处漏挂"。
        var seen = 0; var missed = new List<string>();
        foreach (var key in AdminWindow.PageKeys)
        {
            win.GoTo(key); Pump(600);
            foreach (var sv in AdminScroll.ScrollViewersIn(win))
            {
                seen++;
                if (!AdminScroll.IsTamed(sv) && !AdminScroll.IsExempt(sv))
                    missed.Add($"{key}:{(sv.Name is { Length: > 0 } n ? n : sv.GetType().Name)}");
            }
        }
        Assert(seen > 0, $"★ 元断言:普查真的走到了滚动区(实测 {seen} 个)");
        Assert(missed.Count == 0,
            $"★★★ **树里每一个该让路的滚动区都让了路**(走过 {seen} 个,漏 {missed.Count} 个"
            + (missed.Count > 0 ? ":" + string.Join("、", missed.Distinct()) : "")
            + ")—— ★ 这条钉的是用户那句「一个修法漏改一处,缺陷就完整地留在那一处」:"
            + "新写的页、后加的控件都得被同一张网罩住,不靠谁记得挂");

        win.Close();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ③ 反向:内层**自己还能滚**的时候,滚的是它 —— 外层不许跟着动
    // ══════════════════════════════════════════════════════════════════════
    //  ★ 少了这一条,「一律往上抛」那种实现也会把 ② 全绿,
    //    而它的后果是用户在记忆库正文框里**改不动字**(滚轮全被页面吃了)。
    static void WheelInnerKeepsIt()
    {
        // ★ 用的是**真的那个编辑框**(`MemoryView.EditBlock`,internal 测试缝),
        //   不是形状相同的替身 —— 替身测过了什么也不说明。
        var entry = new MemoryEntry(
            Id: "selftest-wheel", Title: "护栏用的条目",
            Body: string.Join("\n", Enumerable.Range(0, 40).Select(i => $"正文第 {i} 行")),
            Kind: MemoryKind.Summary, Scope: LocalAI.Client.Services.ProjectScope.Personal,
            OwnerMemberId: null, SourceProjectId: null, SourceSessionIds: null,
            CreatedAt: DateTime.UtcNow);
        var editor = new MemoryView().EditBlock(entry);

        var filler = Enumerable.Range(0, 40).Select(i => (UIElement)Ui.Body($"占位第 {i} 行")).ToArray();
        var page = Ui.Page(new[] { (UIElement)editor }.Concat(filler).ToArray());

        var w = new Window
        {
            Content = page, Width = 900, Height = 380,
            Left = -20000, Top = -20000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None,
        };
        w.Show();
        Pump(900);

        var inner = AdminScroll.ScrollViewersIn(editor).FirstOrDefault(sv => sv.ScrollableHeight > 10);
        Assert(inner is not null,
            "★ 元断言:记忆库编辑框里真的有一个**自己能滚**的滚动区(TextBox 的 PART_ContentHost)—— "
            + "没有的话下面那条测的不是「内层还能滚」那个场景");
        Assert(page.ScrollableHeight > 40,
            $"★ 元断言:这一页真的比视口高(可滚 {page.ScrollableHeight:0} px)");
        if (inner is null) { w.Close(); return; }

        page.ScrollToVerticalOffset(0); inner.ScrollToVerticalOffset(0); w.UpdateLayout();
        var pageBefore = page.VerticalOffset;
        for (int i = 0; i < 3; i++) { SendWheel(inner, -120); w.UpdateLayout(); }

        Assert(inner.VerticalOffset > 1 && Math.Abs(page.VerticalOffset - pageBefore) < 1,
            $"★★★ 反向:正文框**自己还能滚**的时候,滚的是它、整页**不动**"
            + $"(内层 {inner.VerticalOffset:0} · 外层 {page.VerticalOffset:0})—— "
            + "少了这一条,「一律往上抛」那种实现也全绿,而它让人在框里改不动字");

        // ── 补上普查的一个已知缺口(V29b)────────────────────────────────────
        //  ★★ `WheelGrid` 那条普查走的是 `AdminWindow` 的四页,而记忆库编辑框里那两个
        //    `PART_ContentHost` **只有展开编辑时才存在** ⇒ 普查那一刻它们不在树上,
        //    覆盖不到。类处理器**盖得到**它们(`ScrollChanged` 抓得到后加进树的),
        //    但"盖得到"与"验过了"是两件事。
        //  ⇒ 这一节手上正好就有那棵展开后的树,顺手把它也普查一遍,缺口就不是缺口了。
        var seen = 0; var missed = new List<string>();
        foreach (var sv in AdminScroll.ScrollViewersIn(editor))
        {
            seen++;
            if (!AdminScroll.IsTamed(sv) && !AdminScroll.IsExempt(sv))
                missed.Add(sv.Name is { Length: > 0 } n ? n : sv.GetType().Name);
        }
        Assert(seen >= 2, $"★ 元断言:展开后的编辑框里真的有滚动区可查(实测 {seen} 个,标题框 + 正文框各一个)");
        Assert(missed.Count == 0,
            $"★★★ **展开编辑时才出现的那几个滚动区也让了路**(走过 {seen} 个,漏 {missed.Count} 个"
            + (missed.Count > 0 ? ":" + string.Join("、", missed) : "")
            + ")—— ★ 四页那条普查**够不着这里**(普查时没人在编辑),而用户真正会在这里滚");

        w.Close();
    }

    // ── 网格与三个工具 ────────────────────────────────────────────────────
    static readonly double[] GridX = { 60, 300, 700, 1000 };
    static readonly double[] GridY = { 40, 120, 200, 300, 360 };

    /// <summary>
    /// 照输入系统的样子发一次滚轮:**先隧道 `PreviewMouseWheel`(根 → 源),再冒泡 `MouseWheel`**。
    /// <para>★★★ 只 `RaiseEvent(MouseWheelEvent)` 的话,`Wheel.PassThrough` 挂的那个
    /// Preview 处理器**一次都不会被调用** —— 于是这条护栏修没修都绿。
    /// 这是实测出来的(第一版就是那样,一条零断言)。</para>
    /// </summary>
    static void SendWheel(UIElement src, int delta)
    {
        var pre = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = src };
        src.RaiseEvent(pre);
        if (pre.Handled) return;
        src.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        { RoutedEvent = UIElement.MouseWheelEvent, Source = src });
    }

    /// <summary>推一段消息循环(排版 · Loaded · 异步填充都要它)。</summary>
    static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var t = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Normal,
                                    (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        t.Start();
        Dispatcher.PushFrame(frame);
        t.Stop();
    }
}
