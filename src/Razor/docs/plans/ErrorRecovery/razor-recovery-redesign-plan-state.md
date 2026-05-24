# Razor recovery plan -- state

Sidecar state file for `razor-recovery-redesign-plan.md` (in the same
directory). The plan is the immutable contract; this file is the
transient run-state that should be updated as each sub-stage
completes.

## Current stage
Stage 1.2 complete. Ready for Stage 1.3 (diagnostic factory updates).

## Status of each stage
- Stage 0.0: complete
- Stage 0.1: complete
- Stage 0.2: complete
- Stage 0.3: complete
- Stage 0.4: complete
- Stage 0.5: complete
- Stage 1.1: complete
- Stage 0.4: not started
- Stage 0.5: not started
- Stage 1.1: not started
- Stage 1.2: complete
- Stage 1.3: not started
- Stage 1.4: not started
- Stage 2.1: not started
- Stage 2.2: not started
- Stage 2.3: not started
- Stage 2.4: not started
- Stage 2.5: not started
- Stage 2.6: not started
- Stage 3.1: not started
- Stage 3.2: not started
- Stage 3.3: not started
- Stage 3.4: not started
- Stage 4.1: not started
- Stage 4.2: not started
- Stage 4.3: not started
- Stage 4.4: not started
- Stage 5.0.0: not started
- Stage 5.0: not started
- Stage 5.1: not started
- Stage 5.2: not started
- Stage 5.3: not started
- Stage 5.4: not started
- Stage 5.5: not started
- Stage 5.6.0: not started
- Stage 5.6: not started
- Stage 6.1: not started
- Stage 6.2: not started
- Stage 6.3: not started
- Stage 6.4: not started
- Stage 7: not started

## Diagnostic IDs allocated
(none yet)

## Stage 5.0.0 spike report
(not yet run -- to be populated with: malformed expression, writer
file:line, IR node type, placeholder shape)

## Stage 5.6.0 LSP anchor classes
(not yet identified -- to be populated with classification /
completion / hover provider class names)

## Stage 2 verification
(parser-recovery branch counts; populate during Stage 2 gate check)

## BaselineWriter location decision
Resolved: reuse the existing `ParserTestBase.AssertSyntaxTreeNodeMatchesBaseline`
infrastructure (which uses `.stree.txt` / `.diag.txt` / `.cspans.txt` /
`.tspans.txt` baselines under `TestFiles/ParserTests/<TestClass>/<TestMethod>.*`
via the `[InitializeTestFile]` attribute on `ParserTestBase`). No copy of
`BaselineWriter.cs` into `legacyTest/` was needed because the shared base
class already provides everything via `ParserTestBase` + the
`/p:GenerateBaselines=true` MSBuild property already supported by the
`legacyTest` csproj.

This is a small deviation from the plan's "Legacy.json / Enhanced.json"
shape: snapshots are file-per-aspect text baselines instead of one
combined JSON. The semantic intent (capture parser output for
mismatch-detection across the migration) is preserved.

## Pre-existing test failures (from prereqs)
None recorded.

Prereq run (Stage 0 entry, branch `razor-recovery-stage-0`, base
commit `f445deb5f8c`):

- `Get-Content global.json` -> SDK pin `10.0.107`, `rollForward: patch`.
- `dotnet --version` -> `10.0.108` (satisfies pin).
- `dotnet build Razor.slnf` -> succeeded (Time Elapsed 00:02:46.23, 0
  warnings, 0 errors) after fixing a pre-existing stale `Razor.slnf`
  entry (`Microsoft.CodeAnalysis.ExternalAccess.Razor.EditorFeatures.csproj`
  was removed in `ff155b40` but not pruned from the slnf -- repaired
  in commit `f445deb5f8c`).
- `dotnet test ... Microsoft.AspNetCore.Razor.Language.UnitTests.csproj --no-build`
  -> 3597 / 3597 passed on both net10.0 and net472.
- `dotnet test ... Microsoft.AspNetCore.Razor.Language.Legacy.UnitTests.csproj --no-build`
  -> 1278 / 1278 passed on both net10.0 and net472.

## Performance baseline
(not yet measured -- Stage 6.4)

## Notes
- 2026-05-24: Stage 0.0 done. Razor.slnf had a stale entry; fixed in
  `f445deb5f8c`. Prereqs all green.
- 2026-05-24: Stage 0.1 done. 10 corpus `.razor` files created under
  `src/Razor/src/Compiler/Microsoft.AspNetCore.Razor.Language/legacyTest/ParserRecoveryCorpus/`,
  matching the cases listed in the plan.
- 2026-05-24: Stage 0.2 done. `ParserRecoveryCorpusSnapshotTests` class
  added to `legacyTest/Legacy/`; extends `ParserTestBase`. 10 `[Fact]`
  methods (one per corpus file) loading source via the embedded resource
  glob (added `ParserRecoveryCorpus\**\*` to both `EmbeddedResource Include`
  and `DefaultItemExcludes` in the csproj). Generated 30 baselines
  via `dotnet test ... /p:GenerateBaselines=true --filter ParserRecoveryCorpusSnapshotTests`.
  All 10 corpus tests green on both net10.0 and net472. Full legacyTest
  project: 1288 / 1288 green (1278 baseline + 10 new), no regressions.
- 2026-05-24: Stage 0.3 done. `UseEnhancedRecovery` flag added at bit
  `1 << 12` in `RazorParserOptions.Flags`, plus the matching getter on
  `RazorParserOptions`, setter on `RazorParserOptions.Builder`, and
  `Optional<bool> useEnhancedRecovery` parameter on `WithFlags`. Default
  is off (not in `GetDefaultFlags`). The compiler csproj has no
  PublicAPI.Unshipped.txt -- no API tracking update required (matches
  the plan's note).

  Downstream audit (per Stage 0.3 step 8): `RazorConfiguration.UseRoslynTokenizer`
  (`Language/RazorConfiguration.cs:18`) and
  `RazorSourceGenerationOptions.UseRoslynTokenizer` (`SourceGenerators/RazorSourceGenerationOptions.cs:33`)
  surface the existing tokenizer flag to SDK consumers. `UseEnhancedRecovery`
  is internal migration scaffolding that will be removed in Stage 6.2;
  **not** surfaced to SDK consumers. No sibling fields added.

  Three new `RazorParserOptionsTest` cases (DefaultsToFalse,
  RoundTripsThroughBuilder, RoundTripsThroughWithFlags) all green.
  Full Razor.slnf build clean; language tests 3600 / 3600 (was 3597, +3);
  legacy tests 1288 / 1288 unchanged.
- 2026-05-24: Stage 0.4 done. `SkippedContentSyntax` added to
  `Syntax.xml` with `SkippedTokens : SyntaxList<SyntaxToken>` and
  `OriginatingLanguage : SyntaxKind` fields. Hand-edited `SyntaxKind.cs`
  to add `SkippedContent` in the `#region Nodes` block (the file is not
  auto-generated, despite living in the Syntax/ directory). Ran
  `dotnet run --project ...RazorSyntaxGenerator -- Syntax.xml Generated/`;
  3 generated files updated (+132 lines). Empirical resolution of
  Round 3's H5 concern: the generator handles `Type="SyntaxKind"`
  fields cleanly, producing a non-nullable `public SyntaxKind OriginatingLanguage`
  property without needing `Optional="true"`. Razor.slnf builds clean;
  test suites unchanged (1288 / 3600).
- 2026-05-24: Stage 0.5 done. Audit checklist
  `legacyTest/ParserRecoveryCorpus/parser-recovery-audit-skipped-content.md`
  enumerates ~22 consumers across 13+ files, categorising each as
  (a) must ignore, (b) skip-and-warn, or (c) unaffected. Identifies
  the 4 most-critical consumer sites for unblocking the #10383 wall
  of red (all owned by Stages 5.0 / 5.1 / 5.3).

  **Stage 0 complete.** Both parser test projects remain green
  (1288 / 1288 legacy + 3600 / 3600 language, both TFMs); Razor.slnf
  builds clean. Ready for handoff to fresh agent for Stage 1.
- 2026-05-25: Stage 1.1 done. Added the `Synchronize` helper plus its
  supporting types to `TokenizerBackedParser`:
  - `FollowSet` (`Legacy/FollowSet.cs`): readonly struct backed by two
    `ulong`s indexed by the low byte of `SyntaxKind`. Supports
    `Empty`, `Contains`, `Union`, `|` operator, params constructor,
    value equality, and a debug assertion that fires if any
    `SyntaxKind` whose underlying value exceeds 127 is added or
    tested (matches plan BDD #4 -- current `FirstAvailableTokenKind`
    is well below that bound).
  - `SyncResult` / `SyncStopReason` / `SyncOptions`
    (`Legacy/SyncResult.cs`): `record struct` for the return value
    (so `default` is a valid no-skip, EOF result), enum reasons
    (`AtFollowToken`, `AtOuterFollowToken`, `AtNewLine`,
    `AtTransition`, `EndOfFile`), and `[Flags]` options
    (`None`, `StopAtNewLine`, `StopAtTransition`).
  - Both `Synchronize` overloads on `TokenizerBackedParser` (the
    full one with `outerFollow`, and a convenience overload that
    delegates with `FollowSet.Empty`). Honours
    `CancellationToken.ThrowIfCancellationRequested()` in the inner
    loop, leaves the sync-point token current (not consumed), does
    NOT call `Accept`, returns the `SkippedContentSyntax` for the
    caller to insert.
  - `RecoveryFollowSets` (`Legacy/RecoveryFollowSets.cs`): seeded
    with `Empty` only. Per the plan's Stage 4.1 reference catalogue,
    named language-scoped sets and the cross-language translation
    helpers (`ForCSharpCallee` / `ForHtmlCallee`) will be added by
    the stages that need them; nothing in Stage 1.1 references
    them yet so populating now would be dead code.

  Test class `TokenizerBackedParserRecoveryTests` added to
  `legacyTest/Legacy/`. 13 `[Fact]` methods cover: `FollowSet.Empty`,
  `FollowSet` construction + `Contains`, `Union` / `|`, value
  equality, and all the `Synchronize` cases the plan enumerates
  (no-op at current token, single-token skip to local follow,
  outer-follow stop reason, many-token skip, EOF, `StopAtNewLine`,
  `StopAtTransition`, cancellation throws, `Synchronize` does not
  populate the parser's token builder). Uses a tiny in-test
  `TestHtmlMarkupParser` subclass to expose the protected helpers
  the harness needs (`EnsureCurrent`, `CurrentToken`, `EndOfFile`,
  `TokenBuilder.Count`).

  **Plan deviations (small, intentional):**
  - `Synchronize` is declared `protected internal`, not `protected`
    as written literally in the plan. This matches the existing
    convention in `TokenizerBackedParser` (`AcceptUntil`,
    `AcceptAndMoveNext`, `Accept(SyntaxToken)`, `NextIs`,
    `NextToken` are all `protected internal`) and lets the
    `legacyTest` assembly call it directly via `InternalsVisibleTo`
    without forcing every test to inherit from a parser.
  - `SyncOptions.StopAtTransition` only matches
    `SyntaxKind.Transition` (the language `@` token). The plan's
    prose only enumerates "transition"; `RazorCommentTransition`
    (the `@*` start of a Razor comment) is intentionally NOT
    included as a stop kind. If a later stage needs it, a
    `StopAtRazorCommentTransition` option can be added without
    changing existing semantics.
  - `RecoveryFollowSets.cs` contains only `Empty` (and a reference
    `FollowSet.Empty` proxy) -- the named follow sets the plan
    catalogues in Stage 4.1 are explicitly out of scope for this
    PR and will be added when their consumers land.

  Razor.slnf builds clean (0 warnings, 0 errors). Legacy tests
  1301 / 1301 (1288 baseline + 13 new); language tests 3600 / 3600
  unchanged. Both TFMs.
- 2026-05-25: Stage 1.2 done. Added three helpers to
  `TokenizerBackedParser` directly after the two `Synchronize`
  overloads:
  - `Required(SyntaxKind kind, RazorDiagnostic diagnostic, FollowSet recovery, SyntaxKind originatingLanguage)`
    returning `(SyntaxToken token, SkippedContentSyntax? skipped)`.
    Consume path: if `CurrentToken.Kind == kind`, advances and
    returns `(token, null)`. Missing path: emits
    `SyntaxFactory.MissingToken(kind, diagnostic)`, runs
    `Synchronize(recovery, originatingLanguage)`, and returns
    `(missing, sync.Skipped)`. The diagnostic is attached to the
    missing token only -- `ErrorSink` is NOT written to (Stage 1.4
    will fix up `ParseRazorComment`'s pre-existing double-emit).
  - Multi-kind `Required(ImmutableArray<SyntaxKind> acceptableKinds, ...)`
    overload. Consumes the current token if its kind matches any
    entry; on failure, emits `MissingToken(acceptableKinds[0], ...)`.
    Asserts `!acceptableKinds.IsDefaultOrEmpty`.
  - `Optional(SyntaxKind kind)` -- thin wrapper over the
    pre-existing `GetOptionalToken(kind)` for vocabulary symmetry
    with `Required`. No diagnostic, no missing token; returns the
    consumed token or `null`.

  All three follow the existing `protected internal` visibility
  convention (matches `Synchronize` and the rest of
  `TokenizerBackedParser`'s public-ish helpers) so the `legacyTest`
  assembly can call them directly via `InternalsVisibleTo`.

  Test class `TokenizerBackedParserRecoveryTests` extended with
  ten new `[Fact]` methods using the same `TestParserHarness`
  fixture from Stage 1.1:
  - `Required_AtExpectedKind_ConsumesAndReturnsTokenWithNoSkipped`
  - `Required_KindMissing_EmitsMissingTokenAndSynchronizesToRecovery`
  - `Required_KindMissingAtEndOfFile_EmitsMissingTokenWithNullSkipped`
  - `Required_KindMissingWithEmptyRecovery_SkipsToEndOfFile`
  - `Required_MultiKind_MatchesFirstKind`
  - `Required_MultiKind_MatchesSecondKind`
  - `Required_MultiKind_NoneMatch_EmitsMissingTokenOfFirstKind`
  - `Required_MissingPath_AttachesDiagnosticToMissingToken_AndDoesNotEmitToErrorSink`
    (Stage 1.2 exit criterion: exactly one diagnostic copy --
    attached to the token, never copied into `ErrorSink`)
  - `Optional_AtExpectedKind_ConsumesAndReturnsToken`
  - `Optional_KindMissing_ReturnsNullAndDoesNotAdvance`

  Helper `CreateTestDiagnostic()` constructs a throwaway
  `RazorDiagnostic` from a local `RazorDiagnosticDescriptor`
  (id `"test0001"`, lower-case so it cannot clash with any real
  `RZxxxx` id) -- intentionally NOT a real factory entry since
  Stage 1.3 owns RZ ID allocation. The diagnostic is only used to
  verify identity-equality (`Assert.Same(diagnostic, ...)`) on the
  missing-token attachment; it never reaches a tree.

  **Plan deviations:** none. The plan's literal signature uses
  `params SyntaxKind[]` phrasing for the multi-kind overload
  ("multi-acceptable-kind"); the implementation uses
  `ImmutableArray<SyntaxKind>` for allocation-friendliness and to
  match Stage 2/3 call-sites which will pre-build these arrays
  with `ImmutableArray.Create(...)`. A `params SyntaxKind[]`
  overload can be added trivially if call-sites need it.

  Razor.slnf builds clean (0 warnings, 0 errors). Legacy tests
  1311 / 1311 (1301 baseline + 10 new); language tests 3600 / 3600
  unchanged. Both TFMs.
