<#
.SYNOPSIS
    ai-op 受限账户 —— create / revert / verify 三个脚本【共用的唯一一张路径表与判定函数】。

.DESCRIPTION
    这个文件本身不做任何事,只被另外三个脚本 dot-source。

    ★ 为什么要单独抽出来(不是为了省行数):
      两层 MCP 设计 §3.5 约束 2 已经裁过同一件事 ——
      「write_text_file 与 patch_toml_key 共用同一张路径黑名单表,元测试断言引用同一符号」。
      ⚠ 编号注意:这条在【设计文档内部】编作 D65,但本仓库 DECISIONS.md 的 D65 是
        Hermes Agent Worker(另一路会话)。决议包
        00-docs/decision-packets/two-layer-mcp-decisions-2026-08-03.md 已把设计文档那批
        重新取号为 D66–D75,且【不是整体平移】:设计文档的 D64→D73、D65→D74,
        D66–D72 编号不变。⇒ 本条将来是 D74(决议包尚未并入 DECISIONS.md,仍可能再动)。
        所以这里【只引条文不引编号】,免得两个 D65 混在一起。
      理由是:两份各自维护的清单必然漂移,而漂移出来的那一条就是没有闸的路。
      这里是同一个形状:如果 create denies 五个目录、verify 只查四个,
      那第五个目录静默失效时【没有任何东西会变红】。
      所以三个脚本的清单必须是同一个函数的返回值,不是三份抄写。

    ★ 路径全部从 config/paths.toml 派生(§11.1:代码里禁止出现绝对路径)。
      唯一的例外是 Windows 自身的位置(profile 根、全机启动目录),它们从注册表与
      环境变量读,同样不硬编码。

.NOTES
    不要直接运行本文件。
#>

Set-StrictMode -Version Latest

# =============================================================================
#  0. 常量
# =============================================================================

# 受限账户名。改这里三个脚本一起改 —— 这正是抽出来的目的。
$script:AiOpName = 'ai-op'

# 已知的本地服务账户(§6.8)。目前只作参考记录:原先 verify 拿它做的
# 「ai-op 的 SID 与它们都不同」是**重言式**(本机账户 SID 天生互不相同),已删除。
# 留着这张表是因为将来真正有用的断言要用它 —— 例如「ai-op 不得出现在
# {state}\memory 的 ACL 里、也不得与 ai-mem 同组」。别再拿它写"都不同"那种断言。
$script:SiblingAccounts = @('ai-mem', 'ai-asset', 'ai-exec')

# ai-op 唯一被允许加入的本地组。除此之外出现在任何组里都是 FAIL。
# ★ 这是一条【反向全表断言】的白名单侧,照抄 gateway.py `_check_local_only` 的形状:
#   不是「检查它不在 Administrators」,而是「穷举全部组,凡不在白名单里的出现即拒」。
#   前者在将来有人把它加进 Power Users / Remote Desktop Users 时不会响。
$script:AllowedGroups = @('Users')

# 权限位(winnt.h)。verify 的有效权限计算要用。
$script:R_READ   = 0x00000001   # FILE_READ_DATA / FILE_LIST_DIRECTORY
$script:R_WRITE  = 0x00000002   # FILE_WRITE_DATA / FILE_ADD_FILE
$script:R_APPEND = 0x00000004   # FILE_APPEND_DATA / FILE_ADD_SUBDIRECTORY
$script:R_EXEC     = 0x00000020   # FILE_EXECUTE / FILE_TRAVERSE
$script:R_DELCHILD = 0x00000040   # FILE_DELETE_CHILD —— 目录上有它就能删里面的东西
$script:R_DELETE   = 0x00010000
$script:R_WDAC     = 0x00040000   # WRITE_DAC —— 能改 ACL 就等于什么都能改
$script:R_WOWNER   = 0x00080000   # WRITE_OWNER —— 夺所有权后可自行改 ACL

# ★ 「禁区必须一位都拿不到」的判定掩码。
#   刻意【不】只写 READ|WRITE|APPEND:那样一个只拿到 DELETE / FILE_DELETE_CHILD 的禁区
#   会被判成「有效权限为空」而变绿 —— 而项目铁律里「永不 delete」正是最不能失守的一条。
#   WRITE_OWNER 同理:夺所有权 = 事后可自行改 ACL,等价于 WRITE_DAC。
$script:R_DENY_LEAK = $script:R_READ -bor $script:R_WRITE -bor $script:R_APPEND `
                 -bor $script:R_DELETE -bor $script:R_DELCHILD `
                 -bor $script:R_WDAC -bor $script:R_WOWNER

# =============================================================================
#  1. 小工具
# =============================================================================

function Say { param([string]$m, [string]$c = 'Gray'); Write-Host $m -ForegroundColor $c }

function Assert-Admin {
    <# 改 NTFS ACL 与建账户都要管理员。不是管理员就停,不降级尝试。 #>
    $isAdmin = ([Security.Principal.WindowsPrincipal] `
        [Security.Principal.WindowsIdentity]::GetCurrent()
      ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        throw '需要管理员权限。右键 PowerShell -> 以管理员身份运行,再跑本脚本。'
    }
}

function Get-NormalPath {
    <# 规范化成可比较的形态:绝对、无尾部反斜杠(盘符根保留 盘符根)。 #>
    param([Parameter(Mandatory)][string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    if ($full.Length -gt 3) { $full = $full.TrimEnd('\') }
    return $full
}

function Test-IsUnder {
    <# $Child 是否落在 $Parent 之下(或就是它)。按目录边界比,防 `{数据根}` 匹配到 `{数据根}foo`。 #>
    param([Parameter(Mandatory)][string]$Child, [Parameter(Mandatory)][string]$Parent)
    $c = (Get-NormalPath $Child).ToLowerInvariant()
    $p = (Get-NormalPath $Parent).ToLowerInvariant()
    if ($c -eq $p) { return $true }
    if (-not $p.EndsWith('\')) { $p = $p + '\' }
    return $c.StartsWith($p)
}

# =============================================================================
#  2. paths.toml —— 唯一路径源
# =============================================================================

function Read-PathsToml {
    <#
      极简解析器,与 backup.ps1 / verify-isolation.ps1 用的是同一个
      —— 只认 `key = 'value'`(单引号)。paths.toml 的路径值全部是单引号,
      双引号的那几个(status / mode / target)不是路径,跳过正好。
      返回 hashtable:"节.键" -> 值
    #>
    param([Parameter(Mandatory)][string]$TomlPath)
    if (-not (Test-Path -LiteralPath $TomlPath)) { throw "找不到路径配置源: $TomlPath" }
    $map = @{}; $sec = ''
    foreach ($line in Get-Content -LiteralPath $TomlPath -Encoding UTF8) {
        $l = $line.Trim()
        if ($l -eq '' -or $l.StartsWith('#')) { continue }
        if ($l -match '^\[([^\]]+)\]') { $sec = $Matches[1]; continue }
        if ($l -match "^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*'([^']*)'") {
            $map["$sec.$($Matches[1])"] = $Matches[2]
        }
    }
    if ($map.Count -eq 0) { throw "paths.toml 解析出 0 个键 —— 拒绝继续(fail-closed)。" }
    return $map
}

# =============================================================================
#  3. 账户 / SID
# =============================================================================

function Get-AiOpSid {
    <# 返回 ai-op 的 SID 字符串;账户不存在返回 $null(不抛 —— 调用方自己决定是不是错)。 #>
    try { return (Get-LocalUser -Name $script:AiOpName -ErrorAction Stop).SID.Value }
    catch { return $null }
}

function Get-PrincipalSidSet {
    <#
      计算「以 ai-op 身份访问时,访问令牌里会带哪些 SID」。
      有效权限必须按整个集合算 —— 只按 ai-op 自己的 SID 算会漏掉最关键的一条:
      数据盘根与代码盘根 的根上有 `Authenticated Users : Modify`,ai-op 是 Authenticated User。

      ★ 这个集合宁可【多列】不可少列:
        多列 -> 算出来的权限偏大 -> 「必须被拒」的断言更容易 FAIL(安全方向);
        少列 -> 算出来的权限偏小 -> 会给出「已经挡住了」的假 PASS(危险方向)。
    #>
    param([Parameter(Mandatory)][string]$Sid)
    $set = New-Object System.Collections.Generic.List[string]
    $set.Add($Sid)
    $set.Add('S-1-1-0')     # Everyone
    $set.Add('S-1-5-11')    # Authenticated Users   <- 数据盘根 / 代码盘根 根上的 Modify 由它来
    $set.Add('S-1-5-15')    # This Organization
    $set.Add('S-1-5-4')     # INTERACTIVE
    $set.Add('S-1-2-0')     # LOCAL
    $set.Add('S-1-2-1')     # CONSOLE LOGON
    $set.Add('S-1-5-113')   # Local account
    # ★ 登录类型 SID 也要列全,否则违反上面那条「宁可多列不可少列」的自定规矩:
    #   ai-op 未必总以交互方式登录 —— runas / 计划任务 / 服务分别带 NETWORK / BATCH / SERVICE。
    #   漏掉哪一种,一条挂在那个 SID 上的 Allow 就算不进来,禁区会得到偏小的有效权限 ⇒ 假 PASS。
    #   代价:某个禁区之外的路径若挂着对这些 SID 的 Deny,会算出偏严的结果、报一条偏保守的 FAIL。
    #   那是**响得太吵**,不是**该响不响** —— 方向对。
    $set.Add('S-1-5-2')     # NETWORK
    $set.Add('S-1-5-3')     # BATCH(计划任务)
    $set.Add('S-1-5-6')     # SERVICE
    $set.Add('S-1-5-14')    # REMOTE INTERACTIVE LOGON
    # 实际所属的本地组
    foreach ($g in (Get-AiOpGroupMembership -Sid $Sid).Groups) {
        try { $set.Add((Get-LocalGroup -Name $g -ErrorAction Stop).SID.Value) } catch { }
    }
    return ($set | Sort-Object -Unique)
}

function Get-AiOpGroupMembership {
    <#
      穷举【全部】本地组,返回 ai-op 所属的组名,以及枚举失败的组名。

      ★ fail-closed:枚举失败的组不能当成「不在里面」。
        Get-LocalGroupMember 在组里含孤儿 SID 时会抛(Windows 已知问题),
        如果把异常吞掉,一个含孤儿 SID 的 Administrators 组就会让
        「ai-op 不是管理员」这条断言【永远绿】—— 那正是本项目最恨的假断言。
        所以失败的组单独回传,由调用方判成 FAIL。
    #>
    param([Parameter(Mandatory)][string]$Sid)
    $inGroups = New-Object System.Collections.Generic.List[string]
    $failed   = New-Object System.Collections.Generic.List[string]
    foreach ($g in (Get-LocalGroup)) {
        try {
            $members = @(Get-LocalGroupMember -Group $g.Name -ErrorAction Stop)
            foreach ($m in $members) {
                if ($m.SID.Value -eq $Sid) { $inGroups.Add($g.Name); break }
            }
        } catch {
            $failed.Add($g.Name)
        }
    }
    # .ToArray() 的理由同 Get-AiOpPlan 结尾:PS 5.1 的 [pscustomobject] 不吃泛型 List
    return [pscustomobject]@{ Groups = $inGroups.ToArray(); Unenumerable = $failed.ToArray() }
}

# =============================================================================
#  4. 有效权限计算(verify 的主力)
# =============================================================================

function Expand-GenericRights {
    <#
      把 ACE 里的 GENERIC_* 通用位展开成具体文件权限位。
      不做这一步会算错:实测 数据盘根 根上的 ACE 是 `-536805376`(0xE0010000),
      即 GENERIC_READ|WRITE|EXECUTE|DELETE —— 直接按数值比对 FILE_WRITE_DATA 会漏判。
    #>
    param([Parameter(Mandatory)][int]$Mask)
    $m = [long][System.BitConverter]::ToUInt32([System.BitConverter]::GetBytes($Mask), 0)
    if ($m -band 0x80000000L) { $m = $m -bor 0x00120089L }  # GENERIC_READ    -> FILE_GENERIC_READ
    if ($m -band 0x40000000L) { $m = $m -bor 0x00120116L }  # GENERIC_WRITE   -> FILE_GENERIC_WRITE
    if ($m -band 0x20000000L) { $m = $m -bor 0x001200A0L }  # GENERIC_EXECUTE -> FILE_GENERIC_EXECUTE
    if ($m -band 0x10000000L) { $m = $m -bor 0x001F01FFL }  # GENERIC_ALL     -> FILE_ALL_ACCESS
    return ($m -band 0x0FFFFFFFL)                            # 清掉通用位本身
}

function Get-EffectiveMask {
    <#
      按 Windows 的 DACL 求值算法,算出给定 SID 集合对某路径的【有效】权限位。

      逐条按 DACL 顺序走:Deny 只对「尚未被授予」的位生效,Allow 只对「尚未被拒绝」
      的位生效。必须按顺序 —— 因为形态 A 的 drive-wide 模式正是靠「离对象更近的
      祖先继承来的 Allow 排在更远祖先继承来的 Deny 前面」才让仓库子树可写的。
      用「有 Deny 就算拒」这种简化算法会把仓库误判成不可写。

      ★ 这是本套脚本里最重要的一个函数:它检查的不是「Deny ACE 在不在」,
        而是「Deny 到底生没生效」。ACL 静默不生效正是这类脚本的头号失效模式,
        而「ACE 存在」与「权限被拒」之间隔着继承断链、顺序、通用位三个坑。
    #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string[]]$SidSet)
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $granted = 0L; $denied = 0L
    foreach ($ace in $acl.Access) {
        $sid = $null
        try { $sid = $ace.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value }
        catch { $sid = $ace.IdentityReference.Value }   # 孤儿 SID:Value 本身就是 SID 串
        if ($SidSet -notcontains $sid) { continue }
        # ★ InheritOnly 的 ACE 【不作用于对象本身】,只作用于它的子项。
        #   不跳过它会算错盘根:create 的 drive-wide 在 数据盘根 / 代码盘根 打的正是 (OI)(CI)(IO) Deny,
        #   而 Windows 默认在 系统盘根 上也放了一条 (OI)(CI)(IO) 的 Authenticated Users : Modify。
        #   把它们当成作用于盘根本身,盘根的有效权限就是假的 —— 两个方向都会错:
        #   盘根会被误判成「已挡住」(那条 (IO) Deny),也会被误判成「可写」(那条 (IO) Allow)。
        #   注意继承到子项之后 (IO) 标志不会跟着传下去,所以禁区子目录的算法不受影响。
        if ($ace.PropagationFlags -band [System.Security.AccessControl.PropagationFlags]::InheritOnly) { continue }
        $r = Expand-GenericRights ([int]$ace.FileSystemRights)
        if ($ace.AccessControlType -eq 'Deny') { $denied  = $denied  -bor ($r -band (-bnot $granted)) }
        else                                   { $granted = $granted -bor ($r -band (-bnot $denied))  }
    }
    return ($granted -band (-bnot $denied))
}

function Test-HasNamedAce {
    <#
      该路径上有没有【具名】给某 SID 的 ACE(Allow / Deny)。

      ★ -ExplicitOnly:只认【直接打在这个对象上】的 ACE,不认继承来的。
        必须有这个开关,理由是 Get-Acl 的 .Access 把继承来的 ACE 一并返回
        (实测:父目录 (OI)(CI) 授权后,子目录的 .Access 里就有一条 IsInherited=True 的同名 ACE)。
        不过滤会同时造出两种错判,方向相反、都很难看:
          · 假 PASS —— drive-wide 模式下 数据盘根 的 Deny 会继承到每个禁区子目录,
            于是「禁区有具名 Deny ACE」这条即使单独那次 icacls 静默失败也照样绿;
            create 的 Assert-AceLanded 同理会把「没落盘」当成「落盘了」。
          · 假 FAIL —— 仓库里的 config\ 会继承到盘根那条 Deny,verify 于是走进
            「-ProtectRepoConfig 已启用」的分支,再断言它不可写 —— 而它本来就该可写,
            于是报一条永远红的 FAIL。而「习惯性忽略红字」正是假断言的另一种长法。
          · revert 里还会因此把「只剩继承 ACE」误判成「摘不干净」而 throw,回退中途炸掉。
        所以凡是要断言「我们自己打的那条 ACE 在不在」,一律带 -ExplicitOnly。
    #>
    param([Parameter(Mandatory)][string]$Path,
          [Parameter(Mandatory)][string]$Sid,
          [Parameter(Mandatory)][ValidateSet('Allow','Deny')][string]$Type,
          [switch]$ExplicitOnly)
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    foreach ($ace in $acl.Access) {
        if ($ace.AccessControlType -ne $Type) { continue }
        if ($ExplicitOnly -and $ace.IsInherited) { continue }
        $s = $null
        try { $s = $ace.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value }
        catch { $s = $ace.IdentityReference.Value }
        if ($s -eq $Sid) { return $true }
    }
    return $false
}

function Format-Mask {
    <# 把权限位渲染成人能读的短串,给 FAIL 行做证据用。 #>
    param([Parameter(Mandatory)][long]$Mask)
    $parts = @()
    if ($Mask -band $script:R_READ)   { $parts += 'READ' }
    if ($Mask -band $script:R_WRITE)  { $parts += 'WRITE' }
    if ($Mask -band $script:R_APPEND) { $parts += 'APPEND' }
    if ($Mask -band $script:R_EXEC)     { $parts += 'EXEC' }
    if ($Mask -band $script:R_DELCHILD) { $parts += 'DELETE_CHILD' }
    if ($Mask -band $script:R_DELETE)   { $parts += 'DELETE' }
    if ($Mask -band $script:R_WDAC)     { $parts += 'WRITE_DAC' }
    if ($Mask -band $script:R_WOWNER)   { $parts += 'WRITE_OWNER' }
    if ($parts.Count -eq 0) { return '(无)' }
    return ($parts -join '|')
}

# =============================================================================
#  5b. ACE 施加 / 摘除 —— create 与 revert 共用,防两边写法漂移
# =============================================================================

function Invoke-Icacls {
    <#
      每一次 icacls 都查退出码。
      ★ 这条是 2026-07-31 那轮审计留下的教训(见 setup-accounts.ps1 的注释):
        原来三条 icacls 都不查退出码,失败照样打绿字「✓ ACL 已设」——
        而 ACL 没设成 = 禁区对 ai-op 敞开,正是这个脚本唯一要防的事。
      ★ 但退出码只是第一道:icacls 的本地化输出不可靠,真正的核验在 Assert-AceLanded。
    #>
    param([Parameter(Mandatory)][string[]]$IcaclsArgs, [Parameter(Mandatory)][string]$What)
    # ★ 刻意【不】写 2>&1:PowerShell 5.1 里把原生命令的 stderr 重定向进来会被包成
    #   NativeCommandError,在 $ErrorActionPreference='Stop' 下直接抛出 ——
    #   于是 icacls 只是往 stderr 写了一行无关紧要的提示,脚本就以一个看不懂的异常炸掉,
    #   而我们连退出码都没来得及看。stderr 让它自己去控制台,判定只看退出码。
    $out = & icacls @IcaclsArgs
    if ($LASTEXITCODE -ne 0) {
        foreach ($l in $out) { Say ("      icacls> {0}" -f $l) 'DarkGray' }
        throw "icacls 失败($What):exit=$LASTEXITCODE"
    }
}

function Remove-AiOpAces {
    <#
      摘掉某路径上【具名给 ai-op 的】显式 ACE(Allow 与 Deny 都摘)。

      两处用到,语义相同,所以只有一份实现:
        · create 施加前先摘 —— 幂等:重复运行不会叠出两条一样的 Deny;
        · revert 回退时摘 —— 这是回退的主体动作。

      ★ 不加 /T:显式 ACE 只存在于我们打过的那些对象上,继承副本会随根上的 ACE
        一起消失。加 /T 会去遍历整棵树(盘根上就是整块盘),既慢又可能改到不该改的。
    #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Sid)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    Invoke-Icacls -IcaclsArgs @($Path, '/remove:g', "*$Sid", '/remove:d', "*$Sid") -What "摘除 ai-op ACE: $Path"
    return $true
}

function Assert-AceLanded {
    <#
      施加之后【重新读一遍】,确认 ACE 真的落在盘上了。

      ★ 这是本套脚本对「ACL 静默不生效」的正面回答。
        icacls 退出 0 不等于 ACE 生效:只读属性、被别的进程持有的句柄、
        owner 不对、路径是重解析点(junction)—— 都可能让它「成功地什么也没做」。
        不重读就宣布成功,就是又造一条假断言。
      失败即 throw:fail-closed,不「继续尽力而为」。
    #>
    param([Parameter(Mandatory)][string]$Path,
          [Parameter(Mandatory)][string]$Sid,
          [Parameter(Mandatory)][ValidateSet('Allow','Deny')][string]$Type)
    # ★ -ExplicitOnly:必须是【我们刚打的那条】。不带这个开关的话,drive-wide 模式下
    #   盘根的 Deny 已经继承到每个禁区子目录,于是这条断言对「单独那次 icacls 静默失败」
    #   永远绿 —— 那正是这个函数存在的唯一理由被抵消掉。
    if (-not (Test-HasNamedAce -Path $Path -Sid $Sid -Type $Type -ExplicitOnly)) {
        throw "ACE 未落盘:$Path 上找不到【直接打在它身上】的给 ai-op 的 $Type ACE(继承来的不算)。icacls 报了成功但盘上没有 —— 停在这里,不继续。"
    }
}

# =============================================================================
#  5a. 继承断开的子目录 —— create 与 verify 共用同一个谓词
# =============================================================================

function Get-ProtectedChildren {
    <#
      递归找出 $Root 下【继承已断开】(AreAccessRulesProtected)的目录。

      ★ 为什么必须【递归】,不能只扫一层:
        打在 $Root 上的可继承 Deny 到不了任何继承被关掉的后代 —— 这与深度无关。
        原先只扫直接子目录,今天恰好够用({state} 下只有 memory / secrets 两个,
        都在第一层),但 D71 马上要建的 `{state}\ctl\journal` 与 `{state}\ctl\anchor`
        本身就要求各自一套断继承的 ACL,它们在**第二层** ——
        只扫一层的话,那两个目录建出来的当天,create 不会给它们打 Deny、
        verify 也不会为此变红。那正是这个文件开头声明要消灭的失效形状。
        实测成本:{state} 下 4066 个目录,枚举 + 逐个 Get-Acl 约 1.8 秒。够便宜。

      ★ 命中后【不剪枝】,继续往下走:
        {state}\memory 断了继承,给它单独打 Deny 之后,若它下面还有一个
        同样断了继承的孙目录,那条 Deny 照样到不了。剪枝就会漏掉它。

      ★ fail-closed:读不到 ACL 的目录一律当作【需要显式处理】返回,不当成"没事"。
      ★ 跳过重解析点(junction / symlink):跟着它走会跑出 $Root 之外,
        而我们要打 Deny 的是真实位置,不是链接。
    #>
    param([Parameter(Mandatory)][string]$Root)
    $out = New-Object System.Collections.Generic.List[object]
    if (-not (Test-Path -LiteralPath $Root)) { return $out.ToArray() }
    $stack = New-Object System.Collections.Stack
    $stack.Push($Root)
    while ($stack.Count -gt 0) {
        $cur = $stack.Pop()
        $kids = @()
        try { $kids = @(Get-ChildItem -LiteralPath $cur -Directory -Force -ErrorAction Stop) }
        catch { continue }
        foreach ($k in $kids) {
            if ($k.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { continue }
            $prot = $false; $why = '继承已断开 —— 根上的 Deny 到不了它'
            try   { $prot = (Get-Acl -LiteralPath $k.FullName -ErrorAction Stop).AreAccessRulesProtected }
            catch { $prot = $true; $why = 'ACL 读取失败 —— 按未覆盖处理(fail-closed)' }
            if ($prot) {
                $out.Add([pscustomobject]@{ Path = (Get-NormalPath $k.FullName); Why = $why }) | Out-Null
            }
            $stack.Push($k.FullName)   # ★ 命中也继续往下,理由见上
        }
    }
    return $out.ToArray()
}

# =============================================================================
#  5. ★ 路径计划 —— 三个脚本唯一的事实来源
# =============================================================================

function Get-AiOpPlan {
    <#
      产出这次要动的全部路径,分四类:

        Deny      —— 打显式 Deny ACE(禁区)
        Allow     —— 打显式 Allow ACE(仓库子树,唯一可写处)
        Traverse  —— 只给「本文件夹」的读+穿越;drive-wide 模式下必须有,
                     否则 ai-op 从盘根走不到仓库(配好了但用不了)
        DriveRoot —— drive-wide 模式下在盘根打 Deny 的两块盘

      以及 Unclassified —— paths.toml 里既不在允许根下、也不在拒绝根下的路径。
      ★ 这一项存在即 FAIL:它是【反向全表断言】的落点,防的是
        「将来 paths.toml 加了第六个根,而这三个脚本忘了跟」。
        这条纪律在 gateway.py 的 KNOWN_AGENTS 上已经用过一次。
    #>
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][hashtable]$Toml,
        [string]$AiOpSid = $null,
        [switch]$ProtectRepoConfig
    )

    $deny     = New-Object System.Collections.Generic.List[object]
    $allow    = New-Object System.Collections.Generic.List[object]
    $traverse = New-Object System.Collections.Generic.List[object]

    function _add { param($list, $p, $why, $kind)
        $list.Add([pscustomobject]@{ Path = (Get-NormalPath $p); Why = $why; Kind = $kind }) | Out-Null }

    # --- 5.1 仓库子树:唯一的可写区 ------------------------------------------
    # ★ 先自证身份:脚本所在的仓库必须就是 paths.toml 里的 code 根。
    #   不一致说明脚本被拷到了别处,而 ACL 会打到错误的目录上 —— 直接拒绝。
    if (-not $Toml.ContainsKey('roots.code')) { throw 'paths.toml 缺键 roots.code —— 拒绝继续(fail-closed)。' }
    $codeRoot = Get-NormalPath $Toml['roots.code']
    if ($codeRoot -ne (Get-NormalPath $RepoRoot)) {
        $msg = "仓库自证失败:脚本位于 [$RepoRoot],而 paths.toml 的 [roots] code = [$codeRoot]。"
        $msg += ' 两者必须相同 —— 否则 ACL 会打到错误的目录上。请在正确的仓库里运行,或先修 paths.toml。'
        throw $msg
    }
    _add $allow $RepoRoot '仓库子树 —— ai-op 唯一的可写区' 'repo'

    # --- 5.2 从 paths.toml 派生的禁区根 --------------------------------------
    foreach ($k in @('roots.state', 'roots.models', 'roots.assets', 'roots.cache')) {
        if (-not $Toml.ContainsKey($k)) { throw "paths.toml 缺键 $k —— 拒绝继续(fail-closed)。" }
        _add $deny $Toml[$k] ("paths.toml [{0}]" -f $k) 'root'
    }
    if ($Toml.ContainsKey('external.comfyui')) {
        _add $deny $Toml['external.comfyui'] 'paths.toml [external] comfyui' 'root'
    }

    # --- 5.3 ★ {state} 下【继承已断开】的子目录:必须各自打 Deny -------------
    #   这是本套脚本里最容易被漏掉的一条,也是最危险的一条:
    #   {state}\memory 与 {state}\secrets 由 setup-accounts.ps1 / setup-secrets-dir.ps1
    #   做过 `icacls /inheritance:r`,继承是【关着】的。
    #   ⇒ 打在 {state} 根上的 Deny ACE 【到不了它们】。
    #   只在根上 deny 一次就宣布「记忆库已挡住」,是彻头彻尾的假防护。
    #   ★ 用共用的 Get-ProtectedChildren(递归,verify ③ 用的是同一个函数):
    #     不递归就会在 D71 的 `{state}\ctl\journal` / `{state}\ctl\anchor` 建出来那天静默失效。
    $stateRoot = Get-NormalPath $Toml['roots.state']
    foreach ($pc in (Get-ProtectedChildren -Root $stateRoot)) {
        _add $deny $pc.Path ('{state} 后代目录,' + $pc.Why) 'protected-child'
    }

    # --- 5.4 全部真实用户 profile(ai-op 自己的除外)-------------------------
    #   机主 profile 里有客户端的落盘数据:%LOCALAPPDATA%\LocalAI\client\
    #   (settings.json + 13 份 store + archive + clips),两层设计 §3.8 的标的全在这。
    #   ★ 从注册表 ProfileList 读,不猜 `%USERPROFILE%`:
    #     profile 目录名与账户名不一定一致(重名、改名、漫游都会岔开)。
    $profileList = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList'
    foreach ($p in (Get-ChildItem -LiteralPath $profileList -ErrorAction Stop)) {
        $sid = Split-Path $p.Name -Leaf
        if ($sid -notmatch '^S-1-5-21-') { continue }          # 跳过 SYSTEM/LocalService/NetworkService
        if ($AiOpSid -and $sid -eq $AiOpSid) { continue }      # ★ 不能 Deny ai-op 自己的 profile,否则它登不上
        $img = $null
        try { $img = (Get-ItemProperty -LiteralPath $p.PSPath -Name ProfileImagePath -ErrorAction Stop).ProfileImagePath } catch { }
        if (-not $img) { continue }
        # ProfileImagePath 是 REG_EXPAND_SZ,可能带 %SystemDrive%。没展开的话
        # Get-NormalPath 会把它当相对路径解析成一个不存在的目录 —— 于是这条 profile
        # 会被静默跳过(路径不存在),机主的客户端数据就没被 Deny 到。展开一次。
        $img = [Environment]::ExpandEnvironmentVariables($img)
        if ($img -notmatch '^[A-Za-z]:\\') { continue }
        _add $deny $img ("用户 profile({0})—— 含客户端落盘数据与 .claude 配置" -f $sid) 'profile'
    }

    # --- 5.5 全机启动目录(H10/§3.5 约束 2:自启目录一律不可达)--------------
    $startup = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\StartUp'
    _add $deny $startup '全机启动目录 —— 写这里等于任意代码执行' 'startup'

    # --- 5.6 可选:仓库内的 config\(CFG-1)---------------------------------
    if ($ProtectRepoConfig) {
        _add $deny (Join-Path $RepoRoot 'config') 'CFG-1 配置目录(-ProtectRepoConfig)—— 代价见 README' 'repo-config'
    }

    # --- 5.7 drive-wide 模式要用的盘根与祖先 ---------------------------------
    $repoDrive  = Get-NormalPath ([System.IO.Path]::GetPathRoot($RepoRoot))
    $stateDrive = Get-NormalPath ([System.IO.Path]::GetPathRoot($stateRoot))
    $driveRoots = @($stateDrive, $repoDrive) | Sort-Object -Unique

    # 仓库的每一级祖先(不含盘根):drive-wide 下必须显式放行「本文件夹」的穿越,
    # 否则盘根的 Deny 会继承到中间目录上,ai-op 根本走不到仓库。
    $cur = Split-Path $RepoRoot -Parent
    while ($cur -and (Get-NormalPath $cur) -ne $repoDrive) {
        _add $traverse $cur '仓库祖先目录 —— drive-wide 模式下必须能穿越' 'ancestor'
        $cur = Split-Path $cur -Parent
    }

    # --- 5.8 ★ 反向全表断言:paths.toml 里有没有【没被分类】的路径 -----------
    $unclassified = New-Object System.Collections.Generic.List[object]
    foreach ($k in $Toml.Keys) {
        $v = $Toml[$k]
        if ($v -notmatch '^[A-Za-z]:\\') { continue }          # 不是绝对路径(端口号等)
        $covered = $false
        if (Test-IsUnder -Child $v -Parent $RepoRoot) { $covered = $true }
        if (-not $covered) {
            foreach ($d in $deny) { if (Test-IsUnder -Child $v -Parent $d.Path) { $covered = $true; break } }
        }
        if (-not $covered) {
            $unclassified.Add([pscustomobject]@{ Key = $k; Path = $v }) | Out-Null
        }
    }

    # ★ 必须先 .ToArray():PowerShell 5.1 里把泛型 List 直接写进 [pscustomobject] 的
    #   哈希表字面量(哪怕外面套了 @())会抛 "Argument types do not match"。
    return [pscustomobject]@{
        RepoRoot     = (Get-NormalPath $RepoRoot)
        Deny         = $deny.ToArray()
        Allow        = $allow.ToArray()
        Traverse     = $traverse.ToArray()
        DriveRoots   = [object[]]$driveRoots
        Unclassified = $unclassified.ToArray()
    }
}
