#!/usr/bin/env bash
# Builds Navigator.app, signs it (Developer ID when configured, ad-hoc otherwise), notarizes when Apple
# credentials are present, and produces the updater ZIP (ditto keeps symlinks/permissions/signatures) and a DMG.
# Usage: installer/macos/package.sh <version> <rid> <publish-dir> <output-dir>
set -euo pipefail

VERSION="$1"; RID="$2"; PUBLISH="$3"; OUT="$4"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"; security delete-keychain "$WORK/build.keychain" 2>/dev/null || true' EXIT

APP="$WORK/Navigator.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH/." "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/Navigator"
sed -e "s/@VERSION@/$VERSION/g" "$HERE/Info.plist" > "$APP/Contents/Info.plist"

# Icon: PNG -> .icns
ICONSET="$WORK/AppIcon.iconset"
mkdir -p "$ICONSET"
SRC="$ROOT/assets/branding/navigator-512.png"
for s in 16 32 128 256 512; do
  sips -z $s $s "$SRC" --out "$ICONSET/icon_${s}x${s}.png" > /dev/null
  d=$((s * 2)); [ $d -le 512 ] && sips -z $d $d "$SRC" --out "$ICONSET/icon_${s}x${s}@2x.png" > /dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

# Signing
IDENTITY="-"
if [ -n "${MACOS_CERTIFICATE:-}" ] && [ -n "${MACOS_SIGNING_IDENTITY:-}" ]; then
  KEYCHAIN="$WORK/build.keychain"
  KEYCHAIN_PASSWORD="$(uuidgen)"
  echo "$MACOS_CERTIFICATE" | base64 --decode > "$WORK/cert.p12"
  security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
  security set-keychain-settings -lut 3600 "$KEYCHAIN"
  security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
  security import "$WORK/cert.p12" -k "$KEYCHAIN" -P "${MACOS_CERTIFICATE_PASSWORD:-}" -T /usr/bin/codesign
  security set-key-partition-list -S apple-tool:,apple: -s -k "$KEYCHAIN_PASSWORD" "$KEYCHAIN" > /dev/null
  existing=()
  while IFS= read -r keychain; do existing+=("$keychain"); done < <(security list-keychains -d user | tr -d '"' | sed 's/^ *//')
  security list-keychains -d user -s "$KEYCHAIN" "${existing[@]}"
  IDENTITY="$MACOS_SIGNING_IDENTITY"
fi

if [ "$IDENTITY" = "-" ]; then
  echo "::warning::No Developer ID configured: ad-hoc signature (development build, Gatekeeper will warn)."
  codesign --force --deep --sign - "$APP"
else
  codesign --force --deep --options runtime --timestamp --entitlements "$HERE/Navigator.entitlements" --sign "$IDENTITY" "$APP"
fi
codesign --verify --deep --strict "$APP"

if [ "$IDENTITY" != "-" ] && [ -n "${APPLE_ID:-}" ] && [ -n "${APPLE_TEAM_ID:-}" ] && [ -n "${APPLE_APP_PASSWORD:-}" ]; then
  ditto -c -k --keepParent "$APP" "$WORK/notarize.zip"
  xcrun notarytool submit "$WORK/notarize.zip" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD" --wait
  xcrun stapler staple "$APP"
fi

# Updater archive (ZIP made by ditto preserves the bundle exactly)
ditto -c -k --sequesterRsrc --keepParent "$APP" "$OUT/Navigator-$VERSION-$RID.zip"

# DMG with an /Applications shortcut
STAGING="$WORK/dmg"
mkdir -p "$STAGING"
cp -R "$APP" "$STAGING/"
ln -s /Applications "$STAGING/Applications"
for attempt in 1 2 3; do
  hdiutil create -volname "Navigator" -srcfolder "$STAGING" -ov -format UDZO "$OUT/Navigator-$VERSION-$RID.dmg" && break
  echo "hdiutil failed, retrying ($attempt)"; sleep 5
done
test -f "$OUT/Navigator-$VERSION-$RID.dmg"
if [ "$IDENTITY" != "-" ]; then codesign --sign "$IDENTITY" --timestamp "$OUT/Navigator-$VERSION-$RID.dmg"; fi

ls -la "$OUT"
