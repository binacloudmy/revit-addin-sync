# Unsigned staging build + publish, for the Jenkins job on the netcup Windows agent.
#
#   powershell -File installer\build-unsigned-staging.ps1 -Tag v0.0.31-staging [-EngineZip <zip>]
#
# Does what release.yml's `release` job + `publish-staging-unsigned` job did, in one place:
# builds the Staging configuration (build-installer.ps1, NO signing), zips the OTA payload,
# then publishes both with installer\publish-unsigned-staging.ps1 (flips latest.json LAST).
# Staging tags only. Prod stays signed (installer\sign-release.ps1).
# Needs: dotnet 8 + 10 SDK, Inno Setup, aws-cli, TMONE_* env vars.

param(
    [Parameter(Mandatory = $true)][string]$Tag,   # vMAJOR.MINOR.PATCH-staging
    [string]$EngineZip = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

if ($Tag -notmatch '^v(\d+\.\d+\.\d+)-staging$') {
    throw "Tag '$Tag' - want vMAJOR.MINOR.PATCH-staging. Prod is signed with sign-release.ps1, never built here."
}
$version = $Matches[1]

# Gateway URL for bina-defaults.json, from .env.staging (same rule as release.yml).
$envMap = @{}
foreach ($line in Get-Content (Join-Path $repo ".env.staging")) {
    if ($line -match '^\s*([A-Z_]+)\s*=\s*(.*?)\s*$') { $envMap[$Matches[1]] = $Matches[2] }
}
$gw = if ($envMap['GATEWAY_URL']) { $envMap['GATEWAY_URL'] } else { $envMap['BASE_URL'] }
if (-not $gw) { throw ".env.staging has neither GATEWAY_URL nor BASE_URL" }

$buildArgs = @{ Version = $version; Configuration = "Staging"; GatewayUrl = $gw }
if ($EngineZip) { $buildArgs.EngineZip = $EngineZip }
& (Join-Path $repo "installer\build-installer.ps1") @buildArgs

$setupExe = Join-Path $repo "RevitCopilot-$version-setup.exe"
if (-not (Test-Path $setupExe)) { throw "build-installer.ps1 did not produce $setupExe" }
if ((Get-Item $setupExe).Length -lt 1MB) { throw "Installer is under 1 MB - harvest failed, refusing to publish" }

# OTA zip: no pdbs, no .complete seed marker (the updater writes its own after a verified extract).
$pluginDir = Join-Path $repo "artifacts\plugin"
Get-ChildItem $pluginDir -Recurse -Filter *.pdb | Remove-Item -ErrorAction SilentlyContinue
Remove-Item (Join-Path $pluginDir ".complete") -ErrorAction SilentlyContinue

$payload = Join-Path $repo "artifacts\unsigned-payload"
Remove-Item -Recurse -Force $payload -ErrorAction SilentlyContinue
New-Item -ItemType Directory $payload | Out-Null
Compress-Archive -Path "$pluginDir\*" -DestinationPath (Join-Path $payload "RevitWebAppSync-$version.zip")
Copy-Item $setupExe $payload

& (Join-Path $repo "installer\publish-unsigned-staging.ps1") -Tag $Tag -PayloadDir $payload
