// P3c -- 浮窗。用户裁定:查看某天时间线、编辑当天日程,都应是【浮窗】而不是弹出独立窗口。
//
// 用 WPF Popup 实现:
//   · StaysOpen=false -> 点浮窗外面自动关(与抽屉"点外部即关"一致的手感);
//   · AllowsTransparency=false -> 实色,不走 layered 合成(设计 §7 显存约束);
//   · Placement 贴着触发元素,超出屏幕时 WPF 自动翻转到另一侧。
//
// 同一时刻只保留一个浮窗(CloseAll),避免层层叠叠。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace LocalAI.Client.Views;

public static class Flyout
{
    static readonly List<Popup> Open = new();

    public static void CloseAll()
    {
        foreach (var p in Open.ToList()) { p.IsOpen = false; }
        Open.Clear();
    }

    public static void Show(FrameworkElement anchor, string title, UIElement body, double width = 320)
    {
        CloseAll();   // 同时只开一个

        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
        var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontSubtitle");
        DockPanel.SetDock(t, Dock.Left);
        head.Children.Add(t);

        var content = new StackPanel();
        content.Children.Add(head);
        content.Children.Add(body);

        var card = new Border
        {
            Child = content,
            Width = width,
            MaxHeight = 460,
            Padding = new Thickness(16),
            BorderThickness = new Thickness(1),
        };
        card.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect
        { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.20, Color = Colors.Black };

        var popup = new Popup
        {
            Child = card,
            PlacementTarget = anchor,
            Placement = PlacementMode.Right,
            StaysOpen = false,          // 点外面就关
            AllowsTransparency = false, // 实色,不走 layered 合成
            HorizontalOffset = 8,
            PopupAnimation = PopupAnimation.Fade,
        };
        popup.Closed += (_, _) => Open.Remove(popup);
        Open.Add(popup);
        popup.IsOpen = true;
    }
}
