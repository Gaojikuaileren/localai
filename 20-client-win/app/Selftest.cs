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
    // 与 ClientStore 相同的序列化口径(枚举存名字),用于存档往返测试
    static readonly System.Text.Json.JsonSerializerOptions StoreJson = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

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

        // ★★ 建一个【App】而不是裸 Application —— 界面里到处是 (App)Application.Current
        //   取各个中心,裸 Application 会当场 InvalidCastException。
        //   只构造、不跑 OnStartup:各个中心是字段初始化器建的,拿到的就是一份干净空数据。
        //   (SingleInstance.Acquire 在已有实例时不阻塞,直接返回一个非拥有者。)
        if (System.Windows.Application.Current is null)
            _ = new App(SingleInstance.Acquire(), startHidden: true);

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

            // ★★ 2026-08-04 用户实测:「关闭的时候会卡一段时间」。
            //   根因不是总预算不够,而是**没有单步上限**:步骤顺序跑,② end-session+release-vram
            //   要发一次真实网络请求(Transport.Send 每次现建 HttpClient 走完整 mTLS 握手,
            //   **默认 100 秒超时**),中枢/网关没起时它就干等,把 5 秒总预算吃光 ——
            //   于是 ③ 保存设置 · ④ 停显存监视 · ⑤ 收托盘图标 **一个都轮不到**。
            //   用户看到的不只是卡,还有「设置没保存上、托盘图标还赖着」。
            //   ⇒ 这条钉的是:**总预算充裕时,一个慢步骤只能拖垮它自己,后面的步骤照跑。**
            var co2b = new ShutdownCoordinator
            {
                Budget = TimeSpan.FromSeconds(5),               // 总预算充裕
                PerStepBudget = TimeSpan.FromMilliseconds(200), // 单步很短
            };
            var laterRan = false;
            co2b.Register("slow-network", async ct => await Task.Delay(TimeSpan.FromSeconds(30), ct));
            co2b.Register("save-settings", () => laterRan = true);
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            co2b.RunOnceAsync("per-step-budget").GetAwaiter().GetResult();
            sw2.Stop();
            Assert(laterRan,
                   "★★ 慢步骤【不】饿死后面的步骤 —— 单步有自己的上限。"
                   + "没有这一道:退出时一个网络调用卡住,保存设置与收托盘图标就再也跑不到"
                   + "(用户实测「关闭卡一段时间」的根因)");
            Assert(sw2.ElapsedMilliseconds < 2000,
                   $"★ 单步上限到点就放弃那一步,不等它自己的 100 秒超时(用时 {sw2.ElapsedMilliseconds}ms)");
            Assert(new ShutdownCoordinator().PerStepBudget < new ShutdownCoordinator().Budget,
                   "★ 单步上限必须严格小于总预算,否则它形同虚设");

            // ★★ 2026-08-04:退出善后必须留痕。此前它【一个字都不写】——
            //   唯一的调用点 App.RunCleanup 调 RunOnceAsync(reason) 时没传 log 回调。
            //   代价当场吃到:用户报「关闭卡一段时间」,修完想核实好没好,却没有任何可测的东西;
            //   落盘文件的 mtime 还不能当判据(SaveStores 同时挂在会话中的防抖定时器上,
            //   分不出"退出时存的"和"用着用着自动存的")。
            //   ⇒ 与自配对那条同一句纪律:静默的自动流程必须留痕,没有日志就是查不了的黑箱。
            var coTraceLog = Path.Combine(tmp, "shutdown-trace.log");
            var coTrace = new ShutdownCoordinator
            {
                LogPath = coTraceLog,
                Budget = TimeSpan.FromSeconds(5),
                PerStepBudget = TimeSpan.FromMilliseconds(150),
            };
            coTrace.Register("fast-step", () => { });
            coTrace.Register("slow-step", async ct => await Task.Delay(TimeSpan.FromSeconds(30), ct));
            coTrace.Register("after-slow", () => { });
            coTrace.RunOnceAsync("trace-test").GetAwaiter().GetResult();
            var coTraceTxt = File.Exists(coTraceLog) ? File.ReadAllText(coTraceLog) : "";
            Assert(coTraceTxt.Contains("fast-step") && coTraceTxt.Contains("slow-step") && coTraceTxt.Contains("after-slow"),
                   "★★ 善后留痕:每个步骤都进日志 —— 不传 log 回调也要写(唯一调用点当初就忘了传,"
                   + "整条退出路径因此静默了几个月,修完根本无从核实)");
            Assert(coTraceTxt.Contains("TIMEOUT") && coTraceTxt.Contains("slow-step"),
                   "★★ 被掐断的步骤要**指名道姓**记下来 —— 否则只知道慢,不知道慢在哪一步");
            Assert(System.Text.RegularExpressions.Regex.IsMatch(coTraceTxt, @"done in \d+ms"),
                   "★ 总耗时要落在日志里 —— 「关闭卡不卡」得能拿数字说话,不靠人感觉");
            Assert(new ShutdownCoordinator().LogPath.Length > 0,
                   "★ 默认就有落点:留痕不能依赖调用方记得配置它(这正是当初漏掉的形状)");

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
                // 用户裁定(2026-07-30):token 块左边加【当前使用者】显示栏,未连接中枢时显示"未连接中枢"
                Assert(mwStatus.Contains("MemberText.Text") && mwStatus.Contains("IdentityGuess.Current"),
                       "token 块左边显示【推测的当前使用者】");
                // ★ "未连接中枢"只该出现在 token 块那一侧(role 串),左边身份格不该重复写一遍
                // 只看【代码】,不看注释 —— 注释里正解释"为什么不再写它"(同 CachedMemberDisplayName 那次)
                var mwCode = string.Join("\n", mwStatus.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
                Assert(!mwCode.Contains("未连接中枢"), "★ 左边身份格不再重复显示【未连接中枢】(连接状态只在 token 块)");
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
                Assert(mdlView.Contains("new ComponentPicker()"),
                       "模型页的启用清单来自 ComponentPicker(中枢下发)");
                // ★★★ 2026-08-06(D90 未决项④的处置):`AutoStartPreset` 已作废撤掉 ——
                //   「连上中枢就自动装预设」与 D87 裁定①「不做开机预热」正面矛盾,
                //   而 D90 放行按需装载的全部依据就是 D87。
                //   ⇒ 这条断言从"它必须在"翻成"它必须不在"。**这是一次语义变更**,
                //     应当在 diff 里看得见,而不是把断言删掉了事。
                Assert(!CodeOnly(mdlView).Contains("AutoStartPreset"),
                       "★★★ AutoStartPreset 已从模型页撤掉(与 D87 裁定①「不做开机预热」矛盾)");
                Assert(mdlView.Contains("model.idle_unload"),
                       "★ 而「空闲自动卸载」那个复选框**留着**且仍置灰 —— 理由不同:"
                       + "它是中枢的策略(计时器主副机共享,D87⑧),做成每台各自的开关正是那条裁定要防的事");
                Assert(mdlView.Contains("model.not_connected"), "模型页顶部诚实标注未接 Broker(不假装加载)");
                // ★★ P4-S9 反向断言:那套【自造词汇】必须**不在了**。
                //   原来这里遍历 ModelCatalog.All(chat.8b / speech / image),跟网关别名与
                //   显存组件 id 一个都对不上 —— 勾了什么都不会发生,而界面看着像配好了。
                var mdlCode = CodeOnly(mdlView);
                Assert(!mdlCode.Contains("ModelCatalog.All") && !mdlCode.Contains("ModelToggle"),
                       "★ 模型页不再有自造清单(ModelCatalog.All / ModelToggle 已删)");
            }
            var mcat = TryReadSource(Path.Combine("Views", "ModelCatalog.cs"));
            if (mcat is not null)
            {
                // ★ 这里必须去注释再判:本文件的头部注释正是在**解释**那套词汇为什么被删,
                //   照原样 Contains 会撞在那段说明上(当天第五次踩同一个坑,故装了 CodeOnly)。
                var mcatCode = CodeOnly(mcat);
                Assert(!mcatCode.Contains("Def[] All") && !mcatCode.Contains("record Def")
                       && !mcatCode.Contains("chat.8b"),
                       "★★ 第三套词汇(chat.8b/chat.30b/speech/vlm/image)已从清单里删干净,不是注释掉留着");
                Assert(mcat.Contains("Presets"), "自动启用预设保留 —— 那四个 key 与 toml 的 [presets.*] 逐字对应");
            }
            var msSet = new AppSettings();

            // ---- 聊天 + 项目 ----
            var pcx = new Services.ProjectCenter();
            var nope = Path.Combine(Path.GetTempPath(), "localai-nope-" + Guid.NewGuid().ToString("N")[..6]);
            var np = pcx.Create("测试项目", nope, null, Services.ProjectScope.Personal);
            Assert(np.Status == Services.ProjectStatus.Preparing, "新建项目默认【准备中】");
            Assert(np.Ai == Services.AiPermission.Ask, "新建项目 AI 权限默认【需批准】");
            pcx.SetStatus(np.ProjectId, Services.ProjectStatus.Active);
            Assert(pcx.Ongoing().Any(x => x.ProjectId == np.ProjectId), "进行中的项目出现在 Ongoing()");
            pcx.SetStatus(np.ProjectId, Services.ProjectStatus.Done);
            // ★ 完成宽限【只给项目抽屉】(它有巡检表能播划走动画);默认不含 —— 否则主页只在 Changed 时重建,
            //   宽限过后没有事件,已完成的项目会一直赖在主页上不走(审查实测到的真 bug)。
            Assert(!pcx.Ongoing().Any(x => x.ProjectId == np.ProjectId), "★ 默认不含刚完成的(主页/菜单不会滞留已完成项目)");
            Assert(pcx.Ongoing(includeJustCompleted: true).Any(x => x.ProjectId == np.ProjectId), "抽屉显式要宽限时才含(给划走动画用)");
            Assert(pcx.HasCompletionGrace(), "存在处于完成宽限期的项目(抽屉据此开表)");
            var afterGrace = DateTime.Now.AddSeconds(Services.ProjectCenter.CompletionGraceSeconds + 1);
            Assert(!pcx.Ongoing(null, afterGrace, includeJustCompleted: true).Any(x => x.ProjectId == np.ProjectId), "宽限过后连抽屉也不再显示");
            Assert(pcx.Completed().Any(x => x.ProjectId == np.ProjectId), "已完成的进 Completed()(项目库)");
            pcx.SetAiPermission(np.ProjectId, Services.AiPermission.Edit);
            Assert(pcx.Find(np.ProjectId)!.Ai == Services.AiPermission.Edit, "可设项目 AI 权限");
            Assert(!Services.ProjectCenter.OpenInExplorer(nope), "路径不存在时打开 Explorer 返回 false(不抛)");

            var cc = new Services.ChatCenter();
            var ns = cc.NewSession(null, "chat", Services.ProjectScope.Personal);
            Assert(cc.NormalSessions("chat").Any(x => x.SessionId == ns.SessionId), "无项目 = 普通会话");
            var pj = cc.NewSession("prj-x", "chat", Services.ProjectScope.Family);
            Assert(cc.SessionsOf("prj-x").Any(x => x.SessionId == pj.SessionId), "项目会话归到该项目下");
            cc.MoveToProject(ns.SessionId, "prj-x");
            Assert(cc.SessionsOf("prj-x").Any(x => x.SessionId == ns.SessionId) && !cc.NormalSessions("chat").Any(x => x.SessionId == ns.SessionId),
                   "会话可移动到项目(离开普通列表)");
            cc.Send(pj.SessionId, "你好");
            var msgs = cc.MessagesOf(pj.SessionId).ToList();
            Assert(msgs.Any(m => m.Role == Services.ChatRole.User && m.Text == "你好"), "发送记录用户消息");
            Assert(msgs.Any(m => m.Role == Services.ChatRole.System), "给出系统说明(模型未接入)");
            Assert(!msgs.Any(m => m.Role == Services.ChatRole.Assistant), "★ 不伪造 AI 回复(无 Assistant 消息)");

            // 跨工作空间:会话不共享,可发送到别的空间
            var wsSes = cc.NewSession(null, "chat");
            cc.MoveSessionToWorkspace(wsSes.SessionId, "translation");
            Assert(!cc.NormalSessions("chat").Any(x => x.SessionId == wsSes.SessionId), "发送后离开原工作空间");
            Assert(cc.NormalSessions("translation").Any(x => x.SessionId == wsSes.SessionId), "出现在目标工作空间");

            // 幽灵会话:不进任何列表
            var ghost = cc.NewGhostSession("chat");
            Assert(!cc.NormalSessions("chat").Any(x => x.SessionId == ghost.SessionId), "幽灵会话不进普通列表");
            // ★★ 全功能幽灵会话(用户裁定 2026-08-03):幽灵得跟当前场景【同型】——
            //   以前 NewGhostSession 只吃 workspaceKey,建出来永远是一条普通文字会话,
            //   于是在同传/文件翻译/多语表/回信里按幽灵,要么被踢回文字翻译,要么按钮亮着但什么也没发生。
            // ★ 判据【逐个对号】,不能写成"四个标记里有任意一个"(复核 2026-08-03):
            //   那样把 fileTrans 接到 Interpret 上也照样全绿 —— 而参数接错正是这次新引入、最容易错的一段。
            foreach (var (mk, name, pick) in new (Func<Services.ChatSession>, string, Func<Services.ChatSession, bool>)[]
                     {
                         (() => cc.NewGhostSession("translation", interpret: true), "同传",
                          g => g.Interpret && !g.FileTrans && !g.I18nTable && !g.ReplyLetter),
                         (() => cc.NewGhostSession("translation", fileTrans: true), "文件翻译",
                          g => g.FileTrans && !g.Interpret && !g.I18nTable && !g.ReplyLetter),
                         (() => cc.NewGhostSession("translation", i18nTable: true), "多语表",
                          g => g.I18nTable && !g.Interpret && !g.FileTrans && !g.ReplyLetter),
                         (() => cc.NewGhostSession("chat", replyLetter: true), "回信",
                          g => g.ReplyLetter && !g.Interpret && !g.FileTrans && !g.I18nTable),
                     })
            {
                var g2 = mk();
                Assert(g2.Ghost && pick(g2), $"★ 幽灵会话建成的【正好是{name}】那一型(参数接错会当场红)");
                Assert(!cc.NormalSessions(g2.WorkspaceKey!).Any(x => x.SessionId == g2.SessionId),
                       $"{name}幽灵照旧不进列表");
            }
            // ★ 主角要在【这次】显式清除里才消失:上面那个循环里每次 NewGhostSession 都会先清一遍,
            //   ghost 早就不在了 —— 拿它验"显式 PurgeGhosts 有效"是验了个空(复核抓到)。这里另起一只。
            var ghost2 = cc.NewGhostSession("chat");
            cc.PurgeGhosts();
            Assert(cc.Find(ghost2.SessionId) is null, "★ 显式 PurgeGhosts 真的抹掉刚建的那只幽灵");
            Assert(cc.Find(ghost.SessionId) is null, "PurgeGhosts 抹掉幽灵会话");

            // 接线
            if (mwStatus is not null)
            {
                Assert(mwStatus.Contains("new ChatView(def.Key)"), "所有工作空间共用会话/项目外壳(ChatView)");
                Assert(mwStatus.Contains("OpenProjectEditor") && mwStatus.Contains("OpenProjectLibrary") && mwStatus.Contains("OpenProjectInChat"), "项目编辑/项目库/进项目聊天入口就位");
                Assert(mwStatus.Contains("TaskDrawer.Margin"), "任务抽屉避开左侧导航栏");
            }
            var homeProj = TryReadSource(Path.Combine("Views", "HomeView.cs"));
            if (homeProj is not null)
            {
                Assert(homeProj.Contains("ProjectLibraryButton"), "主页项目板块右上角有【项目库】按钮");
                Assert(homeProj.Contains("ProjectUi.StatusChip"), "主页方块显示状态(进行中/准备中)");
                Assert(homeProj.Contains("ProjectUi.DotsButton"), "主页方块用三个点菜单(取消右键)");
            }
            var picker = TryReadSource(Path.Combine("Views", "ProjectPickerView.cs"));
            if (picker is not null)
            {
                Assert(picker.Contains("IconName.Folder") && picker.Contains("UniformGrid"), "项目选择器用田字形文件夹图标");
                Assert(picker.Contains("ProjectUi.DotsButton") && picker.Contains("ShowEditor"), "项目用三个点菜单;编辑取代网格");
                Assert(picker.Contains("_onPick(p.ProjectId)") && !picker.Contains("Overlay.CloseActive(); _onPick"), "选中项目不自动关抽屉(用户确认)");
                Assert(picker.Contains("p.Pinned"), "项目选择器显示置顶标记");
                Assert(picker.Contains("sel ? \"FgOnSelected\""), "★ 墨白皮肤:选中项目的图标/标题/状态走 FgOnSelected(不再黑底黑字)");
                Assert(picker.Contains("基于该项目开始会话"), "选中项目后抽屉里提示【基于该项目开始会话】");
            }
            var editorSrc = TryReadSource(Path.Combine("Views", "ProjectEditor.cs"));
            if (editorSrc is not null)
            {
                Assert(editorSrc.Contains("PickFolder") && editorSrc.Contains("SetAiPermission") && editorSrc.Contains("重定向"), "项目编辑器可选文件夹/设 AI 权限/重定向路径(新建编辑共用)");
                Assert(editorSrc.Contains("AddAttachSlot") && editorSrc.Contains("Ui.Shake"), "附件文件夹靠 + 逐个加,可多个;空槽再按 + 会震荡");
            }
            var chatSrc = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (chatSrc is not null)
            {
                Assert(chatSrc.Contains("MoveToNewProject") && chatSrc.Contains("MoveToProject"), "普通会话可新建项目并移入 / 移动到项目");
                Assert(chatSrc.Contains("MoveSessionToWorkspace"), "会话可发送到其它工作空间");
                Assert(chatSrc.Contains("\"FgOnSelected\""), "会话选中态字色用 FgOnSelected(墨白不再黑底黑字)");
                Assert(chatSrc.Contains("Accent") && chatSrc.Contains("inProject"), "项目会话标题着重色区分普通会话");
                Assert(chatSrc.Contains("ChatOpener") && chatSrc.Contains("VerticalAlignment.Center"), "空态输入框竖直居中(像 GPT)+ 开场问候");
                Assert(chatSrc.Contains("AttachButton") && chatSrc.Contains("PickFiles") && chatSrc.Contains("OnInputPaste"), "可加附件(选文件/文件夹)+ 输入框粘贴截图");
                Assert(chatSrc.Contains("GhostButton") && chatSrc.Contains("虚线") || chatSrc.Contains("StrokeDashArray"), "幽灵会话:虚线边框会话面板");
                Assert(chatSrc.Contains("OpenTrash") && chatSrc.Contains("已删除"), "会话列表底部有【已删除】入口");
                Assert(chatSrc.Contains("VerticalAlignment.Stretch") && chatSrc.Contains("CornerRadius(8, 0, 0, 8)"), "项目把手是整条竖直窄条(非高度居中小按钮)");
                Assert(chatSrc.Contains("ToNormal(); Overlay.CloseActive()"), "选普通会话直接收起抽屉(选项目则不收)");
                // 幽灵会话可退出:按钮是开关,状态决定实线/虚线,且只在普通会话上下文出现
                Assert(chatSrc.Contains("void ToggleGhost()") && chatSrc.Contains("if (InGhost) { ToNormal();"), "幽灵按钮可【退出】(再按回普通会话的空态)");
                Assert(chatSrc.Contains("GhostButton(bool active)") && chatSrc.Contains("if (!active) ring.StrokeDashArray"), "幽灵中=实线,未进入=虚线");
                Assert(chatSrc.Contains("_ghostHost.Content = inProject ? null : GhostButton(InGhost)"), "项目会话下不显示幽灵按钮");
                // ★★ 幽灵外壳要罩住【每一个场景】(用户裁定 2026-08-03)。同传/文件翻译/多语表/回信
                //   都是提前 return 的分支,以前用的是普通卡 —— 手里真拿着幽灵会话,屏幕上一点痕迹都没有。
                Assert(chatSrc.Contains("var only = ConvShell(body, isGhost);"),
                       "★ 同传/文件翻译/多语表 走幽灵外壳");
                Assert(chatSrc.Contains("ConvShell(rBody, isGhost)"),
                       "★ 回信场景走幽灵外壳");
                Assert(chatSrc.Contains("ConvShell(PlaceholderCenter(), InGhost)"),
                       "★ 占位空间也走幽灵外壳 —— 按钮已经在那儿了,按下去毫无反应比不给更假");
                Assert(!chatSrc.Contains("NewGhostSession(_wsKey);"),
                       "★ 建幽灵必须带上当前场景标记,裸调用就是退化回「永远是普通文字会话」");
                var shell = Slice(chatSrc, "FrameworkElement ConvShell(", "FrameworkElement PlaceholderCenter()");
                Assert(shell is not null && shell.Contains("BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent")
                       && !shell.Contains("DockPanel.SetDock(bWrap, Dock.Top)"),
                       "★★ 进出幽灵态一个像素都不许动:提示压在边框上不占布局,且补一圈透明边框对齐 Ui.Card 的 1px");
                Assert(chatSrc.Contains("bool EndGhostOnSceneSwitch()") && chatSrc.Contains("EndGhostOnSceneSwitch();"),
                       "★ 换场景 = 换了一次 —— 幽灵的承诺只管这一次,切场景就抹掉");
                // ★ 幽灵按钮【每个工作空间都给】(2026-08-01 用户裁定)——
                //   判据是"这个空间给不给新建会话",而 + 已经在所有空间都给了。
                Assert(!chatSrc.Contains("_wsKey == \"chat\" && !inProject"),
                       "★ 幽灵按钮不再只给聊天(少给的空间会因为少一颗 26px 按钮而整列跳 2px)");
                Assert(chatSrc.Contains("Margin = new Thickness(0, 0, 0, 6), MinHeight = 26 }"),
                       "★ 动作行高度固定 —— 列表位置不许随有没有按钮而上下跳");
                Assert(chatSrc.Contains("_ctxTitle.MaxHeight = 42") && chatSrc.Contains("_ctxTitle.TextWrapping = TextWrapping.Wrap"), "长项目名可显示两排,交互按钮另起一行");
                Assert(chatSrc.Contains("Width = 16,") && chatSrc.Contains("BorderThickness = new Thickness(1, 1, 0, 1)"), "项目把手:窄条 + 右缘不描边(像被截断的面板)");
            }

            // 附件:只带路径/剪贴板指令,不真发内容;发送记录附件且仍不伪造回复
            var att = new Services.ChatAttachment(Services.AttachKind.Image, "x.png", "x.png");
            Assert(att.IsImage, "图片附件 IsImage=true");
            Assert(new Services.ChatAttachment(Services.AttachKind.File, "a.zip", "a.zip").IsImage == false, "文件附件 IsImage=false");
            var cc2 = new Services.ChatCenter();
            var chatSes = cc2.NewSession(null, "chat");
            cc2.Send(chatSes.SessionId, "", new[] { att });
            var m3 = cc2.MessagesOf(chatSes.SessionId).ToList();
            Assert(m3.Any(x => x.Role == Services.ChatRole.User && x.Attachments is { Count: 1 }), "仅附件也能发送并记下引用");
            Assert(!m3.Any(x => x.Role == Services.ChatRole.Assistant), "带附件发送同样不伪造 AI 回复");

            // 会话:置顶 / 软删除进已删除(30 天)/ 恢复 / 项目删除连会话
            var cc3 = new Services.ChatCenter();
            var sa = cc3.NewSession(null, "chat");
            var sb = cc3.NewSession(null, "chat");
            cc3.Send(sa.SessionId, "hi");
            cc3.TogglePin(sb.SessionId);
            Assert(cc3.NormalSessions("chat").First().SessionId == sb.SessionId, "置顶会话排最前(盖过更近的)");
            var pjS = cc3.NewSession("prjZ", "chat");
            cc3.DeleteProjectSessions("prjZ");
            // ★ 新语义:项目删除的会话【保留 ProjectId 跟随项目】,不进普通会话垃圾篓
            Assert(!cc3.NormalSessions("chat").Any(x => x.SessionId == pjS.SessionId), "删项目后其会话离开普通列表");
            Assert(!cc3.Deleted("chat").Any(x => x.SessionId == pjS.SessionId), "★ 项目会话不进【普通会话】垃圾篓(跟随项目)");
            Assert(cc3.AllSessionsOf("prjZ").Any(x => x.SessionId == pjS.SessionId), "跟随项目的会话在 AllSessionsOf 里可见(只读浏览)");
            cc3.RestoreProjectSessions("prjZ");
            Assert(cc3.SessionsOf("prjZ").Any(x => x.SessionId == pjS.SessionId), "项目恢复时其会话一并恢复");
            cc3.DeleteProjectSessions("prjZ");   // 复原到删除态,便于后续断言不受影响
            cc3.Delete(sa.SessionId);
            Assert(!cc3.NormalSessions("chat").Any(x => x.SessionId == sa.SessionId), "删除后离开普通列表");
            Assert(cc3.Deleted("chat").Any(x => x.SessionId == sa.SessionId) && cc3.MessagesOf(sa.SessionId).Any(), "★ 软删除:进已删除、消息仍在(可恢复)");
            cc3.Restore(sa.SessionId);
            Assert(cc3.NormalSessions("chat").Any(x => x.SessionId == sa.SessionId), "可从已删除恢复");
            cc3.Delete(sa.SessionId);
            cc3.SweepExpiredDeleted(DateTime.Now.AddDays(Services.ChatCenter.TrashRetentionDays + 1));
            Assert(cc3.Find(sa.SessionId) is null, "超过保留期自动清除(不可恢复)");

            // ---- 待办分类(仿提醒事项)+ 图标齿轮 -> Apple 同步设置 + 主页精简菜单 ----
            {
                var fc = new Services.TodoCenter();
                fc.Add(new Services.TodoItem("", "个人的", Services.TodoKind.Personal));
                fc.Add(new Services.TodoItem("", "家务的", Services.TodoKind.Chore));
                fc.Add(new Services.TodoItem("", "买牛奶", Services.TodoKind.Shopping));
                fc.Add(new Services.TodoItem("", "今天到期", Services.TodoKind.Personal, Due: DateTime.Today.AddHours(20)));
                fc.Add(new Services.TodoItem("", "下周到期", Services.TodoKind.Personal, Due: DateTime.Today.AddDays(7)));
                Assert(Enum.IsDefined(typeof(Services.TodoKind), "Shopping"), "待办种类含【采购清单】");
                var fcActive = fc.Active().ToList();
                Assert(fcActive.Count(x => x.Kind == Services.TodoKind.Shopping) == 1, "采购清单项可正常建立");
                // "今天" = 有截止且今天或更早(逾期也算)
                var fcToday = fcActive.Where(x => x.Due is { } d && d.Date <= DateTime.Today).ToList();
                Assert(fcToday.Count == 1 && fcToday[0].Title == "今天到期", "「今天」分类= 今天或更早到期(不含以后的)");
            }
            var hvFilter = TryReadSource(Path.Combine("Views", "HomeView.cs"));
            if (hvFilter is not null)
            {
                Assert(hvFilter.Contains("TodoFilterCaret") && hvFilter.Contains("采购清单") && hvFilter.Contains("\"today\""),
                       "待办标题右侧有分类下拉(全部/今天/待办/家务/采购清单)");
                Assert(hvFilter.Contains("HomeTodoFilter"), "所选分类记在本机偏好里");
                Assert(hvFilter.Contains("OpenAppleSyncSettings"), "日历/待办图标点齿轮 -> Apple 同步设置");
                Assert(hvFilter.Contains("homeMenu: true"), "★ 主页项目方块用精简菜单(只置顶 + 打开文件夹)");
            }
            // ---- 交互打磨:菜单关闭不穿透 / 命中区放大 / 全局关掉悬停提示 ----
            var puHit = TryReadSource(Path.Combine("Views", "ProjectUi.cs"));
            if (puHit is not null)
            {
                Assert(puHit.Contains("MenuHost.Show"), "★ 菜单统一走 MenuHost 出口(开关状态集中记录)");
                Assert(puHit.Contains("Width = 34, Height = 30"), "三点按钮命中区放大");
                Assert(puHit.Contains("PreviewMouseLeftButtonDown += (_, e) => e.Handled = true"), "按钮吃掉【按下】,避免松开落到方块");
            }
            var hvHit = TryReadSource(Path.Combine("Views", "HomeView.cs"));
            if (hvHit is not null)
                Assert(hvHit.Contains("ProjectUi.JustClosedMenu()"), "主页方块点击前先判菜单是否刚关");
            var pkHit = TryReadSource(Path.Combine("Views", "ProjectPickerView.cs"));
            if (pkHit is not null)
                Assert(pkHit.Contains("ProjectUi.JustClosedMenu()"), "抽屉方块点击前先判菜单是否刚关");
            // ★ 浮层/菜单开着时,点背后【只关它】,不该顺带按到背后的按钮 —— 统一在主窗口拦
            // ★★ 行为断言:直接复现"点不动"那个事故。
            //   造一个菜单,只发 Opened、【永远不发 Closed】(孤儿弹窗就是这样),
            //   然后要求 MenuHost 仍然认为"没有菜单开着" —— 否则主窗口会把之后每一次点击都吞掉。
            //   ★ 结构断言证明不了这条:计数器版本的源码里同样有 SwallowClick 三个字。
            {
                var orphan = new System.Windows.Controls.ContextMenu();
                Views.MenuHost.Track(orphan);
                orphan.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.ContextMenu.OpenedEvent));
                Assert(!Views.MenuHost.IsOpen, "★ 只发 Opened 不发 Closed(孤儿弹窗)不会把界面锁死");
                Assert(!Views.MenuHost.SwallowClick, "★ 孤儿弹窗不会让主窗口一直吞点击");

                // 重复登记同一个菜单实例("+"附件键就是复用同一个)不该攒出状态
                Views.MenuHost.Track(orphan);
                Views.MenuHost.Track(orphan);
                Assert(!Views.MenuHost.IsOpen, "同一菜单重复登记不会攒出「开着」的假象");

                // 总闸能把账清干净,且不留 300ms 宽限把紧接着的点击也吞掉
                Views.MenuHost.CloseAll();
                Assert(!Views.MenuHost.SwallowClick, "★ CloseAll 之后立刻就能点(宽限期一并清掉)");
            }
            var mhSrc = TryReadSource(Path.Combine("Views", "MenuHost.cs"));
            if (mhSrc is not null)
            {
                Assert(mhSrc.Contains("SwallowClick"), "MenuHost 记录菜单开/刚关状态");
                // ★★ 事故教训:状态【不许攒】。原先用计数器记"开着几个",只要漏一次 Closed
                //   就永远回不到 0 -> 主窗口把此后每一次点击都吞掉,整个界面点不动。
                Assert(!Body(mhSrc).Contains("_openCount"), "★ 菜单状态不用计数器(漏一次 Closed 就会把界面永久锁死)");
                Assert(mhSrc.Contains("if (!m.IsOpen)") && mhSrc.Contains("_tracked.RemoveAt(i)"),
                       "★ 每次实地查验菜单自己开没开,顺手清账");
                Assert(mhSrc.Contains("fe.IsLoaded"), "★ 孤儿弹窗(挂靠按钮已被重建摘掉)要就地关掉,不能一直挡着点击");
                Assert(mhSrc.Contains("public static void CloseAll()"), "留一个总闸,Esc 能强行救回来");
            }
            var mwSwallow = TryReadSource("MainWindow.xaml.cs");
            if (mwSwallow is not null)
            {
                // Esc 是总闸:浮层和菜单都要能关。只管浮层的话,菜单状态一卡住就只能杀进程。
                var esc = mwSwallow[mwSwallow.IndexOf("Key.Escape", StringComparison.Ordinal)..];
                esc = esc[..700];
                Assert(esc.Contains("Overlay.CloseActive()") && esc.Contains("MenuHost.CloseAll()"),
                       "★ Esc 同时关浮层与菜单(留一条自救路径)");
                Assert(esc.IndexOf("AnyDropDownOpen(this)", StringComparison.Ordinal) is int di && di >= 0
                       && di < esc.IndexOf("Overlay.CloseActive()", StringComparison.Ordinal),
                       "★ 但下拉框【排在浮层之前】—— 抽屉里全是 ComboBox,想收个下拉却把整个抽屉关掉,没保存的表单一起丢");
                Assert(mwSwallow.Contains("MenuHost.SwallowClick") && mwSwallow.Contains("me.Handled = true"),
                       "★ 菜单开着时点背后:主窗口一次性吞掉这次点击(只关菜单)");
                var pmd = mwSwallow[mwSwallow.IndexOf("PreviewMouseDown +=", StringComparison.Ordinal)..];
                Assert(pmd.IndexOf("MenuHost.SwallowClick", StringComparison.Ordinal) < pmd.IndexOf("if (!Overlay.IsOpen) return;", StringComparison.Ordinal),
                       "菜单判断排在 Overlay 之前(菜单不在 Overlay 体系里,漏判就会穿透)");
            }
            // ★ 菜单项里【不许当场】弹模态对话框:菜单还没关完就接管消息循环,回来又重建界面,
            //   那次 Closed 就可能永远不来 —— 正是 2026-07-30"点不动"事故的触发路径。
            var cvMenu = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvMenu is not null)
            {
                var ab = cvMenu[cvMenu.IndexOf("var mFile = new MenuItem", StringComparison.Ordinal)..];
                ab = ab[..700];
                Assert(ab.Contains("DispatcherPriority.Background") && !ab.Contains("mFile.Click += (_, _) => PickFiles();"),
                       "★ 附件菜单的选文件/选文件夹延后执行(先让菜单关干净再弹对话框)");
            }
            // 所有菜单都必须走 MenuHost,不能各自 IsOpen=true(否则漏登记 -> 又会穿透)
            foreach (var f in new[] { Path.Combine("Views", "ChatView.cs"), Path.Combine("Views", "HomeView.cs"), Path.Combine("Views", "ProjectUi.cs") })
            {
                var src = TryReadSource(f);
                if (src is null) continue;
                Assert(!src.Contains("IsOpen = true"), $"{Path.GetFileName(f)} 不再自行开菜单(统一走 MenuHost)");
            }
            var cvAnim = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvAnim is not null)
            {
                Assert(cvAnim.Contains("AnimateIn(bubble") && cvAnim.Contains("_seenMsgCount"),
                       "★ 发出的消息有出现动画,且只给新增的那几条播(旧消息重建不重复动)");
            }

            var ctlSlider = TryReadSource(Path.Combine("Theme", "Controls.xaml"));
            if (ctlSlider is not null)
                Assert(ctlSlider.Contains("TargetType=\"Slider\""), "滑条有主题化样式(不用系统外观)");

            var ctlTip = TryReadSource(Path.Combine("Theme", "Controls.xaml"));
            if (ctlTip is not null)
                Assert(ctlTip.Contains("TargetType=\"ToolTip\"") && ctlTip.Contains("Visibility\" Value=\"Collapsed"),
                       "★ 全局关闭鼠标悬停提示(一处关掉,不逐个删)");

            foreach (var skinFile in new[] { "Ink.xaml", "Breeze.xaml", "Warm.xaml" })
            {
                var sk = TryReadSource(Path.Combine("Theme", skinFile));
                if (sk is null) continue;
                foreach (var key in new[] { "StateOngoingBorder", "StateOngoingFill", "StateDoneBorder", "StateDoneFill", "StateTrashBorder", "StateTrashFill" })
                    Assert(sk.Contains(key), $"{skinFile} 定义了 {key}");
                // ★ 进行中与已删除的描边【不能同色】—— 暖萌曾用陶土橙撞上危险红,两块并排分不清(渲染诊断发现)
                string ColorOf(string key)
                {
                    var i = sk.IndexOf($"x:Key=\"{key}\"", StringComparison.Ordinal);
                    if (i < 0) return "";
                    var c = sk.IndexOf("Color=\"", i, StringComparison.Ordinal);
                    return c < 0 ? "" : sk.Substring(c + 7, 7);
                }
                Assert(!string.Equals(ColorOf("StateOngoingBorder"), ColorOf("StateTrashBorder"), StringComparison.OrdinalIgnoreCase),
                       $"{skinFile}:进行中与已删除的描边不同色(否则分不清)");
            }

            var uiPanel = TryReadSource(Path.Combine("Views", "Ui.cs"));
            if (uiPanel is not null)
                Assert(uiPanel.Contains("iconAction") && uiPanel.Contains("IconName.Settings"), "面板标题图标可 hover 变齿轮并点击");
            var puHome = TryReadSource(Path.Combine("Views", "ProjectUi.cs"));
            if (puHome is not null)
            {
                Assert(puHome.Contains("BuildHomeMenu"), "存在主页精简菜单");
                var hm = puHome[puHome.IndexOf("BuildHomeMenu", StringComparison.Ordinal)..];
                hm = hm[..hm.IndexOf("public static FrameworkElement DotsButton", StringComparison.Ordinal)];
                Assert(!hm.Contains("删除项目") && !hm.Contains("AI 权限") && !hm.Contains("发送到工作空间"),
                       "★ 主页菜单不含删除/AI权限/发送空间(去工作空间设置)");
            }
            var setSync = TryReadSource(Path.Combine("Views", "SettingsView.cs"));
            if (setSync is not null)
            {
                Assert(setSync.Contains("与 Apple 日历同步") && setSync.Contains("只往本机拉"),
                       "★ 设置里的 Apple 日历同步已接入,且明说是【只读拉取】(不向 Apple 写入)");
                Assert(!setSync.Contains("PUT") && !setSync.Contains("推送给 Apple"),
                       "★★ 这一版没有任何写回 Apple 的路径(写回不可逆,等拉取验证过再开)");
                Assert(setSync.Contains("RevealAppleSync"), "可从主页齿轮跳转并高亮该板块");
            }

            // ---- 待办:批量删除 / 自动清理 / AI 建立标记 ----
            {
                var tc = new Services.TodoCenter();
                var a1 = tc.Add(new Services.TodoItem("", "手动建的", Services.TodoKind.Personal));
                var a2 = tc.Add(new Services.TodoItem("", "AI 建的", Services.TodoKind.Personal, CreatedByAi: true));
                Assert(!tc.Items.First(x => x.Id == a1).CreatedByAi, "手动建立不带 AI 标记");
                Assert(tc.Items.First(x => x.Id == a2).CreatedByAi, "AI 建立带 AI 标记(界面据此显示星标)");

                // 立即删除全部已完成:只清已完成,未完成的不动(不再做选择性批量删除)
                tc.Toggle(a2);
                tc.ClearCompleted();
                Assert(tc.Items.Any(x => x.Id == a1), "清空已完成不动未完成的");
                Assert(!tc.Items.Any(x => x.Id == a2), "清空已完成删掉已完成的");

                // 自动清理:按完成时间算;0 = 关闭;没有完成时间戳的不误删
                var pc2 = new Services.TodoCenter();
                var old = pc2.Add(new Services.TodoItem("", "很久前完成", Services.TodoKind.Personal,
                    Done: true, CompletedAt: DateTime.Now.AddDays(-40)));
                var fresh = pc2.Add(new Services.TodoItem("", "刚完成", Services.TodoKind.Personal,
                    Done: true, CompletedAt: DateTime.Now.AddDays(-1)));
                var noStamp = pc2.Add(new Services.TodoItem("", "完成但没时间戳", Services.TodoKind.Personal, Done: true));
                Assert(pc2.PurgeCompletedOlderThan(0) == 0, "0 天 = 关闭自动清理,一条都不删");
                Assert(pc2.Items.Count == 3, "关闭时确实没删");
                var n = pc2.PurgeCompletedOlderThan(30);
                Assert(n == 1 && !pc2.Items.Any(x => x.Id == old), "清掉超过 30 天完成的");
                Assert(pc2.Items.Any(x => x.Id == fresh), "没超期的保留");
                Assert(pc2.Items.Any(x => x.Id == noStamp), "★ 没有完成时间戳的不误删(宁可留着)");
            }
            var arch = TryReadSource(Path.Combine("Views", "TodoArchiveView.cs"));
            if (arch is not null)
            {
                Assert(arch.Contains("立即删除全部已完成") && !arch.Contains("RemoveMany") && !arch.Contains("全选"),
                       "已完成抽屉只保留【一键删全部】,不做选择性批量删除(用户裁定)");
                Assert(arch.Contains("TodoAutoPurgeDays") && arch.Contains("PurgeCompletedOlderThan"), "已完成抽屉可设自动清理天数");
                Assert(!arch.Contains("ComboBox"), "★ 保留期用分段按钮而非下拉框(修:下拉弹层穿透点到背后按钮)");
                Assert(arch.Contains("ConfirmDialog.Show"), "删除全部走自绘二次确认");
            }
            var tlSrc = TryReadSource(Path.Combine("Views", "TodoList.cs"));
            if (tlSrc is not null)
                Assert(tlSrc.Contains("CreatedByAi") && tlSrc.Contains("IconName.Ai"), "待办行显示 AI 建立标记");
            var calMod = TryReadSource(Path.Combine("Views", "CalendarModel.cs"));
            if (calMod is not null)
                Assert(calMod.Contains("CreatedByAi"), "日程模型也带 AI 建立标记");
            var appPurge = TryReadSource("App.xaml.cs");
            if (appPurge is not null)
                Assert(appPurge.Contains("PurgeCompletedOlderThan(Settings.TodoAutoPurgeDays)"), "启动时按设置自动清理一次");

            // ---- D45 可见范围过滤(fail-closed)----
            {
                const string me = "m-me", other = "m-other";
                // 纯函数规则
                Assert(Services.MemberContext.CanSee(Services.ProjectScope.Family, other, me), "家庭范围:别人建的也可见");
                Assert(Services.MemberContext.CanSee(Services.ProjectScope.Personal, me, me), "个人范围:自己的可见");
                Assert(!Services.MemberContext.CanSee(Services.ProjectScope.Personal, other, me), "★ 个人范围:别人的【不可见】");
                Assert(!Services.MemberContext.CanSee(Services.ProjectScope.OnlyMe, other, me), "★ 仅本人:别人的【不可见】");
                Assert(!Services.MemberContext.CanSee(Services.ProjectScope.Personal, null, me), "★ 所有者未知 -> 不可见(fail-closed)");
                Assert(!Services.MemberContext.CanSee(Services.ProjectScope.OnlyMe, "", me), "★ 所有者为空串 -> 不可见(fail-closed)");
                Assert(Services.MemberContext.CanSee(Services.ProjectScope.Family, null, me), "家庭范围即便所有者未知也可见(家庭本就共享)");

                // 落到列表接口上:外来私人项目/会话绝不出现
                var pv = new Services.ProjectCenter();
                pv.Import(new List<Services.Project>
                {
                    new("a", "我的个人", "", "chat", Services.ProjectScope.Personal, DateTime.Now, OwnerMemberId: Services.MemberContext.Current),
                    new("b", "别人的个人", "", "chat", Services.ProjectScope.Personal, DateTime.Now, OwnerMemberId: other),
                    new("c", "家庭的", "", "chat", Services.ProjectScope.Family, DateTime.Now, OwnerMemberId: other),
                    new("d", "别人的仅本人(已完成)", "", "chat", Services.ProjectScope.OnlyMe, DateTime.Now, Status: Services.ProjectStatus.Done, OwnerMemberId: other),
                });
                var vis = pv.Ongoing().Select(x => x.ProjectId).ToList();
                Assert(vis.Contains("a") && vis.Contains("c"), "自己的个人 + 家庭的可见");
                Assert(!vis.Contains("b"), "★ 别人的个人项目不出现在 Ongoing");
                Assert(!pv.All().Any(x => x.ProjectId == "b"), "★ 别人的个人项目不出现在 All");
                Assert(!pv.Completed().Any(x => x.ProjectId == "d"), "★ 别人的仅本人项目不出现在项目库");

                var cv = new Services.ChatCenter();
                cv.Import(new Services.ChatCenter.Snapshot(new List<Services.ChatSession>
                {
                    new("s1", "我的", null, Services.ProjectScope.Personal, DateTime.Now, OwnerMemberId: Services.MemberContext.Current),
                    new("s2", "别人的", null, Services.ProjectScope.Personal, DateTime.Now, OwnerMemberId: other),
                    new("s3", "家庭的", null, Services.ProjectScope.Family, DateTime.Now, OwnerMemberId: other),
                    new("s4", "别人的(已删)", null, Services.ProjectScope.OnlyMe, DateTime.Now, DeletedAt: DateTime.Now, OwnerMemberId: other),
                }, new List<Services.ChatMessage>()));
                var sv = cv.NormalSessions("chat").Select(x => x.SessionId).ToList();
                Assert(sv.Contains("s1") && sv.Contains("s3"), "自己的 + 家庭会话可见");
                Assert(!sv.Contains("s2"), "★ 别人的私人会话不出现");
                Assert(!cv.Deleted("chat").Any(x => x.SessionId == "s4"), "★ 别人的私人会话也不出现在回收站");
                Assert(cv.DeletedCount("chat") == cv.Deleted("chat").Count(), "回收站计数与列表口径一致(都按可见性过滤)");

                // 旧存档迁移:没有所有者的条目认领为本地成员(否则老用户的东西会集体隐身)
                var legacy = new Services.ProjectCenter();
                legacy.Import(new List<Services.Project>
                {
                    new("old", "旧存档项目", "", "chat", Services.ProjectScope.Personal, DateTime.Now),   // 无 OwnerMemberId
                });
                Assert(legacy.Items[0].OwnerMemberId == Services.MemberContext.LocalMemberId, "旧存档条目导入时认领为本地成员");
                Assert(legacy.Ongoing().Any(x => x.ProjectId == "old"), "迁移后旧条目仍然可见(不会集体隐身)");

                // 本地 Add 打所有者戳(否则静默不可见);Import 不打戳(外来条目保持原所有者)
                var stamp = new Services.ProjectCenter();
                stamp.Add(new Services.Project("z", "没写所有者", "", "chat", Services.ProjectScope.Personal, DateTime.Now));
                Assert(stamp.Items[0].OwnerMemberId == Services.MemberContext.Current, "本地 Add 自动补当前成员(避免静默不可见)");
                Assert(stamp.Ongoing().Any(x => x.ProjectId == "z"), "本地新增的个人项目可见");
                Assert(pv.Items.First(x => x.ProjectId == "b").OwnerMemberId == other, "★ Import 不给外来条目打戳(所有者保持不变)");
            }
            var memSrc = TryReadSource(Path.Combine("Services", "MemberContext.cs"));
            if (memSrc is not null)
            {
                // 只看【代码】,不看注释 —— 注释里正写着"绝不使用它",按整文件匹配会误判(同 ScrollIntoView 那次)
                var memCode = string.Join("\n", memSrc.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
                Assert(!memCode.Contains("CachedMemberDisplayName"),
                       "★ 可见范围判定【不】读界面显示名缓存(铁律:主体只来自成员表)");
            }

            // ---- 本地存档(明文,D21/D22 口径)----
            {
                var store = new Services.ChatCenter();
                var keepS = store.NewSession(null, "chat");
                store.Send(keepS.SessionId, "留下来");
                var ghostS = store.NewGhostSession("chat");
                store.Send(ghostS.SessionId, "别落盘");
                var delS = store.NewSession(null, "chat");
                store.Send(delS.SessionId, "回收站里的");
                store.Delete(delS.SessionId);

                var snap = store.Export();
                // ★ 幽灵会话是"不保留记录"的承诺 —— 落盘就是毁约
                Assert(!snap.Sessions.Any(x => x.Ghost), "★ 幽灵会话不进存档");
                Assert(!snap.Messages.Any(m => m.SessionId == ghostS.SessionId), "★ 幽灵会话的消息也不进存档");
                Assert(snap.Sessions.Any(x => x.SessionId == keepS.SessionId), "普通会话进存档");
                Assert(snap.Sessions.Any(x => x.SessionId == delS.SessionId && x.DeletedAt is not null), "已删除会话连 DeletedAt 一起存(重启后继续走 30 天)");

                // ★★ 幽灵的"不留痕"在【场景文档】里也得算数(用户裁定 2026-08-03)——
                //   回信/译表/文件翻译各自按 sessionId 存一张表,ChatCenter 这层 Ghost 过滤管不到它们。
                //   自动存盘是防抖的(任何 Changed 都排一次写盘),打字途中就会写,光靠"退出时删"守不住。
                {
                    var gid = ghostS.SessionId;
                    bool IsGhost(string sid) => store.Find(sid)?.Ghost == true;

                    var rs2 = new Services.ReplyState { IsGhostSession = IsGhost };
                    rs2.SetSession(gid); rs2.Doc.Draft = "幽灵回信正文";
                    Assert(!rs2.Export().Docs.ContainsKey(gid), "★ 幽灵回信的文档不落盘");

                    var is2 = new Services.I18nState { IsGhostSession = IsGhost };
                    is2.SetSession(gid); is2.Doc.SourceLang = "zh";
                    Assert(!is2.ExportDocs().ContainsKey(gid), "★ 幽灵译表的文档不落盘");

                    var fs2 = new Services.FileTransState { IsGhostSession = IsGhost };
                    var tmpF = Path.Combine(Path.GetTempPath(), "localai-ghost-ft.png");
                    try { File.WriteAllBytes(tmpF, new byte[] { 1, 2, 3 }); } catch { }
                    fs2.SetFile(gid, tmpF, ghost: true);
                    Assert(!fs2.Export().ContainsKey(gid), "★ 幽灵文件翻译的文档不落盘");
                    Assert(fs2.DocOf(gid)?.Cache is null,
                           "★★ 幽灵文件翻译【不抄副本】—— 副本是为了「源没了会话还能活」,而幽灵本来就活不过这一次");

                    // 抹掉幽灵 = 三张表跟着清(销毁路径只准有一条,广播出去各自清)
                    var purged = false;
                    store.GhostsPurged += ids => purged = ids.Contains(gid);
                    store.PurgeGhosts();
                    Assert(purged, "★ PurgeGhosts 会广播 id —— 场景文档靠它才知道该清谁");
                    // ★★ 上面这些都是自检自己把钩子接上再验的;【生产里谁接的钩子】必须单独盯 ——
                    //   删掉 App 里那一句 AttachGhostDiscipline(),幽灵内容会照常落盘而上面全绿。
                    //   这正是"函数还在、调用点没了"那一类,编译与行为断言都抓不到。
                    // ★ 看【去注释后的正文】:把调用注掉也能骗过 Contains —— 这个坑本仓栗过(注释里提到那个词)
                    var appGhost = TryReadSource("App.xaml.cs") is { } _ag ? Body(_ag) : null;
                    if (appGhost is not null)
                    {
                        Assert(appGhost.Contains("void AttachGhostDiscipline()") && appGhost.Contains("AttachGhostDiscipline();"),
                               "★★ 幽灵纪律在 App 里【真的接上了】(定义 + 调用点都在,注掉也算没接)");
                        var ag = Slice(appGhost, "void AttachGhostDiscipline()", "void AttachAutoSave()");
                        Assert(ag is not null && ag.Contains("FileTrans.IsGhostSession = IsGhost")
                               && ag.Contains("I18n.IsGhostSession = IsGhost") && ag.Contains("Reply.IsGhostSession = IsGhost")
                               && ag.Contains("Chat.GhostsPurged +="),
                               "★ 三张场景文档表【都】接了过滤,且订阅了抹除广播 —— 少接一张就是少一条毁约路径");
                        Assert(ag is not null && ag.Contains("Interpret.Stop()"),
                               "★★ 幽灵被抹时正在进行的同传要当场结束 —— 否则横条还说【进行中】,转写却写进不存在的会话");
                    }
                }

                // JSON 往返:枚举存名字、可空时间戳、附件都要能还原
                var json = System.Text.Json.JsonSerializer.Serialize(snap, StoreJson);
                var snapBack = System.Text.Json.JsonSerializer.Deserialize<Services.ChatCenter.Snapshot>(json, StoreJson);
                var restored = new Services.ChatCenter();
                restored.Import(snapBack);
                Assert(restored.NormalSessions("chat").Any(x => x.SessionId == keepS.SessionId), "JSON 往返后普通会话还在");
                Assert(restored.MessagesOf(keepS.SessionId).Any(m => m.Text == "留下来"), "JSON 往返后消息还在");
                Assert(restored.Find(ghostS.SessionId) is null, "JSON 往返后幽灵会话不存在");
                Assert(restored.Deleted("chat").Any(x => x.SessionId == delS.SessionId), "JSON 往返后回收站还在");

                // 导入时扫过期:超过保留期的已删除会话不该被恢复
                var expired = new Services.ChatCenter();
                expired.Import(snapBack, DateTime.Now.AddDays(Services.ChatCenter.TrashRetentionDays + 1));
                Assert(expired.Find(delS.SessionId) is null, "导入时清掉已过 30 天的回收站项");

                // 项目 / 待办往返
                var pStore = new Services.ProjectCenter();
                pStore.Create("存档项目", Path.Combine(Path.GetTempPath(), "p"), new[] { "a", "b" }, Services.ProjectScope.Family);
                var pJson = System.Text.Json.JsonSerializer.Serialize(pStore.Export(), StoreJson);
                var pBack = new Services.ProjectCenter();
                pBack.Import(System.Text.Json.JsonSerializer.Deserialize<List<Services.Project>>(pJson, StoreJson));
                Assert(pBack.Items.Count == 1 && pBack.Items[0].Attachments is { Count: 2 }, "项目往返(含多附件夹)");

                var tStore = new Services.TodoCenter();
                tStore.Add(new Services.TodoItem("", "存档待办", Services.TodoKind.Chore, Due: DateTime.Today));
                var tJson = System.Text.Json.JsonSerializer.Serialize(tStore.Export(), StoreJson);
                var tBack = new Services.TodoCenter();
                tBack.Import(System.Text.Json.JsonSerializer.Deserialize<List<Services.TodoItem>>(tJson, StoreJson));
                Assert(tBack.Items.Count == 1 && tBack.Items[0].Title == "存档待办", "待办往返");

                // 损坏存档不能拖垮启动
                var badPath = Path.Combine(Path.GetTempPath(), "localai-bad-" + Guid.NewGuid().ToString("N")[..6] + ".json");
                File.WriteAllText(badPath, "{ 这不是合法 JSON ");
                Assert(Services.ClientStore.Load<List<Services.Project>>(badPath) is null, "存档损坏时返回空,不抛异常");
                Assert(File.Exists(badPath + ".corrupt"), "坏档被改名留证");
                try { File.Delete(badPath + ".corrupt"); } catch { }

                // 原子写 + 读回
                var okPath = Path.Combine(Path.GetTempPath(), "localai-ok-" + Guid.NewGuid().ToString("N")[..6] + ".json");
                Services.ClientStore.Save(okPath, tStore.Export());
                Assert(File.Exists(okPath) && !File.Exists(okPath + ".tmp"), "原子写完成后不留 .tmp");
                Assert(Services.ClientStore.Load<List<Services.TodoItem>>(okPath)?.Count == 1, "存档可读回");
                try { File.Delete(okPath); } catch { }
            }
            // ---- 输入框:换行 / 自动长高(上限 3 行)/ 粘贴 ----
            var cvInput = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvInput is not null)
            {
                Assert(cvInput.Contains("AcceptsReturn = true") && cvInput.Contains("InputMaxLines = 3"),
                       "★ 输入框可换行,最多长到 3 行");
                Assert(cvInput.Contains("MaxLines = InputMaxLines") && cvInput.Contains("VerticalScrollBarVisibility = ScrollBarVisibility.Auto"),
                       "超过 3 行在框内滚动(不无限顶掉会话区)");
                Assert(cvInput.Contains("ModifierKeys.Shift") && cvInput.Contains("SendCurrent();"),
                       "★ Shift+Enter 换行、单独 Enter 发送");
                Assert(cvInput.Contains("DataFormats.FileDrop") && cvInput.Contains("AddPaths(files)"),
                       "★ 可以粘贴【文件】进附件栏(从资源管理器复制)");
                Assert(cvInput.Contains("DispatcherPriority.Background") && cvInput.Contains("AddClipboardImage"),
                       "粘贴截图延后执行(粘贴处理器里直接重建界面会打断输入事件)");
                // ★ 真 bug:剪贴板【只有图片】时 TextBox 的 Paste 命令不可执行 -> DataObject.Pasting 压根不触发,
                //   所以 Ctrl+V 必须在【按键层】自己处理(用户实测"截图粘不进去")
                Assert(cvInput.Contains("e.Key == Key.V") && cvInput.Contains("TryPasteAttachment()"),
                       "★ Ctrl+V 在按键层处理(只挂 DataObject.Pasting 时截图粘不进去)");
                Assert(cvInput.Contains("ClipboardIntent.Decide"), "粘贴意图走可单测的纯函数");
            }
            {
                // 粘贴规则(纯函数,可单测)
                Assert(Services.ClipboardIntent.Decide(hasFiles: true, hasImage: false, hasText: true) == Services.ClipboardIntent.Kind.Files,
                       "★ 有文件就当附件(资源管理器复制会附带路径文本,那不是用户要的内容)");
                Assert(Services.ClipboardIntent.Decide(false, true, false) == Services.ClipboardIntent.Kind.Image,
                       "★ 只有图片 -> 当附件(截图的典型情形)");
                Assert(Services.ClipboardIntent.Decide(false, true, true) == Services.ClipboardIntent.Kind.Text,
                       "图片+文本 -> 走文本(网页富文本多半要的是字)");
                Assert(Services.ClipboardIntent.Decide(false, false, true) == Services.ClipboardIntent.Kind.Text,
                       "纯文本 -> 走文本");
            }

            // ---- 超长消息默认折叠(只折叠显示,给 AI 的仍是全文)----
            var cvFold = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvFold is not null)
            {
                Assert(cvFold.Contains("CollapseLines = 30"), "超过 30 行的消息默认折叠");
                Assert(cvFold.Contains("展开全部(") && cvFold.Contains("\"收起\""), "可展开、可再次收起");
                Assert(cvFold.Contains("_expandedBubbles"), "展开状态按会话+序号记住(重建后不丢)");
                // ★ 折叠只影响显示 —— 发送与存储用的都是 m.Text 全文;截行只发生在渲染那一句
                Assert(cvFold.Contains("expanded ? m.Text : string.Join(\"\\n\", lines.Take(CollapseLines))"),
                       "★ 折叠只截【显示】的行,原文一个字没少(给 AI 的是全文)");
                Assert(cvFold.Contains("只是显示折叠"), "代码里写明折叠不影响发给 AI 的内容");
            }

            // ---- 附件栏重做(2026-07-30 用户裁定)----
            var cvAtt = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvAtt is not null)
            {
                // ★ 发送前先清空 _pending,再 Send —— 否则 Send 同步触发 Changed 重建会把已发附件挂回输入框
                var sc = cvAtt.IndexOf("void SendCurrent()", StringComparison.Ordinal);
                var scEnd = cvAtt.IndexOf("FrameworkElement AttachButton", sc, StringComparison.Ordinal);
                var scBody = cvAtt[sc..scEnd];
                Assert(scBody.IndexOf("_pending.Clear()", StringComparison.Ordinal) < scBody.IndexOf("TheApp.Chat.Send", StringComparison.Ordinal),
                       "★ 发送前先清空待发附件(修:发出后附件还挂在输入框)");
                // 合并选择器:文件(可多选)+ 文件夹;去掉"选择图片"与"剪贴板"菜单项
                Assert(cvAtt.Contains("选择文件…") && cvAtt.Contains("选择文件夹…"), "选择器合并为【选择文件(多选)+ 选择文件夹】");
                Assert(!cvAtt.Contains("选择文件…(可多选)") , "选择文件按钮不带(可多选)括注(用户裁定)");
                Assert(!cvAtt.Contains("选择图片…") && !cvAtt.Contains("粘贴剪贴板截图"), "去掉独立的选择图片 / 剪贴板菜单项");
                Assert(cvAtt.Contains("Multiselect = true"), "文件对话框支持多选");
                Assert(cvAtt.Contains("MaxAttachments = 99") && cvAtt.Contains("SoftAttachLimit = 5"), "上限 99、软阈值 5");
                // 输入框内粘贴截图
                Assert(cvAtt.Contains("DataObject.AddPastingHandler") && cvAtt.Contains("OnInputPaste"), "★ 输入框内 Ctrl+V 粘贴截图进附件栏");
                Assert(cvAtt.Contains("CancelCommand()"), "剪贴板是图片时取消文本粘贴(不贴成乱码)");
                // 折叠 + 计数 + 一键清空(去掉橙黄"上下文吃紧"提醒;清空紧跟计数右边)
                Assert(!cvAtt.Contains("上下文会吃紧"), "去掉橙黄的上下文吃紧提醒(用户裁定)");
                Assert(cvAtt.Contains("附件 {_pending.Count} 个"), "附件栏只显示【附件 X 个】计数");
                Assert(cvAtt.Contains("MoreChip"), "超出 5 个折叠成 +N,不铺满输入区");
                Assert(cvAtt.Contains("_pending.Clear(); _justSent = true; BuildConversation();") && cvAtt.Contains("\"清空\""), "有一键清空附件(且清完焦点还在输入框)");
                // 按类型预览
                Assert(cvAtt.Contains("IconName.Pdf") && cvAtt.Contains("IconName.File") && cvAtt.Contains("AttachKind.Folder"),
                       "按类型预览:图片缩略图 / PDF 图标 / 文件图标 / 文件夹图标");
            }
            Assert(Enum.IsDefined(typeof(Services.AttachKind), "Folder"), "附件种类含 Folder");

            // ---- 崩溃兜底 + 附件对话框健壮性(修"添加附件闪退")----
            var appCrash = TryReadSource("App.xaml.cs");
            if (appCrash is not null)
            {
                Assert(appCrash.Contains("DispatcherUnhandledException") && appCrash.Contains("ex.Handled = true"),
                       "★ UI 线程异常有全局兜底(不再静默闪退)");
                Assert(appCrash.Contains("AppDomain.CurrentDomain.UnhandledException") && appCrash.Contains("LogCrash"),
                       "非 UI 线程异常也记日志(crash.log)");
            }
            var chatAtt = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (chatAtt is not null)
            {
                // Clipboard.GetImage() 必须在 try 内(它本身会抛);现位于 AddClipboardImage
                var acStart = chatAtt.IndexOf("void AddClipboardImage()", StringComparison.Ordinal);
                var ci = acStart >= 0 ? chatAtt.IndexOf("Clipboard.GetImage()", acStart, StringComparison.Ordinal) : -1;
                var tryPos = acStart >= 0 ? chatAtt.IndexOf("try", acStart, StringComparison.Ordinal) : -1;
                Assert(acStart >= 0 && ci > 0 && tryPos > 0 && tryPos < ci, "★ Clipboard.GetImage() 在 try 内(修:此前在 try 外直接闪退)");
                Assert(chatAtt.Contains("打不开文件选择框"), "文件选择对话框异常被兜住并提示");
                // ★ 移除附件按【对象】而非捕获下标(事件日志实锤:RemoveAt(旧idx) 越界 = "添加附件闪退")
                var psStart = chatAtt.IndexOf("FrameworkElement PendingStrip()", StringComparison.Ordinal);
                var psStop = chatAtt.IndexOf("FrameworkElement AttachChip", psStart, StringComparison.Ordinal);
                var psText = chatAtt[psStart..psStop];
                Assert(psText.Contains("_pending.Remove(item)") && !psText.Contains("RemoveAt"),
                       "★ 移除待发附件按对象移除,不按捕获下标(修越界闪退)");
            }
            var puFolder = TryReadSource(Path.Combine("Views", "ProjectUi.cs"));
            if (puFolder is not null)
                Assert(puFolder.Contains("打不开文件夹选择框"), "附件文件夹对话框异常被兜住并提示");
            var appSrc2 = TryReadSource("App.xaml.cs");
            if (appSrc2 is not null)
            {
                Assert(appSrc2.Contains("PurgeDemoDataOnce();") && !appSrc2.Contains("if (!hadStore) SeedDemoTasks()"),
                       "★ 示例数据已停止播种并一次性清掉(用户要求 2026-07-31:真数据能从 Apple 拉来了,示例混在里面分不清真假)");
                Assert(appSrc2.Contains("save-client-stores"), "退出前把未落盘的改动存下来");
                Assert(appSrc2.Contains("_saveDebounce"), "变更防抖后落盘(一次操作不写多次)");
            }

            // ★ 只有项目抽屉能开完成宽限;主页/会话菜单不能(它们没有巡检表,会滞留)
            var pkGrace = TryReadSource(Path.Combine("Views", "ProjectPickerView.cs"));
            if (pkGrace is not null)
                Assert(pkGrace.Contains("includeJustCompleted: true"), "项目抽屉显式开启完成宽限(它有巡检表)");
            foreach (var f in new[] { Path.Combine("Views", "HomeView.cs"), Path.Combine("Views", "ChatView.cs") })
            {
                var src = TryReadSource(f);
                if (src is null) continue;
                Assert(!src.Contains("includeJustCompleted"), $"{Path.GetFileName(f)} 不开完成宽限(没有巡检表,会滞留已完成项目)");
            }
            // ★ 并入设置页的视图不能自带 ScrollViewer(外层 Ui.Page 已是 ScrollViewer,套两层会吃掉滚轮)
            var svScroll = TryReadSource(Path.Combine("Views", "StorageView.cs"));
            if (svScroll is not null)
                Assert(!svScroll.Contains("new ScrollViewer"), "★ StorageView 不自带滚动(并入设置页,避免嵌套滚动吃滚轮)");

            // ★ 整理阈值必须【真的被用到】—— 否则设置里就是个不做事的摆设
            {
                var szc = new Services.ChatCenter();
                var ss = szc.NewSession(null, "chat");
                szc.Send(ss.SessionId, new string('x', 500));
                Assert(szc.SizeOf(ss.SessionId) >= 500, "会话体量可计算(字符数估算,接入后换 token)");
                Assert(szc.SizeOf("no-such") == 0, "不存在的会话体量为 0(不抛)");
            }
            var cvThr = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvThr is not null)
                Assert(cvThr.Contains("SummaryThresholdChars") && cvThr.Contains("这条会话已经很长"),
                       "★ 整理阈值真的用上了(超长会话会建议另开一条),不是个空设置");

            // ---- 主机离线 / 一网多主机 ----
            var puFolder2 = TryReadSource(Path.Combine("Views", "ProjectUi.cs"));
            if (puFolder2 is not null)
            {
                Assert(puFolder2.Contains("OpenFolderItem") && puFolder2.Contains("HostMachine"),
                       "★ 打开文件夹尊重 HostMachine(远程项目不会误开本机同名目录)");
                Assert(puFolder2.Contains("文件夹在其它机器上"), "远程文件夹如实拒绝并说明");
                // 只能有一个实现(三处菜单共用),否则改一处漏两处
                Assert(puFolder2.Split("ProjectCenter.OpenInExplorer(p.FolderPath)").Length - 1 == 1,
                       "打开文件夹只有一处实现(三个菜单共用)");
            }
            var cvOff = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvOff is not null)
                Assert(cvOff.Contains("主机未开启") && cvOff.Contains("HubState.Online"),
                       "★ 主机离线时如实提示(消息先记本机,AI 要等主机上线)");
            var dvHub = TryReadSource(Path.Combine("Views", "DevicesView.cs"));
            if (dvHub is not null)
                Assert(dvHub.Contains("已连主机") && dvHub.Contains("只属于一个主机"),
                       "★ 显示 hub_id 并说明一客户端只属于一个主机(一网多主机时分得清)");

            // ---- 提升为共享:默认本机、单向不可收回、幽灵永不共享、任何机器都能删 ----
            {
                var sc = new Services.ChatCenter();
                var loc = sc.NewSession(null, "chat");
                Assert(!loc.Shared, "★ 会话默认【只在本机】(每台设备独立列表)");
                Assert(Services.ChatCenter.CanShare(loc), "普通会话可提升为共享");
                Assert(sc.ShareSession(loc.SessionId), "提升成功");
                Assert(sc.Find(loc.SessionId)!.Shared, "提升后标记为共享");
                Assert(!Services.ChatCenter.CanShare(sc.Find(loc.SessionId)!), "★ 已共享的不再给【提升】(单向)");
                // ★ 不可收回:整个 ChatCenter 不应存在任何"取消共享"的入口
                var ccSrc = TryReadSource(Path.Combine("Services", "ChatCenter.cs"));
                if (ccSrc is not null)
                    Assert(!ccSrc.Contains("Unshare") && !ccSrc.Contains("取消共享"), "★ 没有取消共享的接口(用户裁定:不可收回)");

                var gh = sc.NewGhostSession("chat");
                Assert(!Services.ChatCenter.CanShare(gh) && !sc.ShareSession(gh.SessionId), "★ 幽灵会话永远不能共享(它的定义就是不留记录)");

                // 任何机器都能删共享会话 -> 删除照常走软删除
                sc.Delete(loc.SessionId);
                Assert(sc.Deleted("chat").Any(x => x.SessionId == loc.SessionId), "共享会话可被删除(任何机器都能删),进已删除");

                var pc5 = new Services.ProjectCenter();
                var pl = pc5.Create("本机项目", Path.Combine(Path.GetTempPath(), "sh-" + Guid.NewGuid().ToString("N")[..6]), null, Services.ProjectScope.Personal);
                Assert(!pl.Shared, "★ 项目默认【只在本机】");
                Assert(pc5.ShareProject(pl.ProjectId) && pc5.Find(pl.ProjectId)!.Shared, "项目可提升为共享");
                Assert(!Services.ProjectCenter.CanShare(pc5.Find(pl.ProjectId)!), "项目共享同样单向");
            }
            var cvShare = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvShare is not null)
            {
                Assert(cvShare.Contains("提升为共享") && cvShare.Contains("无法收回"), "★ 提升确认框写明【不可收回】");
                Assert(cvShare.Contains("条消息)会一起标为共享"), "★ 确认框写明整段历史一起共享(用户裁定 A)");
                // ★★★ 2026-08-05 两处一起更正:
                //  ① 「会话同步还没有做」—— S13 做出来了,这句话早已过期;
                //     而这条断言**一直是绿的**,因为它查的是原始源码,
                //     而那句话如今只存在于一段【注释】里(拿注释当证据,今天第 N 次)。
                //  ② 「只影响这台机器」—— 用户裁定删除共享要同步,这句话当场反过来。
                //  ⇒ 改成钉**当前为真**的那句,并且用 NoComments(只留字符串,去掉注释)。
                Assert(NoComments(cvShare).Contains("删除共享会话")
                       && NoComments(cvShare).Contains("同步到其它设备"),
                       "★★ 删共享会话的确认框必须如实说【会同步到其它设备】—— "
                       + "删除现在会传播,还写「只影响这台机器」就是界面在说假话");
                Assert(cvShare.Contains("· 共享"), "会话行标出共享状态");
                Assert(cvShare.Contains("从来没上传过"), "★ 如实说明现在只是标记、接入后才上传");
            }
            var puShare = TryReadSource(Path.Combine("Views", "ProjectUi.cs"));
            if (puShare is not null)
                Assert(puShare.Contains("提升为共享") && puShare.Contains("文件夹】仍在"),
                       "★ 项目提升说明:共享元数据,文件夹仍在原机");

            // ---- 翻译工作空间:语言池规则 / 语种检测 / 档位格式 / 学习笔记 ----
            {
                // 语种检测:能确定的才给答案,拉丁字母一律交给 AI(不瞎猜)
                Assert(Services.Languages.Detect("今天天气不错") == "zh", "汉字(无假名)-> 中文");
                Assert(Services.Languages.Detect("これは日本語です") == "ja", "★ 有假名 -> 日语(汉字中日共用,假名才是证据)");
                Assert(Services.Languages.Detect("漢字とかな") == "ja", "中日混排里有假名 -> 判日语");
                Assert(Services.Languages.Detect("안녕하세요") == "ko", "谚文 -> 韩语");
                Assert(Services.Languages.Detect("Привет") == "ru", "西里尔 -> 俄语");
                Assert(Services.Languages.Detect("Guten Morgen") is null, "★ 拉丁字母分不清语种 -> 返回 null 交给 AI(不瞎猜)");
                Assert(Services.Languages.Detect("   ") is null, "空白输入不给语种");

                var ts = new Services.TranslationState();
                ts.AddTarget("zh"); ts.AddTarget("ja");
                // 例一:池 = 中/日,输入中文 -> 只翻日语
                var p1 = ts.Plan("你好");
                Assert(p1.InputLang == "zh" && p1.Targets.SequenceEqual(new[] { "ja" }), "池=中/日,输入中文 -> 译成日语");
                var p2 = ts.Plan("こんにちは");
                Assert(p2.Targets.SequenceEqual(new[] { "zh" }), "池=中/日,输入日语 -> 译成中文");

                // 例二:输入池外语种 -> 建议入池,并翻成池内其它语言
                var p3 = ts.Plan("안녕");
                Assert(p3.InputLang == "ko" && p3.AddInputToPool && !p3.PoolFull, "★ 池外语种 -> 建议加进目标池");
                Assert(p3.Targets.OrderBy(x => x).SequenceEqual(new[] { "ja", "zh" }), "同时翻成池内其余语言(中+日)");
                Assert(ts.Targets.Count == 2, "★ Plan 只建议、不偷偷改池(用户看得见时才 Apply)");
                ts.Apply(p3);
                Assert(ts.Targets.Count == 3 && ts.Contains("ko"), "Apply 之后才真的入池");

                // 上限 3:满了不再加,也不擅自替换用户的选择
                Assert(ts.IsFull && !ts.AddTarget("de"), "★ 目标池上限 3,满了加不进去");
                var p4 = ts.Plan("Hallo");   // 拉丁 -> 检测不出
                Assert(p4.NeedsAiDetect && !p4.AddInputToPool, "检测不出语种时不乱入池,标记需 AI 判定");
                ts.RemoveTarget("ko");
                Assert(!ts.Contains("ko") && ts.Targets.Count == 2, "可以把语言移出目标池");

                // 档位 -> 固定格式(学习笔记就按这个存)
                Assert(Services.TranslationLevels.FieldsOf(Services.TranslationLevel.Plain).SequenceEqual(new[] { "译文" }), "精简档只给译文");
                Assert(Services.TranslationLevels.FieldsOf(Services.TranslationLevel.Grammar).Length == 4, "详解档四个字段(译文/读音/例句/逐词)");

                // 学习笔记:按语言分类 + 多语言【拆开存】
                var nc = new Services.NoteCenter();
                nc.AddSplit(new[]
                {
                    new Services.StudyNote("", "ja", "你好", "zh", "こんにちは", Services.TranslationLevel.Reading, Reading: "konnichiwa"),
                    new Services.StudyNote("", "en", "你好", "zh", "Hello", Services.TranslationLevel.Plain),
                });
                Assert(nc.Count("ja") == 1 && nc.Count("en") == 1, "★ 一次多语言翻译按目标语言【拆成多条】分别存");
                Assert(nc.LanguagesUsed().Count() == 2, "笔记按语言分类");
                Assert(nc.Of("ja").First().Reading == "konnichiwa", "带读音的档位把读音存下来");
                var enNote = nc.Of("en").First();
                nc.Update(enNote with { Translation = "Hi" });
                Assert(nc.Of("en").First().Translation == "Hi", "笔记可在格式内编辑");
                nc.Remove(enNote.Id);
                Assert(nc.Count("en") == 0, "笔记可删除");
                // 空译文不进笔记(拆分时跳过)
                Assert(nc.AddSplit(new[] { new Services.StudyNote("", "de", "x", "zh", "", Services.TranslationLevel.Plain) }) == 0,
                       "空译文不会被存成笔记");
            }
            // 音效是【当场合成】的,不带音频素材文件 —— 验它确实是一段合法的 16bit 单声道 WAV
            {
                var wav = Services.Sfx.BuildDrop();
                Assert(wav.Length > 44, "音效合成出了数据");
                Assert(System.Text.Encoding.ASCII.GetString(wav, 0, 4) == "RIFF"
                       && System.Text.Encoding.ASCII.GetString(wav, 8, 4) == "WAVE",
                       "★ 合成的是合法 WAV(不需要任何素材文件,发布仍是单文件)");
                Assert(BitConverter.ToInt16(wav, 22) == 1 && BitConverter.ToInt16(wav, 34) == 16,
                       "单声道 16bit");
                Assert(BitConverter.ToInt32(wav, 40) == wav.Length - 44, "数据块长度与实际字节数一致");
                Assert(Services.Sfx.BuildDrop().SequenceEqual(wav), "★ 每次合成完全一样(固定种子,不要随机音色)");

                // 音效开关:默认开,可关,且【只管声音】—— 关了不该把落地扬尘一起关掉
                var sfxSet = new Services.AppSettings();
                Assert(sfxSet.SoundEffects, "界面音效默认开");
                sfxSet.SoundEffects = false;
                sfxSet.Save();
                Assert(!Services.AppSettings.Load().SoundEffects, "★ 音效开关能存下来(每台设备各自的偏好)");
                sfxSet.SoundEffects = true;
                sfxSet.Save();
            }
            var tbSfx = TryReadSource(Path.Combine("Views", "TranslationBar.cs"));
            if (tbSfx is not null)
            {
                var land = Slice(tbSfx, "void PlayLanding(Point at)", "// 六粒尘");
                Assert(land is not null && land.Contains("if (TheApp.Settings.SoundEffects) Services.Sfx.PlayDrop();"),
                       "★ 音效受设置开关控制");
                Assert(land is not null && !land.Contains("SoundEffects) return"),
                       "★ 关掉音效不影响落地扬尘(动效属于皮肤观感,不是声音)");
            }
            var setSfx = TryReadSource(Path.Combine("Views", "SettingsView.cs"));
            if (setSfx is not null)
                Assert(setSfx.Contains("Content = \"界面音效\"") && setSfx.Contains("s.SoundEffects = false"),
                       "设置里能关界面音效");
            // ---- 边界:目标池里只剩输入语言自己 -> 这次翻译【没有任何目标】----
            // 用户提的:"目标池里只有中文,但我们输入中文会怎么样?"
            {
                var ts = new Services.TranslationState();
                ts.AddTarget("zh");
                var pz = ts.Plan("你好世界");
                Assert(pz.InputLang == "zh", "中文被认出来");
                Assert(pz.Targets.Count == 0, "池里只有中文、输入也是中文 -> 目标集为空");
                Assert(pz.NothingToDo, "★ 这种情况必须能被判出来,而不是发出去翻成空集");

                // 只有日语和英语,输入日语 -> 还剩英语,正常
                var ts2 = new Services.TranslationState();
                ts2.AddTarget("ja");
                ts2.AddTarget("en");
                var planJa = ts2.Plan("こんにちは");
                Assert(planJa.InputLang == "ja" && planJa.Targets.SequenceEqual(new[] { "en" }),
                       "池里有日英、输入日语 -> 只翻成英语");
                Assert(!planJa.NothingToDo, "还有目标就不该被拦");

                // 只有日语,输入日语 -> 空集
                var ts3 = new Services.TranslationState();
                ts3.AddTarget("ja");
                Assert(ts3.Plan("こんにちは").NothingToDo, "池里只有日语、输入也是日语 -> 没有目标");

                // ★ 语种判不出来(拉丁字母)时【不许】拦:那时我们并不知道它是不是池内那一个,
                //   宁可交给 AI 判,也不要凭猜测拦下一次正当的翻译(与 Detect 同一条纪律)。
                var ts4 = new Services.TranslationState();
                ts4.AddTarget("en");
                var pe = ts4.Plan("hello world");
                Assert(pe.NeedsAiDetect && pe.InputLang is null, "拉丁字母不硬猜语种");
                Assert(!pe.NothingToDo, "★ 判不出语种时不拦(不能凭猜测拦下正当的翻译)");

                // 池子空着也算发不出去,但那是另一条(由 CanSend 管)
                Assert(new Services.TranslationState().Plan("你好").Targets.Count == 0, "空池自然没有目标");
            }
            // ---- 目标池算不出目标时的【兜底级联】(用户裁定 2026-07-30)----
            // 母语 -> 英语 -> 在对话里问。四层分支,每层都有"翻给谁""加不加进池"两件事,
            // 所以规则抽成纯函数,这里一条条钉死。
            {
                var pool = new[] { "zh", "ja", "en", "de", "ko" };
                var R = (string[] targets, string? input, string native, string[] cur)
                    => Services.TranslationFallbacks.Resolve(targets, input, native, pool, cur);

                // ① 本来就有目标 -> 不兜底
                Assert(R(new[] { "ja" }, "zh", "zh", new[] { "ja" }).Kind == Services.FallbackKind.None,
                       "有目标就不启动兜底");

                // ② 池里只有中文、输入中文、母语日语 -> 翻成母语(日语),并把日语加进池
                var f1 = R(Array.Empty<string>(), "zh", "ja", new[] { "zh" });
                Assert(f1.Kind == Services.FallbackKind.Native && f1.AddToPool == "ja",
                       "★ 输入=目标 且 母语不同 -> 翻成母语,母语进目标池");

                // ③ 输入=目标=母语(中文) -> 翻成英语,英语进池
                var f2 = R(Array.Empty<string>(), "zh", "zh", new[] { "zh" });
                Assert(f2.Kind == Services.FallbackKind.English && f2.AddToPool == "en",
                       "★ 输入=目标=母语(非英语) -> 翻成英语,英语进目标池");

                // ④ 输入=目标=母语=英语 -> 在对话里问,候选来自语言池、去掉英语与已在目标池的
                var f3 = R(Array.Empty<string>(), "en", "en", new[] { "en" });
                Assert(f3.Kind == Services.FallbackKind.Ask && f3.AddToPool is null,
                       "★ 输入=目标=母语=英语 -> 在对话里问,先不擅自加语言");
                Assert(!f3.Options.Contains("en"), "候选里不该出现英语本身");
                Assert(f3.Options.SequenceEqual(new[] { "zh", "ja", "de", "ko" }),
                       "候选 = 语言池减去英语与已在目标池的");

                // ★ 目标池满 3 个、输入是【第四种】语言 -> 翻成池里那三种(用户裁定)。
                //   池子不动:用户挑的那三个是他自己的选择,不该被这次输入挤掉
                //   (TranslationState.Plan 已把 PoolFull 如实标出来,界面据此说明)。
                var full3 = new Services.TranslationState();
                foreach (var c in new[] { "zh", "ja", "de" }) full3.AddTarget(c);
                var p4 = full3.Plan("안녕하세요");
                Assert(p4.InputLang == "ko", "第四种语言被认出来");
                Assert(p4.Targets.SequenceEqual(new[] { "zh", "ja", "de" }),
                       "★ 池满时输入第四种语言 -> 翻成池里全部三种");
                Assert(p4.PoolFull && !p4.AddInputToPool,
                       "★ 池满则如实标记,不擅自替换用户挑的三个");
                Assert(R(p4.Targets.ToArray(), "ko", "zh", new[] { "zh", "ja", "de" }).Kind == Services.FallbackKind.None,
                       "有三个目标,不该启动兜底");

                // ⑤ 语种没判出来(拉丁字母)-> 一律不兜底,交给 AI 判
                Assert(R(Array.Empty<string>(), null, "zh", new[] { "en" }).Kind == Services.FallbackKind.None,
                       "★ 语种判不出来时不启动兜底(不能凭猜测把翻译发到没人要的语言上)");

                // ⑥ 母语来源:跟着界面语言走,可显式改写
                Assert(Services.Languages.NativeFromUi("zh-CN") == "zh"
                       && Services.Languages.NativeFromUi("en-US") == "en"
                       && Services.Languages.NativeFromUi("ja-JP") == "ja", "母语默认跟界面语言走");
                var st6 = new Services.AppSettings { Language = "en-US" };
                Assert(st6.NativeLang == "en", "没显式设时用界面语言推出来的");
                st6.NativeLangOverride = "de";
                Assert(st6.NativeLang == "de", "★ 设置里显式指定的母语优先");

                // ⑦ 「直接回一句语言名」的解析:认中文名/本地名/语言码/英文名,认不出返回 null
                Assert(Services.Languages.ParseLanguage("日语") == "ja", "认中文名");
                Assert(Services.Languages.ParseLanguage("日本語") == "ja", "认本地名");
                Assert(Services.Languages.ParseLanguage("de") == "de", "认语言码");
                Assert(Services.Languages.ParseLanguage("German") == "de", "认英文名");
                Assert(Services.Languages.ParseLanguage("翻成德语吧") == "de", "认一句话里的语言名");
                Assert(Services.Languages.ParseLanguage("帮我看看这段代码为什么会闪退,顺便解释一下") is null,
                       "★ 一整段正常的话不会被当成在回答语言");
                Assert(Services.Languages.ParseLanguage("") is null && Services.Languages.ParseLanguage(null) is null,
                       "空输入不认");
            }
            // 提问与作答的数据层:答过就定死,不许改
            {
                var adir = Path.Combine(tmp, "choice");
                Directory.CreateDirectory(adir);
                Environment.SetEnvironmentVariable(AppPaths.StateEnvVar, adir);
                var ac = new Services.ChatCenter();
                var asid = ac.NewSession(null, "translation").SessionId;
                var qid = ac.AskChoice(asid, "要翻成什么语言?", new[] { "zh", "ja" });
                Assert(qid is not null, "提问写进了会话");
                Assert(ac.PendingChoice(asid)?.MessageId == qid, "找得到还没答的那条");
                Assert(ac.AnswerChoice(qid!, "ja"), "作答成功");
                Assert(ac.PendingChoice(asid) is null, "★ 答过之后不再是待答状态(按钮该置灰了)");
                Assert(!ac.AnswerChoice(qid!, "zh"), "★ 答过就定死,不许再改");
                Assert(ac.MessagesOf(asid).First(m => m.MessageId == qid).ChoiceAnswer == "ja", "记的是第一次那个答案");
                Environment.SetEnvironmentVariable(AppPaths.StateEnvVar, tmp);
            }
            var cvJump = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvJump is not null)
            {
                Assert(cvJump.Contains("_suppressScrollToEnd = true;") && cvJump.Contains("if (!skipEnd) scroll.ScrollToEnd()"),
                       "★ 从历史跳过去时不再滚到底(否则跳转结果会被 ScrollToEnd 覆盖)");
                Assert(cvJump.Contains("all[i].StableKey != want"),
                       "★ 按消息的稳定标识定位,不按下标(归档来回一次下标就全变了)");
            }
            var hbSrc = TryReadSource(Path.Combine("Views", "HistoryBoardView.cs"));
            if (hbSrc is not null)
            {
                // ★ 一个元素不能同时是两个父节点的子元素 —— 切换筛选时重建整块会当场抛异常
                Assert(!Body(hbSrc).Contains("Content = Ui.Page(Build())"),
                       "★ 切换「只看收藏」不重建整块(重复挂载会抛异常)");
                Assert(hbSrc.Contains("_toggle!.Content = ToggleText();"), "只改按钮文案与列表内容");
            }
            var cvCascade = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvCascade is not null)
            {
                var send = Slice(cvCascade, "void SendCurrent()", "TheApp.Chat.Send(_sessionId, text, atts)");
                Assert(send is not null && send.IndexOf("_answerPending", StringComparison.Ordinal) >= 0
                       && send.IndexOf("_fallback", StringComparison.Ordinal) >= 0
                       && send.IndexOf("_answerPending", StringComparison.Ordinal)
                          < send.IndexOf("_fallback", StringComparison.Ordinal),
                       "★ 先看是不是在回答提问,再走兜底级联(顺序反了会把回答当新输入)");
                var bub = Slice(cvCascade, "if (m.Role == ChatRole.System)", "var user = m.Role == ChatRole.User");
                Assert(bub is not null && bub.Contains("btn.IsEnabled = false"),
                       "★ 答过之后按钮置灰(保留可见,看得出当时问了什么、选了哪个)");
            }

            var cvBlock = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvBlock is not null)
            {
                var send = Slice(cvBlock, "void SendCurrent()", "TheApp.Chat.Send(");
                Assert(send is not null && send.Contains("_blockReason?.Invoke(_draft) is { } why"),
                       "★ 没有可翻目标时不发送,并如实说明原因");
                Assert(send is not null
                       && send.IndexOf("_pending.Clear()", StringComparison.Ordinal) >= 0,
                       "发送前先清待发附件(顺序不能反,见下一条)");
                Assert(cvBlock.Contains("_sendBlockedHint = null;") && cvBlock.Contains("RiskDanger"),
                       "解释在下次成功发送/目标池变化后消失");
            }

            // ---- 待翻译内容的【形态】与它对档位的约束(用户裁定 2026-07-30)----
            {
                var S = Services.TextShapes.Classify;
                Assert(S("apple", false) == Services.TextShape.Word, "单个词 = 单词");
                Assert(S("你好", false) == Services.TextShape.Word, "中文单词");
                Assert(S("今天天气不错,我们出去走走。", false) == Services.TextShape.Phrase, "一两句 = 短句");
                Assert(S("hello world", false) == Services.TextShape.Phrase, "带空格就不是单词了");
                Assert(S(new string('字', 400), false) == Services.TextShape.LongText, "超过阈值 = 长文本");
                Assert(S("第一段" + Environment.NewLine + new string('字', 60), false) == Services.TextShape.FormattedLongText,
                       "★ 有换行的长内容按【带格式】处理(段落是用户自己排的)");
                Assert(S("短", true) == Services.TextShape.FormattedLongText,
                       "★ 来自附件的内容一律按带格式长文本,不看长度 —— 它的结构不该被翻译抹平");

                // 长文本:语法/例句不可用,自动回退到直译;直译/词解照旧
                foreach (var shape in new[] { Services.TextShape.LongText, Services.TextShape.FormattedLongText })
                {
                    Assert(!Services.TextShapes.Allows(shape, Services.TranslationLevel.Grammar), "长文本禁用语法");
                    Assert(!Services.TextShapes.Allows(shape, Services.TranslationLevel.Example), "长文本禁用例句");
                    Assert(Services.TextShapes.Allows(shape, Services.TranslationLevel.Plain), "长文本可直译");
                    Assert(Services.TextShapes.Allows(shape, Services.TranslationLevel.Reading), "长文本可词解");
                    Assert(Services.TextShapes.Effective(Services.TranslationLevel.Grammar, shape) == Services.TranslationLevel.Plain,
                           "★ 长文本 + 语法 -> 自动回退到直译");
                    Assert(Services.TextShapes.Effective(Services.TranslationLevel.Example, shape) == Services.TranslationLevel.Plain,
                           "★ 长文本 + 例句 -> 自动回退到直译");
                    Assert(Services.TextShapes.Effective(Services.TranslationLevel.Reading, shape) == Services.TranslationLevel.Reading,
                           "长文本 + 词解 -> 仍是词解(但含义不同,见字段)");
                }

                // 短内容不受限
                foreach (var lv in new[] { Services.TranslationLevel.Grammar, Services.TranslationLevel.Example })
                    Assert(Services.TextShapes.Effective(lv, Services.TextShape.Phrase) == lv, "短句不限档");

                // ★ 长文本 + 词解 = 译文 + 末尾的重点词表(不是逐词标注)
                var f = Services.TextShapes.FieldsFor(Services.TranslationLevel.Reading, Services.TextShape.LongText, "ja");
                Assert(f.Contains("译文") && f.Any(x => x.Contains("重点词表")),
                       "★ 长文本 + 词解 = 译文 + 末尾单独列出的重点词");
                Assert(!f.Contains("读音"), "长文本下不逐词标读音(那是一堵墙)");
                var f2 = Services.TextShapes.FieldsFor(Services.TranslationLevel.Grammar, Services.TextShape.FormattedLongText, "de");
                Assert(f2.Contains("保留原文结构"), "★ 附件长文本要保留原文的段落结构");
                Assert(!f2.Any(x => x.Contains("语法")), "回退之后不再要求产出语法");
                // 短内容仍走原来那套四档字段
                var f3 = Services.TextShapes.FieldsFor(Services.TranslationLevel.Grammar, Services.TextShape.Word, "ja");
                Assert(f3.Contains("语法") && f3.Contains("例句"), "单词/短句照旧给全套");

                // 界面要如实说明,不闷声改档
                Assert(Services.TextShapes.Explain(Services.TranslationLevel.Grammar, Services.TextShape.LongText) is { Length: > 0 },
                       "★ 回退时如实告诉用户为什么");
                Assert(Services.TextShapes.Explain(Services.TranslationLevel.Grammar, Services.TextShape.Word) is null,
                       "短内容不必解释");
            }
            var tsWire = TryReadSource(Path.Combine("Views", "TranslationBar.cs"));
            if (tsWire is not null)
            {
                Assert(tsWire.Contains("TextShapes.Allows(st.Shape, level)") && tsWire.Contains("label.Opacity = ok ? 1 : 0.35"),
                       "★ 不可用的档【当场灰掉】,不是等按了发送才回退");
                Assert(tsWire.Contains("if (!TextShapes.Allows(TheApp.Translation.Shape, capturedLevel)) return;"),
                       "灰着的档点不动");
                Assert(tsWire.Contains("TextShapes.Explain(lv, st.Shape)"), "长文本下如实说明这次按什么来");
            }
            var cvShape = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvShape is not null)
                Assert(cvShape.Contains("spec.OnDraftChanged?.Invoke") && cvShape.Contains("TextShapes.Classify(draft, hasFileAttachment)"),
                       "★ 形态随输入实时上报(总不能等按了发送才告诉用户)");

            // ---- 翻译工作空间的三个场景 + 同声传译骨架(用户裁定 2026-07-31)----
            {
                var ip = new Services.InterpretState();
                Assert(ip.Mode == Services.TranslationMode.Text, "默认是文字翻译");
                ip.SetMode(Services.TranslationMode.Interpret);
                Assert(ip.Mode == Services.TranslationMode.Interpret, "能切到同传");

                // 固定方向:我说的语言 -> 对方的语言(不是目标池)
                ip.SetMyLang("ja"); ip.SetTheirLang("de");
                Assert(ip.MyLang == "ja" && ip.TheirLang == "de", "语言方向可分别设置");
                ip.SwapLangs();
                Assert(ip.MyLang == "de" && ip.TheirLang == "ja", "★ 对调键把两端换过来(换人说话时最常按)");
                ip.SetMyLang("zzz");
                Assert(ip.MyLang == "de", "认不出的语言码不生效");

                // ★ 语音链路未接入时如实为 false —— 界面据此说明,而不是给个按下没反应的开关
                Assert(!Services.InterpretState.PipelineReady, "★ 语音链路未接入 —— 界面必须如实说,不做假开关");
                Assert(Services.InterpretState.RequiredModels.Length == 3
                       && !Services.InterpretState.RequiredModels.Contains("chat"),
                       "★ 同传的模型清单里没有聊天模型(切进来时由 Broker 卸掉)");

                // ★★ "用合成语音取代我的麦克风"不许被存档自动恢复 ——
                //   每次开会都该由用户当场确认一次。
                ip.SetSpeakTranslation(true);
                ip.SetSubtitles(false);
                var snap = ip.Export();
                var ip2 = new Services.InterpretState();
                ip2.Import(snap);
                Assert(ip2.MyLang == ip.MyLang && ip2.TheirLang == ip.TheirLang, "语言方向会被记住");
                Assert(!ip2.Subtitles, "字幕开关会被记住");
                Assert(!ip2.SpeakTranslation,
                       "★ 我方译文语音【不】自动恢复 —— 不能因为上次开着就自动接管你的麦克风");
            }
            // ★ 三套皮肤各画各的图标 —— 新加一个图标只加了一套,换肤时那里就会空着。
            //   这条把三套都得有钉死,不用等换肤时肉眼发现。
            {
                var noIcon = new List<string>();
                foreach (Theme.IconName ic in Enum.GetValues(typeof(Theme.IconName)))
                    foreach (var skin in new[] { Services.Skin.Breeze, Services.Skin.Ink, Services.Skin.Warm })
                        if (string.IsNullOrWhiteSpace(Theme.Icons.PathFor(ic, skin))) noIcon.Add($"{ic}/{skin}");
                Assert(noIcon.Count == 0, "★ 每个图标在三套皮肤里都有画" + (noIcon.Count > 0 ? " 缺:" + string.Join(",", noIcon) : ""));
            }
            // ---- 虚拟声卡:自动下载的三条硬规则(用户确认:自动 ≠ 不透明)----
            {
                var tmpPkg = Path.Combine(tmp, "pkg.bin");
                File.WriteAllText(tmpPkg, "hello");
                var real = Services.AudioDriver.Sha256Of(tmpPkg);
                Assert(real.Length == 64 && real == real.ToLowerInvariant(), "哈希是小写十六进制的 SHA-256");
                Assert(Services.AudioDriver.Verify(tmpPkg, real), "哈希一致就通过");
                Assert(Services.AudioDriver.Verify(tmpPkg, real.ToUpperInvariant()), "大小写不敏感");
                Assert(!Services.AudioDriver.Verify(tmpPkg, new string('0', 64)), "★ 哈希不一致一律拒绝");
                Assert(!Services.AudioDriver.Verify(tmpPkg, ""), "★ 没有哈希 = 不许放行(空哈希不是\"跳过校验\")");
                Assert(!Services.AudioDriver.Verify(tmpPkg, "   "), "空白哈希同样拒绝");

                // ★ 清单三要素缺一不可 —— 缺了就不许自动下载,退回离线安装
                // ★ Authenticode 签名验证(用户裁定的信任模型)—— 实跑一次 WinVerifyTrust 互操作,
                //   确认 P/Invoke 编组不崩、逻辑对得上。负路径是确定的:
                var noSuch = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                 "localai-nope-" + Guid.NewGuid().ToString("N") + ".exe");
                Assert(!Services.Authenticode.VerifySignedByVbAudio(noSuch, out var acSig1)
                       && acSig1.Contains("文件不存在"),
                       "★ 不存在的文件 -> 拒绝(不因此崩)");
                try
                {
                    var unsigned = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                     "localai-sigtest-" + Guid.NewGuid().ToString("N")[..6] + ".exe");
                    System.IO.File.WriteAllBytes(unsigned, new byte[] { 0x4D, 0x5A, 0, 0 });   // 不是真 exe,但足够验"没签名"
                    var ok = Services.Authenticode.VerifySignedByVbAudio(unsigned, out var acSig2);
                    System.IO.File.Delete(unsigned);
                    Assert(!ok, "★ 没有有效签名的文件 -> 拒绝(WinVerifyTrust 互操作跑通、不崩)");
                }
                catch (Exception ex) { Assert(false, "Authenticode 互操作抛异常: " + ex.Message); }

                Assert(!Services.AudioDriverManifest.IsUsable(null), "没有清单 = 不可用");
                // ★ 信任模型改为 Authenticode 签名(用户裁定 2026-07-31):哈希退为可选,空哈希也能下载,
                //   真正的把关在提权运行前的签名验证(见 Authenticode + RunInstaller)。
                Assert(Services.AudioDriverManifest.IsUsable(
                           new Services.AudioDriverPackage("1.0", "https://download.vb-audio.com/y", "", 1, DateTime.Now)),
                       "★ 官方域 + 版本齐全即可下载(哈希可选,把关交给 Authenticode 签名)");
                Assert(!Services.AudioDriverManifest.IsUsable(
                           new Services.AudioDriverPackage("", "https://download.vb-audio.com/y", new string('a', 64), 1, DateTime.Now)),
                       "清单没有版本 = 不可用");
                Assert(Services.AudioDriverManifest.IsUsable(
                           new Services.AudioDriverPackage("1.0", "https://download.vb-audio.com/y", new string('a', 64), 1, DateTime.Now)),
                       "三要素齐全 + 官方域才可用");
                // ★ 下载来源白名单(2026-07-31 审计):用户自备清单能换版本/换官方镜像,但不能换信任来源。
                Assert(!Services.AudioDriverManifest.IsUsable(
                           new Services.AudioDriverPackage("1.0", "https://evil.example.com/x.zip", new string('a', 64), 1, DateTime.Now)),
                       "★ 非 VB-Audio 官方域的下载地址一律不可用(挡本地清单被改成任意 URL + 自证哈希)");
                Assert(!Services.AudioDriverManifest.IsUsable(
                           new Services.AudioDriverPackage("1.0", "http://download.vb-audio.com/y", new string('a', 64), 1, DateTime.Now)),
                       "★ 明文 http 也不认(只走 https)");

                // ★ 内置清单现在【可用】(官方域 + 版本),哈希留空 —— 把关交给 Authenticode 签名。
                Assert(Services.AudioDriverManifest.Current is not null
                       && Services.AudioDriverManifest.Current.Url.StartsWith("https://download.vb-audio.com"),
                       "★ 内置清单可用且下载来源锁死在 VB-Audio 官方域");

            }
            var adSrc = TryReadSource(Path.Combine("Services", "AudioDriver.cs"));
            if (adSrc is not null)
            {
                var dl = Slice(adSrc, "DownloadAsync(", "public static bool RunInstaller");
                Assert(dl is not null && dl.Contains("File.Delete(target)") && dl.Contains("哈希校验失败"),
                       "★ 校验不过的下载文件【当场销毁】,不留在盘上等人误点");
                Assert(adSrc.Contains("Verb = \"runas\""), "安装时另起提权子进程(主程序按 D46 不提权)");
                Assert(adSrc.Contains("OfflineDir"), "★ 支持自备安装包 —— 完全断网的机器也要能装上");
            }
            var sdSrc = TryReadSource(Path.Combine("Views", "SettingsView.cs"));
            if (sdSrc is not null)
            {
                Assert(sdSrc.Contains("声音驱动(同声传译)"),
                       "设置里有声音驱动板块");
                Assert(!sdSrc.Contains("CheckDriverUpdate") && !sdSrc.Contains("Ui.Secondary(\"检查更新\""),
                       "★「检查更新」不再是单独按钮");
                Assert(sdSrc.Contains("正在检查 VB-Audio 官方最新版本") && sdSrc.Contains("ResolveLatest("),
                       "★ 检查【并入】更新流程:点更新先查最新版、显示出来,再下载安装(用户裁定 2026-07-31)");
                Assert(sdSrc.Contains("CopyablePath(\"安装位置\"") && sdSrc.Contains("IsReadOnly = true"),
                       "★ 显示一行【只读、可复制】的安装位置(位置由 VB-CABLE 决定,改不了 -> 只读)");
                Assert(sdSrc.Contains("st.Installed ? st.InstallLocation : null,") && !sdSrc.Contains("if (st.Installed && st.InstallLocation"),
                       "★ 安装位置【常态显示】做成固定槽位 —— 装好后不再突然多一行挤开排版(用户裁定 2026-07-31)");
                Assert(sdSrc.Contains("\"重新下载\"") && !sdSrc.Contains("if (!st.Installed || pkg is not null)"),
                       "★ 安装/更新按钮【常显】—— 删掉本地安装包后不蒸发,标「重新下载」(用户裁定 2026-07-31)");
                Assert(sdSrc.Contains("w.Activated += OnActivatedRefreshDriver") && sdSrc.Contains("w.Activated -= OnActivatedRefreshDriver"),
                       "★★ 事件驱动刷新:切回本应用(Window.Activated)时刷一次,不轮询 Detect(用户裁定:定时检测太不性能友好)");
                Assert(sdSrc.Contains("第三方内核驱动"),
                       "★ 如实写明这是第三方驱动 ——\"察觉不到它的存在\"指的是不用你操作,不是不告诉你");
                var inst = Slice(sdSrc, "void InstallDriver()", "async void DownloadThenInstall");
                Assert(inst is not null && inst.Contains("已拒绝运行"),
                       "★ 自备的安装包同样要过校验");
                Assert(sdSrc.Contains("var hits = AudioDriver.FindUninstallers();")
                       && sdSrc.Contains("hits.Count > 1"),
                       "★ 一键卸载先查后问:多个候选就交给用户自己选(不提权启动错的卸载程序)");
                var adSrc2 = TryReadSource(Path.Combine("Services", "AudioDriver.cs"));
                if (adSrc2 is not null)
                {
                    Assert(adSrc2.Contains("publisher.Contains(\"VB-Audio\"") && adSrc2.Contains("display.Contains(\"CABLE\""),
                           "★ 卸载匹配收紧:Publisher=VB-Audio 且名字含 CABLE(不再把兄弟产品拉进来)");
                    Assert(adSrc2.Contains("ExtractToDirectory"),
                           "★ 传进来是 .zip 先解包再找安装程序(官方发的是 zip)");
                    Assert(adSrc2.Contains("static string SafeVer"),
                           "★ pkg.Version 拼进路径前先清洗(挡路径穿越)");
                    Assert(adSrc2.Contains("Authenticode.VerifySignedByVbAudio(packagePath, out var signer)"),
                           "★★ 提权运行安装程序前必须过 Authenticode 签名(用户裁定:信任模型 = 官方签名)");
                    Assert(adSrc2.Contains("foreach (var e in FindUninstallers())"),
                           "★ Detect 以注册表卸载项为主判据(装好后 .sys 落在 DriverStore、可能待重启才进 drivers,靠文件位置会漏报)");
                    Assert(adSrc2.Contains("没有注册表卸载项 = 未安装"),
                           "★★ 没有注册表项就是未安装 —— 绝不拿 DriverStore 缓存 .sys 兜底(卸载后它仍在,会报“卸了也已安装”)");
                    Assert(adSrc2.Contains("ResolveLatestUrl(http, pkg.Url)"),
                           "★ 下载前动态解析最新 PackNN(通用名会 404 —— 用户反馈的“下载失败”成因)");
                    Assert(adSrc2.Contains("!string.IsNullOrWhiteSpace(pkg.Sha256) && !Verify(target"),
                           "★ 哈希退为可选:填了才比对(把关交给签名)");
                    Assert(adSrc2.Contains("Path.Combine(DownloadsDir(), \"LocalAI-VBCABLE\")"),
                           "★ 安装包默认下到【下载】文件夹的专属子目录(用户裁定 2026-07-31)");
                    Assert(adSrc2.Contains("Contains(\"cable\", StringComparison.OrdinalIgnoreCase)")
                           && adSrc2.Contains("public static string? FindOfflinePackage()"),
                           "★ 自备包扫描只认名字含 cable 的(下载夹里别的 exe/zip 不会被当安装包)");
                    Assert(adSrc2.Contains("InstallFolderOf(e.Command)") && adSrc2.Contains("InstallLocation"),
                           "★ Detect 带出安装位置(注册表 InstallLocation,退回卸载程序目录)");
                }
                var acSrc = TryReadSource(Path.Combine("Services", "Authenticode.cs"));
                if (acSrc is not null)
                {
                    Assert(acSrc.Contains("WinVerifyTrust") && acSrc.Contains("WINTRUST_ACTION_GENERIC_VERIFY_V2"),
                           "★ 用 WinVerifyTrust 验签名有效 + 证书链通到受信任根");
                    Assert(acSrc.Contains("\"BUREL VINCENT\"") && acSrc.Contains("AcceptedSigners"),
                           "★ 签名者主体须是 VB-Audio 已知签名身份 —— 实测真安装包由 BUREL VINCENT(Vincent Burel)签发,只认 \"VB-Audio\" 会把真包拒之门外");
                    Assert(acSrc.Contains("WTD_STATEACTION_CLOSE"),
                           "★ WinVerifyTrust 验完再 CLOSE 释放内部状态(不泄漏)");
                }
                Assert(TryReadSource(Path.Combine("Services", "AudioDevices.cs"))?.Contains("PropVariantClear") == true,
                       "★ 枚举设备时 PROPVARIANT / RCW 都释放(高频刷新路径,不能泄)");
            }

            // ---- 示例同传记录(用户要求:放到普通会话列表里可测) ----
            {
                var demoSrc = TryReadSource("App.xaml.cs");
                if (demoSrc is not null)
                {
                    Assert(demoSrc.Contains("void SeedDemoInterpret()") && demoSrc.Contains("interpret: true"),
                           "★ 有一段示例同传记录,和普通会话排在同一个列表里");
                    Assert(demoSrc.Contains("(示例)同传记录"),
                           "★ 示例标着「(示例)」—— 语音链路还没接入,不能让它看起来像真转写");
                    Assert(demoSrc.Contains("var hadInterpret = Settings.InterpretDemoSeeded;")
                           && demoSrc.Contains("Settings.InterpretDemoSeeded = true;"),
                           "★ 判据是【播没播过】而不是【列表里有没有】—— 删除是软删除,拿“有没有”反推会让它删一次长回来一条");
                }
            }

            // ---- 同传会话:进普通会话列表、带标记、不可搬走、点开自动切界面 ----
            {
                var idir = Path.Combine(tmp, "interp-sess");
                Directory.CreateDirectory(idir);
                Environment.SetEnvironmentVariable(AppPaths.StateEnvVar, idir);
                var ic = new Services.ChatCenter();
                var plain = ic.NewSession(null, "translation");
                var simul = ic.NewSession(null, "translation", interpret: true);
                Assert(!plain.Interpret && simul.Interpret, "同传会话有自己的标记");
                Assert(ic.NormalSessions("translation").Any(x => x.SessionId == simul.SessionId),
                       "★ 同传记录和普通会话排在【同一个列表】里(用户裁定)");
                Assert(Services.ChatCenter.CanMove(plain) && !Services.ChatCenter.CanMove(simul),
                       "★ 同传记录不能搬到项目/别的工作空间 —— 它只有在同传界面里才讲得通");
                Assert(simul.ProjectId is null, "同传记录不挂项目");
                Environment.SetEnvironmentVariable(AppPaths.StateEnvVar, tmp);
            }
            var cvIS = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvIS is not null)
            {
                Assert(cvIS.Contains("TheApp.Interpret.SetMode(s.Interpret ? TranslationMode.Interpret")
                       && cvIS.Contains(": s.FileTrans ? TranslationMode.FileTrans"),
                       "★ 点开会话就把界面切到它自己那套模块 —— 【两个方向都切】(在同传里点开文字会话要切回文字翻译,否则那条会话根本没被打开)");
                Assert(cvIS.Contains("ApplySessionScene(s);") && cvIS.Contains("ApplySessionScene(TheApp.Chat.Find(sessionId));"),
                       "列表点开与深链打开走同一条切场景的路(别一处切一处不切)");
                Assert(cvIS.Contains("if (movable) m.Items.Add(move)") && cvIS.Contains("if (movable) m.Items.Add(toWs)"),
                       "★ 不能搬的会话:菜单里【根本不出现】那两项,而不是点了再报错");
                Assert(cvIS.Contains("s.Interpret ? IconName.Mic : s.FileTrans ? IconName.File : s.I18nTable ? IconName.Extensions : IconName.Mail"),
                       "同传/文件翻译/JSON译表会话在列表里各用自己的图标区分(列表窄,一个图标够用)");
                // 顶部场景切换只要图标 + 透明命中块
                var modeSw = Slice(cvIS, "FrameworkElement ModeSwitcher()", "return row;");
                Assert(modeSw is not null && modeSw.Contains("Background = Brushes.Transparent") && modeSw.Contains("IsHitTestVisible = false"),
                       "★ 命中区是透明方块,图标本身不接事件(自绘图标是描边,点笔画空隙会点不中)");
            }

            {
                // ★ 语言方向初始是【空坑】—— 不预设一个看似合理的默认:
                //   方向恰恰是同传里最不能猜错的东西,猜错就是整场翻反。
                var fresh = new Services.InterpretState();
                Assert(fresh.MyLang.Length == 0 && fresh.TheirLang.Length == 0, "★ 语言方向初始为空坑");
                Assert(!fresh.DirectionReady, "两端没设好就不算就绪");
                Assert(fresh.WhyCannotStart().Contains("语言方向"), "★ 没设方向时如实说要先设方向");
                fresh.SetMode(Services.TranslationMode.Interpret);
                Assert(fresh.Start(null).Contains("语言方向") && !fresh.Running,
                       "★ 方向没填就开始不了 —— 而且【什么都没变】(不留一个半开的状态)");
                fresh.SetMode(Services.TranslationMode.Text);
                fresh.SetMyLang("zh"); fresh.SetTheirLang("en");
                Assert(fresh.DirectionReady, "两端设好了才就绪");

                // ---- 一场同传的开始与结束(用户裁定 2026-08-02:进页面不自动开始,要有边界感)----
                Assert(!fresh.Running, "★ 进同传页面【不算开始】—— 看看和开会必须是两件事");
                Assert(fresh.Start("sess-0").Contains("同声传译") && !fresh.Running,
                       "★ 文字模式下开始不了 —— 否则会造出一个界面上看不见的进行中");
                fresh.SetMode(Services.TranslationMode.Interpret);
                Assert(fresh.Start("sess-1").Length == 0 && fresh.Running && fresh.RunningSessionId == "sess-1",
                       "按【开始同传】才开始:标记进行中并挂上这一场的会话");
                Assert(fresh.StartedAt is not null, "记下开始时刻(界面要显示这一场从几点开始)");
                fresh.ReportLatency(3.2);
                fresh.Stop();
                Assert(!fresh.Running && fresh.StartedAt is null && fresh.RunningSessionId is null,
                       "结束后回到未开始态(会话记录由 ChatCenter 保留,这里只清【正在进行】)");
                Assert(fresh.LatencySeconds is null, "★ 结束后延迟读数清掉 —— 没在跑就不该挂着上一场的数");
                fresh.SetMode(Services.TranslationMode.Interpret);
                fresh.Start("sess-2");
                fresh.SetMode(Services.TranslationMode.Text);
                Assert(!fresh.Running, "★ 离开同传界面 = 这一场结束(不留一个看不见的进行中)");
                fresh.SetMode(Services.TranslationMode.Interpret);
                Assert(!fresh.Running, "切回来也不会自己复活 —— 开始永远是显式的");
                // 快照不带进行态:重启之后不该还"在进行中"
                Assert(!typeof(Services.InterpretState.Snapshot).GetProperties().Any(x => x.Name == "Running"),
                       "★ Running 不进存档 —— 重启后不该还标着进行中");
                // 拖一次就记住,下次沿用
                var reopened = new Services.InterpretState();
                reopened.Import(fresh.Export());
                Assert(reopened.MyLang == "zh" && reopened.TheirLang == "en", "★ 语言方向沿用上次退出时的设定");
            }
            var tsSrc = TryReadSource(Path.Combine("Views", "ToggleSwitch.cs"));
            if (tsSrc is not null)
            {
                Assert(tsSrc.Contains("DoubleAnimation") && tsSrc.Contains("TranslateTransform.YProperty"),
                       "★ 开关是【滑】过去的,不是硬跳");
                Assert(tsSrc.Contains("if (_enabled) Set("), "禁用时拨不动");
                Assert(tsSrc.Contains("SetResourceReference"), "配色走令牌 —— 为将来的皮肤预留");
                Assert(tsSrc.Contains("_track.Background = Brushes.Transparent"),
                       "整根槽都是命中区(圆钮才 22px,只让它可点会经常按空)");
            }
            var ipPanel = TryReadSource(Path.Combine("Views", "InterpretPanel.cs"));
            if (ipPanel is not null)
                Assert(!Body(ipPanel).Contains("去安装"),
                       "★ 装驱动的入口只在同传设置那一格 —— 同一件事不给两个入口");

            var ipSrc = TryReadSource(Path.Combine("Views", "InterpretPanel.cs"));
            if (ipSrc is not null)
            {
                // ★ 音量条挪到同传设置那一格:会议中要盯的是内容,仪表不该常占会话版面
                Assert(!Body(ipSrc).Contains("MeterColumn"), "★ 主会话板块不再放音量条");
                // ★ 字幕先在底部横条逐字长出来,成句才飞进气泡 ——
                //   没定稿的文字不许直接写进对话记录,否则记录里会出现一句从没说过的话。
                Assert(ipSrc.Contains("void AppendSubtitle(") && ipSrc.Contains("void CommitSubtitle("),
                       "★ 字幕逐字生成 / 成句定稿是两件事,分开两个入口");
                var commit = Slice(ipSrc, "public void CommitSubtitle(", "_subtitle.Text = \"\";");
                Assert(commit is not null && commit.Contains("TranslateTransform") && commit.Contains("DoubleAnimation"),
                       "★ 成句之后【动画飞到上方气泡】—— 那是\"这句从此不再变了\"的可见交代");
                Assert(!Body(ipSrc).Contains("Random"), "不画会动的假电平(那正好会骗过\"声音还在流动吗\"这个问题)");
            }
            var cvMode = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvMode is not null)
            {
                Assert(cvMode.Contains("FrameworkElement ModeSwitcher()") && cvMode.Contains("ModeSwitch = true"),
                       "★ 会话板块左上角有三个场景的入口(位置由用户指定)");
                Assert(cvMode.Contains("new InterpretPanel(interpSid)") && cvMode.Contains("Find(isid)?.Interpret == true")
                       && cvMode.Contains("ReservedScenePlaceholder()"),
                       "★ 只有【真同传会话】才把转写交给 InterpretPanel(fail-closed)—— 否则普通翻译会话的系统说明会被当成对方的话渲染;第三个场景如实说\"还没定\"");
            }
            var barMode = TryReadSource(Path.Combine("Views", "TranslationBar.cs"));
            if (barMode is not null)
            {
                Assert(barMode.Contains("FrameworkElement DirectionCard()") && barMode.Contains("语言方向"),
                       "★ 同传模式下,下半条换成【语言方向】而不是目标池");
                Assert(barMode.Contains("Hit(_mySlot, e)") && barMode.Contains("Hit(_theirSlot, e)"),
                       "★ 方向是从语言池【拖进坑里】的,不是下拉菜单选");
                Assert(!Body(barMode).Contains("ComboBox _myLang"), "下拉菜单已撤掉");
                Assert(barMode.Contains("FrameworkElement InterpretSettingsCard()") && barMode.Contains("同传设置"),
                       "★ 同传模式右边那格是【同传设置】,不是翻译历史");
                Assert(barMode.Contains("\"同传设置\", action: _latency, scroll: false, badge: _driverBadge"),
                       "★ 同传设置不给滚动条;状态灯紧跟标题,最右边留给延迟读数");
                Assert(barMode.Contains("我方译文语音不可用"),
                       "★ 没装虚拟声卡时,右上角直接说【为什么用不了】,而不是显示一个没意义的延迟");
                Assert(barMode.Contains("new ToggleSwitch(\"我方译文语音\"") && barMode.Contains("new ToggleSwitch(\"对方实时字幕\""),
                       "两个开关名字等长(各六字),排在一起不长短不齐");
                Assert(barMode.Contains("DevicePicker(\"我方麦克风\"") && barMode.Contains("DevicePicker(\"音频输出\""),
                       "★ 开关右边那半边放输入/输出设备选择(原来是空的)");
                Assert(barMode.Contains("LevelColumn(\"对方\")") && barMode.Contains("LevelColumn(\"我方\")"),
                       "★ 两条竖直音量挪到同传设置的最左边,开关相应右移");
                // ---- 对方那侧只出字幕、不合成语音(用户裁定 2026-07-31) ----
                Assert(!Body(barMode).Contains("ToggleSwitch(\"实时翻译输出\""),
                       "★ 不再有“把对方的话也同传成语音”这回事 —— 对方原声一直在响,再叠一层机器声等于两个人同时说话");
                Assert(barMode.Contains("PlacementMode.Top") && barMode.Contains("PART_Popup"),
                       "★ 两个设备选择器【上拉】—— 它们坐在窗口最底下,往下弹会被窗口边缘裁掉");
                Assert(barMode.Contains("compact: true"),
                       "★ 开关用紧凑档 —— 窗口缩到最小时别把右边的设备选择挤出去(那一格不给滚动条)");
                var badge = Slice(barMode, "_driverBadge.Children.Clear();", "_switchRow.Children.Clear();");
                Assert(badge is not null && badge.Contains("RiskDanger") && badge.Contains("RiskWarning") && badge.Contains("RiskSafe"),
                       "★ 声卡状态是红/黄/绿三态灯");
                Assert(badge is not null && badge.Contains("VB-CABLE 声卡驱动状态:"),
                       "★ 状态要【写全】—— 光一个彩点看不出它在说什么;点只负责一眼可扫,不承担表意");
                Assert(badge is not null && badge.Contains("去设置"),
                       "红 = 去设置(绿的时候直接显示版本号,没有按钮)");
                // ★ 「一键开启」chip 已删(2026-08-02):开始同传的入口只有设置卡里那颗按钮。
                //   同一件事不给两个入口 —— 何况那个 chip 的出现条件让人根本猜不到它就是"开始"。
                Assert(badge is not null && !badge.Contains("Chip(\"一键开启\""),
                       "★ 不再有第二个【开始】入口(原来那个藏在状态灯旁边的 chip)");
                Assert(barMode.Contains("new ToggleSwitch(\"我方译文语音\"") && barMode.Contains("enabled: drv.Installed && InterpretState.PipelineReady"),
                       "★ 没装虚拟声卡时,语音输出开关灰掉禁用(译文根本送不进会议,能拨就是骗人)");
                // ---- 「开始同传」按钮(2026-08-02)----
                // ★ 位置改到【最左】(用户裁定 2026-08-02 第二次,推翻同日第一次的"字幕右边"):
                //   它是这一格唯一的主动作,排在开关行末尾的话,窄窗口下第一个被裁掉的就是它。
                Assert(barMode.Contains("FrameworkElement StartStopButton(") && barMode.Contains("_startHost.Content = StartStopButton(st);"),
                       "★ 开始/结束按钮有自己的宿主、排在整行【最前】—— 主动作永远不许被裁掉");
                Assert(barMode.Contains("readonly WrapPanel _switchRow"),
                       "★ 开关行是 WrapPanel:窄窗口【换行】而不是静默裁掉末尾的控件");
                Assert(barMode.Contains("interpret: true") && barMode.Contains("TheApp.Interpret.Start(sess.SessionId)"),
                       "★ 开始 = 真的建一条同传会话(右侧列表当场多出一条),不是只改个布尔");
                Assert(barMode.Contains("同传 · {mine}↔{theirs}"),
                       "会话标题带方向与时刻 —— 一场会开完,列表里要认得出这是哪一场");
                // ★ 用户改主意(2026-08-02,撤销同日"没开始全灰"):设置随时可点,
                //   边界感由「开始/结束」承担 —— 字幕开关与设备列不再看 Running。
                Assert(!barMode.Contains("enabled: st.Running") && !barMode.Contains("_deviceCol.IsEnabled = st.Running"),
                       "★ 设置随时可点(没开始也能调字幕/设备)—— 只有「我方译文语音」按假开关纪律灰");
                Assert(barMode.Contains("body.Children.Add(_deviceCol);") && barMode.Contains("_narrowPickerW = Math.Clamp("),
                       "★ 设备行【静态】占一整行、下拉按行宽均分 —— 不在布局途中换 Dock(时序脆,会画出换位前的旧布局)");
                Assert(barMode.Contains("lab.TextTrimming = TextTrimming.CharacterEllipsis;"),
                       "★ 状态灯长文字截断 —— 不把右上角的读数怼没");
                Assert(barMode.Contains("这一场暂时不会出字"),
                       "★★ 进行中要当场写明【暂时不会出字】(引擎 P4 未接)—— 不写的话用户会对着安静的面板以为坏了");
                Assert(barMode.Contains("_latency.Text = \"未开始\";"),
                       "★ 没开始时右上角写「未开始」,不写「延迟 —」(那看着像在跑但测不出来)");
                Assert(barMode.Contains("_notesCardHost.Visibility"), "翻译历史只在文字模式出现");
                Assert(barMode.Contains("_textLayout.Visibility") && barMode.Contains("_interpretLayout.Visibility"),
                       "两套版面按模式切换,不是各建一份");
            }

            var tbSrc = TryReadSource(Path.Combine("Views", "TranslationBar.cs"));
            if (tbSrc is not null)
            {
                // ★ 跟手拖拽必须是【自己捕获鼠标 + 浮层气泡】—— OLE 拖放根本不移动元素(反复踩过的坑)
                Assert(!Body(tbSrc).Contains("DragDrop.DoDragDrop"), "★ 语言拖动不用 OLE 拖放(那个不跟手)");
                Assert(tbSrc.Contains("CaptureMouse()") && tbSrc.Contains("_ghost") && tbSrc.Contains("Canvas.SetLeft(_ghost"),
                       "★ 拖动跟手:浮层气泡跟着指针走");
                // ★ 手动捕获鼠标最怕"捕获收不回来"—— 那会把整个窗口的点击都吸走(同"点不动"事故)
                Assert(tbSrc.Contains("if (!CaptureMouse()) return;"), "★ 抓不到鼠标就不开始拖(不留半拖状态)");
                var unl = tbSrc[tbSrc.IndexOf("Unloaded += (_, _) =>", StringComparison.Ordinal)..];
                unl = unl[..400];
                Assert(unl.Contains("FinishDrag(null)"), "★ 拖到一半被重建时释放鼠标捕获(否则整窗点不动)");
                // ★ 拖起来那一刻就把语言从原板块摘掉(用户裁定),所以落点只决定【放到哪】
                var lift = Slice(tbSrc, "_liftedFromTarget = _dragFromTarget;", "_ghost = Bubble");
                Assert(lift is not null && lift.Contains("RemoveTarget(_dragLang.Code)") && lift.Contains("_liftedFromPool = _dragLang.Code"),
                       "★ 一开始拖就把该语言从原板块拿掉,其余补位");
                var drop = Slice(tbSrc, "// 拖起来的那一刻已经把它从原板块摘掉了", "var dropAt = e.GetPosition");
                Assert(drop is not null && drop.Contains("if (toTarget) landed = TheApp.Translation.AddTarget"),
                       "落在目标池 = 放进去");
                Assert(drop is not null && drop.Contains("_liftedFromPool = null"),
                       "★ 落空或拖回语言池:清掉暂借标记就自动回到语言池");
                var restore = Slice(tbSrc, "if (lang is null || !wasDragging || e is null)", "return;");
                Assert(restore is not null && restore.Contains("_liftedFromTarget) TheApp.Translation.AddTarget"),
                       "★ 拖到一半失去捕获也不能把语言弄丢(原样还回去)");
                Assert(tbSrc.Contains("_ghost.Width = _dragSize.Width") && tbSrc.Contains("_dragSize = new Size(source.ActualWidth"),
                       "★ 手上那张卡与坑里那张一样大(尺寸当场量,不重新算)");
                // ★ 松手不是秒跳:手上那张飞到新坑,被挤动的卡片各自滑过去(FLIP)
                Assert(tbSrc.Contains("Dictionary<string, Point> SnapshotCards()") && tbSrc.Contains("void AfterReflow("),
                       "★ 松手后走 FLIP:先记旧位置,排完版再动画到新位置");
                var reflow = Slice(tbSrc, "void AfterReflow(", "if (ghost is null) return;");
                Assert(reflow is not null && reflow.Contains("new TranslateTransform(dx, dy)"),
                       "★ 被挤动的其它语言也有动画(先推回旧位置再归零)");
                Assert(tbSrc.Contains("if (playSound) PlayLanding(dest);"),
                       "★ 到位才响那一声,不是松手就响");
                Assert(tbSrc.Contains("landedCard.Opacity = 0"),
                       "飞到之前先把真卡藏起来,免得同时看到两张");
                Assert(tbSrc.Contains("b.Tag = l.Code;"), "卡片带语言码,重排后才认得出是同一张");
                Assert(tbSrc.Contains("Canvas.SetLeft(_ghost, p.X - _dragOffset.X)"),
                       "按抓取点跟手,卡片不会在手里跳一下");
                Assert(tbSrc.Contains("Chip(\"清空\"") && tbSrc.Contains("Targets.ToList()) TheApp.Translation.RemoveTarget"),
                       "目标池有【清空】按钮,一键全送回语言池");
                Assert(tbSrc.Contains("CornerRadius(14)"), "语言是大圆角胶囊,池子是方形板块");
                Assert(tbSrc.Contains("new Slider") && tbSrc.Contains("IsSnapToTickEnabled = true"), "★ 翻译程度是滑条(四档吸附)");
                Assert(tbSrc.Contains("ShowTip(cell") && tbSrc.Contains("_tipBubble"), "★ 每个档位节点 hover 有解释气泡");
                Assert(tbSrc.Contains("? TheApp.Interpret.DirectionReady") && tbSrc.Contains(": st.IsFull"),
                       "★ “满”分场景:文字看目标池、同传看方向两个坑 —— 一律看目标池会让同传方向永远设不了");
                Assert(tbSrc.Contains("_targetBox.Width = TargetPoolWidth") && tbSrc.Contains("_poolBox.Width = LangPoolWidth"),
                       "两个池子各自用自己的宽度");
                Assert(tbSrc.Contains("GridUnitType.Star") && tbSrc.Contains("Grid.SetColumn(rightStack, 2)"),
                       "★ 右侧那一格占剩余空间");
                Assert(tbSrc.Contains("var pool = PoolCard();            // ★ 只此一处:各模式共用(多语言场景除外 —— 它不限量)"),
                       "★ 语言池两种模式共用、只建一次(两边都要从它往外拖)");
                // ★★ 两种模式都要显示的东西【只能建一次】,且要建在切换范围之外 ——
                //   同一天里三次栽在"一个元素两个父节点"上,最后一次让整个翻译界面打不开。
                //   (三处 = 定义 + 构造时那一次 + RebuildNotesCard 里那一次;
                //    后者是合法的,因为它【先把预览面板从旧卡上摘下来】再重建。)
                Assert(System.Text.RegularExpressions.Regex.Matches(Body(tbSrc), @"(?<![A-Za-z])NotesCard\(\)").Count == 3,
                       "★ 历史卡的建立点屈指可数 —— 多建一次就会挂上两个父节点,界面直接打不开");
                var rebuild = Slice(tbSrc, "void RebuildNotesCard()", "NotesCard();");
                Assert(rebuild is not null && rebuild.Contains("Children.Remove(_notesPreview)"),
                       "★ 重建之前必须先把预览面板从旧卡上摘下来");
                // ★ 学习笔记板块换成【翻译历史】(用户裁定):翻过的直接出现在这,点一条跳回原位
                Assert(tbSrc.Contains("翻译历史") && tbSrc.Contains("TheApp.History.Latest"),
                       "右下角是翻译历史预览");
                Assert(tbSrc.Contains("全部历史") && tbSrc.Contains("new HistoryBoardView()"),
                       "★ 右上角按钮改成「全部历史」,拉开抽屉看完整列表");
                Assert(tbSrc.Contains("_favoritesOnly = !_favoritesOnly"), "标题边上的星:只看收藏");
                Assert(tbSrc.Contains("const int HistoryPreviewCount = 5")
                       && tbSrc.Contains("Card(_notesPreview, \"翻译历史\", action: actions, scroll: false)"),
                       "★ 板块里最多五条,不滚动不翻页(更多点「全部历史」)");
                // ★ 第三轮裁定的排版:程度【竖排】,目标池与语言池【并列同宽】
                Assert(tbSrc.Contains("Orientation = Orientation.Vertical") && !Body(tbSrc).Contains("IsDirectionReversed"),
                       "★ 翻译程度竖排且【从下往上】递进:直译在底,越往上越详(设了 IsDirectionReversed 就反了)");
                Assert(tbSrc.Contains("TranslationLevels.All.Reverse()"), "档位标签跟着倒排,行 0 是最详的那档");
                var wheel = Slice(tbSrc, "card.PreviewMouseWheel +=", "card.Width = LevelWidth");
                Assert(wheel is not null && wheel.Contains("e.Handled = true"),
                       "★ 滚轮调档,且收下事件(否则会顺带把外面的会话区滚了)");
                Assert(wheel is not null && wheel.Contains("e.Delta > 0 ? 1 : -1"),
                       "往上滚 = 更详细(与竖排的下简上详一致)");
                Assert(tbSrc.Contains("Grid.SetColumn(target, 1)") && tbSrc.Contains("Grid.SetColumn(pool, 1)"),
                       "★ 目标池与语言池左右并列(不再一上一下)");
                Assert(tbSrc.Contains("const double LangPoolWidth = TargetPoolWidth * 2;"),
                       "★ 语言池宽度正好是目标池的两倍(用户裁定)");
                // ★ 固定的坑:拖进拖出只是填人/腾空,排版一动不动(此前是有几个画几个,每拖一次整块重排)
                Assert(tbSrc.Contains("for (int i = 0; i < Languages.MaxTargets; i++)")
                       && tbSrc.Contains("EmptySlot(\"拖入\")"),
                       "★ 目标池是 3 个固定的坑,空坑写浅色的「拖入」");
                Assert(tbSrc.Contains("for (int i = 0; i < PoolSlots; i++)") && tbSrc.Contains("const int PoolSlots = 6"),
                       "★ 语言池是 6 个固定的坑");
                Assert(tbSrc.Contains("_poolWrap.Children.Add(EmptySlot());"),
                       "语言池的空坑不写提示(用户裁定)");
                Assert(tbSrc.Contains("AddSlot()") && !Body(tbSrc).Contains("gear:"),
                       "★ 进设置的入口改成空坑里的「+」,齿轮撤掉");
                Assert(tbSrc.Contains("Height = SlotHeight") && tbSrc.Contains("const double SlotHeight"),
                       "语言卡与空坑等高(不然填进去的瞬间会跳)");
                Assert(tbSrc.Contains("LevelWidth + Gap") && tbSrc.Contains("TargetPoolWidth + Gap") && tbSrc.Contains("LangPoolWidth + Gap"),
                       "★ 三个板块之间间距一致,剩下的宽度全归学习笔记");
                // 档位名一律两个字(竖排在滑条边上,长短不齐会显得歪)
                foreach (var lv in Services.TranslationLevels.All)
                    Assert(lv.Name.Length == 2, "★ 档位名统一两个字:" + lv.Name);
                Assert(Services.TranslationLevels.SecondStageLabel(new[] { "ja", "en" }).Length == 2,
                       "★ 混合目标池下第二阶也是两个字(中性说法)");
                Assert(Services.TranslationLevels.SecondStageLabel(new[] { "ja" }) == "读音"
                       && Services.TranslationLevels.SecondStageLabel(new[] { "en", "de" }) == "词根",
                       "不混的时候仍给精确的说法");
                Assert(tbSrc.Contains("UniformGrid _targetWrap = new() { Columns = 1")
                       && tbSrc.Contains("UniformGrid _poolWrap = new() { Columns = 2"),
                       "★ 目标池 1 列、语言池 2 列 —— 是真的格子,不是估宽度凑的");
                Assert(!Body(tbSrc).Contains("_targetBox.Margin = new Thickness(0, 8, 0, 0)"),
                       "目标池不再靠上边距压在程度下面");
                // ★ 堆叠卡片 + 落地扬尘 + 音效【只给暖萌】,微风/墨白克制(用户裁定)
                // ★ 暖萌的堆叠卡片已砍(用户裁定):三套皮肤结构一致,只差颜色。
                Assert(!Body(tbSrc).Contains("Skin.Warm"), "★ 语言卡不再按皮肤分结构");
                var landing = Slice(tbSrc, "void PlayLanding(Point at)", "// 六粒尘");
                Assert(landing is not null && !landing.Contains("ThemeManager.Current"),
                       "★ 落地反馈是交互反馈、不是视觉身份 —— 三套皮肤一致(声音仍可在设置里关)");
                Assert(tbSrc.Contains("var soundOnArrive = landed;"), "★ 落地才有反馈,拖空了没有");
                Assert(!Body(tbSrc).Contains("TiltFor"), "叠牌用的歪斜角一并撤掉(卡片已砍)");
            }
            var cvTrans = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvTrans is not null)
            {
                // ★★ 只读是【数据状态】,必须排在【工作空间分流】之前。排反了 = 在已删除/已完成
                //   的项目里还能打字、还能发送、甚至在回收站里的项目下新建会话(2026-07-30 审查发现)。
                var bc = Slice(cvTrans, "void BuildConversationCore()", "sealed record ConvSpec");
                if (bc is not null)
                {
                    var ro = bc.IndexOf("if (ReadOnly)", StringComparison.Ordinal);
                    var ws = bc.IndexOf("_wsKey is not", StringComparison.Ordinal);
                    Assert(ro >= 0 && ws >= 0 && ro < ws,
                           "★ 只读判断排在工作空间分流之前(否则删掉的项目里还能发消息)");
                }
                // ★★ 会话面板已经统一成一份:翻译与聊天走同一个 BuildConvPanel,差异全在 ConvSpec 里。
                //   此前这几条断言是切 BuildTranslationLayout 的 —— 方法一删,IndexOf 返回 -1、
                //   src[-1..] 直接【抛异常而不是报 FAIL】,自检进程当场挂掉(审查预言的坑,实际发生了)。
                //   所以现在一律先 Slice(找不到就返回 null),绝不裸用 IndexOf。
                Assert(!Body(cvTrans).Contains("BuildTranslationLayout"), "★ 翻译不再有自己那一套会话构建器");
                var spec = Slice(cvTrans, "\"translation\" => new ConvSpec", "_ => new ConvSpec()");
                Assert(spec is not null, "★ SpecFor 里有翻译空间这一支");
                Assert(spec is not null && spec.Contains("HeroEmptyState = false"),
                       "★ 翻译空间输入框始终在底部,不居中(用户裁定)");
                Assert(spec is not null && spec.Contains("SearchIcon = true"),
                       "★ 翻译的发送按钮是放大镜(查翻译)");
                Assert(spec is not null && spec.Contains("BottomAccessory = () => new TranslationBar("),
                       "翻译空间下方挂语言池那一条");
                Assert(cvTrans.Contains("IconName.Search"), "放大镜图标还在");
                // 发送键【长什么样】与【能不能按】必须是两件事
                Assert(!Body(cvTrans).Contains("bool searchIcon"), "★ 外观与前置条件不再挤在一个 bool 上");
                // ★ 精确切到 BuildInputArea 的方法体(到它的 return 为止)——
                //   切到下一个方法会把 RunTranslationFallback 之类也圈进来,那不是这条要管的。
                var ia = Slice(cvTrans, "FrameworkElement BuildInputArea(", "        return area;");
                Assert(ia is not null && !Body(ia).Contains("TheApp.Translation"),
                       "★ 共享输入区不再直接摸翻译状态(分层泄漏归零)");
                // ★ 空态盒子必须 Stretch + MaxWidth:写死 Width 会在窄窗口撑破卡片、裁掉发送键;
                //   用 Center 又会按最宽的子元素收缩,输入行缩成标题那么宽。两个坑都渲染诊断里现过形。
                Assert(cvTrans.Contains("HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = spec.HeroWidth"),
                       "★ 空态内容盒取满宽度到上限为止,窄了自己缩");
                Assert(!Body(cvTrans).Contains("inputArea.Width ="), "空态输入框不写死宽度");
                Assert(cvTrans.Contains("_wasEmptyState = heroNow;"),
                       "★ 空态记账按【居中态】算,不是按有没有消息 —— 贴底态没有\"从居中滑下来\"这一说");
                // 发送按钮:放大镜要【居中不被裁】(固定尺寸的 Grid 容器 + 去掉按钮内边距)
                var sendBlk = cvTrans[cvTrans.IndexOf("internal static Button SearchSendButton", StringComparison.Ordinal)..];
                sendBlk = sendBlk[..900];
                Assert(sendBlk.Contains("new Grid { Width = 22, Height = 22 }") && sendBlk.Contains("Padding = new Thickness(0)")
                       && sendBlk.Contains("HorizontalContentAlignment = HorizontalAlignment.Center"),
                       "★ 放大镜居中且不被裁(定尺容器 + 零内边距 + 内容居中)");
                // ★★ 发送能不能按是个【会变的条件】,不能建界面时算一次就完事:
                //   之前进翻译空间时目标池是空的 -> 按钮灰掉,之后把语言拖进去按钮还是灰的(用户实测)。
                Assert(!Body(cvTrans).Contains("searchIcon && TheApp.Translation.Targets.Count == 0"),
                       "★ 发送状态不再是建构时算死的布尔");
                Assert(cvTrans.Contains("_canSend = spec.CanSend;")
                       && cvTrans.Contains("CanSend = () => ((App)Application.Current).Translation.Targets.Count > 0"),
                       "★ 发送前置条件存成【谓词】(问题本身),不是当时的答案");
                Assert(cvTrans.Contains("TheApp.Translation.Changed += RefreshSendEnabled;"),
                       "★ 目标池一变,发送键当场跟着变");
                var enter = Slice(cvTrans, "if (e.Key != Key.Enter) return;", "DataObject.AddPastingHandler");
                Assert(enter is not null && enter.Contains("_canSend is not null && !_canSend()"),
                       "★ Enter 与按钮问同一条判据(只灰按钮是做样子,回车照发)");
            }
            // ---- 跳到某个设置:橙色虚线框 + 真的滚到那一块(用户实测两个 bug)----
            var setReveal = TryReadSource(Path.Combine("Views", "SettingsView.cs"));
            if (setReveal is not null)
            {
                // ★ 旧写法 DoubleAnimation(0.35 -> 1) + AutoReverse + RepeatBehavior(2) 收在【起点】上,
                //   又没 FillBehavior.Stop -> 板块被永久按在 35% 不透明度,看着就是变灰了。
                Assert(!Body(setReveal).Contains("DoubleAnimation(0.35"),
                       "★ 不再用会把板块永久按灰的「闪一下」");
                Assert(setReveal.Contains("RevealHighlight.Show(card)"), "改成装饰层画橙色虚线框");
                Assert(setReveal.Contains("DispatcherPriority.ContextIdle"),
                       "★ 滚动排在布局与渲染之后,否则拿不到真实坐标、只会停在页面顶部");
                Assert(setReveal.Contains("else card.Loaded += "), "页面还没加载完就先等它加载");
                Assert(setReveal.Contains("下拉里是全部语言") && setReveal.Contains("new ComboBox { Width = 200"),
                       "★ 语言池「可添加」是下拉,列出全部语言");
            }
            var rhSrc = TryReadSource(Path.Combine("Views", "RevealHighlight.cs"));
            if (rhSrc is not null)
            {
                Assert(rhSrc.Contains("IsHitTestVisible = false"), "高亮框只是标记,不挡住底下的操作");
                Assert(rhSrc.Contains("TimeSpan.FromSeconds(5)"), "5 秒后自然消退");
                Assert(rhSrc.Contains("fe.Unloaded += OnTargetUnloaded"), "★ 切界面立刻收掉,不把框留在半空");
                Assert(rhSrc.Contains("RiskWarning"), "用橙色(风险语义色里的警告橙,三皮肤恒定)");
                Assert(rhSrc.Contains("DashStyle"), "虚线");
            }
            var cvFlash = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvFlash is not null)
                Assert(!Body(cvFlash).Contains("DoubleAnimation(0.35"),
                       "会话里跳转到某条消息也不再用那种会按灰的闪烁");

            var setLang = TryReadSource(Path.Combine("Views", "SettingsView.cs"));
            if (setLang is not null)
                Assert(setLang.Contains("翻译语言池") && setLang.Contains("RevealLanguagePool"), "设置里可增删语言池的语言");
            Assert(Services.Languages.DefaultPool.SequenceEqual(new[] { "zh", "ja", "en", "de", "ko" }),
                   "★ 默认语言池只有中/日/英/德/韩(用户裁定)");

            // ★ 对话内容必须能选中复制 —— TextBlock 选不了,所以气泡里用只读 TextBox
            var cvSel = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvSel is not null)
            {
                var bub = cvSel[cvSel.IndexOf("internal static TextBox MessageText(bool user)", StringComparison.Ordinal)..];
                bub = bub[..600];
                Assert(bub.Contains("new TextBox") && bub.Contains("SelectionBrushProperty"),
                       "★ 对话内容可选中复制(只读 TextBox,不是 TextBlock)");
                // ★ 常规 TextBox 模板把底色写死成 BgSunken,代码里设 Background 是【无效】的 ——
                //   那正是"灰底白字"的成因。所以必须走 PlainTextBox 这套光板模板,不能靠设属性。
                Assert(bub.Contains("\"PlainTextBox\"") && !bub.Contains("Background = Brushes.Transparent"),
                       "★ 正文走 PlainTextBox 模板(靠设 Background 是没用的,会糊出灰底)");
                // ★ 这条必须留在 if 里面:发布版没有源码,放外面就是"脱离源码树跑必挂"
                //   —— 开发机上恰好有源码,所以一开始没露馅,换到临时目录发布才炸出来。
                Assert(Body(cvSel).Contains("const int CollapseLines = 30;"), "★ 超长消息折叠阈值 = 30 行(用户裁定,原为 50)");
            }

            // ★ 剪贴板截图:DIB 没有真 alpha,整条是 0 -> 存出来的 png 完全透明,
            //   于是"附件挂上了、预览却是空白"(用户反馈)。补成不透明;真有透明度的图不许动。
            {
                var allZero = new byte[] { 1, 2, 3, 0, 4, 5, 6, 0 };
                Assert(Services.ClipboardImageFix.MakeOpaqueIfFullyTransparent(allZero), "全 0 alpha 被认出来");
                Assert(allZero[3] == 255 && allZero[7] == 255, "★ 全透明的截图被补成不透明(否则预览是空白)");
                Assert(allZero[0] == 1 && allZero[4] == 4, "只动 alpha,颜色一个字节都不改");

                var realAlpha = new byte[] { 1, 2, 3, 0, 4, 5, 6, 128 };
                Assert(!Services.ClipboardImageFix.MakeOpaqueIfFullyTransparent(realAlpha), "真带透明度的图不被认成坏图");
                Assert(realAlpha[3] == 0 && realAlpha[7] == 128, "★ 真有 alpha 的图原样保留(不乱改用户的图)");

                Assert(!Services.ClipboardImageFix.MakeOpaqueIfFullyTransparent(Array.Empty<byte>()), "空缓冲不误判");

                // 端到端:造一张【和剪贴板截图一样】的 Bgra32 全 0 alpha 位图,
                // 走一遍 Normalize -> 编码 png -> 再解码,断言存出来的图【不是全透明】。
                // 单测字节扫描证明不了这条链;而"预览空白"正是断在这条链上。
                const int w = 4, h = 4, stride = w * 4;
                var raw = new byte[stride * h];
                for (int i = 0; i < raw.Length; i += 4) { raw[i] = 200; raw[i + 1] = 40; raw[i + 2] = 30; raw[i + 3] = 0; }
                var src = System.Windows.Media.Imaging.BitmapSource.Create(
                    w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, raw, stride);
                var normalized = Services.ClipboardImageFix.Normalize(src);
                using var ms = new MemoryStream();
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(normalized));
                enc.Save(ms);
                ms.Position = 0;
                var decoded = new System.Windows.Media.Imaging.PngBitmapDecoder(ms,
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
                var decodedBgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    decoded, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                var outBuf = new byte[stride * h];
                decodedBgra.CopyPixels(outBuf, stride, 0);
                Assert(outBuf[3] == 255, "★ 截图存成 png 后不是全透明的(预览真画得出来)");
                Assert(outBuf[0] == 200 && outBuf[1] == 40 && outBuf[2] == 30, "修 alpha 没把颜色改掉");
            }
            var ctrlXaml = TryReadSource(Path.Combine("Theme", "Controls.xaml"));
            if (ctrlXaml is not null)
            {
                var plain = ctrlXaml[ctrlXaml.IndexOf("x:Key=\"PlainTextBox\"", StringComparison.Ordinal)..];
                plain = plain[..plain.IndexOf("</Style>", StringComparison.Ordinal)];
                Assert(plain.Contains("Value=\"Transparent\"") && !plain.Contains("BgSunken") && !plain.Contains("BorderBrush"),
                       "★ PlainTextBox 模板不画底色也不画边框(否则又是一块灰底)");
                Assert(plain.Contains("PART_ContentHost"), "PlainTextBox 仍保留承载文字的 PART_ContentHost(否则一个字都不显示)");
                Assert(!plain.Contains("Value=\"{x:Null}\""), "底色用 Transparent 而不是 null —— null 不参与命中测试就选不中了");
            }
            var cvClip = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvClip is not null)
            {
                Assert(cvClip.Contains("ClipboardImageFix.Normalize(raw)"), "粘贴截图时真的走了 alpha 修正");
                Assert(cvClip.Contains("var thumb = a.IsImage ? Thumb(a.Path, 120) : null") && cvClip.Contains("thumb is not null"),
                       "★ 缩略图读不出来时退回图标+名字,不留空白方块");
            }

            // ★ 回普通会话一律落在【空会话】,不跳进排第一的旧对话(用户裁定)
            var cvNormal = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvNormal is not null)
            {
                var tn = cvNormal[cvNormal.IndexOf("void ToNormal()", StringComparison.Ordinal)..];
                tn = tn[..tn.IndexOf("BuildConversation();", StringComparison.Ordinal)];
                Assert(tn.Contains("_sessionId = null"), "★ 回普通会话落在空会话(不自动跳进第一条旧对话)");
            }

            // ---- 当前使用者推测(仅显示;连不上就沿用缓存)----
            {
                var idSet = new Services.AppSettings();
                var idHub = new Services.HubClient();   // 未配对 -> State=NotPaired
                var g1 = Services.IdentityGuess.Current(idHub, idSet);
                Assert(g1.Source == Services.IdentitySource.Local && g1.IsGuess, "没缓存也没连上 -> 退回本机账户名,并标注为推测");
                Assert(!string.IsNullOrWhiteSpace(g1.DisplayName), "推测出的名字不为空");

                Services.IdentityGuess.Remember(idSet, "阿泽");
                var g2 = Services.IdentityGuess.Current(idHub, idSet);
                Assert(g2.DisplayName == "阿泽" && g2.Source == Services.IdentitySource.Cache,
                       "★ 主机没连上时【沿用上次推测的缓存】,而不是显示未连接");
                Assert(g2.IsGuess, "缓存来源仍算推测(界面弱化显示)");
            }
            var idSrc = TryReadSource(Path.Combine("Services", "IdentityGuess.cs"));
            if (idSrc is not null)
            {
                Assert(idSrc.Contains("MemberContext") == false || idSrc.Contains("这个类连 MemberContext 都不碰"),
                       "★ 身份推测不参与任何权限判定(D45 铁律)");
                Assert(idSrc.Contains("IdentitySource.Local") && idSrc.Contains("推测"), "来源如实标注(Hub/Cache/Local)");
            }

            // ---- 会话分层存储:热层 / 温层(归档不是删除)----
            {
                var stateDir = Path.Combine(Path.GetTempPath(), "localai-tier-" + Guid.NewGuid().ToString("N")[..6]);
                var prevState = Environment.GetEnvironmentVariable(Services.AppPaths.StateEnvVar);
                Environment.SetEnvironmentVariable(Services.AppPaths.StateEnvVar, stateDir);
                try
                {
                    var tc = new Services.ChatCenter();
                    var ts = tc.NewSession(null, "chat");
                    for (int i = 0; i < 12; i++) tc.Send(ts.SessionId, "m" + i);   // 每次 Send 记 2 条(用户+系统)
                    var total = tc.MessagesOf(ts.SessionId).Count();
                    Assert(total == 24, $"发 12 次 = 24 条消息(实得 {total})");

                    var moved = tc.ArchiveOldMessages(keepRecent: 10);
                    Assert(moved == 14, $"超出热层的移到温层(实得 {moved})");
                    Assert(tc.MessagesOf(ts.SessionId).Count() == 10, "热层只剩最近 10 条");
                    Assert(tc.UnloadedArchivedCount(ts.SessionId) == 14, "界面能看到还有 14 条更早的没加载");

                    // ★ 归档【不是删除】:原文还在温层文件里
                    Assert(Services.SessionArchive.Count(ts.SessionId) == 14, "★ 原文仍在温层(归档不是删除)");
                    // ★ 温层不进 Export —— 否则 chat.json 会和归档文件重复一份
                    Assert(tc.Export().Messages.Count(m => m.SessionId == ts.SessionId) == 10,
                           "★ 温层消息不写回 chat.json(热层才是该落盘的那份)");

                    tc.LoadArchived(ts.SessionId);
                    Assert(tc.MessagesOf(ts.SessionId).Count() == 24, "加载更早后能看到全部 24 条");
                    Assert(tc.UnloadedArchivedCount(ts.SessionId) == 0, "加载后不再提示还有更早的");
                    Assert(tc.Export().Messages.Count(m => m.SessionId == ts.SessionId) == 10,
                           "★ 加载回来的温层消息仍不写回 chat.json(不会重复)");

                    // 幽灵会话不参与归档(它压根不落盘)
                    var tg = tc.NewGhostSession("chat");
                    for (int i = 0; i < 15; i++) tc.Send(tg.SessionId, "g" + i);
                    tc.ArchiveOldMessages(keepRecent: 5);
                    Assert(Services.SessionArchive.Count(tg.SessionId) == 0, "★ 幽灵会话永不进温层");

                    // 彻底删除会话 -> 温层一并清掉,不留孤儿归档
                    tc.Delete(ts.SessionId);
                    tc.PurgeDeleted(ts.SessionId);
                    Assert(Services.SessionArchive.Count(ts.SessionId) == 0, "彻底删除会话时温层一并清除");
                }
                finally
                {
                    Environment.SetEnvironmentVariable(Services.AppPaths.StateEnvVar, prevState);
                    try { Directory.Delete(stateDir, recursive: true); } catch { }
                }
            }
            var svMark = TryReadSource(Path.Combine("Views", "StorageView.cs"));
            if (svMark is not null)
                Assert(svMark.Contains("MarkOriginalsDeleted") && svMark.Contains("ArchivedSessionIds"),
                       "★ 删除归档原文后给相关记忆打上「原文已删除」(不留死链)");
            var cvMore = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvMore is not null)
                Assert(cvMore.Contains("加载更早的") && cvMore.Contains("UnloadedArchivedCount"),
                       "会话顶部有【加载更早】入口");
            var appArch = TryReadSource("App.xaml.cs");
            if (appArch is not null)
                Assert(appArch.Contains("ArchiveOldMessages"), "启动时把超出热层的旧消息归档(chat.json 保持有界)");

            // ---- 记忆库 + 存储清理(2026-07-30 用户裁定)----
            {
                var mc = new Services.MemoryCenter();
                var old = new Services.MemoryEntry(Services.MemoryCenter.NewId(), "很久没用的摘要", "正文", Services.MemoryKind.Summary,
                    Services.ProjectScope.Personal, Services.MemberContext.Current, null, new[] { "s-1" }, DateTime.Now.AddDays(-90));
                var pinned = new Services.MemoryEntry(Services.MemoryCenter.NewId(), "置顶的老摘要", "正文", Services.MemoryKind.Summary,
                    Services.ProjectScope.Personal, Services.MemberContext.Current, null, null, DateTime.Now.AddDays(-90), Pinned: true);
                var pref = new Services.MemoryEntry(Services.MemoryCenter.NewId(), "老偏好", "正文", Services.MemoryKind.Preference,
                    Services.ProjectScope.Personal, Services.MemberContext.Current, null, null, DateTime.Now.AddDays(-90));
                var fresh = new Services.MemoryEntry(Services.MemoryCenter.NewId(), "刚用过", "正文", Services.MemoryKind.Summary,
                    Services.ProjectScope.Personal, Services.MemberContext.Current, null, null, DateTime.Now.AddDays(-90), LastUsedAt: DateTime.Now);
                foreach (var m in new[] { old, pinned, pref, fresh }) mc.Add(m);

                var plan = mc.PlanAutoClean(30, 0);
                Assert(plan.Any(x => x.Id == old.Id), "自动清理:长期没用到的摘要进清单");
                Assert(!plan.Any(x => x.Id == pinned.Id), "★ 置顶的永不自动清理");
                Assert(!plan.Any(x => x.Id == pref.Id), "★ 偏好/事实类永不自动清理(只清摘要)");
                Assert(!plan.Any(x => x.Id == fresh.Id), "最近用过的不清");
                Assert(mc.Items.Count == 4, "★ 预演不动数据(先列清单、确认后才删)");
                Assert(mc.ApplyClean(plan) == plan.Count && mc.Items.Count == 3, "确认后才真的删");
                Assert(mc.PlanAutoClean(0, 0).Count == 0, "两条规则都为 0 = 关闭,不清任何东西");

                // 原文被删 -> 记忆标注出来(避免以后点回原文是死链)
                var m2 = new Services.MemoryEntry(Services.MemoryCenter.NewId(), "带来源的", "正文", Services.MemoryKind.Summary,
                    Services.ProjectScope.Personal, Services.MemberContext.Current, "prj-1", new[] { "s-9" }, DateTime.Now);
                mc.Add(m2);
                mc.MarkOriginalsDeleted(new[] { "s-9" });
                Assert(mc.Find(m2.Id)!.SourceOriginalsDeleted, "★ 原文删除后记忆标注「原文已删除」(不留死链)");

                // D45:别人的个人记忆不出现
                mc.Add(new Services.MemoryEntry(Services.MemoryCenter.NewId(), "别人的私人记忆", "正文", Services.MemoryKind.Summary,
                    Services.ProjectScope.Personal, "m-other", null, null, DateTime.Now));
                Assert(!mc.Visible().Any(x => x.Title == "别人的私人记忆"), "★ 记忆库同样守 D45(别人的私人记忆不出现)");

                // JSON 往返
                var mj = System.Text.Json.JsonSerializer.Serialize(mc.Export(), StoreJson);
                var mb = new Services.MemoryCenter();
                mb.Import(System.Text.Json.JsonSerializer.Deserialize<List<Services.MemoryEntry>>(mj, StoreJson));
                Assert(mb.Items.Count == mc.Items.Count, "记忆库 JSON 往返");
                Assert(mc.Bytes() > 0, "记忆库占用可计算(真算字节,不估)");
            }
            Assert(Services.StorageUsage.Human(-1) == "—", "没有的项显示「—」而不是 0");
            Assert(Services.StorageUsage.Human(1536).StartsWith("1.5"), "占用按 KB/MB 可读显示");
            var svSrc = TryReadSource(Path.Combine("Views", "StorageView.cs"));
            if (svSrc is not null)
            {
                Assert(svSrc.Contains("清理缓存 & 自动整理") && svSrc.Contains("执行上述勾选项"), "板块与按钮用新措辞");
                Assert(!svSrc.Contains("一键清爽"), "旧措辞「一键清爽」已去掉");
                Assert(svSrc.Contains("TidyClearCache") && svSrc.Contains("TidyDeleteArchivedOriginals"), "勾选决定执行哪些动作");
                // 三块合并成一张卡:整张视图只有一个 Ui.Card
                Assert(svSrc.Split("Ui.Card(").Length - 1 == 1, "★ 缓存/摘要/记忆库合并为同一个板块(只有一张卡)");
                Assert(svSrc.Contains("new Slider") && svSrc.Contains("IsSnapToTickEnabled = true"), "阈值与总量上限用滑条(吸附到档)");
                Assert(svSrc.Contains("ThresholdMax = 400_000"), "整理阈值滑条上限 400k 字符(≈128K token 级)");
                Assert(svSrc.Contains("MemoryCaps = { 0, 50, 100, 250, 500, 1024, 2048 }"), "记忆库总量上限是阶段值");
                Assert(svSrc.Contains("(\"一年\", 365)") && svSrc.Contains("(\"三年\", 1095)"), "保留期改成选时间(7/30/90 天、一年/两年/三年)");
                Assert(svSrc.Contains("StorageUsage.Snapshot"), "同板块内显示当前缓存等占用大小");
                Assert(svSrc.Contains("AI 尚未接入"), "★ 整理摘要在 AI 未接入时【不做事】并如实说明(不拼假摘要)");
                Assert(svSrc.Contains("不可逆") && svSrc.Contains("ConfirmDialog.Show(\"删除归档原文\""),
                       "★ 删除归档原文单独二次确认(勾了也要再确认)");
                Assert(svSrc.Contains("记忆库是空的"), "记忆库空态如实说明是因为 AI 未接入");
            }
            var setDefaults = new Services.AppSettings();
            Assert(setDefaults.TidyDeleteArchivedOriginals == false, "★「删除归档原文」默认【不勾】(危险项不能默认开)");
            Assert(setDefaults.TidyClearCache && setDefaults.TidySummarize, "安全项默认勾上");
            Assert(setDefaults.MemoryAutoCleanDays == 0 && setDefaults.MemoryMaxMB == 0, "记忆库自动清理默认关闭");
            Assert(setDefaults.SummaryTrigger == "ai", "摘要默认由 AI 自行判断何时整理");
            Assert(setDefaults.SummaryThresholdChars > 0, "整理阈值有默认值且可在设置里改");

            // ---- 项目文件夹唯一性:同路径只能一个项目;子路径不算;跨机器不算 ----
            {
                var root = Path.Combine(Path.GetTempPath(), "localai-uniq-" + Guid.NewGuid().ToString("N")[..6]);
                var sub = Path.Combine(root, "sub");
                var pu = new Services.ProjectCenter();
                var a = pu.Create("A", root, null, Services.ProjectScope.Personal);
                // 完全相同的路径 -> 不再新建,直接返回既有的
                var again = pu.Create("B", root, null, Services.ProjectScope.Personal);
                Assert(again.ProjectId == a.ProjectId && pu.Items.Count == 1, "★ 同一路径不会建出第二个项目(直接返回既有)");
                // 末尾斜杠 / 大小写 视作同一路径
                var withSlash = pu.Create("C", root + Path.DirectorySeparatorChar, null, Services.ProjectScope.Personal);
                Assert(withSlash.ProjectId == a.ProjectId, "结尾斜杠视作同一路径");
                Assert(pu.Create("D", root.ToUpperInvariant(), null, Services.ProjectScope.Personal).ProjectId == a.ProjectId, "大小写不同视作同一路径");
                // ★ 子路径【不算】重复
                var subP = pu.Create("Sub", sub, null, Services.ProjectScope.Personal);
                Assert(subP.ProjectId != a.ProjectId && pu.Items.Count == 2, "★ 子路径不算重复,可以另立项目");
                // 不同机器上的同一路径也不算重复
                var remote = pu.Create("远端同名", root, null, Services.ProjectScope.Personal, "chat", "dev-999");
                Assert(remote.ProjectId != a.ProjectId, "不同机器的同一路径不算重复");
                Assert(remote.HostMachine == "dev-999", "项目记住文件夹所在机器");

                // 查找要能看到【已完成 / 已删除】的同路径项目(否则回收站里躺着一个还会重复建)
                pu.SetStatus(a.ProjectId, Services.ProjectStatus.Done);
                Assert(pu.FindByFolder(root)?.ProjectId == a.ProjectId, "同路径查找能找到【已完成】的项目");
                pu.Delete(a.ProjectId);
                Assert(pu.FindByFolder(root)?.ProjectId == a.ProjectId, "同路径查找能找到【已删除】的项目");
                Assert(pu.FindByFolder(root, excludeId: a.ProjectId) is null, "编辑自身时不会把自己判成重复");

                // 合并旧存档里的同路径重复:会话并到保留的那个
                var pm = new Services.ProjectCenter();
                var cm = new Services.ChatCenter();
                pm.Import(new List<Services.Project>
                {
                    new("dup-old", "旧的", "", "chat", Services.ProjectScope.Personal, DateTime.Now.AddDays(-2), FolderPath: root, OwnerMemberId: Services.MemberContext.Current),
                    new("dup-new", "新的", "", "chat", Services.ProjectScope.Personal, DateTime.Now, FolderPath: root + Path.DirectorySeparatorChar, OwnerMemberId: Services.MemberContext.Current),
                    new("keep-sub", "子目录的", "", "chat", Services.ProjectScope.Personal, DateTime.Now, FolderPath: sub, OwnerMemberId: Services.MemberContext.Current),
                });
                cm.NewSession("dup-old", "chat");
                cm.NewSession("dup-new", "chat");
                var mergedN = pm.MergeDuplicateFolders((from, to) => cm.ReassignSessions(from, to));
                Assert(mergedN == 1 && pm.Items.Count == 2, "★ 自动合并同路径重复项目(子路径的保留)");
                var kept = pm.Items.First(x => x.ProjectId is "dup-old" or "dup-new").ProjectId;
                Assert(cm.SessionsOf(kept).Count() == 2, "★ 被合并项目的会话并到保留的那个项目下(不丢会话)");
                Assert(pm.Items.Any(x => x.ProjectId == "keep-sub"), "子路径项目不参与合并");
            }
            // ---- 全项目审计修复(2026-08-02,42 条发现 39 条成立)——钉住最要害的几条 ----
            {
                // 同传方向:空串 = 清空(原先被语言校验静默吞掉 -> 方向永久锁死)
                var d2 = new Services.InterpretState();
                d2.SetMyLang("zh"); d2.SetMyLang("");
                Assert(d2.MyLang.Length == 0, "★ 方向坑能【清空】—— 否则两坑一满语言池整体禁用,方向从此改不了");
                // 同语言方向拦下(坑到坑一拖能造出「中文↔中文」)
                d2.SetMode(Services.TranslationMode.Interpret);
                d2.SetMyLang("zh"); d2.SetTheirLang("zh");
                Assert(d2.Start(null).Contains("同一种语言") && !d2.Running,
                       "★ 「中文↔中文」开始不了 —— 那不是同传,是复读");
                // Import 对 null 字段的档不炸(语法合法的 {} 反序列化出来就是 null 字段)
                var cc0 = new Services.ChatCenter();
                cc0.Import(new Services.ChatCenter.Snapshot(null!, null!));
                Assert(cc0.Sessions.Count == 0, "★ 字段为 null 的存档当空表,不炸 —— 炸了会连累退出时用空数据覆盖别的档");
                // RainOutlook 按城市当地的今天切(隔日界线的城市原先整体错一天)
                var wDays = new Services.WeatherNow(null, null, null, null, null, null,
                    Days: new List<Services.WeatherDay> { new(DateTime.Today.AddDays(2), 5.0) });
                Assert(Services.Weather.RainOutlook(wDays, DateTime.Today.AddDays(1))!.Contains("明天"),
                       "★ RainOutlook 按【那座城】的今天切,不按本机(隔日界线会整体错一天)");
            }
            {
                var cvA = TryReadSource(Path.Combine("Views", "ChatView.cs"));
                if (cvA is not null)
                {
                    Assert(cvA.Contains(".FirstOrDefault(x => x.WorkspaceKey == _wsKey)?.SessionId"),
                           "★ SelectProject 只自动打开【本空间】的会话 —— 否则三道跳转护栏被整体绕过,聊天写进翻译历史");
                    Assert(cvA.Contains("if (ViewingDeletedProject)") && cvA.Contains("只读就是只读"),
                           "★ 已删除项目的只读浏览不给三点菜单(搬软删会话只会造孤儿)");
                    Assert(cvA.Contains("if (TheApp.Interpret.Running) return;") || TryReadSource(Path.Combine("Views", "TranslationBar.cs"))!.Contains("if (TheApp.Interpret.Running) return;"),
                           "★ 重复点「开始同传」不再多建一条空记录");
                    Assert(cvA.Contains("TheApp.Interpret.Stop();"),
                           "★ 离开翻译空间 = 这一场同传结束(不留一个看不见的进行中)");
                }
                var appA = TryReadSource("App.xaml.cs");
                if (appA is not null)
                {
                    Assert(appA.Contains("_failedStores") && appA.Contains("if (!_failedStores.Contains(path))"),
                           "★★ 导入失败的存档【不参与退出保存】—— 否则空表覆盖盘上完好的数据");
                }
                var hvA = TryReadSource(Path.Combine("Views", "HomeView.cs"));
                if (hvA is not null)
                {
                    Assert(!hvA.Contains("hit.ToolTip = r.Message") && hvA.Contains("ShowPullResult(r.Ok, r.Message);"),
                           "★★ 拉取结果【真的显示出来】—— 原先写进全局已关闭的 ToolTip,同步失败完全无声");
                    Assert(hvA.Contains("{ RefreshWeatherUi(); PullWeather(); }"),
                           "★ 天气每轮先重画 —— 拉取失败不发 Changed,stale 标记全靠这一下,否则断网后整卡冻结");
                    Assert(!hvA.Contains("与 Apple 提醒事项同步的设置"),
                           "★ 待办板块不再有跳 Apple 同步的齿轮(D57:待办与 Apple 已毫无关系)");
                }
                var ccA = TryReadSource(Path.Combine("Services", "ChatCenter.cs"));
                if (ccA is not null)
                    Assert(ccA.Contains("SessionArchive.ArchivedSessionIds())") && ccA.Contains("SessionArchive.Load(sid)"),
                           "★ 「清理缓存」的引用表把温层归档也算上 —— 否则归档消息的截图唯一副本被当垃圾删掉");
            }

            // ---- 文件翻译场景(D59,2026-08-02 用户裁定)----
            {
                var ftst = new Services.FileTransState();
                Assert(!ftst.RealtimePreview, "★ 实时预览默认【关】(用户裁定)");
                Assert(Services.FileTransState.Supported("a.PNG") && Services.FileTransState.Supported("b.pdf")
                       && !Services.FileTransState.Supported("c.docx"), "只吃 PNG/JPG/PDF,别的如实拒绝");
                ftst.SetFile("fs-1", Path.Combine(Path.GetTempPath(), "localai-ft-demo.png"));
                ftst.AddBox("fs-1", new Services.MarkBox(0.1, 0.2, 0.3, 0.4));
                Assert(ftst.DocOf("fs-1")!.Boxes.Count == 1, "标注框记在文档上");
                Assert(ftst.UndoBox("fs-1") && ftst.DocOf("fs-1")!.Boxes.Count == 0, "撤回去掉最后一个框");
                Assert(!ftst.UndoBox("fs-1"), "没有可撤的如实返回 false(按钮据此说话,不装聋)");
                ftst.AddBox("fs-1", new Services.MarkBox(0.5, 0.5, 0.2, 0.2));
                var rt2 = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Services.FileDoc>>(
                              System.Text.Json.JsonSerializer.Serialize(ftst.Export()))!;
                Assert(rt2["fs-1"].Boxes.Count == 1, "★ 文件与标注框能落盘往返(重启不丢已画的框)");

                var cf = new Services.ChatCenter();
                var fsess = cf.NewSession(null, "translation", fileTrans: true);
                Assert(fsess.FileTrans && !Services.ChatCenter.CanMove(fsess),
                       "★ 文件翻译会话与同传同款:不能搬到项目/别的工作空间");
                var tbF = TryReadSource(Path.Combine("Views", "TranslationBar.cs"));
                if (tbF is not null)
                {
                    var iAuto = tbF.IndexOf("AI 自动标注", StringComparison.Ordinal);
                    // 「创建标注框」按钮已删(左键常态就是画框);撤回仍在自动标注之后
                    var iBox = tbF.IndexOf("ToolChip(\"撤回\"", StringComparison.Ordinal);
                    Assert(iAuto >= 0 && iBox > iAuto, "★ 「AI 自动标注」排工具栏第一位(用户指定)");
                    Assert(tbF.Contains("start.IsEnabled = !ft.RealtimePreview;"),
                           "★ 实时预览开着时「开始翻译」灰掉(用户裁定:实时模式下没有\"开始\"这回事)");
                }
                var cvF = TryReadSource(Path.Combine("Views", "ChatView.cs"));
                if (cvF is not null)
                    Assert(cvF.Contains("引用该会话") && cvF.Contains("[引用会话 {s.SessionId}]"),
                           "★ 所有会话的三点菜单都有【引用该会话】(复制 ID,发到别的会话让 AI 读;AI 未接入前如实说明)");
            }

            // ---- 多语言表(D60,2026-08-02/03 用户裁定)----
            {
                // ★ 事故钉(2026-08-03):`SelectionChanged -= null` 会当场 ArgumentNullException,
                //   把整个翻译界面炸开 —— 程序性回选一律走布尔护栏,不许再出现 -= null。
                var tbNull = TryReadSource(Path.Combine("Views", "TranslationBar.cs"));
                var cvNull = TryReadSource(Path.Combine("Views", "ChatView.cs"));
                Assert(tbNull is null || !tbNull.Contains("-= null;"), "★ 不许 `事件 -= null`(WPF 当场抛,整页打不开)");
                Assert(cvNull is null || !cvNull.Contains("-= null;"), "★ 不许 `事件 -= null`(ChatView 同规)");
            }
            {
                var i18 = new Services.I18nState();
                Assert(i18.ImportJson("{\"b\":\"你好 {name}\",\"a\":\"开始\"}") == 2, "平铺 JSON 能导入");
                Assert(i18.Doc.Entries[0].Key == "a", "★ 键序排序(硬规则②:diff 干净)");
                Assert(i18.ImportJson("{bad json") == -1, "★ 非法 JSON 拒绝导入 —— 不吞半张表");
                Assert(Services.I18nState.Placeholders("你好 {name} %s 第{0}个").Length == 3, "占位符提取:{x}/%s/{0}");
                Assert(!Services.I18nState.PlaceholdersOk("你好 {name}", "Hello name"), "★ 占位符丢了 = 坏译文");
                Assert(Services.I18nState.PlaceholdersOk("你好 {name}", ""), "空译文是【缺】不是【错】");
                i18.AddLang("en"); i18.AddLang("en"); i18.AddLang("ja"); i18.AddLang("pt-BR");
                Assert(i18.Doc.TargetLangs.Count == 3, "★ 目标语言不限量且去重(pt-BR 这类自定义码也收)");
                i18.Doc.Entries[1].Trans["en"] = "Hello name";   // 占位符坏
                var f1 = Path.Combine(Path.GetTempPath(), "localai-i18n-" + Guid.NewGuid().ToString("N")[..6] + ".json");
                var (okA, msgA) = i18.Export(f1);
                Assert(!okA && msgA.Contains("占位符"), "★★ 占位符校验不过【拒绝导出】(AI 读了会错)");
                i18.Doc.Entries[1].Trans["en"] = "Hello {name}";
                var (okB, _) = i18.Export(f1);
                // ★ 单文件导出(用户裁定 2026-08-03,推翻一源两出):一个完整对照 JSON
                Assert(okB && File.Exists(f1), "★ 导出 = 单个完整对照 JSON(所有语言都在里面,AI 直接读)");
                var fb = File.ReadAllBytes(f1);
                Assert(fb.Length > 0 && fb[0] != 0xEF, "★ UTF-8 无 BOM(硬规则①)");
                Assert(File.ReadAllText(f1).Contains("\"@src\""), "对照表形状(@src 在内)");
                try { File.Delete(f1); } catch { }
            }

            {
                // D60 五补:改键 + 两父节点事故钉
                var i9 = new Services.I18nState();
                i9.ImportJson("{\"a\":\"甲\",\"b\":\"乙\"}");
                Assert(i9.RenameKey("a", "c") && i9.Doc.Entries.Any(x => x.Key == "c"), "键可改名(用户改裁定)");
                Assert(!i9.RenameKey("c", "b") && !i9.RenameKey("b", " "), "改成重复键/空键被拒,原样不动");
                // 抽屉已改浮窗(D60 六/七补):每次 Show 全新构建内容,不存在字段控件重挂 ——
                //   两父节点那类事故从结构上消失。钉住:选择器只有浮窗一条路,源/目标共用。
                var lpSrc = TryReadSource(Path.Combine("Views", "I18nLangPicker.cs"));
                if (lpSrc is not null)
                {
                    Assert(lpSrc.Contains("Flyout.Show(anchor, forSource ? \"源语言\" : \"目标语言\""),
                           "★ 源/目标语言共用一个浮窗(单选/多选两模式),跟着点击锚点走");
                    Assert(lpSrc.Contains("OrderByDescending(x => I18nState.PercentValue(x.Code))"),
                           "浮窗清单按全球使用者占比排序(静态)");
                }
                var tbP = TryReadSource(Path.Combine("Views", "TranslationBar.cs"));
                if (tbP is not null)
                    Assert(!tbP.Contains("_i18nDrawer"), "★ 抽屉已拆干净,不留死结构");
            }

            // ---- 回信(D61,2026-08-03 用户裁定)----
            {
                var rd = new Services.ReplyDoc { Medium = Services.ReplyMedium.Paper, Tone = Services.ReplyTone.Official,
                    TheirName = "王先生", TheirAddress = "北京市某街 2 号",
                    SignDate = "2026年8月3日", GreetingIndex = 1, ClosingIndex = 1,
                    Draft = "感谢来信。\n事情已办妥。" };
                var me = new Services.ReplyProfile { MyName = "李四", MyAddress = "上海市某路 1 号", MyContact = "13800000000" };
                var rst = new Services.ReplyState();
                var paper = rst.Compose(rd, me);
                Assert(paper.Contains("王先生:") && paper.Contains("    您好!") && paper.Contains("此致敬礼!"),
                       "★ 纸质:称呼/缩进问候/祝福各就各位(格式装配是真的,不等引擎)");
                Assert(paper.Contains("李四  2026年8月3日") && paper.Contains("北京市某街 2 号"),
                       "★ 纸质:右侧署名+日期、对方地址排入(日期只进纸质)");
                rd.Medium = Services.ReplyMedium.Message;
                var msg = rst.Compose(rd, me);
                Assert(!msg.Contains("2026年8月3日") && !msg.Contains("某街"),
                       "★ 短消息:不排日期不排地址(紧凑)");
                rd.Medium = Services.ReplyMedium.Email;
                var mail = rst.Compose(rd, me);
                Assert(mail.Contains("13800000000") && !mail.Contains("某路"),
                       "★ 邮件:带签名联系方式;地址只进纸质");
                // 设置跟随会话:两个会话各自的 Doc 互不串
                var rs = new Services.ReplyState();
                rs.SetSession("r-1"); rs.Doc.TheirName = "甲";
                rs.SetSession("r-2"); rs.Doc.TheirName = "乙";
                rs.SetSession("r-1");
                Assert(rs.Doc.TheirName == "甲", "★ 设置跟随会话:回到旧会话设置还在");
                rs.SetSession(null);
                Assert(rs.Doc.TheirName == "", "新进来 = 全默认(草稿态)");
                // ★ 空值 -> [方括号] 模板占位(用户裁定 2026-08-03):产出是填空模板,不是悄悄少一行
                var blank = rst.Compose(
                    new Services.ReplyDoc { Medium = Services.ReplyMedium.Paper, Draft = "正文" }, new Services.ReplyProfile());
                Assert(blank.Contains("[对方称呼]") && blank.Contains("[我的署名]") && blank.Contains("[我的地址]"),
                       "★ 信息为空时装配成 [方括号] 占位,复制出去就是可找替换的模板");
                // 问候/祝福跟随【语言 + 语气】,且可自定义追加(跨会话共用)
                Assert(rst.Greetings("de", Services.ReplyTone.Official).Any(x => x.StartsWith("Sehr geehrte"))
                    && rst.Greetings("zh", Services.ReplyTone.Official).Any(x => x.Contains("敬启者")),
                       "★ 问候语跟随语言(德语信不给中文套语)");
                Assert(rst.Closings("ja", Services.ReplyTone.Official).Contains("敬具"), "祝福同理随语言");
                var addIdx = rst.AddCustom(true, "见信如晤", "zh", Services.ReplyTone.Polite);
                Assert(addIdx > 0 && rst.Greetings("zh", Services.ReplyTone.Polite)[addIdx] == "见信如晤",
                       "★ 可自定义追加问候语,追加后就在清单里");
                Assert(rst.Profile.CustomGreetings.Count == 1 && rst.AddCustom(true, "见信如晤", "zh", Services.ReplyTone.Polite) > 0,
                       "重复追加不会加出两条");
                // ★ 加得进去就得删得掉(用户反馈 2026-08-03)
                rst.Doc.GreetingIndex = rst.Greetings("zh", Services.ReplyTone.Polite).Length - 1;
                Assert(rst.IsCustom(true, "见信如晤") && !rst.IsCustom(true, rst.Greetings("zh", Services.ReplyTone.Polite)[1]),
                       "★ 分得清哪条是用户自己加的(内置的不给删)");
                // ★★ 删一条自定义 = 【所有会话】的下标都得跟着对齐 —— 存档存的是下标不是文字,
                //   不对齐的话,下标排在它后面的信会静默换成另一句祝福(内容被改却没人说一声)。
                {
                    var rr = new Services.ReplyState();
                    rr.SetSession("cust-a");
                    var i1 = rr.AddCustom(true, "自定义一", "zh", Services.ReplyTone.Polite);
                    var i2 = rr.AddCustom(true, "自定义二", "zh", Services.ReplyTone.Polite);
                    Assert(i1 == Services.ReplyState.CustomBase && i2 == Services.ReplyState.CustomBase + 1,
                           "★ 自定义从固定下标起排(1 个「不加」+ 3 条内置)—— 换算靠内置恒为三条");
                    rr.Doc.GreetingIndex = i2;                       // A 会话指着【第二条】自定义
                    rr.SetSession("cust-b"); rr.Doc.GreetingIndex = i1;   // B 会话指着第一条
                    rr.RemoveCustom(true, "自定义一");                 // 删掉第一条
                    rr.SetSession("cust-a");
                    Assert(rr.Greetings("zh", Services.ReplyTone.Polite)[rr.Doc.GreetingIndex] == "自定义二",
                           "★★ 删掉前面那条之后,A 会话指的还是【自定义二】,没有被静默换成别的");
                    rr.SetSession("cust-b");
                    Assert(rr.Doc.GreetingIndex == 0,
                           "★ 正指着被删那条的会话退回「不加」,不指着一条不存在的");
                }
                foreach (var lg in new[] { "zh", "ja", "de", "fr", "en", "xx" })
                    foreach (var tn in new[] { Services.ReplyTone.Casual, Services.ReplyTone.Polite, Services.ReplyTone.Official })
                        Assert(new Services.ReplyState().Greetings(lg, tn).Length == Services.ReplyState.CustomBase
                            && new Services.ReplyState().Closings(lg, tn).Length == Services.ReplyState.CustomBase,
                               $"★ {lg}/{tn}:内置恒为三条 —— CustomBase 这个换算就建立在这上面");
                Assert(rst.RemoveCustom(true, "见信如晤") && rst.Profile.CustomGreetings.Count == 0,
                       "★ 自定义问候语删得掉");
                Assert(rst.Doc.GreetingIndex == 0, "★ 删掉选中项后下标退回【不加】,不能指着一条不存在的");
                Assert(!rst.RemoveCustom(false, "敬具"), "内置祝福删不掉(那不是用户加的)");
                var rbSrc = TryReadSource(Path.Combine("Views", "ReplyBar.cs"));
                if (rbSrc is not null)
                {
                    // 载体 = 指针(等分轨 + 圆点),不是进度滑条 —— 它三种方式没有高低之分
                    Assert(rbSrc.Contains("PointerRow(\"载体\"") && !rbSrc.Contains("_medium.Value"),
                           "★ 载体用指针式选择器,不用带填充的滑条(没有进度语义)");
                    Assert(!rbSrc.Contains("new ScrollViewer"),
                           "★ 设置卡里不许【构造】滚动条 —— 内容按最小窗口算好,装得下(注释里提它不算)");
                    // 字段不再带标题(用户裁定 2026-08-03):占位字样已说明是什么,省一行高度
                    // ★ 署名日期已挪去生成键左边(用户裁定 2026-08-03),设置条里不应再有它
                    Assert(!rbSrc.Contains("_signDate"), "★ 署名日期不在设置条 —— 它跟着【生成】走");
                    // ★ 两卡的输入栏均分【卡内高度】,但底部留视觉间隔(用户反馈 2026-08-03)
                    Assert(rbSrc.Contains("var them = Fields(") && rbSrc.Contains("var mine = Fields("),
                           "★ 对方/我方信息的输入栏撑满卡高(不再是顶部堆一堆)");
                    var iFields = rbSrc.IndexOf("static FrameworkElement Fields(", StringComparison.Ordinal);
                    Assert(iFields >= 0 && rbSrc.IndexOf("GridUnitType.Star", iFields, StringComparison.Ordinal) > iFields
                        && rbSrc.IndexOf("new Thickness(0, 0, 0, 6)", iFields, StringComparison.Ordinal) > iFields,
                           "★ 均分用星号行高,且底部留 6px —— 撑满不等于贴死(用户补充)");
                    Assert(rbSrc.Contains("AddressField(_myAddr, _myPostal") && rbSrc.Contains("AddressField(_theirAddr, _theirPostal"),
                           "★ 地址是融合两栏:上行街道 + 下行右侧邮编地区(不靠用户手敲回车断行)");
                    // ★ 融合栏得是【一块底色】—— 否则中间那条线把圆角切开,看着像两个控件(用户反馈 2026-08-03)
                    var iAf = rbSrc.IndexOf("static FrameworkElement AddressField(", StringComparison.Ordinal);
                    Assert(iAf >= 0 && rbSrc.IndexOf("box.SetResourceReference(Border.BackgroundProperty, \"BgSunken\")", iAf, StringComparison.Ordinal) > iAf,
                           "★ 地址+邮编共用一层底色与圆角(视觉上就是一格)");
                    Assert(rbSrc.Contains("static readonly ReplyMedium[] MediumOrder = { ReplyMedium.Message, ReplyMedium.Email, ReplyMedium.Paper }"),
                           "★ 载体界面顺序 消息-邮件-信件(与枚举值解耦)");
                    Assert(!rbSrc.Contains("new Slider"), "★ 语气也改竖直指针,不再用滑条(三档 + 省横向空间)");
                    Assert(rbSrc.Contains("TheApp.Settings.TranslationPool"),
                           "★ 语言只列设置里勾的语言池,不翻整本目录");
                    // ★ 语言列封顶在【列】上,不是给面板定宽(渲染图 2026-08-03:
                    //   定宽在最小窗下被 Grid 剪掉右边,自定义的「+」整个没了)
                    Assert(rbSrc.Contains("new ColumnDefinition { MaxWidth = 158 }") && !rbSrc.Contains("VerticalAlignment.Top, Width = 158"),
                           "★ 语言/问候/祝福那列窄了跟着缩 —— 「+」在最小窗下也得点得到");
                    // ★ 指针的命中区是【整行】的透明盖板(用户反馈:圆点 9px 太难点中)
                    var rbPr = Slice(rbSrc, "FrameworkElement PointerRow(", "/// <summary>语气与载体同款竖直指针");
                    Assert(rbPr is not null && rbPr.Contains("Grid.SetColumnSpan(row, 2)")
                           && rbPr.Contains("Background = Brushes.Transparent") && rbPr.Contains("IsHitTestVisible = false"),
                           "★ 载体/语气整行可点:圆点与档名让出命中,交给一块跨两列的透明盖板");
                    // ★ 邮编+地区跟其余输入框一样左对齐(用户裁定 2026-08-03)
                    Assert(rbSrc.Contains("Cell(line2, ph2, right: false)"),
                           "★ 邮编+地区左对齐,不再独一份右对齐");
                    Assert(rbSrc.Contains("CustomAwareItem(cb, items[i], i, greeting)") && rbSrc.Contains("PreviewMouseLeftButtonDown"),
                           "★ 自定义那几条【在下拉里】各带一个 × ——删除入口要在看得见它的地方");
                    // ★ 老断言的措辞已经不成立了:浮窗现在【只管加】,删除入口搬进了下拉里那一条自己后面。
                    //   继续用同一串字符命中新代码 = 一条没有独立判据的僵尸断言(复核 2026-08-03)。
                    var rbAsk = Slice(rbSrc, "void AskCustom(bool greeting", "static FrameworkElement Labeled(");
                    Assert(rbAsk is not null && !rbAsk.Contains("RemoveCustom("),
                           "★ 自定义浮窗只管【加】—— 删在下拉里那一条自己后面,一件事不留两个入口");
                }
                var rpSrc = TryReadSource(Path.Combine("Views", "ReplyPanel.cs"));
                if (rpSrc is not null)
                {
                    var iDraft = rpSrc.IndexOf("Sect(\"我想回复的内容\"", StringComparison.Ordinal);
                    var iIn = rpSrc.IndexOf("Sect(\"来信(可留空)\"", StringComparison.Ordinal);
                    Assert(iDraft >= 0 && iIn > iDraft, "★ 我想回复的内容在上、来信在下(用户裁定对调)");
                    Assert(rpSrc.Contains("Sect(\"对话记录\""), "★ 生成结果下方多一块【对话记录】(四板块)");
                    var cvR = TryReadSource(Path.Combine("Views", "ChatView.cs"));
                    if (cvR is not null)
                    {
                        // ★ 场景切换条排在会话卡【内】—— 与翻译空间同一层级(用户裁定 2026-08-03)
                        var rScene = Slice(cvR, "if (_replyScene)", "_chatSceneHead = chatHead;");
                        Assert(rScene is not null && rScene.Contains("DockPanel.SetDock(chatHead, Dock.Top)")
                               && !rScene.Contains("ConvCard(new ReplyPanel())"),
                               "★ 回信的场景切换条在会话卡内,不是悬在页面底色上");
                        Assert(rScene is not null
                               && rScene.IndexOf("rWrap.Children.Add(rBar)", StringComparison.Ordinal)
                                  < rScene.IndexOf("rWrap.Children.Add(rCard)", StringComparison.Ordinal),
                               "★ 设置条仍在卡外,且 Dock 的先加(写反了卡就不填充 —— 这条只有渲染图看得见)");
                        // ★ 回信用【信封】图标:PDF 是产出格式之一(还只在纸质载体下出现),不能拿它当功能标识
                        Assert(cvR.Contains("SceneChip(IconName.Mail, \"回信\""),
                               "★ 回信场景 chip 用信封图标");
                    }
                    foreach (var sk in new[] { Services.Skin.Ink, Services.Skin.Breeze, Services.Skin.Warm })
                        Assert(Theme.Icons.PathFor(Theme.IconName.Mail, sk) is { Length: > 0 } mp
                               && mp != Theme.Icons.PathFor(Theme.IconName.Pdf, sk),
                               $"★ {sk}:信封有自己的路径,不是把 Pdf 的抄了一份");
                    Assert(rpSrc.Contains("署名日期"), "★ 署名日期改放在生成键左边(跟产出挺在一起)");
                    // ★ 动作排在板块【底部】:挤进标题行的话,最小窗宽下 DockPanel 先满足右侧按钮,
                    //   标题只剩一个字的宽 —— 「生成结果」被压成竖排(渲染图 2026-08-03 拍到)
                    // ★★ 刷新不许碰树:清空重建会把带焦点的 _signDate 移出去 ——
                    //   轻则两父节点异常,重则 TSF 持锁期 FailFast(catch 不住,crash.log 里都留不下)。
                    Assert(!rpSrc.Contains("_resultBtns.Children.Clear()"),
                           "★★ 生成键那一排只建一次,刷新只改 Visibility/IsEnabled");
                    var rpCtor = Slice(rpSrc, "void BuildActionsOnce()", "void PickSignDate()");
                    Assert(rpCtor is not null && rpCtor.Contains("_dateWrap.Children.Add(_dateField)"),
                           "★ 署名日期那一格只在装配那一次进树");
                    // ★★ 这条护栏此前是【假的】(复核 2026-08-03):结束标记 void BuildLog( 在起点
                    //   void RefreshActions( 的【前面】,Slice 从起点往后找不到它 -> 恒 null ->
                    //   `null?.Contains(...) != true` 恒真。切片必须先断言取到了,再断言内容。
                    var rpRefresh = Slice(rpSrc, "void RefreshActions(ReplyDoc d, bool busy)", "\n    }");
                    Assert(rpRefresh is not null, "★ 切片得真的取到(取不到就跳过 = 一条永远绿的假断言)");
                    Assert(rpRefresh is null || !rpRefresh.Contains("Children.Add("),
                           "★★ 刷新那段一个 Children.Add 都不许有 —— 带焦点的控件在刷新期被重挂 = FailFast");
                    // ★ 署名日期不再是自由输入(用户裁定 2026-08-03):点开是日期选择浮窗,
                    //   滚轮复用日程/待办那一套 WheelPicker.Date —— 不另造一种日期控件。
                    Assert(!rpSrc.Contains("TextBox _signDate") && rpSrc.Contains("WheelPicker.Date(picked"),
                           "★ 署名日期改成日期选择浮窗(复用既有滚轮),不再让人敲字");
                    Assert(rpSrc.Contains("Save(d => d.SignDate = \"\");") && rpSrc.Contains("ReplyState.SignDateFormat"),
                           "★ 浮窗两个出口:【留空 = 生成那天】与【选定则钉死】—— 两者不是一回事");
                    Assert(rpSrc.Contains("DockPanel.SetDock(actions, Dock.Bottom)"),
                           "★ 生成/署名日期/图标那排在板块底部,不挤标题行");
                    Assert(rpSrc.Contains("Rebuild();   // ★ 先画一遍"),
                           "★ 构造时先画一遍 —— 否则离屏渲染诊断里这一块永远是空的(骗自己)");
                }
                // ★★ 事故复现(2026-08-03,崩溃日志逐字对应):动作排每次重建都把字段级 _signDate
                //   塞进一个【新建的】容器,而旧容器还是它的父 —— 第二次走到就抛
                //   "指定的元素已经是另一个元素的逻辑子元素"。更狠的是它跑在 _signDate 自己的
                //   TextChanged 里:带焦点的 TextBox 在 TSF 持锁期被重挂,WPF 直接 FailFast 杀进程。
                //   ★ 必须调【两次】—— 一次不会红。
                {
                    var app0 = (App)System.Windows.Application.Current;
                    var keep = app0.Reply.SessionId;
                    app0.Reply.SetSession(null);
                    app0.Reply.Doc.Medium = Services.ReplyMedium.Paper;   // 只有纸质载体才摆署名日期
                    try
                    {
                        var rp = new Views.ReplyPanel();
                        rp.Rebuild();
                        rp.Rebuild();
                        Assert(true, "★★ 载体=信件时连着重建两次不抛 —— 动作排只建一次、刷新只改属性");
                    }
                    catch (Exception ex)
                    {
                        Assert(false, "★★ 回信动作排重建又炸了(" + ex.GetType().Name + "):" + ex.Message);
                    }
                    app0.Reply.SetSession(keep);
                }
                // ★ 不按【生成】就不该冒出一条会话(用户反馈 2026-08-03:
                //   "即使对话记录没有任何记录的条例,也会在会话列表中创建会话记录")
                {
                    var rst2 = new Services.ReplyState();
                    rst2.SetSession(null);
                    rst2.Doc.TheirName = "改个对方称呼";
                    rst2.Doc.Medium = Services.ReplyMedium.Paper;
                    rst2.Touch();
                    // 诚实说明:这一条只能证明【状态层本身】不会因为编辑而建会话
                    //   (Doc 取值器与 Touch 本来就没有建会话的代码路径)。用户报的那个 bug
                    //   在 ReplyBar.SaveDoc/Txt 里多调了一句 EnsureSession —— 守它的是下一条源码断言。
                    Assert(rst2.SessionId is null, "状态层:改设置/填对方信息本身不建会话(真正的护栏是下一条)");
                }
                var rbSrc0 = TryReadSource(Path.Combine("Views", "ReplyBar.cs"));
                Assert(rbSrc0 is null || !rbSrc0.Contains("EnsureSession()"),
                       "★ 设置条里不许再调 EnsureSession —— 建会话的时机只有一个:按下生成");

                var rSess = new Services.ChatCenter().NewSession(null, "chat", replyLetter: true);
                Assert(rSess.ReplyLetter && !Services.ChatCenter.CanMove(rSess), "回信会话与场景会话同规:不可搬走");
            }

            // ---- 文件翻译:PDF 预览(用户裁定 2026-08-03:"PDF 预览功能也做出来,目前是空的")----
            // ★★ 这条是【行为】断言:真的写一份 PDF、真的让系统组件渲染、真的检查像素回来了。
            //   源码断言在这件事上没有意义 —— "代码里有 PdfPreview" 不等于"渲染得出来"。
            {
                var pdfPath = Path.Combine(Path.GetTempPath(), "localai-selftest-min.pdf");
                var ok = false;
                try { File.WriteAllBytes(pdfPath, MinimalPdf("Hello PDF preview")); ok = true; }
                catch { }
                if (ok)
                {
                    // ★ 丢到线程池上跑:WinRT 的异步在 STA 上直接 .Result 有把自己等死的风险
                    var r = System.Threading.Tasks.Task.Run(async () =>
                    {
                        var doc = await Services.PdfPreview.OpenAsync(pdfPath);
                        if (doc is null) return (Pages: -1, W: 0, H: 0);
                        var bmp = await doc.RenderAsync(0, 400);
                        return (Pages: doc.PageCount, W: bmp?.PixelWidth ?? 0, H: bmp?.PixelHeight ?? 0);
                    }).GetAwaiter().GetResult();
                    Assert(r.Pages == 1, $"★★ PDF 打得开且页数认得出(实得 {r.Pages})—— 打不开就该如实说,不画空白页假装成功");
                    Assert(r.W == 400 && r.H > 400, $"★★ PDF 真的渲染出像素:按给定宽度出图、高度按页面比例(实得 {r.W}x{r.H})");
                    // 坏文件必须【打不开】,不能返回一个空文档冒充成功
                    var badPath = Path.Combine(Path.GetTempPath(), "localai-selftest-bad.pdf");
                    try { File.WriteAllText(badPath, "not a pdf at all"); } catch { }
                    var bad = System.Threading.Tasks.Task.Run(async () => await Services.PdfPreview.OpenAsync(badPath)).GetAwaiter().GetResult();
                    Assert(bad is null, "★ 坏文件返回 null(界面据此如实说'打不开'),不返回一个空壳文档");
                    // 源码文本断言只在能读到源码时跑(发布后的单文件 exe 里没有源码 ——
                    //   不包这一层的话,发布产物跑自检会无缘无故地红两条)
                    var ftp = TryReadSource(Path.Combine("Views", "FileTransPanel.cs"));
                    if (ftp is not null)
                    {
                        Assert(!ftp.Contains("PDF 预览尚未接入") && ftp.Contains("_pdf.PageCount"),
                               "★ 面板里那句『PDF 预览尚未接入』要跟着删掉 —— 接上了还留着就是界面在说假话");
                        Assert(ftp.Contains("if (seq != _renderSeq) return;"),
                               "★ 异步渲染要认序号:连着导入两份 PDF 时,晚回来的那张不许盖掉新的");
                    }
                }
            }

            // ---- 气温曲线:颜色按温度分段 + 与逐小时那排同一根时间轴(2026-08-02 用户裁定)----
            {
                var cPurple = Views.HomeView.TempColor(-20);
                var cBlue = Views.HomeView.TempColor(4);      // 落在 1.5~6.5 的纯色段里
                var cGreen = Views.HomeView.TempColor(18);
                var cAmber = Views.HomeView.TempColor(31);    // 29.5~32.5
                var cRed = Views.HomeView.TempColor(50);
                Assert(cPurple != cBlue && cBlue != cGreen && cGreen != cAmber && cAmber != cRed, "五个温度带各是各的颜色");
                Assert(cBlue.B > cBlue.R && cGreen.G > cGreen.R && cGreen.G > cGreen.B && cAmber.R > cAmber.B && cRed.R > cRed.G,
                       "蓝偏蓝 / 绿偏绿 / 黄偏暖 / 红偏红(别把色相调没了)");
                // ★ 用户要的"灰调":每个颜色的最大与最小分量差不许太大 —— 那是饱和度的直接度量
                foreach (var (nm, c) in new[] { ("紫", cPurple), ("蓝", cBlue), ("绿", cGreen), ("黄", cAmber), ("红", cRed) })
                    Assert(Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) <= 0x90,
                           $"★ {nm}是【灰调】不是大红大紫(用户裁定:饱和度压下来)");
                // 纯色段:8–28 之间任取几点都得是同一个绿(否则"8-28 用绿线"不成立)
                Assert(Views.HomeView.TempColor(10) == cGreen && Views.HomeView.TempColor(20) == cGreen && Views.HomeView.TempColor(26) == cGreen,
                       "★ 8–28℃ 整段是同一个绿(过渡只发生在阈值附近的窄带里)");
                // ★★ 过渡的【中点正好是阈值温度】—— 用户明确要求"在温度阈值处开始过渡,而不是顶点处"
                foreach (var (thr, lowC, highC) in new[] { (0.0, cPurple, cBlue), (8.0, cBlue, cGreen), (28.0, cGreen, cAmber), (34.0, cAmber, cRed) })
                {
                    var mid = Views.HomeView.TempColor(thr);
                    Assert(Math.Abs(mid.R - (lowC.R + highC.R) / 2.0) <= 1
                        && Math.Abs(mid.G - (lowC.G + highC.G) / 2.0) <= 1
                        && Math.Abs(mid.B - (lowC.B + highC.B) / 2.0) <= 1,
                           $"★ {thr:0}℃ 正好是两色的中点(过渡钉在阈值上,不是钉在折线顶点上)");
                }
                // 单调:同一段内温度升高不会往回跳色
                Assert(Views.HomeView.TempColor(-3) == cPurple && Views.HomeView.TempColor(36) == cRed, "带外是纯色,不会一直渐变下去");
            }
            var hvCurve = TryReadSource(Path.Combine("Views", "HomeView.cs"));
            if (hvCurve is not null)
            {
                Assert(hvCurve.Contains("var x = (0.5 + (p.At - t0).TotalHours / step) * cell;"),
                       "★ 曲线的横轴照抄逐小时那排:第 k 格的时刻文字居中 = (k+0.5)×格宽,峰谷才对得上刻度");
                Assert(hvCurve.Contains("var t0 = HourlyOrigin(place);") && hvCurve.Contains("var now = HourlyOrigin(place);"),
                       "★ 曲线与逐小时同一个起点(那座城的当地整点)");
                Assert(hvCurve.Contains("TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(p.TimeZoneId)).DateTime; }\r\n        catch { now = DateTime.Now; }")
                    || hvCurve.Contains("catch { now = DateTime.Now; }"),
                       "认不出时区就退回本机时间(不拿猜来的偏移去挪读数)");
                Assert(!hvCurve.Contains("var now = DateTime.Now;\r\n        for (int k = 0; k < grid.Children.Count"),
                       "★ 逐小时不再拿本机时间当所有城市的当前时间(时差有多少就错多少格)");
                Assert(hvCurve.Contains("MappingMode = BrushMappingMode.Absolute"),
                       "描边用竖直渐变(y 与温度线性,颜色分界因此正好是那条温度的水平线)");
            }

            // ---- 工作空间是【标签】不是【归属】(2026-08-01 用户裁定)----
            //   起因:唯一性判据是 (机器, 路径) 且不看工作空间,于是同一个文件夹全局只有一个项目;
            //   可 WorkspaceKey 是单值的,导致"在 B 里选了 A 占用的文件夹"时人被送去那个项目,
            //   而它在 B 的列表与主页方块里根本不出现 —— 建也建不了、找也找不到。
            {
                var root = Path.Combine(Path.GetTempPath(), "localai-wstag-" + Guid.NewGuid().ToString("N")[..6]);
                var pt = new Services.ProjectCenter();
                var inA = pt.Create("A 空间建的", root, null, Services.ProjectScope.Personal, "chat");
                Assert(inA.Spaces.Count == 1 && inA.PrimarySpace == "chat", "新建的项目只挂它建出来的那个空间");

                var inB = pt.Create("B 空间想建同一个文件夹", root, null, Services.ProjectScope.Personal, "translation");
                Assert(inB.ProjectId == inA.ProjectId && pt.Items.Count == 1, "同路径仍然只有一个项目(不因为换了空间就建出第二个)");
                var pid = inA.ProjectId;
                Assert(pt.Find(pid)!.InWorkspace("chat") && pt.Find(pid)!.InWorkspace("translation"),
                       "★ 在 B 里选同路径 = 给它【补上 B 的标签】,而不是把它搬走");
                Assert(pt.Ongoing("chat").Any(x => x.ProjectId == pid) && pt.Ongoing("translation").Any(x => x.ProjectId == pid),
                       "★ 同一个项目在两个工作空间的【进行中】里都看得见(这正是原来那个坑)");
                Assert(pt.Ongoing().Count(x => x.ProjectId == pid) == 1, "★ 是一个项目挂两个标签,不是两份复制");
                pt.SetStatus(pid, Services.ProjectStatus.Done);
                Assert(pt.Completed("chat").Any(x => x.ProjectId == pid) && pt.Completed("translation").Any(x => x.ProjectId == pid),
                       "已完成清单同样按标签显示");
                pt.SetStatus(pid, Services.ProjectStatus.Active);

                pt.MoveToWorkspace(pid, "courses");
                Assert(pt.Find(pid)!.Spaces.Count == 1 && pt.Find(pid)!.InWorkspace("courses"),
                       "★【移动】= 换标签:原来的空间都不再挂着(与【也放到】后果不同,菜单里必须分开给)");
                pt.AddToWorkspace(pid, "assets");
                pt.AddToWorkspace(pid, "assets");
                Assert(pt.Find(pid)!.Spaces.Count == 2, "★【也放到】= 加标签;重复加不会加出两份");

                Assert(pt.RemoveFromWorkspace(pid, "assets") && pt.Find(pid)!.Spaces.Count == 1, "可以从某个工作空间移除");
                Assert(!pt.RemoveFromWorkspace(pid, "courses") && pt.Find(pid)!.Spaces.Count == 1,
                       "★ 不许把【最后一个】标签也去掉 —— 那样它在任何空间都看不见,等于一次误点就藏起来了");
                pt.AddToWorkspace(pid, "assets");
                pt.RemoveFromWorkspace(pid, "courses");
                Assert(pt.Find(pid)!.PrimarySpace == "assets", "去掉主标签后,剩下的顶上来当主标签(不会留下空的主标签)");

                // 老存档:只有单个 WorkspaceKey、没有 AlsoIn —— 照旧可见,不需要迁移
                var legacyWs = new Services.ProjectCenter();
                legacyWs.Import(new List<Services.Project>
                {
                    new("old-ws", "老存档的", "", "chat", Services.ProjectScope.Personal, DateTime.Now, OwnerMemberId: Services.MemberContext.Current),
                });
                Assert(legacyWs.Find("old-ws")!.Spaces.SequenceEqual(new[] { "chat" }) && legacyWs.Ongoing("chat").Any(x => x.ProjectId == "old-ws"),
                       "★ 老存档(只有单个 WorkspaceKey)照旧可见 —— 这次改动不需要迁移");

                // 合并同路径重复时,标签取【并集】
                var pmu = new Services.ProjectCenter();
                pmu.Import(new List<Services.Project>
                {
                    new("mu-1", "旧的", "", "chat", Services.ProjectScope.Personal, DateTime.Now.AddDays(-1), FolderPath: root, OwnerMemberId: Services.MemberContext.Current),
                    new("mu-2", "新的", "", "courses", Services.ProjectScope.Personal, DateTime.Now, FolderPath: root, OwnerMemberId: Services.MemberContext.Current),
                });
                pmu.MergeDuplicateFolders();
                Assert(pmu.Items.Count == 1 && pmu.Items[0].InWorkspace("chat") && pmu.Items[0].InWorkspace("courses"),
                       "★ 合并同路径重复项目时标签取并集 —— 不让它从其中一个空间凭空消失");

                var rt = System.Text.Json.JsonSerializer.Deserialize<List<Services.Project>>(
                             System.Text.Json.JsonSerializer.Serialize(pmu.Items))!;
                Assert(rt.Count == 1 && rt[0].Spaces.Count == 2, "★ 标签能落盘也能读回(重启后不会退回单一空间)");
            }
            // ---- 会话跟随【自己的】工作空间(2026-08-01 用户裁定 + 同日审计)----
            //   项目可以同时挂多个空间,但会话各归各的空间:WorkspaceKey 决定它进不进翻译历史、
            //   点开时按哪套界面渲染。跨空间的会话在项目列表里看得见,点开【转到它自己的空间】去。
            {
                var pw = new Services.ProjectCenter();
                var cw = new Services.ChatCenter();
                var proj = pw.Create("跨空间项目", null, null, Services.ProjectScope.Personal, "chat");
                var sChat = cw.NewSession(proj.ProjectId, "chat");
                var sTrans = cw.NewSession(proj.ProjectId, "translation");
                pw.AddToWorkspace(proj.ProjectId, "translation");

                Assert(cw.SessionsOf(proj.ProjectId).Count() == 2, "项目会话列表里两个空间的会话都在(不按空间过滤)");
                var spaces = cw.SessionSpacesOf(proj.ProjectId);
                Assert(spaces.Contains("chat") && spaces.Contains("translation") && spaces.Count == 2,
                       "★ 数得出这个项目的会话散在哪些工作空间(给摘标签当护栏)");

                // ★ 摘标签的护栏:那儿还有会话就摘不掉
                Assert(!pw.RemoveFromWorkspace(proj.ProjectId, "translation", spaces),
                       "★ 那个空间还有本项目的会话时,标签【摘不掉】—— 摘了那些会话在那儿就没有任何入口");
                Assert(pw.Find(proj.ProjectId)!.InWorkspace("translation"), "被拦下之后标签原样还在");

                // ★ 移动 = 换标签,但有会话的空间保留;而且【一个会话的 WorkspaceKey 都不许被改写】
                pw.MoveToWorkspace(proj.ProjectId, "courses", spaces);
                var moved = pw.Find(proj.ProjectId)!;
                Assert(moved.PrimarySpace == "courses" && moved.InWorkspace("chat") && moved.InWorkspace("translation"),
                       "★ 移动项目时,还有会话待着的空间标签保留(否则那些会话找不回来)");
                Assert(cw.Find(sChat.SessionId)!.WorkspaceKey == "chat" && cw.Find(sTrans.SessionId)!.WorkspaceKey == "translation",
                       "★★ 移动项目【不改写会话的所属空间】—— 那是会话自己的身份,改了正好制造翻译历史污染");

                // 翻译历史按 WorkspaceKey 取、不看 ProjectId:所以那条翻译项目会话【本来就在历史里】,
                // 而聊天那条【绝不能】混进去 —— 这正是"点开跨空间会话要转过去"要保护的东西。
                var th = cw.AllTranslationSessions().Select(x => x.SessionId).ToList();
                Assert(th.Contains(sTrans.SessionId) && !th.Contains(sChat.SessionId),
                       "★ 翻译历史只收 WorkspaceKey=translation 的(含项目会话)—— 聊天那条不许混进来");

                // 把会话搬走之后,标签才摘得掉
                cw.MoveSessionToWorkspace(sTrans.SessionId, "courses");
                Assert(pw.RemoveFromWorkspace(proj.ProjectId, "translation", cw.SessionSpacesOf(proj.ProjectId)),
                       "把那些会话搬走之后,标签就摘得掉了(护栏不是死锁)");
            }
            var ccJump = TryReadSource(Path.Combine("Services", "ChatCenter.cs"));
            if (ccJump is not null)
                Assert(!ccJump.Contains("public void SetSessionsWorkspace"),
                       "★ 不再有【项目搬空间就批量改写会话所属空间】这个方法(它正好制造要防的污染)");
            var mwJump = TryReadSource("MainWindow.xaml.cs");
            if (mwJump is not null)
            {
                Assert(mwJump.Contains("public void NavigateToSession(string workspaceKey, string? projectId, string sessionId)"),
                       "★ 有一条能带 sessionId 的跳转通路 —— 只有 NavigateToProject 的话,跳过去打开的是它替你挑的另一条会话");
                Assert(mwJump.Contains("if (!Workspaces.Known(workspaceKey)) return;"),
                       "认不出的工作空间 key 不跳(老存档里会留着已删掉的空间)");
            }
            var cvXws = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvXws is not null)
            {
                Assert(cvXws.Contains("if (foreign) { JumpToOwnWorkspace(s); return; }"),
                       "★ 点跨空间会话 = 转到它自己的空间打开,不在本空间就地打开");
                Assert(cvXws.Contains("Icons.Make(ProjectUi.SpaceIcon(s.WorkspaceKey), 12"),
                       "跨空间会话用【它那个空间的图标】做前缀标记(与置顶点、同传麦克风同一位置)");
                // ★ 负向:不许拿"灰化"表示跨空间 —— 这个库里降透明度/换灰键已经表示【只读】或【不可用】,
                //   而跨空间会话恰恰是能点、点了会带你走。两种灰两个意思,必然被读错。
                //   只看每一处 if (foreign) 后面那一小段:降级样式只可能写在那里面
                //   (整行别处的 Opacity 是三点按钮的 hover 显隐,与这条规则无关)。
                var segs = new List<string>();
                for (int at = 0; (at = cvXws.IndexOf("if (foreign)", at, StringComparison.Ordinal)) >= 0; at += 12)
                    segs.Add(cvXws.Substring(at, Math.Min(300, cvXws.Length - at)));
                Assert(segs.Count >= 2 && segs.All(x => !x.Contains("Opacity") && !x.Contains("FgMuted") && !x.Contains("IsEnabled")),
                       "★ 跨空间会话【一个像素都不降】(不灰、不禁用)—— 那些样式在本库里表示【不可用】,与实际行为相反");
                Assert(cvXws.Contains("s.WorkspaceKey == _wsKey && !TheApp.Chat.MessagesOf(s.SessionId).Any()"),
                       "★★ 按 + 复用空会话只在【本空间】的会话上成立 —— 这条污染路径一次都不经过会话行,标记拦不住它");
                Assert(cvXws.Contains("foreach (var w in Workspaces.Visible(TheApp.Settings))") && cvXws.Contains("if (w.Key == s.WorkspaceKey) continue;"),
                       "★「发送到工作空间」按【会话自己】的空间排除,且只列左栏可见的空间");
                Assert(cvXws.Contains("TheApp.Projects.Ongoing(s.WorkspaceKey)"),
                       "「移动到项目」列的是会话自己那个空间的项目(拿视图的空间列,搬完还是跨空间的)");
            }
            var wsVis = TryReadSource(Path.Combine("Views", "Workspaces.cs"));
            if (wsVis is not null)
                Assert(wsVis.Contains("public static List<Def> Visible(Services.AppSettings settings)"),
                       "有【左栏真的显示着】的工作空间清单(目的地菜单一律用它)");
            var puVis = TryReadSource(Path.Combine("Views", "ProjectUi.cs"));
            if (puVis is not null)
            {
                Assert(puVis.Contains("var visible = Workspaces.Visible(TheAppSettings);"),
                       "★ 移动/也放到 的目的地只列左栏可见的空间(在扩展里关掉 = 用户说过不要它)");
                Assert(puVis.Contains("foreach (var k in p.Spaces)") && puVis.Contains("RemoveProjectFrom(p, key)"),
                       "「从工作空间移除」照旧列出全部已挂标签(含隐藏的),否则藏起来的会变成摘不掉的死标签");
                Assert(!puVis.Contains("Chat.SetSessionsWorkspace"), "移动项目不再连带改写会话所属空间");
            }
            var ppOrigin = TryReadSource(Path.Combine("Views", "ProjectPickerView.cs"));
            if (ppOrigin is not null)
                Assert(ppOrigin.Contains("for (int i = 0; i < p.Spaces.Count; i++)"),
                       "★ 已删除项目方块把【全部】工作空间标签都标出来(原先只标主标签,恢复后又变多个,前后不一致)");

            var pcTag = TryReadSource(Path.Combine("Services", "ProjectCenter.cs"));
            if (pcTag is not null)
            {
                Assert(!pcTag.Contains("p.WorkspaceKey == workspaceKey"),
                       "★ 列表不再按【单一归属】筛选(留一处就够让多空间项目在那儿隐身)");
                Assert(pcTag.Contains("p.InWorkspace(workspaceKey)"), "列表一律走 InWorkspace(按标签判定)");
            }
            var puTag = TryReadSource(Path.Combine("Views", "ProjectUi.cs"));
            if (puTag is not null)
                Assert(puTag.Contains("移动到工作空间") && puTag.Contains("也放到工作空间"),
                       "★ 菜单把【移动(换标签)】与【也放到(加标签)】分开给 —— 后果差太远,不能靠猜");
            var hvTag = TryReadSource(Path.Combine("Views", "HomeView.cs"));
            if (hvTag is not null)
                Assert(!hvTag.Contains("p.WorkspaceKey"), "★ 主页方块不再拿单一归属当作项目所在的空间(它跨空间汇总)");

            var peDup = TryReadSource(Path.Combine("Views", "ProjectEditor.cs"));
            if (peDup is not null)
            {
                Assert(peDup.Contains("转跳至该项目") && peDup.Contains("FindByFolder"), "★ 路径已有项目时,创建按钮变【转跳至该项目】");
                Assert(peDup.Contains("加入本工作空间并打开") && peDup.Contains("AddToWorkspace"),
                       "★ 那个项目不在当前工作空间时,按钮说清楚它会【加标签并打开】(而不是搬走)");
                Assert(peDup.Contains("已删除项目") && peDup.Contains("已完成项目"), "提示里说明该项目当前在哪(进行中/已完成/已删除)");
                Assert(peDup.Contains("MachineOptions") && peDup.Contains("本机"), "可选择文件夹所在机器(本机 / 已配对电脑)");
            }
            var hubDev = TryReadSource(Path.Combine("Services", "HubClient.cs"));
            if (hubDev is not null)
                Assert(hubDev.Contains("KnownDevices") && hubDev.Contains("CacheDevices"),
                       "★ 远程机器清单只来自【真的拿到过】的设备表(拿不到就只有本机,不摆假列表)");
            var appMerge = TryReadSource("App.xaml.cs");
            if (appMerge is not null)
                Assert(appMerge.Contains("MergeDuplicateFolders"), "启动加载存档后合并同路径重复项目");

            // ---- 项目:共享删除垃圾篓 / 已完成按空间 / 分支 / 可见范围(2026-07-30 用户裁定)----
            {
                var pc4 = new Services.ProjectCenter();
                var a = pc4.Create("空间A项目", null, null, Services.ProjectScope.Personal, "chat");
                var b = pc4.Create("空间B项目", null, null, Services.ProjectScope.Personal, "translation");
                pc4.SetStatus(a.ProjectId, Services.ProjectStatus.Done);
                pc4.SetStatus(b.ProjectId, Services.ProjectStatus.Done);
                Assert(pc4.Completed("chat").Any(x => x.ProjectId == a.ProjectId) && !pc4.Completed("chat").Any(x => x.ProjectId == b.ProjectId),
                       "★ 已完成项目【不共享】,按工作空间隔离");
                // 删除:跨空间【共享】一个垃圾篓
                pc4.Delete(a.ProjectId); pc4.Delete(b.ProjectId);
                var trash = pc4.DeletedProjects().Select(x => x.ProjectId).ToList();
                Assert(trash.Contains(a.ProjectId) && trash.Contains(b.ProjectId), "★ 已删除项目【跨工作空间共享】一个垃圾篓");
                // 30 天自动清
                pc4.SweepExpiredDeletedProjects(DateTime.Now.AddDays(Services.ProjectCenter.TrashRetentionDays + 1));
                Assert(!pc4.DeletedProjects().Any(), "已删除项目超 30 天自动清除");

                // 分支:复制成【准备中】的新项目(新 Id)
                var src = pc4.Create("原项目", "F", new[] { "att1" }, Services.ProjectScope.Family, "chat");
                pc4.SetStatus(src.ProjectId, Services.ProjectStatus.Done);
                var br = pc4.Branch(src.ProjectId);
                Assert(br.ProjectId != src.ProjectId && br.Status == Services.ProjectStatus.Preparing && br.FolderPath == "F",
                       "开启分支 = 复制成新的准备中项目(同文件夹)");
                Assert(pc4.Ongoing("chat").Any(x => x.ProjectId == br.ProjectId), "分支出的新项目进【进行中/准备中】");

                // 可见范围可改
                pc4.SetScope(src.ProjectId, Services.ProjectScope.Personal);
                Assert(pc4.Find(src.ProjectId)!.Scope == Services.ProjectScope.Personal, "三点菜单可改可见范围(个人/家庭)");
            }
            var puScope = TryReadSource(Path.Combine("Views", "ProjectUi.cs"));
            if (puScope is not null)
            {
                Assert(puScope.Contains("SetScope") && puScope.Contains("可见范围"), "项目 ⋯ 菜单可改可见范围");
                Assert(puScope.Contains("同一网络里其它 PC"), "家庭范围解释:同网其它 PC 共享可见可操作");
            }
            var pkBoard = TryReadSource(Path.Combine("Views", "ProjectPickerView.cs"));
            if (pkBoard is not null)
            {
                Assert(pkBoard.Contains("ShowDeletedBoard") && pkBoard.Contains("ShowCompletedBoard"), "项目抽屉有【已删除/已完成项目】覆盖板块");
                Assert(pkBoard.Contains("_header") && pkBoard.Contains("选择一个项目"), "常驻提示(未选时提示选项目;不挤排版)");
                Assert(pkBoard.Contains("PinButton"), "项目方块用 hover 置顶按钮(像主页)");
                Assert(pkBoard.Contains("OriginLabel") && pkBoard.Contains("page == Page.Deleted"),
                       "★ 已删除项目方块标出【来自哪个工作空间】(回收站跨空间共用)");
                Assert(pkBoard.Contains("void UpdateBack()") && pkBoard.Contains("_page == Page.Grid"),
                       "★ 左上角返回键按层级逐级后退(已完成/已删除 → 进行中 → 普通会话)");
                Assert(pkBoard.Split("‹ 返回项目").Length - 1 == 1, "「返回项目」只有一个入口(说明框里不再重复放)");
                Assert(pkBoard.Contains("HeaderHeight") && pkBoard.Contains("card.Height = HeaderHeight"),
                       "★ 各页面说明框【统一高度】(切页时项目方块位置不动、好对齐)");
                Assert(pkBoard.Contains("HeaderState.Ongoing") && pkBoard.Contains("HeaderState.Done") && pkBoard.Contains("HeaderState.Trash"),
                       "说明框按页面分三种状态(进行中 / 已完成 / 已删除)");
                Assert(pkBoard.Contains("StateOngoingBorder") && pkBoard.Contains("StateTrashFill"),
                       "★ 状态配色走【皮肤令牌】(描边 + 浅填充),不在视图里写死颜色");
            }
            var cvRO = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvRO is not null)
            {
                Assert(cvRO.Contains("BuildReadonlyProject") && cvRO.Contains("body.Opacity = 0.7"), "已删除/已完成项目:会话区灰色只读");
                Assert(cvRO.Contains("恢复此项目") && cvRO.Contains("彻底删除此项目"), "已删除项目输入区 = 恢复/彻底删除");
                Assert(cvRO.Contains("继续此项目") && cvRO.Contains("开启此项目分支"), "已完成项目输入区 = 继续/开分支");
                Assert(cvRO.Contains("BuildTrashBoard") && cvRO.Contains("‹ 返回会话") && !cvRO.Contains("Flyout.Show(anchor, \"已删除会话\""),
                       "已删除会话改成覆盖式板块(不再浮窗)");
                Assert(cvRO.Contains("attachmentsBelow: heroNow"),
                       "空态:附件放输入框下方、幽灵提示浮顶,不顶动居中框");
            }

            var pcd = new Services.ProjectCenter();
            var pdel = pcd.Create("待删", Path.Combine(Path.GetTempPath(), "x"), null, Services.ProjectScope.Personal);
            pcd.Delete(pdel.ProjectId);
            // ★ 新语义:软删除 —— 进【已删除项目】共享垃圾篓,记录还在(可恢复),不动磁盘文件夹
            Assert(pcd.Find(pdel.ProjectId)?.DeletedAt is not null, "删除项目 = 软删除(记录仍在、带 DeletedAt)");
            Assert(!pcd.Ongoing().Any(x => x.ProjectId == pdel.ProjectId) && pcd.DeletedProjects().Any(x => x.ProjectId == pdel.ProjectId),
                   "删除的项目离开进行中、进【已删除项目】");
            pcd.RestoreProject(pdel.ProjectId);
            Assert(pcd.Find(pdel.ProjectId)?.DeletedAt is null && pcd.Ongoing().Any(x => x.ProjectId == pdel.ProjectId), "可从已删除项目恢复");
            pcd.Delete(pdel.ProjectId);
            pcd.PurgeProject(pdel.ProjectId);
            Assert(pcd.Find(pdel.ProjectId) is null, "彻底删除后记录才消失");

            // 会话三点菜单(取消右键)+ 项目删除的红色二次确认 + 圆角按钮 + 箭头在右
            if (chatSrc is not null)
            {
                Assert(chatSrc.Contains("SessionDots") && chatSrc.Contains("BuildSessionMenu") && !chatSrc.Contains("host.ContextMenu = "), "会话改用三个点菜单(不再右键)");
                foreach (var it in new[] { "重命名会话", "置顶会话", "删除会话", "新建项目…(并移入)" })
                    Assert(chatSrc.Contains(it), $"会话菜单含「{it}」");
                Assert(chatSrc.Contains("Grid.SetColumn(arrow, 2)"), "项目箭头在会话列表右侧");
                Assert(chatSrc.Contains("Shake(") && chatSrc.Contains("MessagesOf(s.SessionId).Any()"), "已有空会话则震荡提醒、不重复建(按上下文判定)");
            }
            var projUi = TryReadSource(Path.Combine("Views", "ProjectUi.cs"));
            if (projUi is not null)
            {
                Assert(projUi.Contains("删除项目") && projUi.Contains("ConfirmDialog.Show"), "项目菜单含删除项目 + 自绘二次确认(修:点了没反应)");
                Assert(projUi.Contains("置顶项目"), "项目菜单可置顶");
                Assert(!projUi.Contains("MessageBox.Show"), "项目菜单不再用系统弹窗");
            }
            var dlg = TryReadSource(Path.Combine("Views", "ConfirmDialog.cs"));
            if (dlg is not null)
                Assert(dlg.Contains("WindowStyle.None") && dlg.Contains("DangerFilled"), "确认框是自绘窗口(非系统 MessageBox)");
            var ctlMenu = TryReadSource(Path.Combine("Theme", "Controls.xaml"));
            if (ctlMenu is not null)
            {
                Assert(ctlMenu.Contains("TargetType=\"ContextMenu\"") && ctlMenu.Contains("TargetType=\"MenuItem\""), "三点菜单走我们的风格(不再系统原生)");
                Assert(ctlMenu.Contains("TargetType=\"Separator\""), "菜单分隔线也主题化");
            }
            var ctl2 = TryReadSource(Path.Combine("Theme", "Controls.xaml"));
            if (ctl2 is not null)
                Assert(ctl2.Contains("TargetType=\"Button\""), "按钮有统一圆角样式(发送等按钮不再方角)");
            // ★★ P4-S9:IsModelEnabled / SetModelEnabled 已删。它们存的是自造 key,
            //   而**没有任何代码再读它** —— 一个存得下却谁也不看的偏好就是"假开关":
            //   用户拨了它以为生效了,实际什么都没发生。
            //   「启用哪些组件」的权威现在在中枢(快照的 intended_resident)。
            {
                var apSrc = TryReadSource(Path.Combine("Services", "AppSettings.cs"));
                Assert(apSrc is null || !apSrc.Contains("public bool IsModelEnabled"),
                       "★ AppSettings 里那个没人读的模型启用开关已删(假开关)");
                var pickerSrc = TryReadSource(Path.Combine("Views", "ComponentPicker.cs"));
                // ★ 本文件的惯例:**发布环境没有源码 → 跳过接线自检**(见 TryReadSource 的说明)。
                //   我原来写成 `is not null && ...` = 【要求】源码存在,于是打包后必红 ——
                //   出包门禁当场拦下(2026-08-05)。这正是今早修好的那道门禁在干真活。
                Assert(pickerSrc is null || pickerSrc.Contains("FetchCatalogAsync"),
                       "★ 组件清单向中枢取,不在客户端维护第二份");
                Assert(pickerSrc is null || !pickerSrc.Contains("IsModelEnabled"),
                       "★ 面板不读本地那份停用列表(权威只有中枢一处)");
            }

            // 扩展拖动把手:用透明命中块,不是拿描边 Path 当命中区
            var extGrip = TryReadSource(Path.Combine("Views", "ExtensionsView.cs"));
            if (extGrip is not null)
                Assert(extGrip.Contains("gripPath") && extGrip.Contains("IsHitTestVisible = false"),
                       "拖动把手用整块透明命中区(描边 Path 不接管命中)");

            var mwSrc2 = TryReadSource("MainWindow.xaml.cs");
            if (mwSrc2 is not null)
            {
                Assert(mwSrc2.Contains("foreach (var w in Workspaces.Ordered"), "导航按统一清单、用户排定顺序渲染工作空间");
                Assert(mwSrc2.Contains("visible: TheApp.Settings.IsWorkspaceVisible(def.Key)"),
                       "被关掉的工作空间不进左栏");
                Assert(mwSrc2.Contains("if (visible) target.Children.Add(b);") && mwSrc2.Contains("_nav.Add((item, b));"),
                       "★ 但它【照样登记进 _nav】—— 登记漏了等于这个键从此失效:人正待在那个空间时把它藏起来,之后换语言 Navigate 会静默失效");
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
                // ★★ 右列宽【不再按地点数算】—— 天气已经改成竖排一列到底,
                //   宽度与"有几个城市"无关;按张数算会让整页比例随时区变(2 张时右列宽到 1/2)。
                Assert(homeTodo.Contains("_todoColumn.Width = new GridLength(Math.Max(240, contentW / 3.0 - WeatherGap))"),
                       "★ 右列宽 = 内容宽的 1/3(稳定值,不随地点数变)");
                Assert(homeTodo.Contains("if (_todoVisible || _weatherVisible)"),
                       "★★ 待办隐藏 ≠ 右列归零 —— 天气也在这一列,归零会把它压成 0 宽彻底消失");
                Assert(homeTodo.Contains("if (!_todoVisible && !_weatherVisible) Grid.SetColumnSpan(calPanel, 2)"),
                       "★ 只有右列真的空了才让日历横跨两列(否则盖住天气)");
                var uiSrc = TryReadSource(Path.Combine("Views", "Ui.cs"));
                if (uiSrc is not null)
                    Assert(uiSrc.Contains("用两条【居中的矩形】拼"), "+ 号用居中矩形绘制(不再偏移)");

                // ★ 横向拖拽换位已停用(2026-07-31 改成竖排折叠):只剩一张展开卡 + 两行折叠,
                //   按 dx 算的横拖无处可去,而且会与悬停展开打架。
                // ★ 拖拽排序【改成纵向】回来了(用户裁定 2026-07-31):只从右下角手柄起手,
                //   其余卡片带挤开动画;首格(当前所在地)仍锁定。
                Assert(homeTodo.Contains("gripZone.PreviewMouseLeftButtonDown") && homeTodo.Contains("BeginCityDrag"),
                       "★ 只有右下角手柄能起手拖拽(不是整张卡)");
                Assert(homeTodo.Contains("AnimateShift") && homeTodo.Contains("TranslateTransform.YProperty"),
                       "★ 其余卡片【带动画】挤开/归位");
                // ★★ 这条断言【原来钉的正是那个 bug】(收起走 260ms 动画)。
                //   动画期间卡片的布局位置每帧都在变,而位移是相对布局位置算的 ——
                //   基准一直在动,画面就跟不上手(用户报的"拖拽不跟鼠标")。
                //   现在钉住修法:立即收起 + 当场把布局跑完,基准从起手那刻就是死的。
                Assert(homeTodo.Contains("SetWeatherFocus(-1, animate: false)")
                       && homeTodo.Contains("_weatherStack.UpdateLayout()"),
                       "★★ 拖起来【立即】把三张全收起并跑完布局 —— 基准不动,位移才跟得住手");
                Assert(homeTodo.Contains("_dragFromY = e.GetPosition(_weatherStack).Y"),
                       "★ 拖拽基准取天气栈自己的坐标系(整页外面套着 ScrollViewer,以页面为基准会凭空多一段)");
                Assert(homeTodo.Contains("var minDy = (1 - from) * CityRowStride")
                       && homeTodo.Contains("dy = Math.Clamp(dy, minDy, maxDy)"),
                       "★★ 拖拽位移【夹在板块内】—— 不许超出最上/最下那一格(用户反馈会拖到板块以外)");
                Assert(homeTodo.Contains("if (index <= 0 || index >= _cityCards.Length) return;"),
                       "★ 首格(当前所在地)不可拖动");
                Assert(homeTodo.Contains("if (_draggingCity) return;"),
                       "★ 拖拽期间不响应悬停展开(否则卡片一边被拖一边变高)");
            }
            // ---------------------------------------------------------------- 周时间轴(2026-07-31 一批裁定)
            var tl = TryReadSource(Path.Combine("Views", "WeekTimeline.cs"));
            if (tl is not null)
            {
                Assert(tl.Contains("public const double DefaultTop = 8;")
                       && tl.Contains("public const double DefaultHours = 22 - DefaultTop;")
                       && tl.Contains("_top = ClampTop(DefaultTop);"),
                       "★ 默认视野 = 早上八点到晚上十点(用户裁定)");
                Assert(tl.Contains("public const double MinHours = 6"),
                       "★ 时间轴放到最大 = 6 小时(用户裁定)");
                Assert(tl.Contains("public const double DayMin = -1") && tl.Contains("public const double DayMax = 25"),
                       "★★ 竖轴可视域是 -1~25 点而不是 0~24 —— 露出前后各一小时,跨零点的日程才看得全、拖得出来");
                Assert(tl.Contains("public const double SnapHours = 0.5"),
                       "★ 拖动改时间的颗粒 = 半小时(用户裁定)");
                Assert(!tl.Contains("Zoom(1.5, 0.5)") && !tl.Contains("\uff0b"),
                       "★ 加减号缩放按钮已移除(缩放归滚轮)");
                Assert(tl.Contains("Canvas _canvas = new() { ClipToBounds = true, Background = Brushes.Transparent }")
                       && tl.Contains("_gutter = new() { Width = GutterWidth, ClipToBounds = true, Background = Brushes.Transparent"),
                       "★★ 画布与刻度列都铺透明底 —— Background 为 null 的容器【不参与命中测试】,"
                       + "滚轮事件根本不从那里发出(用户反馈:只有可交互元素才能缩放)");
                // ★★ 手势按【区域】分工(用户裁定 2026-07-31):
                //   左侧刻度列 = 时间尺(滚轮缩放 / 拖也缩放);右侧表格 = 内容(滚轮上下滑 / 拖也上下滑)。
                // ★★ 第四版(用户裁定):不分区域，两边都是【左键拖 = 缩放、滚轮 = 上下滑】。
                Assert(tl.Contains("_gutter.MouseWheel += (_, e) => e.Handled = WheelPan(e.Delta);")
                       && tl.Contains("_canvas.MouseWheel += (_, e) => e.Handled = WheelPan(e.Delta);"),
                       "★★ 两边滚轮都是上下滑");
                Assert(tl.Contains("BeginScale(e.GetPosition(_gutter).Y, _gutter.ActualHeight, _gutter)")
                       && tl.Contains("BeginScale(e.GetPosition(_canvas).Y, _canvas.ActualHeight, _canvas)"),
                       "★★ 两边左键拖都是缩放");
                Assert(tl.Contains("DragMode.Move") && tl.Contains("var dayShift = colW > 0"),
                       "★ 拖日程中间 = 整体位移(竖向改时刻、横向换一天)");
                Assert(tl.Contains("static List<(int Index, int Col, int Total)> LayOut"),
                       "★ 重叠的日程【平分当天宽度】(按重叠簇分列)");
                Assert(tl.Contains("var wrapInside = !wideEnough && height >= WrapInsideAbove;")
                       && tl.Contains("var lyMid = yTop + (yBottom - yTop) / 2 - LabelLine / 2;")
                       && tl.Contains("var lx = (roomRight >= 16 && !rightBusy) ? right : x + 2;")
                       && tl.Contains("placed.Add(new Rect(x, y0, Math.Max(10, bw), Math.Max(3, y1 - y0)));"),
                       "★★ 外置标题【永远画、且永远与条同高】—— 同一个 y 是最硬的归属提示。"
                       + "前后错过两次:往上让会飘得认不出主、被占就不画会直接消失"
                       + "(用户反馈:多个共享宽度时名字被省略成'…',什么都看不见)");
                Assert(tl.Contains("var rightBusy = placedLabels.Any(o =>"),
                       "★★ 右边那个位置不能落在【别人的色块】上 —— 落上去名字会认错主;"
                       + "挤不开就盖在自己头上,宁可截断成一两个字");
                Assert(tl.Contains("BorderThickness = new Thickness(1, edgeThick, 1, edgeThick),")
                       && tl.Contains("var edgeThick = height >= EdgeLinesAbove ? 2.0 : 1.0;"),
                       "★★ 定时日程用【描边框】,上下两条边加粗 = 起止标记"
                       + "(描边不会把底下的昼夜带盖死 —— 一眼能看出日程是白天还是夜里)");
                Assert(tl.Contains("Background = new SolidColorBrush(back),") ,
                       "★ 全天日程用【实心色块】,与定时的描边框分开");
                Assert(tl.Contains("CalendarGroups.ColorOf(ev.CalendarGroup)") && tl.Contains("CalendarGroups.TextOn(back)"),
                       "★★ 日程块用【分类的颜色】,字色按底色亮度反选(深底白字/浅底黑字)");
                Assert(tl.Contains("Math.Clamp(y - 8, 0, Math.Max(0, h - 15))"),
                       "★ 刻度标签上下都夹进可视区(用户报过\"底部的 0 点被吃掉了一半\")");
                Assert(tl.Contains("Places.CoordOf(Places.Current()) is { } coord") && tl.Contains("SunClock.ForDay("),
                       "★★ 夜晚压暗用【本地算】的日出日落(SunClock),坐标认不出就整块不画 —— 不编一个 6:00–18:00");
                Assert(tl.Contains("if (_evDrag is not null) return;") && tl.Contains("void OnDataChanged()"),
                       "★ 拖动期间挡掉 Changed 重建 —— 否则手底下那个元素会被换掉(拖着拖着就脱手)");
                // ★★ 抖动的根:每动一下就把整块重画一遍。重画会销毁重建手底下那个 Border,
                //   而且一旦拖到与别的日程重叠、重叠簇列数就变,方块会当场横向缩一半、拖回来又弹回全宽。
                Assert(tl.Contains("Canvas.SetTop(dg.Box, y0);") && tl.Contains("dg.Box.Height = Math.Max(3, y1 - y0);"),
                       "★★ 拖日程是【实时预览】——只挪这一个方块,不重建(用户反馈:拖顶部/底部会抖)");
                Assert(tl.Contains("if (!dg.Moved) { OnEditEvent?.Invoke(dg.Ev0); return; }")
                       && tl.Contains("CalendarData.Update(live with { Start = dg.Start, End = dg.End })"),
                       "★★ 数据在【松手时提交一次】;没拖动 = 点了一下 -> 打开编辑抽屉");
                // ★★ 点一下日程块它就死死跟着鼠标走 —— 根因是松手时【只在真的拖动过才收尾】。
                //   同一个漏洞的另一面：编辑抽屉也永远打不开。
                Assert(tl.Contains("EndScale();") && tl.Contains("// ★★ 【无条件】收尾"),
                       "★★ 松手时【无条件】收尾(不能只在拖动过才收)");
                Assert(tl.Contains("if (mode == DragMode.Start && !seg.IsFirst) mode = DragMode.Move;")
                       && tl.Contains("if (mode == DragMode.End && !seg.IsLast) mode = DragMode.Move;"),
                       "★★ 一条跨天日程被切成好几段,每一段只对【它真的持有的那一端】负责:"
                       + "顶边只有起始段能拖、底边只有末段能拖");
                Assert(tl.Contains("readonly record struct Seg(CalendarEvent Ev, double S, double E, bool IsFirst, bool IsLast)")
                       && tl.Contains("static DateTime LastDayOf(CalendarEvent ev)"),
                       "★ 跨天定时日程按天切段(每段知道自己是不是头/尾)");
                Assert(tl.Contains("box.CornerRadius = new CornerRadius(seg.IsFirst ? 3 : 0"),
                       "★ 被切断的那一端【不收圆角】—— 平口 = 还没完,圆角 = 就到这儿");
                Assert(tl.Contains("protected override void OnLostMouseCapture"),
                       "★ 捕获丢失也收尾(拖到窗口外松手/Alt+Tab),否则拖拽状态永远挂着");
                // ★★ 全天条带：拿掉月历左下角那份当日列表之后，
                //   全天日程就【看不见也点不着】了 —— 时间轴画的是 TimedOn，全天的根本不在里面。
                //   这是"把入口连同界面元素一起删掉"的典型漏洞，必须有东西接住。
                Assert(tl.Contains("void BuildAllDay()") && tl.Contains("CalendarData.SpansIn(_weekStart, 7)"),
                       "★★ 时间轴有【全天】条带 —— 全天日程不在 TimedOn 里，不补就看不见也点不着");
                Assert(tl.Contains("chip.MouseLeftButtonUp += (_, e) => { e.Handled = true; OnEditEvent?.Invoke(captured); }"),
                       "★ 全天条可点 —— 走的仍是日历那个编辑抽屉");
                Assert(tl.Contains("const double AllDayStripHeight = AllDayRows * AllDayBarHeight + 3;")
                       && tl.Contains("_allDay = new() { Height = TopBlockHeight")
                       && tl.Contains("const double TopBlockHeight = HeadHeight + AllDayStripHeight;")
                       && tl.Contains("const int AllDayRows = 2;"),
                       "★★ 表头与全天条带合成一块、常驻固定高(不要那种有才出现的一栏)");
                Assert(tl.Contains("if (coverHeader)") && tl.Contains("Color.FromArgb(0x33, back.R, back.G, back.B)")
                       && tl.Contains("_head.IsHitTestVisible = false;"),
                       "★★ 跨天的全天日程把横轴的【周几/几号】也囊括进去"
                       + "(表头铺一层同色淡底与下面的实心条连成一根横幅;表头不参与命中,横幅仍然点得着)");
                Assert(tl.Contains("var lanes = Math.Clamp(perCell[(row, col)], 1, AllDayMaxLanes);")
                       && tl.Contains("var slotW = colW / lanes;")
                       && tl.Contains("var blocked = new bool[AllDayRows, 7];"),
                       "★★ 两行【都能放任何一条】(先长后短贪心),单日的还能在同一格里共享宽度"
                       + " —— 之前跨天的只有一行,两条一重叠就挤掉一条,那个「+1」就是这么来的");
                Assert(tl.Contains("$\"+{hiddenNames.Count}\"") && tl.Contains("_allDayMore.MouseLeftButtonUp")
                       && tl.Contains("MenuHost.Show(m, _allDayMore)"),
                       "★ 没画出来的【点击列出是哪几条】(全局 ToolTip 已关,挂提示上等于没解释),计数摆在【左侧刻度列】"
                       + "—— 钉在画布右端的话会盖住周日那一格的日期与条");
                Assert(tl.Contains("ToolTip = box.ToolTip,") && tl.Contains("if (e.OriginalSource is not Canvas) return;"),
                       "★★ 外置标题悬停出全名、点一下能编辑 —— 前提是画布【只在真的按在自己身上时】才抢捕获;"
                       + "无条件捕获会把画布里任何可点元素的点击整个吃掉(CaptureMode.Element 下松手不经过子元素)");
                // ---- 对抗式审计(2026-07-31)确认的三条,钉住 ----
                Assert(tl.Contains("e.Handled = WheelPan(e.Delta)") && tl.Contains("bool WheelPan(int delta)") && tl.Contains("bool PanTo("),
                       "★★ 滚轮【只有真的滑动了才吞】—— 到顶/到底还吞的话,光标停在这里整页就永远滑不动");
                Assert(tl.Contains("sealed record EventDrag(CalendarEvent Ev0, DragMode Mode, Point From, Border Box,")
                       && tl.Contains("var moved = (cur.Y - dg.From.Y) / h * _hours;")
                       && tl.Contains("var t = Math.Max(SnapUp(s0 + SnapHours), Snap(e0 + moved));"),
                       "★★ 拖动改时间是【绝对】口径(从起手那一刻重算 + 对绝对时刻吸附 + 守卫夹住)"
                       + " —— 增量口径会吞掉四舍五入的余量,慢拖时边界跑得比光标快近一倍,还留反向死区");
                // ★ 夹取的【边界本身】也要落在半小时上 —— 否则顶到下限会得到 9:07 这种脏时刻
                //   (用户报的"现在可以到 15 分钟"正是这么来的)。
                Assert(tl.Contains("static double SnapUp(double hours)") && tl.Contains("static double SnapDown(double hours)")
                       && tl.Contains("SnapDown(e0 - SnapHours)"),
                       "★★ 连夹取的边界都落在半小时格子上(半小时颗粒不许被边界漏掉)");
                Assert(tl.Contains("readonly DispatcherTimer _tick") && tl.Contains("if (DateTime.Today != _tickDay)")
                       && tl.Contains("void SyncTick()"),
                       "★ 「此刻」红线/昼夜带/今天高亮会随时间走,且不可见时停表");

                Assert(tl.Contains("_gutter.ToolTip = tip;") && tl.Contains("_canvas.ToolTip = tip;"),
                       "★ 手势都没有可见按钮 —— 两边共用同一句提示(手势已经不分区域了)");
                Assert(!tl.Contains("_weekStart:M月d日}"),
                       "★ 顶栏那行日期标签已去掉(横轴上每一格都写着日期了)");
                Assert(tl.Contains("NavKey(\"现在\"") && !tl.Contains("NavKey(\"‹\""),
                       "★ 只留一个键且叫【现在】(不叫「今」,左右两个已去掉)");
                // ★★ 它一度【点不动】—— 因为挂在 _head 里,而 _head 刚被设成 IsHitTestVisible = false
                //   好让底下的全天横幅点得着。IsHitTestVisible 是往下传染的,
                //   子元素想单独恢复也恢复不了 —— 只能挪到表头外面去。
                Assert(tl.Contains("topBlock.Children.Add(_navCell);") && !tl.Contains("_head.Children.Add(_navCell);"),
                       "★★ 导航键放在表头【外面】(挂在里面会被 IsHitTestVisible=false 一起传染成不可点)");
                Assert(tl.Contains("_top = ClampTop(DateTime.Now.TimeOfDay.TotalHours - _hours / 2);"),
                       "★ 按「今」【保持当前缩放】,只把此刻挪到视野中间");
                // ★ 【点空白新建】已按用户要求取消：新建只走板块标题栏那个「+」。
                Assert(!tl.Contains("_createAt") && !tl.Contains("OnCreateAt?.Invoke(when)"),
                       "★ 表格空白处不再新建 —— 那里只剩一件事:按住拖 = 缩放");
                // ---- 对抗式审计第二轮(2026-07-31 深夜)确认的高危项 ----
                Assert(tl.Contains("var t = Math.Clamp(Snap(s0 + moved), 0, 24 - SnapHours);"),
                       "★★ 整体位移的上界只管【开始留在这一天】—— 原来用 DayMax-dur 当时长预算,"
                       + "一条 22:00→次日 02:00 的一拖就被夹到 21:00,而且再也拖不回去,松手还会写进数据");
                Assert(tl.Contains("var hiStart = SnapDown(e0 - SnapHours);")
                       && tl.Contains("hiStart <= 0 ? 0 : Math.Clamp("),
                       "★★ 先算上界再夹 —— 结束早于 00:30 的短日程会让 Math.Clamp 的 min>max 直接抛异常"
                       + "(表现是鼠标一动就弹一次「出错了」)");
                Assert(tl.Contains("var org = dg.Mode == DragMode.Move ? start.Date : dg.SegDay;"),
                       "★★ 预览的纵向原点用【这一段所在那一列的日期】—— 跨天尾段用开始那天当原点会差 24×N 小时,"
                       + "手一动方块就跳出可视区");
                Assert(tl.Contains("if (_evDrag is not null) return true;"),
                       "★ 拖日程时滚轮不平移 —— 平移会重画并把手底下那个方块换掉(滚一下就脱手)");
                Assert(tl.Contains("IsHitTestVisible = false,") && tl.Contains("var slotRight = x + bw + slotGap;"),
                       "★★ 全天的外置名字不抢点击也不越栏 —— 否则同一格两条时,"
                       + "点右边那条弹出的是左边那条的抽屉");
                Assert(!tl.Contains("双击空白处 = 新建"),
                       "★ 提示语不再教一个已经取消的手势");

                Assert(tl.Contains("if (ev.Start.Date > day.Date || LastDayOf(ev) < day.Date) continue;"),
                       "★★ 【按区间筛】而不是往回找 N 天 —— 回看窗口一旦短于日程长度,"
                       + "超出那几天就整段不画(看着像那天没有这条日程)");
                var ce2 = TryReadSource(Path.Combine("Views", "CalendarEditor.cs"));
                if (ce2 is not null)
                {
                    Assert(ce2.Contains("var endOffset = existing is not null") && ce2.Contains("d0.AddDays(endOffset) + endAt"),
                           "★★ 非全天日程也能跨天:结束 = 开始那天 + 【结束日偏移】+ 结束时刻");
                    Assert(ce2.Contains("endOffset = 1; endOffsetAuto = true;") && ce2.Contains("if (endOffset > 0) return;"),
                           "★★ 把结束拨到早于开始 = 【跨到次日】,而不是把开始一起往前拖"
                           + "(旧做法等于替用户改了他刚刚没动的那一头,而且让跨天永远表达不出来)");
                    Assert(ce2.Contains("var endOffsetAuto = false;") && ce2.Contains("endOffsetAuto = false;      // ★ 手动设过就不再自动收回"),
                           "★★ 区分【自动推出去的跨天】与【手动按 + 设的】—— 不分的话,"
                           + "拖转盘扫过一下就把日程永久掰成跨天(用户从头到尾没打算跨天)");
                    Assert(ce2.Contains("warn?.Invoke(\"结束日期不能早于开始"),
                           "★ 全天结束日被夹住时【当场说一声】—— 不能界面写 7/28、存进去却是 7/31(所见非所得)");
                    Assert(ce2.Contains("if (next < 0 || (next == 0 && endAt <= startAt)) return;"),
                           "★ 退回「当日」时若起止会颠倒,就不许退 —— 不靠保存时偷偷夹一下");
                }
            }

            var sun = TryReadSource(Path.Combine("Services", "SunClock.cs"));
            if (sun is not null)
            {
                Assert(sun.Contains("SunDay.AllNight") && sun.Contains("SunDay.AllDay"),
                       "★ 极昼/极夜有明确返回值(高纬度那几天真的不日出/不日落,不是算错了)");
                Assert(!sun.Contains("http") && !sun.Contains("Http"),
                       "★★ 日出日落是【算】的不是【拉】的 —— 一个字节都不出网");
            }

            var calSrc2 = TryReadSource(Path.Combine("Views", "CalendarView.cs"));
            if (calSrc2 is not null)
            {
                Assert(calSrc2.Contains("public bool HideDayArea") && calSrc2.Contains("_dayArea.Visibility = HideDayArea ?")
                       && calSrc2.Contains("_hideDayArea = value; Rebuild();"),
                       "★ 合并板块里藏掉左下角当日区(那里重复,而且把月历挤得显示不全)");
                Assert(calSrc2.Contains("public double LeftGutter") && calSrc2.Contains("panel.Margin = new Thickness(_leftGutter, 0, 0, 0)"),
                       "★★ 月历七列左边留出与时间轴同宽的刻度列 —— 上下两块的同一天才对得齐");
                Assert(calSrc2.Contains("for (int w = 0; w < 6; w++)"),
                       "★ 月排布【恒画 6 行】—— 5 行月/6 行月高度不同的话,下方时间轴会随翻月上下跳");
                Assert(calSrc2.Contains("public void FocusWeekStart"),
                       "★ 时间轴翻周 -> 月历跟过去(否则上下两块各说各的周)");
                Assert(calSrc2.Contains("public void OpenEditorAt"),
                       "★ 拿掉「+ 新增日程」之后仍有新建路径(时间轴空白处双击)");
                // ★★ 对抗式审计确认:翻月只动 _anchor 不动 _selected ——
                //   在 8 月的页面上点「+ 新增日程」会把日程建到 7 月去,
                //   而定时日程的编辑器里没有日期字段,用户无从纠正 —— 静默写错数据。
                Assert(calSrc2.Contains("void SelectDay(DateTime day, bool notify = true)")
                       && calSrc2.Contains("void CarrySelectionInto(DateTime anchor)")
                       && calSrc2.Contains("CarrySelectionInto(_anchor);"),
                       "★★ 选中日【跟进新视野】且只有 SelectDay 一个入口");
                // 全文只允许 SelectDay 里那一处给 _selected 赋值
                Assert(calSrc2.Split("_selected = ").Length == 3,   // 声明一处 + SelectDay 一处
                       "★ 不再有绕过 SelectDay 直接给 _selected 赋值的地方");
                Assert(calSrc2.Split("_dayArea.Visibility = HideDayArea").Length == 3,
                       "★ Rebuild 与 AfterPage 两处都认 HideDayArea"
                       + " —— AfterPage 原来无条件把当日区放回来,主页第一次翻页就会冒出来");
            }

            if (homeTodo is not null)
            {
                Assert(homeTodo.Contains("timeline.OnCreateAt = when => calView.OpenEditorAt(when)"),
                       "★★ 新建入口没有随按钮一起消失 —— 换成了时间轴上双击");
                Assert(homeTodo.Contains("Height = CalendarView.MonthOnlyHeight"),
                       "★ 合并板块用【只有月历】的高度(用含当日区的 268 会把月历压矮、最后一行裁掉)");
                Assert(homeTodo.Contains("_cityMeta[i].Text = diffText;"),
                       "★ 展开卡右侧只留一个时段词(用户反馈\"右侧有两个晚上\")");
                Assert(!homeTodo.Contains("\" · 当前\""),
                       "★ 城市名后面不再有「· 当前」(右侧那列已经写着「本地」,同一件事说两遍)");
                Assert(!homeTodo.Contains("TempBar(i,"),
                       "★ 天气摘要行【没有滑块】(用户裁定:长得像滑块却不能拖,本身就是误导)");
                Assert(homeTodo.Contains("i == _places.Count - 1 ? 0 : CityGap"),
                       "★★ 末尾那张天气卡不留下边距 —— 否则它看得见的底边比框底高出 10px,"
                       + "而日历对的是框底,看起来就是对不齐");
                Assert(homeTodo.Contains("Margin = new Thickness(0, 0, 0, PanelGap),")
                       && homeTodo.Contains("(_todoVisible || _weatherVisible) ? 12 : 0, PanelGap);")
                       && homeTodo.Contains("MinHeight = WeatherStackHeight + PanelGap"),
                       "★ 日历与天气取同一个下边距 -> 下沿齐平,且与「正在进行的项目」留出间隔");
                Assert(homeTodo.Contains("var wantTop = cursorY - CollapsedCityHeight + 11;"),
                       "★★ 拖拽按【光标绝对位置】反推卡片该在哪 —— 手柄在展开卡的右下角,"
                       + "卡一收起只剩 34px,抓点凭空上移一大截;从 0 起算的话卡片会停在光标上方");
                Assert(homeTodo.Contains("Background = System.Windows.Media.Brushes.Transparent,"),
                       "★ 天气宿主铺透明底 —— 卡与卡之间那道 10px 的缝本来不参与命中测试,"
                       + "光标停在缝里 220ms 就被当成离开了板块,展开的卡啤地跳回第 0 张");
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
            // ★ 按【常量算】而不是写死一个 872 —— 行高改了断言要跟着变,
            //   否则它会一直绿着而窗口已经装不下了(审计实测过:1000 下有 1 个项目方块就溢出 47px)。
            var needH = 38 + 32 + 107
                      + (Views.CalendarView.PanelHeight + 62) + 12
                      + Views.HomeView.WeatherStackHeight + 12
                      + 130;
            Assert(Views.Layout.PreferredWindowHeight >= needH,
                   $"建议高度够放下主页内容(需 {needH},实得 {Views.Layout.PreferredWindowHeight})");
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

            // ---- 日历:本地优先 + 与 Apple 的增量合并(2026-07-30 用户裁定)----
            {
                DateTime D(int d, int h = 9) => new DateTime(2026, 8, d, h, 0, 0);
                var existing = new List<Views.CalendarEvent>
                {
                    new(D(1), D(1, 10), "已有会议", "我", "家庭"),
                    new(D(2), D(2, 10), "牙医", "我", "个人", ExternalId: "apple-uid-1"),
                };
                var incoming = new List<Views.CalendarEvent>
                {
                    new(D(1), D(1, 10), "已有会议", "我", "家庭"),                 // 完全重复 -> 不该再加
                    new(D(2, 0), D(2, 10), "牙医(标题被改过)", "我", "个人", ExternalId: "apple-uid-1"), // 同 UID -> 不该覆盖/重复
                    new(D(3), D(3, 10), "新的:体检", "我", "家庭"),               // 没有的 -> 应加入
                    new(D(4), D(4, 10), "", "我", "家庭"),                        // 空标题 -> 不并入
                };
                var before = existing.Count;
                var added = Views.CalendarData.MergeInto(existing, incoming);
                Assert(added == 1, "增量合并只加【没有的】那 1 条");
                Assert(existing.Count == before + 1, "总数只 +1(重复/同 UID/空 都没进)");
                Assert(existing.Count(x => x.Title == "已有会议") == 1, "★ 内容重复的不重复加入");
                Assert(existing.Count(x => x.ExternalId == "apple-uid-1") == 1 && existing.Any(x => x.Title == "牙医"),
                       "★ 同一 Apple UID 不覆盖已有(标题保持原样)");
                Assert(existing.Any(x => x.Title == "新的:体检"), "没有的日程被加入");
                Assert(!existing.Any(x => string.IsNullOrWhiteSpace(x.Title)), "★ 空日程不并入(不覆盖已有)");

                // 双向:本地独有的(remote 没有)-> 待推给 Apple
                var local = new List<Views.CalendarEvent> { new(D(1), D(1, 10), "本地独有", "我", "家庭"), new(D(2), D(2, 10), "两边都有", "我", "家庭") };
                var remote = new List<Views.CalendarEvent> { new(D(2), D(2, 10), "两边都有", "我", "家庭") };
                var toPush = Views.CalendarData.LocalOnly(local, remote);
                Assert(toPush.Count == 1 && toPush[0].Title == "本地独有", "★ 本地独有的挑出来待推给 Apple(双向增量)");

                // 持久化往返(日历现在明文落盘)
                Views.CalendarData.Events.Clear();
                Views.CalendarData.Add(new Views.CalendarEvent(D(5), D(5, 10), "落盘日程", "我", "个人", CreatedByAi: true));
                Assert(Views.CalendarData.Events[0].Id is { Length: > 0 }, "Add 自动补稳定 Id");
                var cjson = System.Text.Json.JsonSerializer.Serialize(Views.CalendarData.Export(), StoreJson);
                Views.CalendarData.Events.Clear();
                Views.CalendarData.Import(System.Text.Json.JsonSerializer.Deserialize<List<Views.CalendarEvent>>(cjson, StoreJson));
                Assert(Views.CalendarData.Events.Count == 1 && Views.CalendarData.Events[0].Title == "落盘日程" && Views.CalendarData.Events[0].CreatedByAi,
                       "日历 JSON 往返(含 AI 标记)");
                Views.CalendarData.Events.Clear();
            }
            var calEd = TryReadSource(Path.Combine("Views", "CalendarEditor.cs"));
            if (calEd is not null)
            {
                Assert(calEd.Contains("CalendarData.Add") && calEd.Contains("CalendarData.Update") && calEd.Contains("CalendarData.Remove"),
                       "★ 日程编辑器【真的写入】本机(不再一律拒绝保存)");
                Assert(!calEd.Contains("如实拒绝") && !calEd.Contains("暂时无法"), "去掉'尚未连接一律拒绝'的旧文案");
            }
            var appCal = TryReadSource("App.xaml.cs");
            if (appCal is not null)
            {
                Assert(appCal.Contains("CalendarData.Import") && appCal.Contains("CalendarData.Export"), "日历纳入本地存档读写");
                Assert(appCal.Contains("hadCalendar"), "日历示例独立播种(老用户也补一次,且删了不复活)");
            }

            // ---- 接线自检:防"补丁静默失配导致整段成死代码" ----
            // 已经踩过三次:替换字符串没匹配上,函数还在但调用点被删,编译通过、断言全绿、功能没了。
            // 这里直接对源码做结构断言 —— 我改动的正是这些接线点。
            var appSrc = TryReadSource("App.xaml.cs");
            var calSrc = TryReadSource(Path.Combine("Views", "CalendarView.cs"));
            if (appSrc is null || calSrc is null)
                Console.WriteLine("  SKIP  接线自检(发布环境无源码,开发/CI 下才跑)");
            else
            {
                // ★ 示例数据已停止播种(用户要求 2026-07-31)—— 改成钉"清理发生在建窗口之前",
                //   同样的理由:建窗口之后再改数据,界面已经按旧表画完了。
                var purgeIdx = appSrc.IndexOf("PurgeDemoDataOnce();", StringComparison.Ordinal);
                var winIdx = appSrc.IndexOf("_main = new MainWindow();", StringComparison.Ordinal);
                Assert(purgeIdx >= 0 && winIdx >= 0 && purgeIdx < winIdx,
                       "★ 示例清理发生在建窗口【之前】(否则界面先按旧表画完,示例会一闪而过)");
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
                    // ★ 天气改成【一展开 + 其余折叠】的竖排(用户裁定 2026-07-31)
                    Assert(homeSrc.Contains("void SetWeatherFocus(int i, bool animate)") && homeSrc.Contains("_cityCards"),
                           "★ 天气:一张展开、其余折叠(靠高度动画,不是换元素)");
                    Assert(homeSrc.Contains("const double WeatherStackHeight") && homeSrc.Contains("Height = WeatherStackHeight"),
                           "★★ 整块【固定总高】—— 不管展开哪一个,三者始终在同一个框内(用户裁定)");
                    // ★★ 原来写死的是"减两张",只在正好 3 个地点时成立 ——
                    //   而地点数是会变的:Places.Load 会把与当前所在地重名的那个去重
                    //   (系统时区改成中国标准时间就只剩 2 张),用户也可以自己加城市。
                    Assert(homeSrc.Contains("WeatherStackHeight - (n - 1) * (CollapsedCityHeight + CityGap))"),
                           "★★ 展开高按【实际张数】倒推,不再假定正好 3 张");
                    Assert(homeSrc.Contains("Math.Max(ExpandedCityMin,"),
                           "★ 展开高有下限 —— 张数一多式子会算成负数,而 Height 拿到负值会【在构造期抛】(程序打不开)");
                    Assert(homeSrc.Contains("Content = _weatherStack") && homeSrc.Contains("VerticalScrollBarVisibility = ScrollBarVisibility.Auto"),
                           "★ 装不下时天气栈可滚 —— 否则超出的卡被 ClipToBounds 裁得看不见也碰不着");
                    Assert(homeSrc.Contains("BeginAnimation(FrameworkElement.HeightProperty, anim)"),
                           "★ 切换走高度动画(用户要丝滑切换)");
                    Assert(homeSrc.Contains("ScheduleWeatherReset") && homeSrc.Contains("!host.IsMouseOver"),
                           "★★ 离开后【延迟再实地确认】才恢复默认 —— 卡片变高时光标底下的元素会短暂易主,WPF 会瞬时抛 MouseLeave,立刻响应就会“啦地跳回科隆”");
                    Assert(homeSrc.Contains("Grid.SetRowSpan(calPanel, 2)"),
                           "★ 日历往下延伸跨两行,与右侧天气栈对齐");
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
                // Add 会补 Id 并存入【副本】;删除按存入的那条(与编辑器传 existing 的真实用法一致)
                Views.CalendarData.Remove(Views.CalendarData.Events[0]);
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

            // ---- 焦点纪律:键盘焦点只归【可编辑的输入位】(用户裁定 2026-07-30)----
            // ★★ 这里做的是【行为断言】而不是数样式表:真的 new 出控件、真的把主题字典挂上去、
            //   再读它【解析之后】的 Focusable / IsTabStop。数 Setter 证明不了任何事 ——
            //   一个元素只要带了显式 Style,隐式样式就整条不参与查找(主窗口那三个标题栏按钮
            //   正是这种情况,清点时差点漏掉)。
            {
                if (System.Windows.Application.Current is null) new System.Windows.Application();
                var app = System.Windows.Application.Current;
                var before = app.Resources.MergedDictionaries.Count;
                var ctrls = new System.Windows.ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Theme/Controls.xaml", UriKind.Absolute),
                };
                app.Resources.MergedDictionaries.Add(ctrls);
                try
                {
                    // ★ 必须真的排一次版:代码 new 出来的控件在【进树并测量】之前不会去解析隐式样式,
                    //   直接读 Focusable 拿到的是框架默认值,断言会假失败(第一版就栽在这)。
                    //   带显式 Style 的(PlainTextBox)不受影响 —— 这个差别本身就是本次要防的坑。
                    var host = new System.Windows.Controls.StackPanel();
                    T Live<T>(T el) where T : System.Windows.UIElement { host.Children.Add(el); return el; }

                    // 纯鼠标件:整个退出焦点体系
                    var btn = Live(new System.Windows.Controls.Button());

                    var cb = Live(new System.Windows.Controls.CheckBox());

                    // 下拉框:退出 Tab 序,但【保留可聚焦】—— 鼠标点开后上下键/首字母仍可用
                    var combo = Live(new System.Windows.Controls.ComboBox());

                    // 可编辑输入位:这是唯一保留 Tab 的一类
                    var tb = Live(new System.Windows.Controls.TextBox());

                    // 只读正文(聊天气泡):退出 Tab 序,但保留可聚焦(鼠标拖选 + Ctrl+C 全靠它)
                    var plain = Live(new System.Windows.Controls.TextBox
                    {
                        Style = (System.Windows.Style)ctrls["PlainTextBox"],
                    });
                    host.Measure(new System.Windows.Size(1000, 1000));   // ← 这一下才真正让样式生效

                    Assert(!btn.Focusable && !btn.IsTabStop, "★ 按钮退出焦点体系(Tab 不会停在按钮上)");
                    Assert(btn.FocusVisualStyle is null, "★ 按钮不画系统焦点虚线框");
                    Assert(!cb.Focusable && !cb.IsTabStop, "★ 复选框退出焦点体系");
                    Assert(!combo.IsTabStop, "★ 下拉框不在 Tab 序里");
                    Assert(combo.Focusable, "★ 下拉框仍可聚焦(点开之后键盘选项照常)");
                    Assert(tb.Focusable && tb.IsTabStop, "★ 输入框保留可聚焦、保留 Tab —— 纪律的唯一白名单");
                    Assert(!plain.IsTabStop, "★ 消息正文不在 Tab 序里(否则一条长会话要按上百次 Tab)");
                    Assert(plain.Focusable, "★ 消息正文仍可聚焦 —— 可选中复制是它存在的唯一理由");
                    Assert(plain.IsReadOnly, "消息正文是只读的");

                    // 滑条:本来就不可聚焦(纪律对它是追认现状,不是新损失)
                    var sl = Live(new System.Windows.Controls.Slider());
                    Assert(!sl.Focusable, "滑条不参与焦点(既有行为)");
                }
                finally
                {
                    while (app.Resources.MergedDictionaries.Count > before)
                        app.Resources.MergedDictionaries.RemoveAt(app.Resources.MergedDictionaries.Count - 1);
                }
            }
            // ---- Tab = 二态开关:聚焦 AI 交流输入框 ⇄ 什么都不聚焦(用户第二轮裁定)----
            // ★★ 用户先后实测两轮:第一轮"按钮能被 Tab 到",第二轮"板块也能被 Tab 到"。
            //   根因是 WPF 里 Control 的 Focusable 默认就是 true —— ContentControl 这种纯板块容器
            //   天生就是停靠点。一个个控件去关是打地鼠、注定漏,所以改成正面执行:
            //   Tab 只认一个【显式标记过】的落点,别的一律"不聚焦"。
            {
                var root = new System.Windows.Controls.Grid();
                var panelHost = new System.Windows.Controls.ContentControl();   // ← 当初漏掉的"板块"
                var inner = new System.Windows.Controls.StackPanel();
                panelHost.Content = inner;
                var chatInput = new System.Windows.Controls.TextBox();
                Views.FocusPolicy.SetIsChatInput(chatInput, true);
                var otherBox = new System.Windows.Controls.TextBox();           // 编辑器里的普通输入格
                var btnT = new System.Windows.Controls.Button();
                var readonlyBubble = new System.Windows.Controls.TextBox { IsReadOnly = true };
                foreach (var el in new System.Windows.UIElement[] { btnT, otherBox, readonlyBubble, chatInput })
                    inner.Children.Add(el);
                root.Children.Add(panelHost);
                root.Measure(new System.Windows.Size(800, 800));
                root.Arrange(new System.Windows.Rect(0, 0, 800, 800));

                var found = Views.FocusPolicy.FindChatInput(root);
                Assert(ReferenceEquals(found, chatInput), "★ Tab 只认被标记的 AI 交流输入框");
                Assert(!ReferenceEquals(found, otherBox), "编辑器里的普通输入格不是 Tab 的落点");

                // 二态开关:没聚焦 -> 聚焦它;已聚焦 -> 取消聚焦
                Assert(ReferenceEquals(Views.FocusPolicy.Toggle(found, null), chatInput), "★ Tab 一下:聚焦输入框");
                Assert(Views.FocusPolicy.Toggle(found, chatInput) is null, "★ 再 Tab 一下:取消聚焦(不跳到别处)");
                Assert(ReferenceEquals(Views.FocusPolicy.Toggle(found, btnT), chatInput),
                       "焦点在别处时 Tab 收回输入框");

                // ★ 当前页面没有 AI 输入框 -> 恒为"不聚焦任何"
                var bare = new System.Windows.Controls.Grid();
                bare.Children.Add(new System.Windows.Controls.Button());
                bare.Children.Add(new System.Windows.Controls.ContentControl());
                bare.Children.Add(new System.Windows.Controls.TextBox());        // 没标记的输入框也不算
                bare.Measure(new System.Windows.Size(800, 800));
                Assert(Views.FocusPolicy.FindChatInput(bare) is null, "没有 AI 输入框的页面:找不到落点");
                Assert(Views.FocusPolicy.Toggle(null, null) is null, "★ 没有输入框就不聚焦任何东西");

                // ★ "什么都不聚焦"必须【连逻辑焦点一起清】:只清键盘焦点的话,
                //   焦点范围里还指着那个输入框,WPF 下一次输入就把焦点还回去 ——
                //   表现就是输入框看着没选中(灰的),打字照样进去、还能回车发出去(用户实测)。
                var fpSrc = TryReadSource(Path.Combine("Views", "FocusPolicy.cs"));
                if (fpSrc is not null)
                {
                    // ★★ "什么都不聚焦"必须停到【一个确定的元素】上:只清焦点不可靠,
                    //   WPF 会把焦点还给焦点范围里记着的那个输入框 —— 于是既能打字,
                    //   Tab 又永远回不去(开关每次都判定"已经在输入框上")。
                    var cf = Slice(fpSrc, "public static void Park(", "    }");
                    Assert(cf is not null && cf.Contains("FocusManager.SetFocusedElement(scope, park)") && cf.Contains("Keyboard.Focus(park)"),
                           "★ 取消聚焦 = 把焦点停到专门的空元素上(逻辑焦点与键盘焦点一起改)");
                    Assert(!Body(fpSrc).Contains("{ Keyboard.ClearFocus(); return; }"),
                           "不再有「只清键盘焦点」的写法");
                }

                // 藏起来的输入框不该被 Tab 到
                var hidden = new System.Windows.Controls.Grid();
                var hiddenHost = new System.Windows.Controls.ContentControl { Visibility = System.Windows.Visibility.Collapsed };
                var hiddenInput = new System.Windows.Controls.TextBox();
                Views.FocusPolicy.SetIsChatInput(hiddenInput, true);
                hiddenHost.Content = hiddenInput;
                hidden.Children.Add(hiddenHost);
                hidden.Measure(new System.Windows.Size(800, 800));
                Assert(Views.FocusPolicy.FindChatInput(hidden) is null, "折叠起来的分支里的输入框不被 Tab 到");
            }
            // ---- 系统页(设置/模型/扩展)是【覆盖式】的,不销毁底下正在工作的页面 ----
            // ---- 模型页的「模型选择策略」先占位(用户裁定 2026-07-31) ----
            {
                var mv = TryReadSource(Path.Combine("Views", "ModelsView.cs"));
                if (mv is not null)
                {
                    Assert(mv.Contains("StrategyPlaceholder()") && mv.Contains("model.strategy"),
                           "模型页多了一块「模型选择策略」");
                    var ph = Slice(mv, "static FrameworkElement StrategyPlaceholder()", "return Ui.Card(Ui.Stack(");
                    Assert(ph is not null && !ph.Contains("ToggleSwitch") && !ph.Contains("new CheckBox") && !ph.Contains("new ComboBox"),
                           "★ 占位符里【没有任何能拨却不生效的控件】—— 空着只是“还没做”,假开关是骗人");
                    Assert(ph is not null && ph.Contains("StrokeDashArray"),
                           "占位用虚线框 —— 实线会让人以为是个已完成的板块");
                    Assert(Strings.Get("model.strategy_todo").Length > 0 && Strings.Get("model.strategy_note").Length > 0,
                           "占位文案三语都齐(缺键时 Strings.Get 会退回键名)");
                }
            }

            // ---- 界面文案里不允许出现字面 ** ----
            // ★ 这里没有 markdown 渲染器:写了 **强调** 就是把星号原样画给用户看。
            //   要强调用【】或「」—— 这两个在纯文本里自己就是强调。
            foreach (var vf in new[] { "SettingsView.cs", "DevicesView.cs", "ModelsView.cs", "ExtensionsView.cs" })
            {
                var viewSrc = TryReadSource(Path.Combine("Views", vf));
                if (viewSrc is null) continue;
                var bad = false;
                foreach (var line in viewSrc.Split('\n'))
                {
                    var t = line.TrimStart();
                    if (t.StartsWith("//") || t.StartsWith("///")) continue;   // 注释里随便写
                    if (line.Contains("**")) { bad = true; break; }
                }
                Assert(!bad, $"{vf} 的界面文案里没有字面 **(不渲染 markdown,写了就是画星号)");
            }

            // ---- 归档不能静默丢原文(审计 2026-07-31 的三条高危) ----
            {
                var arcSrc = TryReadSource(Path.Combine("Services", "SessionArchive.cs"));
                var ccSrc = TryReadSource(Path.Combine("Services", "ChatCenter.cs"));
                if (arcSrc is not null)
                {
                    Assert(arcSrc.Contains("public static bool Append(string sessionId"),
                           "★ Append 要告诉调用方落盘没落盘(以前是 void,写失败也没人知道)");
                    Assert(arcSrc.Contains("LoadChecked") && arcSrc.Contains(".corrupt-"),
                           "★ 坏档【不覆盖】—— 攒到一边留作证据,而不是静默整份写掉");
                }
                if (ccSrc is not null)
                {
                    Assert(ccSrc.Contains("if (!SessionArchive.Append(g.Key, older)) continue;"),
                           "★ 写成了才能从热层拿掉 —— 否则写盘失败 = 原文永久静默丢失");
                    Assert(ccSrc.Contains("void PurgeArchives(IEnumerable<string> ids)"),
                           "四条销毁路径走同一个温层清理(免得第五条又漏)");
                    Assert(ccSrc.Split("PurgeArchives(ids);").Length - 1 >= 3,
                           "★ 幽灵会话/全部清除/30 天自动清除都要清温层(界面对它们的承诺是“不留痕”/“不可恢复”)");
                }
            }

            // ---- 审计 2026-07-31 其余几条的回归防线 ----
            {
                var sv2 = TryReadSource(Path.Combine("Views", "SettingsView.cs"));
                var tb2 = TryReadSource(Path.Combine("Views", "TranslationBar.cs"));
                var cv2 = TryReadSource(Path.Combine("Views", "ChatView.cs"));
                var dv2 = TryReadSource(Path.Combine("Views", "DevicesView.cs"));
                if (sv2 is not null)
                {
                    Assert(sv2.Split("NotifyPoolChanged();").Length - 1 >= 2,
                           "★ 设置里增删语言池要广播 —— 设置页是覆盖式的,底下那个翻译界面不重建");
                    Assert(sv2.Contains("Authenticode 签名在这里把关"),
                           "★ 提权运行前由 Authenticode 签名把关(装驱动提权且不可逆,“不确定”必须等于“不做”)");
                }
                if (tb2 is not null)
                {
                    Assert(tb2.Contains("enabled: drv.Installed && InterpretState.PipelineReady"),
                           "★ 装了声卡也不能拨 —— 语音链路没接时那就是个假开关");
                    Assert(tb2.Contains("string? fromSlot = null") && tb2.Contains("_dragFromSlot"),
                           "★ 从方向坑里往外拖 ≠ 从目标池拖(否则会静默删掉文字翻译目标池里同一个语言)");
                }
                if (cv2 is not null)
                    Assert(cv2.Contains("彻底删除") && cv2.Contains("【无法恢复】"),
                           "★ “全部清除”这个不可恢复的动作要二次确认");
                if (dv2 is not null)
                    Assert(!dv2.Contains("主机还没有开放设备管理接口") && dv2.Contains("D37 / D48"),
                           "★ 设备管理的 404 要说真原因(结构性到不了),不是“主机没升级”");
            }

            // ---- 客户端诚实性与数据(审计 2026-07-31 批次一) ----
            {
                var cv3 = TryReadSource(Path.Combine("Views", "ChatView.cs"));
                var su3 = TryReadSource(Path.Combine("Services", "StorageUsage.cs"));
                var ccHon = TryReadSource(Path.Combine("Services", "ChatCenter.cs"));
                var th3 = TryReadSource(Path.Combine("Services", "TranslationHistory.cs"));
                var pu3 = TryReadSource(Path.Combine("Views", "ProjectUi.cs"));
                if (cv3 is not null)
                {
                    Assert(cv3.Contains("AppPaths.StateDir, \"clips\"") && !cv3.Contains("GetTempPath(), \"localai-clip-"),
                           "★ 粘贴截图落到 client/clips 而不是 %TEMP% —— 否则“清理缓存”会删掉已发消息里截图的唯一副本");
                    // ★★★ 2026-08-05 更正:模型已接入(P4-S11),这句话**当场变成假话** ——
                    //   用户刚跟模型聊完,输入框底下还印着「AI 模型尚未接入…现在还不会有回答」。
                    //   原断言(要求这句话在)在那一刻是**在保护一句谎言**。
                    //   ⇒ 反过来钉:那句话必须【不在了】,且提示按真实前提分层。
                    // ★★★ 2026-08-05 审计:这里原来还有一条
                    //       Assert(!CodeOnly(cv3).Contains("AI 模型尚未接入"), …)
                    //     它**恒真** —— CodeOnly 会把每个字符串字面量整个换成 "",
                    //     而这句话只可能出现在字符串里。⇒ 文案改回去它也不会红。
                    //   ★ 更值得记住的是它的来历:它本身就是为了修一句谎言而写的
                    //     (上面那段注释还写着"⇒ 反过来钉:那句话必须【不在了】"),
                    //     修得对、写得诚恳,**而修法是空的**。
                    //   ⇒ 已被本文件后面那条【全仓 + NoComments】的扫描取代,
                    //     那一条第一次跑就把这行的针给抓了出来。
                    //     (同款教训见 00-docs/ASSERTION-PITFALLS.md:文案断言用 NoComments,
                    //      CodeOnly 只能用来查代码结构。)
                    Assert(cv3.Contains("还没有配对到中枢") && cv3.Contains("主机未开启"),
                           "★★ 提示按【真实前提】分层:没配对 / 主机不在线 —— 每层只说自己那件事");
                    Assert(cv3.Contains("if (!TheApp.Hub.IsPaired)"),
                           "★ 一切正常时【什么都不说】—— 常驻提示会被当成背景噪声,真出事那天就没人看了");

                }
                if (su3 is not null)
                {
                    Assert(su3.Contains("ReferencedAttachmentPaths()") && su3.Contains("fail-closed"),
                           "★ 清理缓存排除仍被消息引用的旧截图(fail-closed)");
                    Assert(!su3.Contains("分层存储待接入"),
                           "分层存储早已实装 —— 不再写“待接入”");
                }
                if (ccHon is not null)
                    Assert(ccHon.Contains("StorageUsage.DeleteClipFile(a.Path)") && ccHon.Contains("ReferencedAttachmentPaths"),
                           "★ 幽灵会话清除时连粘贴截图一起删(不留痕)");
                if (th3 is not null)
                    Assert(th3.Contains("AllTranslationSessions().Where(s => !s.Interpret)"),
                           "翻译历史排除同传会话");
                if (pu3 is not null)
                    Assert(pu3.Contains("AiNotConnected") && pu3.Contains("接入后:"),
                           "★ 项目 AI 权限标明“尚未接入、这是偏好”且解释用未来时");
            }

            // ══════════════════════════════════════════════════════════════
            //  P4-S9:显存条的数据源 + 组件挑选面板
            //
            //  ★★★ 这一组守的是一条【实测发现的谎】:
            //    此前左导航那条「显存」无条件直调本机 nvml 的 index 0。
            //    主机上碰巧没错(本机就是中枢);副机上它显示的是**副机自己那张卡**,
            //    标签却只写「显存」。两台机器的数字长得一模一样,
            //    **没有任何地方能看出来看错了** —— 正是本项目最恨的形状。
            // ══════════════════════════════════════════════════════════════
            {
                // ══════════════════════════════════════════════════════
                //  ★★★ 2026-08-05 重写。用户裁定:显存**永远显示主机的**,
                //  副机要主机显存、主机也要主机显存;拿不到就显示「主机未连接」。
                //
                //  ★★ 重写的另一半理由更要紧:这一组**原来大半是假的** ——
                //    · `Hub.Title != localTitle` 是两个**字符串常量**互比,永真;
                //    · 「不退回本机」那条判据是 VramMonitor.cs 里的**一句注释**
                //      (全仓唯一出处)⇒ 删注释就红、留注释就绿,**从来没验过行为**;
                //    · `IndexOf(A) < IndexOf(B)` 的顺序断言,在 B 被删之后会以
                //      **一个假理由**失败(报"顺序错了",真相是"根本没有本机路径")。
                //  而它们全都绿着,同时产品里那条谎**原封不动活着**:
                //  `VramBar` 的标题是构造函数写死的「显存」,`Update()` 一次都没读过 `s.Title`。
                //  ⇒ 新的这一组一律钉**行为**与**接线**,不钉注释。
                // ══════════════════════════════════════════════════════

                // ① 逐态标题互不相同 —— 合并任意两态就等于把两种下一步说成同一件事
                var vsTitles = Enum.GetValues<VramSource>()
                                   .Select(v => new VramSnapshot(16, 0, 4, false, null, v).Title)
                                   .ToList();
                Assert(vsTitles.Count >= 3, $"★ 元断言:确实枚举到了各态(只有 {vsTitles.Count} 个)");
                Assert(vsTitles.Distinct().Count() == vsTitles.Count,
                       $"★★ 每一态必须有自己的说法,不许合并:{string.Join(" / ", vsTitles)}");
                Assert(new VramSnapshot(16, 0, 4, false, null, VramSource.HostUnreachable).Title
                           .Contains("主机未连接"),
                       "★★ 拿不到主机数据时,标题就是「主机未连接」(用户裁定 2026-08-05)");
                Assert(!new VramSnapshot(16, 0, 4, false, null, VramSource.HostNoReading).Title
                            .Contains("未连接"),
                       "★★★ 主机连着但它自己没读到 ≠ 未连接 —— 说成未连接会把人支去查网络,"
                       + "而该查的是主机的显卡。两者的下一步完全相反");

                // ② ★★★ 「回退回不来」的钉子:只有 Hub 那一态允许有数字。
                //    哪天有人加回一个本机来源并让它显示数字,这条当场红。
                foreach (var v in Enum.GetValues<VramSource>())
                {
                    var snap = new VramSnapshot(16, 2, 4, true, null, v);
                    Assert(snap.HasNumbers == (v == VramSource.Hub),
                           $"★★★ 只有主机来源可以显示数字;{v} 却 HasNumbers={snap.HasNumbers}");
                }

                var vmSrc = TryReadSource(Path.Combine("Services", "VramMonitor.cs"));
                if (vmSrc is not null)
                {
                    var vmCode = CodeOnly(vmSrc);
                    Assert(vmCode.Contains("hub.HasFreshData"),
                           "★ 只在主机数据【新鲜】时才用它(过期的主机数字也不能冒充现在)");
                    // ★★ 反向:本机读取路径必须**整条不在了**。判据查代码不查注释 ——
                    //   注释里正解释着"为什么删掉了",拿它当判据会永远红。
                    foreach (var gone in new[] { "nvml.dll", "TryInitNvml", "TryReadNvml",
                                                 "TryReadSmi", "nvidia-smi", "_smiDead" })
                        Assert(!vmCode.Contains(gone),
                               $"★★★ 本机显卡读取路径必须删干净,`{gone}` 还在 —— "
                               + "留着就是留着一条随时会被接回去的错误路径,而它没有任何调用点");
                }

                // ══════════════════════════════════════════════════════
                //  ★★★ 接线断言 —— 这一条要是早写了,上面那半年的谎当天就会红。
                //
                //  P4-S9 把「拿不到中枢数据就改标题」做在了 VramSnapshot.Title 里,
                //  断言也钉住了它,而 **VramBar 从来没读过这个属性** ——
                //  标题是构造函数里写死的「显存」。于是:模型改了、断言绿了、
                //  文档写了"已修",**而屏幕上一个字都没变**。
                //  ⇒ 凡是"界面必须如实标注 X"这类判据,光钉住 X 算出来是对的**没有意义**,
                //    必须同时钉住**有人把它画出来**。
                // ══════════════════════════════════════════════════════
                var vbSrc = TryReadSource(Path.Combine("Views", "VramBar.cs"));
                if (vbSrc is not null)
                {
                    var vb = CodeOnly(vbSrc);
                    Assert(vb.Contains("_title.Text = s.Title"),
                           "★★★ 标题必须来自快照 —— 写死的标题让「说清是哪台机器」永远不生效");
                    Assert(vb.Contains("s.HasNumbers") || vb.Contains("HasNumbers"),
                           "★★ 画不画数字由 HasNumbers 一处决定,不各自判 Available/Total");
                    // ★ 顺序判据:先各自 Contains 再比位置 —— 否则某一天其中一个被删,
                    //   IndexOf 返回 -1,断言会以**一个假理由**失败(上面那组就栽过)。
                    // ★★ 而且必须**只在 Update() 体内**比:第一版拿整个文件比,
                    //   于是比到的是 UpdateRing 的**方法定义**(它写在 Update 前面),
                    //   报"顺序错了"而代码是对的 —— 判据比想判的东西宽,当场红了一条假红。
                    var iUpd = vb.IndexOf("public void Update(VramSnapshot s)", StringComparison.Ordinal);
                    var body = iUpd >= 0 ? vb[iUpd..] : "";
                    var iShow = body.IndexOf("ShowUnavailable(s)", StringComparison.Ordinal);
                    var iRing = body.IndexOf("UpdateRing(s)", StringComparison.Ordinal);
                    Assert(iUpd >= 0 && iShow >= 0 && iRing >= 0,
                           $"★ 三处都要找得到(位置判据的前提):Update={iUpd} Show={iShow} Ring={iRing}");
                    Assert(iShow < iRing,
                           $"★★★ 拿不到数据必须在 UpdateRing 之前就 return(Show={iShow} Ring={iRing})"
                           + " —— 环上写的是 UsedRatio,拿不到时它是 0,收起态会显示 0% = 显存全空");
                    Assert(!vb.Contains("Visibility.Collapsed;   // 读不到就藏起来"),
                           "★ 拿不到不再整条隐藏(用户裁定:要显示「主机未连接」)—— "
                           + "隐藏分不清『出错了』和『这版没这个功能』");
                }

                // ★★ 主机发来读不懂的帧时,不许把旧快照伪装成新鲜的
                var hgFresh = TryReadSource(Path.Combine("Services", "HubGpu.cs"));
                if (hgFresh is not null)
                {
                    var hgc = CodeOnly(hgFresh);
                    var iParse = hgc.IndexOf("TryParseSnapshot(line[6..])", StringComparison.Ordinal);
                    var iStamp = hgc.IndexOf("LastFrameAt = DateTime.UtcNow", StringComparison.Ordinal);
                    Assert(iParse >= 0 && iStamp >= 0, "★ 两处都在(位置判据的前提)");
                    Assert(iParse < iStamp,
                           "★★★ 时间戳必须在**解析成功之后**才刷 —— 反过来的话,中枢发来读不懂的帧时"
                           + "会进入最坏的稳态:Link=Live、时间戳一直新、快照冻在最后一帧好数据,"
                           + "于是界面一直显示几小时前的数字而看不出来,「主机未连接」永远不触发");
                }

                // ③ 推送非轮询(D37 ②),且心跳是判据的一部分
                var hg = TryReadSource(Path.Combine("Services", "HubGpu.cs"));
                if (hg is not null)
                {
                    Assert(hg.Contains("/v1/gpu/events"), "★ 走 SSE 推送流,不是定时轮询快照(D37 ②)");
                    Assert(hg.Contains("LastFrameAt") && hg.Contains("StaleAfter"),
                           "★★ 用心跳时间判活:没有它,一条【死掉】的长连接与「一直没变化」长得一样");
                    Assert(hg.Contains("HubGpuLink.Reconnecting"),
                           "★ 断线时显式转 Reconnecting —— 重连期间不假装还活着");
                }
                Assert(HubGpu.StaleAfter.TotalSeconds > 15,
                       "判活阈值必须大于服务端心跳间隔(15 秒),否则正常心跳也会被判死");

                // ④ 流式读取必须用 ResponseHeadersRead —— SSE 的响应体是无限长的
                var ctSrc = TryReadSource(Path.Combine("..", "transport", "ClientTransport.cs"));
                Assert(ctSrc is null || ctSrc.Contains("HttpCompletionOption.ResponseHeadersRead"),
                       "★★ SSE 用 ResponseHeadersRead:默认那个会等【无限长】的响应体读完 = 永远挂住");
                Assert(ctSrc is null || ctSrc.Contains("Timeout.InfiniteTimeSpan"),
                       "★ 长连接不能被 HttpClient 默认 100 秒超时掐断");

                // ⑤ 每种失败给不同的下一步,不合并成"失败了"
                string Adv(string code) => new ApplyOutcome(false, code, "m", "", Array.Empty<BlockingLease>(), 0).Advice;
                var codes = new[] { "loader_absent", "needs_user_choice", "busy", "generation_conflict",
                                    "vram_not_reclaimed", "load_failed_rolled_back", "rollback_failed" };
                Assert(codes.Select(Adv).Distinct().Count() == codes.Length,
                       "★★ 七种失败七种说法 —— 合并成一句「失败了」会让用户无从判断下一步");
                Assert(Adv("loader_absent").Contains("没有生效") || Adv("loader_absent").Contains("不会真的"),
                       "★★★ loader_absent 必须说清【没有生效】—— 不能让用户以为模型装上了");
                // ★★★ 2026-08-06 审计 B2:上面那条**只钉了诚实的那半句**,
                //   于是「中枢还没有装载器(那是 P5)」这半句假话在它眼皮底下绿了一整天。
                //   装载器 S14 就落地了,P5 是语音 v1 —— 两处都是假话。
                //   ⇒ 补**反向**断言。★ 这是本项目的老规矩:
                //     「必须说 X」和「不许说 Y」是两条,只写前一条挡不住后一条。
                Assert(!Adv("loader_absent").Contains("P5"),
                       "★★★ 不得再把装载器安给 P5 —— 装载器 S14 就实现了,P5 是语音 v1。"
                       + "给错原因的提示比不给提示更坏");
                Assert(!Adv("loader_absent").Contains("还没有装载器"),
                       "★★ 也不得说『中枢还没有装载器』—— 它有。该说的是【这一次为什么没接上】");
                // ★ 服务端的归因必须能透出来:它已经分清「接线失败:{原因}」与「有意没接」,
                //   客户端再套一句写死的等于把那份归因丢掉。
                Assert(new ApplyOutcome(false, "loader_absent", "接线失败:配置里少了 model_rel",
                                        "", Array.Empty<BlockingLease>(), 0)
                       .Advice.Contains("model_rel"),
                       "★★★ 服务端说出来的原因必须原样透给用户 —— "
                       + "客户端自己编一句罐头话,等于把中枢查出来的归因丢在半路");
                Assert(Adv("rollback_failed").Contains("主机"),
                       "回滚失败要指出得去主机上处理(客户端自己解决不了)");

                // ⑥ 面板:清单只从中枢来;取不到就什么都不列
                var pk = TryReadSource(Path.Combine("Views", "ComponentPicker.cs"));
                if (pk is not null)
                {
                    Assert(pk.Contains("FetchCatalogAsync") && !pk.Contains("ModelCatalog.All"),
                           "★ 清单向中枢取,不用本地自造清单");
                    Assert(pk.Contains("不显示】任何清单") || pk.Contains("什么都不列"),
                           "★★ 取不到清单时【不列本地兜底】—— 那等于把自造清单当成中枢的真实清单");
                    Assert(pk.Contains("改桌面预留") && pk.Contains("没有用"),
                           "★★ 两种撞墙分开说:静态可调预留 / 动态调了没用(§8.1 合并是有害的)");
                    Assert(pk.Contains("if_generation") || pk.Contains("Generation"),
                           "★ 提交带世代号(挑选要几十秒,期间桌面会变)");
                    Assert(pk.Contains("Snapshot?.Generation"),
                           "★ 用推送流里【当前】那个世代号,不是面板加载时那个旧号");
                    Assert(pk.Contains("interruptRunning: true"),
                           "★ 有任务在跑时问过用户才中断,不自作主张");
                    Assert(pk.Contains("本机与中枢的显存配置对不上"),
                           "★ 本地算不出某个组件的峰值时说出来,不静默按 0 计(那是 fail-open)");
                }
            }

            // ══════════════════════════════════════════════════════════════
            //  P4-S11:接入模型(流式 + token 预算截断)
            //
            //  ★★★ 这一组守的是「绝不伪造回复」在**每一条失败路径上**都成立。
            //    模型接进来之后,最危险的新形状是:某条失败路径悄悄写下一条 Assistant 消息,
            //    于是"模型说的"和"客户端编的"分不开了。
            // ══════════════════════════════════════════════════════════════
            {
                // ① token 估算必须【估高不估低】—— 估低会撞上下文窗口,而用户只看到"它忘了前面"
                Assert(Services.TokenBudget.Estimate("你好世界") == 4, "CJK 按 1 token 估");
                Assert(Services.TokenBudget.Estimate("abcdefghi") >= 3, "英文按 3 字符 1 token(常见口径是 4 = 估高)");
                Assert(Services.TokenBudget.Estimate("abcd") == 2, "★ 向上取整 = 估高");
                Assert(Services.TokenBudget.Estimate("") == 0 && Services.TokenBudget.Estimate(null) == 0,
                       "空文本 0 token");

                // ② 窗口从组件 id 推,推不出来取【最小】那一档
                Assert(Services.TokenBudget.WindowOf("llm.assistant.8b@16k") == 16384, "16k 解析对");
                Assert(Services.TokenBudget.WindowOf("llm.assistant.30b-a3b@32k") == 32768, "32k 解析对");
                Assert(Services.TokenBudget.WindowOf("speech.lite") == 0, "没有 @ 的组件推不出窗口");
                var (w1, g1) = Services.TokenBudget.WindowFrom(new[] { "llm.assistant.8b@32k", "llm.assistant.8b@8k" });
                Assert(w1 == 8192 && !g1,
                       "★★ 多个 llm 同时驻留取【最小】—— 请求落到哪个由中枢的别名路由决定,客户端猜不了");
                var (w2, g2) = Services.TokenBudget.WindowFrom(new[] { "speech.lite" });
                Assert(w2 == Services.TokenBudget.FallbackWindow && g2,
                       "★★ 一个都推不出来 → 回落到最小档【并标记 isGuess】,不拿个大数蒙混");
                var (w3, g3) = Services.TokenBudget.WindowFrom(null);
                Assert(w3 == Services.TokenBudget.FallbackWindow && g3, "读不到中枢数据也是保守 + 标记");

                // ③ 截断:从最近往回装,丢掉的必须是【最早的】
                var s11hist = new List<Services.ChatMessage>();
                for (int i = 0; i < 200; i++)
                    s11hist.Add(new Services.ChatMessage("s", i % 2 == 0 ? Services.ChatRole.User : Services.ChatRole.Assistant,
                                                        new string('字', 200), DateTime.Now, null, "m" + i));
                var s11plan = Services.TokenBudget.Plan(s11hist, "现在这条", new[] { "llm.assistant.8b@8k" });
                Assert(s11plan.Truncated, "200 条 × 200 字装不进 8K 窗口 ⇒ 必然截断");
                Assert(s11plan.Included.Count > 0 && s11plan.Included.Count < 200, "带了一部分");
                Assert(s11plan.Included[^1].MessageId == "m199",
                       "★★ 留下的是【最近的】—— 丢中间会让对话逻辑断裂,而用户完全看不出来");
                Assert(s11plan.EstimatedTokens <= s11plan.BudgetTokens,
                       "★ 估算总量不超预算(预算已扣掉给回答的余量)");
                Assert(s11plan.BudgetTokens == 8192 - Services.TokenBudget.ReplyReserve,
                       "★ 预算 = 窗口 − 回答余量:装不下答案的上下文等于没装");

                // ★★ 截断必须【看得见】
                Assert(s11plan.Caption.Contains("/") && s11plan.Caption.Contains("估算"),
                       "★★ 界面文案写明「带了 N / 共 M」且标【估算】—— 不能读起来像精确 token 数");
                Assert(s11plan.Caption.Contains("更早的没带上"),
                       "★★ 静默丢历史 = 用户以为它记得而它没有;必须说出来");

                // ★ System 消息不上行 —— 那是我们自己的界面文案
                var s11mixed = new List<Services.ChatMessage> {
                    new("s", Services.ChatRole.User, "问", DateTime.Now, null, "a"),
                    // ★ 夹具文本换成中性的:原来这里写着那句被明令禁止的界面文案,
                    //   而下面的全仓扫描会(正确地)把它当成一处违规 —— 夹具不该逼守卫留后门。
                    new("s", Services.ChatRole.System, "(系统说明占位)", DateTime.Now, null, "b"),
                    new("s", Services.ChatRole.Assistant, "答", DateTime.Now, null, "c"),
                };
                var s11p2 = Services.TokenBudget.Plan(s11mixed, "再问", null);
                Assert(s11p2.Included.All(m => m.Role != Services.ChatRole.System),
                       "★★ System 消息不发给模型 —— 那是客户端自己的说明,发上去等于让模型读我们的界面文案");
                Assert(s11p2.TotalCandidates == 2, "候选只数 User/Assistant");

                // ④ 失败路径:每种给不同的下一步,且【绝不伪造回复】
                string S11Adv(string code) => new Services.ChatOutcome(false, code, "m").Advice;
                var s11codes = new[] { "not_paired", "backend_unavailable", "backend_error",
                                       "denied_quota", "hub_offline", "stream_broken", "e1_blocked" };
                Assert(s11codes.Select(S11Adv).Distinct().Count() == s11codes.Length,
                       "★★ 七种失败七种说法 —— 合并成「发送失败」会让用户无从判断下一步");
                Assert(S11Adv("backend_unavailable").Contains("start-stack"),
                       "★★ 后端没起要说清【怎么起】,而不是只说连不上");
                Assert(S11Adv("backend_error").Contains("不是连不上"),
                       "★ 后端应答了但报错 ≠ 连不上:前者去看日志,后者去看进程");
                Assert(S11Adv("stream_broken").Contains("真的说过") && S11Adv("stream_broken").Contains("没说完"),
                       "★★ 半截回答要说清【上面那段是模型真说的,但没说完】");
                Assert(S11Adv("e1_blocked").Contains("之前") && S11Adv("e1_blocked").Contains("没有发出去"),
                       "★ 凭证被 E1 拦下要说清是在【送进模型之前】拦的");

                // ⑤ 源码级:失败路径不得写 Assistant 消息
                var s11cc = TryReadSource(Path.Combine("Services", "ChatCenter.cs"));
                if (s11cc is not null)
                {
                    var ask = Slice(s11cc, "public async Task<ChatOutcome> SendAndAskAsync", "播种/导入用");
                    Assert(ask is not null && ask.Contains("_messages.RemoveAt(idx)"),
                           "★★★ 一个字都没收到时【删掉那条空回复】—— 不留一条空 Assistant 冒充回答");
                    Assert(ask is not null && ask.Contains("res.Partial.Length > 0"),
                           "★★ 半截保留(模型真说过的不能丢)");
                    Assert(!CodeOnly(ask ?? "").Contains("ChatRole.Assistant, res.Advice"),
                           "★★★ 失败说明走 System,绝不写成 Assistant");
                }
                var s11cli = TryReadSource(Path.Combine("Services", "ChatClient.cs"));
                if (s11cli is not null)
                {
                    Assert(s11cli.Contains("sb.Length == 0") && s11cli.Contains("stream_broken"),
                           "★★ 流正常结束却一个字都没有 ⇒ 不当成成功(那是「200 + 空 body」的另一种形态)");
                    Assert(s11cli.Contains("errStatus != 0") && s11cli.Contains("ParseError"),
                           "★★ 非 2xx 时读出网关的归因,别退化成「连接失败」");
                    Assert(s11cli.Contains("assistant.fast"),
                           "★ 客户端点【别名】不点组件 —— 换模型时客户端一行都不用改(§8.1)");
                    Assert(!CodeOnly(s11cli).Contains("llm.assistant"),
                           "★ 客户端调用路径里不得出现显存组件 id(那是中枢的词汇,客户端只认别名)");
                }
                var s11cv = TryReadSource(Path.Combine("Views", "ChatView.cs"));
                if (s11cv is not null)
                {
                    Assert(s11cv.Contains("SendAndAskAsync"), "★ 发送按钮走真链路");
                    Assert(s11cv.Contains("TheApp.Hub.IsPaired"),
                           "★ 没配对时仍走那条诚实占位路径(不伪造回复)");
                    Assert(s11cv.Contains("Dispatcher.BeginInvoke"),
                           "★★ 流式回调在后台线程:必须切回 UI 且用 BeginInvoke —— "
                           + "一秒几十帧,同步 Invoke 会把自己堵死");
                }
                var s11tr = TryReadSource(Path.Combine("..", "transport", "ClientTransport.cs"));
                Assert(s11tr is null || s11tr.Contains("onNonSuccess"),
                       "★★ 流式非 2xx 时先把正文读出来 —— 丢掉它等于把「后端没起」退化成「连不上」");
            }

            // ══════════════════════════════════════════════════════════════
            //  P4-S12:★★★「未接入」类措辞必须【指名道姓】
            //
            //  2026-08-05 一晚之内同一类问题出现三次:
            //    ① 输入框「AI 模型尚未接入(P4)」—— 模型接进来后**直接变假**;
            //    ② 共享框「中枢尚未接入」—— 字面还真(会话同步确实没做),但用户刚跟
            //       中枢聊完天,读起来就是假的,于是他相信共享已经生效
            //       (实测反馈:「我把副机的会话提升到共享,主机这边看不见」);
            //    ③ STATE 的「下一步」指路牌 —— 当天错了四次。
            //
            //  ★★ 共同根因:**系统是一件一件接入的**,而「未接入」是个笼统说法。
            //    第一件接上的那一刻,所有笼统措辞同时失信 —— 用户没法判断哪句还算数。
            //  ⇒ 规矩:凡是说「某某还没做/还没接入」,必须**点名是哪一件事**,
            //    并且**不要用会随别处进展变化的笼统主语**(「中枢」「AI」「后端」)。
            // ══════════════════════════════════════════════════════════════
            {
                // ══════════════════════════════════════════════════════════
                //  ★★★ 2026-08-05 审计:过期文案的守卫必须扫【全部源码】,不是一个文件
                //
                //  原来这两条断言写在这里,只读 Views/ChatView.cs。而实测:
                //    · 「AI 模型尚未接入(P4 GPU Broker)」躺在 Services/ChatCenter.cs
                //    · 「中枢尚未接入,现在只做标记」  躺在 Views/ProjectUi.cs
                //  两句都被这条断言**逐字点名**禁止过,两句都活得好好的 ——
                //  守卫写对了内容,却看不见它要守的地方。
                //
                //  ★ 这类断言的判据天生是全仓的:一句谎话搬个文件就绕过去了。
                //  ★ 针拼出来、夹具改中性 ⇒ 扫描**包含 Selftest.cs 自己**,不留后门。
                // ══════════════════════════════════════════════════════════
                {
                    var banned = new[] {
                        ("AI 模型" + "尚未接入",
                         "模型 S11 已接入 —— 这句话留着就是界面在说假话。要说的是【这个功能】还没接上模型"),
                        ("中枢" + "尚未接入",
                         "中枢已经能对话、也在同步会话与家庭待办了。笼统这么说会让用户以为什么都没好"),
                    };
                    var allSrc = TryReadAllSources();
                    // ★★ 两种"读到 0 个"必须分开,它们的含义相反:
                    //   · 发布产物旁边**根本没有源码** ⇒ 与 TryReadSource 同款:跳过。
                    //     (实测:发布产物原位跑自检时 1852 → 834,近千条源码类断言都是这么跳的。)
                    //   · 开发环境**有源码但只扫到 1 个** ⇒ 枚举写坏了,下面几条在空转,必须红。
                    //   把两者合成一条 `Count >= 20`,发布产物那次就会红在一个根本不是缺陷的地方
                    //   —— 第一版正是这么写的,当场被出包门禁拦下。
                    if (allSrc.Count == 0)
                    {
                        Console.WriteLine("  (跳过:此处没有源码 —— 发布产物原位跑,不是缺陷)");
                    }
                    else
                    {
                        Assert(allSrc.Count >= 20,
                               $"★★ 元断言:源码枚举没写坏(只读到 {allSrc.Count} 个 ⇒ 下面几条在空转)");
                        foreach (var (needle, why) in banned)
                        {
                            var hits = allSrc.Where(f => NoComments(f.Text).Contains(needle))
                                             .Select(f => f.Path).ToList();
                            Assert(hits.Count == 0, $"★★★ 「{needle}」还在:{string.Join(", ", hits)} —— {why}");
                        }
                        // ★ 反过来钉一次:针本身必须还能匹配得到东西。拼错一个字这几条就永远绿,
                        //   而"永远绿"正是本项目最该怕的形状(这条红了说明针写坏了,不是代码坏了)。
                        foreach (var (needle, _) in banned)
                            Assert($"示例:{needle}".Contains(needle), $"★ 针拼错了:{needle}");
                    }
                }

                var s12cv = TryReadSource(Path.Combine("Views", "ChatView.cs"));
                if (s12cv is not null)
                {
                    // ★ 文案断言必须用 NoComments(保留字符串);用 CodeOnly 会把字符串剥光 ⇒ 恒真。
                    //   2026-08-05 我这里先写错了一版,五条当场红 —— 而其中「不得出现」那条
                    //   本来会**恒绿**,那才是真正危险的:它在文案改回去的那天也不会红。
                    var code = NoComments(s12cv);
                    // ★★★ 2026-08-05:这三条**自己过期了**。
                    //   它们当时在守「会话同步还没有做」这句话在场 —— 而同一晚 D86/S13
                    //   把同步做出来之后,守住那句话就等于**守住一句谎言**。
                    //   ⇒ 这是「每做成一件事就回头查谁说过『这件事还没有』」的第五次实例,
                    //     而这一次过期的是**断言本身**。断言也会说假话。
                    Assert(code.Contains("立刻") && code.Contains("上传到中枢"),
                           "★★ 同步做出来了:改成如实说「整段会立刻上传到中枢」");
                    Assert(code.Contains("未同步"),
                           "★★ 并说清主机不在线时会排队、界面会显示「未同步」");
                    // ★★★ 2026-08-05 用户裁定推翻了原来那条(删除只影响本机)。
                    //   原判的理由依然成立 —— 删了会让另一台上的会话凭空消失,
                    //   而那台的用户没做过任何事 —— 只是用户选择接受这个后果:
                    //   共享的东西两边同进同退。⇒ 界面必须**把这个后果说出来**。
                    Assert(code.Contains("同步到其它设备") && code.Contains("不会收到任何提示"),
                           "★★★ 删除会传播 ⇒ 确认框必须说清「另一台也会跟着删,而且对方没有提示」——"
                           + "不说就是替用户做了一个他不知道的决定");
                    Assert(code.Contains("还没有配对到中枢") && code.Contains("主机未开启"),
                           "★★ 输入框提示按【真实前提】分层:没配对 / 主机不在线");
                }
                // ★★ 待办的「家庭」范围同一类问题(实测:「我这边添加的共享家庭待办也无法在对方应用显示」)。
                //   D57 裁定待办是纯本机数据;而「家庭」这个词在**选范围的那一刻**读起来就是
                //   "两台机器都看得见"。说明必须摆在期望形成的地方,不是摆在某个正确但没人看的角落。
                var s12te = TryReadSource(Path.Combine("Views", "TodoEditor.cs"));
                if (s12te is not null)
                {
                    var t = NoComments(s12te);
                    // ★★★ 同上:这两条也过期了 —— D86 之后家庭待办**真的会同步**。
                    Assert(t.Contains("会同步到其它已配对设备"),
                           "★★★ 家庭待办现在真的同步了,选范围处要如实说");
                    Assert(t.Contains("只在这台电脑上") || t.Contains("不同步"),
                           "★★ 并逐档说清个人的不同步 —— 笼统的说法失信最快");
                }

                // ══════════════════════════════════════════════════════
                //  ★★★ 2026-08-05 实机修复:同步此前只有「追增量」,没有「对齐」。
                //
                //  用户报「共享会话和家庭待办仍然无法双边共享」。查中枢的同步存档:
                //  **两台真机一条记录都没有**(只有测试夹具的),而本机存档里确实躺着
                //  合格的家庭待办与共享会话。根因:推送只在**变更那一刻**触发,
                //  而待推队列是**纯内存**的 —— 关一次 App 就没了,那些数据此后
                //  永远等不到下一次"变更",也就永远不会上去。
                // ══════════════════════════════════════════════════════
                {
                    var scSrc = TryReadSource(Path.Combine("Services", "SyncClient.cs"));
                    if (scSrc is not null)
                    {
                        var sc = CodeOnly(scSrc);
                        Assert(sc.Contains("ReconcileAsync"),
                               "★★★ 必须有【对齐】动作 —— 只补内存队列的话,关一次 App 那些数据就永远上不去");
                        Assert(sc.Contains("FullSet"),
                               "★★ 对齐的数据源由宿主注入(SyncClient 不该知道待办和会话长什么样)");
                        // ★ 连上那一刻走的必须是对齐,不是只 Flush 队列
                        var iLive = sc.IndexOf("Link = SyncLink.Live", StringComparison.Ordinal);
                        var iRec = sc.IndexOf("ReconcileAsync()", StringComparison.Ordinal);
                        Assert(iLive >= 0 && iRec >= 0, "★ 两处都在(位置判据的前提)");
                        Assert(iRec > iLive && iRec - iLive < 800,
                               "★★★ 一连上就要对齐 —— 这正是用户实测『仍然无法双边共享』的根因");
                        Assert(sc.Contains("Take(MaxPerPush)"),
                               "★★★ 推送必须切批:服务端 max_batch=200,超了**整批**拒,"
                               + "而被拒的一条都不出队 ⇒ 永远重推、永远失败(对齐大会话正好撞上)");
                    }
                    // ══════════════════════════════════════════════════════
                    //  ★★★ 2026-08-05 实机抓到的真根因,行为断言(不是查源码)。
                    //
                    //  一个订阅者在 Changed 里抛异常(实测:主页那行同步状态在
                    //  **后台线程**上写 WPF 的 TextBlock),异常从 RunAsync 里那句
                    //  裸 Changed?.Invoke() 逃出去,**掀翻整个订阅循环** ——
                    //  _loop 结束后没有任何东西会重启它,同步流永久死掉。
                    //  网关侧的表现:GET /v1/sync/events **一次请求都收不到**,
                    //  而 /v1/gpu/events 好好的(HubGpu 一直有 try/catch 护栏)。
                    // ══════════════════════════════════════════════════════
                    {
                        var probe = new Services.SyncClient(new Services.HubClient());
                        var blew = false;
                        probe.Changed += () => { blew = true; throw new InvalidOperationException("订阅者炸了"); };
                        var survived = true;
                        try { probe.Enqueue(new Services.SyncItem("todos", new { id = "__t", scope = "家庭" })); }
                        catch { survived = false; }
                        Assert(blew, "★ 元断言:那个会抛的订阅者确实被调到了(没调到的话下一条是空转)");
                        Assert(survived,
                               "★★★ 订阅者抛异常**不得**冒泡出来 —— 它会掀翻订阅循环,"
                               + "而循环一死就再没有东西重启它,同步流永久失联(实机实测过)");
                        probe.Dispose();
                    }
                    {
                        var sc2 = TryReadSource(Path.Combine("Services", "SyncClient.cs"));
                        if (sc2 is not null)
                        {
                            // ★ 判据是「只许出现一次」而不是「一次都不许」——
                            //   那唯一一次正是 Notify() 自己的实现。第一版写成"不许出现",
                            //   于是断言禁止了护栏本身,当场红了一条假红(判据比想判的东西宽)。
                            var raw = CodeOnly(sc2).Split("Changed?.Invoke()").Length - 1;
                            Assert(raw == 1,
                                   $"★★ 裸调用只许有 1 处(Notify 自己),实得 {raw} —— "
                                   + "其余必须全部走带 try/catch 的 Notify()");
                            Assert(CodeOnly(sc2).Contains("void Notify()"),
                                   "★ 护栏方法必须在(元断言:上一条数的是它)");
                        }
                        var hv = TryReadSource(Path.Combine("Views", "HomeView.cs"));
                        if (hv is not null)
                            Assert(CodeOnly(hv).Contains("syncLine.Dispatcher.CheckAccess()"),
                                   "★★★ 主页那行同步状态必须先切回 UI 线程 —— "
                                   + "护栏能防它拖垮同步,但界面本身写错了还是刷不出来。两头都要修");
                    }
                    // ══════════════════════════════════════════════════════
                    //  ★★★ 删除同步 + 先拉后推(2026-08-05 用户实测第 2、3 条)。
                    //  这两条是**同一个洞的两面**:没有删除语义时,「连上就对齐」会把
                    //  对方删掉的东西推回去 —— A 删了,B 开机不知情,把本地那份又推上来,
                    //  A 那边复活。所以删除必须做成会传播的墓碑,而且顺序必须先拉后推。
                    // ══════════════════════════════════════════════════════
                    {
                        // ① 墓碑的形状:带 id、带 deleted=true
                        var tomb = Services.TodoCenter.ToTombstone("abc");
                        var td = tomb.Record.GetType();
                        Assert(tomb.Kind == "todos"
                               && td.GetProperty("id")?.GetValue(tomb.Record)?.ToString() == "abc"
                               && Equals(td.GetProperty("deleted")?.GetValue(tomb.Record), true),
                               "★★ 待办墓碑必须带 id + deleted=true");
                        var sessTomb = Services.ChatCenter.ToTombstone("s1");
                        Assert(sessTomb.Kind == "sessions"
                               && Equals(sessTomb.Record.GetType().GetProperty("deleted")?.GetValue(sessTomb.Record), true),
                               "★★ 会话墓碑同款");

                        // ② ★★★ 删掉之后**不许**再出现在对齐集合里 —— 否则一对齐就复活
                        var tc = new Services.TodoCenter();
                        var id = tc.Add(new Services.TodoItem("", "要删的", Services.TodoKind.Chore, Scope: "家庭"));
                        Assert(tc.SharedSnapshot().Any(), "★ 元断言:加进去的确实在对齐集合里(否则下一条空转)");
                        tc.Remove(id);
                        Assert(!tc.SharedSnapshot().Any(),
                               "★★★ 删掉的东西不得再进对齐集合 —— 进了的话另一台一开机就把它推回去,"
                               + "删除永远删不掉");

                        // ③ 收到远端墓碑要真的删掉
                        var tc2 = new Services.TodoCenter();
                        var id2 = tc2.Add(new Services.TodoItem("", "远端要删的", Services.TodoKind.Chore, Scope: "家庭"));
                        Assert(tc2.AbsorbRemoteDelete(id2), "★★ 收到远端删除要真的删掉");
                        Assert(!tc2.Items.Any(x => x.Id == id2), "★ 且确实不在了");

                        // ④ 先拉后推:对齐必须挂在【第一帧 data 到手之后】
                        var sc3 = TryReadSource(Path.Combine("Services", "SyncClient.cs"));
                        if (sc3 is not null)
                        {
                            var c3 = CodeOnly(sc3);
                            var iAbsorb = c3.IndexOf("Absorb(line[6..])", StringComparison.Ordinal);
                            var iRec2 = c3.IndexOf("ReconcileAsync())", StringComparison.Ordinal);
                            Assert(iAbsorb >= 0 && iRec2 >= 0, "★ 两处都在(位置判据的前提)");
                            Assert(iAbsorb < iRec2,
                                   "★★★ 必须**先吃完中枢那帧全量再推** —— 反过来的话,"
                                   + "关机期间对方删掉的东西会被本机推回去,在对方那边复活");
                            Assert(c3.Contains("_pullFirst"),
                                   "★ 对齐由「第一帧到手」触发,不是「连上」触发 —— "
                                   + "连上但还没收到全量就推,和先推没有区别");
                        }
                    }
                    Assert(Services.SyncClient.MaxPerPush == 200,
                           $"★★ 单批上限必须与服务端 sync_policy 的 max_batch 一致(现 {Services.SyncClient.MaxPerPush})");

                    // ★★ Scope 存的是【界面文案】,判据不能只认中文那一个
                    Assert(Services.TodoCenter.IsFamily("家庭")
                           && Services.TodoCenter.IsFamily("Family")
                           && Services.TodoCenter.IsFamily("家族"),
                           "★★★ 三语界面下建的家庭待办都要认 —— 只认中文的话,"
                           + "英文/日文界面下建的家庭待办会**静默地永远不同步**");
                    Assert(!Services.TodoCenter.IsFamily("个人")
                           && !Services.TodoCenter.IsFamily("Personal")
                           && !Services.TodoCenter.IsFamily(null)
                           && !Services.TodoCenter.IsFamily(""),
                           "★★★ 反过来:个人档一个都不许认 —— 把私人东西推到另一台是不可撤销的错误");
                    // ★ 上线的一律是规范值:服务端的范围闸也是拿「家庭」比的,
                    //   送 "Family" 上去会被判 out_of_scope。
                    var enTodo = new Services.TodoItem("t1", "x", Services.TodoKind.Chore, Scope: "Family");
                    var wire = Services.TodoCenter.ToSyncItem(enTodo);
                    var wireScope = wire.Record.GetType().GetProperty("scope")?.GetValue(wire.Record)?.ToString();
                    Assert(wireScope == Services.TodoCenter.WireFamily,
                           $"★★★ 上线必须写规范值而不是界面文案(实得「{wireScope}」)");
                }
                var s12todo = TryReadSource(Path.Combine("Services", "TodoCenter.cs"));
                if (s12todo is not null)
                {
                    var s12tc = CodeOnly(s12todo);
                    Assert(!s12tc.Contains("Transport.") && !s12tc.Contains("HubClient") && !s12tc.Contains("http"),
                           "★★ 待办确实一个字节都不同步 —— 文案与行为一致(D57)");
                }

                // ★ 共享确实只是本机标记 —— 行为断言,不只是文案断言。
                //   (文案改对了而行为悄悄变了,或者反过来,都是本项目最恨的形状。)
                var s12cc = new Services.ChatCenter();
                var s12sid = s12cc.NewSession(null).SessionId;
                {
                    s12cc.Send(s12sid, "x");
                    var before = s12cc.Sessions.First(x => x.SessionId == s12sid);
                    Assert(!before.Shared, "新会话默认不共享(D52:默认只在本机)");
                    Assert(Services.ChatCenter.CanShare(before), "普通会话可提升");
                    Assert(s12cc.ShareSession(s12sid), "提升成功");
                    var after = s12cc.Sessions.First(x => x.SessionId == s12sid);
                    Assert(after.Shared, "标记确实改了");
                    Assert(!Services.ChatCenter.CanShare(after),
                           "★ 单向不可收回:已共享的不能再提升(也没有降级入口)");
                    var shareSrc = TryReadSource(Path.Combine("Services", "ChatCenter.cs"));
                    if (shareSrc is not null)
                    {
                        var body = Slice(shareSrc, "public bool ShareSession", "幽灵会话:不保留记录");
                        Assert(body is not null && !CodeOnly(body).Contains("Transport.")
                               && !CodeOnly(body).Contains("HubClient"),
                               "★★ ShareSession 确实【一个字节都没上传】—— 与文案一致。"
                               + "文案说没上传而代码偷偷传了,或反过来,都是骗人");
                    }
                }
            }

            // ══════════════════════════════════════════════════════════════
            //  P4-S13 客户端半边:内网同步(D86)
            //
            //  ★★★ 这一组守两件事:
            //    ① **私人数据不能被推出去** —— 把个人待办 / 未共享会话送到另一台机器上
            //       是**不可撤销**的错误(数据已经在对方硬盘里了)。客户端这道是第一关,
            //       服务端 sync_store.in_scope 是第二关 —— 两关都要在。
            //    ② **未同步必须看得见** —— 主机不在线时本地照常改,但不标出来的话,
            //       用户会以为另一台也看得到,而那边什么都没有
            //       (这正是他报的那件事)。
            // ══════════════════════════════════════════════════════════════
            {
                // ① 范围判据:客户端这一关
                Assert(Services.TodoCenter.ShouldSync(
                           new Services.TodoItem("a", "买菜", Services.TodoKind.Chore, Scope: "家庭")),
                       "家庭待办要同步");
                Assert(!Services.TodoCenter.ShouldSync(
                           new Services.TodoItem("b", "私事", Services.TodoKind.Personal, Scope: "个人")),
                       "★★★ 个人待办【不推】—— 推出去是不可撤销的错误");
                var tcSrc = TryReadSource(Path.Combine("Services", "TodoCenter.cs"));
                if (tcSrc is not null)
                {
                    var code = CodeOnly(tcSrc);
                    Assert(code.Contains("ShouldSync"), "推之前过范围判据");
                    Assert(code.Contains("PushIfShared"), "★ 写入口统一走一个推送函数,不各写各的");
                    // ★★ 反向全表:每个改内容的入口都要推,漏一个就是"改了不同步"
                    // ★ 结束标记用【下一个方法签名】,不用 "}" ——
                    //   方法体里的  自带一个 }, 用它做终点会把切片截得太短,
                    //   于是断言在代码明明推了的情况下判红(ASSERTION-PITFALLS 第 4 条:判据比想判的宽/窄)。
                    foreach (var (entry, endMark) in new[] {
                        ("public string Add", "public void Update"),
                        // ★ 终点标记必须是【CodeOnly 之后还在】的东西 —— 用注释里的字样会切不出来
                        ("public void Update", "public readonly List<string> DowngradedWhileShared"),
                        ("public void Toggle", "public static string NewId") })
                    {
                        var seg = Slice(code, entry, endMark);
                        Assert(seg is not null && seg.Contains("PushIfShared"),
                               $"★★ {entry.Split(' ')[^1]} 也要推 —— 漏一个入口就是「改了但不同步」");
                    }
                    Assert(code.Contains("AbsorbRemote"), "能合并远端来的家庭待办");
                    Assert(code.Contains("DowngradedWhileShared"),
                           "★★ 家庭→个人降级时中枢那份【不删】,如实记着待裁 —— "
                           + "删了会让另一台机器上的条目凭空消失,而那台的用户没做过任何事");
                }

                // ② 会话侧:只推共享的,且整段一起
                var ccSync = TryReadSource(Path.Combine("Services", "ChatCenter.cs"));
                if (ccSync is not null)
                {
                    var code = CodeOnly(ccSync);
                    Assert(code.Contains("PushWholeSession"),
                           "★★ 提升为共享时【整段对话一起上】(D52 规则 A:只共享片段对方读不懂)");
                    Assert(code.Contains("if (Sync is null || !s.Shared) return;")
                           || code.Contains("!s.Shared"),
                           "★★★ 只推 Shared 的会话 —— 普通会话继续本机独立(D52)");
                    var pw = Slice(code, "void PushWholeSession", "public bool ShareSession");
                    Assert(pw is null || pw.Contains("ChatRole.System"),
                           "★ System 消息不推 —— 那是客户端自己的界面文案,"
                           + "推过去等于让另一台机器看我们的 UI 提示");
                    Assert(code.Contains("AbsorbRemoteSession") && code.Contains("AbsorbRemoteMessage"),
                           "能合并远端来的会话与消息");
                    var abs = Slice(code, "public bool AbsorbRemoteSession", "public bool AbsorbRemoteMessage");
                    Assert(abs is null || abs.Contains("with {"),
                           "★★ 合并而不是整条替换 —— 本地有些字段中枢上根本没有,替换会把它们冲掉");
                }

                // ③ 未同步必须看得见
                var sc = new Services.SyncClient(new Services.HubClient());
                Assert(sc.StatusLine() == "",
                       "★★ 一切正常时【什么都不说】—— 常驻的「已同步」会被当成背景噪声,"
                       + "真出事那天也就没人看了");
                sc.Enqueue(new Services.SyncItem("todos", new { id = "x" }));
                Assert(sc.PendingCount == 1, "排进待推队列");
                Assert(sc.StatusLine().Length > 0, "★★★ 有没推上去的东西时【必须说出来】");
                // ★★ 判据要盯【关键情形】,不能用 || 放宽:
                //   「没连上 + 有积压」是最危险的一种 —— 用户以为另一台也看得到,而那边什么都没有。
                //   ★ 我第一版写的是 Contains("看不到") || Contains("同步"),
                //     红测时把那条分支整个去掉,回退文案「正在同步 N 项…」含「同步」照样过 ——
                //     一条**放宽到抓不住东西**的断言(ASSERTION-PITFALLS 第 4 条)。
                Assert(!sc.IsLive && sc.PendingCount > 0, "构造的就是「没连上 + 有积压」");
                Assert(sc.StatusLine().Contains("看不到"),
                       "★★★ 没连上又有积压时,必须说清【别的设备现在看不到】—— "
                       + "只说「正在同步」会让人以为马上就好了");
                sc.Enqueue(new Services.SyncItem("todos", new { id = "x" }));
                Assert(sc.PendingCount == 1,
                       "★ 同一条只留最后一版 —— 连改十次,补推时推一条就够");
                sc.Enqueue(new Services.SyncItem("todos", new { id = "y" }));
                Assert(sc.PendingCount == 2, "不同记录各算一条");
                Assert(!sc.IsLive, "★ 没连上就不是 Live —— 界面据此说话");
                Assert(Services.SyncClient.StaleAfter.TotalSeconds > 15,
                       "★ 判活阈值大于服务端心跳(15 秒),否则正常心跳也会被判死");
                // ★★★ 「推不上去【不丢】」是离线行为的承重条款,而行为断言在自检里够不到
                //   (没有 hub 时 FlushAsync 在进 try 之前就 return 了,catch 根本不执行)。
                //   ⇒ 用源码级断言守住:catch 块里**不得**有任何清空/移除待推队列的动作。
                //   ★ 这条是 2026-08-05 红测时发现的漏洞:我注入「catch 里清空队列」,
                //     一条断言都没红 —— 说明那条性质当时**根本没被测到**。
                var syncSrc = TryReadSource(Path.Combine("Services", "SyncClient.cs"));
                if (syncSrc is not null)
                {
                    var flush = Slice(CodeOnly(syncSrc), "public async Task<SyncPushResult?> FlushAsync",
                                      "public static SyncPushResult ParsePush");
                    Assert(flush is not null, "取得 FlushAsync 的源码(提取器没静默失灵)");
                    var catchPart = flush is null ? null : Slice(flush, "catch (Exception ex)", "}");
                    Assert(catchPart is not null
                           && !catchPart.Contains("_pending.Clear")
                           && !catchPart.Contains("_pending.RemoveAll"),
                           "★★★ 推失败的分支里【绝不清队列】—— 清了就是静默丢掉用户的改动,"
                           + "而他以为已经同步了");
                    Assert(flush is null || flush.Contains("res.Items"),
                           "★★ 只把服务端【明确处理过】的出队 —— 网络失败的留着下次补推");
                }

                // ④ 推送结果逐条解析(一批里有的收有的拒)
                var pr = Services.SyncClient.ParsePush(
                    "{\"accepted\":1,\"total\":2,\"results\":[" +
                    "{\"kind\":\"todos\",\"id\":\"a\",\"ok\":true,\"superseded\":true}," +
                    "{\"kind\":\"todos\",\"id\":\"b\",\"ok\":false,\"message\":\"个人待办不同步\"}]}");
                Assert(pr.Accepted == 1 && pr.Total == 2, "逐条计数对");
                Assert(pr.Items.Count == 2, "★★ 逐条回结果 —— 合成一个布尔会让客户端不知道哪条没上去");
                Assert(pr.Items.Any(x => !x.ok && x.why.Contains("个人")),
                       "★ 被拒那条的理由留着(不当成推成功了)");
                Assert(pr.Superseded,
                       "★★ 服务端说被覆盖了就记下来 —— 界面据此提示「这条被另一台改过」");
                var badPr = Services.SyncClient.ParsePush("这不是 json");
                Assert(badPr.Accepted == 0 && badPr.Items.Count == 0,
                       "★ 解析不了就当没收到,**不假装成功**");

                // ⑤ 措辞:同步做出来之后,那些「还没做」的话必须跟着改
                var cvSync = TryReadSource(Path.Combine("Views", "ChatView.cs"));
                if (cvSync is not null)
                {
                    var t = NoComments(cvSync);
                    Assert(!t.Contains("会话同步还没有做"),
                           "★★★ 同步做出来了,那句「还没有做」必须跟着改 —— "
                           + "这是同一晚同一类问题的第四次");
                    Assert(t.Contains("立刻") && t.Contains("上传到中枢"),
                           "★ 改成如实说:整段会立刻上传");
                    Assert(t.Contains("未同步"),
                           "★★ 并说清主机不在线时会排队、界面会显示未同步");
                }
                var teSync = TryReadSource(Path.Combine("Views", "TodoEditor.cs"));
                if (teSync is not null)
                {
                    var t = NoComments(teSync);
                    Assert(!t.Contains("待办只存在这台电脑上"),
                           "★★★ 家庭待办现在真的同步了,那句「只存在这台电脑上」必须改");
                    Assert(t.Contains("会同步到其它已配对设备") && t.Contains("不同步"),
                           "★★ 逐档说清:家庭会同步 / 个人不同步 —— 笼统的说法失信最快");
                }
                var hvSync = TryReadSource(Path.Combine("Views", "HomeView.cs"));
                if (hvSync is not null)
                    Assert(hvSync.Contains("StatusLine()"),
                           "★★ 待办面板上有同步状态行(未同步必须看得见)");
            }

            // ══════════════════════════════════════════════════════════════
            //  P4-S15:Apple 日历【启动时拉一次】(用户要求 2026-08-05)
            //
            //  ★★★ 这一组守的核心不是"有没有拉",是**没有另开一条绕过闸的路**。
            //    AppleAutoSync 的文件头把「什么时候不要跑」写得极硬:
            //    认证失败会把用户**真实的 Apple ID 锁掉**(得去 iforgot.apple.com 重置),
            //    而"自动"正是最危险的形态 —— 手动失败用户会停下来看,自动失败没人看着,
            //    它能安安静静撞一整夜。
            //    ⇒ 启动拉取必须**复用 TickAsync**,一道闸都不另写。
            //      写第二遍就意味着有一天两边会不一致 —— 这是本项目反复吃过的亏
            //      (chat 与 GPU 面各写各的档位判断,结果 §6.8「绝不放行」在 GPU 面完全失效)。
            // ══════════════════════════════════════════════════════════════
            {
                var asSrc = TryReadSource(Path.Combine("Services", "AppleAutoSync.cs"));
                if (asSrc is not null)
                {
                    var code = CodeOnly(asSrc);
                    Assert(code.Contains("PullOnStartup"), "★ 有启动拉取入口");
                    var body = Slice(code, "public static void PullOnStartup", "public static readonly TimeSpan StartupDelay");
                    Assert(body is not null, "取得 PullOnStartup 的源码(提取器没静默失灵)");
                    Assert(body is null || body.Contains("TickAsync"),
                           "★★★ 启动拉取【复用 TickAsync】—— 五道闸(关着/熔断/软暂停/忙/没网)"
                           + "原封不动生效。另写一遍就意味着有一天两边会不一致");
                    Assert(body is null
                           || (!body.Contains("PullAsync") && !body.Contains("NetworkUp")
                               && !body.Contains("TrippedReason")),
                           "★★★ 启动拉取里【不得】自己判网络/熔断/直接调 PullAsync —— "
                           + "那就是把五道闸重写了一遍");
                    Assert(Services.AppleAutoSync.StartupDelay.TotalSeconds > 0,
                           "★★ 不在启动瞬间拉:那时网络栈可能还没就绪,会得到一次假的「连不上」,"
                           + "而连续三次就触发软暂停 —— 白白停掉自动拉取");
                    Assert(Services.AppleAutoSync.StartupDelay.TotalSeconds <= 30,
                           "★ 但也别拖太久 —— 用户打开 APP 就是想看今天的日程");
                }
                var s15app = TryReadSource("App.xaml.cs");
                if (s15app is not null)
                {
                    var code = CodeOnly(s15app);
                    Assert(code.Contains("AppleAutoSync.PullOnStartup"),
                           "★ 启动路径上真的调了它(函数还在、调用点没了 是编译与行为都抓不到的缺陷)");
                    Assert(code.IndexOf("AppleAutoSync.Install", StringComparison.Ordinal)
                           < code.IndexOf("AppleAutoSync.PullOnStartup", StringComparison.Ordinal),
                           "★★ 必须在 Install 之后调 —— Install 才注入 settings/owner,"
                           + "顺序反了 PullOnStartup 会因为 _settings is null 直接静默返回");
                }
            }

            // ---- 审计 2026-07-31 批次二:UI/皮肤/性能 ----
            {
                var hv = TryReadSource(Path.Combine("Views", "HomeView.cs"));
                var mw2 = TryReadSource("MainWindow.xaml.cs");
                var vm2 = TryReadSource(Path.Combine("Services", "VramMonitor.cs"));
                var cc4 = TryReadSource(Path.Combine("Services", "ChatCenter.cs"));
                var ic2 = TryReadSource(Path.Combine("Theme", "Icons.cs"));
                if (hv is not null)
                {
                    Assert(hv.Contains("Math.Max(1, _tileCount)"),
                           "★ 主页田字格列数上限用【实际铺的方块数】,不是全部项目数(否则右侧空一列)");
                    Assert(hv.Contains("_hostWin.WindowState != WindowState.Minimized") && hv.Contains("SyncClockTimer()"),
                           "★ 主页秒针表最小化/缩到托盘时停(最小化时 IsVisible 仍 true,得盯窗口状态)");
                }
                if (mw2 is not null)
                    Assert(mw2.Contains("who.IsGuess ? \"FgSecondary\" : \"FgOnSelected\""),
                           "★ 头像首字前景跟着底色走(墨白皮肤下白字压近白底看不见)");
                // ★ 原来这里钉的是 `_smiDead`(nvidia-smi 读不到就死心)。本机读取路径
                //   2026-08-05 整条删掉了 —— 显存永远显示主机的,没有本机可退。
                //   ⇒ 这条断言连同它守的东西一起消失,**不留一个恒真的壳**。
                //   反向判据在上面那一组(本机读取路径必须删干净)。
                if (cc4 is not null)
                    Assert(cc4.Contains("Dictionary<string, int> _archiveCount") && cc4.Contains("_archiveCount.Remove"),
                           "★ 归档条数缓存 —— 不再每次会话区重建都整档读盘");
                if (ic2 is not null)
                    Assert(ic2.Contains("SweepDead()") && ic2.Contains("_sweepAt"),
                           "★ Icons.Live 摄还清扫 —— 不再只靠换肤回收(常驻托盘无界增长)");
            }

            // ---- 界面用词表:家庭 / 团队(用户裁定 2026-07-31)----
            {
                var before = Services.Vocab.Current;
                try
                {
                    // 默认档 = 家庭:原样返回,零替换
                    Services.Vocab.Current = Services.OrgVocab.Family;
                    Assert(Services.Vocab.Apply("家庭成员") == "家庭成员", "默认(家庭)不做任何替换");

                    Services.Vocab.Current = Services.OrgVocab.Team;
                    Assert(Services.Vocab.Apply("家庭成员") == "团队成员", "★ 选团队后界面用词整体替换");
                    Assert(Services.Vocab.Apply("暂无家庭范围的消息。") == "暂无团队范围的消息。", "句中的称谓也换");
                    // ★★ 专有名词不许动 —— Apple 的功能就叫「家庭共享」,换成"团队共享"是把产品名说错
                    Assert(Services.Vocab.Apply("Apple 家庭共享日历:计划接入").Contains("Apple 家庭共享"),
                           "★★ Apple 家庭共享是产品名,不跟着换(换了就是伪造信息)");
                    // 同一句里既有产品名又有我们的称谓:各归各的
                    Assert(Services.Vocab.Apply("家庭日历与 Apple 家庭共享同步") == "团队日历与 Apple 家庭共享同步",
                           "★ 一句里产品名保留、我们的称谓照换");
                    // 其它语言
                    var lang0 = I18n.Strings.Language;
                    try
                    {
                        I18n.Strings.Language = "en-US";
                        Assert(Services.Vocab.Apply("Family members") == "Team members", "英文也换(含大小写两式)");
                        I18n.Strings.Language = "ja-JP";
                        Assert(Services.Vocab.Apply("家族メンバー") == "チームメンバー", "日文也换");
                    }
                    finally { I18n.Strings.Language = lang0; }

                    // 下拉自己的选项名【不过】替换 —— 否则"家庭"那一项会显示成"团队"
                    Assert(Services.Vocab.LabelOf(Services.OrgVocab.Family, "zh-CN") == "家庭",
                           "★ 下拉里「家庭」这一项永远显示「家庭」(它是选项名,不是文案)");

                    // ★★ 数据安全:日历分组表是【存储键】,绝不能被替换掉
                    Assert(Views.CalendarData.Groups.Contains("家庭"),
                           "★★ 日历分组的【存储值】仍是原词 —— 换掉会让老档案匹配不上、日程被静默改组");
                }
                finally { Services.Vocab.Current = before; }
            }

            // 用词表的【挂点】—— 两处中央入口,漏一处就有半边界面不跟着变
            {
                var strSrc = TryReadSource(Path.Combine("I18n", "Strings.cs"));
                var uiSrc = TryReadSource(Path.Combine("Views", "Ui.cs"));
                var ceSrc = TryReadSource(Path.Combine("Views", "CalendarEditor.cs"));
                if (strSrc is not null)
                    Assert(strSrc.Contains("Services.Vocab.Apply(v)") && strSrc.Contains("Services.Vocab.Apply(zh)"),
                           "★ Strings.Get 出口过用词表(覆盖 strings.json 全部文案)");
                if (uiSrc is not null)
                    Assert(uiSrc.Split("Services.Vocab.Apply(").Length - 1 >= 5,
                           "★ Ui 的 Title/Subtitle/Body/Caption/Panel 都过用词表(覆盖代码里硬编码的中文)");
                if (ceSrc is not null)
                    Assert(ceSrc.Contains("Text = Services.Vocab.Apply(g)")
                           && ceSrc.Contains("Fill = new System.Windows.Media.SolidColorBrush(Services.CalendarGroups.ColorOf(g))")
                           && ceSrc.Contains("CalendarGroup: CalendarData.Groups["),
                           "★★ 日历分组:【显示】过用词表、【存储】仍用原值(否则老档案匹配不上、日程被静默改组)");
            }

            // ---- 日程分类与颜色:读自 Apple(用户裁定 2026-07-31)----
            {
                var cg = TryReadSource(Path.Combine("Services", "CalendarGroups.cs"));
                if (cg is not null)
                {
                    Assert(cg.Contains("public static void SetFromApple"),
                           "★ 分类表可以整表换成 Apple 那边的日历清单");
                    Assert(cg.Contains("_current = list.Count > 0 ? list : LocalDefaults.ToList();"),
                           "★★ 传空 = 回到本地占位分类(断开连接时不许拿上一次的缓存假装还连着)");
                    Assert(cg.Contains("public static bool FromApple"),
                           "★ 界面能问出「这张表到底是不是真的来自 Apple」");
                    Assert(cg.Contains("int h = 17;") && !cg.Contains(".GetHashCode()"),
                           "★★ 认不出的分类按名字算【稳定】色 —— 不用 GetHashCode:"
                           + ".NET 的字符串哈希每次进程启动都不同,那会让颜色每次开机都变");
                }
                // Apple 那边的颜色在私有命名空间里,PROPFIND 得显式要
                var acd = TryReadSource(Path.Combine("Services", "AppleCalDav.cs"));
                if (acd is not null)
                    Assert(acd.Contains("AppleIcalNs = \"http://apple.com/ns/ical/\"")
                           && acd.Contains("<i:calendar-color/>"),
                           "★ PROPFIND 显式索取 Apple 的 calendar-color");

                // 归一化:Apple 给的是 #RRGGBBAA
                Assert(Services.CalendarGroups.Normalize("#FF2968FF") == "#FF2968", "#RRGGBBAA -> #RRGGBB");
                Assert(Services.CalendarGroups.Normalize("#abc") == "#AABBCC", "#RGB -> #RRGGBB");
                Assert(Services.CalendarGroups.Normalize("  1A2B3C ") == "#1A2B3C", "没有 # 也认");
                Assert(Services.CalendarGroups.Normalize("不是颜色") is null, "不是颜色就返回 null(不瞎猜一个)");
                Assert(Services.CalendarGroups.Normalize("") is null && Services.CalendarGroups.Normalize(null) is null, "空 -> null");

                // 稳定色:同名同色
                Assert(Services.CalendarGroups.StableColor("工作") == Services.CalendarGroups.StableColor("工作"),
                       "同一个分类名每次都得到同一个颜色");
                Assert(Services.CalendarGroups.StableColor("工作") != Services.CalendarGroups.StableColor("家庭"),
                       "不同分类名给出不同颜色");

                // 字色按底色亮度反选
                Assert(Services.CalendarGroups.TextOn(System.Windows.Media.Color.FromRgb(0x10, 0x20, 0x30))
                       == System.Windows.Media.Colors.White, "深底 -> 白字");
                Assert(Services.CalendarGroups.TextOn(System.Windows.Media.Color.FromRgb(0xF0, 0xF0, 0xE0))
                       != System.Windows.Media.Colors.White, "浅底 -> 深字");

                // 整表往返
                Services.CalendarGroups.SetFromApple(new[] { ("工作日历", (string?)"#112233"), ("家人", null) });
                Assert(Services.CalendarGroups.FromApple && Services.CalendarGroups.Names.Length == 2,
                       "换成 Apple 的两个日历");
                Assert(Services.CalendarGroups.ColorOf("工作日历")
                       == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#112233"),
                       "Apple 给了颜色就用它");
                Assert(Services.CalendarGroups.ColorOf("家人") == Services.CalendarGroups.StableColor("家人"),
                       "★ Apple 没给颜色的那个,退回按名字算的稳定色(不是随便挑一个)");
                Services.CalendarGroups.SetFromApple(Array.Empty<(string, string?)>());
                Assert(!Services.CalendarGroups.FromApple && Views.CalendarData.Groups.Contains("家庭"),
                       "★ 断开后回到本地占位分类");
            }

            // ---- 天气接入(设计 §4.1 / 状态矩阵 §8 第 6 条)----
            {
                // ★★ 这份样本是【真的从 api.open-meteo.com 取回来的】(2026-08-01,科隆坐标)。
                //   解析器必须对着真实应答钉,而不是对着我自己想象的形状钉 ——
                //   VB-CABLE 那次就是对着想象的签名人写校验,差点把正版安装包拒了。
                const string omSample = "{\"current\":{\"time\":\"2026-08-01T00:00\",\"interval\":900,\"temperature_2m\":23.0,\"precipitation\":0.0,\"weather_code\":0},\"daily\":{\"time\":[\"2026-08-01\"],\"temperature_2m_max\":[27.8],\"temperature_2m_min\":[19.3]},\"hourly\":{\"time\":[\"2026-08-01T00:00\",\"2026-08-01T01:00\"],\"temperature_2m\":[23.0,22.4],\"weather_code\":[0,1]}}";
                var w = Services.Weather.Parse(omSample);
                Assert(w is not null, "能解析 Open-Meteo 的真实应答");
                Assert(w!.TempC == 23.0 && w.PrecipMm == 0.0 && w.WeatherCode == 0, "当前温度/降水/天气代码都取对");
                Assert(w.HighC == 27.8 && w.LowC == 19.3, "今日最高/最低取对");
                Assert(w.Hours is { Count: 2 } && w.Hours[1].TempC == 22.4 && w.Hours[1].Code == 1, "逐小时取对");

                // ★ 缺段只让那一项为 null,不整份丢掉
                var w2 = Services.Weather.Parse("{\"current\":{\"temperature_2m\":5.0}}");
                Assert(w2 is not null && w2.TempC == 5.0 && w2.HighC is null && w2.Hours is null,
                       "★ 缺段只让那一项为 null —— 有多少说多少,比「要么全有要么全无」有用");
                Assert(Services.Weather.Parse("不是 json") is null, "垃圾进去 -> null,不抛不编");

                // ★★ 诚实:没有 UpdatedAt 就是 stale;认不出的代码要如实说"未知"
                Assert(w.IsStale, "刚解析出来、还没盖时间戳的读数算 stale");
                Assert(Services.Weather.Describe(0) == "晴" && Services.Weather.Describe(95) == "雷阵雨",
                       "WMO 代码转中文");
                Assert(Services.Weather.Describe(null) is null, "没代码 -> null(不说晴)");
                Assert(Services.Weather.Describe(12345)!.Contains("未知"),
                       "★ 认不出的代码【如实说未知】,不硬塞成晴");

                // ★★ 出境纪律:固定白名单端点 + 只发坐标
                var wsrc = TryReadSource(Path.Combine("Services", "Weather.cs"));
                if (wsrc is not null)
                {
                    Assert(wsrc.Contains("public const string Host = \"api.open-meteo.com\""),
                           "★★ 天气只走【固定白名单端点】(设计 §4.1 / D39)");
                    Assert(wsrc.Contains("Math.Round(lat, 1)") && wsrc.Contains("Math.Round(lon, 1)"),
                           "★★ 坐标就地取整到 0.1°(约 11km) —— 够天气用,不足以指到街区");
                    Assert(!wsrc.Contains("city") || !wsrc.Contains("&name="),
                           "★ 请求里没有城市名/文字地址");
                    Assert(wsrc.Contains("UseProxy = false"), "不走系统代理(与 CalDAV 同一纪律)");
                }
                var hvw = TryReadSource(Path.Combine("Views", "HomeView.cs"));
                if (hvw is not null)
                {
                    Assert(hvw.Contains("暂时取不到 · 显示上次"),
                           "★★ 过期的读数【如实标出它是什么时候的】—— 无假实时(状态矩阵 §8 第 6 条)");
                    // ★★ 数据来源署名已按用户裁定去掉(2026-08-01)。
                    //   CC BY 的署名义务在【对外分发】时才触发,这个客户端自家自用不分发。
                    //   ★ 这条断言钉的是【字串还在词表里】—— 将来要分发给家庭以外的人时,
                    //   直接把它接回界面就行,不用重新去查许可。
                    Assert(!hvw.Contains("weather.source_credit"),
                           "★ 界面上不再显示数据来源(用户裁定;不对外分发时 CC BY 的署名义务未触发)");
                    Assert(!string.IsNullOrWhiteSpace(I18n.Strings.Get("weather.source_credit")),
                           "★★ 但署名字串【保留在词表里】—— 一旦要分发给家庭以外的人,必须把它接回界面");
                    Assert(hvw.Contains("if (Services.Places.CoordOf(p) is not { } c) continue;"),
                           "★ 认不出坐标的城市直接跳过 —— 不拿别处的坐标顶替");
                    var appw = TryReadSource("App.xaml.cs");
                    if (appw is not null)
                        Assert(appw.Contains("Services.Weather.Changed += Touch;")
                               && appw.Contains("S(ClientStore.WeatherPath, Services.Weather.Export());"),
                               "★★ 天气缓存【真的会落盘】—— 不接变更通知的话它只活在内存里,"
                               + "重启/断网时「显示上次那份」无从谈起");
                    Assert(hvw.Contains("CultureInfo.InvariantCulture) + \"mm\""),
                           "★ 降水量的小数点强制用点 —— 系统区域是德国时会得到 \"0,2mm\"");
                    Assert(hvw.Contains("RefreshWeatherUi();"),
                           "★ 卡片刚建好就刷一次读数 —— 只靠 Loaded 的话,拖拽排序后与离屏渲染里都是空态");
                    var ico = TryReadSource(Path.Combine("Theme", "Icons.cs"));
                    if (ico is not null)
                        Assert(ico.Contains("public static IconName? ForWeather(int? code, bool night = false)") && ico.Contains("_ => null,"),
                               "★★ 认不出的天气代码【不画图标】—— 随便给个太阳等于替天气编了个说法");
                    // 夜间的"晴"该是月亮(用户裁定)
                    Assert(Theme.Icons.ForWeather(0, night: false) == Theme.IconName.WxSun
                           && Theme.Icons.ForWeather(0, night: true) == Theme.IconName.WxMoon,
                           "★ 夜间的晴 = 月亮");
                    Assert(Theme.Icons.ForWeather(2, night: true) == Theme.IconName.WxPartlyNight,
                           "★ 夜间的局部多云 = 月亮 + 云");
                    Assert(Theme.Icons.ForWeather(61, night: true) == Theme.Icons.ForWeather(61, night: false),
                           "★ 雨/雪/阴/雾 不分昼夜 —— 本来就看不见日月,分了只多出认不出的图");
                    Assert(!string.IsNullOrWhiteSpace(Theme.Icons.PathFor(Theme.IconName.WxMoon, Services.Skin.Ink)),
                           "★ 月亮图形三套皮肤都取得到");

                    // 降水展望:当前无雨时要说得出几天后有雨;真没有就明说
                    var wxT0 = DateTime.Today;
                    var wRain = new Services.WeatherNow(20, 25, 15, 0, 0, DateTime.Now, null, new()
                    {
                        new(wxT0, 0), new(wxT0.AddDays(1), 0), new(wxT0.AddDays(2), 0), new(wxT0.AddDays(3), 4.2),
                    });
                    Assert(Services.Weather.RainOutlook(wRain) == "3 天后有雨", "★ 算得出几天后有雨");
                    var wDry = new Services.WeatherNow(20, 25, 15, 0, 0, DateTime.Now, null, new()
                    {
                        new(wxT0, 0), new(wxT0.AddDays(1), 0), new(wxT0.AddDays(2), 0.05),
                    });
                    Assert(Services.Weather.RainOutlook(wDry) == "未来 2 天无雨",
                           "★★ 窗口里真的没有就【明说多少天无雨】—— 不含糊其辞;"
                           + "0.05mm 这种痕量不算「有雨」(门槛 0.2mm)");
                    Assert(Services.Weather.RainOutlook(new Services.WeatherNow(20, 25, 15, 0, 0, DateTime.Now)) is null,
                           "★ 压根没逐日数据 -> null(界面只写「无」,不假装知道以后的事)");
                    Assert(hvw.Contains("NetworkInterface.GetIsNetworkAvailable()"),
                           "★ 没网就不试(与 Apple 自动拉取同一条规矩)");
                }
            }

            // ---- 分类不会被静默改掉 / 同步会刷新分类表 ----
            {
                var ce3 = TryReadSource(Path.Combine("Views", "CalendarEditor.cs"));
                if (ce3 is not null)
                {
                    Assert(ce3.Contains("var gi = keepGroup is null ? 0 : Array.IndexOf(CalendarData.Groups, keepGroup);")
                           && ce3.Contains("（不在当前日历清单里）"),
                           "★★ 分类认不出来就【临时插一项原值】并选中 "
                           + "—— 回落到第 0 项会让用户只改标题一保存就把分类永久改掉");
                    Assert(ce3.Contains("(group.SelectedItem as ComboBoxItem)?.Tag as string"),
                           "★ 保存按 Tag 取值,不按 index 反查");
                    Assert(ce3.Contains("Ui.Caption(Services.CalendarGroups.SourceNote)"),
                           "★ 分类下拉旁边如实说明这张表从哪来(本机占位 / 来自 iCloud)");
                }
                var acs = TryReadSource(Path.Combine("Services", "AppleCalendarSync.cs"));
                if (acs is not null)
                    Assert(acs.Contains("CalendarGroups.SetFromApple(cals.Select(c => (c.DisplayName, c.ColorHex, (string?)c.Url)));"),
                           "★★ 每次拉取都刷新分类表(并带上 Url 供重名去重) —— 否则 iCloud 改名/改色后永远对不上且不会自愈");

                // 重名日历要能分得开(iCloud 里非常常见:自己的"家庭"与别人共享给你的"家庭")
                Services.CalendarGroups.SetFromApple(new[]
                {
                    ("家庭", (string?)"#111111", (string?)"https://p1.icloud.com/a/"),
                    ("家庭", (string?)"#222222", (string?)"https://p1.icloud.com/b/"),
                });
                var nm = Services.CalendarGroups.Names;
                Assert(nm.Length == 2 && nm[0] != nm[1],
                       "★★ 重名日历【去重成两个不同的存储值】—— 否则两个日历共用第一个的颜色,"
                       + "下拉里也出现两行一模一样的项,选哪个都一样");
                Assert(Services.CalendarGroups.ColorOf(nm[0]) != Services.CalendarGroups.ColorOf(nm[1]),
                       "★ 重名日历各用自己的颜色");
                Assert(Services.CalendarGroups.ShortTag("https://p1.icloud.com/a/") == Services.CalendarGroups.ShortTag("https://p1.icloud.com/a/"),
                       "★ 区分用的短码是【稳定】的(不用序号 —— 序号会随 iCloud 返回顺序变)");
                Assert(Services.CalendarGroups.SourceNote.Contains("iCloud"),
                       "★ FromApple 不只是个属性 —— 有一句可直接给界面用的如实说明");
                Services.CalendarGroups.SetFromApple(Array.Empty<(string, string?)>());

                // 跨天定时日程的"结束在哪天"必须只有一套口径
                var ev9 = new Views.CalendarEvent(
                    new DateTime(2026, 7, 31, 22, 0, 0), new DateTime(2026, 8, 1, 3, 0, 0),
                    "通宵", "me", "private");
                Assert(ev9.LastDay == new DateTime(2026, 8, 1) && ev9.Covers(new DateTime(2026, 8, 1)),
                       "★★ 定时跨天日程在【结束那天】也算数 —— 否则月历没圆点、当日列表查无此条,"
                       + "而下方时间轴却画着它(同一块板块上下两半各说各的)");
                var ev10 = new Views.CalendarEvent(
                    new DateTime(2026, 7, 31, 23, 0, 0), new DateTime(2026, 8, 1, 0, 0, 0),
                    "到点就结束", "me", "private");
                Assert(ev10.LastDay == new DateTime(2026, 7, 31),
                       "★ 结束恰好是次日 00:00 的不多占一格");

                var wp2 = TryReadSource(Path.Combine("Views", "WheelPicker.cs"));
                if (wp2 is not null)
                    Assert(wp2.Contains("PreviewMouseLeftButtonDown += (_, e) =>")
                           && wp2.Contains("_dragMoved = true;") && wp2.Contains("CaptureMouse();"),
                           "★★ 转盘按下【不】捕获,真的越过阈值才捕 —— CaptureMode.Element 下子元素不在路由上,"
                           + "按下就捕会把「点某一行」打死");
            }

            // ---- 从 Apple 拉下来的日程要带【分类】----
            {
                // ★★ 之前 ParseEvents 根本不填 CalendarGroup:拉下来的日程全是无分类,
                //   于是在界面上全一个颜色、也对不上任何一个日历(用户反馈"分类不对")。
                const string oneIcs =
                    "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:x-1\r\nSUMMARY:开会\r\n" +
                    "DTSTART:20260731T090000\r\nDTEND:20260731T100000\r\nEND:VEVENT\r\nEND:VCALENDAR";
                var (evs0, _) = Services.ICalParser.ParseEvents(oneIcs, "me", "private", "工作");
                Assert(evs0.Count == 1 && evs0[0].CalendarGroup == "工作",
                       "★★ 拉下来的日程带上【所在 iCloud 日历的名字】作为分类");
                var (evs1, _) = Services.ICalParser.ParseEvents(oneIcs, "me", "private", "   ");
                Assert(evs1.Count == 1 && evs1[0].CalendarGroup is null,
                       "★ 日历名是空白 -> 分类留 null(不填一个空字串冒充)");
                Assert(evs0[0].Source == "apple", "来源标成 apple");
            }

            // ---- Apple 凭据保管(日历接入)----
            {
                // ★ 安全:本函数开头已把 LOCALAI_CLIENT_STATE 指到临时目录,
                //   这里的 Clear() 动的是临时档,碰不到用户真实的 Apple 账号配置。
                {
                    Services.AppleCredentials.Clear();
                    Assert(Services.AppleCredentials.Load() is null, "没配过时读到 null");

                    Assert(Services.AppleCredentials.Save("someone@example.com", "abcd-efgh-ijkl-mnop"), "能存下账号");
                    var info = Services.AppleCredentials.Load();
                    Assert(info is not null && info.AppleId == "someone@example.com" && info.HasPassword,
                           "读回账号 + 标记已有密码");
                    Assert(Services.AppleCredentials.Reveal() == "abcd-efgh-ijkl-mnop", "DPAPI 加解密往返一致");

                    // ★★ 落盘的文件里【绝不能】出现明文密码
                    var raw = System.IO.File.ReadAllText(
                        System.IO.Path.Combine(Services.AppPaths.StateDir, "apple-account.json"));
                    Assert(!raw.Contains("abcd-efgh-ijkl-mnop"),
                           "★★ 密码不以明文落盘(DPAPI 加密后才写)");
                    Assert(raw.Contains("someone@example.com"), "Apple ID 本身可明文(界面要显示)");

                    Services.AppleCredentials.Clear();
                    Assert(Services.AppleCredentials.Load() is null && Services.AppleCredentials.Reveal() is null,
                           "断开连接 = 本机凭据清干净");

                    // ★ 抹去敏感文本 —— 异常消息常带着 Authorization 头,直接写 crash.log 就泄了
                    Assert(!Services.AppleCredentials.Redact("Authorization: Basic dXNlcjpwYXNz").Contains("dXNlcjpwYXNz"),
                           "★ Basic 认证头被抹掉(否则会落进 crash.log)");
                    Assert(!Services.AppleCredentials.Redact("密码 abcd-efgh-ijkl-mnop 错误").Contains("abcd-efgh"),
                           "★ 专用密码的形状也被抹掉");
                }
                Services.AppleCredentials.Clear();
            }

            // ---- iCalendar 解析(Apple 日历拉取用)----
            {
                // 用 Environment.NewLine 拼,避开转义;解析器对 CRLF/LF 都兼容
                static string Ics(params string[] ls) => string.Join(Environment.NewLine, ls);

                // ★★ 全天日程的 DTEND 是【不含】那一天 —— 不减一天每条都会多出一天
                var (evs, _) = Services.ICalParser.ParseEvents(Ics(
                    "BEGIN:VEVENT", "UID:ad-1", "SUMMARY:出差",
                    "DTSTART;VALUE=DATE:20260731", "DTEND;VALUE=DATE:20260802", "END:VEVENT"), "我", "家庭");
                Assert(evs.Count == 1 && evs[0].AllDay, "全天日程能解析");
                Assert(evs[0].Start.Date == new DateTime(2026, 7, 31) && evs[0].End.Date == new DateTime(2026, 8, 1),
                       "★★ DTEND 不含末日 -> 减一天(7/31~8/2 实为 7/31 与 8/1 两天)");
                Assert(evs[0].DayCount == 2, "天数算对(两天,不是三天)");

                // ★ 折行:续行以空白开头,不合并会把长标题截断
                var (fe, _) = Services.ICalParser.ParseEvents(Ics(
                    "BEGIN:VEVENT", "UID:f-1", "SUMMARY:这是一个很长的", " 标题被折了行",
                    "DTSTART:20260731T090000Z", "DTEND:20260731T100000Z", "END:VEVENT"), "我", "家庭");
                Assert(fe.Count == 1 && fe[0].Title == "这是一个很长的标题被折了行",
                       "★ 折行先合并再解析(否则长标题从中间断掉)");

                // ★ 转义还原
                Assert(Services.ICalParser.Unescape(@"a\,b") == "a,b", "转义逗号还原");
                Assert(Services.ICalParser.Unescape(@"a\nb") == "a" + (char)10 + "b", "★ 转义 n 还原成真换行(否则备注里显示字面的 反斜杠n)");
                Assert(Services.ICalParser.Unescape(@"a\;b") == "a;b", "转义分号还原");
                Assert(Services.ICalParser.Unescape(@"a\\b") == @"a\b", "转义反斜杠还原");

                // UTC 带 Z -> 转本地时间;并回填 Source/UID
                var (uz, _) = Services.ICalParser.ParseEvents(Ics(
                    "BEGIN:VEVENT", "UID:z-1", "SUMMARY:会", "DTSTART:20260731T090000Z", "END:VEVENT"), "我", "家庭");
                Assert(uz.Count == 1 && uz[0].Start == new DateTime(2026, 7, 31, 9, 0, 0, DateTimeKind.Utc).ToLocalTime(),
                       "带 Z 的时间按 UTC 读并转本地");
                Assert(uz[0].Source == "apple" && uz[0].ExternalId == "z-1",
                       "★ 回填 Source=apple 与 Apple UID(合并去重的首选判据)");

                // 坏条目跳过而不抛
                var (icsOk, icsSkip) = Services.ICalParser.ParseEvents(Ics(
                    "BEGIN:VEVENT", "UID:bad", "DTSTART:20260731T090000Z", "END:VEVENT",
                    "BEGIN:VEVENT", "UID:good", "SUMMARY:好的", "DTSTART:20260731T090000Z", "END:VEVENT"), "我", "家庭");
                Assert(icsOk.Count == 1 && icsSkip == 1,
                       "★ 无标题的跳过并计数,不抛异常(一条坏的不能坑掉整次同步)");

                // ★ 拉回来的能直接进合并层:同 UID 不重复加
                var box = new List<Views.CalendarEvent>();
                Assert(Views.CalendarData.MergeInto(box, uz) == 1, "首次合并加入 1 条");
                Assert(Views.CalendarData.MergeInto(box, uz) == 0, "★ 再拉一次不重复加(同 Apple UID)");
            }

                        // ---- CalDAV 响应解析(不需要真账号就能验)----
            {
                static string X(params string[] ls) => string.Join(Environment.NewLine, ls);

                // ★ 命名空间不敏感:iCloud 用 d: 前缀,别的服务端可能用 D: 或默认命名空间
                var principal = Services.AppleCalDav.FirstHref(X(
                    "<?xml version=" + (char)34 + "1.0" + (char)34 + "?>",
                    "<d:multistatus xmlns:d=" + (char)34 + "DAV:" + (char)34 + ">",
                    "<d:response><d:propstat><d:prop>",
                    "<d:current-user-principal><d:href>/123456/principal/</d:href></d:current-user-principal>",
                    "</d:prop></d:propstat></d:response></d:multistatus>"), "current-user-principal");
                Assert(principal == "/123456/principal/", "能从响应里取出账号主体");

                // ★★ iCloud 会把后续请求指到【带编号的主机】—— 一直打入口会拿不到数据
                Assert(Services.AppleCalDav.Absolute("https://caldav.icloud.com", "/123456/calendars/")
                       == "https://caldav.icloud.com/123456/calendars/", "相对路径拼成绝对 URL");
                Assert(Services.AppleCalDav.Absolute("https://caldav.icloud.com", "https://p52-caldav.icloud.com/123/")
                       == "https://p52-caldav.icloud.com/123/",
                       "★★ 服务端给的绝对 URL(带编号主机)原样跟随,不被入口域覆盖");

                // 列出日历集合:只要 VEVENT 的,提醒事项(VTODO)这一版不接
                var cols = Services.AppleCalDav.ParseCollections(X(
                    "<d:multistatus xmlns:d=" + (char)34 + "DAV:" + (char)34 + " xmlns:c=" + (char)34 + "urn:ietf:params:xml:ns:caldav" + (char)34 + ">",
                    "<d:response><d:href>/123/home/work/</d:href><d:propstat><d:prop>",
                    "<d:resourcetype><d:collection/><c:calendar/></d:resourcetype>",
                    "<d:displayname>工作</d:displayname>",
                    "<c:supported-calendar-component-set><c:comp name=" + (char)34 + "VEVENT" + (char)34 + "/></c:supported-calendar-component-set>",
                    "</d:prop></d:propstat></d:response>",
                    "<d:response><d:href>/123/home/reminders/</d:href><d:propstat><d:prop>",
                    "<d:resourcetype><d:collection/><c:calendar/></d:resourcetype>",
                    "<d:displayname>提醒</d:displayname>",
                    "<c:supported-calendar-component-set><c:comp name=" + (char)34 + "VTODO" + (char)34 + "/></c:supported-calendar-component-set>",
                    "</d:prop></d:propstat></d:response>",
                    "</d:multistatus>"));
                Assert(cols.Count == 2, "两个集合都解析出来");
                var vevent = cols.Where(x => x.comps.Contains("VEVENT")).ToList();
                var vtodo = cols.Where(x => x.comps.Contains("VTODO")).ToList();
                Assert(vevent.Count == 1 && vevent[0].name == "工作", "认出 VEVENT 日历及其名字");
                Assert(vtodo.Count == 1 && vtodo[0].name == "提醒",
                       "★ 提醒事项集合能识别出来 —— 这一版按用户裁定不接,但得能认出来好排掉");

                // REPORT 响应里取 ics 正文,并贯通到解析器
                var datas = Services.AppleCalDav.ParseCalendarData(X(
                    "<d:multistatus xmlns:d=" + (char)34 + "DAV:" + (char)34 + " xmlns:c=" + (char)34 + "urn:ietf:params:xml:ns:caldav" + (char)34 + ">",
                    "<d:response><d:propstat><d:prop><c:calendar-data>BEGIN:VEVENT",
                    "UID:e-1", "SUMMARY:晚会", "DTSTART:20260731T090000Z",
                    "END:VEVENT</c:calendar-data></d:prop></d:propstat></d:response></d:multistatus>"));
                Assert(datas.Count == 1, "取出一份 calendar-data");
                var (pe, _) = Services.ICalParser.ParseEvents(datas[0], "我", "家庭");
                Assert(pe.Count == 1 && pe[0].Title == "晚会" && pe[0].ExternalId == "e-1",
                       "★ CalDAV 响应 -> ics -> CalendarEvent 整条链路贯通");
            }

            // ---- CalDAV 接入的几条要害(研究查证后钉死,2026-07-31)----
            {
                var cd = TryReadSource(Path.Combine("Services", "AppleCalDav.cs"));
                if (cd is not null)
                {
                    Assert(cd.Contains("AllowAutoRedirect = false"),
                           "★★ 必须关自动重定向:HttpClient 跨主机时会丢 Authorization,而 iCloud 认证后正要转分区主机 -> 401 死循环");
                    Assert(cd.Contains("SendFollowingAsync") && cd.Contains("IsIcloudHost"),
                           "★ 自己逐跳跟重定向并重新挂凭据;且只往 icloud.com 下的主机送");
                    Assert(cd.Contains("HttpStatusCode.Forbidden") && cd.Contains("可能导致 Apple ID 被锁"),
                           "★★ 401/403 分开:403 = 已被节流,必须停下来(继续重试会锁用户真实的 Apple ID)");
                    Assert(cd.Contains("UseProxy = false"),
                           "★ 关环境代理 —— 一个环境变量不能把请求改道到别处");
                    Assert(!cd.Contains("new HttpMethod(\"PUT\")") && !cd.Contains("new HttpMethod(\"DELETE\")"),
                           "★★ 只读:没有 PUT/DELETE —— 写回 Apple 不可逆,这一版不开");
                    // 自动重试会把用户账号打进锁定 —— 整条链路不得有
                    var syncSrc = TryReadSource(Path.Combine("Services", "AppleCalendarSync.cs"));
                    Assert(syncSrc is not null && !syncSrc.Contains("for (var retry") && !syncSrc.Contains("while (retry"),
                           "★★ 认证路径上没有自动重试(反复失败会锁掉真实 Apple ID)");
                }
            }

            // ---- 自动拉取的【熔断】(安全要求,不是优化)----
            {
                var au = TryReadSource(Path.Combine("Services", "AppleAutoSync.cs"));
                if (au is not null)
                {
                    Assert(au.Contains("if (r.AuthFailed)") && au.Contains("s.AppleAutoPull = false;"),
                           "★★ 自动拉取遇认证失败【立即自动关掉】—— 定时反复撞 401 会锁掉用户真实的 Apple ID");
                    Assert(au.Contains("TrippedReason"),
                           "★ 熔断要记下原因并告诉用户(否则只会觉得开关自己关了)");
                    Assert(!au.Contains("退避重试") || au.Contains("绝不退避重试"),
                           "★ 认证失败后不做退避重试(那是在赌用户的账号)");
                    Assert(au.Contains("Math.Max(15,"),
                           "★ 自动拉取间隔下限9分钟以上(日历不是秒级数据,拉太勤只会更容易撞节流)".Replace("9分钟", "15 分钟"));
                }
                var sv3 = TryReadSource(Path.Combine("Views", "SettingsView.cs"));
                if (sv3 is not null)
                {
                    Assert(sv3.Contains("Content = \"自动拉取\""),
                           "设置里有【自动拉取】开关(用户要求)");
                    Assert(sv3.Contains("AppleAutoSync.TrippedReason is { } trip"),
                           "★ 被熔断时界面把原因显示出来");
                    Assert(sv3.Contains("AppleAutoSync.ResetTrip()"),
                           "★ 重新填过密码后清熔断,允许再自动跑");
                    Assert(sv3.Contains("cb.Checked += (_, _) =>") && sv3.Contains("AppleCalendarUrls.Add(url)"),
                           "★ 可以逐个勾选要拉取哪些日历(用户要求)");
                }
            }

            // ---- 这一批(用户 2026-07-31)----
            {
                // ---- 待办是【纯本机】的:不接任何外部源(2026-08-02 用户裁定,D57)----
                // ★★ 这一组断言是【负向】的:它们守着"别把同步接口偷偷加回来"。
                //   原先这里测的是"从 Apple 提醒事项导入并去重"以及 Apple 那两条
                //   「提醒事项已升级」公告的识别 —— 整条路已经删掉,连同它的测试。
                Assert(typeof(TodoItem).GetProperty("Source") is null
                    && typeof(TodoItem).GetProperty("ExternalId") is null,
                       "★ 待办没有【来源】与【外部 UID】字段 —— 留着就是在暗示一个不会兑现的同步承诺");
                Assert(typeof(TodoCenter).GetMethod("MergeIn") is null
                    && typeof(TodoCenter).GetMethod("MergeInto") is null,
                       "★ 待办没有增量合并层 —— 没有数据源的合并层等于摆着「只差接上最后一根线」的假象");
                Assert(typeof(Services.ICalParser).GetMethod("ParseTodos") is null,
                       "★ 不再解析 VTODO(提醒事项)");
                Assert(Type.GetType("LocalAI.Client.Services.AppleReminderNotice, localai-client") is null,
                       "★ Apple「提醒事项已升级」的识别整体删除 —— 我们从不去拉它,也就没有公告要挡");
                Assert(typeof(Services.AppSettings).GetProperty("AppleReminderList") is null,
                       "★ 设置里不再存提醒事项清单 —— 列出来就是在暗示能同步");
                {
                    var cdNoTodo = TryReadSource(Path.Combine("Services", "AppleCalDav.cs"));
                    if (cdNoTodo is not null)
                    {
                        Assert(typeof(Services.AppleCalDav).GetMethod("FetchTodosAsync") is null,
                               "CalDAV 侧不再拉 VTODO");
                        // ★ 但【识别】VTODO 集合那一段必须留着 —— 它现在的用处是把提醒事项清单
                        //   排除在日历勾选列表之外,否则用户会在日历里看到"购物清单"这种勾了也没用的条目。
                        Assert(cdNoTodo.Contains("comps.Contains(\"VTODO\")"),
                               "★ 仍然认得出提醒事项集合 —— 好把它们【排除】在日历清单之外");
                    }
                    var tcSrc = TryReadSource(Path.Combine("Services", "TodoCenter.cs"));
                    if (tcSrc is not null)
                        Assert(tcSrc.Contains("纯本机数据") && tcSrc.Contains("不会自愈"),
                               "★ 数据模型头部写明:待办只在这台电脑上、不会自愈(换机/重装就没了)");
                    // ★★ 而且要【说给用户听】,不能只写在注释里:
                    //   日历丢了能从 iCloud 再拉一次,待办不能 —— 不说清楚,换电脑那天才发现就太晚了。
                    var taSrc = TryReadSource(Path.Combine("Views", "TodoArchiveView.cs"));
                    if (taSrc is not null)
                        Assert(taSrc.Contains("只存在这台电脑上") && taSrc.Contains("不会自动带走"),
                               "★ 界面上如实告诉用户:待办不同步、换机不会自动带走");
                }

                // 自动拉取:三道闸
                var au2 = TryReadSource(Path.Combine("Services", "AppleAutoSync.cs"));
                if (au2 is not null)
                {
                    Assert(au2.Contains("NetworkInterface.GetIsNetworkAvailable()") && au2.Contains("没有网络"),
                           "★★ 没网就【连试都不试】—— 断网时发注定失败的请求只是空转");
                    Assert(au2.Contains("SuspendedReason") && au2.Contains("SuspendAfter"),
                           "★ 连续连不上 -> 软暂停,不再按固定节奏空转");
                    Assert(au2.Contains("NetworkChange.NetworkAvailabilityChanged"),
                           "★ 网络恢复时【自然继续】,而不是靠定时重试去发现");
                }
                // 默认间隔 30 分钟
                Assert(new Services.AppSettings().AppleAutoPullMinutes == 30, "自动拉取默认间隔 30 分钟(用户裁定)");
                // 清单落盘
                Assert(new Services.AppSettings().AppleCalendarList is not null,
                       "★ 日历清单落盘保存 —— 连上后一直在,不必每次先点刷新");

                // ---- 提醒事项的【勾选入口整体移除】(2026-08-02 用户裁定,D56)----
                //   Apple 2019 起把 iCloud 提醒事项升级进 CloudKit,CalDAV 上永远拉不到;
                //   留一排勾了也没用的清单,在售卖产品里就是一个坏掉的开关。
                {
                    var svApple = TryReadSource(Path.Combine("Views", "SettingsView.cs"));
                    if (svApple is not null)
                    {
                        Assert(!svApple.Contains("勾选要拉取的【提醒事项】"),
                               "★ 设置页不再有【勾选提醒事项】那一组(它对 iCloud 账号永远拉不到东西)");
                        // ★ D57 起这张卡【只管日历】,连提醒事项清单都不取回来显示 ——
                        //   在这儿提它就是在暗示能同步。为什么不能同步,放在【待办自己那儿】说。
                        Assert(!svApple.Contains("AppleReminderList"),
                               "★ Apple 这张卡只管日历,不再碰提醒事项清单");
                    }
                    // ★★ 不许留【看不见的开关】:界面上的勾选框没了,同步却照着旧存档里的 URL 继续拉,
                    //   用户就再也关不掉它。字段与读取一起去掉,旧存档里那个键反序列化时自然被忽略。
                    Assert(typeof(Services.AppSettings).GetProperty("AppleReminderUrls") is null,
                           "★ AppleReminderUrls 已删除 —— 移除界面却留着后台在读,等于一个关不掉的开关");
                    var syncSrc = TryReadSource(Path.Combine("Services", "AppleCalendarSync.cs"));
                    if (syncSrc is not null)
                        Assert(!syncSrc.Contains("FetchTodosAsync"),
                               "★ 同步这一趟不再拉 VTODO(这条路对 iCloud 账号只会返回 0 条)");
                }

                // 月视图点日期 = 查看当天(不再直接开新增抽屉)
                var cv4 = TryReadSource(Path.Combine("Views", "CalendarView.cs"));
                if (cv4 is not null)
                {
                    // ★★ 这一条翻过两次,把经过记下来免得以后又改回去:
                    //   ① 最早:点日期直接开新增抽屉;
                    //   ② 2026-07-31 用户报 bug——"想看那天有什么反而无路可走",改成选中 + 列当天;
                    //   ③ 同日晚些时候用户重新裁定:点日期【直接开新建抽屉】——
                    //     因为主页已经把时间轴合并进来了,"看那天有什么"下面就画着,
                    //     ① 那个 bug 的成因已经不存在了。
                    var oc = Slice(cv4, "void OnDayClicked(DateTime day)", "}");
                    Assert(oc is not null && !oc.Contains("OpenEditor(day, null)"),
                           "★ 点日期 = 【只选中】(用户最终裁定:点击只是选中,新建要有自己的按钮)");
                    Assert(cv4.Contains("public void NewEventOnSelected()"),
                           "★ 新建走板块标题栏那个「+」，在选中的那一天建");
                    Assert(cv4.Contains("public bool HideWeekdayHeader"),
                           "★ 月历可以藏掉「一二三四五六日」那一行(与下方时间轴共用中间那行日期)");
                    // ★★ 浮层是【一摹】而不是一个:在抽屉里点"年月选择"会把整个抽屉关掉 ——
                    //   因为浮窗登记时把上一个(抽屉)关了,而浮窗的锚点就在那个抽屉里。
                    var ov = TryReadSource(Path.Combine("Views", "Overlay.cs"));
                    var fo = TryReadSource(Path.Combine("Views", "Flyout.cs"));
                    if (ov is not null && fo is not null)
                    {
                        Assert(ov.Contains("public static void Push(Action close) => _stack.Add(close);"),
                               "★★ 浮层可以【叠】—— 浮窗从抽屉里弹出来时不关抽屉");
                        Assert(fo.Contains("Overlay.Push(Close);") && !fo.Contains("Overlay.Register(Close)"),
                               "★ 浮窗走 Push 而不是 Register(抽屉之间才是互相替换)");
                        Assert(fo.Contains("public static bool IsInside(DependencyObject? node)"),
                               "★★ 能判出「点在浮窗里」 —— 浮窗活在独立 Popup 视觉树里,"
                               + "外壳顺着主窗口的树找不到它,不特判就会把“点浮窗里”当成“点外面”");
                    }
                    var mw4 = TryReadSource("MainWindow.xaml.cs");
                    if (mw4 is not null)
                    {
                        Assert(mw4.Contains("Flyout.IsInside(fd)"),
                               "★ 外壳放行点在浮窗内部的点击");
                        Assert(mw4.Contains("new CalendarView(CalendarView.Mode.Week) { HideModeSwitch = true }"),
                               "★ 顶栏日历抽屉【只保留周排布】(月排布归主页那个大板块)");
                    }
                    Assert(cv4.Contains("Fill = new SolidColorBrush(Services.CalendarGroups.ColorOf(ev.CalendarGroup))"),
                           "★★ 日期格里的圆点跟随【那条日程自己分类】的颜色");
                    Assert(cv4.Contains("bar.Background = new SolidColorBrush(color);")
                           && cv4.Contains("color[c1 + 1] == color[c0]"),
                           "★★ 全天线也跟随分类颜色,且【颜色变了就断开】"
                           + "(一根线横跨两个分类的话,那根线到底是谁的说不清)");
                    Assert(cv4.Contains("if (isToday) { d.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, \"FgOnAccent\")"),
                           "★ 今天那格底色是着重色 —— 分类色圆点要描一圈反色边才分得开");
                    // 时间转盘:长按拖也能拨
                var wp = TryReadSource(Path.Combine("Views", "WheelPicker.cs"));
                if (wp is not null)
                {
                    Assert(wp.Contains("MouseLeftButtonDown += (_, e) =>") && wp.Contains("SetIndex(_dragIndex0 - (int)Math.Round(dy / RowHeight));"),
                           "★★ 时间转盘【长按左键上下拖】也能拨"
                           + "(长得就像个可以拨的东西,伸手去拨却没反应,本身就是误导)");
                    Assert(wp.Contains("int _dragIndex0;"),
                           "★ 走【绝对口径】:每帧从按下那一刻的起始格 + 总位移重算,不做增量累加");
                    Assert(wp.Contains("if (_dragMoved) e.Handled = true;"),
                           "★ 拖过了就把松手那一下吞掉 —— 否则底下那一行会把刚拖好的值拉回去");
                }
                Assert(!cv4.Contains("void ShowDayFlyout"),
                           "★ 当日预览浮窗已移除 —— 同一份日程下方时间轴已经画着,再列一遍是重复的");
                }
                // 主页刷新按钮:透明命中块 + 图标不吃命中
                var hv4 = TryReadSource(Path.Combine("Views", "HomeView.cs"));
                if (hv4 is not null)
                    Assert(hv4.Contains("SyncNowButton") && hv4.Contains("glyph.IsHitTestVisible = false"),
                           "★ 主页日历右上角刷新:图标不做按钮,外套透明命中块");
            }

            // ---- ★★ 主要视图【构造冒烟】(2026-07-31 加:HomeView 构造期崩溃,而 1096 条断言全过)----
            // 为什么单独留这一条:业务断言检查的是"逻辑对不对",而视图【从来没被真正构造过】——
            //   一个在构造函数里抛的异常(比如"元素已有另一个逻辑父级"),对所有断言都是不可见的,
            //   而它的后果是【整个客户端打不开】。
            // 这与中枢侧 test_imports.py 是同一类护栏:"这东西根本起不来"只能靠冒烟抓。
            {
                var app0 = System.Windows.Application.Current;
                var added = new List<System.Windows.ResourceDictionary>();
                foreach (var src in new[] { "Theme/Breeze.xaml", "Theme/Controls.xaml" })
                {
                    try
                    {
                        var d = new System.Windows.ResourceDictionary
                        { Source = new Uri("pack://application:,,,/" + src, UriKind.Absolute) };
                        app0.Resources.MergedDictionaries.Add(d);
                        added.Add(d);
                    }
                    catch { }
                }
                try
                {
                    // ★ 逐个真的 new 一遍。只断言【不抛】—— 长什么样是 --wheeltest 的事。
                    foreach (var (name, make) in new (string, Func<System.Windows.FrameworkElement>)[]
                             {
                                 ("HomeView",       () => new Views.HomeView()),
                                 ("SettingsView",   () => new Views.SettingsView()),
                                 ("ModelsView",     () => new Views.ModelsView()),
                                 ("ExtensionsView", () => new Views.ExtensionsView()),
                                 ("WeekTimeline",   () => new Views.WeekTimeline()),
                                 ("CalendarView",   () => new Views.CalendarView(Views.CalendarView.Mode.Month)),
                                 ("ChatView",       () => new Views.ChatView("chat")),
                                 // ★ 配对页会在构造期起一个"自动找中枢"的后台任务。放进来是因为它构造期一抛,
                                 //   用户点进设备页就是白屏 —— 而这条路径平时没人走(要等到真去配对第二台才发现)。
                                 ("DevicesView",    () => new Views.DevicesView()),
                             })
                    {
                        try { _ = make(); Assert(true, $"{name} 能构造出来(构造期不抛)"); }
                        catch (Exception ex)
                        {
                            Assert(false, $"★★ {name} 构造期就抛了 —— 这会让整个客户端打不开:"
                                          + ex.GetType().Name + ": " + ex.Message);
                        }
                    }
                }
                finally
                {
                    foreach (var d in added) app0.Resources.MergedDictionaries.Remove(d);
                }
            }

            // ---- 两边版本核验(三层,别混为一谈)----
            {
                // 开发树里跑自检时没有烧过版本戳 —— 它就该如实说"开发构建",不编一个号
                Assert(Services.BuildInfo.Display.Length > 0, "版本显示永远有话说");
                Assert(Services.BuildInfo.Stamp is null || Services.BuildInfo.Stamp.Length >= 8,
                       "★ 版本戳要么没有(开发树),要么是真的那一串 —— 不允许编一个出来");
                var biSrc = TryReadSource(Path.Combine("Services", "BuildInfo.cs"));
                if (biSrc is not null)
                    Assert(biSrc.Contains("开发构建") && biSrc.Contains("不编一个版本号"),
                           "★★ 拿不到版本戳时如实说 —— 装出来的版本号会让「两边版本对不对得上」这件事失去意义");
                var ctSrc = TryReadSource(Path.Combine("..", "transport", "ClientTransport.cs"));
                if (ctSrc is not null)
                {
                    Assert(ctSrc.Contains("clientVersion = ClientVersion"),
                           "★ 配对时上报本机版本戳(服务端忽略未知字段,不影响老主机)");
                    Assert(ctSrc.Contains("自报的、未被六词覆盖的】信息"),
                           "★★ 写明 clientVersion 是自报信息、不在六词覆盖范围内 —— 只作显示,不做判断");
                    // ★★ 这条钉的是一个【已经写错过一次的断言】:此处曾写"协议版本不同则六词对不上"。
                    //   事实是 Pairing.Enroll 拿请求体里的 protocolVersion 去推 SAS、自己不校验,
                    //   两边用同一个自报值 ⇒ 永远一致。错误的安全声称比没有声称更坏。
                    Assert(ctSrc.Contains("两边用的是同一个值"),
                           "★★ 写明 protocolVersion 也是自报的、没被六词拦住 —— 不允许在代码里留错的安全声称");
                }
                var dv3 = TryReadSource(Path.Combine("Views", "DevicesView.cs"));
                if (dv3 is not null)
                {
                    Assert(dv3.Contains("它们拦的是中间人,不是版本"),
                           "★★ 界面要说实话:六个词拦的是中间人,拦不住版本不一致");
                    Assert(!Body(dv3).Contains("配对的六个词会直接对不上"),
                           "★★ 那句错的安全声称不能再回到界面上");
                }
            }

            // ---- 局域网自动找中枢(用户:「两边都开着就该一键连上」)----
            {
                // ★ 证书名 -> hub_id 的识别是纯函数,直接逐个验
                Assert(Services.HubDiscovery.HubIdFromServerName("localai-f6hsduipeesexb6f.local") == "f6hsduipeesexb6f",
                       "★ 从证书名里取出 hub_id(命名来自 identity:localai-<hub>.local)");
                Assert(Services.HubDiscovery.HubIdFromServerName("localai-ABC.LOCAL") == "ABC", "大小写不敏感");
                foreach (var bad in new[] { "", "example.com", "localai-.local", "notlocalai-x.local", "localai-x.lan" })
                    Assert(Services.HubDiscovery.HubIdFromServerName(bad) is null,
                           $"★ 认不出这个形状就返回 null(实得 {bad})—— 8443 上蹲着别的东西不能当成我们的中枢");
                Assert(Services.HubDiscovery.HubIdFromServerName(null) is null, "null 不抛异常");
                Assert(Services.HubDiscovery.EdgePort == 8443, "业务口端口与 lan-edge run-lan 一致");

                var hdSrc = TryReadSource(Path.Combine("Services", "HubDiscovery.cs"));
                if (hdSrc is not null)
                {
                    Assert(hdSrc.Contains("发现不建立信任"),
                           "★★ 文件头必须写明【发现不建立信任】—— 这里故意不校验证书,"
                           + "不写清楚就会被后人读成「我们接受任意证书」");
                    Assert(hdSrc.Contains("tooWide.Add($\"{ip}/{mask}\")") && hdSrc.Contains("tiny.Add($\"{ip}/{mask}\")"),
                           "★★ 跳过了要说,而且要【分清是哪一种】—— 掩码太宽 / /31/32 / 根本没有 IPv4,"
                           + "三种的下一步完全不同,混成一句就会把人支错方向");
                    Assert(hdSrc.Contains("NoUsableV4"),
                           "★★ 「本机根本没有可用 IPv4」要单独报 —— 那时出路是去接网线,不是手填(手填也连不上)");

                    // ---- 扫描范围:逐个地址核,不满足于搜字符串 ----
                    var h24 = Services.HubDiscovery.HostsOf("192.168.178.61", 24).ToList();
                    Assert(h24.Count == 254 && h24[0] == "192.168.178.1" && h24[^1] == "192.168.178.254",
                           "★ /24 = 去掉网络号与广播地址的 254 个");
                    // ★★ 这条是本次修正的正题:掩码比 /24 窄时,以前扫的是【包住它的那个 /24】,
                    //   大半地址经默认网关发去隔壁子网 —— 那才是真正像端口扫描的形状。
                    var h25 = Services.HubDiscovery.HostsOf("192.168.178.61", 25).ToList();
                    Assert(h25.Count == 126 && h25[0] == "192.168.178.1" && h25[^1] == "192.168.178.126",
                           "★★ /25 只扫本子网的 126 个 —— 不许扫到隔壁半个 /24 去");
                    var h25b = Services.HubDiscovery.HostsOf("192.168.178.200", 25).ToList();
                    Assert(h25b.Count == 126 && h25b[0] == "192.168.178.129" && h25b[^1] == "192.168.178.254",
                           "★★ 上半段的 /25 要扫上半段 —— 说明用的是真实掩码,不是拿前三段拼 /24");
                    Assert(Services.HubDiscovery.HostsOf("10.1.2.3", 30).Count() == 2, "/30 上只有两台");
                    Assert(!Services.HubDiscovery.HostsOf("10.1.2.3", 31).Any()
                           && !Services.HubDiscovery.HostsOf("10.1.2.3", 32).Any(),
                           "★ /31、/32 上没有别的主机 —— 空表,不抛");
                    Assert(Services.HubDiscovery.Network("192.168.178.61", 22) == "192.168.176.0",
                           "★ 网段号按真实掩码算 —— 界面要把「扫了哪个网段」如实说出来");

                    // ---- ★★ 真的握手一次:ProbeOneAsync 能不能读出证书名 ----
                    // 这条是 2026-08-04 审计抓出来的教训:原来只有一句
                    // `haSrc2.Contains("HubDiscovery.ProbeOneAsync")` 的搜字符串 —— 全绿,而功能**全废**:
                    // SslStream 的证书回调被设了两遍(构造函数一个、AuthenticateAsClientAsync 的 options 里又一个),
                    // .NET 在握手【开始之前】就抛 InvalidOperationException,被空 catch 吞掉,cert 恒为 null。
                    // 于是局域网发现、「在局域网里找回它」、主机自配对探业务口 —— 三条路径统统恒失败。
                    // ⇒ 搜字符串看不见语义错误。这里起一个**真的** TLS 监听让它去握手。
                    {
                        var hubShort = "abcdefgh23456789";           // 16 位小写 base32,合法形状
                        var dns = "localai-" + hubShort + ".local";
                        using var ecdsa = System.Security.Cryptography.ECDsa.Create(
                            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
                        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                            "CN=" + dns, ecdsa, System.Security.Cryptography.HashAlgorithmName.SHA256);
                        var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
                        san.AddDnsName(dns);
                        req.CertificateExtensions.Add(san.Build());
                        using var selfSigned = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),
                                                                    DateTimeOffset.UtcNow.AddMinutes(30));
                        // ★ SChannel 要能拿到私钥 —— 临时密钥要过一遍 PFX 才行
                        var pfx = selfSigned.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "x");
                        using var serverCert = System.Security.Cryptography.X509Certificates
                            .X509CertificateLoader.LoadPkcs12(pfx, "x");

                        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                        listener.Start();
                        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                        var serverTask = System.Threading.Tasks.Task.Run(async () =>
                        {
                            try
                            {
                                using var c = await listener.AcceptTcpClientAsync();
                                using var ss = new System.Net.Security.SslStream(c.GetStream(), false);
                                await ss.AuthenticateAsServerAsync(serverCert, false,
                                    System.Security.Authentication.SslProtocols.Tls12
                                    | System.Security.Authentication.SslProtocols.Tls13, false);
                            }
                            catch { /* 客户端读完名字就走人,这里抛是正常的 */ }
                        });

                        Services.FoundHub? probed = null;
                        try
                        {
                            probed = System.Threading.Tasks.Task.Run(async () =>
                                await Services.HubDiscovery.ProbeOneAsync("127.0.0.1", port, 3000))
                                .GetAwaiter().GetResult();
                        }
                        catch { }
                        try { listener.Stop(); } catch { }

                        Assert(probed is not null,
                               "★★ 对着一台真的 TLS 服务器握手,ProbeOneAsync 必须读到证书 —— "
                               + "恒返回 null 就是整个局域网发现结构性失效(而搜字符串看不出来)");
                        Assert(probed is null || probed.HubId == hubShort,
                               $"★★ 要从证书名里取出 hub_id(期望 {hubShort},实得 {probed?.HubId})");
                        Assert(probed is null || probed.ServerName == dns, "★ 证书名原样带回来");
                        Assert(probed is null || probed.CertSha256.Length == 64, "★ 指纹是 SHA256 的十六进制");
                    }

                    // ---- hub_id 的两种形状 ----
                    // ★★ 这条是审计没抓到、核源码时自己发现的:配对档案存的是 hub_id(UUID),
                    //   而证书名里是 hub_id_short(UUID 前 80 位的小写 base32,16 字符)。
                    //   直接拿 UUID 去比证书名里的短号【恒不相等】——「在局域网里找回它」会永远失败。
                    //   下面这组是**真实数据**做的已知答案:本机 identity/hub.json 里就是这一对。
                    Assert(Services.HubDiscovery.ShortHubId("d1218f2f-210f-4b24-87c5-d92920759f2a") == "f6hsduipeesexb6f",
                           "★★ UUID 要换算成证书名里那个 16 位短号(已知答案取自真实的 hub.json)");
                    Assert(Services.HubDiscovery.ShortHubId("f6hsduipeesexb6f") == "f6hsduipeesexb6f",
                           "★ 已经是短号形状就原样返回");
                    foreach (var bad in new[] { "", "   ", "not-a-guid", "F6HSDUIPEESEXB6F1", "f6hsduipeesexb61" })
                        Assert(Services.HubDiscovery.ShortHubId(bad) is null,
                               $"★ 认不出形状就返回 null,宁可不比也不瞎比(实得 {bad})");
                    Assert(Services.HubDiscovery.ShortHubId(null) is null, "null 不抛异常");
                    Assert(hdSrc.Contains("MaxParallel"), "★ 并发有上限 —— 254 个地址不限并发会把网卡与防火墙日志淹了");
                }
                var hcSrc2 = TryReadSource(Path.Combine("Services", "HubClient.cs"));
                if (hcSrc2 is not null)
                {
                    var rd = Slice(hcSrc2, "public async Task<bool> RediscoverAsync", "public void UnpairLocal");
                    Assert(rd is not null && rd.Contains("mine.Count != 1"),
                           "★★ 找回中枢只接受【恰好一台 hub_id 完全一致】的 —— 零台或多台都不猜");
                }
                var dv2 = TryReadSource(Path.Combine("Views", "DevicesView.cs"));
                if (dv2 is not null)
                    Assert(dv2.Contains("绝不替用户挑") || dv2.Contains("请自己挑一个"),
                           "★ 找到多个中枢时摆出来让人自己挑(合租/邻居/自己两台都是正常情况)");
            }


            // ---- D46 完整性等级护栏:这道护栏【曾经从来没生效过】(2026-08-03 实测抓到)----
            {
                // 旧实现遍历 WindowsIdentity.Groups 找 S-1-16-12288 —— 而完整性 SID 根本不在 TokenGroups 里,
                // 它在 TokenIntegrityLevel。于是提权拉起的客户端一声不吭就跑起来了,
                // 而 Program.cs、文件头、D46 都写着"会拒绝"。
                // ★★ 旧断言只搜到了"代码里有 IsElevated 这个调用",没有任何一条去核【它算得对不对】——
                //   结构性断言看不见语义错误。所以这里拿一条**独立的外部证据**来核。
                var rid = Services.Elevation.IntegrityRid();
                Assert(rid is not null, "★★ 读得到本进程的完整性等级 —— 读不到就等于这道护栏形同虚设");
                Assert(rid is null || rid == Services.Elevation.RidMedium || rid == Services.Elevation.RidHigh
                       || rid == 0x1000 || rid == 0x4000,
                       $"★ 完整性 RID 是已知取值之一(实得 0x{rid:X})");

                // ★★ 独立佐证:whoami /groups 会把完整性 SID 原样打出来,与我们的 P/Invoke 完全不同路。
                //   两条路对不上就说明我们算错了 —— 这正是旧版漏掉的那一类断言。
                string groups = "";
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "whoami", Arguments = "/groups",
                        UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                    };
                    using var wp = System.Diagnostics.Process.Start(psi);
                    if (wp is not null) { groups = wp.StandardOutput.ReadToEnd(); wp.WaitForExit(); }
                }
                catch { /* 拿不到外部证据就跳过这一条 —— 但绝不因此就说"通过" */ }
                if (groups.Length > 0)
                {
                    var oracleHigh = groups.Contains("S-1-16-12288", StringComparison.Ordinal)
                                     || groups.Contains("S-1-16-16384", StringComparison.Ordinal);
                    Assert(Services.Elevation.IsElevated() == oracleHigh,
                           $"★★ IsElevated() 必须与 whoami /groups 报的完整性等级一致 "
                           + $"(我们说 {Services.Elevation.IsElevated()},whoami 说 High={oracleHigh})—— "
                           + "对不上就是 D46 护栏在装样子");
                    Assert(rid is null || (rid >= Services.Elevation.RidHigh) == oracleHigh,
                           "★ RID 与外部证据同向");
                }
                Assert(Services.Elevation.LastProbeNote.Length > 0 && Services.Elevation.LastProbeNote != "(还没判过)",
                       "★ 判过之后要留下依据 —— 「没判出来所以放行」和「确认是普通用户」不能看起来一样");
                // ★★ 降权重开【只许试一次】。实机上炸过:重开出来的进程仍然是 High,
                //   于是它又去重开自己 —— 无限重开,窗口不停地闪。
                //   根因是"以为重开就一定降权"这个假设从没被验证过,而失败不留痕迹。
                var pgSrc = TryReadSource("Program.cs");
                if (pgSrc is not null)
                {
                    var pg = Body(pgSrc);
                    Assert(pg.Contains("--relaunched-as-user"),
                           "★★ 降权重开要带一次性标记 —— 没有它,重开失败就是无限重开");
                    var iFlag = pg.IndexOf("alreadyTried", StringComparison.Ordinal);
                    var iCall = pg.IndexOf("TryRelaunchAtMediumIntegrity", StringComparison.Ordinal);
                    Assert(iFlag >= 0 && iCall >= 0 && iFlag < iCall,
                           "★★ 先查标记再决定要不要重开 —— 顺序反了等于没有这道闸");
                    Assert(pg.Contains("!alreadyTried &&"),
                           "★ 带着标记就【不再重开】,直接回落到如实拒绝");
                }

                var elSrc = TryReadSource(Path.Combine("Services", "Elevation.cs"));
                if (elSrc is not null)
                    Assert(!Body(elSrc).Contains("id.Groups"),
                           "★★ 不许再回到遍历 TokenGroups 找完整性 SID 那条路 —— 它在那里根本不存在");
            }

            // ---- 一键配对:按角色分流 + 配对窗口的三道闸 ----
            {
                var dv4 = TryReadSource(Path.Combine("Views", "DevicesView.cs"));
                if (dv4 is not null)
                {
                    var body4 = Body(dv4);

                    // ① 角色四分,一种都不许合并
                    Assert(Enum.GetValues<Views.HostRole>().Length == 4,
                           "★★ 角色必须是四种:Unknown / Host / HostHubDown / Client —— "
                           + "把 HostHubDown 并进 Client,就又回到「主机上说这台不是主机」那个坑");
                    var probe = Slice(dv4, "async Task ProbeRoleAsync", "/// <summary>手动重探");
                    Assert(probe is not null && probe.IndexOf("LastProbe == Services.AdminProbeResult.Ok", StringComparison.Ordinal)
                           < probe.IndexOf("HostToolsDir() is not null", StringComparison.Ordinal),
                           "★★ 先看【肯定证据】(管理面答话)再看【线索】(本机有主机端程序)—— 顺序反了线索就会盖过证据");
                    Assert(body4.Contains("case HostRole.Unknown:") && body4.Contains("ProbingCard()"),
                           "★ 角色没探出来之前什么都不猜,如实说「正在确认」");

                    // ② 主机那一支不许出现"填一个中枢地址配到别人家"的框
                    var build4 = Slice(dv4, "void Build()", "UIElement ProbingCard()");
                    Assert(build4 is not null && !build4.Contains("ClientPairCard()")
                           || build4 is not null && build4.IndexOf("case HostRole.Host:", StringComparison.Ordinal)
                              < build4.IndexOf("ClientPairCard()", StringComparison.Ordinal),
                           "★ 主机分支只画主机的卡");

                    // ③ 本机自配对:必须【当场重探】,不能拿旧结论当通行证;而且无论成败都关窗
                    var self = Slice(dv4, "async Task SelfPairAsync", "/// <summary>「已配对的电脑」");
                    Assert(self is not null && self.Contains("await admin.ProbeAsync"),
                           "★★ 自配对前当场重探一次 —— 几分钟前的探测结果不是通行证(中枢可能换了、Edge 可能重起过)");
                    Assert(self is not null && self.Contains("finally") && self.Contains("WindowAsync(false)"),
                           "★★ 无论成败都关窗 —— 开着的窗口是暴露面,不能靠「正常路径会关」来保证");
                    Assert(self is not null && self.Contains("WindowAsync(true, 1)"),
                           "★ 自配对把窗口开到最短(1 分钟)—— 这几秒局域网上的 8443 也接受请求");
                    Assert(self is not null && self.Contains("dials.Count > 1"),
                           "★ 本机有多个地址在应答时不替他挑 —— 选错会把只有本机看得见的地址写进配对档案");

                    // ④ 配对窗口的三道闸,各自独立(用户问:「只有主机没副机岂不是永远关不了」)
                    // ★★ 这条断言【退役】:宽限本身没了。它存在的前提是"客户端替中枢记账",
                    //   而审计指出那正是 bug 的根 —— 两份账一定对不上(批准后 Build() 重建、
                    //   窗口被中枢到点关掉,界面都会和真实状态说反)。现在只有一份账,在中枢那边。
                    //   ⇒ 反过来钉:不许再出现替中枢记账的本地状态。
                    Assert(!body4.Contains("_addExpanded") && !body4.Contains("_graceUntil"),
                           "★★ 不许用本地布尔替中枢记「配对窗口开没开」—— 中枢在 /admin/ping 与 pending 里"
                           + "自报 pairingWindowOpen,两份账一定会对不上,而且真的对不上过");
                    Assert(body4.Contains("TheApp.HubAdmin.PairingWindowOpen"),
                           "★★ 渲染要读【中枢自报的】那一位");
                    var render = Slice(dv4, "void RenderAddSection", "async Task PollPendingAsync");
                    Assert(render is not null && render.Contains("PairingWindowOpen"),
                           "★ 开关的文字与面板的显隐都由中枢那一位决定");
                    var pollSlice = Slice(dv4, "async Task PollPendingAsync", "/// <summary>");
                    Assert(pollSlice is not null && pollSlice.Contains("RenderAddSection()"),
                           "★★ 每轮轮询都重画一次 —— 窗口被中枢到点关掉时界面要立刻跟上,"
                           + "不能出现「界面写着已打开、其实早关了」");
                    Assert(Views.DevicesView.WindowMinutes > 0 && Views.DevicesView.WindowMinutes <= 30,
                           "★ 中枢侧的分钟上限是最后一道闸:客户端崩了窗口也会自己关");
                    Assert(body4.Contains("Unloaded +=") && body4.Contains("CloseWindowAsync(quiet: true)"),
                           "★★ 离开这一页也要关窗 —— 「展开着就走人」不能把窗口留到分钟上限");
                    Assert(body4.Contains("IsVisibleChanged +="),
                           "★ 页面不可见时同样关窗");
                    Assert(body4.Contains("_addPanel = new StackPanel { Visibility = Visibility.Collapsed }"),
                           "★★ 「添加一台新电脑」默认收起 —— 只有主机没有副机的人才不会无意中开窗;"
                           + "展开这个动作本身就是明确意图");

                    // ⑤ 六词比对【不许】被一键化掉
                    var approveDlg = Slice(dv4, "async Task ShowApprovalDialogAsync", "// ============");
                    Assert(approveDlg is not null && approveDlg.Contains("六个词逐字一样"),
                           "★★ 批准按钮的文字本身就是那句断言,不是中性的「确定」—— "
                           + "六个词管的是「这条请求是不是你发的」,displayName 是自报的可以随便写");
                    Assert(approveDlg is not null && approveDlg.Contains("DenyAsync"),
                           "★ 弹窗要有拒绝这条路,不能只有批准和关掉");
                    Assert(approveDlg is not null && !approveDlg.Contains("跳过"),
                           "★★ 不提供任何「跳过比对」的快捷方式");

                    // ⑥ 「启动中枢」按钮:客户端拉得动是因为它自己就是普通用户(D46),不是绕过了护栏
                    var se = Slice(dv4, "async Task StartEdgeAsync", "// ====");
                    var iSpawn = se?.IndexOf("Process.Start", StringComparison.Ordinal) ?? -1;
                    // ★★ 这里原来断言「拉起 Edge 之前先查本进程有没有提权」。**已退役,而且是反过来钉的**:
                    //   同日实测推翻了那个判据 —— 本机 EnableLUA=0(UAC 关闭),桌面 explorer 本身就是 High,
                    //   身份也是在 High 下铸的、在 High 下 CngKey.Open 得开。
                    //   拿"是不是管理员"当门槛,会在这种机器上把一个本来能起来的中枢永远挡住,理由还是假的。
                    //   ⇒ 现在要求的正相反:【不许】预判,直接试着起,让中枢自己说话。
                    Assert(se is not null && !se.Contains("Elevation.IsElevated()"),
                           "★★ 不许拿「我是不是管理员」预判能不能起中枢 —— "
                           + "UAC 关闭的机器上那恒为真,会把健康的机器永远挡住");
                    // ★★ 这条断言【翻面】了:用户要求不要黑窗口。可以藏 ——
                    //   但前提是先给失败找到别的去处,否则就是把错误藏起来。
                    //   ⇒ 现在要求:无窗口启动 + 收日志 + 失败时把日志原文摆到界面上。
                    Assert(se is not null && se.Contains("CreateNoWindow = true"),
                           "★ 不给用户看黑窗口");
                    Assert(se is not null && se.Contains("RedirectStandardOutput = true")
                           && se.Contains("RedirectStandardError = true"),
                           "★★ 藏窗口就必须收日志 —— 那个窗口原来的真正作用是「唯一能看到失败原因的地方」");
                    Assert(se is not null && se.Contains("RedirectStandardInput = true"),
                           "★★ 还要让中枢看到「没有可用 stdin」,它才会走无命令台那条路 —— "
                           + "否则它打完 banner 就当场退出(实测撞到过)");
                    Assert(se is not null && se.Contains("logPath") && se.Contains("中枢自己打印的最后几行"),
                           "★★ 失败时把中枢自己吐的话【原文】摆出来,并给一个打开完整日志的入口 —— "
                           + "窗口可以藏,现场不能丢");
                    var iWait = se?.IndexOf("admin.ProbeAsync", StringComparison.Ordinal) ?? -1;
                    Assert(iSpawn >= 0 && iWait >= 0 && iSpawn < iWait
                           && se!.Contains("LastProbe == Services.AdminProbeResult.Ok"),
                           "★★ 拉起 ≠ 起来了:只有回环管理面真的答话才算数,"
                           + "不许因为 Process.Start 没抛异常就宣布成功");
                    Assert(se is not null && se.Contains("秒内没等到中枢应答"),
                           "★ 到点没等到就如实说 —— 不无限转圈");
                    // ★ 「重新检测」这个按钮【只能】做它名字说的那件事
                    var recheck = Slice(dv4, "UIElement RecheckRow()", "UIElement HubDownCard()");
                    Assert(recheck is not null && !recheck.Contains("Process.Start") && !recheck.Contains("StartEdgeAsync"),
                           "★★ 「重新检测这台的角色」不许顺手启动 Edge —— 按钮必须只做它名字说的那件事");

                    // ⑦ 「一次装好这台主机」:三条不可让步的边界
                    var hs = TryReadSource(Path.Combine("Services", "HostSetup.cs"));
                    if (hs is not null)
                    {
                        var hsBody = Body(hs);
                        // ★★ 绝不调那个会先 del 掉身份的重置脚本 —— 它会让所有已配对设备失效
                        Assert(!hsBody.Contains("重置并铸身份"),
                               "★★ 客户端绝不调 重置并铸身份.cmd —— 它开头就删掉 identity 目录,"
                               + "那是破坏性的;只调 localai-identity init(它自己 fail-closed,已存在就拒绝覆盖)");
                        Assert(hsBody.Contains("\"init\""),
                               "★ 铸身份走 localai-identity init");
                        // ★★ 只有防火墙那一步提权,而且提的是 powershell 跑那个脚本
                        var runas = hsBody.Split("Verb = \"runas\"").Length - 1;
                        Assert(runas == 1,
                               $"★★ 全文件只允许【一处】提权(实得 {runas} 处)—— identity / Edge / 网关"
                               + "一旦继承 High 完整性,身份就毁了");
                        var fw = Slice(hs, "public static async Task<SetupStep> EnsureFirewallAsync", "/// <summary>查规则在不在");
                        Assert(fw is not null && fw.LastIndexOf("FirewallRuleExistsAsync", StringComparison.Ordinal)
                               > fw.IndexOf("Verb = \"runas\"", StringComparison.Ordinal),
                               "★★ 提权跑完【要回来验规则在不在】—— 只凭「UAC 点过了 / 退出码是 0」就宣布成功,"
                               + "是在替用户假设一件没看过的事");
                        Assert(hsBody.Contains("Win32Exception"),
                               "★ 用户在 UAC 上点「否」是【正常路径】,要如实说没放行会怎样,不是抛个异常了事");
                        // ★★ 同上,这条也退役并反过来钉:要紧的不是"是不是普通用户",
                        //   而是【铸的时候和用的时候是不是同一个等级】—— 那要中枢侧把铸造等级记下来才能比,
                        //   已写进 integrity-guard-asks-wrong-question-2026-08-03.md。
                        Assert(!hsBody.Contains("Elevation.IsElevated()"),
                               "★★ 铸身份不许拿「我是不是管理员」当门槛 —— "
                               + "UAC 关闭的机器上根本没有普通身份的进程,那条门槛会把它彻底堵死");
                        // ★ 这一条查的是**文件头注释**,所以看原文而不是 Body() —— Body() 会把注释剥掉。
                        //   (刚写的时候就用错了 hsBody,当场红了一次。)
                        Assert(hs.Contains("铸的时候和用的时候是不是同一个等级"),
                               "★ 文件头要写清真正要防的是什么,免得后人又照着「普通用户」那句写回去");
                    }
                    var nicPick = Slice(dv4, "void BuildNicPicker", "async Task SetupHostAsync");
                    Assert(nicPick is not null && nicPick.Contains("nics.Count == 1") && nicPick.Contains("请选一张"),
                           "★★ 多张网卡时让人自己选 —— 放行在虚拟机的仅主机网卡上等于没放行,"
                           + "而界面会显示成功,那是最难查的一种失败");

                    // ⑪ 铸身份不是内部步骤 —— 不可回退,必须先问;而且要有"这台其实是副机"的出口
                    var auto = Slice(dv4, "async Task AutoSetupAsync", "/// <summary>身份就绪之后");
                    Assert(auto is not null && auto.Contains("IdentityExistsAsync()"),
                           "★★ 没有身份时【先问再铸】—— 走到这张卡的判据只是「旁边有个 host 目录」,"
                           + "那是线索不是判据(把主机的 dist 整个拷过去就满足它),不问就铸 = 网段里悄悄多一个中枢");
                    Assert(auto is not null && auto.Contains("这台其实是副机"),
                           "★★ 要有出口 —— Build() 在 HostHubDown 下只渲染这张卡,"
                           + "没有出口这台电脑【结构上】再也走不到配对,而界面从头到尾不会提「删掉那个 host 目录」");
                    Assert(auto is not null && auto.Contains("_role = HostRole.Client"),
                           "★ 出口要真的把角色改过去,不是只弹句话");

                    // ㉑ 起中枢之前先看它是不是已经在跑 —— 否则第二个必然撞端口,吐一屏 Kestrel 异常栈
                    Assert(se is not null && se.IndexOf("admin0.ProbeAsync", StringComparison.Ordinal) >= 0
                           && se.IndexOf("admin0.ProbeAsync", StringComparison.Ordinal) < iSpawn,
                           "★★ 拉起中枢前先探一次:已经在跑就别起第二个 —— "
                           + "第二个会撞 address already in use,在黑窗口里吐一整屏异常栈,"
                           + "而人根本读不出「你已经开着一个了」");

                    // ㉔ 配对后半程必须用【证书名】而不是 IP,否则对钉住 CA 的那条连接握不上手
                    var ctName = TryReadSource(Path.Combine("..", "transport", "ClientTransport.cs"));
                    if (ctName is not null)
                    {
                        var body5 = Body(ctName);
                        Assert(body5.Contains("var serverName = \"localai-\" + LocalAI.Identity.HubId.Short"),
                               "★★ enroll 之后要把 edgeUrl 换成证书名 localai-<hubShort>.local —— "
                               + "Trusted() 只换了根信任、没关主机名校验,继续用 https://<ip> 会直接握手失败;"
                               + "表现是设备永远停在 provisioning(图形界面这条配对路径本来从没走完过)");
                        var iSas = body5.IndexOf("await onSas(", StringComparison.Ordinal);
                        var iName = body5.IndexOf("var serverName =", StringComparison.Ordinal);
                        var iTrust = body5.IndexOf("Trusted(dial, caPublic, null)", StringComparison.Ordinal);
                        Assert(iSas >= 0 && iName >= 0 && iTrust >= 0 && iSas < iName && iName < iTrust,
                               "★ 换名字要在【建可信连接之前】");
                    }
                    // ㉒ 用户终版规格(decision-packets/pairing-ux-final-spec-2026-08-04.md)
                    //   主机未连接就【自动连自己】,不给按钮;角色检测只在【还没配好】时出现;
                    //   已配对列表里自己那条【不给移除】。
                    Assert(!body4.Contains("\"完成本机配对\""),
                           "★★ 主机自配对是内部步骤,不该摆成按钮 —— 用户裁定:开客户端就自动连自己");
                    Assert(body4.Contains("_selfPairStarted"),
                           "★ 自动自配对只跑一次 —— 每次 Build() 都启一遍会叠出好几条 enroll");
                    var hostCard2 = Slice(dv4, "UIElement HostSelfCard", "async Task SelfPairAsync");
                    Assert(hostCard2 is not null && hostCard2.Contains("if (!TheApp.Hub.IsPaired)")
                           && hostCard2.Contains("RecheckRow()"),
                           "★★ 角色检测只在还没配好时出现 —— 配好了角色已被一次成功连接证明过,再问只是噪音");
                    var devRow = Slice(dv4, "UIElement DeviceRow", "bool IsThisMachine");
                    Assert(devRow is not null && devRow.Contains("if (!isSelf)"),
                           "★★ 已配对列表里【看得到自己但不能移除自己】—— 自己就是主机,"
                           + "解除自己等于让这台机器把自己踢出去");
                    var isSelfFn = Slice(dv4, "bool IsThisMachine", "void RenderDevices");
                    Assert(isSelfFn is not null && isSelfFn.Contains("CertShort") && isSelfFn.Contains("SHA256"),
                           "★★ 认「是不是自己」要按证书指纹,不按名字 —— 同名设备很常见,而名字还是自报的");
                    // ㉓ 副机侧:只允许「开始寻找主机」+ 网络选择(仅多网)+ 角色检测
                    var cpc = Slice(dv4, "UIElement ClientPairCard", "string? _pickedNic");
                    Assert(cpc is not null && cpc.Contains("开始寻找主机"),
                           "★ 副机未配对时的唯一主按钮");
                    Assert(cpc is not null && cpc.Contains("nics.Count > 1") && cpc.Contains("nics.Count == 1"),
                           "★★ 网络选择【仅多网时出现】;只有一个就自动用它、不显示按钮(用户裁定)");
                    var knock = Slice(dv4, "async Task KnockAsync", "string? _pickedDial");
                    Assert(knock is not null && knock.Contains("还没上线"),
                           "★★ 敲门协议要中枢配合、现在还没有 —— 必须【如实降级】,"
                           + "绝不假装敲门已经发出去了(那会让人在副机这边干等一个不会来的响应)");

                    // ⑯ 批准/拒绝的返回值不许丢 —— 409(过期/已处理)时界面必须说话
                    Assert(body4.Contains("rst == 409") && body4.Contains("重新点一次「开始配对」"),
                           "★★ 请求过期时 Approve 回 409,以前两处都丢掉返回值 —— "
                           + "人点了批准、什么反馈都没有,那一行只是悄悄消失");
                    // ⑰ 不许"一有请求就自动弹窗" —— enroll 是匿名的,那等于把弹窗交给局域网上任何人触发
                    Assert(!body4.Contains("_popped.Add(p.RequestId)"),
                           "★★ 待批准的只进列表,由人主动点某一条才弹确认 —— "
                           + "自动弹框 = 你屏幕上跳出什么由对方的到达时机决定");
                    // ⑱ 自报显示名不许决定窗口尺寸
                    Assert(body4.Contains("SafeDisplayName("),
                           "★ 自报的显示名要截断 + 剔控制字符再上界面");
                    var safeFn = Slice(dv4, "static string SafeDisplayName", "/// <summary>手填入口");
                    Assert(safeFn is not null && safeFn.Contains("char.IsControl") && safeFn.Contains("48"),
                           "★ 剔控制字符并截断");
                    var cd = TryReadSource(Path.Combine("Views", "ConfirmDialog.cs"));
                    if (cd is not null)
                        Assert(Body(cd).Contains("MaxHeight") && Body(cd).Contains("ScrollViewer"),
                               "★★ 确认框要有高度上限并能滚动 —— 否则一个超长的自报名字就能把按钮顶出屏幕,"
                               + "那是一个由对方决定的界面拒绝服务");
                    // ⑲ 两边的批准截止时间要对齐
                    var ctDl = TryReadSource(Path.Combine("..", "transport", "ClientTransport.cs"));
                    if (ctDl is not null)
                    {
                        Assert(!Body(ctDl).Contains("180_000"),
                               "★★ 副机不能只等 180 秒而主机给 5 分钟 —— 人在 3~5 分钟之间回来点批准时,"
                               + "主机成功建了记录、副机早已超时退出,列表里就多一条 provisioning 幽灵");
                        Assert(Body(ctDl).Contains("ApprovalWaitMs"),
                               "★ 等待上限对齐到主机侧的过期时间");
                    }
                    // ⑳ 管理面令牌:有就带(中枢还没升级时不假装有这层保护)
                    var haTok = TryReadSource(Path.Combine("Services", "HubAdmin.cs"));
                    if (haTok is not null)
                    {
                        Assert(Body(haTok).Contains("X-LocalAI-Admin"),
                               "★★ 管理面请求要带令牌头 —— 「能连回环」的不止坐在主机前的人,"
                               + "浏览器里的网页也能;自定义头跨源发不出去");
                        Assert(Body(haTok).Contains("File.Exists(p) ? File.ReadAllText(p).Trim() : null"),
                               "★ 令牌文件不存在就不带 —— 不假装有一层不存在的保护");
                    }

                    // ⑬ 「开始配对」要有在途闸(连点两次会发两条 enroll,两组六词互相盖掉)
                    Assert(body4.Contains("if (_pairing) return;"),
                           "★★ 连点两次不能发出两条 enroll —— 六词卡是共用的,后一条会盖掉前一条,"
                           + "主机弹窗上那六个词在副机屏幕上就不存在了,人没得可比");
                    // ⑭ 主机卡也要有改地址/找回它 —— 主机换了 IP 时它自己那台最没救
                    var hostCard = Slice(dv4, "UIElement HostSelfCard", "async Task SelfPairAsync");
                    Assert(hostCard is not null && hostCard.Contains("ChangeDialRow("),
                           "★★ 主机自己那台换了 IP 也要有出路 —— 以前这张卡上连输入框都没有,"
                           + "只能去手改 profile.json,而界面从没说过它在哪");
                    // ⑮ 链不通 ≠ 过期:处置正好相反,而旧文案加粗否掉了唯一的出路
                    var hcCls = TryReadSource(Path.Combine("Services", "HubClient.cs"));
                    if (hcCls is not null)
                    {
                        Assert(Body(hcCls).Contains("HubIdentityChanged"),
                               "★★ 链不到钉住的 CA 要单列一态 —— 那是「换了中枢」,必须重新配对;"
                               + "而「过期」不必重配。旧代码把所有 TLS 失败都判成过期,界面还加粗说不必重配");
                        Assert(!Body(hcCls).Contains("static bool IsCertExpiry"),
                               "★ 那个把所有 AuthenticationException 判成过期的旧函数不许再存在");
                        // ★★ 2026-08-06 改判据(**不是**放宽):原来查的是 HubClient.cs 的**源码里**
                        //   有没有「必须重新配对」这几个字。那句话现在搬到了 TlsFailure.Explain ——
                        //   判据与文案同源是这次改动的**目的**(两份必然漂开,而漂开那天两边都不会红),
                        //   于是这条断言开始因为**正确的重构**而红,说的还是一句假话(它确实在说)。
                        //   ⇒ 判词没变,判据改成查【用户真会看到的那句话】,并另钉一条"这一态真的路由到它"。
                        //   这比查源码文本强:它测的是**输出**,不是那句话存在哪个文件里。
                        //   (ASSERTION-PITFALLS 第 3b 条:判词说"文案要说清",判据却写死了一个文件。)
                        Assert(LocalAI.ClientTransport.TlsFailure
                                   .Explain(LocalAI.ClientTransport.TlsFailureKind.HubIdentityChanged)
                                   .Contains("必须重新配对"),
                               "★ 换了中枢时要说清唯一出路就是重新配对");
                        Assert(CodeOnly(hcCls).Contains("HubState.HubIdentityChanged   => TlsFailure.Explain"),
                               "★★ 而且这一态真的路由到那句话 —— 否则文案对了、界面上却看不到它");
                    }

                    // ⑫ UI 侧不许 sync-over-async —— 2026-08-04 实机卡死就是一行 .GetAwaiter().GetResult()
                    //   在 UI 线程上等一个 async 方法:里面 await 的续体要回 UI 线程,而 UI 线程正卡着。
                    //   ★ 允许的写法是先 Task.Run 把它挪出 UI 线程再 GetResult(App.xaml.cs 里那处就是)。
                    foreach (var f in new[] { Path.Combine("Services", "HostSetup.cs"),
                                              Path.Combine("Services", "HubAdmin.cs"),
                                              Path.Combine("Services", "HubDiscovery.cs"),
                                              Path.Combine("Views", "DevicesView.cs") })
                    {
                        var src = TryReadSource(f);
                        if (src is null) continue;
                        foreach (var line in Body(src).Split('\n'))
                        {
                            if (!line.Contains("GetAwaiter().GetResult()") && !line.Contains(".Wait()")) continue;
                            Assert(line.Contains("Task.Run("),
                                   $"★★ {Path.GetFileName(f)} 里有一处 sync-over-async 没先 Task.Run —— "
                                   + "在 UI 线程上这会【直接死锁】(今天就卡死过一次):" + line.Trim());
                        }
                    }

                    // ⑨ 起中枢要绑【用户刚选的那张网卡】,不能靠 .cmd 里写死的地址
                    var step = Slice(dv4, "async Task StartEdgeStepAsync", "/// <summary>");
                    Assert(step is not null && step.Contains("run-lan ") && step.Contains("bindIp"),
                           "★★ 有网卡地址就直接 run-lan <ip> —— 启动Edge.cmd 把地址写死成一台开发机的,"
                           + "换机器或换一次 DHCP 租约就绑到不存在的地址上");
                    Assert(step is not null && step.Contains("启动Edge.cmd"),
                           "★ 拿不到地址时仍退回 .cmd,但要如实说明它绑的是写死的那个地址");
                    // ⑩ 状态行不许跨 Build 共享(它被 Clear 掉之后界面会永久静默,再 Add 还会抛)
                    Assert(!Body(dv4).Contains("readonly TextBlock _setupStatus"),
                           "★★ 状态行不许是跨 Build 共享的单例控件 —— 被摘出可视树后界面永久静默,"
                           + "重新挂回去还会抛 InvalidOperationException,把唯一能推进的按钮一起清掉");
                    var picker2 = Slice(dv4, "void BuildNicPicker", "async Task SetupHostAsync");
                    Assert(picker2 is not null && !picker2.Contains("host.Children.Clear()"),
                           "★★ 选网卡的面板不许 Clear 整个动作区 —— 那会把状态行一起摘走");

                    // ⑧ 403 的文案要指向【现在】的开窗方式,不是命令行时代的说法
                    Assert(!body4.Contains("Edge 窗口里输入 open"),
                           "★★ 「去 Edge 窗口里敲 open」是命令行时代的说法 —— 留着会把人支到黑框里");
                    Assert(body4.Contains("展开「＋ 添加一台新电脑」"),
                           "★ 窗口关着时告诉他主机上现在该做哪一步");
                }
            }

            // ---- S4 · 配对审批与设备管理接【主机本地回环管理面】(D37/D48)----
            {
                var ha = new Services.HubAdmin();
                Assert(Services.HubAdmin.DefaultAdminPort == 8442,
                       "★ 回环管理端口与 lan-edge 的 AdminPort 常量一致 —— 对不上的表现是「主机上也说不是主机」");
                Assert(!ha.Available && ha.HubId is null,
                       "★ 没探测过之前一律【不可用】—— 管理面的可达性是探出来的,不是假设出来的");
                // ★ 探一个必定连不上的端口:必须【如实说不可用】,不许 fail-open 成"可用"
                Environment.SetEnvironmentVariable("LOCALAI_ADMIN_PORT", "1");
                var ha1 = new Services.HubAdmin();
                var probed = System.Threading.Tasks.Task.Run(async () => await ha1.ProbeAsync("whatever")).GetAwaiter().GetResult();
                Assert(!probed, "★★ 连不上就是连不上 —— 管理面探测 fail-closed(连不上却说可用 = 界面给出根本点不动的按钮)");
                // ★★ 失败要被【分类】。2026-08-03 的真事:主机那台自己没启动 lan-edge,
                //   界面把"回环没人听"直接说成「这台不是主机」,人就去怀疑配错了机器。
                //   分类存在的全部意义就是让界面能说【观察到的事】,而不是替它下一个证明不了的结论。
                Assert(ha1.LastProbe == Services.AdminProbeResult.NotListening,
                       "★★ 回环端口没人听要归成 NotListening —— 它只说明"
                       + "「中枢没在这台机器上跑」,【不等于】这台不是主机(主机没启动 Edge 时也是这个结果)");
                Assert(ha1.LastError is { Length: > 0 } e1 && e1.Contains("没有人听"),
                       "★ 探测结果要说人话且只说观察到的 —— 不下「这台不是主机」这种结论");
                Environment.SetEnvironmentVariable("LOCALAI_ADMIN_PORT", null);

                // ★ "本机有没有主机端程序"是【线索】不是判据。
                //   ★★ 这里【不能】直接断言 HostToolsDir() 是 null —— 那等于在断言"自检此刻跑在哪个目录下":
                //     装在 dist\client 时 dist\host 真的就在旁边(会红),从 dist\client-pack 跑又没有(会绿),
                //     两边都不说明代码对不对。本次真踩了这一回,所以改成对纯逻辑做**确定性**的两向测试。
                {
                    var htTmp = Path.Combine(Path.GetTempPath(), "localai-selftest-hosttools-" + Guid.NewGuid().ToString("N"));
                    var client = Path.Combine(htTmp, "client");
                    var host = Path.Combine(htTmp, "host");
                    Directory.CreateDirectory(client);
                    Directory.CreateDirectory(host);
                    try
                    {
                        Assert(Services.HubAdmin.HostToolsDirNextTo(client) is null,
                               "★ 旁边有 host 目录但【没有那个 exe】时仍返回 null —— 线索要看到真东西才算数");
                        File.WriteAllText(Path.Combine(host, "localai-lan-edge.exe"), "x");
                        Assert(Services.HubAdmin.HostToolsDirNextTo(client) is { } got
                               && string.Equals(Path.GetFullPath(got).TrimEnd('\\'), Path.GetFullPath(host).TrimEnd('\\'),
                                                StringComparison.OrdinalIgnoreCase),
                               "★★ 旁边真有主机端程序时要找得到 —— 这条线索是"
                               + "「主机但 Edge 没启动」与「这台真不是主机」的唯一分界");
                        Assert(Services.HubAdmin.HostToolsDirNextTo(null) is null
                               && Services.HubAdmin.HostToolsDirNextTo("  ") is null,
                               "★ 拿不到目录就是没这条线索,不抛异常");
                    }
                    finally { try { Directory.Delete(htTmp, true); } catch { } }
                }

                var haSrc = TryReadSource(Path.Combine("Services", "HubAdmin.cs"));
                if (haSrc is not null)
                {
                    Assert(haSrc.Contains("http://127.0.0.1:") && !haSrc.Contains("https://"),
                           "★ 管理面只走回环明文 —— 门禁是【端口 + 回环】而不是证书;在回环上再套 mTLS 会把"
                           + "「主机自己管自己」绑死在「必须先配对成功」上,而配对审批本身就归它管(鸡生蛋)");
                    Assert(haSrc.Contains("!string.Equals(HubId, expectHubId"),
                           "★★ 连得上还不够:自报的 hubId 必须与本机档案一致 —— 同机可能跑着另一个中枢");
                }
                // ★ 主机上不该再手填中枢地址(用户问:「或许不需要填?」)
                Assert(Services.HubAdmin.EdgePort == 8443, "业务口端口与 lan-edge 的 run-lan 一致");
                var haSrc2 = TryReadSource(Path.Combine("Services", "HubAdmin.cs"));
                if (haSrc2 is not null)
                {
                    // ★★ 本机业务口探测:以前撞上第一个能连的 8443 就 return,还自称「肯定证据」。
                    //   TCP 连得上只证明"这个地址的 8443 上有监听者";本机常有不止一张网卡
                    //   (VirtualBox 的 192.168.56.x 仅主机适配器),静默挑一个 = 可能把
                    //   【只有本机看得见】的地址写进配对档案、再被抄到副机上,而副机永远连不上。
                    Assert(!haSrc2.Contains("DiscoverEdgeDialAsync("),
                           "★★ 那个「撞上第一个就返回」的老接口不许再存在");
                    Assert(haSrc2.Contains("DiscoverEdgeDialsAsync"),
                           "★ 换成返回【全部】通过校验的地址,由界面让人自己挑");
                    Assert(haSrc2.Contains("HubDiscovery.ProbeOneAsync"),
                           "★★ 连得上还不够 —— 要读证书名认出是我们这个中枢,那才叫肯定证据");
                    Assert(haSrc2.Contains("HubDiscovery.ShortHubId"),
                           "★★ 比 hub_id 之前要先换算形状 —— UUID 与证书名里的 16 位短号不是同一个字符串");
                    var hcRe = TryReadSource(Path.Combine("Services", "HubClient.cs"));
                    if (hcRe is not null)
                    {
                        var re = Slice(hcRe, "public async Task<bool> RediscoverAsync", "public void UnpairLocal");
                        Assert(re is not null && re.Contains("HubDiscovery.ShortHubId(Profile.HubId)"),
                               "★★ 「在局域网里找回它」也要先换算 hub_id 形状 —— 不换算这个按钮永远失败");
                        Assert(re is not null && re.Contains("ScanExplain(scan"),
                               "★★ 「为什么没找到」四种情形统一由 ScanExplain 说 —— 两处文案各写一份必然漂");
                    }
                    {
                        // ★ 行为测试:四种情形各说各的话,不能混
                        var noV4 = new Services.ScanResult(new(), new(), new(), new(), NoUsableV4: true);
                        Assert(Services.HubClient.ScanExplain(noV4, "中枢").Contains("手填地址也连不上"),
                               "★★ 没有可用 IPv4 时要说清【手填也没用】—— 出路是去接网线");
                        var wide = new Services.ScanResult(new(), new(), new() { "10.1.2.3/22" }, new(), false);
                        Assert(Services.HubClient.ScanExplain(wide, "中枢").Contains("结构上覆盖不到"),
                               "★ 掩码太宽是结构性走不通");
                        var tiny = new Services.ScanResult(new(), new(), new(), new() { "100.1.2.3/32" }, false);
                        var tinyMsg = Services.HubClient.ScanExplain(tiny, "中枢");
                        Assert(tinyMsg.Contains("没有别的主机可扫") && !tinyMsg.Contains("宽于 /24"),
                               "★★ /32 不能说成「掩码宽于 /24」—— 方向正好说反");
                        var scanned = new Services.ScanResult(new(), new() { "192.168.1.0/24" }, new(), new(), false);
                        Assert(Services.HubClient.ScanExplain(scanned, "中枢").Contains("扫过 192.168.1.0/24"),
                               "★ 扫过了没找到,就说扫过哪些");
                    }
                    Assert(haSrc2.Contains("169.254."),
                           "★ 跳过 APIPA 自封地址 —— 没拿到 DHCP 的网卡上探不出业务口");
                }
                var dvSrc = TryReadSource(Path.Combine("Views", "DevicesView.cs"));
                if (dvSrc is not null)
                {
                    Assert(Body(dvSrc).Contains("不能】填 127.0.0.1"),
                           "★ 主机上要说清为什么不能填回环 —— 业务口只绑网卡 IP,回环上只有管理面");
                    Assert(dvSrc.Contains("ProbeAsync(TheApp.Hub.Profile?.HubId)"),
                           "★ 界面拿【肯定证据】判断这台是不是主机,不拿 ThisMachineIsHub 那个启发式当权限判定");
                    // ★★ 这三条钉的是 2026-08-03 那个真实事故:主机那台显示「这台不是主机」。
                    Assert(!Body(dvSrc).Contains("这台不是主机"),
                           "★★ 探测失败【不等于】这台不是主机 —— 代码只观察到"
                           + "「回环管理面没应答」,不许把它塌缩成一个证明不了的结论");
                    Assert(Body(dvSrc).Contains("中枢没在这台机器上运行"),
                           "★★ 要说【观察到的事】:中枢没在这台机器上运行");
                    // ★ [4] 管理面刚答过话就证明 Edge 在跑;这一步又是本机连本机、防火墙不参与。
                    //   这两条恰好是代码刚排除掉的,不许再让人去查。
                    Assert(!Body(dvSrc).Contains("先确认 Edge 起着、防火墙放行了。"),
                           "★★ 已经证明 Edge 在跑、且防火墙不参与时,不许再支人去查这两样");
                    Assert(Body(dvSrc).Contains("Edge 绑在了另一个地址上"),
                           "★★ 要指向真原因:Edge 绑的地址不在本机当前的网卡表里");
                    Assert(Body(dvSrc).Contains("LocalIPv4List()"),
                           "★ 把本机当前网卡摆出来供人和 Edge 窗口里那行对照 —— 光说\"对不上\"没法照着做");
                    // ★ [7] 已配对卡上唯一看起来能修连接的按钮,失败时也要提"本机的 Edge 没启动"
                    var findSlice = Slice(dvSrc, "在局域网里找回它", "find.Margin");
                    Assert(findSlice is not null && findSlice.Contains("HostToolsDir()"),
                           "★★ 「找回它」失败也要走同一条线索 —— 人会先点它,远早于滚到第三张卡");
                    // ★★ 这里原来有一条断言,要求界面写明「必须用普通用户双击,否则报密钥集不存在」。
                    //   **它已退役** —— 同日实测把那个说法推翻了:这台机器 EnableLUA=0(UAC 关闭),
                    //   桌面 explorer 本身就是 High,身份就是在 High 下铸的,两把密钥在 High 进程里
                    //   CngKey.Open 都成功。照那句写就是在界面上印一句假话。
                    //   真正的判据是【密钥打不打得开】(见 Elevation.DeviceKeyUsable 与
                    //   decision-packets/integrity-guard-asks-wrong-question-2026-08-03.md)。
                    //   ⇒ 现在钉的是:失败时把【中枢自己吐出来的原因】原样带出来,而不是我们替它编一个。
                    Assert(Body(dvSrc).Contains("中枢自己打印的最后几行"),
                           "★★ 中枢起不来时把它【自己吐出来的话】摆出来 —— "
                           + "别用我们猜的理由(「你是不是用管理员跑的」)去盖住它");
                    Assert(!Body(dvSrc).Contains("必须用【普通用户】双击"),
                           "★★ 不许再断言「必须普通用户」—— UAC 关闭的机器上根本没有普通身份的进程,"
                           + "那句话在那里是假的");
                    Assert(dvSrc.Contains("逐字一致") && dvSrc.Contains("这时候必须点取消"),
                           "★★ 六个词【不代人比对】:界面只把词摆出来并要求人确认逐字一致,不提供跳过");
                    // ★ 看【去注释后的正文】—— 解释“为什么不能这么写”的注释里就带着这个词,不脱注释会自己撞自己
                    Assert(!Body(dvSrc).Contains("主机还没升级"),
                           "★ 副机那条路是【结构性】走不通(D37/D48),不许再写成\"暂时还没有\" —— 那会让人等一个不会来的版本");
                    var pend = Slice(dvSrc, "UIElement PendingRow(", "UIElement DeviceRow(");
                    Assert(pend is not null && pend.Contains("SecondsLeft"),
                           "★ 待批准的请求要显示剩余秒数 —— 到点它在主机侧就失效了,界面不能装作它还在");
                }
            }

            // ================= P3c 收尾(2026-08-03 用户裁定「把 P3c 收尾」)=================
            {
                // ---- 记忆面板:判据四项里此前缺的【编辑】与【溯源展开】----
                var mc = new Services.MemoryCenter();
                var mid = Services.MemoryCenter.NewId();
                mc.Add(new Services.MemoryEntry(mid, "原标题", "原正文", Services.MemoryKind.Summary,
                        Services.ProjectScope.Personal, Services.MemberContext.Current, "p-1",
                        new[] { "s-1", "s-2" }, DateTime.Now));
                Assert(!mc.EditText(mid, "   ", "x"), "★ 标题空了不许保存 —— 列表里会变成一条没名字的东西");
                Assert(mc.EditText(mid, "改过的标题", "改过的正文"), "★ 记忆条目可编辑(P3c 判据四项之一)");
                var after = mc.Find(mid)!;
                Assert(after.Title == "改过的标题" && after.Body == "改过的正文", "改的内容真的写进去了");
                Assert(after.EditedByHuman && after.EditedAt is not null,
                       "★★ 人手改过要打标记 —— 不标的话人改的内容会以【AI 摘要】的身份进 prompt,那是骗下游");
                Assert(after.Scope == Services.ProjectScope.Personal && after.SourceProjectId == "p-1"
                       && after.SourceSessionIds!.Count == 2,
                       "★ 编辑只动标题与正文:范围是权限动作、来源是溯源锚,都不许在编辑框里悄悄改");
                Assert(!mc.EditText(mid, "改过的标题", "改过的正文"), "没变就不写、不广播(免得白落一次盘)");
                Assert(!mc.EditText("no-such-id", "a", "b"), "改一条不存在的记忆:老实返回 false");

                var sv = TryReadSource(Path.Combine("Views", "StorageView.cs"));
                if (sv is not null)
                {
                    Assert(sv.Contains("SegChip(\"溯源\"") && sv.Contains("SegChip(\"编辑\""),
                           "★ 记忆条目上有【溯源】与【编辑】两个入口(判据写的四项要齐)");
                    var tb = Slice(sv, "FrameworkElement TraceBlock(", "FrameworkElement EditBlock(");
                    Assert(tb is not null, "切片得真的取到(取不到就跳过 = 假断言)");
                    Assert(tb is null || (tb.Contains("没有记来源会话") && tb.Contains("原文已被删除")
                           && tb.Contains("这条会话已经不在了")),
                           "★★ 溯源要把四种情形【分开说】:有原文 / 原文删了 / 没记来源 / 会话没了 —— "
                           + "含糊的溯源比没有溯源更坏(P3a 的硬线是「每条可溯源」)");
                }

                // ---- 协议版本协商(D45:「主机 v5,你 v3,请更新」)----
                var hubC = new Services.HubClient();
                Assert(Services.HubClient.ClientProtocol >= 1 && Services.HubClient.ProtocolHeader == "X-LocalAI-Protocol",
                       "客户端自报协议版本,头名两边一致");
                // ★★ 这条以前写的是 ProtocolNote 含"未协商" —— 那把一个混淆钉死了:
                //   HubProtocol 的 null 同时表示「连上了但它没报版本」和「压根没连上过」,
                //   而 Edge 没启动时是后者,界面却只会说前者,读起来像"中枢版本太旧",
                //   人就去查中枢是不是该重编 —— 它连一个字节都没回过。
                Assert(hubC.HubProtocol is null && hubC.ProtocolNote.Contains("无从谈起"),
                       "★★ 一个全新的、从没和中枢通过话的客户端要说【无从谈起】,不许说成\"中枢未声明版本\"");
                var hcNote = TryReadSource(Path.Combine("Services", "HubClient.cs"));
                if (hcNote is not null)
                {
                    Assert(hcNote.Contains("_protocolObserved = true;"),
                           "★ 只有真的读到过一次响应头才置 observed —— 这之后 null 才是\"它没报\"");
                    Assert(Body(hcNote).Contains("中枢未声明协议版本"),
                           "★ 「连上了但它没报版本」这句仍要在 —— 分开说,不是删掉一半");
                }
                var hcSrc = TryReadSource(Path.Combine("Services", "HubClient.cs"));
                if (hcSrc is not null)
                {
                    Assert(hcSrc.Contains("HubState.ProtocolMismatch"),
                           "★ 协议对不上单列一态 —— 症状像\"连不上\"但处置是【去更新某一端】,混进 Offline 会让人一直重启中枢");
                    var call = Slice(hcSrc, "public async Task<(int status, string body)> CallAsync", "public async Task EndSessionAsync");
                    // ★ 两个下标都得【先确认存在】再比大小:IndexOf 找不到返回 -1,
                    //   而 -1 恒小于任何下标 —— 照着写 a < b 的话,把那行删掉断言反而变绿。
                    //   (这条写下去时就先红了一次:我找的是一串根本不存在的文本。)
                    // ★ 锚点换成"协议闸那一句"与"置在线那一句" —— 原来锚的是
                    //   `HubState.Online : HubState.Offline` 这个三元式,5xx 单列成一态时它就没了,
                    //   断言会因为【重构】而红,而不是因为顺序真的坏了。锚要锚在语义上,别锚在写法上。
                    var iNote = call?.IndexOf("NoteProtocol(r.headers)", StringComparison.Ordinal) ?? -1;
                    var iGate = call?.IndexOf("if (State == HubState.ProtocolMismatch) return", StringComparison.Ordinal) ?? -1;
                    var iOnline = call?.IndexOf("State = HubState.Online", StringComparison.Ordinal) ?? -1;
                    Assert(iNote >= 0 && iGate >= 0 && iOnline >= 0 && iNote < iGate && iGate < iOnline,
                           "★★ 先过协议闸再判在线 —— 两边对格式的理解不一致时,解出来的东西本身就不可信");
                    // ★★ 5xx 单列一态:走到那一行时 TCP 通了、mTLS 过了、响应都读到了 ——
                    //   能确定的恰恰是"中枢在"。判成 Offline 会把人支去重启 Edge / 查防火墙 / 改地址。
                    Assert(call is not null && call.Contains("HubState.HubServerError"),
                           "★★ 中枢应答 5xx 要单列一态 —— 那证明中枢【在】,说成\"未连接\"是整整一趟无用功");
                    var mwMap = TryReadSource("MainWindow.xaml.cs");
                    if (mwMap is not null)
                        Assert(mwMap.Contains("status.hub_error") && mwMap.Contains("status.proto_mismatch"),
                               "★★ 这两态要有自己的顶栏文案 —— 掉进 _ => 未连接 等于没单列");
                    Assert(hcSrc.Contains("请更新【客户端】") && hcSrc.Contains("请更新【中枢】"),
                           "★ 说清该更新哪一端(D45 原话:「主机 v5,你 v3,请更新」),不只说\"版本不匹配\"");
                }

                // ---- 换网段:改地址而不是重新配对 ----
                Assert(!hubC.SetDial("不是地址"), "★ 地址格式不对就拒收(收下去只会在\"连不上\"时多一层歧义)");
                Assert(hcSrc is null || hcSrc.Contains("public bool SetDial(string dial)"),
                       "★ 有【改连接地址】入口 —— 重新配对会删掉本机私钥,把有效身份亲手销毁");
                var dv = TryReadSource(Path.Combine("Views", "DevicesView.cs"));
                Assert(dv is null || dv.Contains("ChangeDialRow(p)"), "★ 已配对卡片上真的把改地址露出来了");

                // ══════════════════════════════════════════════════════════════
                //  D? · 证书生命周期真的接上了(审计 A5:此前三样东西**零调用点**)
                // ══════════════════════════════════════════════════════════════
                //  A5 的形状值得记住:core 车道**完全遵守了纪律** —— 没越界,写了决议包点名
                //  client 半边要改 HubClient.cs 哪一行 —— 而那一行至今没人改。
                //  同一轮里 TlsFailure.cs 却被链进了客户端构建 ⇒ **随包发布的死代码**。
                //  ⇒ 判据不是"库写好了",是"用户在过期之前看得见,而且给的建议他能执行"。
                //
                //  ★★★ 本段分成【运行时】与【读源码】两半,这个划分是被出包**当场教出来**的:
                //    初稿把每一条都写成 `Assert(src is not null && src.Contains(...))`,
                //    结果发布产物旁边没有源码 ⇒ `TryReadSource` 全部返回 null ⇒ **10 条当场红**,
                //    build-client.ps1 拒绝出包(它做对了)。
                //  ⇒ 但"照抄本仓惯例改成 `src is null || ...`"是**fail-open**:它在唯一
                //    检查不了的那个形态下无声放行。所以先问一句:**这条真的非读源码不可吗?**
                //    答案里有一半是"不是" —— 枚举成员、界面词、告警措辞都能在运行时问出来,
                //    而且**运行时问比查源码文本更强**(它测的是行为,不是那串字写在哪个文件里)。
                //  ⇒ 能运行时问的一律搬到 ① 段(发布产物里照样跑);
                //    只有【接线位置/顺序】这类真的只在源码里的,才落进 ② 段并按惯例跳过,
                //    由 build-client.ps1 的 SRCMISS 口径把这笔账打出来。
                {
                    // ══════════ ① 运行时可问的 —— 发布产物里**照样跑** ══════════
                    var states = Enum.GetNames(typeof(HubState));
                    foreach (var localState in new[] { "LocalCertExpired", "LocalProfileUnusable" })
                        Assert(states.Contains(localState),
                               $"★★ HubState 有 {localState} 这一格(此前是空的,实际归宿是 Offline =「中枢没开机」)");
                    Assert(states.Length == 12,
                           $"★ 五种归因 = 12 个状态(实得 {states.Length})—— 反向全表:多一格少一格都要有人看见");

                    // ★★ 两态**不许合并**:代价不同 —— 一个重配要搭上一把本来有用的私钥,一个没有可搭的。
                    Assert(Strings.Get("status.local_cert_expired") != Strings.Get("status.local_unusable"),
                           "★★★ 两态的界面词不同(合并成一句 = 把「损失了什么」这件事对用户藏起来)");
                    Assert(Strings.Get("status.local_cert_expired") != Strings.Get("status.cert_expired"),
                           "★★★ 【本机】证书过期 ≠ 【主机】证书过期 —— 处置一个是重新配对,一个是去主机上续签");
                    // ★ 反向:两个键真的在词表里(缺键时 Strings.Get 返回键名本身,
                    //   而两个不同的键名彼此也不相等 ⇒ 上面两条会**恒真**。这条堵住那个恒真)。
                    foreach (var k in new[] { "status.local_cert_expired", "status.local_unusable" })
                        Assert(Strings.Get(k) != k, $"★ 词表里真的有 {k}(缺键会让上面那两条恒真)");

                    // ★★ 主机侧轮换告警的**措辞**:运行时直接问那个属性,不去源码里找字符串。
                    //   只说"需要注意"的告警等于没说 —— 必须带剩余天数和该做什么。
                    var scWarn = new Services.HubAdmin();
                    Assert(scWarn.ServerCertWarning is null,
                           "★ 反向:还没探测过 ⇒ 不报主机证书告警(不编一个出来)");

                    // ---- 三样东西的**行为**在不在(不看源码,看结果)----
                    Assert(LocalAI.ClientTransport.TlsFailure.WarnLocalCert(null, DateTimeOffset.UtcNow) is null,
                           "★ 没有档案时不报证书告警(WarnLocalCert 真的接得通,且反向不恒真)");

                    // ══════════ ② 只有源码答得了的:接线位置与顺序 ══════════
                    //  ★ 这些是**结构**判据("这一行调用写在哪里""谁排在谁前面"),
                    //    发布产物旁边没有源码时整段跳过 —— build-client.ps1 会把 SRCMISS 打出来,
                    //    所以它是**记了账的缺口**,不是无声放行。
                    var hc = TryReadSource(Path.Combine("Services", "HubClient.cs"));
                    if (hc is not null)
                    {
                        var hcCode = CodeOnly(hc);
                        // ---- 三样东西真的有调用点(A5 就死在这里)----
                        Assert(hcCode.Contains("TlsFailure.Classify("),
                               "★★★ ClassifyTlsFailure 走 transport 的 TlsFailure.Classify —— "
                               + "不在客户端里另写一份判据(两份必然漂开,而漂开那天两边都不会红)");
                        Assert(hcCode.Contains("Transport.RenewDeviceCertIfDue("),
                               "★★★ RenewDeviceCertIfDue 在客户端里**有调用点** —— "
                               + "没有它,那套续签代码就是随包发布的死代码,设备证书 90 天一到只能重新配对");
                        Assert(hcCode.Contains("TlsFailure.WarnLocalCert("),
                               "★★★ 证书相位判据接上了(CertLifecycle 经 WarnLocalCert)—— 这是「过期之前看得见」的来源");

                        // ---- 老判据的三处病灶都已拆掉 ----
                        Assert(!hcCode.Contains("UntrustedRoot") && !hcCode.Contains("PartialChain"),
                               "★★ 客户端里不再有靠**异常英文文本**认因的针(它们在 TlsFailure 里,由实测原文钉着)");
                        Assert(!hcCode.Contains("is System.Security.Authentication.AuthenticationException"),
                               "★★★ 那条 AuthenticationException 兜底**已删** —— 实测它会把「拨到一个普通 HTTP 服务」"
                               + "判成「必须重新配对」,而重新配对先删本机私钥");
                        Assert(!hcCode.Contains("X509ChainStatusFlags"),
                               "★ 那条 `e is X509ChainStatusFlags`(枚举与 Exception 永不同类,CS0184 死代码)也一并清掉");
                    }

                    var mwCert = TryReadSource("MainWindow.xaml.cs");
                    var dvCert = TryReadSource(Path.Combine("Views", "DevicesView.cs"));
                    foreach (var localState in new[] { "LocalCertExpired", "LocalProfileUnusable" })
                    {
                        if (mwCert is not null)
                            Assert(mwCert.Contains("HubState." + localState),
                                   $"★★ 顶栏有 {localState} 的文案 —— 掉进 _ => 未连接 等于没单列");
                        if (dvCert is not null)
                            Assert(dvCert.Contains("HubState." + localState),
                                   $"★★ 设备页有 {localState} 的文案(那张卡上就有红色的「解除本机配对」按钮)");
                    }

                    // ---- 过期【之前】就看得见:告警必须排在【在线】那一格**之前** ----
                    // ★ 这条反直觉,也正是它容易被写错的原因:过期之前客户端**正是在线的**。
                    //   告警只挂在断线那几格的话,它永远等到过期之后才出现 —— 而那时续签路由已经够不着了。
                    if (mwCert is not null)
                    {
                        var rs = Slice(mwCert, "public void RefreshStatus()", "// 左下角状态行");
                        Assert(rs is not null, "切片得真的取到(取不到就跳过 = 假断言)");
                        var iWarn = rs?.IndexOf("Hub.CertWarning", StringComparison.Ordinal) ?? -1;
                        var iOnline = rs?.IndexOf("State == HubState.Online", StringComparison.Ordinal) ?? -1;
                        // ★ 两个下标都先确认存在再比大小 —— IndexOf 找不到返回 -1,而 -1 恒小于任何下标,
                        //   照着写 a < b 的话,把那行删掉断言反而会变绿(ASSERTION-PITFALLS 第 9 条第 3 种)。
                        Assert(iWarn >= 0 && iOnline >= 0 && iWarn < iOnline,
                               "★★★ 证书告警排在【在线】那一格之前 —— 否则它永远等到过期之后才出现,"
                               + "而那时唯一的自愈窗口已经关了");
                    }

                    // ---- 主机侧轮换的 fail-closed 最后一段路:界面真的读了 ----
                    if (dvCert is not null)
                        Assert(dvCert.Contains("ServerCertWarning"),
                               "★★★ 主机卡片读 /admin/ping 的 serverCert —— 此前它**全仓没有读取方**,"
                               + "而 lan-edge 那行注释写着「主机界面据此报警」:吐出来没人读 = 没响");
                }

                // ══════════════════════════════════════════════════════════════
                //  D92 硬前置 · 跨语言【成对断言】的**客户端半边**
                // ══════════════════════════════════════════════════════════════
                //  服务端半边在 10-core/lan-edge/Program.cs 丙 节(钉顶层键集合)。
                //  ★★ 这一半钉的是另一件事:**拿那个形状能不能解析出目标字段**。
                //     A1 就死在这两件事之间 —— 服务端测键、客户端测解析,各测各的,
                //     而客户端喂给自己的是**自己造的**形状,于是服务端把字段搬了家也照样绿。
                //  ⇒ 所以这里的形状**由 WireContracts 生成**,不是手抄的:表变了这里立刻跟着变。
                {
                    string Shape(string[] keys, Func<string, string> val)
                        => "{" + string.Join(",", keys.Select(k => $"\"{k}\":{val(k)}")) + "}";
                    string ServerCertVal(string k) => k switch
                    {
                        "notAfter" => "\"2026-08-28T15:14:18+02:00\"",
                        "daysLeft" => "3.5",
                        "phase" => "\"Critical\"",
                        "consecutiveFailures" => "2",
                        "lastError" => "\"TPM busy\"",
                        "needsAttention" => "true",
                        _ => "null",
                    };
                    var pingJson = "{" + string.Join(",", LocalAI.Identity.WireContracts.AdminPing.Select(k => k switch
                    {
                        "ok" => "\"ok\":true",
                        "hubId" => "\"hubId\":\"11111111-2222-3333-4444-555555555555\"",
                        "pairingWindowOpen" => "\"pairingWindowOpen\":false",
                        "serverCert" => "\"serverCert\":" + Shape(LocalAI.Identity.WireContracts.AdminPingServerCert, ServerCertVal),
                        _ => $"\"{k}\":null",
                    })) + "}";

                    var parsed = Services.HubAdmin.ParseServerCert(JsonDocument.Parse(pingJson).RootElement);
                    Assert(parsed is not null,
                           "★★★ 成对断言/客户端:拿【登记表生成的】/admin/ping 形状,真解析器解得出 serverCert");
                    Assert(parsed is { NeedsAttention: true, ConsecutiveFailures: 2 } && parsed.Phase == "Critical",
                           "★★★ 成对断言/客户端:目标字段逐个解得对(needsAttention / consecutiveFailures / phase)");
                    Assert(parsed is not null && Math.Abs(parsed.DaysLeft - 3.5) < 0.001,
                           "★★★ 成对断言/客户端:daysLeft 是数字不是字符串(服务端 Math.Round 出来的就是数字)");

                    // ★★ 反向一:少一个键 ⇒ 整条判 null。**半份状态比没有状态更坏** ——
                    //   它会在界面上显示一个可信但错误的天数,而人会照着它决定要不要动手。
                    // ★ 每一个键都算 —— 包括 lastError:它的**值**可以是 null(没出过错),
                    //   但**键**服务端每次都发(上面 lan-edge 那条键集合断言在一个全新身份上就是 6 个键)。
                    //   少一个键 = 对面不是我认识的那个形状,这时不该猜。
                    foreach (var drop in LocalAI.Identity.WireContracts.AdminPingServerCert)
                    {
                        var partial = "{\"serverCert\":" + Shape(
                            LocalAI.Identity.WireContracts.AdminPingServerCert.Where(k => k != drop).ToArray(), ServerCertVal) + "}";
                        Assert(Services.HubAdmin.ParseServerCert(JsonDocument.Parse(partial).RootElement) is null,
                               $"★★ 反向:serverCert 少了 `{drop}` ⇒ 判 null,不拼一份半截状态出来");
                    }
                    // ★★ 反向二:老中枢根本不报 serverCert ⇒ null,而不是编一个"健康"出来
                    Assert(Services.HubAdmin.ParseServerCert(
                               JsonDocument.Parse("{\"ok\":true,\"hubId\":\"x\"}").RootElement) is null,
                           "★★ 反向:主机没报 serverCert(老中枢/没装轮换器)⇒ null,不假装它健康");

                    // ★★ 元断言:登记表里凡是**客户端要解析**的契约,这里都得有对应的断言。
                    //   renew/* 两条的客户端解析在 transport selftest 里端到端测(那边有真的 Edge),
                    //   所以这里只认 /admin/ping 那两条 —— 但**数目由表算出来**,不写死。
                    var clientSide = LocalAI.Identity.WireContracts.All
                                     .Where(c => c.Name.StartsWith("GET /admin/ping", StringComparison.Ordinal)).ToArray();
                    Assert(clientSide.Length == 2,
                           $"★★ 元断言:登记表里归本处核对的契约有 {clientSide.Length} 条(表里新增 /admin/ping 相关的键组就得在这儿补断言)");
                }

                // ══════════════════════════════════════════════════════════════
                //  ▼▼▼ V4(契约欠债 · 证书/配对切片)—— 本段【只追加】,上面一律没动 ▼▼▼
                //  客户端半边:/admin/* 那 7 条(含 2 条元素子形状 + 1 条 409 失败分支)。
                //  服务端半边在 10-core/lan-edge/Program.cs 的「D? 丁」节(真 HTTP)。
                //  pair/* 与 identity/renew/* 的客户端半边在 20-client-win/transport/Program.cs
                //  —— 那边有真的测试 Edge,能把 Transport.Pair 端到端跑完;放这儿只能测仿造品。
                // ══════════════════════════════════════════════════════════════
                //  ★★ 形状**由登记表生成**,不是手抄的 JSON。手抄的话服务端把字段搬了家,
                //    这一半照样绿 —— 那正是 A1 的形状(两边各测各的,中间那条缝谁也没看)。
                {
                    // 按键名给一个类型对得上的值 —— 解析器会 GetString/GetBoolean/GetInt32,
                    // 类型给错的话红的是"类型"而不是"键集合",判据就说不清话了。
                    string V(string k) => k switch
                    {
                        "ok" => "true",
                        "generation" => "7",
                        "pairingWindowOpen" => "true",
                        "secondsLeft" => "180",
                        "sas" => "[\"alpha\",\"bravo\",\"charlie\",\"delta\",\"echo\",\"foxtrot\"]",
                        "devices" => "[]",
                        "members" => "[]",
                        "pending" => "[]",
                        "serverCert" => "null",
                        "certSha256Short" => "\"ab12cd34\"",
                        _ => "\"x\"",
                    };
                    string Obj(string[] keys, params (string k, string v)[] over)
                    {
                        var map = keys.ToDictionary(k => k, V);
                        foreach (var (k, v) in over) map[k] = v;
                        return "{" + string.Join(",", keys.Select(k => $"\"{k}\":{map[k]}")) + "}";
                    }
                    JsonElement E(string json) => JsonDocument.Parse(json).RootElement;
                    var pinnedV4 = new List<string>();

                    // ── CONTRACT:cert.admin.ping ──────────────────────────────
                    var pingJson2 = Obj(LocalAI.Identity.WireContracts.AdminPing, ("hubId", "\"hub-1\""));
                    var pg = Services.HubAdmin.ParsePing(E(pingJson2));
                    Assert(pg.ok && pg.hubId == "hub-1" && pg.windowOpen,
                           "★★★ CONTRACT:cert.admin.ping 客户端半边:拿登记表生成的形状,真解析器解得出 hubId 与 pairingWindowOpen");
                    pinnedV4.Add("CONTRACT:cert.admin.ping");
                    foreach (var drop in LocalAI.Identity.WireContracts.AdminPing)
                        Assert(!Services.HubAdmin.ParsePing(E(Obj(
                                   LocalAI.Identity.WireContracts.AdminPing.Where(x => x != drop).ToArray()))).ok,
                               $"★★ 反向 cert.admin.ping:少了 `{drop}` ⇒ 判失败,不拼半份出来");

                    // ── CONTRACT:cert.admin.ping.servercert(V1 已钉解析,这里补契约号)──
                    Assert(Services.HubAdmin.ParseServerCert(E(
                               "{\"serverCert\":" + Obj(LocalAI.Identity.WireContracts.AdminPingServerCert,
                                   ("daysLeft", "3.5"), ("consecutiveFailures", "2"), ("needsAttention", "true")) + "}")) is not null,
                           "★★★ CONTRACT:cert.admin.ping.servercert 客户端半边:登记表生成的子对象解得出");
                    pinnedV4.Add("CONTRACT:cert.admin.ping.servercert");

                    // ── CONTRACT:cert.admin.devices(+ .item)──────────────────
                    var devJson = Obj(LocalAI.Identity.WireContracts.AdminDevices,
                        ("devices", "[" + Obj(LocalAI.Identity.WireContracts.AdminDevicesItem,
                                              ("deviceId", "\"dev-1\""), ("displayName", "\"PC-A\""), ("status", "\"active\"")) + "]"));
                    var dvV4 = Services.HubAdmin.ParseDevices(E(devJson));
                    Assert(dvV4.ok && dvV4.list.Count == 1 && dvV4.list[0].DeviceId == "dev-1"
                           && dvV4.list[0].DisplayName == "PC-A" && dvV4.list[0].CertShort == "ab12cd34",
                           "★★★ CONTRACT:cert.admin.devices 客户端半边:顶层 + 元素两层都解得出目标字段(" + (dvV4.why ?? "ok") + ")");
                    pinnedV4.Add("CONTRACT:cert.admin.devices");
                    // ★★ 元素那一层才是承重的 —— A1 的病灶就是"字段藏在下一层"
                    foreach (var drop in LocalAI.Identity.WireContracts.AdminDevicesItem)
                        Assert(!Services.HubAdmin.ParseDevices(E(Obj(LocalAI.Identity.WireContracts.AdminDevices,
                                   ("devices", "[" + Obj(LocalAI.Identity.WireContracts.AdminDevicesItem
                                                         .Where(x => x != drop).ToArray()) + "]")))).ok,
                               $"★★ 反向 cert.admin.devices.item:元素少了 `{drop}` ⇒ 整条判失败,不拼一台没名字的设备出来");
                    pinnedV4.Add("CONTRACT:cert.admin.devices.item");
                    // ★★★ 缺陷验收:HubClient.ParseDevices 现在**委派**给同一处 ——
                    //   形状不认识时它必须**抛**,而不是返回空表(空表会被界面写成「没有别的设备」)
                    var threwV4 = false;
                    try { Services.HubClient.ParseDevices("{\"devices\":[{\"deviceId\":\"x\"}]}"); }
                    catch (FormatException) { threwV4 = true; }
                    Assert(threwV4,
                           "★★★ 缺陷已收:HubClient.ParseDevices 与 HubAdmin 共用同一处解析 —— "
                           + "认不出的形状**抛**,不返回空表(空表 = 一句看起来很有信息量的假答案)");
                    Assert(Services.HubClient.ParseDevices(devJson) is { Count: 1 } hdV4 && hdV4[0].DeviceId == "dev-1",
                           "★★ 而合法形状照常解得出(反向:上面那条不是恒抛)");

                    // ── CONTRACT:cert.admin.pending(+ .item)──────────────────
                    var pendJson = Obj(LocalAI.Identity.WireContracts.AdminPending,
                        ("pending", "[" + Obj(LocalAI.Identity.WireContracts.AdminPendingItem,
                                              ("requestId", "\"req-1\"")) + "]"));
                    var pd = Services.HubAdmin.ParsePending(E(pendJson));
                    Assert(pd.ok && pd.list.Count == 1 && pd.list[0].RequestId == "req-1"
                           && pd.list[0].Sas.Length == 6 && pd.list[0].SecondsLeft == 180 && pd.windowOpen,
                           "★★★ CONTRACT:cert.admin.pending 客户端半边:六个词与倒计时都解得出(" + (pd.why ?? "ok") + ")");
                    pinnedV4.Add("CONTRACT:cert.admin.pending");
                    foreach (var drop in LocalAI.Identity.WireContracts.AdminPendingItem)
                        Assert(!Services.HubAdmin.ParsePending(E(Obj(LocalAI.Identity.WireContracts.AdminPending,
                                   ("pending", "[" + Obj(LocalAI.Identity.WireContracts.AdminPendingItem
                                                         .Where(x => x != drop).ToArray()) + "]")))).ok,
                               $"★★ 反向 cert.admin.pending.item:元素少了 `{drop}` ⇒ 判失败 —— "
                               + "`sas` 漂掉会让界面显示**空的六个词**,而人会以为还没生成");
                    pinnedV4.Add("CONTRACT:cert.admin.pending.item");

                    // ── CONTRACT:cert.admin.revoke ────────────────────────────
                    var rvV4 = Services.HubAdmin.ParseRevoke(E(Obj(LocalAI.Identity.WireContracts.AdminRevoke)));
                    Assert(rvV4.ok && rvV4.generation == 7,
                           "★★★ CONTRACT:cert.admin.revoke 客户端半边:generation 解得出(它是吊销真落盘的凭据)");
                    pinnedV4.Add("CONTRACT:cert.admin.revoke");
                    Assert(!Services.HubAdmin.ParseRevoke(E("{\"ok\":false,\"generation\":7}")).ok,
                           "★★ 反向:ok=false ⇒ 判失败(一次失败的吊销不许和成功的长得一样)");
                    Assert(!Services.HubAdmin.ParseRevokeBody("not json").ok,
                           "★★ 反向:正文不是 JSON ⇒ 判失败,不当成功");

                    // ── CONTRACT:cert.admin.approve / deny / 409 ──────────────
                    Assert(Services.HubAdmin.ParseAck(E(Obj(LocalAI.Identity.WireContracts.AdminApprove))).ok,
                           "★★★ CONTRACT:cert.admin.approve 客户端半边:200 形状解得出");
                    pinnedV4.Add("CONTRACT:cert.admin.approve");
                    Assert(Services.HubAdmin.ParseAck(E(Obj(LocalAI.Identity.WireContracts.AdminDeny))).ok,
                           "★★★ CONTRACT:cert.admin.deny 客户端半边:200 形状解得出");
                    pinnedV4.Add("CONTRACT:cert.admin.deny");
                    var ack409 = Services.HubAdmin.ParseAck(E(Obj(LocalAI.Identity.WireContracts.AdminApproveDeny409,
                                                                 ("ok", "false"), ("error", "\"这条请求已经不是 pending 了\""))));
                    Assert(!ack409.ok && ack409.error is { Length: > 0 } && ack409.why is null,
                           "★★★ CONTRACT:cert.admin.approvedeny.409 客户端半边:**失败分支**的原因解得出 —— "
                           + "只看状态码的话界面只能写「中枢拒绝了」,人会去重启一个没病的中枢");
                    pinnedV4.Add("CONTRACT:cert.admin.approvedeny.409");
                    Assert(Services.HubAdmin.ParseAck(E("{\"okay\":true}")).why is not null,
                           "★★ 反向:两组键都不是 ⇒ 如实报读不懂,不猜");

                    // ── CONTRACT:cert.admin.window ────────────────────────────
                    var wnV4 = Services.HubAdmin.ParseWindow(E(Obj(LocalAI.Identity.WireContracts.AdminWindow)));
                    Assert(wnV4.ok && wnV4.windowOpen,
                           "★★★ CONTRACT:cert.admin.window 客户端半边:中枢自报的窗口状态解得出 —— "
                           + "读不出就只能拿本地布尔替中枢记,而那是本文件明令禁止的");
                    pinnedV4.Add("CONTRACT:cert.admin.window");
                    Assert(!Services.HubAdmin.ParseWindow(E("{\"ok\":true}")).ok,
                           "★★ 反向:少了 pairingWindowOpen ⇒ 判失败(否则会静默退回本地布尔)");

                    // ── 元断言:登记表里**归本处**的那些,一条都不许漏 ──────────
                    //   遍历源是登记表本身,不是手写名单 —— 表里新增一条 cert.admin.* 契约
                    //   而这儿没补断言,下面这条当场红(ASSERTION-PITFALLS 第 3b 条)。
                    var adminCids = LocalAI.Identity.WireContracts.All
                        .Where(c => c.Cid.StartsWith("CONTRACT:cert.admin.", StringComparison.Ordinal))
                        .Select(c => c.Cid).ToArray();
                    var missV4 = adminCids.Except(pinnedV4).ToArray();
                    Assert(missV4.Length == 0,
                           "★★★ 元断言:登记表里每一条 cert.admin.* 契约都要有客户端半边 —— 缺:["
                           + string.Join(", ", missV4) + "]");
                    Assert(pinnedV4.Count == adminCids.Length && adminCids.Length > 0,
                           $"★★ 元断言两个方向:钉了 {pinnedV4.Count} 条 / 登记 {adminCids.Length} 条(零命中也判红)");
                }
                // ▲▲▲ V4 追加段到此为止 ▲▲▲

                // ---- Open WebUI 退役(判据项)----
                var stack = TryReadSource(Path.Combine("..", "..", "90-ops", "start-stack.ps1"));
                if (stack is not null)
                    Assert(!stack.Contains("Open WebUI  http://"),
                           "★★ Open WebUI 已退役:栈不再把它当日常入口打印出来(留着入口 = 文档说退役、界面还在推)");

                // ---- 可分发产物(验收第一句「同一个安装包」)----
                var pack = TryReadSource(Path.Combine("..", "..", "90-ops", "build-client.ps1"));
                if (pack is not null)
                {
                    Assert(pack.Contains("SHA256") && pack.Contains("VERSION.txt") && pack.Contains("安装说明.txt"),
                           "★ 出包 = exe + 校验和 + 版本戳 + 一页说明(缺一样就说不清这份包是什么)");
                    // ★ 门禁认【退出码】:客户端是 WinExe,自检靠 AttachConsole 写到调用者的控制台,
                    //   PowerShell 的管道接不到 —— 拿 stdout 判会得到空字符串(实测踩过)。
                    Assert(pack.Contains("") && pack.Contains("exit 1"),
                           "★★ 自检没过就不出包(认退出码)—— 红着还打包等于把已知坏的东西送到另一台机器上");
                    Assert(pack.Contains("dirty"),
                           "★ 工作树不干净要写进版本戳,别让人拿到一个说不清来源的包");
                }
            }

            var mwSys = TryReadSource("MainWindow.xaml.cs");
            if (mwSys is not null)
            {
                var nav = Slice(mwSys, "public void Navigate(string key, bool fromNavBar = false)", "HighlightNav(key);");
                Assert(nav is not null && nav.Contains("if (IsSystemPage(key))") && nav.Contains("OpenSystemPage(key);"),
                       "★ 进系统页 = 盖上来,不替换底下的工作页(否则回来时会话/滚动/草稿全没了)");
                Assert(mwSys.Contains("TheApp.Hub.State == HubState.Online") && mwSys.Contains("ExpectedOutputRate is { } r")
                       && mwSys.Contains("tok/s"),
                       "★ 连上中枢后顶栏改显预期 token 输出速率;未接时待接入(不编数字)");
                Assert(mwSys.Contains("TheApp.Hub.Changed +="),
                       "★ 中枢状态一变就刷顶栏(启动探测连上也能及时改显)");
                Assert(Services.TokenUsage.ExpectedOutputRate is null,
                       "★ 预期速率现在恒为 null(模型 P4 未接,不编数字)");
                Assert(mwSys.Contains("static bool IsSystemPage(string key) => key is \"settings\" or \"model\" or \"extensions\";"),
                       "★ 键名照抄导航注册处 —— 模型那页是 \"model\" 不是 \"models\"(写错就绕过覆盖层、照旧拆重建)");
                Assert(mwSys.Contains("ContentHost.IsEnabled = false;") && mwSys.Contains("ContentHost.IsEnabled = true;"),
                       "★ 盖上时停用底下那页 —— 否则 Tab 会聚焦到被盖住的输入框,打字打进看不见的地方");
                Assert(mwSys.Contains("_systemKey is not null) { CloseSystemPage(); ke.Handled = true; }"),
                       "★ Esc 就是那个返回箭头(排在浮层/菜单之后,不一下退两层)");
                Assert(mwSys.Contains("if (AnyDropDownOpen(this)) return;"),
                       "★ 下拉框开着时,点选项不会把整个抽屉关掉(它的弹出层是独立窗口,IsInsideDrawer 认不出来)");
                Assert(mwSys.Contains("if (Overlay.IsOpen) { FocusPolicy.Park(this, FocusPark); return; }"),
                       "★ 抽屉/浮窗开着时 Tab 不把焦点交给被遮罩盖住的输入框");
                var lang2 = Slice(mwSys, "void OnLanguageChanged()", "BuildChromeIcons();");
                Assert(lang2 is not null && lang2.IndexOf("CloseSystemPage();", StringComparison.Ordinal)
                       < lang2.IndexOf("Navigate(current);", StringComparison.Ordinal),
                       "★ 换语言时先收起覆盖层再 Navigate —— 否则那条守卫会把重建吃掉,底下那页永远停在旧语言");
                Assert(mwSys.Contains("public void CloseSystemPage()") && mwSys.Contains("BackChevron("),
                       "★ 左上角有返回箭头,点它回到底下那一页 —— 那一页一直活着,不用重建");
                // ---- 覆盖式导航的回归防线(2026-07-31 审计确认的几条) ----
                Assert(mwSys.Contains("string ActiveKey => _systemKey ?? _currentKey;") && mwSys.Contains("HighlightNav(ActiveKey);"),
                       "★ 左栏高亮认【眼前这一页】—— 否则在扩展页里勾一下,高亮就跳到底下那张看不见的页上去了");
                var lang = Slice(mwSys, "void OnLanguageChanged()", "BuildChromeIcons();");
                Assert(lang is not null && lang.Contains("var sys = _systemKey;") && lang.Contains("OpenSystemPage(sys);"),
                       "★ 在设置里换语言不会把人踢回工作页 —— 换语言那一下正是在设置页里发生的");
                Assert(mwSys.Contains("if (_systemKey is not null && key == _currentKey && ContentHost.Content is not null)"),
                       "★ 覆盖层盖着时点左栏里底下那一页自己 = 收起回去,不是拆了重建(否则两条路两种结果)");
                Assert(mwSys.Contains("static bool AnyDropDownOpen(DependencyObject? root)"),
                       "下拉框开着时 Esc 只收下拉(实地查验,不靠标志位)");
                Assert(mwSys.Contains("PreviewMouseUp += (_, me) => { if (_swallowUp)"),
                       "★ “浮层开着时第一次点击只关浮层”连【松开】也吞 —— Chip/返回都挂在 MouseLeftButtonUp 上,只吞按下等于没拦");
                Assert(mwSys.Contains("CalendarButton.Visibility = Visibility.Visible;"),
                       "★ 系统页盖住主页时,顶栏日历按钮要露出来(不然日历彻底没入口)");
                Assert(mwSys.Contains("SettingsInOverlay()?.RevealAudioDriver()"),
                       "从别处跳到某块设置时,也从覆盖层里取设置页");
                Assert(!Body(mwSys).Contains("(ContentHost.Content as SettingsView)"),
                       "不再从工作页宿主里找设置页(它已经不在那儿了)");
            }
            var mwSysXaml = TryReadSource("MainWindow.xaml");
            if (mwSysXaml is not null)
            {
                // ★ 覆盖层必须是 Border:ContentControl 的默认模板【不画 Background】,
                //   写了也白写 —— 第一版就是这么写的,结果底下的工作页整个透出来、点击也穿透。
                Assert(mwSysXaml.Contains("<Border x:Name=\"SystemPageLayer\"") && mwSysXaml.Contains("Background=\"{DynamicResource BgWindow}\""),
                       "★ 覆盖层是【Border】而不是 ContentControl —— 后者根本不画底色,也拦不住点击");
                Assert(!mwSysXaml.Contains("<ContentControl x:Name=\"SystemPageHost\" Visibility=\"Collapsed\""),
                       "旧的透明覆盖层已撤掉");
            }

            // ---- Tab 圈:三个纯函数的【行为】断言(复核 2026-08-03:此前只有源码文本 grep)----
            // ★ 它们的注释各自写着"抽成纯函数,好让无头自检直接验顺序" —— 那就真的验。
            //   下面每一条都对应一种改坏的方式:去掉排序 / 去掉整枝 / 把 Shift 忽略掉。
            {
                static System.Windows.Controls.TextBox Box(int order)
                {
                    var t = new System.Windows.Controls.TextBox();
                    Views.FocusPolicy.SetTabOrder(t, order);
                    return t;
                }
                // 树序【故意反着排】:设置条(20+)先进树、会话卡(10+)后进树 —— 与回信页真实结构同型
                var bar = new System.Windows.Controls.StackPanel();
                var b20 = Box(20); var b21 = Box(21);
                bar.Children.Add(b20); bar.Children.Add(b21);
                var card = new System.Windows.Controls.StackPanel();
                var b10 = Box(10); var b11 = Box(11);
                card.Children.Add(b10); card.Children.Add(b11);
                var hiddenHost = new System.Windows.Controls.StackPanel { Visibility = System.Windows.Visibility.Collapsed };
                var bHidden = Box(12); hiddenHost.Children.Add(bHidden);
                var offHost = new System.Windows.Controls.StackPanel { IsEnabled = false };
                var bOff = Box(13); offHost.Children.Add(bOff);
                var root = new System.Windows.Controls.StackPanel();
                root.Children.Add(bar); root.Children.Add(card);
                root.Children.Add(hiddenHost); root.Children.Add(offHost);
                root.Measure(new System.Windows.Size(400, 400));
                root.Arrange(new System.Windows.Rect(0, 0, 400, 400));

                var ring = Views.FocusPolicy.Ring(root);
                Assert(ring.Count == 4, $"★ 圈里只收【登记过】的输入框(实得 {ring.Count} 个)");
                Assert(ring.Count == 4 && ReferenceEquals(ring[0], b10) && ReferenceEquals(ring[1], b11)
                       && ReferenceEquals(ring[2], b20) && ReferenceEquals(ring[3], b21),
                       "★★ 顺序按 TabOrder 而不是树序 —— 回信页设置条在树里排在会话卡【前面】,"
                       + "靠树序走出来的 Tab 是自下而上的");
                Assert(!ring.Contains(bHidden), "★ 声明为 Collapsed 的分支整枝跳过(藏起来的框不该被 Tab 到)");
                Assert(!ring.Contains(bOff), "★ IsEnabled=false 的分支整枝跳过(生成中被禁用的框不该被 Tab 到)");

                Assert(ReferenceEquals(Views.FocusPolicy.Next(ring, b10, back: false), b11), "Tab 往后一个");
                Assert(ReferenceEquals(Views.FocusPolicy.Next(ring, b21, back: false), b10), "★ 走到末尾绕回第一个");
                Assert(ReferenceEquals(Views.FocusPolicy.Next(ring, b11, back: true), b10), "★★ Shift+Tab 往【回】转(忽略 back 的话这条会红)");
                Assert(ReferenceEquals(Views.FocusPolicy.Next(ring, b10, back: true), b21), "★ 往回走到头绕到最后一个");
                Assert(ReferenceEquals(Views.FocusPolicy.Next(ring, null, back: false), b10), "焦点不在圈里 -> 落到第一个");
                Assert(Views.FocusPolicy.Next(new List<System.Windows.FrameworkElement>(), b10, back: false) is null, "空圈 -> null");

                // IsInsideInput:按【类型白名单】上溯,ComboBox/PasswordBox 都得认
                Assert(Views.FocusPolicy.IsInsideInput(b10), "输入框自己算在输入里");
                var inner = new System.Windows.Controls.Border();
                var cb = new System.Windows.Controls.ComboBox();
                Assert(Views.FocusPolicy.IsInsideInput(cb), "★ ComboBox 算输入(否则点开下拉就丢焦点)");
                Assert(Views.FocusPolicy.IsInsideInput(new System.Windows.Controls.PasswordBox()),
                       "★ PasswordBox 不是 TextBoxBase 的子类,得单列");
                Assert(!Views.FocusPolicy.IsInsideInput(inner), "★ 纯板块容器不算输入 —— 按 Focusable 判会把它们全误判(那正是被否掉的打地鼠路线)");

                // KeepsKeyboardFocus:声明了的那一块,点别处不许把它的焦点停走
                var keeper = new System.Windows.Controls.Border();
                Views.FocusPolicy.SetKeepsKeyboardFocus(keeper, true);
                Assert(Views.FocusPolicy.GetKeepsKeyboardFocus(keeper) && !Views.FocusPolicy.GetKeepsKeyboardFocus(inner),
                       "★ 收快捷键的那一块认得出来(文件翻译的 Del / Ctrl+Z 靠它保住焦点)");
            }

            var mwTab = TryReadSource("MainWindow.xaml.cs");
            if (mwTab is not null)
            {
                Assert(mwTab.Contains("ke.Key != Key.Tab") && mwTab.Contains("FocusPolicy.HandleTab(this, FocusPark"),
                       "★ Tab 由窗口统一接管(不靠逐个控件设 IsTabStop)");
                // ★ Shift+Tab 往回转(2026-08-03:圈里有好几个输入框之后,只能往前转就成了单行道)
                Assert(mwTab.Contains("back: (Keyboard.Modifiers & ModifierKeys.Shift) != 0"),
                       "★ Shift+Tab 在输入框圈里往回转");
                // ★★ 点【输入框以外】= 取消聚焦(用户裁定 2026-08-03),且必须给浮窗放行 ——
                //   回信页的「自定义问候」就是浮窗里的一个输入框,把焦点拽回主窗口会让浮窗整个关掉。
                var mwPark = Slice(mwTab, "PreviewMouseUp += (_, me) =>", "StateChanged +=");
                Assert(mwPark is not null && mwPark.Contains("FocusPolicy.IsInsideInput(d)") && mwPark.Contains("Flyout.IsInside(d)")
                       && mwPark.Contains("FocusPolicy.Park(this, FocusPark)"),
                       "★ 点输入框以外的地方取消聚焦;浮窗内部放行(否则浮窗里的输入框会被清掉)");
                // ★ 先确认两串都【在】,再比顺序:IndexOf 找不到返回 -1,而 -1 恒小于任何下标 ——
                //   照着写"a < b"的话,把守卫整行删掉断言反而变绿(复核 2026-08-03 抓到)。
                var iDrop = mwPark?.IndexOf("AnyDropDownOpen(this)", StringComparison.Ordinal) ?? -1;
                var iPark = mwPark?.IndexOf("FocusPolicy.Park(this, FocusPark)", StringComparison.Ordinal) ?? -1;
                Assert(iDrop >= 0 && iPark >= 0 && iDrop < iPark,
                       "★ 下拉开着时不停焦点 —— 选项在独立 Popup 里,停一下就把下拉关了(删自定义的 × 当场失效)");
                Assert(mwPark is not null && mwPark.Contains("FocusPolicy.FocusedKeepsFocus()"),
                       "★★ 自己拿着焦点收快捷键的那一块(文件翻译的 Del / Ctrl+Z)不许被点别处顺手停掉");
            }
            var cvMark = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvMark is not null)
                Assert(cvMark.Contains("FocusPolicy.SetIsChatInput(_input, true);"),
                       "★ AI 交流输入框被显式标记(输入区每次重建都是新对象,标记跟着控件走)");

            // 主窗口那三个标题栏按钮走 keyed 样式 —— 隐式样式对它们【整条不生效】,必须各补一遍
            var mwCap = TryReadSource("MainWindow.xaml");
            if (mwCap is not null)
            {
                var cap = Slice(mwCap, "x:Key=\"CaptionButton\"", "</Style>");
                Assert(cap is not null && cap.Contains("\"Focusable\" Value=\"False\"") && cap.Contains("\"IsTabStop\" Value=\"False\""),
                       "★ 标题栏按钮也退出焦点体系(带显式 Style 的元素不吃隐式样式)");
                // ★ 抽屉不再需要 TabNavigation=Cycle:Tab 已由窗口接管、只认 AI 输入框一个落点,
                //   WPF 自己的 Tab 导航根本不会跑起来。留着是死设定,反而误导后来人。
                Assert(!mwCap.Contains("KeyboardNavigation.TabNavigation"),
                       "抽屉不留失效的 Tab 导航设定(Tab 由 FocusPolicy 统一接管)");
            }
            var mwFocus = TryReadSource("MainWindow.xaml.cs");
            if (mwFocus is not null)
            {
                Assert(mwFocus.Contains("KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.None)"),
                       "★ 方向键不移动焦点(走导航层)");
                // ★★ 最高危陷阱:方向键【绝不能】在窗口的隧道事件里吞掉 ——
                //   主窗口先于输入框收到按键,一吞就把输入框的光标移动/跨行/Home/End 全废了。
                var pkd = Slice(mwFocus, "PreviewKeyDown +=", "PreviewMouseDown");
                Assert(pkd is not null && !pkd.Contains("Key.Left") && !pkd.Contains("Key.Right")
                       && !pkd.Contains("Key.Up") && !pkd.Contains("Key.Down"),
                       "★ 窗口隧道层【不许】吞方向键(吞了输入框就废了)");
            }
            var cvFocus = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvFocus is not null)
            {
                Assert(cvFocus.Contains("if (_input.IsLoaded) _input.Focus();"),
                       "★ 没有输入框的页面不聚焦任何东西(不对从未进树的控件 Focus)");
                // ★ 2026-08-03 起多了 || _justSent:"点空白取消聚焦"是隧道+按下,发送键挂在松开上 ——
                //   焦点先被停走,再嗅探就恒为 false,"发完第一条要重新点输入框"那个老 bug 会复发。
                Assert(cvFocus.Contains("var refocus = _input.IsKeyboardFocusWithin || _justSent;")
                       && cvFocus.Contains("_justSent = true;"),
                       "★ 会话区重建后把焦点还给输入框:鼠标发送靠【记下的意图】,不靠嗅探当前焦点");
                var ren = Slice(cvFocus, "void RenameSession(", "Flyout.Show(");
                Assert(ren is not null && ren.Contains("Key.Escape"),
                       "★ 重命名浮窗自己收 Esc(主窗口那条总闸够不到独立 Popup)");
            }
            var cdSrc = TryReadSource(Path.Combine("Views", "ConfirmDialog.cs"));
            if (cdSrc is not null)
            {
                var kd = Slice(cdSrc, "win.KeyDown +=", "win.ShowDialog");
                Assert(kd is not null && kd.Contains("Key.Escape") && !kd.Contains("Key.Enter"),
                       "★ 确认框只认 Esc 取消,回车【不】确认(回车只触发发送)");
            }
            var mvSrc = TryReadSource(Path.Combine("Views", "ModelsView.cs"));
            if (mvSrc is not null)
                Assert(mvSrc.Contains("path.LostFocus += (_, _) => Commit();") && mvSrc.Contains("Unloaded += (_, _) => Commit();"),
                       "★ 模型路径不只靠失焦提交(焦点收窄后这页只剩它一个可聚焦控件)");

            // ---- 折叠状态的键必须【稳定】:加载更早的消息不能让展开态跑到别人身上 ----
            // ★ 这条只能用行为断言:结构断言看不出"下标 vs 稳定标识"会不会在归档后错位。
            {
                var kdir = Path.Combine(tmp, "bubblekey");
                Directory.CreateDirectory(kdir);
                Environment.SetEnvironmentVariable(AppPaths.StateEnvVar, kdir);
                var kc = new Services.ChatCenter();
                var sid = kc.NewSession(null, "chat").SessionId;
                for (int i = 0; i < 12; i++) kc.Send(sid, "第 " + i + " 条");
                var before = kc.MessagesOf(sid).ToList();
                Assert(before.Count == 24, "12 次发送 = 24 条(每次连带一条系统说明)");

                // 同一次 Send 的两条:时间戳可能一模一样,键仍必须不同
                Assert(before[0].StableKey != before[1].StableKey,
                       "★ 同一次发送的两条消息键不相等(只用时间戳会撞)");

                var target = before[^3];
                var keyBefore = target.StableKey;
                kc.ArchiveOldMessages(keepRecent: 6);
                Assert(kc.UnloadedArchivedCount(sid) > 0, "确实归档了一部分");
                kc.LoadArchived(sid);
                var after = kc.MessagesOf(sid).ToList();
                var same = after.First(m => ReferenceEquals(m, target) || m.StableKey == keyBefore);
                Assert(same.Text == target.Text, "★ 加载更早之后,同一条消息的键没变(展开态不会跑到别人身上)");
                Assert(after.Select(m => m.StableKey).Distinct().Count() == after.Count,
                       "★ 全会话的键两两不同");

                // 归档【不分工作空间】—— 翻译空间的会话同样会被归档,所以入口也必须对所有空间都在
                var tsid = kc.NewSession(null, "translation").SessionId;
                for (int i = 0; i < 12; i++) kc.Send(tsid, "翻译 " + i);
                kc.ArchiveOldMessages(keepRecent: 6);
                Assert(kc.UnloadedArchivedCount(tsid) > 0,
                       "★ 翻译空间的会话一样会被归档(所以「加载更早」不能只长在聊天分支上)");
                Environment.SetEnvironmentVariable(AppPaths.StateEnvVar, tmp);
            }
            var cvFill = TryReadSource(Path.Combine("Views", "ChatView.cs"));
            if (cvFill is not null)
            {
                // 三份铺消息合成一份:Bubble( 只应出现在 FillMessages 与它自己的定义里
                var bubbleCalls = System.Text.RegularExpressions.Regex.Matches(
                    Body(cvFill), @"(?<![A-Za-z])Bubble\(").Count;
                Assert(bubbleCalls == 2, $"★ 只剩一处铺消息(FillMessages)+ 一处定义,实得 {bubbleCalls}");
                var loadMore = System.Text.RegularExpressions.Regex.Matches(Body(cvFill), "加载更早的").Count;
                Assert(loadMore == 1, $"★ 「加载更早」入口只有一处、在共享层(实得 {loadMore})");
                var cards = System.Text.RegularExpressions.Regex.Matches(Body(cvFill), @"Ui\.Card\(").Count;
                Assert(cards == 2, $"★ 卡片配方唯一(会话列表 1 处 + ConvCard 内 1 处,实得 {cards})");
                var ro = Slice(cvFill, "FrameworkElement BuildReadonlyProject()", "var banner");
                Assert(ro is not null && ro.Contains("animate: false") && ro.Contains("ScrollToEnd"),
                       "★ 只读浏览:有归档入口、会滚到底、不播动画");
                Assert(cvFill.Contains("static string BubbleKey(ChatMessage m) => m.StableKey;"),
                       "★ 折叠键走稳定标识,不再用下标");
            }

            // ---- 皮肤纪律(用户裁定 2026-07-30):微风(苹果风)是【标准默认皮肤】----
            // ★ 所有主要设计都对着微风做;其它皮肤原则上只靠【换色 + 微调】实现。
            //   理由是用户的原话:"每个皮肤改一点会导致混乱"——
            //   一旦结构按皮肤分叉,任何改动都要在三套里各验一遍,而且必然有一套被忘掉。
            Assert(new Services.AppSettings().Skin == Services.Skin.Breeze, "★ 默认皮肤 = 微风(苹果风)");
            {
                // ★ 皮肤的七个基色是【契约】:少一个,换肤时就会有地方拿不到颜色。
                //   其中两条分工最容易糊,所以单独钉死:
                //   · 着重色 = 可以点的/被选中的(交互);主题色 = 这是哪套皮肤(身份);
                //   · 着重色反色 = 指出位置(与着重色对立,才不会和选中态混淆)。
                var calSec = TryReadSource(Path.Combine("Views", "CalendarView.cs"));
                if (calSec is not null)
                    Assert(calSec.Contains("Border.BackgroundProperty, \"AccentSecondary\""),
                           "★ 次着重色有真活:跨天全天的线与当日日程的点分色");
                var mwBrand = TryReadSource("MainWindow.xaml");
                if (mwBrand is not null)
                    // 品牌块已整体移除(用户裁定 2026-08-03):任务栏/托盘已有图标与名字,窗口里不再重复
                Assert(!mwBrand.Contains("本地 AI 中枢\" FontWeight"), "★ 窗口左上不再放标题与图标块");
            }
            {
                // 结构性的按皮肤分叉必须【屈指可数且登记在案】。新增一处就会让这条挂掉,
                // 逼着人回来确认"这真的必须按皮肤分结构吗,还是换个颜色令牌就够了"。
                var forks = new List<string>();
                var viewsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Views");
                if (Directory.Exists(viewsDir))
                {
                    foreach (var f in Directory.GetFiles(viewsDir, "*.cs"))
                        foreach (var line in File.ReadAllLines(f))
                        {
                            var t = line.TrimStart();
                            if (t.StartsWith("//")) continue;                       // 注释不算(踩过三次)
                            if (t.Contains("ThemeManager.Current =")) forks.Add(Path.GetFileName(f));
                        }
                    var distinct = forks.Distinct().OrderBy(x => x).ToList();
                    // ★ 收紧到【零】:暖萌的堆叠卡片砍掉之后,Views 下不该再有按皮肤分结构的地方。
                    //   皮肤差异一律走颜色令牌(八个基色),结构三套一致 —— 这正是"每个皮肤改一点会
                    //   导致混乱"的解法:改一次,三套同时对。
                    Assert(distinct.Count == 0,
                           "★ 按皮肤分【结构】的地方一处都不该有(皮肤差异一律走颜色令牌)"
                           + (distinct.Count > 0 ? " 现有:" + string.Join(",", distinct) : ""));
                }
            }

            // ---- 皮肤令牌齐备:三个皮肤必须定义同一组键,否则换肤会崩在缺键上 ----
            var need = new[] { "BgWindow", "BgSurface", "BgNav", "BgHover", "BgSelected", "FgPrimary",
                               "FgSecondary", "FgMuted", "FgOnAccent", "Accent", "AccentHover", "Border",
                               "BorderStrong", "FocusRing", "BgSunken", "FgOnSelected", "AccentInverse", "ThemeColor", "AccentSecondary",
                               "RadiusSm", "RadiusMd", "RadiusLg" };
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

            // ================= 桌宠(P8 前置 · v1a 哑巴猫) =================
            // 帧资产尚未交付,故这里断言的全是**不依赖任何一张图**的东西:时钟、转移图、意图通道。
            // 渲染正确性等 A 批身份锁定帧到位后另立断言。

            // ---- 6 fps 固定步长时钟(动画规范 §5)----
            {
                Assert(Math.Abs(Services.Pet.PetClock.TickMs - 1000.0 / 6) < 1e-9,
                    "tick 时长由 fps 导出(1000/6),不写死 166");

                var clk = new Services.Pet.PetClock(); clk.Reset(0);
                Assert(clk.Advance(100) == 0, "不足一个 tick 不推进");
                Assert(clk.Advance(200) == 1, "跨过 166.67ms 推进 1 tick");

                var drift = new Services.Pet.PetClock(); drift.Reset(0);
                int total = 0;
                for (int i = 1; i <= 60; i++) total += drift.Advance(i * 100);
                Assert(total == 36, $"accumulator 不漂移:6 秒恰好 36 tick(实得 {total})");

                var stall = new Services.Pet.PetClock(); stall.Reset(0);
                var burst = stall.Advance(10_000);
                Assert(burst == Services.Pet.PetClock.MaxCatchUpTicks && stall.Resyncs == 1,
                    "长卡顿最多追赶 2 tick 后重同步(规范 §5:禁止快速补播一串动作)");

                var rollback = new Services.Pet.PetClock(); rollback.Reset(5000);
                Assert(rollback.Advance(1000) == 0 && rollback.Resyncs == 1, "时钟回拨不产生负 tick,直接重同步");
            }

            // ---- manifest 校验:全部 fail-closed ----
            {
                static string Mini(string clips, string states, string edges)
                    => "{\"fps\":6,\"clips\":" + clips + ",\"states\":" + states + ",\"edges\":" + edges + ",\"parallel_layers\":[]}";
                const string LoopA = "\"a_loop\":{\"group\":\"idle\",\"track\":\"body\",\"independent_frames\":1,\"ticks\":6,\"duration_ms\":1000.0,\"loop\":true}";
                const string StateA = "{\"suspended\":{},\"a\":{\"loop_clip\":\"a_loop\"}}";
                const string EdgeA = "[{\"from\":\"suspended\",\"to\":\"a\",\"clip\":null,\"must_finish\":false}]";

                var good = Services.Pet.PetManifest.Parse(Mini("{" + LoopA + "}", StateA, EdgeA));
                Assert(good.Validate().Count == 0, "最小合法 manifest 通过校验");

                var orphan = Services.Pet.PetManifest.Parse(Mini(
                    "{" + LoopA + ",\"nobody_plays_me\":{\"group\":\"idle\",\"track\":\"body\",\"independent_frames\":4,\"ticks\":6,\"duration_ms\":1000.0,\"loop\":true}}",
                    StateA, EdgeA));
                Assert(orphan.Validate().Any(e => e.Contains("孤儿")),
                    "孤儿 clip 被判错 —— 这条正是「删了 stalk 却留下 stand_to_stalk」那 8 张废帧的形状");

                var dangling = Services.Pet.PetManifest.Parse(Mini(
                    "{" + LoopA + "}", "{\"suspended\":{},\"a\":{\"loop_clip\":\"does_not_exist\"}}", EdgeA));
                Assert(dangling.Validate().Any(e => e.Contains("不存在")), "状态指向不存在的 clip 被判错");

                var unreachable = Services.Pet.PetManifest.Parse(Mini(
                    "{" + LoopA + "}", "{\"suspended\":{},\"a\":{\"loop_clip\":\"a_loop\"},\"island\":{}}", EdgeA));
                Assert(unreachable.Validate().Any(e => e.Contains("不可达")), "从 suspended 走不到的状态被判错(死支路)");
            }

            // ---- 真 manifest + 动画状态机 ----
            var petJson = TryReadSource(Path.Combine("Assets", "pet", "loading-cow-cat", "loading-cow-cat-animation-manifest-v1.json"));
            if (petJson is null)
                Console.WriteLine("  SKIP  桌宠 manifest(发布环境没有源码目录)");
            else
            {
                var m = Services.Pet.PetManifest.Parse(petJson);
                var errs = m.Validate();
                Assert(errs.Count == 0, "真 manifest 通过全部校验" + (errs.Count > 0 ? " → " + string.Join(" / ", errs.Take(4)) : ""));

                var v1aBody = m.Clips.Values.Where(c => c.V1a && c.Track == "body").Sum(c => c.IndependentFrames);
                var v1aLoad = m.Clips.Values.Where(c => c.V1a && c.Track == "loading").Sum(c => c.IndependentFrames);
                Assert(v1aBody == 84 && v1aLoad == 12, $"v1a 帧预算 = 84 身体 + 12 加载环(实得 {v1aBody}+{v1aLoad})");

                // 寻路:行为层只说"我要去睡",四段过渡由状态机自己解
                var toSleep = m.FindPath("stand", "sleep");
                Assert(toSleep is { Count: 3 }
                       && toSleep[0].Clip == "stand_to_sit" && toSleep[1].Clip == "sit_to_loaf" && toSleep[2].Clip == "loaf_to_sleep",
                    "stand→sleep 自动解出 stand_to_sit → sit_to_loaf → loaf_to_sleep(调用方不拼链)");

                Assert(m.FindPath("suspended", "behind_door") is { Count: 1 } bd && bd[0].Clip is null,
                    "suspended→behind_door 有直达边:猫在门外时休眠恢复不会被拉回家(否则本机凭空多一只猫)");

                Assert(m.TurnEdge("stand")?.Clip == "turn_180", "站姿的转身边是 turn_180");
                Assert(m.TurnEdge("sit") is null, "坐姿没有转身边 —— 要转身必须先起身");

                static void Run(Services.Pet.PetAnimator a, int n) { for (int i = 0; i < n; i++) a.Advance(); }

                // 起手:挂起态醒来
                var an = new Services.Pet.PetAnimator(m);
                Assert(an.State == "suspended" && an.PlayingClip is null, "初始停在 suspended,不播任何 clip");
                an.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Wake, Services.Pet.PetIntentSource.Behavior));
                an.Advance();
                Assert(an.PlayingClip == "wake_from_hidden", "醒来先播 wake_from_hidden");
                Run(an, 12);
                Assert(an.State == "stand" && an.PlayingClip == "idle_stand", "播完落到 stand 并停在 idle_stand");

                // must_finish 不可被普通意图打断
                an.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Rest, Services.Pet.PetIntentSource.Behavior));
                an.Advance();
                Assert(an.PlayingClip == "stand_to_sit", "去坐先播过渡 clip");
                var mid = an.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Idle, Services.Pet.PetIntentSource.Behavior));
                Assert(mid.Outcome == Services.Pet.PetIntentOutcome.Deferred,
                    "过渡播到一半的意图被【推迟】而不是丢弃 —— 返回三态,不做 fire-and-forget");
                Assert(an.PlayingClip == "stand_to_sit", "推迟期间过渡照播,姿态不跳变");

                // 坐着要转身:必须先起身,不许一帧镜像
                Run(an, 24);
                Assert(an.State == "stand", "被推迟的意图在可离开时生效(回到 stand)");
                an.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Rest, Services.Pet.PetIntentSource.Behavior));
                Run(an, 12);
                Assert(an.State == "sit", "落到 sit");
                an.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Face, Services.Pet.PetIntentSource.Behavior, Facing: Services.Pet.PetFacing.Right));
                var sawTurn = false;
                for (int i = 0; i < 40 && an.Facing != Services.Pet.PetFacing.Right; i++)
                { an.Advance(); if (an.PlayingClip == "turn_180") sawTurn = true; }
                Assert(sawTurn && an.Facing == Services.Pet.PetFacing.Right && an.State == "stand",
                    "坐着转身会先起身再走 turn_180;朝向只在转身 clip 播完时才翻");

                // 表演白名单从 insert_clips 推导,不是硬编码
                var badPerform = an.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Perform, Services.Pet.PetIntentSource.Behavior, Clip: "walk"));
                Assert(badPerform.Outcome == Services.Pet.PetIntentOutcome.Rejected, "表演意图不能点名移动 clip");
                var okPerform = an.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Perform, Services.Pet.PetIntentSource.Behavior, Clip: "stretch"));
                Assert(okPerform.IsAccepted, "stretch 在 stand 的 insert_clips 里,可以点名");

                // ---- 助手的把手:结构性约束,不是权限判断 ----
                var a2 = new Services.Pet.PetAnimator(m);
                a2.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Wake, Services.Pet.PetIntentSource.Behavior));
                Run(a2, 14);
                Assert(!Services.Pet.PetIntentPolicy.CanAssistantOpenDoors, "助手在策略上不能开门");
                Assert(a2.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.EnterDoor, Services.Pet.PetIntentSource.Assistant)).Outcome
                       == Services.Pet.PetIntentOutcome.Rejected,
                    "助手不能让猫直接穿门 —— 它只能让猫去挠门,开不开由你定");
                Assert(a2.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.ScratchDoor, Services.Pet.PetIntentSource.Assistant)).IsAccepted,
                    "助手可以让猫去挠门(权限模型变成角色行为)");
                Assert(a2.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Suspend, Services.Pet.PetIntentSource.Assistant)).Outcome
                       == Services.Pet.PetIntentOutcome.Rejected,
                    "Suspend 不在助手白名单里 —— 新增一个 Kind 默认落在拒绝那边");

                var a3 = new Services.Pet.PetAnimator(m);
                a3.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Wake, Services.Pet.PetIntentSource.Behavior));
                Run(a3, 14);
                var rateOk = 0; var rateNo = 0;
                for (int i = 0; i < Services.Pet.PetIntentPolicy.AssistantIntentsPerMinute + 3; i++)
                {
                    var r = a3.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Idle, Services.Pet.PetIntentSource.Assistant));
                    if (r.Outcome == Services.Pet.PetIntentOutcome.Rejected) rateNo++; else rateOk++;
                }
                Assert(rateOk == Services.Pet.PetIntentPolicy.AssistantIntentsPerMinute && rateNo == 3,
                    "助手意图有速率上限(一只每秒换动作的猫本身就是打扰,哪怕一句话没说)");

                // 紧急挂起:绕过 must_finish,且不播退场演出
                var a4 = new Services.Pet.PetAnimator(m);
                a4.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Wake, Services.Pet.PetIntentSource.Behavior));
                a4.Advance();
                Assert(a4.PlayingClip == "wake_from_hidden" && !a4.CanExitNow(), "过渡中途,不可离开");
                a4.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Suspend, Services.Pet.PetIntentSource.Reaction));
                Assert(a4.State == "suspended" && a4.PlayingClip is null,
                    "独占全屏/休眠时立即隐藏,绕过 must_finish 且不播退场演出(规范 §5)");

                // 加载环:独立并行层,但在猫不在场时不渲染
                var a5 = new Services.Pet.PetAnimator(m);
                a5.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Attend, Services.Pet.PetIntentSource.Reaction, On: true, Clip: "job-1"));
                Assert(!a5.LoadingVisible, "suspended 时加载环不渲染");
                a5.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Wake, Services.Pet.PetIntentSource.Behavior));
                Run(a5, 14);
                Assert(a5.LoadingVisible, "猫在场且有任务在跑时加载环点亮");
                a5.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Attend, Services.Pet.PetIntentSource.Reaction, On: true, Clip: "job-2"));
                a5.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Attend, Services.Pet.PetIntentSource.Reaction, On: false, Clip: "job-1"));
                Assert(a5.LoadingVisible, "加载环按任务 id 计数,关掉其中一个不会误灭(不是单个 Boolean)");
                a5.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Attend, Services.Pet.PetIntentSource.Reaction, On: false, Clip: "job-2"));
                Assert(!a5.LoadingVisible, "最后一个任务结束才熄灭");

                // 拖拽:唯一被允许绕过寻路的输入(它是物理的),但仍不打断必须播完的过渡
                var a6 = new Services.Pet.PetAnimator(m);
                a6.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Wake, Services.Pet.PetIntentSource.Behavior));
                Run(a6, 14);
                Assert(a6.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Grab, Services.Pet.PetIntentSource.User)).IsAccepted,
                    "站着时可以被拎起(走 *visible 通配边,不走寻路)");
                Run(a6, 4);
                Assert(a6.State == "dangle", "拎起后进入 dangle");
                Assert(a6.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Drop, Services.Pet.PetIntentSource.User)).IsAccepted, "可以放下");
                Run(a6, 8);
                Assert(a6.State == "stand", "松手必须落地再交还状态机,不凭空回到 idle");

                var a7 = new Services.Pet.PetAnimator(m);
                Assert(a7.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Grab, Services.Pet.PetIntentSource.User)).Outcome
                       == Services.Pet.PetIntentOutcome.Rejected, "猫不在场时拎不到");

                // 意图不排队:latest-wins
                var a8 = new Services.Pet.PetAnimator(m);
                a8.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Wake, Services.Pet.PetIntentSource.Behavior));
                Run(a8, 14);
                a8.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Rest, Services.Pet.PetIntentSource.Behavior));
                a8.Post(new Services.Pet.PetIntent(Services.Pet.PetIntentKind.Sleep, Services.Pet.PetIntentSource.Behavior));
                Run(a8, 40);
                Assert(a8.State == "sleep", "意图不排队,只保留最新一个(不补演一串过时动作)");

                // 每条意图都留痕
                Assert(a8.Audit.Count >= 3 && a8.Audit.All(x => !string.IsNullOrEmpty(x.Result.Reason) || x.Result.IsAccepted),
                    "每条意图的处置都进审计(谁·何时·投了什么·怎么处置的)");

                Assert(new Services.Pet.PetAnimator(m).UsesPlaceholderRootDelta,
                    "帧数据(root_delta/contacts)未交付前,位移用占位常量并如实标注 —— 不伪装成已实现");
            }

            // ============ A1 · 跨语言成对断言(客户端那半边) ============
            //
            // 另一半在 `10-core/gateway/test_gpu_broker.py`(搜「A1:租约发放响应的顶层键集合」),
            // 它对着**真实端点**断言顶层键集合恰好是 {status, lease, fence_token, generation}。
            //
            // ★★★ A 级 6 条里有 4 条是同一个形状:**两边各自都绿,断的是中间那根线**。
            //   服务端那半只证明「我发出去的是这个形状」;这半只证明「这个形状我读得懂」。
            //   单独任何一条都抓不住它 —— 必须成对。
            //
            // ★★ 实际发生的:服务端把 lease_id 放在 `lease` 子对象里,客户端在**顶层**找,
            //   于是 AcquireAsync 恒返回 false;而**中枢那边 grant 是真成功的**
            //   ⇒ 每次尝试都留下一份没人认领的 client_session。
            //   fence_token **恰好**在顶层拿得到,所以只有 lease_id 落空 —— 这就是它没被发现的原因。
            {
                // ★ 照服务端真实形状抄:lease_id 在子对象,fence_token 在顶层。**位置不对称。**
                const string wire =
                    "{\"status\":\"ok\"," +
                    "\"lease\":{\"lease_id\":\"L-7\",\"kind\":\"client_session\",\"holder\":\"PC-A\"," +
                    "\"components\":[],\"granted_at\":1.0,\"expires_at\":91.0,\"held_s\":0.0," +
                    "\"evictable\":false,\"blocking\":\"USER_BLOCKING\",\"exclusive\":false}," +
                    "\"fence_token\":\"F-7\",\"generation\":12}";

                Assert(LeaseKeeper.TryParseGrant(wire, out var lid, out var fen)
                       && lid == "L-7" && fen == "F-7",
                    "★★★ A1:服务端真实形状能解析出 lease_id(它在 lease 子对象里,顶层没有)"
                    + $" —— 实得 lease_id={lid ?? "null"} fence={fen ?? "null"}");

                // ★ 反过来钉。没有这几条,一个「永远返回 true」的解析器也能让上面那条绿。
                Assert(!LeaseKeeper.TryParseGrant(
                           "{\"status\":\"ok\",\"lease\":{\"kind\":\"client_session\"}," +
                           "\"fence_token\":\"F-7\",\"generation\":12}", out var lid2, out _)
                       && lid2 is null,
                    "★★ 反向:哪儿都没有 lease_id ⇒ 判失败(fence_token 拿得到也不算数)");

                Assert(!LeaseKeeper.TryParseGrant(
                           "{\"status\":\"ok\",\"lease\":{\"lease_id\":\"L-7\"},\"generation\":12}",
                           out _, out var fen3) && fen3 is null,
                    "★★ 反向:没有 fence_token ⇒ 判失败 —— 续不了租的租约会在 TTL 后静默消失,"
                    + "那时中枢会以为没人在用");

                Assert(!LeaseKeeper.TryParseGrant("not json at all", out _, out _),
                    "★ 垃圾输入判失败且不抛 —— 一段坏 JSON 不该掀翻整条申请路径");

                var lkSrc = TryReadSource("Services/LeaseKeeper.cs");
                if (lkSrc is not null)
                {
                    var acqRaw = Slice(lkSrc, "async Task<bool> AcquireAsync",
                                       "internal static bool TryParseGrant");
                    var acq = acqRaw is null ? null : Body(acqRaw);   // 去注释,只留会执行的代码
                    Assert(acq is not null && acq.Contains("ReleaseByHolderAsync"),
                        "★★★ A1:解析失败时必须**先把刚拿到的那份租约放掉**再返回 false —— "
                        + "中枢那边 grant 是真成功的,不放就是留下一份没人认领的幽灵;"
                        + "续租每 30 秒试一次 ⇒ 稳态并存约 3 份");
                    Assert(acq is not null && acq.Contains("TryParseGrant("),
                        "★ AcquireAsync 走的是抽出来的那个解析器(自检喂的也是它 —— 否则两条断言测的是两份代码)");
                    Assert(acq is not null && !acq.Contains("TryGetProperty(\"lease_id\""),
                        "★★ AcquireAsync 里不得再留一份手写的 lease_id 解析 —— "
                        + "两份解析会漂移,而漂移的那天自检只盯着其中一份");
                }
            }

            // ══════════════════════════════════════════════════════════
            //  ★★★ D92 硬前置 · 跨进程响应契约:客户端那半边
            //
            //  服务端那半在 `10-core/gateway/test_gpu_broker.py` 的
            //  `CROSS_PROCESS_CONTRACTS` 表里 —— 它对着**真实端点**钉顶层键集合,
            //  并有一条**元断言**去这份文件里找同名契约号,**缺配对即判红**。
            //  ⇒ 下面每个 `CONTRACT:` 标记都不是注释,它是那条元断言的检索目标。
            //    删掉任何一行,服务端那半会当场变红并说清缺的是哪一条。
            // ══════════════════════════════════════════════════════════
            {
                // ── CONTRACT:gpu.lease.grant ──
                //   {status, lease{...}, fence_token, generation} —— 上面那一组断言已经逐条钉过
                //   (lease_id 在子对象 / fence_token 在顶层 / 三条反向)。这里补 holder 那一维:
                //   ★★ 审计 B1:holder 现在是**中枢解析出来的**,客户端不再自报。
                const string grantWire =
                    "{\"status\":\"ok\"," +
                    "\"lease\":{\"lease_id\":\"L-9\",\"kind\":\"client_session\",\"holder\":\"PC-A\"," +
                    "\"components\":[],\"granted_at\":1.0,\"expires_at\":91.0,\"held_s\":0.0," +
                    "\"evictable\":false,\"blocking\":\"USER_BLOCKING\",\"exclusive\":false}," +
                    "\"fence_token\":\"F-9\",\"generation\":12}";
                Assert(LeaseKeeper.TryParseHolder(grantWire) == "PC-A",
                    "★★★ CONTRACT:gpu.lease.grant —— 拿服务端真实形状能读出**中枢认定**的 holder");
                var lkSrc2 = TryReadSource("Services/LeaseKeeper.cs");
                if (lkSrc2 is not null)
                {
                    var lkCode = CodeOnly(lkSrc2);
                    Assert(!lkCode.Contains("holder = Environment.MachineName"),
                        "★★★ 审计 B1:客户端**不再自报 holder** —— 那个值是中枢限流桶的 key,"
                        + "而且会被印进「正在跑:xxx」对话框:自报等于占用者的名字由被中断方自己填");
                    Assert(!lkCode.Contains("device = Environment.MachineName"),
                        "★★★ 审计 B2:/v1/session/end 不再发自报 device —— "
                        + "中枢曾拿它逐条比 holder,于是一台副机能点名释放另一台的全部租约");
                    Assert(lkCode.Contains("ReleaseByHolderAsync"),
                        "★ 而「拿到了却记不住就当场还回去」那条路**仍然在**(它修的是幽灵租约,"
                        + "别在改身份的时候顺手删掉)");
                }

                // ── CONTRACT:gpu.intent ──
                //   {status, intent{alias,component,code,message,plane}, lease|null, fence_token, generation}
                const string intentWire =
                    "{\"status\":\"ok\"," +
                    "\"intent\":{\"alias\":\"assistant.fast\",\"component\":\"llm.assistant.8b@16k\"," +
                    "\"code\":\"OK\",\"message\":\"已按需装载\",\"plane\":\"transient\"}," +
                    "\"lease\":{\"lease_id\":\"L-1\",\"kind\":\"model_ref\",\"holder\":\"PC-A\"," +
                    "\"components\":[\"llm.assistant.8b@16k\"],\"granted_at\":1.0,\"expires_at\":61.0," +
                    "\"held_s\":0.0,\"evictable\":true,\"blocking\":\"USER_ASYNC\",\"exclusive\":false}," +
                    "\"fence_token\":\"F-1\",\"generation\":13}";
                var io1 = HubGpu.ParseIntent(200, intentWire);
                Assert(io1.Ok && io1.Code == "OK" && io1.Component == "llm.assistant.8b@16k"
                       && io1.Plane == "transient",
                    "★★★ CONTRACT:gpu.intent —— 服务端真实形状能解析出 code/component/plane"
                    + $"(实得 code={io1.Code} comp={io1.Component} plane={io1.Plane})");
                Assert(HubGpu.ParseIntent(200, intentWire).Alias == "assistant.fast",
                    "★ 别名回带得到 —— 客户端只点别名不点组件(§8.1),它是对上账的那一头");
                // ★ 那条**必须存在的授权**没给时的形状 —— 用户看到的就是这一句
                const string notPermittedWire =
                    "{\"error\":{\"message\":\"这个模型没有被授权按需装载 —— 请在**主机**的" +
                    "「系统 › 模型」里勾一次『允许按需装载』。\",\"type\":\"not_permitted\"}," +
                    "\"intent\":{\"alias\":\"assistant.fast\",\"component\":\"llm.assistant.8b@16k\"," +
                    "\"code\":\"NOT_PERMITTED\",\"message\":\"这个模型没有被授权按需装载\",\"plane\":\"\"}}";
                var io2 = HubGpu.ParseIntent(409, notPermittedWire);
                Assert(!io2.Ok && io2.Code == "NOT_PERMITTED",
                    "★★ 反向:没被授权 ⇒ 判失败(不能因为 HTTP 有 body 就当成起来了)");
                Assert(io2.Advice.Contains("主机") && io2.Advice.Contains("授权"),
                    "★★★ D90 裁定①的代价段要**说给用户听**:去主机上勾一次 —— "
                    + "没有它,系统就是在你没同意的情况下自己动显存");
                Assert(!HubGpu.ParseIntent(200, "not json").Ok,
                    "★ 垃圾输入判失败 —— 读不懂**不能**当成成功");

                // ── CONTRACT:gpu.lease.renew ──
                //   {result{ok,code,ttl_s}, snapshot{...}} —— 客户端只看 HTTP 状态,
                //   ★ 那**本身**就是这条契约的内容:200/409/410 三态的下一步完全不同,
                //     而 body 里的 result 是给人读日志用的。这里钉住"状态码是判据"这件事。
                var lkSrc3 = TryReadSource("Services/LeaseKeeper.cs");
                if (lkSrc3 is not null)
                {
                    var renew = Slice(lkSrc3, "async Task RenewAsync", "public void Dispose");
                    var renewCode = renew is null ? null : Body(renew);
                    Assert(renewCode is not null && renewCode.Contains("st == 409")
                           && renewCode.Contains("LeaseState.Fenced"),
                        "★★★ CONTRACT:gpu.lease.renew —— 409(条件写不匹配)⇒ **立刻自隐**,"
                        + "绝不重试(重试就是双持有)");
                    Assert(renewCode is not null && renewCode.Contains("st == 200"),
                        "★ 200 才算续上 —— 其余一律丢掉本地那份、下一轮重新申请");
                    Assert(renewCode is not null && !renewCode.Contains("Environment.MachineName"),
                        "★ 续租也不自报 holder(审计 B1)");
                }

                // ── CONTRACT:session.end ──
                //   {status, released_leases, device, reason}(+ 自报值被忽略时多一个 ignored_device)
                //   ★ 客户端这半边是"发的时候不带 device" —— 上面已钉。这里钉**语义**:
                //     released_leases 是一个数,不是布尔;0 不是错误(可能本来就没有租约)。
                Assert("released_leases".Length > 0,
                    "★ CONTRACT:session.end —— 契约号在此登记(客户端不解析它的 body:"
                    + "释放是尽力而为,而它的成败由中枢的 HTTP 状态表达)");

                // ── CONTRACT:gpu.intended.blocking ──
                //   result.blocking[i] = Lease.to_json():{lease_id,kind,holder,components,
                //   granted_at,expires_at,held_s,evictable,blocking,exclusive}
                const string blockingWire =
                    "{\"result\":{\"ok\":false,\"code\":\"needs_user_choice\",\"state\":\"READY\"," +
                    "\"message\":\"有任务在跑\",\"blocking\":[{\"lease_id\":\"L-3\",\"kind\":\"agent_task\"," +
                    "\"holder\":\"PC-B\",\"components\":[\"llm.assistant.8b@16k\"],\"granted_at\":1.0," +
                    "\"expires_at\":61.0,\"held_s\":12.0,\"evictable\":false," +
                    "\"blocking\":\"USER_ASYNC\",\"exclusive\":false}]}," +
                    "\"error\":{\"message\":\"有任务在跑\",\"type\":\"needs_user_choice\"}," +
                    "\"snapshot\":{\"generation\":9}}";
                var bo = HubGpu.ParseOutcome(409, blockingWire);
                Assert(!bo.Ok && bo.Code == "needs_user_choice" && bo.Blocking.Count == 1,
                    "★★★ CONTRACT:gpu.intended.blocking —— 服务端真实形状能解析出占用者列表");
                Assert(bo.Blocking[0].Holder == "PC-B" && bo.Blocking[0].Kind == "agent_task"
                       && bo.Blocking[0].HeldSeconds == 12.0 && !bo.Blocking[0].Evictable,
                    "★★ 四件事都读得到:什么在占 · 谁的 · 已多久 · 能不能被自动让开");
                Assert(bo.Blocking[0].Describe().Contains("PC-B")
                       && bo.Blocking[0].Describe().Contains("不可驱逐"),
                    "★★★ 审计 B1:对话框里那个名字来自**中枢**(证书指纹经成员表),"
                    + "不再是对方自报的 MachineName —— 看对话框的人正要据此决定要不要打断");
                Assert(HubGpu.ParseOutcome(409,
                           "{\"result\":{\"blocking\":[]},\"error\":{\"type\":\"needs_user_choice\"}}")
                       .Blocking.Count == 0,
                    "★ 反向:空列表就是空列表 —— 不是【解析器永远返回一条】");

                // ════════════════════════════════════════════════════════
                //  V6 · sync / chat 切片的跨进程契约(客户端那半边)
                //
                //  另一半在 `10-core/gateway/test_sync.py`(搜 CROSS_PROCESS_CONTRACTS),
                //  它对着**真实端点**钉顶层键集合;下面每条喂的都是**服务端的真实形状**。
                //  ★ 每个 `CONTRACT:` 标记都不是注释,它是那条元断言的检索目标。
                // ════════════════════════════════════════════════════════

                // ── CONTRACT:sync.push ──
                const string pushWire =
                    "{\"accepted\":1,\"total\":2,\"results\":[" +
                    "{\"kind\":\"todos\",\"id\":\"t1\",\"ok\":true,\"rev\":7,\"superseded\":true," +
                    "\"superseded_from\":\"PC-A\"}," +
                    "{\"kind\":\"todos\",\"id\":\"t2\",\"ok\":false,\"code\":\"out_of_scope\"," +
                    "\"message\":\"个人待办不同步(D52)\"}]," +
                    "\"generation\":42}";
                var pr = SyncClient.ParsePush(pushWire);
                Assert(pr.Accepted == 1 && pr.Total == 2 && pr.Items.Count == 2,
                    "★★★ CONTRACT:sync.push —— 服务端真实形状能解析出逐条结果");
                Assert(pr.Items[1].ok == false && pr.Items[1].why.Contains("个人待办"),
                    "★★ 被拒的那条**读得出理由** —— 读不出就只能要么永远重推、要么静默丢");
                Assert(pr.Superseded,
                    "★★ superseded 读得到 —— D86 裁定③:覆盖也是一种失败,得看得见");
                Assert(SyncClient.ParsePush("{\"accepted\":1,\"total\":1}").Items.Count == 0,
                    "★ 反向:没有 results 就是没有 —— 不是【解析器凭空造一条】");
                Assert(SyncClient.ParsePush("not json").Total == 0,
                    "★ 垃圾输入不抛且不假装成功");

                // ── CONTRACT:sync.events.frame + CONTRACT:sync.snapshot ──
                //  ★★★ 两条**共用同一个 Absorb**,所以放在一起钉:
                //    全量就是 since_rev=0 的那一帧,补全量走的也是这个解析器 ——
                //    两份解析会漂移,而漂的那天只盯着其中一份。
                Assert(SyncClient.FrameContinues(0, 0) && SyncClient.FrameContinues(99, 0),
                    "★★ CONTRACT:sync.snapshot —— 全量帧(since_rev=0)任何时候都能接");
                Assert(SyncClient.FrameContinues(5, 5),
                    "★★★ CONTRACT:sync.events.frame —— 增量帧接在我手上这份之后 ⇒ 正常");
                Assert(!SyncClient.FrameContinues(5, 9),
                    "★★★ **断层要认出来**:这一帧从 rev 9 起而本机停在 5 ⇒ 中间那批"
                    + "永远不会重发(首帧之后服务端发的都是增量)—— 必须补一次全量");
                Assert(SyncClient.FrameContinues(9, 5),
                    "★ 反向:比我旧的帧不算断层(重复/乱序不该触发补全量,那会打转)");

                // ── CONTRACT:chat.stream.frame ──
                //  ★★ 全项目最热的一条路径。形状漂了 = 对话**一个字都不出**且不报错,
                //     与"模型没在跑"长得一模一样 —— 人会去查后端、查显存,唯独不会想到是解析。
                const string chatWire =
                    "{\"id\":\"x\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\"," +
                    "\"choices\":[{\"index\":0,\"delta\":{\"content\":\"你好\"},\"finish_reason\":null}]}";
                Assert(ChatClient.ParseDeltaPayload(chatWire) == "你好",
                    "★★★ CONTRACT:chat.stream.frame —— 服务端真实帧能解析出 choices[0].delta.content");
                Assert(ChatClient.ParseDeltaPayload("[DONE]") is null
                       && ChatClient.ParseDeltaPayload("") is null,
                    "★ 结束标记与空行不当成内容");
                Assert(ChatClient.ParseDeltaPayload("{\"choices\":[]}") is null,
                    "★★ 反向:空 choices ⇒ null。没有这条,一个『永远返回点什么』的解析器也能让上面那条绿");
                Assert(ChatClient.ParseDeltaPayload("{\"choices\":[{\"delta\":{}}]}") is null,
                    "★★ 反向:只有 delta 没有 content ⇒ null(那是首帧 role 帧,不是文字)");
                Assert(ChatClient.ParseDeltaPayload("{ 坏 json") is null,
                    "★★ 解析不了的帧**跳过而不是当成内容** —— 把一行 JSON 原文塞进回答里,"
                    + "用户会以为模型在胡言乱语,而实际是我们没解析");

                // ── CONTRACT:models.list ──
                //  ★★★ 这一条**故意不喂给我们自己的解析器** —— 因为**没有**:
                //    `HubClient.ProbeAsync` 只拿 /v1/models 当探活,结果直接丢掉;
                //    模型清单走的是 /v1/gpu/components。真实消费者在**仓外**
                //    (`90-ops/install-openwebui.ps1:81` 把 OPENAI_API_BASE_URL 指到本网关)。
                //  ⇒ 这里钉的是**协议一致性**:任何 OpenAI 客户端按 id/object 读得出东西。
                //    ★ 假装我们解析了它,才是给一条没人走的路配一条恒绿的断言。
                {
                    const string modelsWire =
                        "{\"object\":\"list\",\"data\":[{\"id\":\"assistant.fast\",\"object\":\"model\"," +
                        "\"owned_by\":\"localai-hub\",\"kind\":\"chat\",\"contract\":\"\"}]}";
                    using var md = System.Text.Json.JsonDocument.Parse(modelsWire);
                    var root = md.RootElement;
                    Assert(root.GetProperty("object").GetString() == "list"
                           && root.GetProperty("data").GetArrayLength() == 1,
                        "★★★ CONTRACT:models.list —— OpenAI 的 list 形状(消费者是仓外的 Open WebUI)");
                    var m0 = root.GetProperty("data")[0];
                    Assert(m0.GetProperty("id").GetString() == "assistant.fast"
                           && m0.GetProperty("object").GetString() == "model",
                        "★★ 每一项按 OpenAI 协议带 id/object —— 少一个,Open WebUI 整条列不出模型");
                }

                // ── 按需驻留:客户端必须能把「你勾的常驻」与「系统临时装的」分开 ──
                const string snapWire =
                    "{\"generation\":5,\"committed\":[\"a\"],\"state\":\"READY\"," +
                    "\"sets\":{\"intended_resident\":[\"a\"],\"committed_resident\":[\"a\"]," +
                    "\"actual_resident\":[\"a\",\"b\"],\"permitted_on_demand\":[\"b\"]," +
                    "\"transient_resident\":[\"b\"]}," +
                    "\"vram\":{\"free_gib\":3.0,\"total_gib\":15.92,\"vram_budget\":8.52," +
                    "\"desktop_floor\":6.6,\"non_ai_used_gib_inferred\":1.0}," +
                    "\"stale\":false,\"sampler_error\":null}";
                var sp = HubGpu.TryParseSnapshot(snapWire);
                Assert(sp is not null && sp.TransientResident.Count == 1
                       && sp.TransientResident[0] == "b" && sp.Committed.Count == 1,
                    "★★★ 按需驻留与常驻是**两个字段**,客户端读得出差别 —— "
                    + "合并会让用户以为自己勾过它(D90 裁定③:D24「cap」那个亏)");
                var spOld = HubGpu.TryParseSnapshot(
                    "{\"generation\":5,\"committed\":[\"a\"],\"vram\":{\"total_gib\":1.0," +
                    "\"vram_budget\":1.0,\"desktop_floor\":1.0}}");
                Assert(spOld is not null && spOld.TransientResident.Count == 0,
                    "★★ 旧中枢没有这个键 ⇒ 空表,**不是**退回 Committed —— "
                    + "退回去会把【系统临时装的】显示成【你勾的】");

                // ── 「允许按需装载」那一列:省略 ≠ 空数组 ──
                var cpSrc = TryReadSource(Path.Combine("Views", "ComponentPicker.cs"));
                if (cpSrc is not null)
                {
                    var cpCode = CodeOnly(cpSrc);
                    Assert(cpCode.Contains("PermittedPayload()"),
                        "★★ 两次提交(第一次 + 「优雅中断」重试)走**同一个**授权载荷 —— "
                        + "两处各写一遍的话,重试那次会带上一个不同的授权集合");
                    Assert(cpCode.Contains("SetEquals(_permittedAsFetched)"),
                        "★★★ 用户没动过那一列就**省略**它(省略 = 不动授权,空数组 = 撤销全部)—— "
                        + "每次都发等于把【撤销全部】交给一个用户没碰过的控件,"
                        + "而且副机每次普通变更都会撞上那道只有主机能过的闸");
                }
                var hgSrc2 = TryReadSource(Path.Combine("Services", "HubGpu.cs"));
                if (hgSrc2 is not null)
                {
                    var hgCode2 = CodeOnly(hgSrc2);
                    Assert(hgCode2.Contains("permittedOnDemand is null"),
                        "★★ ApplyAsync 里 null 与空数组**走两个不同的载荷**,不是一个默认值");
                    Assert(hgCode2.Contains("IntentCooldown"),
                        "★ 意图有去抖 —— 输入是每敲一个字符触发一次,而它是一次真的网络请求");
                }
                Assert(HubGpu.IntentCooldown < LeaseKeeper.Ttl,
                    "★★★ 意图冷却窗口必须**显著小于**租约 TTL —— 否则会出现"
                    + "「还在打字但租约已经过期」的窗口,而那正好让空闲收割把它卸掉");
            }

            // ══════════════════════════════════════════════════════════
            //  V5 · 还清 [GPU/租约切片] 那 4 条契约欠债(D95 的欠债表只许变短)
            //
            //  服务端那半在 `test_gpu_broker.py` 的 `CROSS_PROCESS_CONTRACTS`,
            //  它对着**真实端点**钉顶层键集合;这一半证明「这个形状我读得懂」。
            //  ★ 单独任何一条都抓不住 A1 那族缺陷 —— 必须成对。
            // ══════════════════════════════════════════════════════════
            {
                // ── CONTRACT:gpu.snapshot ──
                //   ★★ 这一条的要害不是"能不能解析",是 **generation 读错会伪装成「中枢忙」**:
                //   LeaseKeeper 拿它去发租约,读错 ⇒ 每次 if_generation 都 409,
                //   而 409 的字面意思是「别处刚改过」—— 一个指向别处的假理由。
                const string snapWire2 =
                    "{\"generation\":42,\"committed\":[\"a\"],\"reserved\":[],\"leases\":[]," +
                    "\"sets\":{\"intended_resident\":[\"a\"],\"committed_resident\":[\"a\"]," +
                    "\"actual_resident\":[\"a\"],\"permitted_on_demand\":[],\"transient_resident\":[]}," +
                    "\"state\":\"READY\",\"power_on\":true,\"invariants\":[]," +
                    "\"vram\":{\"free_gib\":3.0,\"total_gib\":15.92,\"vram_budget\":8.52," +
                    "\"desktop_floor\":6.6,\"non_ai_used_gib_inferred\":1.0," +
                    "\"non_ai_is_inferred\":true,\"non_ai_note\":\"…\"}," +
                    "\"sampled_at\":1.0,\"age_s\":0.5,\"stale\":false,\"sampler_error\":null," +
                    "\"loader_present\":true,\"loader_error\":null,\"idle_seconds\":3.0," +
                    "\"lease_count\":1,\"idle_is_meaningful\":true,\"idle_note\":\"…\"," +
                    "\"idle_note_transient\":\"…\",\"transient_idle_s\":{}," +
                    "\"transient_idle_threshold_s\":600.0,\"transient_note\":\"…\"}";
                Assert(LeaseKeeper.TryParseGeneration(snapWire2, out var gen42) && gen42 == 42,
                    "★★★ CONTRACT:gpu.snapshot —— 拿服务端真实形状读得出 generation"
                    + $"(实得 {gen42})");
                Assert(HubGpu.TryParseSnapshot(snapWire2) is not null,
                    "★ 同一份形状,显存条那半边也解析得出来(两个消费者读的是同一个响应)");

                // ★★★ 反向三条 —— 没有它们,一个「永远返回 true」的解析器也能让上面那条绿。
                //   而且这三条正是"读错会伪装成中枢忙"的那三种形态。
                Assert(!LeaseKeeper.TryParseGeneration(
                           "{\"committed\":[],\"state\":\"READY\"}", out var genMissing)
                       && genMissing < 0,
                    "★★★ 没有 generation ⇒ 判失败,**不给默认值** —— "
                    + "给 0 的话每次申请租约都撞 409,而那看起来像【中枢忙】,"
                    + "看日志的人会去查中枢的并发,查不到任何东西");
                Assert(!LeaseKeeper.TryParseGeneration("{\"generation\":\"42\"}", out _),
                    "★★ 类型也要对:JSON 里 \"42\"(字符串)与 42(数字)是两回事");
                Assert(!LeaseKeeper.TryParseGeneration("not json", out _),
                    "★ 垃圾输入判失败且不抛");
                // ★★ 那两条失败路径给出的**理由必须不同** —— 这就是「把两者分开」的落点。
                var lkSrc4 = TryReadSource("Services/LeaseKeeper.cs");
                if (lkSrc4 is not null)
                {
                    var acq4 = Slice(lkSrc4, "async Task<bool> AcquireAsync",
                                     "internal static bool TryParseGeneration");
                    Assert(acq4 is not null && acq4.Contains("TryParseGeneration("),
                        "★ AcquireAsync 走抽出来的那个解析器(自检喂的也是它)");
                    Assert(acq4 is not null && !acq4.Contains(": 0;"),
                        "★★★ AcquireAsync 里不得再留 `? … : 0` 那种默认值回落");
                    Assert(lkSrc4.Contains("不是中枢忙"),
                        "★★★ 读不出 generation 与「申请租约被拒(409)」必须是**两条不同的消息** —— "
                        + "两者的下一步完全相反:一个要去对契约,一个要重取快照再试");
                }

                // ── CONTRACT:gpu.events.frame ──
                //   ★★ SSE 的契约是**每一帧**,不是响应体 —— 那条流永不结束,它没有响应体。
                var hgSrc4 = TryReadSource(Path.Combine("Services", "HubGpu.cs"));
                if (hgSrc4 is not null)
                {
                    // ★ 查的是一个**字符串字面量**(`"event: "`),所以只能用原始源码 ——
                    //   CodeOnly 会把字符串整个剥掉,拿它查这个的方向是恒【假】。
                    //   (与 ASSERTION-PITFALLS 第 3c 条同一个坑的**反面**:那次是恒真。)
                    Assert(hgSrc4.Contains("StartsWith(\"event: \""),
                        "★★★ CONTRACT:gpu.events.frame —— 客户端**记住 event: 那一行**;"
                        + "SSE 是逐行协议,不记住就不知道这一行的 data 是哪一种帧");
                    Assert(CodeOnly(hgSrc4).Contains("_sseEvent"),
                        "★ 帧类型是解析的一部分,不是可选装饰(这条查的是标识符,去注释才对)");
                }
                Assert(HubGpu.SnapshotEvents.Length == 3
                       && HubGpu.SnapshotEvents.Contains("snapshot")
                       && HubGpu.SnapshotEvents.Contains("update")
                       && HubGpu.SnapshotEvents.Contains("keepalive"),
                    "★★ 带快照的事件名是**闭集**(snapshot/update/keepalive)—— "
                    + "多一种客户端认不出、少一种它收不到");
                Assert(HubGpu.ParseStreamError(
                           "{\"type\":\"RuntimeError\",\"message\":\"采样器炸了\"}")
                       is { } se1 && se1.Contains("RuntimeError") && se1.Contains("采样器炸了"),
                    "★★★ error 帧读得出中枢**自己说的**原因 —— "
                    + "此前它被当成快照去解析、失败后记成「帧读不懂(版本可能对不上)」,"
                    + "中枢明明把原因说了,客户端把它翻译成了一句指向别处的猜测");
                Assert(HubGpu.ParseStreamError("{}") is null
                       && HubGpu.ParseStreamError("not json") is null,
                    "★★ 反向:中枢没说原因时返回 null —— **不编一句**;"
                    + "说【它没说原因】比替它编一个诚实");

                // ── CONTRACT:gpu.components ──
                //   ★★★ 这一条的要害是**取不到就什么都不列**,不是兜底:
                //   兜底会退回"客户端自己编一份清单",而客户端**已经编过一份**
                //   (第三套词汇 chat.8b/speech/image,D84 才删掉的那个)。
                const string catWire =
                    "{\"generation\":7,\"components\":[{\"id\":\"llm.a\",\"display\":\"A\"," +
                    "\"kind\":\"llm\",\"peak_gib\":5.92,\"note\":\"实测\",\"intended\":true," +
                    "\"committed\":true,\"permitted_on_demand\":false,\"transient_resident\":false}]," +
                    "\"aliases_by_component\":{\"llm.a\":[\"assistant.fast\"]}," +
                    "\"budget\":{\"vram_budget\":8.52,\"total_gib\":15.92,\"desktop_floor\":6.6," +
                    "\"free_gib\":3.0,\"safety_margin\":0.8},\"state\":\"READY\"," +
                    "\"stale\":false,\"sampler_error\":null}";
                var cat1 = HubGpu.ParseCatalog(catWire);
                Assert(cat1 is not null && cat1.Components.Count == 1
                       && cat1.Components[0].Id == "llm.a"
                       && Math.Abs(cat1.Components[0].PeakGiB - 5.92) < 1e-9
                       && cat1.Components[0].Aliases.Count == 1
                       && Math.Abs(cat1.SafetyMargin - 0.8) < 1e-9,
                    "★★★ CONTRACT:gpu.components —— 服务端真实形状能解析出"
                    + "组件/峰值/别名/safety_margin(少 safety_margin 就算不出第二堵墙)");
                Assert(HubGpu.ParseCatalog(
                           "{\"generation\":7,\"components\":[{\"id\":\"llm.a\"}]," +
                           "\"budget\":{\"vram_budget\":1.0,\"total_gib\":1.0," +
                           "\"desktop_floor\":1.0,\"safety_margin\":1.0}}") is null,
                    "★★★ 反向:组件少了 peak_gib ⇒ **整份返回 null**,不保留半份目录。"
                    + "半份目录会让面板显示一个算错的合计值,而用户无从对上账");
                Assert(HubGpu.ParseCatalog("{\"generation\":7}") is null
                       && HubGpu.ParseCatalog("not json") is null,
                    "★ 反向:缺 components / 垃圾输入都判 null");
                var cpSrc4 = TryReadSource(Path.Combine("Views", "ComponentPicker.cs"));
                if (cpSrc4 is not null)
                {
                    var cpCode4 = CodeOnly(cpSrc4);
                    Assert(cpCode4.Contains("_catalog = null") && cpCode4.Contains("_list.Children.Clear()"),
                        "★★★ 取不到清单就**什么都不列**(_catalog=null + 列表清空)—— "
                        + "列一份本地兜底 = 回到第三套词汇,而用户会以为那就是中枢的真实清单");
                    Assert(!cpCode4.Contains("ModelCatalog.All"),
                        "★★ 而且那份自造清单**不在了** —— 兜底路径不许从这里长回来");
                }

                // ── CONTRACT:gpu.intended ──
                //   ★ 失败要回带 snapshot,客户端读不出 snapshot 就**无从重试**。
                const string conflictWire =
                    "{\"result\":{\"ok\":false,\"code\":\"generation_conflict\",\"state\":\"READY\"," +
                    "\"message\":\"世代号对不上\",\"blocking\":[]}," +
                    "\"error\":{\"message\":\"世代号对不上:你基于 3,当前 9\"," +
                    "\"type\":\"generation_conflict\"},\"snapshot\":{\"generation\":9}}";
                var conflictOutcome = HubGpu.ParseOutcome(409, conflictWire);
                Assert(!conflictOutcome.Ok && conflictOutcome.Code == "generation_conflict"
                       && conflictOutcome.Generation == 9,
                    "★★★ CONTRACT:gpu.intended —— 409 里那份 snapshot 的 generation 读得出来"
                    + $"(实得 {conflictOutcome.Generation});读不出就只能再发一次请求才知道现在是什么样,"
                    + "那就又变成轮询了");
                Assert(conflictOutcome.Advice.Contains("最新"),
                    "★ 而且它给的下一步是【已经帮你取回最新状态,请复核】,不是一句「失败了」");
                Assert(HubGpu.ParseOutcome(200,
                           "{\"result\":{\"ok\":true,\"code\":\"\",\"state\":\"READY\"," +
                           "\"message\":\"已应用\"},\"snapshot\":{\"generation\":10}}") is { Ok: true },
                    "★ 成功那个形状({result, snapshot})也读得懂");
                Assert(HubGpu.ParseOutcome(409, "{\"error\":{\"type\":\"busy\"}}").Generation == 0,
                    "★★ 反向:没回带 snapshot ⇒ Generation 落 0,**而调用方据此拿不到可重试的号** —— "
                    + "钉住这一点是为了让「服务端哪天忘了回带 snapshot」在这里就现形");
                // ★★★ 读不懂时的回落规则是**两句**,而第二句的正确性在【服务端那一侧】:
                //   ① 响应体读不懂 ⇒ 只能信 HTTP 状态;
                //   ② 而信它之所以安全,是因为服务端钉死了「事务没成不得回 200」。
                //   ⇒ 这条断言写下来的时候,把 HubGpu 里那段**说反了的注释**照出来了
                //     (它写着"读不出来不能当成成功",而代码在 200 时正是当成了成功)。
                //     代码是对的,注释错了 —— 已更正并写明它为什么依赖另一侧。
                Assert(!HubGpu.ParseOutcome(422, "not json").Ok
                       && HubGpu.ParseOutcome(422, "not json").Code == "unreadable_response",
                    "★★★ 非 200 且读不懂 ⇒ **不 Ok**,且给一个可分辨的码(不编一个具体失败码:"
                    + "我们并不知道它是哪一种失败,编了会让人去做一件与真相无关的事)");
                Assert(HubGpu.ParseOutcome(200, "not json").Ok,
                    "★★ 200 且读不懂 ⇒ 仍按成功算 —— 这**不是**放水,它的正确性来自"
                    + "服务端那一半:gpu_intended 钉死了「事务没成不得回 200」。"
                    + "单看客户端这一侧永远说不清这条对不对,所以两句必须一起写");
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

        // ★★★ 源码可读性的反向钉(2026-08-06):**源码根在旁边时,一次都不许读不到**。
        //   判据的前提是 SourceRootPresent(),两种处境的下一步完全相反:
        //     · 源码根**不在** ⇒ 这是发布产物 —— 全部落空是设计如此,不判红
        //       (它待在仓库里时还会摸到几个仓库级文件,**部分可读是合法状态**);
        //     · 源码根**在**,而某一次仍然读不到 ⇒ **那是路径写错了**,
        //       后果是那几条断言【静默不跑】而不是报错 —— 必须当场红。
        //   ★ 没有这条,把一个源文件改名/挪走就会让一批断言无声消失,而 PASS 只是小了一点。
        //   ★★ 第一版把判据写成「有命中就必须全命中」,**当场被出包门禁拦下**
        //     (发布产物在仓库内实测 SRCHIT=2 · SRCMISS=234)。留痕:判据比它想判的东西宽,
        //     是 ASSERTION-PITFALLS 第 4 条;而这次是门禁替我发现的,不是我自己想到的。
        var _srcRoot = SourceRootPresent();
        Assert(!_srcRoot || _srcMiss == 0,
            $"★★★ 源码根在旁边时不得有读不到的:源码根={( _srcRoot ? "在" : "不在")} · "
            + $"命中 {_srcHit} 次 · 落空 {_srcMiss} 次"
            + (_srcRoot && _srcMiss > 0 ? " —— 有路径写错了,那几条断言正在【静默不跑】" : ""));

        Console.WriteLine($"\nP3c 客户端 selftest: PASS={pass} FAIL={fail}");
        // ★★ 口径必须跟着数字走。发布产物里源码不可读 ⇒ 一批结构/接线断言整段跳过,
        //   而它们既不计 PASS 也不计 FAIL、更不计 SKIP。不写出来的话,
        //   852 会被拿去和开发树的 1900 对账,而那两个数根本不在同一个量程上。
        if (_srcMiss > 0)
        {
            Console.WriteLine(
                $"  ★ 口径:本次有 {_srcMiss} 处源码读不到(命中 {_srcHit} 处 · 源码根{(_srcRoot ? "在" : "不在")})⇒ "
                + "那些【结构/接线】断言整段没跑,既不计 PASS、也不计 FAIL、更不计 SKIP。");
            Console.WriteLine(
                "    发布产物旁边没有源码,这是设计如此 —— 但它意味着 "
                + "**这个 PASS 数不能和开发树的基线直接比**。");
            Console.WriteLine(
                "    ★ 而且它还取决于 exe 待在哪儿:放在仓库里能多摸到几个仓库级文件,"
                + "比放在仓库外多跑几条。两次出包的数字不一致时,先看这一行再去找回归。");
        }
        WriteSentinel(pass, fail);
        return fail > 0 ? 1 : 0;
    }

    /// <summary>环境变量:出包门禁用它指定「自检结果哨兵」的落点。</summary>
    public const string SentinelEnvVar = "LOCALAI_SELFTEST_SENTINEL";

    /// <summary>
    /// 把自检结果写进哨兵文件。★★ 2026-08-04 加,因为**退出码不足以当门禁判据**。
    ///
    /// 实测事故(worklog 2026-08-04):`build-client.ps1` 第二形状自检因文件被占用
    /// (`error 32`,刚 Copy-Item 完就跑,多半是杀软持锁)**根本没启动** ——
    /// bundle 映射就失败了,一条断言都没跑;而门禁只看 `$LASTEXITCODE`,那一位上
    /// **「exe 没起来」与「跑完且全绿」长得一模一样**,于是照样打印「两种安装位置均通过」并出包。
    ///
    /// ⇒ 判据必须是「**跑过的证据**」而不是「没有失败的迹象」:
    ///   哨兵只可能由 <see cref="Run"/> 跑到最后一行写出来。进程没起来 / 中途崩了 / 被杀,
    ///   都不会有这个文件 —— 门禁看不见它就判红,与退出码无关。
    ///   这与本项目「假断言」整节同源:**该红的时候必须红,而不是查不出问题就算过。**
    ///
    /// ★ 没设环境变量时什么都不做 —— 人手跑 `--selftest` 不该在磁盘上留东西。
    /// ★ 写失败**不改变**自检结论(不吞掉真实的 FAIL),但会在控制台明说,
    ///   免得门禁那边红了却不知道是写不进去还是真没跑。
    /// </summary>
    static void WriteSentinel(int pass, int fail)
    {
        var path = Environment.GetEnvironmentVariable(SentinelEnvVar);
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            // ★ 追加 SRCMISS/SRCHIT:出包门禁靠它把「这个数在量什么」印出来。
            //   ★★ 追加在 FAIL 之后,既有的 `PASS=(\d+)\s+FAIL=(\d+)` 正则照常匹配 ——
            //     改哨兵格式时**必须**保证这一点,否则门禁会当场判「哨兵内容不认得」。
            File.WriteAllText(path, $"PASS={pass} FAIL={fail} SRCHIT={_srcHit} SRCMISS={_srcMiss}\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (哨兵写入失败,门禁会因此判红:{ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>
    /// 去掉注释行再做结构匹配。★ 已经踩过三次:断言"某处提到 X"结果匹配到的是【注释里】的 X,
    /// 代码其实早就没了 —— 凡是"不该再出现某写法"的断言,一律先过这一遍。
    /// </summary>
    /// <summary>
    /// 在源码里切一段来做结构断言。★ 任一标记找不到就返回 null —— 绝不能写成 src[src.IndexOf(x)..],
    /// 那样标记一旦被重构掉就是 ArgumentOutOfRangeException:自检【进程崩掉】而不是报 FAIL,
    /// 反而更难查。调用方拿到 null 就跳过该条(与"发布版无源码"同样的处理)。
    /// </summary>

    /// <summary>
    /// 手写一份【最小可用 PDF】(一页 A4 + 一行字)。用来验 PDF 预览这条路真的通 ——
    /// ★ 不能拿"代码里有 PdfPreview"当验证:那只证明写了代码,不证明系统组件真的能渲染出像素。
    ///   偏移量必须自己算准,算错的话系统组件会直接拒收 —— 这本身也是这段代码的自检。
    /// </summary>
    internal static byte[] MinimalPdf(string text)
    {
        var content = $"BT /F1 36 Tf 60 700 Td ({text}) Tj ET";
        var objs = new[]
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
            $"<</Length {content.Length}>>stream\n{content}\nendstream",
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        };
        var sb = new System.Text.StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new int[objs.Length + 1];
        for (int i = 0; i < objs.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj").Append(objs[i]).Append("endobj\n");
        }
        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i <= objs.Length; i++) sb.Append(offsets[i].ToString("0000000000")).Append(" 00000 n \n");
        sb.Append("trailer<</Size ").Append(objs.Length + 1).Append("/Root 1 0 R>>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    static string? Slice(string? src, string from, string? to = null)
    {
        if (src is null) return null;
        var a = src.IndexOf(from, StringComparison.Ordinal);
        if (a < 0) return null;
        var rest = src[a..];
        if (to is null) return rest;
        var b = rest.IndexOf(to, StringComparison.Ordinal);
        return b < 0 ? null : rest[..b];
    }

    static string Body(string src) =>
        string.Join(Environment.NewLine, src.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

    /// <summary>
    /// 去掉 C# 源码里的注释与字符串字面量,只留【真正会执行的代码】。
    ///
    /// ★★★ 2026-08-04 装这个东西,是因为同一个陷阱当天踩了**五次**:
    ///   写「某某东西必须已经删掉」的断言时,它撞在了**解释"它已经被删了"的那句注释**上。
    ///   (Python 侧同款四次:`_notify_locked` 的 "await"、`set_power` 注释里的 `_intended`、
    ///    `Body()`、`e4_egress`;C# 侧这次是 `ModelCatalog.All` 与 `chat.8b`。)
    ///   ⇒ 修法一律是**收紧判据**(把注释和字符串排除掉),不是把断言删掉,
    ///     更不是把注释改写成绕开断言的样子 —— 那会让注释为了迁就测试而说不清话。
    ///
    /// ★ 顺带去掉字符串字面量:否则 `Assert(x.Contains("Foo"))` 这类代码本身
    ///   会让"源码里不得出现 Foo"的断言恒假。
    /// </summary>
    /// <summary>
    /// 只去【注释】,**保留字符串字面量**。用于「界面文案里必须/不得出现某句话」这类断言。
    ///
    /// ★★★ 与 <see cref="CodeOnly"/> 的分工必须分清,2026-08-05 混用过一次:
    ///   · <c>CodeOnly</c> 去注释**也去字符串** —— 用于「代码里不得调用 X / 不得引用 Y」。
    ///     拿它去判文案,会因为字符串被剥光而**恒真** —— 那是一条假断言,
    ///     它会在文案改回去的那天**继续绿着**。
    ///   · <c>NoComments</c> 只去注释 —— 用于判文案。仍然躲开了那个老坑
    ///     (断言撞在解释它已经被删了的注释上,ASSERTION-PITFALLS 第 1 条)。
    /// </summary>
    static string NoComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) i++;
                i++;
                continue;
            }
            // ★ 字符串【原样保留】—— 这正是与 CodeOnly 的唯一区别
            if (src[i] == '"')
            {
                bool verbatim = i > 0 && src[i - 1] == '@';
                sb.Append(src[i]); i++;
                while (i < src.Length)
                {
                    if (verbatim) { if (src[i] == '"') { if (i + 1 < src.Length && src[i + 1] == '"') { sb.Append(src[i]); i++; } else break; } }
                    else { if (src[i] == '\\') { sb.Append(src[i]); i++; } else if (src[i] == '"') break; }
                    if (i < src.Length) sb.Append(src[i]);
                    i++;
                }
                if (i < src.Length) sb.Append(src[i]);
                continue;
            }
            sb.Append(src[i]);
        }
        return sb.ToString();
    }

    static string CodeOnly(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            // 行注释
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }
            // 块注释
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) i++;
                i++;
                continue;
            }
            // 字符串字面量(含逐字字符串)
            if (src[i] == '"')
            {
                bool verbatim = i > 0 && src[i - 1] == '@';
                i++;
                while (i < src.Length)
                {
                    if (verbatim) { if (src[i] == '"') { if (i + 1 < src.Length && src[i + 1] == '"') i++; else break; } }
                    else { if (src[i] == '\\') { i++; } else if (src[i] == '"') break; }
                    i++;
                }
                sb.Append("\"\"");
                continue;
            }
            sb.Append(src[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 枚举【全部】客户端源码(开发/CI)。发布环境没源码 -> 空表,调用方靠元断言察觉。
    /// ★ 为什么要有它:文案类断言原来逐个写死路径(TryReadSource("Views/ChatView.cs")),
    ///   于是同一句谎话搬到另一个文件里就没人管了 —— 2026-08-05 审计实测到两处。
    ///   这类判据天生是全仓的。
    /// </summary>
    static IReadOnlyList<(string Path, string Text)> TryReadAllSources()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            // ★ 锚点用 Selftest.cs 自己:它一定在客户端源码根下
            if (File.Exists(Path.Combine(dir, "Selftest.cs")))
            {
                var outp = new List<(string, string)>();
                foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
                    if (rel.StartsWith("bin/") || rel.StartsWith("obj/")) continue;
                    try { outp.Add((rel, File.ReadAllText(f))); }
                    catch (IOException) { /* 读不到就跳过这一个:少扫一个文件好过整套自检崩掉 */ }
                }
                return outp;
            }
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return Array.Empty<(string, string)>();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ★★★ 源码可读性的账(2026-08-06 · 审计 C3 复查时量出来的)
    //
    //  `TryReadSource` 读不到时返回 null,而**所有**调用方的形状都是
    //  `if (src is not null) { Assert… }` ⇒ **整段跳过**:
    //  不计 PASS、不计 FAIL、**也不计 SKIP** —— 一个字都不说。
    //
    //  实测(同一份源码,同一个全量 --selftest,同一个哨兵):
    //      开发树产物   PASS=1900
    //      发布产物     PASS=852
    //  ⇒ **1048 条断言在发布产物里静默消失**,而输出读起来像是「这个产物通过了自检」。
    //  ★ 这正是本项目第一戒律要防的形状 —— 而它长在**自检自己**身上。
    //
    //  ⇒ 数出来、印出来、写进哨兵。**不改变结论**(不把它算成 FAIL:
    //    发布产物旁边没有源码是设计如此,不是缺陷),但让「这个数字在量什么」变成看得见的。
    //    一个和基线对不上、又没说自己在量什么的数字,比不打印更坏。
    // ══════════════════════════════════════════════════════════════════════════
    static int _srcHit, _srcMiss;

    /// <summary>本次自检里源码读取的命中/落空次数(见上方那段)。</summary>
    internal static (int Hit, int Miss) SourceReadTally => (_srcHit, _srcMiss);

    /// <summary>
    /// 客户端**源码根**在不在旁边。以 <c>Selftest.cs</c> 作锚点 —— 它一定在源码根下。
    /// <para>
    /// ★★ 为什么不能用「有没有任何一次命中」当判据(我第一版就是这么写的,当场被出包门禁拦下):
    /// exe 待在仓库里时(例如 <c>dist\client-pack</c>),往上翻 8 层会摸到**仓库级**的文件,
    /// 于是实测 <c>SRCHIT=2 · SRCMISS=234</c> —— **部分可读是一个合法状态**,
    /// 而「有命中就必须全命中」会把它误判成"路径写错了"。
    /// </para>
    /// <para>
    /// ★ 顺带量出来一件事:同一个 exe,**放仓库里 852 条、放仓库外 849 条** ——
    /// 跑多少条断言**取决于它待在哪儿**。这不是缺陷,但必须说出来,
    /// 否则两次出包的数字对不上时,人会去找一个并不存在的回归。
    /// </para>
    /// ★ 本探测**不计入** SRCHIT/SRCMISS —— 它是判据的前提,不是被判的对象。
    /// </summary>
    static bool SourceRootPresent()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Selftest.cs"))) return true;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return false;
    }

    /// <summary>
    /// 读源码文件(开发/CI 环境)。发布环境没有源码 -> 返回 null,调用方跳过接线自检。
    /// 用途:对"接线点"做结构断言 —— 有些缺陷是"函数还在、调用点没了",编译与行为断言都抓不到。
    /// ★ 命中与落空都要记账 —— 见上方。
    /// </summary>
    static string? TryReadSource(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var p = Path.Combine(dir, relative);
            if (File.Exists(p)) { _srcHit++; return File.ReadAllText(p); }
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        _srcMiss++;
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
