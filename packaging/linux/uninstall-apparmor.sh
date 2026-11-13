#!/bin/sh
# Yawble: unload and remove its AppArmor profile. Needs root.
#   sudo '<install dir>/uninstall-apparmor.sh'
set -eu
profile=/etc/apparmor.d/yawble
if [ "$(id -u)" != 0 ]; then
  echo "uninstall-apparmor.sh needs root. Run: sudo '$(cd "$(dirname "$0")" && pwd)/uninstall-apparmor.sh'" >&2
  exit 1
fi
if [ ! -e "$profile" ]; then echo "No $profile; nothing to remove."; exit 0; fi
if command -v apparmor_parser >/dev/null 2>&1 && [ -d /sys/kernel/security/apparmor ]; then
  apparmor_parser -R "$profile" || true
fi
rm -f "$profile"
echo "Removed $profile"
