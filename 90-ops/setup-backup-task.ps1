# =============================================================================
#  setup-backup-task.ps1 — 一次性:注册「以 ai-mem 身份跑 PG 转储」的计划任务
#
#  ★ 在【你自己的管理员 PowerShell】里跑,只需跑这一次。
#
#  为什么需要它:PG 走 SSPI 认证(D30),认的是连接进程的 Windows 身份。备份脚本以你
#  (管理员)身份运行,pg_ident 里没有你的映射 → 连不上;而 D23 禁止把口令存盘。
#  注册成计划任务后,凭据由任务计划程序存进 LSA,此后 backup.ps1 只需触发它 ——
#  **全程无口令、无明文、不削弱账户隔离**。
#
#  ★ 本脚本会重置 ai-mem 密码并同步所有以它运行的服务(与 install-* 同一套做法)。
#  幂等:可重复运行。
# =============================================================================
$ErrorActionPreference = 'Stop'
try { Set-PSReadLineOption -HistorySaveStyle SaveNothing -ErrorAction SilentlyContinue } catch {}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "  X 需要管理员。" -ForegroundColor Red; exit 1 }

$TaskName = 'localai-memory-dump'
$Inner    = (Resolve-Path (Join-Path $PSScriptRoot 'backup\pg-dump-task.ps1')).Path
$Machine  = $env:COMPUTERNAME

$PathsToml = Join-Path $PSScriptRoot '..\config\paths.toml'
function Get-Path([string]$Key) {
  $m = Select-String -Path $PathsToml -Pattern ("^\s*{0}\s*=\s*'([^']+)'" -f [regex]::Escape($Key)) | Select-Object -First 1
  if (-not $m) { throw "paths.toml 缺键: $Key" }
  return $m.Matches[0].Groups[1].Value
}
$MemRoot = Get-Path 'memory'
$Stage   = Join-Path $MemRoot '_dumps'
$OpsDir  = Split-Path $Inner -Parent

Write-Host "=== 注册记忆库转储任务(以 ai-mem 运行)===" -ForegroundColor Cyan
Write-Host "  脚本: $Inner"
Write-Host "  暂存: $Stage"

# 1) 暂存区 + 让 ai-mem 能写;资产侧拒写(转储是记忆的明文副本,D22)
New-Item -ItemType Directory -Force $Stage | Out-Null
& icacls $Stage /grant "ai-mem:(OI)(CI)(M)" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "  X icacls 授权失败" -ForegroundColor Red; exit 1 }
& icacls $Stage /deny "ai-asset:(OI)(CI)(F)" "ai-exec:(OI)(CI)(F)" | Out-Null
Write-Host "  [1] 暂存区就绪(ai-mem 可写 · 资产侧拒绝)"

# 2) ai-mem 要能读到备份脚本目录(它在 code 树里,D31:代码非机密,给只读执行)
& icacls $OpsDir /grant "ai-mem:(OI)(CI)(RX)" | Out-Null
# 也要能读 config/paths.toml
& icacls (Split-Path $PathsToml -Parent) /grant "ai-mem:(OI)(CI)(RX)" | Out-Null
Write-Host "  [2] ai-mem 可读脚本与 paths.toml(D31)"

# 3) 重置 ai-mem 密码,并同步所有以它运行的服务(按 SID 判定,不用字符串匹配)
$b = New-Object byte[] 30
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
$pw = (([Convert]::ToBase64String($b) -replace '[+/=]','').Substring(0,24)) + '#Aa7'
Set-LocalUser -Name 'ai-mem' -Password (ConvertTo-SecureString $pw -AsPlainText -Force)
$aiSid = (New-Object System.Security.Principal.NTAccount('ai-mem')).Translate(
           [System.Security.Principal.SecurityIdentifier]).Value
$synced = @()
foreach ($s in (Get-WmiObject Win32_Service)) {
  if (-not $s.StartName) { continue }
  try {
    $sid = (New-Object System.Security.Principal.NTAccount($s.StartName.TrimStart('.','\'))
           ).Translate([System.Security.Principal.SecurityIdentifier]).Value
  } catch { continue }
  if ($sid -ne $aiSid) { continue }
  $r = $s.Change($null,$null,$null,$null,$null,$null,".\ai-mem",$pw,$null,$null,$null)
  if ($r.ReturnValue -ne 0) { Write-Host "  X 同步 $($s.Name) 失败 RV=$($r.ReturnValue)" -ForegroundColor Red; exit 1 }
  $synced += $s.Name
}
Write-Host ("  [3] ai-mem 密码已重置;同步服务: {0}" -f ($synced -join ', '))

# 4) 注册计划任务(凭据存进 LSA;此后触发无需口令)
if (Get-ScheduledTask -TaskName $TaskName -EA SilentlyContinue) {
  Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}
$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
            -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}"' -f $Inner)
Register-ScheduledTask -TaskName $TaskName -Action $action `
  -User "$Machine\ai-mem" -Password $pw -RunLevel Limited `
  -Description 'LocalAI Hub: PostgreSQL 逻辑转储(以 ai-mem 跑,供 backup.ps1 触发)' | Out-Null
$pw = $null; [System.GC]::Collect()
Write-Host "  [4] 计划任务 $TaskName 已注册(以 .\ai-mem 运行,凭据存 LSA)"

# 5) 立刻试跑一次,证明它真的能连库转储
Write-Host "  [5] 试跑…"
Start-ScheduledTask -TaskName $TaskName
for ($i=0; $i -lt 40; $i++) { Start-Sleep 1; if ((Get-ScheduledTask -TaskName $TaskName).State -eq 'Running') { break } }
while ((Get-ScheduledTask -TaskName $TaskName).State -eq 'Running') { Start-Sleep 2 }
$rc = (Get-ScheduledTaskInfo -TaskName $TaskName).LastTaskResult

if ((Test-Path (Join-Path $Stage 'PG_OK')) -and $rc -eq 0) {
  Write-Host "      OK 转储成功:" -ForegroundColor Green
  Get-ChildItem $Stage -File | ForEach-Object { "        {0,12:N0}  {1}" -f $_.Length, $_.Name }
  Write-Host ""
  Write-Host "=== 完成 ✓ 此后 backup.ps1 会自动触发本任务,不再需要口令。 ===" -ForegroundColor Green
} else {
  Write-Host "      X 试跑失败(rc=$rc)。日志:" -ForegroundColor Red
  Get-Content (Join-Path $Stage 'pg-dump.log') -Tail 25 -Encoding UTF8 -EA SilentlyContinue | ForEach-Object { "        $_" }
  exit 1
}
