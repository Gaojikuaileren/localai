// P3c -- 转盘渲染诊断(仅调试用,不进正常启动路径)。
//   localai-client --wheeltest <输出目录>
// 把「时间转盘」和「两个日期转盘并排(模拟抽屉里的开始/结束)」离屏渲染成 PNG,
// 用来肉眼确认三列都画出来了、在抽屉宽度内不被裁掉 —— 因为无头 selftest 验不了渲染。

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalAI.Client.Views;

namespace LocalAI.Client;

public static class WheelTest
{
    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);

        var (timeEl, _) = WheelPicker.Time(new TimeSpan(9, 35, 0), _ => { });
        Save(Frame("非全天:时间转盘", timeEl, 220), Path.Combine(outDir, "wheel-time.png"), 240, 150);

        // 模拟编辑抽屉里的一行:开始日期 + 结束日期 并排,总宽按抽屉可用宽度 ~360
        var dual = new StackPanel { Orientation = Orientation.Horizontal };
        dual.Children.Add(Labeled("开始日期", WheelPicker.Date(new DateTime(2026, 7, 29), _ => { })));
        dual.Children.Add(new Border { Width = 12 });
        dual.Children.Add(Labeled("结束日期", WheelPicker.Date(new DateTime(2026, 8, 2), _ => { })));
        Save(ClipTo(dual, 360), Path.Combine(outDir, "wheel-date-dual.png"), 380, 170);

        Console.WriteLine("wheeltest: 已输出 wheel-time.png / wheel-date-dual.png");
        return 0;
    }

    static FrameworkElement Labeled(string caption, FrameworkElement body)
    {
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = caption, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 4) });
        s.Children.Add(body);
        return s;
    }

    // 用一个禁止横向滚动的 ScrollViewer 包起来,精确复刻抽屉里"超宽会被裁掉"的条件
    static FrameworkElement ClipTo(FrameworkElement body, double width)
        => new ScrollViewer
        {
            Content = body,
            Width = width,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

    static FrameworkElement Frame(string caption, FrameworkElement body, double width)
    {
        var s = new StackPanel { Width = width };
        s.Children.Add(new TextBlock { Text = caption, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 4) });
        s.Children.Add(body);
        return s;
    }

    static void Save(FrameworkElement el, string path, int w, int h)
    {
        var root = new Border { Background = Brushes.White, Padding = new Thickness(10), Child = el };
        root.Measure(new Size(w, h));
        root.Arrange(new Rect(new Size(w, h)));
        root.UpdateLayout();

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
