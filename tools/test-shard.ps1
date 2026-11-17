# Runs one shard of the integration tests. CI runs every shard at once (.github/workflows/ci.yml).
# Each test class runs in exactly one shard. Classes are dealt out by how long they took (tools/test-times.json, seconds of test
# time summed over the class's tests), longest first, each to the shard with the least so far, so the shards end at about the same
# time. A class missing from the file counts as the median, so a new class needs nothing; refresh the file when the shards drift
# apart: tools/test-shard.ps1 -Times (after a run with -result-xml).
# Build first (dotnet build Kb.sln). Run from anywhere: tools/test-shard.ps1 -Shard 0 -Of 8
param(
    [int]$Shard,
    [int]$Of = 1,
    [string]$Configuration = "Debug",
    # Only print the classes of this shard.
    [switch]$List,
    # Rewrite tools/test-times.json from an xUnit XML result file (Kb.Tests.Integration.dll -result-xml <file>).
    [string]$Times
)
$ErrorActionPreference = "Stop"
$timesFile = Join-Path $PSScriptRoot "test-times.json"

if ($Times) {
    [xml]$x = Get-Content $Times
    $o = [ordered]@{}
    $x.assemblies.assembly.collection | ForEach-Object { $_.test } | Group-Object type | Sort-Object Name |
        ForEach-Object { $o[$_.Name] = [int][math]::Ceiling(($_.Group | Measure-Object -Property time -Sum).Sum) }
    [IO.File]::WriteAllText($timesFile, (($o | ConvertTo-Json) -replace "`r`n", "`n") + "`n")
    return
}
if ($Of -lt 1 -or $Shard -lt 0 -or $Shard -ge $Of) { throw "Shard must be 0 to $($Of - 1)." }

$dll = Join-Path $PSScriptRoot "..\tests\Kb.Tests.Integration\bin\$Configuration\net10.0\Kb.Tests.Integration.dll"
if (-not (Test-Path $dll)) { throw "Not built: $dll" }
$classes = @(& dotnet $dll -list classes -noColor | Where-Object { $_ -match '^Kb\.Tests\.Integration\.' } | ForEach-Object { $_.Trim() } | Sort-Object)
if ($LASTEXITCODE -ne 0 -or $classes.Count -eq 0) { throw "Could not list the test classes." }

$known = @{}
(Get-Content -Raw $timesFile | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $known[$_.Name] = [double]$_.Value }
$sorted = @($known.Values | Sort-Object)
$median = if ($sorted.Count) { $sorted[[int]($sorted.Count / 2)] } else { 1 }
function Weight($c) { if ($known.ContainsKey($c)) { $known[$c] } else { $median } }

# Longest first, name second, so every shard computes the same split.
$load = [double[]]::new($Of)
$mine = foreach ($c in ($classes | Sort-Object @{ Expression = { Weight $_ }; Descending = $true }, @{ Expression = { $_ } })) {
    $least = 0
    for ($i = 1; $i -lt $Of; $i++) { if ($load[$i] -lt $load[$least]) { $least = $i } }
    $load[$least] += Weight $c
    if ($least -eq $Shard) { $c }
}
$mine = @($mine)
if ($List) { $mine; return }
Write-Host "Shard $Shard of $Of`: $($mine.Count) of $($classes.Count) classes, about $([math]::Round($load[$Shard])) test-seconds."
if ($mine.Count -eq 0) { return }

$filters = foreach ($c in $mine) { "-class"; $c }
& dotnet $dll @filters -noColor
exit $LASTEXITCODE
