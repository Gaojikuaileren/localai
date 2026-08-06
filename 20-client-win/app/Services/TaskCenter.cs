// P3c -- 任务中心。设计 §3「底部任务架(长任务时出现,空闲自动隐)」+ 用户裁定:
//   · 底部横条只显示【简要 + 进度】;多个任务时随时间自动轮播;
//   · 点横条打开【正在进行的任务】抽屉;
//   · 抽屉是【全局】的 —— 不只主页,任何界面都能开(所以状态放这里,不放某个视图里)。
//
// ══════════════════════════════════════════════════════════════════════
//  ★★★ 2026-08-07(V8 · D87③):本类**第一次有了真实客户**。
//
//  在这之前:`TaskCenter` 的生产写入点 = **0**(唯一的 `Tasks.Add` 在
//  `App.xaml.cs` 的 `SeedDemoTasks()` 里,而那个方法**零调用点**),
//  于是底部横条永远 `Collapsed`、任务抽屉**永远进不去**。
//  `App.xaml.cs:222` 那段注释当初就预言了这件事:
//  「真实任务源要等各工作空间接入(P4/P6/P9),在那之前底部横条永远不会出现」。
//
//  用户裁定(2026-08-06)里的「**在任务进度里面可以再开**」——
//  那个任务进度就是这条横条 + 抽屉。⇒ 按需装载/让位给了它第一个真实客户。
//
//  ★★ **示例任务仍然不许回来**。`Selftest.cs` 那条断言钉的是
//     `if (!hadStore) SeedDemoTasks()` 这个**播种调用**,不是"不许有真实任务"。
//     两件事别混:真实源来了,示例源仍然不许回来。
// ══════════════════════════════════════════════════════════════════════

using System.Collections.ObjectModel;
using System.ComponentModel;

namespace LocalAI.Client.Services;

/// <summary>
/// 任务状态。★★★ <c>Paused</c> 与 <c>Failed</c> **必须分开** —— 这是用户裁定的核心:
/// **失败是终点,暂停不是。** 把被显存压力打断的任务显示成"失败",
/// 用户会去重做一件本来只要点一下「再开」的事。
/// </summary>
public enum TaskState
{
    /// <summary>正在跑。</summary>
    Running,
    /// <summary>被**显存压力**让位打断(D87③)。可以再开 —— 前提是显存允许。</summary>
    Paused,
}

public sealed class RunningTask : INotifyPropertyChanged
{
    string _title = "", _detail = "";
    double _progress;          // 0..1;<0 表示不确定进度
    string _workspaceKey = ""; // 点进去要跳到哪个工作空间
    TaskState _state = TaskState.Running;
    string _pausedReason = "";

    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get => _title; set { _title = value; Raise(nameof(Title)); } }
    public string Detail { get => _detail; set { _detail = value; Raise(nameof(Detail)); } }
    public double Progress { get => _progress; set { _progress = value; Raise(nameof(Progress)); Raise(nameof(PercentText)); } }
    public string WorkspaceKey { get => _workspaceKey; set { _workspaceKey = value; Raise(nameof(WorkspaceKey)); } }

    /// <summary>★ 见 <see cref="TaskState"/>:暂停不是失败。</summary>
    public TaskState State
    {
        get => _state;
        set { _state = value; Raise(nameof(State)); Raise(nameof(IsPaused)); Raise(nameof(PercentText)); }
    }

    public bool IsPaused => _state == TaskState.Paused;

    /// <summary>被暂停的**具体理由**(中枢给的那一句,不是我们编的)。</summary>
    public string PausedReason
    {
        get => _pausedReason;
        set { _pausedReason = value; Raise(nameof(PausedReason)); }
    }

    /// <summary>
    /// 「再开」要重新申请的**别名**。★ 只记别名不记组件 ——
    /// §8.1「客户端只点别名不点组件」,别名 → 组件的桥在中枢。
    /// </summary>
    public string ResumeAlias { get; init; } = "";

    /// <summary>
    /// 这个任务依赖的组件(中枢在让位通知里点名的那些)。
    /// ★ **只用来算「显存够不够」这个预览**,不用来向中枢点名要什么。
    /// </summary>
    public IReadOnlyList<string> NeedsComponents { get; set; } = Array.Empty<string>();

    /// <summary>持有的租约 id —— 让位通知按它匹配"这条任务被打断了没有"。</summary>
    public string LeaseId { get; set; } = "";

    // ★ 暂停时不显示百分比:那个数字会让人以为它还在动。
    public string PercentText => _state == TaskState.Paused ? "已暂停"
                                 : _progress < 0 ? "进行中" : $"{_progress * 100:0}%";

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class TaskCenter
{
    public ObservableCollection<RunningTask> Tasks { get; } = new();

    public bool HasTasks => Tasks.Count > 0;

    /// <summary>任务集合发生变化(增删)时触发 —— 外壳据此显示/隐藏底部横条。</summary>
    public event Action? Changed;

    public TaskCenter() => Tasks.CollectionChanged += (_, _) => Changed?.Invoke();

    public RunningTask Add(string title, string detail, string workspaceKey, double progress = -1)
    {
        var t = new RunningTask { Title = title, Detail = detail, WorkspaceKey = workspaceKey, Progress = progress };
        Tasks.Add(t);
        return t;
    }

    public void Remove(string id)
    {
        var t = Tasks.FirstOrDefault(x => x.Id == id);
        if (t is not null) Tasks.Remove(t);
    }

    /// <summary>
    /// 显存压力让位(D87③)⇒ 把受影响的任务转成**暂停**。
    /// <para>
    /// ★★★ 匹配判据是**两条,任一命中即算**:
    /// ① 我持有的租约在通知的 <c>affected_leases</c> 里 —— 最准,中枢点名的;
    /// ② 我依赖的组件在被让掉的那批里 —— 兜住"租约刚过期但任务还在跑"的窗口。
    /// ★ 只用①会漏掉②那种;只用②会把**别人**机器上同组件的任务也算进来 ——
    ///   而这里是本机的任务列表,②在本机范围内是安全的。
    /// </para>
    /// <para>★ 返回被暂停的条数 —— 调用方据它决定要不要弹提示;
    /// 返回 void 的话「一条都没匹配上」与「暂停了 3 条」在外面看来一模一样。</para>
    /// </summary>
    public int PauseForPressure(IReadOnlyList<string> affectedLeaseIds,
                               IReadOnlyList<string> yieldedComponents,
                               string reason)
    {
        var n = 0;
        foreach (var t in Tasks)
        {
            if (t.State == TaskState.Paused) continue;
            var byLease = !string.IsNullOrEmpty(t.LeaseId) && affectedLeaseIds.Contains(t.LeaseId);
            var byComponent = t.NeedsComponents.Any(yieldedComponents.Contains);
            if (!byLease && !byComponent) continue;
            t.State = TaskState.Paused;
            // ★ 理由用**中枢给的那一句**,不是我们编的 —— 编的理由会指向别处。
            t.PausedReason = reason;
            n++;
        }
        if (n > 0) Changed?.Invoke();
        return n;
    }

    /// <summary>把一条任务恢复成运行态(「再开」成功之后)。</summary>
    public void Resume(string id)
    {
        var t = Tasks.FirstOrDefault(x => x.Id == id);
        if (t is null) return;
        t.State = TaskState.Running;
        t.PausedReason = "";
        Changed?.Invoke();
    }
}
