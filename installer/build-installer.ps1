# Revit Copilot — installer build script (run on Windows with .NET 8 SDK).
#
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 0.0.8
#
# Builds the OTA layout with Inno Setup: BinaLoader (goes into Revit Addins
# folders) plus the seed plugin build (goes into
# %LocalAppData%\Bina\RevitSync\versions\<ver>). CI does the same thing in
# .github/workflows/release.yml.
#
# Optional: pass -EngineZip <path to bina-engine-<ver>.zip> to also seed the
# colocated Copilot Engine (see scripts/build-engine-bundle.ps1 in bina-ai).
# The zip's engine-version.json supplies the version the addin's EngineManager
# will look for under %LocalAppData%\Bina\RevitSync\engine\<ver>\. Omit it and
# the output is byte-identical to an addin-only build.
#
#   installer\build-installer.ps1 -Version 0.0.8 -EngineZip dist\bina-engine-1.0.0.zip
#
# Optional: code-sign both the installer EXE and the embedded uninstaller via
# Inno Setup's native SignTool mechanism (needs an EV/OV cert; without
# signing, SmartScreen shows an "unknown publisher" warning but the install
# still works). Two ways to supply the cert, never committed anywhere:
#
#   installer\build-installer.ps1 -Version 0.0.8 -SignCert <cert-thumbprint>
#   installer\build-installer.ps1 -Version 0.0.8 -SignCert C:\path\cert.pfx -SignPassword <pw>
#
# or, to keep the password off the command line entirely (e.g. CI secret),
# pre-build the full signtool argument list into an env var:
#
#   $env:SIGNTOOL_ARGS = '/f C:\path\cert.pfx /p <pw> /fd SHA256 /tr http://timestamp.digicert.com /td SHA256'
#   installer\build-installer.ps1 -Version 0.0.8
#
# -Sign/-Thumbprint (legacy) still works and signs only the setup EXE
# post-compile; -SignCert/SIGNTOOL_ARGS supersede it when both are given.
#
# Zero-config release: pass -GatewayUrl so a drafter's install never needs a
# hand-edited config.json. The addin's BinaConfig.ApplyDefaults() reads this
# back on first run (see BinaConfig.cs) and, when a colocated engine bundle
# is ALSO present (-EngineZip), auto-enables Engine mode + points AIBaseUrl
# at the local engine. Omit it and the installer is addin-only/cloud-mode,
# exactly as before this flag existed.
#
#   installer\build-installer.ps1 -Version 0.0.8 -GatewayUrl https://gw.binacloud.ai

param(
    [string]$Version = "0.0.1",
    [string]$Configuration = "Release",
    [switch]$Sign,
    [string]$Thumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$EngineZip = "",
    [string]$SignCert = "",
    [string]$SignPassword = "",
    [string]$GatewayUrl = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version '$Version' must be MAJOR.MINOR.PATCH"
}

$pluginDir     = Join-Path $repo "artifacts\plugin"
$loaderNet8Dir  = Join-Path $repo "artifacts\loader-net8"
$loaderNet48Dir = Join-Path $repo "artifacts\loader-net48"
$iss       = Join-Path $repo "installer\RevitCopilot.iss"
$exe       = Join-Path $repo "RevitCopilot-$Version-setup.exe"

Remove-Item -Recurse -Force (Join-Path $repo "artifacts") -ErrorAction SilentlyContinue

# Multi-year payload layout (see BinaLoader/LoaderApp.cs ResolvePayloadDir):
#   versions\<ver>\manifest.json   root manifest with the year->subfolder map
#   versions\<ver>\net8.0\         Revit 2025 + 2026 (both .NET 8 hosts)
#   versions\<ver>\net10.0\        Revit 2027 (.NET 10 host)
#   versions\<ver>\net48\          Revit 2024 (Phase B — when the csproj grows net48)
# ALWAYS publish with an explicit -f: publishing a multi-TFM project without
# one is what shipped a net10 payload to a .NET 8 Revit (2026-07-13 incident).
$pluginTargets = [ordered]@{ "net8.0-windows" = "net8.0"; "net10.0-windows" = "net10.0" }
$plugincsproj = [xml](Get-Content (Join-Path $repo "RevitWebAppSync.csproj"))
if ($plugincsproj.Project.PropertyGroup.TargetFrameworks -like "*net48*") {
    $pluginTargets["net48"] = "net48"    # Phase B auto-activates
}
$yearMap = [ordered]@{}
foreach ($tfm in $pluginTargets.Keys) {
    $sub = $pluginTargets[$tfm]
    $out = Join-Path $pluginDir $sub
    Write-Host "==> Publishing plugin $Version ($Configuration, $tfm -> $sub)..." -ForegroundColor Cyan
    dotnet publish "RevitWebAppSync.csproj" -c $Configuration -f $tfm -o $out -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "plugin publish failed for $tfm" }
    # Per-payload manifest: what the loader's Instantiate() reads in the dir it loads.
    @{ version = $Version; assembly = 'RevitWebAppSync.dll'; entryType = 'RevitWebAppSync.App' } |
        ConvertTo-Json | Set-Content (Join-Path $out "manifest.json")
    switch ($sub) {
        "net48"   { $yearMap["2023"] = $sub; $yearMap["2024"] = $sub }
        "net8.0"  { $yearMap["2025"] = $sub; $yearMap["2026"] = $sub }
        "net10.0" { $yearMap["2027"] = $sub }
    }
}

Write-Host "==> Publishing loaders..." -ForegroundColor Cyan
dotnet publish "BinaLoader\BinaLoader.csproj" -c $Configuration -f net8.0-windows -o $loaderNet8Dir -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "loader publish failed for net8.0-windows" }
Copy-Item -Force (Join-Path $repo "BinaLoader\BinaSync.addin") $loaderNet8Dir
dotnet publish "BinaLoader\BinaLoader.csproj" -c $Configuration -f net48 -o $loaderNet48Dir -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "loader publish failed for net48" }
Copy-Item -Force (Join-Path $repo "BinaLoader\BinaSync.addin") $loaderNet48Dir

# Root manifest (year->subfolder map the loader keys on) + completeness marker
# — the seed folder must look exactly like one staged by UpdateService.
@{ version = $Version; assembly = 'RevitWebAppSync.dll'; entryType = 'RevitWebAppSync.App'; targets = $yearMap } |
    ConvertTo-Json | Set-Content (Join-Path $pluginDir "manifest.json")
Set-Content (Join-Path $pluginDir ".complete") $Version

# Prune to what a Windows x64 Revit loads (non-win RIDs, unused Lato weights)
# + guard the PDF natives. Before signing: fewer files to sign, and a broken
# prune fails here rather than after a cert round-trip.
Write-Host "==> Pruning payload..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "prune-payload.ps1") -PluginDir $pluginDir

# Optional: stage the packaged Copilot Engine into the layout the .iss packs.
# EngineDir/EngineVersion are only passed to ISCC when -EngineZip is given —
# without it, the /D flags below are omitted entirely and RevitCopilot.iss
# falls back to its own (nonexistent-by-default) engine dir, which its
# skipifsourcedoesntexist flag skips cleanly. Net effect: no -EngineZip ->
# byte-identical ISCC invocation to before this feature existed.
$engineIscArgs = @()
if ($EngineZip) {
    if (-not (Test-Path $EngineZip)) { throw "-EngineZip path not found: $EngineZip" }
    $engineDir = Join-Path $repo "artifacts\engine"
    Write-Host "==> Staging engine bundle $EngineZip..." -ForegroundColor Cyan
    Expand-Archive -Path $EngineZip -DestinationPath $engineDir -Force
    $engineManifestPath = Join-Path $engineDir "engine-version.json"
    if (-not (Test-Path $engineManifestPath)) {
        throw "engine-version.json not found at the root of $EngineZip — bad bundle"
    }
    $engineVersion = (Get-Content $engineManifestPath -Raw | ConvertFrom-Json).engine_version
    if (-not $engineVersion) { throw "engine-version.json has no engine_version field" }
    Write-Host "==> Engine bundle version $engineVersion" -ForegroundColor Cyan
    $engineIscArgs = @("/DEngineDir=$engineDir", "/DEngineVersion=$engineVersion")
}

# Optional: stage bina-defaults.json (zero-config release) next to the addin
# DLLs. BinaConfig reads it from the executing assembly's own directory, which
# is now a per-target SUBFOLDER — so it goes into every payload subfolder and
# rides the PluginDir recursesubdirs copy (no separate .iss entry needed).
# Without -GatewayUrl nothing is written: byte-identical to an addin-only build.
if ($GatewayUrl) {
    Write-Host "==> Writing bina-defaults.json (GatewayUrl=$GatewayUrl) into each payload..." -ForegroundColor Cyan
    foreach ($sub in $pluginTargets.Values) {
        @{ GatewayUrl = $GatewayUrl } | ConvertTo-Json |
            Set-Content (Join-Path (Join-Path $pluginDir $sub) "bina-defaults.json")
    }
}

# Optional: code signing, native Inno mechanism (signs the setup EXE AND the
# embedded uninstaller stub — post-compile signtool can only reach the EXE).
# Precedence: SIGNTOOL_ARGS env (password never touches the command line) >
# -SignCert (thumbprint if it doesn't resolve to a file, else treated as a
# PFX path, paired with -SignPassword). Omit all three -> no /S/ /D flags ->
# byte-identical unsigned ISCC invocation.
$signIscArgs = @()
if ($env:SIGNTOOL_ARGS -or $SignCert) {
    $signCertObj = $null   # public cert for the TrustedPublisher pre-trust export below
    if ($env:SIGNTOOL_ARGS) {
        $signBody = $env:SIGNTOOL_ARGS
        Write-Host "==> Signing via SIGNTOOL_ARGS env (cert material not logged; no pre-trust .cer export)" -ForegroundColor Cyan
    } elseif (Test-Path $SignCert) {
        if (-not $SignPassword) { throw "-SignCert <pfx path> requires -SignPassword" }
        $signBody = "/f `"$SignCert`" /p `"$SignPassword`" /fd SHA256 /tr $TimestampUrl /td SHA256"
        $signCertObj = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($SignCert, $SignPassword)
        Write-Host "==> Signing via PFX $SignCert" -ForegroundColor Cyan
    } else {
        $signBody = "/sha1 $SignCert /fd SHA256 /tr $TimestampUrl /td SHA256"
        $signCertObj = Get-Item "Cert:\CurrentUser\My\$SignCert"
        Write-Host "==> Signing via cert store thumbprint $SignCert" -ForegroundColor Cyan
    }
    # Revit validates Authenticode on the addin DLLs themselves — an unsigned
    # BinaLoader.dll shows "Unknown Publisher" in Revit's security dialog even
    # when the installer EXE is signed. Sign them here, before ISCC packs them.
    # Via cmd so $signBody stays one opaque string in all three cert forms
    # (SIGNTOOL_ARGS may contain quoted paths that PowerShell's & would resplit).
    $addinDlls = @(
        (Join-Path $loaderNet8Dir "BinaLoader.dll"),
        (Join-Path $loaderNet48Dir "BinaLoader.dll")
    ) + ($pluginTargets.Values | ForEach-Object { Join-Path (Join-Path $pluginDir $_) "RevitWebAppSync.dll" })
    foreach ($dll in $addinDlls) {
        Write-Host "==> Signing $($dll.Substring($repo.Length + 1))..." -ForegroundColor Cyan
        cmd /c "signtool.exe sign $signBody `"$dll`""
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $dll" }
    }
    # Export the PUBLIC cert so the installer can pre-trust the publisher
    # (certutil -addstore TrustedPublisher in the .iss [Run] section) — without
    # it Revit still shows the one-time "Signed Add-In — Always Load?" prompt.
    # Unsigned builds never write the .cer, so the .iss entries skip and the
    # output stays byte-identical.
    if ($signCertObj) {
        $cerPath = Join-Path $repo "artifacts\bina-cloudtech.cer"
        Export-Certificate -Cert $signCertObj -FilePath $cerPath | Out-Null
        Write-Host "==> Exported publisher cert to artifacts\bina-cloudtech.cer" -ForegroundColor Cyan
    }
    # Inno's /S<name>=<command> registers a sign tool ISCC shells out to per
    # artifact; $f is INNO's own placeholder for the file being signed (not a
    # PowerShell variable) so it must stay unexpanded — built via string
    # concatenation, not interpolation, to keep it literal.
    # Inno Setup 6.7.3+ quotes the $f substitution itself; wrapping it in $q
    # (Inno's quote placeholder, needed on older ISCC) now yields ""path""
    # and signtool fails to find the file. Pass bare $f.
    $signCommand = 'signtool.exe sign ' + $signBody + ' $f'
    $signIscArgs = @("/Sbinasign=$signCommand", "/DSignToolName=binasign")
} elseif ($SignPassword) {
    Write-Warning "-SignPassword given without -SignCert — signing skipped"
}

if ($Sign -and $signIscArgs.Count -gt 0) {
    Write-Warning "-Sign/-Thumbprint (legacy, EXE-only) ignored — SIGNTOOL_ARGS/-SignCert already sign the installer + uninstaller via Inno's native SignTool."
    $Sign = $false
}

# Inno Setup compiler — machine-wide (elevated install) or per-user (winget
# without admin lands in %LOCALAPPDATA%\Programs).
$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 not found (looked in: $($isccCandidates -join '; ')). Install: winget install JRSoftware.InnoSetup"
}

# Addins\2024 registration is gated on an actual 2024 payload — a net48 loader
# with nothing to load would dead-end Revit 2024 users on a reinstall dialog.
$net48IscArgs = @()
if ($yearMap.Contains("2024")) {
    $net48IscArgs = @("/DLoaderNet48Dir=$loaderNet48Dir")
}

Write-Host "==> Building installer EXE..." -ForegroundColor Cyan
$iscArgs = @($iss, "/DAppVersion=$Version", "/DLoaderNet8Dir=$loaderNet8Dir", "/DPluginDir=$pluginDir") +
    $net48IscArgs + $engineIscArgs + $signIscArgs + @("/O$repo")
& $iscc @iscArgs
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

if ($Sign) {
    if (-not $Thumbprint) { throw "-Sign requires -Thumbprint <cert-thumbprint>" }
    Write-Host "==> Signing EXE (legacy, exe-only)..." -ForegroundColor Cyan
    signtool sign /sha1 $Thumbprint /tr $TimestampUrl /td sha256 /fd sha256 $exe
}

Write-Host "==> Done: $exe" -ForegroundColor Green
Write-Host "Install (per-user, no admin):  double-click $(Split-Path -Leaf $exe)" -ForegroundColor Green
Write-Host "Silent (IT push):              $(Split-Path -Leaf $exe) /VERYSILENT" -ForegroundColor Green
