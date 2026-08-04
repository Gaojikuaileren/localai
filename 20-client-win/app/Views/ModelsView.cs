// P3c -- 系统 › 模型。四块:统一模型存放路径 · 启用哪些模型 · 自动启用规则 · 模型选择策略(占位)。
//
// ★ 诚实:模型的实际装载由 GPU Broker(P4)按显存预算决定。这里存的是【偏好】,
//   接入后由 Broker 执行;现在【尚未真正加载任何模型】—— 页面顶部明说,绝不假装模型在跑。
//   显存数字不在此重复(唯一来源是主机的 vram-budget.toml)。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        // ② 启用的模型 —— P4-S9:改由【中枢下发】的组件目录驱动,并走真事务。
        //   ★ 原来这里遍历的是 ModelCatalog.All,那是客户端自造的一份占位清单
        //     (chat.8b / speech / image),跟网关别名与显存组件 id **一个都对不上** ——
        //     勾了不会发生任何事,而界面看着像配置好了。现在换成真的。
        var modelList = new ComponentPicker();

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
                Ui.Caption("清单与峰值都由中枢下发(唯一权威是主机的 config/vram-budget.toml)。"
                           + "点确定 = 向中枢提交一次驻留集合变更,中枢会在那一刻重新求值。"),
                new Border { Height = 6 },
                modelList
            )),

            Ui.Card(Ui.Stack(
                Ui.Subtitle(Strings.Get("model.auto_rules")),
                Ui.Caption(Strings.Get("model.auto_preset")), preset,
                idle,
                new Border { Height = 4 },
                Ui.Caption(Strings.Get("model.auto_hint"))
            )),

            StrategyPlaceholder()
        );
    }

    /// <summary>
    /// ④ 模型选择策略 —— 【占位符】(用户裁定 2026-07-31:先占位,想清楚再搭)。
    ///
    /// ★ 为什么是一片空的虚线框而不是几个先摆上的开关/下拉:
    ///   规则怎么表达还没定。摆一个能拨却不生效的开关,用户会以为自己已经配好了策略 ——
    ///   而实际上什么都没发生。这比空着糟得多:空着只是"还没做",假开关是"骗人"。
    ///   同样的理由,这里也【不写】未来功能清单当成承诺 —— 只说清这块要管的那件事,
    ///   以及在它做出来之前现在的真实行为是什么。
    ///
    /// 边界(写在这儿免得以后自己搞混):
    ///   「启用的模型」= 哪些模型【可以】用;「自动启用规则」= 连上中枢时【先装】谁;
    ///   这一块 = 一件事来了【交给谁】。前两块管准入与装载,这块管派活。
    /// </summary>
    static FrameworkElement StrategyPlaceholder()
    {
        // 虚线框 = "这里预留了位置,但里面是空的"。实线框会让人以为是个已完成的板块。
        var hole = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 18, 16, 18),
            Margin = new Thickness(0, 8, 0, 0),
            MinHeight = 96,
        };
        hole.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        hole.SetResourceReference(Border.BorderBrushProperty, "Border");

        // WPF 的虚线走 Pen 的 DashStyle,Border 给不了 —— 用 Rectangle 叠一层描边
        var dash = new System.Windows.Shapes.Rectangle
        {
            RadiusX = 8, RadiusY = 8,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false,
        };
        dash.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Border");

        var inner = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var todo = Ui.Body(Strings.Get("model.strategy_todo"), muted: true);
        todo.HorizontalAlignment = HorizontalAlignment.Center;
        var note = Ui.Caption(Strings.Get("model.strategy_note"));
        note.HorizontalAlignment = HorizontalAlignment.Center;
        note.TextAlignment = TextAlignment.Center;
        note.MaxWidth = 620;
        note.Margin = new Thickness(0, 6, 0, 0);
        inner.Children.Add(todo);
        inner.Children.Add(note);

        var grid = new Grid();
        grid.Children.Add(dash);
        grid.Children.Add(inner);
        hole.Child = grid;
        hole.BorderThickness = new Thickness(0);   // 描边交给上面那个虚线矩形

        return Ui.Card(Ui.Stack(
            Ui.Subtitle(Strings.Get("model.strategy")),
            Ui.Caption(Strings.Get("model.strategy_hint")),
            hole
        ));
    }

}
