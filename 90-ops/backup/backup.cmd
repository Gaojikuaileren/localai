@echo off
REM ============================================================================
REM  backup.cmd - launcher for backup.ps1
REM
REM  Why this exists: the default PowerShell execution policy on Windows client
REM  is Restricted, so running backup.ps1 directly fails with "cannot be loaded
REM  because running scripts is disabled". This wrapper passes
REM  -ExecutionPolicy Bypass for THIS INVOCATION ONLY -- it does not change
REM  any system setting.
REM
REM  ASCII only on purpose: .cmd files are parsed in the OEM codepage, and
REM  non-ASCII comments corrupt parsing on a zh-CN system.
REM
REM  Usage:
REM    double-click            -> prompts for the target path
REM    backup.cmd <target>     -> backs up to <target>
REM    backup.cmd <target> -DryRun
REM ============================================================================
setlocal

set "SCRIPT=%~dp0backup.ps1"
if not exist "%SCRIPT%" (
    echo [ERROR] backup.ps1 not found next to this file.
    pause
    exit /b 1
)

set "TARGET=%~1"
if "%TARGET%"=="" (
    echo.
    echo   Local AI Hub - backup
    echo   ------------------------------------------------
    echo   Plug in the external SSD, then enter the target path.
    echo   Example:  ^<drive^>:\localAI-backup
    echo.
    set /p "TARGET=  Target path: "
)

if "%TARGET%"=="" (
    echo [CANCELLED] No target given.
    pause
    exit /b 1
)

REM Forward everything after the first argument verbatim.
set "EXTRA="
if not "%~2"=="" (
    set "EXTRA=%*"
    call set "EXTRA=%%EXTRA:*%1=%%"
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -Target "%TARGET%" %EXTRA%
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
    echo   Done.
) else (
    echo   [FAILED] exit code %RC%
)
echo.
pause
exit /b %RC%
