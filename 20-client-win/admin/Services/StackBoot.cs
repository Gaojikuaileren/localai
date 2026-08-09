// V22 -- 管理端一起来就把栈起起来。**本文件就是 `EnsureStackAsync` 的生产调用点**。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ 它补的是什么(用户裁定 2026-08-09:「我不要手工起,**必须自动起**」):
//
//  在此之前 `HostProvision.EnsureStackAsync` **全仓零生产调用点** ——
//  唯一引用它的是 `admin/SelftestMoved.cs`。函数写好了、注释里写着「全仓唯一的起栈入口」、
//  自检还验着它的 AllUp/HalfUp 逻辑,而**界面上没有任何东西会触发它**。
//  V21 把客户端那个入口删掉(元断言钉住并红测过)之后,实机上的后果是:
//  **栈谁都起不了**,只能让用户自己去跑 `90-ops\start-stack.ps1`。
//
//  ★ 这是本项目第四次同形(A5 的 `TlsFailure` · doctor ⑫ 环 · `loader.shutdown()` · 这条),
//    而自检**测不到它**:验的是 `StackResult` 的逻辑,那个逻辑永远不会被界面触发。
//    ⇒ V22 立了一条元断言:**凡被文案/注释称为「唯一入口」的函数,必须有生产调用点**。
//      本文件里那一行 `HostProvision.EnsureStackAsync(...)` 就是它咬住的那个调用点 ——
//      删掉它,自检当场红(红测记录在决议包里)。
// ══════════════════════════════════════════════════════════════════════════════
//
// ★★ 两条启动路径都盖到,而且盖法是**结构性**的:
//   `App.OnStartup` 对 `localai-admin`(双击)与 `localai-admin --tray`(客户端拉起)
//   是**同一个**方法 —— 起栈挂在那里,两条路就都走得到,不靠任何 if 去分辨。
//
// ★ 不提权:本进程自己就不提权(Program.cs 的 D46 护栏),子进程继承本进程的等级 ⇒
//   网关与 Edge 天然是普通身份。这条不需要额外代码,但需要**不写**任何 `Verb = "runas"`。
//
// ★★★ 起栈**不阻塞界面**:`OnStartup` 里是 fire-and-forget,进度由 `Changed` 播出去。
//   阻塞的话,双击图标之后窗口要等到两个 20 秒超时都走完才出现 —— 那和没起来看着一样。

using LocalAI.Client.Services;

namespace LocalAI.Admin.Services;

/// <summary>起栈这件事现在走到哪一步了。</summary>
public enum StackPhase
{
    /// <summary>还没开始。</summary>
    Idle,
    /// <summary>正在起。★ 界面必须显示这一档 —— 两个 20 秒的窗口里,不说话的页面看着像死了。</summary>
    Working,
    /// <summary>起完了(成没成看 <see cref="StackBoot.Last"/>)。</summary>
    Done,
}

public static class StackBoot
{
    static readonly object _gate = new();
    static Task<HostProvision.StackResult>? _inflight;
    static readonly List<string> _lines = new();

    public static StackPhase Phase { get; private set; } = StackPhase.Idle;

    /// <summary>最近一次起栈的结果。★ 用 <see cref="HostProvision.StackResult"/> 本身,
    /// 不另造模型 —— AllUp / HalfUp 已经建好了。</summary>
    public static HostProvision.StackResult? Last { get; private set; }

    /// <summary>起栈过程里逐步报出来的话。★ 界面照抄,不加工。</summary>
    public static IReadOnlyList<string> Lines { get { lock (_gate) return _lines.ToList(); } }

    /// <summary>状态变了。★ 界面挂这个,不要轮询。</summary>
    public static event Action? Changed;

    /// <summary>自检闸的环境变量名(见 <see cref="EnsureAsync"/> 里那段)。
    /// ★ 名字放在这里、由自检引用,是为了不出现两个字面量各写一个。</summary>
    public const string NoAutoStackEnvVar = "LOCALAI_ADMIN_NO_AUTOSTACK";

    static void Raise() { try { Changed?.Invoke(); } catch { } }

    /// <summary>
    /// 把栈起起来。★ **单飞**:同时有几个地方调(启动一次、界面「重试」一次)只会真起一次。
    ///
    /// <para>★ 幂等由下面那一层保证:`EnsureGatewayAsync` / `EnsureEdgeAsync` 先探再起,
    /// 已经在跑的直接 Skipped —— 所以重复调不会起出第二套。</para>
    /// </summary>
    /// <param name="force">true = 就算已经跑过一轮也再来一次(界面上的「重试」)。</param>
    /// <param name="bindIp">用户在界面上挑的网卡地址(挑过才传)。</param>
    public static Task<HostProvision.StackResult> EnsureAsync(bool force = false, string? bindIp = null)
    {
        // ══════════════════════════════════════════════════════════════════
        //  ★★★ V23 · **自检闸**:自检要走的是【关栈】那条路,它不许顺手起一套真的栈。
        //
        //  自检里 `④c 托盘右键真关闭` 会真的 `new App(...)` 并跑 `OnStartup` ——
        //  而 `OnStartup` 里就是那条 `StackBoot.EnsureAsync()` 生产调用点。
        //  不拦的话,一次自检会在用户机器上**真的起一个网关和一个 Edge**
        //  (仓库形态下 `LocateGateway()` 找得到真东西),而且它们不会被收走。
        //
        //  ★ 闸开在**起栈**这一侧,不开在关栈那一侧 —— 被测的是关栈,
        //    替身打在被测者身上就是 ASSERTION-PITFALLS 第 13 条那个坑(已踩 2 次)。
        //  ★★ 生产**从不设**这个变量:它由自检自己给子进程设,并有一条断言钉着
        //    「产品源码里不许出现给它赋值」(Selftest,红测过)。
        //  ★★★ 而且**不装成起成功了**:如实报 Failed + 说清是自检闸拦的 ——
        //    装成 Ok 会让「起栈」那一片断言在自检里变成恒绿。
        // ══════════════════════════════════════════════════════════════════
        if (Environment.GetEnvironmentVariable(NoAutoStackEnvVar) == "1")
        {
            const string why = "自检闸(" + NoAutoStackEnvVar + "=1)拦下了自动起栈 —— "
                             + "自检不许在你的机器上真起一套栈。";
            var blocked = new HostProvision.StackResult(
                new SetupStep($"统一入口网关 :{HostSetup.GatewayPort}", SetupOutcome.Failed, why),
                new SetupStep($"LAN Edge :{HostProvision.EdgePort}", SetupOutcome.Failed, why));
            lock (_gate) { Last = blocked; Phase = StackPhase.Done; _lines.Clear(); _lines.Add("★ " + why); }
            Raise();
            return Task.FromResult(blocked);
        }

        lock (_gate)
        {
            // ★★★ 单飞是**承重**的,不是优化:管理端启动时会调一次,而用户这时正好打开
            //   「主机中枢」那一页又会调一次。两边同时探到「Edge 没起」⇒ 各起一个 ⇒
            //   第二个撞 `address already in use`(退出码 4),屏幕上一个好的、一个吐一屏
            //   Kestrel 异常栈。2026-08-04 实测撞过一次,V22 是把两条路合成一条来根治。
            if (_inflight is { IsCompleted: false }) return _inflight;      // 正在起,跟着这一轮
            if (!force && Last is not null) return Task.FromResult(Last);   // 起过了
            _lines.Clear();
            Phase = StackPhase.Working;
            _inflight = RunAsync(bindIp);
            return _inflight;
        }
    }

    static async Task<HostProvision.StackResult> RunAsync(string? bindIp)
    {
        Raise();
        var progress = new Progress<string>(line =>
        {
            lock (_gate)
            {
                // ★ 同一步的「正在启动…」会被它自己的结论顶掉 —— 否则页面上会留一串
                //   「正在启动…」+「已起来」的重复行,而人读到的是"它起了两次"。
                var head = line.Length >= 2 ? line[..2] : line;
                if (_lines.Count > 0 && _lines[^1].StartsWith(head, StringComparison.Ordinal))
                    _lines[^1] = line;
                else _lines.Add(line);
            }
            Raise();
        });

        HostProvision.StackResult result;
        try
        {
            // ★★★ 这里就是那条生产调用点。元断言咬的是这一行。
            result = await HostProvision.EnsureStackAsync(progress, bindIp);
        }
        catch (Exception ex)
        {
            // ★ fire-and-forget 调的 —— 不兜住的话界面永远停在「正在起…」,而没人知道为什么。
            var why = ex.GetType().Name + ": " + ex.Message;
            result = new HostProvision.StackResult(
                new SetupStep($"统一入口网关 :{HostSetup.GatewayPort}", SetupOutcome.Failed, "起栈过程本身出错:" + why),
                new SetupStep($"LAN Edge :{HostProvision.EdgePort}", SetupOutcome.Failed, "起栈过程本身出错:" + why));
            lock (_gate) _lines.Add("★ 起栈过程出错:" + why);
        }

        lock (_gate) { Last = result; Phase = StackPhase.Done; }
        Raise();
        return result;
    }

    /// <summary>
    /// 关栈之后把状态清回去。★ 不清的话,「主机中枢」那一页会一直显示上一次起栈的绿灯,
    /// 而栈已经没了 —— 又一次「失败与成功长得一样」。
    /// </summary>
    public static void Forget()
    {
        lock (_gate) { Last = null; Phase = StackPhase.Idle; _lines.Clear(); _inflight = null; }
        Raise();
    }
}
