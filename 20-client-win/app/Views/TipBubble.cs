// V30 -- 贴着某个控件弹出的【小气泡提示】,自动收。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ 用户裁定(2026-08-09):「如果用户点击禁用的发送按钮,应该在按钮上**气泡提示**:
//    模型未启用或者模型正在启用中请稍等」。这个类就是那颗气泡。
//
//  ══════ 为什么不是 ToolTip ═══════════════════════════════════════════════
//   ToolTip 是**悬停**才出来的,而裁定要的是**点击**时出来。
//   ★ 而且更要命的一条:WPF 的 ToolTip **默认在禁用的控件上根本不显示** ——
//     挂在一颗灰按钮上的 ToolTip,恰恰在最需要它的时候一个字都不说。
//     (同一族缺陷本轮在 `TaskDrawerView` 的「再开」键上也修了一处,那儿用
//      `ToolTipService.SetShowOnDisabled` 就够,因为那一处本来就是悬停语义。)
//
//  ══════ 为什么不走 MenuHost ══════════════════════════════════════════════
//   `MenuHost` 管的是**菜单**(ContextMenu),它存在的理由是:
//   `StaysOpen=false` 的菜单会在鼠标【按下】那一刻关掉,而按钮多挂在【松开】上,
//   于是"按下关菜单、松开点按钮" —— 一次点击干了两件事(2026-07-30 实测点不动)。
//   ★★ 本气泡 `StaysOpen = true`:它**不抓鼠标、不吞点击、不参与关闭竞争**,
//     那条穿透风险在结构上就不存在,所以它不需要登记,也不该混进菜单那套账里。
//   ★ 收法是**定时**(以及调用方在状态变好时主动收),不是"点外面关" ——
//     "点外面关"正是要抓鼠标才做得到的那种。
//
//  ★ 抽成独立文件而不是塞在 ChatView 里,有第三个好处:
//    `TranslationBar` 里已经手写过一份同样的东西(`ShowTip`/`HideTip`,自绘在 overlay 上)。
//    那一份不在本轮改动面内(不动它),但这里留个记号:**这类气泡已经有两份实现了**,
//    下一个要用的人应当收敛到这一份,而不是写第三份。
// ══════════════════════════════════════════════════════════════════════════════

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace LocalAI.Client.Views;

/// <summary>
/// 一颗贴着锚点控件弹出、几秒后自动收的小气泡。★ 一个实例同时只有一颗。
/// </summary>
public sealed class TipBubble
{
    /// <summary>自动收的时间。★ 短到不碍事,长到读得完两行字。</summary>
    static readonly TimeSpan AutoHideAfter = TimeSpan.FromSeconds(4);

    Popup? _popup;
    DispatcherTimer? _timer;

    /// <summary>现在有气泡开着吗(自检与调用方判重用)。</summary>
    public bool IsShowing => _popup is not null;

    /// <summary>
    /// 在锚点控件**上方**弹一颗气泡。★ 重复调用会先收掉上一颗 —— 两颗叠着谁也读不清。
    /// </summary>
    public void Show(FrameworkElement anchor, string text)
    {
        Hide();
        if (string.IsNullOrWhiteSpace(text)) return;   // ★ 没话说就不弹一个空框

        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var card = new Border { Child = t, Padding = new Thickness(10, 7, 10, 7), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.CornerRadius = new CornerRadius(10);
        // ★ 气泡浮在别的内容上面,必须自己有底色 —— 透明的话会和底下的字叠成一团。
        //   BgSurface 是主题色,深浅皮肤都跟着走(不写死颜色)。

        _popup = new Popup
        {
            Child = card,
            PlacementTarget = anchor,
            Placement = PlacementMode.Top,
            VerticalOffset = -6,
            AllowsTransparency = true,
            // ★★ StaysOpen = true:见文件头 —— false 会抓走鼠标并吞掉紧接着的下一次点击。
            StaysOpen = true,
        };
        _popup.IsOpen = true;

        _timer = new DispatcherTimer { Interval = AutoHideAfter };
        _timer.Tick += (_, _) => Hide();
        _timer.Start();
    }

    /// <summary>收掉。★ 可以随便多调 —— 没开着时什么都不做。</summary>
    public void Hide()
    {
        _timer?.Stop();
        _timer = null;
        if (_popup is null) return;
        _popup.IsOpen = false;
        _popup = null;
    }
}
