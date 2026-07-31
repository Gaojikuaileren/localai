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
    const double TargetPoolWidth = 110;
    const double LangPoolWidth = TargetPoolWidth * 2;
    /// <summary>翻译程度那一列的宽度:一条竖滑条 + 四个档位名。</summary>
    const double LevelWidth = 104;
    /// <summary>语言池的坑数(2 列 × 3 行)。</summary>
    const int PoolSlots = 6;
    /// <summary>每个坑的高度 —— 语言卡和空坑必须一样高,否则填进去的瞬间会跳。</summary>
    const double SlotHeight = 30;
    /// <summary>四个板块之间统一的间距。</summary>
    const double Gap = 10;
    /// <summary>板块里预览几条历史。再多要点「全部历史」——这里不滚动、不翻页。</summary>
    const int HistoryPreviewCount = 5;

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

        // ★ 版面:[ 会随模式切换的左半 ][ 翻译历史(两种模式都要,占剩下的) ]
        //   ★★ 翻译历史卡【只建一次】,放在两套版面【之外】。
        //   血的教训:上一版让同传版面又调了一次 NotesCard(),于是历史预览面板被挂到
        //   第二个父节点上 —— WPF 当场抛 InvalidOperationException,整个翻译界面打不开。
        //   这是同一天里第三次栽在"一个元素两个父节点"上(收藏夹、收藏开关、这次),
        //   所以不再打补丁:凡是【两种模式都要显示】的东西,一律建在切换范围之外。
        //
        //   文字翻译:程度【竖排】| 目标池 1×3 | 语言池 2×3(两倍宽,并排才看得出语言从哪搬到哪);
        //   同声传译:换成一对固定方向(我说的语言 -> 对方的语言),程度不适用(同传就是直译)。
        //   三个板块之间间距一律相同:每列比卡片宽出一个 Gap,间隔全落在卡片右侧。
        // ★★ 版面按【谁在两种模式下都要】来分层,而不是按模式各建一套:
        //   [ 左:随模式切换 ][ 语言池:两种模式共用 ][ 右:随模式切换 ]
        //   文字翻译 = 程度 + 目标池 | 语言池 | 翻译历史
        //   同声传译 = 语言方向     | 语言池 | 同传设置
        //   语言池共用不只是省代码 —— 两种模式都要【从它往外拖】,建两份就会撞上
        //   "一个元素两个父节点"(今天栽过三次,最后一次让整个翻译界面打不开)。
        var textLeft = new Grid();
        textLeft.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LevelWidth + Gap) });
        textLeft.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TargetPoolWidth + Gap) });
        var lvl = LevelCard(); Grid.SetColumn(lvl, 0);
        var target = TargetCard(); Grid.SetColumn(target, 1);
        textLeft.Children.Add(lvl); textLeft.Children.Add(target);

        _textLayout = textLeft;
        _interpretLayout = DirectionCard();
        var leftStack = new Grid();
        leftStack.Children.Add(_textLayout);
        leftStack.Children.Add(_interpretLayout);
        Grid.SetColumn(leftStack, 0);

        var pool = PoolCard();            // ★ 只此一处:两种模式共用
        pool.Margin = new Thickness(0, 0, Gap, 0);
        Grid.SetColumn(pool, 1);

        var notes = NotesCard();          // ★ 只此一处
        _notesCardHost = notes;
        _interpretSettings = InterpretSettingsCard();
        var rightStack = new Grid();
        rightStack.Children.Add(notes);
        rightStack.Children.Add(_interpretSettings);
        Grid.SetColumn(rightStack, 2);

        var stack = new Grid();
        stack.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LangPoolWidth + Gap) });
        stack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stack.Children.Add(leftStack);
        stack.Children.Add(pool);
        stack.Children.Add(rightStack);

        var root = new Grid();
        root.Children.Add(stack);
        root.Children.Add(_overlay);
        Content = root;

        Refresh();
        Loaded += (_, _) =>
        {
            TheApp.Translation.Changed += Refresh;
            TheApp.History.Changed += Refresh;
            TheApp.Interpret.Changed += Refresh;
        };
        // ★ 卸载时必须把拖拽善后掉:界面在拖到一半时被重建的话,鼠标捕获会跟着这个已经不在
        //   可视树上的控件走 —— 那之后整个窗口的点击都到不了别处(与"点不动"同一类事故)。
        Unloaded += (_, _) =>
        {
            TheApp.Translation.Changed -= Refresh;
            TheApp.History.Changed -= Refresh;
            TheApp.Interpret.Changed -= Refresh;
            FinishDrag(null);
        };
    }

    Grid _textLayout = null!;
    FrameworkElement _interpretLayout = null!;

    void Refresh()
    {
        var interpreting = TheApp.Interpret.Mode == TranslationMode.Interpret;
        _textLayout.Visibility = interpreting ? Visibility.Collapsed : Visibility.Visible;
        _interpretLayout.Visibility = interpreting ? Visibility.Visible : Visibility.Collapsed;

        _notesCardHost.Visibility = interpreting ? Visibility.Collapsed : Visibility.Visible;
        _interpretSettings.Visibility = interpreting ? Visibility.Visible : Visibility.Collapsed;

        RefreshPools();                       // 语言池两种模式都要刷(池里显示什么随模式变)
        if (interpreting) { RefreshDirection(); RefreshInterpretSettings(); return; }
        RefreshLevel(); RefreshNotes();
    }

    FrameworkElement _interpretSettings = null!;
    FrameworkElement _notesCardHost = null!;

    // ---------------------------------------------------------------- 同传:语言方向(两个坑)
    Border _mySlot = null!, _theirSlot = null!;

    /// <summary>
    /// 语言方向:【我说】与【对方说】两个坑,语言从语言池【拖进来】(用户裁定,不再用下拉)——
    /// 与目标池同一套手势,学一次就够;下拉要先点开、再在一长串里找,是另一种交互。
    /// ★ 方向固定不是偷懒:省掉热路径上的语种检测,既降延迟,也避免半句话被判错语种后整句翻歪。
    /// </summary>
    FrameworkElement DirectionCard()
    {
        _mySlot = new Border { Height = SlotHeight + 4, Margin = new Thickness(0, 3, 0, 0), Background = Brushes.Transparent };
        _theirSlot = new Border { Height = SlotHeight + 4, Margin = new Thickness(0, 3, 0, 0), Background = Brushes.Transparent };

        var swap = Chip("⇅ 对调", () => TheApp.Interpret.SwapLangs());
        swap.HorizontalAlignment = HorizontalAlignment.Left;
        swap.Margin = new Thickness(0, 5, 0, 5);

        var body = new StackPanel();
        body.Children.Add(Ui.Caption("我说"));
        body.Children.Add(_mySlot);
        body.Children.Add(swap);
        body.Children.Add(Ui.Caption("对方说"));
        body.Children.Add(_theirSlot);

        var card = Card(body, "语言方向", scroll: false);
        card.Width = TargetPoolWidth + 22;
        card.HorizontalAlignment = HorizontalAlignment.Left;
        card.Margin = new Thickness(0, 0, Gap, 0);
        return card;
    }

    void RefreshDirection()
    {
        _mySlot.Child = SlotContent(TheApp.Interpret.MyLang);
        _theirSlot.Child = SlotContent(TheApp.Interpret.TheirLang);
    }

    UIElement SlotContent(string code)
    {
        if (Languages.Find(code) is not { } l) return EmptySlot("拖入");
        var card = Bubble(l, selected: true);
        card.Margin = new Thickness(0);
        return card;
    }

    // ---------------------------------------------------------------- 同传设置(取代翻译历史那一格)
    readonly StackPanel _settingsBody = new();

    /// <summary>
    /// 同传模式下右边那一格不是翻译历史,而是【这一场的全部开关】:
    /// 虚拟声卡连接情况、实时翻译输出、字幕、我方设备。
    /// 放这里而不是设置页:开会当下要改的东西,不该让人切走界面去找。
    /// </summary>
    FrameworkElement InterpretSettingsCard() => Card(_settingsBody, "同传设置");

    void RefreshInterpretSettings()
    {
        var st = TheApp.Interpret;
        _settingsBody.Children.Clear();

        // —— 虚拟声卡:装了就自动认出来,没装才出现入口
        var drv = Services.AudioDriver.Detect();
        var drvLine = Ui.Caption(drv.Installed
            ? $"虚拟声卡:已连接 · {drv.Version ?? "版本未知"}"
            : "虚拟声卡:未安装 —— 译文语音送不进会议软件");
        drvLine.SetResourceReference(TextBlock.ForegroundProperty, drv.Installed ? "FgSecondary" : "RiskWarning");
        _settingsBody.Children.Add(drvLine);
        if (!drv.Installed)
        {
            var go = Chip("去安装", () => (Application.Current.MainWindow as MainWindow)?.OpenAudioDriverSettings());
            go.HorizontalAlignment = HorizontalAlignment.Left;
            go.Margin = new Thickness(0, 4, 0, 0);
            _settingsBody.Children.Add(go);
        }

        _settingsBody.Children.Add(new Border { Height = 8 });
        _settingsBody.Children.Add(Toggle("实时翻译输出", st.SpeakTranslation,
            () => TheApp.Interpret.SetSpeakTranslation(!st.SpeakTranslation)));
        _settingsBody.Children.Add(Toggle("对方字幕", st.Subtitles,
            () => TheApp.Interpret.SetSubtitles(!st.Subtitles)));

        _settingsBody.Children.Add(new Border { Height = 8 });
        _settingsBody.Children.Add(Ui.Caption("我方设备"));
        _settingsBody.Children.Add(Ui.Caption("· 输入:系统默认麦克风"));
        _settingsBody.Children.Add(Ui.Caption(drv.Installed
            ? "· 输出:虚拟声卡(会议软件里把麦克风选成 CABLE Output)"
            : "· 输出:尚无 —— 需要先装虚拟声卡"));
        // ★ 诚实:设备可选列表要等语音链路接入才有意义,在那之前不摆能点却不生效的下拉。
        _settingsBody.Children.Add(Ui.Caption("★ 可选设备列表等语音链路接入后开放。"));
    }

    /// <summary>一行开关:左边名字、右边状态。开着用着重色实心,一眼看得出。</summary>
    FrameworkElement Toggle(string label, bool on, Action onClick)
    {
        var chip = Chip(on ? "开" : "关", onClick, on);
        chip.VerticalAlignment = VerticalAlignment.Center;
        var t = Ui.Caption(label);
        t.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 5) };
        DockPanel.SetDock(chip, Dock.Right);
        row.Children.Add(chip);
        row.Children.Add(t);
        return row;
    }

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
            cell.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (!TextShapes.Allows(TheApp.Translation.Shape, capturedLevel)) return;   // 灰着的点不动
                TheApp.Translation.SetLevel(capturedLevel);
            };
            Grid.SetRow(cell, i);
            rows.Children.Add(cell);
            _levelLabels.Add((level, t));
        }

        _shapeNote.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _shapeNote.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var picker = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_levelSlider, Dock.Left);
        picker.Children.Add(_levelSlider);
        picker.Children.Add(rows);

        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_shapeNote, Dock.Bottom);
        body.Children.Add(_shapeNote);
        body.Children.Add(picker);

        var card = Card(body, "翻译程度", scroll: false);
        // ★ 滚轮可调档(用户裁定)。收在【整块卡片】上而不是滑条本身 ——
        //   滑条只有一条细轨道,要求用户先对准它再滚,不如整块都认。
        //   ★ 必须 e.Handled = true:不然滚轮会继续往上冒泡,把外面的会话区一起滚了。
        card.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            var lv = (int)TheApp.Translation.Level;
            var max = TranslationLevels.All.Length - 1;
            // 竖排是【下简上详】:往上滚 = 更详细
            var next = Math.Clamp(lv + (e.Delta > 0 ? 1 : -1), 0, max);
            // 滚到灰着的档就停住 —— 不要滚过去又被弹回来,那种手感像卡住了
            if (!TextShapes.Allows(TheApp.Translation.Shape, (TranslationLevel)next)) return;
            if (next != lv) TheApp.Translation.SetLevel((TranslationLevel)next);
        };
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

    /// <summary>长文本时如实说明这次按什么来(不闷声改档)。</summary>
    readonly TextBlock _shapeNote = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    void RefreshLevel()
    {
        var st = TheApp.Translation;
        var lv = st.Level;
        if ((int)_levelSlider.Value != (int)lv) _levelSlider.Value = (int)lv;
        foreach (var (level, label) in _levelLabels)
        {
            // 第二阶的名字跟着目标池走(全英德 -> 词根;全中日韩 -> 读音;混着 -> 词解)
            label.Text = LabelFor(level);
            // ★ 长文本下语法/例句不可用 —— 【当场灰掉】,而不是等按了发送才回退。
            //   逐句讲语法、给整篇另造例句,都是"做得出但没人要"的东西。
            var ok = TextShapes.Allows(st.Shape, level);
            label.Opacity = ok ? 1 : 0.35;
            if (label.Parent is Border cell) cell.Cursor = ok ? Cursors.Hand : Cursors.Arrow;
        }
        _shapeNote.Text = TextShapes.Explain(lv, st.Shape) ?? "";
        _shapeNote.Visibility = _shapeNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        // ★ 不再要齿轮:进设置的入口改成【空坑里的 +】(用户裁定)——
        //   加语言这件事本来就发生在"还有空位"的时候,把入口放在空位上比放在标题角上更顺手。
        //   只有一种情况坑全满、放不下 +:那时退回标题角上的小 + (下面 RefreshPools 里处理)。
        _poolBox = Card(_poolWrap, "语言池", action: _poolHeaderAdd);
        _poolBox.Width = LangPoolWidth;
        _poolBox.HorizontalAlignment = HorizontalAlignment.Left;
        return _poolBox;
    }

    /// <summary>坑全满时退到标题角上的「+」。平时藏起来。</summary>
    // 坑全满时退到标题角上的「+」。平时 Collapsed。
    readonly FrameworkElement _poolHeaderAdd = Chip("+", OpenPoolSettings);

    /// <summary>
    /// 一个【空坑】。虚线描边、浅色,一眼看出"这里可以放一个语言"。
    /// hint 为空 = 不写字(语言池的空坑不需要提示,用户裁定)。
    /// </summary>
    Border EmptySlot(string? hint = null, Action? onClick = null)
    {
        var box = new Border
        {
            Height = SlotHeight,
            Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            // 虚线:与"实心的语言卡"一眼可分 —— 这是坑,不是内容
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            Background = System.Windows.Media.Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        box.SetResourceReference(Border.BorderBrushProperty, "Border");
        box.Opacity = 0.55;

        if (hint is { Length: > 0 })
        {
            var t = new TextBlock
            {
                Text = hint,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            box.Child = t;
        }

        if (onClick is not null)
        {
            box.Cursor = Cursors.Hand;
            box.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
            box.MouseEnter += (_, _) => box.Opacity = 1;
            box.MouseLeave += (_, _) => box.Opacity = 0.55;
        }
        return box;
    }

    /// <summary>空坑里的「+」:点它去设置里增删语言池。</summary>
    Border AddSlot()
    {
        var slot = EmptySlot("+", OpenPoolSettings);
        if (slot.Child is TextBlock t) t.FontSize = 15;
        slot.Opacity = 0.7;
        return slot;
    }

    static void OpenPoolSettings()
        => (Application.Current.MainWindow as MainWindow)?.OpenLanguagePoolSettings();

    void RefreshPools()
    {
        var st = TheApp.Translation;

        // ★ 目标池满了 -> 语言池整体灰掉禁用(拖不动)
        var poolDisabled = st.IsFull;
        _poolBox.Opacity = poolDisabled ? 0.45 : 1;
        _poolBox.IsHitTestVisible = !poolDisabled;

        // ★★ 两个池子都是【固定的坑】(用户裁定):目标池 3 个、语言池 6 个。
        //   拖进拖出只是往坑里填人/腾空,【排版一动不动】—— 此前是有几个画几个,
        //   于是每拖一次整块都在重排,看着就像界面在抖。

        // 语言池:6 个坑。已在目标池的不在这边重复出现;剩下的坑空着(不写提示,用户裁定),
        // 第一个空坑放「+」当作进设置的入口。
        _poolWrap.Children.Clear();
        // 同传模式下,池里排掉已经放进"我说/对方说"的那两个;文字模式下排掉目标池里的
        var interpreting = TheApp.Interpret.Mode == TranslationMode.Interpret;
        var used = interpreting
            ? new[] { TheApp.Interpret.MyLang, TheApp.Interpret.TheirLang }
            : st.Targets.ToArray();
        var avail = TheApp.Settings.TranslationPool
            .Where(c => !used.Contains(c) && c != _liftedFromPool && Languages.Find(c) is not null)
            .Take(PoolSlots)
            .ToList();
        for (int i = 0; i < PoolSlots; i++)
        {
            if (i < avail.Count)
                _poolWrap.Children.Add(Bubble(Languages.Find(avail[i])!, selected: false, stackIndex: i));
            else if (i == avail.Count)
                _poolWrap.Children.Add(AddSlot());          // 第一个空坑 = 加语言的入口
            else
                _poolWrap.Children.Add(EmptySlot());        // 其余空坑:纯占位,不写字
        }
        // 坑全满时 + 没地方放 -> 退回标题角上那个
        _poolHeaderAdd.Visibility = avail.Count >= PoolSlots ? Visibility.Visible : Visibility.Collapsed;

        // 目标池:3 个坑,空坑写一个浅色的「拖入」
        _targetWrap.Children.Clear();
        for (int i = 0; i < Languages.MaxTargets; i++)
        {
            if (i < st.Targets.Count && Languages.Find(st.Targets[i]) is { } l)
                _targetWrap.Children.Add(Bubble(l, selected: true, stackIndex: i));
            else
                _targetWrap.Children.Add(EmptySlot("拖入"));
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
            Padding = new Thickness(8, 5, 8, 5),
            // 标签在格子里【拉伸填满】(1×3 / 2×3 是真的格子)
            Margin = new Thickness(0, 0, 6, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // ★ 固定高度 + 顶对齐:UniformGrid 会把每个格子撑满可用高度,
            //   不钉住的话标签会被拉成一根高条(渲染诊断里当场看到)。
            VerticalAlignment = VerticalAlignment.Top,
            Height = SlotHeight,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };
        b.SetResourceReference(Border.BackgroundProperty, selected ? "Accent" : "BgSurface");
        b.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "Border");

        b.Tag = l.Code;   // 重排后靠它认出"这还是同一张卡"(FLIP 动画要配对前后位置)
        b.PreviewMouseLeftButtonDown += (_, e) => BeginDrag(b, l, selected, e);
        return b;
    }

    /// <summary>
    /// 落地:在落点扬起一小撮灰 + 一声闷响。
    /// ★ 不按皮肤分(用户裁定砍掉堆叠卡片之后):落地反馈是【交互反馈】,不是视觉身份 ——
    ///   身份走颜色令牌,交互手感三套皮肤应当一致。声音在设置里可单独关。
    /// </summary>
    void PlayLanding(Point at)
    {
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
    /// <summary>这次是从目标池拿起来的(落空要放回目标池)。</summary>
    bool _liftedFromTarget;
    /// <summary>正被拿在手上的语言池语言 —— 拖动期间它不出现在语言池里(已经在你手上了)。</summary>
    string? _liftedFromPool;
    Size _dragSize;
    Point _dragOffset;
    bool _dragging;
    Point _dragStart;

    void BeginDrag(Border source, Lang l, bool fromTarget, MouseButtonEventArgs e)
    {
        // ★ 抓不到鼠标就【别开始拖】:抓不到的话松开事件不一定回到我们身上,
        //   处理器就会一直挂着、状态清不掉。宁可这一下不拖,也不要留个半拖状态。
        if (!CaptureMouse()) return;
        _dragSize = new Size(source.ActualWidth, source.ActualHeight);   // 当场量,保证跟手上那张一样大
        _dragOffset = e.GetPosition(source);                              // 抓在卡片的哪一点上
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

            // ★ 一旦真的开始拖,就把这个语言从【原板块】里拿掉,其余语言补位(用户裁定)。
            //   手上那张卡才真是"被拿起来的那一张" —— 否则同一个语言会同时出现在两个地方,
            //   看着像复制而不是搬运。落空了在 FinishDrag 里原样放回去。
            _liftedFromTarget = _dragFromTarget;
            if (_dragFromTarget) TheApp.Translation.RemoveTarget(_dragLang.Code);
            else { _liftedFromPool = _dragLang.Code; RefreshPools(); }

            // ★ 手上那张卡要和坑里那张【一模一样大】(用户裁定):
            //   尺寸从被拿起的那个元素当场量,不是重新算一个 —— 差一点点就会露馅,
            //   看着像另生成了一张卡,而不是我把这张拿起来了。
            _ghost = Bubble(_dragLang, _dragFromTarget);
            _ghost.Width = _dragSize.Width;
            _ghost.Height = _dragSize.Height;
            _ghost.Margin = new Thickness(0);
            _ghost.HorizontalAlignment = HorizontalAlignment.Left;
            _ghost.VerticalAlignment = VerticalAlignment.Top;
            _ghost.Opacity = 0.9;
            _ghost.IsHitTestVisible = false;
            _overlay.Children.Add(_ghost);
        }
        if (_ghost is null) return;
        // 按【抓取时手指落在卡片上的那一点】偏移 —— 卡片不会在手里跳一下位置
        Canvas.SetLeft(_ghost, p.X - _dragOffset.X);
        Canvas.SetTop(_ghost, p.Y - _dragOffset.Y);

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

        // ★ 幽灵先不撤 —— 它还要飞到目的地(用户裁定:松手不能秒跳过去)
        var ghost = _ghost;
        _ghost = null;

        // 动之前记下每张卡在哪(FLIP 的 F:First)
        var before = SnapshotCards();

        // 没真正拖起来(只是点了一下),或者拖到一半失去捕获 -> 原样还回去,不能把语言弄丢
        if (lang is null || !wasDragging || e is null)
        {
            if (lang is not null && wasDragging && _liftedFromTarget) TheApp.Translation.AddTarget(lang.Code);
            _liftedFromPool = null;
            RefreshPools();
            if (ghost is not null) _overlay.Children.Remove(ghost);
            return;
        }

        // 拖起来的那一刻已经把它从原板块摘掉了,所以这里只决定【放到哪】:
        //   · 落在目标池 -> 放进去;
        //   · 落在语言池,或者落空 -> 回到语言池(= 不在目标池里,自然就在语言池里)。
        // 语言池是"目标池之外的全部",不需要显式塞回去 —— 清掉暂借标记就恢复了。
        var landed = false;
        if (TheApp.Interpret.Mode == TranslationMode.Interpret)
        {
            // 同传:落进哪个坑就设哪个方向。被顶替的那个语言自动回到语言池(池 = 池减去已选的两个)。
            if (Hit(_mySlot, e)) { TheApp.Interpret.SetMyLang(lang.Code); landed = true; }
            else if (Hit(_theirSlot, e)) { TheApp.Interpret.SetTheirLang(lang.Code); landed = true; }
        }
        else
        {
            var toTarget = Hit(_targetBox, e);
            if (toTarget) landed = TheApp.Translation.AddTarget(lang.Code);
            else if (fromTarget && Hit(_poolBox, e)) landed = true;   // 目标池 -> 语言池:摘掉即完成
        }

        _liftedFromPool = null;                                   // 暂借结束,语言池恢复完整
        RefreshPools();

        var dropAt = e.GetPosition(this);
        var soundOnArrive = landed;
        // 排完版才知道各张卡的新位置(FLIP 的 L:Last)
        Dispatcher.BeginInvoke(new Action(() => AfterReflow(before, lang.Code, ghost, dropAt, soundOnArrive)),
                               System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>记下当前每张语言卡的位置(相对本控件)。key = 语言码。</summary>
    Dictionary<string, Point> SnapshotCards()
    {
        var map = new Dictionary<string, Point>();
        foreach (var (code, el) in Cards())
        {
            if (!el.IsArrangeValid || el.ActualWidth <= 0) continue;
            map[code] = el.TranslatePoint(new Point(0, 0), this);
        }
        return map;
    }

    IEnumerable<(string Code, Border El)> Cards()
    {
        foreach (Panel wrap in new Panel[] { _targetWrap, _poolWrap })
            foreach (var child in wrap.Children)
                if (child is Border { Tag: string code } b) yield return (code, b);
    }

    /// <summary>
    /// 重排之后统一做动画:
    ///   · 被挤动的卡片 —— 先用差值推回旧位置,再动画归零(看起来就是滑过去的);
    ///   · 手上那张 —— 幽灵从松手处飞到它的新坑里,到位了才把真卡显出来、才响那一声。
    /// ★ 落点是"新坑的位置",不是松手的位置 —— 所以必须等排完版才算得出来。
    /// </summary>
    void AfterReflow(Dictionary<string, Point> before, string landedCode, Border? ghost, Point dropAt, bool playSound)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(200);

        Border? landedCard = null;
        foreach (var (code, el) in Cards())
        {
            var now = el.TranslatePoint(new Point(0, 0), this);
            if (code == landedCode) { landedCard = el; continue; }
            if (!before.TryGetValue(code, out var was)) continue;
            var dx = was.X - now.X;
            var dy = was.Y - now.Y;
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) continue;

            var tt = new TranslateTransform(dx, dy);
            el.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, dur) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, dur) { EasingFunction = ease });
        }

        if (ghost is null) return;

        // 目的地:落进去了就飞到那张新卡的位置;没落进去(拖回语言池/落空)也飞到它现在所在的坑
        var dest = landedCard is not null
            ? landedCard.TranslatePoint(new Point(0, 0), this)
            : new Point(Canvas.GetLeft(ghost), Canvas.GetTop(ghost));

        if (landedCard is not null) landedCard.Opacity = 0;   // 飞到之前先藏起来,免得同时看到两张

        var fromX = Canvas.GetLeft(ghost);
        var fromY = Canvas.GetTop(ghost);
        var ax = new DoubleAnimation(fromX, dest.X, dur) { EasingFunction = ease };
        var ay = new DoubleAnimation(fromY, dest.Y, dur) { EasingFunction = ease };
        ay.Completed += (_, _) =>
        {
            _overlay.Children.Remove(ghost);
            if (landedCard is not null) landedCard.Opacity = 1;
            if (playSound) PlayLanding(dest);                  // ★ 到位才响,不是松手就响
        };
        ghost.BeginAnimation(Canvas.LeftProperty, ax);
        ghost.BeginAnimation(Canvas.TopProperty, ay);
    }

    // ---------------------------------------------------------------- 翻译历史(预览)
    // ★ 历史不另存原文,它是会话消息的一个【视图】(见 Services/TranslationHistory)——
    //   原文已经在会话里,再存一份就有两份真相,删了会话历史还在,迟早对不上。
    bool _favoritesOnly;

    FrameworkElement NotesCard()
    {
        // 标题边上的星:切换"只看收藏"。右上角「全部历史」拉开抽屉看完整列表。
        var star = Chip("★", () =>
        {
            _favoritesOnly = !_favoritesOnly;
            RebuildNotesCard();
        }, on: _favoritesOnly);
        star.ToolTip = _favoritesOnly ? "显示全部历史" : "只看收藏";
        var all = Chip("全部历史", () => (Application.Current.MainWindow as MainWindow)?.OpenSideDrawer(
            "全部历史", new HistoryBoardView(), IconName.Translation));
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(star);
        actions.Children.Add(new Border { Width = 6 });
        actions.Children.Add(all);

        var card = Card(_notesPreview, "翻译历史", action: actions, scroll: false);
        // 间距统一由列宽给(见构造函数的 Gap),这里不再自带边距,否则会叠加成两倍
        _notesHost.Content = card;
        return _notesHost;
    }

    /// <summary>
    /// 重建历史卡片的外壳。★ 只为了让「只看收藏」那个星按钮换成着重色实心 ——
    /// 按钮的开/关是【建的时候定的】,不重建就换不了样子。
    /// 重建时先把预览面板从旧卡片里摘出来,否则会撞上一个元素不能有两个父节点。
    /// </summary>
    void RebuildNotesCard()
    {
        if (_notesPreview.Parent is Panel oldParent) oldParent.Children.Remove(_notesPreview);
        else if (_notesPreview.Parent is Decorator dec) dec.Child = null;
        else if (_notesPreview.Parent is ContentControl cc) cc.Content = null;
        NotesCard();      // 内部会把新卡片塞进 _notesHost
        RefreshNotes();
    }

    readonly ContentControl _notesHost = new();

    void RefreshNotes()
    {
        _notesPreview.Children.Clear();
        // ★ 板块里最多五条,不滚动不翻页(用户裁定)——要看更多点「全部历史」。
        //   下半条高度是固定的,塞进滚动区只会挤成一条缝。
        var latest = TheApp.History.Latest(HistoryPreviewCount, _favoritesOnly);
        if (latest.Count == 0)
        {
            _notesPreview.Children.Add(Ui.Caption(_favoritesOnly
                ? "还没有收藏 —— 在「全部历史」里点每条后面的星。"
                : "翻过的内容会直接出现在这里,点一条跳回它在会话里的位置。"));
            return;
        }
        foreach (var e in latest) _notesPreview.Children.Add(HistoryBoardView.HistoryRow(e, showTime: false));
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

    /// <summary>
    /// 小按钮。★ on=true 表示【这个开关正开着】—— 用着重色实心填充,
    /// 一眼能和没开分清(用户反馈:只看收藏开没开看不出来)。
    /// </summary>
    static FrameworkElement Chip(string text, Action onClick, bool on = false)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, on ? "FgOnAccent" : "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border { Child = t, Padding = new Thickness(5, 1, 5, 1), Cursor = Cursors.Hand, BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        if (on)
        {
            b.SetResourceReference(Border.BackgroundProperty, "Accent");
            b.SetResourceReference(Border.BorderBrushProperty, "Accent");
        }
        else
        {
            b.Background = Brushes.Transparent;
            b.SetResourceReference(Border.BorderBrushProperty, "Border");
            b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        }
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }
}
