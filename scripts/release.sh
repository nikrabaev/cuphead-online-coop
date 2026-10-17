#!/bin/bash
# Maintainer script: builds the mod from your local Cuphead install and packages a GitHub release.
# Players never run this; they use the one-line installers attached to each release.
#
#   scripts/release.sh            build + package into dist/
#   scripts/release.sh --publish  ...and create the GitHub release (needs `gh`, tag = v<Version>)
#
# The build needs the game's own assemblies, which may not be redistributed, so releases are built
# locally rather than in CI. Override the location with GAME_MANAGED=/path/to/Managed.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIST="$ROOT/dist"
VERSION="$(sed -n 's/.*public const string Version = "\([^"]*\)".*/\1/p' "$ROOT/src/Plugin.cs")"
[ -n "$VERSION" ] || { echo "Could not read Version from src/Plugin.cs" >&2; exit 1; }
TAG="v$VERSION"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

BUILD_ARGS=(-c Release -nologo -v q)
[ -n "${GAME_MANAGED:-}" ] && BUILD_ARGS+=("-p:GameManaged=$GAME_MANAGED")

echo "Building $TAG ..."
rm -rf "$ROOT/bin" "$ROOT/obj" "$DIST"
"$DOTNET" build "$ROOT/CupheadOnline.csproj" "${BUILD_ARGS[@]}"

mkdir -p "$DIST/pkg/BepInEx/plugins"
cp "$ROOT/bin/Release/net35/CupheadOnline.dll" "$DIST/"
cp "$ROOT/bin/Release/net35/CupheadOnline.dll" "$DIST/pkg/BepInEx/plugins/"
cp "$ROOT/LICENSE" "$ROOT/THIRD_PARTY_NOTICES.md" "$ROOT/README.md" "$DIST/pkg/"
(cd "$DIST/pkg" && zip -qr -X "$DIST/CupheadOnline-$TAG.zip" .)
rm -rf "$DIST/pkg"
cp "$ROOT/scripts/install-macos.sh" "$ROOT/scripts/uninstall-macos.sh" \
   "$ROOT/scripts/install-windows.ps1" "$ROOT/scripts/uninstall-windows.ps1" "$DIST/"
(cd "$DIST" && shasum -a 256 CupheadOnline.dll "CupheadOnline-$TAG.zip" install-macos.sh uninstall-macos.sh \
   install-windows.ps1 uninstall-windows.ps1 > SHA256SUMS.txt)

cat > "$DIST/RELEASE_NOTES.md" <<NOTES
Unofficial online co-op mod for Cuphead (game version 1.3.4). Both players need their own copy of Cuphead and this mod.

**macOS** (Terminal, Steam version of Cuphead):
\`\`\`bash
curl -fsSL https://github.com/nikrabaev/cuphead-online-coop/releases/latest/download/install-macos.sh | bash
\`\`\`

**Windows** (PowerShell, experimental):
\`\`\`powershell
irm https://github.com/nikrabaev/cuphead-online-coop/releases/latest/download/install-windows.ps1 | iex
\`\`\`

Then start Cuphead from Steam and press **F8**. Uninstall instructions are in the README.

Files: \`CupheadOnline.dll\` is the mod itself; \`CupheadOnline-$TAG.zip\` is the same plugin laid out for a manual install
into an existing BepInEx 5 setup. Checksums are in \`SHA256SUMS.txt\`. The installers download BepInEx 5.4.23.5 from
the official BepInEx release and verify it against pinned SHA-256 hashes.

Not affiliated with StudioMDHR. No game files are included.
NOTES

echo "Packaged in $DIST:"; ls -1 "$DIST"

if [ "${1:-}" = "--publish" ]; then
  gh release create "$TAG" --title "Online Co-op for Cuphead $TAG" --notes-file "$DIST/RELEASE_NOTES.md" \
    "$DIST/CupheadOnline.dll" "$DIST/CupheadOnline-$TAG.zip" "$DIST/SHA256SUMS.txt" \
    "$DIST/install-macos.sh" "$DIST/uninstall-macos.sh" "$DIST/install-windows.ps1" "$DIST/uninstall-windows.ps1"
fi
