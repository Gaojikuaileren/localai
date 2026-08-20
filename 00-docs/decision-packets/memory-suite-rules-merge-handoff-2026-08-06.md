# 交接:`$RULES` 冲突 + 201 条的只读护栏盘点(2026-08-06)(材料 → **D91**)

> ★ **2026-08-15 第 0 条车道补**:同上 —— 本包是 **D91**「材料:」栏点名的三份之一(交接)。


**状态**:三号执行层**已停手**。不 rebase、不合并、不再往前推。
**分支**:`claude/memory-suite-revival-survey-94f0d0`(main 之上 3 提交,未合并)
**冲突**:`90-ops/run-tests.ps1` —— 我独立复现了协调层的 `git merge-tree` 结果:

```
git merge-tree --write-tree --name-only main HEAD
→ be3f052b5885be4b07d9bfb39b8a129bbdfa2c25
  90-ops/run-tests.ps1
  CONFLICT (content): Merge conflict in 90-ops/run-tests.ps1
```

**本文件是给解冲突的人的。** 我不在其中做设计取舍 ——
下面把两版的差异、以及每一条差异**是实测约束还是设计偏好**分开标注,
取舍留给解冲突的人。相关背景包:[`memory-suite-revival-2026-08-06.md`](memory-suite-revival-2026-08-06.md)。

---

## ① 两版 `$RULES` 与顺序护栏的设计意图 + 逐点差异

### 1.1 共同的出发点(两版一致,不冲突)

「`10-core\memory` 结构上跑不了」这句 Reason 的**机制**是对的
(SSPI / pg_ident / dbname=memory,两边都实地复现过),但**作用域**错了:
规则表粒度是目录,把同目录里不连库的套件一起判掉了。两版都在修同一个病。

### 1.2 逐点差异

| # | 维度 | **main**(`ec95a47`) | **我这版**(`f9259cc`) | 性质 |
|---|---|---|---|---|
| 1 | 例外的表达方式 | 复用 `Dir` 字段塞进文件名:`Dir = 'memory\test_tainted.py'`,靠原有 `-like '*<Dir>*'` 命中 | 新增 `Files = @(...)` 白名单键,`Get-Rule` 按**叶名**过滤 | **设计偏好** |
| 2 | `Get-Rule` | **未改动** | 改了:先匹目录,再按 `Files` 过滤叶名 | **设计偏好**(跟随 #1) |
| 3 | 覆盖范围 | 只提 `test_tainted.py`(75) | 提 4 个(201) | ★ **协调层已裁定**,见 ③ |
| 4 | 顺序保护 | **注释**:「必须在 `'10-core\memory'` 之前(见上方顺序说明)」 | **可执行自检**:逐个解析 `Files` 里的文件,没落到 `Runnable=$true` 就判红 exit 1 | **设计偏好**,但见下方注 |
| 5 | 解释器 | `Interp = $SysPy` | `Interp = $MemPy`(从 `models` 根导出 venv,§11.1 无绝对路径) | ★ **实测约束**,见 1.3 |
| 6 | SKIP 可见性 | 无(运行器看不见 SKIP) | 有:解析 SKIP + 覆盖账单列 +「SKIP 不是 PASS」 | ★ **若提 `test_repo` 则为必需**,见 1.4 |
| 7 | `LOCALAI_PG_USER` 护栏 | 无 | 有:检测到就留痕并清除 | **实测约束**(见背景包 C2) |
| 8 | 表头耗时 | 「约 20 秒」 | 实测 190 秒 | 无冲突的事实更正(与本次新增无关:4 个套件共 0.9 秒) |

**关于 #4 的注(供参考,不是主张)**:两版都知道顺序要紧。差别只在「注释 vs 会执行的检查」。
我做成检查的理由是 —— 顺序排错的后果是**静默少跑**(不会红),
而它正是这张表要修的病本身。我把两条规则对调后实测:脚本解析正常、当场 exit 1 并点名文件。
★ 但这属于设计取舍,由解冲突的人定;若采用 main 的注释方案,建议至少保留这条实测事实。

### 1.3 ★ #5 是实测约束,不是偏好 —— 解冲突时不能只挑一版

用**系统 python**(`$SysPy`,即 main 用的那个)逐个实跑,2026-08-06:

| 套件 | `$SysPy` 结果 |
|---|---|
| `test_tainted.py` | ✅ `75 PASS · 0 FAIL`,exit 0 |
| `test_route.py` | ✅ `55 PASS · 0 FAIL`,exit 0 |
| `test_repo.py` | ❌ exit 1,**`ModuleNotFoundError: No module named 'psycopg'`**,无汇总行 |
| `test_gate.py` | ❌ exit 1,同上 |

⇒ **`$SysPy` 够 tainted 和 route,不够 repo 和 gate。**
⇒ 协调层裁定要提的 `test_route 55 + test_repo 25`,其中 **`test_repo` 必须配 `$MemPy`**。
   只取 main 那版而不带 `$MemPy`,`test_repo` 会以 `Runnable=$true` + `$SysPy` 落地 ⇒
   跑出来没有汇总行 ⇒ 运行器判「没跑起来」⇒ **门禁红**。
   ★ 是**失败得很响**,不是假绿 —— 但仍然是要修的东西,别在合并当天才发现。

`$MemPy` 的取法(我这版,可整段搬):从 `paths.toml` 的 `models` 根导出 AI 根再拼 `venvs\memory`
—— 沿用 `install-embedding.ps1:28` 的既有惯例,**不新增 `paths.toml` 键、无绝对路径**
(pre-commit 会当场抓绝对路径,本脚本被抓过一次)。
★ 并且**解析不到时判红,绝不回退 `$SysPy`** —— 回退会把「venv 没装好」伪装成「测试挂了」。

### 1.4 ★ #6:提 `test_repo` 就必须一起提 SKIP 可见性

`test_repo.py` 的汇总行是 `=== 25 PASS · 0 FAIL · 1 SKIP ===`(那 1 条是 §8 活库段自跳过)。
main 版运行器**完全看不见 SKIP**,只会把 25 计进 PASS。
⇒ 一个套件把断言跳过、覆盖账照样读起来全绿 —— 正是本脚本开篇要防的形状。
⇒ 若采纳「提 `test_repo`」,SKIP 解析建议一并带过去。

★ 带的时候注意取值顺序(我首跑栽过):`$Matches` 是**整个换掉**的,不是累加。
先取 PASS/FAIL 再匹配 SKIP,否则 `25 PASS` 会被读成 `1`(那是 SKIP 数),
而这是往**少**的方向错,比 2026-08-05 那次 `FAIL=422` 更难发现。

---

## ② 那 201 条:只读护栏是怎么切断的,以及**护栏本身有没有断言钉着**

判据分辨(整节的地基):**`import psycopg` 不是连接。** 模块级 `import psycopg`(`repo.py:32`)
只加载驱动;真正建连的只有 `psycopg.connect(...)` → libpq → 到 `127.0.0.1:5432` 的 socket。
⇒ 「切断」有两种强度完全不同的形态,必须分开说。

### 2.1 总表

| 套件 | 条数 | 切断形态 | 强度 | **护栏本身有断言钉着吗** |
|---|---|---|---|---|
| `test_tainted.py` | 75 | **导入闭包止于标准库** —— `tainted.py:32-39` 只 import `secrets/weakref/collections/dataclasses/enum/typing`。psycopg **从未被 import** ⇒ libpq 从未加载 ⇒ **socket 在物理上不可能存在** | ★ 最强(结构性) | ⚠️ **只有间接的,且不在本套件里**:`test_repo.py:87` 断言 `tainted.py` 不 import repo。它①住在 test_repo 里(若 repo 不提级,快层就没人钉它)②只查 `import repo`,不查 psycopg/httpx/socket |
| `test_route.py` | 55 | 同上,闭包止于标准库(`route.py:20-27` 全标准库) | ★ 最强(结构性) | ✅ **有,而且是本套件自己钉的**:`test_route.py:98-100` 用 `inspect.getsource(route)` 扫源码,断言 `psycopg` / `connect(` / `requests.` / `httpx` / `SELECT ` / `cursor` **一个都不许出现**。route.py 哪天长出连库能力,**这个套件当场红** |
| `test_repo.py` | 25 | **不是结构性** —— psycopg 已加载。§1-7 不碰库;§8 `repo.connect()`(`:165`)包在 `try/except` 里,失败即 `skip()` 并打印原因(`:166-168`) | 中(靠代码分支 + 身份被拒) | ❌ **没有**。那个 `try/except` 是代码,不是被断言的性质 —— 有人把 try 去掉,**没有任何断言会红**。(`:80-82`「只有 repo.py 直接 import psycopg」是**架构**断言,不是只读护栏) |
| `test_gate.py` | 46 | ❌ **代码层面没有任何切断**。`test_gate.py:144` → `gate.py:369 log_gate_rejection` → `repo.py:825` `psycopg.connect(_dsn(), autocommit=True)` → `:828-830 INSERT INTO mem.gate_rejection`。**每跑一次都真的拨号** | ★ 最弱 —— **唯一护栏是 SSPI 身份被拒** | ❌ **没有**。没有断言钉住「不得拨号」,`repo.py:832-834` 反而把异常整个吞掉、只往 stderr 打一行、**永不抛** ⇒ 连不上和连上了,**断言计数与退出码完全一样** |

### 2.2 两条全局事实(解冲突/提级前要知道)

1. **仓库里不存在任何「只读跑测试」的机制** —— 全仓没有 psycopg 桩、没有 socket monkeypatch、
   没有 `sys.modules` 层面的 no-connect 断言。查过:这 4 个文件里 `sys.modules` 只出现一次
   (`test_tainted.py:118`,与连接无关)。
   ⇒ 目前的「只读」全部来自**导入闭包**(tainted/route)或**身份被拒**(gate/repo),
     **没有一条来自可执行的护栏**。
2. **`test_gate` 拨号是实测可见的**:每跑一次 stderr 都有
   `[gate_rejection 审计写入失败] OperationalError` —— 那就是它**试过**的直接物证。
   ★ 这一行**消失**不等于变干净了,更可能是**连上了** —— 该去查 pg_ident。

### 2.3 `test_gate` 一旦连上会落什么(为什么它和 repo 不同一档)

- 语句是 `INSERT INTO mem.gate_rejection (...) VALUES (...)`,**`autocommit=True` ⇒ 当场提交,无回滚余地**。
- **应用角色删不掉**:`roles.sql:39` 对 `ai_mem_local` `REVOKE UPDATE, DELETE ON ... mem.gate_rejection`。
- **没有清理路径覆盖它**:全仓对该表的 DELETE 只有 `test_s3_acceptance.py` / `test_s4_acceptance.py`
  两处,条件都是 `session_id LIKE '<TAG>%'`,而 `test_gate.py:145` 写死的是 `"s9"`,两个 TAG 都不匹配。
- **污染的是安全信号**:`repo.py:811` 说明该表是 §9.3 告警计数的来源。
- 对照 `test_repo` §8:写的是自己的测试行,`try/finally` 里紧跟 `DELETE` 清理
  (中途崩则残留)—— **可自清,且不落在安全信号表上**。

⇒ 这就是「gate 和 repo 不同一档」的实测依据,与 ③ 的裁定一致。

---

## ③ ★★ 协调层裁定(原样写入,不得在合并时被稀释)

> **`test_gate.py` 的提级与「去掉身份被拒」这两件事不同时做,而且第二件永不做。**
>
> 它每跑一次会向生产记忆库发起 `autocommit=True` 的 INSERT 拨号,
> 今天安全的唯一理由就是身份被拒 —— 而那 459 条里有一部分测的就是这堵墙。
>
> ⇒ `test_gate` 的提级**只在只读护栏成立且有断言钉着护栏本身时**才允许;
>   `test_route` 55 + `test_repo` 25 那 80 条不受此约束,**该提**。

### 3.1 按 ② 的盘点,这条裁定今天的落点

| 套件 | 条数 | 裁定下的处置 | 依据(来自 ②) |
|---|---|---|---|
| `test_tainted.py` | 75 | **main 已提**(`ec95a47`) | 结构性切断,最强 |
| `test_route.py` | 55 | ✅ **该提** | 结构性切断 + **自己钉着自己的护栏**(`:98-100`) |
| `test_repo.py` | 25 | ✅ **该提**(裁定明示不受约束) | 可自清、不落安全信号表。★ 但护栏**没有断言钉着**,见 3.2 |
| `test_gate.py` | 46 | ⛔ **held —— 今天两个条件一个都不满足** | 无只读护栏(唯一护栏是身份被拒)+ 无断言钉护栏 |

**合计:75(已提)+ 80(该提)= 155;`test_gate` 的 46 条继续挂着。**
★ 这不是把那条发现打了折 —— 201 条**确实**今天就能跑、且实测全绿(见背景包 §1.4);
   只是其中 46 条的「能跑」目前建立在**身份被拒**而不是护栏上,所以不进自动门禁。
   **发现照旧成立,处置更保守。**

### 3.2 残留风险,如实写出(不推翻裁定,只是别让它被遗忘)

`test_repo` 被裁定该提,而它的护栏(`:165` 的 `try/except`)**同样没有断言钉着** ——
有人把 try 去掉,不会有任何断言变红。它与 `test_gate` 的差别是**后果**不同
(可自清 vs 提交到删不掉的安全信号表),不是**护栏强度**不同。
⇒ 建议(不是主张):把 `test_repo` 提级时,顺带记一笔技术债 ——
   §8 那段的 `try/except` 应当被一条断言钉住。

### 3.3 `test_gate` 将来要解锁,需要满足什么(给后续车道的接口,我未实现)

裁定给了条件但没给形态。按 ② 的盘点,至少要同时有:

1. **一条真的只读护栏**。可能形态(供选,未验证):
   a. 跑 `test_gate` 时注入一个 psycopg 桩,`connect()` 一律抛 —— 断言 46 条仍全过;
   b. 或让 `log_gate_rejection` 支持一个显式的「审计禁写」开关,测试态下打开。
   ★ 注意 (a) 改的是跑法(可落在 90-ops),(b) 改的是 `10-core/memory`(**P3d 车道**,不是谁都能动)。
2. **一条钉住护栏本身的断言** —— 即「护栏被拿掉时会红」。没有这条,护栏和注释等价。
3. ★ 并且**不得**为此放宽 pg_ident / pg_hba —— 那正是被测对象(见背景包 §2.2/§2.3.1)。

---

## ④ 我没做、且不该由我做的

- **不 rebase、不合并、不解冲突** —— 两版顺序护栏实现不同,取舍不该由执行层单方面定。
- **未实现 3.3 的只读护栏** —— 它可能要动 `10-core/memory`(P3d 车道),且形态未定。
- **未改 main 的任何东西**;我这版的 `$RULES` 仍在我分支上原样保留,供比对。
- 本包 D 号仍留 `D?`。

## ⑤ 机器状态

生产侧零改动:`pg_ident.conf` SHA256 全程 `C2DDDF2F0D77B0F27728CCC981E8854317E7BF6B8B6224F23D2A77DD37ACB1BC` 未变;
未动 pg_hba / 口令 / 任何服务(`pg-mem` · `Qdrant` · `Qdrant-s2` · `Embedding` 均 Running);
`10-core/memory/` 一个字未改。本次为核实 ①1.3 用系统 python 跑过 4 个套件,
其中 `test_repo`/`test_gate` 在 `import psycopg` 处即失败,**未产生任何连接**。
