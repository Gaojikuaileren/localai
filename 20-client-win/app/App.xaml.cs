// P3c -- 应用生命周期。用户要求的两条行为在这里落地:
//   「关窗口保持后台任务栏图标」 -> ShutdownMode=OnExplicitShutdown + Closing 拦截 + 托盘图标
//   「退出时关闭窗口、释放显存、做好关闭善后」 -> ShutdownCoordinator,四个退出入口汇一处、只跑一次
//
// 注意 ShutdownMode:默认 OnLastWindowClose 会在窗口一关就退进程,那样"留在托盘"根本无从谈起。

using System.Windows;
using LocalAI.Client.I18n;
using LocalAI.Client.Services;
using LocalAI.Client.Theme;
using LocalAI.Client.Views;
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
    /// <summary>「待办与家务」——主页待办板块的数据源(中枢自有数据,手动增删改当场生效)。</summary>
    public TodoCenter Todos { get; } = new();
    public FileTransState FileTrans { get; } = new();   // 文件翻译(D59)
    public I18nState I18n { get; } = new();              // 多语言表(D60)
    /// <summary>聊天:普通会话 + 项目会话。AI 未接入,发送只记录不伪造回复。</summary>
    public ChatCenter Chat { get; } = new();
    /// <summary>记忆库:AI 生成的摘要/事实。★ AI 未接入(P4)前不会有任何内容,界面如实说明。</summary>
    public MemoryCenter Memory { get; } = new();
    /// <summary>翻译工作空间:目标语言池 + 详细程度(每台设备各自的偏好)。</summary>
    public TranslationState Translation { get; } = new();
    /// <summary>学习笔记:翻译结果的收藏,按目标语言分类。</summary>
    public NoteCenter Notes { get; } = new();
    /// <summary>翻译历史:会话消息的一个【视图】,只额外存收藏了哪几条(见 TranslationHistory)。</summary>
    public TranslationHistory History { get; }
    /// <summary>翻译工作空间的三个场景,以及同声传译的状态(见 InterpretState)。</summary>
    public InterpretState Interpret { get; } = new();
    // 命名成 Lifecycle 而不是 Shutdown:后者会遮蔽 Application.Shutdown(),是个陷阱
    // (将来有人在 App 内写 Shutdown() 想退应用,拿到的却是这个协调器)。
    public ShutdownCoordinator Lifecycle { get; } = new();

    public App(SingleInstance instance, bool startHidden)
    {
        History = new TranslationHistory(Chat);
        _instance = instance;
        _startHidden = startHidden;
        // 窗口全关也不退出 —— 退出只能由用户显式触发(托盘「退出」)或系统关机。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureStateDir();

        // ★ 全局异常兜底:此前没有任何处理 —— 任何未捕获异常 = 进程【静默闪退】(用户反馈"添加附件闪退"就是这类)。
        //   UI 线程异常记日志 + 用我们的对话框如实告知,并【标记已处理】让应用活下来(多数是可恢复的,如某个对话框抛错);
        //   非 UI 线程/进程级只能记日志(那时已无法挽救,但至少留下堆栈)。日志落 {state}\crash.log。
        DispatcherUnhandledException += (_, ex) =>
        {
            LogCrash("ui", ex.Exception);
            try { ConfirmDialog.Show("出错了(已记录)", ex.Exception.Message + "\n\n已写入 crash.log;应用会继续运行。", confirmText: "好", cancelText: "关闭"); } catch { }
            ex.Handled = true;   // 别让一处异常掀翻整个应用
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => LogCrash("domain", ex.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) => { LogCrash("task", ex.Exception); ex.SetObserved(); };

        Settings = AppSettings.Load();
        Strings.Language = Settings.Language;
        Vocab.Current = Settings.OrgVocab;   // ★ 在建窗口之前 —— 否则首屏还是旧用词
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

        // ★ 先读本地存档,再决定要不要播种示例;两者都必须在建窗口【之前】完成 ——
        //   否则界面构建时读到的是空表,表现为"开启时读不出来,点一下才有"。
        var hadStore = ClientStore.HasAnyStore();
        var hadCalendar = File.Exists(ClientStore.CalendarPath);   // 日历单独落盘,独立判断是否首见
        var mergedOnLoad = LoadStores();      // 合并了重复项目就得补存(见下)
        // ★★ 示例数据【已停止播种】(用户要求 2026-07-31):真实的日历/待办已经能从 Apple 拉进来,
        //   再摆一堆"(示例)"只会和真数据混在一起,分不清哪条是真的 —— 那本身就是一种误导。
        //   下面这一次性清理会把此前播下的示例删掉;清过之后记档,不再重复扫。
        PurgeDemoDataOnce();
        // 同传示例同样【独立判断】:这台机器上已经有存档的用户,也该看得到这条记录长什么样。
        // 判据是"一条同传会话都没有",而不是"是不是首次运行" —— 删掉之后不会再冒出来。
        // ★ 判据是【播没播过】而不是【列表里有没有】—— 删除是软删除,
        //   拿"有没有"反推的话用户删一次它就长回来一条新的。
        var hadInterpret = Settings.InterpretDemoSeeded;
        if (!hadInterpret) { SeedDemoInterpret(); Settings.InterpretDemoSeeded = true; Settings.Save(); }
        AttachAutoSave();
        // ★ 首次运行必须【立刻】把示例落盘:播种发生在订阅之前,不会触发自动保存;
        //   不补这一次,下次启动仍算"无存档"→ 又播一遍种,用户删掉的示例还会复活。
        //   合并重复项目同理:它发生在订阅【之前】,不补这一次就不会落盘,下次启动又是一堆重复。
        if (!hadStore || !hadCalendar || !hadInterpret || mergedOnLoad) SaveStores();
        // 按"自动删除超过 X 天的已完成"设置清一次(0 = 关闭)。放在建窗口前,界面直接看到清理后的结果。
        Todos.PurgeCompletedOlderThan(Settings.TodoAutoPurgeDays);

        _main = new MainWindow();
        _main.Closing += OnMainWindowClosing;
        if (!_startHidden) _main.Show();

        // Apple 日历的自动拉取(默认关;认证失败会自动熔断,见 AppleAutoSync)
        AppleAutoSync.Install(() => Settings, () => MemberContext.Current);

        _instance.ListenForWake(() => Dispatcher.Invoke(ShowMainWindow));

        // 启动即用已保存的档案连一次:配对过就自动连上,不再打扰用户(用户要求 3)。
        _ = Task.Run(async () => { await Hub.ProbeAsync(); Dispatcher.Invoke(UpdateTrayTooltip); });
    }

    /// <summary>
    /// 一次性清掉此前播下的示例数据(标题带「(示例)」的那些)。
    /// ★ 只删【明确标注示例】的:用户自己建的一律不碰。清过一次就记档,不再重复扫。
    /// ★ 为什么不留着:日历与待办现在能从 Apple 拉到真数据,示例混在里面分不清真假。
    /// </summary>
    void PurgeDemoDataOnce()
    {
        if (Settings.DemoDataPurged) return;

        const string Mark = "(示例)";
        var n = 0;

        foreach (var t in Tasks.Tasks.Where(x => x.Title.Contains(Mark)).ToList())
        { Tasks.Remove(t.Id); n++; }

        foreach (var pr in Projects.All().Where(x => x.Title.Contains(Mark)).ToList())
        { Projects.PurgeProject(pr.ProjectId); n++; }

        foreach (var td in Todos.Items.Where(x => x.Title.Contains(Mark)).ToList())
        { Todos.Remove(td.Id); n++; }

        // 会话:先软删再彻底清(PurgeDeleted 只认已软删的),连温层归档一起清掉
        foreach (var se in Chat.Sessions.Where(x => x.Title.Contains(Mark)).ToList())
        { Chat.Delete(se.SessionId); Chat.PurgeDeleted(se.SessionId); n++; }

        foreach (var ev in Views.CalendarData.Events.Where(e => (e.Title ?? "").Contains(Mark)).ToList())
        { Views.CalendarData.Remove(ev); n++; }

        Settings.DemoDataPurged = true;
        Settings.Save();
        if (n > 0) SaveStores();
    }

    // 外壳评审期的示例任务。真实任务源要等各工作空间接入(P4/P6/P9),在那之前底部横条
    // 永远不会出现、也就没法评审。★ 标题明确标注「示例」——不伪装成真实任务。
    // 真实任务接入后删掉这段(或改成 Settings 里的开发者开关)。
    void SeedDemoTasks()
    {
        Tasks.Add("(示例)生成课件大纲", "第 3 / 8 页 · 课程与演示", "courses", 0.38);
        Tasks.Add("(示例)翻译长文", "中 → 日 · 详细解释档", "translation", 0.72);

        // 项目田字格同理:没有项目就只剩空态,没法评审方块布局。标注「示例」。覆盖三种状态(准备中/进行中/已完成)。
        Projects.Add(new Project("p1", "(示例)家庭旅行计划", "对话 · 12 条消息", "chat", ProjectScope.Family, DateTime.Now.AddMinutes(-8), Status: ProjectStatus.Active, OwnerMemberId: MemberContext.Current));
        Projects.Add(new Project("p2", "(示例)客厅灯光方案", "资产 · 3 张草稿", "assets", ProjectScope.Family, DateTime.Now.AddHours(-2), Pinned: true, Status: ProjectStatus.Active, FolderPath: AppContext.BaseDirectory, OwnerMemberId: MemberContext.Current));
        Projects.Add(new Project("p3", "(示例)日语课件 第 4 讲", "课件草稿 · 8 页", "courses", ProjectScope.Personal, DateTime.Now.AddHours(-5), Status: ProjectStatus.Preparing, OwnerMemberId: MemberContext.Current));
        Projects.Add(new Project("p4", "(示例)论文摘要翻译", "中 → 日 · 详细解释", "translation", ProjectScope.Personal, DateTime.Now.AddDays(-1), Status: ProjectStatus.Active, OwnerMemberId: MemberContext.Current));
        Projects.Add(new Project("p5", "(示例)旧网站搬迁", "已收尾归档", "chat", ProjectScope.Personal, DateTime.Now.AddDays(-9), Status: ProjectStatus.Done, OwnerMemberId: MemberContext.Current));

        // 聊天示例:一个普通会话、一个归在"家庭旅行计划"项目下的会话
        Chat.NewSession(null, "chat", ProjectScope.Personal, "(示例)随便问问");
        Chat.NewSession("p1", "chat", ProjectScope.Family, "(示例)行程讨论");

        SeedDemoTodos();   // 日历示例由 SeedDemoEvents 独立播种(日历单独落盘)
    }

    /// <summary>
    /// 一段示例同传记录 —— 和普通会话排在同一个列表里(用户裁定),用来看版面:
    /// 左边对方、右边我方,和聊天空间同一套气泡。
    /// ★ 标着「(示例)」:语音链路还没接入,这不是真的转写,不能让它看起来像。
    /// </summary>
    void SeedDemoInterpret()
    {
        var s = Chat.NewSession(null, "translation", ProjectScope.Personal, "(示例)同传记录 · 中↔日", interpret: true);
        var t0 = DateTime.Now.AddMinutes(-26);
        (bool me, string text)[] lines =
        {
            // ★ 对方那侧【原文一行、译文一行】—— 同传里对方只出字幕不出语音,
            //   所以原话必须留在记录里:字幕会改口,记录不该只剩译文这一面之词。
            (false, "お忙しいところありがとうございます。今日は納期の件で相談させてください。\n(感谢百忙之中抽空。今天想就交期的事情和您商量。)"),
            (true,  "没问题。我们这边把测试排到了下周三,交期我想确认一下有没有余量。"),
            (false, "テストが水曜に終わるなら、金曜の出荷に間に合います。ただ検品は木曜の午前中までにお願いしたいです。\n(如果测试周三结束,周五发货来得及。不过检验希望在周四上午之前完成。)"),
            (true,  "周四上午可以。检验报告我们当天下午发给你们。"),
            (false, "助かります。では金曜出荷で進めますね。\n(帮大忙了。那就按周五发货推进。)"),
            (true,  "好的,我这边同步给生产。"),
        };
        for (int i = 0; i < lines.Length; i++)
            Chat.SeedMessage(s.SessionId, lines[i].me ? ChatRole.User : ChatRole.Assistant,
                             lines[i].text, t0.AddSeconds(i * 47));
    }

    // 待办/家务同理:没有条目就只剩空态,没法评审列表与勾选交互。全部标注「(示例)」。
    // 覆盖:有时间的家务、带旗标+高优先级的个人待办、无截止的待办、已完成沉底的家务。
    void SeedDemoTodos()
    {
        // 手动建立的:各种状态 —— 今天到期的家务、逾期高优先级、无截止、已完成
        Todos.Add(new TodoItem(TodoCenter.NewId(), "(示例)买菜:西红柿、鸡蛋、牛奶", TodoKind.Chore,
            Due: DateTime.Today.AddHours(18), DueHasTime: true, Owner: "双方", Scope: "家庭"));
        Todos.Add(new TodoItem(TodoCenter.NewId(), "(示例)交电费", TodoKind.Personal,
            Due: DateTime.Today.AddDays(-1), Flagged: true, Priority: TodoPriority.High, Owner: "我", Scope: "个人"));  // 逾期
        Todos.Add(new TodoItem(TodoCenter.NewId(), "(示例)预约理发", TodoKind.Personal, Owner: "我", Scope: "个人"));  // 无截止
        Todos.Add(new TodoItem(TodoCenter.NewId(), "(示例)倒垃圾", TodoKind.Chore,
            Done: true, Owner: "我", Scope: "家庭", CompletedAt: DateTime.Now.AddHours(-2)));  // 已完成(手动)

        // ★ AI 建立的:带星标,同样覆盖未完成 / 高优先级 / 已完成三种状态,便于对照
        Todos.Add(new TodoItem(TodoCenter.NewId(), "(示例)续借图书馆的书", TodoKind.Personal,
            Due: DateTime.Today.AddDays(2), Owner: "我", Scope: "个人", CreatedByAi: true));
        Todos.Add(new TodoItem(TodoCenter.NewId(), "(示例)提醒对方周五体检空腹", TodoKind.Chore,
            Due: DateTime.Today.AddDays(3), DueHasTime: false, Priority: TodoPriority.Medium, Owner: "对方", Scope: "家庭", CreatedByAi: true));
        Todos.Add(new TodoItem(TodoCenter.NewId(), "(示例)整理上周会议纪要", TodoKind.Personal,
            Done: true, Owner: "我", Scope: "个人", CompletedAt: DateTime.Now.AddHours(-5), CreatedByAi: true));  // 已完成(AI)
    }

    // 日历同理:没有日程就只能看到空的格子,没法评审"有日程标点 / 点日期看当天"这些交互。
    // ★ 全部标注「(示例)」;数据源(Apple 家庭共享日历)接入后删掉这段。
    // 覆盖了几种情况:今天多条、明天单条、跨周、周末、下月初 —— 方便验证周/月两种排布。
    void SeedDemoEvents()
    {
        var today = DateTime.Today;
        // ★ 走 CalendarData.Add(而非 Events.Add):让每条都拿到稳定 Id,否则编辑器按 Id 定位会错行。
        void Ev(int dayOffset, int h, int m, int durMin, string title, string owner, string scope, bool ai = false)
            => Views.CalendarData.Add(new Views.CalendarEvent(
                today.AddDays(dayOffset).AddHours(h).AddMinutes(m),
                today.AddDays(dayOffset).AddHours(h).AddMinutes(m + durMin),
                title, owner, scope, CreatedByAi: ai));

        // 今天:三条,验证"多条日程"的点与列表
        Ev(0,  9, 30,  60, "(示例)晨会", "我", "家庭");
        Ev(0, 12, 30,  60, "(示例)午饭 · 和家人", "双方", "家庭");
        Ev(0, 19,  0,  90, "(示例)日语课", "我", "个人", ai: true);          // AI 建立
        // 本周其它天
        Ev(1, 10,  0,  45, "(示例)牙医预约", "对方", "个人", ai: true);        // AI 建立
        Ev(3, 15,  0, 120, "(示例)超市采购", "双方", "家庭");
        // 故意堆满一天(5 条定时)—— 验证"超过 4 条改用实心三角形"
        Ev(6,  8,  0,  30, "(示例)晨跑", "我", "个人");
        Ev(6,  9, 30,  60, "(示例)周会", "我", "家庭");
        Ev(6, 12,  0,  60, "(示例)午饭", "双方", "家庭");
        Ev(6, 14,  0,  90, "(示例)客户电话", "我", "个人");
        Ev(6, 18,  0,  60, "(示例)健身", "我", "个人");
        // 周末
        Ev(5, 11,  0, 180, "(示例)周末远足", "双方", "家庭");
        // 下周(验证周排布翻页 / 两周铺开)
        Ev(8,  9,  0,  60, "(示例)体检", "我", "个人");
        Ev(10, 20, 0,  90, "(示例)家庭电影夜", "双方", "家庭");
        // 下月初(验证月排布翻月)
        Ev(32, 14, 0,  60, "(示例)季度复盘", "我", "个人", ai: true);          // AI 建立

        // 全天 / 跨天(验证贯穿多格的长条):单日全天、本周内跨 3 天、跨周 5 天
        void AllDay(int fromOffset, int toOffset, string title, string owner, string scope, string group, bool ai = false)
            => Views.CalendarData.Add(new Views.CalendarEvent(
                today.AddDays(fromOffset), today.AddDays(toOffset), title, owner, scope,
                AllDay: true, CalendarGroup: group, Location: "", Url: "", Notes: "", CreatedByAi: ai));

        AllDay(2, 2, "(示例)公休日", "双方", "家庭", "家庭");
        AllDay(4, 6, "(示例)出差 · 柏林", "我", "个人", "工作", ai: true);   // AI 建立
        AllDay(9, 13, "(示例)家庭旅行", "双方", "家庭", "家庭");
    }

    // 崩溃日志:追加到 {state}\crash.log(带时间/来源/完整堆栈)。写日志本身绝不能再抛。
    static void LogCrash(string source, Exception? ex)
    {
        try
        {
            AppPaths.EnsureStateDir();
            var line = $"\n===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] =====\n{ex}\n";
            File.AppendAllText(Path.Combine(AppPaths.StateDir, "crash.log"), line);
        }
        catch { }
    }

    // ---------------------------------------------------------------- 本地存档(明文,D21/D22 口径)
    // 用户裁定(2026-07-30):项目/会话/待办落盘为明文,与记忆库、备份同一处理,不引入客户端密钥管理。
    // ★ 幽灵会话不落盘(ChatCenter.Export 排除),已删除会话连 DeletedAt 一起存、启动时扫过期。
    /// <summary>读存档。返回 true 表示【加载过程中改动过数据】(如合并了重复项目),调用方需要补存一次。</summary>
    // ★★ 导入失败的存档【禁止参与退出保存】(审计 2026-08-02):
    //   某一份档字段为 null(语法合法,反序列化不抛,ClientStore 的"损坏改名"兜不住)时,
    //   原先 LoadStores 在那一步整个炸掉 —— 其后所有 store 都没导入(内存为空),
    //   而退出钩子照样跑 SaveStores,把盘上完好的日历/记忆/笔记全部用【空表】覆盖。
    //   现在:每份档各自 try(坏档改名留证、当空档继续),写盘只跳过【真的没导入成功】的那几份。
    readonly HashSet<string> _failedStores = new(StringComparer.OrdinalIgnoreCase);

    void SafeImport(string path, Action import)
    {
        try { import(); }
        catch (Exception ex)
        {
            _failedStores.Add(path);
            try
            {
                if (File.Exists(path)) File.Move(path, path + ".corrupt", overwrite: true);
                LogCrash("store-import:" + Path.GetFileName(path), ex);
            }
            catch { }
        }
    }

    bool LoadStores()
    {
        SafeImport(ClientStore.ProjectsPath, () => Projects.Import(ClientStore.Load<List<Project>>(ClientStore.ProjectsPath)));
        SafeImport(ClientStore.TodosPath, () => Todos.Import(ClientStore.Load<List<TodoItem>>(ClientStore.TodosPath)));
        SafeImport(ClientStore.ChatPath, () => Chat.Import(ClientStore.Load<ChatCenter.Snapshot>(ClientStore.ChatPath)));
        SafeImport(ClientStore.CalendarPath, () => Views.CalendarData.Import(ClientStore.Load<List<Views.CalendarEvent>>(ClientStore.CalendarPath)));
        // ★ 天气缓存恢复 —— 重启后仍然如实标着它是什么时候取的(断网也能看到上次那份)
        SafeImport(ClientStore.WeatherPath, () => Services.Weather.Import(ClientStore.Load<Dictionary<string, Services.WeatherNow>>(ClientStore.WeatherPath)));
        // ★ 日程分类表(= Apple 那边的日历清单 + 颜色)从存档恢复 ——
        //   否则开机后到"去设置里刷新一次清单"之前，新建日程的归类会先退回本地占位，
        //   颜色也跟着变一次 —— 看起来就像断连了。
        {
            var saved = Settings.AppleCalendarList
                .Select(x => x.Split('|', 3))
                .Where(a2 => a2.Length >= 2 && a2[1].Length > 0)
                .Select(a2 => (a2[1], a2.Length >= 3 && a2[2].Length > 0 ? a2[2] : null))
                .ToList();
            if (saved.Count > 0) Services.CalendarGroups.SetFromApple(saved!);
        }
        SafeImport(ClientStore.MemoryPath, () => Memory.Import(ClientStore.Load<List<MemoryEntry>>(ClientStore.MemoryPath)));
        SafeImport(ClientStore.NotesPath, () => Notes.Import(ClientStore.Load<List<StudyNote>>(ClientStore.NotesPath)));
        SafeImport(ClientStore.HistoryFavPath, () => History.Import(ClientStore.Load<List<string>>(ClientStore.HistoryFavPath)));
        SafeImport(ClientStore.InterpretPath, () => Interpret.Import(ClientStore.Load<InterpretState.Snapshot>(ClientStore.InterpretPath)));
        SafeImport(ClientStore.TranslationPath, () => Translation.Import(ClientStore.Load<TranslationState.Snapshot>(ClientStore.TranslationPath)));
        SafeImport(ClientStore.FileTransPath, () => FileTrans.Import(ClientStore.Load<Dictionary<string, FileDoc>>(ClientStore.FileTransPath)));
        SafeImport(ClientStore.I18nPath, () => I18n.Import(ClientStore.Load<I18nDoc>(ClientStore.I18nPath)));
        // ★ 旧存档可能有"同一路径两个项目"(那时还没唯一性约束):合并掉,会话并到保留的那个。
        //   只合并【完全相同的路径 + 同一台机器】—— 子路径不算重复(用户裁定)。
        var merged = Projects.MergeDuplicateFolders((fromId, toId) => Chat.ReassignSessions(fromId, toId)) > 0;
        // ★ 分层存储:把超出热层的旧消息移到温层(仍是原文,只换文件放),让 chat.json 保持有界。
        //   平时不加载,用户在会话顶部点"加载更早"才读回来。
        var archived = Chat.ArchiveOldMessages() > 0;
        return merged || archived;
    }

    // 变更 -> 防抖 400ms 后落盘。防抖是必要的:一次操作常触发多次 Changed(改状态 + 迁会话),
    // 不防抖就会连写好几次。退出时再无条件存一次(见 RegisterCleanupSteps)。
    readonly System.Windows.Threading.DispatcherTimer _saveDebounce =
        new() { Interval = TimeSpan.FromMilliseconds(400) };

    void AttachAutoSave()
    {
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SaveStores(); };
        void Touch() => Dispatcher.Invoke(() => { _saveDebounce.Stop(); _saveDebounce.Start(); });
        Projects.Changed += Touch;
        Todos.Changed += Touch;
        FileTrans.Changed += Touch;
        I18n.Changed += Touch;
        Chat.Changed += Touch;
        Views.CalendarData.Changed += Touch;
        // ★ 天气缓存也要落盘 —— 不接这一行的话,缓存只活在内存里,
        //   重启/断网时"显示上次那份 + 它的时间"根本无从谈起(设计 §8 第 6 条的整个意义就没了)。
        Services.Weather.Changed += Touch;
        Memory.Changed += Touch;
        Notes.Changed += Touch;
        History.Changed += Touch;
        Interpret.Changed += Touch;
        Translation.Changed += Touch;
    }

    void SaveStores()
    {
        // ★ 只存【导入成功过】的档(fail-closed):没导入成功的那份,内存里是空的,
        //   写回去等于用空表覆盖盘上可能还完好的数据(见 SafeImport 的说明)。
        void S<T>(string path, T data) { if (!_failedStores.Contains(path)) ClientStore.Save(path, data); }
        S(ClientStore.ProjectsPath, Projects.Export());
        S(ClientStore.TodosPath, Todos.Export());
        S(ClientStore.ChatPath, Chat.Export());
        S(ClientStore.CalendarPath, Views.CalendarData.Export());
        S(ClientStore.WeatherPath, Services.Weather.Export());
        S(ClientStore.MemoryPath, Memory.Export());
        S(ClientStore.NotesPath, Notes.Export());
        S(ClientStore.HistoryFavPath, History.Export());
        S(ClientStore.InterpretPath, Interpret.Export());
        S(ClientStore.TranslationPath, Translation.Export());
        S(ClientStore.FileTransPath, FileTrans.Export());
        S(ClientStore.I18nPath, I18n.ExportDoc());
    }

    void RegisterCleanupSteps()
    {
        // ① 退出前把未落盘的改动存下来(防抖可能还没到点)。放在最前:先保住数据,再谈释放资源。
        Lifecycle.Register("save-client-stores", () => SaveStores());

        // ② 结束与中枢的会话 + 请主机释放本客户端占用的显存。
        //    ★ 语义要点:请求的是"释放**本会话**占用",不是"卸载所有模型" ——
        //      副机退出绝不能把另一个人正在用的模型干掉(引用计数归零才真卸载,主机侧负责)。
        Lifecycle.Register("end-session+release-vram", async ct =>
        {
            if (!Hub.IsPaired) return;
            await Hub.EndSessionAsync(ct);
        });

        // ③ 落盘界面偏好(皮肤/语言/自启开关),避免设置改了没保存。
        Lifecycle.Register("save-settings", () => Settings.Save());

        // ④ 停显存监视;⑤ 收掉托盘图标(否则进程没了图标还赖在任务栏上直到鼠标划过)。
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
