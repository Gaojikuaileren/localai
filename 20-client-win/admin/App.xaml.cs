// V14 -- 管理端的生命周期。裁定第 1~8 条(admin-app-lifecycle-2026-08-07.md §1)全部落在这里。
//
//  2 双击图标      -> 正常显示界面,★ **不启动客户端**
//  4 客户端关闭    -> 管理端**仍挂后台**(本进程与客户端进程无父子/无监视关系,天然成立)
//  6 点 ×          -> 缩到托盘;**真正关闭只能托盘右键 → 关闭**
//  7 真正关闭      -> 同时请客户端**优雅退出**(八步),★ 不许强杀
//
// ★ 第 1 条(主机客户端启动 ⇒ 起管理端并隐藏托盘)在**客户端**那侧(App.xaml.cs);
// ★ 第 3 条(副机客户端不起管理端)是**结构性**的:副机上根本没有这个 exe(裁定②);
// ★ 第 5 条(主机设置里有「打开管理端面板」按钮、副机没有)在客户端的 SettingsView;
// ★ 第 8 条(管理端未起时副机仍可启动、界面写清哪些还能用)也在客户端那侧。

using System.Windows;
using LocalAI.Admin.Services;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;
using WinForms = System.Windows.Forms;

namespace LocalAI.Admin;

public partial class App : Application
{
    readonly InstanceLock _instance;
    readonly bool _startHidden;
    AdminWindow? _main;
    WinForms.NotifyIcon? _tray;
    FileSystemWatcher? _settingsWatcher;
    Skin _skin = Skin.Breeze;

    public App(InstanceLock instance, bool startHidden)
    {
        _instance = instance;
        _startHidden = startHidden;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;   // 关窗口不等于退应用(第 6 条)
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 皮肤:读**客户端的** settings.json(裁定③ —— 皮肤是本机两个进程之间的事)
        _skin = ReadSkin();
        ThemeManager.Initialize(_skin);

        BuildTray();
        WatchSettings();

        // 第 2 条:双击图标 -> 正常显示界面。★ 这里**不碰客户端** —— 一个字都不起。
        if (!_startHidden) ShowPanel();

        // 再点一次图标 = 叫醒已有实例的窗口(与客户端同一套机制)
        _instance.ListenForWake(() => Dispatcher.Invoke(ShowPanel));
    }

    // ---------------------------------------------------------------- 皮肤(裁定③:监听文件,不走契约)
    /// <summary>
    /// 读客户端的皮肤设置。★ 读**同一个文件**,所以两个进程天生一致 ——
    /// 这正是裁定③不把皮肤做成契约的理由:做成端点只会让那张只许变短的欠债表(D95)平白多一条,
    /// 而它守不住任何东西。
    /// </summary>
    static Skin ReadSkin()
    {
        try { return AppSettings.Load().Skin; } catch { return Skin.Breeze; }
    }

    void WatchSettings()
    {
        try
        {
            var dir = AppPaths.StateDir;
            Directory.CreateDirectory(dir);
            _settingsWatcher = new FileSystemWatcher(dir, Path.GetFileName(AppPaths.SettingsPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            // ★ 客户端存设置是"写 .tmp 再原子替换"(AppSettings.Save),所以既会有 Changed
            //   也会有 Renamed —— 只听 Changed 会漏掉真正落地的那一次。
            _settingsWatcher.Changed += (_, _) => OnSettingsTouched();
            _settingsWatcher.Renamed += (_, _) => OnSettingsTouched();
        }
        catch { /* 看不了就算了:皮肤会停在启动时那一套,不影响其它功能 */ }
    }

    void OnSettingsTouched()
    {
        // ★ 原子替换那一下会连着来几个事件,而且写方可能还没松手 —— 稍等一下再读,
        //   读失败也不当回事(下一个事件还会再来一次)。
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(120);
            var next = ReadSkin();
            if (next == _skin) return;      // 只在**真的变了**时换,避免无谓重建图标
            _skin = next;
            ThemeManager.Apply(next);
            _main?.Refresh();
        });
    }

    // ---------------------------------------------------------------- 托盘
    void BuildTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开管理端面板", null, (_, _) => Dispatcher.Invoke(ShowPanel));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        // ★★ 第 6 条:**真正关闭只能走这里**。窗口的 × 只缩托盘。
        menu.Items.Add("关闭", null, (_, _) => Dispatcher.Invoke(async () => await RealCloseAsync()));

        _tray = new WinForms.NotifyIcon
        {
            // ★ 待定(lifecycle 包 §6.1):客户端与管理端会在托盘里各有一个图标,今天没有区分方案。
            //   16×16 上加角标在高 DPI 下经常糊成一团 —— 这里先用**不同的提示文字**区分,
            //   图标本身的区分留给那条待定,不假装已经解决了。
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "本地 AI · 主机管理端",
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowPanel);
    }

    void ShowPanel()
    {
        if (_main is null)
        {
            _main = new AdminWindow();
            // 第 6 条:点 × = 缩到托盘,不退出
            _main.Closing += (_, ev) => { ev.Cancel = true; _main?.Hide(); };
        }
        _main.Refresh();
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true; _main.Topmost = false;
    }

    // ---------------------------------------------------------------- 真正关闭(第 7 条 + 裁定⑤)
    /// <summary>
    /// 托盘右键 → 关闭。两件事,顺序不能颠倒:
    /// ① 先问「现在关栈会不会切断别人」—— 把判据摆给人看,由**人**决定(D102 裁定④);
    /// ② 决定要关之后,请主机客户端**优雅退出**(八步),★ 等不到就如实说,**绝不强杀**。
    /// </summary>
    async Task RealCloseAsync()
    {
        var verdict = await StackStop.QueryAsync();
        var answer = MessageBox.Show(StackStop.ConfirmText(verdict),
                                     "关闭管理端", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        if (ClientLink.IsClientRunning())
        {
            var (stopped, why) = await ClientLink.RequestClientQuitAsync(TimeSpan.FromSeconds(20));
            if (!stopped)
            {
                // ★ 如实说,并且**不继续关自己** —— 管理端先走会让用户失去唯一的入口,
                //   而客户端还在那儿占着租约。
                MessageBox.Show(why + "\n\n管理端没有关闭。", "关闭管理端",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        _settingsWatcher?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _instance.Dispose();
        Shutdown();
    }
}
