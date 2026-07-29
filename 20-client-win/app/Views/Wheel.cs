// P3c -- 滚轮冒泡修正。
//
// 问题(用户反馈):鼠标停在按钮/卡片/内层滚动区上时,页面滚不动。
// 成因:WPF 里 ScrollViewer 会【无条件吞掉】滚轮事件 —— 即使它自己已经到顶/到底、
//       或者根本没有可滚动内容,也不会把事件让给外层。于是内层一挡,整页就滚不了。
//
// 修法:给内层滚动区挂 PreviewMouseWheel —— 只有当它【自己还能朝那个方向滚】时才消费,
//       否则把同一个滚轮事件重新抛给父级,由外层页面接手。
//       这也顺带解决"鼠标在按钮上"的情况:按钮本身不处理滚轮,事件会冒泡到最近的滚动区,
//       而那个滚动区现在懂得让路了。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LocalAI.Client.Views;

public static class Wheel
{
    /// <summary>让这个滚动区在"自己滚不动了"时把滚轮让给外层。</summary>
    public static ScrollViewer PassThrough(this ScrollViewer sv)
    {
        sv.PreviewMouseWheel += (s, e) =>
        {
            var self = (ScrollViewer)s;
            var canScrollUp = self.VerticalOffset > 0.5;
            var canScrollDown = self.VerticalOffset < self.ScrollableHeight - 0.5;
            var wantsUp = e.Delta > 0;

            // 自己还能朝这个方向滚 -> 正常处理(不干预)
            if ((wantsUp && canScrollUp) || (!wantsUp && canScrollDown)) return;

            // 自己到头了(或压根没有可滚内容)-> 让给外层
            e.Handled = true;
            var parent = self.Parent as UIElement;
            if (parent is null) return;
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = self,
            });
        };
        return sv;
    }
}
