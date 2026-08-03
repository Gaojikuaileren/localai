<#
  {state} 子目录逐个断继承 —— 堵住「审计与成员表对所有已认证账户可写」
  ===========================================================================

  ★★ 本脚本改 NTFS ACL,属【系统安全设置变更】。AI 不得代跑,由机主在提权终端自己执行。

  ── 为什么要有它(2026-08-03 实测)────────────────────────────────────
  `Get-Acl {state}` 逐目录枚举结果:
      memory       Protected=True    ✓ 已断继承
      secrets      Protected=True    ✓ 已断继承
      db / identity / logs / openwebui / quarantine / tickets
                   Protected=False   ✗ 继承 `Authenticated Users : Modify`

  后果具体到两个文件:
    · `identity\store.json`  —— 成员表,D45「按成员可见范围」的【唯一】来源
    · `logs\gate_rejection.jsonl` —— E1 凭证拦截审计(实测 35 KB 真实数据)
  两者对**每一个已认证账户**可写 —— 包括网关在 API 层明令拒绝的 `ai-exec` / `ai-asset`。
  网关在门口拒了它,它转身直接改审计文件就行。
  这正是本项目固定审查视角说的那种:**看着有防护、实际没有**。
  PLAN §6.7.5 自己也写过「P6 建立专用账户之前 OS ACL 层是零防护」。

  ── ACL 表的取证依据(不是猜的)──────────────────────────────────────
  按 `Get-Acl` 逐文件读 Owner 统计(2026-08-03):
      logs        37 个文件:34 × ai-mem · 3 × Administrators
      identity     4 个文件:4  × Administrators
      openwebui    5 个文件:3  × 机主 · 2 × Administrators
      db / tickets 空 · quarantine 1 × Administrators
  ⇒ 只有 `logs` 需要额外授 `ai-mem`;其余 base 表就够。

  ── 已知的【没有】被本脚本解决的问题(诚实记账)────────────────────────
  审计文件(`gate_rejection.jsonl` / `denied_access.jsonl`)与服务的 stdout/stderr 日志
  **躺在同一个目录里**。断继承之后,能写服务日志的账户仍然能改审计文件。
  真正的修法是把审计挪进独立的 append-only 目录(D71 的哈希链 + 跨账户锚点正是为此),
  **那不在本脚本范围内**。本脚本只把「所有已认证账户」收敛到「少数具名账户」。

  用法:
    .\harden-state-acl.ps1 -WhatIf          # 演练:只打印,什么都不改
    .\harden-state-acl.ps1                  # 逐步确认后施加
    .\harden-state-acl.ps1 -Revert          # 回退:恢复继承、摘掉本脚本加的具名 ACE
    .\verify-state-acl.ps1                  # 纯只读复核(任何时候都可以跑)
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$OwnerAccount = $env:USERNAME,
    [switch]$Revert
)

$ErrorActionPreference = 'Stop'
function Say($m, $c = 'Gray') { Write-Host $m -ForegroundColor $c }

# ── 路径全部从 config/paths.toml 派生(§11.1:代码里禁止出现绝对路径)──────
#    解析器与 verify-isolation.ps1 / backup.ps1 同源:只认 `key = 'value'`(单引号)。
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$tomlPath = Join-Path $repoRoot 'config\paths.toml'
if (-not (Test-Path -LiteralPath $tomlPath)) { throw "找不到 config\paths.toml($tomlPath)—— 拒绝继续(fail-closed)。" }
$Paths = @{}; $sec = ''
foreach ($l in Get-Content -LiteralPath $tomlPath -Encoding UTF8) {
    if ($l -match '^\s*\[([^\]]+)\]') { $sec = $Matches[1]; continue }
    if ($l -match "^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*'([^']*)'") { $Paths["$sec.$($Matches[1])"] = $Matches[2] }
}
if ($Paths.Count -eq 0) { throw 'paths.toml 解析出 0 个键 —— 拒绝继续(fail-closed)。' }

# ── 目录 → 除 SYSTEM/Administrators/机主 之外还需要谁 ──────────────────
#    键 = paths.toml 的 [state] 键名。base = SYSTEM(F) + Administrators(F) + 机主(M);下表只写【额外】的。
#    `memory` 与 `secrets` 不在表里:它们已经断继承且有自己的强 ACL(见 verify-isolation.ps1 ②③)。
$PLAN = [ordered]@{
    'state.logs'       = @(@{ Account = 'ai-mem'; Rights = 'M' })  # 实测 34/37 文件由它写
    'state.identity'   = @()                                       # 只有安装/运维写 ⇒ 不给任何 ai-*
    'state.db'         = @()
    'state.quarantine' = @()
    'state.tickets'    = @()
}

# {state} 根:从已登记的路径反推公共父目录,而不是另写一个键。
$declared = @($PLAN.Keys | ForEach-Object { $Paths[$_] } | Where-Object { $_ })
if (-not $declared) { throw 'paths.toml 的 [state] 段没有解析出任何本脚本需要的键 —— 拒绝继续。' }
$StateRoot = Split-Path $declared[0] -Parent

# 断继承后要【摘掉】的宽泛主体
$STRIP = @('NT AUTHORITY\Authenticated Users', 'BUILTIN\Users', 'Everyone')

function Get-Sid($name) {
    try { return (New-Object System.Security.Principal.NTAccount($name)).Translate(
                 [System.Security.Principal.SecurityIdentifier]).Value }
    catch { return $null }
}

function Confirm-Step($title, $lines) {
    Say ''
    Say ("--- $title ---") 'Cyan'
    foreach ($l in $lines) { Say ("    " + $l) }
    if ($WhatIfPreference) { Say '    (演练模式:打印到此为止,不执行)' 'DarkGray'; return $false }
    $a = Read-Host '    执行这一步?输入 y 继续(其它任何输入都视为放弃)'
    return ($a -eq 'y')
}

function Invoke-Icacls($argsArr, $what) {
    # ★ 退出码必须查。原 install 脚本那轮的教训:三条 icacls 都不查退出码,失败照样打绿字。
    $prev = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'   # native 命令 stderr 不当终止错误(PS5.1)
        $out = & icacls @argsArr 2>&1
    } finally { $ErrorActionPreference = $prev }
    if ($LASTEXITCODE -ne 0) {
        foreach ($l in $out) { Say ("      icacls> " + $l) 'DarkGray' }
        throw "icacls 失败($what):exit=$LASTEXITCODE"
    }
}

function Assert-NotWritableBy($path, $account) {
    # ★ 不检查「ACE 在不在」,检查【有效权限】—— ACL 静默不生效是这类脚本的头号失效模式。
    $sid = Get-Sid $account
    if (-not $sid) { return $true }           # 账户不存在 ⇒ 无从写入
    $acl = Get-Acl -LiteralPath $path
    $granted = 0; $denied = 0
    foreach ($ace in $acl.Access) {
        if ($ace.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value -ne $sid) { continue }
        $r = [int]$ace.FileSystemRights
        if ($ace.AccessControlType -eq 'Deny') { $denied  = $denied  -bor ($r -band (-bnot $granted)) }
        else                                   { $granted = $granted -bor ($r -band (-bnot $denied)) }
    }
    $WRITE = [int][System.Security.AccessControl.FileSystemRights]::Write
    return (($granted -band $WRITE) -eq 0)
}

Say ''
Say '=== {state} 子目录 ACL 加固 ===' 'Cyan'
Say ("    StateRoot   : " + $StateRoot)
Say ("    机主账户    : " + $OwnerAccount)
if ($WhatIfPreference) { Say '    ★ 演练模式(-WhatIf):什么都不会改' 'DarkYellow' }
if ($Revert)           { Say '    ★ 回退模式' 'DarkYellow' }

$ownerSid = Get-Sid $OwnerAccount
if (-not $ownerSid) { throw "解析不到机主账户 '$OwnerAccount' 的 SID —— 拒绝继续(fail-closed)。" }

# ★★ 反向全表:盘上实际有、而 paths.toml 没登记的子目录,必须报出来。
#    `openwebui` 就是这么被发现的 —— 它在 {state} 下真实存在,却不在 [state] 段里,
#    于是任何按 paths.toml 遍历的运维脚本都碰不到它,而它照样继承着宽泛 ACE。
#    未登记 = 没有契约 = 没人负责,正是本项目 fail-closed 纪律要挡的形状。
$onDisk = @(Get-ChildItem -LiteralPath $StateRoot -Directory -EA SilentlyContinue | ForEach-Object { $_.Name })
$known  = @($PLAN.Keys | ForEach-Object { Split-Path $Paths[$_] -Leaf }) +
          @('memory', 'secrets')      # 这两个已断继承,有自己的强 ACL
$unregistered = @($onDisk | Where-Object { $known -notcontains $_ })
if ($unregistered.Count) {
    Say ''
    Say ('  ⚠ {state} 下有 ' + $unregistered.Count + ' 个【未在 paths.toml 登记】的子目录:') 'DarkYellow'
    foreach ($u in $unregistered) { Say ('      ' + $u) 'DarkYellow' }
    Say '    本脚本【不动它们】—— 路径契约里没有的东西,不该由一个运维脚本擅自定权限。' 'DarkYellow'
    Say '    要么给它们在 paths.toml 的 [state] 段登记一行,要么确认它们可以删。' 'DarkYellow'
}

foreach ($key in $PLAN.Keys) {
    $p = $Paths[$key]
    # ★ null 检查必须在 Split-Path 之前 —— 顺序写反会让「键缺失」这条本该优雅跳过的路径
    #   变成 ParameterBindingValidationException,而且是在【已经改过前面几个目录之后】炸,
    #   留下一个改了一半的 ACL 状态。fail-closed 的前提是先判定再动手。
    if (-not $p) { Say ("  - 跳过(paths.toml 无此键): " + $key) 'DarkGray'; continue }
    if (-not (Test-Path -LiteralPath $p)) { Say ("  - 跳过(不存在): " + $p) 'DarkGray'; continue }
    $dir = Split-Path $p -Leaf

    if ($Revert) {
        $lines = @("恢复继承: $p", '并摘掉本脚本加的具名 ACE(ai-mem 等)')
        if (Confirm-Step "回退 $dir" $lines) {
            Invoke-Icacls @($p, '/inheritance:e') "恢复继承 $p"
            foreach ($extra in $PLAN[$key]) {
                $s = Get-Sid $extra.Account
                if ($s) { Invoke-Icacls @($p, '/remove:g', "*$s") "摘 $($extra.Account)" }
            }
            Say ("    ✓ 已回退: " + $p) 'Green'
        }
        continue
    }

    $extraDesc = if ($PLAN[$key].Count) { ($PLAN[$key] | ForEach-Object { $_.Account + '(' + $_.Rights + ')' }) -join ', ' } else { '无' }
    $lines = @(
        "断继承(把继承来的 ACE 复制成显式,再关继承): $p",
        ("摘掉宽泛主体: " + ($STRIP -join ', ')),
        "保留 SYSTEM(F) · Administrators(F) · $OwnerAccount(M)",
        ("额外授予: " + $extraDesc)
    )
    if (-not (Confirm-Step "加固 $dir" $lines)) { continue }

    # /inheritance:d = 断继承但把继承来的 ACE 复制成显式(不是 :r,那会清空)
    Invoke-Icacls @($p, '/inheritance:d') "断继承 $p"
    foreach ($who in $STRIP) {
        $s = Get-Sid $who
        if ($s) { Invoke-Icacls @($p, '/remove:g', "*$s") "摘 $who" }
    }
    Invoke-Icacls @($p, '/grant', '*S-1-5-18:(OI)(CI)F')  "SYSTEM $p"          # SYSTEM
    Invoke-Icacls @($p, '/grant', '*S-1-5-32-544:(OI)(CI)F') "Administrators $p"
    Invoke-Icacls @($p, '/grant', "*${ownerSid}:(OI)(CI)M") "机主 $p"
    foreach ($extra in $PLAN[$key]) {
        $s = Get-Sid $extra.Account
        if (-not $s) { Say ("    ⚠ 账户不存在,跳过: " + $extra.Account) 'DarkYellow'; continue }
        Invoke-Icacls @($p, '/grant', "*${s}:(OI)(CI)$($extra.Rights)") "$($extra.Account) $p"
    }

    # ★ 落盘复核:断继承生没生效 + 三个宽泛主体是不是真的写不了了
    $acl = Get-Acl -LiteralPath $p
    if (-not $acl.AreAccessRulesProtected) { throw "断继承没生效: $p" }
    foreach ($who in $STRIP) {
        if (-not (Assert-NotWritableBy $p $who)) { throw "$who 仍可写: $p" }
    }
    Say ("    ✓ 已加固并复核: " + $p) 'Green'
}

Say ''
Say '=== 完成。请跑 .\verify-state-acl.ps1 做独立复核 ===' 'Green'
Say '★ 加固后请重启 start-stack.ps1 里的服务,确认 ai-mem 仍能写 logs、网关仍能读 identity。' 'DarkYellow'
