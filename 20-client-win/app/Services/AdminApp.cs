// V21 -- 出包布局:客户端旁边**装没装**管理端 / 主机端工具。
//
// ★★★ 本文件是 `HubAdmin.cs` 整块搬进管理端时**提出来留在客户端**的那 40 余行(迁移地图 §2.1)。
//   HubAdmin 的其余部分是【回环管理面的客户端】—— 单侧权威,已搬走。
//   而这里剩下的这几条**不是**管理动作,它们只回答一个问题:**旁边那个目录里有没有那个 exe**。
//
// ★★ 为什么它必须留在客户端(照字面整块搬会出两件事):
//   ① `HostSetup.DecideRole` 的第一条证据就是「装没装管理端」—— 搬走客户端**编译不过**;
//   ② 纪律②(通用客户端里不许有"主机上才显示"的分支)**唯一被允许的例外** ——
//      设置里那颗「打开管理端面板」按钮 —— 判据正是 `AdminAppPath() is not null`。
//      搬走这一条,那个例外当场失去判据,而它是**安装事实**、不是运行期角色分叉。
//
// ★ 判据一律是「**装没装**」,不是「**跑没跑**」。后者会死锁:主机第一次启动时管理端当然还没跑,
//   若判据问"跑没跑"就会答"没跑" ⇒ 判成不是主机 ⇒ 不起管理端 ⇒ 永远不跑。
//
// ★★ 两个 csproj **编译同一份**(`<Compile Link>`)—— 管理端那边 `ClientLink` 找客户端 exe
//   用的是同一套布局假设,各抄一份的那天两边会各自"对"。同 `ProcRun.cs` / `InstanceLock.cs` 的手法。

namespace LocalAI.Client.Services;

/// <summary>
/// 出包目录布局:`dist\client\` · `dist\admin\` · `dist\host\` **三个并排**。
/// ★ 这里只做路径推导与存在性判断,**一个网络调用、一个权威动作都没有**。
/// </summary>
public static class AdminApp
{
    /// <summary>管理端的出包目录名 —— `dist\admin\`,与 `dist\client\` 并排。</summary>
    public const string AdminDirName = "admin";
    /// <summary>管理端的可执行文件名。</summary>
    public const string AdminExeName = "localai-admin.exe";

    /// <summary>
    /// 本机上**装没装**管理端程序。
    ///
    /// <para>★★★ 这条改动真正的意义:**副机不起栈从此是结构性的**。
    /// 改之前,副机不起栈靠的是**判断**(它判自己不是主机);
    /// 改之后,**副机机器上根本没有那个程序** —— 不是"判断出来不该起",是"**没有东西可起**"。
    /// 这正是 D48「用够不着代替判断」推到底:
    /// 一条靠判断挡住的路,判断写错就通了;一条结构上不存在的路,写错也通不了。</para>
    ///
    /// <para>★ 但它**不削弱**否定证据:地址明确解析到别人 ⇒ 仍然立刻判不是主机
    /// (见 <see cref="HostSetup.DecideRole"/> 第一段)—— 那条挡的是「整包拷到另一台机器」,
    /// 而那台机器上管理端程序也在(跟着拷过去了)。</para>
    /// </summary>
    public static string? AdminAppPath()
    {
        // ★ 单文件发布下 Environment.ProcessPath 才是真正的 exe 路径(BaseDirectory 可能指向解包目录)
        try { return AdminAppPathNextTo(Path.GetDirectoryName(Environment.ProcessPath)); }
        catch { return null; }   // 路径拿不到就是没这条线索,不是错误
    }

    /// <summary>
    /// 上面那条的纯逻辑部分:给定客户端 exe 所在目录,看它旁边有没有 `..\admin\localai-admin.exe`。
    /// ★ 抽出来是为了能**确定性地**测:直接断言 <see cref="AdminAppPath"/> 的返回值,
    ///   等于在断言「自检此刻跑在哪个目录下」—— 那种断言在 dist\client 里会红、
    ///   在开发树里会绿,两边都不说明代码对不对。
    /// </summary>
    public static string? AdminAppPathNextTo(string? clientExeDir)
    {
        if (string.IsNullOrWhiteSpace(clientExeDir)) return null;
        var exe = Path.GetFullPath(Path.Combine(clientExeDir, "..", AdminDirName, AdminExeName));
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>
    /// 本机上有没有主机端程序目录(`..\host\localai-lan-edge.exe`)。
    ///
    /// <para>★ 这是一条**线索**,不是判据 —— 它回答的是「这台**能不能**当主机」。
    /// 「这台**是不是正在**当主机」只有回环管理面答话才算数,而那个判据现在住在管理端里
    /// (`LocalAI.Admin.Services.HubAdmin.ProbeAsync`)—— 客户端**够不着,也不该够得着**。</para>
    ///
    /// <para>★ 客户端今天只有一个消费者:<see cref="HostSetup.IdentityExistsAsync"/> ——
    /// 它是角色判定的证据之一(D36「持有 CA 私钥的那一台」),问的是**有没有**,
    /// 而**铸**身份那一半已经搬进管理端了。</para>
    /// </summary>
    public static string? HostToolsDir()
    {
        try { return HostToolsDirNextTo(Path.GetDirectoryName(Environment.ProcessPath)); }
        catch { return null; }   // 路径拿不到就是没这条线索,不是错误
    }

    /// <summary>
    /// 上面那条的纯逻辑部分。★ 抽出来的理由与 <see cref="AdminAppPathNextTo"/> 完全相同。
    /// </summary>
    public static string? HostToolsDirNextTo(string? clientExeDir)
    {
        if (string.IsNullOrWhiteSpace(clientExeDir)) return null;
        var host = Path.GetFullPath(Path.Combine(clientExeDir, "..", "host"));
        return File.Exists(Path.Combine(host, "localai-lan-edge.exe")) ? host : null;
    }
}
