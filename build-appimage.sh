#!/usr/bin/env bash
set -euo pipefail

# G-Helper Linux — AppImage Builder
# Packages the dist/ output into a portable AppImage.
#
# Prerequisites:
#   ./build.sh          (produces dist/ghelper — single binary with embedded native libs)
#
# Usage:
#   ./build-appimage.sh
#
# Output:
#   GHelper-N6506MV-x86_64.AppImage

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIST_DIR="${GHELPER_DIST_DIR:-$SCRIPT_DIR/dist}"
INSTALL_DIR="$SCRIPT_DIR/install"
APPDIR="$SCRIPT_DIR/GHelper.AppDir"
OUTPUT="$SCRIPT_DIR/GHelper-N6506MV-x86_64.AppImage"
APPIMAGETOOL="$SCRIPT_DIR/appimagetool"
BUILD_OUTPUT="$OUTPUT.build-$$"
trap 'rm -f "$BUILD_OUTPUT"' EXIT

echo "=== G-Helper AppImage Build ==="
echo ""

# ── Verify dist/ exists ───────────────────────────────────────────────────────

if [[ ! -f "$DIST_DIR/ghelper" ]]; then
    echo "ERROR: dist/ghelper not found."
    echo "Run ./build.sh first to compile the binary."
    exit 1
fi

# ── Download appimagetool if needed ───────────────────────────────────────────

if [[ ! -x "$APPIMAGETOOL" ]]; then
    echo "[1/4] Downloading appimagetool..."
    curl -fsSL "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage" \
        -o "$APPIMAGETOOL"
    chmod +x "$APPIMAGETOOL"
else
    echo "[1/4] appimagetool already present."
fi

# ── Assemble AppDir ──────────────────────────────────────────────────────────

echo "[2/4] Assembling AppDir..."

rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin"
mkdir -p "$APPDIR/usr/lib"

# Binary only — native .so libs are embedded and extracted at runtime
cp "$DIST_DIR/ghelper"             "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/ghelper"

# Desktop entry (required by appimagetool at AppDir root + standard path for validation)
cp "$INSTALL_DIR/ghelper.desktop" "$APPDIR/ghelper.desktop"
mkdir -p "$APPDIR/usr/share/applications"
cp "$INSTALL_DIR/ghelper.desktop" "$APPDIR/usr/share/applications/"

# Icon (required by appimagetool at AppDir root, matching Icon= in .desktop)
if [[ -f "$INSTALL_DIR/ghelper.png" ]]; then
    cp "$INSTALL_DIR/ghelper.png" "$APPDIR/ghelper.png"
else
    echo "WARNING: install/ghelper.png not found. Generating from favicon.ico..."
    if command -v convert &>/dev/null; then
        convert "$SCRIPT_DIR/src/UI/Assets/favicon.ico[2]" "$APPDIR/ghelper.png"
    else
        echo "ERROR: ImageMagick not found and install/ghelper.png missing."
        echo "Install ImageMagick or create install/ghelper.png manually."
        exit 1
    fi
fi

# AppStream metadata (suppresses appimagetool warning)
mkdir -p "$APPDIR/usr/share/metainfo"
cp "$INSTALL_DIR/ghelper.appdata.xml" "$APPDIR/usr/share/metainfo/"

# AppRun — entry point that appimagetool expects
cat > "$APPDIR/AppRun" << 'APPRUN'
#!/bin/bash
HERE="$(dirname "$(readlink -f "$0")")"
case "${1:-}" in
    --version|--extract-helper|--install-gpu-helper|--apply-system-files|--remove-system-files)
        exec "$HERE/usr/bin/ghelper" "$@" ;;
    *) exec "$HERE/usr/bin/ghelper" --install-appimage "$@" ;;
esac
APPRUN
chmod +x "$APPDIR/AppRun"

# ── Build AppImage ────────────────────────────────────────────────────────────

echo "[3/4] Building AppImage..."

# ARCH must be set for appimagetool
export ARCH=x86_64

# appimagetool itself is an AppImage — if FUSE is not available, extract and run
if "$APPIMAGETOOL" --version &>/dev/null 2>&1; then
    "$APPIMAGETOOL" --no-appstream "$APPDIR" "$BUILD_OUTPUT"
else
    echo "  (FUSE not available, using --appimage-extract-and-run)"
    "$APPIMAGETOOL" --no-appstream --appimage-extract-and-run "$APPDIR" "$BUILD_OUTPUT"
fi

mv -f "$BUILD_OUTPUT" "$OUTPUT"

# ── Cleanup ───────────────────────────────────────────────────────────────────

echo "[4/4] Cleaning up..."
rm -rf "$APPDIR"

# ── Summary ───────────────────────────────────────────────────────────────────

APPIMAGE_SIZE=$(du -sh "$OUTPUT" | cut -f1)
APPIMAGE_NAME=$(basename "$OUTPUT")
echo ""
echo "=== AppImage Build Complete ==="
echo "  Output: $OUTPUT"
echo "  Size:   $APPIMAGE_SIZE"
echo ""
echo "Run it:"
echo "  chmod +x $APPIMAGE_NAME"
echo "  ./$APPIMAGE_NAME"
echo ""
echo "First launch installs to ~/Applications and adds the application menu entry."
echo "The app prompts for any required hardware helper setup."
