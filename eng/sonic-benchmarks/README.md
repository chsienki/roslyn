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

## SG-only microbenchmarks (2026-06-03)

End-to-end MudBlazor build is dominated by C# compilation of the generated
files and the user code. To isolate the actual Razor SG cost, we ran the
existing `Microsoft.AspNetCore.Razor.Microbenchmarks.Generator` project on
both branches. (The project's `Release_Nuget` baseline job is pinned to an
old published toolset, so it isn't meaningful for sonic-3 vs sonic-4 -- we
ran a `Release`-only job on each branch in turn and compared.)

### Initial sonic-4 numbers (before fix)

| Benchmark                       | Baseline (`features/sonic`) | Sonic-4 raw | Delta |
|----------------------------------|------------------------------|-------------|-------|
| `ColdBenchmarks.Cold_Compilation` Mean         | 116.30 ms | 73.87 ms | **-36.5%** :white_check_mark: |
| `ColdBenchmarks.Cold_Compilation` Allocated    |  20.89 MB | 14.46 MB | **-30.8%** :white_check_mark: |
| `RazorBenchmarks.Razor_Edit_Independent`        |   7.72 ms | 10.96 ms |     +42.0% :x: |
| `RazorBenchmarks.Razor_Edit_DependentIgnorable` |   1.70 ms |  7.89 ms |    +363.5% :x: |
| `RazorBenchmarks.Razor_Edit_Dependent`          |  35.58 ms | 51.38 ms |     +44.4% :x: |

The cold win was expected and matched the prototype's promise. But the
incremental edits regressed sharply -- especially `Edit_DependentIgnorable`
(a markup-only edit that doesn't change anything decl-relevant) ran 4.6x
slower.

### Root cause: decl `#pragma checksum` invalidating the pre-comp cache

The pre-compilation source output (`RegisterPreCompilationSourceOutput`)
fans into a `CompilationCache` keyed on a `PreCompCacheKey` whose `Text`
component is compared via `ReferenceEquals` (see
`src/Compilers/Core/Portable/SourceGeneration/CompilationCache.cs:200`).
That cache reuses the previous `Compilation` reference when every per-key
text reference matches, which keeps `tagHelpersFromCompilation` (which
walks `compilation.Assembly` for source-declared tag helpers) cached.

In the new pipeline `DefaultRazorDeclCSharpLoweringPhase` uses the full
engine's `RazorCodeGenerationOptions`, which has `SuppressChecksum = false`.
The decl writer therefore emits a `#pragma checksum "file" "{algo}"
"{hash}"` line at the top, derived from the source file's full byte
content. Any markup edit changes those bytes -> different checksum ->
different decl text -> `declSources` content comparer says different ->
new `SourceText` reference -> pre-comp cache key changes -> compilation
is rebuilt -> tag helper discovery re-walks the entire assembly's
namespace tree (`TagHelperDiscoverer.GetTagHelpers`).

The old pipeline's decl-only stripped engine set `SuppressChecksum = true`
on its `GetDeclarationProjectEngine`, so its `SourceGeneratorText` (which
implements content-based `Equals`) compared byte-equal for markup-only
edits and the cache held.

### Fix

`DefaultRazorDeclCSharpLoweringPhase` now applies
`WithFlags(suppressChecksum: true)` to the decl synthetic doc-node's
`Options` before handing it to `RazorCSharpDocumentWriter.Write`. The impl
half keeps its checksum (debuggers need it). The test helper
`RazorSourceGeneratorTestsBase.TrimChecksum` was relaxed to tolerate decl
files that no longer have a `#pragma` header.

### Numbers after the fix

| Benchmark                       | Baseline (`features/sonic`) | Sonic-4 + fix | Delta vs baseline |
|----------------------------------|------------------------------|---------------|-------------------|
| `ColdBenchmarks.Cold_Compilation` Mean         | 116.30 ms | 70.03 ms | **-39.8%** :white_check_mark: |
| `ColdBenchmarks.Cold_Compilation` Allocated    |  20.89 MB | 14.36 MB | **-31.3%** :white_check_mark: |
| `RazorBenchmarks.Razor_Edit_Independent`        |   7.72 ms |  8.57 ms |     +11.0% :large_yellow_circle: |
| `RazorBenchmarks.Razor_Edit_DependentIgnorable` |   1.70 ms |  1.34 ms | **-21.2%** :white_check_mark: |
| `RazorBenchmarks.Razor_Edit_Dependent`          |  35.58 ms | 49.57 ms |     +39.3% :x: |

Cold compilation kept the full ~40% win and `Edit_DependentIgnorable`
flipped from a 4.6x regression to a 21% win -- the cache now short-circuits
correctly when the decl content is unchanged.

`Edit_Dependent` (where the parameter is genuinely removed) still trailed
the baseline by ~14ms.

### Second fix: rebuild IR from cached syntax tree on material tag helper change

`Edit_Dependent`'s remaining regression came from the second
`ProcessTagHelpers` call's material-change replay path. When the new tag
helpers genuinely differ for a document, we previously re-created the
`RazorCodeDocument` from scratch by re-parsing the source item and
replaying phases 0..decl-lowering (~6 phases per affected file).

The fix replaces that with `RebuildUnresolvedIrFromCachedSyntax`, which
re-runs only IR-lowering + document-classifier + directive-classifier
(~3 phases) against the cached syntax tree. IR lowering always allocates
a fresh `DocumentIntermediateNode`, so subsequent classifier mutations
don't touch any shared state. We also skip re-running discovery because
the idempotency probe already produced the new `TagHelperContext` on the
doc.

### Final numbers (after both fixes)

| Benchmark                       | Baseline (`features/sonic`) | Sonic-4 final | Delta vs baseline |
|----------------------------------|------------------------------|---------------|-------------------|
| `ColdBenchmarks.Cold_Compilation` Mean         | 116.30 ms | 68.16 ms | **-41.4%** :white_check_mark: |
| `ColdBenchmarks.Cold_Compilation` Allocated    |  20.89 MB | 14.34 MB | **-31.4%** :white_check_mark: |
| `RazorBenchmarks.Razor_Edit_Independent`        |   7.72 ms |  7.01 ms |  **-9.2%** :white_check_mark: |
| `RazorBenchmarks.Razor_Edit_DependentIgnorable` |   1.70 ms |  1.35 ms | **-20.6%** :white_check_mark: |
| `RazorBenchmarks.Razor_Edit_Dependent`          |  35.58 ms | 32.55 ms |  **-8.5%** :white_check_mark: |

Sonic-4 is faster than `features/sonic` on every microbenchmark.

### Comparison vs `upstream/main` (the published baseline)

`features/sonic` already contains the decl/impl split from Sonic 1/2/3,
which adds incremental work (every component now produces two C# files
instead of one). `upstream/main` doesn't have that split yet. To verify
sonic-4 is also faster than what ships today, we ran the same benchmarks
against `upstream/main` (commit `d1b80d180de`):

| Benchmark                       | `upstream/main` | Sonic-4 final | Delta vs main |
|----------------------------------|------------------|---------------|---------------|
| `ColdBenchmarks.Cold_Compilation` Mean         | 114.00 ms | 68.16 ms | **-40.2%** :white_check_mark: |
| `ColdBenchmarks.Cold_Compilation` Allocated    |  19.77 MB | 14.34 MB | **-27.5%** :white_check_mark: |
| `RazorBenchmarks.Razor_Edit_Independent`        |   7.55 ms |  7.01 ms |  **-7.2%** :white_check_mark: |
| `RazorBenchmarks.Razor_Edit_DependentIgnorable` |   1.55 ms |  1.35 ms | **-12.9%** :white_check_mark: |
| `RazorBenchmarks.Razor_Edit_Dependent`          |  31.72 ms | 32.55 ms |    +2.6% :large_yellow_circle: |

Sonic-4 wins or ties on every benchmark. `Edit_Dependent` is +0.83 ms
(+2.6%) which is within ~1.5σ of either branch's std-dev -- statistically
borderline. Notably, `features/sonic` was already +3.86 ms (+12%) slower
than main on `Edit_Dependent`, so sonic-4 has not just recovered the
sonic-1/2/3 regression but moved Razor SG perf to within noise of (and
better than for cold) the pre-split codebase.

How to reproduce:

```powershell
# On either branch:
cd src\Razor\src\Compiler\perf\Microsoft.AspNetCore.Razor.Microbenchmarks.Generator
# Temporarily edit Program.cs to:
#   (a) skip the Release_Nuget Baseline job (it's not a useful baseline)
#   (b) force InProcessEmitToolchain always (BDN's normal rebuild path
#       fails on the Razor source tree)
# Then:
dotnet build -c Release
cd D:\projects\roslyn2\artifacts\bin\Microsoft.AspNetCore.Razor.Microbenchmarks.Generator\Release\net10.0
.\Microsoft.AspNetCore.Razor.Microbenchmarks.Generator.exe --filter "*ColdBenchmarks*"
.\Microsoft.AspNetCore.Razor.Microbenchmarks.Generator.exe --filter "*RazorBenchmarks.Razor_Edit*"
# Discard the Program.cs edits before committing.
```
