// P4-S11 -- 一次请求往上游带多少历史(用户裁定 2026-08-05:**按 token 预算截断**)。
//
// ★★★ 这个文件里全是**估算**,而估算最容易变成谎。所以三条硬规矩写在最前面:
//
//   ① **估算就叫估算。** 客户端没有分词器 —— 装一个 tokenizer 要跟着模型走,
//      而模型是中枢那边的事。所以这里按字符估,并且**每一个对外的数都带 Estimated 标记**。
//      界面不得把它显示成"精确 token 数"。
//
//   ② **估高不估低。** 估低的后果是撞上下文窗口 —— 上游要么截断要么报错,
//      而用户看到的是"它忘了前面说的话",却不知道为什么。
//      估高的后果只是少带两条历史。**两个方向的代价不对称,所以判据必须偏保守。**
//
//   ③ **不知道窗口有多大时,取最小的那个。** 窗口来自中枢当前驻留的组件
//      (8k / 16k / 32k 三档)。拿不到中枢数据时**不猜**,按 8K 算 ——
//      同样是"估高不估低"那条的推论。
//
// ★ 截断这件事**必须看得见**:返回值里带「带了 N 条 / 共 M 条」,界面如实显示。
//   静默丢历史是这个项目最恨的形状之一 —— 用户会以为它记得,而它没有。

namespace LocalAI.Client.Services;

/// <summary>一次请求实际带了什么。★ 每个数字都能被界面如实显示出来。</summary>
public sealed record BudgetPlan(
    IReadOnlyList<ChatMessage> Included,
    int TotalCandidates,
    int EstimatedTokens,
    int BudgetTokens,
    int ContextWindow,
    bool Truncated,
    bool WindowIsGuess,        // ★ 窗口是猜的(拿不到中枢数据)—— 界面要说出来
    string Note)
{
    /// <summary>给界面的一句话。★ 带「估算」二字,不能读起来像精确值。</summary>
    public string Caption =>
        Truncated
            ? $"本轮带了 {Included.Count} / {TotalCandidates} 条历史(约 {EstimatedTokens} token · 估算,"
              + $"上限 {BudgetTokens})—— 更早的没带上"
            : $"本轮带了全部 {Included.Count} 条历史(约 {EstimatedTokens} token · 估算)";
}

public static class TokenBudget
{
    /// <summary>给回答留出的余量(token)。装不下答案的上下文等于没装。</summary>
    public const int ReplyReserve = 1024;

    /// <summary>拿不到中枢数据时按这个窗口算。★ 取**最小**的那一档 —— 估高不估低。</summary>
    public const int FallbackWindow = 8192;

    /// <summary>组件 id → 上下文窗口。★ 只认中枢下发的 id,不认客户端自造的名字(D84)。</summary>
    public static int WindowOf(string componentId)
    {
        // id 形如 llm.assistant.8b@16k —— @ 后面那段就是窗口
        var at = componentId.LastIndexOf('@');
        if (at < 0 || at + 1 >= componentId.Length) return 0;
        var tail = componentId[(at + 1)..].TrimEnd('k', 'K');
        return int.TryParse(tail, out var k) && k > 0 ? k * 1024 : 0;
    }

    // ══════════════════════════════════════════════════════════════════
    //  ★★★ V20-④(D?):驻留是**两层**的 —— 这里以前只看见一层
    //
    //  D90 把驻留拆成 `committed`(机主勾的常驻)与 `transient_resident`(按需装上的)。
    //  **服务端分了,这一侧没跟上**:本函数只吃 committed,于是用户实测 ——
    //  `assistant.8b@8k` 已经按需装载、显存 1.5→6.8 GiB,而每条回答下面还挂着
    //  「中枢的驻留组件读不到,窗口按最小的 8192 估」。★ 那不只是显示难看:
    //  窗口按 8192 估 ⇒ **真的少带历史**,回答质量跟着掉。
    //
    //  ★★ 判据不是照用户的话写,是照 **Broker 自己的语义**:
    //    `gpu_broker.py` 算显存闸时写的是
    //      `loaded = list(self._committed) + list(self._transient_resident)`
    //    收割时保留的也是这两者的并集(`keep = list(self._committed) + list(self._transient_resident)`)。
    //    ⇒ 「此刻显存里真的装着什么」= **两个集合的并**。这里问的正是这个问题。
    //
    //  ★ 那条「两个字段永不合并」的纪律(HubGpuSnapshot 第 37 行)**没有被违反**:
    //    它禁的是**在界面上把两层说成一层**(会让用户以为自己勾过它)。
    //    而"上下文窗口有多大"是一个纯事实问题,与谁勾过它无关。
    //    ⇒ 合并发生在**这个算窗口的函数里**,快照上那两个字段照旧分开。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 从中枢**此刻真的装着**的组件推出上下文窗口(常驻 ∪ 按需驻留)。
    /// ★ 多个 llm 组件同时驻留时取**最小**的那个:请求会落到哪一个由中枢的别名路由决定,
    ///   客户端猜不了 —— 按最小算才不会撞窗口。
    /// ★ 一个都推不出来 → (FallbackWindow, isGuess: true),而不是拿个大数蒙混。
    /// </summary>
    public static (int window, bool isGuess) WindowFrom(IEnumerable<string>? residentComponents)
    {
        var wins = (residentComponents ?? Array.Empty<string>())
            .Select(WindowOf).Where(w => w > 0).ToList();
        return wins.Count > 0 ? (wins.Min(), false) : (FallbackWindow, true);
    }

    /// <summary>
    /// 「此刻显存里真的装着哪些组件」= 常驻 ∪ 按需驻留。
    /// <para>★★ 这是**唯一**一处做这个并集的地方 —— 三个调用点(算窗口 / 发请求 / 显存条)
    /// 各写一遍的话,漂的那天只有一处会被发现。判据见上面那段(拄 gpu_broker 自己的算法)。</para>
    /// </summary>
    public static IReadOnlyList<string> ResidentOf(HubGpuSnapshot? snapshot)
    {
        if (snapshot is null) return Array.Empty<string>();
        var all = new List<string>(snapshot.Committed);
        foreach (var t in snapshot.TransientResident)
            if (!all.Contains(t, StringComparer.Ordinal)) all.Add(t);
        return all;
    }

    /// <summary>
    /// 估算一段文本的 token 数。★ **估算**,而且是**偏高**的估算。
    ///
    /// 判据:CJK 字符按 1 token 算(实际常在 0.6~1.5 之间),其余按 3 字符 1 token 算
    /// (英文常见口径是 4,这里取 3 = 估高)。再加每条消息的固定开销。
    /// ★ 不去装 tokenizer:那要跟着模型走,而模型在中枢那边;
    ///   一个跟模型对不上的分词器给出的"精确值"比一个诚实的估算更危险。
    /// </summary>
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int cjk = 0, other = 0;
        foreach (var ch in text)
        {
            if (ch >= 0x2E80 && ch <= 0x9FFF) cjk++;       // CJK 及部首扩展
            else if (ch >= 0xAC00 && ch <= 0xD7AF) cjk++;  // 谚文
            else other++;
        }
        return cjk + (other + 2) / 3;                      // 向上取整 = 估高
    }

    /// <summary>每条消息的协议开销(role/分隔符等)。★ 同样估高。</summary>
    public const int PerMessageOverhead = 8;

    /// <summary>
    /// 从最近往回装,装到接近预算就停。
    ///
    /// ★★ 顺序很重要:**从最近往回**。丢掉的必须是最早的那些 ——
    ///   丢中间会让对话逻辑断裂,而用户完全看不出来发生了什么。
    /// ★ 当前这条(最后一条用户消息)**必须带上**:它要是被预算挤掉,
    ///   那就不是"少带历史",是"这次请求根本没内容"。⇒ 它单独先扣。
    /// ★ System 角色的消息**不带**:那是客户端自己写的说明(「AI 未接入」之类),
    ///   不是对话内容,发上去等于让模型读我们的界面文案。
    /// </summary>
    public static BudgetPlan Plan(IReadOnlyList<ChatMessage> history, string currentText,
                                  IEnumerable<string>? committedComponents)
    {
        var (window, isGuess) = WindowFrom(committedComponents);
        var budget = Math.Max(256, window - ReplyReserve);

        // ★ 只带真实对话(User / Assistant),System 是界面文案
        var candidates = history.Where(m => m.Role is ChatRole.User or ChatRole.Assistant).ToList();

        var currentCost = Estimate(currentText) + PerMessageOverhead;
        var used = currentCost;
        var picked = new List<ChatMessage>();
        for (int i = candidates.Count - 1; i >= 0; i--)
        {
            var cost = Estimate(candidates[i].Text) + PerMessageOverhead;
            if (used + cost > budget) break;               // ★ 到此为止,更早的不带
            used += cost;
            picked.Add(candidates[i]);
        }
        picked.Reverse();

        var truncated = picked.Count < candidates.Count;
        var note = isGuess
            ? $"★ 中枢的驻留组件读不到,上下文窗口按最小的 {FallbackWindow} 估 —— "
              + "宁可少带两条,也不撞窗口"
            // ★ V20-④:措辞从「当前驻留的组件」改成「此刻装着的组件」——
            //   现在它包含按需装上的那一层,而"驻留"这个词在 D90 之后专指 committed。
            : $"上下文窗口 {window}(来自中枢此刻装着的组件)";
        return new BudgetPlan(picked, candidates.Count, used, budget, window, truncated, isGuess, note);
    }
}
