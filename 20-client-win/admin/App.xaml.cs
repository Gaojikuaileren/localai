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

        // ══════════════════════════════════════════════════════════════════
        //  ★★★ V21:记忆库的一次性迁移。**排在建窗口之前**,理由是承重的:
        //    迁移失败时界面必须能把这件事说出来(纪律③),而窗口是在
        //    `ShowPanel()` 里建的 —— 迁移排在后面的话,第一次打开的那一页
        //    读到的是「还没迁」的状态,于是**它会显示一个空记忆库**,
        //    而那正是用户会读成「我的记忆没了」的那句假话。
        //  ★ 幂等:每次启动都跑,跑几次结果都一样(见 MemoryStore 文件头的四格表)。
        //  ★★ 迁移失败**不阻止管理端启动** —— 那会让人连看原因的地方都没有。
        //    失败会留在 `MemoryStore.Notice` 里,由「记忆库」那一页顶上说出来。
        // ══════════════════════════════════════════════════════════════════
        MemoryStore.Migrate();
        if (MemoryStore.LoadOrNull() is { } mem) Views.MemoryView.Memory.Import(mem);
        Views.MemoryView.Memory.Changed += SaveMemory;

        BuildTray();
        WatchSettings();

        // ══════════════════════════════════════════════════════════════════
        //  ★★★ V22(D115):**管理端起来就把栈起起来**。用户裁定 2026-08-09:
        //    「我不要手工起,**必须自动起**」。
        //
        //  ★ 在此之前 `HostProvision.EnsureStackAsync` **零生产调用点** ——
        //    V21 把客户端那个入口删掉之后,实机上**栈谁都起不了**,
        //    只能让用户自己去跑 `90-ops\start-stack.ps1`。这一行就是那个缺口。
        //
        //  ★★ 两条启动路径**结构性**地都盖到:`OnStartup` 对「双击图标」与
        //    「客户端拉起(--tray)」是同一个方法,起栈挂在这里就不靠任何 if 去分辨。
        //    ⇒ 这也是为什么它排在 `if (!_startHidden)` **之前**:排在后面就只剩双击那一条路。
        //
        //  ★★★ 不 await:起栈里有两个 20 秒的探活窗口,await 会让双击图标之后
        //    窗口要等到最坏 40 秒才出现 —— 而"窗口不出来"和"没起来"在用户眼里一模一样。
        //    进度由 `StackBoot.Changed` 播给「主机中枢」那一页。
        // ══════════════════════════════════════════════════════════════════
        _ = StackBoot.EnsureAsync();

        // 第 2 条:双击图标 -> 正常显示界面。★ 这里**不碰客户端** —— 一个字都不起。
        if (!_startHidden) ShowPanel();

        // 再点一次图标 = 叫醒已有实例的窗口(与客户端同一套机制)
        _instance.ListenForWake(() => Dispatcher.Invoke(ShowPanel));
    }

    /// <summary>
    /// 记忆变了就存回**管理端自己那份**。★ 与客户端 `Touch` 同一条纪律:改一次存一次,
    /// 不等退出 —— 管理端是后台常驻的,它没有「退出」这个可靠的存盘时机。
    /// </summary>
    void SaveMemory()
    {
        // ★★ 迁移失败时**绝不存盘**:那会拿一个空库覆盖掉…… 不,它甚至更坏 ——
        //   迁移失败意味着我们没读到你的记忆,这时存一份空的过去,
        //   就把「读不到」变成了「真的没有了」。宁可这一次不存。
        if (MemoryStore.LastResult == MemoryMigration.Failed) return;
        try { MemoryStore.Save(Views.MemoryView.Memory.Export()); } catch { }
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
    /// <summary>托盘菜单里「打开管理端面板」那一项的名字(自检按名字找它)。</summary>
    internal const string TrayOpenItemName = "TrayOpenPanel";

    /// <summary>托盘菜单里「关闭」那一项的名字 —— 裁定第 6 条说的唯一真关闭入口。</summary>
    internal const string TrayCloseItemName = "TrayRealClose";

    void BuildTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        var open = menu.Items.Add("打开管理端面板", null, (_, _) => Dispatcher.Invoke(ShowPanel));
        open.Name = TrayOpenItemName;
        menu.Items.Add(new WinForms.ToolStripSeparator());
        // ★★ 第 6 条:**真正关闭只能走这里**。窗口的 × 只缩托盘。
        var close = menu.Items.Add("关闭", null, (_, _) => Dispatcher.Invoke(async () => await RealCloseAsync()));
        // ★ 起个名字是为了让自检**按身份**找到它,而不是按显示文案去猜 ——
        //   按文案找的断言会在改文案那天红,而它本来要守的是「这条路通不通」。
        close.Name = TrayCloseItemName;

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
    /// 托盘右键 → 关闭。★ 顺序不能颠倒:
    /// ① 先问「现在关栈会不会切断别人」—— 把判据摆给人看,由**人**决定(D102 裁定④);
    /// ② 请主机客户端**优雅退出**(八步),★ 等不到就如实说,**绝不强杀**;
    /// ③ ★★★ V22:**真的把栈停掉**,然后**验**它没了。
    ///
    /// <para>★★★ 第 ③ 步在此之前**根本不存在** —— 框弹了、人点了确定、客户端退了,
    /// 而网关与 lan-edge 原地继续跑。「关掉整套 AI 栈」这句话是假的(见 StackStop 文件头)。</para>
    ///
    /// <para>★ 为什么排在客户端退出**之后**:客户端正用着这套栈。先拆栈再让它退,
    /// 它那八步善后里每一次拨号都会失败,日志里全是连不上 —— 而那是我们自己造的假现场。</para>
    /// </summary>
    // ══════════════════════════════════════════════════════════════════════════
    //  ★ 诚实的测试缝(捞自未并分支 `worktree-agent-ad36411fae961778d`,逐段搬,不整片 apply)
    //
    //  出包自检要覆盖管理端,而「托盘右键能真关闭 + 栈真的没了」是**行为**判据,
    //  不是源码文本判据。只验「源码里有个叫『关闭』的菜单项」仍然是"编得过",
    //  不是"跑得起来"(ASSERTION-PITFALLS 第 12 条:4197 条全绿而真机开不起来)。
    //
    //  ★★ 而这条路中间隔着一个**模态框** —— 自检进程里没有人去点它,进程会当场挂死。
    //    那正是 `admin/Selftest.cs` 今天那条 SKIP 的真原因。
    //    ⇒ 把「问人」和「告诉人」抽成可替换的一环,自检就能走**真正那条路**。
    //  ★ 默认实现**就是原来那两个 MessageBox**,生产路径一个字都没有改变;
    //    自检替换的只是"谁来回答那一句",不是被测的那条路本身。
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>关栈前问人的那一下。默认 = 原来的模态框;自检替换成"就当人点了确定"。</summary>
    internal Func<string, bool> ConfirmClose { get; set; } = text =>
        MessageBox.Show(text, "关闭管理端", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            == MessageBoxResult.OK;

    /// <summary>关不成时告诉人的那一下。默认 = 原来的模态框。</summary>
    internal Action<string> ReportCloseBlocked { get; set; } = text =>
        MessageBox.Show(text, "关闭管理端", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>自检读:托盘图标。null = 压根没建出来;Visible=false = 收掉了。</summary>
    internal WinForms.NotifyIcon? TrayIcon => _tray;

    /// <summary>自检读:面板窗口。null = 还没建过 —— `--tray` 启动时就该是这样(不弹窗)。</summary>
    internal AdminWindow? Panel => _main;

    /// <summary>自检读:最近一次关栈的实测结果(没关过就是 null)。</summary>
    internal StackStop.StopReport? LastStopReport { get; private set; }

    async Task RealCloseAsync()
    {
        var verdict = await StackStop.QueryAsync();
        if (!ConfirmClose(StackStop.ConfirmText(verdict))) return;

        if (ClientLink.IsClientRunning())
        {
            var (stopped, why) = await ClientLink.RequestClientQuitAsync(TimeSpan.FromSeconds(20));
            if (!stopped)
            {
                // ★ 如实说,并且**不继续关自己** —— 管理端先走会让用户失去唯一的入口,
                //   而客户端还在那儿占着租约。
                // ★★ 也**不关栈**:客户端还在用它。
                ReportCloseBlocked(why + "\n\n管理端没有关闭,AI 栈也没有停。");
                return;
            }
        }

        // ── ③ 真的关栈,并验 ────────────────────────────────────────────
        var report = await StackStop.StopAsync();
        LastStopReport = report;
        if (!report.AllGone)
        {
            // ★★ 没停干净就**把还剩什么摆出来**,并且**不关掉管理端** ——
            //   管理端一走,用户就失去了唯一能再试一次的入口,而屏幕上刚才那句
            //   「已关闭」会变成一句没人能验证的话。
            ReportCloseBlocked(report.ToText()
                               + "\n\n★ 管理端没有关闭 —— 留着它,你还能再点一次「关闭」重试。");
            return;
        }

        _settingsWatcher?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _instance.Dispose();
        Shutdown();
    }
}
