# AI-75: artifact discovery + fail-closed resolution for sign-release.ps1.
#
# Factored out of the release script so the discovery/fail-closed rules can be
# unit tested with temporary local fixtures (no build, no signing, no network).

function Get-ExpectedSignedArtifacts {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepoDir,
        [Parameter(Mandatory)][string]$PluginDir,
        [Parameter(Mandatory)][string]$SetupExe
    )
    $manifestPath = Join-Path $PluginDir 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw "missing $manifestPath" }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $targets = @()
    if ($manifest.PSObject.Properties['targets']) {
        # Enumerate explicitly: member-enumeration over an EMPTY PSCustomObject
        # (targets = {}) errors under StrictMode instead of yielding nothing.
        $targets = @($manifest.targets.PSObject.Properties | ForEach-Object { $_.Value } | Select-Object -Unique)
    }
    if (-not $targets) { throw "$manifestPath has no targets map" }

    # Both loader shims are always published by build-installer.ps1 (net8 +
    # net48); a missing one is a broken build, not an addin-only release.
    $loaderDlls = @(
        (Join-Path $RepoDir 'artifacts\loader-net8\BinaLoader.dll'),
        (Join-Path $RepoDir 'artifacts\loader-net48\BinaLoader.dll')
    )
    $payloadDlls = @($targets | ForEach-Object { Join-Path (Join-Path $PluginDir $_) 'RevitWebAppSync.dll' })

    $expected = @($loaderDlls) + $payloadDlls + @($SetupExe)
    foreach ($f in $expected) {
        if (-not (Test-Path -LiteralPath $f)) { throw "expected signed artifact missing: $f" }
    }
    return $expected
}
