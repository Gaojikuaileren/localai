# `GET /v1/sync/snapshot` 的处置 —— 接上,而且它补的不是重连(V6 · 2026-08-06) · `DRAFT-D: none`

> **DRAFT-D: none** —— **第 0 条车道 2026-08-06 并入 V4 时明裁不取号**,
> 原话在 `DECISIONS.md` **D98** 段里逐字留着:
> 「★ **V6 车道的工作今天没有取号** —— 它只产出了本包,而那一份是**待用户裁定**的
>   处置建议,按 D91 的同源理由不取号。**没有为了凑一个号去编一份包。**」
> ★ 2026-08-15 补这行豁免:**那句裁定一直在,只是写在别处** ——
>   而判据只读这份包自己。**写在别人身上的理由,等于没写。**


> 车道:**V6 · 契约欠债:同步 + 对话切片**
> 任务原话:「这条**要么接上、要么撤掉**,不许原样留着补一条成对断言了事 ——
> 给一条没人走的路配断言,断言就是绿的,而"重连后对不齐"照旧。」
> D 号:草案写 `D?`,并入那刻取号

---

## 0. 结论先说:**简报里的两条前提都不成立,而真问题在别处**

| 简报说 | 实测 | |
|---|---|---|
| 「客户端一个字都没读它 ⇒ **无消费者**」 | **有三个消费者,只是都不在客户端**:`90-ops/debug/doctor.py:404`(⑦ 环)· `90-ops/debug/probe_sync.py:104` · `10-core/gateway/test_sync.py:193` | ⇒ **「撤掉」这条路基本被堵死** |
| 「重连之后拿什么对齐?…如果答案是'靠下一次推送',那就是丢过的更新永远补不回来」 | 答案**不是**"靠下一次推送":`/v1/sync/events` **首帧就是全量**(`gateway.py:1441` 的 `snapshot()`,`since_rev=0`),客户端还专门等它吃完才推 | ⇒ **重连这条路径没有洞** |

**但「丢过的更新永远补不回来」这句话是对的 —— 它只是发生在另一条路径上:**

> **流没有断,只是丢了一帧。**
> 首帧之后服务端发的全是**增量**(`snapshot(since_rev=last)`,`gateway.py:1450`),
> 而客户端 `SyncClient.Absorb` 的 catch 里写着
> **「整帧丢掉,下一帧会带全量」—— 那是一句错话**。
> 丢一帧 = 那批更新**永远补不回来**,而且**没有任何东西会红**:
> 订阅还活着、`generation` 还在涨、界面显示「已同步」。

⇒ 处置:**接上**。而且**只接在这一处** —— 接成"重连也走它"就是把首帧全量那条路重复一遍,
那种断言恒绿,正是任务里点名不许干的事。

---

## 1. 勘察:今天重连后**事实上**是怎么对齐的

两个方向都有,而且都不经过 `/v1/sync/snapshot`:

| 方向 | 靠什么 | 证据 |
|---|---|---|
| **拉**(别人改的落到我这儿) | `/v1/sync/events` **首帧全量** | `gateway.py:1441-1443`:`snap = store().snapshot()` → `yield "event: snapshot\ndata: …"` |
| **推**(我改的送出去) | `ReconcileAsync()` 重推**当前全部合格数据** | `SyncClient.cs:197`;`FullSet` 由宿主注入 |

★ 而且顺序是**有意**的:客户端 `_pullFirst` 挂在**第一帧 data 到手之后**,不是"连上之后"。
注释里写着原因,而那个原因是硬的:

> A 删掉一条共享待办 → B 一直关着机 → B 开机**先推**本地那份 → **删掉的东西在 A 那边复活了**。

⇒ 必须先吃完带墓碑的那一帧全量再推。**这条已经做对了,本车道一个字都没动它。**

---

## 2. 真问题:首帧之后全是增量,而客户端以为下一帧还会带全量

```python
# gateway.py:1441   首帧
snap = sync_store.store().snapshot()              # since_rev=0 ⇒ 全量
# gateway.py:1450   之后每一帧
cur = sync_store.store().snapshot(since_rev=last) # ⇒ 增量,且 last 随即前移
```

```python
# sync_store.py     since_rev 的语义(逐字)
out[k] = [r for r in self._cache[k].values() if int(r.get("rev", 0)) > since_rev]
```

```csharp
// SyncClient.cs   改之前
catch { /* 半份解析出来的比没有更危险 —— 整帧丢掉,下一帧会带全量 */ }
```

**三段拼起来就是缺陷本身。** 而它的形状正是这条车道要防的那种:

- 不报错、不断连、不掉线;
- `generation` 照涨(下一帧带着新的 generation);
- 界面 `IsLive` 为真 ⇒ 显示「已同步」;
- **丢掉的那几条待办/消息,在双方都不再改动它们的前提下,永远不会再出现。**

★ 这条**不是**理论风险:`Absorb` 里那个 `catch` 是**真的会走到**的 ——
同一个文件上方就记着一次实机事故(`JsonElement` 跨 `JsonDocument` 生命周期,
每条都抛 `ObjectDisposedException` 而被逐条 `catch {}` 吞光,
表现是"帧收到了、generation 在涨、本地一条记录都不多")。
**那次的表现,和丢帧的表现,一模一样。**

---

## 3. 处置:接上(已落地),并且**判据不是"解析失败"而是"断层"**

只在 catch 里补是不够的 —— 那只盖住"我这边解析炸了"。
真正的判据用帧**自带的 `since_rev`**:

```csharp
internal static bool FrameContinues(long haveGeneration, long frameSinceRev) =>
    frameSinceRev == 0                      // 全量帧:任何时候都能接
    || frameSinceRev <= haveGeneration;     // 增量帧:必须接在我手上这份之后
```

> 正常时下一帧的 `since_rev` 恰好等于我手上的 `Generation`;
> **大于它**,就说明中间有一帧我没吃到。

⇒ 两个触发点都指向同一个恢复动作 `PullFullAsync()`:
`GET /v1/sync/snapshot`(`since_rev=0`,**全量**)→ 喂进**同一个** `Absorb`。

★ 三条设计取舍,逐条写明:
1. **拉全量,不"从我以为的位置续拉"** —— 断层时我们并不知道漏了哪几条,
   续拉会把漏掉的那段**永远跳过去**;
2. **走同一个 `Absorb`**,不另写一份解析 —— 两份解析会漂移,而漂的那天自检只盯着其中一份;
3. **只在帧到达时触发,不起定时器** —— D37 ② 推送非轮询。补不回来就**留着标记**下次再试,
   清掉标记等于假装补上了。

---

## 4. 为什么不选「撤掉」

1. **它有三个消费者**,只是都不在客户端:`doctor.py` ⑦ 环 · `probe_sync.py` · `test_sync.py:193`。
   撤掉要同时改 `90-ops/**`,而那是本车道的**禁区**;
2. **诊断工具需要的正是一个平的 HTTP GET** —— SSE 那条要能流式读、要处理心跳,
   让体检脚本去开一条长连接只为读一次状态,是把简单的事做复杂;
3. **撤掉之后这个洞还得补**,只是换个补法:丢帧 → **主动断开重连** → 靠首帧全量对齐。
   ★ 这条路**能走**,但代价更大:重连要重跑一次 `ReconcileAsync` 全量重推、
   要重新注册在线名单(会让别的机器看到你闪一下离线),而收益只是少一条路由。

> ⇒ **接上**是代价最小、且让这条路由**真的有人走**的那个选择。
> 它现在不是重复路径了:它是**唯一**的丢帧恢复路径。

**★ 如果用户裁定「撤掉」**:改动很小 —— 删 `PullFullAsync`,把 `_needFullPull`
改成"主动断流并重连",并请 90-ops 车道同步处理 `doctor.py` ⑦ 与 `probe_sync.py`。
断层检测(`FrameContinues`)与它的 4 条断言**两种处置都要保留** —— 洞是同一个。

---

## 5. 5 条契约的成对断言(验收的另一半)

| 契约号 | 服务端半边(`test_sync.py`) | 客户端半边 |
|---|---|---|
| `CONTRACT:sync.snapshot` | 顶层键集合 `{generation,since_rev,data,counts}` | `SyncClient.PullFullAsync` → `Absorb` |
| `CONTRACT:sync.push` | `{accepted,total,results,generation}` | `SyncClient.ParsePush` |
| `CONTRACT:sync.events.frame` | 首帧键集合 + **首帧必须是全量** | `SyncClient.Absorb` / `FrameContinues` |
| `CONTRACT:chat.stream.frame` | 帧里 `choices[0].delta.content` | `ChatClient.ParseDeltaPayload`(**新抽出来的**) |
| `CONTRACT:models.list` | `{object,data}` + 每项 `{id,object,owned_by,kind,contract}` | ★ **仓外**,见下 |

**★★ `/v1/models` 的客户端半边:仓内没有,而且不该假装有。**

简报说它「有两个消费者(`HubClient.cs:290` 与 `transport/Program.cs:93`),两处各自解析」——
**实测两处都不解析响应体**:

- `HubClient.ProbeAsync()` 只拿它**探活**,`await CallAsync("/v1/models")` 的结果**直接丢掉**;
- `transport/Program.cs:93` 断言的是 `body == "ok"` —— 那是它自己那个**测试替身**的回复,
  不是真网关的 `{"object":"list",…}`;
- 客户端的模型清单走的是 **`/v1/gpu/components`**(`HubGpu.cs:350`)。

⇒ 它的真实消费者在**仓外**:`90-ops/install-openwebui.ps1:81` 把
`OPENAI_API_BASE_URL` 指到 `http://127.0.0.1:8080/v1`,**Open WebUI 按 OpenAI 协议读它**。
⇒ 所以这条钉的是**协议一致性**(`object=="list"`、每项 `id`/`object=="model"`),
**不是**"我们能解析"。★ 假装我们解析了它,才是给一条没人走的路配一条恒绿的断言 ——
和 `/v1/sync/snapshot` 那条要防的是同一件事。

---

## 6. ★ 交回时的一个卡点:DEBT 数字**降不下来**,因为改它要动禁区

验收写的是「DEBT 从 23 降到 18」。**这 5 条的活已经干完了**(见上表 + 第 7 节红测),
但那个数字来自 `90-ops/gate/check_contract_pairs.py`,而 `90-ops/**` 是本车道的**禁区**。

**要 90-ops 车道改三处**(逐条给,改完 DEBT 自动变 18):

1. `CONTRACTS` 里这 5 条从 `"state": "none"` 改成 `"state": "paired"` 并补 `"cid"`:

```python
("gateway", "GET",  "/v1/sync/snapshot"):   {"state": "paired", "cid": "CONTRACT:sync.snapshot", …}
("gateway", "POST", "/v1/sync/push"):       {"state": "paired", "cid": "CONTRACT:sync.push", …}
("gateway", "GET",  "/v1/sync/events"):     {"state": "paired", "cid": "CONTRACT:sync.events.frame", …}
("gateway", "POST", "/v1/chat/completions"):{"state": "paired", "cid": "CONTRACT:chat.stream.frame", …}
("gateway", "GET",  "/v1/models"):          {"state": "paired", "cid": "CONTRACT:models.list", …}
```

2. `_EXPECTED_DEBT = 23` → `18`;

3. ★★★ **`_PEER_FILE` 必须从"一个文件"变成"一组文件"** —— 这是**真正要紧的那一条**:

```python
_PEER_FILE = "10-core/gateway/test_gpu_broker.py"     # 今天:写死一个
```

它今天**只读 `test_gpu_broker.py`**。本车道把契约号声明在 `test_sync.py` 里(故意同名同结构),
而那张表**它看不见** ⇒ 双向对拍对不到这 5 条,`_CID_KEY` 零命中检查也不会响。
**后果是"少盖"那一侧**:第二条车道开始声明契约的那一刻,广度表的对拍就**静默瞎掉一半**,
而它自己不会说。

> ★ 这正是那个工具**自己**要防的形状(ASSERTION-PITFALLS **3b**:
> 判词说"每一个",判据是一份写死的路径)——
> 它在 V3 落地时只有一个对端,所以写死是"当天正确";**今天起不再正确。**
> 建议改成扫 `10-core/**/test_*.py` 里所有声明了 `CROSS_PROCESS_CONTRACTS` 的文件,
> 并配一条"至少扫到 2 个对端表"的元断言(零命中/只扫到一个都判红)。

---

## 7. 红测 —— 每条护栏都亲眼见过红

| # | 改坏什么 | 红的那条 |
|---|---|---|
| V1 | 服务端键集合漂一个键(`counts`→`count`) | 顶层键集合逐字对拍 |
| V2 | 客户端**断言消息**里的契约号被删(注释还留着) | 元断言:缺配对即判红 |
| V3 | 契约号只留在自检里、**解析器那侧**被抹掉 | 「钉在解析器那一侧」 |
| V4 | SSE 首帧改成增量(`snapshot(since_rev=1)`) | 「首帧必须是全量」 |

★ 一律**字节备份还原**,不用 git;四次还原后逐字节一致(SHA 已核)。

**★★ V2 第一版没红 —— 而它暴露的是判据本身的洞:**

我最初写的是 `_cid in _st_src`(整份文件里有没有这个字符串)。
把契约号从**断言消息**里删掉、只留下分节注释 `// ── CONTRACT:chat.stream.frame ──`,
**它照样为真** ⇒ 一条断言的内容被换掉了,而元断言一声不吭。
⇒ 收紧成**先去 `//` 行注释再判**:注释是**标签**,字符串字面量才是**会被打印出来的那条断言**。
配一条"去注释器没把整份文件吃掉"的元断言(否则这一组会静默变成零断言)。

> ★ 这是 ASSERTION-PITFALLS 第 1 条那套"去注释再判"的**反向**用法:
> 那边怕注释把**反向**断言弄红,这边怕注释把**正向**断言**弄绿**。
> 值得作为第 1 条的一个补注记下来。

---

## 8. 实测数

- `test_sync.py`:**120 PASS · 0 FAIL**(本车道之前 ~90);
- 客户端 `--selftest`:**1981 PASS · 0 FAIL**(本车道新增 16 条);
- ★ `test_sync.py` 现在跑约 **1 分 50 秒**,其中**约 45 秒是三次 `TestClient` 生命周期**
  (每次 ~15 秒,`_start_gpu_broker` 在探一个**没起来的**后端)。本车道新增一次 ⇒ **+15 秒**。
  这不是本车道引入的问题,但它现在更明显了,**如实记在这儿**。

---

## 9. ★ 一处必须记下来的踩坑(给 ASSERTION-PITFALLS 第 6 条补一个实例)

**SSE 的契约不能用 `TestClient` 去取。**

第一版我用 `TestClient.stream()` 读首帧 —— **整套测试永久挂住**(实测 120 秒零输出)。
根因:进程内传输**永远不会让 `request.is_disconnected()` 变真**,
而 `gen()` 的退出条件正是它 ⇒ 生成器无限循环、每 15 秒吐一个心跳,`with` 退出时等它收尾。

★ **挂住比判红更坏**:运行器只看到"没有汇总行",看不出是哪条没守住;
而 `run-tests.ps1` 会把它记成「没跑起来」——**离真因很远**。

⇒ 改成**直接驱动那个真的异步生成器**,喂一个「第一次问说还连着、第二次问说断了」的请求:
生成器吐完首帧、走 `break`、进 `finally` 把自己从在线名单摘掉,**全程走真代码**。
★ 这不是"另建一份模型来验":`gen()` 是 gateway 里那个真的,一行没换。

> 出处:2026-08-06 · V6 车道 · 全部数字为本车道实测。
