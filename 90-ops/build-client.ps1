# build-client.ps1 — 出一份可以拿到另一台机器上装的客户端产物(P3c 验收第一句)。
#
# ★ 这不是「安装器」,是【可分发产物】。区别写在这儿,免得以后有人拿它当 MSI 用:
#   · 它做的:单文件 exe + SHA256 + 版本戳 + 一页安装说明,打成一个目录(可整包拷走/压缩)。
#   · 它不做的:注册表写入、开机自启注册、卸载项、升级替换、数字签名。
#     那些属于真正的安装器,归 P7/P11 —— 现在做等于在没有升级策略的时候先造一个升级问题。
#   · 客户端自己已经能处理开机自启(HKCU,普通用户权限,见 Services/Autostart.cs),
#     所以「装」= 把这个目录放到你想放的位置、双击一次。这条要写进说明里,不能让人猜。
#
# 用法:  pwsh -File 90-ops\build-client.ps1 [-Out <目录>]
# 产物:  <Out>\localai-client.exe · SHA256.txt · VERSION.txt · 安装说明.txt

param(
    [string]$Out = ""
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$app  = Join-Path $repo '20-client-win\app'
if (-not (Test-Path $app)) { Write-Host "X 找不到客户端工程:$app" -ForegroundColor Red; exit 1 }

# 版本戳 = 提交短哈希 + 构建时刻。★ 不编版本号:没有发布流程之前,编出来的号只会互相矛盾。
Push-Location $repo
$sha = (& git rev-parse --short HEAD 2>$null)
$dirty = (& git status --porcelain 2>$null)
Pop-Location
if (-not $sha) { $sha = 'nogit' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$ver = "$stamp+$sha"
if ($dirty) { $ver = "$ver.dirty" }   # ★ 工作树不干净就写在版本里 —— 别让人拿到一个说不清来源的包

if (-not $Out) { $Out = Join-Path $repo "dist\client-pack" }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

Write-Host "[1] 发布单文件 exe(自包含,win-x64)…"
Push-Location $app
& dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $Out --nologo -v q
$code = $LASTEXITCODE
Pop-Location
if ($code -ne 0) { Write-Host "X 发布失败" -ForegroundColor Red; exit 1 }

$exe = Join-Path $Out 'localai-client.exe'
if (-not (Test-Path $exe)) { Write-Host "X 没有产出 exe" -ForegroundColor Red; exit 1 }

Write-Host "[2] 自检（发布产物本身跑一遍）…"
# ★ 门禁看【退出码】而不是 stdout：客户端是 WinExe，自检靠 AttachConsole 把字写到
#   【调用者的控制台】上，PowerShell 的管道根本接不到 —— 拿 $()抓它会得到空字符串。
#   Selftest.Run() 的契约是：有一条红就返回 1。那才是可靠的那一位。
& $exe --selftest
$code = $LASTEXITCODE
if ($code -ne 0) {
    Write-Host ""
    Write-Host "X 发布产物自检没过（退出码 $code）—— 上面就是它的输出。" -ForegroundColor Red
    Write-Host "  ★ 不出包：自检红着还打包，等于把已知坏的东西送到另一台机器上。" -ForegroundColor Red
    exit 1
}
$last = "自检通过（退出码 0）"
Write-Host "    $last"

Write-Host "[3] 校验和与版本戳…"
$hash = (Get-FileHash -Algorithm SHA256 $exe).Hash
Set-Content -Path (Join-Path $Out 'SHA256.txt') -Encoding utf8 -Value "$hash  localai-client.exe"
Set-Content -Path (Join-Path $Out 'VERSION.txt') -Encoding utf8 -Value @"
localai-client
版本戳: $ver
自检:   $last
构建于: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
"@

Set-Content -Path (Join-Path $Out '安装说明.txt') -Encoding utf8 -Value @"
本地 AI 中枢 · 客户端
版本戳 $ver

【怎么装】
把整个目录拷到你想放的位置(随便哪个盘下建一个 LocalAI\client 之类的目录即可),
双击 localai-client.exe 就能用。
不需要管理员权限,不写系统目录,不改注册表以外的任何东西。

【第一次打开】
它会问你要中枢的连接地址,并做一次配对(六个词的短语要与主机上显示的逐字一致才按"确认")。
配对只做一次 —— 以后启动自动连接。

【换了路由器/网段】
不要"解除配对"。打开 系统 → 设备,在已配对卡片里直接【改地址】即可;
证书与配对原样保留。(自动发现要等 P3b.2。)

【开机自启】
在客户端的设置里开关,写的是当前用户的启动项(HKCU),不需要管理员。

【校验】
SHA256.txt 里是这份 exe 的哈希。从别处拿到这个包时先对一遍。

【已知边界(如实说)】
· AI 模型尚未接入(P4):聊天会记录但不会有回答,记忆库不会产生内容。
· 界面目前只有中文;中/日/英三语已裁定移到 P7。
· 这不是安装器:没有卸载项、没有自动升级。删除 = 直接删这个目录。
"@

Write-Host ""
Write-Host "=== 出包完成 ===" -ForegroundColor Green
Write-Host "  $Out"
Write-Host "  版本戳 $ver"
Write-Host "  SHA256 $hash"
