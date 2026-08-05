# 给 `unseal_for_prompt` 补 `sink` 维 · **改动清单**(交主执行层执行)

> 日期:2026-08-06
> 性质:**执行清单,不是决议**。承 **D81 待裁 2**、承本车道
> [egress-b-gates-impact-2026-08-06.md](egress-b-gates-impact-2026-08-06.md) §8.1 建议 ①。
> **本车道不执行它** —— `10-core/memory/**` 与 `10-core/gateway/**` 归主执行层。
> 本文件只写「改哪个文件的哪一行、改成什么、为什么、以及怎么证明改对了」。
>
> **前置**:D81 待裁 2 须先被裁为「要加」。**§1 那条命名冲突要在动手之前先定**,
> 它不是风格问题 —— 定错会在同一个文件里留下两个含义相反的 `sink`。
>
> 行号基准:`main` = `f600461`(本车道 rebase 后)。主执行层正在动这两个目录,
> 动手前请重新定位;**每条都给了可 grep 的锚点**,不依赖行号。

---

## 0. 一句话

**签名改动本身可以现在就做完,而且零生产风险 —— 因为生产调用点是 0(实测)。
真正卡住的不是迁移成本,是 `sink` 的取值从哪来,而那要等 D81 待裁 1。
⇒ 拆成两期:M1 只加维度与断言(今天可做),M2 接线(等待裁 1)。**

---

## 1. ★ 动手之前必须先定:`sink` 这个词已经被占用了

`tainted.py` 里 **`sink` 已经是台账的字段名**,而且含义与 D39 的 `sink` **不是一回事**:

| | 现有的 `sink`(台账) | D39 要的 `sink`(策略轴) |
|---|---|---|
| 在哪 | `UnsealRecord.sink` :66<br>`UnsealLedger.note(handle, purpose, sink)` :85<br>`_unseal(t, purpose, sink)` :265 | 要加到 `unseal_for_prompt` 的**入参** |
| 是什么 | **导出值**:正文实际去了哪(描述性标签) | **输入值**:答案最终发到哪(策略判据) |
| 现在的取值 | `pg:{table}` :274 · `http://{endpoint}` :287 · `client:{caller.value}` :309 · **`backend:{backend.name}` :337** | 待定(本地 sink / 出境 sink) |

★★ **注意 :337 那一行本身就是 D81 决定 1-1 要纠正的那个误解的化石**:
台账把「哪个后端」记成了 `sink`,也就是**账本自己编码了错的心智模型**。
若不处理就直接加一个入参也叫 `sink`,同一个函数里会同时出现
「入参 sink(答案去哪)」与「台账 sink(其值是 backend 名)」——
**两个 `sink` 指向两根不同的轴,而 `_unseal(t, "prompt", sink)` 这行读起来完全合法。**

### 1.1 建议(A 案,推荐)

1. 把**台账**那一维改名为 `sink_label`(它本来就是标签,不是判据):
   - `UnsealRecord.sink` → `sink_label`(:66)
   - `UnsealLedger.note(self, handle, purpose, sink)` → `..., sink_label`(:85-86)
   - `_unseal(t, purpose, sink)` → `_unseal(t, purpose, sink_label)`(:265, :268)
   - 四个调用点的**位置参数**不变,只是形参名变(:274 :287 :309 :337)
2. `sink` 这个名字**留给策略轴**,与 D39 / D81 的用词一致;
3. ★ 顺手修掉那块化石:`unseal_for_prompt` 的台账标签改成**两根轴都记**:
   ```python
   return _unseal(t, "prompt", f"sink:{sink.name}|backend:{backend.name}")
   ```
   这样审计里能直接看出「答案去哪 × 用了哪个后端」,而不是只剩后者。

**B 案(备选)**:台账不动,新入参叫 `answer_sink`。代价是与 D39/D81 的词汇不一致,
半年后又要有人解释「文档说 sink,代码叫 answer_sink,而代码里还有个别的 sink」。
⇒ **不推荐**,但如果主执行层判断改台账字段的波及面更贵,B 案也可接受 ——
**唯一不可接受的是两个 `sink` 并存**。

---

## 2. 迁移面:实测把 D81 的估算收紧了

D81 待裁 2 写「约 10 个调用点全在测试里」。**实测精确值:**

| 文件 | 行 | 形态 |
|---|---|---|
| `10-core/memory/test_tainted.py` | 113 | `unseal_for_prompt(t, backend=LOCAL_BACKEND)` |
| | 122 | `blocks(lambda: ...(s2, backend=LOCAL_BACKEND), ...)` |
| | 137 | `blocks(lambda: ...(t, backend=CLOUD), ...)` |
| | 139 | `blocks(lambda: ...(s2, backend=CLOUD), ...)` |
| | 141 | `...(t, backend=LOCAL_BACKEND) == SECRET` |
| | 148 | `blocks(lambda: ...(t, backend="assistant.fast"), ...)` ← **TypeError 用例** |
| `10-core/memory/test_s4_acceptance.py` | 127 | `blocked(lambda: ...(row.statement, backend=CLOUD), ...)` |
| | 129 | `blocked(lambda: ...(row.statement, backend=LOCAL), ...)` |
| | 134 | `"小雨" in ...(s0row.object_text, backend=LOCAL)` |
| | 135 | `blocked(lambda: ...(s0row.object_text, backend=CLOUD), ...)` |
| | 215 | `blocked(lambda: ...(s0row.object_text, backend="assistant.fast"), ...)` ← **TypeError 用例** |

**⇒ 真实调用表达式 = 11 个,全部在测试里。生产调用点 = 0。**

不是调用点但会被误数进来的三处(**不用改**):
- `10-core/memory/panel.py:14` —— 注释;
- `10-core/memory/tainted.py:320` —— docstring 里的反例;
- `10-core/memory/test_tainted.py:133` —— 注释;
- `10-core/memory/test_s5_acceptance.py:229-234` —— **AST 断言**「面板不调用 `unseal_for_prompt`」。
  ★ 它查的是**调用**(`ast.Call` 的 func 名),与签名无关 ⇒ 签名改了它照样成立,**不用动**。

### 2.1 ★ 加成 keyword-only 无默认值,是**故意让 11 处全部炸**

现签名 `def unseal_for_prompt(t, *, backend: Backend)`(:312)—— `*` 已使参数 keyword-only。
新增 `sink` 同样 keyword-only、**不给默认值** ⇒ 那 11 个调用点**全部立刻 TypeError**。

**这是想要的结果,不是麻烦**:一个新的安全轴如果有默认值,
就等于「不写 = 默认放行」,那正是本项目反复吃亏的 denylist 形状
(`provenance` denylist / E1 override / unseal caller,`tainted.py:215-219` 自己列了这一族)。
⇒ **不许给 `sink` 任何默认值,也不许在过渡期加 `sink=None` 再补判断。**

---

## 3. M1:今天就能做完的(不依赖任何待裁)

### M1-1 · 新增 `Sink` 类型 —— 照抄 `Backend` 已被验证的形状

位置:`10-core/memory/tainted.py`,紧邻 `Backend`(:250-259)之后。

```python
@dataclass(frozen=True)
class Sink:
    """答案最终发到哪儿。★ `egress` 必填,没有默认值。

    ★★ 与 Backend 是**两根轴**(D81 决定 1-1):
       Backend.egress  = 请求发给哪个模型
       Sink.egress     = 答案最终发到哪儿
       一条 S0 记忆经本地后端生成、再顺着外联通道发出去,Backend 侧全程干净,
       而它正是 §5.6.2 L5 要禁的那件事。
    """
    name: str
    egress: bool
```

★ **必须是一个独立类型,不得复用 `Backend`** —— 复用等于把 D81 刚拆开的两根轴又焊回去。

### M1-2 · 改签名 + 加判据

`unseal_for_prompt`(:312)。判据顺序**照现有风格**(先类型、再策略):

```python
def unseal_for_prompt(t: TaintedText, *, backend: Backend, sink: Sink) -> str:
    if not isinstance(backend, Backend):
        raise TypeError(...)                     # 现有,不动
    if not isinstance(sink, Sink):               # ← 新增,照 backend 那条抄
        raise TypeError(
            f"sink 必须是 Sink(name, egress),收到 {type(sink).__name__}。"
            "传字符串会让出境判据无从做起 —— sink 必须由调用方显式声明。")
    if backend.egress:
        raise MemoryLeakError(...)               # 现有,不动
    if sink.egress:                              # ← 新增。★ 与 sensitivity 无关
        raise MemoryLeakError(
            f"记忆正文不得进入出境 sink {sink.name}(D39 · §5.6.2 L5)。"
            "与敏感度无关 —— S0 记忆发出去同样是出境。")
    if t.sensitivity == "S2":
        raise MemoryLeakError(...)               # 现有,不动
    return _unseal(t, "prompt", f"sink:{sink.name}|backend:{backend.name}")   # ← §1.1 ③
```

★ **`sink.egress` 的判据必须与 sensitivity 无关** —— 这是 D39:1242-1243 的字面要求
(「对出境 sink 拒绝**全部**记忆正文而不只是 S2」),也是 `Backend.egress` 已经踩对的那条。

### M1-3 · 11 个测试调用点补 `sink=`

两个夹具(建议加在各文件现有 `LOCAL` / `CLOUD` 夹具旁边):

```python
LOCAL_SINK = Sink("client.lan", egress=False)     # PC 端:受控设备集内(D81 决定 3)
EGRESS_SINK = Sink("channel.signal", egress=True) # 外联通道
```

逐点改法(把 `backend=X` 保持原样,只**追加** `sink=`):

| 文件:行 | 改成 | 期望结果 |
|---|---|---|
| `test_tainted.py:113` | `+ sink=LOCAL_SINK` | 仍取到正文 |
| `:122` | `+ sink=LOCAL_SINK` | 仍被拒(S2) |
| `:137` `:139` | `+ sink=LOCAL_SINK` | 仍被拒(backend 出境) |
| `:141` | `+ sink=LOCAL_SINK` | 仍 `== SECRET` |
| `:148` | `+ sink=LOCAL_SINK` | 仍 TypeError(backend 是字符串) |
| `test_s4_acceptance.py:127` `:129` `:134` `:135` | `+ sink=LOCAL_SINK` | 结果均不变 |
| `:215` | `+ sink=LOCAL_SINK` | 仍 TypeError |

### M1-4 · 新增断言(**这一节是 M1 的重点,不是附属**)

加在 `10-core/memory/test_tainted.py`。没有这些,新轴会在半年内被默认掉。

| # | 断言 | 为什么 |
|---|---|---|
| 1 | `sink` **省略即 TypeError**(`unseal_for_prompt(t, backend=LOCAL_BACKEND)` 必炸) | 钉住「无默认值」。这条是整组里最重要的一条 |
| 2 | `sink` 传字符串必 TypeError | 照 :148 `backend` 那条抄 |
| 3 | **出境 sink 拒绝全部敏感度**:对 `S0` / `S1` / `S2` 各跑一次 `sink=EGRESS_SINK` 必被拒 | 钉住「与 sensitivity 无关」——D39 要的正是这一条,而它最容易被实现成「只拦 S2」 |
| 4 | **`Sink` 与 `Backend` 不可互换**:`sink=Backend(...)` 必 TypeError、`backend=Sink(...)` 必 TypeError | 钉住两根轴不许焊回去 |
| 5 | **本地 backend + 出境 sink 的组合必被拒**(`backend=LOCAL, sink=EGRESS`) | 这正是 D81 决定 1-1 那条失效路径的最小复现;它今天**放行**,改完必须变拒 |
| 6 | 台账两根轴都记:解封一次后 `_LEDGER` 最后一条的 `sink_label` 同时含 `sink:` 与 `backend:` | 防 §1.1 ③ 被后人简化回只记一根 |
| 7 | **`Sink` 没有默认值**(`Sink("x")` 必 TypeError) | 照 `Backend` 的「egress 必填」抄 |

★ 第 5 条是全组的**核心用例**:它是「桥调本地别名 → 记忆正文进 prompt → 模型复述 → 顺通道出去」
这条路径在类型层的最小可测形态。**M1 的验收就看它从绿(放行)变红(拒绝)。**

### M1-5 · 顺手改掉一个误名(独立小项,可单独提交)

`10-core/memory/gate.py:500` 的 `unseal_for_prompt_free`:

- 它叫 `for_prompt`,但**不调** `unseal_for_prompt`、也不朝模型去;它转的是
  `unseal_for_client`(出口③),并把 `caller` **硬编码成 `TRUSTED_LOCAL`**(:502);
- 调用点两处:`gate.py:421`(`issue_confirm_ticket`)· `:454`(`confirm_pending`),都是面板确认流程。

⇒ 建议改名 **`unseal_for_panel_confirm`**,并把那句硬编码的 `caller` 写成**显式传入**
(由调用方给出真实档位),或至少在注释里写清「面板路径无条件自称 trusted-local」这个前提。
★ 这一项**不属于 sink 轴**,但它是同一个文件族里「名字在说谎」的另一处,
放在这里是因为改 sink 的人一定会读到它。

---

## 4. M2:必须等 D81 待裁 1 的部分

**M2 的全部内容都是「`sink` 的取值从哪来」,而那正是待裁 1。**

### M2-1 · 阻塞项:`sink` 由谁解析

第一个生产调用点出现时,它必须回答「这次请求的答案要发到哪」。而:

- 今天**没有任何生产调用点**(实测)⇒ M1 落地后系统行为**零变化**;
- 第一个生产调用点将在方向 B / P3d 接桥时出现;
- 那时 `sink` 必须由**调用方档位**推出,而这条映射就是待裁 1
  (`unregistered-local` → 出境 sink 还是本地 sink)。

⇒ **M2 不得先于待裁 1 动手。** 若先写了一个「暂时都当本地 sink」的默认映射,
那等于把待裁 1 悄悄裁成了「本地 sink」,而且没有决议、没有 diff 提示。

### M2-2 · 待裁 1 落地前必须先修的一条(本车道实测发现)

**`unregistered-local` 今天同时收容两类东西,而它们的正确处置方向相反:**

- **未登记账户**(实测 3 个启用账户:`Alle` 访客 · `CodexSandboxOffline` · `CodexSandboxOnline`);
- **身份解析失败** —— `classify_caller`(`gateway.py:528-542`)每个身份分支都是 `if ident and ...`,
  而 `caller_identity.resolve_account`(`:194-204`)有 **4 条 `return None`**,含一条**裸 `except Exception`**。

⇒ 一次瞬时 WMI / 端口表抖动会把**任何**调用方降到 `unregistered-local`,**包括 `Zori Ma` 自己**。
若待裁 1 裁「出境 sink」而不先拆开这两类,后果是:
**机主偶发地被静默关进出境侧**(还会按 §4.6.3 顺带卸掉 `memory.search`),且无任何告知。

**建议的改动(归主执行层,`gateway.py` + `caller_identity.py`)**:
给「解析失败」一个独立档位(如 `identity-unresolved`),与 `unregistered-local` 分开:

| 档位 | 语义 | 建议的 sink |
|---|---|---|
| `unregistered-local` | 身份**解析成功**,但不在 `caller-accounts.toml` 的 allowlist 里 | **出境 sink**(它可能就是个桥) |
| `identity-unresolved` | 身份**解析失败**(WMI 抖动 / 端口表竞态 / 进程已退出) | 单独裁。★ 无论怎么裁都**必须可观测**(计数 + 日志),不许静默 |

★ 顺带:D81 决定 1-2 已记「网关档位词表与 `tainted.CallerTier` 是**两个不同集合**,无映射、无断言」。
M2 的映射函数正是补这个洞的地方 ⇒ 建议一并加一条**穷举断言**:
`classify_caller` 的每个可能返回值都必须能映射到某个 `CallerTier` 与某个 `Sink`,
**新增档位而不登记 ⇒ 拒绝启动**(照 `ROUTE_TIERS` + `unclassified_routes()` 那个已验证的形状)。

---

## 5. 提交切分建议(每条都能独立跑绿)

| 提交 | 内容 | 依赖 | 落地后系统行为 |
|---|---|---|---|
| C1 | §1.1 台账 `sink` → `sink_label`(纯改名) | 无 | 零变化 |
| C2 | M1-1 `Sink` 类型 + M1-2 签名与判据 + M1-3 十一处调用点 + M1-4 七条断言 | C1 | **零变化**(生产调用点为 0),但类型层从此能表达「答案去哪」 |
| C3 | M1-5 `unseal_for_prompt_free` 改名 + 硬编码 caller 的处置 | 无(可与 C1/C2 并行) | 零变化 |
| C4 | M2-2 拆 `identity-unresolved` 档位 + 穷举断言 | 待裁 1 已裁 | **有行为变化**,须单独验收 |
| C5 | M2-1 sink 解析接线 | C4 + 待裁 1 | 有行为变化 |

★ **C1+C2 建议一次提交进去**(不要留「台账改了名但签名还没加」的中间态);
C4/C5 **不得**在待裁 1 之前动。

---

## 6. 验收口径

M1 做完时应当能说出这三句,且每句都有一条测试对应:

1. **`unseal_for_prompt` 少给 `sink` 会当场 TypeError** —— 新轴不可能被默默跳过(断言 1);
2. **本地 backend + 出境 sink 的组合被拒** —— D81 决定 1-1 那条失效路径在类型层已堵(断言 5);
3. **出境 sink 对 S0/S1/S2 一律拒** —— D39:1243「拒绝全部记忆正文而不只是 S2」已兑现(断言 3)。

**做不到第 2 条 ⇒ 这次改动没有解决它要解决的问题**,只是多了一个参数。

### 6.1 ★★ 门禁实况:这七条断言落进去之后,**没有任何东西会跑它们**

这一条我起草时写错过一次(原稿说「`test_tainted.py` 会被 `-Full` 收到」),**实测更正如下**:

| 关卡 | 会不会跑 `test_tainted.py` | 依据 |
|---|---|---|
| `.githooks/pre-commit` 自检段 | **不会** —— 只在 `10-core/(gateway\|gpu-broker)/` 有改动时才触发 | 钩子自述 |
| `90-ops/run-tests.ps1` 的反向全表扫描 | **扫到了,但不判红** —— `10-core\memory` 已在 `$RULES` 里登记 | `run-tests.ps1:43-51, 62` |
| `90-ops/run-tests.ps1 -Full` **实际执行** | **不会跑** —— 该规则是 `Tier='manual'` · **`Runnable = $false`** | `run-tests.ps1:48-51` |

`$RULES` 给 `10-core\memory` 的 Reason 是:

> 连的是【真实】记忆库(dbname=memory),且 pg_ident 只映射 ai-mem —— 当前身份 SSPI 会被拒,
> 结构上跑不了;其中 `test_s9_drill.py` 还是 pg_dump/pg_restore 恢复演练。必须以 ai-mem 身份手动跑,不进自动门禁。

**这个理由对整个目录成立,但对 `test_tainted.py` 不成立**:

- 它的 import 只有 `io / json / logging / sys` + `tainted`(:7-17),**没有 psycopg、没有连库**;
- 本车道 2026-08-06 用**系统 python 直接跑通**:**75 PASS / 0 FAIL**,无需 `ai-mem` 身份、无需数据库。

⇒ **规则的粒度是「目录」,而事实的粒度是「文件」。** 于是 `test_tainted.py` ——
**类型层全部安全断言的所在地** —— 落在了一条为恢复演练写的豁免里,**从不被自动执行**。

★ 这不是「没登记所以判红」那种洞(那种 `run-tests.ps1:62-79` 的反向全表已经堵住了),
是**登记为「手动」之后就没人再管**的洞。表面上账是清的,实际上这个项目最核心的
一组安全断言没有任何自动关卡在看。⇒ 加了 §M1-4 那七条,**默认也是没人跑**。

**建议(归 ops 车道,与 sink 改动并列,不阻塞它)**:
把 `$RULES` 从「目录粒度」放宽到允许**按文件例外**,并把确实不连库的几个提到 `fast`。
本车道只对 `test_tainted.py` 给出实测依据(零 DB 依赖 + 系统 python 跑通 75 PASS);
`test_gate.py` / `test_s1..s8_acceptance.py` 看起来也不直接连库,**但本车道未逐个实跑,不下断言**。

⇒ **在门禁能真的跑到它之前,M1 的验收只能是「人手跑一次并把输出贴进提交信息」** ——
这不理想,但把它写出来,好过让七条断言以为自己被守着。

---

## 7. 本清单**不**声称

- **不声称**方向 B 可以开工 —— D81 决定 4 的三路设计仍未达开工线,本清单只处理类型层的一维;
- **不声称**加了 `sink` 就守住了「记忆被模型复述后出境」—— 复述后无正则特征,
  这条 D81 决定 1-3 已定调,类型层管不到(那要靠 PLAN §4.6.3 的工具池裁剪,而它**尚未实现**);
- **不声称**迁移零风险 —— 生产调用点为 0 是**今天**的实测;若主执行层这期间新增了生产调用点,
  §2 的表要重数;
- **不代替**待裁 1 / 待裁 2 的裁定。本清单是「裁了要怎么做」,不是「就这么定了」。

---

## 8. 一手来源

- 行号与调用点清单:本车道 2026-08-06 实测,基准 `f600461`
  (`grep -rn "unseal_for_prompt(" --include=*.py`)
- 只读引用:`10-core/memory/tainted.py`(:66 `UnsealRecord.sink` · :85 `note` · :250-259 `Backend` ·
  :265 `_unseal` · :274/:287/:309/:337 四处台账标签 · :312 `unseal_for_prompt`)·
  `10-core/memory/gate.py`(:421 :454 :500 :502)·
  `10-core/memory/test_tainted.py`(:113 :122 :137 :139 :141 :148)·
  `10-core/memory/test_s4_acceptance.py`(:127 :129 :134 :135 :215)·
  `10-core/memory/test_s5_acceptance.py`(:229-234 AST 断言)·
  `10-core/gateway/gateway.py`(:528-542)· `10-core/gateway/caller_identity.py`(:194-204)
- 我方决议:**D39**(`DECISIONS.md:1242-1243` —— `sink` 必填的原始要求)· **D81**(待裁 1/2 · 决定 1-1 · 决定 4)
- 方案书:`PROJECT_PLAN_v3.0.md` §4.6.3(:355-365)
- 同车道:[egress-b-gates-impact-2026-08-06.md](egress-b-gates-impact-2026-08-06.md)(两道闸的实测形状 · 覆盖矩阵 · 爆炸半径)
