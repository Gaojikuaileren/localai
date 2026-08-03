// P3c -- 回信的底部设置条(D61):载体滑条 / 语气滑条 / 语言 / 称谓地址联系 / 问候祝福 / 生成。
//   全部设置跟随会话(存 ReplyDoc);新进来 = 默认。

using System.Windows;
using System.Windows.Controls;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public sealed class ReplyBar : UserControl
{
    static App TheApp => (App)Application.Current;
    bool _syncing;   // 程序性回填时不当用户操作(否则每次刷新都触发一轮保存)

    readonly Slider _medium = new() { Minimum = 0, Maximum = 2, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 130 };
    readonly TextBlock _mediumLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly Slider _tone = new() { Minimum = 0, Maximum = 3, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 130 };
    readonly TextBlock _toneLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly ComboBox _lang = new() { Width = 130 };
    readonly ComboBox _greet = new() { Width = 150 };
    readonly ComboBox _close = new() { Width = 150 };
    readonly TextBox _theirName = new(), _myName = new(), _myAddr = new(), _theirAddr = new(),
                     _myContact = new(), _theirContact = new(), _signDate = new();

    public ReplyBar()
    {
        foreach (var l in Languages.Catalog)
            _lang.Items.Add(new ComboBoxItem { Content = $"{l.Name} ({l.Code})", Tag = l.Code });

        var left = new StackPanel();
        left.Children.Add(Row("载体", _medium, _mediumLabel));
        left.Children.Add(Row("语气", _tone, _toneLabel));
        left.Children.Add(Row("语言", _lang, null));
        left.Children.Add(Row("问候", _greet, null));
        left.Children.Add(Row("祝福", _close, null));

        var mid = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        mid.Children.Add(Field("对方姓名", _theirName));
        mid.Children.Add(Field("我方署名", _myName));
        mid.Children.Add(Field("署名日期(纸质;空=当天)", _signDate));

        var right = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        right.Children.Add(Field("对方地址(纸质排入;其余仅参考)", _theirAddr));
        right.Children.Add(Field("我方地址", _myAddr));
        right.Children.Add(Field("对方联系(邮箱/手机)", _theirContact));
        right.Children.Add(Field("我方联系", _myContact));

        // 主动作:生成回信(格式装配 = 真;AI 润色等引擎,如实说)
        var gen = Ui.Primary("生成回信", (_, _) =>
        {
            var st = TheApp.Reply;
            st.EnsureSession();
            var d = st.Doc;
            if (d.Draft.Trim().Length == 0)
            { ConfirmDialog.Show("还没有内容", "先在中间那块写下你想回复的内容。", confirmText: "好", cancelText: "关闭"); return; }
            d.Result = ReplyState.Compose(d);
            st.Touch();
        });
        gen.VerticalAlignment = VerticalAlignment.Center;
        gen.Margin = new Thickness(14, 0, 0, 0);
        var genCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        genCol.Children.Add(gen);
        var note = Ui.Caption("生成 = 按载体排好格式(称呼/正文/祝福/署名),复制即用;AI 润色随引擎(P4)接入。");
        note.MaxWidth = 170; note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(14, 6, 0, 0);
        genCol.Children.Add(note);

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(left); row.Children.Add(mid); row.Children.Add(right); row.Children.Add(genCol);
        var scroll = new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                                        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var card = new Border { Child = scroll, Padding = new Thickness(10), BorderThickness = new Thickness(1), Height = TranslationBar.BarHeight };
        card.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        Content = card;

        WireEvents();
        Loaded += (_, _) => { TheApp.Reply.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => TheApp.Reply.Changed -= Refresh;
    }

    static FrameworkElement Row(string label, FrameworkElement el, FrameworkElement? extra)
    {
        var p = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
        var t = Ui.Caption(label);
        t.Width = 34; t.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(t, Dock.Left);
        p.Children.Add(t);
        if (extra is not null) { DockPanel.SetDock(extra, Dock.Right); p.Children.Add(extra); }
        p.Children.Add(el);
        return p;
    }

    static FrameworkElement Field(string label, TextBox tb)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 3) };
        p.Children.Add(Ui.Caption(label));
        tb.Width = 190; tb.Padding = new Thickness(5, 3, 5, 3); tb.HorizontalAlignment = HorizontalAlignment.Left;
        p.Children.Add(tb);
        return p;
    }

    void WireEvents()
    {
        void Save(Action<ReplyDoc> set)
        {
            if (_syncing) return;
            TheApp.Reply.EnsureSession();
            set(TheApp.Reply.Doc);
            TheApp.Reply.Touch();
        }
        _medium.ValueChanged += (_, _) => Save(d => d.Medium = (ReplyMedium)(int)_medium.Value);
        _tone.ValueChanged += (_, _) => Save(d => { d.Tone = (ReplyTone)(int)_tone.Value; d.GreetingIndex = 0; d.ClosingIndex = 0; });
        _lang.SelectionChanged += (_, _) => Save(d => { if (_lang.SelectedItem is ComboBoxItem { Tag: string c }) d.Language = c; });
        _greet.SelectionChanged += (_, _) => Save(d => d.GreetingIndex = Math.Max(0, _greet.SelectedIndex));
        _close.SelectionChanged += (_, _) => Save(d => d.ClosingIndex = Math.Max(0, _close.SelectedIndex));
        void Txt(TextBox tb, Action<ReplyDoc, string> set) => tb.TextChanged += (_, _) => Save(d => set(d, tb.Text));
        Txt(_theirName, (d, v) => d.TheirName = v); Txt(_myName, (d, v) => d.MyName = v);
        Txt(_myAddr, (d, v) => d.MyAddress = v); Txt(_theirAddr, (d, v) => d.TheirAddress = v);
        Txt(_myContact, (d, v) => d.MyContact = v); Txt(_theirContact, (d, v) => d.TheirContact = v);
        Txt(_signDate, (d, v) => d.SignDate = v);
    }

    void Refresh()
    {
        var d = TheApp.Reply.Doc;
        _syncing = true;
        try
        {
            _medium.Value = (int)d.Medium;
            _mediumLabel.Text = d.Medium switch { ReplyMedium.Email => "邮件", ReplyMedium.Paper => "纸质信件", _ => "短消息" };
            _tone.Value = (int)d.Tone;
            _toneLabel.Text = d.Tone switch { ReplyTone.Friend => "朋友", ReplyTone.Normal => "普通", ReplyTone.Formal => "正式", _ => "行政" };
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
            Set(_theirName, d.TheirName); Set(_myName, d.MyName);
            Set(_myAddr, d.MyAddress); Set(_theirAddr, d.TheirAddress);
            Set(_myContact, d.MyContact); Set(_theirContact, d.TheirContact); Set(_signDate, d.SignDate);
        }
        finally { _syncing = false; }
    }
}
