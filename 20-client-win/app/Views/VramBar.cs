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
    Border _clip = null!;

    public VramBar()
    {
        // ★★★ 这里原来写死 `_title.Text = "显存"`,而 Update() 从头到尾没碰过 s.Title ——
        //   于是 P4-S9「拿不到中枢数据就把标题改成『本机显卡(不是中枢的)』」那个修复
        //   **从来没有在界面上生效过**:副机上显示的一直是「显存」+ 副机自己那张卡。
        //   钉着 Title 的几条断言测的是一个孤立的纯函数,所以它们一直是绿的。
        //   ⇒ 标题一律由 Update() 从快照里取(见下)。这里只放一个建构期占位。
        _title.Text = "主机显存";
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
        // ★ 提成字段:拿不到主机数据时要单独把这三段条收掉,而说明那一行要留着
        _clip = new Border { Child = _bar, CornerRadius = new CornerRadius(3), ClipToBounds = true };
        var clip = _clip;

        _caption.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _caption.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        _caption.Margin = new Thickness(0, 5, 0, 0);
        _caption.TextWrapping = TextWrapping.Wrap;

        _root.Children.Add(head);
        _root.Children.Add(clip);
        _root.Children.Add(_caption);
        _root.Margin = new Thickness(0, 0, 0, 10);

        BuildRing();
        var host = new Grid();
        host.Children.Add(_root);
        host.Children.Add(_ring);
        Content = host;

        Visibility = Visibility.Collapsed;   // 读到数据前不占位
    }

    readonly TextBlock _pct;

    // ---- 收起态:环形 + 中间百分比(用户裁定)----
    readonly Grid _ring = new() { Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Center };
    readonly System.Windows.Shapes.Ellipse _ringTrack = new() { StrokeThickness = 3.5 };
    readonly System.Windows.Shapes.Path _ringArc = new() { StrokeThickness = 3.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    readonly TextBlock _ringText = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 9.5, FontWeight = FontWeights.SemiBold };
    bool _collapsed;
    VramSnapshot _last = new(0, 0, 0, false);

    void BuildRing()
    {
        _ringTrack.Stroke = FreeBrush;
        _ring.Children.Add(_ringTrack);
        _ring.Children.Add(_ringArc);
        _ring.Children.Add(_ringText);
        _ring.Visibility = Visibility.Collapsed;
        _ring.Margin = new Thickness(0, 0, 0, 10);
    }

    /// <summary>导航收起时切成环形(只剩 34px 宽,横条没有意义);展开时切回三段横条。</summary>
    public void SetCollapsed(bool collapsed)
    {
        _collapsed = collapsed;
        _root.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _ring.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        Update(_last);
    }

    /// <summary>
    /// 生成占用弧的路径数据(12 点起顺时针)。抽成独立方法是为了能被自检直接断言 ——
    /// 这段曾经在德语区域崩过:小数点是逗号时 "14,5" 被几何解析器当成两个坐标。
    /// </summary>
    public static string ArcPath(double ratio)
    {
        const double r = 14.5, cx = 17, cy = 17;
        var sweep = Math.Clamp(ratio, 0, 0.9999) * 360.0;
        var rad = (sweep - 90) * Math.PI / 180.0;
        var x = cx + r * Math.Cos(rad);
        var y = cy + r * Math.Sin(rad);
        var large = sweep > 180 ? 1 : 0;
        // ★ 整串用不变文化格式化,不要逐个字段打补丁 —— 漏掉任何一个都会在逗号小数点区域崩。
        return FormattableString.Invariant($"M {cx},{cy - r} A {r},{r} 0 {large} 1 {x},{y}");
    }

    void UpdateRing(VramSnapshot s)
    {
        var danger = s.UsedRatio >= VramMonitor.DangerRatio;
        var warn = !danger && s.UsedRatio >= VramMonitor.WarnRatio;
        var brush = danger ? DesktopDanger : warn ? WarnBrush : DesktopBrush;

        _ringText.Text = $"{s.UsedRatio * 100:0}";
        _ringText.Foreground = brush;
        _ringArc.Stroke = brush;

        // 从 12 点顺时针画占用弧。
        // ★ 路径字符串必须【整串】用不变文化格式化 —— 本机是德语区域,小数点是逗号,
        //   "14,5" 会被几何解析器当成两个坐标(实测崩在这:FormatException «M 17,2,5 A 14,5,14,5…»)。
        //   所以用 FormattableString.Invariant 一次性管住所有数字,而不是逐个字段 .ToString(Invariant)。
        _ringArc.Data = Geometry.Parse(ArcPath(s.UsedRatio));
        _ringArc.Fill = null;
        _ring.ToolTip = ToolTip;
    }

    public void Update(VramSnapshot s)
    {
        _last = s;
        Visibility = Visibility.Visible;
        // ★★★ 标题一律来自快照 —— 这一行就是 P4-S9 那个修复缺的那一环。
        _title.Text = s.Title;

        if (!s.HasNumbers)
        {
            // ★★ 用户裁定(2026-08-05):「拿不到就显示主机未连接」——**不是隐藏**。
            //   隐藏是不可读的:用户分不清"出错了"和"这个版本没这个功能"。
            //   ★ 必须在 UpdateRing 之前 return:环上写的是 UsedRatio,
            //     拿不到时它是 0 ⇒ 收起态会显示 **0% = 显存全空**,那是最坏的一种谎。
            ShowUnavailable(s);
            return;
        }

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

        // ══════════════════════════════════════════════════════════════════
        //  ★★★ V20-④:模型段现在含**按需装载**那一层(见 VramMonitor 里那段)。
        //  用户实测:模型已按需装好、显存 6.8 GiB,这一行却写着「暂无已启用模型」——
        //  与底部横条那条「按需模型 · 进行中」当场自相矛盾。
        //
        //  ★★ 但措辞**必须分得开**:D90 裁定③ 禁的是让用户以为按需那一层是他勾的。
        //    「已启用」= 他在组件面板上勾过的;按需装上的**不是**。
        //    ⇒ 有按需成分就把它单独说出来:「模型 6.8(含按需 5.3)」。
        // ══════════════════════════════════════════════════════════════════
        var used = s.ModelReservedGiB + s.DesktopUsedGiB;
        var onDemand = s.TransientGiB > 0.01 ? $"(含按需 {s.TransientGiB:0.0})" : "";
        _caption.Text = s.ModelReservedGiB > 0.01
            ? $"模型 {s.ModelReservedGiB:0.0}{onDemand} + 桌面 {s.DesktopUsedGiB:0.0} / {s.TotalGiB:0.0} GiB"
            : $"已用 {used:0.0} / {s.TotalGiB:0.0} GiB · 暂无已启用模型";
        // ★ 提示框里把两层**逐行**分开:「已启用」是你勾的常驻,「按需装载」是系统按你的授权临时装的。
        //   合成一行会把 D90 裁定③ 那个亏(让人以为自己勾过它)搬到这里来。
        ToolTip = $"装着的模型 max:{s.ModelReservedGiB:0.00} GiB"
                  + (s.TransientGiB > 0.01
                        ? $"\n  ├ 你勾的常驻:{Math.Max(0, s.ModelReservedGiB - s.TransientGiB):0.00} GiB"
                          + $"\n  └ 按需装载(空闲会自动卸):{s.TransientGiB:0.00} GiB"
                        : "")
                  + $"\n当前桌面占用:{s.DesktopUsedGiB:0.00} GiB\n未占用:{s.FreeGiB:0.00} GiB\n总计:{s.TotalGiB:0.00} GiB"
                  + (danger ? "\n\n⚠ 已逼近显存上限" : "");

        _clip.Visibility = Visibility.Visible;
        UpdateRing(s);   // 收起态的环形与展开态的横条读同一份数据
    }

    /// <summary>
    /// 拿不到主机显存时的样子。★ 一个数字都不给 —— 不给 0、不给旧值、不给本机那张卡。
    /// ★★ 三态各有各的说法(标题 + 说明),因为它们的下一步完全不同:
    ///   未连接 → 去开主机 / 起 lan-edge;主机读不到 → 去查主机的驱动。
    ///   合并成一句"读不到"会把人支去错的地方。
    /// </summary>
    void ShowUnavailable(VramSnapshot s)
    {
        _clip.Visibility = Visibility.Collapsed;     // 条子整条收掉:没有数据就不画进度
        _pct.Text = "—";                             // ★ 不写 0% —— 那会读成"显存全空"
        _pct.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _caption.Text = s.Note ?? "";
        ToolTip = $"{s.Title}\n{s.Note}"
                  + (s.HubState is { Length: > 0 } st ? $"\n主机状态:{st}" : "");
        // 收起态的环:同样不画弧、不写百分比
        _ringArc.Data = null;
        _ringText.Text = "—";
        _ring.ToolTip = ToolTip;
    }
}
