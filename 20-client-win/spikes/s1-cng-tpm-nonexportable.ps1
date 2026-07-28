# P3b S1 / Spike 1 -- TPM/CNG P-256 non-exportable key
# LocalAI, decision D43 (S0 -> S1). Loopback-only, no network, no firewall.
#
# Proves on THIS machine that the client-key primitive the whole mTLS design
# rests on actually holds:
#   * a P-256 signing key can be created in the TPM 2.0 KSP with ExportPolicy=None
#   * private-key export is REFUSED (both PKCS8 and ECC blob forms)
#   * public-key export SUCCEEDS (needed to build a CSR later)
#   * signing works and verifies; tampered data fails
#
# Creates a named key and DELETES it in the finally block (no residue).
# ASCII-only on purpose (PS 5.1 reads a no-BOM utf-8 .ps1 as ANSI).

$ErrorActionPreference = 'Stop'
$KeyName  = 'localai-spike-s1-tpm'
$Provider = 'Microsoft Platform Crypto Provider'   # TPM 2.0 KSP
$script:pass = 0; $script:fail = 0

function Assert($cond, $msg) {
  if ($cond) { $script:pass++; Write-Output ("  PASS  " + $msg) }
  else       { $script:fail++; Write-Output ("  FAIL  " + $msg) }
}

$prov = New-Object System.Security.Cryptography.CngProvider($Provider)

# remove any leftover key from a prior run
try {
  if ([System.Security.Cryptography.CngKey]::Exists($KeyName, $prov)) {
    ([System.Security.Cryptography.CngKey]::Open($KeyName, $prov)).Delete()
    Write-Output "note: removed leftover spike key from a prior run"
  }
} catch { Write-Output ("note: leftover cleanup skipped: " + $_.Exception.Message) }

$key = $null
try {
  $p = New-Object System.Security.Cryptography.CngKeyCreationParameters
  $p.Provider     = $prov
  $p.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::None
  $p.KeyUsage     = [System.Security.Cryptography.CngKeyUsages]::Signing
  $key = [System.Security.Cryptography.CngKey]::Create(
           [System.Security.Cryptography.CngAlgorithm]::ECDsaP256, $KeyName, $p)

  Assert ($key -ne $null) "created P-256 key"
  Assert ($key.Provider.Provider -eq $Provider) ("key lives in TPM provider: " + $key.Provider.Provider)
  Assert ($key.ExportPolicy -eq [System.Security.Cryptography.CngExportPolicies]::None) "ExportPolicy = None"

  # private-key export MUST fail (two blob forms)
  $exP8 = $false
  try { [void]$key.Export([System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob); $exP8 = $true } catch { $exP8 = $false }
  Assert (-not $exP8) "private-key export (Pkcs8PrivateBlob) REFUSED"

  $exEcc = $false
  try { [void]$key.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob); $exEcc = $true } catch { $exEcc = $false }
  Assert (-not $exEcc) "private-key export (EccPrivateBlob) REFUSED"

  # public-key export MUST succeed
  $pub = $null
  try { $pub = $key.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob) } catch {}
  Assert ($pub -ne $null -and $pub.Length -gt 0) ("public-key export SUCCEEDS (" + ($(if($pub){$pub.Length}else{0})) + " bytes)")

  # sign + verify with the TPM key
  $ecdsa = New-Object System.Security.Cryptography.ECDsaCng($key)
  $data  = [System.Text.Encoding]::UTF8.GetBytes('localai-p3b-s1-spike')
  $sig   = $ecdsa.SignData($data, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
  Assert ($sig.Length -gt 0) ("signing with TPM key SUCCEEDS (" + $sig.Length + " bytes)")
  Assert ($ecdsa.VerifyData($data, $sig, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) "signature verifies"
  $tamper = $data.Clone(); $tamper[0] = $tamper[0] -bxor 0xFF
  Assert (-not $ecdsa.VerifyData($tamper, $sig, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) "tampered data FAILS verification"
}
finally {
  if ($key -ne $null) {
    try { $key.Delete(); Write-Output "cleanup: spike key deleted (no residue)" }
    catch { Write-Output ("cleanup WARN: " + $_.Exception.Message) }
  }
}

Write-Output ""
Write-Output ("S1-Spike1 result: PASS=" + $script:pass + " FAIL=" + $script:fail)
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
