// P3c -- 「跳到某个设置」时把它框出来(用户裁定 2026-07-30):
//   橙色虚线框住对应板块,5 秒后自然消退;中途切界面也立刻消失。
//
// ★★ 为什么不用原来那套"闪一下透明度":那正是用户报的 bug ——
//   DoubleAnimation(0.35 -> 1) 配 AutoReverse + RepeatBehavior(2),【收在起点】0.35 上,
//   而且没设 FillBehavior.Stop,于是动画结束后把 0.35 一直按着不放:
//   板块从此永久停在 35% 不透明度,看着就是"变灰了"。
//   闪透明度这个手法本身也不好 —— 它改的是内容本身,而我们只想【指出位置】。
//
// ★ 用 Adorner 画在装饰层上:不进布局、不改被指的元素一个像素,消失时也不留痕。

using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace LocalAI.Client.Views;

public sealed class RevealHighlight : Adorner
{
    static readonly TimeSpan Linger = TimeSpan.FromSeconds(5);

    readonly Pen _pen;
    readonly DispatcherTimer _timer;

    RevealHighlight(UIElement target, Brush stroke) : base(target)
    {
        IsHitTestVisible = false;                       // 只是个标记,不能挡住底下的操作
        _pen = new Pen(stroke, 2)
        {
            DashStyle = new DashStyle(new double[] { 4, 3 }, 0),
            DashCap = PenLineCap.Flat,
        };
        _pen.Freeze();

        _timer = new DispatcherTimer { Interval = Linger };
        _timer.Tick += (_, _) => Remove();
        _timer.Start();

        // 切界面/板块被移出可视树 -> 立刻收掉,别把框留在半空
        if (target is FrameworkElement fe) fe.Unloaded += OnTargetUnloaded;
    }

    void OnTargetUnloaded(object sender, RoutedEventArgs e) => Remove();

    /// <summary>把某个元素用橙色虚线框起来。重复调用会先撤掉旧的,不叠框。</summary>
    public static void Show(FrameworkElement target)
    {
        var layer = AdornerLayer.GetAdornerLayer(target);
        if (layer is null) return;                      // 还没进可视树 —— 调用方应等 Loaded 之后再来

        foreach (var a in layer.GetAdorners(target) ?? Array.Empty<Adorner>())
            if (a is RevealHighlight old) old.Remove();

        // ★ 用【着重色反色】而不是写死的橙:标记色的职责是"指出位置",
        //   它必须与该皮肤的着重色对立才不会和选中态/按钮混淆 ——
        //   苹果风的着重色是蓝,反色正好落在橙上;换皮肤时它自己会跟着变。
        var stroke = target.TryFindResource("AccentInverse") as Brush
                     ?? target.TryFindResource("RiskWarning") as Brush
                     ?? Brushes.Orange;
        layer.Add(new RevealHighlight(target, stroke));
    }

    public void Remove()
    {
        _timer.Stop();
        if (AdornedElement is FrameworkElement fe) fe.Unloaded -= OnTargetUnloaded;
        AdornerLayer.GetAdornerLayer(AdornedElement)?.Remove(this);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // 略微外扩,免得虚线压在板块自己的描边上
        var r = new Rect(new Point(-3, -3), new Size(AdornedElement.RenderSize.Width + 6,
                                                     AdornedElement.RenderSize.Height + 6));
        dc.DrawRoundedRectangle(null, _pen, r, 10, 10);
    }
}
