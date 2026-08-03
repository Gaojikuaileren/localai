// P3c -- 回信底部三卡(D61 二次重设计,用户裁定 2026-08-03):
//   [用语] 左列:载体(★指针,不是进度条)+ 语气(滑条,它才真有由浅到正式的梯度);
//          右列:语言 / 问候 / 祝福 —— 单独一列,不再挤到出竖向滚动条。
//   [对方信息] 姓名 / 地址 / 联系 —— 跟随会话。
//   [我方信息] 署名 / 地址 / 联系 —— 常驻模板,跨会话共用(署名日期在 ReplyPanel 的生成键左边)。
// ★ 全部用星号宽度 + 紧凑字段:最小窗口(960×540)下不出滚动条、不塌结构。
// ★ 空值显示 [方括号] 占位提示,与 Compose 产出的模板占位一致。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public sealed class ReplyBar : UserControl
{
    /// <summary>回信设置条的高度。★ 比翻译条高一点:我方卡有四个字段,190 装不下会挤出滚动条。</summary>
    public const double BarHeight = 206;

    static App TheApp => (App)Application.Current;
    bool _syncing;

    readonly ComboBox _lang = new(), _greet = new(), _close = new();
    readonly TextBox _theirName = new(), _theirAddr = new(), _theirPostal = new(), _theirContact = new();
    readonly TextBox _myName = new(), _myAddr = new(), _myPostal = new(), _myContact = new();

    // 载体顺序:消息 -> 邮件 -> 信件(用户裁定 2026-08-03,由轻到重)。
    // 界面顺序与枚举值不是一回事 —— 下面这张表把"第几格"翻译成 ReplyMedium。
    static readonly string[] MediumNames = { "消息", "邮件", "信件" };
    static readonly ReplyMedium[] MediumOrder = { ReplyMedium.Message, ReplyMedium.Email, ReplyMedium.Paper };
    static int MediumSlot(ReplyMedium m) => Math.Max(0, Array.IndexOf(MediumOrder, m));
    static readonly string[] ToneNames = { "熟人", "礼貌", "行政" };

    readonly List<Border> _mediumDots = new();
    readonly List<TextBlock> _mediumLabels = new();
    readonly List<Border> _toneDots = new();
    readonly List<TextBlock> _toneLabels = new();

    public ReplyBar()
    {
        foreach (var l in Languages.Catalog)
            _lang.Items.Add(new ComboBoxItem { Content = $"{l.Name} ({l.Code})", Tag = l.Code });

        // ---- 卡 1:用语(两列)----
        // ★ 左边两组指针【占满整卡高度】(用户裁定:不用那么拘谨),右边语言列自己多宽算多宽、
        //   靠左收着 —— 不必跟上面的会话板块对齐宽度。
        var speech = new Grid();
        speech.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        speech.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        // ★ 星号列 + MaxWidth(渲染图 2026-08-03):定宽列在最小窗下装不下,
        //   Grid 直接把右边剪掉 —— 问候/祝福后面那个「+」就没了,自定义根本点不到。
        //   星号列窄时跟着缩,宽时封顶在 158 并靠左 —— 两边都对。
        speech.ColumnDefinitions.Add(new ColumnDefinition { MaxWidth = 158 });   // 封顶在列上:宽了不撑满、窄了跟着缩
        var sLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };
        sLeft.Children.Add(PointerRow("载体", MediumNames, _mediumDots, _mediumLabels,
                                      i => { if (!_syncing) SaveDoc(d => d.Medium = MediumOrder[i]); }));
        sLeft.Children.Add(ToneRow());
        Grid.SetColumn(sLeft, 0); speech.Children.Add(sLeft);
        var sRight = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        sRight.Children.Add(Labeled("语言", _lang));
        sRight.Children.Add(Labeled("问候", WithAdd(_greet, greeting: true)));
        sRight.Children.Add(Labeled("祝福", WithAdd(_close, greeting: false)));
        Grid.SetColumn(sRight, 2); speech.Children.Add(sRight);

        // ★ 字段【均分卡内高度】(用户裁定 2026-08-03):地址是两行所以给两份权重;
        //   底部留 6px 视觉间隔 —— 撑满不等于贴死(用户补充)。
        var them = Fields(
            (Field(_theirName, "对方称呼"), 1),
            (AddressField(_theirAddr, _theirPostal, "对方地址(只排进纸质)", "邮编 + 地区"), 2),
            (Field(_theirContact, "对方联系方式"), 1));

        var mine = Fields(
            (Field(_myName, "我的署名"), 1),
            (AddressField(_myAddr, _myPostal, "我的地址", "邮编 + 地区"), 2),
            (Field(_myContact, "我的联系方式(邮箱/手机)"), 1));

        // ★ 星号宽度:用语要两列所以宽些;两张信息卡窄(用户裁定),窄窗口一起等比压缩
        var grid = new Grid { Height = BarHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.7, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(CardOf("用语", speech, 0, false));
        grid.Children.Add(CardOf("对方信息", them, 1, true));
        grid.Children.Add(CardOf("我方信息", mine, 2, true));   // 不写「· 常驻模板」(用户裁定):它是行为,不必写在标题里
        Content = grid;

        WireEvents();
        Refresh();   // ★ 先画一遍(同 ReplyPanel):离屏渲染诊断不触发 Loaded —— 不先画,图里的指针圆点是没上色的
        Loaded += (_, _) => { TheApp.Reply.Changed -= Refresh; TheApp.Reply.Changed += Refresh; Refresh(); };   // 同 ReplyPanel:防重复订阅
        Unloaded += (_, _) => TheApp.Reply.Changed -= Refresh;
    }

    /// <summary>把若干字段按权重【均分整卡高度】;底部留一点视觉间隔,不贴死。</summary>
    static FrameworkElement Fields(params (FrameworkElement El, int Weight)[] items)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        for (int i = 0; i < items.Length; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(items[i].Weight, GridUnitType.Star) });
            var el = items[i].El;
            el.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetRow(el, i);
            g.Children.Add(el);
        }
        return g;
    }

    static FrameworkElement CardOf(string title, UIElement body, int col, bool gapLeft)
    {
        var inner = new DockPanel { LastChildFill = true };
        var t = Ui.Caption(title);
        t.FontWeight = FontWeights.SemiBold;
        t.Margin = new Thickness(0, 0, 0, 5);
        DockPanel.SetDock(t, Dock.Top);
        inner.Children.Add(t);
        inner.Children.Add(body);
        // ★ 不套 ScrollViewer(用户裁定:不许出现滚动条)—— 内容按最小窗口尺寸算好,装得下
        var card = new Border { Child = inner, Padding = new Thickness(9), BorderThickness = new Thickness(1),
                                Margin = new Thickness(gapLeft ? 8 : 0, 0, 0, 0) };
        card.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        Grid.SetColumn(card, col);
        return card;
    }

    /// <summary>
    /// ★ 指针式选择器(用户裁定 2026-08-03):载体只是三种【方式】,没有高低多少之分 ——
    ///   所以是一条等分刻度轨 + 一个指针圆点,【左右都不填色】,不做进度感。
    ///   全部档名常显,点圆点或点档名都能选;切换只换高亮,排版一格都不动。
    /// </summary>
    FrameworkElement PointerRow(string label, string[] names, List<Border> dots, List<TextBlock> labels, Action<int> pick)
    {
        // 竖直排布(用户裁定 2026-08-03):档名在圆点右边一行一个,横向只占一列宽 ——
        //   横向摆三四个档名太吃宽度,竖着摆省下的宽度留给信息卡。
        var box = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 16, 0) };
        var cap = Ui.Caption(label);
        DockPanel.SetDock(cap, Dock.Top);
        box.Children.Add(cap);
        // ★ 行高用星号:圆点在整卡高度上均分,不再挤在顶上一小撮(用户裁定"不用这么拘谨")
        var body = new Grid { Margin = new Thickness(2, 2, 0, 2) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < names.Length; i++)
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 竖直轨道线:贯穿全部档位、恒定灰 —— 只说明"这些档位在一条线上",不表示进度
        var line = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center,
                                Margin = new Thickness(0, 10, 0, 10) };
        line.SetResourceReference(Border.BackgroundProperty, "Border");
        Grid.SetRowSpan(line, names.Length);
        body.Children.Add(line);

        for (int i = 0; i < names.Length; i++)
        {
            var idx = i;
            var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5),
                                   HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                   IsHitTestVisible = false, BorderThickness = new Thickness(1) };
            Grid.SetRow(dot, i);
            body.Children.Add(dot);
            dots.Add(dot);

            var t = new TextBlock { Text = names[i], Margin = new Thickness(6, 0, 0, 0),
                                    VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            Grid.SetRow(t, i); Grid.SetColumn(t, 1);
            body.Children.Add(t);
            labels.Add(t);

            // ★★ 命中区 = 【整行】的透明盖板(用户反馈 2026-08-03:"现在点击范围太小很难点到")。
            //   圆点才 9px、档名只有几个字,两者之间和右边的空白原本都是死区。
            //   圆点与档名改成不参与命中(IsHitTestVisible=false),点击全交给这块盖板。
            var row = new Border { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
                                   Margin = new Thickness(-2, 0, -4, 0) };
            row.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "BgHover");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; pick(idx); };
            Grid.SetRow(row, i); Grid.SetColumnSpan(row, 2);
            body.Children.Add(row);
        }
        box.Children.Add(body);
        return box;
    }

    /// <summary>语气与载体同款竖直指针 —— 三档之后滑条已无必要,也省下横向空间。</summary>
    FrameworkElement ToneRow()
        => PointerRow("语气", ToneNames, _toneDots, _toneLabels,
                      i => { if (!_syncing) SaveDoc(d => { d.Tone = (ReplyTone)i; d.GreetingIndex = 0; d.ClosingIndex = 0; }); });

    /// <summary>下拉右侧挂一个「+」:自定义问候/祝福(跨会话共用,存 Profile)。</summary>
    FrameworkElement WithAdd(ComboBox cb, bool greeting)
    {
        var row = new DockPanel { LastChildFill = true };
        var plus = new TextBlock { Text = "+", FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 2, 0),
                                   VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand,
                                   ToolTip = greeting ? "自定义问候语" : "自定义祝福语" };
        plus.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        plus.MouseLeftButtonUp += (_, e) => { e.Handled = true; AskCustom(greeting, plus); };
        DockPanel.SetDock(plus, Dock.Right);
        row.Children.Add(plus);
        row.Children.Add(cb);
        return row;
    }

    void AskCustom(bool greeting, FrameworkElement anchor)
    {
        var box = new TextBox { Padding = new Thickness(8, 5, 8, 5), Width = 240 };
        var body = new StackPanel();
        // ★ 这个浮窗【只管加】—— 删除入口在下拉里那条目自己后面(见 CustomAwareItem)。
        //   一件事两个入口 = 两份真相,而且藏在浮窗里的那份用户根本没找到。
        body.Children.Add(Ui.Caption(greeting ? "写一句你自己的问候语:" : "写一句你自己的祝福语:"));
        box.Margin = new Thickness(0, 4, 0, 6);
        body.Children.Add(box);
        var ok = Ui.Primary("加入清单", (_, _) =>
        {
            var st = TheApp.Reply;
            var d = st.Doc;
            var idx = st.AddCustom(greeting, box.Text, d.Language, d.Tone);
            if (idx >= 0)
            {
                if (greeting) d.GreetingIndex = idx; else d.ClosingIndex = idx;
                st.Touch();
            }
            Overlay.CloseActive();
        });
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        body.Children.Add(ok);
        Flyout.Show(anchor, greeting ? "自定义问候" : "自定义祝福", body, width: 280);
        box.Focus();
    }

    static FrameworkElement Labeled(string label, FrameworkElement el)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
        p.Children.Add(Ui.Caption(label));
        el.Margin = new Thickness(0, 1, 0, 0);
        p.Children.Add(el);
        return p;
    }

    /// <summary>
    /// 只有输入框、【不带标题】(用户裁定 2026-08-03):空栏里的占位字样已经说明这一格是什么,
    /// 再加一行标题纯属重复,还白吃一行高度。
    /// </summary>
    static FrameworkElement Field(TextBox tb, string placeholder)
    {
        tb.Padding = new Thickness(5, 3, 5, 3);
        tb.VerticalContentAlignment = VerticalAlignment.Center;   // 栏变高之后字要居中,不能吊在顶上
        var hint = new TextBlock { Text = placeholder, IsHitTestVisible = false,
                                   Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        hint.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        void Sync() => hint.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        tb.TextChanged += (_, _) => Sync();
        Sync();
        // ★ 外面【不再套 StackPanel】:StackPanel 只按内容高度排,给它再多空间也不吃,
        //   字段就永远挤在卡顶上。直接给 Grid,星号行的高度才真落到输入框上。
        var stack = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        stack.Children.Add(tb);
        stack.Children.Add(hint);
        return stack;
    }

    /// <summary>
    /// ★ 地址 = 【融合的两栏】(用户裁定 2026-08-03):上行街道地址、下行【右侧】邮编+地区,
    ///   共用一个边框看着是一格 —— 比让人在单行里敲回车可控:排版时两行各就各位,不靠用户手动断行。
    /// </summary>
    static FrameworkElement AddressField(TextBox line1, TextBox line2, string ph1, string ph2)
    {
        static Grid Cell(TextBox tb, string ph, bool right)
        {
            tb.BorderThickness = new Thickness(0);
            tb.Background = Brushes.Transparent;   // 底色由外层那一个 Border 统一给 —— 两栏才像一格
            tb.Padding = new Thickness(5, 3, 5, 3);
            tb.TextAlignment = right ? TextAlignment.Right : TextAlignment.Left;
            tb.VerticalContentAlignment = VerticalAlignment.Center;
            var hint = new TextBlock { Text = ph, IsHitTestVisible = false,
                                       HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                                       VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 7, 0) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            hint.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            void Sync() => hint.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            tb.TextChanged += (_, _) => Sync();
            Sync();
            var g = new Grid();
            g.Children.Add(tb); g.Children.Add(hint);
            return g;
        }
        var stack = new Grid();
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var c1 = Cell(line1, ph1, right: false);
        var c2 = Cell(line2, ph2, right: false);   // 左对齐:和其余输入栏一致(用户裁定 2026-08-03)
        var sep = new Border { Height = 1, Margin = new Thickness(7, 0, 7, 0), Opacity = 0.6 };
        sep.SetResourceReference(Border.BackgroundProperty, "Border");
        Grid.SetRow(c1, 0); Grid.SetRow(sep, 1); Grid.SetRow(c2, 2);
        stack.Children.Add(c1); stack.Children.Add(sep); stack.Children.Add(c2);
        // ★ 整块一个底色 + 一个圆角(用户反馈 2026-08-03):此前两栏各自透明,
        //   中间那条分隔线把圆角切开,看着像两个独立控件。现在底色连成一片 = 视觉上一格。
        var box = new Border { Child = stack, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 6) };
        box.SetResourceReference(Border.BackgroundProperty, "BgSunken");
        box.SetResourceReference(Border.BorderBrushProperty, "Border");
        box.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        return box;
    }

    // ★ 这里【不建会话】(用户反馈 2026-08-03:"即使对话记录没有任何记录的条例,
    //   也会在会话列表中创建会话记录")—— 设置照样存进草稿档,
    //   等按下「生成」时 EnsureSession 会把整份草稿搬进新会话(见 ReplyState.EnsureSession)。
    void SaveDoc(Action<ReplyDoc> set)
    {
        set(TheApp.Reply.Doc);
        TheApp.Reply.Touch();
    }

    void WireEvents()
    {
        _lang.SelectionChanged += (_, _) => { if (!_syncing && _lang.SelectedItem is ComboBoxItem { Tag: string c }) SaveDoc(d => d.Language = c); };
        _greet.SelectionChanged += (_, _) => { if (!_syncing) SaveDoc(d => d.GreetingIndex = Math.Max(0, _greet.SelectedIndex)); };
        _close.SelectionChanged += (_, _) => { if (!_syncing) SaveDoc(d => d.ClosingIndex = Math.Max(0, _close.SelectedIndex)); };
        void Txt(TextBox tb, Action<string> set)
            => tb.TextChanged += (_, _) =>
            {
                if (_syncing) return;
                set(tb.Text);          // 同上:不建会话,写进草稿档就行
                TheApp.Reply.Touch();
            };
        // ★ Tab 次序接在会话卡那三个之后(10/11/12,见 ReplyPanel)—— 顺序即阅读顺序
        FocusPolicy.SetTabOrder(_theirName, 20);
        FocusPolicy.SetTabOrder(_theirAddr, 21);
        FocusPolicy.SetTabOrder(_theirPostal, 22);
        FocusPolicy.SetTabOrder(_theirContact, 23);
        FocusPolicy.SetTabOrder(_myName, 24);
        FocusPolicy.SetTabOrder(_myAddr, 25);
        FocusPolicy.SetTabOrder(_myPostal, 26);
        FocusPolicy.SetTabOrder(_myContact, 27);

        // 对方四项 = 跟着会话走(存在 Doc 里)
        Txt(_theirName, v => TheApp.Reply.Doc.TheirName = v);
        Txt(_theirAddr, v => TheApp.Reply.Doc.TheirAddress = v);
        Txt(_theirPostal, v => TheApp.Reply.Doc.TheirPostal = v);
        Txt(_theirContact, v => TheApp.Reply.Doc.TheirContact = v);
        // 我方四项 = 常驻模板(存在 Profile 里,跨会话共用)
        Txt(_myName, v => TheApp.Reply.Profile.MyName = v);
        Txt(_myAddr, v => TheApp.Reply.Profile.MyAddress = v);
        Txt(_myPostal, v => TheApp.Reply.Profile.MyPostal = v);
        Txt(_myContact, v => TheApp.Reply.Profile.MyContact = v);
    }

    /// <summary>
    /// 下拉里的一项。★【自定义的那几条后面带一个 ×】(用户裁定 2026-08-03:
    /// "下拉菜单中自定义的字符后面加上一个叉点击删除")——
    /// 删除入口就应该在看得见它的地方,藏在「+」浮窗里等于没有。
    /// 内置条目不给 × —— 那不是用户加的。
    /// </summary>
    ComboBoxItem CustomAwareItem(ComboBox cb, string text, bool greeting)
    {
        if (!TheApp.Reply.IsCustom(greeting, text)) return new ComboBoxItem { Content = text };

        var it = new ComboBoxItem { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var row = new DockPanel { LastChildFill = true };
        var x = new TextBlock { Text = "×", Margin = new Thickness(8, 0, 0, 0), FontWeight = FontWeights.SemiBold,
                                VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand,
                                ToolTip = "删掉这条自定义" };
        x.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        x.MouseEnter += (_, _) => x.SetResourceReference(TextBlock.ForegroundProperty, "RiskDanger");
        x.MouseLeave += (_, _) => x.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        // ★ 用 Preview + Handled:不拦的话这一下会被 ComboBoxItem 当成「选中我」,
        //   变成「想删却选上了」。★★ 删除本身排到下一轮 Dispatcher:
        //   删一删就会 Changed -> Refresh -> cb.Items.Clear(),而此刻正在处理这个 Item 自己的鼠标事件 ——
        //   当场把自己拆了就是又一次「输入期间移树」。
        x.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            cb.IsDropDownOpen = false;
            Dispatcher.BeginInvoke(new Action(() => TheApp.Reply.RemoveCustom(greeting, text)));
        };
        DockPanel.SetDock(x, Dock.Right);
        row.Children.Add(x);
        row.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis,
                                         VerticalAlignment = VerticalAlignment.Center });
        it.Content = row;
        return it;
    }

    void HighlightMedium(int sel) => Paint(_mediumDots, _mediumLabels, sel);

    void HighlightTone(int sel) => Paint(_toneDots, _toneLabels, sel);

    static void Paint(List<Border> dots, List<TextBlock> labels, int sel)
    {
        for (int i = 0; i < dots.Count; i++)
        {
            var on = i == sel;
            dots[i].SetResourceReference(Border.BackgroundProperty, on ? "Accent" : "BgSurface");
            dots[i].SetResourceReference(Border.BorderBrushProperty, on ? "Accent" : "Border");
            labels[i].SetResourceReference(TextBlock.ForegroundProperty, on ? "Accent" : "FgMuted");
            labels[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    void Refresh()
    {
        var d = TheApp.Reply.Doc;
        var me = TheApp.Reply.Profile;
        _syncing = true;
        try
        {
            HighlightMedium(MediumSlot(d.Medium));
            HighlightTone((int)d.Tone);
            // ★ 语言只列【设置里勾的语言池】(用户裁定 2026-08-03)——
            //   回信要用的语言就那几种,整本目录翻起来没意义。池是空的就退回母语一项,不给空下拉。
            var pool = TheApp.Settings.TranslationPool?.Where(x => Languages.Find(x) is not null).ToList() ?? new();
            if (pool.Count == 0) pool.Add(d.Language);
            if (!pool.Contains(d.Language)) pool.Insert(0, d.Language);   // 存的语言不在池里也得看得见
            _lang.Items.Clear();
            foreach (var code in pool)
                _lang.Items.Add(new ComboBoxItem { Content = $"{Languages.Find(code)?.Name ?? code} ({code})", Tag = code });
            for (int i = 0; i < _lang.Items.Count; i++)
                if (_lang.Items[i] is ComboBoxItem { Tag: string c } && c == d.Language) { _lang.SelectedIndex = i; break; }
            void FillCombo(ComboBox cb, string[] items, int sel, bool greeting)
            {
                cb.Items.Clear();
                foreach (var x in items) cb.Items.Add(CustomAwareItem(cb, x, greeting));
                cb.SelectedIndex = Math.Clamp(sel, 0, items.Length - 1);
            }
            // 问候/祝福跟随【语言 + 语气】(用户裁定):换语言就换那门语言的说法
            FillCombo(_greet, TheApp.Reply.Greetings(d.Language, d.Tone), d.GreetingIndex, greeting: true);
            FillCombo(_close, TheApp.Reply.Closings(d.Language, d.Tone), d.ClosingIndex, greeting: false);
            void Set(TextBox tb, string v) { if (!tb.IsFocused && tb.Text != v) tb.Text = v; }
            Set(_theirName, d.TheirName); Set(_theirAddr, d.TheirAddress); Set(_theirPostal, d.TheirPostal); Set(_theirContact, d.TheirContact);
            Set(_myName, me.MyName); Set(_myAddr, me.MyAddress); Set(_myPostal, me.MyPostal); Set(_myContact, me.MyContact);
        }
        finally { _syncing = false; }
    }
}
