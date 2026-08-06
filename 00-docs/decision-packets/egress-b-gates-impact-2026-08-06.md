# 出境闸方向 B · 两道闸的真实形状 + 待裁 1 的爆炸半径 · 决议包 **D?**

> 日期:2026-08-06
> 性质:**实测清单 + 结构更正 + 裁定建议**。承 **D81**「待裁五条」,只补它**没有**的实测与影响面,
> 不重写它已经写过的三条更正与两条实测约束。
> 产出物:本文件 + `90-ops/spikes/egress-direction-b/gate-coverage-probe.py`(一次性勘察)
>
> **取号**:按 D75/D82 办 —— 写作时 `DECISIONS.md` 已提交最大号 **D88**。本包**不占号**,标题 `D?`。
>
> **并发提示**:本包**只新建**文件。`10-core/memory/**` 与 `10-core/gateway/**` 归主执行层,
> 本包**一个字节都没改**(§1 有哈希证明)。未改 `DECISIONS.md` / `PROJECT_PLAN_v3.0.md` / `STATE.md`。
> 基线:本车道已 rebase 到 `main` = `f600461`。

---

## 0. 一句话

**这不是「两道闸拦着」—— 是那两道闸从不在同一条路上,而它们守的那条路今天没有任何人走。**

- **闸一(`_ALLOWED_CALLERS`)与闸二(`Backend.egress`)长在两个不同的函数上,签名互不相交**,
  `unseal_for_prompt` **根本没有 `caller` 这一维** ⇒ 「即使闸一放行、再撞闸二」这个模型不成立(§2);
- **`unseal_for_prompt` 生产调用点 = 0**(唯一像调用的那个名字 `unseal_for_prompt_free` 是**误名**,
  它转的是 `unseal_for_client`)⇒ 两道闸今天都是**休眠脚手架**,与 STATE 记的 `require_trusted_local` 同族(§2.3);
- **D81 决定 1-3 指向的那条「真正的结构性强制」(PLAN §4.6.3「出境 sink 的会话不挂载 `memory.search`」)
  在代码里完全不存在** —— `config/tools.toml` 都没有(§6)。
  ⇒ **上游那条防线是空的,所以回程闸不是「替它背锅」,而是【今天全场只有它一个候选】。**

⇒ 建议:**先裁 D81 待裁 2(给 prompt 出口补维),它是待裁 1 与闸一/闸二能否合流的前提**;
待裁 1 建议裁**出境 sink(fail-closed)**,但**必须同时修掉身份解析失败会静默降档这一条**(§4.3),
否则 fail-closed 会把机主自己一起关进出境侧。

---

## 1. 本轮没有改任何机器状态,也没碰 `10-core`

上一轮我有三处副作用要交账。**这一轮零副作用**,并且可验证:

| | |
|---|---|
| 改过的生产代码 | **无**。`git status --short 10-core/` 为空 |
| `tainted.py` 跑前哈希 | `d689a299df6a201eebec18ba1e9f69e25be754114fa30c0f42d5838b71262ab7` |
| `tainted.py` 跑后哈希 | **同上,逐字节一致** |
| 机器级状态(防火墙 / 注册表 / 账户 / 服务) | **一律未碰**,本轮只有读文件 + 跑两个只读脚本 |

★ **「不许为了验证而真的把 channel-relay 加进 `_ALLOWED_CALLERS`」这条我是这样满足的**:
勘察脚本正常 `import tainted`(读磁盘上的真实定义),**只在本进程内存里**替换
`tainted._ALLOWED_CALLERS`,再把 `test_tainted.py` 守这张表的两条断言逐字抄过来对着跑。
**不落盘、不留痕、进程退出即消失** —— 与「本地改了再改回来」有本质区别:
后者会经过一个**磁盘上真的错了的瞬间**,而那个瞬间可能被别的进程或别的测试读到。

**基线**:`python test_tainted.py` → **75 PASS / 0 FAIL**(绿)。
所以下面「没有任何断言变红」这句话是有意义的,不是在一堆红里看不出来。

---

## 2. ★★ 结构更正:两道闸从不叠加(这一条推翻了任务描述里的模型)

### 2.1 实测:签名互不相交

`10-core/memory/tainted.py` 的四个具名解封点,**闸一与闸二各自只长在一个上**:

| 出口 | 函数(行) | 签名 | 闸一 `caller` | 闸二 `backend.egress` |
|---|---|---|---|---|
| ① 写库 | `unseal_for_storage` :272 | `(t, *, table)` | ✗ | ✗ |
| ② 向量化 | `unseal_for_embedding` :277 | `(t, *, endpoint)` | ✗ | ✗(改判回环地址前缀) |
| ③ **回客户端** | `unseal_for_client` :290 | `(t, *, caller: CallerTier)` | **✓ 闸一在这** | ✗ |
| ④ **进 prompt** | `unseal_for_prompt` :312 | `(t, *, backend: Backend)` | **✗ 没有这一维** | **✓ 闸二在这** |

`inspect.signature` 实测(勘察脚本 §⑤,4 条全 PASS):

```
unseal_for_client(t: 'TaintedText', *, caller: 'CallerTier') -> 'str'
unseal_for_prompt(t: 'TaintedText', *, backend: 'Backend') -> 'str'
★★ unseal_for_prompt 没有 caller 维  ⇒ 闸一不在 prompt 路径上
★★ unseal_for_client 没有 backend 维 ⇒ 闸二不在回客户端路径上
```

### 2.2 所以「闸一放行 → 撞闸二」这个模型要改

任务描述里写「即使闸一放行,只要 Signal 桥被建模成 `egress=true`,S0 正文照样出不去」。
**实测把这句话拆成两句**:

- 把 `channel-relay` 加进 `_ALLOWED_CALLERS["S0"]`,打开的是**出口③(回客户端)** ——
  即 `unseal_for_client` 会把 S0 正文以 JSON 交给那个档位。**实测拿到了正文**(§3 ④);
- 它**完全不影响出口④(进 prompt)**,因为④ 压根不看 `caller`。
- 反过来,闸二(`backend.egress`)**也管不到出口③** —— 回客户端那条路上没有 backend 这一维。

⇒ **两者不是串联的两道闸,是两个不同水池各自的一道闸。**
一个桥要「答上次聊的灯光问题」,走的是**④**(记忆正文进 prompt、模型复述);
而④ 上唯一的判据是 `backend.egress`,桥调 `assistant.fast`(`egress=false`)⇒ **不触发**。
这正是 D81 决定 1-1 描述的失效路径 —— 本包补充的是:**闸一在那条路上从头到尾不在场**。

### 2.3 ★★ 而且这两道闸今天都没有生产调用点

全仓搜 `unseal_for_prompt` 的调用方:

| 调用点 | 是什么 |
|---|---|
| `10-core/gateway/gateway.py:425` | **仅注释/docstring**(「给 `tainted.unseal_for_prompt` 用的后端契约」)。★ 网关全文 **不 import tainted、不 import memory**、不调用任何解封点 |
| `10-core/memory/gate.py:421` `:454` | 调的是 **`unseal_for_prompt_free`**,而它(`gate.py:500`)**转的是 `unseal_for_client`**,并把 `caller` **硬编码成 `TRUSTED_LOCAL`** |
| `test_s4_acceptance.py` / `test_tainted.py` | 测试 |

⇒ **`unseal_for_prompt` 的生产调用点 = 0。** 出口④ 目前是一个**没有人走的门**,
门上那把锁(闸二)因此从未被真正启用。

★ 两个附带发现,都是「名字在说谎」的形状:

1. **`unseal_for_prompt_free` 是误名。** 它叫 "for_prompt",但既不调 `unseal_for_prompt`、
   也不朝模型去;它把正文交给面板确认流程(出口③)。
   ⇒ 半年后有人按名字以为「确认流程走的是 prompt 出口、受闸二保护」——**恰好相反**。
   建议改名 `unseal_for_panel_confirm`(归主执行层)。
2. **它把 `caller` 硬编码为 `TRUSTED_LOCAL`**(`gate.py:502`)。
   面板确认流程因此**无条件自称最高档**,与真正触发它的是谁无关。
   今天面板只绑回环管理面(D48),风险有界;但这是一个**声明式的绕过**,
   一旦面板可被非 trusted-local 触达,闸一对该路径即刻失效。

⇒ **对 P3d 验收句的结论**:
「在外面用 Signal 问『上次聊的那个灯光问题』能答」今天不可能达成,
**但原因不是两道闸拦着,而是记忆到 prompt 的整条路径尚未接线**。
这个区别很要紧 —— **「被闸拦住」是安全态,「路还没接」是空白态**;
把空白态当安全态记账,就是本项目最贵的那种错。

---

## 3. 断言覆盖矩阵:四个无人看守的格子(实测,不是读代码猜的)

`tainted.py:240` 的 `_ALLOWED_CALLERS` 由 `test_tainted.py` 的两条断言守着:

- **断言 A**(`test_tainted.py:150-153`)「新增档位默认无权」——
  `all(tier not in _ALLOWED_CALLERS.get("S2", ...) for tier in (LAN_DEVICE, CHANNEL_RELAY, REMOTE_UNAUTH))`
  ⇒ ★ **只查 `"S2"`**;
- **断言 B**(`test_tainted.py:160-163`)——
  `NO_PLAINTEXT_TIERS`(只含 `RESIDENT_OBSERVER` / `EXT_OPERATOR`)不出现在**任何**值里。

勘察脚本对每个 (档位 × 敏感度) 格子做一次「加进去,两条断言还全绿吗」:

| 档位 | S0 | S1 | S2 |
|---|---|---|---|
| `trusted-local` | (已在表内) | (已在表内) | (已在表内) |
| `lan-device` | (已在表内) | (已在表内) | 有断言守着 |
| **`channel-relay`** | **无人看守** | **无人看守** | 有断言守着 |
| **`remote-unauthenticated`** | **无人看守** | **无人看守** | 有断言守着 |
| `resident-observer` | 有断言守着 | 有断言守着 | 有断言守着 |
| `ext-operator` | 有断言守着 | 有断言守着 | 有断言守着 |

**无人看守的四格:`channel-relay × {S0,S1}` · `remote-unauthenticated × {S0,S1}`。**

★ **比任务描述里说的更宽**:任务只点了 `channel-relay`,
但 **`remote-unauthenticated` × S0/S1 是同一个洞,而且更难看** ——
`remote-unauthenticated` 正是「直连本口的任意远程请求」拿到的档位
(`gateway.py:531`,含 `::1`),把它加进 S0 等于对**任何直连者**开放 S0 正文,
而这同样**一条断言都不会红**。

**实测后果**(勘察脚本 §④,内存内改):

```
基线:channel-relay 取 S0 正文 → 被拒
      「S0 内容不得交给 channel-relay(§4.11.4 结构性隔离)。允许的档位:['lan-device','trusted-local']」
改后:channel-relay 取 S0 正文 → **拿到了** '这是一条 S0 记忆的正文'
```

⇒ 一行、无摩擦、无痕迹、`test_tainted.py` 仍然 75 PASS / 0 FAIL。
这正是我上一轮在 AppContainer 决议包 §4.2 描述过的那个形状,**在另一处原样复现**。

### ★★ 3.1 警告:把 `test_tainted.py` 提进门禁,**并不守住闸一**

(2026-08-06 协调层要求写明,免得本包自己制造一次「看着被守住了、实际没守」。)

同车道的 [test-tainted-gate-promotion-2026-08-06.md](test-tainted-gate-promotion-2026-08-06.md)
主张把 `test_tainted.py` 提进 `fast` 层,让它**每次都被跑**。那条建议本身成立,
但**它与闸一的覆盖是两件不相干的事**:

- 提级解决的是「**这个文件有没有人跑**」;
- §3 这张矩阵说的是「**这个文件里有没有那条断言**」。

而 `test_tainted.py:151` 的那句 `_ALLOWED_CALLERS.get("S2", frozenset())`
**只断言 `channel-relay` 不在 S2** —— 对 **S0 / S1** 那两格,它一个字都没说。

⇒ **即使提级落地、门禁每次都跑它,把 `channel-relay` 加进 `_ALLOWED_CALLERS["S0"]`
依然不会有任何测试变红。** `remote-unauthenticated` × S0/S1 同理。

**两件事必须都做,缺一不可:**

| 做了什么 | 解决了 | **没**解决 |
|---|---|---|
| 只提级(进 fast 层) | 现有 75 条断言从「没人跑」变「每次跑」 | **闸一的四个空格子照旧没人看守** |
| 只补断言(§3 末的全表反向断言) | 四个空格子被守住 | 断言仍落在一个门禁不跑的文件里 |
| **两个都做** | 才真的守住 | — |

★ 只做前者最危险:它会产生一条**看得见的绿灯**(`test_tainted.py PASS=75`),
而那条绿灯**不覆盖**本节刚测出来的那个洞。绿灯比没有绿灯更能让人停止追问。

★ **补断言的正确形状(建议,归主执行层)**:不要逐个档位列举 ——
那还是 denylist。应写成**全表反向断言**:
> 对每个敏感度,`_ALLOWED_CALLERS[sens]` **必须是**一张显式登记表的子集,
> 且该登记表以「(档位, 敏感度) → 一条决议号」的形式给出;出现未登记组合即拒绝启动。

照 `test_local_only_registry.py` 的反向全表断言与 `load_registry` 拒启动那个**已被验证的形状**抄。

---

## 4. 任务 1:待裁 1 的爆炸半径(实测清单)

D81 待裁 1 自陈「fail-closed 答案会改变**本机每个未登记账户**的行为」。**到底是哪些 —— 数出来:**

### 4.1 落 `unregistered-local` 的账户(2026-08-06 `Get-LocalUser` 实跑 × `caller-accounts.toml` 账目)

| 账户 | Enabled | 账目里的处置 | 实际档位 |
|---|---|---|---|
| `Zori Ma` | ✔ | `trusted_local` | trusted-local |
| `ai-mem` | ✔ | `trusted_local` | trusted-local |
| `ai-asset` | ✔ | `LOCAL_DENY` | denied-account |
| `ai-exec` | ✔ | `LOCAL_DENY` | denied-account |
| **`Alle`** | **✔** | 【不登记】✅ 已裁定=访客账户 | **unregistered-local** |
| **`CodexSandboxOffline`** | **✔** | 【不登记】外部 AI 沙箱 | **unregistered-local** |
| **`CodexSandboxOnline`** | **✔** | 【不登记】外部 AI 沙箱 | **unregistered-local** |
| `WsiAccount` | **✘ 已禁用** | 【不登记】 | (不是现实调用方) |
| `Administrator` / `Guest` / `DefaultAccount` / `WDAGUtilityAccount` | ✘ | 账目已记「均 Disabled」 | (不是现实调用方) |
| `ai-vigil` / `ai-ctl` / `ai-op` | 账户**不存在** | `LOCAL_DENY`(预留) | (不存在) |

⇒ **今天真正会落 `unregistered-local` 的启用账户 = 3 个**:
`Alle`(**真人访客账户**)· `CodexSandboxOffline` · `CodexSandboxOnline`。

★ **账目漂移一处(建议,归 ops 车道)**:`caller-accounts.toml` 把
`Administrator / Guest / DefaultAccount / WDAGUtilityAccount` 归为「均 Disabled」,
但 **`WsiAccount` 现在也是 Disabled**,账目里仍单独列着没标禁用。
一行的事,但那张表的价值就在于「谁不在里面、以及为什么」都准确。

### 4.2 `unregistered-local` 今天能碰到什么(逐平面实测清单)

`ROUTE_TIERS` 共 **12** 条:`/health` 是 `public-minimal`,**其余 11 条全是 `authenticated`**。
`unregistered-local` 不是 `remote-unauthenticated`,因此**这 11 条它都进得去**;
收窄发生在各平面的能力表里:

| 平面 | 它有什么 | 依据 |
|---|---|---|
| **chat** | **完整 chat + 流式**(D30「降档不断连」) | `gateway.py:542` |
| GPU | **只有 `read`**;`lease_kinds=∅` · `max_ttl_s=0` · `max_components=0` · `changes_per_min=0` · `max_leases=0` | `gpu_policy.py:127-134` |
| 同步 | **只有 `sync_read`**;`max_batch=0` · `pushes_per_min=0` | `sync_policy.py:54-60` |
| E1 解除 | **没有**(`E1_OVERRIDE_ALLOWED_TIERS = {trusted-local}`) | `gateway.py:525` |
| S2 正文 | **没有**(`_ALLOWED_CALLERS["S2"] = {trusted-local}`) | `tainted.py:243` |
| S0/S1 正文 | ★ **不适用** —— 记忆平面与网关**未接线**(§2.3),这一维今天在请求路径上不存在 | 实测 |

### 4.3 ★★ D81 少写了半个爆炸半径:**身份解析失败也落这里**

`classify_caller`(`gateway.py:528-542`)的每一个身份分支都写成 `if ident and ...`,
⇒ **`ident` 为 `None` 时全部短路,直落 `unregistered-local`**。
而 `caller_identity.resolve_account`(`:194-204`)有 **4 条 `return None`**,
其中一条是**裸 `except Exception: return None`**。

⇒ `unregistered-local` 不只是「未登记账户」,它还是「**解析失败**」的收容档 ——
而解析失败可以发生在**任何**调用方身上,**包括 `Zori Ma` 自己**
(port→PID→WMI GetOwner 这条链有 WMI 抖动、端口表竞态、进程刚退出等多种瞬时失败)。

**这一条直接改变待裁 1 的性质**:
把 `unregistered-local` 判成**出境 sink**,不只影响那 3 个账户 ——
**一次瞬时身份解析失败,会把机主自己的会话静默转成出境侧**
(按 §4.6.3 的设计还会顺带卸掉 `memory.search`),而且**没有任何告知**。

### 4.4 两个方向各自的爆炸半径

| | 判成**出境 sink**(fail-closed) | 判成**本地 sink** |
|---|---|---|
| 直接命中 | `Alle` + 2 个 Codex 沙箱账户的会话变出境侧 | 三者维持现状 |
| **★ 附带命中** | **每一次身份解析失败** ⇒ 机主会话被静默降级(§4.3) | 无 |
| 与 D81 决定 2-1 的合成后果 | naive 响应扫描器**100% 全拦** ⇒ 访客 `Alle` 会看到「什么都被拦」,而当值的人**多半会去调松闸门**(D81 已预警) | 无 |
| 放开了什么 | — | ★ **一个 Signal 桥今天正好落 `unregistered-local`**(D81 决定 1-2)⇒ 判成本地 sink 等于**让 P3d 存在的那个理由本身免检** |
| 与 §4.6.3 的关系 | 会触发「不挂载 `memory.search`」—— 但**该强制不存在**(§6),所以今天这一格是空的 | 同 |

⇒ **不对称是决定性的**:判本地 sink 的代价直接落在 **P3d 要防的那一个场景**上;
判出境 sink 的代价落在**可用性**上,且**可以用一条正交的修复消掉**
(把「解析失败」与「未登记账户」分成两个档位,别共用一个 —— 见 §8)。

---

## 5. 任务 2:每一条 return 的覆盖清单

`chat_completions`(`gateway.py:1381`–`:1650`)的**全部出口**,以及今天有没有出境扫描点:

| # | 行 | 出口 | 载不载上游生成内容 | 今天的扫描点 |
|---|---|---|---|---|
| 1 | 1388 | `JSONResponse` 早退 | 否 | — |
| 2 | 1397 | `JSONResponse` | 否 | — |
| 3 | 1411 | `JSONResponse` | 否 | — |
| 4 | 1474–1477 | SSE `sse()`:3× `yield` + `StreamingResponse` | 否(本方生成的告知帧) | — |
| 5 | 1478 | `JSONResponse` | 否 | — |
| 6 | 1498 | `JSONResponse` | 否 | — |
| — | **1511** | **`e4.scan(_scannable_text(body["messages"]))`** | — | ★ **全函数唯一的扫描点,且是【请求】方向** |
| 7 | 1526–1529 | SSE `sse_e4()`(E4 拦截告知) | 否 | — |
| 8 | 1530 | `JSONResponse` | 否 | — |
| 9 | 1546 | `JSONResponse` | 否 | — |
| 10 | 1593 | `JSONResponse`(后端连不上) | 否 | — |
| 11 | **1604 / 1607** | **`async for chunk in r.aiter_raw(): yield chunk` → `StreamingResponse`** | **是(逐块原样透传)** | **无** |
| 12 | 1617 | `JSONResponse`(上游非 JSON;原文只落服务端日志) | 否 | — |
| 13 | **1630** | **`JSONResponse(content=data)`,`data = r.json()`** | **是(整个响应体)** | **无** |
| 14 | 1639 | `JSONResponse`(`httpx.RequestError`) | 否 | — |

**⇒ 两条真正载有上游生成内容的出口(#11 流式、#13 非流式),今天都是零扫描。** 这就是「方向 B 未做」的行级形态。

### ★ 而且那个唯一的扫描点今天基本不生效

`gateway.py:1510` 把 `e4.scan` 包在 **`if entry.get("egress"):`** 里面。
`registry.toml` 里 `egress = true` 的别名只有两个:`escalate.cloud`(:148)与 `image.concept`(:159);
chat 路由上可达的只有 `escalate.cloud`,而它**紧接着就在 kind 检查处 400**(`gateway.py:1508-1509` 自陈)。

⇒ 组合后的实况:

- 调 `assistant.fast` / `voice` / `deep` / `vision` / `resident`(`egress=false`)⇒ **请求侧 e4.scan 完全跳过**;
- 调 `escalate.cloud` ⇒ e4.scan 会跑,但那条请求**本来就会 400**;
- 响应侧 ⇒ **两条内容路径都没有扫描点**。

**⇒ 一个桥用本地别名走进来,今天是【两个方向都零扫描】。**
E4 现在的位置是「一道装在已经关着的门前面的闸」——
它的价值是**为将来云端别名接入时闸已在位**(注释自陈,这是诚实的),
但**不得**据此声称请求侧今天有出境防护。

### ★ 覆盖清单本身的一条纪律建议

D81 决定 4 的共同必修写着「sink 解析后**每一条** return 都要覆盖」。
上表说明**为什么这条不能靠人眼守**:14 个出口,其中只有 2 个载内容,
而新增一个 `return` 是最容易的事。
⇒ 建议配一条**元测试**:AST 扫 `chat_completions`,
枚举所有 `return`/`yield`,要求每一个都被显式标注为「载内容 / 不载内容」,
**未标注即拒绝启动**。照 `ROUTE_TIERS` + `unclassified_routes()` 那个已被验证的形状抄
(它解的正是同一个问题:穷举 + 未分类即失败)。

---

## 6. 任务 3:PLAN §4.6.3 那条结构性强制 —— **完全没有实现**

`PROJECT_PLAN_v3.0.md:362` 原文:

> 会话建立时若解析出的生成后端 `egress=true`,**该会话的工具池里根本不挂载 `memory.search`**

**实测核实结果:不存在。**

| 要素 | 状态 |
|---|---|
| `config/tools.toml`(D68 说的工具池权威) | **不存在**。`config/` 只有 `caller-accounts` / `eval-thresholds` / `paths` / `retrieval-lexicon` / `vram-budget` |
| 全仓 `memory.search` / `mem.search` 出现次数 | **1 次**,且在 `test_gpu_tool_isolation.py:190` 的**字符串夹具**里(`'[pools.agent_worker]\ntools = ["fs.read", "mem.search"]\n'`)—— 测试数据,不是生产 |
| 「按会话挂载工具池」的实现 | **无**。`agent_allow` 只在 `load_registry` 加载期校验(`gateway.py:357-382`);两层 MCP 决议包 §4.8-6.2 已自陈「运行期无调用点,请求上下文里根本没有 agent 维」 |
| 会话建立时按 `egress` 裁剪工具池 | **无** |

⇒ **对 D81 决定 1-3 的修正**:那条说「真正的结构性强制已写在 PLAN §4.6.3」——
**写在方案书里,没写在代码里。** 因此不能说回程闸「在替它背锅」:

> **今天全场只有回程闸一个候选,而它还没开工;上游那条真正该承重的防线是一张空白。**

这反过来加强了 D81 决定 1-3 的**定位**结论(方向 B 只是第二道防线,不得写成 L5 的代码对应物),
但**削弱**了它的**安慰**成分:D81 读起来像「主防线在上游、这里只是补强」,
实况是**主防线尚不存在**。⇒ 建议把 §4.6.3 的实现列为 P3d 的**并列前置**,而不是背景假设。

---

## 7. 判据表

| 判据 | 闸一 `_ALLOWED_CALLERS` | 闸二 `Backend.egress` | 回程闸(方向 B,待建) | §4.6.3 工具池裁剪(待建) |
|---|---|---|---|---|
| 守哪个出口 | ③ 回客户端 | ④ 进 prompt | HTTP 响应体 | 会话工具池 |
| 今天有生产调用点吗 | **无**(唯一像的那个是误名,转 ③ 并硬编码 trusted-local) | **无** | 未开工 | **不存在** |
| 有 caller 维吗 | ✔ | **✗ 实测** | 待定(D81 待裁 1) | ✔(会话档位) |
| 有 sink/egress 维吗 | **✗ 实测** | ✔ | ✔ | ✔ |
| 断言覆盖 | **4 格无人看守**(§3 实测) | `test_tainted.py` 有正面断言(75 PASS 基线) | — | — |
| 能不能守住「记忆被模型复述后出境」 | 不能(不在那条路上) | **能,但判据是后端不是 sink** ⇒ 本地别名免检 | **不能**(D81 决定 1-3:复述后无正则特征) | **能**(结构上不可表达) |
| 改动摩擦 | **一行,零测试变红** | 需改签名(约 10 个调用点,全在测试里 —— D81 待裁 2 自陈) | 三路设计均未达开工线(D81 决定 4) | 需新建 `config/tools.toml` + 会话装载 |

---

## 8. 建议 + 推翻条件

### 8.1 建议裁定(三条,有顺序)

**① 先裁 D81 待裁 2 —— 它是前提,不是并列项。**
> ★ 裁「要加」之后怎么做,已整理成给主执行层的执行清单:
> [sink-axis-change-list-2026-08-06.md](sink-axis-change-list-2026-08-06.md)
> (含 11 个调用点的精确清单 · 一处必须先定的命名冲突 · 七条新断言 · 提交切分 ·
> 以及一条实测:那七条断言落进去之后**门禁根本不会跑它们**)。
实测已证:出口④ 没有 caller/sink 维 ⇒ **闸二在结构上无法表达「这个答案要发给 Signal」**。
不给它补一维,待裁 1 无论怎么裁都落不到 ④ 上,而 ④ 才是 P3d 验收句真正要走的出口。
迁移成本 D81 自陈有界(约 10 个调用点全在测试里),**且本包实测确认生产调用点为 0**
⇒ 迁移成本实际上比 D81 估的还低:**改签名不会碰任何生产代码路径**。

**② 待裁 1 建议裁「出境 sink」(fail-closed),但捆绑一条正交修复。**
理由是 §4.4 那个不对称:判本地 sink 的代价正好落在 P3d 要防的那一个场景上。
**捆绑的修复(缺它则本建议作废)**:
把「**解析失败**」从「**未登记账户**」里拆出去,给它自己的档位(如 `identity-unresolved`)。
两者今天共用 `unregistered-local`(§4.3),而它们的正确处置**方向相反**:
- 未登记账户 ⇒ 判出境侧是**对的**(它可能就是个桥);
- 解析失败 ⇒ 判出境侧会**把机主自己关进去**,且静默。
不拆就裁 fail-closed,等于给自己埋一个「机主偶发失能且无提示」的雷。

**③ 闸一的四个无人看守格子,补成全表反向断言(§3 末),不要逐个档位列举。**

### 8.2 我**不**建议的

- **不建议**现在开工方向 B 的扫描器实现 —— D81 决定 4 的三路都没到开工线,本包没有推翻它;
- **不建议**把 §4.6.3 的缺失当成「先上回程闸顶一顶」的理由。
  上游是结构性强制、回程是正则第二道防线,**两者不可互相替代**(D81 决定 1-3 已定调,本包只补实测)。

### 8.3 推翻条件

1. 若主执行层给 `unseal_for_prompt` 补了 caller/sink 维 ⇒ §2 的「两道闸不叠加」失效,
   本包 §7 判据表第 3/4 行须重算,建议 ① 消耗完毕;
2. 若 §4.6.3 的工具池裁剪落地 ⇒ §6 的「主防线是空白」失效,回程闸的定位回到 D81 写的「第二道防线」,
   待裁 1 的爆炸半径随之缩小(出境侧会话本就拿不到记忆);
3. 若「解析失败」被独立成档 ⇒ 建议 ② 的捆绑条件已满足,可直接裁 fail-closed;
4. 若 P3d 裁定**不做**外联通道 ⇒ 同 D81 推翻条件 2,方向 B 失去唯一消费者,本包 §4/§5 降为存档;
5. 若 `Alle` 这个访客账户被裁定进 `trusted_local`(或停用)⇒ §4.4 「直接命中」一栏要重数;
6. 若身份解析链改成 fail-closed(解析不到即拒)⇒ §4.3 消失,但会引入新的可用性风险,须另裁。

---

## 9. 覆盖账

### 9.1 我没测到的

| 未测的 | 为什么 |
|---|---|
| **端到端「桥进来 → 取记忆 → 复述 → 出去」** | **这条路径今天不存在**(记忆与网关未接线,§2.3);没有桥、没有 sink 维,无从端到端 |
| D81 决定 2 的两条实测约束(整响应体 100% 全拦 · `\uXXXX` 转义盲区) | **D81 已复测过,本包不重复**。我未独立复现,引用时按「D81 实测」标注 |
| `Alle` / Codex 沙箱账户**真的**发一次请求会拿到什么 | 需要以那些账户身份起进程(要凭据 / 要切换会话),**不在本轮授权范围**;本包给的是**能力表推导**,已逐条标出依据行号 |
| `image.concept`(第二个 `egress=true` 别名)所在路由的出口清单 | 本包只穷举了 chat 路由(`chat_completions`)。图像路由是另一条,**未数** |
| 面板确认流程能否被非 trusted-local 触达 | 关系到 §2.3 那条硬编码的实际风险;要读 `20-client-win` 与管理面路由,**越界,未查** |

### 9.2 我造成的机器状态改动

**无。** 本轮只读文件 + 跑两个只读脚本(`gate-coverage-probe.py` · `test_tainted.py`)。
`tainted.py` 哈希跑前跑后逐字节一致;`git status --short 10-core/` 为空。

### 9.3 门禁覆盖

- **本 worktree 跑 `run-tests.ps1 -Full` 会报「客户端自检 — 没有构建产物」** ——
  worktree 的正常形状,**未去修**(口径同上一轮);
- 本包**没有动 `10-core/`**,`.githooks/pre-commit` 的自检门禁段因此不触发;
- **绝对路径检查**:勘察脚本已用钩子自己的正则扫过,零命中(路径全部由 `Path(__file__).parents[3]` 运行期推导)。

---

## 10. spike 的性质:**(a) 一次性勘察产物,不进门禁**

`90-ops/spikes/egress-direction-b/gate-coverage-probe.py`,口径与上一轮完全相同:

- `run-tests.ps1` 的反向全表扫描根写死 `10-core`、只收 `test_*.py`
  ⇒ 放在 `90-ops/spikes/` 下**不会判红,也永远不会被跑**;
- 本包**不为它申请门禁登记**。它的产出是 §3 那张矩阵,不是一个绿灯。

**★ 但 §3 末与 §5 末各提了一条【值得长期守】的断言**,它们**不该**留在 spike 里:

| 候选断言 | 要落哪 | 归谁 |
|---|---|---|
| `_ALLOWED_CALLERS` 全表反向断言(每个 (档位,敏感度) 组合须显式登记) | `10-core/memory/test_tainted.py` | **主执行层** |
| `chat_completions` 每个 `return`/`yield` 须标注「载内容/不载内容」,未标注拒绝启动 | `10-core/gateway/` | **主执行层** |

两条都落 `10-core/`,才被既有门禁白拿(照 §3.5 落点勘察「落这里才被 `test_imports.py` 自动扫到」)。
**我不动它们**,只在此点名。

---

## 11. 一手来源

- 实测脚本:`90-ops/spikes/egress-direction-b/gate-coverage-probe.py`
- 代码(**只读**,行号以 `f600461` + 本车道 rebase 后为准):
  `10-core/memory/tainted.py`(:211 CallerTier · :240 `_ALLOWED_CALLERS` · :247 `NO_PLAINTEXT_TIERS` ·
  :290 `unseal_for_client` · :312 `unseal_for_prompt`)·
  `10-core/memory/gate.py`(:421 :454 调用点 · :500 `unseal_for_prompt_free`)·
  `10-core/memory/test_tainted.py`(:150-153 断言 A · :160-163 断言 B)·
  `10-core/gateway/gateway.py`(:58 `LOCAL_DENY_ACCOUNTS` · :525 `E1_OVERRIDE_ALLOWED_TIERS` ·
  :528-542 `classify_caller` · :620-651 `ROUTE_TIERS` · :1381-1650 `chat_completions` · :1510-1511 唯一扫描点)·
  `10-core/gateway/caller_identity.py`(:194-204 `resolve_account` 的四条 `return None`)·
  `10-core/gateway/gpu_policy.py`(:127)· `sync_policy.py`(:54)· `registry.toml`(:148 :159)
- 配置:`config/caller-accounts.toml`(账目节)
- 我方决议:[DECISIONS.md](../DECISIONS.md) **D81**(方向 B · 五条待裁)· D30 · D39 · D48 · D68/D69 · D75/D82
- 方案书:[PROJECT_PLAN_v3.0.md](../PROJECT_PLAN_v3.0.md) §4.6.3(:355-365)
- 环境:`Get-LocalUser` 实跑(2026-08-06);`python test_tainted.py` → 75 PASS / 0 FAIL
