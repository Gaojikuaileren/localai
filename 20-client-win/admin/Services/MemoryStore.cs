// V21 -- `memory.json` 的落点与**一次性迁移**。用户裁定 2026-08-08:搬干净。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ 这条里**唯一会丢数据的动作就是迁移本身**,所以三条纪律(迁移地图 §3)
//    一条都不能省。下面每一条都标了它挡的是哪种具体的坏结果:
//
//  ① **先复制、验完再删原件** —— 不许 move-then-pray。
//     ★ 验的是**内容**(条目数 + 内容哈希),**不是**「文件存在」:
//       `File.Move` 之后目标文件当然存在 —— 那个判据在任何情况下都为真,
//       包括「复制到一半断电、目标是半个 JSON」。它是一条恒真断言。
//
//  ② **幂等** —— 迁过一次之后再跑不许重复迁、不许把新数据覆盖回旧的。
//     判据(逐格写死,不许合并):
//       · 原件不在 且 目标在 ⇒ **什么都不做**(常态:已经迁过了);
//       · 原件在   且 目标不在 ⇒ 迁;
//       · **两个都在 ⇒ 以目标为准,并留证**(把原件改名成 .migrated-<时间戳>),
//         **不静默选一个**。两个都在意味着有人在迁移之后又用旧路径写过 ——
//         那件事本身要留下痕迹,而不是被一次"聪明的合并"抹掉。
//       · 两个都不在 ⇒ 空库起步(全新安装,不是失败)。
//
//  ③ **迁移失败要看得见** —— 失败时管理端**不许静默用空库启动**。
//     那会让用户以为记忆没了,而真相是文件还在原地。
//     照 D50「坏档留证」的既有形状办:留一句 <see cref="LastError"/>,由界面显示。
//
//  ★★ 为什么不做成"自动重试":迁移是**一次性**的,而重试会把一个需要人看一眼的
//    状态变成一个反复失败的后台噪音。失败一次就停下来说话。
// ══════════════════════════════════════════════════════════════════════════════

using System.Security.Cryptography;
using System.Text;
using LocalAI.Client.Services;

namespace LocalAI.Admin.Services;

/// <summary>一次迁移的结果。★ 四种,一种都不许合并成"成功/失败"两档。</summary>
public enum MemoryMigration
{
    /// <summary>两个都不在 —— 全新安装,空库起步。**不是**失败。</summary>
    FreshStart,
    /// <summary>原件不在、目标在 —— 早就迁过了,这次什么都没做(幂等)。</summary>
    AlreadyMigrated,
    /// <summary>这次真的迁了,而且**验过内容**。</summary>
    MigratedNow,
    /// <summary>两个都在 —— 以目标为准,原件已改名留证。</summary>
    BothPresentKeptTarget,
    /// <summary>★ 迁移失败。<see cref="MemoryStore.LastError"/> 里有原因,界面**必须**显示。</summary>
    Failed,
}

public static class MemoryStore
{
    /// <summary>记忆库的**新**落点:`%LOCALAPPDATA%\LocalAI\admin\memory.json`。</summary>
    public static string Path_ => System.IO.Path.Combine(AdminPaths.StateDir, "memory.json");

    /// <summary>
    /// 记忆库的**旧**落点(客户端状态目录下)。★ 只在迁移这一处出现,而且**只读、只删**——
    /// 客户端那边已经连读都不留了(纪律③:每个 json 只能有一个写者)。
    /// </summary>
    public static string LegacyPath => System.IO.Path.Combine(AppPaths.StateDir, "memory.json");

    /// <summary>上一次迁移的结论;界面据此决定要不要说话。</summary>
    public static MemoryMigration LastResult { get; private set; } = MemoryMigration.FreshStart;

    /// <summary>迁移失败的原因;null = 没失败。★ 失败时界面**必须**显示它(纪律③)。</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// 跑一次迁移。★ **幂等**:随便跑几次结果都一样(见文件头纪律②的四格表)。
    /// </summary>
    public static MemoryMigration Migrate()
    {
        LastError = null;
        try
        {
            Directory.CreateDirectory(AdminPaths.StateDir);
            var srcExists = File.Exists(LegacyPath);
            var dstExists = File.Exists(Path_);

            if (!srcExists && !dstExists) return LastResult = MemoryMigration.FreshStart;
            if (!srcExists && dstExists) return LastResult = MemoryMigration.AlreadyMigrated;

            if (srcExists && dstExists)
            {
                // ★★ 两个都在 ⇒ **以目标为准并留证**,不静默选一个。
                //   把原件改名(不是删掉)—— 万一选错了,数据还在,人找得回来。
                var keep = LegacyPath + ".migrated-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(LegacyPath, keep, overwrite: false);
                return LastResult = MemoryMigration.BothPresentKeptTarget;
            }

            // ── 只有原件在:真正要迁的那一次 ──────────────────────────
            // ★ 纪律①:**先复制**。用 File.Copy 而不是 File.Move ——
            //   Move 在跨卷时是"复制+删除"且中途断电会两头不着,而这里我们要的是
            //   「原件在验完之前一直原封不动」。
            File.Copy(LegacyPath, Path_, overwrite: false);

            // ★★ 纪律①的验:比**内容**,不比"文件存在"。
            //   两条都要:字节哈希对上(逐字节相同)+ 条目数对上(解析得出来、且不是 0 变 N)。
            //   ★ 只比哈希不够:哈希对上只说明"复制没坏",不说明"这份东西读得懂"。
            //   ★ 只比条目数也不够:两份都读不懂时条目数会同为 0,那是一条恒真判据。
            var srcHash = Sha256(LegacyPath);
            var dstHash = Sha256(Path_);
            if (srcHash != dstHash)
            {
                LastError = $"迁移校验没过:复制出来的 {Path_} 与原件内容不一致"
                          + $"(原件 {srcHash[..12]}… / 副本 {dstHash[..12]}…)。"
                          + "**原件没有删**,请把它手动复制过去后再启动管理端。";
                return LastResult = MemoryMigration.Failed;
            }
            var (okSrc, nSrc) = CountEntries(LegacyPath);
            var (okDst, nDst) = CountEntries(Path_);
            if (!okSrc || !okDst || nSrc != nDst)
            {
                LastError = $"迁移校验没过:条目数对不上(原件 {(okSrc ? nSrc.ToString() : "读不懂")}"
                          + $" / 副本 {(okDst ? nDst.ToString() : "读不懂")})。"
                          + "**原件没有删**,请先确认 " + LegacyPath + " 的内容。";
                return LastResult = MemoryMigration.Failed;
            }

            // ★ 验完了才删原件。★ 删不掉**不算失败** —— 数据已经安全到位了,
            //   剩下的只是一份多余的旧文件。但要留下 LastError 说清它还在。
            try { File.Delete(LegacyPath); }
            catch (Exception ex)
            {
                LastError = "记忆已经迁好并验过了,但旧文件删不掉(" + ex.Message + ")—— "
                          + "它现在是一份**不会再被读的**残留:" + LegacyPath;
            }
            return LastResult = MemoryMigration.MigratedNow;
        }
        catch (Exception ex)
        {
            // ★★★ 纪律③:失败**要看得见**,而且**绝不静默用空库启动**。
            LastError = "记忆库迁移失败(" + ex.GetType().Name + "):" + ex.Message
                      + "\n★ 旧文件在:" + LegacyPath
                      + "\n★ 新落点应为:" + Path_
                      + "\n在这条修好之前,管理端**不会**拿一个空记忆库冒充你的记忆。";
            return LastResult = MemoryMigration.Failed;
        }
    }

    /// <summary>
    /// 迁移之后把记忆读进内存。
    /// ★★★ 纪律③:<see cref="MemoryMigration.Failed"/> 时**返回 null**,让调用方
    /// 把界面停在「读不到」上 —— **不返回空表**。空表与"你确实没有记忆"长得一模一样,
    /// 而那正是本仓最恨的形状(失败与成功长得一样)。
    /// </summary>
    public static List<MemoryEntry>? LoadOrNull()
    {
        if (LastResult == MemoryMigration.Failed) return null;
        try
        {
            if (!File.Exists(Path_)) return new List<MemoryEntry>();
            return ClientStore.Load<List<MemoryEntry>>(Path_) ?? new List<MemoryEntry>();
        }
        catch (Exception ex)
        {
            LastError = "记忆库读不出来(" + ex.Message + ")—— 文件在:" + Path_;
            LastResult = MemoryMigration.Failed;
            return null;
        }
    }

    /// <summary>把记忆存回**管理端自己那份**。★ 原子写走 `ClientStore.Save`(与 D50 同一条纪律)。</summary>
    public static void Save(List<MemoryEntry> items) => ClientStore.Save(Path_, items);

    /// <summary>给人看的一句话;null = 不必打扰。★ 只有"要人管"的两档才说话。</summary>
    public static string? Notice => LastResult switch
    {
        MemoryMigration.Failed => "★★ 记忆库没能迁过来,现在**读不到**你的记忆:\n" + (LastError ?? "(没有记下原因)"),
        MemoryMigration.BothPresentKeptTarget =>
            "★ 新旧两处都有 `memory.json`。已**以管理端这份为准**,"
            + "旧的那份改名留在原处(不会被读,也没有删)。",
        _ => LastError,   // MigratedNow 时若旧文件删不掉,这里会带出那句
    };

    static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s));
    }

    /// <summary>数一遍条目。★ 返回 (读得懂吗, 几条) —— 读不懂与 0 条**必须分开**。</summary>
    static (bool Ok, int Count) CountEntries(string path)
    {
        try
        {
            var list = ClientStore.Load<List<MemoryEntry>>(path);
            return list is null ? (false, 0) : (true, list.Count);
        }
        catch { return (false, 0); }
    }
}
