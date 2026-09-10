# HexBridge — установка на Windows.
#
# Запускать от администратора (нужно только для правила брандмауэра):
#   powershell -ExecutionPolicy Bypass -File .\install.ps1
#
# Скрипт идемпотентный: можно запускать повторно после обновления.
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\HexBridge",
    [switch]$NoAutostart,
    [switch]$KeepConsoleTask
)

$ErrorActionPreference = "Stop"
$source = $PSScriptRoot
$taskName = "HexBridge Receiver"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

function Say($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Warn($text) { Write-Host "    $text" -ForegroundColor Yellow }

if (-not (Test-Path (Join-Path $source "HexBridge.exe"))) {
    throw "запускайте скрипт из распакованной папки — рядом должен лежать HexBridge.exe"
}

# ── 1. Остановить всё, что уже работает ───────────────────────────────────────
# Порт может держать и старый консольный приёмник, и предыдущая версия UI.

Say "останавливаю запущенное"

if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($KeepConsoleTask) {
        Warn "задача «$taskName» оставлена, но остановлена (-KeepConsoleTask)"
    } else {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Warn "задача «$taskName» удалена — теперь автозапуском занимается само приложение"
    }
}

Get-Process -Name "HexBridge", "HexBridge.Receiver" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 700

# ── 2. Забрать существующий config.json ───────────────────────────────────────
# Ключ и порт уже настроены и работают — молча затирать их нельзя.

$newConfig = Join-Path $source "config.json"
$oldConfig = $null
foreach ($candidate in @(
    (Join-Path $InstallDir "config.json"),
    "C:\HexBridge-Windows\config.json",
    "C:\HexBridge\config.json"
)) {
    if (Test-Path $candidate) { $oldConfig = $candidate; break }
}

if ($oldConfig) {
    Say "нашёл прежний конфиг: $oldConfig"
    $old = Get-Content $oldConfig -Raw | ConvertFrom-Json
    $new = Get-Content $newConfig -Raw | ConvertFrom-Json
    if ($old.Psk -and $old.Psk -ne $new.Psk) {
        Warn "ключ в прежнем конфиге отличается — оставляю прежний, чтобы не разорвать связь с Mac"
        $new.Psk = $old.Psk
    }
    if ($old.Listen) { $new.Listen = $old.Listen }
    if ($old.Device) { $new.Device = $old.Device }
    $new | ConvertTo-Json | Set-Content $newConfig -Encoding UTF8
}

# ── 3. Скопировать файлы ──────────────────────────────────────────────────────

Say "ставлю в $InstallDir"
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

Get-ChildItem $source -File |
    Where-Object { $_.Name -notin @("install.ps1") } |
    Copy-Item -Destination $InstallDir -Force

$exe = Join-Path $InstallDir "HexBridge.exe"

# ── 4. Брандмауэр ─────────────────────────────────────────────────────────────
# Правило пересоздаётся всегда: если порт в конфиге поменялся, старое открывало бы не тот.

$port = (Get-Content (Join-Path $InstallDir "config.json") -Raw | ConvertFrom-Json).Listen -replace '.*:', ''
if (-not $port) { $port = "47702" }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($admin) {
    Say "открываю UDP/$port в брандмауэре"
    Remove-NetFirewallRule -DisplayName "HexBridge" -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName "HexBridge" -Direction Inbound -Protocol UDP `
        -LocalPort $port -Action Allow -Profile Any | Out-Null
} else {
    Warn "не администратор — правило брандмауэра пропущено."
    Warn "если звук не пойдёт, перезапустите скрипт от администратора."
}

# ── 5. Автозапуск ─────────────────────────────────────────────────────────────
# Ветка Run, а не задача планировщика: приёмник пишет в звуковое устройство
# текущего сеанса, поэтому обязан работать от вошедшего пользователя. UAC не нужен.

if ($NoAutostart) {
    Remove-ItemProperty -Path $runKey -Name "HexBridge" -ErrorAction SilentlyContinue
    Warn "автозапуск не настроен (-NoAutostart)"
} else {
    Say "включаю автозапуск при входе в Windows"
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name "HexBridge" -Value "`"$exe`" --tray"
}

# ── 6. Запустить ──────────────────────────────────────────────────────────────

Say "запускаю"
Start-Process -FilePath $exe -WorkingDirectory $InstallDir

Write-Host ""
Write-Host "Готово." -ForegroundColor Green
Write-Host "  Окно:        значок в трее, двойной клик"
Write-Host "  Закрытие:    окно прячется в трей, выход — через меню значка"
Write-Host "  Автозапуск:  включён, снять можно в Настройках или -NoAutostart"
Write-Host "  Порт:        UDP/$port"
Write-Host ""
Write-Host "В играх и Discord выберите микрофон, который приложение покажет на вкладке «Статус»."
Write-Host ""
Write-Host "Откатиться на консольную версию:" -ForegroundColor DarkGray
Write-Host "  Remove-ItemProperty -Path '$runKey' -Name HexBridge" -ForegroundColor DarkGray
Write-Host "  powershell -ExecutionPolicy Bypass -File `"$InstallDir\install-task.ps1`"" -ForegroundColor DarkGray
