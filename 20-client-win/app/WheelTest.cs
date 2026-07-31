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
        // ★ 必须是 App 而不是裸 Application:界面里到处 (App)Application.Current 取各个中心
        //   (翻译下半条就是),裸 Application 会当场 InvalidCastException。这里只构造、不跑
        //   OnStartup —— 各个中心是字段初始化器建的,拿到的就是一份干净的空数据。
        if (Application.Current is null)
        {
            _ = new App(SingleInstance.Acquire(), startHidden: true);
            ThemeManager.Initialize(Skin.Warm);
        }

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

        // 图标核对。★ 用【微风】画:它是标准皮肤,而且是描边风格 —— 描边最能看出路径画错没有
        //   (暖萌是实心的,一条画歪的路径填出来可能只是个看不出问题的黑块)。
        ThemeManager.Initialize(Skin.Breeze);
        var icons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var ic in new[] { IconName.Translation, IconName.Mic, IconName.Dots, IconName.Star, IconName.Ai, IconName.File, IconName.Folder })
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

        // 项目抽屉三个页面的说明框:每种皮肤各出一张,核对描边 + 浅填充是否分得清、且与方块左右对齐
        foreach (var (skin, file) in new[] { (Skin.Breeze, "headers-breeze.png"), (Skin.Ink, "headers-ink.png"), (Skin.Warm, "headers-warm.png") })
        {
            ThemeManager.Initialize(skin);
            var heads = new StackPanel { Width = 400 };
            heads.Children.Add(ProjectPickerView.PageHeader("项目会话", ProjectPickerView.HeaderState.Ongoing,
                Ui.Stack(Ui.Body("选择一个项目"), Ui.Caption("选中项目 = 基于它开会话;普通会话点左上「‹ 普通会话」。"))));
            heads.Children.Add(ProjectPickerView.PageHeader("已完成项目", ProjectPickerView.HeaderState.Done,
                Ui.Stack(Ui.Caption("本空间已收尾的项目。选中可只读浏览;会话区可继续或开分支。"), ChipRow("‹ 返回项目"))));
            heads.Children.Add(ProjectPickerView.PageHeader("已删除项目", ProjectPickerView.HeaderState.Trash,
                Ui.Stack(Ui.Caption("所有工作空间共享,保留 30 天后自动清除。选中可只读浏览。"),
                         ChipRow("‹ 返回项目", "全选", "彻底删除所选 (2)", "退出多选"))));
            var tileRow = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
            foreach (var nm in new[] { "家庭旅行计划", "客厅灯光方案" })
            {
                var inner = new StackPanel();
                inner.Children.Add(Icons.Make(IconName.Folder, 30, "FgSecondary"));
                var lab = new TextBlock { Text = nm, FontSize = 12, Margin = new Thickness(0, 6, 26, 0) };
                lab.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                inner.Children.Add(lab);
                var tb = new Border { Child = inner, Height = 100, Padding = new Thickness(12), Margin = new Thickness(4), BorderThickness = new Thickness(1) };
                tb.SetResourceReference(Border.BackgroundProperty, "BgSurface");
                tb.SetResourceReference(Border.BorderBrushProperty, "Border");
                tb.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
                tileRow.Children.Add(tb);
            }
            heads.Children.Add(tileRow);
            Save(Themed(heads), Path.Combine(outDir, file), 420, 560);
        }

        // 已删除项目方块:底行应显示【来自哪个工作空间】(回收站跨空间共用)
        ThemeManager.Initialize(Skin.Breeze);
        var trashTiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Width = 400 };
        foreach (var (title, wsKey) in new[] { ("家庭旅行计划", "chat"), ("论文摘要翻译", "translation") })
        {
            var inner = new StackPanel();
            inner.Children.Add(Icons.Make(IconName.Folder, 30, "FgSecondary"));
            var lab = new TextBlock { Text = title, FontSize = 12, Margin = new Thickness(0, 6, 26, 0) };
            lab.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            inner.Children.Add(lab);
            var pr = new Project("x", title, "", wsKey, ProjectScope.Personal, DateTime.Now, DeletedAt: DateTime.Now);
            var origin = ProjectPickerView.OriginLabelPreview(pr);
            origin.Margin = new Thickness(0, 0, 30, 0);
            inner.Children.Add(origin);
            var tb = new Border { Child = inner, Height = 100, Padding = new Thickness(12), Margin = new Thickness(4), BorderThickness = new Thickness(1) };
            tb.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            tb.SetResourceReference(Border.BorderBrushProperty, "Border");
            tb.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
            trashTiles.Children.Add(tb);
        }
        Save(Themed(trashTiles), Path.Combine(outDir, "trash-tiles.png"), 420, 150);

        // 滑条:自绘模板必须真能画出来(轨道 + 已选段 + 圆滑块),否则设置页一开就是空白
        ThemeManager.Initialize(Skin.Breeze);
        var sl = new StackPanel { Width = 360 };
        foreach (var (label, val) in new[] { ("整理阈值 120k", 0.28), ("总量上限 中档", 0.5), ("满档", 1.0) })
        {
            var t = new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 6, 0, 2) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            sl.Children.Add(t);
            sl.Children.Add(new Slider { Minimum = 0, Maximum = 1, Value = val, Width = 320, HorizontalAlignment = HorizontalAlignment.Left });
        }
        Save(Themed(sl), Path.Combine(outDir, "slider.png"), 380, 180);

        // 翻译工作空间下半条:程度滑条 + 目标池 + 语言池 + 学习笔记。
        // 无头断言验不了"目标池刚好放得下三个气泡""放大镜有没有被裁""笔记够不够宽",只能画出来看。
        // ★ 三种皮肤各出一张:堆叠卡片只给暖萌,微风/墨白必须看得出【克制】(用户裁定)。
        //   这正是无头断言验不了的东西 —— "克制"只能画出来看。
        var app = (App)Application.Current!;
        foreach (var (skin, file) in new[] { (Skin.Breeze, "translation-bar-breeze.png"),
                                             (Skin.Ink, "translation-bar-ink.png"),
                                             (Skin.Warm, "translation-bar-warm.png") })
        {
            ThemeManager.Initialize(skin);
            ClearTargets(app);
            foreach (var c in new[] { "ja", "en", "de" }) app.Translation.AddTarget(c);
            Save(Themed(SizedBar()), Path.Combine(outDir, file), 1000, (int)TranslationBar.BarHeight + 48);
        }
        ThemeManager.Initialize(Skin.Breeze);
        ClearTargets(app);
        Save(Themed(SizedBar()), Path.Combine(outDir, "translation-bar-empty.png"), 1000, (int)TranslationBar.BarHeight + 48);

        // ★★ 同传模式也要画出来 —— 上一版就是【只在文字模式下建过】,
        //   切到同传时才发现历史卡被建了第二次、元素挂上两个父节点,整个界面打不开。
        //   结构断言看不见这种事:它只在【真的把界面建出来】时才现形。
        app.Interpret.SetMode(TranslationMode.Interpret);
        Save(Themed(SizedBar()), Path.Combine(outDir, "translation-bar-interpret.png"), 1000, (int)TranslationBar.BarHeight + 48);
        app.Interpret.SetMode(TranslationMode.Text);

        // ★ 消息气泡的【正文对比度】:三种皮肤各画一张。
        //   这类毛病(底色被模板写死 -> 白字糊在灰底上)无头断言看不见,只有画出来才发现 —— 已经栽过一次。
        foreach (var (skin, file) in new[] { (Skin.Breeze, "bubbles-breeze.png"), (Skin.Ink, "bubbles-ink.png"), (Skin.Warm, "bubbles-warm.png") })
        {
            ThemeManager.Initialize(skin);
            var conv = new StackPanel { Width = 460 };
            var mine = ChatView.MessageText(user: true);
            mine.Text = "这是我发出去的消息,应该是强调色底 + 反白字,能选中能复制。";
            conv.Children.Add(ChatView.BubbleShell(mine, user: true));
            var theirs = ChatView.MessageText(user: false);
            theirs.Text = "这是 AI 的回复,应该是沉底色 + 正文色。";
            conv.Children.Add(ChatView.BubbleShell(theirs, user: false));
            Save(Themed(conv), Path.Combine(outDir, file), 500, 160);
        }
        ThemeManager.Initialize(Skin.Breeze);

        // ★★ 统一之后的会话面板:直接把【真的 ChatView】画出来。
        //   重构的验收点是"聊天空间逐像素不变",无头断言一个像素也验不了。
        //   这里连带覆盖了 Bubble() 内部三条从未被画过的路径:
        //   折叠 toggle 的取色、附件缩略图、系统消息居中说明。
        try
        {
            ClearTargets(app);
            var seeded = app.Chat.NewSession(null, "chat");
            app.Chat.Send(seeded.SessionId, "帮我看看这段代码为什么会闪退");
            app.Chat.Send(seeded.SessionId, string.Join(Environment.NewLine,
                Enumerable.Range(1, 40).Select(i => $"第 {i} 行:这是一条很长的消息,用来触发默认折叠")));
            foreach (var (skin, file) in new[] { (Skin.Breeze, "conv-breeze.png"),
                                                 (Skin.Ink, "conv-ink.png"),
                                                 (Skin.Warm, "conv-warm.png") })
            {
                ThemeManager.Initialize(skin);
                var cv = new ChatView("chat") { Width = 900, Height = 560 };
                Save(Themed(cv), Path.Combine(outDir, file), 940, 600);
            }
            ThemeManager.Initialize(Skin.Breeze);
        }
        catch (Exception ex)
        {
            // 画不出来不该让整套诊断挂掉 —— 如实说一声,别静默跳过
            Console.WriteLine("wheeltest: 会话面板渲染跳过(" + ex.GetType().Name + ": " + ex.Message + ")");
        }

        // 放大镜发送键:画的就是界面里那一个(SearchSendButton),不是复刻件
        var sendRow = new StackPanel { Orientation = Orientation.Horizontal };
        var sendOn = ChatView.SearchSendButton(() => { }); sendOn.Height = 40;
        var sendOff = ChatView.SearchSendButton(() => { }); sendOff.Height = 40;
        sendOff.IsEnabled = false; sendOff.Opacity = 0.45; sendOff.Margin = new Thickness(16, 0, 0, 0);
        sendRow.Children.Add(sendOn);
        sendRow.Children.Add(sendOff);
        Save(Themed(sendRow), Path.Combine(outDir, "search-send.png"), 160, 70);

        Console.WriteLine("wheeltest: 已输出 wheel-time.png / wheel-date-dual.png / todo-panel.png / greeting.png / icons.png / ink-selected-tile.png / translation-bar-*.png / search-send.png");
        return 0;
    }

    static void ClearTargets(App app)
    {
        foreach (var c in app.Translation.Targets.ToList()) app.Translation.RemoveTarget(c);
    }

    /// <summary>翻译下半条按真实宽度量一遍 —— 三列的宽度分配只有排出来才看得见。</summary>
    static FrameworkElement SizedBar()
    {
        var bar = new TranslationBar { Width = 940 };
        return bar;
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

    // 模拟抽屉说明框里的 chip 行(与 ProjectPickerView.Chip 同样的尺寸口径)
    static FrameworkElement ChipRow(params string[] labels)
    {
        var wrap = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var l in labels)
        {
            var t = new TextBlock { Text = l, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            var b = new Border { Child = t, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 6, 4), BorderThickness = new Thickness(1) };
            b.SetResourceReference(Border.BorderBrushProperty, "Border");
            b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            wrap.Children.Add(b);
        }
        return wrap;
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
