[CmdletBinding(PositionalBinding=$false)]
Param(
    # Branch (or commit) representing the OLD pipeline (the baseline to compare against).
    [string] $BaselineBranch = "upstream/features/sonic",

    # Branch (or commit) representing the NEW pipeline.
    [string] $NewBranch = "sonic/4_decl_pipeline_front",

    # Number of build iterations per branch. Min 3 recommended.
    [int] $Iterations = 3,

    # Path to the Razor compiler repo root. Defaults to the git toplevel of this script's location.
    [string] $RazorRepoDir = (& git -C $PSScriptRoot rev-parse --show-toplevel),

    # Path to a local MudBlazor checkout.
    [string] $MudBlazorDir = "D:\projects\mudblazor",

    # Where to put the combined comparison results.
    [string] $OutputDir = $PSScriptRoot
)

Set-StrictMode -version 2.0
$ErrorActionPreference = "Stop"

$RazorRepoDir = (Resolve-Path $RazorRepoDir).Path
$MudBlazorDir = (Resolve-Path $MudBlazorDir).Path
$OutputDir    = (Resolve-Path $OutputDir).Path

Write-Output "=== Sonic 4 pipeline comparison benchmark"
Write-Output "  Baseline branch : $BaselineBranch"
Write-Output "  New branch      : $NewBranch"
Write-Output "  Iterations/run  : $Iterations"
Write-Output "  Razor repo      : $RazorRepoDir"
Write-Output "  MudBlazor       : $MudBlazorDir"
Write-Output ""

# Sanity: must have a clean working tree, otherwise the branch switches will conflict.
$gitStatus = git -C $RazorRepoDir status --porcelain
if ($gitStatus) {
    throw "Working tree at $RazorRepoDir is not clean. Commit or stash first, then re-run."
}

$originalBranch = git -C $RazorRepoDir rev-parse --abbrev-ref HEAD
Write-Output "  (Will return to $originalBranch when done)"
Write-Output ""

$benchmarkScript = Join-Path $PSScriptRoot "benchmark-mudblazor.ps1"
if (-not (Test-Path $benchmarkScript)) {
    throw "Benchmark script not found at $benchmarkScript"
}

# Copy the benchmark script to a temp location OUTSIDE the repo so it survives branch
# switches (the baseline branch may not have this script yet).
$tempBenchmark = Join-Path $env:TEMP "benchmark-mudblazor-$([Guid]::NewGuid().ToString('N')).ps1"
Copy-Item $benchmarkScript $tempBenchmark

# Clear the results CSV so we collect only this comparison's data.
$resultsCsv = "$RazorRepoDir\artifacts\benchmarks\mudblazor-results.csv"
if (Test-Path $resultsCsv) {
    Remove-Item $resultsCsv
}

try {
    foreach ($pair in @(@{Label="baseline"; Branch=$BaselineBranch}, @{Label="new"; Branch=$NewBranch})) {
        Write-Output ""
        Write-Output "############################################"
        Write-Output "### Running benchmark on $($pair.Branch) (label: $($pair.Label))"
        Write-Output "############################################"
        Write-Output ""

        Write-Output "=== Switching to $($pair.Branch)"
        git -C $RazorRepoDir checkout $pair.Branch
        if (-not $?) { throw "Failed to check out $($pair.Branch)" }

        & $tempBenchmark -Iterations $Iterations -RazorRepoDir $RazorRepoDir -MudBlazorDir $MudBlazorDir -RunLabel $pair.Label
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Benchmark run for $($pair.Branch) exited with code $LASTEXITCODE"
        }
    }
} finally {
    Write-Output ""
    Write-Output "=== Restoring original branch ($originalBranch)"
    git -C $RazorRepoDir checkout $originalBranch
    if (Test-Path $tempBenchmark) {
        Remove-Item $tempBenchmark -ErrorAction SilentlyContinue
    }
}

Write-Output ""
Write-Output "############################################"
Write-Output "### Comparison"
Write-Output "############################################"
Write-Output ""

if (-not (Test-Path $resultsCsv)) {
    Write-Output "No results CSV at $resultsCsv -- both runs must have failed."
    exit 1
}

$results = Import-Csv $resultsCsv
$results | Format-Table -AutoSize

$baselineRow = $results | Where-Object { $_.RunLabel -eq "baseline" } | Select-Object -First 1
$newRow      = $results | Where-Object { $_.RunLabel -eq "new" }      | Select-Object -First 1

if ($baselineRow -and $newRow) {
    $deltaMean   = [double]$newRow.MeanSeconds   - [double]$baselineRow.MeanSeconds
    $deltaMedian = [double]$newRow.MedianSeconds - [double]$baselineRow.MedianSeconds
    $deltaMin    = [double]$newRow.MinSeconds    - [double]$baselineRow.MinSeconds
    $ratioMean   = [double]$newRow.MeanSeconds   / [double]$baselineRow.MeanSeconds
    $ratioMedian = [double]$newRow.MedianSeconds / [double]$baselineRow.MedianSeconds

    Write-Output ("  Baseline mean   : {0:N3}s" -f [double]$baselineRow.MeanSeconds)
    Write-Output ("  New mean        : {0:N3}s" -f [double]$newRow.MeanSeconds)
    Write-Output ("  Mean delta      : {0:+0.000;-0.000}s ({1:P1} of baseline)" -f $deltaMean, ($ratioMean - 1))
    Write-Output ""
    Write-Output ("  Baseline median : {0:N3}s" -f [double]$baselineRow.MedianSeconds)
    Write-Output ("  New median      : {0:N3}s" -f [double]$newRow.MedianSeconds)
    Write-Output ("  Median delta    : {0:+0.000;-0.000}s ({1:P1} of baseline)" -f $deltaMedian, ($ratioMedian - 1))
    Write-Output ""
    Write-Output ("  Min delta       : {0:+0.000;-0.000}s" -f $deltaMin)

    Write-Output ""
    Write-Output "  Full CSV: $resultsCsv"
} else {
    Write-Output "One of the runs is missing -- baseline and/or new row not found in CSV."
    exit 1
}
