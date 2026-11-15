#!/bin/sh
# Yawble: remove this install, its launcher, its menu entry and its data: the app's folder (~/.local/share/yawble:
# database, logs, backups, teams and their copies of your repositories) and the companion's pairing keys
# (~/.config/yawble-companion). Pass --keep-data to keep the data, for a reinstall that picks up where it left off.
# Yawble must not be running. If the AppArmor profile is installed for this install, remove it first, with root:
#   sudo '<install dir>/uninstall-apparmor.sh'
set -eu
here=$(cd "$(dirname "$0")" && pwd -P)
keep=0
case "${1:-}" in
  "") ;;
  --keep-data) keep=1 ;;
  --purge) ;; # older wording: the data goes either way
  *) echo "usage: uninstall.sh [--keep-data]" >&2; exit 2 ;;
esac
if grep -qF "\"$here/yawble\"" /etc/apparmor.d/yawble 2>/dev/null; then
  echo "The AppArmor profile for this install is still loaded. Remove it first, then run me again:" >&2
  echo "  sudo '$here/uninstall-apparmor.sh'" >&2
  exit 1
fi
[ -x "$here/yawble" ] || { echo "uninstall.sh: $here is not a Yawble install" >&2; exit 1; }

# Where the app and the companion keep their data (shell/src/main/app-folder.ts, Companion/ControlDiscovery.cs), and
# the folder older builds used. A data folder named by YAWBLE_DATA_DIR is the person's own and is left alone.
data="${XDG_DATA_HOME:-$HOME/.local/share}/yawble"
config="${XDG_CONFIG_HOME:-$HOME/.config}"

# Nothing may run from this install, nor (when the data goes) on its data folder: a running window keeps working on
# deleted files, and the next start would run a second Yawble beside it.
running=
for p in /proc/[0-9]*; do
  [ "$p" = "/proc/$$" ] && continue
  target=$(readlink "$p/exe" 2>/dev/null) || continue
  case "$target" in "$here"/*) running="${p#/proc/}"; break ;; esac
  # A Yawble from another install (or one whose files are already deleted) that works on this data folder.
  [ "$keep" = 0 ] || continue
  case "${target##*/}" in yawble|yawble-core|"yawble (deleted)"|"yawble-core (deleted)") ;; *) continue ;; esac
  if tr '\0' '\n' <"$p/cmdline" 2>/dev/null | grep -qF "$data"; then running="${p#/proc/}"; break; fi
done
if [ -n "$running" ]; then
  echo "Yawble is running (pid $running). Quit it, then run me again." >&2
  exit 1
fi

for d in "$HOME/.local/bin" /usr/local/bin; do
  if [ -f "$d/yawble" ] && grep -qF "'$here/yawble-launch.sh'" "$d/yawble"; then rm -f "$d/yawble"; fi
done
for d in "$HOME/.local/share/applications" /usr/share/applications; do
  if [ -f "$d/yawble.desktop" ] && grep -qF "\"$here/yawble-launch.sh\"" "$d/yawble.desktop"; then rm -f "$d/yawble.desktop"; fi
done
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$HOME/.local/share/applications" 2>/dev/null || true
if [ "$keep" = 0 ]; then
  rm -rf "$data" "$config/yawble-companion" "$config/Yawble"
fi
rm -rf "$here"
if [ "$keep" = 0 ]; then
  echo "Yawble removed from $here, with its data"
else
  echo "Yawble removed from $here; its data stays in $data"
fi
