<#
.SYNOPSIS
    建立 {state}\secrets —— 长期第三方凭据与私钥的唯一落点。

.DESCRIPTION
    这个目录的两条性质缺一不可,少任何一条它就没有意义:

      ① 强 ACL      —— 挡【在线读取】(ai-mem 独占;ai-asset / ai-exec 显式 Deny)
      ② 排除出备份  —— 挡【离线拷贝】(已在 backup.ps1 的 $excludeAbs 里落实)

    ★ 为什么 ② 不可省:{state} 就是备份根。backup.ps1 会把它整根复制到 G:
      (SanDisk · USB · exFAT · **不加密**,D21)。只做强 ACL 的凭据,会被逐字
      复制到一块随身盘上 —— ACL 挡得住在线读取,挡不住把盘拔走。

      D22 已经接受「丢盘 = 可读全部记忆」,但那笔账**只算了记忆**。
      这里要放的是客户端 CA 私钥与外联通道的长期凭据,丢掉它们的后果不是
      「被读」,是**被接管**:签发新的成员设备、以你的身份对外发消息。
      这是另一个量级的事,不能沿用 D22 的结论。
      (同一条推理此前已用在 qdrant\config 上 —— 那里也是排除出备份、恢复时重新配发。)

    ★ 恢复语义:这些东西**不在备份里**。换机 / 恢复之后必须重新签发、重新配对、
      重新链接设备 —— 这正是「换主机 = 所有客户端重新配对」那条安全性质的物理载体。
      如果它能无感恢复,那么攻击者伪装成新主机也能无感完成。

.NOTES
    需要管理员权限(改 NTFS ACL)。
    前置:ai-mem / ai-asset / ai-exec 三账户已存在(setup-accounts.ps1)。

.EXAMPLE
    .\setup-secrets-dir.ps1
    .\setup-secrets-dir.ps1 -WhatIf     # 只看会做什么,不动手
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param()

$ErrorActionPreference = 'Stop'

function Say { param([string]$m, [string]$c = 'Gray'); Write-Host $m -ForegroundColor $c }

# ---- 从 paths.toml 读落点(§11.1 禁止硬编码绝对路径)------------------------
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tomlPath = Join-Path $repoRoot 'config\paths.toml'
if (-not (Test-Path $tomlPath)) { throw "找不到路径配置源: $tomlPath" }

function Get-PathKey {
    param([string]$Section, [string]$Key)
    $sec = ''
    foreach ($line in Get-Content -LiteralPath $tomlPath -Encoding UTF8) {
        $l = $line.Trim()
        if ($l -eq '' -or $l.StartsWith('#')) { continue }
        if ($l -match '^\[([^\]]+)\]') { $sec = $Matches[1]; continue }
        if ($sec -eq $Section -and $l -match "^$([regex]::Escape($Key))\s*=\s*'([^']*)'") {
            return $Matches[1]
        }
    }
    throw "paths.toml 缺键: [$Section] $Key"
}

$secretsDir = Get-PathKey -Section 'state' -Key 'secrets'

Say ''
Say '=== {state}\secrets — 凭据与私钥的唯一落点 ===' 'Cyan'
Say ("  路径(来自 paths.toml): {0}" -f $secretsDir)

# ---- 前置核验:三账户必须已存在 ---------------------------------------------
foreach ($acct in @('ai-mem', 'ai-asset', 'ai-exec')) {
    try { Get-LocalUser -Name $acct -ErrorAction Stop | Out-Null }
    catch { throw "本地账户 $acct 不存在 —— 请先跑 90-ops\setup-accounts.ps1。" }
}
Say '  ✓ 三个隔离账户都在' 'Green'

# ---- 建目录 ------------------------------------------------------------------
if (-not (Test-Path $secretsDir)) {
    if ($PSCmdlet.ShouldProcess($secretsDir, '新建目录')) {
        New-Item -ItemType Directory -Force -Path $secretsDir | Out-Null
        Say ("  + 已建目录: {0}" -f $secretsDir) 'Green'
    }
} else {
    Say ("  = 目录已存在: {0}" -f $secretsDir)
}

# ---- ACL:与 setup-accounts.ps1 对 memory 目录的做法保持一致 -----------------
if ($PSCmdlet.ShouldProcess($secretsDir, '关继承 + 只留 ai-mem/Administrators/SYSTEM + 显式拒绝资产侧')) {
    Say '  设 ACL(关继承 · 只留 ai-mem/Administrators/SYSTEM · 显式拒绝 ai-asset/ai-exec)...'
    & icacls $secretsDir /inheritance:r | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls 关闭继承失败: $secretsDir" }
    & icacls $secretsDir /grant:r "ai-mem:(OI)(CI)F" "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls 授权失败: $secretsDir" }
    # 显式 Deny 是 §6.8 明写的 belt-and-suspenders:即使将来有人误加了授权,Deny 优先
    & icacls $secretsDir /deny "ai-asset:(OI)(CI)F" "ai-exec:(OI)(CI)F" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls 拒绝失败: $secretsDir" }
    Say '  ✓ ACL 已设' 'Green'
}

# ---- 放一份说明进去,免得将来有人往里塞该进备份的东西 ------------------------
$readme = Join-Path $secretsDir 'README.txt'
if (-not (Test-Path $readme) -and $PSCmdlet.ShouldProcess($readme, '写入说明')) {
    $txt = @'
这个目录【不进备份】。

放这里的东西:客户端 CA 私钥、外联通道的长期凭据、任何"泄露即被接管"的密钥物料。
不要放这里的东西:任何丢了就再也拿不回来的数据(那些属于记忆库,要进备份)。

判据很简单:
  · 丢了会被【接管】的  -> 放这里(不进备份,换机后重新签发/配对)
  · 丢了会被【读到】但可重建的 -> 不放这里

换机 / 恢复之后,这里是空的。这是设计,不是故障:
必须重新签发客户端证书、重新配对所有设备、重新链接外联通道。
如果换主机能无感完成,那么攻击者伪装成新主机也能无感完成。
'@
    Set-Content -LiteralPath $readme -Value $txt -Encoding UTF8
    Say ("  + 已写说明: {0}" -f $readme) 'Green'
}

# ---- 核对 --------------------------------------------------------------------
Say ''
Say '=== 核对 ===' 'Cyan'
Say '  ACL:'
(& icacls $secretsDir) | Select-Object -SkipLast 1 | ForEach-Object { "    $_" }

Say ''
Say '  备份排除(应当能在 backup.ps1 的 $excludeAbs 里看到这个路径):'
$bk = Join-Path $PSScriptRoot 'backup\backup.ps1'
if ((Get-Content -LiteralPath $bk -Raw -Encoding UTF8) -match "state\.secrets") {
    Say "    ✓ backup.ps1 已引用 state.secrets" 'Green'
} else {
    Say "    X backup.ps1 未引用 state.secrets —— 强 ACL 单独存在没有意义,请修!" 'Red'
}

Say ''
Say '=== 完成 ===' 'Green'
Say '  回去跟 Claude 说「secrets 目录建好了」。'
