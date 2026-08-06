# D93 · 证书生命周期【接到底】:三样死代码接上调用点 · 五种归因落到界面 · 过期之前看得见

> 车道:V1 证书生命周期(worktree `cert-lifecycle-wiring-f002f3`,分支 `claude/cert-lifecycle-wiring-f002f3`)
> 日期:2026-08-06 · 状态:**待第 0 条车道取号并入**
> 前置:**D92**(垂直切片 + 跨语言成对断言的硬前置)· **D89**(证书生命周期闭环)· **D49**(服务器证书必须可续签)
> 上一棒:`decision-packets/cert-lifecycle-2026-08-05.md` —— 本包执行的就是它的 **§2**

---

## §0 这条车道为什么存在:A5 不是"忘了做",是"纪律全守了,洞照样出现"

审计 A5:`TlsFailure.cs`(259 行)· `CertLifecycle.cs` · `RenewDeviceCertIfDue`
在 WPF 客户端里 **零调用点**,而 `TlsFailure.cs` 已经被链进客户端构建 ⇒ **随包发布的死代码**。

★ 它的来历才是要记住的那一半:上一条车道(core/identity)**完全遵守了 D82** ——
没越界,写了决议包(§2)点名 client 半边要改 `HubClient.cs` 哪一行 —— **而那一行至今没人改**。
**缝就是车道边界本身**,这正是 D92 把车道改成垂直切片的理由。本车道 = 那条缝的两侧合一。

⇒ 因此本包的验收判据不是"库写好了",是
**「用户在过期之前看得见,而且给的建议是他能执行的」**。

### ★★★ 而按这条判据一验,发现原来那句建议**是他不能执行的**

本车道补的第一条断言(lan-edge selftest 甲2)实测三问三答:

| # | 实测 | 结果 |
|---|---|---|
| ① | 带着**已过期**的设备证书 `POST /identity/renew/enroll` | **连一个 HTTP 状态码都拿不到** |
| ② | 带着已过期的设备证书 POST **匿名**的 `/pair/enroll` | **同样拿不到状态码** |
| ③ | **把证书摘掉**,同一个中枢、同一秒,POST `/pair/enroll` | **200 + 六个词** |

**⇒ 承重结论:设备证书一旦真过期,续签这条路【结构上】就断了** ——
续签本身要用那张证书跟中枢握手,而它已经握不上了。
**唯一的出路是重新配对,而且必须先摘掉那张证书。**

而 `TlsFailure.Explain(LocalDeviceCertExpired)` 原话是:

> 「本机的设备证书已过期 —— **本机会自动续签**;若一直没成功,请确认中枢在线。
>  ★ **不要点「重新配对」**:那会删掉本机私钥,把一个只需要续签的身份亲手销毁。」

**两句都是错的,而且错得正好把用户锁死在原地**:许了一个结构上不可能兑现的诺言,
又劝阻了唯一能自救的动作。这段话**只会**在 `notAfter <= now` 时出现(那就是它的判据),
所以它没有一次是对的。

---

## §1 裁定

### 1.1 三样东西的调用点(A5 关闭)

| 东西 | 调用点 | 备注 |
|---|---|---|
| `TlsFailure.Classify` | `HubClient.ClassifyTlsFailure(ex, profile)` | 客户端**一行判据都不自己写**,只做枚举映射 |
| `TlsFailure.WarnLocalCert` / `CertLifecycle` | `HubClient.CertWarning` | 「过期之前看得见」的来源 |
| `Transport.RenewDeviceCertIfDue` | `HubClient.ProbeAsync` → `TryRenewDeviceCertAsync` | 每次探测前自愈一次 |

**★ 判据与文案必须同源。** `LastError` 的五个分支全部改成调用 `TlsFailure.Explain(...)`,
客户端里不再留一份平行的文案。两份文案迟早分家,而分家那天
「归因改了话没改」不会有任何东西变红,用户看到的是一句与结论对不上的建议。

**★ 决议包 §2.6 的片段有一个真缺口,本包补上**:那段没有 `try/catch`,
而 `RenewDeviceCertIfDue` 内部走 `Transport.Send`,中枢不在线时**会抛**。
不接住的话 `ProbeAsync` 在调 `/v1/models` **之前**就抛出去 ⇒ State 永远停在 `Connecting`,
五种归因一格都跑不到 —— 「中枢没开机」这个最常见的情形反而变成转圈。
★ 这正是垂直切片该抓到的那类东西:上一条车道**写不出**这个缺口,因为它跑不到客户端。

### 1.2 ★★★ **推翻 D89 §1.6 那张表的一半**:过期之后「不要点重新配对」是错的

D89 §1.6 的表:

| 归因 | 私钥状态 | 该说什么 |
|---|---|---|
| `LocalDeviceCertExpired` | 还在 | 「**不要**点重新配对」|
| `LocalProfileUnusable` | 已经没了 | 「**只能**重新配对」|

**左列的判断本身没错,错的是它被钉在了"已经过期"那一格。**
私钥还在**不等于**还能续签 —— 续签要跟中枢握手,而握手要用那张已经死掉的证书。
私钥活着救不了你:中枢根本不会跟你说话。

**⇒ 裁定:建议**跟着相位走**,不钉在一句固定的话上。**

| 相位 | 谁说 | 说什么 |
|---|---|---|
| `Healthy` | — | 不出声 |
| `RenewDue` | — | **不出声**(系统正在正常自愈,正常运转也报警的告警两周内就被学会忽略) |
| `Critical`(还没过期) | `WarnLocalCert` | 「还有 N 天…**不要点重新配对**(私钥还在,续签就够)…但请在过期**之前**处理」 |
| `Expired` | `Explain` / `WarnLocalCert` | 「**只能重新配对**;自动续签这条路已经走不通了,**等下去不会自愈**」 |

★ D89 那句劝阻**没有被删掉,是被搬到了它成立的那一段**。
★ `Critical` 那句必须带**截止期限**(「过期之后就只剩重新配对」)—— 不说期限的提醒只是噪音。

**同时更正 `Explain(LocalProfileUnusable)` 里那句「与『设备证书过期』不同:那一种**不该**重新配对」**
—— 它复读了同一个错误。两者真正的区别不在**动作**(都只能重新配对),而在**代价**:
过期那一种要搭上一把本来有用的私钥,材料损坏那一种没有可搭的。

**原来那三条逐字断言随之改判**(不是放宽,是反过来钉):
- 过期文案**不许**含「不要点」· **不许**含「会自动续签」· **必须**含「只能重新配对」;
- 「不要点」改钉在 `WarnLocalCert` 的 `Critical` 段。

### 1.3 过期【之前】看得见:告警必须排在【在线】那一格**之前**

这一条反直觉,也正是它容易被写错的地方:**过期之前客户端正是在线的。**
告警只挂在断线那几格的话,它永远等到过期之后才出现 —— 而那时自愈窗口已经关了。

⇒ `MainWindow.RefreshStatus` 里 `CertWarning` 的判断排在 `State == Online` 的提前返回**之前**,
挡掉那个 `tok/s` 读数。**已用顺序断言钉死**(两个下标先各自确认存在再比大小 ——
ASSERTION-PITFALLS 第 9 条第 3 种:`IndexOf` 找不到返回 -1,而 -1 恒小于任何下标)。

### 1.4 两条 fail-closed 通道:接上**一条**,另一条如实记账

| 通道 | 处置 |
|---|---|
| `/admin/ping` 的 `serverCert`(**全仓无读取方**,而注释写着「主机界面据此报警」) | **接上了** |
| lan-edge 的 stderr `[cert] !!` 横幅 | **没接**,理由见 §5 |

接法:`HubAdmin.ParseServerCert` → `HubAdmin.ServerCertWarning` → `DevicesView` 主机卡片。
★ `NeedsAttention` **由主机算好后直接吐出**,客户端不自己再推一遍 ——
判据在主机那边,重算一份就是给"两边说法相反"留门。
★ 只在 `NeedsAttention` 时才显示,轮换正常工作时一个字都不说。

### 1.5 ★★ 跨语言成对断言做成【一份被两侧编译的常量】,不是两份手抄的期望值

D92 的硬前置要求"服务端钉顶层键集合,客户端钉能不能解析"。
**照字面做仍然会再造一个 A1** —— 因为两份期望值会分家,而分家那天**两边都不会红**
(服务端照自己那份绿,客户端照自己那份也绿)。这正是 A1 的实际形状。

⇒ 新增 `10-core/identity/WireContracts.cs`,由 **lan-edge / transport / 客户端三个 csproj 同时编译**:

- 服务端断言:实际响应的顶层键集合 **== 登记表**(集合相等,不是"包含" ——
  「包含」放过"多发一个键"和"改了名还留着旧的",而那两种正是字段搬家的实际形状);
- 客户端断言:形状**由登记表生成**再喂给真解析器 —— 不是手抄一段 JSON(手抄的话服务端搬了家也照样绿);
- `HubAdmin.ParseServerCert` 自己也拿登记表核对键集合,认不出的形状一律判 `null`:
  **半份状态比没有状态更坏**,它会在界面上显示一个可信但错误的天数;
- **元断言**:`WireContracts.All` 里每一条都要被核对过,缺一条当场红(遍历源是表本身,不是手写名单)。

登记的四条:`GET /admin/ping` · `GET /admin/ping .serverCert` ·
`POST /identity/renew/enroll` · `POST /identity/renew/complete`。

★ 顺带堵住一条本来没人看的缝:transport 自检的测试 Edge 是**同一个文件里的桩**,
与真 lan-edge 是两份实现。桩漂了而真服务端没漂,那个套件照样全绿而生产是坏的。
现在**桩也对着同一份登记表核对**。

### 1.6 lan-edge 自检加上 AdminPort(为什么不放进 admin-e2e)

`/admin/ping` 的成对断言需要一个真的回环管理面。**门禁只跑 `lan-edge selftest`**
(`run-tests.ps1` 的 `Args = @('selftest')`),放进 `admin-e2e` 就是写一条**没人跑的断言** ——
ASSERTION-PITFALLS 第 10 条那种形状,而且它会躺在覆盖账里显得已被认真处置过。
⇒ 给 `Selftest()` 的 `EdgeConfig` 加 `AdminPort: 18442`。**没有改 `run-tests.ps1`**(那是 V3 的地盘)。

---

## §2 越界清单(点名到文件,写清为什么)

本车道**拥有**:`10-core/identity/**` · `20-client-win/transport/**` ·
`20-client-win/app/Services/HubClient.cs` · `20-client-win/app/localai-client.csproj` · 本决议包。

以下**不在**归属里,逐个说明:

| 文件 | 改了什么 | 为什么必须在本车道改 |
|---|---|---|
| `20-client-win/app/MainWindow.xaml.cs` | `RefreshStatus`:①两态加进状态映射 ②`CertWarning` 判断插在 `State == Online` 提前返回**之前** | 「过期之前看得见」**只能**落在这里 —— 它是全客户端唯一始终在屏幕上的那一格,而过期之前客户端正是在线的 |
| `20-client-win/app/Views/DevicesView.cs` | 两张状态映射各补三格(含主机卡片原本漏的 `HubIdentityChanged`)· 两处显示 `CertWarning` · 主机卡片显示 `ServerCertWarning` | 那张卡上就有红色的「解除本机配对」按钮 —— 五种归因里说错任何一种,代价都直接落在这个按钮上 |
| `20-client-win/app/Services/HubAdmin.cs` | 新增 `ServerCertStatus` / `ParseServerCert` / `ServerCertWarning`,`ProbeAsync` 里解析 `serverCert` | 它是**唯一**已经在读 `/admin/ping` 的地方;`serverCert` 的读取方只能长在这儿 |
| `20-client-win/app/Selftest.cs` | +29 条断言(见 §3) | 客户端半边的断言必须在客户端套件里 |
| `20-client-win/app/I18n/strings.json` | 新增 `status.local_cert_expired` / `status.local_unusable` | 两态要有自己的词,否则等于没单列 |
| `10-core/lan-edge/Program.cs` | selftest 甲2/丙 两节 + `Selftest()` 加 `AdminPort` | 任务 4 明确指派;成对断言的服务端半边 |
| `10-core/lan-edge/localai-lan-edge.csproj` | 加一行 `WireContracts.cs` | 两侧编译同一份登记表的必要条件 |

**没有碰**:`10-core/gateway/**` · `HubGpu.cs` / `LeaseKeeper.cs`(V2)·
`90-ops/**` · `config/**` · `.githooks/**`(V3)· `DECISIONS/PROJECT_PLAN/STATE/worklog`(第 0 条车道)。

---

## §3 改了哪些文件 · 断言数 · 门禁数字

| 文件 | 动作 |
|---|---|
| `10-core/identity/WireContracts.cs` | **新增** — 跨进程响应契约的唯一登记表(两侧编译) |
| `10-core/lan-edge/Program.cs` | selftest 甲2(自救路径)· 丙(成对断言 + 元断言)· `AdminPort` |
| `20-client-win/transport/TlsFailure.cs` | 新增 `WarnLocalCert`;更正两段文案(§1.2) |
| `20-client-win/transport/Program.cs` | 文案断言改判 + `WarnLocalCert` 相位断言 + 客户端侧成对断言 |
| `20-client-win/app/Services/HubClient.cs` | 五态枚举 · `ClassifyTlsFailure` 改为委派 · 文案同源 · `CertWarning` · 续签调用点 |
| `20-client-win/app/Services/HubAdmin.cs` | `serverCert` 的读取方(fail-closed 最后一段路) |
| `20-client-win/app/MainWindow.xaml.cs` · `Views/DevicesView.cs` · `I18n/strings.json` | 五种归因 + 两条告警落到界面 |
| 三个 csproj | 各加一行 `WireContracts.cs` |

**门禁数字(`run-tests.ps1 -Full`)**

| 套件 | 前 | 后 |
|---|---|---|
| identity selftest / 2 / 3 / 4 / 5 | 11 / 15 / 42 / 14 / 57 | 不变 |
| **lan-edge selftest** | 8→**20** | **33**(+13) |
| **transport selftest** | **58** | **69**(+11) |
| Python 21 套件 | 不变 | 不变 |
| 客户端工程编译 | √ | √(扫描根 11 → **12** 条 Compile Include) |
| `client --selftest` | **没有构建产物**(门禁判「没跑起来」) | **PASS=1935 FAIL=0**(出包后补上,见下) |
| **门禁合计** | **PASS=1420 FAIL=0**,**未过**(没跑起来 1 个) | **PASS=3379 FAIL=0**,**√ 门禁通过** |

★ 合计从 1444 跳到 3379 **不是又加了 1900 条断言** —— 是 `client --selftest`
这一整个套件(1935 条)第一次被算进来了。前后两个合计**不是同一个量程**,别拿它们相减。
可比的是逐套件那几行。

**客户端自检**(Debug 产物量,同一量程前后对比):

| | 断言总数 | 结果 |
|---|---|---|
| 改动前(把 `Selftest.cs` 还原成 HEAD 那份,其余保持本次改动) | **1905** | 1904 PASS / 1 FAIL |
| 改动后 | **1939** | **1939 PASS / 0 FAIL** |

⇒ **净增 +34 条**(实测,不是数源码行数得来的 —— 有两处 `foreach` 会把几行源码放大成十几条运行时断言)。
★ 门禁跑的是 **Release/win-x64** 那个产物,数字是 **1935** —— 与 Debug 的 1939 差 4 条是
**TFM 不同**造成的(ASSERTION-PITFALLS 第 3 条已记:Debug 与 Release/win-x64 本来就不是同一个量程)。
两个数各自内部前后对比才有意义,**不要交叉相减**。
★ 改动前那 1 条红是**预期的**:HEAD 的断言查的是「`HubClient.cs` 的**源码里**有没有『必须重新配对』」,
而这次改动**故意**把那句话搬到了 `TlsFailure.Explain`(判据与文案同源)。
该断言的判词没变、判据已改成查**用户真会看到的那句话**,并另钉一条"这一态真的路由到它"(见 §1.1)。
★ 这两个数字**不能**拿去和 CI 的基线比 —— Debug 与 Release/win-x64 是两个 TFM,
断言总数本来就不同(ASSERTION-PITFALLS 第 3 条)。它们只用来证明**新加的这些断言真的跑过且是绿的**。

### ★ 出包 + Release 自检(2026-08-06 傍晚补做,覆盖缺口已关闭)

`90-ops\build-client.ps1` 出包(`dist\client-pack`,版本戳 `20260806-1817+9bd2fec`,**非 dirty**):

| 形态 | 断言数 | SRCMISS(读不到源码的处数) |
|---|---|---|
| 发布产物原位(仓库内) | PASS=873 FAIL=0 | **237** |
| 换个安装位置(仓库外) | PASS=869 FAIL=0 | **239** |
| **门禁跑的 Release/win-x64(源码就在旁边)** | **PASS=1935 FAIL=0** | **0** |

★★ 前两个数**不能**和最后一个比:发布产物旁边没有源码 ⇒ `TryReadSource` 全部落空 ⇒
那些【结构/接线】断言整段没跑(不计 PASS、不计 FAIL、也不计 SKIP)。
**`SRCMISS=0` 才是"这批断言真的全跑了"的凭据**,而门禁那次正是 0。

★ 逐条点名核对过本车道新断言在 Release 产物里的命中数(不是靠总数推断):
续签调用点 1 · `TlsFailure.Classify` 1 · `WarnLocalCert` 2 · 告警排序 1 ·
成对断言/客户端 3 · `HubState` 两态 2 · 顶栏两态 2 · 设备页两态 2 · 主机卡片读 serverCert 1 ·
界面词不同 1 · 本机≠主机 1 · 词表有键 2 · 少键判 null 6 · 老中枢不报 1 · 反向全表 12 格 1。

**净增断言:+58 条**(lan-edge +13 · transport +11 · 客户端 +34),
其中**跨语言成对断言 10 条**:
服务端 4 条(`/admin/ping` 顶层 · `.serverCert` · `renew/enroll` · `renew/complete`)+
客户端 4 条(解析出 serverCert · 目标字段逐个对 · daysLeft 是数字 · 桩形状 == 登记表)+
**元断言 2 条**(登记表每条都被核对过 · 两个方向的条数对拍)。
★ 另有 8 条反向断言(少任一个键 ⇒ 判 null;老中枢不报 ⇒ 判 null)守着"认不出就 fail-closed"。

---

## §4 红测(证明这些断言不是恒绿的)

| # | 动作 | 结果 |
|---|---|---|
| 1 | 摘掉 `ProbeAsync` 里的 `RenewDeviceCertIfDue` 调用(连同函数体里的引用) | **恰好** 1 条红:「RenewDeviceCertIfDue 在客户端里有调用点」。★ 这条就是 A5 本身 |
| 2 | 把 `CertWarning` 判断挪到 `State == Online` 提前返回**之后** | **恰好** 1 条红:「证书告警排在【在线】那一格之前」 |
| 3 | 服务端 `/admin/ping` 的 `serverCert` 里**悄悄多发一个键** | **恰好** 1 条红,且消息直接打出病因:`实际 [… redTestExtraKey] / 登记 […]` |

★ 1 与 2 是同一次运行里一起做的,结果是 `PASS=1932 FAIL=2` —— **只有那两条红**,其余全绿。
★ 三次都已用**文件备份**还原(不是 `git checkout` —— ASSERTION-PITFALLS 第 2 条),
并逐个核对 SHA-256 与改动前一致;`git diff --stat` 前后一致(613 插入 / 50 删除)。

---

## §5 没做的,和为什么(★ 跑不了和跑过了必须长得不一样)

1. ~~`client --selftest` 没进门禁~~ —— **2026-08-06 傍晚已补做**:出了包、跑了 Release 自检,
   门禁 **PASS=3379 FAIL=0 通过**(退出码 0,第一次)。`SRCMISS=0` 证明那 34 条真的全跑了。
   ★ 出包时**当场抓到我自己写的一个错**:那批结构断言原来写成 `Assert(src is not null && ...)`,
     而发布产物旁边没有源码 ⇒ 10 条当场红,`build-client.ps1` 拒绝出包(它做对了)。
     修法**不是**照惯例改成 `src is null || ...`(那是 fail-open,在唯一检查不了的形态下无声放行),
     而是先问「这条真的非读源码不可吗」—— 一半答案是"不是",搬成了运行时判据
     (`Enum.GetNames` / `Strings.Get` / 直接调 `WarnLocalCert`),它们在发布产物里**照样跑**,
     而且比查源码文本更强:测的是行为,不是那串字写在哪个文件里。见 commit `9bd2fec`。
2. **lan-edge 的 stderr `[cert] !!` 横幅**没接。它落在 `dist/host/启动Edge.cmd` 拉起的控制台窗口里,
   而那个 `.cmd` 在 `dist/`(gitignore)、启动脚本归 `90-ops`(V3)。
   ⇒ 本车道接的是**结构化**那条(`/admin/ping` → 界面),它现在是活的;
   stderr 那条**降级为冗余备份,不再是唯一通路**。要不要把 Edge 的输出落到一份持久日志,
   是 V3 的事,已写进 §7 的移交草稿。
3. **实机没有跑过一次真的续签** —— 本包全部是自检里的端到端(真 HTTP + 真 mTLS + 临时身份)。
   实机 hub 的服务器证书**一个字节没动**(D89 §4.4 那一格仍然要用户双击)。
4. **`DevicesView.cs:618` 显示存的 `EdgeUrl`**(决议包 §2.7 那条纯显示问题)**没修**。
   它不在本车道的判据里,且**不修没有功能后果**;修它要让那一处能拿到 `IPEndPoint`,
   属于另一块改动,不搭本次的车。
5. **设备私钥仍不轮换** —— D89 §1.2 的明确裁定,不是遗漏。
6. **`/admin/ping` 的令牌**(`HubAdmin` 顶部注释里那条"能连回环的不止坐在主机前的人")没做 ——
   那是准入,不是证书生命周期,属另一条车道。

---

## §6 推翻条件

1. 若设备证书续签改成**不经过 mTLS**(例如另开一条带外的续签通道,或允许过期证书在宽限期内握手)
   ⇒ §0 那三条实测结论作废,§1.2 的文案要改回"可以自动续签";
   **lan-edge selftest 甲2 会在那一刻变红,它就是提醒**;
2. 若证书有效期校验被挪到应用层(过期也能拿到 401)⇒ 同上,且 D89 §0(d) 那条断言也会红;
3. 若 `run-tests.ps1` 开始跑 `admin-e2e` ⇒ §1.6 那个 `AdminPort: 18442` 可以搬回 admin-e2e;
4. 若 V3 的"枚举所有跨进程契约"元断言落地 ⇒ `WireContracts.All` 应当成为它的数据源之一,
   而不是被它再抄一份(抄一份就是又一个 A1)。

---

## §7 给第 0 条车道并入中央文档的**草稿**(本车道不改那四份)

### 7.1 `DECISIONS.md` 新条目(建议紧接 D92 之后取号)

```
## 2026-08-06 · D93 · 证书生命周期接到底:三样死代码接上调用点 · 五种归因落到界面 ·
##                    ★ 推翻 D89 §1.6 的一半(过期之后「不要点重新配对」是错的)

> 车道:V1 证书生命周期(decision-packets/cert-lifecycle-wiring-2026-08-06.md)。
> 前置:D92(垂直切片 + 成对断言硬前置)· D89 · D49。

### 背景:A5 是"纪律全守了,洞照样出现"

core/identity 车道按 D82 写了决议包点名 client 半边要改哪一行,而那一行至今没人改;
同一轮里 TlsFailure.cs 被链进客户端构建 ⇒ 随包发布的死代码。缝就是车道边界本身。

### 裁定① 三样东西的调用点

TlsFailure.Classify → HubClient.ClassifyTlsFailure(客户端一行判据都不自己写);
WarnLocalCert/CertLifecycle → HubClient.CertWarning;
RenewDeviceCertIfDue → ProbeAsync。文案全部改成调用 TlsFailure.Explain —— 判据与说法同源。

### 裁定② ★★★ 推翻 D89 §1.6 表格的左半:过期之后「不要点重新配对」是错的

实测(lan-edge selftest 甲2,三条断言):设备证书一旦过期,/identity/renew/enroll
连一个 HTTP 状态码都拿不到 —— 续签要用那张证书握手,而它已经握不上了。
带着过期证书连匿名的 /pair/enroll 也够不着;把证书摘掉,同一秒 200 + 六个词。
⇒ 过期之后唯一的出路是重新配对。原文案许了一个结构上不可能兑现的诺言,
  又劝阻了唯一能自救的动作,而它只会在已经过期时出现 —— 没有一次是对的。
⇒ 建议改成【跟着相位走】:Critical(还没过期)说「不要点重新配对 + 截止期限」,
  Expired 说「只能重新配对,等下去不会自愈」。D89 那句劝阻没被删,是搬到了它成立的那一段。

### 裁定③ 过期之前的告警排在【在线】那一格之前

反直觉但承重:过期之前客户端正是在线的。挂在断线那几格 = 永远等到过期之后才出现。

### 裁定④ 跨语言成对断言 = 一份被两侧编译的常量(10-core/identity/WireContracts.cs)

照 D92 字面做两份手抄的期望值,仍会再造一个 A1 —— 两份分家那天两边都不会红。
⇒ lan-edge / transport / 客户端三个 csproj 编译同一份登记表;
  服务端钉键集合(集合相等,不是"包含"),客户端的形状由登记表生成再喂真解析器;
  配元断言枚举登记表逐条核对。认不出的形状一律判 null —— 半份状态比没有状态更坏。

### 裁定⑤ /admin/ping 的 serverCert 接上界面;stderr 那条降级为冗余备份

前者此前全仓无读取方(而注释写着「主机界面据此报警」)—— 吐出来没人读 = 没响。
后者落在 dist/host 那个 .cmd 拉起的控制台窗口里,归 V3。

门禁:**PASS=3379 FAIL=0,通过**(逐套件:lan-edge 20→33、transport 58→69、
client --selftest 从「没跑起来」→ 1935)。净增 58 条断言(成对 8 条 + 元断言 2 条 + 反向 8 条)。
红测 3 次,每次恰好红该红的那 1 条。
★ 已出包并跑 Release 自检(`dist\client-pack`,版本戳 20260806-1817+9bd2fec,非 dirty),
  `SRCMISS=0` ⇒ 那批结构断言真的全跑了,覆盖缺口**已关闭**。
```

### 7.1b ★ 建议顺手记进 `ASSERTION-PITFALLS.md`(本车道只读那份,不自己改)

> **「结构断言在发布产物里读不到源码」** —— 出包当场踩到,10 条同时红。
> 症状:`Assert(src is not null && src.Contains(...))` 在开发树全绿,一出包就整批红
> (发布产物旁边没有源码,`TryReadSource` 全部返回 null ⇒ 条件为假 ⇒ **红,不是跳过**)。
> ★ 而本仓惯例的 `Assert(src is null || ...)` 是**另一头的坑**:它 fail-open,
> 在唯一检查不了的那个形态下无声放行。
> **判据:先问「这条真的非读源码不可吗」** —— 枚举成员用 `Enum.GetNames`、
> 界面词用 `Strings.Get`、措辞用直接调那个属性,这些都能在运行时问出来,
> 而且**比查源码文本更强**(测行为,不测那串字写在哪个文件里)。
> 只有【接线位置与顺序】才是真的非源码不可,那部分按惯例跳过,
> 由 `build-client.ps1` 的 `SRCMISS` 口径把这笔账打出来 —— 是**记了账的缺口**,不是无声放行。
> 护栏:`build-client.ps1` 的哨兵 + SRCMISS 口径(它这次就是当场抓到的)。

### 7.2 `STATE.md` 要改的口径

- A5「三样东西零调用点 / 随包发布的死代码」⇒ **已关闭**;
- **客户端自检基线要更新**:Release/win-x64 = **1935**(SRCMISS=0);
  发布产物 = **873**(原位,SRCMISS=237)/ **869**(仓库外,SRCMISS=239)。
  ★ 三个数**互不可比**,基线行请把口径一起写上 —— 这正是 08-06 那条「口径要逐个数字给」的延续;
- 「设备证书过期时用户能不能自救」从"未验"改为**已钉死:能,但只能靠重新配对**;
- D89 §1.6 那张表标注**已被 D93(本条)更正一半**。

### 7.3 `worklog/2026-08.md` 一段

> **2026-08-06 · V1 证书生命周期接到底。** A5 关闭:`TlsFailure`/`CertLifecycle`/`RenewDeviceCertIfDue`
> 三样东西在客户端里有了调用点,五种归因落到顶栏与设备页。
> ★ 补断言时发现原文案是**反的**:设备证书一旦过期,续签路由结构上就够不着了(实测三条),
> 而文案却在许诺自动续签、劝阻唯一能自救的重新配对 —— 推翻 D89 §1.6 那张表的左半。
> ★ 跨语言成对断言做成一份两侧编译的常量(`WireContracts.cs`),而不是两份手抄的期望值 ——
> 照字面做会再造一个 A1。门禁 1420 → 1444,红测 3 次各红 1 条。

### 7.4 给 **V3**(拥有 `90-ops`)的两条

1. **元断言的数据源**:要枚举"所有跨进程响应契约"时,请直接读 `WireContracts.All`,
   **不要再抄一份** —— 抄一份就是又一个 A1。本车道已按它钉了 4 条契约 + 2 条元断言。
2. **lan-edge 的 stderr 落盘**:`[cert] !!` 横幅目前只进那个控制台窗口。
   结构化那条通道已经接到界面上了,所以这条不再是唯一通路;
   但要让"没开客户端时也留得下证据",需要启动脚本把 Edge 的输出落到一份**持久**日志
   (不是每次启动就删的临时文件)。归 V3。

### 7.5 给 **client/UI** 相关车道的一条(不阻塞)

`DevicesView.cs:618` 显示的是存的 `EdgeUrl`,`SetDial` 改端口后会显示陈旧值(纯外观,
连接本身不受影响 —— 见 D89 裁定④)。要修就换成 `Transport.EdgeUrlFor(p, ep)`。
本车道**没修**:不在判据里,且要让那一处拿到 `IPEndPoint` 属另一块改动。
