#!/usr/bin/env bash
# Builds Blish HUD on Linux (for running under Wine/Proton next to Guild Wars 2).
#
#   ./build-linux.sh [Debug|Release]
#
# The game itself is a Windows program, so only the MonoGame *content* build needs help from Linux: the shader compiler
# (mgfxc) is a Windows tool and is run through Wine.  Prerequisites:
#   - .NET SDK (any recent one) and the .NET Framework 4.7.2 targeting pack
#   - wine
#   - a Wine prefix for the shader compiler containing d3dcompiler_47 and the Windows .NET Core 3.1 runtime
#     (default: ~/.cache/bh/wp-mgfxc, override with BLISH_MGFXC_PREFIX)
#
# Output goes to BLISH_OUT (default ~/.cache/bh/runclean).  Keep it short: the shader tooling breaks on paths over 260 characters.
set -euo pipefail

CONFIG=${1:-Debug}
REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
PREFIX=${BLISH_MGFXC_PREFIX:-$HOME/.cache/bh/wp-mgfxc}
OUT=${BLISH_OUT:-$HOME/.cache/bh/runclean}

command -v dotnet >/dev/null || { echo "dotnet SDK not found" >&2; exit 1; }
WINE=$(command -v wine) || { echo "wine not found" >&2; exit 1; }
[ -d "$PREFIX/drive_c" ] || { echo "Wine prefix for the shader compiler not found at $PREFIX (set BLISH_MGFXC_PREFIX); see the notes at the top of this script." >&2; exit 1; }

# The content builder looks for a binary called wine64.
SHIM=$(mktemp -d)
trap 'rm -rf "$SHIM"' EXIT
printf '#!/bin/sh\nexec %s "$@"\n' "$WINE" > "$SHIM/wine64"
chmod +x "$SHIM/wine64"

# Submodule used by the build.
[ -e "$REPO/thirdparty/nvidiaProfileInspector/.git" ] || [ -n "$(ls -A "$REPO/thirdparty/nvidiaProfileInspector" 2>/dev/null)" ] \
    || git -C "$REPO" submodule update --init --recursive

# Consolas is not redistributable.  If it's missing, use DejaVu Sans Mono as a stand-in so the content build works
# (local only; the copies are git-ignored).
FONTS="$REPO/Blish HUD/Content/fonts"
if [ ! -f "$FONTS/Consolas.ttf" ] || [ ! -f "$FONTS/Consolas Bold.ttf" ]; then
    echo "Consolas not found; using DejaVu Sans Mono as a stand-in."
    cp "$(fc-match -f '%{file}' 'DejaVu Sans Mono')"      "$FONTS/Consolas.ttf"
    cp "$(fc-match -f '%{file}' 'DejaVu Sans Mono:bold')" "$FONTS/Consolas Bold.ttf"
    for f in "Consolas.ttf" "Consolas Bold.ttf"; do
        grep -qxF "Blish HUD/Content/fonts/$f" "$REPO/.git/info/exclude" 2>/dev/null || echo "Blish HUD/Content/fonts/$f" >> "$REPO/.git/info/exclude"
    done
fi

# A zero-byte config left by an interrupted build breaks the next one.
find "$REPO/Blish HUD/obj" -name '*.config' -size 0 -delete 2>/dev/null || true

mkdir -p "$OUT"
DOTNET_ROLL_FORWARD=Major \
DOTNET_CLI_TELEMETRY_OPTOUT=1 \
WINEPREFIX="$PREFIX" \
WINEDEBUG=-all \
MGFXC_WINE_PATH="$PREFIX" \
PATH="$SHIM:$PATH" \
    dotnet build "$REPO/Blish HUD/Blish HUD.csproj" -c "$CONFIG" -p:OutDir="$OUT/"

echo
echo "Built: $OUT/Blish HUD.exe"
