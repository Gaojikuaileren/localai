// V14 硬前置 -- 单实例的**排他**与**唤醒**是两件不同作用域的事,今天被合成了一件,而且合错了。
//
// ★★ 为什么不是把 Local\ 改成 Global\(那是最顺手、也是错的那个修法)
//
//   `Global\` 命名对象的**创建**要 `SeCreateGlobalPrivilege`。2026-08-08 实测本机:
//       SeCreateGlobalPrivilege = *S-1-5-19,*S-1-5-20,*S-1-5-32-544,*S-1-5-6
//       (LOCAL SERVICE · NETWORK SERVICE · Administrators · SERVICE)
//   —— **没有 `BUILTIN\Users`、没有 INTERACTIVE**。
//   而 D46 白纸黑字:「客户端一律普通用户运行」(见 AppPaths.cs 顶部)。
//   ⇒ 改成 `Global\` 会在**普通用户**手里直接 ACCESS_DENIED,而开发机上
//     (机主是管理员、EnableLUA=0)它**永远试不出来** —— 这正是本仓最恨的形状:
//     在你的机器上好好的,在真正要跑的那个环境里失灵,而且没人看得见。
//   ★ 实测佐证:同一台机器上,以管理员身份 `new Mutex(true, "Global\\...")` 创建**成功**。
//     成功的原因是**我是管理员**,不是这条路对。
//
// ★★ 正确的判据:**锁的作用域必须等于它保护的那个东西的作用域。**
//
//   它保护的是 `AppPaths.StateDir` 下的 `profile.json`(配对档案 + 设备密钥引用)。
//   那个目录在 `%LOCALAPPDATA%` ⇒ **按用户**,不是按会话。
//   而 `Local\` 是**按会话** —— 两者在"同一个用户开了两个会话"时分家
//   (远程桌面 / 计划任务 / 会话 0),于是**两个实例同时抱着同一份 profile.json**,
//   而且**两个都以为自己是第一个**。
//
//   ⇒ 排他用**锁文件**:`FileShare.None` 是内核级排他,天然跨会话;
//     文件放在它所保护的那个目录里 ⇒ 天然按用户。**而且一个特权都不需要。**
//
// ★ 唤醒**仍然留在 `Local\`,这不是遗漏,是它本来就对**:
//   "唤醒"= 把窗口显示出来,而窗口只能显示在**自己的会话**里(Windows 会话隔离)。
//   一个跨会话的唤醒信号没有任何意义 —— 收到了也没处显示。
//   ⇒ 两件事作用域本来就不同,合成一件才是当初那个错误。

namespace LocalAI.Client.Services;

/// <summary>
/// 单实例:**按用户**排他(锁文件)+ **按会话**唤醒(命名事件)。
/// 与路径无关 —— 客户端与管理端各自传自己的状态目录,因此两者可以编译**同一份**源码
/// (裁定④那条手法:link 源码,不新建类库)。
/// </summary>
public sealed class InstanceLock : IDisposable
{
    readonly FileStream? _lock;
    readonly EventWaitHandle? _wake;
    CancellationTokenSource? _listen;

    /// <summary>本进程是不是那个唯一实例。</summary>
    public bool IsFirst { get; }

    InstanceLock(FileStream? l, EventWaitHandle? w, bool first) { _lock = l; _wake = w; IsFirst = first; }

    /// <summary>锁文件的位置。★ 与 <paramref name="stateDir"/> 同目录 —— 作用域一致是本类的全部要点。</summary>
    public static string LockPathFor(string stateDir, string appKey)
        => Path.Combine(stateDir, appKey + ".lock");

    static string WakeNameFor(string appKey) => @"Local\LocalAI." + appKey + ".Wake";

    /// <param name="stateDir">被保护的状态目录(客户端 = <see cref="AppPaths.StateDir"/>)。</param>
    /// <param name="appKey">应用键,进锁文件名与唤醒事件名(客户端 = <c>Client</c>,管理端 = <c>Admin</c>)。</param>
    public static InstanceLock Acquire(string stateDir, string appKey)
    {
        Directory.CreateDirectory(stateDir);
        FileStream? fs;
        try
        {
            // FileShare.None = 内核排他。DeleteOnClose 让进程无论正常退出还是崩溃都不留垃圾锁文件
            // (句柄一关 OS 就删),所以**不需要**"上次是不是没清干净"那种陈旧判断。
            fs = new FileStream(LockPathFor(stateDir, appKey), FileMode.OpenOrCreate,
                                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            // ★ 只吞**共享冲突**这一种。别的异常(目录不可写、权限不对)一律往上抛 ——
            //   与改动前 `new Mutex(...)` 的失败语义一致,不新开一条"静默当成第一个"的路。
            fs = null;
        }
        return new InstanceLock(fs, new EventWaitHandle(false, EventResetMode.AutoReset, WakeNameFor(appKey)),
                                fs is not null);
    }

    /// <summary>
    /// 不取锁,只问「那个应用现在在不在跑」。★ 管理端用它回答「客户端在不在」——
    /// 这正是 lifecycle 包 §6.2 要的那个跨进程判据。
    /// </summary>
    public static bool IsRunning(string stateDir, string appKey)
    {
        var p = LockPathFor(stateDir, appKey);
        if (!File.Exists(p)) return false;
        try
        {
            using var probe = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;   // 开得独占 ⇒ 没人持有
        }
        catch (IOException) { return true; }          // 共享冲突 ⇒ 有人正持有
        catch (UnauthorizedAccessException) { return false; }
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
        try { _lock?.Dispose(); } catch { }
        _wake?.Dispose();
    }
}
