# Razor parser architecture & error-recovery analysis

> **About this document.** Architectural deep-dive on Razor's current
> two-parser architecture and why its error recovery is poor today.
> Companion to the redesign plan at
> `razor-recovery-redesign-plan.md` in this directory.
> Motivating bug: [dotnet/razor#10383](https://github.com/dotnet/razor/issues/10383).

Scope: `src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Legacy/*`
Motivating bug: dotnet/razor#10383 (`@onclick=""` produces a "wall of red" half the file long).

This analysis exists to *understand* the current design well enough that a future
refactor can pick the right improvement; it is **not** itself a proposal/fix.

---

## 1. Top-level shape: co-routine parsers over a shared character stream

`RazorParser.Parse(source)` constructs three things and runs them once:

```
RazorParser.Parse
  ParserContext         -- owns the shared SeekableTextReader + ErrorSink stack
  CSharpCodeParser      -- TokenizerBackedParser<CSharpTokenizer>
  HtmlMarkupParser      -- TokenizerBackedParser<HtmlTokenizer>
  codeParser.HtmlParser = markupParser
  markupParser.CodeParser = codeParser
  markupParser.ParseDocument()   -- HTML is the outer mode
```

So the document is always entered from the HTML side. The two parsers know
about each other (circular reference) and call into one another whenever they
detect a transition.

### 1.1 Shared character stream

Both `HtmlMarkupParser` and `CSharpCodeParser` derive from
`TokenizerBackedParser<TTokenizer>` (a generic over `HtmlTokenizer` /
`CSharpTokenizer`). Each owns its own `TokenizerView<T>`, which wraps a
`Tokenizer`, which reads from **the same** `SeekableTextReader` exposed by
`ParserContext.Source`.

`SeekableTextReader` is a `TextReader` over a Roslyn `SourceText` with a
single mutable `Position`. So there is exactly one character cursor for the
whole document, shared by both languages.

Implication: switching languages is *not* "swap a token stream", it is
"the other tokenizer starts emitting tokens from the current character offset".

### 1.2 The PutBack mechanism

When a parser is done with a region, it typically calls
`PutCurrentBack()` / `PutBack(token)`. `TokenizerView<T>.PutBack` simply does:

```csharp
public void PutBack(SyntaxToken token)
    => Reset(Source.Position - token.Content.Length);
```

i.e. it **moves the shared character cursor backwards** by the token's character
length, throws away the cached `Current`, and tells its own `Tokenizer` to
reset to the new position. Lookahead in `Lookahead(int)` and `LookaheadUntil`
works the same way: tokenize, save tokens, then PutBack them all in reverse
order.

Consequences for recovery:

* Putback is *destructive* of nothing because tokens are pure value objects
  derived from `Content` characters in the buffer. There is no token stream
  to corrupt.
* But it does throw away any tokenizer-internal state (mode, "is this trivia
  leading or trailing", etc.). Both `RoslynCSharpTokenizer` and
  `NativeCSharpTokenizer` re-derive that on the next `NextToken()`.
* The character cursor is shared, so cross-parser PutBack is just: "I tokenized
  too far in my language, rewind so the other language can re-tokenize from
  here".

### 1.3 The cross-parser handoff

CSharp -> HTML handoff: `CSharpCodeParser.OtherParserBlock`:

```csharp
private void OtherParserBlock(in SyntaxListBuilder<RazorSyntaxNode> builder)
{
    var wasNested = IsNested;
    IsNested = false;
    EndingBlock();                       // tokenizer hook
    using (PushSpanContextConfig())
    {
        htmlBlock = HtmlParser.ParseBlock();
    }
    builder.Add(htmlBlock);
    InitializeContext();
    StartingBlock();                     // tokenizer hook
    IsNested = wasNested;
    NextToken();
}
```

HTML -> CSharp handoff happens whenever HTML's `GetParserState` returns
`CodeTransition`, `RazorComment`, etc. The HTML parser calls
`CodeParser.ParseBlock()` to consume one Razor construct, then resumes.

`StartingBlock()` / `EndingBlock()` give each tokenizer a chance to sync. For
example `RoslynCSharpTokenizer.StartingBlock()` calls
`_roslynTokenParser.SkipForwardTo(Source.Position)` to align Roslyn's
`SyntaxTokenParser` with the new cursor; `EndingBlock()` rewinds it so the
trailing trivia of the last token belongs to the other parser, not C#.

So the *protocol* is:

1. The active parser detects it should hand off.
2. It calls into the other parser (`ParseBlock()` / `OtherParserBlock`).
3. Inside the call, the other parser runs to completion of "its" construct.
4. Before returning, it `PutCurrentBack()` so the next character is unread.
5. The original parser calls `NextToken()` and continues.

There is no peer review of where the other parser actually stopped; whoever
calls "back into" the other parser trusts that the cursor is at a sensible
character.

---

## 2. Token / syntax-tree shape

The legacy parser produces a tree of `RazorSyntaxNode`s defined in
`src/Razor/src/Compiler/Microsoft.CodeAnalysis.Razor.Compiler/src/Language/Syntax/Syntax.xml`.
Notable shapes:

* `RazorDocumentSyntax`
* `MarkupBlockSyntax`, `MarkupElementSyntax(StartTag?, Body, EndTag?)`
* `MarkupStartTagSyntax(OpenAngle, Bang?, Name, Attributes, ForwardSlash?, CloseAngle)`
* `MarkupAttributeBlockSyntax(NamePrefix?, Name, NameSuffix?, EqualsToken, ValuePrefix?, Value?, ValueSuffix?)`
* `MarkupTagHelperElementSyntax`, `MarkupTagHelperStartTagSyntax`,
  `MarkupTagHelperAttributeSyntax`, `MarkupTagHelperDirectiveAttributeSyntax`
* `CSharpCodeBlockSyntax`, `CSharpStatementBodySyntax(OpenBrace, CSharpCode, CloseBrace)`
* `CSharpStatementLiteralSyntax(LiteralTokens, ChunkGenerator?, EditHandler?)`
* `CSharpExpressionLiteralSyntax(LiteralTokens, ChunkGenerator?, EditHandler?)`
* `RazorMetaCodeSyntax(MetaCode, ChunkGenerator?, EditHandler?)`

The tag-helper variants (`MarkupTagHelperElementSyntax`,
`MarkupTagHelperAttributeSyntax`, etc.) are not produced by the initial parse.
They are produced by `TagHelperParseTreeRewriter` in a **post-parse rewrite**
that re-shapes plain `MarkupElement`s when bindings match. So parse-time
recovery decisions become the input to the tag-helper rewriter, which then
inherits whatever shape the parser produced.

### 2.1 Two crucial syntactic properties absent from Razor

Compared to Roslyn, Razor's syntax tree is missing the two features that make
Roslyn's recovery feel narrow and local:

1. **Missing tokens.** There is a `SyntaxFactory.MissingToken(kind)` and it is
   used in a handful of places (RightBrace, RightParenthesis, RazorCommentStar,
   tag names) -- so the concept exists -- but missing-token usage is the
   exception, not the rule. There is no parser-wide invariant that "when an
   expected token is absent, a zero-width missing token is emitted at the
   exact position with the diagnostic attached".
2. **Skipped-tokens trivia.** Razor `SyntaxToken` has no leading / trailing
   trivia list at all -- whitespace and newlines are first-class tokens
   (`SyntaxKind.Whitespace`, `SyntaxKind.NewLine`). There is therefore **no
   mechanism for absorbing "garbage" tokens between expected tokens** while
   preserving the syntactic shape. Garbage is always folded into a
   `CSharpStatementLiteral` / `CSharpExpressionLiteral` / `MarkupTextLiteral`
   / `MarkupMiscAttributeContent` node, i.e. it becomes part of "the normal
   tree", not a side-band.

These two absences are arguably the root cause of "wall of red" symptoms:
recovery always produces *one fat literal* covering everything it skipped,
which then becomes a single source mapping (see Section 5).

---

## 3. How recovery is actually done today

Recovery is **per-construct and ad hoc**. There is no central recovery loop,
no follow-set/synchronization-token framework, and no "panic mode" abstraction.

### 3.1 The Balance method

`CSharpCodeParser.Balance(builder, mode, left, right, start)` is the workhorse
for `( ) [ ] { }`-style ranges. It maintains a depth counter and reads tokens
until depth==0 or EOF / EOL (with `StopAtEndOfLine`). On failure it:

* If `BacktrackOnFailure` is set, **resets the tokenizer to `startPosition`
  and re-tokenizes from there.** All tokens it speculatively consumed are
  discarded; the caller continues as if nothing had been consumed past the
  opening bracket.
* If `NoErrorOnFailure` is set, no error is emitted.
* Otherwise, it `Accept`s everything it consumed into the current literal and
  raises `Parsing_ExpectedCloseBracketBeforeEOF`.

`BalancingModes` is a small `[Flags]` enum: `None`, `BacktrackOnFailure`,
`NoErrorOnFailure`, `AllowCommentsAndTemplates`, `AllowEmbeddedTransitions`,
`StopAtEndOfLine`. It is the closest thing the parser has to a recovery
configuration.

### 3.2 ParseStandardStatement -- the typical recovery shape

`CSharpCodeParser.ParseStandardStatement` is representative:

```csharp
while (!EndOfFile)
{
    var bookmark = CurrentStart.AbsoluteIndex;
    using var read = new PooledArrayBuilder<SyntaxToken>();
    ReadWhile(token => token.Kind is not (Semicolon or RazorCommentTransition
        or Transition or LeftBrace or LeftParenthesis or LeftBracket
        or RightBrace or Keyword), ref read.AsRef());

    if (At(LeftBrace) || At(LeftParenthesis) || At(LeftBracket))
    {
        Accept(in read);
        if (!TryBalanceBlock(builder)) return;  // -> AcceptUntil(<, })
    }
    else if (At(Transition)) { ... }
    else if (At(RazorCommentTransition)) { ... }
    else if (At(Semicolon)) { Accept(in read); AcceptAndMoveNext(); return; }
    else if (At(RightBrace)) { Accept(in read); return; }
    else if (At(Keyword)) { ... }
    else
    {
        _tokenizer.Reset(bookmark);
        NextToken();
        AcceptUntil(SyntaxKind.LessThan, SyntaxKind.LeftBrace,
                    SyntaxKind.RightBrace);   // panic to <, {, }
        return;
    }
}
```

The recovery strategy is essentially:

* If something balanced went wrong, `AcceptUntil(<, })`. Everything in between
  becomes one giant `CSharpStatementLiteral`.
* `AcceptUntil(LessThan)` is the canonical fallback: "the next HTML-looking
  thing is probably where I should give up". You see this pattern in 4+ places
  in `CSharpCodeParser.cs` (lines 465, 572, 1120, 1134).

### 3.3 ParseConditionalBlock / TryParseCondition

```csharp
private bool TryParseCondition(in SyntaxListBuilder<RazorSyntaxNode> builder)
{
    if (At(LeftParenthesis))
    {
        var complete = Balance(builder, BacktrackOnFailure | AllowCommentsAndTemplates);
        if (!complete) AcceptUntil(NewLine);   // "give up at end of line"
        else TryAccept(RightParenthesis);
        return complete;
    }
    return true;
}
```

The tests `TerminatesIfBlockAtEOLWhenRecoveringFromMissingCloseParen` etc.
in `CSharpErrorTest.cs` show what this looks like: `@if(foo bar\nbaz`
becomes "if with an unbalanced paren, absorb up to EOL, then quit".

### 3.4 HtmlMarkupParser recovery

The HTML side has its own tag-balancing recovery, `TryRecoverStartTag`:

* `_tagTracker` is a `Stack<TagTracker>` of open `<tag>` scopes within the
  current block.
* On end-tag mismatch, it walks up the stack looking for a matching open;
  if found, all intermediate scopes are flushed as malformed elements.
* If not found, the end tag becomes a `MarkupElement(startTag: null, body: [], endTag: ...)`,
  i.e. an element with no start tag at all.

Two related properties:

* The tag stack is *replaced* when entering a code block or razor block
  (see `ParseBlock()` / `ParseRazorBlock()`): the outer stack is saved, a
  fresh stack is used inside, and the outer one is restored on exit. This
  intentionally prevents `<div>@{ </div> }` from matching up. It is by
  design, but it means imbalances inside a code block cannot use surrounding
  context to recover.
* `ParseMiscAttribute` is the recovery routine for "I expected an attribute
  but got garbage": it reads until `<`, `>`, `/`, or a quote.

### 3.5 ParseExplicitExpressionBody -- typical "give up at <" recovery

```csharp
var success = Balance(builder,
    BacktrackOnFailure | NoErrorOnFailure | AllowCommentsAndTemplates,
    LeftParenthesis, RightParenthesis, block.Start);
if (!success)
{
    AcceptUntil(LessThan);
    Context.ErrorSink.OnError(Parsing_ExpectedEndOfBlockBeforeEOF(...));
}
```

When `@(...)` can't be balanced, the parser eats everything up to the next
`<` and reports one diagnostic. Everything eaten becomes one
`CSharpExpressionLiteral`.

### 3.6 ParseAttribute / ParseRemainingAttribute

For HTML attribute parsing, when the value is conditional and looks like
C#-bearing (`OtherParserBlock` is called in `ParseConditionalAttributeValue`),
the HTML parser hands off to the C# parser. If the C# parser produces a
zero-content expression (e.g. `""`), the result is a `MarkupDynamicAttributeValue`
with an empty inner block. That ultimately produces an empty C# expression
chunk, which is what feeds the "wall of red" via codegen + Roslyn (Section 5).

### 3.7 What never happens

* No parser function asks "given the construct I'm in, what is the set of
  tokens I can safely synchronize to?"
* No parser function emits a "missing X here" *while continuing* to parse
  the surrounding construct as if X had been there.
* No node type is "this region is an error production" -- recovery is always
  expressed by inflating one of the existing literal/MiscAttribute nodes.

---

## 4. Tokenization

### 4.1 Hand-written tokenizers

* `HtmlTokenizer`, `NativeCSharpTokenizer` are state-machine tokenizers
  inheriting from `Tokenizer`. They read one character at a time from
  `SeekableTextReader`, push into a `StringBuilder`, and emit a Razor
  `SyntaxToken(Kind, Content)`.
* They are mode-aware: `StartingBlock()` / `EndingBlock()` are called by the
  parser when switching parsers; `DirectiveCSharpTokenizer` /
  `DirectiveHtmlTokenizer` / `FirstDirectiveCSharpLanguageCharacteristics`
  are variants used while parsing leading directives in `_Imports.cshtml`-style
  files (`Options.ParseLeadingDirectives`).

### 4.2 RoslynCSharpTokenizer

There is a newer tokenizer (`RoslynCSharpTokenizer`) used when
`Options.UseRoslynTokenizer` is set. It wraps Roslyn's
`SyntaxTokenParser` (`CodeAnalysis.CSharp.SyntaxFactory.CreateTokenParser`)
to do lexing. It still emits Razor's own `SyntaxToken` -- not Roslyn
`Microsoft.CodeAnalysis.SyntaxToken` -- so the *parser logic* downstream is
unchanged. It maintains a `_resultCache` so it can rewind when the parser
does (i.e. it makes Roslyn's stateful lexer behave like a seekable lexer).

The takeaway: even with `UseRoslynTokenizer`, **only the lexer is shared with
Roslyn**. The actual C# parsing logic (statements, expressions, balancing)
is still Razor's own hand-written recursive-descent code in
`CSharpCodeParser.cs`. Roslyn's parser and recovery algorithms are not used.

---

## 5. Why a small error becomes a wall of red (issue #10383)

End-to-end, for `<button @onclick="">...`:

1. `HtmlMarkupParser.ParseMarkupElement` enters a tag, calls
   `ParseStartTag` -> `ParseAttributes` -> `ParseAttribute`.
2. `TryParseAttributeName` recognises `@onclick` as a C#-introducing
   attribute name (Components flag).
3. `ParseRemainingAttribute` sees `=""`, opens the quote, finds the closing
   quote immediately, and produces a `MarkupAttributeBlock` with an empty
   inner `Value`.
4. Tag-helper rewrite (`TagHelperBlockRewriter.Rewrite`) recognises
   `@onclick` as a bound (non-string) attribute, downstream
   `DefaultTagHelperResolutionPhase.LegacyTagHelperResolver.LowerBoundLegacyAttributeValue`
   sees `prop.Children.Count == 0` and inserts a synthetic empty
   `CSharpIntermediateToken("", source)` -- the empty C# expression -- to
   distinguish "empty" from "minimized". It also emits **RZ2008** with the
   attribute-name span as `SourceSpan`.
5. Code generation builds the standard Components emit pattern roughly
   `__builder.AddAttribute(N, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, ));`
   passing the empty C# expression as the last argument.
6. The generated text is added to the user's compilation as a `SyntaxTree`
   by `RazorSourceGenerator`. Roslyn parses it and produces **CS1525:
   Invalid expression term ')'** at the empty argument position.
7. The diagnostic position in the generated file is mapped back to the
   `.razor` file via `SourceMapping`. The relevant mapping's `OriginalSpan`
   is the attribute-value source span, **but** because of how source
   mappings are aggregated by `CodeRenderingContext`, the C# diagnostic
   often lands in a mapping that covers a much wider range than just the
   `""` -- often the surrounding component / code block / file region --
   producing the "half the file is red" effect.

So there are at least three independent contributors to the bug:

* **Parser recovery** does not represent "empty bound attribute value" as a
  *missing* expression; it represents it as a *present, empty* expression.
  Compare to Roslyn: an absent expression becomes a missing
  `IdentifierName` with a diagnostic at the precise insertion point.
* **Codegen** emits the empty expression as literal text into the
  generated file, producing malformed C#. There is no "if the expression
  is empty, emit `default!` or skip the call" guard.
* **Source mappings** are coarse-grained -- a single mapping can cover a
  large region of the .razor source, so a narrow C# diagnostic projects
  back as a wide .razor diagnostic.

`@onclick=""` is the simplest expression of the pattern, but the same chain
applies whenever a Razor construct recovers by emitting a fat literal that
then becomes one source mapping and contains malformed code.

---

## 6. Concrete reasons recovery is poor

Combining sections 1-5:

1. **No missing-token / skipped-trivia invariant.** The syntax tree cannot
   represent "I expected X here; pretend it was there and continue". The only
   tools are (a) the small set of locations that already use
   `SyntaxFactory.MissingToken` and (b) folding into literal nodes.
2. **Recovery is per-function, written from local knowledge.** There is no
   shared notion of "follow set of an expression", "synchronization tokens of
   a directive", etc. Every author writes their own `AcceptUntil(...)`.
3. **Fall-back tokens are coarse.** The two universal "panic" tokens are
   `LessThan` ("next HTML thing") and `LeftBrace/RightBrace` ("next code-block
   boundary"). Anything between the error and the next such token is absorbed
   into a single literal.
4. **Character-level rollback prevents per-token try/fail.** Because both
   parsers share a character cursor and tokenizers have state (especially
   the Roslyn-wrapping tokenizer), large speculative paths are expensive and
   relatively rare. `Balance` with `BacktrackOnFailure` is the main
   speculation primitive, and it is binary success/failure with no partial
   result.
5. **Recovery results compound through codegen.** A wide literal becomes a
   wide source mapping becomes a wide C# diagnostic. The Razor parser cannot
   produce a narrow diagnostic *just* by reporting one -- it also has to
   produce a narrow node.
6. **The CSharp parser is hand-rolled, not Roslyn.** Roslyn already has
   excellent C# recovery (skipped trivia, missing tokens, expression-context
   resync). Razor's CSharp parser only uses Roslyn's *lexer*, and even that
   is opt-in. Razor reimplements `if`/`for`/`foreach`/`try`/`do`/`using`/
   `switch` framing and a small slice of expression parsing.
7. **Single shared ErrorSink stack.** All parser errors flow into the same
   `ErrorSink.OnError(diagnostic)`, with no association between an error and
   a specific node. There is no "this node carries this diagnostic" link
   except where errors are attached to `SyntaxToken.SetDiagnostics`. This
   limits the IDE's ability to surface errors at the exact node boundary.
8. **The tag-helper rewriter operates on the already-recovered tree.** Any
   loss of structure during the initial parse degrades the rewriter's
   accuracy. There is no second-chance reparse for tag-helper-specific
   structure.
9. **No partial-parse plumbing in the new world.** `SpanEditHandler`,
   `AcceptedCharactersInternal`, `AutoCompleteEditHandler`, and friends
   exist for the legacy editor's incremental-edit-in-place model. They
   complicate the parser without helping recovery in the source-generator
   world, which always re-parses end-to-end.

---

## 7. Directions for a future fix (sketch only)

A future refactor should consider some combination of:

A. **Introduce skipped-tokens trivia and a "missing" invariant.** Adopt
   Roslyn's pattern: tokens carry leading/trailing trivia, including a
   `SkippedTokensTrivia` kind. A `MissingToken(kind)` is zero-width and
   carries the diagnostic. Every parser function that expects token X
   either consumes X or emits `MissingToken(X)` plus skipped-tokens trivia
   for what it had to skip to resync.
   * This is the largest change. It requires regenerating `Syntax.xml` so
     tokens have trivia, audits of every node consumer that walks tokens,
     and changes to all the literal-producing parsers so that "garbage"
     no longer becomes part of `CSharpStatementLiteral.LiteralTokens`.

B. **Define follow sets and a synchronize() primitive.** Each parser
   function declares the set of tokens it will recover at; a single
   helper `Synchronize(followSet)` skips tokens into trivia until one is
   reached. Combined with (A), this gives uniform narrow recovery.

C. **Hand off C# parsing to Roslyn.** Once a transition is detected and
   the boundary tokens (`(`, `{`, end of implicit expression) are
   identified, parse the slice with Roslyn's `CSharpParseOptions` /
   `CSharpSyntaxTree.ParseText` and embed the resulting Roslyn syntax in
   the Razor tree (or a thin adapter). Roslyn's recovery is far better
   than anything Razor can re-engineer cheaply. This needs careful design
   around explicit-expression `@(...)` (where Razor must find the matching
   `)`, since Roslyn won't know about Razor's grammar) and implicit
   expressions (which truncate on a different set of follow characters than
   any Roslyn production). The `RoslynCSharpTokenizer` is a half-step in
   this direction.

D. **Make codegen tolerant of empty / missing expressions.** Where the
   parser reports a missing expression, emit a safe placeholder (`default!`,
   `_ = (object?)null`, etc.) in the generated C# so Roslyn does not see
   syntactically malformed code. This decouples Razor parse errors from
   C# parse errors and is the smallest behavioural change that would
   already fix the *visible* "wall of red" for #10383, even without
   parser changes.

E. **Narrow source mappings around recovery.** Even with the current
   parser, splitting source mappings at logical boundaries within recovery
   regions (e.g. per token, or per construct attempt) would bound the
   maximum span of a single mapped diagnostic.

F. **Pre-tokenize, parse from a token list.** Move both tokenizers behind
   a "token reader" abstraction that pre-tokenizes a region (or the whole
   file) and lets the parsers index into a token array. Speculative
   parsing then costs O(tokens) of pointer movement instead of re-running
   the lexer over characters. This is a precondition for cheap
   try/backtrack which is in turn a precondition for some forms of
   recovery.

Notes on staging: D is the smallest and most surgical and probably the right
*first* move to ship a fix for #10383 quickly. A + B together is the
"correct" long-term fix and is a multi-quarter effort. C is appealing but
the Razor/C# grammar interaction is subtle (esp. for `@(...)` and implicit
expressions); a hybrid where Razor still finds the boundary and Roslyn does
the inner parse may be more realistic than full delegation.

