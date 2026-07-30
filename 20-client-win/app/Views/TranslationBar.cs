// P3c -- 翻译工作空间的【下半部分】:程度滑条 + 目标池 + 语言池 + 学习笔记预览。
//
// 用户裁定(2026-07-30,第二轮):
//   · 语言在【语言池 ⇄ 目标池】之间【拖来拖去】—— 取消也是拖回去,不是点一下就没了;
//     另给一个【清空】按钮,一键把目标池的语言都送回语言池;
//   · 拖动要【跟手】—— ★ 所以不能用 OLE 拖放(DragDrop.DoDragDrop 根本不移动元素,只换光标),
//     改成自己捕获鼠标 + 一个跟着指针走的浮层气泡(与天气卡拖拽同一套教训);
//   · 翻译程度要【滑条】,每个节点有解释气泡(★ 全局 ToolTip 已关,这里自绘一个小气泡);
//   · 目标池宽度 = 刚好放下三个气泡;学习笔记往左多占一些空间;
//   · 目标池满 3 个 -> 语言池整体【灰掉禁用】(拖不动了)。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class TranslationBar : UserControl
{
    static App TheApp => (App)Application.Current;

    public const double BarHeight = 176;
    /// <summary>目标池宽度 = 三个气泡刚好放满(气泡约 54 + 间距,再加卡片内边距)。</summary>
    const double PoolWidth = 208;

    // 负的下边距吃掉【最后一行气泡】的 6px 外边距 —— 否则内容比可视区高 6px,
    // ScrollViewer 就会挂出一条多余的滚动条(渲染诊断里看得一清二楚)。
    readonly WrapPanel _targetWrap = new() { Margin = new Thickness(0, 0, 0, -6) };
    readonly WrapPanel _poolWrap = new() { Margin = new Thickness(0, 0, 0, -6) };
    readonly StackPanel _notesPreview = new();
    readonly Canvas _overlay = new() { IsHitTestVisible = false };   // 跟手气泡 + 节点气泡都画在这层
    Border _targetBox = null!, _poolBox = null!;
    Slider _levelSlider = null!;
    Border? _tipBubble;

    public TranslationBar()
    {
        Height = BarHeight;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PoolWidth + 16) });        // 程度 + 目标池
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PoolWidth + 16) });        // 语言池
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // 学习笔记(占剩下的,更宽)

        var left = new DockPanel { LastChildFill = true };
        var lvl = LevelCard();
        DockPanel.SetDock(lvl, Dock.Top);
        left.Children.Add(lvl);
        left.Children.Add(TargetCard());
        Grid.SetColumn(left, 0);

        var pool = PoolCard(); Grid.SetColumn(pool, 1);
        var notes = NotesCard(); Grid.SetColumn(notes, 2);
        grid.Children.Add(left); grid.Children.Add(pool); grid.Children.Add(notes);

        var root = new Grid();
        root.Children.Add(grid);
        root.Children.Add(_overlay);
        Content = root;

        Refresh();
        Loaded += (_, _) => { TheApp.Translation.Changed += Refresh; TheApp.Notes.Changed += Refresh; };
        Unloaded += (_, _) => { TheApp.Translation.Changed -= Refresh; TheApp.Notes.Changed -= Refresh; };
    }

    void Refresh() { RefreshPools(); RefreshLevel(); RefreshNotes(); }

    // ---------------------------------------------------------------- 程度:滑条 + 每档解释气泡
    FrameworkElement LevelCard()
    {
        _levelSlider = new Slider
        {
            Minimum = 0, Maximum = TranslationLevels.All.Length - 1,
            TickFrequency = 1, IsSnapToTickEnabled = true,
            Value = (int)TheApp.Translation.Level,
        };
        _levelSlider.ValueChanged += (_, _) => TheApp.Translation.SetLevel((TranslationLevel)(int)_levelSlider.Value);

        // 四个节点标签:hover 时在上方弹出自绘小气泡解释这一档(全局 ToolTip 已关,这里自己画)。
        // ★ 用 Canvas 手工摆位而不是等分格子:等分格子的【格心】和滑块的【落点】不是一回事
        //   (滑块首尾贴着轨道两端,格心却缩在 1/8、3/8…),差十几像素,看着就是"标签没对准节点"。
        var ticks = new Canvas { Height = 18, Margin = new Thickness(0, 2, 0, 0) };
        var cells = new List<Border>();
        foreach (var (level, name, desc) in TranslationLevels.All)
        {
            var t = new TextBlock { Text = name, TextAlignment = TextAlignment.Center, Cursor = Cursors.Hand };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            var cell = new Border { Child = t, Background = Brushes.Transparent, Padding = new Thickness(2, 0, 2, 0) };
            var capturedLevel = level;
            var capturedDesc = $"{name} —— {desc}";
            cell.MouseEnter += (_, _) => ShowTip(cell, capturedDesc);
            cell.MouseLeave += (_, _) => HideTip();
            cell.MouseLeftButtonUp += (_, e) => { e.Handled = true; TheApp.Translation.SetLevel(capturedLevel); };
            cells.Add(cell);
            ticks.Children.Add(cell);
        }
        ticks.SizeChanged += (_, _) => PlaceTicks(ticks, cells);

        var body = new StackPanel();
        body.Children.Add(_levelSlider);
        body.Children.Add(ticks);
        return Card(body, "翻译程度", scroll: false);
    }

    /// <summary>把四个档位标签摆到滑块【真正会停的位置】下面(轨道两端各让出半个滑块的宽度)。</summary>
    static void PlaceTicks(Canvas host, List<Border> cells)
    {
        const double thumbHalf = 8;   // 滑块是 16px 的圆点(Controls.xaml)
        var w = host.ActualWidth;
        if (w <= 0 || cells.Count < 2) return;
        var span = Math.Max(0, w - thumbHalf * 2);
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var x = thumbHalf + span * i / (cells.Count - 1) - c.DesiredSize.Width / 2;
            Canvas.SetLeft(c, Math.Clamp(x, 0, Math.Max(0, w - c.DesiredSize.Width)));
        }
    }

    void RefreshLevel()
    {
        var lv = TheApp.Translation.Level;
        if ((int)_levelSlider.Value != (int)lv) _levelSlider.Value = (int)lv;
    }

    /// <summary>自绘的小气泡提示(全局 ToolTip 已关,但这里用户明确要提示)。</summary>
    void ShowTip(FrameworkElement anchor, string text)
    {
        HideTip();
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 190 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        _tipBubble = new Border { Child = t, Padding = new Thickness(9, 6, 9, 6), BorderThickness = new Thickness(1) };
        _tipBubble.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        _tipBubble.SetResourceReference(Border.BorderBrushProperty, "Border");
        _tipBubble.CornerRadius = new CornerRadius(10);
        _overlay.Children.Add(_tipBubble);

        var p = anchor.TranslatePoint(new Point(0, 0), _overlay);
        _tipBubble.Measure(new Size(200, 200));
        Canvas.SetLeft(_tipBubble, Math.Max(0, p.X - 20));
        Canvas.SetTop(_tipBubble, Math.Max(0, p.Y - _tipBubble.DesiredSize.Height - 6));
    }

    void HideTip()
    {
        if (_tipBubble is not null) { _overlay.Children.Remove(_tipBubble); _tipBubble = null; }
    }

    // ---------------------------------------------------------------- 目标池 / 语言池
    FrameworkElement TargetCard()
    {
        var clear = Chip("清空", () =>
        {
            foreach (var c in TheApp.Translation.Targets.ToList()) TheApp.Translation.RemoveTarget(c);
        });
        _targetBox = Card(_targetWrap, $"目标池(最多 {Languages.MaxTargets})", action: clear);
        _targetBox.Width = PoolWidth;
        _targetBox.HorizontalAlignment = HorizontalAlignment.Left;
        _targetBox.Margin = new Thickness(0, 8, 0, 0);
        return _targetBox;
    }

    FrameworkElement PoolCard()
    {
        _poolBox = Card(_poolWrap, "语言池",
            gear: () => (Application.Current.MainWindow as MainWindow)?.OpenLanguagePoolSettings());
        _poolBox.Width = PoolWidth;
        _poolBox.HorizontalAlignment = HorizontalAlignment.Left;
        _poolBox.Margin = new Thickness(8, 0, 0, 0);
        return _poolBox;
    }

    void RefreshPools()
    {
        var st = TheApp.Translation;

        // ★ 目标池满了 -> 语言池整体灰掉禁用(拖不动)
        var poolDisabled = st.IsFull;
        _poolBox.Opacity = poolDisabled ? 0.45 : 1;
        _poolBox.IsHitTestVisible = !poolDisabled;

        _poolWrap.Children.Clear();
        foreach (var code in TheApp.Settings.TranslationPool)
        {
            var l = Languages.Find(code);
            if (l is null || st.Contains(code)) continue;     // 已在目标池的不在这边重复出现
            _poolWrap.Children.Add(Bubble(l, selected: false));
        }
        if (_poolWrap.Children.Count == 0)
            _poolWrap.Children.Add(Ui.Caption(poolDisabled ? "目标池已满(3)" : "都在目标池里了"));

        _targetWrap.Children.Clear();
        if (st.Targets.Count == 0)
            _targetWrap.Children.Add(Ui.Caption("把语言拖进来"));
        else
            foreach (var code in st.Targets)
            {
                var l = Languages.Find(code);
                if (l is not null) _targetWrap.Children.Add(Bubble(l, selected: true));
            }
    }

    // 语言气泡。★ 拖动是【自己捕获鼠标】做的 —— OLE 拖放不移动元素,做不出跟手效果。
    Border Bubble(Lang l, bool selected)
    {
        var t = new TextBlock { Text = l.Name, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, selected ? "FgOnAccent" : "FgPrimary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border
        {
            Child = t, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };
        b.SetResourceReference(Border.BackgroundProperty, selected ? "Accent" : "BgSurface");
        b.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "Border");
        b.PreviewMouseLeftButtonDown += (_, e) => BeginDrag(b, l, selected, e);
        return b;
    }

    // ---------------------------------------------------------------- 跟手拖拽(手动实现)
    Border? _ghost;          // 跟着指针走的浮层气泡
    Lang? _dragLang;
    bool _dragFromTarget;
    bool _dragging;
    Point _dragStart;

    void BeginDrag(Border source, Lang l, bool fromTarget, MouseButtonEventArgs e)
    {
        _dragLang = l;
        _dragFromTarget = fromTarget;
        _dragging = false;
        _dragStart = e.GetPosition(this);
        CaptureMouse();
        MouseMove += OnDragMove;
        MouseLeftButtonUp += OnDragEnd;
        LostMouseCapture += OnDragLost;
        e.Handled = true;
    }

    void OnDragMove(object? sender, MouseEventArgs e)
    {
        if (_dragLang is null) return;
        var p = e.GetPosition(this);
        if (!_dragging)
        {
            // 超过一点距离才算拖动 —— 否则轻轻一点也会被当成拖
            if (Math.Abs(p.X - _dragStart.X) < 4 && Math.Abs(p.Y - _dragStart.Y) < 4) return;
            _dragging = true;
            _ghost = Bubble(_dragLang, _dragFromTarget);
            _ghost.Opacity = 0.9;
            _ghost.IsHitTestVisible = false;
            _overlay.Children.Add(_ghost);
        }
        if (_ghost is null) return;
        Canvas.SetLeft(_ghost, p.X - 28);      // 让气泡大致贴在指针下方偏左,像被捏住
        Canvas.SetTop(_ghost, p.Y - 14);

        // 目标高亮:指针落在哪个池子上就点亮哪个
        var overTarget = Hit(_targetBox, e);
        var overPool = Hit(_poolBox, e);
        _targetBox.SetResourceReference(Border.BorderBrushProperty, overTarget && !_dragFromTarget ? "Accent" : "Border");
        _poolBox.SetResourceReference(Border.BorderBrushProperty, overPool && _dragFromTarget ? "Accent" : "Border");
    }

    bool Hit(FrameworkElement el, MouseEventArgs e)
    {
        var p = e.GetPosition(el);
        return p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight;
    }

    void OnDragEnd(object? sender, MouseButtonEventArgs e) => FinishDrag(e);
    void OnDragLost(object? sender, MouseEventArgs e) => FinishDrag(null);

    void FinishDrag(MouseButtonEventArgs? e)
    {
        MouseMove -= OnDragMove;
        MouseLeftButtonUp -= OnDragEnd;
        LostMouseCapture -= OnDragLost;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _targetBox.SetResourceReference(Border.BorderBrushProperty, "Border");
        _poolBox.SetResourceReference(Border.BorderBrushProperty, "Border");

        var lang = _dragLang;
        var fromTarget = _dragFromTarget;
        var wasDragging = _dragging;
        _dragLang = null;
        _dragging = false;

        if (_ghost is not null) { _overlay.Children.Remove(_ghost); _ghost = null; }
        if (lang is null || !wasDragging || e is null) return;

        // 落点决定去留:语言池 -> 目标池 = 加;目标池 -> 语言池 = 移出(用户裁定:取消也是拖回去)
        if (!fromTarget && Hit(_targetBox, e)) TheApp.Translation.AddTarget(lang.Code);
        else if (fromTarget && Hit(_poolBox, e)) TheApp.Translation.RemoveTarget(lang.Code);
    }

    // ---------------------------------------------------------------- 学习笔记(预览)
    FrameworkElement NotesCard()
    {
        var card = Card(_notesPreview, "学习笔记",
            action: Chip("全部", () => (Application.Current.MainWindow as MainWindow)?.OpenSideDrawer(
                "学习笔记", new NotesBoardView(), IconName.Translation)));
        card.Margin = new Thickness(8, 0, 0, 0);
        return card;
    }

    void RefreshNotes()
    {
        _notesPreview.Children.Clear();
        var latest = TheApp.Notes.Items.OrderByDescending(n => n.CreatedAt ?? DateTime.MinValue).Take(4).ToList();
        if (latest.Count == 0)
        {
            _notesPreview.Children.Add(Ui.Caption("翻译结果右侧点收藏,就会存到这里(按语言分类)。"));
            return;
        }
        foreach (var n in latest)
        {
            var line = new TextBlock { Text = $"[{Languages.NameOf(n.Lang)}] {n.Translation}", TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 3) };
            line.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            line.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            _notesPreview.Children.Add(line);
        }
    }

    // ---------------------------------------------------------------- 小工具
    static Border Card(UIElement body, string title, Action? gear = null, FrameworkElement? action = null, bool scroll = true)
    {
        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        var t = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        DockPanel.SetDock(t, Dock.Left);
        head.Children.Add(t);
        if (gear is not null)
        {
            var g = Icons.Make(IconName.Settings, 14, "FgMuted");
            var gb = new Border { Child = g, Padding = new Thickness(4), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            gb.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            gb.MouseEnter += (_, _) => gb.SetResourceReference(Border.BackgroundProperty, "BgHover");
            gb.MouseLeave += (_, _) => gb.Background = Brushes.Transparent;
            gb.MouseLeftButtonUp += (_, e) => { e.Handled = true; gear(); };
            DockPanel.SetDock(gb, Dock.Right);
            head.Children.Add(gb);
        }
        if (action is not null) { DockPanel.SetDock(action, Dock.Right); head.Children.Add(action); }

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(scroll
            ? new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }.PassThrough()
            : body);

        var card = new Border { Child = dock, Padding = new Thickness(10), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        return card;
    }

    static FrameworkElement Chip(string text, Action onClick)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border { Child = t, Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }
}
