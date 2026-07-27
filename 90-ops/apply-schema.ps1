# =============================================================================
#  apply-schema.ps1 — P2 记忆库 schema 应用 · D30
#
#  ★ 在【你自己的管理员 PowerShell】里跑,不要贴进 Claude 的工具里执行。
#    (要用 ai-mem 密码起计划任务;错误回显可能带出密钥进 LLM 上下文,违 D23。)
#
#  做四件事:
#    1. pg_hba / pg_ident 加两个新角色的 SSPI 映射(ai-mem→ai_mem_local / ai_mem_remote)
#    2. 应用 10-core/memory/schema.sql   (以 mem_rw → 对象 OWNER=mem_rw)
#    3. 应用 10-core/memory/roles.sql    (以 postgres → 建角色需 superuser;此时对象已存在)
#    4. 跑 10-core/memory/verify.sql     (否定用例:试图攻破隔离,期望被拒)
#
#  ★ 顺序不可颠倒:schema.sql 里视图用 DROP+CREATE(要改列清单)会丢 GRANT,
#    roles.sql 必须在其后重新授权。
#
#  幂等:全部可重复应用。
# =============================================================================
param([switch]$SkipVerify)
$ErrorActionPreference = 'Stop'
try { Set-PSReadLineOption -HistorySaveStyle SaveNothing -ErrorAction SilentlyContinue } catch {}
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "  X 需管理员。" -ForegroundColor Red; exit 1 }

$PathsToml = Join-Path $PSScriptRoot '..\config\paths.toml'
function Get-Path([string]$Key) {
  $m = Select-String -Path $PathsToml -Pattern ("^\s*{0}\s*=\s*'([^']+)'" -f [regex]::Escape($Key)) | Select-Object -First 1
  if (-not $m) { throw "paths.toml 缺键: $Key" }
  return $m.Matches[0].Groups[1].Value
}
function Write-NoBom([string]$Path,[string]$Text) {
  [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}
$PgBin = Get-Path 'pg_bin'; $PgData = Get-Path 'pg_data'; $PgPort = Get-Path 'pg_port'
$PgRoot = Split-Path $PgBin -Parent
$SqlDir = Join-Path $PSScriptRoot '..\10-core\memory'
$psql = Join-Path $PgBin 'psql.exe'
$Machine = $env:COMPUTERNAME

# ---- 以 ai-mem 身份跑(SSPI 要求连接进程的 OS 身份就是 ai-mem)----
function New-SafePassword {
  $b = New-Object byte[] 30
  [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
  return (([Convert]::ToBase64String($b) -replace '[+/=]','').Substring(0,24)) + '#Aa7'
}
function Invoke-AsAiMem([string]$Body,[string]$Password) {
  $cmdFile = Join-Path $PgRoot '_schema_task.cmd'
  Write-NoBom $cmdFile ("@echo off`r`nchcp 65001 >nul`r`n" + $Body + "`r`n")
  $tn = 'localai-schema-oneshot'
  $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument ('/c "' + $cmdFile + '"')
  Register-ScheduledTask -TaskName $tn -Action $action -User "$Machine\ai-mem" -Password $Password -RunLevel Limited -Force | Out-Null
  Start-ScheduledTask -TaskName $tn
  while ((Get-ScheduledTask -TaskName $tn).State -eq 'Running') { Start-Sleep -Seconds 1 }
  $rc = (Get-ScheduledTaskInfo -TaskName $tn).LastTaskResult
  Unregister-ScheduledTask -TaskName $tn -Confirm:$false
  Remove-Item $cmdFile -Force -EA SilentlyContinue
  return $rc
}

Write-Host "=== 记忆库 schema 应用 ===" -ForegroundColor Cyan

# ---- 1. pg_hba / pg_ident 加两角色(幂等)----
$hba = Join-Path $PgData 'pg_hba.conf'; $ident = Join-Path $PgData 'pg_ident.conf'
$hbaText = Get-Content $hba -Raw; $identText = Get-Content $ident -Raw
$changed = $false
foreach ($r in 'ai_mem_local','ai_mem_remote') {
  if ($hbaText -notmatch [regex]::Escape($r)) {
    $hbaText = $hbaText.TrimEnd() + "`r`nhost    memory    $r    127.0.0.1/32    sspi  map=mem  include_realm=0"
    $changed = $true
  }
  if ($identText -notmatch [regex]::Escape($r)) {
    $identText = $identText.TrimEnd() + "`r`nmem        ai-mem           $r"
    $changed = $true
  }
}
if ($changed) {
  Write-NoBom $hba ($hbaText + "`r`n"); Write-NoBom $ident ($identText + "`r`n")
  & (Join-Path $PgBin 'pg_ctl.exe') reload -D $PgData | Out-Null
  Start-Sleep 2
  Write-Host "  [1] pg_hba / pg_ident 已加 ai_mem_local / ai_mem_remote 并 reload OK"
} else { Write-Host "  [1] pg_hba / pg_ident 已含两角色,跳过 OK" }

# ---- 需要 ai-mem 密码起计划任务 ----
$pw = New-SafePassword
Set-LocalUser -Name 'ai-mem' -Password (ConvertTo-SecureString $pw -AsPlainText -Force)
# ★ 密码耦合:同步所有以 ai-mem 运行的服务,否则它们下次启动 1069
$svcs = Get-WmiObject Win32_Service | Where-Object { $_.StartName -eq '.\ai-mem' }
foreach ($s in $svcs) {
  $r = $s.Change($null,$null,$null,$null,$null,$null,".\ai-mem",$pw,$null,$null,$null)
  if ($r.ReturnValue -ne 0) { throw "同步服务 $($s.Name) 凭据失败 RV=$($r.ReturnValue)" }
}
Write-Host ("      (已同步服务凭据: {0})" -f (($svcs.Name) -join ', '))

# ---- 2/3/4. 应用 SQL ----
function Run-Sql([string]$Role,[string]$File,[string]$LogName,[string]$Extra='-v ON_ERROR_STOP=1') {
  $log = Join-Path $PgRoot $LogName
  $body = ('"{0}" -h 127.0.0.1 -p {1} -U {2} -d memory {3} -f "{4}" > "{5}" 2>&1' -f `
           $psql,$PgPort,$Role,$Extra,$File,$log)
  $rc = Invoke-AsAiMem $body $pw
  return @{ rc = $rc; log = $log }
}

Write-Host "  [2] 应用 schema.sql(以 mem_rw)…"
$r2 = Run-Sql 'mem_rw' (Join-Path $SqlDir 'schema.sql') 'schema.log'
if ($r2.rc -ne 0) {
  Write-Host "      X 失败。日志尾:" -ForegroundColor Red
  Get-Content $r2.log -Tail 25 | ForEach-Object { "        $_" }
  exit 1
}
Write-Host "      OK"

Write-Host "  [3] 应用 roles.sql(以 postgres — 建角色需 superuser)…"
$r3 = Run-Sql 'postgres' (Join-Path $SqlDir 'roles.sql') 'roles.log'
if ($r3.rc -ne 0) {
  Write-Host "      X 失败。日志尾:" -ForegroundColor Red
  Get-Content $r3.log -Tail 25 | ForEach-Object { "        $_" }
  exit 1
}
Write-Host "      OK"

if (-not $SkipVerify) {
  Write-Host "  [4] 跑 verify.sql 否定用例(以 postgres,内部 SET ROLE 降权)…"
  # ★ 不加 ON_ERROR_STOP:否定用例【期望报错】,报错是通过的标志
  $r4 = Run-Sql 'postgres' (Join-Path $SqlDir 'verify.sql') 'verify.log' ''
  Write-Host ""
  Write-Host "=== 否定用例结果 ===" -ForegroundColor Cyan
  Get-Content $r4.log | ForEach-Object { "  $_" }
  Write-Host ""
  $txt = (Get-Content $r4.log -Raw)
  $fails = ([regex]::Matches($txt,'FAIL')).Count
  if ($fails -gt 0) {
    Write-Host ("  XX 有 {0} 处 FAIL —— 隔离/约束未达标,不要继续。" -f $fails) -ForegroundColor Red
  } else {
    Write-Host "  ✓ 无 FAIL。请人工确认标注『期望: ERROR』的用例确实报错了(报错=隔离生效)。" -ForegroundColor Green
  }
}

$pw = $null; [System.GC]::Collect()
Write-Host ""
Write-Host "=== 完成 ===" -ForegroundColor Green
Write-Host "  把上面输出贴回给 Claude 核验。"
