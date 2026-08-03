<#
.SYNOPSIS
    完整回退 create-ai-op.ps1 —— 摘掉全部 ai-op 的 ACE,可选停用/删除账户。

.DESCRIPTION
    这个脚本不是附赠品,是形态 A 的前提条件之一。

    形态 A 的已知代价写在设计文档 §10-Q1 里:「你要在两个账户之间切换来跑 Claude Code,
    某些工具链要重新配一遍,前两天会有摩擦。」摩擦大到不值得的时候,你要能【干净地退回去】——
    退不回去的方案,在真的难用的那一天只会被绕过,而绕过是最坏的结果。

    做三件事(后两件都是 opt-in,默认只做第一件):
      1. 摘掉所有具名给 ai-op 的显式 ACE(禁区 Deny / 仓库 Allow / 祖先穿越 / 盘根 Deny)
      2. -DisableAccount  停用账户(保留,随时可再启用)
      3. -RemoveAccount   删除本地账户
         ★ 账户的 profile 目录【不删除】,按项目铁律「永不 delete」移入 {state}\quarantine。
           里面可能有外部 AI 干了两天的活。

.NOTES
    · 必须以管理员身份运行,请你自己在提权终端里跑。
    · 幂等:没有 ACE 可摘的路径直接跳过;账户不存在时也不炸。
    · fail-closed:任何一步失败立即停。
    · 顺序是硬的:先摘 ACE 再动账户。反过来会留下一地解析不出名字的孤儿 SID ACE,
      那些 ACE 仍然生效,而且以后没人看得懂它们是谁。

.EXAMPLE
    .\revert-ai-op.ps1 -WhatIf
    .\revert-ai-op.ps1
    .\revert-ai-op.ps1 -RemoveAccount
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$DisableAccount,
    [switch]$RemoveAccount,
    [switch]$AssumeYes,
    # 账户已经被手工删掉、SID 解析不出来时,用它指定要清理的孤儿 SID。
    [string]$OrphanSid
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'ai-op-paths.ps1')

function Confirm-Step {
    param([Parameter(Mandatory)][string]$Title, [string[]]$Detail = @())
    Say ''
    Say ('--- 步骤:' + $Title + ' ---') 'Cyan'
    foreach ($d in $Detail) { Say ('    ' + $d) }
    if ($WhatIfPreference) { Say '    (演练模式:不执行)' 'DarkGray'; return $false }
    if ($AssumeYes)        { Say '    (-AssumeYes:已自动确认)' 'DarkYellow'; return $true }
    $a = Read-Host '    执行这一步?输入 y 继续(其它任何输入都视为放弃)'
    if ($a -ne 'y' -and $a -ne 'Y' -and $a -ne 'yes') { throw "已在步骤【$Title】放弃。" }
    return $true
}

Assert-Admin

$repoRoot = Get-NormalPath (Join-Path $PSScriptRoot '..\..')
$toml     = Read-PathsToml -TomlPath (Join-Path $repoRoot 'config\paths.toml')
$record   = Join-Path $PSScriptRoot 'ai-op-applied.json'

# --- SID ----------------------------------------------------------------------
$sid = Get-AiOpSid
$sidSource = 'Get-LocalUser'
if (-not $sid -and $OrphanSid) { $sid = $OrphanSid; $sidSource = '-OrphanSid 参数' }
if (-not $sid -and (Test-Path -LiteralPath $record)) {
    try {
        $sid = (Get-Content -LiteralPath $record -Raw -Encoding UTF8 | ConvertFrom-Json).sid
        $sidSource = 'ai-op-applied.json 记录'
    } catch { }
}
if (-not $sid) {
    throw ('取不到 ai-op 的 SID:账户不存在、也没有 -OrphanSid、记录文件也读不出来。' +
           ' 没有 SID 就无法定位要摘的 ACE —— 停止(fail-closed),不做「看名字猜」的清理。')
}

Say ''
Say '=============================================================' 'Cyan'
Say ' ai-op 受限账户 —— 回退' 'Cyan'
Say '=============================================================' 'Cyan'
Say ("  SID: {0}   (来源: {1})" -f $sid, $sidSource)
if ($WhatIfPreference) { Say '  ★ 演练模式(-WhatIf):什么都不会改' 'DarkYellow' }

# --- 要清理的路径 --------------------------------------------------------------
# ★ 从 paths.toml 重新算一遍,不依赖记录文件。
#   记录文件放在仓库里,而仓库正是 ai-op 唯一可写的地方 —— 让回退的正确性依赖
#   一份被约束方能改的文件,就是把回退做成了可以被静默削弱的东西。
#   记录文件只用来【补充】(比如 paths.toml 改过之后,旧路径上还挂着 ACE)。
$plan = Get-AiOpPlan -RepoRoot $repoRoot -Toml $toml -AiOpSid $sid -ProtectRepoConfig

$targets = New-Object System.Collections.Generic.List[string]
foreach ($x in $plan.Deny)     { $targets.Add($x.Path) | Out-Null }
foreach ($x in $plan.Allow)    { $targets.Add($x.Path) | Out-Null }
foreach ($x in $plan.Traverse) { $targets.Add($x.Path) | Out-Null }
foreach ($x in $plan.DriveRoots) { $targets.Add($x) | Out-Null }

$extra = 0
if (Test-Path -LiteralPath $record) {
    try {
        $r = Get-Content -LiteralPath $record -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($p in (@($r.deny_paths) + @($r.allow_paths) + @($r.traverse) + @($r.drive_roots))) {
            if ($p -and ($targets -notcontains (Get-NormalPath $p))) {
                $targets.Add((Get-NormalPath $p)) | Out-Null; $extra++
            }
        }
    } catch { Say ('  ! 记录文件读不出来,忽略它继续: ' + $_.Exception.Message) 'DarkYellow' }
}

$targets = @($targets | Sort-Object -Unique)
Say ("  将检查 {0} 条路径(其中 {1} 条来自旧记录、当前 paths.toml 已不涉及)" -f $targets.Count, $extra)

# =============================================================================
#  1. 摘 ACE
# =============================================================================

if (Confirm-Step -Title '摘掉全部 ai-op ACE' -Detail (@(
        '对下列每条路径执行 icacls /remove:g /remove:d(具名 ACE,Allow 与 Deny 都摘)',
        '盘根上的 ACE 摘掉后,继承副本会随之自动消失,不需要遍历整棵树'
    ) + ($targets | ForEach-Object { '  · ' + $_ }))) {
    $done = 0; $skipped = 0
    foreach ($p in $targets) {
        if (-not (Test-Path -LiteralPath $p)) {
            Say ("    - 跳过(路径不存在): {0}" -f $p) 'DarkGray'; $skipped++; continue
        }
        # ★ -ExplicitOnly:只看【直接打在这个对象上】的 ACE。
        #   继承来的副本 icacls /remove 摘不掉(它们随祖先上的那条一起消失),
        #   不过滤的话:① 会对只剩继承 ACE 的路径白跑一次 icacls;
        #   ② 更糟的是下面那次复核会永远看见它,于是 throw「ACE 摘不掉」把回退炸在半路。
        $hadDeny  = Test-HasNamedAce -Path $p -Sid $sid -Type 'Deny'  -ExplicitOnly
        $hadAllow = Test-HasNamedAce -Path $p -Sid $sid -Type 'Allow' -ExplicitOnly
        if (-not $hadDeny -and -not $hadAllow) {
            Say ("    = 无 ai-op ACE,跳过: {0}" -f $p) 'DarkGray'; $skipped++; continue
        }
        Remove-AiOpAces -Path $p -Sid $sid | Out-Null
        # ★ 摘完重读一遍确认真的没了 —— 与 create 的 Assert-AceLanded 对称。
        #   「以为摘干净了其实没有」比「以为设上了其实没有」更阴:你会以为自己已经退回原状。
        if ((Test-HasNamedAce -Path $p -Sid $sid -Type 'Deny'  -ExplicitOnly) -or
            (Test-HasNamedAce -Path $p -Sid $sid -Type 'Allow' -ExplicitOnly)) {
            throw ("ACE 摘不掉: {0} 上仍有【直接打在它身上】的给 ai-op 的 ACE。停在这里,不继续。" -f $p)
        }
        Say ("    ✓ 已摘干净: {0}" -f $p) 'Green'
        $done++
    }
    Say ("    小计:{0} 条已摘 · {1} 条无需处理" -f $done, $skipped)
}

# =============================================================================
#  2. 账户(opt-in)
# =============================================================================

$acct = $null
try { $acct = Get-LocalUser -Name $script:AiOpName -ErrorAction Stop } catch { }

if (-not $acct) {
    Say ''
    Say '--- 账户 ---' 'Cyan'
    Say '    = 账户已不存在,跳过。'
} elseif ($DisableAccount -or $RemoveAccount) {

    if ($DisableAccount -and -not $RemoveAccount) {
        if (Confirm-Step -Title '停用账户' -Detail @(
                ('Disable-LocalUser ' + $script:AiOpName),
                '账户与 profile 都保留,随时可以 Enable-LocalUser 再启用')) {
            Disable-LocalUser -Name $script:AiOpName
            Say '    ✓ 已停用' 'Green'
        }
    }

    if ($RemoveAccount) {
        # profile 目录:按铁律「永不 delete」,移入隔离区,不删。
        $profDir = $null
        $pl = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\' + $sid
        if (Test-Path -LiteralPath $pl) {
            try { $profDir = (Get-ItemProperty -LiteralPath $pl -Name ProfileImagePath -ErrorAction Stop).ProfileImagePath } catch { }
        }
        $loaded = $false
        try {
            $up = Get-CimInstance Win32_UserProfile -ErrorAction Stop | Where-Object { $_.SID -eq $sid }
            if ($up -and $up.Loaded) { $loaded = $true }
        } catch { }
        if ($loaded) {
            throw ('ai-op 的用户配置文件当前处于【已加载】状态 —— 说明还有它的会话/进程在跑。' +
                   ' 先注销那个会话再回来。此时删账户会留下半个 profile。')
        }

        $quar = $null
        if ($profDir -and (Test-Path -LiteralPath $profDir)) {
            if (-not $toml.ContainsKey('state.quarantine')) {
                throw 'paths.toml 缺 [state] quarantine —— 没有隔离区就没有「永不 delete」的落点,拒绝删账户。'
            }
            $quar = Join-Path $toml['state.quarantine'] ('ai-op-profile-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        }

        $detail = @(('Remove-LocalUser ' + $script:AiOpName + '  (SID ' + $sid + ')'))
        if ($quar) {
            $detail += ('★ profile 目录【不删除】,移入隔离区:')
            $detail += ('    ' + $profDir)
            $detail += ('  → ' + $quar)
            $detail += '  (铁律:一切删除实现为移入隔离区。里面可能有外部 AI 干了两天的活。)'
        } else {
            $detail += '(没找到 profile 目录,或它还没被创建过 —— 无需移动)'
        }
        $detail += '注册表 ProfileList 里的登记项【不动】—— 那属于系统设置,请你自己在'
        $detail += '「系统属性 → 高级 → 用户配置文件」里清理。'

        if (Confirm-Step -Title '删除账户' -Detail $detail) {
            if ($quar) {
                New-Item -ItemType Directory -Force -Path (Split-Path $quar -Parent) | Out-Null
                Move-Item -LiteralPath $profDir -Destination $quar
                Say ('    ✓ profile 已移入隔离区: ' + $quar) 'Green'
            }
            Remove-LocalUser -Name $script:AiOpName
            Say '    ✓ 账户已删除' 'Green'
        }
    }
} else {
    Say ''
    Say '--- 账户 ---' 'Cyan'
    Say '    = 保留(没给 -DisableAccount / -RemoveAccount)。'
    Say '      ACE 已摘干净,此时 ai-op 对本机的权限退回「一个普通标准用户」,'
    Say '      也就是说它又能靠 Authenticated Users 写 数据盘根与代码盘根 上没有单独保护的地方了。'
    Say '      不打算再用就加 -DisableAccount。' 'DarkYellow'
}

# =============================================================================
#  3. 收尾
# =============================================================================

if (-not $WhatIfPreference -and (Test-Path -LiteralPath $record)) {
    $bak = $record + '.reverted-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Move-Item -LiteralPath $record -Destination $bak
    Say ''
    Say ('  施加记录已改名为: ' + $bak + '(不删,留痕)') 'DarkGray'
}

Say ''
Say '=== 回退完成 ===' 'Green'
Say '  跑一次 .\verify-ai-op.ps1 看现在的状态。'
Say '  回退之后它【应该】报大量 FAIL —— 那正是「禁区不再被 Deny」的如实反映,不是故障。' 'DarkYellow'
Say ''
Say '  别忘了这些东西 revert 管不着,要你自己收:' 'DarkYellow'
Say '    · 以 ai-op 身份配过的 git 凭据(在 ai-op 的凭据管理器里,随 profile 走)'
Say '    · ai-op 建过的计划任务(NTFS ACL 管不到任务计划程序)'
Say '    · ai-op 装在自己 profile 下的工具链(nvm / npm 全局包 / dotnet 用户级 SDK)'
