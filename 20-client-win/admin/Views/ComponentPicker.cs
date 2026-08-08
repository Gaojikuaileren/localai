// P4-S9 -- 组件挑选面板(「系统 › 模型」里「启用的模型」那一块)。
//
// ★★ 这块此前是一份**自造的占位清单**(Views/ModelCatalog.cs:chat.8b / chat.8b.long /
//   chat.30b / speech / vlm / image)。那六个 key 跟网关别名对不上、跟显存组件 id 也对不上,
//   是**第三套词汇**,谁也映射不到谁 —— 勾了它们不会发生任何事,而界面看着像配置好了。
//   现在清单由中枢下发(GET /v1/gpu/components),勾选提交走真事务(POST /v1/gpu/intended)。
//
// ★★★ 三条硬规矩,都是"看着有防护、实际没有"的反面:
//
//   ① **两种撞墙必须分开说**(§8.1)。合并成一句「显存不足」是有害的:
//      · 撞 vram_budget(静态)⇒ 改桌面预留【有用】
//      · 撞此刻可用(动态)  ⇒ 改预留【没用】,得去关占显存的程序
//      合并之后用户会去调预留,调完发现没用 —— 因为撞的是物理墙。
//
//   ② **点确定不是"保存偏好",是一次事务**。中枢会在那一刻**重新求值**,
//      所以面板算出来的"能装下"只是预览。确定之后以中枢的回话为准,
//      而每一种失败给**不同**的下一步(见 ApplyOutcome.Advice),不合并成"失败了"。
//
//   ③ **每种失败给不同的下一步**(见 ApplyOutcome.Advice),不合并成"失败了"。
//      ★★ 2026-08-06 更正:这条原文写的是「今天点确定**必然**得到 loader_absent ——
//        中枢还没有装载器(那是 P5)」。**两处都是假话**:装载器 S14 就落地了
//        (model_loader.py),而 P5 是语音 v1。客户端这半边此前漏改(服务端 S15 已改),
//        于是用户点完确定看到的是一句假的解释。⇒ 不再自己编,照搬中枢的 message。
//
// ══════════════════════════════════════════════════════════════════════
//  ★★★ P4-S16b:本面板多了**第二列勾选** ——「允许按需装载」(D90 裁定①)。
//
//  两列的意思完全不同,界面必须让人一眼分得出来:
//    · 第一列「常驻」 = 你要它**一直装着**。系统**一个字节都不会自动改它**;
//    · 第二列「按需」 = 你**授权**系统在需要时自动装、空闲时自动卸。
//
//  ★ 第二列存在的理由就是 D90 裁定①的代价段:
//    「用户要先授权一次,才有按需可言 —— 没有它,系统就是在你没同意的情况下自己动显存。」
//    ⇒ 这一步**不能省**,也不能默认全勾上:默认全勾 = 把那次同意伪造出来。
//
//  ★★ 这一列**只有主机能写**(审计 B6,服务端有闸)。而这里**不预先灰掉它** ——
//    「我是不是主机」是个**代理指标**,而真正要问的是「这个操作做不做得成」
//    (ASSERTION-PITFALLS 第 9 条:判据问的是"我是什么身份"而不是"我做不做得到")。
//    ⇒ 让人点,失败时由中枢给出**它真的验过**的理由(denied_action + 哪一维)。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Admin.Services;
using LocalAI.Client.Services;
using LocalAI.Client.Views;
using LocalAI.Client.Theme;

namespace LocalAI.Admin.Views;

public sealed class ComponentPicker : UserControl
{
    // ★★★ V21:GPU 面从客户端的 `TheApp.Gpu`(HubGpu,要先判主机/副机再决定拨哪儿)
    //   换成管理端的 `AdminGpu`(**只走回环**)。理由见 AdminGpu.cs 文件头:
    //   这个 exe 只装在主机上,所以「拨哪儿」这个问题在本进程里不存在。
    //   ★ 解析仍然是**同一份**(`HubGpu.ParseCatalog` / `ParseOutcome`,GpuWire.cs)。
    static AdminGpu Gpu => AdminGpu.Instance;

    readonly StackPanel _list = new();
    readonly TextBlock _sumLine = new();
    readonly TextBlock _wallStatic = new();
    readonly TextBlock _wallDynamic = new();
    /// <summary>
    /// 三段预览的**最后一行**:「还能让桌面再涨 N GiB 才会出问题」。
    /// <para>★★★ 方案书 §8.1 原文:「**★ 最后那一行是本界面存在的理由。**
    /// 只显示「装得下」是不够的 —— 桌面占用是**波动**的,用户需要知道自己离墙有多远,
    /// 而不是知道此刻没撞墙。」而它此前**整行缺席**。</para>
    /// </summary>
    readonly TextBlock _headroom = new();
    readonly TextBlock _status = new();
    readonly Button _apply = new() { Content = "确定", Padding = new Thickness(18, 7, 18, 7) };
    readonly Button _reload = new() { Content = "重新取", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };

    GpuCatalog? _catalog;
    readonly HashSet<string> _checked = new(StringComparer.Ordinal);
    /// <summary>「允许按需装载」那一列的当前勾选(D90 裁定①的那次授权)。</summary>
    readonly HashSet<string> _permitted = new(StringComparer.Ordinal);
    /// <summary>中枢下发时的授权原样 —— 用来判断用户**有没有动过**这一列。
    /// ★ 没动过就**不发** permitted_on_demand:省略 = 不动授权,而空数组 = 撤销全部。
    ///   分不清这两者的话,副机每次普通变更都会撞上那道只有主机能过的闸。</summary>
    readonly HashSet<string> _permittedAsFetched = new(StringComparer.Ordinal);
    bool _busy;

    public ComponentPicker()
    {
        _sumLine.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        foreach (var t in new[] { _wallStatic, _wallDynamic, _headroom, _status })
        {
            t.TextWrapping = TextWrapping.Wrap;
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            t.Margin = new Thickness(0, 3, 0, 0);
        }
        _apply.Click += async (_, _) => await ApplyAsync();
        _reload.Click += async (_, _) => await LoadAsync();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(_reload);
        buttons.Children.Add(_apply);

        // ★ 两列的表头。没有它,两个挨着的复选框在界面上**无从分辨**,
        //   而它们的含义差得很远(一个是"我要它一直在",一个是"我授权系统自己动它")。
        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };
        var hCommitted = new TextBlock { Text = "常驻", Margin = new Thickness(0, 0, 0, 0) };
        var hOnDemand = new TextBlock { Text = "按需", Margin = new Thickness(0, 0, 12, 0) };
        foreach (var t in new[] { hCommitted, hOnDemand })
        {
            t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        }
        DockPanel.SetDock(hCommitted, Dock.Right); head.Children.Add(hCommitted);
        DockPanel.SetDock(hOnDemand, Dock.Right); head.Children.Add(hOnDemand);

        var root = new StackPanel();
        root.Children.Add(head);
        root.Children.Add(_list);
        root.Children.Add(new Border { Height = 8 });
        root.Children.Add(_sumLine);
        root.Children.Add(_wallStatic);
        root.Children.Add(_wallDynamic);
        // ★ 分隔线 + 最后一行 —— §8.1 的三段预览就是「三段 + 一条横线 + 结论」那个形状。
        var rule = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 4), Opacity = 0.35 };
        rule.SetResourceReference(Border.BackgroundProperty, "FgMuted");
        root.Children.Add(rule);
        root.Children.Add(_headroom);
        root.Children.Add(_status);
        root.Children.Add(buttons);
        Content = root;

        Loaded += async (_, _) => await LoadAsync();
    }

    async Task LoadAsync()
    {
        SetStatus("正在向中枢取组件清单…");
        _list.Children.Clear();
        try
        {
            var cat = await Gpu.FetchCatalogAsync();
            if (cat is null)
            {
                // ★ 取不到就**什么都不列**。列一份本地兜底清单等于回到"第三套词汇",
                //   而且用户会以为那就是中枢的真实清单。
                _catalog = null;
                SetStatus("取不到中枢的组件清单 —— 这里【不显示】任何清单,"
                          + "因为唯一权威在中枢。" + (Gpu.LastError is { } e ? "(" + e + ")" : ""),
                          danger: true);
                _apply.IsEnabled = false;
                return;
            }
            _catalog = cat;
            _checked.Clear();
            _permitted.Clear();
            _permittedAsFetched.Clear();
            foreach (var c in cat.Components) if (c.Intended) _checked.Add(c.Id);
            // ★ 授权那一列的初值来自**中枢**,不是本地记忆 —— 它是权威状态的一部分。
            foreach (var c in cat.Components)
                if (c.PermittedOnDemand) { _permitted.Add(c.Id); _permittedAsFetched.Add(c.Id); }
            foreach (var c in cat.Components) _list.Children.Add(Row(c));
            _apply.IsEnabled = true;
            SetStatus("");
            Recompute();
        }
        catch (Exception ex)
        {
            _catalog = null;
            _apply.IsEnabled = false;
            SetStatus("取组件清单失败:" + ex.Message, danger: true);
        }
    }

    FrameworkElement Row(GpuComponent c)
    {
        var name = new TextBlock { Text = c.Display, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        // ★ peak 直接显示 —— 用户要能自己判断"再勾一个还装不装得下"。
        var peak = new TextBlock
        {
            Text = $"  {c.PeakGiB:0.00} GiB",
            VerticalAlignment = VerticalAlignment.Center,
        };
        peak.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        peak.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var ic = Icons.Make(IconName.Model, 17, "FgSecondary");
        ic.VerticalAlignment = VerticalAlignment.Center;
        ic.Margin = new Thickness(0, 0, 10, 0);
        left.Children.Add(ic);
        left.Children.Add(name);
        left.Children.Add(peak);

        var check = new CheckBox
        {
            IsChecked = _checked.Contains(c.Id),
            VerticalAlignment = VerticalAlignment.Center,
            // ★ 两列必须能一眼分清是哪一列 —— 只靠位置的话,勾错的那次没人看得出来。
            ToolTip = "常驻:一直装着。★ 系统【不会】自动改这一列 —— 一个字节都不会。",
        };
        check.Checked += (_, _) => { _checked.Add(c.Id); Recompute(); };
        check.Unchecked += (_, _) => { _checked.Remove(c.Id); Recompute(); };

        // ── 第二列:允许按需装载(D90 裁定① —— 那次必须存在的授权)──
        var onDemand = new CheckBox
        {
            IsChecked = _permitted.Contains(c.Id),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 18, 0),
            ToolTip = "按需:授权系统在你用到它的时候自动装、空闲 10 分钟后自动卸。\n"
                      + "★ 不勾就没有按需 —— 没有这次授权,系统就是在你没同意的情况下自己动显存。\n"
                      + "★ 这一列只能在【主机】上改。",
        };
        onDemand.Checked += (_, _) => { _permitted.Add(c.Id); Recompute(); };
        onDemand.Unchecked += (_, _) => { _permitted.Remove(c.Id); Recompute(); };

        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 5, 0, 0) };
        DockPanel.SetDock(check, Dock.Right);
        row.Children.Add(check);
        DockPanel.SetDock(onDemand, Dock.Right);
        row.Children.Add(onDemand);
        row.Children.Add(left);

        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        stack.Children.Add(row);

        // ★ 此刻是不是**正按需装着** —— 这是事实,不是勾选。分开说,免得读成"我勾过它"。
        if (c.TransientResident)
        {
            var live = new TextBlock
            {
                Text = "· 此刻正按需装着(空闲后会自动卸,不是你勾的常驻)",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(27, 1, 0, 0),
            };
            live.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
            live.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            stack.Children.Add(live);
        }

        // ★ 勾掉它会停掉哪些功能 —— 别名映射由中枢下发(S2 的桥),客户端不自己猜。
        //   没有任何别名指向它时**明说**,而不是留白:留白读起来像"没查到",
        //   而真相是"这个组件今天不服务任何对外功能"。
        var sub = c.Aliases.Count > 0
            ? "支撑:" + string.Join("、", c.Aliases)
            : "★ 今天没有任何对外功能指向它(勾了也不会被谁用到)";
        var subTb = new TextBlock { Text = sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(27, 0, 0, 0) };
        subTb.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        subTb.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        stack.Children.Add(subTb);

        if (!string.IsNullOrWhiteSpace(c.Note))
        {
            // ★ 这是**测量出处**,原样照抄,不改写成宣传语。
            var note = new TextBlock { Text = c.Note, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(27, 1, 0, 0) };
            note.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
            note.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            note.Opacity = 0.75;
            stack.Children.Add(note);
        }
        return stack;
    }

    /// <summary>
    /// 重算两道墙。★★ 必须分开算、分开说 —— 见文件头规矩 ①。
    /// ★ 这里算出来的只是【预览】:中枢在点确定那一刻会重新求值,期间桌面会变。
    /// </summary>
    void Recompute()
    {
        if (_catalog is not { } cat) return;
        var unknown = new List<string>();
        var sum = VramBudget.PeakSumGiB(_checked, unknown);

        // ── 第一段:已选组件 ────────────────────────────────────
        // ★ 中枢下发的 id 本地 toml 里找不到 ⇒ 两份配置漂了。**说出来**,不静默按 0 计。
        var sumText = $"已选组件　　　已选 {_checked.Count} 项,合计峰值 = {sum:0.00} GiB　← 估算";
        if (unknown.Count > 0)
            sumText += $"(★ 有 {unknown.Count} 项本机算不出峰值:{string.Join("、", unknown)} —— "
                       + "本机与中枢的显存配置对不上了,合计值偏低,以中枢判定为准)";
        _sumLine.Text = sumText;

        // ── 第二段:你的桌面预留 → vram_budget ⇒ 撞它时改预留【有用】──
        var overStatic = sum - cat.VramBudget;
        if (overStatic > 1e-9)
            Danger(_wallStatic, $"你的桌面预留　desktop_floor {cat.DesktopFloor:0.00} → vram_budget {cat.VramBudget:0.00}"
                                + $"(总量 {cat.TotalGiB:0.00} − 预留 {cat.DesktopFloor:0.00} − 安全余量 {cat.SafetyMargin:0.00})"
                                + $"　★ 超 {overStatic:0.00} GiB。→ 这堵墙【可以】靠调小桌面预留让开,或者少选几个。");
        else
            Muted(_wallStatic, $"你的桌面预留　desktop_floor {cat.DesktopFloor:0.00} → vram_budget {cat.VramBudget:0.00}"
                               + $"(还余 {cat.VramBudget - sum:0.00})");

        // ── 第三段:此刻实际可用 = NVML free − 安全余量 ⇒ 撞它时改预留【没用】──
        if (cat.FreeGiB is { } free)
        {
            var usable = free - cat.SafetyMargin;
            Muted(_wallDynamic, $"此刻实际可用　NVML free {free:0.00} − {cat.SafetyMargin:0.00} = {usable:0.00} GiB");

            // ══════════════════════════════════════════════════════
            //  ★★★ 最后那一行 —— 方案书 §8.1 原文:
            //  「**★ 最后那一行是本界面存在的理由。** 只显示「装得下」是不够的 ——
            //    桌面占用是**波动**的,用户需要知道自己离墙有多远,而不是知道此刻没撞墙。」
            //
            //  ★ 它此前**整行缺席**:面板只说「装完还剩 X」,而"还剩"与
            //    「还能让桌面再涨多少才会出问题」是**两个不同的数**
            //    (前者没减安全余量),读起来也是两句不同的话。
            //  ★★ 它算的是 (free − safety_margin) − Σpeak,与 §8.1 的例子逐字对得上:
            //     14.86 − 0.8 = 14.06;14.06 − 11.4 = 2.66。
            // ══════════════════════════════════════════════════════
            var headroom = usable - sum;
            if (headroom >= 0 && overStatic <= 1e-9)
                Muted(_headroom, $"可以确定 ✓　　还能让桌面再涨 {headroom:0.00} GiB 才会出问题");
            else if (overStatic > 1e-9)
                // 撞的是预算墙 —— 这一行不谈"再涨多少",谈了会把人支去关程序(而那没用)。
                Danger(_headroom, $"不能确定 ✗　　撞的是【显存预算】(超 {overStatic:0.00} GiB)—— "
                                  + "改桌面预留有用,关程序没有用。");
            else
                Danger(_headroom, $"不能确定 ✗　　此刻【已经】差 {-headroom:0.00} GiB —— "
                                  + "撞的是物理墙,【改桌面预留没有用】,得关掉正在占显存的程序。");
        }
        else
        {
            Danger(_wallDynamic, "此刻实际可用　中枢这一轮没读到实时可用显存 —— 装不装得下"
                                 + "【现在算不出来】(不拿旧值冒充)。点确定时中枢会重新求值。");
            // ★ 读不到 free 就**不显示**那一行 —— 它需要 free 才算得出来,
            //   编一个"还能再涨 N"比不说更坏(那正是这一行存在要防的事:让人以为自己知道离墙多远)。
            Muted(_headroom, "还能让桌面再涨多少　—— 读不到实时可用显存,这一行现在算不出来。");
        }
    }

    async Task ApplyAsync()
    {
        if (_busy || _catalog is null) return;
        _busy = true; _apply.IsEnabled = false;
        try
        {
            // ★ 世代号取【当前推送流里那份】,不是面板加载时那份 —— 用户挑选期间中枢可能已经变了。
            //   拿旧号提交会稳定收到 409,而那本可以避免:409 该留给"真的有人同时改了"。
            var gen = Gpu.Snapshot?.Generation ?? _catalog.Generation;
            SetStatus("正在提交…");
            var res = await Gpu.ApplyAsync(_checked.ToList(), gen, interruptRunning: false,
                                                  permittedOnDemand: PermittedPayload());
            if (res.Ok)
            {
                SetStatus("✔ " + res.Advice);
            }
            else if (res.Code == "generation_conflict")
            {
                // ★ 冲突不是错误,是"你手上那份过期了"。自动取回最新并让用户复核 ——
                //   但**不**自动重提交:那等于替用户按了确定。
                SetStatus(res.Advice, danger: true);
                await LoadAsync();
            }
            else if (res.Code == "needs_user_choice")
            {
                var ok = ConfirmDialog.Show("有任务正在跑",
                    "现在变更驻留组件会打断它。\n\n" +
                    // ★ 审计 C4:原来拼的是 lease_id(secrets.token_hex(8)),
                    //   用户看到「正在跑:a3f9c1d2e8b74501」—— 说不出是谁在占,也就无从决定。
                    //   现在说清:什么在占 · 谁的 · 已多久 · 能不能被自动让开。
                    (res.Blocking.Count > 0
                        ? "正在跑:" + string.Join("、", res.Blocking.Select(b => b.Describe())) + "\n\n"
                        : "") +
                    "选『优雅中断』会给它一次收尾的机会;选『等它跑完』就先不改。",
                    confirmText: "优雅中断", cancelText: "等它跑完");
                if (ok)
                {
                    var gen2 = Gpu.Snapshot?.Generation ?? gen;
                    var res2 = await Gpu.ApplyAsync(_checked.ToList(), gen2, interruptRunning: true,
                                                           permittedOnDemand: PermittedPayload());
                    SetStatus((res2.Ok ? "✔ " : "") + res2.Advice, danger: !res2.Ok);
                }
                else SetStatus("已取消 —— 没有改动任何东西。");
            }
            else
            {
                // ★★ 每种失败自己的下一步。特别是 loader_absent:必须说清**没有生效**,
                //   不能让用户以为模型装上了。
                SetStatus(res.Advice, danger: true);
            }
        }
        catch (Exception ex) { SetStatus("提交失败:" + ex.Message, danger: true); }
        finally { _busy = false; _apply.IsEnabled = _catalog is not null; }
    }

    /// <summary>
    /// 这次提交要不要带上「按需授权」。★★ 用户**没动过**那一列就返回 <c>null</c>(= 省略)。
    /// <para>
    /// 三条理由缺一不可:
    /// ① 服务端把「省略」与「空数组」当成两件事(不动授权 / 撤销全部),
    ///    每次都发等于把"撤销全部"的语义交给一个用户没碰过的控件;
    /// ② 那一列**只有主机能写**(审计 B6)—— 副机每次普通变更都带上它,
    ///    会稳定撞 403,而用户明明只是勾了个常驻组件;
    /// ③ 顺序无关的幂等:发的是集合相等判断,不是"我记得我改过"。
    /// </para>
    /// ★ 抽成方法(而不是在两处各写一遍)是因为「优雅中断」那条路会**再提交一次** ——
    ///   两处若不一致,重试那一次就会带上一个与第一次不同的授权集合。
    /// </summary>
    internal IReadOnlyList<string>? PermittedPayload() =>
        _permitted.SetEquals(_permittedAsFetched) ? null : _permitted.ToList();

    void SetStatus(string text, bool danger = false)
    {
        _status.Text = text;
        _status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        // ★★ V21:这里原来是写死的 `Color.FromRgb(0xD9,0x30,0x25)`。搬进管理端之后被那条
        //   「管理端里不得出现颜色字面量」的断言当场抓住 —— 而它抓得对:
        //   写死的颜色**换肤时不跟着变**,而两个 exe 各自渲染,漂了不会有任何东西红。
        //   ★ 这段代码在客户端里躲过了那条断言,只是因为那条断言的范围是 admin/ ——
        //     不是因为它在客户端里就没问题。搬家把一个一直存在的问题照出来了。
        _status.SetResourceReference(TextBlock.ForegroundProperty, danger ? "RiskDanger" : "FgMuted");
    }

    static void Danger(TextBlock t, string s)
    {
        t.Text = s;
        // ★ 同上:颜色走令牌(RiskDanger),不写死 —— 见 SetStatus 上方那段。
        t.SetResourceReference(TextBlock.ForegroundProperty, "RiskDanger");
    }

    static void Muted(TextBlock t, string s)
    {
        t.Text = s;
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
    }
}
