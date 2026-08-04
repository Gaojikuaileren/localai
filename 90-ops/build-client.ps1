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

# ★★ 2026-08-04:工作树不干净时,光写 `.dirty` 是【不够】的 —— 那个戳把人指向 $sha,
#    而 exe 里装的根本不是 $sha 的代码。实际吃过:03:40 那份 exe 戳着 c37a890,
#    可它携带的两条修复在 d2c941c,按戳去翻 c37a890 一个字都找不到。
#    ⇒ 脏树构建必须带上【工作树本身的指纹】,让这份 exe 能被唯一识别:
#      指纹 = 对 porcelain 清单 + 每个脏文件(含**未跟踪**文件,它们照样会被编进去)的内容哈希
#      再取一次哈希。同样的工作树 → 同样的指纹;改一个字 → 指纹变。
$dirtyFiles = @()
if ($dirty) {
    Push-Location $repo
    $dirtyFiles = @($dirty -split "`r?`n" | Where-Object { $_ -match '\S' })
    $acc = New-Object System.Text.StringBuilder
    foreach ($line in ($dirtyFiles | Sort-Object)) {
        [void]$acc.AppendLine($line)
        # porcelain 的路径从第 4 个字符起(前两位是状态、第三位是空格);带引号的路径去掉引号
        $rel = $line.Substring(3).Trim().Trim('"')
        if ($rel -match ' -> ') { $rel = ($rel -split ' -> ')[-1] }   # 重命名取新名
        if (Test-Path -LiteralPath $rel -PathType Leaf) {
            [void]$acc.AppendLine((Get-FileHash -LiteralPath $rel -Algorithm SHA256).Hash)
        }
    }
    Pop-Location
    $bytes  = [Text.Encoding]::UTF8.GetBytes($acc.ToString())
    $sha256 = [Security.Cryptography.SHA256]::Create()
    $treeId = ([BitConverter]::ToString($sha256.ComputeHash($bytes)) -replace '-','').Substring(0,8).ToLower()
    $sha256.Dispose()
    $ver = "$ver.dirty-$treeId"
}

if (-not $Out) { $Out = Join-Path $repo "dist\client-pack" }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

Write-Host "[1] 发布单文件 exe(自包含,win-x64)…"
Push-Location $app
# ★ 把版本戳烧进程序集 —— 客户端才能自报"我是哪一版"。
#   不烧的话,版本只存在 VERSION.txt 里,而那个文件拷丢了就永远说不清手上这个 exe 是什么。
& dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:InformationalVersion=$ver `
    -o $Out --nologo -v q
$code = $LASTEXITCODE
Pop-Location
if ($code -ne 0) { Write-Host "X 发布失败" -ForegroundColor Red; exit 1 }

$exe = Join-Path $Out 'localai-client.exe'
if (-not (Test-Path $exe)) { Write-Host "X 没有产出 exe" -ForegroundColor Red; exit 1 }

Write-Host "[2] 自检（发布产物本身跑一遍）…"
# ★★ 2026-08-04 重写判据。原先只看 $LASTEXITCODE，而那一位【分不出】两件事：
#      「自检跑完且全绿」 与 「exe 根本没启动」。
#    实测栽过一次(worklog 2026-08-04)：第二形状因文件被占用(error 32,刚 Copy-Item 完就跑,
#    多半是杀软持锁)连 bundle 都没映射上,一条断言都没跑,门禁却打印「两种安装位置均通过」并出包。
#    ⇒ 判据改成【跑过的证据】而不是【没有失败的迹象】:
#      要求自检写出哨兵文件(只可能由 Selftest.Run 最后一行写出),且 FAIL=0、PASS>0,
#      三条同时成立才算过。没有哨兵 = 没跑 = 红,与退出码无关。
#    ★ 客户端是 WinExe,自检靠 AttachConsole 把字写到【调用者的控制台】,PowerShell 管道接不到
#      —— 所以不能靠抓 stdout 来判,哨兵文件正是为此。
function Invoke-GateSelftest {
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [Parameter(Mandatory)][string]$Label,
        [int]$MaxAttempts = 3
    )
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $sentinel = Join-Path ([IO.Path]::GetTempPath()) ("localai-selftest-" + [Guid]::NewGuid().ToString('N') + ".txt")
        $stdout   = "$sentinel.out"
        Remove-Item $sentinel, $stdout -Force -ErrorAction SilentlyContinue
        $env:LOCALAI_SELFTEST_SENTINEL = $sentinel
        try {
            # ★★ 必须用 Start-Process -Wait,【不能】用 `& $exe`。
            #   客户端是 WinExe(GUI 子系统),PowerShell 的调用运算符对这类进程**不等待**,
            #   当场就返回 —— 于是 $LASTEXITCODE 拿到的是【上一条命令】的残留值。
            #   ⇒ 原来那道门禁根本没在读自检结果,它读的是一个巧合为 0 的旧值。
            #     这是 2026-08-04 假通过的**真正**根因(比"退出码分不出没启动"更靠下一层)。
            #   -RedirectStandardOutput:新进程没有可 AttachConsole 的父控制台,
            #     输出必须重定向到文件才留得住(自检失败时要能看见是哪条红)。
            $proc = Start-Process -FilePath $ExePath -ArgumentList '--selftest' `
                                  -PassThru -Wait -WindowStyle Hidden -RedirectStandardOutput $stdout
            $code = $proc.ExitCode
        } finally {
            Remove-Item Env:\LOCALAI_SELFTEST_SENTINEL -ErrorAction SilentlyContinue
        }

        # 自检的输出:失败时必须摆出来,否则门禁只会说"红了"而不说红在哪。
        $outText = if (Test-Path $stdout) { (Get-Content $stdout -Raw -Encoding UTF8) } else { '' }
        function Show-Out {
            if ($outText -match '\S') {
                Write-Host "  ---- 自检输出(末 25 行) ----" -ForegroundColor DarkGray
                ($outText -split "`r?`n" | Where-Object { $_ -match '\S' } | Select-Object -Last 25) |
                    ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
            } else {
                Write-Host "  ---- 自检没有任何输出(它多半没跑起来) ----" -ForegroundColor DarkGray
            }
        }

        if (-not (Test-Path $sentinel)) {
            # 没有哨兵 = 自检【没跑完】。error 32(文件被占用)是暂态,值得重试;
            # 但重试用尽仍没有,就必须判红 —— 绝不当作通过。
            if ($attempt -lt $MaxAttempts) {
                Write-Host "    ! $Label 没留下哨兵(自检没跑起来),$attempt/$MaxAttempts 重试…" -ForegroundColor Yellow
                Remove-Item $stdout -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 2
                continue
            }
            Write-Host ""
            Write-Host "X $Label：自检【根本没跑起来】（$MaxAttempts 次都没有哨兵，退出码 $code）。" -ForegroundColor Red
            Write-Host "  ★ 这正是 2026-08-04 那次假通过的形状：退出码看着是 0，但一条断言都没执行。" -ForegroundColor Red
            Write-Host "    常见成因：exe 被占用(error 32，杀软扫描持锁) / bundle 映射失败 / 进程被杀。" -ForegroundColor Red
            Show-Out
            Remove-Item $stdout -Force -ErrorAction SilentlyContinue
            return $null
        }

        $text = (Get-Content $sentinel -Raw).Trim()
        Remove-Item $sentinel -Force -ErrorAction SilentlyContinue
        if ($text -notmatch 'PASS=(\d+)\s+FAIL=(\d+)') {
            Write-Host "X $Label：哨兵内容不认得（'$text'）—— 不猜，判红。" -ForegroundColor Red
            Show-Out; Remove-Item $stdout -Force -ErrorAction SilentlyContinue
            return $null
        }
        $p = [int]$Matches[1]; $f = [int]$Matches[2]

        if ($f -ne 0) {
            Write-Host ""
            Write-Host "X $Label：自检有 $f 条红（PASS=$p）。" -ForegroundColor Red
            Write-Host "  ★ 不出包：自检红着还打包，等于把已知坏的东西送到另一台机器上。" -ForegroundColor Red
            if ($outText -match '\S') {
                Write-Host "  ---- 红的那几条 ----" -ForegroundColor Red
                ($outText -split "`r?`n" | Where-Object { $_ -match '^\s*FAIL' }) |
                    ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
            }
            Remove-Item $stdout -Force -ErrorAction SilentlyContinue
            return $null
        }
        if ($p -le 0) {
            Write-Host "X $Label：PASS=0 —— 跑是跑了，但一条都没断言到，等于没测。" -ForegroundColor Red
            Show-Out; Remove-Item $stdout -Force -ErrorAction SilentlyContinue
            return $null
        }
        if ($code -ne 0) {
            # 哨兵说全绿而退出码非 0：两个来源打架，一律判红并说清楚，绝不挑一个信。
            Write-Host "X $Label：哨兵说 PASS=$p FAIL=0，退出码却是 $code —— 两份账对不上，判红。" -ForegroundColor Red
            Show-Out; Remove-Item $stdout -Force -ErrorAction SilentlyContinue
            return $null
        }
        Remove-Item $stdout -Force -ErrorAction SilentlyContinue
        Write-Host "    $Label：PASS=$p FAIL=0"
        return $p
    }
}

$pass1 = Invoke-GateSelftest -ExePath $exe -Label '发布产物原位'
if ($null -eq $pass1) { exit 1 }

# ★ 再跑一遍【第二种目录形状】—— 客户端真正装的位置是 dist\client,那儿 dist\host 就在旁边,
#   而这里的 client-pack 旁边没有。只跑一种形状,"依赖安装位置"的断言就会溜过门禁:
#   2026-08-03 真的溜过一次(HostToolsDir 的断言在 client-pack 绿、装到 dist\client 红)。
#   代价是一次 exe 拷贝,换的是"断言必须与装在哪儿无关"这条被机械地钉住。
$shape = Join-Path ([IO.Path]::GetTempPath()) ("localai-gate-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path (Join-Path $shape 'client') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $shape 'host')   | Out-Null
Copy-Item $exe (Join-Path $shape 'client\localai-client.exe') -Force
Set-Content -Path (Join-Path $shape 'host\localai-lan-edge.exe') -Value 'stub' -Encoding utf8
$pass2 = Invoke-GateSelftest -ExePath (Join-Path $shape 'client\localai-client.exe') -Label '换个安装位置'
Remove-Item $shape -Recurse -Force -ErrorAction SilentlyContinue
if ($null -eq $pass2) {
    Write-Host "  ★ 若是断言红:说明有断言在断言【它自己跑在哪个目录下】,而不是断言代码的行为。" -ForegroundColor Red
    Write-Host "    把那条断言改成与位置无关(例如把纯逻辑抽出来、拿临时目录做两向测试)。" -ForegroundColor Red
    exit 1
}
$last = "自检通过（两种安装位置,均有哨兵佐证:PASS=$pass1 / PASS=$pass2,FAIL=0）"
Write-Host "    $last"

Write-Host "[3] 校验和与版本戳…"
$hash = (Get-FileHash -Algorithm SHA256 $exe).Hash
Set-Content -Path (Join-Path $Out 'SHA256.txt') -Encoding utf8 -Value "$hash  localai-client.exe"
$verNote = ""
if ($dirtyFiles.Count -gt 0) {
    # ★ 脏树构建:必须把「这份 exe 里多了/少了什么」摊开写,否则 .dirty 只是个免责声明,
    #   拿到包的人(包括三个月后的自己)仍然说不清手上这个二进制到底是什么代码。
    $verNote = @"

★ 本 exe 是在【工作树不干净】的情况下构建的 —— 版本戳里的提交 $sha
   【不能】完整解释它的内容。工作树指纹已附在版本戳末尾(.dirty-xxxxxxxx)。
   构建时这些文件与该提交不一致(git status --porcelain 原样):
$($dirtyFiles | ForEach-Object { "     $_" } | Out-String)
"@
}
Set-Content -Path (Join-Path $Out 'VERSION.txt') -Encoding utf8 -Value @"
localai-client
版本戳: $ver
自检:   $last
构建于: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
$verNote
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
