#!/usr/bin/env bash
# Packages a published Linux build as an AppImage and a portable tar.gz.
# Usage: installer/linux/package.sh <version> <rid> <appimage-arch> <publish-dir> <output-dir>
#   e.g. installer/linux/package.sh 1.2.3 linux-x64 x86_64 publish/linux-x64 dist
# The AppImage structure follows the approach of the ED Assistant workflow (same author), see docs/SOURCE_ORIGINALITY.md.
set -euo pipefail

VERSION="$1"; RID="$2"; ARCH="$3"; PUBLISH="$4"; OUT="$5"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
APP_ID="io.github.igorbabayan.Navigator"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

test -x "$PUBLISH/Navigator" || { echo "::error::$PUBLISH/Navigator missing or not executable"; exit 1; }

# ── Portable archive ─────────────────────────────────────────────
PORTABLE="$WORK/Navigator"
cp -a "$PUBLISH/." "$PORTABLE/"
touch "$PORTABLE/.navigator-portable"
chmod +x "$PORTABLE/Navigator"
tar -C "$WORK" -czf "$OUT/Navigator-$VERSION-$RID.tar.gz" Navigator

# ── AppImage ─────────────────────────────────────────────────────
APPDIR="$WORK/Navigator.AppDir"
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps"
cp -a "$PUBLISH/." "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/Navigator"
cp "$ROOT/src/Navigator.App/Assets/navigator-256.png" "$APPDIR/$APP_ID.png"
cp "$ROOT/src/Navigator.App/Assets/navigator-256.png" "$APPDIR/.DirIcon"
cp "$ROOT/src/Navigator.App/Assets/navigator-256.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/$APP_ID.png"
sed "s/@VERSION@/$VERSION/" "$HERE/$APP_ID.desktop" > "$APPDIR/$APP_ID.desktop"
cp "$APPDIR/$APP_ID.desktop" "$APPDIR/usr/share/applications/"

cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/Navigator" "$@"
EOF
chmod +x "$APPDIR/AppRun"

TOOL="$WORK/appimagetool"
curl -fsSL -o "$TOOL" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$ARCH.AppImage"
# The upstream "continuous" tag is rebuilt in place; pin the binary by hash via a repository variable.
if [ -n "${APPIMAGETOOL_SHA256:-}" ]; then
  echo "$APPIMAGETOOL_SHA256  $TOOL" | sha256sum -c -
else
  echo "::warning::APPIMAGETOOL_SHA256 is not set; appimagetool was not verified by hash (docs/RELEASE.md)."
fi
chmod +x "$TOOL"
export ARCH VERSION
"$TOOL" --appimage-extract-and-run --no-appstream "$APPDIR" "$OUT/Navigator-$VERSION-$RID.AppImage"
chmod +x "$OUT/Navigator-$VERSION-$RID.AppImage"

ls -la "$OUT"
