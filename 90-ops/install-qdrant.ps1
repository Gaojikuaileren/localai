# =============================================================================
#  install-qdrant.ps1 — P2 记忆库:Qdrant ×2(mem_main + mem_s2)装到 ai-mem 下 · D30
#
#  ★ 在【你自己的管理员 PowerShell】里跑,不要贴进 Claude 的工具里执行。
#
#  设计见 00-docs/memory-backbone-design.md。要点:
#   · 官方原生 windows-msvc 二进制 + NSSM 包装成服务(qdrant.exe 非 SCM 感知,裸 sc 报 1053)。
#   · 两实例(用户拍板双实例双端口 · §4.11.4):
#       Qdrant     mem_main  127.0.0.1:6333/6334
#       Qdrant-s2  mem_s2    127.0.0.1:6335/6336(S2 机密 · local 独有)
#   · 都以 .\ai-mem 运行 · 只听回环 · 各自 api_key(bearer;Qdrant 无 SSPI 等价物)。
#   · api_key 写进各自 config.yaml(强 ACL 保护,不进 git/paths.toml);账户密码经 WMI 配进服务。
#   · ★ 密码耦合:本脚本会重置 ai-mem 密码,并【同步更新已存在的 ai-mem 服务(pg-mem)】,
#     否则 pg-mem 下次启动 1069。见 STEP 4。
#
#  幂等 fail-fast。前置:install-postgres.ps1 已跑通(pg-mem 在)+ 已下载 qdrant/nssm 两个 zip。
# =============================================================================
param([string]$QdrantZip,[string]$NssmZip)
$ErrorActionPreference = 'Stop'
try { Set-PSReadLineOption -HistorySaveStyle SaveNothing -ErrorAction SilentlyContinue } catch {}
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "  X 需管理员。" -ForegroundColor Red; exit 1 }

$QVER = 'v1.18.3'
$PathsToml = Join-Path $PSScriptRoot '..\config\paths.toml'
function Get-Path([string]$Key) {
  $m = Select-String -Path $PathsToml -Pattern ("^\s*{0}\s*=\s*'([^']+)'" -f [regex]::Escape($Key)) | Select-Object -First 1
  if (-not $m) { throw "paths.toml 缺键: $Key" }
  return $m.Matches[0].Groups[1].Value
}
function Write-NoBom([string]$Path,[string]$Text) {
  [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}
function New-Secret([int]$Bytes=32) {
  $b = New-Object byte[] $Bytes
  [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
  return ([Convert]::ToBase64String($b) -replace '[+/=]','')
}
function New-SafePassword { return (New-Secret 30).Substring(0,24) + '#Aa7' }
function Grant-UserRight([string]$Account,[string]$Right,[string]$WorkDir) {
  $sid = (New-Object System.Security.Principal.NTAccount($Account)).Translate([System.Security.Principal.SecurityIdentifier]).Value
  $inf = Join-Path $WorkDir "sr_$Right.inf"; $sdb = Join-Path $WorkDir "sr.sdb"
  secedit /export /cfg $inf /areas USER_RIGHTS | Out-Null
  $c = Get-Content $inf
  $line = ($c | Where-Object { $_ -match "^$Right\s*=" } | Select-Object -First 1)
  if ($line) { if ($line -match [regex]::Escape($sid)) { Remove-Item $inf,$sdb -Force -EA SilentlyContinue; return }
    $c = $c -replace [regex]::Escape($line), ($line + ",*$sid") }
  else { $c = $c -replace '(\[Privilege Rights\])', ("`$1`r`n$Right = *$sid") }
  Set-Content $inf $c -Encoding Unicode
  secedit /import /db $sdb /cfg $inf /areas USER_RIGHTS | Out-Null
  secedit /configure /db $sdb /areas USER_RIGHTS | Out-Null
  Remove-Item $inf,$sdb -Force -EA SilentlyContinue
}
function Set-SvcAccount([string]$Svc,[string]$Pw) {   # WMI 设账户(密码不进命令行)
  $w = Get-WmiObject Win32_Service -Filter "Name='$Svc'"
  $r = $w.Change($null,$null,$null,$null,$null,$null,".\ai-mem",$Pw,$null,$null,$null)
  if ($r.ReturnValue -ne 0) { throw "设 $Svc 账户失败 ReturnValue=$($r.ReturnValue)" }
}

# 路径
$QBin  = Get-Path 'qdrant_bin'                 # ...\qdrant\bin
$QRoot = Split-Path $QBin -Parent              # ...\qdrant
$MemRoot = Split-Path $QRoot -Parent           # ...\memory
$S2Root  = Join-Path $MemRoot 'qdrant-s2'
$QExe = Join-Path $QBin 'qdrant.exe'
$Nssm = Join-Path $QBin 'nssm.exe'
Write-Host "=== Qdrant $QVER ×2 -> ai-mem ===" -ForegroundColor Cyan

# ============================ 1 · 目录 ============================
foreach ($d in @($QBin, (Get-Path 'qdrant_storage'), (Get-Path 'qdrant_snapshots'),
                  (Join-Path $QRoot 'config'), (Join-Path $QRoot 'tmp'), (Join-Path $QRoot 'logs'),
                  (Get-Path 'qdrant_s2_storage'), (Get-Path 'qdrant_s2_snapshots'),
                  (Join-Path $S2Root 'config'), (Join-Path $S2Root 'tmp'), (Join-Path $S2Root 'logs'))) {
  New-Item -ItemType Directory -Force $d | Out-Null
}
Write-Host "  [1] 目录就绪(继承 memory 强 ACL)OK"

# ============================ 2 · qdrant.exe + nssm.exe ============================
function Find-Zip([string]$Given,[string]$Pattern) {
  if ($Given -and (Test-Path $Given)) { return $Given }
  $dl = Join-Path $env:USERPROFILE 'Downloads'
  return (Get-ChildItem $dl -Filter $Pattern -EA SilentlyContinue | Select-Object -First 1).FullName
}
if (Test-Path $QExe) { Write-Host "  [2a] qdrant.exe 已就位 OK" }
else {
  $z = Find-Zip $QdrantZip 'qdrant-*windows-msvc*.zip'
  if (-not $z) { Write-Host "  X 缺 qdrant zip。下:https://github.com/qdrant/qdrant/releases/download/$QVER/qdrant-x86_64-pc-windows-msvc.zip → 放 Downloads,重跑。" -ForegroundColor Red; exit 1 }
  $ext = Join-Path $QRoot '_qx'; if (Test-Path $ext) { Remove-Item $ext -Recurse -Force }
  Expand-Archive $z $ext -Force
  $found = Get-ChildItem $ext -Recurse -Filter 'qdrant.exe' | Select-Object -First 1
  if (-not $found) { throw "zip 里没 qdrant.exe" }
  Copy-Item $found.FullName $QExe -Force
  Remove-Item $ext -Recurse -Force
  Write-Host ("  [2a] qdrant.exe 解出({0})OK" -f (Split-Path $z -Leaf))
}
if (Test-Path $Nssm) { Write-Host "  [2b] nssm.exe 已就位 OK" }
else {
  $z = Find-Zip $NssmZip 'nssm-*.zip'
  if (-not $z) { Write-Host "  X 缺 nssm zip。下:https://nssm.cc/release/nssm-2.24.zip → 放 Downloads,重跑。" -ForegroundColor Red; exit 1 }
  $ext = Join-Path $QRoot '_nx'; if (Test-Path $ext) { Remove-Item $ext -Recurse -Force }
  Expand-Archive $z $ext -Force
  $found = Get-ChildItem $ext -Recurse -Filter 'nssm.exe' | Where-Object { $_.FullName -match 'win64' } | Select-Object -First 1
  if (-not $found) { $found = Get-ChildItem $ext -Recurse -Filter 'nssm.exe' | Select-Object -First 1 }
  if (-not $found) { throw "zip 里没 nssm.exe" }
  Copy-Item $found.FullName $Nssm -Force
  Remove-Item $ext -Recurse -Force
  Write-Host "  [2b] nssm.exe 解出 OK"
}

# ============================ 3 · config.yaml ×2 ============================
function Write-QdrantConfig([string]$CfgPath,[string]$Storage,[string]$Snap,[string]$Tmp,[int]$Http,[int]$Grpc,[string]$ApiKey) {
  $sp = $Storage -replace '\\','/'; $np = $Snap -replace '\\','/'; $tp = $Tmp -replace '\\','/'
  $yaml = @"
# LocalAI Hub — Qdrant(D30)。★ config.yaml 含明文 api_key(S2),靠 NTFS 强 ACL 保护,勿提交仓库。
service:
  host: 127.0.0.1          # 只听回环(默认 0.0.0.0 会暴露局域网)· 同时约束 http/grpc
  http_port: $Http
  grpc_port: $Grpc
  api_key: $ApiKey         # 无 SSPI 等价物,只能 bearer;仅 ai-mem 进程/网关持有
  enable_tls: false        # loopback-only,不跨主机,不需 TLS
storage:
  storage_path: $sp        # ★ 活库 · 绝不文件级复制(§8.5.5)
  snapshots_path: $np
  temp_path: $tp
telemetry_disabled: true   # 离线/隐私
log_level: INFO
"@
  Write-NoBom $CfgPath $yaml
}
$apiMain = New-Secret 32; $apiS2 = New-Secret 32
$cfgMain = Get-Path 'qdrant_config'; $cfgS2 = Get-Path 'qdrant_s2_config'
Write-QdrantConfig $cfgMain (Get-Path 'qdrant_storage') (Get-Path 'qdrant_snapshots') (Join-Path $QRoot 'tmp') ([int](Get-Path 'qdrant_http_port')) ([int](Get-Path 'qdrant_grpc_port')) $apiMain
Write-QdrantConfig $cfgS2  (Get-Path 'qdrant_s2_storage') (Get-Path 'qdrant_s2_snapshots') (Join-Path $S2Root 'tmp') ([int](Get-Path 'qdrant_s2_http_port')) ([int](Get-Path 'qdrant_s2_grpc_port')) $apiS2
Write-Host "  [3] 两份 config.yaml 已写(host 127.0.0.1 · 各自 api_key · telemetry off)OK"

# ============================ 4 · 重置 ai-mem 密码 + 同步已有服务 ============================
$pw = New-SafePassword
Set-LocalUser -Name 'ai-mem' -Password (ConvertTo-SecureString $pw -AsPlainText -Force)
Grant-UserRight 'ai-mem' 'SeServiceLogonRight' $QRoot
# ★ 同步所有已存在的 ai-mem 服务(如 pg-mem),否则它们下次启动 1069
$existing = Get-WmiObject Win32_Service | Where-Object { $_.StartName -eq '.\ai-mem' -and $_.Name -notin @('Qdrant','Qdrant-s2') }
foreach ($e in $existing) { Set-SvcAccount $e.Name $pw; Restart-Service $e.Name -Force -EA SilentlyContinue }
Write-Host ("  [4] ai-mem 密码已重置;同步已有服务:{0} OK" -f (($existing.Name) -join ', '))

# ============================ 5 · 注册两个 Qdrant 服务(NSSM)============================
function Install-QdrantSvc([string]$Name,[string]$Cfg,[string]$AppDir,[string]$Pw) {
  if (Get-Service $Name -EA SilentlyContinue) { Stop-Service $Name -Force -EA SilentlyContinue; Start-Sleep 1; & $Nssm remove $Name confirm | Out-Null; Start-Sleep 1 }
  & $Nssm install $Name $QExe | Out-Null
  & $Nssm set $Name AppParameters "--config-path $Cfg" | Out-Null    # 显式绝对路径(治 issue #5964 CWD 坑)
  & $Nssm set $Name AppDirectory $AppDir | Out-Null
  & $Nssm set $Name AppStdout (Join-Path $AppDir 'logs\qdrant.out.log') | Out-Null
  & $Nssm set $Name AppStderr (Join-Path $AppDir 'logs\qdrant.err.log') | Out-Null
  & $Nssm set $Name AppRotateFiles 1 | Out-Null
  & $Nssm set $Name Start SERVICE_AUTO_START | Out-Null
  & $Nssm set $Name AppExit Default Restart | Out-Null
  Set-SvcAccount $Name $Pw            # 账户经 WMI(不进命令行)
}
Install-QdrantSvc 'Qdrant'    $cfgMain $QRoot  $pw
Install-QdrantSvc 'Qdrant-s2' $cfgS2   $S2Root $pw
Start-Service Qdrant; Start-Service Qdrant-s2
$pw = $null; [System.GC]::Collect()
function Wait-Qdrant([int]$Port,[string]$Name,[string]$LogDir) {
  for ($i=0; $i -lt 20; $i++) { Start-Sleep 1
    try { if ((Invoke-WebRequest "http://127.0.0.1:$Port/healthz" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { return $true } } catch {}
  }
  Write-Host "  X $Name 未就绪。err 日志尾:" -ForegroundColor Red
  Get-Content (Join-Path $LogDir 'logs\qdrant.err.log') -Tail 15 -EA SilentlyContinue | ForEach-Object { "    $_" }
  return $false
}
if (-not (Wait-Qdrant ([int](Get-Path 'qdrant_http_port'))    'Qdrant'    $QRoot))  { exit 1 }
if (-not (Wait-Qdrant ([int](Get-Path 'qdrant_s2_http_port')) 'Qdrant-s2' $S2Root)) { exit 1 }
Write-Host "  [5] Qdrant / Qdrant-s2 服务已注册并就绪 OK"

# ============================ 6 · 建 collection(1024 维 Cosine)============================
function Ensure-Collection([int]$Port,[string]$Key,[string]$Coll) {
  $h = @{ 'api-key' = $Key }
  try { Invoke-RestMethod "http://127.0.0.1:$Port/collections/$Coll" -Headers $h -EA Stop | Out-Null; return 'exists' }
  catch {
    $body = '{"vectors":{"size":1024,"distance":"Cosine"}}'
    Invoke-RestMethod "http://127.0.0.1:$Port/collections/$Coll" -Method Put -Headers $h -ContentType 'application/json' -Body $body | Out-Null
    return 'created'
  }
}
$rMain = Ensure-Collection ([int](Get-Path 'qdrant_http_port'))    $apiMain 'mem_main'
$rS2   = Ensure-Collection ([int](Get-Path 'qdrant_s2_http_port')) $apiS2   'mem_s2'
Write-Host ("  [6] collection mem_main={0} · mem_s2={1}(1024 维 Cosine)OK" -f $rMain,$rS2)

# ============================ 7 · 核验 ============================
Write-Host ""
Write-Host "=== 核验 ===" -ForegroundColor Cyan
foreach ($n in 'Qdrant','Qdrant-s2') {
  $w = Get-WmiObject Win32_Service -Filter "Name='$n'"
  Write-Host ("  {0}: {1} / 账户 {2}" -f $n, (Get-Service $n).Status, $w.StartName)
}
Write-Host "  监听(应仅 127.0.0.1 的 6333/6335):"
(netstat -ano | Select-String ':633[35]\s') | ForEach-Object { "    $_" }
Write-Host "  ★ 鉴权:不带 api-key 应被拒 —"
try { Invoke-RestMethod "http://127.0.0.1:$((Get-Path 'qdrant_http_port'))/collections" -EA Stop | Out-Null; Write-Host "    ⚠ 无 key 居然通过?!" -ForegroundColor Yellow }
catch { Write-Host ("    ✓ 无 key 被拒(HTTP {0})" -f [int]$_.Exception.Response.StatusCode.value__) -ForegroundColor Green }
Write-Host ""
Write-Host "=== 完成 ===" -ForegroundColor Green
Write-Host "  回去跟 Claude 说「Qdrant 装好了」,它会只读核验(回环、账户、鉴权、collection)。"
Write-Host "  ★ api_key 在两份 config.yaml 里(强 ACL);ai-mem 密码轮换须同步 pg-mem/Qdrant/Qdrant-s2 三服务。"
