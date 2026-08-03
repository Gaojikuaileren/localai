// P3c -- 回信会话区(D61 二次重设计,用户裁定 2026-08-03):【四板块】2×2
//   左上 我想回复的内容 | 右上 生成结果(生成按钮 + 推送/PDF/复制 图标)
//   左下 来信(可留空)   | 右下 对话记录(这条回信会话的真实往复;AI 未接入前为空,如实说)
// ★ 来信与我的回复上下对调(用户裁定):先写你要说的,来信是参考,放下面。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public sealed class ReplyPanel : UserControl
{
    static App TheApp => (App)Application.Current;

    static TextBox Area(bool readOnly = false) => new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        IsReadOnly = readOnly,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
    };

    readonly TextBox _draft = Area();
    readonly TextBox _incoming = Area();
    readonly TextBox _result = Area(readOnly: true);
    readonly StackPanel _log = new();
    readonly StackPanel _resultBtns = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    bool _editing;

    public ReplyPanel()
    {
        _result.FontFamily = new FontFamily("Consolas");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        grid.RowDefinitions.Add(new RowDefinition());

        void Put(FrameworkElement el, int col, int row) { Grid.SetColumn(el, col); Grid.SetRow(el, row); grid.Children.Add(el); }
        Put(Sect("我想回复的内容", _draft, null), 0, 0);
        Put(Sect("来信(可留空)", _incoming, null), 0, 2);
        Put(Sect("生成结果", _result, _resultBtns), 2, 0);
        Put(Sect("对话记录", new ScrollViewer { Content = _log, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, null), 2, 2);
        Content = grid;

        _draft.TextChanged += (_, _) => Save(d => d.Draft = _draft.Text);
        _incoming.TextChanged += (_, _) => Save(d => d.Incoming = _incoming.Text);
        Loaded += (_, _) => { TheApp.Reply.Changed += Rebuild; TheApp.Chat.Changed += Rebuild; Rebuild(); };
        Unloaded += (_, _) => { TheApp.Reply.Changed -= Rebuild; TheApp.Chat.Changed -= Rebuild; };
    }

    void Save(Action<ReplyDoc> set)
    {
        TheApp.Reply.EnsureSession();
        set(TheApp.Reply.Doc);
        _editing = true;
        try { TheApp.Reply.Touch(); } finally { _editing = false; }
    }

    static FrameworkElement Sect(string title, FrameworkElement body, FrameworkElement? actions)
    {
        var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
        if (actions is not null) { DockPanel.SetDock(actions, Dock.Right); head.Children.Add(actions); }
        var t = Ui.Caption(title);
        t.FontWeight = FontWeights.SemiBold;
        t.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(t);
        var inner = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top);
        inner.Children.Add(head);
        inner.Children.Add(body);
        var card = new Border { Child = inner, Padding = new Thickness(10), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        return card;
    }

    static FrameworkElement IconBtn(Theme.IconName icon, string tip, Action click)
    {
        var b = new Border { Child = Theme.Icons.Make(icon, 15, "FgSecondary"), Padding = new Thickness(6),
                             Margin = new Thickness(4, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand,
                             Background = Brushes.Transparent, ToolTip = tip };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    void Rebuild()
    {
        var d = TheApp.Reply.Doc;
        if (!_editing)
        {
            if (_draft.Text != d.Draft) _draft.Text = d.Draft;
            if (_incoming.Text != d.Incoming) _incoming.Text = d.Incoming;
        }
        if (_result.Text != d.Result) _result.Text = d.Result;

        // ---- 对话记录:这条回信会话的真实往复 —— 没有就如实说没有,不摆假对话
        _log.Children.Clear();
        var msgs = TheApp.Reply.SessionId is { } sid
            ? TheApp.Chat.MessagesOf(sid).ToList()
            : new List<ChatMessage>();
        if (msgs.Count == 0)
        {
            var empty = Ui.Caption("这封信与 AI 的往复会记录在这里。\n★ AI 尚未接入(P4)——现在还没有对话,这里不摆假记录。");
            empty.TextWrapping = TextWrapping.Wrap;
            _log.Children.Add(empty);
        }
        else
            foreach (var m in msgs)
            {
                var who = Ui.Caption(m.Role == ChatRole.User ? "我" : m.Role == ChatRole.Assistant ? "AI" : "系统");
                who.Margin = new Thickness(0, 4, 0, 0);
                var body = new TextBlock { Text = m.Text, TextWrapping = TextWrapping.Wrap };
                body.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                _log.Children.Add(who);
                _log.Children.Add(body);
            }

        // ---- 结果头部:生成(主动作,用户裁定放这儿)+ 载体相关的图标按钮
        _resultBtns.Children.Clear();
        var gen = Ui.Primary("生成", (_, _) =>
        {
            var st = TheApp.Reply;
            st.EnsureSession();
            var doc = st.Doc;
            if (doc.Draft.Trim().Length == 0)
            { ConfirmDialog.Show("还没有内容", "先在左上写你想回复的内容。", confirmText: "好", cancelText: "关闭"); return; }
            doc.Result = ReplyState.Compose(doc, st.Profile);
            st.Touch();
        });
        gen.Margin = new Thickness(0, 0, 6, 0);
        _resultBtns.Children.Add(gen);

        if (d.Medium == ReplyMedium.Email)
            _resultBtns.Children.Add(IconBtn(Theme.IconName.Send, "推送到邮箱(接线方案待议)", () =>
                ConfirmDialog.Show("还不能推送",
                    "直接发进邮箱的接线方案还没定(候选:本机邮件客户端 mailto / SMTP)。\n先用复制图标粘到邮箱里发。",
                    confirmText: "知道了", cancelText: "关闭")));
        if (d.Medium == ReplyMedium.Paper)
            _resultBtns.Children.Add(IconBtn(Theme.IconName.Pdf, "生成 PDF(随引擎接入)", () =>
                ConfirmDialog.Show("还不能出 PDF",
                    "PDF 排版输出随引擎(P4)一起接。复制图标给出的就是排好的信件格式文本,可粘进任何文档软件打印。",
                    confirmText: "知道了", cancelText: "关闭")));
        _resultBtns.Children.Add(IconBtn(Theme.IconName.Copy,
            d.Medium == ReplyMedium.Paper ? "复制格式文本" : "复制", () =>
        {
            if (d.Result.Trim().Length == 0)
            { ConfirmDialog.Show("还没有结果", "先点「生成」。", confirmText: "好", cancelText: "关闭"); return; }
            try { Clipboard.SetText(d.Result); } catch { }
        }));
    }
}
