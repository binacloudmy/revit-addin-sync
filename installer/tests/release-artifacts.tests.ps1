<#
.SYNOPSIS
  Tests for installer/lib/ReleaseArtifacts.ps1 (AI-75): artifact discovery and
  fail-closed resolution.

.DESCRIPTION
  Uses temporary local fixtures only - no build, signing, network or publishing.
  Run from anywhere:  installer\tests\release-artifacts.tests.ps1
  Exit code is the failure count.
#>
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\lib\ReleaseArtifacts.ps1')

$script:total = 0
$script:failures = 0
function Pass([string]$name) { $script:total++; Write-Host "PASS  $name" }
function Fail([string]$name, [string]$why) { $script:total++; $script:failures++; Write-Host "FAIL  $name`n      $why" }

function New-Fixture {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("ai75-artifacts-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path $root 'artifacts\loader-net8') | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'artifacts\loader-net48') | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'artifacts\plugin\net8.0') | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'artifacts\plugin\net10.0') | Out-Null
    Set-Content -LiteralPath (Join-Path $root 'artifacts\loader-net8\BinaLoader.dll') -Value 'x' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $root 'artifacts\loader-net48\BinaLoader.dll') -Value 'x' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $root 'artifacts\plugin\net8.0\RevitWebAppSync.dll') -Value 'x' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $root 'artifacts\plugin\net10.0\RevitWebAppSync.dll') -Value 'x' -Encoding ascii
    $setup = Join-Path $root 'RevitCopilot-0.0.0-setup.exe'
    Set-Content -LiteralPath $setup -Value 'x' -Encoding ascii
    @{ version = '0.0.0'; assembly = 'RevitWebAppSync.dll'; entryType = 'RevitWebAppSync.App';
       targets = @{ '2025' = 'net8.0'; '2026' = 'net8.0'; '2027' = 'net10.0' } } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'artifacts\plugin\manifest.json') -Encoding ascii
    return [pscustomobject]@{ Root = $root; PluginDir = (Join-Path $root 'artifacts\plugin'); SetupExe = $setup }
}

function Remove-Fixture($Fixture) {
    Remove-Item -LiteralPath $Fixture.Root -Recurse -Force -ErrorAction SilentlyContinue
}

function Assert-ThrowsWith([string]$Name, $Fixture, [string]$ReasonLike) {
    try {
        $r = @(Get-ExpectedSignedArtifacts -RepoDir $Fixture.Root -PluginDir $Fixture.PluginDir -SetupExe $Fixture.SetupExe)
        Fail $Name "expected a throw, got $($r.Count) artifact(s)"
    } catch {
        if ($_.Exception.Message -match $ReasonLike) { Pass $Name }
        else { Fail $Name "threw the wrong thing: $($_.Exception.Message)" }
    }
}

# 1. complete tree -> resolves both loaders + both payloads + setup EXE
$f = New-Fixture
try {
    $r = @(Get-ExpectedSignedArtifacts -RepoDir $f.Root -PluginDir $f.PluginDir -SetupExe $f.SetupExe)
    if ($r.Count -ne 5) { Fail 'complete tree resolves 5 artifacts' "expected 5, got $($r.Count)" }
    elseif (-not ($r -match 'loader-net8\\BinaLoader\.dll$')) { Fail 'complete tree resolves 5 artifacts' 'loader-net8 missing' }
    elseif (-not ($r -match 'loader-net48\\BinaLoader\.dll$')) { Fail 'complete tree resolves 5 artifacts' 'loader-net48 missing' }
    elseif (-not ($r -match 'plugin\\net8\.0\\RevitWebAppSync\.dll$')) { Fail 'complete tree resolves 5 artifacts' 'net8.0 payload missing' }
    elseif (-not ($r -match 'plugin\\net10\.0\\RevitWebAppSync\.dll$')) { Fail 'complete tree resolves 5 artifacts' 'net10.0 payload missing' }
    elseif (-not ($r -match 'setup\.exe$')) { Fail 'complete tree resolves 5 artifacts' 'setup exe missing' }
    else { Pass 'complete tree resolves 5 artifacts' }
} catch { Fail 'complete tree resolves 5 artifacts' $_.Exception.Message } finally { Remove-Fixture $f }

# 2. missing root manifest -> throws
$f = New-Fixture
Remove-Item -LiteralPath (Join-Path $f.PluginDir 'manifest.json') -Force
Assert-ThrowsWith 'missing root manifest throws' $f 'missing'
Remove-Fixture $f

# 3. empty targets map -> throws
$f = New-Fixture
@{ version = '0.0.0'; assembly = 'RevitWebAppSync.dll'; entryType = 'RevitWebAppSync.App'; targets = @{} } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $f.PluginDir 'manifest.json') -Encoding ascii
Assert-ThrowsWith 'empty targets map throws' $f 'no targets map'
Remove-Fixture $f

# 4. missing loader DLL -> throws
$f = New-Fixture
Remove-Item -LiteralPath (Join-Path $f.Root 'artifacts\loader-net48\BinaLoader.dll') -Force
Assert-ThrowsWith 'missing loader DLL throws' $f 'expected signed artifact missing'
Remove-Fixture $f

# 5. missing manifest-declared payload DLL -> throws
$f = New-Fixture
Remove-Item -LiteralPath (Join-Path $f.PluginDir 'net10.0\RevitWebAppSync.dll') -Force
Assert-ThrowsWith 'missing payload DLL throws' $f 'expected signed artifact missing'
Remove-Fixture $f

Write-Host ""
Write-Host "$($script:total - $script:failures) passed, $script:failures failed"
exit $script:failures
