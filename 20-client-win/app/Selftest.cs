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
                Assert(mdlView.Contains("IsModelEnabled") && mdlView.Contains("AutoStartPreset"), "模型页可选启用模型 + 自动启用规则");
                Assert(mdlView.Contains("model.not_connected"), "模型页顶部诚实标注未接 Broker(不假装加载)");
            }
            var msSet = new AppSettings();
            Assert(msSet.IsModelEnabled("chat.8b"), "模型默认启用");

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
            cc.PurgeGhosts();
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
                Assert(chatSrc.Contains("(_wsKey == \"chat\" && !inProject) ? GhostButton(InGhost) : null"), "项目会话下不显示幽灵按钮");
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
                Assert(cvAtt.Contains("_pending.Clear(); BuildConversation();") && cvAtt.Contains("\"清空\""), "有一键清空附件");
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
                Assert(cvShare.Contains("条消息)会一起共享"), "★ 确认框写明整段历史一起共享(用户裁定 A)");
                Assert(cvShare.Contains("删除共享会话") && cvShare.Contains("中枢尚未接入") && cvShare.Contains("只影响这台"),
                       "★ 删共享会话前【不得】断言对所有设备生效 —— 中枢未接入时共享只是本机标记,什么都没发生");
                Assert(cvShare.Contains("· 共享"), "会话行标出共享状态");
                Assert(cvShare.Contains("中枢尚未接入"), "★ 如实说明现在只是标记、接入后才上传");
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
                Assert(cvIS.Contains("if (s.Interpret) TheApp.Interpret.SetMode(TranslationMode.Interpret);"),
                       "★ 在文字翻译界面点开同传记录会自动切到同传界面");
                Assert(cvIS.Contains("if (movable) m.Items.Add(move)") && cvIS.Contains("if (movable) m.Items.Add(toWs)"),
                       "★ 不能搬的会话:菜单里【根本不出现】那两项,而不是点了再报错");
                Assert(cvIS.Contains("Icons.Make(IconName.Mic, 12"),
                       "同传记录在列表里用麦克风图标区分(列表窄,一个图标够用)");
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
                Assert(fresh.TryStart().Contains("语言方向"), "★ 没设方向时如实说要先设方向");
                fresh.SetMyLang("zh"); fresh.SetTheirLang("en");
                Assert(fresh.DirectionReady, "两端设好了才就绪");
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
                Assert(badge is not null && badge.Contains("去设置") && badge.Contains("一键开启"),
                       "红=去设置、黄=一键开启;绿的时候直接显示版本号,没有按钮");
                Assert(barMode.Contains("new ToggleSwitch(\"我方译文语音\"") && barMode.Contains("enabled: drv.Installed"),
                       "★ 没装虚拟声卡时,语音输出开关灰掉禁用(译文根本送不进会议,能拨就是骗人)");
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
                Assert(tbSrc.Contains("var pool = PoolCard();            // ★ 只此一处:两种模式共用"),
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
                Assert(spec is not null && spec.Contains("BottomAccessory = () => new TranslationBar()"),
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
            var peDup = TryReadSource(Path.Combine("Views", "ProjectEditor.cs"));
            if (peDup is not null)
            {
                Assert(peDup.Contains("转跳至该项目") && peDup.Contains("FindByFolder"), "★ 路径已有项目时,创建按钮变【转跳至该项目】");
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
                Assert(cvRO.Contains("attachmentsBelow: heroNow") && cvRO.Contains("overlayBanner: heroNow"),
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
                       && tl.Contains("var lx = roomRight >= 16 ? right : x + 2;")
                       && tl.Contains("placedLabels.Add(new Rect(x, yTop, w, height));"),
                       "★★ 外置标题【永远画、且永远与条同高】—— 同一个 y 是最硬的归属提示。"
                       + "前后错过两次:往上让会飘得认不出主、被占就不画会直接消失"
                       + "(用户反馈:多个共享宽度时名字被省略成'…',什么都看不见)");
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
                Assert(tl.Contains("$\"还有 {hiddenNames.Count} 条全天\"")
                       && tl.Contains("string.Join(\"、\", hiddenNames)"),
                       "★ 没画出来的【如实说一句白话、悬停列出是哪几条】"
                       + "—— 原来只写一个「+1」,用户直接问「这个 +1 是什么」,要人猜的标记等于没标");
                Assert(tl.Contains("ToolTip = box.ToolTip,") && tl.Contains("ToolTip = chip.ToolTip,"),
                       "★ 外置标题悬停出全名、点一下能编辑(它比那条细色块好碰得多)");
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
                Assert(tl.Contains("for (int back = 0; back <= 7; back++)")
                       && tl.Contains("if (back > 0 && LastDayOf(ev) < day.Date) continue;"),
                       "★★ 往回找【整周】而不是只找前一天 —— 一条跨三天的定时日程,"
                       + "中间那天既不是起始日也不是结束日,只看前一天会把它整段漏掉");
                var ce2 = TryReadSource(Path.Combine("Views", "CalendarEditor.cs"));
                if (ce2 is not null)
                {
                    Assert(ce2.Contains("var endOffset = existing is not null") && ce2.Contains("d0.AddDays(endOffset) + endAt"),
                           "★★ 非全天日程也能跨天:结束 = 开始那天 + 【结束日偏移】+ 结束时刻");
                    Assert(ce2.Contains("endOffset = 1;") && ce2.Contains("if (endAt > startAt || endOffset > 0) return;"),
                           "★★ 把结束拨到早于开始 = 【跨到次日】,而不是把开始一起往前拖"
                           + "(旧做法等于替用户改了他刚刚没动的那一头,而且让跨天永远表达不出来)");
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
                       && homeTodo.Contains("_todoVisible ? 12 : 0, PanelGap);"),
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
                    var ph = Slice(mv, "static FrameworkElement StrategyPlaceholder()", "static FrameworkElement ModelToggle");
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
                    Assert(cv3.Contains("AI 模型尚未接入(P4)—— 消息会记在本机"),
                           "★ AI 不回答的真因(P4 未接)无条件单说,不再归因到主机离线");
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
                if (vm2 is not null)
                    Assert(vm2.Contains("_smiDead"),
                           "★ nvidia-smi 读不到就死心(无 N 卡机器不再每 2 秒起一次进程)");
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
                // 待办的合并层:与日历同一套规则
                var box = new List<TodoItem>();
                var todo1 = new TodoItem("", "买菜", TodoKind.Chore, Source: "apple", ExternalId: "r-1");
                Assert(TodoCenter.MergeInto(box, new[] { todo1 }) == 1, "待办首次合并加入");
                Assert(TodoCenter.MergeInto(box, new[] { todo1 }) == 0, "★ 同 Apple UID 的待办不重复加");
                Assert(TodoCenter.MergeInto(box, new[] { new TodoItem("", "  ", TodoKind.Chore) }) == 0,
                       "空标题的待办不并入");

                // VTODO 解析
                static string V(params string[] ls) => string.Join(Environment.NewLine, ls);
                var (tds, _, _) = Services.ICalParser.ParseTodos(V(
                    "BEGIN:VTODO", "UID:t-1", "SUMMARY:交电费", "DUE;VALUE=DATE:20260805",
                    "PRIORITY:1", "STATUS:COMPLETED", "END:VTODO"), TodoKind.Personal);
                Assert(tds.Count == 1 && tds[0].Title == "交电费", "VTODO 能解析");
                Assert(tds[0].Done && tds[0].Priority == TodoPriority.High, "★ 完成状态与优先级都读到(PRIORITY 1-4 = 高)");
                Assert(tds[0].Source == "apple" && tds[0].ExternalId == "t-1", "回填来源与 UID");

                // ---- Apple「提醒事项已升级】的墓碑条目(用户实遇 2026-07-31,已查证)----
                // ★★ 这两条不是待办,是 Apple 在说"东西不在这儿"。当任务导入 =
                //   替 Apple 把一句通知伪装成两条任务,用户会以为同步成功了。
                Assert(Services.AppleReminderNotice.IsUpgradeNotice("在哪里可以找到我的提醒事项？", null),
                       "★ 认得出中文公告一(全角问号也要能匹配)");
                Assert(Services.AppleReminderNotice.IsUpgradeNotice("此列表的创建者已升级这些提醒事项。", null),
                       "★ 认得出中文公告二");
                Assert(Services.AppleReminderNotice.IsUpgradeNotice("Where are my reminders?", null),
                       "认得出英文公告一");
                Assert(Services.AppleReminderNotice.IsUpgradeNotice("The creator of this list has upgraded these reminders.", null),
                       "认得出英文公告二");
                Assert(Services.AppleReminderNotice.IsUpgradeNotice("随便什么", "详见 support.apple.com/HT210220"),
                       "★ 描述里的 Apple 支持链接是【与语言无关】的标记(换什么语言都认得出)");
                Assert(!Services.AppleReminderNotice.IsUpgradeNotice("买菜", null),
                       "★ 普通待办不会被误判成公告");
                Assert(Services.AppleReminderNotice.IsUpgradedList("提醒 ⚠️") && !Services.AppleReminderNotice.IsUpgradedList("工作"),
                       "★ 清单名带 ⚠ = Apple 的占位清单(集合层判定,最稳)");

                // 解析层:公告不入待办,且【单独计数】—— 与"读不懂而跳过"分开
                var (nt, nsk, nnotice) = Services.ICalParser.ParseTodos(string.Join(Environment.NewLine,
                    "BEGIN:VTODO", "UID:n-1", "SUMMARY:在哪里可以找到我的提醒事项？", "END:VTODO",
                    "BEGIN:VTODO", "UID:n-2", "SUMMARY:此列表的创建者已升级这些提醒事项。", "END:VTODO",
                    "BEGIN:VTODO", "UID:r-9", "SUMMARY:真的待办", "END:VTODO"), TodoKind.Personal);
                Assert(nt.Count == 1 && nt[0].Title == "真的待办", "★ 只有真待办被导入");
                Assert(nnotice == 2 && nsk == 0,
                       "★★ Apple 公告单独计数(2),不混进「读不懂」那个数 —— 混了就只能说「没东西」,而真相是「拿不到」");

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
                Assert(new Services.AppSettings().AppleCalendarList is not null
                       && new Services.AppSettings().AppleReminderList is not null,
                       "★ 日历/提醒清单落盘保存 —— 连上后一直在,不必每次先点刷新");

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

            var mwSys = TryReadSource("MainWindow.xaml.cs");
            if (mwSys is not null)
            {
                var nav = Slice(mwSys, "public void Navigate(string key)", "HighlightNav(key);");
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

            var mwTab = TryReadSource("MainWindow.xaml.cs");
            if (mwTab is not null)
            {
                Assert(mwTab.Contains("ke.Key != Key.Tab") && mwTab.Contains("FocusPolicy.HandleTab(this, FocusPark)"),
                       "★ Tab 由窗口统一接管(不靠逐个控件设 IsTabStop)");
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
                Assert(cvFocus.Contains("var refocus = _input.IsKeyboardFocusWithin;"),
                       "★ 会话区重建后把焦点还给输入框(每发一条消息 _input 就被换掉一次)");
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
                    Assert(mwBrand.Contains("Background=\"{DynamicResource ThemeColor}\""),
                           "★ 品牌标记用主题色(身份),不用着重色 —— 否则看着像个能点的按钮");
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
    /// 去掉注释行再做结构匹配。★ 已经踩过三次:断言"某处提到 X"结果匹配到的是【注释里】的 X,
    /// 代码其实早就没了 —— 凡是"不该再出现某写法"的断言,一律先过这一遍。
    /// </summary>
    /// <summary>
    /// 在源码里切一段来做结构断言。★ 任一标记找不到就返回 null —— 绝不能写成 src[src.IndexOf(x)..],
    /// 那样标记一旦被重构掉就是 ArgumentOutOfRangeException:自检【进程崩掉】而不是报 FAIL,
    /// 反而更难查。调用方拿到 null 就跳过该条(与"发布版无源码"同样的处理)。
    /// </summary>
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
