# Test history: every suite run's per-test results kept, and a report over all of them.
#
#   scripts/test-history.ps1 add -Suite dotnet -File <x.trx>        -Commit <sha> [-Source release]
#   scripts/test-history.ps1 add -Suite web    -File <vitest.json>  -Commit <sha>
#   scripts/test-history.ps1 add -Suite cli    -File <go-test.json> -Commit <sha>
#   scripts/test-history.ps1 report [-Suite dotnet] [-Top 25] [-Last 20]
#
# WHERE: `.test-history/` at the repository root (ignored by git; set YAWBLE_TEST_HISTORY to keep it
# elsewhere). `runs.jsonl` holds one line per run (when, commit, suite, source, counts, summed and
# wall seconds); `runs/<run>.jsonl` holds one line per test (name, class, outcome, ms). Plain JSON
# lines, so any tool can read them and nothing here has to be kept in step with a database.
#
# WHAT THE REPORT ANSWERS, over every kept run of a suite (or the last -Last of them):
#   - the slowest tests and classes, by median and by worst run, so a one-off slow run does not
#     rank a test and a test that is always slow does;
#   - flakes: a test with both a pass and a fail on the SAME commit (the code did not change, the
#     outcome did), and tests that failed in a run where they passed in the run before and after;
#   - each run's wall time beside its summed test time, so a change to how tests are packed
#     (ordering, parallelism) is told apart from a change to what they cost.
#
# INPUTS are what each runner writes: Microsoft.Testing.Platform's TRX (`--report-trx`), Vitest's
# JSON reporter (`--reporter=json --outputFile=...`) and `go test -json`.
param(
    [Parameter(Position = 0, Mandatory)][ValidateSet('add', 'report')][string]$Command,
    [ValidateSet('dotnet', 'web', 'cli')][string]$Suite,
    [string]$File,
    [string]$Commit,
    [string]$Source = 'local',
    [int]$Top = 25,
    [int]$Last = 0
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$history = if ($env:YAWBLE_TEST_HISTORY) { $env:YAWBLE_TEST_HISTORY } else { Join-Path $root '.test-history' }
$runsFile = Join-Path $history 'runs.jsonl'
$runsDir = Join-Path $history 'runs'

function Read-Trx([string]$path) {
    [xml]$x = Get-Content -Raw -LiteralPath $path
    $ns = @{ t = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010' }
    $defs = @{}
    foreach ($u in (Select-Xml -Xml $x -XPath '//t:UnitTest' -Namespace $ns)) {
        $m = $u.Node.TestMethod
        $defs[$u.Node.id] = @{ class = ($m.className -split '\.')[-1]; name = $m.name }
    }
    $tests = foreach ($r in (Select-Xml -Xml $x -XPath '//t:UnitTestResult' -Namespace $ns)) {
        $n = $r.Node
        $d = $defs[$n.testId]
        $outcome = switch ($n.outcome) { 'Passed' { 'pass' } 'Failed' { 'fail' } default { 'skip' } }
        [pscustomobject]@{
            class   = if ($d) { $d.class } else { '' }
            test    = if ($d) { "$($d.class).$($d.name)" } else { $n.testName }
            outcome = $outcome
            ms      = [math]::Round(([TimeSpan]::Parse($n.duration)).TotalMilliseconds)
            start   = [DateTimeOffset]::Parse($n.startTime)
            end     = [DateTimeOffset]::Parse($n.endTime)
        }
    }
    $wall = if ($tests) { (($tests | Measure-Object -Property end -Maximum).Maximum - ($tests | Measure-Object -Property start -Minimum).Minimum).TotalSeconds } else { 0 }
    return @{ tests = @($tests | Select-Object class, test, outcome, ms); wall = $wall }
}

function Read-Vitest([string]$path) {
    $j = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
    $tests = foreach ($f in $j.testResults) {
        $file = ($f.name -replace '\\', '/') -replace '^.*?/web/', ''
        foreach ($a in $f.assertionResults) {
            $outcome = switch ($a.status) { 'passed' { 'pass' } 'failed' { 'fail' } default { 'skip' } }
            [pscustomobject]@{ class = $file; test = "$file > $($a.fullName)"; outcome = $outcome; ms = [math]::Round([double]($a.duration ?? 0)) }
        }
    }
    $wall = if ($j.startTime -and $j.testResults) {
        (($j.testResults | ForEach-Object { $_.endTime } | Measure-Object -Maximum).Maximum - $j.startTime) / 1000
    } else { 0 }
    return @{ tests = @($tests); wall = $wall }
}

function Read-GoTest([string]$path) {
    $tests = @()
    $packages = @{}
    foreach ($line in Get-Content -LiteralPath $path) {
        if (-not $line.StartsWith('{')) { continue }
        $e = $line | ConvertFrom-Json
        if (-not $e.Test) {
            if ($e.Action -in 'pass', 'fail' -and $e.Elapsed) { $packages[$e.Package] = [double]$e.Elapsed }
            continue
        }
        # A subtest is counted inside its parent, so only top-level tests are kept.
        if ($e.Test.Contains('/')) { continue }
        if ($e.Action -in 'pass', 'fail', 'skip') {
            $pkg = ($e.Package -split '/')[-1]
            $tests += [pscustomobject]@{ class = $pkg; test = "$pkg.$($e.Test)"; outcome = $e.Action; ms = [math]::Round([double]$e.Elapsed * 1000) }
        }
    }
    $wall = ($packages.Values | Measure-Object -Maximum).Maximum
    return @{ tests = $tests; wall = [double]($wall ?? 0) }
}

function Add-Run {
    if (-not $Suite -or -not $File -or -not $Commit) { throw 'add needs -Suite, -File and -Commit.' }
    if (-not (Test-Path -LiteralPath $File)) { throw "No results file at $File." }
    $read = switch ($Suite) { 'dotnet' { Read-Trx $File } 'web' { Read-Vitest $File } 'cli' { Read-GoTest $File } }
    $tests = $read.tests
    New-Item -ItemType Directory -Force $runsDir | Out-Null
    $at = (Get-Date).ToUniversalTime()
    $id = '{0}-{1}-{2}' -f $at.ToString('yyyyMMddTHHmmssZ'), $Suite, $Commit.Substring(0, [math]::Min(7, $Commit.Length))
    $lines = foreach ($t in $tests) { $t | ConvertTo-Json -Compress }
    [IO.File]::WriteAllLines((Join-Path $runsDir "$id.jsonl"), [string[]]$lines)
    $summary = [pscustomobject]@{
        id = $id; at = $at.ToString('o'); commit = $Commit; suite = $Suite; source = $Source
        tests = $tests.Count
        failed = @($tests | Where-Object outcome -eq 'fail').Count
        skipped = @($tests | Where-Object outcome -eq 'skip').Count
        summedSeconds = [math]::Round((($tests | Measure-Object -Property ms -Sum).Sum ?? 0) / 1000, 1)
        wallSeconds = [math]::Round($read.wall, 1)
    }
    [IO.File]::AppendAllText($runsFile, ($summary | ConvertTo-Json -Compress) + "`n")
    Write-Host ("Kept {0}: {1} tests, {2} failed, {3}s summed, {4}s wall -> {5}" -f $id, $summary.tests, $summary.failed, $summary.summedSeconds, $summary.wallSeconds, $history)
}

function Get-Median([double[]]$values) {
    $s = $values | Sort-Object
    if ($s.Count -eq 0) { return 0 }
    $mid = [int][math]::Floor($s.Count / 2)
    if ($s.Count % 2) { return $s[$mid] } else { return ($s[$mid - 1] + $s[$mid]) / 2 }
}

function Show-Report {
    if (-not (Test-Path -LiteralPath $runsFile)) { Write-Host "No runs kept yet in $history."; return }
    $runs = @(Get-Content -LiteralPath $runsFile | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    $suites = if ($Suite) { @($Suite) } else { @($runs.suite | Sort-Object -Unique) }
    foreach ($s in $suites) {
        $mine = @($runs | Where-Object suite -eq $s | Sort-Object at)
        if ($Last -gt 0) { $mine = @($mine | Select-Object -Last $Last) }
        if ($mine.Count -eq 0) { continue }
        Write-Host ''
        Write-Host "===== ${s}: $($mine.Count) run(s) kept ====="
        Write-Host 'Runs (newest last): when, commit, source, tests, failed, summed s, wall s'
        foreach ($r in ($mine | Select-Object -Last 10)) {
            Write-Host ('  {0}  {1}  {2,-7} {3,5} {4,3} {5,8} {6,7}' -f ([datetime]$r.at).ToUniversalTime().ToString('yyyy-MM-dd HH:mm'), $r.commit.Substring(0, 7), $r.source, $r.tests, $r.failed, $r.summedSeconds, $r.wallSeconds)
        }

        # Every test row of these runs, tagged with its run's commit and order.
        $rows = foreach ($i in 0..($mine.Count - 1)) {
            $r = $mine[$i]
            $path = Join-Path $runsDir "$($r.id).jsonl"
            if (-not (Test-Path -LiteralPath $path)) { continue }
            foreach ($line in [IO.File]::ReadLines($path)) {
                $t = $line | ConvertFrom-Json
                [pscustomobject]@{ run = $i; commit = $r.commit; class = $t.class; test = $t.test; outcome = $t.outcome; ms = [double]$t.ms }
            }
        }
        $byTest = $rows | Group-Object test

        Write-Host ''
        Write-Host "Slowest tests (median of passing runs; worst; runs)"
        $byTest | ForEach-Object {
            $ms = @($_.Group | Where-Object outcome -eq 'pass' | ForEach-Object ms)
            if ($ms.Count) { [pscustomobject]@{ test = $_.Name; median = (Get-Median $ms); worst = ($ms | Measure-Object -Maximum).Maximum; n = $ms.Count } }
        } | Sort-Object median -Descending | Select-Object -First $Top | ForEach-Object {
            Write-Host ('  {0,7:N1}s {1,7:N1}s {2,3}  {3}' -f ($_.median / 1000), ($_.worst / 1000), $_.n, $_.test)
        }

        Write-Host ''
        Write-Host "Slowest classes or files (median summed seconds per run; tests)"
        $rows | Group-Object class | ForEach-Object {
            $perRun = @($_.Group | Group-Object run | ForEach-Object { ($_.Group | Measure-Object -Property ms -Sum).Sum })
            [pscustomobject]@{ class = $_.Name; median = (Get-Median $perRun); tests = @($_.Group.test | Sort-Object -Unique).Count }
        } | Sort-Object median -Descending | Select-Object -First $Top | ForEach-Object {
            Write-Host ('  {0,8:N1}s {1,4}  {2}' -f ($_.median / 1000), $_.tests, $_.class)
        }

        Write-Host ''
        Write-Host 'Flakes'
        $flaky = $byTest | ForEach-Object {
            $g = $_.Group
            $sameCommit = @($g | Group-Object commit | Where-Object { ($_.Group.outcome -contains 'pass') -and ($_.Group.outcome -contains 'fail') }).Count
            $ordered = @($g | Sort-Object run)
            $blips = 0
            for ($k = 1; $k -lt $ordered.Count - 1; $k++) {
                if ($ordered[$k].outcome -eq 'fail' -and $ordered[$k - 1].outcome -eq 'pass' -and $ordered[$k + 1].outcome -eq 'pass') { $blips++ }
            }
            $fails = @($g | Where-Object outcome -eq 'fail').Count
            if ($sameCommit -or $blips) { [pscustomobject]@{ test = $_.Name; fails = $fails; runs = $g.Count; sameCommit = $sameCommit; blips = $blips } }
        }
        if (-not $flaky) { Write-Host '  none seen (a flake shows once a test both passes and fails on one commit, or fails between two passes)' }
        foreach ($f in ($flaky | Sort-Object fails -Descending)) {
            Write-Host ('  failed {0} of {1} runs; mixed on {2} commit(s); {3} fail(s) between passes  {4}' -f $f.fails, $f.runs, $f.sameCommit, $f.blips, $f.test)
        }

        $failing = $byTest | Where-Object { ($_.Group | Sort-Object run | Select-Object -Last 1).outcome -eq 'fail' }
        if ($failing) {
            Write-Host ''
            Write-Host 'Failing in the newest run'
            foreach ($f in $failing) { Write-Host "  $($f.Name)" }
        }
    }
}

switch ($Command) {
    'add' { Add-Run }
    'report' { Show-Report }
}
