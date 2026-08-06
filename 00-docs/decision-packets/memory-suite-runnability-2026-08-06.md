# 记忆套件跑不起来 · 那道墙的形状 + 四条路的代价 · 裁定材料 **D?**

> ## ★★ 本包已停手,记忆套件这条线交给 **3 号执行层**(2026-08-06)
>
> 协调层告知:本包与 3 号车道(`worktree: memory-suite-revival-survey-94f0d0`)**正面重合** ——
> 3 号是协调层派去做记忆套件复活的,已提交 `b7a9d62`
> `memory-suite-revival-2026-08-06.md`(408 行)+ `run-memory-suite.ps1/.cmd`。
> **这是派重,不是本车道越界**(协调层未给本车道划这条边)。
>
> ⇒ **本包不再往前推进**;已写的内容**不删不改**,作为独立佐证保留。
> 唯一新增的是 **§11「交接给 3 号车道」** —— 协调层点名要过去的那一格。
> §5 的四条路、§6 的建议**不再更新**,以 3 号的裁定材料为准。

> 日期:2026-08-06
> 性质:**裁定材料**。要裁的是「**怎样才能让 `10-core/memory` 的断言真的被跑一次**」,
> 而它的难点不是工程量,是**每条可行的路都要削弱一部分「正被这些断言验证」的隔离**。
> 产出物:本文件 + `90-ops/spikes/memory-suite-runnability/static-connect-scan.py`
>
> **取号**:`DECISIONS.md` 已提交最大 **D88**,本包**不占号**,标题 `D?`。
> **边界**:本包**只新建**文件。`10-core/**` · `90-ops/*.ps1` · `config/**` · PG 的 `pg_hba/pg_ident`
> **一律未改**,只读。未改中央三文档。
>
> ★ 本轮**没有跑任何 `10-core/memory` 下的测试**(除已被独立复核过的 `test_tainted.py`)。
> 理由见 §1.2 —— 那正是本包要立的判据。

---

## 0. 一句话

**那道墙不是「没有密码」,是 `pg_ident` 只认一个 Windows 账户 —— 而那个账户同时被映射到
PG 超级用户 `postgres`。所以「让套件跑起来」的每条路,给出的都不是「读记忆库的权限」,
而是「记忆库的超级用户」。**

而且这些断言里有一部分**验的正是那个角色的权限边界本身**
(`ai_mem_local` 故意没有 DELETE、故意不能删冷启动标记、`ai_mem_remote` 对 `l4_procedure` 零授权)——
**换个身份去跑,它们就不再验真东西了。** 这是循环,不是麻烦。

⇒ 建议:**不要拓宽现有那条泳道,另开一条隔离的测试泳道**(新 Windows 账户 + 新 PG 角色 +
新数据库 + 一个 `dbname` 的 env 缝),使「只有 `ai-mem` 能碰 `dbname=memory`」**保持为真**。
代价是一次性的建账户 + 四处配置;收益是这批断言从此可跑,**且不用拿被测的性质去换**。

---

## 1. 本轮的实测纪律,以及一条我自己的更正

### 1.1 判据顺序:先静态排除,再跑;拿不准就不跑

`10-core/memory/test_*.py` 是**「import 即执行」**的形态 ⇒ 真跑起来若连上库,
可能对**真实记忆库**产生写入。所以本包的做法是:

1. 先用 AST 建**模块级 import 图**,标出自身含连库/网络代码的模块(种子),沿 import 边传播;
2. 只有**传递依赖上碰不到任何连库模块**的文件才判 `PURE`,允许跑;
3. 其余判 `NEEDS-BACKEND` = **不许跑**(不是「跑了会失败」),报告里写「未跑,因为无法静态排除写入」。

★ 判定刻意**偏保守**:传递路径上碰到一个连库模块就判 NEEDS-BACKEND,
哪怕那条路径运行时未必被走到 —— **宁可少跑,不可误写生产库。**

### 1.2 ★ 一条我要更正的东西:我上一轮那个 grep 是假信号

上一份清单里我写过「对 `test_gate.py` / `test_s1..s8_acceptance.py` 做过粗粒度静态查
(有没有 `psycopg` / `dbname=` 字样),**多数为 0 处**」,并据此说它们「看着也不直接连库」。

**那个 screen 是错的。** 本轮的 import 图显示:**它们全都传递地连库** ——
经 `repo`(`import psycopg`)或 `vectors`(`import httpx`)。
`grep` 只看**本文件**有没有那些字样,而连库发生在**被 import 的模块**里。

⇒ **如果当时信了那个 grep 去跑,就可能写进真实记忆库。**
幸好当时写的是「未实跑,不下断言」—— 这一次「没敢跑」确实兜住了。
**记在这里是因为**:这类「粗筛看着干净」正是本项目最贵的那种错的入口。

---

## 2. 那道墙的确切形状(全部实测)

### 2.1 认证:`pg_ident` 只认一个 Windows 账户

`D:\AI\state\memory\pg\18\data\pg_ident.conf`(去注释):

```
mem        ai-mem           postgres
mem        ai-mem           mem_rw
mem        ai-mem           ai_mem_local
mem        ai-mem           ai_mem_remote
```

`pg_hba.conf`(去注释):

```
host    all       postgres        127.0.0.1/32    sspi  map=mem  include_realm=0
host    memory    mem_rw          127.0.0.1/32    sspi  map=mem  include_realm=0
host    memory    ai_mem_local    127.0.0.1/32    sspi  map=mem  include_realm=0
host    memory    ai_mem_remote    127.0.0.1/32    sspi  map=mem  include_realm=0
```

**三条结论:**

1. **四个 PG 角色,同一张 `mem` 映射,同一个 Windows 主体 `ai-mem`。**
   ⇒ 以 `Zori Ma` 身份,**任何**角色都认证不上;
2. ★★ **第一行是 `all` + `postgres`** ⇒ Windows 账户 `ai-mem` 能以 **PG 超级用户**
   连**所有**数据库。测试自己就在用这一点做清场
   (`test_s3_acceptance.py:39`「用 postgres 清场 —— `ai_mem_local` 故意没有 DELETE 权限」)。
   ⇒ **「能跑这套测试」等价于「是这台机器上 PG 的超级用户」。**
   任何一条让人能跑它的路,给出的都不是「读记忆库」,是**超级用户**;
3. `repo.py:87` 那个 env 缝 `LOCALAI_PG_USER`(默认 `ai_mem_local`)**在这个问题上没用** ——
   它只能换「请求哪个角色」,而四个角色都卡在同一张 `map=mem` 上。
   `dbname=memory` 则**写死**在 `:88`,没有对应的 env 缝。

### 2.2 密码:没人持有,而且账户不许自己改

| 项 | 实测值 |
|---|---|
| `setup-accounts.ps1:11-13` | 随机 24 字符建号,**不显示、不保存**;装 PG/Qdrant 时又重置为新随机值并当场配进服务 —— 「密码只活在那一次运行里」 |
| `Get-LocalUser ai-mem` | `Enabled=True` · `PasswordLastSet=2026-07-28` · `PasswordExpires=(永不)` · **`UserMayChangePassword=False`** |

### 2.3 ★ 重置密码的爆炸半径:四个正在跑的服务

```
Name        StartName   State     StartMode
Embedding   .\ai-mem    Running   Auto
pg-mem      .\ai-mem    Running   Auto
Qdrant      .\ai-mem    Running   Auto
Qdrant-s2   .\ai-mem    Running   Auto
```

⇒ `Set-LocalUser -Password` **不会**更新服务里存的凭据。
重置密码而不同步改这**四个**服务的登录凭据 ⇒ **下次重启整个记忆栈起不来**。

### 2.4 墙是 **PG 专属**的 —— Qdrant 不在墙后

`vectors.py:162-182` 的 api_key 是**从磁盘上的 `config.yaml` 读**的,不是 Windows 身份。
实测:两个实例的 `config.yaml` **管理员可读**(各含 1 行 `api_key`),
因为 `{state}\memory` 的 ACL 是 `Administrators:(OI)(CI)(F)` + `ai-mem:(F)`,
只对 `ai-asset` / `ai-exec` 是 `(N)` 真 Deny。

⇒ **Qdrant 那一半的断言并不缺身份,缺的只是 PG。**
(今天这个区分不改变结论,因为 13 个 NEEDS-BACKEND **全都**同时经 `repo` ——
但它决定了将来拆分的可能形状,见 §5 的 P5。)

---

## 3. 静态分类结果:**1 能跑 / 13 不许跑**

`90-ops/spikes/memory-suite-runnability/static-connect-scan.py`(只 `ast.parse`,不执行被测脚本):

**种子(自身含连库/网络代码)**:`repo`(psycopg)· `vectors`(httpx)·
`test_route`(requests)· `test_s2_acceptance`(httpx)· `test_s3_repo`(psycopg)

| 文件 | 判定 | 模块级调用数 | 经由 |
|---|---|---|---|
| `test_tainted.py` | **PURE(可跑)** | 181 | 只依赖纯模块 |
| `test_gate.py` | NEEDS-BACKEND | 113 | `repo` |
| `test_repo.py` | NEEDS-BACKEND | 116 | `repo` |
| `test_route.py` | NEEDS-BACKEND | 61 | 自身 `requests` |
| `test_s1_acceptance.py` | NEEDS-BACKEND | 67 | `repo` |
| `test_s2_acceptance.py` | NEEDS-BACKEND | 158 | `repo` / 自身 `httpx` / `vectors` |
| `test_s3_acceptance.py` | NEEDS-BACKEND | 196 | `repo` |
| `test_s3_repo.py` | NEEDS-BACKEND | 94 | `repo` / 自身 `psycopg` |
| `test_s4_acceptance.py` | NEEDS-BACKEND | 130 | `repo` / `vectors` |
| `test_s5_acceptance.py` | NEEDS-BACKEND | 127 | `repo` / `vectors` |
| `test_s6_acceptance.py` | NEEDS-BACKEND | 86 | `repo` / `vectors` |
| `test_s7_acceptance.py` | NEEDS-BACKEND | 61 | `repo` / `vectors` |
| `test_s8_acceptance.py` | NEEDS-BACKEND | 103 | `repo` |
| `test_s9_drill.py` | NEEDS-BACKEND | 87 | `repo` / `vectors` |

⇒ **「先把纯逻辑的挑出来跑」这条路已经走到尽头**:它只产出 `test_tainted.py` 一个,
而那一格已由 [test-tainted-gate-promotion-2026-08-06.md](test-tainted-gate-promotion-2026-08-06.md) 处置完毕。
**剩下 13 个,没有免费的午餐。**

★ 附带一条:那些文件的「模块级调用数」在 61–196 之间 ——
**它们不是「有个 main() 等你调」,是 import 就跑完整套**。这印证了「不许试探性地跑一下」。

---

## 4. ★★ 循环:有一部分断言,验的正是你为了跑它而要改的东西

这不是修辞。逐条点名(全部只读引用):

| 断言 | 它在验什么 | 换身份跑之后会怎样 |
|---|---|---|
| `test_s3_repo.py:47-51` `★ ai_mem_local 对 {tbl} 无 DELETE 权限` | **PG 角色 `ai_mem_local` 的权限边界**(§12.4 永不 delete) | 若以 `postgres` 或一个新测试角色跑 ⇒ 它验的是**那个角色**的授权,与生产角色无关 ⇒ **断言还在,意义没了** |
| `test_s6_acceptance.py:124-132` `★ ai_mem_local 不得删除冷启动标记(权限层拒绝)` | 同上,且「否则可重开」 | 同上 |
| `test_s8_acceptance.py:154` `ai_mem_remote 对 l4_procedure 零授权` | 第三个角色的边界 | 同上 |
| `test_s3_acceptance.py:39` / `test_s3_repo.py:18` 「用 `postgres` 清场」 | —— | 这两处**依赖** `pg_ident` 把 `ai-mem` 映射到超级用户;它是套件的**前提**,不是被测项 |
| `test_repo.py:168` / `test_s1_acceptance.py:40` 连不上就 `skip` 并写明「需以 ai-mem 身份运行(SSPI)」 | —— | ★ **这两处是诚实的**:它们不静默假通过。但「一直 skip」= 这些断言从来没绿过 |

⇒ **循环的精确形状**:

> 这批断言的价值来自「跑它的身份**恰好是**生产身份」。
> 而生产身份今天无人可用。
> 一旦为了跑它而换一个身份,**它们就从「验生产隔离」退化成「验测试环境的自洽」**。

★ 所以「随便造个测试角色跑一遍拿到绿灯」是**最坏的选项** ——
它会产出一屏漂亮的 PASS,而其中至少三条已经不再验真东西,**且没有任何东西会提示这件事**。
这正是本项目固定审查视角里最贵的那一类:**看着有防护、实际没有。**

---

## 5. 四条路的判据表

| | **P1 · 以 `ai-mem` 身份跑** | **P2 · 给机主加 `pg_ident` 映射** | **P3 · 指向一次性测试库** | **P5 · 另开隔离测试泳道**(建议) |
|---|---|---|---|---|
| 要动什么 | 重置 `ai-mem` 密码 + **同步改 4 个服务的登录凭据** | `pg_ident.conf` 加一行 + `pg_ctl reload` | `repo.py:88` 加 `dbname` env 缝 | 新 Windows 账户 + 新 PG 角色 + 新库 + 新 `pg_hba` 行 + `dbname` env 缝 |
| 能解决认证吗 | ✔ | ✔ | **✗ 完全不解决** —— 新库仍要过 `pg_hba`,角色仍只从 `ai-mem` 映射 | ✔ |
| 授出的权限 | **PG 超级用户**(`map=mem` 含 `postgres`) | **PG 超级用户**(同一张映射) | — | 仅新库上的新角色,**不含 `dbname=memory`** |
| 谁获得它 | 任何知道新密码的人 | ★ **每一个以 `Zori Ma` 运行的进程** —— 而本机 `EnableLUA=0`,一切进程皆 High ⇒ 含跑在机主账户下的任何 AI agent | — | 仅该测试账户 |
| 可逆吗 | 密码可再改,但**「无人持有」这个性质一旦破就回不去** | ✔ 删行 + reload | ✔ | ✔(删账户/角色/库) |
| 会作废哪些断言 | **不会** —— 身份就是生产身份 ⇒ **P1 是唯一能让 §4 那三条继续有意义的路** | §4 三条**退化**(除非仍以 `ai_mem_local` 角色连,但那时机主也拿到了它的全部权限) | — | §4 三条**退化**,除非泳道里**复刻**同名角色与同样的授权(见 §6.2) |
| 失败模式 | 漏改任一服务 ⇒ **下次重启整个记忆栈起不来** | 忘删那一行 ⇒ 记忆库对机主长期敞开,**且没有任何断言会红** | 以为解决了认证 ⇒ 白做 | 泳道与生产漂移 ⇒ 绿灯与生产无关 |
| 今天可执行吗 | 要管理员 + 停改 4 个服务(**按 D46 纪律,不由 agent 代跑**) | 要管理员 + 改 PG 配置 | 要改生产代码(主执行层) | 要管理员 + 建账户(**须先有决议**) |

★ **P3 不是一条独立的路** —— 它只消掉「可能写真实库」这一半风险,不动认证。
它的正确定位是 **P1/P2/P5 的必配项**,不是替代品。

---

## 6. 建议

### 6.1 建议采 **P5**,并且**不要**为了省事走 P2

理由三条:

1. **P2 的代价在本机被放大**:`EnableLUA=0`(见 `STATE.md` 环境事实表)⇒ 机主账户的一切进程都是
   High + Administrators enabled。给 `Zori Ma` 加一条 `pg_ident` 映射,等于把**记忆库的超级用户**
   发给「任何以机主身份跑的东西」,其中包括在这个仓库里跑的 AI agent。
   ⇒ 这与 D30「混淆代理」那整条推理**直接冲突**(`caller_identity.py:5` 自陈:
   「一旦经网关触达记忆…就是绕过隔离读全库的混淆代理」);
2. **P1 破掉的是一个结构性性质**:`setup-accounts.ps1` 刻意让密码「只活在那一次运行里」。
   一旦有人持有它,§6.8 的账户隔离就从「无人可冒充」退成「有人知道口令」,
   而这个退化**不可逆、也不会被任何断言发现**;
3. **P5 让被测性质保持为真**:「只有 `ai-mem` 能碰 `dbname=memory`」在 P5 下**仍然成立** ——
   泳道用的是另一个 (账户, 角色, 库) 三元组。

### 6.2 P5 要成立,必须解决 §4 的退化 —— 具体做法

泳道里**复刻角色名与授权**,而不是随手给一个宽权限角色:

- 新库 `memory_test` 里建**同名角色** `ai_mem_local` / `ai_mem_remote` / `mem_rw`,
  **授权逐条照生产 grant 抄**(§12.4「永不 delete」等边界必须一致);
- `pg_hba` 只加 `host memory_test <role> 127.0.0.1/32 sspi map=memtest`,
  `pg_ident` 加 `memtest <测试账户> <role>` —— ★ **不得**复用 `map=mem`,
  否则会把测试账户也接到生产库上;
- ★ **加一条反向断言**:`pg_ident` 里 `mem` 这张映射的 SYSTEM-USERNAME 列
  **有且只有 `ai-mem`** —— `worklog/2026-08.md:222-227` 记着 `verify-isolation.ps1` 已为此新增 ⑤ 段,
  **本建议要求它继续成立**,并把「`memtest` 映射不得出现在 `map=mem` 里」一并纳入;
- 授权一致性本身也要有断言(否则泳道会漂移):
  用 `postgres` 身份对比两个库里同名角色的 `information_schema.role_table_grants`,**不一致即判红**。

⇒ 这样 §4 那三条断言在泳道里**验的仍是同一组授权边界**,只是数据库不同。
**没有这条授权复刻 + 一致性断言,P5 退化成「一屏没有意义的 PASS」,不如不做。**

### 6.3 短期(不等裁定就能做的两件,零生产改动)

1. **`test_tainted.py` 进 fast 层** —— 已在
   [test-tainted-gate-promotion-2026-08-06.md](test-tainted-gate-promotion-2026-08-06.md) 给出清单;
2. ★ **把 `verify-isolation.ps1` 接进门禁**(见 §7)—— 它验的正是账户/ACL/`pg_ident` 那一层,
   **不需要 `ai-mem` 身份**,而现在**没有任何门禁会跑它**。
   这是「今天就能让一部分隔离验证真的有人跑」的唯一一条零成本路径。

### 6.4 推翻条件

1. 若用户裁定**接受** P2(机主长期可连记忆库)⇒ 本建议作废,但必须同时:
   ① 在 `STATE.md` 环境事实表记一行(它改变了一条承重性质);
   ② 给 §4 那三条断言各加一条注释,写明「本断言在当前配置下不再验生产隔离」;
2. 若 `ai-mem` 的密码在某处**其实**有留存(本包只查了 `setup-accounts.ps1` 与账户属性,
   **没有全盘搜凭据管理器 / DPAPI / 服务凭据存储**)⇒ P1 的代价大幅下降,应重比价;
3. 若 `pg_ident` 的 `map=mem` 被裁定移除对 `postgres` 的映射
   (即测试不再靠超级用户清场)⇒ §2.1 结论 2 失效,P1/P2 授出的权限从超级用户降为角色权限,
   三条路的代价全部要重算;
4. 若记忆套件被重构成「注入连接对象」的形态(而非 import 即执行 + 模块级 `repo.connect()`)
   ⇒ 静态分类的结论(13 个不许跑)可能变化,应重扫。

---

## 7. 顺带发现:**验隔离的那个验证器,没有任何门禁会跑它**

`90-ops/verify-isolation.ps1` —— 它验账户存在性、**ACL 是真 Deny 而不是空 Allow**、
`secrets` 的两条性质,以及(据 `worklog/2026-08.md:227`)新增的 `pg_ident` 反向全表断言。

实测:`run-tests.ps1` 与 `.githooks/pre-commit` **都不引用它**
(`grep verify-isolation` 在两者中零命中)。全仓引用它的只有文档、worklog、
`install-postgres.ps1:230` 的一句提示、以及几个脚本说「解析器与它同源」。

⇒ **它是一个纯手动工具。** 而它的内容恰恰是「隔离到底还在不在」——
本项目最不该靠人记得去跑的那一类。

★ 这是本车道两天内遇到的**第三个同族形状**:
① `90-ops/debug/selfcheck.py` 不叫 `test_*.py`,扫描器收不到(审计 ⑦ 已修);
② `10-core\memory` 登记为 `Runnable=$false`,类型层断言从不被跑(上一份清单);
③ **`verify-isolation.ps1` 谁都不跑**。
共同根因:**门禁只认一种形状(`10-core` 下的 `test_*.py`),而承重检查有三种形状。**
⇒ 建议(归 ops 车道):`run-tests.ps1` 增加一节显式跑 `90-ops/verify-isolation.ps1`,
照它已有的「调试工具箱自检」那一节的体例(**存在才跑,删掉了要判红**)。

---

## 8. 覆盖账

### 8.1 没测到的

| 未测的 | 为什么 |
|---|---|
| **13 个 NEEDS-BACKEND 测试的实际 PASS/FAIL** | **未跑,因为无法静态排除对真实记忆库的写入**(§1.1 判据)。这是有意的,不是遗漏 |
| `ai-mem` 密码是否在别处有留存 | 只查了 `setup-accounts.ps1` 与 `Get-LocalUser` 属性;**没有**去搜凭据管理器 / DPAPI / 服务凭据存储(那本身是敏感面,不在本轮授权内) |
| `verify-isolation.ps1` 实跑结果 | **未跑**。它有一段用 `psql` 的「DB 角色分离」检查;以机主身份跑会落 SKIP 或 FAIL,而我无法静态排除它某个分支有副作用 ⇒ 按本包自己的判据,不跑 |
| P5 的工程量 | 只给了要素清单,**未估工时**;也未验证新建 Windows 账户是否与项目账户纪律冲突(§6.8 只列了三个服务账户) |
| Qdrant 侧单独可跑性 | §2.4 证明 Qdrant 不缺身份,但 13 个测试**全都**同时经 `repo` ⇒ **没有**「只连 Qdrant 就能跑」的现成文件,故未进一步测 |

### 8.2 我造成的机器状态改动

**无。** 本轮只读文件 + 跑一个只做 `ast.parse` 的脚本 + 只读的 `Get-CimInstance` / `Get-LocalUser` /
`icacls` / 读 `pg_hba`·`pg_ident`。**没有连过 PG,没有连过 Qdrant,没有跑任何被测脚本。**
`git status --short 10-core/ 90-ops/ config/` 为空。

### 8.3 门禁覆盖

- 本 worktree 跑 `-Full` 报「客户端自检 — 没有构建产物」——worktree 正常形状,**未去修**;
- 本包未动 `10-core/` ⇒ pre-commit 自检段不触发;
- 绝对路径:勘察脚本用钩子自己的正则扫过,零命中(路径由 `Path(__file__).parents[3]` 推导)。

---

## 9. spike 的性质:**(a) 一次性勘察产物,不进门禁**

`90-ops/spikes/memory-suite-runnability/static-connect-scan.py` —— 口径同前两轮。

★ **但它有一条值得长期守**(建议,归 ops 车道,本包不动):
把「静态连库扫描」做成 `run-tests.ps1` 的**准入检查** ——
任何被标为 `Runnable=$true` 的测试文件,若静态扫出连库路径,**判红**。
这样「某天有人把一个连库测试提进 fast 层」会当场被拦,而不是等它写坏一次生产库。
⇒ 这条比本包的任何结论都更值得留下来。

---

## 10. 一手来源

- 实测(2026-08-06,本车道):`static-connect-scan.py` 输出(1 PURE / 13 NEEDS-BACKEND)·
  `Get-CimInstance Win32_Service`(4 个服务跑在 `.\ai-mem`)· `Get-LocalUser ai-mem`
  (`UserMayChangePassword=False`)· `icacls D:\AI\state\memory` · 两个 `qdrant/config.yaml` 可读性
- 只读引用:`D:\AI\state\memory\pg\18\data\{pg_ident,pg_hba}.conf` ·
  `90-ops/setup-accounts.ps1`(:11-13 密码不保存)· `90-ops/verify-isolation.ps1`(:106-117 psql 段)·
  `90-ops/run-tests.ps1`(:43-51 `$RULES`)· `.githooks/pre-commit` ·
  `10-core/memory/repo.py`(:17 SSPI 说明 · :80-88 `_dsn` · :87 `LOCALAI_PG_USER`)·
  `10-core/memory/vectors.py`(:162-182 `_api_key`)·
  `10-core/memory/test_s3_repo.py`(:18 :47-51)· `test_s6_acceptance.py`(:124-132)·
  `test_s8_acceptance.py`(:154)· `test_s3_acceptance.py`(:39)· `test_repo.py`(:5 :168)·
  `test_s1_acceptance.py`(:40)· `10-core/gateway/caller_identity.py`(:5 混淆代理)
- 配置:`config/paths.toml`(`[memory]` 段)· `config/caller-accounts.toml`
- 我方决议:`DECISIONS.md` D22 · D30 · D46(提权纪律)· D75/D82 ·
  `STATE.md` 环境事实表(PostgreSQL 那一行 · UAC 那一行)
- 同车道:[test-tainted-gate-promotion-2026-08-06.md](test-tainted-gate-promotion-2026-08-06.md) ·
  [sink-axis-change-list-2026-08-06.md](sink-axis-change-list-2026-08-06.md) ·
  [egress-b-gates-impact-2026-08-06.md](egress-b-gates-impact-2026-08-06.md)
- worklog:`2026-08.md:222-227`(`verify-isolation.ps1` 新增 `pg_ident` 反向全表断言)

---

## 11. ★★ 交接给 3 号车道:那道墙给的不是「读库权限」,是 **PG 超级用户**

> 协调层点名要这一格,理由是 3 号是从「范围」那头进来的(哪些套件不需要活库),
> 可能量不到「让它跑起来要付什么代价」这一格。以下是**判据 + 我怎么量出来的**。

### 11.1 结论一句话

**任何让人能跑这套测试的路,授出的都不是「读记忆库」,而是「这台机器上 PG 的超级用户」。**
因此「先让它跑起来再说」这个直觉是错的:它不是降低门槛,是**授出最高权限**。

### 11.2 判据链(三步,每步都可复核)

**第一步 · 认证只认一个 Windows 账户。**
`D:\AI\state\memory\pg\18\data\pg_ident.conf`(去注释后**全文只有四行**):

```
mem        ai-mem           postgres
mem        ai-mem           mem_rw
mem        ai-mem           ai_mem_local
mem        ai-mem           ai_mem_remote
```

`pg_hba.conf`(去注释后**全文只有四行**):

```
host    all       postgres        127.0.0.1/32    sspi  map=mem  include_realm=0
host    memory    mem_rw          127.0.0.1/32    sspi  map=mem  include_realm=0
host    memory    ai_mem_local    127.0.0.1/32    sspi  map=mem  include_realm=0
host    memory    ai_mem_remote    127.0.0.1/32    sspi  map=mem  include_realm=0
```

**第二步 · ★ 第一行是 `all` + `postgres`。**
即 Windows 账户 `ai-mem` 可以 **PG 超级用户身份连所有数据库**。
这不是推断 —— **测试自己就在用它**:
`test_s3_acceptance.py:39` 与 `test_s3_repo.py:18` 的清场注释原话是
「用 `postgres` 清场 —— `ai_mem_local` **故意没有 DELETE 权限**(§12.4)」。
⇒ 超级用户不是副作用,是**这套测试的既有前提**。

**第三步 · 那个 env 缝不构成第三条路。**
`repo.py:87` 有 `user = os.environ.get("LOCALAI_PG_USER", "ai_mem_local")`,
看着像可以换身份;但四个角色**共用同一张 `map=mem`** ⇒ 换角色不换 Windows 主体,
以机主身份一个都认证不上。`dbname=memory` 则**写死**在 `:88`,没有对应的 env 缝。

**独立印证(不靠我读文件):** `90-ops/verify-isolation.ps1` 实跑
(2026-08-06,机主身份,只读,**17 PASS · 0 FAIL · 3 SKIP**,退出码 0)其中两条:

```
PASS  ★★ pg_ident 的 SYSTEM-USERNAME 列有且只有 ai-mem(反向全表)
PASS  ★ pg_hba 无 trust/md5/password 兜底行(只认 SSPI 绑 SID)
SKIP  DB 角色分离 — 连不上 —— 需以 ai-mem 身份运行(生产即如此)
```

★ 而该脚本自己也已经把超级用户这条记下来了(`:212-217`):

> ⚠ 有 N 条映射指向 PG 超级用户 postgres —— 超级用户绕过一切权限检查,
> 故 `roles.sql` 的 REVOKE 只对非超级角色成立(**文档不得声称 append-only 绝对**)

⇒ **这条不是我的新发现,是既有认知**;我补的是它对「怎么让套件跑起来」的**定价**。

### 11.3 对 P3a S4/S9 那批「验隔离本身」的断言意味着什么

有一批断言,**验的正是 PG 角色的权限边界**:

| 断言 | 验什么 |
|---|---|
| `test_s3_repo.py:47-51` | `★ ai_mem_local 对 {tbl} 无 DELETE 权限`(§12.4 永不 delete) |
| `test_s6_acceptance.py:124-132` | `★ ai_mem_local 不得删除冷启动标记(权限层拒绝)` —— 否则可重开 |
| `test_s8_acceptance.py:154` | `ai_mem_remote 对 l4_procedure 零授权` |

**它们的价值来自「跑它的身份恰好是生产身份」。** 于是:

1. **以 `postgres`(超级用户)跑 ⇒ 这三条必然「通过」而毫无意义** ——
   超级用户绕过一切权限检查,`REVOKE` 对它不成立(见 §11.2 那段 ⚠)。
   ★ 更糟的是它们会打印 **PASS**;
2. **以一个新造的宽权限测试角色跑 ⇒ 同样退化**,它验的是那个角色的授权,不是生产角色的;
3. ⇒ **想让这三条继续有意义,泳道里必须复刻角色名 + 逐条复刻授权**,
   并加一条**授权一致性断言**(用 `postgres` 比对两库同名角色的
   `information_schema.role_table_grants`,不一致判红)。
   没有这条,复活出来的是**一屏没有意义的 PASS** —— 比一直 SKIP 更坏,
   因为 SKIP 是诚实的(`test_repo.py:168` / `test_s1_acceptance.py:40` 现在就是这么做的)。

### 11.4 ★ 我这份的数字与 3 号的「4 个不需要活库」**不矛盾**,别当成互相打架

我的静态扫描报的是 **1 PURE / 13 NEEDS-BACKEND**;3 号的提交标题是「**4 个**不需要活库」。
**两个数回答的是两个不同的问题:**

| | 我问的 | 3 号问的 |
|---|---|---|
| 问题 | 能不能**静态排除**「跑起来会写真实库」? | 哪些套件**实际上**不需要活库? |
| 判据 | 传递 import 图上碰到任一连库模块即判不许跑 | 需要逐文件看那个 connect 有没有被 guard 住 |
| 性质 | **刻意偏保守的下界** | 更细的真实值 |

我的工具是钝的,而且是**故意**钝的(脚本注释原话:「宁可少跑,不可误写生产库」)。
它把「import 了 `repo` 但把连库包在 guard 里、连不上就 skip」的那些**一律算进不许跑**——
而那正是 `test_repo.py:5` 与 `:168` 的形状(「连不上则跳过并明确说明,不静默假通过」)。

⇒ **以 3 号的 4 为准**;我的 1 只应被读作「在不做逐文件人工判断的前提下,能安全断定的最小集」。
**不要把我的 1 当成对 4 的反驳。**

### 11.5 交接清单(3 号可直接取用的实测值)

| 实测值 | 来源 |
|---|---|
| `pg_ident` 全文四行,SYSTEM-USERNAME 列**只有** `ai-mem` | 读文件 + `verify-isolation.ps1` 反向全表断言 PASS |
| `pg_hba` 全文四行,全 `sspi map=mem`;**第一行 `all`+`postgres`** | 读文件 |
| 「能跑套件」⇒ **PG 超级用户** | 上两条 + `test_s3_acceptance.py:39` 用 `postgres` 清场 |
| `ai-mem` 密码**无人持有**;`UserMayChangePassword=False` | `setup-accounts.ps1:11-13` + `Get-LocalUser` |
| **四个**服务跑在 `.\ai-mem`:`pg-mem` · `Qdrant` · `Qdrant-s2` · `Embedding`(全 Running/Auto) | `Get-CimInstance Win32_Service` |
| ⇒ 重置密码必须**同步改四个服务凭据**,漏一个下次重启整栈起不来 | 上一条 |
| 墙是 **PG 专属**:Qdrant 的 api_key 从磁盘 `config.yaml` 读,**管理员可读** | `vectors.py:162-182` + 实测两个 config 可读 |
| `LOCALAI_PG_USER` 缝**无用**;`dbname=memory` 写死 | `repo.py:87-88` |
| 「验隔离本身」的三条断言在换身份后会**退化成 PASS** | §11.3 |

★ **本车道未测**:`ai-mem` 密码是否在凭据管理器 / DPAPI / 服务凭据存储里有留存
(那是敏感面,不在本轮授权内)。**这一条是 P1 定价的推翻条件** ——
若其实有留存,「重置密码」的代价大幅下降,四条路要重比价。**建议 3 号把它列进自己的待核实。**

---

## 12. 上一条指示的收尾:`verify-isolation.ps1` 接进门禁(**已测完,不再推进**)

> 协调层在「停手」之前要过这份清单,数据已全部测到,故一并交出。**本节不扩展。**

**它今天就是绿的,而且几乎不花时间** —— 接进去是低风险的:

| 实测项 | 值 |
|---|---|
| 以机主身份跑(不需要 `ai-mem`) | **17 PASS · 0 FAIL · 3 SKIP**,退出码 **0** |
| 耗时 | **0.5 秒** |
| 汇总行 | `=== 隔离验证:17 PASS · 0 FAIL · 3 SKIP ===` |
| 是否只读 | ✔ 静态扫零处写操作;唯一 SQL 是对 `pg_roles` / `information_schema` 的 `SELECT` |
| ★ 对照:`run-tests.ps1` fast 层现耗时 | **189.3 秒**(≈3 分钟)—— 见 §12.3 |

### 12.1 汇总行**已经**匹配现成的判据(不用改正则)

`run-tests.ps1` 的锚定正则 `^\s*===.*\d+\s*PASS.*\d+\s*FAIL` 与抽取式
`(\d+)\s*PASS[^0-9]+(\d+)\s*FAIL` **全是 ASCII**,而 `=== 隔离验证:17 PASS · 0 FAIL · 3 SKIP ===`
里 `===` / `PASS` / `FAIL` / 数字都是 ASCII ⇒ **即使在 cp936 下中文与 `·` 变成乱码,判据依然命中。**

★ 这一条必须写出来,因为 `run-tests.ps1:161-171` 记着一次**实测被拒**的事故:
第一版拿 `·`(U+00B7)当分隔符去匹配,作者终端里好好的,而 pre-commit 是从 git bash 起、
码页 cp936 ⇒ 匹配不上 ⇒ 门禁报「没跑起来」并拒绝提交。
⇒ **接线时不得引入任何非 ASCII 判据**,照那一节的体例。

### 12.2 建议的接法(照「调试工具箱自检」那一节的体例)

在 `run-tests.ps1` 的调试工具箱自检那一节**之后**,加一节:

```powershell
# --- 隔离验证(★ 存在才跑,删掉了自动跳过)---------------------------------
#  ★ 它验账户存在 / ACL 是真 Deny 而不是空 Allow / secrets 排除出备份 /
#    pg_ident 的 SYSTEM-USERNAME 反向全表 / pg_hba 无 trust 兜底行。
#    这些是「隔离到底还在不在」,最不该靠人记得去跑。
#  ★ 它不需要 ai-mem 身份:DB 角色分离那一段连不上会 Skip-It(生产即如此)。
#  ★ 判据只认 ASCII(理由同上一节:cp936 下中文与 · 会变乱码)。
$isoScript = Join-Path $repo '90-ops\verify-isolation.ps1'
if (Test-Path $isoScript) {
    $isoOut = & powershell -NoProfile -ExecutionPolicy Bypass -File $isoScript 2>&1 | Out-String
    $isoLine = ($isoOut -split "`r?`n" |
                Where-Object { $_ -match '^\s*===.*\d+\s*PASS.*\d+\s*FAIL' } |
                Select-Object -Last 1)
    if ($isoLine -match '(\d+)\s*PASS.*?(\d+)\s*FAIL') {
        $ip = [int]$Matches[1]; $if_ = [int]$Matches[2]
        $totalPass += $ip; $totalFail += $if_
        $ran += '90-ops\verify-isolation.ps1'
        $c = if ($if_ -gt 0) { 'Red' } else { 'DarkGray' }
        Write-Host ("  {0,-46} PASS={1,-5} FAIL={2}" -f '90-ops\verify-isolation.ps1', $ip, $if_) -ForegroundColor $c
        if ($if_ -gt 0) { ($isoOut -split "`r?`n" | Where-Object { $_ -match 'FAIL' } | Select-Object -First 8) | ForEach-Object { Write-Host "        $_" -ForegroundColor Red } }
    } else {
        $broken += [pscustomobject]@{ File = '90-ops\verify-isolation.ps1'; Why = '没有汇总行 —— 多半没跑起来' }
        Write-Host "  X 隔离验证没跑起来" -ForegroundColor Red
    }
}
```

### 12.3 ★ 两条接线前必须先处理的

1. **它需要「能读 ACL」的令牌。** 它 `Get-Acl` 读 `{state}\memory` 与 `{state}\secrets`,
   而那两处的 ACL 只给 `Administrators` / `SYSTEM` / `ai-mem`。
   本机 `EnableLUA=0`,一切进程皆 High ⇒ **今天恒可读**;
   但在**UAC 开启**的机器上,普通 `git commit` 起的是 Medium 令牌(Administrators 只用于拒绝)
   ⇒ `Get-Acl` 会抛,而脚本第 25 行是 `$ErrorActionPreference='Stop'`
   ⇒ **脚本中止 → 没有汇总行 → 门禁判「没跑起来」→ 提交被拒。**
   ⇒ **接线前应先让它在权限不足时 `Skip-It` 并写明理由**,而不是中止。
   ★ 该脚本自己就修过一次同族问题(`:129-134` 那段注释:一个「优雅降级分支」因为
   `ErrorActionPreference='Stop'` 而是**死代码**,一次都没跑过)—— 同一个坑,换个位置。
2. **fast 层现在要 189.3 秒,不是钩子注释说的「20 秒」。**
   `.githooks/pre-commit` 的注释写着「20 秒的钩子如果每次提交都跑,人会开始习惯性 `--no-verify`」——
   **那个前提已经不成立了**(实测 ≈3 分钟)。`verify-isolation.ps1` 只加 0.5 秒,不是问题;
   **问题是 189 秒本身**已经进入「人会开始绕过钩子」的区间。
   ⇒ 这一条**归 ops 车道另议**,本包只报数,不给方案。

⇒ 综合建议:**先接进 `-Full`(零风险,今天就绿)**;
把「权限不足降级为 Skip」和「fast 层 189 秒」处理掉之后,再考虑进 always-on 那一路。
