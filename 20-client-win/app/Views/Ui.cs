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
    public static Border Panel(string title, UIElement body, Theme.IconName? icon = null, Thickness? margin = null,
                               bool compact = false, FrameworkElement? headerAction = null)
    {
        // 标题(图标 + 文字)靠左
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (icon is { } ic)
        {
            var el = Theme.Icons.Make(ic, 16, "FgMuted");
            el.Margin = new Thickness(0, 0, 8, 0);
            el.VerticalAlignment = VerticalAlignment.Center;
            titleRow.Children.Add(el);
        }
        var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        t.Dyn(TextBlock.ForegroundProperty, "FgPrimary").Dyn(TextBlock.FontSizeProperty, "FontSubtitle");
        titleRow.Children.Add(t);

        // 可选的右侧动作(如"+"新增)—— 标题行用 DockPanel,动作贴右,标题占满其余
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, compact ? 6 : 10), LastChildFill = true };
        if (headerAction is not null)
        {
            headerAction.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(headerAction, Dock.Right);
            head.Children.Add(headerAction);
        }
        head.Children.Add(titleRow);

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(body);
        var card = Card(dock, margin);
        if (compact) card.Padding = new Thickness(12, 10, 12, 10);   // 抽屉里的表单卡更紧凑
        return card;
    }

    /// <summary>
    /// 板块标题栏右侧的小圆角"+"按钮(新增待办/家务等)。自绘 —— 走强调色、圆角随皮肤,
    /// 不用系统按钮外观。hover 略微加深。
    /// </summary>
    public static FrameworkElement PlusButton(Action onClick, string? tip = null)
    {
        // ★ 用两条【居中的矩形】拼"+",而不是 Path。Path(Stretch=None)会把几何自带的
        //   原点偏移一起算进元素边界,导致"+"在按钮块里偏右下、看着不居中(用户反馈)。
        //   两条矩形各自在 24×24 里水平/垂直居中,交点恰在正中,像素也更清晰。
        const double bar = 11, thick = 1.8;
        var hBar = new System.Windows.Shapes.Rectangle
        { Width = bar, Height = thick, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var vBar = new System.Windows.Shapes.Rectangle
        { Width = thick, Height = bar, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        hBar.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "FgOnAccent");
        vBar.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "FgOnAccent");

        var glyph = new Grid();
        glyph.Children.Add(hBar);
        glyph.Children.Add(vBar);

        var b = new Border { Width = 24, Height = 24, Cursor = System.Windows.Input.Cursors.Hand, Child = glyph };
        b.Dyn(Border.BackgroundProperty, "Accent").Dyn(Border.CornerRadiusProperty, "RadiusSm");
        b.MouseEnter += (_, _) => b.Opacity = 0.85;
        b.MouseLeave += (_, _) => b.Opacity = 1.0;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        if (tip is not null) b.ToolTip = tip;
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
