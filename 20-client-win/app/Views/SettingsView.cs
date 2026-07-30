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
                new Border { Height = 8 },
                Ui.Caption("皮肤与语言是**每台设备**各自的选择,不会同步到其它电脑。")
            )),

            Ui.Card(Ui.Stack(
                Ui.Subtitle("启动与后台"),
                auto,
                blocked,
                Ui.Caption("以当前用户身份自启(不提权)。客户端必须保持普通用户运行,否则设备密钥打不开(见决议 D46)。"),
                tray,
                Ui.Caption("勾选后点窗口的 × 只是收起窗口,程序继续在托盘运行;要真正退出请用托盘图标右键 →「退出」。")
            )),

            LanguagePoolCard(),

            AppleSyncCard(),

            // 存储与清理 + 记忆库编辑(用户裁定:一键清爽 + 勾选执行内容;记忆可预览删减)
            new StorageView(),

            // 连接与设备:已配对的电脑、配对/解除 —— 从独立导航项并入设置(用户裁定)
            Ui.Subtitle(Strings.Get("devices.title")),
            new DevicesView(embedded: true)
        );
    }

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

        var rest = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var l in Languages.Catalog.Where(x => !s.TranslationPool.Contains(x.Code)))
            rest.Children.Add(LangPill(l, inPool: false, () =>
            {
                s.TranslationPool.Add(l.Code);
                s.Save();
                RefreshLangPool();
            }));
        if (rest.Children.Count == 0) rest.Children.Add(Ui.Caption("目录里的语言都已经在池中了。"));
        _langBody.Children.Add(Ui.Caption("可添加"));
        _langBody.Children.Add(rest);
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

    /// <summary>从翻译空间的齿轮跳进来:滚到语言池并闪一下。</summary>
    public void RevealLanguagePool()
    {
        if (_langCard is null) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _langCard.BringIntoView();
            var flash = new System.Windows.Media.Animation.DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(420))
            { AutoReverse = true, RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2) };
            _langCard.BeginAnimation(OpacityProperty, flash);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

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

    /// <summary>从主页齿轮跳进来时:把"与 Apple 同步"这块滚到可视区并高亮一下,让人知道跳到哪了。</summary>
    public void RevealAppleSync()
    {
        if (_appleSyncCard is null) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _appleSyncCard.BringIntoView();
            // 轻微闪一下描边:不改布局(避免跳动),只是提示"就是这一块"
            var flash = new System.Windows.Media.Animation.DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(420))
            { AutoReverse = true, RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2) };
            _appleSyncCard.BeginAnimation(OpacityProperty, flash);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
