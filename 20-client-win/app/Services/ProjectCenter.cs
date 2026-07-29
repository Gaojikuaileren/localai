// P3c -- 「正在进行的项目」。设计 §4.3:跨空间列出**可恢复的工作**(一个对话线 / 一个资产项目 /
// 一份课件草稿),点开**直达那个项目**(深链,不用先进空间再找)。
//
// 与任务的分工(§4.3):项目 = 回到工作(持久);任务 = 正在跑的计算进度(短时,空闲隐)。
//
// 可见范围(§4.3 / D45):只列**当前成员自己的 + 家庭的**;绝不出现另一成员的个人/仅本人项目。
// 本轮成员层刚落地、内容后端未接,故先带 Scope 字段,过滤逻辑等成员会话打通后接上。

namespace LocalAI.Client.Services;

public enum ProjectScope { Family, Personal, OnlyMe }

/// <summary>
/// 一个可恢复的项目。WorkspaceKey 决定点开跳到哪个工作空间,ProjectId 决定打开哪个会话/文档。
/// </summary>
public sealed record Project(
    string ProjectId,
    string Title,
    string Subtitle,
    string WorkspaceKey,
    ProjectScope Scope,
    DateTime LastOpened);

public sealed class ProjectCenter
{
    readonly List<Project> _items = new();

    public IReadOnlyList<Project> Items => _items;

    public event Action? Changed;

    public void Add(Project p) { _items.Add(p); Changed?.Invoke(); }

    public void Touch(string projectId)
    {
        var i = _items.FindIndex(x => x.ProjectId == projectId);
        if (i >= 0) { _items[i] = _items[i] with { LastOpened = DateTime.Now }; Changed?.Invoke(); }
    }

    /// <summary>最近使用在前 —— 主页要的是"回到刚才那件事"。</summary>
    public IEnumerable<Project> Recent() => _items.OrderByDescending(p => p.LastOpened);
}
