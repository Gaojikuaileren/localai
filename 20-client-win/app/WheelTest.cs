// P3c -- 转盘渲染诊断(仅调试用,不进正常启动路径)。
//   localai-client --wheeltest <输出目录>
// 把「时间转盘」和「两个日期转盘并排(模拟抽屉里的开始/结束)」离屏渲染成 PNG,
// 用来肉眼确认三列都画出来了、在抽屉宽度内不被裁掉 —— 因为无头 selftest 验不了渲染。

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;
using LocalAI.Client.Views;

namespace LocalAI.Client;

public static class WheelTest
{
    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);

        // 需要一个 Application + 皮肤字典,DynamicResource 才解析得到颜色(圆圈/强调色等)。
        if (Application.Current is null) { _ = new Application(); ThemeManager.Initialize(Skin.Warm); }

        var (timeEl, _) = WheelPicker.Time(new TimeSpan(9, 35, 0), _ => { });
        Save(Frame("非全天:时间转盘", timeEl, 220), Path.Combine(outDir, "wheel-time.png"), 240, 150);

        var dual = new StackPanel { Orientation = Orientation.Horizontal };
        dual.Children.Add(Labeled("开始日期", WheelPicker.Date(new DateTime(2026, 7, 29), _ => { })));
        dual.Children.Add(new Border { Width = 12 });
        dual.Children.Add(Labeled("结束日期", WheelPicker.Date(new DateTime(2026, 8, 2), _ => { })));
        Save(ClipTo(dual, 360), Path.Combine(outDir, "wheel-date-dual.png"), 380, 170);

        // 待办/家务面板(仿提醒事项):手动 vs AI 建立 × 各种状态 —— 让 AI 星标一眼可辨
        var list = new StackPanel();
        TodoItem[] demo =
        {
            new(TodoCenter.NewId(), "买菜:西红柿、鸡蛋、牛奶", TodoKind.Chore, Due: DateTime.Today.AddHours(18), DueHasTime: true),
            new(TodoCenter.NewId(), "交电费", TodoKind.Personal, Due: DateTime.Today.AddDays(-1), Flagged: true, Priority: TodoPriority.High),
            new(TodoCenter.NewId(), "预约理发", TodoKind.Personal),
            new(TodoCenter.NewId(), "续借图书馆的书", TodoKind.Personal, Due: DateTime.Today.AddDays(2), CreatedByAi: true),
            new(TodoCenter.NewId(), "提醒对方周五体检空腹", TodoKind.Chore, Due: DateTime.Today.AddDays(3), Priority: TodoPriority.Medium, CreatedByAi: true),
            new(TodoCenter.NewId(), "倒垃圾", TodoKind.Chore, Done: true),
        };
        foreach (var t in demo) list.Children.Add(TodoList.Row(t, () => { }, () => { }));
        var panel = Ui.Panel("待办事项", list, IconName.Member, new Thickness(0),
            headerAction: Ui.PlusButton(() => { }, "新增"));
        panel.Width = 320;
        Save(Themed(panel), Path.Combine(outDir, "todo-panel.png"), 340, 380);

        // 日程行:手动 vs AI 建立(AI 带星标)—— 与待办同一套标记
        var evlist = new StackPanel { Width = 300 };
        CalendarEvent[] evs =
        {
            new(DateTime.Today.AddHours(9).AddMinutes(30), DateTime.Today.AddHours(10).AddMinutes(30), "晨会", "我", "家庭"),
            new(DateTime.Today.AddHours(19), DateTime.Today.AddHours(20).AddMinutes(30), "日语课", "我", "个人", CreatedByAi: true),
            new(DateTime.Today, DateTime.Today.AddDays(2), "出差 · 柏林", "我", "个人", AllDay: true, CreatedByAi: true),
        };
        foreach (var ev in evs) evlist.Children.Add(CalendarView.EventRowPreview(ev));
        Save(Themed(evlist), Path.Combine(outDir, "calendar-events.png"), 320, 150);

        // 问候块:大字号主句 + 小助手副句,约 1/3 宽
        var gbox = new StackPanel { Width = 300 };
        var gt = new TextBlock { Text = Greetings.TitleFor(9), FontWeight = FontWeights.SemiBold, FontSize = 30, TextWrapping = TextWrapping.Wrap };
        gt.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        var gs = new TextBlock { Text = Greetings.SubFor(new DateTime(2026, 7, 29, 9, 0, 0)), FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        gs.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        gbox.Children.Add(gt);
        gbox.Children.Add(gs);
        Save(Themed(gbox), Path.Combine(outDir, "greeting.png"), 340, 130);

        // 图标核对:财务管理(钱包)vs 投资(走势图)要能一眼区分
        var icons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var ic in new[] { IconName.Ai, IconName.File, IconName.Pdf, IconName.Folder, IconName.Model })
        {
            var box = new StackPanel { Margin = new Thickness(6, 0, 6, 0) };
            var el = Icons.Make(ic, 34, "FgPrimary");
            box.Children.Add(el);
            var lab = new TextBlock { Text = ic.ToString(), TextAlignment = TextAlignment.Center, FontSize = 10 };
            lab.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            box.Children.Add(lab);
            icons.Children.Add(box);
        }
        Save(Themed(icons), Path.Combine(outDir, "icons.png"), 340, 80);

        // ★ 墨白皮肤下"选中项目"的可读性核对(用户反馈曾黑底黑字)。切到 Ink 皮肤渲染选中 vs 未选中两块。
        ThemeManager.Initialize(Skin.Ink);
        var pair = new StackPanel { Orientation = Orientation.Horizontal };
        pair.Children.Add(InkTile("家庭旅行计划", selected: true));
        pair.Children.Add(new Border { Width = 12 });
        pair.Children.Add(InkTile("客厅灯光方案", selected: false));
        Save(Themed(pair), Path.Combine(outDir, "ink-selected-tile.png"), 300, 150);

        Console.WriteLine("wheeltest: 已输出 wheel-time.png / wheel-date-dual.png / todo-panel.png / greeting.png / icons.png / ink-selected-tile.png");
        return 0;
    }

    // 复刻 ProjectPickerView.FolderTile 的选中/未选中观感,用来在墨白皮肤下核对对比度。
    static FrameworkElement InkTile(string title, bool selected)
    {
        var body = new StackPanel();
        body.Children.Add(Icons.Make(IconName.Folder, 34, selected ? "FgOnSelected" : "FgSecondary"));
        var nm = new TextBlock { Text = title, Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
        nm.SetResourceReference(TextBlock.ForegroundProperty, selected ? "FgOnSelected" : "FgPrimary");
        body.Children.Add(nm);
        var chip = ProjectUi.StatusChip(ProjectStatus.Active, selected ? "FgOnSelected" : null);
        body.Children.Add(chip);
        var tile = new Border { Child = body, Width = 120, Height = 100, Padding = new Thickness(12), BorderThickness = new Thickness(selected ? 2 : 1) };
        tile.SetResourceReference(Border.BackgroundProperty, selected ? "BgSelected" : "BgSurface");
        tile.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "Border");
        tile.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        return tile;
    }

    // 用皮肤的窗口底色垫一层,还原真实观感(而不是纯白)
    static FrameworkElement Themed(FrameworkElement body)
    {
        var b = new Border { Padding = new Thickness(12), Child = body };
        b.SetResourceReference(Border.BackgroundProperty, "BgWindow");
        return b;
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
