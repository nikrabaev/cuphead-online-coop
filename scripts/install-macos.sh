#!/bin/bash
# Online Co-op for Cuphead (unofficial) - macOS installer.
#
#   curl -fsSL https://github.com/nikrabaev/cuphead-online-coop/releases/latest/download/install-macos.sh | bash
#
# Options (after "| bash -s --"):
#   --yes                  close Steam without asking (needed to set Cuphead's launch options)
#   --skip-launch-options  don't touch Steam settings; print the launch options to paste instead
#   --game-dir PATH        Cuphead folder (the one containing Cuphead.app), if not auto-detected
#
# What it does: downloads BepInEx 5.4.23.5 (the mod loader) from its official GitHub release and the mod from
# this project's latest release, verifies both against SHA-256 checksums, puts them into the Cuphead folder,
# and sets Cuphead's Steam launch options so Steam starts the game with the mod. Nothing else is installed.
set -euo pipefail

REPO="nikrabaev/cuphead-online-coop"
RELEASE_URL="${CUPHEAD_ONLINE_RELEASE_URL:-}"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_macos_universal_5.4.23.5.zip"
BEPINEX_SHA256="01c2ae782eb016dfd6c345a18dbd2dcafffb3d9d318449d6486689f426b4a323"
APP_ID="268910"
SUPPORTED_GAME_VERSION="1.3.4"
STEAM_DIR="$HOME/Library/Application Support/Steam"

ASSUME_YES=0
SKIP_LAUNCH=0
GAME_DIR="${CUPHEAD_DIR:-}"
while [ $# -gt 0 ]; do
  case "$1" in
    --yes|-y) ASSUME_YES=1 ;;
    --skip-launch-options) SKIP_LAUNCH=1 ;;
    --game-dir) shift; GAME_DIR="${1:-}" ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

say()  { printf '\033[1m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[33mWarning:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[31mError:\033[0m %s\n' "$*" >&2; exit 1; }

# Reading answers from the terminal works even when this script itself arrives through a pipe.
ask() {
  local reply
  if [ "$ASSUME_YES" = 1 ]; then return 0; fi
  if ! (exec </dev/tty) 2>/dev/null; then return 1; fi
  printf '%s [Y/n] ' "$1" >/dev/tty
  read -r reply </dev/tty || return 1
  case "$reply" in [nN]*) return 1 ;; *) return 0 ;; esac
}

[ "$(uname -s)" = "Darwin" ] || die "This installer is for macOS. On Windows use install-windows.ps1."

# ---------------------------------------------------------------------------------------------- locate the game
find_game() {
  # No pipeline here: stopping early would SIGPIPE the producer and, with pipefail, abort the script.
  local vdf="$STEAM_DIR/steamapps/libraryfolders.vdf" libs lib
  libs="$STEAM_DIR"
  if [ -f "$vdf" ]; then
    libs="$libs
$(sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$vdf" | sed 's/\\\\/\\/g')"
  fi
  while IFS= read -r lib; do
    if [ -n "$lib" ] && [ -d "$lib/steamapps/common/Cuphead/Cuphead.app" ]; then
      echo "$lib/steamapps/common/Cuphead"
      return 0
    fi
  done <<LIBS
$libs
LIBS
  return 0
}

if [ -z "$GAME_DIR" ]; then GAME_DIR="$(find_game)"; fi
[ -n "$GAME_DIR" ] || die "Cuphead was not found in your Steam libraries. Install it from Steam, or rerun with --game-dir \"/path/to/Cuphead\"."
APP="$GAME_DIR/Cuphead.app"
[ -d "$APP" ] || die "No Cuphead.app in $GAME_DIR"
say "Found Cuphead in $GAME_DIR"

GAME_VERSION="$(/usr/bin/defaults read "$APP/Contents/Info" CFBundleShortVersionString 2>/dev/null || echo unknown)"
if [ "$GAME_VERSION" != "$SUPPORTED_GAME_VERSION" ]; then
  warn "This mod was built for Cuphead $SUPPORTED_GAME_VERSION; you have $GAME_VERSION. It may not load."
fi

if pgrep -f "Cuphead.app/Contents/MacOS/Cuphead" >/dev/null 2>&1; then
  die "Cuphead is running. Quit the game and run the installer again."
fi

# ---------------------------------------------------------------------------------------------- downloads
TAG=""
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

download() { curl -fL --retry 3 --silent --show-error -o "$2" "$1" || die "Download failed: $1"; }
sha256() { shasum -a 256 "$1" | awk '{print $1}'; }

# Download and verify everything before changing anything in the game folder.
NEED_BEPINEX=1
if [ -f "$GAME_DIR/BepInEx/core/BepInEx.Preloader.dll" ] && [ -f "$GAME_DIR/run_bepinex.sh" ]; then
  NEED_BEPINEX=0
  say "BepInEx is already installed"
else
  say "Downloading BepInEx 5.4.23.5 (mod loader)"
  download "$BEPINEX_URL" "$TMP/bepinex.zip"
  [ "$(sha256 "$TMP/bepinex.zip")" = "$BEPINEX_SHA256" ] || die "BepInEx download failed its checksum; nothing was changed."
fi

# Pin one release for every file: "latest/download/..." links are cached separately and can briefly point at
# different releases right after a new one is published.
if [ -z "$RELEASE_URL" ]; then
  TAG="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" 2>/dev/null | sed -n 's/^[[:space:]]*"tag_name":[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1 || true)"
  if [ -n "$TAG" ]; then
    RELEASE_URL="https://github.com/$REPO/releases/download/$TAG"
  else
    RELEASE_URL="https://github.com/$REPO/releases/latest/download"
  fi
fi

say "Downloading the mod${TAG:+ $TAG}"
download "$RELEASE_URL/CupheadOnline.dll" "$TMP/CupheadOnline.dll"
download "$RELEASE_URL/SHA256SUMS.txt" "$TMP/SHA256SUMS.txt"
EXPECTED="$(awk '$2 == "CupheadOnline.dll" {print $1}' "$TMP/SHA256SUMS.txt")"
[ -n "$EXPECTED" ] && [ "$(sha256 "$TMP/CupheadOnline.dll")" = "$EXPECTED" ] || die "Mod download failed its checksum; nothing was changed."

if [ "$NEED_BEPINEX" = 1 ]; then
  /usr/bin/ditto -x -k "$TMP/bepinex.zip" "$GAME_DIR"
fi
# Point BepInEx's launcher at the game bundle.
/usr/bin/sed -i '' 's|^executable_name=.*|executable_name="Cuphead.app"|' "$GAME_DIR/run_bepinex.sh"
chmod +x "$GAME_DIR/run_bepinex.sh"
mkdir -p "$GAME_DIR/BepInEx/plugins"
cp "$TMP/CupheadOnline.dll" "$GAME_DIR/BepInEx/plugins/CupheadOnline.dll"
/usr/bin/xattr -dr com.apple.quarantine "$GAME_DIR/BepInEx" "$GAME_DIR/run_bepinex.sh" "$GAME_DIR/libdoorstop.dylib" 2>/dev/null || true
say "Mod installed into $GAME_DIR/BepInEx/plugins"

# ---------------------------------------------------------------------------------------------- launch options
LAUNCH="\"$GAME_DIR/run_bepinex.sh\" %command%"

manual_launch_options() {
  echo
  echo "Last step: in Steam, right-click Cuphead > Properties > General > Launch Options, and paste:"
  echo
  echo "    $LAUNCH"
  echo
}

# Edits Steam's localconfig.vdf (KeyValues text) without touching anything but Cuphead's LaunchOptions.
read -r -d '' VDF_TOOL <<'PERL' || true
use strict; use warnings;
my ($mode, $file, $appid, $value) = @ARGV;
open(my $fh, '<', $file) or die "cannot read $file\n"; local $/; my $text = <$fh>; close $fh;
my @tok;
while ($text =~ /\G(\s+|\/\/[^\n]*|"((?:[^"\\]|\\.)*)"|\{|\}|\[[^\]]*\]|[^\s"{}]+)/gcs) {
  my ($all, $str) = ($1, $2);
  next if $all =~ /^\s/ || $all =~ /^\/\// || $all =~ /^\[/;
  if    ($all eq '{') { push @tok, ['{'] }
  elsif ($all eq '}') { push @tok, ['}'] }
  else  { push @tok, ['s', defined $str ? $str : $all] }
}
die "unexpected data in $file\n" if (pos($text) // 0) < length($text) && $text =~ /\S/;
my $i = 0;
sub parse_node {
  my @n;
  while ($i < @tok) {
    my $t = $tok[$i++];
    return \@n if $t->[0] eq '}';
    die "malformed vdf\n" unless $t->[0] eq 's';
    my $key = $t->[1];
    my $v = $tok[$i++] or die "malformed vdf\n";
    if ($v->[0] eq '{') { push @n, [$key, parse_node()] }
    elsif ($v->[0] eq 's') { push @n, [$key, $v->[1]] }
    else { die "malformed vdf\n" }
  }
  return \@n;
}
my $root = parse_node();
sub child {
  my ($node, $key, $create) = @_;
  for my $kv (@$node) { return $kv->[1] if lc($kv->[0]) eq lc($key) && ref $kv->[1] }
  return undef unless $create;
  my $new = []; push @$node, [$key, $new]; return $new;
}
my $create = $mode eq 'set';
my $n = $root;
for my $k ('UserLocalConfigStore', 'Software', 'Valve', 'Steam', 'apps', $appid) {
  $n = child($n, $k, $create);
  if (!$n) { print "" if $mode eq 'get'; exit 0 }
}
my ($idx) = grep { lc($n->[$_][0]) eq 'launchoptions' && !ref $n->[$_][1] } 0..$#$n;
if ($mode eq 'get') { if (defined $idx) { (my $v = $n->[$idx][1]) =~ s/\\(.)/$1/g; print $v } exit 0 }
if ($mode eq 'set') {
  (my $esc = $value) =~ s/(["\\])/\\$1/g;
  if (defined $idx) { $n->[$idx][1] = $esc } else { push @$n, ['LaunchOptions', $esc] }
}
if ($mode eq 'clear') {
  exit 0 unless defined $idx && $n->[$idx][1] =~ /run_bepinex\.sh/;
  splice(@$n, $idx, 1);
}
sub ser {
  my ($node, $d) = @_; my $ind = "\t" x $d; my $o = '';
  for my $kv (@$node) {
    my ($k, $v) = @$kv;
    if (ref $v) { $o .= "$ind\"$k\"\n$ind\{\n" . ser($v, $d + 1) . "$ind\}\n" }
    else { $o .= "$ind\"$k\"\t\t\"$v\"\n" }
  }
  return $o;
}
open(my $out, '>', "$file.tmp") or die "cannot write $file.tmp\n"; print $out ser($root, 0); close $out;
rename("$file.tmp", $file) or die "cannot replace $file\n";
PERL

steam_running() { pgrep -x steam_osx >/dev/null 2>&1; }

set_launch_options() {
  local configs=() f
  for f in "$STEAM_DIR"/userdata/*/config/localconfig.vdf; do [ -f "$f" ] && configs+=("$f"); done
  if [ ${#configs[@]} -eq 0 ]; then
    warn "No Steam user settings found (sign in to Steam once)."
    return 1
  fi
  if steam_running; then
    if ask "Steam must be closed to set Cuphead's launch options. Close Steam now?"; then
      say "Closing Steam"
      /usr/bin/osascript -e 'tell application "Steam" to quit' >/dev/null 2>&1 || true
      local i=0
      while steam_running && [ $i -lt 60 ]; do sleep 1; i=$((i + 1)); done
      steam_running && { warn "Steam did not close."; return 1; }
      REOPEN_STEAM=1
    else
      return 1
    fi
  fi
  for f in "${configs[@]}"; do
    [ -f "$f.cupheadonline-backup" ] || cp "$f" "$f.cupheadonline-backup"
    /usr/bin/perl -e "$VDF_TOOL" set "$f" "$APP_ID" "$LAUNCH" || { warn "Could not update $f"; return 1; }
    [ "$(/usr/bin/perl -e "$VDF_TOOL" get "$f" "$APP_ID")" = "$LAUNCH" ] || { warn "Could not verify $f"; return 1; }
  done
  say "Set Cuphead's Steam launch options"
  return 0
}

REOPEN_STEAM=0
if [ "$SKIP_LAUNCH" = 1 ] || ! set_launch_options; then
  manual_launch_options
fi
if [ "$REOPEN_STEAM" = 1 ]; then /usr/bin/open -a Steam || true; fi

echo
say "Done. Start Cuphead from Steam and press F8 in game to host or join."
echo "    Your friend needs the mod too. To uninstall:"
echo "    curl -fsSL https://github.com/nikrabaev/cuphead-online-coop/releases/latest/download/uninstall-macos.sh | bash"
