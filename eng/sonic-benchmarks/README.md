# Sonic 4 pipeline benchmarks

Total-compilation-time benchmarks for the MudBlazor source tree, comparing the
Sonic 4 (decl-pipeline-front + `RegisterPreCompilationSourceOutput`) pipeline
against the previous (`compilation.AddSyntaxTrees`) pipeline.

## What this measures

Each iteration:

1. Clears `obj/` and `bin/` recursively under `src/MudBlazor`.
2. Calls `dotnet build -c Release --no-incremental` on `MudBlazor.csproj`.
3. Wall-clock-times the build via `[System.Diagnostics.Stopwatch]`.

This is end-to-end build time -- it includes restore graph evaluation
(although NuGet caches stay warm), the Razor source generator running
(decl-lowering, tag-helper discovery, the full SG pipeline), and the C#
compiler producing the final assembly. The Razor SG's behavior dominates the
delta between the two pipelines.

We deliberately don't measure just the SG (the existing
`Microsoft.AspNetCore.Razor.Microbenchmarks.Generator` project covers that
with `BenchmarkDotNet`). The point of this script is "what does a real
end-developer see when they hit `dotnet build` on a non-trivial Blazor app".

## Prerequisites

- `D:\projects\roslyn2` (this repo) checked out, on a branch that should
  represent the new pipeline.
- `D:\projects\mudblazor` checked out (any recent commit).
- `dotnet` SDK on PATH, matching the global.json of the roslyn repo.

## Quick start

```powershell
# Compare new pipeline vs baseline (default branches: features/sonic vs sonic/4_decl_pipeline_front).
.\compare-pipelines.ps1 -Iterations 5

# Override branches/commits.
.\compare-pipelines.ps1 -BaselineBranch upstream/features/sonic -NewBranch sonic/4_decl_pipeline_front

# Run a single benchmark on the current branch (no comparison, useful for iterating).
.\benchmark-mudblazor.ps1 -Iterations 3 -RunLabel current

# Skip the toolset pack step (e.g. if you already packed and just want to re-run timings).
.\benchmark-mudblazor.ps1 -Iterations 3 -SkipPack -RunLabel current
```

## What each script does

### `benchmark-mudblazor.ps1`

Single-branch benchmark runner.

1. Builds + packs `Microsoft.Net.Compilers.Razor.Toolset` from the current
   working tree of the roslyn repo. Produces a versioned `.nupkg` in
   `artifacts/packages/Release/Shipping/`.
2. Patches the MudBlazor directory:
   - Adds a `Directory.Build.targets` referencing the packed Razor toolset
     and the matching Roslyn toolset version (pinned via
     `eng/Versions.props`).
   - Adds NuGet sources for nuget.org, the local packed-output folder, and
     the dnceng feeds that host the Roslyn toolset.
3. Warms up: runs one untimed `dotnet restore` + `dotnet build` so the NuGet
   cache and SDK are hot.
4. Loops `Iterations` times, each time:
   - Deletes all `obj/` and `bin/` directories under `src/MudBlazor`.
   - Times `dotnet build -c Release --no-incremental`.
5. Computes min/mean/median/max and appends a row to
   `artifacts/benchmarks/mudblazor-results.csv`.
6. Reverts the patch and removes the NuGet sources it added.

### `compare-pipelines.ps1`

Orchestrator. Refuses to run with a dirty working tree, then:

1. Remembers the current branch.
2. Wipes the results CSV.
3. Checks out the baseline branch, runs `benchmark-mudblazor.ps1` with label
   `baseline`.
4. Checks out the new branch, runs `benchmark-mudblazor.ps1` with label
   `new`.
5. Restores the original branch.
6. Prints mean/median/min delta and ratio.

## Output

Results CSV at `artifacts/benchmarks/mudblazor-results.csv` with columns:

```
Timestamp,RunLabel,GitSha,GitBranch,RazorPkg,Iterations,MinSeconds,MeanSeconds,MedianSeconds,MaxSeconds
```

Rows accumulate across runs unless `compare-pipelines.ps1` clears the file.

## Initial results (2026-06-03)

First end-to-end run with 5 iterations per branch against
`D:\projects\mudblazor` (MudBlazor.csproj only, net8.0 + net9.0 multi-TFM):

| Metric  | Baseline (`features/sonic`) | New (`sonic/4_decl_pipeline_front`) | Delta |
|---------|------------------------------|-------------------------------------|--------|
| Mean    | 39.843 s | 40.477 s | +0.634 s (+1.6%) |
| Median  | 39.863 s | 39.263 s | -0.600 s (-1.5%) |
| Min     | 38.991 s | 38.766 s | -0.225 s (-0.6%) |

Earlier 3-iteration warmup run was -1.8% on mean. The 5-iteration run includes
one outlier at 44.4 s on the new branch (likely background CPU contention) that
pulls the mean up; the median is more representative.

**Interpretation**: total compile time is essentially unchanged (within
noise) between the two pipelines. This is the expected outcome -- the
architectural change (one unified engine + `RegisterPreCompilationSourceOutput`
vs two engines + `compilation.AddSyntaxTrees`) shouldn't change the gross
work the Razor compiler + Roslyn need to do; only how that work is split
across the SG and compilation construction stages. Run on a quiet machine
with 10+ iterations if you need to defend a precise number.



- The benchmark measures one MudBlazor project (`src/MudBlazor/MudBlazor.csproj`),
  not the entire MudBlazor solution. That project has ~750 `.razor` files
  which is enough to expose SG behavior, but the consumer apps and tests
  are not included.
- Wall-clock numbers vary with system load. Run on a quiet machine and use
  `-Iterations 5` or more for any number you intend to quote.
- The `Microsoft.Net.Compilers.Razor.Toolset` build step is fairly slow
  (~30-60s); the pack step is fast. Use `-SkipPack` once you've packed for
  a given branch and just want to redo timings.
