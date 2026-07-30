// P3c -- 系统 › 模型。三块:统一模型存放路径 · 启用哪些模型 · 自动启用规则。
//
// ★ 诚实:模型的实际装载由 GPU Broker(P4)按显存预算决定。这里存的是【偏好】,
//   接入后由 Broker 执行;现在【尚未真正加载任何模型】—— 页面顶部明说,绝不假装模型在跑。
//   显存数字不在此重复(唯一来源是主机的 vram-budget.toml)。

using System.Windows;
using System.Windows.Controls;
using LocalAI.Client.I18n;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;

namespace LocalAI.Client.Views;

public sealed class ModelsView : UserControl
{
    static App TheApp => (App)Application.Current;

    public ModelsView()
    {
        var s = TheApp.Settings;

        // ① 统一模型存放路径
        var path = new TextBox
        {
            Text = s.ModelStorePath ?? "",
            Margin = new Thickness(0, 4, 0, 6),
            Padding = new Thickness(9, 6, 9, 6),
        };
        // ★ 不能只靠 LostFocus 提交:焦点纪律收窄后,这一页里可聚焦的控件【只剩这个框】——
        //   点复选框、点下拉、点按钮都不会夺走焦点(它们都不可聚焦了),Tab 也没有第二个框可去。
        //   而切页面时元素是被整体摘出可视树的,那种情况下 WPF 触不触发 LostFocus 并不可靠
        //   (就是 UpdateSourceTrigger=LostFocus 在切标签页时丢数据的老毛病)。
        //   所以:失焦时提交【照留】,再补一次卸载时提交,两条路任意一条到达都算数(Commit 幂等)。
        //   ★ 不用 TextChanged:那是每敲一个字符就往盘上写一次路径,没必要。
        void Commit()
        {
            var v = string.IsNullOrWhiteSpace(path.Text) ? null : path.Text.Trim();
            if (v != s.ModelStorePath) { s.ModelStorePath = v; s.Save(); }
        }
        path.LostFocus += (_, _) => Commit();
        Unloaded += (_, _) => Commit();

        // ② 启用的模型
        var modelList = new StackPanel();
        foreach (var m in ModelCatalog.All)
        {
            var key = m.Key;
            modelList.Children.Add(ModelToggle(m, s.IsModelEnabled(key),
                on => s.SetModelEnabled(key, on)));
        }

        // ③ 自动启用规则
        var preset = new ComboBox { Margin = new Thickness(0, 4, 0, 6), Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (_, label) in ModelCatalog.Presets) preset.Items.Add(label);
        var pIdx = Array.FindIndex(ModelCatalog.Presets, p => p.Key == s.AutoStartPreset);
        preset.SelectedIndex = pIdx < 0 ? 0 : pIdx;
        preset.SelectionChanged += (_, _) =>
        {
            if (preset.SelectedIndex >= 0) { s.AutoStartPreset = ModelCatalog.Presets[preset.SelectedIndex].Key; s.Save(); }
        };

        var idle = new CheckBox { Content = Strings.Get("model.idle_unload"), IsChecked = s.AutoUnloadIdle, Margin = new Thickness(0, 6, 0, 0) };
        idle.Checked += (_, _) => { s.AutoUnloadIdle = true; s.Save(); };
        idle.Unchecked += (_, _) => { s.AutoUnloadIdle = false; s.Save(); };

        Content = Ui.Page(
            Ui.Title(Strings.Get("nav.model")),

            // 顶部诚实横幅:现在还没接 Broker,这些只是偏好
            Ui.Card(Ui.Stack(Ui.Body(Strings.Get("model.not_connected"), muted: true))),

            Ui.Card(Ui.Stack(
                Ui.Subtitle(Strings.Get("model.store_path")),
                Ui.Caption(Strings.Get("model.store_path_hint")),
                path
            )),

            Ui.Card(Ui.Stack(
                Ui.Subtitle(Strings.Get("model.enabled")),
                Ui.Caption(Strings.Get("model.enabled_hint")),
                new Border { Height = 6 },
                modelList
            )),

            Ui.Card(Ui.Stack(
                Ui.Subtitle(Strings.Get("model.auto_rules")),
                Ui.Caption(Strings.Get("model.auto_preset")), preset,
                idle,
                new Border { Height = 4 },
                Ui.Caption(Strings.Get("model.auto_hint"))
            ))
        );
    }

    static FrameworkElement ModelToggle(ModelCatalog.Def m, bool on, Action<bool> onChanged)
    {
        var ic = Icons.Make(IconName.Model, 17, "FgSecondary");
        ic.VerticalAlignment = VerticalAlignment.Center;
        ic.Margin = new Thickness(0, 0, 10, 0);

        var name = new TextBlock { Text = m.Name, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "FgPrimary");
        var role = new TextBlock { Text = "  ·  " + m.Role, VerticalAlignment = VerticalAlignment.Center };
        role.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        role.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(ic);
        left.Children.Add(name);
        left.Children.Add(role);

        var check = new CheckBox { IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
        check.Checked += (_, _) => onChanged(true);
        check.Unchecked += (_, _) => onChanged(false);

        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 5, 0, 5) };
        DockPanel.SetDock(check, Dock.Right);
        row.Children.Add(check);
        row.Children.Add(left);
        return row;
    }
}
