@echo off
rem ============================================================================
rem  2-豁免粒度-需管理员.cmd  —— 只在需要复核「豁免有没有端口粒度」时才跑
rem
rem  ★ 本脚本【需要管理员】:请右键「以管理员身份运行」。
rem  ★ 它会临时把一个勘察用 AppContainer 加进回环豁免列表,测完自己撤掉。
rem    撤法:先试 -d 删单条;删不掉、且【进场时列表本来是空的】才用 -c 清空。
rem    你机器上本来就有别的回环豁免时,脚本会拒绝清空并让你手动撤 —— 这是有意的。
rem ============================================================================
setlocal
chcp 936 >nul
title LocalAI - 回环豁免的粒度(需管理员)
cd /d "%~dp0"

net session >nul 2>&1
if errorlevel 1 (
  echo [x] 没有管理员权限。请右键「以管理员身份运行」。
  goto :done
)

where dotnet >nul 2>&1
if errorlevel 1 (
  echo [x] 找不到 dotnet。请先装 .NET 9 SDK。
  goto :done
)

set BIN=%TEMP%\localai-acspike\bin
set OBJ=%TEMP%\localai-acspike\obj\
dotnet build "%~dp0AcSpike.csproj" -c Release -o "%BIN%" -p:BaseIntermediateOutputPath=%OBJ% --nologo
if errorlevel 1 goto :done

"%BIN%\AcSpike.exe" run-exempt
:done
echo.
pause
endlocal
