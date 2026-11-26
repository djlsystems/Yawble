# Prints where a test run spent its time, from a TRX result file: the slowest tests and the total per test class.
#
#   dotnet test --project core/tests/Yawble.Core.Tests -- --report-trx --report-trx-filename core.trx --results-directory ~/yawble-runs
#   pwsh scripts/test-times.ps1 ~/yawble-runs/core.trx
#
# -Top sets how many rows each list shows (30 by default). Class totals add up test durations, so with classes
# running in parallel they sum to more than the run's wall time.
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Trx,
    [int]$Top = 30
)
$ErrorActionPreference = 'Stop'

$path = (Resolve-Path -LiteralPath $Trx).Path
$doc = New-Object System.Xml.XmlDocument
$doc.Load($path)
$ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
$ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')

# test id -> class name (a result row carries only the full test name, which for a theory row includes its arguments)
$classOf = @{}
foreach ($test in $doc.SelectNodes('//t:TestDefinitions/t:UnitTest', $ns)) {
    $method = $test.SelectSingleNode('t:TestMethod', $ns)
    if ($method -ne $null) { $classOf[$test.GetAttribute('id')] = $method.GetAttribute('className') }
}

$rows = New-Object System.Collections.Generic.List[object]
foreach ($result in $doc.SelectNodes('//t:Results/t:UnitTestResult', $ns)) {
    $seconds = 0.0
    $duration = $result.GetAttribute('duration')
    if ($duration) { $seconds = [TimeSpan]::Parse($duration, [Globalization.CultureInfo]::InvariantCulture).TotalSeconds }
    $class = $classOf[$result.GetAttribute('testId')]
    if (-not $class) { $class = '(unknown class)' }
    $rows.Add([pscustomobject]@{
        Seconds = $seconds
        Outcome = $result.GetAttribute('outcome')
        Class   = $class
        Test    = $result.GetAttribute('testName')
    })
}
if ($rows.Count -eq 0) { Write-Output "no test results in $path"; exit 1 }

function Format-Seconds([double]$s) { $s.ToString('0.00', [Globalization.CultureInfo]::InvariantCulture).PadLeft(9) }

$sum = ($rows | Measure-Object -Property Seconds -Sum).Sum
Write-Output "$($rows.Count) tests, $((Format-Seconds $sum).Trim()) s of test time in $path"
Write-Output ''
Write-Output "The $Top slowest tests (seconds)"
foreach ($row in ($rows | Sort-Object -Property Seconds -Descending | Select-Object -First $Top)) {
    $mark = ''
    if ($row.Outcome -ne 'Passed') { $mark = "  [$($row.Outcome)]" }
    Write-Output "$(Format-Seconds $row.Seconds)  $($row.Test)$mark"
}

Write-Output ''
Write-Output "The $Top slowest classes (seconds, tests)"
$classes = $rows | Group-Object -Property Class | ForEach-Object {
    [pscustomobject]@{ Seconds = ($_.Group | Measure-Object -Property Seconds -Sum).Sum; Count = $_.Count; Class = $_.Name }
}
foreach ($class in ($classes | Sort-Object -Property Seconds -Descending | Select-Object -First $Top)) {
    Write-Output "$(Format-Seconds $class.Seconds)  $($class.Count.ToString().PadLeft(5))  $($class.Class)"
}
