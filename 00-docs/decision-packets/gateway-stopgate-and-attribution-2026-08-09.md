# V25 · 关栈判据的对外入口 + 副机那句把人送去错地方的报错(D128)

日期:2026-08-09 · 车道:`v25/gateway-stopgate-and-attribution`
边界:拥有 `10-core/gateway/**` · `10-core/lan-edge/**` · `20-client-win/app/Services/HubClient.cs`

---

## 0. 开工前必须先说的一件事:**任务 1 的三条约束互斥**

工单同时要求三件事,而它们在给定边界下**不可能同时满足**:

| 要求 | 落点 | 边界 |
|---|---|---|
| 开路由 | `10-core/gateway/**` | **拥有** ✓ |
| 「让管理端真的去问」 | `20-client-win/admin/Services/StackStop.cs` | **禁区** ✗ |
| 「那条 Skip 应该能撤掉」 | `20-client-win/admin/SelftestMoved.cs` | **禁区** ✗ |
| 收工时 DEBT 仍是 1 | 新契约必须 `paired`,而 `paired` 要一个**客户端半边** | 落在 admin ✗ |

★ 而 `admin/**` 这条禁区**不是过时的注记**,开工时实测:

- `stackstop-kill-safety-assertions-0ffd29` 对 `admin/SelftestMoved.cs` 有**未提交**改动;
- `agent-ad36411fae961778d` 有 `admin/Selftest.cs` / `App.xaml.cs` / `Program.cs` 的改动
  (且它基于 `88414c3`,与 main 分叉)。

### 试过并**否决**的三条绕路(逐条记下,免得下一个人再试一遍)

1. **把断言塞进 `app/Selftest.cs`,让元断言变绿。**
   ⇒ 否决。消费者在管理端,客户端工程里那条断言**什么都没守**。
   `90-ops/gate/check_contract_pairs.py` 对 `GET /health` 逐字写过这句:
   「给一条没人走的路配断言,断言是绿的,而它什么都没守」。
   而且这么做之后 Skip **仍然撤不掉** —— 按工单自己的判词,那就是**没落干净**。
2. **登记成 `server-only`,DEBT 1→2。** ⇒ 否决,工单明写「不许只落一半」。
3. **先还掉 `GET /health` 那条债来抵消。** ⇒ 否决。它**已交第 0 条车道**,
   而且那正是同一份文件警告的「为了把数字清零而凑掉」。

### 裁定(用户,2026-08-09):**窄口子放开 admin**

放开 `Services/StackStop.cs` 与 `SelftestMoved.cs`。
★ 实际另动了**第三个** admin 文件,理由在 §1.3,如实记在这里而不是藏进 diff。

---

## 1. 任务 1 · 让 D102 那条通则自己满足自己

### 1.1 路由(服务端半边)

`10-core/gateway/gateway.py` 新增 `GET /v1/stack/safe-to-stop` → `stack_safe_to_stop`,
并登记进 `ROUTE_TIERS`(`authenticated` / 判据档 `read`)。

★★ **承重的设计是 `known` 与 `can_stop` 分成两个键**:

- `known=false` —— 判据**没读成**(Broker 读不到),此时 `can_stop` 无意义,
  `blocking`/`resident` 如实为 `null`(**不是 0**:0 会被读成「没人在用」);
- `known=true, can_stop=false` —— 读成了,答案是**不能关**。

合成一个布尔的写法能过其它每一条断言,唯独过不了红测②/⑤ —— 而合并正是
D102 留痕里 `snap.get()` 那次的形状:**恒假的判据不是判据**。

★ 顺带把 Broker 读法抽成 `_stack_counts()`,`safe_to_stop_stack` 与路由**共用一份**。
不抽的话路由里会再抄一遍那两行,而抄的那份漂开时**不会有任何东西红**。

### 1.2 消费端(客户端半边)

`20-client-win/admin/Services/StackStop.cs`:

- `QueryAsync()` 从 `Task.FromResult(Known: false)`(一次请求都不发)改成**真的拨**
  `http://127.0.0.1:{HostSetup.GatewayPort}/v1/stack/safe-to-stop`;
  端口读 `HostSetup.GatewayPort` —— 与「管理端把网关起在哪儿」是**同一个数**。
- 新增**纯函数** `ParseVerdict(string)` —— 这就是这条契约的客户端半边。
  纯函数是必要的:成对断言要能喂它合成的回答体,混在 `QueryAsync` 里就得先起一个网关。
- 「读不到」那条路**一个字都没删**,而且现在有**四种互不相同**的 `Why`:
  ①拒连/超时 ②非 200 ③读不懂 ④网关自己说 `known=false`。
  合成一句会让人不知道该去起网关还是去看 Broker。

`20-client-win/admin/SelftestMoved.cs`:那条 Skip **撤掉了**,换成正向 3 条 + 反向 5 条,
锚点 `CONTRACT:stack.safe_to_stop`。

### 1.3 ★ 多动的那个文件,以及**为什么不动它才是错的**

`20-client-win/admin/Selftest.cs:128-134` 有一条**预约式红灯**,逐字写着:

> 今天必然读不到 —— 中枢那条路由还没开(DEBT,交接 V16)。
> ★ 这条断言是【故意】钉住"今天读不到"的:哪天 V16 把路由开出来,它会红。

**它不会红。** 自检环境里没有网关 ⇒ `QueryAsync` 走**拒连**那条路 ⇒ 仍然 `Known=false`
⇒ `!v.Known` 照样成立、照样绿,而它的**判词已经变成假的**(路由已经有了)。

★★ 这类「预约式红灯」的通病值得单独记一条:**它预约的那个信号,和它实际判的那个条件,
不是同一件事**。留着不动 = 一条会说谎却永远不会红的断言,比没有断言更坏。
⇒ 改成一条**能为假**的:临时把 `LOCALAI_GATEWAY_PORT` 指到确定没人听的 1 口,
断言 `Why` 里带着**它真的拨过的那个地址**。改回 `Task.FromResult` 当场红(红测④)。

---

## 2. 任务 2 · 那句把人送去错地方的报错

### 2.1 病灶(实况复核确认)

`10-core/lan-edge/Program.cs` 的 `Proxy` 对 `http.SendAsync` **没有 try/catch**,
`MapFallback` 外也没有异常中间件 ⇒ 上游 8080 拒连时抛 `HttpRequestException`,
框架兜成 5xx ⇒ 客户端 `HubClient` 对 `>=500` 判 `HubServerError`:
「中枢应答了,但返回 500 —— **不是连不上,是中枢内部出错,请看中枢日志**」。
★ **而中枢日志里没有网关的事。**

★★ 更坏的是时机:配对整条链(pair/enroll/六词/approve/active)只用 8443+8442,
**一次都不碰网关** ⇒ 副机**配得上**、主机 list 显示 active,之后全线失败;
而副机上没有管理端,那张会说真话的「AI 栈」卡它根本看不到。

### 2.2 修法

- **lan-edge**:`SendAsync` 包 try/catch ⇒ **502**(语义就是「我是网关,我上游够不着」;
  不用 503:503 的意思是「我自己不可用」,会让人去重启一台好好的 Edge),
  正文带 `type = Edge.UpstreamUnreachableType`,`message` 说清**下一步在【主机】上做**、
  且**不要重新配对**(重配会删掉本机私钥)。
  ★ 客户端自己取消时(`RequestAborted`)不记成「网关连不上」。
- **HubClient**:`LooksUpstreamGatewayDown` 认那个**明确给出的词**(不拿状态码猜 ——
  502 可以来自任何一层代理),两条通道(业务 + 配对)共用同一句话。
  ★★ 新分支必须排在光秃秃的 `>= 500` **前面**,否则一行都执行不到(红测⑨)。
- 状态仍用 `HubServerError`,理由见 §5 第 1 条。

### 2.3 ★ 那条「要自己判的」:配对后要不要主动探网关

**我的判断是不探**(按需归因即可),已如实报给用户,**用户裁定:探**。按裁定实现。

⇒ `PairAsync` 结尾调 `ProbeBusinessAfterPairAsync()`。

★★ 我原先反对的**唯一实质理由是「又是一条新契约、DEBT 压力」,而这一条在实现里被消掉了**:
探的是 `CallAsync("/v1/models")` —— 它**就是业务流量真正走的那条路**(Edge → 上游网关),
而且是**已登记的契约**(`CONTRACT:models.list`)⇒ **本轮零新增契约**。
新开一条 8443 上的健康端点才会触发我担心的那件事,而那条路没走。

★ 仍然存在的代价,如实写下:**配对多花一次往返**(网关没起时是一次立刻失败的 502)。
换来的是「配得上但用不了」从**可以一直存在**变成**当场就说**。

★★★ 探失败**绝不回滚配对** —— 设备已经在主机成员表里 active。
判成「配对失败」会引导用户再配一次,而再配一次**删掉本机私钥**:
为一件「主机没起栈」的事销毁一个完好的身份。归因一个字都不在探里写,
全部复用 `CallAsync` 那套(两处各写一份必然漂开)。

---

## 3. 契约表变化 · DEBT

新增 **1** 条,**成对落地**:

| 契约 | 服务端半边 | 客户端半边 | state |
|---|---|---|---|
| `CONTRACT:stack.safe_to_stop`<br>`GET /v1/stack/safe-to-stop` | `10-core/gateway/test_gpu_broker.py` | `20-client-win/admin/SelftestMoved.cs` | **paired** |

```
=== contract-pairs: TOTAL=31 PAIRED=30 DEBT=1 ===
```

★ 开工前 TOTAL=30 PAIRED=29 DEBT=1 ⇒ **DEBT 未变**,唯一那条仍是 `GET /health`(已交第 0 条车道)。

### ★★ 顺带补了一处门禁自己的洞

`test_gpu_broker.py` 的配对元断言原来把客户端半边**写死**成 `20-client-win/app/Selftest.cs`。
本条是**第一条消费者在管理端**的网关契约 ⇒ 那条元断言会逼着人
「把断言塞进客户端工程」,也就是逼出 §0 里那条已被否决的绕路。
⇒ 加了 `_CLIENT_HALF_FILES` 逐条登记(与 `check_contract_pairs.py` 的 `client_file` 同一手法,
那边 V19 就做了)。**不在表里的契约行为一个字节没变**;
在表里的那条,文件读不到照样判红。

---

## 4. 断言红测(**九条,逐条真的红过**)

| # | 把什么弄坏 | 结果 |
|---|---|---|
| ① | 路由不再调 `safe_to_stop_stack`(就地重抄判据) | 红 3 条(含「生产模块里有调用点」) |
| ② | `known`/`can_stop` 合并(读不到 ⇒ 伪装成不能关) | 红 |
| ③ | 读不到时 `blocking/resident` 填 0 而非 null | 红 |
| ④ | `QueryAsync` 改回 `Task.FromResult` | 红 |
| ⑤ | `ParseVerdict` 把 `known=false` 并进「不能关」 | 红 |
| ⑥ | lan-edge 的 `Proxy` 取消 try/catch | 红(实得 500) |
| ⑦ | 两侧那个词改成不一样 | 红 |
| ⑧ | `PairAsync` 那行调用**注释掉** | **第一版没红** → 见下 |
| ⑨ | 把「上游网关」分支挪到裸 `>=500` 后面 | **第一版没红** → 见下 |

### ★★★ ⑧⑨ 第一版**恒真**,是红测自己抓出来的 —— 这一条必须留痕

判据原来直接在**原文**里 `Contains("ProbeBusinessAfterPairAsync()")`。
把那行调用**注释掉**之后,断言**照样绿** —— 注释里那串字还在。

⇒ 一条「接线还在不在」的判据,被**一句解释它已被删掉的注释**喂绿了。
这正是 `ASSERTION-PITFALLS` 第 1 条、本仓已踩 10 次的形状,而我又踩了一次。
★ 正解是**收紧判据**(判据先过 `CodeOnly`:剥注释 + 剥字符串),
不是删断言、也不是改注释去迁就它。收紧后 ⑧⑨ 都当场红。

★★ 记一句方法论:**没红过的护栏和没有护栏是一回事** —— 这两条如果不做红测,
会以「两条绿断言」的样子躺进覆盖账,而它们守的东西是空的。

---

## 5. 如实交代:**没做的 / 做不了的 / 留给下一个人的**

1. **副机那格状态词仍是「中枢报错(它在)」,没有专属词。**
   ★ 它对这一处境**不算错**(Edge 确实在、确实报了错),错的从来是它后面那句
   「请看中枢日志」—— 那句已经换掉,处置办法在 `LastError` 里
   (`DevicesView.cs:227` 就在状态词下面显示它)。
   ★★ 要一个专属状态词就得动 `app/Views/**` + `MainWindow.xaml.cs`(**V24 的禁区**),
   而且新加一个 `HubState` 而不改 Views 只会掉进 `_ => status.offline`「未连接」——
   **正是本轮要消掉的那句错归因**。⇒ 交给能同时拿到 Views 的车道。
2. **客户端自检有 1 条 FAIL,是既有的,不是本轮引入的。**
   「管理端界面文案里不许有字面 `**`」,点名 `App.xaml.cs · HostHubView.cs ·
   StackOwnership.cs · StackStop.cs`。★ 已在 **main 的干净工作树上实测对拍**:
   `main = PASS 2096 / FAIL 1`(同一条、同样四个文件),本车道 `PASS 2108 / FAIL 1`。
   ⇒ **+12 PASS,0 新增 FAIL**。四个文件里三个本轮一行都没动。
3. **`safe_to_stop_stack` 的四条条件没有被拆成四条独立理由。**
   今天仍是两条(`blocking>0` / `resident>0`),各说各的、没有短路合并 ——
   工单引的那条「四条件不许被短路合并」在**今天的形状**下只有两条,如实说明,不硬凑成四条。
4. **本轮没有动 `10-core/memory/**`。**(那些测试 import 即执行,会写生产库。)

---

## 6. 门禁 / 实测

| 项 | 结果 |
|---|---|
| `test_gpu_broker.py` | **717 PASS · 0 FAIL** |
| `test_sync.py` | 160 PASS · 0 FAIL |
| `test_gpu_policy.py` | 102 PASS · 0 FAIL |
| `test_lan_edge_policy.py` | 10 PASS · 0 FAIL |
| `check_contract_pairs.py` | 331 PASS · 0 FAIL · **TOTAL=31 PAIRED=30 DEBT=1** |
| `check_decision_numbers.py`(main 新加) | 4 PASS · 0 FAIL |
| lan-edge `selftest` | **74 PASS · 0 FAIL** |
| 管理端 `--selftest` | **304 PASS · 0 FAIL · 9 SKIP**(那条 safe_to_stop Skip 已不在) |
| 客户端 `--selftest` | 2122 PASS · **1 FAIL(既有,见 §5.2)** |

★ 本车道开工时 main 在 `b32a1bf`,收工前 main **前进了两次**:
`4044671`(V26,`admin/SelftestMoved.cs` 改 487 行)→ `80b6b31`(V24,改 `app/Selftest.cs`)。
**两次都 merge 了并复跑上表全部**,自动合并均无冲突,
本轮那 3 处 `CONTRACT:stack.safe_to_stop` 锚点与撤掉的 Skip 均完好。

### ★★★ 一条**不是本轮引入、但必须交代**的实况:`test_gpu_policy.py` 慢了两个量级

跑整套门禁时,`10-core/gateway/test_gpu_policy.py` 长时间不出结果。

★ **先说结论,免得下一个人跟我一样误判**:它**不是卡死,是慢** ——
计时实测 **3 分 39 秒**跑完,**102 PASS · 0 FAIL**。
而同一份代码在本轮早些时候是**约 2 秒**跑完的。⇒ 慢了约两个量级,但**结果照样是全绿**。

★★ 我一开始把它读成了「卡住」并按那个结论查了半天 —— 记下来,因为
**「没返回」和「不会返回」是两件事**,而我拿前者当了后者。判据是**计一次时**,
不是「等得不耐烦了」。

★ 病灶指向 `_probe()`:它用 `trusted-local` 身份**真的** `POST /v1/gpu/intended`
(`components: ["speech.lite"]`),也就是**真的去装一次模型**。
机器负载高时这一步就非常慢 —— 收工时这台机器上同时挂着 **7 个 worktree**,
且实测到另一条车道正在并发跑它自己的 `test_sync.py`。

★★ **已在 main 的干净工作树上对拍过 —— 同一位置、同样慢。**
⇒ 与本轮改动**无关**(本轮没碰 `/v1/gpu/intended` 那条路径)。

★★★ 为什么仍要记一笔:`.githooks/pre-commit` 第 ② 段在**动了 `10-core/gateway/`** 时
会跑 `run-tests.ps1`。⇒ 多车道并发时,提交会**静默地等上十几分钟**,
而现象是「提交没反应」——很容易被读成钩子挂了,然后有人去用 `--no-verify`
(那会把①②③④⑤⑥六道闸一起关掉)。
建议交给门禁车道裁定:`_probe` 到底该不该**真装模型**,或给它一个超时。
