// P3c -- 「正在进行的任务」抽屉内容。全局:从底部横条点开,在任何界面都可用。
// 与底部横条的分工:横条只给【一条简要 + 进度】(轮播);抽屉给【全部任务的完整列表】。

//
// ══════════════════════════════════════════════════════════════════════
//  ★★★ 2026-08-07(V8 · D87③):这里是用户裁定里的「**在任务进度里面可以再开**」。
//
//  裁定原文:「AI,让,任务暂停,并弹提示。然后在任务进度里面可以再开,
//  然后启动需要的模型,前提是显存允许的情况,**不然开始按钮是不可用的**。」
//
//  ★★ 「按钮不可用」附带一条要求,项目里已经栽过:
//    **置灰但不说原因,和骗人是一回事**;而给一个**错的**原因比不给更坏。
//    ⇒ 这里的置灰必须同时说清:为什么不可用 · **还差多少**。
//  ★ 差多少是**预览**:中枢在你点下去那一刻会重新求值(§8.1「确定 = 一次事务」第 1 条),
//    所以文案里明说"以中枢判定为准",不假装这个数就是最终答案。
// ══════════════════════════════════════════════════════════════════════

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public sealed class TaskDrawerView : UserControl
{
    /// <summary>
    /// 这条暂停任务现在能不能再开。★ 返回 (能不能, 给人看的理由)。
    /// <para>
    /// 判据就是 §8.1 的**动态闸**:<c>free − Σpeak(要装的) ≥ safety_margin</c>。
    /// ★★ 这是**预览**,不是准入 —— 准入在中枢,点下去那一刻重新求值。
    /// </para>
    /// <para>★ 拿不到中枢数据时**不猜**:按钮不可用,理由写"读不到"。
    /// 猜一个"应该够"让用户点下去撞一鼻子灰,比直接说读不到更坏。</para>
    /// <para>★ 抽成 <c>static</c> 是为了让自检能直接喂它 —— 界面里算的那个数,
    /// 和断言验的那个数必须是**同一段代码**。</para>
    /// </summary>
    internal static (bool CanStart, string Why) CanResume(GpuCatalog? cat, RunningTask t)
    {
        if (cat is null)
            return (false, "读不到中枢的组件清单 —— 现在算不出显存够不够(不拿旧值冒充)。");
        if (cat.FreeGiB is not { } free)
            return (false, "中枢这一轮没读到实时可用显存 —— 现在算不出够不够(不拿旧值冒充)。");
        var need = 0.0;
        var unknown = new List<string>();
        foreach (var id in t.NeedsComponents)
        {
            var c = cat.Components.FirstOrDefault(x => x.Id == id);
            if (c is null) unknown.Add(id); else need += c.PeakGiB;
        }
        if (unknown.Count > 0)
            return (false, $"本机算不出这些组件的峰值:{string.Join("、", unknown)} —— "
                           + "本机与中枢的配置对不上,不敢说够不够。");
        var after = free - need;
        if (after >= cat.SafetyMargin)
            return (true, $"此刻可用 {free:0.00} GiB,这条要 {need:0.00},装完还剩 {after:0.00}"
                          + $"(需 ≥ {cat.SafetyMargin:0.00})。★ 以中枢点下去那一刻的重新求值为准。");
        var missing = cat.SafetyMargin - after;
        // ★★ 说清**还差多少**,而不是一句"显存不足" —— 差 0.2 和差 6 GiB
        //   对用户是两件事:前者关个标签页就行,后者得关游戏。
        return (false, $"显存不够:此刻可用 {free:0.00} GiB,这条要 {need:0.00},"
                       + $"装完只剩 {after:0.00} < 安全余量 {cat.SafetyMargin:0.00} —— "
                       + $"还差 {missing:0.00} GiB。→ 关掉占显存的程序再试"
                       + "(这堵是物理墙,改桌面预留没有用)。");
    }

    static App TheApp => (App)Application.Current;

    // ══════════════════════════════════════════════════════════════════════
    //  ★★★ V30:模型装载那条的【状态从哪儿来】—— 与发送键**同一个就绪闸**。
    //
    //  ★ 这是本轮最要紧的一条纪律,而它很容易被写反:抽屉里再判一次
    //    「装完没有」就是**第二套口径**,而两套口径岔开的那天,发送键亮着、
    //    抽屉里还写着"正在启用中"(或反过来)—— 而不会有任何东西红。
    //  ⇒ 一个源:`TheApp.Ready.Gate(alias)`。抽屉只负责把它**画**出来。
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>模型装载那一行右侧的文字态。★ 取自就绪闸,不在这儿另判一次。</summary>
    static string ModelStateText(RunningTask t) => TheApp.Ready.Gate(t.ResumeAlias).State switch
    {
        ModelReadyState.Ready => "已启用",
        ModelReadyState.Starting => "正在启用中",
        _ => "未启用",
    };

    /// <summary>
    /// 模型装载那一行的状态点。★★ 它替掉的是进度条 —— 一个**不动的**图元
    /// 说的正是实话:「我知道自己在哪个态,但我不知道还要多久」。
    /// <para>★ 三态三色**外加**三种文字(见 <see cref="ModelStateText"/>)——
    /// 颜色只是辅助;真正把两类任务分开的是"有没有进度条"这个形状差异。</para>
    /// </summary>
    static UIElement StateDot(RunningTask t)
    {
        var st = TheApp.Ready.Gate(t.ResumeAlias).State;
        var dot = new Border
        {
            Width = 8, Height = 8,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(st switch
            {
                ModelReadyState.Ready => Color.FromRgb(0x2E, 0xA0, 0x43),     // 绿:能用了
                ModelReadyState.Starting => Color.FromRgb(0xD9, 0x8A, 0x0B), // 琥珀:还在起
                _ => Color.FromRgb(0x8A, 0x8A, 0x8A),                        // 灰:没起
            }),
        };
        DockPanel.SetDock(dot, Dock.Left);
        return dot;
    }

    GpuCatalog? _catalog;

    public TaskDrawerView()
    {
        Build();
        // ══════════════════════════════════════════════════════════════════
        //  ★★★ V30:模型装载那一行要**跟着闸动**。
        //    抽屉此前是"构造时画一次"就完事 —— 而模型往往正是在抽屉开着的时候装完的,
        //    于是那一行会一直停在「正在启用中」,而模型其实已经好了。
        //    ★ 那正是本仓最恨的形状:**一句曾经为真的话没跟着改**。
        //  ★★ 闸只在【答案真的变了】时响,所以整块重画不会变成每秒一次。
        // ══════════════════════════════════════════════════════════════════
        Loaded += (_, _) => TheApp.Ready.Changed += OnReadyChanged;
        Unloaded += (_, _) => TheApp.Ready.Changed -= OnReadyChanged;
    }

    /// <summary>★ 闸可能在后台线程上响 ⇒ 切回 UI 线程再重画。</summary>
    void OnReadyChanged() => Dispatcher.BeginInvoke(new Action(Build));

    void Build()
    {
        var app = (App)Application.Current;
        var list = new StackPanel();
        // ★ 目录里才有 peak 与 safety_margin(快照里没有)—— 「够不够」要靠它算。
        //   ★★ 这里**同步取上一次缓存**,拿不到就是 null ⇒ CanResume 会如实说"读不到",
        //     而不是先画一个可点的按钮再在点下去时失败。
        _catalog = app.Gpu.LastCatalog;
        if (app.Tasks.Tasks.Any(t => t.IsPaused) && _catalog is null)
        {
            // 有暂停任务却没有目录 ⇒ 现取一次(抽屉是用户主动打开的,一次读没问题)。
            _ = app.Gpu.FetchCatalogAsync().ContinueWith(_ =>
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (Application.Current.MainWindow is MainWindow mw) mw.RefreshTaskBar();
                }));
        }

        if (app.Tasks.Tasks.Count == 0)
        {
            list.Children.Add(Ui.Body("暂无正在运行的任务。", muted: true));
        }
        else
        {
            foreach (var t in app.Tasks.Tasks)
            {
                var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
                var pct = new TextBlock { Text = t.IsModelLoad ? ModelStateText(t) : t.PercentText, VerticalAlignment = VerticalAlignment.Center };
                pct.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                pct.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
                DockPanel.SetDock(pct, Dock.Right);
                head.Children.Add(pct);

                var title = new TextBlock { Text = t.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                title.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
                // ★★★ V30:模型装载在标题**左边**多一颗状态点 —— 见 StateDot 那段说明。
                //   形上就分得开(多一个图元),不是靠颜色:色觉差异 / 深浅皮肤下颜色都可能对不上。
                if (t.IsModelLoad) head.Children.Add(StateDot(t));
                head.Children.Add(title);

                var bar = new ProgressBar { Height = 4, Minimum = 0, Maximum = 1, BorderThickness = new Thickness(0), Margin = new Thickness(0, 6, 0, 0) };
                bar.SetResourceReference(ProgressBar.ForegroundProperty, "Accent");
                bar.SetResourceReference(ProgressBar.BackgroundProperty, "BgSunken");
                if (t.Progress < 0) bar.IsIndeterminate = true; else bar.Value = t.Progress;

                // ── D87③:暂停态那一段 ────────────────────────────
                if (!t.IsPaused)
                {
                    // ══════════════════════════════════════════════════════════════
                    //  ★★★ V30(用户裁定):「任务栏里载入中的模型要和真任务分开,
                    //    而且**不要进度条**。」★ 用户明说这个功能本身「很好」——
                    //    ⇒ 只改**呈现**,一个字节的行为都不动(任务照常在、照常能再开)。
                    //
                    //  ★★ 判词:**一个不知道自己进度的进度条,是在假装它知道。**
                    //    模型装载的 Progress 恒为 -1,走到这条通用路径上就变成
                    //    `IsIndeterminate = true` —— 一条来回跑的动画,而那条动画在说
                    //    "我在推进";它其实什么都不知道。
                    //
                    //  ★★★ 「在形上就分得开」而不是靠颜色:状态点(●)+ 文字态,
                    //    与"正在计算"那种带进度条的任务多/少一个图元。只改颜色的话,
                    //    换皮肤、色觉差异、灰度截图三种情况下它们会重新长得一模一样。
                    // ══════════════════════════════════════════════════════════════
                    list.Children.Add(Ui.Card(
                        t.IsModelLoad
                            ? Ui.Stack(head, Ui.Caption(t.Detail))       // ★ 没有 bar —— 这就是那条裁定
                            : Ui.Stack(head, Ui.Caption(t.Detail), bar),
                        new Thickness(0, 0, 0, 10)));
                    continue;
                }

                // ★ 暂停不画进度条:一条停着的进度条会让人以为它还在动。
                var why = new TextBlock
                {
                    Text = t.PausedReason,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                why.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
                why.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

                var (canStart, reason) = CanResume(_catalog, t);
                var gate = new TextBlock
                {
                    Text = reason,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                };
                gate.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
                if (canStart) gate.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
                else gate.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x30, 0x25));

                var start = new Button
                {
                    Content = "再开",
                    Padding = new Thickness(16, 5, 16, 5),
                    Margin = new Thickness(0, 8, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    // ★★★ 裁定原文:「前提是显存允许的情况,不然开始按钮是不可用的」。
                    //   ★ 它旁边那行 `gate` 永远在说**为什么** —— 置灰但不说原因等于骗人。
                    IsEnabled = canStart,
                    // ★ 悬停也给同一句:按钮本身也要能自己解释自己
                    ToolTip = reason,
                };
                // ★★★ V30 顺手修:WPF 的 ToolTip **默认在禁用的控件上不显示**。
                //   ⇒ 上面那句 `ToolTip = reason` 恰恰在**最需要它的时候**(按钮灰着、
                //     人正想知道为什么)一个字都不显示,而它旁边的注释写着
                //     「按钮本身也要能自己解释自己」—— 那句话此前是假的。
                //   ★ 与本轮发送键那颗气泡是同一族缺陷:禁用的控件收不到输入,
                //     挂在它身上的解释就够不着。这里一行就能修好(发送键那边要外包容器,
                //     因为用户点名要的是**点击**弹气泡,而 ToolTip 是悬停)。
                ToolTipService.SetShowOnDisabled(start, true);
                var self = t;
                start.Click += async (_, _) =>
                {
                    start.IsEnabled = false;
                    start.Content = "正在启动…";
                    var res = await TheApp.Gpu.ResumeTaskAsync(self);
                    // ★ 不管成没成都重建这份列表:成了要变回运行态,
                    //   没成要把中枢给的**新理由**显示出来(而不是留着旧的那句)。
                    if (Application.Current.MainWindow is MainWindow mw) mw.RefreshTaskBar();
                    if (!res.Ok) { start.Content = "再开"; start.IsEnabled = true; }
                };

                list.Children.Add(Ui.Card(
                    Ui.Stack(head, Ui.Caption(t.Detail), why, gate, start),
                    new Thickness(0, 0, 0, 10)));
            }
        }

        Content = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }
}
