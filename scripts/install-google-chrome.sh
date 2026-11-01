#!/bin/sh
# GOOGLE CHROME FOR THE SIGN-IN PAGE, onto the volume: $HARNESS_SIGN_IN_CHROME (/data/google-chrome),
# one folder per version, <root>/<version>/chrome. A website a person signs in to through the page
# must see the browser they use themselves, so this is Google's own stable build, not a test build
# (docs/decisions.md, "Which browser the sign-in page runs").
#
# Run by the entrypoint of every worker (and `all`) at each start, as the `worker` user that runs
# the browser, under a lock every worker takes. The folder is worker:agent with no group write
# (prepare-volume.sh): an agent can read the browser, never replace it.
#
# WHERE IT COMES FROM: Google's apt index over HTTPS gives the newest stable version, its .deb and
# that file's SHA-256; the download must match it, and is unpacked without root (dpkg-deb -x).
# HOW IT STAYS CURRENT: every start checks the index (one small download) and installs a newer
# stable beside the old one; the newest two are kept, so a browser still open on the older one is
# not pulled from under it. A start with no network keeps what it has.
#
# A failure is reported and never stops the start: the sign-in page then says no browser can start.
#
# NOTHING WAITS FOR IT. The entrypoint runs this in the background, beside the agent CLIs' installs,
# and the Host starts at once. What it is doing is written to <root>/.status, one line - `installing
# <percent>`, `ready <version>`, `failed <sentence>` - which control reads for `yawble up`, `yawble
# status` and the doctor. The percent is the download's, against the size Google's index gives.

set -u
root="${HARNESS_SIGN_IN_CHROME:-${HARNESS_DATA_ROOT:-/data}/google-chrome}"
index_url="${HARNESS_CHROME_INDEX:-https://dl.google.com/linux/chrome/deb/dists/stable/main/binary-amd64/Packages}"
pool_url="${HARNESS_CHROME_POOL:-https://dl.google.com/linux/chrome/deb}"

if [ ! -d "$root" ] || [ ! -w "$root" ]; then
  echo "google chrome: $root is missing or not writable; the sign-in page cannot start a browser"
  exit 0
fi

newest() { ls -d "$root"/[0-9]*/ 2>/dev/null | sed 's#/$##; s#.*/##' | sort -V | tail -n 1; }

# The one status line, readable by control (the host's user is in the folder's group).
status() { printf '%s\n' "$*" > "$root/.status.$$" && chmod 0644 "$root/.status.$$" && mv -f "$root/.status.$$" "$root/.status"; }
status installing 0

index=$(curl -fsSL --max-time 60 "$index_url" \
  | awk '/^Package: google-chrome-stable$/{p=1} p&&/^(Version|Filename|SHA256|Size):/{print} p&&/^$/{exit}') || index=""
version=$(printf '%s\n' "$index" | sed -n 's/^Version: \([0-9][0-9.]*\)-.*/\1/p')
file=$(printf '%s\n' "$index" | sed -n 's/^Filename: \(pool\/[A-Za-z0-9._\/-]*\.deb\)$/\1/p')
sha=$(printf '%s\n' "$index" | sed -n 's/^SHA256: \([0-9a-f]\{64\}\)$/\1/p')
size=$(printf '%s\n' "$index" | sed -n 's/^Size: \([0-9][0-9]*\)$/\1/p')
if [ -z "$version" ] || [ -z "$file" ] || [ -z "$sha" ]; then
  have=$(newest)
  echo "google chrome: could not read the newest stable version from dl.google.com; keeping ${have:-none}"
  if [ -n "$have" ]; then status ready "$have"; else status failed "could not reach dl.google.com to install it"; fi
  exit 0
fi

if [ "${HARNESS_REINSTALL_AGENTS:-}" != "1" ] && [ -x "$root/$version/chrome" ]; then
  echo "google chrome: $version, the newest stable, is installed"
  status ready "$version"
  exit 0
fi

echo "google chrome: installing $version into $root"
work="$root/.download-$$"
rm -rf "$work" && mkdir -p "$work/x" || exit 0
# The download's percent, every two seconds, against the index's size: measured, never estimated.
if [ -n "$size" ] && [ "$size" -gt 0 ]; then
  ( while sleep 2; do
      [ -d "$work" ] || exit 0
      got=$(stat -c %s "$work/chrome.deb" 2>/dev/null || echo 0)
      [ "$got" -lt "$size" ] || exit 0
      status installing $((got * 100 / size))
    done ) &
  watcher=$!
fi
if curl -fsSL --max-time 900 -o "$work/chrome.deb" "$pool_url/$file" \
  && [ "$(sha256sum "$work/chrome.deb" | cut -d ' ' -f 1)" = "$sha" ] \
  && dpkg-deb -x "$work/chrome.deb" "$work/x" \
  && [ -x "$work/x/opt/google/chrome/chrome" ]; then
  rm -rf "${root:?}/$version"
  # No setuid helper: the browser runs with its user-namespace sandbox, never the setuid one.
  rm -f "$work/x/opt/google/chrome/chrome-sandbox"
  chmod -R g-w,o-w "$work/x/opt/google/chrome"
  mv "$work/x/opt/google/chrome" "$root/$version"
  [ -z "${watcher:-}" ] || kill "$watcher" 2>/dev/null
  echo "google chrome: $version installed"
  status ready "$version"
  ls -d "$root"/[0-9]*/ 2>/dev/null | sed 's#/$##' | sort -V | head -n -2 | while read -r old; do rm -rf "$old"; done
else
  [ -z "${watcher:-}" ] || kill "$watcher" 2>/dev/null
  have=$(newest)
  echo "google chrome: FAILED to install $version (download, checksum or unpack); keeping ${have:-none}"
  if [ -n "$have" ]; then status ready "$have"; else status failed "the download of $version failed (download, checksum or unpack)"; fi
fi
rm -rf "$work"
