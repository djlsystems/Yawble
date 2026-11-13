#!/bin/sh
# Yawble: install its AppArmor profile. Needs root, ONCE per install (and again if you move the install).
#
#   sudo '<install dir>/install-apparmor.sh'
#
# Ubuntu 24.04+ sets kernel.apparmor_restrict_unprivileged_userns=1, which refuses the unprivileged user
# namespaces that Electron's sandbox and the agent sandbox's Bubblewrap backend (bwrap, unshare --user --net for proxy mode)
# need, unless the process runs under an AppArmor profile with the `userns` permission.
# This writes /etc/apparmor.d/yawble with two such profiles, for this install's two executables only,
# and loads it. Both are otherwise unconfined: they grant userns and take nothing away.
# Undo with: sudo '<install dir>/uninstall-apparmor.sh'
set -eu

here=$(cd "$(dirname "$0")" && pwd)
profile=/etc/apparmor.d/yawble

if [ "$(id -u)" != 0 ]; then
  echo "install-apparmor.sh needs root. Run: sudo '$here/install-apparmor.sh'" >&2
  exit 1
fi
case "$here" in
  *[\"\'\`\$\*\?\[\]\{\}\^\\]*) echo "install-apparmor.sh: the install path cannot contain quotes, \$, \`, * ? [ ] { } ^ or \\: $here" >&2; exit 2 ;;
esac
[ -x "$here/yawble" ] && [ -x "$here/resources/core/yawble-core" ] ||
  { echo "install-apparmor.sh: $here is not a Yawble install" >&2; exit 1; }
if ! command -v apparmor_parser >/dev/null 2>&1 || [ ! -d /sys/kernel/security/apparmor ]; then
  echo "AppArmor is not active on this host, so Yawble needs no profile. Nothing done."
  exit 0
fi
if [ ! -e /etc/apparmor.d/abi/4.0 ]; then
  echo "install-apparmor.sh: this AppArmor has no abi 4.0 (it predates the userns permission). Nothing done." >&2
  exit 1
fi

tmp="$profile.tmp.$$"
cat > "$tmp" <<EOF
# Yawble (unsigned dev build) - written by $here/install-apparmor.sh
# Lets this install's app and core create unprivileged user namespaces on hosts that restrict them.
abi <abi/4.0>,
include <tunables/global>

profile yawble "$here/yawble" flags=(unconfined) {
  userns,
  include if exists <local/yawble>
}

profile yawble-core "$here/resources/core/yawble-core" flags=(unconfined) {
  userns,
  include if exists <local/yawble-core>
}
EOF
apparmor_parser -Q -T "$tmp" || { rm -f "$tmp"; echo "install-apparmor.sh: the profile did not parse; nothing loaded" >&2; exit 1; }
mv "$tmp" "$profile"
chmod 644 "$profile"
apparmor_parser -r "$profile"
echo "Loaded $profile for $here"

# The agent sandbox on Linux is built on these; the app's startup check names them too.
missing=""
for t in bwrap slirp4netns; do command -v "$t" >/dev/null 2>&1 || missing="$missing $t"; done
if [ -n "$missing" ]; then
  echo "Still missing:$missing. On Ubuntu: sudo apt install bubblewrap slirp4netns"
fi
