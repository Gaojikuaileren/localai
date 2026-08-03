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

    readonly Slider _tone = new() { Minimum = 0, Maximum = 3, TickFrequency = 1, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true };
    readonly ComboBox _lang = new(), _greet = new(), _close = new();
    readonly TextBox _theirName = new(), _theirAddr = new(), _theirContact = new();
    readonly TextBox _myName = new(), _myAddr = new(), _myContact = new(), _signDate = new();

    static readonly string[] MediumNames = { "邮件", "纸质", "消息" };
    static readonly string[] ToneNames = { "朋友", "普通", "正式", "行政" };

    readonly List<Border> _mediumDots = new();
    readonly List<TextBlock> _mediumLabels = new();
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
        var sLeft = new StackPanel();
        sLeft.Children.Add(PointerRow("载体", MediumNames, _mediumDots, _mediumLabels,
                                      i => { if (!_syncing) SaveDoc(d => d.Medium = (ReplyMedium)i); }));
        sLeft.Children.Add(ToneRow());
        Grid.SetColumn(sLeft, 0); speech.Children.Add(sLeft);
        var sRight = new StackPanel();
        sRight.Children.Add(Labeled("语言", _lang));
        sRight.Children.Add(Labeled("问候", _greet));
        sRight.Children.Add(Labeled("祝福", _close));
        Grid.SetColumn(sRight, 2); speech.Children.Add(sRight);

        // ---- 卡 2:对方信息(跟随会话)----
        var them = new StackPanel();
        them.Children.Add(Field("姓名", _theirName, "[对方称呼]"));
        them.Children.Add(Field("地址(只排进纸质)", _theirAddr, "[对方地址]"));
        them.Children.Add(Field("联系(邮箱/手机)", _theirContact, "[对方联系方式]"));

        // ---- 卡 3:我方信息(常驻模板 + 署名日期)----
        var mine = new StackPanel();
        mine.Children.Add(Field("署名", _myName, "[我的署名]"));
        mine.Children.Add(Field("地址", _myAddr, "[我的地址]"));
        mine.Children.Add(Field("联系(邮箱/手机)", _myContact, "[我的联系方式]"));
        mine.Children.Add(Field("署名日期(纸质)", _signDate, "空 = 生成当天"));

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
        var box = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        box.Children.Add(Ui.Caption(label));

        var track = new Grid { Margin = new Thickness(0, 6, 0, 3), Height = 10 };
        for (int i = 0; i < names.Length; i++) track.ColumnDefinitions.Add(new ColumnDefinition());
        // 轨道线:贯穿全宽、恒定灰 —— 它只表示"这些档位在一条线上",不表示进度
        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(6, 0, 6, 0) };
        line.SetResourceReference(Border.BackgroundProperty, "Border");
        Grid.SetColumnSpan(line, names.Length);
        track.Children.Add(line);

        var row = new Grid();
        for (int i = 0; i < names.Length; i++) row.ColumnDefinitions.Add(new ColumnDefinition());

        for (int i = 0; i < names.Length; i++)
        {
            var idx = i;
            var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5),
                                   HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                   Cursor = System.Windows.Input.Cursors.Hand, BorderThickness = new Thickness(1) };
            dot.MouseLeftButtonUp += (_, e) => { e.Handled = true; pick(idx); };
            Grid.SetColumn(dot, i);
            track.Children.Add(dot);
            dots.Add(dot);

            var t = new TextBlock { Text = names[i], TextAlignment = TextAlignment.Center,
                                    Cursor = System.Windows.Input.Cursors.Hand };
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            t.MouseLeftButtonUp += (_, e) => { e.Handled = true; pick(idx); };
            Grid.SetColumn(t, i);
            row.Children.Add(t);
            labels.Add(t);
        }
        box.Children.Add(track);
        box.Children.Add(row);
        return box;
    }

    /// <summary>语气用真滑条:朋友→行政【确实】是由随意到正式的梯度,填充在这儿有意义。</summary>
    FrameworkElement ToneRow()
    {
        var box = new StackPanel();
        box.Children.Add(Ui.Caption("语气"));
        _tone.Margin = new Thickness(0, 2, 0, 1);
        box.Children.Add(_tone);
        var row = new Grid();
        for (int i = 0; i < ToneNames.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var idx = i;
            var t = new TextBlock { Text = ToneNames[i], Cursor = System.Windows.Input.Cursors.Hand,
                                    TextAlignment = i == 0 ? TextAlignment.Left
                                                  : i == ToneNames.Length - 1 ? TextAlignment.Right : TextAlignment.Center };
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            t.MouseLeftButtonUp += (_, e) => { e.Handled = true; _tone.Value = idx; };
            Grid.SetColumn(t, i);
            row.Children.Add(t);
            _toneLabels.Add(t);
        }
        box.Children.Add(row);
        return box;
    }

    static FrameworkElement Labeled(string label, FrameworkElement el)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
        p.Children.Add(Ui.Caption(label));
        el.Margin = new Thickness(0, 1, 0, 0);
        p.Children.Add(el);
        return p;
    }

    /// <summary>带 [方括号] 占位提示的输入框 —— 空着时告诉你这一格会被填成什么。</summary>
    static FrameworkElement Field(string label, TextBox tb, string placeholder)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        p.Children.Add(Ui.Caption(label));
        tb.Padding = new Thickness(5, 2, 5, 2);
        tb.Margin = new Thickness(0, 1, 0, 0);
        var hint = new TextBlock { Text = placeholder, IsHitTestVisible = false,
                                   Margin = new Thickness(7, 3, 0, 0), VerticalAlignment = VerticalAlignment.Center };
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

    void SaveDoc(Action<ReplyDoc> set)
    {
        TheApp.Reply.EnsureSession();
        set(TheApp.Reply.Doc);
        TheApp.Reply.Touch();
    }

    void WireEvents()
    {
        _tone.ValueChanged += (_, _) =>
        {
            HighlightTone((int)_tone.Value);
            if (_syncing) return;
            SaveDoc(d => { d.Tone = (ReplyTone)(int)_tone.Value; d.GreetingIndex = 0; d.ClosingIndex = 0; });
        };
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
        Txt(_theirContact, v => TheApp.Reply.Doc.TheirContact = v, true);
        Txt(_signDate, v => TheApp.Reply.Doc.SignDate = v, true);   // 日期随信,不随模板
        // 我方三项 = 常驻模板:不建会话、跨会话共享
        Txt(_myName, v => TheApp.Reply.Profile.MyName = v, false);
        Txt(_myAddr, v => TheApp.Reply.Profile.MyAddress = v, false);
        Txt(_myContact, v => TheApp.Reply.Profile.MyContact = v, false);
    }

    void HighlightMedium(int sel)
    {
        for (int i = 0; i < _mediumDots.Count; i++)
        {
            var on = i == sel;
            _mediumDots[i].SetResourceReference(Border.BackgroundProperty, on ? "Accent" : "BgSurface");
            _mediumDots[i].SetResourceReference(Border.BorderBrushProperty, on ? "Accent" : "Border");
            _mediumLabels[i].SetResourceReference(TextBlock.ForegroundProperty, on ? "Accent" : "FgMuted");
            _mediumLabels[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    void HighlightTone(int sel)
    {
        for (int i = 0; i < _toneLabels.Count; i++)
        {
            _toneLabels[i].SetResourceReference(TextBlock.ForegroundProperty, i == sel ? "Accent" : "FgMuted");
            _toneLabels[i].FontWeight = i == sel ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    void Refresh()
    {
        var d = TheApp.Reply.Doc;
        var me = TheApp.Reply.Profile;
        _syncing = true;
        try
        {
            HighlightMedium((int)d.Medium);
            _tone.Value = (int)d.Tone; HighlightTone((int)d.Tone);
            for (int i = 0; i < _lang.Items.Count; i++)
                if (_lang.Items[i] is ComboBoxItem { Tag: string c } && c == d.Language) { _lang.SelectedIndex = i; break; }
            void FillCombo(ComboBox cb, string[] items, int sel)
            {
                cb.Items.Clear();
                foreach (var x in items) cb.Items.Add(new ComboBoxItem { Content = x });
                cb.SelectedIndex = Math.Clamp(sel, 0, items.Length - 1);
            }
            FillCombo(_greet, ReplyState.GreetingsFor(d.Tone), d.GreetingIndex);
            FillCombo(_close, ReplyState.ClosingsFor(d.Tone), d.ClosingIndex);
            void Set(TextBox tb, string v) { if (!tb.IsFocused && tb.Text != v) tb.Text = v; }
            Set(_theirName, d.TheirName); Set(_theirAddr, d.TheirAddress); Set(_theirContact, d.TheirContact);
            Set(_signDate, d.SignDate);
            Set(_myName, me.MyName); Set(_myAddr, me.MyAddress); Set(_myContact, me.MyContact);
        }
        finally { _syncing = false; }
    }
}
