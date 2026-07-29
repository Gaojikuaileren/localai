// P3c -- 扩展。目前先落地一件用户要的事:选择【左边栏显示哪些工作空间】。
//   勾选 = 显示;取消 = 从导航里隐藏(工作空间本身仍在,只是不在左栏出现)。
//   即时生效:改完就重建导航栏(不重建正在看的页面)。偏好存本机(每台设备各自)。

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
        var list = new StackPanel();
        foreach (var w in Workspaces.All)
            list.Children.Add(WorkspaceToggle(w));

        Content = Ui.Page(
            Ui.Title(Strings.Get("nav.extensions")),
            Ui.Card(Ui.Stack(
                Ui.Subtitle(Strings.Get("ext.workspaces_title")),
                Ui.Caption(Strings.Get("ext.workspaces_hint")),
                new Border { Height = 8 },
                list
            ))
        );
    }

    FrameworkElement WorkspaceToggle(Workspaces.Def w)
    {
        var icon = Icons.Make(w.Icon, 17, "FgSecondary");
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 10, 0);

        var label = new TextBlock { Text = Strings.Get(w.TitleKey), VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(icon);
        left.Children.Add(label);

        var check = new CheckBox
        {
            IsChecked = TheApp.Settings.IsWorkspaceVisible(w.Key),
            VerticalAlignment = VerticalAlignment.Center,
        };
        check.Checked += (_, _) => Apply(w.Key, true);
        check.Unchecked += (_, _) => Apply(w.Key, false);

        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 5, 0, 5) };
        DockPanel.SetDock(check, Dock.Right);
        row.Children.Add(check);
        row.Children.Add(left);
        return row;
    }

    static void Apply(string key, bool visible)
    {
        TheApp.Settings.SetWorkspaceVisible(key, visible);
        // 即时反映到左边栏(只重建导航,不重建当前正在看的"扩展"页,免得开关被重新创建)
        (Application.Current.MainWindow as MainWindow)?.RefreshNavRail();
    }
}
