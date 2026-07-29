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
        new("finance", "nav.finance", IconName.Investment),
    };
}
