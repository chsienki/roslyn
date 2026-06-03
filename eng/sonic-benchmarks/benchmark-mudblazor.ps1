[CmdletBinding(PositionalBinding=$false)]
Param(
    # Number of build iterations to time. Min recommended: 3. Higher = more accurate but slower.
    [int] $Iterations = 3,

    # Path to the Razor compiler repo root. Defaults to the git toplevel of this script's location.
    [string] $RazorRepoDir = (& git -C $PSScriptRoot rev-parse --show-toplevel),

    # Path to a local MudBlazor checkout to use for the build.
    [string] $MudBlazorDir = "D:\projects\mudblazor",

    # Label printed in the output so you can tell runs apart (e.g. "baseline" vs "sonic4").
    [string] $RunLabel = "current",

    # If set, skips packing the toolset (assumes it was packed already). Useful for repeat runs.
    [switch] $SkipPack,

    # If set, restores between iterations as well as builds (closer to clean-build numbers).
    # Defaults to false so we measure incremental rebuild-from-clean-output, which is the
    # most representative single-developer scenario.
    [switch] $RestoreEachIteration
)

Set-StrictMode -version 2.0
$ErrorActionPreference = "Stop"

$RazorRepoDir = (Resolve-Path $RazorRepoDir).Path
$MudBlazorDir = (Resolve-Path $MudBlazorDir).Path

Write-Output "=== Benchmark configuration"
Write-Output "  RazorRepoDir = $RazorRepoDir"
Write-Output "  MudBlazorDir = $MudBlazorDir"
Write-Output "  RunLabel     = $RunLabel"
Write-Output "  Iterations   = $Iterations"
Write-Output "  Current git  = $(git -C $RazorRepoDir rev-parse --short HEAD) on $(git -C $RazorRepoDir branch --show-current)"
Write-Output ""

# Where the packed toolset will appear after `dotnet pack`. The toolset csproj writes to
# artifacts\packages\Release\Shipping (same convention as the official Arcade build).
$razorPackagesDir = "$RazorRepoDir\artifacts\packages\Release\Shipping"

if (-not $SkipPack) {
    Write-Output "=== Packing the Razor.Toolset project"
    # Plain `dotnet pack` is faster and simpler than going through eng\common\build.ps1 (which
    # cleans + restores from scratch, taking minutes). The toolset csproj's normal pack flow is
    # fine for benchmarking purposes.
    dotnet pack "$RazorRepoDir\src\Razor\src\Compiler\Microsoft.Net.Compilers.Razor.Toolset\Microsoft.Net.Compilers.Razor.Toolset.csproj" `
        -c Release `
        --nologo `
        -v:minimal
    if (-not $?) { exit $LASTEXITCODE }
}

# Sanity-check that the packed nupkg exists; find the most recent one to use.
$packedNupkg = Get-ChildItem "$razorPackagesDir\Microsoft.Net.Compilers.Razor.Toolset.*.nupkg" -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending |
               Select-Object -First 1
if (-not $packedNupkg) {
    throw "No packed Razor toolset nupkg found in $razorPackagesDir. Did the pack step succeed?"
}
# Re-derive the version from the actual file name -- the pack target may stamp a version
# different from what we'd compute from the build number (e.g. "10.4.0-dev" rather than our
# arcade-style version).
if ($packedNupkg.Name -match "^Microsoft\.Net\.Compilers\.Razor\.Toolset\.(.+)\.nupkg$") {
    $razorPkgVersion = $Matches[1]
}
Write-Output "  Using packed toolset: $($packedNupkg.Name)"
Write-Output ""

# Patch MudBlazor's MudBlazor.csproj to pin the Razor + Roslyn toolset versions, mirroring
# what razor-toolset-tests does. Save the original so we can restore at the end.
$mudProjectPath = "$MudBlazorDir\src\MudBlazor\MudBlazor.csproj"
if (-not (Test-Path $mudProjectPath)) {
    throw "MudBlazor.csproj not found at $mudProjectPath"
}

# We also need a Directory.Build.targets in the MudBlazor root to add the package refs.
$dirBuildTargetsPath = "$MudBlazorDir\Directory.Build.targets"
$dirBuildTargetsOriginal = if (Test-Path $dirBuildTargetsPath) { Get-Content $dirBuildTargetsPath -Raw } else { $null }

# Remember any nuget sources we add so we can clean them up.
$pushedSources = @()

# Run the actual benchmark inside the MudBlazor source dir.
Push-Location "$MudBlazorDir\src\MudBlazor"
try {
    Write-Output "=== Wiring up Razor toolset $razorPkgVersion"

    # If Directory.Build.targets doesn't exist, create one with a single empty <Project> root.
    if (-not $dirBuildTargetsOriginal) {
        Set-Content $dirBuildTargetsPath "<Project></Project>"
    }

    $toolsetPatch = @"
    <ItemGroup>
        <PackageReference Include="Microsoft.Net.Compilers.Razor.Toolset" Version="$razorPkgVersion" Condition="'`$(UsingMicrosoftNETSdkRazor)' == 'true'">
            <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
            <PrivateAssets>all</PrivateAssets>
        </PackageReference>
    </ItemGroup>
</Project>
"@
    (Get-Content $dirBuildTargetsPath -Raw).Replace("</Project>", $toolsetPatch) | Set-Content $dirBuildTargetsPath

    Write-Output "=== Adding NuGet sources"
    # nuget.org and razor-toolset (the local packed output).
    try { dotnet nuget add source "$razorPackagesDir" --name razor-toolset 2>&1 | Out-Null; $pushedSources += "razor-toolset" } catch { }
    try { dotnet nuget add source "https://api.nuget.org/v3/index.json" --name nuget 2>&1 | Out-Null; $pushedSources += "nuget" } catch { }

    Write-Output "=== Warmup restore + build (not timed)"
    dotnet build -c Release 2>&1 | Select-Object -Last 5
    if (-not $?) { exit $LASTEXITCODE }

    Write-Output ""
    Write-Output "=== Timed iterations ($Iterations)"

    $samples = New-Object System.Collections.ArrayList
    for ($i = 1; $i -le $Iterations; $i++) {
        if ($RestoreEachIteration) {
            dotnet restore 2>&1 | Out-Null
            if (-not $?) { exit $LASTEXITCODE }
        }

        # `--no-incremental` forces a full rebuild without deleting obj/ (which would lose the
        # restore graph). This is the most representative "I just made a change, what's my
        # rebuild time" scenario.
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        dotnet build -c Release --no-incremental 2>&1 | Out-Null
        $exitCode = $LASTEXITCODE
        $sw.Stop()

        if ($exitCode -ne 0) {
            Write-Output "  Iteration ${i}: BUILD FAILED (exit $exitCode)"
            continue
        }

        $elapsed = [double]$sw.Elapsed.TotalSeconds
        [void]$samples.Add($elapsed)
        Write-Output ("  Iteration {0}: {1:N3} seconds" -f $i, $elapsed)
    }

    Write-Output ""
    if ($samples.Count -gt 0) {
        $stats = $samples | Measure-Object -Minimum -Maximum -Average
        $mean = $stats.Average
        $min  = $stats.Minimum
        $max  = $stats.Maximum
        $sorted = @($samples | Sort-Object)
        $median = if ($sorted.Count % 2 -eq 1) { $sorted[[Math]::Floor($sorted.Count / 2)] } else { ($sorted[($sorted.Count / 2) - 1] + $sorted[$sorted.Count / 2]) / 2 }

        Write-Output "=== Results ($RunLabel)"
        Write-Output ("  Min    : {0:N3}s" -f $min)
        Write-Output ("  Mean   : {0:N3}s" -f $mean)
        Write-Output ("  Median : {0:N3}s" -f $median)
        Write-Output ("  Max    : {0:N3}s" -f $max)
        Write-Output "  Samples: $($samples -join ', ')"

        # Persist the result so subsequent runs (with different branch) can be compared.
        $resultsPath = "$RazorRepoDir\artifacts\benchmarks\mudblazor-results.csv"
        $headerNeeded = -not (Test-Path $resultsPath)
        $record = [PSCustomObject]@{
            Timestamp     = (Get-Date).ToString("o")
            RunLabel      = $RunLabel
            GitSha        = (git -C $RazorRepoDir rev-parse --short HEAD)
            GitBranch     = (git -C $RazorRepoDir branch --show-current)
            RazorPkg      = $razorPkgVersion
            Iterations    = $samples.Count
            MinSeconds    = [Math]::Round($min, 3)
            MeanSeconds   = [Math]::Round($mean, 3)
            MedianSeconds = [Math]::Round($median, 3)
            MaxSeconds    = [Math]::Round($max, 3)
        }
        if ($headerNeeded) {
            $record | Export-Csv -NoTypeInformation -Path $resultsPath
        } else {
            $record | Export-Csv -NoTypeInformation -Path $resultsPath -Append
        }
        Write-Output ""
        Write-Output "  Logged to $resultsPath"
    } else {
        Write-Output "=== No successful iterations -- nothing to report."
        exit 1
    }
} finally {
    # Restore original Directory.Build.targets (or delete it if we created it).
    if ($dirBuildTargetsOriginal) {
        Set-Content $dirBuildTargetsPath $dirBuildTargetsOriginal
    } elseif (Test-Path $dirBuildTargetsPath) {
        Remove-Item $dirBuildTargetsPath
    }

    foreach ($source in $pushedSources) {
        try { dotnet nuget remove source $source 2>&1 | Out-Null } catch { }
    }
    Pop-Location
}

# Explicit exit so the caller sees success even if the cleanup `dotnet nuget remove source`
# returned a nonzero code (e.g. source didn't exist after all, race with another script).
exit 0
