# Install (or update) the OmdropOwl service: publishes it to
# %ProgramFiles%\omdrop-owl with owl.exe and wintun.dll beside it, registers it
# to start with Windows as LocalSystem, and starts it. Run as an administrator.
#   tools\install-service.ps1            install or update
#   tools\install-service.ps1 -Uninstall stop and remove the service (files stay)
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Dest = Join-Path $env:ProgramFiles 'omdrop-owl'
$Name = 'OmdropOwl'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'run this from an administrator terminal'
}

$existing = Get-Service $Name -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') { Stop-Service $Name; $existing.WaitForStatus('Stopped', '00:00:15') }
}
if ($Uninstall) {
    if ($existing) { sc.exe delete $Name | Out-Null; Write-Host "removed the $Name service" }
    return
}

$owl = Join-Path $Root 'windows\owl\build\owl.exe'
if (-not (Test-Path $owl)) { throw "build owl.exe first: tools\build-owl.ps1" }
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

New-Item -ItemType Directory -Force $Dest | Out-Null
& $dotnet publish (Join-Path $Root 'windows\OmdropService\OmdropService.csproj') -c Release -o $Dest --nologo -v q
if ($LASTEXITCODE) { throw 'publish failed' }
Copy-Item $owl, (Join-Path $Root 'windows\owl\build\wintun.dll') $Dest -Force

# A config left by development may point owl.exe elsewhere; installed, it lives beside the service.
$config = Join-Path $env:ProgramData 'omdrop-owl\config.json'
if (Test-Path $config) {
    $c = Get-Content $config -Raw | ConvertFrom-Json
    if ($c.PSObject.Properties['OwlPath']) { $c.PSObject.Properties.Remove('OwlPath'); $c | ConvertTo-Json | Set-Content $config }
}

$exe = Join-Path $Dest 'OmdropService.exe'
if (-not $existing) {
    New-Service -Name $Name -BinaryPathName "`"$exe`"" -DisplayName 'omdrop-owl (AirDrop radio)' `
        -Description 'Runs owl.exe: AWDL over an ESP32, as the awdl0 adapter AirDrop uses.' -StartupType Automatic | Out-Null
    sc.exe failure $Name reset= 86400 actions= restart/5000/restart/30000 | Out-Null
}
Start-Service $Name
Write-Host "$Name installed in $Dest and started; log: $env:ProgramData\omdrop-owl\service.log"
