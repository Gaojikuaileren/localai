// P3c -- 窗口圆角跟随皮肤(用户裁定:暖萌大圆角 / 微风中等 / 墨白小圆角)。
//
// 做法:Windows 11 的 DWM 窗口圆角属性(DWMWA_WINDOW_CORNER_PREFERENCE)。
// ★ 为什么不用 WindowStyle=None + AllowsTransparency 自己画圆角:那会让整窗走 layered window
//   合成,显存与性能开销比设计 §7 明令禁掉的毛玻璃还高 —— 为了圆角付这个代价不值。
//
// ★ 诚实的限制:DWM 只提供【两档】真实窗口圆角 —— Round(大,约 8px)与 RoundSmall(小,约 4px)。
//   所以三档在窗口边框上只能落成两档(暖萌/微风 = Round,墨白 = RoundSmall)。
//   三档的差异真正体现在**窗口内部**的卡片/按钮圆角(Theme/*.xaml 里各皮肤自定义 RadiusSm/Md/Lg),
//   那里是我们自己画的,可以精确分三档。

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LocalAI.Client.Services;

public static class WindowCorners
{
    const int DwmwaWindowCornerPreference = 33;

    enum CornerPreference { Default = 0, DoNotRound = 1, Round = 2, RoundSmall = 3 }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Window window, Skin skin)
    {
        var pref = skin switch
        {
            Skin.Warm => CornerPreference.Round,        // 暖萌:大圆角
            Skin.Breeze => CornerPreference.Round,      // 微风:中等(DWM 无中间档,取 Round)
            Skin.Ink => CornerPreference.RoundSmall,    // 墨白:小圆角
            _ => CornerPreference.Default,
        };
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;   // 窗口还没建好,由调用方在 SourceInitialized 后再调
            var v = (int)pref;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref v, sizeof(int));
        }
        catch { /* 老版本 Windows 没这个属性 -> 直角,不影响功能 */ }
    }
}
