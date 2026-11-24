<#
.SYNOPSIS
    Produces the Autodesk Marketplace package: dist\bundle\ABSwitchBack.bundle and a .zip of it to upload.

.DESCRIPTION
    Autodesk builds the store installer from a .bundle folder; Revit and Navisworks load it from
    ApplicationPlugins through PackageContents.xml, one Components entry per release with SeriesMin and
    SeriesMax set to that release.

      ABSwitchBack.bundle\
        PackageContents.xml
        Contents\Revit\2020 ... 2027\     ABSwitchBack.addin, ABSwitchBack.Revit.dll, ABSwitchBack.Core.dll
        Contents\Navisworks\v21 ... v24\  ABSwitchBack.dll, en-US\ABSwitchBack.xaml, Images\  (2024 ... 2027)
        Contents\Resources\               icon, Help\ (user guide = F1 help, privacy policy, licence)

    Everything is built with -StoreBuild: no GitHub release check, no update UI (the store forbids an
    app's own update mechanism). The script checks that in the staged files before zipping.

    SwitchBack needs both halves, so by default one package carries Revit and Navisworks, uploaded to the
    Revit listing and to the Navisworks listing. -Product Revit or -Product Navisworks makes a package
    with one half, should Autodesk ask for one package per product.

.EXAMPLE
    .\build\make-bundle.ps1
    .\build\make-bundle.ps1 -Product Revit
    .\build\make-bundle.ps1 -RevitYears 2027 -NavisYears 2027 -SkipBuild

    On Linux (cloud session): pwsh -File build/make-bundle.ps1   (needs the .NET 8 and 10 SDKs)
#>
[CmdletBinding()]
param(
    [ValidateSet('Both', 'Revit', 'Navisworks')]
    [string]$Product = 'Both',
    # Split on commas as well: started with  powershell -File , "2024,2026" arrives as one string.
    [string[]]$RevitYears,
    [string[]]$NavisYears,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

function Split-Years([string[]]$Years, [int[]]$Default) {
    $list = @($Years | ForEach-Object { "$_" -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | ForEach-Object {
        if ($_ -notmatch '^\d{4}$') { throw "'$_' is not a release year." }
        [int]$_
    })
    if ($list.Count -gt 0) { return $list } else { return $Default }
}

$revitYears = if ($Product -ne 'Navisworks') { @(Split-Years $RevitYears $script:RevitYears) } else { @() }
$navisYears = if ($Product -ne 'Revit') { @(Split-Years $NavisYears $script:NavisYears) } else { @() }

$repoRoot  = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts\store'
$outRoot   = Join-Path $repoRoot 'dist\bundle'
$bundle    = Join-Path $outRoot 'ABSwitchBack.bundle'
$store     = Import-PowerShellDataFile (Join-Path $repoRoot 'store\store.psd1')
$utf8      = New-Object System.Text.UTF8Encoding($false)

function Step($t) { Write-Host ''; Write-Host "== $t" -ForegroundColor Cyan }
function Ok($t)   { Write-Host "   [ok]   $t" -ForegroundColor Green }
function Note($t) { Write-Host "   [note] $t" -ForegroundColor Yellow }
function Die($t)  { Write-Host "   [fail] $t" -ForegroundColor Red; exit 1 }
function Esc([string]$s) { [System.Security.SecurityElement]::Escape($s) }

# Navisworks 2024 is API major 21 (common.ps1 uses the same rule for the registry): Nw21 in
# PackageContents, and v21 as its folder, as Autodesk's Navisworks publisher guidelines lay out.
function NavisMajor([int]$year) { $year - 2003 }

$version = ([xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props'))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1

Write-Host ''
Write-Host "  $($store.Name) $version - Autodesk Marketplace bundle ($Product)" -ForegroundColor White
if ($revitYears.Count) { Write-Host ("  Revit:      " + ($revitYears -join ', ')) -ForegroundColor DarkGray }
if ($navisYears.Count) { Write-Host ("  Navisworks: " + ($navisYears -join ', ')) -ForegroundColor DarkGray }

if (-not $SkipBuild) {
    Step 'Building the store build of every release'
    $buildArgs = @{ StoreBuild = $true }
    # build.ps1 builds the default list for an empty parameter, so a one-product package passes a year
    # that is then not staged - cheaper than a second code path in build.ps1.
    $buildArgs.RevitYears = if ($revitYears.Count) { [string[]]$revitYears } else { [string[]]@($script:RevitYears[-1]) }
    $buildArgs.NavisYears = if ($navisYears.Count) { [string[]]$navisYears } else { [string[]]@($script:NavisYears[-1]) }
    & (Join-Path $PSScriptRoot 'build.ps1') @buildArgs
    if ($LASTEXITCODE -ne 0) { Die 'Build failed.' }
}

Step 'Staging the bundle'
if (Test-Path $outRoot) { [System.IO.Directory]::Delete($outRoot, $true) }
$contents = New-Item -ItemType Directory -Force -Path (Join-Path $bundle 'Contents')
$components = New-Object System.Text.StringBuilder
$toSign = @()

foreach ($year in $revitYears) {
    $source = Join-Path $artifacts "Revit\$year"
    if (-not (Test-Path (Join-Path $source 'ABSwitchBack.Revit.dll'))) { Die "Revit $year is not built in $source." }
    $to = New-Item -ItemType Directory -Force -Path (Join-Path $contents.FullName "Revit\$year")
    Get-ChildItem $source -File | Where-Object { $_.Extension -in '.dll', '.json' } | ForEach-Object {
        Copy-Item $_.FullName -Destination $to.FullName
        if ($_.Extension -eq '.dll') { $toSign += (Join-Path $to.FullName $_.Name) }
    }

    # The assembly path is relative to the manifest, which sits beside it.
    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>$(Esc $store.Name)</Name>
    <Assembly>ABSwitchBack.Revit.dll</Assembly>
    <FullClassName>ABSwitchBack.Revit.App</FullClassName>
    <AddInId>$($store.AddInId)</AddInId>
    <VendorId>$($store.VendorId)</VendorId>
    <VendorDescription>$(Esc $store.Publisher)</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
    [System.IO.File]::WriteAllText((Join-Path $to.FullName 'ABSwitchBack.addin'), $manifest, $utf8)

    [void]$components.AppendLine(("  <Components Description=""Revit {0}"">" -f $year))
    [void]$components.AppendLine(("    <RuntimeRequirements OS=""Win64"" Platform=""Revit"" SeriesMin=""R{0}"" SeriesMax=""R{0}"" />" -f $year))
    [void]$components.AppendLine(("    <ComponentEntry AppName=""{0}"" Version=""{1}"" ModuleName=""./Contents/Revit/{2}/ABSwitchBack.addin"" AppDescription=""{0} for Revit {2}"" />" -f (Esc $store.Name), $version, $year))
    [void]$components.AppendLine('  </Components>')
    Ok "Revit $year"
}

foreach ($year in $navisYears) {
    $source = Join-Path $artifacts "Navisworks\$year\ABSwitchBack"
    if (-not (Test-Path (Join-Path $source 'ABSwitchBack.dll'))) { Die "Navisworks $year is not built in $source." }
    $major = NavisMajor $year
    $to = Join-Path $contents.FullName "Navisworks\v$major"
    New-Item -ItemType Directory -Force -Path $to | Out-Null

    # The plugin needs its ribbon layout in en-US\ and its button images in Images\ beside the DLL.
    Copy-Item (Join-Path $source 'ABSwitchBack.dll') -Destination $to
    Copy-Item (Join-Path $source 'en-US') -Destination $to -Recurse
    Copy-Item (Join-Path $source 'Images') -Destination $to -Recurse
    $toSign += (Join-Path $to 'ABSwitchBack.dll')

    $xaml = Get-Content (Join-Path $to 'en-US\ABSwitchBack.xaml') -Raw
    if ($xaml -match 'ABADV_SharedPanel|ID_ABADV_') {
        Die "Navisworks $year carries the shared AB Adv Tools panel (Check for Updates) - not a store build."
    }

    [void]$components.AppendLine(("  <Components Description=""Navisworks {0}"">" -f $year))
    [void]$components.AppendLine(("    <RuntimeRequirements OS=""Win64"" Platform=""NAVMAN|NAVSIM"" SeriesMin=""Nw{0}"" SeriesMax=""Nw{0}"" />" -f $major))
    [void]$components.AppendLine(("    <ComponentEntry AppName=""{0}"" Version=""{1}"" ModuleName=""./Contents/Navisworks/v{2}/ABSwitchBack.dll"" AppType=""ManagedPlugin"" AppDescription=""{0} for Navisworks {3}"" />" -f (Esc $store.Name), $version, $major, $year))
    [void]$components.AppendLine('  </Components>')
    Ok "Navisworks $year (v$major)"
}

# Every DLL staged must be a store build: Directory.Build.props stamps those, and only those, with
# AssemblyMetadata("ABStoreBuild", "true"). A GitHub build copied in by mistake would carry the update check.
foreach ($dll in $toSign) {
    $text = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($dll))
    if ($text.IndexOf('ABStoreBuild', [StringComparison]::Ordinal) -lt 0) {
        Die "$dll is not a store build (no ABStoreBuild stamp)."
    }
}
Ok ("{0} DLLs carry the store-build stamp" -f $toSign.Count)

$resources = New-Item -ItemType Directory -Force -Path (Join-Path $contents.FullName 'Resources')
Copy-Item (Join-Path $repoRoot 'assets\store_icon_256.png') -Destination (Join-Path $resources.FullName 'ABSwitchBack.png')
$help = New-Item -ItemType Directory -Force -Path (Join-Path $resources.FullName 'Help')
Get-ChildItem (Join-Path $repoRoot 'help') -File | ForEach-Object { Copy-Item $_.FullName -Destination $help.FullName }
Copy-Item (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $help.FullName 'LICENSE.txt')
Ok 'Icon, help page, privacy policy and licence'

$autodeskProduct = if ($Product -eq 'Navisworks') { 'Navisworks' } else { 'Revit' }
$companyEmail = if ($store.SupportEmail) { ' Email="' + (Esc $store.SupportEmail) + '"' } else { '' }
$companyUrl = if ($store.Website) { ' Url="' + (Esc $store.Website) + '"' } else { '' }
$online = if ($store.Website) { ' OnlineDocumentation="' + (Esc $store.Website) + '"' } else { '' }
$package = @"
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="$autodeskProduct" ProductType="Application" Name="$(Esc $store.Name)" AppVersion="$version" FriendlyVersion="$version" Description="$(Esc $store.Description)" Author="$(Esc $store.Publisher)" Icon="./Contents/Resources/ABSwitchBack.png" HelpFile="./Contents/Resources/Help/index.html"$online ProductCode="$($store.ProductCode)" UpgradeCode="$($store.UpgradeCode)" SupportedLocales="Enu">
  <CompanyDetails Name="$(Esc $store.Publisher)"$companyEmail$companyUrl />
$($components.ToString().TrimEnd())
</ApplicationPackage>
"@
[System.IO.File]::WriteAllText((Join-Path $bundle 'PackageContents.xml'), $package, $utf8)
[xml]$check = Get-Content (Join-Path $bundle 'PackageContents.xml')
foreach ($entry in $check.ApplicationPackage.Components.ComponentEntry) {
    $module = Join-Path $bundle ($entry.ModuleName -replace '^\./', '')
    if (-not (Test-Path $module)) { Die "PackageContents.xml points at a missing file: $($entry.ModuleName)" }
}
Ok ("PackageContents.xml ({0} components, every ModuleName present)" -f @($check.ApplicationPackage.Components).Count)

Step 'Signing'
$sign = Join-Path $PSScriptRoot 'sign.ps1'
if (Test-Path $sign) { & $sign -Files $toSign } else { Note 'Not signed (no build\sign.ps1).' }

Step 'Zipping for upload'
$suffix = if ($Product -eq 'Both') { '' } else { "-$Product" }
$zip = Join-Path $outRoot ("ABSwitchBack-{0}{1}.bundle.zip" -f $version, $suffix)
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($bundle, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
Ok ("{0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))

Write-Host ''
Write-Host '  Bundle ready. To try it locally, uninstall the GitHub .msi first (same add-in id and plugin),'
Write-Host "  then copy $bundle"
Write-Host '  to %AppData%\Autodesk\ApplicationPlugins and start Revit and Navisworks.'
Write-Host ''
