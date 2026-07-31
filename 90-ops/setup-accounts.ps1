# =============================================================================
#  setup-accounts.ps1 — P2 账户隔离(§6.8)
#  在【你自己的管理员 PowerShell】里跑,不要贴进 Claude 的工具里执行。
#
#  做三件事:
#    1. 建三个低权限服务账户:ai-mem / ai-asset / ai-exec
#    2. 建记忆库目录(路径取自 config/paths.toml 的 [state] memory)并设 NTFS ACL:
#       只有 ai-mem + 管理员能进,显式拒绝 ai-asset / ai-exec(§6.8:D22 后这是主要保护层)
#    3. 打印结果供核对
#
#  ★ 密码:脚本内生成随机密码建号,【不显示、不保存】—— 装 PostgreSQL/Qdrant 时
#     会把 ai-mem 的密码重置为新随机值并当场配进服务(密码只活在那一次运行里)。
#     所以这里不产生任何需要你记的密码。
#
#  幂等:已存在的账户/ACL 会跳过或覆盖,可重复运行。
# =============================================================================

$ErrorActionPreference = 'Stop'

# --- 必须管理员 ---
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
  Write-Host "  ✗ 需要管理员权限。右键 PowerShell → 以管理员身份运行,再跑本脚本。" -ForegroundColor Red
  exit 1
}

function New-RandomPassword {
  # 24 字符,含大小写数字符号 —— 服务账户用,人不需要记
  $bytes = New-Object byte[] 32
  [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
  return [Convert]::ToBase64String($bytes) + '!Aa9'
}

$accounts = @(
  @{ Name='ai-mem';   Desc='LocalAI mem: PG/Qdrant/embed (6.8)' },
  @{ Name='ai-asset'; Desc='LocalAI asset: ComfyUI/game (6.8)' },
  @{ Name='ai-exec';  Desc='LocalAI exec: browser/file/cred (6.9.5)' }
)

Write-Host "=== 1. 建服务账户 ===" -ForegroundColor Cyan
foreach ($a in $accounts) {
  $existing = Get-LocalUser -Name $a.Name -ErrorAction SilentlyContinue
  if ($existing) {
    Write-Host ("  = 已存在: {0}" -f $a.Name)
    continue
  }
  $pw = ConvertTo-SecureString (New-RandomPassword) -AsPlainText -Force
  New-LocalUser -Name $a.Name -Password $pw -Description $a.Desc `
    -PasswordNeverExpires -UserMayNotChangePassword -AccountNeverExpires | Out-Null
  # 不加入任何组(默认就不在 Users)—— 最小权限
  Write-Host ("  + 已建: {0}" -f $a.Name) -ForegroundColor Green
}

Write-Host ""
Write-Host "=== 2. 记忆库目录 + ACL ===" -ForegroundColor Cyan
# §11.1:不硬编码路径,从唯一路径源 config/paths.toml 读 memory 路径(换盘只改那一处)
$pathsToml = Join-Path $PSScriptRoot '..\config\paths.toml'
if (-not (Test-Path $pathsToml)) {
  Write-Host ("  ✗ 找不到 {0} —— 请在项目内运行本脚本" -f $pathsToml) -ForegroundColor Red
  exit 1
}
$memLine = Select-String -Path $pathsToml -Pattern "^\s*memory\s*=\s*'([^']+)'" | Select-Object -First 1
if (-not $memLine) {
  Write-Host "  ✗ paths.toml 里没找到 [state] memory 项" -ForegroundColor Red
  exit 1
}
$memDir = $memLine.Matches[0].Groups[1].Value
Write-Host ("  memory 路径(来自 paths.toml): {0}" -f $memDir)
if (-not (Test-Path $memDir)) {
  New-Item -ItemType Directory -Force -Path $memDir | Out-Null
  Write-Host ("  + 已建目录: {0}" -f $memDir)
} else {
  Write-Host ("  = 目录已存在: {0}" -f $memDir)
}

# 关闭继承(把继承的 ACE 复制为显式,随后清理),然后只留 ai-mem + 管理员 + SYSTEM
Write-Host "  设 ACL(关继承 · 只留 ai-mem/Administrators/SYSTEM · 显式拒绝 ai-asset/ai-exec)..."
# ★ 每条 icacls 都查退出码(2026-07-31 审计):原来三条都不查,失败照样绿字「✓ ACL 已设」——
#   而 ACL 没设成 = 记忆库对 ai-asset/ai-exec 敞开,正是这个脚本唯一要防的事。
#   照抄 setup-secrets-dir.ps1 已有的写法。
& icacls $memDir /inheritance:r | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls 关闭继承失败: $memDir" }
& icacls $memDir /grant:r "ai-mem:(OI)(CI)F" "Administrators:(OI)(CI)F" "SYSTEM:(OI)(CI)F" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls 授权失败: $memDir" }
# 显式拒绝资产侧(即使不授予,拒绝 ACE 是 §6.8 明写的 belt-and-suspenders)
& icacls $memDir /deny "ai-asset:(OI)(CI)F" "ai-exec:(OI)(CI)F" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls 拒绝失败: $memDir" }
Write-Host "  ✓ ACL 已设" -ForegroundColor Green

Write-Host ""
Write-Host "=== 3. 核对 ===" -ForegroundColor Cyan
Write-Host "  账户:"
Get-LocalUser -Name 'ai-*' | ForEach-Object { "    {0}  (Enabled={1})" -f $_.Name, $_.Enabled }
Write-Host ""
Write-Host "  memory 目录 ACL:"
(& icacls $memDir) | Select-Object -SkipLast 1 | ForEach-Object { "    $_" }

Write-Host ""
Write-Host "=== 完成 ===" -ForegroundColor Green
Write-Host "  回去跟 Claude 说「账户建好了」,它会核验并继续装 PostgreSQL + Qdrant 到 ai-mem 下。"
Write-Host "  (装库时会重置 ai-mem 密码为新随机值并配进服务,你不用管密码)"
