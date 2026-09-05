<#
.SYNOPSIS
    Builds a single AB SwitchBack .msi covering every Revit and Navisworks release that
    was compiled into artifacts\.

.DESCRIPTION
    The WiX source is generated from the contents of artifacts\ rather than hand-written,
    so adding an Autodesk release means re-running build.ps1 and this script - no XML edits.

    Deployment model (per-machine, so it suits a team rollout):

      Revit      -> %ProgramData%\Autodesk\Revit\Addins\<year>\
                    Installed for every built year unconditionally. Revit only reads its
                    own year folder, so one MSI serves a team on mixed Revit versions.

      Navisworks -> <install>\Plugins\ABSwitchBack\
                    The install path is read from the registry at install time, and the
                    files are skipped entirely when that release is not present.

    Requires the WiX 5 CLI:  dotnet tool install --global wix

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\make-msi.ps1
#>
[CmdletBinding()]
param(
    [string]$ProductVersion = '1.1.1',
    [switch]$KeepSource
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$repoRoot  = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $repoRoot 'artifacts'
$assets    = Join-Path $repoRoot 'assets'
$dist      = Join-Path $repoRoot 'dist'
$objDir    = Join-Path $repoRoot 'obj\msi'

# Constant for the life of the product: MSI upgrade logic keys off it.
$upgradeCode = 'EC8C6EF4-D80D-43D8-AAFA-81653D9BA6FD'

if (-not (Test-Path $artifacts)) { throw "No artifacts folder. Run build\build.ps1 first." }

$wix = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wix) {
    $candidate = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
    if (Test-Path $candidate) { $wix = $candidate } else {
        throw "The WiX CLI was not found. Install it with:  dotnet tool install --global wix"
    }
}
else { $wix = $wix.Source }

New-Item -ItemType Directory -Path $dist, $objDir -Force | Out-Null

# ---------------------------------------------------------------- gather payload

$components = New-Object System.Collections.Generic.List[string]
$groupRefs  = New-Object System.Collections.Generic.List[string]
$directories = New-Object System.Collections.Generic.List[string]
$properties = New-Object System.Collections.Generic.List[string]
$setProps   = New-Object System.Collections.Generic.List[string]

$fileIndex = 0
function New-FileComponent {
    param(
        [string]$DirectoryId,
        [string]$SourcePath,
        [string]$Condition
    )
    $script:fileIndex++
    $id = "cmp$($script:fileIndex)"
    $fileId = "fil$($script:fileIndex)"
    $name = Split-Path $SourcePath -Leaf

    $conditionAttr = if ($Condition) { " Condition=`"$Condition`"" } else { '' }

    return @"
      <Component Id="$id" Directory="$DirectoryId" Guid="*"$conditionAttr>
        <File Id="$fileId" Name="$name" Source="$SourcePath" KeyPath="yes" />
      </Component>
"@
}

Write-Section 'Collecting Revit payloads'

$revitYears = @()
$revitRoot = Join-Path $artifacts 'Revit'
if (Test-Path $revitRoot) {
    $revitYears = Get-ChildItem $revitRoot -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'ABSwitchBack.Revit.dll') } |
        Sort-Object Name
}
if ($revitYears.Count -eq 0) { Write-Host '  none' -ForegroundColor Yellow }

$revitDirXml = New-Object System.Collections.Generic.List[string]
foreach ($yearDir in $revitYears) {
    $year = $yearDir.Name
    $yearDirId = "RevitYear$year"
    $payloadDirId = "RevitPayload$year"

    $revitDirXml.Add(@"
          <Directory Id="$yearDirId" Name="$year">
            <Directory Id="$payloadDirId" Name="ABSwitchBack" />
          </Directory>
"@)

    $groupId = "GrpRevit$year"
    $inner = New-Object System.Collections.Generic.List[string]

    # The manifest sits in the year folder; the binaries in the subfolder it points at.
    $manifest = Join-Path $yearDir.FullName 'ABSwitchBack.addin'
    if (-not (Test-Path $manifest)) { New-RevitAddInManifest -Destination $manifest }
    $inner.Add((New-FileComponent -DirectoryId $yearDirId -SourcePath $manifest))

    Get-ChildItem $yearDir.FullName -File |
        Where-Object { $_.Extension -eq '.dll' -or $_.Name -like '*.deps.json' } |
        ForEach-Object { $inner.Add((New-FileComponent -DirectoryId $payloadDirId -SourcePath $_.FullName)) }

    $components.Add(@"
    <ComponentGroup Id="$groupId">
$($inner -join "`r`n")
    </ComponentGroup>
"@)
    $groupRefs.Add("      <ComponentGroupRef Id=`"$groupId`" />")
    Write-Host "  Revit $year"
}

Write-Section 'Collecting Navisworks payloads'

$navisRoot = Join-Path $artifacts 'Navisworks'
$navisYears = @()
if (Test-Path $navisRoot) {
    $navisYears = Get-ChildItem $navisRoot -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'ABSwitchBack\ABSwitchBack.dll') } |
        Sort-Object Name
}
if ($navisYears.Count -eq 0) { Write-Host '  none' -ForegroundColor Yellow }

$navisDirXml = New-Object System.Collections.Generic.List[string]
foreach ($yearDir in $navisYears) {
    $year = [int]$yearDir.Name
    $payloadRoot = Join-Path $yearDir.FullName 'ABSwitchBack'

    # Navisworks API major 21 == the 2024 release.
    $major = $year - 2003

    foreach ($product in $script:NavisProducts) {
        $tag = "$product$year"
        $foundProp = ("NW{0}FOUND" -f $tag).ToUpper()
        $dirProp = ("NW{0}DIR" -f $tag).ToUpper()

        # Locate the install at install time; skip the files entirely when absent.
        $properties.Add(@"
    <Property Id="$foundProp">
      <RegistrySearch Id="Search$tag" Root="HKLM"
                      Key="SOFTWARE\Autodesk\Navisworks $product\$major.0\Location"
                      Name="Path" Type="directory" />
    </Property>
"@)
        $setProps.Add("    <SetProperty Id=`"$dirProp`" Value=`"[$foundProp]`" After=`"AppSearch`" Sequence=`"both`" Condition=`"$foundProp`" />")

        # A resolvable default keeps the MSI valid when the product is not installed;
        # the registry value overrides it whenever it is.
        $navisDirXml.Add(@"
        <Directory Id="$dirProp" Name="Navisworks $product $year">
          <Directory Id="NWPlugins$tag" Name="Plugins">
            <Directory Id="NWPayload$tag" Name="ABSwitchBack">
              <Directory Id="NWImages$tag" Name="Images" />
              <Directory Id="NWLocale$tag" Name="en-US" />
            </Directory>
          </Directory>
        </Directory>
"@)

        $groupId = "GrpNavis$tag"
        $inner = New-Object System.Collections.Generic.List[string]

        Get-ChildItem $payloadRoot -File |
            Where-Object { $_.Extension -eq '.dll' } |
            ForEach-Object { $inner.Add((New-FileComponent -DirectoryId "NWPayload$tag" -SourcePath $_.FullName -Condition $foundProp)) }

        $imagesDir = Join-Path $payloadRoot 'Images'
        if (Test-Path $imagesDir) {
            Get-ChildItem $imagesDir -File | ForEach-Object {
                $inner.Add((New-FileComponent -DirectoryId "NWImages$tag" -SourcePath $_.FullName -Condition $foundProp))
            }
        }

        $localeDir = Join-Path $payloadRoot 'en-US'
        if (Test-Path $localeDir) {
            Get-ChildItem $localeDir -File | ForEach-Object {
                $inner.Add((New-FileComponent -DirectoryId "NWLocale$tag" -SourcePath $_.FullName -Condition $foundProp))
            }
        }

        $components.Add(@"
    <ComponentGroup Id="$groupId">
$($inner -join "`r`n")
    </ComponentGroup>
"@)
        $groupRefs.Add("      <ComponentGroupRef Id=`"$groupId`" />")
        Write-Host "  Navisworks $product $year"
    }
}

if ($revitYears.Count -eq 0 -and $navisYears.Count -eq 0) {
    throw 'Nothing to package. Run build\build.ps1 first.'
}

# ---------------------------------------------------------------- emit WiX source

$logoIcon = Join-Path $assets 'logo.ico'
$msiVersion = "$ProductVersion.0"

$wxs = @"
<?xml version="1.0" encoding="utf-8"?>
<!--
  Generated by build\make-msi.ps1 - do not edit by hand.
  Regenerate with:  powershell -ExecutionPolicy Bypass -File build\make-msi.ps1
-->
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">

  <Package Name="AB SwitchBack"
           Manufacturer="$($script:ProductAuthor)"
           Version="$msiVersion"
           UpgradeCode="$upgradeCode"
           Scope="perMachine"
           Compressed="yes"
           InstallerVersion="500">

    <SummaryInformation Description="AB SwitchBack - Navisworks to Revit switch back"
                        Manufacturer="$($script:ProductAuthor)" />

    <MajorUpgrade AllowSameVersionUpgrades="yes"
                  DowngradeErrorMessage="A newer version of AB SwitchBack is already installed." />

    <MediaTemplate EmbedCab="yes" />

    <Icon Id="AppIcon.ico" SourceFile="$logoIcon" />
    <Property Id="ARPPRODUCTICON" Value="AppIcon.ico" />
    <Property Id="ARPURLINFOABOUT" Value="$($script:LinkedInUrl)" />
    <Property Id="ARPCONTACT" Value="$($script:ProductAuthor)" />
    <Property Id="ARPNOMODIFY" Value="1" />

    <!--
      A running Revit or Navisworks holds these DLLs open. The Restart Manager is disabled
      deliberately: left on, it offers to shut those applications down to complete the
      install, which risks a modeller losing unsaved work. Disabling it falls back to the
      classic "files in use" prompt, which asks the user to close them and retry.
    -->
    <Property Id="MSIRESTARTMANAGERCONTROL" Value="Disable" />

$($properties -join "`r`n")

$($setProps -join "`r`n")

    <StandardDirectory Id="CommonAppDataFolder">
      <Directory Id="AutodeskProgramData" Name="Autodesk">
        <Directory Id="RevitProgramData" Name="Revit">
          <Directory Id="RevitAddinsRoot" Name="Addins">
$($revitDirXml -join "`r`n")
          </Directory>
        </Directory>
      </Directory>
    </StandardDirectory>

    <StandardDirectory Id="ProgramFiles64Folder">
      <Directory Id="AutodeskProgramFiles" Name="Autodesk">
$($navisDirXml -join "`r`n")
      </Directory>
    </StandardDirectory>

$($components -join "`r`n")

    <Feature Id="Main" Title="AB SwitchBack" Level="1" AllowAbsent="no">
$($groupRefs -join "`r`n")
    </Feature>

  </Package>
</Wix>
"@

$wxsPath = Join-Path $objDir 'ABSwitchBack.wxs'
$encoding = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($wxsPath, $wxs, $encoding)

Write-Section 'Building the MSI'
Write-Host "  source: $wxsPath"

$msiPath = Join-Path $dist "ABSwitchBack-$ProductVersion.msi"
$output = & $wix build $wxsPath -arch x64 -o $msiPath 2>&1
$exit = $LASTEXITCODE

$output | ForEach-Object { Write-Host "  $_" }

if ($exit -ne 0) {
    throw "wix build failed with exit code $exit."
}

if (-not $KeepSource) { Remove-Item $wxsPath -Force -ErrorAction SilentlyContinue }

$size = [Math]::Round((Get-Item $msiPath).Length / 1MB, 2)
Write-Section 'Done'
Write-Host ("  {0}  ({1} MB)" -f $msiPath, $size) -ForegroundColor Green
