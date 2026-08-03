// P3c -- 回信会话区(D61 重设计,用户裁定 2026-08-03):
//   左列上下两块(来信 / 我想回复),右侧整高一块(生成结果)——
//   生成按钮在结果板块头部;推送/PDF/复制全部图标化(按下如实说明各自现状)。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public sealed class ReplyPanel : UserControl
{
    static App TheApp => (App)Application.Current;
    readonly TextBox _incoming = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                         BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    readonly TextBox _draft = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                      BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    readonly TextBox _result = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, IsReadOnly = true,
                                       VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"),
                                       BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    readonly StackPanel _resultBtns = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    bool _editing;

    public ReplyPanel()
    {
        // 左列:来信(上,可空)+ 我想回复(下);右:生成结果整高 —— 视线从左往右就是工作流
        var leftCol = new Grid();
        leftCol.RowDefinitions.Add(new RowDefinition());
        leftCol.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        leftCol.RowDefinitions.Add(new RowDefinition());
        var sIn = Sect("来信(可留空)", _incoming, null);
        Grid.SetRow(sIn, 0); leftCol.Children.Add(sIn);
        var sDraft = Sect("我想回复的内容", _draft, null);
        Grid.SetRow(sDraft, 2); leftCol.Children.Add(sDraft);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        Grid.SetColumn(leftCol, 0); grid.Children.Add(leftCol);
        var sOut = Sect("生成结果", _result, _resultBtns);
        Grid.SetColumn(sOut, 2); grid.Children.Add(sOut);
        Content = grid;

        _incoming.TextChanged += (_, _) => Save(d => d.Incoming = _incoming.Text);
        _draft.TextChanged += (_, _) => Save(d => d.Draft = _draft.Text);
        Loaded += (_, _) => { TheApp.Reply.Changed += Rebuild; Rebuild(); };
        Unloaded += (_, _) => TheApp.Reply.Changed -= Rebuild;
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

    /// <summary>结果头部的小图标按钮(推送/PDF/复制 —— 用户裁定图标化)。</summary>
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
            if (_incoming.Text != d.Incoming) _incoming.Text = d.Incoming;
            if (_draft.Text != d.Draft) _draft.Text = d.Draft;
        }
        if (_result.Text != d.Result) _result.Text = d.Result;

        _resultBtns.Children.Clear();
        // 生成 = 主动作,在结果板块头部(用户裁定);格式装配是真的,AI 润色随引擎
        var gen = Ui.Primary("生成", (_, _) =>
        {
            var st = TheApp.Reply;
            st.EnsureSession();
            var doc = st.Doc;
            if (doc.Draft.Trim().Length == 0)
            { ConfirmDialog.Show("还没有内容", "先在左下写你想回复的内容。", confirmText: "好", cancelText: "关闭"); return; }
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
