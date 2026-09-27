#!/bin/sh
# Builds the CLI at a release tag and publishes it: cross-compiles every target, writes checksums,
# and creates the tag's GitHub Release with those assets. No hosted CI; this is the release.
#
#   cli/scripts/release.sh v2026.09.24.1      (run from cli/)
#
# ONE VERSION, ONE REPOSITORY. The CLI lives in cli/ of Yawble, so the tag scripts/release.ps1
# just cut names the core commit and this build together; the binary pins the image of the same
# version (the tag without its v). release.ps1 runs this itself as its last step, after the image
# is pushed, so the Release appears once with both the notes and the binaries; `latest` on GitHub
# is therefore never a Release without a CLI to download. By hand it only finishes a release
# whose last step failed. A CLI is released only against an image that exists, and the image's
# `latest` is never pinned.
set -eu

version="${1:-}"
# --prerelease marks the GitHub Release as a pre-release. The installers and `yawble update` take
# the newest release either way; GitHub's own "latest" label skips pre-releases.
prerelease=""
[ "${2:-}" = "--prerelease" ] && prerelease="--prerelease"
printf '%s' "$version" | grep -Eq '^v[0-9]{4}\.[0-9]{2}\.[0-9]{2}\.[1-9][0-9]*$' || { echo "usage: scripts/release.sh v<yyyy.mm.dd.N>, the tag scripts/release.ps1 cut" >&2; exit 2; }
image_tag="${version#v}"
[ -f go.mod ] || { echo "run from cli/" >&2; exit 2; }
[ -z "$(git status --porcelain)" ] || { echo "the working tree is not clean" >&2; exit 1; }
git rev-parse -q --verify "refs/tags/$version" >/dev/null || { echo "no tag $version; create it: git tag -a $version -m $version" >&2; exit 1; }
[ "$(git rev-parse HEAD)" = "$(git rev-parse "$version^{commit}")" ] || { echo "HEAD is not $version" >&2; exit 1; }
command -v gh >/dev/null || { echo "gh is needed to create the release" >&2; exit 1; }
command -v go >/dev/null || { echo "go is needed to build" >&2; exit 1; }

# No hosted CI: this is the only gate a release passes through.
go vet ./... && go test ./... || { echo "vet or tests failed; nothing released" >&2; exit 1; }

# The image must exist before a CLI pins it, and on the registry, not only in the local store the
# build just filled. Asked of GitHub's packages API: Podman 6 refuses the docker:// transport for
# `podman manifest inspect`, and without it the command reads the local manifest, which proves
# nothing about the push.
# djlsystems is a user account, so the path is users/, not orgs/; needs read:packages.
if ! gh api --paginate "users/djlsystems/packages/container/yawble/versions"     --jq '.[].metadata.container.tags[]' 2>/dev/null | grep -qx "$image_tag"; then
  echo "ghcr.io/djlsystems/yawble:$image_tag is not on ghcr.io (or the gh token lacks read:packages); release the image first" >&2
  exit 1
fi

commit=$(git rev-parse --short HEAD)
mod=github.com/djlsystems/yawble/cli/internal/buildinfo
ldflags="-s -w -X $mod.Version=$version -X $mod.Commit=$commit -X $mod.ImageTag=$image_tag"
rm -rf dist && mkdir -p dist
bare="${version#v}"
# macOS is Apple silicon only: no darwin/amd64 build is offered.
for target in linux/amd64 linux/arm64 darwin/arm64 windows/amd64 windows/arm64; do
  os="${target%/*}"
  arch="${target#*/}"
  out="dist/yawble"
  [ "$os" = windows ] && out="dist/yawble.exe"
  echo "building $target"
  CGO_ENABLED=0 GOOS="$os" GOARCH="$arch" go build -trimpath -ldflags "$ldflags" -o "$out" ./cmd/yawble
  if [ "$os" = windows ]; then
    # Git for Windows ships no `zip`; Go does, in effect.
    go run ./scripts/zip "dist/yawble_${bare}_${os}_${arch}.zip" dist/yawble.exe && rm dist/yawble.exe
  else
    (cd dist && tar -czf "yawble_${bare}_${os}_${arch}.tar.gz" yawble && rm yawble)
  fi
done
# Two spaces between hash and name, always. MSYS sha256sum switches to binary mode (" *name")
# when stdout is not a terminal, and the install script greps for the name after a space.
(cd dist && (sha256sum yawble_* 2>/dev/null || shasum -a 256 yawble_*) | sed 's/ \*/  /' > checksums.txt)
grep -q ' \*' dist/checksums.txt && { echo "checksums.txt still carries binary-mode markers" >&2; exit 1; }

notes="Image: ghcr.io/djlsystems/yawble:$image_tag. CLI: the yawble_* archives below, which pin that image."
if gh release view "$version" >/dev/null 2>&1; then
  # Finishing by hand a release whose Release was already created: replace the assets only.
  gh release upload "$version" dist/* --clobber
  [ -n "$prerelease" ] && gh release edit "$version" --prerelease
else
  gh release create "$version" dist/* --verify-tag --generate-notes --notes "$notes" $prerelease
fi
echo "Released $version, pinning core image $image_tag."
