// V21 -- 「模型」与「已配对设备」这两页**真的点得开、点开有东西**。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★★ 这一节存在的全部理由是 ASSERTION-PITFALLS 第 14 条:
//    「把那段实现整个换成空的,再跑一遍 —— 你的判据里有几条会红?
//      一条都不红 ⇒ 你钉住的是**文本**,不是**行为**。」
//    扫源码的判据对「这个按钮点下去到底做没做事」什么都不会说。
//
//  ⇒ 所以这一节**真的**做四件事,一件都不是读源码:
//    ① 真 `new` 出 `HostHubView` / `ModelsView`(WPF 控件真的构造出来);
//    ② 真发 HTTP:管理面 `GET /admin/devices` · 网关 `GET /v1/gpu/components`;
//    ③ 真跑 Dispatcher(不跑的话那两页的 async 填充永远不会落到界面上);
//    ④ 真去**可视树里**找那几行字 —— 设备名、组件名、峰值。
//
//  ★★ 替身只打在**这个进程结构上拿不到的那一件事**上(第 13 条):
//    「回环那个端口后面坐着谁」。判据链一段都没被换掉 ——
//    HTTP → `HubAdmin.ParseDevices` / `HubGpu.ParseCatalog` → 视图 → 可视树,全程真跑。
//
//  ★★★ 中枢**不在**时这一节整段 SKIP,而且**明说是跳过**,不计 PASS。
//    「没有中枢可测」与「测过了、没问题」必须分得开 —— 后者是本仓最恨的那种绿。
// ══════════════════════════════════════════════════════════════════════════════

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LocalAI.Admin.Services;
using LocalAI.Admin.Views;

namespace LocalAI.Admin;

public static partial class Selftest
{
    /// <summary>那两页**真的点开用**。中枢不在就整段 SKIP。</summary>
    static void RunLiveViews()
    {
        Console.WriteLine("\n-- 两页真的点得开(V21 · 出包实测那一条)--");

        // ── 前提:回环上真的有人答话 ────────────────────────────────────
        var adminUp = TcpUp(HubAdmin.AdminPort);
        var gatewayUp = TcpUp(LocalAI.Client.Services.HostSetup.GatewayPort);
        if (!adminUp && !gatewayUp)
        {
            Skip("★★★ 「主机中枢(已配对设备)」与「模型」两页真的点开用",
                 $"本机回环上没有中枢(127.0.0.1:{HubAdmin.AdminPort} 与 :"
                 + $"{LocalAI.Client.Services.HostSetup.GatewayPort} 都没人听)—— "
                 + "这一节**没跑**,不要读成「那两页没问题」");
            return;
        }

        // ★ WPF 控件必须在 STA 线程上、且有一个 Application 才建得出来。
        //   自检进程本身就是 [STAThread](见 Program.Main),这里只补 Application 与主题字典。
        if (Application.Current is null) new Application();
        LocalAI.Client.Theme.ThemeManager.Initialize(LocalAI.Client.Services.Skin.Breeze);

        if (adminUp) LiveDevices();
        else Skip("★★★ 「已配对设备」真的点开用",
                  $"回环管理面 127.0.0.1:{HubAdmin.AdminPort} 没人听");

        // ★★ V22:栈那一格。★ 它**不需要**管理面在 —— 恰恰相反,
        //   「Edge 没起」也是它必须说清楚的一档,所以这一节在两种前提下都跑。
        LiveStackCard();

        if (gatewayUp) LiveModels();
        else Skip("★★★ 「模型」真的点开用",
                  $"回环网关 127.0.0.1:{LocalAI.Client.Services.HostSetup.GatewayPort} 没人听");
    }

    // ── 已配对设备 ────────────────────────────────────────────────────────
    static void LiveDevices()
    {
        // ① 先把**数据这一段**真跑一遍:真发 HTTP、真过唯一那处解析器。
        var (ok, devices) = HostHubView.Admin.DevicesAsync().GetAwaiter().GetResult();
        Assert(ok, "★★★ 「已配对设备」真的从回环管理面取到了设备表 —— "
                   + "取不到的原因:" + (HostHubView.Admin.LastError ?? "(没记原因)"));
        if (!ok) return;
        Assert(devices.Count > 0,
            $"★★ 取到的设备表**不是空的**(实测 {devices.Count} 条)—— "
            + "空表与「取不到」在界面上长得一模一样,所以这一条必须单独钉");

        // ② 再把**界面这一段**真跑一遍:真 new 出那一页,真等它把行填进去。
        //   ★ 这一页的填充走 `Build()` → `HostDevicesCard()` → `LoadDevicesAsync()`,
        //     而那条链在构造函数里就起了 —— 不靠 `Loaded`,所以无头也跑得动。
        var view = new HostHubView();
        {
        // ★★★ 先钉住**角色真的判成主机**。不钉的话,`WrongHub`(连得上但不是本机那个中枢)
        //   会让这一页画成 HubDownCard,于是下面几条红,而红的理由是假的
        //   ——「设备没出现在界面上」听起来像面板坏了,真相是这一页根本不是设备页。
        //   ★ 这一格是跑出来的:桩第一版编了个陌生 hubId,`ProbeAsync` 正确地判了 WrongHub。
        PumpUntil(() => HostHubView.Admin.LastProbe != AdminProbeResult.Unknown, seconds: 10);
        Assert(HostHubView.Admin.LastProbe == AdminProbeResult.Ok,
            $"★★★ 回环管理面答话了、且 hubId 与本机档案一致(实测 {HostHubView.Admin.LastProbe})—— "
            + "对不上时这一页画的是「正在把这台准备成中枢主机」,不是设备列表;"
            + "不先钉这一条,下面几条会红得给出一个假理由");
        PumpUntil(() => TextsOf(view).Any(t => t.Contains(devices[0].DisplayName,
                                                          StringComparison.Ordinal)),
                  seconds: 20);

        var texts = TextsOf(view);
        Assert(texts.Count > 0,
            $"★ 元断言:那一页真的画出了东西(可视树里 {texts.Count} 段文字)—— "
            + "0 段的话下面几条会静默变成零断言");
        foreach (var d in devices.Where(x => x.Status != "revoked"))
            Assert(texts.Any(t => t.Contains(d.DisplayName, StringComparison.Ordinal)),
                $"★★★★ **在册设备「{d.DisplayName}」真的出现在界面上** —— "
                + "这一条是「点开能用」的直接证据:HTTP → 唯一那处解析器 → 可视树,全程真跑,"
                + "没有一段是读源码读出来的");
        // ★ 反向:已吊销的**不该**出现 —— 只钉"该出的出来了"是一条随时会变成恒真的判据
        //
        // ══════════════════════════════════════════════════════════════════
        //  ★★★★ V22:判据从【显示名】改成【证书指纹短码】。这是在修一条**红得理由是假的**断言。
        //
        //  实测(2026-08-09,本机真中枢):`SENIORBIRDS` 与 `HONGKONGPINGPON`
        //  **各有在册的一条 + 已吊销的若干条**,名字一模一样(本文件下面那条注释
        //  自己写着「同名设备很常见(实机就有两条 SENIORBIRDS)」)。
        //  于是这条按名字找的断言在要求:**把在册那条也从界面上抹掉** —— 而那正是
        //  上一条断言(「在册设备真的出现在界面上」)要求的**反面**。两条互相打架,
        //  按名字那条必然红,且**永远**红。
        //
        //  ★ 实测数字:FAIL=8 **全部**来自这一条(SENIORBIRDS ×2 + HONGKONGPINGPON ×6),
        //    而产品行为是**对的** —— `LoadDevicesAsync` 里 `Where(d => d.Status != "revoked")`
        //    过滤得好好的,上一条断言绿着就是证据。
        //  ★★ 这与下面那条指纹断言踩的是**同一个坑**,而那一条已经修过并把理由写在注释里了:
        //    「两条判据互相打架时,先看哪一条说的是真的」。这一条当时漏了。
        //
        //  ⇒ 改钉 **DeviceId**:它是这条记录的唯一标识,而且**界面上真的印着它**
        //    (`DeviceRow`:「指纹 … · {DeviceId}」)—— 所以它既分得开同名设备,
        //    又确实是"这一行在不在屏幕上"的直接证据。
        //  ★★ 为什么**不是**指纹短码:已吊销的设备恰恰**没有活动证书** ⇒ `CertShort` 为 null。
        //    第一版改成钉指纹,结果 8 条全落进"分不开、跳过"那一支,反向判据整段空转 ——
        //    而那个空转是被下面那条零命中断言当场抓出来的(FAIL=1),不是蒙混过去的。
        // ══════════════════════════════════════════════════════════════════
        var revokedChecked = 0;
        foreach (var d in devices.Where(x => x.Status == "revoked"))
        {
            if (string.IsNullOrWhiteSpace(d.DeviceId)) continue;   // 没标识就没法可靠地找,下面零命中会兜住
            revokedChecked++;
            Assert(!texts.Any(t => t.Contains(d.DeviceId, StringComparison.Ordinal)),
                $"★★ 反向:已解除的「{d.DisplayName}」(id {d.DeviceId})**不出现**在已配对列表里 —— "
                + "混在里面会让人以为它还连得上");
        }
        // ★ 零命中判红:一条已吊销设备都没查到的话,上面那个 foreach 是空转,
        //   而空转的表现是**一片绿**。中枢上确实有已吊销记录(实测 8 条)。
        Assert(revokedChecked > 0 || !devices.Any(x => x.Status == "revoked"),
            "★ 元断言:反向判据至少查了一条已吊销设备 —— 0 条的话这一段是空转,而空转看着像通过");
        // ★ 指纹短码也要摆出来:同名设备很常见(实机就有两条 SENIORBIRDS),只按名字分不开。
        //   ★★ 只看**在册**那些 —— 第一版把已吊销的也算了进去,于是它红了,
        //     而红的理由是假的:已吊销的本来就**不该**出现在这一页上(上一条正是在钉这件事)。
        //     两条判据互相打架时,先看哪一条说的是真的 —— 是上一条。
        var liveDevs = devices.Where(x => x.Status != "revoked" && x.CertShort is { Length: > 0 }).ToList();
        Assert(liveDevs.Count > 0, "★ 元断言:真的有带指纹的在册设备可查(0 台的话下面那条恒真)");
        Assert(liveDevs.All(d => texts.Any(t => t.Contains(d.CertShort!, StringComparison.OrdinalIgnoreCase))),
            "★★ 每一行都带**证书指纹短码** —— 同名设备只按名字分不开(D47 那条的界面落点)");
        }
    }

    // ── AI 栈那一格(V22)────────────────────────────────────────────────
    /// <summary>
    /// 「主机中枢」那一页上的**栈状态**真的画出来了,而且**它说的与探到的一致**。
    ///
    /// <para>★★★★ 这一节钉的是本轮最要紧的那条:**页面的结论必须跟着探测走**。
    /// 在此之前那一页只探 Edge 的回环管理面(8442),网关(8080)**整档不看** ——
    /// 于是「Edge 起着、网关没起」时它显示「这台电脑就是中枢主机」一路绿灯,
    /// 而「模型」页是空的。写这段时本机的真实状态**正是**那一档。</para>
    ///
    /// <para>★★ 判据写成**四种状态都能查**的形式(全起 / 半起两向 / 全没起),
    /// 而不是"现在应该显示某句话" —— 后者只在写它的那天成立,
    /// 换一台机器或者过一小时就变成一条恒假或恒真的断言。</para>
    /// </summary>
    static void LiveStackCard()
    {
        // ① 先**探**一次,拿到这一刻的事实。这是判据的另一端。
        var probed = HostProvision.ProbeStackAsync().GetAwaiter().GetResult();
        var gwUp = probed.Gateway.Outcome != SetupOutcome.Failed;
        var edgeUp = probed.Edge.Outcome != SetupOutcome.Failed;
        Console.WriteLine($"     (实测这一刻:网关 {(gwUp ? "在跑" : "没在跑")} · "
                          + $"Edge {(edgeUp ? "在跑" : "没在跑")})");

        // ② 真 new 出那一页,真等它把栈那一格填进去
        var view = new HostHubView();
        PumpUntil(() => TextsOf(view).Any(t => t.Contains("统一入口网关", StringComparison.Ordinal)),
                  seconds: 20);
        var texts = TextsOf(view);
        var all = string.Join("\n", texts);

        Assert(texts.Count > 0, "★ 元断言:「主机中枢」那一页真的画出了东西");
        // ★★ 这一格必须在**两档里都画**(Host 与 HostHubDown)。
        //   只在 HubDown 档画的话,最坏的中间态恰好落在看不见的地方 —— 那正是本轮修的病。
        Assert(all.Contains("统一入口网关", StringComparison.Ordinal),
            "★★★★ 「主机中枢」那一页上**必须有网关那一行** —— "
            + "在此之前这一页整档不看网关,于是「Edge 起着、网关没起」时它一路绿灯,"
            + "而「模型」页是空的。★ 这一条与角色判成 Host 还是 HostHubDown 无关");
        Assert(all.Contains($"{LocalAI.Client.Services.HostSetup.GatewayPort}", StringComparison.Ordinal),
            $"★★ 那一行要写明端口 {LocalAI.Client.Services.HostSetup.GatewayPort} —— "
            + "模型清单读的就是这个口,写出来才对得上账");

        // ③ ★★★★ 承重的那一条:**页面的结论必须与探到的一致**。
        var saysAllUp = all.Contains("整套栈都在跑", StringComparison.Ordinal);
        var saysHalf = all.Contains("【起了一半】", StringComparison.Ordinal);
        var saysNone = all.Contains("整套栈都没在跑", StringComparison.Ordinal);
        if (gwUp && edgeUp)
            Assert(saysAllUp && !saysHalf && !saysNone,
                "★★★ 两个都探到了 ⇒ 页面必须说「整套栈都在跑」,而且不能同时说别的");
        else if (gwUp || edgeUp)
            Assert(saysHalf && !saysAllUp,
                $"★★★★ 【半起】(网关 {(gwUp ? "在" : "不在")} / Edge {(edgeUp ? "在" : "不在")})"
                + " ⇒ 页面必须**明说是半起**,绝不许显示成全好 —— "
                + "这是最坏的中间态,因为两半都像成功");
        else
            Assert(saysNone && !saysAllUp,
                "★★★ 两个都没探到 ⇒ 页面必须说整套都没在跑");

        // ④ 半起时要说清**缺的那一半会怎样**,不是笼统一句「有问题」
        if (gwUp ^ edgeUp)
            Assert(all.Contains("模型", StringComparison.Ordinal)
                   || all.Contains("副机", StringComparison.Ordinal),
                "★★ 半起那句话要说清**缺了它会怎样**(没网关 ⇒ 模型页是空的;"
                + "没 Edge ⇒ 副机连不上),不是笼统一句「起了一半」");

        // ⑤ ★★ 绝不许把人推回命令行 —— 这是本车道存在的理由(用户裁定:不跑任何命令)
        Assert(!all.Contains("start-stack", StringComparison.OrdinalIgnoreCase),
            "★★★★ 这一页**不许**叫人去跑 `start-stack.ps1` —— 起栈是这个程序的活。"
            + "用户裁定:全程不需要跑任何命令");
    }

    // ── 模型 ──────────────────────────────────────────────────────────────
    static void LiveModels()
    {
        // ① 数据这一段:真发 `GET /v1/gpu/components`,真过 `HubGpu.ParseCatalog`
        var cat = AdminGpu.Instance.FetchCatalogAsync().GetAwaiter().GetResult();
        Assert(cat is not null, "★★★ 「模型」真的从回环网关取到了组件目录 —— "
                                + "取不到的原因:" + (AdminGpu.Instance.LastError ?? "(没记原因)"));
        if (cat is null) return;
        Assert(cat.Components.Count > 0,
            $"★★ 目录**不是空的**(实测 {cat.Components.Count} 个组件)—— "
            + "空目录会让面板显示成「一个组件都没有」,而那与「读不到」是两件事");

        // ② 界面这一段:真 new 出面板、**真调那个入口**、真去它的树里找组件行。
        //   ★ 「模型」那一页(ModelsView)也真的构造一遍 —— 它一构造就抛的话,
        //     用户点进去就是白屏,而那条路平时没人走。
        var page = new ModelsView();
        Assert(TextsOf(page).Count > 0,
            "★★ 「模型」那一页**构造得出来**且画得出东西 —— 构造期一抛,用户点进去就是白屏");
        var view = new ComponentPicker();
        {
        // ★★★ 不能直接 `GetAwaiter().GetResult()`:`LoadAsync` 里 `await` 之后要回到
        //   **UI 线程**去动控件,而这条线程上没装 SynchronizationContext ⇒ 续体落到线程池
        //   ⇒ 「调用线程无法访问此对象,因为另一个线程拥有该对象」。
        //   ⇒ 装上 DispatcherSynchronizationContext + 推一帧消息循环,让续体回到本线程。
        //   ★ 这一段是**测试脚手架**,不是被测代码 —— 真程序里那个上下文由 WPF 自己装。
        RunOnDispatcher(view.LoadAsync);

        var texts = TextsOf(view);
        Assert(texts.Count > 0, $"★ 元断言:「模型」那一页真的画出了东西({texts.Count} 段文字)");
        // ★ 诊断:把实际拿到的那几段字打出来 —— 「面板坏了」与「我没让它 Loaded」
        //   在一条 `Count > 0` 的断言下长得一模一样,而两者的下一步完全不同。
        foreach (var c in cat.Components)
            Assert(texts.Any(t => t.Contains(c.Display, StringComparison.Ordinal)),
                $"★★★★ **组件「{c.Display}」真的出现在面板上** —— "
                + "清单由中枢下发,面板不许自己维护一份(那正是 ModelCatalog.All 那份自造清单的病)");
        // ★ 峰值也要来自中枢:面板自己算一个数出来,用户就会照着一个**不是权威**的数做决定
        Assert(texts.Any(t => t.Contains(cat.Components[0].PeakGiB.ToString("0.0"), StringComparison.Ordinal)
                              || t.Contains(cat.Components[0].PeakGiB.ToString("0.00"), StringComparison.Ordinal)),
            "★★ 峰值显存也是**中枢下发的那个数**(唯一权威是主机的 vram-budget.toml)");

        // ══════════════════════════════════════════════════════════════════
        //  ★★★ V29(实机反馈②)· 「按需」那一列的默认值。用户 2026-08-09 一次性授权全部组件。
        //
        //  ★ 判据按**中枢此刻的真实状态**分两支写,而不是"现在应该是全勾" ——
        //    后者只在写它的那天成立,中枢那边一被授权过就变成恒假(本仓踩过这个形状)。
        //  ★★ 两支都真的走过一遍:哪一支跑了,输出里说得出来。
        // ══════════════════════════════════════════════════════════════════
        var hubPermitted = cat.Components.Where(c => c.PermittedOnDemand).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (hubPermitted.Count == 0)
        {
            Assert(view.DefaultedAllOnDemand,
                $"★★★★ 中枢那份授权是空的 ⇒ 面板按 **2026-08-09 那次一次性授权**把「按需」默认全勾上"
                + $"(实测 {view.PermittedNow.Count}/{cat.Components.Count} 个)—— "
                + "这是用户原话「我需要所有模型一开始都是默认按需被勾上的」的落点");
            Assert(cat.Components.All(c => view.PermittedNow.Contains(c.Id)),
                $"★★★ **每一个**组件都勾上了,不是勾了一部分"
                + $"(实测 {view.PermittedNow.Count}/{cat.Components.Count})");
            Assert(TextsOf(view).Any(t => t.Contains("一次性授权", StringComparison.Ordinal)),
                "★★★★ 默认勾上这件事**在界面上说出来了** —— 一次性授权省掉的是「逐个点」,"
                + "不是「让人知道自己被默认授权了什么」。不说的话,用户看到的就是一排"
                + "他没点过的勾,而 D90 裁定①防的正是这个");
        }
        else
        {
            Assert(!view.DefaultedAllOnDemand && hubPermitted.SetEquals(view.PermittedNow),
                $"★★★★ 中枢已经记着 {hubPermitted.Count} 个授权 ⇒ **以中枢为准**,面板不再默认全勾"
                + $"(实测面板 {view.PermittedNow.Count} 个)—— ★ 少了这一条,用户取消勾选、"
                + "点确定、再打开这一页时会被**重新勾回去**,于是这一页永远撤不掉授权");
        }
        Console.WriteLine($"     (这一趟走的是:中枢授权 {hubPermitted.Count} 个 ⇒ "
                          + $"{(hubPermitted.Count == 0 ? "默认全勾那一支" : "以中枢为准那一支")})");
        }
    }

    // ── 三个小工具 ────────────────────────────────────────────────────────
    /// <summary>
    /// 在**本线程的 Dispatcher 上**跑一个 async 方法并等它完成。
    /// ★ 装上 `DispatcherSynchronizationContext` 让 `await` 的续体回到这条线程,
    ///   再用 `DispatcherFrame` 推消息循环 —— 直接 `GetResult()` 会死锁或跨线程炸。
    /// </summary>
    static void RunOnDispatcher(Func<Task> work)
    {
        var disp = Dispatcher.CurrentDispatcher;
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(disp));
        try
        {
            var frame = new DispatcherFrame();
            Exception? failed = null;
            disp.InvokeAsync(async () =>
            {
                try { await work(); }
                catch (Exception ex) { failed = ex; }
                finally { frame.Continue = false; }
            });
            // ★ 到点也要放行:卡死在这儿会让整套自检没有汇总行,而「没有汇总行 = 没跑起来」
            var guard = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromSeconds(20), DispatcherPriority.Normal,
                (_, _) => frame.Continue = false, disp);
            guard.Start();
            Dispatcher.PushFrame(frame);
            guard.Stop();
            if (failed is not null) throw failed;
        }
        finally { SynchronizationContext.SetSynchronizationContext(prev); }
    }

    static bool TcpUp(int port)
    {
        try
        {
            using var t = new System.Net.Sockets.TcpClient();
            var c = t.ConnectAsync(System.Net.IPAddress.Loopback, port);
            return c.Wait(1200) && t.Connected;
        }
        catch { return false; }
    }

    /// <summary>
    /// ★★★ V21 留痕:这里**曾经**挂过一个屏幕外的真窗口,为的是让 `Loaded` 触发。
    ///   实测不成立 —— 完全在屏幕外的窗口 Windows 不真渲染,`Loaded` 照样不来,
    ///   而可视树里只剩 4 段文字。那一刻两种解释代价天差地别:
    ///   「面板坏了」还是「我根本没让它 Loaded」—— 是后者。
    ///   ⇒ 改成**直接调那个入口**(`ComponentPicker.LoadAsync`,`Loaded` 处理器调的就是它)。
    ///     这既是第 14 条要的「真的触发那个入口并观察后果」,又不赌无头渲染。
    ///   ★ 这段函数留着不删:它记的是「为什么不走那条路」,而理由不随实现消失。
    /// </summary>
    /// <summary>
    /// 把视图挂进一个**真窗口**里,但摆到屏幕外、不抢焦点、不进任务栏。
    ///
    /// <para>★★★ 为什么非要真窗口:两页的填充都挂在 `Loaded` 上,而 `Loaded` 只有在元素
    /// 连上一个 **PresentationSource**(也就是真的有个窗口)时才会触发。
    /// 光 `Measure/Arrange/UpdateLayout` 一个裸 `Grid` **不会**让它触发 ——
    /// 第一版就是这么写的,结果:目录取到了 3 个组件、页面也建出来了,
    /// 而可视树里只有 4 段文字,组件行一行都没有。
    /// ★ 那一刻两种解释代价天差地别:「面板坏了」还是「我根本没让它 Loaded」。
    ///   是后者 —— 而这正是第 15 条那句「一个没有按预期红的测量,是**证据**,不是噪音」。</para>
    ///
    /// <para>★★ 摆到屏幕外 + `ShowActivated=false`:自检要能在门禁里跑,
    /// 不许往用户屏幕上弹窗、更不许抢焦点。</para>
    /// </summary>
    static Window OffscreenHost(UIElement view)
    {
        var w = new Window
        {
            Content = view,
            Width = 1000, Height = 740,
            Left = -20000, Top = -20000,          // ★ 屏幕外
            ShowInTaskbar = false,
            ShowActivated = false,                 // ★ 不抢焦点
            WindowStyle = WindowStyle.None,
        };
        w.Show();
        return w;
    }

    /// <summary>
    /// 真跑消息循环直到条件成立或到点。
    /// ★★ 不跑的话那两页的 `async` 填充**永远不会**落到界面上 —— 而"没跑消息循环"
    ///   与"填充坏了"在断言上长得一模一样,那正是要避开的那种绿。
    /// </summary>
    static void PumpUntil(Func<bool> done, int seconds)
    {
        // ══════════════════════════════════════════════════════════════════════
        //  ★★★ 第一版是 `Invoke(ContextIdle)` + `Thread.Sleep(100)` 轮询 ——
        //    它**时绿时红**:同一份代码,一次跑设备行出来了,下一次就没有。
        //    根因是没装 `SynchronizationContext`:那几个 `async` 续体落在**线程池**上,
        //    再从那儿 `Dispatcher.Invoke` 回来 —— 而我的线程大部分时间卡在 `Sleep` 里。
        //  ★★ 一条**时绿时红**的判据比没有更坏(第 5 条):它训练人去重跑,
        //    而重跑几次总会绿一次,于是真缺陷也会被「重跑一下就好了」盖过去。
        //  ⇒ 与 `RunOnDispatcher` 同一套:装上下文 + 推帧,让续体**确定地**回到本线程。
        // ══════════════════════════════════════════════════════════════════════
        if (done()) return;
        var disp = Dispatcher.CurrentDispatcher;
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(disp));
        try
        {
            var frame = new DispatcherFrame();
            var poll = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
                (_, _) => { if (done()) frame.Continue = false; }, disp);
            // ★ 到点也要放行:卡死会让整套自检**没有汇总行**,而那被门禁读成「没跑起来」
            var guard = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromSeconds(seconds), DispatcherPriority.Normal,
                (_, _) => frame.Continue = false, disp);
            poll.Start(); guard.Start();
            Dispatcher.PushFrame(frame);
            poll.Stop(); guard.Stop();
        }
        finally { SynchronizationContext.SetSynchronizationContext(prev); }
    }

    /// <summary>把可视/逻辑树里所有 TextBlock / ContentControl 的文字收集起来。</summary>
    static List<string> TextsOf(DependencyObject root)
    {
        var outp = new List<string>();
        void Walk(DependencyObject d)
        {
            if (d is TextBlock tb && !string.IsNullOrEmpty(tb.Text)) outp.Add(tb.Text);
            if (d is ContentControl cc && cc.Content is string s && s.Length > 0) outp.Add(s);
            foreach (var c in System.Windows.LogicalTreeHelper.GetChildren(d))
                if (c is DependencyObject dc) Walk(dc);
            var n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        try { Walk(root); } catch { /* 走树时个别节点抛不该拖垮整节 */ }
        return outp.Distinct().ToList();
    }
}
