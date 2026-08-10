// V29 -- 「跑不了」怎么说(实机反馈③「在管理端打开客户端不要弹窗」)。
//
// ══════════════════════════════════════════════════════════════════════════════
//  用户原话:在管理端打开客户端不要弹窗。
//
//  ★ 成功路径上原来弹的是一个只有「确定」的信息框(`AdminWindow.Say`)。
//    **客户端窗口本身就是它成功的证据** —— 弹窗只是在要求用户多点一下。
//
//  ★★★ 而失败路径**不许**跟着一起静音。车道任务里的判词逐字:
//    「跑不了和跑过了必须长得不一样。」
//    `ClientLink.StartClient` 返回的是 `(ok, why)`,`why` 里装的是真原因
//    (「旁边没有 ..\client\localai-client.exe —— 这台没装客户端。」/「起不来:…」)。
//    把它一起吞掉,用户点完按钮**什么也不会发生**,而那正是本仓最恨的形状:
//    做不成和做成了在屏幕上一模一样。
//  ⇒ 失败改成**界面上的一行**(不是弹窗):看得见、不用点、留在原地不会自己消失。
//
//  ★ 颜色走 `RiskDanger` 令牌 —— 管理端里不许出现颜色字面量(V21 那条断言),
//    而且换肤时它得跟着变。
// ══════════════════════════════════════════════════════════════════════════════

using System.Windows;
using System.Windows.Controls;
using LocalAI.Client.Views;

namespace LocalAI.Admin.Views;

public static class AdminNotice
{
    /// <summary>
    /// 一行**看得见的失败原因**。★ 用在"这个按钮没做成事"那些地方,替掉信息弹窗。
    /// <para>★ 前缀写「没能打开:」这类主语由调用方给 —— 不同按钮失败的是不同的事,
    /// 在这里统一编一句会把真原因挤掉。</para>
    /// </summary>
    public static TextBlock Failure(string why)
    {
        var t = new TextBlock
        {
            Text = why,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 8),
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "RiskDanger");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        return t;
    }
}
