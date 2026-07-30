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
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class TranslationBar : UserControl
{
    static App TheApp => (App)Application.Current;

    public const double BarHeight = 176;
    /// <summary>目标池宽度 = 三个气泡刚好放满(气泡约 54 + 间距,再加卡片内边距)。</summary>
    /// <summary>
    /// 目标池 = 1 列 × 3 行语言标签;语言池 = 2 列 × 3 行,宽度正好是目标池的两倍(用户裁定)。
    /// ★ 标签在格子里【拉伸填满】,所以"1×3 / 2×3"是真的格子,不是靠估宽度凑出来的。
    /// </summary>
    const double TargetPoolWidth = 92;
    const double LangPoolWidth = TargetPoolWidth * 2;
    /// <summary>翻译程度那一列的宽度:一条竖滑条 + 四个档位名。</summary>
    const double LevelWidth = 132;

    // 负的下边距吃掉【最后一行气泡】的 6px 外边距 —— 否则内容比可视区高 6px,
    // ScrollViewer 就会挂出一条多余的滚动条(渲染诊断里看得一清二楚)。
    // 负的下边距吃掉【最后一行标签】的外边距 —— 否则内容比可视区高一点,
    // ScrollViewer 就会挂出一条多余的滚动条(渲染诊断里看得一清二楚)。
    readonly UniformGrid _targetWrap = new() { Columns = 1, Margin = new Thickness(0, 0, 0, -6) };
    readonly UniformGrid _poolWrap = new() { Columns = 2, Margin = new Thickness(0, 0, 0, -6) };
    readonly StackPanel _notesPreview = new();
    readonly Canvas _overlay = new() { IsHitTestVisible = false };   // 跟手气泡 + 节点气泡都画在这层
    Border _targetBox = null!, _poolBox = null!;
    Slider _levelSlider = null!;
    readonly List<(TranslationLevel Level, TextBlock Label)> _levelLabels = new();
    Border? _tipBubble;

    public TranslationBar()
    {
        Height = BarHeight;

        // ★ 四列并排(用户第三轮裁定):程度【竖排】| 目标池 | 语言池 | 学习笔记。
        //   目标池与语言池是【并列关系】—— 左右排布、同宽同高,而不是一上一下。
        //   语言在两者之间拖来拖去,并排才看得出"从这边搬到那边"。
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LevelWidth) });            // 翻译程度(竖)
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TargetPoolWidth + 8) });   // 目标池 1×3
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LangPoolWidth + 8) });     // 语言池 2×3(两倍宽)
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // 学习笔记(占剩下的)

        var lvl = LevelCard(); Grid.SetColumn(lvl, 0);
        var target = TargetCard(); Grid.SetColumn(target, 1);
        var pool = PoolCard(); Grid.SetColumn(pool, 2);
        var notes = NotesCard(); Grid.SetColumn(notes, 3);
        grid.Children.Add(lvl); grid.Children.Add(target); grid.Children.Add(pool); grid.Children.Add(notes);

        var root = new Grid();
        root.Children.Add(grid);
        root.Children.Add(_overlay);
        Content = root;

        Refresh();
        Loaded += (_, _) => { TheApp.Translation.Changed += Refresh; TheApp.Notes.Changed += Refresh; };
        // ★ 卸载时必须把拖拽善后掉:界面在拖到一半时被重建的话,鼠标捕获会跟着这个已经不在
        //   可视树上的控件走 —— 那之后整个窗口的点击都到不了别处(与"点不动"同一类事故)。
        Unloaded += (_, _) =>
        {
            TheApp.Translation.Changed -= Refresh;
            TheApp.Notes.Changed -= Refresh;
            FinishDrag(null);
        };
    }

    void Refresh() { RefreshPools(); RefreshLevel(); RefreshNotes(); }

    // ---------------------------------------------------------------- 程度:竖排滑条 + 每档解释气泡
    FrameworkElement LevelCard()
    {
        // ★ 竖排,且【从下往上】递进(用户裁定):第一阶「直译」在底,越往上越详,顶上是「语法」。
        //   WPF 竖向 Slider 默认就是最小值在下,所以【不要】设 IsDirectionReversed ——
        //   设了就变成从上往下拉,正是要改掉的那个方向。标签排列也要跟着倒过来(见下)。
        _levelSlider = new Slider
        {
            Orientation = Orientation.Vertical,
            Minimum = 0, Maximum = TranslationLevels.All.Length - 1,
            TickFrequency = 1, IsSnapToTickEnabled = true,
            Value = (int)TheApp.Translation.Level,
            Margin = new Thickness(0, 2, 8, 2),
        };
        _levelSlider.ValueChanged += (_, _) => TheApp.Translation.SetLevel((TranslationLevel)(int)_levelSlider.Value);

        // 四个档位标签竖着排在滑条右边,hover 弹自绘小气泡解释这一档(全局 ToolTip 已关)。
        // 第二阶的名字随目标池变(读音 / 词根),所以标签文字要能刷新 —— 存起来备用。
        var rows = new Grid();
        for (int i = 0; i < TranslationLevels.All.Length; i++)
            rows.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _levelLabels.Clear();
        // 倒着放:行 0 = 最详的档位(顶),最后一行 = 直译(底),与滑条方向一致
        var ordered = TranslationLevels.All.Reverse().ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            var (level, name, desc) = ordered[i];
            var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            var cell = new Border { Child = t, Background = Brushes.Transparent, Padding = new Thickness(2, 0, 2, 0) };
            var capturedLevel = level;
            cell.MouseEnter += (_, _) => ShowTip(cell, $"{LabelFor(capturedLevel)} —— {DescFor(capturedLevel)}");
            cell.MouseLeave += (_, _) => HideTip();
            cell.MouseLeftButtonUp += (_, e) => { e.Handled = true; TheApp.Translation.SetLevel(capturedLevel); };
            Grid.SetRow(cell, i);
            rows.Children.Add(cell);
            _levelLabels.Add((level, t));
        }

        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_levelSlider, Dock.Left);
        body.Children.Add(_levelSlider);
        body.Children.Add(rows);

        var card = Card(body, "翻译程度", scroll: false);
        card.Width = LevelWidth;
        card.HorizontalAlignment = HorizontalAlignment.Left;
        return card;
    }

    /// <summary>档位显示名。★ 第二阶随目标池变:非拉丁语言给【读音】,拉丁语言(英/德)给【词根】。</summary>
    string LabelFor(TranslationLevel l)
        => l == TranslationLevel.Reading
            ? TranslationLevels.SecondStageLabel(TheApp.Translation.Targets)
            : TranslationLevels.NameOf(l);

    string DescFor(TranslationLevel l) => TranslationLevels.DescOf(l);

    void RefreshLevel()
    {
        var lv = TheApp.Translation.Level;
        if ((int)_levelSlider.Value != (int)lv) _levelSlider.Value = (int)lv;
        // 第二阶的名字跟着目标池走(全是英/德 -> 词根;全是中日韩 -> 读音;混着 -> 两个都写)
        foreach (var (level, label) in _levelLabels) label.Text = LabelFor(level);
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
        // ★ 标题只留三个字:目标池只有一列宽(92px),再加"(最多 3)"就会把标题和「清空」一起挤出去。
        //   上限改写在空态提示里 —— 那里本来就有位置,也更是用户真正需要看到它的时机。
        _targetBox = Card(_targetWrap, "目标池", action: clear);
        _targetBox.Width = TargetPoolWidth;
        _targetBox.HorizontalAlignment = HorizontalAlignment.Left;
        return _targetBox;
    }

    FrameworkElement PoolCard()
    {
        _poolBox = Card(_poolWrap, "语言池",
            gear: () => (Application.Current.MainWindow as MainWindow)?.OpenLanguagePoolSettings());
        _poolBox.Width = LangPoolWidth;
        _poolBox.HorizontalAlignment = HorizontalAlignment.Left;
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
            _poolWrap.Children.Add(Bubble(l, selected: false, stackIndex: _poolWrap.Children.Count));
        }
        if (_poolWrap.Children.Count == 0)
            _poolWrap.Children.Add(Ui.Caption(poolDisabled ? "目标池已满(3)" : "都在目标池里了"));

        _targetWrap.Children.Clear();
        if (st.Targets.Count == 0)
            _targetWrap.Children.Add(Ui.Caption($"拖进来{Environment.NewLine}(最多 {Languages.MaxTargets})"));
        else
            foreach (var code in st.Targets)
            {
                var l = Languages.Find(code);
                if (l is not null) _targetWrap.Children.Add(Bubble(l, selected: true, stackIndex: _targetWrap.Children.Count));
            }
    }

    /// <summary>
    /// 语言卡片。★ 观感【按皮肤分档】(用户裁定):
    ///   · 暖萌 = 抽屉里的一叠卡片 —— 互相压着、各带一点歪斜,拿起来像从堆里抽一张;
    ///   · 微风(苹果风)/ 墨白 = 克制的胶囊,不歪不叠,只有干净的圆角。
    /// 拖动一律是【自己捕获鼠标】做的 —— OLE 拖放不移动元素,做不出跟手效果。
    /// </summary>
    Border Bubble(Lang l, bool selected, int stackIndex = 0)
    {
        var playful = ThemeManager.Current == Skin.Warm;

        var t = new TextBlock
        {
            Text = l.Name, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,   // 「西班牙语」这种长名字也不撑破格子
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, selected ? "FgOnAccent" : "FgPrimary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border
        {
            Child = t,
            Padding = playful ? new Thickness(8, 7, 8, 7) : new Thickness(8, 5, 8, 5),
            // 标签在格子里【拉伸填满】(1×3 / 2×3 是真的格子)。
            // 暖萌:上边距为负 -> 后一张压住前一张,叠成一摞;克制皮肤下规规矩矩留空。
            Margin = playful ? new Thickness(0, stackIndex < 2 ? 0 : -5, 6, 6) : new Thickness(0, 0, 6, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // ★ 固定高度 + 顶对齐:UniformGrid 会把每个格子撑满可用高度,
            //   不钉住的话标签会被拉成一根高条(渲染诊断里当场看到)。
            VerticalAlignment = VerticalAlignment.Top,
            Height = 30,
            CornerRadius = new CornerRadius(playful ? 8 : 14),   // 卡片是方一点的圆角,胶囊才是大圆角
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };
        b.SetResourceReference(Border.BackgroundProperty, selected ? "Accent" : "BgSurface");
        b.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "Border");

        if (playful)
        {
            // 每张歪一点点(角度按序号定,不随机 —— 重建时不能跳来跳去),并让后面的压在上层
            b.RenderTransformOrigin = new Point(0.5, 1);
            b.RenderTransform = new RotateTransform(TiltFor(stackIndex));
            Panel.SetZIndex(b, stackIndex);
            b.Effect = new DropShadowEffect
            {
                BlurRadius = 6, ShadowDepth = 1.5, Direction = 270, Opacity = 0.22,
                Color = Colors.Black, RenderingBias = RenderingBias.Performance,
            };
            // 悬停时"抽出来一点",提示它可以被拿走
            b.MouseEnter += (_, _) => Lift(b, -3);
            b.MouseLeave += (_, _) => Lift(b, 0);
        }

        b.PreviewMouseLeftButtonDown += (_, e) => BeginDrag(b, l, selected, e);
        return b;
    }

    /// <summary>一摞卡片的歪斜角:按序号在 ±2.5° 之间来回,不用随机 —— 界面重建时不能跳。</summary>
    static double TiltFor(int i) => (i % 3) switch { 0 => -2.2, 1 => 1.6, _ => 2.6 };

    static void Lift(Border card, double dy)
    {
        var tt = card.RenderTransform as TransformGroup;
        if (tt is null)
        {
            var g = new TransformGroup();
            g.Children.Add(card.RenderTransform ?? new RotateTransform(0));
            g.Children.Add(new TranslateTransform());
            card.RenderTransform = g;
            tt = g;
        }
        if (tt.Children[^1] is TranslateTransform tr)
            tr.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(dy, TimeSpan.FromMilliseconds(120))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>
    /// 落地:在落点扬起一小撮灰 + 一声闷响。★ 只在暖萌皮肤下发生(用户裁定)——
    /// 微风与墨白要克制,不出声也不扬尘。
    /// </summary>
    void PlayLanding(Point at)
    {
        if (ThemeManager.Current != Skin.Warm) return;

        if (TheApp.Settings.SoundEffects) Services.Sfx.PlayDrop();   // 设置里可关;关声音不影响扬尘

        // 六粒尘:向外上方散开,同时变大变淡。用 Canvas 层画,不影响布局。
        for (int i = 0; i < 6; i++)
        {
            var d = new Ellipse { Width = 5, Height = 5, Opacity = 0.5, IsHitTestVisible = false };
            d.SetResourceReference(Shape.FillProperty, "FgMuted");
            Canvas.SetLeft(d, at.X - 2.5);
            Canvas.SetTop(d, at.Y - 2.5);
            _overlay.Children.Add(d);

            var dir = (i - 2.5) * 9;                       // 左右铺开
            var move = new TranslateTransform();
            d.RenderTransform = move;
            var dur = TimeSpan.FromMilliseconds(420 + i * 25);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dir, dur) { EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -10 - (i % 3) * 4, dur) { EasingFunction = ease });
            var fade = new DoubleAnimation(0.5, 0, dur) { EasingFunction = ease };
            var dust = d;
            fade.Completed += (_, _) => _overlay.Children.Remove(dust);
            d.BeginAnimation(OpacityProperty, fade);
        }
    }

    // ---------------------------------------------------------------- 跟手拖拽(手动实现)
    Border? _ghost;          // 跟着指针走的浮层气泡
    Lang? _dragLang;
    bool _dragFromTarget;
    bool _dragging;
    Point _dragStart;

    void BeginDrag(Border source, Lang l, bool fromTarget, MouseButtonEventArgs e)
    {
        // ★ 抓不到鼠标就【别开始拖】:抓不到的话松开事件不一定回到我们身上,
        //   处理器就会一直挂着、状态清不掉。宁可这一下不拖,也不要留个半拖状态。
        if (!CaptureMouse()) return;
        _dragLang = l;
        _dragFromTarget = fromTarget;
        _dragging = false;
        _dragStart = e.GetPosition(this);
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
        var landed = false;
        if (!fromTarget && Hit(_targetBox, e)) landed = TheApp.Translation.AddTarget(lang.Code);
        else if (fromTarget && Hit(_poolBox, e)) { TheApp.Translation.RemoveTarget(lang.Code); landed = true; }
        if (landed) PlayLanding(e.GetPosition(this));   // 落地才有反馈,落空了没有
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
        var b = new Border { Child = t, Padding = new Thickness(5, 1, 5, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }
}
