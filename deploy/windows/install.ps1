<#
.SYNOPSIS
Installs Dormant as a Windows Service. Run in an elevated PowerShell:

    .\deploy\windows\install.ps1 -Executable .\dormant.exe

.DESCRIPTION
Copies dormant.exe to "C:\Program Files\Dormant", then runs 'dormant service install', which:
- writes %ProgramData%\Dormant\dormant.yaml from the example if there is none;
- creates the service 'Dormant' running 'dormant run' as NT SERVICE\Dormant, starting automatically (delayed),
  restarting after failures;
- gives the service account read access to the configuration and write access to the history and reports, in
  directories only Administrators, SYSTEM and the service can open;
- registers the event log source 'Dormant' in the Application log.
The service is not started until you have configured it. Running this again replaces the executable and keeps the
configuration and history.

.PARAMETER Account
A group managed service account (CORP\dormant$) instead of the virtual account, for Windows authentication to
SQL Server on other machines. Passwords are never accepted.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Executable,
    [string] $InstallDirectory = (Join-Path $env:ProgramFiles 'Dormant'),
    [string] $Account
)

$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this in an elevated PowerShell (Run as administrator).'
}

$target = Join-Path $InstallDirectory 'dormant.exe'
$existing = Get-Service -Name Dormant -ErrorAction SilentlyContinue
if ($existing) {
    # An upgrade: stop the service, replace the executable, start it again if it was running.
    $wasRunning = $existing.Status -eq 'Running'
    Stop-Service -Name Dormant
    New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
    Copy-Item -Force -Path $Executable -Destination $target
    Write-Host "Replaced $target ($(& $target version | Select-Object -First 1))."
    if ($wasRunning) { Start-Service -Name Dormant; Write-Host 'Started the Dormant service again.' }
    return
}

New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
Copy-Item -Force -Path $Executable -Destination $target
Write-Host "Installed $target ($(& $target version | Select-Object -First 1))."

$arguments = @('service', 'install')
if ($Account) { $arguments += @('--account', $Account) }
& $target @arguments
if ($LASTEXITCODE -ne 0) { throw "dormant service install failed with exit code $LASTEXITCODE." }
