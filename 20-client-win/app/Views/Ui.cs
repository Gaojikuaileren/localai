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

    /// <summary>
    /// 统一板块容器。★ 用户裁定:相同/并列功能的板块必须**同格式同大小** ——
    /// 所以所有卡片一律经由这里产出:同样的内边距、同样的圆角令牌(跟随皮肤)、同样的描边。
    /// 各视图不要再自己 new Border 拼卡片,否则并列板块又会长得不一样。
    /// </summary>
    public static Border Card(UIElement child, Thickness? margin = null)
    {
        var b = new Border
        {
            Child = child,
            Padding = new Thickness(16),
            Margin = margin ?? new Thickness(0, 0, 0, 16),
            BorderThickness = new Thickness(1),
        };
        b.Dyn(Border.BackgroundProperty, "BgSurface")
         .Dyn(Border.BorderBrushProperty, "Border")
         .Dyn(Border.CornerRadiusProperty, "RadiusMd");
        return b;
    }

    /// <summary>
    /// 带标题(可选图标)的统一板块。并列板块用它,标题栏高度与排版完全一致。
    /// </summary>
    public static Border Panel(string title, UIElement body, Theme.IconName? icon = null, Thickness? margin = null, bool compact = false)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, compact ? 6 : 10) };
        if (icon is { } ic)
        {
            var el = Theme.Icons.Make(ic, 16, "FgMuted");
            el.Margin = new Thickness(0, 0, 8, 0);
            el.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(el);
        }
        var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        t.Dyn(TextBlock.ForegroundProperty, "FgPrimary").Dyn(TextBlock.FontSizeProperty, "FontSubtitle");
        head.Children.Add(t);

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(body);
        var card = Card(dock, margin);
        if (compact) card.Padding = new Thickness(12, 10, 12, 10);   // 抽屉里的表单卡更紧凑
        return card;
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
