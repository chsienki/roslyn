# SkippedContent consumer audit (Stage 0.5)

> Sister artifact of `razor-recovery-redesign-plan.md` (Stage 0.5).
> Lists every place that walks tokens and indicates whether it needs
> updating to handle the new `SkippedContentSyntax` node introduced
> in Stage 0.4. Phase 5 (downstream consumers) executes this checklist.

`SkippedContentSyntax` was added in Stage 0.4 and holds tokens that
were skipped while resyncing to a follow set during enhanced-mode
parser recovery. It carries an `OriginatingLanguage` field
(`CSharpCodeBlock` / `MarkupBlock` / `None`) so completion providers
and similar consumers can dispatch to the right language at the
skipped-region cursor (see plan Big Design Decision #10 and Stage 5.6).

For every consumer below, mark one of:

- **(a)** Needs to ignore `SkippedContentSyntax` -- the node should
  not contribute output and the walker should treat it as a no-op for
  this consumer's purpose.
- **(b)** Needs to skip-and-warn -- if a `SkippedContentSyntax` shows
  up where this consumer expects only specific node kinds, that's a
  red flag worth flagging via an assert or a structured diagnostic
  (not user-facing).
- **(c)** Unaffected -- the consumer already handles "unknown node
  kinds" correctly (e.g., generic syntax walkers that visit
  children).

The list is best-effort: built from grep + read-throughs against the
state of the repo at branch `razor-recovery-stage-0`. Stage 5
re-verifies and executes.

---

## Output-as-X helpers (the producer side; trivially unaffected)

Stage 0.5 doesn't change these but lists them so reviewers know the
producer side is fully accounted for. None of these produce
`SkippedContentSyntax`; they continue to produce literals from
accepted tokens. `Synchronize` is the new producer that emits
`SkippedContentSyntax` directly into the parser builder (see
Stage 1.1).

| Method | File:line | Category | Notes |
|---|---|---|---|
| `OutputAsMarkupLiteralRequired` | `TokenizerBackedParser.cs:616` | (c) unaffected | producer side |
| `OutputAsMarkupLiteral` | `TokenizerBackedParser.cs:627` | (c) unaffected | producer side |
| `OutputAsMarkupEphemeralLiteral` | `TokenizerBackedParser.cs:638` | (c) unaffected | producer side |
| `OutputAsMetaCode` | `TokenizerBackedParser.cs:649` | (c) unaffected | producer side |
| `OutputTokensAsStatementLiteral` | `CSharpCodeParser.cs:2842` | (c) unaffected | producer side |
| `OutputTokensAsExpressionLiteral` | `CSharpCodeParser.cs:2853` | (c) unaffected | producer side |
| `OutputTokensAsEphemeralLiteral` | `CSharpCodeParser.cs:2864` | (c) unaffected | producer side |
| `OutputTokensAsUnclassifiedLiteral` | `CSharpCodeParser.cs:2875` | (c) unaffected | producer side |

---

## Tree walkers / `GetContent()` consumers

`RazorSyntaxNode.GetContent()` and `GreenNode.ToFullString()` produce
the literal source text from a tree, including any descendant token
content. Most of these callers want the original source verbatim,
which is also what skipped content represents -- so the **default
disposition is (c) unaffected**: skipped content's tokens are part
of the source and naturally flow through `GetContent` like any other
tokens.

Where it matters: consumers that build IR / generated code from
`GetContent` (lowering, codegen) should NOT include skipped-content
text as if it were valid code. Those are tagged (a).

| File | Approx call count | Category | Notes for Stage 5 |
|---|---|---|---|
| `DefaultRazorIntermediateNodeLoweringPhase.cs` | 41 | **(a)** must ignore | Lowering produces IR from parse tree. Skipped content should NOT become IR `IntermediateToken`s. Stage 5.0 audit work. |
| `TagHelperBlockRewriter.cs` | 10 | **(a)** must ignore | Per Stage 5.2: rewriter walks `MarkupStartTag.Attributes`. If a `SkippedContentSyntax` appears between attributes, treat as if absent for tag-helper-attribute binding purposes. Stage 5.2. |
| `TagHelperParseTreeRewriter.cs` | 4 | **(a)** must ignore | Same reasoning as above. Stage 5.2. |
| `ImplicitExpressionEditHandler.cs` | 8 | **(b)** skip-and-warn | Legacy in-place editor handler. Out-of-scope per plan limitations #5; will be touched only if it intersects recovery directly. |
| `MarkupTagHelperDirectiveAttributeSyntax.cs` | 4 | (c) unaffected | Generated/manual extension on the syntax node; reads its own fields. |
| `MarkupMinimizedTagHelperDirectiveAttributeSyntax.cs` | 4 | (c) unaffected | Same. |
| `SyntaxExtensions.cs` | 6 | (c) unaffected | Generic extension helpers. |
| `SyntaxSerializer.cs` | 1 | (c) unaffected | Test-time serializer; faithful representation including any new node kinds. |
| `CodeBlockEditHandler.cs` | 1 | **(b)** skip-and-warn | Same as `ImplicitExpressionEditHandler` reasoning. |
| `SpanEditHandler.cs` | 1 | **(b)** skip-and-warn | Same. |
| `NamespaceComputer.cs` | 1 | (c) unaffected | Walks namespace info, not source content. |
| `RazorCodeDocumentExtensions.cs` | 3 | (c) unaffected | Document-level helpers. |
| `SourceChange.cs` | 2 | (c) unaffected | Source-position helper. |

---

## IR-level token consumers (per plan Stage 5.0 audit)

| File:line | Category | Notes for Stage 5 |
|---|---|---|
| `DefaultRazorIntermediateNodeLoweringPhase.cs` (attribute merging helpers around lines 682-889) | **(a)** must ignore | Per plan Stage 5.0. Also: recognise the "missing C# attribute value" tree shape `GenericBlock([CSharpExpressionLiteral([MissingToken])])` and propagate a missing-value marker to `ComponentAttributeIntermediateNode` / `TagHelperPropertyIntermediateNode`. |
| `DefaultTagHelperResolutionPhase.LegacyTagHelperResolver.cs` `LowerBoundLegacyAttributeValue` | **(a)** must ignore | Per plan Stage 5.0. Currently produces synthetic empty `CSharpIntermediateToken("")` for empty bound values; Stage 5.0 changes this to a tagged missing-value marker per the spike findings. |
| `DefaultTagHelperResolutionPhase.ComponentTagHelperResolver.cs` (locate via `Get-ChildItem -Recurse -Filter`) | **(a)** must ignore | Component pipeline counterpart. The `@onclick=""` (issue #10383) case flows through this path. Stage 5.0 spike confirms exact site. |
| `Components/ComponentEventHandlerLoweringPass.cs` `RewriteUsage` (lines ~161-169) | **(a)** must ignore (and rewrite the bail-out) | Today's early bail-out `if (original.Length == 0) return node;` silently drops empty bound values. Stage 5.0 changes this to emit a placeholder per the Stage 5.1 matrix. |

---

## Source-mapping (Stage 5.3)

`CodeRenderingContext.cs` creates `SourceMapping` ranges as codegen
writes generated C#. With `SkippedContentSyntax`, source-mapping
ranges that would cross a skipped region should be SPLIT at the
boundary so any C# diagnostic that maps back lands in a narrow
region, not a wide one. This is part of Stage 5.3 and not a simple
"ignore" -- the consumer needs explicit awareness of the new node.

| File | Category | Notes for Stage 5 |
|---|---|---|
| `CodeGeneration/CodeRenderingContext.cs` | **(a)** must ignore + split mappings | Stage 5.3. |

---

## LSP / IDE consumers (Stage 5.6)

Listed in the plan's Stage 5.6.0 anchor-class spike step. They are
**not** enumerated here -- the spike fills them in. Initial guidance
per plan:

- Classification (semantic tokens): classify `SkippedContentSyntax`
  visually distinct from real code (e.g., "comment" or "unknown").
- Completion: dispatch on `OriginatingLanguage` per Big Design
  Decision #10.
- Hover / go-to-definition: missing tokens have no hover.

---

## Summary

| Category | Count |
|---|---|
| (a) must ignore | ~9 files |
| (b) skip-and-warn | 3 files (in-place editor handlers; deferred) |
| (c) unaffected | ~10 files |
| Other (codegen / mappings / LSP) | 3 areas, expanded in Stages 5.0-5.6 |

The most critical sites for unblocking the wall-of-red fix in
issue #10383 are:

1. `ComponentEventHandlerLoweringPass.RewriteUsage` (line ~161)
2. `DefaultTagHelperResolutionPhase.LegacyTagHelperResolver.LowerBoundLegacyAttributeValue`
3. The component-pipeline counterpart in
   `ComponentTagHelperResolver.cs`
4. `CodeRenderingContext.cs` source-mapping split logic

All four are owned by Stage 5.0 / 5.1 / 5.3.
