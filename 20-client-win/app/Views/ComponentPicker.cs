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
//   ③ **今天点确定必然得到 loader_absent** —— 中枢还没有装载器(那是 P5)。
//      面板如实说出这件事,**不假装模型装上了**。这不是缺陷提示,是当前的真实行为:
//      勾选会被记下并通过三道闸,但显存里不会真的多出模型来。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class ComponentPicker : UserControl
{
    static App TheApp => (App)Application.Current;

    readonly StackPanel _list = new();
    readonly TextBlock _sumLine = new();
    readonly TextBlock _wallStatic = new();
    readonly TextBlock _wallDynamic = new();
    readonly TextBlock _status = new();
    readonly Button _apply = new() { Content = "确定", Padding = new Thickness(18, 7, 18, 7) };
    readonly Button _reload = new() { Content = "重新取", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };

    GpuCatalog? _catalog;
    readonly HashSet<string> _checked = new(StringComparer.Ordinal);
    bool _busy;

    public ComponentPicker()
    {
        _sumLine.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        foreach (var t in new[] { _wallStatic, _wallDynamic, _status })
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

        var root = new StackPanel();
        root.Children.Add(_list);
        root.Children.Add(new Border { Height = 8 });
        root.Children.Add(_sumLine);
        root.Children.Add(_wallStatic);
        root.Children.Add(_wallDynamic);
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
            var cat = await TheApp.Gpu.FetchCatalogAsync();
            if (cat is null)
            {
                // ★ 取不到就**什么都不列**。列一份本地兜底清单等于回到"第三套词汇",
                //   而且用户会以为那就是中枢的真实清单。
                _catalog = null;
                SetStatus("取不到中枢的组件清单 —— 这里【不显示】任何清单,"
                          + "因为唯一权威在中枢。" + (TheApp.Gpu.LastError is { } e ? "(" + e + ")" : ""),
                          danger: true);
                _apply.IsEnabled = false;
                return;
            }
            _catalog = cat;
            _checked.Clear();
            foreach (var c in cat.Components) if (c.Intended) _checked.Add(c.Id);
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

        var check = new CheckBox { IsChecked = _checked.Contains(c.Id), VerticalAlignment = VerticalAlignment.Center };
        check.Checked += (_, _) => { _checked.Add(c.Id); Recompute(); };
        check.Unchecked += (_, _) => { _checked.Remove(c.Id); Recompute(); };

        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 5, 0, 0) };
        DockPanel.SetDock(check, Dock.Right);
        row.Children.Add(check);
        row.Children.Add(left);

        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        stack.Children.Add(row);

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
        // ★ 中枢下发的 id 本地 toml 里找不到 ⇒ 两份配置漂了。**说出来**,不静默按 0 计。
        var sumText = $"已选 {_checked.Count} 项,合计峰值 {sum:0.00} GiB";
        if (unknown.Count > 0)
            sumText += $"(★ 有 {unknown.Count} 项本机算不出峰值:{string.Join("、", unknown)} —— "
                       + "本机与中枢的显存配置对不上了,合计值偏低,以中枢判定为准)";
        _sumLine.Text = sumText;

        // ── 墙一:静态。Σpeak vs vram_budget ⇒ 改桌面预留【有用】──
        var overStatic = sum - cat.VramBudget;
        if (overStatic > 1e-9)
            Danger(_wallStatic, $"① 超出显存预算 {overStatic:0.00} GiB(预算 {cat.VramBudget:0.00} = "
                                + $"总量 {cat.TotalGiB:0.00} − 桌面预留 {cat.DesktopFloor:0.00} − 安全余量 {cat.SafetyMargin:0.00})。"
                                + "→ 这堵墙**可以**靠调小桌面预留让开,或者少选几个。");
        else
            Muted(_wallStatic, $"① 预算内:{sum:0.00} / {cat.VramBudget:0.00} GiB(还余 {cat.VramBudget - sum:0.00})");

        // ── 墙二:动态。free − Σpeak vs safety_margin ⇒ 改预留【没用】──
        if (cat.FreeGiB is { } free)
        {
            var after = free - sum;
            if (after < cat.SafetyMargin)
                Danger(_wallDynamic, $"② 此刻装不下:可用 {free:0.00} GiB,装完只剩 {after:0.00}(需 ≥ {cat.SafetyMargin:0.00})。"
                                     + "→ 这堵墙**改桌面预留没有用**,它是物理墙 —— 得去关掉正在占显存的程序。");
            else
                Muted(_wallDynamic, $"② 此刻可用 {free:0.00} GiB,装完还剩 {after:0.00}");
        }
        else
        {
            Danger(_wallDynamic, "② 中枢这一轮没读到实时可用显存 —— 装不装得下**现在算不出来**"
                                 + "(不拿旧值冒充)。点确定时中枢会重新求值。");
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
            var gen = TheApp.Gpu.Snapshot?.Generation ?? _catalog.Generation;
            SetStatus("正在提交…");
            var res = await TheApp.Gpu.ApplyAsync(_checked.ToList(), gen, interruptRunning: false);
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
                    var gen2 = TheApp.Gpu.Snapshot?.Generation ?? gen;
                    var res2 = await TheApp.Gpu.ApplyAsync(_checked.ToList(), gen2, interruptRunning: true);
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

    void SetStatus(string text, bool danger = false)
    {
        _status.Text = text;
        _status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (danger) _status.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x30, 0x25));
        else _status.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
    }

    static void Danger(TextBlock t, string s)
    {
        t.Text = s;
        t.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x30, 0x25));
    }

    static void Muted(TextBlock t, string s)
    {
        t.Text = s;
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
    }
}
