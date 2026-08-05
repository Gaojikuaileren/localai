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
    bool Shared = false,             // ★ 是否已【提升为共享】(送主机、全设备可见)。默认只在本机;单向,不可收回
    /// <summary>
    /// 这是一场【同声传译】的记录。★ 它和普通会话一起排在列表里(用户裁定),但有两条硬约束:
    ///   · 不能搬到项目或别的工作空间 —— 它的内容只有在同传界面里才讲得通(两方对话、语言方向);
    ///   · 在文字翻译界面点开它,自动切到同传界面。
    /// 位置记录末尾追加可选参数:老档案没有这个字段 -> 读成 false,照常读得动。
    /// </summary>
    bool Interpret = false,
    /// <summary>文件翻译会话(D59):与同传同款 —— 不能搬到项目/别的工作空间,点开自动切到文件翻译场景。</summary>
    bool FileTrans = false,
    /// <summary>JSON 译表会话(D60 八补):一个 JSON 文件对应一个会话,同款不可搬走。</summary>
    bool I18nTable = false,
    /// <summary>回信会话(D61):设置跟随会话;同款不可搬走,点开自动切回信场景。</summary>
    bool ReplyLetter = false);

public sealed class ChatCenter
{
    readonly List<ChatSession> _sessions = new();
    readonly List<ChatMessage> _messages = new();

    public IReadOnlyList<ChatSession> Sessions => _sessions;

    public event Action? Changed;

    public static string NewId() => "s-" + Guid.NewGuid().ToString("N")[..8];
    /// <summary>消息的稳定标识。前缀区别于会话 id,方便肉眼分辨。</summary>
    public static string NewMsgId() => "m-" + Guid.NewGuid().ToString("N")[..10];

    public ChatSession NewSession(string? projectId, string workspaceKey = "chat", ProjectScope scope = ProjectScope.Personal, string? title = null, bool interpret = false, bool fileTrans = false, bool i18nTable = false, bool replyLetter = false)
    {
        var s = new ChatSession(NewId(), title ?? (interpret ? "同传记录" : fileTrans ? "文件翻译" : i18nTable ? "JSON 译表" : replyLetter ? "回信" : "新会话"),
            interpret || fileTrans || i18nTable || replyLetter ? null : projectId, scope, DateTime.Now,
            WorkspaceKey: workspaceKey, OwnerMemberId: MemberContext.Current,   // D45:建的时候就定所有者
            Interpret: interpret, FileTrans: fileTrans, I18nTable: i18nTable, ReplyLetter: replyLetter);
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

    /// <summary>
    /// 这个项目名下的会话散在哪些工作空间里(含已随项目软删的 —— 它们恢复后还要回去)。
    /// ★ 给"摘掉项目的工作空间标签"当护栏用:那个空间里还有会话,标签就【不能摘】。
    ///   会话只按 ProjectId 归属项目(见 SessionsOf),进不了任何一个按工作空间的清单;
    ///   项目一旦在那个空间消失,那些会话就再没有任何入口 —— 数据还在,人却找不到。
    /// </summary>
    public IReadOnlyCollection<string> SessionSpacesOf(string projectId)
        => _sessions.Where(s => s.ProjectId == projectId && !s.Ghost)
                    .Select(s => s.WorkspaceKey)
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

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
    // ══════════════════════════════════════════════════════════════
    //  P4-S13(D86):共享会话走内网同步。
    //
    //  ★★ D86 裁定②:**提升为共享的那一刻就同步,此后内容更新也实时**。
    //    所以这里不只推会话本身,还要把**整段消息**一起推 ——
    //    D52 规则 A 写着「整段对话一起上去,只共享片段的话对方读不懂上下文」。
    //  ★ 只推 Shared == true 的。普通会话继续本机独立(D52「默认只在本机」)。
    // ══════════════════════════════════════════════════════════════

    /// <summary>同步器。★ 由宿主注入 —— 全进程只有一条流。</summary>
    public SyncClient? Sync { get; set; }

    /// <summary>
    /// 一个共享会话的**上线形态**。★ 只此一处 —— 增量推与全量对齐共用,
    /// 两处各写一份迟早会漂(本项目已经栽过好几次)。
    /// </summary>
    public static SyncItem ToSyncItem(ChatSession s) => new("sessions", new
    {
        id = s.SessionId, title = s.Title, shared = true,
        workspace = s.WorkspaceKey, owner = s.OwnerMemberId,
        last_active = s.LastActive.ToString("o"),
    });

    /// <summary>一条消息的上线形态。★ 同上,只此一处。</summary>
    public static SyncItem ToSyncItem(ChatMessage m) => new("messages", new
    {
        id = m.MessageId ?? (m.SessionId + "-" + m.At.Ticks),
        session_id = m.SessionId, role = m.Role.ToString(),
        text = m.Text, at = m.At.ToString("o"),
    });

    /// <summary>
    /// 当前**全部**该同步的会话与消息。★ 连上中枢时用来对齐(见 SyncClient.ReconcileAsync)。
    /// ★★ 顺序:会话在前、消息在后 —— 服务端判消息范围时要查它所属会话在不在共享表里,
    ///   顺序反了同一批里"新共享的会话 + 它的消息"会因为会话还没进表而被拒。
    ///   (服务端也按 kind 重排过一次,这里保持同序是为了**两边不依赖对方的好意**。)
    /// ★ System 消息不推:那是客户端自己的界面文案,推过去等于让另一台机器读我们的 UI 提示。
    /// </summary>
    public IEnumerable<SyncItem> SharedSnapshot()
    {
        var shared = _sessions.Where(s => s.Shared && s.DeletedAt is null && !s.Ghost).ToList();
        foreach (var s in shared) yield return ToSyncItem(s);
        var ids = shared.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
        foreach (var m in _messages.Where(m => ids.Contains(m.SessionId) && m.Role != ChatRole.System))
            yield return ToSyncItem(m);
    }

    void PushSession(ChatSession s)
    {
        if (Sync is null || !s.Shared) return;
        Sync.Enqueue(ToSyncItem(s));
    }

    void PushMessage(ChatMessage m)
    {
        if (Sync is null) return;
        var s = _sessions.FirstOrDefault(x => x.SessionId == m.SessionId);
        if (s is null || !s.Shared) return;      // ★ 只推共享会话里的消息
        Sync.Enqueue(ToSyncItem(m));
    }

    /// <summary>把一条共享会话的**全部消息**推上去。★ 提升那一刻用(D52 规则 A:整段一起上)。</summary>
    void PushWholeSession(string sessionId)
    {
        foreach (var m in _messages.Where(x => x.SessionId == sessionId)
                                   .Where(x => x.Role != ChatRole.System))
            PushMessage(m);
        // ★ System 消息不推:那是客户端自己的界面文案(「未同步」「本轮带了 N 条」之类),
        //   推过去等于让另一台机器看我们的 UI 提示。与 TokenBudget 里那条同一judgment。
    }

    public bool ShareSession(string sessionId)
    {
        var i = _sessions.FindIndex(x => x.SessionId == sessionId);
        if (i < 0 || !CanShare(_sessions[i])) return false;
        _sessions[i] = _sessions[i] with { Shared = true };
        // ★★ 立刻同步(D86 裁定②)—— 不是"下次打开时拉一下"。
        PushSession(_sessions[i]);
        PushWholeSession(sessionId);
        Changed?.Invoke();
        return true;
    }

    /// <summary>合并一条来自中枢的共享会话。返回是否真的变了。</summary>
    public bool AbsorbRemoteSession(ChatSession s)
    {
        if (!s.Shared) return false;             // ★ 中枢不该发来未共享的;真发来了也不收
        var i = _sessions.FindIndex(x => x.SessionId == s.SessionId);
        if (i < 0) { _sessions.Add(s); Changed?.Invoke(); return true; }
        if (_sessions[i].Shared && _sessions[i].Title == s.Title) return false;
        // ★ 只覆盖【共享相关】的字段,不动本机独有的(比如本机的归档状态)——
        //   整条替换会把本机的东西也冲掉,而那些字段中枢上根本没有。
        _sessions[i] = _sessions[i] with { Shared = true, Title = s.Title };
        Changed?.Invoke();
        return true;
    }

    /// <summary>合并一条来自中枢的消息。★ 同 id 已存在就不重复插。</summary>
    public bool AbsorbRemoteMessage(ChatMessage m)
    {
        var s = _sessions.FirstOrDefault(x => x.SessionId == m.SessionId);
        if (s is null || !s.Shared) return false;
        if (_messages.Any(x => x.MessageId == m.MessageId)) return false;
        _messages.Add(m);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 幽灵会话:不保留记录、不纳入记忆,不进任何列表。开一个新的前先清掉旧的幽灵。
    ///
    /// ★★ 带【场景标记】(用户裁定 2026-08-03:"全功能都要幽灵会话")——
    ///   此前它只吃 workspaceKey,建出来永远是一条普通文字会话:
    ///   在同传/文件翻译/多语表/回信里按幽灵,会被踢回文字翻译或者按钮亮着但什么也没发生。
    ///   带上标记后,各场景那四处 "Find(sid)?.XXX == true" 的判据一行都不用改就接上了。
    /// </summary>
    public ChatSession NewGhostSession(string workspaceKey, bool interpret = false, bool fileTrans = false,
                                       bool i18nTable = false, bool replyLetter = false)
    {
        PurgeGhosts();
        var title = interpret ? "幽灵同传" : fileTrans ? "幽灵文件翻译"
                  : i18nTable ? "幽灵译表" : replyLetter ? "幽灵回信" : "幽灵会话";
        var s = new ChatSession(NewId(), title, null, ProjectScope.OnlyMe, DateTime.Now,
            WorkspaceKey: workspaceKey, Ghost: true, OwnerMemberId: MemberContext.Current,
            Interpret: interpret, FileTrans: fileTrans, I18nTable: i18nTable, ReplyLetter: replyLetter);
        _sessions.Add(s);
        Changed?.Invoke();
        return s;
    }

    /// <summary>
    /// 幽灵会话被抹掉时广播一声(带上那批 id)。
    /// ★ 场景状态(回信/译表/文件翻译)把自己的文档按 sessionId 存在另一张表里,
    ///   ChatCenter 的 Ghost 过滤管不到它们 —— 不广播的话，“不留痕”就只是句口号。
    ///   销毁路径只准有一条(同 PurgeArchives 那条纪律),免得将来又漏一处。
    /// </summary>
    public event Action<IReadOnlyCollection<string>>? GhostsPurged;

    /// <summary>清除所有幽灵会话及其消息(切走/退出即抹掉,不留痕)。</summary>
    public void PurgeGhosts()
    {
        var ids = _sessions.Where(s => s.Ghost).Select(s => s.SessionId).ToHashSet();
        if (ids.Count == 0) return;
        // ★ 幽灵会话里粘贴的截图也要删(2026-07-31 审计):它落成了一个真实文件,
        //   "不留痕"就得把这个文件也收掉 —— 否则幽灵消息没了、截图还躺在 clips\ 里。
        foreach (var m in _messages.Where(m => ids.Contains(m.SessionId)))
            foreach (var a in m.Attachments ?? (IReadOnlyList<ChatAttachment>)Array.Empty<ChatAttachment>())
                if (a.Kind == AttachKind.Clipboard) StorageUsage.DeleteClipFile(a.Path);
        _sessions.RemoveAll(s => ids.Contains(s.SessionId));
        _messages.RemoveAll(m => ids.Contains(m.SessionId));
        PurgeArchives(ids);   // ★ 幽灵会话的承诺是【不留痕】,温层当然也不能留
        GhostsPurged?.Invoke(ids);   // 回信/译表/文件翻译的文档与副本跟着一起没
        Changed?.Invoke();
    }

    /// <summary>
    /// 当前所有【非幽灵】消息引用到的附件路径 —— 供「清理缓存」判断哪些 clip 文件还被消息用着,
    /// 不能删(fail-closed:拿不到就不删 clip)。
    /// </summary>
    public HashSet<string> ReferencedAttachmentPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _messages)
            foreach (var a in m.Attachments ?? (IReadOnlyList<ChatAttachment>)Array.Empty<ChatAttachment>())
                if (!string.IsNullOrEmpty(a.Path)) set.Add(a.Path);
        // ★★ 温层归档的消息也在引用(审计 2026-08-02):热层里看不见 ≠ 没人用 ——
        //   归档消息里粘贴的截图,唯一副本就是 clips\ 里那个 png;
        //   不算上它们,「清理缓存」会把还被引用的图当垃圾删掉,回头"加载更早"就是死链。
        foreach (var sid in SessionArchive.ArchivedSessionIds())
            foreach (var m in SessionArchive.Load(sid))
                foreach (var a in m.Attachments ?? (IReadOnlyList<ChatAttachment>)Array.Empty<ChatAttachment>())
                    if (!string.IsNullOrEmpty(a.Path)) set.Add(a.Path);
        return set;
    }

    /// <summary>归档目录被外部整体删除后调用:清掉计数缓存,别再显示"加载更早的 N 条"。</summary>
    public void InvalidateArchiveCounts()
    {
        _archiveCount.Clear();
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

    // ★ 这里【故意没有】"项目搬空间时把它名下会话一起搬"的方法(原 SetSessionsWorkspace,
    //   2026-08-01 删除)。会话的 WorkspaceKey 是它自己的身份:决定它进不进翻译历史、
    //   点开时按哪套界面渲染。项目换个标签就批量改写别人的身份,正好造出
    //   "聊天内容混进翻译历史"这类污染 —— 那恰恰是这套规则要防的事。
    //   要搬会话,走它自己的三点菜单「发送到工作空间」(MoveSessionToWorkspace),一条一条来。

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
        foreach (var id in ids) { _loadedArchive.Remove(id); _archiveCount.Remove(id); SessionArchive.Delete(id); }   // 温层一并清
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
        var wasShared = _sessions[i].Shared;
        _sessions[i] = _sessions[i] with { DeletedAt = DateTime.Now, Pinned = false, ProjectId = null };
        // ══════════════════════════════════════════════════════════════
        //  ★★★ 2026-08-05 用户裁定:**删除共享也要同步**。
        //
        //  ★ 这**推翻了**此前那条「删共享会话只影响这台、中枢那份不删」。
        //    原来那条的理由是:删了会让另一台上的会话凭空消失,而那台的用户没做过任何事。
        //    这个后果**依然成立**,只是用户选择接受它 —— 共享的东西两边同进同退。
        //  ★ 原文保留在此,因为它记录了当时为什么反过来 —— 哪天要改回去,理由在这儿。
        // ══════════════════════════════════════════════════════════════
        if (wasShared) Sync?.Enqueue(ToTombstone(sessionId));
        Changed?.Invoke();
    }

    /// <summary>删除一个共享会话的**上线形态**(墓碑)。</summary>
    public static SyncItem ToTombstone(string sessionId) => new("sessions", new { id = sessionId, deleted = true });

    /// <summary>收到远端的会话删除。★ 连它的消息一起清 —— 否则留下一堆孤儿消息。</summary>
    public bool AbsorbRemoteDelete(string sessionId)
    {
        var i = _sessions.FindIndex(x => x.SessionId == sessionId);
        if (i < 0) return false;
        if (_sessions[i].DeletedAt is not null) return false;      // 已经删过了,别再刷
        _sessions[i] = _sessions[i] with { DeletedAt = DateTime.Now, Pinned = false };
        Changed?.Invoke();
        return true;
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
            _archiveCount.Remove(sessionId);
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
        PurgeArchives(ids);   // ★ 温层一并清 —— 否则明文原文永久留在盘上且再也删不到
        Changed?.Invoke();
    }

    /// <summary>
    /// 把这几条会话的【温层归档】一并清掉。
    /// ★ 为什么单抽出来(审计 2026-07-31):四条销毁路径里只有两条清了温层,
    ///   手动"全部清除"与 30 天自动清除只删了内存里那两张表 ——
    ///   于是会话在界面上没了,而归档里的【明文原文】永久留在盘上,
    ///   之后再没有任何入口能定点删到它(删需要 sessionId,而记录已经没了)。
    ///   界面对这两个动作的承诺是"不可恢复"/"全部清除" —— 那就得真的清干净。
    ///   四条路径现在全走这一个,免得将来第五条又漏。
    /// </summary>
    void PurgeArchives(IEnumerable<string> ids)
    {
        foreach (var id in ids) { _loadedArchive.Remove(id); _archiveCount.Remove(id); SessionArchive.Delete(id); }
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
        // ★ 字段为 null 的档(如手写的 {})反序列化不抛 —— 这里当空表,别往下走到 NRE
        //   (那会让 LoadStores 把这份档标成导入失败、改名留证;空表才是它的本意)。
        if (snap is null) return;
        var sess = snap.Sessions ?? new List<ChatSession>();
        var msgs = snap.Messages ?? new List<ChatMessage>();
        _sessions.Clear();
        _messages.Clear();
        // 双保险:即便存档里混进了幽灵(不该发生),恢复时也丢掉
        // 旧存档没有 OwnerMemberId:那时只有本机本人能写,故认领为本地成员(运行期规则仍 fail-closed)
        _sessions.AddRange(sess.Where(s => !s.Ghost)
            .Select(s => string.IsNullOrWhiteSpace(s.OwnerMemberId)
                ? s with { OwnerMemberId = MemberContext.LocalMemberId } : s));
        var ids = _sessions.Select(s => s.SessionId).ToHashSet();
        _messages.AddRange(msgs.Where(m => ids.Contains(m.SessionId)));
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
        PurgeArchives(ids);   // ★ 温层一并清 —— 否则明文原文永久留在盘上且再也删不到
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

    // ★ 归档条数缓存(审计 2026-07-31 · 性能):UnloadedArchivedCount 此前每次都 SessionArchive.Count()
    //   整档读盘 + 反序列化,而它被【每次会话区重建】调用(回车即触发),O(N) 读盘白烧。
    //   缓存它;失效点只有三处:Append 之后写新值、LoadArchived 后置 0、Delete 时清掉。
    readonly Dictionary<string, int> _archiveCount = new();

    /// <summary>该会话还有多少条【更早的消息】没加载(界面据此显示"加载更早的 N 条")。</summary>
    public int UnloadedArchivedCount(string sessionId)
    {
        if (_loadedArchive.ContainsKey(sessionId)) return 0;
        if (_archiveCount.TryGetValue(sessionId, out var n)) return n;
        n = SessionArchive.Count(sessionId);   // 缺失才落盘统计一次
        _archiveCount[sessionId] = n;
        return n;
    }

    /// <summary>把该会话的温层消息读进来(用户点"加载更早")。</summary>
    public void LoadArchived(string sessionId)
    {
        if (_loadedArchive.ContainsKey(sessionId)) return;
        var older = SessionArchive.Load(sessionId);
        if (older.Count == 0) return;
        _loadedArchive[sessionId] = older;
        _archiveCount[sessionId] = 0;   // 已全部读进热层,没有"未加载"的了
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
            // ★★ 写成了才能从热层拿掉(审计 2026-07-31 的高危):
            //   以前是无条件 Remove —— 归档写盘失败(盘满/权限/被锁)时,
            //   原文从内存里没了、盘上也没有,一声不响地永久丢掉。
            //   写不成就留在热层,下次启动再归 —— 多占点内存远比丢掉对话好。
            if (!SessionArchive.Append(g.Key, older)) continue;
            foreach (var m in older) _messages.Remove(m);
            _archiveCount[g.Key] = SessionArchive.Count(g.Key);   // ★ 归档缓存跟着更新
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
        // ★ 诚实:这条路径只在**没配对中枢**时走(见 ChatView 发送处的两条路径)。
        //   附件只是【路径/剪贴板指令】,不真发内容(见 ChatAttachment)。
        // ★★★ 2026-08-05 审计改写:原文是「AI 模型尚未接入(P4 GPU Broker)」——
        //   S11 之后模型**已经接入**,这句话把"你这台还没配对"说成了"产品还没做出来"。
        //   两者的下一步完全不同:一个是去配对(用户三步就能做完),
        //   一个是干等下个版本。给错原因的提示比不给提示更坏 —— 它把人引向错的方向。
        var note = hasAtt
            ? "还没有配对中枢,所以这条消息没有发给模型。消息与附件引用(路径/剪贴板)已记录;"
              + "配对后由 AI 自行在本机读取,不会真的把文件发出去。"
            : "还没有配对中枢,所以这条消息没有发给模型 —— 去「设置 · 中枢」配对后,这里会给出真实回复。";
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

    // ══════════════════════════════════════════════════════════════
    //  P4-S11 · 真的把消息发给模型
    //
    //  ★★★ 三条硬规矩(都是「失败必须长得和成功不一样」的具体形态):
    //    ① **任何失败路径都不写 Assistant 消息** —— 写了就分不清"模型说的"和"客户端编的"。
    //       失败一律走 System 说明。
    //    ② 流式中途断了,**已经吐出来的半截保留**(那是模型真说的),
    //       但后面必须补一条系统说明写清"这条没说完"。
    //       丢掉 = 用户以为没发生过;不说明 = 用户以为它就说这么多。
    //    ③ **未配对 / 后端没起时,回到那条诚实占位说明** —— 不是显示"发送失败"就完了,
    //       要说清该做什么(见 ChatOutcome.Advice)。
    // ══════════════════════════════════════════════════════════════

    /// <summary>每个会话上一次说过的预算提示 —— 用来避免每轮重复同一句(见 SendAndAskAsync)。
    /// ★ 只在内存里:重启后重说一次是对的,那时用户也确实需要重新知道。</summary>
    readonly Dictionary<string, string> _lastBudgetNotice = new();

    /// <summary>正在流式接收的那条回复的 MessageId(null = 没有在进行的请求)。</summary>
    public string? StreamingMessageId { get; private set; }

    /// <summary>
    /// 发一条用户消息并向中枢请求回复(流式)。返回本轮结局。
    /// ★ 与 <see cref="Send"/> 的分工:Send 是"模型没接入"那条路径上的诚实占位,
    ///   本方法是真链路。两者都**不伪造回复**。
    /// </summary>
    public async Task<ChatOutcome> SendAndAskAsync(
        string sessionId, string text, HubClient hub,
        IEnumerable<string>? committedComponents,
        IReadOnlyList<ChatAttachment>? attachments = null,
        Action? onTick = null, CancellationToken ct = default)
    {
        text = text?.Trim() ?? "";
        var hasAtt = attachments is { Count: > 0 };
        if (text.Length == 0 && !hasAtt) return new ChatOutcome(false, "empty", "空消息");
        var i = _sessions.FindIndex(x => x.SessionId == sessionId);
        if (i < 0) return new ChatOutcome(false, "no_session", "会话不存在");

        var first = !_messages.Any(m => m.SessionId == sessionId && m.Role == ChatRole.User);
        _messages.Add(new ChatMessage(sessionId, ChatRole.User, text, DateTime.Now, attachments, NewMsgId()));
        PushMessage(_messages[^1]);        // ★ 共享会话:一条新消息就推(D86 裁定②「实时」)

        // ★ 历史取【本会话的、这条用户消息之前的】—— 刚加进去的那条由 Plan 单独计入
        var history = _messages
            .Where(m => m.SessionId == sessionId)
            .Take(Math.Max(0, _messages.Count(m => m.SessionId == sessionId) - 1))
            .ToList();
        var plan = TokenBudget.Plan(history, text, committedComponents);

        // ★ 先占一条空的 Assistant 消息作为流式落点。★ 它**不是伪造** ——
        //   它是"正在说"的容器;失败时会被删掉,绝不留下一条空回复冒充回答。
        var replyId = NewMsgId();
        _messages.Add(new ChatMessage(sessionId, ChatRole.Assistant, "", DateTime.Now, null, replyId));
        StreamingMessageId = replyId;
        _sessions[i] = _sessions[i] with
        {
            LastActive = DateTime.Now,
            Title = first && text.Length > 0 ? Trim(text) : _sessions[i].Title,
        };
        Changed?.Invoke();

        var buf = new System.Text.StringBuilder();
        ChatOutcome res;
        try
        {
            res = await ChatClient.StreamAsync(hub, history, text, committedComponents,
                delta =>
                {
                    buf.Append(delta);
                    var k = _messages.FindIndex(m => m.MessageId == replyId);
                    if (k >= 0) _messages[k] = _messages[k] with { Text = buf.ToString() };
                    onTick?.Invoke();
                    return Task.CompletedTask;
                }, ct);
        }
        finally { StreamingMessageId = null; }

        var idx = _messages.FindIndex(m => m.MessageId == replyId);
        if (res.Ok)
        {
            if (idx >= 0) { _messages[idx] = _messages[idx] with { Text = res.Text() }; PushMessage(_messages[idx]); }
        }
        else if (res.Partial.Length > 0)
        {
            // ★ 半截保留(模型真说过),后面补一条说明它没说完
            if (idx >= 0) _messages[idx] = _messages[idx] with { Text = res.Partial };
            _messages.Add(new ChatMessage(sessionId, ChatRole.System, res.Advice,
                                          DateTime.Now, null, NewMsgId()));
        }
        else
        {
            // ★★ 一个字都没有 ⇒ **把那条空回复删掉**,不留一条空 Assistant 冒充回答
            if (idx >= 0) _messages.RemoveAt(idx);
            _messages.Add(new ChatMessage(sessionId, ChatRole.System, res.Advice,
                                          DateTime.Now, null, NewMsgId()));
        }
        // ★ 截断了就说出来 —— 静默丢历史 = 用户以为它记得,而它没有。
        //
        // ★★★ 但**只在它变了的时候说**(2026-08-05 实测改):第一版每一轮都印一遍,
        //   而「窗口按最小的 8192 估」是**常态条件**、不是每轮新闻。
        //   每轮刷一遍的后果不是"更透明",是**训练人忽略它** ——
        //   跟一条会漂的断言训练人去用 --no-verify 是同一个失败模式(D85 第 5 条)。
        //   ⇒ 判据:同一会话里,只有这句话**与上次不同**时才写。
        //     ★ 方向仍然是宁可多说:第一次必说、内容一变就说,只是不重复同一句。
        var notice = (plan.Truncated || plan.WindowIsGuess) ? plan.Caption + "。" + plan.Note : "";
        if (notice.Length > 0 && (!_lastBudgetNotice.TryGetValue(sessionId, out var prev) || prev != notice))
        {
            _lastBudgetNotice[sessionId] = notice;
            _messages.Add(new ChatMessage(sessionId, ChatRole.System, notice,
                                          DateTime.Now, null, NewMsgId()));
        }
        else if (notice.Length == 0)
        {
            // ★ 条件恢复正常(不再截断、窗口也读到了)⇒ 忘掉上一句,
            //   下次再出问题时会重新说 —— 不然"好了又坏了"这一轮会被吞掉。
            _lastBudgetNotice.Remove(sessionId);
        }
        Changed?.Invoke();
        return res;
    }

    /// <summary>
    /// 播种/导入用:直接写一条消息,【不】附带"AI 未接入"的系统说明。
    /// ★ 只给示例数据和(将来的)同传转写用 —— 用户发消息仍然走 Send,
    ///   那条诚实说明不能因为多了这个入口就绕过去。
    /// </summary>
    public void SeedMessage(string sessionId, ChatRole role, string text, DateTime at)
    {
        if (!_sessions.Any(x => x.SessionId == sessionId)) return;
        _messages.Add(new ChatMessage(sessionId, role, text, at, null, NewMsgId()));
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

    /// <summary>
    /// 这条会话能不能被搬到项目 / 别的工作空间。★ 同传记录【不能】——
    /// 它的内容(两方对话、固定的语言方向)只有在同传界面里才讲得通,
    /// 搬到聊天空间就成了一堆没有上下文的碎句。与其搬完让人困惑,不如一开始就不许。
    /// </summary>
    public static bool CanMove(ChatSession s) => !s.Interpret && !s.FileTrans && !s.I18nTable && !s.ReplyLetter;   // 场景会话只在自己的场景里讲得通

    static string Trim(string t) => t.Length <= 18 ? t : t[..18] + "…";
}
