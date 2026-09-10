HexBridge — Mac microphone on this PC
=====================================

The key and the address are already written into config.json. You do not need
to install .NET; the exe files are self-contained.

INSTALL — ONE COMMAND
---------------------
Unpack the folder anywhere (Downloads is fine — the script moves the files to a
permanent location itself) and run PowerShell AS ADMINISTRATOR:

    powershell -ExecutionPolicy Bypass -File .\install.ps1

The script does everything:
  * stops and removes the old "HexBridge Receiver" task
  * kills running processes to free up port 47702
  * picks up a previous config.json if there was one and keeps the key from it
  * copies the files to %LOCALAPPDATA%\HexBridge
  * recreates the firewall rule for UDP/47702
  * enables start with Windows at sign-in
  * launches the app

Administrator rights are needed for exactly one thing: the firewall rule.
Without them the script still runs, but it will warn you.

WHAT YOU GET
------------
A tray icon. Double-click it for the window.

  Status    - whether audio is flowing, level, packets/s, latency, which
              device is selected and which microphone to pick in games
  Quality   - buffer, packet loss, graphs for the last minute
  Settings  - port, device, buffer, gain, key, start with Windows, theme
  Log       - what is happening

Closing the window with the X does NOT stop reception - the window goes to the
tray. To quit for real: right-click the tray icon -> Quit.

IN GAMES AND DISCORD
--------------------
Select the microphone the app shows on the Status tab, in the "select this in
games" line. It is usually "Microphone (Steam Streaming Microphone)" or
"CABLE Output (VB-Audio Virtual Cable)", depending on which virtual cable was
found.

IF SOMETHING GOES WRONG
-----------------------
Stop starting with Windows:
    Clear the checkbox in Settings, or:
    Remove-ItemProperty -Path HKCU:\Software\Microsoft\Windows\CurrentVersion\Run -Name HexBridge

Go back to the old console version:
    powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\HexBridge\install-task.ps1"
    (HexBridge.Receiver.exe sits right there and has not gone anywhere)

See what the system sees:
    & "$env:LOCALAPPDATA\HexBridge\HexBridge.Receiver.exe" list-devices

Check reception without a sound card:
    & "$env:LOCALAPPDATA\HexBridge\HexBridge.Receiver.exe" --output wav:C:\temp\mic.wav

Install without starting with Windows:
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -NoAutostart

Install but keep the old scheduled task:
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -KeepConsoleTask
