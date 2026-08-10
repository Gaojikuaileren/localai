// V29 -- 管理端的滚轮让路:把 `Wheel.PassThrough` 装到**每一个** ScrollViewer 上。
//
// ══════════════════════════════════════════════════════════════════════════════
//  实机反馈①(2026-08-09):「管理端鼠标滚轮不是全局可以滚动上下的」。
//
//  ★ 这不是新缺陷,是「修法写好了、新地方没接上」:
//    · 管理端**有**外层滚动容器 —— `Ui.Page(...)` 返回的就是 ScrollViewer;
//    · 这个坑 `00-docs/WPF-PITFALLS.md` 第 4 条记过;
//    · 修法 `app/Views/Wheel.cs` 也写好了(「自己滚不动了就把同一个事件重新抛给父级」);
//    · **而 `Wheel.cs` 当时不在管理端 csproj 的 29 条 `<Compile Include>` 里。**
//
//  ══════════════════════════════════════════════════════════════════════════
//  ★★★ 真凶是**页中页**,而不是随便哪个控件 —— 这是量出来的,不是猜的
//
//  在真窗口上打了一张 4×5 的网格,逐点做命中测试、逐点发一次滚轮、逐点读根偏移
//  (诊断源码见 `SelftestWheel.WheelSweep` 那一节的由来):
//
//    | 页       | 20 个点里滚不动的 |
//    |----------|------------------|
//    | 主机中枢 | 0                |
//    | **模型** | **12** ★         |
//    | 记忆库   | 0                |
//    | 客户端与栈 | 0              |
//
//  ⇒ 只有「模型」那一页是死的,而死因是 `ModelsView.cs:83` —— 它自己也调了
//    `Ui.Page(...)`,**而 `Ui.Page` 返回的就是一个 ScrollViewer**。
//    于是窗口的页(`AdminWindow.cs` 的 `Ui.Page(_body)`)里又套了一个页。
//    ★★ 内层那个**一个像素都滚不动**(高度由内容撑满,ScrollableHeight = 0),
//      却仍然把每一个滚轮事件吞掉 —— 这正是 WPF-PITFALLS 第 4 条点名的最阴那一种:
//      「**起手就已经在边界上**,于是开屏第一下就是死的」。
//
//  ★ 顺带纠正一条我一开始的误判:`TextBox` 那个 `PART_ContentHost`(记忆库编辑框)
//    **不是**真凶 —— 实测 WPF 的 TextBox 滚到底之后本来就会把滚轮让上去。
//
//  ══════════════════════════════════════════════════════════════════════════
//  ★★★ 为什么仍然是**一网打尽**,而不是只改 ModelsView 那一行
//
//  客户端那边是逐处挂的(18 处 `.PassThrough()`)。照抄那种做法会踩用户当天点名的事:
//    「别只修用户碰到的那一处 —— **一个修法漏改一处,缺陷就完整地留在那一处**。」
//  今天只有一页中招,是因为管理端今天只有四页;而「新写的页顺手调一下 `Ui.Page`」
//  正是最容易再犯的那一步 —— 它编得过、看着对、只是滚不动。
//  ⇒ 类处理器:凡是本进程里出现的 ScrollViewer,一律让路。没有"记得挂"这一步。
//
//  ★ 那为什么不干脆把 `ModelsView.cs:83` 的 `Ui.Page` 换成 `Ui.Stack`?
//    因为 `Ui.Page` 同时给着 28/24 的边距与 MaxWidth 980 —— 换掉会动版面,
//    而用户这次要的是"滚得动",不是"挪位置"。**这条留在交回里说,不顺手改。**
//
//  ══════════════════════════════════════════════════════════════════════════
//  ★★ 锚点为什么是 `ScrollChangedEvent` 而不是 `Loaded` —— 三个候选都实测过
//     (探针源码见交回;下面三行是**量出来的**,不是推的):
//
//     | 锚点            | 初次进树 | **后加进树** | 无窗口(只 UpdateLayout) |
//     |-----------------|---------|-------------|--------------------------|
//     | `Loaded`        | 抓得到  | ★ **抓不到** | 抓不到                   |
//     | `SizeChanged`   | 抓得到  | 抓得到       | 抓得到(但非 ScrollViewer 专属) |
//     | `ScrollChanged` | 抓得到  | 抓得到       | 抓得到                   |
//
//     ★ 「后加进树」这一格是承重的:`MemoryView` 的条目、`ComponentPicker` 的组件行
//       都是**异步填完才进树**的。`Loaded` 在那一格抓不到 ⇒ 用 `Loaded` 等于
//       「初次那几个修好了,用户真正会去滚的那些没修」—— 又是一次漏改一处。
//     ★ `ScrollChanged` 还有一层合适:一个从没排过版的 ScrollViewer 本来也吞不了谁,
//       所以"排完版才装"不是妥协,是**正好那个时刻**。
//
//  ★ 幂等靠 `Tamed` 附加属性,不靠"我记得我挂过":`ScrollChanged` 每次滚动都来,
//    重复 `PassThrough` 会让同一次滚轮被重抛两遍(外层滚双倍)。
// ══════════════════════════════════════════════════════════════════════════════

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.Views;

namespace LocalAI.Admin.Views;

public static class AdminScroll
{
    /// <summary>这个 ScrollViewer 已经装过让路了。★ 幂等的凭据,不是"我记得我挂过"。</summary>
    static readonly DependencyProperty TamedProperty = DependencyProperty.RegisterAttached(
        "Tamed", typeof(bool), typeof(AdminScroll), new PropertyMetadata(false));

    static bool _installed;

    /// <summary>
    /// 装上类处理器。★ 进程级、一次就够 —— 之后本进程里**任何** ScrollViewer
    /// 一排完版就自动让路。
    /// <para>★★ 幂等:自检会先调它再建视图,而真程序在 <c>OnStartup</c> 里调 ——
    /// 装两次会让每个 ScrollViewer 被 <c>Tame</c> 两次,而 <see cref="Tame"/> 自己也挡了一层。</para>
    /// </summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer), ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((s, _) => Tame((ScrollViewer)s)));
    }

    /// <summary>装过没有。★ 自检的普查判据读的就是这一个。</summary>
    public static bool IsTamed(ScrollViewer sv) => (bool)sv.GetValue(TamedProperty);

    /// <summary>
    /// 这一个**该不该**装让路。★★ 两个条件都是「PassThrough 在这里帮得上忙吗」,
    /// 不是口味问题 —— 装错地方会把滚轮弄得比不装还坏,两条各有一次实测:
    ///
    /// <para>① **必须是内层**(祖先里还有一个 ScrollViewer)。最外那个没有"外层"可让 ——
    /// 它到底之后 `PassThrough` 会把事件标成已处理再抛给窗口,而窗口不滚。
    /// 后果:页面滚到底时,光标底下那个文本框**也跟着不动了**。
    /// ★ 客户端那 18 处 `.PassThrough()` 挂的**全是内层**,最外的 `Ui.Page` 一处都没挂 ——
    /// 这条规矩本来就在,只是从来没写下来。</para>
    ///
    /// <para>② **必须真的有父级可抛**(`Parent is UIElement`)。`PassThrough` 让路那一支是
    /// 「`e.Handled = true` 然后 `Parent.RaiseEvent(...)`」—— 没有父级时前半句照做、
    /// 后半句做不了,滚轮**当场消失**,比不装还坏。控件模板的**根**元素就是这一档。
    /// <br/>★ 实测更正:本仓 TextBox 模板里的 `PART_ContentHost` 外面**还包着一个 Border**
    /// (`Theme/Controls.xaml:382-390`),所以它的 `Parent` 不是 null、也**确实被装上了** ——
    /// 红测里它就在"漏装"名单上。这条判据因此不是给它写的,是给"哪天真有个模板根"兜底的。</para>
    ///
    /// <para>★ 不满足时**不打标记**,下一次 `ScrollChanged` 会再问一遍 ——
    /// 元素刚建出来还没进树时祖先链是不全的,一次判死会永久错过。</para>
    /// </summary>
    static bool ShouldTame(ScrollViewer sv)
        => sv.Parent is UIElement && HasScrollViewerAncestor(sv);

    static bool HasScrollViewerAncestor(DependencyObject node)
    {
        var p = VisualTreeHelper.GetParent(node);
        while (p is not null)
        {
            if (p is ScrollViewer) return true;
            p = VisualTreeHelper.GetParent(p);
        }
        return false;
    }

    /// <summary>
    /// 给这一个装上让路。★ 让路的逻辑**一个字都不在这儿** —— 走客户端那份
    /// <see cref="Wheel.PassThrough"/>(csproj link 同一个文件)。
    /// 在这里再写一遍的话,两份漂了不会有任何东西红。
    /// </summary>
    public static void Tame(ScrollViewer sv)
    {
        if (IsTamed(sv) || !ShouldTame(sv)) return;
        sv.SetValue(TamedProperty, true);
        sv.PassThrough();
    }

    /// <summary>
    /// 这一个**按规矩本来就不该装**(最外层 / 抛不出去的模板内层)。
    /// ★ 自检的普查判据要用它把「合规地没装」与「漏装了」分开 ——
    /// 分不开的话,那条普查断言要么恒假、要么得把真漏的那个也放过去。
    /// </summary>
    public static bool IsExempt(ScrollViewer sv) => !ShouldTame(sv);

    /// <summary>
    /// 可视树里所有的 ScrollViewer(含控件模板内部的,例如 TextBox 的 <c>PART_ContentHost</c>)。
    /// ★ 给自检的普查判据用:「这一页上还有没有**没让路**的滚动区」。
    /// </summary>
    public static IEnumerable<ScrollViewer> ScrollViewersIn(DependencyObject root)
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is ScrollViewer sv) yield return sv;
            foreach (var x in ScrollViewersIn(c)) yield return x;
        }
    }
}
