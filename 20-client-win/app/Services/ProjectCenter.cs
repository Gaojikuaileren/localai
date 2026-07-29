// P3c -- 项目。设计 §4.3:跨空间列出**可恢复的工作**;主页只显示【非已完成】的,已完成的进"项目库"。
//
// 用户裁定(2026-07-30):
//   · 项目有实际【文件夹】(建时选)+ 可选【附件文件夹】;
//   · 状态:准备中 / 进行中 / 已完成(可手动改);主页只显示前两者,已完成进项目库;
//   · 项目对应聊天里的"项目会话";会话可移动到某项目;普通会话可升级为项目;
//   · 右键项目可在 Explorer 里打开其文件夹。
//
// 可见范围(§4.3 / D45):只列当前成员自己的 + 家庭的;过滤逻辑等成员会话打通后接上(先带 Scope 字段)。
// ★ 目前项目为内存态(与其它示例数据同),跨设备/落盘随中枢接入;文件夹路径是真实本机路径。

using System.Diagnostics;

namespace LocalAI.Client.Services;

public enum ProjectScope { Family, Personal, OnlyMe }

/// <summary>准备中 / 进行中 / 已完成。主页项目板块只显示前两者;已完成进"项目库"。</summary>
public enum ProjectStatus { Preparing, Active, Done }

/// <summary>
/// 项目里给 AI 的权限(用户裁定:每个项目可单独设)。★ 这是"数据 × AI × 回滚"矩阵的第一块落地:
///   ReadOnly 只读项目内容;Ask 可提议修改但要你批准(默认,最稳);Edit 可直接改项目文件夹里的文件。
/// AI 未接入前只是偏好;接入后 Edit 级别必须配操作历史与回滚(见 STATE 待决 #5),否则不放行。
/// </summary>
public enum AiPermission { ReadOnly, Ask, Edit }

public sealed record Project(
    string ProjectId,
    string Title,
    string Subtitle,
    string WorkspaceKey,
    ProjectScope Scope,
    DateTime LastOpened,
    bool Pinned = false,
    ProjectStatus Status = ProjectStatus.Active,
    string? FolderPath = null,        // 实际本机文件夹(建时选)
    string? AttachmentPath = null,    // 可选附件文件夹
    AiPermission Ai = AiPermission.Ask);   // 给 AI 的权限(默认"需批准")

public sealed class ProjectCenter
{
    readonly List<Project> _items = new();

    public IReadOnlyList<Project> Items => _items;

    public event Action? Changed;

    public void Add(Project p) { _items.Add(p); Changed?.Invoke(); }

    public static string NewId() => "prj-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>新建项目。默认【准备中】。folder 为实际文件夹,attachment 可空。</summary>
    public Project Create(string title, string? folder, string? attachment, ProjectScope scope, string workspaceKey = "chat")
    {
        var p = new Project(NewId(), title, "", workspaceKey, scope, DateTime.Now,
            Status: ProjectStatus.Preparing, FolderPath: folder, AttachmentPath: attachment);
        _items.Add(p);
        Changed?.Invoke();
        return p;
    }

    public Project? Find(string projectId) => _items.FirstOrDefault(x => x.ProjectId == projectId);

    public void Touch(string projectId)
    {
        var i = _items.FindIndex(x => x.ProjectId == projectId);
        if (i >= 0) { _items[i] = _items[i] with { LastOpened = DateTime.Now }; Changed?.Invoke(); }
    }

    public void TogglePin(string projectId)
    {
        var i = _items.FindIndex(x => x.ProjectId == projectId);
        if (i >= 0) { _items[i] = _items[i] with { Pinned = !_items[i].Pinned }; Changed?.Invoke(); }
    }

    public void SetStatus(string projectId, ProjectStatus status)
    {
        var i = _items.FindIndex(x => x.ProjectId == projectId);
        if (i >= 0 && _items[i].Status != status) { _items[i] = _items[i] with { Status = status }; Changed?.Invoke(); }
    }

    public void SetAiPermission(string projectId, AiPermission ai)
    {
        var i = _items.FindIndex(x => x.ProjectId == projectId);
        if (i >= 0 && _items[i].Ai != ai) { _items[i] = _items[i] with { Ai = ai }; Changed?.Invoke(); }
    }

    public void Update(Project p)
    {
        var i = _items.FindIndex(x => x.ProjectId == p.ProjectId);
        if (i >= 0) { _items[i] = p; Changed?.Invoke(); }
    }

    /// <summary>删除项目【记录】。★ 不动磁盘上的文件夹(那是用户的文件,绝不替他删)。会话由调用方移出。</summary>
    public void Delete(string projectId)
    {
        if (_items.RemoveAll(x => x.ProjectId == projectId) > 0) Changed?.Invoke();
    }

    /// <summary>非已完成(准备中 + 进行中),置顶在前、再按最近。主页项目板块用它。</summary>
    public IEnumerable<Project> Ongoing()
        => _items.Where(p => p.Status != ProjectStatus.Done)
                 .OrderByDescending(p => p.Pinned).ThenByDescending(p => p.LastOpened);

    /// <summary>已完成 —— 项目库用它。</summary>
    public IEnumerable<Project> Completed()
        => _items.Where(p => p.Status == ProjectStatus.Done).OrderByDescending(p => p.LastOpened);

    /// <summary>全部(项目抽屉里可能想全看,按状态分组)。</summary>
    public IEnumerable<Project> All()
        => _items.OrderByDescending(p => p.Pinned).ThenByDescending(p => p.LastOpened);

    /// <summary>兼容旧调用:主页要的是"回到刚才那件事" —— 现等价于 Ongoing()。</summary>
    public IEnumerable<Project> Recent() => Ongoing();

    /// <summary>在系统文件管理器里打开项目文件夹。没设路径或路径不存在则返回 false(界面据此提示)。</summary>
    public static bool OpenInExplorer(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return false;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); return true; }
        catch { return false; }
    }
}
