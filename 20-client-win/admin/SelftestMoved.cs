// V21 -- 跟着那 3100 行**一起搬过来的断言**。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ 为什么单独一个文件,而不是塞进 `Selftest.cs` 的三个 Run 里:
//    这些断言的**出处**是账的一部分。交回时要答的是「掉了多少 / 去了哪里 / 净丢失几条」,
//    而一份混进去的名单答不了第二问。
//    ⇒ 搬过来的原样留在这里(判词一个字没改),新写的在 `Selftest.cs` 里。
//
//  ★★ 判据跟着**锚点**走:锚点文件搬到哪边,断言就跟到哪边(V10 §10.2 给的方法)。
//    所以下面读源码的那些,`TryReadSource` 解到的是**管理端**的源码根。
//
//  ★★★ 一处必须写清的**判词改写**(不是改宽,是改对):
//    原来钉在 `Views/DevicesView.cs` 上的那些,现在钉 `Views/HostHubView.cs`;
//    切片的成员名有几个跟着换了(`SelfPairAsync` 没了 —— 见 HostHubView 文件头那段
//    「如实交代的行为变化」)。**凡是判词本身要跟着改的,下面逐条标了 `★ V21 判词改写`,
//    并写明改成了什么、为什么那不是把判据改宽。**
// ══════════════════════════════════════════════════════════════════════════════

using System.Text.Json;
using LocalAI.Admin.Services;
using LocalAI.Client.I18n;
using LocalAI.Client.Services;

namespace LocalAI.Admin;

public static partial class Selftest
{
    /// <summary>跟着 3100 行一起搬过来的那批断言。★ 由 <c>Run()</c> 调。</summary>
    static void RunMoved()
    {
        Console.WriteLine("\n-- 跟着迁移搬过来的断言(V21)--");
            // ---- 记忆库 + 存储清理(2026-07-30 用户裁定)----
            {
                var mc = new MemoryCenter();
                var old = new MemoryEntry(MemoryCenter.NewId(), "很久没用的摘要", "正文", MemoryKind.Summary,
                    ProjectScope.Personal, MemberContext.Current, null, new[] { "s-1" }, DateTime.Now.AddDays(-90));
                var pinned = new MemoryEntry(MemoryCenter.NewId(), "置顶的老摘要", "正文", MemoryKind.Summary,
                    ProjectScope.Personal, MemberContext.Current, null, null, DateTime.Now.AddDays(-90), Pinned: true);
                var pref = new MemoryEntry(MemoryCenter.NewId(), "老偏好", "正文", MemoryKind.Preference,
                    ProjectScope.Personal, MemberContext.Current, null, null, DateTime.Now.AddDays(-90));
                var fresh = new MemoryEntry(MemoryCenter.NewId(), "刚用过", "正文", MemoryKind.Summary,
                    ProjectScope.Personal, MemberContext.Current, null, null, DateTime.Now.AddDays(-90), LastUsedAt: DateTime.Now);
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
                var m2 = new MemoryEntry(MemoryCenter.NewId(), "带来源的", "正文", MemoryKind.Summary,
                    ProjectScope.Personal, MemberContext.Current, "prj-1", new[] { "s-9" }, DateTime.Now);
                mc.Add(m2);
                mc.MarkOriginalsDeleted(new[] { "s-9" });
                Assert(mc.Find(m2.Id)!.SourceOriginalsDeleted, "★ 原文删除后记忆标注「原文已删除」(不留死链)");

                // D45:别人的个人记忆不出现
                mc.Add(new MemoryEntry(MemoryCenter.NewId(), "别人的私人记忆", "正文", MemoryKind.Summary,
                    ProjectScope.Personal, "m-other", null, null, DateTime.Now));
                Assert(!mc.Visible().Any(x => x.Title == "别人的私人记忆"), "★ 记忆库同样守 D45(别人的私人记忆不出现)");

                // JSON 往返
                var mj = System.Text.Json.JsonSerializer.Serialize(mc.Export(), StoreJson);
                var mb = new MemoryCenter();
                mb.Import(System.Text.Json.JsonSerializer.Deserialize<List<MemoryEntry>>(mj, StoreJson));
                Assert(mb.Items.Count == mc.Items.Count, "记忆库 JSON 往返");
                Assert(mc.Bytes() > 0, "记忆库占用可计算(真算字节,不估)");
            }
            // ---- 一键配对:按角色分流 + 配对窗口的三道闸 ----
            {
                // ⑥ 起中枢那一步:客户端拉得动是因为它自己就是普通用户(D46),不是绕过了护栏
                //
                // ══════════════════════════════════════════════════════════
                //  ★★★ V22:这一组断言**换了钉的对象** —— 从 `HostHubView.StartEdgeAsync`
                //    改钉 `HostProvision.EnsureEdgeAsync`。理由不是重构口味,是那个函数**已经删了**:
                //    它是**第二套起栈实现**(只起 Edge、从不起网关),与
                //    `HostProvision.EnsureStackAsync` 平行存在 ——
                //    而正是它让「EnsureStackAsync 零调用点」这件事**从表面上看不出来**
                //    (界面上确实有一条能起 Edge 的路,只是那条路起不出一个能用的栈)。
                //  ★★ 两套合成一条之后,这些硬救回来的约束**一条都没丢**,只是换了地方 ——
                //    所以断言跟着搬,**不是**删掉。搬完仍然能为假:
                //    把 `CreateNoWindow` 从 `EnsureEdgeAsync` 里去掉,下面第二条当场红。
                // ══════════════════════════════════════════════════════════
                // ★★★★ V22:本组**从 `if (dv4 is not null)` 里搬了出来**。理由是实测出来的,
                //   而且它本身就是本轮要抓的那个病:
                //   `dv4 = TryReadSource("Views/DevicesView.cs")`,而 V21 已经把那个文件
                //   改名成 `HostHubView.cs` —— **管理端里根本没有 DevicesView.cs**。
                //   ⇒ `dv4` 恒为 null ⇒ 那个 `if` 底下的**整片断言(实测 69 条)一条都没跑过**,
                //     而且**不留任何痕迹**:不计 PASS、不计 FAIL、连 SKIP 都没有。
                //   ★ 也就是说,把这一组留在里面 = 写了一组永远不会执行的断言。
                //   ★★ 那 69 条的处置**不在本车道**(重新指向之后 44 条绿、25 条红,
                //     红的多半是 V21 有意留在客户端的成员 —— 要逐条裁,不是改个文件名了事)。
                //     已在交回里列成第一条「同形」发现。这里先把**本轮新写的**那几条救出来。
                var hhv = TryReadSource(Path.Combine("Views", "HostHubView.cs"));
                var hpSrc = TryReadSource(Path.Combine("Services", "HostProvision.cs"));
                // ★★★★ V22 出包门禁当场抓住的一条（它就是干这个的）：
                //   把这组断言从 `if (dv4 is not null)` 里搬出来的时候，
                //   把它们「读不到源码就跳过」那层保护**一并搬没了** ⇒
                //   在**出包形态**（exe 旁边没有源码）里这几条全部变红，
                //   而红的理由是假的：`spawn=-1 wait=-1 ok=-1` —— 那不是代码坏了，是没读到文件。
                //   ★ 本文件早就写过这一条（「必须用 if (src is not null) 兜住」），而我又踩了一次。
                //   ⇒ 读不到就**明着 Skip**，不判红；读得到才断言。
                if (hpSrc is null || hhv is null)
                    Skip("起中枢那一步的那一组断言",
                         "读不到源码（"
                         + (hpSrc is null ? "Services/HostProvision.cs " : "")
                         + (hhv is null ? "Views/HostHubView.cs " : "")
                         + "—— 出包产物旁边没有源码，设计如此）。"
                         + "★ 这一档以前是**静默跳过**的，现在至少说得出自己没跑。");
                if (hpSrc is not null && hhv is not null)
                {
                    var se = hpSrc is null ? null
                           : Slice(hpSrc, "static async Task<SetupStep> EnsureEdgeAsync",
                                          "/// <summary>自动起栈拉起来的那个中枢进程");
                    Assert(hpSrc is null || se is not null,
                           "★ 元断言:切得到 `EnsureEdgeAsync` —— 切不到的话下面整组会静默零断言");
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
                    Assert(se is not null && se.Contains("EdgeLogPath"),
                           "★★ 藏窗口就必须把中枢自己吐的话收进日志 —— 窗口可以藏,现场不能丢");
                    // ★★★ 「把日志原文摆到界面上」那一半**搬到了界面**(StartEdgeAsync 删掉时跟过去的),
                    //   所以它要在**那边**钉:钉在这儿会因为找不到而恒假,钉丢了则会静默恒真。
                    Assert(hhv is not null && hhv.Contains("中枢自己打印的最后几行")
                           && hhv.Contains("EdgeLogTail"),
                           "★★ 起不来时,「主机中枢」那一页要把中枢自己吐的最后几行【原文】摆出来 —— "
                           + "窗口可以藏,现场不能丢");
                    // ★★ 判据钉的是**三件事的先后**:先起进程 → 再去探 → 探到了才敢报 Ok。
                    //   ★ 不能拿「`EdgeUpAsync` 的第一次出现」当探测点:`EnsureEdgeAsync` 开头
                    //     那一句 `if (await EdgeUpAsync()) … Skipped`(幂等预检)**排在 Process.Start 之前**,
                    //     拿它去比会让这条判据恒假 —— V22 写第一版时就是这么红的,而红得理由是假的。
                    // ㉑ 起中枢**之前**先看它是不是已经在跑 —— 否则第二个必然撞端口,吐一屏 Kestrel 异常栈。
                    //   ★ 这里要的是 `EdgeUpAsync` 的**第一次**出现(幂等预检,排在 Process.Start 前);
                    //     下面那条要的是**最后一次**(起完之后的探活)。**两条是不同的性质,不能合并**:
                    //     只钉"探过"的话,把预检删掉它照样绿,而症状是每次都起出第二个中枢。
                    var iPre = se?.IndexOf("EdgeUpAsync", StringComparison.Ordinal) ?? -1;
                    Assert(iSpawn >= 0 && iPre >= 0 && iPre < iSpawn,
                           "★★ 拉起中枢前先探一次:已经在跑就别起第二个 —— "
                           + "第二个会撞 address already in use,在黑窗口里吐一整屏异常栈,"
                           + "而人根本读不出「你已经开着一个了」"
                           + $"(实测位置:pre={iPre} spawn={iSpawn})");

                    var iWait = se?.LastIndexOf("WaitUntilAsync", StringComparison.Ordinal) ?? -1;
                    var iOk = se?.IndexOf("SetupOutcome.Ok", StringComparison.Ordinal) ?? -1;
                    Assert(iSpawn >= 0 && iWait > iSpawn && iOk > iWait,
                           "★★ 拉起 ≠ 起来了:必须【起进程 → 去探 → 探到才报 Ok】这个顺序,"
                           + "不许因为 Process.Start 没抛异常就宣布成功"
                           + $"(实测位置:spawn={iSpawn} wait={iWait} ok={iOk})");
                    Assert(se is not null && se.Contains("【不当作成功】"),
                           "★ 到点没等到就如实说 —— 不无限转圈,也不把它记成起来了");
                    // ★ 「重新检测」这个按钮【只能】做它名字说的那件事
                    // ★★ V22:切片的下界从 `UIElement HubDownCard()` 收成 `;`。
                    //   理由是实打实的:V22 在 RecheckRow 与 HubDownCard **之间**插进了「AI 栈」那一格,
                    //   而那一格里有一个「打开中枢日志」按钮(Process.Start)——
                    //   旧的下界会把它一起切进来,于是这条断言会**红,而理由是假的**
                    //   (它会说"重新检测按钮顺手启动了 Edge",而那按钮一个字都没改)。
                    //   RecheckRow 是个单行表达式体成员,切到第一个 `;` 就正好是它整个。
                    Assert(hhv is not null,
                           "★ 元断言:读得到 `Views/HostHubView.cs` —— 读不到的话上下几条会静默恒假");
                    var recheck = hhv is null ? null : Slice(hhv, "UIElement RecheckRow()", ";");
                    Assert(recheck is not null && !recheck.Contains("Process.Start")
                           && !recheck.Contains("EnsureStackAsync") && !recheck.Contains("StackBoot"),
                           "★★ 「重新检测这台的角色」不许顺手启动 Edge —— 按钮必须只做它名字说的那件事");
                }

                // ══════════════════════════════════════════════════════════════
                //  ★★★★ V23 · 救出三片「双重指错」的断言
                //
                //  V22 把「TryReadSource 指向不落盘文件 ⇒ 恒 null ⇒ 整片静默不跑」这个病
                //  **诊断得一字不差**并补了 Skip —— 却没搜一下同一表达式还有几处。
                //  ★ 判词:**一个修法漏改一处,缺陷就完整地留在那一处。**
                //
                //  剩下的三处,而且每一处都**错了两层**:
                //    ⑦  `Services/HostSetup.cs`     —— 路径错(真身在 `..\app\Services\`)· 下辖 7 条
                //    ⑱b `Views/ConfirmDialog.cs`    —— 路径错(真身在 `..\app\Views\`)  · 下辖 1 条
                //    ⑫  sync-over-async 那一组      —— 路径错 **且** 被一个空壳 `if` 吞掉
                //  ★★ 第二层是它们**同时还嵌在 `if (dv4 is not null)` 里面** ——
                //    光把路径改对**它们照样一条都不跑**。这正是"漏改一处"最阴的地方:
                //    修了看得见的那一层,人就以为修完了。
                //  ⇒ 两层一起修:搬出 `dv4` 那个块 + 指到**管理端真的编译进来的那份**
                //    (csproj `<Compile Link>` 了 `..\app\Services\HostSetup.cs` 与
                //     `..\app\Views\ConfirmDialog.cs`,所以断言的是**这个 exe 里真有的代码**)。
                //  ★ 而 `Views/DevicesView.cs` / `Services/HubClient.cs` **没有**被 link 进来
                //    ⇒ 它们不在这里救,救了就是拿管理端自检去测客户端不编译的代码。
                // ══════════════════════════════════════════════════════════════
                // ⑦ 「一次装好这台主机」:三条不可让步的边界
                // ══════════════════════════════════════════════════════════════
                //  ★★★ 读的是 `Services/HostProvision.cs`,**不是** `HostSetup.cs` ——
                //    而这一条是那条元断言当场量出来的,不是我推出来的:
                //    第一版按注释里的说法指向 `..\app\Services\HostSetup.cs`,实跑后
                //    元断言红了(那份文件里没有 `EnsureFirewallAsync`),另外六条跟着红。
                //    ★ 真相是 V21 把 `EnsureIdentityAsync` / `EnsureFirewallAsync` /
                //      `FirewallRuleExistsAsync` **搬到了管理端这一侧**(HostProvision.cs:106),
                //      `HostSetup.cs` 只剩一句注释提到它们的名字。
                //  ⇒ **同一个病在同一片代码上叠了两层**:路径指向不落盘的文件(第一层),
                //    而就算读到了也切不出东西,因为函数早搬走了(第二层)——
                //    与 V22 在 `:1200` 那段记的「双重指错」逐字同形。
                //  ★★ 这就是元断言存在的理由:没有它,这七条会以「防火墙那步没验规则」
                //    这类**假理由**红着,而真因是判据读错了文件。
                // ══════════════════════════════════════════════════════════════
                var hs = hpSrc;   // ★ 上面已经读过同一份,不再读第二遍
                if (hs is null)
                    Skip("「一次装好这台主机」那七条(HostProvision.cs)",
                         "读不到 `Services/HostProvision.cs`(出包产物旁边没有源码)");
                if (hs is not null)
                {
                    var hsBody = Body(hs);
                    // ★ 元断言:读到的**确实**是那份含铸身份/防火墙的源码,不是撞名撞上的别的文件。
                    Assert(hsBody.Contains("EnsureFirewallAsync"),
                           "★ 元断言:读到的确实是含 `EnsureFirewallAsync` 的那一份 —— "
                           + "读错文件时下面六条会**红得给出假理由**(V23 第一版就是这么红的)");
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
                    // ══════════════════════════════════════════════════════════
                    //  ★★ V23 换了钉的那句话,并如实说清为什么(不是嫌它不好看):
                    //    原来钉的是 `铸的时候和用的时候是不是同一个等级` —— 那句话写在
                    //    `HostSetup.cs` 的文件头上,而 V21 把**代码**搬到了 HostProvision.cs,
                    //    **那句话没跟过来**,今天全仓的产品源码里一个字都找不到。
                    //  ⇒ 继续钉它 = 钉一句已经不存在的话(ASSERTION-PITFALLS 第 1 条那个形状)。
                    //  ⇒ 改钉**跟着代码一起搬过来的**那条边界原文(HostProvision.cs 文件头 ②),
                    //    它说的是同一件事:提权的只许是防火墙那一步,身份绝不能继承 High。
                    // ══════════════════════════════════════════════════════════
                    Assert(hs.Contains("一旦继承 High,身份就毁了"),
                           "★ 文件头要写清真正要防的是什么(提权只许给防火墙那一步,身份不能继承 High),"
                           + "免得后人又照着「是不是管理员」那种判据写回去");
                }

                // ⑱b 确认框不许被一个超长的自报名字顶爆
                var cd = TryReadSource(Path.Combine("..", "app", "Views", "ConfirmDialog.cs"));
                if (cd is null)
                    Skip("确认框高度上限那一条(ConfirmDialog.cs)",
                         "读不到 `..\\app\\Views\\ConfirmDialog.cs`(出包产物旁边没有源码)");
                if (cd is not null)
                    Assert(Body(cd).Contains("MaxHeight") && Body(cd).Contains("ScrollViewer"),
                           "★★ 确认框要有高度上限并能滚动 —— 否则一个超长的自报名字就能把按钮顶出屏幕,"
                           + "那是一个由对方决定的界面拒绝服务");

                // ⑫ UI 侧不许 sync-over-async —— 2026-08-04 实机卡死就是一行 .GetAwaiter().GetResult()
                //   在 UI 线程上等一个 async 方法:里面 await 的续体要回 UI 线程,而 UI 线程正卡着。
                //   ★ 允许的写法是先 Task.Run 把它挪出 UI 线程再 GetResult(App.xaml.cs 里那处就是)。
                //   ★★ 名单是**管理端真的编译进来的那几份**,一份不落盘的都不许写进来 ——
                //     写进来的那一条会静默 continue 掉,而名单看着还是"覆盖了四个文件"。
                var syncOverAsyncFiles = new[]
                {
                    Path.Combine("..", "app", "Services", "HostSetup.cs"),
                    Path.Combine("Services", "HubAdmin.cs"),
                    Path.Combine("..", "app", "Services", "HubDiscovery.cs"),
                    Path.Combine("Views", "HostHubView.cs"),
                };
                var soaRead = 0;
                foreach (var f in syncOverAsyncFiles)
                {
                    var src = TryReadSource(f);
                    if (src is null) continue;
                    soaRead++;
                    foreach (var line in Body(src).Split('\n'))
                    {
                        if (!line.Contains("GetAwaiter().GetResult()") && !line.Contains(".Wait()")) continue;
                        Assert(line.Contains("Task.Run("),
                               $"★★ {Path.GetFileName(f)} 里有一处 sync-over-async 没先 Task.Run —— "
                               + "在 UI 线程上这会【直接死锁】(实机卡死过一次):" + line.Trim());
                    }
                }
                // ★ 元断言:名单里的文件**真的读到了**。读到 0 份时上面那个 foreach 一条断言都不做,
                //   而"一条都没做"与"一条违例都没有"在结果上长得一模一样(第 10/13 条那个形状)。
                if (soaRead == 0)
                    Skip("sync-over-async 那一组", "名单里的源码一份都读不到(出包产物旁边没有源码)");
                else
                    Assert(soaRead == syncOverAsyncFiles.Length,
                           $"★★ 元断言:sync-over-async 名单里 {syncOverAsyncFiles.Length} 份源码要**全部**读到"
                           + $"(实得 {soaRead} 份)—— 少一份就是那一份在静默跳过,"
                           + "而名单看上去仍然覆盖着它");


                // ══════════════════════════════════════════════════════════════
                //  ★★★★ V22 实测:**下面这一整片断言,一条都没跑过。**
                //
                //  `dv4` 读的是 `Views/DevicesView.cs`,而 V21 把那个文件改名成
                //  `HostHubView.cs` 并搬掉了一半成员 —— **管理端里没有 DevicesView.cs**。
                //  ⇒ `dv4` 恒为 null ⇒ 这个 `if` 底下的 **69 条断言**全部不执行,
                //    而且**不留痕迹**:不计 PASS、不计 FAIL、连一行 SKIP 都没有。
                //    自检末尾那个 PASS 数看着照常涨,没有任何东西提示这里空了。
                //
                //  ★ 这与本轮要修的 `EnsureStackAsync` 是**同一个病**:
                //    东西写好了、看着在那儿,而**没有任何东西会真的走到它**。
                //    区别只在于这一次躺着的是**断言**,而断言躺下的后果更坏 ——
                //    它让别的缺陷也跟着看不见(V21 那次搬迁就是在这片沉默里做的)。
                //
                //  ★★ 为什么本车道**不**顺手改掉那个文件名:改完实测 PASS 166→210、
                //    FAIL 8→33。多出来的 25 条红,绝大多数钉的是 V21 **有意留在客户端**的成员
                //    (`SelfPairAsync` / `ClientPairCard` / `KnockAsync` …)—— 那要**逐条裁**
                //    「这条该跟去客户端自检、还是本来就该撤」,不是改个文件名了事。
                //    在一条 24 小时的车道里草草改名,只会把 25 条红塞给下一个人,
                //    而且那时它们看起来像是**本轮引入的**。
                //  ⇒ 本轮做两件**够得着且不留假象**的事:
                //    ① 本轮新写的那几条断言已经搬到这个 `if` **外面**(见上面 ⑥);
                //    ② 这里补一条 `Skip`,把"沉默"换成"看得见的空" ——
                //       实测数字与逐条处置写在交回里。
                // ══════════════════════════════════════════════════════════════
                // ══════════════════════════════════════════════════════════════
                //  ★★★★ V23 · 把这条 SKIP 的**文案改准**,以及它原来会怎样害人
                //
                //  上一版这条 SKIP 写的是「V21 已把该文件**改名**为 `HostHubView.cs`」——
                //  **那是假的**:V21 是**拆分**,`app/Views/DevicesView.cs` 今天仍在(491 行)。
                //  `dv4` 恒为 null 的真因是另一回事:`TryReadSource` 是从**管理端源码根**往下找,
                //  而它够不到 `app/Views/`(那是客户端那一半,没有被 csproj link 进管理端)。
                //
                //  ★★★ 为什么这个差别要紧,而不是措辞洁癖:
                //    按「已经改名了」那个说法,这一片是**没东西可读**,读到才奇怪;
                //    按真因,这一片是**读错了地方** —— 只要哪天锚点解析到了
                //    `Views/DevicesView.cs`(例如管理端 exe 的某个祖先目录里出现了同名文件,
                //    出包闸把 exe 拷进 %TEMP% 那类形态正是 PITFALLS 第 9 条那个坑),
                //    这 58 条就会**静默地对着客户端那一半源码跑,红绿都是假的** —— 比现在的 null 更坏。
                //  ⇒ V23 把锚点收到 `AdminSourceRoot()`(Selftest.cs 里那段):
                //    只认「`Selftest.cs` + `localai-admin.csproj` 同时在」的那一级,
                //    一份路过的同名文件配不上这个条件 ⇒「解析到了但解析错了」这条路被堵死。
                //  ★ 而「解析不到」这件事本身由 `RunAnchorTally` 报出来:
                //    在登记表里的算一条**看得见的 SKIP**,不在表里的算 **OWED**(门禁判红)。
                // ══════════════════════════════════════════════════════════════
                var dv4 = TryReadSource(Path.Combine("Views", "DevicesView.cs"));
                if (dv4 is null)
                    Skip("管理端「一键配对 + 配对窗口三道闸」那一片断言",
                         "它们读的是 `Views/DevicesView.cs` —— 那个文件在**客户端**(`app/Views/`,今天仍在、491 行),"
                         + "而本自检的锚点只认管理端源码根 ⇒ 这一片**一条都没跑**。"
                         + "★ 这不是「没什么可测」,是判据指错了地方。"
                         + "重新指向后实测 PASS 166→210 / FAIL 8→33,那 25 条红要逐条裁 —— 协调层已裁:单开车道。");
                if (dv4 is not null)
                {
                    var body4 = Body(dv4);

                    // ① 角色四分,一种都不许合并
                    Assert(Enum.GetValues<Views.HostRole>().Length == 4,
                           "★★ 角色必须是四种:Unknown / Host / HostHubDown / Client —— "
                           + "把 HostHubDown 并进 Client,就又回到「主机上说这台不是主机」那个坑");
                    var probe = Slice(dv4, "async Task ProbeRoleAsync", "/// <summary>手动重探");
                    Assert(probe is not null && probe.IndexOf("LastProbe == AdminProbeResult.Ok", StringComparison.Ordinal)
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
                    Assert(Views.HostHubView.WindowMinutes > 0 && Views.HostHubView.WindowMinutes <= 30,
                           "★ 中枢侧的分钟上限是最后一道闸:客户端崩了窗口也会自己关");
                    Assert(body4.Contains("Unloaded +=") && body4.Contains("CloseWindowAsync(quiet: true)"),
                           "★★ 离开这一页也要关窗 —— 「展开着就走人」不能把窗口留到分钟上限");
                    Assert(body4.Contains("IsVisibleChanged +="),
                           "★ 页面不可见时同样关窗");
                    Assert(body4.Contains("_addPanel = new StackPanel { Visibility = Visibility.Collapsed }"),
                           "★★ 「添加一台新电脑」默认收起 —— 只有主机没有副机的人才不会无意中开窗;"
                           + "展开这个动作本身就是明确意图");

                    // ══════════════════════════════════════════════════════════
                    //  ⑤ 六词比对【不许】被一键化掉
                    //
                    //  ★★★ V19(2026-08-08):这三条**原来钉在死代码上**。
                    //    它们切的是 `ShowApprovalDialogAsync`,而那条路 2026-08-08 前就废了
                    //    (DevicesView 里那段注释白纸黑字:enroll 是**匿名**的,自动弹窗
                    //     等于「局域网上任何人都能触发的动作」⇒ 改成不自动弹)。
                    //    真正在跑的是 `PendingRow` 上的批准/拒绝按钮。
                    //    ⇒ **今天测的是死代码**:把活路径上的六词比对整个删掉,这三条照样绿。
                    //
                    //  ★ 修法不是"把针脚挪过去就完事" —— 判据要**能扛住那两种去留**:
                    //    弹窗接回来(由人主动点某一条才弹)也好、彻底删掉也好,
                    //    「批准之前必须逐字比过六个词」这条判词都得成立。
                    //    ⇒ 所以下面第三条是**遍历所有批准入口**,不是钉某一个函数。
                    // ══════════════════════════════════════════════════════════
                    var pendRow = Slice(dv4, "UIElement PendingRow", "UIElement DeviceRow");
                    Assert(pendRow is not null,
                           "★ 元断言:切得到 `PendingRow`(活的批准路径)—— "
                           + "切不到的话下面三条会**静默变成零断言**");
                    Assert(pendRow is not null && pendRow.Contains("逐字核对过了,批准"),
                           "★★ 批准按钮的文字本身就是那句断言,不是中性的「确定」—— "
                           + "六个词管的是「这条请求是不是你发的」,displayName 是自报的可以随便写");
                    Assert(pendRow is not null && pendRow.Contains("DenyAsync"),
                           "★ 批准那一行要有拒绝这条路,不能只有批准和无视");
                    Assert(pendRow is not null && pendRow.Contains("p.Sas"),
                           "★★ 六个词要**摆在屏幕上**给人对 —— 只写一句「请核对」而不显示词,"
                           + "那句话就没有对象可核");

                    // ★★★ 遍历**所有**批准入口:每一处 `ApproveAsync(` 之前都得有六词比对的字样。
                    //   ★ 这条**不挑函数** —— 挑函数的判据会随那个函数的去留一起失效,
                    //     而这正是上面那三条栽过的地方。
                    //
                    //   ★★★ 有**一个**登记在册的例外:`SelfPairAsync`(本机自配对)。
                    //     它写这条判据时**当场把我抓了一次** —— 第一版不认例外,
                    //     于是对着一条项目**深思熟虑决定不比六个词**的路径判红。
                    //     那种红是误红,而本仓的经验是:**误红的护栏很快就没人看**。
                    //     ⇒ 例外要**登记**,而且例外自己的那道闸要被单独钉住
                    //       (与契约门禁里 `_SUBSHAPE_CIDS` 逐条写明"为什么不抵消欠债"同款手法)。
                    //     它凭什么免:同机走回环,没有中间人可防;能调回环管理面的人已经在这台机器上,
                    //     他本来就能批准任何请求。⇒ 免的是**②确认请求来源**,不是①防中间人(那层仍在)。
                    {
                        var dvCode = NoComments(dv4);
                        var selfPair = Slice(dv4, "async Task SelfPairAsync", "UIElement HostDevicesCard");
                        var selfPairCode = selfPair is null ? "" : NoComments(selfPair);

                        var allSites = System.Text.RegularExpressions.Regex
                            .Matches(dvCode, @"ApproveAsync\s*\(").Count;
                        Assert(allSites >= 2,
                               $"★ 元断言:数得到批准入口(现 {allSites} 处)—— "
                               + "数到 0/1 时下面的判据会静默恒真或把例外算成全部");

                        // ★ 把登记在册的那个例外**整段挖掉**再扫剩下的。
                        var scanned = selfPairCode.Length > 0 ? dvCode.Replace(selfPairCode, "") : dvCode;
                        var scannedSites = System.Text.RegularExpressions.Regex
                            .Matches(scanned, @"ApproveAsync\s*\(").Count;
                        // ★★ 挖掉的**必须恰好是 1 个** —— 挖 0 个说明切片没匹配上(判据白扫),
                        //   挖 ≥2 个说明例外的范围悄悄变大了(那才是真正危险的方向:
                        //   有人把新的批准入口塞进自配对那一段来躲开这条判据)。
                        Assert(allSites - scannedSites == 1,
                               $"★★★ 登记在册的例外必须**恰好挖掉一个**批准入口"
                               + $"(全部 {allSites} · 挖后 {scannedSites})—— "
                               + "挖 0 个 = 切片没匹配上,这条判据在空扫;"
                               + "挖 ≥2 个 = 有人把新的批准入口塞进自配对那一段来躲开它");

                        var bad = 0;
                        foreach (System.Text.RegularExpressions.Match m in
                                 System.Text.RegularExpressions.Regex.Matches(scanned, @"ApproveAsync\s*\("))
                        {
                            var from = Math.Max(0, m.Index - 1200);
                            if (!scanned[from..m.Index].Contains("逐字")) bad++;
                        }
                        Assert(bad == 0,
                               $"★★★ 除登记的自配对例外外,**每一个**批准入口之前都必须有六词【逐字】比对"
                               + $"({bad} 处没有)—— 这条判据不挑函数:弹窗接回来也好、彻底删掉也好,"
                               + "「批准之前逐字比过六个词」都得成立。"
                               + "上一版把它钉在一个具体函数上,而那个函数早就没人调了");

                        // ★★★ 例外**自己的那道闸**:免了六词,就必须当场重探管理面并确认 hubId。
                        //   不能拿几分钟前的探测结果当通行证 —— 那期间中枢可能换了、Edge 可能重起过。
                        //   ⇒ 免六词的**全部依据**就是"这是同一台机器的回环",这条一松,例外就不成立了。
                        Assert(selfPairCode.Length > 0,
                               "★ 元断言:切得到 `SelfPairAsync` —— 切不到的话下面那条会静默零断言,"
                               + "而上面那条「恰好挖掉一个」也会同时红(两条互相兜)");
                        Assert(selfPairCode.Contains("admin.ProbeAsync(TheApp.Hub.Profile?.HubId)")
                               && selfPairCode.Contains("admin.LastProbe != AdminProbeResult.Ok"),
                               "★★★ 自配对免比六个词的**前提**:必须【当场重探】回环管理面并确认 hubId 一致。"
                               + "拿旧结论当通行证,那段免除的理由就不成立了 —— "
                               + "免的是「确认请求来源」,凭的是「这确实是本机的回环」,"
                               + "而那件事只有刚刚探过才知道");
                    }

                    // ★★★ 安全判据:**轮询那条路不许自动弹窗**。
                    //   enroll 是匿名的 ⇒ 自动弹窗 = 局域网上任何人都能决定你屏幕上跳出什么,
                    //   由对方的到达时机说了算。这条与「弹窗要不要回来」**无关**:
                    //   就算接回来,也只能是**人主动点某一条**才弹,不能由轮询触发。
                    //   ★★★ V19(用户裁定 A 之后)判据**换了形状**:
                    //     上一版钉的是「轮询里不许出现 `ShowApprovalDialogAsync`」——
                    //     而那个方法当天被删了 ⇒ 那条判据**永远为真**,成了一条恒真断言,
                    //     而它守的是一条**安全**性质。一条守着安全性质的恒真断言,
                    //     比没有更坏:它会让人以为那件事有人看着。
                    //   ⇒ 改钉**行为**:轮询那一段里不许出现**任何**弹窗调用。
                    //     这样无论将来那个弹窗叫什么名字、由谁写,只要它被轮询触发就会红。
                    var pollForPopup = Slice(dv4, "async Task PollPendingAsync", "// ═════");
                    Assert(pollForPopup is not null,
                           "★ 元断言:切得到 `PollPendingAsync` —— 切不到的话下面那条会静默零断言");
                    {
                        var pollCode = pollForPopup is null ? "" : NoComments(pollForPopup);
                        // ★ 判据是「有没有开窗这个动作」,不认具体名字:
                        //   ConfirmDialog.Show / MessageBox.Show / .ShowDialog() 全算。
                        var popups = System.Text.RegularExpressions.Regex
                            .Matches(pollCode, @"ConfirmDialog\.Show|MessageBox\.Show|\.ShowDialog\s*\(").Count;
                        Assert(pollForPopup is not null && popups == 0,
                               $"★★★ 待批准**轮询**里不许弹窗(现数到 {popups} 处)—— enroll 是匿名的,"
                               + "「新请求一到就自动弹」等于把「你屏幕上跳出什么」交给局域网上的任何人,"
                               + "准入的节奏必须归你,不归发起方。"
                               + "★ 判据钉的是**开窗这个动作**,不是某个方法名 —— "
                               + "上一版钉名字,而那个名字被删掉之后判据就恒真了");
                    }

                    // ⑥ 起中枢那一步的断言已移到本块之外（V22）—— 理由见那儿。
                    // ⑦ 「一次装好这台主机」那七条也已移到本块之外（V23）—— 理由见那儿。
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

                    // ㉑ 「起中枢之前先探一次」那一条**已随 ⑥ 搬到本块之外**(V22)——
                    //    它引用的 `se` / `iSpawn` 是 ⑥ 的局部量,留在这儿连编都编不过。
                    //    ★ 判据本身一个字没让步,只是改钉 `HostProvision.EnsureEdgeAsync`。

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
                    // ★ 下界原来是 `void RenderDevices` —— 那个方法**一个调用方都没有**,已随 V19 删掉。
                    //   ★★ 顺带记一条:拿一段**死代码**当切片边界,等于让一条活断言的范围
                    //     由一段没人调的代码来定 —— 它被删掉的那天,`Slice` 返回 null,
                    //     而 `is not null &&` 会让整条断言**静默变成恒假**…… 不,更糟:
                    //     写成 `x is not null && x.Contains(...)` 时它判红(还好);
                    //     写成 `x?.Contains(...) != false` 那种就会静默转绿。
                    //     ⇒ 边界要挑**结构上不会消失**的东西。这里改用类的收尾分节注释。
                    var isSelfFn = Slice(dv4, "bool IsThisMachine", "// ═════");
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
                    //
                    // ★★★ V19(2026-08-08 用户裁定 A):这一条**也是钉在死代码上的**。
                    //   上一版的针脚是 `rst == 409` —— 而 `rst` 这个名字**只存在于**
                    //   `ShowApprovalDialogAsync` 里,那个方法零调用方、当天被删。
                    //   ⇒ 删掉它的那一刻这条断言当场红,而它守的那件事(409 要说话)
                    //     在活路径上**一直是成立的** —— 也就是说:这条断言此前一直在
                    //     替死代码作证,活路径上那份是"顺便也对",不是被它测出来的。
                    //   ★ 这是同一天里发现的**第 2 条**寄生在这个死方法上的断言
                    //     (另 3 条是六词比对那组)。⇒ 针脚改钉活路径,并且**不依赖变量名**。
                    Assert(pendRow is not null && pendRow.Contains("409")
                           && pendRow.Contains("重新点一次「开始配对」"),
                           "★★ 请求过期时 Approve 回 409,以前两处都丢掉返回值 —— "
                           + "人点了批准、什么反馈都没有,那一行只是悄悄消失");
                    // ★ 批准与拒绝**两条**都要看返回码,不是只看批准那条。
                    //   ★★ 数 `!= 200` 的出现次数,不认变量名 —— 上一版栽的就是认了名字。
                    {
                        var notOk = pendRow is null ? 0 : System.Text.RegularExpressions.Regex
                            .Matches(pendRow, @"!=\s*200").Count;
                        Assert(notOk >= 2,
                               $"★★ 批准与拒绝**各自**都要核返回码(现数到 {notOk} 处 `!= 200`)—— "
                               + "只核一条的话,另一条失败时界面一个字都不说,"
                               + "而失败的吊销/拒绝与成功的长得一模一样");
                    }
                    // ⑰ 不许"一有请求就自动弹窗" —— enroll 是匿名的,那等于把弹窗交给局域网上任何人触发
                    //   ★ 上一版针脚是 `_popped.Add(p.RequestId)`(自动弹窗那条路的去重集合)。
                    //     那个字段随弹窗一起删了 ⇒ 这条判据会**永远为真**,变成一条恒真断言。
                    //     ⇒ 改成钉**行为**:轮询那一段里不许出现任何弹窗调用。见下面「轮询里不许弹窗」那条。
                    Assert(!body4.Contains("_popped"),
                           "★ 自动弹窗那条路的伴生状态(_popped)不许回来 —— "
                           + "它一回来就说明有人在按到达时机弹窗。"
                           + "★ 这条只是**顺带**:真正的判据是下面那条「轮询里不许弹窗」,"
                           + "它钉的是行为,不是某个字段名");
                    // ⑱ 自报显示名不许决定窗口尺寸
                    Assert(body4.Contains("SafeDisplayName("),
                           "★ 自报的显示名要截断 + 剔控制字符再上界面");
                    var safeFn = Slice(dv4, "static string SafeDisplayName", "/// <summary>手填入口");
                    Assert(safeFn is not null && safeFn.Contains("char.IsControl") && safeFn.Contains("48"),
                           "★ 剔控制字符并截断");
                    // ⑱b 确认框那一条也已移到本块之外（V23）—— 理由见那儿。
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
                    // ⑮ 「链不通 ≠ 过期」那条断言**在某次搬迁里被删掉了,而它的 `if` 留了下来** ——
                    //   于是 `if (hcCls is not null)` 底下**没有自己的语句**,它一路吞掉了紧跟其后的
                    //   ⑫ 那整个 `foreach`(C# 里 if 的主体就是下一条语句,中间隔多少注释都不算)。
                    //   ⇒ V23 删掉这个空壳,并把 ⑫ 搬到本块之外(它读的文件与 DevicesView 无关)。
                    // ⑫ sync-over-async 那一组也已移到本块之外（V23）—— 理由见那儿。

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
                var ha = new HubAdmin();
                Assert(HubAdmin.DefaultAdminPort == 8442,
                       "★ 回环管理端口与 lan-edge 的 AdminPort 常量一致 —— 对不上的表现是「主机上也说不是主机」");
                Assert(!ha.Available && ha.HubId is null,
                       "★ 没探测过之前一律【不可用】—— 管理面的可达性是探出来的,不是假设出来的");
                // ★ 探一个必定连不上的端口:必须【如实说不可用】,不许 fail-open 成"可用"
                Environment.SetEnvironmentVariable("LOCALAI_ADMIN_PORT", "1");
                var ha1 = new HubAdmin();
                var probed = System.Threading.Tasks.Task.Run(async () => await ha1.ProbeAsync("whatever")).GetAwaiter().GetResult();
                Assert(!probed, "★★ 连不上就是连不上 —— 管理面探测 fail-closed(连不上却说可用 = 界面给出根本点不动的按钮)");
                // ★★ 失败要被【分类】。2026-08-03 的真事:主机那台自己没启动 lan-edge,
                //   界面把"回环没人听"直接说成「这台不是主机」,人就去怀疑配错了机器。
                //   分类存在的全部意义就是让界面能说【观察到的事】,而不是替它下一个证明不了的结论。
                Assert(ha1.LastProbe == AdminProbeResult.NotListening,
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
                        Assert(AdminApp.HostToolsDirNextTo(client) is null,
                               "★ 旁边有 host 目录但【没有那个 exe】时仍返回 null —— 线索要看到真东西才算数");
                        File.WriteAllText(Path.Combine(host, "localai-lan-edge.exe"), "x");
                        Assert(AdminApp.HostToolsDirNextTo(client) is { } got
                               && string.Equals(Path.GetFullPath(got).TrimEnd('\\'), Path.GetFullPath(host).TrimEnd('\\'),
                                                StringComparison.OrdinalIgnoreCase),
                               "★★ 旁边真有主机端程序时要找得到 —— 这条线索是"
                               + "「主机但 Edge 没启动」与「这台真不是主机」的唯一分界");
                        Assert(AdminApp.HostToolsDirNextTo(null) is null
                               && AdminApp.HostToolsDirNextTo("  ") is null,
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
                Assert(HubAdmin.EdgePort == 8443, "业务口端口与 lan-edge 的 run-lan 一致");
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

                // ---- 记忆面板:判据四项里此前缺的【编辑】与【溯源展开】----
                var mcMem = new MemoryCenter();
                var mid = MemoryCenter.NewId();
                mcMem.Add(new MemoryEntry(mid, "原标题", "原正文", MemoryKind.Summary,
                        ProjectScope.Personal, MemberContext.Current, "p-1",
                        new[] { "s-1", "s-2" }, DateTime.Now));
                Assert(!mcMem.EditText(mid, "   ", "x"), "★ 标题空了不许保存 —— 列表里会变成一条没名字的东西");
                Assert(mcMem.EditText(mid, "改过的标题", "改过的正文"), "★ 记忆条目可编辑(P3c 判据四项之一)");
                var afterEdit = mcMem.Find(mid)!;
                Assert(afterEdit.Title == "改过的标题" && afterEdit.Body == "改过的正文", "改的内容真的写进去了");
                Assert(afterEdit.EditedByHuman && afterEdit.EditedAt is not null,
                       "★★ 人手改过要打标记 —— 不标的话人改的内容会以【AI 摘要】的身份进 prompt,那是骗下游");
                Assert(afterEdit.Scope == ProjectScope.Personal && afterEdit.SourceProjectId == "p-1"
                       && afterEdit.SourceSessionIds!.Count == 2,
                       "★ 编辑只动标题与正文:范围是权限动作、来源是溯源锚,都不许在编辑框里悄悄改");
                Assert(!mcMem.EditText(mid, "改过的标题", "改过的正文"), "没变就不写、不广播(免得白落一次盘)");
                Assert(!mcMem.EditText("no-such-id", "a", "b"), "改一条不存在的记忆:老实返回 false");

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

                    // ★★ 主机侧轮换告警的**措辞**:运行时直接问那个属性,不去源码里找字符串。
                    //   只说"需要注意"的告警等于没说 —— 必须带剩余天数和该做什么。
                {
                    var scWarn = new HubAdmin();
                    Assert(scWarn.ServerCertWarning is null,
                           "★ 反向:还没探测过 ⇒ 不报主机证书告警(不编一个出来)");

                    // ---- 主机侧轮换的 fail-closed 最后一段路:界面真的读了 ----
                    var dvCert = TryReadSource(Path.Combine("Views", "HostHubView.cs"));
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

                    var parsed = HubAdmin.ParseServerCert(JsonDocument.Parse(pingJson).RootElement);
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
                        Assert(HubAdmin.ParseServerCert(JsonDocument.Parse(partial).RootElement) is null,
                               $"★★ 反向:serverCert 少了 `{drop}` ⇒ 判 null,不拼一份半截状态出来");
                    }
                    // ★★ 反向二:老中枢根本不报 serverCert ⇒ null,而不是编一个"健康"出来
                    Assert(HubAdmin.ParseServerCert(
                               JsonDocument.Parse("{\"ok\":true,\"hubId\":\"x\"}").RootElement) is null,
                           "★★ 反向:主机没报 serverCert(老中枢/没装轮换器)⇒ null,不假装它健康");

                    // ★★ 元断言:登记表里凡是**客户端要解析**的契约,这里都得有对应的断言。
                    //   renew/* 两条的客户端解析在 transport selftest 里端到端测(那边有真的 Edge),
                    //   所以这里只认 /admin/ping 那两条 —— 但**数目由表算出来**,不写死。
                {
                    var clientSide = LocalAI.Identity.WireContracts.All
                                     .Where(c => c.Name.StartsWith("GET /admin/ping", StringComparison.Ordinal)).ToArray();
                    Assert(clientSide.Length == 2,
                           $"★★ 元断言:登记表里归本处核对的契约有 {clientSide.Length} 条(表里新增 /admin/ping 相关的键组就得在这儿补断言)");
                }

                // ══════════════════════════════════════════════════════════════
                //  ▼▼▼ V4(契约欠债 · 证书/配对切片)—— 本段【只追加】,上面一律没动 ▼▼▼
                //  客户端半边:/admin/* 那 7 条(含 2 条元素子形状 + 1 条 409 失败分支)。
                //  服务端半边在 10-core/lan-edge/Program.cs 的「D96 丁」节(真 HTTP)。
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
                    var pg = HubAdmin.ParsePing(E(pingJson2));
                    Assert(pg.ok && pg.hubId == "hub-1" && pg.windowOpen,
                           "★★★ CONTRACT:cert.admin.ping 客户端半边:拿登记表生成的形状,真解析器解得出 hubId 与 pairingWindowOpen");
                    pinnedV4.Add("CONTRACT:cert.admin.ping");
                    foreach (var drop in LocalAI.Identity.WireContracts.AdminPing)
                        Assert(!HubAdmin.ParsePing(E(Obj(
                                   LocalAI.Identity.WireContracts.AdminPing.Where(x => x != drop).ToArray()))).ok,
                               $"★★ 反向 cert.admin.ping:少了 `{drop}` ⇒ 判失败,不拼半份出来");

                    // ── CONTRACT:cert.admin.ping.servercert(V1 已钉解析,这里补契约号)──
                    Assert(HubAdmin.ParseServerCert(E(
                               "{\"serverCert\":" + Obj(LocalAI.Identity.WireContracts.AdminPingServerCert,
                                   ("daysLeft", "3.5"), ("consecutiveFailures", "2"), ("needsAttention", "true")) + "}")) is not null,
                           "★★★ CONTRACT:cert.admin.ping.servercert 客户端半边:登记表生成的子对象解得出");
                    pinnedV4.Add("CONTRACT:cert.admin.ping.servercert");

                    // ── CONTRACT:cert.admin.devices(+ .item)──────────────────
                    var devJson = Obj(LocalAI.Identity.WireContracts.AdminDevices,
                        ("devices", "[" + Obj(LocalAI.Identity.WireContracts.AdminDevicesItem,
                                              ("deviceId", "\"dev-1\""), ("displayName", "\"PC-A\""), ("status", "\"active\"")) + "]"));
                    var dvV4 = HubAdmin.ParseDevices(E(devJson));
                    Assert(dvV4.ok && dvV4.list.Count == 1 && dvV4.list[0].DeviceId == "dev-1"
                           && dvV4.list[0].DisplayName == "PC-A" && dvV4.list[0].CertShort == "ab12cd34",
                           "★★★ CONTRACT:cert.admin.devices 客户端半边:顶层 + 元素两层都解得出目标字段(" + (dvV4.why ?? "ok") + ")");
                    pinnedV4.Add("CONTRACT:cert.admin.devices");
                    // ★★ 元素那一层才是承重的 —— A1 的病灶就是"字段藏在下一层"
                    foreach (var drop in LocalAI.Identity.WireContracts.AdminDevicesItem)
                        Assert(!HubAdmin.ParseDevices(E(Obj(LocalAI.Identity.WireContracts.AdminDevices,
                                   ("devices", "[" + Obj(LocalAI.Identity.WireContracts.AdminDevicesItem
                                                         .Where(x => x != drop).ToArray()) + "]")))).ok,
                               $"★★ 反向 cert.admin.devices.item:元素少了 `{drop}` ⇒ 整条判失败,不拼一台没名字的设备出来");
                    pinnedV4.Add("CONTRACT:cert.admin.devices.item");
                    // ══════════════════════════════════════════════════════════
                    //  ★★★ V21 判词改写(不是改宽,是**改对**):
                    //  原文钉的是「`HubClient.ParseDevices` 与 `HubAdmin` 共用同一处解析」——
                    //  那是 V4 修「两个解析器」时留下的验收。
                    //  今天那**第二个消费者整组删掉了**(V10 §2.4:它打的是副机结构上永远 404
                    //  的路由)⇒ 判词本身失效了:没有第二处,自然谈不上「共用」。
                    //  ★ 留着原判词只有两种下场:要么恒红(类型都没了),
                    //    要么被人顺手改成一句轻的。两个都不行。
                    //  ⇒ 改成钉**今天真正要守的那件事**:唯一那处解析器
                    //    认不出形状时必须**判失败**,不返回一份空表。
                    //    (空表在界面上会被写成「没有别的设备」—— 一句看起来很有信息量的假答案。)
                    // ══════════════════════════════════════════════════════════
                    var badItem = E("{\"devices\":[{\"deviceId\":\"x\"}]}");
                    var badParsed = HubAdmin.ParseDevices(badItem);
                    Assert(!badParsed.ok && badParsed.list.Count == 0 && badParsed.why is { Length: > 0 },
                           "★★★ 认不出的设备表形状 ⇒ **判失败并说出原因**,不返回一份空表 —— "
                           + "空表会被界面写成「没有别的设备」,而人会据此把在册的机器再配一次");
                    Assert(HubAdmin.ParseDevices(E(devJson)).ok,
                           "★★ 而合法形状照常解得出(反向:上面那条不是恒失败)");

                    // ── CONTRACT:cert.admin.pending(+ .item)──────────────────
                    var pendJson = Obj(LocalAI.Identity.WireContracts.AdminPending,
                        ("pending", "[" + Obj(LocalAI.Identity.WireContracts.AdminPendingItem,
                                              ("requestId", "\"req-1\"")) + "]"));
                    var pd = HubAdmin.ParsePending(E(pendJson));
                    Assert(pd.ok && pd.list.Count == 1 && pd.list[0].RequestId == "req-1"
                           && pd.list[0].Sas.Length == 6 && pd.list[0].SecondsLeft == 180 && pd.windowOpen,
                           "★★★ CONTRACT:cert.admin.pending 客户端半边:六个词与倒计时都解得出(" + (pd.why ?? "ok") + ")");
                    pinnedV4.Add("CONTRACT:cert.admin.pending");
                    foreach (var drop in LocalAI.Identity.WireContracts.AdminPendingItem)
                        Assert(!HubAdmin.ParsePending(E(Obj(LocalAI.Identity.WireContracts.AdminPending,
                                   ("pending", "[" + Obj(LocalAI.Identity.WireContracts.AdminPendingItem
                                                         .Where(x => x != drop).ToArray()) + "]")))).ok,
                               $"★★ 反向 cert.admin.pending.item:元素少了 `{drop}` ⇒ 判失败 —— "
                               + "`sas` 漂掉会让界面显示**空的六个词**,而人会以为还没生成");
                    pinnedV4.Add("CONTRACT:cert.admin.pending.item");

                    // ── CONTRACT:cert.admin.revoke ────────────────────────────
                    var rvV4 = HubAdmin.ParseRevoke(E(Obj(LocalAI.Identity.WireContracts.AdminRevoke)));
                    Assert(rvV4.ok && rvV4.generation == 7,
                           "★★★ CONTRACT:cert.admin.revoke 客户端半边:generation 解得出(它是吊销真落盘的凭据)");
                    pinnedV4.Add("CONTRACT:cert.admin.revoke");
                    Assert(!HubAdmin.ParseRevoke(E("{\"ok\":false,\"generation\":7}")).ok,
                           "★★ 反向:ok=false ⇒ 判失败(一次失败的吊销不许和成功的长得一样)");
                    Assert(!HubAdmin.ParseRevokeBody("not json").ok,
                           "★★ 反向:正文不是 JSON ⇒ 判失败,不当成功");

                    // ── CONTRACT:cert.admin.approve / deny / 409 ──────────────
                    Assert(HubAdmin.ParseAck(E(Obj(LocalAI.Identity.WireContracts.AdminApprove))).ok,
                           "★★★ CONTRACT:cert.admin.approve 客户端半边:200 形状解得出");
                    pinnedV4.Add("CONTRACT:cert.admin.approve");
                    Assert(HubAdmin.ParseAck(E(Obj(LocalAI.Identity.WireContracts.AdminDeny))).ok,
                           "★★★ CONTRACT:cert.admin.deny 客户端半边:200 形状解得出");
                    pinnedV4.Add("CONTRACT:cert.admin.deny");
                    var ack409 = HubAdmin.ParseAck(E(Obj(LocalAI.Identity.WireContracts.AdminApproveDeny409,
                                                                 ("ok", "false"), ("error", "\"这条请求已经不是 pending 了\""))));
                    Assert(!ack409.ok && ack409.error is { Length: > 0 } && ack409.why is null,
                           "★★★ CONTRACT:cert.admin.approvedeny.409 客户端半边:**失败分支**的原因解得出 —— "
                           + "只看状态码的话界面只能写「中枢拒绝了」,人会去重启一个没病的中枢");
                    pinnedV4.Add("CONTRACT:cert.admin.approvedeny.409");
                    Assert(HubAdmin.ParseAck(E("{\"okay\":true}")).why is not null,
                           "★★ 反向:两组键都不是 ⇒ 如实报读不懂,不猜");

                    // ── CONTRACT:cert.admin.window ────────────────────────────
                    var wnV4 = HubAdmin.ParseWindow(E(Obj(LocalAI.Identity.WireContracts.AdminWindow)));
                    Assert(wnV4.ok && wnV4.windowOpen,
                           "★★★ CONTRACT:cert.admin.window 客户端半边:中枢自报的窗口状态解得出 —— "
                           + "读不出就只能拿本地布尔替中枢记,而那是本文件明令禁止的");
                    pinnedV4.Add("CONTRACT:cert.admin.window");
                    Assert(!HubAdmin.ParseWindow(E("{\"ok\":true}")).ok,
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
                    // ── ★★ 半套状态必须能被认出来(起了网关没起 Edge 是最坏的中间态)──
                {
                    var okStep = new SetupStep("网关", SetupOutcome.Ok, "");
                    var badStep = new SetupStep("Edge", SetupOutcome.Failed, "没起来");
                    Assert(new HostProvision.StackResult(okStep, badStep).HalfUp,
                        "★★★ 起了一半 ⇒ HalfUp 为真(它和「全没起来」的下一步不同,必须分开)");
                    Assert(!new HostProvision.StackResult(okStep, okStep).HalfUp
                           && new HostProvision.StackResult(okStep, okStep).AllUp,
                        "★ 两个都起来了才算全起");
                    Assert(!new HostProvision.StackResult(badStep, badStep).HalfUp
                           && !new HostProvision.StackResult(badStep, badStep).AllUp,
                        "★ 两个都没起 ⇒ 不是半套,是全没起");
                    Assert(new HostProvision.StackResult(
                               new SetupStep("网关", SetupOutcome.Skipped, ""), okStep).AllUp,
                        "★★ Skipped(本来就在跑)算**成**,不算失败 —— 幂等的那一半");

                    // ════════════════════════════════════════════════════════
                    //  ★★★★ 元断言(V22 · D115):
                    //    **凡被注释称为「唯一入口」的函数,必须有生产调用点。**
                    //
                    //  ★ 为什么够格立这一条:**第四次**同形了 ——
                    //    A5 的 `TlsFailure` · doctor ⑫ 环 · `loader.shutdown()` ·
                    //    `HostProvision.EnsureStackAsync`。形状每次都一样:
                    //    **函数写好、有文档、有自检、零生产调用点**。
                    //
                    //  ★★ 而上面那一整段断言**测不到它**:它们验的是 `StackResult` 的逻辑,
                    //    而那个逻辑在 V22 之前**永远不会被界面触发**。
                    //    一条只被自检引用的函数,在自检眼里和一条真在跑的函数长得一模一样 ——
                    //    所以判据必须**把自检自己排除在"调用点"之外**,否则它恒真。
                    //
                    //  ★★★ 判据能为假(红测记录在决议包里):
                    //    把 `StackBoot.cs` 里那一行 `HostProvision.EnsureStackAsync(...)` 删掉
                    //    ⇒ 本条当场红。
                    // ════════════════════════════════════════════════════════
                    // ════════════════════════════════════════════════════════
                    //  ★★★★ V25:上面那条 SKIP **撤掉了** —— 通则终于满足自己。
                    //
                    //  它当年逐字登记的是:「`safe_to_stop_stack` 生产调用点 0、没有 HTTP 路由,
                    //  所以 `StackStop.QueryAsync` 恒返回 `Known=false`」,并注明
                    //  「修它要动 `10-core/gateway/**`(当时的禁区)⇒ 本轮只登记不修」。
                    //  V25 把那条路由开出来了(`GET /v1/stack/safe-to-stop`),
                    //  `QueryAsync` 现在**真的发一次请求**,SKIP 的前提整个消失。
                    //
                    //  ★★ 换上的不是一条"看起来像"的断言,是这条契约的**客户端半边**:
                    //    服务端在 `10-core/gateway/test_gpu_broker.py` 钉顶层键集合,
                    //    这里钉「拿那个形状**解析得出目标字段**」——
                    //    两半共用锚点 CONTRACT:stack.safe_to_stop(D92 元规则,审计 A1 的病灶)。
                    //  ★★★ 承重的那一格是**倒数第二条**:`known=false` 与 `can_stop=false`
                    //    必须解析成**两件不同的事**。把它们合并的实现能过前面每一条,
                    //    唯独过不了那一条 —— 而合并正是本仓最爱犯的那个错
                    //    (「读不到」伪装成「不能关」⇒ 又一条恒假判据)。
                    // ════════════════════════════════════════════════════════
                    {
                        // 正向:中枢说"读到了、可以关"
                        var okBody = "{\"known\":true,\"can_stop\":true,\"why\":\"没有点名组件的租约\","
                                   + "\"blocking\":0,\"resident\":0}";
                        var vOk = StackStop.ParseVerdict(okBody);
                        Assert(vOk.Known && vOk.Safe && vOk.Why.Contains("租约"),
                            "★★★ CONTRACT:stack.safe_to_stop 客户端半边:【可以关】那个形状解得出"
                            + "(理由原样带出来 —— 弹窗要把它摆到人面前)");

                        // 正向:中枢说"读到了、不能关"
                        var noBody = "{\"known\":true,\"can_stop\":false,\"why\":\"有 2 份租约点名了组件\","
                                   + "\"blocking\":2,\"resident\":0}";
                        var vNo = StackStop.ParseVerdict(noBody);
                        Assert(vNo.Known && !vNo.Safe && vNo.Why.Contains("2 份租约"),
                            "★★★ 【不能关】那个形状也解得出,且说得出**是哪一条**理由 —— "
                            + "合并成一句「条件不满足」的话,人再也不知道该修哪一条");

                        // ★★★ 承重格:中枢自己说"我读不到 Broker"(known=false)
                        var unkBody = "{\"known\":false,\"can_stop\":false,"
                                    + "\"why\":\"读不到 Broker 状态(RuntimeError)\","
                                    + "\"blocking\":null,\"resident\":null}";
                        var vUnk = StackStop.ParseVerdict(unkBody);
                        Assert(!vUnk.Known,
                            "★★★★ known=false ⇒ 判【读不到】,**不是**判【不能关】。"
                            + "两者在弹窗上说两句不同的话:前者「你自己判断」,后者「副机正在用」。"
                            + "★ 把两个键合并成一个布尔的实现,前面每一条都能过,唯独过不了这一条");
                        Assert(vUnk.Why.Contains("Broker"),
                            "★★ 而且带出的是**中枢给的那句原因**(Broker 读不到),"
                            + "不是我们自己编的措辞 —— 编一句会盖掉真正的下一步");
                        Assert(StackStop.ConfirmText(vUnk) != StackStop.ConfirmText(vNo),
                            "★★★ 两种处境的**弹窗文案**也必须不同(D99 裁定④:给错原因比不给更坏)");

                        // 反向:三个承重键各缺一次 ⇒ 一律判读不懂,不给默认值
                        foreach (var (bad, name) in new[]
                        {
                            ("{\"can_stop\":true,\"why\":\"w\"}", "known"),
                            ("{\"known\":true,\"can_stop\":true}", "why"),
                            ("{\"known\":true,\"why\":\"w\"}", "can_stop"),
                        })
                        {
                            var v = StackStop.ParseVerdict(bad);
                            Assert(!v.Known,
                                $"★★ 反向 CONTRACT:stack.safe_to_stop:少了 `{name}` ⇒ 判读不懂。"
                                + "缺键就给默认值的话,一次**解析失败**会长得和一次"
                                + "**「有人在用」**一模一样,而这两件事的下一步完全不同");
                        }
                        Assert(!StackStop.ParseVerdict("not json").Known
                               && !StackStop.ParseVerdict("[1,2]").Known,
                            "★★ 反向:正文不是 JSON 对象 ⇒ 如实报读不懂,不猜、不抛");
                        Assert(StackStop.ParseVerdict("not json").Why.Contains("读不到"),
                            "★ 读不懂时措辞仍带【读不到】—— ConfirmText 的三分支判据靠它");
                    }

                    {
                        var cwRoot = ClientWinRoot();
                        if (cwRoot is null)
                        {
                            // ★ 发布产物旁边没有源码 —— 第 11 条:那一趟要【跳过】,不是判红。
                            Skip("元断言「唯一入口必须有生产调用点」",
                                 "读不到 20-client-win 源码树(发布产物旁边没有源码)");
                        }
                        else
                        {
                            var prod = ProductionCsFiles(cwRoot);
                            var claims = SoleEntryClaims(prod, cwRoot);

                            // ★★ 零命中判红:扫不到任何声明 = 判据失效,而失效会**静默变绿**。
                            //   (今天实际扫到的:HostProvision.EnsureStackAsync 与 Strings.Get。)
                            Assert(claims.Count >= 2,
                                   $"★★★ 元断言的元断言:全仓「唯一入口」声明只扫到 {claims.Count} 条(要 ≥2)—— "
                                   + "扫不到就说明这条判据自己坏了(措辞换了 / 文件枚举错了),"
                                   + "而坏掉的表现是**它会一直绿**");
                            Assert(prod.Count >= 20,
                                   $"★★ 生产源码只枚举到 {prod.Count} 个 .cs(要 ≥20)—— "
                                   + "枚举不到文件的话,下面每一条的「调用点」都会是 0,红得理由是假的");

                            foreach (var c in claims)
                            {
                                var n = QualifiedCallSites(prod, c.Method);
                                Assert(n >= 1,
                                       $"★★★★ 「唯一入口」必须有生产调用点:`{c.Method}` 被 {c.File}:{c.Line} "
                                       + "的注释称为唯一入口,而**全仓生产源码里一个调用点都没有**"
                                       + "(自检里的引用【不算】—— 那正是这条要抓的形状:"
                                       + "函数写好、有文档、有自检,而界面上没有任何东西会触发它)。"
                                       + "★ 要么把它接到真正会跑的那条路上,要么把「唯一入口」这句话改掉。");
                            }
                        }
                    }

                    // ════════════════════════════════════════════════════
                    //  ★★★ D? · 自动起栈必须起出一个**本机客户端用得上**的中枢
                    //
                    //  实测过的病(2026-08-07):自动起栈用 `run`,而 `run` 走
                    //  `Program.cs` 的 `Run()`,那里建 EdgeConfig 时**不传 AdminPort**
                    //  ⇒ 回环管理面根本没绑。于是:
                    //    · ProbeRoleAsync 判 HostHubDown ⇒ 只渲染 HubDownCard
                    //    · SelfPairAsync 的唯一调用点在 HostSelfCard 里 ⇒ **结构上永不触发**
                    //  而 EnsureEdgeAsync 当时探的是 `127.0.0.1:8443`(run 模式下**确实开着**)
                    //  ⇒ 它报「已起并探到 8443」,一路绿灯。**失败与成功长得一模一样。**
                    // ════════════════════════════════════════════════════

                    // ── ① 行为:起栈的"起来了没"必须问【管理面】那个口,不是业务口 ──
                    //   ★ 这条是**行为**判据,不是"源码里有没有那个词"(第 9 条坑):
                    //     真开一个监听、真让 EdgeUpAsync 去探,看它探的是哪个口。
                    Assert(HubAdmin.AdminPort != HubAdmin.EdgePort,
                        "★ 管理口与业务口本来就是两个口 —— 相等的话下面那条判据区分不了任何东西");
                    var envSaved = Environment.GetEnvironmentVariable("LOCALAI_ADMIN_PORT");
                    System.Net.Sockets.TcpListener? la = null, lb = null;
                    try
                    {
                        // ★★ 端口要【先绑住再读号】,不许"读一个空闲号 → 放掉 → 过会儿再绑回去" ——
                        //   那中间有一段谁都能抢走的窗口,实测过一次假红(第一遍绿、第二遍红)。
                        //   ⇒ 两个监听都**一直绑着**,要制造"没人听"就把其中一个 Stop 掉。
                        System.Net.Sockets.TcpListener Listen()
                        {
                            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                            l.Start();
                            return l;
                        }
                        lb = Listen();                       // "别的口",全程听着
                        la = Listen();                       // 冒充管理口
                        int portA = ((System.Net.IPEndPoint)la.LocalEndpoint).Port;
                        Environment.SetEnvironmentVariable("LOCALAI_ADMIN_PORT", portA.ToString());

                        // ① 管理口上有人听 ⇒ 必须说【起来了】。
                        var upRightPort = System.Threading.Tasks.Task.Run(
                            async () => await HostProvision.EdgeUpAsync(800)).GetAwaiter().GetResult();
                        Assert(upRightPort,
                            "★★★ 回环**管理面**答话了才算中枢起来了 —— 那正是角色判定与自配对"
                            + "接下来要用的那个口(两者都只走 127.0.0.1:AdminPort)");

                        // ② 把管理口停掉,"别的口"照样听着 ⇒ 必须说【没起来】。
                        //   ★ 这一条同时钉住"没在探 8443":这台机器上 8443 可能正有中枢在跑,
                        //     而它**不许**因此说成起来了。
                        la.Stop(); la = null;
                        var upWrongPort = System.Threading.Tasks.Task.Run(
                            async () => await HostProvision.EdgeUpAsync(800)).GetAwaiter().GetResult();
                        Assert(!upWrongPort,
                            "★★ 别的口上有人听**不算**中枢起来了 —— 原来那一版探 8443,"
                            + "而 `run` 模式下 8443 确实开着、管理面却没绑,于是"
                            + "「起栈成功」与「客户端根本用不了」长得一模一样");
                    }
                    finally
                    {
                        try { la?.Stop(); } catch { }
                        try { lb?.Stop(); } catch { }
                        Environment.SetEnvironmentVariable("LOCALAI_ADMIN_PORT", envSaved);
                    }

                    // ── ② 行为:选绑定地址要么给出一个**本机真有**的地址,要么说清为什么没有 ──
                    var pickedIp = HostProvision.PickBindIp(out var pickWhy);
                    Assert(pickWhy is { Length: > 10 },
                        "★★ 选没选出来都要说得出理由 —— 这一步失败会让整台主机起不来栈,"
                        + "而一句「选不出来」不足以让人知道下一步该干什么");
                    Assert(pickedIp is null || HubAdmin.LocalIPv4List().Contains(pickedIp),
                        "★★★ 选出来的必须是**本机真的有**的网卡地址 —— "
                        + "绑一个不存在的地址会让 Edge 起不来,而症状是「起了但连不上」,极难查");
                    Assert(pickedIp is null || !pickedIp.StartsWith("127.", StringComparison.Ordinal),
                        "★★★ **绝不能选回环**:业务口绑回环时 DiscoverEdgeDialsAsync 逐张网卡去找"
                        + "(它明确跳过回环)⇒ 一个都找不到,自配对停在「网卡地址上都没人在 8443 上听」。"
                        + "这是那条链上的**第二道闸**,只补管理面那一道是不通的");

                    // ── ③ 结构:自动起栈用 run-lan,不是 run ──
                    //   ★ 这一条**只能**写成结构判据:要变成行为判据就得在自检里真起一个中枢
                    //     (要身份、要证书、要端口),那不是自检该做的事。所以如实标成结构判据,
                    //     并且钉得**够窄**:既要有 run-lan,也要确认那个光秃秃的 `"run"` 不在了。
                    //   ★★ 与本文件其它源码判据同款,**必须**用 `if (src is not null)` 兜住:
                    //     出厂产物旁边没有源码,读不到时这几条要【跳过】而不是判红。
                    //     (写成 `Assert(slice is not null && …)` 的那一版实测在
                    //      `build-client.ps1` 的「发布产物原位自检」那一趟直接三条红。)
                    // ══════════════════════════════════════════════════════════
                    //  ★★★★ V22:这四条**一次都没跑过**,而且是**双重指错**:
                    //    ① `TryReadSource("Services/HostSetup.cs")` 沿 BaseDirectory 找**磁盘上**的文件,
                    //       而 `HostSetup.cs` 的真身在 `app/Services/`,靠 csproj 的
                    //       `<Compile Link>` 进管理端 —— **它不落盘**。⇒ `hsSrc` 恒为 null。
                    //    ② 就算读到了也切不出东西:`EnsureEdgeAsync` 在 V21 已经从
                    //       `HostSetup.cs` **搬进了 `HostProvision.cs`**。
                    //  ⇒ 后果:`if (hsSrc is not null)` 静默跳过 ——
                    //    不计 PASS、不计 FAIL、**连 SKIP 都不计**。「零命中与全清白长得一模一样」。
                    //  ★ 而它守的恰恰是本轮动的那块:「自动起栈必须用 run-lan,不许退回 run」。
                    //  ⇒ 改读**真的在管理端磁盘上、而且真的含 EnsureEdgeAsync** 的那个文件,
                    //    并且读不到时**明着 Skip**,不许再静默。
                    // ══════════════════════════════════════════════════════════
                    var hsSrc = TryReadSource(Path.Combine("Services", "HostProvision.cs"));
                    if (hsSrc is null)
                        Skip("「自动起栈必须用 run-lan」那四条",
                             "读不到 `admin/Services/HostProvision.cs`(发布产物旁边没有源码)—— "
                             + "★ 这一档以前是**静默跳过**的,连 SKIP 都不计;现在它至少说得出自己没跑。");
                    if (hsSrc is not null)
                    {
                        var ensureEdge = Slice(hsSrc, "static async Task<SetupStep> EnsureEdgeAsync",
                                               "static string ExitNote");
                        // ★ 元断言:切得到。切不到的话下面四条会全部恒假,而红的理由是假的。
                        Assert(ensureEdge is not null,
                            "★ 元断言:切得到 `EnsureEdgeAsync` —— 切不到的话下面四条会红得给出假理由");
                        Assert(ensureEdge is not null && ensureEdge.Contains("\"run-lan \""),
                            "★★★ 自动起栈必须用 `run-lan <ip>` —— `run` 那条路上 lan-edge 不绑管理面"
                            + "(EdgeConfig 的 AdminPort 形参默认 0),主机上的客户端会被自己判成「不是主机」");
                        Assert(ensureEdge is not null && !ensureEdge.Contains("Arguments = \"run\""),
                            "★★ 而且那个光秃秃的 `Arguments = \"run\"` 不许再出现 —— 它同时还漏掉了"
                            + "`OpenPairingWindowOnStart: false`(run 走默认值 true)⇒ 开机自启会"
                            + "**自动敞开 30 分钟准入窗口**,正是审计发现 [3] 禁止的那件事");
                        Assert(ensureEdge is not null && ensureEdge.Contains("PickBindIp"),
                            "★ 选不出地址时**停在这一步**,不许退回 `run` —— 退回去会「成功」,"
                            + "而那个成功正是这次要消灭的东西");
                        // ★★ stdin 必须重定向:`run-lan` 末尾有个 REPL,读到 null 就 break
                        //   ⇒ 中枢打完「已监听」当场退出。中枢那边的
                        //   `Console.IsInputRedirected ⇒ 不进 REPL` 只有在我们真重定向了才为真。
                        //   ★ `run` 那条路没有 REPL,所以这一条是**换成 run-lan 之后才承重**的。
                        Assert(ensureEdge is not null && ensureEdge.Contains("RedirectStandardInput = true"),
                            "★★★ 起 `run-lan` 必须重定向 stdin —— 不然中枢会在打完 banner 之后"
                            + "自己退出(它的命令台 REPL 读到 null 就 break),症状是"
                            + "「起来了、几秒后就没了」,比原来的病更难查");
                    }
                }


        // ══════════════════════════════════════════════════════════════════
        //  ★ 从客户端整块搬过来的七块 —— 锚点是 `Views/ModelsView.cs` 与
        //    `Views/ComponentPicker.cs`,它们现在就在管理端的 `Views/` 下。
        //    ★★ 断言正文**一个字都没改**:管理端的 `TryReadSource` 解到的正是那个根。
        // ══════════════════════════════════════════════════════════════════
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

            var mvSrc = TryReadSource(Path.Combine("Views", "ModelsView.cs"));
            if (mvSrc is not null)
                Assert(mvSrc.Contains("path.LostFocus += (_, _) => Commit();") && mvSrc.Contains("Unloaded += (_, _) => Commit();"),
                       "★ 模型路径不只靠失焦提交(焦点收窄后这页只剩它一个可聚焦控件)");

            // ---- 折叠状态的键必须【稳定】:加载更早的消息不能让展开态跑到别人身上 ----
            // ★ 这条只能用行为断言:结构断言看不出"下标 vs 稳定标识"会不会在归档后错位。

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

        // ★ 这两块的锚点是记忆库那一段,现在在 admin/Views/MemoryView.cs 里。
        //   ★★ 判词一个字没改;改的只有变量名与它读的那个文件。
        {
            var mvSrc2 = TryReadSource(Path.Combine("Views", "MemoryView.cs"));
            if (mvSrc2 is not null)
            {
                Assert(mvSrc2.Contains("MemoryCaps = { 0, 50, 100, 250, 500, 1024, 2048 }"), "记忆库总量上限是阶段值");
                Assert(mvSrc2.Contains("(\"一年\", 365)") && mvSrc2.Contains("(\"三年\", 1095)"), "保留期改成选时间(7/30/90 天、一年/两年/三年)");
                Assert(mvSrc2.Contains("记忆库是空的"), "记忆库空态如实说明是因为 AI 未接入");
            }
        }
    }
    // ══════════════════════════════════════════════════════════════════════════
    //  ★ 三个共用小工具 —— 与客户端 `Selftest.cs` 里那三个**逐字同款**。
    //    ★★ 为什么不是链同一份:客户端那三个是 `Selftest` 类的私有成员,
    //      而那个类里还有两千条客户端断言。把整份链过来 = 管理端跑一遍客户端自检。
    //    ★★★ 代价如实说:这是本轮**唯一**一处「同一段逻辑存在两份」。
    //      它们漂了不会有任何东西红 —— 已写进决议包的 DEBT,正解是提成
    //      `app/Services/SourceProbe.cs` 之类由两个 csproj 编同一份。
    // ══════════════════════════════════════════════════════════════════════════

    static readonly System.Text.Json.JsonSerializerOptions StoreJson = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>只去注释、**保留字符串**(查文案用)。与 NoComments 同一个东西,换个名字方便逐字搬。</summary>
    static string Body(string src) => NoComments(src);

    /// <summary>去注释**与字符串字面量**(查代码结构用)。用反了方向是恒真那一边(第 3c 条)。</summary>
    static string CodeOnly(string src)
    {
        var noc = NoComments(src);
        var sb = new System.Text.StringBuilder(noc.Length);
        bool inStr = false, inChar = false, verbatim = false;
        for (int i = 0; i < noc.Length; i++)
        {
            var c = noc[i];
            if (!inStr && !inChar)
            {
                if (c == '@' && i + 1 < noc.Length && noc[i + 1] == '"') { inStr = verbatim = true; i++; sb.Append("\"\""); continue; }
                if (c == '"') { inStr = true; verbatim = false; sb.Append("\"\""); continue; }
                if (c == '\'') { inChar = true; continue; }
                sb.Append(c);
                continue;
            }
            if (inStr)
            {
                if (verbatim) { if (c == '"' && (i + 1 >= noc.Length || noc[i + 1] != '"')) inStr = false; else if (c == '"') i++; }
                else if (c == '\\') i++;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '\\') { i++; continue; }
            if (c == '\'') inChar = false;
        }
        return sb.ToString();
    }

    /// <summary>取 [from, to) 之间那一段;任一端找不到就返回 null(**不返回半段**)。</summary>
    static string? Slice(string src, string from, string to)
    {
        var a = src.IndexOf(from, StringComparison.Ordinal);
        if (a < 0) return null;
        var b = src.IndexOf(to, a + from.Length, StringComparison.Ordinal);
        return b < 0 ? null : src[a..b];
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ★★★ 元断言「唯一入口必须有生产调用点」的三件工具(V22 · D115)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 找到 `20-client-win` 那一级。★ 判据是**它下面同时有 admin 与 app** ——
    /// 不是"往上找得到同名文件就采信"(那是 ASSERTION-PITFALLS 第 9 条那个坑:
    /// 出包闸把 exe 拷进 %TEMP%,而那儿躺着别的会话留下的陈旧源码,一找就中)。
    /// 出包形态的临时目录里是 `client\` + `admin\`,**没有 app\** ⇒ 配不上这个条件。
    /// </summary>
    static string? ClientWinRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "admin")) && Directory.Exists(Path.Combine(dir, "app")))
                return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    /// <summary>
    /// 全部**生产**源码。★★★ `Selftest*.cs` **不算生产** —— 这一条是整个元断言的支点:
    /// 「只被自检引用的函数」正是要抓的那个形状,把自检算进调用点,判据就恒真了。
    /// </summary>
    static List<string> ProductionCsFiles(string root)
    {
        var files = new List<string>();
        foreach (var sub in new[] { "admin", "app", "transport" })
        {
            var d = Path.Combine(root, sub);
            if (!Directory.Exists(d)) continue;
            IEnumerable<string> found;
            try { found = Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var f in found)
            {
                var norm = f.Replace('\\', '/');
                if (norm.Contains("/bin/", StringComparison.Ordinal)
                    || norm.Contains("/obj/", StringComparison.Ordinal)) continue;
                if (Path.GetFileName(f).StartsWith("Selftest", StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(f);
            }
        }
        return files;
    }

    /// <summary>一条「唯一入口」声明:谁在哪儿说的、说的是哪个函数。</summary>
    sealed record SoleEntryClaim(string File, int Line, string Method);

    /// <summary>
    /// 扫出所有「唯一入口」声明。★ 只认**紧挨着方法声明**的 `///` 文档块:
    ///   · 枚举成员 / 属性没有括号 —— 它们不是「入口」,是状态,跳过;
    ///   · 普通 `//` 注释与界面文案不参与 —— 它们说的常常是"面板入口""菜单入口"这类**人**的入口,
    ///     不是函数。判据宁可窄一点,也不要红得理由是假的。
    /// </summary>
    static List<SoleEntryClaim> SoleEntryClaims(List<string> files, string root)
    {
        var claims = new List<SoleEntryClaim>();
        var phrase = new System.Text.RegularExpressions.Regex("唯一[^\n]{0,10}入口");
        var decl = new System.Text.RegularExpressions.Regex(@"(\w+)\s*(?:<[^>]*>)?\s*\(");
        foreach (var f in files)
        {
            string[] lines;
            try { lines = File.ReadAllLines(f); } catch { continue; }
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal)) continue;
                if (!phrase.IsMatch(lines[i])) continue;
                // 走到这个 /// 块的末尾,再跳过特性行与空行,停在**声明那一行**
                var j = i;
                while (j < lines.Length && lines[j].TrimStart().StartsWith("///", StringComparison.Ordinal)) j++;
                while (j < lines.Length)
                {
                    var d = lines[j].TrimStart();
                    if (d.Length == 0 || d.StartsWith("[", StringComparison.Ordinal)) { j++; continue; }
                    break;
                }
                if (j < lines.Length && lines[j].Contains('('))
                {
                    var m = decl.Match(lines[j]);
                    if (m.Success)
                        claims.Add(new SoleEntryClaim(
                            Path.GetRelativePath(root, f).Replace('\\', '/'), i + 1, m.Groups[1].Value));
                }
                i = j;   // 同一个块里的多行只算一条
            }
        }
        return claims;
    }

    /// <summary>
    /// 数**限定形式**的调用点(`Type.Method(`)。
    /// <para>★ 为什么只数限定形式:声明那一行长成 `public static X Foo(`,它**不含** `.Foo(` ——
    /// 于是"声明"与"调用"天然分得开,不需要去猜哪一行是声明(猜错会让判据恒真或恒假)。
    /// 这几个入口本来就都是静态工具函数,全仓都写成 `Type.Method(...)`。</para>
    /// <para>★★ 先剥注释再数:注释里提到函数名**不算调用点** —— 恰恰相反,
    /// 「注释里到处都是它、代码里一次都没有」正是这条要抓的病。</para>
    /// </summary>
    static int QualifiedCallSites(List<string> files, string method)
    {
        var rx = new System.Text.RegularExpressions.Regex(
            @"\." + System.Text.RegularExpressions.Regex.Escape(method) + @"\s*\(");
        var n = 0;
        foreach (var f in files)
        {
            string src;
            try { src = File.ReadAllText(f); } catch { continue; }
            n += rx.Matches(NoComments(src)).Count;
        }
        return n;
    }
}
