> **Historical document.** This plan has been executed. It is preserved
> here as a record of the design rationale and the migration steps. The
> live contract is [`src/Razor/docs/parser-recovery.md`](../../parser-recovery.md).
> Tag: `razor-recovery-redesign-complete` (commit `15da146f66a`).
>
> If you are starting a NEW parser-recovery improvement, do not edit this
> file -- author a new plan.

---

# Razor parser error-recovery redesign (A+B)

> **About this document.** This is a multi-stage execution plan,
> authored to be picked up by a fresh agent (human or AI) at any
> stage and executed without further research. The plan was iterated
> over 9 rubber-duck review rounds against 6 different reviewer
> models before being committed; see the companion analysis at
> `razor-parser-analysis.md` in this directory for the architectural
> background it builds on. Motivating bug:
> [dotnet/razor#10383](https://github.com/dotnet/razor/issues/10383).
> Run state lives in the sibling file
> `razor-recovery-redesign-plan-state.md` (created by Stage 0.0).

**One-line goal.** Replace Razor's ad-hoc, panic-mode error recovery with a
Roslyn-style _missing tokens + skipped content + synchronization_ model, so
that a single bad character in a `.razor` file no longer causes a "wall of
red" spanning the rest of the document, and so that the parser can _guess
its way back_ to good code after errors.

**Success looks like.** The motivating bug `dotnet/razor#10383`
(`<button @onclick="">`) reports a narrow, single-token diagnostic at the
empty attribute value (or close to it), with no cascading CS errors in the
rest of the file. Equivalent narrowness applies to a representative
corpus of "one bad character" cases collected in Phase 0.

**Plan file.** `src/Razor/docs/plans/ErrorRecovery/razor-recovery-redesign-plan.md`
(committed to the repo when this plan was authored). See
**Plan persistence and resumption** below.

**Plan-state file (resumption).** Long-lived per-stage status lives in a
sibling `plan-state.md` next to this file. The plan itself is the
immutable contract; transient run-state goes in `plan-state.md`.

---

## Background a fresh agent must read first

This section is the minimum-viable context for someone with no prior
Razor knowledge. Skip if you've read the sibling
`razor-parser-analysis.md` recently --
otherwise read it first; it's the source for almost everything here.

### Architecture in one paragraph

Razor parsing lives in
`src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Legacy/`.
The entry point `RazorParser.Parse` constructs **two co-routine parsers** --
`HtmlMarkupParser` and `CSharpCodeParser` -- that share a single
`SeekableTextReader` over the source. HTML is the outer mode. When a parser
hits a transition (HTML sees `@`, C# sees `<` or `}` etc.) it calls into
the other, which runs to completion of its construct, rewinds the shared
character cursor via `PutCurrentBack()`, and returns. Each parser owns its
own tokenizer (`HtmlTokenizer`, `CSharpTokenizer`; the C# side also has
`RoslynCSharpTokenizer` which wraps Roslyn's `SyntaxTokenParser` for
lexing only). The syntax tree lives in
`src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Syntax/`,
defined in `Syntax.xml` and generated via the
`src/Razor/src/Compiler/tools/RazorSyntaxGenerator` tool.

### Why recovery is poor today (the two missing invariants)

1. **No skipped-tokens trivia (or equivalent node).** Razor `SyntaxToken`
   has _no_ leading/trailing trivia at all (compare Roslyn). Whitespace
   and newlines are first-class tokens (`SyntaxKind.Whitespace`,
   `SyntaxKind.NewLine`). There is therefore no place to put "garbage
   between expected tokens". Today, recovery folds everything skipped into
   a single fat literal node (`CSharpStatementLiteral`,
   `CSharpExpressionLiteral`, `MarkupTextLiteral`,
   `MarkupMiscAttributeContent`). One `MissingExpression` becomes
   "everything until the next `<`".

2. **No missing-token invariant.** `SyntaxFactory.MissingToken(kind)`
   _exists_ and is invoked at approximately **23 sites total** across
   `CSharpCodeParser` (8), `HtmlMarkupParser` (7), `TokenizerBackedParser` (4),
   and `TagHelperBlockRewriter` (4) (counted via
   `Select-String -Path "**\Legacy\*.cs" -Pattern "SyntaxFactory\.MissingToken"`
   -- re-run before relying on the exact number; some sites are
   duplicates within one expression). There is no parser-wide rule that
   "any expected-but-absent token is emitted as a zero-width missing
   token at the exact position with a precise diagnostic". Most parser
   functions instead `AcceptUntil(LessThan)` or `AcceptUntil(NewLine)`
   and produce a diagnostic against the _start_-of-construct location,
   with the absorbed range becoming part of the next big literal.

### Why this becomes a "wall of red" for the user

End-to-end for `<button @onclick="">`:

1. Parser produces a `MarkupAttributeBlock` with an empty `Value`.
2. `TagHelperBlockRewriter` reshapes as `MarkupTagHelperDirectiveAttribute`.
3. `DefaultTagHelperResolutionPhase.LegacyTagHelperResolver.LowerBoundLegacyAttributeValue`
   detects empty children and synthesises an empty `CSharpIntermediateToken("")`
   plus emits **RZ2008** (attribute requires a value).
4. Codegen emits something like
   `EventCallback.Factory.Create<MouseEventArgs>(this, )` -- syntactically
   broken C# -- into the generated file.
5. `RazorSourceGenerator` adds the generated text as a `SyntaxTree`;
   Roslyn parses it and emits **CS1525: Invalid expression term ')'**.
6. The CS diagnostic is mapped back to the `.razor` file via a
   `SourceMapping` whose `OriginalSpan` is wider than the offending value;
   the user sees the diagnostic underline a large region.

There are at least three independent contributors (parser, codegen,
mapping) -- this plan fixes the parser cause and forces codegen and
mapping to do their jobs accurately on a precise parse tree.

### The scope this plan covers

- **In scope:** parser-level recovery in both `HtmlMarkupParser` and
  `CSharpCodeParser`; the cross-parser handoff; the syntax tree
  (`Syntax.xml`) changes required to support skipped content; codegen
  guards that turn missing tokens into safe placeholders; source-mapping
  narrowing for missing/skipped regions; tests/baselines.
- **Out of scope (explicit non-goals):**
    - Merging the two parsers into one.
    - Replacing the `SeekableTextReader` character-cursor model with
      pre-tokenized token streams. (Pre-tokenization is a desirable
      future optimisation but is _not_ required for the recovery
      contract this plan establishes; it can be a follow-up.)
    - Delegating C# parsing inside Razor blocks to Roslyn's full parser
      (hybrid C+A from the original direction options). The
      `RoslynCSharpTokenizer` continues to be opt-in via
      `Options.UseRoslynTokenizer` and is unchanged.
    - The legacy editor's in-place incremental-parse infrastructure
      (`SpanEditHandler`, `AcceptedCharactersInternal`,
      `AutoCompleteEditHandler`). These are touched only where they
      directly intersect with recovery changes; broader cleanup is
      separate work.

### The big design decisions (pre-resolved here so reviewers don't re-litigate)

1. **Skipped content lives in a new tree _node_, not as trivia.** Adding
   leading/trailing trivia to `SyntaxToken` would require restructuring
   every node that holds tokens and every walker. Instead, introduce a
   new `SkippedContentSyntax` node that holds a `SyntaxList<SyntaxToken>`
   of skipped tokens and inserts inline into the tree wherever recovery
   skipped. Consumers that don't care (codegen for content, source
   mappings, etc.) ignore it; walkers see it like any other node.
2. **Missing-token invariant via `Required(kind, ...)`.** A new helper on
   `TokenizerBackedParser` either consumes the expected token (returning
   it) or emits `MissingToken(kind)` at the current position with the
   supplied diagnostic, _then synchronizes_ to a follow-set.
3. **Synchronize via `Synchronize(followSet)`.** A new helper that emits
   `SkippedContentSyntax` for tokens it skips until it sees a token in
   `followSet` (or EOF). Returns the skipped-content node (which the
   caller wraps into its parse-output, so source positions are preserved).
4. **Follow sets are explicit per parser function _and per language_.**
   Each parser function declares the tokens it can resync at (e.g.,
   `SemicolonOrRightBrace`, `OpenAngleOrTransition`). These compose: a
   child's follow set is the union of the child's "own" terminators
   and the parent's follow set.

   **Critical: the two languages tokenize differently.** Verified:
   `HtmlTokenizer` emits `SyntaxKind.OpenAngle` for `<` (see
   `HtmlTokenizer.cs:19,229`); both `NativeCSharpTokenizer` and
   `RoslynCSharpTokenizer` emit `SyntaxKind.LessThan` for `<` (see
   `NativeCSharpTokenizer.cs:478`). Same character, different
   `SyntaxKind` per tokenizer. The same applies to `>`/`GreaterThan`
   vs `>`/`CloseAngle`, and to quotes / slashes (the C# tokenizer
   typically eats `"` as part of a `StringLiteral`, so the `"` kind
   isn't a useful sync token in C# at all).

   So a `FollowSet` is **language-scoped**: an HTML-side follow set
   contains HTML kinds; a C#-side follow set contains C# kinds.
   Synchronize matches against the active tokenizer's kinds only.

   At a cross-language boundary, the caller's follow set must be
   **translated** to the callee's language before being threaded as
   `outerFollow`. The translation table is small:

   | Caller (HTML) kind | Callee (C#) kind |
   |--------------------|------------------|
   | `OpenAngle` (`<`) | `LessThan` |
   | `CloseAngle` (`>`) | `GreaterThan` |
   | `ForwardSlash` (`/`) | `Slash` |
   | `DoubleQuote` (`"`) | (no equivalent; `"` becomes part of `StringLiteral` in C#) -- drop from translation |
   | `SingleQuote` (`'`) | (similar; `'` becomes `CharacterLiteral`) -- drop |
   | `Whitespace`, `NewLine`, `Equals` | same kind in both (they're shared / structural) |

   And in the opposite direction (C# follow set translated to HTML):

   | Caller (C#) kind | Callee (HTML) kind |
   |------------------|--------------------|
   | `LessThan` | `OpenAngle` |
   | `GreaterThan` | `CloseAngle` |
   | `Slash` | `ForwardSlash` |
   | `Semicolon`, `LeftBrace`, `RightBrace`, `LeftParenthesis`, `RightParenthesis` | (no HTML equivalent; drop) |
   | `Transition` (`@`) | `Transition` (same kind in both) |

   These translations are implemented as `FollowSet.ForCSharpCallee()`
   and `FollowSet.ForHtmlCallee()` static methods on the
   `RecoveryFollowSets` helper class (Stage 4.1). They run at the
   call site, NOT inside `Synchronize`.

   The `FollowSet` representation: a struct holding two `ulong`s
   keyed on the kind value's low byte (`SyntaxKind` is `enum : byte`,
   verified at `SyntaxKind.cs:6`), plus an optional
   `Predicate<SyntaxToken>` slot for special cases. With ~121 kinds
   today and `FirstAvailableTokenKind` at value 119, the two-ulong
   layout has ~7 spare slots; add a `Debug.Assert((int)kind < 128)`
   in the set/test methods so adding a new kind beyond byte value 127
   is caught at debug time. Profile before optimizing further; the
   simple representation may be fine.
5. **Codegen emits safe placeholders for missing C#.** Where a C# child
   of an emit construct is a `MissingToken` of an expression kind (or a
   zero-width / empty C# block), codegen emits a syntactically valid
   placeholder. The full per-context placeholder matrix is in
   **Reference: Codegen placeholder matrix** below. This is what kills
   the cascading CS errors.
6. **Source mappings are split at missing/skipped boundaries.** A
   `MissingToken` produces a zero-width mapping at the insertion point;
   a `SkippedContentSyntax` produces no mapping at all (the skipped text
   has no generated counterpart). The current "one mapping per literal"
   behaviour is what currently widens diagnostics.
7. **Feature-flag the new behaviour during migration.** Add a
   `UseEnhancedRecovery` bit to `RazorParserOptions.Flags` (see Stage
   0.3 for the exact flag-based pattern), default off. New tests target
   the new mode; existing tests run under the old mode until each
   parser function is migrated and its baselines are updated. After
   full migration, the flag flips to default `true` and the legacy
   paths are deleted.
8. **The two-parser coroutine architecture is preserved.** Cross-parser
   synchronization is a contract on the handoff: the caller passes the
   _outer_ follow set into the callee; the callee uses it to stop early
   when garbage is unrecoverable in the inner grammar but recoverable in
   the outer one.
9. **A "missing C# attribute value" is represented as
   `GenericBlock([CSharpExpressionLiteral([MissingToken(Identifier)])])`.**
   The `Value` slot of `MarkupAttributeBlockSyntax` and the `Value`
   slot of `MarkupTagHelperAttributeSyntax`/`MarkupTagHelperDirectiveAttributeSyntax`
   are typed `RazorBlockSyntax` (and `MarkupTagHelperAttributeValueSyntax`
   respectively -- itself a `RazorBlockSyntax`), so a bare `SyntaxToken`
   cannot go there. Wrapping a single `MissingToken(Identifier)` inside
   a `CSharpExpressionLiteralSyntax` inside a `GenericBlockSyntax` /
   `MarkupTagHelperAttributeValueSyntax` keeps the existing tree
   contract intact while encoding "the expression is missing here".
   Codegen (Stage 5.1) detects this exact shape and emits the safe
   placeholder; tag-helper resolution (Stage 5.0) propagates the
   "missing value" signal to the IR.
10. **`SkippedContentSyntax.OriginatingLanguage`** carries the language
    context in which the skip happened (`CSharpCodeBlock` or
    `MarkupBlock`, or unset for document-level). Stage 5.6
    (completion / classification) uses this to dispatch to the
    appropriate language provider when the cursor lands inside skipped
    content.

### What "verified" looks like for this plan

Every stage has _executable_ exit criteria. The plan never says
"complete when done"; it says "complete when `dotnet test
src\Razor\src\Compiler\Microsoft.AspNetCore.Razor.Language\test\Microsoft.AspNetCore.Razor.Language.UnitTests.csproj
--filter 'FullyQualifiedName~ParserRecoveryCorpusSnapshotTests'`
shows 0 failures and the listed baselines match" or similar.

**The plan is intentionally strict about this**: if a stage says "0 HIGH
issues", that means 0 in the corpus snapshots, not 0 across the whole
test suite. Where prose contains softer phrases ("audit", "tests cover",
"a focused suite") -- those are inputs you must convert to specific
test filter strings before claiming the stage done. If you can't make
that conversion from a stage as written, escalate.

**Re: PowerShell vs grep.** All shell snippets in this plan are intended
for PowerShell on Windows. Where the plan shows `grep`, that's
`Select-String` on PowerShell-only machines:
`(Select-String -Path "<file>" -Pattern "<regex>" -SimpleMatch:$false).Count`.
If `grep`/`rg` is available via Git Bash or a separate install, it's
fine to use. The intent is the count, not the tool.

---

## Prerequisites

The executor must, before Stage 0, confirm all of:

| Check | Command | Expected |
|-------|---------|----------|
| Repo clone present | `cd D:\projects\roslyn4 && git --no-pager status` | Working tree clean or expected dirty state |
| .NET SDK | `dotnet --version` | Matches `global.json` (currently `10.0.*`) |
| Razor sub-tree present | `dir D:\projects\roslyn4\src\Razor\src\Compiler` | Lists `Microsoft.CodeAnalysis.Razor.Compiler` |
| Razor solution build | `dotnet build D:\projects\roslyn4\Razor.slnf` | succeeds. **Use `Razor.slnf`, NOT `Compilers.slnf`.** The latter does not include `Microsoft.CodeAnalysis.Razor.Compiler.csproj` or either parser test project (verified: `Compilers.slnf` only references the `ExternalAccess.RazorCompiler` projects). Building only `Compilers.slnf` will let Razor-side compile errors slip through as false-greens. |
| Razor parser tests baseline (new) | `dotnet test D:\projects\roslyn4\src\Razor\src\Compiler\Microsoft.AspNetCore.Razor.Language\test\Microsoft.AspNetCore.Razor.Language.UnitTests.csproj --no-build` | All green (record count for Phase 0) |
| Razor parser tests baseline (legacy) | `dotnet test D:\projects\roslyn4\src\Razor\src\Compiler\Microsoft.AspNetCore.Razor.Language\legacyTest\Microsoft.AspNetCore.Razor.Language.Legacy.UnitTests.csproj --no-build` | All green (record count for Phase 0). This project owns the existing parser-error tests (e.g., `CSharpErrorTest.cs`, `HtmlBlockTest.cs`) that will be baseline-churned by Stages 2-3. |
| Syntax generator runs | `dotnet run --project D:\projects\roslyn4\src\Razor\src\Compiler\tools\RazorSyntaxGenerator\RazorSyntaxGenerator.csproj -- <Syntax.xml path> <output dir>` | Regenerates the three `Syntax.xml.*.Generated.cs` files identically |

If any of these fails: **do not start Stage 0**. Fix the prerequisite or
escalate to the user. Common causes:

- SDK mismatch -> `global.json` pin updated. **Block** -- fix before
  continuing.
- Syntax generator can't run -> `tools/RazorSyntaxGenerator` project
  itself is broken. **Block** -- fix before continuing.
- Razor.slnf build fails -> the repo is in a broken state.
  **Block** -- fix before continuing.
- Pre-existing failing tests in either parser test project ->
  **record and continue**. Snapshot the names of failing tests, count
  them, and add them to `plan-state.md` under "Pre-existing test
  failures". Stage 0 will use this as the baseline; later stages must
  not regress past it. If the count is large enough to obscure
  regression detection (say, > 10), escalate.

### When to stop and ask the user (escalation triggers)

Stop and ask immediately if:

- Any of the Big Design Decisions (1-8 above) is challenged by something
  you discover during a stage and the discovery makes the decision look
  wrong. Decisions are pre-resolved precisely so the executor doesn't
  re-litigate them; deviating from them needs explicit user sign-off.
- A stage's exit criteria turn out to be unmeasurable as written.
  Don't invent new criteria; ask.
- Baseline updates would lose more than ~5% of existing test coverage
  semantics (i.e., tests pass but the new baselines no longer assert
  what they used to). Recovery refactors necessarily change baselines,
  but coverage erosion is a regression.
- A downstream consumer (formatter, LSP, completion) breaks in a way
  that isn't a straightforward update to the new node shape.
- Anything in the codegen / source-generator integration would require
  shipping a Razor SDK update before the parser change can land.

Don't stop for: routine baseline diff, garden-variety merge conflicts,
choosing which sub-helper to extract, etc. Use judgement; ship a stage
when its exit criteria pass.

---

## Stage 0 -- Foundation: corpus, baselines, infra (no behaviour change)

> **A note on line citations throughout the plan.** Specific line numbers
> (`line 442`, `line ~547`) are pinned to the repo state at the time of
> drafting (see synthesis doc `round-1-synthesis.md` for the commit
> reference). Line numbers drift as PRs land. Always treat them as
> approximate -- locate the cited function/snippet by name, then
> confirm the line range before editing. If line numbers diverge from
> the cited values by more than ~20 lines, do a quick check that you're
> looking at the right region; the underlying construct is the
> authoritative anchor, not the number.

**Goal.** Establish the regression-safety net before any parser changes
land: a golden-baseline corpus of "single bad character" cases, baseline
diagnostic snapshots, the feature flag, and the new tree node.

### Stage 0.0 -- Initialise `plan-state.md`

Before any other work, create the sibling state file. Path: the same
directory as this plan, i.e.
`src/Razor/docs/plans/ErrorRecovery/razor-recovery-redesign-plan-state.md`.
(During a fresh draft the same convention applies: state file is
sidecar to the plan wherever the plan lives.)

Initial content:

```markdown
# Razor recovery plan -- state

## Current stage
Stage 0.1 -- Build the "wall of red" corpus

## Status of each stage
- Stage 0.0: complete
- Stage 0.1: not started
- Stage 0.2: not started
- (... all sub-stages from the plan listed as "not started" ...)

## Diagnostic IDs allocated
(none yet)

## Stage 5.0.0 spike report
(not yet run -- to be populated with: malformed expression, writer file:line, IR node type, placeholder shape)

## Stage 5.6.0 LSP anchor classes
(not yet identified -- to be populated with classification / completion / hover provider class names)

## Stage 2 verification
(parsing-recovery branch counts; populate during Stage 2 gate check)

## BaselineWriter location decision
(not yet decided -- Stage 0.2)

## Pre-existing test failures (from prereqs)
(none recorded; populate during prereq check)

## Performance baseline
(not yet measured -- Stage 6.4)

## Notes
(append notes per-stage as needed)
```

The state file is not committed to the repo until Stage 7 (it's
session-scoped during execution). Stage 7 commits it alongside the
plan to the canonical docs location, marked historical. However it
IS the durable record of plan progress across sessions / agents --
so if you're resuming mid-execution, ALWAYS update it.

Exit criteria: file exists; the skeleton sections above are present.

### Stage 0.1 -- Build the "wall of red" corpus

Create the corpus folder at:

```
src/Razor/src/Compiler/Microsoft.AspNetCore.Razor.Language/legacyTest/ParserRecoveryCorpus/
```

(Rationale: the existing parser-error tests live in `legacyTest/Legacy/`;
the corpus belongs alongside them. The new `test/` project is for
non-legacy test infrastructure and should not be polluted with
parser-recovery fixtures.)

Populate with at minimum these files (each one a `.razor` snippet),
and expect to add to the corpus through later stages:

- `EmptyBoundAttribute_Onclick.razor` -- `<button @onclick="">click</button>` plus surrounding markup
- `UnclosedExplicitExpression.razor` -- `@(foo.Bar` mid-document
- `UnclosedIfParen.razor` -- `@if(foo bar\nbaz` mid-document
- `UnclosedCodeBlock.razor` -- `@{ var x = 1;` (no closing brace) followed by markup
- `UnclosedString.razor` -- `@{ var s = "hello }` (string runs past brace)
- `MalformedTagAttribute.razor` -- `<input @bind=>` (no value after =)
- `MidStatementGarbage.razor` -- `@{ var x = ?? 1; }` (unexpected token mid-statement)
- `UnclosedTag.razor` -- `<div><span><p>text</div>` (mismatched closes)
- `BareAtFollowedByGarbage.razor` -- `@!@#$ <p>real markup</p>`
- `EmptyExplicitExpression.razor` -- `@()`

For each file, the corpus test records two scopes:

1. **Parser-side** (Stage 0.2): the list of Razor parser diagnostic
   IDs + spans, plus the parser-side metrics (Razor-diagnostic counts,
   widths) listed in the exit criteria below.
2. **End-to-end** (Stage 5.1's e2e harness): the list of C# diagnostics
   produced by Roslyn after the source-generator runs against a
   representative containing project (a minimal Blazor app shape).
   This is **out of scope for Stage 0.2** -- the codegen-side data
   lives in Stage 5.1's snapshot file (`Codegen.json` sibling).

Exit criteria:

- Corpus folder exists with the listed files committed.
- `dotnet test ... --filter ParserRecoveryCorpus_Snapshot` produces the
  baseline files and all assertions pass.
- The "wall of red" property is captured numerically per case. Two
  metric scopes:
    - **Parser-side** (recorded by Stage 0.2 snapshot harness):
      number of Razor diagnostics, widest single Razor diagnostic
      span, total Razor diagnostic span chars.
    - **End-to-end** (recorded by Stage 5.1 e2e harness, NOT Stage
      0.2): `cascading_csharp_diag_count`, `widest_diag_span_chars`
      (after mapping CS diagnostics back through SourceMapping),
      `total_diag_chars_underlined`.
  Later stages drive both scopes towards zero (or near-zero).

### Stage 0.2 -- Snapshot harness (parser-only)

Add a snapshot test class `ParserRecoveryCorpusSnapshotTests` in
`src/Razor/src/Compiler/Microsoft.AspNetCore.Razor.Language/legacyTest/Legacy/`
(extends `ParserTestBase`; see existing parser tests for the pattern).

For each corpus file:

- Parses with current (legacy) recovery -> records `Legacy.json` snapshot.
- Parses with `UseEnhancedRecovery = true` once the flag exists (initial
  Stage 0 commit: skipped; later: enabled per corpus file as that
  feature class is migrated).

**Snapshot content.** Snapshots include: token stream summary, tree
shape (kinds only, no content), Razor diagnostic IDs + spans, and the
**parser-side** source-position list (each significant tree node's
start/length). The source-mapping width metrics described in
Stage 5.3 require a `RazorCSharpDocument` (produced by codegen, not
parse) and are authored / consumed in Stage 5.1's e2e harness inside
`Microsoft.NET.Sdk.Razor.SourceGenerators.UnitTests` -- *not* here.
This split honours the Stage 0.2 parser-only scope.

**Scope: parser-only.** Stage 0.2 snapshots cover *parser-side* data:
token stream summary, tree shape (kinds only, no content), Razor
diagnostic IDs + spans, parser-side source-position list. The three
end-to-end wall-of-red metrics from Stage 0.1
(`cascading_csharp_diag_count`, `widest_diag_span_chars`,
`total_diag_chars_underlined`) require running the **source generator
+ Roslyn** on the generated text, which is downstream work owned by
Stage 5.1's e2e harness in
`Microsoft.NET.Sdk.Razor.SourceGenerators.UnitTests`. **Do not try to
add the source-generator path here** -- the `legacyTest` project
doesn't reference the source-generator infra, and forcing the
reference creates circular/scope problems. Stage 5.1 produces its own
sibling snapshot file (e.g., `Codegen.json`) per corpus case; the two
files together describe end-to-end behaviour.

**Baseline-update mechanism.** Use `/p:GenerateBaselines=true`
**consistently**. Drop any references to env-vars or
`--update-baselines` switches; the existing
`IntegrationTests/CodeGenerationIntegrationTest.cs` + `BaselineWriter`
pattern is the source of truth. To regenerate a baseline:

```powershell
dotnet test `
  D:\projects\roslyn4\src\Razor\src\Compiler\Microsoft.AspNetCore.Razor.Language\legacyTest\Microsoft.AspNetCore.Razor.Language.Legacy.UnitTests.csproj `
  --filter "FullyQualifiedName~ParserRecoveryCorpusSnapshotTests" `
  /p:GenerateBaselines=true
```

The snapshot file naming convention: `<CorpusFileName>.<Mode>.json`
where `<Mode>` is `Legacy` or `Enhanced`. Both files live alongside
the corpus `.razor` file under `legacyTest/ParserRecoveryCorpus/`.

**Note: `BaselineWriter.cs` lives in the `test/` project**
(`Microsoft.AspNetCore.Razor.Language.UnitTests`), but the corpus
snapshot tests live in the `legacyTest/` project, which does not
reference the `test/` project. Pick one of:

- (preferred) Copy the BaselineWriter pattern into `legacyTest/Legacy/`
  as a sibling file. Keeps the snapshot harness self-contained;
  small duplication is acceptable.
- (riskier) Refactor `BaselineWriter` into `Microsoft.AspNetCore.Razor.Test.Common`
  (the shared test-common project, already referenced from both).
  Broader blast radius; only do this if other test projects also need
  it.

Document the choice in `plan-state.md`.

Exit criteria: harness runs; produces legacy snapshots for every corpus
file; the snapshot files are committed (they are the moving target of
this plan). The harness emits the parser-side metrics; the codegen-side
metrics are explicitly out-of-scope for this stage.

### Stage 0.3 -- Add the feature flag

`RazorParserOptions` is partial-class flag-based, NOT init-property
based. Verify the existing shape before editing:
`src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/RazorParserOptions.cs`
(main class with `private readonly Flags _flags`),
`RazorParserOptions.Flags.cs` (the `Flags` enum), and
`RazorParserOptions.Builder.cs` (the builder used by
`RazorParserOptions.Create(...)`).

Add the flag using the same pattern as `UseRoslynTokenizer`:

1. In `RazorParserOptions.Flags.cs`, add a new bit:
   ```csharp
   UseEnhancedRecovery = 1 << <next_unused_bit>,
   ```
   (Inspect the file first to pick the next unused power of 2.)

2. In `RazorParserOptions.cs`, add a public getter following the
   `UseRoslynTokenizer` template:
   ```csharp
   public bool UseEnhancedRecovery
       => _flags.IsFlagSet(Flags.UseEnhancedRecovery);
   ```

3. In `RazorParserOptions.Builder.cs`, add the matching setter so
   `Create(..., configure: b => b.UseEnhancedRecovery = true)` works.
   Match the surrounding patterns (don't invent a new shape).

4. **In `RazorParserOptions.cs` `WithFlags(...)` method** (verified at
   `RazorParserOptions.cs:113`), add a matching `Optional<bool>
   useEnhancedRecovery = default` parameter and the corresponding
   conditional flag-update block (follow the existing pattern).
   **Note:** the compiler project (`Microsoft.CodeAnalysis.Razor.Compiler.csproj`)
   does NOT use the `Microsoft.CodeAnalysis.PublicApiAnalyzers` package
   and has no `PublicAPI.*.txt` files (verified via
   `Get-ChildItem D:\projects\roslyn4\src\Razor\src\Compiler\Microsoft.CodeAnalysis.Razor.Compiler -Recurse -Filter PublicAPI*` returns nothing).
   So **no `PublicAPI.Unshipped.txt` update is required**. Other Razor
   projects (Workspaces, Remote, etc.) do use PublicAPI files, but
   `RazorParserOptions` doesn't surface through them directly. If a
   downstream project does need the API tracking, the build will tell
   you (`RS0016`/`RS0017` analyzer errors).

5. Plumb through `ParserContext`: `ParserContext` already exposes
   `Options`, so parsers can read `Context.Options.UseEnhancedRecovery`
   directly. No new `ParserContext` field is needed.

6. Default: the flag bit is **not** in the default flags returned by
   `GetDefaultFlags(...)` (so existing parses behave as before). New
   tests opt in via `RazorParserOptions.Create(..., b => b.UseEnhancedRecovery = true)`.

7. Update `RazorParserOptionsTest.cs` to cover the new flag with the
   same shape as existing flag tests, including a `WithFlags` round-trip
   test.

8. Add a corresponding flag bit to any per-flag downstream surfaces
   (none expected as of writing -- run a quick `Select-String -Path
   src\Razor -Pattern "UseRoslynTokenizer"` and ensure every shape
   that surfaces `UseRoslynTokenizer` also gets a sibling for
   `UseEnhancedRecovery` if appropriate; if not, that's expected,
   just document why).

Exit criteria:

- Flag is reachable from both parsers via `Context.Options.UseEnhancedRecovery`.
- Defaults match: `RazorParserOptions.Default.UseEnhancedRecovery == false`.
- All existing tests pass unchanged.
- `RazorParserOptionsTest.cs` has at least one test that confirms the
  flag round-trips through `Create` + `Builder`.

### Stage 0.4 -- Add `SkippedContentSyntax` to `Syntax.xml`

Edit `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Syntax/Syntax.xml`:

Add a new node (suggest placing near `MarkupMiscAttributeContentSyntax`
for thematic proximity, but any consistent location is fine):

```xml
<Node Name="SkippedContentSyntax" Base="RazorSyntaxNode">
  <Kind Name="SkippedContent" />
  <Field Name="SkippedTokens" Type="SyntaxList&lt;SyntaxToken&gt;" />
  <Field Name="OriginatingLanguage" Type="SyntaxKind" />
</Node>
```

`OriginatingLanguage` is metadata: `SyntaxKind.CSharpCodeBlock` if the
skip happened in a CSharp context, `SyntaxKind.MarkupBlock` if in
HTML, or `SyntaxKind.None` for the unset/document-level case. **Note:
the field is not `Optional="true"`.** The syntax generator does
support `Optional` on value-type fields (e.g., `bool IsMarkupTransition`
already uses it -- see `Syntax.xml:180,202`), so the choice here is
*design*, not *capability*. Using a non-Optional field with
`SyntaxKind.None` as the sentinel is explicit, matches established
sentinel idioms in the parsers (e.g., `quote = SyntaxKind.Marker` in
`HtmlMarkupParser.cs`), and avoids the slight ambiguity of "is unset
'unset' or is unset 'CSharpCodeBlock = 0'?" that Optional-on-enum
would introduce.

**Manually edit `SyntaxKind.cs`.** `SyntaxKind.cs` is hand-authored
(no `// <auto-generated />` header, lives in `Syntax/` not
`Syntax/Generated/`, has the `FirstAvailableTokenKind` sentinel for
hand-edit slots). The generator does **not** regenerate it. After
adding the `<Kind>` to `Syntax.xml`, also add `SkippedContent` to the
appropriate `#region Nodes` block in `SyntaxKind.cs`, before
`FirstAvailableTokenKind`. Follow the surrounding member ordering
conventions.

**Verify the generator's output before committing.** Run the generator
end-to-end and inspect the produced
`Generated/Syntax.xml.Internal.Generated.cs`,
`Generated/Syntax.xml.Main.Generated.cs`,
`Generated/Syntax.xml.Syntax.Generated.cs` to confirm:

- `InternalSyntax.SkippedContentSyntax` is generated with both fields.
- `OriginatingLanguage` shows up as a non-nullable `public SyntaxKind`
  property (matches the no-Optional design above).
- `SyntaxFactory.SkippedContent(SyntaxList<SyntaxToken>, SyntaxKind)`
  factory method is generated.
- The corresponding red-tree wrapper class compiles.

Regenerate:

```powershell
dotnet run --project D:\projects\roslyn4\src\Razor\src\Compiler\tools\RazorSyntaxGenerator\RazorSyntaxGenerator.csproj `
  -- D:\projects\roslyn4\src\Razor\src\Compiler\Microsoft.CodeAnalysis.Razor.Compiler\src\Language\Syntax\Syntax.xml `
  D:\projects\roslyn4\src\Razor\src\Compiler\Microsoft.CodeAnalysis.Razor.Compiler\src\Language\Syntax\Generated
```

Verify the generator's CLI is positional `<input> <output>` by reading
`tools/RazorSyntaxGenerator/Program.cs` first; if it differs, adjust
the command accordingly.

Update `SyntaxVisitor` / `SyntaxWalker` / `SyntaxRewriter` defaults: the
generated `VisitSkippedContent` default is "visit children" (the
generator handles this automatically for new nodes). This is fine for
walkers; **codegen** and **source-mapping** consumers must explicitly
skip it (Phase 5).

Exit criteria:

- `dotnet build Razor.slnf` succeeds (NOT `Compilers.slnf` -- see Prerequisites table).
- All existing tests still pass.
- `SkippedContentSyntax` is reachable from `SyntaxKind.SkippedContent`
  in both internal and red layers.
- The `OriginatingLanguage` field is typed `SyntaxKind` (not
  `SyntaxKind?`) and round-trips through factory + getter, with
  `SyntaxKind.None` as the unset sentinel.

### Stage 0.5 -- `SkippedContent` is "ignored" by content-emitting consumers

Audit (do not yet fix) all places that walk tokens to produce output.

The single canonical list of helpers to audit (read the actual code,
don't trust this list -- it's a starting point):

- `TokenizerBackedParser.OutputAsMarkupLiteral`,
  `OutputAsMarkupLiteralRequired`, `OutputAsMarkupEphemeralLiteral`,
  `OutputAsMetaCode`.
- `CSharpCodeParser.OutputTokensAsStatementLiteral`,
  `OutputTokensAsExpressionLiteral`, `OutputTokensAsEphemeralLiteral`.
- `GreenNode.ToFullString` / `RazorSyntaxNode.GetContent` callers --
  enumerate via (PowerShell needs `-Recurse` to walk the tree; the
  `\*\*\` glob in `-Path` is not recursive on its own, and
  `-SimpleMatch` makes regex metachars literal):
  ```powershell
  Get-ChildItem -Path "D:\projects\roslyn4\src\Razor\src\Compiler" -Recurse -Filter "*.cs" |
      Select-String -Pattern 'GetContent\(\)' |
      Select-Object -Property Path, LineNumber, Line
  ```
- IR-level token consumers in `DefaultRazorIntermediateNodeLoweringPhase.cs`
  and `DefaultTagHelperResolutionPhase.LegacyTagHelperResolver.cs` that
  copy tokens into IR nodes.

Produce a checklist file `parser-recovery-audit-skipped-content.md`,
**committed to the repo** alongside the corpus
(`src/Razor/src/Compiler/Microsoft.AspNetCore.Razor.Language/legacyTest/ParserRecoveryCorpus/`),
listing each call site (file:line + function) and whether it (a) needs
to ignore `SkippedContent`, (b) needs to skip-and-warn, or
(c) is unaffected. Phase 5 will execute the checklist; Stage 0 just
produces it.

Exit criteria: audit file committed under the corpus directory; no
behaviour changes yet.

### Stage 0 exit criteria (gate to Phase 1)

- 0.0-0.5 individually complete.
- Both parser test projects are green:
  `dotnet test src\Razor\src\Compiler\Microsoft.AspNetCore.Razor.Language\test\Microsoft.AspNetCore.Razor.Language.UnitTests.csproj` and
  `dotnet test src\Razor\src\Compiler\Microsoft.AspNetCore.Razor.Language\legacyTest\Microsoft.AspNetCore.Razor.Language.Legacy.UnitTests.csproj`.
- `dotnet build D:\projects\roslyn4\Razor.slnf` succeeds.
- The corpus snapshot tests pass (against legacy behaviour).
- The feature flag exists and defaults off; round-trips through `Create`+`Builder`.
- `SkippedContentSyntax` exists, has `OriginatingLanguage` field, is
  visitor-safe.
- The audit checklist (Stage 0.5) is committed to the corpus directory.
- `plan-state.md` exists with the Stage 0.0 skeleton.

---

## Stage 1 -- Establish the recovery primitives

**Goal.** Add `Required(kind, ...)`, `Synchronize(followSet)`, and the
follow-set composition machinery to `TokenizerBackedParser`. No parser
function is migrated yet.

### Stage 1.1 -- `Synchronize(followSet)` helper

In `TokenizerBackedParser`, add:

```csharp
public readonly record struct SyncResult(
    InternalSyntax.SkippedContentSyntax? Skipped,
    SyncStopReason StopReason);

public enum SyncStopReason
{
    AtFollowToken,        // hit a token in the local follow set
    AtOuterFollowToken,   // hit a token in the outer/caller follow set (cross-language case)
    AtNewLine,
    AtTransition,
    EndOfFile,
}

/// <summary>
/// Skips tokens until one in <paramref name="localFollow"/> is current,
/// one in <paramref name="outerFollow"/> is current, EOF is reached, or
/// a stop condition in <paramref name="options"/> fires.
///
/// Returns a SyncResult that includes the SkippedContentSyntax (or null
/// if nothing was skipped) AND the reason synchronization stopped. The
/// stop reason matters for cross-language sync (Stage 4.4): if it's
/// AtOuterFollowToken, the caller knows it should bail back to the
/// outer parser rather than continuing inner-grammar work.
///
/// The current token is positioned at the synchronization point (NOT
/// consumed) on return.
/// </summary>
protected SyncResult Synchronize(
    FollowSet localFollow,
    FollowSet outerFollow,
    SyntaxKind originatingLanguage,
    SyncOptions options = SyncOptions.None);

// Convenience overload (same-language sync, no outer set):
protected SyncResult Synchronize(
    FollowSet localFollow,
    SyntaxKind originatingLanguage,
    SyncOptions options = SyncOptions.None)
    => Synchronize(localFollow, FollowSet.Empty, originatingLanguage, options);
```

The result type is a `record struct` so `default` is a valid "no skipped
content, EOF" result. Razor's green nodes live in the
`Microsoft.AspNetCore.Razor.Language.Syntax.InternalSyntax` namespace.
The parser files under `Legacy/*Parser.cs` `using ...Syntax.InternalSyntax;`
already, so within those files the unqualified `SkippedContentSyntax`
name resolves to the green type (parallel to how `RazorCommentBlockSyntax`,
`MarkupBlockSyntax`, etc. in those files refer to green types). The
qualifier `InternalSyntax.SkippedContentSyntax` in the abstract
`Synchronize` signature above is for clarity when reading the abstract
base class -- it's not strictly required inside the parser files
themselves.

`FollowSet` representation: see Big Design Decision #4. Start with a
struct holding two `ulong`s plus an optional `Predicate<SyntaxToken>`
slot. Provide:

```csharp
public readonly struct FollowSet
{
    public static readonly FollowSet Empty = default;
    public FollowSet(params SyntaxKind[] kinds) { ... }
    public bool Contains(SyntaxKind kind) { ... }
    public FollowSet Union(FollowSet other) { ... }
    public static FollowSet operator |(FollowSet a, FollowSet b) => a.Union(b);
}
```

Add a debug assertion: `Debug.Assert((int)kind < 128)` in `Contains`/
constructor. The two-ulong layout has ~7 spare slots above the current
`FirstAvailableTokenKind`; any new kind beyond byte value 127 requires
extending the layout.

`SyncOptions`: enum with `None`, `StopAtNewLine`, `StopAtTransition`.
(Removed `StopAtOuterFollow` from earlier drafts -- it's implicit in
the `outerFollow` parameter.)

Exit criteria:

- `Synchronize` and `FollowSet` are callable from both parsers.
- Unit tests in a new `TokenizerBackedParserRecoveryTests` class
  (added to `legacyTest/Microsoft.AspNetCore.Razor.Language.Legacy.UnitTests.csproj`)
  cover: synchronize to single token (local follow), synchronize to
  outer-follow token (stop reason `AtOuterFollowToken`), synchronize
  across many tokens, synchronize hits EOF, synchronize hits
  `StopAtNewLine`, synchronize at current token (no-op).
- Synchronize does NOT call `Accept` -- it produces a node the caller
  inserts into its own builder. This keeps the literal-token pipeline
  separate.
- Synchronize honours `CancellationToken.ThrowIfCancellationRequested()`
  in its inner loop.
- `FollowSet`'s assertion fires (debug-only) if a kind exceeds value 127.

### Stage 1.2 -- `Required(kind, ...)` helper

In `TokenizerBackedParser`, add:

```csharp
/// <summary>
/// If the current token's kind is <paramref name="kind"/>, consume it
/// and return it (with null skipped-content). Otherwise emit
/// MissingToken(kind) at the current position with
/// <paramref name="diagnostic"/> attached, synchronize to
/// <paramref name="recovery"/>, and return the MissingToken plus the
/// SkippedContent. Caller is responsible for placing both into its
/// output in the correct positional order.
///
/// The diagnostic, when attached to a missing token, must NOT also be
/// emitted to ErrorSink -- the missing-token attachment IS the
/// diagnostic. (Pre-existing parser functions that double-emit must be
/// audited when migrated; see Stage 1.4 for the pattern.)
/// </summary>
protected (SyntaxToken token, InternalSyntax.SkippedContentSyntax? skipped)
Required(
    SyntaxKind kind,
    RazorDiagnostic diagnostic,
    FollowSet recovery,
    SyntaxKind originatingLanguage);
```

Variants: `Required` with multiple acceptable kinds (one of N), and
`Optional(kind)` for the cases where missing is acceptable (no
diagnostic).

Diagnostics convention:

- Position is the **current** location (where the token was expected).
- Length is zero.
- The diagnostic ID is whatever the call site supplies (preserves
  RZ-numbering).
- The diagnostic is attached to the `MissingToken` and **not** also
  pushed to `Context.ErrorSink.OnError(...)`. The `ErrorSink` collection
  is populated automatically as the tree is built (token-attached
  diagnostics flow through `Accept` -> `TokenBuilder.Add` -> green-tree
  diagnostic table). Double-emit is a common migration bug; the audit
  in Stage 1.4 catches it for the pilot, and each migration sub-stage
  in Stage 2/3 must repeat the check.

Exit criteria:

- Both `Required` overloads callable; unit tests cover the consume case,
  the missing case (with and without recovery), and the
  multi-acceptable-kind case.
- A unit test confirms that calling `Required` for a missing token
  results in exactly one diagnostic in the final tree (i.e., the
  ErrorSink doesn't get an extra copy).

### Stage 1.3 -- Diagnostic factory updates

Audit `RazorDiagnosticFactory` for diagnostics whose source span is
authored as "from start of construct to current cursor". For every such
factory used in a recovery context, add a paired factory whose span is
a single zero-width position (the missing-token site).

The existing factories are NOT removed (legacy code paths still call
them). The new ones are suffixed `_At` or similar (e.g.,
`CreateParsing_ExpectedCloseBracket_At(SourceLocation, ...)`). Document
the pairing in the factory file.

**Allocating new RZ IDs.** Any genuinely new diagnostic (not a paired
`_At` variant of an existing one) must claim the next free
`RZxxxx` number. **RZ IDs are constructed in `RazorDiagnosticFactory.cs`
as `$"{DiagnosticPrefix}NNNN"` (where `DiagnosticPrefix = "RZ"`), NOT
stored in `Resources.resx`.** To find the next free ID:

1. `Select-String -Path "src\Razor\src\Compiler\Microsoft.CodeAnalysis.Razor.Compiler\src\Language\RazorDiagnosticFactory.cs", "src\Razor\src\Compiler\Microsoft.CodeAnalysis.Razor.Compiler\src\Language\Components\ComponentDiagnosticFactory.cs" -Pattern '\$"\{DiagnosticPrefix\}(\d{4,5})"'`
2. Existing ID ranges (verified at draft time; re-verify):
   - **RZ0xxx** -- general / infrastructure.
   - **RZ1xxx** -- parser diagnostics. New parser-recovery diagnostics
     go here (next free integer above the current max RZ1xxx).
   - **RZ2xxx** -- tag-helper / binding diagnostics (e.g., RZ2008).
   - **RZ3xxx** -- descriptor / tag-helper-resolution diagnostics.
   - **RZ9xxx, RZ10xxx** -- component-specific diagnostics (declared
     in `ComponentDiagnosticFactory.cs`).
3. Record allocations in `plan-state.md` under a "Diagnostic IDs
   allocated" section so subsequent stages don't collide.

Exit criteria:

- New factories exist for every diagnostic used in
  `AcceptUntil(...)`-style recovery (target the ~23 sites identified
  in the missing-token-invariant background section -- but the
  number is approximate; produce a per-site list during the audit).
- Diagnostics inventory file `parser-recovery-diagnostics-pairing.md`
  exists in the corpus directory and pairs every legacy factory with
  its new `_At` counterpart.
- All new RZ IDs are recorded in `plan-state.md`.

### Stage 1.4 -- Pilot: migrate `ParseRazorComment`

`TokenizerBackedParser.ParseRazorComment` is the smallest parser function
that demonstrates the new pattern end-to-end. Today it both creates
`MissingToken`s for `endStar` and `endTransition` AND **also** emits
each diagnostic via `Context.ErrorSink.OnError(diagnostic)`. Verify by
inspection: in `TokenizerBackedParser.cs` `ParseRazorComment`, locate
the `endStar = SyntaxFactory.MissingToken(SyntaxKind.RazorCommentStar, diagnostic)`
call and the immediately-following `Context.ErrorSink.OnError(diagnostic)`
call -- both should pass the same `diagnostic` instance. Same pattern
for `endTransition`. (Approximate location: search for
`Parsing_RazorCommentNotTerminated` in the file; both calls are
within ~5 lines of each other.)

If on inspection the double-emit pattern is NOT present (e.g., the
two diagnostics are actually different), downgrade the exit criterion
from "exactly one" to "diagnostic count under enhanced mode <= count
under legacy mode for the same input" -- the principle is "don't
regress diagnostic count", whatever the current value is.

Migrate it under the `UseEnhancedRecovery` flag:

- Old path: unchanged (legacy mode keeps double-emit because legacy
  consumers depend on it).
- New path: use `Required(RazorCommentStar, ...)`,
  `Required(RazorCommentTransition, ...)`. Crucially: **do NOT** call
  `Context.ErrorSink.OnError(...)` for the same diagnostic. The
  `Required` helper attaches it to the missing token; the diagnostic
  flows into the final `ErrorSink` through normal tree-building.
- Add an assertion in the new test that, under enhanced mode, the
  diagnostic count does not exceed the legacy baseline (see fallback
  rule above).
- Update the existing unterminated-comment tests under the new flag
  (snapshot the new shape under `Enhanced.json`). The relevant tests
  live in `legacyTest/Legacy/CSharpRazorCommentsTest.cs`; search for
  `RazorCommentNotTerminated` to find the specific `[Fact]` methods.

This stage proves the pattern end-to-end on a function whose existing
tests are simple and whose recovery semantics are obvious.

Exit criteria:

- `ParseRazorComment` has both legacy and enhanced paths gated by the
  flag.
- New test `ParseRazorComment_Unterminated_EnhancedRecovery` produces a
  tree with `MissingToken(RazorCommentStar)` and
  `MissingToken(RazorCommentTransition)` at exact positions, plus a
  snapshot of the new shape.
- Assertion: under enhanced mode, the diagnostic count for the same
  input does not exceed the legacy baseline.
- Legacy tests still pass; new snapshot is committed.

### Stage 1 exit criteria (gate to Phase 2)

- `Synchronize` and `Required` are callable, unit-tested, documented.
- New diagnostic factories exist for every recovery site.
- `ParseRazorComment` is the proof case; its enhanced-mode test is green.
- All existing tests still pass.

---

## Stage 2 -- Migrate CSharp parser

**Goal.** Convert every recovery site in `CSharpCodeParser` to the new
model, under the flag. After Stage 2, all 19 `AcceptUntil` /
`Balance`-style recovery sites in `CSharpCodeParser` either (a) use the
new `Required`/`Synchronize` helpers, or (b) are explicitly documented
in `plan-state.md` as intentionally unchanged.

**Important: Stage 2 exit criteria are PARSER-ONLY.** The "wall of red
goes away" end-to-end metric depends on Stage 5.1 (codegen safe
placeholders) and Stage 5.3 (source-mapping narrowing). Stage 2 cannot
measure cascading CS diagnostics because Stage 5 hasn't shipped yet.
Stage 2 exit criteria measure:

- The parse tree contains a `MissingToken` at the expected position.
- The diagnostic span on the missing token is <= 5 characters.
- The skipped tokens are wrapped in a `SkippedContentSyntax` (not a
  `CSharpStatementLiteral` or `CSharpExpressionLiteral`).
- No new `MarkupMiscAttributeContent` is produced for the recovered
  region.

The end-to-end "0 cascading CS diagnostics in the corpus" metric is
reasserted as a Stage 5 exit criterion (with Stages 2 and 3 listed as
prerequisites).

Stages 2.1-2.6 are ordered by importance and by independence. Each
sub-stage can be a separate PR. Each migrates one parser function
family, updates baselines for new-mode tests, and adds at least one
corpus snapshot expectation that becomes a "narrowed" parse-tree
shape.

### Stage 2.1 -- `ParseExplicitExpressionBody` (covers `@(...)`)

Current recovery (`CSharpCodeParser.cs` near line 442):

```csharp
var success = Balance(... BacktrackOnFailure | NoErrorOnFailure ...);
if (!success)
{
    AcceptUntil(SyntaxKind.LessThan);
    Context.ErrorSink.OnError(...);
}
```

After:

- Try `Balance(BacktrackOnFailure | NoErrorOnFailure ...)`.
- On failure: do NOT `AcceptUntil(LessThan)`. Instead:
    1. Emit `MissingToken(RightParenthesis)` **at the current cursor**
       (which after Balance failure is at the end of where the
       expression should have closed). Use the new `_At` factory
       variant with this current position. The diagnostic is a
       single-character-width span at the missing-token position.
       (Today's code already emits the missing token at the current
       cursor; the change is only that the *diagnostic span* shrinks
       from a 1-char span starting at `block.Start` to a 1-char span
       at the cursor.)
    2. Call the **convenience overload** of `Synchronize` (no
       `outerFollow`):
       `Synchronize(followSet, originatingLanguage: SyntaxKind.CSharpCodeBlock)`
       where `followSet` (C#-side kinds) =
       `(RightParenthesis, LessThan, Transition)`. Stage 4.2 will
       later upgrade these calls to the full overload threading the
       outer follow set; for Stage 2.1 the convenience form is
       sufficient (per the BDD #4 / Stage 4.1 split that establishes
       the cross-language plumbing).
    3. Insert the `SkippedContentSyntax` result into the expression
       builder so positions are preserved.

Update corpus expectation: `UnclosedExplicitExpression.razor` and
`EmptyExplicitExpression.razor` parse-tree snapshots show a
`MissingToken(RightParenthesis)` at the precise EOF position and a
`SkippedContentSyntax` for any garbage between the open paren and EOF
(if applicable). Diagnostic span on the missing token is <= 1 character.

Exit criteria:

- Both old and new behaviour validated by their respective tests.
- Corpus snapshot for `UnclosedExplicitExpression`,
  `EmptyExplicitExpression` updated under enhanced mode:
    - Tree contains `MissingToken(RightParenthesis)` at the construct's
      first un-matched position.
    - Tree contains `SkippedContentSyntax` (not a fat
      `CSharpExpressionLiteral`) for any absorbed garbage.
    - Diagnostic span on the missing token is <= 1 character.
- (Deferred to Stage 5 exit) Cascading CS diagnostic count for these
  cases will be 0.

### Stage 2.2 -- `ParseStatementBody` / `ParseCodeBlock` (covers `@{...}`)

Current recovery: missing `RightBrace` already uses
`SyntaxFactory.MissingToken(SyntaxKind.RightBrace)` (see line 748). The
bigger problem is `ParseStandardStatement` (Stage 2.3) and the
`Parsing_ExpectedEndOfBlockBeforeEOF` diagnostic whose span is currently
"the whole block".

Steps:

- Replace direct `SyntaxFactory.MissingToken(RightBrace)` callsite with
  `Required(RightBrace, ...)` so the diagnostic is precise.
- Pass an outer follow set into `ParseCodeBlock` (defaulting to the
  current implicit one); `ParseCodeBlock`'s while loop synchronizes on
  it after a failed statement parse instead of falling through to
  `ParseStandardStatement`'s panic.
- `Parsing_ExpectedEndOfBlockBeforeEOF` uses the `_At` factory with the
  precise position (the EOF, not the block start).

Exit criteria:

- Corpus `UnclosedCodeBlock` snapshot updated: diagnostic at EOF (or
  the last token), not the `@{` position.
- No widening of pre-existing tests; baselines reviewed.

### Stage 2.3 -- `ParseStandardStatement` (the biggest recovery site)

This function's `else` branch is the canonical "fat literal" producer:

```csharp
else
{
    _tokenizer.Reset(bookmark);
    NextToken();
    AcceptUntil(SyntaxKind.LessThan, SyntaxKind.LeftBrace, SyntaxKind.RightBrace);
    return;
}
```

After enhanced mode:

- The unknown-token branch emits a `Parsing_UnexpectedTokenInStatement`
  diagnostic (NEW -- allocate the next free RZ ID per Stage 1.3
  procedure; suggested name `RZ1066` or whatever is next free; record
  in `plan-state.md`) for `CurrentToken` at zero width.
- Use the **convenience overload** of `Synchronize` (no `outerFollow`
  -- Stage 4.2 will upgrade callers later):
  `Synchronize(followSet, originatingLanguage: SyntaxKind.CSharpCodeBlock)`
  where `followSet` (C#-side kinds) =
  `(Semicolon, RightBrace, Transition, LessThan)`.
- The skipped content is added to the builder as a `SkippedContentSyntax`
  node so positions are preserved but the bad tokens don't become part
  of `CSharpStatementLiteral.LiteralTokens` (so codegen won't dump them
  as C# text).

The `TryBalanceBlock` recovery (inner function) similarly converts
its `AcceptUntil` to `Synchronize`.

Exit criteria:

- Corpus `MidStatementGarbage`, `UnclosedIfParen` parse-tree snapshots:
  narrow diagnostic on the offending token, garbage absorbed as
  `SkippedContentSyntax`, no `CSharpStatementLiteral` wraps the
  recovered region.
- The number of `AcceptUntil(LessThan, ...)` calls in the
  enhanced-mode code paths of `CSharpCodeParser` is 0 (the legacy
  paths still have them). Count via
  `Select-String -Path D:\projects\roslyn4\src\Razor\src\Compiler\Microsoft.CodeAnalysis.Razor.Compiler\src\Language\Legacy\CSharpCodeParser.cs -Pattern "AcceptUntil\(SyntaxKind\.LessThan"`
  -- expected to remain at the legacy-baseline value (~4) until Stage
  6.2 deletes the legacy paths, but the enhanced-mode branches added
  in this stage must not contribute new occurrences.
- (Deferred to Stage 5 exit) Cascading CS errors for the corpus cases
  will be 0.

### Stage 2.4 -- Conditional / loop / try / using / do / switch frames

`ParseConditionalBlock`, `ParseIfStatement`, `ParseTryStatement`,
`ParseDoStatement`, `ParseUsingKeyword`, the `switch` branch of
`ParseStandardStatement`, `ParseFilterableCatchBlock`,
`ParseWhileClause`.

For each: condition-parsing uses `Required(LeftParenthesis, ...)` +
`Balance` + `Required(RightParenthesis, ...)`; body parsing uses
`Required(LeftBrace, ...)` and the new code-block path.

Exit criteria: each of the corpus files exercising these (add
`UnclosedForeach.razor`, `UnclosedSwitch.razor`, etc., during this
stage) shows narrow diagnostics under enhanced mode.

### Stage 2.5 -- Directive parsers (`@using`, `@addTagHelper`, `@inherits`, extensible)

`ParseTagHelperPrefixDirective`, `ParseAddTagHelperDirective`,
`ParseRemoveTagHelperDirective`, `ParseTagHelperDirective`,
`ParseUsingDeclaration`, `ParseExtensibleDirective` (this is the long
function around line 1522).

The diagnostics here today are already mostly position-precise; the
recovery (when arguments are missing/garbage) is the issue. Each
expected directive token becomes `Required(...)`; each trailing-garbage
case becomes (using the convenience overload per Stage 1.1, to be
upgraded by Stage 4.2):

```csharp
Synchronize(
    new FollowSet(SyntaxKind.NewLine, SyntaxKind.RightBrace, SyntaxKind.LessThan),
    originatingLanguage: SyntaxKind.CSharpCodeBlock)
```

or, equivalently, define a named `RecoveryFollowSets.CSharpDirectiveTrailing`
constant per Stage 4.1 conventions. Note the kinds are C#-side
(`LessThan`, not `OpenAngle`) per Big Design Decision #4.

Exit criteria: corpus directive cases (add `MalformedUsing.razor`,
`MalformedExtensible.razor`) show narrow diagnostics; the rest of the
file parses with **zero new parser diagnostics outside the directive
token** (verified via the parser-side metrics in the snapshot
harness).

### Stage 2.6 -- Implicit expressions (`ParseMethodCallOrArrayIndex` recovery)

Implicit expressions are subtle because their terminator is "the next
character that isn't part of the implicit expression". The recovery
sites actually live in `ParseMethodCallOrArrayIndex` (line ~547) which
uses `Balance` for parens/brackets and `AcceptUntil(LessThan)` on
failure (line ~572). Implicit expressions are also touched by the
"unknown after transition" handler in `ParseStandardStatement`'s
implicit-fallback path (around line 1120) and the panic in the
fallthrough else branch (line 1134).

After enhanced mode:

- The "Balance failed" branch in `ParseMethodCallOrArrayIndex` calls
  the **convenience overload** of `Synchronize` (Stage 4.2 upgrades
  later): `Synchronize(followSet, originatingLanguage: SyntaxKind.CSharpCodeBlock)`
  where `followSet` (C#-side kinds) =
  `(LessThan, NewLine, Whitespace)`.
- The closing bracket missing case is `Required(right, ...)` instead of
  the current `At(right) ? AcceptAndMoveNext() : nothing`.

Exit criteria: corpus `UnclosedMethodCallInImplicit.razor` (add) shows
narrow `MissingToken(RightParenthesis)`; subsequent markup parses
cleanly as a real `MarkupElement`.

### Stage 2 exit criteria (gate to Phase 3)

- All sub-stages individually complete.
- Every enhanced-mode code path added by Stage 2 uses `Required` /
  `Synchronize` rather than `AcceptUntil(LessThan, ...)`. Verify
  mechanically:
    - Each enhanced-mode branch in `CSharpCodeParser.cs` is bracketed
      by `if (Context.Options.UseEnhancedRecovery)` (or the equivalent
      branch keyword chosen during Stage 0.3). Find every enhanced
      branch via:
      `Select-String -Path "...\Legacy\CSharpCodeParser.cs" -Pattern "Context\.Options\.UseEnhancedRecovery"`.
    - For the line-range of each enhanced branch (from the `if` to
      its matching `else`/closing `}`), confirm zero occurrences of
      `AcceptUntil(SyntaxKind.LessThan` and zero of the other panic
      patterns. A small helper script that opens the file, scans
      enhanced-branch ranges, and counts `AcceptUntil(SyntaxKind\.LessThan`
      hits within those ranges is the cleanest check. Record the
      count in `plan-state.md` under "Stage 2 verification".
    - Legacy branches still contain the old patterns (this is
      expected until Stage 6.2 deletes them).
- Corpus parse-tree snapshots for C# cases show:
    - `MissingToken` at precise positions for missing required tokens.
    - `SkippedContentSyntax` (not fat literals) wrapping skipped
      garbage.
    - Diagnostic spans <= 5 characters on each new diagnostic.
- (Deferred to Stage 5 exit) End-to-end "zero cascading CS errors"
  metric.

---

## Stage 3 -- Migrate HTML parser

**Goal.** Convert recovery sites in `HtmlMarkupParser` similarly. HTML
recovery is more structured (tag stack) but has its own ad-hoc
synchronisation: `ParseMiscAttribute` and the unclosed-tag handling in
`CompleteEndTag` / `TryRecoverStartTag`.

### Stage 3.1 -- `ParseStartTag` / `ParseEndTag` precise missing tokens

Today `ParseStartTag` already uses `MissingToken(Text)` for the tag name
and `MissingToken(CloseAngle)` for the close angle, but the diagnostic
positions are the start-of-tag, not the missing-token site. Migrate
under the flag:

- `Required(Text, Parsing_TagNameExpected_At(...))` produces a
  zero-width missing token with the diagnostic at the cursor.
- `Required(CloseAngle, Parsing_UnfinishedTag_At(...))` similarly.

Exit criteria: `MalformedTagAttribute.razor` and a new
`UnnamedTag.razor` corpus case show narrow diagnostics.

### Stage 3.2 -- `ParseAttribute` / `ParseRemainingAttribute` for empty / missing values

Today, an attribute with `=` followed by nothing or by garbage falls
through to `ParseMiscAttribute`, which accumulates a fat
`MarkupMiscAttributeContent`.

After:

- After `Required(Equals, ...)`, if the value parse produces an empty /
  missing C# expression for a C#-bearing attribute (the
  `OtherParserBlock` callout in `ParseConditionalAttributeValue`), the
  synthesised value is shaped as
  **`GenericBlock([CSharpExpressionLiteral([MissingToken(Identifier)])])`**
  (or `MarkupTagHelperAttributeValueSyntax(...)` with the same inner
  shape if the attribute is later rewritten as a tag-helper attribute).
  See Big Design Decision #9 for why this shape: the `Value` field is
  typed `RazorBlockSyntax` (or `MarkupTagHelperAttributeValueSyntax`,
  which is a `RazorBlockSyntax`), so a bare `SyntaxToken` cannot be
  assigned there.
- Stage 5.1 codegen detects the exact shape "GenericBlock with single
  CSharpExpressionLiteral with single MissingToken" and emits the
  safe placeholder instead of empty C# text.

Exit criteria:

- Corpus `EmptyBoundAttribute_Onclick` parse-tree snapshot under
  enhanced mode: the `MarkupAttributeBlock.Value` contains exactly the
  shape above (one `MissingToken(Identifier)` inside one
  `CSharpExpressionLiteral` inside one `GenericBlock`).
- RZ2008 narrowed to the attribute-name span (already narrow today
  per the analysis; verify it does not widen).
- (Deferred to Stage 5 exit) Zero CS1525 in the generated C#.
- (Deferred to Stage 5 exit) Visible "wall of red" in the corpus is
  gone end-to-end.

### Stage 3.3 -- `TryRecoverStartTag` / `CompleteEndTag` precise diagnostics

The tag-stack recovery itself doesn't change structurally -- it's
already trying to do the right thing. What changes: the position of the
"MissingEndTag" and "UnexpectedEndTag" diagnostics becomes the precise
location of the missing/extra tag, not the start of the construct.

Exit criteria: corpus `UnclosedTag.razor` shows diagnostics at the
exact tag positions; element nesting in the resulting tree matches the
user-visible structure (no whole-file `MarkupMiscAttributeContent`).

### Stage 3.4 -- `ParseMiscAttribute` -> recovery rather than absorb-all

`ParseMiscAttribute` is the HTML parser's `AcceptUntil(LessThan)`. It's
called when an attribute name is malformed or when the cursor isn't at
a recognisable attribute. Convert: instead of looping until `<`, `>`,
`/`, or a quote, use (the convenience overload per Stage 1.1; Stage
4.2 will upgrade to the full overload):

```csharp
Synchronize(
    RecoveryFollowSets.HtmlEndOfTagFollowSet,
    originatingLanguage: SyntaxKind.MarkupBlock)
```

where `HtmlEndOfTagFollowSet` (defined in `RecoveryFollowSets.cs`,
HTML-side kinds: `OpenAngle, CloseAngle, ForwardSlash, DoubleQuote,
SingleQuote`) captures "whatever the surrounding tag parser cares
about". The skipped range becomes a `SkippedContentSyntax`; an
unrecognised-attribute diagnostic is emitted at the cursor.

**Note on `originatingLanguage`**: HTML-side `Synchronize` calls
consistently use `originatingLanguage: SyntaxKind.MarkupBlock`,
parallel to the C#-side convention of `SyntaxKind.CSharpCodeBlock`.
Stage 3 follows this throughout.

Exit criteria: `BareAtFollowedByGarbage.razor` corpus shows the garbage
absorbed as `SkippedContentSyntax`, with the `<p>real markup</p>`
parsed cleanly as a normal `MarkupElement`.

### Stage 3 exit criteria (gate to Phase 4)

- All 3.x sub-stages individually complete.
- Corpus HTML cases show narrow diagnostics and well-formed surrounding
  tree.

---

## Stage 4 -- Cross-parser handoff

**Goal.** Make `Synchronize` aware of the outer language. When CSharp
recovery hits garbage that looks like HTML (`<`), it should hand back
to HTML rather than absorb it.

This stage is the one most likely to require iteration -- the two
parsers don't currently share a recovery vocabulary, so this is genuinely
new design.

### Stage 4.1 -- Define the cross-language follow-set protocol

A parser entered via `OtherParserBlock` / `ParseBlock` receives the
caller's `FollowSet` (the set of tokens that, if seen, mean "give up
back to me"). It threads this through its recovery: `Synchronize` is
called with `(myOwnFollowSet | outerFollowSet)`.

**Two distinct `OtherParserBlock` implementations exist.** Both need
the same overload-pair treatment:

- `CSharpCodeParser.OtherParserBlock` (verified at
  `CSharpCodeParser.cs:2886`).
- `HtmlMarkupParser.OtherParserBlock` (verified at
  `HtmlMarkupParser.cs:2219`). This is a separate private method on
  the HTML parser, **not** the same as the CSharp one.

**Add new overloads** rather than mutating the existing public signatures
(the public `ParseBlock()` methods have other callers that should
continue to work):

```csharp
// In HtmlMarkupParser (existing: public MarkupBlockSyntax? ParseBlock()):
public MarkupBlockSyntax? ParseBlock()
    => ParseBlock(FollowSet.Empty);
public MarkupBlockSyntax? ParseBlock(FollowSet outerFollow);

// In CSharpCodeParser (existing: public CSharpCodeBlockSyntax? ParseBlock()):
public CSharpCodeBlockSyntax? ParseBlock()
    => ParseBlock(FollowSet.Empty);
public CSharpCodeBlockSyntax? ParseBlock(FollowSet outerFollow);

// In CSharpCodeParser (existing: private OtherParserBlock(builder)):
private void OtherParserBlock(in SyntaxListBuilder<RazorSyntaxNode> builder)
    => OtherParserBlock(builder, FollowSet.Empty);
private void OtherParserBlock(in SyntaxListBuilder<RazorSyntaxNode> builder, FollowSet outerFollow);

// In HtmlMarkupParser (existing: private OtherParserBlock(builder)):
private void OtherParserBlock(in SyntaxListBuilder<RazorSyntaxNode> builder)
    => OtherParserBlock(builder, FollowSet.Empty);
private void OtherParserBlock(in SyntaxListBuilder<RazorSyntaxNode> builder, FollowSet outerFollow);
```

This pattern (parameterless wrapper -> parameterised method) preserves
existing callers and lets new callers thread the outer follow set.

**`FollowSet.Empty`** must be defined in Stage 1.1 along with the
`FollowSet` struct itself: `public static readonly FollowSet Empty = default;`.

**Default outer follow sets.** The outermost entry point is
`HtmlMarkupParser.ParseDocument()` (near line 65 -- re-verify if it
drifted). It calls `ParseMarkupNodes` directly (no follow-set yet).
Recovery there uses the implicit "end of file" follow set; threading a
`FollowSet.Empty` through is harmless and matches the convention.

**Per-call-site mapping (Stage 4.2 will update these).** Verified by
`Select-String -Path "...\Legacy\*Parser*.cs" -Pattern "OtherParserBlock\("`
(re-run before editing to refresh line numbers). **Important: each
follow set is in the language of the CALLEE** (the parser being
entered) -- per Big Design Decision #4, kinds are language-scoped.
When the caller's "natural" follow set comes from a different
language, translate via `FollowSet.ForCSharpCallee(...)` or
`FollowSet.ForHtmlCallee(...)` at the call site.

| Caller | Callee | Outer follow set to thread (in callee's language) |
|--------|--------|---------------------------|
| `CSharpCodeParser.OtherParserBlock` callers (lines 857 / 1164 in CSharpCodeParser) | `HtmlParser.ParseBlock` | The C# parser's local recovery set, *translated to HTML kinds* (close brace stays as `RightBrace` -- shared kind; `LessThan` becomes `OpenAngle` for the HTML callee) |
| `HtmlMarkupParser.OtherParserBlock` from `ParseAttribute` (HtmlMarkupParser.cs ~1125) | `CodeParser.ParseBlock` | HTML's attribute-terminator set `(OpenAngle, ForwardSlash, DoubleQuote, SingleQuote)`, *translated to C# kinds*: `(LessThan, Slash)` (the quote kinds drop because C# tokenizer eats `"`/`'` as part of string/char literals) |
| `HtmlMarkupParser.OtherParserBlock` from `ParseConditionalAttributeValue` (HtmlMarkupParser.cs ~1419) | `CodeParser.ParseBlock` | If `quote != SyntaxKind.Marker` (quoted value): the closing-quote kind -- but since C# absorbs `"`/`'` as literals, the closing-quote follow set degenerates; the most useful sync token is `NewLine` (force end-of-line recovery). If `quote == SyntaxKind.Marker` (unquoted): use the full unquoted-attribute-terminator set translated to C# kinds: `(LessThan, Slash, GreaterThan, Equals, Whitespace, NewLine)`. See `IsUnquotedEndOfAttributeValue` at `HtmlMarkupParser.cs:1462` for the authoritative HTML-side list. |
| `HtmlMarkupParser.OtherParserBlock` from `ParseCodeTransition` (HtmlMarkupParser.cs ~1642) | `CodeParser.ParseBlock` | The current HTML follow set inherited from the enclosing `ParseMarkupNodes`, translated to C# kinds |
| `CSharpCodeParser.ParseNestedBlock` (line ~971) -> `CSharpCodeParser.ParseBlock` | `CSharpCodeParser.ParseBlock` | Parent's follow set (same language; no translation needed) |
| `CSharpCodeParser` extensible-directive handler (line ~1858) -> `HtmlParser.ParseRazorBlock` | `HtmlParser.ParseRazorBlock` | The block's nesting sequences (already passed; no translation needed -- these are matched as text by `ParseRazorBlock`) |

**The `FollowSet` values listed above are named constants you must
define** in a static helper class (suggest `RecoveryFollowSets.cs` next
to `TokenizerBackedParser`). E.g.:

```csharp
public static class RecoveryFollowSets
{
    // HTML-side sets (kinds in HtmlTokenizer's vocabulary):
    public static readonly FollowSet HtmlAttributeTerminators =
        new(SyntaxKind.OpenAngle, SyntaxKind.ForwardSlash,
            SyntaxKind.DoubleQuote, SyntaxKind.SingleQuote);
    public static readonly FollowSet HtmlUnquotedAttributeValueTerminators =
        new(SyntaxKind.OpenAngle, SyntaxKind.ForwardSlash, SyntaxKind.CloseAngle,
            SyntaxKind.Equals, SyntaxKind.Whitespace, SyntaxKind.NewLine);

    // C#-side sets (kinds in CSharpTokenizer's vocabulary):
    public static readonly FollowSet CSharpStatementTerminators =
        new(SyntaxKind.Semicolon, SyntaxKind.RightBrace);
    public static readonly FollowSet CSharpExpressionTerminators =
        new(SyntaxKind.RightParenthesis, SyntaxKind.LessThan, SyntaxKind.Transition);

    // Cross-language translations (called at boundary):
    public static FollowSet ForCSharpCallee(FollowSet htmlSet) { /* map per BDD #4 */ }
    public static FollowSet ForHtmlCallee(FollowSet csharpSet) { /* map per BDD #4 */ }
}
```

Define every named set used in the table above before wiring up
Stage 4.2.

Exit criteria:

- Both `OtherParserBlock` implementations have the overload-pair
  treatment; legacy callers compile via the parameterless wrappers.
- `FollowSet.Empty` exists.
- The named follow sets in `RecoveryFollowSets.cs` exist and are
  referenced by Stage 4.2.
- Compiles. No behaviour change yet (the new outer follow set isn't
  consumed by any recovery code until Stage 4.2).

### Stage 4.2 -- Wire outer follow sets through recovery

**This is the upgrade pass.** Stages 2 and 3 deliberately used the
**convenience overload** of `Synchronize` (no `outerFollow`
parameter) so they could land independently. Stage 4.2 walks every
enhanced-mode `Synchronize` call added by Stages 2/3 and upgrades it
to the **full overload** that threads the caller's outer follow set.

The mechanical pattern at each call site:

1. The function must already receive a `FollowSet outerFollow`
   parameter (added by Stage 4.1's overload-pair work for parser
   entrypoints; new parameter on inner helpers needs to be added here
   and threaded by every caller).
2. Replace `Synchronize(localFollow, originatingLanguage: X)` with
   `Synchronize(localFollow, outerFollow, originatingLanguage: X)`.
3. After the call: if
   `result.StopReason == SyncStopReason.AtOuterFollowToken`, return
   without further parsing of the inner construct (the caller will
   pick up at the outer-follow token).

**Caller-chain enumeration.** Every enhanced-mode recovery site added
by Stages 2/3 needs the `outerFollow` parameter on its enclosing
function chain. For each function migrated in Stages 2-3, walk up
the call stack until you reach a public entry point (one of the
`ParseBlock` overloads from Stage 4.1) and add `FollowSet outerFollow`
to every intermediate function's signature. Use a placeholder
`FollowSet.Empty` at any non-recovery internal call site
(e.g., recursive calls that don't change the outer context).

The actual call sites to update are exactly:

- Every function listed in Stage 2.1 through Stage 2.6 that calls
  `Synchronize`.
- Every function listed in Stage 3.1 through Stage 3.4 that calls
  `Synchronize`.
- Plus the cross-parser handoff per Stage 4.1's per-call-site mapping
  table: each `OtherParserBlock` / `ParseBlock` call site translates
  its outer follow set into the callee's language via
  `RecoveryFollowSets.ForCSharpCallee(...)` /
  `ForHtmlCallee(...)`.

Exit criteria:

- Every enhanced-mode `Synchronize` call now uses the full overload.
- Tests covering "HTML around malformed C#" (corpus already includes
  several) show the C# parser stopping when it sees an outer-HTML
  token rather than absorbing it. The HTML parser then continues
  parsing the visible HTML correctly.
- Add a new corpus file `MalformedCSharpWithSurroundingMarkup.razor`:
  `<div>@if(foo bar baz<p>still html</p></div>` -- the `<p>` should
  be parsed as a real HTML element, not as part of the malformed if.

### Stage 4.3 -- Special case: implicit expressions hitting markup

`ParseImplicitExpression` (Stage 2.6) is special because its natural
terminator is "anything not in the implicit grammar". The outer follow
set is broader (any HTML or whitespace). Validate that the Stage 4.2
wiring works here too; if not, add specific handling.

Exit criteria: `@foo.<p>` parses as "@foo." (implicit expression
ending) + `<p>` (markup), with a narrow diagnostic on the `.`.

### Stage 4.4 -- Tokenizer state hooks across cross-language sync

**The hook situation is asymmetric**, verified:

- `CSharpCodeParser.OtherParserBlock` (line ~2886) calls `EndingBlock()`
  before handing off and `StartingBlock()` after returning.
- `CSharpCodeParser.ParseBlock` (line ~279) also calls `StartingBlock()`
  at entry.
- `HtmlMarkupParser.OtherParserBlock` (line 2219) calls neither hook.
- `HtmlTokenizer.StartingBlock`/`EndingBlock` are no-ops (the base
  `Tokenizer` implementations are virtual no-ops; `HtmlTokenizer`
  doesn't override them).
- `NativeCSharpTokenizer.StartingBlock`/`EndingBlock` are also no-ops.
- **Only `RoslynCSharpTokenizer` overrides them** (lines 61-103) to
  reconcile its wrapped Roslyn `SyntaxTokenParser` state.

Consequence: the entire hook story only matters when the C# parser is
using `RoslynCSharpTokenizer`. Inside that mode, the C# parser already
calls hooks correctly on the C#-leaves-to-HTML boundary; it does not
need to call hooks on the HTML-leaves-to-C# boundary because the C#
parser's `ParseBlock` calls `StartingBlock()` at entry.

What does need to change for cross-language sync: when **C# parser**
calls `Synchronize` and the result's `StopReason` is
`AtOuterFollowToken`, the C# parser must call `EndingBlock()` on its
own tokenizer before returning the skipped content to its caller, so
the cursor position the caller resumes from is properly aligned for
the HTML tokenizer. The Stage 1.1 signature now returns
`SyncStopReason`, so the C# parser can branch on it.

The HTML side does nothing -- its tokenizer is a no-op for these
hooks.

**Concrete steps:**

1. In every enhanced-mode C# parser function that calls `Synchronize`
   with a non-empty `outerFollow`: after the call, if
   `result.StopReason == SyncStopReason.AtOuterFollowToken`, call
   `EndingBlock()` (the internal wrapper on `TokenizerBackedParser`,
   defined at `TokenizerBackedParser.cs:725-733`) before returning.
   This forwards to the active tokenizer's `EndingBlock()` override
   the same way the existing `OtherParserBlock` path does. (Or wrap
   the conditional in a helper `EndingBlockIfStoppedOnOuter(SyncResult)`.)
2. No change needed on the HTML side.
3. Verify with `RoslynCSharpTokenizer`: the Roslyn token parser's
   state stays aligned across the boundary.

Exit criteria:

- Unit tests in `RoslynCSharpTokenizerRecoveryTests` cover: C# parser
  synchronizes across an HTML transition with the Roslyn tokenizer
  active -- after the sync, parsing resumes in HTML and the Roslyn
  tokenizer state is properly torn down (verify by re-entering C# on a
  subsequent transition and confirming the Roslyn parser is at the
  expected position).
- The existing `RoslynCSharpTokenizer.StartingBlock`/`EndingBlock` tests
  still pass.
- The synchronization-result `SyncStopReason` is consumed correctly by
  every enhanced-mode C# parser function that calls `Synchronize` with
  an outer follow set.

### Stage 4 exit criteria (gate to Phase 5)

- The cross-language handoff respects the outer follow set in all
  enhanced-mode paths.
- Tokenizer state hooks (`EndingBlock`/`StartingBlock`) fire correctly
  when `Synchronize` crosses a language boundary.
- The "malformed inner, well-formed outer" corpus cases all parse with
  the outer correctly recovered.

---

## Stage 5 -- Downstream consumers

**Dependencies (read carefully -- not all sub-stages share the same gate):**

- **Stage 5.0.0** (the codegen-site spike) is independent of Stages
  2/3/4 and can run any time after Stage 0 is complete. It uses the
  **legacy** generated output to identify the codegen site producing
  CS1525 today. Running it early unblocks Stages 5.0 and 5.1's
  encoding decisions and is the right way to use available parallelism.
- **Stages 5.0 through 5.6** depend on Stages 2, 3, AND 4 being fully
  complete AND on the Stage 5.0.0 spike report being recorded in
  `plan-state.md`. Stage 5.1 specifically depends on Stage 3.2 (the
  missing-attribute-value tree shape); Stage 5.0 depends on Stage 3.2
  for the same reason.

**Goal.** Update consumers that walk the tree to handle the new
`SkippedContentSyntax` and the missing-token invariant correctly.
This is the largest behavioural surface of the change.

### Stage 5.0.0 -- Codegen-site spike

Before any code changes, run this 30-minute investigation to find the
exact codegen site that produces the CS1525 today and the placeholder
format that fixes it. Without the spike, Stages 5.0 and 5.1 can't
write `IsMissingValueMarker` / `SubstituteMissingValuePlaceholder` (or
their equivalents) because the encoding depends on what the existing
codegen path emits.

**Procedure:**

1. Pick a host project to run the source generator against. The
   cheapest option is to **extend the existing test harness in
   `src/Razor/src/Compiler/test/Microsoft.NET.Sdk.Razor.SourceGenerators.UnitTests/RazorSourceGeneratorComponentTests.cs`**.
   Add a test method `Spike_EmptyOnclickAttribute_DumpsGeneratedSource`
   that:
   - Calls `await GetDriverWithAdditionalText` (or whatever helper is
     used by existing tests in that file -- read the file first)
     with a single component:
     `<button @onclick="">Click</button>`.
   - Calls `RunGenerators` (or the harness's existing equivalent).
   - Writes the generated `.razor.g.cs` to a known repo-local path so
     it's reachable across sessions: e.g.,
     `D:\projects\roslyn4\artifacts\razor-recovery-spike\empty-onclick.razor.g.cs`
     (create the directory if missing). Avoid `Path.GetTempPath()` /
     `%TEMP%` -- those vary per session and lose the artifact between
     agent runs.
   - Asserts true so the test always passes (this is for inspection,
     not validation).
2. Run the test:
   `dotnet test src\Razor\src\Compiler\test\Microsoft.NET.Sdk.Razor.SourceGenerators.UnitTests\Microsoft.NET.Sdk.Razor.SourceGenerators.UnitTests.csproj --filter "FullyQualifiedName~Spike_EmptyOnclickAttribute"`
3. Open the dumped `.cs` file. Locate the malformed expression that
   produces CS1525. The most likely shapes:
   - `EventCallback.Factory.Create<MouseEventArgs>(this, )` (empty
     trailing arg).
   - `__builder.AddAttribute(N, "onclick", )`.
   - Something else.
4. Use call-stack analysis (`Select-String` for the string-literal
   prefix that produced that line in
   `src\Razor\src\Compiler\Microsoft.CodeAnalysis.Razor.Compiler\src`)
   to find the writer responsible. Record in `plan-state.md` under
   "Stage 5.0.0 spike report":
   - The exact malformed C# expression.
   - The writer file + line that emitted it (the `WriteCSharpExpression`
     or `WriteCSharpToken` call).
   - The IR node type whose contents produced the empty/malformed
     output (e.g., `ComponentAttributeIntermediateNode`,
     `TagHelperPropertyIntermediateNode`).
   - The shape of the placeholder needed (from the placeholder
     matrix in Stage 5.1).
5. The spike test is committed (it's useful for future regression
   verification) but tagged with a comment explaining it's
   investigation-only and only asserts trivially.

Exit criteria:

- Spike report exists in `plan-state.md` with the four bullets above
  filled in.
- The spike test exists and runs green.
- Subsequent Stage 5.0 and 5.1 work references the spike report's
  conclusions.

### Stage 5.0 -- IR lowering phase

`DefaultRazorIntermediateNodeLoweringPhase` (in
`src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/DefaultRazorIntermediateNodeLoweringPhase.cs`)
sits between parse and codegen: it walks the syntax tree and produces
the IR (`IntermediateNode` hierarchy) that codegen consumes. With the
new tree shapes, it must:

- Treat `SkippedContentSyntax` as a no-op (its tokens have no IR
  representation -- skipped content is purely a syntax-tree concept
  for source-mapping and editor purposes).
- Recognise the "missing C# attribute value" shape
  (`GenericBlock([CSharpExpressionLiteral([MissingToken(Identifier)])])`)
  and propagate a "value missing" signal to the IR's
  `UnresolvedAttributeIntermediateNode` /
  `TagHelperPropertyIntermediateNode` /
  `ComponentAttributeIntermediateNode` (the component pipeline uses
  this last one; verified in `Intermediate/ComponentAttributeIntermediateNode.cs`).
- Treat `MissingToken` of any kind that survives into IR token lists
  as empty content (zero-length token list); codegen handles the
  placeholder emission.

**Both legacy and component pipelines need the signal.** The motivating
bug (`@onclick=""`) flows through the **component pipeline**, not the
legacy tag-helper pipeline. Specifically:

1. `DefaultTagHelperResolutionPhase.LegacyTagHelperResolver.LowerBoundLegacyAttributeValue`
   handles the legacy path: it already detects empty values and emits
   a synthetic empty `CSharpIntermediateToken("")` plus RZ2008. With
   the new tree shape, it should instead emit a tagged "missing-value"
   marker that codegen recognises (see Stage 5.1 placeholder matrix).
2. The component pipeline's equivalent path lives in
   `Components/ComponentLoweringPass.cs` (or whichever component
   lowering pass handles `MarkupTagHelperDirectiveAttributeSyntax`).
   Locate the exact file via:
   `Select-String -Path "src\Razor\src\Compiler\Microsoft.CodeAnalysis.Razor.Compiler\src\Language\Components\*.cs" -Pattern "ComponentAttributeIntermediateNode|DirectiveAttribute"`
   and update accordingly.

**The `ComponentEventHandlerLoweringPass.RewriteUsage` early bail-out
(lines 164-169) must be updated.** Today it returns the node unchanged
when `original.Length == 0`, with the comment "the parser will already
have flagged this as an error, so ignore it". With the enhanced
parser, "missing value" is no longer empty in IR -- it's a tagged
missing-value signal. Update the bail-out to:

```csharp
if (original.Length == 0 || IsMissingValueMarker(original))
{
    // Emit a safe placeholder so downstream Roslyn doesn't see invalid C#.
    // The placeholder choice depends on the bound type (see Stage 5.1).
    return SubstituteMissingValuePlaceholder(node);
}
```

`IsMissingValueMarker` and `SubstituteMissingValuePlaceholder` are new
helpers; their exact shape depends on how the signal is encoded
(decide during the Stage 5.0.0 spike).

Audit:

- `DefaultRazorIntermediateNodeLoweringPhase.cs` -- the main visitor.
- `DefaultRazorIntermediateNodeLoweringPhase.cs` attribute-merging
  helpers around line 682-889 (see `razor-parser-analysis.md` for
  context).
- `Components/*Component*LoweringPass*.cs` -- component-specific
  lowering passes; specifically `ComponentEventHandlerLoweringPass.cs`
  needs the bail-out update above.
- `DefaultTagHelperResolutionPhase.LegacyTagHelperResolver.cs`
  `LowerBoundLegacyAttributeValue` -- already emits the empty-value
  signal; verify it sees the new shape correctly and converts to the
  new tagged form.
- **`DefaultTagHelperResolutionPhase.ComponentTagHelperResolver.cs`**
  (the component pipeline counterpart -- search for the file with
  `Get-ChildItem -Recurse -Filter ComponentTagHelperResolver.cs`).
  This is where the empty-value handling for component attributes
  lives; the Stage 5.0.0 spike will confirm whether the missing-value
  marker needs to flow through here too.

Exit criteria:

- The lowering tests in
  `test/DefaultRazorIntermediateNodeLoweringPhaseIntegrationTest.cs` and
  `test/DefaultRazorIntermediateNodeLoweringPhaseTest.cs`
  (located in the `test/` project, **not** `legacyTest/` -- the
  lowering test project is the `Microsoft.AspNetCore.Razor.Language.UnitTests`
  csproj) pass with the new shapes.
- New IR-shape tests cover: `SkippedContent` is dropped; missing
  attribute value flows through to a tagged "missing-value" marker
  on the appropriate `IntermediateNode` (legacy:
  `TagHelperPropertyIntermediateNode`; component:
  `ComponentAttributeIntermediateNode`).
- `ComponentEventHandlerLoweringPass.RewriteUsage` no longer
  silently bails out on missing values; it substitutes the placeholder.

### Stage 5.1 -- Codegen safe placeholders for missing C#

The point that fixes "wall of red" most directly.

The relevant codegen classes are in
`src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/CodeGeneration/`
and `Language/Components/`:

- `IntermediateNodeWriter.cs` -- the abstract base writer.
- `LiteralRuntimeNodeWriter.cs` -- writes literal text output (the
  `@expr` markup-output path).
- `TagHelperHtmlAttributeRuntimeNodeWriter.cs` -- writes HTML attribute
  values.
- `Components/ComponentNodeWriter.cs` -- the component pipeline writer
  (covers the issue #10383 case).

For each codegen path that emits a C# expression from a parsed Razor
node, check whether the IR signal is "missing value" (see Stage 5.0
for how the signal flows). If so, emit a safe placeholder.

**Component pipeline note (for issue #10383).** Verified: the
empty-`@onclick=""` case currently flows through
`ComponentEventHandlerLoweringPass.RewriteUsage`
(`Components/ComponentEventHandlerLoweringPass.cs:161`), which has an
**early bail-out at line 164-169** when `GetAttributeContent` returns
an empty array:

```csharp
if (original.Length == 0)
{
    // This can happen in error cases, the parser will already have flagged this
    // as an error, so ignore it.
    return node;
}
```

When the early bail-out fires, the `Create<T>(this, ...)` synthesis
is **skipped entirely**. The CS1525 the user sees in the original bug
therefore must originate from a different codegen site -- the most
likely candidate is the `LowerBoundLegacyAttributeValue` synthesis at
`DefaultTagHelperResolutionPhase.LegacyTagHelperResolver.cs` (which
inserts a synthetic empty `CSharpIntermediateToken("")` for the
missing value) followed by `ComponentNodeWriter` emitting that empty
token literally into the generated text.

**Spike before implementing.** The very first step of Stage 5.1 is to
ensure the **Stage 5.0.0 spike report** is in `plan-state.md`. If not,
go back and complete Stage 5.0.0 (the spike) -- Stage 5.1's placeholder
substitution depends on knowing which writer fires for the corpus
case. Stage 5.0.0's procedure is also documented inline above; the
fix here uses the spike's findings.

**Placeholder matrix.** Once the substitution point is known, the
exact placeholder depends on the surrounding generated C# context.
For each entry, the bound type is available from the tag-helper
binding (`BoundAttribute.TypeName` on `TagHelperPropertyIntermediateNode`
or the equivalent on `ComponentAttributeIntermediateNode`); thread it
through IR (Stage 5.0) so codegen can use it.

| Generated context | Bound type signal | Emit |
|-------------------|-------------------|------|
| `EventCallback<T>` bound attribute (e.g. `@onclick`), inner expression slot in `Create<T>(this, ...)` | T known from binding | `default(global::System.Action<T>)` (binds to `Create<TValue>(object, Action<TValue>)`) |
| `EventCallback` (untyped) bound attribute, inner slot | known | `default(global::System.Action)` |
| Other bound attribute, type known | known | `default(<fullyQualifiedType>)` |
| Bound attribute, type unknown / generic | unknown | `default!` |
| `@expr` in markup output context | n/a | `""` (empty string -- output is text) |
| C# expression in statement context (e.g. inside `@{}`) | n/a | `_ = (object?)null` (compiles cleanly; statement context provides the `;`) |
| Argument position in a generated call | known | `default(<argType>)` (use the binding's signature) |

Each placeholder choice has a corresponding e2e test
(see exit criteria) that asserts the generated C# parses cleanly under
Roslyn.

**Source mappings for placeholders.** For missing-content placeholders,
the mapping is zero-width at the missing-token position (so any Roslyn
diagnostic that lands here maps back to a single character in the
.razor file).

**E2E harness.** Extend the existing
`src/Razor/src/Compiler/test/Microsoft.NET.Sdk.Razor.SourceGenerators.UnitTests/RazorSourceGeneratorComponentTests.cs`
(or its sibling `RazorSourceGeneratorTests.cs`). These already exercise
the source generator end-to-end inside a host compilation and inspect
Roslyn diagnostics. Add a new corpus-driven test class
`ParserRecoveryCorpus_CodegenSafetyTests` in the same project that:

1. Loads each `.razor` file from
   `src/Razor/src/Compiler/Microsoft.AspNetCore.Razor.Language/legacyTest/ParserRecoveryCorpus/`.
2. Runs the source generator against a minimal Blazor project shape
   (re-use the harness's `Compilation` setup from existing tests).
3. Counts C# diagnostics produced by Roslyn on the generated text.
4. Asserts the count is 0 for the corpus cases listed below.

For tests under enhanced mode, set `UseEnhancedRecovery = true` via
the generator's `RazorParserOptions` pipeline.

Exit criteria:

- The Stage 5.0.0 spike report exists in `plan-state.md` and identifies
  the codegen site producing CS1525 today.
- The source-mapping-width measurement helper is authored **here** (in
  the Stage 5.1 e2e harness, where `RazorCSharpDocument` is available):

  ```csharp
  // In Microsoft.NET.Sdk.Razor.SourceGenerators.UnitTests
  internal static (int max, int total, int count) MeasureMappingWidths(
      RazorCSharpDocument csharpDoc)
  {
      var widths = csharpDoc.SourceMappings.Select(m => m.OriginalSpan.Length);
      return (widths.DefaultIfEmpty(0).Max(),
              widths.Sum(),
              csharpDoc.SourceMappings.Length);
  }
  ```

  Stage 5.3 consumes this helper for its source-mapping audit.
  (Earlier draft text suggested authoring the helper in Stage 0.2 --
  ignore that; the helper requires `RazorCSharpDocument` which is a
  codegen output type not reachable from the `legacyTest/` project
  hosting the Stage 0.2 snapshot harness.)
- The corpus `EmptyBoundAttribute_Onclick` produces:
    - Razor diagnostic: 1 narrow RZ2008.
    - Generated C# parses cleanly under Roslyn (no CS1525 etc.).
    - Source mappings around the attribute are <= 5 chars wide.
- The placeholder matrix is implemented for at least the 4 most
  common contexts (EventCallback<T>, EventCallback, generic bound,
  expression output).
- Every entry in the matrix has a compile-test that asserts the
  generated C# parses cleanly when triggered.
- The corpus e2e test asserts 0 cascading CS diagnostics for the
  wall-of-red cases listed in Stage 0.1.

### Stage 5.2 -- Tag-helper rewriter

`TagHelperParseTreeRewriter` and `TagHelperBlockRewriter` walk
`MarkupStartTagSyntax.Attributes`. With the enhanced parser, these can
now contain `MarkupAttributeBlock` nodes whose `Value` is the
"missing C# expression" shape (Big Design Decision #9), plus
`SkippedContentSyntax` siblings.

Audit:

- `MarkupTagHelperAttributeSyntax.Value` (typed
  `MarkupTagHelperAttributeValueSyntax`, itself a `RazorBlockSyntax`):
  if the inner shape is `CSharpExpressionLiteral([MissingToken])`,
  pass that through unchanged; downstream IR (Stage 5.0) handles it.
- `MarkupTagHelperDirectiveAttributeSyntax.Value` (same type, same
  flow): this is the form that the motivating bug `@onclick=""`
  actually exercises -- verify it works here specifically.
- `MarkupMinimizedTagHelperAttributeSyntax` and
  `MarkupMinimizedTagHelperDirectiveAttributeSyntax` semantics for
  missing values: unchanged (these are already treated as "no value
  at all", distinct from "empty value").
- `SkippedContentSyntax` between attributes: ignored at the rewriter
  level (it's a no-op in tag-helper semantics).

Exit criteria: rewriter tests pass. New tag-helper test cases for
missing/skipped attribute content produce the expected
`MarkupTagHelperAttributeSyntax` and
`MarkupTagHelperDirectiveAttributeSyntax` shapes.

### Stage 5.3 -- Source-mapping precision

In `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/CodeGeneration/CodeRenderingContext.cs`:

Audit where `SourceMapping` ranges are created. For each, ensure:

- A range that crosses a `MissingToken` or `SkippedContentSyntax` is
  _split_ at that boundary, producing a hole (no mapping for the
  skipped range) and tight mappings on either side.
- The current "one mapping per literal" behaviour is preserved for
  legacy-mode parses (so existing tests still match baselines).

**Use the measurement helper authored in Stage 5.1.** The
`MeasureMappingWidths(RazorCSharpDocument)` helper added in Stage 5.1's
e2e harness is the right tool for this audit. Snapshot the
triple `(max, total, count)` for each corpus case into Stage 5.1's
codegen snapshot file (`<CorpusFileName>.Codegen.json`, sibling of
the Stage 0.2 parser snapshots).

Exit criteria:

- For each corpus case under enhanced mode: the widest single
  `OriginalSpan` in the mapping list is no longer than 50 characters
  (target). Concrete failure to investigate if any case exceeds 200.
  Record the actual max widths in the snapshot via the Stage 5.1
  `MeasureMappingWidths` helper.

### Stage 5.4 -- `SyntaxNavigator` / `FindToken`

`SyntaxNavigator.FindToken` (or equivalent in
`src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Syntax/SyntaxNavigator.cs`)
needs to handle missing tokens: a position search must not return a
missing token (zero-width); it should fall through to the next real
token. Verify the behaviour matches Roslyn's.

Exit criteria: `SyntaxNavigatorTests` cover position-on-missing-token
returning the adjacent real token, not the missing one.

### Stage 5.5 -- Formatter

In `src/Razor/src/Razor/src/Microsoft.CodeAnalysis.Razor.Workspaces/Formatting/`:

Format walkers iterate tokens to compute indentation, whitespace
boundaries, and edits. `MissingToken` (zero width) must not influence
formatting; `SkippedContentSyntax` should be preserved as-is (do not
re-indent skipped content -- the user will fix it).

Exit criteria: formatting tests pass; add corpus formatting tests
covering "format a file with errors" that previously misformatted
because of fat literals.

### Stage 5.6 -- LSP / classification / completion / hover

`src/Razor/src/Razor/src/Microsoft.CodeAnalysis.Razor.Workspaces/`
and `src/Razor/src/Razor/src/Microsoft.VisualStudio.LanguageServices.Razor/`:

**Stage 5.6.0 -- locate the anchor classes.** Before changing behaviour,
identify (and record in `plan-state.md`) the active provider classes:

```powershell
# Classification
Select-String -Path "src\Razor\src\Razor\src\**\*.cs" -Pattern "class.*Classifi|ISemanticTokens|SemanticTokensRange"
# Completion
Select-String -Path "src\Razor\src\Razor\src\**\*.cs" -Pattern "class.*Completion|ICompletionService|CompletionEndpoint"
# Hover
Select-String -Path "src\Razor\src\Razor\src\**\*.cs" -Pattern "class.*Hover|IHoverService|HoverEndpoint"
```

The Razor LSP code may have multiple providers (different VS / VS Code
flows); the correct anchor is the one that visits the syntax tree.
Record the class names in `plan-state.md` -- the rest of Stage 5.6
depends on them.

Classification (semantic tokens): `SkippedContentSyntax` -> classified
as "comment" or "unknown" (visually distinct from real code/markup so
the user sees something is wrong).

**Completion language-dispatch.** `SkippedContentSyntax.OriginatingLanguage`
(set by `Synchronize` per Stage 0.4) indicates whether the skipped
region was originally in C# or HTML context. The dispatch rule:

- If `OriginatingLanguage == CSharpCodeBlock` -> C# completion provider.
- If `OriginatingLanguage == MarkupBlock` -> HTML completion provider.
- If `OriginatingLanguage == None` (the document-level case) -> walk
  up the syntax tree ancestor chain and use the closest ancestor's
  language (`CSharpCodeBlockSyntax` -> C#, `MarkupBlockSyntax` -> HTML,
  default `RazorDocumentSyntax` -> HTML since HTML is the outer mode).

Test: `<p @on|` (cursor in skipped attribute name area, after
`@onclick=""` typo) still offers C# event-handler attribute completions.

Hover / "go to definition": missing tokens have no hover; navigate to
the diagnostic instead.

Exit criteria:

- Stage 5.6.0 anchor class list is recorded in `plan-state.md`.
- A focused suite of LSP integration tests (named `RazorRecoveryLspTests`
  or similar; add to
  `src/Razor/src/Razor/test/Microsoft.CodeAnalysis.Razor.Workspaces.UnitTests/`)
  covers each of these paths against the corpus. Each test is a
  specific filterable `[Fact]`, not a vague "focused suite":
    - `Classification_SkippedContent_AppearsAsComment`
    - `Completion_InsideCSharpSkippedContent_OffersCSharpCompletions`
    - `Completion_InsideHtmlSkippedContent_OffersHtmlCompletions`
    - `Hover_OnMissingToken_FallsBackToDiagnostic`

### Stage 5 exit criteria (gate to Phase 6)

- Each downstream consumer in 5.0-5.6 either handles the new shape
  correctly or has an explicit, documented escape hatch (treat
  `SkippedContentSyntax` as comment, treat `MissingToken` as no-op).
- The full corpus, run end-to-end (parser -> codegen -> Roslyn ->
  diagnostics -> source mappings -> LSP), shows the wall-of-red
  metrics from Stage 0.1 reduced to single-token diagnostics.
- **End-to-end "0 cascading CS diagnostics"** for every corpus case in
  the `ParserRecoveryCorpus_CodegenSafetyTests` suite (this is the
  metric deferred from Stages 2 and 3).
- **Source-mapping max width <= 50 chars** for every corpus case.

---

## Stage 6 -- Flip the flag, remove legacy paths, polish

**Goal.** Make enhanced recovery the default and delete the legacy
recovery paths.

### Stage 6.1 -- Flip `UseEnhancedRecovery` default to `true`

Run the full test suite. Update baselines for tests that were on the
old recovery path -- they will all change shape. This stage is heavy
on baseline churn; expect to update hundreds of baseline files.

Exit criteria: full test suite green with `UseEnhancedRecovery = true`
as default.

### Stage 6.2 -- Delete the legacy paths

Remove the `if (Context.Options.UseEnhancedRecovery)` branches in every
parser function migrated in Stages 1-3 (keep the enhanced branch).
Remove `UseEnhancedRecovery` from `RazorParserOptions` entirely (the
flag bit, getter, builder setter, and any tests that exercise both
modes).

Remove now-dead helper code: the old `AcceptUntil(LessThan)` panic
patterns, any diagnostic factories that were used only by legacy
recovery (audit via grep before delete).

**Safety procedure** (multi-step, do all):

1. `Select-String -Path D:\projects\roslyn4\src\Razor -Pattern "UseEnhancedRecovery" -Recurse` -> expect 0 hits after the deletion PR.
2. Manual review of every diagnostic factory deletion: each should
   verify that no remaining caller uses the legacy factory (by name
   search) before removing it from `RazorDiagnosticFactory.cs` and
   `Resources.resx`.
3. Full test suite green:
   `dotnet test src\Razor\src\Compiler\Microsoft.AspNetCore.Razor.Language\test\Microsoft.AspNetCore.Razor.Language.UnitTests.csproj`
   AND
   `dotnet test src\Razor\src\Compiler\Microsoft.AspNetCore.Razor.Language\legacyTest\Microsoft.AspNetCore.Razor.Language.Legacy.UnitTests.csproj`
   AND
   `dotnet test src\Razor\src\Compiler\test\Microsoft.NET.Sdk.Razor.SourceGenerators.UnitTests\Microsoft.NET.Sdk.Razor.SourceGenerators.UnitTests.csproj`.
4. No baseline regressions outside the expected enhanced-mode shape.

Exit criteria:

- `Select-String -Path "...\CSharpCodeParser.cs" -Pattern "AcceptUntil\(SyntaxKind\.LessThan"` returns 0 hits.
- Same for `HtmlMarkupParser.cs`.
- `UseEnhancedRecovery` no longer appears anywhere in the codebase
  (`Select-String -Recurse`).
- All test suites listed above are green.

### Stage 6.3 -- Update documentation

Update:

- `src/Razor/docs/Parsing.md` -- add a section describing
  `SkippedContentSyntax` and the missing-token invariant.
- Add `src/Razor/docs/parser-recovery.md` (new, kebab-case per the
  user-level naming convention in `.github/instructions/Documentation`)
  -- explains the recovery model, the follow-set protocol, and how
  to write a new parser function that uses `Required`/`Synchronize`.
  This is a developer-facing reference doc and sits alongside the
  existing `Parsing.md`, *not* under
  `src/Razor/docs/plans/ErrorRecovery/` (that directory is reserved
  for the historical plan / analysis / state artifacts).

Exit criteria:

- Both files exist and are reachable from the existing docs index
  (verify via `Select-String -Path "src\Razor\docs\**\*.md" -Pattern "parser-recovery"`).
- Internal links in the new doc resolve (no broken `[...](path)`
  references). Optional: run a markdown link checker if one is
  available locally; otherwise eyeball them.
- The new file is committed and visible in `git log` for the docs
  commit.

### Stage 6.4 -- Performance pass

The new recovery model should not regress parse time on well-formed
input. The existing Razor compiler benchmarks live at:

- `src/Razor/src/Compiler/perf/Microbenchmarks/Microsoft.AspNetCore.Razor.Microbenchmarks.Compiler.csproj`
- `src/Razor/src/Compiler/perf/Microsoft.AspNetCore.Razor.Microbenchmarks.Generator/...`
- `src/Razor/src/Razor/benchmarks/Microsoft.AspNetCore.Razor.Microbenchmarks/`

**There is no existing parser-only benchmark.** Either:

(a) Add a new parser benchmark in
`src/Razor/src/Compiler/perf/Microbenchmarks/` (suggested:
`ParserBenchmarks.cs`) that, for each corpus file plus a handful of
well-formed `.razor` files (Counter.razor and FetchData.razor from a
default Blazor template are good baselines), measures
`RazorParser.Parse(source)` time and allocations under BenchmarkDotNet.
The existing
`src/Razor/src/Compiler/perf/Microbenchmarks/*.cs` files (e.g., the
existing codegen and tag-helper benchmarks in the same csproj) are
the templates -- read one to crib the `[MemoryDiagnoser]` /
`[Benchmark]` / `[GlobalSetup]` attribute conventions before authoring
the new file.

OR

(b) Use a coarser proxy: time the corpus snapshot suite under
`dotnet test ... --filter ParserRecoveryCorpusSnapshotTests` with a
stopwatch wrapper and compare elapsed wall time pre/post.

Pick (a) for rigour. Pick (b) only if BenchmarkDotNet integration is
blocked (it shouldn't be -- the existing perf projects already use it).

Common regressions to watch:

- `FollowSet` allocation if implemented naively (use struct, not class;
  pool predicates).
- `SkippedContentSyntax` allocations if recovery is unexpectedly hot on
  well-formed code (it shouldn't fire at all on well-formed input;
  if it does, something is wrong).
- `Synchronize` re-tokenisation if a path slips through.

Exit criteria: benchmark numbers within ±3% of pre-change baseline for
well-formed inputs; documented regressions (with justification) for
ill-formed inputs (which will naturally be slower since they do more
work). Record numbers in `plan-state.md` under "Performance baseline".

### Stage 6 exit criteria (gate to Phase 7)

- Flag is gone. Legacy paths are gone. Docs are updated. Benchmarks are
  within tolerance.

---

## Stage 7 -- Persist, communicate, hand off

**Goal.** Make the plan and its rationale discoverable for future agents.

- Tag the final commit of Stage 6 with `razor-recovery-redesign-complete`.
  **Verify the origin remote before pushing:** run
  `git remote get-url origin` and check that the URL contains the user's
  GitHub login (e.g., `chsienki/roslyn`) and **not** `dotnet/roslyn`.
  If origin points at `dotnet/roslyn`, abort and ask the user which
  remote to push the tag to. Then run
  `git push <verified-remote> razor-recovery-redesign-complete`.
  Record the chosen remote name in `razor-recovery-redesign-plan-state.md`.
  Do NOT push to upstream (`dotnet/roslyn`) -- the user owns that
  decision.
- Rename this plan file (in place) to
  `razor-recovery-redesign-completed-plan.md` and add a header note
  declaring it historical (kebab-case per the docs convention).
- Rename `razor-recovery-redesign-plan-state.md` to
  `razor-recovery-redesign-completed-plan-state.md` likewise. Future
  agents reading the completed plan get the full execution record
  alongside.
- Cross-link from `src/Razor/docs/parser-recovery.md`
  (Stage 6.3's new doc) -- under a "How we got here" section.

Exit criteria:

- Tag exists and is pushed to the **verified remote** (the one chosen
  by the `git remote get-url origin` check above; recorded in
  `razor-recovery-redesign-plan-state.md`).
- Plan file is renamed to `razor-recovery-redesign-completed-plan.md`
  with the historical header.
- State file is renamed to
  `razor-recovery-redesign-completed-plan-state.md`, marked historical.
- Cross-link is in place and resolves.

---

## Reference: tricky mechanics the stages assume

### Where to find things

| What | Path |
|------|------|
| `Syntax.xml` | `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Syntax/Syntax.xml` |
| Syntax generator | `src/Razor/src/Compiler/tools/RazorSyntaxGenerator/` |
| Generated syntax | `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Syntax/Generated/Syntax.xml.*.Generated.cs` |
| `RazorParser` | `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Legacy/RazorParser.cs` |
| `CSharpCodeParser` | `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Legacy/CSharpCodeParser.cs` |
| `HtmlMarkupParser` | `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Legacy/HtmlMarkupParser.cs` |
| `TokenizerBackedParser` | `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Legacy/TokenizerBackedParser.cs` |
| `SeekableTextReader` | `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Legacy/SeekableTextReader.cs` |
| Source mapping | `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/SourceMapping.cs` and `CodeGeneration/CodeRenderingContext.cs` |
| Tag-helper rewriter | `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Legacy/TagHelperParseTreeRewriter.cs` |
| Tag-helper attribute rewriter | `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Legacy/TagHelperBlockRewriter.cs` |
| Default tag-helper resolution / RZ2008 emit | `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/DefaultTagHelperResolutionPhase.LegacyTagHelperResolver.cs` |
| Source generator | `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/SourceGenerators/RazorSourceGenerator.cs` |
| Razor language unit tests | `src/Razor/src/Compiler/Microsoft.AspNetCore.Razor.Language/test/` |
| Razor language legacy tests (parser-recovery work) | `src/Razor/src/Compiler/Microsoft.AspNetCore.Razor.Language/legacyTest/` |
| Parser-recovery corpus + snapshots | `src/Razor/src/Compiler/Microsoft.AspNetCore.Razor.Language/legacyTest/ParserRecoveryCorpus/` |

### How `SyntaxFactory.MissingToken` works today

`SyntaxToken.CreateMissing(kind, diagnostics)` returns a `MissingToken`
private class instance whose `Content` is `string.Empty` and whose
`Flags` contains `NodeFlags.IsMissing`. Width is zero. The diagnostic
array, if any, is attached via the standard
`GreenNode.SetDiagnostics` mechanism.

`NodeFlags` (`NodeFlags.cs`) has bits free; adding `IsSkipped` if
useful is cheap. Initial plan does not require it -- `SkippedContentSyntax`
is itself the marker.

### How `SyntaxKind` is extended

`SyntaxKind.cs` is **hand-authored** (no `// <auto-generated />`
header, lives in `Syntax/` not `Syntax/Generated/`, contains comments
like `// New nodes should go before this one` and the sentinel
`FirstAvailableTokenKind`). The syntax generator produces
`Syntax.xml.{Internal,Main,Syntax}.Generated.cs` -- **not**
`SyntaxKind.cs`.

To add a new kind:

1. Add `<Kind Name="X" />` inside the relevant `<Node>` in
   `Syntax.xml` (this is what the green/red tree generation uses).
2. **Manually** add `X` to the `#region Nodes` block of
   `SyntaxKind.cs`, before `FirstAvailableTokenKind`.
3. Run the syntax generator.
4. Build and check that no `error CS0117` ("does not contain a
   definition for X") appears -- if it does, step 2 was skipped.

### Codegen placeholder matrix (full version of Stage 5.1's matrix)

The exact placeholder depends on the surrounding generated C# context.
For each entry, the binding type comes from
`TagHelperPropertyIntermediateNode.BoundAttribute.TypeName` (resolved
at `DefaultTagHelperResolutionPhase.LegacyTagHelperResolver.LowerBoundLegacyAttributeValue`).
Thread it into IR (Stage 5.0) so codegen can read it.

| Code-emission context | Discriminator | Emit |
|---|---|---|
| `EventCallback<T>` bound attribute | `BoundAttribute.IsEventCallbackProperty` && type-args known | `default(global::System.Action<T>)` |
| `EventCallback` bound attribute (no type arg) | `BoundAttribute.IsEventCallbackProperty` && no type-args | `default(global::System.Action)` |
| Other bound attribute, type fully known | `BoundAttribute.TypeName` resolves | `default(<fullTypeName>)` |
| Bound attribute, type generic / unresolved | type not resolvable at codegen time | `default!` |
| `@expr` in markup output | inside `WriteCSharpExpression` for output | `""` |
| C# expression in statement context (inside `@{}`) | inside statement writer | `_ = (object?)null` |
| Argument position in synthesized call (e.g. `Create<T>(this, _)`) | inside argument list of an injected call | `default(<argType>)` |

The default if none of the above matches: `default!`. Each row has a
compile-test in Stage 5.1's exit criteria.

### Test baseline duplication policy

During the migration (Stages 1-5), tests can run under either mode.
The rule:

- **Corpus snapshots only** carry dual baselines (`Legacy.json` and
  `Enhanced.json`). The corpus is the moving target.
- **Existing tests** (`CSharpErrorTest`, `HtmlBlockTest`, etc.) stay on
  legacy mode by default. When a parser function is migrated, the
  small subset of existing tests that touch that function are
  re-baselined to enhanced mode (single baseline, replacing the
  legacy one). Document which tests were re-baselined in the PR
  description.
- After Stage 6.1 (flip default to enhanced), legacy baselines are
  deleted in bulk. After Stage 6.2 (delete legacy paths), the dual
  `Legacy.json` / `Enhanced.json` files in the corpus are collapsed
  to a single `.json` per file.

This avoids the "every test runs twice forever" maintenance burden
while still validating that the migration is correct.

### How `Balance` interacts with new recovery

The new model does NOT replace `Balance` for the case where you can
actually find the matching close. `Balance` remains the right primitive
for "we know the open exists, count nesting until close". Only its
*failure* path changes: instead of `AcceptUntil(LessThan)` after a
failed `Balance`, you `Synchronize(...)` with the outer follow set.
`BalancingModes.BacktrackOnFailure` remains valid (rewinds the
character cursor on failure so the synchronization starts from the
construct's beginning).

### How to add a corpus snapshot

The harness from Stage 0.2 reads each `.razor` file in
`ParserRecoveryCorpus/`, parses under both modes, and writes
`<name>.Legacy.json` and `<name>.Enhanced.json` next to it. Snapshot
files are committed; mismatches are test failures. Update via the
`/p:GenerateBaselines=true` MSBuild property (matching the existing
`IntegrationTests/CodeGenerationIntegrationTest.cs` + `BaselineWriter`
convention -- use this property name **consistently** across all
stages; ignore any references to env vars or `--update-baselines`
switches in earlier draft text).

### How the cross-parser handoff works (pre-change)

`OtherParserBlock`/`ParseBlock` is the cross-parser call. It:

1. Saves `IsNested` state, sets it false.
2. Calls `EndingBlock()` on its own tokenizer (lets it finalise state).
3. Calls the other parser's `ParseBlock(...)`.
4. On return, restores state, calls `StartingBlock()` on its own
   tokenizer, calls `NextToken()`.

The character cursor is shared, so wherever the callee left the cursor
is where the caller resumes. Stage 4 layers the follow-set onto this:
it changes the call signature only, no other dynamics.

### Cancellation

All parser methods thread `CancellationToken`. `Synchronize` and
`Required` must also honour cancellation (call
`CancellationToken.ThrowIfCancellationRequested()` in any inner loop
that runs over O(N) tokens). Failure mode: an editor cancellation
during recovery hangs.

### Threading

The parser is single-threaded by construction (one `RazorParser.Parse`
call per syntax tree). No locking is required. Caches that are
process-wide (e.g. `SyntaxTokenCache`) are already thread-safe.

### Roslyn diagnostic mapping

Final user diagnostics flow:

- Razor parser diagnostics: -> `ErrorSink` -> `RazorSyntaxTree.Diagnostics`
  -> LSP / IDE.
- Generated C# diagnostics: Roslyn parses generated C#, emits
  diagnostics with source positions in the generated file. These are
  mapped via `SourceMapping`s back to `.razor` file positions. Both
  the Razor source-generator pipeline (for diagnostics surfaced via
  IDE diagnostics) and the LSP (for diagnostics surfaced via the
  Razor LSP service) do this mapping.

The "wall of red" fundamentally happens because (a) Razor emits
malformed C# and (b) the mapping is wider than the malformed expression.
Stages 5.1 and 5.3 fix (a) and (b) respectively.

---

## Plan persistence and resumption

This plan is intended to be executed across multiple sessions / days /
weeks. To resume mid-execution:

1. Open this file. Each Stage section begins with a one-line **Goal**
   and ends with **exit criteria**. The exit criteria of the last
   completed stage in `plan-state.md` is the authoritative
   current-state contract.
2. Check the sibling `plan-state.md` for which sub-stage is in progress
   and what was completed. Always update `plan-state.md`, never modify
   this file (except via a deliberate plan-revision PR).
3. Re-run the **Prerequisites** table before resuming. If anything is
   different from when the plan started (SDK version, repo state),
   reconcile before proceeding.
4. Resume at the first sub-stage whose exit criteria are not met. Do
   not re-do completed sub-stages.

`plan-state.md` minimal schema:

```markdown
# Razor recovery plan -- state

## Current stage
Stage 2.3 -- ParseStandardStatement

## Status of each stage
- Stage 0: complete (commit abc1234)
- Stage 0.1: complete (commit ...)
- Stage 0.2: complete (commit ...)
- Stage 0.3: complete (commit ...)
- Stage 0.4: complete (commit ...)
- Stage 0.5: complete (commit ...)
- Stage 1: complete (commits ..., ...)
- Stage 1.1: complete
- ...
- Stage 2.1: complete (PR #...)
- Stage 2.2: complete (PR #...)
- Stage 2.3: in progress
- Stages 2.4-7: not started

## Notes
- (date) Found that `ParseStandardStatement` needed a special case
  for `@@` escape; updated to handle.
- (date) Baseline churn in Stage 2.1 was ~80 files; reviewer noted
  some baselines were over-asserting whitespace; trimmed those.
```

---

## Known limitations / residual risks

These are explicitly accepted (rather than fixed by the plan) and the
executor should not be surprised by them:

1. **Pre-tokenisation not addressed.** The shared `SeekableTextReader`
   cursor remains. Recovery still costs tokenize + putback. If
   `Synchronize` proves hot, that's a follow-up project.
2. **Two-parser architecture preserved.** A future plan may merge them;
   not this one.
3. **C# parsing is still hand-rolled.** This plan improves Razor's own
   C# recovery; it does not delegate to Roslyn for the C# slices. A
   future plan may; not this one.
4. **Codegen safe-placeholder rules are heuristic.** "Emit `default!`
   here" works for most cases but a tighter type-aware choice could
   produce slightly better diagnostics. Stage 5.1's choices are a
   reasonable baseline.
5. **The legacy editor in-place incremental-parse infrastructure**
   (`SpanEditHandler`, `AcceptedCharactersInternal`,
   `AutoCompleteEditHandler`) is touched only where it intersects
   directly. A broader cleanup is separate work.
