# =============================================================================
#  install-postgres.ps1 — P2 记忆库:PostgreSQL 18 装到 ai-mem 下(SSPI)· D30
#
#  ★ 在【你自己的管理员 PowerShell】里跑,不要贴进 Claude 的工具里执行。
#    (错误回显可能带出密码/密钥进 LLM 上下文,违 D23。)
#
#  设计见 00-docs/memory-backbone-design.md。要点:
#   · SSPI 认证:PG 连接绑到 Windows 账户 SID(ai-mem→mem_rw / ai-mem→postgres)。
#     → postgres 与 mem_rw 都【没有 DB 口令】。ai-asset 没有「口令」可拿,其 SID 也
#       映射不到这两个角色,连都连不上。彻底不在磁盘存 DB 明文口令。
#   · 只听 127.0.0.1(IPv4-only)· UTF8 · 数据在 {state}/memory 强 ACL 内。
#   · ai-mem 账户密码:脚本内随机重置一次,经 WMI(非命令行,避免 Win32_Process 泄露)
#     配进服务 LSA;人不需记、不落盘、不进 paths.toml。
#
#  幂等:分阶段,已完成的阶段跳过,可重复运行。任一阶段失败即停(fail-fast)。
#  前置:1) 已跑过 setup-accounts.ps1  2) 已下载 PG18 官方 windows-x64 *binaries* ZIP
# =============================================================================
param([string]$ZipPath)
$ErrorActionPreference = 'Stop'
try { Set-PSReadLineOption -HistorySaveStyle SaveNothing -ErrorAction SilentlyContinue } catch {}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host "  X 需管理员。右键 PowerShell -> 以管理员身份运行。" -ForegroundColor Red; exit 1 }

# ---- 从 paths.toml 读路径(§11.1)----
$PathsToml = Join-Path $PSScriptRoot '..\config\paths.toml'
function Get-Path([string]$Key) {
  $m = Select-String -Path $PathsToml -Pattern ("^\s*{0}\s*=\s*'([^']+)'" -f [regex]::Escape($Key)) | Select-Object -First 1
  if (-not $m) { throw "paths.toml 缺键: $Key" }
  return $m.Matches[0].Groups[1].Value
}
$PgBin = Get-Path 'pg_bin'; $PgData = Get-Path 'pg_data'; $PgPort = Get-Path 'pg_port'
$PgRoot = Split-Path $PgBin -Parent            # ...\pg\18(在 memory 强 ACL 内 -> ai-mem 可读写)
$Machine = $env:COMPUTERNAME
Write-Host "=== PostgreSQL 18 -> ai-mem(SSPI)===" -ForegroundColor Cyan
Write-Host ("  bin={0}`n  data={1}`n  port={2}" -f $PgBin,$PgData,$PgPort)

# ---- 工具 ----
function Write-NoBom([string]$Path,[string]$Text) {
  [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}
function Grant-UserRight([string]$Account,[string]$Right) {
  $sid = (New-Object System.Security.Principal.NTAccount($Account)).Translate([System.Security.Principal.SecurityIdentifier]).Value
  $inf = Join-Path $PgRoot "sr_$Right.inf"; $sdb = Join-Path $PgRoot "sr.sdb"   # 避开 %TEMP% 的 ~ 短名路径
  secedit /export /cfg $inf /areas USER_RIGHTS | Out-Null
  $c = Get-Content $inf
  $line = ($c | Where-Object { $_ -match "^$Right\s*=" } | Select-Object -First 1)
  if ($line) {
    if ($line -match [regex]::Escape($sid)) { Remove-Item $inf,$sdb -Force -EA SilentlyContinue; return }
    $c = $c -replace [regex]::Escape($line), ($line + ",*$sid")
  } else { $c = $c -replace '(\[Privilege Rights\])', ("`$1`r`n$Right = *$sid") }
  Set-Content $inf $c -Encoding Unicode
  secedit /import /db $sdb /cfg $inf /areas USER_RIGHTS | Out-Null
  secedit /configure /db $sdb /areas USER_RIGHTS | Out-Null
  Remove-Item $inf,$sdb -Force -EA SilentlyContinue
}
function Invoke-AsAiMem([string]$Body,[string]$Password) {
  # 以 ai-mem 跑一段批处理(批处理登录,需 SeBatchLogonRight)。返回退出码。
  # ★ 批处理文件放 $PgRoot(memory 强 ACL 内),否则 ai-mem 读不到 admin 的 %TEMP%。
  $cmdFile = Join-Path $PgRoot '_aimem_task.cmd'
  Write-NoBom $cmdFile ("@echo off`r`n" + $Body + "`r`n")
  $tn = 'localai-pg-oneshot'
  $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument ('/c "' + $cmdFile + '"')
  Register-ScheduledTask -TaskName $tn -Action $action -User "$Machine\ai-mem" -Password $Password -RunLevel Limited -Force | Out-Null
  Start-ScheduledTask -TaskName $tn
  # ★ 先等它真的进入 Running(见 apply-schema.ps1 同处注释):否则与状态轮询抢跑,
  #   会拿到上次的 LastTaskResult 并把仍在跑的任务拆掉。
  for ($i = 0; $i -lt 30; $i++) {
    if ((Get-ScheduledTask -TaskName $tn).State -eq 'Running') { break }
    Start-Sleep -Milliseconds 300
  }
  while ((Get-ScheduledTask -TaskName $tn).State -eq 'Running') { Start-Sleep -Seconds 1 }
  $rc = (Get-ScheduledTaskInfo -TaskName $tn).LastTaskResult
  Unregister-ScheduledTask -TaskName $tn -Confirm:$false
  Remove-Item $cmdFile -Force -EA SilentlyContinue
  return $rc
}
function New-SafePassword {
  $bytes = New-Object byte[] 30
  [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
  return (([Convert]::ToBase64String($bytes) -replace '[+/=]','').Substring(0,24)) + '#Aa7'
}

# ============================ 1 · 前置 VC++ ============================
$vc = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64' -EA SilentlyContinue
if (-not ($vc -and $vc.Installed -eq 1)) {
  Write-Host "  X 缺 Microsoft Visual C++ 2015-2022 x64 Redistributable(binaries ZIP 不自带)。" -ForegroundColor Red
  Write-Host "    装好再重跑:https://aka.ms/vs/17/release/vc_redist.x64.exe"
  exit 1
}
Write-Host "  [1] VC++ x64 Redistributable OK"

# ============================ 2 · 解压二进制 ============================
if (Test-Path (Join-Path $PgBin 'initdb.exe')) {
  Write-Host "  [2] 二进制已就位,跳过解压 OK"
} else {
  if (-not $ZipPath) {
    $dl = Join-Path $env:USERPROFILE 'Downloads'
    $ZipPath = (Get-ChildItem $dl -Filter 'postgresql-18*binaries*.zip' -EA SilentlyContinue | Select-Object -First 1).FullName
  }
  if (-not $ZipPath -or -not (Test-Path $ZipPath)) {
    Write-Host "  X 没找到 PG18 binaries ZIP。" -ForegroundColor Red
    Write-Host "    去 https://www.enterprisedb.com/download-postgresql-binaries 下 [Win x86-64] 的 18.x ZIP"
    Write-Host "    (是 *binaries* 压缩包,不是 .exe 图形安装器),放进 Downloads 或 -ZipPath 指定,再重跑。"
    exit 1
  }
  Write-Host ("  [2] 解压 {0}" -f (Split-Path $ZipPath -Leaf))
  New-Item -ItemType Directory -Force $PgRoot | Out-Null
  $ext = Join-Path $PgRoot '_extract'    # 在 D: 目标下解压,避开 %TEMP% 的 8.3 短名(~)路径
  if (Test-Path $ext) { Remove-Item $ext -Recurse -Force }
  Expand-Archive -Path $ZipPath -DestinationPath $ext -Force
  $pgsql = Get-ChildItem $ext -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'bin\initdb.exe') } | Select-Object -First 1
  if (-not $pgsql) { throw "ZIP 里没找到 bin\initdb.exe,可能下错了包(需 binaries ZIP)" }
  Copy-Item (Join-Path $pgsql.FullName '*') $PgRoot -Recurse -Force
  if (Test-Path $ext) { Remove-Item $ext -Recurse -Force }
  if (-not (Test-Path (Join-Path $PgBin 'initdb.exe'))) { throw "解压后仍无 $PgBin\initdb.exe" }
  Write-Host "      -> $PgBin OK"
}

# ============================ 3 · 账户密码 + 权限 ============================
$pw = New-SafePassword
Set-LocalUser -Name 'ai-mem' -Password (ConvertTo-SecureString $pw -AsPlainText -Force)
Grant-UserRight 'ai-mem' 'SeServiceLogonRight'   # 服务登录(缺 -> 1069)
Grant-UserRight 'ai-mem' 'SeBatchLogonRight'     # 计划任务批处理登录
# ★★ 重跑安全:重置 ai-mem 密码会作废【所有】以它运行的服务的存储凭据(不止 pg-mem)。
#    早期版本只在下方同步 pg-mem,重跑就把 Qdrant / Qdrant-s2 / Embedding 全部打成 1069。
#    此处统一同步所有 StartName 指向 ai-mem 的服务(不用字符串字面量匹配,按 SID 判定)。
$aiMemSid = (New-Object System.Security.Principal.NTAccount('ai-mem')).Translate(
              [System.Security.Principal.SecurityIdentifier]).Value
foreach ($s in (Get-WmiObject Win32_Service)) {
  if (-not $s.StartName) { continue }
  try {
    $sid = (New-Object System.Security.Principal.NTAccount($s.StartName.TrimStart('.','\'))
           ).Translate([System.Security.Principal.SecurityIdentifier]).Value
  } catch { continue }
  if ($sid -ne $aiMemSid) { continue }
  $r = $s.Change($null,$null,$null,$null,$null,$null,".\ai-mem",$pw,$null,$null,$null)
  if ($r.ReturnValue -ne 0) { Write-Host "  X 同步服务 $($s.Name) 凭据失败 RV=$($r.ReturnValue)" -ForegroundColor Red; exit 1 }
  Write-Host "      (已同步既有 ai-mem 服务: $($s.Name))"
}
Write-Host "  [3] ai-mem 密码重置 + SeServiceLogonRight/SeBatchLogonRight + 同步既有服务 OK"

# ============================ 4 · initdb(以 ai-mem)============================
if (Test-Path (Join-Path $PgData 'PG_VERSION')) {
  Write-Host "  [4] 集群已存在,跳过 initdb OK"
} else {
  New-Item -ItemType Directory -Force $PgData | Out-Null
  $acl = Get-Acl $PgData
  $hasMem = $acl.Access | Where-Object { $_.IdentityReference -like "*ai-mem" -and $_.AccessControlType -eq 'Allow' -and $_.FileSystemRights -match 'FullControl' }
  if (-not $hasMem) { & icacls $PgData /grant "ai-mem:(OI)(CI)F" | Out-Null; Write-Host "      (补授 ai-mem 直接 FullControl)" }
  $initLog = Join-Path $PgRoot 'initdb.log'
  $initdb = Join-Path $PgBin 'initdb.exe'
  # 无 --pwfile:postgres 无口令,靠 SSPI;pg_hba 随后覆盖为 sspi,服务此时未启动,initdb 生成的 trust 从不服务任何连接
  $body = ('"{0}" -D "{1}" -U postgres --encoding=UTF8 --locale=C --data-checksums > "{2}" 2>&1' -f $initdb,$PgData,$initLog)
  Write-Host "  [4] 以 ai-mem 跑 initdb(UTF8 · locale=C · checksums)…"
  $rc = Invoke-AsAiMem $body $pw
  if (-not (Test-Path (Join-Path $PgData 'PG_VERSION'))) {
    Write-Host "      X initdb 失败(rc=$rc)。日志:" -ForegroundColor Red
    if (Test-Path $initLog) { Get-Content $initLog -Tail 20 | ForEach-Object { "        $_" } }
    exit 1
  }
  Write-Host "      -> 集群已建 OK"
}

# ============================ 5 · 配置文件(无 BOM)============================
$conf = Join-Path $PgData 'postgresql.conf'
$hba  = Join-Path $PgData 'pg_hba.conf'
$ident= Join-Path $PgData 'pg_ident.conf'
$marker='# --- LocalAI Hub ---'; $endm='# --- end LocalAI Hub ---'
$confText = (Get-Content $conf -Raw) -replace ("(?ms)" + [regex]::Escape($marker) + ".*?" + [regex]::Escape($endm) + "\r?\n?"), ''
$confAdd = @"
$marker
listen_addresses = '127.0.0.1'
port = $PgPort
password_encryption = scram-sha-256
logging_collector = on
log_directory = 'log'
# ★★ 承重的默认值,必须显式钉死(2026-07-28 实测)
#
# PostgreSQL 在 ERROR 时会把【失败的语句】写进服务器日志。对参数化查询,
# 参数是否一并记录由 log_parameter_max_length_on_error 决定:
#   0(默认)= 出错时不记录绑定参数  ← 生产路径(repo.py 全用 %s)因此安全
#   >0     = 记录参数(截断到 N 字节)← 会把记忆正文写进日志,违反 §9.2
#
# 默认值会随版本变,而这一条是承重的:repo._sanitize 只净化 Python 侧的异常传播,
# 对 PG 自己已经写在磁盘上的那一行无能为力。故显式写死,并由 verify.sql 持续断言。
#
# ★ 剩余暴露面(配置挡不住,只能靠纪律):**手写字面量的 SQL**
#   —— psql / 迁移脚本 / 临时排查里 `SET statement='正文'` 这种写法,
#   正文是语句文本的一部分,一定会进日志。实测确认。
log_parameter_max_length_on_error = 0
log_filename = 'pg-%Y-%m-%d.log'
$endm
"@
Write-NoBom $conf ($confText.TrimEnd() + "`r`n" + $confAdd + "`r`n")
# ★ 重跑安全:pg_hba / pg_ident 只在【首次】写入基线。无条件覆写会抹掉 apply-schema.ps1
#   追加的 ai_mem_local / ai_mem_remote 映射 —— 重跑一次 PG 安装,记忆库的两个角色就连不上了。
if (Select-String -Path $hba -Pattern 'LocalAI Hub' -Quiet -EA SilentlyContinue) {
  Write-Host "  [5] pg_hba/pg_ident 已是本项目基线,保留现状(含 schema 追加的角色映射)"
} else {
  Write-NoBom $hba @"
# LocalAI Hub — 仅本机回环 · SSPI 绑 Windows SID(D30 · §6.8)
# ★ 本文件由 install-postgres.ps1 建立基线;apply-schema.ps1 会在末尾【追加】
#   ai_mem_local / ai_mem_remote 两行。重跑安装不会覆写本文件(见脚本 [5] 的守卫)。
# TYPE  DATABASE  USER      ADDRESS         METHOD
host    all       postgres  127.0.0.1/32    sspi  map=mem  include_realm=0
host    memory    mem_rw    127.0.0.1/32    sspi  map=mem  include_realm=0
"@
  Write-NoBom $ident @"
# MAPNAME  SYSTEM-USERNAME  PG-USERNAME
# ★ apply-schema.ps1 会追加 ai_mem_local / ai_mem_remote 两行,勿手工覆写本文件。
mem        ai-mem           postgres
mem        ai-mem           mem_rw
"@
  Write-Host "  [5] postgresql.conf / pg_hba.conf(sspi) / pg_ident.conf 已写(无 BOM)OK"
}

# ============================ 6 · 注册并启动服务 ============================
if (-not (Get-Service -Name 'pg-mem' -EA SilentlyContinue)) {
  & (Join-Path $PgBin 'pg_ctl.exe') register -N 'pg-mem' -D $PgData -S auto | Out-Null  # 先 LocalSystem(命令行无密码)
  Start-Sleep 2
}
$wsvc = Get-WmiObject Win32_Service -Filter "Name='pg-mem'"     # 经 WMI 设 .\ai-mem+密码(不进命令行)
$chg = $wsvc.Change($null,$null,$null,$null,$null,$null,".\ai-mem",$pw,$null,$null,$null)
if ($chg.ReturnValue -ne 0) { throw "设服务账户失败 ReturnValue=$($chg.ReturnValue)" }
Stop-Service pg-mem -Force -EA SilentlyContinue; Start-Sleep 1
Start-Service pg-mem -EA SilentlyContinue; Start-Sleep 3
if ((Get-Service pg-mem).Status -ne 'Running') {
  Write-Host "  X pg-mem 未启动。多半 1069(密码/服务登录权)或数据目录 ACL。看事件查看器 + $PgRoot\initdb.log" -ForegroundColor Red
  exit 1
}
Write-Host "  [6] 服务 pg-mem 以 .\ai-mem 运行中 OK"

# ============================ 7 · 建角色 + 库(以 ai-mem 经 SSPI)============================
# ★ CREATE DATABASE 不能在事务块里 → 必须单独一条 -c(不能与 CREATE ROLE 挤同一个 -c)。幂等。
$psql = Join-Path $PgBin 'psql.exe'
$qFile = Join-Path $PgRoot 'q.txt'
$bootLog = Join-Path $PgRoot 'bootstrap.log'
function PgHas([string]$db,[string]$sql) {
  Invoke-AsAiMem ('"{0}" -h 127.0.0.1 -p {1} -U postgres -d {2} -tAc "{3}" > "{4}" 2>&1' -f $psql,$PgPort,$db,$sql,$qFile) $pw | Out-Null
  return (((Get-Content $qFile -EA SilentlyContinue) -join '') -match '1')
}
function PgRun([string]$sql) {
  return (Invoke-AsAiMem ('"{0}" -h 127.0.0.1 -p {1} -U postgres -d postgres -v ON_ERROR_STOP=1 -c "{2}" > "{3}" 2>&1' -f $psql,$PgPort,$sql,$bootLog) $pw)
}
if (PgHas 'postgres' "SELECT 1 FROM pg_database WHERE datname='memory'") {
  Write-Host "  [7] memory 库已存在,跳过 OK"
} else {
  if (-not (PgHas 'postgres' "SELECT 1 FROM pg_roles WHERE rolname='mem_rw'")) {
    PgRun "CREATE ROLE mem_rw LOGIN" | Out-Null      # 角色不存在才建
  }
  PgRun "CREATE DATABASE memory OWNER mem_rw ENCODING 'UTF8' LC_COLLATE 'C' LC_CTYPE 'C' TEMPLATE template0" | Out-Null  # 单独一条,不在事务块
  if (-not (PgHas 'postgres' "SELECT 1 FROM pg_database WHERE datname='memory'")) {
    Write-Host "  X 建库失败。日志:" -ForegroundColor Red
    if (Test-Path $bootLog) { Get-Content $bootLog -Tail 20 | ForEach-Object { "        $_" } }
    exit 1
  }
  Write-Host "  [7] 角色 mem_rw + 库 memory(UTF8 · 无 DB 口令 · SSPI)已建 OK"
}

# ============================ 8 · 核验 + 收尾 ============================
$encFile = Join-Path $PgRoot 'enc.txt'
Invoke-AsAiMem ('"{0}" -h 127.0.0.1 -p {1} -U postgres -d memory -tAc "SHOW server_encoding" > "{2}" 2>&1' -f $psql,$PgPort,$encFile) $pw | Out-Null
$enc = (Get-Content $encFile -EA SilentlyContinue | Where-Object { $_ -match '\S' } | Select-Object -First 1)
Remove-Item $qFile,$encFile -Force -EA SilentlyContinue
$pw = $null; [System.GC]::Collect()   # 明文密码只活在 LSA 里了

Write-Host ""
Write-Host "=== 核验 ===" -ForegroundColor Cyan
Write-Host ("  server_encoding(应 UTF8):{0}" -f $enc)
if ($enc -and $enc.Trim() -ne 'UTF8') {
  Write-Host "  XX 编码不是 UTF8!中文会不可逆损坏 -> 必须删 memory 库重建,不要继续。" -ForegroundColor Red
}
Write-Host ("  服务:{0} / 账户 {1}" -f (Get-Service pg-mem).Status, (Get-WmiObject Win32_Service -Filter "Name='pg-mem'").StartName)
Write-Host "  监听(应仅 127.0.0.1:$PgPort):"
(netstat -ano | Select-String ":$PgPort\s") | ForEach-Object { "    $_" }
Write-Host "  数据目录 ACL(ai-asset/ai-exec 应仍 Deny):"
(& icacls $PgData) | Select-String 'ai-asset|ai-exec' | ForEach-Object { "    $_" }
Write-Host ""
Write-Host "=== 完成 ===" -ForegroundColor Green
Write-Host "  回去跟 Claude 说「PG 装好了」并把上面核验贴回去。"
Write-Host "  ★ 日后若轮换 ai-mem 账户密码,必须同步进服务(WMI/services.msc),否则 pg-mem 启动 1069。"
