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

        log?.Invoke($"shutdown cleanup start (reason={reason}, budget={Budget.TotalSeconds}s)");
        using var cts = new CancellationTokenSource(Budget);
        var sw = Stopwatch.StartNew();

        while (_steps.TryDequeue(out var step))
        {
            if (cts.IsCancellationRequested) { log?.Invoke($"  SKIP {step.Name} (budget exhausted)"); continue; }
            // ★ 单步闸:与总预算取**先到者**。没有这一道,一个慢步骤就能把后面的步骤全饿死
            //   (见 PerStepBudget 的说明)。用 linked 是为了总预算先到时照样立刻停。
            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            stepCts.CancelAfter(PerStepBudget);
            try
            {
                // ConfigureAwait(false):善后步骤里的 await 续体绝不回捕获的同步上下文。
                // 若调用方在 UI 线程上阻塞等待本方法(退出路径就是),续体回 UI 线程会与
                // 那个阻塞互相死等。调用方另外用 Task.Run 脱离 UI 上下文是主防线,这里是第二道。
                await step.Run(stepCts.Token).WaitAsync(stepCts.Token).ConfigureAwait(false);
                log?.Invoke($"  ok   {step.Name}");
            }
            // ★ 单步超时【不】中断整个流程:被掐掉的是这一步,后面的步骤照跑 ——
            //   这正是本次修复的要点(原来网络步骤一慢,保存设置与收托盘就再也轮不到)。
            catch (OperationCanceledException) { log?.Invoke($"  TIMEOUT {step.Name}"); }
            catch (Exception ex) { log?.Invoke($"  FAIL {step.Name}: {ex.GetType().Name}: {ex.Message}"); }
            // 单步失败不阻断后续步骤:善后是尽力而为,一步失败不该让其余清理也做不成。
        }

        log?.Invoke($"shutdown cleanup done in {sw.ElapsedMilliseconds}ms");
        return true;
    }
}
