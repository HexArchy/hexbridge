#!/bin/bash
# Builds HexBridge.app.
#
# The binary has to live in an app bundle: under launchd, TCC attributes the
# microphone request to the bundle, and without NSMicrophoneUsageDescription the
# request is denied outright with no prompt.
set -euo pipefail

cd "$(dirname "$0")/.."

APP="${1:-build/HexBridge.app}"
# Переопределяется только для отладочной копии, которую надо запустить рядом с
# установленным агентом: идентификатор бандла — это и то, по чему приложение
# снимает свои прежние экземпляры, и то, к чему привязано разрешение на
# микрофон. Копия с другим идентификатором не трогает ни то, ни другое.
IDENTIFIER="${HEXBRIDGE_IDENTIFIER:-ru.hexarch.hexbridge}"
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
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Frameworks" "$APP/Contents/Resources"

cp .build/release/HexBridge "$APP/Contents/MacOS/HexBridge"

# Обе языковые таблицы — в Contents/Resources, туда, где их кладёт любое другое
# приложение macOS и где Bundle.main находит их само.
#
# Не в виде SwiftPM-бандла: аксессор Bundle.module ищет ресурсы рядом с
# исполняемым файлом и в каталоге сборки пакета, а Contents/Resources не является
# ни тем, ни другим — и при неудаче он не возвращает nil, а убивает процесс.
# Поэтому строки едут отдельно, а HexBridgeText ищет их сам (StringsBundle).
for LPROJ in Sources/HexBridgeText/Resources/*.lproj; do
    cp -R "$LPROJ" "$APP/Contents/Resources/"
done
echo "==> строки: $(ls -d "$APP"/Contents/Resources/*.lproj | xargs -n1 basename | tr '\n' ' ')"

# Текст запроса на микрофон показывает система, а не мы, и берёт она его по
# языку macOS, а не по выбранному в приложении. Английский — в Info.plist,
# русский — здесь; переключатель в настройках на этот диалог не влияет, и это
# ровно то поведение, которого пользователь от системного диалога ждёт.
cat > "$APP/Contents/Resources/ru.lproj/InfoPlist.strings" <<'PLIST_STRINGS'
"NSMicrophoneUsageDescription" = "HexBridge передаёт звук микрофона на игровой ПК во время стрима.";
PLIST_STRINGS
cat > "$APP/Contents/Resources/en.lproj/InfoPlist.strings" <<'PLIST_STRINGS'
"NSMicrophoneUsageDescription" = "HexBridge sends microphone audio to the gaming PC while you stream.";
PLIST_STRINGS

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
    <!-- Интерфейс двуязычный: английский по умолчанию, русский вторым. Сами
         строки лежат в Contents/Resources/<lang>.lproj/Localizable.strings, а
         выбор языка — в конфиге приложения; здесь только то, что нужно знать
         системе. -->
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleLocalizations</key>
    <array>
        <string>en</string>
        <string>ru</string>
    </array>
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
    <string>HexBridge sends microphone audio to the gaming PC while you stream.</string>
    <!-- Послаблений ATS здесь нет и не требуется: обмен коротким кодом ходит по
         сокету (Core/Pairing.swift), а URLSession в приложении не пользуется никто,
         кроме Sparkle, и тот ходит по HTTPS. -->
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
# Стабильная подпись, если она есть.
#
# macOS привязывает выданное разрешение на микрофон не только к идентификатору
# бандла, но и к подписи. Ad-hoc подпись содержит хеш самого кода, поэтому меняется
# с каждой сборкой, и разрешение приходится выдавать заново после каждого
# обновления. Самоподписанный сертификат из связки ключей эту привязку
# стабилизирует. Как его завести — в docs/UPDATES.md.
SIGN_ID="${CODESIGN_IDENTITY:--}"
if [ "$SIGN_ID" != "-" ]; then
    echo "==> подписываю личностью $SIGN_ID"
else
    echo "==> подписываю ad-hoc (разрешения слетят при следующей сборке)"
fi

if [ -d "$APP/Contents/Frameworks/Sparkle.framework" ]; then
    # Sparkle carries two XPC services and an updater app of its own. Each is
    # code in its own right and each has to be signed, or Gatekeeper refuses the
    # bundle as a whole.
    find "$APP/Contents/Frameworks/Sparkle.framework" \
        \( -name "*.xpc" -o -name "*.app" \) -print0 |
        while IFS= read -r -d "" nested; do
            codesign --force --sign "$SIGN_ID" --timestamp=none "$nested"
        done
    codesign --force --sign "$SIGN_ID" --timestamp=none "$APP/Contents/Frameworks/Sparkle.framework"
fi
codesign --force --sign "$SIGN_ID" --identifier "$IDENTIFIER" "$APP"

echo "готово: $APP"
echo "проверка: $APP/Contents/MacOS/HexBridge probe"
