# `D?` · Broker 的卸载与启动重整 —— 三条今天第一次可达的缺陷(V16 · 2026-08-08)

> 车道:**V16 · Broker 卸载与启动重整**(分支 `claude/broker-unload-reconcile-db2d03`)
> 来源:**V13 收工报告**(提交 `0474d8c`,packet `host-loopback-business-route-2026-08-08.md` §4.2)
> D 号:草案写 `D?`,并入那刻取号(落笔时仓库里**已提交**的最大号 = **D107**;
> 同时在排队的还有 `host-loopback-business-route` 与 `sync-snapshot-pull-on-connect` 两包,
> 取号顺序由第 0 条车道定)。
> 边界:本包**只动** `10-core/gateway/{gpu_broker,model_loader}.py`、`gateway.py` 的 GPU 段、
> `test_gpu_broker.py`。`config/vram-budget.toml`、`10-core/speech/**`、`20-client-win/**`、
> `sync_*.py`、中央四文档**一个字节都没动**(第 7 节列出因此没做的事)。

---

## 0. 先说复核结论:三条里 **①② 成立、③ 说窄了**

三条我**逐条自己复核过,并且两条路径都在本机真跑过**(真 Broker · 真 ModelLoader ·
真进程 · 真 NVML · **没有任何故障注入**)。V13 那份记录写得准,但有三处要更正,
而其中一处会把修法引到错的方向上去。

| # | 原话 | 判定 | 要更正的地方 |
|---|---|---|---|
| ① | `speech.lite` 装载不吃显存 ⇒ 卸载必然撞「显存未回收」 | **成立** | 「必然」略强(见 §1.4);更要紧的是**根因不在 speech** |
| ② | 422 把中枢打进 RECONCILING,拒收一切新事务 ⇒ 卡死,只能重启 | **成立** | 之后的码是 **409 `busy`** 不是 422;「整台卡死」只死**一个**能力面 |
| ③ | 按需装载起的 llama-server 在判定「已卸载」之后仍活着 | **说窄了** | 按需**自己起的**那条**是好的**;活下来的是**认领来的**那一类 |

---

## 1. ① `speech.lite` 那一条 —— 根因不是语音,是**拿准入上界当回收等式**

### 1.1 你问的那个岔路口:「是它真的不占,还是账本与实际不符?」

**两个都是,而且是叠着的两条缺陷,修法完全不同。**

**它真的不占。** `10-core/speech/launch.toml:44` 一字不改地写着:

```
device = "cpu"        # ★ v1 只走 CPU:0 显存 ⇒ 不必等任何 GPU 裁定,也不抢租约
```

`server.py:185` 就是把这个字段喂给 `WhisperModel(device=…)`;TTS 那半边(Piper /
onnxruntime)`launch.toml` 自己记着「实测只有 CPU/Azure 两个 provider」。
⇒ 一个跑着的 `speech.lite` 后端**持有 0 GiB 显存**。

**而账本说的是另一回事。** `config/vram-budget.toml` 里 `speech.lite` 的
`peak = 2.07`,note 写着「turbo ASR **2.07 GPU** + Piper CPU TTS 0」。
那是一次**对 GPU 版 ASR** 的实测,而今天出厂的启动规格把 ASR 钉死在 CPU 上。
⇒ **两份数据源对同一件事说了相反的话**,而闸读的是前者。

**实机读数(2026-08-08,本机):装载前 free 14.559 → 装载后 14.557 ⇒ 足迹 0.002 GiB。**

### 1.2 但**根因不是这两条中的任何一条** —— 是第三条,而它管所有组件

旧代码(`gpu_broker.py`,V16 之前):

```python
expect = self._free + sum(self.cfg.peak(c) for c in drop if c in self.cfg.components)
await self._loader.unload(drop)
err = await self._await_reclaim(expect)          # free 要回到 expect ± 0.2
```

`peak` 是**准入**量,而且是**有意偏保守**的上界 —— `vram-budget.toml:105-108` 自己写着
「5.31 > 5.0,闸变更保守……**方向也是 fail-safe 的**」。

> **把一个上界当成卸载后必须等额回吐的等式,方向恰好反过来:**
> **准入上多算是安全的,回收上多算就是必然误报。**

算术(F = 预检那一刻的 free):`F ≥ F + 2.07 − 0.2` ⟺ `0 ≥ 1.87` —— **对任何 F 都为假**。
容差要 ≥ 2.07 才吸得住,而它是 0.2,差了十倍。

★ 这条**不是语音专有**,逐条:
- **认领来的孤儿**按边界不杀(`model_loader.unload` 的说明),却照样被要求吐出全额 peak
  ⇒ 对一个**真的 llama-server** 也必然误报。这件事**早就被撞到过并写下来了** ——
  `test_gpu_broker.py`(V16 后在 :1193-1197)逐字记着「卸不掉(认领来的进程按设计不杀)⇒ 回
  `vram_not_reclaimed`」。**当时的处置是改测试绕开这条路,不是修 Broker。**
- `comfyui.sdxl` 的 8.14 是**出图时**的峰值,不是驻留量;
- `30b-a3b` 的 11.9 是 38% offload 下的 GPU 侧峰值;
- `llm` 各档只是因为 llama.cpp 在装载时就把权重 + 定长 KV 一次分配完,才**碰巧**
  `resident ≈ peak` —— 那是 llama.cpp 的偶然,不是这个公式的性质。

### 1.3 修法:判据分成两条,**按顺序**

| | 判据 | 硬/软 | 能不能为假 |
|---|---|---|---|
| ① | 卸完之后**端口上还有人吗**(`verify_unloaded`) | **硬** | ★ 能,而且今天就为假(见 §3) |
| ② | free 回到**实测足迹**之内(`_reclaim_after_unload`) | 软 | 能;期望值 < 容差时如实说「没什么可等的」 |

- 期望值改由 `_footprint` 给:**装载那一刻 NVML free 的实测降幅**,夹在 `[0, peak]`;
- **一次装多个时不记**(分不出谁占了多少,而按 peak 分摊是**编一个数**)——
  不记的那些如实进 `unknown`,期望值贡献 0,由结构判据接住;
- **被污染的读数不记**(负数或超过 peak = 别的进程同时动了显存);
- 快照里 `footprint.inferred = true`,与 `non_ai_used_gib_inferred` 同款纪律。

★ `vram_not_reclaimed` 这个码、`RECLAIM_TIMEOUT_S = 10`、`RECLAIM_TOLERANCE_GIB = 0.2`
**一个字没动**(方案书行 1507)。改的只是**期望值从哪来**。

★★ 而且它**不再落在 RECONCILING**:进程确实没了、账本也记下了 ⇒ 账本与现实**没有分家**,
而 RECONCILING 的语义就是分家。拿它当「显存异常」的落点,是把一次可重试的失败变成死锁态。
现在落 `READY`(此刻 `actual == committed`,I2 成立),事务失败但**可以直接再点一次确定**。

### 1.4 我要更正原话的那一处

「**必然**撞」略强。判据比的是**总的** NVML free,不是那个组件的差额 ——
所以那 10 秒窗口里任何一次 ≥1.87 GiB 的无关释放(关掉一个游戏/浏览器)都会让它**通过**,
而那次通过的**理由是假的**。反过来也一样:桌面在窗口里吃掉显存,会让一次**正确**的卸载判失败。
⇒ 它不是"总是错",是**两个方向都会说谎**,而这比"总是错"更难查。

---

## 2. ② RECONCILING —— 白名单里写着合法的边,**从来没有代码走过它**

### 2.1 复核:确实是死锁态,而且比原话更死

`_transition` 的**全部**生产调用点(枚举,不是抽样;行号是 **V16 之前**那一版的):
`:738 :742 :1665 :1674 :1694 :1722 :1740 :1741 :1749 :1772 :1779 :1789`。逐条查它们的源状态之后:

- `RECONCILING` 的**入边**来自 `STARTING` 与 `APPLYING`;
- **出边有零条**。`ALLOWED_TRANSITIONS[RECONCILING] = {READY, DEGRADED_SAFE}`
  (旧版 `:194`),而 `RECONCILING → READY` 在全仓**没有任何调用点**;
  `→ DEGRADED_SAFE` 那一条(旧版 `:1741`)只在 `:1740` 的下一行、同一把锁里,
  只有"几微秒前还在 APPLYING"的 Broker 够得着。
- `set_power(True)` 只救 `DEGRADED_SAFE`;`finish_startup()` 只认 `STARTING`;
  采样循环里一个 `_transition` 都没有;`set_power` **没有任何 HTTP 路由**。

**实机逐条试过,一条都出不去**:`finish_startup()` → RECONCILING ·
`set_power(False)` → RECONCILING · `set_power(True)` → RECONCILING · 再点确定 → `busy`。

### 2.2 三处要更正原话

1. **进去的是 422,之后的是 409。** `gateway.py:1785` 把 `busy` 映射成 409。
   原话把入口码和后续码混成了一个。
2. **「整台中枢卡死」说宽了。** `RECONCILING ∈ SERVING_STATES`,而且没有任何生产端点
   去查 Broker 的状态 ⇒ chat、快照、SSE、租约、续租、以及**整条按需装载平面**
   (`POST /v1/gpu/intent`)全都照常。死掉的**只有一个能力**:
   `POST /v1/gpu/intended`(改常驻集合的那次事务)。
   ★ 而且「仍然服务」这条性质**今天没有任何东西在守** —— `serves_requests()`
   在全仓零调用点,它成立只是因为**碰巧没人去查状态**。这是一条**遗漏出来的正确**,
   不是一条保证;哪天有人加一个查状态的端点,它会静默消失。**本包没有修这条**(§7)。
3. **「只能重启进程才出得来」——** 对,但比原话更糟:**重启也不保证救得出来**。
   开机路是 `adopt_running()` → `finish_startup()`,后者若发现 `actual ≠ committed`
   会**直接把新起来的进程再打进 RECONCILING**。V13 那次能靠重启出来,
   只是因为重启后 committed 恰好是空的。
4. 「**状态不落盘**」这句要说得更重:**不是"重启后查不到"，是"重启之前就查不到"**。
   `_transition(to, why)` 在成功路径上把 `why` **整个丢掉**(它只出现在
   `IllegalTransition` 的消息里),而 `gpu_broker.py` 没有任何日志与文件 I/O。
   唯一的痕迹是那条 422 的响应体,只交给了触发它的那一个调用方。

### 2.3 修法:两条出路,**分开**,因为授权来源不同

| | 谁 | 做什么 | 挂在哪 |
|---|---|---|---|
| `reconcile_tick()` | **判据** | 账本与现实重新对上(`actual − transient == committed`,与 `finish_startup` **同一个式子**)⇒ 回 READY | 采样循环 **+** 每次「点确定」之前 |
| `reconcile_to_actual()` | **人的动作** | 以现实为准对齐账本(与 `adopt_running` 同一条原则),再回 READY | **只在网关开机路**上 |

★ `reconcile_tick` **不动任何集合、不起停任何进程** ⇒ 它不是 D10 禁的那种「自动触发」:
D10 禁的是**系统自己去动显存**,这里动的只是"承认条件已经满足了"。
★ `reconcile_to_actual` **会改 committed**,所以它绝不挂进任何自动路径 ——
开机那一次除外,理由写在 `gateway.py` 该处:开机这一刻 committed **本来就是刚从现实推出来的**
(上一句 `adopt_running` 做的),它还没承载任何用户意图。**两处都不动 `intended`**。
★ 「点确定」之前也判一次,而不是只靠采样循环:**判据不该依赖某个后台任务活着** ——
采样器崩了的时候,恰恰是最需要人能点一次确定的时候。
★ 而 `busy` 的**理由**现在会点名:「账本与现实仍未对上:committed=… · 实测 actual=…」。
「忙」是个指向别处的假理由。

### 2.4 落盘:落的是**事件**,不是**状态**

- **进程内**:`Broker._reconcile_log`(环,上限 32),随快照下发 —— 界面/排查当场看得见;
- **跨重启**:`gateway.log_gpu_reconcile()` → `{state}/logs/gpu_reconcile.jsonl`,
  与 `upstream_problem` / `gate_rejection` / `denied_access` **同一个落点、同一套强 ACL**。
  三个调用点:开机对齐、`/v1/gpu/intended` 落进 RECONCILING/DEGRADED_SAFE、开机对齐失败。

★★ 这条分工是**有意的**,而且它不违反 `p4-broker-shape-2026-08-04`:
那份决议定的是 **Broker 的状态**(租约、世代号)有意不挺过重启 —— 那一条一个字没改。
**"发生过什么"是事件,不是状态**,它本来就该落在网关那套审计落点里。

---

## 3. ③ 孤儿进程 —— 你说窄了,而说窄的那一处正好是修法的关键

### 3.1 更正:**按需自己起的那条路是好的**

`request_on_demand` 真的**自己起**的进程落在 `_procs` 里,卸载走 `_kill()` → 真的死掉。
实机验过。活下来的是**另外两类**:

- **(a) 认领来的**(`_adopted`):`unload()` 对它只 `discard`,**按边界不杀**
  ——「不是我起的进程,我不知道谁还在用它」。这条边界是对的,**本包一个字没动**。
  问题不在不杀,在**杀没杀这件事没人说得出来**。
- **(b) 杀不掉的**:`terminate()` 与 `kill()` 都抛(Windows 上两者都是 `TerminateProcess`,
  前者失败后者会以同样理由失败),旧代码把两次异常都 `pass` 掉,而 `_procs.pop` 是**第一句**
  ⇒ 句柄已经丢了,这个进程**再也没有任何一条路径能杀它第二次**。

★ 而「按需装载起的 llama-server」**经由一次网关重启就变成 (a)** —— 这正是 V13 撞到的形状,
我照着复现了:按需起 `llm.assistant.8b@8k`(实测占 **5.311 GiB**,与原话「~5.2」对得上)
→ 网关重启 → `adopt()` 把它当成 `llm.assistant.8b@16k` 采纳进 committed
(同端口三档分不清是哪一档,这是 `adopt` 早就写明的诚实边界)→ 取消勾选 → 确定
→ 认领来的不杀 → 账本把它抹了 ⇒ **一个 6.5 GiB 的进程活着,`running()` 报空,
I2/I3/I4 三条全绿**,再重启一次又被采纳一遍。

### 3.2 你问的那一条:**`/health` 探活为什么没抓到**

答案是一句话,而且它是**结构性**的:

> `running()` 的**候选池就是账本**(`_procs ∪ _adopted`)。
> 它只能**确认或否定账本已经相信的条目**,永远探不到账本里没有的那一条。
> ⇒ 它对「账本说卸了、进程还在」**结构上不可能为真**。

对照:`adopt()` 的候选池是 `cfg.components` **全表** —— 所以它找得到那个孤儿,
而 `running()` 找不到。同一个 `/health`,两个候选池,两种能力。

★★ 更糟的一条(V13 那份记录里没有,是本次查出来的):**`running()` 自己在制造孤儿。**
旧代码(`running()` 里对 `_adopted` 那一段)的处理是「**非 2xx 就丢账**」,没有任何存活性检查、
没有进程句柄。而 llama-server **加载模型时回 503**(`model_loader.py` 文件头 `:24-27` 自己写着),
`_health_ok` 的超时又只有 2 秒 ⇒ **一次加载中、一次慢响应**,一个活着且占满显存的后端
就被从账本上**永久抹掉**,之后 `unload()` → `_kill()` → `pop` 返回 `None` → 静默成功。

### 3.3 修法:一条**能为假**的判据

`ModelLoader.residency_truth()` —— 候选池是**全部登记端口**,与账本无关。
它问一个账本回答不了的问题:**「哪个端口上有人,而我们说不出他是谁?」**

- 结果进快照(`residency_truth`),并成为 **I3 的第二条子句** ——
  这是对 I3 的**加强**,不是新开一条不变式:它问的仍然是「在的都该在」,
  只是把"在"从"账本认得的组件"扩到了"端口上真的有人"。
- **同端口多组件时只报端口,不报组件名**(分不清就不假装分得清,与 `adopt` 同一条边界)。
- **只会漏报、不会误报**:连不上就算 down(哪怕它其实僵在那里占着显存)。
  方向是有意选的 —— 误报会让 I3 因为一次网络抖动就红,而经常误红的告警等于没有告警。
- `probe == null` 表示**还没探过**,与"探过且没有孤儿"是两件事;I3 的理由里会说出这一点。

配套五处:
1. `unload()` 从 `-> None` 改成**回执三分**:`killed` / `skipped_adopted` / `kill_failed`。
   ——「我们没杀它」与「它已经没了」此前在账上**完全同形**,三个调用方一个字都收不到。
2. `_kill()`:**确认死亡之后才 pop**;硬杀之后**再核实一次**;返回布尔。杀不掉就**留着句柄**。
3. `readopt()`:核实发现它还活着 ⇒ **认回账上**。认回来**不等于**要杀它 ——
   边界一个字没动,只是**不再说谎**。
4. `sweep_idle_transient` / `yield_under_pressure` 也加了核实 ——
   这两条路径此前**一次核对都没有**,而它们正是按需平面卸载的全部入口。
5. `_unload_shortfall()`:**端口探针与装载器回执两个来源合起来**才算数。
   端口探针盖不到 `port = 0` 的组件(`comfyui.*`),回执盖不到「按边界没杀但还占着显存」
   的认领孤儿 —— 只用一个就有**一整格盲区**。

★ 顺带把探针做成**并发 + 更短超时**(`TRUTH_PROBE_TIMEOUT_S = 1.0`):它挂在 1 Hz
采样循环上,串行探 4 个端口 × 2 秒超时 ⇒ 一个黑洞端口能把采样循环拖成 8 秒一轮,
而那个循环同时还担着压力让位与 RECONCILING 的出口判据。
★ 事务里动完真实状态之后**立刻重取一次独立观测**(`_refresh_actual`)——
不做的话有一个可复现的窗口:committed 已改、状态已回 READY,而 `_actual_cache`
还是上一秒的旧值 ⇒ **I2 会红上最多一秒,理由指向一个已经不成立的差异**。

### 3.4 顺带查出来的第四条:**置信度在说谎**

`check_invariants` 的 `_conf` 判据是「装载器接上了吗」。而 `actual_resident` 有**第三种**形态:
装载器接上了但**一次都还没探过**(`_actual_cache is None`,采样循环没跑起来时就是这样)
—— 它退回账本,却被标成 `observed`。实机复现当场撞到:6.5 GiB 的孤儿活着,
三条不变式自称 `observed` 而数据来自账本本身。
⇒ 判据改成问**这一次的数从哪来**,不是问接线在不在。

---

## 4. 实机复现:两条路径,**没有任何故障注入**

### 路径 A · 就是你写的那条(`speech.lite`)

```
NVML free                14.559 → 14.557 GiB   ⇒ 实测足迹 0.002(peak 声称 2.07)
取消勾选 → 确定           vram_not_reclaimed · state=RECONCILING · 耗时 10.7s → HTTP 422
端口 18085                已经没了;loader.running() = []      ← 卸载其实**成功了**
重试点确定 ×3             ok=False code=busy state=RECONCILING
finish_startup()          RECONCILING
set_power(False/True)     RECONCILING · RECONCILING
落盘                      open()/json.dump/sqlite/write_text 在 gpu_broker.py 里各 0 次
```

### 路径 B · 孤儿(`llm.assistant.8b@8k`,同一条闸的另一个入口)

```
按需装载                  used 1.188 → 6.499 GiB   ⇒ 实测足迹 5.311(原话「~5.2」对得上)
网关重启 → adopt          committed = ['llm.assistant.8b@16k']   ← 采纳成了**另一档**
取消勾选 → 确定           vram_not_reclaimed · RECONCILING · 10.6s → 422
独立观测                  llama-server pid 存活 · 18081 LISTENING · used 6.505 GiB
账本                      running() = [] · _adopted = [] · _procs = []
快照                      I2/I3/I4 **三条全绿**,confidence 全标 observed
再重启一次                同一个孤儿**又被采纳一遍**
```

### 修好之后,同样两条路径再跑一次

```
A:装上/卸掉两次事务都 ok=True state=READY,卸载耗时 2.3s(不再等那 10 秒)
B:code=unload_not_effective(0.3s),消息点名「不是我们起的(认领来的孤儿),按边界不杀
   —— 但它**还占着显存**」;readopt 把它认回账上,running() 重新报得出它;
   之后**点得动确定**(ok=True state=READY)
B'(账本空 + 端口活着):residency_truth().orphan_ports = [18081],
   候选 = 18081 上登记的三档,**I3 判红** —— 同一处境下 V16 之前它报绿
```

---

## 5. 断言:**每一条都先在旧代码上判过红**

`10-core/gateway/test_gpu_broker.py` 追加 V16 一节(**只追加,没有重排**)。

| | 判据 | 旧代码上的红 |
|---|---|---|
| ① | 零足迹组件装得上也卸得掉 | `(True, False, 'vram_not_reclaimed', 'RECONCILING', ['speech.lite'], 10.03s)` |
| ① | 足迹是**实测**的,不是 peak | `{}`(旧代码根本没量过) |
| ① | **反向**:真没吐回来 ⇒ 照样判 `vram_not_reclaimed` | 旧码同样红(它连 committed 都没更新) |
| ② | 存在 `reconcile_tick` / `reconcile_to_actual` 且**有调用点** | 全红(旧代码零调用点) |
| ② | 账本对上 ⇒ 离开 RECONCILING | `(None, 'RECONCILING')` |
| ② | **反向**:没对上就**停在** RECONCILING | 旧码"红"得不同 —— 它永远停着 |
| ② | 之后**还点得动确定** | `(False, 'busy', 'RECONCILING')` ← **这就是那条卡死路径** |
| ② | 进/出留下可查记录 | `[]` |
| ③ | `residency_truth` 报得出孤儿端口 | `{'orphan_ports': None}` |
| ③ | I3 因此判红 | `(True, '没有不该在的组件')` ← **它在旧码上报绿** |
| ③ | `unload()` 回执三分 | `{'killed': None, 'skipped_adopted': None}` |
| ③ | 杀不掉 ⇒ `_kill` 回 False 且**句柄留着** | 旧码回 `None`、`_procs = []` |
| ③ | 端口探针 + 回执**合起来**才盖得住(comfyui port=0 那一格) | 旧码没有合流器 |
| 通则 | 白名单里每条边**要么有驱动者、要么进欠债表** | `reconcile_tick` 不存在 ⇒ 红 |

**实测:同一份断言文件 —— 旧源码 `622 PASS / 42 FAIL`;新源码 `684 PASS / 0 FAIL`。**
(改动前该文件是 585 PASS / 0 FAIL,所以 V16 净增 **99** 条断言。)

### 5.2 ★★★ 一条**通则**断言 —— 这个 bug 的一般形式

`RECONCILING → READY` 写在 `ALLOWED_TRANSITIONS` 里而**零调用点**,
躲过了此前**所有**断言,直到有人真的被卡在里面。⇒ 加一张反向全表:

> **白名单里的每一条边,要么在 `_EDGE_DRIVERS` 里登记一个驱动者(而且那个函数的源码里
> 真的写着目标状态),要么逐条写进 `_EDGES_WITHOUT_DRIVER` 这张欠债表。没有第三种。**

今天欠债表里**恰好一条**:`READY → RECONCILING` 直达边 ——
READY 永远先经 STAGING 进事务,所以没人走它。**留着**是因为 §8.1 的状态图里有它;
哪天真要用,得先登记驱动者,否则这条断言会红。
★ 这张表**只许变短**(与契约欠债表同款纪律)。

★ 每条正向断言都配了**反向**(端口全灭 ⇒ I3 必须回绿;没对上 ⇒ 必须停在 RECONCILING;
DEGRADED_SAFE **不许**被自愈判据带出来)——
一个永远判红的检测器和一个永远判绿的检测器,都不是检测器。
★ 「源码里没有 `peak`」那条配了**元断言**:同一个词在 `pressure_victims`
(真的按 peak 挑人)里必须查得到,否则它只是因为我把词写错了。

### 5.1 断言自己抓出来的一个真 bug

第一版把 `_footprint.pop()` 写在了核对**之前** ⇒ 期望值恒为 0 ⇒ 那条算术判据
变成了一个**永远说通过**的探测器。**那条反向断言当场把它抓了出来**(源码注释里留了痕)。

---

## 6. 契约与门禁

**门禁(`run-tests.ps1 -Full`,本 worktree,前后各真跑一次):**

| | 前 | 后 |
|---|---|---|
| 合计 | `PASS=2136 FAIL=0` | **`PASS=2235 FAIL=0`** |
| `test_gpu_broker.py` | `585` | **`684`**(净增 99) |
| 跨进程契约欠债 | `1 / 30` | **`1 / 30`**(不变) |
| SKIP | 1 | 1(同一条,`test_repo.py`) |

★ 「没跑的」那几项与基线**逐条相同**(memory 套件的 11 个手动件 · 客户端
`--selftest` 在 worktree 里没产物 · 三个环境验证脚本)—— 本包一条都没有新增,
也一条都没有让它们变得更少。

- 快照顶层新增四个键:`residency_truth` / `reconcile_log` / `reconcile_note` / `footprint`
  ⇒ `_SNAPSHOT_TOP_KEYS`(**手写字面量**,不从 `to_json()` 反推)同步登记。
  加上它们那一刻,V5 钉的三条断言(顶层键 · SSE 帧载荷 · 409 里那份完整快照)
  一起变红,理由写着「多 [...]」—— **成对断言又一次挡住了自己人的契约漂移**。
- **没有新增任何 HTTP 路由** ⇒ `_EXPECTED_GPU_ROUTES` 不动,`ROUTE_TIERS` 不动,
  跨进程契约总数不动,**DEBT 保持 1/30**。
  ★ 这是有意的取舍:`reconcile_to_actual` 本可以做成一个端点,但那要新增一条契约
  并在 `Selftest.cs` 里写它的客户端半边 —— 而那个文件今天由 V14 在动(§7)。

---

## 7. 没做的,和为什么

1. **`config/vram-budget.toml` 里 `speech.lite` 的 `peak = 2.07` 没改。**
   它与 `launch.toml` 的 `device = "cpu"` **互相矛盾**,而它是**准入闸的唯一数据源**,
   归 config 车道,不在本包边界内。
   ★ 本包的修法**不依赖**改它:回收判据已经不看 peak 了。但**闸仍然按 2.07 给它算账**
   ⇒ 一个 0 显存的组件仍然会占掉 2.07 GiB 的预算额度。
   **建议下一条 config 车道裁:重测,或加一个与 peak 分开的 `runtime_vram` 字段。**
   ⇒ 「两份数据源对同一件事说相反的话」这件事本身,**今天没有任何断言在守**。
2. **`Selftest.cs` 里 `gpu.snapshot` 的客户端 fixture 没有补上新增的四个键。**
   那份 fixture 是一整条字符串字面量,补它是**改**不是**追加**,而 `20-client-win/**`
   今天由 V14 在动 ⇒ 会撞合并。契约配对**没有变松**(它验的是「这个形状我读得懂」,
   而新增键对客户端是可加的);但**成对的严格度在这一格上欠了一次**,记在这里。
3. **`serves_requests()` 仍然零调用点**(§2.2 第 2 条)。
   「RECONCILING 仍然提供服务」今天成立**只是因为没人去查状态** —— 它是一条遗漏出来的
   正确,不是一条被守住的性质。修它要动的是"谁该查状态"这条设计,超出本包。
4. **`request_on_demand` 的过账**:一个按需请求若撞上"端口已经有人"会走**认领**
   ——不起进程、不占显存,但**闸已经按它的 peak 算过一遍并放行了**,而且它照样进 transient 平面。
   这是**多算**(方向安全),但账是错的。本包没动,因为它属于按需平面的过账口径,
   与本包三条不是同一件事。
5. **`p4-broker-shape` 里那条「采样器崩了该进 DEGRADED_SAFE」** 与实现不符
   (实现只记 `sampler_error`)。查出来了,**没改** —— 那是一次口径裁定,不该顺手做掉。
6. **`vram_not_reclaimed` 落 READY 那条路上,`intended` 与 `committed` 会不相等。**
   卸载成功了(committed 少了那一项),而 `intended` 仍是**上一次成功事务**的那份。
   这是**有意的**:事务没走完,不该把用户的意图记成已应用。
   今天没有任何不变式管 intended vs committed(I2/I3/I4 都不问它),
   而面板上的勾选状态来自客户端自己的选择、不来自 `intended` ⇒ 界面上看不出异常。
   ★ 但这一格**没有断言在守**,记在这里。
7. **白名单里 `READY → RECONCILING` 那条直达边今天没有驱动者。**
   已经登进 `_EDGES_WITHOUT_DRIVER` 欠债表(§5.2),**没有顺手删掉它** ——
   §8.1 的状态图里有它,删边是一次口径变更,不该由本车道做。

---

## 8. 一句话的判据(留给下一个人)

> **「Broker 说已卸载」不是一条判据,除非有东西能让它为假。**
> 今天让它为假的那个东西叫 `residency_truth()`,而它之所以能为假,
> 只因为它的**候选池不是账本**。任何以账本为候选池的探针 ——
> 不管它探得多勤、判据写得多严 —— 都只能确认账本已经相信的事。
