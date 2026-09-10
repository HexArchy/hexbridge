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
# CFBundleShortVersionString is what the user reads; CFBundleVersion is what
# Sparkle compares, and it has to increase with every release or an update is
# offered to nobody. A tag drives both in CI (see .github/workflows/release.yml).
VERSION="${HEXBRIDGE_VERSION:-1.0.0}"
BUILD="${HEXBRIDGE_BUILD:-$VERSION}"

# The feed the built app will look at, and the public half of the key its
# updates are signed with. The private half is never here — docs/UPDATES.md.
FEED_URL="${SPARKLE_FEED_URL:-https://github.com/HexArchy/hexbridge/releases/latest/download/appcast.xml}"
SPARKLE_PUBLIC_KEY="${SPARKLE_PUBLIC_KEY:-dCeQPn6bc2CeCgLpffrSITwjLydswffxxHQGvjPbnUI=}"

echo "==> swift build"
# --disable-keychain --disable-netrc: every dependency is public, so there is
# nothing to authenticate. Left to itself SwiftPM asks the Keychain for
# credentials for the artifact host, and on a machine where the Keychain cannot
# prompt — a CI runner, an ssh session, a locked screen — that call blocks
# forever with no output at all. Ten minutes of a build that has not started.
swift build -c release --disable-keychain --disable-netrc

echo "==> собираю $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Frameworks"

cp .build/release/HexBridge "$APP/Contents/MacOS/HexBridge"

# Sparkle is a framework with XPC services nested inside it; it has to travel in
# the bundle, and the binary has to be able to find it there.
SPARKLE_FRAMEWORK="$(find .build/artifacts -type d -name "Sparkle.framework" -path "*macos*" | head -1)"
if [ -z "$SPARKLE_FRAMEWORK" ]; then
    echo "!! Sparkle.framework не найден в .build/artifacts — обновления в этой сборке работать не будут" >&2
else
    cp -R "$SPARKLE_FRAMEWORK" "$APP/Contents/Frameworks/"
    install_name_tool -add_rpath "@executable_path/../Frameworks" "$APP/Contents/MacOS/HexBridge" 2>/dev/null || true
fi

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
    <string>$BUILD</string>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSMicrophoneUsageDescription</key>
    <string>HexBridge передаёт звук микрофона на игровой ПК во время стрима.</string>
    <!-- Обновления (docs/UPDATES.md). SUPublicEDKey — публичная половина ключа
         подписи: Sparkle проверяет EdDSA-подпись архива сама, поверх подписи кода,
         поэтому ad-hoc сборка обновляется без сертификата Developer ID.
         SUEnableAutomaticChecks — только значение по умолчанию: настоящий
         выключатель живёт в настройках приложения и переписывает его. -->
    <key>SUFeedURL</key>
    <string>$FEED_URL</string>
    <key>SUPublicEDKey</key>
    <string>$SPARKLE_PUBLIC_KEY</string>
    <key>SUEnableAutomaticChecks</key>
    <true/>
    <key>SUAutomaticallyUpdate</key>
    <false/>
    <key>SUScheduledCheckInterval</key>
    <integer>86400</integer>
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
# Nested code first, outside-in last: a signature over the bundle is a signature
# over the hashes of what is inside it, so re-signing the framework afterwards
# would invalidate the one on the app.
echo "==> подписываю"
if [ -d "$APP/Contents/Frameworks/Sparkle.framework" ]; then
    # Sparkle carries two XPC services and an updater app of its own. Each is
    # code in its own right and each has to be signed, or Gatekeeper refuses the
    # bundle as a whole.
    find "$APP/Contents/Frameworks/Sparkle.framework" \
        \( -name "*.xpc" -o -name "*.app" \) -print0 |
        while IFS= read -r -d "" nested; do
            codesign --force --sign - --timestamp=none "$nested"
        done
    codesign --force --sign - --timestamp=none "$APP/Contents/Frameworks/Sparkle.framework"
fi
codesign --force --sign - --identifier "$IDENTIFIER" "$APP"

echo "готово: $APP"
echo "проверка: $APP/Contents/MacOS/HexBridge probe"
