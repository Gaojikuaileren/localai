<#
.SYNOPSIS
    ai-op 受限账户的否定用例套件 —— 纯只读,输出 PASS / FAIL 清单。

.DESCRIPTION
    ★ 这个脚本比另外两个都重要。

    ACL【静默不生效】是这类脚本的头号失效模式:icacls 退出 0、屏幕上一片绿字、
    而禁区其实是敞开的。本项目已经因为这件事挨过一次(setup-accounts.ps1 里那三条
    不查退出码的 icacls,2026-07-31 审计)。所以这里的断言必须能【真的失败】,
    而且不能只查「ACE 在不在」——「ACE 在」与「权限被拒」之间隔着三个坑:

      坑① 继承断链。{state}\memory 与 {state}\secrets 的继承是【关着】的
           ({state} 根上的 Deny 根本到不了它们)。只在根上 deny 一次就宣布
           「记忆库挡住了」,是彻头彻尾的假防护。
      坑② ACE 顺序。形态 A 的 drive-wide 模式正是靠「近祖先的 Allow 排在
           远祖先的 Deny 前面」让仓库可写的。用「有 Deny 就算拒」的简化判法
           会把仓库误判成不可写,反过来也会把某些实际敞开的地方误判成已挡。
      坑③ 通用位。盘根上的 ACE 是 0xE0010000(GENERIC_*),按数值直接比对
           FILE_WRITE_DATA 会漏判。

    所以主力断言是【有效权限计算】:按 Windows 的 DACL 求值算法,算出以 ai-op
    身份访问时对每条路径实际拿到的权限位,再断言它是 0(禁区)或含写位(仓库)。

.NOTES
    · 只读。不改任何东西,可反复跑,也可以给 AI 跑。
    · 需要管理员权限才能读全部 ACL(读不到就 FAIL,不静默跳过)。
    · 有一类它【验不了】,会在最后一节诚实列出来。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\verify-ai-op.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'ai-op-paths.ps1')

$script:pass = 0; $script:fail = 0; $script:skip = 0

function Test-It { param([string]$Name, [bool]$Cond, [string]$Extra = '')
    if ($Cond) { $script:pass++; Write-Host "  PASS  $Name" -ForegroundColor Green }
    else       { $script:fail++; Write-Host "  FAIL  $Name" -ForegroundColor Red
                 if ($Extra) { Write-Host "        $Extra" -ForegroundColor DarkRed } } }
function Skip-It { param([string]$Name, [string]$Why)
    $script:skip++; Write-Host "  SKIP  $Name" -ForegroundColor DarkGray
    Write-Host "        $Why" -ForegroundColor DarkGray }

$repoRoot = Get-NormalPath (Join-Path $PSScriptRoot '..\..')
$toml     = Read-PathsToml -TomlPath (Join-Path $repoRoot 'config\paths.toml')

Write-Host ''
Write-Host '=== ai-op 受限账户 · 否定用例套件(两层 MCP 设计 形态 A)===' -ForegroundColor Cyan
Write-Host ("    仓库: {0}" -f $repoRoot)

# =============================================================================
#  ① 账户
# =============================================================================
Write-Host ''
Write-Host '① 账户本身'

$sid = Get-AiOpSid
Test-It 'ai-op 账户存在' ([bool]$sid) '未创建 —— 跑 create-ai-op.ps1'
if (-not $sid) {
    Write-Host ''
    Write-Host '  账户不存在,后面全部无从验起。停在这里。' -ForegroundColor Red
    Write-Host ("=== {0} PASS · {1} FAIL · {2} SKIP ===" -f $pass, $fail, $skip) -ForegroundColor Red
    exit 1
}

$u = Get-LocalUser -Name $script:AiOpName
Test-It 'ai-op 已启用' ([bool]$u.Enabled) '账户被停用 —— 它跑不了任何东西(可能是你 revert 过)'

# ★ 反向全表断言:穷举【全部】本地组,不是只看 Administrators。
#   只查 Administrators 的话,有人把它加进 Power Users / Remote Desktop Users
#   / Hyper-V Administrators 时这条断言不会响 —— 那就是一条永远绿的假断言。
#   形状照抄 gateway.py `_check_local_only` 的第 4 条反向全表断言。
$mem = Get-AiOpGroupMembership -Sid $sid
if ($mem.Unenumerable.Count -gt 0) {
    # fail-closed:枚举不到的组不能当成「它不在里面」
    Test-It '★★ 全部本地组可枚举(枚举不了就无法证明它不在里面)' $false `
        ('这些组枚举失败: ' + ($mem.Unenumerable -join ', ') + ' —— 需管理员权限,或组内含孤儿 SID')
} else {
    Test-It '全部本地组可枚举' $true
}
$inAdmins = ($mem.Groups -contains 'Administrators')
Test-It '★ ai-op 不在 Administrators' (-not $inAdmins) `
    'ai-op 是管理员 ⇒ 它能改任何 ACL,本套脚本的每一条 Deny 都失去意义'
$badGroups = @($mem.Groups | Where-Object { $script:AllowedGroups -notcontains $_ })
Test-It ('★★ 反向全表断言:ai-op 只出现在 [' + ($script:AllowedGroups -join ',') + '] 里') `
    ($badGroups.Count -eq 0) ('实际还在: ' + ($badGroups -join ', '))
Write-Host ("        (当前组: {0})" -f $(if ($mem.Groups.Count) { $mem.Groups -join ', ' } else { '(一个都不在)' })) -ForegroundColor DarkGray

if ($mem.Groups.Count -eq 0) {
    Skip-It 'ai-op 能否实际登录并运行程序' `
        '它不在 BUILTIN\Users 里。实测:%SystemRoot% 与 %ProgramFiles% 只给 BUILTIN\Users 读+执行,没有 Authenticated Users ⇒ 这个账户跑不了任何程序。这是 -Membership no-group 的预期结果。'
}

# ★ 这里原来的断言是「ai-op 的 SID 与 ai-mem/ai-asset/ai-exec 都不同」。
#   那是**重言式**:本机账户的 SID 天生互不相同,Get-LocalUser -Name 'ai-op' 不可能
#   返回 ai-mem 的 SID。它无论如何都会绿,守不住任何东西 —— 正是本项目「假断言」那一族。
#   换成一条【真的会失败】的:ai-op 必须是一个新建的本地普通账户,而不是被改了名的内置账户。
#   把内置 Administrator(RID 500)改名叫 ai-op,是这套 ACL 一次就全废的最短路径:
#   RID 500 有 SeTakeOwnership / SeRestore,任何 Deny ACE 对它都是纸糊的。
#   (①的组断言也能抓到这种情况,但只在 Administrators 组可枚举时才行 —— 这条不依赖枚举。)
$ridOk = $false; $ridWhy = 'SID 形状不认识: ' + $sid
if ($sid -match '^S-1-5-21-\d+-\d+-\d+-(\d+)$') {
    $rid = [int]$Matches[1]
    $ridOk = ($rid -ge 1000)
    $ridWhy = "RID = $rid" + $(if ($rid -eq 500) { '  <- 这是内置 Administrator 被改了名' }
                               elseif ($rid -lt 1000) { '  <- 这是一个内置账户,不是新建的普通账户' }
                               else { '' })
}
Test-It '★ ai-op 是新建的本地普通账户(域内 SID 且 RID>=1000,不是改了名的内置账户)' $ridOk $ridWhy

# =============================================================================
#  ② 禁区:逐条实测【有效权限】
# =============================================================================
Write-Host ''
Write-Host '② 禁区 —— 不看 ACE 在不在,看有效权限到底是多少'

$sidSet = Get-PrincipalSidSet -Sid $sid
Write-Host ("        (按 {0} 个 SID 合并计算:含 Everyone / Authenticated Users / 所属组)" -f $sidSet.Count) -ForegroundColor DarkGray

# ★ 不带 -ProtectRepoConfig 取计划:那是个 opt-in 开关。
#   把它无条件算进禁区,会让没开这个开关的人看到一条永远红的 FAIL —— 而「习惯性忽略红字」
#   正是假断言的另一种长法。config\ 单独在 ④ 之后按【实际是否打过】来判。
$plan = Get-AiOpPlan -RepoRoot $repoRoot -Toml $toml -AiOpSid $sid

foreach ($d in $plan.Deny) {
    if (-not (Test-Path -LiteralPath $d.Path)) {
        Skip-It ("禁区 {0}" -f $d.Path) '路径当前不存在'
        continue
    }
    $hasDeny = $false; $mask = $null; $err = $null
    try {
        # -ExplicitOnly:drive-wide 模式下盘根的 Deny 会继承到每个禁区子目录,
        # 不过滤的话这条对「单独那次 icacls 静默失败」永远绿。
        $hasDeny = Test-HasNamedAce -Path $d.Path -Sid $sid -Type 'Deny' -ExplicitOnly
        $mask    = Get-EffectiveMask -Path $d.Path -SidSet $sidSet
    } catch { $err = $_.Exception.Message }

    if ($err) {
        # 读不到 ACL 不能算通过 —— fail-closed
        Test-It ("禁区 {0}" -f $d.Path) $false ('ACL 读取失败(需管理员): ' + $err)
        continue
    }
    Test-It ("禁区有【直接打在它身上】的 Deny ACE: {0}" -f $d.Path) $hasDeny $d.Why
    # ★ 判定掩码用共用常量 R_DENY_LEAK(含 DELETE / DELETE_CHILD / WRITE_OWNER)。
    #   早先只算 READ|WRITE|APPEND|WRITE_DAC —— 那样一个「只拿到 DELETE」的禁区会被判成
    #   「有效权限为空」而变绿,而铁律「永不 delete」恰恰是最不能失守的一条。
    $leak = $mask -band $script:R_DENY_LEAK
    Test-It ("★ 禁区有效权限为空: {0}" -f $d.Path) ($leak -eq 0) `
        ('ai-op 实际拿到: ' + (Format-Mask $mask) + '  <- Deny ACE 存在也可能不生效,这一条才是真的')
}

# =============================================================================
#  ③ 继承断链 —— 这类脚本的头号静默失效
# =============================================================================
Write-Host ''
Write-Host '③ ★ {state} 下继承已断开的后代目录,必须【各自】有 Deny'

$stateRoot = Get-NormalPath $toml['roots.state']
if (-not (Test-Path -LiteralPath $stateRoot)) {
    Skip-It '{state} 子目录继承检查' ('{state} 不存在: ' + $stateRoot)
} else {
    # ★ 必须调用 ai-op-paths.ps1 里【那个】谓词,不能在这里再写一遍。
    #   本节原先自带一份只扫一层的实现 —— 那就是这个文件开头反对的「三份抄写」:
    #   create 用递归版、verify 用单层版,两边一旦漂移,漂出来的那一条就没有闸。
    #   现在 create 的 §5.3 与本节共用 Get-ProtectedChildren(递归)。
    $protectedChildren = @(Get-ProtectedChildren -Root $stateRoot | ForEach-Object { $_.Path })
    if ($protectedChildren.Count -eq 0) {
        Write-Host '        (当前没有继承断开的子目录)' -ForegroundColor DarkGray
    }
    foreach ($c in $protectedChildren) {
        # ★★ 这里断言的必须是【盘上有没有】,不是【计划里有没有】。
        #   原先的写法是「用同一个谓词重新扫一遍子目录,再检查它在不在 $plan.Deny 里」——
        #   而 $plan.Deny 恰恰就是 Get-AiOpPlan 用同一个谓词、同一时刻扫出来的同一批目录。
        #   那是把计划拿去跟它自己比,**结构上不可能失败**,是标准的重言式假断言:
        #   即使 create 那次 icacls 一条都没落盘,它照样全绿。
        #   改成读盘:该目录上必须有一条【直接打在它身上】(非继承)的给 ai-op 的 Deny。
        $landed = $false; $rerr = $null
        try { $landed = Test-HasNamedAce -Path $c -Sid $sid -Type 'Deny' -ExplicitOnly }
        catch { $rerr = $_.Exception.Message }
        if ($rerr) {
            Test-It ("★ 继承断开的子目录已单独打上 Deny: {0}" -f $c) $false ('ACL 读取失败(需管理员): ' + $rerr)
            continue
        }
        Test-It ("★ 继承断开的子目录已单独打上 Deny(读盘,非继承): {0}" -f $c) $landed `
            '这个目录继承是关着的,{state} 根上的 Deny 到不了它 —— 必须单独打。盘上现在没有这条。'
        $m = Get-EffectiveMask -Path $c -SidSet $sidSet
        $leak = $m -band $script:R_DENY_LEAK
        Test-It ("★★ 有效权限为空: {0}" -f $c) ($leak -eq 0) ('实际: ' + (Format-Mask $m))
    }
}

# =============================================================================
#  ④ 仓库子树:必须真的可写
# =============================================================================
Write-Host ''
Write-Host '④ 仓库子树 —— 唯一可写区'

$repoNamed = Test-HasNamedAce -Path $repoRoot -Sid $sid -Type 'Allow' -ExplicitOnly
Test-It '★ 仓库根有【具名给 ai-op】的 Allow ACE' $repoNamed `
    '只靠 Authenticated Users 兜底不行:drive-wide 模式下那条会被盘根的 Deny 盖掉'

$repoMask = Get-EffectiveMask -Path $repoRoot -SidSet $sidSet
$needWrite = $script:R_READ -bor $script:R_WRITE -bor $script:R_APPEND
Test-It '★ 仓库根有效权限含 READ+WRITE+APPEND' (($repoMask -band $needWrite) -eq $needWrite) `
    ('实际: ' + (Format-Mask $repoMask))
Test-It '仓库根有效权限含 DELETE(git 换分支要删文件)' (($repoMask -band $script:R_DELETE) -ne 0) `
    ('实际: ' + (Format-Mask $repoMask))
# ★ 措辞只说验到的那件事。「ai-op 不能自己改 ACL 扩权」是**夸大**:
#   Windows 上对象的**所有者**隐含拥有 READ_CONTROL|WRITE_DAC,与 DACL 无关。
#   ai-op 在仓库里新建的每个文件/目录都归它所有 ⇒ 它对**自己造的东西**照样能改 ACL。
#   本条真正保证的是:它改不了**仓库根这个对象**的 ACL,因而无法把禁区的 Deny 反过来拆掉。
Test-It '仓库根这个对象上 ai-op 没有 WRITE_DAC(拆不掉禁区 Deny;它自建文件的 ACL 仍归它管)' `
    (($repoMask -band $script:R_WDAC) -eq 0) ('实际: ' + (Format-Mask $repoMask))

# 抽查两个深层子目录:验的是「继承真的传下去了」,不是「根上写了一条」
foreach ($sub in @('10-core', '20-client-win', '00-docs')) {
    $p = Join-Path $repoRoot $sub
    if (-not (Test-Path -LiteralPath $p)) { Skip-It ("仓库子目录可写: " + $sub) '不存在'; continue }
    $m = Get-EffectiveMask -Path $p -SidSet $sidSet
    Test-It ("仓库子目录可写(继承确实传下去了): {0}" -f $sub) (($m -band $needWrite) -eq $needWrite) `
        ('实际: ' + (Format-Mask $m))
}

# --- 可选项:-ProtectRepoConfig 打过没有,按实际情况判,不制造永远红的 FAIL ---
$cfgDir = Join-Path $repoRoot 'config'
if (-not (Test-Path -LiteralPath $cfgDir)) {
    Skip-It '仓库内 config\ 保护' 'config\ 不存在'
} elseif (Test-HasNamedAce -Path $cfgDir -Sid $sid -Type 'Deny' -ExplicitOnly) {
    # ★ -ExplicitOnly 是必须的:drive-wide 模式下 config\ 会继承到盘根那条 Deny。
    #   不过滤就会在【没开 -ProtectRepoConfig】时也走进这个分支,然后断言 config\ 不可写 ——
    #   而它本来就该可写,于是每次跑都多一条红字。永远红的 FAIL 会训练人忽略红字。
    $m = Get-EffectiveMask -Path $cfgDir -SidSet $sidSet
    $w = $m -band ($script:R_WRITE -bor $script:R_APPEND)
    Test-It '★ config\ 的 Deny 压过仓库根继承下来的 Allow(显式优先于继承)' ($w -eq 0) `
        ('实际: ' + (Format-Mask $m) + ' —— Deny ACE 在但没生效,这正是本套件要抓的那类失效')
} else {
    Skip-It '仓库内 config\ 保护(-ProtectRepoConfig)' `
        '未启用。config\ 随仓库对 ai-op 可写 ⇒ CFG-1 的四份配置在形态 A 下不由 ACL 保护。想开就重跑 create 时加 -ProtectRepoConfig(代价见 README)。'
}

# =============================================================================
#  ⑤ 遏制强度:「只对仓库子树可写」到底成不成立
# =============================================================================
Write-Host ''
Write-Host '⑤ ★ 遏制强度 —— 形态 A 的核心主张能不能兑现'

$driveWide = $true
foreach ($r in $plan.DriveRoots) {
    $has = $false
    try { $has = Test-HasNamedAce -Path $r -Sid $sid -Type 'Deny' -ExplicitOnly } catch { }
    if (-not $has) { $driveWide = $false }
    Write-Host ("        盘根 {0}: ai-op Deny ACE = {1}" -f $r, $has) -ForegroundColor DarkGray
}

# ★ 断言名只说它【真的验了什么】。
#   原来的名字是「ai-op 只对仓库子树可写」成立 —— 那是夸大:本条只看 paths.toml 派生出来的
#   两块盘(D: 与 E:)的盘根。**C: 完全不在遏制范围内**,create 在 C: 上只 Deny 了
#   各用户 profile 与全机 StartUp,而 Windows 默认给
#     系统盘根               Authenticated Users : (AD) 本文件夹 + (OI)(CI)(IO) Modify
#     %ProgramData%    BUILTIN\Users : Write
#   ⇒ 即便 drive-wide 全绿,ai-op 仍能建 `%SystemDrive%\<任意目录>` 并对它有完全控制(它是所有者),
#     也仍能在 %ProgramData% 下建文件。所以「只对仓库子树可写」这句话在任何模式下都不成立,
#     成立的是弱得多的一句:「D: 与 E: 上除仓库外不可写」。见 README 第 7 节。
Test-It ('★★ 数据盘/代码盘 两块盘上「除仓库子树外不可写」成立(盘根已打 Deny)') $driveWide `
    ('数据盘根与代码盘根 的盘根带 `Authenticated Users : Modify` 并向下继承,ai-op 也是 Authenticated User。' +
     '没有盘根 Deny ⇒ 它仍能写这两块盘上禁区之外的其它位置。' +
     '这是 -Containment enumerated 的【预期结果】,不是脚本 bug —— 但形态 A 的这句主张此时不成立,文档不得声称它成立。')

Skip-It '「ai-op 只对仓库子树可写」(全机口径)' `
    ('🔴 本套件【验不了也不成立】。遏制只覆盖 paths.toml 派生出的 D: 与 E: 两块盘;' +
     'C: 上除各用户 profile 与全机 StartUp 外没有 Deny,而 系统盘根 默认给 Authenticated Users ' +
     '「创建文件夹/追加数据」+ 对子项 Modify、%ProgramData% 给 BUILTIN\Users「写入」⇒ ' +
     'ai-op 能建 %SystemDrive%\<任意目录> 并作为所有者完全控制它。要堵需要第三块盘根 Deny 或组策略,本期不做。')

# 抽查两类「不在禁区清单里、但也不该可写」的位置,用来把上面那条落到实处。
# ★ 必须同时抽【仓库的同级目录】:只抽父目录是不够的 —— drive-wide 模式下父目录正是
#   create 亲手打过 (RX) 的那一级,它不可写是构造出来的必然,证明不了别处也被挡住。
$probeTargets = New-Object System.Collections.Generic.List[string]
$parent = Split-Path $repoRoot -Parent
if ($parent -and (Test-Path -LiteralPath $parent)) {
    $probeTargets.Add($parent) | Out-Null          # 仓库的父目录(create 打过 (RX))
    foreach ($s in (Get-ChildItem -LiteralPath $parent -Directory -Force -ErrorAction SilentlyContinue |
                    Where-Object { (Get-NormalPath $_.FullName) -ne $repoRoot } | Select-Object -First 2)) {
        $probeTargets.Add($s.FullName) | Out-Null  # 真正的同级目录:典型的「其它项目」
    }
}
foreach ($t in $probeTargets) {
    $m = Get-EffectiveMask -Path $t -SidSet $sidSet
    $w = $m -band ($script:R_WRITE -bor $script:R_APPEND -bor $script:R_DELCHILD)
    Test-It ("★ 仓库【之外】的目录不可写: {0}" -f $t) ($w -eq 0) `
        ('ai-op 实际拿到: ' + (Format-Mask $m) + ' —— 它能在仓库旁边建文件/目录')
}

if ($driveWide) {
    Write-Host ''
    Write-Host '   drive-wide 模式的配套:祖先目录必须能穿越,否则配好了也用不了'
    foreach ($t in $plan.Traverse) {
        if (-not (Test-Path -LiteralPath $t.Path)) { Skip-It ('祖先穿越 ' + $t.Path) '不存在'; continue }
        $m = Get-EffectiveMask -Path $t.Path -SidSet $sidSet
        Test-It ("★ 祖先目录可穿越: {0}" -f $t.Path) (($m -band $script:R_EXEC) -ne 0) `
            ('实际: ' + (Format-Mask $m) + ' —— 少了穿越权,ai-op 从盘根根本走不到仓库')
    }
}

# =============================================================================
#  ⑥ 反向全表断言:paths.toml 里没有漏网的路径
# =============================================================================
Write-Host ''
Write-Host '⑥ ★ 反向全表断言 —— paths.toml 里的每条路径都被分类过'

Test-It '★ paths.toml 无未分类路径(既不在仓库下、也不在任何禁区下)' `
    ($plan.Unclassified.Count -eq 0) `
    ('未分类: ' + (($plan.Unclassified | ForEach-Object { $_.Key + '=' + $_.Path }) -join ' ; ') +
     ' —— 说明 paths.toml 加了新根而这三个脚本没跟上,禁区清单已经不完整')

# =============================================================================
#  ⑦ 与两层 MCP 设计的接口交叉核对(只读源码)
# =============================================================================
Write-Host ''
Write-Host '⑦ 与网关的接口交叉核对(只读 gateway.py,不修改)'

$gw = Join-Path $repoRoot '10-core\gateway\gateway.py'
if (-not (Test-Path -LiteralPath $gw)) {
    Skip-It 'gateway.py LOCAL_DENY_ACCOUNTS 交叉核对' 'gateway.py 不存在'
} else {
    $gwSrc = Get-Content -LiteralPath $gw -Raw -Encoding UTF8
    $m = [regex]::Match($gwSrc, 'LOCAL_DENY_ACCOUNTS\s*=\s*\{([^}]*)\}')
    if (-not $m.Success) {
        Test-It 'gateway.py 里能找到 LOCAL_DENY_ACCOUNTS' $false '常量名变了?交叉核对失效 —— 按 fail-closed 记 FAIL'
    } else {
        $hasAiOp = $m.Groups[1].Value -match '["'']ai-op["'']'
        Test-It '★ gateway.py 的 LOCAL_DENY_ACCOUNTS 含 ai-op' $hasAiOp `
            ('当前是 {' + $m.Groups[1].Value.Trim() + '}。ai-op 跑的是外部 AI:若它能直连网关,就绕开了整个层二' +
             '(§4.1-② 对 ai-ctl 用的正是这条理由)。NTFS ACL 挡不住 TCP —— 这一条只能在 gateway.py 里补,' +
             '而 gateway.py 由主进程持有,本套脚本不碰它。')
    }
}

# =============================================================================
#  ⑧ 诚实边界 —— 这个套件【验不了】什么
# =============================================================================
Write-Host ''
Write-Host '⑧ 诚实边界(方案书 §12.3 首轮门禁「失败可见 · 不静默降级」:说清这个套件验不了什么)' -ForegroundColor DarkYellow

Skip-It '★ 本套件自身没被 ai-op 改过' `
    ('🔴 这三个脚本就放在仓库里(90-ops\ai-op-account\),而仓库正是 ai-op 唯一的可写区 ⇒ ' +
     '在 ai-op 下跑过 AI 之后,create/verify/ai-op-paths 全都是【被约束方能改的文件】。' +
     'revert 已经为 ai-op-applied.json 想过这件事(它拒绝依赖记录文件来决定改哪些路径),' +
     '但同一条反对意见对整套脚本成立得更强:一个被改过的 verify 会把每一条都报成绿的。' +
     '唯一的补法是【从仓库外的一份副本跑 verify】,并且切回本人账户后先看 git diff。')

Skip-It '★ 用户 profile 下【继承已断开】的后代目录也各自有 Deny' `
    ('🔴 **没做,也没验**。第 ③ 节那套「继承断开的后代必须各自打 Deny」只跑在 {state} 上;' +
     'create 对用户 profile 只在**根**上打一条 (OI)(CI) Deny。实测(2026-08-03,' +
     '%USERPROFILE%,深度≤3):**249 个后代目录继承是关着的**,根上的 Deny 到不了它们。' +
     '其中对一个新建标准用户仍有权限的有 **5 个**(4 个 READ|EXEC:VS Code 安装目录 / 两个 Temp ' +
     '子目录 / Roaming\Microsoft\Installer;1 个可写:Documents\Adobe\...\Tutorial)—— ' +
     '客户端落盘数据 %LOCALAPPDATA%\LocalAI\client 与 .claude 都**是继承的**,确实被挡住了。' +
     '⇒ 当前泄漏面小且不含标的,但这是**结构性缺口**:装一个新软件就可能多出一个断继承目录,' +
     '而这里不会有任何东西变红。要补的话是把 Get-ProtectedChildren 也跑在每个 profile 上' +
     '(代价:全量遍历 profile 很慢,且要给几百个目录逐个打 ACE)。本期只如实登记。')

Skip-It '以 ai-op 身份实际去读记忆库会失败' `
    '需要该账户的凭据起一个进程,本套件不持有。它验的是「拦截该在的地方在不在、算出来生不生效」,不是「运行时真的触发了」。'
Skip-It 'H2 的通配符禁令(**/*.key|pem|pfx|credentials*|secrets*)' `
    '🔴 NTFS 的 ACE 挂在对象上,没有按文件名通配的授权。这一条只能靠目录级 Deny 覆盖 + ctld 的编译期黑名单,ACL 层做不到。'
Skip-It '阻止 ai-op 改仓库内的 registry.toml / gateway.py / config' `
    '🔴 那些就在仓库里,而仓库是 ai-op 的工作区。H9「外部 AI 永远不能改谁能读什么」在形态 A 下【不由 ACL 强制】,只由 ctld 的编译期黑名单 + 你看 git diff 强制。-ProtectRepoConfig 只覆盖 config\,10-core\gateway\registry.toml 仍可写。'
Skip-It '阻止 ai-op 建计划任务 / 写自己 profile 下的自启项' `
    '🔴 任务计划程序与注册表 Run 键不在 NTFS 管辖内。全机启动目录已 Deny,ai-op 自己 profile 下的启动目录要等它首次登录后才存在。'
Skip-It '挡住机主自己' `
    '🔴 这套 ACL 挡的是 ai-op 这个身份。你在自己账户下跑 AI 时,形态 A 的保证【不成立】,层二退化成问责与可复原能力(设计文档 §9-R1/R2)。'
Skip-It 'DACL 顺序的权威判定' `
    '有效权限按 Get-Acl 返回的 DACL 顺序复算 Windows 的算法。权威答案是以 ai-op 身份真的去访问一次 —— 见上面第一条。'

# =============================================================================
Write-Host ''
Write-Host ("=== ai-op 校验:{0} PASS · {1} FAIL · {2} SKIP ===" -f $pass, $fail, $skip) `
    -ForegroundColor $(if ($fail) { 'Red' } else { 'Green' })
if ($fail) {
    Write-Host '    有 FAIL —— 先别把 Claude Code 切过去。逐条看上面的红字。' -ForegroundColor Red
    exit 1
}
