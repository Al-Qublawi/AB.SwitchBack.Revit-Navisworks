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
    [switch]$Clean,

    # Build against the SDKs installed on this machine rather than the NuGet reference
    # assemblies. Not the default: an Autodesk update can change a local SDK in place, so
    # the result depends on when the build ran.
    [switch]$UseInstalledSdk
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$repoRoot  = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $repoRoot 'artifacts'

if ($Clean -and (Test-Path $artifacts)) {
    Write-Host "Cleaning $artifacts"
    Remove-Item $artifacts -Recurse -Force
}

Write-Section 'Build matrix'

# Every declared release is built, whether or not it is installed here: the projects
# reference NuGet API packages by default, so the output does not depend on which Autodesk
# products happen to be on the build machine, or on updates Autodesk pushes to them.
$revitTargets = if ($RevitYears) { @($RevitYears) } else { @($script:RevitYears) }
$navisTargets = if ($NavisYears) { @($NavisYears) } else { @($script:NavisYears) }

Write-Host ("  Revit:       " + ($revitTargets -join ', '))
Write-Host ("  Navisworks:  " + ($navisTargets -join ', '))
Write-Host ''
Write-Host '  API source: NuGet reference assemblies (no Autodesk install required).' -ForegroundColor DarkGray
Write-Host '  Pass -UseInstalledSdk to build against the SDKs on this machine instead.' -ForegroundColor DarkGray

if ($UseInstalledSdk) {
    Write-Host ''
    Write-Host '  -UseInstalledSdk: building against locally installed SDKs where found.' -ForegroundColor Yellow
    Write-Host '  Note that an Autodesk update can change a local SDK''s runtime in place,' -ForegroundColor Yellow
    Write-Host '  producing an add-in that will not load for anyone without that update.' -ForegroundColor Yellow
}

$results = @()

Write-Section 'Building Revit add-ins'

foreach ($year in $revitTargets) {
    Write-Host "  Revit $year ... " -NoNewline

    $project = Join-Path $repoRoot 'src\ABSwitchBack.Revit\ABSwitchBack.Revit.csproj'
    $arguments = @($project, '-c', $Configuration, '-v', 'quiet', '--nologo', "-p:RevitVersion=$year")

    if ($UseInstalledSdk) {
        $local = Get-RevitInstallPath -Year $year
        if ($local) { $arguments += "-p:RevitApiDir=$local" }
    }

    $output = & dotnet build @arguments 2>&1

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

# One build per release: VariantData gained its 64-bit accessors in 2026, so 2024/2025 and
# 2026/2027 are not interchangeable. See NavisCompat.
foreach ($year in $navisTargets) {
    Write-Host "  Navisworks $year ... " -NoNewline

    $project = Join-Path $repoRoot 'src\ABSwitchBack.Navisworks\ABSwitchBack.Navisworks.csproj'
    $arguments = @($project, '-c', $Configuration, '-v', 'quiet', '--nologo', "-p:NavisVersion=$year")

    if ($UseInstalledSdk) {
        $local = Get-NavisworksInstallPath -Year $year -Product 'Manage'
        if (-not $local) { $local = Get-NavisworksInstallPath -Year $year -Product 'Simulate' }
        if ($local) { $arguments += "-p:NavisApiDir=$local" }
    }

    $output = & dotnet build @arguments 2>&1

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
