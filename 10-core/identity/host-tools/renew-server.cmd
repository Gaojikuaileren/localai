@echo off
chcp 65001 >nul
title LocalAI - renew the hub SERVER certificate
setlocal

REM ============================================================================
REM  Renew the hub's SERVER certificate (D49) on the real machine.
REM
REM  ASCII only on purpose: .cmd files are parsed in the OEM codepage, and
REM  non-ASCII text here corrupts parsing on a zh-CN system. The Chinese
REM  walkthrough is in the .txt checklist next to this file.
REM
REM  Why a human double-clicks this instead of the agent running it:
REM  this is a PRODUCTION IDENTITY CHANGE. It rewrites server.cer and removes
REM  the previous certificate from the personal certificate store. That should
REM  be authorised by you, not done on your behalf.
REM  (It is NOT because of an integrity-level limit -- on this machine
REM  EnableLUA=0, so a double-click and the agent run at the same level.)
REM
REM  What it does NOT do: it does not touch the CA, the hub id or the server
REM  name. That is exactly why PAIRED DEVICES DO NOT HAVE TO PAIR AGAIN.
REM ============================================================================

set "PS=powershell -NoProfile -ExecutionPolicy Bypass -File"
set "VERIFY=%~dp0renew-server-verify.ps1"
set "IDENTITY=%~dp0localai-identity.exe"

if not exist "%VERIFY%"   ( echo [x] missing: %VERIFY%   & goto :die )
if not exist "%IDENTITY%" ( echo [x] missing: %IDENTITY% & goto :die )

echo.
echo ============================================================
echo   STEP 1 of 5  -  what we have right now (read-only)
echo ============================================================
%PS% "%VERIFY%" -Stage Pre
if errorlevel 1 ( echo. & echo [x] STEP 1 failed - stopping before changing anything. & goto :die )

echo.
echo ============================================================
echo   STEP 2 of 5  -  renew
echo ============================================================
echo.
echo   This rewrites the hub's server certificate.
echo   The CA, the hub id and the server name are NOT touched,
echo   so every already-paired device stays valid.
echo.
set "GO="
set /p "GO=Type  yes  and press Enter to renew (anything else aborts): "
if /i not "%GO%"=="yes" ( echo. & echo Aborted. Nothing was changed. & goto :end )

echo.
"%IDENTITY%" renew-server
if errorlevel 1 ( echo. & echo [x] renew-server reported an error - see the message above. & goto :die )

echo.
echo ============================================================
echo   STEP 3 of 5  -  verify what the renewal left behind
echo ============================================================
%PS% "%VERIFY%" -Stage Post
if errorlevel 1 ( echo. & echo [x] STEP 3 found a problem - read the FAIL lines above, then see the checklist. & goto :die )

echo.
echo ============================================================
echo   STEP 4 of 5  -  restart the Edge, then come back here
echo ============================================================
echo.
echo   1. Close the LocalAI Edge window if it is running.
echo   2. Start it again the usual way.
echo   3. Then press any key in THIS window to check what it serves.
echo.
pause >nul
%PS% "%VERIFY%" -Stage Live
if errorlevel 1 ( echo. & echo [x] STEP 4 failed - the running Edge is NOT serving the new certificate. & goto :die )

echo.
echo ============================================================
echo   STEP 5 of 5  -  the one that actually matters (do this by hand)
echo ============================================================
echo.
echo   Go to the SECOND PC and open the LocalAI client.
echo   It must connect WITHOUT pairing again.
echo.
echo   That is the promise D49 was written to keep, and it has never
echo   once been verified on real hardware - only in the self-test.
echo.
echo   *** If it asks you to pair again: DO NOT DO IT. ***
echo   Re-pairing deletes that machine's private key and destroys an
echo   identity that is still perfectly valid. Read the rollback
echo   section of the checklist next to this file instead.
echo.

:end
echo.
echo Press any key to close this window.
pause >nul
endlocal
exit /b 0

:die
echo.
echo Press any key to close this window.
pause >nul
endlocal
exit /b 1
