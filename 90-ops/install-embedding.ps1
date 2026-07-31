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
# ★ 必须钉 transformers<5:transformers 5.x 移除了 prepare_for_model,而 FlagEmbedding 1.4.0
#   的 reranker 仍在调它 → rerank 报 "XLMRobertaTokenizer has no attribute prepare_for_model"。
#   (embedding 路径不受影响,所以症状是「维度对但 rerank 崩」。2026-07-28 实测确认。)
& $VPy -m pip install -q "FlagEmbedding" "transformers<5" fastapi uvicorn pydantic 2>&1 | Tee-Object -FilePath $Log -Append | Out-Null
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

Say "[5] 授 ai-mem 读服务代码 + venv + 基础 Python + HF 缓存,并【拒绝资产侧写入】(D31)…"
# ★★ 只 /grant RX 是【收紧不了任何东西】的(2026-07-28 审查):grant 只增不减。
#    这几处都是 Embedding 服务(以 ai-mem 运行)【要加载执行】的内容 —— 代码、venv 里的
#    .pyd/.dll、模型权重。若 ai-asset / ai-exec 能写,它们改一个文件就能在 ai-mem 身份下
#    拿到代码执行 = 一跳打穿 D30/§6.8 的账户隔离。故必须显式 Deny 写。
foreach ($d in @($SvcDir, $Venv)) {
  & icacls $d /grant "ai-mem:(OI)(CI)(RX)" | Out-Null
  if ($LASTEXITCODE -ne 0) { Say "  X icacls 授权失败: $d"; exit 1 }
  & icacls $d /deny "ai-asset:(OI)(CI)(W,D,WDAC,WO)" "ai-exec:(OI)(CI)(W,D,WDAC,WO)" | Out-Null
  if ($LASTEXITCODE -ne 0) { Say "  X icacls 拒绝写入失败: $d"; exit 1 }
  Say "      $d -> ai-mem RX · 资产侧拒写"
}
# ★ HF 缓存需要 Modify 而非 RX:huggingface_hub 加载时要写 .locks;只给 RX 会在
#   服务启动时失败(实测预判)。缓存内容不是机密,给写不违反 D22/D31。
& icacls $HfHome /grant "ai-mem:(OI)(CI)(M)" | Out-Null
if ($LASTEXITCODE -ne 0) { Say "  X icacls 授权失败: $HfHome"; exit 1 }
& icacls $HfHome /deny "ai-asset:(OI)(CI)(W,D,WDAC,WO)" "ai-exec:(OI)(CI)(W,D,WDAC,WO)" | Out-Null
Say "      $HfHome -> ai-mem Modify(需写 .locks)· 资产侧拒写"

# ★★ 基础 Python:venv 里的 python.exe 只是个壳,要去 pyvenv.cfg 指的基础安装找标准库。
#    本机 Python 装在【当前用户的配置文件目录】下,ai-mem 无权读 →
#    服务启动报 "No Python at ..."(2026-07-28 实测)。这影响【所有】以 ai-mem 跑的服务。
#    路径从 pyvenv.cfg 读,不硬编码(§11.1)。只授只读执行:那里只有解释器与标准库,
#    没有你的数据 —— 符合 D31「要隔离的是数据不是代码」。
$cfg = Join-Path $Venv 'pyvenv.cfg'
$homeLine = Select-String -Path $cfg -Pattern '^\s*home\s*=\s*(.+)$' | Select-Object -First 1
if (-not $homeLine) { Say "  X 读不到 $cfg 的 home 项"; exit 1 }
$BasePy = $homeLine.Matches[0].Groups[1].Value.Trim()
if (-not (Test-Path $BasePy)) { Say "  X 基础 Python 不存在: $BasePy"; exit 1 }
& icacls $BasePy /grant "ai-mem:(OI)(CI)(RX)" | Out-Null
if ($LASTEXITCODE -ne 0) { Say "  X 授予 ai-mem 读基础 Python 失败: $BasePy"; exit 1 }
Say "      基础 Python $BasePy -> ai-mem RX(否则服务报 No Python at ...)"

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


# ★ 重注册 localai-memory-dump 计划任务的凭据(2026-07-31 审计):
#   Task Scheduler 把密码【存在自己这里】,不像服务那样跟着 SetPassword 走 ——
#   上面的服务同步循环碰不到它。ai-mem 密码一换,这个备份任务下次登录就 0x8007052E 失败。
#   保留原 Action,只换密码;任务不存在(还没跑过 setup-backup-task)就跳过。
$__dumpTask = 'localai-memory-dump'
if ($__t = Get-ScheduledTask -TaskName $__dumpTask -ErrorAction SilentlyContinue) {
  Register-ScheduledTask -TaskName $__dumpTask -Action $__t.Actions -TaskPath $__t.TaskPath `
    -User "$env:COMPUTERNAME\ai-mem" -Password $pw -RunLevel Limited -Force | Out-Null
  Write-Host "      (已刷新计划任务 $__dumpTask 的 ai-mem 凭据)"
}

Say "[7] 注册 Embedding 服务(NSSM · ai-mem · :$httpPort)…"
# ★ 上一次失败可能把服务留在 Paused/StartPending 等状态,Stop-Service 对这些状态无效 →
#   remove 也会失败 → 新配置写不进去。先无条件 nssm stop 再 remove。
if (Get-Service Embedding -EA SilentlyContinue) {
  & $Nssm stop Embedding 2>&1 | Out-Null
  Start-Sleep 2
  & $Nssm remove Embedding confirm 2>&1 | Out-Null
  Start-Sleep 2
}
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
Start-Service Embedding -EA SilentlyContinue
$pw = $null; [System.GC]::Collect()
# ★ 启动失败就【立刻报错并把 stderr 打出来】,不要傻等一个永远不会就绪的服务。
#   (2026-07-28:Start-Service 已失败,脚本却仍进 [8] 空转 5 分钟,错误线索全被埋掉。)
Start-Sleep 3
$st = (Get-Service Embedding -EA SilentlyContinue).Status
if ($st -ne 'Running') {
  Say "  X 服务未能启动(状态 $st)。stderr 尾:"
  if (Test-Path "$LogDir\embedding.err.log") {
    Get-Content "$LogDir\embedding.err.log" -Tail 15 -Encoding UTF8 | ForEach-Object { Say "      $_" }
  } else { Say "      (无 stderr 日志 —— 进程根本没起来,多半是账户/权限问题)" }
  exit 1
}

Say "[8] 等就绪(首次加载模型较慢,约 10–30 秒)…"
$ready = $false
for ($i=0; $i -lt 60; $i++) {
  Start-Sleep 2
  if ((Get-Service Embedding -EA SilentlyContinue).Status -ne 'Running') { break }  # 中途崩了就别等了
  try { if ((Invoke-WebRequest "http://127.0.0.1:$httpPort/health" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) { $ready=$true; break } } catch {}
}
Say ("  就绪: " + $ready)
if (-not $ready) {
  Say "  X 未就绪。stderr 尾:"
  Get-Content "$LogDir\embedding.err.log" -Tail 20 -Encoding UTF8 -EA SilentlyContinue | ForEach-Object { Say "      $_" }
  exit 1
}
Say "=== 完成 ✓ 跟 Claude 说「embedding 服务起来了」核验(1024 维 + 账户 ai-mem + 回环)。 ==="
