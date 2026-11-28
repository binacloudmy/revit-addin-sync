<#
.SYNOPSIS
  One-command live smoke battery for the Copilot (AI-301): find, preview, commit,
  undo, redo, highlight against the JKR EL test model, with pass/fail per step.

.DESCRIPTION
  Reads docs/copilot-live-smoke/battery.json and runs each step through a driver.
  The driver is the Windows-MCP loop from AI-269 (not in this repo). Its contract:
    pwsh <Driver> -StepId <id> -Action <prompt|ui> -Text <text> -Expect <expect>
  and it exits 0 for PASS, non-zero for FAIL (print the reason on stdout).
  Steps run in order; a FAIL does not stop later steps unless -StopOnFail.
  Exit code is the number of failed steps.

  -ValidateOnly checks the manifest locally (no Revit, no driver). Run it anywhere.

.EXAMPLE
  pwsh scripts/copilot-live-smoke.ps1 -ValidateOnly
  pwsh scripts/copilot-live-smoke.ps1 -Driver C:\vm\ai269-loop.ps1
#>
param(
    [string]$Driver,
    [string]$Battery = (Join-Path $PSScriptRoot '..\docs\copilot-live-smoke\battery.json'),
    [switch]$ValidateOnly,
    [switch]$StopOnFail
)
$ErrorActionPreference = 'Stop'
$steps = (Get-Content -Raw $Battery | ConvertFrom-Json).steps
$ids = @{}
foreach ($s in $steps) {
    foreach ($f in 'id', 'area', 'action', 'text', 'expect') {
        if (-not $s.$f) { throw "Step '$($s.id)' is missing '$f'" }
    }
    if ($s.action -notin 'prompt', 'ui') { throw "Step '$($s.id)': action must be prompt or ui" }
    if ($ids[$s.id]) { throw "Duplicate step id '$($s.id)'" }
    $ids[$s.id] = $true
}
if ($ValidateOnly) { Write-Host "Battery OK: $($steps.Count) steps"; exit 0 }
if (-not $Driver -or -not (Test-Path $Driver)) { throw 'Pass -Driver <path to the AI-269 Windows-MCP loop script> (needs Revit on the VM)' }

$results = @()
foreach ($s in $steps) {
    $out = & $Driver -StepId $s.id -Action $s.action -Text $s.text -Expect $s.expect 2>&1 | Out-String
    $ok = ($LASTEXITCODE -eq 0)
    $results += [pscustomobject]@{ Step = $s.id; Area = $s.area; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Detail = $out.Trim() }
    Write-Host ("{0,-16} {1}" -f $s.id, $results[-1].Result)
    if (-not $ok -and $StopOnFail) { break }
}
$failed = @($results | Where-Object Result -eq 'FAIL').Count
Write-Host "`n$($results.Count - $failed) passed, $failed failed"
$results | Where-Object Result -eq 'FAIL' | ForEach-Object { Write-Host "FAIL $($_.Step): $($_.Detail)" }
exit $failed
