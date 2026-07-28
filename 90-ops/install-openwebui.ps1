# =============================================================================
#  install-openwebui.ps1 — P2 首版界面 Open WebUI(第三方前端)
#
#  两阶段:
#   -DownloadOnly (今晚,普通终端):建 venv + pip install open-webui(大,数 GB)。
#     ★ 可无人值守跑一夜,失败不碰任何服务/密码。
#   不带参数 (明天):配置(指向网关 :8080 · 换端口避冲突 · 数据目录 · 关外部遥测)+ 首启。
#
#  说明:Open WebUI 是【第三方前端】,E1 在网关侧做正因为不信任它。它自己会建首个 admin 账户
#  (本机你用)。它跑在【人类会话】下即可,不必是 ai-mem 服务(它不碰记忆库,只经网关说话)。
#  幂等。全程写日志。
# =============================================================================
param([switch]$DownloadOnly)
$ErrorActionPreference = 'Stop'
try { Set-PSReadLineOption -HistorySaveStyle SaveNothing -ErrorAction SilentlyContinue } catch {}

$PathsToml = Join-Path $PSScriptRoot '..\config\paths.toml'
function Get-Path([string]$Key) {
  $m = Select-String -Path $PathsToml -Pattern ("^\s*{0}\s*=\s*'([^']+)'" -f [regex]::Escape($Key)) | Select-Object -First 1
  if (-not $m) { throw "paths.toml 缺键: $Key" }
  return $m.Matches[0].Groups[1].Value
}
$LogDir = Get-Path 'logs'
$AiRoot = Split-Path (Get-Path 'models') -Parent
$Venv   = Join-Path $AiRoot 'venvs\openwebui'
$VPy    = "$Venv\Scripts\python.exe"
$Log    = "$LogDir\openwebui-install.log"
New-Item -ItemType Directory -Force $LogDir | Out-Null
function Say($m){ $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m; Write-Host $line; Add-Content $Log $line }

Say "=== Open WebUI 安装 (DownloadOnly=$DownloadOnly) ==="
Say "  venv=$Venv"

# ---------- 阶段 1:venv(Open WebUI 需 Python 3.11 或 3.12,不支持 3.13)----------
if (-not (Test-Path $VPy)) {
  Say "[1] 建 venv(需 Python 3.11/3.12,不能 3.13)…"
  $py = $null; $ver = ''
  foreach ($cand in @('py -3.11','py -3.12','python3.11','python3.12','python')) {
    try { $v = & cmd /c "$cand --version 2>&1"; if ($v -match '3\.1[12]\.') { $py = $cand; $ver = $v; break } } catch {}
  }
  if (-not $py) { Say "  X 找不到 Python 3.11/3.12(3.13 不行)。装一个再跑。"; exit 1 }
  Say "  用 $py ($ver)"
  & cmd /c "$py -m venv `"$Venv`"" 2>&1 | Tee-Object -FilePath $Log -Append | Out-Null
  if (-not (Test-Path $VPy)) { Say "  X venv 创建失败"; exit 1 }
} else { Say "[1] venv 已存在,跳过" }

# ---------- 阶段 2:pip install open-webui(大,含前端资源 + 一堆依赖)----------
Say "[2] pip install open-webui … ★ 很大(数 GB),放着睡。"
& $VPy -m pip install -q --upgrade pip 2>&1 | Tee-Object -FilePath $Log -Append | Out-Null
& $VPy -m pip install -q open-webui 2>&1 | Tee-Object -FilePath $Log -Append
if ($LASTEXITCODE -ne 0) { Say "  X 安装失败,见日志尾:"; Get-Content $Log -Tail 20; Say "  (若是构建依赖报错,明天我们评估改 Docker 方式。)"; exit 1 }

# ---------- 阶段 3:确认可导入 ----------
$show = & $VPy -m pip show open-webui 2>&1
$ver2 = ($show | Select-String '^Version:').ToString()
Say "[3] 已装:$ver2"

if ($DownloadOnly) {
  Say ""
  Say "=== 下载阶段完成 ✓ 明天跑(不带 -DownloadOnly)配置指向网关并首启。 ==="
  Say "  睡前到此;明天跟 Claude 说「open-webui 下好了」。"
  exit 0
}

# ---------- 阶段 4(明天):配置 + 首启 ----------
# 数据目录放 state 下(用户账户/对话;非记忆库,但也是本机状态,纳入备份)
$DataDir = Join-Path (Get-Path 'db') '..\openwebui'
$DataDir = [System.IO.Path]::GetFullPath($DataDir)
New-Item -ItemType Directory -Force $DataDir | Out-Null
$env:DATA_DIR = $DataDir
$env:WEBUI_URL = "http://127.0.0.1:8081"
# ★ 指向【网关】,而不是直连 llama —— 这样 E1/别名/审计全生效
$env:OPENAI_API_BASE_URL = "http://127.0.0.1:8080/v1"
$env:OPENAI_API_KEY = "localai"          # 网关本机不校验 key(走 OS 身份);占位即可
$env:ENABLE_OLLAMA_API = "false"
$env:WEBUI_AUTH = "true"                 # 建首个本机 admin 账户
$env:ANONYMIZED_TELEMETRY = "false"      # 关遥测
$env:SCARF_NO_ANALYTICS = "true"
$env:DO_NOT_TRACK = "true"
$env:HF_HUB_OFFLINE = "0"

Say "[4] 首启 Open WebUI @ :8081,指向网关 :8080 …"
Say "    数据目录 $DataDir · 关遥测 · 首个账户即 admin(你注册)"
Say "    (前台运行;确认能开后,明天再决定要不要做成开机自启服务)"
Push-Location (Split-Path $VPy -Parent)
& $VPy -m open_webui serve --port 8081
Pop-Location
