# Offline checks for scripts/release-functions.ps1. No network, no podman, no git remote.
# Run: pwsh scripts/tests/release-functions.tests.ps1   (exits non-zero on any failure)
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'release-functions.ps1')

$failures = 0
function Check([string]$Name, $Actual, $Expected) {
    if ("$Actual" -ceq "$Expected") {
        Write-Host "ok   $Name"
    } else {
        Write-Host "FAIL $Name`n     expected: $Expected`n     actual:   $Actual"
        $script:failures++
    }
}

$day = [datetime]'2026-09-23'

# Next version
Check 'no tags at all starts at .1' (Get-NextReleaseVersion -Tags @() -Date $day) '2026.09.23.1'
Check 'null tag list starts at .1' (Get-NextReleaseVersion -Tags $null -Date $day) '2026.09.23.1'
Check 'first release of the day, then the second' (Get-NextReleaseVersion -Tags @('v2026.09.23.1') -Date $day) '2026.09.23.2'
Check 'counts past the highest, not the count' (Get-NextReleaseVersion -Tags @('v2026.09.23.1', 'v2026.09.23.3') -Date $day) '2026.09.23.4'
Check 'N is numeric, .10 beats .9' (Get-NextReleaseVersion -Tags @('v2026.09.23.9', 'v2026.09.23.10') -Date $day) '2026.09.23.11'
Check 'other days do not count' (Get-NextReleaseVersion -Tags @('v2026.09.22.5', 'v2026.09.24.1') -Date $day) '2026.09.23.1'
Check 'ls-remote ref names and peeled refs are read' (Get-NextReleaseVersion -Tags @('refs/tags/v2026.09.23.1', 'refs/tags/v2026.09.23.1^{}', 'refs/tags/v2026.09.23.2') -Date $day) '2026.09.23.3'
Check 'tags that are not releases are ignored' (Get-NextReleaseVersion -Tags @('v1.2.3', '2026.09.23.7', 'v2026.9.23.4', 'v2026.09.23.1-rc', 'v2026.09.23') -Date $day) '2026.09.23.1'
Check 'month and day are zero-padded' (Get-NextReleaseVersion -Tags @() -Date ([datetime]'2027-01-05')) '2027.01.05.1'
# One number for core and CLI: release.ps1 passes both origins' tags, so a tag either repository holds is taken.
Check 'a tag the CLI already holds is taken' (Get-NextReleaseVersion -Tags (@('refs/tags/v2026.09.23.1') + @('refs/tags/v2026.09.23.2')) -Date $day) '2026.09.23.3'

# The same day, twice: the first run's tag is on origin when the second run looks.
$tags = @()
$first = Get-NextReleaseVersion -Tags $tags -Date $day
$tags += "refs/tags/v$first"
$second = Get-NextReleaseVersion -Tags $tags -Date $day
Check 'run twice on one day: first' $first '2026.09.23.1'
Check 'run twice on one day: second' $second '2026.09.23.2'

# Owner and repository from the origin remote, lowercased
$r = Get-GitHubRepository 'https://github.com/SomeOwner/Some-Repo'
Check 'https remote owner' $r.Owner 'someowner'
Check 'https remote repo' $r.Repo 'some-repo'
$r = Get-GitHubRepository 'https://github.com/SomeOwner/Some-Repo.git'
Check 'https remote with .git' "$($r.Owner)/$($r.Repo)" 'someowner/some-repo'
$r = Get-GitHubRepository 'git@github.com:SomeOwner/Some.Repo.git'
Check 'scp-style ssh remote' "$($r.Owner)/$($r.Repo)" 'someowner/some.repo'
$r = Get-GitHubRepository 'ssh://git@github.com/SomeOwner/Some-Repo.git/'
Check 'ssh url remote' "$($r.Owner)/$($r.Repo)" 'someowner/some-repo'
$r = Get-GitHubRepository 'https://x-access-token:abc@github.com/SomeOwner/Some-Repo'
Check 'credentials in the url are skipped' "$($r.Owner)/$($r.Repo)" 'someowner/some-repo'
Check 'a remote that is not GitHub is refused' (Get-GitHubRepository '/srv/git/some-repo.git') ''

# Agent CLIs among the container's processes (pid, cwd, argv joined with spaces)
$lines = @(
    "1`t/app`tdotnet /app/Harness.Host.dll",
    "13529`t/data/tenant-interactive-agent-workspaces/someone`tclaude --dangerously-skip-permissions --mcp-config /tmp/harness-mcp/concierge-43ea/mcp.json",
    "36429`t/data/teams/alpha/workspaces/Tester`t/data/npm-global/bin/claude -p --dangerously-skip-permissions",
    "36430`t/data/teams/alpha/workspaces/Tester`t/bin/bash -c claude --version",
    "40001`t/data/teams/beta/workspaces/Developer`tnode /data/npm-global/bin/codex exec --json",
    "40002`t/data/teams/beta/repos/Yawble/wt_Developer_1`t/data/agent-home/.grok/bin/grok --prompt x",
    "40003`t/`tgrep claude",
    "40004`t/data/teams/beta/workspaces/Developer`tnode /data/npm-global/lib/node_modules/some-mcp/index.js"
)
$agents = @(Get-AgentProcess -Lines $lines)
Check 'finds four agent CLIs' $agents.Count 4
Check 'a concierge is named as one' $agents[0].Who 'concierge'
Check 'a member is named team/member' $agents[1].Who 'alpha/Tester'
Check 'the cli is named' $agents[1].Cli 'claude'
Check 'a node-launched cli is found' "$($agents[2].Cli) $($agents[2].Who)" 'codex beta/Developer'
Check 'a cli outside a workspace is named by its folder' "$($agents[3].Cli) $($agents[3].Who)" 'grok /data/teams/beta/repos/Yawble/wt_Developer_1'
Check 'no agent: nothing' @(Get-AgentProcess -Lines @("1`t/app`tdotnet /app/Harness.Host.dll")).Count 0

if ($failures -gt 0) {
    Write-Host "$failures check(s) failed."
    exit 1
}
Write-Host 'All checks passed.'
