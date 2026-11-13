#!/bin/sh
# Yawble, unsigned dev build: install from this unpacked tarball. No root needed.
#
#   ./install.sh [--prefix DIR]
#
# Default prefix: ~/.local/opt/yawble (or /opt/yawble when run as root). Also writes a launcher
# (~/.local/bin/yawble) and a menu entry (~/.local/share/applications/yawble.desktop).
# On Ubuntu 24.04+ the app also needs its AppArmor profile, which needs root ONCE:
#   sudo '<prefix>/install-apparmor.sh'
set -eu

src=$(cd "$(dirname "$0")" && pwd)
if [ "$(id -u)" = 0 ]; then
  prefix=/opt/yawble; bindir=/usr/local/bin; appsdir=/usr/share/applications
else
  prefix="$HOME/.local/opt/yawble"; bindir="$HOME/.local/bin"; appsdir="$HOME/.local/share/applications"
fi
while [ $# -gt 0 ]; do
  case "$1" in
    --prefix) prefix=$2; shift 2 ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "install.sh: unknown argument $1" >&2; exit 2 ;;
  esac
done
case "$prefix" in
  /*) ;;
  *) prefix="$(pwd)/$prefix" ;;
esac
# The AppArmor profile names this path; its glob characters and quotes would change what the profile matches.
case "$prefix" in
  *[\"\'\`\$\*\?\[\]\{\}\^\\]*) echo "install.sh: the install path cannot contain quotes, \$, \`, * ? [ ] { } ^ or \\: $prefix" >&2; exit 2 ;;
esac
[ -x "$src/yawble" ] || { echo "install.sh: run me from the unpacked tarball (no yawble beside me)" >&2; exit 1; }

if [ "$src" != "$prefix" ]; then
  if [ -e "$prefix" ]; then
    [ -x "$prefix/yawble" ] || { echo "install.sh: $prefix exists and is not a Yawble install; not touching it" >&2; exit 1; }
    rm -rf "$prefix"
  fi
  mkdir -p "$prefix"
  cp -R "$src/." "$prefix/"
fi
# Every user may run a system-wide install; nobody but its owner may change it.
chmod -R u+rwX,go+rX,go-w "$prefix"

mkdir -p "$bindir" "$appsdir"
# The launcher checks the AppArmor profile before starting Electron, whose own sandbox needs it too.
launcher="$bindir/yawble"
cat > "$launcher" <<EOF
#!/bin/sh
exec '$prefix/yawble-launch.sh' "\$@"
EOF
chmod 755 "$launcher"
desktop="$appsdir/yawble.desktop"
while IFS= read -r line; do
  case "$line" in
    Exec=*) echo "Exec=\"$prefix/yawble-launch.sh\" %U" ;;
    *) echo "$line" ;;
  esac
done < "$prefix/yawble.desktop.in" > "$desktop"

echo "Yawble installed in $prefix"
echo "  launcher:   $launcher"
echo "  menu entry: $desktop"
echo "  uninstall:  '$prefix/uninstall.sh'"
if [ "$(cat /proc/sys/kernel/apparmor_restrict_unprivileged_userns 2>/dev/null || echo 0)" = 1 ]; then
  echo
  echo "This host restricts unprivileged user namespaces (Ubuntu 24.04+). Yawble cannot sandbox agents"
  echo "until its AppArmor profile is installed. Run this ONCE, as root:"
  echo "  sudo '$prefix/install-apparmor.sh'"
fi
