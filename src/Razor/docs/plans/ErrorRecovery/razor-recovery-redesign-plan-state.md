# Razor recovery plan -- state

Sidecar state file for `razor-recovery-redesign-plan.md` (in the same
directory). The plan is the immutable contract; this file is the
transient run-state that should be updated as each sub-stage
completes.

## Current stage
Stage 5.0 complete. Ready for Stage 5.1.

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
- Stage 1.3: complete
- Stage 1.4: complete
- Stage 2.1: complete
- Stage 2.2: complete
- Stage 2.3: complete
- Stage 2.4: complete
- Stage 2.5: complete
- Stage 2.6: complete
- Stage 3.1: complete
- Stage 3.2: complete
- Stage 3.3: complete
- Stage 3.4: complete
- Stage 4.1: complete
- Stage 4.2: complete
- Stage 4.3: complete
- Stage 4.4: complete
- Stage 5.0.0: complete
- Stage 5.0: complete
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

Inventory of current maximum RZ ID in each range (recorded by Stage 1.3;
re-verify with the `Select-String '\$"\{DiagnosticPrefix\}(\d{4,5})"'`
discovery procedure in the plan). No new RZ IDs were allocated by Stage
1.3 -- the paired `_At` factories reuse the existing factory's
`RazorDiagnosticDescriptor` (same `RZxxxx` ID, same message). Genuinely
new diagnostics are allocated by Stages 2.x / 3.x when their call sites
appear.

- **RZ0xxx** (general / infrastructure) -- max in use: **RZ0000**
  (`Directive_BlockDirectiveCannotBeImported`).
- **RZ1xxx** (parser diagnostics) -- max in use: **RZ1048**. Next free
  parser-recovery ID: **RZ1049**. (Stage 2.3 allocated RZ1046 for
  `Parsing_UnexpectedTokenInStatement`, a new diagnostic emitted at
  zero width when the panic-else of `ParseStandardStatement` fires;
  Stage 3.1 allocated RZ1047 for `Parsing_TagNameExpected`, a new
  diagnostic emitted at zero width when the HTML tag-name slot is
  empty in `ParseStartTag` / `ParseEndTag` (e.g. `<>`, `</>`).
  Stage 3.4 allocated RZ1048 for `Parsing_UnexpectedAttributeName`,
  a new diagnostic emitted at zero width when `ParseMiscAttribute`
  encounters a token that cannot start an attribute name.
  See notes below for empirical observations on reachability.)
- **RZ2xxx** (tag-helper / binding diagnostics) -- max in use:
  **RZ2012**. Next free: **RZ2013**.
- **RZ3xxx** (descriptor / tag-helper-resolution diagnostics) -- max in
  use: **RZ3017**. Next free: **RZ3018**.
- **RZ9xxx** (component-specific diagnostics in
  `ComponentDiagnosticFactory.cs`) -- max in use: **RZ9999**. Next free:
  **(none -- range exhausted)**. New component diagnostics must use
  RZ10xxx.
- **RZ10xxx** (component-specific diagnostics in
  `ComponentDiagnosticFactory.cs`) -- max in use: **RZ10024**. Next
  free: **RZ10025**.

## Stage 5.0.0 spike report

Investigation-only test added at
`src/Razor/src/Compiler/test/Microsoft.NET.Sdk.Razor.SourceGenerators.UnitTests/RazorSourceGeneratorComponentTests.cs`,
method `Spike_EmptyOnclickAttribute_DumpsGeneratedSource` (around
line 1879). The test exercises three configurations and dumps the
generated C# plus Razor diagnostics under `artifacts/razor-recovery-spike/`
(gitignored):

1. Source-generator pipeline (legacy parser, real tag-helper discovery
   from the test compilation -- the user-visible path).
2. Direct `RazorProjectEngine` with `UseEnhancedRecovery = false`.
3. Direct `RazorProjectEngine` with `UseEnhancedRecovery = true`
   (BDD #9 tree shape -- Stage 3.2's `GenericBlock([CSharpExpressionLiteral
   ([MissingToken(Identifier)])])`).

Reproducer (the canonical corpus content from
`legacyTest/ParserRecoveryCorpus/EmptyBoundAttribute_Onclick.razor`,
with `@using Microsoft.AspNetCore.Components.Web` prepended so
`EventHandlerTagHelperProducer` actually discovers `onclick` as a
bound event-handler attribute):

```razor
@using Microsoft.AspNetCore.Components.Web

<h1>Counter</h1>

<p>Current count: @currentCount</p>

<button class="btn btn-primary" @onclick="">Click me</button>

@code {
    private int currentCount = 0;

    private void IncrementCount()
    {
        currentCount++;
    }
}
```

### Findings per configuration

**Configuration 1 (source generator, legacy mode -- user-visible
path).** With the `@using Microsoft.AspNetCore.Components.Web` import
the tag-helper discovery fires and `@onclick` is recognized. IR
lowering then dispatches to
`ComponentEventHandlerLoweringPass.RewriteUsage`
(`src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Components/ComponentEventHandlerLoweringPass.cs:161`),
where the **early bail-out at lines 164-169** fires because
`GetAttributeContent` returns `ImmutableArray<IntermediateToken>.Empty`
(the legacy parser produced no value node for `@onclick=""`):
```csharp
if (original.Length == 0)
{
    // This can happen in error cases, the parser will already have
    // flagged this as an error, so ignore it.
    return node;
}
```
**The entire `@onclick` attribute is silently dropped** from the
generated render-tree calls. The artifact
`artifacts/razor-recovery-spike/empty-onclick.sourcegen.razor.g.cs`
contains:
```csharp
__builder.OpenElement(5, "button");
__builder.AddAttribute(6, "class", "btn btn-primary");
__builder.AddContent(7, "Click me");
__builder.CloseElement();
```
Note: no `AddAttribute` for `onclick` at all. **No CS1525 is emitted
in the current legacy source-generator output for the BDD #9
reproducer.** This was the first surprise of the spike: the plan's
motivating "wall of red CS1525" for `<button @onclick="">` is
**already** mitigated for the plain-HTML-element case by the
`original.Length == 0` bail-out. The remaining CS1525 risk lives
elsewhere -- see "Codegen sites at risk" below.

**Configuration 2 (direct engine, legacy mode).** No tag-helper
discovery (the direct path does not wire up
`StaticCompilationTagHelperFeature`), so `@onclick` falls through to
plain markup. Artifact `empty-onclick.legacy.razor.g.cs` produces a
single `AddMarkupContent` with the raw HTML, no separate attribute
emit, no CS1525. This configuration is **not informative** for the
codegen question (the lowering pass that owns the bug is never
reached); it's retained as a control showing that direct-engine
output is parser-shape-driven only.

**Configuration 3 (direct engine, enhanced mode -- BDD #9).** Same
absence of tag-helper discovery, so the `@onclick` attribute is again
treated as plain HTML. With the BDD #9 tree shape on the parser side,
codegen emits `__builder.AddAttribute(7, "@onclick");` (the
two-argument `AddAttribute` overload, no value) -- valid C#, no
CS1525. Artifact: `empty-onclick.enhanced.razor.g.cs`. Again the
lowering pass is not reached, so this configuration cannot directly
exhibit the bug; its purpose is to confirm that the BDD #9 tree shape
does not introduce a regression on the *non-component* HTML attribute
codegen path.

Razor parser diagnostics for configurations 2 and 3 are both empty
(`empty-onclick.razor-diagnostics.txt`), confirming the direct-engine
path produces no parser errors for `@onclick=""` in either parser
mode -- so any CS1525 the user sees comes from C# codegen, not from
the Razor parser.

### Codegen sites at risk (the real bug surface for Stage 5.0 / 5.1)

Two sites emit `EventCallback.Factory.Create<T>(this, <code>)`:

1. **`ComponentEventHandlerLoweringPass.cs:181-186`** -- assembles
   IR tokens of the form
   `global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<T>(this, ` + `original` + `)`.
   Used for `@onclick`-style event-handler directive attributes on
   **plain HTML elements** (Razor synthesises an `EventCallback`
   wrapper). **Gated by the `original.Length == 0` bail-out at
   line 164-169.** In legacy mode the bail-out fires for `@onclick=""`
   and the attribute is dropped (Configuration 1 above). **In
   enhanced mode the BDD #9 tree shape surfaces a single
   `MissingToken`-bearing `CSharpIntermediateToken`** through
   `CSharpExpressionLiteral` lowering. If that token reaches
   `GetAttributeContent` (`ComponentEventHandlerLoweringPass.cs:232`),
   `original.Length` is **1, not 0**, the bail-out skips, and codegen
   emits `EventCallback.Factory.Create<MouseEventArgs>(this, )`
   (single missing-content token sandwiched between `, ` and `)`).
   That is the CS1525 site. The bug only manifests once both
   (a) BDD #9 is **on** (Stage 3.2 lights this up under the
   `UseEnhancedRecovery` flag), and (b) the missing-content token
   survives lowering as a length-1 array.

2. **`ComponentNodeWriter.cs:1335-1376`** -- emits
   `EventCallback.Factory.Create<T>(this, <tokens>)` directly during
   codegen for `ComponentAttributeIntermediateNode` whose bound
   attribute satisfies `BoundAttribute.IsEventCallbackProperty()`
   (i.e. `<MyComponent OnClick="..."/>` where the parameter is
   declared `EventCallback<T>`). **This site is NOT guarded by any
   length check** -- `WriteCSharpTokens(context, tokens)` at line
   1374 emits whatever tokens it receives. If the value is empty
   (or a single empty-content missing token), the emitted text is
   `EventCallback.Factory.Create<T>(this, )` -- CS1525, today, in
   legacy mode, for *component* event-handler attributes
   `OnXxx=""`. The motivating bug `dotnet/razor#10383` very likely
   originates here for the *component-attribute* variant; the
   plain-HTML-element variant happens to be mitigated by the
   `RewriteUsage` bail-out.

### Spike-required four bullets

- **Malformed C# expression emitted today**: the literal text
  `EventCallback.Factory.Create<{eventArgsType}>(this, )` --
  closing paren immediately follows the second comma with no
  expression. Produces Roslyn CS1525 ("Invalid expression term ')'")
  at the position of the closing paren.
- **Writer file:line(s)**:
  - `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Components/ComponentEventHandlerLoweringPass.cs:181-186`
    (lowering-time IR-token assembly; gated by line 164-169
    bail-out). Path: plain-HTML element + directive attribute
    `@onclick=""`. Currently safe in legacy mode (bail-out fires).
    **Will break in enhanced mode** unless Stage 5.0 hardens the
    bail-out or the placeholder.
  - `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Components/ComponentNodeWriter.cs:1351-1376`
    (codegen-time direct emit; **unguarded**). Path: component
    attribute bound to an `EventCallback<T>` parameter, written with
    an empty value (`OnClick=""`). **Already breaks today** in
    legacy mode; #10383's "wall of red" most plausibly originates
    here.
- **IR node type(s) flowing in**:
  - For the lowering-pass site:
    `TagHelperDirectiveAttributeIntermediateNode` (input) ->
    `HtmlAttributeIntermediateNode` containing a
    `CSharpExpressionAttributeValueIntermediateNode` (output for
    `MarkupElementIntermediateNode` parent at line 190-213) OR
    `ComponentAttributeIntermediateNode` containing a
    `CSharpExpressionIntermediateNode` (output for component-parent
    at line 214-229). The content-bearing leaves are
    `IntermediateToken` (specifically `CSharpIntermediateToken`).
  - For the node-writer site:
    `ComponentAttributeIntermediateNode` with
    `BoundAttribute.IsEventCallbackProperty() == true`. Tokens come
    from a child `CSharpExpressionIntermediateNode`.
- **Recommended placeholder shape (for Stage 5.0 / 5.1)**: when
  the original token stream is "effectively empty" (length 0, OR
  length >= 1 with every token having `string.IsNullOrEmpty(Content)`),
  substitute a single token containing `default!`. Concretely the
  emitted call becomes
  `EventCallback.Factory.Create<{eventArgsType}>(this, default!)`.
  Rationale: (a) parses cleanly under Roslyn; (b) `default!` resolves
  unambiguously to `EventCallback<T>` (or any `Delegate`-typed
  overload `Create<T>` chooses) without producing CS8625 because of
  the suppression; (c) the synthetic-vs-user position information is
  preserved (codegen still emits `#line` markers around the
  placeholder so IDE tooling like signature help / completion
  re-targets to the original `""` span); (d) does not introduce
  runtime divergence -- a `default` EventCallback is a no-op handler,
  which is the same observable behaviour as the current bail-out's
  "attribute silently dropped".

  The placeholder logic must be added to **both** writer sites for
  full coverage. The lowering-pass site (`RewriteUsage`) should
  replace the line-164 bail-out with the placeholder substitution
  (so it works in both legacy and enhanced mode and on both HTML
  and component parents); the `ComponentNodeWriter` site needs the
  same emptiness probe over `tokens` before the `WriteCSharpTokens`
  call at line 1374.

### Deviations from the plan procedure

- **The plan envisioned the spike empirically dumping CS1525 from
  the source-generator output for `<button @onclick="">`.** Empirical
  finding: the source-generator output for that exact reproducer
  has **no** CS1525 today because of the `RewriteUsage`
  `original.Length == 0` bail-out. The motivating bug therefore lives
  on the **component-attribute** branch
  (`ComponentNodeWriter.cs:1351-1376`), not the plain-HTML-element
  branch. The spike report documents both sites; Stage 5.0 / 5.1
  must cover both.
- **The `UseEnhancedRecovery` flag is not surfaced through the
  source generator** (it is internal compiler scaffolding -- per
  Stage 0.3's downstream audit it is deliberately not added to
  `RazorConfiguration` / `RazorSourceGenerationOptions`). The spike
  reaches the flag via `InternalsVisibleTo
  "Microsoft.NET.Sdk.Razor.SourceGenerators.UnitTests"`, and only
  through the direct `RazorProjectEngine` path -- which does **not**
  do tag-helper discovery. Consequently the spike cannot directly
  exhibit `EventCallback.Factory.Create<T>(this, )` under enhanced
  mode in this harness; the inference about enhanced-mode behaviour
  is based on reading `GetAttributeContent`
  (`ComponentEventHandlerLoweringPass.cs:232-254`) and the BDD #9
  parse-tree shape from Stage 3.2. Stage 5.0's first action will be
  to wire tag-helper discovery into the spike's direct path (or
  flip the source generator's options surface) so the enhanced-mode
  emission can be observed directly before the codegen fix lands.

## Stage 5.6.0 LSP anchor classes
(not yet identified -- to be populated with classification /
completion / hover provider class names)

## Stage 2 verification
(parser-recovery branch counts; populate during Stage 2 gate check)

- Stage 2.1 (after migration):
  - `AcceptUntil(SyntaxKind.LessThan` in `CSharpCodeParser.cs`:
    4 occurrences total (lines 501, 636, 1184, 1198).
    - Line 501 (`ParseExplicitExpressionBody`) is now inside the
      `else` of the `UseEnhancedRecovery` guard (the Stage 2.1
      legacy branch); the new enhanced branch is `Synchronize`.
    - Lines 636 (implicit-expression fallback in
      `ParseImplicitExpression`), 1184 / 1198
      (`ParseStandardStatement`-family) remain untouched -- they
      are owned by Stages 2.3 / 2.6.
    All 4 to be deleted by Stage 6.2 once their owning stages
    have shipped enhanced branches.

- Stage 2.6 (after migration, Stage 2 final audit):
  - `AcceptUntil(SyntaxKind.LessThan` in `CSharpCodeParser.cs`:
    4 occurrences total (lines 501, 699, 1378, 1423).
    Line numbers shifted slightly due to Stage 2.2-2.6 insertions
    but the SET is unchanged from Stage 2.1's audit. All four are
    now confirmed inside `else` branches of `UseEnhancedRecovery`
    guards (verified by inspection of each site -- the Stage 2.6
    implicit-expression site at line 699 was the last one to migrate;
    the Stage 2.3 statement-family sites at lines 1378 and 1423
    are inside `TryBalanceBlock` and the explicit-expression
    fallback's legacy branch).
  - **Stage 2 exit criterion met:** every enhanced branch in
    `CSharpCodeParser.cs` is `AcceptUntil(LessThan)`-free. The
    remaining 4 legacy occurrences will be deleted by Stage 6.2
    cleanup once the `UseEnhancedRecovery` flag is removed.

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

## Stage 4.1 verification

Stage 4.1 added the overload-pair signatures for `ParseBlock` and
`OtherParserBlock` on both parsers, the `_outerFollow` private field
(stored but not yet consumed), and the `RecoveryFollowSets.ForCSharpCallee`
/ `RecoveryFollowSets.ForHtmlCallee` translation helpers per Big Design
Decision #4. Behaviour-inert: legacy 1335 / 1335 + language 3600 / 3600,
unchanged on both `net10.0` and `net472`.

### Signatures added

- `CSharpCodeParser.ParseBlock()` -> wrapper delegating to `ParseBlock(FollowSet.Empty)`.
- `CSharpCodeParser.ParseBlock(FollowSet outerFollow)` -> stores
  `outerFollow` in the new `_outerFollow` field (save/restore in
  try/finally) and delegates to private `ParseBlockCore()` which holds
  the pre-existing body.
- `CSharpCodeParser.OtherParserBlock(in SyntaxListBuilder<RazorSyntaxNode> builder)`
  -> wrapper delegating to `OtherParserBlock(builder, FollowSet.Empty)`.
- `CSharpCodeParser.OtherParserBlock(in SyntaxListBuilder<RazorSyntaxNode> builder, FollowSet outerFollow)`
  -> pre-existing body, with the `HtmlParser.ParseBlock()` handoff now
  passing `RecoveryFollowSets.ForHtmlCallee(outerFollow)`.
- Same overload-pair shape on `HtmlMarkupParser.ParseBlock` /
  `HtmlMarkupParser.OtherParserBlock`, with the `OtherParserBlock`
  handoff calling `CodeParser.ParseBlock(RecoveryFollowSets.ForCSharpCallee(outerFollow))`.

Because every Stage 4.1 caller still goes through the parameterless
wrapper, `outerFollow` is always `FollowSet.Empty`; translation of an
empty set is empty; no recovery code consumes `_outerFollow` yet -- so
Stage 4.1 is a true no-op.

### Translation helpers added (in `RecoveryFollowSets.cs`)

- `public static FollowSet ForCSharpCallee(FollowSet htmlSet)` --
  HTML-side -> C#-side translation. Maps `OpenAngle`->`LessThan`,
  `CloseAngle`->`GreaterThan`, `ForwardSlash`->`Slash`; drops
  `DoubleQuote` / `SingleQuote` (C# absorbs `"`/`'` into
  `StringLiteral` / `CharacterLiteral`); preserves the shared
  structural kinds `Whitespace`, `NewLine`, `Equals`, `Transition`;
  all other kinds dropped.
- `public static FollowSet ForHtmlCallee(FollowSet csharpSet)` --
  C#-side -> HTML-side translation. Maps `LessThan`->`OpenAngle`,
  `GreaterThan`->`CloseAngle`, `Slash`->`ForwardSlash`; drops
  `Semicolon` / `LeftBrace` / `RightBrace` / `LeftParenthesis` /
  `RightParenthesis` (no HTML equivalent); preserves the shared
  structural kinds `Whitespace`, `NewLine`, `Equals`, `Transition`;
  all other kinds dropped.

### Per-call-site mapping table (for Stage 4.2)

Re-verified line numbers via grep against the post-Stage-3.4 source
tree. Line numbers will continue to drift as Stage 4.2 inserts new
parameters; treat these as cursors for re-grep before editing.

| # | Caller                                                                         | Callee                          | Outer follow set to thread (in callee's language)                                                                                                                                                                                                                                                                                                                              |
|---|--------------------------------------------------------------------------------|---------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 1 | `CSharpCodeParser.OtherParserBlock` callsite at `CSharpCodeParser.cs:1060` (inside `ParseStatement`, markup-transition branch) | `HtmlParser.ParseBlock`         | Caller's local recovery set, translated to HTML via `RecoveryFollowSets.ForHtmlCallee(...)`. Caller is C#-statement / statement-body context; typical local set includes `RightBrace` / `Semicolon` (both drop in translation) plus `Transition` (preserved).                                                                                              |
| 2 | `CSharpCodeParser.OtherParserBlock` callsite at `CSharpCodeParser.cs:1454` (inside `ParseTemplate`)                            | `HtmlParser.ParseBlock`         | Caller's local recovery set, translated to HTML via `RecoveryFollowSets.ForHtmlCallee(...)`. `ParseTemplate` is invoked from inside an explicit-expression body; the propagated outer follow contributes the enclosing `RightParenthesis` (drops in translation).                                                                                                              |
| 3 | `HtmlMarkupParser.OtherParserBlock` from `ParseAttribute` (`HtmlMarkupParser.cs:1348`, `AttributeNameParsingResult.CSharp` arm) | `CodeParser.ParseBlock`         | HTML attribute-terminator set `(OpenAngle, ForwardSlash, DoubleQuote, SingleQuote)` translated via `RecoveryFollowSets.ForCSharpCallee(...)` -> `(LessThan, Slash)` (the quote kinds drop because the C# tokenizer eats `"`/`'` as part of string/character literals).                                                                                                          |
| 4 | `HtmlMarkupParser.OtherParserBlock` from `ParseConditionalAttributeValue` (`HtmlMarkupParser.cs:1703`)                          | `CodeParser.ParseBlock`         | If `quote != SyntaxKind.Marker` (quoted value): the closing-quote kind degenerates in C#, so the most useful sync token is `NewLine` (force end-of-line recovery). If `quote == SyntaxKind.Marker` (unquoted): the full unquoted-attribute-terminator set translated to C# -> `(LessThan, Slash, GreaterThan, Equals, Whitespace, NewLine)`. See `IsUnquotedEndOfAttributeValue` in `HtmlMarkupParser.cs` for the authoritative HTML-side list. |
| 5 | `HtmlMarkupParser.OtherParserBlock` from `ParseCodeTransition` (`HtmlMarkupParser.cs:1926`)                                     | `CodeParser.ParseBlock`         | The current HTML follow set inherited from the enclosing `ParseMarkupNodes`, translated via `RecoveryFollowSets.ForCSharpCallee(...)`. Stage 4.2 must thread `outerFollow` through `ParseMarkupNodes` / `ParseCodeTransition` to be available at this site.                                                                                                                    |
| 6 | `CSharpCodeParser.ParseNestedBlock` (definition `CSharpCodeParser.cs:1166`, internal `ParseBlock()` call at `1174`, lone caller at `1161`) | `CSharpCodeParser.ParseBlock` | Parent's `outerFollow` (same language; no translation needed). Stage 4.2 adds an `outerFollow` parameter to `ParseNestedBlock` and threads it from the `ParseEmbeddedExpression` caller chain.                                                                                                                                                                                  |
| 7 | `CSharpCodeParser` extensible-directive handler at `CSharpCodeParser.cs:2170` (inside the `DirectiveKind.RazorBlock` branch of `ParseExtensibleDirective`) | `HtmlParser.ParseRazorBlock`    | The block's nesting sequences (no translation needed -- these are matched as text by `ParseRazorBlock`). Stage 4.2 should not add `outerFollow` plumbing through `ParseRazorBlock` unless evidence of cross-parser bail-back from inside `@section`/`@functions` shows up in Stage 4.2's test runs; this row is informational only.                                              |

The total is **seven** call sites once `OtherParserBlock`'s two C#-side
callers are counted separately (the plan's framing said "6 call sites"
and lumped the two C# `OtherParserBlock` callers into one row -- the
distinction surfaces because the two C# callers live in different parse
contexts and need to compute their outer follow set independently).

### Tests
- Legacy: 1335 / 1335 pass on `net10.0`; 1335 / 1335 pass on `net472`.
- Language: 3600 / 3600 pass on `net10.0`; 3600 / 3600 pass on `net472`.

Identical to the Stage 3.4 baselines, confirming Stage 4.1 is
behaviour-inert.

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

- 2026-05-25: Stage 1.3 done. Added 7 paired `_At` diagnostic
  factories to `RazorDiagnosticFactory.cs`, each reusing its legacy
  counterpart's `RazorDiagnosticDescriptor` (same RZ ID, same
  message) but taking a `SourceLocation` and emitting a zero-width
  `SourceSpan` at the missing-token cursor position:
  - `CreateParsing_ExpectedEndOfBlockBeforeEOF_At` (RZ1006)
  - `CreateParsing_DirectiveMustHaveValue_At` (RZ1018)
  - `CreateParsing_UnfinishedTag_At` (RZ1024)
  - `CreateParsing_MissingEndTag_At` (RZ1025)
  - `CreateParsing_UnexpectedEndTag_At` (RZ1026)
  - `CreateParsing_ExpectedCloseBracketBeforeEOF_At` (RZ1027)
  - `CreateParsing_RazorCommentNotTerminated_At` (RZ1028)
  Total: 7 pairs covering 14 legacy call sites across
  `CSharpCodeParser.cs` (7 sites), `HtmlMarkupParser.cs` (5
  sites), and `TokenizerBackedParser.cs` (2 sites). Each pair
  shares its descriptor field -- the legacy method stays unchanged
  for the existing call sites; Stages 1.4 / 2.x / 3.x migrate call
  sites to the `_At` variants under the `UseEnhancedRecovery`
  flag.

  Inventory file
  `legacyTest/ParserRecoveryCorpus/parser-recovery-diagnostics-pairing.md`
  records the legacy / `_At` pairing table, the 5 categories of
  factories that were audited but intentionally NOT paired
  (already-narrow spans, value-validation diagnostics, found-but-
  unexpected-token diagnostics, etc.), and the rationale.

  RZ ID inventory recorded in the `Diagnostic IDs allocated` section
  above. No new RZ IDs allocated in this stage. Next free parser ID:
  RZ1046. Component RZ9xxx range is exhausted; new component
  diagnostics go in RZ10xxx (next free RZ10025).

  **Plan deviations:** none.

  Razor.slnf builds clean (0 warnings, 0 errors). Legacy tests
  1311 / 1311 unchanged; language tests 3600 / 3600 unchanged. Both
  TFMs. The pairing alone changes no behaviour -- no test
  regressions expected and none observed.
- 2026-05-25: Stage 1.4 done. Pilot migration of
  `TokenizerBackedParser.ParseRazorComment` (lines ~381-462 of
  `Legacy/TokenizerBackedParser.cs`) under the `UseEnhancedRecovery`
  flag. The end-of-comment `endStar` / `endTransition` handling is
  now split into two branches:
  - **Legacy branch** (`Context.Options.UseEnhancedRecovery == false`):
    byte-for-byte the prior code, kept so the existing baselines
    (`UnterminatedRazorComment.{stree,diag,cspans}.txt` and the four
    other unterminated-comment tests in `CSharpRazorCommentsTest.cs`
    and elsewhere) continue to pass unchanged.
  - **Enhanced branch** (`UseEnhancedRecovery == true`): calls the
    Stage 1.2 `Required(SyntaxKind.RazorCommentStar, ..., FollowSet.Empty, SyntaxKind.RazorComment)`
    and `Required(SyntaxKind.RazorCommentTransition, ...)` helpers,
    each producing a zero-width `MissingToken` at `CurrentStart` with
    the Stage 1.3 `RazorDiagnosticFactory.CreateParsing_RazorCommentNotTerminated_At`
    diagnostic attached. The branch passes `FollowSet.Empty` because
    `RazorCommentLiteral` has already consumed everything up to `*@`
    or EOF -- empirically `sync.Skipped` is always `null` and a
    `Debug.Assert` documents the invariant. When `endStar` is missing
    (the typical case for `@*`), `endTransition` is emitted as a
    plain `MissingToken` without re-attaching the same RZ1028
    diagnostic (a second copy would dedupe to one anyway via
    `RazorSyntaxTree.Diagnostics`' `HashSet<RazorDiagnostic>`).

  **Double-emit verification.** Confirmed by inspection of the
  pre-migration code at lines 382-401 of `TokenizerBackedParser.cs`:
  the `endStar` missing path called `SyntaxFactory.MissingToken(..., diagnostic)`
  AND `Context.ErrorSink.OnError(diagnostic)` on the same diagnostic
  instance (lines 386-387). The `endTransition` missing path also
  called `Context.ErrorSink.OnError(diagnostic)` plus assigned a
  token with the diagnostic (line 397) -- but that assignment was
  unconditionally overwritten on the next line (a pre-existing
  legacy bug: the diagnostic-bearing `endTransition` token was
  replaced by a plain `MissingToken` with no diagnostic). The
  user-visible diagnostic count in `UnterminatedRazorComment.diag.txt`
  is `1` because `RazorSyntaxTree.Diagnostics` (`RazorSyntaxTree.cs`
  lines 48-69) merges `ErrorSink` and tree-attached diagnostics
  through a `HashSet<RazorDiagnostic>` that dedupes by value
  equality. So the "double-emit" exists at the source-code level
  (the migration cleans it up) but was already invisible to
  end-users (the migration preserves that invariant).

  **Test added:** `ParseRazorComment_Unterminated_EnhancedRecovery`
  in `legacyTest/Legacy/CSharpRazorCommentsTest.cs` (chosen over
  `TokenizerBackedParserRecoveryTests.cs` for cohesion with the
  existing `UnterminatedRazorComment` legacy test). Asserts:
  - `RazorCommentBlockSyntax.EndCommentStar.IsMissing` with
    `SpanStart == 2`, `Span.Length == 0`.
  - `RazorCommentBlockSyntax.EndCommentTransition.IsMissing` at the
    same `(2, 0)` position.
  - Exactly one `RZ1028` diagnostic in `enhancedTree.Diagnostics`,
    at `AbsoluteIndex == 2`, `Length == 0` (the new zero-width
    placement at the missing-token cursor, vs. the legacy
    `(0, 2)` span covering the opening `@*`).
  - Plan exit-criterion sanity: `enhancedTree.Diagnostics.Length <= legacyTree.Diagnostics.Length`
    for the same input (both are 1, so the assertion is trivially
    satisfied and the stronger "exactly one" check passes too).

  The test uses `ParseDocument(..., configureParserOptions: b => b.UseEnhancedRecovery = true)`
  (not `ParseDocumentTest`) -- per the plan's note "in-memory
  assertions if the snapshot harness isn't a clean fit for an
  enhanced-mode test". No new `.stree.txt` / `.diag.txt` /
  `.cspans.txt` baselines were generated; the legacy
  `UnterminatedRazorComment.*` baselines remain untouched and still
  back the original legacy-mode test.

  **Plan deviations:**
  - The plan literal calls for "snapshot of the new shape under
    `Enhanced.json`". Per the plan's BaselineWriter decision (also
    in this state file) the project uses file-per-aspect text
    baselines instead of one combined JSON; the enhanced-mode test
    uses targeted in-memory assertions rather than a parallel
    `.enhanced.{stree,diag,cspans}.txt` set. The semantic
    intent (verify position + count + token-shape under the new
    flag) is preserved by the explicit `Assert.Equal(2, ...)` /
    `Assert.Single(rz1028)` / etc. assertions.
  - The `originatingLanguage` argument passed to `Required` is
    `SyntaxKind.RazorComment` (the comment block's own kind), not
    `MarkupBlock` or `CSharpCodeBlock`. Rationale: in practice
    `sync.Skipped` is always `null` for `ParseRazorComment` so the
    tag is never consumed, but tagging it as the comment's own
    kind reads naturally and won't conflict with anything.

  No new RZ IDs allocated (reuses RZ1028 via the `_At` factory
  from Stage 1.3).

  Razor.slnf builds clean (0 warnings, 0 errors). Legacy tests
  1312 / 1312 (1311 baseline + 1 new); language tests 3600 / 3600
  unchanged. Both TFMs.

  **Stage 1 complete.** All Stage 1 exit criteria (Synchronize +
  Required + diagnostic factories + green pilot under the flag)
  satisfied. Ready for handoff to fresh agent for Stage 2.
- 2026-05-26: Stage 2.1 done. Canonical Stage 2 migration of
  `CSharpCodeParser.ParseExplicitExpressionBody` (lines ~442-505 of
  `Legacy/CSharpCodeParser.cs`) under the `UseEnhancedRecovery` flag.
  The `if (!success)` branch (Balance failure) is now split:
  - **Legacy branch** (`UseEnhancedRecovery == false`): byte-for-byte
    the prior code -- `AcceptUntil(SyntaxKind.LessThan)` plus
    `Context.ErrorSink.OnError(CreateParsing_ExpectedEndOfBlockBeforeEOF(...))`
    with a 1-char span at `block.Start`. The existing legacy baselines
    (`UnclosedExplicitExpression.{stree,diag,cspans}.txt` and every
    other test that exercises an unclosed `@(...)`) keep passing
    unchanged.
  - **Enhanced branch** (`UseEnhancedRecovery == true`): calls the
    Stage 1.1 convenience overload of `Synchronize` with C#-side
    follow set `(RightParenthesis | LessThan | Transition)` and
    `originatingLanguage: SyntaxKind.CSharpCodeBlock`, then inserts
    the returned `SkippedContentSyntax?` into the expression
    builder after the marker / accepted-token flush. The closing
    `)` is then emitted via `SyntaxFactory.MissingToken(RightParenthesis, ...)`
    with `RazorDiagnosticFactory.CreateParsing_ExpectedEndOfBlockBeforeEOF_At(CurrentStart, ...)`
    attached -- a zero-width span at the first un-matched position
    (the follow token or EOF, not the opening `(`).

  Per Big Design Decision #4, the follow set is C#-side
  (`LessThan` / `Transition`, NOT the HTML-side `OpenAngle`). Stage
  4.2 will mechanically upgrade this `Synchronize` call to the full
  overload threading the caller's outer follow set; Stage 2.1 ships
  the convenience form.

  **Tests added** to
  `legacyTest/Legacy/ParserRecoveryCorpusSnapshotTests.cs` (two
  `[Fact]`s, in-memory assertions per the Stage 1.4 deviation):
  - `UnclosedExplicitExpression_EnhancedRecovery`: re-parses the
    corpus `UnclosedExplicitExpression.razor` with
    `UseEnhancedRecovery = true` and asserts the Stage 2.1 exit
    criteria:
    - `OpenParen` token present at position 10.
    - Exactly one `SkippedContentSyntax` inside the expression
      block, at `[11..18)`, content `"foo.Bar"`, with
      `OriginatingLanguage == CSharpCodeBlock`.
    - Every `CSharpExpressionLiteralSyntax` inside the expression
      block is zero-width (only the marker literal from
      `OutputTokensAsExpressionLiteral` remains; the legacy "fat
      `foo.Bar` literal" is gone).
    - `CloseParen` token is missing at position 18 (the `<` of
      `</p>`), `Span.Length == 0`.
    - Exactly one `RZ1006` diagnostic, at `AbsoluteIndex == 18`,
      `Length == 0` (the new zero-width placement at the
      missing-token cursor, vs the legacy 1-char span at
      `block.Start == 10`).
    - Zero `MarkupMiscAttributeContentSyntax` nodes (the recovered
      `</p>` is picked up by the markup parser as a real end-tag).
  - `EmptyExplicitExpression_EnhancedRecovery`: re-parses
    `EmptyExplicitExpression.razor`. `@()` is well-formed so
    `Balance` succeeds; the enhanced tree is identical to legacy
    (no diagnostics, no `SkippedContentSyntax`, real `)` token at
    position 2). This pins the invariant that the enhanced branch
    is dead code for the success path.

  **Diagnostic ID choice.** Used `CreateParsing_ExpectedEndOfBlockBeforeEOF_At`
  (RZ1006), matching the legacy diagnostic ID. The task prompt
  suggested `CreateParsing_ExpectedCloseBracketBeforeEOF_At`
  (RZ1027) -- both `_At` factories were paired in Stage 1.3 and
  are equally available -- but the Stage 2.1 plan literal says
  "the change is only that the *diagnostic span* shrinks from a
  1-char span starting at `block.Start` to a 1-char span at the
  cursor", which implies same diagnostic descriptor / same id /
  same args (`blockName`, `closeBlock`, `openBlock`). Preserving
  the RZ1006 id also keeps the user-facing message identical to
  legacy, so the only observable change to a downstream consumer
  is the diagnostic span. Stage 1.3's pairing inventory comment
  ("Used by Stage 2.1") is slightly stale; either factory would
  work, but RZ1006 minimises surface area. Recording this as a
  small deviation from the prompt (not from the plan).

  **Plan deviations:**
  - Used in-memory assertions rather than enhanced-mode parallel
    `.stree/.diag/.cspans` baselines (same rationale as Stage 1.4
    deviation #1 -- the dual-baseline shape isn't a clean fit when
    each test method targets one specific flag value).
  - Diagnostic-id choice (above): RZ1006 not RZ1027.
  - `EmptyExplicitExpression_EnhancedRecovery` is essentially a
    "legacy parity" test (Balance succeeds, enhanced branch never
    runs). Kept it per the plan's literal corpus enumeration so the
    invariant is pinned and future Stage 2.1-adjacent changes
    cannot regress the `@()` success path silently.

  No new RZ IDs allocated (reuses RZ1006 via the `_At` factory
  from Stage 1.3). Diagnostic ID inventory unchanged.

  **AcceptUntil(LessThan) audit** (Stage 2 exit-criteria check):
  `Select-String -Path .../CSharpCodeParser.cs -Pattern "AcceptUntil\(SyntaxKind\.LessThan"`
  reports 1 occurrence (line 471, inside the legacy branch of
  `ParseExplicitExpressionBody`). The enhanced branch added in
  Stage 2.1 contains zero `AcceptUntil(LessThan)` occurrences --
  satisfies the per-stage "enhanced branches must not contribute
  new occurrences" rule. The legacy branch's single occurrence
  remains and will be deleted in Stage 6.2.

  Razor.slnf builds clean (0 warnings, 0 errors). Legacy tests
  1314 / 1314 (1312 baseline + 2 new); language tests 3600 / 3600
  unchanged. Both TFMs.

- 2026-05-26: Stage 2.2 done. Migration of
  `CSharpCodeParser.ParseStatementBody` (lines ~768-836 of
  `Legacy/CSharpCodeParser.cs`) under the `UseEnhancedRecovery` flag.
  Two adjacent sites in the function are now split into legacy /
  enhanced branches:
  - **EOF diagnostic site** (was line ~785-790): legacy emits
    `CreateParsing_ExpectedEndOfBlockBeforeEOF` to `ErrorSink`
    with a 1-char span at `block.Start` (the `{` at position 1);
    enhanced skips this branch entirely (the diagnostic is now
    attached to the missing `RightBrace` token below).
  - **RightBrace consume / missing site** (was line ~804-814):
    legacy keeps the `if (At(RightBrace)) eat; else MissingToken`
    pair byte-for-byte; enhanced calls the Stage 1.2
    `Required(SyntaxKind.RightBrace, ...)` helper, which
    consumes the brace if present or emits
    `MissingToken(RightBrace)` with
    `CreateParsing_ExpectedEndOfBlockBeforeEOF_At(CurrentStart, ...)`
    attached (zero-width span at EOF, not 1-char at `block.Start`).
    `Required`'s sync follow-set is `FollowSet.Empty` because
    `ParseCodeBlock`'s loop invariant guarantees the cursor is
    at EOF or at `RightBrace` on exit -- a `Debug.Assert`
    documents that `sync.Skipped` is always `null` here.

  **Option B chosen** (signature of `ParseCodeBlock` unchanged).
  The plan offered two options:
  - **A**: add a `FollowSet outerFollow` parameter to
    `ParseCodeBlock` now, threaded `FollowSet.Empty` from all
    three callers with `[TODO Stage 4.2]` comments.
  - **B**: keep the signature unchanged, defer parameter threading
    to Stage 4.2.

  Picked **B**: the synchronization inside `ParseCodeBlock`'s
  `while` loop is functionally inert until Stage 2.3 migrates
  `ParseStandardStatement`'s panic (the current
  `ParseStatement` consumes-or-panics; there is no "failed
  statement parse" return-without-progress to synchronize on).
  Adding the parameter now would be dead plumbing whose only
  effect would be to ripple through the `Stage 4.2` diff with
  no behavioural meaning at any intermediate stage. The
  `[TODO Stage 4.2]` marker lives in the new enhanced branch's
  comment block instead.

  **Test added** to
  `legacyTest/Legacy/ParserRecoveryCorpusSnapshotTests.cs`:
  `UnclosedCodeBlock_EnhancedRecovery` re-parses the corpus
  `UnclosedCodeBlock.razor` (created in Stage 0.1) with
  `UseEnhancedRecovery = true` and asserts the Stage 2.2 exit
  criteria:
  - `OpenBrace` token present at position 1 (the `{`).
  - `CloseBrace` token is missing at the EOF position
    (`source.Length == 69`), `Span.Length == 0` -- down from
    the legacy missing-token's position-69 placement but
    paired-with a diagnostic at the wrong location.
  - Exactly one `RZ1006` diagnostic, at `AbsoluteIndex == source.Length`,
    `Length == 0` (the new zero-width placement at EOF, vs the
    legacy 1-char span at position 1).
  - No non-empty `CSharpStatementLiteralSyntax` inside the
    code block overlaps the `<p>` markup at position 17 (the
    trailing zero-width marker literal at `[69..69)` is
    permitted via the `Width == 0` guard).
  - Zero `MarkupMiscAttributeContentSyntax` nodes (the recovered
    markup is parsed as a real `MarkupBlock` -- in this corpus
    case the legacy parser already does this, so the assertion
    just pins the property under enhanced mode too).

  Before / after diagnostic spans for `UnclosedCodeBlock`:
  - **Legacy** (unchanged baseline): `RZ1006` at
    `(1,2)` = AbsoluteIndex 1, Length 1 (covers the `{`).
  - **Enhanced**: `RZ1006` at AbsoluteIndex 69 (EOF), Length 0.

  In-memory assertions are used (matching the Stage 1.4 / 2.1
  deviation about not generating parallel
  `.enhanced.{stree,diag,cspans}.txt` baselines for
  enhanced-mode-only tests). The legacy
  `UnclosedCodeBlock.{stree,diag,cspans}.txt` baselines remain
  untouched.

  **Sites intentionally NOT migrated in Stage 2.2:**
  - The nested verbatim-block site in `ParseStatement`'s
    `case SyntaxKind.LeftBrace` (lines ~945-957 of
    `Legacy/CSharpCodeParser.cs`) has the same shape
    (`ParseCodeBlock` followed by an `if (EndOfFile)
    ErrorSink.OnError(...)` + `Assert(RightBrace)`). It belongs
    to Stage 2.3's `ParseStandardStatement` family and will be
    migrated there.
  - The other `MissingToken(SyntaxKind.RightBrace)` at line
    ~2120 is inside `ParseExtensibleDirective`-style code and
    belongs to Stage 2.5 (directive parsers).

  No new RZ IDs allocated (reuses RZ1006 via the `_At` factory
  from Stage 1.3). Diagnostic ID inventory unchanged.

  **`AcceptUntil(LessThan)` audit** (Stage 2 exit-criteria
  check): unchanged from Stage 2.1 -- 4 occurrences total (lines
  501, 636, 1184, 1198). Stage 2.2 added no new occurrences and
  removed none (it only touches the `RightBrace` recovery, which
  never used `AcceptUntil(LessThan)`).

  Razor.slnf builds clean (0 warnings, 0 errors). Legacy tests
  1315 / 1315 (1314 baseline + 1 new); language tests 3600 / 3600
  unchanged. Both TFMs.

- 2026-05-26: Stage 2.3 done. Canonical migration of
  `CSharpCodeParser.ParseStandardStatement`'s panic-else branch
  (lines ~1214-1220 of `Legacy/CSharpCodeParser.cs`) and its inner
  `TryBalanceBlock` recovery (was line ~1232) under the
  `UseEnhancedRecovery` flag. This is the `fat literal` site the
  plan calls out as the canonical Stage 2 producer.

  - **Panic-else** (`ParseStandardStatement`'s outer `while` loop):
    legacy `_tokenizer.Reset(bookmark); NextToken();
    AcceptUntil(LessThan, LeftBrace, RightBrace)` is preserved
    byte-for-byte. The enhanced branch (a) emits the new RZ1046
    `Parsing_UnexpectedTokenInStatement` diagnostic at zero width
    via `ErrorSink.OnError` (the diagnostic is purely positional;
    there is no missing token to attach it to), (b) flushes any
    prior-iteration accepted tokens via
    `AcceptMarkerTokenIfNecessary` + `OutputTokensAsStatementLiteral`
    so the pre-recovery literal boundary is precise, (c) calls
    `Synchronize(new FollowSet(Semicolon, RightBrace, Transition,
    LessThan), originatingLanguage: CSharpCodeBlock)` and (d) adds
    the resulting `SkippedContentSyntax` to the builder.

  - **`TryBalanceBlock`** (inner local function, hot for `@{ var x =
    (foo;` style inputs): legacy `AcceptUntil(LessThan, RightBrace)`
    is preserved. The enhanced branch runs `Synchronize(new
    FollowSet(LessThan, RightBrace), CSharpCodeBlock)`, flushes the
    prior literal, and adds `SkippedContentSyntax`. The pre-existing
    RZ1027 (`Parsing_ExpectedCloseBracketBeforeEOF`) emitted by
    `Balance` itself is preserved unchanged -- its narrowing belongs
    to whichever stage owns the construct's open-bracket emission, not
    Stage 2.3.

  **Per Big Design Decision #4** the follow sets are C#-side
  (`LessThan`, not the HTML-side `OpenAngle`; `Semicolon` /
  `RightBrace` / `Transition` are shared). Stage 4.2 will
  mechanically upgrade both `Synchronize` calls to the full overload
  threading the caller's outer follow set.

  **New diagnostic factory:**
  - Resource string `ParseError_UnexpectedTokenInStatement` added to
    `Language/Resources.resx` with message `An unexpected token was
    encountered in a CSharp statement: '{0}'.`.
  - `Parsing_UnexpectedTokenInStatement` descriptor at `RZ1046`,
    severity Error.
  - `CreateParsing_UnexpectedTokenInStatement_At(SourceLocation,
    string token)` factory emits a zero-width `SourceSpan` at the
    cursor per the `_At` convention from Stage 1.3. There is no
    non-`_At` paired variant: the diagnostic was introduced new in
    Stage 2.3 and only ever has the narrow span.

  No `.xlf` files exist for `Microsoft.CodeAnalysis.Razor.Compiler`
  (`Microsoft.CodeAnalysis.Razor.Compiler.csproj` only registers
  `EmbeddedResource` `.resx` updates; the strongly-typed
  `Resources` accessor is SDK-generated). No `UpdateXlf` target
  invocation was needed; the resource designer regenerates at build.

  **Tests added** to
  `legacyTest/Legacy/ParserRecoveryCorpusSnapshotTests.cs` (three
  `[Fact]`s, in-memory assertions per the Stage 1.4 deviation):
  - `MidStatementGarbage_EnhancedRecovery` -- parity test for the
    Stage 0.1 corpus file `MidStatementGarbage.razor`.
  - `UnclosedIfParen_EnhancedRecovery` -- parity test for
    `UnclosedIfParen.razor`.
  - `UnclosedParenInsideCodeBlock_EnhancedRecovery` -- synthetic
    test on `@{ var x = (foo; }` that actually exercises
    `TryBalanceBlock`'s enhanced recovery (the corpus files don't;
    see deviation below).

  **Plan deviations (significant, documented):**

  1. **Empirical: the corpus files don't exercise Stage 2.3 paths.**
     The plan literal calls for `MidStatementGarbage` and
     `UnclosedIfParen` to assert `garbage absorbed as
     SkippedContentSyntax, no CSharpStatementLiteral wraps the
     recovered region`. Empirically:
     - `MidStatementGarbage` (`@{ var x = ?? 1; <p>...</p> }`) is
       fully well-formed at the lexer/parser level: `??` is a valid
       C# `NullCoalesce` token, `<p>` triggers
       `ParseStatement`'s markup-transition handoff (line ~916 of
       `CSharpCodeParser.cs`), and `}` closes the block. The
       legacy parse produces zero diagnostics and the enhanced parse
       is identical.
     - `UnclosedIfParen` (`@if(foo bar ...`) is parsed via
       `ParseImplicitExpression` + `ParseMethodCallOrArrayIndex`
       (Stage 2.6 territory), NOT `ParseStandardStatement`. The
       recovery site at line 636 (`AcceptUntil(LessThan)` in
       `ParseMethodCallOrArrayIndex`) is owned by Stage 2.6 and is
       unchanged by this stage.

     **Structural finding**: `ParseStandardStatement`'s panic-else
     is effectively unreachable from typical input. `ReadWhile`'s
     stop set (Semicolon, RazorCommentTransition, Transition,
     LeftBrace, LeftParenthesis, LeftBracket, RightBrace, Keyword)
     is identical to the kinds handled by the function's if-else
     chain; `ParseStatement` (the caller) already dispatches
     markup-transition (`LessThan` / `Transition+:`) to the HTML
     parser before reaching `ParseStandardStatement`. The panic
     would only fire on pathological tokenizer states. The
     migration is still landed for forward-compatibility and to keep
     the code path consistent with the rest of Stage 2.

     `TryBalanceBlock`'s recovery IS reachable -- the synthetic
     `UnclosedParenInsideCodeBlock_EnhancedRecovery` test exercises
     it directly (`@{ var x = (foo; }`).

  2. **In-memory assertions** instead of parallel
     `.enhanced.{stree,diag,cspans}.txt` baselines (same rationale
     as Stage 1.4 / 2.1 / 2.2).

  3. **Synthetic test added** beyond the plan literal's two corpus
     tests, to give Stage 2.3's actual behaviour-changing code path
     real test coverage. The corpus parity tests are kept (as the
     plan literal directs) but they pin the invariant that Stage
     2.3 doesn't regress these inputs rather than demonstrating the
     new shape -- which the synthetic test does.

  4. **Diagnostic attachment is via `ErrorSink`, not the missing
     token.** RZ1046 fires for an *unexpected* token (not a
     *missing* one), so there is no `MissingToken(kind)` to attach
     it to. The `_At` factory still produces a zero-width
     `SourceSpan`; it is just placed in `ErrorSink` rather than
     on a token. This matches Stage 1.4's pre-existing pattern for
     diagnostics that don't have a natural token home. The new
     RZ1046 will therefore appear in `RazorSyntaxTree.Diagnostics`
     via the `ErrorSink` merge path, not the tree-attached path.

  **`AcceptUntil(LessThan)` audit** (Stage 2 exit-criteria check):
  `Select-String -Path ... -Pattern "AcceptUntil\(SyntaxKind\.LessThan"`
  reports 4 occurrences -- the same count as before Stage 2.3
  started. Two old occurrences (the panic-else at line ~1218 and
  `TryBalanceBlock`'s recovery at line ~1232) are now inside the
  `else` of their respective `UseEnhancedRecovery` guards
  (lines 1273 and 1318 after the migration). Lines 501 (Stage 2.1
  legacy) and 636 (Stage 2.6) are unchanged. The enhanced branches
  added by Stage 2.3 contain zero `AcceptUntil(LessThan)`
  occurrences -- satisfies the per-stage `enhanced branches must
  not contribute new occurrences` rule.

  Razor.slnf builds clean (0 warnings, 0 errors). Legacy tests
  1318 / 1318 (1315 baseline + 3 new); language tests 3600 / 3600
  unchanged. Both TFMs.
- 2026-05-27: Stage 2.4 done. Migration of
  `CSharpCodeParser.TryParseCondition` (lines ~2369-2385 of
  `Legacy/CSharpCodeParser.cs`) under the `UseEnhancedRecovery` flag.
  This is the single migration site that covers the entire C# control-flow
  keyword-block family: `@if`, `@for`, `@foreach`, `@while`,
  `@switch`, `@lock`, `@try`/`catch`, `@do`/`while`, `@using`.
  All of those frames route their `(condition)` syntax through
  `TryParseCondition`; the body `{...}` block is already migrated by
  Stage 2.2's `ParseStatementBody`.

  - **TryParseCondition panic site**: legacy
    `if (!complete) AcceptUntil(SyntaxKind.NewLine)` is preserved
    byte-for-byte in the `else` branch. The enhanced branch runs
    `Synchronize(new FollowSet(NewLine, RightBrace, LeftBrace),
    CSharpCodeBlock)`, flushes the pending accepted `(` as a
    precise `CSharpStatementLiteral` via `AcceptMarkerTokenIfNecessary`
    + `OutputTokensAsStatementLiteral`, then adds the
    `SkippedContentSyntax` (when non-null) to the builder. Pattern
    mirrors Stage 2.3's `TryBalanceBlock` enhanced branch.

  **Per Big Design Decision #4** the follow set is C#-side
  (`NewLine`, `RightBrace`, `LeftBrace` are all shared structural
  kinds). The follow set choice:
  - `NewLine` preserves the legacy panic boundary so single-line
    control-flow inputs sync at end of line;
  - `RightBrace` lets `@{ for(... } ` recover at the enclosing
    code block's outer `}`;
  - `LeftBrace` lets the body of `@if(foo bar { ... }` still be
    parsed even when the condition is malformed.

  Stage 4.2 will mechanically upgrade this call to thread the caller's
  outer follow set per BDD #4.

  **No new diagnostic IDs allocated.** `Balance` itself already
  emits the pre-existing RZ1027 (`Parsing_ExpectedCloseBracketBeforeEOF`)
  at the opening `(`; narrowing that diagnostic belongs to whichever
  stage owns the open-bracket emission, not Stage 2.4. The enhanced
  branch introduces no zero-width diagnostic of its own (unlike Stage
  2.3's RZ1046).

  **Corpus additions:**
  - `UnclosedForeach.razor` (46 bytes): `@foreach(var x in items\n{...}`
    exercises `ParseConditionalBlock` -> `TryParseCondition`.
  - `UnclosedSwitch.razor` (82 bytes): `@switch(x\n{...}` same path
    via `CSharpSyntaxKind.SwitchKeyword`.
  Both have legacy `.stree.txt` / `.diag.txt` / `.cspans.txt`
  baselines generated via `/p:GenerateBaselines=true` and pin the
  legacy fat-literal absorption of the malformed condition body.

  **Tests added** to
  `legacyTest/Legacy/ParserRecoveryCorpusSnapshotTests.cs` (five new
  `[Fact]`s, in-memory assertions per the Stage 1.4 / 2.1 / 2.2 / 2.3
  deviation):
  - `UnclosedForeach_EnhancedRecovery` -- corpus
    `UnclosedForeach.razor` via `ParseConditionalBlock`.
  - `UnclosedSwitch_EnhancedRecovery` -- corpus
    `UnclosedSwitch.razor` via `ParseConditionalBlock`.
  - `UnclosedCatchParen_EnhancedRecovery` -- synthetic
    `@try { } catch(ex bad { }` via `ParseFilterableCatchBlock`.
  - `UnclosedUsingParen_EnhancedRecovery` -- synthetic
    `@using(var x = foo bar { }` via `ParseUsingStatement`.
  - `UnclosedWhileInDoLoop_EnhancedRecovery` -- synthetic
    `@do { } while(foo bar` via `ParseWhileClause`.

  Plus 2 new legacy snapshot `[Fact]`s (`UnclosedForeach`,
  `UnclosedSwitch`) pinning the legacy fat-literal behaviour via
  the new baselines.

  **Existing test updated:** `UnclosedIfParen_EnhancedRecovery`
  (added in Stage 2.3 as a `parity` test). Stage 2.3's comment
  claimed `@if(foo bar` routed through
  `ParseMethodCallOrArrayIndex` (`Stage 2.6 territory`), which was
  empirically incorrect: `if` IS dispatched to `ParseIfStatement`
  -> `ParseConditionalBlock` -> `TryParseCondition`. The legacy
  `AcceptUntil(NewLine)` was flattening the entire structure into
  a single fat `CSharpStatementLiteral`. Stage 2.4's migration
  produces a real `SkippedContentSyntax` for `foo bar`; the
  updated assertions verify that shape.

  **Sites intentionally NOT migrated in Stage 2.4:**
  - The inner `switch` branch of `ParseStandardStatement`
    (line ~1202 of `Legacy/CSharpCodeParser.cs`, the
    `AcceptUntil(SyntaxKind.LeftBrace)` site marked with a legacy
    `// TODO: how do we do error recovery at this point?` comment).
    The plan lists this site, but a naive `Synchronize(LeftBrace, ...)`
    swap would regress the well-formed `switch (foo) { ... }` case:
    the legacy `AcceptUntil(LeftBrace)` *accepts* the intermediate
    tokens (`(foo) ` -- real C# tokens) as a statement literal,
    which is the correct behaviour for that input. `Synchronize`
    would instead skip those tokens into `SkippedContent`, losing
    them from the C# emit stream. A proper migration here requires
    a structural refactor (use `TryParseCondition` for the `(...)`
    part, then `Required(LeftBrace, ...)` + `TryBalanceBlock` for
    the body) which is outside the literal Stage 2.4 recipe (`each
    [frame's] condition-parsing uses Required(LeftParen, ...) +
    Balance + Required(RightParen, ...); body parsing uses
    Required(LeftBrace, ...)`) and would change the well-formed
    case's literal-stream content. Deferred -- the canonical
    `@switch (x) {` top-level path goes through
    `ParseConditionalBlock` (now covered by `TryParseCondition`'s
    migration), so this inner fallback is the rare-nested case only.

  - `ParseCaseStatement` (line ~2435) -- no
    `AcceptUntil` / panic recovery exists in this function;
    it uses `Balance` for brackets but with `BalancingModes.None`
    (no recovery contract). Nothing to migrate.

  - `ParseUsingDeclaration` (line ~2737) -- this is a directive
    parser (`@using Foo.Bar;`), not a statement frame. The plan
    explicitly lists it under Stage 2.5 (`ParseUsingDeclaration`
    is in Stage 2.5's directive list). No `AcceptUntil` to migrate
    at this layer; deferred to Stage 2.5.

  - `ParseConditionalBlock` / `ParseIfStatement` /
    `ParseAfterIfClause` / `ParseElseClause` / `ParseTryStatement`
    / `ParseAfterTryClause` / `ParseFilterableCatchBlock` /
    `ParseDoStatement` / `ParseWhileClause` / `ParseUsingKeyword`
    / `ParseUsingStatement` -- these methods have no
    `AcceptUntil` / panic recovery of their own. They all delegate
    `(condition)` parsing to `TryParseCondition` and `{ body }`
    parsing to `ParseExpectedCodeBlock` -> `ParseStatement` ->
    `ParseStatementBody` (already migrated in Stage 2.2). Migrating
    `TryParseCondition` therefore mechanically covers the entire
    family. No per-method enhanced branches required.

  **`AcceptUntil(LessThan)` audit** (Stage 2 exit-criteria check):
  unchanged from Stage 2.3 -- 4 occurrences total (lines 471 legacy,
  636 Stage 2.6, 1273 / 1287 Stage 2.3 legacy paths). Stage 2.4 added
  no new occurrences and removed none. The new enhanced branch in
  `TryParseCondition` contains zero `AcceptUntil(LessThan)` /
  `AcceptUntil(NewLine)` occurrences -- satisfies the per-stage
  `enhanced branches must not contribute new occurrences` rule.

  **Plan deviations:**
  - Inner `switch` branch of `ParseStandardStatement` deferred
    (see `Sites intentionally NOT migrated` above for rationale).
    The literal Stage 2.4 plan text listed it; the deferral is
    documented here so a future maintenance pass (Stage 6.2 cleanup
    or a follow-up structural refactor) can pick it up. The legacy
    branch is untouched.
  - `ParseUsingDeclaration` deferred to Stage 2.5 (where the plan
    also explicitly lists it). The Stage 2.4 prompt mentioned it in
    the migration list, but per the plan's stage ownership it belongs
    to the directive-parser migration of Stage 2.5.
  - In-memory assertions for new enhanced tests (matching the Stage
    1.4 / 2.1 / 2.2 / 2.3 deviation): no parallel
    `.enhanced.{stree,diag,cspans}.txt` baselines generated; the
    test asserts the Stage 2.4 exit criteria directly.

  Razor.slnf builds clean (0 warnings, 0 errors). Legacy tests
  1325 / 1325 (1318 baseline + 2 new legacy corpus + 5 new enhanced);
  language tests 3600 / 3600 unchanged. Both TFMs (net10.0 and net472).
- 2026-05-28: Stage 2.5 done. Migration of the Razor directive parsers
  in `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Legacy/CSharpCodeParser.cs`
  to the `Required` / `Synchronize` machinery under the
  `UseEnhancedRecovery` flag.

  **What landed:**

  - `RecoveryFollowSets.CSharpDirectiveTrailing` named constant
    (`{ NewLine, RightBrace }`, C#-side kinds per Big Design Decision
    #4). `LessThan` was deliberately excluded after experimenting:
    including it would cause sync to stop at a stray `<` on the
    directive line, leaving the bad `<` to leak to the outer markup
    parser as a fake `MarkupStartTag` + `MarkupMiscAttributeContent`
    (the very pollution Stage 2.5 is trying to eliminate). The
    trade-off: a directive on the same line as a real markup tag
    (`@inherits System<p>after</p>`, no intervening newline) would
    have its trailing `<p>after</p>` absorbed into SkippedContent.
    This is an acceptable corner-case loss because directives are
    line-terminated in practice, and the dominant pattern is
    newline-separated markup after the directive.

  - `BuildBailedDirective(SyntaxKind missingKind)` local function added
    to `ParseExtensibleDirective` (right after the existing
    `BuildDirective` local function). Under enhanced mode it calls
    `Synchronize(CSharpDirectiveTrailing, originatingLanguage: CSharpCodeBlock)`,
    flushes pending tokens via `AcceptMarkerTokenIfNecessary` +
    `OutputTokensAsStatementLiteral`, then appends `sync.Skipped` to
    `directiveBuilder` before delegating to `BuildDirective`. The
    helper returns `RazorDirectiveSyntax` (rather than calling
    `builder.Add` directly) because `builder` is an `in` parameter
    and C# does not permit capturing `ref`/`in`/`out` parameters in
    nested functions.

  - All 11 early-bail sites in `ParseExtensibleDirective` migrated
    from `builder.Add(BuildDirective(K)); return;` to
    `builder.Add(BuildBailedDirective(K)); return;`. Diagnostics
    covered: `DirectiveTokensMustBeSeparated`,
    `UnexpectedEOFAfterDirective`, `DirectiveExpectsTypeName` (RZ1013),
    `DirectiveExpectsNamespace` (RZ1015), `DirectiveExpectsIdentifier`,
    `DirectiveExpectsQuotedStringLiteral`,
    `DirectiveExpectsBooleanLiteral`,
    `DirectiveExpectsCSharpAttribute`,
    `GenericTypeParameterIdentifierMismatch`, `UnexpectedIdentifier`,
    `DirectiveExpectsIdentifierOrExpression`. The pre-existing
    diagnostic factories and their spans are unchanged; only the
    cursor advancement and recovered tree shape differ.

  - Trailing-literal site at `Parsing_UnexpectedDirectiveLiteral`
    (the SingleLine directive's trailing-content error) wrapped with
    the same enhanced-mode `Synchronize` pattern (inline, not via
    `BuildBailedDirective`).

  - `ParseUsingDeclaration` gained an enhanced-mode `Synchronize`
    block between `builder.Add(SyntaxFactory.RazorUsingDirective(...))`
    and `CaptureWhitespaceToEndOfLine()`. Skipped content is added to
    the OUTER `builder` (not `directiveBuilder`, which is asserted
    empty) and appears as a sibling AFTER the `RazorUsingDirective`.
    No new diagnostic is emitted (the legacy path is also silent for
    `@using foo bar`).

  **Sites intentionally NOT migrated in Stage 2.5:**

  - `ParseTagHelperDirective` (and its `ParseTagHelperPrefixDirective`
    / `ParseAddTagHelperDirective` / `ParseRemoveTagHelperDirective`
    front doors) -- the `AcceptUntil(SyntaxKind.NewLine)` at
    `CSharpCodeParser.cs:1541` is value-collection (it builds the
    directive's value string up to the line terminator), NOT panic
    recovery. Replacing it with `Synchronize` would change
    well-formed-input behavior by absorbing the entire value content
    into SkippedContent. The diagnostic for missing-value at
    `Parsing_DirectiveMustHaveValue` already runs at a narrow span
    (1 char); no recovery cleanup is required.

  - `ParseUsingStatement` / `ParseUsingKeyword` -- already migrated
    in Stage 2.4 via `TryParseCondition`. The Stage 2.4 enhanced
    branch handles the `using (` Balance-fails path; nothing further
    to add here.

  **Deviations from the plan:**

  - No `MalformedExtensible.razor` corpus file. The corpus test
    infrastructure (`ParseCorpusFile`) doesn't pass `DirectiveDescriptor`s
    to `ParseDocument`, and extensible directives like `@inherits`
    require their descriptor to be registered to take the
    `ParseExtensibleDirective` path. The `MalformedInherits_EnhancedRecovery`
    synthetic test uses inline source plus
    `directives: [InheritsDirective.Directive]` instead (matching
    the Stage 2.4 `UnclosedCatchParen_EnhancedRecovery` pattern).

  - In-memory assertions for new enhanced tests (matching the Stage
    1.4 / 2.1 / 2.2 / 2.3 / 2.4 deviation): no parallel
    `.enhanced.{stree,diag,cspans}.txt` baselines generated; the
    test asserts the Stage 2.5 exit criteria directly.

  **No new RZ IDs allocated** in Stage 2.5. All migrated bail sites
  reuse the existing diagnostic factories with their existing narrow
  spans. The redesign target for Stage 2.5 is purely about
  recovered-tree shape (absorb trailing garbage as `SkippedContentSyntax`
  inside the directive instead of leaking as `MarkupTextLiteral` /
  `MarkupMiscAttributeContent` on the outer markup side), not about
  changing error reporting.

  **Risk verified:** A first cut had `BailWithSync` as a `void`
  local function calling `builder.Add(...)` internally, which failed
  to compile (CS1628: cannot use `in` parameter inside local
  function). The fix was to make it return the directive and have
  each caller do `builder.Add(BuildBailedDirective(K))`. The
  identifier was also renamed to `BuildBailedDirective` to reflect
  the return-based shape. End-to-end metrics (the cross-cutting
  `RecoveryDelta` aggregation) are deferred to Stage 5.

  Razor.slnf builds clean (0 warnings, 0 errors). Legacy tests
  1328 / 1328 (1325 baseline + 1 new legacy corpus + 2 new enhanced);
  language tests 3600 / 3600 unchanged. Both TFMs (net10.0 and net472).
- 2026-05-29: Stage 2.6 done. Migration of the implicit-expression
  method-call / array-index Balance failure recovery in
  `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Legacy/CSharpCodeParser.cs`
  (`ParseMethodCallOrArrayIndex`) to the `Required` / `Synchronize`
  machinery under the `UseEnhancedRecovery` flag. This is the final
  Stage 2 sub-stage; Stage 2 is now complete.

  **What landed:**

  - `RecoveryFollowSets.CSharpImplicitExpressionTrailing` named
    constant (`{ LessThan, NewLine, Whitespace }`, C#-side kinds per
    Big Design Decision #4). Models the natural end of an implicit
    expression: markup follow-up (`<`), end-of-line, or trailing
    whitespace before non-expression text.

  - `ParseMethodCallOrArrayIndex` (around line 611 of
    `Legacy/CSharpCodeParser.cs`) split into enhanced / legacy
    branches on `Context.Options.UseEnhancedRecovery`. The Balance
    call gains `BalancingModes.NoErrorOnFailure` conditionally in
    the enhanced branch to suppress Balance's own wide RZ1027
    (1-char span at the opening bracket); the enhanced path emits
    its own narrow zero-width RZ1027 via the `_At` factory attached
    to the MissingToken returned by `Required`. The legacy branch
    keeps Balance with its default error-emission and the
    pre-existing `AcceptUntil(LessThan)` fat-literal absorb plus
    the `At(right) ? AcceptAndMoveNext() : nothing` open-or-drop
    behaviour.

  - Enhanced branch flow: `Synchronize(CSharpImplicitExpressionTrailing,
    originatingLanguage: CSharpCodeBlock)` to absorb intra-call
    garbage as a `SkippedContentSyntax`, then `OutputTokensAsExpressionLiteral`
    flush, then `Required(right, ExpectedCloseBracketBeforeEOF_At,
    recovery: CSharpImplicitExpressionTrailing,
    originatingLanguage: CSharpCodeBlock)` to either consume the
    real closing bracket or emit a zero-width MissingToken carrying
    the narrow RZ1027 at the current cursor.

  - Corpus added: `legacyTest/ParserRecoveryCorpus/UnclosedMethodCallInImplicit.razor`
    -- the canonical `<p>@foo.Bar(baz</p><div>after</div>` shape
    (37 bytes, CRLF) that exercises the implicit-expression Balance
    failure followed by markup follow-set tokens.

  - Tests added: `UnclosedMethodCallInImplicit` [Fact] (legacy
    snapshot binding the pre-migration fat-literal behaviour via
    new `.stree.txt` / `.diag.txt` / `.cspans.txt` baselines) and
    `UnclosedMethodCallInImplicit_EnhancedRecovery` [Fact]
    (in-memory assertions of the enhanced shape: real
    `SkippedContentSyntax` for `baz`, zero-width MissingToken with
    narrow RZ1027 for the closing `)`, no leakage of `</p>` into
    the expression).

  **Sites intentionally NOT migrated in Stage 2.6:**

  - The Stage 2.3 statement-family sites (`TryBalanceBlock` at
    line 1423 and the explicit-expression fallback at line 1378)
    remain in their Stage 2.3-shipped state. They are inside
    `else` branches of `UseEnhancedRecovery` guards; no further
    work is needed.

  - The Stage 2.1 explicit-expression site at line 501 -- already
    inside an `else` branch from Stage 2.1's own migration.

  **Plan deviations:**

  - `Required`'s `recovery` parameter uses `CSharpImplicitExpressionTrailing`
    (the same follow set as the outer `Synchronize`) rather than
    `FollowSet.Empty`. Using `FollowSet.Empty` would cause
    `Synchronize` (invoked by `Required` on the missing-token path)
    to consume tokens all the way to EOF because
    `FollowSet.Empty.Contains(_)` is always false. Reusing the
    outer follow set is safe because: (a) on the success path
    `Required` consumes and never syncs; (b) on the missing path
    the cursor is already at a follow token (placed there by the
    outer `Synchronize`), so the secondary sync breaks immediately
    with `Skipped = null`.

  - MissingToken handling bypasses `Accept(SyntaxToken)` and writes
    directly to `TokenBuilder.Add(missingToken)`. The `Accept`
    helper copies the token's `GetDiagnostics()` into `ErrorSink`,
    which would double-emit the narrow RZ1027 (once on the token,
    once on the sink). Writing to `TokenBuilder` directly preserves
    the Stage 1.4 "diagnostic on missing token only" contract.

  - In-memory assertions for the new enhanced test (matching the
    Stage 1.4 / 2.1 / 2.2 / 2.3 / 2.4 / 2.5 deviation): no parallel
    `.enhanced.{stree,diag,cspans}.txt` baselines generated; the
    test asserts the Stage 2.6 exit criteria directly.

  **No new RZ IDs allocated** in Stage 2.6. The enhanced branch
  reuses `CreateParsing_ExpectedCloseBracketBeforeEOF_At` (RZ1027)
  -- the `_At` factory that Stage 1.3 already paired for narrow,
  zero-width emission. The next free `RZ1xxx` parser-recovery ID
  remains **RZ1047**.

  **`AcceptUntil(LessThan)` audit** (Stage 2 final exit-criteria
  check): 4 occurrences total in `CSharpCodeParser.cs` (lines 501,
  699, 1378, 1423). All four are now inside `else` branches of
  `UseEnhancedRecovery` guards -- verified by inspection of each
  site. Stage 2 exit criterion is met: every enhanced branch is
  `AcceptUntil(LessThan)`-free. The remaining 4 legacy occurrences
  will be deleted by Stage 6.2 cleanup once the
  `UseEnhancedRecovery` flag is removed.

  Razor.slnf builds clean (0 warnings, 0 errors). Legacy tests
  1330 / 1330 (1328 baseline + 1 new legacy corpus + 1 new enhanced);
  language tests 3600 / 3600 unchanged. Both TFMs (net10.0 and net472).

- **Stage 3.1 (HtmlMarkupParser tag-name / close-angle migration)**:
  First HTML-side migration. Migrated two sites in
  HtmlMarkupParser.ParseStartTag and two sites in
  HtmlMarkupParser.ParseEndTag (tag-name slot + close-angle slot in
  each) to use Required (plus implicit Synchronize) under the
  Context.Options.UseEnhancedRecovery flag. Legacy paths kept
  byte-for-byte; new behaviour is gated by the flag.

  - **ParseStartTag tag-name slot** (HtmlMarkupParser.cs ~line 660):
    enhanced path calls Required(SyntaxKind.Text, ...,
    Parsing_TagNameExpected_At(CurrentStart), HtmlTagRecovery,
    originatingLanguage: SyntaxKind.MarkupBlock). Any returned
    skipped content is inserted into the attribute builder as
    `SkippedContentSyntax` (positionally between the open angle and
    the first real attribute) so source positions remain monotonic.
    Legacy emitted a bare MissingToken(Text) with no diagnostic.

  - **ParseStartTag close-angle slot** (HtmlMarkupParser.cs ~line
    720, only in MarkupInCodeBlock mode): enhanced path calls
    Required(SyntaxKind.CloseAngle, ...,
    Parsing_UnfinishedTag_At(CurrentStart, tagName), HtmlTagRecovery,
    originatingLanguage: SyntaxKind.MarkupBlock). The
    `Debug.Assert(skipped is null, ...)` invariant holds because
    ParseAttributes has already absorbed everything up to a
    tag-recovery boundary by this point (EOF, CloseAngle, or
    OpenAngle). Legacy emitted a wide-span `Parsing_UnfinishedTag`
    via `ErrorSink.OnError` covering the tag name (or
    `[tagStart, +1)` if the tag name is empty); enhanced emits the
    narrow _At variant on the MissingToken instead.

  - **ParseEndTag tag-name slot** (HtmlMarkupParser.cs ~line 968):
    enhanced path mirrors ParseStartTag -- Required(SyntaxKind.Text,
    ..., Parsing_TagNameExpected_At, HtmlTagRecovery,
    SyntaxKind.MarkupBlock). Skipped content goes into
    miscAttributeBuilder (the misc-attribute slot for end tags,
    inserted before the AcceptWhile(Whitespace) to preserve
    positions).

  - **ParseEndTag close-angle slot** (HtmlMarkupParser.cs ~line
    1029): enhanced path calls Required(SyntaxKind.CloseAngle, ...,
    Parsing_UnfinishedTag_At(CurrentStart, tagName), HtmlTagRecovery,
    SyntaxKind.MarkupBlock). Same Debug.Assert(skipped is null)
    invariant. Note this is a **broader migration** than the legacy
    behaviour for end tags: legacy only emitted
    `Parsing_UnfinishedTag` for start tags in MarkupInCodeBlock
    mode; enhanced now emits the narrow _At variant on the missing
    close angle for end tags in **all** modes (plain markup and
    MarkupInCodeBlock). This is consistent with Stage 3.1's
    "tighter recovery" goal: it produces a tree where every missing
    token carries its narrow diagnostic, rather than dropping a bare
    MissingToken(CloseAngle) with no diagnostic. Verified by the
    3600 / 3600 language tests passing under flag-off (legacy
    behaviour unchanged); under flag-on, no existing baselines
    needed to change because no existing enhanced test exercises an
    end-tag-without-close-angle scenario.

  - **Follow set**: a new named entry HtmlTagRecovery was added to
    RecoveryFollowSets.cs containing the HTML-side kinds
    {Whitespace, NewLine, OpenAngle, CloseAngle, ForwardSlash,
    Equals, DoubleQuote, SingleQuote, Transition}. The set is
    intentionally broader than the HtmlEndOfTagFollowSet planned
    for Stage 3.4 (which will be narrower, for misc-attribute
    absorption): at a missing-tag-name site the current token can be
    almost any HTML-side boundary and the sync needs to stop
    immediately, with no skipped content in the typical case. Per
    Big Design Decision #4, the set is HTML-side only and omits the
    C#-side Text kind that the same English word would map to in
    a C#-language set.

  - **RZ1047 / Parsing_TagNameExpected**: net-new diagnostic
    introduced by Stage 3.1. Allocated as the next free RZ1xxx ID
    (RZ1046 was Stage 2.3's Parsing_UnexpectedTokenInStatement).
    Added with its _At factory in RazorDiagnosticFactory.cs at
    the end of the Language Errors region and the
    ParseError_TagNameExpected resource string ("Expected an HTML
    tag name.") in Resources.resx. No XLF updates needed (Razor
    has no .xlf sidecars). RZ1047 paired diagnostic format
    follows Stage 1.3's convention: zero-width source span on the
    missing token.

  - **Parsing_UnfinishedTag_At** (RZ1024): not new -- already
    paired by Stage 1.3 in RazorDiagnosticFactory.cs. Reused as-is
    in both enhanced close-angle sites. No legacy RZ-ID reuse
    issues; the _At factory's descriptor is the same RZ1024.

  - **Convenience overload of Synchronize** used throughout (no
    outerFollow threading). Stage 4.2 will mechanically upgrade
    all enhanced-mode Required / Synchronize call sites to
    accept and thread the caller's outer follow set per Big Design
    Decision #4. Until then, the outerFollow = FollowSet.Empty
    default is safe for Stage 3.1 because the typical missing-token
    sites here are inside HtmlTagRecovery-rich contexts where the
    sync stops immediately on the first non-whitespace token.

  - **Refactor of legacy close-angle branch in ParseStartTag**:
    extracted a local ool closeAngleConsumed flag so the
    void-element / AcceptedCharacters setup block is shared
    between the legacy and enhanced branches. The inner
    if (At(CloseAngle)) that previously gated the void-element
    block was redundant in the legacy code (we were already inside
    the lse of EndOfFile || !At(CloseAngle)), so removing it
    is semantically equivalent. Verified by the 1330 / 1330 legacy
    corpus tests passing unchanged with UseEnhancedRecovery = false.

  - **Test added**:
    - UnnamedTag.razor corpus (24 bytes CRLF):
      <>foo</>\r\n<p>after</p>\r\n. Exercises the tag-name slot in
      both ParseStartTag (<>) and ParseEndTag (</>).
    - [Fact] UnnamedTag (legacy snapshot baseline): generated
      UnnamedTag.stree.txt and UnnamedTag.cspans.txt. No
      `.diag.txt` baseline produced because the legacy parser
      emits no diagnostic for <>foo</> outside MarkupInCodeBlock
      mode (legacy produces a bare Text;[<Missing>] for both tag
      names with no error). The lack of a legacy `.diag.txt` is
      itself the key Stage 3.1 "before" observation: the bug is
      that legacy silently accepts an unnamed tag.
    - [Fact] UnnamedTag_EnhancedRecovery (in-memory assertions
      matching the Stage 1.4 / 2.x deviation): asserts two RZ1047
      diagnostics at AbsoluteIndex 1 and 7 (zero-width), two
      MissingToken(Text) at the same positions, real CloseAngle
      tokens at positions 1 and 7, and that the trailing
      <p>after</p> parses as a real MarkupElement with no
      `MarkupMiscAttributeContent` / SkippedContentSyntax
      contamination.

  - **No close-angle-missing enhanced test** is added by Stage 3.1.
    The close-angle Required path is exercised indirectly by the
    28 pre-existing corpus tests passing under legacy mode (no new
    diagnostics in legacy mode since the flag is off); under flag
    on, the 3600 / 3600 language tests pass unchanged (which
    includes scenarios like @{<p that hit this code path). A
    dedicated enhanced test for the close-angle path would require
    a MarkupInCodeBlock context, where the } end-of-block
    character is tokenized as Text by HtmlTokenizer (not as a
    distinct RightBrace kind, which HtmlTokenizer does not
    emit). Stage 3.3 / 3.4 will revisit close-angle recovery
    interactions with code-block boundaries; testing in isolation
    here would create a brittle assertion against the current
    token-classification quirk.

  - **In-memory assertions for the new enhanced test** (matching
    the Stage 1.4 / 2.x deviation): no parallel
    .enhanced.{stree,diag,cspans}.txt baselines generated; the
    test asserts the Stage 3.1 exit criteria directly. This keeps
    the corpus / baseline files representing the legacy "before"
    state only, and the enhanced "after" state is documented in
    the test's assertions and comments.

  **New RZ IDs allocated** in Stage 3.1: **RZ1047**
  (Parsing_TagNameExpected). The descriptor was added at the end
  of the Language Errors region in RazorDiagnosticFactory.cs
  (after RZ1046). The next free RZ1xxx parser-recovery ID is now
  **RZ1048**.

  **HtmlMarkupParser.cs Stage-3.1 site audit**: 2 tag-name
  recovery sites (one in ParseStartTag, one in ParseEndTag) and
  2 close-angle recovery sites (one in each), all migrated under
  the UseEnhancedRecovery flag. Legacy paths preserved
  byte-for-byte except for the harmless closeAngleConsumed
  extraction in ParseStartTag. Stage 3.2 (attribute parsing) and
  Stage 3.3 / 3.4 (other recovery sites in HtmlMarkupParser) will
  follow in subsequent stages.

  Razor.slnf builds clean (0 warnings, 0 errors). Legacy tests
  1332 / 1332 (1330 baseline + 1 new legacy corpus + 1 new enhanced);
  language tests 3600 / 3600 unchanged. Both TFMs (net10.0 and net472).

- **Stage 3.2 (HtmlMarkupParser attribute-parsing migration --
  empty C#-bound attribute value)**: First parser fix landing the
  BDD #9 shape `GenericBlock([CSharpExpressionLiteral([MissingToken(Identifier)])])`
  for the motivating bug from dotnet/razor#10383
  (`<button @onclick="">`).

  **Flow analysis (deviation from plan literal text)**: the plan
  references `ParseConditionalAttributeValue` / `OtherParserBlock`
  as the migration site, but empirical investigation of the
  motivating bug shows that under default Legacy file kind, the
  `@onclick` name is parsed via the
  `AttributeNameParsingResult.CSharp` -> `OtherParserBlock` path,
  splitting `@onclick` and `=""` into two `MarkupMiscAttributeContent`
  nodes; the bug shape never reaches `ParseRemainingAttribute`.
  Under Component file kind (`AllowCSharpInMarkupAttributeArea`
  cleared in `RazorParserOptions.Flags.cs:52`), `@onclick` parses
  as a regular attribute name and flows through `ParseRemainingAttribute`
  -> `IsConditionalAttributeName` true -> empty value -> `attributeValue`
  is `null`. That is the path the fix targets, and it's the one
  the Blazor component scenario in #10383 actually executes.

  **Implementation (Option A)**: surgical injection at a single
  site at the end of `HtmlMarkupParser.ParseRemainingAttribute`,
  immediately before the final `return SyntaxFactory.MarkupAttributeBlock(...)`.
  When `UseEnhancedRecovery` is set and `attributeValue is null`
  and the name passes both `IsConditionalAttributeName(nameContent)`
  (mirrors the original branch gate, excluding `data-`) and a new
  `IsCSharpBoundAttributeName(nameContent)` check (name starts
  with `@`), the parser synthesises a zero-width
  `MarkupTagHelperAttributeValue` containing the BDD #9
  `GenericBlock([CSharpExpressionLiteral([MissingToken(Identifier)])])`
  subtree. The `@` gate is essential to leave plain HTML
  attributes like `<input value="">` undisturbed (still null
  Value, still valid HTML). Skipped also handling the
  `ParseConditionalAttributeValue` -> `OtherParserBlock` path
  (`class="@"` scenario) since the corpus exit criterion does
  not require it and restructuring the
  `MarkupDynamicAttributeValue` wrapper to emit the BDD #9
  shape would be significantly larger; can be revisited in
  Stage 3.3 / 3.4 if a corpus test forces it.

  **Helpers added** (private static, placed between
  `ParseRemainingAttribute` and `ParseNonConditionalAttributeValue`):
  - `IsCSharpBoundAttributeName(string name)`: returns true iff
    the attribute name starts with `@`.
  - `CreateMissingCSharpExpressionValueBlock()`: builds the
    BDD #9 zero-width subtree using the InternalSyntax
    `SyntaxFactory` overloads (`MissingToken(SyntaxKind.Identifier)`,
    `CSharpExpressionLiteral(SyntaxList<SyntaxToken>)`,
    `GenericBlock(SyntaxList<RazorSyntaxNode>)`). Uses the
    `SyntaxList<TNode>(GreenNode)` single-element constructor.

  **No new RZ IDs**: Stage 3.2 introduces no parser diagnostic.
  The shape downstream codegen (Stage 5.1) will read produces
  the existing RZ2008 / RZ10024-class diagnostic via the
  tag-helper / binding pipeline, not via the parser. Tests
  assert `Assert.Empty(tree.Diagnostics)` to confirm.

  **Tests**: one new enhanced-mode `[Fact]`
  `EmptyBoundAttribute_Onclick_EnhancedRecovery` added to
  `ParserRecoveryCorpusSnapshotTests.cs` (after
  `UnnamedTag_EnhancedRecovery`, before `ParseCorpusFile`). Uses
  the existing `EmptyBoundAttribute_Onclick.razor` corpus file
  but parses with `fileKind: RazorFileKind.Component` and
  `UseEnhancedRecovery=true` to hit the targeted code path,
  then asserts:
  - the targeted attribute has a non-null `Value` of type
    `MarkupTagHelperAttributeValue`,
  - it contains exactly one `GenericBlock` child whose single
    child is a `CSharpExpressionLiteral` whose single token is a
    missing `Identifier`,
  - the synthesised subtree is zero-width,
  - the sibling `class` attribute is unaffected (still produces
    a normal markup-text value),
  - the parse tree carries no diagnostics.

  The corpus baselines (`EmptyBoundAttribute_Onclick.stree.txt` /
  `.diag.txt`) document the legacy "before" state and were not
  regenerated for Stage 3.2 (consistent with the Stage 1.4
  / 2.x / 3.1 deviation: enhanced mode uses in-memory assertions
  rather than parallel `.enhanced.*` baselines).

  **WorkItem attribute note**: the legacyTest project does not
  reference the `WorkItem` xUnit-extension attribute used elsewhere
  in roslyn; the GitHub issue link is recorded as a `//` comment
  on the test method instead.

  **HtmlMarkupParser.cs Stage-3.2 site audit**: 1
  `ParseRemainingAttribute` site migrated. The two `OtherParserBlock`
  call sites (one in `ParseConditionalAttributeValue`, one in the
  `AttributeNameParsingResult.CSharp` flow inside
  `ParseAttributeName`) are intentionally not migrated -- they
  do not produce the BDD #9 shape today and are deferred to
  Stage 3.3 / 3.4 if a corpus case requires them.

  Razor.slnf builds clean (0 warnings, 0 errors). Legacy tests
  1333 / 1333 (1332 baseline + 1 new enhanced); language tests
  3600 / 3600 unchanged. Both TFMs (net10.0 and net472).

- **Stage 3.3 (HtmlMarkupParser tag-stack-recovery diagnostic
  position migration -- complete)**: Three diagnostic emission
  sites in `HtmlMarkupParser.cs` gated under
  `Context.Options.UseEnhancedRecovery`. The tag-stack recovery
  algorithm itself is unchanged; only the diagnostic spans
  narrow.

  Migrated sites (all in `HtmlMarkupParser.cs`):

  1. `CompleteMarkupInCodeBlock` (~line 336): RZ1025
     (`Parsing_MissingEndTag`) for unclosed start tags at the
     code block's end. Legacy span: at the unclosed start tag's
     name (`SourceLocationTracker.Advance(tracker.TagLocation, "<")`,
     length = `tracker.TagName.Length`). Enhanced span: zero-width
     at `CurrentStart` (the cursor at EOF / end-of-block).
     Migrated to `CreateParsing_MissingEndTag_At` (Stage 1.3
     pairing, RZ1025 reused).

  2. `CompleteEndTag` empty-tracker branch (~line 552): RZ1026
     (`Parsing_UnexpectedEndTag`) for an orphan end tag with no
     matching open. Legacy span: at the end-tag name
     (`SourceLocationTracker.Advance(endTagStartLocation, "</")`,
     length = `Math.Max(endTagName.Length, 1)`). Enhanced span:
     zero-width at `endTagStartLocation` (the start of `</`).
     Migrated to `CreateParsing_UnexpectedEndTag_At` (Stage 1.3
     pairing, RZ1026 reused).

  3. `CompleteEndTag` outer-unclosed cleanup loop (~line 568):
     RZ1025 for the outermost unclosed start tag when an
     unexpected end tag triggers tracker unwinding. Legacy span:
     at the unclosed start tag's name. Enhanced span: zero-width
     at `endTagStartLocation` (where the matching end tag should
     have appeared -- i.e. where the unexpected end tag now is).
     Migrated to `CreateParsing_MissingEndTag_At`.

  **No new RZ IDs**: Stage 3.3 reuses the existing RZ1025 /
  RZ1026 descriptors via the `_At` pairings allocated by Stage
  1.3. Next free parser-recovery ID after Stage 3.3 was **RZ1048**
  (subsequently consumed by Stage 3.4).

  **Tag-stack recovery silent-success path NOT changed**:
  `TryRecoverStartTag`'s success path (where it finds a matching
  open tag further down the stack) silently pops intermediate
  unclosed tags as malformed elements without emitting any
  diagnostic. This long-standing behaviour is unchanged. The
  document-mode EOF cleanup in `ParseDocument` (lines 83-102)
  similarly pops without emitting. These silent paths are
  outside Stage 3.3's scope -- the plan says "the tag-stack
  recovery itself doesn't change structurally; what changes is
  the position of existing diagnostics". Adding new emission
  sites is deferred (potentially to a later stage if a corpus
  case requires it).

  **`TryRecoverStartTag` failure path** (returns false) is
  handled by `ParseMarkupElement` -> `CompleteEndTag` -> sites
  #2 and #3, which are migrated.

  **Corpus assertion (`UnclosedTag_EnhancedRecovery`)**: pure
  document-mode markup (`<div>...<span>...<p>text</div>...<section>...</section>`).
  All recovery for this file flows through the silent paths
  (`TryRecoverStartTag` success path matches the outer `<div>`,
  popping `<span>` and `<p>` silently), so no RZ1025 / RZ1026
  diagnostics fire. The test asserts the tree shape: outer
  `<div>...</div>` `MarkupElement` containing nested malformed
  `<span>` and `<p>` (start tag, no end tag), sibling
  `<section>...</section>` parsing cleanly, no
  `MarkupMiscAttributeContent` across the whole file.

  The test additionally exercises three in-memory sources to
  validate the migrated diagnostic positions on the three sites:

  - `@{ </div> }` -- site #2: RZ1026 zero-width at position 3
    (start of `</`).
  - `@{ <div> }` -- site #1: RZ1025 zero-width at position 10
    (EOF / end of source -- the markup parser's `CurrentStart`
    after exiting the in-code-block loop).
  - `@{ <div></span> }` -- site #3: RZ1025 zero-width at
    position 8 (start of the unexpected `</span>`). Note that
    in this scenario RZ1026 does NOT fire -- the
    non-empty-tracker branch of `CompleteEndTag` attributes the
    recovery to the unclosed start tags only (RZ1025), not to
    the orphan end tag itself.

  **Corpus baselines (`UnclosedTag.{stree,cspans}.txt`)
  unchanged**: legacy mode parses the corpus file identically
  to the existing baselines. No `.diag.txt` exists for
  `UnclosedTag` because the silent recovery paths emit no
  diagnostics under legacy either.

  Razor.slnf builds clean (0 warnings, 0 errors). Legacy tests
  1334 / 1334 (1333 baseline + 1 new enhanced); language tests
  3600 / 3600 unchanged. Both TFMs (net10.0 and net472).
- **Stage 3.4 (HtmlMarkupParser `ParseMiscAttribute` migration --
  complete)**: Replaces the legacy "absorb everything into a fat
  `MarkupMiscAttributeContent`" loop with a single
  `Synchronize(HtmlEndOfTagFollowSet, originatingLanguage: MarkupBlock)`
  call gated under `Context.Options.UseEnhancedRecovery`. Stops at
  the first HTML tag boundary (`<`, `>`, `/`, `"`, `'`) and emits a
  narrow zero-width RZ1048 (`Parsing_UnexpectedAttributeName`) at
  the cursor where an attribute name was expected. Absorbed tokens
  become `SkippedContentSyntax` tagged with `MarkupBlock`.

  Migrated site (in `HtmlMarkupParser.cs`):

  - `ParseMiscAttribute` (~line 1199): legacy code loops through
    `ParseMarkupNodes(ParseMode.Text, IsTagRecoveryStopPoint)` and
    a switch on quote / open-angle / forward-slash / close-angle,
    wrapping everything in `MarkupMiscAttributeContent`. Enhanced
    code flushes any pending accepted tokens via
    `OutputAsMarkupLiteral()` (the `attributePrefixWhitespace`
    that `ParseAttribute`'s `Other` branch accepted just before
    calling us), then either no-ops at a follow-set boundary or
    emits RZ1048 and synchronizes. The resulting
    `SkippedContentSyntax` is appended to the caller's builder.

  Two call sites both use the enhanced branch:
  1. `ParseAttributes` immediate-when-no-whitespace path
     (~line 1167): triggered when `<tag>` is followed by any token
     other than whitespace / newline / a follow-set boundary.
     Example: `<input!garbage>` -> cursor at `!`, RZ1048 at
     position 6, `SkippedContent("!garbage")` stopping at `>`.
     For the very common well-formed `<p>` shape (no whitespace
     before `>`), the cursor is already at the follow-set boundary
     and the enhanced branch no-ops -- matching legacy's no-op
     behaviour (where the inner switch hits the
     `CloseAngle / OpenAngle / ForwardSlash` case immediately and
     returns with an empty `MarkupMiscAttributeContent` that gets
     dropped via the `Count > 0` guard).
  2. `ParseAttribute`'s `AttributeNameParsingResult.Other` branch
     (~line 1367): triggered when the attribute-name slot is
     occupied by a non-name token (e.g. `=`, `!`).
     Example: `<input @bind=>` -> cursor at `=` (position 12),
     RZ1048 at 12, `SkippedContent("=")` stopping at `>`.

  **New RZ IDs allocated** in Stage 3.4: **RZ1048**
  (`Parsing_UnexpectedAttributeName`). Next free parser-recovery
  ID: **RZ1049**.

  **New follow set**: `RecoveryFollowSets.HtmlEndOfTagFollowSet`
  = `(OpenAngle, CloseAngle, ForwardSlash, DoubleQuote, SingleQuote)`.
  Quote kinds are part of the set so the surrounding
  `ParseAttributes` loop can resume normal attribute parsing of a
  well-formed quoted segment rather than swallowing it as garbage.
  Quote tokens are valid attribute-name tokens
  (`IsValidAttributeNameToken` does not exclude them), so the
  outer loop makes progress and cannot infinite-loop.

  **Diagnostic attachment via `ErrorSink`** (not on a missing
  token): RZ1048 fires for an *unexpected* token (not a *missing*
  one), so there is no `MissingToken(kind)` to attach the
  diagnostic to. This mirrors Stage 2.3's RZ1046
  (`Parsing_UnexpectedTokenInStatement`).

  **Stage 3 panic-recovery exit criterion**: the four
  fat-`MarkupMiscAttributeContent`-emitting recovery sites in
  `HtmlMarkupParser` are now migrated (3.1 tag name / close angle,
  3.2 attribute parsing, 3.3 tag-stack recovery, 3.4 misc
  attribute). Remaining `AcceptUntil` usages in
  `HtmlMarkupParser.cs` are in non-Stage-3 functions:

  - `RecoverTextTag` (~line 958): `<text>`-tag specific.
  - `ParseEndTag` malformed-end-tag absorber (~line 1055): in
    `MarkupInCodeBlock` mode; the absorbed content is positioned
    immediately before the close-angle `Required` call migrated by
    Stage 3.1. Whether this absorber is itself replaced by a
    `Synchronize` is a Stage 4 / 6 decision (the close-angle
    diagnostic position has already narrowed).
  - Script-tag parsing (~line 1819).

  None of these are part of Stage 3's enumerated migration scope.

  **Corpus assertion (`MalformedTagAttribute_EnhancedRecovery`)**:
  uses the existing `MalformedTagAttribute.razor` corpus
  (`<input @bind=>\r\n\r\n<p>after the malformed bind</p>\r\n`),
  which directly exercises the `ParseAttribute.Other` call site
  via the unexpected `=` after `@bind`. The test asserts:
  - One RZ1048 at position 12, length 0.
  - One `SkippedContentSyntax` inside the `<input>` start tag
    tagged with `MarkupBlock`, containing `=`, at span [12..13).
    (The `@bind` implicit expression produces its own
    `SkippedContentSyntax` tagged with `CSharpCodeBlock` under
    Stage 2.1; the assertion filters by `OriginatingLanguage`.)
  - One residual `MarkupMiscAttributeContent` for ` @bind` -- this
    wrapping is created by `ParseAttribute`'s `CSharp` branch (NOT
    by `ParseMiscAttribute`) and is out of scope for Stage 3.4.
  - The trailing `<p>after the malformed bind</p>` parses cleanly
    (no recovery contamination).

  The test additionally exercises two in-memory sources:

  - `<input!garbage>` -- `ParseAttributes` immediate-call site.
    RZ1048 at position 6, `SkippedContent("!garbage")` at [6..14)
    tagged with `MarkupBlock`, no `MarkupMiscAttributeContent`
    inside the start tag, real `CloseAngle` at position 14.
  - `<p></p>` -- no-op-at-boundary guard. No RZ1048, no
    `SkippedContentSyntax`, and a clean `MarkupElement` with real
    start / end tag names. This is the critical regression
    backstop: a spurious diagnostic here would fire on most
    well-formed minimal-attribute markup.

  **Corpus baselines unchanged**: `MalformedTagAttribute.razor`'s
  legacy `.stree.txt` / `.diag.txt` / `.cspans.txt` / `.tspans.txt`
  baselines are unmodified -- legacy mode still wraps `=` in
  `MarkupMiscAttributeContent` with no diagnostic. No corpus
  files were added.

  Razor.slnf builds clean (0 warnings, 0 errors). Legacy tests
  1335 / 1335 (1334 baseline + 1 new enhanced); language tests
  3600 / 3600 unchanged. Both TFMs (net10.0 and net472).

  **Stage 3 (HtmlMarkupParser migration) complete.** Ready for
  Stage 4.1 (cross-parser handoff `OtherParserBlock` migration).
- 2026-05-26: Stage 4.1 done. Cross-parser handoff signature/protocol
  setup (no behaviour change). Added the overload-pair signatures for
  `ParseBlock` and `OtherParserBlock` on both `CSharpCodeParser` and
  `HtmlMarkupParser` (parameterless wrapper -> parameterised method
  delegating to a private `ParseBlockCore`), plus a private
  `_outerFollow` field on each parser (save/restore around the
  parameterised `ParseBlock` body) so Stage 4.2 has a place to read
  the outer follow set from inner helpers.

  The parameterised `OtherParserBlock` overloads thread their
  `outerFollow` arg into the callee via the new cross-language
  translation helpers `RecoveryFollowSets.ForCSharpCallee(...)` /
  `RecoveryFollowSets.ForHtmlCallee(...)` -- defined in
  `RecoveryFollowSets.cs` per Big Design Decision #4's translation
  tables. Since Stage 4.1 callers all funnel through the parameterless
  wrapper, `outerFollow` is always `FollowSet.Empty` at this stage;
  translating an empty set yields an empty set, so the wiring is
  observably inert.

  Razor.slnf builds clean (0 warnings, 0 errors). Legacy tests
  1335 / 1335 unchanged; language tests 3600 / 3600 unchanged. Both
  TFMs (net10.0 and net472).

  See the **Stage 4.1 verification** section above for the per-call-site
  mapping table that Stage 4.2 will consume.
- 2026-05-27: Stage 4.2 done. Outer follow sets are now wired through
  every enhanced-mode `Synchronize` / `Required` / `OtherParserBlock`
  call site added by Stages 2/3. Per-call-site upgrade audit:

  **Enhanced-mode `Synchronize` call sites upgraded (9 total)**:
    - `CSharpCodeParser.cs` (8 sites): `ParseExplicitExpressionBody`,
      `ParseMethodCallOrArrayIndex`, `ParseStandardStatement`,
      `TryBalanceBlock`, `ParseExtensibleDirective` (x2),
      `TryParseCondition`, `ParseUsingDeclaration`. Each now passes
      `_outerFollow` as the second arg to the full `Synchronize`
      overload (`Synchronize(localFollow, outerFollow, originatingLanguage, options)`).
    - `HtmlMarkupParser.cs` (1 site): `ParseMiscAttribute` -- same
      mechanical upgrade.

  **Enhanced-mode `Required` call sites upgraded (6 total)**:
    - `TokenizerBackedParser` now accepts an optional
      `FollowSet outerFollow = default` on both `Required` overloads
      (single-kind and multi-kind). The inner `Synchronize` call
      threads that value into the full overload. `FollowSet`'s default
      value is `Empty`, so existing callers (notably the 2 in
      `TokenizerBackedParser.ParseRazorComment`, which has no
      per-parser context in the base class) are observably unchanged.
    - `CSharpCodeParser.cs` (2 sites):
      `ParseMethodCallOrArrayIndex` close-bracket, `ParseCodeBlock`
      right-brace.
    - `HtmlMarkupParser.cs` (4 sites): `ParseStartTag` tag-name and
      close-angle, `ParseEndTag` tag-name and close-angle.

  **Enhanced-mode `OtherParserBlock` cross-parser handoff sites
  upgraded (5 total) + `ParseNestedBlock`**:
    - `CSharpCodeParser.ParseStatement` markup-transition handoff:
      passes `_outerFollow | (RightBrace, Semicolon, Transition, LessThan)`.
    - `CSharpCodeParser.ParseTemplate` template handoff:
      passes `_outerFollow | (RightParenthesis, Transition)`.
    - `HtmlMarkupParser.ParseAttribute` C# branch:
      passes `_outerFollow | (OpenAngle, ForwardSlash, DoubleQuote, SingleQuote)`.
    - `HtmlMarkupParser.ParseConditionalAttributeValue` dynamic-value:
      passes `_outerFollow | attributeFollow` where `attributeFollow`
      is `(quote, NewLine)` when quoted or the full
      unquoted-attribute terminator set otherwise.
    - `HtmlMarkupParser.ParseCodeTransition`:
      passes `_outerFollow | (OpenAngle)` -- this is the critical site
      for the new corpus case below.
    - `CSharpCodeParser.ParseNestedBlock` now calls
      `ParseBlock(_outerFollow)` instead of the parameterless overload,
      threading the outer-follow context through the embedded-expression
      caller chain.
    - Row 7 of the Stage 4.1 verification table (extensible-directive
      `ParseRazorBlock` handoff at `CSharpCodeParser.cs:2170`) is
      explicitly informational only -- Stage 4.2 leaves the parameterless
      `OtherParserBlock(...)` form in place since `ParseRazorBlock`
      matches nesting sequences as raw text.

  No new diagnostics, no new public APIs, no `Synchronize` /
  `OtherParserBlock` overloads removed. Cross-parser translation is
  done inside `OtherParserBlock`'s parameterised body via
  `RecoveryFollowSets.ForCSharpCallee` / `ForHtmlCallee` (Stage 4.1
  wiring), so each upgraded caller passes `outerFollow` in its OWN
  parser's vocabulary -- translation is automatic.

  No explicit `AtOuterFollowToken` bail-out logic was added at any of
  the 9 direct `Synchronize` sites: examining each call site, the
  post-`Synchronize` flow naturally returns/exits the construct (e.g.
  `TryParseCondition` returns `false` -> `ParseConditionalBlock` skips
  the body -> `ParseStatement` returns -> `ParseBlockCore`'s `finally`
  calls `PutCurrentBack` on the outer token). The plain mechanical
  upgrade suffices.

  **Corpus addition**: `MalformedCSharpWithSurroundingMarkup.razor`
  (`<div>@if(foo bar baz<p>still html</p></div>`) -- the canonical
  outer-follow handoff case. Without Stage 4.2, the C# parser
  absorbs `<p>still html</p></div>` into a fat `CSharpStatementLiteral`
  (legacy baseline confirms this). With Stage 4.2, HTML's
  `ParseCodeTransition` passes `LessThan` as the outer-follow into the
  C# parser via `OtherParserBlock`; `TryParseCondition`'s `Balance`
  fails on `<`, `Synchronize` stops at the `<` outer-follow token,
  `ParseConditionalBlock` skips the body, control returns to HTML
  which resumes at `<p>` and parses it as a real `MarkupElement`. The
  outer `<div>...</div>` survives intact with both start and end
  tags. Enhanced-recovery test
  `MalformedCSharpWithSurroundingMarkup_EnhancedRecovery` asserts:
  the `foo bar baz` garbage is in a single `SkippedContentSyntax`
  tagged `CSharpCodeBlock`; `<p>` is a real `MarkupElement` nested
  inside the surviving `<div>` element; no `MarkupMiscAttributeContent`;
  every non-zero-width `CSharpStatementLiteral` ends at or before the
  opening `(`.

  Razor.slnf builds clean (0 warnings, 0 errors). Legacy tests
  1337 / 1337 (1335 baseline + 2 new -- legacy `MalformedCSharpWithSurroundingMarkup`
  pins the legacy fat-literal behaviour via standard
  `.stree.txt` / `.diag.txt` / `.cspans.txt` baselines, enhanced
  `_EnhancedRecovery` test asserts the fix); language tests
  3600 / 3600 unchanged. Both TFMs (net10.0 and net472).

  No Stage 2/3 enhanced-mode test regressed: the upgrades are
  backward-compatible because `_outerFollow` is `FollowSet.Empty`
  whenever the parser was entered via the parameterless `ParseBlock`
  wrapper, and `FollowSet.Empty` is the identity for both the `|`
  operator and `ForCSharpCallee` / `ForHtmlCallee` translation.
- 2026-05-28: Stage 4.3 done. Validation-only: the canonical implicit-
  expression / markup boundary case `@foo.<p>after</p>` already
  parses cleanly under both legacy AND enhanced mode without any
  parser change beyond Stage 4.2's outer-follow wiring. No source
  changes were needed in `ParseImplicitExpression` or
  `ParseMethodCallOrArrayIndex`; no new RZ ID was allocated.

  **Why no change was needed**: the input `@foo.<p>` does not invoke
  the Stage 2.6 `Balance` failure path at all. The grammar drives the
  parser as follows:
    1. `@` -> transition; `foo` accepted in the outer
       `ParseImplicitExpression` loop.
    2. First `ParseMethodCallOrArrayIndex` call: sees `.`, accepts it,
       calls `NextToken()`, sees `<` (`LessThan`) -- not an
       `Identifier` / `Keyword`, so the existing legacy logic does
       `PutCurrentBack()` (pushes `<` back) and -- because `!IsNested`
       at the top-level implicit-expression site -- does `PutBack(dot)`,
       returning the cursor to the `.` and returning `false`.
    3. Outer loop exits. `OutputTokensAsExpressionLiteral()` emits
       `foo` (3 chars) as the expression body. The `.` is put back
       into the lexer stream and becomes a `MarkupTextLiteral` consumed
       by the HTML parser; `<p>after</p>` parses as a real
       `MarkupElement`.
  `Balance` is never invoked (there is no `(` or `[`), so the Stage
  2.6 enhanced sync branch is unreached and `_outerFollow` is never
  consulted. Enhanced mode produces a tree identical to legacy mode
  for this input.

  **Why the plan's "narrow diagnostic on the `.`" criterion is not
  triggered**: the trailing-dot put-back is not treated as an error
  by the parser -- it is a legitimate boundary in the implicit-
  expression grammar (a trailing `.` that is not followed by a member
  name simply isn't part of the expression). Neither the legacy
  baseline nor the enhanced run emits any diagnostic. The plan's
  exit-criterion wording leaves room for this: "with a narrow
  diagnostic on the `.` (if appropriate) -- or no diagnostic if the
  parser successfully recovers without one". The enhanced parser
  recovers without one.

  **Corpus addition**: `ImplicitExpressionHittingMarkup.razor`
  (`@foo.<p>after</p>\r\n`). Legacy baselines
  (`.stree.txt` / `.cspans.txt`; no `.diag.txt` because there are
  zero legacy diagnostics) pin the trailing-dot put-back behaviour.
  Enhanced-recovery test
  `ImplicitExpressionHittingMarkup_EnhancedRecovery` asserts:
    - Zero diagnostics on the tree.
    - The `CSharpImplicitExpression` is the 4-char span `[0..4)` =
      `@foo` -- the trailing `.` is NOT part of the implicit
      expression.
    - No `SkippedContentSyntax` anywhere (Stage 2.6 sync branch
      unreached).
    - No missing `RightParenthesis` / `RightBracket` (the
      `Required(right, ...)` site is unreached).
    - The trailing `<p>after</p>` is a real `MarkupElement` starting
      at offset 5, with content exactly `<p>after</p>`.
    - No `MarkupMiscAttributeContent` (Stage 2 exit criterion #4).

  This test is essentially a regression guard: any future change to
  `ParseMethodCallOrArrayIndex` (or the dot-handling branch) that
  accidentally absorbs the `<` into a fat literal -- or that emits a
  spurious diagnostic on the trailing dot under enhanced mode --
  will fail this test.

  **Second-case decision**: the plan's "Possibly add a second case for
  the trickier `@foo.bar()<p>` or `@foo.!<p>` patterns" was evaluated
  and skipped. Tracing both:
    - `@foo.bar()<p>` exercises a successful `Balance` for `()` followed
      by `<` hitting the post-`Balance` recursion's final
      `!At(Whitespace) && !At(NewLine)` `PutCurrentBack` branch. Same
      family as the `@foo.<p>` test (no `Balance` failure, no
      diagnostic).
    - `@foo.!<p>` (without `AllowNullableForgivenessOperator`) goes
      through the `Dot` branch's `NextToken()` -> `!` not identifier ->
      `PutCurrentBack` + `PutBack(dot)` exit. Same outcome as
      `@foo.<p>`.
  Neither case exercises the Stage 2.6 sync branch, so neither would
  meaningfully add coverage beyond what the canonical case already
  pins. The existing `UnclosedMethodCallInImplicit` corpus case
  (`<p>@foo.Bar(baz</p>...`) already covers the `Balance`-failure
  path -- including the `LessThan` outer-follow stop -- under Stage
  2.6's enhanced-recovery test.

  **Stage 4.3 verdict**: validation-only. The Stage 4.2 outer-follow
  wiring is correct for the implicit-expression case and the canonical
  exit-criterion input (`@foo.<p>after</p>`) is now regression-pinned
  under enhanced mode.

  Razor.slnf builds clean (0 warnings, 0 errors). Legacy tests
  1339 / 1339 (1337 baseline + 2 new -- legacy
  `ImplicitExpressionHittingMarkup` pins the trailing-dot put-back
  behaviour via standard `.stree.txt` / `.cspans.txt` baselines,
  enhanced `_EnhancedRecovery` test asserts identical shape under
  enhanced mode); language tests 3600 / 3600 unchanged. Both TFMs
  (net10.0 and net472).
- 2026-05-29: Stage 4.4 done. Cross-language tokenizer-state hooks
  are now fired on every enhanced-mode C# `Synchronize` bail-out that
  stops at a token in the caller's outer follow set.

  **Helper added (`TokenizerBackedParser.cs`)**:
    - `internal void EndingBlockIfStoppedOnOuter(SyncResult result)` --
      when `result.StopReason == SyncStopReason.AtOuterFollowToken`,
      calls `EndingBlock()` (the existing internal wrapper that
      forwards to the active tokenizer's `EndingBlock()` override).
      The companion `StartingBlockIfStoppedOnOuter` was deliberately
      NOT added: the C# parser's `ParseBlock` (and
      `OtherParserBlock`'s post-return hook) already fire
      `StartingBlock` on every re-entry, so cursor re-alignment on
      the C#-back-from-HTML boundary is unchanged.

  **Enhanced-mode C# Synchronize call sites upgraded (8 total)** --
  identical set to the 8 sites Stage 4.2 wired with `_outerFollow`,
  each now followed by an `EndingBlockIfStoppedOnOuter(sync)` call:
    - `ParseExplicitExpressionBody` (Balance-failure recovery in `@(`).
    - `ParseImplicitExpression` / `ParseMethodCallOrArrayIndex`
      (Balance-failure recovery in implicit-expression call/index).
    - `ParseStandardStatement` (mid-statement garbage recovery).
    - `TryBalanceBlock` inside `ParseStatementBody` (`@{ }` balance
      failure recovery).
    - `ParseExtensibleDirective` trailing-junk sync (in the
      `DirectiveTokenKind` recovery branch).
    - `BuildBailedDirective` (extensible-directive trailing sync on
      missing required tokens).
    - `TryParseCondition` (the canonical Stage 4.2 case).
    - `ParseUsingDeclaration` trailing sync.

  **HTML side**: no change. `HtmlTokenizer.StartingBlock` /
  `EndingBlock` are no-op overrides of the base virtual no-ops, so
  the single enhanced-mode HTML `Synchronize` site
  (`ParseMiscAttribute`) does not need the hook -- calling
  `EndingBlock` on the HTML tokenizer would be a no-op anyway. The
  helper lives on `TokenizerBackedParser` (not on `CSharpCodeParser`)
  for symmetry, so future HTML-side requirements (or a future
  RoslynHtmlTokenizer) could opt in without a parser-side rework.

  **`Required` call sites**: deliberately not updated. The plan
  literal scopes the hook to direct `Synchronize` calls. The 6
  enhanced-mode `Required` sites (2 in C#, 4 in HTML) thread
  `outerFollow` into their internal `Synchronize` but do not expose
  the `SyncStopReason`. Threading the stop reason out through
  `Required` would be a wider signature churn than Stage 4.4
  authorises. The actual observable behaviour is identical because:
  (a) every C# `Required` site is followed by code that either
  immediately exits the construct or runs further parsing that hits
  the next `OtherParserBlock`, which fires `EndingBlock` itself;
  (b) HTML's `Required` sites use the no-op HTML tokenizer hooks.

  **Tests**: new test class
  `RoslynCSharpTokenizerRecoveryTests` (3 tests, both TFMs) under
  `legacyTest/Legacy/`, using `useLegacyTokenizer: false` so the
  Roslyn tokenizer is active. Each test sets
  `UseEnhancedRecovery = true` on top:
    1. `MalformedCSharpWithSurroundingMarkup_RoslynTokenizer_EnhancedRecovery`
       -- the Stage 4.2 canonical case
       (`<div>@if(foo bar baz<p>still html</p></div>`)
       re-exercised under the Roslyn tokenizer. Asserts the same
       Stage 4.2 invariants (skipped-content shape, `<p>` is a real
       `MarkupElement`, outer `<div>` survives intact, no
       `MarkupMiscAttributeContent`).
    2. `RecoveryFollowedByImplicitTransition_RoslynTokenizer_EnhancedRecovery`
       -- the canonical "re-enter C# after recovery" test
       (`<div>@if(foo bar baz<p>@bar</p></div>`). Verifies the
       subsequent `@bar` implicit expression parses cleanly at the
       correct source positions, proving the Roslyn parser's state
       is realigned after the recovery bail-out.
    3. `RoslynAndLegacyTokenizers_ProduceEquivalentTrees_AcrossRecovery`
       -- pins the structural invariant that the tokenizer choice is
       not observable in the produced syntax tree across multiple
       enhanced-mode recovery sites (`@if(...`, `@(...`, `@{...}`),
       including re-entry scenarios.

  **Behavioural-difference investigation (recorded for traceability)**:
  the three tests pass under both Roslyn and legacy tokenizers
  whether or not the Stage 4.4 hook is applied -- a confirming
  observation, not a counter-example. The reason is that
  `RoslynCSharpTokenizer.StartingBlock` (which fires on every C#
  re-entry from `ParseBlock`) calls
  `_roslynTokenParser.SkipForwardTo(Source.Position)`, which
  defensively re-aligns the Roslyn parser's position even if
  `EndingBlock` was skipped at the previous exit. The Stage 4.4 hook
  is the **prescriptive correctness invariant** required by the
  plan: it ensures `RoslynCSharpTokenizer.CurrentState` is reset
  back to `Start` (via `EndingBlock`'s `CurrentState =
  RoslynCSharpTokenizerState.Start` assignment) and that the
  `_resultCache` last entry is rolled back via `ResetTo(result)`
  whenever C# relinquishes control across a recovery bail. Without
  the hook, both stay populated until the next `OtherParserBlock`
  call (which always fires `EndingBlock` itself); the
  `RoslynAndLegacyTokenizers_ProduceEquivalentTrees_AcrossRecovery`
  test pins this equivalence as a regression guard so a future
  tighter-state RoslynCSharpTokenizer (e.g. asserting `CurrentState
  == Start` on entry to `StartingBlock`) would fail loudly if the
  hook ever regressed.

  Razor.slnf builds clean (0 warnings, 0 errors). Legacy tests
  1342 / 1342 (1339 baseline + 3 new in
  `RoslynCSharpTokenizerRecoveryTests`); language tests 3600 / 3600
  unchanged. Both TFMs (net10.0 and net472).

  Stage 4 closed: cross-language handoff respects the outer follow
  set in all enhanced-mode paths (Stage 4.2), implicit-expression /
  markup boundary validated (Stage 4.3), and tokenizer state hooks
  fire correctly across `Synchronize` bail-out (Stage 4.4).
- 2026-05-30: Stage 5.0.0 done. Investigation-only spike test
  `Spike_EmptyOnclickAttribute_DumpsGeneratedSource` added to
  `src/Razor/src/Compiler/test/Microsoft.NET.Sdk.Razor.SourceGenerators.UnitTests/RazorSourceGeneratorComponentTests.cs`
  (around line 1879) plus a `ProcessSingleComponent` helper that
  mirrors `RazorSourceGenerator.Helpers.GetGenerationProjectEngine`
  with a `useEnhancedRecovery` knob (the internal `UseEnhancedRecovery`
  flag is reachable through `InternalsVisibleTo`). The test dumps
  four artifacts to `artifacts/razor-recovery-spike/` (gitignored
  via the pre-existing `[Aa]rtifacts/` rule): the source-generator
  output (user-visible legacy), direct-engine legacy, direct-engine
  enhanced, and a parser-diagnostics summary.

  Key empirical findings (full detail in the "Stage 5.0.0 spike
  report" section above):
    - Source-generator output for `<button @onclick="">` under the
      legacy parser **silently drops** the `@onclick` attribute --
      no `AddAttribute` call is emitted at all -- because
      `ComponentEventHandlerLoweringPass.RewriteUsage`'s `original.Length == 0`
      bail-out at lines 164-169 fires. No CS1525 in current
      legacy source-generator output for the BDD #9 reproducer.
    - The motivating "wall of red" in dotnet/razor#10383 therefore
      most plausibly originates from the second writer site --
      `ComponentNodeWriter.cs:1351-1376` -- which emits
      `EventCallback.Factory.Create<T>(this, <tokens>)` directly for
      component attributes bound to an `EventCallback<T>` parameter
      (e.g. `<MyComponent OnClick="">`) and is **NOT guarded** by any
      length / emptiness check. Stage 5.0 / 5.1 must cover both
      writer sites.
    - The recommended placeholder shape (`default!` substituted for
      the empty token stream) is recorded in the report so Stage 5.0
      can pick it up without re-running the spike.

  Test runs green (~2s) on net10.0; full source-generator unit-test
  suite still green: 214 / 214 passing on net10.0
  (the spike adds 1 test, baseline 213 -> 214). No baseline
  changes elsewhere. Razor.slnf builds clean.

  Deviations recorded for Stage 5.0:
    1. The source generator does not surface `UseEnhancedRecovery`
       (deliberate per Stage 0.3's downstream audit), and the direct
       `RazorProjectEngine` path used by the spike does **not** wire
       tag-helper discovery. Consequently the spike cannot directly
       observe enhanced-mode `EventCallback.Factory.Create<T>(this, )`
       emission in this harness; the enhanced-mode behavioural
       prediction relies on reading `GetAttributeContent`
       (`ComponentEventHandlerLoweringPass.cs:232-254`) plus the
       Stage 3.2 BDD #9 parse-tree shape. **Stage 5.0's first
       action** must be to wire tag-helper discovery into the spike's
       direct path (or surface a parser-options override on the
       source generator) so the enhanced-mode emission can be
       observed before the codegen fix lands.
    2. The `Spike_EmptyOnclickAttribute_DumpsGeneratedSource` test is
       deliberately investigation-only (asserts `Assert.True(true)`
       after writing the artifacts). Stage 5.1 will replace it with
       an assertion-driven corpus harness.
- 2026-05-26: Stage 5.0 done. Two deliverables landed:

  **Deliverable A - source-generator `UseEnhancedRecovery` plumbing.**
  Mirrored the existing `use-roslyn-tokenizer` ParseOption feature
  pattern. New `ParseOptions.UseEnhancedRecovery()` extension reads
  the `use-enhanced-recovery` feature key
  (`CSharp/ParseOptionsExtensions.cs`). New internal
  `RazorSourceGenerationOptions.UseEnhancedRecovery` property
  (`SourceGenerators/RazorSourceGenerationOptions.cs`). Wired through
  `ComputeRazorSourceGeneratorOptions` (RazorProviders) and both
  `GetDeclarationProjectEngine` / `GetGenerationProjectEngine` (Helpers).
  This addresses the Stage 5.0.0 spike deviation #1 by giving the
  source generator a way to turn on enhanced parsing, which Stage 5.0's
  Deliverable B then exercises end-to-end via a feature-flagged
  source-gen test.

  **Deliverable B - IR missing-value marker pipeline.**
    - New internal `IntermediateToken.IsMissingValue` boolean property
      on the abstract base (`Intermediate/IntermediateToken.cs`).
    - New `Intermediate/MissingValueMarker.cs` helper class providing
      `IsMissingValueMarker(IReadOnlyList<IntermediateToken>)`,
      `IsMissingValueMarker(ImmutableArray<IntermediateToken>)`, and
      `CreateMissingCSharpToken(SourceSpan?)`. The detection helper
      classifies a stream as a missing-value marker when the stream
      length is zero, all tokens carry `IsMissingValue == true`, or
      all tokens have `string.IsNullOrEmpty(Content)`. This handles
      the three observed shapes for empty C# attribute values
      (legacy = empty array; enhanced / BDD #9 = single
      MissingToken-bearing token; resolver-synthesized = single
      empty-content token).
    - `DefaultRazorIntermediateNodeLoweringPhase.LoweringVisitor.VisitCSharpExpressionLiteral`
      tags the IR token when all `LiteralTokens` have
      `GreenNode.IsMissing == true`. New private helper
      `IsAllMissing(SyntaxTokenList)` (the literal node's
      `IsMissing` aggregate is NOT used because `GreenNode.IsMissing`
      is the flag-only check, never aggregated from children).
    - `DefaultTagHelperResolutionPhase` adds `CreateMissingValueCSharpToken`
      next to the existing `CreateEmptyCSharpToken`. The
      `LegacyTagHelperResolver` synthetic-empty-token call sites in
      `LowerBoundLegacyAttributeValue` and `ConvertValueChildren` now
      route through it / `MissingValueMarker.CreateMissingCSharpToken`
      so resolver-synthesised empty tokens are tagged.
    - `ComponentEventHandlerLoweringPass.RewriteUsage` bail-out
      (`if (original.Length == 0) return node;`) replaced with
      `if (MissingValueMarker.IsMissingValueMarker(original))
       original = SubstituteMissingValuePlaceholder(node, original);`.
      New private helper `SubstituteMissingValuePlaceholder` returns
      `[MissingValueMarker.CreateMissingCSharpToken(source)]` (single
      tagged token; Stage 5.1 will refine the content to a safe
      placeholder such as `default!`). The `SourceSpan` is carried
      from the original first token (or `node.Source` when the
      stream was empty) so signature help still maps back to the
      user's `""` span.

  **Behavioural impact verified via spike artifacts.** Before
  Stage 5.0 the source-gen path emitted
  `__builder.AddMarkupContent(5, "<button ... @onclick>...</button>")`
  for `<button @onclick="">`, silently dropping the directive.
  After Stage 5.0 the same input produces
  `__builder.AddAttribute(7, "onclick",
   global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<
   global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, )); `
  with an empty argument placeholder. The empty argument still
  produces CS1525 in C# compilation; Stage 5.1 will finalize the
  placeholder content (e.g. `default!`) to remove the diagnostic.

  **Tests added (7 new):**
    - 4 new `MissingValueMarkerLoweringTests` in the Language
      UnitTests project: enhanced-mode tagging assertion via direct
      `RazorProjectEngine` (sufficient because tag-helper discovery
      is not required at this lower-level boundary),
      `LegacyMode_BoundNonStringAttributeWithEmptyValue_TagsSyntheticToken`,
      `IsMissingValueMarker_DetectsEffectivelyEmptyStreams`, and
      `SkippedContentSyntax_IsNotProjectedToIR`.
    - 3 new theory rows on
      `RazorSourceGeneratorComponentTests.EmptyOnclickAttribute_SourceGenerator_ReachesEventCallbackWrapping`
      (`null`, `"false"`, `"true"` for the `use-enhanced-recovery`
      feature value). Each asserts the generated code no longer
      contains the silent-drop `@onclick>Click me` markup blob and
      DOES contain `EventCallback.Factory.Create` and
      `AddAttribute(7, "onclick"`.

  **Test counts after Stage 5.0:**
    - Legacy: 1342 / 1342 (unchanged).
    - Language: 3604 / 3604 (was 3600, +4 new).
    - Source-gen: 217 / 217 (was 214, +3 new theory rows).
  Razor.slnf builds clean. No baseline changes elsewhere.

  Deviations carried into Stage 5.1:
    1. `ComponentNodeWriter.cs:1335-1376` (the unguarded second
       writer site noted in the Stage 5.0.0 spike report) was not
       touched by Stage 5.0; it remains the codegen site that
       Stage 5.1 must refine to emit the actual placeholder
       content (`default!`) instead of the empty token Stage 5.0
       hands it.
    2. The enhanced-mode IR-shape behaviour for `@onclick=""` cannot
       be observed end-to-end in the bare component engine harness
       used by `MissingValueMarkerLoweringTests` because tag-helper
       discovery is not wired up there; the `EmptyOnclickAttribute_*`
       tests therefore assert at the lower-level
       `HtmlAttributeIntermediateNode` boundary and the
       `EventCallback.Factory.Create` wrapping is asserted by the
       source-generator theory which DOES have tag-helper discovery.
    3. Stage 5.0 unifies the legacy `original.Length == 0` bail-out
       with the enhanced single-missing-token path, so the legacy
       parser now also produces an `EventCallback.Factory.Create<T>(this, )`
       call (empty argument) for `@onclick=""` instead of silently
       dropping the attribute. This is the intended unification: it
       gives Stage 5.1 a single codegen site to fix.
