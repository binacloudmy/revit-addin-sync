<#
.SYNOPSIS
  Bring the BINA Copilot Engine back up at Windows logon, with zero user
  interaction. Also self-registers/unregisters its own Scheduled Task.

.DESCRIPTION
  Registered by the installer (RevitCopilot.iss) as an ONLOGON Scheduled Task.
  Runs hidden: no console, no focus steal. Without it the engine stays down
  after a reboot until a human opens Revit.

  This script deliberately DERIVES NOTHING. Port, gateway URL, engine bundle and
  environment are all values the add-in computes with rules that live in C#
  (UrlResolution.ResolveGateway rewrites a persisted cloud host to the build's
  embedded default; NewestEngineLauncher picks the bundle; TooOldForEngine can
  refuse it; ProviderKeyEnvs is the poison-pill strip list). Re-implementing any
  of those here would silently drift the moment the C# side changes. Instead
  EngineManager records exactly what it used in

      %LocalAppData%\Bina\RevitSync\engine\engine-boot.json

  and this script replays it. See Services\EngineBootManifest.cs.

  Credentials are NOT in that manifest: it names the config.json field each
  secret env var comes from, and we read the live value here - so a rotated
  device token is picked up at the next logon instead of being pinned.

.PARAMETER Register
  Create/replace the ONLOGON task for the current user, then exit. Idempotent.

.PARAMETER Unregister
  Remove the task, then exit. Never fails when the task is already gone.
#>
[CmdletBinding()]
param(
    [switch]$Register,
    [switch]$Unregister
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$TaskName    = 'BINA Copilot Engine'
$EngineRoot  = Join-Path $env:LOCALAPPDATA 'Bina\RevitSync\engine'
$ConfigPath  = Join-Path $env:APPDATA     'RevitWebAppSync\config.json'
$ManifestPath = Join-Path $EngineRoot 'engine-boot.json'
$PidPath     = Join-Path $EngineRoot 'engine.pid'
$LogPath     = Join-Path $EngineRoot 'logs\engine-boot.log'
$SchemaVersion = 1

# ---------------------------------------------------------------- logging ----
# A boot failure with no console and no log is undiagnosable, which is how the
# engine "just doesn't come up" tickets start. Cheap rolling log, capped.
function Write-BootLog([string]$Message) {
    try {
        $dir = Split-Path -Parent $LogPath
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        if ((Test-Path $LogPath) -and (Get-Item $LogPath).Length -gt 256KB) {
            Move-Item $LogPath "$LogPath.1" -Force
        }
        $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        Add-Content -Path $LogPath -Value "$stamp  $Message" -Encoding UTF8
    } catch { }   # logging must never be the thing that breaks boot
}

# ------------------------------------------------------------- task setup ----
# Register-ScheduledTask over `schtasks /Create /TR "..."`: the CLI form needs
# the inner -File path escaped as \" INSIDE an already-quoted /TR value, and
# gets it wrong for any user whose profile path contains a space. The COM/CIM
# layer takes the argument string as data, so there is nothing to escape.
function Register-BootTask {
    $self = $PSCommandPath
    if (-not $self) { throw 'cannot resolve own path for task registration' }

    $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $self)

    # AtLogOn scoped to THIS user: the engine runs as the signed-in drafter and
    # writes its session DB under that profile, so a machine-wide trigger would
    # start it in the wrong session.
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
    # Ride out the logon storm (AV, Autodesk services, network) before spending
    # a cold engine start on a box that is still thrashing.
    $trigger.Delay = 'PT30S'

    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit ([TimeSpan]::Zero)

    # LogonType Interactive explicitly: the alternative (Password) would need a
    # stored credential, which a per-user, non-elevated install cannot supply and
    # must never ask a drafter for.
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
        -LogonType Interactive -RunLevel Limited

    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
        -Settings $settings -Principal $principal -Force | Out-Null
    Write-BootLog "registered scheduled task '$TaskName' -> $self"
}

function Unregister-BootTask {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-BootLog "unregistered scheduled task '$TaskName'"
}

if ($Register) {
    try { Register-BootTask }
    catch {
        # Never fail the install over the task. The add-in still spawns the
        # engine when Revit opens; only reboot-survival is lost.
        Write-BootLog "task registration FAILED: $($_.Exception.Message)"
    }
    exit 0
}

if ($Unregister) {
    try { Unregister-BootTask } catch { Write-BootLog "task removal failed: $($_.Exception.Message)" }
    exit 0
}

# ------------------------------------------------------------------ boot ----
function Get-JsonFile([string]$Path) {
    return (Get-Content -Raw -LiteralPath $Path -Encoding UTF8 | ConvertFrom-Json)
}

function Test-Prop($Object, [string]$Name) {
    return ($null -ne $Object) -and ($null -ne $Object.PSObject.Properties[$Name])
}

function Get-Prop($Object, [string]$Name) {
    if (Test-Prop $Object $Name) { return $Object.PSObject.Properties[$Name].Value }
    return $null
}

try {
    if (-not (Test-Path -LiteralPath $ConfigPath)) { exit 0 }        # not installed/configured
    $cfg = Get-JsonFile $ConfigPath

    # The add-in only auto-spawns when BOTH flags are on (App.cs) and refuses to
    # start the tool server at all without a secret. Honour the same gates  - 
    # a cloud-mode box that deliberately turned the engine off must not get one
    # started behind its back at every logon.
    if (-not (Get-Prop $cfg 'EngineMode') -or -not (Get-Prop $cfg 'EngineAutoSpawn')) { exit 0 }

    if (-not (Test-Path -LiteralPath $ManifestPath)) { exit 0 }      # add-in has never spawned yet
    $m = Get-JsonFile $ManifestPath

    if ((Get-Prop $m 'schema') -ne $SchemaVersion) {
        Write-BootLog "manifest schema $(Get-Prop $m 'schema') != $SchemaVersion - refusing to guess; skipping"
        exit 0
    }

    $port     = [int](Get-Prop $m 'port')
    $launcher = [string](Get-Prop $m 'launcher')
    $workDir  = [string](Get-Prop $m 'working_dir')
    if ($port -le 0 -or -not $launcher) { Write-BootLog 'manifest missing port/launcher; skipping'; exit 0 }

    # Already serving? Attach, don't spawn. Same shape check the add-in does
    # (app/engine/main.py returns {"status":"ok","engine":true}) so a foreign
    # process squatting the port is never mistaken for our engine.
    try {
        $h = Invoke-RestMethod -Uri "http://127.0.0.1:$port/health" -TimeoutSec 2
        # Get-Prop, not $h.engine: Set-StrictMode makes a missing property throw,
        # and "some other process answered 200" must fall through to a spawn
        # attempt, not look like an error.
        if ((Get-Prop $h 'engine') -eq $true) { exit 0 }
    } catch { }

    if (-not (Test-Path -LiteralPath $launcher)) {
        Write-BootLog "launcher missing: $launcher (engine bundle removed?); skipping"
        exit 0
    }

    # ---- environment: manifest verbatim, secrets read live from config.json ----
    $envMap = @{}
    $envNode = Get-Prop $m 'env'
    if ($envNode) {
        foreach ($p in $envNode.PSObject.Properties) { $envMap[$p.Name] = [string]$p.Value }
    }

    $secretNode = Get-Prop $m 'secret_env'
    if ($secretNode) {
        foreach ($p in $secretNode.PSObject.Properties) {
            $value = [string](Get-Prop $cfg $p.Value)
            if (-not $value) {
                # A blank secret means the user has logged out / the token was
                # cleared. The engine would refuse to start anyway (config.py);
                # exiting quietly is the honest outcome.
                Write-BootLog "config.json has no $($p.Value) for $($p.Name) - not started"
                exit 0
            }
            $envMap[$p.Name] = $value
        }
    }

    # A .cmd is not a PE and cannot be CreateProcess'd under UseShellExecute=0  - 
    # same cmd.exe /c wrapper the add-in uses. Legacy bina-engine.exe is a real
    # PE and launches directly.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    if ([System.IO.Path]::GetExtension($launcher) -ieq '.cmd') {
        $psi.FileName  = 'cmd.exe'
        $psi.Arguments = "/c `"$launcher`""
    } else {
        $psi.FileName = $launcher
    }
    $psi.UseShellExecute  = $false
    $psi.CreateNoWindow   = $true
    if ($workDir -and (Test-Path -LiteralPath $workDir)) { $psi.WorkingDirectory = $workDir }

    foreach ($k in $envMap.Keys) { $psi.EnvironmentVariables[$k] = $envMap[$k] }

    # Poison-pill parity: whatever the add-in stripped, we strip. A provider key
    # left in a machine-scope variable otherwise reaches the engine and bricks
    # the start on a gateway-configured box (bina-ai app/engine/config.py).
    $strip = Get-Prop $m 'strip_env'
    if ($strip) { foreach ($k in $strip) { [void]$psi.EnvironmentVariables.Remove([string]$k) } }

    $proc = [System.Diagnostics.Process]::Start($psi)
    if (-not $proc) { Write-BootLog 'Process.Start returned null'; exit 0 }

    # Pidfile so a LATER add-in session can reap this process if it hangs without
    # answering /health. Safe now that EngineManager health-checks before it
    # touches the pidfile - with the old order this line got the boot engine
    # tree-killed on every Revit open.
    Set-Content -LiteralPath $PidPath -Value $proc.Id -Encoding Ascii
    Write-BootLog "started pid=$($proc.Id) port=$port launcher=$launcher"
    $proc.Close()
}
catch {
    Write-BootLog "boot FAILED: $($_.Exception.Message)"
}

exit 0
