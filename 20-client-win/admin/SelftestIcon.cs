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

        var mine = TrayIconBitmap(Environment.ProcessPath);
        Assert(mine is not null,
            "★★★ 管理端 exe **自己带着图标** —— 走的是托盘那一句 "
            + "`Icon.ExtractAssociatedIcon(ProcessPath)`,取不到就说明 `<ApplicationIcon>` 没生效;"
            + "在 V29 之前这里**根本没有那一行**,exe 与任务栏用的是 .NET 默认图标");
        if (mine is null) return;

        // ① 红色:用户裁定「红色 + 镜像」。★ 钉的是**看得见的红**,不是"我设了个红色常量"。
        var red = RedShare(mine);
        Assert(red > 0.30,
            $"★★★ 图标**是红的**(不透明像素里偏红的占 {red:P1},阈值 30%)—— "
            + "客户端那只是纯黑白(实测 0.0%),两个托盘图标靠颜色一眼分得开");

        // ② 镜像:与客户端那只**镜像之后**才对得上。
        var theirs = ClientIconBitmap();
        if (theirs is null)
        {
            Skip("★★ 「与客户端那只是镜像关系」",
                 "这一趟旁边既没有客户端 exe(`..\\client\\localai-client.exe`),"
                 + "也没有源码里的 `app/Assets/icon/icon-32x32.png` —— 没有可比的另一半,"
                 + "**这一条没跑**,不要读成「镜像没问题」");
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
