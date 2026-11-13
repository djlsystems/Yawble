#!/bin/sh
# Starts Yawble from this install. On a host that restricts unprivileged user namespaces (Ubuntu 24.04+)
# and has no Yawble AppArmor profile for this install, it says so and how to fix it, instead of letting
# Electron's sandbox fail with an unrelated-looking error. The app never turns its sandbox off.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
profile=/etc/apparmor.d/yawble

if [ "$(cat /proc/sys/kernel/apparmor_restrict_unprivileged_userns 2>/dev/null || echo 0)" = 1 ] &&
   ! grep -qF "\"$here/yawble\"" "$profile" 2>/dev/null; then
  msg="Yawble's AppArmor profile is missing for $here. This host restricts unprivileged user namespaces (Ubuntu 24.04+), so neither the app's own sandbox nor the agents' sandboxes can start. Run this once, as root, then start Yawble again:
  sudo '$here/install-apparmor.sh'"
  echo "$msg" >&2
  if [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ] && command -v notify-send >/dev/null 2>&1; then
    notify-send -u critical "Yawble cannot start" "$msg" || true
  fi
  exit 1
fi
exec "$here/yawble" "$@"
