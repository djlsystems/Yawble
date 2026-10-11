# Prints this checkout's version on stdout, e.g. 2026.09.23.1 or 2026.09.23.1+3.eed3fd0.
# No arguments. The rule lives in version.mjs, which the web and .NET builds use too, so this is a
# thin door onto it for PowerShell callers (dev-up.ps1, release.ps1, update.ps1).
# Exits non-zero, printing nothing on stdout, when git cannot describe the checkout.
$ErrorActionPreference = 'Stop'
& node (Join-Path $PSScriptRoot 'version.mjs') @args
exit $LASTEXITCODE
