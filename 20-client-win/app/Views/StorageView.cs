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

    // ★★★ V21:记忆库的**上限/保留期/列表/编辑**整段已搬进 `admin/Views/MemoryView.cs`。
    //   用户裁定「副机上不能编辑记忆,也不能浏览」——拆分之后它从「按成员过滤」
    //   升级成「**结构上没有入口**」:这个 exe 里没有那一页。
    //   ★ 而「副机看不到记忆」**≠**「副机上的 AI 没有记忆」——
    //     AI 照常用记忆(模型与检索都在主机上)。这一句写在 MemoryCenter.cs 头上。

    readonly StackPanel _root = new();
    readonly StackPanel _usage = new();
    TextBlock? _status, _thresholdLabel;

    public StorageView()
    {
        // 并入设置页的一段,不自带滚动(外层 Ui.Page 已是 ScrollViewer,套两层会吃掉滚轮)
        Content = _root;
        Build();
    }

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
            // ★ 记忆库那一整块搬走了 —— 这里**说清它去哪了**,而不是让它凭空消失。
            //   一个功能没了却没人说,和它坏了长得一模一样。
            Ui.Body("记忆库"),
            Ui.Caption("★ 记忆库的浏览与编辑在**主机的管理端**里(设置最下面那颗「打开管理端面板」)。"
                       + "记忆从不离开主机 —— 副机上没有这一页,是**结构上没有**,不是被藏起来了。"),
            Ui.Caption("★★ 这**不代表**副机上的 AI 没有记忆:模型在主机上跑、检索在主机上做,"
                       + "你在这台上对话时它照常用记忆。")
        )));
        RefreshUsage();
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
        // ★ V21:记忆库归管理端了,客户端**算不出**它多大 —— 传 -1,由 StorageUsage
        //   把那一行写成「在管理端里看」。★ 传 0 是不行的:0 会被显示成「0 字节」,
        //   而那是一句**假话**(记忆可能有几百条,只是不在这台程序管的范围里)。
        foreach (var it in StorageUsage.Snapshot(memoryBytes: -1))
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
        // ★ 「按规则清理记忆库」跟着记忆库一起搬进管理端 —— 客户端够不着那份数据了。
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
            // ★ 诚实:这里【什么都不做】,绝不拿本地规则拼假摘要。
            // ★★ 2026-08-05 审计改写:原文说「AI 尚未接入(P4)」—— 模型 S11 就接上了。
            //   还没有的是**摘要这条链路**(没人把会话喂给模型再写回记忆库)。
            done.Add("整理摘要:这个功能还没有接上模型,本次未执行");

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
                    // ★★ V21 如实交代一处**断了的链**:原来这里会给引用这些会话的记忆条目
                    //   打上「原文已删除」(`Memory.MarkOriginalsDeleted`),不留死链。
                    //   记忆库现在归管理端,客户端**写不了**它(纪律③:一个 json 一个写者)。
                    //   ⇒ 那一步没有做,而且**必须说出来** —— 溯源链上会出现
                    //     「点得到、但原文其实已经删了」的条目。
                    //   ★ 正解是让管理端在它自己那一页里对账(它读得到归档目录在不在),
                    //     已写进决议包的 DEBT 一栏。**不在这里偷偷跨进程写。**
                    // ★ 计数缓存要跟着清(审计 2026-08-02):否则会话顶上还挂着
                    //   "加载更早的 N 条",点了什么也不发生。
                    TheApp.Chat.InvalidateArchiveCounts();
                    done.Add($"归档原文:已删除(涉及 {affected.Count} 个会话)。"
                             + "★ 记忆库里引用它们的摘要**还没有**被标注「原文已删除」—— "
                             + "记忆库在管理端,客户端写不了它。到管理端的「记忆库」页对一次账。");
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
            // ★★ 2026-08-05 审计改写:原文「模型还没接入(P4 GPU Broker)」是假话(S11 已接入)。
            ConfirmDialog.Show("摘要还没接上模型",
                "摘要必须由 AI 生成,而这个功能还没有接上模型(会话对话本身已经能用了)。\n\n" +
                "接上之后这里会真正整理会话并写入记忆库;在那之前不会拿本地规则拼一段假摘要冒充。",
                confirmText: "好", cancelText: "关闭"));
        manual.HorizontalAlignment = HorizontalAlignment.Left;
        manual.Margin = new Thickness(0, 10, 0, 0);

        return Ui.Stack(
            Ui.Caption("触发方式"), mode,
            _thresholdLabel, slider,
            Ui.Caption($"会话累计超过这个量就提示另开一条(可调 {ThresholdMin / 1000}k–{ThresholdMax / 1000}k)。" +
                       // ★ 2026-08-05 审计改写:原文说"模型未接入前用字符数估算" —— 模型早接入了,
                       //   而估算仍然是字符数,原因是**客户端没有分词器**(见 TokenBudget 的说明),
                       //   不是"还没接入"。原因写错会让人以为这是个会自动消失的临时状态。
                       "★ 真正的约束是模型上下文窗口;客户端没有分词器,所以按字符估算(估高不估低)。"),
            manual,
            Ui.Caption("★ 原文永不自动删除 —— 摘要只是索引,原始对话一直留着(超出热层的会移到归档,仍是原文)。"));
    }

    static CheckBox Check(string text, bool value, Action<bool> onChange)
    {
        var cb = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 4, 0, 0) };
        cb.Checked += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);
        return cb;
    }

    // ★ `SegChip`(分段小按钮)跟着记忆库的保留期/置顶那几处一起搬进了
    //   `admin/Views/MemoryView.cs` —— 这一页里已经没有用它的地方了。
}
