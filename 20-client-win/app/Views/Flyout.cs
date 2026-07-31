// P3c -- 浮窗(轻量、贴着触发点弹出的小面板:当日日程、年月选择、日程编辑)。
//
// ★ 它只是【浮层】的一种,与外壳的抽屉共用同一套规则 —— 见 Overlay.cs。
//   所以这里不再自己维护"同时只开一个",而是登记到 Overlay,由它统一裁决。
//
// 实现要点:
//   · StaysOpen=false -> 点浮窗外面自动关;
//   · AllowsTransparency=true -> 圆角外必须透明,否则四角会露出弹窗自身的黑底(实测)。
//     设计 §7 禁的是【整窗大面积】半透明带来的显存开销;一个小的临时浮窗用 layered 合成
//     代价可忽略,且不开就做不出圆角与投影 —— 有意的例外。
//   · 阴影画在【外层容器】而非圆角卡片本身:直接给带 CornerRadius 的 Border 加 Effect,
//     会在圆角处与描边叠出脏边;外层还要留 Margin 给阴影渲染,否则被弹窗边界裁掉。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace LocalAI.Client.Views;

public static class Flyout
{
    /// <summary>阴影渲染需要的外圈留白 —— 不留会被弹窗边界裁掉。</summary>
    const double ShadowPad = 14;
    /// <summary>浮窗与鼠标之间的间距。贴着光标会挡住刚点的内容,也显得局促。</summary>
    const double MouseGap = 18;

    static Popup? _current;

    public static bool IsOpen => _current is not null;

    /// <summary>
    /// 这个元素是不是【当前浮窗内部】的。
    /// ★ 浮窗的内容活在独立的 Popup 视觉树里,外壳顺着主窗口的树往上找是找不到它的 ——
    ///   不特判的话,点浮窗里的任何东西都会被判成"点在浮层外面",于是选没选上、浮层先没了。
    /// </summary>
    public static bool IsInside(DependencyObject? node)
    {
        var child = _current?.Child;
        if (child is null) return false;
        for (var n = node; n is not null; n = VisualTreeHelper.GetParent(n))
            if (ReferenceEquals(n, child)) return true;
        return false;
    }

    public static void CloseAll()
    {
        var p = _current;
        _current = null;
        if (p is not null) p.IsOpen = false;
    }

    /// <summary>在鼠标位置弹出(点日期格时用)。锚元素只需存活,不要求它还在原位 ——
    /// 点击往往会触发重建,原来那个格子已经被换掉了。</summary>
    public static void ShowAtMouse(FrameworkElement anchor, string title, UIElement body,
                                   double width = 320, UIElement? headerAction = null)
        => Show(anchor, title, body, width, atMouse: true, headerAction: headerAction);

    /// <param name="headerAction">放在标题行【右侧】的操作(如"新增日程"),与标题同一行。</param>
    public static void Show(FrameworkElement anchor, string title, UIElement body, double width = 320,
                            bool atMouse = false, UIElement? headerAction = null)
    {
        var content = new StackPanel();
        // 标题为空且无 headerAction => 无头模式:内容自带 chrome(项目选择器就是这么用的)
        if (!string.IsNullOrEmpty(title) || headerAction is not null)
        {
            var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
            var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontSubtitle");
            DockPanel.SetDock(t, Dock.Left);
            head.Children.Add(t);
            if (headerAction is not null)
            {
                DockPanel.SetDock(headerAction, Dock.Right);
                head.Children.Add(headerAction);
            }
            content.Children.Add(head);
        }
        content.Children.Add(body);

        var card = new Border
        {
            Child = content,
            Width = width,
            MaxHeight = 460,
            Padding = new Thickness(16),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            ClipToBounds = true,   // 内容不得溢出圆角,否则四角会露出方角的子元素边缘
        };
        card.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");

        var shadowHost = new Border
        {
            Child = card,
            Margin = new Thickness(ShadowPad),
            Background = Brushes.Transparent,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 26,      // 与主页底色接近时,层次全靠这层阴影撑起来
                ShadowDepth = 6,
                Direction = 270,
                Opacity = 0.34,
                Color = Colors.Black,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality,
            },
        };

        var popup = new Popup
        {
            Child = shadowHost,
            PlacementTarget = anchor,
            Placement = atMouse ? PlacementMode.MousePoint : PlacementMode.Right,
            StaysOpen = false,
            AllowsTransparency = true,
            HorizontalOffset = atMouse ? MouseGap : 10,
            VerticalOffset = atMouse ? MouseGap : 0,
            PopupAnimation = PopupAnimation.Fade,
        };

        void Close() { _current = null; popup.IsOpen = false; }

        // 点浮窗外面时 WPF 自己会关 -> 通知协调器清账,免得留下一个已失效的关闭回调
        popup.Closed += (_, _) => { if (ReferenceEquals(_current, popup)) _current = null; Overlay.Unregister(Close); };

        // ★ 浮窗【叠】在上面而不是替换 —— 它常常是从抽屉里弹出来的,
        //   替换会把承载它的抽屉一起关掉(用户报的"点了就关了")。
        Overlay.Push(Close);
        _current = popup;
        popup.IsOpen = true;
    }
}
