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
$psqlExe = Join-Path $PgBin 'psql.exe'

# ---- 演练模式(可选):存在 DRILL 标记文件时,先埋一行金丝雀再转储 ----
#   为什么需要:库为空时,「恢复后表数/行数一致」这个校验是 0=0 的平凡真 —— 证明不了【数据】能回来。
#   埋一行带唯一标记的数据再走全流程,才真正验证数据保真。演练结束会自动清掉金丝雀。
#   ★ 必须在这里做:SSPI 认的是连接进程身份,只有本任务是以 ai-mem 跑的。
$drill = Test-Path (Join-Path $Stage 'DRILL')
$CANARY = 'CANARY_RESTORE_DRILL_7Q4X'
if ($drill) {
  Say "演练模式:埋入金丝雀 $CANARY"
  & $psqlExe -h 127.0.0.1 -p $PgPort -U postgres -d memory -v ON_ERROR_STOP=1 -c @"
INSERT INTO mem.l3_fact (statement, provenance, source_confidence, sensitivity_domain)
VALUES ('$CANARY', 'user_typed', 1.0, 'S0');
INSERT INTO mem.secret_ref (ref, value_kind, issuer, last4, sensitivity_domain)
VALUES ('drill.de.canary.iban', 'string', 'DrillBank', '7Q4X', 'S2')
ON CONFLICT (ref) DO NOTHING;
"@ 2>&1 | Add-Content $log
  Say "  金丝雀已埋(l3_fact + secret_ref 各一行)"
}

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

# ============================================================================
# 5) ★★ 自证可恢复 —— 每次转储都【当场恢复一遍】并比对,不是只做一次演练。
#    §8.5 铁律「没演练过的备份不算备份」的最强形式:把演练做进流程,
#    而不是靠人记得每季度做一次。库很小(~100KB),代价可忽略。
#    失败即整个转储失败 → backup.ps1 整体失败 → 不会产出一个「恢复不出来」的备份集。
# ============================================================================
$psql       = Join-Path $PgBin 'psql.exe'
$pgrestore  = Join-Path $PgBin 'pg_restore.exe'
$TestDb     = 'memory_restore_test'
$rehearseOk = $false
if ($okD) {
  Say "自证可恢复:恢复进 $TestDb 并比对"
  # 源库指纹:各表行数之和 + 表数量
  $srcSig = (& $psql -h 127.0.0.1 -p $PgPort -U postgres -d memory -tAc @"
SELECT (SELECT count(*) FROM information_schema.tables WHERE table_schema='mem' AND table_type='BASE TABLE')
    || '/' || (SELECT coalesce(sum(n_live_tup),0) FROM pg_stat_user_tables WHERE schemaname='mem')
"@ 2>&1 | Out-String).Trim()
  Say "  源库指纹(表数/行数) = $srcSig"

  & $psql -h 127.0.0.1 -p $PgPort -U postgres -d postgres -c "DROP DATABASE IF EXISTS $TestDb" 2>&1 | Add-Content $log
  & $psql -h 127.0.0.1 -p $PgPort -U postgres -d postgres -c "CREATE DATABASE $TestDb ENCODING 'UTF8' LC_COLLATE 'C' LC_CTYPE 'C' TEMPLATE template0" 2>&1 | Add-Content $log
  if ($LASTEXITCODE -eq 0) {
    & $pgrestore -h 127.0.0.1 -p $PgPort -U postgres -d $TestDb $dump 2>&1 | Add-Content $log
    $rcR = $LASTEXITCODE
    # 恢复后立刻 ANALYZE,否则 n_live_tup 还是 0(统计信息未更新)——否则会误判成丢数据
    & $psql -h 127.0.0.1 -p $PgPort -U postgres -d $TestDb -c 'ANALYZE' 2>&1 | Add-Content $log
    $dstSig = (& $psql -h 127.0.0.1 -p $PgPort -U postgres -d $TestDb -tAc @"
SELECT (SELECT count(*) FROM information_schema.tables WHERE table_schema='mem' AND table_type='BASE TABLE')
    || '/' || (SELECT coalesce(sum(n_live_tup),0) FROM pg_stat_user_tables WHERE schemaname='mem')
"@ 2>&1 | Out-String).Trim()
    Say "  恢复库指纹(表数/行数) = $dstSig  (pg_restore rc=$rcR)"
    $rehearseOk = ($rcR -eq 0) -and ($srcSig -eq $dstSig) -and ($srcSig -notmatch '^0/')
    if (-not $rehearseOk) { Say "  ✗ 指纹不一致或恢复报错 —— 这个转储【恢复不出原样】" }
    else { Say "  ✓ 恢复后表数与行数与源库一致" }

    # ★ 演练模式:光比对数量不够 —— 必须确认【那一行具体数据】真的回来了。
    if ($drill -and $rehearseOk) {
      $found = (& $psql -h 127.0.0.1 -p $PgPort -U postgres -d $TestDb -tAc `
        "SELECT count(*) FROM mem.l3_fact WHERE statement='$CANARY'" 2>&1 | Out-String).Trim()
      $foundS2 = (& $psql -h 127.0.0.1 -p $PgPort -U postgres -d $TestDb -tAc `
        "SELECT count(*) FROM mem.secret_ref WHERE ref='drill.de.canary.iban'" 2>&1 | Out-String).Trim()
      Say "  金丝雀在恢复库中:l3_fact=$found · secret_ref=$foundS2"
      if ($found -ne '1' -or $foundS2 -ne '1') {
        $rehearseOk = $false; Say "  ✗ 金丝雀没回来 —— 数据没有真正恢复"
      } else { Say "  ✓ 金丝雀数据完整回来了" }
    }
    & $psql -h 127.0.0.1 -p $PgPort -U postgres -d postgres -c "DROP DATABASE IF EXISTS $TestDb" 2>&1 | Add-Content $log
  } else { Say "  ✗ 建测试库失败" }
  "源=$srcSig 恢复=$dstSig 演练=$(if($drill){'是'}else{'否'}) 结果=$(if($rehearseOk){'PASS'}else{'FAIL'})" |
    Set-Content (Join-Path $Stage 'RESTORE-REHEARSAL.txt') -Encoding UTF8
}

# ---- 演练收尾:清掉金丝雀,别把演练数据留在生产库 ----
if ($drill) {
  & $psqlExe -h 127.0.0.1 -p $PgPort -U postgres -d memory -c @"
DELETE FROM mem.l3_fact  WHERE statement='$CANARY';
DELETE FROM mem.secret_ref WHERE ref='drill.de.canary.iban';
"@ 2>&1 | Add-Content $log
  Say "演练收尾:金丝雀已从生产库清除"
}

if ($okG -and $okD -and $rehearseOk) {
  # 成功标记:backup.ps1 只认这个文件,认不到就整体失败(不容忍静默跳过数据库的「成功」备份)
  "$stamp" | Set-Content (Join-Path $Stage 'PG_OK') -Encoding ASCII
  Say "OK"
  exit 0
} else {
  Say "FAILED"
  exit 1
}
