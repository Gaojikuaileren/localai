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
