@echo off
rem ============================================================================
rem  1-普通用户双击.cmd  —— 待决 6 路线 C(AppContainer)的实测
rem
rem  ★ 编码:本文件是 UTF-8 无 BOM,配合下面的 chcp 65001。
rem    AcSpike 自己把控制台切成 UTF-8,所以 .cmd 也必须是 UTF-8,
rem    否则第二段的中文会变成乱码(踩过)。
rem  ★ 不需要管理员。不改防火墙规则、不装东西、不在仓库里留编译产物;
rem    唯一会碰的机器级状态是「回环豁免列表」,跑完自己恢复。
rem  ★ 本机 EnableLUA=0(UAC 关闭)⇒ 管理员账户的一切进程都是 High,
rem    「普通用户能不能开豁免」这一格在本机【测不出来】,程序会自己说明。
rem ============================================================================
setlocal
chcp 65001 >nul
title LocalAI - AppContainer 回环隔离实测
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
  echo [x] 找不到 dotnet。请先装 .NET 9 SDK。
  goto :done
)

set BIN=%TEMP%\localai-acspike\bin
set OBJ=%TEMP%\localai-acspike\obj\
echo == 编译勘察程序(产物全部落 TEMP,不进仓库)==
dotnet build "%~dp0AcSpike.csproj" -c Release -o "%BIN%" -p:BaseIntermediateOutputPath=%OBJ% --nologo
if errorlevel 1 (
  echo [x] 编译失败。
  goto :done
)

echo.
echo ============================================================
echo  第一问:这个上下文能不能自己打开回环豁免
echo ============================================================
"%BIN%\AcSpike.exe" user-exempt-probe

echo.
echo ============================================================
echo  第二问:AppContainer 到底挡住了什么
echo ============================================================
"%BIN%\AcSpike.exe" run

echo.
echo 把上面两段整个回贴即可。完整 JSON 在 %TEMP%\localai-acspike\out\report.json
:done
echo.
pause
endlocal
