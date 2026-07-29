// P3c -- 任务中心。设计 §3「底部任务架(长任务时出现,空闲自动隐)」+ 用户裁定:
//   · 底部横条只显示【简要 + 进度】;多个任务时随时间自动轮播;
//   · 点横条打开【正在进行的任务】抽屉;
//   · 抽屉是【全局】的 —— 不只主页,任何界面都能开(所以状态放这里,不放某个视图里)。
//
// 本轮是外壳:任务来源(真实长任务)要等各工作空间接入,这里先提供模型 + 变更通知,
// 并在没有任务时让横条**自动隐藏**(不做空横条占位)。

using System.Collections.ObjectModel;
using System.ComponentModel;

namespace LocalAI.Client.Services;

public sealed class RunningTask : INotifyPropertyChanged
{
    string _title = "", _detail = "";
    double _progress;          // 0..1;<0 表示不确定进度
    string _workspaceKey = ""; // 点进去要跳到哪个工作空间

    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get => _title; set { _title = value; Raise(nameof(Title)); } }
    public string Detail { get => _detail; set { _detail = value; Raise(nameof(Detail)); } }
    public double Progress { get => _progress; set { _progress = value; Raise(nameof(Progress)); Raise(nameof(PercentText)); } }
    public string WorkspaceKey { get => _workspaceKey; set { _workspaceKey = value; Raise(nameof(WorkspaceKey)); } }

    public string PercentText => _progress < 0 ? "进行中" : $"{_progress * 100:0}%";

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
}
