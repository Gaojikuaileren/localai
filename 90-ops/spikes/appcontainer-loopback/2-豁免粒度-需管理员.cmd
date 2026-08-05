@echo off
rem ============================================================================
rem  2-豁免粒度-需管理员.cmd  —— 复核「豁免有没有端口粒度」
rem  ★ 编码同上:UTF-8 无 BOM + chcp 65001。
rem  ★ 会临时把一个勘察用 AppContainer 加进回环豁免列表,测完自己撤掉。
rem    撤法:先试 -d 删单条;删不掉、且【进场时列表本来是空的】才用 -c 清空。
rem ============================================================================
setlocal
chcp 65001 >nul
title LocalAI - 回环豁免的粒度
cd /d "%~dp0"

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
