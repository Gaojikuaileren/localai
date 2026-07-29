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

            // 连接与设备:已配对的电脑、配对/解除 —— 从独立导航项并入设置(用户裁定)
            Ui.Subtitle(Strings.Get("devices.title")),
            new DevicesView(embedded: true)
        );
    }
}
