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

            // ---- 项目中心(主页田字格 + 深链)----
            var pc = new ProjectCenter();
            Assert(pc.Items.Count == 0, "项目中心初始为空");
            pc.Add(new Project("a", "旧项目", "x", "chat", ProjectScope.Family, DateTime.Now.AddHours(-3)));
            pc.Add(new Project("b", "新项目", "y", "courses", ProjectScope.Personal, DateTime.Now.AddMinutes(-1)));
            Assert(pc.Recent().First().ProjectId == "b", "项目按最近打开排序(主页要的是回到刚才那件事)");
            pc.Touch("a");
            Assert(pc.Recent().First().ProjectId == "a", "打开项目后它排到最前");

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
