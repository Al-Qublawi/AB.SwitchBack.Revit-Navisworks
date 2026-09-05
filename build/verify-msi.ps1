<#
.SYNOPSIS
    Reports exactly what an AB SwitchBack .msi will install, and where.

.DESCRIPTION
    Reads the MSI tables and reconstructs each file's full destination path by walking the
    Directory tree, so the layout can be checked without running the installer. Also lists
    the install condition on every component, which is what keeps Navisworks files off a
    machine that does not have that release.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\verify-msi.ps1
#>
[CmdletBinding()]
param(
    [string]$MsiPath
)

$ErrorActionPreference = 'Stop'

if (-not $MsiPath) {
    $repoRoot = Split-Path $PSScriptRoot -Parent
    $MsiPath = Get-ChildItem (Join-Path $repoRoot 'dist') -Filter '*.msi' |
               Sort-Object LastWriteTime -Descending |
               Select-Object -First 1 -ExpandProperty FullName
}
if (-not $MsiPath -or -not (Test-Path $MsiPath)) { throw 'No .msi found. Run build\make-msi.ps1 first.' }

$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($MsiPath, 0))

function Get-MsiRows {
    param([string]$Sql)

    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($Sql))
    $null = $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)

    $rows = New-Object System.Collections.Generic.List[object]
    while ($true) {
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $record) { break }

        $count = $record.GetType().InvokeMember('FieldCount', 'GetProperty', $null, $record, $null)
        $values = @()
        for ($i = 1; $i -le $count; $i++) {
            $values += $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @($i))
        }
        $rows.Add($values)
    }
    $null = $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
    return $rows
}

# Directory table: Directory, Directory_Parent, DefaultDir ("short|long").
$dirParent = @{}
$dirName = @{}
foreach ($row in Get-MsiRows 'SELECT Directory, Directory_Parent, DefaultDir FROM Directory') {
    $id = $row[0]
    $dirParent[$id] = $row[1]
    $name = $row[2]
    if ($name -match '\|') { $name = $name.Split('|')[-1] }
    $dirName[$id] = $name
}

function Resolve-DirPath {
    param([string]$Id)

    $parts = New-Object System.Collections.Generic.List[string]
    $current = $Id
    $guard = 0
    while ($current -and $guard -lt 32) {
        $guard++
        $name = $dirName[$current]
        if ($name -and $name -ne '.') { $parts.Insert(0, $name) }
        if (-not $dirParent.ContainsKey($current)) { break }
        $parent = $dirParent[$current]
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }
    # The outermost entry is a standard-directory token such as CommonAppDataFolder.
    if ($parts.Count -gt 0 -and -not $dirParent[$Id]) { }
    return ($parts -join '\')
}

$componentDir = @{}
$componentCondition = @{}
foreach ($row in Get-MsiRows 'SELECT Component, Directory_, Condition FROM Component') {
    $componentDir[$row[0]] = $row[1]
    $componentCondition[$row[0]] = $row[2]
}

$entries = New-Object System.Collections.Generic.List[object]
foreach ($row in Get-MsiRows 'SELECT File, Component_, FileName, FileSize FROM File') {
    $component = $row[1]
    $name = $row[2]
    if ($name -match '\|') { $name = $name.Split('|')[-1] }

    $dir = $componentDir[$component]
    $entries.Add([pscustomobject]@{
        Path      = (Resolve-DirPath -Id $dir) + '\' + $name
        Root      = $dir
        Condition = $componentCondition[$component]
        Size      = [int]$row[3]
    })
}

Write-Host ''
Write-Host ("Package: " + (Split-Path $MsiPath -Leaf)) -ForegroundColor Cyan
Write-Host ("Files:   {0}   ({1:N0} KB uncompressed)" -f $entries.Count, (($entries | Measure-Object Size -Sum).Sum / 1KB))
Write-Host ''

foreach ($group in $entries | Group-Object { Split-Path $_.Path -Parent } | Sort-Object Name) {
    $condition = ($group.Group | Select-Object -First 1).Condition
    $suffix = if ($condition) { "   [only if $condition]" } else { '' }

    Write-Host ($group.Name + $suffix) -ForegroundColor Yellow
    foreach ($entry in $group.Group | Sort-Object Path) {
        Write-Host ("    {0}" -f (Split-Path $entry.Path -Leaf))
    }
}

Write-Host ''
Write-Host 'Branding' -ForegroundColor Cyan
foreach ($row in Get-MsiRows 'SELECT Property, Value FROM Property') {
    if ($row[0] -match '^(ProductName|ProductVersion|Manufacturer|ARPCONTACT|ARPURLINFOABOUT|ARPPRODUCTICON)$') {
        Write-Host ("    {0,-18} {1}" -f $row[0], $row[1])
    }
}
Write-Host ''
