// P3c -- 尚未实现的工作空间占位。设计 §11 明确:本轮做外壳与骨架,六个工作空间的功能
// 分别依赖 P4/P6/P9 等前置,不在这里假装能用。占位必须**明说没做**,不做假界面。

using System.Windows.Controls;
using LocalAI.Client.I18n;

namespace LocalAI.Client.Views;

public sealed class PlaceholderView : UserControl
{
    public PlaceholderView(string titleKey)
    {
        Content = Ui.Page(
            Ui.Title(Strings.Get(titleKey)),
            Ui.Card(Ui.Stack(
                Ui.Body(Strings.Get("common.coming_soon")),
                Ui.Caption("外壳与导航已就绪;该工作空间的功能待其前置阶段完成后接入。")
            ))
        );
    }
}
