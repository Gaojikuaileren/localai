# 待决 6(Agent Worker 隔离路线)+ 待决 7(MCP 端点传输)· 决议包 **D?**

> 日期:2026-08-05
> 性质:**可行性核实 + 裁定建议**。未改任何生产代码。
> 产出物:本文件 + 一次性勘察 spike `90-ops/spikes/appcontainer-loopback/`
>
> **取号**:按 D75 办 —— 取号时 `DECISIONS.md` 已提交的最大号是 **D88**。
> 本包**不占号**,标题写 `D?`;并入者按当时的 git 历史重新取号。
>
> **并发提示**(D75 ②):写作时 `main` 工作树有另一路未提交改动 ——
> `10-core/gateway/gateway.py` · `gpu_broker.py` · `test_gpu_broker.py` ·
> `20-client-win/app/Services/HubGpu.cs`。本包**未改**其中任何一个,也未改
> `DECISIONS.md` / `PROJECT_PLAN_v3.0.md` / `STATE.md` / `HERMES_INTEGRATION_DESIGN.md`。
> 本包只**新建**文件。

---

## 0. 一句话

**AppContainer 这个机制真的挡住了 DD-7 要挡的东西 —— 挡得比设计估计的还宽(文件侧白拿),
而且被关起来的进程自己拆不开它。但那把「回环豁免」的钥匙,实测【不需要管理员组】就能转动,
而且它没有端口粒度 —— 于是它同时给出了待决 7 的答案:MCP 只能走管道,走 HTTP 在结构上自毁。**

⇒ 建议 **C(AppContainer)+ 命名管道**,并**顺手做 B**;A(容器化)降为备选。

**两条必须和结论一起读的限定(否则会把 C 当成比它更强的东西):**

- ★ **我测的是机制,不是 Hermes 本体** —— 「AppContainer 挡得住回环」已证,
  「Hermes 能在里面正常跑」**未证**(§6.1)。这是最大的未测项。
- ★ **那道闸只覆盖 TCP。** 实测 **AppContainer 完全不拦 AF_UNIX**,管道也只是靠 DACL 拒 ——
  TCP 之外的完整性**由文件/对象 ACL 决定,不由网络隔离决定**(§2.5 ③)。
  DD-7 那句「worker 的网络能力 = 一条连接,别无其它」**不会自动成立**,要另配断言(§4.2 ⑥)。

---

## 1. ★ 先交代我自己的上下文 —— 以及一条我自己造成的副作用

### 1.1 我是提权跑的 —— 而这台机器上**只有**提权

```
launcher: HONGKONGPINGPON\Zori Ma
完整性等级 : High Mandatory Level (S-1-16-12288)
Administrators : enabled(不是「只用于拒绝」)
UAC : EnableLUA = 0  ← 彻底关闭
OS : Windows 11 Pro 25H2 · build 26200.8875
```

按 D46 的教训,**提权上下文里测出来的「能/不能」不能直接当普通用户的结论**。
所以本包对每一格都标了它是在哪个 token 上测的。

★ **2026-08-06 更新**:用户双击复测回来的**仍然是 High + Administrators enabled** ——
根因是 `EnableLUA=0`(本机 UAC 彻底关闭),该账户**不存在**分裂 token。
⇒ 「留给用户双击复测」这条路**在本机走不通,而这本身就是答案**。详见 §2.2b。

★ 一条**反直觉但重要**的观测:AppContainer 子进程的 token 里
`Administrators` 仍然是 **enabled**(继承自我这个提权父进程),
**而文件侧照样全拒**(§2.4)。说明 AppContainer 的第二道检查
(对象 DACL 必须显式授权某个 app-package SID)**压过**了 Administrators 成员身份。
—— 但生产上仍然必须用非提权的父进程去起 worker,不要靠这一条兜。

### 1.2 ★ 我误改了一次机器状态,已复原

排障阶段我为了确认一个 .cmd 能不能跑,在**自己的提权上下文里**直接执行了它,
而那个 .cmd 里含 `CheckNetIsolation LoopbackExempt -a` ——
**于是我以管理员身份真的加了一条回环豁免**,而这正是我事先声明「交给用户双击、不自己跑」的那类操作。

- 影响面:仅限勘察用的 `LocalAI.Spike.NoCaps` 容器,不涉及任何真实应用;
- 已复原:进场时豁免列表**本来是空的**(实测,`-s` 只列出我加的那一条),
  收尾用 `-d` 删单条成功,复核 `-s` 为空;`AppIso` 下的探针值已删;
  两个勘察 profile 已删;`C:\Windows\Temp` 的临时件已清;计划任务已删;
  `%LOCALAPPDATA%\Packages` 无残留。
- 附带后果:它让「受限 token 下 `-a` 到底有没有生效」这一测**一度读不出来**
  (列表里本来就有了),后面重做了干净的一轮才拿到 §2.2 那个数。

**记在这里是因为**:这条纪律不是形式的 —— 它一旦破一次,当轮的实测值就会被污染。

### 1.3 第二处副作用:四条防火墙规则(也已清掉)

勘察程序为了测「换成本机 LAN IP 能不能绕过回环隔离」,在 `0.0.0.0` 上绑过监听 ——
Windows 因此弹了「是否要允许公共网络和专用网络访问此应用?AcSpike」。
**用户点了「取消」,而「取消」会写四条 `acspike.exe` 的 Inbound **Block** 规则(Private profile)。**

- 已用 `Remove-NetFirewallRule` 全部删除,复核规则数 = 0;
- **已消掉根源**:`0.0.0.0` 绑定改成**默认关**,要测才加 `--lan`。
  理由不只是弹窗烦人 —— **不管点「允许」还是「取消」,都会留下一条持久规则**,
  那是一支勘察程序不该留下的机器状态。
- 判据不受影响:LAN IP 那一格的结论(容器挂住 / 对照组 connected)是在弹窗之前测到的,
  而且**本机到本机的流量不过防火墙**,放行与否都不改变它。

### 1.4 第三处:9 个删不掉的 socket 文件(**没能清掉,如实交账**)

AF_UNIX 那一组测试在机主 `%TEMP%` 下留下了 **9 个 0 字节的孤立 ReparsePoint**:

```
%TEMP%\s.sock · u2.sock · u_usertemp.sock
%TEMP%\localai-acspike\out\spike-{16196,32368,32720,5228,8272}.sock · spike.sock
```

`File.Delete` / `cmd del` / `rm -f` / 改名 / `fsutil reparsepoint delete` **全部无效**;
`fsutil reparsepoint query` 报 `Error 1920:文件无法被系统访问`。
⇒ **这是我没能清掉的残留。** 影响评估:0 字节、位于临时目录、不被任何东西引用;
真实代价是**那几个路径今后不能再用作 AF_UNIX 落点**(bind 会 `10022`)。
勘察程序已改成每轮换新文件名,不会继续累积;
但**已经产生的这 9 个,除重启/系统清理临时目录外我没有办法删掉**。

---

## 2. 实测值(每格是实测值或「未测」,不填推断)

环境:Windows 11 Pro 25H2 build 26200.8875 · .NET 9.0.315 ·
`llama-server` version **10107 (c0bc8591e)**。
勘察程序:`90-ops/spikes/appcontainer-loopback/`(投两个 AppContainer:
一个零 capability、一个带 `internetClient`+`privateNetworkClientServer`)。

### 2.1 ★ 第一问:AppContainer 进程连 `127.0.0.1:18081` 是不是真被拒?错误码?

**★ 这一格测了两遍,两遍都在 18081 上:**

1. 第一遍:`llama-server` 当时没跑、端口空闲,勘察程序**自己绑了 18081** 当靶子;
2. 第二遍(2026-08-06 00:0x):**真的 `llama-server` 正在 18081 上跑**(PID 32376),
   勘察程序**不绑监听、直接打真家伙**(`LOCALAI_TARGET_PORT=18081`)。
   结果与第一遍**逐格一致**;并且复核了 `llama-server` 跑完仍在监听,没被搞挂。

⇒ 「用替身监听测的」这条保留意见**已消除**。下表的数取自这两遍(一致)。

| 从哪连 | 目标 | 结果 | 错误码 | 耗时 |
|---|---|---|---|---|
| 对照组(不进容器) | `127.0.0.1:18081`(有人听) | **connected** | — | 0–8 ms |
| AppContainer 零 capability | `127.0.0.1:18081`(有人听) | **失败** | **WSAETIMEDOUT `10060`** | **21 039 ms** |
| AppContainer 带网络 capability | `127.0.0.1:18081`(有人听) | **失败** | **WSAETIMEDOUT `10060`** | **21 034 ms** |
| AppContainer(任一) | `127.0.0.1:28281`(**没人听**) | 失败 | **WSAECONNREFUSED `10061`** | 2 032 ms |
| 对照组 | `127.0.0.1:28281`(没人听) | 失败 | WSAECONNREFUSED `10061` | 2 032 ms |
| AppContainer(任一) | `192.168.178.61:28181`(**本机 LAN IP**,监听绑 `0.0.0.0`) | **失败(挂住)** | 4 秒档超时 | — |
| 对照组 | 同上 | **connected** | — | 0 ms |

> LAN IP 这两行需要在 `0.0.0.0` 上绑监听,而那会弹防火墙授权框并留下持久规则(§1.3)。
> ⇒ 勘察程序已把它改成**默认关**,要复测得显式加 `--lan`。上面两行是已测到的值。

**四条结论:**

1. **是真的被拒,但形状是【静默丢包】,不是 access denied。**
   SYN 被 WFP 丢掉,调用方**挂住**,直到 Windows 自己的 TCP 连接超时才拿到 `10060`。
2. **能和「服务没起」分开**:被拒 = `10060` @ ~21 秒;没人听 = `10061` @ ~2 秒。
   ⇒ 判据可写成:**`10061` 才是「后端没起」;`10060`/挂住 = 「被隔离挡住」**,
   两者**不得**合并成一句「连不上」。
3. ★ **最坏的诊断形状**:默认它看起来是「慢」,不是「被禁」。
   任何客户端都必须设**短连接超时**(建议 ≤2 s)+ 把 `10060` 单独归类,
   否则界面上会显示成「worker 卡住了」,而值班的人第一反应是去加长超时 —— 那是反方向。
4. **换成本机 LAN IP 绕不过去**(去本机自己 IP 的流量同样按回环处理),
   **capability 也不影响回环**(带 `internetClient`+`privateNetworkClientServer` 的容器结果一模一样)。
   ⇒ 回环这道闸与 capability 是两条正交的东西,给网络 capability **不会**顺带放开回环。

**反方向也挡**(这一条对待决 7 是决定性的):

| | bind `127.0.0.1` | 宿主(非容器)连进来 | 容器收到连接 |
|---|---|---|---|
| 对照组 | ok | **connected** | **true** |
| AppContainer 零 capability | **ok** | **timeout** | **false** |
| AppContainer 带 capability | **ok** | **timeout** | **false** |

⇒ 容器里**能** bind 回环(bind 不报错),但**没有人连得进来**。
「bind 成功」是个假信号 —— 又一个「看着在工作、实际没有」。

**逃逸测试(对会拉工具子进程的 Hermes 是关键):**

容器内的进程再 `Process.Start` 一个子进程 ⇒ 孙进程
`isAppContainer=true`、**AppContainer SID 与父完全相同**、回环同样是 `10060`。
⇒ **容器是继承的,拉子进程逃不出去。**
(★ 这一条我第一版差点报错:孙进程的结果文件被读到了**上一轮的旧快照**
——因为它没写完而旧文件还在。已在 spike 里改成「先删再起、超时不采信」。
live 的管道服务端日志与修好后的重跑互相印证。)

### 2.2 ★★ 第二问:回环豁免,谁能打开?(承重的一格)

**工具的参数表(本机 `CheckNetIsolation LoopbackExempt -?` 实测输出):**

```
操作: -a 添加 / -d 删除 / -c 清空 / -s 显示 / -is 允许入站
参数: -n=<AppContainer 名或包族名>   -p=<SID>   -?
```

⇒ **没有端口参数,没有地址参数。** 豁免的粒度是「**哪个容器**」,不是「哪个端口」。

**同一轮里的 A/B 实测(最硬的粒度证据):**

| 容器 | 在豁免列表内? | 连 `127.0.0.1:18081` |
|---|---|---|
| `LocalAI.Spike.NoCaps` | **是** | **connected** |
| `LocalAI.Spike.Caps` | 否 | timeout |

同一次运行、同一个监听、同一台机器。
⇒ **为了让 worker 能连网关而开的豁免,会把 18081 一起放开。** 这是待决 7 的判决依据。

**谁能打开(三格实测 + 一格未测):**

| 调用者的 token | `LoopbackExempt -a` | 判定方式 |
|---|---|---|
| 提权 Administrator(High + Administrators **enabled**) | **成功** | 加完读列表,条目出现 |
| **Administrators = 只用于拒绝**(deny-only)+ High 完整性<br>(`runas /trustlevel:0x20000`) | **★ 成功** | **加完从管理员上下文读列表,条目确实出现** —— 看效果,不看退出码 |
| **容器里的进程本人**(Low 完整性 + AppContainer SID) | **★ 被拒** | `CheckNetIsolation` **exit=5**,原文「拒绝访问。请以管理员身份运行该命令」;绕过工具直接 `reg add` → **exit=1**「拒绝访问」 |
| **机主自己双击**(2026-08-06 用户实跑) | **★ 成功** | 双击回来的仍是 **High + Administrators enabled**;加完读列表,条目出现 |
| ~~普通用户双击态(Medium + Administrators 只用于拒绝)~~ | **本机不存在这个上下文** | 见 §2.2b |

**为什么最后一格未测(如实记账):**
我这个进程是提权的,而三种降权手段**都没能造出 Medium 完整性的可用进程**:

1. `runas /trustlevel:0x20000` → 拿到的是 **High** 完整性 + Administrators 只用于拒绝
   (SAFER Basic User 只过滤组,不降完整性);且它**禁止执行 .cmd 脚本**,
   一开始让我误判成「PowerShell 起不来」;
2. `explorer.exe <cmd>` 交接 → 本机**没有**交接给非提权 shell,起出来仍是 High + Administrators enabled;
3. `schtasks /rl LIMITED /it` → 同样是 High + Administrators enabled;
4. 自己写的 `CreateRestrictedToken` + `DuplicateTokenEx` + 降完整性到 Medium →
   **token 造成功了**(降 Medium 确认成功),但 `CreateProcessWithTokenW` 起出来的进程
   一律 `0xC0000142`(STATUS_DLL_INIT_FAILED);补 `lpDesktop=winsta0\default`、
   换 `CREATE_NEW_CONSOLE`、去掉 `DISABLE_MAX_PRIVILEGE`(它会连
   `SeChangeNotifyPrivilege` 一起剥掉,那本身就足以让 DLL 加载失败)都一样;
5. 换 `CreateProcessAsUserW`(文档:token 是调用方主令牌的受限版本时**不需要**
   `SeAssignPrimaryTokenPrivilege`)→ **进程创建成功了**,但**仍然** `0xC0000142`。
   ⇒ 说明卡点不在 API,而在**窗口站/桌面的 DACL 没有授权给这个受限 token**。
   MSDN 的解法是把 logon SID 加进 `winsta0` 与 `default` 的 DACL —— 那是**改会话级
   系统对象的 ACL**,为了一次测量去动它不合适,**故止步**。

### 2.2b ★★ 用户双击回来了:**这台机器上根本没有「普通用户」上下文**(2026-08-06 结案)

用户双击 `1-普通用户双击.cmd`,回来的是:

```
身份         : HONGKONGPINGPON\Zori Ma
完整性等级   : High   (S-1-16-12288)
Administrators: enabled
UAC(EnableLUA): 0
【写】LoopbackExempt -a  exit=0  输出="完成。"   → 列表里条目出现
```

**根因不是「跑法不对」,是 `EnableLUA = 0` —— 本机 UAC 彻底关闭。**
`Zori Ma` 在 Administrators 组里,UAC 一关就没有分裂 token,
⇒ **该账户启动的每一个进程都是 High + Administrators enabled**,双击也一样。
用户原话:「我的账号身份就是管理员身份,没办法用普通身份跑。」

**这同时解释了我那五种降权手段为什么全失败** —— 机器上没有过滤 token 可拿,
不是我 API 用错了。(第 5 条 `CreateProcessAsUserW` 卡在窗口站 DACL 是另一层,但方向一致。)

★ **本条不是新发现,仓库早就记过**,我差点重复报一个已修的问题:
- `10-core/lan-edge/Program.cs:205-213` 的注释明写「在 UAC 关闭的机器上(EnableLUA=0)
  『我是不是管理员』对管理员账户**恒为真**……拿代理指标当门槛,等于把一台完全健康的机器
  判成不能用,而且给出的理由是假的」;护栏已改成**直接试着打开 CA 私钥**,打得开就放行;
- `decision-packets/integrity-guard-asks-wrong-question-2026-08-03.md` §表格已记
  `EnableLUA = 0 —— UAC 彻底关闭`;`identity-elevation-guard-2026-08-03.md:12` 同。
⇒ **D46 没有被这条配置打穿**,不需要为它开新工。

**✅ 记账缺口已补(2026-08-06,用户指示后办)**:
`EnableLUA=0` 此前只活在两份决议包里,**没有进 `STATE.md` 的「环境事实(变更即更新)」表** ——
它已经咬过两次(身份护栏那轮、本轮),照 MEMORY 里「同一陷阱踩满三次就写进 PITFALLS 并配护栏」
的纪律,不该等第三次。

**已按用户明确指示补进 `STATE.md` 环境事实表**(★ UAC 一行,列在「剪贴板」之前),
并按 D75 ② 的纪律执行:

- **外科式编辑**,`git diff --numstat` = **`1  0`**(只加一行、零删除),不是整体覆盖;
- **动之前重读**了那一段,不吃几分钟前的印象;
- **查过并发**:`STATE.md` 自本车道分叉后**无人改动**、main 工作树里也**无未提交改动**;
- **查过重叠**:另一路交回的 `A3-state-worklog-draft-2026-08-06.md` 动的是
  顶部基线 / P4 阶段行 / 客户端自检 / worklog / 「下一步」/ 欠账 —— **没碰环境事实表**,不冲突。

★ **一处口径不一致,留给协调层知道**:同期另一路的做法是
「**不自己动中央三文档**,交回草稿由协调层并入」(见该草稿 §开头)。
本条是**用户显式指示直接补**,故走了直接编辑;两种模式此刻并存。
若协调层希望统一成「一律走草稿」,把本行回退成草稿即可 —— 它是独立的一行,不牵连任何其它段落。

**结论怎么改口径:**

原来那一格「普通用户能不能自己开豁免」**在本机不成立为一个问题**。拆成两问:

| 问 | 答 |
|---|---|
| **(a) 这台机器上,机主能不能无摩擦地打开豁免?** | **能。实测。** 不需要任何提权步骤、不弹任何框 —— 因为一切进程本来就是 High |
| **(b) 在 UAC 开启的机器上,Medium 进程能不能打开?** | **仍然未测,且本机测不到**(要 `EnableLUA=1` + 重启,是机器级安全配置变更,我不做、也不建议为一次测量去做)。**这一问只对产品化重要**,对本机的裁定不重要 |

⇒ **对本机的裁定而言,这一格已经结案,而且答案比初版更硬**:
路线 C 的隔离**对 worker 成立**(worker 自己 exit 5),
**对机主完全不成立,且连一次 UAC 提示都没有**。
⇒ §4.2 的断言 ① 因此从「建议」升为**硬前置**(见该节)。

★ 附带修掉勘察程序两个真问题(用户那一贴里能直接看到):
① 它在 High 上跑却打印「**普通用户**【可以】…」——**那是假陈述**。已改成读 `EnableLUA`、
   按上下文措辞,并在 UAC 关闭时明说「这就是本机的最终答案,不是测法不对」;
② 两处乱码:`.cmd` 是 GBK 而 AcSpike 把控制台切成 UTF-8 ⇒ 第二段中文全乱
   (已把 `.cmd` 改成 UTF-8 无 BOM + `chcp 65001`);
   系统工具的本地化输出被当 UTF-8 读 ⇒ 「完成。」读成「`��ɡ�`」
   (已按 `GetOEMCP()` 设 `StandardOutputEncoding`)。

★ **一条要撤回的证据链**:我一开始把
`HKLM\...\FirewallPolicy\RestrictedServices\AppIso\FirewallRules` 的 ACL
(`BUILTIN\Users` 只有 `ReadKey`)当成「普通用户写不进去」的结构性证据。
**这条不成立** —— 我按 SID 在整个 `FirewallPolicy` 子树里搜过,
**豁免条目并不存在于那个键里**,那个键不是豁免的存储处。
⇒ 该 ACL 与本题无关,已从判据里剔除。(留在这里是为了别人不再重走这条错路。)

### 2.3 ★ 第三问:命名管道能不能回连宿主?D73 的双校验还成不成立?

**管道本身:成立,而且形状很好。**

| 管道 | 从对照组连 | 从 AppContainer 连 |
|---|---|---|
| 默认 DACL | **roundtrip-ok** | **失败 · `ERROR_ACCESS_DENIED (5)`** |
| SDDL 里显式加一条 AppContainer SID 的 ACE | roundtrip-ok | **roundtrip-ok**(写入 + 读回都通) |

⇒ **默认拒绝 + 显式授权**。这正是本项目想要的形状:
要让 worker 用管道,必须在 DACL 里**写出来**;漏写就是连不上,不是悄悄放行。

**★★ 但 D73 的「父子 PID + SID 双校验」在这个边界下有两处不成立:**

管道服务端 `GetNamedPipeClientProcessId` + `ImpersonateNamedPipeClient` 实测拿到的:

```json
{"clientPid":27964, "clientParentPid":7572, "clientIsDirectChildOfMe":true,
 "clientUserSid":"S-1-5-21-…-1001", "clientUserName":"HONGKONGPINGPON\\Zori Ma",
 "clientIsAppContainer":true,
 "clientAppContainerSid":"S-1-15-2-957109880-…-2987043793",
 "clientIntegrity":"Low"}
```

**① SID 校验分不出容器里的 worker 和机主随便开的一个进程。**
容器进程的 `clientUserSid` **就是机主本人的 SID**,`clientUserName` 就是机主账户名 ——
与任何一个同用户的普通进程**逐字节相同**。
⇒ 「校验对端 SID」这一句在 AppContainer 世界里**不是**一个身份判据。
真正能分开的是另外两样,而它们**不在 D73 的表述里**:
- `GetTokenInformation(TokenIsAppContainer)` → `true`
- `GetTokenInformation(TokenAppContainerSid)` → **必须等于我们自己建的那个容器 SID**
- (附带信号)完整性 = **Low**

⇒ **D73 的双校验要补成三元**:父子关系 + 对端**用户** SID + 对端**AppContainer** SID。
只做前两项 ⇒ 「看着有校验、实际谁都能连」。

**② 严格的「必须是我的直接子进程」会拒掉合法的工具子进程。**
实测:worker 的**孙**进程连进来时
`clientParentPid` 指向 worker(不是宿主),`clientIsDirectChildOfMe=false`。
而 Hermes 的 loop **会**拉工具子进程。
⇒ 要么放宽成「任意后代」(得走进程树,且有 **PID 复用**风险),
要么**改判据**:不认父子,认 **AppContainer SID** —— 它对整棵子树天然相同(§2.1 逃逸测试已证),
**比父子关系更稳、更便宜、也更贴合这道边界**。
建议后者,并把「父子」降为辅助信号。

### 2.4 附带实测:文件侧比设计估计的**宽**

容器内尝试列目录 / 读文件的结果(`LOCALAI_PROBE` 传进去的真实路径,代码里不写死盘符):

| 目标 | 对照组 | AppContainer |
|---|---|---|
| 机主 profile 根 | dir-listed | **denied**(`UnauthorizedAccessException`) |
| 机主 Documents | dir-listed | **denied** |
| `%LOCALAPPDATA%` | dir-listed | **denied** |
| `%LOCALAPPDATA%\LocalAI` | dir-listed | **denied** |
| `{state}` | dir-listed | **denied** |
| `{state}\secrets` | dir-listed | **denied** |
| `{state}\memory` | dir-listed | **denied** |
| `{models}` | dir-listed | **denied** |
| `{code}` 仓库根 | dir-listed | **denied** |
| `D:` 盘根 | dir-listed | **denied** |
| `System32`(对照,应可读) | dir-listed | **dir-listed**(`ALL APPLICATION PACKAGES` 有 RX) |

⇒ 设计文档 §6 路线 C 的代价栏写「**它只隔离网络与部分对象命名空间,文件侧仍需 ACL 配合**」——
**这句过重了**。实测是**默认全不可见**;需要 ACL 的方向**是反过来的**:
把**该给**的目录(只读输入 / 可写 scratch)**显式授权**给容器 SID。
这与 DD-7 那句「不可见:记忆库 · `{state}/secrets` · `config/*.toml` · `{code}/00-docs`」
**方向一致,而且是白拿的**,不必逐条配 Deny ACE。

★ **实现陷阱(我自己踩过,必须写下来)**:
`Directory.Exists()` 在**拒绝访问**时也返回 `false`。
我第一版据此把 `{state}` 记成了「not-found」,而它其实是「denied」。
⇒ 任何用 `Exists()` 判断可达性的护栏,都会把「被挡住」读成「不存在」——
**这正是本项目固定审查视角里那种缺陷**。判据必须**真开一次、看异常类型**。

### 2.5 ★★ AF_UNIX:**AppContainer 根本不挡它** —— 以及一处我先前写错、现已更正的结论

> **本节是 2026-08-06 补测后重写的。** 初版把 AF_UNIX 记成「未测 + 不适合长期常驻」,
> 那两句都不准:未测的理由写错了(服务端 bind 是成功的,失败在**客户端 connect**),
> 而「不适合」是从一个**只在特定目录下成立**的现象过度推广出来的。
> 更正如下,并保留错处以免别人重走。

**① 落点矩阵(把「路径长度」和「路径含空格」两个变量分开测):**

| 路径 | 长度 | 含空格 | bind | connect | socket 文件事后能删掉吗 |
|---|---|---|---|---|---|
| `C:\Windows\Temp\u_short.sock` | 28 | 否 | ok | **ok** | **能** |
| `C:\Windows\Temp\<40×x>.sock` | 61 | 否 | ok | **ok** | **能** |
| `C:\Windows\Temp\a b\u.sock` | 26 | **是** | ok | **ok** | **能** |
| `C:\Windows\Temp\a b\<40×y>.sock` | 65 | **是** | ok | **ok** | **能** |
| `{cache}\tmp\u_probe.sock` | 28 | 否 | ok | **ok** | **能** |
| `{state}\u_probe.sock` | 24 | 否 | ok | **ok** | **能** |
| `{code}` 仓库根 `\u_probe.sock` | 48 | 否 | ok | **ok** | **能** |
| **机主 `%TEMP%`** 下任意 `.sock` | 43–59 | 是 | ok | **失败 `WSAEINVAL 10022`** | **不能** |

⇒ **长度不是变量,空格不是变量。** AF_UNIX 在本机是**好用的**:
`{state}` / `{cache}\tmp` / `{code}` / `C:\Windows\Temp` 全都 bind + connect + 收发 + 干净删除。
**唯一失败的是机主的 `%LOCALAPPDATA%\Temp`**(原因未能归因,只记现象)。

★ 我初版那句「AF_UNIX 不适合做长期常驻通道」**作废** —— 它是把
「机主 `%TEMP%` 下失败」当成了普遍性质。**在项目自己的五个根下它都是好的。**

**② 但失败会留下真正删不掉的东西**(这一条成立,且只在失败的那个落点上出现):

connect 失败后,socket 文件成为**孤立 ReparsePoint**:
`File.Delete` / `cmd del` / `rm -f` / 改名 / `fsutil reparsepoint delete` **全部无效**,
`fsutil reparsepoint query` 报 **`Error 1920:文件无法被系统访问`**。
⇒ 该路径此后连 **bind 都会** `10022` —— 「上一轮的残留让这一轮静默失败」。
**要用 AF_UNIX,起停必须处理残留,并且落点要先实测过。**

**③ ★★ 最重要的一格(初版未测,现已测到):容器能不能连宿主的 AF_UNIX —— 能。**

把 socket 放到实测可用的落点(`{cache}\tmp` 下一个我显式授权给容器 SID 的目录)之后:

| 从哪连 | AF_UNIX 到宿主 |
|---|---|
| 对照组(不进容器) | **roundtrip-ok** |
| AppContainer 零 capability | **roundtrip-ok** |
| AppContainer 带网络 capability | **roundtrip-ok** |

⇒ **AppContainer 的回环隔离只覆盖 TCP,不覆盖 AF_UNIX。**
换句话说:**这条边界的完整性,在 TCP 之外靠的是文件 ACL,不是网络隔离。**
命名管道(§2.3)也是同一个道理 —— 默认拒是因为 **DACL**,不是因为网络闸。

★ 这一条同时是**好消息和坏消息**:
- 好:待决 7 有**两条**可用传输(命名管道、AF_UNIX),都是「默认拒绝 + 显式授权」;
- 坏:**它是 DD-7「worker 的网络能力 = 一条连接,别无其它」的一个真实缺口** ——
  只要宿主上有任何一个 AF_UNIX 监听、且其路径的 ACL 够得着,容器就连得上,
  **回环那道闸一点都拦不住**。⇒ 见 §4.2 新增的第 6 条强制点。

> 附:本轮这一跑的 TCP 行**不作证据** —— 跑的时候 `llama-server` 已经停了,
> 对照组也是 `10061`。TCP 那三个数取自 §2.1 的两遍(那两遍 18081 上确有监听)。

### 2.6 附带实测:`llama-server` 自己支持的两样东西

`llama-server.exe --help`(version **10107 / c0bc8591e**)原文:

```
--host HOST         ip address to listen, or bind to an UNIX socket if the address ends
                    with .sock (default: 127.0.0.1)        (env: LLAMA_ARG_HOST)
--api-key KEY       API key to use for authentication, multiple keys can be provided as a
                    comma-separated list (default: none)   (env: LLAMA_API_KEY)
--api-key-file FNAME  path to file containing API keys, one per line; lines starting with a
                    hash are treated as comments (default: none)
                                                           (env: LLAMA_ARG_API_KEY_FILE)
```

两条都是**实测的 CLI 能力**,不是文档推断:

1. `--api-key-file` ⇒ 路线 B 不必把密钥塞进命令行(命令行会出现在 `Get-Process`/WMI 里),
   可以落 `{state}\secrets`(现有强 ACL,且已排除出备份);
2. ★ **`--host` 支持 `.sock` = 可以完全不监听 TCP** ⇒ 见 §5.3 的 **B′**。

---

## 3. 三条路线的判据表

「未测」就是未测,不填推断。

| 判据 | **A · 容器化(WSL2/Hyper-V)** | **B · 给 `llama-server` 加鉴权** | **C · AppContainer** |
|---|---|---|---|
| 挡住 worker 直连 18081 | **未测**(启用 WSL2 需管理员,按 D46 纪律不自跑) | 不挡「连得上」,挡「用得了」:无 key 则 401 —— **未测**(没起过带 key 的实例) | **实测挡住**:`10060` @21 s;LAN IP 别名同样挡;capability 无关 |
| 与「服务没起」可区分 | 未测 | 未测(应为 401 vs 连不上,形状清楚) | **实测可区分**:`10060`/挂住 vs `10061` |
| 拉子进程能否逃逸 | 未测 | 不适用 | **实测不能**:孙进程 AppContainer SID 与父相同 |
| 文件侧 | 未测(容器天然分离,但挂载点要自己配) | **不改善**(与文件侧无关) | **实测默认全不可见**({state}/secrets/memory/models/code/D: 全 denied) |
| 需不需要提权 | **要**(启用 WSL2/Hyper-V:管理员 + 可能重启)—— DD-16 要消掉的正是这一步 | **要一次**(改 `start-stack.ps1` 与后端启动参数;运维动作,非用户动作) | **建容器不需要**(`CreateAppContainerProfile` 实测在普通流程里成功) |
| 命名管道能否跨边界(待决 7) | **未测**;按机制推断跨 VM 边界不通,要 vsock/TCP ⇒ **与 D73「全链路无 HTTP」冲突** | 不适用(worker 还在本机,管道自然) | **实测通**:默认 DACL 拒(`5`),显式授权 AppContainer SID 后 roundtrip-ok |
| **非 TCP 的本机 IPC(管道 / AF_UNIX)** | 未测 | 不适用 | **实测都能跨边界**,且**不受回环闸约束** —— 管道靠 DACL 拒、AF_UNIX **完全不被拦**(§2.5 ③)⇒ TCP 之外的完整性只由文件/对象 ACL 决定 |
| D30 的 PID 身份 | 失效(D65 已记)⇒ mTLS 必做 | 不变 | **部分失效**:对端**用户** SID 与机主相同,须补 AppContainer SID(§2.3) |
| 这道隔离能被谁拆开 | 未测 | key 泄漏 / 有人把参数删掉(**会有 diff、可以配断言**) | **实测:容器里的 worker 拆不开(`exit 5`)**;`Administrators`=只用于拒绝的 token **拆得开**;**机主双击也拆得开** —— 本机 `EnableLUA=0`,一切进程皆 High,**拆它零摩擦、零提示**(§2.2b) |
| 拆开之后的粒度 | 不适用 | 单端口 | **实测:整个容器的全部回环,没有端口粒度** ⇒ 开一次就把 18081 一起放开 |
| 消掉 STATE:405 那条技术债 | 不消(只是绕开) | **消掉** | 不消(只是让 worker 到不了) |
| 对「将来还有别的低信任进程」 | 通解 | 只堵这一个口,每新增后端重做一遍 | **通解**(每个低信任进程一个容器) |
| 安装包体积 / 离线部署 | **未测**(§7 待核实 8) | 无影响 | **无影响**(OS 内置机制,零额外产物) |

---

## 4. 建议裁定 + 推翻条件

### 4.1 建议

**待决 6 = 路线 C(AppContainer),并顺手做 B。A 降为备选。**

理由,按份量排:

1. **C 实测挡住了 DD-7 要挡的那一件事**,而且挡得比设计估计宽(文件侧白拿),
   并且**被关起来的 worker 自己拆不开**(exit 5)。对「提示注入 → 直连 18081 绕过 E1/审计」
   这个威胁模型,C 是成立的。
2. **C 是唯一不与 D73 冲突的**:管道实测跨得过 AppContainer 边界(§2.3),
   而 A 跨 VM 边界要 vsock/TCP ⇒ 会逼出一条 D73 明令不存在的网络面。
3. **零提权**(DD-16 的实际诉求)。用户要消掉的「自己动手」一大半就是启用 WSL2 那一步。
4. **B 独立成立**(D65 已明写),而且实测发现它比原设想更好做(§5.3)。

### 4.2 但 C 必须配一条断言,否则它就是「看着有防护」

**豁免这把钥匙是:不需要管理员组(实测)· 机器级 · 没有端口粒度 · 会持久留着 · 加上之后没有任何测试会变红。**
——这五条凑在一起,正是本项目最贵的那种缺陷形状。

⇒ **C 的采纳必须捆绑以下强制点**(否则不得声称 worker 被隔离):

1. **【硬前置,2026-08-06 从「建议」升级】运行期断言:worker 的 AppContainer SID
   不得出现在回环豁免列表里。**
   升级理由:实测本机 `EnableLUA=0`,机主账户的一切进程都是 High(§2.2b)⇒
   打开这道豁免**不需要任何提权步骤、不弹任何框、不留任何痕迹**。
   在这种机器上,「没人会去开它」这个假设一点支撑都没有。
   `CheckNetIsolation LoopbackExempt -s` 可读(实测普通 token 也读得到),
   或走 `NetworkIsolationGetAppContainerConfig`。
   命中 ⇒ **fail-closed:不启动 worker**,并如实告知「隔离已被解除」。
   照 `assistant.resident` 反向全表断言那个已被验证的形状写 ——
   使「为了跑通先放开一下」必然表现为**启动被拒 + 有 diff + 要留决议**。
2. **自检项:worker 起来后,主动验一次它连 18081 是 `10060`。**
   连得上 ⇒ 立刻停。这条比读列表更硬,因为它测的是**效果**不是**配置**。
3. **管道端的身份校验必须是三元**(父子 + 用户 SID + **AppContainer SID**),
   见 §2.3;且**不得**用严格「直接子进程」。
4. **诚实措辞**:在断言 ① ② 落地前,UI 与文档**不得**写「worker 无法访问后端」,
   只能写「worker 被投进 AppContainer;该隔离可由本机管理动作解除,当前未做检测」。
   —— 照 D73 `deployment_form` 那条纪律的体例。
5. **容器只对 `bin` 目录给 RX、对 `out`/scratch 给 Modify**,不得让它可写自己的可执行文件
   (spike 里已按这个分法做)。
6. ★ **非 TCP 的本机 IPC 必须单独收口**(2026-08-06 补测后新增)。
   实测:**AppContainer 不挡 AF_UNIX**(§2.5 ③),也不靠网络闸挡命名管道 ——
   这两条通道能不能走,**只由文件/对象的 DACL 决定**。
   ⇒ DD-7 那句「worker 的网络能力 = 一条连接,别无其它」**在 TCP 之外并不自动成立**。
   必须做的两件事:① 登记 worker 容器 SID **被授权**的每一个管道/socket(白名单,默认空);
   ② 断言「除白名单外,没有任何本机 IPC 对象的 DACL 授予该容器 SID」。
   ★ 尤其:**如果采纳 B′(把 `llama-server` 挪到 `.sock`),那个 socket 的 ACL
   就是唯一的闸** —— 漏了它,worker 直连后端这条路会**原样回来**,而回环隔离一点都拦不住。

### 4.3 推翻条件

1. ★ **`Hermes` 本体在 AppContainer 里跑不起来** ⇒ C 出局,退回 A。
   **这一条现在是未测的最大项**,见 §6.1;
2. ~~用户双击实测显示 Medium 也能开豁免~~ ⇒ **✅ 2026-08-06 已结案,但走的是第三条路**:
   本机 `EnableLUA=0`,根本没有 Medium 上下文(§2.2b)。裁定按「**worker 绕不过,
   机主无摩擦能绕**」定稿,§4.2 断言 ① **已升为硬前置**。**本条推翻条件消耗完毕。**
3. ~~用户双击实测显示 Medium 不能开~~ ⇒ **在本机已不可能触发**。
   它转成一条**产品化**的待核实:若将来在 UAC 开启的机器上实测出 Medium **不能**开,
   则那类机器上 C 更强(豁免要一次提权才拆),但**本机的裁定不变** ——
   因为 §4.2 断言 ① 防的是「运维图方便自己开一下」,与 UAC 状态无关;
4. **豁免列表断言做不出来**(读不到、或没有稳定 API)⇒ C 降级为「问责,不是遏制」,
   按 D73 形态 A/B 那套措辞纪律处理;
5. 将来需要 worker 访问外网或 LAN ⇒ capability 可以单独给(实测与回环正交),
   但**回环仍然只能靠豁免**,届时 §4.2 断言 ① 与新需求会直接冲突,须重裁;
6. 若 P4 Broker 或方向 B 迟迟不落地 ⇒ 本裁定不受影响(它们是 H1/H2 的独立前置)。

---

## 5. 待决 7 的裁定,以及耦合怎么解

### 5.1 裁定:**命名管道。不是 HTTP。**

与 D73「层一全链路无 HTTP」**一致**,而且在路线 C 下这不再是风格选择,是**结构约束**:

- MCP over HTTP 要走回环(worker ⇄ 网关 / MCP 服务端);
- AppContainer **双向**都挡回环(§2.1);
- 唯一的开关是豁免,而豁免**没有端口粒度**(§2.2 的 A/B 实测);
- ⇒ **为了让 HTTP 通而开的豁免,会把 18081 一起放开** ——
  也就是**为了接 MCP 而亲手拆掉 DD-7 想要的那道隔离**。

**HTTP + C 是自毁组合。** 管道则实测可用,且形状是「默认拒绝 + 显式授权」(§2.3)。

### 5.2 耦合怎么解 —— 它其实自己解开了

STATE 待决 7 写「**选容器则管道跨边界成问题,选 AppContainer/本机则管道自然**」。
实测把这句话收紧成一条单向推理:

| 隔离路线 | 管道 | HTTP | 与 D73 |
|---|---|---|---|
| A 容器化 | 未测,按机制跨 VM 不通 ⇒ 要 vsock/TCP | 天然可行 | **冲突**:会引入 D73 明令不存在的网络面,须按三通道表新开一行 |
| C AppContainer | **实测可行** | **实测不可行**(除非拆掉隔离) | **一致**,不需要新决议 |

⇒ **两件事不是「一并裁定」,而是「裁了 6 就等于裁了 7」**:
选 C ⇒ 只能管道 ⇒ D73 不用改。选 A ⇒ 被迫重开 HTTP 的口 ⇒ 要为 D73 补一条例外。
**这是 C 优于 A 的第二个独立理由**,与「零提权」不重叠。

### 5.3 ★ B 的独立价值:要加什么、加在哪一行(**我不动,由 P4 车道带**)

D65 明写「B 的收益(消技术债)独立成立,做 A 也应顺手做 B」。复核确认:
**全仓 `grep api.key` 在 `90-ops/start-stack.ps1` 与 `10-core/gateway/model_loader.py` 里一个都没有** ——
`llama-server` 至今无鉴权。

★ 纪律:`model_loader.py` 与 `gateway.py` 归 **P4 车道**(S14 刚落地、S16 还在动,
且 `main` 工作树里它们**正处于未提交修改状态**)。**本包只写「改哪一行」,一个字都不改。**

**B(加鉴权)—— 五个落点(行号以 2026-08-05 提交态 `3b34053` 为准,P4 改动后须重新定位):**

| # | 文件:行 | 现状 | 要加什么 |
|---|---|---|---|
| 1 | `90-ops/start-stack.ps1:104-107` | `Start-Process -FilePath $Llama -ArgumentList @('-m',$Model8B,…,'--host','127.0.0.1','--port','18081',…)` | 加 `'--api-key-file', $KeyFile`。★ 用 `--api-key-file` 而**不是** `--api-key`:命令行会出现在 `Get-Process`/WMI 里,等于把密钥摊开给同机进程 |
| 2 | `90-ops/start-stack.ps1:114` | `& curl.exe -sf -m 3 http://127.0.0.1:18081/health` | 就绪探测要带 key。★ **先实测 `/health` 是否受 `--api-key` 约束** —— 不得假设它免检;假设错了会让「就绪」永远为假,而 `-f` 会把它变成启动失败 |
| 3 | `10-core/gateway/model_loader.py:223` | `"-c",str(int(c["ctx"])),"--host","127.0.0.1","--port",str(port)` | 动态起的后端同样要带 key(与 ① 同一把) |
| 4 | `10-core/gateway/model_loader.py:131-132` | `_health_ok`:`c.get(f"http://127.0.0.1:{port}/health")` | 同 ②:探测要带 key |
| 5 | `10-core/gateway/gateway.py:1513/1523/1548` | `upstream_url = backend.rstrip("/")+"/v1/chat/completions"`;`_client.build_request("POST",…)` / `_client.post(…)` | 出站转发要带 `Authorization: Bearer <key>` |

密钥落点:`{state}\secrets`(现有唯一凭据落点,实测真 Deny ACE,且已排除出备份)。

**★ B′(更彻底,建议 P4 车道一并评估):把后端从 TCP 挪到 AF_UNIX。**

`--host` 实测支持「地址以 `.sock` 结尾则 bind UNIX socket」(§2.6)。
若后端**根本不监听 TCP**,则 STATE:405 那条「同机进程可直连 18081 绕过 E1/审计」
**在结构上消失**,而不是靠一层鉴权挡住 —— 这与本项目「结构性强制优于配置字段」的偏好一致。

**★★ 但 2026-08-06 的补测把 B′ 的账算反了一半,必须写在采纳之前:**

1. **容器【能】连宿主的 AF_UNIX**(§2.5 ③ 实测,零 capability 容器也能)。
   ⇒ **B′ 并不能靠路线 C 的网络隔离保护后端。** 把 `llama-server` 从 TCP 挪到 `.sock`
   之后,「worker 直连后端」这条路是否被堵,**完全取决于那个 socket 文件的 ACL** ——
   而回环那道闸对它**一点作用都没有**。
   ⇒ 就 worker 这个威胁模型而言,**B′ 不比「TCP + AppContainer」更强;配错 ACL 就更弱。**
   我初版把 B′ 说成「结构性地消掉技术债」,**那句要收窄**:它消掉的是
   「**任意同机进程**都能直连 18081」这个面(对**非容器**的低信任进程仍然是真收益),
   但它**不**消掉「被 AppContainer 关起来的 worker 能不能连后端」——那一格改由 ACL 决定。
2. **落点必须先实测**:AF_UNIX 在 `{state}` / `{cache}\tmp` / `{code}` 下实测可用,
   但在机主 `%TEMP%` 下 connect 报 `10022`,且失败会留下**永久删不掉**的孤立 ReparsePoint,
   把该路径后续的 bind 也弄坏(§2.5 ①②)。⇒ 起停必须处理残留,落点不得随手选。

⇒ 建议:**B 先做(五个落点,收益确定且不依赖任何隔离假设)**;
**B′ 单独立项**,并且它的验收口径必须写成「socket 的 ACL 不授予 worker 容器 SID」
这条断言(§4.2 第 6 条),而**不是**「挪到 socket 就安全了」。

---

## 6. 我没能测到的部分(覆盖账)

### 6.1 ★★ 最大的一项:我测的是**机制**,不是 **Hermes 本体**

投进 AppContainer 的是我写的 .NET 探针,**不是 Hermes**。
`HERMES_INTEGRATION_DESIGN.md` §7 待核实 1 至今是「Hermes 的实际运行时与语言未知」。
⇒ 「AppContainer 能挡住回环」**已证**;「**Hermes 能在 AppContainer 里正常跑**」**未证**。

一个进程能不能活在 AppContainer 里,取决于它要碰什么:
写自己的安装目录、读机主 profile 下的配置、起 shell、用某些命名对象……
**任何一样被挡住,C 对 Hermes 就不成立**(机制仍然成立)。

⇒ **这一条应当排为 H1 之前的第一顺位实测**,并写进 §7 待核实第 7 条。
本包的 spike 可以直接复用:把 `AcSpike run` 里的子进程换成 Hermes 的启动命令即可。

### 6.2 其余未测项

| 未测的 | 为什么 |
|---|---|
| ~~普通用户双击态能否开豁免~~ | ✅ **2026-08-06 结案**:本机 `EnableLUA=0`,**没有这个上下文**;机主双击一样是 High,且能开(§2.2b) |
| **UAC 开启的机器上,Medium 进程能否开豁免** | **未测,且本机测不到** —— 要 `EnableLUA=1` + 重启,是机器级安全配置变更,不为一次测量做。**只对产品化重要** |
| **豁免生效/撤销后 worker 是否立刻改变行为**(有没有缓存/需不需要重启进程) | 需管理员加豁免;按纪律不自跑。`run-exempt` 已写好交用户 |
| ~~容器内进程能否连宿主的 AF_UNIX~~ | ✅ **2026-08-06 已测:能**(§2.5 ③)。这一格从「未测」变成了一条**缺口**,并连带改写了 B′ 的账(§5.3) |
| **AF_UNIX 为什么只在机主 `%TEMP%` 下失败** | 现象已测清(§2.5 ①),**原因未归因**。不影响裁定(项目五个根下都可用),但 B′ 立项时应当查明 |
| **`llama-server` `/health` 是否受 `--api-key` 约束** | 要真起一个带 key 的实例;会占显存,且 `gpu_broker` 正由另一路会话在改,不在本轮碰 |
| **A(WSL2/Hyper-V)的任何一格** | 启用 WSL2/Hyper-V 需管理员 + 可能重启,按 D46 纪律一律不自跑 |
| **自带产物体积 / 离线部署**(§7 待核实 8) | 与本包无关,未碰 |
| **`-is`(允许入站)的行为** | 未测。若将来 worker 要当服务端,这是另一把钥匙,粒度问题同族 |

### 6.3 门禁覆盖(如实记账)

- **本 worktree 跑 `run-tests.ps1 -Full` 会报「客户端自检 — 没有构建产物」** ——
  那是 worktree 的正常形状,**未去修**。
- 本包**没有动 `10-core/`**,`.githooks/pre-commit` 的自检门禁段因此不触发(它只在
  `10-core/(gateway|gpu-broker)/` 有改动时才跑)。
- **绝对路径检查**:已用钩子自己的正则
  `(^|[^A-Za-z0-9])[A-Za-z]:[\\/]` 扫过 spike 的四个源文件,**零命中**;
  真实路径全部运行期从 `LOCALAI_PROBE` / `SpecialFolder` 取。

---

## 7. spike 的性质:**(a) 一次性勘察产物,不进门禁**

明确归类,避免落进「看起来在门禁里、其实没人跑」的中间态:

- `run-tests.ps1` 的反向全表扫描根写死 `10-core` 且只收 `test_*.py`
  ⇒ `90-ops/spikes/` 下的东西**不会被判红,也永远不会被跑**;
- 本包**不为它申请门禁登记**。它的产出是 §2 那张表,不是一个绿灯;
- `90-ops/spikes/appcontainer-loopback/README.md` 里把这一条写在最前面,
  并注明是照 2026-08-05 审计第 ⑦ 条(`90-ops/debug/selfcheck.py` 收不到扫描器,
  而 README 却声称「由某个测试文件检查」)的教训写的。

**若将来判定某条值得长期守**,候选只有两条,且都**不在本包范围**、须由**主执行层**(它拥有 `90-ops`)去改:

| 候选断言 | 为什么值得守 | 要动谁 |
|---|---|---|
| **worker 的 AppContainer SID 不在回环豁免列表里**(§4.2 ①) | 这是 C 唯一的可绕点,且绕过之后没有任何现有测试会红 | 归 `10-core/`(网关启动期或 worker 启动器),会被 `test_imports.py` 自动扫到;**不需要**改 `$RULES` |
| **worker 起来后实连 18081 必须得到 `10060`**(§4.2 ②) | 测效果而不是配置 | 同上,属运行期自检 |

★ 两条都应当落在 `10-core/`,而**不是** `90-ops/spikes/` ——
落 `10-core/` 才被既有门禁白拿(照 §3.5 落点勘察「落这里才被 `test_imports.py` 自动扫到」那条)。

---

## 8. 交给用户的两个双击入口

`90-ops/spikes/appcontainer-loopback/`,两个都自己恢复机器状态:

| 文件 | 要不要管理员 | 补哪一格 |
|---|---|---|
| **`1-普通用户双击.cmd`** | 不要 | ✅ **已于 2026-08-06 跑过并结案**(§2.2b)。留着的用途变成两条:① 换一台 **UAC 开启**的机器时,它能直接回答残留的那一问;② 作为「AppContainer 到底挡住了什么」的可复现回归。程序会自己读 `EnableLUA` 并按上下文措辞 |
| `2-豁免粒度-需管理员.cmd` | **要**(右键以管理员身份运行) | 复核 §2.2 的粒度结论:加了豁免之后容器是不是**连 18081 都通了**、撤销后是否立刻恢复。★ 本机 `EnableLUA=0` 下「需管理员」这个区分没有实际意义,但脚本仍会自检 |

★ **本机上这两个脚本的「提权/非提权」区分是失效的**(`EnableLUA=0`,一切进程皆 High)。
换到 UAC 开启的机器上区分才恢复 —— 脚本的自检逻辑照旧成立,不用改。

两个脚本对机器级状态的处理:
除「回环豁免列表」外**什么都不碰**(不改防火墙规则、不装驱动、不启用 Windows 功能、不建账户);
豁免的恢复策略是「先试 `-d` 删单条;删不掉**且进场时列表本来是空的**才用 `-c`;
进场时列表里本来有别人的条目 ⇒ **拒绝清空**,改为要求手动撤」。

★ `1-` 只需要回贴两段输出。判据是**看效果**(加完再读一次列表),不看退出码 ——
实测 `CheckNetIsolation` 在**被拒**时也会打印「完成。」,退出码单独看会读反。

---

## 9. 一手来源

- 实测:`90-ops/spikes/appcontainer-loopback/`;原始 JSON 落
  `%TEMP%\localai-acspike\out\report.json`(每轮重写,非归档)
- 我方约束:[DECISIONS.md](../DECISIONS.md)(D46 提权护栏 · D65 + D65 补 · D75 取号纪律)·
  [decision-packets/two-layer-mcp-decisions-2026-08-03.md](two-layer-mcp-decisions-2026-08-03.md)(**D73** 层一无 HTTP)
- 设计:[HERMES_INTEGRATION_DESIGN.md](../HERMES_INTEGRATION_DESIGN.md)
  (DD-7 进程级隔离 · DD-16 自带分发 · §4 分期 · §6 三选一 · §7 待核实 7)
- 现状:[STATE.md](../STATE.md)(待决 6 / 待决 7 · 技术债「后端无鉴权」)
- 二进制自述:`llama-server --help`,version 10107 (c0bc8591e)
- 系统工具自述:`CheckNetIsolation LoopbackExempt -?`(Windows 11 25H2 build 26200.8875)
