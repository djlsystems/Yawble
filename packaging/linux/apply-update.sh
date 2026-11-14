#!/bin/sh
# Yawble: applies a downloaded update (Restart to update). The app runs a copy of this script, detached, then quits.
#
#   apply-update.sh <tarball> <install folder> <log file>
#
# It waits until nothing runs from the install folder (60 s at most), unpacks the tarball, which the app has already
# checked against the release's SHA256SUMS-linux, runs its install.sh over the same folder, and starts Yawble again.
# If anything fails, the install is left as install.sh left it and the reason is in the log.
set -eu
[ $# = 3 ] || { echo "usage: apply-update.sh <tarball> <install folder> <log file>" >&2; exit 2; }
tarball=$1
prefix=$2
log=$3
mkdir -p "$(dirname "$log")"
exec >>"$log" 2>&1
say() { echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) update: $*"; }
say "applying $tarball over $prefix"

[ -f "$tarball" ] || { say "no tarball at $tarball; nothing changed"; exit 1; }
[ -x "$prefix/yawble" ] && [ -f "$prefix/install.sh" ] || { say "$prefix is not a Yawble install; nothing changed"; exit 1; }
# The running programs' paths in /proc are resolved, so the install's must be too (a symlink in it, a trailing slash).
prefix=$(cd "$prefix" && pwd -P)

# Wait for the window and the core (every process whose program is inside the install) to end.
until=$(( $(date +%s) + 60 ))
while :; do
  busy=
  for exe in /proc/[0-9]*/exe; do
    target=$(readlink "$exe" 2>/dev/null) || continue
    case "$target" in "$prefix"/*) busy=$target; break ;; esac
  done
  [ -z "$busy" ] && break
  if [ "$(date +%s)" -ge "$until" ]; then say "$busy still runs after 60 s; nothing changed"; exit 1; fi
  sleep 0.5
done

stage=$(mktemp -d "${TMPDIR:-/tmp}/yawble-update.XXXXXX")
trap 'rm -rf "$stage"' EXIT
tar -xzf "$tarball" -C "$stage" --no-same-owner
set -- "$stage"/*/
if [ $# != 1 ] || [ ! -f "$1install.sh" ]; then say "the tarball does not hold one folder with install.sh; nothing changed"; exit 1; fi
root=${1%/}
sh "$root/install.sh" --prefix "$prefix"
say "installed; starting Yawble"
nohup "$prefix/yawble-launch.sh" >/dev/null 2>&1 &
