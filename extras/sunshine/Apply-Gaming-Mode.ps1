<#
.SYNOPSIS
  Applies the gaming Sunshine profile (NVENC, 1080p60 HEVC, HDR available) and restarts Sunshine.

.DESCRIPTION
  * Needs administrator rights (Sunshine's config is in Program Files) - it asks for them itself.
  * Backs up the current sunshine.conf (sunshine.conf.bak-<timestamp>) before replacing it.
  * Detects the virtual display ("VDD by MTT") Sunshine id from sunshine.log; if the virtual
    display driver is installed but disabled, enables it first (a device rescan + Enable-PnpDevice;
    the plain "pnputil /enable-device" fails on some machines).
  * Refuses to restart Sunshine while a client is connected (a restart ends the stream and kills
    the streamed app) unless -Force is given.

.PARAMETER SunshineDir
  Default: C:\Program Files\Sunshine

.PARAMETER Force
  Restart Sunshine even if a client appears to be connected.
#>
[CmdletBinding()]
param(
    [string]$SunshineDir = 'C:\Program Files\Sunshine',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'

# --- self-elevate ----------------------------------------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$($MyInvocation.MyCommand.Path)`"",'-SunshineDir',"`"$SunshineDir`"")
    if ($Force) { $argList += '-Force' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$cfgDir = Join-Path $SunshineDir 'config'
$conf = Join-Path $cfgDir 'sunshine.conf'
$log = Join-Path $cfgDir 'sunshine.log'
if (-not (Test-Path $cfgDir)) { Write-Host "Sunshine config folder not found: $cfgDir" -ForegroundColor Red; Read-Host 'Press Enter'; exit 1 }

# --- never kill a live stream ----------------------------------------------------------------------
if ((Test-Path $log) -and -not $Force) {
    $events = Select-String -Path $log -Pattern 'CLIENT CONNECTED|CLIENT DISCONNECTED|Sunshine version' | Select-Object -Last 1
    if ($events -and $events.Line -match 'CLIENT CONNECTED') {
        Write-Host "A Moonlight client appears to be connected. Disconnect first, or re-run with -Force." -ForegroundColor Yellow
        Read-Host 'Press Enter'; exit 1
    }
}

# --- virtual display: make sure it is enabled -----------------------------------------------------------
$vdd = Get-CimInstance Win32_PnPEntity -Filter 'DeviceID LIKE "ROOT\\DISPLAY\\%"' | Where-Object Name -like '*Virtual Display*' | Select-Object -First 1
if ($vdd -and $vdd.ConfigManagerErrorCode -ne 0) {
    Write-Host "Enabling the virtual display..."
    pnputil /scan-devices | Out-Null
    Enable-PnpDevice -InstanceId $vdd.DeviceID -Confirm:$false
    Start-Sleep -Seconds 5
} elseif (-not $vdd) {
    Write-Host "Virtual Display Driver not found - the profile will use Sunshine's default display." -ForegroundColor Yellow
}

# --- find Sunshine's own id for the virtual display (needs one Sunshine start to have logged it) --------
function Get-VddOutputName([string]$logPath) {
    if (-not (Test-Path $logPath)) { return $null }
    $lines = Get-Content $logPath
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        if ($lines[$i] -match '"friendly_name":\s*"VDD by MTT"') {
            for ($j = $i; $j -ge [Math]::Max(0, $i - 12); $j--) {
                if ($lines[$j] -match '"device_id":\s*"(\{[0-9a-fA-F-]+\})"') { return $Matches[1] }
            }
        }
    }
    return $null
}
$outputName = Get-VddOutputName $log
if (-not $outputName -and $vdd) {
    # Sunshine only lists displays at startup: restart once so the (re-)enabled display is logged.
    Write-Host "Restarting Sunshine once so it detects the virtual display..."
    Restart-Service SunshineService -Force; Start-Sleep -Seconds 40
    $outputName = Get-VddOutputName $log
}
$outputLine = if ($outputName) { "output_name = $outputName" } else { "# output_name not detected - Sunshine will use the primary display" }
Write-Host ("Virtual display id: " + $(if ($outputName) { $outputName } else { '(not detected)' }))

# --- apply ------------------------------------------------------------------------------------------
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if (Test-Path $conf) { Copy-Item $conf "$conf.bak-$stamp"; Write-Host "Backed up sunshine.conf -> sunshine.conf.bak-$stamp" }
$text = (Get-Content (Join-Path $here 'gaming.conf.template') -Raw).Replace('{{OUTPUT_NAME_LINE}}', $outputLine)
[System.IO.File]::WriteAllText($conf, $text, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Restarting Sunshine (about a minute)..."
Restart-Service SunshineService -Force
$deadline = (Get-Date).AddSeconds(90)
do { Start-Sleep -Seconds 2 } while (-not (Test-NetConnection localhost -Port 47990 -WarningAction SilentlyContinue -InformationLevel Quiet) -and (Get-Date) -lt $deadline)
Select-String -Path $log -Pattern 'Found (H264|HEVC|AV1) encoder|Couldn.t find any working' | Select-Object -Last 3 | ForEach-Object { $_.Line.Trim() }
Write-Host "Done. In Moonlight: HEVC, 1080p/60, 'Optimize game settings' ON, HDR if wanted." -ForegroundColor Green
Start-Sleep -Seconds 5
