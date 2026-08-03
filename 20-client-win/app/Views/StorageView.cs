// P3c -- 设置 ›「清理缓存 & 自动整理」。
//
// 用户裁定(2026-07-30,措辞与合并):
//   · 板块叫【清理缓存 & 自动整理】,按钮叫【执行上述勾选项】;同板块内显示当前缓存大小;
//   · 会话整理与摘要、记忆库 两块【并入本板块】,不再各占一张卡;
//   · 整理阈值改【滑条】;记忆库总量上限用【阶段滑条】;"多少天没用就清理"改成【选时间】
//     (关闭 / 7 天 / 30 天 / 90 天 / 一年 / 两年 / 三年);
//   · 危险项(删除归档原文)默认【不勾】,勾了执行前仍单独确认 —— 对应"永不删原文"的决议(D52)。
//
// ★ 诚实:AI 未接入(P4)。"整理摘要"现在【不做任何事】并如实说明;记忆库为空也如实说明原因。
//   占用数字全部真读文件算的,不估不编。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class StorageView : UserControl
{
    App TheApp => (App)Application.Current;

    // 整理阈值滑条(字符数估算)。★ 上限 400,000 ≈ 128K token 级模型的量级;
    //   默认 120,000 ≈ 一般 32K 上下文用到六成的水平。模型接入后换真 token 计数(ChatCenter.SizeOf 是唯一换算点)。
    const int ThresholdMin = 20_000, ThresholdMax = 400_000, ThresholdStep = 10_000;

    // 记忆库总量上限的阶段(MB)。0 = 不限。
    static readonly int[] MemoryCaps = { 0, 50, 100, 250, 500, 1024, 2048 };
    static string CapLabel(int mb) => mb == 0 ? "不限" : mb >= 1024 ? (mb / 1024) + " GB" : mb + " MB";

    // 记忆保留期(天)。0 = 关闭。
    static readonly (string Label, int Days)[] Retentions =
    {
        ("关闭", 0), ("7 天", 7), ("30 天", 30), ("90 天", 90), ("一年", 365), ("两年", 730), ("三年", 1095),
    };

    readonly StackPanel _root = new();
    readonly StackPanel _usage = new();
    readonly StackPanel _memList = new();
    readonly StackPanel _retentionRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
    readonly HashSet<string> _picked = new();
    TextBlock? _status, _thresholdLabel, _capLabel;

    public StorageView()
    {
        // 并入设置页的一段,不自带滚动(外层 Ui.Page 已是 ScrollViewer,套两层会吃掉滚轮)
        Content = _root;
        Build();
        Loaded += (_, _) => TheApp.Memory.Changed += OnMemChanged;
        Unloaded += (_, _) => TheApp.Memory.Changed -= OnMemChanged;
    }

    void OnMemChanged() { RefreshUsage(); RefreshMemory(); }

    void Build()
    {
        _root.Children.Clear();
        _root.Children.Add(Ui.Card(Ui.Stack(
            Ui.Subtitle("清理缓存 & 自动整理"),
            Ui.Caption("下面是本机实际读到的大小 —— 不是估算。"),
            _usage,

            Divider(),
            Ui.Body("执行内容(勾选)"),
            Checks(),
            Ui.Caption("★「删除归档原文」不可逆:摘要会留在记忆库,但原始对话删了就回不来了 —— 执行前会再确认一次。"),
            RunRow(),

            Divider(),
            Ui.Body("会话整理与摘要"),
            SummarySection(),

            Divider(),
            Ui.Body("记忆库"),
            MemorySection()
        )));
        RefreshUsage();
        RefreshMemory();
    }

    static UIElement Divider()
    {
        var b = new Border { Height = 1, Margin = new Thickness(0, 14, 0, 12) };
        b.SetResourceReference(Border.BackgroundProperty, "Border");
        return b;
    }

    // ---------------------------------------------------------------- 占用一览(含当前缓存大小)
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

    // ---------------------------------------------------------------- 勾选项 + 执行按钮
    UIElement Checks()
    {
        var s = TheApp.Settings;
        var box = new StackPanel();
        box.Children.Add(Check("清理缓存(剪贴板预览图、临时与损坏文件)", s.TidyClearCache, v => { s.TidyClearCache = v; s.Save(); }));
        box.Children.Add(Check("整理摘要(把长会话交给 AI 归纳)", s.TidySummarize, v => { s.TidySummarize = v; s.Save(); }));
        box.Children.Add(Check("按规则清理记忆库(会先列出将删哪些)", s.TidyCleanMemory, v => { s.TidyCleanMemory = v; s.Save(); }));
        var danger = Check("删除归档原文(不可逆)", s.TidyDeleteArchivedOriginals, v => { s.TidyDeleteArchivedOriginals = v; s.Save(); });
        danger.SetResourceReference(ForegroundProperty, "RiskDanger");
        box.Children.Add(danger);
        return box;
    }

    UIElement RunRow()
    {
        _status = Ui.Caption("");
        var run = Ui.Primary("执行上述勾选项", (_, _) => RunTidy());
        run.HorizontalAlignment = HorizontalAlignment.Left;
        run.Margin = new Thickness(0, 10, 0, 0);
        return Ui.Stack(run, _status);
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
            // ★ 诚实:AI 未接入,这里【什么都不做】,绝不拿本地规则拼假摘要。
            done.Add("整理摘要:AI 尚未接入(P4),本次未执行");

        if (s.TidyCleanMemory)
        {
            var plan = TheApp.Memory.PlanAutoClean(s.MemoryAutoCleanDays, s.MemoryMaxMB * 1024L * 1024L);
            if (plan.Count == 0) done.Add("记忆库:没有符合清理规则的条目");
            else if (ConfirmDialog.Show("清理记忆库",
                        $"将删除 {plan.Count} 条记忆:\n\n" + string.Join("\n", plan.Take(10).Select(m => "· " + m.Title))
                        + (plan.Count > 10 ? $"\n…… 另有 {plan.Count - 10} 条" : "")
                        + "\n\n置顶的与事实/偏好类不会被清理。",
                        confirmText: "删除这些", danger: true))
                done.Add($"记忆库:清掉 {TheApp.Memory.ApplyClean(plan)} 条");
            else done.Add("记忆库:已取消");
        }

        if (s.TidyDeleteArchivedOriginals)
        {
            var dir = StorageUsage.ArchiveDir;
            if (!System.IO.Directory.Exists(dir)) done.Add("归档原文:没有归档");
            else if (ConfirmDialog.Show("删除归档原文",
                        $"彻底删除已归档的会话原文?\n\n位置:{dir}\n\n★ 不可逆。摘要仍留在记忆库,但原始对话删了无法恢复。",
                        confirmText: "永久删除", danger: true))
            {
                try
                {
                    // ★ 删之前先记下涉及哪些会话 —— 删完要给引用它们的记忆打上"原文已删除",不留死链
                    var affected = SessionArchive.ArchivedSessionIds();
                    System.IO.Directory.Delete(dir, recursive: true);
                    TheApp.Memory.MarkOriginalsDeleted(affected);
                    // ★ 计数缓存要跟着清(审计 2026-08-02):否则会话顶上还挂着
                    //   "加载更早的 N 条",点了什么也不发生。
                    TheApp.Chat.InvalidateArchiveCounts();
                    done.Add($"归档原文:已删除(涉及 {affected.Count} 个会话;摘要仍在并已标注原文已删除)");
                }
                catch (Exception ex) { done.Add("归档原文:删除失败 —— " + ex.Message); }
            }
            else done.Add("归档原文:已取消");
        }

        if (done.Count == 0) done.Add("没有勾选任何动作。");
        if (_status is not null) _status.Text = string.Join(";", done);
        RefreshUsage();
    }

    // ---------------------------------------------------------------- 会话整理与摘要
    UIElement SummarySection()
    {
        var s = TheApp.Settings;

        var mode = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        mode.Items.Add("AI 自行判断何时整理(默认)");
        mode.Items.Add("只在这里手动触发");
        mode.SelectedIndex = s.SummaryTrigger == "manual" ? 1 : 0;
        mode.SelectionChanged += (_, _) => { s.SummaryTrigger = mode.SelectedIndex == 1 ? "manual" : "ai"; s.Save(); };

        _thresholdLabel = new TextBlock { Margin = new Thickness(0, 6, 0, 0) };
        _thresholdLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        var slider = new Slider
        {
            Minimum = ThresholdMin, Maximum = ThresholdMax,
            TickFrequency = ThresholdStep, IsSnapToTickEnabled = true,
            Value = Math.Clamp(s.SummaryThresholdChars == 0 ? ThresholdMin : s.SummaryThresholdChars, ThresholdMin, ThresholdMax),
            Width = 320, HorizontalAlignment = HorizontalAlignment.Left,
        };
        void SyncThreshold() => _thresholdLabel.Text = $"整理阈值:约 {(int)slider.Value:N0} 字符";
        slider.ValueChanged += (_, _) => { SyncThreshold(); s.SummaryThresholdChars = (int)slider.Value; s.Save(); };
        SyncThreshold();

        var manual = Ui.Secondary("立即整理并生成摘要", (_, _) =>
            ConfirmDialog.Show("尚未接入 AI",
                "摘要必须由 AI 生成,而模型还没接入(P4 GPU Broker)。\n\n接入后这里会真正整理会话并写入记忆库;" +
                "在那之前不会拿本地规则拼一段假摘要冒充。",
                confirmText: "好", cancelText: "关闭"));
        manual.HorizontalAlignment = HorizontalAlignment.Left;
        manual.Margin = new Thickness(0, 10, 0, 0);

        return Ui.Stack(
            Ui.Caption("触发方式"), mode,
            _thresholdLabel, slider,
            Ui.Caption($"会话累计超过这个量就提示另开一条(可调 {ThresholdMin / 1000}k–{ThresholdMax / 1000}k)。" +
                       "★ 真正的约束是模型上下文窗口;模型未接入前用字符数估算,接入后换真 token 计数。"),
            manual,
            Ui.Caption("★ 原文永不自动删除 —— 摘要只是索引,原始对话一直留着(超出热层的会移到归档,仍是原文)。"));
    }

    // ---------------------------------------------------------------- 记忆库
    UIElement MemorySection()
    {
        var s = TheApp.Settings;

        // 保留期:选时间(关闭 / 7 / 30 / 90 天 / 一年 / 两年 / 三年)
        RefreshRetention();

        // 总量上限:阶段滑条
        _capLabel = new TextBlock { Margin = new Thickness(0, 10, 0, 0) };
        _capLabel.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        var capIdx = Array.IndexOf(MemoryCaps, s.MemoryMaxMB);
        var capSlider = new Slider
        {
            Minimum = 0, Maximum = MemoryCaps.Length - 1,
            TickFrequency = 1, IsSnapToTickEnabled = true,
            Value = capIdx < 0 ? 0 : capIdx,
            Width = 320, HorizontalAlignment = HorizontalAlignment.Left,
        };
        void SyncCap() => _capLabel.Text = "总量上限:" + CapLabel(MemoryCaps[(int)capSlider.Value]);
        capSlider.ValueChanged += (_, _) => { SyncCap(); s.MemoryMaxMB = MemoryCaps[(int)capSlider.Value]; s.Save(); };
        SyncCap();

        var delSel = Ui.DangerFilled("删除所选", (_, _) =>
        {
            if (_picked.Count == 0) return;
            if (!ConfirmDialog.Show("删除记忆", $"删除选中的 {_picked.Count} 条记忆?此操作不可撤销。", confirmText: "删除", danger: true)) return;
            TheApp.Memory.RemoveMany(_picked);
            _picked.Clear();
        });
        delSel.HorizontalAlignment = HorizontalAlignment.Left;
        delSel.Margin = new Thickness(0, 8, 0, 0);

        return Ui.Stack(
            Ui.Caption("AI 生成的摘要与事实。可逐条预览、置顶、删减 —— 置顶的永不被自动清理。"),
            Ui.Caption("多少天没被用到就清理"), _retentionRow,
            _capLabel, capSlider,
            Ui.Caption("★ 自动清理只动【摘要】;事实/偏好类与置顶的永远不动,且执行前先列出将删哪些。"),
            new Border { Height = 8 },
            _memList,
            delSel);
    }

    void RefreshRetention()
    {
        var s = TheApp.Settings;
        _retentionRow.Children.Clear();
        foreach (var (label, days) in Retentions)
        {
            var on = s.MemoryAutoCleanDays == days;
            var d = days;
            _retentionRow.Children.Add(SegChip(label, on, () =>
            {
                if (s.MemoryAutoCleanDays == d) return;
                s.MemoryAutoCleanDays = d;
                s.Save();
                RefreshRetention();
            }));
        }
    }

    // 就地展开的两种状态(按条目 id 记)。放在视图里而不是数据里 —— 它是"我正在看什么",不是记忆本身。
    readonly HashSet<string> _traced = new(StringComparer.Ordinal);
    readonly HashSet<string> _editing = new(StringComparer.Ordinal);

    void Toggle(HashSet<string> set, string id)
    {
        if (!set.Remove(id)) set.Add(id);
        RefreshMemory();
    }

    /// <summary>
    /// 溯源展开:这条摘要是【从哪来的】。
    /// ★ 如实分四种情况说,不含糊:有原文可点回去 / 原文被删了 / 压根没记来源(老条目)/ 项目归属。
    ///   含糊的溯源比没有溯源更坏 —— P3a 的验收硬线是「每条可溯源」,含糊等于把这条线悄悄放过。
    /// </summary>
    FrameworkElement TraceBlock(MemoryEntry m)
    {
        var box = new StackPanel { Margin = new Thickness(0, 6, 0, 2) };
        var proj = m.SourceProjectId is null ? "普通会话(不属于任何项目)"
                 : "项目「" + (TheApp.Projects.Find(m.SourceProjectId)?.Title ?? m.SourceProjectId) + "」";
        box.Children.Add(Ui.Caption("来自:" + proj));

        var ids = m.SourceSessionIds ?? Array.Empty<string>();
        if (ids.Count == 0)
        {
            box.Children.Add(Ui.Caption("★ 这条没有记来源会话 —— 无法回到原文。"));
        }
        else if (m.SourceOriginalsDeleted)
        {
            box.Children.Add(Ui.Caption($"覆盖 {ids.Count} 条会话,但原文已被删除 —— 点不回去了,只剩这段摘要。"));
        }
        else
        {
            box.Children.Add(Ui.Caption($"覆盖 {ids.Count} 条会话,点一条跳回原位:"));
            foreach (var sid in ids.Take(8))
            {
                var s = TheApp.Chat.Find(sid);
                var name = s?.Title ?? "(这条会话已经不在了)";
                var link = new TextBlock { Text = "· " + name, Margin = new Thickness(6, 1, 0, 1),
                                           TextTrimming = TextTrimming.CharacterEllipsis };
                link.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
                if (s is null) link.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                else
                {
                    link.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                    link.Cursor = System.Windows.Input.Cursors.Hand;
                    var captured = sid;
                    var ws = s.WorkspaceKey ?? "chat";
                    link.MouseLeftButtonUp += (_, e) =>
                    {
                        e.Handled = true;
                        (Application.Current.MainWindow as MainWindow)?.NavigateToSession(ws, s.ProjectId, captured);
                    };
                }
                box.Children.Add(link);
            }
            if (ids.Count > 8) box.Children.Add(Ui.Caption($"…另有 {ids.Count - 8} 条"));
        }
        return box;
    }

    /// <summary>编辑:只改标题与正文(为什么只改这两样,见 MemoryCenter.EditText 的说明)。</summary>
    FrameworkElement EditBlock(MemoryEntry m)
    {
        var title = new TextBox { Text = m.Title, Margin = new Thickness(0, 6, 0, 4), Padding = new Thickness(6, 4, 6, 4) };
        var body = new TextBox { Text = m.Body, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                                 MinHeight = 64, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                 Padding = new Thickness(6, 4, 6, 4) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                                   Margin = new Thickness(0, 6, 0, 0) };
        var cancel = Ui.Secondary("取消", (_, _) => Toggle(_editing, m.Id));
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var save = Ui.Primary("保存", (_, _) =>
        {
            if (title.Text.Trim().Length == 0)
            { ConfirmDialog.Show("标题不能为空", "记忆条目要有个名字,否则列表里认不出它。", confirmText: "好", cancelText: "关闭"); return; }
            TheApp.Memory.EditText(m.Id, title.Text, body.Text);
            _editing.Remove(m.Id);
            RefreshMemory();
        });
        row.Children.Add(cancel);
        row.Children.Add(save);

        var box = new StackPanel();
        box.Children.Add(Ui.Caption("改标题与正文。范围与来源不在这儿改 —— 改范围是权限动作,改来源会断掉溯源链。"));
        box.Children.Add(title);
        box.Children.Add(body);
        box.Children.Add(row);
        return box;
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

        var preview = new TextBlock { Text = m.Body, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        preview.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        preview.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var meta = Ui.Caption($"{m.Kind} · {ProjectUi.ScopeLabel(m.Scope)} · {m.CreatedAt:M月d日}"
                              + (m.SourceOriginalsDeleted ? " · 原文已删除(只剩摘要)" : "")
                              // ★ 人手改过的要写在脸上:它已经不是 AI 写的那份了
                              + (m.EditedByHuman ? $" · 已人工修改({m.EditedAt:M月d日})" : ""));

        var col = new StackPanel();
        col.Children.Add(title);
        col.Children.Add(preview);
        col.Children.Add(meta);

        // ★ P3c 判据的四项是【浏览 · 编辑 · 删除 · 溯源展开】—— 此前只有浏览与删除。
        //   编辑与溯源都做成【就地展开】而不是弹窗:记忆条目是一段文字,为改一行字换一个窗口太重。
        var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        acts.Children.Add(SegChip("溯源", _traced.Contains(m.Id), () => Toggle(_traced, m.Id)));
        acts.Children.Add(SegChip("编辑", _editing.Contains(m.Id), () => Toggle(_editing, m.Id)));
        var pin = SegChip(m.Pinned ? "已置顶" : "置顶", m.Pinned, () => TheApp.Memory.TogglePin(m.Id));
        acts.Children.Add(pin);

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 6, 0, 6) };
        DockPanel.SetDock(cb, Dock.Left);
        DockPanel.SetDock(acts, Dock.Right);
        row.Children.Add(cb);
        row.Children.Add(acts);
        row.Children.Add(col);

        if (_traced.Contains(m.Id)) col.Children.Add(TraceBlock(m));
        if (_editing.Contains(m.Id)) col.Children.Add(EditBlock(m));
        return row;
    }

    static CheckBox Check(string text, bool value, Action<bool> onChange)
    {
        var cb = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 4, 0, 0) };
        cb.Checked += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);
        return cb;
    }

    // 分段小按钮(选中 = 强调底 + 反色字)。与待办的保留期选择同一套观感。
    static FrameworkElement SegChip(string text, bool selected, Action onClick)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, selected ? "FgOnAccent" : "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border
        {
            Child = t, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand, BorderThickness = new Thickness(1),
        };
        b.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        b.SetResourceReference(Border.BackgroundProperty, selected ? "Accent" : "BgSurface");
        b.SetResourceReference(Border.BorderBrushProperty, selected ? "Accent" : "Border");
        if (!selected)
        {
            b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
            b.MouseLeave += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgSurface");
        }
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }
}
