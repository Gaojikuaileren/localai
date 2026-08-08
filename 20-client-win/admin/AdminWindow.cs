// V14 -- 管理端主窗口。★ 整窗用的是**客户端那 704 行界面原语**(csproj link,不是复制):
//   Ui.Page / Ui.Panel / Ui.Card / Ui.Body / Ui.Primary ... 一个都没有重写。
//
// ★★ 颜色**一律走令牌**(SetResourceReference / Ui.Dyn),本文件里不许出现任何颜色字面量。
//   两个 exe 各自渲染,漂了不会有任何东西红 —— 所以这条靠断言钉(见客户端 Selftest 的 V14 段)。

using System.Windows;
using System.Windows.Controls;
using LocalAI.Admin.Services;
using LocalAI.Client.Theme;
using LocalAI.Client.Views;

namespace LocalAI.Admin;

public sealed class AdminWindow : Window
{
    readonly StackPanel _body = new();

    public AdminWindow()
    {
        Title = "本地 AI · 主机管理端";
        Width = 980; Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this.Dyn(BackgroundProperty, "BgWindow");
        this.Dyn(FontFamilyProperty, "FontUI");
        this.Dyn(FontSizeProperty, "FontBody");
        Content = Ui.Page(_body);
        Refresh();
    }

    /// <summary>把当前状态重画一遍。★ 每一格都只说**读到的**,读不到就说读不到。</summary>
    public void Refresh()
    {
        _body.Children.Clear();
        _body.Children.Add(Ui.Title("主机管理端"));
        _body.Children.Add(Ui.Body(
            "这台是主机。中枢身份、组件与模型、存储、起关栈都在这里管。", muted: true));

        // ── 客户端那一格 ────────────────────────────────────────────
        var running = ClientLink.IsClientRunning();
        var clientBody = Ui.Stack(
            Ui.Body(running ? "主机客户端:正在运行。" : "主机客户端:没有在运行。"),
            Ui.Caption(running
                ? "关闭管理端(托盘右键 → 关闭)时会请它优雅退出 —— 走它自己的八步善后,不强杀。"
                : "可以从这里把它打开;它也会在开机时自启到托盘。"));
        _body.Children.Add(Ui.Panel("主机客户端", clientBody, IconName.Devices));

        var act = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 12) };
        if (!running)
            act.Children.Add(Ui.Primary("打开主机客户端", (_, _) =>
            {
                var (ok, why) = ClientLink.StartClient(tray: false);
                Say(ok ? why : "没能打开:" + why);
                Refresh();
            }));
        act.Children.Add(Ui.Secondary("刷新", (_, _) => Refresh()));
        _body.Children.Add(act);

        // ── 关栈那一格(裁定⑤)──────────────────────────────────────
        _body.Children.Add(Ui.Panel("关掉整套 AI 栈",
            Ui.Stack(
                Ui.Body("关栈是人的动作,不是推断(D102 裁定④)。"),
                Ui.Caption("入口在托盘右键 → 关闭:那一下会先问一句「现在关会不会切断别人」,"
                         + "把判据摆给你看,再由你决定。")),
            IconName.Settings));
    }

    void Say(string text) => MessageBox.Show(this, text, "主机管理端",
                                             MessageBoxButton.OK, MessageBoxImage.Information);
}
