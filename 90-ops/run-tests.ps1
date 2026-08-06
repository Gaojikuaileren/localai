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
#    pwsh -File 90-ops\run-tests.ps1              只跑快的那层(约 20 秒,提交前用)
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
# ★ 记忆套件已裁定不自动跑(见 $RULES 里的 reason),所以这里【不需要】它的解释器。
#   将来若要让它跑起来:venv 路径必须先登记进 config/paths.toml 再从那儿读 ——
#   §11.1 路径契约禁止代码里出现绝对路径,而 pre-commit 钩子会当场抓(本脚本就被抓过一次)。

# --- 规则表:目录 → 怎么跑 ------------------------------------------------
#  ★ 这是【唯一】的分类来源。新增测试目录必须在这里登记,否则反向全表会判红。
#  runnable = $false 的条目不是"忽略",是**已裁定不在本机自动跑**,并写明理由。
#  ★★★ 顺序有语义:Get-Rule **首个匹配胜出**。文件级例外必须排在它所属目录那条**之前**,
#     排在后面会**静默失效** —— 不报错、不告警、-ListOnly 照样显示 manual。
#     下面那条顺序护栏就是为这件事写的,别把它删了。
#  ★ 字段名 `Dir` 从此**名不副实**:它现在也接文件路径片段(见 test_tainted.py 那条)。
#    改名单独提交,不在这次混改 —— 一次提交只做一件事。
$RULES = @(
    # ★★ 文件级例外,必须在 '10-core\memory' 之前(见上方顺序说明)。
    #   test_tainted.py **不连数据库**:它验的是密封/解封与档位允许表(纯逻辑),
    #   而它一直被 memory 目录那条规则连坐成 manual ⇒ 75 条断言从来没有人跑过。
    #   ★ 独立复核过:75 PASS / 0 FAIL · 退出码 0 · 零 DB import。
    @{ Dir = 'memory\test_tainted.py'; Tier = 'fast'; Interp = $SysPy; Runnable = $true
       Reason = '不连数据库,纯逻辑(密封/解封 + 档位允许表)—— 此前被 memory 目录整条连坐成 manual' }
    @{ Dir = '10-core\gateway';    Tier = 'fast'; Interp = $SysPy; Runnable = $true
       Reason = '纯逻辑,无外部状态' }
    @{ Dir = '10-core\gpu-broker'; Tier = 'fast'; Interp = $SysPy; Runnable = $true
       Reason = '纯逻辑,无外部状态' }
    @{ Dir = '10-core\memory';     Tier = 'manual'; Interp = $null; Runnable = $false
       Reason = '★ 连的是【真实】记忆库(dbname=memory),且 pg_ident 只映射 ai-mem —— ' +
                '当前身份 SSPI 会被拒,结构上跑不了;其中 test_s9_drill.py 还是 pg_dump/pg_restore ' +
                '恢复演练。必须以 ai-mem 身份、有意识地手动跑,不进自动门禁。' }
)

function Get-Rule([string]$fullPath) {
    foreach ($r in $RULES) {
        if ($fullPath -like ('*' + $r.Dir + '*')) { return $r }
    }
    return $null
}

# ══════════════════════════════════════════════════════════════════════════
#  ★★★ 顺序护栏 —— 把「排错位置」这种**静默失效**变成当场判红。
#
#  Get-Rule 首个匹配胜出。文件级例外若排到它所属目录那条**后面**,
#  目录规则会先命中 ⇒ 例外**永远不生效**,而且:
#    · 不报错 · 不告警 · -ListOnly 照样把它显示成 manual
#  ⇒ 那 75 条断言会继续没人跑,而门禁看起来一切正常。
#    这正是本仓反复吃亏的那个形状:**看着被守住了,实际没守**。
#
#  ★ 判据用**真实的规则表 + 真实的 Get-Rule**,不另建一份模型 ——
#    另建一份的话,它验的是那份模型,不是这个脚本。
# ══════════════════════════════════════════════════════════════════════════
$__probe = Join-Path $repo '10-core\memory\test_tainted.py'
$__hit = Get-Rule $__probe
if (-not $__hit -or -not $__hit.Runnable -or $__hit.Tier -ne 'fast') {
    Write-Host ""
    Write-Host "X 规则表顺序错了:test_tainted.py 命中的是 '$($__hit.Dir)'(Tier=$($__hit.Tier))" -ForegroundColor Red
    Write-Host "  ★ 文件级例外必须排在 '10-core\memory' 那条【之前】—— Get-Rule 首个匹配胜出。" -ForegroundColor Red
    Write-Host "  ★ 排在后面不会报错、不会告警、-ListOnly 也照显 manual —— 它会【静默失效】," -ForegroundColor Red
    Write-Host "    而那 75 条断言继续没人跑。这条护栏就是为了让它当场红。" -ForegroundColor Red
    exit 1
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
$totalPass = 0; $totalFail = 0
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
    if ($summaryLine -and $summaryLine -match '(\d+)\s*PASS[^0-9]+(\d+)\s*FAIL') {
        $p = [int]$Matches[1]; $f = [int]$Matches[2]
    }
    $sawSummary = [bool]$summaryLine
    return [pscustomobject]@{ Pass = $p; Fail = $f; Code = $code; Out = $out; SawSummary = $sawSummary }
}

foreach ($f in $found) {
    $r = Get-Rule $f.FullName
    $rel = $f.FullName.Replace($repo + '\', '')
    if (-not $r.Runnable) { $skipped += [pscustomobject]@{ File = $rel; Reason = $r.Reason }; continue }
    if (-not $r.Interp) {
        $broken += [pscustomobject]@{ File = $rel; Why = '找不到解释器' }; continue
    }
    $res = Invoke-PySuite $f $r.Interp
    if (-not $res.SawSummary) {
        # 跑了但没有汇总行 = 多半根本没跑起来。绝不当作通过。
        $broken += [pscustomobject]@{ File = $rel; Why = "没有汇总行(退出码 $($res.Code))—— 多半没跑起来" }
        Write-Host ("  X {0,-46} 没跑起来" -f $rel) -ForegroundColor Red
        continue
    }
    $totalPass += $res.Pass; $totalFail += $res.Fail
    $ran += $rel
    $color = if ($res.Fail -gt 0 -or $res.Code -ne 0) { 'Red' } else { 'DarkGray' }
    Write-Host ("  {0,-46} PASS={1,-5} FAIL={2}" -f $rel, $res.Pass, $res.Fail) -ForegroundColor $color
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
    $srcDir = Join-Path $repo '20-client-win\app'
    $newestSrc = Get-ChildItem $srcDir -Recurse -Include *.cs, *.xaml -File -ErrorAction SilentlyContinue |
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
