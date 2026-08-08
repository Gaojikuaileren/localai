// P3c -- 单实例。客户端会常驻托盘,用户很容易再点一次图标;那时应当**唤醒已有窗口**,
// 而不是开出第二个实例(两个实例会同时持有同一份配对档案/密钥,状态互相打架)。
//
// ★★ V14:机制整个搬到了 InstanceLock —— 本类现在只是**客户端那一侧的门面**
//   (把客户端的状态目录与应用键填进去,好让调用处 `SingleInstance.Acquire()` 一个字不用改)。
//
//   为什么要搬:排他与唤醒是**两个不同作用域**的东西,原来两把都写成会话级 `Local\`,
//   而排他保护的是 `%LOCALAPPDATA%` 下的 profile.json —— 那是**按用户**的。
//   同一个用户开出第二个会话(远程桌面 / 计划任务 / 会话 0)时,
//   会话级的锁会让**两个实例都以为自己是第一个**,而它们抱着同一份配对档案。
//   ⇒ 详细的判据、以及"为什么不改成 Global\"的实测,写在 InstanceLock.cs 顶部。

namespace LocalAI.Client.Services;

public sealed class SingleInstance : IDisposable
{
    /// <summary>应用键 —— 进锁文件名与唤醒事件名。管理端用的是 <c>Admin</c>。</summary>
    public const string AppKey = "Client";

    readonly InstanceLock _inner;

    SingleInstance(InstanceLock inner) => _inner = inner;

    public bool IsFirst => _inner.IsFirst;

    public static SingleInstance Acquire() => new(InstanceLock.Acquire(AppPaths.StateDir, AppKey));

    /// <summary>管理端问「客户端在不在跑」时走这条 —— 不取锁,只探。</summary>
    public static bool IsClientRunning() => InstanceLock.IsRunning(AppPaths.StateDir, AppKey);

    /// <summary>第二个实例调用:叫醒已有实例,然后自己退出。</summary>
    public void SignalExisting() => _inner.SignalExisting();

    /// <summary>第一个实例调用:后台等待"再次启动"信号,收到就把窗口显示出来。</summary>
    public void ListenForWake(Action onWake) => _inner.ListenForWake(onWake);

    public void Dispose() => _inner.Dispose();
}
