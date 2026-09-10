#!/bin/bash
# Signs a release archive and writes the Sparkle appcast for it.
#
#   make-appcast.sh <version> <zip> <download-url> [output]
#
# The appcast has exactly one item — the release being published. That is not a
# simplification, it is how the feed is hosted: `SUFeedURL` points at
# `releases/latest/download/appcast.xml`, and GitHub resolves that to the newest
# release's asset. Each release therefore carries its own feed, the URL never
# changes, and there is no file anywhere that has to be edited by hand and
# re-signed on every release.
#
# The private key is read from $SPARKLE_PRIVATE_KEY (the CI secret) or from
# secrets/sparkle_ed25519_private_key (the release machine). It is never written
# anywhere this script can be asked to publish. See docs/UPDATES.md.
set -euo pipefail

VERSION="${1:?укажите версию, например 1.2.0}"
ARCHIVE="${2:?укажите zip со сборкой}"
URL="${3:?укажите URL, по которому zip будет лежать}"
OUTPUT="${4:-appcast.xml}"

# Resolve the caller's paths before moving: the script cd's into the package to
# find sign_update, and a relative archive path handed in from the repository
# root would silently stop existing after that. CI passed exactly such a path and
# the release failed with "no such file" on a file that was right there.
abspath() {
    case "$1" in
        /*) printf '%s\n' "$1" ;;
        *)  printf '%s/%s\n' "$(pwd)" "$1" ;;
    esac
}
ARCHIVE="$(abspath "$ARCHIVE")"
OUTPUT="$(abspath "$OUTPUT")"

cd "$(dirname "$0")/.."

SIGN_UPDATE="$(find .build/artifacts -type f -name sign_update -perm -u+x | head -1)"
if [ -z "$SIGN_UPDATE" ]; then
    echo "sign_update не найден — сначала соберите пакет: swift build -c release" >&2
    exit 1
fi

KEY_FILE="$(mktemp)"
# The key file is removed whatever happens, including on the `set -e` path: a
# private key left in a temp directory on a shared CI runner is the one mistake
# in this file that cannot be undone afterwards.
trap 'rm -f "$KEY_FILE"' EXIT

if [ -n "${SPARKLE_PRIVATE_KEY:-}" ]; then
    printf '%s\n' "$SPARKLE_PRIVATE_KEY" > "$KEY_FILE"
elif [ -f ../secrets/sparkle_ed25519_private_key ]; then
    cat ../secrets/sparkle_ed25519_private_key > "$KEY_FILE"
else
    echo "нет ключа подписи: задайте SPARKLE_PRIVATE_KEY или положите secrets/sparkle_ed25519_private_key" >&2
    echo "как — написано в docs/UPDATES.md" >&2
    exit 1
fi
chmod 600 "$KEY_FILE"

SIGNATURE="$("$SIGN_UPDATE" --ed-key-file "$KEY_FILE" -p "$ARCHIVE")"
LENGTH="$(wc -c < "$ARCHIVE" | tr -d ' ')"
PUBDATE="$(LC_ALL=C date -u '+%a, %d %b %Y %H:%M:%S +0000')"

cat > "$OUTPUT" <<XML
<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
  <channel>
    <title>HexBridge</title>
    <link>https://github.com/HexArchy/hexbridge</link>
    <description>Обновления HexBridge для macOS</description>
    <language>ru</language>
    <item>
      <title>HexBridge $VERSION</title>
      <pubDate>$PUBDATE</pubDate>
      <sparkle:version>$VERSION</sparkle:version>
      <sparkle:shortVersionString>$VERSION</sparkle:shortVersionString>
      <sparkle:minimumSystemVersion>14.0</sparkle:minimumSystemVersion>
      <link>https://github.com/HexArchy/hexbridge/releases/tag/v$VERSION</link>
      <description><![CDATA[
        <h2>HexBridge $VERSION</h2>
        <p>Что изменилось — на странице релиза:
           <a href="https://github.com/HexArchy/hexbridge/releases/tag/v$VERSION">v$VERSION</a>.</p>
        <p><b>После установки macOS ещё раз спросит доступ к микрофону.</b>
           Это нормально и не значит, что что-то сломалось: сборка подписана ad-hoc,
           её хеш меняется с каждой версией, и система считает обновлённое приложение
           новым. Нажмите «Разрешить» — адрес ПК и ключ связывания останутся на месте.</p>
      ]]></description>
      <enclosure url="$URL"
                 sparkle:edSignature="$SIGNATURE"
                 length="$LENGTH"
                 type="application/octet-stream" />
    </item>
  </channel>
</rss>
XML

echo "готово: $OUTPUT"
echo "  версия   $VERSION"
echo "  архив    $ARCHIVE ($LENGTH байт)"
echo "  подпись  ${SIGNATURE:0:24}…"
