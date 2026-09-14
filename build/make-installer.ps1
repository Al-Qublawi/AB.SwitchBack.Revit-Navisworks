<#
.SYNOPSIS
    Packages every Revit and Navisworks build in artifacts\ into dist\AB.SwitchBack.Setup.exe,
    a single self-contained installer.

.DESCRIPTION
    Replaces the 1.x MSI. The installer is built on the AB Adv Tools installer engine
    (shared\ABAdvTools): it finds earlier versions - the 1.x MSI included - offers to remove
    them, installs, and registers in Apps and Features.

      Revit       -> Addins\<year>\ for all users (default) or the current user
      Navisworks  -> <install>\Plugins\ABSwitchBack\ (needs administrator rights)

    Each built release becomes one embedded zip, so adding an Autodesk release means
    re-running build.ps1 and this script - nothing else.

    Publish the result as a GitHub release asset (tag v<version>) on
    Al-Qublawi/AB.SwitchBack.Revit-Navisworks: installed copies are told about new releases there.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\build.ps1
    powershell -ExecutionPolicy Bypass -File build\make-installer.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$repoRoot   = Split-Path $PSScriptRoot -Parent
$artifacts  = Join-Path $repoRoot 'artifacts'
$dist       = Join-Path $repoRoot 'dist'
$setupProj  = Join-Path $repoRoot 'installer\ABSwitchBack.Setup\ABSwitchBack.Setup.csproj'
$payloadDir = Join-Path $repoRoot 'installer\ABSwitchBack.Setup\payload'

if (-not (Test-Path $artifacts)) { throw 'No artifacts folder. Run build\build.ps1 first.' }

$version = ([xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props'))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
Write-Section "AB SwitchBack $version - installer"

if (Test-Path $payloadDir) { Remove-Item $payloadDir -Recurse -Force }
New-Item -ItemType Directory -Path $payloadDir, $dist -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-Payload([string]$name, [string]$stage) {
    $zip = Join-Path $payloadDir "$name.zip"
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
    Write-Host ("  {0,-18} {1,10:N0} bytes" -f "$name.zip", (Get-Item $zip).Length)
}

$packed = 0

# ---- Revit: the binaries that go in Addins\<year>\ABSwitchBack. The manifest is written by
#      the installer itself, so the add-in id lives in exactly one place per build.
$revitRoot = Join-Path $artifacts 'Revit'
foreach ($yearDir in @(Get-ChildItem $revitRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name)) {
    if (-not (Test-Path (Join-Path $yearDir.FullName 'ABSwitchBack.Revit.dll'))) { continue }

    $stage = Join-Path $env:TEMP "absb_payload_revit_$($yearDir.Name)"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage | Out-Null

    # Runtime files only. .deps.json matters for Revit 2025+ (.NET 8).
    Get-ChildItem $yearDir.FullName -File |
        Where-Object { $_.Extension -eq '.dll' -or $_.Name -like '*.deps.json' } |
        ForEach-Object { Copy-Item $_.FullName $stage }

    New-Payload "Revit$($yearDir.Name)" $stage
    Remove-Item $stage -Recurse -Force
    $packed++
}

# ---- Navisworks: the whole plugin folder - DLL, Images and the en-US ribbon layout.
$navisRoot = Join-Path $artifacts 'Navisworks'
foreach ($yearDir in @(Get-ChildItem $navisRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name)) {
    $plugin = Join-Path $yearDir.FullName 'ABSwitchBack'
    if (-not (Test-Path (Join-Path $plugin 'ABSwitchBack.dll'))) { continue }

    $stage = Join-Path $env:TEMP "absb_payload_navis_$($yearDir.Name)"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    Copy-Item $plugin $stage -Recurse
    Get-ChildItem $stage -Recurse -Filter *.pdb | Remove-Item -Force

    New-Payload "Navisworks$($yearDir.Name)" $stage
    Remove-Item $stage -Recurse -Force
    $packed++
}

if ($packed -eq 0) { throw 'Nothing to package. Run build\build.ps1 first.' }

Write-Section 'Building the setup executable'
# --no-incremental: the payload is embedded at compile time, and an incremental build would
# happily ship yesterday's binaries inside today's installer.
& dotnet build $setupProj -c $Configuration --nologo -v q --no-incremental
if ($LASTEXITCODE -ne 0) { throw 'Setup build failed.' }

$built = Join-Path $artifacts 'Setup\AB.SwitchBack.Setup.exe'
$final = Join-Path $dist 'AB.SwitchBack.Setup.exe'

$running = @(Get-Process -Name 'AB.SwitchBack.Setup' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) { throw "The installer is running (PID $($running.Id -join ', ')). Close it and re-run." }
Copy-Item $built $final -Force

# Prove the payloads really travelled inside the executable.
$assembly = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($final))
$embedded = @($assembly.GetManifestResourceNames() | Where-Object { $_ -like 'ABAdvTools.Payload.*' })
if ($embedded.Count -ne $packed) {
    throw "Expected $packed payload(s) but the installer holds $($embedded.Count). Do not distribute this build."
}

Write-Section 'Done'
Write-Host ("  {0}  ({1:N1} MB)" -f $final, ((Get-Item $final).Length / 1MB)) -ForegroundColor Green
Write-Host ("  {0} payloads: {1}" -f $packed, (($embedded | ForEach-Object { $_ -replace '^ABAdvTools\.Payload\.', '' -replace '\.zip$', '' }) -join ', '))
Write-Host ("  SHA256: {0}" -f (Get-FileHash $final -Algorithm SHA256).Hash)
Write-Host ''
Write-Host '  Silent:     AB.SwitchBack.Setup.exe /silent          (elevated prompt; all users)'
Write-Host '  Uninstall:  AB.SwitchBack.Setup.exe /uninstall /silent'
