// P3c -- 浮窗。用户裁定:查看某天时间线、编辑当天日程,都应是【浮窗】而不是弹出独立窗口。
//
// 用 WPF Popup 实现:
//   · StaysOpen=false -> 点浮窗外面自动关(与抽屉"点外部即关"一致的手感);
//   · AllowsTransparency=true -> 圆角外必须透明,否则圆角四周会露出弹窗自身的黑底(实测)。
//     设计 §7 禁的是【整窗大面积】毛玻璃/半透明带来的显存开销;一个小的、临时的浮窗
//     用 layered 合成代价可忽略,而且不用它就做不出圆角与投影。这是有意的例外。
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

    /// <summary>在鼠标位置弹出(点日期格时用)。锚元素只需存活,不要求它还在原位 ——
    /// 因为点击往往会触发重建,原来的格子已经被换掉了。</summary>
    public static void ShowAtMouse(FrameworkElement anchor, string title, UIElement body, double width = 320)
        => Show(anchor, title, body, width, atMouse: true);

    public static void Show(FrameworkElement anchor, string title, UIElement body, double width = 320, bool atMouse = false)
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
            Placement = atMouse ? PlacementMode.MousePoint : PlacementMode.Right,
            StaysOpen = false,         // 点外面就关
            AllowsTransparency = true, // 圆角外透明,否则四角露黑底
            HorizontalOffset = 8,
            PopupAnimation = PopupAnimation.Fade,
        };
        popup.Closed += (_, _) => Open.Remove(popup);
        Open.Add(popup);
        popup.IsOpen = true;
    }
}
