// P3c -- 图标系统。用户裁定:图标【跟随皮肤】——
//   墨白 Ink   = 黑白线性(用户指定的那套极简线性理念)
//   微风 Breeze = 苹果风线性(更圆润的端点、细描边)
//   暖萌 Warm  = 可爱风(圆润实心 + 更饱满的造型)
//
// 实现方式:自绘矢量路径(Geometry),不依赖字体图标。
// 理由:① Segoe MDL2 只有一套造型,做不出三皮肤差异;② 矢量随 DPI/尺寸无损;
//      ③ 项目零第三方依赖惯例;④ 描边 vs 实心正好承载"线性 vs 可爱"的差异。
//
// 每个图标给三份路径数据 + 一个渲染模式(描边 / 实心)。皮肤切换时整套跟着换。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using LocalAI.Client.Services;

namespace LocalAI.Client.Theme;

public enum IconName
{
    Home, Chat, Assets, Translation, Courses, Computer, Investment,
    Finance, Model, Folder, Ai, File, Pdf, Search, Extensions, Settings, Devices, Calendar, Tasks, Weather, Clock,
    // 天气实况(按 WMO 代码选,见 Icons.ForWeather)
    WxSun, WxPartly, WxCloud, WxFog, WxDrizzle, WxRain, WxSnow, WxShower, WxThunder,
    // 夜间变体:晴 -> 月亮;局部多云 -> 月亮 + 云(用户裁定 2026-08-01)
    WxMoon, WxPartlyNight,
    Menu, Close, Minimize, Maximize, Restore, ChevronRight, Member, Star, Mic, Dots, Refresh,
}

public static class Icons
{
    // 全部路径按 24×24 视框绘制,渲染时按需缩放。
    // Ink/Breeze = 描边(stroke),Warm = 实心(fill)。三套形状各自独立,不是同一份换个粗细。

    static readonly Dictionary<IconName, string> Line = new()   // 墨白:几何、直角、等宽
    {
        [IconName.Home] = "M3 11 L12 3 L21 11 M5.5 9.5 V20 H18.5 V9.5",
        [IconName.Chat] = "M3.5 5 H20.5 V16 H12 L7 20 V16 H3.5 Z",
        [IconName.Assets] = "M3.5 4.5 H20.5 V19.5 H3.5 Z M3.5 15 L9 10 L13 14 L16.5 11 L20.5 15",
        [IconName.Translation] = "M3 6 H11 M7 6 V4 M4.5 6 C4.5 12 8 15 11 16 M9.5 16 C12 14 13.5 10.5 13.5 6 M12.5 20 L16.5 10 L20.5 20 M14 17 H19",
        [IconName.Courses] = "M3.5 5 H20.5 V16.5 H3.5 Z M9 20 H15 M12 16.5 V20 M7 8.5 H17 M7 11.5 H13",
        [IconName.Computer] = "M4 5.5 H20 V15.5 H4 Z M8 19 H16 M12 15.5 V19",
        [IconName.Investment] = "M3.5 18.5 H20.5 M6 15 V11 M10.5 15 V7 M15 15 V13 M19.5 15 V5",
        [IconName.Finance] = "M4 7.5 H20 V17.5 H4 Z M4 10.5 H20 M16 14 A1.2 1.2 0 1 0 15.99 14",
        [IconName.Model] = "M7.5 7.5 H16.5 V16.5 H7.5 Z M4 10 H7.5 M4 14 H7.5 M16.5 10 H20 M16.5 14 H20 M10 4 V7.5 M14 4 V7.5 M10 16.5 V20 M14 16.5 V20",
        [IconName.Folder] = "M3.5 9 V7 H9 L11 9 H20.5 V18 H3.5 Z",
        [IconName.Ai] = "M12 3 L13.8 9.4 L20 11.5 L13.8 13.6 L12 20 L10.2 13.6 L4 11.5 L10.2 9.4 Z",
        [IconName.Star] = "M12 3.5 L14.6 9.3 L21 10.1 L16.3 14.4 L17.6 20.6 L12 17.5 L6.4 20.6 L7.7 14.4 L3 10.1 L9.4 9.3 Z",
        // 同传:麦克风 + 两侧声波(说与听是同一件事的两端)
        [IconName.Mic] = "M9.5 4 H14.5 V12 H9.5 Z M6.5 11 V12 A5.5 5.5 0 0 0 17.5 12 V11 M12 17.5 V20.5 M9 20.5 H15 M3.5 9 V14 M20.5 9 V14",
        // 未定:三个点
        [IconName.Dots] = "M6 12 A0.9 0.9 0 1 0 5.99 12 M12 12 A0.9 0.9 0 1 0 11.99 12 M18 12 A0.9 0.9 0 1 0 17.99 12",
        [IconName.File] = "M7 3.5 H14 L17.5 7 V20.5 H7 Z M14 3.5 V7 H17.5",
        [IconName.Search] = "M10.5 3.5 A7 7 0 1 0 10.5 17.5 A7 7 0 1 0 10.5 3.5 M15.6 15.6 L20.5 20.5",
        [IconName.Pdf] = "M7 3.5 H14 L17.5 7 V20.5 H7 Z M14 3.5 V7 H17.5 M9.5 12.5 H15 M9.5 15.5 H15",
        [IconName.Extensions] = "M4.5 4.5 H10.5 V10.5 H4.5 Z M13.5 4.5 H19.5 V10.5 H13.5 Z M4.5 13.5 H10.5 V19.5 H4.5 Z M13.5 13.5 H19.5 V19.5 H13.5 Z",
        [IconName.Settings] = "M12 8.5 A3.5 3.5 0 1 0 12 15.5 A3.5 3.5 0 1 0 12 8.5 M12 3 V5.5 M12 18.5 V21 M3 12 H5.5 M18.5 12 H21 M5.6 5.6 L7.4 7.4 M16.6 16.6 L18.4 18.4 M18.4 5.6 L16.6 7.4 M7.4 16.6 L5.6 18.4",
        [IconName.Devices] = "M3.5 5.5 H14.5 V14 H3.5 Z M7 18 H11 M9 14 V18 M16.5 8.5 H20.5 V18.5 H16.5 Z",
        [IconName.Calendar] = "M4 6 H20 V20 H4 Z M4 10 H20 M8 4 V7.5 M16 4 V7.5",
        [IconName.Tasks] = "M4 7 H8 V11 H4 Z M4 14 H8 V18 H4 Z M11 9 H20 M11 16 H20",
        [IconName.Weather] = "M7.5 17 A4 4 0 0 1 7.8 9.1 A5.2 5.2 0 0 1 17.5 10 A3.5 3.5 0 0 1 17 17 Z",
        [IconName.Clock] = "M12 3.5 A8.5 8.5 0 1 0 12 20.5 A8.5 8.5 0 1 0 12 3.5 M12 7 V12 L15.5 14",
        [IconName.Refresh] = "M20 12 A8 8 0 1 1 17.2 6 M17.2 6 V4 M17.2 6 H14.4",
        [IconName.Menu] = "M4 7 H20 M4 12 H20 M4 17 H20",
        [IconName.Close] = "M6 6 L18 18 M18 6 L6 18",
        [IconName.Minimize] = "M5 12 H19",
        [IconName.Maximize] = "M5 5 H19 V19 H5 Z",
        [IconName.Restore] = "M7.5 7.5 H16.5 V16.5 H7.5 Z M9.5 7.5 V5.5 H18.5 V14.5 H16.5",
        [IconName.ChevronRight] = "M9.5 5.5 L16 12 L9.5 18.5",
        [IconName.Member] = "M12 4.5 A3.8 3.8 0 1 0 12 12.1 A3.8 3.8 0 1 0 12 4.5 M4.5 20 C4.5 15.8 8 14 12 14 C16 14 19.5 15.8 19.5 20",
    };

    static readonly Dictionary<IconName, string> Apple = new()  // 微风:圆润、简洁、留白多
    {
        [IconName.Home] = "M4 10.8 L12 4.2 L20 10.8 M6.4 9.2 V18.4 A1.4 1.4 0 0 0 7.8 19.8 H16.2 A1.4 1.4 0 0 0 17.6 18.4 V9.2",
        [IconName.Chat] = "M12 4.6 C7 4.6 3.6 7.6 3.6 11.4 C3.6 13.6 4.8 15.5 6.7 16.7 L6 20 L9.7 18.1 C10.4 18.2 11.2 18.3 12 18.3 C17 18.3 20.4 15.3 20.4 11.4 C20.4 7.6 17 4.6 12 4.6 Z",
        [IconName.Assets] = "M5.5 4.8 H18.5 A1.7 1.7 0 0 1 20.2 6.5 V17.5 A1.7 1.7 0 0 1 18.5 19.2 H5.5 A1.7 1.7 0 0 1 3.8 17.5 V6.5 A1.7 1.7 0 0 1 5.5 4.8 Z M3.8 15.5 L8.8 10.8 L12.6 14.3 L15.8 11.4 L20.2 15.4 M14.8 8.6 A1.1 1.1 0 1 0 14.81 8.6",
        [IconName.Translation] = "M3.6 6.4 H10.8 M7.2 6.4 V4.4 M4.9 6.4 C4.9 11.6 8 14.6 10.8 15.6 M9.4 15.6 C11.7 13.7 13 10.5 13 6.4 M12.8 19.6 L16.4 10.6 L20 19.6 M14.1 16.9 H18.7",
        [IconName.Courses] = "M5.5 5 H18.5 A1.5 1.5 0 0 1 20 6.5 V15 A1.5 1.5 0 0 1 18.5 16.5 H5.5 A1.5 1.5 0 0 1 4 15 V6.5 A1.5 1.5 0 0 1 5.5 5 Z M9.5 19.6 H14.5 M12 16.5 V19.6",
        [IconName.Computer] = "M5.6 5.4 H18.4 A1.6 1.6 0 0 1 20 7 V14.4 A1.6 1.6 0 0 1 18.4 16 H5.6 A1.6 1.6 0 0 1 4 14.4 V7 A1.6 1.6 0 0 1 5.6 5.4 Z M8.4 19.2 H15.6",
        [IconName.Investment] = "M4 17.6 C7 17.6 8.4 8.8 11.6 8.8 C14.4 8.8 14.6 13.6 17 13.6 C18.8 13.6 19.4 10.4 20 6.8 M16.4 6.4 H20.2 V10.2",
        [IconName.Finance] = "M6 7.5 H18 A2 2 0 0 1 20 9.5 V15.5 A2 2 0 0 1 18 17.5 H6 A2 2 0 0 1 4 15.5 V9.5 A2 2 0 0 1 6 7.5 Z M4 11.4 H20 M16.2 14.2 A1.15 1.15 0 1 0 16.19 14.2",
        [IconName.Model] = "M8 7.6 H16 A0.4 0.4 0 0 1 16.4 8 V16 A0.4 0.4 0 0 1 16 16.4 H8 A0.4 0.4 0 0 1 7.6 16 V8 A0.4 0.4 0 0 1 8 7.6 Z M4.5 10 H7.6 M4.5 14 H7.6 M16.4 10 H19.5 M16.4 14 H19.5 M10 4.5 V7.6 M14 4.5 V7.6 M10 16.4 V19.5 M14 16.4 V19.5",
        [IconName.Folder] = "M4 9 V7.6 A1.4 1.4 0 0 1 5.4 6.2 H8.8 A1.4 1.4 0 0 1 9.9 6.7 L11.4 8.4 H18.6 A1.4 1.4 0 0 1 20 9.8 V16.6 A1.4 1.4 0 0 1 18.6 18 H5.4 A1.4 1.4 0 0 1 4 16.6 Z",
        [IconName.Star] = "M12 4 L14.4 9.3 L20.2 10 L15.9 13.9 L17.1 19.6 L12 16.8 L6.9 19.6 L8.1 13.9 L3.8 10 L9.6 9.3 Z",
        [IconName.Mic] = "M12 3.8 A2.5 2.5 0 0 1 14.5 6.3 V11.6 A2.5 2.5 0 0 1 9.5 11.6 V6.3 A2.5 2.5 0 0 1 12 3.8 Z M6.8 11.6 A5.2 5.2 0 0 0 17.2 11.6 M12 16.8 V20.4 M9.2 20.4 H14.8 M3.6 9.4 A6 6 0 0 0 3.6 14 M20.4 9.4 A6 6 0 0 1 20.4 14",
        [IconName.Dots] = "M6.2 12 A1 1 0 1 0 6.19 12 M12 12 A1 1 0 1 0 11.99 12 M17.8 12 A1 1 0 1 0 17.79 12",
        [IconName.Ai] = "M12 3.4 C12.6 7.2 14.8 9.4 18.6 10 C14.8 10.6 12.6 12.8 12 16.6 C11.4 12.8 9.2 10.6 5.4 10 C9.2 9.4 11.4 7.2 12 3.4 Z M17.4 15 C17.7 16.6 18.4 17.3 20 17.6 C18.4 17.9 17.7 18.6 17.4 20.2 C17.1 18.6 16.4 17.9 14.8 17.6 C16.4 17.3 17.1 16.6 17.4 15 Z",
        [IconName.Search] = "M10.6 4 A6.6 6.6 0 1 0 10.6 17.2 A6.6 6.6 0 1 0 10.6 4 M15.4 15.4 L20 20",
        [IconName.File] = "M7 3.6 H13.8 A0.8 0.8 0 0 1 14.4 3.9 L17.2 6.7 A0.8 0.8 0 0 1 17.5 7.3 V19.6 A0.9 0.9 0 0 1 16.6 20.5 H7.8 A0.9 0.9 0 0 1 6.9 19.6 V4.5 A0.9 0.9 0 0 1 7 3.6 Z M13.8 3.6 V6.6 A0.8 0.8 0 0 0 14.6 7.4 H17.5",
        [IconName.Pdf] = "M7 3.6 H13.8 A0.8 0.8 0 0 1 14.4 3.9 L17.2 6.7 A0.8 0.8 0 0 1 17.5 7.3 V19.6 A0.9 0.9 0 0 1 16.6 20.5 H7.8 A0.9 0.9 0 0 1 6.9 19.6 V4.5 A0.9 0.9 0 0 1 7 3.6 Z M13.8 3.6 V6.6 A0.8 0.8 0 0 0 14.6 7.4 H17.5 M9.6 12.6 H14.9 M9.6 15.4 H14.9",
        [IconName.Extensions] = "M5.6 4.8 H9.6 A0.8 0.8 0 0 1 10.4 5.6 V9.6 A0.8 0.8 0 0 1 9.6 10.4 H5.6 A0.8 0.8 0 0 1 4.8 9.6 V5.6 A0.8 0.8 0 0 1 5.6 4.8 Z M14.4 4.8 H18.4 A0.8 0.8 0 0 1 19.2 5.6 V9.6 A0.8 0.8 0 0 1 18.4 10.4 H14.4 A0.8 0.8 0 0 1 13.6 9.6 V5.6 A0.8 0.8 0 0 1 14.4 4.8 Z M5.6 13.6 H9.6 A0.8 0.8 0 0 1 10.4 14.4 V18.4 A0.8 0.8 0 0 1 9.6 19.2 H5.6 A0.8 0.8 0 0 1 4.8 18.4 V14.4 A0.8 0.8 0 0 1 5.6 13.6 Z M16.4 13.4 A2.9 2.9 0 1 0 16.41 13.4",
        [IconName.Settings] = "M12 9 A3 3 0 1 0 12 15 A3 3 0 1 0 12 9 M19.2 12 A7.2 7.2 0 0 0 19.1 10.8 L20.9 9.5 L19.3 6.7 L17.2 7.5 A7.2 7.2 0 0 0 15.1 6.3 L14.8 4.1 H11.6 L11.3 6.3 A7.2 7.2 0 0 0 9.2 7.5 L7.1 6.7 L5.5 9.5 L7.3 10.8 A7.2 7.2 0 0 0 7.3 13.2 L5.5 14.5 L7.1 17.3 L9.2 16.5 A7.2 7.2 0 0 0 11.3 17.7 L11.6 19.9 H14.8 L15.1 17.7 A7.2 7.2 0 0 0 17.2 16.5 L19.3 17.3 L20.9 14.5 L19.1 13.2 Z",
        [IconName.Devices] = "M4.8 5.6 H13.6 A1.2 1.2 0 0 1 14.8 6.8 V13.2 A1.2 1.2 0 0 1 13.6 14.4 H4.8 A1.2 1.2 0 0 1 3.6 13.2 V6.8 A1.2 1.2 0 0 1 4.8 5.6 Z M7.2 18 H11.2 M17.6 8.4 H19.6 A0.9 0.9 0 0 1 20.5 9.3 V17.7 A0.9 0.9 0 0 1 19.6 18.6 H17.6 A0.9 0.9 0 0 1 16.7 17.7 V9.3 A0.9 0.9 0 0 1 17.6 8.4 Z",
        [IconName.Calendar] = "M5.6 6.4 H18.4 A1.6 1.6 0 0 1 20 8 V18.4 A1.6 1.6 0 0 1 18.4 20 H5.6 A1.6 1.6 0 0 1 4 18.4 V8 A1.6 1.6 0 0 1 5.6 6.4 Z M4 10.4 H20 M8.4 4.4 V8 M15.6 4.4 V8",
        [IconName.Tasks] = "M4.4 7.6 L6.2 9.4 L9.4 6.2 M4.4 15.6 L6.2 17.4 L9.4 14.2 M12.4 8 H19.6 M12.4 16 H19.6",
        [IconName.Weather] = "M7.6 17.2 A3.9 3.9 0 0 1 8 9.3 A5.2 5.2 0 0 1 17.4 10.2 A3.5 3.5 0 0 1 16.9 17.2 Z",
        [IconName.Clock] = "M12 3.8 A8.2 8.2 0 1 0 12 20.2 A8.2 8.2 0 1 0 12 3.8 M12 7.4 V12.2 L15.4 14",
        [IconName.Refresh] = "M19.8 12 A7.8 7.8 0 1 1 17.1 6.2 M17.1 6.2 V4.2 M17.1 6.2 H14.3",
        [IconName.Menu] = "M4.4 7.4 H19.6 M4.4 12 H19.6 M4.4 16.6 H19.6",
        [IconName.Close] = "M6.6 6.6 L17.4 17.4 M17.4 6.6 L6.6 17.4",
        [IconName.Minimize] = "M5.4 12 H18.6",
        [IconName.Maximize] = "M6.4 5.6 H17.6 A0.8 0.8 0 0 1 18.4 6.4 V17.6 A0.8 0.8 0 0 1 17.6 18.4 H6.4 A0.8 0.8 0 0 1 5.6 17.6 V6.4 A0.8 0.8 0 0 1 6.4 5.6 Z",
        [IconName.Restore] = "M8 8 H16 A0.8 0.8 0 0 1 16.8 8.8 V16.8 A0.8 0.8 0 0 1 16 17.6 H8 A0.8 0.8 0 0 1 7.2 16.8 V8.8 A0.8 0.8 0 0 1 8 8 Z M10 8 V6.4 A0.8 0.8 0 0 1 10.8 5.6 H17.6 A0.8 0.8 0 0 1 18.4 6.4 V13.2 A0.8 0.8 0 0 1 17.6 14 H16.8",
        [IconName.ChevronRight] = "M9.8 5.8 L16 12 L9.8 18.2",
        [IconName.Member] = "M12 4.6 A3.7 3.7 0 1 0 12 12 A3.7 3.7 0 1 0 12 4.6 M5 19.8 C5 16 8.2 13.8 12 13.8 C15.8 13.8 19 16 19 19.8",
    };

    static readonly Dictionary<IconName, string> Cute = new()   // 暖萌:圆滚滚、实心为主
    {
        [IconName.Home] = "M12 3.2 L2.8 11 A1.2 1.2 0 0 0 4.4 12.9 L5 12.4 V18.6 A2.4 2.4 0 0 0 7.4 21 H16.6 A2.4 2.4 0 0 0 19 18.6 V12.4 L19.6 12.9 A1.2 1.2 0 0 0 21.2 11 Z",
        [IconName.Chat] = "M12 4 C6.8 4 3 7.1 3 11.1 C3 13.3 4.2 15.3 6.1 16.6 L5.3 20.2 A0.6 0.6 0 0 0 6.2 20.8 L9.9 18.6 C10.6 18.7 11.3 18.8 12 18.8 C17.2 18.8 21 15.6 21 11.1 C21 7.1 17.2 4 12 4 Z",
        [IconName.Assets] = "M5 4.4 H19 A2.2 2.2 0 0 1 21.2 6.6 V17.4 A2.2 2.2 0 0 1 19 19.6 H5 A2.2 2.2 0 0 1 2.8 17.4 V6.6 A2.2 2.2 0 0 1 5 4.4 Z M8.6 7.8 A1.6 1.6 0 1 1 8.59 7.8 Z",
        [IconName.Translation] = "M3.4 6.6 A1 1 0 0 1 4.4 5.6 H6.6 V4.6 A1 1 0 0 1 8.6 4.6 V5.6 H10.8 A1 1 0 0 1 10.8 7.6 H9.9 C9.6 10.6 8.6 12.9 7.4 14.4 C8.2 15.1 9 15.6 9.8 15.9 A1 1 0 0 1 9 17.8 C7.9 17.4 6.9 16.7 6 15.9 C5.1 16.7 4.2 17.3 3.4 17.7 A1 1 0 0 1 2.6 15.9 C3.2 15.6 3.9 15.1 4.6 14.5 C4 13.8 3.5 13 3.1 12.1 A1 1 0 0 1 5 11.4 C5.3 12 5.6 12.6 6 13.1 C6.9 12 7.6 10.1 7.9 7.6 H4.4 A1 1 0 0 1 3.4 6.6 Z M16.4 9.4 A1 1 0 0 1 17.3 10 L21.1 19.2 A1 1 0 0 1 19.3 20 L18.4 17.8 H14.4 L13.5 20 A1 1 0 0 1 11.7 19.2 L15.5 10 A1 1 0 0 1 16.4 9.4 Z M15.2 15.8 H17.6 L16.4 12.9 Z",
        [IconName.Courses] = "M5 4.6 H19 A2.2 2.2 0 0 1 21.2 6.8 V14.8 A2.2 2.2 0 0 1 19 17 H13.2 V19 H15.4 A1 1 0 0 1 15.4 21 H8.6 A1 1 0 0 1 8.6 19 H10.8 V17 H5 A2.2 2.2 0 0 1 2.8 14.8 V6.8 A2.2 2.2 0 0 1 5 4.6 Z",
        [IconName.Computer] = "M5 5 H19 A2.2 2.2 0 0 1 21.2 7.2 V14 A2.2 2.2 0 0 1 19 16.2 H13.1 V18 H15.6 A1 1 0 0 1 15.6 20 H8.4 A1 1 0 0 1 8.4 18 H10.9 V16.2 H5 A2.2 2.2 0 0 1 2.8 14 V7.2 A2.2 2.2 0 0 1 5 5 Z",
        [IconName.Investment] = "M3.6 17.4 A1.1 1.1 0 0 1 4.7 16.3 H6.4 V12.4 A1.1 1.1 0 0 1 8.6 12.4 V16.3 H10.4 V8.4 A1.1 1.1 0 0 1 12.6 8.4 V16.3 H14.4 V13.6 A1.1 1.1 0 0 1 16.6 13.6 V16.3 H18.4 V6.2 A1.1 1.1 0 0 1 20.6 6.2 V16.3 A1.1 1.1 0 0 1 20.4 18.5 H4.7 A1.1 1.1 0 0 1 3.6 17.4 Z",
        [IconName.Finance] = "M5 6.4 H19 A2.4 2.4 0 0 1 21.4 8.8 V16.2 A2.4 2.4 0 0 1 19 18.6 H5 A2.4 2.4 0 0 1 2.6 16.2 V8.8 A2.4 2.4 0 0 1 5 6.4 Z M16.6 14 A1.4 1.4 0 1 0 16.59 14 Z",
        [IconName.Model] = "M8 7.4 H16 A1.4 1.4 0 0 1 17.4 8.8 V15.2 A1.4 1.4 0 0 1 16 16.6 H8 A1.4 1.4 0 0 1 6.6 15.2 V8.8 A1.4 1.4 0 0 1 8 7.4 Z M4.6 9.4 H6.6 V10.6 H4.6 Z M4.6 13.4 H6.6 V14.6 H4.6 Z M17.4 9.4 H19.4 V10.6 H17.4 Z M17.4 13.4 H19.4 V14.6 H17.4 Z M9.4 4.6 H10.6 V6.6 H9.4 Z M13.4 4.6 H14.6 V6.6 H13.4 Z M9.4 17.4 H10.6 V19.4 H9.4 Z M13.4 17.4 H14.6 V19.4 H13.4 Z",
        [IconName.Folder] = "M4 8.5 A2 2 0 0 1 6 6.5 H9 A2 2 0 0 1 10.6 7.3 L11.8 8.9 H18 A2 2 0 0 1 20 10.9 V16.5 A2 2 0 0 1 18 18.5 H6 A2 2 0 0 1 4 16.5 Z",
        [IconName.Star] = "M12 4.2 L14.3 9.2 L19.8 9.9 L15.7 13.6 L16.8 19 L12 16.3 L7.2 19 L8.3 13.6 L4.2 9.9 L9.7 9.2 Z",
        [IconName.Mic] = "M12 3.6 A2.9 2.9 0 0 1 14.9 6.5 V11.4 A2.9 2.9 0 0 1 9.1 11.4 V6.5 A2.9 2.9 0 0 1 12 3.6 Z M6.6 11.6 A5.4 5.4 0 0 0 17.4 11.6 M12 17 V20.4 M9 20.4 H15 M3.4 9.6 A5.6 5.6 0 0 0 3.4 13.8 M20.6 9.6 A5.6 5.6 0 0 1 20.6 13.8",
        [IconName.Dots] = "M6.4 12 A1.2 1.2 0 1 0 6.39 12 Z M12 12 A1.2 1.2 0 1 0 11.99 12 Z M17.6 12 A1.2 1.2 0 1 0 17.59 12 Z",
        [IconName.Ai] = "M12 2.8 A1 1 0 0 1 12.9 3.5 C13.6 7 15 8.4 18.5 9.1 A1 1 0 0 1 18.5 11.1 C15 11.8 13.6 13.2 12.9 16.7 A1 1 0 0 1 11.1 16.7 C10.4 13.2 9 11.8 5.5 11.1 A1 1 0 0 1 5.5 9.1 C9 8.4 10.4 7 11.1 3.5 A1 1 0 0 1 12 2.8 Z M17.6 14.6 A1 1 0 0 1 18.5 15.3 C18.7 16.5 19.1 16.9 20.3 17.1 A1 1 0 0 1 20.3 19.1 C19.1 19.3 18.7 19.7 18.5 20.9 A1 1 0 0 1 16.7 20.9 C16.5 19.7 16.1 19.3 14.9 19.1 A1 1 0 0 1 14.9 17.1 C16.1 16.9 16.5 16.5 16.7 15.3 A1 1 0 0 1 17.6 14.6 Z",
        [IconName.Search] = "M10.8 4.2 A6.4 6.4 0 1 0 10.8 17 A6.4 6.4 0 1 0 10.8 4.2 M15.3 15.3 A1 1 0 0 1 16.7 15.3 L19.8 18.4 A1 1 0 0 1 18.4 19.8 L15.3 16.7 A1 1 0 0 1 15.3 15.3 Z",
        [IconName.File] = "M7.5 3.8 H13.6 A1 1 0 0 1 14.3 4.1 L17.2 7 A1 1 0 0 1 17.5 7.7 V19.2 A1.3 1.3 0 0 1 16.2 20.5 H7.5 A1.3 1.3 0 0 1 6.2 19.2 V5.1 A1.3 1.3 0 0 1 7.5 3.8 Z M13.6 3.8 V6.7 A1 1 0 0 0 14.6 7.7 H17.5",
        [IconName.Pdf] = "M7.5 3.8 H13.6 A1 1 0 0 1 14.3 4.1 L17.2 7 A1 1 0 0 1 17.5 7.7 V19.2 A1.3 1.3 0 0 1 16.2 20.5 H7.5 A1.3 1.3 0 0 1 6.2 19.2 V5.1 A1.3 1.3 0 0 1 7.5 3.8 Z M13.6 3.8 V6.7 A1 1 0 0 0 14.6 7.7 H17.5 M9.6 12.8 H14.9 M9.6 15.6 H14.9",
        [IconName.Extensions] = "M5.8 4.6 H9.4 A1.4 1.4 0 0 1 10.8 6 V9.6 A1.4 1.4 0 0 1 9.4 11 H5.8 A1.4 1.4 0 0 1 4.4 9.6 V6 A1.4 1.4 0 0 1 5.8 4.6 Z M14.6 4.6 H18.2 A1.4 1.4 0 0 1 19.6 6 V9.6 A1.4 1.4 0 0 1 18.2 11 H14.6 A1.4 1.4 0 0 1 13.2 9.6 V6 A1.4 1.4 0 0 1 14.6 4.6 Z M5.8 13 H9.4 A1.4 1.4 0 0 1 10.8 14.4 V18 A1.4 1.4 0 0 1 9.4 19.4 H5.8 A1.4 1.4 0 0 1 4.4 18 V14.4 A1.4 1.4 0 0 1 5.8 13 Z M16.4 12.8 A3.3 3.3 0 1 1 16.39 12.8 Z",
        [IconName.Settings] = "M12 8.4 A3.6 3.6 0 1 0 12 15.6 A3.6 3.6 0 1 0 12 8.4 Z M10.6 2.6 H13.4 A1 1 0 0 1 14.4 3.5 L14.6 5.4 A7 7 0 0 1 16.3 6.4 L18.1 5.7 A1 1 0 0 1 19.3 6.1 L20.7 8.5 A1 1 0 0 1 20.5 9.8 L19 11 A7 7 0 0 1 19 13 L20.5 14.2 A1 1 0 0 1 20.7 15.5 L19.3 17.9 A1 1 0 0 1 18.1 18.3 L16.3 17.6 A7 7 0 0 1 14.6 18.6 L14.4 20.5 A1 1 0 0 1 13.4 21.4 H10.6 A1 1 0 0 1 9.6 20.5 L9.4 18.6 A7 7 0 0 1 7.7 17.6 L5.9 18.3 A1 1 0 0 1 4.7 17.9 L3.3 15.5 A1 1 0 0 1 3.5 14.2 L5 13 A7 7 0 0 1 5 11 L3.5 9.8 A1 1 0 0 1 3.3 8.5 L4.7 6.1 A1 1 0 0 1 5.9 5.7 L7.7 6.4 A7 7 0 0 1 9.4 5.4 L9.6 3.5 A1 1 0 0 1 10.6 2.6 Z",
        [IconName.Devices] = "M4.4 5.2 H13.6 A2 2 0 0 1 15.6 7.2 V13.4 A2 2 0 0 1 13.6 15.4 H10.2 V17.2 H11.6 A1 1 0 0 1 11.6 19.2 H6.4 A1 1 0 0 1 6.4 17.2 H7.8 V15.4 H4.4 A2 2 0 0 1 2.4 13.4 V7.2 A2 2 0 0 1 4.4 5.2 Z M17.6 8 H19.8 A1.8 1.8 0 0 1 21.6 9.8 V17.4 A1.8 1.8 0 0 1 19.8 19.2 H17.6 A1.8 1.8 0 0 1 15.8 17.4 V9.8 A1.8 1.8 0 0 1 17.6 8 Z",
        [IconName.Calendar] = "M8 3 A1 1 0 0 1 9 4 V5.4 H15 V4 A1 1 0 0 1 17 4 V5.4 H18.6 A2.4 2.4 0 0 1 21 7.8 V18.6 A2.4 2.4 0 0 1 18.6 21 H5.4 A2.4 2.4 0 0 1 3 18.6 V7.8 A2.4 2.4 0 0 1 5.4 5.4 H7 V4 A1 1 0 0 1 8 3 Z M5 10.6 V18.6 A0.4 0.4 0 0 0 5.4 19 H18.6 A0.4 0.4 0 0 0 19 18.6 V10.6 Z",
        [IconName.Tasks] = "M5.4 6 H8.6 A1.4 1.4 0 0 1 10 7.4 V10.6 A1.4 1.4 0 0 1 8.6 12 H5.4 A1.4 1.4 0 0 1 4 10.6 V7.4 A1.4 1.4 0 0 1 5.4 6 Z M5.4 13.6 H8.6 A1.4 1.4 0 0 1 10 15 V18.2 A1.4 1.4 0 0 1 8.6 19.6 H5.4 A1.4 1.4 0 0 1 4 18.2 V15 A1.4 1.4 0 0 1 5.4 13.6 Z M13 8 H19 A1 1 0 0 1 19 10 H13 A1 1 0 0 1 13 8 Z M13 15.6 H19 A1 1 0 0 1 19 17.6 H13 A1 1 0 0 1 13 15.6 Z",
        [IconName.Weather] = "M8.2 18.4 A4.6 4.6 0 0 1 8.4 9.2 A5.6 5.6 0 0 1 18.4 10.2 A4.1 4.1 0 0 1 17.8 18.4 Z",
        [IconName.Clock] = "M12 3 A9 9 0 1 0 12 21 A9 9 0 1 0 12 3 Z M12 6.6 A1 1 0 0 1 13 7.6 V11.5 L15.9 13.2 A1 1 0 0 1 14.9 14.9 L11.5 13 A1 1 0 0 1 11 12.1 V7.6 A1 1 0 0 1 12 6.6 Z",
        [IconName.Refresh] = "M20 12 A8 8 0 1 1 17.2 6 M17.2 6 V4 M17.2 6 H14.4",
        [IconName.Menu] = "M4.6 6.4 H19.4 A1.2 1.2 0 0 1 19.4 8.8 H4.6 A1.2 1.2 0 0 1 4.6 6.4 Z M4.6 10.8 H19.4 A1.2 1.2 0 0 1 19.4 13.2 H4.6 A1.2 1.2 0 0 1 4.6 10.8 Z M4.6 15.2 H19.4 A1.2 1.2 0 0 1 19.4 17.6 H4.6 A1.2 1.2 0 0 1 4.6 15.2 Z",
        [IconName.Close] = "M6.6 5 L12 10.4 L17.4 5 A1.1 1.1 0 0 1 19 6.6 L13.6 12 L19 17.4 A1.1 1.1 0 0 1 17.4 19 L12 13.6 L6.6 19 A1.1 1.1 0 0 1 5 17.4 L10.4 12 L5 6.6 A1.1 1.1 0 0 1 6.6 5 Z",
        [IconName.Minimize] = "M5.6 10.8 H18.4 A1.2 1.2 0 0 1 18.4 13.2 H5.6 A1.2 1.2 0 0 1 5.6 10.8 Z",
        [IconName.Maximize] = "M6.6 5.4 H17.4 A1.2 1.2 0 0 1 18.6 6.6 V17.4 A1.2 1.2 0 0 1 17.4 18.6 H6.6 A1.2 1.2 0 0 1 5.4 17.4 V6.6 A1.2 1.2 0 0 1 6.6 5.4 Z M7.6 7.6 V16.4 H16.4 V7.6 Z",
        [IconName.Restore] = "M7.4 8.4 H15.6 A1.2 1.2 0 0 1 16.8 9.6 V16.6 A1.2 1.2 0 0 1 15.6 17.8 H7.4 A1.2 1.2 0 0 1 6.2 16.6 V9.6 A1.2 1.2 0 0 1 7.4 8.4 Z M10.2 6.2 A1.2 1.2 0 0 1 11.4 5 H17.4 A1.2 1.2 0 0 1 18.6 6.2 V13.4 A1.2 1.2 0 0 1 17.4 14.6 Z",
        [IconName.ChevronRight] = "M9.6 4.6 A1.3 1.3 0 0 1 11.4 4.6 L17.4 11 A1.4 1.4 0 0 1 17.4 13 L11.4 19.4 A1.3 1.3 0 0 1 9.6 17.6 L14.8 12 Z",
        [IconName.Member] = "M12 4 A4 4 0 1 0 12 12 A4 4 0 1 0 12 4 Z M12 13.6 C7.9 13.6 4.4 15.9 4.4 19.2 A1.4 1.4 0 0 0 5.8 20.6 H18.2 A1.4 1.4 0 0 0 19.6 19.2 C19.6 15.9 16.1 13.6 12 13.6 Z",
    };

    /// <summary>暖萌用实心(圆滚滚更萌),墨白与微风用描边(线性)。</summary>
    static bool IsFilled(Skin s) => s == Skin.Warm;

    /// <summary>
    /// 天气实况字形 —— ★ 三套皮肤【共用】。理由见文件里这一段:
    ///   它们是象形图,不是界面装饰。皮肤照旧决定线宽/填充/圆角(见 Fill),
    ///   但"这是太阳还是云"不该因为换肤而变。
    /// 全部画在 24×24 的格子里,且尽量【以 (12,12) 为心】—— 与 Refresh 那次同一条教训:
    ///   包围盒不对称会让旋转/居中都歪掉。
    /// </summary>
    static readonly Dictionary<IconName, string> Weather3 = new()
    {
        // 太阳:圆 + 八条光芒
        [IconName.WxSun] = "M12 7.4 A4.6 4.6 0 1 1 11.99 7.4 Z M12 2.6 V4.8 M12 19.2 V21.4 "
                         + "M2.6 12 H4.8 M19.2 12 H21.4 M5.4 5.4 L6.9 6.9 M17.1 17.1 L18.6 18.6 "
                         + "M18.6 5.4 L17.1 6.9 M6.9 17.1 L5.4 18.6",
        // 局部多云:小太阳 + 一朵云
        [IconName.WxPartly] = "M9.2 6.6 A3.2 3.2 0 0 1 15.4 7.8 M9.2 3.2 V4.6 M4.9 5.4 L5.9 6.4 "
                            + "M14.6 4.2 L13.6 5.2 M8 20 A3.6 3.6 0 0 1 8.3 12.9 A4.8 4.8 0 0 1 17.4 13.8 "
                            + "A3.1 3.1 0 0 1 16.9 20 Z",
        // 阴:两朵云
        [IconName.WxCloud] = "M7.5 17 A4 4 0 0 1 7.8 9.1 A5.2 5.2 0 0 1 17.5 10 A3.5 3.5 0 0 1 17 17 Z",
        // 雾:云 + 三条横线
        [IconName.WxFog] = "M7.6 13.4 A3.6 3.6 0 0 1 7.9 6.2 A4.8 4.8 0 0 1 17 7 A3.1 3.1 0 0 1 16.5 13.4 Z "
                         + "M5 16.6 H19 M6.6 19.4 H17.4",
        // 毛毛雨:云 + 两小点
        [IconName.WxDrizzle] = "M7.6 13.8 A3.6 3.6 0 0 1 7.9 6.6 A4.8 4.8 0 0 1 17 7.4 A3.1 3.1 0 0 1 16.5 13.8 Z "
                             + "M9.6 16.8 V18.2 M14.4 16.8 V18.2",
        // 雨:云 + 三条斜雨
        [IconName.WxRain] = "M7.6 13.4 A3.6 3.6 0 0 1 7.9 6.2 A4.8 4.8 0 0 1 17 7 A3.1 3.1 0 0 1 16.5 13.4 Z "
                          + "M8.8 16.4 L7.8 19.4 M12.4 16.4 L11.4 19.4 M16 16.4 L15 19.4",
        // 雪:云 + 三片雪花(十字)
        [IconName.WxSnow] = "M7.6 13.2 A3.6 3.6 0 0 1 7.9 6 A4.8 4.8 0 0 1 17 6.8 A3.1 3.1 0 0 1 16.5 13.2 Z "
                          + "M8.6 16.4 H10.2 M9.4 15.6 V17.2 M13.8 16.4 H15.4 M14.6 15.6 V17.2 "
                          + "M11.2 19 H12.8 M12 18.2 V19.8",
        // 阵雨:云 + 两条斜雨(比"雨"少一条,表示间歇)
        [IconName.WxShower] = "M7.6 13.4 A3.6 3.6 0 0 1 7.9 6.2 A4.8 4.8 0 0 1 17 7 A3.1 3.1 0 0 1 16.5 13.4 Z "
                            + "M10 16.4 L9 19.4 M15 16.4 L14 19.4",
        // 月亮(夜间的"晴")—— 渐开弧:大圆挖去一个偏右上的小圆
        [IconName.WxMoon] = "M14.6 3.6 A8.4 8.4 0 1 0 20.4 12.8 A6.5 6.5 0 0 1 14.6 3.6 Z",
        // 夜间的局部多云:小月亮 + 一朵云(与白天那个同一个云,只把太阳换成月亮)
        [IconName.WxPartlyNight] = "M11.4 3 A4.8 4.8 0 1 0 14.8 8.6 A3.7 3.7 0 0 1 11.4 3 Z "
                                 + "M8 20 A3.6 3.6 0 0 1 8.3 12.9 A4.8 4.8 0 0 1 17.4 13.8 A3.1 3.1 0 0 1 16.9 20 Z",
        // 雷阵雨:云 + 闪电
        [IconName.WxThunder] = "M7.6 13.2 A3.6 3.6 0 0 1 7.9 6 A4.8 4.8 0 0 1 17 6.8 A3.1 3.1 0 0 1 16.5 13.2 Z "
                             + "M12.8 15.4 L10.4 18.6 H13 L11.4 21.4",
    };

    /// <summary>
    /// WMO 天气代码 -> 图标。★ 认不出的代码返回 null —— 界面据此【不画图标】,
    /// 而不是随便给一个太阳(那等于替天气编了个说法)。与 Weather.Describe 的口径一致。
    /// </summary>
    /// <param name="night">
    /// 这一刻在那个城市是不是夜里 —— 夜里的"晴"该是月亮而不是太阳(用户裁定)。
/// ★ 只有【晴、局部多云】分昼夜:阴天/雨/雪/雾 本来就看不见日月,分了反而多出两个认不出的图。
    /// </param>
    public static IconName? ForWeather(int? code, bool night = false) => code switch
    {
        null => null,
        0 => night ? IconName.WxMoon : IconName.WxSun,
        1 or 2 => night ? IconName.WxPartlyNight : IconName.WxPartly,
        3 => IconName.WxCloud,
        45 or 48 => IconName.WxFog,
        51 or 53 or 55 or 56 or 57 => IconName.WxDrizzle,
        61 or 63 or 65 or 66 or 67 => IconName.WxRain,
        71 or 73 or 75 or 77 or 85 or 86 => IconName.WxSnow,
        80 or 81 or 82 => IconName.WxShower,
        95 or 96 or 99 => IconName.WxThunder,
        _ => null,
    };

    static Dictionary<IconName, string> SetFor(Skin s) => s switch
    {
        Skin.Ink => Line,
        Skin.Warm => Cute,
        _ => Apple,
    };

    /// <summary>
    /// 造一个图标。foregroundKey 走 DynamicResource,所以换肤时颜色自动跟随;
    /// 形状则由 Rebuild 在换肤时整体重建(见 ThemeManager.SkinChanged)。
    /// </summary>
    // ★ 图标要在换肤时重画形状,但**不能**让每个图标都 += 到静态事件上 ——
    //   界面每次重建(导航、换语言)都会造一批新图标,旧的永远挂在事件上 = 内存泄漏,
    //   而且换肤时会去刷早已不在可视树里的死对象。改用弱引用登记 + 换肤时顺带清理。
    static readonly List<WeakReference<ContentControl>> Live = new();
    static bool _hooked;
    static int _sweepAt = 256;   // ★ 下次摊还清扫的水位(审计 2026-07-31)

    // ★★★ 刷新图标的几何【故意保持“以圆心为中心的正方形”】—— 它是要旋转的。
    //   原来箭头尖撑到 y=2.6,而圆(心 12,12 半径 8)的盒子是 [4,20]² ——
    //   几何盒子于是变成 y[2.6,20],它的中心不再是圆心。
    //   更阴的是 WPF 的 Stretch.Uniform 对描边图形是【左上对齐】而不是居中对齐
    //   (translate = -bounds*scale + 笔宽/2),所以短边那一侧会多出一截空白——
    //   结果是圆心在 x 与 y 两个方向上都偏(实测 0.464 / 0.536),转起来整个圈在晟。
    //   一度想用"把旋转原点调成 0.538"来这样绕过去 —— 那只修了 y,而且是个会烂的魔数。
    //   现在直接把【箭头收进圆的包围盒】:几何盒子 = [4,20]²,既是正方形、又以圆心为心,
    //   Uniform 缩放后刚好填满正方形的框,圆心就落在框的正中央 —— 绕 (0.5,0.5) 转就是对的。
    //   ★ 改图形时请守住这一条:箭头不要超出 y∈[4,20]。

    public static FrameworkElement Make(IconName name, double size = 18, string foregroundKey = "FgSecondary")
    {
        var host = new ContentControl { Width = size, Height = size, Focusable = false };
        host.Tag = (name, foregroundKey);
        Fill(host);

        if (!_hooked) { _hooked = true; ThemeManager.SkinChanged += RefreshAll; }
        Live.Add(new WeakReference<ContentControl>(host));
        // ★ 摊还清扫:此前只有【换肤】才回收死登记项 —— 常驻托盘、从不换肤的话,
        //   每次导航新建整页图标,Live 只增不减,无界增长。到水位就清一次死项,
        //   并把水位定在"当前活图标数的 2 倍",表长恒定被压住,不再依赖换肤。
        if (Live.Count >= _sweepAt)
        {
            SweepDead();
            _sweepAt = Math.Max(256, Live.Count * 2);
        }
        return host;
    }

    /// <summary>清掉已被 GC 回收的弱引用登记项(不重画,只收表)。</summary>
    static void SweepDead()
    {
        for (int i = Live.Count - 1; i >= 0; i--)
            if (!Live[i].TryGetTarget(out _)) Live.RemoveAt(i);
    }

    /// <summary>换肤时重画所有仍存活的图标,并顺手清掉已被回收的登记项。</summary>
    static void RefreshAll()
    {
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            if (Live[i].TryGetTarget(out var host)) Fill(host);
            else Live.RemoveAt(i);
        }
    }

    /// <summary>改图标前景(如导航选中态:墨白是黑底,图标必须转白,否则看不见)。</summary>
    public static void SetForeground(FrameworkElement icon, string foregroundKey)
    {
        if (icon is not ContentControl host || host.Tag is not ValueTuple<IconName, string> t) return;
        host.Tag = (t.Item1, foregroundKey);
        Fill(host);
    }

    /// <summary>
    /// 当前皮肤下这个图标的路径;没画就是 null。
    /// ★ 给自检用:新加图标很容易只加一套皮肤,换肤时那里就空着 —— 这个口子让它当场被抓到。
    /// </summary>
    public static string? PathFor(IconName name) => PathFor(name, ThemeManager.Current);

    /// <summary>指定皮肤下的路径。★ 不碰 ThemeManager,自检可以直接查而不换肤(换肤要 Application 在场)。</summary>
    public static string? PathFor(IconName name, Skin skin)
        // ★ 与 Fill 同一条查找路径:皮肤表里没有就去【天气共用表】再找一次。
        //   两边路径不一致的话,自检会报"这个皮肤缺图标"而界面上其实好好的。
        => SetFor(skin).TryGetValue(name, out var d) ? d
         : Weather3.TryGetValue(name, out var w) ? w : null;

    static void Fill(ContentControl host)
    {
        var (name, fgKey) = ((IconName, string))host.Tag;
        var skin = ThemeManager.Current;
        var set = SetFor(skin);
        // ★ 皮肤表里没有就去【天气共用表】再找一次(天气字形三套共用,见 Weather3 的说明)
        if (!set.TryGetValue(name, out var data) && !Weather3.TryGetValue(name, out data))
        { host.Content = null; return; }

        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(data),
            Stretch = Stretch.Uniform,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        if (IsFilled(skin)) path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, fgKey);
        else
        {
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, fgKey);
            // 墨白线性更硬朗(等宽直角),苹果线性更细更圆润
            path.StrokeThickness = skin == Skin.Ink ? 1.9 : 1.6;
            if (skin == Skin.Ink) { path.StrokeStartLineCap = PenLineCap.Flat; path.StrokeEndLineCap = PenLineCap.Flat; path.StrokeLineJoin = PenLineJoin.Miter; }
        }
        host.Content = path;
    }
}
