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
# 产物(★ V31 起是**三件**,不是两件):
#   <Out>\localai-client.exe        · SHA256.txt · VERSION.txt · 安装说明.txt
#   <Out>\..\admin\localai-admin.exe · SHA256.txt · VERSION.txt
#   <Out>\..\host\localai-lan-edge.exe + localai-identity.exe · SHA256.txt · VERSION.txt
# ★ 第三件是 2026-08-09 挖出来的:它此前**从来没有被任何脚本发布过**,见 [5] 那段。

param(
    [string]$Out = "",
    # V14b:管理端的产物目录。★ 默认与客户端产物**并排**(见 [3]/[4] 那两段的理由)。
    [string]$AdminOut = "",
    # V31:主机端程序(lan-edge + identity)的产物目录。★ 默认同样与客户端**并排**,
    #   而且目录名不是挑的,是从客户端那条探测里抠出来的 —— 见 [5] 那段。
    [string]$HostOut = ""
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

# ══════════════════════════════════════════════════════════════════════════════
#  ★★★★ V36 · 并发闸(D124③ 只登记了,代码一直没动)
#
#  为什么它必须存在:本脚本**没有**任何并发保护,而两次构建同时跑会互相踩:
#    · 驻留编译器(VBCSCompiler,跑在 dotnet.exe 里)会锁住 `obj\Release\`
#      ⇒ 后一趟报一句「dotnet 一个字都没输出」的进程级死法(上面 Invoke-Publish
#        那段注释里那条排查建议,第一句就是「先查有没有第二个构建在跑」);
#    · 更坏的一半:两趟**共用同一个 dist 目录**。A 的 exe + B 的 VERSION.txt
#      拼出来的那一份**每个文件都成立、合起来是假的** —— 而它长得和一次干净出包一模一样。
#  ★ 2026-08-11 没撞上,**是因为那天只有一条车道在跑,不是因为它被挡住了**。
#
#  ★★ 判据形状(为什么是 PID 存活而不是"锁文件在不在"):
#    锁文件残留(上一趟被 Ctrl-C / 蓝屏 / 被杀)会变成一条**永久的假红**,
#    而假红会训练人绕过门禁 —— D82 已经因此失效过两条,第 5 条整节记的就是这个代价。
#    ⇒ 锁里记 **PID + 起始时间 + 仓库路径**;PID 不在了就当陈旧锁,覆盖并**明说**。
#  ★★★ 判红时**不产出任何东西**:先判、再干活,顺序不能反 ——
#    "先出了一半再发现冲突"和没有闸的区别只是多浪费十五分钟。
# ══════════════════════════════════════════════════════════════════════════════
$buildLock = Join-Path ([IO.Path]::GetTempPath()) 'localai-build-client.lock'
if (Test-Path $buildLock) {
    $lockTxt = ''
    try { $lockTxt = (Get-Content $buildLock -Raw -ErrorAction Stop).Trim() } catch { }
    $lockPid = 0
    if ($lockTxt -match '^PID=(\d+)') { $lockPid = [int]$Matches[1] }
    $alive = $false
    if ($lockPid -gt 0) {
        try { $null = Get-Process -Id $lockPid -ErrorAction Stop; $alive = $true } catch { $alive = $false }
    }
    if ($alive) {
        Write-Host ""
        Write-Host "X 已经有另一次出包在跑 —— 本次【不产出任何东西】。" -ForegroundColor Red
        Write-Host "  锁:$buildLock" -ForegroundColor Red
        ($lockTxt -split "`r?`n") | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        Write-Host "  ★ 为什么不让两趟一起跑:驻留编译器会锁住 obj\Release\(表现是一句" -ForegroundColor Red
        Write-Host "    「dotnet 一个字都没输出」的死法),而且两趟共用同一个 dist 目录 ——" -ForegroundColor Red
        Write-Host "    A 的 exe 配 B 的 VERSION.txt,**每个文件都成立、合起来是假的**。" -ForegroundColor Red
        Write-Host "  ⇒ 等那一趟跑完再来;确认它已经死了的话,删掉上面那个锁文件。" -ForegroundColor Red
        exit 1
    }
    Write-Host "  ! 捡到一个陈旧的出包锁(PID=$lockPid 已经不在了)—— 覆盖它继续。" -ForegroundColor Yellow
    Write-Host "    原锁内容:$($lockTxt -replace "`r?`n", ' | ')" -ForegroundColor DarkGray
}
Set-Content -Path $buildLock -Encoding utf8 -Value @(
    "PID=$PID"
    "起始=$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    "仓库=$repo"
)

# ══════════════════════════════════════
#  ★★★★ 2026-08-20(第 0 条车道):**释放锁** —— 上面写了锁,而在此之前
#  **本脚本从来没有在任何判红路径上释放过它**。
#
#  ★ 昨天登记这条时我写的是「脏树闸判红时不释放锁」—— **那个说法太小了**。
#    实测:写锁在本行上方,而它下面有 **18 处以上的 `exit 1`**,
#    释放只有**一处**,在脚本最末尾的正常路径上。⇒ **每一次判红都留一个锁文件。**
#
#  为什么不致命(而它仍然该修):下一趟会把它当陈旧锁(PID 已不在)覆盖掉 ——
#  V36 设计的「陈旧锁不假红」那一条正好接住了它。
#  ★★ 但**靠另一条判据兜住的缺陷,仍然是缺陷**:
#    锁里记着仓库路径与起始时刻,指向一次**没有产物**的构建 ——
#    而人读到它时,读到的是「有一趟在跑过」。**一条留下来的假痕迹。**
#
#  ★★★ 判据形状(为什么不是"在每个 exit 前面加一句"):
#    18 处 exit,逐个加**一定会漏**,而漏掉的那一处**不会报错**,只会安静地留锁。
#    ⇒ 注册一次退出事件,覆盖**所有**退出路径 —— 包括将来新加的。
#    实测(2026-08-20 用一个最小探针跑过,不是推演):`-File` 模式下
#    `exit 1` **确实会**触发 PowerShell.Exiting,锁被删掉。
#
#  ★★★★ 位置是判据的一部分:本段必须在 `Set-Content $buildLock` **之后** ——
#    上面「已经有另一次出包在跑」那条判红发生在写锁**之前**,
#    若把注册提到那之前,本次判红就会**删掉正在跑的那一趟的锁**,
#    等于亲手把并发闸拆了。★ 已双向红测(见交回)。
#
#  ★ 锁路径用 -MessageData 传进去,**不在事件块里第二次拼那个文件名**:
#    两处字面量总有一天会分叉,而分叉之后这段代码会去删一个不存在的文件、
#    **并且一声不吭**。
# ══════════════════════════════════════
#  ★★★★ 2026-08-20 第二版 —— **第一版我写错了,而它错得一声不吭**:
#    第一版写 `-MessageData $buildLock` + 事件块里读 `$Event.MessageData`。
#    实测(最小探针):事件块**确实跑了**,而 `$Event.MessageData` 是**空的** ——
#    于是 `Remove-Item ""` 配上 `-ErrorAction SilentlyContinue` **什么都不说地失败**,
#    锁照旧留着。
#    ★★ 而我第一次的探针**没验这一半**:它验的是「Exiting 会不会触发」,
#      我写进脚本的却是「MessageData 传不传得进去」——**验的和写的不是同一件事**。
#      判词:**一个只验了一半的探针,给出的是一个完整的绿。**
#    ⇒ 改成把路径**烘进脚本块文本**:来源仍然只有 `$buildLock` 一处,
#      而块里是字面量,跨 runspace 不依赖任何变量传递。
Register-EngineEvent -SourceIdentifier PowerShell.Exiting -SupportEvent -Action (
    [scriptblock]::Create(
        "Remove-Item -LiteralPath '" + ($buildLock -replace "'", "''") + "' -Force -ErrorAction SilentlyContinue"
    )
) | Out-Null

# ══════════════════════════════════════════════════════════════════════════════
#  ★★★ 发布失败时**必须把 dotnet 的原话打出来**(2026-08-10 实测踩到)。
#
#  那天 `[1]` 报了一句「X 发布失败」就退出,**dotnet 一个字都没吐** ——
#  于是唯一的查法是把同一条 publish 手工再跑一遍(那次跑出来是 exit 0,偶发)。
#  ★ 根因是 `-v q`:它把一切压到只剩 error 级。而这一趟连 error 都没有,
#    说明是 MSBuild 进程层面的死法(节点崩了 / 被杀 / restore 没起来)——
#    **恰恰是最需要原文的那一类**,却什么都没留下。
#
#  ★★ 处置不是把 `-v q` 改成 normal(那会让正常一趟刷几千行,把真正的 warning 淹掉),
#    而是**把输出接住**:成功时照旧只见 warning,失败时把接住的尾巴原样打出来。
#  ★ 明确**不加重试**:偶发重跑一次多半能过,但"自动重试"会把一类真实的
#    间歇性缺陷永久藏起来 —— 那是本仓最恨的形状。红了就红了,把原文交出来让人看。
# ══════════════════════════════════════════════════════════════════════════════
function Invoke-Publish {
    # ★ 参数名**不能叫 $Args**:那是 PowerShell 的自动变量,在函数里会和它打架
    #   (声明得下去,但 `@Args` 展开的到底是谁,取决于你不该需要知道的细节)。
    param([string]$Label, [string[]]$PubArgs)
    # ★★ **不要**在这儿写 `2>&1`:PS 5.1 会把原生命令的每行 stderr 包成 ErrorRecord
    #   (NativeCommandError),配上本脚本顶上的 `$ErrorActionPreference = 'Stop'`,
    #   一行无害的 stderr 就能让**成功的**发布当场抛异常。
    #   ⇒ 只接 stdout(MSBuild 的错误本来就走 stdout);stderr 照旧直接显示在控制台。
    $out = & dotnet publish @PubArgs
    $code = $LASTEXITCODE
    $out | ForEach-Object { Write-Host $_ }
    if ($code -ne 0) {
        Write-Host "X $Label 发布失败(dotnet 退出码 $code)" -ForegroundColor Red
        if (@($out).Count -eq 0) {
            Write-Host "  ★★ dotnet **一个字都没输出** —— 这不是编译错误,是进程层面的死法:" -ForegroundColor Red
            Write-Host "     MSBuild 节点崩了 / 被杀 / restore 没起来 / 输出目录被占。" -ForegroundColor Red
            Write-Host "     ⇒ 先查:有没有第二个构建在跑、驻留编译器在不在" -ForegroundColor Red
            Write-Host "       (它跑在 dotnet.exe 里,`taskkill /IM VBCSCompiler.exe` 杀不到,见下方注释)。" -ForegroundColor Red
        } else {
            Write-Host "  ---- dotnet 原话(末 40 行)----" -ForegroundColor Red
            @($out) | Select-Object -Last 40 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        }
        exit 1
    }
}
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

# ══════════════════════════════════════════════════════════════════════════════
#  ★★★★ 脏树 ⇒ **直接判红,一个产物都不产**(2026-08-10,协调层裁定)。
#
#  判词:**一个说不清自己是哪一版的产物,它验出来的东西也说不清是谁的。**
#
#  ★ 在此之前(上面那整段)的做法是:算一个工作树指纹、拼进 `.dirty-xxxxxxxx`,
#    然后**照样出包**。那比什么都不写强,但它治的是"事后能不能认出来",
#    治不了"这份包能不能用" —— 而后者才是出包的目的:
#      · 一个 `.dirty` 包没法从任何一个提交重建,SHA256 对得上也说明不了它是什么;
#      · 更要命的是它会被当成基线:2026-08-10 就是靠读 exe 的 ProductVersion 才发现
#        dist\host 落后 137 个提交 —— 换成一个 `.dirty` 戳,那次根本查不下去。
#  ⇒ 所以不是"警告一下然后出",是**停在这里**,而且是在
#    `New-Item $Out` 之前 —— **一个字节都还没往 dist 里写**。
#
#  ★★ 未跟踪文件(`??`)**同样算脏**,这是有意的:`git status --porcelain` 收它们,
#    而一个未跟踪的 `.cs` 会被 MSBuild 的默认 glob **编进 exe**。
#    把它们放行等于留一条"改了代码但戳是干净的"的路。
#
#  ★ 没有 `-AllowDirty` 之类的开关。要出包就先提交或先 `git stash` ——
#    一个能被绕过的判据,在赶时间的那一次一定会被绕过,而那一次正是最需要它的一次。
# ══════════════════════════════════════════════════════════════════════════════
if ($dirtyFiles.Count -gt 0) {
    Write-Host "X 工作树不干净 —— **不出包**。" -ForegroundColor Red
    Write-Host "  这一趟的戳会是:$ver" -ForegroundColor Red
    Write-Host "  ★ 一个说不清自己是哪一版的产物,它验出来的东西也说不清是谁的。" -ForegroundColor Red
    Write-Host "  git status --porcelain($($dirtyFiles.Count) 行,原样):" -ForegroundColor Red
    $dirtyFiles | ForEach-Object { Write-Host "     $_" -ForegroundColor Red }
    Write-Host "  ⇒ 先提交(或 git stash),再重跑。" -ForegroundColor Red
    Write-Host "  ★★ 现在停下**没有动过 dist**:这一条在 New-Item `$Out 之前。" -ForegroundColor Red
    Write-Host "  ★ 多条车道共用一棵工作树时,脏的那几行可能**不是你改的** ——" -ForegroundColor Red
    Write-Host "    先看清是谁的,别顺手 checkout 掉别人正在写的东西。" -ForegroundColor Red
    exit 1
}

# ══════════════════════════════════════════════════════════════════════════════
#  ★★★ V28:`$Out` 默认值从 `dist\client-pack` 改成**落位目录** ——
#    补上 V22 只做了一半的那件事。V22 把 `$AdminOut` 改成并排的 `admin\`(管理端出包即落位),
#    **而 `$Out` 一个字没动**。后果不是"名字不好看":
#      · 无参跑一次,管理端落到 `dist\admin\`,客户端落到 `dist\client-pack\`,
#        而部署位 `dist\client\` **原地不动** ⇒ 每出一次包,两个目录的戳就分裂一次。
#      · 2026-08-09 那天 P4 清单为此连写错三次方向,第三次把**当天最新的**客户端包
#        (`client-pack`)称作「上一轮的残留」,而它指人去拷的 `dist\client` 才是旧的那份。
#        ★ 那份清单最后只好加一条「本节从此不写死任何版本戳」—— 拿纪律去糊一个**机械缺陷**。
#
#  ★★ 目录名是**算出来的,不是挑出来的**(与 [3b] 同一个手法,方向相反):
#    管理端逐字探 `<管理端 exe 目录>\..\client\localai-client.exe`
#    (`ClientLink.ClientExePathNextTo`,与客户端探管理端的 `AdminAppPathNextTo` 互为镜像)。
#    ⇒ 这里直接把那条探测里的字面量抠出来用。在本文件里另写一个 "client" 字面量的话,
#      改一处就会再断一次 —— 而断掉的表现仍然是"开发树里好好的"。
#  ★ 抠不出来**判红**:零命中与全清白长得一模一样;更不许静默用 'client' 兜底 ——
#    兜底会让这个洞再躺一版,而这一版它已经躺过了。
# ══════════════════════════════════════════════════════════════════════════════
$clientLinkSrc = Join-Path $repo '20-client-win\admin\Services\ClientLink.cs'
if (-not (Test-Path $clientLinkSrc)) {
    Write-Host "X 找不到 $clientLinkSrc —— 客户端落位目录名的【唯一来源】不见了。" -ForegroundColor Red
    Write-Host "  ★ 不许猜一个:猜对了也只是这一次对,下一次改名照样断。" -ForegroundColor Red
    exit 1
}
$cm = [regex]::Match((Get-Content $clientLinkSrc -Raw),
                     'Path\.Combine\(\s*adminExeDir\s*,\s*"\.\."\s*,\s*"([^"]+)"')
if (-not $cm.Success) {
    Write-Host "X 读不出管理端探客户端时用的目录名(ClientLink.ClientExePathNextTo)。" -ForegroundColor Red
    Write-Host "  ★ 这条判据的两端有一端不见了,不许静默兜底 —— 兜底就是把「客户端出包即落位」" -ForegroundColor Red
    Write-Host "    这件事重新变成一句没人守的承诺。" -ForegroundColor Red
    exit 1
}
$clientDirName = $cm.Groups[1].Value

if (-not $Out) {
    $Out = Join-Path $repo "dist\$clientDirName"
} else {
    # ★ 带 -Out 是**有意去别处**(例如出一份拿去比对的包)。允许,但要当场说清它不落位 ——
    #   否则「我明明出过包了,怎么部署位还是旧的」会再发生一次。
    Write-Host "  ! 指定了 -Out:$Out" -ForegroundColor Yellow
    Write-Host "    ★ 这【不是】部署位($repo\dist\$clientDirName)—— 出完包部署位不会动。" -ForegroundColor Yellow
    Write-Host "      要落位就别带 -Out。" -ForegroundColor Yellow
}
# ★★★★ V36:这里原来紧跟着一句 `New-Item -Force -Path $Out` —— **挪到占用闸之后**了。
#   理由:占用闸判红时的承诺是「现在停下**什么都还没动**」,而先建目录就已经动了 dist。
#   三个产物目录一律在闸**通过之后**才建(见 [0c])。

# ══════════════════════════════════════════════════════════════════════════════
#  [0] ★★★ V31:主机端产物目录 + **开工前的占用闸**。
#
#  ★ 为什么这两件事放在**最前面**而不是放在 [5] 那一节里(实测教训,2026-08-09):
#    第一版把闸放在 [5],于是一次被挡下的运行**已经把客户端与管理端重出并覆盖了落位**,
#    只有 host 是旧的 —— 三个目录当场分裂,而这正是本轮要治的病本身。
#    ⇒ 会让整趟失败的检查,必须在**动任何产物之前**跑完。
#
#  ★★ 目录名与 exe 名是**算出来的,不是挑出来的**(与 [3b] 完全同一手法):
#    客户端逐字探  <客户端 exe 目录>\..\host\localai-lan-edge.exe
#    (`AdminApp.HostToolsDirNextTo`,喂 `HostSetup.IdentityExistsAsync` 那条角色证据)。
#    抠不出来**判红**:零命中与全清白长得一模一样,更不许静默用 'host' 兜底。
# ══════════════════════════════════════════════════════════════════════════════
$hostProbeSrc = Join-Path $repo '20-client-win\app\Services\AdminApp.cs'
if (-not (Test-Path $hostProbeSrc)) {
    Write-Host "X 找不到 $hostProbeSrc —— 主机端目录名的【唯一来源】不见了。" -ForegroundColor Red
    Write-Host "  ★ 不许猜一个 'host':猜对了也只是这一次对,下一次改名照样断。" -ForegroundColor Red
    exit 1
}
$hostProbeRaw = Get-Content $hostProbeSrc -Raw
# ★ 先切到 HostToolsDirNextTo 这个方法之后再抠,免得将来同文件里多一个 Path.Combine 就抠错。
$hpIdx = $hostProbeRaw.IndexOf('HostToolsDirNextTo(string? clientExeDir)')
if ($hpIdx -lt 0) {
    Write-Host "X 在 AdminApp.cs 里找不到 HostToolsDirNextTo(string? clientExeDir)。" -ForegroundColor Red
    Write-Host "  ★ 这条判据的两端有一端不见了,不许静默兜底。" -ForegroundColor Red
    exit 1
}
$hostProbeBody = $hostProbeRaw.Substring($hpIdx)
# `Path.Combine(clientExeDir, "..", "host")` —— 注意 AdminAppPathNextTo 那条第三段是常量
# (AdminDirName,没有引号),所以下面这个"引号里的字面量"只会命中主机端这一条。
$hdm = [regex]::Match($hostProbeBody, 'Path\.Combine\(\s*clientExeDir\s*,\s*"\.\."\s*,\s*"([^"]+)"\s*\)')
# `Path.Combine(host, "localai-lan-edge.exe")`
$hem = [regex]::Match($hostProbeBody, 'Path\.Combine\(\s*host\s*,\s*"([^"]+)"\s*\)')
if (-not $hdm.Success -or -not $hem.Success) {
    Write-Host "X 读不出客户端探主机端时用的目录名/exe 名(AdminApp.HostToolsDirNextTo)。" -ForegroundColor Red
    Write-Host "  ★ 抠不出来就判红:零命中与全清白长得一模一样。" -ForegroundColor Red
    exit 1
}
$hostDirName = $hdm.Groups[1].Value
$hostExeName = $hem.Groups[1].Value

if (-not $HostOut) {
    $HostOut = Join-Path (Split-Path $Out -Parent) $hostDirName
} else {
    Write-Host "  ! 指定了 -HostOut:$HostOut" -ForegroundColor Yellow
    Write-Host "    ★ 这【不是】部署位 —— 出完包 dist\$hostDirName 不会动。" -ForegroundColor Yellow
}

# ══════════════════════════════════════════════════════════════════════════════
#  [0b] ★★★★ V36:管理端产物目录**提前算出来** —— 占用闸要盖住它
#
#  ★ 它原来是在 [3] 那儿(约 600 行)才算的,而占用闸在这上面 ⇒
#    闸**结构上不可能**知道 dist\admin 会被写。下面 [3] 那段只剩一句"已经算过了"。
#  ★★ `$adminDirName` 仍是这个字面量,而 [3b] 有一条断言拿它与
#    `AdminApp.AdminDirName` 源码常量对拍 —— 漂了就判红。那条判据一个字没动。
# ══════════════════════════════════════════════════════════════════════════════
$adminDirName = 'admin'
if (-not $AdminOut) { $AdminOut = Join-Path (Split-Path $Out -Parent) $adminDirName }

# ══════════════════════════════════════════════════════════════════════════════
#  [0c] ★★★★★ V36 · 占用闸盖住**这一趟会写的每一个产物**(此前只盖住三件里的一件)
#
#  ★ 上一版这里只问一句 `Get-Process -Name localai-lan-edge` —— 全文件唯一一处 Get-Process。
#    而这一趟**三件都直接写落位**:[1]→dist\client · [3]→dist\admin · [5]→dist\host。
#    ⇒ 闸的**位置**是对的(开工前、动任何产物之前),**覆盖面**没跟上。
#  ★★ 而管理端是**常驻托盘程序**,主机上它本来就开着 ⇒ [3] 在写 exe 那一步炸掉,
#    而此时 [1] 已经把 dist\client 的 exe 覆盖过了。
#  ★★★ 危害比"三个目录分裂"更重:client 的 `SHA256.txt` / `VERSION.txt` / 安装说明
#    **全都写在 [3] 之后** ⇒ [3] 一死,dist\client 是【新 exe + 上一轮的三份文本】,
#    SHA256 与 exe 对不上 —— **而那份安装说明正让人"装之前先对一遍哈希"**。
#    一个让人按它做、做完得出错误结论的说明,比没有说明更坏。
#  ★ 最常撞的其实是 [1]:客户端走 HKCU 自启,主机上它也开着,占着自己的 exe。
#
#  ══ 判据形状(为什么**不是**一张"这几个进程"的清单)══
#  一张手写的进程清单会被逐条做完,然后给出"全覆盖了"的假象 —— 而真正会撞的那个不在表上。
#  ⇒ 反过来推:**先列出这一趟会写哪些文件**(`$writeTargets`,与真正去写它们的那几行
#    用的是同一批变量),再由它导出:
#      ① 会被写的**目录**集合 ⇒ 跑在这些目录里的**任何** exe 都算占用者
#         —— 不问它叫什么名字,新增一个产物 exe 也自动被盖住;
#      ② 会被写的 exe **基名**集合 ⇒ 只在**读不到进程路径**时用(fail-closed,理由见下)。
#  ★ ① 是主判据,② 只是兜底 —— 反过来的话就又变成一张名字清单了。
#
#  ★★ 比的是**路径**不是名字:往 `-Out <别处>` 出一份拿去比对的包,与
#    "落位那份正被占用"是两件事;只按名字拦会把前者无辜挡下,
#    而一条会误挡的闸会被人加 `-Force` 绕过去(第 5 条量过这个代价)。
#  ★★★ 读不到路径时 **fail-closed**(当成"就是它"):读不到通常是权限,
#    而"猜它不是"猜错的后果是发布在写文件那一步炸掉,报错还指不到这儿。
#
#  ★★ 明确**不**自动去杀它们:D116 立的是「关栈动谁」要有裁定 ——
#    Edge 一停所有副机连接当场断,管理端一停托盘里那套就没了。
#    出包脚本没有资格替用户做这个决定。⇒ 说清楚,然后停下,让人自己关。
# ══════════════════════════════════════════════════════════════════════════════
$writeTargets = @(
    @{ Path = (Join-Path $Out      'localai-client.exe');   Who = '客户端';        Step = '[1]' }
    @{ Path = (Join-Path $AdminOut 'localai-admin.exe');    Who = '主机管理端';    Step = '[3]' }
    @{ Path = (Join-Path $HostOut  $hostExeName);           Who = '主机端 Edge';   Step = '[5]' }
    @{ Path = (Join-Path $HostOut  'localai-identity.exe'); Who = '主机端 identity'; Step = '[5]' }
)
# ★ 这四个路径变量下面照原样用(`$exe` / `$adminExe` / `$hostExe` / `$identityExe`),
#   **不再各自 Join-Path 一次** —— 两处各算一遍的话,闸检查的和真正被写的可以是两个地方。
$exe         = $writeTargets[0].Path
$adminExe    = $writeTargets[1].Path
$hostExe     = $writeTargets[2].Path
$identityExe = $writeTargets[3].Path

$targetDirs  = @($writeTargets | ForEach-Object {
    [IO.Path]::GetFullPath((Split-Path $_.Path -Parent)).TrimEnd('\') } | Select-Object -Unique)
$targetNames = @($writeTargets | ForEach-Object {
    [IO.Path]::GetFileNameWithoutExtension($_.Path) } | Select-Object -Unique)

$occupied = @()
foreach ($proc in @(Get-Process -ErrorAction SilentlyContinue)) {
    $pPath = $null
    try { $pPath = $proc.Path } catch { $pPath = $null }
    if ($pPath) {
        $pDir = [IO.Path]::GetFullPath((Split-Path $pPath -Parent)).TrimEnd('\')
        # ① 主判据:它跑在一个**这一趟会写**的目录里 —— 不问它叫什么
        if ($targetDirs -contains $pDir) {
            $occupied += "PID $($proc.Id)  $pPath"
        }
    } elseif ($targetNames -contains $proc.ProcessName) {
        # ② 兜底:路径读不到(多半是权限),而名字对得上 ⇒ fail-closed,宁可多问一句
        $occupied += "PID $($proc.Id)  $($proc.ProcessName)(★ 读不到它的路径 —— 按 fail-closed 当成就是它)"
    }
}
if ($occupied.Count -gt 0) {
    Write-Host ""
    Write-Host "X 要被覆盖的产物里有正在运行的 —— 发布会写不进去,而现在停下**什么都还没动**。" -ForegroundColor Red
    $occupied | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    Write-Host "  ---- 这一趟会写的产物(闸就是从这张表推出来的)----" -ForegroundColor Red
    $writeTargets | ForEach-Object { Write-Host "    $($_.Step) $($_.Who)  $($_.Path)" -ForegroundColor Red }
    Write-Host "  ★ 请先把对应的程序关掉(客户端 / 托盘里的主机管理端 / 「启动Edge」那个窗口),再重跑。" -ForegroundColor Red
    Write-Host "  ★★ 本脚本【有意】不替你杀它们:Edge 一停所有副机连接当场断" -ForegroundColor Red
    Write-Host "     (D116:关栈动谁要有裁定 —— 用户可见、不可自动恢复的后果)。" -ForegroundColor Red
    Write-Host "  ★★★ 为什么必须在这儿拦住:client 的 SHA256/VERSION/安装说明都写在 [3] 之后 ——" -ForegroundColor Red
    Write-Host "     [3] 中途死掉的话,dist\client 会是【新 exe + 上一轮的三份文本】," -ForegroundColor Red
    Write-Host "     SHA256 与 exe 对不上,而那份安装说明正让人装之前先对一遍哈希。" -ForegroundColor Red
    Write-Host "  ★ 只想出一份拿去比对、不碰落位的包:加 -Out <别处>(admin/host 会跟着去 <别处> 旁边)。" -ForegroundColor Red
    exit 1
}

# ★ 闸过了才建目录 —— 顺序不能反(见上面 [0] 那句「什么都还没动」)。
New-Item -ItemType Directory -Force -Path $Out      | Out-Null
New-Item -ItemType Directory -Force -Path $AdminOut | Out-Null
New-Item -ItemType Directory -Force -Path $HostOut  | Out-Null

# ★★★★ V36:这里原来是一段**只问 Edge 一个进程**的占用闸(全文件唯一一处 `Get-Process`)。
#   它已经被上面 [0c] 那段**从「这一趟会写哪些文件」推出来的**闸取代 ——
#   起因原文留在 [0c] 里:2026-08-09 实测 Edge 活着(PID 15452);
#   而管理端与客户端都是主机上**本来就开着**的程序,它们一个都没在那道闸的覆盖面里。
#   ★ 「闸的位置对了、覆盖面没跟上」—— 位置那一半仍然照旧:动任何产物之前跑完。

Write-Host "[1] 发布单文件 exe(自包含,win-x64)…"
Push-Location $app
# ★ 把版本戳烧进程序集 —— 客户端才能自报"我是哪一版"。
#   不烧的话,版本只存在 VERSION.txt 里,而那个文件拷丢了就永远说不清手上这个 exe 是什么。
try {
    Invoke-Publish '客户端' @(
        '-c','Release','-r','win-x64','--self-contained','true',
        '-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',"-p:InformationalVersion=$ver",
        '-o',$Out,'--nologo','-v','q')
} finally { Pop-Location }   # ★ 失败也要退栈:Invoke-Publish 里是 exit,但留个 finally 免得将来改成 throw 时漏掉

# ★ V36:`$exe` 已在 [0c] 的 `$writeTargets` 里算过 —— 这里**不再算第二遍**。
#   两处各算一遍的话,占用闸检查的和真正被写的可以是两个地方。
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

        # ══════════════════════════════════════════════════════════════════
        #  ★★★★ V23:「本该跑而没跑」在门禁里**看得见**(在此之前它是隐形的)
        #
        #  V22 立了「把静默跳过换成看得见的 SKIP」这个做法,却没走完最后一步:
        #  上面那条正则只认 PASS/FAIL ⇒ 那些「看得见的 SKIP」在**门禁里谁也不会红**,
        #  于是「登记成 SKIP」等于「登记给自己看」。
        #
        #  ★ 而「SKIP 一律判红」会走到反面:发布产物旁边没有源码、这台没装 python、
        #    8080 被真东西占着 —— 这些是**设计如此**,天天判红只会训练人绕过门禁
        #    (D82 已经因此失效过两条)。
        #  ⇒ 口径是把 SKIP 切成两类,只对第二类判红:
        #      · SKIP  = 这个形态下**本来就测不了** ⇒ 黄字打印,不判红;
        #      · OWED  = **本该跑得了却没跑成**(判据指错了文件、子进程挂死、
        #                生命周期那段一条结果都没写出来)⇒ **判红**;
        #      · LIFE  = 「托盘右键关闭 → 管理端退出 → 栈真的没了」到底验没验到。
        #                它是验收④本身,0 就是**没验**,而没验与全绿在退出码上长得一样 ⇒ 判红。
        #  ★★ 字段缺失(exe 比本脚本旧)**不判红**,只打黄字说"口径不明" ——
        #    与上面 SRCMISS 那一档同一条处置:不拿一个没有的数去否定一次构建。
        # ══════════════════════════════════════════════════════════════════
        $skip = -1; $owed = -1; $life = -1
        if ($text -match 'SKIP=(\d+)') { $skip = [int]$Matches[1] }
        if ($text -match 'OWED=(\d+)') { $owed = [int]$Matches[1] }
        if ($text -match 'LIFE=(\d+)') { $life = [int]$Matches[1] }

        if ($owed -gt 0) {
            Write-Host ""
            Write-Host "X $Label：有 $owed 条【本该跑而没跑】(OWED)。" -ForegroundColor Red
            Write-Host "  ★ OWED 不是 SKIP：SKIP 是「这个形态下测不了」，OWED 是「本该跑得了却没跑成」——" -ForegroundColor Red
            Write-Host "    判据指错了文件 / 子进程挂死 / 那一段一条结果都没写出来。" -ForegroundColor Red
            Write-Host "    ★★ 不出包：一条没跑过的断言与一条通过的断言，在 PASS 数里是看不出来的。" -ForegroundColor Red
            if ($outText -match '\S') {
                Write-Host "  ---- 欠着的那几条 ----" -ForegroundColor Red
                ($outText -split "`r?`n" | Where-Object { $_ -match '^\s*OWED' }) |
                    ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
            }
            Remove-Item $stdout -Force -ErrorAction SilentlyContinue
            return $null
        }
        if ($life -eq 0) {
            Write-Host ""
            Write-Host "X $Label：LIFE=0 —— 「托盘右键关闭 → 管理端退出 → 栈真的没了」这一条【没验到】。" -ForegroundColor Red
            Write-Host "  ★ 那是验收④本身，也是关栈那条路唯一算数的证据(扫源码对「点下去做没做事」什么都不会说)。" -ForegroundColor Red
            Write-Host "  ★★ 常见成因：这个环境没有可用的桌面 / window station(托盘图标与 WPF 窗口都要它)，" -ForegroundColor Red
            Write-Host "     或者那条路上多了一个模态框把自检子进程挡住了。上面那条 OWED 写了实测原因。" -ForegroundColor Red
            Remove-Item $stdout -Force -ErrorAction SilentlyContinue
            return $null
        }

        Write-Host "    $Label：PASS=$p FAIL=0"
        if ($owed -lt 0) {
            Write-Host "      ! 哨兵里没有 OWED/LIFE —— 这份自检还没有把「这个形态下测不了」与「本该跑而没跑」分开。" -ForegroundColor Yellow
            Write-Host "        ★ 不判红,但这一趟【本该跑而没跑】的那些在门禁里仍然是隐形的。" -ForegroundColor Yellow
            # ★ V36 更正:上一版这里写着「今天只有管理端那份带这两个字段;客户端那份是已知的未了项」——
            #   **那句话 V36 起是假的**。客户端哨兵现在是 `PASS FAIL SRCHIT SRCMISS SKIP OWED`
            #   (没有 LIFE:生命周期那条是管理端专有的,客户端凭空写一个 LIFE=1 等于伪造证据)。
            #   ⇒ 走到这一档只剩一种可能:这份 exe 比本脚本旧。
            Write-Host "        ★★ 两份自检 V36 起都带 OWED(客户端没有 LIFE —— 那条是管理端专有的)。" -ForegroundColor Yellow
            Write-Host "           走到这一档 = 这份 exe 比本脚本旧,先重新出一次包再对账。" -ForegroundColor Yellow
        } elseif ($skip -gt 0) {
            Write-Host "      ★ 跳过 $skip 条(OWED=0)：都是【这个形态下测不了】那一类 —— 发布产物旁边没有源码、" -ForegroundColor Yellow
            Write-Host "        这台机器上没装某个外部程序、某个端口被真东西占着。逐条原因见自检输出，" -ForegroundColor Yellow
            Write-Host "        **不要把它们读成通过**；但它们不判红，判红会天天误报。" -ForegroundColor Yellow
        }
        if ($life -eq 1) {
            Write-Host "      OK LIFE=1：托盘右键「关闭」是**真点过**的，关栈也是**真跑过**的(替身进程真的没了)。" -ForegroundColor DarkGray
        }
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

# ★ 再跑一遍【第二种目录形状】。只跑一种形状,"依赖安装位置"的断言就会溜过门禁:
#   2026-08-03 真的溜过一次(HostToolsDir 的断言在 client-pack 绿、装到 dist\client 红)。
#   代价是一次 exe 拷贝,换的是"断言必须与装在哪儿无关"这条被机械地钉住。
#
# ★★ V28 更正本段的口径 —— `$Out` 改成落位目录之后,这两种形状的**含义变了**:
#   · 从前:① = `dist\client-pack`(旁边**没有** host)· ② = 临时目录(有 stub host)。
#   · 现在:① = `dist\client`(**真的落位**,旁边就是真的 `dist\host`)
#           ② = 临时目录(仓库外 + stub host)。
#   ⇒ ① 从"一个谁也不装的中转目录"变成了**用户真正会双击的那一份**,这正是想要的:
#     门禁第一次跑在真实安装形态上。★ 而两个数仍然**不可互比** —— ② 在仓库外,
#     往上翻摸不到仓库级文件,那批【结构/接线】断言整段测不了(见下面写进 VERSION.txt 的口径)。
#   ★★★ 若 ① 这一趟因此变红而 ② 是绿的:**不要**把 `$Out` 改回去。
#     那说明有断言此前一直靠"旁边没有 host"才绿 —— 它测的是中转目录,不是产品。
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
# ★★★★ V36:`$adminDirName` / `$AdminOut` / 目录创建都**提前到 [0b]/[0c]** 了 ——
#   占用闸必须知道这一趟会写 dist\admin,否则它结构上不可能盖住管理端。
#   这里只留一句自证:走到这儿它们必须已经有值(没有 = 上面那段被人删了)。
if (-not $adminDirName -or -not $AdminOut) {
    Write-Host "X [3] 走到这儿 `$adminDirName/`$AdminOut 还是空的 —— [0b] 那段被删了?" -ForegroundColor Red
    Write-Host "  ★ 不许在这里补算一遍:补算会让占用闸(它按 [0b] 的值推产物)与真正被写的地方分家。" -ForegroundColor Red
    exit 1
}
$adminProj = Join-Path $repo '20-client-win\admin\localai-admin.csproj'
if (-not (Test-Path $adminProj)) {
    # ★ 零命中判红:找不到 = 路径写错或工程被删,两种都得当场知道,不许静默跳过。
    Write-Host "X 找不到管理端工程:$adminProj" -ForegroundColor Red; exit 1
}
Invoke-Publish '管理端' @(
    $adminProj,'-c','Release','-r','win-x64','--self-contained','true',
    '-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',"-p:InformationalVersion=$ver",
    '-o',$AdminOut,'--nologo','-v','q')
# ★ V36:`$adminExe` 已在 [0c] 的 `$writeTargets` 里算过 —— 这里不再算第二遍(理由同 [1])。
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

# ══════════════════════════════════════════════════════════════════════════════
#  ★★★ V28:客户端的 VERSION.txt **不再写管理端的数**。
#
#  「一个数写在两处」在这儿不是理论风险,是**已经发生的事故**:
#    两个目录可以来自**不同的两次构建**(而"只重出改动的那一个"恰恰是正确做法),
#    于是 `dist\client\VERSION.txt` 里那句「管理端 PASS=134」在盘上一直指着一个
#    早就不存在的数 —— 同一天 `dist\admin\VERSION.txt` 里写的是 164。
#  ★ 而当时的处置是往 P4 清单 §0 加一条「那个数不可信,管理端的数只看 dist\admin」——
#    **拿文档去糊一个本来就不该存在的第二处**。文档糊不住:读的人先读到的是包里的数。
#  ⇒ 不写第二处。管理端的数只由管理端自己的 VERSION.txt 说(就在旁边的目录里),
#    它和管理端 exe 一起出、一起过期,永远不会各说各话。
# ══════════════════════════════════════════════════════════════════════════════
$last = "自检通过（两种安装位置,均有哨兵佐证:PASS=$pass1 / PASS=$pass2,FAIL=0）"
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
# ══════════════════════════════════════════════════════════════════════════════
#  $verNote —— 这一段**从 2026-08-10 起结构上到不了**:脏树在脚本开头就判红了。
#
#  ★ 留着它不是忘了删,是把它改成一条**兜底断言**:
#    如果执行流真的走到了这里而 $dirtyFiles 非空,那说明开头那道闸被绕过了
#    (被人加了开关、被改坏了、或者工作树是在开头那次检查**之后**才变脏的
#     —— 最后这一种真会发生:构建要十几分钟,期间别人动了同一棵树)。
#  ★★ 那种情况下**不能**照旧写一段"本 exe 是脏树构建的"免责声明然后把包发出去:
#    那正是这条裁定要废掉的做法。⇒ 当场判红。
#  ★ "构建期间才变脏"那一半**不在这里查**,在 [5c] —— 这里只到 [1]~[3],
#    而那种变化要等整趟跑完才问得完整。两条判据一头一尾,与 [5c] 的 HEAD 对拍同一手法。
# ══════════════════════════════════════════════════════════════════════════════
$verNote = ""
if ($dirtyFiles.Count -gt 0) {
    Write-Host "X 走到这里时 `$dirtyFiles 非空 —— 开头那道脏树闸被绕过了。" -ForegroundColor Red
    Write-Host "  ★ 一个说不清自己是哪一版的产物,它验出来的东西也说不清是谁的。" -ForegroundColor Red
    Write-Host "  ★★ 注意 dist 已经被这一趟写过一半 —— 等树稳下来整个重出,别只补一半。" -ForegroundColor Red
    exit 1
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

★ 这一次构建**同时**出了客户端,落在:$Out
  那份客户端的版本戳与本文件里这个是同一个($ver)—— 同一次构建、同一棵源码树。
★★ 但**不要**把「两个目录的戳必须相同」当判据 —— 那条判据本身是错的:
  只重出改动的那一个是**正确**做法,戳不同很正常。要核的是
  「每个包各自含到哪一次提交」,各看各自的 VERSION.txt,不要互相推断。
  (本文件此前写的是「与旁边的 client 必须是同一个,对不上就别混用」。
   那句话在盘上长期不成立 —— 而它不成立的原因不是有人拷错了包,是判据写错了。)
★ 这个目录必须与 client\ 并排(客户端逐字探 ..\$adminDirName\localai-admin.exe),
  单独拷走会让「客户端拉起管理端 ⇒ 自动起栈」整条走不到。
$verNote
"@

# ══════════════════════════════════════════════════════════════════════════════
#  [5] ★★★ V31:主机端程序(dist\host)—— 这条出包线**漏掉的第三件产物**。
#
#  ★ 实况(2026-08-09 协调层用 ProductVersion 挖出来的,不是推测):
#      dist\host\localai-lan-edge.exe  ProductVersion = 1.0.0+0c86261f(2026-08-06)
#      dist\client / dist\admin        版本戳         = 20260809-2355+c340b3d
#    两者之间隔着 **137 个提交**,而 `git diff 0c86261..HEAD -- 10-core\lan-edge`
#    = **Program.cs +107/-3,是产品代码** —— 正是 V25 那条
#    `upstream_gateway_unreachable` / 502 的归因修复。
#
#  ★★ 后果不是"少发一个文件":V25 的副机归因是【三个半边一条链】,
#    而 lan-edge 那一半**在已部署二进制里根本不存在** ⇒ 重出 client+admin 也修不好它。
#    危害面要说准:网关活着时那条错归因**不会发生**;它在**网关一掉的瞬间**发生,
#    而那恰是副机验收会撞的形态 ⇒ 下一次副机出问题时,它会给出一个错误的原因。
#
#  ★★★ 为什么它躺了 137 个提交没人发现 —— 两个洞叠在一起:
#      ① `90-ops\*.ps1` 里**没有任何脚本发布 dist\host**(全目录 grep 过);
#      ② 那个目录里**一个版本戳都没有**(没有 VERSION.txt / SHA256.txt),
#         于是"它落后了"这件事**没有任何人会看见**。
#    ⇒ 本节把①②一起补上。只补①的话,下一次分裂照样是隐形的。
#
#  ★★ 目录名/exe 名怎么来的、以及"要被覆盖的 Edge 在不在跑"这道闸,都在 **[0]**
#    (脚本最前面)。放在那儿是实测教训:第一版把闸放在这里,于是一次被挡下的运行
#    **已经把客户端与管理端重出并覆盖了落位**,只有 host 是旧的 —— 三个目录当场分裂。
#
#  ★ 为什么连 localai-identity.exe 一起发:dist\host 里那两个 exe 是**同一次构建**的产物
#    (两个 ProductVersion 逐字相同),而 `重置并铸身份.cmd` / `续签服务器证书.cmd`
#    调的是 identity 那一个。只发一个的话,VERSION.txt 里那个戳只解释得了目录里的一半 ——
#    而"一个数说不清它在描述什么"正是本文件 V28 那段刚治过的病。
#    (查实:`git diff 0c86261..HEAD -- 10-core\identity` **为空** ——
#     identity 的源码没变,重发只是让它拿到一个说得清的戳。)
#
#  ★★ 发布形态**照抄盘上现有的那一份**(框架依赖,非单文件):
#    dist\host 今天就是 exe + dll + deps.json + runtimeconfig.json 这一形态。
#    改成自包含单文件是另一件事(体积、启动、TPM 取密钥的路径都要重验),
#    而本节要治的是"根本没发"——**不要把两件事捆在一次改动里**。
#    ⇒ 想改形态,单独一条车道,并且要在主机上实跑一次 Edge。
#
#  ★★★ 这个目录里还有**手写的、仓库里没有的**东西:
#    启动Edge.cmd · 重置并铸身份.cmd · 续签服务器证书.cmd · 吊销测试.cmd ·
#    续签验证清单.txt · 主机-开机上线.txt · renew-server-verify.ps1。
#    `dotnet publish` **不清空**输出目录,所以它们不会被删。
#    ★ **不许**为了"发得干净"往这里加任何清空/删除动作 —— 那会把主机上唯一一份
#      铸身份/续签的操作入口删掉,而它们在仓库里没有备份。
# ══════════════════════════════════════════════════════════════════════════════
Write-Host "[5] 发布主机端程序(lan-edge + identity)…"
Write-Host "     目录名/exe 名取自 AdminApp.HostToolsDirNextTo:$hostDirName\$hostExeName" -ForegroundColor DarkGray
$hostProjects = @(
    @{ Name = 'lan-edge'; Proj = (Join-Path $repo '10-core\lan-edge\localai-lan-edge.csproj');  Exe = $hostExeName }
    @{ Name = 'identity'; Proj = (Join-Path $repo '10-core\identity\localai-identity.csproj'); Exe = 'localai-identity.exe' }
)
foreach ($hp in $hostProjects) {
    if (-not (Test-Path $hp.Proj)) {
        # ★ 零命中判红:找不到 = 路径写错或工程被删,两种都得当场知道,不许静默跳过。
        Write-Host "X 找不到主机端工程($($hp.Name)):$($hp.Proj)" -ForegroundColor Red; exit 1
    }
    # ★ 框架依赖发布(不带 -r / 不带 --self-contained)—— 与盘上现有形态一致,理由见上。
    #   `-p:InformationalVersion=$ver` 是把版本戳**烧进二进制**:VERSION.txt 会被拷丢,
    #   ProductVersion 不会。这次能挖出"落后 137 个提交",靠的正是它。
    Invoke-Publish "主机端($($hp.Name))" @(
        $hp.Proj,'-c','Release',"-p:InformationalVersion=$ver",'-o',$HostOut,'--nologo','-v','q')
    if (-not (Test-Path (Join-Path $HostOut $hp.Exe))) {
        Write-Host "X 没有产出 $($hp.Exe)" -ForegroundColor Red; exit 1
    }
}
# ★ V36:`$hostExe` / `$identityExe` 已在 [0c] 的 `$writeTargets` 里算过(理由同 [1])。

# ══════════════════════════════════════════════════════════════════════════════
#  [5b] ★★★ 验:客户端旁边找得到主机端 —— 而且是**这一次发的**那一个。
#
#  ★★★ 只问"那个路径上有没有 exe"是**远远不够**的,这不是理论顾虑:
#    dist\host 里本来就躺着一个 2026-08-06 的 localai-lan-edge.exe。
#    存在性判据在把上面整段发布摘掉之后**照样绿** —— 它守不住这次要治的那个病
#    (那份 exe 恰恰是"存在但落后 137 个提交")。
#  ⇒ 判据必须问【这一次发的】:比 ProductVersion 的前缀是不是本轮的 $ver。
#    ★ 必须是**前缀比**,不是全等:SourceLink 会在 InformationalVersion 后面再接一段
#      完整 sha(实测:$ver 里已经有 `+` 时接成 `…+c340b3d.c340b3d6022…`,
#      没有 `+` 时接成 `…+27daab2f7cc…`)。写成 `-eq` 的话**每一轮都会红**,
#      而一条每轮都红的判据会在三天内被人删掉。
#  ★ 红测:把上面 [5] 那个 foreach 注释掉 ⇒ 本条当场红(旧 exe 的戳是 1.0.0+…)。
# ══════════════════════════════════════════════════════════════════════════════
Write-Host "[5b] 验:客户端旁边找得到【这一次发的】主机端…"
$expectedHostDir    = Join-Path (Split-Path $Out -Parent) $hostDirName
$asIfClientSeesHost = Join-Path $expectedHostDir $hostExeName
if ((Resolve-Norm $HostOut) -ne (Resolve-Norm $expectedHostDir)) {
    Write-Host "X 主机端发错了地方。" -ForegroundColor Red
    Write-Host "  这一次发到:  $(Resolve-Norm $HostOut)" -ForegroundColor Red
    Write-Host "  客户端会去看:$(Resolve-Norm $expectedHostDir)" -ForegroundColor Red
    Write-Host "  ★ 客户端探的是 <自己所在目录>\..\$hostDirName\$hostExeName" -ForegroundColor Red
    Write-Host "    (AdminApp.HostToolsDirNextTo,喂 HostSetup.IdentityExistsAsync 的角色证据)。" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $asIfClientSeesHost)) {
    # ★ `${}` 不是洁癖:`"$hostExeName:$path"` 会被 PS 当成 `${hostExeName:...}` 作用域限定符,
    #   整个脚本**解析失败**(实测,2026-08-09 就在这一行踩到)。变量后面紧跟冒号一律加 `${}`。
    Write-Host "X 目录对了,但那儿没有 ${hostExeName}:$asIfClientSeesHost" -ForegroundColor Red
    exit 1
}
Write-Host "     OK 客户端会去看的那个目录,就是这一次发到的目录($expectedHostDir)" -ForegroundColor DarkGray

# ══════════════════════════════════════════════════════════════════════════════
#  [5c] ★★★ 「三件产物是不是同一轮」—— 在此之前**从来没有判据守着**。
#
#  这正是 dist\host 能落后 137 个提交而无人发觉的根本原因:
#  三个目录各自独立,谁也不问一句"你们是一起出的吗"。
#  ★ 判据读的是**烧进 exe 里的 ProductVersion**,不是旁边那个 VERSION.txt ——
#    文本文件会被拷错、拷丢、手改;ProductVersion 跟着二进制走。
#  ★★ 注意这条与 V28 在管理端 VERSION.txt 里写的那句**不矛盾**:
#    那句说的是「不要拿两个**已落盘目录**的戳互相推断」(只重出改动的那个是正确做法);
#    这里说的是「**这一次构建**同时发出去的四个 exe 必须同戳」——
#    同一次 dotnet publish 都发了,戳还不一样的话只有一种解释:某一步根本没跑。
#  ★ 红测:把 [5] 那段发布摘掉 ⇒ 本条列出 lan-edge/identity 两行并判红。
#
# ══════════════════════════════════════════════════════════════════════════════
#  ★★★★ 2026-08-10 实测追加:**只比前缀是不够的**,而这一条是当天就被咬到的。
#
#  那一趟出包 17:27 开跑、17:41 跑完。**17:38 另一条车道(V32)把它自己合进了 main** ——
#  于是同一次构建里:
#      dist\client\localai-client.exe   实际编译修订 959480c   ← [1] 在 17:38 之前编的
#      dist\admin  / dist\host 三个     实际编译修订 179375a   ← 在 17:38 之后编的
#  而四个 exe 的 ProductVersion **前缀完全一致**(都是注进去的 $ver),
#  VERSION.txt 上也写着 959480c。⇒ **那份包的版本戳在说谎**,而 [5c] 全绿放行了。
#
#  ★★ 根因是一个隐含前提:`$ver` 在脚本开头算一次,然后**默认这棵树不会动**。
#    在一个多条车道各自往 main 合的仓库里,那个前提是错的 —— 而且它错得静悄悄。
#  ★★★ 露馅的是 SourceLink 在 InformationalVersion 后面接的那截**真实编译修订**
#    (`…+959480c.959480cb8be5…` vs `…+959480c.179375a6490…`)。
#    ⇒ 判据改成:四个 ProductVersion 必须**逐字完全相同**,不是"都以 $ver 开头"。
#      这一条不用调 git 就能咬住树动过 —— 它比的是二进制自己说的话。
#  ★ 再加一条 HEAD 对拍(开头一次、这里一次)兜底:万一哪天 SourceLink 被关掉,
#    上面那条会退化成恒真,而这条还在。两条都留着,理由与 D95 那张表同源。
#
#  ★★ 判红之后**不要**只是重跑就算了:这一趟已经把 dist 写成"混的"了
#    (客户端一个修订、管理端/主机端另一个)。报错里必须说清这一点 ——
#    否则下一个人会以为"红了 = 没动过盘"。
# ══════════════════════════════════════════════════════════════════════════════
Write-Host "[5c] 验:三件产物出自同一轮…"
$roundParts = @(
    @{ Name = '客户端 localai-client.exe';       Path = $exe }
    @{ Name = '管理端 localai-admin.exe';        Path = $adminExe }
    @{ Name = "主机端 $hostExeName";             Path = $hostExe }
    @{ Name = '主机端 localai-identity.exe';     Path = $identityExe }
)
$roundSeen = @()
$roundBad  = @()
foreach ($rp in $roundParts) {
    $pv = $null
    try { $pv = (Get-Item -LiteralPath $rp.Path).VersionInfo.ProductVersion } catch { $pv = $null }
    $roundSeen += [pscustomobject]@{ Name = $rp.Name; Pv = $pv }
    if ([string]::IsNullOrWhiteSpace($pv) -or (-not $pv.StartsWith($ver))) {
        $roundBad += ("     · {0}`n       烧进去的戳: {1}" -f $rp.Name, $(if ($pv) { $pv } else { '(读不到)' }))
    }
}
if ($roundBad.Count -gt 0) {
    Write-Host "X 这一轮的产物里有对不上版本戳的 —— 说明某一步【根本没跑】,不是「数字不好看」。" -ForegroundColor Red
    Write-Host "  本轮版本戳:$ver" -ForegroundColor Red
    $roundBad | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    Write-Host "  ★ 这正是 2026-08-09 那次的形状:dist\host 落后 137 个提交,而三个目录谁也不问一句。" -ForegroundColor Red
    exit 1
}
# ★★★ 前缀一致还不够:四个必须**逐字相同**。不同 = 构建期间源码树动过。
$roundDistinct = @($roundSeen | ForEach-Object { $_.Pv } | Sort-Object -Unique)
Push-Location $repo
$headNow  = (& git rev-parse --short HEAD 2>$null)
# ★★ 顺带**收尾再采一次样**:开头那道脏树闸只问了"开工时干不干净",
#   而构建要十几分钟 —— 别人在这十几分钟里动了同一棵树,产物里就混进了没提交的代码,
#   而戳仍然是那个干净提交。⇒ 一头一尾各问一次,与上面 HEAD 对拍同一手法。
$dirtyNow = @((& git status --porcelain 2>$null) -split "`r?`n" | Where-Object { $_ -match '\S' })
Pop-Location
if ($dirtyNow.Count -gt 0) {
    Write-Host "X 工作树在构建【期间】变脏了 —— 这份产物说不清自己是哪一版。" -ForegroundColor Red
    Write-Host "  开工时是干净的,此刻 $($dirtyNow.Count) 行(原样):" -ForegroundColor Red
    $dirtyNow | ForEach-Object { Write-Host "     $_" -ForegroundColor Red }
    Write-Host "  ★ 一个说不清自己是哪一版的产物,它验出来的东西也说不清是谁的。" -ForegroundColor Red
    Write-Host "  ★★ dist 已经被这一趟写过了 —— 等树稳下来整个重出,别只补一半。" -ForegroundColor Red
    Write-Host "  ★ 治本:从钉死在某提交的临时 worktree 出包,那棵树不会被人动" -ForegroundColor Red
    Write-Host "    (决议包 v31-persona-floor-and-host-pack §3b.5 有两行命令)。" -ForegroundColor Red
    exit 1
}
if ($roundDistinct.Count -gt 1 -or ($headNow -and $sha -ne 'nogit' -and $headNow -ne $sha)) {
    Write-Host "X 这一趟【构建期间源码树动过】—— 四个 exe 不是同一份代码编出来的。" -ForegroundColor Red
    Write-Host "  注进去的戳(四个一样,所以它骗得过前缀判据):$ver" -ForegroundColor Red
    Write-Host "  而各自**实际的编译修订**:" -ForegroundColor Red
    $roundSeen | ForEach-Object { Write-Host ("     · {0,-30} {1}" -f $_.Name, $_.Pv) -ForegroundColor Red }
    if ($headNow -and $headNow -ne $sha) {
        Write-Host "  HEAD:开跑时 $sha  →  现在 $headNow(多半是另一条车道在这十几分钟里合进了 main)" -ForegroundColor Red
    }
    Write-Host "  ★★ 后果:VERSION.txt 会写着开跑时那个提交,而二进制里装的是别的代码 ——" -ForegroundColor Red
    Write-Host "     **版本戳在说谎**,而这正是本脚本存在的理由。" -ForegroundColor Red
    Write-Host "  ★★★ 注意:dist 现在是**混的**(已经被这一趟写过一半),不是「红了就等于没动盘」。" -ForegroundColor Red
    Write-Host "     ⇒ 等树稳下来(问一句还有没有车道要合)再整个重出一次,别只补一半。" -ForegroundColor Red
    exit 1
}
Write-Host "     OK 四个 exe 的 ProductVersion 逐字相同,且 HEAD 全程没动($sha):$ver" -ForegroundColor DarkGray

# ══════════════════════════════════════════════════════════════════════════════
#  ★★ 版本戳与校验和(主机端)—— 在此之前 dist\host **一个都没有**。
#    没有它们的后果不是"不好查",是**落后这件事不可见**:
#    要靠有人想到去读 exe 的 ProductVersion 才能发现,而那次是隔了 137 个提交才有人想到。
# ══════════════════════════════════════════════════════════════════════════════
$hostHash     = (Get-FileHash -Algorithm SHA256 $hostExe).Hash
$identityHash = (Get-FileHash -Algorithm SHA256 $identityExe).Hash
Set-Content -Path (Join-Path $HostOut 'SHA256.txt') -Encoding utf8 -Value @"
$hostHash  $hostExeName
$identityHash  localai-identity.exe
"@
Set-Content -Path (Join-Path $HostOut 'VERSION.txt') -Encoding utf8 -Value @"
localai 主机端程序(LAN Edge + identity)
版本戳: $ver
内含:   $hostExeName · localai-identity.exe(两个都由本轮构建发出,戳相同)
构建于: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
形态:   框架依赖发布(需要本机已装 .NET 9 运行时)——**不是**自包含单文件,
        与 client\ / admin\ 那两件不同。这是有意的:改形态要单独一条车道并在主机上实跑。

★ 这个目录必须与 client\ 并排:客户端逐字探 ..\$hostDirName\$hostExeName
  (AdminApp.HostToolsDirNextTo),那是"这台能不能当主机"的一条证据。

★★ 这个目录里除了发布产物,还有**手写的、仓库里没有备份的**操作入口:
   启动Edge.cmd · 重置并铸身份.cmd · 续签服务器证书.cmd · 吊销测试.cmd ·
   续签验证清单.txt · 主机-开机上线.txt · renew-server-verify.ps1
   出包**不会**删它们(dotnet publish 不清空输出目录)。整目录搬走时请一起搬。

★★★ 本文件是 2026-08-09 才有的。在此之前这个目录**没有任何版本戳**,
   于是它落后 137 个提交(lan-edge 的 Program.cs +107/-3,V25 的 502 归因修复)
   这件事没有任何人会发现 —— 是拿 exe 的 ProductVersion 挖出来的。
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
它会问你要中枢的连接地址,并做一次配对:
  ① 在这台上点「开始寻找主机」,从找到的中枢里选一个;这台会显示【六个词】。
  ② 到**主机那台**的管理端「主机中枢」页上,把那六个词逐字核对一遍,再按「词一致,批准」。
★ 批准是在【主机】上按的,不在这台按 —— 这台只负责把六个词显示出来给你核对。
配对只做一次 —— 以后启动自动连接。

【换了路由器/网段】
不要点「解除本机配对」。打开 系统 → 设置,找到「已配对的电脑」那一节,
在卡片上直接点「改地址」即可;证书与配对原样保留。(自动发现要等 P3b.2。)

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

# ══════════════════════════════════════════════════════════════════════════════
#  [6] ★★ dist\README.txt —— **反向全表**:这个盘上还躺着什么,逐个列出来。
#
#  起因(2026-08-09):dist 下除了 client / admin / host,还躺着
#  `client-stage`(2026-08-03 的 exe,没有任何版本戳)·`_backup-20260806` ·
#  `_retired-2nd-pc-P3b`。前两个**没有任何东西说明它们是什么** ——
#  于是每个新来的人都要自己猜"哪个才是要拷走的那份",而猜错过不止一次。
#
#  ★ 这里用的是本脚本开篇那条纪律的同一手法(run-tests.ps1 的"反向全表"):
#    **不写死一张清单**,而是扫目录 —— 凡是不是这一轮发出来的,一律列出来。
#    写死清单的话,下一个人再堆一个目录进来,这份 README 会安静地漏掉它。
#
#  ★★ 只列不删,而且**不判红**:
#    · 不删 —— 这些目录里的东西没有版本戳、复现不出来,而"识别不出来"恰恰是
#      不该替别人删的理由。删除的裁定权在用户,不在出包脚本。
#    · 不判红 —— 让出包因为盘上的历史遗留而失败,只会训练人绕开门禁。
#    ⇒ 列出来 + 末尾黄字提醒,处置由人来定。
# ══════════════════════════════════════════════════════════════════════════════
$distRoot  = Split-Path $Out -Parent
$knownDirs = @($Out, $AdminOut, $HostOut) | ForEach-Object { Resolve-Norm $_ }
$otherDirs = @(Get-ChildItem -LiteralPath $distRoot -Directory -ErrorAction SilentlyContinue |
               Where-Object { $knownDirs -notcontains (Resolve-Norm $_.FullName) })
$otherLines = ""
foreach ($od in $otherDirs) {
    $newest = (Get-ChildItem -LiteralPath $od.FullName -Recurse -File -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1)
    $when   = if ($newest) { $newest.LastWriteTime.ToString('yyyy-MM-dd') } else { '(空目录)' }
    $hasVer = if (Test-Path (Join-Path $od.FullName 'VERSION.txt')) { '有 VERSION.txt' } else { '★ 没有版本戳 —— 说不清它是哪一版' }
    $note   = @(Get-ChildItem -LiteralPath $od.FullName -Filter '*退役*' -ErrorAction SilentlyContinue)
    $retire = if ($note.Count -gt 0) { '(旁边有退役说明)' } else { '' }
    $otherLines += "  · $($od.Name)`n      最新文件 $when · $hasVer $retire`n"
}
if (-not $otherLines) { $otherLines = "  (没有别的目录)`n" }

Set-Content -Path (Join-Path $distRoot 'README.txt') -Encoding utf8 -Value @"
dist —— 出包产物目录。本文件由 90-ops\build-client.ps1 **每次出包重新生成**,不要手改。
生成于 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') · 本轮版本戳 $ver

【这一轮发出来的三件(★ 要拷走的就是这三个,而且要并排)】
  client\  客户端        $(Split-Path $Out -Leaf)
  admin\   主机管理端    $(Split-Path $AdminOut -Leaf)
  host\    主机端程序    $(Split-Path $HostOut -Leaf)(LAN Edge + identity)
  ★ 三个目录必须并排:客户端逐字探 ..\$adminDirName\ 与 ..\$hostDirName\,
    单独拷走任何一个都会让「客户端拉起管理端 ⇒ 自动起栈」或角色判定走不到。
  ★★ 三件的 exe 里都烧着同一个版本戳($ver),出包时逐个对拍过(见 [5c])。
    在此之前 host\ **从来没有被任何脚本发过**,曾经落后 137 个提交而无人发现。

【这个盘上还躺着的别的目录(★ 不是这一轮出的,处置未定的请自己裁)】
$otherLines
  ★ 上面这份是**扫出来的**,不是手写清单 —— 再堆一个目录进来它也会出现在这里。
  ★★ 没有版本戳的那些:说不清是哪一版、也复现不出来。
    要拷去装机的话**不要用它们**,用上面那三个。
"@

# ══════════════════════════════════════════════════════════════════════════════
#  ★★★★ V36 · 反向:产物目录里**真的出现了**哪些 exe,必须都在 [0c] 那张表上
#
#  ★ [0c] 的主判据(「跑在会被写的目录里的任何 exe 都算占用者」)本来就不认名字,
#    所以新增一个产物 exe 在**拦截**上是自动被盖住的。这一条守的是**另一半**:
#    那张表还会被**打印给人看**(「这一趟会写的产物」),
#    一张漏了一件的表会让人以为"就这几件" —— 而下一个人会照它去关程序。
#  ★★ 判据是**扫出来的**,不是手写的:枚举三个目录里实际存在的 `*.exe`,
#    与 `$writeTargets` 对拍。多出来一件就判红,逼着人把它登记进去。
#  ★ 放在这里(全部发布完成之后)而不是开头:开头那会儿目录里还是上一轮的东西。
# ══════════════════════════════════════════════════════════════════════════════
$declaredExes = @($writeTargets | ForEach-Object { [IO.Path]::GetFullPath($_.Path) })
$actualExes = @(
    @($Out, $AdminOut, $HostOut) | Select-Object -Unique | Where-Object { Test-Path $_ } |
    ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter *.exe -File -ErrorAction SilentlyContinue } |
    ForEach-Object { [IO.Path]::GetFullPath($_.FullName) }
)
$undeclared = @($actualExes | Where-Object { $declaredExes -notcontains $_ })
if ($undeclared.Count -gt 0) {
    Write-Host ""
    Write-Host "X 产物目录里有 $($undeclared.Count) 个 exe **不在 [0c] 那张表上**:" -ForegroundColor Red
    $undeclared | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    Write-Host "  ★ 那张表是占用闸打印给人看的「这一趟会写的产物」—— 漏一件,人就会照它去关程序," -ForegroundColor Red
    Write-Host "    而漏掉的那个程序开着的时候,发布会在写它那一步炸掉,报错还指不到这儿。" -ForegroundColor Red
    Write-Host "  ⇒ 把它加进 [0c] 的 `$writeTargets(连同它属于哪一步、给人怎么称呼)。" -ForegroundColor Red
    exit 1
}
Write-Host "  OK 产物 exe 与 [0c] 占用闸那张表逐个对上($($actualExes.Count) 个)" -ForegroundColor DarkGray

# ★ V36 并发闸:干完活把锁摘掉。**尽力而为** —— 摘不掉也不要紧,
#   下一趟看见 PID 已经不在会当陈旧锁覆盖掉(判据是 PID 存活,不是文件在不在)。
Remove-Item $buildLock -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "=== 出包完成(三件,同一轮)===" -ForegroundColor Green
Write-Host "  客户端  $Out"
Write-Host "          SHA256 $hash"
Write-Host "  管理端  $AdminOut"
Write-Host "          SHA256 $adminHash"
Write-Host "  主机端  $HostOut"
Write-Host "          SHA256 $hostHash  ($hostExeName)"
Write-Host "          SHA256 $identityHash  (localai-identity.exe)"
Write-Host "  版本戳  $ver  —— 四个 exe 的 ProductVersion 已逐个对拍过(见 [5c])"
if ($otherDirs.Count -gt 0) {
    Write-Host ""
    Write-Host "  ! dist 下还有 $($otherDirs.Count) 个不是这一轮出的目录:$(($otherDirs | ForEach-Object { $_.Name }) -join '、')" -ForegroundColor Yellow
    Write-Host "    已逐个写进 $distRoot\README.txt。★ 删还是留由你裁,但别默默留着让下一个人猜。" -ForegroundColor Yellow
}
