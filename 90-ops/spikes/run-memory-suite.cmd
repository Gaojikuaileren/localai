@echo off
rem ===========================================================================
rem  run-memory-suite.cmd - double-click entry point for the P3a memory suites
rem
rem  ASCII ONLY, on purpose. This project has been bitten twice by cp936:
rem  a .cmd with non-ASCII text can mis-parse under a non-UTF8 console codepage
rem  and swallow the trailing pause, so the window closes before you read it.
rem  All Chinese output comes from the .ps1, which PowerShell handles correctly.
rem
rem  What this does:
rem    - runs the 4 pure-logic memory suites as you (no credential touch), and
rem    - runs the 10 live-DB suites as ai-mem via a one-shot scheduled task.
rem  What this does NOT do: touch pg_ident / pg_hba / any PG or Qdrant config.
rem ===========================================================================
setlocal
title LocalAI - P3a memory suite

net session >nul 2>&1
if errorlevel 1 (
  echo.
  echo   Administrator is required: this starts a one-shot scheduled task
  echo   running as the ai-mem account. Relaunching elevated...
  echo.
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b 0
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-memory-suite.ps1" -Scope Full
set RC=%ERRORLEVEL%

echo.
echo   ---------------------------------------------------------------
echo   Exit code: %RC%    (0 = all green, 1 = FAIL or a suite did not run)
echo.
echo   Paste the whole window back to Claude. The red lines matter most:
echo   the memory code belongs to the P3d lane, so this script only runs
echo   the suites - it never fixes them.
echo   ---------------------------------------------------------------
echo.
pause
exit /b %RC%
