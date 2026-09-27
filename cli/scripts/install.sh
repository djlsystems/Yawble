#!/bin/sh
# Installs the yawble binary and nothing else. Piped from the internet, this places one file and
# stops; the binary does everything else where it can be inspected and rerun.
#
#   curl -fsSL https://raw.githubusercontent.com/djlsystems/Yawble/main/cli/scripts/install.sh | sh
#
# YAWBLE_VERSION=v2026.09.24.1 pins a release; YAWBLE_INSTALL_DIR overrides ~/.local/bin.
# YAWBLE_CHANNEL=stable takes the newest regular release, skipping pre-releases, and saves that
# choice so `yawble update` keeps to it; the default takes the newest release of either kind.
#
# No token is needed. Installing from a private fork: set GH_TOKEN (or GITHUB_TOKEN) to a token
# with the repo scope, and fetch this script with it too:
#
#   export GH_TOKEN=...
#   curl -fsSL -H "Authorization: Bearer $GH_TOKEN" \
#     https://raw.githubusercontent.com/djlsystems/Yawble/main/cli/scripts/install.sh | sh
#
# With a token the release is read through the GitHub API, the only place a private repository
# serves its assets. The token is sent to api.github.com only: the asset's redirect to storage is
# followed here, without it. It is never printed and never on a command line.
set -eu

repo="djlsystems/Yawble"
api="${YAWBLE_GITHUB_API:-https://api.github.com}"
dir="${YAWBLE_INSTALL_DIR:-$HOME/.local/bin}"
token="${GH_TOKEN:-${GITHUB_TOKEN:-}}"

os=$(uname -s | tr '[:upper:]' '[:lower:]')
case "$os" in
  linux|darwin) ;;
  *) echo "yawble: $os is not supported by this script; on Windows use install.ps1" >&2; exit 1 ;;
esac
arch=$(uname -m)
case "$arch" in
  x86_64|amd64) arch=amd64 ;;
  aarch64|arm64) arch=arm64 ;;
  *) echo "yawble: unsupported architecture $arch" >&2; exit 1 ;;
esac
# macOS is supported on Apple silicon only.
if [ "$os" = darwin ] && [ "$arch" != arm64 ]; then
  echo "yawble: Macs with Apple silicon (M1 or later) only; this Mac has an Intel processor" >&2
  exit 1
fi

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

# newest_tag: from a releases list on stdin, the highest v<yyyy.mm.dd.N> tag that is not a draft,
# pre-releases included unless YAWBLE_CHANNEL=stable. GitHub's own "latest" never is a pre-release,
# and its list is not in release order, so the tags are sorted here, part by part as numbers (.10
# after .9). Within one release the API lists tag_name, then draft, then prerelease.
case "${YAWBLE_CHANNEL:-latest}" in
  latest|stable) ;;
  *) echo "yawble: YAWBLE_CHANNEL must be latest or stable, not ${YAWBLE_CHANNEL}" >&2; exit 2 ;;
esac
newest_tag() {
  tr ',' '\n' | awk -v stable="$([ "${YAWBLE_CHANNEL:-}" = stable ] && echo 1)" '
    /"tag_name":/   { t = $0; sub(/.*"tag_name": *"v/, "", t); sub(/".*/, "", t); d = 0; next }
    /"draft":/      { d = ($0 ~ /true/); next }
    /"prerelease":/ { if (t != "" && !d && !(stable && $0 ~ /true/)) print t; t = "" }' \
    | grep -E '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$' \
    | sort -t. -k1,1n -k2,2n -k3,3n -k4,4n | tail -n 1 | sed 's/^/v/'
}

if [ -n "$token" ]; then
  # A header file, not -H "...$token": a command line is readable by every process on the machine.
  # mktemp -d made the folder 0700. Nothing here reads stdin: when piped, stdin is this script.
  ( umask 077; printf 'Authorization: Bearer %s\n' "$token" > "$tmp/auth" )

  if [ -n "${YAWBLE_VERSION:-}" ]; then
    version="v${YAWBLE_VERSION#v}"
  else
    version=$(curl -fsSL -H @"$tmp/auth" -H "Accept: application/vnd.github+json" -H "User-Agent: yawble-install" \
      "$api/repos/$repo/releases?per_page=100" | newest_tag) \
      || { echo "yawble: could not read the releases of $repo with GH_TOKEN set (does the token have the repo scope?)" >&2; exit 1; }
    [ -n "$version" ] || { echo "yawble: $repo has no ${YAWBLE_CHANNEL:+$YAWBLE_CHANNEL }release yet" >&2; exit 1; }
  fi
  curl -fsSL -H @"$tmp/auth" -H "Accept: application/vnd.github+json" -H "User-Agent: yawble-install" \
    -o "$tmp/release.json" "$api/repos/$repo/releases/tags/$version" \
    || { echo "yawble: could not read the release $version of $repo with GH_TOKEN set (does the token have the repo scope?)" >&2; exit 1; }
else
  if [ -n "${YAWBLE_VERSION:-}" ]; then
    # With or without the leading v; the tag has it.
    version="v${YAWBLE_VERSION#v}"
  else
    list=$(curl -fsSL "$api/repos/$repo/releases?per_page=100") \
      || { echo "yawble: could not read the releases of $repo (check the network connection; a private fork also needs GH_TOKEN)" >&2; exit 1; }
    version=$(printf '%s' "$list" | newest_tag)
    if [ -z "$version" ] && [ "${YAWBLE_CHANNEL:-}" = stable ]; then
      echo "yawble: $repo has no regular release yet, only pre-releases; run without YAWBLE_CHANNEL=stable to take the newest" >&2; exit 1
    fi
    [ -n "$version" ] || { echo "yawble: $repo has no release yet" >&2; exit 1; }
  fi
fi
bare="${version#v}"
asset="yawble_${bare}_${os}_${arch}.tar.gz"
base="https://github.com/$repo/releases/download/$version"

# asset_api_url NAME: the API URL of the release asset called NAME. An asset object lists its url
# (.../releases/assets/<id>) before its name; the release's own url and the uploader's do not
# match /releases/assets/, so the last matching url before the name is that asset's.
asset_api_url() {
  tr ',{}[]' '\n\n\n\n\n' < "$tmp/release.json" | awk -v want="\"$1\"" '
    /"url": *"[^"]*\/releases\/assets\/[0-9]+"/ { u = $0; sub(/.*"url": *"/, "", u); sub(/".*/, "", u) }
    /"name":/ { n = $0; sub(/.*"name": */, "", n); sub(/ *$/, "", n); if (n == want && u != "") { print u; exit } }'
}

# download NAME OUT: the public URL without a token; with one, the API URL, whose redirect to
# storage is followed here, by a second request that carries no token.
download() {
  if [ -z "$token" ]; then
    curl -fsSL -o "$2" "$base/$1"
    return
  fi
  url=$(asset_api_url "$1")
  [ -n "$url" ] || { echo "yawble: release $version has no asset $1" >&2; exit 1; }
  location=$(curl -fsS -H @"$tmp/auth" -H "Accept: application/octet-stream" -H "User-Agent: yawble-install" \
    -o "$2" -w '%{redirect_url}' "$url")
  if [ -n "$location" ]; then
    curl -fsSL -o "$2" "$location"
  fi
}

echo "Downloading yawble $version for $os/$arch"
download "$asset" "$tmp/$asset"
download checksums.txt "$tmp/checksums.txt"

# The checksum file is the release's own statement of what it shipped; a download that does not
# match it is deleted and nothing is installed.
# Accepts "hash  name" and coreutils' binary-mode "hash *name".
expected=$(awk -v a="$asset" '$2==a || $2=="*"a {print $1}' "$tmp/checksums.txt" | head -n 1)
[ -n "$expected" ] || { echo "yawble: $asset is not in checksums.txt" >&2; exit 1; }
if command -v sha256sum >/dev/null 2>&1; then
  actual=$(sha256sum "$tmp/$asset" | cut -d' ' -f1)
else
  actual=$(shasum -a 256 "$tmp/$asset" | cut -d' ' -f1)
fi
[ "$expected" = "$actual" ] || { echo "yawble: checksum mismatch for $asset; nothing installed" >&2; exit 1; }

mkdir -p "$dir"
tar -xzf "$tmp/$asset" -C "$tmp" yawble
install -m 0755 "$tmp/yawble" "$dir/yawble"
echo "Installed $dir/yawble ($("$dir/yawble" version | head -n 1))"
# The channel travels with the install, so `yawble update` keeps to the same kind of release.
if [ "${YAWBLE_CHANNEL:-}" = stable ]; then
  "$dir/yawble" config set channel stable >/dev/null && echo "Channel: stable (yawble update takes regular releases only; yawble config set channel latest undoes it)"
fi

case ":$PATH:" in
  *":$dir:"*) ;;
  *)
    if [ "$os" = darwin ]; then
      # zsh is the macOS shell: ~/.zprofile is read by every new Terminal window. Added once;
      # this piped shell cannot change the caller's PATH, so the line to run now is printed too.
      line="export PATH=\"$dir:\$PATH\""
      if ! grep -qsF "$line" "$HOME/.zprofile"; then
        printf '\n# yawble\n%s\n' "$line" >> "$HOME/.zprofile"
        echo "Added $dir to your PATH in ~/.zprofile (new Terminal windows will see it)."
      fi
      echo "For this window, run:  export PATH=\"$dir:\$PATH\""
    else
      echo "Add it to your PATH, for example in ~/.profile:"
      echo "    export PATH=\"$dir:\$PATH\""
    fi
    ;;
esac
# A container engine first. yawble installs none: it uses Podman or Docker. With
# neither installed, say where to get one - Podman recommended - and to run `yawble up` after.
if command -v podman >/dev/null 2>&1 || command -v docker >/dev/null 2>&1; then
  echo "Next: yawble up"
else
  echo
  echo "No container engine is installed. Yawble runs in one; install either, then run: yawble up"
  if [ "$os" = darwin ]; then
    echo "  Podman Desktop (recommended, free for everyone):  https://podman-desktop.io"
    echo "  Docker Desktop (free for personal use and small businesses):  https://www.docker.com/products/docker-desktop"
  else
    echo "  Podman (recommended):  https://podman.io/docs/installation"
    echo "  Docker Engine:  https://docs.docker.com/engine/install"
  fi
fi
