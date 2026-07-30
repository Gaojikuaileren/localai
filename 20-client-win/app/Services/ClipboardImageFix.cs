// P3c -- 剪贴板截图的 alpha 修正。
//
// 症状(用户反馈):截图能粘进附件栏,但预览是【空的】。
// 原因:Windows 的剪贴板位图走的是 DIB,DIB 没有真正的 alpha 通道 —— 但 WPF 的
//   Clipboard.GetImage() 仍按 Bgra32 交给我们,那条 alpha 全是 0。
//   于是存出来的 png 整幅【完全透明】:文件在、尺寸对、画出来什么都没有。
//   截图工具、QQ/微信截图、Win+Shift+S 都会这样,是个老坑。
// 处置:整幅 alpha 都是 0 = 这条通道没意义,补成不透明;只要有一个像素不是 0,
//   说明是真的带透明度的图(从图像软件复制的),原样保留 —— 绝不乱改用户的图。

using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LocalAI.Client.Services;

public static class ClipboardImageFix
{
    /// <summary>
    /// 纯函数版(可单测):BGRA 缓冲区里若 alpha 全是 0,就整条补成 255 并返回 true;
    /// 否则一个字节都不动,返回 false。
    /// </summary>
    public static bool MakeOpaqueIfFullyTransparent(byte[] bgra)
    {
        if (bgra.Length < 4) return false;
        for (int i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 0) return false;                 // 有真 alpha -> 不碰
        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        return true;
    }

    /// <summary>把剪贴板拿到的位图整成"画得出来"的样子。没救的情况原样返回,不抛。</summary>
    public static BitmapSource Normalize(BitmapSource src)
    {
        try
        {
            var conv = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var buf = new byte[stride * h];
            conv.CopyPixels(buf, stride, 0);
            if (!MakeOpaqueIfFullyTransparent(buf)) return src;
            var fixedUp = BitmapSource.Create(w, h, conv.DpiX, conv.DpiY, PixelFormats.Bgra32, null, buf, stride);
            fixedUp.Freeze();
            return fixedUp;
        }
        catch { return src; }
    }
}
