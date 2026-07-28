# =============================================================================
#  pg-dump-task.ps1 — 由计划任务【以 ai-mem 身份】运行的 PostgreSQL 逻辑转储
#
#  ★ 为什么要绕这一圈:PG 走 SSPI 认证(D30),认的是【连接进程的 Windows 身份】。
#    备份脚本以你(管理员)身份跑,pg_ident 里没有你的映射 → 连不上。
#    而 D23 又禁止把口令存盘。
#    解法:setup-backup-task.ps1 一次性注册本脚本为【以 ai-mem 运行】的计划任务
#    (凭据由任务计划程序存进 LSA),此后 backup.ps1 只需 Start-ScheduledTask,
#    全程无口令、无明文、不削弱隔离。
#
#  产出落到暂存区 {state}\memory\_dumps\(强 ACL 内),由 backup.ps1 复制进备份集。
#  ★ §8.5.5:活库绝不文件级复制 —— 这里产出的是【逻辑转储】,复制它是安全的。
# =============================================================================
$ErrorActionPreference = 'Continue'

$PathsToml = Join-Path $PSScriptRoot '..\..\config\paths.toml'
function Get-Path([string]$Key) {
  $m = Select-String -Path $PathsToml -Pattern ("^\s*{0}\s*=\s*'([^']+)'" -f [regex]::Escape($Key)) | Select-Object -First 1
  if (-not $m) { throw "paths.toml 缺键: $Key" }
  return $m.Matches[0].Groups[1].Value
}
$PgBin   = Get-Path 'pg_bin'
$PgData  = Get-Path 'pg_data'
$PgPort  = Get-Path 'pg_port'
$MemRoot = Get-Path 'memory'
$Stage   = Join-Path $MemRoot '_dumps'
New-Item -ItemType Directory -Force $Stage | Out-Null

$log = Join-Path $Stage 'pg-dump.log'
function Say($m) { "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m | Add-Content $log }
Set-Content $log "=== pg-dump-task 开始 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') · 身份 $env:USERNAME ==="

# 旧产物先清,避免「上次的转储冒充这次的」
Get-ChildItem $Stage -Filter 'memory_*.dump' -EA SilentlyContinue | Remove-Item -Force -EA SilentlyContinue
Get-ChildItem $Stage -Filter 'globals_*.sql' -EA SilentlyContinue | Remove-Item -Force -EA SilentlyContinue
Remove-Item (Join-Path $Stage 'PG_OK') -Force -EA SilentlyContinue

$stamp = Get-Date -Format 'yyyy-MM-dd_HHmm'
$dump    = Join-Path $Stage ("memory_{0}.dump"  -f $stamp)
$globals = Join-Path $Stage ("globals_{0}.sql"  -f $stamp)

# 1) 角色定义(pg_dumpall --globals-only 需超级用户读 pg_authid;SSPI 映射 ai-mem→postgres)
Say "pg_dumpall --globals-only"
& (Join-Path $PgBin 'pg_dumpall.exe') -h 127.0.0.1 -p $PgPort -U postgres --globals-only -f $globals 2>&1 | Add-Content $log
$rcG = $LASTEXITCODE

# 2) memory 库本体(自定义格式:压缩 + 可选择性恢复)
Say "pg_dump -Fc -d memory"
& (Join-Path $PgBin 'pg_dump.exe') -h 127.0.0.1 -p $PgPort -U postgres -Fc -d memory -f $dump 2>&1 | Add-Content $log
$rcD = $LASTEXITCODE

# 3) 恢复所需的溯源信息 —— 没有它,恢复现场不知道该用什么参数建空集群
$pgver = (Get-Content (Join-Path $PgData 'PG_VERSION') -EA SilentlyContinue).Trim()
@"
PG_MAJOR=$pgver
INITDB_ARGS=--encoding=UTF8 --locale=C --data-checksums
NOTE=恢复时新集群必须用完全相同的 initdb 参数,否则 pg_restore 可能失败或损坏中文
DUMP_FORMAT=custom (pg_dump -Fc) -> 用 pg_restore 恢复
GLOBALS=globals_*.sql 含角色定义与 SCRAM 哈希;裸机恢复须先 psql -f 它
"@ | Set-Content (Join-Path $Stage 'PG_VERSION.txt') -Encoding UTF8

# 4) ★ 逐件验真 —— 退出码 0 且文件存在且非空。半截产物绝不能带着「成功」退出。
$okG = ($rcG -eq 0) -and (Test-Path $globals) -and ((Get-Item $globals).Length -gt 0)
$okD = ($rcD -eq 0) -and (Test-Path $dump)    -and ((Get-Item $dump).Length    -gt 0)
Say ("globals rc=$rcG size=" + $(if (Test-Path $globals) { (Get-Item $globals).Length } else { 'NA' }))
Say ("dump    rc=$rcD size=" + $(if (Test-Path $dump)    { (Get-Item $dump).Length    } else { 'NA' }))

if ($okG -and $okD) {
  # 成功标记:backup.ps1 只认这个文件,认不到就整体失败(不容忍静默跳过数据库的「成功」备份)
  "$stamp" | Set-Content (Join-Path $Stage 'PG_OK') -Encoding ASCII
  Say "OK"
  exit 0
} else {
  Say "FAILED"
  exit 1
}
