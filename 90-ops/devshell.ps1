<#
.SYNOPSIS
    进入本项目的原生构建环境(MSVC + CUDA + CMake + Ninja)

.DESCRIPTION
    编译 llama.cpp 的 CUDA 后端、或任何 sm_120 扩展时用这个。

    为什么需要它:把 cmake 加进 PATH 是不够的 —— 编译还需要 cl.exe、link.exe、
    Windows SDK 头文件和库路径,这些只有在 VS 的 vcvars64 初始化之后才存在于环境里。
    本脚本把 vcvars64 的环境导入当前 PowerShell 会话,并校验整条链齐全。

    定位 VS 用官方的 vswhere,不写死安装路径 —— 遵守 v2.2 §11.1「代码中禁止绝对路径」。

.PARAMETER Check
    只检查不导入,用于确认工具链完整性。

.EXAMPLE
    . .\90-ops\devshell.ps1          # 注意前面的点:必须 dot-source 才能改当前会话的环境
    .\90-ops\devshell.ps1 -Check     # 仅检查
#>

[CmdletBinding()]
param([switch]$Check)

$ErrorActionPreference = 'Stop'

# --- 刷新 PATH ---------------------------------------------------------------
# 安装 CUDA / 改过系统 PATH 之后,已经开着的会话不会自动继承新值。
# 从注册表重新拼一次,否则 nvcc 可能"明明装了却找不到"。
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' +
            [Environment]::GetEnvironmentVariable('Path','User')

# --- 定位 Visual Studio(不硬编码路径)---------------------------------------
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw "找不到 vswhere。Visual Studio 2017+ 未安装,或安装器组件缺失。"
}

$vsPath = & $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $vsPath) {
    throw "vswhere 没找到带 C++ 工具集的 VS 实例。需要「使用 C++ 的桌面开发」工作负载。"
}

$vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) {
    throw "找到了 VS ($vsPath) 但缺 vcvars64.bat —— C++ 工作负载可能不完整。"
}

Write-Host "Visual Studio : $vsPath" -ForegroundColor Cyan

# --- CUDA -------------------------------------------------------------------
$cudaPath = $env:CUDA_PATH
if (-not $cudaPath) { $cudaPath = [Environment]::GetEnvironmentVariable('CUDA_PATH','Machine') }
if (-not $cudaPath) { $cudaPath = [Environment]::GetEnvironmentVariable('CUDA_PATH','User') }

if ($cudaPath -and (Test-Path $cudaPath)) {
    $env:CUDA_PATH = $cudaPath
    $cudaBin = Join-Path $cudaPath 'bin'
    if ($env:Path -notlike "*$cudaBin*") { $env:Path = "$cudaBin;$env:Path" }
    $ver = (Get-Content (Join-Path $cudaPath 'version.json') -Raw -ErrorAction SilentlyContinue)
    Write-Host "CUDA          : $cudaPath" -ForegroundColor Cyan
} else {
    Write-Warning "CUDA_PATH 未设置或无效。CUDA Toolkit 可能未安装 —— 只能做纯 CPU 构建。"
}

if ($Check) {
    Write-Host ''
    Write-Host '仅检查模式,未导入环境。' -ForegroundColor Yellow
}

# --- 导入 vcvars64 的环境 ----------------------------------------------------
if (-not $Check) {
    $before = @{}
    Get-ChildItem env: | ForEach-Object { $before[$_.Name] = $_.Value }

    # 让 cmd 执行 vcvars64 然后 dump 环境,逐行搬回 PowerShell
    $lines = cmd /c "`"$vcvars`" >nul 2>&1 && set"
    $n = 0
    foreach ($line in $lines) {
        if ($line -match '^([^=]+)=(.*)$') {
            $name = $Matches[1]; $value = $Matches[2]
            if ($before[$name] -ne $value) { $n++ }
            Set-Item -Path "env:$name" -Value $value -ErrorAction SilentlyContinue
        }
    }
    Write-Host "已导入 vcvars64 环境($n 个变量变更)" -ForegroundColor Green
}

# --- 校验整条链 --------------------------------------------------------------
Write-Host ''
Write-Host '工具链:' -ForegroundColor Cyan

$tools = [ordered]@{
    'cl'    = '-help'          # MSVC 编译器
    'link'  = ''               # 链接器
    'nvcc'  = '--version'      # CUDA 编译器
    'cmake' = '--version'
    'ninja' = '--version'
}

$missing = @()

# 版本探测只是附加信息,不该让整个脚本挂掉。
# 且 PS 5.1 对原生 exe 做 2>&1 会把 stderr 包装成 ErrorRecord 并令 $? 为 false
# (cl.exe 不带参数正是把版权信息写到 stderr) —— 所以重定向交给 cmd 做。
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'

foreach ($t in $tools.Keys) {
    $c = Get-Command $t -ErrorAction SilentlyContinue
    if ($c) {
        $ver = ''
        try {
            switch ($t) {
                'cl'    { $ver = (cmd /c "cl 2>&1" | Select-Object -First 1) }
                'link'  { $ver = (cmd /c "link 2>&1" | Select-Object -First 1) }
                'nvcc'  { $ver = (cmd /c "nvcc --version 2>&1" | Select-String 'release' | Select-Object -First 1) }
                'cmake' { $ver = (cmake --version | Select-Object -First 1) }
                'ninja' { $ver = 'ninja ' + (ninja --version | Select-Object -First 1) }
            }
        } catch { $ver = '(版本探测失败,但程序存在)' }
        Write-Host ("  {0,-6} OK   {1}" -f $t, ("$ver" -replace '\s+', ' ').Trim())
    } else {
        Write-Host ("  {0,-6} 缺失" -f $t) -ForegroundColor Red
        $missing += $t
    }
}

$ErrorActionPreference = $prevEap
# link/cl 不带参数会返回非零退出码,别让它污染本脚本的结果
$global:LASTEXITCODE = 0

Write-Host ''
if ($missing.Count -eq 0) {
    Write-Host '构建环境就绪。' -ForegroundColor Green
    if ($Check) { Write-Host '(记得 dot-source 才会作用于当前会话:  . .\90-ops\devshell.ps1)' -ForegroundColor Yellow }
} else {
    Write-Host ("缺少: {0}" -f ($missing -join ', ')) -ForegroundColor Red
    if ($Check) { Write-Host 'cl / link 在仅检查模式下必然缺失 —— 它们要 vcvars64 导入后才出现。' -ForegroundColor Yellow }
}
