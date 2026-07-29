// P3c -- 响应式布局度量。做成纯函数以便自动回归(界面本身无法肉眼验,但这些决策可以逐尺寸断言)。
//
// ★ 丝滑的关键(用户反馈"缩放跳来跳去"):
//   ① 能连续的就【连续插值】,不要分档 —— 曲线高度、日历栏宽度、限高都随尺寸平滑变化;
//   ② 必须离散的(列数、逐小时格数)加【迟滞】—— 增/减用不同阈值,避免在边界反复横跳;
//   ③ 窗口最小尺寸提高到【屏幕的四分之一】(2K→1280×720,HD→960×540),
//      于是极端紧凑档几乎不会触发,少了一整类跳变。
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

    /// <summary>
    /// 窗口最小尺寸 = 屏幕的【四分之一大小】(面积的四分之一 = 宽高各一半)。
    /// 2560×1440 -> 1280×720;1920×1080 -> 960×540。用户裁定;最大值 = 全屏(由工作区给出)。
    /// </summary>
    public static (double W, double H) MinWindowFor(double screenW, double screenH)
        => (Math.Max(960, Math.Round(screenW / 2)), Math.Max(540, Math.Round(screenH / 2)));

    /// <summary>
    /// 首次打开的建议窗口尺寸 —— 取"主页内容刚好放得下"的高度,这样【一开始就不出滚动条】(用户裁定)。
    /// 估算(与 HomeView 的行结构对应):
    ///   标题栏 38 + 顶栏 54 + 页边距 32 + 问候(大字号+留白)~110 + 日历/待办 ~342 + 天气 ~220
    ///   + 项目一行 ~150 ≈ 946,再留余量给不同 DPI 的字体高度差与面板内边距 -> 1000。
    /// 之前用 940 仍会出滚动条(问候块加大后更高了),据此上调。
    /// 会被工作区与最小尺寸夹住,小屏上不会超出。
    /// </summary>
    public const double PreferredWindowWidth = 1480;
    public const double PreferredWindowHeight = 1000;

    // ---------------------------------------------------------------- 连续量(不分档,不跳)
    static double Lerp(double a, double b, double t) => a + (b - a) * Math.Clamp(t, 0, 1);

    /// <summary>把一个尺寸映射到 0..1 的"宽裕度"。用它驱动所有连续量。</summary>
    static double Roominess(double value, double tight, double roomy)
        => Math.Clamp((value - tight) / Math.Max(1, roomy - tight), 0, 1);

    /// <summary>气温曲线高度:随内容区高度【连续】变化(28→56),不再一跳一跳。</summary>
    public static double CurveHeight(double contentH)
        => Math.Round(Lerp(28, 56, Roominess(contentH, 520, 900)));

    /// <summary>右侧日历栏宽度:随内容区宽度【连续】变化(264→352)。</summary>
    public static double CalendarWidth(double contentW)
        => Math.Round(Lerp(264, 352, Roominess(contentW, 780, 1500)));

    /// <summary>简报/待办的限高:连续变化(96→168)。限高 + 内部滚动 = 一长一短也不畸变。</summary>
    public static double PanelMaxHeight(double contentH)
        => Math.Round(Lerp(96, 168, Roominess(contentH, 520, 950)));

    /// <summary>项目方块高度:连续变化(104→140)。</summary>
    public static double TileHeight(double contentH)
        => Math.Round(Lerp(104, 140, Roominess(contentH, 520, 950)));

    // ---------------------------------------------------------------- 离散量(带迟滞,不横跳)
    public const double TileIdealWidth = 210;
    public const int MinTileColumns = 2;
    public const int MaxTileColumns = 8;

    /// <summary>迟滞带宽:要多超出边界这么多像素才肯改变档位,避免在阈值附近反复切换。</summary>
    public const double Hysteresis = 34;

    /// <summary>
    /// 项目田字格列数。算出后交给 UniformGrid【平分】可用宽度,方块随之拉伸填满。
    /// current 传入当前列数以启用迟滞:只有明显越过边界才改,拖动时不会来回抖。
    /// </summary>
    /// <param name="itemCount">项目总数。列数【不能超过它】—— 否则多出来的空列就是右侧一块空白。</param>
    public static int ProjectColumns(double availableWidth, int current = 0, int itemCount = int.MaxValue)
    {
        if (double.IsNaN(availableWidth) || availableWidth <= 0) return Math.Max(current, MinTileColumns);
        var cap = Math.Clamp(itemCount, MinTileColumns, MaxTileColumns);
        var raw = Math.Clamp((int)Math.Floor(availableWidth / TileIdealWidth), MinTileColumns, cap);
        if (current < MinTileColumns || current > MaxTileColumns) return raw;
        if (raw == current) return current;

        // 变多:要宽到"再多一列还绰绰有余"才升;变少:要窄到明显放不下才降。
        if (current > cap) return cap;   // 项目变少了要立刻收,不走迟滞(否则留空列)
        if (raw > current && availableWidth < (current + 1) * TileIdealWidth + Hysteresis) return current;
        if (raw < current && availableWidth > current * TileIdealWidth - Hysteresis) return current;
        return raw;
    }

    // 逐小时天气:每格约需 46px。宽度富余时【加格数 + 缩小时间间隔】,
    // 而不是把 6 个格子撑得又宽又空 —— 用户反馈全屏时天气多出很多宽度没被利用。
    const double HourlySlotWidth = 46;
    public const int MinHourlySlots = 3;
    public const int MaxHourlySlots = 12;

    /// <summary>逐小时格数,带迟滞。窄了少给几格;宽了多给几格(配合更细的间隔)。</summary>
    public static int HourlySlots(double cardWidth, int current = 0)
    {
        if (double.IsNaN(cardWidth) || cardWidth <= 0) return Math.Max(current, MinHourlySlots);
        var raw = Math.Clamp((int)Math.Floor(cardWidth / HourlySlotWidth), MinHourlySlots, MaxHourlySlots);
        if (current is < MinHourlySlots or > MaxHourlySlots) return raw;
        if (raw == current) return current;
        // 迟滞:要明显越过边界才改档,避免拖动时格数抖动
        if (raw > current && cardWidth < (current + 1) * HourlySlotWidth + Hysteresis) return current;
        if (raw < current && cardWidth > current * HourlySlotWidth - Hysteresis) return current;
        return raw;
    }

    /// <summary>
    /// 逐小时的【时间间隔】(小时)。格子多了就把间隔调细 —— 格数 × 间隔 大致覆盖未来 12–24 小时,
    /// 这样多出来的宽度换来的是【更细的时间粒度】,而不是更空的格子。
    /// </summary>
    public static int HourlyStepHours(int slots)
        => slots >= 12 ? 1 : slots >= 8 ? 2 : 3;

    /// <summary>
    /// 密度仍保留,但只用于极端兜底(窗口最小值已提到屏幕四分之一,正常几乎不会到 Tight)。
    /// </summary>
    public static Density For(double width, double height)
    {
        if (height < 480 || width < 760) return Density.Tight;
        if (height < 700 || width < 1000) return Density.Compact;
        return Density.Comfortable;
    }
}
