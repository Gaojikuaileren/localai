# 把 `test_tainted.py` 提进门禁(`$RULES` → `fast`)· **改动清单**(交拥有 `90-ops` 的车道执行)

> 日期:2026-08-06
> 性质:**执行清单,不是决议**。承本车道
> [sink-axis-change-list-2026-08-06.md](sink-axis-change-list-2026-08-06.md) §6.1 实测。
> **本车道不执行它** —— `90-ops/run-tests.ps1` 归主执行层 / ops 车道,我只出清单。
> 行号基准:`main` = `f600461`。
>
> ★ 本清单**不改变任何测试的内容**,只改变「谁会跑它」。

---

## 0. 一句话

**`10-core\memory` 整个目录被登记成 `Runnable = $false`,理由是「连真实库 + 需 ai-mem 身份 + 有恢复演练」——
这个理由对目录成立,对 `test_tainted.py` 不成立(实测:零 DB 依赖、系统 python 跑通、退出码 0)。
于是本项目类型层的全部安全断言,落在一条为恢复演练写的豁免里,从不被自动执行。
改法是加一条【文件粒度】的例外规则 —— 而它有一个静默失效点:规则表是【首个匹配胜出】,
新规则必须排在目录规则【之前】,排后面不会报错,只是永远不生效。**

---

## 1. 现状(实测,不是读注释)

### 1.1 为什么它今天不跑

`90-ops/run-tests.ps1:48-51`:

```powershell
@{ Dir = '10-core\memory';     Tier = 'manual'; Interp = $null; Runnable = $false
   Reason = '★ 连的是【真实】记忆库(dbname=memory),且 pg_ident 只映射 ai-mem —— ' +
            '当前身份 SSPI 会被拒,结构上跑不了;其中 test_s9_drill.py 还是 pg_dump/pg_restore ' +
            '恢复演练。必须以 ai-mem 身份、有意识地手动跑,不进自动门禁。' }
```

执行段(`:127`)`if (-not $r.Runnable) { $skipped += ...; continue }` ⇒ 直接跳过并列进「没跑的部分」。

★ **注意这不是那种「没登记所以扫描器收不到」的洞** —— 反向全表(`:62-79`)已经堵住了那一种。
这是**登记为「手动」之后就没人再管**的洞:账面清清楚楚,实际没有关卡在看。

### 1.2 那条 Reason 对 `test_tainted.py` 不成立(四条实测)

| 核实项 | 结果 | 怎么测的 |
|---|---|---|
| 有没有 DB 依赖 | **没有**。`import` 只有 `io / json / logging / sys` + `from tainted import ...`(:7-17) | 读 import 段 |
| 系统 python 跑不跑得起来 | **跑得起来** | `python test_tainted.py` → `=== 75 PASS · 0 FAIL ===` |
| 退出码 | **0** | `python test_tainted.py > /dev/null; echo $?` |
| 汇总行合不合运行器的契约 | **合** | 实际输出 `=== 75 PASS · 0 FAIL ===`,匹配 `Invoke-PySuite` 的锚定正则 `^\s*===.*\d+\s*PASS.*\d+\s*FAIL`(:106),且抽取正则 `(\d+)\s*PASS[^0-9]+(\d+)\s*FAIL` 能取到 `75` / `0`(中间的 `·` 落在 `[^0-9]+` 里) |

⇒ 四条都过 ⇒ 它**结构上就是一个 fast 层的纯逻辑套件**,与 `10-core\gateway` 同类。

★ **顺带澄清一条会挡路的注释**:`run-tests.ps1:36-39` 写着

> 记忆套件已裁定不自动跑…所以这里【不需要】它的解释器。
> 将来若要让它跑起来:venv 路径必须先登记进 `config/paths.toml` 再从那儿读

这条对**需要 `D:\AI\venvs\memory` 的那些套件**成立,**对 `test_tainted.py` 不成立** ——
它用系统 python 就跑通了,**不需要 venv、因而不触发 paths.toml 那条前置**。
不写清这一点,执行的人会以为提这一个文件也要先做 venv 登记那一整摊。

---

## 2. ★★ 唯一的静默失效点:规则表是「首个匹配胜出」

`Get-Rule`(`run-tests.ps1:54-60`):

```powershell
function Get-Rule([string]$fullPath) {
    foreach ($r in $RULES) {
        if ($fullPath -like ('*' + $r.Dir + '*')) { return $r }
    }
    return $null
}
```

**遍历顺序即优先级,第一个匹配上的直接 return。**

⇒ 新增的文件级例外**必须排在 `10-core\memory` 那条之前**。
排在后面会发生什么:目录规则先匹配上并 return ⇒ 例外规则**永远不被求值** ⇒
`test_tainted.py` 照旧被跳过,**而且没有任何报错、没有任何告警**,
`-ListOnly` 里它还是显示 `manual`。**这正是本项目最恨的那种失败:改了、看起来改了、其实没生效。**

★ 好消息:`-like ('*' + $r.Dir + '*')` 是**子串匹配**,所以 `Dir` 里放一个**文件路径片段**
就能工作,**不需要改匹配逻辑**。代价是字段名 `Dir` 从此名不副实(见 §3 第 3 步)。

---

## 3. 改动清单(`90-ops/run-tests.ps1`,四步)

### 第 1 步 · 在 `$RULES` 数组里,**`10-core\memory` 那条之前**插入文件级例外

位置:`:43` `$RULES = @(` 之后、`:48` 的 `10-core\memory` 那条**之前**。

```powershell
    # ★ 文件级例外,必须排在 '10-core\memory' 那条【之前】—— Get-Rule 是首个匹配胜出,
    #   排后面不会报错,只会永远不生效(静默)。
    @{ Dir = '10-core\memory\test_tainted.py'; Tier = 'fast'; Interp = $SysPy; Runnable = $true
       Reason = '★ 目录级豁免(连真实库/需 ai-mem/恢复演练)对本文件不成立:它只 import ' +
                'stdlib 与 tainted,零 DB 依赖,系统 python 直接跑通(实测 75 PASS / 0 FAIL,退出码 0)。' +
                '它是类型层全部安全断言的所在地 —— 让它跟着目录一起被跳过,等于这组断言没人跑。' }
```

### 第 2 步 · 加一条**防止顺序被写反**的元断言

只加规则不加护栏,下一个人重排数组时会把它静默弄坏。插在 `$RULES` 定义之后、`Get-Rule` 之前:

```powershell
# ★★ 护栏:文件级例外必须排在会把它盖住的目录规则之前。
#    Get-Rule 首个匹配胜出 ⇒ 顺序写反不会报错,只会让例外静默失效。
#    这条把「静默失效」变成「当场判红」。
for ($i = 0; $i -lt $RULES.Count; $i++) {
    for ($j = 0; $j -lt $i; $j++) {
        if ($RULES[$i].Dir -like ('*' + $RULES[$j].Dir + '*')) {
            Write-Host "X 规则表顺序错了:" -ForegroundColor Red
            Write-Host ("    '{0}' 会被排在它前面的 '{1}' 抢先匹配,永远不生效。" -f $RULES[$i].Dir, $RULES[$j].Dir) -ForegroundColor Red
            Write-Host "  修法:把更具体的那条挪到更宽泛的那条【之前】。" -ForegroundColor Yellow
            exit 1
        }
    }
}
```

★ **这段代码本车道已实跑验证过**(2026-08-06,PowerShell 5.1),两个方向都对:

```
正确顺序(例外在目录之前) → OK 顺序正确
写反的顺序(目录在例外之前) → X '10-core\memory\test_tainted.py' 会被 '10-core\memory' 抢先匹配
```

同时验证了它赖以成立的那个前提 —— 同一个文件路径**同时**匹配得上文件级与目录级两条规则:

```
'…\10-core\memory\test_tainted.py' -like '*10-core\memory\test_tainted.py*'  → True
'…\10-core\memory\test_tainted.py' -like '*10-core\memory*'                  → True   ← 所以顺序是承重的
```

★ 即便如此,验收时仍要**亲手把它挪到后面看一次红**(§5 第 3 条)——
我验的是这段逻辑,不是它在 `run-tests.ps1` 真实上下文里的接线。

### 第 3 步 · 把字段名的谎言补一句(可选但建议)

`Dir` 现在既可能是目录也可能是文件。**要么**改名为 `Match`(需同步改 `Get-Rule` 与三处现有条目),
**要么**在 `$RULES` 头部注释里加一行:

```powershell
#  ★ 字段名叫 Dir,但 Get-Rule 用的是**子串匹配**,所以它也可以是一个文件路径片段。
#    需要给单个文件开例外时就这么用 —— 但务必排在会盖住它的目录规则之前。
```

⇒ 本车道建议**先加注释**(零风险),改名单独一条提交。理由:改名要动 `Get-Rule` 与三条现有规则,
和「让断言跑起来」这件事混在一起提交,出问题时不好二分。

### 第 4 步 · pre-commit 钩子的触发条件(**独立决定,不阻塞前三步**)

`.githooks/pre-commit`:

```sh
changed_core=$(git diff --cached --name-only --diff-filter=ACM | grep -E '^10-core/(gateway|gpu-broker)/' || true)
```

现状:**动 `10-core/memory/` 不触发自检**。所以第 1 步做完之后:

- 动 `gateway` / `gpu-broker` ⇒ 跑 fast 层 ⇒ **会顺带跑到 `test_tainted.py`** ✔
- 只动 `10-core/memory/tainted.py` 本身 ⇒ **仍然不跑** ✘ —— 而那恰恰是最该跑的时候。

⇒ 建议把 grep 扩成:

```sh
grep -E '^10-core/(gateway|gpu-broker|memory)/'
```

**成本评估**:钩子跑的是 `run-tests.ps1`(fast 层,不带 `-Full`)。
新增的只有 `test_tainted.py` 一个纯逻辑文件,增量耗时可忽略;
总时长仍是钩子注释里说的那 ~20 秒量级。
★ 钩子自己的注释写着「20 秒的钩子如果每次提交都跑,人会开始习惯性 `--no-verify`」——
这条顾虑成立,所以**只加 `memory`,不要顺手把 `10-core/**` 全加进去**。

---

## 4. 这次改动**不**做什么(边界写清,免得顺手扩大)

- **不动 `test_tainted.py` 的任何内容** —— 本清单只改「谁跑它」;
- **不动 `10-core\memory` 那条目录规则** —— 其余 13 个 memory 测试维持 `manual`,理由照旧成立;
- **不给其它 memory 测试提级**。本车道对 `test_gate.py` / `test_s1..s8_acceptance.py` 只做过
  「有没有出现 `psycopg` / `dbname=` 字样」的粗粒度静态查(多数为 0 处),
  **但没有实跑过,因此不下断言** ——
  ★ **不实跑是有意的**:这些脚本是「import 即执行」的形态,真跑起来若连上了库,
  可能对**真实记忆库**产生写入。为了给一份清单去冒动生产数据的风险,不划算。
  ⇒ 要提级它们,应由拥有该目录的车道在 `ai-mem` 身份下逐个实跑后再定。

---

## 5. 验收口径(四条,每条都要真看到)

1. `pwsh -File 90-ops\run-tests.ps1 -ListOnly` 的输出里,
   `10-core\memory\test_tainted.py` 那一行的 Tier 从 **`manual` 变成 `fast`**;
   其余 13 个 memory 文件**仍然是 `manual`**;
2. `pwsh -File 90-ops\run-tests.ps1`(不带 `-Full`)的运行清单里
   **出现 `10-core\memory\test_tainted.py  PASS=75  FAIL=0`**,
   并且它**不再**出现在「没跑的部分」列表里;
3. ★ **护栏能真的失败**:把新规则临时挪到 `10-core\memory` 那条**之后**,
   重跑 ⇒ 必须**判红并 exit 1**,报出「会被…抢先匹配,永远不生效」。
   看到红之后再挪回来。**没验过这一步,等于第 2 步白加**;
4. (若采纳第 4 步)只 stage 一个 `10-core/memory/` 下的改动做一次 `git commit`,
   确认钩子**真的触发了**自检(输出里有「→ 动了 …,跑一遍自检…」)。

★ 第 3 条是这份清单里唯一一条**要求看到红色**的验收 ——
本项目的纪律是「断言要能真的失败」,而一条从没红过的护栏和没有护栏是一回事。

---

## 6. 提交切分

| 提交 | 内容 | 验收 |
|---|---|---|
| C1 | 第 1 步(例外规则)+ 第 2 步(顺序护栏)+ 第 3 步的注释版 | §5 的 1 / 2 / 3 |
| C2 | 第 4 步(钩子 grep 扩到 memory) | §5 的 4 |
| C3(可选) | `Dir` → `Match` 改名 | `-ListOnly` 输出不变 |

**C1 三步建议一次提交** —— 不要留「例外加了但护栏没加」的中间态,那正是它最容易被写反的窗口。

---

## 7. 影响面(改完之后有什么变化)

| | 变化 |
|---|---|
| 自动跑的断言数 | **+75**(`test_tainted.py` 当前 75 PASS) |
| fast 层耗时 | 增加一个纯逻辑套件,量级可忽略 |
| 其它 memory 测试 | **无变化**,仍 `manual` |
| 反向全表断言 | **无影响** —— `test_tainted.py` 本来就匹配得到规则,不曾判红 |
| 生产代码 | **零改动** |
| ★ 真正的收益 | 类型层安全断言(含 sink 轴将要新增的七条)从「没人跑」变成「每次 fast 层都跑」 |

★ 与 sink 清单的关系:[sink-axis-change-list](sink-axis-change-list-2026-08-06.md) §M1-4 要加七条断言到
`test_tainted.py`。**本清单不落地,那七条就是写给没人看的**。
两者**没有先后依赖**,但**本清单先落地更划算** —— 否则 sink 那边的验收只能靠人手跑一次贴输出。

---

## 8. 一手来源

- 实测(2026-08-06,本车道):
  `python test_tainted.py` → `=== 75 PASS · 0 FAIL ===`,退出码 `0`;
  import 段 `test_tainted.py:7-17` 无 DB 依赖
- 只读引用:`90-ops/run-tests.ps1`
  (:34 `$SysPy` · :36-39 venv/paths.toml 那条注释 · :43-51 `$RULES` · :54-60 `Get-Rule` 首个匹配胜出 ·
  :62 扫描根 · :68-79 反向全表 · :99-115 `Invoke-PySuite` 的汇总行契约 · :127 `Runnable` 跳过)·
  `.githooks/pre-commit`(`changed_core` 那一段)
- 同车道:[sink-axis-change-list-2026-08-06.md](sink-axis-change-list-2026-08-06.md) §6.1(本清单的起因)·
  [egress-b-gates-impact-2026-08-06.md](egress-b-gates-impact-2026-08-06.md)
