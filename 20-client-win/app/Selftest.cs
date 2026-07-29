// P3c -- 无界面自检。GUI 部分没法自动断言,但**决定行为正确性的逻辑**都能:
// 自启注册表读写 · 退出善后的"只跑一次/区分托盘与真退出" · 配对档案持久化 · 三语文案完整性 · 皮肤令牌齐备。
// 项目习惯:输出 PASS=n FAIL=0。
//
// 纪律:自检**绝不碰真实状态** —— 状态目录指向临时目录,自启写到 HKCU 下的测试子键,跑完删掉。

using System.Text.Json;
using LocalAI.Client.I18n;
using LocalAI.Client.Services;
using LocalAI.ClientTransport;
using Microsoft.Win32;

namespace LocalAI.Client;

public static class Selftest
{
    public static int Run()
    {
        int pass = 0, fail = 0;
        void Assert(bool c, string m) { if (c) { pass++; Console.WriteLine("  PASS  " + m); } else { fail++; Console.WriteLine("  FAIL  " + m); } }

        var tmp = Path.Combine(Path.GetTempPath(), "localai-client-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        var oldState = Environment.GetEnvironmentVariable(AppPaths.StateEnvVar);
        var testRunKey = @"Software\LocalAI\SelftestRun";
        var oldKeyPath = Autostart.KeyPath;

        Environment.SetEnvironmentVariable(AppPaths.StateEnvVar, tmp);
        Autostart.KeyPath = testRunKey;

        try
        {
            // ---- 状态目录 ----
            Assert(AppPaths.StateDir == tmp, "状态目录可被环境变量覆盖(自检不碰真实档案)");
            AppPaths.EnsureStateDir();
            Assert(Directory.Exists(tmp), "状态目录会被创建");

            // ---- 设置持久化 ----
            var s = new AppSettings { Skin = Skin.Ink, Language = "ja-JP", MinimizeToTrayOnClose = false };
            s.Save();
            var back = AppSettings.Load();
            Assert(back.Skin == Skin.Ink && back.Language == "ja-JP" && !back.MinimizeToTrayOnClose, "界面偏好往返持久化");
            Assert(new AppSettings().MinimizeToTrayOnClose, "默认「关窗口留在托盘」为开(用户要求)");
            Assert(new AppSettings().Skin == Skin.Breeze, "默认皮肤 = 微风(设计 §7)");

            // ---- 开机自启(写在 HKCU 测试子键,不污染真实启动项)----
            Assert(!Autostart.IsEnabled(), "初始未设置自启");
            Autostart.Enable();
            Assert(Autostart.IsEnabled(), "可以打开开机自启");
            using (var k = Registry.CurrentUser.OpenSubKey(testRunKey))
            {
                var v = k?.GetValue(Autostart.ValueName) as string ?? "";
                Assert(v.Contains("--tray"), "自启命令带 --tray(登录时直接进托盘,不弹窗打扰)");
                Assert(v.StartsWith("\""), "自启命令给路径加了引号(路径含空格不会被截断)");
            }
            Assert(Autostart.IsCurrent(), "自启项指向当前 exe(exe 换位置后应重写)");
            Autostart.Disable();
            Assert(!Autostart.IsEnabled(), "可以关闭开机自启");

            // ---- 退出善后:只跑一次 + 顺序 + 超时不拖死 ----
            var order = new List<string>();
            var co = new ShutdownCoordinator();
            co.Register("a", () => order.Add("a"));
            co.Register("b", () => order.Add("b"));
            var first = co.RunOnceAsync("test").GetAwaiter().GetResult();
            var second = co.RunOnceAsync("test-again").GetAwaiter().GetResult();
            Assert(first && !second, "善后**恰好执行一次**(多入口重复调用不会重复清理)");
            Assert(order.SequenceEqual(new[] { "a", "b" }), "善后步骤按注册顺序执行");
            Assert(co.HasRun, "善后状态可查询");

            var co2 = new ShutdownCoordinator { Budget = TimeSpan.FromMilliseconds(200) };
            var ran = false;
            co2.Register("hang", async ct => await Task.Delay(TimeSpan.FromSeconds(30), ct));
            co2.Register("after", () => ran = true);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            co2.RunOnceAsync("timeout-test").GetAwaiter().GetResult();
            sw.Stop();
            Assert(sw.ElapsedMilliseconds < 3000, $"卡住的清理步骤会被预算掐断,不拖住关机(用时 {sw.ElapsedMilliseconds}ms)");
            Assert(!ran, "预算耗尽后跳过剩余步骤(而不是无限等下去)");

            var co3 = new ShutdownCoordinator();
            var reached = false;
            co3.Register("boom", () => throw new InvalidOperationException("x"));
            co3.Register("next", () => reached = true);
            co3.RunOnceAsync("fault-test").GetAwaiter().GetResult();
            Assert(reached, "某一步失败不阻断后续善后步骤(尽力而为)");

            // 死锁回归:退出时善后是 async(含网络调用),若在有同步上下文的线程(WPF UI 线程)上
            // 直接阻塞等待,续体会想回该线程而与阻塞互相死等。App.RunCleanup 的正解 = Task.Run 脱离
            // 上下文再等。这里装一个"永不执行 post 的上下文"验证该模式不会 hang(带超时,失败也不卡住自检)。
            var prevCtx = SynchronizationContext.Current;
            try
            {
                var blocking = new NeverRunsSyncContext();
                SynchronizationContext.SetSynchronizationContext(blocking);
                var co4 = new ShutdownCoordinator();
                var did = false;
                co4.Register("net-like", async ct => { await Task.Delay(20, ct); did = true; });
                var finished = Task.Run(() => co4.RunOnceAsync("deadlock-test")).Wait(TimeSpan.FromSeconds(3));
                Assert(finished && did, "退出善后不会在 UI 同步上下文上死锁(Task.Run 脱离上下文 + ConfigureAwait(false))");
                Assert(blocking.Posts == 0, "善后续体没有被 post 回会死锁的 UI 上下文");
            }
            finally { SynchronizationContext.SetSynchronizationContext(prevCtx); }

            // ---- 配对档案:配一次就记住 ----
            var hub = new HubClient();
            Assert(!hub.IsPaired && hub.State == HubState.NotPaired, "没有档案时 = 尚未配对");
            var profile = new ClientProfile
            {
                EdgeUrl = "https://localai-test.local:8443", HubId = "hub-1", KeyName = "k",
                CaCertB64 = "", DeviceCertB64 = "", Dial = "192.168.178.61:8443",
            };
            File.WriteAllText(AppPaths.ProfilePath, JsonSerializer.Serialize(profile));
            var hub2 = new HubClient();
            Assert(hub2.IsPaired, "重启后能从磁盘读回配对档案(配一次就记住,不再重复配对)");
            Assert(hub2.Profile!.Dial == "192.168.178.61:8443", "档案里记住了拨号地址(下次能自动连)");

            hub2.UnpairLocal();
            Assert(!File.Exists(AppPaths.ProfilePath) && !hub2.IsPaired, "解除配对会删掉本机档案");

            // ---- 显存条分段口径 ----
            var vs = new VramSnapshot(16.0, 4.0, 6.0, true);
            Assert(Math.Abs(vs.FreeGiB - 6.0) < 0.001, "显存三段相加等于总量(模型 + 桌面 + 未占用)");
            Assert(Math.Abs(vs.UsedRatio - 10.0 / 16.0) < 0.001, "占用比例 = (模型 + 桌面) / 总量");
            var vFull = new VramSnapshot(16.0, 10.0, 5.6, true);
            Assert(vFull.UsedRatio >= VramMonitor.DangerRatio, "逼近上限时越过危险阈值(界面转红)");
            var vNone = new VramSnapshot(0, 0, 0, false, "no gpu");
            Assert(!vNone.Available && vNone.UsedRatio == 0, "读不到 GPU 时标记不可用(界面隐藏该条,不显示 0 冒充空闲)");
            var vOver = new VramSnapshot(16.0, 12.0, 8.0, true);
            Assert(vOver.FreeGiB == 0, "占用超过总量时未占用段不为负");
            Assert(VramMonitor.Interval.TotalSeconds is >= 1 and <= 5, "显存轮询间隔在合理区间(默认 2 秒)");

            // 区域回归:显存环形的弧线路径是【拼出来的字符串再被几何解析器读回】,
            // 这类地方对小数点符号极敏感。本机是德语区域(小数点=逗号),曾因此启动即崩:
            // FormatException «M 17,2,5 A 14,5,14,5 …» —— 半径 14.5 被写成 14,5 后当成两个坐标。
            // 注意:路径里逗号本就是 x,y 分隔符,所以不能笼统查逗号,要查【小数必须用点】。
            var prevCulture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                var half = Views.VramBar.ArcPath(0.5);
                Assert(half.Contains("14.5"), "德语区域下半径仍写成 14.5(不变文化)  实得:" + half);
                Assert(!half.Contains("14,5"), "德语区域下不会出现 14,5 这种会被解析成两个坐标的写法");

                // 逐比例确认能真的被几何解析器读回(这才是最终判据)
                var allParsed = true; string? bad = null;
                foreach (var ratio in new[] { 0.0, 0.065, 0.25, 0.5, 0.78, 0.9, 1.0 })
                {
                    try { System.Windows.Media.Geometry.Parse(Views.VramBar.ArcPath(ratio)); }
                    catch { allParsed = false; bad = ratio.ToString(); break; }
                }
                Assert(allParsed, "各占用比例下的环形弧线都能被 Geometry.Parse 解析" + (bad is null ? "" : " 失败于 ratio=" + bad));
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = prevCulture; }

            // ---- 响应式布局:全屏 ↔ 最小窗口(= 屏幕四分之一)都不畸变,且缩放要丝滑 ----
            // 用户反馈"跳来跳去" -> 连续量做插值、离散量加迟滞。这里逐尺寸断言这两点。
            (double w, double h, string name)[] sizes =
            {
                (3840, 2160, "4K 全屏"), (2560, 1440, "2K 全屏"), (1920, 1080, "1080p 全屏"),
                (1440, 900, "设计基线"), (1280, 720, "2K 屏的四分之一 = 最小窗口"),
                (960, 540, "HD 屏的四分之一 = 最小窗口"),
            };
            var layoutOk = true; string? layoutBad = null;
            foreach (var (winW, winH, name) in sizes)
            {
                var (w, h) = Views.Layout.ContentSize(winW, winH);
                var calW = Views.Layout.CalendarWidth(w);
                var contentW = Math.Max(0, w - calW - 64);
                var cols = Views.Layout.ProjectColumns(contentW);
                var curve = Views.Layout.CurveHeight(h);
                var slots = Views.Layout.HourlySlots(contentW / 3);
                var panelMax = Views.Layout.PanelMaxHeight(h);
                var tileH = Views.Layout.TileHeight(h);

                if (cols < Views.Layout.MinTileColumns || cols > Views.Layout.MaxTileColumns) { layoutOk = false; layoutBad = name + " 列数越界 " + cols; break; }
                if (contentW / cols < 120) { layoutOk = false; layoutBad = name + " 单块过窄 " + (contentW / cols).ToString("0"); break; }
                if (panelMax < 90 || tileH < 100) { layoutOk = false; layoutBad = name + " 高度过小"; break; }
                if (curve < 28) { layoutOk = false; layoutBad = name + " 曲线高度过小会显示不全 " + curve; break; }
                if (slots < Views.Layout.MinHourlySlots || slots > Views.Layout.MaxHourlySlots) { layoutOk = false; layoutBad = name + " 逐小时格数异常 " + slots; break; }
                if (calW < 260) { layoutOk = false; layoutBad = name + " 日历栏过窄 " + calW; break; }
            }
            Assert(layoutOk, "全屏→最小窗口(屏幕四分之一)各档判据均成立" + (layoutBad is null ? "" : "  失败于:" + layoutBad));

            // 最小窗口 = 屏幕的四分之一大小(面积四分之一 = 宽高各一半)
            var min2K = Views.Layout.MinWindowFor(2560, 1440);
            Assert(min2K.W == 1280 && min2K.H == 720, $"2K 屏最小窗口 = 1280×720  实得 {min2K.W}×{min2K.H}");
            var minHD = Views.Layout.MinWindowFor(1920, 1080);
            Assert(minHD.W == 960 && minHD.H == 540, $"HD 屏最小窗口 = 960×540  实得 {minHD.W}×{minHD.H}");

            // 连续量:逐像素扫描,任何一步的跳变都不能超过 1px(= 丝滑,没有分档硬跳)
            double maxJump = 0; string? jumpAt = null;
            double prevCurve = Views.Layout.CurveHeight(400), prevPanel = Views.Layout.PanelMaxHeight(400),
                   prevTile = Views.Layout.TileHeight(400), prevCal = Views.Layout.CalendarWidth(400);
            for (double v = 401; v <= 2200; v += 1)
            {
                var c = Views.Layout.CurveHeight(v); var pm = Views.Layout.PanelMaxHeight(v);
                var th = Views.Layout.TileHeight(v); var cw = Views.Layout.CalendarWidth(v);
                foreach (var (cur, prev, tag) in new[] { (c, prevCurve, "曲线高度"), (pm, prevPanel, "限高"), (th, prevTile, "方块高度"), (cw, prevCal, "日历宽") })
                {
                    var jump = Math.Abs(cur - prev);
                    if (jump > maxJump) { maxJump = jump; jumpAt = tag + "@" + v.ToString("0"); }
                }
                prevCurve = c; prevPanel = pm; prevTile = th; prevCal = cw;
            }
            Assert(maxJump <= 1.0, $"连续量逐像素最大跳变 ≤1px(丝滑,无分档硬跳)  实得 {maxJump} 于 {jumpAt}");

            // 迟滞:在阈值附近来回拖动不应反复切换列数
            var atBoundary = Views.Layout.TileIdealWidth * 4;   // 恰好 4 列的边界
            var stableCols = true;
            var cur4 = Views.Layout.ProjectColumns(atBoundary + 5, 4);
            var back4 = Views.Layout.ProjectColumns(atBoundary - 5, cur4);
            if (cur4 != 4 || back4 != 4) stableCols = false;
            Assert(stableCols, $"列数在边界±5px 来回拖动时保持不变(迟滞生效)  实得 {cur4}/{back4}");

            // 迟滞:格数边界(每格 46px)附近来回拖动应保持不变
            var slotBoundary = 46.0 * 6;   // 恰好 6 格
            var slotsStable = Views.Layout.HourlySlots(slotBoundary + 6, 6) == 6
                           && Views.Layout.HourlySlots(slotBoundary - 6, 6) == 6;
            Assert(slotsStable, "逐小时格数在边界附近不横跳(迟滞生效)");

            // 项目方块:宽度增加时列数单调不减
            var mono = true; int prevCols = 0;
            for (double w = 400; w <= 3000; w += 50)
            {
                var c = Views.Layout.ProjectColumns(w);
                if (c < prevCols) { mono = false; break; }
                prevCols = c;
            }
            Assert(mono, "可用宽度变大时项目列数单调不减");
            Assert(Views.Layout.ProjectColumns(0) >= Views.Layout.MinTileColumns, "宽度未知(0/NaN)时回落到最小列数,不会算出 0 列");
            Assert(Views.Layout.ProjectColumns(double.NaN) >= Views.Layout.MinTileColumns, "NaN 宽度不会导致 0 列");

            // 列数不得超过项目数 —— 否则多出的空列就是右侧一块空白(用户反馈的排版问题)
            Assert(Views.Layout.ProjectColumns(3000, 0, 4) == 4, "很宽但只有 4 个项目时用 4 列(方块变宽填满,右侧不留空列)");
            Assert(Views.Layout.ProjectColumns(3000, 0, 2) == 2, "只有 2 个项目时最多 2 列");
            Assert(Views.Layout.ProjectColumns(3000, 6, 3) == 3, "项目变少时列数立刻收缩,不走迟滞(否则留空列)");
            Assert(Views.Layout.ProjectColumns(3000, 0, 100) <= Views.Layout.MaxTileColumns, "项目再多也不超过列数上限");

            // 宽度富余 -> 更多格子 + 更细的时间间隔(用户:全屏时天气多出的宽度要利用起来)
            var narrowSlots = Views.Layout.HourlySlots(200);
            var wideSlots = Views.Layout.HourlySlots(600);
            Assert(wideSlots > narrowSlots, $"卡片更宽时逐小时格数更多({narrowSlots} -> {wideSlots})");
            Assert(Views.Layout.HourlyStepHours(narrowSlots) >= Views.Layout.HourlyStepHours(wideSlots),
                   "格子多时时间间隔更细(3h -> 2h -> 1h),多出的宽度换成更细的粒度");
            Assert(Views.Layout.HourlyStepHours(12) == 1 && Views.Layout.HourlyStepHours(8) == 2 && Views.Layout.HourlyStepHours(6) == 3,
                   "间隔分档正确:12 格=1h · 8 格=2h · 6 格=3h");
            var span = wideSlots * Views.Layout.HourlyStepHours(wideSlots);
            Assert(span is >= 8 and <= 26, $"逐小时覆盖的时间跨度保持在半天到一天之间(实得 {span}h)");

            // 月历高度必须够 —— 否则最后一两行被裁(用户反馈"按月显示不全")
            // 周/月排布【同尺寸】(用户裁定),且必须容得下 6 行月历(否则"按月显示不全")
            Assert(Views.CalendarView.PanelHeight >= 28 + 18 + 6 * 30 + 40,
                   $"日历面板高度容得下 6 行月历 + 表头 + 当日日程区(实得 {Views.CalendarView.PanelHeight})");

            // ---- 项目中心(主页田字格 + 深链)----
            var pc = new ProjectCenter();
            Assert(pc.Items.Count == 0, "项目中心初始为空");
            pc.Add(new Project("a", "旧项目", "x", "chat", ProjectScope.Family, DateTime.Now.AddHours(-3)));
            pc.Add(new Project("b", "新项目", "y", "courses", ProjectScope.Personal, DateTime.Now.AddMinutes(-1)));
            Assert(pc.Recent().First().ProjectId == "b", "项目按最近打开排序(主页要的是回到刚才那件事)");
            pc.Touch("a");
            Assert(pc.Recent().First().ProjectId == "a", "打开项目后它排到最前");

            // ---- 日历:全天 / 跨天的分段计算 ----
            // 跨天日程要在每一周里画成一条贯穿多格的长条,跨到区间外的部分要被裁断并标出续前/续后。
            Views.CalendarData.Events.Clear();
            var monday = new DateTime(2026, 7, 27);   // 周一
            Views.CalendarData.Events.Add(new Views.CalendarEvent(monday.AddDays(1), monday.AddDays(3), "跨三天", "我", "家庭", AllDay: true));
            Views.CalendarData.Events.Add(new Views.CalendarEvent(monday.AddDays(5), monday.AddDays(9), "跨周", "我", "家庭", AllDay: true));
            Views.CalendarData.Events.Add(new Views.CalendarEvent(monday.AddHours(10), monday.AddHours(11), "定时", "我", "家庭"));

            var week1 = Views.CalendarData.SpansIn(monday, 7);
            Assert(week1.Count == 2, $"本周有两条全天条(实得 {week1.Count})");
            var s3 = week1.First(x => x.Ev.Title == "跨三天");
            Assert(s3.Col == 1 && s3.Span == 3, $"跨三天:第 2 格起、占 3 格(实得 col={s3.Col} span={s3.Span})");
            Assert(!s3.ClipStart && !s3.ClipEnd, "完全落在本周内的条,两端都不裁断");
            var sx = week1.First(x => x.Ev.Title == "跨周");
            Assert(sx.Col == 5 && sx.Span == 2, $"跨周条在本周占最后 2 格(实得 col={sx.Col} span={sx.Span})");
            Assert(!sx.ClipStart && sx.ClipEnd, "延伸到下周的条:右端标记为续后");

            var week2 = Views.CalendarData.SpansIn(monday.AddDays(7), 7);
            var sx2 = week2.First(x => x.Ev.Title == "跨周");
            Assert(sx2.Col == 0 && sx2.Span == 3, $"跨周条在下周从第 1 格起占 3 格(实得 col={sx2.Col} span={sx2.Span})");
            Assert(sx2.ClipStart && !sx2.ClipEnd, "承接上周的条:左端标记为续前");

            Assert(Views.CalendarData.TimedOn(monday).Count() == 1, "定时日程只算在起始那天");
            Assert(!Views.CalendarData.TimedOn(monday.AddDays(1)).Any(), "全天条不计入定时日程(否则日期格会重复标点)");
            Assert(Views.CalendarData.On(monday.AddDays(2)).Any(e => e.Title == "跨三天"), "跨天日程覆盖区间内的每一天");
            Assert(Views.CalendarEditor.DefaultDuration == TimeSpan.FromHours(1), "新建日程默认时长 1 小时");
            Views.CalendarData.Events.Clear();

            // ---- 日程数据变更通知(修"开启时日程读不出来")----
            // 成因:示例数据在窗口构建【之后】才播种,日历读到的是空表;任务与项目有变更通知
            // 能补刷,日历没有,于是表现为"必须点一下才出现"。加了 Changed 事件后不再依赖时序。
            Views.CalendarData.Events.Clear();
            var calNotified = 0;
            void OnCal() => calNotified++;
            Views.CalendarData.Changed += OnCal;
            try
            {
                var ev = new Views.CalendarEvent(DateTime.Today.AddHours(9), DateTime.Today.AddHours(10), "x", "我", "家庭");
                Views.CalendarData.Add(ev);
                Assert(calNotified == 1, "写入日程会触发变更通知(界面据此自动刷新,不依赖播种早于建窗口)");
                Views.CalendarData.Remove(ev);
                Assert(calNotified == 2, "删除日程同样触发变更通知");
            }
            finally { Views.CalendarData.Changed -= OnCal; Views.CalendarData.Events.Clear(); }

            // ---- 翻页动画的"两个父级"回归 ----
            // 闪退根因:WPF 元素只能有一个父级。翻页动画把仍挂在容器里的旧页直接加进新容器,
            // 抛 InvalidOperationException -> 进程崩。这里用同构的最小场景把规则钉住。
            var holder = new System.Windows.Controls.ContentControl();
            var page = new System.Windows.Controls.Grid();
            holder.Content = page;
            var animHost = new System.Windows.Controls.Grid();
            var threw = false;
            try { animHost.Children.Add(page); } catch (InvalidOperationException) { threw = true; }
            Assert(threw, "把仍有父级的元素加进另一个容器会抛异常(这正是翻页闪退的机制)");
            holder.Content = null;                       // 正解:先脱离旧父级
            var ok2 = true;
            try { animHost.Children.Add(page); } catch { ok2 = false; }
            Assert(ok2, "先解除旧父级后即可加入新容器(翻页动画的正确顺序)");

            // ---- 浮层协调(抽屉与浮窗共用一套规则)----
            // 重构前抽屉与浮窗各写一套规则、互不知情:Esc 关不掉浮窗、抽屉里的浮窗会变孤儿、
            // "同时只开一个"合起来不成立。现在统一由 Overlay 裁决,这里逐条断言。
            Views.Overlay.CloseActive();
            Assert(!Views.Overlay.IsOpen, "初始没有浮层");

            var closedA = 0; var closedB = 0;
            void CloseA() => closedA++;
            void CloseB() => closedB++;

            Views.Overlay.Register(CloseA);
            Assert(Views.Overlay.IsOpen, "登记后有浮层开着");

            Views.Overlay.Register(CloseB);
            Assert(closedA == 1, "打开新浮层会先关掉旧的(全局同时只有一个)");

            Assert(Views.Overlay.ConsumeClick(), "浮层开着时,入口按钮的这一次点击被消费掉");
            Assert(closedB == 1 && !Views.Overlay.IsOpen, "被消费的点击只负责关闭当前浮层,不打开新的");
            Assert(!Views.Overlay.ConsumeClick(), "没有浮层时不消费点击(按钮正常打开自己的浮层)");

            // 浮层自行关闭(如点了浮窗外面)后要清账,否则会留下失效的关闭回调
            Views.Overlay.Register(CloseA);
            Views.Overlay.Unregister(CloseA);
            Assert(!Views.Overlay.IsOpen, "浮层自行关闭后向协调器清账");
            Assert(!Views.Overlay.ConsumeClick(), "清账后不会再误消费点击");

            // 关闭回调内部若再次触发关闭,不能递归
            var reentrant = 0;
            void SelfClosing() { reentrant++; Views.Overlay.CloseActive(); }
            Views.Overlay.Register(SelfClosing);
            Views.Overlay.CloseActive();
            Assert(reentrant == 1, "关闭回调内部再次触发关闭不会递归(只执行一次)");

            // ---- 三语文案 ----
            var (keys, missing) = Strings.Audit();
            Assert(keys > 40, $"文案表已装载({keys} 个键)");
            Assert(missing.Count == 0, "所有文案键在中/英/日三语齐全" + (missing.Count > 0 ? " 缺:" + string.Join(",", missing.Take(6)) : ""));
            Strings.Language = "en-US";
            Assert(Strings.Get("visibility.only_me") == "Private to me", "「仅本人」英文用 Private to me(禁用 Confidential —— 那是敏感度轴)");
            Strings.Language = "ja-JP";
            Assert(Strings.Get("nav.chat") == "チャット", "可切换到日语");
            Strings.Language = "zh-CN";
            Assert(Strings.Get("__no_such_key__").StartsWith("⟦"), "缺失的文案键会显眼报出(不静默回退)");
            Assert(Strings.Get("member.current_is", ("m", "A")) == "当前识别为 A", "占位符替换正常");

            // 实时切换语言(无需重启):界面靠 LanguageChanged 就地重建
            int langEvents = 0;
            void OnLang() => langEvents++;
            Strings.LanguageChanged += OnLang;
            try
            {
                Strings.Language = "en-US";
                Assert(langEvents == 1, "切换语言会触发 LanguageChanged(界面据此就地重建,不用重启)");
                Strings.Language = "en-US";
                Assert(langEvents == 1, "重复设置同一语言不重复触发(避免无谓重建)");
                Strings.Language = "zh-CN";
                Assert(langEvents == 2 && Strings.Get("nav.chat") == "聊天", "切回中文后取到的就是中文文案");
            }
            finally { Strings.LanguageChanged -= OnLang; }

            // ---- 皮肤令牌齐备:三个皮肤必须定义同一组键,否则换肤会崩在缺键上 ----
            var need = new[] { "BgWindow", "BgSurface", "BgNav", "BgHover", "BgSelected", "FgPrimary",
                               "FgSecondary", "FgMuted", "FgOnAccent", "Accent", "AccentHover", "Border",
                               "BorderStrong", "FocusRing", "BgSunken", "FgOnSelected", "RadiusSm", "RadiusMd", "RadiusLg" };
            // 开发/CI 环境下源码 Theme 目录在旁边,能逐皮肤核对令牌齐全;单文件发布里这些 xaml
            // 已编进程序集资源(磁盘上没有源码目录),此检查跳过 —— 运行时皮肤从 pack 资源正常加载。
            var themeDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Theme");
            if (!Directory.Exists(themeDir))
                Console.WriteLine("  SKIP  皮肤令牌一致性(发布环境:xaml 已作为 pack 资源编入,非磁盘文件)");
            else foreach (var skin in new[] { "Breeze", "Ink", "Warm" })
            {
                var xaml = File.ReadAllText(Path.Combine(themeDir, skin + ".xaml"));
                var miss = need.Where(k => !xaml.Contains("\"" + k + "\"")).ToList();
                Assert(miss.Count == 0, $"皮肤 {skin} 定义了全部令牌" + (miss.Count > 0 ? " 缺:" + string.Join(",", miss) : ""));
            }
        }
        catch (Exception ex) { fail++; Console.WriteLine("  FAIL  自检自身抛异常: " + ex); }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\LocalAI", throwOnMissingSubKey: false); } catch { }
            Autostart.KeyPath = oldKeyPath;
            Environment.SetEnvironmentVariable(AppPaths.StateEnvVar, oldState);
            try { Directory.Delete(tmp, true); } catch { }
        }

        Console.WriteLine($"\nP3c 客户端 selftest: PASS={pass} FAIL={fail}");
        return fail > 0 ? 1 : 0;
    }

    // 模拟被阻塞的 WPF UI 线程:任何 post 进来的续体都不会被执行。若善后代码依赖回到此上下文,
    // 就会永久卡住 —— 测试用它证明我们的退出路径不依赖它。
    sealed class NeverRunsSyncContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref Posts);
        public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref Posts);
    }
}
