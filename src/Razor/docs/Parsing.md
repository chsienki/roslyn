# Introduction

This doc aims to keep a list of decisions that have been made around how razor syntax is parsed into a syntax tree.

## Whitespace handling

Whitespace handling is currently differently parsed depending on the chosen emit strategy (runtime or design time).

When in DesignTime whitespace between a CSharp and HTML node is generally parsed as an HTML node, whereas in Runtime the whitespace is parsed as part of a CSharp node. This ensures that at runtime arbitrary whitespace isn't incorrectly emitted as part of the HTML, but in design time the editor will only identify the actual code portion as being CSharp.

An example of this can be seen here: <https://github.com/dotnet/razor/blob/9f10012f7bbee0c17be26de048aee3e5adbc6c80/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Legacy/CSharpCodeParser.cs#L743>

As part of the transition to use only runtime code generation, we had to make some subtle changes to the parsing of whitespace to ensure that the existing behavior in the editor continues to function as before.

Specifically we changed the parsing of trailing whitespace of razor code block directives (i.e. `@code`, `@function` and `@section`). Previously the whitespace was attached to a meta node that included the closing `}`

Using `^` to indicate whitespace:

```csharp
@code {
    // code
}^^^

```

This would previously be conceptually parsed as something like:

```text
CSharpCode
    RazorDirective
        CSharpTransition
        RazorDirectiveBody
            RazorMetaCode
                Identifier
            CSharpCode
                ...
            RazorMetaCode
                Literal: }
                Literal: ^^^\r\n
```

Thus when looking at the length of the RazorDirective, it includes the `^^^\r\n`. This causes issues with editor features like code folding. The user only want to fold the directive, not the directive and the following new line. (see <https://github.com/dotnet/razor/issues/10358>)

Instead, we now break the trailing whitespace into its own RazorMetaCode node, which is not a part of the directive itself. Conceptually something like:

```text
CSharpCode
    RazorDirective
        CSharpTransition
        RazorDirectiveBody
            RazorMetaCode
                Identifier
            CSharpCode
                ...
            RazorMetaCode
                Literal: }
    RazorMetaCode
        Literal: ^^^\r\n
```

In this way we keep the whitespace as belonging to the overall CSharpCode node, but don't make it part of the directive itself, ensuring the editor sees the correct length for the directive.

We apply a very similar fix to `@using` directives, to ensure that the newline is treated as metacode of the overall block, rather than being a part of the `using` itself.

## Error recovery: missing tokens and `SkippedContentSyntax`

Razor's two parsers (`HtmlMarkupParser`, `CSharpCodeParser`) share a single character cursor and historically recovered from syntax errors by absorbing everything from the point of failure to the next recognisable terminator (typically `<` or a newline) into a single fat literal node, then attaching one diagnostic to the start of the construct. That is what produces the "wall of red" experience for small typos like `<button @onclick="">`. The parsers were reworked to use a Roslyn-style recovery model with two invariants. The redesign is described in `parser-recovery.md`; the rest of this section names the two tree shapes a reader of the syntax tree now needs to understand.

### Missing-token invariant

Every token the grammar expects but does not find is emitted as a zero-width `SyntaxToken` with `IsMissing == true`, at the precise position where the token was expected (typically `CurrentStart` at the cursor). The diagnostic for the failure is attached to that missing token; it is **not** also pushed to `ErrorSink` (the token-attached diagnostic flows into `ErrorSink` automatically via tree building). This means:

- The shape of the tree is the same on success and failure -- every child that the schema requires is present; the failure case just has zero-width tokens with `IsMissing` set.
- Each missing token carries one diagnostic with a zero-length `SourceSpan` at the insertion point, so IDE squiggles land on a single character position rather than spanning the whole construct.
- Source mapping for a missing token is zero-width at the insertion point (a `MissingToken` has no generated counterpart on its own; codegen Stage 5.1 substitutes a safe placeholder where the missing token would otherwise produce invalid C#).

To check whether a token came from this recovery path, test `token.IsMissing`.

### `SkippedContentSyntax`

`SkippedContentSyntax` (defined in `Syntax.xml`) is a `RazorSyntaxNode` that holds tokens the parser had to skip past while recovering -- i.e. tokens that are present in the source but had no grammatical home. It has two fields:

```text
SkippedContentSyntax
    SkippedTokens : SyntaxList<SyntaxToken>
    OriginatingLanguage : SyntaxKind
```

- `SkippedTokens` is the contiguous run of tokens the recovery synchronizer absorbed. Source positions are preserved because the original tokens are still in the tree -- they are just inside a `SkippedContentSyntax` wrapper instead of inside whatever node they would have landed in had parsing succeeded.
- `OriginatingLanguage` records which tokenizer was active when the skip happened (typically `SyntaxKind.CSharpCodeBlock` or `SyntaxKind.MarkupBlock`, or `None` for document-level). LSP completion / classification (Stage 5.6 of the redesign) uses this to dispatch back to the appropriate language provider when the cursor lands inside skipped content.

`SkippedContentSyntax` appears wherever recovery skipped tokens to find a sync point. The producer is always `TokenizerBackedParser.Synchronize`; the parser function that called it inserts the returned node into its own output in positional order, immediately after the missing token (or after the failed construct's last good child).

Downstream consumers treat `SkippedContentSyntax` as semantically inert:

- **Codegen** ignores it (the skipped tokens have no generated counterpart -- they are recovery debris, not source the user intended to compile).
- **Lowering** drops it on the floor.
- **Formatter** preserves it byte-for-byte (the user typed those characters; formatting must not eat them).
- **LSP** classifies the contained tokens as "unknown" but dispatches completion / hover into the language indicated by `OriginatingLanguage`.

If you are walking the tree and want to skip recovery debris, filter out `SkippedContentSyntax` nodes (and their descendants).

For the full design of how `Required` / `Optional` / `Synchronize` / `FollowSet` produce these shapes -- and the worked example a contributor needs to write a new recovery-aware parser function -- see [`parser-recovery.md`](parser-recovery.md).