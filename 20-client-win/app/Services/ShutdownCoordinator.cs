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
            try
            {
                await step.Run(cts.Token).WaitAsync(cts.Token);
                log?.Invoke($"  ok   {step.Name}");
            }
            catch (OperationCanceledException) { log?.Invoke($"  TIMEOUT {step.Name}"); }
            catch (Exception ex) { log?.Invoke($"  FAIL {step.Name}: {ex.GetType().Name}: {ex.Message}"); }
            // 单步失败不阻断后续步骤:善后是尽力而为,一步失败不该让其余清理也做不成。
        }

        log?.Invoke($"shutdown cleanup done in {sw.ElapsedMilliseconds}ms");
        return true;
    }
}
