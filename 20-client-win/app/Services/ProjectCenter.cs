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
    string? FolderPath = null,               // 实际本机文件夹(建时选)
    IReadOnlyList<string>? Attachments = null, // 可选附件文件夹(可多个)
    AiPermission Ai = AiPermission.Ask,    // 给 AI 的权限(默认"需批准")
    string? OwnerMemberId = null);         // D45 所有者(空 = 未知 -> 非家庭范围一律不可见)

public sealed class ProjectCenter
{
    readonly List<Project> _items = new();

    public IReadOnlyList<Project> Items => _items;

    public event Action? Changed;

    /// <summary>
    /// 本地新增。★ 没带所有者就补成当前成员 —— 否则按 D45 的 fail-closed 规则它会【静默不可见】,
    /// 是个很难查的坑。★ 同步下来的外来条目【不要走这里】,走 Import(那条路不打戳,保持原所有者)。
    /// </summary>
    public void Add(Project p)
    {
        _items.Add(string.IsNullOrWhiteSpace(p.OwnerMemberId)
            ? p with { OwnerMemberId = MemberContext.Current } : p);
        Changed?.Invoke();
    }

    public static string NewId() => "prj-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>新建项目。默认【准备中】。folder 为实际文件夹,attachments 可为多个或空。</summary>
    public Project Create(string title, string? folder, IEnumerable<string>? attachments, ProjectScope scope, string workspaceKey = "chat")
    {
        var p = new Project(NewId(), title, "", workspaceKey, scope, DateTime.Now,
            Status: ProjectStatus.Preparing, FolderPath: folder, Attachments: attachments?.ToList(),
            OwnerMemberId: MemberContext.Current);   // D45:建的时候就定所有者
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

    /// <summary>非已完成(准备中 + 进行中),置顶在前、再按最近。workspaceKey 给定则只取该空间的。
    ///   主页项目板块用【全部】(跨空间总览);各工作空间的项目选择器用【本空间】。</summary>
    public IEnumerable<Project> Ongoing(string? workspaceKey = null)
        => _items.Where(p => p.Status != ProjectStatus.Done && (workspaceKey is null || p.WorkspaceKey == workspaceKey))
                 .Where(Visible)                                     // D45:别人的个人/仅本人项目绝不出现
                 .OrderByDescending(p => p.Pinned).ThenByDescending(p => p.LastOpened);

    /// <summary>D45 可见性:家庭范围全家可见;个人/仅本人只有所有者可见;所有者未知一律不可见。</summary>
    public static bool Visible(Project p) => MemberContext.CanSee(p.Scope, p.OwnerMemberId);

    /// <summary>把项目【发送到另一个工作空间】。它名下的会话由调用方一并迁移(SetSessionsWorkspace)。</summary>
    public void MoveToWorkspace(string projectId, string workspaceKey)
    {
        var i = _items.FindIndex(x => x.ProjectId == projectId);
        if (i >= 0 && _items[i].WorkspaceKey != workspaceKey) { _items[i] = _items[i] with { WorkspaceKey = workspaceKey }; Changed?.Invoke(); }
    }

    /// <summary>已完成 —— 项目库用它。</summary>
    public IEnumerable<Project> Completed()
        => _items.Where(p => p.Status == ProjectStatus.Done).Where(Visible).OrderByDescending(p => p.LastOpened);

    /// <summary>全部(项目抽屉里可能想全看,按状态分组)。</summary>
    public IEnumerable<Project> All()
        => _items.Where(Visible).OrderByDescending(p => p.Pinned).ThenByDescending(p => p.LastOpened);

    /// <summary>兼容旧调用:主页要的是"回到刚才那件事" —— 现等价于 Ongoing()。</summary>
    public IEnumerable<Project> Recent() => Ongoing();

    // ---------------------------------------------------------------- 存档(明文,见 ClientStore)
    public List<Project> Export() => _items.ToList();

    public void Import(List<Project>? items)
    {
        if (items is null) return;
        _items.Clear();
        // 旧存档没有 OwnerMemberId:那时只有本机本人能写这个存档,故认领为本地成员。
        // ★ 迁移只在导入时做一次;【运行期规则仍是 fail-closed】(所有者空 -> 不可见)。
        _items.AddRange(items.Select(p => string.IsNullOrWhiteSpace(p.OwnerMemberId)
            ? p with { OwnerMemberId = MemberContext.LocalMemberId } : p));
        Changed?.Invoke();
    }

    /// <summary>在系统文件管理器里打开项目文件夹。没设路径或路径不存在则返回 false(界面据此提示)。</summary>
    public static bool OpenInExplorer(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return false;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); return true; }
        catch { return false; }
    }
}
