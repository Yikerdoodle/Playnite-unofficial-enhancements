<#
.SYNOPSIS
  Turns on taskbar auto-hide for the main display AND every per-display entry Windows has saved.

.DESCRIPTION
  Windows stores taskbar auto-hide separately per monitor. When a streaming virtual display becomes
  the primary screen it uses its own saved entry, which may have auto-hide off, so the taskbar
  appears in the stream. This sets the auto-hide bit on all saved entries. Your current settings are
  exported to .reg backup files next to this script first. No Explorer restart is done; Windows reads
  the entries when a display attaches. A newly created display entry can come back with auto-hide
  off; run this again if it does.
#>
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$reg = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer'
$ps  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer'

reg export "$reg\StuckRects3"   (Join-Path $here "taskbar-main-before-$stamp.reg") /y | Out-Null
reg export "$reg\MMStuckRects3" (Join-Path $here "taskbar-displays-before-$stamp.reg") /y 2>$null | Out-Null

# main taskbar: byte 8 of the Settings blob, bit 0x01 = auto-hide
$k = "$ps\StuckRects3"
$v = [byte[]](Get-ItemProperty $k).Settings
if (-not ($v[8] -band 1)) { $v[8] = $v[8] -bor 1; Set-ItemProperty $k -Name Settings -Value $v -Type Binary }

# every other display
$k = "$ps\MMStuckRects3"
if (Test-Path $k) {
    foreach ($name in (Get-Item $k).GetValueNames()) {
        $b = [byte[]](Get-ItemProperty $k).$name
        if ($b.Length -gt 8 -and -not ($b[8] -band 1)) { $b[8] = $b[8] -bor 1; Set-ItemProperty $k -Name $name -Value $b -Type Binary }
    }
}
Write-Host "Taskbar auto-hide is now on for all saved displays. Backups: taskbar-*-before-$stamp.reg"
