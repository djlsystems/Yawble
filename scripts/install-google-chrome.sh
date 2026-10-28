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

set -u
root="${HARNESS_SIGN_IN_CHROME:-${HARNESS_DATA_ROOT:-/data}/google-chrome}"
index_url="${HARNESS_CHROME_INDEX:-https://dl.google.com/linux/chrome/deb/dists/stable/main/binary-amd64/Packages}"
pool_url="${HARNESS_CHROME_POOL:-https://dl.google.com/linux/chrome/deb}"

if [ ! -d "$root" ] || [ ! -w "$root" ]; then
  echo "google chrome: $root is missing or not writable; the sign-in page cannot start a browser"
  exit 0
fi

newest() { ls -d "$root"/[0-9]*/ 2>/dev/null | sed 's#/$##; s#.*/##' | sort -V | tail -n 1; }

index=$(curl -fsSL --max-time 60 "$index_url" \
  | awk '/^Package: google-chrome-stable$/{p=1} p&&/^(Version|Filename|SHA256):/{print} p&&/^$/{exit}') || index=""
version=$(printf '%s\n' "$index" | sed -n 's/^Version: \([0-9][0-9.]*\)-.*/\1/p')
file=$(printf '%s\n' "$index" | sed -n 's/^Filename: \(pool\/[A-Za-z0-9._\/-]*\.deb\)$/\1/p')
sha=$(printf '%s\n' "$index" | sed -n 's/^SHA256: \([0-9a-f]\{64\}\)$/\1/p')
if [ -z "$version" ] || [ -z "$file" ] || [ -z "$sha" ]; then
  have=$(newest)
  echo "google chrome: could not read the newest stable version from dl.google.com; keeping ${have:-none}"
  exit 0
fi

if [ "${HARNESS_REINSTALL_AGENTS:-}" != "1" ] && [ -x "$root/$version/chrome" ]; then
  echo "google chrome: $version, the newest stable, is installed"
  exit 0
fi

echo "google chrome: installing $version into $root"
work="$root/.download-$$"
rm -rf "$work" && mkdir -p "$work/x" || exit 0
if curl -fsSL --max-time 900 -o "$work/chrome.deb" "$pool_url/$file" \
  && [ "$(sha256sum "$work/chrome.deb" | cut -d ' ' -f 1)" = "$sha" ] \
  && dpkg-deb -x "$work/chrome.deb" "$work/x" \
  && [ -x "$work/x/opt/google/chrome/chrome" ]; then
  rm -rf "${root:?}/$version"
  # No setuid helper: the browser runs with its user-namespace sandbox, never the setuid one.
  rm -f "$work/x/opt/google/chrome/chrome-sandbox"
  chmod -R g-w,o-w "$work/x/opt/google/chrome"
  mv "$work/x/opt/google/chrome" "$root/$version"
  echo "google chrome: $version installed"
  ls -d "$root"/[0-9]*/ 2>/dev/null | sed 's#/$##' | sort -V | head -n -2 | while read -r old; do rm -rf "$old"; done
else
  echo "google chrome: FAILED to install $version (download, checksum or unpack); keeping $(newest)"
fi
rm -rf "$work"
