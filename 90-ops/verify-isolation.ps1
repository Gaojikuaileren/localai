<#
.SYNOPSIS
    账户隔离否定用例套件(§6.8)—— D22 之后这是记忆库的【主要】保护层。

.DESCRIPTION
    D21/D22 取消了静态加密,于是「谁能读到记忆库」不再靠密钥,全靠 OS 账户隔离。
    这个脚本把 §6.8 的隔离验证从「装的时候看一眼」变成【可天天跑的断言】。

    ★ 它验的是**结构性事实**,不需要真的以 ai-asset 身份运行:
      账户存在 · ACL 是真 Deny(不是空 Allow)· secrets 的两条性质 ·
      DB 角色分离 · 备份排除 —— 这些一旦被谁改动,这里立刻变红。

    ★ 有一类它【验不了】,并会诚实说出来:「以 ai-asset 身份实际去读记忆库会不会失败」
      需要那个账户的凭据来起一个进程,而本脚本不持有。它验的是「拦截该在的地方在不在」,
      不是「拦截运行时真的触发了」。后者属于 P3a S9 的恢复/隔离实攻演练。

.NOTES
    只读,不改任何东西。可反复跑。需要管理员权限才能读全 ACL。
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\verify-isolation.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$script:pass = 0
$script:fail = 0
$script:skip = 0

function Test-It { param([string]$Name, [bool]$Cond, [string]$Extra = '')
    if ($Cond) { $script:pass++; Write-Host "  PASS  $Name" -ForegroundColor Green }
    else       { $script:fail++; Write-Host "  FAIL  $Name  $Extra" -ForegroundColor Red }
}
function Skip-It { param([string]$Name, [string]$Why)
    $script:skip++; Write-Host "  SKIP  $Name — $Why" -ForegroundColor DarkGray }

# ---- 读 paths.toml(用 backup.ps1 的同一个极简解析器)------------------------
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$toml = Join-Path $repoRoot 'config\paths.toml'
$P = @{}; $sec = ''
foreach ($line in Get-Content -LiteralPath $toml -Encoding UTF8) {
    $l = $line.Trim()
    if ($l -eq '' -or $l.StartsWith('#')) { continue }
    if ($l -match '^\[([^\]]+)\]') { $sec = $Matches[1]; continue }
    if ($l -match "^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*'([^']*)'") { $P["$sec.$($Matches[1])"] = $Matches[2] }
}
$memDir     = $P['state.memory']
$secretsDir = $P['state.secrets']

Write-Host ''
Write-Host '=== 账户隔离否定用例(§6.8)===' -ForegroundColor Cyan

Write-Host ''
Write-Host '① 三个隔离账户存在且互相独立'
$accts = @{}
foreach ($a in 'ai-mem','ai-asset','ai-exec') {
    try { $u = Get-LocalUser -Name $a -ErrorAction Stop; $accts[$a] = $u.SID.Value
          Test-It "账户 $a 存在" $true }
    catch { Test-It "账户 $a 存在" $false '未创建 —— 跑 setup-accounts.ps1' }
}
if ($accts.Count -eq 3) {
    Test-It '★ 三个账户 SID 互不相同(不是同一个账户的别名)' (($accts.Values | Sort-Object -Unique).Count -eq 3)
}

# ---- ACL 断言:必须是真 Deny,不是空 Allow ----------------------------------
function Assert-DenyAcl { param([string]$Path, [string]$Label)
    if (-not (Test-Path $Path)) { Skip-It "$Label ACL" "目录不存在: $Path"; return }
    $acl = Get-Acl $Path
    Test-It "${Label}:继承已关闭(AreAccessRulesProtected)" $acl.AreAccessRulesProtected
    foreach ($who in 'ai-asset','ai-exec') {
        $deny = $acl.Access | Where-Object {
            $_.IdentityReference.Value -like "*\$who" -and $_.AccessControlType -eq 'Deny' }
        # ★ 关键:必须是 Deny ACE,而不是"没给 Allow"。空 Allow 会被别处的授权穿透。
        Test-It "★ ${Label}:${who} 是显式 Deny(不是空 Allow)" ([bool]$deny) `
            '§6.8 belt-and-suspenders:Deny 优先于任何 Allow'
    }
    $memAllow = $acl.Access | Where-Object {
        $_.IdentityReference.Value -like '*\ai-mem' -and $_.AccessControlType -eq 'Allow' }
    Test-It "${Label}:ai-mem 有 Allow(它得能用)" ([bool]$memAllow)
}

Write-Host ''
Write-Host '② 记忆库目录:ai-asset / ai-exec 被显式 Deny'
Assert-DenyAcl -Path $memDir -Label 'memory'

Write-Host ''
Write-Host '③ secrets 目录:强 ACL + 排除出备份(两条缺一不可)'
Assert-DenyAcl -Path $secretsDir -Label 'secrets'
# 第二条性质:必须在 backup.ps1 的排除清单里
$bk = Join-Path $PSScriptRoot 'backup\backup.ps1'
if (Test-Path $bk) {
    $bkSrc = Get-Content -LiteralPath $bk -Raw -Encoding UTF8
    Test-It '★★ secrets 排除出备份(强 ACL 挡在线读,排除挡拔盘)' ($bkSrc -match 'state\.secrets')
} else { Skip-It 'secrets 备份排除' 'backup.ps1 不存在' }

Write-Host ''
Write-Host '④ DB 角色分离:结构性事实(不越俎代庖去跑 verify.sql C 段)'
# ★ DB 层的隔离实攻由 verify.sql C 段完整覆盖(以 ai_mem_remote 身份跑一组
#   "期望 permission denied" 的查询)。那组用例需要完整 schema 重放 + 临时 pg_ident 映射,
#   不适合塞进这个只读运维脚本。
#   ★ 而且:用一个能 SET ROLE 的角色去模拟另一个角色,本身就削弱了测试 ——
#     实测发现 ai_mem_local 连 SET ROLE 到 ai_mem_remote 都不允许(这反而是隔离成立的
#     正面证据,但不是这里要测的东西)。
#   所以这里只做**能独立验证的结构性事实**:两个角色存在、且不是同一个;
#   完整的否定用例引导到 verify.sql。
# ★ psql 路径从 paths.toml 的 memory.pg_bin 读(§11.1:不硬编码绝对路径)
$psql = $null
$pgBin = $P['memory.pg_bin']
if ($pgBin -and (Test-Path (Join-Path $pgBin 'psql.exe'))) {
    $psql = Join-Path $pgBin 'psql.exe'
} elseif ($env:PGBIN -and (Test-Path (Join-Path $env:PGBIN 'psql.exe'))) {
    $psql = Join-Path $env:PGBIN 'psql.exe'
}
# ★ 端口从 paths.toml 读,不写死(2026-07-31 审计):全仓其它 psql 调用都读 memory.pg_port,
#   唯独这里破例写死 5432。端口一改,这道 DB 隔离检查会连不上 —— 而它是安全检查,静默失效最危险。
$pgPort = $P['memory.pg_port']; if (-not $pgPort) { $pgPort = '5432' }
if (-not $psql) { Skip-It 'DB 角色分离' "找不到 psql.exe(paths.toml 的 memory.pg_bin 未指向有效目录)" }
else {
    $q = @'
SELECT
  (SELECT count(*) FROM pg_roles WHERE rolname='ai_mem_local')  AS has_local,
  (SELECT count(*) FROM pg_roles WHERE rolname='ai_mem_remote') AS has_remote,
  (SELECT rolsuper::int FROM pg_roles WHERE rolname='ai_mem_remote') AS remote_super,
  (SELECT count(*) FROM information_schema.role_table_grants
     WHERE grantee='ai_mem_remote' AND table_schema='mem'
       AND table_name IN ('l3_fact','secret_ref','pending_review','write_ticket')) AS remote_basetable_grants;
'@
    # ★★ 2026-08-03 修:下面那句 Skip-It('连不上 —— 需以 ai-mem 身份运行')原本是**死代码**。
    #   第 25 行的 $ErrorActionPreference='Stop' 会把 native 命令(psql.exe)写到 stderr 的每一行
    #   包成 NativeCommandError 的 ErrorRecord ⇒ 终止错误 ⇒ 脚本在这一行直接中止,
    #   永远走不到下面的 if,后面的 ⑤⑥ 两段也跟着不执行。
    #   ——「写了一个优雅降级分支,它一次都不会跑」正是本项目最恨的形状(见 DECISIONS「假断言」整节)。
    #   实测:以机主身份跑本脚本,输出停在 ④ 的红字上,退出码却是 0。
    $out = ''
    $prevEap = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $out = ($q | & $psql -h 127.0.0.1 -p $pgPort -U ai_mem_local -d memory -t -A -F ',' 2>&1 | Out-String)
    } catch {
        $out = $_.Exception.Message
    } finally {
        $ErrorActionPreference = $prevEap
    }
    if ($out -match 'SSPI authentication failed|could not connect') {
        Skip-It 'DB 角色分离' '连不上 —— 需以 ai-mem 身份运行(生产即如此)'
    } else {
        $vals = ($out.Trim() -split "`n" | Where-Object { $_ -match '^\d' } | Select-Object -First 1) -split ','
        if ($vals.Count -ge 4) {
            Test-It '两个角色都存在'                 (($vals[0] -eq '1') -and ($vals[1] -eq '1'))
            Test-It '★ ai_mem_remote 不是超级用户'   ($vals[2] -eq '0')
            Test-It '★★ ai_mem_remote 对敏感基表零直接授权(只能经视图)' ($vals[3] -eq '0') `
                "实得 $($vals[3]) 处授权 —— 应为 0"
        } else {
            Skip-It 'DB 角色分离' "查询返回异常: $($out.Trim())"
        }
        Write-Host '    (完整的「远程读基表 → permission denied」否定用例见 verify.sql C 段)' -ForegroundColor DarkGray
    }
}

Write-Host ''
Write-Host '⑤ ★★ pg_ident.conf:SSPI 映射表(记忆库唯一那道不依赖 NTFS 的强闸)'
# ★ 为什么必须验这一条(2026-08-03 补):
#   PG 的闸是 `pg_hba: sspi map=mem` + `pg_ident: 只映 ai-mem`。它比 Qdrant 的 bearer key 更强 ——
#   SSPI 绑 Windows SID,**没有可窃取的秘密**;实测以【提权的机主账户】连 mem_rw 也是
#   `FATAL: SSPI authentication failed`。这是全项目对记忆库最硬的一道防线。
#
#   ★★ 但它此前【零看守】:
#     · 本脚本原来一个字都不读 pg_ident(唯一一处提及是 ④ 段的注释);
#     · install-postgres.ps1 的重跑守卫只 Select-String 检查 pg_hba,else 分支却同时写两个文件。
#   ⇒ 往 pg_ident.conf 追加一行 `mem <任意账户> mem_rw`,记忆库对那个账户全开,
#     而**全套断言没有一条会红**,并且会被一个从不读它的守卫永久保住。
#     这正是本项目最恨的形状:看着有防护、实际没有。
$pgData = $P['memory.pg_data']
if (-not $pgData) { $pgData = Join-Path $memDir 'pg\18\data' }
$identPath = Join-Path $pgData 'pg_ident.conf'
$hbaPath   = Join-Path $pgData 'pg_hba.conf'

if (-not (Test-Path -LiteralPath $identPath)) {
    Skip-It 'pg_ident.conf 映射表' "找不到 $identPath(PG 未安装?)"
} else {
    $identLines = @(Get-Content -LiteralPath $identPath -Encoding UTF8 |
                    ForEach-Object { $_.Trim() } |
                    Where-Object { $_ -and -not $_.StartsWith('#') })
    # 每行形如: MAPNAME  SYSTEM-USERNAME  PG-USERNAME
    $sysUsers = @($identLines | ForEach-Object { ($_ -split '\s+')[1] } | Sort-Object -Unique)
    Write-Host ("    实得 {0} 条映射,SYSTEM-USERNAME 列 = [{1}]" -f $identLines.Count, ($sysUsers -join ', '))

    # ★★ 反向全表断言:允许出现在 SYSTEM-USERNAME 列的 OS 账户【有且只有】ai-mem。
    #    不写成「检查 ai-mem 在不在」—— 那条在有人【追加】一行时不会响,而追加正是事故的形状。
    $unexpected = @($sysUsers | Where-Object { $_ -ne 'ai-mem' })
    Test-It '★★ pg_ident 的 SYSTEM-USERNAME 列有且只有 ai-mem(反向全表)' `
        ($unexpected.Count -eq 0) `
        ("多出来的 OS 账户: [{0}] —— 记忆库对它们全开" -f ($unexpected -join ', '))

    Test-It 'pg_ident 至少有一条映射(空表 = PG 谁都连不上)' ($identLines.Count -ge 1)

    # pg_hba 侧:不得出现 trust / md5 / password 这类不绑 SID 的方法
    if (Test-Path -LiteralPath $hbaPath) {
        $hbaLines = @(Get-Content -LiteralPath $hbaPath -Encoding UTF8 |
                      ForEach-Object { $_.Trim() } |
                      Where-Object { $_ -and -not $_.StartsWith('#') })
        $weak = @($hbaLines | Where-Object { $_ -match '\b(trust|md5|password)\b' })
        Test-It '★ pg_hba 无 trust/md5/password 兜底行(只认 SSPI 绑 SID)' `
            ($weak.Count -eq 0) ("弱认证行: {0}" -f ($weak -join ' | '))
        $noMap = @($hbaLines | Where-Object { $_ -match '\bsspi\b' -and $_ -notmatch 'map=' })
        Test-It '★ 每条 sspi 行都带 map=(不带 map 等于放行任意 Windows 账户)' `
            ($noMap.Count -eq 0) ("无 map 的行: {0}" -f ($noMap -join ' | '))
    } else {
        Skip-It 'pg_hba 认证方法' "找不到 $hbaPath"
    }

    # ★ 诚实降级:第一行把 ai-mem 映到 PG 超级用户 postgres,而超级用户绕过一切权限检查
    #   ⇒ roles.sql 的 REVOKE UPDATE, DELETE 只对 ai_mem_local/remote 成立,
    #     不得声称 mem.audit_log 的 append-only 是绝对的。
    $toSuper = @($identLines | Where-Object { ($_ -split '\s+')[2] -eq 'postgres' })
    if ($toSuper.Count -gt 0) {
        Write-Host ('    ⚠ 有 {0} 条映射指向 PG 超级用户 postgres —— 超级用户绕过一切权限检查,' -f $toSuper.Count) -ForegroundColor DarkYellow
        Write-Host '      故 roles.sql 的 REVOKE 只对非超级角色成立(文档不得声称 append-only 绝对)' -ForegroundColor DarkYellow
    }
}

Write-Host ''
Write-Host '⑥ 诚实边界(§12.3:说清这个套件【验不了】什么)' -ForegroundColor DarkYellow
Skip-It '以 ai-asset 身份实际读记忆库会失败' `
    '需该账户凭据起进程 —— 本套件验"拦截该在的地方在不在",运行时实攻属 S9 演练'
Skip-It '越权尝试触发 §9.3 告警' `
    '告警通道待 P7 客户端 v2;当前只落审计文件(STATE 已记 🟡)'

Write-Host ''
Write-Host ("=== 隔离验证:{0} PASS · {1} FAIL · {2} SKIP ===" -f $pass, $fail, $skip) `
    -ForegroundColor $(if ($fail) { 'Red' } else { 'Green' })
if ($fail) { exit 1 }
