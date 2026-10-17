#!/bin/bash
# Developer script (macOS): builds the mod from source and installs it, plus BepInEx if missing, into your
# Cuphead install. Players should use the one-line installers from the README instead.
# Expects this repository to be cloned inside the Cuphead folder (next to Cuphead.app), or GAME_DIR to be set.
# Usage: scripts/dev-install.sh [path/to/BepInEx_macos_*.zip]
set -euo pipefail

MOD_DIR="$(cd "$(dirname "$0")/.." && pwd)"
GAME_DIR="${GAME_DIR:-$(cd "$MOD_DIR/.." && pwd)}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"

if [ ! -x "$GAME_DIR/Cuphead.app/Contents/MacOS/Cuphead" ]; then
  echo "Cuphead.app not found next to $MOD_DIR" >&2
  exit 1
fi

if [ ! -f "$GAME_DIR/BepInEx/core/BepInEx.Preloader.dll" ]; then
  ZIP="${1:-}"
  if [ -z "$ZIP" ]; then
    echo "BepInEx is not installed. Download BepInEx_macos_universal_5.4.23.5.zip from" >&2
    echo "https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5 and run: scripts/dev-install.sh path/to/zip" >&2
    exit 1
  fi
  unzip -q -o "$ZIP" -d "$GAME_DIR"
fi

# Point the BepInEx launcher at the game bundle.
sed -i '' 's|^executable_name=.*|executable_name="Cuphead.app"|' "$GAME_DIR/run_bepinex.sh"
chmod +x "$GAME_DIR/run_bepinex.sh"

"$DOTNET" build "$MOD_DIR/CupheadOnline.csproj" -c Release -nologo -v q "-p:GameManaged=$GAME_DIR/Cuphead.app/Contents/Resources/Data/Managed"
mkdir -p "$GAME_DIR/BepInEx/plugins"
cp "$MOD_DIR/bin/Release/net35/CupheadOnline.dll" "$GAME_DIR/BepInEx/plugins/"

echo "Installed. Start Cuphead from Steam after setting its launch options"
echo "(Steam > Cuphead > Properties > General > Launch Options) to:"
echo
echo "  \"$GAME_DIR/run_bepinex.sh\" %command%"
