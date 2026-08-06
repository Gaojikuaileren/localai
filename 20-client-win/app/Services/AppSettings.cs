// P3c -- 本设备的界面偏好。设计文档 §7:**每台设备独立选皮肤**,所以这些存在本机,不同步到中枢。
// 语言同理:UI 语言跟随设备设置,不随说话者切换(D45/设计 §6.1)。

using System.Text.Json;

namespace LocalAI.Client.Services;

public enum Skin { Breeze, Ink, Warm }        // 微风(默认) / 墨白 / 暖萌 —— 设计文档 §7
public enum ThemeMode { System, Light, Dark }
public enum Density { Comfortable, Compact }

public sealed class AppSettings
{
    public string Language { get; set; } = "zh-CN";      // zh-CN | en-US | ja-JP
    public Skin Skin { get; set; } = Skin.Breeze;

    /// <summary>
    /// 界面用词:对「共用这台中枢的这群人」的称谓(家庭 / 团队)。默认家庭。
    /// ★ 只影响界面文案,不影响任何存储值(见 Services/Vocab)。与皮肤/语言同类:每台设备各自的选择。
    /// </summary>
    public OrgVocab OrgVocab { get; set; } = OrgVocab.Family;

    // ---- Apple 日历(只读拉取)----
    /// <summary>要拉取的日历集合 URL。空 = 还没选。★ 不存密码(那在 AppleCredentials,DPAPI 加密)。</summary>
    public List<string> AppleCalendarUrls { get; set; } = new();

    /// <summary>
    /// 已发现的日历清单(URL|名字),【落盘保存】。
    /// ★ 为什么要存:此前列表只活在内存里 —— 切走再回来/重启就没了,
    ///   用户得再点一次"刷新日历列表"才能挑选。存下来才能【连上后一直在】。
    /// 只存 URL 与显示名,不存任何日程内容。
    /// </summary>
    public List<string> AppleCalendarList { get; set; } = new();

    /// <summary>上次成功拉取的时间(本地时间)。null = 从没成功过 —— 界面据此如实说"从未同步"。</summary>
    public DateTime? AppleLastSync { get; set; }

    /// <summary>拉取区间:往前多少天 / 往后多少天。默认过去 90 天 + 未来一年。</summary>
    public int AppleSyncPastDays { get; set; } = 90;
    public int AppleSyncFutureDays { get; set; } = 365;

    /// <summary>自动拉取(默认关)。★ 认证失败会被【自动关掉】—— 见 AppleAutoSync 的熔断。</summary>
    public bool AppleAutoPull { get; set; }

    /// <summary>待办(提醒事项)要拉取的清单 URL。与日历分开选 —— 它们在 iCloud 里就是两类集合。</summary>
    // ★ 原 AppleReminderUrls 已删除(2026-08-02,D56)。界面上的勾选框移除之后,
    //   若这里还留着并被同步读,旧存档里存过的 URL 会继续被拉 —— 那就是一个
    //   【用户关不掉的开关】。字段删掉,旧存档里多出来的这个键反序列化时自然被忽略。
    /// <summary>已发现的提醒事项清单(URL|名字),落盘。</summary>
    // ★ 原 AppleReminderList 已删除(2026-08-02,D57):待办是纯本机数据,
    //   我们既不拉提醒事项、也不该在设置页里列出它们 —— 列出来就是在暗示能同步。
    /// <summary>自动拉取间隔(分钟)。下限 15 —— 日历不是秒级数据,拉太勤只会更容易撞上节流。</summary>
    public int AppleAutoPullMinutes { get; set; } = 30;   // 默认 30 分钟(用户裁定 2026-07-31)
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public Density Density { get; set; } = Density.Comfortable;
    public bool Autostart { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;   // 用户要求:关窗口 = 留在托盘

    // ★ 这里【故意不存】当前成员/默认成员。
    // 铁律(gateway.py:227):「主体只来自成员表;客户端自报的 device_id / tier 一律忽略」。
    // 本文件是普通用户可用记事本改的 JSON;若可见范围判定读它,两成员共用一台机器时
    // 另一位改一行就能看「仅本人」—— D45 就成了演出。
    // 成员真相在主机的 Store.Device.DefaultMemberId,由 Edge 按证书指纹反查后下发,
    // 客户端只缓存**显示名**用于渲染(见 MemberDisplayCache),且任何权限判定都不读缓存。
    /// <summary>仅供界面渲染的成员显示名缓存 —— **不是**身份,不可用于任何可见范围/权限判定。</summary>
    public string? CachedMemberDisplayName { get; set; }

    /// <summary>
    /// 天气板块里【可拖动城市】的顺序(JSON:[[城市,时区], ...])。
    /// 第一格是"当前所在地",由系统时区推断、固定在首位,不参与这个顺序。
    /// 与皮肤/语言同理:每台设备各自的偏好,不同步到中枢。
    /// </summary>
    public string? WeatherCityOrder { get; set; }

    /// <summary>
    /// 左边栏【隐藏】的工作空间 key 列表(JSON string[])。在"扩展"里勾选控制。
    /// 存"隐藏项"而非"显示项":这样以后新增的工作空间默认可见,不必逐台设备去开。
    /// 每台设备各自的偏好,不同步到中枢。
    /// </summary>
    public List<string> HiddenWorkspaces { get; set; } = new();

    public bool IsWorkspaceVisible(string key) => !HiddenWorkspaces.Contains(key);

    public void SetWorkspaceVisible(string key, bool visible)
    {
        var changed = visible ? HiddenWorkspaces.Remove(key)
                              : (!HiddenWorkspaces.Contains(key) && AddHidden(key));
        if (changed) Save();
    }

    bool AddHidden(string key) { HiddenWorkspaces.Add(key); return true; }

    /// <summary>
    /// 左边栏工作空间的【顺序】(key 列表,在"扩展 › 工作空间"里拖动调整)。
    /// 空 = 用默认顺序。存这里的可能少于/多于当前清单 —— 取交集 + 新增项追加(见 Workspaces.Ordered)。
    /// 每台设备各自的偏好,不同步到中枢。
    /// </summary>
    public List<string> WorkspaceOrder { get; set; } = new();

    public void SetWorkspaceOrder(IEnumerable<string> keys) { WorkspaceOrder = keys.ToList(); Save(); }

    /// <summary>主页【隐藏】的板块 key 列表(日历/待办/天气/项目)。在"扩展 › 主页板块"里勾选。同样存"隐藏项"。</summary>
    public List<string> HiddenPanels { get; set; } = new();

    public bool IsPanelVisible(string key) => !HiddenPanels.Contains(key);

    public void SetPanelVisible(string key, bool visible)
    {
        var changed = visible ? HiddenPanels.Remove(key)
                              : (!HiddenPanels.Contains(key) && AddHiddenPanel(key));
        if (changed) Save();
    }

    bool AddHiddenPanel(string key) { HiddenPanels.Add(key); return true; }

    /// <summary>主页待办板块当前显示的分类(all/today/personal/chore/shopping)。每台设备各自的偏好。</summary>
    public string HomeTodoFilter { get; set; } = "all";

    /// <summary>翻译工作空间的【常用语言池】(可在设置里增删)。默认只有中/日/英/德/韩。</summary>
    public List<string> TranslationPool { get; set; } = Languages.DefaultPool.ToList();

    /// <summary>
    /// 使用者母语(语言码)。空 = 跟着界面语言走(见 NativeLang)。
    /// ★ 翻译的兜底级联要用它:目标池算不出目标时,第一顺位就是翻成母语。
    /// </summary>
    public string? NativeLangOverride { get; set; }

    /// <summary>实际生效的母语:显式设了就用设的,否则从界面语言推。</summary>
    public string NativeLang => string.IsNullOrWhiteSpace(NativeLangOverride)
        ? Languages.NativeFromUi(Language)
        : NativeLangOverride!;

    /// <summary>
    /// 界面音效(拖动卡片落地的闷响等)。默认开。★ 每台设备各自的偏好,不同步。
    /// 只管【声音】—— 落地扬尘属于暖萌皮肤的观感,不受它影响(关声音不该把动效也一起关掉)。
    /// </summary>
    public bool SoundEffects { get; set; } = true;

    // ---- 会话整理 / 记忆库(用户裁定 2026-07-30)----
    /// <summary>摘要触发方式:"ai" = AI 自己判断何时整理(默认);"manual" = 只在设置里手动点。</summary>
    public string SummaryTrigger { get; set; } = "ai";

    /// <summary>
    /// 会话整理阈值。★ 真正的约束是模型上下文窗口,但模型未接入(P4),先用【字符数估算】顶着,
    /// 接入后换成真 tokenizer 计数。用户可在设置里改。0 = 不按阈值提醒。
    /// </summary>
    public int SummaryThresholdChars { get; set; } = 120_000;

    /// <summary>记忆库自动清理:超过 X 天没被用到就清(0 = 关闭)。★ 置顶与 事实/偏好 类永不清。</summary>
    public int MemoryAutoCleanDays { get; set; }

    /// <summary>记忆库总量上限 MB,超了从最旧的开始清(0 = 不限)。</summary>
    public int MemoryMaxMB { get; set; }

    // ---- 「一键清爽」按钮执行哪些动作(用户勾选;危险项默认不勾)----
    public bool TidyClearCache { get; set; } = true;         // 安全
    public bool TidySummarize { get; set; } = true;          // 安全(只增不减);AI 未接入时不做事
    public bool TidyCleanMemory { get; set; }                 // 按上面的规则,先预演再删
    /// <summary>★ 不可逆:删除已归档的会话原文。默认【不勾】,勾了执行前仍要单独确认。</summary>
    public bool TidyDeleteArchivedOriginals { get; set; }

    /// <summary>
    /// 自动删除【超过 X 天】的已完成待办。0 = 不自动删除(默认,保守:不替用户丢东西)。
    /// 在"待办 › 已完成"抽屉里设置。
    /// </summary>
    public int TodoAutoPurgeDays { get; set; }

    // ---- 模型(在"系统 › 模型"里设置)。★ 这些是【偏好】:接入 GPU Broker(P4)后才真正装载模型。----
    /// <summary>各模型权重的统一存放目录(接入后中枢按此路径加载)。</summary>
    public string? ModelStorePath { get; set; }

    /// <summary>
    /// 示例同传记录已经播过种了(一次性标记)。
    /// ★ 为什么不用"列表里有没有同传会话"反推(审计 2026-07-31):
    ///   界面上的删除是【软删除】,而那个查询会滤掉已删除的 ——
    ///   于是用户删一次,下次启动它就长回来一条新的,回收站里还越攒越多。
    ///   "有没有"和"播没播过"是两回事,只有后者才能当播种判据。
    /// </summary>
    public bool InterpretDemoSeeded { get; set; }

    /// <summary>示例数据已清理过(一次性)。★ 停止播种后用它避免每次启动都重复扫一遍。</summary>
    public bool DemoDataPurged { get; set; }
    /// <summary>被【停用】的模型 key(存停用项 -> 新模型默认启用)。</summary>
    public List<string> DisabledModels { get; set; } = new();
    /// <summary>空闲时自动卸载模型腾显存。★ 今天仍**没有读取方**且界面上拨不动 ——
    /// 「空闲即卸」(D87②)已在中枢落地,但那个计时器是主机与副机**共享的一个**(D87⑧),
    /// 做成每台客户端各自的开关正是那条裁定要防的事。它要么变成主机上的中枢设置、
    /// 要么撤掉,在那件事被裁之前保持现状。见 Views/ModelsView.cs ③ 那段。</summary>
    public bool AutoUnloadIdle { get; set; } = true;
    // ★★★ 2026-08-06(D90 未决项④的处置):`AutoStartPreset`(连上中枢就自动装预设)
    //   **已删**。它与 D87 裁定①「不做开机预热」正面矛盾,而 D90 放行按需装载的
    //   全部依据就是 D87 —— 不能一边引用它、一边把与它相反的开关留在原地。
    //   ★ 按 D25 的做法:**写出来,不静默删**。旧档案里的这个键会被忽略,不报错。

    // ★★ 2026-08-04(P4-S9)删掉 IsModelEnabled / SetModelEnabled。
    //   它们存的是 ModelCatalog 那套自造 key(chat.8b…),而【没有任何代码再读它】——
    //   一个存得下、却谁也不看的偏好,就是本项目最恨的那种"假开关":
    //   用户拨了它,以为配置生效了,实际什么都没发生。
    //   ⇒ 「启用哪些组件」的权威现在在中枢(快照的 intended_resident),
    //     由 Views/ComponentPicker.cs 经 POST /v1/gpu/intended 变更。
    //   DisabledModels 字段本身留着只为**读回旧档案不报错**,不再有任何读取方。

    static readonly JsonSerializerOptions J = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsPath)) ?? new AppSettings();
        }
        catch { /* 配置损坏 -> 回到默认值,不让用户开不了应用 */ }
        return new AppSettings();
    }

    public void Save()
    {
        // ★ 包 try(审计 2026-07-31):设置里的滑条每拖一格就整文件重写一次,
        //   盘满/权限/被占时 File.WriteAllText 会抛 —— 而滑条回调里没有 catch,
        //   一次拖动就能把整个界面推倒。写不成就下次再写,不该霍掉 UI。
        try
        {
            AppPaths.EnsureStateDir();
            var tmp = AppPaths.SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, J));
            File.Move(tmp, AppPaths.SettingsPath, overwrite: true);   // 原子替换,防写一半断电留下坏文件
        }
        catch { /* 写盘失败不该抛到 UI 线程;内存里的值仍在,下次 Save 再试 */ }
    }
}
