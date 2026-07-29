// P3c -- 扩展。分【两类】(用户裁定):
//   ① 工作空间扩展:决定左边栏出现哪些工作空间;接入模型后,也在这里为每个工作空间指定用哪个 AI 模型。
//   ② 主页板块扩展:决定主页显示哪些板块(内容与种类)。
//
// 即时生效:工作空间勾选改完只重建导航栏(不重建正在看的页面);
//   主页板块勾选存本机,下次进主页时按新设置构建(主页本就每次导航重建)。
// 偏好都存本机 AppSettings(每台设备各自),不同步到中枢。

using System.Windows;
using System.Windows.Controls;
using LocalAI.Client.I18n;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class ExtensionsView : UserControl
{
    static App TheApp => (App)Application.Current;

    public ExtensionsView()
    {
        // ① 工作空间扩展
        var wsList = new StackPanel();
        foreach (var w in Workspaces.All)
            wsList.Children.Add(ToggleRow(w.Icon, Strings.Get(w.TitleKey),
                TheApp.Settings.IsWorkspaceVisible(w.Key),
                on => ApplyWorkspace(w.Key, on)));

        // ② 主页板块扩展
        var panelList = new StackPanel();
        foreach (var p in HomePanels.All)
            panelList.Children.Add(ToggleRow(p.Icon, p.Title,
                TheApp.Settings.IsPanelVisible(p.Key),
                on => ApplyPanel(p.Key, on)));

        Content = Ui.Page(
            Ui.Title(Strings.Get("nav.extensions")),

            Ui.Card(Ui.Stack(
                Ui.Subtitle(Strings.Get("ext.ws_title")),
                Ui.Caption(Strings.Get("ext.ws_hint")),
                new Border { Height = 8 },
                wsList,
                new Border { Height = 6 },
                Ui.Caption(Strings.Get("ext.ws_model_note"))
            )),

            Ui.Card(Ui.Stack(
                Ui.Subtitle(Strings.Get("ext.panels_title")),
                Ui.Caption(Strings.Get("ext.panels_hint")),
                new Border { Height = 8 },
                panelList
            ))
        );
    }

    static FrameworkElement ToggleRow(IconName icon, string title, bool on, Action<bool> onChanged)
    {
        var ic = Icons.Make(icon, 17, "FgSecondary");
        ic.VerticalAlignment = VerticalAlignment.Center;
        ic.Margin = new Thickness(0, 0, 10, 0);

        var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(ic);
        left.Children.Add(label);

        var check = new CheckBox { IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        check.Checked += (_, _) => onChanged(true);
        check.Unchecked += (_, _) => onChanged(false);

        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 5, 0, 5) };
        DockPanel.SetDock(check, Dock.Right);
        row.Children.Add(check);
        row.Children.Add(left);
        return row;
    }

    static void ApplyWorkspace(string key, bool visible)
    {
        TheApp.Settings.SetWorkspaceVisible(key, visible);
        (Application.Current.MainWindow as MainWindow)?.RefreshNavRail();   // 即时反映到左边栏
    }

    static void ApplyPanel(string key, bool visible)
        => TheApp.Settings.SetPanelVisible(key, visible);   // 主页下次构建时生效(导航到主页即重建)
}
