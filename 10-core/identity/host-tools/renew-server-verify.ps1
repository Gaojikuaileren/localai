# ============================================================================
#  renew-server-verify.ps1 - checks around `localai-identity renew-server`
#
#  Why this file exists: D49 made the server certificate renewable, but nobody
#  ever ran a renewal on the real hub. The renewal itself is one command; the
#  RISK is entirely in what it leaves behind:
#    - the OLD cert must be gone from CurrentUser\My (D49: the Edge finds its
#      cert BY THUMBPRINT, so a leftover old one keeps being served);
#    - the CA must be byte-identical (that is the ONLY reason paired devices
#      do not have to pair again);
#    - the running Edge must actually pick the new cert up.
#  None of that is visible from the renew command's own output, so this script
#  measures it instead of trusting it.
#
#  ASCII only on purpose: the .cmd launcher next to this file is parsed in the
#  OEM codepage on a zh-CN system, and this script is kept in the same style so
#  both can be edited with any tool without an encoding surprise. The Chinese
#  walkthrough for the operator lives in the accompanying .txt checklist.
#
#  Stages:
#    -Stage Pre    before renewing: snapshot + backup (makes rollback real)
#    -Stage Post   after renewing:  the invariants above
#    -Stage Live   after restarting the Edge: what it actually serves on :8443
#
#  Exit code 0 = all checks passed. Non-zero = at least one FAILED.
# ============================================================================
[CmdletBinding()]
param(
    [ValidateSet('Pre', 'Post', 'Live')]
    [string]$Stage = 'Pre',
    [int]$Port = 8443
)

$ErrorActionPreference = 'Stop'
$pass = 0
$fail = 0

function Check($ok, $msg) {
    if ($ok) { $script:pass++; Write-Host "  PASS  $msg" -ForegroundColor Green }
    else     { $script:fail++; Write-Host "  FAIL  $msg" -ForegroundColor Red }
}
function Info($msg) { Write-Host "  ....  $msg" -ForegroundColor DarkGray }

# --- locate config/paths.toml the same way the exe does (walk up from here) --
# The repo forbids absolute paths in code; the logical roots live in paths.toml.
function Get-RepoRoot {
    $d = Split-Path -Parent $PSCommandPath
    while ($d) {
        if (Test-Path (Join-Path $d 'config\paths.toml')) { return $d }
        $d = Split-Path -Parent $d
    }
    throw "config\paths.toml not found above $PSCommandPath"
}
function Get-StatePath([string]$key) {
    $toml = Join-Path (Get-RepoRoot) 'config\paths.toml'
    $inState = $false
    foreach ($raw in (Get-Content $toml)) {
        $line = $raw.Trim()
        if ($line.StartsWith('[')) { $inState = $line.StartsWith('[state]'); continue }
        if (-not $inState -or $line.StartsWith('#')) { continue }
        $eq = $line.IndexOf('=')
        if ($eq -lt 0) { continue }
        if ($line.Substring(0, $eq).Trim() -ne $key) { continue }
        $m = [regex]::Match($line.Substring($eq + 1), "'([^']*)'")
        if ($m.Success) { return $m.Groups[1].Value }
    }
    throw "[state] $key not found in $toml"
}

$idDir  = Get-StatePath 'identity'
$secDir = Get-StatePath 'secrets'
$srvCer = Join-Path $idDir 'server.cer'
$caCer  = Join-Path $idDir 'ca.cer'
$hubJs  = Join-Path $idDir 'hub.json'
$locJs  = Join-Path $secDir 'identity-locators.json'

function Load-Cert([string]$p) {
    return New-Object Security.Cryptography.X509Certificates.X509Certificate2 (,[IO.File]::ReadAllBytes($p))
}
function Sha256Hex([string]$p) {
    return (Get-FileHash -Path $p -Algorithm SHA256).Hash
}
function Backup-Root { Join-Path $idDir '_renew-backup' }
function Latest-Backup {
    $root = Backup-Root
    if (-not (Test-Path $root)) { return $null }
    return Get-ChildItem $root -Directory | Sort-Object Name -Descending | Select-Object -First 1
}

Write-Host ""
Write-Host "=== renew-server-verify : stage $Stage ===" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $srvCer)) { throw "no hub identity at $idDir (server.cer missing)" }

# ---------------------------------------------------------------- Pre
if ($Stage -eq 'Pre') {
    $srv = Load-Cert $srvCer
    $hub = Get-Content $hubJs -Raw | ConvertFrom-Json
    $left = ($srv.NotAfter - (Get-Date)).TotalDays

    Info "identity dir : $idDir"
    Info "hub id short : $($hub.hub_id_short)"
    Info "server name  : $($hub.server_name)"
    Write-Host ""
    Write-Host "  CURRENT server certificate" -ForegroundColor Yellow
    Write-Host "    thumbprint : $($srv.Thumbprint)"
    Write-Host "    not after  : $($srv.NotAfter.ToString('yyyy-MM-dd HH:mm'))"
    Write-Host ("    days left  : {0:N1}" -f $left)
    Write-Host ""
    Write-Host "  ^^ WRITE THESE DOWN. Step 3 and 4 of the checklist compare against them." -ForegroundColor Yellow
    Write-Host ""

    # Backup makes the rollback line in the checklist real instead of aspirational.
    # These are PUBLIC materials plus key LOCATORS (names), never private keys --
    # the CA/server private keys stay non-exportable in their key store.
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $dest  = Join-Path (Backup-Root) $stamp
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    foreach ($f in @($srvCer, $caCer, $hubJs, $locJs)) {
        if (Test-Path $f) { Copy-Item $f -Destination $dest -Force }
    }
    Check (Test-Path (Join-Path $dest 'server.cer')) "backed up server.cer"
    Check (Test-Path (Join-Path $dest 'ca.cer'))     "backed up ca.cer (the thing that must NOT change)"
    Check (Test-Path (Join-Path $dest 'hub.json'))   "backed up hub.json"
    Info "backup folder: $dest"
}

# ---------------------------------------------------------------- Post
if ($Stage -eq 'Post') {
    $bk = Latest-Backup
    if (-not $bk) { throw "no backup folder found under $(Backup-Root) -- run stage Pre first" }
    Info "comparing against backup: $($bk.FullName)"

    $oldSrv = Load-Cert (Join-Path $bk.FullName 'server.cer')
    $newSrv = Load-Cert $srvCer

    Write-Host ""
    Write-Host "  OLD thumbprint : $($oldSrv.Thumbprint)"
    Write-Host "  NEW thumbprint : $($newSrv.Thumbprint)"
    Write-Host "  NEW not after  : $($newSrv.NotAfter.ToString('yyyy-MM-dd HH:mm'))"
    Write-Host ""

    Check ($newSrv.Thumbprint -ne $oldSrv.Thumbprint) "a NEW server certificate was issued"
    Check ($newSrv.NotAfter -gt $oldSrv.NotAfter)     "the new certificate expires LATER than the old one"

    # THE property. CA unchanged => every device certificate ever issued still
    # verifies => nobody has to pair again. This is the whole point of D49.
    $caNow = Sha256Hex $caCer
    $caWas = Sha256Hex (Join-Path $bk.FullName 'ca.cer')
    Check ($caNow -eq $caWas) "*** the CA is byte-identical -- PAIRED DEVICES STAY VALID (D49's core promise)"

    $hubNow = (Get-Content $hubJs -Raw | ConvertFrom-Json)
    $hubWas = (Get-Content (Join-Path $bk.FullName 'hub.json') -Raw | ConvertFrom-Json)
    Check ($hubNow.hub_id -eq $hubWas.hub_id)           "hub_id unchanged"
    Check ($hubNow.server_name -eq $hubWas.server_name) "server_name unchanged (TLS host name still matches)"

    # Same key reused => the pinned trust chain is untouched.
    Check ($newSrv.GetPublicKeyString() -eq $oldSrv.GetPublicKeyString()) "server public key unchanged (same key reused)"

    # D49's named trap: the Edge looks its certificate up BY THUMBPRINT, so a
    # leftover old certificate in the personal store keeps getting served.
    $store = New-Object Security.Cryptography.X509Certificates.X509Store 'My', 'CurrentUser'
    $store.Open('ReadOnly')
    try {
        $oldInStore = $store.Certificates.Find('FindByThumbprint', $oldSrv.Thumbprint, $false)
        $newInStore = $store.Certificates.Find('FindByThumbprint', $newSrv.Thumbprint, $false)
        Check ($oldInStore.Count -eq 0) "*** the OLD certificate is GONE from CurrentUser\My (D49: leaving it keeps the expired one in use)"
        Check ($newInStore.Count -ge 1) "the NEW certificate is present in CurrentUser\My"
        if ($newInStore.Count -ge 1) {
            Check ($newInStore[0].HasPrivateKey) "the NEW certificate has its private key bound (the Edge needs this to serve TLS)"
        }
    } finally { $store.Close() }

    $locNow = Get-Content $locJs -Raw | ConvertFrom-Json
    Check ($locNow.server_thumbprint -eq $newSrv.Thumbprint) "identity-locators.json points at the NEW thumbprint"
    Check ($locNow.ca_key_name -eq (Get-Content (Join-Path $bk.FullName 'identity-locators.json') -Raw | ConvertFrom-Json).ca_key_name) "ca_key_name unchanged"
}

# ---------------------------------------------------------------- Live
if ($Stage -eq 'Live') {
    $expected = (Load-Cert $srvCer).Thumbprint
    $hub = Get-Content $hubJs -Raw | ConvertFrom-Json
    Info "expecting the Edge to serve thumbprint $expected"
    Info "server name for SNI: $($hub.server_name)"

    # The Edge binds :8443 to the LAN address, not loopback -- so try every
    # local IPv4 plus loopback rather than guessing which one is configured.
    $targets = @('127.0.0.1')
    $targets += (Get-NetIPAddress -AddressFamily IPv4 -EA SilentlyContinue |
                 Where-Object { $_.IPAddress -ne '127.0.0.1' } | ForEach-Object { $_.IPAddress })
    $seen = $null
    $where = $null
    foreach ($t in $targets) {
        $tcp = New-Object Net.Sockets.TcpClient
        try {
            $iar = $tcp.BeginConnect($t, $Port, $null, $null)
            if (-not $iar.AsyncWaitHandle.WaitOne(700)) { continue }
            $tcp.EndConnect($iar)
            $ssl = New-Object Net.Security.SslStream($tcp.GetStream(), $false, { param($a, $b, $c, $d) $true })
            try {
                $ssl.AuthenticateAsClient($hub.server_name)
                $seen = (New-Object Security.Cryptography.X509Certificates.X509Certificate2 $ssl.RemoteCertificate).Thumbprint
                $where = "${t}:$Port"
                break
            } finally { $ssl.Dispose() }
        } catch { } finally { $tcp.Close() }
    }

    if (-not $seen) {
        Check $false "could not reach the Edge on port $Port (is it running? did you restart it?)"
    } else {
        Info "connected to $where"
        Write-Host "  served thumbprint : $seen"
        Check ($seen -eq $expected) "*** the RUNNING Edge is serving the NEW certificate (if this fails: restart the Edge)"
    }
}

Write-Host ""
# ASCII summary line on purpose: anything that parses this output must survive a
# mangled codepage, so the machine-readable part uses === PASS FAIL and digits only.
Write-Host "=== RENEW-VERIFY $Stage : PASS=$pass FAIL=$fail ===" -ForegroundColor Cyan
Write-Host ""
if ($fail -gt 0) { exit 1 } else { exit 0 }
