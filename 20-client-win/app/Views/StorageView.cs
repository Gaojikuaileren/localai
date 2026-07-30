// P3c -- 设置 › 存储与清理 + 记忆库编辑。
//
// 用户裁定(2026-07-30):
//   · 一个【一键清爽】按钮,下面用勾选决定它执行哪些动作;
//     ★ 危险项(删除归档原文)默认【不勾】,勾了执行前仍单独确认并列出将删什么 —— 对应"永不删原文"的决议;
//   · 摘要必须由 AI 生成,默认 AI 自行判断,可切成手动触发(手动就在这里点);
//   · 会话整理阈值可在这里改;
//   · 记忆编辑板块:以摘要形式预览、手动删减、置顶;自动清理规则先预演再删。
//
// ★ 诚实:AI 未接入(P4)。所以"整理摘要"现在【不做任何事】并如实说明;记忆库为空也如实显示。
//   占用数字全部是真读文件算的,不估不编。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class StorageView : UserControl
{
    App TheApp => (App)Application.Current;

    readonly StackPanel _root = new();
    readonly StackPanel _usage = new();
    readonly StackPanel _memList = new();
    readonly HashSet<string> _picked = new();
    TextBlock? _status;

    public StorageView()
    {
        Content = new ScrollViewer
        {
            Content = _root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough();
        Build();
        Loaded += (_, _) => TheApp.Memory.Changed += OnMemChanged;
        Unloaded += (_, _) => TheApp.Memory.Changed -= OnMemChanged;
    }

    void OnMemChanged() { RefreshUsage(); RefreshMemory(); }

    void Build()
    {
        _root.Children.Clear();
        _root.Children.Add(UsageCard());
        _root.Children.Add(TidyCard());
        _root.Children.Add(SummaryCard());
        _root.Children.Add(MemoryCard());
        RefreshUsage();
        RefreshMemory();
    }

    // ---------------------------------------------------------------- 占用一览(真数字)
    UIElement UsageCard()
        => Ui.Card(Ui.Stack(
            Ui.Subtitle("占用一览"),
            Ui.Caption("下面是本机实际读到的大小 —— 不是估算。"),
            _usage));

    void RefreshUsage()
    {
        _usage.Children.Clear();
        foreach (var it in StorageUsage.Snapshot(TheApp.Memory.Bytes()))
        {
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 6, 0, 0) };
            var size = new TextBlock { Text = StorageUsage.Human(it.Bytes), VerticalAlignment = VerticalAlignment.Center, MinWidth = 80, TextAlignment = TextAlignment.Right };
            size.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            DockPanel.SetDock(size, Dock.Right);
            row.Children.Add(size);
            var col = new StackPanel();
            var lab = new TextBlock { Text = it.Label };
            lab.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
            col.Children.Add(lab);
            col.Children.Add(Ui.Caption(it.Note));
            row.Children.Add(col);
            _usage.Children.Add(row);
        }
    }

    // ---------------------------------------------------------------- 一键清爽 + 勾选它做什么
    UIElement TidyCard()
    {
        var s = TheApp.Settings;
        _status = Ui.Caption("");

        var cbCache = Check("清理缓存(剪贴板预览图、临时与损坏文件)", s.TidyClearCache, v => { s.TidyClearCache = v; s.Save(); });
        var cbSum = Check("整理摘要(把长会话交给 AI 归纳)", s.TidySummarize, v => { s.TidySummarize = v; s.Save(); });
        var cbMem = Check("按规则清理记忆库(会先列出将删哪些)", s.TidyCleanMemory, v => { s.TidyCleanMemory = v; s.Save(); });
        var cbDel = Check("删除归档原文(不可逆)", s.TidyDeleteArchivedOriginals, v => { s.TidyDeleteArchivedOriginals = v; s.Save(); });
        // 危险项标红,和其它三项在视觉上分开
        if (cbDel.Content is string txt) cbDel.Content = txt;
        cbDel.SetResourceReference(ForegroundProperty, "RiskDanger");

        var run = Ui.Primary("一键清爽", (_, _) => RunTidy());

        return Ui.Card(Ui.Stack(
            Ui.Subtitle("一键清爽"),
            Ui.Caption("保持程序与内存清爽。下面勾选这个按钮要执行的内容。"),
            new Border { Height = 6 },
            cbCache, cbSum, cbMem, cbDel,
            Ui.Caption("★「删除归档原文」不可逆:摘要会留在记忆库,但原始对话删了就回不来了 —— 执行前会再确认一次。"),
            new Border { Height = 8 },
            run,
            _status));
    }

    void RunTidy()
    {
        var s = TheApp.Settings;
        var done = new List<string>();

        if (s.TidyClearCache)
        {
            var (files, bytes) = StorageUsage.ClearCache();
            done.Add(files == 0 ? "缓存:已经很干净" : $"缓存:清掉 {files} 个文件({StorageUsage.Human(bytes)})");
        }

        if (s.TidySummarize)
        {
            // ★ 诚实:AI 未接入,这里【什么都不做】,绝不拿本地规则拼假摘要。
            done.Add("整理摘要:AI 尚未接入(P4),本次未执行");
        }

        if (s.TidyCleanMemory)
        {
            var plan = TheApp.Memory.PlanAutoClean(s.MemoryAutoCleanDays, s.MemoryMaxMB * 1024L * 1024L);
            if (plan.Count == 0) done.Add("记忆库:没有符合清理规则的条目");
            else if (ConfirmDialog.Show("清理记忆库",
                        $"将删除 {plan.Count} 条记忆:\n\n" + string.Join("\n", plan.Take(10).Select(m => "· " + m.Title))
                        + (plan.Count > 10 ? $"\n…… 另有 {plan.Count - 10} 条" : "")
                        + "\n\n置顶的与事实/偏好类不会被清理。",
                        confirmText: "删除这些", danger: true))
            {
                var n = TheApp.Memory.ApplyClean(plan);
                done.Add($"记忆库:清掉 {n} 条");
            }
            else done.Add("记忆库:已取消");
        }

        if (s.TidyDeleteArchivedOriginals)
        {
            var dir = StorageUsage.ArchiveDir;
            if (!System.IO.Directory.Exists(dir)) done.Add("归档原文:没有归档(分层存储待接入)");
            else if (ConfirmDialog.Show("删除归档原文",
                        $"彻底删除已归档的会话原文?\n\n位置:{dir}\n\n★ 不可逆。摘要仍留在记忆库,但原始对话删了无法恢复。",
                        confirmText: "永久删除", danger: true))
            {
                try
                {
                    System.IO.Directory.Delete(dir, recursive: true);
                    done.Add("归档原文:已删除(摘要仍在记忆库)");
                }
                catch (Exception ex) { done.Add("归档原文:删除失败 —— " + ex.Message); }
            }
            else done.Add("归档原文:已取消");
        }

        if (done.Count == 0) done.Add("没有勾选任何动作。");
        if (_status is not null) _status.Text = string.Join(";", done);
        RefreshUsage();
    }

    // ---------------------------------------------------------------- 摘要触发方式 + 阈值
    UIElement SummaryCard()
    {
        var s = TheApp.Settings;

        var mode = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        mode.Items.Add("AI 自行判断何时整理(默认)");
        mode.Items.Add("只在这里手动触发");
        mode.SelectedIndex = s.SummaryTrigger == "manual" ? 1 : 0;
        mode.SelectionChanged += (_, _) => { s.SummaryTrigger = mode.SelectedIndex == 1 ? "manual" : "ai"; s.Save(); };

        var thr = new TextBox { Text = s.SummaryThresholdChars.ToString(), Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 4, 0, 0) };
        thr.LostFocus += (_, _) =>
        {
            if (int.TryParse(thr.Text.Trim(), out var v) && v >= 0) { s.SummaryThresholdChars = v; s.Save(); }
            thr.Text = s.SummaryThresholdChars.ToString();
        };

        var manual = Ui.Secondary("立即整理并生成摘要", (_, _) =>
            ConfirmDialog.Show("尚未接入 AI",
                "摘要必须由 AI 生成,而模型还没接入(P4 GPU Broker)。\n\n接入后这里会真正整理会话并写入记忆库;" +
                "在那之前不会拿本地规则拼一段假摘要冒充。",
                confirmText: "好", cancelText: "关闭"));

        return Ui.Card(Ui.Stack(
            Ui.Subtitle("会话整理与摘要"),
            Ui.Body("触发方式"), mode,
            new Border { Height = 8 },
            Ui.Body("整理阈值(字符数估算)"), thr,
            Ui.Caption("会话累计超过这个量就该整理。★ 真正的约束是模型上下文窗口;模型未接入前先用字符数估算,接入后换成真 token 计数。0 = 不提醒。"),
            new Border { Height = 8 },
            manual,
            Ui.Caption("★ 原文永不自动删除 —— 摘要只是索引,原始对话一直留着,除非你在上面显式删除归档原文。")));
    }

    // ---------------------------------------------------------------- 记忆库编辑
    UIElement MemoryCard()
    {
        var s = TheApp.Settings;

        var days = new TextBox { Text = s.MemoryAutoCleanDays.ToString(), Width = 120, Padding = new Thickness(8, 5, 8, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        days.LostFocus += (_, _) => { if (int.TryParse(days.Text.Trim(), out var v) && v >= 0) { s.MemoryAutoCleanDays = v; s.Save(); } days.Text = s.MemoryAutoCleanDays.ToString(); };
        var maxMb = new TextBox { Text = s.MemoryMaxMB.ToString(), Width = 120, Padding = new Thickness(8, 5, 8, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        maxMb.LostFocus += (_, _) => { if (int.TryParse(maxMb.Text.Trim(), out var v) && v >= 0) { s.MemoryMaxMB = v; s.Save(); } maxMb.Text = s.MemoryMaxMB.ToString(); };

        var delSel = Ui.DangerFilled("删除所选", (_, _) =>
        {
            if (_picked.Count == 0) return;
            if (!ConfirmDialog.Show("删除记忆", $"删除选中的 {_picked.Count} 条记忆?此操作不可撤销。", confirmText: "删除", danger: true)) return;
            TheApp.Memory.RemoveMany(_picked);
            _picked.Clear();
        });
        delSel.HorizontalAlignment = HorizontalAlignment.Left;

        return Ui.Card(Ui.Stack(
            Ui.Subtitle("记忆库"),
            Ui.Caption("AI 生成的摘要与事实。可以逐条预览、置顶、删减 —— 置顶的永不被自动清理。"),
            new Border { Height = 6 },
            Ui.Body("多少天没被用到就清理(0 = 关闭)"), days,
            Ui.Body("总量上限 MB(0 = 不限)"), maxMb,
            Ui.Caption("★ 自动清理只动【摘要】;事实/偏好类与置顶的永远不动,且执行前先列出将删哪些。"),
            new Border { Height = 10 },
            _memList,
            delSel));
    }

    void RefreshMemory()
    {
        _memList.Children.Clear();
        var items = TheApp.Memory.Visible().ToList();
        if (items.Count == 0)
        {
            // ★ 诚实的空态:不是"还没整理",而是根本还没有 AI 来产生记忆
            _memList.Children.Add(Ui.Body("记忆库是空的。", muted: true));
            _memList.Children.Add(Ui.Caption("摘要必须由 AI 生成,而模型尚未接入(P4)—— 接入后整理出的摘要会出现在这里。"));
            return;
        }
        foreach (var m in items) _memList.Children.Add(MemoryRow(m));
    }

    FrameworkElement MemoryRow(MemoryEntry m)
    {
        var cb = new CheckBox { IsChecked = _picked.Contains(m.Id), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0) };
        cb.Checked += (_, _) => _picked.Add(m.Id);
        cb.Unchecked += (_, _) => _picked.Remove(m.Id);

        var title = new TextBlock { Text = m.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        title.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        // 预览:摘要正文的前两行
        var preview = new TextBlock { Text = m.Body, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        preview.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        preview.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var meta = Ui.Caption($"{m.Kind} · {ProjectUi.ScopeLabel(m.Scope)} · {m.CreatedAt:M月d日}"
                              + (m.SourceOriginalsDeleted ? " · 原文已删除(只剩摘要)" : ""));

        var col = new StackPanel();
        col.Children.Add(title);
        col.Children.Add(preview);
        col.Children.Add(meta);

        var pin = Chip(m.Pinned ? "已置顶" : "置顶", m.Pinned ? "Accent" : "FgSecondary", () => TheApp.Memory.TogglePin(m.Id));

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 6, 0, 6) };
        DockPanel.SetDock(cb, Dock.Left);
        DockPanel.SetDock(pin, Dock.Right);
        row.Children.Add(cb);
        row.Children.Add(pin);
        row.Children.Add(col);
        return row;
    }

    static CheckBox Check(string text, bool value, Action<bool> onChange)
    {
        var cb = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 4, 0, 0) };
        cb.Checked += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);
        return cb;
    }

    static FrameworkElement Chip(string text, string colorKey, Action onClick)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border { Child = t, Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(8, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.SetResourceReference(Border.BorderBrushProperty, "Border");
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }
}
