<#
.SYNOPSIS
    建立受限账户 ai-op,并按【形态 A】给禁区打显式 Deny ACE、给仓库子树打 Allow。

.DESCRIPTION
    两层 MCP 设计 §3.0 把部署分成两个形态:

      形态 A · 受限操作者 —— 外部 AI(Claude Code / Codex)跑在专用标准账户 ai-op;
               该账户对 {state}、机主 profile 显式 Deny,只对仓库子树可写。
               ⇒ 层二是【遏制边界】,红区裁决真正成立。
      形态 B · 同用户操作者 —— 外部 AI 以机主身份跑,能直接读写客户端落盘数据。
               ⇒ 层二只是【问责与可复原能力】,不是遏制。

    用户已裁定形态 A。本脚本是形态 A 的落地动作。

    ★ 它挡的是 ai-op 这个身份,不挡机主自己。见 README「诚实声明」一节。

.NOTES
    · 必须以管理员身份运行。请你【自己】在提权终端里跑,不要交给 AI 执行
      —— 建账户与改 ACL 属于系统/安全设置变更。
    · 幂等:重复运行不炸。已存在的账户跳过;ACE 先摘后加,不会叠出重复条目。
    · fail-closed:任何一步失败立即停,不「继续尽力而为」。已完成的步骤保留,
      跑 revert-ai-op.ps1 回退。
    · 两个参数【没有默认值】,必须显式选。这不是啰嗦:它们各自决定这套 ACL
      到底挡不挡得住,给默认值就等于替你做了一个安全裁决。

.PARAMETER Membership
    ai-op 加不加 BUILTIN\Users 组。

      users-group  账户能登录、能跑 Claude Code。★ 实测过的事实:%SystemRoot% 与
                   %ProgramFiles% 的 ACL 里【只有】BUILTIN\Users 一条给普通用户的
                   ReadAndExecute,没有 Authenticated Users —— 不在 Users 组的账户
                   读不到系统目录,连 runas 起一个进程都做不到。
      no-group     一个组都不加。最小权限,但这个账户【跑不了任何东西】。
                   只有当你想先把账户与 ACL 摆好、稍后再决定时才选它。

.PARAMETER Containment
    遏制强度。★ 这是本脚本最重要的一个选择。

      enumerated   只对枚举出来的禁区打 Deny(见 ai-op-paths.ps1 的清单)。
                   🔴 实测:数据盘根与代码盘根 的盘根都带 `Authenticated Users : Modify`,
                      且向下继承。ai-op 是 Authenticated User ⇒ 在这个模式下它
                      【仍然能写这两块盘上禁区之外的其它地方】(别的项目、{models}
                      以外的目录等)。也就是说「只对仓库子树可写」这句话在本模式下
                      【不成立】。verify 会因此报一条 FAIL —— 那是如实反映,不是 bug。
      drive-wide   额外在 数据盘根与代码盘根 盘根打 Deny(仅继承给子项,不锁盘根本身),
                   再对仓库子树打显式 Allow、对仓库的每一级祖先目录打「本文件夹」
                   的穿越许可。显式/近祖先的 Allow 压过远祖先继承来的 Deny,
                   于是「只对仓库子树可写」才真的成立。
                   代价:盘根加可继承 ACE 会向下传播,两块盘文件多时可能跑几分钟到
                   十几分钟;期间不要中断。

.PARAMETER PasswordMode
    prompt(默认)= 你当场输入;random = 随机生成并【只显示这一次】。
    脚本自身不包含任何密码。

.PARAMETER ProtectRepoConfig
    额外把仓库内的 config\ 设为 ai-op 不可读写(CFG-1)。
    代价见 README:git checkout / pull 一旦触及 config\ 会在 ai-op 下失败。

.EXAMPLE
    .\create-ai-op.ps1 -Membership users-group -Containment drive-wide -WhatIf
    .\create-ai-op.ps1 -Membership users-group -Containment drive-wide
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][ValidateSet('users-group', 'no-group')][string]$Membership,
    [Parameter(Mandatory = $true)][ValidateSet('enumerated', 'drive-wide')][string]$Containment,
    [ValidateSet('prompt', 'random')][string]$PasswordMode = 'prompt',
    [switch]$ProtectRepoConfig,
    [switch]$AssumeYes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'ai-op-paths.ps1')

# =============================================================================
#  0. 前置
# =============================================================================

function Confirm-Step {
    <#
      每一步执行前打印它要做什么,然后要一次确认。
      ★ fail-closed:只有恰好输入 y / yes 才继续,其它任何输入(包括直接回车)
        都当作放弃并 throw。默认值不设在「放行」那一侧。
    #>
    param([Parameter(Mandatory)][string]$Title, [string[]]$Detail = @())
    Say ''
    Say ('--- 步骤:' + $Title + ' ---') 'Cyan'
    foreach ($d in $Detail) { Say ('    ' + $d) }
    if ($WhatIfPreference) { Say '    (演练模式:打印到此为止,不执行)' 'DarkGray'; return $false }
    if ($AssumeYes)        { Say '    (-AssumeYes:已自动确认)' 'DarkYellow'; return $true }
    $a = Read-Host '    执行这一步?输入 y 继续(其它任何输入都视为放弃)'
    if ($a -ne 'y' -and $a -ne 'Y' -and $a -ne 'yes') {
        throw "已在步骤【$Title】放弃。前面完成的步骤保留在盘上,需要清掉就跑 revert-ai-op.ps1。"
    }
    return $true
}

Assert-Admin

$repoRoot = Get-NormalPath (Join-Path $PSScriptRoot '..\..')
$toml     = Read-PathsToml -TomlPath (Join-Path $repoRoot 'config\paths.toml')

Say ''
Say '=============================================================' 'Cyan'
Say ' ai-op 受限账户 —— 两层 MCP 设计【形态 A】落地' 'Cyan'
Say '=============================================================' 'Cyan'
Say ("  仓库根(自证过,与 paths.toml [roots] code 一致): {0}" -f $repoRoot)
Say ("  组成员策略: {0}" -f $Membership)
Say ("  遏制强度  : {0}" -f $Containment)
Say ("  密码来源  : {0}" -f $PasswordMode)
if ($WhatIfPreference) { Say '  ★ 演练模式(-WhatIf):什么都不会改' 'DarkYellow' }

if ($Containment -eq 'enumerated') {
    Say ''
    Say '  🔴 你选的是 enumerated:' 'Yellow'
    Say '     数据盘根与代码盘根 盘根带 `Authenticated Users : Modify` 并向下继承,ai-op 也是' 'Yellow'
    Say '     Authenticated User ⇒ 它仍然能写这两块盘上【禁区之外】的其它位置。' 'Yellow'
    Say '     「只对仓库子树可写」在本模式下不成立,verify 会为此报 FAIL —— 那是如实反映。' 'Yellow'
}

# --- 计划 ---------------------------------------------------------------------
$existingSid = Get-AiOpSid
$plan = Get-AiOpPlan -RepoRoot $repoRoot -Toml $toml -AiOpSid $existingSid `
                     -ProtectRepoConfig:$ProtectRepoConfig

# ★ 反向全表断言:paths.toml 里不许有「既不在允许根下、也不在拒绝根下」的第三种路径。
#   有 = 这份脚本没跟上 paths.toml 的变化,禁区清单已经不完整了 ⇒ 拒绝执行。
if ($plan.Unclassified.Count -gt 0) {
    Say ''
    Say '  ✗ paths.toml 里有未分类的路径 —— 拒绝执行:' 'Red'
    foreach ($u in $plan.Unclassified) { Say ("      {0} = {1}" -f $u.Key, $u.Path) 'Red' }
    throw '未分类路径存在即停(fail-closed)。请把它加进 ai-op-paths.ps1 的 Deny 清单,或确认它属于仓库子树。'
}

Say ''
Say '  将要打【Deny】的路径:' 'Cyan'
foreach ($d in $plan.Deny) {
    $mark = '   '
    if (-not (Test-Path -LiteralPath $d.Path)) { $mark = ' ! ' }
    Say ("  {0}{1}" -f $mark, $d.Path)
    Say ("       ^ {0}" -f $d.Why) 'DarkGray'
}
Say ''
Say '  将要打【Allow】的路径(唯一可写区):' 'Cyan'
foreach ($a in $plan.Allow) { Say ("     {0}" -f $a.Path) }
if ($Containment -eq 'drive-wide') {
    Say ''
    Say '  drive-wide 附加:' 'Cyan'
    foreach ($r in $plan.DriveRoots) { Say ("     盘根 Deny(仅继承给子项): {0}" -f $r) }
    foreach ($t in $plan.Traverse)   { Say ("     祖先穿越 Allow(本文件夹): {0}" -f $t.Path) }
}
Say ''
Say ('  带 ! 的路径当前不存在,会跳过并说明 —— 不会替你新建禁区目录。') 'DarkGray'

# =============================================================================
#  1. 建账户
# =============================================================================

$didCreate = $false
if ($existingSid) {
    Say ''
    Say ("--- 步骤:建账户 {0} ---" -f $script:AiOpName) 'Cyan'
    Say ("    = 已存在(SID {0}),跳过创建。密码不动。" -f $existingSid)
} else {
    $detail = @(
        ("建本地标准用户 {0}" -f $script:AiOpName),
        '不加入 Administrators —— 一个字都不会写进任何管理员组',
        ('密码来源:' + $PasswordMode + '(脚本自身不含任何密码)'),
        '设 PasswordNeverExpires / AccountNeverExpires / UserMayNotChangePassword'
    )
    if (Confirm-Step -Title ('建账户 ' + $script:AiOpName) -Detail $detail) {
        if ($PasswordMode -eq 'prompt') {
            $pw = Read-Host -AsSecureString ('    请为 ' + $script:AiOpName + ' 设置密码(输入不回显)')
            $pw2 = Read-Host -AsSecureString '    再输一次确认'
            $b1 = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($pw)
            $b2 = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($pw2)
            $same = ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($b1) -ceq
                     [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b2))
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b1)
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b2)
            if (-not $same) { throw '两次输入不一致 —— 停止。' }
        } else {
            # 随机生成,只显示这一次。不写文件、不进日志。
            $bytes = New-Object byte[] 30
            [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
            $plain = [Convert]::ToBase64String($bytes).Replace('/', '_').Replace('+', '-') + '!Aa9'
            Say ''
            Say '    ================ 密码(只显示这一次)================' 'Yellow'
            Say ("      {0}" -f $plain) 'Yellow'
            Say '    ==================================================' 'Yellow'
            Say '    请立刻存进你的密码管理器。脚本不保存它,也不会再显示。' 'Yellow'
            $pw = ConvertTo-SecureString $plain -AsPlainText -Force
            $plain = $null
        }
        New-LocalUser -Name $script:AiOpName -Password $pw `
            -FullName 'LocalAI external operator' `
            -Description 'LocalAI 形态A: 外部 AI(Claude Code/Codex)宿主账户。非管理员。只对仓库子树可写。' `
            -PasswordNeverExpires -AccountNeverExpires -UserMayNotChangePassword | Out-Null
        $didCreate = $true
        Say ('    + 已建: ' + $script:AiOpName) 'Green'
    }
}

$sid = Get-AiOpSid
if (-not $sid) {
    if ($WhatIfPreference) {
        Say ''
        Say '  (演练模式:账户尚未建立,后续 ACL 步骤只能打印计划,无法演练具体 ACE)' 'DarkGray'
        Say ''
        Say '=== 演练结束(什么都没改)===' 'Green'
        return
    }
    throw '账户创建后仍取不到 SID —— 停止。'
}
Say ("    SID: {0}" -f $sid)

# =============================================================================
#  2. 组成员
# =============================================================================

$detail = @()
if ($Membership -eq 'users-group') {
    $detail = @('把 ai-op 加入 BUILTIN\Users(且仅此一个组)',
                '不加入 Administrators,也不加入任何其它组',
                '★ 不加 Users 的话它读不到 %SystemRoot% 与 %ProgramFiles%,起不了任何进程')
} else {
    $detail = @('不加入任何组(-Membership no-group)',
                '★ 这个账户将【无法登录、无法运行任何程序】—— 你选的是先摆架子')
}
if (Confirm-Step -Title '组成员' -Detail $detail) {
    if ($Membership -eq 'users-group') {
        $already = $false
        try {
            foreach ($m in @(Get-LocalGroupMember -Group 'Users' -ErrorAction Stop)) {
                if ($m.SID.Value -eq $sid) { $already = $true; break }
            }
        } catch { throw ('无法枚举 Users 组成员,拒绝盲目添加(fail-closed):' + $_.Exception.Message) }
        if ($already) { Say '    = 已在 Users 组,跳过' }
        else {
            Add-LocalGroupMember -Group 'Users' -Member $script:AiOpName
            Say '    + 已加入 Users' 'Green'
        }
    } else {
        Say '    = 不做任何组操作'
    }

    # ★ 无论走哪一支,都立刻做一次反向全表断言:穷举全部本地组。
    #   不是「检查它不在 Administrators」—— 那条在有人把它加进 Power Users 时不会响。
    $mem = Get-AiOpGroupMembership -Sid $sid
    if ($mem.Unenumerable.Count -gt 0) {
        throw ('这些本地组枚举失败,无法证明 ai-op 不在其中,拒绝继续(fail-closed): ' +
               ($mem.Unenumerable -join ', '))
    }
    $bad = @($mem.Groups | Where-Object { $script:AllowedGroups -notcontains $_ })
    if ($bad.Count -gt 0) {
        throw ('ai-op 出现在不该出现的本地组里,停止: ' + ($bad -join ', '))
    }
    Say ('    ✓ 反向全表断言通过:ai-op 只在 [' + (($mem.Groups -join ', ')) + '] 里') 'Green'
}

# =============================================================================
#  3. 禁区 Deny ACE
# =============================================================================

$denyDetail = @(("对 {0} 条禁区路径施加显式 Deny(先摘后加,幂等)" -f $plan.Deny.Count),
                '每条施加后【重新读一遍 ACL 确认它真的落盘】,没落盘就停',
                '★ {state} 下继承已断开的子目录(memory / secrets)单独打 —— 根上的 Deny 到不了它们')
if (Confirm-Step -Title '禁区 Deny ACE' -Detail $denyDetail) {
    foreach ($d in $plan.Deny) {
        if (-not (Test-Path -LiteralPath $d.Path)) {
            Say ("    - 跳过(路径不存在): {0}" -f $d.Path) 'DarkGray'
            continue
        }
        Remove-AiOpAces -Path $d.Path -Sid $sid | Out-Null
        Invoke-Icacls -IcaclsArgs @($d.Path, '/deny', ("*${sid}:(OI)(CI)F")) -What ('Deny ' + $d.Path)
        Assert-AceLanded -Path $d.Path -Sid $sid -Type 'Deny'
        Say ("    ✓ Deny 已落盘: {0}" -f $d.Path) 'Green'
    }
}

# =============================================================================
#  4. 仓库子树 Allow
# =============================================================================

$allowDetail = @(('对仓库子树打显式 Allow(Modify): ' + $repoRoot),
                 '★ 必须是【具名给 ai-op】的 ACE,不能靠 Authenticated Users 兜底',
                 '  —— drive-wide 模式下那条兜底会被盘根的 Deny 盖掉')
if (Confirm-Step -Title '仓库子树 Allow' -Detail $allowDetail) {
    foreach ($a in $plan.Allow) {
        if (-not (Test-Path -LiteralPath $a.Path)) { throw ('仓库路径不存在: ' + $a.Path) }
        Remove-AiOpAces -Path $a.Path -Sid $sid | Out-Null
        Invoke-Icacls -IcaclsArgs @($a.Path, '/grant', ("*${sid}:(OI)(CI)M")) -What ('Allow ' + $a.Path)
        Assert-AceLanded -Path $a.Path -Sid $sid -Type 'Allow'
        Say ("    ✓ Allow 已落盘: {0}" -f $a.Path) 'Green'
    }
    # -ProtectRepoConfig 的 Deny 在第 3 步已经打过。这里必须【实测】它压过了刚打上去的
    # 仓库根 Allow —— 原先这里只打印一行绿色的 ✓ 而不做任何检查,那是一个绿勾形状的空话:
    # 「显式优先于继承」是对的,但「那条显式 Deny 现在还在、并且真的生效」不是必然的。
    # 绿勾必须有断言在后面撑着,否则它就是本项目最恨的那种假断言。
    if ($ProtectRepoConfig) {
        $cfgDir = Join-Path $repoRoot 'config'
        $cfgMask = Get-EffectiveMask -Path $cfgDir -SidSet (Get-PrincipalSidSet -Sid $sid)
        $cfgLeak = $cfgMask -band ($script:R_WRITE -bor $script:R_APPEND -bor $script:R_DELCHILD)
        if ($cfgLeak -ne 0) {
            throw ("config\ 的 Deny 没能压过仓库根的 Allow:ai-op 对 $cfgDir 实际拿到 " +
                   (Format-Mask $cfgMask) + " —— 停在这里,不宣布成功。")
        }
        Say ('    ✓ 实测:ai-op 对 config\ 的有效权限不含写位(' + (Format-Mask $cfgMask) + ')') 'Green'
    }
}

# =============================================================================
#  5. drive-wide:盘根 Deny + 祖先穿越
# =============================================================================

if ($Containment -eq 'drive-wide') {
    $dwDetail = @(('在盘根打 Deny(仅继承给子项,不锁盘根本身): ' + ($plan.DriveRoots -join ', ')),
                  '再对仓库的每一级祖先目录打「本文件夹」的读+穿越许可',
                  '★ 少了祖先穿越,ai-op 从盘根走不到仓库 —— 这是本模式头号「配好了却用不了」',
                  '⏱ 盘根加可继承 ACE 会向下传播,文件多时可能几分钟到十几分钟,别中断')
    if (Confirm-Step -Title 'drive-wide 遏制' -Detail $dwDetail) {
        foreach ($r in $plan.DriveRoots) {
            Remove-AiOpAces -Path $r -Sid $sid | Out-Null
            # (IO) = inherit-only:ACE 不作用于盘根这个对象本身,只继承给子项。
            # 不加 (IO) 会把盘根自己也锁掉 ⇒ ai-op 连穿越 代码盘根 都不行,仓库照样进不去。
            Invoke-Icacls -IcaclsArgs @($r, '/deny', ("*${sid}:(OI)(CI)(IO)F")) -What ('盘根 Deny ' + $r)
            Assert-AceLanded -Path $r -Sid $sid -Type 'Deny'
            Say ("    ✓ 盘根 Deny 已落盘: {0}" -f $r) 'Green'
        }
        foreach ($t in $plan.Traverse) {
            if (-not (Test-Path -LiteralPath $t.Path)) { throw ('仓库祖先目录不存在: ' + $t.Path) }
            Remove-AiOpAces -Path $t.Path -Sid $sid | Out-Null
            # 无继承标志 = 只作用于本文件夹:给穿越,不给下级任何东西。
            Invoke-Icacls -IcaclsArgs @($t.Path, '/grant', ("*${sid}:(RX)")) -What ('祖先穿越 ' + $t.Path)
            Assert-AceLanded -Path $t.Path -Sid $sid -Type 'Allow'
            Say ("    ✓ 祖先穿越已落盘: {0}" -f $t.Path) 'Green'
        }
        # 仓库根自身的 Allow 是【显式】ACE,压过从盘根继承下来的 Deny;
        # 仓库子项里,来自仓库根的 Allow 比来自盘根的 Deny 更近,排在前面,同样压过。
        # 这条依赖 Windows 的继承顺序规则,不算显然 —— verify 会用有效权限计算实测它。
    }
}

# =============================================================================
#  6. 落一份施加记录(只是面包屑,不是信任根)
# =============================================================================

$record = Join-Path $PSScriptRoot 'ai-op-applied.json'
if (Confirm-Step -Title '写施加记录' -Detail @(('落一份记录: ' + $record),
        '记 SID / 时间 / 模式 / 路径清单,给 revert 在账户已被删除时兜底用',
        '★ 它只是面包屑:revert 不依赖它来决定改哪些路径(那仍然从 paths.toml 重新算)')) {
    $obj = [ordered]@{
        applied_at   = (Get-Date).ToString('s')
        applied_by   = ("{0}\{1}" -f $env:USERDOMAIN, $env:USERNAME)
        account      = $script:AiOpName
        sid          = $sid
        membership   = $Membership
        containment  = $Containment
        protect_repo_config = [bool]$ProtectRepoConfig
        repo_root    = $repoRoot
        deny_paths   = @($plan.Deny | ForEach-Object { $_.Path })
        allow_paths  = @($plan.Allow | ForEach-Object { $_.Path })
        traverse     = @($plan.Traverse | ForEach-Object { $_.Path })
        drive_roots  = @($plan.DriveRoots)
    }
    $obj | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $record -Encoding UTF8
    Say ('    + 已写: ' + $record) 'Green'
}

# =============================================================================
#  7. 收尾
# =============================================================================

Say ''
Say '=== 完成 ===' 'Green'
if ($didCreate -and $PasswordMode -eq 'random') {
    Say '  ★ 上面那串密码只显示了一次。现在就存进密码管理器。' 'Yellow'
}
Say ''
Say '  下一步(缺一不可):'
Say '    1. 跑校验:   .\verify-ai-op.ps1'
Say '       —— 它逐条实测有效权限,而不是只看 ACE 在不在。有 FAIL 先别用。'
Say '    2. 读一遍 README.md 的「切过去会踩的坑」与「诚实声明」两节。'
Say '    3. 要退回去:.\revert-ai-op.ps1'
Say ''
Say '  ★ 诚实声明(README 里有完整版):' 'DarkYellow'
Say '    这套 ACL 挡的是 ai-op 这个身份,【不挡你自己】。你在自己的账户下跑任何 AI 时,' 'DarkYellow'
Say '    形态 A 的保证不成立 —— 那时层二只是问责与可复原能力,不是遏制。' 'DarkYellow'
