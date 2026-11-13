#!/bin/sh
# Yawble: remove this install, its launcher and its menu entry. Your data (~/.config/Yawble) stays
# unless you pass --purge. If the AppArmor profile is installed for this install, remove it first, with root:
#   sudo '<install dir>/uninstall-apparmor.sh'
set -eu
here=$(cd "$(dirname "$0")" && pwd)
purge=0
[ "${1:-}" = --purge ] && purge=1
if grep -qF "\"$here/yawble\"" /etc/apparmor.d/yawble 2>/dev/null; then
  echo "The AppArmor profile for this install is still loaded. Remove it first, then run me again:" >&2
  echo "  sudo '$here/uninstall-apparmor.sh'" >&2
  exit 1
fi
[ -x "$here/yawble" ] || { echo "uninstall.sh: $here is not a Yawble install" >&2; exit 1; }
for d in "$HOME/.local/bin" /usr/local/bin; do
  if [ -f "$d/yawble" ] && grep -qF "'$here/yawble-launch.sh'" "$d/yawble"; then rm -f "$d/yawble"; fi
done
for d in "$HOME/.local/share/applications" /usr/share/applications; do
  if [ -f "$d/yawble.desktop" ] && grep -qF "\"$here/yawble-launch.sh\"" "$d/yawble.desktop"; then rm -f "$d/yawble.desktop"; fi
done
if [ "$purge" = 1 ]; then rm -rf "${XDG_CONFIG_HOME:-$HOME/.config}/Yawble"; fi
rm -rf "$here"
echo "Yawble removed from $here$( [ "$purge" = 1 ] && echo ', with its data')"
