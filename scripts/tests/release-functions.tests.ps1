# Offline checks for scripts/release-functions.ps1. No network, no git, no remote: tags and git output are passed in.
# Run: pwsh scripts/tests/release-functions.tests.ps1   (exits non-zero on any failure; scripts/test-all.sh runs it)
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'release-functions.ps1')

$failures = 0
$passed = 0
function Check([string]$Name, $Actual, $Expected) {
    if ("$Actual" -ceq "$Expected") {
        Write-Host "ok   $Name"
        $script:passed++
    } else {
        Write-Host "FAIL $Name`n     expected: $Expected`n     actual:   $Actual"
        $script:failures++
    }
}

$day = [datetime]'2026-11-12'

# Next version (one repository's tags, then the two-repository and UTC cases)
Check 'no tags at all starts at .1' (Get-NextReleaseVersion -Tags @() -Date $day) '2026.11.12.1'
Check 'null tag list starts at .1' (Get-NextReleaseVersion -Tags $null -Date $day) '2026.11.12.1'
Check 'first release of the day, then the second' (Get-NextReleaseVersion -Tags @('v2026.11.12.1') -Date $day) '2026.11.12.2'
Check 'counts past the highest, not the count' (Get-NextReleaseVersion -Tags @('v2026.11.12.1', 'v2026.11.12.3') -Date $day) '2026.11.12.4'
Check 'N is numeric, .10 beats .9' (Get-NextReleaseVersion -Tags @('v2026.11.12.9', 'v2026.11.12.10') -Date $day) '2026.11.12.11'
Check 'other days do not count' (Get-NextReleaseVersion -Tags @('v2026.11.11.5', 'v2026.11.13.1') -Date $day) '2026.11.12.1'
Check 'ls-remote ref names and peeled refs are read' (Get-NextReleaseVersion -Tags @('refs/tags/v2026.11.12.1', 'refs/tags/v2026.11.12.1^{}', 'refs/tags/v2026.11.12.2') -Date $day) '2026.11.12.3'
Check 'tags that are not releases are ignored' (Get-NextReleaseVersion -Tags @('v1.2.3', '2026.11.12.7', 'v2026.11.2.4', 'v2026.11.12.1-rc', 'v2026.11.12') -Date $day) '2026.11.12.1'
Check 'month and day are zero-padded' (Get-NextReleaseVersion -Tags @() -Date ([datetime]'2027-01-05')) '2027.01.05.1'
Check 'a tag listed twice counts once' (Get-NextReleaseVersion -Tags @('refs/tags/v2026.11.12.1', 'refs/tags/v2026.11.12.1') -Date $day) '2026.11.12.2'
$lateUtc = [datetime]::SpecifyKind([datetime]'2026-11-12T23:30:00', [DateTimeKind]::Utc)
Check 'a UTC date is used as it is' (Get-NextReleaseVersion -Tags @() -Date $lateUtc) '2026.11.12.1'
$local = $lateUtc.ToLocalTime()
Check 'a local time is turned into its UTC date' (Get-NextReleaseVersion -Tags @() -Date $local) '2026.11.12.1'

# The same day, twice: the first run's tag is on origin when the second run looks.
$tags = @()
$first = Get-NextReleaseVersion -Tags $tags -Date $day
$tags += "refs/tags/v$first"
$second = Get-NextReleaseVersion -Tags $tags -Date $day
Check 'run twice on one day: first' $first '2026.11.12.1'
Check 'run twice on one day: second' $second '2026.11.12.2'

# Tag names from ls-remote output
$lines = @("abc`trefs/tags/v2026.11.12.1", "def`trefs/tags/v2026.11.12.1^{}", "123`trefs/tags/other", '')
Check 'ls-remote lines give unique tag names' ((Get-TagNames $lines) -join ',') 'v2026.11.12.1,other'
Check 'no ls-remote lines give none' (@(Get-TagNames @()).Count) 0
Check 'a taken tag is seen' (Test-TagTaken -Names @('v2026.11.12.1') -Tag 'v2026.11.12.1') 'True'
Check 'a free tag is free' (Test-TagTaken -Names @('v2026.11.12.1') -Tag 'v2026.11.12.2') 'False'

# Build versions: the same cases scripts/build-version.mjs is tested with (shell/tests/unit/build-version.test.ts).
$cases = Get-Content -Raw (Join-Path $PSScriptRoot 'build-version-cases.json') | ConvertFrom-Json
foreach ($c in $cases) {
    Check "build version: $($c.name)" (Get-BuildVersion -Describe $c.describe -Sha $c.sha -Dirty ([bool]$c.dirty)) $c.version
}

# Failed test names from dotnet test output (the release reruns them alone, once)
$out = @(
    '  passed Yawble.Core.Tests.A.B.Ok (12ms)',
    'failed Yawble.Core.Tests.Scheduling.TeamsUnderOneLimitTests.TwoTeams_Oldest (5s 792ms)',
    "`e[31mfailed`e[0m Yawble.Core.Tests.Teams.TeamFlowTests.Flow(realBridgeProcess: True) (1s 2ms)",
    'failed Yawble.Core.Tests.Teams.TeamFlowTests.Flow(realBridgeProcess: False) (1s 2ms)',
    '  failed: 3',
    '  total: 1977'
)
Check 'failed names: plain, coloured, theory args dropped, unique' ((Get-FailedTestNames -Lines $out) -join ',') 'Yawble.Core.Tests.Scheduling.TeamsUnderOneLimitTests.TwoTeams_Oldest,Yawble.Core.Tests.Teams.TeamFlowTests.Flow'
Check 'failed names: two names stay two names when wrapped in @()' (@(Get-FailedTestNames -Lines @('failed A.B.C (1s)', 'failed D.E.F (2s)')).Count) '2'
Check 'failed names: none in a passing run' ((Get-FailedTestNames -Lines @('  failed: 0', '  total: 10')).Count) '0'

Write-Host ''
Write-Host "$passed passed, $failures failed"
if ($failures -gt 0) { exit 1 }
