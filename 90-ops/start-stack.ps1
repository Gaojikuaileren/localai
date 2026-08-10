# =============================================================================
#  start-stack.ps1 — 起「无 Broker 期」的模型栈:llama 后端 + 统一入口网关
#
#  数据库(pg-mem / Qdrant / Qdrant-s2 / Embedding)是 Windows 服务,开机自启,不用管。
#  但 **llama 后端与网关目前不是服务**(P4 的 GPU Broker 才负责按需装载/卸载),
#  所以要用它们就得先跑本脚本。
#
#  用法:
#    .\start-stack.ps1              起 assistant.fast(8B @16K, q8_0 KV)+ 网关
#    .\start-stack.ps1 -Ctx 32768   要长上下文(显存够的话)
#    .\start-stack.ps1 -NoBackend   只起网关(后端你自己另起)
#
#  ★ 保持本窗口开着;Ctrl+C 会一并停掉后端与网关。
#  ★ 普通终端即可,不需要管理员。
# =============================================================================
param(
  [int]$Ctx = 16384,
  [switch]$NoBackend,
  [switch]$WithSpeech,          # 一并计入 speech.lite(暂不真启动,只让闸算进去)
  [switch]$Force                # 明知超预算仍要起(会打印被绕过的是哪道闸)
)
$ErrorActionPreference = 'Continue'

$PathsToml = Join-Path $PSScriptRoot '..\config\paths.toml'
function Get-Path([string]$Key) {
  $m = Select-String -Path $PathsToml -Pattern ("^\s*{0}\s*=\s*'([^']+)'" -f [regex]::Escape($Key)) | Select-Object -First 1
  if (-not $m) { throw "paths.toml 缺键: $Key" }
  return $m.Matches[0].Groups[1].Value
}
$Models  = Get-Path 'models'
$AiRoot  = Split-Path $Models -Parent
$GwDir   = (Resolve-Path (Join-Path $PSScriptRoot '..\10-core\gateway')).Path
$GwPy    = Join-Path $AiRoot 'venvs\gateway\Scripts\python.exe'
$Llama   = Join-Path $AiRoot 'tools\llama.cpp\llama-server.exe'
$Model8B = Join-Path $Models 'qwen3-8b\Qwen3-8B-Q4_K_M.gguf'

$procs = @()
function Stop-All {
  foreach ($p in $script:procs) { if ($p -and -not $p.HasExited) { $p.Kill() } }
  Write-Host "`n  已停止后端与网关。数据库服务不受影响(它们是 Windows 服务)。" -ForegroundColor Yellow
}

# ============================================================================
#  ★ 显存闸(§8.1 三层检查)—— 无 Broker 期的过渡措施
#
#  P4 的 GPU Broker 之前,没有任何东西阻止你把显存装爆。而 OOM 在这里不是
#  「干净失败」:sysmem fallback 关掉后是硬失败,没关则静默溢出到内存慢到不可用
#  —— 两种都违反 §12.3「失败可见,不静默降级」。
#
#  ★★★ 2026-08-06 审计 C6 更正:上面那句一直被当成【已成立的前提】在用,
#    而它**从来没有被建立过** —— 全仓没有任何代码去关 sysmem fallback,
#    也没有任何断言验过它是关的。
#    实测(2026-08-06,RTX 5080 / 驱动 610.62):注册表里**没有任何**
#    Sysmem/Fallback 覆盖项 ⇒ 用的是**驱动默认值**,而不是"已关"。
#    ⇒ 因此现在超配的真实表现更可能是**静默溢出到内存、慢到不可用**,
#      而不是这段注释假定的硬失败。★ 两者都违 §12.3,但**长得完全不一样**,
#      排查方向也相反(一个"报错了"、一个"怎么这么卡")。
#  ★ 这三道闸的价值不因此打折 —— 恰恰相反:既然兜底不是硬失败,
#    **闸就是唯一拦得住的东西**。但不许再拿"反正会硬失败"当安慰。
#  ★ 要不要真的去关它是**用户的决定**(那是系统级驱动设置,会影响游戏等所有程序),
#    本脚本不代做。欠账已交回协调层(见 decision-packets/A3-…)。
#  故:起任何后端【之前】先过闸。判定内核是 10-core/gpu-broker/vram_gate.py,
#  P4 的 Broker 会直接复用同一段代码(§8.1「预览与准入必须是同一段代码」)。
# ============================================================================
if (-not $NoBackend) {
  # ctx → 组件 id(peak 随上下文长度变,必须用对应那一档的实测值)
  # ★★ fail-closed:只承认 vram-budget.toml 里【实测过】的档位(2026-07-31 审计)。
  #   原写法 default 兜底到 32k —— 于是 -Ctx 1000000 也被当成 32k 放行,
  #   而 32k 的 Σpeak 恰好卡在预算内,显存闸完全被绕过。超大 ctx 直接爆显存 OOM。
  #   32k 是当前实测过的最大档;要更大先在 toml 里加实测条目,而不是让脚本替你猜。
  $comp = switch ($Ctx) {
    { $_ -le 8192  } { 'llm.assistant.8b@8k';  break }
    { $_ -le 16384 } { 'llm.assistant.8b@16k'; break }
    { $_ -le 32768 } { 'llm.assistant.8b@32k'; break }
    default {
      Write-Host ("XX -Ctx {0} 超过实测过的最大档 32768。" -f $Ctx) -ForegroundColor Red
      Write-Host "   没有对应的实测显存条目,拒绝启动(要更大 ctx 请先在 config/vram-budget.toml 里加实测档位)。" -ForegroundColor Red
      exit 1
    }
  }
  $wanted = @($comp)
  if ($WithSpeech) { $wanted += 'speech.lite' }

  $gate = Join-Path (Split-Path $PSScriptRoot -Parent) '10-core\gpu-broker\vram_gate.py'
  $py = Join-Path $AiRoot 'venvs\gateway\Scripts\python.exe'
  if (-not (Test-Path $py)) { $py = 'python' }
  Write-Host "[0] 显存闸(§8.1 三层检查)…"
  $gateOut = & $py $gate @wanted 2>&1
  $gateCode = $LASTEXITCODE
  $gateOut | ForEach-Object { "    $_" }

  # ★★ 2026-08-04:退出码按【三态】读,不再是 0/非0。
  #   原来把 2(闸自己崩了)和 1(闸判定为拒)混为一谈,而闸在 cp936 控制台下
  #   连**通过**的路径都会崩成退出码 1 ⇒ 这里一直打印「拒绝启动」,
  #   于是要么起不了栈、要么靠 -Force —— 而 -Force 把三道闸整个跳过。
  #   ⇒ 「闸说不行」可以被 -Force 覆盖(你明知故犯);
  #     「闸没跑起来」**不可以** —— 没拿到判定就装,是 §12.3 禁止的静默降级。
  if ($gateCode -eq 2) {
    Write-Host "  X 显存闸【没能跑起来】(退出码 2)—— 这不是「被拒绝」,是闸本身坏了。" -ForegroundColor Red
    Write-Host "    上面是它的异常。先修闸,再谈启动。" -ForegroundColor Red
    Write-Host "    ★ -Force 不能覆盖这一种:没拿到判定就装 = 静默降级。" -ForegroundColor Red
    exit 1
  }
  elseif ($gateCode -ne 0) {
    if ($Force) {
      Write-Host "  ! -Force:明知被上面那道闸拒绝仍继续。OOM 后果自负。" -ForegroundColor Yellow
    } else {
      Write-Host "  拒绝启动 —— 按上面的归因处理(它已经告诉你该改预留还是该关程序)。" -ForegroundColor Red
      Write-Host "  确实要强行起:加 -Force" -ForegroundColor DarkGray
      exit 1
    }
  }

  if (-not (Test-Path $Llama))   { Write-Host "  X 找不到 $Llama" -ForegroundColor Red; exit 1 }
  if (-not (Test-Path $Model8B)) { Write-Host "  X 找不到 $Model8B" -ForegroundColor Red; exit 1 }
  # ==========================================================================
  #  ★★★ D?:起后端之前先备好钥匙 —— 「网关是唯一咽喉」这句话的物理载体。
  #
  #  在此之前 llama-server 无鉴权 ⇒ 这台机器上**任何**程序都能直连 18081,
  #  绕过网关的 E1 / 审计 / 档位(STATE「已知技术债」原文;D65 把它升级成待决 6 的一半:
  #  回环不过防火墙、账户 ACL 管不了 TCP ⇒ 把 worker 跑成 ai-exec 也挡不住它)。
  #  ACL 管不了 TCP,但管得了**文件**:钥匙放在 ai-exec / ai-asset 读不到的目录里。
  #
  #  ★ 生成/校验/目录 ACL 的实现**只有一份**(10-core/gateway/backend_key.py),
  #    这里只是**调用**它 —— 与显存闸「预览与准入必须是同一段代码」同一条纪律。
  #    两处各写一份的话,总有一天两处的判据会分家,而分家那天没有任何东西会红。
  #  ★ 命令行上出现的是**密钥文件路径**,不是密钥本身 —— 命令行谁都读得到。
  # ==========================================================================
  $keyTool = Join-Path $GwDir 'backend_key.py'
  $keyOut  = & $py $keyTool ensure 2>&1
  $keyRc   = $LASTEXITCODE
  $KeyFile = ''
  if ($keyOut) { $KeyFile = (($keyOut | Select-Object -Last 1) -as [string]).Trim() }
  if ($keyRc -ne 0 -or -not $KeyFile -or -not (Test-Path -LiteralPath $KeyFile)) {
    Write-Host "  X 备不出后端鉴权密钥 —— 拒绝起一个**不上锁**的后端。" -ForegroundColor Red
    $keyOut | ForEach-Object { Write-Host ("    " + $_) -ForegroundColor DarkGray }
    exit 1
  }

  Write-Host ("[1] 起 llama 后端 assistant.fast({0} · ctx {1} · q8_0 KV)…" -f $comp, $Ctx)
  $procs += Start-Process -FilePath $Llama -PassThru -NoNewWindow -ArgumentList @(
    '-m', $Model8B, '-ngl', '99', '-c', "$Ctx",
    '--host', '127.0.0.1', '--port', '18081',
    '-fa', 'on', '-ctk', 'q8_0', '-ctv', 'q8_0',
    '--api-key-file', $KeyFile)
  # ★ -f:HTTP 非 2xx 即失败。llama-server 在加载模型时 /health 会回 503,
  #   不加 -f 会误以为已就绪(踩过)。
  $ok = $false
  for ($i = 0; $i -lt 150; $i++) {
    Start-Sleep 2
    if ($procs[0].HasExited) { break }
    & curl.exe -sf -m 3 http://127.0.0.1:18081/health *> $null
    if ($LASTEXITCODE -eq 0) { $ok = $true; break }
  }
  if (-not $ok) { Write-Host "  X 后端未就绪(显存不足?换 -Ctx 更小值试试)" -ForegroundColor Red; Stop-All; exit 1 }

  # ★★★ 上面那个 /health 判不了「钥匙对不对」。实测(2026-08-10 · llama-server 10107):
  #   `/health` 与 `/v1/models` **不受 api-key 约束**,不带 key 也回 200;
  #   受约束的是 `/props` 与 `/v1/chat/completions`(不带 key → 401)。
  #   ⇒ 好消息:加了 key **不会**让上面那道就绪闸变红(动工前最担心的就是这一格)。
  #   ⇒ 坏消息:钥匙不匹配时,栈会**报「已就绪」**,然后每一次对话都 401 ——
  #     失败发生在启动,却要等用户第一次说话才显形。§12.3 明令禁止这种形状。
  #   所以这里**带着钥匙再探一次**。
  # ★ 用 Invoke-WebRequest 而不是 curl.exe:key 要放进请求头,而 curl 的话它就落到
  #   **命令行**上 —— 命令行是同机任何进程都读得到的东西,那等于把钥匙又交出去了。
  $bkey = (Get-Content -LiteralPath $KeyFile -Raw -Encoding ASCII).Trim()
  $authOk = $false
  try {
    $probe = Invoke-WebRequest -Uri 'http://127.0.0.1:18081/props' -UseBasicParsing -TimeoutSec 10 `
                               -Headers @{ Authorization = "Bearer $bkey" }
    $authOk = ($probe.StatusCode -ge 200 -and $probe.StatusCode -lt 300)
  } catch { $authOk = $false }
  if (-not $authOk) {
    Write-Host "  X 后端起来了,但**不认这把钥匙**(带 key 打 /props 没回 2xx)。" -ForegroundColor Red
    Write-Host "    最常见的成因:18081 上那个后端是**上一把钥匙**起的(密钥文件被删过/换过)," -ForegroundColor DarkGray
    Write-Host "    或者它根本不是本脚本起的。修法:把该端口上的 llama-server 停掉再起栈。" -ForegroundColor DarkGray
    Stop-All; exit 1
  }
  Write-Host "    OK 127.0.0.1:18081(已上锁,钥匙已核对)"
} else { Write-Host "[1] 跳过后端(-NoBackend)" }

# ---- 网关 ----
if (-not (Test-Path $GwPy)) { Write-Host "  X 找不到网关 venv: $GwPy" -ForegroundColor Red; Stop-All; exit 1 }
Write-Host "[2] 起统一入口网关 :8080 …"
Push-Location $GwDir
$procs += Start-Process -FilePath $GwPy -PassThru -NoNewWindow -ArgumentList @(
  '-m', 'uvicorn', 'gateway:app', '--host', '127.0.0.1', '--port', '8080')
Pop-Location
$gok = $false
for ($i = 0; $i -lt 30; $i++) {
  Start-Sleep 1
  & curl.exe -sf -m 2 http://127.0.0.1:8080/health *> $null
  if ($LASTEXITCODE -eq 0) { $gok = $true; break }
}
if (-not $gok) { Write-Host "  X 网关未就绪" -ForegroundColor Red; Stop-All; exit 1 }
Write-Host "    OK 127.0.0.1:8080"

Write-Host ""
Write-Host "=== 栈已就绪 ===" -ForegroundColor Green
# ★ Open WebUI 已退役(P3c 判据项,2026-08-03)—— 日常入口只剩统一客户端。
#   不再印它的地址,也不再把它算作栈的一部分。安装脚本暂留(卸载归运维),
#   但任何文档/提示都不得再把它当日常用法推给人。
Write-Host "  客户端      dist\client\localai-client.exe   (唯一入口;Open WebUI 已退役)"
Write-Host "  可用别名:"
try {
  (Invoke-RestMethod 'http://127.0.0.1:8080/v1/models').data |
    ForEach-Object { "    {0,-20} {1}" -f $_.id, $_.contract }
} catch { Write-Host "    (取模型列表失败)" }
Write-Host ""
Write-Host "  ★ 只有 assistant.fast 现在真的能对话 —— 其它别名的后端没起(会返回 503 带缺口,这是设计)。"
Write-Host "  ★ 保持本窗口开着。Ctrl+C 停掉后端与网关。"
Write-Host ""

try {
  while ($true) {
    Start-Sleep 5
    foreach ($p in $procs) {
      if ($p.HasExited) { Write-Host "  ! 有进程退出了,收工。" -ForegroundColor Yellow; Stop-All; exit 1 }
    }
  }
} finally { Stop-All }
