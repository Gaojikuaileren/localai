// P3c -- 设置。设计 §7:皮肤在【设置 › 外观】里改,每台设备独立。
// 用户明确要求的两个开关也在这里:开机自启 · 关窗口留在托盘。

using System.Windows;
using System.Windows.Controls;
using LocalAI.Client.I18n;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class SettingsView : UserControl
{
    App TheApp => (App)Application.Current;

    public SettingsView()
    {
        var s = TheApp.Settings;

        // ---- 外观:皮肤 ----
        var skin = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var k in new[] { Skin.Breeze, Skin.Ink, Skin.Warm })
            skin.Items.Add(new ComboBoxItem { Content = Strings.Get("settings.skin." + k.ToString().ToLowerInvariant()), Tag = k });
        skin.SelectedIndex = (int)s.Skin;
        skin.SelectionChanged += (_, _) =>
        {
            if (skin.SelectedItem is ComboBoxItem { Tag: Skin picked })
            {
                s.Skin = picked; s.Save();
                ThemeManager.Apply(picked);   // 即时生效,不需重启
                // 窗口圆角也是皮肤的一部分(暖萌大 / 微风中 / 墨白小),跟着一起换
                if (Application.Current.MainWindow is { } w) WindowCorners.Apply(w, picked);
            }
        };

        // ---- 语言 ----
        var lang = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var l in Strings.Languages) lang.Items.Add(new ComboBoxItem { Content = l switch { "zh-CN" => "简体中文", "en-US" => "English", _ => "日本語" }, Tag = l });
        lang.SelectedIndex = Array.IndexOf(Strings.Languages, s.Language);
        lang.SelectionChanged += (_, _) =>
        {
            if (lang.SelectedItem is ComboBoxItem { Tag: string picked })
            {
                s.Language = picked; s.Save();
                Strings.Language = picked;   // 触发 LanguageChanged -> 界面就地重建,无需重启
            }
        };

        // ---- 母语(翻译兜底级联要用)----
        var native = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        native.Items.Add(new ComboBoxItem { Content = "跟随界面语言", Tag = "" });
        foreach (var l in Languages.Catalog) native.Items.Add(new ComboBoxItem { Content = l.Name, Tag = l.Code });
        native.SelectedIndex = string.IsNullOrWhiteSpace(s.NativeLangOverride)
            ? 0 : Math.Max(0, Array.FindIndex(Languages.Catalog, x => x.Code == s.NativeLangOverride) + 1);
        native.SelectionChanged += (_, _) =>
        {
            if (native.SelectedItem is ComboBoxItem { Tag: string code })
            {
                s.NativeLangOverride = string.IsNullOrEmpty(code) ? null : code;
                s.Save();
            }
        };

        // ---- 界面音效 ----
        var sfx = new CheckBox
        {
            Content = "界面音效",
            IsChecked = s.SoundEffects,
            Margin = new Thickness(0, 12, 0, 0),
        };
        sfx.Checked += (_, _) => { s.SoundEffects = true; s.Save(); };
        sfx.Unchecked += (_, _) => { s.SoundEffects = false; s.Save(); };

        // ---- 开机自启(用户明确要求)----
        var auto = new CheckBox
        {
            Content = Strings.Get("settings.autostart"),
            IsChecked = Autostart.IsEnabled(),
            Margin = new Thickness(0, 6, 0, 0),
        };
        auto.Checked += (_, _) => { Autostart.Enable(); s.Autostart = true; s.Save(); };
        auto.Unchecked += (_, _) => { Autostart.Disable(); s.Autostart = false; s.Save(); };

        // 已登记但被「任务管理器 › 启动应用」禁用时,如实说明 —— 否则开关显示"开"却不启动,
        // 用户只会以为程序坏了(审查发现 [13])。
        var blocked = Ui.Caption("已在「任务管理器 › 启动应用」里被禁用,所以开机不会启动。要恢复请在那里重新启用。");
        blocked.Visibility = Autostart.IsEnabled() && Autostart.IsBlockedByWindows()
                             ? Visibility.Visible : Visibility.Collapsed;

        // ---- 关窗口留在托盘(用户明确要求)----
        var tray = new CheckBox
        {
            Content = Strings.Get("settings.close_to_tray"),
            IsChecked = s.MinimizeToTrayOnClose,
            Margin = new Thickness(0, 8, 0, 0),
        };
        tray.Checked += (_, _) => { s.MinimizeToTrayOnClose = true; s.Save(); };
        tray.Unchecked += (_, _) => { s.MinimizeToTrayOnClose = false; s.Save(); };

        Content = Ui.Page(
            Ui.Title(Strings.Get("nav.settings")),

            Ui.Card(Ui.Stack(
                Ui.Subtitle(Strings.Get("settings.appearance")),
                Ui.Body(Strings.Get("settings.skin")), skin,
                new Border { Height = 12 },
                Ui.Body(Strings.Get("settings.language")), lang,
                new Border { Height = 12 },
                Ui.Body("母语"), native,
                Ui.Caption("翻译时如果目标池算不出目标(比如池里只有中文、你输入的也是中文),会先翻成母语。"),
                sfx,
                Ui.Caption("拖动语言卡片落地时的轻响。★ 只有暖萌皮肤会出声 —— 微风与墨白本来就是克制的。"),
                new Border { Height = 8 },
                Ui.Caption("皮肤、语言与音效都是**每台设备**各自的选择,不会同步到其它电脑。")
            )),

            Ui.Card(Ui.Stack(
                Ui.Subtitle("启动与后台"),
                auto,
                blocked,
                Ui.Caption("以当前用户身份自启(不提权)。客户端必须保持普通用户运行,否则设备密钥打不开(见决议 D46)。"),
                tray,
                Ui.Caption("勾选后点窗口的 × 只是收起窗口,程序继续在托盘运行;要真正退出请用托盘图标右键 →「退出」。")
            )),

            AudioDriverCard(),

            LanguagePoolCard(),

            AppleSyncCard(),

            // 存储与清理 + 记忆库编辑(用户裁定:一键清爽 + 勾选执行内容;记忆可预览删减)
            new StorageView(),

            // 连接与设备:已配对的电脑、配对/解除 —— 从独立导航项并入设置(用户裁定)
            Ui.Subtitle(Strings.Get("devices.title")),
            new DevicesView(embedded: true)
        );
    }

    // ---------------------------------------------------------------- 声音驱动(同传要用的虚拟声卡)
    Border? _driverCard;
    readonly StackPanel _driverBody = new();

    /// <summary>
    /// 同声传译要把译文语音送进会议软件,而 Windows 上【没有】不写内核驱动就能被别的软件
    /// 选中的麦克风 —— 所以要装一个虚拟声卡。这里把它的状态、版本、更新集中管起来。
    ///
    /// ★★ 三条硬规则(用户确认:自动 ≠ 不透明):
    ///   ① 安装包必须过 SHA-256 校验才允许运行,不过就删掉,【不给"仍然继续"】;
    ///   ② 界面上如实写清来源、版本、它会做什么 —— "察觉不到它的存在"指的是不用你操作,不是不告诉你;
    ///   ③ 允许自备安装包(离线也能装)。
    /// </summary>
    public Border AudioDriverCard()
    {
        RefreshDriver();
        _driverCard = Ui.Card(Ui.Stack(
            Ui.Subtitle("声音驱动(同声传译)"),
            Ui.Caption($"同传要把译文语音送进会议软件,这需要一个虚拟声卡。我们用的是 {AudioDriver.Vendor} 的 " +
                       $"{AudioDriver.ProductName} —— ★ 这是【第三方内核驱动】,不是我们写的,如实告知。"),
            Ui.Caption("装好之后你不需要打开它、也不需要做任何设置;只需在会议软件里把麦克风选成 CABLE Output。"),
            _driverBody));
        return _driverCard;
    }

    void RefreshDriver()
    {
        _driverBody.Children.Clear();
        var st = AudioDriver.Detect();

        var line = Ui.Body(st.Installed
            ? $"已安装 · 版本 {st.Version ?? "(读不到)"}"
            : "未安装");
        line.Margin = new Thickness(0, 8, 0, 0);
        line.SetResourceReference(TextBlock.ForegroundProperty, st.Installed ? "FgPrimary" : "RiskWarning");
        _driverBody.Children.Add(line);
        if (st.DriverPath is { Length: > 0 })
            _driverBody.Children.Add(Ui.Caption("驱动文件:" + st.DriverPath));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(Ui.Secondary("重新检测", (_, _) => { RefreshDriver(); }));
        var check = Ui.Secondary("检查更新", (_, _) => CheckDriverUpdate());
        check.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(check);

        var pkg = AudioDriver.FindOfflinePackage();
        if (!st.Installed || pkg is not null)
        {
            var install = Ui.Primary(st.Installed ? "重新安装 / 更新" : "一键安装", (_, _) => InstallDriver());
            install.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(install);
        }
        _driverBody.Children.Add(row);

        _driverStatus.TextWrapping = TextWrapping.Wrap;
        _driverStatus.Margin = new Thickness(0, 8, 0, 0);
        _driverStatus.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        _driverStatus.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        _driverBody.Children.Add(_driverStatus);

        _driverBody.Children.Add(Ui.Caption(
            $"离线安装:把官方安装包放到 {AudioDriver.OfflineDir} 再点安装 —— 完全断网的机器也能装上。"));
        _driverBody.Children.Add(Ui.Caption(
            "★ 安装那一步会弹一次系统的管理员提示 —— 装内核驱动必须管理员,这一步谁也绕不过;" +
            "本程序自身【始终以普通权限运行】(见决议 D46),只在这一刻另起一个提权子进程。"));
    }

    readonly TextBlock _driverStatus = new();

    void CheckDriverUpdate()
    {
        AudioDriverManifest.Reload();
        var pkg = AudioDriverManifest.Current;
        _driverStatus.Text = AudioDriver.Compare(AudioDriver.Detect(), pkg, DateTime.Now);
    }

    void InstallDriver()
    {
        var local = AudioDriver.FindOfflinePackage();
        if (local is not null)
        {
            // 自备的包也要校验 —— 除非清单里没有这个版本的哈希,那就如实说"没法校验"
            var pkg = AudioDriverManifest.Current;
            if (pkg is not null && !AudioDriver.Verify(local, pkg.Sha256))
            {
                _driverStatus.Text = "本地安装包与清单里的哈希对不上 —— 已拒绝运行。" +
                                     "请删掉它重新下载,或确认你放的是官方原包。";
                return;
            }
            AudioDriver.RunInstaller(local, out var msg);
            _driverStatus.Text = msg;
            return;
        }
        DownloadThenInstall();
    }

    async void DownloadThenInstall()
    {
        var pkg = AudioDriverManifest.Current;
        if (pkg is null) { _driverStatus.Text = "没有可用的版本清单,请自备安装包(见下方离线安装说明)。"; return; }

        _driverStatus.Text = $"正在下载 {pkg.Version}({pkg.Bytes / 1024} KB)…";
        var progress = new Progress<double>(p => _driverStatus.Text = $"正在下载 {pkg.Version} … {p:P0}");
        var (ok, path, msg) = await AudioDriver.DownloadAsync(pkg, progress);
        _driverStatus.Text = msg;
        if (!ok) return;
        AudioDriver.RunInstaller(path, out var m2);
        _driverStatus.Text = msg + " " + m2;
    }

    /// <summary>从同传界面跳进来:滚到声音驱动这块并框出来。</summary>
    public void RevealAudioDriver() => Reveal(_driverCard);

    Border? _langCard;
    readonly StackPanel _langBody = new();

    /// <summary>
    /// 翻译工作空间的【常用语言池】:增删这里的语言,翻译空间的语言池就跟着变。
    /// ★ 默认只有中/日/英/德/韩(用户裁定);其余在目录里,想用再加 —— 池子太长不好拖。
    /// </summary>
    public Border LanguagePoolCard()
    {
        RefreshLangPool();
        _langCard = Ui.Card(Ui.Stack(
            Ui.Subtitle("翻译语言池"),
            Ui.Caption("这里决定翻译空间【常用语言池】里有哪些语言。目标池最多同时选 3 个。"),
            _langBody));
        return _langCard;
    }

    void RefreshLangPool()
    {
        var s = TheApp.Settings;
        _langBody.Children.Clear();

        var inPool = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var code in s.TranslationPool.ToList())
        {
            var l = Languages.Find(code);
            if (l is null) continue;
            inPool.Children.Add(LangPill(l, inPool: true, () =>
            {
                s.TranslationPool.Remove(code);
                s.Save();
                TheApp.Translation.RemoveTarget(code);   // 从池里删掉的,目标池也不该再留着
                RefreshLangPool();
            }));
        }
        if (inPool.Children.Count == 0) inPool.Children.Add(Ui.Caption("语言池是空的 —— 下面加几个。"));
        _langBody.Children.Add(Ui.Caption("池内(点可移除)"));
        _langBody.Children.Add(inPool);

        // ★ 可添加改成【下拉菜单列全部语言】(用户裁定):平铺成一排小药丸时,
        //   语言一多就换行成一大片,而且看不出"总共有哪些可选"。下拉是一个完整的清单。
        var rest = Languages.Catalog.Where(x => !s.TranslationPool.Contains(x.Code)).ToList();
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        if (rest.Count == 0)
        {
            addRow.Children.Add(Ui.Caption("目录里的语言都已经在池中了。"));
        }
        else
        {
            var pick = new ComboBox { Width = 200, VerticalAlignment = VerticalAlignment.Center };
            foreach (var l in rest) pick.Items.Add(new ComboBoxItem { Content = $"{l.Name}({l.Native})", Tag = l.Code });
            pick.SelectedIndex = 0;
            var add = Ui.Secondary("添加", (_, _) =>
            {
                if (pick.SelectedItem is not ComboBoxItem { Tag: string code }) return;
                s.TranslationPool.Add(code);
                s.Save();
                RefreshLangPool();
            });
            add.Margin = new Thickness(8, 0, 0, 0);
            addRow.Children.Add(pick);
            addRow.Children.Add(add);
        }
        _langBody.Children.Add(Ui.Caption("可添加(下拉里是全部语言)"));
        _langBody.Children.Add(addRow);
    }

    static FrameworkElement LangPill(Lang l, bool inPool, Action onClick)
    {
        var t = new TextBlock { Text = inPool ? l.Name : "+ " + l.Name, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, inPool ? "FgOnAccent" : "FgSecondary");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        var b = new Border
        {
            Child = t, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        b.SetResourceReference(Border.BackgroundProperty, inPool ? "Accent" : "BgSurface");
        b.SetResourceReference(Border.BorderBrushProperty, inPool ? "Accent" : "Border");
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    /// <summary>从翻译空间跳进来:滚到语言池并用橙色虚线框出来(5 秒或切界面后消退)。</summary>
    public void RevealLanguagePool() => Reveal(_langCard);

    /// <summary>
    /// 与 Apple 同步(日历 / 提醒事项)。★ 目前是【预留板块】:接入方式与所需字段还没定,
    /// 这里如实说明现状与规则,不放假开关、不谎称已连接。主页日历/待办的图标 hover 变齿轮点进来。
    /// </summary>
    public Border AppleSyncCard()
    {
        var card = Ui.Card(Ui.Stack(
            Ui.Subtitle("与 Apple 同步(日历 / 提醒事项)"),
            Ui.Body("尚未接入。", muted: true),
            new Border { Height = 6 },
            Ui.Body("现在:日历与待办都是【本机数据】,新增/编辑当场生效并保存在本机。"),
            Ui.Caption("接入后按【增量合并】双向同步,不是全局覆盖:已有的不重复加、没有的才加、" +
                       "★ 绝不用空日程覆盖已有;本机独有的反向推给 Apple(见决议 D50 补充)。"),
            new Border { Height = 10 },
            Ui.Body("接入前还需要确定(留待后续):"),
            Ui.Caption("· 用哪条通路(CalDAV / 本机 Apple 应用桥接)与相应的凭据保管方式;"),
            Ui.Caption("· 同步哪些日历组 / 提醒事项清单,以及冲突时以哪边为准;"),
            Ui.Caption("· 与对方相关的日程只发邀请/修改建议,遵守 D45 的可见范围规则。")
        ));
        _appleSyncCard = card;
        return card;
    }

    Border? _appleSyncCard;

    /// <summary>从主页齿轮跳进来:把"与 Apple 同步"这块滚到可视区并框出来。</summary>
    public void RevealAppleSync() => Reveal(_appleSyncCard);

    /// <summary>
    /// 跳到某个设置板块:滚过去 + 橙色虚线框(5 秒或切界面后自然消退)。
    /// ★★ 此前是"闪一下透明度":DoubleAnimation(0.35 -> 1) 配 AutoReverse + RepeatBehavior(2)
    ///   【收在起点 0.35 上】,又没设 FillBehavior.Stop —— 动画结束后把 0.35 一直按着,
    ///   板块从此永久停在 35% 不透明度,看着就是"变灰了"(用户实测的 bug)。
    ///   而且闪透明度改的是内容本身,我们只想【指出位置】—— 所以改用装饰层画框,
    ///   不进布局、不改被指的元素一个像素。
    /// </summary>
    /// <summary>
    /// 把某块滚到滚动区的【正中】。
    /// ★ BringIntoView 只保证看得见 —— 它滚的是最小距离,于是一块高卡片会贴着视口顶边停住,
    ///   看起来就是把目标栏的顶部放在了中间(用户实测)。要居中就得自己算偏移量。
    /// </summary>
    static void CenterInView(FrameworkElement card)
    {
        var sv = FindScrollHost(card);
        if (sv is null) { card.BringIntoView(); return; }

        var top = card.TransformToAncestor(sv).Transform(new Point(0, 0)).Y + sv.VerticalOffset;
        var target = top - (sv.ViewportHeight - card.ActualHeight) / 2;   // 让卡片中心对上视口中心
        sv.ScrollToVerticalOffset(Math.Max(0, Math.Min(target, sv.ScrollableHeight)));
    }

    static ScrollViewer? FindScrollHost(DependencyObject? node)
    {
        for (var n = node; n is not null; n = System.Windows.Media.VisualTreeHelper.GetParent(n))
            if (n is ScrollViewer sv) return sv;
        return null;
    }

    void Reveal(FrameworkElement? card)
    {
        if (card is null) return;

        void Go()
        {
            CenterInView(card);            // ★ 放到【视觉中心】,不是刚好露出来
            RevealHighlight.Show(card);
        }

        // ★★ 时机很讲究:在 Loaded 优先级上做,元素还没排完版 —— BringIntoView 拿到的
        //   位置是空的,于是滚不动、停在页面顶部(用户实测)。ContextIdle 排在布局与渲染【之后】,
        //   那时才有真实坐标。页面若还没加载完,先等它的 Loaded 再排队。
        //   装饰层同理:AdornerLayer 要元素进了可视树才拿得到。
        if (card.IsLoaded) Dispatcher.BeginInvoke(new Action(Go), System.Windows.Threading.DispatcherPriority.ContextIdle);
        else card.Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(Go), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }
}
