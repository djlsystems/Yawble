# Cut a release from this checkout: check, test, tag v<version>, push the tag. Nothing else is published here.
#
#   scripts\release.ps1 -DryRun          # checks, the suites, and the version it would use; tags and pushes nothing
#   scripts\release.ps1                  # the same, then git tag -a v<version> and push that one tag to origin
#
# The version is yyyy.mm.dd.N (UTC date), the tag v<version>. N is 1 + the highest N among that day's release tags on
# origin. Never force-pushes; never moves an existing tag.
param(
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-functions.ps1')
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Invoke-Checked {
    param([string]$What, [scriptblock]$Command)
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

# 1. Clean, and HEAD is origin/main (the tag names exactly what main holds).
$dirty = @(git status --porcelain)
if ($dirty.Count -gt 0) { throw "Refusing: the checkout has uncommitted changes:`n$($dirty -join "`n")" }
$head = (git rev-parse HEAD).Trim()
$originMain = @(git ls-remote origin refs/heads/main)
if ($LASTEXITCODE -ne 0 -or $originMain.Count -eq 0) { throw 'Could not read main from origin.' }
$originSha = ($originMain[0] -split '\s+')[0]
if ($head -ne $originSha) { throw "Refusing: HEAD is $head but origin/main is $originSha. Check out origin/main (pull or push first)." }
Write-Host "Releasing $head (origin/main)."

# 2. The suites: the core, the shell's unit tests and its typecheck, and these scripts' own offline tests.
Write-Host '== Release functions =='
# The same PowerShell edition as this one, found by name: the current process may be a host that embeds PowerShell
# (e.g. dotnet.exe), which is not a shell that takes -File.
$posh = if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh' } else { 'powershell' }
$poshExe = Join-Path $PSHOME "$posh.exe"
if (-not (Test-Path -LiteralPath $poshExe)) { $poshExe = (Get-Command $posh -ErrorAction Stop).Source }
Invoke-Checked 'The release-function tests' { & $poshExe -NoProfile -File (Join-Path $PSScriptRoot 'tests\release-functions.tests.ps1') }
Write-Host '== Core suite =='
# A few core tests are timing-sensitive under full load. A failure in the full run is
# rerun ONCE on its own, by name; the release goes on only if every one of them then passes, and says which they were.
$coreTests = Join-Path $root 'core\tests\Yawble.Core.Tests'
$coreOut = @(dotnet test $coreTests 2>&1 | ForEach-Object { $_.ToString() } | Tee-Object -Variable coreLines)
$coreOut | Select-Object -Last 8 | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) {
    $failed = @(Get-FailedTestNames -Lines $coreOut)
    if ($failed.Count -eq 0 -or $failed.Count -gt 3) {
        throw "The core suite failed (exit code $LASTEXITCODE) with $($failed.Count) failing test(s) named; not retrying."
    }
    Write-Host "Rerunning alone, once: $($failed -join ', ')"
    $filter = ($failed | ForEach-Object { "FullyQualifiedName=$_" }) -join '|'
    Invoke-Checked 'The core tests rerun alone' { dotnet test $coreTests --no-build --filter $filter }
    Write-Warning "Passed when rerun alone (timing under full load): $($failed -join ', ')"
}
Write-Host '== Shell suite =='
Push-Location (Join-Path $root 'shell')
try {
    if (-not (Test-Path 'node_modules')) { Invoke-Checked 'npm ci' { npm ci --no-audit --no-fund } }
    Invoke-Checked 'The shell typecheck' { npm run typecheck }
    Invoke-Checked 'The shell unit tests' { npx vitest run }
} finally {
    Pop-Location
}

# 3. The next number, from origin's tags.
$lines = @(git ls-remote --tags origin 'refs/tags/v*')
if ($LASTEXITCODE -ne 0) { throw 'Could not read the tags on origin.' }
$names = @(Get-TagNames $lines)
$version = Get-NextReleaseVersion -Tags $names -Date ([datetime]::UtcNow)
$tag = "v$version"
if (Test-TagTaken -Names $names -Tag $tag) { throw "Refusing: $tag already exists; a release never moves a tag." }
if (@(git tag --list $tag).Count -gt 0) { throw "Refusing: $tag already exists in this checkout (not on origin). Delete the stray local tag or look into it first." }

if ($DryRun) {
    Write-Host ''
    Write-Host "Dry run. The suites passed. Next version: $version"
    Write-Host "Would tag      $tag (annotated, 'Release $version') on $head"
    Write-Host "Would push     refs/tags/$tag to origin (only that tag)"
    Write-Host "The app built from that commit shows $version (scripts/build-version.mjs)."
    return
}

# 4. Tag and push that one tag. A tag that exists is never replaced (no -f anywhere).
Invoke-Checked 'git tag' { git tag -a $tag -m "Release $version" $head }
git push origin "refs/tags/${tag}:refs/tags/$tag"
if ($LASTEXITCODE -ne 0) {
    $code = $LASTEXITCODE
    git tag -d $tag | Out-Null
    throw "The tag push failed (exit code $code). The local tag $tag was deleted again; nothing was released. Fix the push and run this again."
}

Write-Host ''
Write-Host "Released $version."
Write-Host "  Tag: $tag on $head"
