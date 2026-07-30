// P3c -- 聊天:会话与消息。设计(2026-07-30 用户裁定):
//   · 会话分【普通会话】与【项目会话】。ProjectId 为空 = 普通会话;否则归到该项目下。
//   · 选中项目后新开的会话属于该项目;没选项目直接聊 = 普通会话。
//   · 会话可【移动】到某项目;普通会话可【升级为项目】(连人带会话归到新项目)。
//   · 普通会话列在普通记录里;项目会话在项目抽屉对应项目下归类。
//
// ★ 诚实:AI 模型尚未接入(P4 GPU Broker)。发送只【记录用户消息】,并给一条【系统说明】表示
//   模型未接入 —— 绝不伪造 AI 回复。接入后在 Send 里改为真正向中枢请求即可,数据结构不变。

namespace LocalAI.Client.Services;

public enum ChatRole { User, Assistant, System }

public enum AttachKind { File, Image, Clipboard, Folder }

/// <summary>
/// 一个附件引用。★ 本机运行,不真的"发文件":只带【路径】(文件/图片)或【读剪贴板图片】的指令,
/// 让 AI 自己去本机读;界面上给用户看的是预览。Path 对剪贴板 = 落到本机临时目录的预览 png。
/// </summary>
public sealed record ChatAttachment(AttachKind Kind, string Path, string Display)
{
    public bool IsImage => Kind is AttachKind.Image or AttachKind.Clipboard;
}

public sealed record ChatMessage(string SessionId, ChatRole Role, string Text, DateTime At,
    IReadOnlyList<ChatAttachment>? Attachments = null,
    /// <summary>
    /// 稳定标识。★ 界面用它记"这条消息被展开了" —— 此前用的是【下标】,
    /// 而"加载更早"会把所有下标整体后移,于是用户展开的那条收了回去、另一条莫名展开着。
    /// 位置记录末尾追加可选参数:老档案里没有这个字段 -> 反序列化成 null,照常读得动。
    /// </summary>
    string? MessageId = null,
    /// <summary>
    /// 这条消息附带的【选项按钮】(语言码)。目前只有一处用:目标池算不出目标、
    /// 且输入=目标=母语=英语时,在对话里问"要翻成什么"。
    /// </summary>
    IReadOnlyList<string>? ChoiceOptions = null,
    /// <summary>用户选了哪个(null = 还没选)。选过之后按钮置灰,不能再改。</summary>
    string? ChoiceAnswer = null)
{
    /// <summary>
    /// 给界面用的稳定键。新消息有 MessageId 直接用;老消息(含已归档到温层的)退回内容指纹。
    /// ★ 指纹【不能只用时间戳】:Send 一次会连加"用户消息 + 系统说明"两条,各自 DateTime.Now,
    ///   而 Windows 上它的分辨率是毫秒级 —— 这两条完全可能拿到相同的 Ticks。
    /// </summary>
    public string StableKey => MessageId is { Length: > 0 } id
        ? $"{SessionId}#{id}"
        : $"{SessionId}#{At.Ticks}#{(int)Role}#{Text.Length}";
}

public sealed record ChatSession(
    string SessionId,
    string Title,
    string? ProjectId,          // null = 普通会话
    ProjectScope Scope,
    DateTime LastActive,
    bool Pinned = false,
    string WorkspaceKey = "chat",    // 会话属于哪个工作空间(不跨空间共享;可发送到别的空间)
    bool Ghost = false,              // 幽灵会话:不保留记录、不纳入记忆,不进任何列表
    DateTime? DeletedAt = null,      // 软删除:进"已删除",保留 30 天,过期自动清除
    string? OwnerMemberId = null,    // D45 所有者(空 = 未知 -> 非家庭范围一律不可见)
    bool Shared = false);            // ★ 是否已【提升为共享】(送主机、全设备可见)。默认只在本机;单向,不可收回

public sealed class ChatCenter
{
    readonly List<ChatSession> _sessions = new();
    readonly List<ChatMessage> _messages = new();

    public IReadOnlyList<ChatSession> Sessions => _sessions;

    public event Action? Changed;

    public static string NewId() => "s-" + Guid.NewGuid().ToString("N")[..8];
    /// <summary>消息的稳定标识。前缀区别于会话 id,方便肉眼分辨。</summary>
    public static string NewMsgId() => "m-" + Guid.NewGuid().ToString("N")[..10];

    public ChatSession NewSession(string? projectId, string workspaceKey = "chat", ProjectScope scope = ProjectScope.Personal, string? title = null)
    {
        var s = new ChatSession(NewId(), title ?? "新会话", projectId, scope, DateTime.Now,
            WorkspaceKey: workspaceKey, OwnerMemberId: MemberContext.Current);   // D45:建的时候就定所有者
        _sessions.Add(s);
        Changed?.Invoke();
        return s;
    }

    public ChatSession? Find(string sessionId) => _sessions.FirstOrDefault(x => x.SessionId == sessionId);

    /// <summary>某工作空间的【普通会话】(不含项目/幽灵/已删除)。</summary>
    public IEnumerable<ChatSession> NormalSessions(string workspaceKey)
        => _sessions.Where(s => s.ProjectId is null && !s.Ghost && s.DeletedAt is null && s.WorkspaceKey == workspaceKey)
                    .Where(Visible)                                   // D45:别人的私人会话绝不出现
                    .OrderByDescending(s => s.Pinned).ThenByDescending(s => s.LastActive);

    /// <summary>
    /// 翻译工作空间里【所有可见的】会话(含项目会话,不含幽灵与已删除)——翻译历史从这里取。
    /// ★ 幽灵会话不进历史:它的承诺就是不留痕。
    /// </summary>
    public IEnumerable<ChatSession> AllTranslationSessions()
        => _sessions.Where(s => s.WorkspaceKey == "translation" && !s.Ghost && s.DeletedAt is null)
                    .Where(Visible)
                    .OrderByDescending(s => s.LastActive);

    /// <summary>D45 可见性:家庭范围全家可见;个人/仅本人只有所有者可见;所有者未知一律不可见。</summary>
    public static bool Visible(ChatSession s) => MemberContext.CanSee(s.Scope, s.OwnerMemberId);

    public IEnumerable<ChatSession> SessionsOf(string projectId)
        => _sessions.Where(s => s.ProjectId == projectId && !s.Ghost && s.DeletedAt is null)
                    .Where(Visible)
                    .OrderByDescending(s => s.Pinned).ThenByDescending(s => s.LastActive);

    // ---------------------------------------------------------------- 提升为共享(单向,不可收回)
    // 用户裁定(2026-07-30):每台设备【各自独立】的会话列表;要让全家看到,必须显式"提升为共享"。
    //   ★ 提升时【整段对话一起上去】(否则对方看半截读不懂);
    //   ★ 提升【不可收回】—— 界面必须在确认框里说清楚,别让人以为能撤回。
    //   ★ 幽灵会话【永远不能共享】:它的定义就是不保留记录。

    /// <summary>能否提升为共享:不是幽灵、没删除、且还没共享过。</summary>
    public static bool CanShare(ChatSession s) => !s.Ghost && s.DeletedAt is null && !s.Shared;

    /// <summary>
    /// 提升为共享 —— 单向。成功返回 true。
    /// ★ 这里只改【标记】;真正上传到主机要等中枢接入(P4+),界面须如实说明"接入后上传"。
    /// </summary>
    public bool ShareSession(string sessionId)
    {
        var i = _sessions.FindIndex(x => x.SessionId == sessionId);
        if (i < 0 || !CanShare(_sessions[i])) return false;
        _sessions[i] = _sessions[i] with { Shared = true };
        Changed?.Invoke();
        return true;
    }

    /// <summary>幽灵会话:不保留记录、不纳入记忆,不进任何列表。开一个新的前先清掉旧的幽灵。</summary>
    public ChatSession NewGhostSession(string workspaceKey)
    {
        PurgeGhosts();
        var s = new ChatSession(NewId(), "幽灵会话", null, ProjectScope.OnlyMe, DateTime.Now,
            WorkspaceKey: workspaceKey, Ghost: true, OwnerMemberId: MemberContext.Current);
        _sessions.Add(s);
        Changed?.Invoke();
        return s;
    }

    /// <summary>清除所有幽灵会话及其消息(切走/退出即抹掉,不留痕)。</summary>
    public void PurgeGhosts()
    {
        var ids = _sessions.Where(s => s.Ghost).Select(s => s.SessionId).ToHashSet();
        if (ids.Count == 0) return;
        _sessions.RemoveAll(s => ids.Contains(s.SessionId));
        _messages.RemoveAll(m => ids.Contains(m.SessionId));
        Changed?.Invoke();
    }

    /// <summary>把会话【发送到另一个工作空间】。跨空间就离开原项目(项目不跨空间);随后可在新空间继续。</summary>
    public void MoveSessionToWorkspace(string sessionId, string workspaceKey)
    {
        var i = _sessions.FindIndex(x => x.SessionId == sessionId);
        if (i < 0 || _sessions[i].WorkspaceKey == workspaceKey) return;
        _sessions[i] = _sessions[i] with { WorkspaceKey = workspaceKey, ProjectId = null };
        Changed?.Invoke();
    }

    /// <summary>项目被发送到别的工作空间时,它名下的会话跟着走。</summary>
    public void SetSessionsWorkspace(string projectId, string workspaceKey)
    {
        var any = false;
        for (int i = 0; i < _sessions.Count; i++)
            if (_sessions[i].ProjectId == projectId && _sessions[i].WorkspaceKey != workspaceKey)
            { _sessions[i] = _sessions[i] with { WorkspaceKey = workspaceKey }; any = true; }
        if (any) Changed?.Invoke();
    }

    /// <summary>置顶 / 取消置顶(置顶排在会话列表最前)。</summary>
    public void TogglePin(string sessionId)
    {
        var i = _sessions.FindIndex(x => x.SessionId == sessionId);
        if (i >= 0) { _sessions[i] = _sessions[i] with { Pinned = !_sessions[i].Pinned }; Changed?.Invoke(); }
    }

    /// <summary>已删除会话保留天数(用户裁定:30 天后自动清除,不可恢复)。</summary>
    public const int TrashRetentionDays = 30;

    /// <summary>
    /// 项目被删除时:软删除它名下的会话,★【保留 ProjectId】—— 让这些会话【跟随项目】进"已删除项目",
    /// 而不是跑进普通会话垃圾篓(用户裁定)。项目恢复时它们一并恢复(RestoreProjectSessions)。
    /// </summary>
    public void DeleteProjectSessions(string projectId)
    {
        var any = false;
        for (int i = 0; i < _sessions.Count; i++)
            if (_sessions[i].ProjectId == projectId && _sessions[i].DeletedAt is null)
            { _sessions[i] = _sessions[i] with { DeletedAt = DateTime.Now }; any = true; }   // 保留 ProjectId
        if (any) Changed?.Invoke();
    }

    /// <summary>项目恢复时:把随它一起删的会话恢复。</summary>
    public void RestoreProjectSessions(string projectId)
    {
        var any = false;
        for (int i = 0; i < _sessions.Count; i++)
            if (_sessions[i].ProjectId == projectId && _sessions[i].DeletedAt is not null)
            { _sessions[i] = _sessions[i] with { DeletedAt = null }; any = true; }
        if (any) Changed?.Invoke();
    }

    /// <summary>项目被彻底删除时:连它的所有会话与消息一并抹掉。</summary>
    public void PurgeProjectSessions(string projectId)
    {
        var ids = _sessions.Where(s => s.ProjectId == projectId).Select(s => s.SessionId).ToHashSet();
        if (ids.Count == 0) return;
        _sessions.RemoveAll(s => ids.Contains(s.SessionId));
        _messages.RemoveAll(m => ids.Contains(m.SessionId));
        foreach (var id in ids) { _loadedArchive.Remove(id); SessionArchive.Delete(id); }   // 温层一并清
        Changed?.Invoke();
    }

    /// <summary>把 fromProjectId 名下的会话整体改挂到 toProjectId(合并同路径重复项目时用)。</summary>
    public void ReassignSessions(string fromProjectId, string toProjectId)
    {
        var any = false;
        for (int i = 0; i < _sessions.Count; i++)
            if (_sessions[i].ProjectId == fromProjectId)
            { _sessions[i] = _sessions[i] with { ProjectId = toProjectId }; any = true; }
        if (any) Changed?.Invoke();
    }

    /// <summary>
    /// 一条会话的累计体量(字符数估算)。★ 真正的约束是模型上下文窗口,但模型未接入(P4),
    /// 先用字符数顶着;接入后换成真 tokenizer 计数,这个方法是唯一的换算点。
    /// </summary>
    public int SizeOf(string sessionId)
        => _messages.Where(m => m.SessionId == sessionId).Sum(m => m.Text?.Length ?? 0);

    /// <summary>某项目的【全部】会话(含已随项目删除的)—— 供"选中已删除/已完成项目"只读浏览。</summary>
    public IEnumerable<ChatSession> AllSessionsOf(string projectId)
        => _sessions.Where(s => s.ProjectId == projectId && !s.Ghost)
                    .Where(Visible)
                    .OrderByDescending(s => s.Pinned).ThenByDescending(s => s.LastActive);

    /// <summary>删除会话 = 【软删除】进"已删除"(保留 30 天;不弹确认)。
    ///   ★ 单独删项目会话时【断开项目归属】(ProjectId=null),让它落到普通会话垃圾篓、不被孤立。
    ///   幽灵会话直接抹掉。</summary>
    public void Delete(string sessionId)
    {
        var i = _sessions.FindIndex(x => x.SessionId == sessionId);
        if (i < 0) return;
        if (_sessions[i].Ghost) { _sessions.RemoveAt(i); _messages.RemoveAll(m => m.SessionId == sessionId); Changed?.Invoke(); return; }
        _sessions[i] = _sessions[i] with { DeletedAt = DateTime.Now, Pinned = false, ProjectId = null };
        Changed?.Invoke();
    }

    /// <summary>某工作空间"已删除"的【普通】会话(最近删的在前)。★ 排除跟随项目删除的(ProjectId!=null),
    ///   那些在"已删除项目"里显示。取时顺带清掉过期的。</summary>
    public IEnumerable<ChatSession> Deleted(string workspaceKey, DateTime? asOf = null)
    {
        SweepExpiredDeleted(asOf ?? DateTime.Now);
        return _sessions.Where(s => s.DeletedAt is not null && s.ProjectId is null && s.WorkspaceKey == workspaceKey)
                        .Where(Visible)
                        .OrderByDescending(s => s.DeletedAt);
    }

    public int DeletedCount(string workspaceKey)
    {
        SweepExpiredDeleted(DateTime.Now);
        return _sessions.Count(s => s.DeletedAt is not null && s.ProjectId is null && s.WorkspaceKey == workspaceKey && Visible(s));
    }

    /// <summary>从"已删除"恢复(回到普通/项目会话)。</summary>
    public void Restore(string sessionId)
    {
        var i = _sessions.FindIndex(x => x.SessionId == sessionId);
        if (i >= 0 && _sessions[i].DeletedAt is not null) { _sessions[i] = _sessions[i] with { DeletedAt = null }; Changed?.Invoke(); }
    }

    /// <summary>彻底删除一条已删除会话(不可恢复)。</summary>
    public void PurgeDeleted(string sessionId)
    {
        var removed = _sessions.RemoveAll(x => x.SessionId == sessionId && x.DeletedAt is not null) > 0;
        if (removed)
        {
            _messages.RemoveAll(m => m.SessionId == sessionId);
            _loadedArchive.Remove(sessionId);
            SessionArchive.Delete(sessionId);   // 连温层一起清,别留孤儿归档
            Changed?.Invoke();
        }
    }

    /// <summary>清空某工作空间的"已删除"【普通】会话(手动清除,不可恢复)。项目垃圾篓里的不受影响。</summary>
    public void ClearDeleted(string workspaceKey)
    {
        var ids = _sessions.Where(s => s.DeletedAt is not null && s.ProjectId is null && s.WorkspaceKey == workspaceKey).Select(s => s.SessionId).ToHashSet();
        if (ids.Count == 0) return;
        _sessions.RemoveAll(s => ids.Contains(s.SessionId));
        _messages.RemoveAll(m => ids.Contains(m.SessionId));
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- 存档(明文,见 ClientStore)
    /// <summary>存档结构。★ 幽灵会话【不进存档】—— 见 Export。</summary>
    public sealed record Snapshot(List<ChatSession> Sessions, List<ChatMessage> Messages);

    /// <summary>
    /// 导出可落盘的内容。★ 幽灵会话及其消息【一律排除】:它的定义就是"不保留记录",
    /// 落盘等于毁约(selftest 钉死)。已删除会话【保留】,连同 DeletedAt,重启后继续走 30 天窗口。
    /// </summary>
    public Snapshot Export()
    {
        var keep = _sessions.Where(s => !s.Ghost).ToList();
        var ids = keep.Select(s => s.SessionId).ToHashSet();
        return new Snapshot(keep, _messages.Where(m => ids.Contains(m.SessionId)).ToList());
    }

    /// <summary>从存档恢复(启动时)。顺带扫掉已过保留期的已删除会话。</summary>
    public void Import(Snapshot? snap, DateTime? asOf = null)
    {
        if (snap is null) return;
        _sessions.Clear();
        _messages.Clear();
        // 双保险:即便存档里混进了幽灵(不该发生),恢复时也丢掉
        // 旧存档没有 OwnerMemberId:那时只有本机本人能写,故认领为本地成员(运行期规则仍 fail-closed)
        _sessions.AddRange(snap.Sessions.Where(s => !s.Ghost)
            .Select(s => string.IsNullOrWhiteSpace(s.OwnerMemberId)
                ? s with { OwnerMemberId = MemberContext.LocalMemberId } : s));
        var ids = _sessions.Select(s => s.SessionId).ToHashSet();
        _messages.AddRange(snap.Messages.Where(m => ids.Contains(m.SessionId)));
        SweepExpiredDeleted(asOf ?? DateTime.Now);
        Changed?.Invoke();
    }

    /// <summary>清掉超过保留期的已删除会话(30 天)。asOf 供测试注入。</summary>
    public void SweepExpiredDeleted(DateTime asOf)
    {
        var cutoff = asOf.AddDays(-TrashRetentionDays);
        var ids = _sessions.Where(s => s.DeletedAt is { } d && d < cutoff).Select(s => s.SessionId).ToHashSet();
        if (ids.Count == 0) return;
        _sessions.RemoveAll(s => ids.Contains(s.SessionId));
        _messages.RemoveAll(m => ids.Contains(m.SessionId));
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- 分层存储(热层 / 温层)
    // ★ 温层(已归档)的消息【不放进 _messages】,而是单独放这里 —— 否则 Export() 会把它们
    //   又写回 chat.json,和归档文件重复一份。这样热层永远是"该落盘的那份"。
    readonly Dictionary<string, List<ChatMessage>> _loadedArchive = new();

    /// <summary>热层保留的消息条数:超出的部分归档到温层(仍是原文,只是换文件放)。</summary>
    public const int HotMessages = 200;

    public IEnumerable<ChatMessage> MessagesOf(string sessionId)
    {
        var hot = _messages.Where(m => m.SessionId == sessionId);
        return _loadedArchive.TryGetValue(sessionId, out var warm)
            ? warm.Concat(hot).OrderBy(m => m.At)   // 已加载的更早消息排在前面
            : hot.OrderBy(m => m.At);
    }

    /// <summary>该会话还有多少条【更早的消息】没加载(界面据此显示"加载更早的 N 条")。</summary>
    public int UnloadedArchivedCount(string sessionId)
        => _loadedArchive.ContainsKey(sessionId) ? 0 : SessionArchive.Count(sessionId);

    /// <summary>把该会话的温层消息读进来(用户点"加载更早")。</summary>
    public void LoadArchived(string sessionId)
    {
        if (_loadedArchive.ContainsKey(sessionId)) return;
        var older = SessionArchive.Load(sessionId);
        if (older.Count == 0) return;
        _loadedArchive[sessionId] = older;
        Changed?.Invoke();
    }

    /// <summary>
    /// 把超出热层的旧消息移到温层。返回归档条数。
    /// ★ 幽灵会话不参与(它不落盘);已加载温层的会话本轮跳过(避免把刚读回来的又写回去)。
    /// </summary>
    public int ArchiveOldMessages(int keepRecent = HotMessages)
    {
        if (keepRecent <= 0) return 0;
        var ghosts = _sessions.Where(s => s.Ghost).Select(s => s.SessionId).ToHashSet();
        var moved = 0;
        foreach (var g in _messages.GroupBy(m => m.SessionId).ToList())
        {
            if (ghosts.Contains(g.Key) || _loadedArchive.ContainsKey(g.Key)) continue;
            var ordered = g.OrderBy(m => m.At).ToList();
            if (ordered.Count <= keepRecent) continue;
            var older = ordered.Take(ordered.Count - keepRecent).ToList();
            SessionArchive.Append(g.Key, older);
            foreach (var m in older) _messages.Remove(m);
            moved += older.Count;
        }
        if (moved > 0) Changed?.Invoke();
        return moved;
    }

    /// <summary>把会话移动到某项目(projectId=null 则变回普通会话)。</summary>
    public void MoveToProject(string sessionId, string? projectId)
    {
        var i = _sessions.FindIndex(x => x.SessionId == sessionId);
        if (i < 0 || _sessions[i].ProjectId == projectId) return;
        _sessions[i] = _sessions[i] with { ProjectId = projectId };
        Changed?.Invoke();
    }

    public void Rename(string sessionId, string title)
    {
        var i = _sessions.FindIndex(x => x.SessionId == sessionId);
        if (i >= 0) { _sessions[i] = _sessions[i] with { Title = title }; Changed?.Invoke(); }
    }

    /// <summary>
    /// 发送一条用户消息。★ 模型未接入:只记录用户消息 + 一条系统说明,不伪造 AI 回复。
    /// 首条消息把会话标题设为消息开头(方便在列表里认出来)。返回是否记下了(空消息不记)。
    /// </summary>
    public bool Send(string sessionId, string text, IReadOnlyList<ChatAttachment>? attachments = null)
    {
        text = text?.Trim() ?? "";
        var hasAtt = attachments is { Count: > 0 };
        if (text.Length == 0 && !hasAtt) return false;   // 空消息且无附件 -> 不记
        var i = _sessions.FindIndex(x => x.SessionId == sessionId);
        if (i < 0) return false;

        var first = !_messages.Any(m => m.SessionId == sessionId && m.Role == ChatRole.User);
        _messages.Add(new ChatMessage(sessionId, ChatRole.User, text, DateTime.Now, attachments, NewMsgId()));
        // ★ 诚实:AI 未接入。附件只是【路径/剪贴板指令】,不真发内容(见 ChatAttachment)。
        var note = hasAtt
            ? "AI 模型尚未接入(P4)。消息与附件引用(路径/剪贴板)已记录;接入后由 AI 自行在本机读取,不会真的把文件发出去。"
            : "AI 模型尚未接入(P4 GPU Broker)。你的消息已记录;接入后这里会给出真实回复。";
        _messages.Add(new ChatMessage(sessionId, ChatRole.System, note, DateTime.Now, null, NewMsgId()));

        // ★ 标题:接入模型后由 AI 依会话内容起;未接入前用首条消息(截断)作占位。
        var titleSeed = text.Length > 0 ? text : (hasAtt ? attachments![0].Display : "");
        _sessions[i] = _sessions[i] with
        {
            LastActive = DateTime.Now,
            Title = first && titleSeed.Length > 0 ? Trim(titleSeed) : _sessions[i].Title,
        };
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 往会话里写一条【带选项按钮】的系统提问。返回这条消息的稳定标识,便于之后作答。
    /// ★ 这不是"伪造 AI 回复":它是客户端自己要问的事(翻成哪种语言),
    ///   与 AI 未接入无关,所以照常写进会话。
    /// </summary>
    public string? AskChoice(string sessionId, string question, IReadOnlyList<string> options)
    {
        if (!_sessions.Any(x => x.SessionId == sessionId)) return null;
        var id = NewMsgId();
        _messages.Add(new ChatMessage(sessionId, ChatRole.System, question, DateTime.Now,
                                      null, id, options.ToList(), null));
        Changed?.Invoke();
        return id;
    }

    /// <summary>
    /// 回答一条提问。★ 只认【还没答过】的那条 —— 答过就定死,按钮置灰不能再改
    /// (用户裁定:点过之后 disable 掉)。返回是否真的记上了。
    /// </summary>
    public bool AnswerChoice(string messageId, string answer)
    {
        var i = _messages.FindIndex(m => m.MessageId == messageId);
        if (i < 0) return false;
        if (_messages[i].ChoiceOptions is not { Count: > 0 }) return false;
        if (_messages[i].ChoiceAnswer is not null) return false;      // 已经答过,不覆盖
        _messages[i] = _messages[i] with { ChoiceAnswer = answer };
        Changed?.Invoke();
        return true;
    }

    /// <summary>该会话里【还没被回答】的最后一条提问(用户直接回一句语言名时要找的就是它)。</summary>
    public ChatMessage? PendingChoice(string sessionId)
        => _messages.LastOrDefault(m => m.SessionId == sessionId
                                        && m.ChoiceOptions is { Count: > 0 }
                                        && m.ChoiceAnswer is null);

    static string Trim(string t) => t.Length <= 18 ? t : t[..18] + "…";
}
