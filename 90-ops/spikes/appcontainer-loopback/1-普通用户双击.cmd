@echo off
rem ============================================================================
rem  1-普通用户双击.cmd  —— 待决 6 路线 C(AppContainer)的实测
rem
rem  ★ 请用【资源管理器双击】运行,不要「以管理员身份运行」,也不要从
rem    已提权的终端里敲 —— 那样测出来的「能/不能」不代表普通用户(D46)。
rem  ★ 不需要管理员。不改防火墙规则、不装东西、不在仓库里留编译产物;
rem    唯一会碰的机器级状态是「回环豁免列表」,跑完自己恢复。
rem  ★ 不会弹「是否允许公共网络访问此应用」—— 那个框来自绑 0.0.0.0,默认已关
rem    (要测「换成本机 LAN IP 绕不绕得过」那一格,才在 run 后面加 --lan)。
rem ============================================================================
setlocal
chcp 936 >nul
title LocalAI - AppContainer 回环隔离实测(普通用户)
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
echo  第一问:普通用户能不能自己打开回环豁免
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
