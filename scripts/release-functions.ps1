# Shared by release.ps1. Dot-source it. Every function here touches nothing outside its arguments (no git, no network),
# so scripts/tests/release-functions.tests.ps1 checks them offline with tags and git output passed in.
# Written for Windows PowerShell 5.1 as well as PowerShell 7.

# The next release version for a date, given the tags already on origin. A release tag is v<yyyy>.<mm>.<dd>.<N>;
# N counts that date's releases from 1. The date is taken in UTC. Accepts bare tag names or the ref names
# `git ls-remote --tags` prints, including peeled `^{}` lines. Anything else is ignored.
function Get-NextReleaseVersion {
    param([string[]]$Tags, [datetime]$Date)
    $utc = if ($Date.Kind -eq [DateTimeKind]::Local) { $Date.ToUniversalTime() } else { $Date }
    $day = $utc.ToString('yyyy.MM.dd', [Globalization.CultureInfo]::InvariantCulture)
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

# The tag names among `git ls-remote --tags <remote>` lines ("<sha>`trefs/tags/<name>"), peeled lines folded in.
function Get-TagNames {
    param([string[]]$Lines)
    $names = foreach ($line in @($Lines)) {
        if (-not $line) { continue }
        $ref = ($line.Trim() -split '\s+')[-1]
        $ref -replace '^refs/tags/', '' -replace '\^\{\}$', ''
    }
    return @($names | Where-Object { $_ } | Select-Object -Unique)
}

# What a build shows, from `git describe --tags --match "v2*" --long` (empty: no release tag before HEAD), the commit
# and whether the tree has uncommitted changes. The same rule as scripts/build-version.mjs (buildVersion):
#   2026.11.12.1                   the release's own commit, clean
#   2026.11.12.1+dev.ee55691       a later commit (the last release, + the short commit)
#   0.0.0+dev.ee55691              before the first release
#   ...+dev.ee55691.dirty          uncommitted changes
#   0.0.0+dev.unknown              no commit (no git)
function Get-BuildVersion {
    param([string]$Describe, [string]$Sha, [bool]$Dirty)
    $commit = "$Sha".Trim()
    if ($commit -notmatch '^[0-9a-f]{7,40}$') { return '0.0.0+dev.unknown' }
    $short = $commit.Substring(0, 7)
    $m = [regex]::Match("$Describe".Trim(), '^v(\d{4}\.\d{2}\.\d{2}\.\d+)(?:-(\d+)-g[0-9a-f]+)?$')
    $release = if ($m.Success) { $m.Groups[1].Value } else { '0.0.0' }
    $distance = if (-not $m.Success) { -1 } elseif ($m.Groups[2].Success) { [int]$m.Groups[2].Value } else { 0 }
    if ($m.Success -and $distance -eq 0 -and -not $Dirty) { return $release }
    return "$release+dev.$short$(if ($Dirty) { '.dirty' } else { '' })"
}

# Whether a tag already exists among the names (a release never moves a tag).
function Test-TagTaken {
    param([string[]]$Names, [string]$Tag)
    return @($Names) -contains $Tag
}

# The fully qualified names of the tests a `dotnet test` (Microsoft Testing Platform) run reports as failed, from its
# output lines ("failed Namespace.Class.Method(args) (1s 035ms)"). Theory arguments are dropped, so a rerun by name
# covers every case of that method; names are unique and in report order.
function Get-FailedTestNames {
    param([string[]]$Lines)
    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($line in @($Lines)) {
        if (-not $line) { continue }
        $plain = $line -replace "`e\[[0-9;]*m", ''
        $m = [regex]::Match($plain, '^\s*failed\s+([A-Za-z_][\w.]*)')
        if ($m.Success -and -not $names.Contains($m.Groups[1].Value)) { $names.Add($m.Groups[1].Value) }
    }
    return $names.ToArray()
}
