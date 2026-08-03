// P3c -- 回信会话区(D61 五补,用户裁定 2026-08-03):会话卡里【只有】这四块 ——
//   左上 我想回复的内容(2/3 高) | 右上 生成结果(署名日期 + 生成 + 推送/PDF/复制)
//   左下 来信(可留空,1/3 高)    | 右下 对话记录(1/3 高)
//
// ★ 对话记录 = 真记录:按下「生成」才落一条(输入与设置整份快照 + 产出);
//   生成中那条显示【正在生成】且输入框禁用;选中旧记录改完再生成 -> 追加新的一条,不覆盖。
// ★ 会话【有记录才进右侧会话列表】—— 随便写写不留空会话(见 ReplyState.EnsureSession)。

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
    readonly TextBox _signDate = new() { Width = 132, Padding = new Thickness(5, 3, 5, 3),
                                         VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    readonly StackPanel _log = new();
    readonly DockPanel _resultBtns = new() { LastChildFill = true };
    bool _editing;

    public ReplyPanel()
    {
        _result.FontFamily = new FontFamily("Consolas");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        // ★ 上 2 : 下 1(用户裁定):来信与对话记录各占三分之一,其余留给我的回复与生成结果
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        void Put(FrameworkElement el, int col, int row) { Grid.SetColumn(el, col); Grid.SetRow(el, row); grid.Children.Add(el); }
        Put(Sect("我想回复的内容", _draft, null), 0, 0);
        Put(Sect("来信(可留空)", _incoming, null), 0, 2);
        Put(Sect("生成结果", _result, _resultBtns), 2, 0);
        Put(Sect("对话记录", new ScrollViewer { Content = _log, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, null), 2, 2);
        Content = grid;

        _draft.TextChanged += (_, _) => Save(d => d.Draft = _draft.Text);
        _incoming.TextChanged += (_, _) => Save(d => d.Incoming = _incoming.Text);
        _signDate.TextChanged += (_, _) => Save(d => d.SignDate = _signDate.Text);
        Rebuild();   // ★ 先画一遍:离屏渲染诊断不会触发 Loaded,不先画按钮排在图里是空的
        Loaded += (_, _) => { TheApp.Reply.Changed += Rebuild; Rebuild(); };
        Unloaded += (_, _) => TheApp.Reply.Changed -= Rebuild;
    }

    /// <summary>写进当前文档 —— ★ 这里【不建会话】:会话只在按下生成时才产生(见 ReplyState)。</summary>
    void Save(Action<ReplyDoc> set)
    {
        set(TheApp.Reply.Doc);
        _editing = true;
        try { TheApp.Reply.Touch(); } finally { _editing = false; }
    }

    /// <summary>四个内容块:会话卡内的子区域 —— 用沉底色分区,不再各套一圈边框(那会变成框中框)。</summary>
    static FrameworkElement Sect(string title, FrameworkElement body, FrameworkElement? actions)
    {
        var t = Ui.Caption(title);
        t.FontWeight = FontWeights.SemiBold;
        t.VerticalAlignment = VerticalAlignment.Center;
        t.Margin = new Thickness(0, 0, 0, 5);
        var inner = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(t, Dock.Top);
        inner.Children.Add(t);
        // ★ 动作排在【板块底部】,不挤进标题行(渲染图 2026-08-03:最小窗宽下
        //   DockPanel 先满足右侧按钮,标题只剩一个字的宽,"生成结果"被压成竖排)。
        if (actions is not null)
        {
            actions.Margin = new Thickness(0, 8, 0, 0);
            DockPanel.SetDock(actions, Dock.Bottom);
            inner.Children.Add(actions);
        }
        inner.Children.Add(body);
        var card = new Border { Child = inner, Padding = new Thickness(9) };
        card.SetResourceReference(Border.BackgroundProperty, "BgSunken");
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
        var st = TheApp.Reply;
        var d = st.Doc;
        if (!_editing)
        {
            if (_draft.Text != d.Draft) _draft.Text = d.Draft;
            if (_incoming.Text != d.Incoming) _incoming.Text = d.Incoming;
            if (_signDate.Text != d.SignDate) _signDate.Text = d.SignDate;
        }
        if (_result.Text != d.Result) _result.Text = d.Result;

        // ★ 生成中:输入一律禁用(用户裁定)—— 半路改输入会让产出与记录对不上号
        var busy = st.Busy;
        _draft.IsEnabled = _incoming.IsEnabled = _signDate.IsEnabled = !busy;

        BuildLog(st, d);
        BuildResultActions(st, d, busy);
    }

    void BuildLog(ReplyState st, ReplyDoc d)
    {
        _log.Children.Clear();
        if (d.Records.Count == 0)
        {
            var empty = Ui.Caption("按下「生成」后,这一次的输入、设置与产出会整份记在这里。\n改完再生成 = 追加新的一条,旧的原样保留。");
            empty.TextWrapping = TextWrapping.Wrap;
            _log.Children.Add(empty);
            return;
        }
        for (int i = d.Records.Count - 1; i >= 0; i--)   // 新的在上
        {
            var r = d.Records[i];
            var sel = d.SelectedRecordId == r.Id;
            var head = new DockPanel { LastChildFill = true };
            var when = Ui.Caption(r.Generating ? "正在生成…" : r.At.ToString("HH:mm:ss"));
            when.SetResourceReference(TextBlock.ForegroundProperty, r.Generating ? "RiskWarning" : "FgMuted");
            DockPanel.SetDock(when, Dock.Right);
            head.Children.Add(when);
            var title = new TextBlock
            {
                Text = $"#{i + 1} · {(r.Medium == ReplyMedium.Email ? "邮件" : r.Medium == ReplyMedium.Paper ? "信件" : "消息")}"
                     + $" · {(r.Tone == ReplyTone.Casual ? "熟人" : r.Tone == ReplyTone.Polite ? "礼貌" : "行政")}",
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, sel ? "Accent" : "FgPrimary");
            title.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            head.Children.Add(title);

            var preview = new TextBlock
            {
                Text = r.Generating ? "…" : (r.Draft.Length > 40 ? r.Draft[..40] + "…" : r.Draft),
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0),
            };
            preview.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            preview.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

            var body = new StackPanel();
            body.Children.Add(head);
            body.Children.Add(preview);
            var row = new Border { Child = body, Padding = new Thickness(7, 5, 7, 5), Margin = new Thickness(0, 0, 0, 3),
                                   Cursor = System.Windows.Input.Cursors.Hand, BorderThickness = new Thickness(1) };
            row.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            row.SetResourceReference(Border.BorderBrushProperty, sel ? "Accent" : "Border");
            row.Background = Brushes.Transparent;
            var id = r.Id;
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (!st.Busy) st.SelectRecord(id); };
            _log.Children.Add(row);
        }
    }

    void BuildResultActions(ReplyState st, ReplyDoc d, bool busy)
    {
        _resultBtns.Children.Clear();
        // ★ 右边的图标与生成键先占位(Dock.Right 按添加顺序从右往左排),
        //   署名日期最后放【吃剩下的宽】—— 窗口再窄也是它变短,不会把按钮挤出去。
        var tail = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(tail, Dock.Right);
        _resultBtns.Children.Add(tail);
        if (d.Medium == ReplyMedium.Paper)
        {
            var hint = new TextBlock { Text = "署名日期 · 空=当天", IsHitTestVisible = false,
                                       VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            hint.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            hint.Visibility = _signDate.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _signDate.TextChanged -= SyncDateHint;
            _dateHint = hint;
            _signDate.TextChanged += SyncDateHint;
            _signDate.Width = double.NaN;   // 吃剩余宽度,不再钉死 132
            _signDate.MinWidth = 84;
            var wrap = new Grid { Margin = new Thickness(0, 0, 6, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
            wrap.Children.Add(_signDate);
            wrap.Children.Add(hint);
            _resultBtns.Children.Add(wrap);   // LastChildFill:留到最后 add 才吃得到剩余宽
        }
        else
        {
            _resultBtns.Children.Add(new Border());   // 没有日期栏时,占位撑开左边的空白
        }

        var gen = Ui.Primary(busy ? "生成中…" : "生成", (_, _) =>
        {
            if (st.Busy) return;
            if (st.Doc.Draft.Trim().Length == 0)
            { ConfirmDialog.Show("还没有内容", "先在左上写你想回复的内容。", confirmText: "好", cancelText: "关闭"); return; }
            st.Generate();   // 建会话(有记录才进列表)+ 落记录 + 装配
        });
        gen.IsEnabled = !busy;
        gen.Margin = new Thickness(0, 0, 6, 0);
        tail.Children.Add(gen);

        if (d.Medium == ReplyMedium.Email)
            tail.Children.Add(IconBtn(Theme.IconName.Send, "推送到邮箱(接线方案待议)", () =>
                ConfirmDialog.Show("还不能推送",
                    "直接发进邮箱的接线方案还没定(候选:本机邮件客户端 mailto / SMTP)。\n先用复制图标粘到邮箱里发。",
                    confirmText: "知道了", cancelText: "关闭")));
        if (d.Medium == ReplyMedium.Paper)
            tail.Children.Add(IconBtn(Theme.IconName.Pdf, "生成 PDF(随引擎接入)", () =>
                ConfirmDialog.Show("还不能出 PDF",
                    "PDF 排版输出随引擎(P4)一起接。复制图标给出的就是排好的信件格式文本,可粘进任何文档软件打印。",
                    confirmText: "知道了", cancelText: "关闭")));
        tail.Children.Add(IconBtn(Theme.IconName.Copy,
            d.Medium == ReplyMedium.Paper ? "复制格式文本" : "复制", () =>
        {
            if (d.Result.Trim().Length == 0)
            { ConfirmDialog.Show("还没有结果", "先点「生成」。", confirmText: "好", cancelText: "关闭"); return; }
            try { Clipboard.SetText(d.Result); } catch { }
        }));
    }

    TextBlock? _dateHint;
    void SyncDateHint(object? s, TextChangedEventArgs e)
    {
        if (_dateHint is not null)
            _dateHint.Visibility = _signDate.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
