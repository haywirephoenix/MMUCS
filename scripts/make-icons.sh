#!/usr/bin/env bash
# Regenerates every app icon from assets/icon.svg. Requires ImageMagick 7 (`magick`) with SVG support.
# Run from anywhere:  bash scripts/make-icons.sh
#
# Outputs (commit them):
#   src/MMUCS.Desktop/Assets/mmucs.ico          Windows exe + window icon (16–256 px)
#   assets/generated/icon-macos-1024.png        macOS master; CI turns it into AppIcon.icns (sips + iconutil)
#   assets/generated/icon-black.svg             black-fill copy for the README in light mode
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
src="$root/assets/icon.svg"
gen="$root/assets/generated"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$gen"

MAGICK="${MAGICK:-magick}"
PLATE="#1d1f24"   # dark rounded-square behind the white artwork

# Artwork rendered large, trimmed to its bounds so it can be centred exactly.
"$MAGICK" -background none -density 400 "$src" -resize 2048x2048 -trim +repage "$tmp/glyph.png"

# Composite: canvas size, plate rectangle (x0,y0,x1,y1,radius), glyph box size.
make_icon() {
  local out=$1 canvas=$2 x0=$3 y0=$4 x1=$5 y1=$6 radius=$7 glyph=$8
  "$MAGICK" -size "${canvas}x${canvas}" xc:none \
    -fill "$PLATE" -draw "roundrectangle $x0,$y0 $x1,$y1 $radius,$radius" \
    \( "$tmp/glyph.png" -resize "${glyph}x${glyph}" \) -gravity center -composite \
    "$out"
}

# macOS: Apple's 1024 grid — 824 px plate inset by 100 px, ~185 px corner radius.
make_icon "$gen/icon-macos-1024.png" 1024 100 100 923 923 185 560

# Windows/Linux: near full-bleed plate so small sizes stay legible.
make_icon "$tmp/icon-win-1024.png" 1024 16 16 1007 1007 180 760
"$MAGICK" "$tmp/icon-win-1024.png" -define icon:auto-resize=256,128,64,48,32,24,16 \
  "$root/src/MMUCS.Desktop/Assets/mmucs.ico"

# README light-mode variant: same artwork, black fill.
sed 's/fill:white/fill:black/g' "$src" > "$gen/icon-black.svg"

echo "Icons written:"
echo "  src/MMUCS.Desktop/Assets/mmucs.ico"
echo "  assets/generated/icon-macos-1024.png"
echo "  assets/generated/icon-black.svg"
