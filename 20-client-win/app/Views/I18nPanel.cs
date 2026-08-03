// P3c -- 多语言表场景(D60,版式经两轮用户裁定收敛):
//   会话区 = 【可编辑网格】:行 = 键,列 = 键 | 源文 | 每种目标语言。
//   每个格子直接编辑(占位符坏的当场红边);【键列只读】—— 格式与标题是 AI/导入定的,
//   人只改内容不改结构。语言与工具在底部横条(TranslationBar 的多语言卡)。
//   「JSON 源码」= 整表直编覆盖层(粘外部 AI 产物的通道,应用前强校验)。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public sealed class I18nPanel : UserControl
{
    static App TheApp => (App)Application.Current;
    readonly Grid _grid = new();
    readonly TextBox _raw = new() { AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                    FontFamily = new FontFamily("Consolas"), Visibility = Visibility.Collapsed };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    bool _editing;   // 正在格子里打字 -> Touch 引发的重建跳过,不打断输入

    TextBlock MakeAddLang()
    {
        var t = new TextBlock { Text = "+", FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center,
                                Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(6, 2, 6, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        t.MouseLeftButtonUp += (_, e3) => { e3.Handled = true; I18nLangPicker.Show(t); };
        return t;
    }
    TextBlock? _addLangBtnBack;
    TextBlock _addLangBtn => _addLangBtnBack ??= MakeAddLang();

    readonly TextBox _pathBox = new() { IsReadOnly = true, BorderThickness = new Thickness(0),
        Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.NoWrap, Visibility = Visibility.Collapsed };

    public I18nPanel()
    {
        // ---- 顶行:左 = 导入文件路径(只读 TextBox,可选中复制),右 = 导入/导出(用户裁定 2026-08-03)
        var topBar = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(Ui.Secondary("导入 JSON", (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json" };
            if (dlg.ShowDialog() != true) return;
            var st0 = TheApp.I18n;
            var n = st0.ImportJson(System.IO.File.ReadAllText(dlg.FileName));
            if (n >= 0) st0.Doc.SourcePath = dlg.FileName;
            st0.SetStatus(n < 0 ? "解析失败:不是合法 JSON。" : $"读入 {n} 条词条。", n < 0);
        }));
        var exp = Ui.Secondary("导出 JSON", (_, _) =>
        {
            // ★ 单文件导出(用户裁定 2026-08-03,推翻一源两出):一个完整对照 JSON,AI 直接读
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "JSON|*.json", FileName = "strings.i18n.json" };
            if (dlg.ShowDialog() != true) return;
            var (ok, msg) = TheApp.I18n.Export(dlg.FileName);
            TheApp.I18n.SetStatus(msg, !ok);
        });
        exp.Margin = new Thickness(6, 0, 0, 0);
        btns.Children.Add(exp);
        DockPanel.SetDock(btns, Dock.Right);
        topBar.Children.Add(btns);
        _pathBox.SetResourceReference(TextBox.ForegroundProperty, "FgMuted");
        _pathBox.SetResourceReference(TextBox.FontSizeProperty, "FontCaption");
        topBar.Children.Add(_pathBox);

        // ★ 常态【表格线】(用户裁定):外框走左/上边,单元格补右/下边(见 Line)—— 线不叠加
        var gridFrame = new Border { Child = _grid, BorderThickness = new Thickness(1, 1, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        gridFrame.SetResourceReference(Border.BorderBrushProperty, "Border");
        var gridScroll = new ScrollViewer { Content = gridFrame,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };   // 语言不限量 -> 横向可滚

        var stage = new Grid();
        stage.Children.Add(gridScroll);
        stage.Children.Add(_raw);

        _status.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _status.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(topBar, Dock.Top); root.Children.Add(topBar);
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        root.Children.Add(stage);
        Content = root;

        _raw.TextChanged += (_, _) => TheApp.I18n.RawText = _raw.Text;   // 底条「应用源码」读它
        Loaded += (_, _) => { TheApp.I18n.Changed += Rebuild; Rebuild(); };
        Unloaded += (_, _) => TheApp.I18n.Changed -= Rebuild;
    }

    void Rebuild()
    {
        if (_editing) { RefreshStatus(); return; }   // 打字中的 Touch 不重建 —— 否则每敲一键焦点就没了
        RefreshStatus();
        var st = TheApp.I18n;
        if (st.RawMode && _raw.Visibility != Visibility.Visible)
        { _raw.Text = st.RawText; _raw.Visibility = Visibility.Visible; }
        else if (!st.RawMode && _raw.Visibility == Visibility.Visible)
            _raw.Visibility = Visibility.Collapsed;

        _grid.Children.Clear();
        _grid.ColumnDefinitions.Clear();
        _grid.RowDefinitions.Clear();

        var langs = st.Doc.TargetLangs;
        // 列:键 | 源 | 每语言
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        foreach (var _ in langs) _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });   // + 号列

        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Header("键", 0); Header("源 · " + st.Doc.SourceLang, 1);
        for (int c = 0; c < langs.Count; c++)
        {
            // ★ 语言列标题自带删除入口(用户裁定 2026-08-03):点「×」摘掉该语言列 ——
            //   自定义码也由此删;已填的译文仍留在词条里,重新勾选即恢复显示。
            var lc = langs[c];
            var hRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 6, 4) };
            var hT = new TextBlock { Text = lc, FontWeight = FontWeights.SemiBold };
            hT.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            hT.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            hRow.Children.Add(hT);
            var hX = new TextBlock { Text = " ×", Cursor = System.Windows.Input.Cursors.Hand };
            hX.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            hX.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            hX.MouseLeftButtonUp += (_, e5) => { e5.Handled = true; TheApp.I18n.RemoveLang(lc); };
            hX.MouseEnter += (_, _) => hX.SetResourceReference(TextBlock.ForegroundProperty, "RiskDanger");
            hX.MouseLeave += (_, _) => hX.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            hRow.Children.Add(hX);
            var hCell = Line(hRow);
            Grid.SetRow(hCell, 0); Grid.SetColumn(hCell, c + 2);
            _grid.Children.Add(hCell);
        }
        // ★ 表头末尾「+」= 加语言(用户裁定):开底部的目标语言抽屉,勾选即加列
        // ★ 「+」用字段(锚点要稳定):浮窗开着时表格重建,锚点若是新建控件,浮窗就跟着"飞了"。
        //   重挂前先断旧父(WPF 两父节点)。
        // ★ 旧父可能是 Panel 也可能是 Line() 包的 Border —— 两种都要断,断不干净就是两父节点崩溃
        if (_addLangBtn.Parent is Border ob) ob.Child = null;
        else (_addLangBtn.Parent as Panel)?.Children.Remove(_addLangBtn);
        var addCell = Line(_addLangBtn);
        Grid.SetRow(addCell, 0); Grid.SetColumn(addCell, 2 + langs.Count);
        _grid.Children.Add(addCell);

        // ★ 空表也照常显示表格(用户裁定 2026-08-03):没导入任何 JSON 时从下面那行直接开工,
        //   填完点「导出」就得到一份相应内容的 JSON 文件 —— 从零建表和导入改表是同一条路。
        int r = 1;
        foreach (var e in st.Doc.Entries)
        {
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // 键列【可编辑】(用户改裁定 2026-08-03):回车/失焦提交;空或与别的键重复 -> 还原不动
            var keyBox = new TextBox { Text = e.Key, VerticalAlignment = VerticalAlignment.Center,
                                       Padding = new Thickness(4, 2, 4, 2), BorderThickness = new Thickness(0),
                                       Background = Brushes.Transparent };
            keyBox.SetResourceReference(TextBox.ForegroundProperty, "FgSecondary");
            var oldKey = e.Key;
            void CommitKey()
            {
                if (keyBox.Text.Trim() == oldKey) return;
                if (!TheApp.I18n.RenameKey(oldKey, keyBox.Text))
                { keyBox.Text = oldKey; TheApp.I18n.SetStatus("键没有改:不能为空、也不能与已有键重复。", true); }
            }
            keyBox.KeyDown += (_, e4) => { if (e4.Key == System.Windows.Input.Key.Enter) { CommitKey(); e4.Handled = true; } };
            keyBox.LostFocus += (_, _) => CommitKey();
            var kc = Line(keyBox);
            Grid.SetRow(kc, r); Grid.SetColumn(kc, 0);
            _grid.Children.Add(kc);

            // 源文:可编辑(改错别字是人的活;键与结构不动)
            var entry = e;
            Cell(r, 1, e.Source, v =>
            {
                var i = st.Doc.Entries.IndexOf(entry);
                if (i >= 0) { st.Doc.Entries[i] = entry with { Source = v }; entry = st.Doc.Entries[i]; }
            }, () => entry.Source);

            for (int c = 0; c < langs.Count; c++)
            {
                var lang = langs[c];
                Cell(r, c + 2, e.Trans.GetValueOrDefault(lang, ""), v => entry.Trans[lang] = v,
                     () => entry.Source, checkPlaceholder: true);
            }
            r++;
        }

        // 末尾常驻【新词条】行:键列在这一行可编辑(手建的键当然人来起;已导入的键仍只读)
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var newKey = new TextBox { Margin = new Thickness(1), Padding = new Thickness(4, 2, 4, 2),
                                   BorderThickness = new Thickness(1) };
        newKey.SetResourceReference(TextBox.BorderBrushProperty, "Border");
        var hint = Ui.Caption("+ 新词条:输入键名后回车");
        void Commit()
        {
            if (TheApp.I18n.AddEntry(newKey.Text)) newKey.Text = "";
        }
        newKey.KeyDown += (_, e2) => { if (e2.Key == System.Windows.Input.Key.Enter) { Commit(); e2.Handled = true; } };
        newKey.LostFocus += (_, _) => { if (newKey.Text.Trim().Length > 0) Commit(); };
        newKey.BorderThickness = new Thickness(0);
        var nkc = Line(newKey);
        Grid.SetRow(nkc, r); Grid.SetColumn(nkc, 0);
        _grid.Children.Add(nkc);
        hint.VerticalAlignment = VerticalAlignment.Center; hint.Margin = new Thickness(6, 2, 6, 2);
        var hc = Line(hint);
        Grid.SetRow(hc, r); Grid.SetColumn(hc, 1); Grid.SetColumnSpan(hc, 1 + Math.Max(1, langs.Count));
        _grid.Children.Add(hc);
    }

    void Header(string text, int col)
    {
        var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 2, 6, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var cellH = Line(t);
        Grid.SetRow(cellH, 0); Grid.SetColumn(cellH, col);
        _grid.Children.Add(cellH);
    }

    /// <summary>一个可编辑格子。checkPlaceholder = 与源文比对占位符,坏的红边(导出时硬拦)。</summary>
    void Cell(int row, int col, string text, Action<string> save, Func<string> sourceOf, bool checkPlaceholder = false)
    {
        var tb = new TextBox { Text = text, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                               Padding = new Thickness(4, 2, 4, 2),
                               BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        tb.TextChanged += (_, _) =>
        {
            save(tb.Text);
            // 占位符坏 -> 浅红底(格线归 Line 包边管,别再抢边框)
            tb.Background = checkPlaceholder && !I18nState.PlaceholdersOk(sourceOf(), tb.Text)
                ? new SolidColorBrush(Color.FromArgb(45, 205, 92, 92)) : Brushes.Transparent;
            _editing = true;                 // 打字中的 Touch 不整表重建(见 Rebuild)
            try { TheApp.I18n.Touch(); }     // 完成度/落盘照走
            finally { _editing = false; }
        };
        var cellB = Line(tb);
        Grid.SetRow(cellB, row); Grid.SetColumn(cellB, col);
        _grid.Children.Add(cellB);
    }

    void RefreshStatus()
    {
        var st = TheApp.I18n;
        _status.Text = st.StatusLine;
        _status.SetResourceReference(TextBlock.ForegroundProperty, st.StatusWarn ? "RiskWarning" : "FgMuted");
        // 路径行:导入过才显示;只读 TextBox = 可选中可复制
        var sp = st.Doc.SourcePath;
        var missing = !string.IsNullOrEmpty(sp) && !System.IO.File.Exists(sp);
        _pathBox.Text = missing ? $"文件丢失:{sp} —— 表内容仍在;点「导入 JSON」重新指到同一份文件即可。" : sp ?? "";
        _pathBox.SetResourceReference(TextBox.ForegroundProperty, missing ? "RiskWarning" : "FgMuted");
        _pathBox.Visibility = string.IsNullOrEmpty(sp) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>包一层右/下 1px 边 —— 与外框的左/上边拼成完整表格线。</summary>
    Border Line(FrameworkElement el)
    {
        var b = new Border { Child = el, BorderThickness = new Thickness(0, 0, 1, 1) };
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        return b;
    }
}
