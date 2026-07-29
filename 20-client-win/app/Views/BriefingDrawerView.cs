// P3c -- 简报(消息栏)抽屉内容。用户裁定:简报不占主页板块,移到【右侧消息栏抽屉】。
//
// 设计 §4.2:应用每天第一次打开时生成;每成员每本地自然日最多主动展示一次;
// 关闭再开不重复弹;留在这里可回看。个人简报只给本人,家庭简报只含家庭范围内容。
//
// ★ 生成简报要靠模型与记忆(P4/P3a 后端),现在没有内容 —— 如实显示"今天还没有简报",
//   不编造摘要(状态矩阵 §8:失败/空态要说清发生了什么)。

using System.Windows;
using System.Windows.Controls;
using LocalAI.Client.I18n;

namespace LocalAI.Client.Views;

public sealed class BriefingDrawerView : UserControl
{
    public BriefingDrawerView()
    {
        var list = Ui.Stack(
            Ui.Panel(Strings.Get("today.briefing"),
                Ui.Stack(
                    Ui.Body("今天还没有简报。", muted: true),
                    Ui.Caption("接入后:应用每天第一次打开时为你生成个人简报,每人每天只主动展示一次;" +
                               "关闭再开不会重复弹,随时可以回到这里回看。")),
                Theme.IconName.Chat, new Thickness(0, 0, 0, 12)),

            Ui.Panel("家庭动态",
                Ui.Stack(
                    Ui.Body("暂无家庭范围的消息。", muted: true),
                    Ui.Caption("只含家庭可见范围的内容;另一位成员的个人与「仅本人」内容永不出现在这里。")),
                Theme.IconName.Member, new Thickness(0, 0, 0, 12)),

            Ui.Panel("系统提示",
                Ui.Stack(
                    Ui.Body("没有需要你处理的事项。", muted: true),
                    Ui.Caption("证书到期、设备待批准、显存吃紧一类需要你决定的事,会出现在这里。")),
                Theme.IconName.Settings, new Thickness(0, 0, 0, 0))
        );

        Content = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough();
    }
}
