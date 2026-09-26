# Shared by release.ps1 and update.ps1. Dot-source it. The first three functions touch nothing
# outside their arguments, so scripts/tests/release-functions.tests.ps1 checks them offline.
# Written for Windows PowerShell 5.1 as well as PowerShell 7.

# The next release version for a date, given the tags already on origin. A release tag is
# v<yyyy>.<mm>.<dd>.<N>; N counts that date's releases from 1. Accepts bare tag names or the
# ref names `git ls-remote --tags` prints, including peeled `^{}` lines. Anything else is ignored.
function Get-NextReleaseVersion {
    param([string[]]$Tags, [datetime]$Date)
    $day = $Date.ToString('yyyy.MM.dd')
    $highest = 0
    foreach ($tag in @($Tags)) {
        if (-not $tag) { continue }
        $name = $tag.Trim() -replace '^refs/tags/', '' -replace '\^\{\}$', ''
        $m = [regex]::Match($name, '^v(\d{4}\.\d{2}\.\d{2})\.(\d+)$')
        if ($m.Success -and $m.Groups[1].Value -eq $day) {
            $n = [int]$m.Groups[2].Value
            if ($n -gt $highest) { $highest = $n }
        }
    }
    return "$day.$($highest + 1)"
}

# Owner and repository of a GitHub remote URL, lowercased as ghcr.io requires. $null when the
# URL is not a GitHub one.
function Get-GitHubRepository {
    param([string]$Url)
    $m = [regex]::Match("$Url".Trim(), '^(?:https?://(?:[^@/]+@)?|ssh://git@|git@)github\.com[:/]([^/]+)/([^/]+?)(?:\.git)?/?$')
    if (-not $m.Success) { return $null }
    return [pscustomobject]@{
        Owner = $m.Groups[1].Value.ToLowerInvariant()
        Repo  = $m.Groups[2].Value.ToLowerInvariant()
    }
}

# The agent CLIs among a process listing. Each line is "<pid>`t<cwd>`t<argv joined by spaces>",
# which is what $AgentProcessListing prints inside the container. A CLI is found by the file
# name of the command, or of the script node was handed. Who is the team/member whose
# workspace the process runs in, "concierge" for the tenant Concierge, else its folder.
$AgentCliNames = @('claude', 'codex', 'copilot', 'grok', 'agy')
function Get-AgentProcess {
    param([string[]]$Lines)
    foreach ($line in @($Lines)) {
        if (-not $line) { continue }
        $parts = $line -split "`t", 3
        if ($parts.Count -lt 3) { continue }
        $argv = @($parts[2].Trim() -split '\s+')
        $cli = $null
        foreach ($candidate in @($argv | Select-Object -First 2)) {
            $leaf = ($candidate -split '/')[-1]
            if ($AgentCliNames -contains $leaf) { $cli = $leaf; break }
            if ($leaf -ne 'node') { break }
        }
        if (-not $cli) { continue }
        $cwd = $parts[1]
        $member = [regex]::Match($cwd, '/teams/([^/]+)/workspaces/([^/]+)')
        if ($member.Success) {
            $who = "$($member.Groups[1].Value)/$($member.Groups[2].Value)"
        } elseif ($cwd -like '*/tenant-interactive-agent-workspaces/*') {
            $who = 'concierge'
        } else {
            $who = $cwd
        }
        [pscustomobject]@{ Pid = [int]$parts[0]; Cli = $cli; Who = $who }
    }
}

# Run inside the container with `sh -c`: one line per process, pid TAB cwd TAB argv.
$AgentProcessListing = 'for p in /proc/[0-9]*; do a=$(tr "\000" " " < "$p/cmdline" 2>/dev/null); [ -n "$a" ] || continue; printf "%s\t%s\t%s\n" "${p#/proc/}" "$(readlink "$p/cwd" 2>/dev/null)" "$a"; done'

# A shell script as one `sh -c` argument with no quotes in it. Windows PowerShell 5.1 strips the
# double quotes inside an argument it hands to a native command, so the script travels as base64.
function ConvertTo-ShArgument {
    param([string]$Script)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Script -replace "`r", ''))
    return "echo $([Convert]::ToBase64String($bytes)) | base64 -d | sh"
}

# Run a native command and stop on a non-zero exit. Windows PowerShell 5.1 does not do that by
# itself, whatever $ErrorActionPreference says.
function Invoke-Checked {
    param([string]$What, [scriptblock]$Command)
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

# The checkout's branch, or the reason it cannot be used. Shared refusal for both scripts.
function Assert-CleanMain {
    param([string]$Root)
    $branch = (git -C $Root rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne 'main') { throw "Refusing: the checkout is on '$branch', not main." }
    $dirty = @(git -C $Root status --porcelain)
    if ($dirty.Count -gt 0) {
        throw "Refusing: the checkout has uncommitted changes:`n$($dirty -join "`n")"
    }
}
