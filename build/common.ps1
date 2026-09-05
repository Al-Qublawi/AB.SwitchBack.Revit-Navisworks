# Shared helpers: locate installed Autodesk products without assuming any fixed path.
#
# Detection order for both products:
#   1. The product's own registry key (authoritative)
#   2. The Windows uninstall registry (survives non-default install drives)
#   3. Probing the standard Program Files layout (last resort)
# Every candidate is then validated by checking that the API assembly really exists,
# which also filters out leftover registry keys from uninstalled or partial installs.

$ErrorActionPreference = 'Stop'

$script:RevitYears  = 2020..2027
$script:NavisYears  = 2024..2027
$script:NavisProducts = @('Manage', 'Simulate')

# Product identity used in the Revit .addin manifest. Must stay constant forever:
# Revit keys the add-in registration off it.
$script:AddInClientId = 'b6342c28-4b89-4417-bb34-17d8f02664e3'
$script:VendorId      = 'ABSB'
$script:ProductName   = 'AB SwitchBack'

# Author identity, mirroring src\ABSwitchBack.Core\Branding.cs.
$script:ProductAuthor = 'Abdullah Lotfy'
$script:LinkedInUrl   = 'https://www.linkedin.com/in/abdullahalqublawi/'

function Get-UninstallInstallLocation {
    param([Parameter(Mandatory)][string]$DisplayName)

    $roots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        foreach ($item in Get-ChildItem $root -ErrorAction SilentlyContinue) {
            $props = Get-ItemProperty $item.PSPath -ErrorAction SilentlyContinue
            if ($null -eq $props) { continue }
            if ($props.DisplayName -ne $DisplayName) { continue }
            if ([string]::IsNullOrWhiteSpace($props.InstallLocation)) { continue }
            return $props.InstallLocation.TrimEnd('\')
        }
    }
    return $null
}

function Test-ApiFolder {
    param([string]$Path, [string]$ApiFileName)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    return Test-Path (Join-Path $Path $ApiFileName)
}

function Get-RevitInstallPath {
    param([Parameter(Mandatory)][int]$Year)
    $api = 'RevitAPI.dll'

    # 1. HKLM\SOFTWARE\Autodesk\Revit\<year>\<product code>\InstallationLocation
    $key = "HKLM:\SOFTWARE\Autodesk\Revit\$Year"
    if (Test-Path $key) {
        foreach ($sub in Get-ChildItem $key -ErrorAction SilentlyContinue) {
            $location = (Get-ItemProperty $sub.PSPath -ErrorAction SilentlyContinue).InstallationLocation
            if ($location) {
                $location = $location.TrimEnd('\')
                if (Test-ApiFolder $location $api) { return $location }
            }
        }
    }

    # 2. Uninstall registry
    $fromUninstall = Get-UninstallInstallLocation -DisplayName "Autodesk Revit $Year"
    if (Test-ApiFolder $fromUninstall $api) { return $fromUninstall }

    # 3. Conventional layout
    foreach ($programFiles in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not $programFiles) { continue }
        $candidate = Join-Path $programFiles "Autodesk\Revit $Year"
        if (Test-ApiFolder $candidate $api) { return $candidate }
    }

    return $null
}

function Get-NavisworksInstallPath {
    param(
        [Parameter(Mandatory)][int]$Year,
        [Parameter(Mandatory)][string]$Product
    )
    $api = 'Autodesk.Navisworks.Api.dll'

    # Navisworks API major version 21 corresponds to the 2024 release.
    $major = $Year - 2003

    # 1. HKLM\SOFTWARE\Autodesk\Navisworks <Product>\<major>.0\Location  ->  Path
    $key = "HKLM:\SOFTWARE\Autodesk\Navisworks $Product\$major.0\Location"
    if (Test-Path $key) {
        $path = (Get-ItemProperty $key -ErrorAction SilentlyContinue).Path
        if ($path) {
            $path = $path.TrimEnd('\')
            if (Test-ApiFolder $path $api) { return $path }
        }
    }

    # 2. Uninstall registry
    $fromUninstall = Get-UninstallInstallLocation -DisplayName "Autodesk Navisworks $Product $Year"
    if (Test-ApiFolder $fromUninstall $api) { return $fromUninstall }

    # 3. Conventional layout
    foreach ($programFiles in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not $programFiles) { continue }
        $candidate = Join-Path $programFiles "Autodesk\Navisworks $Product $Year"
        if (Test-ApiFolder $candidate $api) { return $candidate }
    }

    return $null
}

function Get-RevitInstalls {
    $found = @()
    foreach ($year in $script:RevitYears) {
        $path = Get-RevitInstallPath -Year $year
        if ($path) {
            $found += [pscustomobject]@{
                Year    = $year
                Path    = $path
                # Revit 2025 moved the add-in runtime from .NET Framework to .NET 8.
                Runtime = $(if ($year -ge 2025) { 'net8.0-windows' } else { 'net48' })
            }
        }
    }
    return $found
}

function Get-NavisworksInstalls {
    $found = @()
    foreach ($year in $script:NavisYears) {
        foreach ($product in $script:NavisProducts) {
            $path = Get-NavisworksInstallPath -Year $year -Product $product
            if ($path) {
                $found += [pscustomobject]@{
                    Year    = $year
                    Product = $product
                    Path    = $path
                    Runtime = 'net48'
                }
            }
        }
    }
    return $found
}

function New-RevitAddInManifest {
    param([Parameter(Mandatory)][string]$Destination)

    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>$script:ProductName</Name>
    <Assembly>ABSwitchBack\ABSwitchBack.Revit.dll</Assembly>
    <FullClassName>ABSwitchBack.Revit.App</FullClassName>
    <ClientId>$script:AddInClientId</ClientId>
    <VendorId>$script:VendorId</VendorId>
    <VendorDescription>$script:ProductName - Navisworks to Revit switch back</VendorDescription>
  </AddIn>
</RevitAddIns>
"@

    # Revit's manifest parser wants plain UTF-8; a BOM makes some versions ignore the file.
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Destination, $xml, $encoding)
}

function Test-IsElevated {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Section {
    param([string]$Text)
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ('-' * $Text.Length) -ForegroundColor DarkGray
}
