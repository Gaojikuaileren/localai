@echo off
rem ===========================================================================
rem  create-ai-op-ADMIN.cmd - double-click entry point for create-ai-op.ps1
rem
rem  WHY THIS FILE EXISTS (2026-08-06, gate-honesty lane):
rem    The local accounts ai-op / ai-ctl / ai-vigil DO NOT EXIST on this machine
rem    (measured: Get-LocalUser -> MISSING for all three; only ai-mem exists).
rem    Form A treats "ai-op has a Deny ACE on {state}" as the SECOND layer of a
rem    two-layer containment story for R1/R2/R3.  An ACE cannot exist for an
rem    account that does not exist, so that layer is absent and the protection
rem    is single-layer today, while the docs read as if it were double.
rem
rem    The first layer IS real and IS asserted: gateway.py LOCAL_DENY_ACCOUNTS
rem    contains ai-op / ai-ctl / ai-vigil by NAME, and that works whether or not
rem    the accounts exist (test_local_only_registry.py pins it).  The missing
rem    piece is only the filesystem layer.
rem
rem  WHY A .CMD AND NOT AN AGENT RUN (D46):
rem    Creating local accounts and rewriting ACLs is a system/security setting
rem    change and needs elevation.  Agent tooling here already runs elevated,
rem    and things minted from the wrong integrity level bind to the wrong
rem    context.  So this is handed to YOU to double-click, in YOUR own session.
rem
rem  ASCII ONLY, ON PURPOSE.  Same reason as run-memory-suite.cmd: this project
rem  has been bitten by cp936 more than once -- a .cmd containing non-ASCII text
rem  can mis-parse under a non-UTF8 console codepage and swallow the trailing
rem  pause, so the window closes before you can read anything.  All Chinese
rem  output comes from the .ps1 files, which PowerShell handles correctly.
rem ===========================================================================
setlocal
title LocalAI - create ai-op (needs administrator)
cd /d "%~dp0"

rem --- elevate if needed -----------------------------------------------------
rem  SELF goes through an environment variable on purpose: the repo path may
rem  contain spaces, and passing it inline through -Command quoting is where
rem  these wrappers usually break.
net session >nul 2>&1
if not errorlevel 1 goto :elevated
echo.
echo Not running as administrator. Asking for elevation...
set "SELF=%~f0"
powershell -NoProfile -Command "Start-Process -FilePath $env:ComSpec -ArgumentList '/c', $env:SELF -Verb RunAs"
exit /b

:elevated
echo ===========================================================================
echo   LocalAI - ai-op restricted account
echo ===========================================================================
echo.
echo Current state on this machine:
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "foreach ($n in 'ai-op','ai-ctl','ai-vigil','ai-mem') { $u = Get-LocalUser -Name $n -ErrorAction SilentlyContinue; if ($u) { Write-Host ('   EXISTS   ' + $n) -ForegroundColor Green } else { Write-Host ('   MISSING  ' + $n) -ForegroundColor Yellow } }"
echo.
echo   Note: ai-ctl and ai-vigil have NO creator script and do not need one.
echo   They are used only as NAMES in gateway.py LOCAL_DENY_ACCOUNTS, which
echo   works without the accounts existing.  Only ai-op carries a filesystem
echo   (Deny ACE) layer, and only ai-op is created here.
echo.
echo ---------------------------------------------------------------------------
echo   Pick one.  Both parameters of create-ai-op.ps1 are deliberately
echo   REQUIRED with no default -- a default would be making a security
echo   decision on your behalf.  Read the trade-offs it prints.
echo ---------------------------------------------------------------------------
echo.
echo   [1] Dry run  (-WhatIf)  - changes nothing, shows every step.  START HERE.
echo   [2] Create, containment = drive-wide
echo         Deny on both drive roots + explicit Allow on the repo subtree.
echo         "writable only under the repo" is TRUE in this mode.
echo         Cost: propagating inheritable ACEs can take many minutes.
echo   [3] Create, containment = enumerated
echo         Deny only on the enumerated forbidden paths.
echo         ai-op stays able to write elsewhere on those drives.
echo         verify-ai-op.ps1 will report a FAIL for that -- honest, not a bug.
echo   [4] Quit
echo.
set "PICK="
set /p PICK=Choice [1-4]:
if "%PICK%"=="1" goto :dry
if "%PICK%"=="2" goto :wide
if "%PICK%"=="3" goto :enum
goto :done

:dry
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0create-ai-op.ps1" -Membership users-group -Containment drive-wide -WhatIf
goto :verify

:wide
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0create-ai-op.ps1" -Membership users-group -Containment drive-wide
goto :verify

:enum
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0create-ai-op.ps1" -Membership users-group -Containment enumerated
goto :verify

:verify
echo.
echo ===========================================================================
echo   Now the read-only check.  It is the important one.
echo   Before the account exists it stops at section (1) with exit 1 -- that is
echo   it telling the truth, not a broken script.
echo ===========================================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-ai-op.ps1"

:done
echo.
echo Done.  Nothing above ran unless you picked 2 or 3.
pause
endlocal
