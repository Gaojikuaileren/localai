# =============================================================================
#  memory-dump.ps1 — 记忆库的【逻辑】导出(§8.5.5)· 由 backup.ps1 在复制 STATE 之前调用
#
#  ★ §8.5.5 铁律:活数据库【绝不能文件级复制】。对运行中的 PG data 目录 / Qdrant storage
#    做 robocopy,得到的是撕裂的快照 —— 典型的「备份成功但恢复不出来」。
#    本脚本产出的是逻辑转储与快照文件,复制【它们】才是安全的。
#
#  两条路径的身份问题不同:
#    · PostgreSQL —— 走 SSPI,认连接进程的 Windows 身份。故经【计划任务以 ai-mem 运行】
#      (setup-backup-task.ps1 一次性注册,凭据在 LSA)。全程无口令。
#    · Qdrant     —— 只认 api_key,与 OS 身份无关,本脚本直接调 HTTP 即可。
#
#  返回:$true = 全部产物验真通过;$false = 有问题(调用方必须整体失败,不得静默跳过)。
# =============================================================================
param([string]$DestDir)

$ErrorActionPreference = 'Continue'
$TaskName = 'localai-memory-dump'

$PathsToml = Join-Path $PSScriptRoot '..\..\config\paths.toml'
function Get-Path([string]$Key) {
  $m = Select-String -Path $PathsToml -Pattern ("^\s*{0}\s*=\s*'([^']+)'" -f [regex]::Escape($Key)) | Select-Object -First 1
  if (-not $m) { throw "paths.toml 缺键: $Key" }
  return $m.Matches[0].Groups[1].Value
}
$MemRoot = Get-Path 'memory'
$Stage   = Join-Path $MemRoot '_dumps'

function Fail($m) { Write-Host "  X $m" -ForegroundColor Red; return $false }

Write-Host '记忆库逻辑导出(§8.5.5):' -ForegroundColor Cyan

# ---------- 0. 预检:服务必须在跑 ----------
# 「悄悄跳过数据库的成功备份」是 §8.5.5 陷阱换的新马甲 —— 宁可响亮失败。
foreach ($svc in 'pg-mem','Qdrant','Qdrant-s2') {
  $s = Get-Service $svc -EA SilentlyContinue
  if (-not $s -or $s.Status -ne 'Running') {
    return (Fail "$svc 未运行 —— 拒绝产出一个不含记忆库的『成功』备份。先启动它。")
  }
}
Write-Host '  预检: pg-mem / Qdrant / Qdrant-s2 均在运行'

# ---------- 1. PostgreSQL(经计划任务,以 ai-mem 身份)----------
if (-not (Get-ScheduledTask -TaskName $TaskName -EA SilentlyContinue)) {
  return (Fail "计划任务 $TaskName 不存在。先以管理员跑一次 90-ops\setup-backup-task.ps1")
}
Remove-Item (Join-Path $Stage 'PG_OK') -Force -EA SilentlyContinue
Start-ScheduledTask -TaskName $TaskName
for ($i=0; $i -lt 40; $i++) { Start-Sleep 1; if ((Get-ScheduledTask -TaskName $TaskName).State -eq 'Running') { break } }
while ((Get-ScheduledTask -TaskName $TaskName).State -eq 'Running') { Start-Sleep 2 }
$rc = (Get-ScheduledTaskInfo -TaskName $TaskName).LastTaskResult
if (-not (Test-Path (Join-Path $Stage 'PG_OK')) -or $rc -ne 0) {
  Write-Host '  PG 转储日志尾:' -ForegroundColor Red
  Get-Content (Join-Path $Stage 'pg-dump.log') -Tail 20 -Encoding UTF8 -EA SilentlyContinue | ForEach-Object { "    $_" }
  return (Fail "PostgreSQL 转储失败(rc=$rc)")
}
$pgFiles = @(Get-ChildItem $Stage -File | Where-Object { $_.Name -match '^(memory_.*\.dump|globals_.*\.sql|PG_VERSION\.txt)$' })
Write-Host ("  PostgreSQL: {0} 个产物,共 {1:N2} MB" -f $pgFiles.Count, (($pgFiles | Measure-Object Length -Sum).Sum / 1MB))

# ---------- 2. Qdrant ×2(snapshot API;只需 api_key)----------
function Get-ApiKey([string]$cfg) {
  $raw = Get-Content $cfg -Raw -EA SilentlyContinue
  if ($raw -match '(?m)^\s*api_key:\s*(\S+)') { return [string]$matches[1] }
  return $null
}
$qdrantJobs = @(
  @{ Name='mem_main'; Cfg=(Get-Path 'qdrant_config');    Port=[int](Get-Path 'qdrant_http_port');    Snap=(Get-Path 'qdrant_snapshots') },
  @{ Name='mem_s2';   Cfg=(Get-Path 'qdrant_s2_config'); Port=[int](Get-Path 'qdrant_s2_http_port'); Snap=(Get-Path 'qdrant_s2_snapshots') }
)
$snapTotal = 0
foreach ($j in $qdrantJobs) {
  $key = Get-ApiKey $j.Cfg
  if (-not $key) { return (Fail "读不到 $($j.Name) 的 api_key: $($j.Cfg)") }
  $h = @{ 'api-key' = $key }
  try { $colls = (Invoke-RestMethod "http://127.0.0.1:$($j.Port)/collections" -Headers $h -EA Stop).result.collections }
  catch { return (Fail "$($j.Name) 枚举 collection 失败: $_") }
  foreach ($c in $colls) {
    $n = $c.name
    try {
      $res = Invoke-RestMethod "http://127.0.0.1:$($j.Port)/collections/$n/snapshots?wait=true" -Method Post -Headers $h -EA Stop
    } catch { return (Fail "$($j.Name)/$n 快照失败: $_") }
    $sf = Join-Path (Join-Path $j.Snap $n) $res.result.name
    if (-not (Test-Path $sf) -or (Get-Item $sf).Length -le 0) {
      return (Fail "$($j.Name)/$n 快照文件缺失或为空: $sf")
    }
    $snapTotal++
    Write-Host ("  Qdrant {0,-9} {1,-10} {2,10:N0} bytes" -f $j.Name, $n, (Get-Item $sf).Length)
  }
}
if ($snapTotal -eq 0) { return (Fail "一个 Qdrant 快照都没产出") }

# 版本溯源:snapshot 只能恢复进【同版本或更新的次版本】
$qver = 'unknown'
try { $qver = (Invoke-RestMethod "http://127.0.0.1:$([int](Get-Path 'qdrant_http_port'))/" -EA Stop).version } catch {}
@"
QDRANT_VERSION=$qver
NOTE=snapshot 只能恢复进【同版本或更新的次版本】。降级或跨大版本恢复会失败。
RESTORE=qdrant.exe --config-path <cfg> --snapshot <file>:<collection>   (覆盖同名加 --force-snapshot)
INSTANCES=mem_main(6333) / mem_s2(6335,S2 机密,独立 api_key)
"@ | Set-Content (Join-Path $Stage 'QDRANT_VERSION.txt') -Encoding UTF8

# ---------- 3. 复制进备份集 ----------
if ($DestDir) {
  $pgOut = Join-Path $DestDir 'memory-db\pg'
  $qdOut = Join-Path $DestDir 'memory-db\qdrant'
  New-Item -ItemType Directory -Force $pgOut, $qdOut | Out-Null
  foreach ($f in $pgFiles) { Copy-Item $f.FullName $pgOut -Force }
  Copy-Item (Join-Path $Stage 'QDRANT_VERSION.txt') $qdOut -Force
  foreach ($j in $qdrantJobs) {
    if (-not (Test-Path $j.Snap)) { continue }
    $sub = Join-Path $qdOut $j.Name
    New-Item -ItemType Directory -Force $sub | Out-Null
    # 只复制快照【文件】(它们不是活库,复制安全)
    Get-ChildItem $j.Snap -Recurse -File -Filter '*.snapshot' -EA SilentlyContinue |
      ForEach-Object { Copy-Item $_.FullName (Join-Path $sub $_.Name) -Force }
  }
  $n = (Get-ChildItem (Join-Path $DestDir 'memory-db') -Recurse -File).Count
  Write-Host ("  已写入备份集: memory-db\ ({0} 个文件)" -f $n)
  if ($n -lt 3) { return (Fail "memory-db 里文件太少($n),不合理") }
}

Write-Host '  记忆库导出完成 ✓' -ForegroundColor Green
return $true
