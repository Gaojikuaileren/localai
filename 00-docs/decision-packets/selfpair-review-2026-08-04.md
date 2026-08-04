# 主机自配对对抗式复核 —— 判 DO_NOT_COMMIT · 决议草案 · 取号待定

> 日期:2026-08-04
> 来源:接手方会话对上一手【未提交】的 5 个客户端改动跑了一遍对抗式复核(14 个 agent:4 维度审 → 每条发现独立反驳 → 汇总)。
> 并发提示:本包只**新建**文件,未动 `DECISIONS.md` / `PROJECT_PLAN_v3.0.md` / `STATE.md`。写入时 `git status` 仅本包相关文件脏,近三提交同属配对规格线,无并发卷入。取号按惯例以【已进 git 历史】为准,由用户串行并入。
> 性质:草案。**结论已部分落地**(见 §1),其余待用户裁定出路(§5)。

---

## 1. 本次已落地(不必再问)

| 动作 | 结果 |
|---|---|
| **证书名修复单独提交** | `b136f01` —— `ClientTransport.cs`(enroll 后把 `edgeUrl` 换成证书名 `localai-<hubShort>.local`)+ `Selftest.cs` ㉔ 两条断言。复核**零发现**,是这批活里真正的价值:修的是「图形界面配对从来走不完」的真 bug。 |
| **主机自配对整块封存** | `SelfPair.cs`(新增)· `App.xaml.cs` 启动触发 · `Selftest.cs` ㉕ · `DevicesView.cs` 日志 —— **保持未提交**,留在工作区待后续。原因见 §2。 |

★ 部署产物 `dist/client/localai-client.exe`(版本戳 `…dirty`)**仍含自配对整块**,即仍带 §2 的 blocker。要让部署物与已提交状态一致,需从 `b136f01` 干净重编(见 §6)。

---

## 2. 🔴 阻断项(HIGH · 驳不倒 · 我逐行核过核心代码)

**主机自配对每次未配对启动,都会在局域网上敞开一个匿名准入窗口。**

`SelfPair.cs:67` 的 `admin.WindowAsync(true,1)` 由 `App.xaml.cs:145` 在**每次启动(未配对时)无条件**触发。核实过的机制:

- 它打开的是**全局**窗口:`Pairing.cs:74` `OpenWindow` 只置 `_windowOpen`,**无来源、无 CSR 限定**。
- `/pair/enroll` 是**匿名**的,且 `run-lan <ip>` **只把 :8443 绑在网卡 IP 上,回环 :8443 没有监听者**(`HubAdmin.cs:264-266` 白纸黑字)⇒ 主机自己的 enroll 只能走局域网,**逼出全局开窗**。
- 开窗期间门槛只有 `_windowOpen` + `MaxPending=8`(`Pairing.cs:143/146`)。**局域网上任何设备都能匿名 enroll。**

**具体危害(持久 DoS)**:攻击者在开窗那一刻灌满 8 条 pending(pending 存活 5 分钟,`Pairing.cs:169`),主机自己的 enroll 抛 `pending queue is full` → 自配对失败 → **设备永远停在 provisioning**,每次开机被局域网远程拒一次 —— 正是这批改动本想消除的症状。

**三处成文契约同时被违反:**
1. 规格 `pairing-ux-final-spec-2026-08-04.md` §6:主机自配对「**不该开局域网窗口**」,合规机制是 §5.1 的**回环 self-enroll**;
2. `HubAdmin.cs` 里 `WindowAsync` 自己的文档:警告「自启 + 无条件开窗 = 每次开机在局域网上敞一个无人看管的准入窗口」;
3. `lan-edge/Program.cs:268`:启动开窗是**审查发现 [3] 专门拿掉过的**(`OpenPairingWindowOnStart:false`,控制台打印「配对窗口:关闭」)。这批改动把已被安全审查移除的暴露,从客户端这边重新打开了。

**★ 自检 0 FAIL 洗不掉它** —— 测试把这个违规**钉成了「正确行为」**(`Selftest.cs` 相关断言的消息本身就承认「这几秒 :8443 在局域网上也收请求」)。

**★★ 关键:客户端单方面改不成 compliant。** §5.1 的回环 self-enroll 需要 **core 让 hub 在回环上受理主机自己的 enroll**,而现在回环 :8443 根本没监听者。所以只能等 core、或改设计。

> **practical 风险(如实标注)**:自家有线局域网、窗口约 1 分钟、只在未配对开机时开、还需一个**主动在你网里的攻击者**才成 DoS —— 日常自用踩雷概率低。但按项目自己的规矩(规格明禁 + 审查专门拿掉过 + 测试替违规背书),它就是不能提交。

---

## 3. 🟡 中危(记账,不拦已提交的证书修复)

**启动自配对 与 `DevicesView` 那条自配对是两把独立的锁,并发能跑出重复设备记录 + 悬空签名密钥。**

- 新加的启动触发用静态 `SelfPair._running`(`SelfPair.cs:35/43`);既有的 `DevicesView.SelfPairAsync` 用实例 `_selfPairStarted`(`DevicesView.cs:47/766`)。**两锁互不知情**,都对同一个 `TheApp.Hub/HubAdmin` 与同一条 pending 队列动手。
- 复现:开机自配对飞行中(~1–5 秒,含 per-NIC 探测 + enroll/approve/poll)**打开设置页** → `HostSelfCard` 见 `!IsPaired` 且新实例 `_selfPairStarted=false` → 起**第二条**自配对 → 同一机器两条 enroll → **重复的活跃设备记录** + 一把没人引用的 CNG 签名密钥(`Transport.Pair` 每次铸新密钥,只在下次配对回收 profile 引用的那把)。
- 配对最终仍能成,不是安全漏洞,但残留物不自愈。
- 修法:两个入口共用一把锁(让 `DevicesView` 走 `SelfPair.TryOnStartupAsync`),或自配对改开机自动后**删掉 `DevicesView.SelfPairAsync`**。

---

## 4. ⚪ 低危 —— 测试强度债(当前代码是对的,是断言假,不是行为坏)

| 位置 | 问题 | 该怎么钉 |
|---|---|---|
| `Selftest.cs:5179` | **假断言**:`Contains("finally") && Contains("WindowAsync(false)")` 两个子串**分开查**;`finally` 被无关的幂等 `finally { Interlocked.Exchange(ref _running,0) }` 满足。把关窗调用移出 `finally` 到 try 里,断言仍绿而异常路径会**留着局域网窗口开着**。 | `Slice` 出关窗那个 `finally` 块,断言 `WindowAsync(false)` **在块内**。 |
| `Selftest.cs:5183` | **重言式**:`Contains("Log(")` 命中的是 `static void Log(string line)` **方法定义本身**。删光所有失败路径的 `Log(...)` 调用、只留方法定义,编译照过、断言照绿 —— 静默流程可无声变黑箱。 | 切 catch/return 块,断言含 `Log(ex` 等**具体调用点**。 |
| `Selftest.cs:5157/5164`(㉔,**已随 b136f01 提交**) | 只钉了 `var serverName =` 声明,**没钉承重的 `edgeUrl = $"https://{serverName}…"` 那行**。只回退 `edgeUrl` 一行(留 `serverName` 当未用变量,仅 CS0219 警告)就能复活「停在 provisioning」而断言全绿。整体回退才抓得住。 | 补断言 `edgeUrl = $"https://{serverName}` 出现且在 `Trusted()` 之前。 |

★ 前两条在**封存的** ㉕ 里,重做自配对时一并修;第三条在**已提交的** ㉔ 里,是既有小债,值得补一条断言(不阻塞)。

---

## 5. 三条出路(自配对要往下走,须先裁方向)

1. **等 core 补 §5.1 回环 self-enroll**(合规、不动客户端行为语义)—— hub 在回环上受理主机自己 CSR 的 enroll,自配对全程不碰局域网窗口。**动 core 车道,注意并发。**
2. **改设计:自配对退回显式按钮** —— 客户端单方能做,但**偏离规格 §1.1**「自动、不问、不给按钮」,需用户重新裁定。
3. **core 把开的窗口按来源 + 主机自己 CSR 指纹收窄**(§5.1 敲门/接受路径本就要求)—— 折中,仍动 core。

★ 三条里唯一「客户端单方能落」的是 2,但它推翻用户已裁的 §1.1;1 与 3 都要 core。**因此这不是能当晚顺手修的小问题,是要拍板的方向题。**

---

## 6. 真机确认清单(自检证明不了的那件事:GUI 配对现在到底能不能一路走完)

> 自配对的日志在 `%TEMP%\localai-selfpair.log`。

- [ ] **主机**:起 hub/lan-edge,确认控制台绑到网卡 IP:8443 且打印「配对窗口:关闭」。全新未配对启动客户端,**先别开设置**。
- [ ] **主机**:~1–5 秒内应零点击自配对完成。看日志出现 `本机是主机(hub …)` → `enroll 成功 req=… 六词=…` → `自己批准` → `本机已连接自己的中枢。`,**且无异常块**(无 `HttpRequestException … <stacktrace>`)。这就是证书名修复(`b136f01`)对钉住 CA 握上手的**第一个证据** —— 设备不该停在 provisioning。
- [ ] **主机**:开设置 → 设备。确认配对列表里**恰好一条主机**(无重复/幽灵 provisioning 记录),自己那条**无移除按钮**,**无角色检测按钮**。
- [ ] **主机 · 竞态探针**(§3):再次全新启动,**头 1–2 秒内故意打开设置页**。仍须落到单条主机设备、无孤儿 provisioning / 无第二把密钥。出现重复 = 两锁竞态触发了。
- [ ] **主机 · blocker 观察**(§2,预期**复现**,不是通过项):自配对开窗那 ~1 分钟,从**副机**去打 `https://<主机IP>:8443/pair/enroll`,确认它可达/受理 —— 这演示 §6 违规,也是判 DO_NOT_COMMIT 的理由。
- [ ] **副机(P3b 用控制台绕开过的 GUI 路径)**:起客户端 → 设置 → 设备 → 按「开始寻找主机」。这依赖 core 的敲门/接受(§5.1);hub 若报不支持敲门,记录之并退到手填地址的 GUI 路径,好让 `ClientTransport.Pair` 仍跑到。
- [ ] **主机**:敲门条目出现,设备名下显示六个词;确认副机「正在等待主机接受…」屏幕上是**同样六个词**;按【接受】。
- [ ] **副机**:确认配对**端到端走完** —— 状态离开 provisioning,设置切到「当前主机 + 解除匹配」。这是证书名修复补上「P3b 从没走过的 GUI 配对路径」的证据。客户端日志与 `DevicesView` 失败日志里**不得有** `SSL connection could not be established`。

---

## 7. 复核方法留痕

对抗式 workflow:4 维度(证书修复 / 自配对安全 / 断言质量 / 回归)并行审 → 每条发现交独立 agent【尽力反驳,默认不成立】→ 只留驳不倒的。14 个 agent、6 条确认成立、2 条被驳倒(TryReadSource 空守卫跳过 = 既有模式非本批缺陷;托盘 tooltip「变旧」不可观测)。证书修复维零发现。裁决 agent 判 **DO_NOT_COMMIT**。
