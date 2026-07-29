// P3c -- 响应式布局度量。用户提出的三个畸变风险都收敛到这里,**做成纯函数**以便自动回归
// (界面本身我没法肉眼验,但这些决策可以逐尺寸断言):
//
//   ① 项目方块要【平分横向空间】,不能右侧留一大块空白 -> ProjectColumns()
//   ② 全屏与最小窗口都不能畸变(实例:窗口过小天气曲线显示不全)-> Density() + 各处按密度取舍
//   ③ 简报/待办若一长一短,不能一个大片留白、另一个过于紧凑 -> PanelMaxHeight() 限高 + 内部滚动
//
// 设计基线(§3):适配 1440×900,另备 1280×720 与紧凑密度。

namespace LocalAI.Client.Views;

public enum Density { Comfortable, Compact, Tight }

public static class Layout
{
    // 外壳固定开销:自绘标题栏 38 + 页面顶栏 54;左导航展开 240 / 收起 58。
    // 显式写出来,免得调用方与测试各猜一套(曾因此把整窗尺寸当成内容区尺寸)。
    public const double TitleBarHeight = 38;
    public const double TopBarHeight = 54;
    public const double NavWidthExpanded = 240;
    public const double NavWidthCollapsed = 58;

    /// <summary>由窗口尺寸推出主页内容区尺寸(密度判据用的是内容区,不是整窗)。</summary>
    public static (double W, double H) ContentSize(double windowW, double windowH, bool navCollapsed = false)
        => (Math.Max(0, windowW - (navCollapsed ? NavWidthCollapsed : NavWidthExpanded)),
            Math.Max(0, windowH - TitleBarHeight - TopBarHeight));

    /// <summary>方块的理想宽度。实际宽度由 UniformGrid 平分,所以这只是"多宽算一列"的判据。</summary>
    public const double TileIdealWidth = 210;
    public const int MinTileColumns = 2;
    public const int MaxTileColumns = 8;

    /// <summary>
    /// 项目田字格的列数。★ 关键:算出列数后交给 UniformGrid **平分**可用宽度,
    /// 方块随之拉伸填满 —— 而不是固定宽度靠 WrapPanel 排,那样右侧必然留下一条空白。
    /// </summary>
    public static int ProjectColumns(double availableWidth)
    {
        if (double.IsNaN(availableWidth) || availableWidth <= 0) return MinTileColumns;
        var n = (int)Math.Floor(availableWidth / TileIdealWidth);
        return Math.Clamp(n, MinTileColumns, MaxTileColumns);
    }

    /// <summary>
    /// 按内容区尺寸判定密度。高度是主要判据 —— 天气卡里"曲线 + 逐小时"最先被挤掉,
    /// 用户遇到的"窗口过小曲线显示不全"就是这个。
    /// </summary>
    public static Density For(double width, double height)
    {
        if (height < 560 || width < 900) return Density.Tight;
        if (height < 760 || width < 1150) return Density.Compact;
        return Density.Comfortable;
    }

    /// <summary>逐小时天气的格数:窄了就少给几格,而不是把每格挤到看不清。</summary>
    public static int HourlySlots(Density d, double cardWidth) => d switch
    {
        Density.Tight => 0,                                   // 极小窗口:整行隐藏(留曲线更有用)
        Density.Compact => cardWidth < 260 ? 3 : 4,
        _ => cardWidth < 300 ? 4 : 6,
    };

    /// <summary>气温曲线区的高度。返回 0 表示这个尺寸下不显示曲线(与其显示不全,不如不显示)。</summary>
    public static double CurveHeight(Density d) => d switch
    {
        Density.Tight => 0,
        Density.Compact => 34,
        _ => 48,
    };

    /// <summary>
    /// 简报/待办这类并列板块的最大高度。限高 + 内部滚动 = 一侧内容再长也不会
    /// 把另一侧撑出大片留白,更不会把下面的天气/项目挤变形。
    /// </summary>
    public static double PanelMaxHeight(Density d) => d switch
    {
        Density.Tight => 92,
        Density.Compact => 116,
        _ => 140,
    };

    /// <summary>右侧日历栏宽度。窄窗口下收窄,极窄时由调用方整栏隐藏。</summary>
    public static double CalendarWidth(Density d) => d switch
    {
        Density.Tight => 0,        // 0 = 不显示(横向已经不够分)
        Density.Compact => 280,
        _ => 330,
    };

    /// <summary>项目方块高度:紧凑时压低,保证一行方块仍完整可见。</summary>
    public static double TileHeight(Density d) => d switch
    {
        Density.Tight => 96,
        Density.Compact => 116,
        _ => 132,
    };
}
