# =============================================================================
#  install-embedding.ps1 — P2 embedding/rerank 服务(CPU · :18084)· §4.2
#
#  两阶段:
#   -DownloadOnly (今晚,普通终端即可,不需管理员):建 venv + 装 torch/FlagEmbedding
#     + 下载 bge-m3/bge-reranker(约 2.3GB)+ 自测。★ 可无人值守跑一夜,失败也不碰任何服务/密码。
#   不带参数 (明天,管理员):在 -DownloadOnly 完成后,把服务注册成 ai-mem 的 NSSM 服务。
#
#  幂等。日志全程写文件,失败可事后看。
# =============================================================================
param([switch]$DownloadOnly)
# ★ 不能用 'Stop':native 命令(python/pip)往 stderr 写【警告】(如 HF「无 token」提示)时,
#   PS5.1 会把它包成 NativeCommandError 致命错误、即使 exit code=0 也中断脚本。
#   故用 'Continue',真失败一律靠显式 $LASTEXITCODE / Test-Path 检查(下方各步都有)。
$ErrorActionPreference = 'Continue'
try { Set-PSReadLineOption -HistorySaveStyle SaveNothing -ErrorAction SilentlyContinue } catch {}

$PathsToml = Join-Path $PSScriptRoot '..\config\paths.toml'
function Get-Path([string]$Key) {
  $m = Select-String -Path $PathsToml -Pattern ("^\s*{0}\s*=\s*'([^']+)'" -f [regex]::Escape($Key)) | Select-Object -First 1
  if (-not $m) { throw "paths.toml 缺键: $Key" }
  return $m.Matches[0].Groups[1].Value
}
$HfHome  = Get-Path 'HF_HOME'                              # [env] HF_HOME
$LogDir  = Get-Path 'logs'                                 # [state] logs
$SvcDir  = (Resolve-Path (Join-Path $PSScriptRoot '..\10-core\memory')).Path
$AiRoot  = Split-Path (Get-Path 'models') -Parent          # 从 models 根导出 AI 根,不硬编码
$Venv    = Join-Path $AiRoot 'venvs\embedding'
$VPy     = "$Venv\Scripts\python.exe"
$Log     = "$LogDir\embedding-install.log"
New-Item -ItemType Directory -Force $LogDir | Out-Null
function Say($m){ $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m; Write-Host $line; Add-Content $Log $line }

$env:HF_HOME = $HfHome
New-Item -ItemType Directory -Force $HfHome | Out-Null

Say "=== embedding 安装开始 (DownloadOnly=$DownloadOnly) ==="
Say "  venv=$Venv  HF_HOME=$HfHome  svc=$SvcDir"

# ---------- 阶段 1:Python 3.12 + venv ----------
if (-not (Test-Path $VPy)) {
  Say "[1] 建 venv(需要 Python 3.12)…"
  $py = $null
  foreach ($cand in @('py -3.12','python3.12','python')) {
    try { $v = & cmd /c "$cand --version 2>&1"; if ($v -match '3\.1[2-9]') { $py = $cand; break } } catch {}
  }
  if (-not $py) { Say "  X 找不到 Python 3.12。装好(python.org)再跑。"; exit 1 }
  Say "  用 $py ($v)"
  & cmd /c "$py -m venv `"$Venv`"" 2>&1 | Tee-Object -FilePath $Log -Append | Out-Null
  if (-not (Test-Path $VPy)) { Say "  X venv 创建失败"; exit 1 }
} else { Say "[1] venv 已存在,跳过" }

# ---------- 阶段 2:依赖(torch CPU + FlagEmbedding)----------
Say "[2] 装依赖(torch CPU + FlagEmbedding + fastapi/uvicorn)… 这一步会下载数百 MB,耐心"
& $VPy -m pip install -q --upgrade pip 2>&1 | Tee-Object -FilePath $Log -Append | Out-Null
& $VPy -m pip install -q torch --index-url https://download.pytorch.org/whl/cpu 2>&1 | Tee-Object -FilePath $Log -Append | Out-Null
if ($LASTEXITCODE -ne 0) { Say "  X torch 安装失败,见日志尾"; Get-Content $Log -Tail 15; exit 1 }
& $VPy -m pip install -q "FlagEmbedding" fastapi uvicorn pydantic 2>&1 | Tee-Object -FilePath $Log -Append | Out-Null
if ($LASTEXITCODE -ne 0) { Say "  X FlagEmbedding 安装失败,见日志尾"; Get-Content $Log -Tail 15; exit 1 }
Say "  依赖装好"

# ---------- 阶段 3:下载模型(约 2.3GB → HF_HOME)----------
Say "[3] 下载 bge-m3 + bge-reranker-v2-m3(约 2.3GB → $HfHome)… ★ 最慢的一步,可放着睡"
$dl = @"
from FlagEmbedding import BGEM3FlagModel, FlagReranker
print('downloading bge-m3 ...', flush=True)
BGEM3FlagModel('BAAI/bge-m3', use_fp16=False)
print('downloading bge-reranker-v2-m3 ...', flush=True)
FlagReranker('BAAI/bge-reranker-v2-m3', use_fp16=False)
print('models ready', flush=True)
"@
$dl | & $VPy - 2>&1 | Tee-Object -FilePath $Log -Append
if ($LASTEXITCODE -ne 0) { Say "  X 模型下载失败,见日志尾"; Get-Content $Log -Tail 15; exit 1 }
Say "  模型就位"

# ---------- 阶段 4:自测 ----------
Say "[4] 自测(embedding 维度应 1024,rerank 第一个分更高)…"
Push-Location $SvcDir
& $VPy embedding_service.py --selftest 2>&1 | Tee-Object -FilePath $Log -Append
$stok = ($LASTEXITCODE -eq 0)
Pop-Location
if (-not $stok) { Say "  X 自测失败,见日志尾"; Get-Content $Log -Tail 15; exit 1 }
Say "  自测通过"

if ($DownloadOnly) {
  Say ""
  Say "=== 下载阶段完成 ✓ 明天以【管理员】跑(不带 -DownloadOnly)注册成 ai-mem 服务。 ==="
  Say "  睡前就到这;明天跟 Claude 说「embedding 下好了」。"
  exit 0
}

# ---------- 阶段 5(管理员):注册成 ai-mem 服务 ----------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Say "  X 阶段 5 需要管理员。以管理员重跑(不带 -DownloadOnly)。"; exit 1 }

$Nssm = Join-Path (Get-Path 'qdrant_bin') 'nssm.exe'   # 复用 Qdrant 装的 nssm
if (-not (Test-Path $Nssm)) { Say "  X 没找到 nssm($Nssm)。先跑 install-qdrant.ps1。"; exit 1 }
$httpPort = 18084

Say "[5] 授 ai-mem 读服务代码 + venv + HF 缓存(D31)…"
foreach ($d in @($SvcDir, $Venv, $HfHome)) {
  & icacls $d /grant "ai-mem:(OI)(CI)(RX)" | Out-Null
  if ($LASTEXITCODE -ne 0) { Say "  X icacls 授权失败: $d"; exit 1 }
}

Say "[6] 重置 ai-mem 密码 + 同步已有 ai-mem 服务(否则它们下次 1069)…"
$b = New-Object byte[] 30; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
$pw = (([Convert]::ToBase64String($b) -replace '[+/=]','').Substring(0,24)) + '#Aa7'
try { Set-LocalUser -Name 'ai-mem' -Password (ConvertTo-SecureString $pw -AsPlainText -Force) -ErrorAction Stop }
catch { Say "  X 重置 ai-mem 密码失败: $_"; exit 1 }
foreach ($s in (Get-WmiObject Win32_Service | Where-Object { $_.StartName -eq '.\ai-mem' })) {
  $r = $s.Change($null,$null,$null,$null,$null,$null,".\ai-mem",$pw,$null,$null,$null)
  if ($r.ReturnValue -ne 0) { Say "  X 同步 $($s.Name) 失败 RV=$($r.ReturnValue)"; exit 1 }
  Restart-Service $s.Name -Force -EA SilentlyContinue
}

Say "[7] 注册 Embedding 服务(NSSM · ai-mem · :$httpPort)…"
if (Get-Service Embedding -EA SilentlyContinue) { Stop-Service Embedding -Force -EA SilentlyContinue; & $Nssm remove Embedding confirm | Out-Null; Start-Sleep 1 }
& $Nssm install Embedding $VPy | Out-Null
& $Nssm set Embedding AppParameters "-m uvicorn embedding_service:app --host 127.0.0.1 --port $httpPort" | Out-Null
& $Nssm set Embedding AppDirectory $SvcDir | Out-Null
& $Nssm set Embedding AppEnvironmentExtra "HF_HOME=$HfHome" | Out-Null
& $Nssm set Embedding AppStdout "$LogDir\embedding.out.log" | Out-Null
& $Nssm set Embedding AppStderr "$LogDir\embedding.err.log" | Out-Null
& $Nssm set Embedding AppRotateFiles 1 | Out-Null
& $Nssm set Embedding Start SERVICE_AUTO_START | Out-Null
$w = Get-WmiObject Win32_Service -Filter "Name='Embedding'"
$chg = $w.Change($null,$null,$null,$null,$null,$null,".\ai-mem",$pw,$null,$null,$null)
if ($chg.ReturnValue -ne 0) { Say "  X 设服务账户失败 RV=$($chg.ReturnValue)"; exit 1 }
Start-Service Embedding
$pw = $null; [System.GC]::Collect()

Say "[8] 等就绪(首次加载模型较慢)…"
$ready = $false
for ($i=0; $i -lt 60; $i++) { Start-Sleep 2
  try { if ((Invoke-WebRequest "http://127.0.0.1:$httpPort/health" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) { $ready=$true; break } } catch {} }
Say ("  就绪: " + $ready)
if (-not $ready) { Say "  X 未就绪,见 $LogDir\embedding.err.log"; exit 1 }
Say "=== 完成 ✓ 跟 Claude 说「embedding 服务起来了」核验(1024 维 + 账户 ai-mem + 回环)。 ==="
