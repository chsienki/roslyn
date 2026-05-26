# Razor parser error recovery

This document is the contributor-facing reference for the Razor parser's error-recovery model. Read it before writing or modifying a parser function under `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Legacy/`. For the broader parsing architecture (whitespace handling, directive shapes) see [`Parsing.md`](Parsing.md).

## Contents

- [Overview](#overview)
- [The `FollowSet` API](#the-followset-api)
- [The `Synchronize` helper](#the-synchronize-helper)
- [The `Required` / `Optional` helpers](#the-required--optional-helpers)
- [How to write a new recovery-aware parser function](#how-to-write-a-new-recovery-aware-parser-function)
- [The `SkippedContentSyntax` node](#the-skippedcontentsyntax-node)
- [Diagnostic factory `_At` convention](#diagnostic-factory-_at-convention)
- [How we got here](#how-we-got-here)

## Overview

Razor parsing is performed by two co-routine parsers -- `HtmlMarkupParser` and `CSharpCodeParser` -- both deriving from `TokenizerBackedParser<TTokenizer>` and sharing a single character cursor through `SeekableTextReader`. The pre-redesign recovery model had two structural gaps:

1. There was no place in the tree for "tokens that were present in the source but had no grammatical home" -- recovery folded them into a single fat literal (`CSharpStatementLiteral`, `MarkupTextLiteral`, `MarkupMiscAttributeContent`, etc.).
2. There was no parser-wide invariant for expected-but-absent tokens -- most parser functions called `AcceptUntil(LessThan)` or `AcceptUntil(NewLine)` and emitted a single diagnostic whose span covered the whole construct.

The recovery model corrects both gaps using a Roslyn-style approach:

- **Missing-token invariant.** Every expected-but-absent token is emitted as a zero-width `SyntaxToken` with `IsMissing == true` at the precise position where the token was expected, with the diagnostic attached.
- **`SkippedContentSyntax` for absorbed tokens.** Tokens that recovery had to skip past to reach a sync point live inside a `SkippedContentSyntax` node tagged with the originating language. Source positions are preserved; downstream consumers treat the node as semantically inert.
- **Follow-set-driven synchronization.** Each parser function declares the set of token kinds it can resynchronize at (e.g. "the next `;` or `}`"). When a `Required` token is missing, the parser advances past tokens until it sees one in the follow set (or EOF), wrapping the skipped tokens into `SkippedContentSyntax`.

The new primitives live in `TokenizerBackedParser`:

| Primitive | File | Role |
|-----------|------|------|
| `FollowSet` | `Legacy/FollowSet.cs` | Bitmap-backed set of `SyntaxKind` values. |
| `SyncResult` / `SyncStopReason` / `SyncOptions` | `Legacy/SyncResult.cs` | Return value and tuning knobs for `Synchronize`. |
| `RecoveryFollowSets` | `Legacy/RecoveryFollowSets.cs` | Named follow-set constants + cross-language translation helpers. |
| `Synchronize` / `Required` / `Optional` | `Legacy/TokenizerBackedParser.cs` | The parser-facing helpers. |
| `SkippedContentSyntax` | `Syntax/Syntax.xml` | The tree node holding absorbed tokens. |

## The `FollowSet` API

A `FollowSet` is a bitmap of `SyntaxKind` values used to express "tokens I can stop at when synchronizing". It lives at `Legacy/FollowSet.cs`.

```csharp
internal readonly struct FollowSet : IEquatable<FollowSet>
{
    public static readonly FollowSet Empty;

    public FollowSet(SyntaxKind kind);
    public FollowSet(params SyntaxKind[] kinds);

    public bool Contains(SyntaxKind kind);
    public bool IsEmpty { get; }
    public FollowSet Union(FollowSet other);
    public static FollowSet operator |(FollowSet a, FollowSet b);
}
```

Implementation notes:

- Backed by two `ulong`s indexed by the low byte of `SyntaxKind`. `SyntaxKind` is a `byte`-backed enum; the layout supports kinds with underlying value `<= 127`. A debug assertion fires if a kind beyond that range is added or tested -- if you extend `SyntaxKind` past 127, extend the bitmap layout too.
- The struct is small enough to pass by value through the parser without allocating.
- `default(FollowSet) == FollowSet.Empty`. The `outerFollow` parameter on `Required` / `Synchronize` defaults to `Empty`, which is the right thing for non-cross-parser call sites.

### Language scoping (the cross-language translation table)

A `FollowSet` is **language-scoped**. The two Razor tokenizers emit different `SyntaxKind` values for the same characters:

| Character | HTML kind | C# kind |
|-----------|-----------|---------|
| `<` | `OpenAngle` | `LessThan` |
| `>` | `CloseAngle` | `GreaterThan` |
| `/` | `ForwardSlash` | `Slash` |
| `"` | `DoubleQuote` | (absorbed into `StringLiteral`) |
| `'` | `SingleQuote` | (absorbed into `CharacterLiteral`) |
| whitespace, newline, `=`, `@` | same kind in both tokenizers |

A follow set authored in one tokenizer's vocabulary is not directly meaningful to the other. At a cross-language handoff, the caller's follow set must be **translated** to the callee's language before being threaded as `outerFollow`. Use the helpers on `RecoveryFollowSets`:

```csharp
public static FollowSet ForCSharpCallee(FollowSet htmlSet);
public static FollowSet ForHtmlCallee(FollowSet csharpSet);
```

Translation drops kinds that have no equivalent in the target vocabulary (e.g. C#-side `Semicolon` has no HTML translation; HTML-side `DoubleQuote` has no C# translation because `"` is absorbed into `StringLiteral`). The translation runs at the call site; `Synchronize` itself does no translation.

### Named follow-set constants

`RecoveryFollowSets` is a static class of named constants for follow sets used by more than one parser function. Add to it (rather than inlining `new FollowSet(...)` at the call site) when a follow set is reused or when its rationale is non-trivial. Existing entries:

| Constant | Used by | Tokens |
|----------|---------|--------|
| `CSharpDirectiveTrailing` | C# directive parsers (`@using`, `@addTagHelper`, `@inherits`, etc.) | `NewLine`, `RightBrace` |
| `CSharpImplicitExpressionTrailing` | `ParseMethodCallOrArrayIndex` recovery after `Balance` failure | `LessThan`, `NewLine`, `Whitespace` |
| `HtmlTagRecovery` | `ParseStartTag` / `ParseEndTag` recovery (missing tag name, missing `>`) | tag boundaries (`<`, `>`, `/`), attribute boundaries (`=`, `"`, `'`), whitespace, newline, `@` |
| `HtmlEndOfTagFollowSet` | `ParseMiscAttribute` recovery (unexpected attribute-name token) | `<`, `>`, `/`, `"`, `'` |

Each constant has a doc comment explaining its rationale. Read those before assuming a constant is a drop-in for a new use site.

## The `Synchronize` helper

`Synchronize` advances the tokenizer past unexpected tokens until the current token is in `localFollow`, in `outerFollow`, at EOF, or matches a stop condition in `options`. The synchronization-point token is NOT consumed -- it remains the current token on return so the caller can decide what to do with it.

```csharp
// Full overload (cross-language case).
protected internal SyncResult Synchronize(
    FollowSet localFollow,
    FollowSet outerFollow,
    SyntaxKind originatingLanguage,
    SyncOptions options = SyncOptions.None);

// Convenience overload (same-language; outerFollow defaults to Empty).
protected internal SyncResult Synchronize(
    FollowSet localFollow,
    SyntaxKind originatingLanguage,
    SyncOptions options = SyncOptions.None);
```

Return value:

```csharp
internal readonly record struct SyncResult(
    SkippedContentSyntax? Skipped,
    SyncStopReason StopReason);

internal enum SyncStopReason : byte
{
    AtFollowToken,        // hit a token in localFollow
    AtOuterFollowToken,   // hit a token in outerFollow (cross-language case)
    AtNewLine,            // only fires when SyncOptions.StopAtNewLine is set
    AtTransition,         // only fires when SyncOptions.StopAtTransition is set
    EndOfFile,
}

[Flags]
internal enum SyncOptions : byte
{
    None = 0,
    StopAtNewLine = 1 << 0,
    StopAtTransition = 1 << 1,
}
```

Semantics:

- The skipped tokens (if any) are packaged as a `SkippedContentSyntax` node tagged with `originatingLanguage` (typically `SyntaxKind.MarkupBlock` or `SyntaxKind.CSharpCodeBlock`). If nothing needed to be skipped (`Skipped == null`), the synchronization point was already current or EOF was reached immediately.
- `Synchronize` does NOT call `Accept` -- it produces a node the caller inserts into its own builder in positional order. This keeps the literal-token pipeline separate from the recovery pipeline.
- `Synchronize` honours `CancellationToken.ThrowIfCancellationRequested()` in its inner loop, so editor cancellation during recovery never hangs.
- `StopReason == AtOuterFollowToken` is the signal to bail back to the outer parser rather than continuing inner-grammar work. The caller for cross-language constructs typically checks this to terminate its enclosing block early.

When to call `Synchronize` directly (rather than through `Required`): when the failure has already been diagnosed (e.g. by a failed `Balance` call) and you just need to absorb garbage to a sync point. `Required` already calls `Synchronize` internally on its failure path; don't double-skip.

When NOT to call `Synchronize`: when `Balance` can find the matching close. `Balance` remains the right primitive for "we know the open exists, count nesting until close". Only its **failure** path uses `Synchronize`.

## The `Required` / `Optional` helpers

These are the per-token helpers that enforce the missing-token invariant.

```csharp
// Single-kind: either consume `kind` or emit MissingToken(kind) and synchronize.
protected internal (SyntaxToken token, SkippedContentSyntax? skipped) Required(
    SyntaxKind kind,
    RazorDiagnostic diagnostic,
    FollowSet recovery,
    SyntaxKind originatingLanguage,
    FollowSet outerFollow = default);

// Multi-kind: consume if the current token matches any acceptable kind;
// otherwise emit a missing token of acceptableKinds[0] and synchronize.
protected internal (SyntaxToken token, SkippedContentSyntax? skipped) Required(
    ImmutableArray<SyntaxKind> acceptableKinds,
    RazorDiagnostic diagnostic,
    FollowSet recovery,
    SyntaxKind originatingLanguage,
    FollowSet outerFollow = default);

// Consume `kind` if present; otherwise return null. No diagnostic.
protected internal SyntaxToken? Optional(SyntaxKind kind);
```

`Required` semantics:

- **Consume path** (`token.Kind == expected kind`): returns `(consumedToken, skipped: null)` and advances the tokenizer.
- **Missing path** (token missing or wrong kind): returns `(MissingToken(kind) with diagnostic attached, sync.Skipped)`. The caller must place both the missing token and the skipped-content node into its output in positional order (missing-token first, skipped-content second).

`Required` calling convention:

- `diagnostic` should be constructed with a **zero-length** `SourceSpan` at the current position (where the token was expected). Use a paired `_At` factory (see [Diagnostic factory `_At` convention](#diagnostic-factory-_at-convention)).
- The diagnostic is attached to the missing token. **Do NOT** also push the diagnostic to `Context.ErrorSink.OnError(...)` -- the token-attached diagnostic flows into `ErrorSink` automatically via tree building. Double-emit is the most common migration bug.
- `originatingLanguage` is the language tag for any `SkippedContentSyntax` produced by the recovery sync. Use `SyntaxKind.CSharpCodeBlock` from the C# parser and `SyntaxKind.MarkupBlock` from the HTML parser. (`SyntaxKind.RazorComment` is used by `ParseRazorComment`, which is language-agnostic.)
- `outerFollow` is the caller-supplied outer-language follow set, **already translated** to the callee's language (use `RecoveryFollowSets.ForCSharpCallee` / `ForHtmlCallee` at the cross-parser boundary). Defaults to `FollowSet.Empty` for non-cross-parser call sites.

`Optional` semantics: functionally identical to the older `GetOptionalToken(kind)`. Use the `Optional` spelling in new recovery-aware code for symmetry with `Required`.

### Use `Required` when

- The grammar says "this token must be here" (e.g. the closing `)` of an explicit expression body, the closing `*@` of a Razor comment, the tag-name `Text` token of an HTML start tag).

### Use `Optional` when

- The grammar says "this token may or may not be here" -- absence is not an error (e.g. whitespace between attributes, an optional self-closing `/` before `>`).

### Avoid (legacy)

- `AcceptUntil(SomeKind)` followed by `Context.ErrorSink.OnError(...)` -- this is the legacy pattern the redesign replaced. The skipped tokens become part of the next fat literal, and the diagnostic's span is wider than the actual fault. New code should not introduce this shape.

## How to write a new recovery-aware parser function

A worked example using the Stage 1.4 pilot (`ParseRazorComment` in `TokenizerBackedParser.cs`). The construct is `@*...*@`. The grammar:

```text
RazorComment := RazorCommentTransition RazorCommentStar [RazorCommentLiteral] RazorCommentStar RazorCommentTransition
```

The opening `@*` is always present (the parser only enters `ParseRazorComment` after asserting we are at `RazorCommentTransition`). The closing `*@` may be missing -- this is the case we recover from.

**Step 1: decide which tokens are `Required` vs `Optional`.**

The opening `@` and `*` are required, but their presence is asserted by precondition; no recovery is needed. The body literal (`RazorCommentLiteral`) is `Optional` -- an empty comment is grammatical. The closing `*` and `@` are `Required`. The diagnostic is "comment not terminated" (RZ1028).

**Step 2: pick a follow set for each `Required` call.**

For `ParseRazorComment`, the body literal already consumed everything up to the next `*@` or EOF. So by the time we call `Required(RazorCommentStar, ...)`, the cursor is either at `*` (consume path) or at EOF (missing path). The follow set is therefore `FollowSet.Empty` -- there is nothing to synchronize to. The assertion `Debug.Assert(endStarResult.skipped is null)` documents this.

**Step 3: pick the `originatingLanguage`.**

`ParseRazorComment` lives on `TokenizerBackedParser` and is called by both parsers. Pass `SyntaxKind.RazorComment` so any `SkippedContentSyntax` produced is tagged with the comment context.

**Step 4: build the diagnostic with the paired `_At` factory.**

```csharp
RazorDiagnosticFactory.CreateParsing_RazorCommentNotTerminated_At(CurrentStart)
```

The `_At` variant takes a `SourceLocation` (not a `SourceSpan`) and constructs a zero-width span. `CurrentStart` is the cursor position where the missing token would have been.

**Step 5: place the returned tokens into the output.**

```csharp
var endStarResult = Required(
    SyntaxKind.RazorCommentStar,
    RazorDiagnosticFactory.CreateParsing_RazorCommentNotTerminated_At(CurrentStart),
    FollowSet.Empty,
    SyntaxKind.RazorComment);
Debug.Assert(endStarResult.skipped is null,
    "ParseRazorComment expects no skipped tokens after RazorCommentLiteral; sync should hit EOF.");
endStar = endStarResult.token;
```

The tuple destructures into `(token, skipped)`. If `skipped` is non-null, the caller adds it to its output builder immediately after `token`:

```csharp
expressionBuilder.Add(token);
if (skipped is not null)
{
    expressionBuilder.Add(skipped);
}
```

(Order matters: missing-token first, skipped-content second, so source positions stay monotonic.)

**Step 6: handle the cascade case.**

If a `Required` fails, the next `Required` in the same function will usually also fail (the user is mid-typing). Avoid emitting a duplicate diagnostic in the cascade case. The `ParseRazorComment` pattern: if `endStar.IsMissing`, emit `endTransition` as a plain `MissingToken` without re-attaching the same diagnostic.

```csharp
if (!endStar.IsMissing)
{
    var endTransitionResult = Required(
        SyntaxKind.RazorCommentTransition,
        RazorDiagnosticFactory.CreateParsing_RazorCommentNotTerminated_At(CurrentStart),
        FollowSet.Empty,
        SyntaxKind.RazorComment);
    endTransition = endTransitionResult.token;
}
else
{
    // endStar was already missing; a second copy on endTransition would dedupe
    // to the same RZ1028 entry (same descriptor, same zero-width span at the
    // same EOF cursor). Emit a plain missing token without re-attaching.
    endTransition = SyntaxFactory.MissingToken(SyntaxKind.RazorCommentTransition);
}
```

### Worked example two: cross-parser case (`ParseExplicitExpressionBody`)

For a cross-parser case, see `CSharpCodeParser.ParseExplicitExpressionBody` (the `@(...)` body). It demonstrates:

- A failed `Balance` call followed by `Synchronize` with the outer follow set (`_outerFollow` carries the translated HTML-side follow set, threaded from the cross-parser handoff).
- Inserting the recovered `SkippedContentSyntax` into the expression block AFTER the literal flush, so source positions remain monotonic.
- Building a `MissingToken(SyntaxKind.RightParenthesis)` with `CreateParsing_ExpectedEndOfBlockBeforeEOF_At(CurrentStart, ...)` -- the paired `_At` factory produces a zero-width span at the current cursor (the first un-matched position after `Balance` failure rewinds), rather than the legacy 1-char span at the opening `(`.

See `CSharpCodeParser.cs` around the `if (!success)` branch of `ParseExplicitExpressionBody`.

### Checklist for adding a new call site

1. Is the failure diagnosable as "expected token X here"? -> use `Required(X, _At factory, ...)`.
2. Is the token grammatically optional? -> use `Optional(X)`.
3. Does the recovery need to bail to the outer parser? -> thread `outerFollow` from the cross-parser handoff (already translated via `RecoveryFollowSets.For{CSharp,Html}Callee`).
4. Is there a named follow set in `RecoveryFollowSets` that fits? -> use it. Otherwise consider adding one if the rationale is non-trivial or it is reused.
5. Diagnostic constructed with a zero-width span? -> use the paired `_At` factory; if none exists yet, add one (see next section).
6. Diagnostic NOT also pushed to `Context.ErrorSink.OnError(...)`? -> verify by reading the function end-to-end.
7. `SkippedContentSyntax` placed into the output AFTER the missing token (and after any marker / accepted-token flush)? -> source positions must stay monotonic.

## The `SkippedContentSyntax` node

Defined in `Syntax.xml`:

```xml
<Node Name="SkippedContentSyntax" Base="RazorSyntaxNode">
    <Kind Name="SkippedContent" />
    <Field Name="SkippedTokens" Type="SyntaxList&lt;SyntaxToken&gt;" />
    <Field Name="OriginatingLanguage" Type="SyntaxKind" />
</Node>
```

Producer:

- `TokenizerBackedParser.Synchronize` is the only producer. It is called by `Required` on the failure path, and directly by call sites that have already diagnosed the failure (e.g. after a failed `Balance`).

Consumers (and how each handles it):

| Consumer | Behaviour |
|----------|-----------|
| **Codegen** (`DefaultRazorIntermediateNodeLoweringPhase`, `CodeRenderingContext`) | Ignores `SkippedContentSyntax`. The contained tokens have no generated counterpart -- they are recovery debris, not source the user intended to compile. |
| **Lowering** (Stage 5.0 IR phase) | Drops `SkippedContentSyntax` on the floor; nothing flows into the IR. |
| **Formatter** | Preserves the contained tokens byte-for-byte -- the user typed those characters, formatting must not eat them. The formatter audit and regression guards landed in Stage 5.5 of the redesign. |
| **LSP classification / completion / hover** (Stage 5.6) | Classifies the contained tokens as "unknown". For completion and hover inside skipped content, dispatches to the language provider indicated by `OriginatingLanguage`. |
| **Tree walkers / `FindToken` / `SyntaxNavigator`** (Stage 5.4) | Treats `SkippedContentSyntax` like any other `RazorSyntaxNode`; the contained tokens are reachable as children. |

If you are writing a new consumer that walks the tree, decide explicitly whether `SkippedContentSyntax` should be visited (preserve / format / classify) or skipped (codegen / lowering / IR transforms). The `OriginatingLanguage` field is the discriminator if you need language-context inside the skipped region.

## Diagnostic factory `_At` convention

`RazorDiagnosticFactory.cs` (and `ComponentDiagnosticFactory.cs`) carry pairs of factories for diagnostics used in recovery contexts:

| Original factory | Paired `_At` factory |
|------------------|----------------------|
| `CreateParsing_ExpectedEndOfBlockBeforeEOF(SourceSpan, ...)` | `CreateParsing_ExpectedEndOfBlockBeforeEOF_At(SourceLocation, ...)` |
| `CreateParsing_RazorCommentNotTerminated(SourceSpan)` | `CreateParsing_RazorCommentNotTerminated_At(SourceLocation)` |
| `CreateParsing_DirectiveMustHaveValue(SourceSpan, ...)` | `CreateParsing_DirectiveMustHaveValue_At(SourceLocation, ...)` |
| `CreateParsing_UnfinishedTag(SourceSpan, ...)` | `CreateParsing_UnfinishedTag_At(SourceLocation, ...)` |
| ...and similar pairings for the other recovery-site diagnostics. |

The convention:

- The original factory takes a `SourceSpan` and is what legacy code paths used; it remains for any pre-existing non-recovery call site. **Do not remove it** if other call sites still reference it.
- The `_At` factory takes a `SourceLocation` and constructs a zero-width `SourceSpan` internally:

  ```csharp
  public static RazorDiagnostic CreateParsing_ExpectedEndOfBlockBeforeEOF_At(SourceLocation location, ...)
      => RazorDiagnostic.Create(Parsing_ExpectedEndOfBlockBeforeEOF, new SourceSpan(location, contentLength: 0), ...);
  ```

- Both members of a pair share the **same** `RazorDiagnosticDescriptor` instance (same RZ ID, same message template). The `_At` factory is a span-only variant; it does not allocate a new diagnostic ID.

When to use which:

- **Use the `_At` variant** from a `Required` call site, or any other recovery code path that attaches a diagnostic to a `MissingToken` (or otherwise wants a zero-width span at the current cursor).
- **Use the non-`_At` variant** from a legacy code path that genuinely needs a wider span (e.g. underlining a malformed token that was actually consumed), or from a non-recovery call site that already has a `SourceSpan` to hand.

Adding a new `_At` variant: copy the original factory's signature, change the `SourceSpan` parameter to `SourceLocation`, and pass `new SourceSpan(location, contentLength: 0)` into `RazorDiagnostic.Create`. Add a brief comment noting which factory it pairs with and that it shares the descriptor (and therefore the RZ ID). If you instead want a genuinely new diagnostic (different message), allocate the next free RZ ID following the procedure in the historical plan -- the current per-range maxima are tracked in `docs/plans/ErrorRecovery/razor-recovery-redesign-plan-state.md` under "Diagnostic IDs allocated".

## How we got here

This recovery model replaced an older "absorb everything to the next `<` or newline" approach. The redesign was executed in stages between Stage 0 (foundation: corpus, snapshot harness, feature flag, `SkippedContentSyntax` node) and Stage 6 (flip the flag, delete the legacy paths, polish). The motivating bug was [dotnet/razor#10383](https://github.com/dotnet/razor/issues/10383) -- a typo like `<button @onclick="">` produced a "wall of red" diagnostic underline that covered roughly half the file because of three independent contributors (parser absorbed too much, codegen emitted invalid C#, source mapping was wider than the offending value).

The complete record lives under [`plans/ErrorRecovery/`](plans/ErrorRecovery/):

- `razor-parser-analysis.md` -- the pre-redesign architectural deep-dive describing the two-parser co-routine architecture and the specific reasons recovery was poor.
- `razor-recovery-redesign-plan.md` -- the staged execution plan, including the "Big Design Decisions" section that pre-resolves the architectural choices (skipped content as a tree node rather than trivia; missing-token invariant via `Required`; follow sets per parser function per language; cross-language translation at the handoff; etc.).
- `razor-recovery-redesign-plan-state.md` -- the per-stage execution record, including diagnostic IDs allocated, baseline triages, and notes on each stage's exit criteria.

Those files are historical: they document the design rationale and the migration steps, but the contract this document describes is the live one. If you find a discrepancy between this doc and the plan files, this doc is correct (the plan files are not updated post-execution); please update this doc to track reality.
