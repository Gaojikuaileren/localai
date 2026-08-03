// P3c -- 回信场景(D61):上三块(来信 / 我想回复 / 生成结果)+ 下设置条(ReplyBar 在本文件底部)。
//   结果区按载体给按钮:消息 = 复制;邮件 = 推送到邮箱(接线方案待议,如实说)+ 复制;
//   纸质 = 生成 PDF(随引擎接,如实说)+ 复制格式文本。
// ★ 「生成」= 格式装配(真);AI 润色等引擎(P4),界面写明。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public sealed class ReplyPanel : UserControl
{
    static App TheApp => (App)Application.Current;
    readonly TextBox _incoming = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly TextBox _draft = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly TextBox _result = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, IsReadOnly = true,
                                       VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas") };
    readonly StackPanel _resultBtns = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    bool _editing;

    public ReplyPanel()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });

        grid.Children.Add(Sect("来信(可留空)", _incoming, 0, null));
        grid.Children.Add(Sect("我想回复的内容", _draft, 1, null));
        grid.Children.Add(Sect("生成结果", _result, 2, _resultBtns));

        Content = grid;

        _incoming.TextChanged += (_, _) => Save(d => d.Incoming = _incoming.Text);
        _draft.TextChanged += (_, _) => Save(d => d.Draft = _draft.Text);
        Loaded += (_, _) => { TheApp.Reply.Changed += Rebuild; Rebuild(); };
        Unloaded += (_, _) => TheApp.Reply.Changed -= Rebuild;
    }

    void Save(Action<ReplyDoc> set)
    {
        TheApp.Reply.EnsureSession();   // 设置与内容跟随会话 —— 第一笔编辑就得有会话
        set(TheApp.Reply.Doc);
        _editing = true;
        try { TheApp.Reply.Touch(); } finally { _editing = false; }
    }

    FrameworkElement Sect(string title, FrameworkElement body, int col, FrameworkElement? actions)
    {
        var head = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 0, 2, 4) };
        if (actions is not null) { DockPanel.SetDock(actions, Dock.Right); head.Children.Add(actions); }
        var t = Ui.Caption(title);
        t.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(t);
        var card = new Border { Padding = new Thickness(6), Margin = new Thickness(col == 0 ? 0 : 6, 0, 0, 0) };
        card.SetResourceReference(Border.BackgroundProperty, "BgSunken");
        card.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        var inner = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top);
        inner.Children.Add(head);
        inner.Children.Add(body);
        card.Child = inner;
        Grid.SetColumn(card, col);
        return card;
    }

    void Rebuild()
    {
        var d = TheApp.Reply.Doc;
        if (!_editing)
        {
            if (_incoming.Text != d.Incoming) _incoming.Text = d.Incoming;
            if (_draft.Text != d.Draft) _draft.Text = d.Draft;
        }
        if (_result.Text != d.Result) _result.Text = d.Result;

        // 结果区按钮随载体变(用户裁定):消息=复制;邮件=推送+复制;纸质=PDF+复制格式文本
        _resultBtns.Children.Clear();
        void Add(string text, Action click)
        {
            var c = Ui.Secondary(text, (_, _) => click());
            c.Margin = new Thickness(6, 0, 0, 0);
            _resultBtns.Children.Add(c);
        }
        if (d.Medium == ReplyMedium.Email)
            Add("推送到邮箱", () => ConfirmDialog.Show("还不能推送",
                "直接发进邮箱的接线方案还没定(候选:本机邮件客户端 mailto / SMTP)。\n先用「复制」粘到你的邮箱里发。",
                confirmText: "知道了", cancelText: "关闭"));
        if (d.Medium == ReplyMedium.Paper)
            Add("生成 PDF", () => ConfirmDialog.Show("还不能出 PDF",
                "PDF 排版输出随引擎(P4)一起接。当前「复制」给出的就是排好的信件格式文本,可粘进任何文档软件打印。",
                confirmText: "知道了", cancelText: "关闭"));
        Add(d.Medium == ReplyMedium.Paper ? "复制格式文本" : "复制", () =>
        {
            if (d.Result.Trim().Length == 0)
            { ConfirmDialog.Show("还没有结果", "先点下方的「生成回信」。", confirmText: "好", cancelText: "关闭"); return; }
            try { Clipboard.SetText(d.Result); TheApp.Reply.Touch(); } catch { }
        });
    }
}
