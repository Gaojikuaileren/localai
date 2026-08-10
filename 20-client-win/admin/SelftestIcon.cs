// V29 -- 管理端图标那条的护栏(实机反馈⑩)。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ 为什么这条值得一节判据,而不是"看一眼图就行":
//    `RealCloseAsync` 是**唯一**的真关闭入口,全靠用户在托盘里认出哪个是管理端。
//    两个 exe 的托盘图标长得一样 ⇒ **用户会点错**,而点错关掉的是另一个程序。
//    ⇒ 「两个托盘图标不许长得一样」是个**行为性质**,该被钉住。
//
//  ★★ 判据走的是**生产那一句**:`Icon.ExtractAssociatedIcon(Environment.ProcessPath)` ——
//    `App.BuildTray()` 用的就是它。不去读 csproj 里有没有那行 `<ApplicationIcon>`:
//    那是扫源码,而「标签写着」与「exe 里真有这个资源」是两件事
//    (ASSERTION-PITFALLS 第 14 条)。
//
//  ★ 三个数都是先量后钉的(2026-08-09 实测,余量很大):
//    · 偏红像素占比:管理端 0.608 · 客户端 0.000        ⇒ 阈值取 0.30
//    · 与客户端**镜像后**的墨迹一致率 0.954,原样只有 0.524 ⇒ 差值阈值取 0.15
//    ★ 钉「镜像后更像」而不是钉某个具体一致率:后者换一版画就得改数,
//      而"镜像成立"这件事本身是用户裁定的内容。
// ══════════════════════════════════════════════════════════════════════════════

// ★ csproj 里 `<Using Remove="System.Drawing" />`(WinForms 与 WPF 撞名),所以这里显式起个别名。
using SysDraw = System.Drawing;

namespace LocalAI.Admin;

public static partial class Selftest
{
    static void RunIcon()
    {
        Console.WriteLine("\n-- 管理端图标(V29 · 实机反馈⑩)--");

        // ── ⓪ 元断言:**取到图了**。★★ 它只说得起这么多 ────────────────────
        //  ★★★ V29b 更正一条判词:上一版这里写的是「取不到就说明 `<ApplicationIcon>` 没生效」,
        //    而**那件事它根本不检验**。实测(自己建了个没有 `<ApplicationIcon>` 的 exe):
        //    `ExtractAssociatedIcon` 对没有自己图标的 exe **不返回 null**,
        //    它返回 **shell 的默认 exe 图标** —— 非空、32×32、偏红占比 0.0%。
        //    ⇒ `is not null` 近似恒真,它只配当一条**元断言**:下面几条有没有图可比。
        //  ★ 真正守着「静默退回默认图标」的是下一条。而那个缺陷的样子也**不是**
        //    走 `?? SystemIcons.Application` 那一支 —— 是**取到一个通用图标、非空、
        //    看起来和以前一模一样**。判词现在说的是这件事。
        var mine = TrayIconBitmap(Environment.ProcessPath);
        Assert(mine is not null,
            "★ 元断言:从本进程 exe 里**取到了一张图**(走的就是托盘那一句 "
            + "`Icon.ExtractAssociatedIcon(ProcessPath)`)—— 取不到的话下面几条是零断言。"
            + "★★ 它**不**证明 `<ApplicationIcon>` 生效了:没有自己图标的 exe 也会拿到 "
            + "shell 的默认图标(实测非空、32×32、红占比 0.0%),那一半由下一条守");
        if (mine is null) return;

        // ① 红色:用户裁定「红色 + 镜像」。★ 钉的是**看得见的红**,不是"我设了个红色常量"。
        var red = RedShare(mine);
        Assert(red > 0.30,
            $"★★★★ 图标**是红的**(不透明像素里偏红的占 {red:P1},阈值 30%)—— "
            + "★ 这一条才是真正守着那个缺陷的:`<ApplicationIcon>` 一旦没生效(比如那一行被删掉、"
            + "或者 .ico 路径写错),exe 会**静默退回 shell 的默认图标** —— "
            + "非空、看起来和 V29 之前一模一样,而红占比会掉到 0.0%。"
            + "★★ 用 V29 之前那个 exe 实测过:这一条当场红");

        // ② 镜像:与客户端那只**镜像之后**才对得上。
        var theirs = ClientIconBitmap();
        if (theirs is null)
        {
            // ══════════════════════════════════════════════════════════════
            //  ★★★ V29b 从 `Skip` 改成 `Owed` —— 上一版记 Skip 是 **fail-open**。
            //
            //  `Selftest.cs` 那条口径逐字:
            //    · Skip —— 「**这个形态下本来就测不了**」(恒常发生,判红=天天误报);
            //    · Owed —— 「**本该跑得了,却没跑成**」(它不该发生,发生了就该红)。
            //  而这一条的两条取图路都是**本该有**的:
            //    · 出包形态:`dist\admin\` 与 `dist\client\` 是**并排出的**(ClientLink 的布局),
            //      取不到就说明包缺了一半;
            //    · 仓库形态:`app/Assets/icon/icon-32x32.png` 在版本库里。
            //  ⇒ 两条都落空 = 这台机器上的形态坏了,不是"这里本来就测不了"。记 Owed,门禁看它判红。
            //
            //  ★ 损失可控但方向是错的:红占比那条排在 return 之前,所以"是不是红的"仍然测过了;
            //    没测的是「与客户端分不分得开」—— 而那正是 `RealCloseAsync` 那条路的全部依靠。
            // ══════════════════════════════════════════════════════════════
            Owed("★★ 「与客户端那只是镜像关系 / 两个托盘图标分得开」",
                 "旁边既没有客户端 exe(`..\\client\\localai-client.exe`),"
                 + "也没有源码里的 `app/Assets/icon/icon-32x32.png`。"
                 + "★ 这两条**本该有一条在**(出包时两个 exe 并排出、仓库里那张 PNG 在版本库里)"
                 + " ⇒ 记 OWED 不记 SKIP:**这一条没跑**,而且它不该跑不了");
            return;
        }

        var asIs = InkAgreement(mine, theirs, mirror: false);
        var mirrored = InkAgreement(mine, theirs, mirror: true);
        Assert(mirrored - asIs > 0.15,
            $"★★★ 与客户端那只是**镜像**关系(镜像后墨迹一致 {mirrored:P1},原样只有 {asIs:P1})—— "
            + "★ 这条钉得住的前提是原图**不对称**(星芒压一侧、一只耳朵有缺口);"
            + "图案若对称,镜像等于什么都没做,两个数会一样高");

        // ③ 承重的那一条:两个托盘图标**不许长得一样**。
        //   ★ 上面两条都成立时它自然成立 —— 但它是**用户会不会点错**那件事本身,
        //     值得单独有一句话在输出里,而不是让人从两条间接判据里推。
        Assert(InkAgreement(mine, theirs, mirror: false) < 0.95 || red > 0.30,
            "★★★★ 管理端与客户端的托盘图标**分得开** —— "
            + "分不开的后果不是难看:`RealCloseAsync`(唯一的真关闭入口)全靠用户在托盘里认出它,"
            + "认错就把另一个程序关了");
    }

    // ── 取图 ──────────────────────────────────────────────────────────────
    static SysDraw.Bitmap? TrayIconBitmap(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return null;
        try
        {
            using var ic = SysDraw.Icon.ExtractAssociatedIcon(exePath);
            return ic?.ToBitmap();
        }
        catch { return null; }
    }

    /// <summary>
    /// 客户端那只 —— 两条路:① 旁边真有客户端 exe(出包形态);② 仓库里的原始 PNG(开发形态)。
    /// ★ 两条都没有就返回 null,由调用方**登记一条看得见的 SKIP**,不静默跳过。
    /// </summary>
    static SysDraw.Bitmap? ClientIconBitmap()
    {
        var exe = Services.ClientLink.ClientExePath();
        if (exe is not null && TrayIconBitmap(exe) is { } fromExe) return fromExe;

        var root = AdminSourceRoot();
        if (root is null) return null;
        var png = Path.GetFullPath(Path.Combine(root, "..", "app", "Assets", "icon", "icon-32x32.png"));
        try { return File.Exists(png) ? new SysDraw.Bitmap(png) : null; }
        catch { return null; }
    }

    // ── 两个度量 ──────────────────────────────────────────────────────────
    /// <summary>不透明像素里「明显偏红」的占比。★ 阈值 +40 是为了把灰与暗色排除掉,不是随手取的。</summary>
    static double RedShare(SysDraw.Bitmap bmp)
    {
        int tot = 0, red = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.A < 128) continue;
                tot++;
                if (p.R > p.G + 40 && p.R > p.B + 40) red++;
            }
        return tot == 0 ? 0 : (double)red / tot;
    }

    /// <summary>
    /// 两张图的**墨迹**一致率(32×32 上比)。★ 比的是"哪儿有墨"而不是颜色 ——
    /// 一边是黑、一边是红,直接比颜色的话两张永远不像,而要问的是**形状**是不是镜像。
    /// </summary>
    static double InkAgreement(SysDraw.Bitmap a, SysDraw.Bitmap b, bool mirror)
    {
        const int N = 32;
        var ma = InkMask(a, N, false);
        var mb = InkMask(b, N, mirror);
        int same = 0;
        for (int i = 0; i < ma.Length; i++) if (ma[i] == mb[i]) same++;
        return (double)same / ma.Length;
    }

    static bool[] InkMask(SysDraw.Bitmap src, int n, bool mirror)
    {
        using var small = new SysDraw.Bitmap(src, new SysDraw.Size(n, n));
        var m = new bool[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                var p = small.GetPixel(mirror ? n - 1 - x : x, y);
                m[y * n + x] = p.A > 128 && (p.R + p.G + p.B) / 3 < 190;   // 不透明且不接近白 = 有墨
            }
        return m;
    }
}
