# Adds the Android package to version.json and re-signs it.
# Run this on the Beast as daemonlinks\ahc. The code-signing key does not leave
# the Windows certificate store. Do not copy version.json.p7s from another machine.
#
#   powershell -File universal/scripts/Publish-AndroidRelease.ps1 `
#     -Version 0.2.0 `
#     -ApkUrl https://github.com/daemonlinks/alice-voice-desktop/releases/download/v0.2.0/AliceVoice.AppU.apk `
#     -Sha256 <64 hex chars from (Get-FileHash the.apk).Hash>
#
# Default thumbprint is CN=AHC, valid until 2028-10-29. Renew before then
# (Get-Certificate -Template AliceVoiceCodeSigning) and pass the new thumbprint.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$ApkUrl,
    [Parameter(Mandatory = $true)][string]$Sha256,
    [string]$Thumbprint = "9F5116C32A280552E38F7FA87967EDED6C9C34C2",
    [string]$Manifest = "version.json"
)

$ErrorActionPreference = "Stop"
if ($ApkUrl -notmatch '^https://') { throw "ApkUrl must be https." }
if ($Sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "Sha256 must be 64 hex characters." }
if (-not (Test-Path $Manifest)) { throw "Manifest not found: $Manifest" }

$json = Get-Content -Raw -Path $Manifest | ConvertFrom-Json
$android = [ordered]@{
    version     = $Version
    downloadUrl = $ApkUrl
    sha256      = $Sha256.ToLowerInvariant()
}
# Keep the Windows fields. ConvertFrom-Json / ConvertTo-Json round-trips them.
$json | Add-Member -NotePropertyName android -NotePropertyValue ([pscustomobject]$android) -Force
$text = ($json | ConvertTo-Json -Depth 6) -replace "`r`n", "`n"
if (-not $text.EndsWith("`n")) { $text += "`n" }
[System.IO.File]::WriteAllText((Resolve-Path $Manifest), $text, (New-Object System.Text.UTF8Encoding $false))

Write-Host "Wrote android $Version to $Manifest. Signing with $Thumbprint (the key stays in the certificate store)."
dotnet run --project tools/AliceVoice.ReleaseSigner -c Release -- sign --thumbprint $Thumbprint --file $Manifest
if ($LASTEXITCODE -ne 0) { throw "Signing failed ($LASTEXITCODE). version.json.p7s was not updated." }
Write-Host "Signed. Commit version.json and version.json.p7s together. Any later edit invalidates the signature."
