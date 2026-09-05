<#
.SYNOPSIS
    Builds AB SwitchBack for every Revit and Navisworks release installed on this machine.

.DESCRIPTION
    Projects exist for Revit 2020-2027 and Navisworks 2024-2027. This script detects which
    of those are actually installed and compiles only those, so installing a new Autodesk
    release later needs no code or project change - just re-run this script.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\build.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\build.ps1 -RevitYears 2024,2026
#>
[CmdletBinding()]
param(
    [int[]]$RevitYears,
    [int[]]$NavisYears,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$repoRoot  = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $repoRoot 'artifacts'

if ($Clean -and (Test-Path $artifacts)) {
    Write-Host "Cleaning $artifacts"
    Remove-Item $artifacts -Recurse -Force
}

Write-Section 'Detecting installed Autodesk products'

$revitInstalls = @(Get-RevitInstalls)
$navisInstalls = @(Get-NavisworksInstalls)

if ($RevitYears) { $revitInstalls = @($revitInstalls | Where-Object { $RevitYears -contains $_.Year }) }
if ($NavisYears) { $navisInstalls = @($navisInstalls | Where-Object { $NavisYears -contains $_.Year }) }

if ($revitInstalls.Count -eq 0) { Write-Host '  Revit:       none found' -ForegroundColor Yellow }
foreach ($install in $revitInstalls) {
    Write-Host ("  Revit {0}  [{1}]  {2}" -f $install.Year, $install.Runtime, $install.Path)
}

if ($navisInstalls.Count -eq 0) { Write-Host '  Navisworks:  none found' -ForegroundColor Yellow }
foreach ($install in $navisInstalls) {
    Write-Host ("  Navisworks {0} {1}  [{2}]  {3}" -f $install.Product, $install.Year, $install.Runtime, $install.Path)
}

if ($revitInstalls.Count -eq 0 -and $navisInstalls.Count -eq 0) {
    throw 'No Revit or Navisworks installation was found. Nothing to build.'
}

$results = @()

Write-Section 'Building Revit add-ins'

foreach ($install in $revitInstalls) {
    $year = $install.Year
    Write-Host "  Revit $year ... " -NoNewline

    $project = Join-Path $repoRoot 'src\ABSwitchBack.Revit\ABSwitchBack.Revit.csproj'
    $output  = & dotnet build $project -c $Configuration -v quiet --nologo `
        "-p:RevitVersion=$year" "-p:RevitApiDir=$($install.Path)" 2>&1

    if ($LASTEXITCODE -eq 0) {
        Write-Host 'OK' -ForegroundColor Green

        # Ship the manifest next to the binaries so install.ps1 only has to copy.
        $addinDir = Join-Path $artifacts "Revit\$year"
        New-RevitAddInManifest -Destination (Join-Path $addinDir 'ABSwitchBack.addin')

        $results += [pscustomobject]@{ Product = "Revit $year"; Status = 'built'; Path = $addinDir }
    }
    else {
        Write-Host 'FAILED' -ForegroundColor Red
        $output | Where-Object { $_ -match 'error' } | Select-Object -First 8 | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
        $results += [pscustomobject]@{ Product = "Revit $year"; Status = 'FAILED'; Path = '' }
    }
}

Write-Section 'Building Navisworks plugins'

# One binary serves every Navisworks release of the same API major version, but each is
# built separately so the reference always matches the installed assembly exactly.
foreach ($install in ($navisInstalls | Group-Object Year | ForEach-Object { $_.Group[0] })) {
    $year = $install.Year
    Write-Host "  Navisworks $year ... " -NoNewline

    $project = Join-Path $repoRoot 'src\ABSwitchBack.Navisworks\ABSwitchBack.Navisworks.csproj'
    $output  = & dotnet build $project -c $Configuration -v quiet --nologo `
        "-p:NavisVersion=$year" "-p:NavisApiDir=$($install.Path)" 2>&1

    if ($LASTEXITCODE -eq 0) {
        Write-Host 'OK' -ForegroundColor Green
        $results += [pscustomobject]@{
            Product = "Navisworks $year"; Status = 'built'
            Path    = (Join-Path $artifacts "Navisworks\$year\ABSwitchBack")
        }
    }
    else {
        Write-Host 'FAILED' -ForegroundColor Red
        $output | Where-Object { $_ -match 'error' } | Select-Object -First 8 | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
        $results += [pscustomobject]@{ Product = "Navisworks $year"; Status = 'FAILED'; Path = '' }
    }
}

Write-Section 'Summary'
$results | Format-Table -AutoSize

$failed = @($results | Where-Object { $_.Status -eq 'FAILED' })
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) target(s) failed to build." -ForegroundColor Red
    exit 1
}

Write-Host "All targets built into $artifacts" -ForegroundColor Green
Write-Host 'Next: powershell -ExecutionPolicy Bypass -File build\install.ps1' -ForegroundColor Gray
