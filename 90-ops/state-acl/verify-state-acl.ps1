<#
  {state} 子目录 ACL 复核 —— 纯只读,任何时候都可以跑,不改任何东西。

  ★ 它检查的不是「ACE 在不在」,而是「**有效权限**到底是什么」。
    ACL 静默不生效是这类脚本的头号失效模式:icacls 退出 0、ACE 也在,
    但被 DACL 顺序或继承关系压掉 —— 只看 ACE 存在与否会给出假 PASS。
#>
param()

$ErrorActionPreference = 'Stop'

# ── 路径从 config/paths.toml 派生(§11.1:代码里禁止出现绝对路径)──────────
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
if (-not $Paths['state.logs']) { throw 'paths.toml 的 [state] 段缺 logs 键 —— 拒绝继续。' }
$StateRoot = Split-Path $Paths['state.logs'] -Parent
$pass = 0; $fail = 0
function Test-It($n, $c, $d = '') {
    if ($c) { $script:pass++; Write-Host "  PASS  $n" -ForegroundColor Green }
    else    { $script:fail++; Write-Host "  FAIL  $n $d" -ForegroundColor Red }
}
function Get-Sid($name) {
    try { return (New-Object System.Security.Principal.NTAccount($name)).Translate(
                 [System.Security.Principal.SecurityIdentifier]).Value } catch { return $null }
}
function Get-TokenSids($account) {
    <#
      ★★ 这个函数是本脚本第一版的 bug 修复,值得留个记号:
        第一版只比对【账户自己的 SID】,于是「ai-asset 写不了 logs」报 PASS ——
        **假 PASS**。ai-asset 没有具名 ACE 不代表它写不了:它在 `Authenticated Users` 里,
        而那条组 ACE 有 Modify。有效权限算的是【令牌里所有 SID 的并集】,不是账户那一个 SID。
        ——「只看具名 ACE 在不在」正是本函数注释里警告的那种失效模式,第一版自己踩了。
    #>
    $sid = Get-Sid $account
    if (-not $sid) { return $null }
    $sids = @($sid)
    # 本地组成员关系(穷举所有本地组,不是只查几个已知的)
    foreach ($g in Get-LocalGroup -EA SilentlyContinue) {
        try {
            foreach ($m in Get-LocalGroupMember -Group $g.Name -EA Stop) {
                if ($m.SID.Value -eq $sid) { $sids += $g.SID.Value; break }
            }
        } catch { }   # 枚举不了的组:下面用 well-known 兜底
    }
    # 任何已认证的本地账户,登录令牌里必然带这两个 well-known 组
    $sids += 'S-1-5-11'   # Authenticated Users
    $sids += 'S-1-1-0'    # Everyone
    return ($sids | Sort-Object -Unique)
}

function Get-EffectiveWrite($path, $account) {
    # 逐条按 DACL 顺序算有效权限:Deny 只对「尚未被授予」的位生效,Allow 只对「尚未被拒绝」的位生效。
    # ★ 比对的是【令牌 SID 集合】,不是单个账户 SID(见 Get-TokenSids 的注释)。
    $tokenSids = Get-TokenSids $account
    if (-not $tokenSids) { return $null }                 # 账户不存在
    $acl = Get-Acl -LiteralPath $path
    $granted = 0; $denied = 0
    foreach ($ace in $acl.Access) {
        $aSid = try { $ace.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value } catch { $null }
        if ($tokenSids -notcontains $aSid) { continue }
        $r = [int]$ace.FileSystemRights
        if ($ace.AccessControlType -eq 'Deny') { $denied  = $denied  -bor ($r -band (-bnot $granted)) }
        else                                   { $granted = $granted -bor ($r -band (-bnot $denied)) }
    }
    $WRITE = [int][System.Security.AccessControl.FileSystemRights]::Write
    return (($granted -band $WRITE) -ne 0)
}

$DIRS  = @('db', 'identity', 'logs', 'openwebui', 'quarantine', 'tickets')
$STRIP = @('NT AUTHORITY\Authenticated Users', 'BUILTIN\Users', 'Everyone')

Write-Host ''
Write-Host '=== {state} 子目录 ACL 复核(只读)===' -ForegroundColor Cyan
Write-Host ("    StateRoot: " + $StateRoot)

Write-Host ''
Write-Host '① 已知应当【已经】断继承的两个(基线,不该因本次改动而变)'
foreach ($d in @('memory', 'secrets')) {
    $p = Join-Path $StateRoot $d
    if (Test-Path -LiteralPath $p) {
        Test-It "$d 断继承" (Get-Acl -LiteralPath $p).AreAccessRulesProtected
    } else { Write-Host "  SKIP  $d 不存在" -ForegroundColor DarkGray }
}

Write-Host ''
Write-Host '② 本次要加固的六个:断继承 + 宽泛主体写不了'
foreach ($d in $DIRS) {
    $p = Join-Path $StateRoot $d
    if (-not (Test-Path -LiteralPath $p)) { Write-Host "  SKIP  $d 不存在" -ForegroundColor DarkGray; continue }
    Test-It "$d 断继承" (Get-Acl -LiteralPath $p).AreAccessRulesProtected
    foreach ($who in $STRIP) {
        $w = Get-EffectiveWrite $p $who
        if ($null -eq $w) { continue }
        Test-It "  $d :: $who 写不了" (-not $w) '(有效权限里仍有 Write)'
    }
}

Write-Host ''
Write-Host '③ ★ 隔离服务账户不得写审计与成员表'
foreach ($acct in @('ai-asset', 'ai-exec')) {
    foreach ($d in @('logs', 'identity')) {
        $p = Join-Path $StateRoot $d
        if (-not (Test-Path -LiteralPath $p)) { continue }
        $w = Get-EffectiveWrite $p $acct
        if ($null -eq $w) { Write-Host "  SKIP  $acct 不存在" -ForegroundColor DarkGray; continue }
        Test-It "★ $acct 写不了 $d" (-not $w) `
            '(网关在 API 层拒了它,它却能直接改文件 —— 看着有防护、实际没有)'
    }
}

Write-Host ''
Write-Host '④ ★ 服务仍然能用(加固不得把栈打死)'
$p = Join-Path $StateRoot 'logs'
if (Test-Path -LiteralPath $p) {
    $w = Get-EffectiveWrite $p 'ai-mem'
    if ($null -ne $w) { Test-It '★ ai-mem 仍能写 logs(实测 34/37 日志文件由它写)' $w '(它写不了 = 记忆服务的日志会静默消失)' }
}

Write-Host ''
Write-Host '⑤ ★★ 隔离区里的条目必须【继承】隔离区的 ACL,不得自带显式宽泛 ACE'
<#
  ★★ 这一节的由来(2026-08-03 实测,值得记住):
    把 {state}\openwebui 归档进 quarantine 之后,发现它 `Protected=True` 且 DACL 里
    赫然还有 `Authenticated Users : Modify` —— 宽泛权限**跟着数据一起搬过去了**。

    原因:**同卷 Move 是重命名**,NTFS 为了让对象在新位置保持相同的有效访问,
    会把它原先【继承来的】ACE **转成显式**带过去,并置上 Protected 位。
    ⇒ 「移进一个加固过的目录」**不等于**「变成加固过的」。

    这条对 §6.5「隔离区 = delete 的替代品,永不 delete」是直接的设计影响:
    P6 的执行器把一个人人可写的文件移进隔离区之后,它**还是人人可写** ——
    隔离区看着把东西关起来了,实际只是换了个位置。
    入区时必须 `icacls <目标> /reset /T`(丢掉显式 DACL、改回从隔离区继承)。
#>
$qRoot = $Paths['state.quarantine']
if ($qRoot -and (Test-Path -LiteralPath $qRoot)) {
    $entries = @(Get-ChildItem -LiteralPath $qRoot -Force -EA SilentlyContinue)
    if (-not $entries) {
        Write-Host '  SKIP  隔离区为空' -ForegroundColor DarkGray
    }
    foreach ($e in $entries) {
        $acl = Get-Acl -LiteralPath $e.FullName
        Test-It "★ 隔离区条目 $($e.Name) 继承隔离区 ACL(未自带显式 DACL)" `
            (-not $acl.AreAccessRulesProtected) `
            '(同卷 Move 会把原目录的 ACE 转成显式带过来 —— 入区时须 icacls /reset /T)'
        foreach ($who in $STRIP) {
            $w = Get-EffectiveWrite $e.FullName $who
            if ($null -eq $w) { continue }
            Test-It "  $($e.Name) :: $who 写不了" (-not $w) '(宽泛权限跟着数据搬进了隔离区)'
        }
    }
} else {
    Write-Host '  SKIP  paths.toml 无 state.quarantine 或目录不存在' -ForegroundColor DarkGray
}

Write-Host ''
Write-Host '⑥ 诚实边界:本套件【验不了】什么' -ForegroundColor DarkYellow
Write-Host '  · 审计文件与服务日志仍在同一目录 —— 能写服务日志的账户仍能改审计。' -ForegroundColor DarkGray
Write-Host '    真正的修法是独立的 append-only 审计目录(D71),不在本脚本范围内。' -ForegroundColor DarkGray
Write-Host '  · 本机管理员可以改回任何 ACL —— 对管理员,这一层不构成边界。' -ForegroundColor DarkGray

Write-Host ''
Write-Host ("=== {{state}} ACL 复核:{0} PASS · {1} FAIL ===" -f $pass, $fail) `
    -ForegroundColor $(if ($fail) { 'Red' } else { 'Green' })
if ($fail) { exit 1 }
