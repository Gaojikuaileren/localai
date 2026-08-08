# `D?` · V19:phase 2 迁移的六条前置**已解**,外加**第七条**(它长在契约门禁自己身上)

> 车道:V19(worktree `v13-host-loopback-tier-9bc9fb`,分支 `claude/v19-phase2-prereqs-d227dd`)
> 2026-08-08 · 基线 `main@8225735`
> **性质:前置清障 + 一条新发现 + 一件交回用户裁。本车道那 3100 行迁移代码一行都没搬。**
>
> ★ 行号全部是**今天实测**。地图(`admin-app-phase2-migration-map-2026-08-08.md`)记的是
>   `main@88414c3`,已经漂了 —— 例如 `HostSetup.cs` 那时 963 行,今天 978 行;
>   `CLIENT_ROOTS` 那时 :335,今天 :349;「0 次 = 被删了」那时 :568,今天 :586。
> ★ D 号待并入 `DECISIONS.md` 那一刻分配(**当前最大 D113** —— 合并 main 时又漂了一次:本包写完时是 D111,而 main 上 `daa9a0e` 取了 D112/D113;地图里写的 D107 更早就过期了)。

---

## 0. 一句话

**六条全解**,但其中两条的实情比地图写的更糟(B2 已经坏了,不是"将会坏");
另外**发现第七条** —— 契约门禁那条承重判据 `0 次 = 这一半被删了`,
因为用**裸子串**计数,对**四条**契约**结构上永远不会红**。

---

## 1. 六条逐条

| # | 事 | 结论 |
|---|---|---|
| A1 | 契约门禁认得 `admin/` | ✅ 解 · 红测 7/7 |
| A2 | `RunCapturedAsync` 被切成两份 | ✅ 解(提成 `Services/ProcRun.cs`,两个 csproj 编同一份) |
| A3 | 管理端 csproj 缺 `ConfigDialog` 等 | ✅ 解(链得动的 5 份已链)+ **7 份链不动的逐条交代**(见 §3) |
| B1 | 左下角「主/副」 | ✅ 解 · 口径已改 · 红测 7/7 |
| B2 | `KnownDevices` 结构性恒空 | ✅ 解 · 红测 5/5 · ★ **它今天就已经是空的** |
| B3 | `ShowApprovalDialogAsync` 是死代码 | ⚠️ **半解**:假绿已清、判据已改钉活代码;**弹窗去留交回用户裁** |
| ★ 7 | 契约门禁的锚点计数用裸子串 | ✅ 解 —— **本车道发现的,不在地图上** |

---

## 2. ★★★★ 第七条:`0 次 = 这一半被删了` 对四条契约**结构上不会红**

`check_contract_pairs.py` 的 `_anchor_count` 原本是 `text.count(cid)` —— **裸子串**。
而登记表里有**四对前缀关系**:

```
CONTRACT:cert.admin.ping     ⊂ CONTRACT:cert.admin.ping.servercert
CONTRACT:cert.admin.devices  ⊂ CONTRACT:cert.admin.devices.item
CONTRACT:cert.admin.pending  ⊂ CONTRACT:cert.admin.pending.item
CONTRACT:gpu.intended        ⊂ CONTRACT:gpu.intended.blocking
```

⇒ 把**父契约的那一半整个删掉**,只要子形状那几行还在,`.count()` 照样 ≥ 1。
实测:`cert.admin.ping` 在 `Selftest.cs` 里 6 次命中,**其中 3 次只是 `.servercert` 的前缀** ——
也就是说那条承重判据的一半是**借来的**。

★★ 最刺眼的是:这正是本文件**第 ⑤ 组已经写明并已经防住**的那条前缀陷阱
(「判据要**恰好**这条路径,不能是它的前缀:`/v1/gpu/lease` 是 `/v1/gpu/lease/renew` 的前缀」)——
**在消费者那一栏防住了,在承重的锚点那一栏没防**。
而且 `gpu.intended` 的 note 里白纸黑字写着「注意它与 `gpu.intended.blocking` 是**前缀关系**」:
**知道**这件事,和**判据里防住**这件事,是两回事。

**修法**:锚点计数加尾边界(契约号后面不许再跟 `[a-z0-9_.]`),并配两个方向的自测。

★ **它是怎么被发现的**:A1 的红测说「删掉 `cert.admin.ping` 的锚点,门禁必须红」,
而门禁是**绿的**。当时有两条路 —— 把期望值改宽收工,或者去查。
详见 `ASSERTION-PITFALLS` 第 15 条那条推论。

---

## 3. A3 实测:「除 `ConfirmDialog` 外还缺哪些」

★ **判官是 `dotnet build`,不是 grep。** 第一版用正则算出 15 个候选,**4 个是误报**
(`Snapshot` / `Kind` / `Page`,以及 `MainWindow` 的一部分)。逐个试链后:

### ✅ 链得动且闭合(已加进 `localai-admin.csproj`)

| 文件 | 谁要它 |
|---|---|
| `Views/ConfirmDialog.cs` | 批准弹窗 / 删除确认;**六词判词的载体** |
| `Services/ProcRun.cs` | A2 提出来的那份 |
| `Services/BuildInfo.cs` | `DevicesView:624` |
| `Services/VramBudget.cs` | `ComponentPicker:271` |
| `Services/HubDiscovery.cs` **+** `10-core/identity/HubId.cs` | `HubAdmin:531/545` 与 `DevicesView:1172` **两边都用**;★ **成对才闭合**,单链 `HubDiscovery` 会 `CS0234` |

### ❌ 链不动 —— **不是 csproj 缺条目,是迁移本身要先裁的依赖**

| 文件 | 链进去之后缺什么(实测编译错误) |
|---|---|
| `Services/StorageUsage.cs` | `ChatCenter` · `ClientStore` · **`App`**(客户端的 App 单例) |
| `Services/HubGpu.cs` | `ClientTransport` · `HubClient` · `RunningTask` · `TaskCenter` |
| `Services/MemoryCenter.cs` | `ProjectScope` → 链 `ProjectCenter` 之后还缺 `MemberContext` |
| `Services/SessionArchive.cs` | `ChatMessage` → 链 `ChatCenter` 之后还缺 `HubClient` · `SyncClient` · `SyncItem` · `ChatOutcome` · `ProjectScope` |
| `Views/ProjectUi.cs` | `Project` · `ProjectCenter` · `ProjectScope` · `ProjectStatus` · `AiPermission` · `ChatCenter` |
| `MainWindow`(类型本身) | `HostSetup` / `HubAdmin` 里三处 `(Application.Current.MainWindow as MainWindow)?.RefreshStatus()` —— 管理端没有 `MainWindow`,这是**跨窗口回调**,要断掉或改写 |
| `HubClient` / `HubState` / `HubDevice` | `DevicesView` 用(`HubState.Online` 那一段是活的)—— 连着 §2.4 那一族一起裁 |

⇒ **这七条不该靠"再链一个文件"解决** —— 那样会把整个客户端拖进管理端。
它们是迁移要做的**切分决定**,请在动手前逐条裁。

---

## 4. B2:比地图写的更糟 —— **今天就已经断了**

地图说「删 §2.4 那组死代码**会**连带切断 `CacheDevices` 的唯一写入点」。
实测:`CacheDevices` 的唯一调用点在 `DevicesView.RenderDevices` 里,
而 **`RenderDevices` 一个调用方都没有**(全仓,排除 `bin/obj`:只有它自己的声明,
加上自检里拿它当 `Slice` 边界的一处)。

⇒ `HubClient.KnownDevices` **今天**就是结构性恒空,
`ProjectEditor.MachineOptions()`(项目「文件夹所在机器」下拉)**从来只有「本机」一项**。
而 `Selftest.cs:2963` 一直是绿的 —— 它 grep 的是 `HubClient.cs` 里的两个**名字**(声明),
判词说的却是「远程机器清单只来自真的拿到过的设备表」。**测的和说的不是一件事。**

**已做**:写入点挪到 `LoadDevicesAsync`(活路径);删掉 `RenderDevices`
(它是活路径的**旧副本** —— 少了「自己不能解除自己」D47、少了指纹短码、少了 provisioning 那一档,
接回去用会**悄悄退回**三条已修缺陷);断言换成四条能为假的。

★ `HubClient.ParseDevices` 现在只剩自检在调 —— **本轮不动**,它连着 §2.4 那一族。

---

## 5. B1:那一格的**新文案**

「装了管理端」≠「中枢正在跑」。摘掉回环探测之后,客户端**没有**中枢跑没跑的活证据了,
所以文案必须说清它答的是什么、以及去哪儿看它不答的那件事。

正文(ToolTip):

```
本机角色:主机
依据:<RoleVerdict.Why 原文>

★ 这一格说的是【这台机器的角色】,依据是安装事实
  (装没装管理端 · 铸没铸中枢身份 · 中枢地址解析到谁)。
★★ 它【不代表中枢正在跑】—— 管理端装着、身份也在,而网关/Edge 没起来时,
  这一格照样写「主机」。想知道中枢跑没跑,看右上角那颗状态点(它是真的连过)。
```

判定还没落定时(`App.Boot` 还是 null):

```
本机:判定中…
开机角色判定还在跑 —— 判完这一格就会写定。现在不显示结论,是因为这时候说什么都是编的。
```

★ 「(推测)」后缀去掉:`RoleVerdict` 不是关于运行状态的猜测,而**依据原文**已经摆出来了,
比一个"推测/确认"的二值标签说得多。弱化字色改用在「判定中」那一档。

★★ 取舍如实记下:**客户端从此没有「中枢在这台上跑」的活证据**。
这是纪律②(客户端不留运行期角色分支)换来的,不是漏掉的。

---

## 6. ★ 交回用户裁:**批准弹窗要不要回来?**

`ShowApprovalDialogAsync`(`DevicesView.cs`,36 行)今天**零调用方**。
自动弹窗那条路已废,理由是 enroll 是**匿名**的 ⇒ 自动弹窗等于
「局域网上任何人都能触发的动作」,由对方的到达时机决定你屏幕上跳出什么。

**两个选项**(本车道**不替你选**):

* **A · 删掉** —— 现在就是这个行为,`PendingRow` 上的批准/拒绝按钮已经完整
  (六个词摆出来 + 「逐字核对过了,批准」+ 拒绝 + 409 归因)。
* **B · 接回来**,但只能由**人主动点某一条**才弹(绝不由轮询触发)。
  好处是弹窗能把六个词摆得更大、把过期秒数说清楚。

★ **无论哪一个,判据都已经就位**(不用改断言):
每一个 `ApproveAsync` 入口之前都必须有六词【逐字】比对(自配对那条登记例外除外),
而且**轮询里不许弹窗**。

★ 在你裁定之前,那个方法**留着**并已标清「当前不可达 · 不许原样搬进管理端」——
此前有 3 条断言钉在它上面,**原样搬过去等于把一条假绿搬进新工程**。

---

## 7. 本包**不**声称

* **不声称**搬了任何一行迁移代码 —— 本车道**一行都没搬**;
* **不声称** A3 那份「链得动」的清单在依赖变化后仍然准 —— 它是 `main@8225735` 的实测值,
  唯一可信的判据是**两个 csproj 真的编得过**;
* **不声称**第七条之外没有第八条 —— 只声称这七条是**这一轮真的验过的**;
* **不声称** B3 已解 —— 假绿清掉了,**产品决定还欠着**。

---

> V19 · 2026-08-08 · 基线 `main@8225735`
> 红测记录:A1 7/7 · B1 7/7 · B2 5/5 · B3 6/6(每一条红的**原因都不同** ——
> 一条恒红的判据和一条恒绿的一样没用)
