#!/bin/sh
# Builds one installable version of the plugin into <out> (default: this folder): the two static
# binaries the manifest's `platforms` names, plus the manifest and the skill.
#   ./build.sh ~/plugins-build/sample-whoami-go/0.1.0
set -e
here=$(cd "$(dirname "$0")" && pwd)
out=${1:-$here}
mkdir -p "$out/bin/linux-x64" "$out/bin/linux-arm64" "$out/skills"
out=$(cd "$out" && pwd)
cd "$here"
CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -trimpath -ldflags="-s -w" -o "$out/bin/linux-x64/sample-whoami-go" .
CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build -trimpath -ldflags="-s -w" -o "$out/bin/linux-arm64/sample-whoami-go" .
if [ "$out" != "$here" ]; then
  cp plugin.json "$out/"
  cp skills/sample-whoami-go.md "$out/skills/"
fi
