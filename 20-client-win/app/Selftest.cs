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
                Assert(chatSrc.Contains("void ToggleGhost()") && chatSrc.Contains("if (InGhost) { ToNormal(); return; }"), "幽灵按钮可【退出】(再按回普通会话)");
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
            var mhSrc = TryReadSource(Path.Combine("Views", "MenuHost.cs"));
            if (mhSrc is not null)
                Assert(mhSrc.Contains("SwallowClick") && mhSrc.Contains("_openCount"), "MenuHost 记录菜单开/刚关状态");
            var mwSwallow = TryReadSource("MainWindow.xaml.cs");
            if (mwSwallow is not null)
            {
                Assert(mwSwallow.Contains("MenuHost.SwallowClick") && mwSwallow.Contains("me.Handled = true"),
                       "★ 菜单开着时点背后:主窗口一次性吞掉这次点击(只关菜单)");
                var pmd = mwSwallow[mwSwallow.IndexOf("PreviewMouseDown +=", StringComparison.Ordinal)..];
                Assert(pmd.IndexOf("MenuHost.SwallowClick", StringComparison.Ordinal) < pmd.IndexOf("if (!Overlay.IsOpen) return;", StringComparison.Ordinal),
                       "菜单判断排在 Overlay 之前(菜单不在 Overlay 体系里,漏判就会穿透)");
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
                Assert(setSync.Contains("与 Apple 同步") && setSync.Contains("尚未接入"), "设置里有【与 Apple 同步】预留板块且如实标注未接入");
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
                Assert(appSrc2.Contains("if (!hadStore) SeedDemoTasks()"), "有存档就不再播种示例数据");
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
                Assert(cvShare.Contains("删除共享会话") && cvShare.Contains("所有设备】生效"),
                       "★ 删共享会话前提示会影响所有设备(任何机器都能删)");
                Assert(cvShare.Contains("· 共享"), "会话行标出共享状态");
                Assert(cvShare.Contains("中枢尚未接入"), "★ 如实说明现在只是标记、接入后才上传");
            }
            var puShare = TryReadSource(Path.Combine("Views", "ProjectUi.cs"));
            if (puShare is not null)
                Assert(puShare.Contains("提升为共享") && puShare.Contains("文件夹】仍在"),
                       "★ 项目提升说明:共享元数据,文件夹仍在原机");

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
                Assert(cvRO.Contains("attachmentsBelow: true") && cvRO.Contains("overlayBanner: !hasMsgs"),
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
