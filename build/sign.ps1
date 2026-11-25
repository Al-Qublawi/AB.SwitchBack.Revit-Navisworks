<#
.SYNOPSIS
    Authenticode-signs files with signtool when a code-signing certificate is configured; otherwise says
    so and leaves them unsigned.

.DESCRIPTION
    Configure one of these (environment variables, so no secret lands in the repository):

      AB_SIGN_THUMBPRINT         thumbprint of a certificate in CurrentUser\My or LocalMachine\My
                                 (hardware tokens and cloud HSM certificates appear here too)
      AB_SIGN_PFX                path to a .pfx file, with AB_SIGN_PFX_PASSWORD

      AB_SIGN_TIMESTAMP          RFC 3161 timestamp server (default http://timestamp.digicert.com)

    Microsoft's Artifact Signing (formerly Trusted Signing) uses its own dlib with signtool; set
    AB_SIGN_DLIB and AB_SIGN_DLIB_METADATA to the dlib path and its metadata.json to use it.

.EXAMPLE
    .\build\sign.ps1 -Files dist\bundle\ABSwitchBack.bundle\Contents\Revit\2026\ABSwitchBack.Revit.dll
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string[]] $Files,
    [switch] $Required
)

$ErrorActionPreference = 'Stop'

$thumbprint = $env:AB_SIGN_THUMBPRINT
$pfx        = $env:AB_SIGN_PFX
$dlib       = $env:AB_SIGN_DLIB
$timestamp  = if ($env:AB_SIGN_TIMESTAMP) { $env:AB_SIGN_TIMESTAMP } else { 'http://timestamp.digicert.com' }

if (-not $thumbprint -and -not $pfx -and -not $dlib) {
    $message = 'Not signed: no code-signing certificate configured (see build\sign.ps1).'
    if ($Required) { throw $message }
    Write-Host "   [note] $message" -ForegroundColor Yellow
    return
}

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName | Select-Object -Last 1 -ExpandProperty FullName
if (-not $signtool) { throw 'signtool.exe was not found. Install the Windows SDK signing tools.' }

$arguments = @('sign', '/fd', 'SHA256', '/tr', $timestamp, '/td', 'SHA256')
if ($dlib)           { $arguments += @('/dlib', $dlib, '/dmdf', $env:AB_SIGN_DLIB_METADATA) }
elseif ($thumbprint) { $arguments += @('/sha1', $thumbprint) }
else                 { $arguments += @('/f', $pfx, '/p', $env:AB_SIGN_PFX_PASSWORD) }

foreach ($file in $Files) {
    & $signtool @arguments $file | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $file (signtool exit $LASTEXITCODE)." }
    & $signtool verify /pa $file | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "The signature on $file does not verify." }
    Write-Host ("   [ok]   signed {0}" -f (Split-Path -Leaf $file)) -ForegroundColor Green
}
