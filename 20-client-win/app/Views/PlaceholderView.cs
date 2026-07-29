// P3c -- 尚未实现的工作空间占位。设计 §11 明确:本轮做外壳与骨架,六个工作空间的功能
// 分别依赖 P4/P6/P9 等前置,不在这里假装能用。占位必须**明说没做**,不做假界面。

using System.Windows.Controls;
using LocalAI.Client.I18n;

namespace LocalAI.Client.Views;

public sealed class PlaceholderView : UserControl
{
    readonly StackPanel _extra = new();

    public PlaceholderView(string titleKey)
    {
        Content = Ui.Page(
            Ui.Title(Strings.Get(titleKey)),
            Ui.Card(Ui.Stack(
                Ui.Body(Strings.Get("common.coming_soon")),
                Ui.Caption("外壳与导航已就绪;该工作空间的功能待其前置阶段完成后接入。")
            )),
            _extra
        );
    }

    /// <summary>
    /// 从主页项目方块深链过来时调用。功能未接入,所以【如实说明】要打开哪个项目、
    /// 以及为什么还打不开 —— 不假装已经进入了那个会话。
    /// </summary>
    public void ShowPendingProject(string projectId)
    {
        _extra.Children.Clear();
        _extra.Children.Add(Ui.Card(Ui.Stack(
            Ui.Subtitle("要打开的项目"),
            Ui.Body(projectId),
            Ui.Caption("深链已到位:这个工作空间的功能接入后,这里会直接打开该项目的会话。")
        )));
    }
}
