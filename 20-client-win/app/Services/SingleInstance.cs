// P3c -- 单实例。客户端会常驻托盘,用户很容易再点一次图标;那时应当**唤醒已有窗口**,
// 而不是开出第二个实例(两个实例会同时持有同一份配对档案/密钥,状态互相打架)。
//
// 做法:命名 Mutex 判存在 + 命名 EventWaitHandle 做跨进程唤醒。
// 名字带上会话前缀(Local\)= 每个登录会话一个实例,不跨用户互相干扰。

namespace LocalAI.Client.Services;

public sealed class SingleInstance : IDisposable
{
    const string MutexName = @"Local\LocalAI.Client.Instance";
    const string WakeName = @"Local\LocalAI.Client.Wake";

    readonly Mutex? _mutex;
    readonly EventWaitHandle? _wake;
    CancellationTokenSource? _listen;

    public bool IsFirst { get; }

    SingleInstance(Mutex? m, EventWaitHandle? w, bool first) { _mutex = m; _wake = w; IsFirst = first; }

    public static SingleInstance Acquire()
    {
        var m = new Mutex(initiallyOwned: true, MutexName, out bool created);
        var w = new EventWaitHandle(false, EventResetMode.AutoReset, WakeName);
        if (!created) { m.Dispose(); return new SingleInstance(null, w, false); }
        return new SingleInstance(m, w, true);
    }

    /// <summary>第二个实例调用:叫醒已有实例,然后自己退出。</summary>
    public void SignalExisting() => _wake?.Set();

    /// <summary>第一个实例调用:后台等待"再次启动"信号,收到就把窗口显示出来。</summary>
    public void ListenForWake(Action onWake)
    {
        if (!IsFirst || _wake is null) return;
        _listen = new CancellationTokenSource();
        var token = _listen.Token;
        var t = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try { if (_wake.WaitOne(500)) onWake(); }
                catch { break; }
            }
        })
        { IsBackground = true, Name = "localai-single-instance" };
        t.Start();
    }

    public void Dispose()
    {
        _listen?.Cancel();
        try { if (_mutex is not null) { _mutex.ReleaseMutex(); _mutex.Dispose(); } } catch { }
        _wake?.Dispose();
    }
}
