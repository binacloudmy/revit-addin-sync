# Prunes a published plugin payload tree to what a Windows x64 Revit actually
# loads, then guards that the PDF natives survived.
#
#   installer\prune-payload.ps1 -PluginDir artifacts\plugin
#
# Per TFM subfolder (net48\, net8.0\, net10.0\):
#   - runtimes\<rid>\  deleted for every RID except win-x64 / win (QuestPDF +
#     qpdf ship 7 platforms; Revit is Windows x64 only — 56MB dead per TFM)
#   - LatoFont\        pruned to the weights QuestPDF reports use (Regular,
#     Bold, Italic, BoldItalic; see Services\ReportExporter.cs) + OFL.txt
#
# Guard: runtimes\win-x64\native\qpdf.dll + QuestPdfSkia.dll must remain in
# every TFM subfolder — fail the build rather than ship a payload that cannot
# render PDF. Must stay PowerShell 5.1-compatible (build-installer.ps1 runs
# under powershell.exe): nested Join-Path only.

param(
    [Parameter(Mandatory = $true)][string]$PluginDir
)

$ErrorActionPreference = "Stop"

$keepRids  = @("win-x64", "win")
$keepLato  = @("Lato-Regular.ttf", "Lato-Bold.ttf", "Lato-Italic.ttf",
               "Lato-BoldItalic.ttf", "OFL.txt")
$guardDlls = @("qpdf.dll", "QuestPdfSkia.dll")

$tfmDirs = Get-ChildItem $PluginDir -Directory
if (-not $tfmDirs) { throw "prune-payload: no TFM subfolders under '$PluginDir'" }

foreach ($tfm in $tfmDirs) {
    $runtimes = Join-Path $tfm.FullName "runtimes"
    if (Test-Path $runtimes) {
        Get-ChildItem $runtimes -Directory |
            Where-Object { $keepRids -notcontains $_.Name } |
            ForEach-Object {
                Write-Host "prune-payload: $($tfm.Name)/runtimes/$($_.Name) deleted"
                Remove-Item $_.FullName -Recurse -Force
            }
    }

    $lato = Join-Path $tfm.FullName "LatoFont"
    if (Test-Path $lato) {
        Get-ChildItem $lato -File |
            Where-Object { $keepLato -notcontains $_.Name } |
            ForEach-Object {
                Write-Host "prune-payload: $($tfm.Name)/LatoFont/$($_.Name) deleted"
                Remove-Item $_.FullName -Force
            }
    }

    $native = Join-Path (Join-Path $runtimes "win-x64") "native"
    foreach ($dll in $guardDlls) {
        if (-not (Test-Path (Join-Path $native $dll))) {
            throw "prune-payload: $($tfm.Name) lost runtimes/win-x64/native/$dll — refusing to ship a payload that cannot render PDF"
        }
    }
}

Write-Host "prune-payload: OK ($($tfmDirs.Count) TFM folders, win-x64 natives verified)"
