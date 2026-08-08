// V21 -- 「这条东西给谁看」的范围(D45)。★ 从 `ProjectCenter.cs` 提出来的**一个枚举**。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★ 为什么要单独一个文件:记忆库搬进管理端之后,`MemoryEntry.Scope` 与
//    `MemberContext.CanSee` 都要用它,而它原来住在 `ProjectCenter.cs` 里 ——
//    那个文件拖着 `Project` / `ProjectStatus` / `AiPermission` / `ChatCenter` 一整族
//    (`admin-app-phase2-prereqs-2026-08-08.md` §3 逐条实测过)。
//    为了一个枚举把半个客户端拖进管理端,是本仓明令不做的那种"再链一个文件"。
//
//  ★★★ 而**绝不**在管理端另写一份同名枚举:那两份的成员顺序一旦漂开,
//    `Family/Personal/OnlyMe` 的**序号**就对不上,而这两个进程读写的是同一批
//    序列化过的条目 ⇒ 一条「仅本人」会在另一个进程里被读成「家庭」。
//    ★ 那正是 D45 裁定 2 要挡的事,而且它**不会有任何东西红**:
//      两份枚举各自合法,序列化也不报错。
//  ⇒ 一份定义,两个 csproj 编它(`<Compile Link>`)。
// ══════════════════════════════════════════════════════════════════════════════

namespace LocalAI.Client.Services;

/// <summary>D45 的三档可见范围。★ 顺序**不许调**,也不许在中间插值 ——
/// 它被序列化进项目/记忆的存档里(`JsonStringEnumConverter` 存的是名字,
/// 但仍有代码按序号比较)。要加档就加在**末尾**。</summary>
public enum ProjectScope { Family, Personal, OnlyMe }
