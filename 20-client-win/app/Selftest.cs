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

            // ---- 天气地点表:当前地固定首位 + 可拖拽排序 ----
            var wSettings = new AppSettings();
            var places = Places.Load(wSettings);
            Assert(places.Count >= 1, "地点表至少有当前所在地一项");
            Assert(places[0].IsCurrent, "第 0 项是【当前所在地】(固定首位,不可拖动)");
            Assert(places.Skip(1).All(p2 => !p2.IsCurrent), "只有首项标记为当前");
            Assert(places.Select(p2 => p2.City).Distinct().Count() == places.Count,
                   "当前地与默认城市重名时会去重,不出现两格同名");
            var here = Places.Current();
            Assert(!string.IsNullOrWhiteSpace(here.City) && here.IsCurrent,
                   $"当前地由系统时区推断得出(实得 {here.City} / {here.TimeZoneId})");

            // 顺序持久化:只存可拖动部分
            var reordered = places.Skip(1).Reverse().ToList();
            Places.SaveOrder(wSettings, reordered);
            Assert(!string.IsNullOrWhiteSpace(wSettings.WeatherCityOrder), "拖拽后的顺序被写入设置");
            var reloaded = Places.Load(wSettings);
            Assert(reloaded[0].IsCurrent, "重载后当前地仍在首位");
            if (reordered.Count >= 2)
                Assert(reloaded[1].City == reordered[0].City, "重载后可拖动部分保持用户排定的顺序");

            // 时间滚轮:5 分钟粒度、有头有尾
            Assert(Views.WheelPicker.MinuteStep == 5, "时间滚轮最小单位 5 分钟");
            Assert(Views.WheelPicker.Snap(TimeSpan.FromMinutes(7)) == TimeSpan.FromMinutes(5), "7 分就近对齐到 5 分");
            Assert(Views.WheelPicker.Snap(TimeSpan.FromMinutes(8)) == TimeSpan.FromMinutes(10), "8 分就近对齐到 10 分");
            Assert(Views.WheelPicker.Snap(TimeSpan.FromMinutes(-5)) == TimeSpan.Zero, "负值被夹到 00:00(有头)");
            Assert(Views.WheelPicker.Snap(TimeSpan.FromHours(25)) == TimeSpan.FromMinutes(24 * 60 - 5),
                   "超过一天被夹到 23:55(有尾,不循环)");

            // 新建日程默认开始 = 当前时间【向上】取到最近五分钟
            Assert(Views.WheelPicker.CeilToStep(new TimeSpan(9, 32, 0)) == new TimeSpan(9, 35, 0), "9:32 向上取到 9:35");
            Assert(Views.WheelPicker.CeilToStep(new TimeSpan(9, 31, 0)) == new TimeSpan(9, 35, 0), "9:31 也向上取到 9:35(不是就近的 9:30)");
            Assert(Views.WheelPicker.CeilToStep(new TimeSpan(9, 35, 0)) == new TimeSpan(9, 35, 0), "恰在刻度上则保持 9:35");
            Assert(Views.WheelPicker.CeilToStep(new TimeSpan(9, 35, 1)) == new TimeSpan(9, 40, 0), "刚过刻度一秒也进到下一格 9:40");
            Assert(Views.WheelPicker.CeilToStep(new TimeSpan(23, 58, 0)) == new TimeSpan(23, 55, 0), "临近午夜夹到 23:55");

            // ---- 问候语:时段主句 + 小助手副句(同一小时内稳定)----
            Assert(Views.Greetings.TitleFor(3) == "夜深了", "凌晨=夜深了");
            Assert(Views.Greetings.TitleFor(8) == "早上好", "上午=早上好");
            Assert(Views.Greetings.TitleFor(12) == "中午好", "中午=中午好");
            Assert(Views.Greetings.TitleFor(15) == "下午好", "下午=下午好");
            Assert(Views.Greetings.TitleFor(20) == "晚上好", "晚上=晚上好");
            var gA = Views.Greetings.SubFor(new DateTime(2026, 7, 29, 9, 15, 0));
            var gB = Views.Greetings.SubFor(new DateTime(2026, 7, 29, 9, 55, 0));
            Assert(!string.IsNullOrWhiteSpace(gA), "副句非空");
            Assert(gA == gB, "副句同一小时内稳定(不每秒乱跳)");

            // ---- 待办 / 家务:数据模型 + 排序 + 逾期 ----
            var todos = new Services.TodoCenter();
            var todoChanged = 0;
            todos.Changed += () => todoChanged++;
            var tid = todos.Add(new Services.TodoItem("", "买菜", Services.TodoKind.Chore));
            Assert(!string.IsNullOrEmpty(tid), "新增自动生成 Id");
            Assert(todos.Items.Count == 1 && todoChanged == 1, "新增触发 Changed");
            todos.Toggle(tid);
            Assert(todos.Items[0].Done && todoChanged == 2, "Toggle 置为完成并触发 Changed");
            todos.Update(todos.Items[0] with { Title = "买菜和蛋" });
            Assert(todos.Items[0].Title == "买菜和蛋", "Update 生效");
            // 排序:未完成按截止升序(逾期/最近在前),无截止排后
            var t0 = todos.Add(new Services.TodoItem("", "无截止", Services.TodoKind.Personal));
            var t1 = todos.Add(new Services.TodoItem("", "后天到期", Services.TodoKind.Personal, Due: DateTime.Now.AddDays(2)));
            var t2 = todos.Add(new Services.TodoItem("", "已逾期", Services.TodoKind.Personal, Due: DateTime.Now.AddDays(-1)));
            var act = todos.Active().Where(x => !x.Done).ToList();   // 排除上面刚 Toggle 的那条
            var iOver = act.FindIndex(x => x.Id == t2);
            var iSoon = act.FindIndex(x => x.Id == t1);
            var iNone = act.FindIndex(x => x.Id == t0);
            Assert(iOver < iSoon && iSoon < iNone, "排序:逾期 < 将到期 < 无截止(按截止升序,无截止垫底)");

            todos.Remove(tid);
            Assert(todos.Items.All(x => x.Id != tid), "Remove 删除该条");

            // 完成后 3 秒宽限:刚完成仍在 Active,超过宽限期则进 Completed、离开 Active
            var g = todos.Add(new Services.TodoItem("", "刚完成", Services.TodoKind.Personal));
            todos.Toggle(g);
            var justNow = DateTime.Now;
            Assert(todos.Active(justNow).Any(x => x.Id == g), "刚勾选完成的项仍留在待办板块(宽限期内)");
            Assert(todos.HasGrace(justNow), "有项处于宽限期");
            var later = justNow.AddSeconds(Services.TodoCenter.ArchiveGraceSeconds + 1);
            Assert(!todos.Active(later).Any(x => x.Id == g), "宽限期过后离开待办板块");
            Assert(todos.Completed().Any(x => x.Id == g), "完成项进入【已完成】");
            Assert(!todos.HasGrace(later), "宽限期过后不再有宽限项");
            // 取消完成 -> 回到待办、离开已完成
            todos.Toggle(g);
            Assert(todos.Active(later).Any(x => x.Id == g) && !todos.Completed().Any(x => x.Id == g),
                   "取消完成后回到待办、不在已完成里");

            var overdue = new Services.TodoItem("x", "逾期", Services.TodoKind.Personal, Due: DateTime.Now.AddMinutes(-5));
            Assert(overdue.IsOverdue, "有截止、过期、未完成 => 逾期");
            Assert(!(overdue with { Due = DateTime.Now.AddHours(1) }).IsOverdue, "未来截止不算逾期");
            Assert(!(overdue with { Done = true }).IsOverdue, "已完成不算逾期");

            // ---- 项目置顶:置顶在前 + 切换 ----
            var projCenter = new Services.ProjectCenter();
            projCenter.Add(new Services.Project("a", "较新", "", "chat", Services.ProjectScope.Family, DateTime.Now));
            projCenter.Add(new Services.Project("b", "较旧", "", "chat", Services.ProjectScope.Family, DateTime.Now.AddHours(-3)));
            Assert(projCenter.Recent().First().ProjectId == "a", "未置顶时:最近的在前");
            projCenter.TogglePin("b");
            Assert(projCenter.Recent().First().ProjectId == "b", "置顶后:置顶项排到最前(盖过更近的)");
            Assert(projCenter.Items.First(x => x.ProjectId == "b").Pinned, "TogglePin 置为已置顶");
            projCenter.TogglePin("b");
            Assert(projCenter.Recent().First().ProjectId == "a", "取消置顶后回到按最近排序");

            // ---- 工作空间显示开关 + 财务管理 + 系统贴底 + 设备并入设置 ----
            Assert(Views.Workspaces.All.Any(w => w.Key == "finance"), "有「财务管理」工作空间");
            Assert(Views.Workspaces.All.Any(w => w.Key == "investment"), "有「投资」工作空间(之前误删已补回)");
            Assert(Views.Workspaces.All.Single(w => w.Key == "investment").Icon == Theme.IconName.Investment, "投资用走势图图标");
            Assert(Views.Workspaces.All.Single(w => w.Key == "finance").Icon == Theme.IconName.Finance, "财务管理用钱包图标(与投资区分)");
            var iconsSrc = TryReadSource(Path.Combine("Theme", "Icons.cs"));
            if (iconsSrc is not null)
            {
                var n = iconsSrc.Split("[IconName.Finance]").Length - 1;
                Assert(n >= 3, $"Finance 图标三种皮肤都补齐了(实得 {n} 处,需 3)");
            }
            Assert(Strings.Get("nav.finance") == "财务管理", "财务管理文案就位");
            var st = new AppSettings();
            Assert(st.IsWorkspaceVisible("finance"), "工作空间默认显示");
            st.HiddenWorkspaces.Add("finance");
            Assert(!st.IsWorkspaceVisible("finance"), "加入隐藏列表后不在左栏显示");
            st.HiddenWorkspaces.Remove("finance");
            Assert(st.IsWorkspaceVisible("finance"), "移出隐藏列表后恢复显示");
            Assert(Views.Layout.PreferredWindowHeight >= 980, "默认窗口更高,一开始不出滚动条");

            // ---- 左下角状态块:本机是否主机 + token 用量(去掉原"当前成员"块)----
            Assert(!Services.TokenUsage.Connected, "token 用量统计尚未接入(如实,不编数字)");
            Assert(Services.TokenUsage.Today is null && Services.TokenUsage.Week is null
                   && Services.TokenUsage.Month is null && Services.TokenUsage.Total is null,
                   "未接入时四个用量值都为 null(界面显示 —)");
            var mwStatus = TryReadSource("MainWindow.xaml.cs");
            if (mwStatus is not null)
            {
                Assert(mwStatus.Contains("ThisMachineIsHub"), "状态块显示本机是否为主机");
                Assert(mwStatus.Contains("OnOpenUsage") && mwStatus.Contains("usage.title"), "点状态块弹出 token 用量表");
                Assert(!mwStatus.Contains("MemberText.Text"), "移除原左下角登录成员块");
            }

            // token 用量按钮:用 Button(Click)而非 Border(MouseLeftButtonUp),否则会"关一下又弹开"
            var mwXaml = TryReadSource("MainWindow.xaml");
            if (mwXaml is not null)
                Assert(mwXaml.Contains("x:Name=\"StatusBlock\"") && mwXaml.Contains("Click=\"OnOpenUsage\""),
                       "token 用量块是 Button(Click),不会关了又开");
            if (mwStatus is not null)
                Assert(mwStatus.Contains("OnOpenUsage(object sender, RoutedEventArgs"), "OnOpenUsage 用 Click 签名");

            // 系统栏「模型」:存放路径 + 启用模型 + 自动规则(偏好,未接 Broker)
            Assert(Strings.Get("nav.model") == "模型", "系统组新增「模型」栏");
            if (mwStatus is not null)
                Assert(mwStatus.Contains("new NavItem(\"model\""), "模型是系统组导航项");
            var mdlView = TryReadSource(Path.Combine("Views", "ModelsView.cs"));
            if (mdlView is not null)
            {
                Assert(mdlView.Contains("ModelStorePath"), "模型页可设统一存放路径");
                Assert(mdlView.Contains("IsModelEnabled") && mdlView.Contains("AutoStartPreset"), "模型页可选启用模型 + 自动启用规则");
                Assert(mdlView.Contains("model.not_connected"), "模型页顶部诚实标注未接 Broker(不假装加载)");
            }
            var msSet = new AppSettings();
            Assert(msSet.IsModelEnabled("chat.8b"), "模型默认启用");
            msSet.DisabledModels.Add("chat.8b");
            Assert(!msSet.IsModelEnabled("chat.8b"), "停用列表里的模型不启用");

            // 扩展拖动把手:用透明命中块,不是拿描边 Path 当命中区
            var extGrip = TryReadSource(Path.Combine("Views", "ExtensionsView.cs"));
            if (extGrip is not null)
                Assert(extGrip.Contains("gripPath") && extGrip.Contains("IsHitTestVisible = false"),
                       "拖动把手用整块透明命中区(描边 Path 不接管命中)");

            var mwSrc2 = TryReadSource("MainWindow.xaml.cs");
            if (mwSrc2 is not null)
            {
                Assert(mwSrc2.Contains("foreach (var w in Workspaces.Ordered"), "导航按统一清单、用户排定顺序渲染工作空间");
                Assert(mwSrc2.Contains("IsWorkspaceVisible(w.Key)"), "被关掉的工作空间不进导航");
                Assert(mwSrc2.Contains("NavSystemPanel"), "系统组放在贴底的独立面板");
                Assert(mwSrc2.Contains("public void RefreshNavRail"), "扩展改动后能只刷新导航栏");
                Assert(!mwSrc2.Contains("ShouldShowInvestment"), "移除旧的投资隐藏策略(改由用户勾选)");
                Assert(!mwSrc2.Contains("new NavItem(\"devices\""), "设备不再单列(已并入设置)");
            }
            var extSrc = TryReadSource(Path.Combine("Views", "ExtensionsView.cs"));
            if (extSrc is not null)
            {
                Assert(extSrc.Contains("SetWorkspaceVisible") && extSrc.Contains("RefreshNavRail"), "扩展页勾选即时改左栏");
                Assert(extSrc.Contains("ext.ws_title") && extSrc.Contains("ext.panels_title"), "扩展分两类:工作空间扩展 + 主页板块扩展");
                Assert(extSrc.Contains("SetPanelVisible"), "主页板块扩展勾选写入面板显隐");
                Assert(extSrc.Contains("ext.ws_model_note"), "工作空间扩展注明将决定 AI 模型选择(接入后)");
                Assert(extSrc.Contains("MoveWorkspace") && extSrc.Contains("SetWorkspaceOrder"), "工作空间可拖动排序并落盘");
            }
            // 拖动排序:自定义顺序 -> Ordered 反映;新增/未列项追加不丢
            var os = new AppSettings();
            os.WorkspaceOrder = new System.Collections.Generic.List<string> { "finance", "chat" };
            var ord2 = Views.Workspaces.Ordered(os);
            Assert(ord2[0].Key == "finance" && ord2[1].Key == "chat", "已存顺序在最前");
            Assert(ord2.Count == Views.Workspaces.All.Length, "未列入顺序的工作空间也全部追加(不丢)");
            {
            }

            // 主页板块显隐:默认全显示;隐藏后 HomeView 会跳过该板块
            Assert(Views.HomePanels.All.Length == 4, "主页有 4 个可控板块(日历/待办/天气/项目)");
            var ps = new AppSettings();
            Assert(ps.IsPanelVisible("weather"), "板块默认显示");
            ps.HiddenPanels.Add("weather");
            Assert(!ps.IsPanelVisible("weather"), "加入隐藏列表后板块不显示");
            var homeVis = TryReadSource(Path.Combine("Views", "HomeView.cs"));
            if (homeVis is not null)
            {
                Assert(homeVis.Contains("IsPanelVisible(\"weather\")"), "HomeView 读取板块显隐");
                Assert(homeVis.Contains("_weatherVisible") && homeVis.Contains("_projectsVisible"), "各板块按显隐条件构建");
            }
            var setSrc = TryReadSource(Path.Combine("Views", "SettingsView.cs"));
            if (setSrc is not null)
                Assert(setSrc.Contains("new DevicesView(embedded: true)"), "已配对的电脑并入设置页");

            // 接线:板块有 + 按钮、用共享行渲染器、变更自动刷新;编辑器当场写库并收起抽屉
            var homeTodo = TryReadSource(Path.Combine("Views", "HomeView.cs"));
            if (homeTodo is not null)
            {
                Assert(homeTodo.Contains("Ui.PlusButton"), "待办板块标题栏有 + 新增按钮");
                Assert(homeTodo.Contains("TodoList.Row("), "待办列表用共享行渲染器(与诊断同一份布局)");
                Assert(homeTodo.Contains("Todos.Changed += BuildTodos"), "待办变更自动刷新列表(不依赖播种时序)");
                Assert(homeTodo.Contains("Ui.Panel(\"待办事项\"") , "板块改名为「待办事项」");
                Assert(homeTodo.Contains("OpenTodoArchive"), "右下角有「已完成」入口打开抽屉");
                Assert(homeTodo.Contains("Todos.Active()"), "主板块只显示进行中(含宽限期)项");
                Assert(homeTodo.Contains("_todoGrace"), "有 3 秒宽限轮询把已完成项刷走");

                Assert(homeTodo.Contains("Greetings.SubFor"), "问候块显示小助手副句");
                Assert(homeTodo.Contains("contentW / 3.0"), "问候块占约 1/3 宽");
                Assert(homeTodo.Contains("FontSize = 30"), "问候主句字号更大");

                Assert(homeTodo.Contains("PinButton(") && homeTodo.Contains("Projects.TogglePin"), "项目方块有置顶按钮");
                Assert(homeTodo.Contains("p.Pinned ? 2 : 1"), "置顶方块描边更粗");
                Assert(homeTodo.Contains("Opacity = 0") && homeTodo.Contains("pinBtn.Opacity = 1"), "置顶按钮平时隐藏、hover 才显示");
                Assert(homeTodo.Contains("AnimateTodoOut") && homeTodo.Contains("IsHitTestVisible = false"), "完成项向右划出、动画期间不可交互");
                Assert(homeTodo.Contains("_todoColumn.Width = new GridLength(Math.Max(150, cardOuter))"), "待办列宽=一个天气卡宽(与天气对齐)");
                var uiSrc = TryReadSource(Path.Combine("Views", "Ui.cs"));
                if (uiSrc is not null)
                    Assert(uiSrc.Contains("用两条【居中的矩形】拼"), "+ 号用居中矩形绘制(不再偏移)");

                // 天气拖拽只能从右下角手柄起手,不是整块板块(用户裁定)
                Assert(homeTodo.Contains("gripZone.PreviewMouseLeftButtonDown"), "天气拖拽从右下角手柄区起手");
                Assert(!homeTodo.Contains("card.PreviewMouseLeftButtonDown"), "整块卡片不再作为拖拽起手区");
            }
            var todoEd = TryReadSource(Path.Combine("Views", "TodoEditor.cs"));
            if (todoEd is not null)
            {
                Assert(todoEd.Contains("TheApp.Todos.Add") && todoEd.Contains("TheApp.Todos.Update"), "编辑器当场写入 TodoCenter(非伪造)");
                Assert(todoEd.Contains("Overlay.CloseActive()"), "保存/删除后收起右侧抽屉");
            }
            var appTodo = TryReadSource("App.xaml.cs");
            if (appTodo is not null)
                Assert(appTodo.Contains("SeedDemoTodos"), "示例待办在建窗口【之前】播种");

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
            // 首屏建议尺寸:必须不小于最小窗口,且在常见屏幕上放得下(否则打开就出滚动条)
            Assert(Views.Layout.PreferredWindowHeight >= 872,
                   $"建议高度够放下主页内容(标题栏+页边距+四行 ≈ 872,实得 {Views.Layout.PreferredWindowHeight})");
            var min2Kw = Views.Layout.MinWindowFor(2560, 1440);
            Assert(Views.Layout.PreferredWindowHeight >= min2Kw.H && Views.Layout.PreferredWindowWidth >= min2Kw.W,
                   "建议尺寸不小于 2K 屏的最小窗口(否则会被夹回去,建议值形同虚设)");

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

            // 单日全天:两端都是【真端点】,所以左右都应内缩(用户裁定的边界感)
            Views.CalendarData.Events.Clear();
            var oneDay = monday.AddDays(2);
            Views.CalendarData.Events.Add(new Views.CalendarEvent(oneDay, oneDay, "单日全天", "我", "家庭", AllDay: true));
            var one = Views.CalendarData.SpansIn(monday, 7);
            Assert(one.Count == 1 && one[0].Col == 2 && one[0].Span == 1, $"单日全天占 1 格(实得 col={one[0].Col} span={one[0].Span})");
            Assert(!one[0].ClipStart && !one[0].ClipEnd, "单日全天两端都不被裁断 -> 左右都内缩,看得出起止就在这一天");

            // 月末单日(如 31 日)落在周内任意位置都一样成立
            var endOfMonth = new DateTime(2026, 7, 31);
            Views.CalendarData.Events.Clear();
            Views.CalendarData.Events.Add(new Views.CalendarEvent(endOfMonth, endOfMonth, "31日全天", "我", "家庭", AllDay: true));
            var eom = Views.CalendarData.SpansIn(Views.CalendarViewTestHooks.StartOfWeek(endOfMonth), 7);
            Assert(eom.Count == 1 && eom[0].Span == 1 && !eom[0].ClipStart && !eom[0].ClipEnd,
                   "月末单日全天(31 日)同样两端内缩");
            Views.CalendarData.Events.Clear();
            Views.CalendarData.Events.Clear();

            // ---- 接线自检:防"补丁静默失配导致整段成死代码" ----
            // 已经踩过三次:替换字符串没匹配上,函数还在但调用点被删,编译通过、断言全绿、功能没了。
            // 这里直接对源码做结构断言 —— 我改动的正是这些接线点。
            var appSrc = TryReadSource("App.xaml.cs");
            var calSrc = TryReadSource(Path.Combine("Views", "CalendarView.cs"));
            if (appSrc is null || calSrc is null)
                Console.WriteLine("  SKIP  接线自检(发布环境无源码,开发/CI 下才跑)");
            else
            {
                Assert(appSrc.Contains("SeedDemoTasks();"), "示例数据的播种函数【真的被调用】(曾出现整段成死代码)");
                var seedIdx = appSrc.IndexOf("SeedDemoTasks();", StringComparison.Ordinal);
                var winIdx = appSrc.IndexOf("_main = new MainWindow();", StringComparison.Ordinal);
                Assert(seedIdx >= 0 && winIdx >= 0 && seedIdx < winIdx,
                       "播种发生在建窗口【之前】(否则界面读到空表 = 开启时日程读不出来)");
                Assert(calSrc.Contains("OpenSideDrawer"), "日程编辑走【右侧抽屉】而不是浮窗(曾被后续重写覆盖回去)");

                // 抽屉与输入控件要走主题,不能露出系统外观(用户反馈"太过于系统")
                var ctlSrc = TryReadSource(Path.Combine("Theme", "Controls.xaml"));
                if (ctlSrc is not null)
                {
                    foreach (var ctl in new[] { "TextBox", "ComboBox", "CheckBox", "ComboBoxItem" })
                        Assert(ctlSrc.Contains($"TargetType=\"{ctl}\""), $"{ctl} 有主题化样式(不用系统外观)");
                    Assert(ctlSrc.Contains("{DynamicResource RadiusSm}"), "输入控件圆角走皮肤令牌(随皮肤变)");
                }
                var mwSrc = TryReadSource("MainWindow.xaml.cs");
                if (mwSrc is not null)
                {
                    Assert(mwSrc.Contains("SideDrawerIcon.Content = Icons.Make"), "侧边抽屉标题带图标");
                    Assert(mwSrc.Contains("SideDrawer.CornerRadius = new CornerRadius"), "侧边抽屉圆角跟随皮肤令牌");
                }
                Assert(calSrc.Contains("CalendarData.Changed += Rebuild"), "日历订阅了数据变更通知");

                // 日期区高度必须【恒定】—— 行数随日程条数变化会让下方日程表位置上下跳(用户反馈)。
                // 判据:行定义全部是绝对高度,且预留行数是常量;不允许在行结构里用 Auto。
                var bandStart = calSrc.IndexOf("UIElement Band(", StringComparison.Ordinal);
                var bandEnd = calSrc.IndexOf("static Border SpanBar(", StringComparison.Ordinal);
                var bandSrc = bandStart >= 0 && bandEnd > bandStart ? calSrc[bandStart..bandEnd] : "";
                Assert(bandSrc.Length > 0, "找到横带构建代码");
                Assert(!bandSrc.Contains("Height = GridLength.Auto"),
                       "横带的行高不含 Auto(Auto 会随内容有无而变 = 日期区高度浮动)");
                Assert(bandSrc.Contains("SpanRowsReserved"), "全天线占【固定预留行数】,与实际条数无关");
                Assert(calSrc.Contains("const int SpanRowsReserved"), "预留行数是编译期常量");

                // 天气三卡必须等宽:所有卡用同一个边距常量,末尾由容器负边距吸收
                var homeSrc = TryReadSource(Path.Combine("Views", "HomeView.cs"));
                if (homeSrc is not null)
                {
                    Assert(homeSrc.Contains("const double WeatherGap"), "天气卡间距是统一常量");
                    Assert(!homeSrc.Contains("i < _places.Count - 1 ? 12"),
                           "不再按'是否末格'给不同边距(那会让末格宽出一截)");
                    Assert(homeSrc.Contains("new Thickness(0, 0, -WeatherGap, 12)"),
                           "容器用负右边距吸收末格多出的间距,整排右缘仍对齐");
                }

                // 编辑器:时间/日期转盘必须【互斥显示】,且滚动要有动画
                var edSrc = TryReadSource(Path.Combine("Views", "CalendarEditor.cs"));
                if (edSrc is not null)
                {
                    Assert(edSrc.Contains("void SyncMode()"), "全天开关切换两组转盘的互斥显示(曾被重写连带删掉)");
                    Assert(edSrc.Contains("timedRow.Visibility") && edSrc.Contains("allDayRow.Visibility"),
                           "未勾选只显示时间转盘;勾选后只显示日期转盘");
                    Assert(edSrc.Contains("allDay.Checked") && edSrc.Contains("allDay.Unchecked"),
                           "勾选与取消勾选都会触发切换");
                }
                var whSrc = TryReadSource(Path.Combine("Views", "WheelPicker.cs"));
                if (whSrc is not null)
                {
                    Assert(whSrc.Contains("BeginAnimation(TranslateTransform.YProperty"),
                           "转盘滚动用位移动画(不是瞬间跳到位)");
                    // 只看【实际调用】,不看注释 —— 注释里还留着"为什么不用它"的说明
                    Assert(!whSrc.Contains(".ScrollIntoView("), "不再调用 ListBox.ScrollIntoView(那是硬切)");
                    Assert(whSrc.Contains("Math.Clamp(next, 0"), "转盘有头有尾,越界停在边界");
                    // 大索引列曾整列空白:滑动列被定高格子加了布局裁剪,位移前只剩头三行。
                    // 修法是放进 Canvas(不加布局裁剪)。钉住这个结构,别再退回直接塞 Grid。
                    Assert(whSrc.Contains("new Canvas()"), "滑动列放进 Canvas(否则大索引列会被布局裁剪成空白)");
                    Assert(whSrc.Contains("IsInsideWheel"), "转盘暴露 IsInsideWheel 供外层滚动区让路");
                }
                var whlSrc = TryReadSource(Path.Combine("Views", "Wheel.cs"));
                if (whlSrc is not null)
                    Assert(whlSrc.Contains("IsInsideWheel(e.OriginalSource"),
                           "PassThrough 发现滚轮落在转盘里就让路(否则抽屉不滚时会吞掉滚轮)");
                Assert(calSrc.Contains("const int SpanRowsReserved = 1"), "全天线只占【一行】——多条不分行,不会一上一下");
                Assert(calSrc.Contains("MergeSpans("), "多条全天日程会合并成同一行的连续线段");
                Assert(calSrc.Contains("const int DotsMaxBeforeTriangle = 4"), "定时日程超过 4 条改用实心三角形(阈值是常量)");
                Assert(calSrc.Contains("clipStart ? 0 : EndInset"), "全天线在真正的起始日内缩,被周界裁断的一端贯通");
                Assert(calSrc.Contains("clipEnd ? 0 : EndInset"), "全天线在真正的结束日内缩");
                Assert(calSrc.Contains("ev.AllDay") && calSrc.Contains("\"全天\""),
                       "全天日程在列表里显示「全天」而不是 00:00");
                Assert(!calSrc.Contains("Text = ev.Start.ToString(\"HH:mm\")"),
                       "不再无条件用起始时刻渲染(全天没有起始时刻)");
            }

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

    /// <summary>
    /// 读源码文件(开发/CI 环境)。发布环境没有源码 -> 返回 null,调用方跳过接线自检。
    /// 用途:对"接线点"做结构断言 —— 有些缺陷是"函数还在、调用点没了",编译与行为断言都抓不到。
    /// </summary>
    static string? TryReadSource(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var p = Path.Combine(dir, relative);
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
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
