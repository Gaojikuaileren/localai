// P3c -- 多语言:目标语言选择【浮窗】(D60 六补,用户裁定 2026-08-03)。
//   原先是盖在设置卡上的抽屉 —— 盖住自己的开关、位置固定、塞不下东西。
//   改用库里现成的 Flyout:跟着点击的锚点走(底条按钮 / 网格表头 + 号都能开),
//   有搜索、按全球使用者占比排序、自定义码添加。勾选即生效(状态在 I18nState)。

using System.Windows;
using System.Windows.Controls;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public static class I18nLangPicker
{
    static App TheApp => (App)Application.Current;

    /// <param name="forSource">true = 选【源语言】(单选,点一行即设定并关窗);false = 勾【目标语言】(多选)。
    /// 同一张带占比的清单、同一个浮窗 —— 用户裁定 2026-08-03:源语言也不用干巴巴的下拉。</param>
    public static void Show(FrameworkElement anchor, bool forSource = false)
    {
        var st = TheApp.I18n;
        var body = new StackPanel();

        // 搜索:名字/语言码都能过滤(浮窗有空间,给建全清单的人省翻找)
        var search = new TextBox { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 6) };
        var list = new StackPanel();

        void Fill()
        {
            list.Children.Clear();
            var q = search.Text.Trim();
            var items = Languages.Catalog
                .Where(x => forSource || x.Code != st.Doc.SourceLang)
                .Where(x => q.Length == 0
                            || x.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                            || x.Code.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => I18nState.PercentValue(x.Code))
                .ToList();
            if (items.Count == 0)
                list.Children.Add(Ui.Caption("没有匹配 —— 下面手输语言码也能加(如 pt-BR)。"));
            foreach (var l in items)
            {
                var code = l.Code;
                var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1, 0, 1) };
                var pct = Ui.Caption(I18nState.PercentOf(code));
                DockPanel.SetDock(pct, Dock.Right);
                row.Children.Add(pct);
                if (forSource)
                {
                    // 单选:点一行 = 设为源语言并关窗(当前源打勾标记)
                    var cur = code == st.Doc.SourceLang;
                    var t = new TextBlock { Text = (cur ? "✓ " : "") + $"{l.Name} ({code})",
                                            VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand };
                    t.SetResourceReference(TextBlock.ForegroundProperty, cur ? "Accent" : "FgPrimary");
                    row.Children.Add(t);
                    row.Background = System.Windows.Media.Brushes.Transparent;
                    row.Cursor = System.Windows.Input.Cursors.Hand;
                    row.MouseLeftButtonUp += (_, e) => { e.Handled = true; st.SetSourceLang(code); Flyout.CloseAll(); };
                }
                else
                {
                    var cb = new CheckBox { Content = $"{l.Name} ({code})", IsChecked = st.Doc.TargetLangs.Contains(code) };
                    cb.Checked += (_, _) => st.AddLang(code);
                    cb.Unchecked += (_, _) => st.RemoveLang(code);
                    row.Children.Add(cb);
                }
                list.Children.Add(row);
            }
        }
        search.TextChanged += (_, _) => Fill();
        Fill();

        body.Children.Add(search);
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        // 自定义码(pt-BR / zh-Hant 这类)
        var custom = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 6, 0, 0) };
        var addBox = new TextBox { Padding = new Thickness(6, 3, 6, 3), ToolTip = "语言码,如 pt-BR" };
        var add = Ui.Secondary("+ 添加", (_, _) =>
        { var c = addBox.Text.Trim(); if (c.Length > 0) { st.AddLang(c); addBox.Text = ""; Fill(); } });
        add.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(add, Dock.Right);
        custom.Children.Add(add);
        custom.Children.Add(addBox);
        body.Children.Add(custom);
        body.Children.Add(Ui.Caption("百分比 = 全球使用者占比(静态口径,含二语)。勾选即生效。"));

        Flyout.Show(anchor, forSource ? "源语言" : "目标语言", body, width: 300);
    }
}
