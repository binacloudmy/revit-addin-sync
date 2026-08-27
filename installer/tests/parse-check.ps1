<#
.SYNOPSIS
  Parse (never execute) every installer\*.ps1 and fail on any syntax error.

.DESCRIPTION
  Run under BOTH hosts in CI: pwsh 7, and Windows PowerShell 5.1 - the latter
  is what the ONLOGON scheduled task actually runs engine-boot.ps1 under, and
  5.1 rejects syntax 7 accepts. Typed [ref] variables on purpose: 5.1 cannot
  bind [ref]$null to ParseFile's out-params (pwsh 7 can), which is exactly the
  kind of host difference this script exists to catch.

  Usage:  powershell.exe -NoProfile -File installer\tests\parse-check.ps1
          pwsh          -NoProfile -File installer\tests\parse-check.ps1
  Exit code is the number of files that failed to parse.
#>
param(
    [string]$Root = (Join-Path $PSScriptRoot '..')
)
$ErrorActionPreference = 'Stop'

$host_ = "$($PSVersionTable.PSEdition) $($PSVersionTable.PSVersion)"
$bad = 0
foreach ($f in Get-ChildItem -LiteralPath $Root -Recurse -Filter *.ps1) {
    # ASCII only. These files carry no BOM (editors strip them), and without
    # one Windows PowerShell 5.1 decodes UTF-8 as cp1252 - an em dash's third
    # byte 0x94 becomes a CLOSING QUOTE, the string ends early, and the parse
    # collapses. That is exactly how engine-boot.ps1 failed under 5.1 on
    # 2026-08-27 while parsing clean under pwsh 7. Reject at the byte level so
    # it cannot come back through a comment.
    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
    $hi = @($bytes | Where-Object { $_ -ge 128 })
    if ($hi.Count) {
        $bad++
        Write-Host "FAIL  $($f.Name)  ($host_)"
        Write-Host "      $($hi.Count) non-ASCII byte(s) - cp1252 mis-decode risk under Windows PowerShell 5.1; use plain '-', '->', '...'"
        continue
    }
    [System.Management.Automation.Language.Token[]]$tokens = $null
    [System.Management.Automation.Language.ParseError[]]$errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tokens, [ref]$errors)
    if ($errors -and $errors.Count) {
        $bad++
        Write-Host "FAIL  $($f.Name)  ($host_)"
        foreach ($e in $errors) { Write-Host "      line $($e.Extent.StartLineNumber): $($e.Message)" }
    } else {
        Write-Host "ok    $($f.Name)  ($host_)"
    }
}
if ($bad) { Write-Host "$bad script(s) failed to parse under $host_" }
exit $bad
