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
    [string]$Out = "",
    # V14b:管理端的产物目录。★ 默认与客户端产物**并排**(见 [3]/[4] 那两段的理由)。
    [string]$AdminOut = ""
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

        # ══════════════════════════════════════════════════════════════════
        #  ★★★ 把【口径】印出来(2026-08-06,审计 C3 复查带出来的)
        #
        #  发布产物旁边**没有源码** ⇒ Selftest 里所有 `TryReadSource` 落空,
        #  而调用方一律是 `if (src is not null) { Assert… }` ⇒ **整段跳过**:
        #  不计 PASS、不计 FAIL、**也不计 SKIP**。
        #  实测:同一份源码,开发树产物 1901、发布产物 852 —— **1049 条静默消失**,
        #  而这一行读起来像是「这个产物通过了自检」。
        #  ⇒ 一个和基线对不上、又没说自己在量什么的数字,**比不打印更坏**。
        #
        #  ★ 不在这里写死那个基线数 —— 写死了它就会过期,而过期的数字正是本条要治的病。
        #    要对账去看 STATE 的基线行。
        #  ★★ 取值顺序:$Matches 是**整个换掉**的,不是累加 ——
        #    $p/$f 必须在上面先取走(已经是),这里再逐个匹配。
        # ══════════════════════════════════════════════════════════════════
        $srcHit = -1; $srcMiss = -1
        if ($text -match 'SRCHIT=(\d+)')  { $srcHit  = [int]$Matches[1] }
        if ($text -match 'SRCMISS=(\d+)') { $srcMiss = [int]$Matches[1] }

        Write-Host "    $Label：PASS=$p FAIL=0"
        if ($srcMiss -lt 0) {
            Write-Host "      ! 哨兵里没有 SRCMISS —— 这份 exe 比本脚本旧(自检还没带口径)。" -ForegroundColor Yellow
            Write-Host "        ★ 不判红,但这个 PASS 数【口径不明】,不要拿它和基线比。" -ForegroundColor Yellow
        } elseif ($srcMiss -gt 0) {
            Write-Host "      ★ 口径：$srcMiss 处源码读不到（命中 $srcHit 处）⇒ 那些【结构/接线】断言整段没跑，" -ForegroundColor Yellow
            Write-Host "        既不计 PASS、也不计 FAIL、更不计 SKIP。发布产物旁边没有源码，这是设计如此；" -ForegroundColor Yellow
            Write-Host "        但它意味着 **这个数不能和开发树的基线直接比**（基线见 00-docs\STATE.md）。" -ForegroundColor Yellow
        } else {
            Write-Host "      ★ 口径：源码全部读得到（命中 $srcHit 处，落空 0）—— 与开发树同量程。" -ForegroundColor DarkGray
        }
        $script:LastSrcHit = $srcHit; $script:LastSrcMiss = $srcMiss
        return $p
    }
}

$pass1 = Invoke-GateSelftest -ExePath $exe -Label '发布产物原位'
$hit1 = $script:LastSrcHit; $miss1 = $script:LastSrcMiss
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
$hit2 = $script:LastSrcHit; $miss2 = $script:LastSrcMiss
Remove-Item $shape -Recurse -Force -ErrorAction SilentlyContinue
if ($null -eq $pass2) {
    Write-Host "  ★ 若是断言红:说明有断言在断言【它自己跑在哪个目录下】,而不是断言代码的行为。" -ForegroundColor Red
    Write-Host "    把那条断言改成与位置无关(例如把纯逻辑抽出来、拿临时目录做两向测试)。" -ForegroundColor Red
    exit 1
}
# ══════════════════════════════════════════════════════════════════════════════
#  [3][4] 管理端(V14b)—— 在此之前本脚本**零命中 admin**:那个程序编得过、门禁也编它,
#         而 dist 下根本没有它 ⇒ **用户拿不到、双击不了**。
#         审计刚记过这个形状:**编得过 ≠ 跑得起来**。
#
#  ★★ 出包自检必须在【出包形态】里跑,不能在仓库里跑:
#     管理端自检的 live 段要真起一个客户端、真发一次「请你优雅退出」,
#     再读客户端写的善后日志断言那八步逐条跑过 —— 而它找客户端的路径是 `..\client\`。
#     ⇒ 这里搭出 <tmp>\client\ + <tmp>\admin\ 这个**真实的并排形状**再跑。
#     ★ 不搭的话 live 段会 SKIP,而 SKIP 会被读成通过 —— 那正是本项目最恨的形状。
# ══════════════════════════════════════════════════════════════════════════════
Write-Host "[3] 发布管理端(单文件,win-x64)…"
# ══════════════════════════════════════════════════════════════════════════════
#  ★★★ V22:默认值从 `admin-pack` 改成 `admin`。这**不是**改名好看,是修一个
#    让整条「客户端拉起管理端」结构性失效的路径错。
#
#  客户端认「这台装没装管理端」用的是 `AdminApp.AdminAppPathNextTo`,它**逐字**探
#    <客户端 exe 目录>\..\admin\localai-admin.exe          (`AdminApp.AdminDirName` = "admin")
#  而这里以前发到 `<dist>\admin-pack\` ⇒ 那个探测**永远落空**
#  ⇒ `HostSetup.RoleEvidence.AdminAppPresent` **结构上恒为 false**
#  ⇒ 裁定第 1 条(主机客户端启动 ⇒ 拉起管理端)在出包产物上**根本走不到**,
#     而在开发树里它是好的 —— 典型的「只在发布形态下断掉」。
#  ★ 实测(2026-08-09):`dist\admin` **不存在**,exe 躺在 `dist\admin-pack`。
#
#  ★★ 目录名是**算出来的,不是挑出来的**:下面直接引用客户端那一侧的常量口径
#    (`AdminApp.AdminDirName`)。两处各写一个字面量的话,改一处就会再断一次 ——
#    而断掉的表现仍然是"开发树里好好的"。
# ══════════════════════════════════════════════════════════════════════════════
# ★ 与 `20-client-win/app/Services/AdminApp.cs` 的 `AdminDirName` 对齐;
#   下面 [3b] 有一条断言拿源码里的常量与这个值对拍,漂了就判红。
$adminDirName = 'admin'
if (-not $AdminOut) { $AdminOut = Join-Path (Split-Path $Out -Parent) $adminDirName }
New-Item -ItemType Directory -Force -Path $AdminOut | Out-Null
$adminProj = Join-Path $repo '20-client-win\admin\localai-admin.csproj'
if (-not (Test-Path $adminProj)) {
    # ★ 零命中判红:找不到 = 路径写错或工程被删,两种都得当场知道,不许静默跳过。
    Write-Host "X 找不到管理端工程:$adminProj" -ForegroundColor Red; exit 1
}
& dotnet publish $adminProj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:InformationalVersion=$ver `
    -o $AdminOut --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host "X 管理端发布失败" -ForegroundColor Red; exit 1 }
$adminExe = Join-Path $AdminOut 'localai-admin.exe'
if (-not (Test-Path $adminExe)) { Write-Host "X 没有产出管理端 exe" -ForegroundColor Red; exit 1 }

# ══════════════════════════════════════════════════════════════════════════════
#  [3b] ★★★★ 把「客户端找得到管理端」这件事**当场验掉**(V22)。
#
#  ★ 这条判据在此之前**不存在** —— 而它不存在的后果不是"少测一条":
#    `AdminAppPresent` 恒为 false 这件事在出包产物上躺了整整一版,
#    开发树里一切正常,谁也没看出来。★ 红测:把 $AdminOut 改回 'admin-pack' ⇒ 本条当场红。
#
#  ★★ 判据算的是**客户端会去看的那个路径**,不是"我发到哪儿了":
#    客户端 exe 在 <Out>,它探 <Out>\..\admin\localai-admin.exe。
#    这样写,哪天有人改了 $AdminOut 的算法、或者改了客户端那个常量,
#    两边一漂本条就红 —— 而不是各自都"对"。
# ══════════════════════════════════════════════════════════════════════════════
Write-Host "[3b] 验:客户端旁边找得到管理端…"
$adminSrc = Join-Path $repo '20-client-win\app\Services\AdminApp.cs'
if (Test-Path $adminSrc) {
    $m = [regex]::Match((Get-Content $adminSrc -Raw), 'AdminDirName\s*=\s*"([^"]+)"')
    if (-not $m.Success) {
        Write-Host "X 读不出 AdminApp.AdminDirName —— 这条判据的两端有一端不见了,不许静默放过。" -ForegroundColor Red
        exit 1
    }
    if ($m.Groups[1].Value -ne $adminDirName) {
        Write-Host ("X 出包目录名与客户端常量对不上:脚本用 '$adminDirName',而 " +
                    "AdminApp.AdminDirName = '" + $m.Groups[1].Value + "'。") -ForegroundColor Red
        Write-Host "  ★ 这正是「客户端探不到管理端」那个病的源头 —— 两处各写一个字面量。" -ForegroundColor Red
        exit 1
    }
    Write-Host "     OK 目录名与 AdminApp.AdminDirName 一致($adminDirName)" -ForegroundColor DarkGray
}
# ★★ 真正那一问:一个放在 <Out> 的客户端 exe,按它自己的算法找得到**这一次发的**管理端吗
$expectedAdminDir = Join-Path (Split-Path $Out -Parent) $adminDirName
$asIfClientSees   = Join-Path $expectedAdminDir 'localai-admin.exe'
# ★★★ 先比**目录**,再看文件在不在。顺序不能反,而且两条都要:
#   只看"那个路径上有没有 exe"是**不够**的 —— 上一次构建留下的旧 exe 会让它变绿,
#   于是把 $AdminOut 改错了也照样过。那种判据在红测里会骗人(实测想到过这一条)。
#   ⇒ 先要求"这一次发到的就是客户端会去看的那个目录",这条与残留物无关。
function Resolve-Norm([string]$p) {
    try { return (Resolve-Path -LiteralPath $p -ErrorAction Stop).Path.TrimEnd('\') }
    catch { return ([IO.Path]::GetFullPath($p)).TrimEnd('\') }
}
if ((Resolve-Norm $AdminOut) -ne (Resolve-Norm $expectedAdminDir)) {
    Write-Host "X 管理端发错了地方。" -ForegroundColor Red
    Write-Host "  这一次发到:  $(Resolve-Norm $AdminOut)" -ForegroundColor Red
    Write-Host "  客户端会去看:$(Resolve-Norm $expectedAdminDir)" -ForegroundColor Red
    Write-Host "  ★ 客户端探的是 <自己所在目录>\..\$adminDirName\localai-admin.exe" -ForegroundColor Red
    Write-Host "    (AdminApp.AdminAppPathNextTo,喂 HostSetup.RoleEvidence.AdminAppPresent)。" -ForegroundColor Red
    Write-Host "  ★★ 后果不是少个文件:AdminAppPresent 会**恒为 false**,于是" -ForegroundColor Red
    Write-Host "     【主机客户端启动 ⇒ 拉起管理端 ⇒ 自动起栈】整条在出包产物上走不到," -ForegroundColor Red
    Write-Host "     而在开发树里它是好的 —— 这正是它躺了一整版没人发现的原因。" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $asIfClientSees)) {
    Write-Host "X 目录对了,但那儿没有 localai-admin.exe:$asIfClientSees" -ForegroundColor Red
    exit 1
}
Write-Host "     OK 客户端会去看的那个目录,就是这一次发到的目录($expectedAdminDir)" -ForegroundColor DarkGray

Write-Host "[4] 管理端自检（出包形态:client 与 admin 并排）…"
$ashape = Join-Path ([IO.Path]::GetTempPath()) ("localai-admin-gate-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path (Join-Path $ashape 'client') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $ashape 'admin')  | Out-Null
Copy-Item $exe      (Join-Path $ashape 'client\localai-client.exe') -Force
Copy-Item $adminExe (Join-Path $ashape 'admin\localai-admin.exe')   -Force
$passAdmin = Invoke-GateSelftest -ExePath (Join-Path $ashape 'admin\localai-admin.exe') -Label '管理端(出包形态)'
Remove-Item $ashape -Recurse -Force -ErrorAction SilentlyContinue
if ($null -eq $passAdmin) {
    Write-Host "  ★ 管理端自检没过。live 段若红,多半是「八步优雅退出」这条真的断了 ——" -ForegroundColor Red
    Write-Host "    那是裁定第 7 条的承重路径。**不要**靠强杀绕过去:" -ForegroundColor Red
    Write-Host "    D106 钉住的是那张八步表本身,强杀会让它守不到真正会跑的那条路。" -ForegroundColor Red
    exit 1
}

$last = "自检通过（两种安装位置,均有哨兵佐证:PASS=$pass1 / PASS=$pass2,FAIL=0;管理端 PASS=$passAdmin,FAIL=0）"
# ★★★ 口径跟着数字进 VERSION.txt(2026-08-06)。
#   在此之前 VERSION.txt 只写「PASS=852 / PASS=848」,而 STATE 的基线是四位数 ——
#   读的人只能自己猜这两个数在量什么,而**猜错的方向是"以为覆盖变少了"**。
#   ⇒ 把「少的那些是什么、为什么少」写在数字旁边,和数字一起被读到。
# ★★ 口径要**逐个数字**给,不能只给最后一个。
#   上一版这里只写了 $script:LastSrcMiss(= 第二次那个),而上面印的是【两个】数
#   (PASS=853 / PASS=849)—— 于是第一个数仍然没有自己的口径。
#   这正是本条要治的病本身:**一个没说自己在量什么的数字**。给两个,就写两行。
if ($miss1 -lt 0 -or $miss2 -lt 0) {
    $last += "`n        ! 口径不明：这份 exe 的自检还没带 SRCMISS，不要拿这两个数与基线比。"
} else {
    $last += "`n        ★ 口径（逐个给，两个数不是同一个量程）：" +
             "`n          · 原位（$Out）：读不到 $miss1 处 / 读得到 $hit1 处" +
             "`n          · 换个位置（仓库外）：读不到 $miss2 处 / 读得到 $hit2 处" +
             "`n          发布产物旁边没有源码 ⇒ 那些【结构/接线】断言整段没跑，" +
             "既不计 PASS、也不计 FAIL、更不计 SKIP。" +
             "`n        ★ 两个数不一样是**正常的**：exe 待在仓库里时往上翻能多摸到几个仓库级文件，" +
             "比放在仓库外多跑几条。数字对不上时先看这一行，别去找一个并不存在的回归。" +
             "`n        ⇒ **两个都不能和开发树的基线直接比**（基线见 00-docs\STATE.md）。" +
             "少的不是覆盖变差了，是那批断言在这个形态下【测不了】。"
}
Write-Host "    $($last -split "`n" | Select-Object -First 1)"

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

# ══════════════════════════════════════════════════════════════════════════════
#  ★★ V22:管理端包也要有版本戳与校验和。
#
#  在此之前 `dist\admin-pack` 里**只有 exe 与 pdb** —— 拿到手上说不清是哪一版。
#  这与本脚本开头第 67 行自己写的那条道理是同一条:
#    「不烧的话,版本只存在 VERSION.txt 里,而那个文件拷丢了就永远说不清手上这个 exe 是什么」
#  —— 管理端连那个文件都没有,所以它比客户端还差一档。
#  ★ 两个 exe 的版本戳**是同一个 $ver**(同一次构建、同一棵树),这一点要写在文件里:
#    两个包各自带一个不同的戳,排查时会让人以为它们不是一起出的。
# ══════════════════════════════════════════════════════════════════════════════
$adminHash = (Get-FileHash -Algorithm SHA256 $adminExe).Hash
Set-Content -Path (Join-Path $AdminOut 'SHA256.txt') -Encoding utf8 -Value "$adminHash  localai-admin.exe"
Set-Content -Path (Join-Path $AdminOut 'VERSION.txt') -Encoding utf8 -Value @"
localai-admin(主机管理端)
版本戳: $ver
自检:   管理端(出包形态)PASS=$passAdmin FAIL=0
构建于: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')

★ 版本戳与同目录旁边的 localai-client 是【同一个】—— 同一次构建、同一棵源码树。
  对不上就说明这两个包不是一起出的,别混用。
★ 这个目录必须与 client\ 并排(客户端逐字探 ..\$adminDirName\localai-admin.exe),
  单独拷走会让「客户端拉起管理端 ⇒ 自动起栈」整条走不到。
$verNote
"@

Set-Content -Path (Join-Path $Out '安装说明.txt') -Encoding utf8 -Value @"
本地 AI 中枢 · 客户端
版本戳 $ver

【怎么装】
把整个目录拷到你想放的位置(随便哪个盘下建一个 LocalAI\client 之类的目录即可),
双击 localai-client.exe 就能用。
不需要管理员权限,不写系统目录,不改注册表以外的任何东西。

★ 主机上有一条例外(副机没有这个约束):
  客户端要认出"这台是主机"并自动起栈,其中一条证据是 exe 旁边的  ..\host\localai-lan-edge.exe。
  把 client\ 单独拷走会拆掉这条证据 —— 在【还没配过对的首次启动】那一路上,
  主机会被判成"拿不准 ⇒ 不是主机"(fail-closed),自动起栈整条不走。
  ⇒ 主机上请保持  client\  与  host\  并排。
  (配过对之后还有别的证据可用,所以这条只在"未配对首启"这个窄场景下要紧。)

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
· AI 已接入(P4 · S11,2026-08-05):聊天能逐字回答。
  ★ 前提是【中枢那台在跑】;中枢没起时聊天只会记录、不会有回答,界面会说清是哪一步没通。
· 记忆库是这台机器自己的本地库,不与中枢或另一台共用(记忆服务化仍未做)。
· 界面目前只有中文;中/日/英三语已裁定移到 P7。
· 这不是安装器:没有卸载项、没有自动升级。删除 = 直接删这个目录。
"@

Write-Host ""
Write-Host "=== 出包完成 ===" -ForegroundColor Green
Write-Host "  $Out"
Write-Host "  版本戳 $ver"
Write-Host "  SHA256 $hash"
