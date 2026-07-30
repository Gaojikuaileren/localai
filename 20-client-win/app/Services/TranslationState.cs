// P3c -- 翻译工作空间的状态:目标语言池 + 详细程度。落在本机偏好里(每台设备各自)。
//
// 用户裁定的规则(2026-07-30):
//   · 目标池最多 3 个语言;
//   · 输入某语言 -> 翻译成池内【除它以外】的所有语言。
//       例:池 = 中/日,输入中文 -> 译成日语;输入日语 -> 译成中文。
//       例:池 = 中/日/德,输入德语 -> 译成中文 + 日语。
//   · ★ 输入的语种【不在池中】:先把它【加进池】,再翻成池内其它语言。
//       例:池 = 中/日,输入德语 -> 德语入池(中/日/德),译成中文 + 日语。
//   · 池满(3 个)时又来了个新语种:★ 不能静默丢弃,也不该乱踢 —— 见 Plan 的返回值,
//     由界面如实告诉用户"池已满,这次按池内语言翻译"(不自作主张改用户的池)。

namespace LocalAI.Client.Services;

/// <summary>一次输入要怎么翻:翻成哪些语言、是否需要把输入语种加进池、以及为什么。</summary>
public sealed record TranslationPlan(
    string? InputLang,            // 检测出的输入语种(null = 拉丁字母等分不清的,待 AI 判定)
    IReadOnlyList<string> Targets,// 这次要翻成的语言
    bool AddInputToPool,          // 是否应把输入语种加进目标池
    bool PoolFull,                // 池已满,没能把新语种加进去
    bool NeedsAiDetect)           // 语种靠字符集判不出来,需要 AI 判定
{
    /// <summary>
    /// 这次【没有任何可翻的目标】。★ 典型情形:目标池里只有中文,而你输入的也是中文 ——
    /// "翻成池内除输入语言以外的全部"算出来是空集。
    /// 判据里必须带上 !NeedsAiDetect:语种没判出来时(拉丁字母)我们【不知道】它是不是池内那一个,
    /// 这时不能拦 —— 宁可交给 AI 判,也不要凭猜测拦下用户一次正当的翻译。
    /// </summary>
    public bool NothingToDo => !NeedsAiDetect && Targets.Count == 0;
}

public sealed class TranslationState
{
    readonly List<string> _targets = new();

    /// <summary>目标语言池(有序,最多 3)。</summary>
    public IReadOnlyList<string> Targets => _targets;

    public TranslationLevel Level { get; private set; } = TranslationLevel.Reading;

    public event Action? Changed;

    public bool IsFull => _targets.Count >= Languages.MaxTargets;

    public bool Contains(string code) => _targets.Contains(code);

    /// <summary>把语言拖进目标池。已在池中或池满则不动,返回是否真的加进去了。</summary>
    public bool AddTarget(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || _targets.Contains(code) || IsFull) return false;
        _targets.Add(code);
        Changed?.Invoke();
        return true;
    }

    public void RemoveTarget(string code)
    {
        if (_targets.Remove(code)) Changed?.Invoke();
    }

    public void SetLevel(TranslationLevel level)
    {
        if (Level == level) return;
        Level = level;
        Changed?.Invoke();
    }

    /// <summary>
    /// 给定输入文本,算出这次的翻译计划。★ 纯函数式:不改池,只【建议】——
    /// 真正入池由界面在用户可见的情况下执行(Apply),避免"我打了句德语,池子被偷偷改了"。
    /// </summary>
    public TranslationPlan Plan(string? input)
    {
        var detected = Languages.Detect(input);

        // 判不出语种(拉丁字母等):照池内全部翻,并标记需要 AI 判定
        if (detected is null)
            return new TranslationPlan(null, _targets.ToList(), AddInputToPool: false, PoolFull: false, NeedsAiDetect: true);

        // 已在池里:翻成池内其它语言
        if (_targets.Contains(detected))
            return new TranslationPlan(detected, Languages.TargetsFor(_targets, detected), false, false, false);

        // 不在池里:该把它加进去(池满则如实标记,不擅自替换用户的选择)
        var full = IsFull;
        return new TranslationPlan(detected, Languages.TargetsFor(_targets, detected), AddInputToPool: !full, PoolFull: full, NeedsAiDetect: false);
    }

    /// <summary>按计划把输入语种加进池(界面在用户看得见的地方调用)。</summary>
    public void Apply(TranslationPlan plan)
    {
        if (plan.AddInputToPool && plan.InputLang is { } c) AddTarget(c);
    }

    // ---------------------------------------------------------------- 存档(本机偏好)
    public sealed record Snapshot(List<string> Targets, TranslationLevel Level);

    public Snapshot Export() => new(_targets.ToList(), Level);

    public void Import(Snapshot? s)
    {
        if (s is null) return;
        _targets.Clear();
        foreach (var c in s.Targets.Distinct().Take(Languages.MaxTargets))
            if (Languages.Find(c) is not null) _targets.Add(c);   // 认不出的语言码丢掉,别留脏数据
        Level = s.Level;
        Changed?.Invoke();
    }
}
