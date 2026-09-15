<#
.SYNOPSIS
    Packages every Revit and Navisworks build in artifacts\ into dist\AB.SwitchBack-<version>.msi.

.DESCRIPTION
    One Windows Installer package for every release built, made by the AB Adv Tools kit
    (shared\ABAdvTools\msi) from installer\SwitchBack.msi.psd1. The package runs no code of its
    own, so company PCs with Defender's attack surface reduction rules install it like any .msi.

      Revit       -> Addins\<year>\ for everyone (default, ProgramData) or only me (AppData)
      Navisworks  -> <install>\Plugins\ABSwitchBack\ (everyone only)

    It upgrades the 1.x packages in place (same UpgradeCode), and offers to remove a copy that the
    retired AB.SwitchBack.Setup.exe (1.3.0) installed.

    Each built release is one tick on the installer's "Choose releases" page, so adding an Autodesk
    release means re-running build.ps1 and this script - nothing else.

    Publish the result as a GitHub release asset (tag v<version>) on
    Al-Qublawi/AB.SwitchBack.Revit-Navisworks: installed copies are told about new releases there.

    Requires the WiX CLI 5:  dotnet tool install --global wix --version 5.0.2
                             wix extension add -g WixToolset.UI.wixext/5.0.2

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\build.ps1
    powershell -ExecutionPolicy Bypass -File build\make-installer.ps1
    powershell -ExecutionPolicy Bypass -File build\make-installer.ps1 -DryRun   # what it would do here
#>
[CmdletBinding()]
param(
    # After building, show what the package would do on this computer, changing nothing.
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$repoRoot  = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $repoRoot 'artifacts'
$dist      = Join-Path $repoRoot 'dist'
$stage     = Join-Path $repoRoot 'obj\msi\payload'
$kitMsi    = Join-Path $repoRoot 'shared\ABAdvTools\msi'

if (-not (Test-Path $artifacts)) { throw 'No artifacts folder. Run build\build.ps1 first.' }

$version = ([xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props'))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
Write-Section "AB SwitchBack $version - installer"

if (Test-Path $stage) { [System.IO.Directory]::Delete($stage, $true) }
New-Item -ItemType Directory -Path $stage, $dist -Force | Out-Null
$units = 0

# ---- Revit: the binaries that go in Addins\<year>\ABSwitchBack. The manifest is written by the
#      kit from the definition, so the add-in id lives in exactly one place.
foreach ($yearDir in @(Get-ChildItem (Join-Path $artifacts 'Revit') -Directory -ErrorAction SilentlyContinue | Sort-Object Name)) {
    if (-not (Test-Path (Join-Path $yearDir.FullName 'ABSwitchBack.Revit.dll'))) { continue }
    $to = New-Item -ItemType Directory -Force -Path (Join-Path $stage "Revit$($yearDir.Name)")
    # Runtime files only. .deps.json matters for Revit 2025+ (.NET 8).
    Get-ChildItem $yearDir.FullName -File |
        Where-Object { $_.Extension -eq '.dll' -or $_.Name -like '*.deps.json' } |
        ForEach-Object { Copy-Item $_.FullName $to.FullName }
    Write-Host "  Revit $($yearDir.Name)"
    $units++
}

# ---- Navisworks: the whole plugin folder - DLL, Images and the en-US ribbon layout.
foreach ($yearDir in @(Get-ChildItem (Join-Path $artifacts 'Navisworks') -Directory -ErrorAction SilentlyContinue | Sort-Object Name)) {
    $plugin = Join-Path $yearDir.FullName 'ABSwitchBack'
    if (-not (Test-Path (Join-Path $plugin 'ABSwitchBack.dll'))) { continue }
    $to = Join-Path $stage "Navisworks$($yearDir.Name)"
    Copy-Item $plugin $to -Recurse
    Get-ChildItem $to -Recurse -Filter *.pdb | ForEach-Object { [System.IO.File]::Delete($_.FullName) }
    Write-Host "  Navisworks $($yearDir.Name)"
    $units++
}

if ($units -eq 0) { throw 'Nothing to package. Run build\build.ps1 first.' }

Write-Section 'Building the .msi'
$built = & (Join-Path $kitMsi 'New-AdvToolsMsi.ps1') `
    -Definition (Join-Path $repoRoot 'installer\SwitchBack.msi.psd1') `
    -Payload $stage -Version $version -OutputDirectory $dist `
    -WorkDirectory (Join-Path $repoRoot 'obj\msi\work')

# Older builds in dist\ would only confuse whoever picks a file to publish.
Get-ChildItem $dist -File | Where-Object { $_.FullName -ne $built.Path -and ($_.Name -like 'AB.SwitchBack*' -or $_.Name -like 'ABSwitchBack*') } |
    ForEach-Object { [System.IO.File]::Delete($_.FullName) }

Write-Section 'Done'
Write-Host ("  {0}  ({1:N1} MB)" -f $built.Path, ((Get-Item $built.Path).Length / 1MB)) -ForegroundColor Green
Write-Host ("  SHA256: {0}" -f $built.Sha256)
Write-Host ''
Write-Host '  Install:    double-click, or  msiexec /i AB.SwitchBack-<version>.msi'
Write-Host '  Silent:     msiexec /i AB.SwitchBack-<version>.msi /qn             (everyone; elevated prompt)'
Write-Host '              msiexec /i AB.SwitchBack-<version>.msi /qn MSIINSTALLPERUSER=1   (only me: Revit only)'
Write-Host '  Uninstall:  Apps and Features, or  msiexec /x AB.SwitchBack-<version>.msi /qn'

if ($DryRun) {
    & (Join-Path $kitMsi 'Test-AdvToolsMsi.ps1') -Msi $built.Path -WorkDirectory (Join-Path $repoRoot 'obj\msi\dryrun') | Out-Null
}
