// P3c -- 工作空间清单(单一事实来源)。
//   左导航的可切换工作空间、以及"扩展"里的显示开关,都读这一份 —— 避免两处各写一遍走样。
//   系统项(扩展 / 设置)不在此列:它们常驻、贴底、不可隐藏。
//
// 说明:各工作空间目前是占位(功能待 P4/P6/P9)。财务管理同样先占位;真正实现时须遵守既定边界
//   —— 不提供个性化投资/理财建议、不代替用户下单或转账(仅做记账/预算/账单提醒一类的管理)。

using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public static class Workspaces
{
    public sealed record Def(string Key, string TitleKey, IconName Icon);

    /// <summary>左边栏可显示的工作空间(顺序即显示顺序)。是否显示由用户在"扩展"里勾选。</summary>
    public static readonly Def[] All =
    {
        new("chat", "nav.chat", IconName.Chat),
        new("assets", "nav.assets", IconName.Assets),
        new("translation", "nav.translation", IconName.Translation),
        new("courses", "nav.courses", IconName.Courses),
        new("computer", "nav.computer_control", IconName.Computer),
        new("finance", "nav.finance", IconName.Finance),        // 财务管理(钱包图标)
        new("investment", "nav.investment", IconName.Investment), // 投资研究(走势图图标)—— 之前误删,现补回为可勾选项
    };

    /// <summary>
    /// 按用户在"扩展"里拖定的顺序返回工作空间清单:已存顺序的在前(按其顺序),
    /// 清单里有、顺序里没有的(如新增工作空间)追加在后 —— 保证新工作空间默认可见、不丢。
    /// </summary>
    public static List<Def> Ordered(Services.AppSettings settings)
    {
        var byKey = new Dictionary<string, Def>();
        foreach (var d in All) byKey[d.Key] = d;

        var result = new List<Def>();
        var seen = new HashSet<string>();
        foreach (var k in settings.WorkspaceOrder)
            if (byKey.TryGetValue(k, out var d) && seen.Add(k)) result.Add(d);
        foreach (var d in All)
            if (seen.Add(d.Key)) result.Add(d);
        return result;
    }

    /// <summary>
    /// 左栏【真的显示着】的工作空间(顺序同左栏)。
    /// ★ 凡是"送到哪个空间 / 跳到哪个空间"的【目的地清单】都得用这一份 ——
    ///   在扩展里关掉,等于用户明说过"我不要这个空间";还把东西往那儿送、把人往那儿跳,
    ///   是拿他关掉的东西当默认值。
    /// ★ 反过来,【已经挂着的】标签照旧显示、照旧摘得掉(见 ProjectUi 的"从工作空间移除"),
    ///   否则藏起来的空间会变成摘不掉的死标签。"不在左栏显示"和"这一页不存在"是两件事。
    /// </summary>
    public static List<Def> Visible(Services.AppSettings settings)
        => Ordered(settings).Where(d => settings.IsWorkspaceVisible(d.Key)).ToList();

    /// <summary>这个 key 是不是【认得出来的】工作空间(老存档里可能留着已删掉的 key)。</summary>
    public static bool Known(string? key) => key is not null && All.Any(d => d.Key == key);
}

/// <summary>
/// 主页可显示的板块清单(单一事实来源)。是否显示由用户在"扩展 › 主页板块"里勾选;
/// HomeView 构建时读取,隐藏的板块不占版面。
/// </summary>
public static class HomePanels
{
    public sealed record Def(string Key, string Title, IconName Icon);

    public static readonly Def[] All =
    {
        new("calendar", "日历", IconName.Calendar),
        new("todo", "待办事项", IconName.Member),
        new("weather", "天气", IconName.Weather),
        new("projects", "正在进行的项目", IconName.Tasks),
    };
}
