// P3c -- 回信底部三卡(D61 重设计,用户裁定 2026-08-03):
//   [用语] 载体/语气(指针式滑条,全档刻度常显,不因滑动改排版)· 语言 · 问候 · 祝福
//   [对方信息] 姓名/地址/联系/署名日期 —— 跟随会话
//   [我方信息] 常驻模板,跨会话共享,很少修改
//   ★ 生成按钮不在这儿 —— 在上方「生成结果」板块(用户裁定)。

using System.Windows;
using System.Windows.Controls;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public sealed class ReplyBar : UserControl
{
    static App TheApp => (App)Application.Current;
    bool _syncing;

    readonly Slider _medium = new() { Minimum = 0, Maximum = 2, TickFrequency = 1, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true };
    readonly Slider _tone = new() { Minimum = 0, Maximum = 3, TickFrequency = 1, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true };
    readonly StackPanel _mediumTicks = new() { Orientation = Orientation.Horizontal };
    readonly StackPanel _toneTicks = new() { Orientation = Orientation.Horizontal };
    readonly ComboBox _lang = new() { Width = 148 };
    readonly ComboBox _greet = new() { Width = 148 };
    readonly ComboBox _close = new() { Width = 148 };
    readonly TextBox _theirName = new(), _theirAddr = new(), _theirContact = new(), _signDate = new();
    readonly TextBox _myName = new(), _myAddr = new(), _myContact = new();

    static readonly string[] MediumNames = { "邮件", "纸质", "消息" };
    static readonly string[] ToneNames = { "朋友", "普通", "正式", "行政" };

    public ReplyBar()
    {
        foreach (var l in Languages.Catalog)
            _lang.Items.Add(new ComboBoxItem { Content = $"{l.Name} ({l.Code})", Tag = l.Code });

        // ---- 卡 1:用语 ----
        var speech = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        speech.Children.Add(PointerSlider("载体", _medium, _mediumTicks, MediumNames));
        speech.Children.Add(PointerSlider("语气", _tone, _toneTicks, ToneNames));
        speech.Children.Add(LabeledBox("语言", _lang));
        speech.Children.Add(LabeledBox("问候", _greet));
        speech.Children.Add(LabeledBox("祝福", _close));

        // ---- 卡 2:对方信息(跟随会话)----
        var them = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        them.Children.Add(Field("姓名", _theirName));
        them.Children.Add(Field("地址(只排进纸质)", _theirAddr));
        them.Children.Add(Field("联系(邮箱/手机)", _theirContact));
        them.Children.Add(Field("署名日期(纸质;空=当天)", _signDate));

        // ---- 卡 3:我方信息(常驻模板)----
        var mine = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        mine.Children.Add(Field("署名", _myName));
        mine.Children.Add(Field("地址", _myAddr));
        mine.Children.Add(Field("联系(邮箱/手机)", _myContact));
        var mineNote = Ui.Caption("常驻模板:所有回信共用,改一次处处生效。");
        mineNote.TextWrapping = TextWrapping.Wrap;
        mine.Children.Add(mineNote);

        var grid = new Grid { Height = TranslationBar.BarHeight };
        for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        FrameworkElement CardOf(string title, UIElement body, int col, bool gapLeft)
        {
            var inner = new DockPanel { LastChildFill = true };
            var t = Ui.Caption(title);
            t.FontWeight = FontWeights.SemiBold;
            DockPanel.SetDock(t, Dock.Top);
            inner.Children.Add(t);
            inner.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var card = new Border { Child = inner, Padding = new Thickness(10), BorderThickness = new Thickness(1),
                                    Margin = new Thickness(gapLeft ? 10 : 0, 0, 0, 0) };
            card.SetResourceReference(Border.BackgroundProperty, "BgSurface");
            card.SetResourceReference(Border.BorderBrushProperty, "Border");
            card.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
            Grid.SetColumn(card, col);
            return card;
        }
        grid.Children.Add(CardOf("用语", speech, 0, false));
        grid.Children.Add(CardOf("对方信息", them, 1, true));
        grid.Children.Add(CardOf("我方信息", mine, 2, true));
        Content = grid;

        WireEvents();
        Loaded += (_, _) => { TheApp.Reply.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => TheApp.Reply.Changed -= Refresh;
    }

    /// <summary>
    /// 指针式滑条:全部档位名【常显】在下方等分刻度上(选中的着重色),滑块只是指针 ——
    /// 滑动只换高亮,不改任何排版(用户裁定:滑动不许引起版面变化)。
    /// </summary>
    FrameworkElement PointerSlider(string label, Slider slider, StackPanel ticks, string[] names)
    {
        var box = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
        var head = Ui.Caption(label);
        box.Children.Add(head);
        slider.Margin = new Thickness(0, 2, 0, 0);
        box.Children.Add(slider);
        ticks.Children.Clear();
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 0) };
        for (int i = 0; i < names.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var t = new TextBlock { Text = names[i], TextAlignment = i == 0 ? TextAlignment.Left : i == names.Length - 1 ? TextAlignment.Right : TextAlignment.Center,
                                    Cursor = System.Windows.Input.Cursors.Hand };
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            var idx = i;
            t.MouseLeftButtonUp += (_, e) => { e.Handled = true; slider.Value = idx; };   // 点档名也能选
            Grid.SetColumn(t, i);
            grid.Children.Add(t);
            ticks.Children.Add(new StackPanel());   // 占位:高亮逻辑在 Refresh 里按 grid 找
        }
        _tickGrids[slider] = grid;
        box.Children.Add(grid);
        return box;
    }

    readonly Dictionary<Slider, Grid> _tickGrids = new();

    void HighlightTicks(Slider slider, int sel)
    {
        if (!_tickGrids.TryGetValue(slider, out var grid)) return;
        for (int i = 0; i < grid.Children.Count; i++)
            if (grid.Children[i] is TextBlock t)
            {
                t.SetResourceReference(TextBlock.ForegroundProperty, Grid.GetColumn(t) == sel ? "Accent" : "FgMuted");
                t.FontWeight = Grid.GetColumn(t) == sel ? FontWeights.SemiBold : FontWeights.Normal;
            }
    }

    static FrameworkElement LabeledBox(string label, FrameworkElement el)
    {
        var p = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
        var t = Ui.Caption(label);
        t.Width = 30; t.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(t, Dock.Left);
        p.Children.Add(t);
        p.Children.Add(el);
        return p;
    }

    static FrameworkElement Field(string label, TextBox tb)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        p.Children.Add(Ui.Caption(label));
        tb.Padding = new Thickness(5, 3, 5, 3);
        p.Children.Add(tb);
        return p;
    }

    void WireEvents()
    {
        void Save(Action set)
        {
            if (_syncing) return;
            TheApp.Reply.EnsureSession();
            set();
            TheApp.Reply.Touch();
        }
        _medium.ValueChanged += (_, _) => { HighlightTicks(_medium, (int)_medium.Value); Save(() => TheApp.Reply.Doc.Medium = (ReplyMedium)(int)_medium.Value); };
        _tone.ValueChanged += (_, _) => { HighlightTicks(_tone, (int)_tone.Value); Save(() => { var d = TheApp.Reply.Doc; d.Tone = (ReplyTone)(int)_tone.Value; d.GreetingIndex = 0; d.ClosingIndex = 0; }); };
        _lang.SelectionChanged += (_, _) => Save(() => { if (_lang.SelectedItem is ComboBoxItem { Tag: string c }) TheApp.Reply.Doc.Language = c; });
        _greet.SelectionChanged += (_, _) => Save(() => TheApp.Reply.Doc.GreetingIndex = Math.Max(0, _greet.SelectedIndex));
        _close.SelectionChanged += (_, _) => Save(() => TheApp.Reply.Doc.ClosingIndex = Math.Max(0, _close.SelectedIndex));
        void Txt(TextBox tb, Action<string> set, bool session = true)
            => tb.TextChanged += (_, _) => { if (_syncing) return; if (session) TheApp.Reply.EnsureSession(); set(tb.Text); TheApp.Reply.Touch(); };
        Txt(_theirName, v => TheApp.Reply.Doc.TheirName = v);
        Txt(_theirAddr, v => TheApp.Reply.Doc.TheirAddress = v);
        Txt(_theirContact, v => TheApp.Reply.Doc.TheirContact = v);
        Txt(_signDate, v => TheApp.Reply.Doc.SignDate = v);
        // 我方 = 常驻模板:不建会话、不随会话,直接写 Profile
        Txt(_myName, v => TheApp.Reply.Profile.MyName = v, session: false);
        Txt(_myAddr, v => TheApp.Reply.Profile.MyAddress = v, session: false);
        Txt(_myContact, v => TheApp.Reply.Profile.MyContact = v, session: false);
    }

    void Refresh()
    {
        var d = TheApp.Reply.Doc;
        var me = TheApp.Reply.Profile;
        _syncing = true;
        try
        {
            _medium.Value = (int)d.Medium; HighlightTicks(_medium, (int)d.Medium);
            _tone.Value = (int)d.Tone; HighlightTicks(_tone, (int)d.Tone);
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
            Set(_theirName, d.TheirName); Set(_theirAddr, d.TheirAddress);
            Set(_theirContact, d.TheirContact); Set(_signDate, d.SignDate);
            Set(_myName, me.MyName); Set(_myAddr, me.MyAddress); Set(_myContact, me.MyContact);
        }
        finally { _syncing = false; }
    }
}
