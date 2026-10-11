# One AirDrop test run: owl.exe in its own administrator window (Wintun needs
# it; Windows asks once), which first adds omdrop's firewall rule, and here
# omdrop.exe receive, or send with -Send. Both log to %TEMP%\omdrop-test,
# with every packet over awdl0 in OWL's log.
#   tools\airdrop-test.ps1 [-Port COM9] [-Channel 6] [-Send FILE [-To NAME]]
# Stop: Ctrl-C here, then Ctrl-C in the OWL window (that removes awdl0).
param([string]$Port = 'COM9', [int]$Channel = 6, [string]$Send, [string]$To)
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Logs = Join-Path $env:TEMP 'omdrop-test'
New-Item -ItemType Directory -Force $Logs | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$owlLog = Join-Path $Logs "owl-$stamp.log"
$omdropLog = Join-Path $Logs "omdrop-$stamp.log"

$owl = Join-Path $Root 'windows\owl\build\owl.exe'
$omdrop = Join-Path $Root 'windows\omdrop\bin\Release\omdrop.exe'
foreach ($exe in $owl, $omdrop) {
    if (-not (Test-Path $exe)) { throw "$exe isn't built (tools\build-owl.ps1; dotnet build windows\omdrop -c Release)" }
}

$owlCmd = "& '$omdrop' firewall; `$env:OWL_LOG_FILE='$owlLog'; `$env:OWL_PKTLOG='1'; " +
          "& '$owl' -i $Port -c $Channel -N -F -E -S pin -T -v"
Start-Process pwsh -Verb RunAs -ArgumentList '-NoExit', '-Command', $owlCmd
Write-Host "OWL log:    $owlLog"
Write-Host "omdrop log: $omdropLog"
if ($Send) {
    $sendArgs = @('send', (Resolve-Path $Send).Path, '--timeout', '90', '-v')
    if ($To) { $sendArgs += @('--to', $To) }
    & $omdrop @sendArgs 2>&1 | Tee-Object -FilePath $omdropLog
} else {
    & $omdrop receive -v 2>&1 | Tee-Object -FilePath $omdropLog
}
