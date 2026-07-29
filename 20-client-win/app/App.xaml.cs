// P3c -- 应用生命周期。用户要求的两条行为在这里落地:
//   「关窗口保持后台任务栏图标」 -> ShutdownMode=OnExplicitShutdown + Closing 拦截 + 托盘图标
//   「退出时关闭窗口、释放显存、做好关闭善后」 -> ShutdownCoordinator,四个退出入口汇一处、只跑一次
//
// 注意 ShutdownMode:默认 OnLastWindowClose 会在窗口一关就退进程,那样"留在托盘"根本无从谈起。

using System.Windows;
using LocalAI.Client.I18n;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;
using WinForms = System.Windows.Forms;

namespace LocalAI.Client;

public partial class App : Application
{
    readonly SingleInstance _instance;
    readonly bool _startHidden;

    WinForms.NotifyIcon? _tray;
    MainWindow? _main;

    public AppSettings Settings { get; private set; } = new();
    public HubClient Hub { get; private set; } = new();
    /// <summary>全局任务中心:底部横条与全局抽屉共用同一份状态(用户裁定抽屉是全局的)。</summary>
    public TaskCenter Tasks { get; } = new();
    /// <summary>显存实时监视(左导航的显存条)。2 秒轮询,窗口不可见时自动停表。</summary>
    public VramMonitor Vram { get; } = new();
    /// <summary>「正在进行的项目」——主页田字格的数据源;点方块深链到对应工作空间。</summary>
    public ProjectCenter Projects { get; } = new();
    // 命名成 Lifecycle 而不是 Shutdown:后者会遮蔽 Application.Shutdown(),是个陷阱
    // (将来有人在 App 内写 Shutdown() 想退应用,拿到的却是这个协调器)。
    public ShutdownCoordinator Lifecycle { get; } = new();

    public App(SingleInstance instance, bool startHidden)
    {
        _instance = instance;
        _startHidden = startHidden;
        // 窗口全关也不退出 —— 退出只能由用户显式触发(托盘「退出」)或系统关机。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureStateDir();
        Settings = AppSettings.Load();
        Strings.Language = Settings.Language;
        ThemeManager.Initialize(Settings.Skin);

        // 自启项若指向旧路径(exe 被移动/更新过)则重写,否则开机会启动到一个不存在的文件。
        if (Settings.Autostart && !Autostart.IsCurrent()) Autostart.Enable();

        RegisterCleanupSteps();

        // Windows 关机/注销:系统只给有限时间,善后必须有预算上限(见 ShutdownCoordinator)。
        // ★ 用 Task.Run 脱离 UI 同步上下文再阻塞等待:善后是 async 且含网络调用,直接在
        //   UI 线程上 GetResult 会与内部 await 续体死锁。RunCleanup 统一处理。
        SessionEnding += (_, args) => RunCleanup("session-ending:" + args.ReasonSessionEnding);
        // 兜底:任何路径导致进程退出时,若还没善后过就补一次(强杀除外,那种情况谁也救不了)。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RunCleanup("process-exit");

        SetupTray();
        Strings.LanguageChanged += () => Dispatcher.Invoke(RebuildTrayMenu);

        _main = new MainWindow();
        _main.Closing += OnMainWindowClosing;
        if (!_startHidden) _main.Show();

        _instance.ListenForWake(() => Dispatcher.Invoke(ShowMainWindow));

        // 启动即用已保存的档案连一次:配对过就自动连上,不再打扰用户(用户要求 3)。
        _ = Task.Run(async () => { await Hub.ProbeAsync(); Dispatcher.Invoke(UpdateTrayTooltip); });

        SeedDemoTasks();
    }

    // 外壳评审期的示例任务。真实任务源要等各工作空间接入(P4/P6/P9),在那之前底部横条
    // 永远不会出现、也就没法评审。★ 标题明确标注「示例」——不伪装成真实任务。
    // 真实任务接入后删掉这段(或改成 Settings 里的开发者开关)。
    void SeedDemoTasks()
    {
        Tasks.Add("(示例)生成课件大纲", "第 3 / 8 页 · 课程与演示", "courses", 0.38);
        Tasks.Add("(示例)翻译长文", "中 → 日 · 详细解释档", "translation", 0.72);

        // 项目田字格同理:没有项目就只剩空态,没法评审方块布局。同样明确标注「示例」。
        Projects.Add(new Project("p1", "(示例)家庭旅行计划", "对话 · 12 条消息", "chat", ProjectScope.Family, DateTime.Now.AddMinutes(-8)));
        Projects.Add(new Project("p2", "(示例)客厅灯光方案", "资产 · 3 张草稿", "assets", ProjectScope.Family, DateTime.Now.AddHours(-2)));
        Projects.Add(new Project("p3", "(示例)日语课件 第 4 讲", "课件草稿 · 8 页", "courses", ProjectScope.Personal, DateTime.Now.AddHours(-5)));
        Projects.Add(new Project("p4", "(示例)论文摘要翻译", "中 → 日 · 详细解释", "translation", ProjectScope.Personal, DateTime.Now.AddDays(-1)));
    }

    void RegisterCleanupSteps()
    {
        // ① 结束与中枢的会话 + 请主机释放本客户端占用的显存。
        //    ★ 语义要点:请求的是"释放**本会话**占用",不是"卸载所有模型" ——
        //      副机退出绝不能把另一个人正在用的模型干掉(引用计数归零才真卸载,主机侧负责)。
        Lifecycle.Register("end-session+release-vram", async ct =>
        {
            if (!Hub.IsPaired) return;
            await Hub.EndSessionAsync(ct);
        });

        // ② 落盘界面偏好(皮肤/语言/自启开关),避免设置改了没保存。
        Lifecycle.Register("save-settings", () => Settings.Save());

        // ③ 收掉托盘图标,否则进程没了图标还赖在任务栏上直到鼠标划过。
        Lifecycle.Register("stop-vram-monitor", () => Vram.Dispose());

        Lifecycle.Register("dispose-tray", () => { _tray?.Dispose(); _tray = null; });
    }

    void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            // 暂用系统图标;墨白皮肤的自制黑白图标随视觉资源一起补(用户指定设计理念)。
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = Strings.Get("app.title"),
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(Strings.Get("tray.open"), null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Strings.Get("tray.exit"), null, (_, _) => Dispatcher.Invoke(() => ExitApplication("tray-menu")));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        UpdateTrayTooltip();
    }

    /// <summary>语言变更后重建托盘菜单项(菜单文案同样是构造时取的)。</summary>
    void RebuildTrayMenu()
    {
        if (_tray is null) return;
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(Strings.Get("tray.open"), null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Strings.Get("tray.exit"), null, (_, _) => Dispatcher.Invoke(() => ExitApplication("tray-menu")));
        _tray.ContextMenuStrip?.Dispose();
        _tray.ContextMenuStrip = menu;
        UpdateTrayTooltip();
    }

    public void UpdateTrayTooltip()
    {
        if (_tray is null) return;
        var key = Hub.State == HubState.Online ? "tray.tooltip_online" : "tray.tooltip_offline";
        // NotifyIcon.Text 上限 63 字符,超了会抛;这里文案短,仍做个保险截断。
        var t = Strings.Get(key);
        _tray.Text = t.Length > 62 ? t[..62] : t;
    }

    public void ShowMainWindow()
    {
        _main ??= new MainWindow();
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true; _main.Topmost = false;   // 强制前置,否则可能只在任务栏闪
    }

    // 关窗口 ≠ 退出:按用户要求隐藏到托盘,后台继续(不做善后、不释放显存)。
    void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!Settings.MinimizeToTrayOnClose) { ExitApplication("window-close"); return; }
        e.Cancel = true;
        _main?.Hide();
    }

    /// <summary>真正退出:善后恰好一次,然后结束进程。</summary>
    public void ExitApplication(string reason)
    {
        RunCleanup(reason);
        _instance.Dispose();
        Current.Shutdown();
    }

    // 在线程池线程上跑善后并阻塞等待。脱离 UI 同步上下文是关键 —— 否则 async 善后里的
    // await 续体会想回被本调用阻塞着的 UI 线程,互相死等(WPF 退出死锁的经典成因)。
    void RunCleanup(string reason)
    {
        try { Task.Run(() => Lifecycle.RunOnceAsync(reason)).GetAwaiter().GetResult(); }
        catch { /* 善后已是尽力而为;它自身抛异常也不能挡住退出 */ }
    }
}
