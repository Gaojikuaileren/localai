// P3c -- 回信底部三卡(D61 二次重设计,用户裁定 2026-08-03):
//   [用语] 左列:载体(★指针,不是进度条)+ 语气(滑条,它才真有由浅到正式的梯度);
//          右列:语言 / 问候 / 祝福 —— 单独一列,不再挤到出竖向滚动条。
//   [对方信息] 姓名 / 地址 / 联系 —— 跟随会话。
//   [我方信息] 署名 / 地址 / 联系 / 署名日期 —— 常驻模板(日期除外,见 ReplyDoc.SignDate 的说明)。
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
    readonly TextBox _myName = new(), _myAddr = new(), _myPostal = new(), _myContact = new(), _signDate = new();

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
        var speech = new Grid();
        speech.ColumnDefinitions.Add(new ColumnDefinition());
        speech.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        speech.ColumnDefinitions.Add(new ColumnDefinition());
        var sLeft = new StackPanel { Orientation = Orientation.Horizontal };
        sLeft.Children.Add(PointerRow("载体", MediumNames, _mediumDots, _mediumLabels,
                                      i => { if (!_syncing) SaveDoc(d => d.Medium = MediumOrder[i]); }));
        sLeft.Children.Add(ToneRow());
        Grid.SetColumn(sLeft, 0); speech.Children.Add(sLeft);
        var sRight = new StackPanel();
        sRight.Children.Add(Labeled("语言", _lang));
        sRight.Children.Add(Labeled("问候", WithAdd(_greet, greeting: true)));
        sRight.Children.Add(Labeled("祝福", WithAdd(_close, greeting: false)));
        Grid.SetColumn(sRight, 2); speech.Children.Add(sRight);

        // ---- 卡 2:对方信息(跟随会话)----
        var them = new StackPanel();
        them.Children.Add(Field(_theirName, "对方称呼"));
        them.Children.Add(AddressField(_theirAddr, _theirPostal, "对方地址(只排进纸质)", "邮编 + 地区"));
        them.Children.Add(Field(_theirContact, "对方联系方式"));

        // ---- 卡 3:我方信息(常驻模板 + 署名日期)----
        var mine = new StackPanel();
        mine.Children.Add(Field(_myName, "我的署名"));
        mine.Children.Add(AddressField(_myAddr, _myPostal, "我的地址", "邮编 + 地区"));
        mine.Children.Add(Field(_myContact, "我的联系方式(邮箱/手机)"));
        mine.Children.Add(Field(_signDate, "署名日期 · 纸质用,空 = 当天"));

        // ★ 星号宽度:用语要两列所以宽些;两张信息卡窄(用户裁定),窄窗口一起等比压缩
        var grid = new Grid { Height = BarHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.7, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(CardOf("用语", speech, 0, false));
        grid.Children.Add(CardOf("对方信息", them, 1, true));
        grid.Children.Add(CardOf("我方信息 · 常驻模板", mine, 2, true));
        Content = grid;

        WireEvents();
        Loaded += (_, _) => { TheApp.Reply.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => TheApp.Reply.Changed -= Refresh;
    }

    static FrameworkElement CardOf(string title, UIElement body, int col, bool gapLeft)
    {
        var inner = new StackPanel();
        var t = Ui.Caption(title);
        t.FontWeight = FontWeights.SemiBold;
        t.Margin = new Thickness(0, 0, 0, 5);
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
        var box = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        box.Children.Add(Ui.Caption(label));
        var body = new Grid { Margin = new Thickness(2, 3, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < names.Length; i++) body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 竖直轨道线:贯穿全部档位、恒定灰 —— 只说明"这些档位在一条线上",不表示进度
        var line = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center,
                                Margin = new Thickness(0, 8, 0, 8) };
        line.SetResourceReference(Border.BackgroundProperty, "Border");
        Grid.SetRowSpan(line, names.Length);
        body.Children.Add(line);

        for (int i = 0; i < names.Length; i++)
        {
            var idx = i;
            var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5),
                                   HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                   Cursor = System.Windows.Input.Cursors.Hand, BorderThickness = new Thickness(1) };
            dot.MouseLeftButtonUp += (_, e) => { e.Handled = true; pick(idx); };
            Grid.SetRow(dot, i);
            body.Children.Add(dot);
            dots.Add(dot);

            var t = new TextBlock { Text = names[i], Margin = new Thickness(6, 2, 0, 2),
                                    Cursor = System.Windows.Input.Cursors.Hand };
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            t.MouseLeftButtonUp += (_, e) => { e.Handled = true; pick(idx); };
            Grid.SetRow(t, i); Grid.SetColumn(t, 1);
            body.Children.Add(t);
            labels.Add(t);
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
                st.EnsureSession();
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
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
        tb.Padding = new Thickness(5, 3, 5, 3);
        var hint = new TextBlock { Text = placeholder, IsHitTestVisible = false,
                                   Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        hint.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        void Sync() => hint.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        tb.TextChanged += (_, _) => Sync();
        Sync();
        var stack = new Grid();
        stack.Children.Add(tb);
        stack.Children.Add(hint);
        p.Children.Add(stack);
        return p;
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
            tb.Background = Brushes.Transparent;
            tb.Padding = new Thickness(5, 3, 5, 3);
            tb.TextAlignment = right ? TextAlignment.Right : TextAlignment.Left;
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
        var stack = new StackPanel();
        stack.Children.Add(Cell(line1, ph1, right: false));
        var sep = new Border { Height = 1, Margin = new Thickness(5, 0, 5, 0) };
        sep.SetResourceReference(Border.BackgroundProperty, "Border");
        stack.Children.Add(sep);
        stack.Children.Add(Cell(line2, ph2, right: true));
        var box = new Border { Child = stack, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 5) };
        box.SetResourceReference(Border.BorderBrushProperty, "Border");
        box.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        return box;
    }

    void SaveDoc(Action<ReplyDoc> set)
    {
        TheApp.Reply.EnsureSession();
        set(TheApp.Reply.Doc);
        TheApp.Reply.Touch();
    }

    void WireEvents()
    {
        _lang.SelectionChanged += (_, _) => { if (!_syncing && _lang.SelectedItem is ComboBoxItem { Tag: string c }) SaveDoc(d => d.Language = c); };
        _greet.SelectionChanged += (_, _) => { if (!_syncing) SaveDoc(d => d.GreetingIndex = Math.Max(0, _greet.SelectedIndex)); };
        _close.SelectionChanged += (_, _) => { if (!_syncing) SaveDoc(d => d.ClosingIndex = Math.Max(0, _close.SelectedIndex)); };
        void Txt(TextBox tb, Action<string> set, bool session)
            => tb.TextChanged += (_, _) =>
            {
                if (_syncing) return;
                if (session) TheApp.Reply.EnsureSession();
                set(tb.Text);
                TheApp.Reply.Touch();
            };
        Txt(_theirName, v => TheApp.Reply.Doc.TheirName = v, true);
        Txt(_theirAddr, v => TheApp.Reply.Doc.TheirAddress = v, true);
        Txt(_theirPostal, v => TheApp.Reply.Doc.TheirPostal = v, true);
        Txt(_theirContact, v => TheApp.Reply.Doc.TheirContact = v, true);
        Txt(_signDate, v => TheApp.Reply.Doc.SignDate = v, true);   // 日期随信,不随模板
        // 我方三项 = 常驻模板:不建会话、跨会话共享
        Txt(_myName, v => TheApp.Reply.Profile.MyName = v, false);
        Txt(_myAddr, v => TheApp.Reply.Profile.MyAddress = v, false);
        Txt(_myPostal, v => TheApp.Reply.Profile.MyPostal = v, false);
        Txt(_myContact, v => TheApp.Reply.Profile.MyContact = v, false);
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
            void FillCombo(ComboBox cb, string[] items, int sel)
            {
                cb.Items.Clear();
                foreach (var x in items) cb.Items.Add(new ComboBoxItem { Content = x });
                cb.SelectedIndex = Math.Clamp(sel, 0, items.Length - 1);
            }
            // 问候/祝福跟随【语言 + 语气】(用户裁定):换语言就换那门语言的说法
            FillCombo(_greet, TheApp.Reply.Greetings(d.Language, d.Tone), d.GreetingIndex);
            FillCombo(_close, TheApp.Reply.Closings(d.Language, d.Tone), d.ClosingIndex);
            void Set(TextBox tb, string v) { if (!tb.IsFocused && tb.Text != v) tb.Text = v; }
            Set(_theirName, d.TheirName); Set(_theirAddr, d.TheirAddress); Set(_theirPostal, d.TheirPostal); Set(_theirContact, d.TheirContact);
            Set(_signDate, d.SignDate);
            Set(_myName, me.MyName); Set(_myAddr, me.MyAddress); Set(_myPostal, me.MyPostal); Set(_myContact, me.MyContact);
        }
        finally { _syncing = false; }
    }
}
