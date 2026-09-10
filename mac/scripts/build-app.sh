#!/bin/bash
# Builds HexBridge.app.
#
# The binary has to live in an app bundle: under launchd, TCC attributes the
# microphone request to the bundle, and without NSMicrophoneUsageDescription the
# request is denied outright with no prompt.
set -euo pipefail

cd "$(dirname "$0")/.."

APP="${1:-build/HexBridge.app}"
IDENTIFIER="ru.hexarch.hexbridge"
VERSION="1.0.0"

echo "==> swift build"
swift build -c release

echo "==> собираю $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"

cp .build/release/HexBridge "$APP/Contents/MacOS/HexBridge"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>HexBridge</string>
    <key>CFBundleIdentifier</key>
    <string>$IDENTIFIER</string>
    <key>CFBundleName</key>
    <string>HexBridge</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSMicrophoneUsageDescription</key>
    <string>HexBridge передаёт звук микрофона на игровой ПК во время стрима.</string>
    <!-- Связывание машин: приёмник на Windows отдаёт адрес и ключ одной
         ссылкой, macOS доставляет её сюда через схему URL. -->
    <key>CFBundleURLTypes</key>
    <array>
        <dict>
            <key>CFBundleURLName</key>
            <string>$IDENTIFIER.pair</string>
            <key>CFBundleTypeRole</key>
            <string>Viewer</string>
            <key>CFBundleURLSchemes</key>
            <array>
                <string>hexbridge</string>
            </array>
        </dict>
    </array>
</dict>
</plist>
PLIST

# Ad-hoc signing is enough for TCC to keep a stable identity between launches.
# The hash changes on every rebuild, so macOS asks for microphone access again
# after an update — that is expected.
echo "==> подписываю"
codesign --force --sign - --identifier "$IDENTIFIER" "$APP"

echo "готово: $APP"
echo "проверка: $APP/Contents/MacOS/HexBridge probe"
