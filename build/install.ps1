<#
.SYNOPSIS
    Deploys AB SwitchBack to every installed Revit and Navisworks release.

.DESCRIPTION
    Revit      -> %APPDATA%\Autodesk\Revit\Addins\<year>\   (per user, no admin needed)
    Navisworks -> <install>\Plugins\ABSwitchBack\           (Navisworks has no per-user
                                                             plugin folder, so this part
                                                             requires an elevated shell)

    Run build\build.ps1 first. Close Revit and Navisworks before installing, otherwise the
    files are locked and the copy will fail.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\install.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\install.ps1 -RevitOnly
#>
[CmdletBinding()]
param(
    [switch]$RevitOnly,
    [switch]$NavisworksOnly,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$repoRoot  = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $repoRoot 'artifacts'

if (-not (Test-Path $artifacts)) {
    throw "No artifacts folder. Run build\build.ps1 first."
}

# A running host holds its add-in DLLs open, which produces a confusing mid-copy failure.
# Only the hosts actually being written to matter: a running Navisworks must not block a
# Revit-only install, and vice versa.
$blockingProcesses = @()
if (-not $NavisworksOnly) { $blockingProcesses += 'Revit' }
if (-not $RevitOnly)      { $blockingProcesses += 'Roamer' }   # Roamer.exe is Navisworks

$running = @(Get-Process -Name $blockingProcesses -ErrorAction SilentlyContinue)
if ($running.Count -gt 0 -and -not $Force) {
    Write-Host 'These applications are still running and will lock the files:' -ForegroundColor Yellow
    $running | ForEach-Object { Write-Host ("  {0} (PID {1})" -f $_.ProcessName, $_.Id) -ForegroundColor Yellow }
    Write-Host ''
    Write-Host 'Close them, or install one side only:' -ForegroundColor Gray
    Write-Host '  install.ps1 -RevitOnly        (Revit is closed, Navisworks is open)' -ForegroundColor Gray
    Write-Host '  install.ps1 -NavisworksOnly   (Navisworks is closed, Revit is open)' -ForegroundColor Gray
    throw 'Close the listed applications first, or re-run with -Force to try anyway.'
}

$installed = @()
$skipped   = @()

function Copy-Payload {
    param([string]$Source, [string]$Destination)

    if (-not (Test-Path $Destination)) { New-Item -ItemType Directory -Path $Destination -Force | Out-Null }

    # Runtime files only - no .pdb noise, and the .addin manifest goes one level up.
    # .deps.json matters for Revit 2025+ (.NET 8), where the add-in load context can use it.
    Get-ChildItem $Source -File |
        Where-Object { $_.Extension -in @('.dll', '.config') -or $_.Name -like '*.deps.json' } |
        ForEach-Object { Copy-Item $_.FullName -Destination (Join-Path $Destination $_.Name) -Force }

    # Navisworks reads its ribbon layout from a locale subfolder (en-US) and its button
    # images from Images, both as loose files beside the DLL. Miss either and the tab
    # silently never appears.
    foreach ($subfolder in @('Images', 'en-US')) {
        $subSource = Join-Path $Source $subfolder
        if (-not (Test-Path $subSource)) { continue }

        $subDestination = Join-Path $Destination $subfolder
        if (-not (Test-Path $subDestination)) {
            New-Item -ItemType Directory -Path $subDestination -Force | Out-Null
        }
        Get-ChildItem $subSource -File |
            ForEach-Object { Copy-Item $_.FullName -Destination (Join-Path $subDestination $_.Name) -Force }
    }
}

# ---------------------------------------------------------------- Revit
if (-not $NavisworksOnly) {
    Write-Section 'Installing Revit add-ins (per user)'

    $addinsRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'

    foreach ($install in Get-RevitInstalls) {
        $year   = $install.Year
        $source = Join-Path $artifacts "Revit\$year"

        if (-not (Test-Path (Join-Path $source 'ABSwitchBack.Revit.dll'))) {
            Write-Host ("  Revit {0}: not built, skipping" -f $year) -ForegroundColor DarkYellow
            $skipped += "Revit $year (not built)"
            continue
        }

        $yearFolder = Join-Path $addinsRoot $year
        $payload    = Join-Path $yearFolder 'ABSwitchBack'

        try {
            Copy-Payload -Source $source -Destination $payload
            New-RevitAddInManifest -Destination (Join-Path $yearFolder 'ABSwitchBack.addin')

            Write-Host ("  Revit {0}: installed -> {1}" -f $year, $yearFolder) -ForegroundColor Green
            $installed += "Revit $year"
        }
        catch {
            Write-Host ("  Revit {0}: FAILED - {1}" -f $year, $_.Exception.Message) -ForegroundColor Red
            $skipped += "Revit $year (error)"
        }
    }
}

# ---------------------------------------------------------------- Navisworks
if (-not $RevitOnly) {
    Write-Section 'Installing Navisworks plugins (machine wide)'

    $elevated = Test-IsElevated
    if (-not $elevated) {
        Write-Host '  This shell is not elevated. Navisworks plugins live under Program Files' -ForegroundColor Yellow
        Write-Host '  and cannot be written without administrator rights.' -ForegroundColor Yellow
    }

    foreach ($install in Get-NavisworksInstalls) {
        $year   = $install.Year
        $source = Join-Path $artifacts "Navisworks\$year\ABSwitchBack"
        $label  = "Navisworks $($install.Product) $year"

        if (-not (Test-Path (Join-Path $source 'ABSwitchBack.dll'))) {
            Write-Host ("  {0}: not built, skipping" -f $label) -ForegroundColor DarkYellow
            $skipped += "$label (not built)"
            continue
        }

        # The DLL name must match its folder name or Navisworks will not load it.
        $destination = Join-Path $install.Path 'Plugins\ABSwitchBack'

        try {
            # Wipe first: the plugin used to ship a separate ABSwitchBack.Core.dll, and a
            # leftover copy beside the new self-contained assembly is at best confusing.
            if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }

            Copy-Payload -Source $source -Destination $destination
            Write-Host ("  {0}: installed -> {1}" -f $label, $destination) -ForegroundColor Green
            $installed += $label
        }
        catch [System.UnauthorizedAccessException] {
            Write-Host ("  {0}: ACCESS DENIED" -f $label) -ForegroundColor Red
            $skipped += "$label (needs admin)"
        }
        catch {
            Write-Host ("  {0}: FAILED - {1}" -f $label, $_.Exception.Message) -ForegroundColor Red
            $skipped += "$label (error)"
        }
    }
}

# ---------------------------------------------------------------- Summary
Write-Section 'Summary'

if ($installed.Count -gt 0) {
    Write-Host 'Installed:' -ForegroundColor Green
    $installed | ForEach-Object { Write-Host "  $_" }
}
if ($skipped.Count -gt 0) {
    Write-Host 'Skipped:' -ForegroundColor Yellow
    $skipped | ForEach-Object { Write-Host "  $_" }

    if ($skipped -match 'needs admin') {
        Write-Host ''
        Write-Host 'To finish the Navisworks side, run an elevated PowerShell and then:' -ForegroundColor Yellow
        Write-Host "  powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`" -NavisworksOnly" -ForegroundColor Gray
    }
}

Write-Host ''
Write-Host 'Config and logs will appear in:' -ForegroundColor Gray
Write-Host ("  {0}\ABSwitchBack" -f $env:LOCALAPPDATA) -ForegroundColor Gray
