#!/bin/sh
# Runs the test suite on Linux, which is the only platform the Host runs on. On a Windows or macOS
# development machine several tests fail for platform reasons alone (Unix file modes, setsid,
# process groups), so this is how "the suite passes" is established there. The working tree is
# copied into a named volume (bin, obj, node_modules and .git are left behind) and built inside the
# .NET SDK image; NuGet packages are cached in a second volume so the second run is fast.
#
#   scripts/test-in-container.sh                          the whole suite
#   scripts/test-in-container.sh --filter-class '*.HostDoctorTests'
#
# Any arguments are passed to the test dll (Microsoft.Testing.Platform syntax; --list-tests shows names).
set -eu

# Git Bash on Windows rewrites arguments that look like POSIX paths (-w /src becomes a path under
# the Git install). Both variables stop that; they are harmless elsewhere.
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'

if [ ! -f Harness.slnx ]; then
  echo "Run this from the repository root." >&2; exit 2
fi

# The arguments, quoted for the shell inside the container.
args=""
for a in "$@"; do
  args="$args '$(printf '%s' "$a" | sed "s/'/'\\''/g")'"
done

tar --exclude=./.git \
    --exclude='./src/*/bin' --exclude='./src/*/obj' \
    --exclude='./tests/*/bin' --exclude='./tests/*/obj' \
    --exclude=./web/node_modules --exclude=./web/dist \
    --exclude=./.superpowers \
    -cf - . \
| podman run --rm -i \
    -v harness-testsrc:/src \
    -v harness-nuget:/root/.nuget/packages \
    -w /src \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    sh -c "rm -rf /src/* /src/.[!.]* 2>/dev/null; tar -xf - \
        && dotnet build tests/Harness.Tests/Harness.Tests.csproj -v q \
        && dotnet tests/Harness.Tests/bin/Debug/net10.0/Harness.Tests.dll $args"
