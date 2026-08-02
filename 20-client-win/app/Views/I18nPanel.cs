// P3c -- 多语言表场景(D60):键清单 + 详情(方案 C,用户选定)。
//   顶栏:源语言 · 目标语言 chips(不限量,+ 搜索添加)· 导入 / 导出 / JSON 源码 / 翻译缺失
//   左:键清单(完成度徽标);右:选中键的全部语言纵向逐条编辑(占位符坏的当场标红)。
//   「JSON 源码」= 整表直编(用户裁定):粘贴外部 AI 产物的通道 —— 应用前强校验,非法拒绝。
// ★ 诚实:引擎未接(P4),「翻译缺失」如实说;人工/粘贴填译文本身已可用。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public sealed class I18nPanel : UserControl
{
    static App TheApp => (App)Application.Current;
    readonly StackPanel _keys = new();
    readonly StackPanel _detail = new();
    readonly StackPanel _langChips = new();
    readonly TextBox _raw = new() { AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                    FontFamily = new FontFamily("Consolas"), Visibility = Visibility.Collapsed };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    bool _rawMode;

    public I18nPanel()
    {
        var st = TheApp.I18n;

        // ---- 顶栏 ----
        var top = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        top.Children.Add(_langChips);
        var addBox = new TextBox { Width = 90, ToolTip = "语言码,如 en / ja / pt-BR" };
        var add = Ui.Secondary("+ 语言", (_, _) =>
        {
            var code = addBox.Text.Trim();
            if (code.Length == 0) return;
            st.AddLang(code); addBox.Text = "";
        });
        top.Children.Add(addBox); top.Children.Add(add);
        top.Children.Add(Ui.Secondary("导入 JSON", (_, _) => ImportFile()));
        top.Children.Add(Ui.Secondary("导出(一源两出)", (_, _) => ExportAll()));
        top.Children.Add(Ui.Secondary("JSON 源码", (_, _) => ToggleRaw()));
        top.Children.Add(Ui.Secondary("翻译缺失项", (_, _) =>
            ConfirmDialog.Show("还不能自动翻译",
                "翻译引擎(P4)还没接入 —— 表、语言与已填的译文都会保留。\n先用「JSON 源码」把外部 AI 的产物粘进来,或逐条手填。",
                confirmText: "知道了", cancelText: "关闭")));

        // ---- 主从 ----
        var keysScroll = new ScrollViewer { Content = _keys, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var detailScroll = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        split.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(keysScroll, 0); split.Children.Add(keysScroll);
        Grid.SetColumn(detailScroll, 1); split.Children.Add(detailScroll);
        detailScroll.Margin = new Thickness(8, 0, 0, 0);

        var stage = new Grid();
        stage.Children.Add(split);
        stage.Children.Add(_raw);

        _status.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _status.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        root.Children.Add(stage);
        Content = root;

        Loaded += (_, _) => { TheApp.I18n.Changed += Rebuild; Rebuild(); };
        Unloaded += (_, _) => TheApp.I18n.Changed -= Rebuild;
    }

    void ImportFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json" };
        if (dlg.ShowDialog() != true) return;
        var n = TheApp.I18n.ImportJson(System.IO.File.ReadAllText(dlg.FileName));
        _status.Text = n < 0 ? "解析失败:不是合法 JSON。" : $"读入 {n} 条词条。";
    }

    void ExportAll()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog() != true) return;
        var (ok, msg) = TheApp.I18n.Export(dlg.FolderName);
        _status.Text = msg;
        _status.SetResourceReference(TextBlock.ForegroundProperty, ok ? "FgMuted" : "RiskWarning");
    }

    void ToggleRaw()
    {
        var st = TheApp.I18n;
        if (!_rawMode)
        {
            // 进源码视图:把当前表序列化出来(对照表形状,与导出①一致)
            var table = new SortedDictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var e in st.Doc.Entries)
            {
                var row = new Dictionary<string, string> { ["@src"] = st.Doc.SourceLang, [st.Doc.SourceLang] = e.Source };
                foreach (var l in st.Doc.TargetLangs) if (e.Trans.TryGetValue(l, out var t) && t.Length > 0) row[l] = t;
                table[e.Key] = row;
            }
            _raw.Text = System.Text.Json.JsonSerializer.Serialize(table,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            _raw.Visibility = Visibility.Visible; _rawMode = true;
            _status.Text = "源码视图:改完再点一次「JSON 源码」应用 —— 非法 JSON 会被拒绝,不会吞掉半张表。";
        }
        else
        {
            // ★ 应用前强校验:解析失败就不动表 —— 「AI 直接读不出错」从入口就守起
            var n = st.ImportJson(_raw.Text);
            if (n < 0) { _status.Text = "改动没有应用:不是合法 JSON(检查逗号/引号/花括号)。"; _status.SetResourceReference(TextBlock.ForegroundProperty, "RiskWarning"); return; }
            _raw.Visibility = Visibility.Collapsed; _rawMode = false;
            _status.Text = $"已应用:{n} 条词条。";
            _status.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        }
    }

    void Rebuild()
    {
        var st = TheApp.I18n;
        // 语言 chips:源语言实心 + 目标语言描边(点删)
        _langChips.Children.Clear();
        _langChips.Orientation = Orientation.Horizontal;
        _langChips.Children.Add(Chip($"源:{st.Doc.SourceLang}", true, null));
        foreach (var l in st.Doc.TargetLangs)
        { var cap = l; _langChips.Children.Add(Chip(cap + " ×", false, () => st.RemoveLang(cap))); }

        // 键清单
        _keys.Children.Clear();
        if (st.Doc.Entries.Count == 0)
            _keys.Children.Add(Ui.Caption("还没有词条 —— 导入 JSON,或用「JSON 源码」粘贴。"));
        foreach (var e in st.Doc.Entries)
        {
            var (done, total) = st.Progress(e);
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1, 0, 1) };
            var badge = Ui.Caption($"{done}/{total}");
            badge.SetResourceReference(TextBlock.ForegroundProperty,
                total > 0 && done == total ? "RiskSafe" : "FgMuted");
            DockPanel.SetDock(badge, Dock.Right);
            row.Children.Add(badge);
            var k = new TextBlock { Text = e.Key, TextTrimming = TextTrimming.CharacterEllipsis };
            k.SetResourceReference(TextBlock.ForegroundProperty, st.SelectedKey == e.Key ? "Accent" : "FgPrimary");
            row.Children.Add(k);
            var host = new Border { Child = row, Padding = new Thickness(6, 3, 6, 3), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent };
            host.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
            var key = e.Key;
            host.MouseLeftButtonUp += (_, _) => st.SelectKey(key);
            _keys.Children.Add(host);
        }

        // 详情:选中键的全部语言纵向排开
        _detail.Children.Clear();
        var cur = st.Doc.Entries.FirstOrDefault(x => x.Key == st.SelectedKey);
        if (cur is null) { _detail.Children.Add(Ui.Caption("左边选一个键。")); return; }
        _detail.Children.Add(Ui.Body(cur.Key));
        var src = Ui.Caption($"源({st.Doc.SourceLang}):{cur.Source}");
        src.TextWrapping = TextWrapping.Wrap;
        _detail.Children.Add(src);
        var ph = I18nState.Placeholders(cur.Source);
        if (ph.Length > 0) _detail.Children.Add(Ui.Caption("占位符:" + string.Join(" ", ph) + "(译文必须原样带全)"));
        foreach (var l in st.Doc.TargetLangs)
        {
            var lang = l;
            _detail.Children.Add(Ui.Caption(lang));
            var tb = new TextBox { Text = cur.Trans.GetValueOrDefault(lang, ""), AcceptsReturn = true,
                                   TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
            tb.TextChanged += (_, _) =>
            {
                cur.Trans[lang] = tb.Text;
                // 占位符坏的当场标红边(不弹窗打断输入;导出时硬拦)
                tb.BorderBrush = I18nState.PlaceholdersOk(cur.Source, tb.Text) ? null : Brushes.IndianRed;
                TheApp.I18n.Touch();   // 完成度徽标/落盘跟上
            };
            _detail.Children.Add(tb);
        }
    }

    static FrameworkElement Chip(string text, bool solid, Action? onClick)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, solid ? "FgOnAccent" : "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border { Child = t, Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0, 0, 4, 4),
                             BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        if (solid) { b.SetResourceReference(Border.BackgroundProperty, "Accent"); b.SetResourceReference(Border.BorderBrushProperty, "Accent"); }
        else { b.Background = Brushes.Transparent; b.SetResourceReference(Border.BorderBrushProperty, "Border"); }
        if (onClick is not null) b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }
}
