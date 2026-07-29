// P3c -- 视图层的小工具集。目的是让各视图用统一的间距/字号/颜色令牌,
// 不在每个页面里手写魔法数字(设计 §7:基础设计变量定义一次,不为每皮肤写死页面)。
// 所有颜色一律走 DynamicResource,换肤才能即时生效。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace LocalAI.Client.Views;

public static class Ui
{
    public static T Dyn<T>(this T el, DependencyProperty prop, string resourceKey) where T : FrameworkElement
    { el.SetResourceReference(prop, resourceKey); return el; }

    public static TextBlock Title(string text) => new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) }
        .Dyn(TextBlock.FontSizeProperty, "FontTitle").Dyn(TextBlock.ForegroundProperty, "FgPrimary");

    public static TextBlock Subtitle(string text) => new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) }
        .Dyn(TextBlock.FontSizeProperty, "FontSubtitle").Dyn(TextBlock.ForegroundProperty, "FgPrimary");

    public static TextBlock Body(string text, bool muted = false) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) }
        .Dyn(TextBlock.ForegroundProperty, muted ? "FgSecondary" : "FgPrimary");

    public static TextBlock Caption(string text) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }
        .Dyn(TextBlock.FontSizeProperty, "FontCaption").Dyn(TextBlock.ForegroundProperty, "FgMuted");

    public static Border Card(UIElement child, Thickness? margin = null)
    {
        var b = new Border
        {
            Child = child,
            Padding = new Thickness(20),
            Margin = margin ?? new Thickness(0, 0, 0, 16),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
        };
        b.Dyn(Border.BackgroundProperty, "BgSurface").Dyn(Border.BorderBrushProperty, "Border");
        return b;
    }

    public static Button Primary(string text, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = text, Height = 36, Padding = new Thickness(18, 0, 18, 0),
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        b.Dyn(Button.BackgroundProperty, "Accent").Dyn(Button.ForegroundProperty, "FgOnAccent");
        b.Click += onClick;
        return b;
    }

    public static Button Secondary(string text, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = text, Height = 34, Padding = new Thickness(14, 0, 14, 0),
            BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent,
        };
        b.Dyn(Button.BorderBrushProperty, "BorderStrong").Dyn(Button.ForegroundProperty, "FgPrimary");
        b.Click += onClick;
        return b;
    }

    /// <summary>危险操作按钮(解除设备一类)。风险色跨皮肤恒定,皮肤禁改(设计 §7)。</summary>
    public static Button Danger(string text, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = text, Height = 32, Padding = new Thickness(14, 0, 14, 0),
            BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent,
        };
        b.Dyn(Button.BorderBrushProperty, "RiskDanger").Dyn(Button.ForegroundProperty, "RiskDanger");
        b.Click += onClick;
        return b;
    }

    public static StackPanel Stack(params UIElement[] children)
    {
        var p = new StackPanel();
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static ScrollViewer Page(params UIElement[] children)
    {
        var p = new StackPanel { Margin = new Thickness(28, 24, 28, 24), MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var c in children) p.Children.Add(c);
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
}
