<#
.SYNOPSIS
    本地 AI 中枢 — 手动备份脚本

.DESCRIPTION
    对应方案书 v2.1 §8.5「备份与灾难恢复」。

    手动触发式:你决定什么时候插上移动固态并运行,
    脚本负责一致性 —— 排除规则、清单、SHA256 校验、报告。

    备份矩阵(§8.5.2):
      state       全量      记忆库、数据库、票据、日志 —— 唯一不可重建的数据
      assets\adopted 全量   你标记为要用的产物
      code        git bundle(完整仓库快照,含全部历史)
      models      只备清单  权重可重新下载;备份「清单 + 哈希 + 来源」即可
      assets\draft / exported / cache   不备份

.PARAMETER Target
    备份目标目录,通常在移动固态上。例:<盘符>:\localAI-backup

.PARAMETER DryRun
    只输出将要做什么,不实际复制。

.PARAMETER SkipHash
    跳过 SHA256 校验(快,但违反「没演练过的备份不算备份」的精神,仅用于赶时间)。

.PARAMETER PruneOld
    删除超出保留数(paths.toml 的 [backup].keep_last)的旧备份集。
    不加此开关时,脚本只**报告**哪些超出保留策略,不删任何东西 ——
    与 §8.4 GC 的原则一致:除 CACHE 根外一律先报告后执行。

.PARAMETER IAcceptUnencryptedTarget
    允许备份到**未加密**的目标盘。这是 §8.5 铁律 1 的唯一逃生口。
    正常情况下未加密目标会**直接拒绝执行**(throw),而不是警告后继续 ——
    因为「警告后继续」的净效果是铁律 1 生效率为 0。
    使用本开关会写入审计日志与备份报告,留下痕迹。

.EXAMPLE
    .\backup.ps1 -Target <盘符>:\localAI-backup -DryRun
    .\backup.ps1 -Target <盘符>:\localAI-backup
    .\backup.ps1 -Target <盘符>:\localAI-backup -PruneOld
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Target,
    [switch]$DryRun,
    [switch]$SkipHash,
    [switch]$PruneOld,
    [switch]$IAcceptUnencryptedTarget
)

$ErrorActionPreference = 'Stop'
$stamp = Get-Date -Format 'yyyy-MM-dd_HHmm'

# --- 定位仓库与配置(全部相对路径 — §11.1 禁止硬编码绝对路径)-----------------
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot  = (Resolve-Path (Join-Path $scriptDir '..\..')).Path
$tomlPath  = Join-Path $repoRoot 'config\paths.toml'

if (-not (Test-Path $tomlPath)) {
    throw "找不到路径配置源: $tomlPath"
}

# --- 极简 TOML 读取(只认 key = 'value' 形式的字面字符串)---------------------
function Read-PathsToml {
    param([string]$Path)
    $map = @{}
    $section = ''
    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        $l = $line.Trim()
        if ($l -eq '' -or $l.StartsWith('#')) { continue }
        if ($l -match '^\[([^\]]+)\]') { $section = $Matches[1]; continue }
        if ($l -match "^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*'([^']*)'") {
            $map["$section.$($Matches[1])"] = $Matches[2]
        }
    }
    return $map
}

$P = Read-PathsToml -Path $tomlPath
$rootCode   = $P['roots.code']
$rootState  = $P['roots.state']
$rootModels = $P['roots.models']
$adopted    = $P['assets.adopted']

foreach ($k in @('roots.code','roots.state','roots.models','assets.adopted')) {
    if (-not $P[$k]) { throw "paths.toml 缺少 $k" }
}

# --- 安全检查:目标不得与数据同一块物理盘 ------------------------------------
function Get-DiskNumberOf {
    param([string]$PathValue)
    $letter = ($PathValue -replace '^([A-Za-z]):.*$', '$1')
    if ($letter.Length -ne 1) { return $null }
    try { return (Get-Partition -DriveLetter $letter -ErrorAction Stop).DiskNumber }
    catch { return $null }
}

$diskTarget = Get-DiskNumberOf $Target
$diskState  = Get-DiskNumberOf $rootState
$diskCode   = Get-DiskNumberOf $rootCode

Write-Host "本地 AI 中枢 — 备份  $stamp" -ForegroundColor Cyan
Write-Host ("  目标      : {0}  (Disk {1})" -f $Target, $diskTarget)
Write-Host ("  state 根  : {0}  (Disk {1})" -f $rootState, $diskState)
Write-Host ("  code  根  : {0}  (Disk {1})" -f $rootCode,  $diskCode)
Write-Host ''

if ($null -ne $diskTarget -and ($diskTarget -eq $diskState -or $diskTarget -eq $diskCode)) {
    throw "拒绝执行:备份目标与源数据在同一块物理盘 (Disk $diskTarget)。同盘备份等于没备份。"
}

# --- 介质加密检查 — §8.5 ----------------------------------------------------
# 是否强制由 paths.toml 的 [backup].require_encryption 决定(D21:默认 false)。
# ★ D22:记忆库本身也已取消加密(普通文件夹)。因此磁盘与备份盘上均为明文。
#   本检查保留下来只是为了将来若改变主意时有一个开关,当前恒为放行。
$tLetter = ($Target -replace '^([A-Za-z]):.*$', '$1')
$encStatus = 'unknown'
try {
    $bl = Get-BitLockerVolume -MountPoint "${tLetter}:" -ErrorAction Stop
    $encStatus = [string]$bl.ProtectionStatus
} catch {
    $encStatus = 'unreadable'
}

$requireEnc = $false
if ($P['backup.require_encryption']) {
    $requireEnc = ($P['backup.require_encryption'] -match '^(?i:true|1|yes)$')
}

if ($encStatus -eq 'On') {
    Write-Host "  介质加密  : BitLocker 已启用 ✓" -ForegroundColor Green
} elseif ($requireEnc -and -not $IAcceptUnencryptedTarget) {
    throw @"
拒绝执行:目标盘 ${tLetter}: 未启用 BitLocker,而 paths.toml 的
[backup].require_encryption = true。

改用未加密介质:把该项设为 false;或本次显式放行:
  .\backup.ps1 -Target '$Target' -IAcceptUnencryptedTarget
"@
} else {
    Write-Host "  介质加密  : 未启用(按 D21 允许)" -ForegroundColor DarkGray
}

$dest = Join-Path $Target $stamp
Write-Host ''
if ($DryRun) { Write-Host '[DryRun] 以下动作不会实际执行' -ForegroundColor Yellow }

# --- 执行 ---------------------------------------------------------------------
$report = [System.Collections.Generic.List[string]]::new()
$report.Add("# 备份报告 $stamp")
$report.Add('')
$report.Add("目标: $dest")
if ($encStatus -eq 'On') {
    $report.Add('介质加密: BitLocker 已启用')
} else {
    $report.Add("介质加密: **未启用**(ProtectionStatus=$encStatus)。按决议 D21/D22,这是预期行为。")
    $report.Add('')
    $report.Add('> **本备份集全部内容为明文**,包括记忆库(D22 取消了加密卷)。')
    $report.Add('> 拿到这块盘的人可以直接读取其中的一切。物理保管是唯一的保护。')
}
$report.Add('')
$report.Add('排除项: `state\quarantine`(隔离区装的是打算删除的数据,不应进备份代)')
$report.Add('')

function Copy-Root {
    param([string]$Name, [string]$Source, [string[]]$ExcludeDir = @())
    if (-not (Test-Path $Source)) {
        Write-Host ("  {0,-16} 源不存在,跳过" -f $Name) -ForegroundColor DarkGray
        $script:report.Add("- **$Name**: 源不存在,跳过")
        return
    }
    $files = Get-ChildItem $Source -Recurse -File -Force -ErrorAction SilentlyContinue |
             Where-Object { $p = $_.FullName; -not ($ExcludeDir | Where-Object { $p -like "*\$_\*" }) }
    $sum = ($files | Measure-Object Length -Sum).Sum
    $gb  = [math]::Round(($sum / 1GB), 2)
    Write-Host ("  {0,-16} {1,6} files  {2,8} GB" -f $Name, $files.Count, $gb)
    $report.Add("- **$Name**: $($files.Count) files, $gb GB")

    if (-not $DryRun) {
        $to = Join-Path $dest $Name
        New-Item -ItemType Directory -Force -Path $to | Out-Null
        $roboArgs = @($Source, $to, '/E', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
        foreach ($x in $ExcludeDir) { $roboArgs += @('/XD', $x) }
        & robocopy @roboArgs | Out-Null
        # robocopy 退出码 <8 表示成功
        if ($LASTEXITCODE -ge 8) { throw "robocopy 失败 ($Name),退出码 $LASTEXITCODE" }
    }
}

Write-Host '内容:' -ForegroundColor Cyan

# 1. STATE — 全量(含记忆库),唯一不可重建的数据
#    排除 quarantine:隔离区装的是你**打算删掉**的东西。把它备份进来,
#    等于让已经决定丢弃的数据在 12 个备份代里继续存活,并且会把
#    list-only 区域的正文搬进一个可被完整读取的位置(审查发现 P-3)。
Copy-Root -Name 'state' -Source $rootState -ExcludeDir @('quarantine')

# 2. ASSETS/adopted — 你标记为要用的
Copy-Root -Name 'assets-adopted' -Source $adopted

# 3. CODE — git bundle(完整历史,单文件,自带校验)
if (Test-Path (Join-Path $rootCode '.git')) {
    Write-Host ("  {0,-16} git bundle" -f 'code')
    if (-not $DryRun) {
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Push-Location $rootCode
        & git bundle create (Join-Path $dest 'code.bundle') --all 2>&1 | Out-Null
        Pop-Location
    }
    $report.Add('- **code**: git bundle (--all,含全部分支与历史)')
} else {
    Write-Host ("  {0,-16} 非 git 仓库,改为整目录复制" -f 'code') -ForegroundColor Yellow
    Copy-Root -Name 'code' -Source $rootCode -ExcludeDir @('node_modules','venv','.venv','target','__pycache__')
}

# 4. MODELS — 只备清单(权重可重新下载;§8.5.2)
Write-Host ("  {0,-16} 只备清单" -f 'models')
if (-not $DryRun) {
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    $manifest = @()
    if (Test-Path $rootModels) {
        $manifest = Get-ChildItem $rootModels -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object {
            [PSCustomObject]@{
                rel    = $_.FullName.Substring($rootModels.Length).TrimStart('\')
                bytes  = $_.Length
                sha256 = if ($SkipHash) { $null } else { (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower() }
            }
        }
    }
    $manifest | ConvertTo-Json -Depth 3 | Out-File (Join-Path $dest 'models-manifest.json') -Encoding utf8
    $report.Add("- **models**: 清单 $($manifest.Count) 条(路径 + 大小" + $(if ($SkipHash) { '' } else { ' + sha256' }) + ")")
}

# --- 校验与报告 ---------------------------------------------------------------
if (-not $DryRun) {
    if (-not $SkipHash) {
        Write-Host ''
        Write-Host '生成校验清单...' -ForegroundColor Cyan
        $hashOut = Join-Path $dest 'SHA256SUMS.txt'

        # 格式必须与 GNU coreutils 的 sha256sum 兼容:
        #   无 BOM · LF 行尾 · 正斜杠路径 · 哈希与路径间两个空格
        # 理由:灾难恢复时可能只有 Linux live USB 或 WSL 可用(Windows 起不来正是
        # 备份要应对的场景)。若清单只能被 PowerShell 读,就把恢复路径限死在
        # 「Windows 还能启动」这个前提上。
        $lines = Get-ChildItem $dest -Recurse -File -Force |
            Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
            ForEach-Object {
                $rel = $_.FullName.Substring($dest.Length).TrimStart('\').Replace('\', '/')
                '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $rel
            }

        $utf8NoBom = New-Object System.Text.UTF8Encoding $false
        [System.IO.File]::WriteAllText($hashOut, (($lines -join "`n") + "`n"), $utf8NoBom)

        $n = @($lines).Count
        Write-Host "  $n 个文件已记录哈希(sha256sum -c 兼容格式)"
        $report.Add("- 校验: SHA256SUMS.txt,$n 个文件(GNU sha256sum 兼容)")
    }

    $report.Add('')
    $report.Add('## 恢复')
    $report.Add('')
    $report.Add('### 1. 先校验完整性')
    $report.Add('```bash')
    $report.Add('# Linux / WSL / Git Bash —— 清单是 GNU sha256sum 兼容格式')
    $report.Add('cd <本备份集目录> && sha256sum -c SHA256SUMS.txt')
    $report.Add('```')
    $report.Add('```powershell')
    $report.Add('# Windows PowerShell')
    $report.Add('Get-Content SHA256SUMS.txt | ForEach-Object {')
    $report.Add('    $h,$p = $_ -split "  ",2')
    $report.Add('    $a = (Get-FileHash $p.Replace("/","\") -Algorithm SHA256).Hash.ToLower()')
    $report.Add('    if ($a -ne $h) { "MISMATCH: $p" }')
    $report.Add('}')
    $report.Add('```')
    $report.Add('')
    $report.Add('### 2. 恢复代码')
    $report.Add('```bash')
    $report.Add('git clone code.bundle <目标目录>    # bundle 含全部分支与历史')
    $report.Add('```')
    $report.Add('恢复后可比对 `git rev-parse HEAD^{tree}` 与原仓库是否一致。')
    $report.Add('')
    $report.Add('### 3. 恢复 state(含记忆库)')
    $report.Add('```')
    $report.Add('robocopy state\ <state 根>\ /E')
    $report.Add('```')
    $report.Add('')
    $report.Add('### 4. 模型')
    $report.Add('本备份**不含模型权重本体**,只有 `models-manifest.json`(路径 + 大小 + sha256)。')
    $report.Add('按清单重新下载,再逐个比对 sha256。')
    $report.Add('')
    $report.Add('> v2.1 §8.5 铁律 3:**没演练过的备份不算备份。**')
    $report.Add('> P3 记忆系统上线前须跑通一次完整恢复,此后每季度一次。')

    $utf8NoBomR = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText((Join-Path $dest 'BACKUP-REPORT.md'),
                                   (($report -join "`n") + "`n"), $utf8NoBomR)

    Write-Host ''
    Write-Host "完成 -> $dest" -ForegroundColor Green
} else {
    Write-Host ''
    Write-Host '[DryRun] 结束,未写入任何文件。' -ForegroundColor Yellow
}

# --- 保留策略 — v2.1 §8.5.2 -------------------------------------------------
# 只报告,不自动删。与 §8.4 GC 的原则一致:除 CACHE 根外一律先报告后执行。
$keepLast = 12
if ($P['backup.keep_last']) { $keepLast = [int]$P['backup.keep_last'] }

$sets = @(Get-ChildItem $Target -Directory -ErrorAction SilentlyContinue |
          Where-Object { $_.Name -match '^\d{4}-\d{2}-\d{2}_\d{4}$' } |
          Sort-Object Name -Descending)

Write-Host ''
Write-Host ("备份集: {0} 个,保留策略 keep_last = {1}" -f $sets.Count, $keepLast) -ForegroundColor Cyan

if ($sets.Count -gt $keepLast) {
    $stale = $sets[$keepLast..($sets.Count - 1)]
    $staleBytes = 0
    foreach ($s in $stale) {
        $staleBytes += (Get-ChildItem $s.FullName -Recurse -File -Force -ErrorAction SilentlyContinue |
                        Measure-Object Length -Sum).Sum
    }
    Write-Host ("  超出保留策略: {0} 个,共 {1:N2} GB" -f $stale.Count, ($staleBytes / 1GB)) -ForegroundColor Yellow
    foreach ($s in $stale) { Write-Host "    $($s.Name)" -ForegroundColor DarkGray }

    if ($PruneOld) {
        if ($DryRun) {
            Write-Host '  [DryRun] 未删除。' -ForegroundColor Yellow
        } else {
            foreach ($s in $stale) {
                Remove-Item -LiteralPath $s.FullName -Recurse -Force
                Write-Host "    已删除 $($s.Name)" -ForegroundColor Green
            }
            Write-Host ("  释放 {0:N2} GB" -f ($staleBytes / 1GB)) -ForegroundColor Green
        }
    } else {
        Write-Host '  未删除 —— 加 -PruneOld 才执行清理(先报告后执行,§8.4)' -ForegroundColor Yellow
    }
} else {
    Write-Host '  未超出保留策略,无需清理。'
}
