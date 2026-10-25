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
#
# The SQL Server tests need a server. Unless HARNESS_TEST_SQLSERVER is already set (then that server is
# used and nothing is started), a SQL Server 2022 container is started on a private network shared with
# the test container, with a password generated for this run only, and removed when the script exits.
set -eu

# Git Bash on Windows rewrites arguments that look like POSIX paths (-w /src becomes a path under
# the Git install). Both variables stop that; they are harmless elsewhere.
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'

if [ ! -f Harness.slnx ]; then
  echo "Run this from the repository root." >&2; exit 2
fi

sql_name=""
sql_network=""
cleanup() {
  [ -n "$sql_name" ] && podman rm -f "$sql_name" >/dev/null 2>&1
  [ -n "$sql_network" ] && podman network rm -f "$sql_network" >/dev/null 2>&1
  return 0
}
trap cleanup EXIT
trap 'exit 130' INT TERM

network_args=""
if [ -z "${HARNESS_TEST_SQLSERVER:-}" ]; then
  sql_name="harness-test-sql-$$"
  sql_network="harness-test-net-$$"
  # Complexity rules want upper, lower, digit and symbol; the random tail is what makes it unguessable.
  sql_password="Yw1!$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')"

  podman network create "$sql_network" >/dev/null
  podman run -d --name "$sql_name" --network "$sql_network" \
      --memory 3g -e MSSQL_MEMORY_LIMIT_MB=2048 \
      -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="$sql_password" \
      mcr.microsoft.com/mssql/server:2022-latest >/dev/null

  echo "Waiting for SQL Server..." >&2
  ready=""
  for _ in $(seq 1 90); do
    if podman exec "$sql_name" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$sql_password" -Q "SELECT 1" >/dev/null 2>&1; then
      ready=1; break
    fi
    sleep 2
  done
  if [ -z "$ready" ]; then
    echo "SQL Server did not accept connections in time." >&2; exit 1
  fi

  HARNESS_TEST_SQLSERVER="Server=$sql_name,1433;Database=master;User Id=sa;Password=$sql_password;TrustServerCertificate=True"
  network_args="--network $sql_network"
fi
export HARNESS_TEST_SQLSERVER

# The arguments, quoted for the shell inside the container.
args=""
for a in "$@"; do
  args="$args '$(printf '%s' "$a" | sed "s/'/'\\''/g")'"
done

tar --exclude=./.git \
    --exclude='./src/*/bin' --exclude='./src/*/obj' \
    --exclude='./tests/*/bin' --exclude='./tests/*/obj' \
    --exclude=./web/node_modules --exclude=./web/dist \
    --exclude=./.superpowers --exclude=./.claude \
    -cf - . \
| podman run --rm -i \
    -v harness-testsrc:/src \
    -v harness-nuget:/root/.nuget/packages \
    -w /src \
    $network_args -e HARNESS_TEST_SQLSERVER \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1     -e ASPNETCORE_URLS=http://0.0.0.0:8080 \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    sh -c "rm -rf /src/* /src/.[!.]* 2>/dev/null; tar -xf - \
        && dotnet build tests/Harness.Tests/Harness.Tests.csproj -v q \
        && dotnet tests/Harness.Tests/bin/Debug/net10.0/Harness.Tests.dll $args"
