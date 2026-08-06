# =============================================================================
#  run-tests.ps1 — 一键跑全仓自检,并【如实报出没跑的部分】
#
#  ★★ 本脚本存在的理由(2026-08-04,方向 B 勘察时发现):
#     这个项目的质量模型完全建立在断言上,而在此之前**没有任何东西会自动跑它们** ——
#     `.githooks/pre-commit` 只 grep 绝对路径,`90-ops/` 下没有任何测试入口。
#     按项目自己的标准:**没人跑的断言就是假断言**。
#
#  ★★ 因此本脚本最重要的性质【不是】覆盖率,而是【诚实】:
#     一个悄悄跳过某套件却打印「全绿」的运行器,比没有运行器更危险 ——
#     那正是 2026-08-04 刚从 build-client.ps1 里修掉的假门禁形状。
#     所以:
#       · 套件清单**靠扫描得出**,不手写死;
#       · **反向全表**:扫到的每个测试文件都必须能落到某条已登记规则上,
#         落不上就【判红】—— 新加一个测试目录却没登记时,门禁必须响;
#       · 跑不了的套件**逐条列出原因**,绝不静默省略。
#
#  用法:
#    pwsh -File 90-ops\run-tests.ps1              只跑快的那层(**实测约 3 分钟**,提交前用)
#      ★ 这里原来写的是「约 20 秒」。2026-08-06 实测:22 个套件 1217 条断言,**190 秒**。
#        不是本次新增的 4 个记忆套件造成的 —— 它们四个加起来 **0.9 秒**(占 0.5%),
#        大头一直在 gateway 那层(单 test_gpu_broker 就 357 条)。
#        ⇒ 这句「20 秒」在此之前就已经过期约 9 倍。改成实测值是因为:
#          一个说自己 20 秒、实际跑 3 分钟的门禁,会被人当成"卡住了"而中途掐掉,
#          而被掐掉的门禁等于没有门禁。数字不准也是一种假绿。
#    pwsh -File 90-ops\run-tests.ps1 -Full        连 dotnet 与客户端自检一起跑(数分钟)
#    pwsh -File 90-ops\run-tests.ps1 -ListOnly    只列清单与分类,不跑
#
#  退出码:0 = 全绿且无未分类文件;1 = 有 FAIL 或有未分类文件。
# =============================================================================
[CmdletBinding()]
param(
    [switch]$Full,
    [switch]$ListOnly
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot

# --- 解释器 ---------------------------------------------------------------
$SysPy = (Get-Command python -ErrorAction SilentlyContinue).Source

# ══════════════════════════════════════════════════════════════════════════
#  ★★ 记忆套件的解释器(2026-08-06 · D91)。本文件此前写着:
#     「记忆套件已裁定不自动跑,所以【不需要】它的解释器。将来若要让它跑起来:
#       venv 路径必须先登记进 config/paths.toml 再从那儿读。」
#   ⇒ 现在真的要让它跑起来了,所以**照那句话做**:已在 `config/paths.toml`
#     的 `[memory]` 段登记 `venv`,这里只**读**它。
#
#  ★ 为什么不从 `models` 根推导出 `<AI 根>\venvs\memory`(那是另一种可行写法):
#    推导要先假定「`models` 的父目录就是 AI 根」——**那是一次猜测**,
#    而且它在 `paths.toml` 里没有任何一行写着。今天 models 与 venvs 恰好同级,
#    让这个猜测碰巧成立;哪天 models 单独挪到别的盘,推导会指向一个不存在的路径,
#    而失败信息会说「venv 没装好」。⇒ 宁可多一个键,也不要一个隐含前提。
#  ★ 顺带一提:上面这段原本抄了一行真实路径当例子,**被 pre-commit 当场拦下** ——
#    §11.1 连注释里的绝对路径也禁,理由正是它会随换盘而变成一句错话。钩子是对的。
#
#  ★★ 解析不到就让它是 $null,**绝不回退到 $SysPy**:
#    系统 python 没有 psycopg(实测:test_repo 在 import 处即 ModuleNotFoundError),
#    回退只会把「venv 没登记/没装好」伪装成「测试挂了」——
#    而这两件事的下一步完全相反。$null 会走下面「找不到解释器」那条,判红并说清楚。
# ══════════════════════════════════════════════════════════════════════════
$MemPy = $null
$MemPyWhy = ''
try {
    $__ptoml = Join-Path $repo 'config\paths.toml'
    # ★ 全文件匹配 `venv = '...'`。**必须恰好一条** —— 零条和多条都判红:
    #   零条 = 没登记;多条 = 将来别的段也加了 venv,而"取第一条"会静默取错一个,
    #   于是记忆套件会用别的环境去跑,失败信息还指不到这儿。
    $__all = @(Select-String -Path $__ptoml -Pattern "^\s*venv\s*=\s*'([^']+)'")
    if ($__all.Count -eq 0) {
        $MemPyWhy = "config\paths.toml 里没有 venv 键 —— §11.1:路径只从这里读,不在脚本里写死。请在 [memory] 段登记 venv"
    } elseif ($__all.Count -gt 1) {
        $MemPyWhy = "config\paths.toml 里有 $($__all.Count) 条 venv 键(第 $($__all.LineNumber -join '、') 行)—— 无法判断该用哪个,拒绝猜"
    } else {
        $__cand = Join-Path $__all[0].Matches[0].Groups[1].Value 'Scripts\python.exe'
        if (Test-Path -LiteralPath $__cand) { $MemPy = $__cand }
        else { $MemPyWhy = "paths.toml 登记的 venv 下找不到 Scripts\python.exe($__cand)" }
    }
} catch { $MemPyWhy = "读 paths.toml 出错:$($_.Exception.Message)" }

# --- 规则表:目录 → 怎么跑 ------------------------------------------------
#  ★ 这是【唯一】的分类来源。新增测试目录必须在这里登记,否则反向全表会判红。
#  runnable = $false 的条目不是"忽略",是**已裁定不在本机自动跑**,并写明理由。
#  ★★★ 文件级例外用 **`Files` 白名单键**,不要往 `Dir` 里塞文件名(D91 裁定①)。
#     `Dir` 是 `-like '*<Dir>*'` **子串**匹配 —— 拿它当文件名用,匹配面比看上去宽,
#     而且和目录规则一起参与「首个匹配胜出」⇒ 顺序承重 ⇒ 排错位置就**静默失效**。
#     `Files` 是**精确叶名**,且 `Get-Rule` 让它优先于目录兜底 ⇒ 顺序在结构上不再承重。
#  ★ 上面那条护栏会把整张表**倒过来**再问一次,证明这句话是真的,而不是碰巧排对了。
$RULES = @(
    @{ Dir = '10-core\gateway';    Tier = 'fast'; Interp = $SysPy; Runnable = $true
       Reason = '纯逻辑,无外部状态' }
    @{ Dir = '10-core\gpu-broker'; Tier = 'fast'; Interp = $SysPy; Runnable = $true
       Reason = '纯逻辑,无外部状态' }
    # ══════════════════════════════════════════════════════════════════════
    #  ★★★ 2026-08-06:记忆套件从「整目录不跑」拆成两条。
    #
    #  下面那条整目录规则的**机制描述是对的**(SSPI / pg_ident / dbname=memory
    #  都实地复现过),但它的**作用域错了**:规则表的粒度是目录,于是
    #  「这个目录里有 10 个套件连库」被写成了「10-core\memory 结构上跑不了」,
    #  把同目录下 4 个【根本不连库】的纯逻辑套件一起判掉了。
    #
    #  实测(2026-08-06,机主身份,零配置改动,记忆 venv):
    #    tainted 75 · route 55 · gate 46 · repo 25(+1 SKIP,活库段自跳过)
    #    = **201 条断言全绿**,占 P3a 那 459 条的 41%。
    #  ★ 这是**发现**的原始数字,原样保留。**D91 的处置比它保守**:
    #    只放行 tainted + route + repo(155 条),`test_gate` 的 46 条挂起 —— 见下方。
    #    而 repo 落地时是 **29** 条(比原测的 25 多 4):本次按协调层裁定
    #    「不记技术债,同一次提交里补上」给它的只读护栏补了 4 条钉住护栏本身的断言。
    #    ⇒ 发现照旧成立,处置更保守;两个数字都如实留着,不互相覆盖。
    #
    #  ★ 危害方向要看清:它不会让任何东西变红,只会**少跑** —— 所以躺了 8 天没人发现。
    #    「能跑却一直不跑」和「跑不了」是两回事,而这张表把它们混成了一句话。
    #    这正是本脚本开篇要防的形状(「悄悄跳过某套件却打印全绿」)的**同款**,
    #    只不过它藏在 Reason 里:那句理由读起来像覆盖账诚实,而它对 41% 的断言是错的。
    #
    #  ★ 归档依据不是「看着像纯逻辑」,是逐个静态确认过导入图与全部调用点
    #    (见 00-docs/decision-packets/memory-suite-revival-2026-08-06.md 的确认程序)。
    #    ⇒ 往这个 Files 里加文件之前,**必须先按那套程序确认它不连库** ——
    #      加错的代价是门禁真的连上生产记忆库。
    #
    #  ★★★ 这条豁免的成立【有前提】,不写清楚将来一定出事:
    #    「不连库」的准确说法有**两种强度**,必须分开:
    #      · test_tainted / test_route —— **导入闭包止于标准库**,psycopg 从未被 import
    #        ⇒ socket 在物理上不可能存在。★ 最强,结构性。
    #        而且 test_route 自己钉着自己的护栏(`:98-100` 用 inspect.getsource 扫源码,
    #        psycopg / connect( / httpx / SELECT / cursor 一个都不许出现)。
    #      · test_repo —— **不是结构性**:psycopg 已加载。§1-7 不碰库;
    #        §8 活库段 `repo.connect()` 包在 try/except 里,失败即 skip() 并打印原因。
    #        ⇒ 它安全靠的是**代码分支 + 身份被拒**,不是护栏。
    #
    #  ★★★ **`test_gate.py` 的 46 条【挂起】,不进这张白名单**(D91)。
    #    理由不是"护栏还没写好",是**根本没有护栏机制**:
    #    全仓不存在任何只读跑测试的东西(psycopg 桩 / socket monkeypatch /
    #    sys.modules 层面的 no-connect 断言,grep 全空)。
    #    而它每跑一次都真的拨号:test_gate.py:144 → gate.py:369 log_gate_rejection
    #    → repo.py:825 `psycopg.connect(_dsn(), autocommit=True)` → `INSERT INTO mem.gate_rejection`。
    #      · **autocommit=True ⇒ 一旦连得上,当场提交,没有回滚余地**;
    #      · **写进去还删不掉**:roles.sql:39 对 ai_mem_local REVOKE 了 DELETE;
    #      · 污染的是 §9.3 告警计数的来源 —— **安全信号表**。
    #    ⇒ 今天它安全的唯一理由是**身份被拒**,而那堵墙正是那 459 条里一部分测的对象。
    #      **不能为了验那堵墙而拆掉那堵墙。** 解锁条件见 DECISIONS.md D91。
    #    ★ 实测可见:每跑一次 stderr 都有 `[gate_rejection 审计写入失败] OperationalError`
    #      —— 那是它**试过**的物证。这一行**消失不等于变干净了,更可能是连上了**。
    #
    #  ⇒ **绝不要以 ai-mem 身份跑 run-tests.ps1**。要以 ai-mem 跑记忆套件,
    #    走 90-ops\spikes\run-memory-suite.ps1 -Scope Full(它只把活库套件交给 ai-mem)。
    @{ Dir = '10-core\memory'; Tier = 'fast'; Interp = $MemPy; Runnable = $true
       Files = @('test_tainted.py', 'test_route.py', 'test_repo.py')
       Reason = '不连活库(tainted/route 导入闭包止于标准库;test_repo 的活库段自己 SKIP 并说明)。' +
                '★ test_gate 不在此列 —— 它每跑一次都真的拨号且 autocommit,见上方 D91' }
    @{ Dir = '10-core\memory';     Tier = 'manual'; Interp = $null; Runnable = $false
       Reason = '★ 连的是【真实】记忆库(dbname=memory),且 pg_ident 只映射 ai-mem —— ' +
                '当前身份 SSPI 会被拒;其中 test_s9_drill.py 还是 pg_dump/pg_restore 恢复演练。' +
                '必须以 ai-mem 身份、有意识地手动跑(90-ops\spikes\run-memory-suite.cmd),不进自动门禁。' +
                '★★ test_gate.py 也落在这一档,但**理由不同**:它不是"连不上",是' +
                '【每跑一次都真的拨号且 autocommit,写进去还删不掉(roles.sql:39 REVOKE 了 DELETE)】,' +
                '而仓库里根本没有只读护栏机制 —— 今天安全的唯一理由是身份被拒,' +
                '而那堵墙正是被测对象。解锁条件见 DECISIONS.md D91。' }
)

#  ★★★ 参数名叫 `$ruleTable` 而**不是** `$rules` —— PowerShell 的变量名
#     **不区分大小写**,所以参数 `$rules` 与全局 `$RULES` 是**同一个变量**:
#     参数会把全局表遮住,`$rules = $RULES` 变成把 $null 赋给它自己,
#     于是两趟循环都在空表上跑、恒返回 $null。
#     ★ 这是写这个函数时**当场踩到的**,而它的表现是:每个测试文件都"未分类"。
#       上面那条护栏第一时间抓住了它 —— 那正是把护栏从"钉配置"改成"钉行为"的收益:
#       钉配置的老护栏(检查例外排在前面)对这个 bug **完全无感**。
function Get-Rule([string]$fullPath, $ruleTable = $null) {
    # ══════════════════════════════════════════════════════════════════════
    #  ★★★ 两趟匹配(D91 裁定②):**`Files` 精确匹配优先于 `Dir` 子串匹配**。
    #
    #  此前是「首个匹配胜出」的一趟循环 ⇒ **顺序承重**:细分规则一旦被挪到
    #  同目录兜底规则后面就永远不生效,而后果是【静默少跑】——
    #  不报错、不告警、`-ListOnly` 照显 manual。它和这张表要修的病一模一样。
    #  ⇒ 靠"记得排对顺序"来保证正确性,本身就是一条靠人记得的护栏。
    #
    #  改成两趟之后,顺序**在结构上**不再承重:第一趟只看带 `Files` 的精确条目,
    #  第二趟才轮到目录兜底。规则表怎么排都得到同一个答案 ——
    #  上面那条护栏会把表**整个倒过来**再问一次,以此证明这句话是真的。
    #
    #  ★ `$ruleTable` 参数默认取 $RULES,只为让护栏能拿一张倒序表问同一个问题;
    #    生产路径永远不传它(传了就不是在测这个脚本了)。
    # ══════════════════════════════════════════════════════════════════════
    if ($null -eq $ruleTable) { $ruleTable = $RULES }
    $leaf = Split-Path $fullPath -Leaf
    # 第一趟:带 Files 的精确条目 —— 同一目录里代价不同的套件要分开裁。
    foreach ($r in $ruleTable) {
        if (-not $r.ContainsKey('Files')) { continue }
        if ($fullPath -notlike ('*' + $r.Dir + '*')) { continue }
        if ($r.Files -contains $leaf) { return $r }
    }
    # 第二趟:目录兜底。
    foreach ($r in $ruleTable) {
        if ($r.ContainsKey('Files')) { continue }
        if ($fullPath -like ('*' + $r.Dir + '*')) { return $r }
    }
    return $null
}

# ══════════════════════════════════════════════════════════════════════════
#  ★★★ 规则表护栏 —— 测 Get-Rule 的【行为】,不测配置的排列(D91 裁定③)
#
#  两版护栏合并成这一条。它们原本各自钉的东西:
#    · main 那版钉「文件级例外排在目录规则之前」—— 钉的是一个**配置事实**;
#    · 3 号那版钉「Files 里每个文件都落到 Runnable=$true」—— 钉的是**覆盖**。
#
#  ★ 前者今天已经不该再钉了:`Get-Rule` 改成 **Files 精确匹配优先于 Dir 子串匹配**
#    之后,顺序在**结构上**不再承重 —— 继续钉顺序等于钉一句已经不成立的话,
#    而那正是本仓 ASSERTION-PITFALLS 第 6 次踩的形状(守着旧理由)。
#
#  ⇒ 现在钉三条**性质**:
#    ① 同时匹配 Files 与目录兜底的路径,必须落到**带 Files** 的那条;
#    ② ★★ **把规则表整个倒过来,归属必须一模一样** ——
#       倒过来仍然对,才证明「顺序不承重」是真的,而不是"碰巧现在顺序排对了";
#       它一旦变了,说明 Get-Rule 的两趟匹配被改坏、顺序又变成承重的了。
#    ③ Files 里每一个文件都必须真的落到 `Runnable=$true`(3 号那版的覆盖半边,保留)。
#
#  ★ 判据一律用**真实的规则表 + 真实的 Get-Rule**,不另建一份模型 ——
#    另建一份的话,它验的是那份模型,不是这个脚本。
# ══════════════════════════════════════════════════════════════════════════
$__rev = @($RULES); [array]::Reverse($__rev)
foreach ($__r in ($RULES | Where-Object { $_.ContainsKey('Files') })) {
    foreach ($__fn in $__r.Files) {
        $__p   = Join-Path (Join-Path $repo $__r.Dir) $__fn
        $__hit = Get-Rule $__p
        $__hr  = Get-Rule $__p $__rev          # ★ 同一个问题,问一张【倒过来】的表
        if (-not ($__hit -and $__hit.Runnable -and $__hit.ContainsKey('Files'))) {
            Write-Host ""
            Write-Host "X 规则表护栏:$__fn 没落到带 Files 的那条 Runnable=`$true 规则上。" -ForegroundColor Red
            Write-Host "  实得:Dir='$($__hit.Dir)' Tier='$($__hit.Tier)' Runnable=$($__hit.Runnable)" -ForegroundColor Red
            Write-Host "  ★ 这种错**不会让任何测试变红**,只会让它们悄悄不跑 —— 所以必须在这里拦。" -ForegroundColor Red
            exit 1
        }
        if (-not ($__hr -and $__hr.Tier -eq $__hit.Tier -and $__hr.Runnable -eq $__hit.Runnable)) {
            Write-Host ""
            Write-Host "X 规则表护栏:把 `$RULES **倒过来**之后,$__fn 的归属变了。" -ForegroundColor Red
            Write-Host "  正序 → Tier='$($__hit.Tier)' Runnable=$($__hit.Runnable)" -ForegroundColor Red
            Write-Host "  倒序 → Tier='$($__hr.Tier)' Runnable=$($__hr.Runnable)" -ForegroundColor Red
            Write-Host "  ★ 说明 Get-Rule 的两趟匹配(Files 优先于 Dir)被改坏了,顺序又变成承重的。" -ForegroundColor Red
            Write-Host "    顺序一旦承重,排错位置就会【静默少跑】—— 而那正是这张表要修的病本身。" -ForegroundColor Red
            exit 1
        }
    }
}

# --- 发现:扫盘,不写死清单 ------------------------------------------------
$found = Get-ChildItem -Path (Join-Path $repo '10-core') -Recurse -Filter 'test_*.py' -File -ErrorAction SilentlyContinue |
         Where-Object { $_.FullName -notlike '*__pycache__*' } | Sort-Object FullName

Write-Host ""
Write-Host "扫到 $($found.Count) 个 Python 测试文件" -ForegroundColor Cyan

# --- ★ 反向全表:每个都必须能落到某条规则上 -------------------------------
$unclassified = @()
foreach ($f in $found) { if (-not (Get-Rule $f.FullName)) { $unclassified += $f.FullName } }
if ($unclassified.Count -gt 0) {
    Write-Host ""
    Write-Host "X 有测试文件不属于任何已登记规则 —— 判红。" -ForegroundColor Red
    Write-Host "  ★ 这一条是本脚本的承重断言:新加了测试却没登记时,门禁必须响," -ForegroundColor Red
    Write-Host "    否则它会被静默漏跑,而运行器照样打印「全绿」。" -ForegroundColor Red
    $unclassified | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
    Write-Host "  修法:在 run-tests.ps1 的 `$RULES 里给它的目录登记一条(含 Runnable 与 Reason)。" -ForegroundColor Yellow
    exit 1
}

# --- 列出分类 -------------------------------------------------------------
$byTier = $found | Group-Object { (Get-Rule $_.FullName).Tier }
foreach ($g in $byTier | Sort-Object Name) {
    $r = Get-Rule $g.Group[0].FullName
    $mark = if ($r.Runnable) { '将运行' } else { '不自动跑' }
    Write-Host ("  [{0}] {1} 个文件 — {2}" -f $g.Name, $g.Count, $mark)
}

if ($ListOnly) {
    Write-Host ""
    foreach ($f in $found) {
        $r = Get-Rule $f.FullName
        Write-Host ("  {0,-8} {1}" -f $r.Tier, $f.FullName.Replace($repo + '\', ''))
    }
    exit 0
}

# --- 跑 -------------------------------------------------------------------
# ★★★ C2 护栏(2026-08-06,对抗式复核逼出来的):门禁必须在【已知环境】里跑。
#   `repo.py:87` 是 `os.environ.get("LOCALAI_PG_USER", "ai_mem_local")` ——
#   **连哪个 PG 角色由环境变量决定**,而同目录有 8 处兄弟套件会把它设成 `postgres`
#   (test_s3_repo.py:22 / test_s3_acceptance.py:42 / test_s4_acceptance.py:64 /
#    test_s5_acceptance.py:48,115 / eval_memory.py:173,253)。环境是**继承**的。
#
#   ★ 实测厘清(别把这条记成它不是的东西):它**不能**让机主连上生产库 ——
#     `LOCALAI_PG_USER=postgres` + 机主身份实测仍是 REFUSED,因为 pg_ident 校验的是
#     (系统用户, 目标角色) **这一对**,机主对任何角色都不在表里。
#   ⇒ 它不是一条独立的绕过路径,它是 **C1 的放大器**:一旦哪天以 ai-mem 身份跑,
#     默认的 `ai_mem_local` 会被悄悄换成**超级用户**,`roles.sql` 的全部 REVOKE 当场失效
#     —— 包括 `mem.gate_rejection` 那条 append-only。
#   ⇒ 所以无条件清掉并留痕。这是子进程的环境,清了不影响调用方的 shell。
if ($env:LOCALAI_PG_USER) {
    Write-Host ("! 检测到 LOCALAI_PG_USER={0} —— 已在本次门禁中清除(门禁必须在已知环境里跑)" -f $env:LOCALAI_PG_USER) -ForegroundColor Yellow
    Remove-Item Env:\LOCALAI_PG_USER
}

$totalPass = 0; $totalFail = 0; $totalSkip = 0
$ran = @(); $skipped = @(); $broken = @()

function Invoke-PySuite($file, $interp) {
    # 约定:每个测试脚本自己打印 "N PASS · M FAIL" 并以 0/1 退出。
    # ★ 判据同时看【退出码】与【汇总行】—— 只看其一都可能被"根本没跑起来"骗过
    #   (build-client.ps1 就栽过:退出码是上一条命令的残留值)。
    $out = & $interp $file.FullName 2>&1 | Out-String
    $code = $LASTEXITCODE
    $p = 0; $f = 0
    # ★★★ 2026-08-05 修:原判据是非锚定的 `(\d+)\s*FAIL`,它会匹配到**任何**
    #   「数字 + FAIL」的片段 —— 包括某条 FAIL 行的 extra 里的状态码。
    #   实测:一条断言打印 `FAIL  ... 422`(422 是 HTTP 状态码),
    #   运行器报出 **FAIL=422**,而真实失败数是 **2**。
    #   ★ 危害方向要看清:这一次是把 2 夸大成 422(吓人但不致命);
    #     反方向同样可能 —— 输出里先出现一个「0 FAIL」的片段就会把真失败**盖掉**。
    #   ⇒ 判据锚定到**汇总行本身**:必须是 "N PASS · M FAIL" 那一整行。
    $summaryLine = ($out -split "`r?`n" | Where-Object { $_ -match '^\s*===.*\d+\s*PASS.*\d+\s*FAIL' } | Select-Object -Last 1)
    $s = 0
    if ($summaryLine -and $summaryLine -match '(\d+)\s*PASS[^0-9]+(\d+)\s*FAIL') {
        $p = [int]$Matches[1]; $f = [int]$Matches[2]
        # ★★ SKIP 也要读出来(2026-08-06 补:记忆套件里的 test_repo 会报 SKIP)。
        #   在此之前本脚本**完全看不见 SKIP** —— 一个套件把半数断言跳过,
        #   汇总行照样只贡献 PASS,覆盖账读起来是全绿。那正是开篇要防的形状。
        # ★ 必须【先把 $p/$f 取走再匹配 SKIP】:$Matches 是整个换掉的,不是累加。
        #   实测踩过:拿 SKIP 那次匹配把 PASS 的捕获顶掉,25 PASS 被读成 1(那是 SKIP 数)。
        #   危害方向是往【少】的方向错 —— 比 2026-08-05 那次 FAIL=422 更难发现。
        if ($summaryLine -match '(\d+)\s*SKIP') { $s = [int]$Matches[1] }
    }
    $sawSummary = [bool]$summaryLine
    return [pscustomobject]@{ Pass = $p; Fail = $f; Skip = $s; Code = $code; Out = $out; SawSummary = $sawSummary }
}

foreach ($f in $found) {
    $r = Get-Rule $f.FullName
    $rel = $f.FullName.Replace($repo + '\', '')
    if (-not $r.Runnable) { $skipped += [pscustomobject]@{ File = $rel; Reason = $r.Reason }; continue }
    if (-not $r.Interp) {
        # ★ 记忆套件解析不到解释器时走这里(见上面 $MemPy):判红,**不回退系统 python** ——
        #   系统 python 没有 psycopg,回退只会把「venv 没装好」伪装成「测试挂了」。
        $why = if ($rel -like '*10-core\memory\*' -and $MemPyWhy) {
            "找不到记忆套件的解释器 —— $MemPyWhy。★ 不回退系统 python:它没有 psycopg,回退会把「venv 没登记/没装好」伪装成「测试挂了」"
        } else {
            '找不到解释器 —— 记忆套件读 config\paths.toml 的 [memory] venv;其余套件需 PATH 上的 python'
        }
        $broken += [pscustomobject]@{ File = $rel; Why = $why }
        Write-Host ("  X {0,-46} 找不到解释器" -f $rel) -ForegroundColor Red
        continue
    }
    $res = Invoke-PySuite $f $r.Interp
    if (-not $res.SawSummary) {
        # 跑了但没有汇总行 = 多半根本没跑起来。绝不当作通过。
        $broken += [pscustomobject]@{ File = $rel; Why = "没有汇总行(退出码 $($res.Code))—— 多半没跑起来" }
        Write-Host ("  X {0,-46} 没跑起来" -f $rel) -ForegroundColor Red
        continue
    }
    $totalPass += $res.Pass; $totalFail += $res.Fail; $totalSkip += $res.Skip
    $ran += $rel
    $color = if ($res.Fail -gt 0 -or $res.Code -ne 0) { 'Red' } else { 'DarkGray' }
    # ★ 有 SKIP 才印 SKIP —— 没有的套件保持原来的行形状,免得整屏噪音把真信号淹掉
    $skipNote = if ($res.Skip -gt 0) { "  SKIP=$($res.Skip)" } else { '' }
    Write-Host ("  {0,-46} PASS={1,-5} FAIL={2}{3}" -f $rel, $res.Pass, $res.Fail, $skipNote) -ForegroundColor $color
    if ($res.Fail -gt 0) { ($res.Out -split "`r?`n" | Where-Object { $_ -match 'FAIL' } | Select-Object -First 8) | ForEach-Object { Write-Host "        $_" -ForegroundColor Red } }
}

# --- 调试工具箱自检(★ 存在才跑,删掉了自动跳过)-----------------------------
#  ★★★ 2026-08-05 审计:「一键移除」这条承诺**只由 90-ops\debug\selfcheck.py 守着**,
#    而那个文件不叫 test_*.py,上面的扫描根本不收它 ⇒ 承诺从来没有被自动检查过。
#    (更糟的是 README 当时还写着由 `10-core\gateway\test_debug_removable.py` 检查 ——
#     那个文件**压根不存在**。写着有防护、实际没有,正是本项目的签名失败模式。)
#  ★ 为什么不在 10-core 下建那个测试:那条判据要扫"生产代码里不得出现 90-ops\debug",
#    而测试文件本身就得写出这个路径 ⇒ 它会绊倒自己。放在被检查的目录之外才成立。
#  ★ 条件执行:`git rm -r 90-ops\debug` 之后这一段自动变成空操作,门禁照常绿。
$dbgSelf = Join-Path $repo '90-ops\debug\selfcheck.py'
if (Test-Path $dbgSelf) {
    $dbgOut = & py -3 $dbgSelf 2>&1 | Out-String
    # ★★ 判据**只用 ASCII**:汇总行长这样 —— `=== 调试工具自检:40 PASS · 0 FAIL ===`。
    #   第一版拿 `·`(U+00B7)当分隔符去匹配,在我的终端里好好的,
    #   而 pre-commit 钩子是从 git bash 起的、控制台码页是 cp936 ⇒ 中文与 `·` 全成乱码,
    #   正则匹配不上 ⇒ 门禁报「没跑起来」并拒绝提交。**实测被拒了一次。**
    #   ★ 这正是 S0 那个老问题的同款(vram_gate 唯一的生产集成因 cp936 坏了 5 天):
    #     `===` / `PASS` / `FAIL` / 数字都是 ASCII,乱码之后**依然完好**,所以只认它们。
    #   ★ 并且锚定到汇总行(`^\s*===`),不在整段输出里乱找 ——
    #     run-tests 自己就栽过一次:非锚定的 `(\d+)\s*FAIL` 匹配到了某行里的 HTTP 状态码。
    $dbgLine = ($dbgOut -split "`r?`n" |
                Where-Object { $_ -match '^\s*===.*\d+\s*PASS.*\d+\s*FAIL' } |
                Select-Object -Last 1)
    if ($dbgLine -match '(\d+)\s*PASS.*?(\d+)\s*FAIL') {
        $dp = [int]$Matches[1]; $df = [int]$Matches[2]
        $totalPass += $dp; $totalFail += $df
        $ran += '90-ops\debug\selfcheck.py'
        $c = if ($df -gt 0) { 'Red' } else { 'DarkGray' }
        Write-Host ("  {0,-46} PASS={1,-5} FAIL={2}" -f '90-ops\debug\selfcheck.py', $dp, $df) -ForegroundColor $c
        # ★ 挑红行也不认非 ASCII 符号(理由同上:cp936 下 ✘ 会变成乱码)——
        #   认 selfcheck 的判据是"不是 PASS 行、且不是分隔线",宁可多打两行也别一行不打。
        if ($df -gt 0) { ($dbgOut -split "`r?`n" | Where-Object { $_.Trim() -and $_ -notmatch '^\s*[-=]+\s*$' -and $_ -notmatch '^\s*===' } | Select-Object -First 8) | ForEach-Object { Write-Host "        $_" -ForegroundColor Red } }
    } else {
        $broken += [pscustomobject]@{ File = '90-ops\debug\selfcheck.py'; Why = '没有汇总行 —— 多半没跑起来' }
        Write-Host "  X 调试工具自检没跑起来" -ForegroundColor Red
    }
}

# --- dotnet 与客户端(只在 -Full 时)---------------------------------------
$dotnetResults = @()
if ($Full) {
    Write-Host ""
    Write-Host "[dotnet 自检]" -ForegroundColor Cyan
    $dotnetSuites = @(
        @{ Name = 'identity';  Dir = '10-core\identity';        Args = @('selftest','selftest2','selftest3','selftest4','selftest5') }
        @{ Name = 'lan-edge';  Dir = '10-core\lan-edge';        Args = @('selftest') }
        @{ Name = 'transport'; Dir = '20-client-win\transport'; Args = @('selftest') }
    )
    foreach ($s in $dotnetSuites) {
        $d = Join-Path $repo $s.Dir
        if (-not (Test-Path $d)) { $broken += [pscustomobject]@{ File = $s.Dir; Why = '目录不存在' }; continue }
        Push-Location $d
        & dotnet build -c Release -v quiet --nologo *> $null
        foreach ($a in $s.Args) {
            $out = & dotnet run -c Release --no-build -- $a 2>&1 | Out-String
            $p = 0; $f = 0
            if ($out -match 'PASS=(\d+)') { $p = [int]$Matches[1] }
            if ($out -match 'FAIL=(\d+)') { $f = [int]$Matches[1] }
            if ($out -notmatch 'PASS=') {
                $broken += [pscustomobject]@{ File = "$($s.Name) $a"; Why = '没有 PASS= 汇总行' }
                Write-Host ("  X {0,-46} 没跑起来" -f "$($s.Name) $a") -ForegroundColor Red
                continue
            }
            $totalPass += $p; $totalFail += $f
            $dotnetResults += "$($s.Name) $a"
            $c = if ($f -gt 0) { 'Red' } else { 'DarkGray' }
            Write-Host ("  {0,-46} PASS={1,-5} FAIL={2}" -f "$($s.Name) $a", $p, $f) -ForegroundColor $c
        }
        Pop-Location
    }

    # ══════════════════════════════════════════════════════════════════════
    #  ★★★ A2(2026-08-06 审计)· 客户端工程要**编译得过**,而扫描根不能写死
    #
    #  两件事同一个根因:客户端工程的源码**不止** `20-client-win\app` 一处。
    #  csproj 用 `<Compile Include="..\..\10-core\identity\*.cs" />` 直接链源码
    #  (D47 理由 1:不把 mTLS/CNG 那套逻辑重写一遍)。于是:
    #    · 别的车道改了 `10-core\identity` 里一个文件 ⇒ 客户端**编译不过**,
    #      而门禁跑的是**已有产物** ⇒ 照样全绿,编译错误一路飘到出包那一刻;
    #    · 时间戳只比 `20-client-win\app` ⇒ 那些文件改了**不算"产物过期"**,
    #      于是「跑了旧产物」这条守卫**恰恰在它最该响的那种情况下不响**。
    #      ★ 这就是那条守卫自己的盲区 —— 它守住了一半,而看起来守住了全部。
    # ══════════════════════════════════════════════════════════════════════
    Write-Host ""
    Write-Host "[客户端工程编译 + 扫描根核对]" -ForegroundColor Cyan
    $clientCsproj = Join-Path $repo '20-client-win\app\localai-client.csproj'

    # ★ 扫描根【登记表】—— 与网关 ROUTE_TIERS 同款手法:表是人写的,
    #   而下面那条**反向**断言保证没有漏网的。新链一个别处的源文件却不登记
    #   ⇒ 当场判红,而不是静默少扫一个目录(少扫是看不见的,判红是看得见的)。
    $CLIENT_SCAN_ROOTS = @(
        '20-client-win\app'          # 工程自己(SDK 隐式 glob)
        '20-client-win\transport'    # ClientTransport / TlsFailure
        '10-core\identity'           # Ca / Store / Sas / Pairing / CertLifecycle …
    )

    if (-not (Test-Path $clientCsproj)) {
        $broken += [pscustomobject]@{ File = 'client csproj'; Why = "找不到 $clientCsproj" }
        Write-Host "  X 找不到客户端 csproj" -ForegroundColor Red
    } else {
        $csprojDir = Split-Path -Parent $clientCsproj
        $xml = $null
        try { $xml = [xml](Get-Content $clientCsproj -Raw -Encoding UTF8) } catch { $xml = $null }
        if ($null -eq $xml) {
            $broken += [pscustomobject]@{ File = 'client csproj'
                                          Why = 'csproj 解析不了 —— 扫描根无从核对,不能当作"没问题"' }
            Write-Host "  X 客户端 csproj 解析不了" -ForegroundColor Red
        } else {
            $includes = @($xml.SelectNodes('//*[local-name()="Compile"]/@Include') |
                          ForEach-Object { $_.Value })
            # ★★ 零命中判红。"解析出 0 条"与"确实没链任何外部源码"在输出上长得一模一样,
            #    而今天它明明链着十几个 ⇒ 0 条只可能是解析坏了。
            #    (同族教训:scan_fake.py 的盘符正则写死 D: 而项目在 E: ⇒ 零命中报"未发现问题"。)
            if ($includes.Count -eq 0) {
                $broken += [pscustomobject]@{ File = 'client 扫描根核对'
                                              Why = 'csproj 里一条 <Compile Include> 都没解析出来 —— 多半是解析坏了,不是真的没有' }
                Write-Host "  X csproj 解析出 0 条 Compile Include" -ForegroundColor Red
            }
            $rootsAbs = @($CLIENT_SCAN_ROOTS | ForEach-Object {
                [IO.Path]::GetFullPath((Join-Path $repo $_)) })
            $unregistered = @()
            foreach ($inc in $includes) {
                $abs = [IO.Path]::GetFullPath((Join-Path $csprojDir $inc))
                $covered = $false
                foreach ($rf in $rootsAbs) {
                    if ($abs.StartsWith($rf + [IO.Path]::DirectorySeparatorChar,
                                        [StringComparison]::OrdinalIgnoreCase)) { $covered = $true; break }
                }
                if (-not $covered) { $unregistered += $inc }
            }
            if ($unregistered.Count -gt 0) {
                $broken += [pscustomobject]@{ File = 'client 扫描根核对'
                                              Why = ("csproj 链了这些源文件,但它们不在任何一个登记的扫描根下:" +
                                                     ($unregistered -join '、') +
                                                     "。改它们【不会】让「产物比源码旧」那条守卫响 ⇒ 会静默跑旧产物。" +
                                                     "请在 run-tests.ps1 的 CLIENT_SCAN_ROOTS 里登记。") }
                Write-Host "  X 扫描根漏登记:$($unregistered -join '、')" -ForegroundColor Red
            } elseif ($includes.Count -gt 0) {
                Write-Host ("  √ 扫描根覆盖 csproj 的全部 {0} 条 Compile Include" -f $includes.Count) -ForegroundColor DarkGray
            }
        }

        # ── 编译一次:**只 build 不 publish**(十几秒) ──────────────────
        #  ★★★ 绝不能加 `-r win-x64`:那会把下面自检要跑的**那个**产物覆盖掉,
        #     于是「产物比源码旧」这条守卫**永远不会响** —— 门禁自己把自己的守卫拆了,
        #     而且拆得悄无声息(数字照出、颜色照绿)。
        #     默认 build 落在 `bin\Release\<tfm>\`(没有 RID 子目录),与被测产物是两条路。
        #     ⇒ 下面那条前后时间戳断言就是钉这件事的,别把它删了。
        $exeForStale = Join-Path $repo '20-client-win\app\bin\Release\net9.0-windows10.0.19041.0\win-x64\localai-client.exe'
        $exeBefore = if (Test-Path $exeForStale) { (Get-Item $exeForStale).LastWriteTimeUtc } else { $null }

        $bout = & dotnet build $clientCsproj -c Release --nologo -v quiet 2>&1 | Out-String
        $bcode = $LASTEXITCODE
        if ($bcode -ne 0) {
            $errLines = @($bout -split "`r?`n" | Where-Object { $_ -match ':\s*error\s' } | Select-Object -First 12)
            if ($errLines.Count -eq 0) { $errLines = @($bout -split "`r?`n" | Select-Object -Last 8) }
            $broken += [pscustomobject]@{ File = 'client build'
                                          Why = ("客户端工程编译不过(exit $bcode)。" +
                                                 "★ 这条此前根本不存在 —— 门禁跑的是已有产物," +
                                                 "编译错误会一路飘到出包那一刻才被发现。首条:" +
                                                 ($errLines | Select-Object -First 1)) }
            Write-Host "  X 客户端工程编译不过(exit $bcode):" -ForegroundColor Red
            foreach ($l in $errLines) { Write-Host "      $l" -ForegroundColor Red }
        } else {
            Write-Host "  √ 客户端工程编译通过(只 build,未 publish)" -ForegroundColor DarkGray
        }
        if ($null -ne $exeBefore -and (Test-Path $exeForStale) -and
            (Get-Item $exeForStale).LastWriteTimeUtc -ne $exeBefore) {
            $broken += [pscustomobject]@{ File = 'client build'
                                          Why = ('★★ 这次 build 动了自检要跑的那个产物 —— ' +
                                                 '「产物比源码旧」那条守卫会因此【永远不响】。' +
                                                 'build 必须落在与被测产物不同的输出路径(不要加 -r win-x64)。') }
            Write-Host "  X 客户端 build 覆盖了被测产物 —— 陈旧守卫已被架空" -ForegroundColor Red
        }
    }

    Write-Host ""
    Write-Host "[客户端自检]" -ForegroundColor Cyan
    $exe = Join-Path $repo '20-client-win\app\bin\Release\net9.0-windows10.0.19041.0\win-x64\localai-client.exe'
    # ══════════════════════════════════════════════════════════════════
    #  ★★★ 2026-08-05 审计:这里跑的是**已有的构建产物**,不重新编译。
    #
    #  实测抓到的形状:改了 Selftest.cs、加了 3 条断言,门禁照样报
    #  「client --selftest PASS=1852 FAIL=0」—— 与改动前一模一样。
    #  那 1852 条是**上一次出包时**的源码跑出来的,而输出读起来像是
    #  "当前源码通过了"。⇒ 改坏客户端源码但不出包,门禁会一直是绿的。
    #
    #  ★ 不在这里强制重编(单文件发布要几分钟,会把门禁拖成没人跑的东西),
    #    改成**比时间戳**:产物比源码旧就当"没跑起来"报出来。
    #    失败必须长得和成功不一样 —— 而"跑了旧产物"就是一种失败。
    # ══════════════════════════════════════════════════════════════════
    # ★★ A2:扫描根**不再写死** —— 用上面那张登记表(且已由 csproj 反向核对过没漏)。
    #   此前只扫 `20-client-win\app`:改 `10-core\identity` 或 `20-client-win\transport`
    #   里的文件**不算"产物过期"**,而那两处恰恰是别的车道会动的地方
    #   ⇒ 这条守卫在它最该响的场景里静默失灵。
    $newestSrc = @($CLIENT_SCAN_ROOTS |
                   ForEach-Object { Join-Path $repo $_ } |
                   Where-Object { Test-Path $_ } |
                   ForEach-Object { Get-ChildItem $_ -Recurse -Include *.cs, *.xaml -File -ErrorAction SilentlyContinue }) |
                 Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
                 Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    $stale = $false
    if ((Test-Path $exe) -and $newestSrc) {
        $exeTime = (Get-Item $exe).LastWriteTimeUtc
        if ($newestSrc.LastWriteTimeUtc -gt $exeTime) {
            $stale = $true
            $rel = $newestSrc.FullName.Substring($repo.Length).TrimStart('\')
            $broken += [pscustomobject]@{ File = 'client --selftest'
                                          Why  = "产物比源码旧($rel 改于 $($newestSrc.LastWriteTimeUtc.ToString('MM-dd HH:mm')),产物出于 $($exeTime.ToString('MM-dd HH:mm')))—— 跑它等于测上一版。先跑 90-ops\build-client.ps1" }
            Write-Host "  X 客户端自检:产物比源码旧,跑它等于测上一版 —— 先出包" -ForegroundColor Red
            Write-Host "     最新改动:$rel" -ForegroundColor DarkGray
        }
    }
    if (-not (Test-Path $exe)) {
        # ★★ 两种"没有产物"要分开说,它们的下一步**完全相反**:
        #   · 主树里没有 ⇒ 你还没出过包,去出一次;
        #   · git worktree 里没有 ⇒ **本来就不会有**(bin/ 不进 git),
        #     那不是缺陷,别去出包把这条红消掉 —— 照跑,把"没跑客户端"如实写进覆盖账。
        #   判据用 .git 是文件(worktree 的 .git 是一个指向主库的文件,不是目录)。
        $dotGit = Join-Path $repo '.git'
        $inWorktree = (Test-Path $dotGit -PathType Leaf)
        $why = if ($inWorktree) {
            "没有构建产物 —— 这是一个 git worktree,bin/ 不进 git,**本来就不会有**。" +
            "别为消这条红去出包;如实记成「本车道没跑客户端自检」即可"
        } else {
            "没有构建产物($exe)—— 先跑 90-ops\build-client.ps1"
        }
        $broken += [pscustomobject]@{ File = 'client --selftest'; Why = $why }
        Write-Host ("  X 客户端自检:没有构建产物" + $(if ($inWorktree) { "(worktree 本来就没有,别出包)" } else { ",先出一次包" })) -ForegroundColor Red
    } elseif ($stale) {
        # 已在上面报过;这里**不跑** —— 跑出来的绿数字比不跑更有害
    } else {
        $log = Join-Path ([IO.Path]::GetTempPath()) ("localai-ci-selftest-" + [Guid]::NewGuid().ToString('N') + ".txt")
        $proc = Start-Process -FilePath $exe -ArgumentList '--selftest' -PassThru -Wait -WindowStyle Hidden -RedirectStandardOutput $log
        $out = if (Test-Path $log) { Get-Content $log -Raw -Encoding UTF8 } else { '' }
        Remove-Item $log -Force -ErrorAction SilentlyContinue
        if ($out -match 'PASS=(\d+)\s+FAIL=(\d+)') {
            $p = [int]$Matches[1]; $f = [int]$Matches[2]
            $totalPass += $p; $totalFail += $f
            $dotnetResults += 'client --selftest'
            $c = if ($f -gt 0) { 'Red' } else { 'DarkGray' }
            Write-Host ("  {0,-46} PASS={1,-5} FAIL={2}" -f 'client --selftest', $p, $f) -ForegroundColor $c
        } else {
            $broken += [pscustomobject]@{ File = 'client --selftest'; Why = "没有汇总行(退出码 $($proc.ExitCode))" }
            Write-Host "  X 客户端自检没跑起来" -ForegroundColor Red
        }
    }
} else {
    $skipped += [pscustomobject]@{ File = 'dotnet 自检(identity / lan-edge / transport)+ 客户端 --selftest'
                                   Reason = '慢(数分钟)。加 -Full 一起跑。' }
}

# --- ★ 覆盖账:必须把没跑的也说清楚 ---------------------------------------
Write-Host ""
Write-Host "==================== 覆盖账 ====================" -ForegroundColor Cyan
$extra = if ($dotnetResults.Count) { " + $($dotnetResults.Count) 个 dotnet/客户端套件" } else { "" }
Write-Host "  跑了      : $($ran.Count) 个 Python 套件$extra"
Write-Host "  合计      : PASS=$totalPass  FAIL=$totalFail"
if ($totalSkip -gt 0) {
    # ★ SKIP 单独一行,且明写「不是 PASS」:它是套件**自己**报的「这段我没验」,
    #   混进 PASS 里就等于把没验过的东西算成验过了。
    Write-Host "  ★ SKIP    : $totalSkip 条 —— **SKIP 不是 PASS**,是套件自报「这段没验」,逐条看上面哪个套件报的" -ForegroundColor Yellow
}
if ($skipped.Count -gt 0) {
    Write-Host "  ★ 没跑的(不是忽略,是已裁定 + 写明理由):" -ForegroundColor Yellow
    # 按理由归并 —— 同一条理由重复十几遍会把覆盖账淹掉,而覆盖账正是本脚本的重点。
    foreach ($g in ($skipped | Group-Object Reason)) {
        $files = $g.Group.File
        $head = if ($files.Count -le 3) { $files -join '、' } else { ($files[0..2] -join '、') + " 等 $($files.Count) 个" }
        Write-Host ("      {0}" -f $head) -ForegroundColor Yellow
        Write-Host ("        理由:{0}" -f $g.Name) -ForegroundColor DarkYellow
    }
}
if ($broken.Count -gt 0) {
    Write-Host "  X 应该跑却没跑起来的:" -ForegroundColor Red
    foreach ($b in $broken) { Write-Host ("      {0} — {1}" -f $b.File, $b.Why) -ForegroundColor Red }
}

Write-Host ""
if ($totalFail -gt 0 -or $broken.Count -gt 0) {
    Write-Host "X 门禁未过(FAIL=$totalFail,没跑起来 $($broken.Count) 个)" -ForegroundColor Red
    exit 1
}
Write-Host "√ 门禁通过:PASS=$totalPass FAIL=0" -ForegroundColor Green
exit 0
