<#
.SYNOPSIS
    Removes AB SwitchBack from every Revit and Navisworks installation.

.DESCRIPTION
    Deletes the Revit .addin manifests and payload folders from %APPDATA%, and the plugin
    folders from each Navisworks install (that part needs an elevated shell).
    Logs and settings under %LOCALAPPDATA%\ABSwitchBack are kept unless -PurgeSettings.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\uninstall.ps1
#>
[CmdletBinding()]
param(
    [switch]$RevitOnly,
    [switch]$NavisworksOnly,
    [switch]$PurgeSettings,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

# Only the hosts actually being touched can block the removal.
$blockingProcesses = @()
if (-not $NavisworksOnly) { $blockingProcesses += 'Revit' }
if (-not $RevitOnly)      { $blockingProcesses += 'Roamer' }   # Roamer.exe is Navisworks

$running = @(Get-Process -Name $blockingProcesses -ErrorAction SilentlyContinue)
if ($running.Count -gt 0 -and -not $Force) {
    $running | ForEach-Object { Write-Host ("  {0} (PID {1}) is running" -f $_.ProcessName, $_.Id) -ForegroundColor Yellow }
    Write-Host ''
    Write-Host 'Close them, or remove one side only with -RevitOnly / -NavisworksOnly.' -ForegroundColor Gray
    throw 'Close the listed applications first, or re-run with -Force.'
}

$removed = @()

if (-not $NavisworksOnly) {

Write-Section 'Removing Revit add-ins'

$addinsRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'
if (Test-Path $addinsRoot) {
    foreach ($yearFolder in Get-ChildItem $addinsRoot -Directory -ErrorAction SilentlyContinue) {
        $manifest = Join-Path $yearFolder.FullName 'ABSwitchBack.addin'
        $payload  = Join-Path $yearFolder.FullName 'ABSwitchBack'

        foreach ($target in @($manifest, $payload)) {
            if (-not (Test-Path $target)) { continue }
            try {
                Remove-Item $target -Recurse -Force
                Write-Host ("  removed {0}" -f $target) -ForegroundColor Green
                $removed += $target
            }
            catch {
                Write-Host ("  FAILED {0} - {1}" -f $target, $_.Exception.Message) -ForegroundColor Red
            }
        }
    }
}

} # end Revit scope

if (-not $RevitOnly) {

Write-Section 'Removing Navisworks plugins'

foreach ($install in Get-NavisworksInstalls) {
    $target = Join-Path $install.Path 'Plugins\ABSwitchBack'
    if (-not (Test-Path $target)) { continue }

    try {
        Remove-Item $target -Recurse -Force
        Write-Host ("  removed {0}" -f $target) -ForegroundColor Green
        $removed += $target
    }
    catch [System.UnauthorizedAccessException] {
        Write-Host ("  ACCESS DENIED {0} - re-run elevated" -f $target) -ForegroundColor Red
    }
    catch {
        Write-Host ("  FAILED {0} - {1}" -f $target, $_.Exception.Message) -ForegroundColor Red
    }
}

} # end Navisworks scope

if ($PurgeSettings) {
    Write-Section 'Removing settings and logs'
    $settings = Join-Path $env:LOCALAPPDATA 'ABSwitchBack'
    if (Test-Path $settings) {
        Remove-Item $settings -Recurse -Force
        Write-Host ("  removed {0}" -f $settings) -ForegroundColor Green
    }
}

Write-Section 'Summary'
if ($removed.Count -eq 0) {
    Write-Host 'Nothing was installed.' -ForegroundColor Yellow
}
else {
    Write-Host ("Removed {0} item(s)." -f $removed.Count) -ForegroundColor Green
}
