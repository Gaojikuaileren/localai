// P3c -- PDF 逐页渲染(文件翻译的左侧预览)。
//
// ★ 用【Windows 自带的】渲染组件 Windows.Data.Pdf,不引第三方二进制、不联网 ——
//   这是本项目「全本地」底线下唯一站得住的选择:PDFium/Docnet 那类要么带原生 dll,
//   要么得联网取包;WebView2 则等于把浏览器塞进来。代价只是目标框架要写成带
//   Windows SDK 版本的形式(见 csproj 里的说明),WPF 本身一点没变。
//
// ★ 如实边界:
//   · 加密/损坏的 PDF 打不开 —— 如实报错,不画一张空白页假装成功;
//   · 渲染是异步的,调用方拿到的是 Task;界面在等的时候要说"正在渲染",不能装作已经好了;
//   · 渲染出来的位图【冻结】(Freeze)后才交出去:它要跨线程回到 UI 线程,不冻会抛。

using System.Windows.Media.Imaging;

namespace LocalAI.Client.Services;

/// <summary>一份打开着的 PDF。用完记得 Dispose(它持有系统侧的文档对象)。</summary>
public sealed class PdfPreview
{
    readonly Windows.Data.Pdf.PdfDocument _doc;

    PdfPreview(Windows.Data.Pdf.PdfDocument doc) => _doc = doc;

    public int PageCount => (int)_doc.PageCount;

    /// <summary>打开一个 PDF。打不开(加密/损坏/文件没了)返回 null —— 由调用方如实说明。</summary>
    public static async Task<PdfPreview?> OpenAsync(string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            return new PdfPreview(doc);
        }
        catch { return null; }
    }

    /// <summary>
    /// 渲染第 index 页(0 起)。widthPx = 想要的像素宽,按页面比例出高。
    /// 返回【已冻结】的位图,可直接跨线程给 UI;失败返回 null。
    /// </summary>
    public async Task<BitmapSource?> RenderAsync(int index, uint widthPx)
    {
        if (index < 0 || index >= PageCount) return null;
        try
        {
            using var page = _doc.GetPage((uint)index);
            using var mem = new System.IO.MemoryStream();
            // ★ 这个包装器【不能用 using】:释放它会连底下的 MemoryStream 一起关掉,
            //   接下来读 mem 就是读一个已关闭的流 —— 表现是渲染"成功"但拿到 0x0。
            //   (这一条是自检里那条行为断言当场拓出来的。)
            var ras = mem.AsRandomAccessStream();
            await page.RenderToStreamAsync(ras, new Windows.Data.Pdf.PdfPageRenderOptions
            {
                // 只给宽:高度由组件按页面比例算 —— 自己算高容易在非 A4 页面上拉变形
                DestinationWidth = Math.Max(1, widthPx),
            });
            await ras.FlushAsync();
            mem.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;    // 读完就脱离流,否则流一关就成空图
            bmp.StreamSource = mem;
            bmp.EndInit();
            bmp.Freeze();                                   // 跨线程交给 UI 的前提
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>页面的原始尺寸(点)—— 标注框按比例换算时要用它,不能拿渲染像素当真值。</summary>
    public (double W, double H) PageSize(int index)
    {
        if (index < 0 || index >= PageCount) return (0, 0);
        using var page = _doc.GetPage((uint)index);
        return (page.Size.Width, page.Size.Height);
    }

    // ★ 没有 Dispose:PdfDocument 本身不是 IDisposable,摆一个空的 Dispose 在这里
    //   只会让调用方以为"已经收干净了"。不用了就把引用置 null,交给 GC。
}
