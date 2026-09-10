# Registers HexBridge.Receiver.exe as a scheduled task that starts at logon.
#
# A scheduled task is deliberate rather than a Windows service: the receiver
# renders into a per-session audio endpoint, and a session-0 service cannot see
# the user's audio devices.
#
#   powershell -ExecutionPolicy Bypass -File install-task.ps1 -InstallDir C:\HexBridge
param(
    [string]$InstallDir = $PSScriptRoot,
    [string]$TaskName = "HexBridge Receiver"
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $InstallDir "HexBridge.Receiver.exe"
if (-not (Test-Path $exe)) {
    throw "не найден $exe — скопируйте туда содержимое publish\win-x64"
}

$config = Join-Path $InstallDir "config.json"
if (-not (Test-Path $config)) {
    throw "не найден $config — создайте его по образцу из README"
}

# Opening the inbound UDP port is what lets the Mac reach us directly.
$port = (Get-Content $config -Raw | ConvertFrom-Json).Listen -replace '.*:', ''
if (-not $port) { $port = "47702" }

# Always recreate the rule: if the port in config.json changed, a stale rule
# would still be opening the old one.
Write-Host "==> открываю UDP/$port в брандмауэре"
Remove-NetFirewallRule -DisplayName "HexBridge" -ErrorAction SilentlyContinue
New-NetFirewallRule -DisplayName "HexBridge" -Direction Inbound -Protocol UDP `
    -LocalPort $port -Action Allow -Profile Any | Out-Null

Write-Host "==> регистрирую задачу «$TaskName»"
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $InstallDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1)
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Settings $settings -Principal $principal | Out-Null

Start-ScheduledTask -TaskName $TaskName

Write-Host "готово."
Write-Host "проверить устройства: & '$exe' list-devices"
Write-Host "остановить:          Stop-ScheduledTask -TaskName '$TaskName'"
