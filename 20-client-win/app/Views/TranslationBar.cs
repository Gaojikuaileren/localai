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

    // ★ 190(2026-08-02):窄卡模式下设备列下移成一行之后,「进行中·暂时不会出字」那行
    //   在 176 高里被挤出卡底 —— 那是 D58 的诚实口径,比 14px 的会话区高度重要。
    public const double BarHeight = 190;
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

    // 当前选中的会话由宿主(ChatView)提供 —— 选中的是同传会话时,「开始」在它里面继续,
    //   而不是再建一条(用户裁定 2026-08-02)。null = 宿主没给/没选中。
    readonly Func<ChatSession?>? _currentSession;

    public TranslationBar(Func<ChatSession?>? currentSession = null)
    {
        _currentSession = currentSession;
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

        var pool = PoolCard();            // ★ 只此一处:各模式共用(多语言场景除外 —— 它不限量)
        _poolCard = pool;
        pool.Margin = new Thickness(0, 0, Gap, 0);
        Grid.SetColumn(pool, 1);

        var notes = NotesCard();          // ★ 只此一处
        _notesCardHost = notes;
        _interpretSettings = InterpretSettingsCard();
        _fileTools = FileToolsCard();     // 文件翻译的工具栏(D59)
        var rightStack = new Grid();
        rightStack.Children.Add(notes);
        rightStack.Children.Add(_interpretSettings);
        rightStack.Children.Add(_fileTools);
        Grid.SetColumn(rightStack, 2);

        _i18nBar = I18nBarCard();          // 多语言场景:整条横跨(语言不限量,不用限 3 的池)
        var stack = new Grid();
        stack.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LangPoolWidth + Gap) });
        stack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_i18nBar, 0); Grid.SetColumnSpan(_i18nBar, 3);
        stack.Children.Add(_i18nBar);
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
            TheApp.I18n.Changed += Refresh;   // ★ 漏订阅 = 源码按钮不翻转/抽屉不刷新的根因(2026-08-03)
        };
        // ★ 卸载时必须把拖拽善后掉:界面在拖到一半时被重建的话,鼠标捕获会跟着这个已经不在
        //   可视树上的控件走 —— 那之后整个窗口的点击都到不了别处(与"点不动"同一类事故)。
        Unloaded += (_, _) =>
        {
            TheApp.Translation.Changed -= Refresh;
            TheApp.History.Changed -= Refresh;
            TheApp.Interpret.Changed -= Refresh;
            TheApp.I18n.Changed -= Refresh;
            FinishDrag(null);
        };
    }

    Grid _textLayout = null!;
    FrameworkElement _interpretLayout = null!;

    bool _inRefresh;   // ★ 重入护栏:构建设置卡途中,设备下拉发现存档 id 失效会同步清状态并触发
                       //   Changed -> Refresh 重入,把"我方麦克风/音频输出"整组画两遍(审计 2026-08-02)。

    void Refresh()
    {
        if (_inRefresh) return;
        _inRefresh = true;
        try { RefreshCore(); } finally { _inRefresh = false; }
    }

    void RefreshCore()
    {
        var mode = TheApp.Interpret.Mode;
        var interpreting = mode == TranslationMode.Interpret;
        var filing = mode == TranslationMode.FileTrans;
        // ★ 多语言(D60 补,用户裁定回归常规版式):整条底条换成 语言chips+工具+主动作 的横跨卡
        var i18ing = mode == TranslationMode.I18n;
        _i18nBar.Visibility = i18ing ? Visibility.Visible : Visibility.Collapsed;
        _poolCard.Visibility = i18ing ? Visibility.Collapsed : Visibility.Visible;
        if (i18ing)
        {
            _textLayout.Visibility = Visibility.Collapsed;
            _interpretLayout.Visibility = Visibility.Collapsed;
            _notesCardHost.Visibility = Visibility.Collapsed;
            _interpretSettings.Visibility = Visibility.Collapsed;
            _fileTools.Visibility = Visibility.Collapsed;
            RefreshI18nBar();
            return;
        }
        // 方向卡与语言池:同传/文件翻译共用(用户裁定 2026-08-02:文件翻译的语言选择参考同传)
        _textLayout.Visibility = interpreting || filing ? Visibility.Collapsed : Visibility.Visible;
        _interpretLayout.Visibility = interpreting || filing ? Visibility.Visible : Visibility.Collapsed;

        _notesCardHost.Visibility = interpreting || filing ? Visibility.Collapsed : Visibility.Visible;
        _interpretSettings.Visibility = interpreting ? Visibility.Visible : Visibility.Collapsed;
        _fileTools.Visibility = filing ? Visibility.Visible : Visibility.Collapsed;

        RefreshPools();                       // 语言池各模式都要刷(池里显示什么随模式变)
        if (interpreting) { RefreshDirection(); RefreshInterpretSettings(); return; }
        if (filing) { RefreshDirection(); RefreshFileTools(); return; }
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
        _mySlot.Child = SlotContent(TheApp.Interpret.MyLang, "my");
        _theirSlot.Child = SlotContent(TheApp.Interpret.TheirLang, "their");
    }

    UIElement SlotContent(string code, string which)
    {
        if (Languages.Find(code) is not { } l) return EmptySlot("拖入");
        var card = Bubble(l, selected: true, slot: which);
        card.Margin = new Thickness(0);
        return card;
    }

    // ---------------------------------------------------------------- 同传设置(取代翻译历史那一格)
    // ★ WrapPanel(用户反馈 2026-08-02 第二次):窄窗口时开关【换行】而不是被裁掉 ——
    //   StackPanel 会静默裁掉排在末尾的东西,谁排最后谁消失,那不叫自适应。
    readonly WrapPanel _switchRow = new() { Orientation = Orientation.Horizontal };
    // 开始/结束是这一格的【主动作】,有自己的宿主、排最前 —— 主动作永远不许被裁掉。
    readonly ContentControl _startHost = new() { VerticalAlignment = VerticalAlignment.Center, Focusable = false };
    // ★ DockPanel 而不是 StackPanel(用户截图 2026-08-02):状态文字很长,窄卡上
    //   StackPanel 会按完整宽度硬占,把右上角的「未开始」读数怼得只剩两个字。
    //   现在:灯 Dock.Left、按钮 Dock.Right、文字最后【吃剩余宽度并截断】—— 读数永远完整。
    readonly DockPanel _driverBadge = new() { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>
    /// 同传设置。★ 不给滚动条(用户裁定):一格能装下的东西不该滚 ——
    /// 要滚就说明版面没排好,那不是滚动条能补的。
    /// 版面:标题右边一个【状态灯 + 相应动作】;下方一排上下拨的开关,名字在开关下面,
    /// 从左往右排,右侧【故意留空】给以后加的开关或滑条。
    /// </summary>
    // ★ 窗口缩到最小时这一格也得放得下:设备名很长,给下拉一个上限而不是让它把开关挤出去
    readonly StackPanel _deviceCol = new() { VerticalAlignment = VerticalAlignment.Top };
    readonly TextBlock _latency = new() { VerticalAlignment = VerticalAlignment.Center };

    FrameworkElement InterpretSettingsCard()
    {
        // ★★ 结构是【静态】的(2026-08-02 第三版):
        //   上行 = 开始/结束(主动作,最左)-> 音量仪表 -> 这一场的开关(WrapPanel,窄了换行)
        //   下行 = 两个设备下拉【永远占一整行、并排均分】 -> 进行中的提示
        //   此前设备列在宽卡靠右、窄卡用 SizeChanged 里 SetDock 换到底部 ——
        //   布局途中换 Dock 的时序太脆(离屏渲染画的是换位前的旧布局,真窗口也会闪一帧),
        //   而"设备一整行"在宽卡上同样好看。随尺寸变的只剩两件事:仪表显隐、下拉均分宽度。
        var topRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 0) };

        DockPanel.SetDock(_startHost, Dock.Left);
        topRow.Children.Add(_startHost);

        // 两条竖直音量(对方 / 我方)。★ 仍是空槽:没有数据源就不画会动的假动画 ——
        //   那正好会骗过"声音还在流动吗"这个我们最该诚实回答的问题。
        var meters = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch,
                                      Margin = new Thickness(0, 0, 14, 0) };
        meters.Children.Add(LevelColumn("对方"));
        meters.Children.Add(LevelColumn("我方"));
        DockPanel.SetDock(meters, Dock.Left);
        topRow.Children.Add(meters);

        _switchRow.VerticalAlignment = VerticalAlignment.Top;
        topRow.Children.Add(_switchRow);

        // 设备行:永远一整行,两个下拉并排,宽度按行宽均分(SizeChanged 只调数值,不换结构)
        _deviceCol.Orientation = Orientation.Horizontal;
        _deviceCol.Margin = new Thickness(0, 2, 0, 0);

        var body = new StackPanel();
        body.Children.Add(topRow);
        body.Children.Add(_deviceCol);
        // ★★ 进行中的提示(2026-08-02):「开始」建的是这一场会话,引擎(P4)还没接 ——
        //   不当场写明"暂时不会出字",用户会对着一个安静的面板等半天,以为是坏了。
        _runNote.Margin = new Thickness(0, 3, 0, 0);
        _runNote.SetResourceReference(TextBlock.ForegroundProperty, "RiskWarning");
        _runNote.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        _runNote.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(_runNote);

        body.SizeChanged += (_, e2) =>
        {
            // ★ 窄卡时收起音量仪表:它现在是【空槽】(引擎 P4 未接,没有数据源)——
            //   极窄下它把宽度吃光,两个开关被迫竖叠。引擎接入后这一条要重新权衡。
            meters.Visibility = e2.NewSize.Width < 470 ? Visibility.Collapsed : Visibility.Visible;
            _narrowPickerW = Math.Clamp((e2.NewSize.Width - 12) / 2, 110, 240);
            ApplyDevicePickerWidths();
        };

        // 标题右上角:延迟读数 + 声卡状态灯
        _latency.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        _latency.Margin = new Thickness(0, 0, 10, 0);
        // 状态灯紧跟标题;最右边是延迟读数
        return Card(body, "同传设置", action: _latency, scroll: false, badge: _driverBadge);
    }

    /// <summary>
    /// 【开始同传 / 结束同传】—— 这一格的主动作,摆在「对方实时字幕」右边(用户指定 2026-08-02)。
    ///
    /// ★★ 诚实口径:按下去【真的】会发生三件事 —— 建一条同传会话(会出现在右侧会话列表)、
    ///   解锁这一格的设置、开始计时。它【不】启动引擎:采集/识别/翻译/合成要等 P4。
    ///   所以进行中时旁边那行小字会当场写明"还不会有转写" —— 不写的话,
    ///   用户会对着一个安静的面板等半天,以为是坏了。这条比按钮本身重要。
    /// </summary>
    FrameworkElement StartStopButton(InterpretState st)
    {
        var running = st.Running;
        // ★ 视觉语言收敛到本库自己的按钮语法(用户反馈 2026-08-02 第二次:大红圆饼与周围不统一):
        //   胶囊按钮(RadiusMd,与 Ui.Primary/Danger 同族)+ 左侧一个小字形表达录音语义 ——
        //   未开始 = 描边胶囊 + 红色小圆点(「会录下来」的暗示,但不喧哗);
        //   进行中 = 红色实心胶囊 + 白色小方块(通用的"停止",进行中要一眼找得到怎么停)。
        FrameworkElement glyph;
        if (running)
        {
            var sq = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Center };
            sq.SetResourceReference(Border.BackgroundProperty, "FgOnAccent");
            glyph = sq;
        }
        else
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "RiskDanger");
            glyph = dot;
        }

        var t = new TextBlock
        {
            Text = running ? "结束同传" : "开始同传",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0),
            FontWeight = FontWeights.SemiBold,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, running ? "FgOnAccent" : "FgPrimary");

        var inner = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        inner.Children.Add(glyph);
        inner.Children.Add(t);

        var b = new Border
        {
            Child = inner,
            Padding = new Thickness(13, 7, 13, 7),
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1.2),
        };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        b.SetResourceReference(Border.BorderBrushProperty, "RiskDanger");
        if (running) b.SetResourceReference(Border.BackgroundProperty, "RiskDanger");
        else b.Background = Brushes.Transparent;
        b.MouseEnter += (_, _) => { if (!running) b.SetResourceReference(Border.BackgroundProperty, "BgHover"); };
        b.MouseLeave += (_, _) => { if (!running) b.Background = Brushes.Transparent; };
        b.ToolTip = running
            ? "结束这一场。已经建好的同传记录会保留在右侧会话列表里。"
            : "开始一场同传。\n★ 转写与语音要等引擎接入(P4),这一场暂时不会出字。";
        b.MouseLeftButtonUp += (_, _) => { if (running) StopInterpret(); else StartInterpret(); };
        return b;
    }

    void StartInterpret()
    {
        // ★ 防重复(审计 2026-08-02):事件时序上这颗按钮可能在"已开始"之后仍显示旧态,
        //   再点一次不该再建一条从未运行的空记录。
        if (TheApp.Interpret.Running) return;
        var why = TheApp.Interpret.WhyCannotStart();
        if (why.Length > 0) { ConfirmDialog.Show("还不能开始", why, confirmText: "知道了", cancelText: "关闭"); return; }

        // ★ 当前选中的就是一条同传会话 -> 在【它】里面继续(用户裁定 2026-08-02):
        //   一场会常常中途停一下再继续,每按一次就多一条记录的话,列表里全是碎片。
        //   已删除/幽灵的不算 —— 往回收站里的会话续写等于写进看不见的地方。
        if (_currentSession?.Invoke() is { Interpret: true, DeletedAt: null, Ghost: false } cur)
        {
            TheApp.Interpret.Start(cur.SessionId);
            return;
        }

        // ★ 会话标题带上【方向与时刻】—— 一场会开完,列表里要认得出这是哪一场。
        var mine = Services.Languages.Find(TheApp.Interpret.MyLang)?.Name ?? TheApp.Interpret.MyLang;
        var theirs = Services.Languages.Find(TheApp.Interpret.TheirLang)?.Name ?? TheApp.Interpret.TheirLang;
        var title = $"同传 · {mine}↔{theirs} · {DateTime.Now:M月d日 HH:mm}";
        var sess = TheApp.Chat.NewSession(null, "translation", Services.ProjectScope.Personal, title, interpret: true);
        TheApp.Interpret.Start(sess.SessionId);
    }

    void StopInterpret() => TheApp.Interpret.Stop();

    double _narrowPickerW = 200;   // 每个设备下拉分到的宽度(按行宽均分,不写死)

    /// <summary>两个设备下拉并排、按行宽均分。</summary>
    void ApplyDevicePickerWidths()
    {
        foreach (var c in _deviceCol.Children.OfType<FrameworkElement>())
        {
            c.Width = _narrowPickerW;
            c.Margin = new Thickness(0, 0, 8, 0);
        }
    }

    // ---------------------------------------------------------------- 多语言:底条(D60 补)
    FrameworkElement _i18nBar = null!;
    FrameworkElement _poolCard = null!;
    readonly WrapPanel _i18nLangs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly WrapPanel _i18nTools = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly ContentControl _i18nMainHost = new() { Focusable = false, VerticalAlignment = VerticalAlignment.Center };

    FrameworkElement I18nBarCard()
    {
        // ★ 两张卡分开(用户裁定 2026-08-03):左「多语言设置」只管语言,右「工具」管按钮 ——
        //   目标语言抽屉从底部拉出,【只覆盖左卡】(宽度=左卡)。
        var langsBody = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var row1 = new WrapPanel { Orientation = Orientation.Horizontal };
        row1.Children.Add(_i18nSrcHost);
        row1.Children.Add(_i18nTargetsBtnHost);
        langsBody.Children.Add(row1);
        langsBody.Children.Add(_i18nTargetsSummary);
        var langsCard = Card(langsBody, "多语言设置", scroll: false);

        // ★ 抽屉已拆(D60 六补):语言选择改浮窗(I18nLangPicker),跟着点击锚点走。
        var leftStack = new Grid();
        leftStack.Children.Add(langsCard);

        var toolsBody = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var row2 = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_i18nMainHost, Dock.Right);
        row2.Children.Add(_i18nMainHost);
        _i18nTools.Margin = new Thickness(0, 0, 10, 0);
        row2.Children.Add(_i18nTools);
        toolsBody.Children.Add(row2);
        var tip = Ui.Caption("键与译文在上方网格直接编辑;「JSON 源码」整表直编;「复制 Prompt」给别的 AI 产出同格式 JSON。");
        tip.Margin = new Thickness(0, 4, 0, 0);
        toolsBody.Children.Add(tip);
        var toolsCard = Card(toolsBody, "工具", scroll: false);
        toolsCard.Margin = new Thickness(Gap, 0, 0, 0);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        Grid.SetColumn(leftStack, 0); grid.Children.Add(leftStack);
        Grid.SetColumn(toolsCard, 1); grid.Children.Add(toolsCard);
        return grid;
    }

    readonly ContentControl _i18nSrcHost = new() { Focusable = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    readonly ContentControl _i18nTargetsBtnHost = new() { Focusable = false, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _i18nTargetsSummary = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 4, 0, 0) };
    bool _i18nSrcInit;

    void RefreshI18nBar()
    {
        var st = TheApp.I18n;

        // ---- 源语言(用户裁定 2026-08-03):与目标语言同一个浮窗,单选模式 ——
        //   首次进来还没定源语言时按母语顶上(设置里的母语,没设就按界面语言)。
        if (!_i18nSrcInit)
        {
            _i18nSrcInit = true;
            var native = string.IsNullOrWhiteSpace(TheApp.Settings.NativeLangOverride)
                ? (TheApp.Settings.Language?.StartsWith("en") == true ? "en" : TheApp.Settings.Language?.StartsWith("ja") == true ? "ja" : "zh")
                : TheApp.Settings.NativeLangOverride!;
            if (st.Doc.Entries.Count == 0 && st.Doc.SourceLang == "zh") st.Doc.SourceLang = native;
        }
        var srcName = Services.Languages.Find(st.Doc.SourceLang)?.Name ?? st.Doc.SourceLang;
        _i18nSrcHost.Content = ToolChip($"源:{srcName} ({st.Doc.SourceLang})…", true, () =>
        {
            if (_i18nSrcHost.Content is FrameworkElement fe) I18nLangPicker.Show(fe, forSource: true);
        });

        _i18nTargetsBtnHost.Content = ToolChip($"目标语言({st.Doc.TargetLangs.Count})…", false, () =>
        {
            if (_i18nTargetsBtnHost.Content is FrameworkElement fe) I18nLangPicker.Show(fe);
        });
        _i18nTargetsSummary.Text = st.Doc.TargetLangs.Count == 0
            ? "还没选目标语言 —— 点上面的按钮挑。"
            : string.Join(" · ", st.Doc.TargetLangs);
        _i18nTargetsSummary.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _i18nTargetsSummary.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        // ---- 工具卡
        _i18nTools.Children.Clear();
        // 导入/导出已上移到会话框右上角(用户裁定 2026-08-03)—— 底条只留视图与协作工具
        _i18nTools.Children.Add(ToolChip(st.RawMode ? "预览视图" : "JSON 源码", st.RawMode, () =>
        {
            if (!st.RawMode) { st.RawText = st.ToTableJson(); st.SetRawMode(true); st.SetStatus("源码视图:改完点「预览视图」应用并返回 —— 非法 JSON 会被拒绝,不会吞掉半张表。"); return; }
            var n = st.ImportJson(st.RawText);
            if (n < 0) { st.SetStatus("没有应用:不是合法 JSON(检查逗号/引号/花括号)—— 仍在源码视图。", true); return; }
            st.SetRawMode(false);
            st.SetStatus($"已应用:{n} 条词条。");
        }));
        // ★ 复制 Prompt(用户裁定):给别的 AI 产出同格式 JSON —— 含硬规则与当前整表
        _i18nTools.Children.Add(ToolChip("复制 Prompt", false, () =>
        {
            try { Clipboard.SetText(st.PromptText()); } catch { }
            st.SetStatus("已复制 Prompt(含格式硬规则与当前整表)—— 粘给任何 AI,回来的 JSON 用「JSON 源码」直接贴回。");
        }));

        // 主动作两枚(用户裁定 2026-08-03):缺失项只补空格子;校准整表审校已填的
        var mains = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var calib = ToolChip("翻译校准", false, () =>
            ConfirmDialog.Show("还不能校准",
                "「校准」= 把已填的译文整表过一遍:术语一致、语气统一、占位符完好 —— 只改有问题的。\n翻译引擎(P4)接入后可用。",
                confirmText: "知道了", cancelText: "关闭"));
        calib.Margin = new Thickness(0, 0, 6, 0);
        mains.Children.Add(calib);
        mains.Children.Add(ToolChip("翻译缺失项", true, () =>
            ConfirmDialog.Show("还不能自动翻译",
                "「翻译缺失项」只补空着的格子,不动已填的。\n翻译引擎(P4)还没接入 —— 先「复制 Prompt」粘给别的 AI,回来的 JSON 用「JSON 源码」贴回。",
                confirmText: "知道了", cancelText: "关闭")));
        _i18nMainHost.Content = mains;
    }

    // ---------------------------------------------------------------- 文件翻译:工具栏(D59)
    FrameworkElement _fileTools = null!;
    readonly WrapPanel _fileToolsRow = new() { Orientation = Orientation.Horizontal };
    readonly ContentControl _fileStartHost = new() { Focusable = false };   // 主动作宿主(永不被裁)

    FrameworkElement FileToolsCard()
    {
        // ★ 自适应与排序(裁定 2026-08-02,D59 四补,沿用 D58 的课):
        //   「开始翻译」是这一格的主动作 -> 自己的宿主、Dock.Right 常驻,任何宽度都不被裁;
        //   其余工具在 WrapPanel 里按【动作 -> 整理 -> 偏好】排,窄了换行:
        //   AI 自动标注(用户定死第一位)· 撤回 · 清除所选 · 清空全部 | 实时预览 · 双语对照 · 行政翻译
        // ★ 高度居中(用户反馈 2026-08-03):卡有一整格的高,内容全堆左上看着失衡 ——
        //   工具行+提示行作为整体在卡内垂直居中,与左边方向卡/语言池的视觉重心持平。
        var toolsRow = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_fileStartHost, Dock.Right);
        _fileStartHost.VerticalAlignment = VerticalAlignment.Center;
        toolsRow.Children.Add(_fileStartHost);
        toolsRow.Children.Add(_fileToolsRow);
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _fileToolsRow.VerticalAlignment = VerticalAlignment.Center;
        body.Children.Add(toolsRow);
        var tip = Ui.Caption("左键:画框 / 点框选中 / 按住角标移动 / 拉边调大小(Del 删除,Ctrl+Z 撤回);右键拖拽平移,滚轮缩放。「双语对照」「行政翻译」引擎接入(P4)后生效。");
        tip.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(tip);
        return Card(body, "文件翻译工具", scroll: false);
    }

    void RefreshFileTools()
    {
        var ft = TheApp.FileTrans;
        _fileToolsRow.Children.Clear();

        // ① AI 自动标注 —— 用户指定放第一位。★ 引擎未接入:按下如实说,不做假动作
        _fileToolsRow.Children.Add(ToolChip("AI 自动标注", false, () =>
            ConfirmDialog.Show("还不能自动标注",
                "自动找出该翻译的区域要用视觉模型 —— 翻译引擎(P4)接入后这里一键标完。\n先用「创建标注框」手动圈。",
                confirmText: "知道了", cancelText: "关闭")));

        // ★「创建标注框」按钮已删(用户裁定 2026-08-02):左键在这个板块没有别的用途,
        //   常态就是画框 —— 点框选中/拖角标移动/拉边调大小靠命中判定分流,不用切工具。

        // ③ 撤回(没有可撤的就说,不装聋)
        var curFt = _currentSession?.Invoke();
        var curSid = curFt is { FileTrans: true, DeletedAt: null } ? curFt.SessionId : null;
        _fileToolsRow.Children.Add(ToolChip("撤回", false, () =>
        {
            var sid = curSid;
            if (sid is null || !ft.UndoBox(sid))
                ConfirmDialog.Show("没有可撤回的", "还没画过标注框。", confirmText: "好", cancelText: "关闭");
        }));

        // ④ 实时翻译预览(默认关,用户裁定)
        _fileToolsRow.Children.Add(new ToggleSwitch("实时预览", ft.RealtimePreview,
            on => ft.SetRealtimePreview(on), compact: true));

        // —— 选中了框就给【删除所选】;有框就给【清空】(用户选定:点选删除单个框)
        var curDoc = ft.DocOf(curSid);
        if (ft.SelectedBox is { } selIdx && curDoc is not null && selIdx < curDoc.Boxes.Count)
            _fileToolsRow.Children.Add(ToolChip($"清除所选(框 {selIdx + 1})", false, () =>
            { if (curSid is not null) ft.RemoveBox(curSid, selIdx); }));
        if (curDoc is { Boxes.Count: > 0 })
            _fileToolsRow.Children.Add(ToolChip("清空全部", false, () =>
            {
                if (curSid is not null && ConfirmDialog.Show("清空全部标注框",
                        $"删掉这 {curDoc.Boxes.Count} 个框?", confirmText: "清空", danger: true))
                    ft.ClearBoxes(curSid);
            }));

        // ★ 术语表【不暴露给用户】(用户裁定 2026-08-02 改):给人看太鸡肋 ——
        //   它是 AI 翻译时自己维护、自己参考的内部一致性表,引擎(P4)接入时长在引擎侧。
        _fileToolsRow.Children.Add(new ToggleSwitch("双语对照", ft.BilingualOutput,
            on => ft.SetBilingualOutput(on), compact: true));
        // 普通 vs 行政翻译(用户追加):开 = 公文/证件的正式语体与套语
        _fileToolsRow.Children.Add(new ToggleSwitch("行政翻译", ft.OfficialStyle,
            on => ft.SetOfficialStyle(on), compact: true));

        // 主动作:开始翻译(右侧常驻;实时预览开着就灰 —— 实时模式下没有"开始"这回事)
        var start = ToolChip("开始翻译", true, () =>
            ConfirmDialog.Show("还不能翻译",
                "翻译引擎(P4)还没接入 —— 标注框和文件都会保留,接入后一键出结果。",
                confirmText: "知道了", cancelText: "关闭"));
        start.IsEnabled = !ft.RealtimePreview;
        start.Opacity = ft.RealtimePreview ? 0.45 : 1;
        start.Margin = new Thickness(10, 0, 0, 0);
        _fileStartHost.Content = start;
    }

    /// <summary>工具栏的小胶囊(与 Chip 同族;on = 选中态,给"创建标注框"这类开关用)。</summary>
    FrameworkElement ToolChip(string text, bool on, Action onClick)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, on ? "FgOnAccent" : "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border { Child = t, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 4),
                             Cursor = Cursors.Hand, BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        if (on) { b.SetResourceReference(Border.BackgroundProperty, "Accent"); b.SetResourceReference(Border.BorderBrushProperty, "Accent"); }
        else { b.Background = Brushes.Transparent; b.SetResourceReference(Border.BorderBrushProperty, "Border"); }
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    /// <summary>一条竖直音量 + 底下的名字。空槽 —— 接入采集后才有数据。</summary>
    static FrameworkElement LevelColumn(string who)
    {
        var bar = new Border { Width = 6, VerticalAlignment = VerticalAlignment.Stretch, CornerRadius = new CornerRadius(3) };
        bar.SetResourceReference(Border.BackgroundProperty, "BgSunken");

        var t = Ui.Caption(who);
        t.TextAlignment = TextAlignment.Center;
        t.Margin = new Thickness(0, 5, 0, 0);

        var col = new DockPanel { LastChildFill = true, Width = 28, Margin = new Thickness(0, 0, 4, 0) };
        DockPanel.SetDock(t, Dock.Bottom);
        col.Children.Add(t);
        col.Children.Add(bar);
        return col;
    }

    /// <summary>一个设备下拉(输入/输出)。第一项永远是"跟随系统默认"。</summary>
    FrameworkElement DevicePicker(string label, List<Services.AudioDeviceInfo> devices, string? currentId, Action<string?> pick)
    {
        var box = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        box.Children.Add(Ui.Caption(label));

        if (devices.Count == 0)
        {
            // ★ 读不到就如实说,不摆一个空下拉让人以为机器上没设备
            var t = Ui.Caption("读不到设备列表");
            t.SetResourceReference(TextBlock.ForegroundProperty, "RiskWarning");
            box.Children.Add(t);
            return box;
        }

        // ★ 【上拉】而不是下拉(用户裁定):这两个选择器坐在整个窗口的最底下一格,
        //   往下弹的话列表要么被窗口边缘裁掉,要么盖到任务栏上去 —— 往上弹才有地方展开。
        var cb = new ComboBox { Margin = new Thickness(0, 2, 0, 0) };
        cb.DropDownOpened += (_, _) =>
        {
            if (cb.Template?.FindName("PART_Popup", cb) is System.Windows.Controls.Primitives.Popup pop)
                pop.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        };
        cb.Items.Add(new ComboBoxItem { Content = "跟随系统默认", Tag = "" });
        foreach (var d in devices) cb.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
        // ★ 存的设备现在不可用时,下拉回落到"跟随系统默认",并【把状态里的旧 id 也清掉】(2026-07-31 审计)——
        //   否则界面显示"默认"、状态里却还是那个拔掉的设备,界面和状态说的不是一回事。
        var found = string.IsNullOrEmpty(currentId) ? -1 : devices.FindIndex(d => d.Id == currentId);
        if (!string.IsNullOrEmpty(currentId) && found < 0) pick(null);   // 旧 id 已失效 -> 清成默认
        cb.SelectedIndex = found >= 0 ? found + 1 : 0;
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is ComboBoxItem { Tag: string id }) pick(string.IsNullOrEmpty(id) ? null : id);
        };
        box.Children.Add(cb);
        return box;
    }

    readonly TextBlock _runNote = new();

    void RefreshInterpretSettings()
    {
        var st = TheApp.Interpret;
        _runNote.Visibility = st.Running ? Visibility.Visible : Visibility.Collapsed;
        if (st.Running)
            // ★ 一行放得下的措辞(窄卡 310px 也不换行):关键主张是"暂时不会出字",一个字不能截
            // 开始时刻不再重复 —— 右上角已经写着「已开始 HH:mm」;省下的宽度让这句在最窄的卡上也一行放完
            _runNote.Text = "进行中 · 转写要等引擎接入(P4),暂时不会出字。";
        var drv = Services.AudioDriver.Detect();

        // —— 标题右边:绿 / 黄 / 红 三态灯(用户裁定)
        //    绿 = 已连接 -> 直接显示版本号,没有按钮
        //    黄 = 装了但同传没开启 -> 给「一键开启」
        //    红 = 没找到驱动 -> 给「去设置」
        _driverBadge.Children.Clear();
        var connected = drv.Installed && Services.InterpretState.PipelineReady;
        var (dotKey, text) = !drv.Installed ? ("RiskDanger", "未找到")
                           : connected      ? ("RiskSafe", drv.Version ?? "已连接")
                           // ★ 措辞把账算对(审计 2026-08-02):驱动明明装好了,差的是翻译引擎(P4)。
                           //   写"未开启"会让人去折腾驱动 —— 那儿没有任何可开的东西。
                                            : ("RiskWarning", "已装好 · 翻译引擎未接入(P4)");
        // ★ 光一个彩点看不出它在说什么(用户反馈):把话【写全】——
        //   「· VB-CABLE 声卡驱动状态:未找到」。点只是让状态一眼可扫,不承担表意。
        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dotKey);
        DockPanel.SetDock(dot, Dock.Left);
        _driverBadge.Children.Add(dot);

        if (!drv.Installed)
        {
            var go = Chip("去设置", () => (Application.Current.MainWindow as MainWindow)?.OpenAudioDriverSettings());
            go.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(go, Dock.Right);
            _driverBadge.Children.Add(go);
        }

        // 文字最后加 = 吃剩余宽度;放不下就截断(灯的颜色仍在表意,读数不被牺牲)
        var lab = Ui.Caption("VB-CABLE 声卡驱动状态:" + text);
        lab.VerticalAlignment = VerticalAlignment.Center;
        lab.Margin = new Thickness(6, 0, 0, 0);
        lab.TextTrimming = TextTrimming.CharacterEllipsis;
        lab.TextWrapping = TextWrapping.NoWrap;   // ★ 必须禁换行:Caption 默认可换行,截断只在单行时生效 —— 换行会吃掉下面两行的位置
        lab.SetResourceReference(TextBlock.ForegroundProperty, drv.Installed ? "FgSecondary" : dotKey);
        _driverBadge.Children.Add(lab);
        // ★ 原先这里还有一个「一键开启」chip —— 已删(2026-08-02):
        //   现在开始同传的入口是设置卡里那颗正经按钮(见 StartStopButton),
        //   同一件事不给两个入口;而且那个 chip 只在"装了声卡但引擎没接入"时才出现,
        //   位置和出现条件都让人猜不到它就是"开始"。

        // —— 开关:上下拨,名字在下面,从左往右排;右边留空给以后
        _switchRow.Children.Clear();
        // ★ 没装虚拟声卡时,「实时语音翻译输出」【灰掉禁用】(用户裁定):
        //   译文语音根本送不进会议软件,给一个能拨的开关就是骗人。
        //   去安装的入口只在上面那个状态栏里 —— 同一件事不给两个入口。
        // ★ 只有【我这一侧】有语音输出 —— 对方那侧只出字幕(用户裁定 2026-07-31):
        //   对方的原声一直在响,再叠一层机器声等于两个人同时说话。
        // ★ 设置【随时可点】(用户改主意 2026-08-02,撤销同日"没开始全灰"):
        //   开会前就该把字幕、设备这些调好 —— 边界感由「开始/结束」本身承担,不靠锁设置。
        _switchRow.Children.Add(new ToggleSwitch("我方译文语音", st.SpeakTranslation,
            // ★★ 装了声卡【也不能拨】(审计 2026-07-31):声卡只是必要条件,
            //   真正决定它生不生效的是语音链路(采集/ASR/合成/注入),而那一整套还没接。
            //   只看 drv.Installed 的话,装完 VB-CABLE 的用户会得到一个能拨、会亮、
            //   但什么都不会发生的开关 —— 那正是本项目最该避免的“假开关”。
            on => TheApp.Interpret.SetSpeakTranslation(on),
            // 这一个仍然灰:不是因为"没开始",是驱动/引擎的假开关纪律(见上方审计注释)
            enabled: drv.Installed && InterpretState.PipelineReady, compact: true));
        _switchRow.Children.Add(new ToggleSwitch("对方实时字幕", st.Subtitles,
            on => TheApp.Interpret.SetSubtitles(on), compact: true));
        // ★ 主动作放自己的宿主(最左,见 InterpretSettingsCard 的布局说明)——
        //   不再挤在开关行末尾,窄窗口下被第一个裁掉的就是它(用户反馈 2026-08-02 第二次)。
        _startHost.Content = StartStopButton(st);
        // —— 右侧:设备选择(原来空着的那半边)
        _deviceCol.Children.Clear();
        _deviceCol.Children.Add(DevicePicker("我方麦克风", Services.AudioDevices.Inputs(),
            st.InputDeviceId, id => TheApp.Interpret.SetInputDevice(id)));
        _deviceCol.Children.Add(DevicePicker("音频输出", Services.AudioDevices.Outputs(),
            st.OutputDeviceId, id => TheApp.Interpret.SetOutputDevice(id)));
        ApplyDevicePickerWidths();

        // —— 右上角:实时延迟。★ 没在跑就显示"—",不显示 0.0s ——
        //    一个写着 0.0 的读数会让人以为"零延迟",那是这套系统里最不可能的事。
        // ★ 没装虚拟声卡时,这里不显示"延迟 —"而是直接说【实时翻译输出不可用】——
        //   延迟读数在那种情况下没有意义,而"为什么用不了"才是用户此刻要知道的。
        if (!st.Running)
        {
            // ★ 没开始时不显示"延迟 —" —— 那看着像"在跑但测不出来"。如实说它还没开始。
            _latency.Text = "未开始";
            _latency.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        }
        else if (!drv.Installed)
        {
            _latency.Text = "我方译文语音不可用";
            _latency.SetResourceReference(TextBlock.ForegroundProperty, "RiskWarning");
        }
        else
        {
            _latency.Text = st.LatencySeconds is { } sec ? $"延迟 {sec:0.0}s"
                          : $"已开始 {st.StartedAt:HH:mm}";
            _latency.SetResourceReference(TextBlock.ForegroundProperty,
                st.LatencySeconds is { } v ? (v > 6 ? "RiskWarning" : "FgSecondary") : "FgMuted");
        }
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

        // ★ 池子满了 -> 语言池整体灰掉禁用(拖不动)
        // ★★ 两个场景的"满"是两回事(审计 2026-07-31 抓到的高危):
        //   此前一律看 st.IsFull——那是【文字翻译的目标池】。
        //   于是只要目标池满了 3 个,切到同传后语言池也是灰的,
        //   而同传的方向只能从语言池拖 —— 方向永远设不了,功能直接死掉。
        //   同传的"满"应该是【我说/对方说两个坑都填了】。
        var poolDisabled = TheApp.Interpret.Mode is TranslationMode.Interpret or TranslationMode.FileTrans
            ? TheApp.Interpret.DirectionReady
            : st.IsFull;
        _poolBox.Opacity = poolDisabled ? 0.45 : 1;
        _poolBox.IsHitTestVisible = !poolDisabled;

        // ★★ 两个池子都是【固定的坑】(用户裁定):目标池 3 个、语言池 6 个。
        //   拖进拖出只是往坑里填人/腾空,【排版一动不动】—— 此前是有几个画几个,
        //   于是每拖一次整块都在重排,看着就像界面在抖。

        // 语言池:6 个坑。已在目标池的不在这边重复出现;剩下的坑空着(不写提示,用户裁定),
        // 第一个空坑放「+」当作进设置的入口。
        _poolWrap.Children.Clear();
        // 同传模式下,池里排掉已经放进"我说/对方说"的那两个;文字模式下排掉目标池里的
        var interpreting = TheApp.Interpret.Mode is TranslationMode.Interpret or TranslationMode.FileTrans;   // 文件翻译与同传同一套方向手势
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
    /// <param name="slot">
    /// 不为空 = 这张卡坐在同传的方向坑里("my" / "their"),不属于文字翻译的目标池。
    /// ★ 外形共用、来历不共用 —— 见 BeginDrag 的 fromSlot。
    /// </param>
    Border Bubble(Lang l, bool selected, int stackIndex = 0, string? slot = null)
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
        b.PreviewMouseLeftButtonDown += (_, e) => BeginDrag(b, l, selected && slot is null, e, slot);
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
    string? _dragFromSlot;    // "my" / "their" / null：这一拖是不是从方向坑里出来的

    /// <param name="fromSlot">
    /// 从同传的【我说/对方说】坑里拖出来的。
    /// ★ 必须与 fromTarget 分开(审计 2026-07-31 抓到的高危):
    ///   坑里那张卡和目标池里那张用的是同一个 Bubble(selected: true),
    ///   于是从坑里往外拖会被当成"从目标池拖" ——
    ///   静默删掉【文字翻译目标池】里的同一个语言,而坑里那张还原地不动。
    ///   两个场景共用卡片外形是对的,共用"我从哪里来"是错的。
    /// </param>
    void BeginDrag(Border source, Lang l, bool fromTarget, MouseButtonEventArgs e, string? fromSlot = null)
    {
        // ★ 抓不到鼠标就【别开始拖】:抓不到的话松开事件不一定回到我们身上,
        //   处理器就会一直挂着、状态清不掉。宁可这一下不拖,也不要留个半拖状态。
        if (!CaptureMouse()) return;
        _dragSize = new Size(source.ActualWidth, source.ActualHeight);   // 当场量,保证跟手上那张一样大
        _dragOffset = e.GetPosition(source);                              // 抓在卡片的哪一点上
        _dragLang = l;
        _dragFromTarget = fromTarget;
        _dragFromSlot = fromSlot;
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
            if (_dragFromSlot is not null)
            {
                // 从方向坑里拿起来 -> 清空那个坑,别去碰文字翻译的目标池
                if (_dragFromSlot == "my") TheApp.Interpret.SetMyLang("");
                else TheApp.Interpret.SetTheirLang("");
                RefreshDirection();
            }
            else if (_dragFromTarget) TheApp.Translation.RemoveTarget(_dragLang.Code);
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
        if (TheApp.Interpret.Mode is TranslationMode.Interpret or TranslationMode.FileTrans)
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
                : "你发去翻译的原文会出现在这里,点一条跳回它在会话里的位置。"));
            return;
        }
        foreach (var e in latest) _notesPreview.Children.Add(HistoryBoardView.HistoryRow(e, showTime: false));
    }

    // ---------------------------------------------------------------- 小工具
    static Border Card(UIElement body, string title, Action? gear = null, FrameworkElement? action = null, bool scroll = true,
                       FrameworkElement? badge = null)
    {
        // ★ LastChildFill 随有无 badge 而定(用户截图 2026-08-02):badge 的状态文字很长,
        //   原先它按 Dock.Left 排在读数前面 —— DockPanel 按序分宽,窄卡上右侧的「未开始」
        //   被怼得只剩两个字。现在右侧的 gear/action【先】占位,badge 最后吃剩余并截断。
        var head = new DockPanel { LastChildFill = badge is not null, Margin = new Thickness(0, 0, 0, 6) };
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
        // badge 最后加(吃剩余宽度):状态灯仍紧跟标题读,长文字放不下就截断,不牺牲右侧读数
        if (badge is not null)
        {
            badge.Margin = new Thickness(8, 0, 8, 0);
            badge.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(badge);
        }

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
