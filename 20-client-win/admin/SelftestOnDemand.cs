// V29 -- 「按需」那一列默认值的护栏(实机反馈②)。
//
// ══════════════════════════════════════════════════════════════════════════════
//  用户原话:「我需要所有模型一开始都是默认按需被勾上的。」
//  裁定留痕:`decision-packets/admin-on-demand-default-grant-2026-08-09.md`(D 号待并入)。
//
//  ★★★ 为什么这一节是**纯函数**判据,而不是只靠 live 那一段:
//    live 那段读的是**真中枢此刻的状态**,而实机上中枢已经记着 9 个授权 ⇒
//    「中枢为空就全勾」那一支**永远跑不到**。跑不到的分支等于没有判据,
//    而它偏偏是这次要改的那一支。
//  ⇒ 这里直接喂两份目录、把两支各走一遍。★ 被测的那个函数
//    (`ComponentPicker.InitialPermitted`)**就是 `LoadAsync` 生产走的那一个**,
//    不是为了测试另写的一份。
//
//  ★ live 那一段照留(`SelftestLiveViews.LiveModels`):它钉的是**接线** ——
//    这个函数真的被 `LoadAsync` 用上了、面板里真的是那份集合。
//    两条各管一半:这里管"规则对不对",那里管"规则有没有接上"。
// ══════════════════════════════════════════════════════════════════════════════

using LocalAI.Admin.Views;
using LocalAI.Client.Services;

namespace LocalAI.Admin;

public static partial class Selftest
{
    static void RunOnDemand()
    {
        Console.WriteLine("\n-- 「按需」默认全勾(V29 · 实机反馈②)--");

        var comps = new[]
        {
            Comp("chat.qwen3-30b", permitted: false),
            Comp("speech.asr", permitted: false),
            Comp("vision.vlm", permitted: false),
        };

        // ── ① 中枢那份是空的 ⇒ 按 2026-08-09 那次一次性授权,全勾 ──────────
        var all = ComponentPicker.InitialPermitted(comps, out var defaultedAll);
        Assert(defaultedAll && all.Count == comps.Length && comps.All(c => all.Contains(c.Id)),
            $"★★★★ 中枢那份授权是空的 ⇒ 「按需」**全勾**(实测 {all.Count}/{comps.Length},"
            + $"defaultedAll={defaultedAll})—— 用户原话「所有模型一开始都是默认按需被勾上的」");

        // ── ② 中枢已经记着一部分 ⇒ **以中枢为准**,不再默认全勾 ──────────────
        //   ★★ 这一支是本次改动里**唯一**能挡住"授权撤不掉"的那道闸:
        //     没有它,用户取消勾选 → 点确定 → 中枢记下少的那份 → 再打开这一页
        //     又被勾回全部 → 下次确定悄悄授权回去。⇒ 这一页永远撤不掉授权。
        var partial = new[]
        {
            Comp("chat.qwen3-30b", permitted: true),
            Comp("speech.asr", permitted: false),          // ← 用户取消掉的那个
            Comp("vision.vlm", permitted: true),
        };
        var kept = ComponentPicker.InitialPermitted(partial, out var defaulted2);
        Assert(!defaulted2 && kept.Count == 2
               && kept.Contains("chat.qwen3-30b") && kept.Contains("vision.vlm")
               && !kept.Contains("speech.asr"),
            $"★★★★ 中枢已经记着 2 个 ⇒ **以中枢为准**,用户取消掉的那个**不会被勾回去**"
            + $"(实测 {kept.Count} 个,speech.asr 在里面 = {kept.Contains("speech.asr")})—— "
            + "少了这一条,这一页永远撤不掉授权,而那正是 D90 裁定①要防的事");

        // ── ③ 空目录:不许"全勾"成一个空集合还自称默认过 ────────────────────
        //   ★ 取不到清单时面板本来就什么都不列(`LoadAsync` 那一支),
        //     这里钉的是**别把"没有组件"说成"已按一次性授权勾上"**。
        var none = ComponentPicker.InitialPermitted(Array.Empty<GpuComponent>(), out var defaulted3);
        Assert(!defaulted3 && none.Count == 0,
            $"★★ 组件清单是空的 ⇒ 不算「默认全勾过」(defaultedAll={defaulted3})—— "
            + "界面上会因此**不显示**那句一次性授权说明,而那是对的:没有东西被授权");
    }

    static GpuComponent Comp(string id, bool permitted) => new(
        Id: id, Display: id, Kind: "chat", PeakGiB: 1.0, Note: "",
        Intended: false, Committed: false, Aliases: Array.Empty<string>(),
        PermittedOnDemand: permitted, TransientResident: false);
}
