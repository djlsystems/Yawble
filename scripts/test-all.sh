#!/usr/bin/env bash
# The repository's full test command (Linux, macOS, or Git Bash on Windows).
# .NET build + tests, the offline sandbox release gate, the release-function tests, and the shell's typecheck, build, unit and window tests.
# On Linux without a display the window test runs under xvfb-run; set YAWBLE_E2E_NO_SANDBOX=1 where Chromium's
# sandbox cannot start (containers, Ubuntu 24.04 AppArmor).
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

dotnet build Yawble.slnx -c Debug
dotnet test --solution Yawble.slnx -c Debug --no-build
dotnet run --project core/tools/Yawble.SandboxCheck --no-build -- ${YAWBLE_GATE_ARGS:-}
# The release script's offline functions (version numbers; tags passed in, no network).
if command -v pwsh >/dev/null 2>&1; then pwsh -NoProfile -File scripts/tests/release-functions.tests.ps1
elif command -v powershell.exe >/dev/null 2>&1; then powershell.exe -NoProfile -File scripts/tests/release-functions.tests.ps1
else echo "skipped scripts/tests/release-functions.tests.ps1: no PowerShell on PATH"; fi

cd shell
[ -d node_modules ] || npm ci
npm run typecheck
npm run build
npm test
if [ "$(uname -s)" = "Linux" ] && [ -z "${DISPLAY:-}" ] && [ -z "${WAYLAND_DISPLAY:-}" ]; then
  xvfb-run -a npm run test:e2e
else
  npm run test:e2e
fi
