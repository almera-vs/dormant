<#
.SYNOPSIS
Stops and removes the Dormant Windows Service and its executable. Run in an elevated PowerShell.

.DESCRIPTION
The configuration, history and reports in %ProgramData%\Dormant are kept; delete that directory yourself once you no
longer need the history.
#>
[CmdletBinding()]
param(
    [string] $InstallDirectory = (Join-Path $env:ProgramFiles 'Dormant')
)

$ErrorActionPreference = 'Stop'

$target = Join-Path $InstallDirectory 'dormant.exe'
if (Test-Path $target) {
    & $target service uninstall
    if ($LASTEXITCODE -ne 0) { throw "dormant service uninstall failed with exit code $LASTEXITCODE." }
    Remove-Item -Force $target
    if (-not (Get-ChildItem $InstallDirectory -ErrorAction SilentlyContinue)) { Remove-Item -Force $InstallDirectory }
    Write-Host "Removed $target. Kept $(Join-Path $env:ProgramData 'Dormant')."
}
else {
    Write-Host "Dormant is not installed in $InstallDirectory."
}
