<#
.SYNOPSIS
    run-memory-suite.ps1 — 让 P3a 记忆库那 14 个套件重新跑起来(2026-08-06)

.DESCRIPTION
    背景:P3a 2026-07-28 验收,459 条断言全绿,**此后一次没跑过**。
    run-tests.ps1 把整个 10-core\memory 判成 Runnable=$false,理由是
    「pg_ident 只映射 ai-mem,当前身份 SSPI 被拒,结构上跑不了」。

    ★ 那条理由对 14 个套件里的 10 个成立,对另外 4 个【不成立】。
      实测(2026-08-06):tainted / route / gate / repo 四个套件根本不需要活库,
      用记忆 venv 的解释器当场就能跑,共 201 条断言。
      它们被一句「结构上跑不了」连坐了 8 天。

    ⇒ 本脚本把这 14 个套件拆成两档,分别用【代价完全不同】的方式跑:

      -Scope NoDb (默认)
          4 个纯逻辑套件。当前身份即可,**不需要管理员、不碰任何凭据、
          不改任何机器状态**。这一档应当进日常门禁。

      -Scope Full
          14 个全跑。需要管理员。以 **ai-mem 身份**跑 —— 那是生产身份本身,
          不是绕过隔离。用的是 apply-schema.ps1 已有的同一套机制:
          随机重置 ai-mem 口令 → 同步所有以 ai-mem 运行的服务与计划任务 →
          起一次性计划任务 → 用完擦除。口令只活在这一次运行里,无人持有。

    ★★ 本脚本【不做】什么(这几条比它做什么更重要):
      · 不改 pg_ident / pg_hba —— 往 SYSTEM-USERNAME 列加任何账户都会让
        verify-isolation.ps1 ⑤ 的反向全表断言判红,而那条断言正是为了抓这个动作写的;
      · 不改 PG / Qdrant 的任何配置,不停任何服务;
      · 不给机主账户开任何映射。
      跑完之后隔离形状与跑之前【逐字节相同】—— 这一点由 -Scope Full 的
      前置/后置 verify-isolation.ps1 各跑一次来证明,不靠承诺。

.NOTES
    §11.1 路径契约:本文件内无绝对路径,一律从 config\paths.toml 导出。
    venv 路径按 install-embedding.ps1 的既有惯例从 models 根导出,不新增配置键。

.EXAMPLE
    pwsh -File 90-ops\spikes\run-memory-suite.ps1
    pwsh -File 90-ops\spikes\run-memory-suite.ps1 -Scope Full     # 需管理员
    pwsh -File 90-ops\spikes\run-memory-suite.ps1 -RepairCredentials
#>
[CmdletBinding()]
param(
    [ValidateSet('NoDb', 'Full')]
    [string]$Scope = 'NoDb',
    [switch]$ListOnly,
    [switch]$RepairCredentials
)

# ★ 'Continue' 而不是 'Stop':verify-isolation.ps1 2026-08-03 实测过这个坑 ——
#   'Stop' 会把 native 命令(python.exe / psql.exe)写到 stderr 的每一行包成
#   NativeCommandError ⇒ 终止错误 ⇒ 优雅降级分支变成死代码,一次都不会跑。
#   真失败一律靠显式退出码 / Test-Path 判。
$ErrorActionPreference = 'Continue'
try { Set-PSReadLineOption -HistorySaveStyle SaveNothing -ErrorAction SilentlyContinue } catch {}

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# --- paths.toml:唯一路径源(§11.1)---------------------------------------
$PathsToml = Join-Path $repo 'config\paths.toml'
function Get-Path([string]$Key) {
    $m = Select-String -Path $PathsToml -Pattern ("^\s*{0}\s*=\s*'([^']+)'" -f [regex]::Escape($Key)) |
         Select-Object -First 1
    if (-not $m) { throw "paths.toml 缺键: $Key" }
    return $m.Matches[0].Groups[1].Value
}
function Write-NoBom([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

$LogDir = Get-Path 'logs'
# ★ venv 路径:按 install-embedding.ps1:28 的既有惯例从 models 根导出 AI 根,
#   不硬编码、也不新增 paths.toml 配置键(那要动 config/,不在本车道边界内)。
$AiRoot = Split-Path (Get-Path 'models') -Parent
$Vpy    = Join-Path $AiRoot 'venvs\memory\Scripts\python.exe'
$MemDir = Join-Path $repo '10-core\memory'

# --- 套件分档:扫描 + 反向全表(抄 run-tests.ps1 的承重形状)-----------------
#  ★ 不写死清单就完事 —— 扫到的每个 test_*.py 都必须落到某一档上,
#    落不上就判红。新加一个套件却没归档时,门禁必须响。
$NODB = @('test_tainted.py', 'test_route.py', 'test_gate.py', 'test_repo.py')
$DB   = @('test_s1_acceptance.py', 'test_s2_acceptance.py', 'test_s3_acceptance.py',
          'test_s3_repo.py', 'test_s4_acceptance.py', 'test_s5_acceptance.py',
          'test_s6_acceptance.py', 'test_s7_acceptance.py', 'test_s8_acceptance.py',
          'test_s9_drill.py')

$found = @(Get-ChildItem -LiteralPath $MemDir -Filter 'test_*.py' -File -ErrorAction SilentlyContinue |
           Where-Object { $_.FullName -notlike '*__pycache__*' } | Sort-Object Name)

Write-Host ''
Write-Host "扫到 $($found.Count) 个记忆库测试文件" -ForegroundColor Cyan

$unclassified = @($found.Name | Where-Object { $NODB -notcontains $_ -and $DB -notcontains $_ })
if ($unclassified.Count -gt 0) {
    Write-Host ''
    Write-Host 'X 有测试文件不属于任何一档 —— 判红。' -ForegroundColor Red
    Write-Host '  ★ 新加了套件却没归档时门禁必须响,否则它会被静默漏跑而本脚本照样报绿。' -ForegroundColor Red
    $unclassified | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
    Write-Host '  修法:把它加进本脚本的 $NODB 或 $DB —— 先实跑一次确认它到底需不需要活库。' -ForegroundColor Yellow
    exit 1
}
# 反向:登记了却不存在的(套件被删/改名时同样必须响)
$missing = @(($NODB + $DB) | Where-Object { $found.Name -notcontains $_ })
if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host 'X 登记了但磁盘上不存在的套件 —— 判红(被删或改名了?)' -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
    exit 1
}

Write-Host ("  [纯逻辑] {0} 个 — 当前身份即可跑" -f $NODB.Count)
Write-Host ("  [需活库] {0} 个 — 需 ai-mem 身份(-Scope Full)" -f $DB.Count)

if ($ListOnly) {
    Write-Host ''
    foreach ($n in $NODB) { Write-Host ("  纯逻辑  {0}" -f $n) }
    foreach ($n in $DB)   { Write-Host ("  需活库  {0}" -f $n) }
    exit 0
}

# --- 判据:只认 ASCII 的汇总行 ---------------------------------------------
#  ★★ 两条教训都写进这一个函数里:
#   ① 只认 ASCII:汇总行是 `=== 25 PASS · 0 FAIL · 1 SKIP ===`。控制台/日志走 cp936 时
#     中文与 `·` 全成乱码,而 `===` / PASS / FAIL / 数字**依然完好**。
#     run-tests.ps1 的调试自检段就因为拿 `·` 当分隔符匹配,被 pre-commit 钩子拒过一次。
#   ② 锚定到汇总行本身:非锚定的 `(\d+)\s*FAIL` 会匹配到正文里任意「数字+FAIL」——
#     run-tests.ps1 实测被一个 HTTP 状态码骗出 FAIL=422(真实是 2)。反方向同样成立:
#     先出现一个「0 FAIL」片段就会把真失败盖掉。
function Read-Summary([string]$Text) {
    $line = ($Text -split "`r?`n" |
             Where-Object { $_ -match '^\s*===.*\d+\s*PASS.*\d+\s*FAIL' } |
             Select-Object -Last 1)
    if (-not $line) { return $null }
    if ($line -notmatch '(\d+)\s*PASS[^0-9]+(\d+)\s*FAIL') { return $null }
    # ★★ 先把值取走再做下一次 -match:$Matches 是**整个换掉**的,不是累加。
    #   本脚本 2026-08-06 首跑就栽在这:下面那次 SKIP 匹配把 $Matches 换成了 SKIP 的捕获,
    #   于是 test_repo 被报成 PASS=1(真实 25)——「1」正是它的 SKIP 数。
    #   ★ 危害方向要看清:它不是报错,是报出一个**小了 24 的、看着很正常的数**。
    #     覆盖账会因此少算而没人察觉 —— 与 run-tests.ps1 那次 FAIL=422 是同一类,
    #     但这次是往【少】的方向错,更难发现。
    $p = [int]$Matches[1]; $f = [int]$Matches[2]
    $skip = 0
    if ($line -match '(\d+)\s*SKIP') { $skip = [int]$Matches[1] }
    return [pscustomobject]@{ Pass = $p; Fail = $f; Skip = $skip }
}

$results = @()   # 每项: Name / Pass / Fail / Skip / Broken / Why

# ══════════════════════════════════════════════════════════════════════════
#  A 档:纯逻辑套件 —— 当前身份,零凭据接触,零机器状态改动
# ══════════════════════════════════════════════════════════════════════════
if (-not $RepairCredentials) {
    Write-Host ''
    Write-Host '[A 档:纯逻辑套件(当前身份,不碰任何凭据)]' -ForegroundColor Cyan

    if (-not (Test-Path -LiteralPath $Vpy)) {
        Write-Host "  X 找不到记忆 venv 的解释器:$Vpy" -ForegroundColor Red
        Write-Host '    这一档需要 psycopg + httpx + fastapi + pydantic —— 系统 python 没有 psycopg。' -ForegroundColor Yellow
        exit 1
    }

    # ★★★ C2 护栏(2026-08-06,对抗式复核抓到的 —— 本脚本第一版漏了这条):
    #   `repo.py:87` 是 `os.environ.get("LOCALAI_PG_USER", "ai_mem_local")`,
    #   **连哪个 PG 角色由环境变量决定**,而同目录 8 处兄弟套件会把它设成 `postgres`。
    #   本脚本原来只存还 PYTHONPATH / PYTHONIOENCODING,**LOCALAI_PG_USER 原样继承调用方**
    #   —— 而 A 档对外宣称的正是「当前身份,不碰任何凭据,零机器状态改动」。
    #   ★ 实测厘清:它**不能**让机主连上生产库(pg_ident 校验的是「系统用户+目标角色」这一对,
    #     机主对任何角色都不在表里;实测 REFUSED)。所以这不是宣称作废,
    #     而是 **C1 的放大器**:哪天以 ai-mem 跑,默认角色会被悄悄换成超级用户。
    #   ⇒ A 档无条件清掉它,让「当前身份」这句话在环境上也成立,不只在 Windows 身份上成立。
    if ($env:LOCALAI_PG_USER) {
        Write-Host ("  ! 检测到 LOCALAI_PG_USER={0} —— A 档已清除后再跑" -f $env:LOCALAI_PG_USER) -ForegroundColor Yellow
    }
    Push-Location $MemDir
    $prevPP = $env:PYTHONPATH; $prevEnc = $env:PYTHONIOENCODING; $prevRole = $env:LOCALAI_PG_USER
    $env:PYTHONPATH = '.'
    $env:PYTHONIOENCODING = 'utf-8'    # 让输出与日志一律 UTF-8,不受控制台码页摆布
    Remove-Item Env:\LOCALAI_PG_USER -ErrorAction SilentlyContinue
    foreach ($n in $NODB) {
        $out = & $Vpy $n 2>&1 | Out-String
        $code = $LASTEXITCODE
        $s = Read-Summary $out
        if (-not $s) {
            # ★ 跑了但没有汇总行 = 多半根本没跑起来。绝不当作通过。
            $results += [pscustomobject]@{ Name = $n; Broken = $true
                                           Why = "没有汇总行(退出码 $code)—— 多半没跑起来" }
            Write-Host ("  X {0,-30} 没跑起来" -f $n) -ForegroundColor Red
            continue
        }
        $results += [pscustomobject]@{ Name = $n; Pass = $s.Pass; Fail = $s.Fail; Skip = $s.Skip; Broken = $false }
        $c = if ($s.Fail -gt 0 -or $code -ne 0) { 'Red' } else { 'DarkGray' }
        Write-Host ("  {0,-30} PASS={1,-5} FAIL={2,-4} SKIP={3}" -f $n, $s.Pass, $s.Fail, $s.Skip) -ForegroundColor $c
        if ($s.Fail -gt 0) {
            ($out -split "`r?`n" | Where-Object { $_ -match 'FAIL' } | Select-Object -First 8) |
                ForEach-Object { Write-Host "        $_" -ForegroundColor Red }
        }
    }
    $env:PYTHONPATH = $prevPP; $env:PYTHONIOENCODING = $prevEnc
    if ($prevRole) { $env:LOCALAI_PG_USER = $prevRole }   # 还原调用方的值,别替人做决定
    Pop-Location
}

# ══════════════════════════════════════════════════════════════════════════
#  B 档:活库套件 —— 以 ai-mem 身份跑(生产身份本身)
# ══════════════════════════════════════════════════════════════════════════
if ($Scope -eq 'Full' -or $RepairCredentials) {

    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
      ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Host ''
        Write-Host '  X -Scope Full 需要管理员(要起以 ai-mem 身份运行的计划任务)。' -ForegroundColor Red
        Write-Host '    右键 run-memory-suite.cmd → 以管理员身份运行。' -ForegroundColor Yellow
        exit 1
    }

    # ★★ 守卫:worktree 里跑不了 B 档,而且原因不是"没构建产物"那种可以照跑的形状。
    #   实测:ai-mem 对主树 10-core\memory 有 ReadAndExecute 的显式 ACE,
    #   对 .claude\worktrees\... 下的副本【没有任何 ACE】⇒ 计划任务起来就是拒绝访问。
    #   与其让它跑出一堆看不懂的红,不如在这里说清楚。
    if (Test-Path (Join-Path $repo '.git') -PathType Leaf) {
        Write-Host ''
        Write-Host '  X 这是一个 git worktree —— B 档必须在主树里跑。' -ForegroundColor Red
        Write-Host '    ai-mem 只对主树的 10-core\memory 有读权限(setup 时授的),' -ForegroundColor Yellow
        Write-Host '    对 worktree 副本没有任何 ACE。请到主树跑同一条命令。' -ForegroundColor Yellow
        Write-Host '    ★ 别为此去给 worktree 授权 —— 那等于给记忆库代码开一个新的可读副本。' -ForegroundColor Yellow
        exit 1
    }

    $verifyIso = Join-Path $repo '90-ops\verify-isolation.ps1'

    # --- 前置:隔离形状存档 ------------------------------------------------
    Write-Host ''
    Write-Host '[前置:隔离形状存档(跑完要逐字节对回来)]' -ForegroundColor Cyan
    $PgData = Get-Path 'pg_data'
    $identPath = Join-Path $PgData 'pg_ident.conf'
    $hbaPath   = Join-Path $PgData 'pg_hba.conf'
    function Get-FileHashSafe([string]$p) {
        if (Test-Path -LiteralPath $p) { return (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }
        return 'MISSING'
    }
    $identBefore = Get-FileHashSafe $identPath
    $hbaBefore   = Get-FileHashSafe $hbaPath
    Write-Host ("  pg_ident.conf SHA256 = {0}" -f $identBefore.Substring(0, 16))
    Write-Host ("  pg_hba.conf   SHA256 = {0}" -f $hbaBefore.Substring(0, 16))
    if (Test-Path $verifyIso) {
        Write-Host '  隔离套件(前置):'
        $isoBefore = & powershell -NoProfile -ExecutionPolicy Bypass -File $verifyIso 2>&1 | Out-String
        $isoB = ($isoBefore -split "`r?`n" | Where-Object { $_ -match '\d+\s*PASS.*\d+\s*FAIL' } | Select-Object -Last 1)
        Write-Host ("    {0}" -f $isoB.Trim())
        if ($isoBefore -match 'FAIL\s+\S') {
            Write-Host '  X 隔离套件跑之前就是红的 —— 先修那个,不要在这个状态上跑记忆套件。' -ForegroundColor Red
            exit 1
        }
    } else {
        Write-Host '  ! 找不到 verify-isolation.ps1,跳过隔离前置核对' -ForegroundColor Yellow
    }

    # --- 口令:生成 → 同步全部依赖方 → 用完擦除 ----------------------------
    #  ★ 这一段与 apply-schema.ps1:91-110 是同一套机制,刻意保持一致:
    #    它已经踩过并修好了两个坑(服务 1069、计划任务 0x8007052E),
    #    在这里重新发明一遍只会重新踩一次。
    Write-Host ''
    Write-Host '[以 ai-mem 身份运行 —— 口令只活在这一次运行里]' -ForegroundColor Cyan
    function New-SafePassword {
        $b = New-Object byte[] 30
        [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
        return (([Convert]::ToBase64String($b) -replace '[+/=]', '').Substring(0, 24)) + '#Aa7'
    }
    $Machine = $env:COMPUTERNAME
    $pw = New-SafePassword

    # ★ 先把依赖方清点出来【再】改口令 —— 清点失败就什么都还没动,可以干净退出。
    $svcs = @(Get-CimInstance Win32_Service | Where-Object { $_.StartName -eq '.\ai-mem' })
    Write-Host ("  以 ai-mem 运行的服务:{0}" -f (($svcs.Name) -join ', '))
    $dumpTask = 'localai-memory-dump'
    $hasDumpTask = [bool](Get-ScheduledTask -TaskName $dumpTask -ErrorAction SilentlyContinue)
    Write-Host ("  以 ai-mem 运行的计划任务:{0}" -f $(if ($hasDumpTask) { $dumpTask } else { '(无)' }))

    Set-LocalUser -Name 'ai-mem' -Password (ConvertTo-SecureString $pw -AsPlainText -Force)
    # ★★ 口令耦合:改完必须同步,否则这些服务【下次启动】1069。
    #   注意是"下次启动"—— 正在跑的进程不受影响,所以这个坑不会当场暴露,
    #   会在下一次重启时以"记忆库整个不见了"的形态出现。
    $syncFailed = @()
    foreach ($s in $svcs) {
        $r = Invoke-CimMethod -InputObject $s -MethodName Change `
             -Arguments @{ StartName = '.\ai-mem'; StartPassword = $pw }
        if ($r.ReturnValue -ne 0) { $syncFailed += "$($s.Name)(RV=$($r.ReturnValue))" }
    }
    if ($hasDumpTask) {
        # Task Scheduler 把口令存在自己这里,不跟着 SetPassword 走 —— 服务同步循环碰不到它。
        $t = Get-ScheduledTask -TaskName $dumpTask
        Register-ScheduledTask -TaskName $dumpTask -Action $t.Actions -TaskPath $t.TaskPath `
            -User "$Machine\ai-mem" -Password $pw -RunLevel Limited -Force | Out-Null
    }
    if ($syncFailed.Count -gt 0) {
        Write-Host ''
        Write-Host ('  XX 服务凭据同步失败: {0}' -f ($syncFailed -join ', ')) -ForegroundColor Red
        Write-Host '  ★ 现在的状态是危险的:ai-mem 口令已换,但这些服务还存着旧口令,' -ForegroundColor Red
        Write-Host '    它们【下次启动】会 1069 失败(现在还在跑,所以你不会立刻看见)。' -ForegroundColor Red
        Write-Host '  修法:立刻重跑  run-memory-suite.ps1 -RepairCredentials' -ForegroundColor Yellow
        $pw = $null; [System.GC]::Collect()
        exit 1
    }
    Write-Host '  凭据已同步(服务 + 计划任务)OK' -ForegroundColor DarkGray

    if ($RepairCredentials) {
        $pw = $null; [System.GC]::Collect()
        Write-Host ''
        Write-Host '=== 凭据已重新同步。没有跑任何测试。 ===' -ForegroundColor Green
        exit 0
    }

    # --- 起一次性计划任务,以 ai-mem 跑 10 个活库套件 ----------------------
    $runDir = Join-Path $LogDir ('mem-suite-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force $runDir | Out-Null

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append("@echo off`r`nchcp 65001 >nul`r`n")
    [void]$sb.Append("cd /d `"$MemDir`"`r`n")
    [void]$sb.Append("set PYTHONPATH=.`r`n")
    [void]$sb.Append("set PYTHONIOENCODING=utf-8`r`n")
    foreach ($n in $DB) {
        $log = Join-Path $runDir ($n -replace '\.py$', '.log')
        [void]$sb.Append("`"$Vpy`" `"$n`" > `"$log`" 2>&1`r`n")
    }
    $cmdFile = Join-Path $runDir '_run.cmd'
    Write-NoBom $cmdFile $sb.ToString()

    $tn = 'localai-memsuite-oneshot'
    Write-Host ''
    Write-Host '[B 档:活库套件(ai-mem 身份)]' -ForegroundColor Cyan
    Write-Host ("  日志目录:{0}" -f $runDir) -ForegroundColor DarkGray
    $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument ('/c "' + $cmdFile + '"')
    Register-ScheduledTask -TaskName $tn -Action $action -User "$Machine\ai-mem" `
        -Password $pw -RunLevel Limited -Force | Out-Null
    Start-ScheduledTask -TaskName $tn
    # ★ 先等它真的进入 Running 再等它结束 —— 否则 Start 与轮询抢跑:
    #   任务还没起来时 State 已是 Ready,循环立刻退出 ⇒ 拿到上一次的结果,
    #   并把仍在跑的任务 Unregister 掉。(apply-schema.ps1:53 踩过)
    for ($i = 0; $i -lt 60; $i++) {
        if ((Get-ScheduledTask -TaskName $tn).State -eq 'Running') { break }
        Start-Sleep -Milliseconds 300
    }
    while ((Get-ScheduledTask -TaskName $tn).State -eq 'Running') { Start-Sleep -Seconds 2 }
    $taskRc = (Get-ScheduledTaskInfo -TaskName $tn).LastTaskResult
    Unregister-ScheduledTask -TaskName $tn -Confirm:$false

    $pw = $null; [System.GC]::Collect()

    foreach ($n in $DB) {
        $log = Join-Path $runDir ($n -replace '\.py$', '.log')
        if (-not (Test-Path -LiteralPath $log)) {
            $results += [pscustomobject]@{ Name = $n; Broken = $true; Why = '没有日志 —— 任务没跑到它' }
            Write-Host ("  X {0,-30} 没有日志" -f $n) -ForegroundColor Red
            continue
        }
        $out = Get-Content -LiteralPath $log -Raw -Encoding UTF8
        $s = Read-Summary $out
        if (-not $s) {
            # ★★★ 这是本车道 2026-08-06 实测抓到的形状,必须专门说清:
            #   这 10 个套件里有 9 个在连不上库时打印一行「跳过」然后 **sys.exit(0)**。
            #   ⇒ 退出码 0 = 成功。手工跑、看退出码的人会得到一个**假绿**。
            #   所以这里的判据是【汇总行】而不是退出码:没有汇总行一律算没跑起来。
            $tail = (($out -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 1))
            $results += [pscustomobject]@{ Name = $n; Broken = $true
                                           Why = "没有汇总行 —— 没跑起来。末行:$tail" }
            Write-Host ("  X {0,-30} 没跑起来 — {1}" -f $n, $tail) -ForegroundColor Red
            continue
        }
        $results += [pscustomobject]@{ Name = $n; Pass = $s.Pass; Fail = $s.Fail; Skip = $s.Skip; Broken = $false }
        $c = if ($s.Fail -gt 0) { 'Red' } else { 'DarkGray' }
        Write-Host ("  {0,-30} PASS={1,-5} FAIL={2,-4} SKIP={3}" -f $n, $s.Pass, $s.Fail, $s.Skip) -ForegroundColor $c
        if ($s.Fail -gt 0) {
            ($out -split "`r?`n" | Where-Object { $_ -match 'FAIL' } | Select-Object -First 8) |
                ForEach-Object { Write-Host "        $_" -ForegroundColor Red }
        }
    }
    if ($taskRc -ne 0) {
        Write-Host ("  ! 计划任务 LastTaskResult = {0}(非 0;逐套件结果以上面的汇总行为准)" -f $taskRc) -ForegroundColor Yellow
    }

    # --- 后置:隔离形状必须逐字节没变 --------------------------------------
    Write-Host ''
    Write-Host '[后置:隔离形状对账]' -ForegroundColor Cyan
    $identAfter = Get-FileHashSafe $identPath
    $hbaAfter   = Get-FileHashSafe $hbaPath
    $identSame = ($identAfter -eq $identBefore)
    $hbaSame   = ($hbaAfter -eq $hbaBefore)
    Write-Host ("  pg_ident.conf 未改动 : {0}" -f $(if ($identSame) { '是' } else { '★ 否 —— 出事了' })) `
        -ForegroundColor $(if ($identSame) { 'Green' } else { 'Red' })
    Write-Host ("  pg_hba.conf   未改动 : {0}" -f $(if ($hbaSame) { '是' } else { '★ 否 —— 出事了' })) `
        -ForegroundColor $(if ($hbaSame) { 'Green' } else { 'Red' })
    $isoAfterOk = $true
    if (Test-Path $verifyIso) {
        $isoAfter = & powershell -NoProfile -ExecutionPolicy Bypass -File $verifyIso 2>&1 | Out-String
        $isoA = ($isoAfter -split "`r?`n" | Where-Object { $_ -match '\d+\s*PASS.*\d+\s*FAIL' } | Select-Object -Last 1)
        Write-Host ("  隔离套件(后置):{0}" -f $isoA.Trim())
        if ($isoAfter -match 'FAIL\s+\S') { $isoAfterOk = $false }
    }
    if (-not ($identSame -and $hbaSame -and $isoAfterOk)) {
        Write-Host ''
        Write-Host '  XX 隔离形状变了 —— 这次运行的结果不可信,而且机器现在处于未知状态。' -ForegroundColor Red
        Write-Host '     不要相信上面的绿数字。先查 pg_ident / pg_hba 的 diff。' -ForegroundColor Red
        exit 1
    }
} elseif (-not $RepairCredentials) {
    Write-Host ''
    Write-Host '[B 档:活库套件]' -ForegroundColor Cyan
    Write-Host ("  没跑 —— {0} 个套件需要活库(ai-mem 身份)。加 -Scope Full 跑。" -f $DB.Count) -ForegroundColor Yellow
}

# ══════════════════════════════════════════════════════════════════════════
#  覆盖账
# ══════════════════════════════════════════════════════════════════════════
$ran     = @($results | Where-Object { -not $_.Broken })
$broken  = @($results | Where-Object { $_.Broken })
$totalP  = ($ran | Measure-Object -Property Pass -Sum).Sum
$totalF  = ($ran | Measure-Object -Property Fail -Sum).Sum
$totalS  = ($ran | Measure-Object -Property Skip -Sum).Sum
if (-not $totalP) { $totalP = 0 }; if (-not $totalF) { $totalF = 0 }; if (-not $totalS) { $totalS = 0 }

Write-Host ''
Write-Host '==================== 覆盖账 ====================' -ForegroundColor Cyan
Write-Host ("  跑了  : {0} / {1} 个套件" -f $ran.Count, $found.Count)
Write-Host ("  合计  : PASS={0}  FAIL={1}  SKIP={2}" -f $totalP, $totalF, $totalS)
if ($Scope -ne 'Full') {
    Write-Host ("  ★ 没跑:{0} 个活库套件(不是忽略 —— 需 ai-mem 身份,加 -Scope Full)" -f $DB.Count) -ForegroundColor Yellow
}
if ($totalS -gt 0) {
    Write-Host ("  ★ 有 {0} 条 SKIP —— SKIP 不是 PASS。逐条看上面哪个套件报的。" -f $totalS) -ForegroundColor Yellow
}
if ($broken.Count -gt 0) {
    Write-Host '  X 应该跑却没跑起来的:' -ForegroundColor Red
    foreach ($b in $broken) { Write-Host ("      {0} — {1}" -f $b.Name, $b.Why) -ForegroundColor Red }
}

Write-Host ''
if ($totalF -gt 0 -or $broken.Count -gt 0) {
    Write-Host ("X 未过(FAIL={0},没跑起来 {1} 个)" -f $totalF, $broken.Count) -ForegroundColor Red
    Write-Host '  ★ 记忆库代码属 P3d 车道 —— 本脚本只跑不修。把上面的红行原样交回去。' -ForegroundColor Yellow
    exit 1
}
Write-Host ("√ 通过:PASS={0} FAIL=0" -f $totalP) -ForegroundColor Green
exit 0
