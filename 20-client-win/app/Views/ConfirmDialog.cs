// P3c -- 自绘确认框(取代系统 MessageBox)。用户裁定:提醒框要用我们自己的风格,不要系统的。
//
// ★ 为什么用独立 Window 而不是浮窗/抽屉内嵌:
//   删除项目的确认是从【抽屉里的三点菜单】触发的。浮窗(Flyout)要登记到 Overlay,
//   而 Overlay 只允许一个浮层 —— 登记时会把抽屉一起关掉,连锚点元素都随之消失,
//   于是"点了删除没有任何反应"(用户两次反馈的成因)。
//   独立模态窗口不参与 Overlay 的单浮层规则,任何上下文里都稳。
//
// 外观:无系统边框(WindowStyle=None + AllowsTransparency),自绘圆角卡片 + 投影 + 令牌配色,
//   跟随皮肤;危险确认用实心红按钮。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace LocalAI.Client.Views;

public static class ConfirmDialog
{
    /// <summary>弹出确认框。返回 true = 用户确认。danger=true 时确认按钮为实心红。</summary>
    public static bool Show(string title, string message, string confirmText = "确定",
                            string cancelText = "取消", bool danger = false)
    {
        var result = false;

        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            SizeToContent = SizeToContent.Height,
            Width = 400,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        if (Application.Current?.MainWindow is { IsVisible: true } owner) win.Owner = owner;

        var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontSubtitle");

        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        body.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");

        var ok = danger
            ? Ui.DangerFilled(confirmText, (_, _) => { result = true; win.Close(); })
            : Ui.Primary(confirmText, (_, _) => { result = true; win.Close(); });
        var cancel = Ui.Secondary(cancelText, (_, _) => win.Close());

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(new Border { Child = cancel, Margin = new Thickness(0, 0, 10, 0) });
        buttons.Children.Add(ok);

        var stack = new StackPanel();
        stack.Children.Add(t);
        stack.Children.Add(body);
        stack.Children.Add(buttons);

        var card = new Border { Child = stack, Padding = new Thickness(20), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");

        // 阴影画在外层(直接给圆角 Border 加 Effect 会在圆角处叠出脏边)
        var shadowHost = new Border
        {
            Child = card,
            Margin = new Thickness(14),
            Background = Brushes.Transparent,
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = 0.36, Color = Colors.Black, RenderingBias = RenderingBias.Quality },
        };
        win.Content = shadowHost;

        // Esc 取消 —— 键盘留一条【只会取消、不会误触发】的退路。
        // ★ 回车【不】确认(用户裁定:回车只触发发送,不触发任何别的按钮)。
        //   确认框往往是在连按回车的节奏里弹出来的,一个惯性回车就把东西删了,
        //   而这些操作(删除项目、彻底删除、清空记忆)恰恰是不可回收的。确认必须用鼠标点。
        //   (挂在 win.KeyDown 上是【冒泡】事件,按钮退出焦点体系后焦点停在 win 自身,依旧收得到。)
        win.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; win.Close(); }
        };

        win.ShowDialog();
        return result;
    }
}
