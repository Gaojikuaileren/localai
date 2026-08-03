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
    // ★ 署名日期【不让人敲字】(用户裁定 2026-08-03):点开是日期选择浮窗。
    //   理由:它最后要排进信里,自由输入必然出现"2026/8/3""八月三日""明天"这类各写各的;
    //   而且这一栏原来就是那次 FailFast 事故的现场(带焦点的 TextBox 被重挂)——
    //   换成一枚按钮之后,这一格连焦点都不需要了。
    //   滚轮复用日程/待办那一套 WheelPicker.Date,不另造一种日期控件。
    readonly TextBlock _dateText = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly Border _dateField = new() { Padding = new Thickness(8, 4, 8, 4), MinWidth = 84,
                                         VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
                                         BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand };
    readonly StackPanel _log = new();
    // ★★ 动作排【只建一次】(2026-08-03 事故):此前每次 Rebuild 都新建容器再把字段级 _signDate
    //   塞进去 —— 旧父子关系没解开,直接 InvalidOperationException;更要命的是
    //   带焦点的 TextBox 在 TSF(输入法框架)持锁期间被重挂,WPF 会 Environment.FailFast
    //   把整个进程杀掉(catch 不住,日志里只留 TextStore.GrantLockWorker 那根栈)。
    //   所以这一排从此只在构造时建,刷新只改属性,永不重建。
    readonly DockPanel _resultBtns = new() { LastChildFill = true };
    readonly StackPanel _tail = new() { Orientation = Orientation.Horizontal };
    readonly Grid _dateWrap = new() { Margin = new Thickness(0, 0, 6, 0) };

    Button _gen = null!;
    FrameworkElement _sendBtn = null!, _pdfBtn = null!, _copyBtn = null!;
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

        BuildActionsOnce();

        // ★ Tab 次序【显式登记】(用户裁定 2026-08-03:tab 在不同输入框之间切换)。
        //   从会话卡里的正文起步,再到下方设置条(见 ReplyBar 的 20 开头那一段)——
        //   靠树序不行:设置条在树里排在会话卡【前面】,走出来会是自下而上的。
        //   _result 是只读产出框,不进圈(与"只读正文不占 Tab 序"的既定纪律一致,鼠标照样能拖选复制)。
        FocusPolicy.SetTabOrder(_draft, 10);
        FocusPolicy.SetTabOrder(_incoming, 11);
        // 署名日期现在是一枚按钮(点开浮窗选),不是输入框 —— 不进 Tab 圈。

        _draft.TextChanged += (_, _) => Save(d => d.Draft = _draft.Text);
        _incoming.TextChanged += (_, _) => Save(d => d.Incoming = _incoming.Text);

        Rebuild();   // ★ 先画一遍:离屏渲染诊断不会触发 Loaded,不先画按钮排在图里是空的
        // -= 再 +=:Loaded 在重新挂树时会再来一次,而 Unloaded 不保证成对 —— 不防就会重复订阅
        Loaded += (_, _) => { TheApp.Reply.Changed -= Rebuild; TheApp.Reply.Changed += Rebuild; Rebuild(); };
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

    /// <summary>
    /// internal 是一条【真缝】:无头自检要能连着调两次,复现 2026-08-03 那次
    /// "第二次重建时 _signDate 还挂在上一个容器上"的事故 —— 靠 RaiseEvent(Loaded) 不可靠,
    /// 离屏环境下订阅到底有没有生效不确定,那样断言会变成永远绿的假断言。
    /// </summary>
    internal void Rebuild()
    {
        var st = TheApp.Reply;
        var d = st.Doc;
        if (!_editing)
        {
            if (_draft.Text != d.Draft) _draft.Text = d.Draft;
            if (_incoming.Text != d.Incoming) _incoming.Text = d.Incoming;

        }
        if (_result.Text != d.Result) _result.Text = d.Result;

        // ★ 生成中:输入一律禁用(用户裁定)—— 半路改输入会让产出与记录对不上号
        var busy = st.Busy;
        _draft.IsEnabled = _incoming.IsEnabled = !busy;
        _dateField.IsEnabled = !busy;

        BuildLog(st, d);
        RefreshActions(d, busy);
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

    /// <summary>
    /// 动作排装配 —— ★【只跑一次】。刷新走 RefreshActions,只改属性。
    /// 右边的图标与生成键先占位(Dock.Right 按添加顺序从右往左排),
    /// 署名日期最后放,吃剩下的宽 —— 窗口再窄也是它变短,不会把按钮挤出去。
    /// </summary>
    void BuildActionsOnce()
    {
        _dateText.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var dateRow = new StackPanel { Orientation = Orientation.Horizontal };
        var dateIcon = Theme.Icons.Make(Theme.IconName.Calendar, 13, "FgMuted");
        dateIcon.Margin = new Thickness(0, 0, 6, 0);
        dateIcon.VerticalAlignment = VerticalAlignment.Center;
        dateRow.Children.Add(dateIcon);
        dateRow.Children.Add(_dateText);
        _dateField.Child = dateRow;
        _dateField.SetResourceReference(Border.BackgroundProperty, "BgSunken");
        _dateField.SetResourceReference(Border.BorderBrushProperty, "Border");
        _dateField.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        _dateField.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (!TheApp.Reply.Busy) PickSignDate(); };
        _dateWrap.Children.Add(_dateField);

        _gen = Ui.Primary("生成", (_, _) =>
        {
            var st = TheApp.Reply;
            if (st.Busy) return;
            if (st.Doc.Draft.Trim().Length == 0)
            { ConfirmDialog.Show("还没有内容", "先在左上写你想回复的内容。", confirmText: "好", cancelText: "关闭"); return; }
            st.Generate();   // 建会话(有记录才进列表)+ 落记录 + 装配
        });
        _gen.Margin = new Thickness(0, 0, 6, 0);
        _tail.Children.Add(_gen);

        _sendBtn = IconBtn(Theme.IconName.Send, "推送到邮箱(接线方案待议)", () =>
            ConfirmDialog.Show("还不能推送",
                "直接发进邮箱的接线方案还没定(候选:本机邮件客户端 mailto / SMTP)。\n先用复制图标粘到邮箱里发。",
                confirmText: "知道了", cancelText: "关闭"));
        _pdfBtn = IconBtn(Theme.IconName.Pdf, "生成 PDF(随引擎接入)", () =>
            ConfirmDialog.Show("还不能出 PDF",
                "PDF 排版输出随引擎(P4)一起接。复制图标给出的就是排好的信件格式文本,可粘进任何文档软件打印。",
                confirmText: "知道了", cancelText: "关闭"));
        _copyBtn = IconBtn(Theme.IconName.Copy, "复制", () =>
        {
            var d = TheApp.Reply.Doc;
            if (d.Result.Trim().Length == 0)
            { ConfirmDialog.Show("还没有结果", "先点「生成」。", confirmText: "好", cancelText: "关闭"); return; }
            try { Clipboard.SetText(d.Result); } catch { }
        });
        _tail.Children.Add(_sendBtn);
        _tail.Children.Add(_pdfBtn);
        _tail.Children.Add(_copyBtn);

        DockPanel.SetDock(_tail, Dock.Right);
        _resultBtns.Children.Add(_tail);
        _resultBtns.Children.Add(_dateWrap);   // LastChildFill:留到最后 add 才吃得到剩余宽
    }

    /// <summary>
    /// 署名日期浮窗:滚轮选年月日 + 两个出口。
    /// ★「用当天」不是"选今天",而是把这一栏【清空】—— 空的含义是"生成那天",
    ///   隔几天再生成会跟着变;选定的日期则钉死。两者不是一回事,所以给两个出口。
    /// </summary>
    void PickSignDate()
    {
        var st = TheApp.Reply;
        var picked = ReplyState.ParseSignDate(st.Doc.SignDate) ?? DateTime.Today;
        var body = new StackPanel();
        body.Children.Add(WheelPicker.Date(picked, d => picked = d));
        var note = Ui.Caption("留空 = 生成那天(隔天再生成会跟着变);选定则钉死。");
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 4, 0, 8);
        body.Children.Add(note);

        var row = new DockPanel { LastChildFill = false };
        var useToday = Ui.Secondary("用当天(留空)", (_, _) =>
        {
            Save(d => d.SignDate = "");
            Overlay.CloseActive();
        });
        DockPanel.SetDock(useToday, Dock.Left);
        row.Children.Add(useToday);
        var ok = Ui.Primary("就用这天", (_, _) =>
        {
            Save(d => d.SignDate = picked.ToString(ReplyState.SignDateFormat));
            Overlay.CloseActive();
        });
        DockPanel.SetDock(ok, Dock.Right);
        row.Children.Add(ok);
        body.Children.Add(row);
        Flyout.Show(_dateField, "署名日期", body, width: 300);
    }

    /// <summary>刷新动作排 —— 只改属性,一个控件都不重挂。</summary>
    void RefreshActions(ReplyDoc d, bool busy)
    {
        var paper = d.Medium == ReplyMedium.Paper;
        _dateWrap.Visibility = paper ? Visibility.Visible : Visibility.Collapsed;
        _pdfBtn.Visibility = paper ? Visibility.Visible : Visibility.Collapsed;
        _sendBtn.Visibility = d.Medium == ReplyMedium.Email ? Visibility.Visible : Visibility.Collapsed;
        _copyBtn.ToolTip = paper ? "复制格式文本" : "复制";
        _gen.Content = busy ? "生成中…" : "生成";
        _gen.IsEnabled = !busy;
        var set = d.SignDate.Trim().Length > 0;
        _dateText.Text = set ? d.SignDate.Trim() : "署名日期 · 空=当天";
        _dateText.SetResourceReference(TextBlock.ForegroundProperty, set ? "FgPrimary" : "FgMuted");
    }


}
