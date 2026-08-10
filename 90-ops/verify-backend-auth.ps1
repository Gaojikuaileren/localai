<#
.SYNOPSIS
    验【这台机器上】的模型后端是不是真的上了锁 —— D? 方向 B 的实证半边。

.DESCRIPTION
    ★★★ 这个脚本存在的唯一理由是**反向**:

        「不带 key 必须连不上。」

    源码门禁(`90-ops\gate\check_backend_auth.py`)只能证明**起法里写了**
    `--api-key-file`,证明不了**那个后端此刻真的在拒绝无钥匙的连接**。
    只有对着真后端打一次才知道 —— 而这台机器上的后端是不是这一版起的、
    密钥文件被没被换过,都只有在这里才看得见。

    ★ 判据分两块,**在不在跑无关的那块永远跑**:
      ① 密钥目录的 ACL(不需要后端在跑)—— 钥匙的全部强度都在这儿。
         ai-exec / ai-asset 拿不到钥匙,是「跑腿 worker 直连 18081」这条路被堵死的**唯一**依据
         (D65:回环不过防火墙、账户 ACL 管不了 TCP)。
      ② 端口上的真实行为(需要后端在跑)—— 后端没起就 SKIP,**不算通过**。

    ★ 为什么它是 verify-*.ps1 而不是进自动门禁:它验的是**本机环境**,
      不是源码。塞进提交门禁会因为"这会儿没起栈"而红,而那会训练人去 --no-verify
      (ASSERTION-PITFALLS 第 5 条量过这个代价)。

.EXAMPLE
    powershell -File 90-ops\verify-backend-auth.ps1
#>
[CmdletBinding()]
param(
    [int]$Port = 18081
)

$ErrorActionPreference = 'Continue'
$script:P = 0; $script:F = 0; $script:S = 0

function Ok  ($m) { $script:P++; Write-Host "  OK   $m" -ForegroundColor DarkGray }
function Bad ($m) { $script:F++; Write-Host "  X    $m" -ForegroundColor Red }
function Skip($m) { $script:S++; Write-Host "  SKIP $m" -ForegroundColor DarkYellow }
function Judge($cond, $m) { if ($cond) { Ok $m } else { Bad $m } }

$repo = Split-Path -Parent $PSScriptRoot

Write-Host ''
Write-Host '=== D? · 后端鉴权(本机实证)===' -ForegroundColor Cyan

# ── 落点从 backend_key.py 问,不在这儿写第二份 ────────────────────────────
$py = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $py) {
    Write-Host '  X 找不到 python —— 拿不到密钥落点,拒绝往下猜。' -ForegroundColor Red
    exit 1
}
$keyTool = Join-Path $repo '10-core\gateway\backend_key.py'
$keyFile = (& $py $keyTool check 2>&1 | Select-Object -Last 1)
$keyRc = $LASTEXITCODE
if ($keyRc -ne 0 -or -not $keyFile -or -not (Test-Path -LiteralPath $keyFile)) {
    Write-Host '  X 拿不到一把合法的后端密钥 —— 下面所有判据都无从谈起。' -ForegroundColor Red
    Write-Host ("    " + $keyFile) -ForegroundColor DarkGray
    Write-Host '    ★ 起过一次栈就会生成它(90-ops\start-stack.ps1 或管理端)。' -ForegroundColor DarkGray
    exit 1
}
$keyDir = Split-Path -Parent $keyFile
Write-Host ("  密钥:{0}" -f $keyFile)

# ══════════════════════════════════════════════════════════════════════════
#  ① 密钥目录的 ACL —— 钥匙的全部强度在这儿(不需要后端在跑)
# ══════════════════════════════════════════════════════════════════════════
Write-Host ''
Write-Host '--- ① 密钥目录 ACL(判据拿 SID 比,不拿显示名比)---' -ForegroundColor Cyan
$acl = Get-Acl -LiteralPath $keyDir
Judge $acl.AreAccessRulesProtected `
      '★★★ DACL 已断继承 —— {state} 根继承下来的是 Authenticated Users:(M),不断继承就等于没设锁'

$me = ([Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
$allowed = @($me, 'S-1-5-32-544', 'S-1-5-18')     # 机主 / Administrators / SYSTEM
$sids = @()
foreach ($r in $acl.Access) {
    try { $sids += $r.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value }
    catch { $sids += ('(解析不出: ' + $r.IdentityReference.Value + ')') }
}
$extra = @($sids | Where-Object { $allowed -notcontains $_ } | Select-Object -Unique)
Judge ($extra.Count -eq 0) `
      ("★★★ 授权表里没有多余的人 —— 多出的:" + ($(if ($extra.Count) { $extra -join '、' } else { '(无)' })))

# ★★ 逐个点名 ai-exec / ai-asset:上面那条是"没有多余的",这条是"**那两个**确实不在"。
#    两条不是一回事 —— 前者会因为 $allowed 写错而放过,后者不会。
foreach ($acct in @('ai-exec', 'ai-asset')) {
    $sid = $null
    try { $sid = (New-Object Security.Principal.NTAccount($env:COMPUTERNAME, $acct)).Translate(
                     [Security.Principal.SecurityIdentifier]).Value } catch { }
    if (-not $sid) { Skip ("本机没有 $acct 账户 —— 这条点名判据没有对象(不算通过)"); continue }
    Judge ($sids -notcontains $sid) `
          ("★★★ $acct 拿不到这把钥匙($sid)—— 这就是【跑腿 worker 直连 18081】被堵死的依据本身")
}

# ══════════════════════════════════════════════════════════════════════════
#  ② 端口上的真实行为 —— ★★ 反向才是重点
# ══════════════════════════════════════════════════════════════════════════
Write-Host ''
Write-Host '--- ② 端口上的真实行为(★★ 不带 key 必须连不上)---' -ForegroundColor Cyan

function Probe([string]$path, [hashtable]$headers, [string]$method = 'GET', $body = $null) {
    try {
        $p = @{ Uri = ("http://127.0.0.1:{0}{1}" -f $Port, $path); UseBasicParsing = $true
                TimeoutSec = 15; Method = $method }
        if ($headers) { $p.Headers = $headers }
        if ($body)    { $p.Body = $body; $p.ContentType = 'application/json' }
        return (Invoke-WebRequest @p).StatusCode
    } catch {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        return 0                                   # 连不上
    }
}

$health = Probe '/health' $null
if ($health -eq 0) {
    Skip ("18081 上没有后端在跑 —— 端口行为这一组**跑不了**。★ 这不叫通过:起栈后重跑本脚本。")
} else {
    $key = (Get-Content -LiteralPath $keyFile -Raw -Encoding ASCII).Trim()
    $auth = @{ Authorization = "Bearer $key" }
    $chatBody = '{"messages":[{"role":"user","content":"ping"}],"max_tokens":1}'

    # ★★★ 反向:这两条是本车道的全部价值。只测正向的断言在 key 被摘掉那天照样绿。
    Judge ((Probe '/v1/chat/completions' $null 'POST' $chatBody) -eq 401) `
          '★★★ 反向:**不带 key 打 /v1/chat/completions → 401**(直连绕过网关这条路已经堵死)'
    Judge ((Probe '/v1/chat/completions' @{ Authorization = 'Bearer wrong-key' } 'POST' $chatBody) -eq 401) `
          '★★★ 反向:**错 key → 401**(不是"有个头就放行")'
    Judge ((Probe '/props' $null) -eq 401) '★★ 反向:不带 key 打 /props → 401'

    # 正向
    Judge ((Probe '/props' $auth) -eq 200) '★ 正向:带对 key 打 /props → 200(网关手里这把钥匙确实能开门)'

    # ★ 如实记账:这两个端点**本来就不受 key 约束**(2026-08-10 实测),不是漏了。
    Judge ($health -eq 200) '★ /health 不受 key 约束 → 200(llama-server 如此设计;所以就绪闸看不见钥匙对不对)'
    $models = Probe '/v1/models' $null
    if ($models -eq 200) {
        Write-Host ("  !    ★ 如实记账:/v1/models **不受 key 约束**(不带 key 回 200)—— " +
                    "别名/模型清单对同机进程仍可见。不构成「能用模型」,但也别当它被锁上了。") -ForegroundColor DarkYellow
    }
}

# ── 汇总 ────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host ("=== 后端鉴权(本机):{0} PASS · {1} FAIL · {2} SKIP ===" -f $script:P, $script:F, $script:S) `
           -ForegroundColor $(if ($script:F) { 'Red' } else { 'Green' })
if ($script:S -gt 0) {
    Write-Host '  ★ SKIP 不是通过 —— 上面写着它为什么跑不了。' -ForegroundColor DarkYellow
}
exit $(if ($script:F) { 1 } else { 0 })
