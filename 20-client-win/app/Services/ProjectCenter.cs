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
    string? OwnerMemberId = null,          // D45 所有者(空 = 未知 -> 非家庭范围一律不可见)
    DateTime? DeletedAt = null,            // 软删除:进【已删除项目】共享垃圾篓,保留 30 天
    DateTime? CompletedAt = null,          // 标记完成的时刻(用于"完成后停留 3 秒再划走"的宽限)
    string? HostMachine = null,            // 项目文件夹在哪台机器上(null/空 = 本机);跨 PC 项目用
    bool Shared = false);                  // ★ 是否已【提升为共享】。默认只在本机;单向,不可收回

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
    public Project Create(string title, string? folder, IEnumerable<string>? attachments, ProjectScope scope,
                          string workspaceKey = "chat", string? hostMachine = null)
    {
        // ★ 同一台机器的【同一个文件夹】只能有一个项目:已经有就直接返回它,不再重复建(用户裁定)。
        if (FindByFolder(folder, hostMachine) is { } existing) return existing;
        var p = new Project(NewId(), title, "", workspaceKey, scope, DateTime.Now,
            Status: ProjectStatus.Preparing, FolderPath: folder, Attachments: attachments?.ToList(),
            OwnerMemberId: MemberContext.Current,   // D45:建的时候就定所有者
            HostMachine: hostMachine);
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

    /// <summary>改状态。标记为【已完成】时打上时间戳 —— 用于"在项目抽屉里停留 3 秒再划走"的宽限。</summary>
    public void SetStatus(string projectId, ProjectStatus status)
    {
        var i = _items.FindIndex(x => x.ProjectId == projectId);
        if (i < 0 || _items[i].Status == status) return;
        _items[i] = _items[i] with
        {
            Status = status,
            CompletedAt = status == ProjectStatus.Done ? DateTime.Now : null,
        };
        Changed?.Invoke();
    }

    /// <summary>标记完成后先在"进行中"停留几秒再划走(与待办一致:3 秒)。</summary>
    public const double CompletionGraceSeconds = 3;

    static bool InCompletionGrace(Project p, DateTime now)
        => p.Status == ProjectStatus.Done && p.CompletedAt is { } c && (now - c).TotalSeconds < CompletionGraceSeconds;

    /// <summary>是否还有处于"完成后 3 秒宽限"的项目(界面据此决定要不要继续轮询刷新)。</summary>
    public bool HasCompletionGrace(string? workspaceKey = null, DateTime? asOf = null)
    {
        var now = asOf ?? DateTime.Now;
        return _items.Any(p => p.DeletedAt is null && (workspaceKey is null || p.WorkspaceKey == workspaceKey) && InCompletionGrace(p, now));
    }

    /// <summary>某项目是否正处在"刚完成、还没划走"的宽限期(界面据此播放划出动画)。</summary>
    public static bool IsCompletingNow(Project p, DateTime? asOf = null) => InCompletionGrace(p, asOf ?? DateTime.Now);

    public void SetAiPermission(string projectId, AiPermission ai)
    {
        var i = _items.FindIndex(x => x.ProjectId == projectId);
        if (i >= 0 && _items[i].Ai != ai) { _items[i] = _items[i] with { Ai = ai }; Changed?.Invoke(); }
    }

    /// <summary>改可见范围:家庭 = 同网其它 PC 共享可见可操作;个人/仅本人 = 只在本机显示(D45)。</summary>
    public void SetScope(string projectId, ProjectScope scope)
    {
        var i = _items.FindIndex(x => x.ProjectId == projectId);
        if (i >= 0 && _items[i].Scope != scope) { _items[i] = _items[i] with { Scope = scope }; Changed?.Invoke(); }
    }

    public void Update(Project p)
    {
        var i = _items.FindIndex(x => x.ProjectId == p.ProjectId);
        if (i >= 0) { _items[i] = p; Changed?.Invoke(); }
    }

    // ---------------------------------------------------------------- 提升为共享(单向,不可收回)
    /// <summary>能否提升:没删除、且还没共享过。</summary>
    public static bool CanShare(Project p) => p.DeletedAt is null && !p.Shared;

    /// <summary>
    /// 提升项目为共享 —— 单向。★ 只共享【元数据】;文件夹内容仍在 HostMachine 那台机器上,
    /// 别的机器要读写它得等中枢的文件通道(P4+),界面须如实标注"文件夹在 XX · 当前不可访问"。
    /// </summary>
    public bool ShareProject(string projectId)
    {
        var i = _items.FindIndex(x => x.ProjectId == projectId);
        if (i < 0 || !CanShare(_items[i])) return false;
        _items[i] = _items[i] with { Shared = true };
        Changed?.Invoke();
        return true;
    }

    // ---------------------------------------------------------------- 文件夹唯一性(一个路径只能有一个项目)
    /// <summary>本机的机器标识(HostMachine 为空即表示这台机器)。</summary>
    public const string LocalMachine = "";

    /// <summary>
    /// 路径归一化:去掉首尾空白与结尾的斜杠,取完整路径,Windows 下不区分大小写。
    /// ★ 只做【完全相同】的判定 —— 子路径不算重复(用户裁定:子目录可以另立项目)。
    /// </summary>
    public static string NormalizeFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var s = path.Trim();
        try { s = Path.GetFullPath(s); } catch { /* 不合法路径就按原样比 */ }
        s = s.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return s.ToLowerInvariant();
    }

    static bool SameFolder(Project p, string normFolder, string? machine)
        => NormalizeFolder(p.FolderPath) == normFolder
           && string.Equals(p.HostMachine ?? "", machine ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 找出【同一台机器上、同一个文件夹】的既有项目 —— ★ 含已完成与已删除的
    /// (否则用户会在回收站里躺着一个同路径项目的情况下又建一个)。excludeId 用于编辑自身时跳过。
    /// </summary>
    public Project? FindByFolder(string? folder, string? machine = null, string? excludeId = null)
    {
        var norm = NormalizeFolder(folder);
        if (norm.Length == 0) return null;
        return _items.FirstOrDefault(p => p.ProjectId != excludeId && SameFolder(p, norm, machine));
    }

    /// <summary>
    /// 合并【完全同路径 + 同机器】的重复项目:保留最早建立的那个(会话最全的历史),
    /// 把其余的会话并过去再移除。返回被合并掉的条数。★ 子路径不合并。
    /// 用于导入旧存档(那时还没有唯一性约束)后的一次性收敛。
    /// </summary>
    public int MergeDuplicateFolders(Action<string, string>? moveSessions = null)
    {
        var merged = 0;
        // 有路径的才参与;按 (机器, 归一化路径) 分组
        var groups = _items.Where(p => !string.IsNullOrWhiteSpace(p.FolderPath))
                           .GroupBy(p => ((p.HostMachine ?? "").ToLowerInvariant(), NormalizeFolder(p.FolderPath)))
                           .Where(g => g.Count() > 1);
        foreach (var g in groups.ToList())
        {
            // 保留:优先未删除的、再按最近打开(最活跃的那个当主)
            var keep = g.OrderBy(p => p.DeletedAt is null ? 0 : 1).ThenByDescending(p => p.LastOpened).First();
            foreach (var dup in g.Where(p => p.ProjectId != keep.ProjectId))
            {
                moveSessions?.Invoke(dup.ProjectId, keep.ProjectId);   // 会话并到保留的那个项目下
                _items.RemoveAll(x => x.ProjectId == dup.ProjectId);
                merged++;
            }
        }
        if (merged > 0) Changed?.Invoke();
        return merged;
    }

    /// <summary>已删除项目保留天数(与会话垃圾篓一致:30 天后自动清除,不可恢复)。</summary>
    public const int TrashRetentionDays = 30;

    /// <summary>软删除项目 —— 进【已删除项目】共享垃圾篓(保留 30 天)。★ 不动磁盘文件夹。
    ///   它名下的会话由调用方随项目一起软删除(ChatCenter.DeleteProjectSessions,会话跟随项目)。</summary>
    public void Delete(string projectId)
    {
        var i = _items.FindIndex(x => x.ProjectId == projectId);
        if (i >= 0 && _items[i].DeletedAt is null) { _items[i] = _items[i] with { DeletedAt = DateTime.Now, Pinned = false }; Changed?.Invoke(); }
    }

    /// <summary>从【已删除项目】恢复(回到它原来的状态与工作空间)。会话由调用方一并恢复。</summary>
    public void RestoreProject(string projectId)
    {
        var i = _items.FindIndex(x => x.ProjectId == projectId);
        if (i >= 0 && _items[i].DeletedAt is not null) { _items[i] = _items[i] with { DeletedAt = null }; Changed?.Invoke(); }
    }

    /// <summary>彻底删除项目【记录】(不可恢复)。★ 仍然不动磁盘文件夹。会话由调用方彻底删除。</summary>
    public void PurgeProject(string projectId)
    {
        if (_items.RemoveAll(x => x.ProjectId == projectId) > 0) Changed?.Invoke();
    }

    /// <summary>清掉超过保留期的已删除项目(30 天)。asOf 供测试注入。</summary>
    public void SweepExpiredDeletedProjects(DateTime asOf)
    {
        var cut = asOf.AddDays(-TrashRetentionDays);
        if (_items.RemoveAll(x => x.DeletedAt is { } d && d < cut) > 0) Changed?.Invoke();
    }

    /// <summary>【已删除项目】—— ★ 跨工作空间【共享】一个垃圾篓(用户裁定)。取时顺带清过期的。</summary>
    public IEnumerable<Project> DeletedProjects(DateTime? asOf = null)
    {
        SweepExpiredDeletedProjects(asOf ?? DateTime.Now);
        return _items.Where(p => p.DeletedAt is not null).Where(Visible).OrderByDescending(p => p.DeletedAt);
    }

    public int DeletedProjectsCount()
    {
        SweepExpiredDeletedProjects(DateTime.Now);
        return _items.Count(p => p.DeletedAt is not null && Visible(p));
    }

    /// <summary>
    /// 开启【项目分支】:把项目复制成一个新的【准备中】项目(新 Id、同文件夹/附件/权限/范围),
    /// 放在同一个工作空间。用于"已完成项目 → 开启此项目分支"。返回新项目。
    /// </summary>
    public Project Branch(string projectId)
    {
        var src = Find(projectId) ?? throw new InvalidOperationException("项目不存在");
        var copy = src with
        {
            ProjectId = NewId(),
            Title = src.Title + "(分支)",
            Status = ProjectStatus.Preparing,
            DeletedAt = null,
            Pinned = false,
            LastOpened = DateTime.Now,
            OwnerMemberId = MemberContext.Current,
        };
        _items.Add(copy);
        Changed?.Invoke();
        return copy;
    }

    /// <summary>非已完成(准备中 + 进行中)且未删除,置顶在前、再按最近。workspaceKey 给定则只取该空间的。
    ///   主页项目板块用【全部】(跨空间总览);各工作空间的项目选择器用【本空间】。</summary>
    public IEnumerable<Project> Ongoing(string? workspaceKey = null, DateTime? asOf = null)
    {
        var now = asOf ?? DateTime.Now;
        // ★ 刚标记完成的仍留在列表里(3 秒宽限),让界面能播"划走"的动画再消失(与待办一致)。
        return _items.Where(p => p.DeletedAt is null
                                 && (p.Status != ProjectStatus.Done || InCompletionGrace(p, now))
                                 && (workspaceKey is null || p.WorkspaceKey == workspaceKey))
                     .Where(Visible)                                 // D45:别人的个人/仅本人项目绝不出现
                     .OrderByDescending(p => p.Pinned).ThenByDescending(p => p.LastOpened);
    }

    /// <summary>D45 可见性:家庭范围全家可见;个人/仅本人只有所有者可见;所有者未知一律不可见。</summary>
    public static bool Visible(Project p) => MemberContext.CanSee(p.Scope, p.OwnerMemberId);

    /// <summary>把项目【发送到另一个工作空间】。它名下的会话由调用方一并迁移(SetSessionsWorkspace)。</summary>
    public void MoveToWorkspace(string projectId, string workspaceKey)
    {
        var i = _items.FindIndex(x => x.ProjectId == projectId);
        if (i >= 0 && _items[i].WorkspaceKey != workspaceKey) { _items[i] = _items[i] with { WorkspaceKey = workspaceKey }; Changed?.Invoke(); }
    }

    /// <summary>已完成(未删除)。★ 已完成项目【不共享】,按工作空间隔离(用户裁定);不传则全部。</summary>
    public IEnumerable<Project> Completed(string? workspaceKey = null)
        => _items.Where(p => p.Status == ProjectStatus.Done && p.DeletedAt is null && (workspaceKey is null || p.WorkspaceKey == workspaceKey))
                 .Where(Visible).OrderByDescending(p => p.LastOpened);

    /// <summary>全部未删除(项目抽屉里可能想全看,按状态分组)。</summary>
    public IEnumerable<Project> All()
        => _items.Where(p => p.DeletedAt is null).Where(Visible).OrderByDescending(p => p.Pinned).ThenByDescending(p => p.LastOpened);

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
