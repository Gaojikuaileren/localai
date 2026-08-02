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

    public I18nPanel()
    {
        var gridScroll = new ScrollViewer { Content = _grid,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };   // 语言不限量 -> 横向可滚

        var stage = new Grid();
        stage.Children.Add(gridScroll);
        stage.Children.Add(_raw);

        _status.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _status.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var root = new DockPanel { LastChildFill = true };
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

        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Header("键", 0); Header("源 · " + st.Doc.SourceLang, 1);
        for (int c = 0; c < langs.Count; c++) Header(langs[c], c + 2);

        if (st.Doc.Entries.Count == 0)
        {
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var empty = Ui.Caption("还没有词条 —— 底部「导入 JSON」,或「JSON 源码」整表粘贴。");
            Grid.SetRow(empty, 1); Grid.SetColumn(empty, 0); Grid.SetColumnSpan(empty, 2 + Math.Max(1, langs.Count));
            empty.Margin = new Thickness(6);
            _grid.Children.Add(empty);
            return;
        }

        int r = 1;
        foreach (var e in st.Doc.Entries)
        {
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // 键列【只读】:格式与标题是 AI/导入定的(用户裁定 2026-08-03)
            var k = new TextBlock { Text = e.Key, TextTrimming = TextTrimming.CharacterEllipsis,
                                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 2, 6, 2) };
            k.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            Grid.SetRow(k, r); Grid.SetColumn(k, 0);
            _grid.Children.Add(k);

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
    }

    void Header(string text, int col)
    {
        var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 2, 6, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        Grid.SetRow(t, 0); Grid.SetColumn(t, col);
        _grid.Children.Add(t);
    }

    /// <summary>一个可编辑格子。checkPlaceholder = 与源文比对占位符,坏的红边(导出时硬拦)。</summary>
    void Cell(int row, int col, string text, Action<string> save, Func<string> sourceOf, bool checkPlaceholder = false)
    {
        var tb = new TextBox { Text = text, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                               Margin = new Thickness(1), Padding = new Thickness(4, 2, 4, 2),
                               BorderThickness = new Thickness(1), Background = Brushes.Transparent };
        tb.SetResourceReference(TextBox.BorderBrushProperty, "Border");
        tb.TextChanged += (_, _) =>
        {
            save(tb.Text);
            if (checkPlaceholder && !I18nState.PlaceholdersOk(sourceOf(), tb.Text)) tb.BorderBrush = Brushes.IndianRed;
            else tb.SetResourceReference(TextBox.BorderBrushProperty, "Border");
            _editing = true;                 // 打字中的 Touch 不整表重建(见 Rebuild)
            try { TheApp.I18n.Touch(); }     // 完成度/落盘照走
            finally { _editing = false; }
        };
        Grid.SetRow(tb, row); Grid.SetColumn(tb, col);
        _grid.Children.Add(tb);
    }

    void RefreshStatus()
    {
        var st = TheApp.I18n;
        _status.Text = st.StatusLine;
        _status.SetResourceReference(TextBlock.ForegroundProperty, st.StatusWarn ? "RiskWarning" : "FgMuted");
    }
}
