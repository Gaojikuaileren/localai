// V21 -- 管理端这一侧的 GPU 面:**只走回环网关**。
//
// ══════════════════════════════════════════════════════════════════════════════
//  ★★★ 为什么它可以这么薄(而客户端那份 `HubGpu` 不行):
//
//  客户端要先回答「我该拨回环还是拨 LAN Edge」——那是 V13 整条修复的核心,
//  因为**同一个 exe** 既跑在主机上也跑在副机上。
//  管理端**只装在主机上**(副机上根本没有这个 exe),所以那个问题在这儿不存在:
//  它永远是回环。⇒ 少一层路由判断,也少一整套 mTLS 传输。
//
//  ★★ 而回环这条路正是 `change_resident` / `permitted_on_demand` **唯一**能走通的路:
//    网关按 OS 账户把回环调用判成 `trusted-local`;
//    经 lan-edge 的话会被注入证书指纹、档位封顶 `lan-device` ⇒ 403 denied_action
//    (实机报的那句「这台设备不能做这个操作」就是它)。
//    ⇒ 「组件/模型只能在主机上改」这件事,在新架构里是**结构性**的:
//      改它的那个界面只存在于一个**只装在主机上、且只会拨回环**的程序里。
//
//  ★★★ 解析**一行都没有重写**:目录、结果、快照全部走 `HubGpu.ParseCatalog` /
//    `HubGpu.ParseOutcome`(`GpuWire.cs`,两个 csproj 编同一份)。
//    在管理端另写一份解析器,就是 V4 修过一次的那个缺陷再来一遍
//    (`/admin/devices` 曾有两个解析器,服务端改一个键名只有一处会被发现)。
// ══════════════════════════════════════════════════════════════════════════════

using System.Net.Http;
using System.Text;
using System.Text.Json;
using LocalAI.Client.Services;

namespace LocalAI.Admin.Services;

public sealed class AdminGpu
{
    /// <summary>管理端进程里**只有这一个** —— 两份会各拿各的目录,而"哪份是权威"就没有答案了。</summary>
    public static readonly AdminGpu Instance = new();

    // ★ UseProxy=false:与 `HubClient.LoopHttp` 逐字对齐。配了系统代理的机器上,
    //   一个走代理一个不走会让「探到了网关」与「改得动组件」一真一假。
    static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>回环网关的地址。★ 端口读 <see cref="HostSetup.GatewayPort"/> —— 与客户端拨号、
    /// 与 <see cref="HostProvision"/> 起网关**同一个数**(那个文件两个 csproj 编同一份)。</summary>
    static string Base => $"http://127.0.0.1:{HostSetup.GatewayPort}";

    /// <summary>上一次失败的原因;null = 没失败过。★ 取不到就说取不到,**不拿旧值冒充**。</summary>
    public string? LastError { get; private set; }

    /// <summary>上一次成功取到的组件目录;null = 还没取到过。</summary>
    public GpuCatalog? LastCatalog { get; private set; }

    /// <summary>
    /// 中枢 GPU 快照。★★ 管理端**恒为 null**,而且这是**有意的、如实的**:
    /// 客户端那份靠一条 SSE 长连接维持(`HubGpu.Start()`),管理端没有起那条流。
    ///
    /// <para>★ 唯一的消费者是「点确定时用哪个世代号」,而那里本来就写着
    /// <c>Snapshot?.Generation ?? _catalog.Generation</c> —— 退到目录的世代号。
    /// 而目录是**点确定前刚取的**,所以这条路不但成立,还更准。</para>
    /// <para>★★ 不假装有一份快照:编一个出来会让面板显示一个**没人在更新**的显存数字,
    /// 而人会照着它决定要不要勾。宁可这一格不存在。</para>
    /// </summary>
    public HubGpuSnapshot? Snapshot => null;

    /// <summary>取组件目录。★ 目录由中枢下发 —— 这一侧**不得**自己维护一份清单。</summary>
    public async Task<GpuCatalog?> FetchCatalogAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await Http.GetAsync(Base + "/v1/gpu/components", ct);
            var body = await r.Content.ReadAsStringAsync(ct);
            if (!r.IsSuccessStatusCode)
            {
                LastError = $"取组件目录失败({(int)r.StatusCode})";
                return null;
            }
            var cat = HubGpu.ParseCatalog(body);
            // ★ 只有解析成功才更新缓存:半份/读不懂的目录**不能**盖掉上一份好的,
            //   但也不能让它冒充新的 —— ParseCatalog 解析失败返回 null,这里就不动缓存。
            if (cat is null) { LastError = "组件目录读不懂(形状与登记的契约对不上)"; return null; }
            LastError = null;
            LastCatalog = cat;
            return cat;
        }
        catch (Exception ex)
        {
            // ★ 说清它是**哪一种**够不着:网关没起 ≠ 网关拒绝了。
            LastError = $"连不上本机回环网关 {Base} —— 多半是栈还没起来"
                      + "(到「主机中枢」那一页看它起到哪一步了)。原因:" + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 提交一次驻留集合变更。★ 必带 if_generation —— 挑组件要几十秒,期间桌面会变,
    /// 「预览过、确定时不过」是**必然**会发生的,世代号是两边唯一能对上账的东西。
    /// </summary>
    /// <param name="permittedOnDemand">
    /// 「允许按需装载」的授权集合。★★ <c>null</c> 与空数组**不是一回事**:
    /// null = 这次不动授权;空数组 = 撤销全部授权。合并的话,任何一次普通变更
    /// 都会**静默清空**用户的按需授权(服务端那半边同款判据)。
    /// </param>
    public async Task<ApplyOutcome> ApplyAsync(IReadOnlyList<string> ids, long ifGeneration,
                                               bool interruptRunning,
                                               IReadOnlyList<string>? permittedOnDemand = null,
                                               CancellationToken ct = default)
    {
        // ★ 省略 ≠ 空集合 —— 所以这里也**分两个载荷**,而不是给一个默认值。
        object payload = permittedOnDemand is null
            ? new { if_generation = ifGeneration, components = ids, interrupt_running = interruptRunning }
            : new { if_generation = ifGeneration, components = ids, interrupt_running = interruptRunning,
                    permitted_on_demand = permittedOnDemand };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/v1/gpu/intended")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            using var r = await Http.SendAsync(req, ct);
            var body = await r.Content.ReadAsStringAsync(ct);
            // ★ 解析走**唯一**那一处(GpuWire.cs 的 partial HubGpu),不在这儿另写一遍。
            return HubGpu.ParseOutcome((int)r.StatusCode, body);
        }
        catch (Exception ex)
        {
            // ★★ 送不出去**绝不当成功**。而且要与"送到了、中枢拒绝了"分开说 ——
            //   两者的下一步完全不同(一个是去起栈,一个是看拒绝理由)。
            return new ApplyOutcome(false, "gateway_unreachable",
                $"没能把这次变更送到本机回环网关 {Base} —— 栈多半还没起来。原因:" + ex.Message,
                "", Array.Empty<BlockingLease>(), 0);
        }
    }
}
