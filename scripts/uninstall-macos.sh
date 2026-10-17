#!/bin/bash
# Online Co-op for Cuphead (unofficial) - macOS uninstaller.
#
#   curl -fsSL https://github.com/nikrabaev/cuphead-online-coop/releases/latest/download/uninstall-macos.sh | bash
#
# Options (after "| bash -s --"):
#   --yes             close Steam without asking (needed to clear Cuphead's launch options)
#   --keep-bepinex    remove only this mod, keep the BepInEx mod loader
#   --game-dir PATH   Cuphead folder (the one containing Cuphead.app), if not auto-detected
#
# Removes the mod and its settings. BepInEx is removed too unless other BepInEx mods are still installed.
# Cuphead's Steam launch options are cleared only if they point at BepInEx's run_bepinex.sh. Saves are not touched.
set -euo pipefail

APP_ID="268910"
STEAM_DIR="$HOME/Library/Application Support/Steam"

ASSUME_YES=0
KEEP_BEPINEX=0
GAME_DIR="${CUPHEAD_DIR:-}"
while [ $# -gt 0 ]; do
  case "$1" in
    --yes|-y) ASSUME_YES=1 ;;
    --keep-bepinex) KEEP_BEPINEX=1 ;;
    --game-dir) shift; GAME_DIR="${1:-}" ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

say()  { printf '\033[1m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[33mWarning:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[31mError:\033[0m %s\n' "$*" >&2; exit 1; }

ask() {
  local reply
  if [ "$ASSUME_YES" = 1 ]; then return 0; fi
  if ! (exec </dev/tty) 2>/dev/null; then return 1; fi
  printf '%s [Y/n] ' "$1" >/dev/tty
  read -r reply </dev/tty || return 1
  case "$reply" in [nN]*) return 1 ;; *) return 0 ;; esac
}

[ "$(uname -s)" = "Darwin" ] || die "This uninstaller is for macOS. On Windows use uninstall-windows.ps1."

find_game() {
  local vdf="$STEAM_DIR/steamapps/libraryfolders.vdf" lib
  {
    echo "$STEAM_DIR"
    if [ -f "$vdf" ]; then
      sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$vdf" | sed 's/\\\\/\\/g'
    fi
  } | while IFS= read -r lib; do
    if [ -d "$lib/steamapps/common/Cuphead/Cuphead.app" ]; then
      echo "$lib/steamapps/common/Cuphead"
      break
    fi
  done
}

if [ -z "$GAME_DIR" ]; then GAME_DIR="$(find_game)"; fi
[ -n "$GAME_DIR" ] && [ -d "$GAME_DIR/Cuphead.app" ] || die "Cuphead was not found. Rerun with --game-dir \"/path/to/Cuphead\"."
say "Found Cuphead in $GAME_DIR"

if pgrep -f "Cuphead.app/Contents/MacOS/Cuphead" >/dev/null 2>&1; then
  die "Cuphead is running. Quit the game and run the uninstaller again."
fi

# ---------------------------------------------------------------------------------------------- files
rm -f "$GAME_DIR/BepInEx/plugins/CupheadOnline.dll" "$GAME_DIR/BepInEx/config/dev.cupheadonline.mod.cfg"
rm -f "$GAME_DIR"/BepInEx/CupheadOnline-*.log
say "Removed the mod"

# steam_appid.txt is only ever created by developer test setups of this mod.
if [ -f "$GAME_DIR/steam_appid.txt" ] && [ "$(tr -d '[:space:]' < "$GAME_DIR/steam_appid.txt")" = "$APP_ID" ]; then
  rm -f "$GAME_DIR/steam_appid.txt"
fi

OTHER_MODS="$(find "$GAME_DIR/BepInEx/plugins" "$GAME_DIR/BepInEx/patchers" -type f 2>/dev/null | head -n 1 || true)"
if [ "$KEEP_BEPINEX" = 1 ]; then
  say "Keeping BepInEx (--keep-bepinex)"
elif [ -n "$OTHER_MODS" ]; then
  say "Keeping BepInEx: other BepInEx mods are still installed"
  KEEP_BEPINEX=1
else
  rm -rf "$GAME_DIR/BepInEx" "$GAME_DIR/run_bepinex.sh" "$GAME_DIR/libdoorstop.dylib" "$GAME_DIR/.doorstop_version"
  if [ -f "$GAME_DIR/changelog.txt" ] && grep -q "since v5" "$GAME_DIR/changelog.txt" 2>/dev/null; then
    rm -f "$GAME_DIR/changelog.txt"
  fi
  say "Removed BepInEx"
fi

# ---------------------------------------------------------------------------------------------- launch options
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

needs_clear() {
  local f
  for f in "$STEAM_DIR"/userdata/*/config/localconfig.vdf; do
    [ -f "$f" ] || continue
    case "$(/usr/bin/perl -e "$VDF_TOOL" get "$f" "$APP_ID" 2>/dev/null)" in *run_bepinex.sh*) return 0 ;; esac
  done
  return 1
}

REOPEN_STEAM=0
if [ "$KEEP_BEPINEX" = 1 ]; then
  : # BepInEx stays, so its launch options stay valid
elif needs_clear; then
  CLEARED=0
  if steam_running; then
    if ask "Steam must be closed to clear Cuphead's launch options. Close Steam now?"; then
      say "Closing Steam"
      /usr/bin/osascript -e 'tell application "Steam" to quit' >/dev/null 2>&1 || true
      i=0
      while steam_running && [ $i -lt 60 ]; do sleep 1; i=$((i + 1)); done
      steam_running || REOPEN_STEAM=1
    fi
  fi
  if ! steam_running; then
    for f in "$STEAM_DIR"/userdata/*/config/localconfig.vdf; do
      [ -f "$f" ] || continue
      /usr/bin/perl -e "$VDF_TOOL" clear "$f" "$APP_ID" || warn "Could not update $f"
    done
    CLEARED=1
    say "Cleared Cuphead's Steam launch options"
  fi
  if [ "$CLEARED" = 0 ]; then
    echo
    echo "Last step: in Steam, right-click Cuphead > Properties > General > Launch Options, and delete the text there."
  fi
fi
if [ "$REOPEN_STEAM" = 1 ]; then /usr/bin/open -a Steam || true; fi

echo
say "Done. Cuphead is back to normal; your saves were not touched."
