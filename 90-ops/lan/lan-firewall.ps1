# P3b S5 -- LocalAI LAN Edge firewall rule (D43 S0.6). Narrow, fail-closed-by-default open of 8443/TCP.
#
# ★ YOU run this (elevated). It is the outward-facing "open the LAN" step; Claude writes/verifies it
#   but does not execute it. It opens ONE port, ONLY on the NIC + Private profile + LocalSubnet you name.
#
# Usage (elevated PowerShell):
#   .\lan-firewall.ps1 -InterfaceAlias "Ethernet" -Program "<full path>\localai-lan-edge.exe"    # add
#   .\lan-firewall.ps1 -Remove                                                                    # remove
#
# ASCII-only on purpose (PS 5.1 reads a no-BOM utf-8 .ps1 as ANSI).

[CmdletBinding()]
param(
    [string]$InterfaceAlias,
    [string]$Program,
    [int]$Port = 8443,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$RuleName = 'LocalAI-LAN-Edge'

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This must run in an ELEVATED PowerShell (firewall changes need admin)."
    }
}

Assert-Admin

if ($Remove) {
    Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Write-Output "removed firewall rule '$RuleName' (LAN closed; Edge falls back to loopback-only)."
    return
}

if (-not $InterfaceAlias) { throw "-InterfaceAlias is required (e.g. 'Ethernet' / 'Wi-Fi'). See: Get-NetConnectionProfile" }
if (-not $Program) { throw "-Program is required (full path to localai-lan-edge.exe)" }
if (-not (Test-Path $Program)) { throw "Program not found: $Program" }

# 1) the chosen network must be Private, not Public (D43 S0.6: do not silently open on Public).
$profile = Get-NetConnectionProfile -InterfaceAlias $InterfaceAlias -ErrorAction Stop
if ($profile.NetworkCategory -eq 'Public') {
    throw ("Network on '$InterfaceAlias' is categorized PUBLIC. Fix it first (Set it to Private for your " +
           "home network), then re-run. We do NOT open the port on a Public network.")
}
Write-Output ("network '$InterfaceAlias' category = " + $profile.NetworkCategory)

# 2) scan for a conflicting BROAD allow rule on the same port (Any profile / no interface scoping).
$conflicts = Get-NetFirewallRule -Direction Inbound -Action Allow -Enabled True -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -ne $RuleName } |
    ForEach-Object {
        $pf = $_ | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue
        if ($pf -and $pf.Protocol -eq 'TCP' -and ($pf.LocalPort -eq "$Port" -or $pf.LocalPort -contains "$Port")) { $_ }
    }
if ($conflicts) {
    Write-Output "WARNING: existing inbound allow rule(s) also cover TCP $Port -- review/remove them so the narrow rule is authoritative:"
    $conflicts | ForEach-Object { Write-Output ("  - " + $_.DisplayName + " (Profile=" + $_.Profile + ")") }
}

# 3) replace any prior LocalAI rule, then create the narrow one.
Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule `
    -DisplayName $RuleName `
    -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port `
    -Program $Program `
    -Profile Private `
    -InterfaceAlias $InterfaceAlias `
    -RemoteAddress LocalSubnet `
    -EdgeTraversalPolicy Block | Out-Null

Write-Output ""
Write-Output ("created narrow firewall rule '$RuleName':")
Write-Output ("  TCP $Port  |  Program=$Program  |  Profile=Private  |  Interface=$InterfaceAlias  |  Remote=LocalSubnet  |  EdgeTraversal=Block")
Write-Output ""
Write-Output "reminder: no router port-forward / UPnP / DDNS. This opens the port ONLY to your local subnet on this one NIC."
Write-Output "to close: .\lan-firewall.ps1 -Remove"
