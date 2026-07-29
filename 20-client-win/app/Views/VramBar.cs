// P3c -- 左导航的显存占用条(在「当前成员/纠正」上方)。
//
// 用户裁定的三段构成:
//   浅蓝 = 启用的模型 max 占用   蓝 = 当前桌面占用   灰 = 未占用
//   实际占用逼近显存上限时,整条转【红色系】。
//
// ★ 颜色说明:这三段颜色**不随皮肤变**(和风险语义色同理)——
//   显存是资源安全信息,墨白皮肤下也必须能一眼分辨"逼近上限",不能被皮肤稀释成灰阶。
// ★ 读不到 GPU 时整条隐藏,不显示 0% 冒充"很空闲"。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public sealed class VramBar : UserControl
{
    // 正常配色(用户指定)
    static readonly Brush ModelBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0xC5, 0xFE));   // 浅蓝:模型 max
    static readonly Brush DesktopBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xE4)); // 蓝:桌面占用
    static readonly Brush FreeBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xDD, 0xE4));    // 灰:未占用
    // 逼近上限的红色系
    static readonly Brush ModelDanger = new SolidColorBrush(Color.FromRgb(0xF3, 0xB0, 0xA8));
    static readonly Brush DesktopDanger = new SolidColorBrush(Color.FromRgb(0xD9, 0x30, 0x25));
    static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0x74, 0x00));

    readonly Grid _bar = new();
    readonly ColumnDefinition _colModel = new();
    readonly ColumnDefinition _colDesktop = new();
    readonly ColumnDefinition _colFree = new();
    readonly Border _segModel = new();
    readonly Border _segDesktop = new();
    readonly Border _segFree = new();
    readonly TextBlock _caption = new();
    readonly TextBlock _title = new();
    readonly StackPanel _root = new();

    public VramBar()
    {
        _title.Text = "显存";
        _title.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _title.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var pct = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
        pct.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        pct.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        _pct = pct;

        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 5) };
        DockPanel.SetDock(_title, Dock.Left); head.Children.Add(_title);
        DockPanel.SetDock(pct, Dock.Right); head.Children.Add(pct);

        _bar.ColumnDefinitions.Add(_colModel);
        _bar.ColumnDefinitions.Add(_colDesktop);
        _bar.ColumnDefinitions.Add(_colFree);
        _bar.Height = 6;
        Grid.SetColumn(_segModel, 0); Grid.SetColumn(_segDesktop, 1); Grid.SetColumn(_segFree, 2);
        _bar.Children.Add(_segModel); _bar.Children.Add(_segDesktop); _bar.Children.Add(_segFree);

        // 整条圆角:两端裁圆,中间平接(靠 Clip 实现,避免每段各自圆角显得断续)
        var clip = new Border { Child = _bar, CornerRadius = new CornerRadius(3), ClipToBounds = true };

        _caption.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _caption.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        _caption.Margin = new Thickness(0, 5, 0, 0);
        _caption.TextWrapping = TextWrapping.Wrap;

        _root.Children.Add(head);
        _root.Children.Add(clip);
        _root.Children.Add(_caption);
        _root.Margin = new Thickness(0, 0, 0, 10);
        Content = _root;

        Visibility = Visibility.Collapsed;   // 读到数据前不占位
    }

    readonly TextBlock _pct;

    public void Update(VramSnapshot s)
    {
        if (!s.Available || s.TotalGiB <= 0)
        {
            Visibility = Visibility.Collapsed;   // 读不到就藏起来,不显示 0 冒充空闲
            return;
        }
        Visibility = Visibility.Visible;

        // 星号宽度按 GiB 分配 -> 三段比例即真实占比
        _colModel.Width = new GridLength(Math.Max(0.0001, s.ModelReservedGiB), GridUnitType.Star);
        _colDesktop.Width = new GridLength(Math.Max(0.0001, s.DesktopUsedGiB), GridUnitType.Star);
        _colFree.Width = new GridLength(Math.Max(0.0001, s.FreeGiB), GridUnitType.Star);

        var danger = s.UsedRatio >= VramMonitor.DangerRatio;
        var warn = !danger && s.UsedRatio >= VramMonitor.WarnRatio;

        _segModel.Background = danger ? ModelDanger : ModelBrush;
        _segDesktop.Background = danger ? DesktopDanger : warn ? WarnBrush : DesktopBrush;
        _segFree.Background = FreeBrush;

        _pct.Text = $"{s.UsedRatio * 100:0}%";
        _pct.Foreground = danger ? DesktopDanger : warn ? WarnBrush : (Brush)FindResource("FgSecondary");

        var used = s.ModelReservedGiB + s.DesktopUsedGiB;
        _caption.Text = s.ModelReservedGiB > 0.01
            ? $"模型 {s.ModelReservedGiB:0.0} + 桌面 {s.DesktopUsedGiB:0.0} / {s.TotalGiB:0.0} GiB"
            : $"已用 {used:0.0} / {s.TotalGiB:0.0} GiB · 暂无已启用模型";
        ToolTip = $"启用的模型 max:{s.ModelReservedGiB:0.00} GiB\n当前桌面占用:{s.DesktopUsedGiB:0.00} GiB\n未占用:{s.FreeGiB:0.00} GiB\n总计:{s.TotalGiB:0.00} GiB"
                  + (danger ? "\n\n⚠ 已逼近显存上限" : "");
    }
}
