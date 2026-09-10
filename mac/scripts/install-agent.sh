#!/bin/bash
# Installs HexBridge.app into /Applications and starts it at login.
set -euo pipefail

cd "$(dirname "$0")/.."

LABEL="ru.hexarch.hexbridge"
APP="/Applications/HexBridge.app"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
LOG="$HOME/Library/Logs/HexBridge.log"

if [ ! -d build/HexBridge.app ]; then
    echo "сначала соберите: scripts/build-app.sh" >&2
    exit 1
fi

CONFIG="$HOME/Library/Application Support/HexBridge/config.json"
if [ ! -f "$CONFIG" ]; then
    echo "нет конфига: $CONFIG" >&2
    echo "создайте его: HexBridge init --target ХОСТ:47702 --psk КЛЮЧ" >&2
    exit 1
fi

echo "==> ставлю $APP"
rm -rf "$APP"
cp -R build/HexBridge.app "$APP"

mkdir -p "$(dirname "$PLIST")"
cat > "$PLIST" <<PLIST_EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>$LABEL</string>
    <key>ProgramArguments</key>
    <array>
        <string>$APP/Contents/MacOS/HexBridge</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>ProcessType</key>
    <string>Interactive</string>
    <key>StandardOutPath</key>
    <string>$LOG</string>
    <key>StandardErrorPath</key>
    <string>$LOG</string>
</dict>
</plist>
PLIST_EOF

echo "==> перезапускаю агент"
launchctl bootout "gui/$UID/$LABEL" 2>/dev/null || true
launchctl bootstrap "gui/$UID" "$PLIST"

echo "готово. Логи: $LOG"
echo "мьют:      kill -USR1 \$(pgrep -f HexBridge.app)"
echo "остановить: launchctl bootout gui/$UID/$LABEL"
