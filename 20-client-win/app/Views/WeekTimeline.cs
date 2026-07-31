// P3c -- 周时间轴(用户裁定 2026-07-31)。
//
// 形态:竖轴是一天里的时刻(上 0 点、下 24 点),横轴是一周七天 —— 与上方月历的那一周对应。
//   · 最小缩放 = 24 小时全览;默认常态 = 只显示 6 小时;滚轮或按钮缩放。
//   · 内容【全部来自日历日程】—— 它不是另一份数据,只是同一批日程的另一种看法。
//   · 拖日程的上/下边可改开始/结束时间,改的就是日历里那一条(同一个对象)。
//   · 点一下打开【与日历共用的那个编辑抽屉】,不另造一套编辑界面。
//   · 今天整列用着重色标出来。
//
// ★★ 一条贯穿始终的纪律:这里【不存任何日程数据】。
//   画的时候现读 CalendarData,改的时候直接改 CalendarData —— 两边永远是同一份。
//   一旦在这里缓存一份"时间轴自己的日程",就会出现"日历改了时间轴没跟上"的经典割裂。

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class WeekTimeline : UserControl
{
    static readonly CultureInfo Zh = new("zh-CN");

    /// <summary>能看到的最少小时数(放到最大)。</summary>
    public const double MinHours = 2;
    /// <summary>能看到的最多小时数 = 全天(最小缩放,用户裁定)。</summary>
    public const double MaxHours = 24;
    /// <summary>默认常态:只显示 6 小时(用户裁定)。</summary>
    public const double DefaultHours = 6;

    const double GutterWidth = 44;      // 左侧时刻刻度列
    const double HeadHeight = 22;       // 顶部星期几那一行

    DateTime _weekStart;                // 本周一
    double _hours = DefaultHours;       // 当前可见小时数
    double _top = 8;                    // 顶部对应的时刻(小时)

    readonly Grid _head = new();
    readonly Canvas _canvas = new() { ClipToBounds = true };
    readonly Canvas _gutter = new() { Width = GutterWidth, ClipToBounds = true };
    readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center };

    /// <summary>点日程要打开编辑器 —— 由宿主提供,保证与日历【共用同一个】编辑抽屉。</summary>
    public Action<CalendarEvent>? OnEditEvent;

    public WeekTimeline()
    {
        _weekStart = StartOfWeek(DateTime.Today);

        _label.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        _label.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        // 顶栏:← 周 → + 缩放两个键
        var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Step("‹", () => { _weekStart = _weekStart.AddDays(-7); Rebuild(); }));
        left.Children.Add(_label);
        left.Children.Add(Step("›", () => { _weekStart = _weekStart.AddDays(7); Rebuild(); }));
        DockPanel.SetDock(left, Dock.Left); bar.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(Step("－", () => Zoom(1.5, 0.5)));   // 看得更多(小时数变大 = 缩小)
        right.Children.Add(Step("＋", () => Zoom(1 / 1.5, 0.5)));
        right.Children.Add(Step("今", () => { _weekStart = StartOfWeek(DateTime.Today); _hours = DefaultHours; _top = Math.Clamp(DateTime.Now.Hour - 1, 0, 24 - _hours); Rebuild(); }));
        DockPanel.SetDock(right, Dock.Right); bar.Children.Add(right);

        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_gutter, Dock.Left);
        body.Children.Add(_gutter);
        body.Children.Add(_canvas);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        DockPanel.SetDock(_head, Dock.Top); root.Children.Add(_head);
        root.Children.Add(body);
        Content = root;

        // ★ 滚轮缩放(用户裁定)。以【光标所在时刻】为锚点 —— 否则缩放时内容会从指尖溜走。
        MouseWheel += (_, e) =>
        {
            e.Handled = true;
            var f = e.Delta > 0 ? 1 / 1.2 : 1.2;
            var anchor = _canvas.ActualHeight > 0 ? e.GetPosition(_canvas).Y / _canvas.ActualHeight : 0.5;
            Zoom(f, anchor);
        };

        SizeChanged += (_, _) => Rebuild();
        Loaded += (_, _) => { CalendarData.Changed += Rebuild; Rebuild(); };
        Unloaded += (_, _) => CalendarData.Changed -= Rebuild;
    }

    /// <summary>把上方月历选中的那一天所在周同步过来 —— 两块要说的是同一周。</summary>
    public void FocusWeekOf(DateTime day)
    {
        var w = StartOfWeek(day);
        if (w == _weekStart) return;
        _weekStart = w;
        Rebuild();
    }

    static DateTime StartOfWeek(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));   // 周一起始

    /// <param name="anchor">0..1,画布上要保持不动的那个纵向位置。</param>
    void Zoom(double factor, double anchor)
    {
        var hourAt = _top + _hours * anchor;                 // 锚点处对应的时刻
        var next = Math.Clamp(_hours * factor, MinHours, MaxHours);
        if (Math.Abs(next - _hours) < 0.001) return;
        _hours = next;
        _top = Math.Clamp(hourAt - _hours * anchor, 0, 24 - _hours);
        Rebuild();
    }

    FrameworkElement Step(string glyph, Action onClick)
    {
        var t = new TextBlock { Text = glyph, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        t.IsHitTestVisible = false;                          // 命中交给外面那块(项目一贯做法)
        var hit = new Grid { Width = 24, Height = 20, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        hit.Children.Add(t);
        hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return hit;
    }

    // ---------------------------------------------------------------- 绘制
    void Rebuild()
    {
        BuildHead();
        BuildGutter();
        BuildBody();
        _label.Text = $"{_weekStart:M月d日}–{_weekStart.AddDays(6):M月d日}";
    }

    void BuildHead()
    {
        _head.Children.Clear();
        _head.ColumnDefinitions.Clear();
        _head.Height = HeadHeight;
        _head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        for (int i = 0; i < 7; i++) _head.ColumnDefinitions.Add(new ColumnDefinition());

        for (int i = 0; i < 7; i++)
        {
            var d = _weekStart.AddDays(i);
            var isToday = d.Date == DateTime.Today;
            var t = new TextBlock
            {
                Text = d.ToString("ddd", Zh).Replace("星期", "") + " " + d.Day,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, isToday ? "Accent" : "FgMuted");
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            Grid.SetColumn(t, i + 1);
            _head.Children.Add(t);
        }
    }

    void BuildGutter()
    {
        _gutter.Children.Clear();
        var h = _canvas.ActualHeight;
        if (h <= 0) return;

        // 刻度密度随缩放变 —— 显示 6 小时时每小时一条,全天时每 3 小时一条,免得糊成一片
        var stepH = _hours <= 8 ? 1 : _hours <= 16 ? 2 : 3;
        for (double hr = Math.Ceiling(_top); hr <= _top + _hours; hr += 1)
        {
            if ((int)hr % stepH != 0) continue;
            var y = (hr - _top) / _hours * h;
            var t = new TextBlock { Text = ((int)hr % 24).ToString("00") + ":00" };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            Canvas.SetTop(t, y - 8);
            Canvas.SetLeft(t, 4);
            _gutter.Children.Add(t);
        }
    }

    void BuildBody()
    {
        _canvas.Children.Clear();
        var w = _canvas.ActualWidth;
        var h = _canvas.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var colW = w / 7;

        // ① 今天整列着重(用户裁定)—— 先铺底,别盖住日程
        for (int i = 0; i < 7; i++)
        {
            if (_weekStart.AddDays(i).Date != DateTime.Today) continue;
            var band = new System.Windows.Shapes.Rectangle { Width = colW, Height = h, Opacity = 0.10 };
            band.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
            Canvas.SetLeft(band, i * colW);
            Canvas.SetTop(band, 0);
            _canvas.Children.Add(band);
        }

        // ② 横向时刻线 + 竖向分日线
        var stepH = _hours <= 8 ? 1 : _hours <= 16 ? 2 : 3;
        for (double hr = Math.Ceiling(_top); hr <= _top + _hours; hr += 1)
        {
            if ((int)hr % stepH != 0) continue;
            var y = (hr - _top) / _hours * h;
            var line = new System.Windows.Shapes.Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, StrokeThickness = 1, Opacity = 0.5 };
            line.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Border");
            _canvas.Children.Add(line);
        }
        for (int i = 1; i < 7; i++)
        {
            var line = new System.Windows.Shapes.Line { X1 = i * colW, X2 = i * colW, Y1 = 0, Y2 = h, StrokeThickness = 1, Opacity = 0.5 };
            line.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Border");
            _canvas.Children.Add(line);
        }

        // ③ 日程块 —— ★ 现读 CalendarData,不缓存
        for (int i = 0; i < 7; i++)
        {
            var day = _weekStart.AddDays(i);
            foreach (var ev in CalendarData.TimedOn(day))
            {
                var block = EventBlock(ev, colW - 6);
                Canvas.SetLeft(block, i * colW + 3);
                Canvas.SetTop(block, YOf(ev.Start, h));
                _canvas.Children.Add(block);
            }
        }

        // ④ 此刻的红线(只在本周画)
        if (DateTime.Today >= _weekStart && DateTime.Today <= _weekStart.AddDays(6))
        {
            var nowH = DateTime.Now.TimeOfDay.TotalHours;
            if (nowH >= _top && nowH <= _top + _hours)
            {
                var y = (nowH - _top) / _hours * h;
                var now = new System.Windows.Shapes.Line { X1 = 0, X2 = w, Y1 = y, Y2 = y, StrokeThickness = 1.5 };
                now.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "RiskDanger");
                _canvas.Children.Add(now);
            }
        }
    }

    double YOf(DateTime t, double h) => (t.TimeOfDay.TotalHours - _top) / _hours * h;
    double HeightOf(CalendarEvent ev, double h)
        => Math.Max(14, (ev.End - ev.Start).TotalHours / _hours * h);

    /// <summary>
    /// 一条日程的方块。上下各留一条【改时间的边】,中间点开编辑。
    /// ★ 拖动改的是 CalendarData 里那一条 —— 时间轴与日历是同一份数据的两种画法。
    /// </summary>
    FrameworkElement EventBlock(CalendarEvent ev, double width)
    {
        var h = _canvas.ActualHeight;
        var txt = new TextBlock
        {
            Text = ev.Title,
            Margin = new Thickness(5, 2, 4, 2),
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false,
        };
        txt.SetResourceReference(TextBlock.ForegroundProperty, "FgOnAccent");
        txt.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var box = new Border
        {
            Child = txt,
            Width = Math.Max(10, width),
            Height = HeightOf(ev, h),
            Cursor = Cursors.Hand,
        };
        box.SetResourceReference(Border.BackgroundProperty, "Accent");
        box.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");

        const double EdgeGrab = 6;      // 上下各 6px 是"改时间"的边
        box.MouseMove += (_, e) =>
        {
            if (_drag is not null) return;
            var y = e.GetPosition(box).Y;
            box.Cursor = y <= EdgeGrab || y >= box.Height - EdgeGrab ? Cursors.SizeNS : Cursors.Hand;
        };
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            var y = e.GetPosition(box).Y;
            if (y > EdgeGrab && y < box.Height - EdgeGrab) return;      // 中间 -> 留给点击编辑
            e.Handled = true;
            _drag = new Drag(ev, y <= EdgeGrab, e.GetPosition(_canvas).Y);
            _canvas.CaptureMouse();
        };
        box.MouseLeftButtonUp += (_, e) =>
        {
            if (_drag is not null) return;
            e.Handled = true;
            OnEditEvent?.Invoke(ev);
        };
        return box;
    }

    // ---------------------------------------------------------------- 拖动改时间
    sealed record Drag(CalendarEvent Ev, bool IsStart, double FromY);
    Drag? _drag;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag is null) return;
        var h = _canvas.ActualHeight;
        if (h <= 0) return;

        var dy = e.GetPosition(_canvas).Y - _drag.FromY;
        var dh = dy / h * _hours;                                  // 位移换算成小时
        var snapped = Math.Round(dh * 4) / 4;                      // ★ 对齐到 15 分钟 —— 免得拖出 9:07 这种时刻
        if (Math.Abs(snapped) < 0.001) return;

        var ev = _drag.Ev;
        var start = ev.Start;
        var end = ev.End;
        if (_drag.IsStart) start = start.AddHours(snapped);
        else end = end.AddHours(snapped);

        // ★ 不许把开始拖到结束之后(反过来同理)—— 留 15 分钟的最小长度
        if (end - start < TimeSpan.FromMinutes(15)) return;

        CalendarData.Update(ev with { Start = start, End = end });   // 按 Id 更新那一条 —— 日历会同步看到
        _drag = _drag with { Ev = CalendarData.Events.FirstOrDefault(x => x.Id == ev.Id) ?? ev, FromY = e.GetPosition(_canvas).Y };
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag is null) return;
        _drag = null;
        _canvas.ReleaseMouseCapture();
    }
}
