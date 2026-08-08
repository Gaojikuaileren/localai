// V21 -- 管理端 ›「记忆库」。从 `app/Views/StorageView.cs` 切出来的那一段(V10 §2.1「主体搬」)。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★ 客户端留下的是「**本机存档用量**」那一小块(D50:客户端存档在 %LOCALAPPDATA%,
//    不在 {state}),以及清缓存与会话摘要 —— 那些动的都是**客户端自己的**文件。
//    搬过来的是记忆库:数据(`memory.json`)与界面一起,一个都不留在客户端。
//
//  ★★★ 一处**没能原样搬过来**的东西,必须写在最前面(不是漏掉,是结构决定的):
//    **溯源(TraceBlock)里那些"点回原文"的链接**。
//    它读的是**客户端的**会话表(`ChatCenter`)与项目表(`ProjectCenter`),
//    并且要调 `MainWindow.NavigateToSession` 跳到那条会话 —— 三样东西都在另一个进程里。
//    ⇒ 这里**保留溯源信息本身**(来自哪个项目、覆盖了几条会话、原文删没删),
//      但那几条**不再是可点的链接**,并且界面上明说「要看原文去客户端」。
//    ★ P3a 的验收硬线是「每条可溯源」——**溯源仍然在**(说得出来自哪儿);
//      少掉的是"一键跳过去"。这两件事不许混着说,所以这里逐条写清。
//    ★★ 想把跳转补回来:那需要一条**管理端 → 客户端**的深链(进程间),
//      而不是让管理端去读客户端的 chat.json(那会踩纪律③)。已写进决议包的 DEBT。
// ══════════════════════════════════════════════════════════════════════════════

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Admin.Services;
using LocalAI.Client.Services;
using LocalAI.Client.Views;

namespace LocalAI.Admin.Views;

public sealed class MemoryView : UserControl
{
    /// <summary>管理端进程内**唯一**那份记忆库(与 `App.Memory` 同一条纪律:一份数据一个持有者)。</summary>
    public static readonly MemoryCenter Memory = new();

    static AppSettings Settings => AdminSettings.Current;

    // 记忆库总量上限的阶段(MB)。0 = 不限。
    static readonly int[] MemoryCaps = { 0, 50, 100, 250, 500, 1024, 2048 };
    static string CapLabel(int mb) => mb == 0 ? "不限" : mb >= 1024 ? (mb / 1024) + " GB" : mb + " MB";

    // 记忆保留期(天)。0 = 关闭。
    static readonly (string Label, int Days)[] Retentions =
    {
        ("关闭", 0), ("7 天", 7), ("30 天", 30), ("90 天", 90), ("一年", 365), ("两年", 730), ("三年", 1095),
    };

    readonly StackPanel _root = new();
    readonly StackPanel _memList = new();
    readonly StackPanel _retentionRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
    readonly HashSet<string> _picked = new();
    TextBlock? _capLabel;

    public MemoryView()
    {
        Content = _root;
        Build();
        Loaded += (_, _) => Memory.Changed += RefreshMemory;
        Unloaded += (_, _) => Memory.Changed -= RefreshMemory;
    }

    void Build()
    {
        _root.Children.Clear();
        var card = Ui.Stack(Ui.Subtitle("记忆库"));

        // ★★★ 纪律③:迁移失败**要看得见**,而且**绝不静默用空库启动**。
        //   这一格就是那句话的落点 —— 失败时它排在最前面,而下面的列表会说"读不到",
        //   不会显示一个空列表冒充「你没有记忆」。
        if (MemoryStore.Notice is { Length: > 0 } notice)
        {
            var warn = Ui.Body(notice);
            card.Children.Add(warn);
            card.Children.Add(new Border { Height = 8 });
        }

        card.Children.Add(MemorySection());
        _root.Children.Add(Ui.Card(card));
        RefreshMemory();
    }

    // ---------------------------------------------------------------- 记忆库
    UIElement MemorySection()
    {
        var s = Settings;

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
        capSlider.ValueChanged += (_, _) => { SyncCap(); s.MemoryMaxMB = MemoryCaps[(int)capSlider.Value]; AdminSettings.Save(); };
        SyncCap();

        var delSel = Ui.DangerFilled("删除所选", (_, _) =>
        {
            if (_picked.Count == 0) return;
            if (!ConfirmDialog.Show("删除记忆", $"删除选中的 {_picked.Count} 条记忆?此操作不可撤销。", confirmText: "删除", danger: true)) return;
            Memory.RemoveMany(_picked);
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
        var s = Settings;
        _retentionRow.Children.Clear();
        foreach (var (label, days) in Retentions)
        {
            var on = s.MemoryAutoCleanDays == days;
            var d = days;
            _retentionRow.Children.Add(SegChip(label, on, () =>
            {
                if (s.MemoryAutoCleanDays == d) return;
                s.MemoryAutoCleanDays = d;
                AdminSettings.Save();
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
            Memory.EditText(m.Id, title.Text, body.Text);
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
        var items = Memory.Visible().ToList();
        if (items.Count == 0)
        {
            // ★ 诚实的空态:不是"还没整理",而是根本还没有 AI 来产生记忆
            _memList.Children.Add(Ui.Body("记忆库是空的。", muted: true));
            // ★ 2026-08-05 审计改写:同上,不再声称"模型尚未接入"。
            _memList.Children.Add(Ui.Caption("摘要必须由 AI 生成,而摘要这条链路还没接上模型 —— 接上后整理出的摘要会出现在这里。"));
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

        // ★ `ProjectUi.ScopeLabel` 留在客户端(`ProjectUi.cs` 拖着 Project/ProjectCenter
        //   一整族,链不动 —— prereqs §3 实测过)。这里就地把三档翻成词。
        //   ★★ 判据走**枚举本身**,不走字符串:`ProjectScope` 是两个 csproj 编的同一份
        //     (见 ProjectScope.cs 头),所以加一档时这里会**编译不过**,而不是静默漏掉。
        var meta = Ui.Caption($"{m.Kind} · {ScopeLabel(m.Scope)} · {m.CreatedAt:M月d日}"
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
        var pin = SegChip(m.Pinned ? "已置顶" : "置顶", m.Pinned, () => Memory.TogglePin(m.Id));
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

    /// <summary>三档可见范围的中文。★ 用 `switch` 表达式**穷举**(没有 `_ =>` 兜底)——
    /// 加一档时编译器会当场说「没有覆盖所有分支」,而不是悄悄显示成空白。</summary>
    static string ScopeLabel(ProjectScope s) => s switch
    {
        ProjectScope.Family => "家庭",
        ProjectScope.Personal => "个人",
        ProjectScope.OnlyMe => "仅本人",
    };

    /// <summary>
    /// 溯源展开:这条摘要是【从哪来的】。
    /// ★ 如实分四种情况说,不含糊:有原文 / 原文被删了 / 压根没记来源(老条目)/ 项目归属。
    ///   含糊的溯源比没有溯源更坏 —— P3a 的验收硬线是「每条可溯源」,含糊等于把这条线悄悄放过。
    ///
    /// <para>★★ V21:会话标题与项目名**读不到了**(它们在客户端的 chat.json / projects.json 里,
    /// 而那两份归客户端独占 —— 纪律③)。⇒ 这里显示的是**会话 id**,并明说去哪看原文。
    /// 显示 id 比显示一个猜出来的标题诚实:后者一旦对不上,人会以为记忆记错了。</para>
    /// </summary>
    FrameworkElement TraceBlock(MemoryEntry m)
    {
        var box = new StackPanel { Margin = new Thickness(0, 6, 0, 2) };
        box.Children.Add(Ui.Caption("来自:" + (m.SourceProjectId is null
            ? "普通会话(不属于任何项目)"
            : "项目 " + m.SourceProjectId + "(项目名在客户端里)")));

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
            box.Children.Add(Ui.Caption($"覆盖 {ids.Count} 条会话:"));
            foreach (var sid in ids.Take(8))
                box.Children.Add(Ui.Caption("· " + sid));
            if (ids.Count > 8) box.Children.Add(Ui.Caption($"…另有 {ids.Count - 8} 条"));
            // ★ 说清为什么这里点不动 —— 一个点不动的链接比没有链接更让人困惑。
            box.Children.Add(Ui.Caption("★ 原文在【客户端】的会话里(记忆与会话分属两个程序)。"
                                        + "到客户端里按这个 id 找那条会话 —— 管理端不去读它的 chat.json。"));
        }
        return box;
    }
}
