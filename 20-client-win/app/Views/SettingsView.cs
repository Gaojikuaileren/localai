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

        // ---- 界面用词:家庭 / 团队(用户裁定 2026-07-31)----
        // ★ 只换界面文案里的称谓,不动任何存储值(见 Services/Vocab 头部的纪律)。
        var vocab = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var v in new[] { OrgVocab.Family, OrgVocab.Team })
            vocab.Items.Add(new ComboBoxItem { Content = Vocab.LabelOf(v, s.Language), Tag = v });
        vocab.SelectedIndex = (int)s.OrgVocab;
        vocab.SelectionChanged += (_, _) =>
        {
            if (vocab.SelectedItem is ComboBoxItem { Tag: OrgVocab picked })
            {
                s.OrgVocab = picked; s.Save();
                Vocab.Current = picked;   // 触发 Changed -> 界面就地重建(与换语言同一条路)
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
                new Border { Height = 12 },
                Ui.Body("称呼这群人"), vocab,
                // ★ 这两行说明【不能】自己写上"家庭""团队"这两个词 —— 它们会被界面用词表就地替换,
                //   于是句子变成"…的『团队』会整体换成『团队』",自己把自己说糊涂了。
                //   所以描述成"这个称谓",不点名具体词。
                Ui.Caption("界面里把「共用这台中枢的这群人」叫什么。改了之后,成员、可见范围、动态等处的这个称谓会整体跟着变。"),
                Ui.Caption("★ 只改界面用词 —— 不动任何已存的数据(日程分组、待办范围等存的仍是原值);Apple 家庭共享这类产品名也照旧。"),
                sfx,
                Ui.Caption("拖动语言卡片落地时的轻响。★ 只有暖萌皮肤会出声 —— 微风与墨白本来就是克制的。"),
                new Border { Height = 8 },
                Ui.Caption("皮肤、语言与音效都是「每台设备」各自的选择,不会同步到其它电脑。")
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
            Ui.Caption("「一键安装」从 VB-Audio 官方 https 下载,提权运行前会验证它由 VB-Audio 官方签名(证书链有效),验不过就拒绝。"),
            Ui.Caption("装好之后你不需要打开它、也不需要做任何设置;只需在会议软件里把麦克风选成 CABLE Output。"),
            _driverBody));

        // ★★ 事件驱动刷新,不轮询(用户裁定 2026-07-31:跑检测不该定时,太不性能友好):
        //   安装 / 卸载走的是【外部的官方安装器】(另一个提权进程),它跑完我们无从得知确切时刻。
        //   但用户装完/卸完总会【切回本应用】—— 那一下 Window.Activated 触发,正好刷一次:
        //   状态、安装位置、按钮标签(删了安装包 -> "重新下载")全都跟着更新。
        //   只在这张卡活着时挂,卡卸载即摘,不留常驻表。Detect 只在此刻按需跑一次,不是每 N 秒扫一遍。
        _driverCard.Loaded += (_, _) => { if (Application.Current.MainWindow is { } w) w.Activated += OnActivatedRefreshDriver; };
        _driverCard.Unloaded += (_, _) => { if (Application.Current.MainWindow is { } w) w.Activated -= OnActivatedRefreshDriver; };
        return _driverCard;
    }

    long _lastDriverRefreshTick;
    void OnActivatedRefreshDriver(object? sender, EventArgs e)
    {
        // 防抖:短时间内反复切焦点(在安装器/说明和本应用之间来回)不重复扫注册表/DriverStore。
        var now = Environment.TickCount64;
        if (now - _lastDriverRefreshTick < 800) return;
        _lastDriverRefreshTick = now;
        RefreshDriver();
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

        // ★ 安装位置【常态显示】(用户裁定 2026-07-31):做成固定槽位 —— 装好后不再突然多出一行把排版挤开。
        //   未安装时显示占位、复制按钮禁用。只读:位置由 VB-CABLE 自己的安装器固定,客户端改不了,
        //   给个能编辑的框会让人以为改了就能挪动它,那是骗人。
        //   (原来还有一行"驱动文件:<.sys 路径>"—— 已撤:那是深层技术细节,且它也会装好后才冒出来挤排版;
        //    安装位置更有用、且来自注册表更快。)
        _driverBody.Children.Add(CopyablePath("安装位置",
            st.Installed ? st.InstallLocation : null,
            st.Installed ? "(读不到安装位置)" : "未安装"));

        // ★ 「检查更新」不再单列,【并入「重装 / 更新」】(用户裁定 2026-07-31):
        //   点更新时先查官方最新版本、把查到的版本显示出来,再下载安装 —— 检查是更新的第一步,
        //   不是并排的第二个按钮。(旧的独立"检查"还拿占位版本"官方最新"去比对,永远显示"可更新",是误导。)
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(Ui.Secondary("重新检测", (_, _) => { RefreshDriver(); }));

        // ★ 安装/更新按钮【常显】(用户裁定 2026-07-31):删掉本地安装包后按钮不该蒸发。
        //   有本地包 -> 用本地包重装 / 更新;没有 -> 得下载,所以标「重新下载」——
        //   InstallDriver 内部本就"有本地包用本地、没有就下载",这里只是把这层意思写到按钮上。
        var hasPkg = AudioDriver.FindOfflinePackage() is not null;
        var installLabel = !st.Installed ? "一键安装"
                         : hasPkg        ? "重新安装 / 更新"
                                         : "重新下载";
        var install = Ui.Primary(installLabel, (_, _) => InstallDriver());
        install.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(install);
        if (st.Installed)
        {
            // ★ 卸载走【官方卸载程序】,不自己删 .sys ——
            //   手动拆内核驱动的残留会让下次安装也装不上,严重时整机没声音。
            var uninstall = Ui.Danger("一键卸载", (_, _) =>
            {
                // ★ 先查注册表、再问(2026-07-31 审计):把实际会运行的那一条给用户核对过再启动 ——
                //   而不是先弹一个写死"卸载 VB-CABLE"的框、点了之后才去查会启动哪个 exe。
                var hits = AudioDriver.FindUninstallers();
                if (hits.Count == 0)
                {
                    _driverStatus.Text = "找不到官方卸载入口 —— 请在「设置 › 应用」里卸载。"
                        + "我们不会自己去删驱动文件:手动拆内核驱动的残留会让下次装不上。";
                    return;
                }
                if (hits.Count > 1)
                {
                    // fail-closed(同 D45 路子):拿不准是哪一个就不替用户下手
                    _driverStatus.Text = "找到多个 VB-Audio CABLE 卸载入口:"
                        + string.Join(" / ", hits.Select(h => h.DisplayName))
                        + " —— 为免卸错,请到「设置 › 应用」里自己选。";
                    return;
                }
                var hit = hits[0];
                if (!ConfirmDialog.Show("卸载虚拟声卡",
                        $"将卸载:{hit.DisplayName}\n将运行:{hit.Command}\n\n"
                        + "卸载后同传的译文语音无法送进会议软件,但文字翻译与对方字幕不受影响。"
                        + "走官方卸载程序,会弹一次系统管理员提示。",
                        confirmText: "卸载", danger: true)) return;
                AudioDriver.RunUninstaller(hit, out var msg);
                _driverStatus.Text = msg;
            });
            uninstall.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(uninstall);
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


    /// <summary>
    /// 一行【只读、可复制】的路径:标签 + 只读输入框(可选中)+ 复制按钮。
    /// path 为空时显示 <paramref name="placeholder"/> 占位、复制按钮禁用 —— 用于做固定槽位,排版不因有无而抖。
    /// </summary>
    FrameworkElement CopyablePath(string label, string? path, string placeholder)
    {
        var has = !string.IsNullOrWhiteSpace(path);

        var lab = Ui.Caption(label + ":");
        lab.VerticalAlignment = VerticalAlignment.Center;
        lab.Margin = new Thickness(0, 0, 8, 0);

        // 只读输入框:有路径时可选中复制;IsReadOnly 明示"这里改了也没用"(安装位置由 VB-CABLE 决定)。
        var box = new TextBox
        {
            Text = has ? path : placeholder,
            IsReadOnly = true,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 4, 8, 4),
            MinWidth = 220,
        };
        if (!has) box.SetResourceReference(TextBox.ForegroundProperty, "FgMuted");

        var copy = Ui.Secondary("复制", (_, _) =>
        {
            try { System.Windows.Clipboard.SetText(path); _driverStatus.Text = "已复制路径到剪贴板。"; }
            catch { _driverStatus.Text = "复制失败(剪贴板被占用),请手动选中复制。"; }
        });
        copy.Margin = new Thickness(8, 0, 0, 0);
        copy.IsEnabled = has;   // 没路径可复时禁用,不给个点了没反应的按钮

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(lab);
        row.Children.Add(box);
        row.Children.Add(copy);
        return row;
    }

    void InstallDriver()
    {
        var local = AudioDriver.FindOfflinePackage();
        if (local is not null)
        {
            // ★ 信任模型 = Authenticode 签名(用户裁定 2026-07-31):真正的把关在 RunInstaller ——
            //   提权运行前验证安装程序由 VB-Audio 官方签发。哈希退为【可选】:清单里填了才多比对一次。
            //   装驱动不可逆且提权,验不过一律不装(这条纪律没变,只是把关手段换成了更强的签名验证)。
            var pkg = AudioDriverManifest.Current;
            if (pkg is not null && !string.IsNullOrWhiteSpace(pkg.Sha256) && !AudioDriver.Verify(local, pkg.Sha256))
            {
                _driverStatus.Text = "本地安装包与清单里的哈希对不上 —— 已拒绝运行。" +
                                     "请删掉它重新下载,或确认你放的是官方原包。";
                return;
            }
            AudioDriver.RunInstaller(local, out var msg);   // ← Authenticode 签名在这里把关
            _driverStatus.Text = msg;
            return;
        }
        DownloadThenInstall();
    }

    async void DownloadThenInstall()
    {
        var pkg = AudioDriverManifest.Current;
        if (pkg is null) { _driverStatus.Text = "没有可用的版本清单,请自备安装包(见下方离线安装说明)。"; return; }

        // ★ 第一步 = 检查(已并入更新,不再是单独按钮):查官方当前最新版本,并把查到的显示出来。
        _driverStatus.Text = "正在检查 VB-Audio 官方最新版本…";
        var (url, label) = await AudioDriver.ResolveLatest(pkg.Url, pkg.Version);

        // 第二步 = 下载查到的那一版
        _driverStatus.Text = $"官方最新:{label} —— 正在下载…";
        var progress = new Progress<double>(p => _driverStatus.Text = $"正在下载 {label} … {p:P0}");
        var (ok, path, msg) = await AudioDriver.DownloadAsync(pkg, progress, url);
        _driverStatus.Text = msg;
        if (!ok) return;

        // 第三步 = 验签并安装(Authenticode 在 RunInstaller 里把关)
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
                TheApp.Translation.NotifyPoolChanged();  // ★ 设置页是覆盖式的,底下那个翻译界面不重建 —— 得告诉它
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
                TheApp.Translation.NotifyPoolChanged();  // ★ 同上:不广播的话,回到翻译空间会发现刚加的语言不在池里
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
    // ---------------------------------------------------------------- 与 Apple 同步(日历,只读拉取)
    readonly StackPanel _appleBody = new();
    readonly TextBlock _appleStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    // ★ 不再只活在内存里:清单【落盘保存】(见 AppSettings.AppleCalendarList)——
    //   此前切走再回来/重启就没了,用户得再点一次"刷新日历列表"才能勾选。
    // 存盘格式：URL|显示名[|#RRGGBB] —— 颜色是后加的第三段，
    // 旧存档只有两段也能读(颜色为 null -> 按名字算一个稳定色)。
    static List<AppleCalendar> Decode(List<string> raw)
        => raw.Select(x => x.Split('|', 3))
              .Where(a => a.Length >= 2 && a[0].Length > 0)
              .Select(a => new AppleCalendar(a[0], a[1], a.Length >= 3 && a[2].Length > 0 ? a[2] : null)).ToList();
    static List<string> Encode(List<AppleCalendar> cals)
        => cals.Select(c => c.Url + "|" + c.DisplayName + (c.ColorHex is null ? "" : "|" + c.ColorHex)).ToList();

    /// <summary>
    /// 把日历清单推给【日程分类表】—— 新建日程的归类与颜色都读它(用户裁定 2026-07-31)。
    /// ★ 清单为空(没连/断开)时传空，CalendarGroups 会如实退回本地占位分类，
    ///   而不是拿上一次的缓存假装还连着。
    /// </summary>
    static void PushGroups(List<AppleCalendar> cals)
        // ★ URL 必须带上(审计 2026-08-02):重名日历靠它算稳定短码去重,
        //   丢了的话两个同名日历会合并成一个,颜色和下拉都混在一起。
        => Services.CalendarGroups.SetFromApple(cals.Select(c => (c.DisplayName, c.ColorHex, (string?)c.Url)));

    /// <summary>
    /// 范围裁定(用户 2026-07-31):【只读拉取 + 只做日历】。
    /// 拉取不会动你真实的 iCloud;写回是不可逆的,等这条链路验证过再开。
    /// </summary>
    public Border AppleSyncCard()
    {
        RefreshApple();
        var card = Ui.Card(Ui.Stack(
            Ui.Subtitle("与 Apple 日历同步"),
            Ui.Caption("用 CalDAV 连你自己的 iCloud 账号。★ 这一版【只往本机拉】—— 不会向 Apple 写入任何东西,"
                       + "也不会修改/删除你 iCloud 里的日程。"),
            Ui.Caption("拉回来的按【增量合并】进本机:已有的不重复加、同一条不覆盖、空日程不并入(见决议 D50 补充)。"),
            Ui.Caption("★ 需要【专用密码】(app-specific password),不是你的 Apple ID 密码 —— "
                       + "开了两步验证后 Apple 只认它。到 appleid.apple.com 生成,可随时单独吊销。"),
            Ui.Caption("密码经 Windows DPAPI 加密后才落盘(只有当前 Windows 用户能解开),明文不入盘、不入日志。"),
            _appleBody,
            _appleStatus));
        _appleSyncCard = card;
        return card;
    }

    void RefreshApple()
    {
        _appleBody.Children.Clear();
        var s = TheApp.Settings;
        var acct = AppleCredentials.Load();

        // —— 未连接:填账号
        if (acct is null || !acct.HasPassword)
        {
            // ★ 未连接时先给【怎么弄】—— 专用密码不是人人都知道在哪生成,
            //   把步骤和入口摆在这里,比让用户自己去搜要好。
            _appleBody.Children.Add(new Border { Height = 8 });
            _appleBody.Children.Add(Ui.Body("怎么连:"));
            _appleBody.Children.Add(Ui.Caption("① 你的 Apple 账号要先开启【双重认证】—— 这是生成专用密码的前提。"));
            _appleBody.Children.Add(Ui.Caption("② 打开下面的链接 → 登录与安全 → App 专用密码 → 生成一个(可以叫 LocalAI)。"));
            _appleBody.Children.Add(Ui.Caption("③ 把生成的 16 位密码【原样】粘到下面 —— 连字符不要去掉。"));

            var open = Ui.Secondary("打开 Apple 账户页面生成专用密码", (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "https://account.apple.com/account/manage") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    _appleStatus.Text = "打不开浏览器:" + ex.Message + " —— 请手动访问 account.apple.com。";
                }
            });
            open.Margin = new Thickness(0, 6, 0, 2);
            open.HorizontalAlignment = HorizontalAlignment.Left;
            _appleBody.Children.Add(open);
            _appleBody.Children.Add(Ui.Caption("链接指向 account.apple.com(Apple 官方)。用你系统默认的浏览器打开,我们不经手你的登录。"));

            // ★ 有效期:这事必须提前说清,否则哪天突然连不上会以为是我们坏了
            _appleBody.Children.Add(new Border { Height = 8 });
            _appleBody.Children.Add(Ui.Caption("★ 专用密码【不会】按时间过期,但你【改了或重置了 Apple ID 主密码】时,"
                                               + "Apple 会把所有专用密码一次性全部撤销 —— 那时需要回到这里重新生成并填写。"
                                               + "(同时最多 25 个;也可以随时在同一页面单独撤销它,撤销只影响本客户端,不影响你的账号。)"));

            _appleBody.Children.Add(new Border { Height = 8 });
            var id = new TextBox { Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(9, 6, 9, 6) };
            var pw = new PasswordBox { Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(9, 6, 9, 6) };
            _appleBody.Children.Add(Ui.Caption("Apple ID(邮箱全称)"));
            _appleBody.Children.Add(id);
            _appleBody.Children.Add(Ui.Caption("专用密码(形如 xxxx-xxxx-xxxx-xxxx,原样粘贴)"));
            _appleBody.Children.Add(pw);

            var connect = Ui.Primary("连接并检测日历", async (_, _) =>
            {
                var aid = id.Text.Trim();
                var p = pw.Password;
                if (aid.Length == 0 || p.Length == 0) { _appleStatus.Text = "请先填写 Apple ID 与专用密码。"; return; }
                _appleStatus.Text = "正在连接 iCloud…";
                var (ok, msg, cals, rems) = await AppleCalDav.DiscoverAsync(aid, p);
                _appleStatus.Text = msg;
                if (!ok) return;
                // ★ 先连通了再存 —— 连不通的凭据存下来,只会让下次启动又试一遍失败
                AppleCredentials.Save(aid, p);
                AppleAutoSync.ResetTrip();   // 新密码填过了 -> 允许自动拉取再跑
                TheApp.Settings.AppleCalendarList = Encode(cals);
                PushGroups(cals);

                TheApp.Settings.Save();
                RefreshApple();
            });
            connect.Margin = new Thickness(0, 6, 0, 0);
            connect.HorizontalAlignment = HorizontalAlignment.Left;
            _appleBody.Children.Add(connect);
            return;
        }

        // —— 已连接
        _appleBody.Children.Add(Ui.Body("已连接:" + acct.AppleId));
        _appleBody.Children.Add(Ui.Caption(s.AppleLastSync is { } t
            ? $"上次同步:{t:yyyy-MM-dd HH:mm}"
            : "还没同步过。"));

        // ★ 清单来自【落盘保存】的那份 -> 连上之后一直都在,不必每次先点刷新。
        //   ★ 构造界面时仍然【不静默联网】—— 要拿最新清单得按「刷新清单」,用户没按就发请求是不该有的行为。
        var calList = Decode(s.AppleCalendarList);

        if (calList.Count == 0)
        {
            _appleBody.Children.Add(Ui.Caption("还没取过清单 —— 点下面的「刷新清单」把 iCloud 里的日历取过来。"));
        }
        else
        {
            {
                _appleBody.Children.Add(Ui.Caption("勾选要拉取的【日历】:"));
                foreach (var cal in calList)
                {
                    var url = cal.Url;
                    var cb = new CheckBox
                    {
                        Content = cal.DisplayName,
                        IsChecked = s.AppleCalendarUrls.Contains(url),
                        Margin = new Thickness(0, 2, 0, 2),
                    };
                    cb.Checked += (_, _) => { if (!s.AppleCalendarUrls.Contains(url)) { s.AppleCalendarUrls.Add(url); s.Save(); } };
                    cb.Unchecked += (_, _) => { s.AppleCalendarUrls.Remove(url); s.Save(); };
                    _appleBody.Children.Add(cb);
                }
            }
            // ★★ 这张卡【只管日历】(2026-08-02 用户裁定,D57)。
            //   提醒事项那一组勾选框已整体移除,连清单也不再取回来显示 ——
            //   待办改成【纯本机】数据,不接任何外部源;在这儿列出提醒事项清单
            //   就是在暗示"能同步",而那是不会兑现的。
            //   ★ 为什么不能同步的解释,放在【待办自己那儿】说(见 TodoCenter 头部与待办板块),
            //     不放这里 —— 这是待办的属性,不是 Apple 连接的属性。
        }

        // ---- 自动拉取(用户要求 2026-07-31)----
        // ★ 熔断优先显示:被自动关掉时必须说清为什么,否则用户只会觉得"开关自己关了"。
        if (AppleAutoSync.TrippedReason is { } trip)
        {
            var warn = Ui.Body(trip, muted: false);
            warn.SetResourceReference(TextBlock.ForegroundProperty, "RiskWarning");
            warn.Margin = new Thickness(0, 10, 0, 0);
            _appleBody.Children.Add(warn);
        }

        var auto = new CheckBox
        {
            Content = "自动拉取",
            IsChecked = s.AppleAutoPull,
            Margin = new Thickness(0, 10, 0, 0),
            IsEnabled = AppleAutoSync.TrippedReason is null,
        };
        auto.Checked += (_, _) => { s.AppleAutoPull = true; s.Save(); AppleAutoSync.Apply(); };
        auto.Unchecked += (_, _) => { s.AppleAutoPull = false; s.Save(); AppleAutoSync.Apply(); };
        _appleBody.Children.Add(auto);

        var every = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var m in new[] { 15, 30, 60, 180, 360, 720 })
            every.Items.Add(new ComboBoxItem { Content = m < 60 ? $"每 {m} 分钟" : $"每 {m / 60} 小时", Tag = m });
        var mi = Array.IndexOf(new[] { 15, 30, 60, 180, 360, 720 }, Math.Max(15, s.AppleAutoPullMinutes));
        every.SelectedIndex = mi < 0 ? 2 : mi;
        every.SelectionChanged += (_, _) =>
        {
            if (every.SelectedItem is ComboBoxItem { Tag: int m }) { s.AppleAutoPullMinutes = m; s.Save(); AppleAutoSync.Apply(); }
        };
        _appleBody.Children.Add(every);
        _appleBody.Children.Add(Ui.Caption("★ 一旦某次自动拉取被 Apple 拒绝认证(比如你改了 Apple ID 主密码,"
                                           + "所有专用密码会被一次性撤销),自动拉取会【立刻自动关闭】并告诉你原因 —— "
                                           + "绝不反复重试:反复失败会导致 Apple 锁定你的账号。"));
        if (AppleAutoSync.LastMessage is { } lm) _appleBody.Children.Add(Ui.Caption(lm));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(Ui.Secondary("刷新清单", async (_, _) =>
        {
            var p = AppleCredentials.Reveal();
            if (p is null) { _appleStatus.Text = "读不出已保存的密码(多半是换了 Windows 用户)。请断开后重新连接。"; return; }
            _appleStatus.Text = "正在取日历清单…";
            var (ok, msg, cals, rems) = await AppleCalDav.DiscoverAsync(acct.AppleId, p);
            _appleStatus.Text = msg;
            if (!ok) return;
            TheApp.Settings.AppleCalendarList = Encode(cals);
            PushGroups(cals);

            TheApp.Settings.Save();
            RefreshApple();
        }));

        var sync = Ui.Primary("立即同步", async (_, _) =>
        {
            _appleStatus.Text = "正在同步…";
            var r = await AppleCalendarSync.PullAsync(TheApp.Settings, MemberContext.Current, "家庭");
            _appleStatus.Text = r.Message;
            if (r.Ok) AppleAutoSync.NoteManualSuccess();   // 手动通了 -> 解除软暂停
            RefreshApple();
        });
        sync.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(sync);

        var off = Ui.Danger("断开连接", (_, _) =>
        {
            if (!ConfirmDialog.Show("断开 Apple 连接",
                    "将删除本机保存的 Apple ID 与专用密码。" + "\n" + "\n"
                    + "★ 已经同步到本机的日程【不会】被删掉;也不会动 Apple 那边的任何东西。",
                    confirmText: "断开", danger: true)) return;
            AppleCredentials.Clear();
            TheApp.Settings.AppleAutoPull = false;
            TheApp.Settings.AppleCalendarUrls.Clear();
            TheApp.Settings.AppleCalendarList.Clear();
            TheApp.Settings.AppleLastSync = null;   // 换账号重连后不该还显示前一个账号的时间
            PushGroups(new List<AppleCalendar>());   // 断开 -> 分类表退回本地占位

            TheApp.Settings.Save();
            AppleAutoSync.Stop();          // 停表 + 清熔断 —— 断开后不该还留着上次的报错
            _appleStatus.Text = "已断开。本机已有的日程原样保留。";
            RefreshApple();
        });
        off.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(off);
        _appleBody.Children.Add(row);
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
