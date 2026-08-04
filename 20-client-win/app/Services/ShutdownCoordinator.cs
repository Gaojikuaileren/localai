// P3c -- 退出善后。用户要求:「客户端退出的时候也要关闭窗口,释放显存,做好关闭善后」,
// 同时「关窗口保持后台任务栏图标」—— 所以必须严格区分两条路径:
//
//   关窗口(X)      -> 隐藏到托盘,**不**善后(后台继续,显存该留就留)
//   真正退出        -> 善后**恰好一次**:断开会话 · 停后台任务 · 通知主机释放显存
//
// 真正退出有四个入口,全部汇到这里,靠 Interlocked 保证只跑一次:
//   ① 托盘菜单「退出」 ② 主窗口菜单退出 ③ Windows 关机/注销(SessionEnding) ④ 进程退出兜底
//
// 纪律:善后是 best-effort + 有超时。绝不允许一个卡住的清理步骤把 Windows 关机拖住
// (系统给应用的时间有限,超时后会被强杀,那样反而更脏)。

using System.Collections.Concurrent;
using System.Diagnostics;

namespace LocalAI.Client.Services;

public sealed record CleanupStep(string Name, Func<CancellationToken, Task> Run);

public sealed class ShutdownCoordinator
{
    /// <summary>整个善后流程的硬上限。超时即放弃剩余步骤并退出(见文件头纪律)。</summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// **单步**上限。★ 2026-08-04 加:只有总预算是不够的 —— 步骤是**顺序**跑的,
    /// 一个慢步骤会把总预算吃光,后面的步骤全被 SKIP 掉。
    /// 实测形状:② `end-session+release-vram` 要发一次真实网络请求(`Transport.Send` 每次现建
    /// HttpClient 走完整 mTLS 握手,**用的是默认 100 秒超时**),中枢没起/网关没起时它就干等;
    /// 于是 ③ `save-settings`(界面偏好)· ④ 停显存监视 · ⑤ 收托盘图标 **一个都跑不到** ——
    /// 用户看到的是「关闭的时候卡一段时间」,而且**设置还没保存上、托盘图标还赖在那儿**。
    /// ⇒ 网络步骤必须有自己的短上限,不许它替本地步骤把预算花完。
    /// 1.5 秒对局域网上的一次 mTLS 握手是宽裕的;超了就放弃 —— 结束会话通知本来就是尽力而为
    /// (`HubClient.EndSessionAsync` 自己的注释:「结束会话通知失败(不影响退出)」)。
    /// </summary>
    public TimeSpan PerStepBudget { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// 善后留痕的落点。★★ 2026-08-04 加:退出善后此前**一个字都不写**
    /// (`App.RunCleanup` 调 `RunOnceAsync(reason)` 时压根没传 log 回调,而这是唯一的调用点)。
    ///
    /// 后果在这次排查里直接吃到:用户实测「关闭卡一段时间」,修完想核实到底好了没,
    /// 却**没有任何可测的东西** —— 落盘文件的 mtime 不能用作判据,因为 `SaveStores`
    /// 同时还挂在会话中的防抖定时器上(`App.xaml.cs` 的 `_saveDebounce`),
    /// 分不出「退出时存的」和「用着用着自动存的」。
    ///
    /// ★ 这与本项目自己的纪律同源,而且是同一句话:**静默的自动流程必须留痕 ——
    ///   没有日志就是一个查不了的黑箱。** 那句话已经为自配对立成了断言,
    ///   而退出路径这条更早存在、也更常跑,却一直漏着。
    /// </summary>
    public string LogPath { get; init; } = Path.Combine(Path.GetTempPath(), "localai-shutdown.log");

    readonly ConcurrentQueue<CleanupStep> _steps = new();
    int _ran;   // Interlocked:0 = 未跑,1 = 已跑

    public bool HasRun => Volatile.Read(ref _ran) == 1;

    /// <summary>注册一个善后步骤。按注册顺序执行。</summary>
    public void Register(string name, Func<CancellationToken, Task> run) => _steps.Enqueue(new CleanupStep(name, run));

    public void Register(string name, Action run) => Register(name, _ => { run(); return Task.CompletedTask; });

    /// <summary>
    /// 执行善后,**幂等**:无论被调用多少次、从多少个入口调用,实际只跑一次。
    /// 返回本次是否真的执行了(false = 之前已经跑过)。
    /// </summary>
    public async Task<bool> RunOnceAsync(string reason, Action<string>? log = null)
    {
        if (Interlocked.Exchange(ref _ran, 1) == 1) return false;   // 已经跑过 -> 直接返回

        // ★ 留痕不依赖调用方记得传 log —— 唯一的调用点当初就忘了传,于是整条路径静默了几个月。
        //   所以写盘这件事由本类自己兜底,log 回调仍然照调(两者不互斥)。
        var trace = new System.Text.StringBuilder();
        void Say(string line)
        {
            log?.Invoke(line);
            trace.AppendLine(DateTimeOffset.Now.ToString("HH:mm:ss.fff") + "  " + line);
        }

        Say($"shutdown cleanup start (reason={reason}, budget={Budget.TotalSeconds}s, perStep={PerStepBudget.TotalSeconds}s)");
        using var cts = new CancellationTokenSource(Budget);
        var sw = Stopwatch.StartNew();

        while (_steps.TryDequeue(out var step))
        {
            if (cts.IsCancellationRequested) { Say($"  SKIP {step.Name} (budget exhausted)"); continue; }
            // ★ 单步闸:与总预算取**先到者**。没有这一道,一个慢步骤就能把后面的步骤全饿死
            //   (见 PerStepBudget 的说明)。用 linked 是为了总预算先到时照样立刻停。
            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            stepCts.CancelAfter(PerStepBudget);
            var stepSw = Stopwatch.StartNew();
            try
            {
                // ConfigureAwait(false):善后步骤里的 await 续体绝不回捕获的同步上下文。
                // 若调用方在 UI 线程上阻塞等待本方法(退出路径就是),续体回 UI 线程会与
                // 那个阻塞互相死等。调用方另外用 Task.Run 脱离 UI 上下文是主防线,这里是第二道。
                await step.Run(stepCts.Token).WaitAsync(stepCts.Token).ConfigureAwait(false);
                Say($"  ok      {step.Name} ({stepSw.ElapsedMilliseconds}ms)");
            }
            // ★ 单步超时【不】中断整个流程:被掐掉的是这一步,后面的步骤照跑 ——
            //   这正是本次修复的要点(原来网络步骤一慢,保存设置与收托盘就再也轮不到)。
            catch (OperationCanceledException) { Say($"  TIMEOUT {step.Name} ({stepSw.ElapsedMilliseconds}ms) —— 被单步上限掐断"); }
            catch (Exception ex) { Say($"  FAIL    {step.Name} ({stepSw.ElapsedMilliseconds}ms): {ex.GetType().Name}: {ex.Message}"); }
            // 单步失败不阻断后续步骤:善后是尽力而为,一步失败不该让其余清理也做不成。
        }

        Say($"shutdown cleanup done in {sw.ElapsedMilliseconds}ms");
        Flush(trace.ToString());
        return true;
    }

    /// <summary>
    /// 把这一次善后的全过程追加到 <see cref="LogPath"/>。
    /// ★ 一次性追加而不是逐行写:退出路径上每行都开一次文件句柄,本身就会让关闭更慢
    ///   —— 而"关闭慢"正是这条日志要去测量的东西,不能让测量手段污染被测对象。
    /// ★ 写失败一律吞掉:留痕是诊断手段,不能反过来把退出搞挂。
    /// </summary>
    void Flush(string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LogPath)) return;
            File.AppendAllText(LogPath, text + Environment.NewLine);
        }
        catch { /* 留痕失败不能影响退出 */ }
    }
}
