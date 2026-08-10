// V21 -- 主机中枢:把这台装成主机 · 已配对的电脑 · 配对窗口与批准。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ 本文件是 `app/Views/DevicesView.cs` 搬过来的**主机侧那一半**(迁移地图 §2.2)。
//
//  ★★ 切法是【按成员名】,不是按行号 —— 地图点名的那条:
//    `StartPairing` 落在 V10 §2.2「124–597 ⇒ 管理端」那个**旧基线**区间里,
//    但它是客户端 `KnockAsync` 唯一的下一步,而且**六词 SAS 显示**就在它里面(D47)。
//    §2.3② 白纸黑字:「SAS 必须在副机屏幕上显示 …… 搬走就等于取消它」。
//    ⇒ 按行号切会一刀切走 D47 的安全根基,而**不会有任何东西红**。
//
//  搬过来的(逐个成员):
//    ProbeRoleAsync · ProbingCard · HubDownCard · Line/ResetLines/Action/NewStatus ·
//    AutoSetupAsync · ContinueAfterIdentityAsync · StartEdgeStepAsync · MintThenContinueAsync ·
//    Retry · BuildNicPicker · SetupHostAsync · StartEdgeAsync ·
//    HostSelfCard · HostDevicesCard · LoadDevicesAsync ·
//    ToggleAddAsync · CloseWindowAsync · RenderAddSection · 轮询 · PendingRow ·
//    DeviceRow · IsThisMachine · SafeDisplayName
//
//  留在客户端的(逐个成员):
//    PairedCard · ChangeDialRow · **StartPairing** · ClientPairCard · KnockAsync ·
//    ScanForHubsAsync · ManualDialRow  —— 全是「敲门那一侧」(V10 §2.3)。
// ══════════════════════════════════════════════════════════════════════════════
//
// ★★★ 一处**如实交代的行为变化**(不是漏掉,是拆分的直接后果):
//   `SelfPairAsync`(主机一键自配对)**没有跟着搬,也没有留下** —— 它跨两个进程:
//   enroll 那一半是**客户端**的(要写这台机器的设备私钥与 profile.json),
//   开窗+批准那一半是**管理端**的(走回环 `/admin/*`)。
//   一个进程里做不完,而把任何一半留在对面都会破坏纪律①或③。
//   ⇒ 主机上的自配对从此走**和任何一台副机一模一样**的两步:
//     客户端点「开始寻找主机」→ 屏幕上出六个词 → 到管理端这一页批准。
//   ★ 这**不是**能力退化,反而补上了一条:原来的一键路径**明文跳过六词比对**
//     (它自己的注释花了 8 行解释为什么可以跳过)。现在主机也逐字比对了。
//   ★ 代价如实说:主机上的人要在**两个窗口之间**走一趟,而不是点一下。
//
// ★ 安全边界(从 DevicesView 原样带过来,不可让步):
//   把配对搬进界面,只改变**六个词显示在哪里**,不改变安全性质 ——
//   六词仍由两端各自独立推导、仍需**人工逐词比对**、仍需**主机侧批准**。
//   界面绝不代替人做比对,也不提供"跳过比对"的快捷方式。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Admin.Services;
using LocalAI.Client.I18n;
using LocalAI.Client.Services;
using LocalAI.Client.Views;

namespace LocalAI.Admin.Views;

/// <summary>
/// 这台电脑在这套装置里是什么角色。★ 三种,一种都不许合并:
///   · Unknown     —— 还没探完。**不猜**,界面如实说"正在确认"。
///   · Host        —— 回环管理面答话了且 hubId 对得上。这是唯一的【肯定证据】。
///   · HostHubDown —— 探不到管理面 ⇒ 中枢没起来,这一页去把它装起来/起起来。
///
/// <para>★★ V21 删掉了原来的第四种 `Client`。理由不是"合并了两种",是**这个进程里它不存在**:
/// 管理端只装在主机上(副机上根本没有这个 exe)—— 一个在本进程里永远取不到的枚举值,
/// 留着只会让下一个人写出一段永远走不到的分支。
/// 「这台其实是副机」那条出路也跟着走了:在管理端里那句话没有意义。</para>
/// </summary>
public enum HostRole { Unknown, Host, HostHubDown }

public sealed class HostHubView : UserControl
{
    /// <summary>配对窗口一次开多久(分钟)。★ 这是【主机侧】的上限,由中枢自己到点失效 ——
    /// 和客户端在不在、页面在哪、进程有没有崩,全都无关。这是最后一道闸。</summary>
    public const int WindowMinutes = 10;

    /// <summary>拉起 Edge 后等它应答多久(秒)。★ 到点就如实说"没等到",不无限转圈。</summary>
    public const int StartEdgeWaitSeconds = 30;

    /// <summary>这台的回环管理面通道。★ 管理端进程里**只有一个** —— 两份会各探各的。</summary>
    public static readonly HubAdmin Admin = new();

    HostRole _role = HostRole.Unknown;

    StackPanel? _devList;
    StackPanel? _addPanel;
    TextBlock? _addStatus;
    Button? _addToggle;
    System.Windows.Threading.DispatcherTimer? _pendTimer;

    /// <summary>上一次「打开这台的客户端」失败的原因;成功或没点过是 <c>null</c>。
    /// ★ 存成字段是因为点完要 `Build()` 重画整页 —— 不存的话那一行当场被自己刷掉,
    /// 而那与"点了没反应"在屏幕上一模一样(实机反馈③要修的正是这个形状)。</summary>
    string? _clientStartError;

    readonly StackPanel _root = new();

    public HostHubView()
    {
        Content = _root;
        // ★ 第 ① 道闸的另一半:离开这一页(切到别的分节、关窗口、重建界面)也要关掉配对窗口。
        //   不挂这个的话,"展开着就走人"会把窗口一直留到中枢侧的分钟上限才关。
        // ★ 判据用【中枢自报的】那一位,不用本地布尔。
        Unloaded += (_, _) => { if (Admin.PairingWindowOpen) _ = CloseWindowAsync(quiet: true); };
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue && Admin.PairingWindowOpen) _ = CloseWindowAsync(quiet: true);
        };

        // ★★ V22:起栈是在**管理端启动时**就跑起来的(App.OnStartup),多半比这一页早得多。
        //   挂上它的进度播报,这一页才能在"正在起"的那 40 秒里说人话;
        //   ★ 一定要在 Unloaded 里摘掉 —— 事件是静态的,不摘就把这个视图一直吊着,
        //     而且 Build() 换掉控件之后回调会往一棵摘下来的树上写。
        StackBoot.Changed += OnStackChanged;
        Unloaded += (_, _) => StackBoot.Changed -= OnStackChanged;

        Build();
    }

    /// <summary>起栈那边有动静了 —— 只重画栈那一格,不动整页(整页重画会打断配对轮询)。</summary>
    void OnStackChanged() => Dispatcher.InvokeAsync(() => { _ = RenderStackAsync(); });

    /// <summary>★ 角色没探出来之前【什么都不猜】—— 如实说"正在确认",探完再画。</summary>
    void Build()
    {
        _root.Children.Clear();
        switch (_role)
        {
            case HostRole.Unknown:
                _root.Children.Add(ProbingCard());
                _ = ProbeRoleAsync();
                break;

            // ★★★ V22:栈那一格在**两档里都画**,这一点是承重的。
            //   在此之前「这台电脑就是中枢主机」那一档**根本不看网关** ——
            //   而写这段时本机的真实状态正是「Edge 的管理面 8442 通、网关 8080 不通」:
            //   页面一路绿灯说「就是中枢主机」,「模型」页却是空的(模型清单读的是 8080)。
            //   ⇒ 只在 HubDown 那一档显示栈状态,等于让**最坏的中间态**恰好落在看不见的地方。
            case HostRole.Host:
                _root.Children.Add(StackCard());
                _root.Children.Add(HostSelfCard());
                _root.Children.Add(HostDevicesCard());
                break;

            default:   // HostHubDown
                _root.Children.Add(StackCard());
                _root.Children.Add(HubDownCard());
                break;
        }
    }

    /// <summary>手动重探一次(换了状态 —— 比如刚把 Edge 起起来 —— 用它,不用重开管理端)。</summary>
    UIElement RecheckRow() => Ui.Secondary("重新检测", (_, _) => { _role = HostRole.Unknown; Build(); });

    // ════════════════════════════════════════════════════════════════════════
    //  ★★★ V22 · 栈的进度与失败原因(D115)
    //
    //  「客户端与栈」那一页的文案一直写着:
    //    「起栈的进度与失败原因在【主机中枢】那一页」
    //  ——而这一页在此之前**没有那个东西**。两页互相担保,谁都没兑现。
    //  这一格就是那句承诺的落地。
    //
    //  ★ 判据用 `StackResult`(AllUp / HalfUp),**不另造模型** —— 用户明说别新造。
    //  ★★ 就绪判据探的是**网关 8080**,不是 8443:模型清单读的就是 8080,
    //    而 8443 那个口在 `run` 模式下本来就开着 ⇒ 探它等于「失败与成功长得一样」。
    // ════════════════════════════════════════════════════════════════════════

    StackPanel? _stackBox;

    /// <summary>中枢日志的最后 14 行。★ 读不到就如实返回那句话,不返回空(空会让这一段整个消失,
    /// 而"没有日志"与"读不到日志"是两件事)。</summary>
    static string EdgeLogTail()
    {
        try
        {
            if (!File.Exists(HostProvision.EdgeLogPath)) return "";
            var all = File.ReadAllLines(HostProvision.EdgeLogPath);
            return string.Join(Environment.NewLine, all.Reverse().Take(14).Reverse());
        }
        catch (Exception ex) { return "(读不到中枢日志:" + ex.Message + ")"; }
    }

    /// <summary>栈那一格。★ 内容由 <see cref="RenderStackAsync"/> 填,因为它要去探端口。</summary>
    UIElement StackCard()
    {
        _stackBox = new StackPanel();
        _ = RenderStackAsync();
        return Ui.Card(Ui.Stack(Ui.Subtitle("AI 栈"), _stackBox));
    }

    /// <summary>
    /// 画栈的现状。★ 两份材料合起来才够:
    ///   · **现在通不通** —— 去探(`ProbeStackAsync`),这是唯一的肯定证据;
    ///   · **为什么没通** —— 起栈那一轮留下的 `Detail`(venv 缺依赖 / 端口被占 / 退出码几)。
    /// 只有探测没有原因,页面就只会说"没起来"而不说是哪一步;
    /// 只有原因没有探测,栈被别人关掉之后页面还会显示上一次的绿灯。
    /// </summary>
    async Task RenderStackAsync()
    {
        var box = _stackBox;
        if (box is null) return;

        var phase = StackBoot.Phase;
        var lines = StackBoot.Lines;
        var boot = StackBoot.Last;
        var live = await HostProvision.ProbeStackAsync();

        Dispatcher.Invoke(() =>
        {
            if (_stackBox != box) return;   // 这一格已经被 Build() 换掉了,别往旧控件上写
            box.Children.Clear();

            // ── 正在起:把逐步进度摆出来 ──────────────────────────────
            if (phase == StackPhase.Working)
            {
                box.Children.Add(Ui.Body("正在启动 AI 栈…"));
                foreach (var l in lines) box.Children.Add(Ui.Caption(l));
                box.Children.Add(Ui.Caption("★ 每一步最多等 20 秒 —— 起了进程不算数,要探到端口答话才算。"));
                return;
            }

            // ── 逐个组件说结论。★ 用现探的结果,不用上一轮的记忆 ──────
            void Component(SetupStep now, SetupStep? attempt)
            {
                var up = now.Outcome != SetupOutcome.Failed;
                box.Children.Add(Ui.Body((up ? "✓ " : "✗ ") + now.Name + (up ? ":在跑" : ":【没在跑】")));
                box.Children.Add(Ui.Caption("　" + now.Detail));
                // ★ 没起来的时候,把**起栈那一轮的真实原因**接在后面 —— 那才是可执行的下一步。
                if (!up && attempt is { Outcome: SetupOutcome.Failed })
                    box.Children.Add(Ui.Caption("　起栈时它说:" + attempt.Detail));
            }
            Component(live.Gateway, boot?.Gateway);
            Component(live.Edge, boot?.Edge);

            // ★★★ 中枢没起来时,把它**自己打印的最后几行原文**摆出来。
            //   这是原来那个黑窗口真正担着的活:窗口可以藏,**现场不能丢**。
            //   (`StartEdgeAsync` 删掉时这段跟着搬过来,一个字没少 —— 自检钉的就是这条。)
            if (live.Edge.Outcome == SetupOutcome.Failed && EdgeLogTail() is { Length: > 0 } tail)
            {
                box.Children.Add(Ui.Caption("　中枢自己打印的最后几行:"));
                var log = Ui.Caption("　" + tail.Replace("\n", "\n　"));
                log.FontFamily = new FontFamily("Consolas");
                box.Children.Add(log);
            }

            box.Children.Add(new Border { Height = 8 });

            if (live.AllUp)
            {
                box.Children.Add(Ui.Body("整套栈都在跑。"));
                box.Children.Add(Ui.Caption("★ 判据是【探到的】:网关 /health 答话、Edge 回环管理面答话。"
                                            + "不是「进程起来了」就算 —— 那两件事经常不一样。"));
            }
            else if (live.HalfUp)
            {
                // ★★★ 半起 —— 用户点名要它看得见:这是最坏的中间态,因为**两半都像成功**。
                var down = live.Gateway.Outcome == SetupOutcome.Failed ? "网关" : "LAN Edge";
                var upOne = down == "网关" ? "LAN Edge" : "网关";
                box.Children.Add(Ui.Body($"★★ 【起了一半】:{upOne} 在跑,{down} 没起来。"));
                box.Children.Add(Ui.Caption(down == "网关"
                    ? "　这台的中枢在,副机连得上 —— 但【没有任何模型用得了】:"
                      + "「模型」页会是空的,聊天会说后端没应答。缺的就是上面那一条。"
                    : "　模型用得了(本机客户端走回环网关)—— 但【副机连不上这台】:"
                      + "对外的 8443 没人在听。"));
                box.Children.Add(Ui.Caption("　★ 这一档在此之前【看不见】:页面只探 Edge 的管理面,"
                                            + "网关不通时照样显示「就是中枢主机」。"));
            }
            else
            {
                box.Children.Add(Ui.Body("★ 整套栈都没在跑。"));
                box.Children.Add(Ui.Caption("　上面两行写着卡在哪一步。"
                                            + "★ 这里【不会】叫你去跑 start-stack.ps1 —— 起栈是这个程序的活。"));
            }

            // ── 可执行的下一步 ────────────────────────────────────────
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            if (!live.AllUp)
            {
                var retry = Ui.Primary("重新启动 AI 栈", async (_, _) =>
                {
                    await StackBoot.EnsureAsync(force: true);
                    _role = HostRole.Unknown;
                    Build();
                });
                retry.Margin = new Thickness(0, 0, 8, 0);
                row.Children.Add(retry);
            }
            var recheck = Ui.Secondary("重新检测", (_, _) => { _ = RenderStackAsync(); });
            recheck.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(recheck);
            if (File.Exists(HostProvision.EdgeLogPath))
                row.Children.Add(Ui.Secondary("打开中枢日志", (_, _) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo(HostProvision.EdgeLogPath) { UseShellExecute = true });
                    }
                    catch { }
                }));
            box.Children.Add(row);
        });
    }

    UIElement ProbingCard() => Ui.Card(Ui.Stack(
        Ui.Subtitle("正在确认这台电脑的角色…"),
        Ui.Body("在 ping 本机的回环管理面(127.0.0.1:" + HubAdmin.AdminPort + ")。", muted: true),
        Ui.Caption("★ 拿到肯定证据才下结论 —— 这一页在探完之前不显示任何猜测。")));

    /// <summary>
    /// 探角色。★ 顺序有讲究:先看【肯定证据】(管理面答话且 hubId 一致),
    /// 拿不到再看【线索】(本机有没有主机端程序),两样都没有才认定是副机。
    /// 把第二档漏掉就会在"主机但 Edge 没启动"时说成"这台不是主机" —— 那正是要修的那个坑。
    /// </summary>
    async Task ProbeRoleAsync()
    {
        // ★ 期望的 hubId 从**客户端的档案**里读(只读)——「连得上」还不够,
        //   同机跑着另一个中枢时 hubId 会对不上,那一档(WrongHub)必须走得到。
        //   ★ 客户端还没配过对时是 null,那正是「主机第一次自己配自己」的场景:
        //     那时只要连得上就认(HubAdmin.ProbeAsync 自己写着这条)。
        try { await Admin.ProbeAsync(ClientProfilePeek.HubId()); }
        catch { /* 探不到就是没证据,不是错误 */ }
        // ★★ 只有两档:答话了 = Host;没答话 = 中枢没起来,这一页去把它起起来。
        //   原来还有第三档「这台不是主机」—— 在**管理端**里那一档不存在:
        //   这个 exe 只装在主机上(副机上根本没有它)。
        var role = Admin.LastProbe == AdminProbeResult.Ok ? HostRole.Host : HostRole.HostHubDown;
        Dispatcher.Invoke(() => { _role = role; Build(); });
    }

    /// <summary>
    /// 本机多半就是主机,但中枢没起来。
    ///
    /// ★★ 这张卡【不摆按钮】。用户裁定:「无痛丝滑,不需要跑任何命令栏和设置」——
    ///   那么"铸身份""起中枢"这些就是内部步骤,不是让人做选择的地方。进这一页就自己做完。
    /// ★ 唯一会冒出按钮的情形只有两种,而且都是**真的需要人**:
    ///   ① 防火墙规则不在 —— 那一步要系统授权框,绝不能不问自取地弹;
    ///   ② 上面哪一步失败了 —— 给一次重试,并且把真实原因摆出来。
    /// </summary>
    UIElement HubDownCard()
    {
        _setupLines = new StackPanel();
        _setupActions = new StackPanel();
        var card = Ui.Card(Ui.Stack(
            Ui.Subtitle("正在把这台准备成中枢主机…"),
            // ★ 先把【观察到的事】说出来,再说我要做什么 —— 顺序反了就成了"我说了算"
            Ui.Body("中枢没在这台机器上运行。本机装着主机端程序,所以这台应该就是主机。", muted: true),
            _setupLines,
            _setupActions,
            new Border { Height = 10 },
            // ★ 用户要求保留:换了状态(比如手动起了中枢)时不用重开客户端。
            //   它【只重探】,不启动任何东西 —— 按钮必须只做它名字说的那件事(自检钉着)。
            RecheckRow()));
        _ = AutoSetupAsync();
        return card;
    }

    StackPanel? _setupLines;
    StackPanel? _setupActions;

    void Line(string text, bool muted = false) => Dispatcher.Invoke(() =>
        _setupLines?.Children.Add(muted ? Ui.Caption(text) : Ui.Body(text)));

    void ResetLines() => Dispatcher.Invoke(() =>
    {
        _setupLines?.Children.Clear();
        _setupActions?.Children.Clear();
    });

    void Action(UIElement e) => Dispatcher.Invoke(() => _setupActions?.Children.Add(e));

    /// <summary>
    /// 自动把这台装成主机。★ 顺序:身份 → (防火墙,只在缺的时候才问)→ 起中枢。
    /// ★ 每一步都把【实际结果】写出来,失败就停在那儿说真原因,不往下堆新错误。
    /// </summary>
    async Task AutoSetupAsync()
    {
        ResetLines();
        try
        {
            // ---- ① 身份 ----
            // ★★ 铸身份【不是内部步骤】—— 它会在这台机器上新建一个中枢,而且不可回退
            //   (要撤只能跑破坏性的 重置并铸身份.cmd,所有已配对设备全部失效)。
            //   而走到这张卡的判据只是【旁边有个 host 目录】—— 那是线索不是判据:
            //   把主机上整个 dist 拷到第二台电脑就会满足它。不问就铸 = 网段里悄悄多出一个中枢。
            //   ⇒ 已经有身份就静默继续(那才是真·内部步骤);没有就停下来问。
            Line("① 中枢身份:正在检查…", muted: true);
            if (!await HostSetup.IdentityExistsAsync())
            {
                ResetLines();
                Line("这台机器还没有中枢身份。");
                Line("★ 建一个中枢是【不可回退】的:之后要撤只能把身份删掉重铸,那会让所有已配对的电脑全部失效。"
                     + "所以这一步要你点一下,我不替你决定。", muted: true);
                Action(Ui.Primary("在这台上建中枢(我确认这台是主机)", async (_, _) => await MintThenContinueAsync()));
                // ★★ 这里原来还有一颗「这台其实是副机」的出路按钮。它跟着 `HostRole.Client` 一起走了:
                //   **管理端只装在主机上**,在这个进程里「其实是副机」不是一个能成立的答案。
                //   ★ 真的装错了地方怎么办:那台机器上该做的是**卸掉管理端**(或者干脆不装),
                //     而不是在管理端界面里点一个按钮把自己降级 —— 后者只是把界面藏起来,
                //     开机自启那条路还在。这一句写在这儿,免得下一个人把按钮加回来。
                return;
            }
            var id = await HostProvision.EnsureIdentityAsync();
            ResetLines();
            Line(id.Outcome == SetupOutcome.Failed
                ? "① 中枢身份:没弄成 —— " + id.Detail
                : "① 中枢身份:" + (id.Outcome == SetupOutcome.Skipped ? "本来就有" : "已铸好"));
            if (id.Outcome == SetupOutcome.Failed) { Retry(); return; }

            await ContinueAfterIdentityAsync();
        }
        catch (Exception ex)
        {
            // ★ 这是 fire-and-forget 调的 —— 不兜住的话界面就停在"正在…",而没人知道为什么
            ResetLines();
            Line("准备过程出错(" + ex.GetType().Name + "):" + ex.Message);
            Retry();
        }
    }

    /// <summary>身份就绪之后的两步:防火墙(只在缺的时候才问)→ 起中枢。</summary>
    async Task ContinueAfterIdentityAsync()
    {
        try
        {
            // ---- ② 防火墙:★ 只在【规则不在】时才出按钮,因为它要弹系统授权框 ----
            if (await HostProvision.FirewallRuleExistsAsync())
            {
                Line("② 防火墙 8443:本来就放行了");
            }
            else
            {
                Line("② 防火墙 8443:还没放行 —— 副机会连不上这台。");
                Line("★ 这一步要弹一次系统授权框,所以不替你点。", muted: true);
                var fwStatus = NewStatus();
                Action(Ui.Primary("放行防火墙 8443(需要一次系统授权)", (_, _) => BuildNicPicker(_setupActions!, fwStatus)));
                Action(Ui.Secondary("先跳过(只在本机用)", async (_, _) => await StartEdgeStepAsync()));
                return;
            }

            // ---- ③ 起中枢 ----
            await StartEdgeStepAsync();
        }
        catch (Exception ex)
        {
            // ★ 这是 fire-and-forget 调的 —— 不兜住的话界面就停在"正在…",而没人知道为什么
            ResetLines();
            Line("准备过程出错(" + ex.GetType().Name + "):" + ex.Message);
            Retry();
        }
    }

    // ★★ 这里原来是 `readonly TextBlock _setupStatus = Ui.Body("")` —— 一个**跨 Build 共享**的控件。
    //   两个后果都真出现了:BuildNicPicker 会 Children.Clear() 把它从可视树上摘走,
    //   之后所有状态文字都写进一个看不见的控件(界面永久静默,而"公用网络"那句最有用的解释
    //   恰恰就是没人看得见的那句);再把同一个控件重新 Add 又会抛
    //   InvalidOperationException(元素已有父级),catch 里的 ResetLines() 顺手把唯一能推进的
    //   按钮也清掉,此后这个视图里不可恢复。
    //   ⇒ 每次要用就【新建一个】,谁用谁负责把它挂进树里。
    TextBlock NewStatus()
    {
        var t = Ui.Body("");
        Action(t);
        return t;
    }

    /// <summary>
    /// 起栈(身份/防火墙那两步做完之后的第三步)。
    ///
    /// <para>★★★ V22:这里原来是**第二套起栈实现** —— 它自己 `Process.Start` 一个
    /// `localai-lan-edge.exe`,与 `HostProvision.EnsureStackAsync` 平行存在。
    /// 那正是 `EnsureStackAsync` 零调用点却**没人察觉**的原因:界面上确实有一条能起 Edge 的路,
    /// 于是"起栈入口没接上"这件事从表面上看不出来。而两套实现的差别是实打实的:</para>
    /// <list type="bullet">
    ///   <item>这一套**只起 Edge,从来不起网关** ⇒ 起完之后「模型」页仍然是空的;</item>
    ///   <item>两套同时跑会各起一个 Edge,第二个撞 `address already in use`(退出码 4)。</item>
    /// </list>
    /// <para>⇒ 合成一条:这里改调 <see cref="StackBoot"/>(单飞),网关与 Edge 一起起,
    /// 进度与失败原因落在上面那一格里。</para>
    ///
    /// <para>★ 一处**如实交代的行为变化**:`启动Edge.cmd` 那条退路**没有保留**。
    /// 它把绑定地址写死成一台开发机的 192.168.178.61 —— 换台机器就绑到一个不存在的地址上,
    /// 而且**会显示成功**。拿不到网卡地址时现在如实停下来说选不出地址、让人去挑一张,
    /// 那比"起了个绑错地址的中枢"可查得多。</para>
    /// </summary>
    async Task StartEdgeStepAsync(string? bindIp = null)
    {
        Line(bindIp is { Length: > 0 }
            ? $"③ AI 栈:正在启动(网关 + 中枢,中枢绑定 {bindIp}:{HubAdmin.EdgePort})…"
            : "③ AI 栈:正在启动(网关 + 中枢)…");
        Line("★ 进度与失败原因在上面那一格「AI 栈」里。", muted: true);

        var r = await StackBoot.EnsureAsync(force: true, bindIp: bindIp);

        ResetLines();
        Line("③ " + HostProvision.Describe(r.Gateway));
        Line("③ " + HostProvision.Describe(r.Edge));
        if (r.AllUp)
        {
            // ★ 起来了就回到 Host 那一档 —— 让整页重画成"就是中枢主机"那一套。
            Dispatcher.Invoke(() => { _role = HostRole.Unknown; Build(); });
            return;
        }
        if (r.HalfUp) Line("★★ 【起了一半】—— 上面那一格写着缺的是哪一半、缺了它会怎样。");
        Retry();
    }

    /// <summary>用户明确确认之后才铸身份,然后接着往下走。</summary>
    async Task MintThenContinueAsync()
    {
        ResetLines();
        Line("① 中枢身份:正在铸造…", muted: true);
        var id = await HostProvision.EnsureIdentityAsync();
        ResetLines();
        Line(id.Outcome == SetupOutcome.Failed
            ? "① 中枢身份:没弄成 —— " + id.Detail
            : "① 中枢身份:已铸好");
        if (id.Outcome == SetupOutcome.Failed) { Retry(); return; }
        await ContinueAfterIdentityAsync();
    }

    /// <summary>失败之后给一次重试 —— 而且只有一个按钮,别让人在一堆选项里猜。</summary>
    void Retry() => Action(Ui.Secondary("重试", (_, _) => { _ = AutoSetupAsync(); }));

    /// <summary>
    /// 防火墙规则要绑在【某一张网卡】上,所以先让人选一张 —— ★ 不替他挑:
    /// 本机常有虚拟机的仅主机网卡(如 192.168.56.x),放行在那上面等于没放行,而且看起来是成功的。
    /// 只有一张时不啰嗦,直接用。
    /// </summary>
    void BuildNicPicker(StackPanel host, TextBlock status)
    {
        var nics = HostSetup.LocalNics();
        if (nics.Count == 0)
        {
            host.Children.Add(Ui.Caption("没找到启用中的网卡 —— 先把网络连上。"));
            return;
        }
        if (nics.Count == 1)
        {
            // ★ 只有一张网卡就不问 —— 没有可选的东西时弹选择框只是在浪费一次点击
            _ = SetupHostAsync(nics[0].Alias, nics[0].Ip, status);
            return;
        }
        // ★ 画在【自己的子面板】里,不去 Clear 整个动作区 —— 那会把状态行一起摘走,界面从此静默
        var box = new StackPanel();
        box.Children.Add(Ui.Body("这台有多张网卡,防火墙规则要放在【副机能看见的那一张】上,请选一张:"));
        box.Children.Add(Ui.Caption("★ 192.168.56.x 之类通常是虚拟机的仅主机网卡 —— 放在那上面副机看不见,"
                                    + "而且界面会显示成功,那是最难查的一种失败。"));
        foreach (var (alias, ip) in nics)
        {
            var a = alias; var i = ip;
            var b = Ui.Secondary($"{a} · {i}", (_, _) => { box.Visibility = Visibility.Collapsed; _ = SetupHostAsync(a, i, status); });
            b.Margin = new Thickness(0, 4, 0, 0);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            box.Children.Add(b);
        }
        host.Children.Add(box);
    }

    /// <summary>
    /// 按顺序把这台装成主机。★ 一步失败就停 —— 后面几步在前一步没成的前提下做了也白做,
    /// 而且会用一串新错误盖住真正的原因。
    /// </summary>
    async Task SetupHostAsync(string nicAlias, string nicIp, TextBlock status)
    {
        void Say(string s) => Dispatcher.Invoke(() => status.Text = s);
        string Mark(SetupStep st) => st.Outcome switch
        {
            SetupOutcome.Ok => "完成",
            SetupOutcome.Skipped => "本来就好的",
            _ => "没成",
        };

        Say("① 中枢身份:正在检查/铸造…");
        var id = await HostProvision.EnsureIdentityAsync();
        Say($"① 中枢身份:{Mark(id)} —— {id.Detail}");
        if (id.Outcome == SetupOutcome.Failed) return;

        var script = HostProvision.FirewallScript();
        var dir = AdminApp.HostToolsDir();
        if (script is null || dir is null)
        {
            Say($"① 中枢身份:{Mark(id)}。② 防火墙:找不到 lan-firewall.ps1 —— "
                + "请手动放行 8443(用管理员 PowerShell 跑 90-ops/lan/lan-firewall.ps1),或先跳过、只在本机用。");
            return;
        }
        Say($"① 中枢身份:{Mark(id)}。② 防火墙:马上会弹一次管理员授权框…");
        var fw = await HostProvision.EnsureFirewallAsync(nicAlias, script, Path.Combine(dir, "localai-lan-edge.exe"));
        Say($"① 身份:{Mark(id)}。② 防火墙:{Mark(fw)} —— {fw.Detail}");
        // ★ 防火墙没成也【继续】起栈:本机自己用是通的,只是副机连不上。
        //   直接中止会让人以为整套都废了 —— 那不是实情。
        // ★★ V22:这里原来先查一次 `HubAdmin.StartEdgeCmd()`,查不到就**整个不起**。
        //   那条判据在 `启动Edge.cmd` 那条退路撤掉之后已经没有意义了 ——
        //   而它留着的后果是:一台没有那个 .cmd 的机器上,身份和防火墙都做完了,
        //   栈却因为"找不到一个我们根本不打算用的脚本"而不起,理由还写在界面上。
        await StartEdgeStepAsync(nicIp);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ★★★ `StartEdgeAsync` 已删（V22 · 2026-08-09）—— 它是**第二套起栈实现**。
    //
    //  它与 `HostProvision.EnsureStackAsync` 平行存在，而且正是它让后者「零生产调用点」
    //  这件事**从表面上看不出来**：界面上确实有一条能起 Edge 的路。
    //  两套的差别是实打实的：
    //    · 这一套**只起 Edge、从来不起网关** ⇒ 起完之后「模型」页仍然是空的；
    //    · 两套同时跑会各起一个 Edge，第二个撞 `address already in use`（退出码 4）。
    //
    //  ★★ 它身上那几条硬救回来的约束**一条都没丢**，只是换了地方：
    //    无窗口启动 + 收 stdout/stderr + 重定向 stdin（不进 REPL）+ 探到才算起来 +
    //    失败时把中枢自己打印的最后几行原文摆出来 —— 全在 `HostProvision.EnsureEdgeAsync`
    //    与上面那一格「AI 栈」里。自检那几条断言已改钉**那条活路径**（SelftestMoved ⑥）。
    //
    //  ★ 一处如实交代的行为变化：`启动Edge.cmd` 那条退路**没有保留**。
    //    它把绑定地址写死成一台开发机的 192.168.178.61 —— 换台机器就绑到一个
    //    不存在的地址上，而且**会显示成功**。现在拿不到网卡地址就如实停下来说选不出地址。
    // ════════════════════════════════════════════════════════════════════════

    // ================================================================ 主机侧
    /// <summary>
    /// 「这台电脑上的中枢」这一格。
    ///
    /// <para>★★★ V21 与客户端那一版的差别,逐条写清(不是删,是**归位**):</para>
    /// <list type="bullet">
    ///   <item>「本机客户端已配对/状态/证书告警」那一段**没有跟过来** —— 那是**客户端自己**的状态
    ///     (`HubClient.State` / `Profile` / `CertWarning`),住在客户端进程里。
    ///     管理端去重画一份就是**第二份真相**,而两份一定会漂。
    ///     ⇒ 这里只说管理端**自己**验得出来的两件事:中枢答不答话、它的服务器证书要不要管。
    ///     客户端那台的连接状态在**客户端的**「设备」页上,那儿一个字都没少。</item>
    ///   <item>`SelfPairAsync`(一键自配对)**没有跟过来,也没有留在客户端** —— 见文件头。
    ///     取而代之的是下面这句**可执行的下一步**:主机上的客户端也去敲门,回这一页批准。</item>
    ///   <item>`ChangeDialRow`(改拨号地址)留在客户端:它改的是 `profile.json`,
    ///     而那个文件归客户端**独占写**(纪律③)。</item>
    /// </list>
    /// </summary>
    UIElement HostSelfCard()
    {
        var stack = Ui.Stack(
            Ui.Subtitle("这台电脑就是中枢主机"),
            Ui.Body($"hub {Admin.HubId}", muted: true),
            Ui.Caption("★ 判据是【肯定证据】:本机回环管理面答话了,而且它自报的 hub id 与本机档案一致。"));

        // ★★★ 主机侧证书自动轮换的告警落点。**这一格在此之前是空的**:
        //   /admin/ping 一直在吐 serverCert,lan-edge 那行注释写着「主机界面据此报警」,
        //   而全仓没有任何读取方 —— fail-closed 的最后一段路断在这里,状态吐出来了却没人读。
        //   轮换器另一条通道(stderr 的 [cert] !! 横幅)只落在那个控制台窗口里 ⇒
        //   两条通道都到不了人眼前,失败就会一路静默滑到证书过期。
        // ★ 只在 NeedsAttention 时出现:轮换正常工作时一个字都不说(否则两周内就被学会忽略)。
        if (Admin.ServerCertWarning is { Length: > 0 } scw)
        {
            stack.Children.Add(new Border { Height = 10 });
            stack.Children.Add(Ui.Body(scw));
        }

        // ── 主机上的那台客户端 ──────────────────────────────────────
        // ★ 主机这台【也必须配对】,这一点不能含糊:业务口只绑在网卡 IP 上
        //   (`k.Listen(cfg.Bind, 8443)`),回环上只有管理面。所以本机客户端要聊天,
        //   同样得有成员证书,同样得走一次 enroll + 批准。
        stack.Children.Add(new Border { Height = 10 });
        if (ClientProfilePeek.ClientPaired())
        {
            stack.Children.Add(Ui.Body("这台上的客户端:【已配对】(" + (ClientProfilePeek.Dial() ?? "地址未记录") + ")"));
            stack.Children.Add(Ui.Caption("★ 它现在连得上连不上,看【客户端】自己的「设备」页 —— "
                                          + "那是它自己的状态,这里不复述(两份会漂)。"));
        }
        else
        {
            stack.Children.Add(Ui.Body("这台上的客户端:【还没配对】。"));
            stack.Children.Add(Ui.Caption("★ 走法和任何一台副机【完全一样】,只是两块屏幕在同一张桌子上:\n"
                                          + "  ① 打开这台的客户端 →「设备」→ 点「开始寻找主机」;\n"
                                          + "  ② 它屏幕上会出现六个词;\n"
                                          + "  ③ 回到这一页下面的「＋ 添加一台新电脑」,逐字核对那六个词,再批准。"));
            stack.Children.Add(Ui.Caption("★★ 这里【不提供】「一键自己配自己」。旧版有,而它明文跳过了六词比对 —— "
                                          + "跨进程之后没法既保住那条捷径又保住纪律,\n"
                                          + "  而在两者之间,逐字比对是不能让的那一个(D47)。"));
            if (!ClientLink.IsClientRunning())
            {
                stack.Children.Add(Ui.Secondary("打开这台的客户端", (_, _) =>
                {
                    // ★★ 实机反馈③的**第二处**。这里原来把 `(ok, why)` 整个丢掉了 ——
                    //   起不来时用户点完**什么也不会发生**,连个说法都没有。
                    //   「不要弹窗」要的是别拿弹窗报喜,不是把报忧也一起吞掉。
                    //   判词:跑不了和跑过了必须长得不一样。
                    var (ok, why) = ClientLink.StartClient(tray: false);
                    _clientStartError = ok ? null : "没能打开:" + why;
                    Build();
                }));
                if (_clientStartError is { Length: > 0 } err) stack.Children.Add(AdminNotice.Failure(err));
            }
        }

        stack.Children.Add(new Border { Height = 10 });
        stack.Children.Add(RecheckRow());
        return Ui.Card(stack);
    }

    /// <summary>「已配对的电脑」+「添加一台新电脑」。后者默认收起 —— 展开才开窗。</summary>
    UIElement HostDevicesCard()
    {
        _devList = new StackPanel();
        _addPanel = new StackPanel { Visibility = Visibility.Collapsed };
        _addStatus = Ui.Caption("");

        // ★ 「可见即开」只对这一块生效,而它默认收起 ⇒ 只有主机、没有副机的人永远不会无意中开窗。
        //   展开这个动作本身就是明确意图,不用再去别处找开关。
        _addToggle = Ui.Secondary("＋ 添加一台新电脑", async (_, _) => await ToggleAddAsync());

        var stack = Ui.Stack(
            Ui.Subtitle("已配对的电脑"),
            _devList,
            new Border { Height = 12 },
            _addToggle,
            _addStatus,
            _addPanel);

        _ = LoadDevicesAsync();
        return Ui.Card(stack);
    }

    async Task LoadDevicesAsync()
    {
        var (ok, devices) = await Admin.DevicesAsync();
        var why = ok ? null : Admin.LastError;
        Dispatcher.Invoke(() =>
        {
            if (_devList is null) return;
            _devList.Children.Clear();
            if (!ok)
            {
                // ★ 取不到 ≠ 一台都没有。写成"没有别的电脑"会让人把在册的机器再配一次。
                _devList.Children.Add(Ui.Caption("没能取到设备列表(" + (why ?? "原因不明")
                                                 + ")—— 这【不等于】没有别的电脑在册,别据此重复配对。"));
                return;
            }
            // ★★★ 真拿到了才缓存 —— 项目「文件夹所在机器」下拉复用这一份(ProjectEditor.MachineOptions)。
            //
            //   ★ 这一行以前在 `RenderDevices` 里,而 `RenderDevices` **一个调用方都没有**
            //     (V19 · 2026-08-08 实测:全仓只有它自己的声明,以及自检里拿它当切片边界的一处)。
            //     ⇒ `CacheDevices` 今天**根本没人调**,`KnownDevices` 是**结构性恒空**,
            //       那个下拉从来只有「本机」一项 —— 而 Selftest 那条断言照绿,
            //       因为它只在 HubClient.cs 里 grep 到了这两个**名字**(声明,不是调用)。
            //   ⇒ 挪到这条**活路径**上来(LoadDevicesAsync ← :921 真的被调),并配一条能为假的断言。
            //
            //   ★★ 给下一轮迁移的人:主机侧这一段搬进管理端之后,客户端就**再也没有**
            //     `CacheDevices` 的写入点了。那不是"顺手带走一行",那是把那个下拉**功能删掉**。
            //     ⇒ 那条断言会当场红,请在那里做决定(接回来 / 还是连同下拉一起撤掉),
            //       不要把断言改宽让它闭嘴。

            // ★ provisioning = 批准了但对方没来领证(常见于两边截止时间不一致那一档)。
            //   混在"已配对"里会让人以为配好了,而它其实是个没走完的半截 —— 要标出来。
            var live = devices.Where(d => d.Status != "revoked").ToList();
            if (live.Count == 0)
            {
                _devList.Children.Add(Ui.Caption("还没有别的电脑配对进来。"));
                // ★ 说清"在线/离线"这一档现在给不出来,别让人以为列表里的都在线
                _devList.Children.Add(Ui.Caption("★ 这里只列【在册】的电脑。「现在在不在线」中枢侧还没透出来"
                                                 + "(设备记录里有 LastSeenAt,管理面还没带上它)—— 已写进决议包。"));
                return;
            }
            foreach (var d in live) _devList.Children.Add(DeviceRow(d));
            _devList.Children.Add(Ui.Caption("★ 这里列的是【在册】,不是【在线】。「现在连着没有」中枢侧还没透出来 —— 已写进决议包。"));
        });
    }


    // ================================================================ 配对窗口:只有一个所有者
    // ★ 用户的顾虑:「只有主机没有副机,岂不是窗口永远开着关不了?」
    //   原来的答案是三道闸(收起关 / 宽限 90 秒 / 中枢分钟上限)。审计指出那是**替中枢记账**:
    //   `_addExpanded` 与 `_graceUntil` 是客户端自己编的一份状态,而中枢早就在
    //   /admin/ping 与 /admin/pairing/pending 里自报 pairingWindowOpen —— HubAdmin 一直解析并存着它,
    //   全仓从没读过。两份账一定会对不上,而且真的对不上了:
    //     · 批准/拒绝/解除任一按钮触发 Build() 重建控件,而 _addExpanded 不复位
    //       ⇒ 窗口还开着,界面却显示收起,按钮说的和做的相反;
    //     · 窗口到点被中枢自己关了,界面还写着"已打开",两台屏幕互相指着对方。
    //   ⇒ 删掉本地那份账。渲染只读 admin.PairingWindowOpen,Build() 重建就没有状态可对不上。
    //   剩下的是**两道真闸**:中枢自己的分钟上限(与客户端死活无关)+ 离开这一页时关窗。
    //   「收起就关」退化成一个动作,不再需要本地布尔去记它。

    async Task ToggleAddAsync()
    {
        var admin = Admin;
        if (admin.PairingWindowOpen) { await CloseWindowAsync(); return; }

        var (st, body) = await admin.WindowAsync(true, WindowMinutes);
        if (st == 200) await admin.PendingAsync();      // 立刻回读一次,让 PairingWindowOpen 是真的
        Dispatcher.Invoke(() =>
        {
            if (_addStatus is not null)
                _addStatus.Text = st == 200
                    ? $"配对窗口已打开,最多 {WindowMinutes} 分钟后由中枢自己关掉;收起这一块或离开本页也会关。"
                    : $"没能打开配对窗口({st} {body})—— 副机现在配不进来。";
            RenderAddSection();
        });
        StartPendPolling();
    }

    async Task CloseWindowAsync(bool quiet = false)
    {
        StopPendPolling();
        try { await Admin.WindowAsync(false); } catch { /* 关不掉也有中枢侧的分钟上限兜底 */ }
        try { await Admin.PendingAsync(); } catch { }   // 回读真实状态
        if (quiet) return;
        Dispatcher.Invoke(() =>
        {
            if (_addStatus is not null) _addStatus.Text = "配对窗口已关闭。";
            RenderAddSection();
        });
    }

    /// <summary>
    /// 按【中枢自报的】窗口状态渲染这一块。★ 不看任何本地布尔 ——
    /// 这就是"两份账对不上"这一类 bug 的根治办法:只有一份账,而且在中枢那边。
    /// </summary>
    void RenderAddSection()
    {
        var open = Admin.PairingWindowOpen;
        if (_addToggle is not null) _addToggle.Content = open ? "－ 收起(收起就关掉配对窗口)" : "＋ 添加一台新电脑";
        if (_addPanel is not null) _addPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    void StartPendPolling()
    {
        if (_pendTimer is not null) return;
        _pendTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pendTimer.Tick += async (_, _) => await PollPendingAsync();
        _pendTimer.Start();
    }

    void StopPendPolling() { _pendTimer?.Stop(); _pendTimer = null; }

    async Task PollPendingAsync()
    {
        var admin = Admin;
        var (ok, pend) = await admin.PendingAsync();

        Dispatcher.Invoke(() =>
        {
            // ★ 每一轮都按中枢自报的状态重画开关 —— 窗口被中枢到点关掉时,界面立刻跟上,
            //   不会再出现"界面写着已打开、其实早关了"这种两台屏幕互相指着对方的情形。
            RenderAddSection();
            if (_addPanel is null) return;
            _addPanel.Children.Clear();
            _addPanel.Children.Add(Ui.Body("等待副机来配对"));
            if (!ok)
            {
                _addPanel.Children.Add(Ui.Caption("没能从管理面取到待批准列表(" + (admin.LastError ?? "原因不明")
                                                  + ")—— 这【不等于】没有请求。先别让对方重新配对。"));
                return;
            }
            if (pend.Count == 0)
            {
                _addPanel.Children.Add(Ui.Caption("现在没有等待批准的请求。到那台新电脑上打开客户端,"
                                                  + "在同一页里选中这台主机、点「开始配对」。"));
                return;
            }
            foreach (var p in pend) _addPanel.Children.Add(PendingRow(p));
        });

        // ★★ 这里原来是"新请求一到就自动弹框"。审计指出:enroll 是**匿名**的,
        //   弹窗因此变成一个【局域网上任何人都能触发的动作】,由对方的到达时机决定你屏幕上跳出什么。
        //   ⇒ 改成不自动弹:待批准的只进列表(PendingRow 上本来就有批准/拒绝),
        //     由你主动点某一条才弹确认。准入的节奏归你,不归发起方。
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ★★★ `ShowApprovalDialogAsync` 已删(用户裁定 A · 2026-08-08)。
    //
    //  它是"新请求一到就自动弹框"那条路的弹窗。那条路先被停掉(见上面 PollPendingAsync
    //  收尾那段:enroll 是**匿名**的 ⇒ 自动弹窗等于把「你屏幕上跳出什么」交给
    //  局域网上的任何人,由对方的到达时机说了算),弹窗本体就此**零调用方**。
    //  它的伴生字段 `_dialogOpen` 也只写不读(编译器 CS0414 一直在说这件事),一并删。
    //
    //  ★★ 它躺了多久没人知道,而**期间有 3 条自检断言钉在它身上** ——
    //    也就是说那 3 条**测的是死代码**:把活路径上的六词比对整个删掉,它们照样绿。
    //    ⇒ 判据已改钉活路径 `PendingRow`,并补了一条**不挑函数**的:
    //      DevicesView 里**每一个** `ApproveAsync` 入口之前都必须有六词【逐字】比对
    //      (只有 `SelfPairAsync` 是登记在册的例外,且它自己那道闸被单独钉住)。
    //
    //  ★ 准入的六词判词**一个字都没少** —— 它现在写在真正会跑的那条路上:
    //    `PendingRow` 的按钮是「词一致,批准」,确认框是「逐字核对过了,批准」,
    //    六个词由 `p.Sas` 摆在屏幕上,旁边就是拒绝。
    //
    //  ★★★ 要再做「点某一条才弹确认」的话:那是**新写一条活路径**,
    //    上面那条不挑函数的判据会自动咬住它(弹窗里必须出现「逐字」)。
    //    **不要**把这段历史当模板复活 —— 它的价值在这段注释里,不在那 36 行代码里。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 把自报的显示名收拾干净再往界面上放:剔掉控制字符与换行,截到 48 字。
    /// ★ 理由不是好看:~32 KB 的名字能把批准框撑到屏幕外,按钮就点不到了。
    ///   任何来自网络的文本都不该能决定窗口尺寸。
    /// </summary>
    static string SafeDisplayName(string? raw)
    {
        var t = new string((raw ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (t.Length == 0) return "(没有名字)";
        return t.Length <= 48 ? t : t[..48] + "…";
    }

    /// <summary>
    /// 一条待批准的请求。★ 六个词要与对方屏幕上【逐字一致】才能按批准 ——
    /// 界面只把两边的词摆出来,**绝不代人比对**,也不提供"跳过"。
    /// </summary>
    UIElement PendingRow(PendingPair p)
    {
        var box = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        box.Children.Add(Ui.Body(p.DisplayName.Length > 0 ? p.DisplayName : "(未自报名)"));
        var words = new TextBlock
        {
            Text = string.Join("  ", p.Sas),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 18, Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap,
        };
        words.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        box.Children.Add(words);
        box.Children.Add(Ui.Caption($"这六个词必须与那台电脑屏幕上显示的【逐字一致】。剩余 {Math.Max(0, p.SecondsLeft)} 秒。"));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var deny = Ui.Danger("拒绝", async (_, _) =>
        {
            var (dst, dbody) = await Admin.DenyAsync(p.RequestId);
            if (dst != 200) ConfirmDialog.Show("没能拒绝", $"中枢回了 {dst}:{dbody}", confirmText: "知道了", cancelText: "关闭");
            Build();
        });
        var ok = Ui.Primary("词一致,批准", async (_, _) =>
        {
            if (!ConfirmDialog.Show("批准这台电脑",
                    "确认那台电脑屏幕上显示的六个词与这里【逐字一致】吗?\n\n" + string.Join("  ", p.Sas)
                    + "\n\n★ 不一致就意味着中间有人 —— 这时候必须点取消。",
                    confirmText: "逐字核对过了,批准", cancelText: "取消")) return;
            var (ast2, abody2) = await Admin.ApproveAsync(p.RequestId);
            if (ast2 != 200)
                ConfirmDialog.Show("没能批准",
                    ast2 == 409
                        ? "这条请求已经过期或已被处理了。请让对方重新点一次「开始配对」。"
                          + Environment.NewLine + Environment.NewLine + "(中枢原话:" + abody2 + ")"
                        : $"中枢回了 {ast2}。" + Environment.NewLine + Environment.NewLine + abody2,
                    confirmText: "知道了", cancelText: "关闭");
            Build();
        });
        ok.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(ok);
        row.Children.Add(deny);
        box.Children.Add(row);

        var card = new Border { Child = box, Padding = new Thickness(10), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BorderBrushProperty, "Accent");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        return card;
    }

    UIElement DeviceRow(AdminDevice d)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6), LastChildFill = true };
        // ★★ 用户裁定:列表里【能看到自己,但不能移除自己】—— 自己就是主机,
        //   把主机自己那条解除掉,等于让这台机器自己把自己踢出去。
        var isSelf = IsThisMachine(d);
        if (!isSelf)
        {
            var revoke = Ui.Danger(Strings.Get("devices.revoke"), async (_, _) =>
            {
                if (!ConfirmDialog.Show(Strings.Get("devices.revoke"),
                        Strings.Get("devices.revoke_confirm", ("device", SafeDisplayName(d.DisplayName))),
                        confirmText: Strings.Get("devices.revoke"), danger: true)) return;
                var (rs, rb) = await Admin.RevokeAsync(d.DeviceId);
                if (rs != 200) ConfirmDialog.Show("没能解除", $"中枢回了 {rs}:{rb}", confirmText: "知道了", cancelText: "关闭");
                Build();
            });
            DockPanel.SetDock(revoke, Dock.Right);
            row.Children.Add(revoke);
        }
        // 自报名可能含恶意内容:只作显示(WPF 文本节点已转义),永不进 prompt。
        // ★ 同名设备很常见(实机就有两条 SENIORBIRDS)—— 必须带上证书指纹短码,只按名字分不开。
        var col = new StackPanel();
        col.Children.Add(Ui.Body($"{SafeDisplayName(d.DisplayName)}   ·   {d.Status}"
                                 + (isSelf ? "   ·   这台(主机)" : "")));
        col.Children.Add(Ui.Caption("指纹 " + (d.CertShort ?? "(无活动证书)") + "   ·   " + d.DeviceId));
        if (isSelf) col.Children.Add(Ui.Caption("★ 自己是主机,不能在这里把自己解除。"));
        row.Children.Add(col);
        return row;
    }

    /// <summary>
    /// 这一条是不是【这台主机上的客户端】。★ 按证书指纹认,不按名字 —— 同名设备很常见,
    /// 而名字还是自报的。中枢给的是 SHA256 短码,拿那张证书算一遍前缀比对。
    ///
    /// <para>★★ V21:证书从**客户端的档案**里只读取出(`ClientProfilePeek`)。
    /// 管理端进程里没有 `HubClient`,而这条判据承的是 D47 用户裁定
    /// 「列表里能看到自己,但**不能移除自己**」—— 把主机自己那条解除掉,
    /// 等于让这台机器自己把自己踢出去。</para>
    ///
    /// <para>★★★ 认不出时的方向是**有意**选的,而且和客户端那一版**相反**:
    /// 客户端那版认不出就当「不是自己」(宁可多给一个解除按钮)。
    /// 在管理端里这个方向是**错的** —— 读不到客户端档案是个**常见**状态
    /// (客户端还没配过对 / 档案被锁),而那时把主机自己那条也摆上解除按钮,
    /// 一点下去整台主机就把自己踢出成员表了。
    /// ⇒ 这里 fail-closed:**读不到就不敢说它不是自己**,那一条不给解除按钮,
    /// 并在行里如实写明「认不出是不是这台」。</para>
    /// </summary>
    bool IsThisMachine(AdminDevice d)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(d.CertShort)) return false;
            var b64 = ClientProfilePeek.DeviceCertB64();
            if (string.IsNullOrWhiteSpace(b64)) return true;   // ★ 读不到 ⇒ fail-closed(见上面第三段)
            var raw = Convert.FromBase64String(b64);
            var hex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw));
            return hex.StartsWith(d.CertShort.Replace(":", "").Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }   // ★ 同上:算不出来就不敢给解除按钮
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ★★★ `RenderDevices` 已删(V19 · 2026-08-08)—— 它**一个调用方都没有**。
    //
    //  实测(全仓,排除 bin/obj):只有它自己的声明,加上自检里拿它当 `Slice` 边界的一处。
    //  它是 `DeviceRow` / `LoadDevicesAsync` 那条活路径的**旧副本**:
    //  少了「自己不能解除自己」(D47 用户裁定)、少了指纹短码、少了 provisioning 那一档 ——
    //  也就是说,它要是哪天真的被接回去用,会**悄悄退回**三条已经修过的缺陷。
    //
    //  ★★ 而它带走的不只是死代码:`CacheDevices` 的**唯一写入点**在它里面。
    //    ⇒ 今天 `HubClient.KnownDevices` 是**结构性恒空**,项目「文件夹所在机器」那个下拉
    //      从来只有「本机」—— 而 Selftest 那条断言**照绿**,因为它 grep 的是
    //      `HubClient.cs` 里的两个**名字**(声明),不是有没有人调。
    //      这正是「功能没了而断言照绿」。写入点已挪到 `LoadDevicesAsync` 那条活路径上。
    //
    //  ★ `HubClient.ParseDevices` 现在只剩自检在调 —— **本轮不动它**:
    //    它的去留是 V10 §2.4 的事(那一族打的是副机结构上永远 404 的路由),
    //    而那要连着 `ListDevicesRawAsync` / `RevokeDeviceAsync` 一起裁。
    //
    //  ★★★ V21 后记:上面最后一句里的那件事**已经做了** —— `HubClient` 里
    //    `ListDevicesRawAsync` / `KnownDevices` / `CacheDevices` / `RevokeDeviceAsync` /
    //    `ParseDevices` 整组已从客户端**净删**(V10 §2.4),而不是搬过来:
    //    它们打的是副机结构上永远 404 的路由。唯一的设备表解析器现在就在
    //    `LocalAI.Admin.Services.HubAdmin.ParseDevices` —— 也就是这一页在用的那一个。
    // ════════════════════════════════════════════════════════════════════════
}
