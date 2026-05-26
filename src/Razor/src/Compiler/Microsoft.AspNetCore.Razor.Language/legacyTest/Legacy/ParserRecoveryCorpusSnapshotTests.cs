// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Linq;
using Microsoft.AspNetCore.Razor.Language.Extensions;
using Microsoft.AspNetCore.Razor.Language.Syntax;
using Xunit;

namespace Microsoft.AspNetCore.Razor.Language.Legacy;

/// <summary>
/// Golden-baseline snapshot tests for the parser-recovery corpus
/// (Stage 0.2 of the parser error-recovery redesign plan;
/// see <c>src/Razor/docs/plans/ErrorRecovery/razor-recovery-redesign-plan.md</c>).
///
/// Each [Fact] loads a single ".razor" file from the corpus
/// (<c>legacyTest/ParserRecoveryCorpus/*.razor</c>, embedded as a
/// resource via the csproj's <c>EmbeddedResource Include="ParserRecoveryCorpus\**\*"</c>),
/// parses it with the legacy parser, and asserts against the existing
/// <c>.stree.txt</c> / <c>.diag.txt</c> / <c>.cspans.txt</c> /
/// <c>.tspans.txt</c> baselines (under <c>TestFiles/ParserRecoveryCorpusSnapshotTests/</c>).
///
/// The corpus is the "moving target" of the redesign: each later
/// stage that migrates a parser function updates the affected
/// corpus baselines under the enhanced-recovery mode. See plan
/// section "Stage 0.2 -- Snapshot harness (parser-only)" for the
/// parser-side scope and the (deferred) end-to-end metrics owned
/// by Stage 5.1's e2e harness.
///
/// Regenerate baselines via
/// <c>dotnet test ...Legacy.UnitTests.csproj /p:GenerateBaselines=true --filter ParserRecoveryCorpusSnapshotTests</c>.
///
/// Enhanced-mode tests (suffix <c>_EnhancedRecovery</c>) re-parse the
/// same corpus file with <c>UseEnhancedRecovery = true</c> and use
/// in-memory assertions rather than a parallel set of baselines
/// (per the Stage 1.4 pilot deviation). Each enhanced test asserts the
/// Stage 2.x exit criteria for the function being migrated.
/// </summary>
public class ParserRecoveryCorpusSnapshotTests() : ParserTestBase(layer: TestProject.Layer.Compiler, validateSpanEditHandlers: true, useLegacyTokenizer: true)
{
    [Fact]
    public void EmptyBoundAttribute_Onclick()
        => ParseCorpusFile("EmptyBoundAttribute_Onclick.razor");

    [Fact]
    public void UnclosedExplicitExpression()
        => ParseCorpusFile("UnclosedExplicitExpression.razor");

    [Fact]
    public void UnclosedIfParen()
        => ParseCorpusFile("UnclosedIfParen.razor");

    [Fact]
    public void UnclosedCodeBlock()
        => ParseCorpusFile("UnclosedCodeBlock.razor");

    [Fact]
    public void UnclosedString()
        => ParseCorpusFile("UnclosedString.razor");

    [Fact]
    public void MalformedTagAttribute()
        => ParseCorpusFile("MalformedTagAttribute.razor");

    [Fact]
    public void MidStatementGarbage()
        => ParseCorpusFile("MidStatementGarbage.razor");

    [Fact]
    public void UnclosedTag()
        => ParseCorpusFile("UnclosedTag.razor");

    [Fact]
    public void BareAtFollowedByGarbage()
        => ParseCorpusFile("BareAtFollowedByGarbage.razor");

    [Fact]
    public void EmptyExplicitExpression()
        => ParseCorpusFile("EmptyExplicitExpression.razor");

    [Fact]
    public void UnclosedForeach()
        => ParseCorpusFile("UnclosedForeach.razor");

    [Fact]
    public void UnclosedSwitch()
        => ParseCorpusFile("UnclosedSwitch.razor");

    [Fact]
    public void MalformedUsing()
        => ParseCorpusFile("MalformedUsing.razor");

    [Fact]
    public void UnclosedMethodCallInImplicit()
        => ParseCorpusFile("UnclosedMethodCallInImplicit.razor");

    [Fact]
    public void UnnamedTag()
        => ParseCorpusFile("UnnamedTag.razor");

    // ----------------------------------------------------------------
    // Stage 2.1: ParseExplicitExpressionBody enhanced-recovery tests.
    //
    // These exercise the new `Context.Options.UseEnhancedRecovery == true`
    // branch of `CSharpCodeParser.ParseExplicitExpressionBody` added in
    // Stage 2.1. The legacy [Fact]s above continue to pin the old
    // behaviour via the existing .stree/.diag/.cspans baselines.
    //
    // Each enhanced test asserts the Stage 2.1 exit criteria:
    //   - `MissingToken(RightParenthesis)` at the construct's first
    //     un-matched position (the follow token or EOF, not the opening `(`).
    //   - Absorbed garbage is wrapped in `SkippedContentSyntax`
    //     (not a fat `CSharpExpressionLiteral`).
    //   - The diagnostic span on the missing token is <= 1 character
    //     (specifically: zero-width at the missing-token cursor).
    // ----------------------------------------------------------------

    [Fact]
    public void UnclosedExplicitExpression_EnhancedRecovery()
    {
        var testFile = TestFile.Create("ParserRecoveryCorpus/UnclosedExplicitExpression.razor", typeof(ParserRecoveryCorpusSnapshotTests));
        var source = testFile.ReadAllText();

        var tree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        var explicitBody = tree.Root
            .DescendantNodes()
            .OfType<CSharpExplicitExpressionBodySyntax>()
            .Single();

        // Open `(` at position 10.
        var openParenToken = explicitBody.OpenParen.MetaCode.Single();
        Assert.False(openParenToken.IsMissing);
        Assert.Equal(SyntaxKind.LeftParenthesis, openParenToken.Kind);
        Assert.Equal(10, openParenToken.SpanStart);

        // Inside the expression block: a `SkippedContentSyntax` wraps the
        // absorbed `foo.Bar` tokens (Stage 2.1 exit criterion -- not a fat
        // `CSharpExpressionLiteral`). The skipped node carries its
        // originating language so Stage 5.6 can route IDE features at
        // positions inside the skipped span to the C# language.
        var skipped = explicitBody.CSharpCode
            .DescendantNodes()
            .OfType<SkippedContentSyntax>()
            .Single();
        Assert.Equal(SyntaxKind.CSharpCodeBlock, skipped.OriginatingLanguage);
        Assert.Equal(11, skipped.SpanStart);
        Assert.Equal("foo.Bar", skipped.GetContent());

        // Any `CSharpExpressionLiteral` inside the expression block must be
        // zero-width: the legacy "fat literal" wrapping `foo.Bar` is now a
        // `SkippedContentSyntax`. Only the marker literal flushed by
        // `OutputTokensAsExpressionLiteral` (Width == 0) may remain.
        Assert.All(
            explicitBody.CSharpCode.DescendantNodes().OfType<CSharpExpressionLiteralSyntax>(),
            lit => Assert.Equal(0, lit.Width));

        // The closing `)` is missing at the first un-matched position --
        // the `<` of `</p>` at position 18 -- with the new zero-width
        // diagnostic span.
        var rightParenToken = explicitBody.CloseParen.MetaCode.Single();
        Assert.True(rightParenToken.IsMissing);
        Assert.Equal(SyntaxKind.RightParenthesis, rightParenToken.Kind);
        Assert.Equal(18, rightParenToken.SpanStart);
        Assert.Equal(0, rightParenToken.Span.Length);

        // The diagnostic is RZ1006 (`Parsing_ExpectedEndOfBlockBeforeEOF` --
        // same descriptor as legacy, only the span has narrowed). It's
        // attached to the missing token, not duplicated into `ErrorSink`
        // (the new recovery contract).
        var rz1006 = tree.Diagnostics.Where(d => d.Id == "RZ1006").ToArray();
        Assert.Single(rz1006);
        Assert.Equal(18, rz1006[0].Span.AbsoluteIndex);
        Assert.Equal(0, rz1006[0].Span.Length);

        // The garbage region (`</p>`) past the `<` follow token must be
        // picked up by the markup parser as a real `MarkupEndTag` -- not
        // re-absorbed as `MarkupMiscAttributeContent` (Stage 2 exit
        // criterion #4).
        Assert.Empty(tree.Root.DescendantNodes().OfType<MarkupMiscAttributeContentSyntax>());
    }

    [Fact]
    public void EmptyExplicitExpression_EnhancedRecovery()
    {
        var testFile = TestFile.Create("ParserRecoveryCorpus/EmptyExplicitExpression.razor", typeof(ParserRecoveryCorpusSnapshotTests));
        var source = testFile.ReadAllText();

        var tree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        // `@()` is well-formed: `Balance` succeeds, so the recovery branch
        // is never entered and the enhanced tree is identical to the
        // legacy one. Verified by the absence of diagnostics, the absence
        // of `SkippedContentSyntax`, and the presence of a real (non-
        // missing) closing `)`.
        var explicitBody = tree.Root
            .DescendantNodes()
            .OfType<CSharpExplicitExpressionBodySyntax>()
            .Single();

        var rightParenToken = explicitBody.CloseParen.MetaCode.Single();
        Assert.False(rightParenToken.IsMissing);
        Assert.Equal(SyntaxKind.RightParenthesis, rightParenToken.Kind);
        Assert.Equal(2, rightParenToken.SpanStart);

        Assert.Empty(tree.Root.DescendantNodes().OfType<SkippedContentSyntax>());
        Assert.Empty(tree.Diagnostics);
    }

    // ----------------------------------------------------------------
    // Stage 2.2: ParseStatementBody enhanced-recovery test.
    //
    // Exercises the new `Context.Options.UseEnhancedRecovery == true`
    // branch of `CSharpCodeParser.ParseStatementBody` added in Stage 2.2.
    // The legacy `UnclosedCodeBlock` [Fact] above continues to pin the
    // old behaviour via the existing baselines (diagnostic at position
    // 1 with span length 1 covering the `{`).
    //
    // Stage 2.2 exit criteria asserted here:
    //   - `MissingToken(RightBrace)` at the EOF position, not the `@{`.
    //   - The diagnostic span on the missing token is zero-width at the
    //     missing-token cursor (down from a 1-char span at the opening
    //     `{`).
    //   - No fat `CSharpStatementLiteral` absorbs the markup following
    //     the unclosed block (it's already parsed as real markup in
    //     legacy mode too; this just asserts the property is preserved).
    //   - No `MarkupMiscAttributeContent` is produced for the recovered
    //     region.
    // ----------------------------------------------------------------

    [Fact]
    public void UnclosedCodeBlock_EnhancedRecovery()
    {
        var testFile = TestFile.Create("ParserRecoveryCorpus/UnclosedCodeBlock.razor", typeof(ParserRecoveryCorpusSnapshotTests));
        var source = testFile.ReadAllText();

        var tree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        var statementBody = tree.Root
            .DescendantNodes()
            .OfType<CSharpStatementBodySyntax>()
            .Single();

        // Open `{` at position 1 (after the `@` transition).
        var openBraceToken = statementBody.OpenBrace.MetaCode.Single();
        Assert.False(openBraceToken.IsMissing);
        Assert.Equal(SyntaxKind.LeftBrace, openBraceToken.Kind);
        Assert.Equal(1, openBraceToken.SpanStart);

        // The closing `}` is missing at the EOF position (source length
        // 69, after the `</p>\r\n`) with the new zero-width diagnostic
        // span. Legacy mode produces the same missing-token shape but
        // attaches the diagnostic to `ErrorSink` instead of the token
        // and uses a 1-char span at position 1 (the `{`).
        var rightBraceToken = statementBody.CloseBrace.MetaCode.Single();
        Assert.True(rightBraceToken.IsMissing);
        Assert.Equal(SyntaxKind.RightBrace, rightBraceToken.Kind);
        Assert.Equal(source.Length, rightBraceToken.SpanStart);
        Assert.Equal(0, rightBraceToken.Span.Length);

        // The diagnostic is RZ1006 (`Parsing_ExpectedEndOfBlockBeforeEOF`
        // -- same descriptor as legacy, only the span has narrowed). It's
        // attached to the missing token, not duplicated into `ErrorSink`
        // (the new recovery contract).
        var rz1006 = tree.Diagnostics.Where(d => d.Id == "RZ1006").ToArray();
        Assert.Single(rz1006);
        Assert.Equal(source.Length, rz1006[0].Span.AbsoluteIndex);
        Assert.Equal(0, rz1006[0].Span.Length);

        // The markup following the unclosed `@{` (`<p>...</p>`) must be
        // parsed as real markup nested inside the `CSharpStatementBody`,
        // not absorbed as a fat `CSharpStatementLiteral`. Only zero-width
        // marker literals (flushed by `OutputTokensAsStatementLiteral`) may
        // appear at or past the `<p>` position.
        var pStartTagPosition = source.IndexOf("<p>");
        Assert.True(pStartTagPosition > 0, "Corpus file should contain `<p>` markup.");
        Assert.All(
            statementBody.CSharpCode.DescendantNodes().OfType<CSharpStatementLiteralSyntax>(),
            lit =>
            {
                if (lit.Width == 0)
                {
                    return;
                }
                Assert.True(
                    lit.EndPosition <= pStartTagPosition,
                    $"Non-empty CSharpStatementLiteral at [{lit.SpanStart}..{lit.EndPosition}) overlaps the `<p>` markup at {pStartTagPosition}.");
            });

        // The recovered markup region must NOT be re-wrapped as
        // `MarkupMiscAttributeContent` (Stage 2 exit criterion #4).
        Assert.Empty(tree.Root.DescendantNodes().OfType<MarkupMiscAttributeContentSyntax>());
    }

    // ----------------------------------------------------------------
    // Stage 2.3: ParseStandardStatement enhanced-recovery tests.
    //
    // Exercise the new `Context.Options.UseEnhancedRecovery == true`
    // branches added in Stage 2.3 to:
    //   - `ParseStandardStatement`'s panic-else (the canonical "fat
    //     literal" producer at the end of the function's while loop);
    //   - `TryBalanceBlock`'s recovery after a failed `Balance` with
    //     `BacktrackOnFailure`.
    //
    // Stage 2.3 exit criteria asserted under enhanced mode:
    //   - Absorbed garbage is wrapped in `SkippedContentSyntax` (not
    //     `CSharpStatementLiteral.LiteralTokens`), so codegen won't
    //     dump it as C# text.
    //   - The new `RZ1046` (`Parsing_UnexpectedTokenInStatement`)
    //     diagnostic is zero-width at the offending token (only fires
    //     from the panic-else, not from `TryBalanceBlock` -- Balance's
    //     RZ1027 covers that site).
    //   - The surrounding markup parses cleanly without
    //     `MarkupMiscAttributeContent` wrappers.
    //
    // Empirical note: the corpus files `MidStatementGarbage.razor` and
    // `UnclosedIfParen.razor` do NOT actually exercise Stage 2.3's
    // panic branches (see the per-test comment). The panic-else in
    // `ParseStandardStatement` is structurally unreachable from typical
    // input (`ReadWhile`'s stop set covers every kind handled by the
    // if-else chain); `UnclosedIfParen` is handled by
    // `ParseMethodCallOrArrayIndex` (Stage 2.6 territory).
    // `UnclosedParenInsideCodeBlock_EnhancedRecovery` constructs an
    // input that DOES exercise `TryBalanceBlock`'s enhanced recovery,
    // giving Stage 2.3 real test coverage.
    // ----------------------------------------------------------------

    [Fact]
    public void MidStatementGarbage_EnhancedRecovery()
    {
        var testFile = TestFile.Create("ParserRecoveryCorpus/MidStatementGarbage.razor", typeof(ParserRecoveryCorpusSnapshotTests));
        var source = testFile.ReadAllText();

        var tree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        // The corpus input `@{ var x = ?? 1; <p>...</p> }` is fully
        // recognised by the lexer/parser: `??` is a valid C# NullCoalesce
        // token, `<p>` triggers `ParseStatement`'s markup-transition
        // handoff, and `}` closes the block. No recovery branch is hit,
        // so Stage 2.3's enhanced path is dead code for this input.
        //
        // Parity assertions: enhanced mode produces the same shape as
        // legacy (the existing `MidStatementGarbage` baseline). This
        // pins the invariant that Stage 2.3 doesn't regress well-formed
        // mixed C#/markup inputs.
        Assert.Empty(tree.Diagnostics);
        Assert.Empty(tree.Root.DescendantNodes().OfType<SkippedContentSyntax>());
        Assert.Empty(tree.Root.DescendantNodes().OfType<MarkupMiscAttributeContentSyntax>());

        // No new RZ1046 (`Parsing_UnexpectedTokenInStatement`) -- the
        // panic-else didn't fire.
        Assert.Empty(tree.Diagnostics.Where(d => d.Id == "RZ1046"));

        // The `<p>...</p>` markup is parsed as a real `MarkupElement`,
        // not absorbed as a fat statement literal.
        var pStartTagPosition = source.IndexOf("<p>");
        Assert.True(pStartTagPosition > 0, "Corpus file should contain `<p>` markup.");
        var pElement = tree.Root.DescendantNodes().OfType<MarkupElementSyntax>().Single();
        Assert.Equal(pStartTagPosition, pElement.SpanStart);
    }

    [Fact]
    public void UnclosedIfParen_EnhancedRecovery()
    {
        var testFile = TestFile.Create("ParserRecoveryCorpus/UnclosedIfParen.razor", typeof(ParserRecoveryCorpusSnapshotTests));
        var source = testFile.ReadAllText();

        var tree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        // Stage 2.4 update: `@if(foo bar` IS exercised by Stage 2.4's
        // migrated `TryParseCondition` (the Stage 2.3 comment that
        // labelled this "Stage 2.6 territory" was empirically incorrect:
        // `if` IS dispatched to `ParseIfStatement` -> `ParseConditionalBlock`
        // -> `TryParseCondition`, and the legacy `AcceptUntil(NewLine)` was
        // flattening the structure into a single fat `CSharpStatementLiteral`).
        //
        // Stage 2.4 enhanced-recovery assertions for `TryParseCondition`:
        //   - `Balance`'s pre-existing RZ1027 at the opening `(` is preserved.
        //   - No new RZ1046 fires (Stage 2.4 introduced no new diagnostics).
        //   - Absorbed garbage between the unclosed `(` and the synchronization
        //     point (`NewLine` here) is wrapped in `SkippedContentSyntax`
        //     tagged with `CSharpCodeBlock`, NOT a fat `CSharpStatementLiteral`.
        //   - The trailing markup parses cleanly as a real `MarkupElement`.
        //   - No `MarkupMiscAttributeContent` is produced (Stage 2 exit
        //     criterion #4).
        var rz1027 = tree.Diagnostics.Where(d => d.Id == "RZ1027").ToArray();
        Assert.Single(rz1027);
        Assert.Empty(tree.Diagnostics.Where(d => d.Id == "RZ1046"));

        var skipped = tree.Root.DescendantNodes().OfType<SkippedContentSyntax>().Single();
        Assert.Equal(SyntaxKind.CSharpCodeBlock, skipped.OriginatingLanguage);

        // The skipped span covers `foo bar` -- the content between the
        // unclosed `(` and the synchronization point (the trailing `\n`).
        // The leading `(` remains accepted as part of the precise
        // `CSharpStatementLiteral` flushed before the recovery sync.
        Assert.Equal("foo bar", skipped.GetContent());

        Assert.Empty(tree.Root.DescendantNodes().OfType<MarkupMiscAttributeContentSyntax>());

        // The trailing `<p>this should still parse as HTML</p>` parses as
        // a real markup element after the unclosed `@if(...`. The `</p>`
        // is the end-tag of that element -- not absorbed as a fat literal.
        var pElement = tree.Root.DescendantNodes().OfType<MarkupElementSyntax>().Single();
        Assert.Equal(source.IndexOf("<p>"), pElement.SpanStart);
    }

    [Fact]
    public void UnclosedParenInsideCodeBlock_EnhancedRecovery()
    {
        // Synthetic input that DOES exercise Stage 2.3's `TryBalanceBlock`
        // enhanced recovery branch (the corpus files don't -- see
        // per-test comments above).
        //
        //   @{ var x = (foo; }
        //         ^^^^^^^^^^^^
        //         ParseStandardStatement reads `var x = ` via ReadWhile,
        //         then At(LeftParen) -> TryBalanceBlock.
        //         Balance(BacktrackOnFailure) fails (no matching `)`
        //         before the outer `}`); the cursor is backtracked to
        //         right after `(`. Stage 2.3's enhanced recovery branch
        //         then runs `Synchronize((LessThan, RightBrace))` which
        //         skips `foo;` (plus trailing whitespace) and stops at
        //         the outer `}`. The skipped tokens are wrapped in a
        //         `SkippedContentSyntax` rather than absorbed into a
        //         fat `CSharpStatementLiteral`.
        const string source = "@{ var x = (foo; }";

        var tree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        // Exactly one `SkippedContentSyntax`, with `OriginatingLanguage ==
        // CSharpCodeBlock`, covering `foo;` (plus the trailing whitespace
        // up to the `}`).
        var skipped = tree.Root.DescendantNodes().OfType<SkippedContentSyntax>().Single();
        Assert.Equal(SyntaxKind.CSharpCodeBlock, skipped.OriginatingLanguage);
        var openParenPosition = source.IndexOf('(');
        var closeBracePosition = source.LastIndexOf('}');
        Assert.Equal(openParenPosition + 1, skipped.SpanStart);
        Assert.True(skipped.EndPosition <= closeBracePosition,
            $"SkippedContent at [{skipped.SpanStart}..{skipped.EndPosition}) overlaps the closing `}}` at {closeBracePosition}.");
        Assert.Contains("foo", skipped.GetContent());

        // Every `CSharpStatementLiteral` past the `(` must be zero-width:
        // the legacy "fat literal" wrapping `foo;` is now a
        // `SkippedContentSyntax`. Only the marker literal flushed by
        // `OutputTokensAsStatementLiteral` (Width == 0) may remain.
        Assert.All(
            tree.Root.DescendantNodes().OfType<CSharpStatementLiteralSyntax>(),
            lit =>
            {
                if (lit.Width == 0)
                {
                    return;
                }
                Assert.True(
                    lit.EndPosition <= openParenPosition + 1,
                    $"Non-empty CSharpStatementLiteral at [{lit.SpanStart}..{lit.EndPosition}) overlaps the skipped region starting at {openParenPosition + 1}.");
            });

        // The pre-existing RZ1027 from `Balance` remains (Stage 2.3 does
        // NOT narrow it -- that belongs to whichever stage owns the
        // open-bracket emission). It's emitted with a 1-char span at the
        // `(` position via `ErrorSink`.
        var rz1027 = tree.Diagnostics.Where(d => d.Id == "RZ1027").ToArray();
        Assert.Single(rz1027);
        Assert.Equal(openParenPosition, rz1027[0].Span.AbsoluteIndex);

        // No new RZ1046 (`Parsing_UnexpectedTokenInStatement`) -- that
        // diagnostic only fires from the panic-else, which `TryBalanceBlock`
        // doesn't touch.
        Assert.Empty(tree.Diagnostics.Where(d => d.Id == "RZ1046"));

        // The outer `}` is consumed as a real `RightBrace` MetaCode -- not
        // absorbed by recovery.
        var statementBody = tree.Root.DescendantNodes().OfType<CSharpStatementBodySyntax>().Single();
        var rightBraceToken = statementBody.CloseBrace.MetaCode.Single();
        Assert.False(rightBraceToken.IsMissing);
        Assert.Equal(SyntaxKind.RightBrace, rightBraceToken.Kind);
        Assert.Equal(closeBracePosition, rightBraceToken.SpanStart);

        // No `MarkupMiscAttributeContent` produced (Stage 2 exit criterion #4).
        Assert.Empty(tree.Root.DescendantNodes().OfType<MarkupMiscAttributeContentSyntax>());
    }

    // ----------------------------------------------------------------
    // Stage 2.4: TryParseCondition enhanced-recovery tests.
    //
    // Stage 2.4 migrates the C# control-flow keyword frames
    // (`@if`, `@for`, `@foreach`, `@while`, `@switch`, `@lock`,
    // `@try`, `@do`, `@using`, plus `catch` / `while` clauses).
    // Structurally all of these route through `TryParseCondition`
    // for their `(condition)` syntax: it's the single migration site
    // that covers the entire family. The body `{...}` block is
    // already migrated by Stage 2.2's `ParseStatementBody`.
    //
    // Stage 2.4 exit criteria asserted under enhanced mode:
    //   - Absorbed garbage between an unclosed `(` and the next sync
    //     point (`NewLine`, `LeftBrace`, or `RightBrace`) is wrapped
    //     in a `SkippedContentSyntax` tagged with `CSharpCodeBlock`
    //     (not absorbed into a fat `CSharpStatementLiteral.LiteralTokens`).
    //   - `Balance`'s pre-existing RZ1027 at the opening `(` is preserved
    //     (its narrowing belongs to the construct's open-bracket stage).
    //   - No new RZ1046 (Stage 2.4 introduces no new diagnostics).
    //   - The surrounding markup parses cleanly without
    //     `MarkupMiscAttributeContent` wrappers.
    //
    // Corpus coverage: `UnclosedForeach.razor` and `UnclosedSwitch.razor`
    // (added in Stage 2.4) exercise the canonical conditional-block
    // recovery via `ParseConditionalBlock` + `TryParseCondition`.
    // `UnclosedIfParen_EnhancedRecovery` above (updated in Stage 2.4)
    // also exercises the same path via `ParseIfStatement`. Additional
    // synthetic in-memory tests below cover `ParseFilterableCatchBlock`,
    // `ParseUsingStatement`, and `ParseWhileClause`.
    // ----------------------------------------------------------------

    [Fact]
    public void UnclosedForeach_EnhancedRecovery()
    {
        var testFile = TestFile.Create("ParserRecoveryCorpus/UnclosedForeach.razor", typeof(ParserRecoveryCorpusSnapshotTests));
        var source = testFile.ReadAllText();

        var tree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        // RZ1027 from `Balance` is preserved. No new RZ1046.
        var rz1027 = tree.Diagnostics.Where(d => d.Id == "RZ1027").ToArray();
        Assert.Single(rz1027);
        Assert.Empty(tree.Diagnostics.Where(d => d.Id == "RZ1046"));

        // Exactly one `SkippedContentSyntax` covers the malformed
        // condition body `var x in items`. The legacy parser absorbed
        // these tokens into the fat `CSharpStatementLiteral` shown by
        // the `UnclosedForeach.stree.txt` baseline.
        var skipped = tree.Root.DescendantNodes().OfType<SkippedContentSyntax>().Single();
        Assert.Equal(SyntaxKind.CSharpCodeBlock, skipped.OriginatingLanguage);
        Assert.Equal("var x in items", skipped.GetContent());

        // No fat `CSharpStatementLiteral` overlaps the skipped region:
        // every non-empty literal must lie entirely before the skipped
        // span (the pre-recovery `foreach(` boundary) or entirely after
        // it (trailing-trivia whitespace consumed by outer parsing).
        Assert.All(
            tree.Root.DescendantNodes().OfType<CSharpStatementLiteralSyntax>(),
            lit =>
            {
                if (lit.Width == 0)
                {
                    return;
                }
                Assert.True(
                    lit.EndPosition <= skipped.SpanStart || lit.SpanStart >= skipped.EndPosition,
                    $"Non-empty CSharpStatementLiteral at [{lit.SpanStart}..{lit.EndPosition}) overlaps the skipped region [{skipped.SpanStart}..{skipped.EndPosition}).");
            });

        Assert.Empty(tree.Root.DescendantNodes().OfType<MarkupMiscAttributeContentSyntax>());
    }

    [Fact]
    public void UnclosedSwitch_EnhancedRecovery()
    {
        var testFile = TestFile.Create("ParserRecoveryCorpus/UnclosedSwitch.razor", typeof(ParserRecoveryCorpusSnapshotTests));
        var source = testFile.ReadAllText();

        var tree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        // RZ1027 from `Balance` is preserved. No new RZ1046.
        var rz1027 = tree.Diagnostics.Where(d => d.Id == "RZ1027").ToArray();
        Assert.Single(rz1027);
        Assert.Empty(tree.Diagnostics.Where(d => d.Id == "RZ1046"));

        // Exactly one `SkippedContentSyntax` covers the malformed
        // condition body `x`. The legacy parser absorbed `x` and the
        // trailing newline into the fat `CSharpStatementLiteral` shown
        // by the `UnclosedSwitch.stree.txt` baseline.
        var skipped = tree.Root.DescendantNodes().OfType<SkippedContentSyntax>().Single();
        Assert.Equal(SyntaxKind.CSharpCodeBlock, skipped.OriginatingLanguage);
        Assert.Equal("x", skipped.GetContent());

        // The trailing markup elements parse cleanly. The legacy baseline
        // shows two `MarkupElement`s for the `<p>one</p>` and `<p>other</p>`
        // chunks; the enhanced shape preserves them (the SkippedContent
        // doesn't pollute the markup transition).
        var markupElements = tree.Root.DescendantNodes().OfType<MarkupElementSyntax>().ToArray();
        Assert.Equal(2, markupElements.Length);

        Assert.Empty(tree.Root.DescendantNodes().OfType<MarkupMiscAttributeContentSyntax>());
    }

    [Fact]
    public void UnclosedCatchParen_EnhancedRecovery()
    {
        // Synthetic input exercising `TryParseCondition` via
        // `ParseFilterableCatchBlock` (the `catch (ExceptionType ex)` site).
        //
        //   @try { } catch(ex bad { }
        //                ^^^^^^^^
        //                Balance(BacktrackOnFailure) fails (no `)`); the
        //                Stage 2.4 enhanced branch syncs at the body `{`,
        //                wrapping `ex bad` in SkippedContent.
        const string source = "@try { } catch(ex bad { }";

        var tree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        var rz1027 = tree.Diagnostics.Where(d => d.Id == "RZ1027").ToArray();
        Assert.Single(rz1027);
        Assert.Empty(tree.Diagnostics.Where(d => d.Id == "RZ1046"));

        var skipped = tree.Root.DescendantNodes().OfType<SkippedContentSyntax>().Single();
        Assert.Equal(SyntaxKind.CSharpCodeBlock, skipped.OriginatingLanguage);
        Assert.Equal("ex bad ", skipped.GetContent());

        Assert.Empty(tree.Root.DescendantNodes().OfType<MarkupMiscAttributeContentSyntax>());
    }

    [Fact]
    public void UnclosedUsingParen_EnhancedRecovery()
    {
        // Synthetic input exercising `TryParseCondition` via
        // `ParseUsingStatement` (the `using (resource)` site).
        //
        //   @using(var x = foo bar { }
        //         ^^^^^^^^^^^^^^^^^
        //         Balance(BacktrackOnFailure) fails; the Stage 2.4
        //         enhanced branch syncs at the body `{`, wrapping
        //         the malformed content in SkippedContent.
        const string source = "@using(var x = foo bar { }";

        var tree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        var rz1027 = tree.Diagnostics.Where(d => d.Id == "RZ1027").ToArray();
        Assert.Single(rz1027);
        Assert.Empty(tree.Diagnostics.Where(d => d.Id == "RZ1046"));

        var skipped = tree.Root.DescendantNodes().OfType<SkippedContentSyntax>().Single();
        Assert.Equal(SyntaxKind.CSharpCodeBlock, skipped.OriginatingLanguage);
        Assert.Contains("var x = foo bar", skipped.GetContent());

        Assert.Empty(tree.Root.DescendantNodes().OfType<MarkupMiscAttributeContentSyntax>());
    }

    [Fact]
    public void UnclosedWhileInDoLoop_EnhancedRecovery()
    {
        // Synthetic input exercising `TryParseCondition` via
        // `ParseWhileClause` (the `while (condition)` site after `do`).
        //
        //   @do { } while(foo bar
        //                ^^^^^^^^
        //                Balance(BacktrackOnFailure) fails at EOF; the
        //                Stage 2.4 enhanced branch syncs at EOF,
        //                wrapping `foo bar` in SkippedContent.
        const string source = "@do { } while(foo bar";

        var tree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        var rz1027 = tree.Diagnostics.Where(d => d.Id == "RZ1027").ToArray();
        Assert.Single(rz1027);
        Assert.Empty(tree.Diagnostics.Where(d => d.Id == "RZ1046"));

        var skipped = tree.Root.DescendantNodes().OfType<SkippedContentSyntax>().Single();
        Assert.Equal(SyntaxKind.CSharpCodeBlock, skipped.OriginatingLanguage);
        Assert.Equal("foo bar", skipped.GetContent());

        Assert.Empty(tree.Root.DescendantNodes().OfType<MarkupMiscAttributeContentSyntax>());
    }

    // ----------------------------------------------------------------
    // Stage 2.5: Razor directive parsers enhanced-recovery tests.
    //
    // Stage 2.5 migrates `ParseExtensibleDirective` (the workhorse for
    // user-defined directives like `@inherits`, `@model`, `@attribute`
    // etc., plus the built-in `@namespace`) and `ParseUsingDeclaration`
    // (the `@using foo.bar` import directive).
    //
    // Exit criteria (per plan section "Stage 2.5"):
    //   - Trailing garbage on the directive's line is absorbed as
    //     `SkippedContentSyntax` (originating language: C#) inside the
    //     directive's syntax span, not leaked to outer markup where it
    //     would become `MarkupTextLiteral` or fake `MarkupStartTag` +
    //     `MarkupMiscAttributeContent`.
    //   - The pre-existing directive diagnostic (e.g. RZ1014 for an
    //     `@inherits` with a malformed type name) is unchanged in span;
    //     no new RZ diagnostics are introduced.
    //   - Subsequent well-formed markup parses cleanly (no cascading
    //     `MarkupMiscAttributeContent` from leaked C# tokens).
    //
    // `ParseUsingDeclaration` is silent for trailing garbage on
    // `@using foo bar` (the legacy path also emits no diagnostic);
    // recovery here is purely about tree shape, not error reporting.
    //
    // Corpus coverage: `MalformedUsing.razor` (added in Stage 2.5)
    // exercises `ParseUsingDeclaration`. Extensible directives like
    // `@inherits` cannot be in the corpus (the corpus test harness
    // doesn't pass `DirectiveDescriptor`s), so `MalformedInherits`
    // below uses inline source with `directives: [InheritsDirective.Directive]`.
    // ----------------------------------------------------------------

    [Fact]
    public void MalformedUsing_EnhancedRecovery()
    {
        var testFile = TestFile.Create("ParserRecoveryCorpus/MalformedUsing.razor", typeof(ParserRecoveryCorpusSnapshotTests));
        var source = testFile.ReadAllText();

        var tree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        // `@using foo bar` produces no diagnostic on either path; the
        // enhanced branch is purely shape-cleanup.
        Assert.Empty(tree.Diagnostics.Where(d => d.Id == "RZ1046"));

        // Exactly one `SkippedContentSyntax` covers ` bar` (the leading
        // whitespace is consumed by sync because the follow set does not
        // include `Whitespace`; the legacy path leaked this as
        // `MarkupTextLiteral - " bar"` on the markup side, see the
        // `MalformedUsing.stree.txt` baseline at offsets [10..16)).
        var skipped = tree.Root.DescendantNodes().OfType<SkippedContentSyntax>().Single();
        Assert.Equal(SyntaxKind.CSharpCodeBlock, skipped.OriginatingLanguage);
        Assert.Equal(" bar", skipped.GetContent());

        // The `<p>after</p>` element parses cleanly on the markup side
        // (the legacy baseline shows it does too, but the cleanup target
        // is asserting the absence of leaked `MarkupTextLiteral` content
        // overlapping `bar`).
        var markupElements = tree.Root.DescendantNodes().OfType<MarkupElementSyntax>().ToArray();
        Assert.Single(markupElements);

        // No fake markup wrappers from leaked C# tokens.
        Assert.Empty(tree.Root.DescendantNodes().OfType<MarkupMiscAttributeContentSyntax>());

        // No `MarkupTextLiteral` containing `bar` (the legacy leaked
        // ` bar` as `MarkupTextLiteral` -- enhanced recovery absorbs it
        // before the outer markup parser sees it).
        Assert.DoesNotContain(
            tree.Root.DescendantNodes().OfType<MarkupTextLiteralSyntax>(),
            lit => lit.GetContent().Contains("bar"));
    }

    [Fact]
    public void MalformedInherits_EnhancedRecovery()
    {
        // Synthetic input exercising `ParseExtensibleDirective` via
        // `@inherits` (an `AddTypeToken` / `FileScopedSinglyOccurring`
        // directive). The leading `+` after `@inherits ` is neither
        // an Identifier nor a Keyword, so `TryParseNamespaceOrTypeName`
        // returns false and the directive's type-token branch bails
        // with `Parsing_DirectiveExpectsTypeName` (RZ1014).
        //
        // Stage 2.5's `BuildBailedDirective` helper wraps the bail
        // with a `Synchronize` so `+ bad` is absorbed as
        // `SkippedContentSyntax` (originating: C#) inside the directive,
        // rather than leaking to the outer markup parser (which would
        // produce a `MarkupTextLiteral` for ` bad`).
        //
        //   @inherits + badLF<p>after</p>LF
        //             ^^^^^^
        //             Stage 2.5: absorbed as SkippedContent.
        const string source = "@inherits + bad\r\n<p>after</p>\r\n";

        var tree = ParseDocument(
            source,
            directives: [InheritsDirective.Directive],
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        // RZ1013 (DirectiveExpectsTypeName) is preserved -- the
        // pre-existing diagnostic's span is unchanged.
        var rz1013 = tree.Diagnostics.Where(d => d.Id == "RZ1013").ToArray();
        Assert.Single(rz1013);

        // Stage 2.5 introduces no new diagnostics.
        Assert.Empty(tree.Diagnostics.Where(d => d.Id == "RZ1046"));

        // Exactly one `SkippedContentSyntax` covers `+ bad`. The legacy
        // path would leak ` bad` as `MarkupTextLiteral` to the outer
        // markup parser.
        var skipped = tree.Root.DescendantNodes().OfType<SkippedContentSyntax>().Single();
        Assert.Equal(SyntaxKind.CSharpCodeBlock, skipped.OriginatingLanguage);
        Assert.Contains("+", skipped.GetContent());
        Assert.Contains("bad", skipped.GetContent());

        // No fake `MarkupMiscAttributeContent` from leaked tokens.
        Assert.Empty(tree.Root.DescendantNodes().OfType<MarkupMiscAttributeContentSyntax>());

        // The trailing `<p>after</p>` parses as a real element on the
        // markup side. Enhanced recovery prevents leaked C# tokens
        // (` bad`) from polluting the outer markup parser.
        var markupElements = tree.Root.DescendantNodes().OfType<MarkupElementSyntax>().ToArray();
        Assert.Single(markupElements);
        Assert.Equal("<p>after</p>", markupElements[0].GetContent());

        // No `MarkupTextLiteral` containing `bad` (the legacy leaked
        // ` bad` as `MarkupTextLiteral` -- enhanced recovery absorbs it
        // before the outer markup parser sees it).
        Assert.DoesNotContain(
            tree.Root.DescendantNodes().OfType<MarkupTextLiteralSyntax>(),
            lit => lit.GetContent().Contains("bad"));
    }

    // ----------------------------------------------------------------
    // Stage 2.6: ParseMethodCallOrArrayIndex enhanced-recovery test.
    //
    // Exercises the new `Context.Options.UseEnhancedRecovery == true`
    // branches added in Stage 2.6 to:
    //   - The `Balance` failure path in `ParseMethodCallOrArrayIndex`
    //     (the canonical implicit-expression unclosed-call producer);
    //   - The closing-bracket emission, which now uses `Required(right, ...)`
    //     so the missing close bracket is always represented in the tree
    //     (either as the real token or as a zero-width `MissingToken`
    //     carrying a narrow RZ1027 diagnostic).
    //
    // Stage 2.6 exit criteria asserted under enhanced mode:
    //   - `MissingToken(RightParenthesis)` at the precise sync position
    //     (the follow token or EOF, not the opening `(`).
    //   - Absorbed garbage is wrapped in `SkippedContentSyntax` (not
    //     `CSharpExpressionLiteral.LiteralTokens`).
    //   - Narrow zero-width RZ1027 diagnostic, attached to the missing
    //     token rather than duplicated into `ErrorSink`.
    //   - Subsequent markup parses cleanly as a real `MarkupElement`
    //     (not absorbed as `MarkupMiscAttributeContent`).
    // ----------------------------------------------------------------

    [Fact]
    public void UnclosedMethodCallInImplicit_EnhancedRecovery()
    {
        var testFile = TestFile.Create("ParserRecoveryCorpus/UnclosedMethodCallInImplicit.razor", typeof(ParserRecoveryCorpusSnapshotTests));
        var source = testFile.ReadAllText();

        var tree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        // Position layout for the corpus input `<p>@foo.Bar(baz</p><div>after</div>\r\n`:
        //   0..2   `<p>`
        //   3      `@`
        //   4..6   `foo`
        //   7      `.`
        //   8..10  `Bar`
        //   11     `(`
        //   12..14 `baz`
        //   15     `<` (start of `</p>`) -- sync stops here
        //   15..18 `</p>`
        //   19..   `<div>after</div>` markup

        // Exactly one RZ1027 diagnostic, zero-width at position 15 (the
        // `<` of `</p>` where the sync stopped). Legacy mode produces
        // RZ1027 as a 1-char span at the opening `(` (position 11) via
        // `Balance`'s `ErrorSink.OnError`; the enhanced branch suppresses
        // that wide span (via `BalancingModes.NoErrorOnFailure`) and
        // emits the narrow span on the `MissingToken` instead.
        var rz1027 = tree.Diagnostics.Where(d => d.Id == "RZ1027").ToArray();
        Assert.Single(rz1027);
        Assert.Equal(15, rz1027[0].Span.AbsoluteIndex);
        Assert.Equal(0, rz1027[0].Span.Length);

        // Exactly one `SkippedContentSyntax` wraps the absorbed `baz`
        // tokens (Stage 2.6 exit criterion -- not a fat
        // `CSharpExpressionLiteral`). Tagged with `CSharpCodeBlock` so
        // Stage 5.6 can route IDE features at positions inside the
        // skipped span to the C# language.
        var implicitExpression = tree.Root
            .DescendantNodes()
            .OfType<CSharpImplicitExpressionSyntax>()
            .Single();
        var skipped = implicitExpression
            .DescendantNodes()
            .OfType<SkippedContentSyntax>()
            .Single();
        Assert.Equal(SyntaxKind.CSharpCodeBlock, skipped.OriginatingLanguage);
        Assert.Equal(12, skipped.SpanStart);
        Assert.Equal("baz", skipped.GetContent());

        // The closing `)` is represented as a `MissingToken` at the sync
        // position (the `<` at offset 15), zero-width. Find it inside
        // the implicit expression's expression literals.
        var missingCloseParen = implicitExpression
            .DescendantTokens()
            .Single(t => t.IsMissing && t.Kind == SyntaxKind.RightParenthesis);
        Assert.Equal(15, missingCloseParen.SpanStart);
        Assert.Equal(0, missingCloseParen.Span.Length);

        // The trailing `</p><div>after</div>` markup is parsed as real
        // markup -- the `</p>` is the orphan end-tag of the surrounding
        // `<p>...</p>` element, and the `<div>after</div>` parses as a
        // standalone element after the unclosed implicit expression.
        // Stage 2 exit criterion #4: no `MarkupMiscAttributeContent`
        // wrappers from leaked C# tokens.
        Assert.Empty(tree.Root.DescendantNodes().OfType<MarkupMiscAttributeContentSyntax>());

        var divElement = tree.Root
            .DescendantNodes()
            .OfType<MarkupElementSyntax>()
            .Single(e => e.GetContent().Contains("<div>"));
        Assert.Equal("<div>after</div>", divElement.GetContent());
    }

    // ----------------------------------------------------------------
    // Stage 3.1: ParseStartTag / ParseEndTag enhanced-recovery test.
    //
    // Exercises the new `Context.Options.UseEnhancedRecovery == true`
    // branches added in Stage 3.1 to:
    //   - The tag-name slot in `ParseStartTag` and `ParseEndTag`, which
    //     now uses `Required(SyntaxKind.Text, ...)` so the missing tag
    //     name is represented as a zero-width `MissingToken(Text)` with
    //     a narrow RZ1047 (Parsing_TagNameExpected) diagnostic attached
    //     to the missing token at the precise cursor position;
    //   - (Indirectly, via the existing 28 corpus tests passing) the
    //     close-angle slot in `ParseStartTag`'s MarkupInCodeBlock branch
    //     and `ParseEndTag`'s end-tag-close branch, which now use
    //     `Required(SyntaxKind.CloseAngle, ...)` emitting a narrow
    //     RZ1024 (Parsing_UnfinishedTag) on the missing close angle.
    //
    // Stage 3.1 exit criteria asserted under enhanced mode:
    //   - Two RZ1047 diagnostics, zero-width, at the precise positions
    //     of the missing tag names in `<>` and `</>` (positions 1 and 7,
    //     not at the start-of-tag position 0 or 5).
    //   - Two `MissingToken(Text)` at the same positions, both
    //     zero-width.
    //   - Real `CloseAngle` tokens at positions 1 and 7 (not missing).
    //   - Trailing `<p>after</p>` parses cleanly as a real
    //     `MarkupElement` (Stage 3.1 produces no recovery contamination
    //     that would push it into `MarkupMiscAttributeContent`).
    //   - No RZ1024 diagnostics: enhanced recovery only emits
    //     `Parsing_UnfinishedTag` when the close angle is actually
    //     missing (in `MarkupInCodeBlock` mode for start tags, or in
    //     plain markup mode for end tags). Here both tags have a real
    //     close angle, so RZ1024 must not appear.
    // ----------------------------------------------------------------

    [Fact]
    public void UnnamedTag_EnhancedRecovery()
    {
        var testFile = TestFile.Create("ParserRecoveryCorpus/UnnamedTag.razor", typeof(ParserRecoveryCorpusSnapshotTests));
        var source = testFile.ReadAllText();

        var tree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        // Position layout for the corpus input `<>foo</>\r\n<p>after</p>\r\n`:
        //   0      `<` (start tag open angle)
        //   1      `>` (start tag close angle -- tag name is missing right before this)
        //   2..4   `foo`
        //   5      `<` (end tag open angle)
        //   6      `/`
        //   7      `>` (end tag close angle -- tag name is missing right before this)
        //   8..9   `\r\n`
        //   10..12 `<p>`
        //   13..17 `after`
        //   18..21 `</p>`
        //   22..23 `\r\n`

        // Exactly two RZ1047 diagnostics, both zero-width, at the
        // precise missing-tag-name sites: position 1 (after `<`) and
        // position 7 (after `</`). Legacy mode produces a bare
        // `MissingToken(Text)` with no diagnostic, so RZ1047 is the
        // net-new narrow diagnostic introduced in Stage 3.1.
        var rz1047 = tree.Diagnostics.Where(d => d.Id == "RZ1047").ToArray();
        Assert.Equal(2, rz1047.Length);
        Assert.Equal(1, rz1047[0].Span.AbsoluteIndex);
        Assert.Equal(0, rz1047[0].Span.Length);
        Assert.Equal(7, rz1047[1].Span.AbsoluteIndex);
        Assert.Equal(0, rz1047[1].Span.Length);

        // RZ1024 (Parsing_UnfinishedTag) must not be emitted: both tags
        // have a real `>` token, so the close-angle Required path is
        // not exercised here. (Stage 3.1's close-angle migration is
        // covered indirectly by the 28 pre-existing corpus baselines
        // continuing to pass under enhanced mode.)
        Assert.Empty(tree.Diagnostics.Where(d => d.Id == "RZ1024"));

        // Two `MarkupStartTag`s (`<>` and `<p>`) and two
        // `MarkupEndTag`s (`</>` and `</p>`).
        var startTags = tree.Root.DescendantNodes().OfType<MarkupStartTagSyntax>().ToArray();
        var endTags = tree.Root.DescendantNodes().OfType<MarkupEndTagSyntax>().ToArray();
        Assert.Equal(2, startTags.Length);
        Assert.Equal(2, endTags.Length);

        // The unnamed start tag `<>` has a zero-width
        // `MissingToken(Text)` at position 1 and a real `CloseAngle`
        // `>` at position 1 (also length 1, so the next non-virtual
        // position is 2).
        var unnamedStartTag = startTags[0];
        Assert.True(unnamedStartTag.Name.IsMissing);
        Assert.Equal(SyntaxKind.Text, unnamedStartTag.Name.Kind);
        Assert.Equal(1, unnamedStartTag.Name.SpanStart);
        Assert.Equal(0, unnamedStartTag.Name.Span.Length);
        Assert.NotNull(unnamedStartTag.CloseAngle);
        Assert.False(unnamedStartTag.CloseAngle!.IsMissing);
        Assert.Equal(1, unnamedStartTag.CloseAngle.SpanStart);
        Assert.Equal(">", unnamedStartTag.CloseAngle.Content);

        // The unnamed end tag `</>` has a zero-width
        // `MissingToken(Text)` at position 7 and a real `CloseAngle`
        // `>` at position 7.
        var unnamedEndTag = endTags[0];
        Assert.True(unnamedEndTag.Name.IsMissing);
        Assert.Equal(SyntaxKind.Text, unnamedEndTag.Name.Kind);
        Assert.Equal(7, unnamedEndTag.Name.SpanStart);
        Assert.Equal(0, unnamedEndTag.Name.Span.Length);
        Assert.False(unnamedEndTag.CloseAngle.IsMissing);
        Assert.Equal(7, unnamedEndTag.CloseAngle.SpanStart);
        Assert.Equal(">", unnamedEndTag.CloseAngle.Content);

        // The trailing `<p>after</p>` parses as a real `MarkupElement`
        // with real (non-missing) tag-name tokens. Stage 3.1 exit
        // criterion: no recovery contamination leaks into the trailing
        // markup.
        var namedStartTag = startTags[1];
        Assert.False(namedStartTag.Name.IsMissing);
        Assert.Equal("p", namedStartTag.Name.Content);
        var namedEndTag = endTags[1];
        Assert.False(namedEndTag.Name.IsMissing);
        Assert.Equal("p", namedEndTag.Name.Content);

        // No `MarkupMiscAttributeContent` wrappers from leaked tokens
        // around the unnamed tags. (Stage 3.1's tag-name sync adds any
        // skipped tokens to the attribute / misc-attribute builder as
        // `SkippedContentSyntax`, not as `MarkupMiscAttributeContent`.
        // Because `HtmlTagRecovery` matches the current token at every
        // missing-tag-name site exercised here, no skipped content is
        // produced in practice.)
        Assert.Empty(tree.Root.DescendantNodes().OfType<MarkupMiscAttributeContentSyntax>());
        Assert.Empty(tree.Root.DescendantNodes().OfType<SkippedContentSyntax>());
    }

    // ----------------------------------------------------------------
    // Stage 3.2: ParseRemainingAttribute enhanced-recovery test for the
    // motivating bug (`<button @onclick="">`, dotnet/razor#10383).
    //
    // Exercises the new `Context.Options.UseEnhancedRecovery == true`
    // branch added in Stage 3.2 to `HtmlMarkupParser.ParseRemainingAttribute`,
    // which detects an empty C#-bound attribute value (i.e. the attribute
    // name starts with `@` and the value parse produced nothing) and
    // synthesises the "missing C# expression" tree shape mandated by
    // Big Design Decision #9:
    //
    //     GenericBlock([ CSharpExpressionLiteral([ MissingToken(Identifier) ]) ])
    //
    // The corpus file is parsed under `RazorFileKind.Component`: in
    // Component mode `AllowCSharpInMarkupAttributeArea` is cleared, so
    // `@onclick` is parsed as a regular markup attribute name (with `@`
    // as the first character of the name) and flows through
    // `ParseRemainingAttribute`. Under `RazorFileKind.Legacy` (the
    // default the corpus snapshot uses) the same input splits into two
    // separate `MarkupMiscAttributeContent` nodes -- the Stage 5.2
    // tag-helper rewriter glues those back together. Stage 3.2 only
    // covers the Component-direct-parse path; the legacy snapshot is
    // unchanged.
    //
    // Stage 3.2 exit criteria asserted under enhanced mode:
    //   - The `@onclick` attribute is a real `MarkupAttributeBlockSyntax`
    //     (not split into `MarkupMiscAttributeContent` like the legacy
    //     snapshot shows).
    //   - Its `Value` is exactly the BDD #9 shape: one `GenericBlockSyntax`
    //     containing one `CSharpExpressionLiteralSyntax` containing one
    //     `MissingToken(Identifier)`.
    //   - The whole `Value` subtree is zero-width (no source characters
    //     were absorbed into the missing-expression placeholder).
    //   - Sibling attribute `class="btn btn-primary"` is unaffected --
    //     the fix is gated on the name starting with `@`.
    //   - No new parser diagnostics are introduced by the enhanced
    //     branch (RZ2008 is emitted later by tag-helper resolution, not
    //     by the parser; this assertion guards against the enhanced
    //     branch accidentally widening the diagnostic set).
    // ----------------------------------------------------------------

    // Motivating bug: dotnet/razor#10383 (https://github.com/dotnet/razor/issues/10383).
    [Fact]
    public void EmptyBoundAttribute_Onclick_EnhancedRecovery()
    {
        var testFile = TestFile.Create("ParserRecoveryCorpus/EmptyBoundAttribute_Onclick.razor", typeof(ParserRecoveryCorpusSnapshotTests));
        var source = testFile.ReadAllText();

        var tree = ParseDocument(
            source,
            fileKind: RazorFileKind.Component,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        // Locate the `@onclick=""` attribute. In Component mode the
        // attribute name is parsed as `@onclick` (the `@` is just the
        // first character of the name), so this is a real
        // `MarkupAttributeBlockSyntax` with `name.Content == "@onclick"`.
        var attributeBlock = tree.Root
            .DescendantNodes()
            .OfType<MarkupAttributeBlockSyntax>()
            .Single(a => GetAttributeNameContent(a) == "@onclick");

        // Sanity check: the corpus has `@onclick=""` so an equals token
        // is present and not missing.
        Assert.False(attributeBlock.EqualsToken.IsMissing);
        Assert.Equal(SyntaxKind.Equals, attributeBlock.EqualsToken.Kind);

        // BDD #9 shape: GenericBlock([ CSharpExpressionLiteral([ MissingToken(Identifier) ]) ]).
        // Stage 5.1 codegen detects this exact shape (single-child
        // GenericBlock containing a single-token CSharpExpressionLiteral
        // whose only token is a missing Identifier) and emits a safe
        // placeholder. Any deviation here breaks that contract.
        var value = Assert.IsType<GenericBlockSyntax>(attributeBlock.Value);
        var expressionLiteral = Assert.IsType<CSharpExpressionLiteralSyntax>(Assert.Single(value.Children));
        var missingToken = Assert.Single(expressionLiteral.LiteralTokens);
        Assert.True(missingToken.IsMissing);
        Assert.Equal(SyntaxKind.Identifier, missingToken.Kind);

        // The whole synthesised value subtree is zero-width: the parser
        // did not absorb any source characters into the placeholder.
        Assert.Equal(0, value.Width);
        Assert.Equal(0, expressionLiteral.Width);
        Assert.Equal(0, missingToken.Span.Length);

        // The sibling `class="btn btn-primary"` attribute is unaffected;
        // it has a non-null `Value` with real content (BDD #9 only
        // applies to names starting with `@`).
        var classAttribute = tree.Root
            .DescendantNodes()
            .OfType<MarkupAttributeBlockSyntax>()
            .Single(a => GetAttributeNameContent(a) == "class");
        Assert.NotNull(classAttribute.Value);
        Assert.Contains("btn", classAttribute.Value!.GetContent());

        // The enhanced branch must not emit any parser diagnostics --
        // RZ2008 (empty bound attribute) is emitted later in tag-helper
        // resolution (DefaultTagHelperResolutionPhase.LegacyTagHelperResolver),
        // not in the parser. This assertion guards against the enhanced
        // branch accidentally widening the diagnostic set.
        Assert.Empty(tree.Diagnostics);

        static string GetAttributeNameContent(MarkupAttributeBlockSyntax attribute)
        {
            return attribute.Name is { } name ? name.GetContent() : string.Empty;
        }
    }

    // ----------------------------------------------------------------
    // Stage 3.3: TryRecoverStartTag / CompleteEndTag enhanced-recovery
    // test.
    //
    // Exercises the new `Context.Options.UseEnhancedRecovery == true`
    // branches added in Stage 3.3 to `HtmlMarkupParser`:
    //   - `CompleteMarkupInCodeBlock` (the markup-in-code-block EOF
    //     cleanup loop): emits a narrow zero-width RZ1025
    //     (Parsing_MissingEndTag) at the precise cursor position (EOF
    //     or end-of-block) rather than the legacy wide span at the
    //     unclosed start tag's name.
    //   - `CompleteEndTag` (the "no tracker" / orphan-end-tag branch):
    //     emits a narrow zero-width RZ1026 (Parsing_UnexpectedEndTag)
    //     at the precise cursor position (start of the unexpected
    //     `</`) rather than the legacy span covering the end tag name.
    //   - `CompleteEndTag` (the outer-unclosed-tag cleanup loop):
    //     emits a narrow zero-width RZ1025 at the unexpected end tag's
    //     start position (where the missing end tag should have
    //     appeared) rather than the legacy wide span at the unclosed
    //     start tag's name.
    //
    // Stage 3.3 exit criteria asserted here:
    //   - The corpus file `UnclosedTag.razor` is pure document-mode
    //     markup where `TryRecoverStartTag` silently pops the
    //     intermediate `<span>` / `<p>` as malformed elements (this
    //     silent path is unchanged by Stage 3.3 -- see the
    //     "tag-stack recovery itself doesn't change structurally"
    //     wording in the plan). The corpus exercises the resulting
    //     tree shape: a well-formed `<div>...</div>` outer element
    //     with malformed `<span>` / `<p>` inside, a sibling
    //     `<section>...</section>` element parsing cleanly, and no
    //     `MarkupMiscAttributeContent` across the whole file.
    //   - In-memory `@{ </div> }` and `@{ <div> }` sources cover the
    //     three migrated diagnostic sites and verify the new spans
    //     are zero-width at the precise tag positions (not at the
    //     start of the construct).
    // ----------------------------------------------------------------

    [Fact]
    public void UnclosedTag_EnhancedRecovery()
    {
        var testFile = TestFile.Create("ParserRecoveryCorpus/UnclosedTag.razor", typeof(ParserRecoveryCorpusSnapshotTests));
        var source = testFile.ReadAllText();

        var tree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        // The corpus file `<div>\r\n    <span>\r\n        <p>text</div>\r\n\r\n<section>after the mismatch</section>\r\n`
        // parses to:
        //   - One outer `<div>...</div>` `MarkupElement` containing the
        //     two intermediate unclosed elements (`<span>` and `<p>`)
        //     popped as malformed by `TryRecoverStartTag`.
        //   - One sibling `<section>...</section>` element parsing
        //     cleanly (no contamination from the recovery).
        var topLevelMarkupBlock = tree.Root
            .DescendantNodes()
            .OfType<MarkupBlockSyntax>()
            .First();

        var topLevelElements = topLevelMarkupBlock.Children
            .OfType<MarkupElementSyntax>()
            .ToArray();
        Assert.Equal(2, topLevelElements.Length);

        // The outer `<div>` element has a real `</div>` end tag (the
        // recovery in `TryRecoverStartTag` matched it past the
        // intermediate unclosed `<span>` / `<p>`).
        var divElement = topLevelElements[0];
        Assert.NotNull(divElement.MarkupStartTag);
        Assert.Equal("div", divElement.MarkupStartTag.Name.Content);
        Assert.NotNull(divElement.MarkupEndTag);
        Assert.Equal("div", divElement.MarkupEndTag.Name.Content);

        // The sibling `<section>` element is well-formed and unaffected
        // by the upstream recovery -- no recovery contamination leaks
        // into trailing markup.
        var sectionElement = topLevelElements[1];
        Assert.NotNull(sectionElement.MarkupStartTag);
        Assert.Equal("section", sectionElement.MarkupStartTag.Name.Content);
        Assert.NotNull(sectionElement.MarkupEndTag);
        Assert.Equal("section", sectionElement.MarkupEndTag.Name.Content);

        // Inside the `<div>` element, the intermediate `<span>` and
        // `<p>` are nested malformed elements (start tag present, end
        // tag absent). This is the "user-visible structure" exit
        // criterion: nested elements are grouped correctly rather than
        // sitting as siblings.
        var spanElement = divElement.Body
            .OfType<MarkupElementSyntax>()
            .Single();
        Assert.NotNull(spanElement.MarkupStartTag);
        Assert.Equal("span", spanElement.MarkupStartTag.Name.Content);
        Assert.Null(spanElement.MarkupEndTag);

        var pElement = spanElement.Body
            .OfType<MarkupElementSyntax>()
            .Single();
        Assert.NotNull(pElement.MarkupStartTag);
        Assert.Equal("p", pElement.MarkupStartTag.Name.Content);
        Assert.Null(pElement.MarkupEndTag);

        // No `MarkupMiscAttributeContent` across the whole file --
        // recovery did not absorb anything into a fat misc-attribute
        // wrapper (Stage 3 exit criterion).
        Assert.Empty(tree.Root.DescendantNodes().OfType<MarkupMiscAttributeContentSyntax>());

        // The corpus file exercises only the silent `TryRecoverStartTag`
        // success path (and the document-mode EOF cleanup in
        // `ParseDocument`, which also pops silently). Neither path is
        // a Stage 3.3 diagnostic emission site, so no RZ1025 / RZ1026
        // diagnostics fire here. The in-memory verifications below
        // cover the actual migrated sites.
        Assert.Empty(tree.Diagnostics.Where(d => d.Id == "RZ1025"));
        Assert.Empty(tree.Diagnostics.Where(d => d.Id == "RZ1026"));

        // ----------------------------------------------------------------
        // In-memory verification of the three migrated diagnostic sites.
        //
        // Each scenario uses `@{ ... }` so that the markup parser runs
        // in `ParseMode.MarkupInCodeBlock`, which is the mode that
        // actually fires `CompleteEndTag` (the no-tracker branch and
        // the outer-unclosed cleanup) and `CompleteMarkupInCodeBlock`
        // (the markup-in-code-block EOF cleanup). These three sites
        // are the only emitters of RZ1025 / RZ1026 inside
        // `HtmlMarkupParser`.
        // ----------------------------------------------------------------

        // Site #2 -- `CompleteEndTag` with an empty tag tracker:
        // `</div>` inside a code block with no matching start tag.
        // Legacy emits RZ1026 covering the end tag name; enhanced
        // emits zero-width at the start of `</` (position 3: `@{ ` is
        // 3 chars, then `</div>` begins).
        {
            var unexpectedSource = "@{ </div> }";
            var unexpectedTree = ParseDocument(
                unexpectedSource,
                configureParserOptions: builder => builder.UseEnhancedRecovery = true);

            var rz1026 = unexpectedTree.Diagnostics
                .Where(d => d.Id == "RZ1026")
                .ToArray();
            var unexpectedEndTagDiagnostic = Assert.Single(rz1026);
            Assert.Equal(3, unexpectedEndTagDiagnostic.Span.AbsoluteIndex);
            Assert.Equal(0, unexpectedEndTagDiagnostic.Span.Length);
        }

        // Sites #1 and #3 -- `CompleteMarkupInCodeBlock` and
        // `CompleteEndTag` outer-unclosed cleanup respectively.
        //
        // `@{ <div> }` reaches `CompleteMarkupInCodeBlock` (site #1)
        // because the loop exits with `<div>` still on the tracker at
        // the `}` of the code block. Legacy emits RZ1025 covering
        // `div` at the unclosed start tag (position 4, length 3);
        // enhanced emits zero-width at the cursor (the `}` at position
        // 9). The unclosed `<div>` is also marked `IsWellFormed=true`
        // (it had a real `>` close angle), so the diagnostic does
        // fire.
        {
            var unclosedSource = "@{ <div> }";
            var unclosedTree = ParseDocument(
                unclosedSource,
                configureParserOptions: builder => builder.UseEnhancedRecovery = true);

            var rz1025 = unclosedTree.Diagnostics
                .Where(d => d.Id == "RZ1025")
                .ToArray();
            var missingEndTagDiagnostic = Assert.Single(rz1025);
            Assert.Equal(0, missingEndTagDiagnostic.Span.Length);
            // The cursor at `CompleteMarkupInCodeBlock` is past the
            // close `}` of the code block (the markup parser exits its
            // loop at EOF, which sits at the very end of the source --
            // position 10 for a 10-character `@{ <div> }`). The
            // diagnostic is zero-width at that cursor.
            Assert.Equal(unclosedSource.Length, missingEndTagDiagnostic.Span.AbsoluteIndex);
        }

        // Site #3 -- `CompleteEndTag` outer-unclosed cleanup loop.
        // `@{ <div></span> }`: `</span>` has no matching open in the
        // tracker; `TryRecoverStartTag` returns false; `CompleteEndTag`
        // is called with a non-empty tracker (still holding `<div>`).
        // The loop emits RZ1025 for the unclosed `<div>` at the
        // position of the unexpected end tag (position 8: start of
        // `</span>`). Note that the orphan `</span>` itself does NOT
        // emit RZ1026 here -- in `CompleteEndTag`, RZ1026 only fires
        // in the empty-tracker branch (site #2 above); the non-empty
        // branch attributes the recovery to the unclosed start tags
        // (RZ1025), not to the extra end tag.
        {
            var mixedSource = "@{ <div></span> }";
            var mixedTree = ParseDocument(
                mixedSource,
                configureParserOptions: builder => builder.UseEnhancedRecovery = true);

            var rz1025 = mixedTree.Diagnostics
                .Where(d => d.Id == "RZ1025")
                .ToArray();
            var missingEndTagDiagnostic = Assert.Single(rz1025);
            Assert.Equal(8, missingEndTagDiagnostic.Span.AbsoluteIndex);
            Assert.Equal(0, missingEndTagDiagnostic.Span.Length);

            Assert.Empty(mixedTree.Diagnostics.Where(d => d.Id == "RZ1026"));
        }
    }

    // ----------------------------------------------------------------
    // Stage 3.4 -- `ParseMiscAttribute` migration.
    //
    // Replaces the legacy "absorb everything into a fat
    // `MarkupMiscAttributeContent`" loop with a single
    // `Synchronize(HtmlEndOfTagFollowSet, originatingLanguage: MarkupBlock)`
    // call. Stops at the first HTML tag boundary (`<`, `>`, `/`, `"`,
    // `'`) and emits a narrow zero-width RZ1048
    // (`Parsing_UnexpectedAttributeName`) at the cursor where an
    // attribute name was expected. Absorbed tokens become
    // `SkippedContentSyntax` tagged with `MarkupBlock`.
    //
    // No-op when the cursor is already at a follow-set boundary:
    // `ParseAttributes` calls `ParseMiscAttribute` for the well-formed
    // `<p>` shape too (no whitespace before `>`), so the enhanced
    // branch must match the legacy no-op behaviour to avoid
    // contaminating clean markup with a spurious RZ1048.
    //
    // Test layout:
    //   - Corpus parse of `MalformedTagAttribute.razor` covers the
    //     `ParseAttribute.AttributeNameParsingResult.Other` call site
    //     (current is `=` after `<input @bind`).
    //   - In-memory `<input!garbage>` covers the
    //     `ParseAttributes` immediate-call site (no whitespace
    //     between tag name and the next token).
    //   - In-memory `<p>` (well-formed minimal tag) verifies the
    //     no-op-at-boundary guard (no spurious RZ1048).
    // ----------------------------------------------------------------

    [Fact]
    public void MalformedTagAttribute_EnhancedRecovery()
    {
        var testFile = TestFile.Create("ParserRecoveryCorpus/MalformedTagAttribute.razor", typeof(ParserRecoveryCorpusSnapshotTests));
        var source = testFile.ReadAllText();

        var tree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        // Position layout for `<input @bind=>\r\n\r\n<p>after the malformed bind</p>\r\n`:
        //   0      `<`
        //   1..5   `input`
        //   6      ` ` (whitespace)
        //   7      `@`
        //   8..11  `bind`
        //   12     `=`
        //   13     `>`
        //   14..17 `\r\n\r\n`
        //   18     `<`
        //   19     `p`
        //   20     `>`
        //   21..44 `after the malformed bind`
        //   45..48 `</p>`
        //   49..50 `\r\n`

        // The `<input ...>` start tag fires ParseMiscAttribute from the
        // `Other` branch of `ParseAttribute` with the cursor at `=`
        // (position 12). The enhanced branch emits a zero-width RZ1048
        // there and synchronizes to the close angle (`>` at position 13).
        // Legacy mode emits no diagnostic at this site; RZ1048 is the
        // net-new narrow diagnostic introduced in Stage 3.4.
        var rz1048 = tree.Diagnostics.Where(d => d.Id == "RZ1048").ToArray();
        var unexpectedAttributeNameDiagnostic = Assert.Single(rz1048);
        Assert.Equal(12, unexpectedAttributeNameDiagnostic.Span.AbsoluteIndex);
        Assert.Equal(0, unexpectedAttributeNameDiagnostic.Span.Length);

        // The `=` is absorbed into a `SkippedContentSyntax` tagged with
        // `MarkupBlock` (legacy mode wrapped it in a
        // `MarkupMiscAttributeContent` with a `MarkupTextLiteral`
        // child). The skipped span starts at `=` and stops at `>`.
        //
        // The `@bind` CSharp implicit expression produces its own
        // `SkippedContentSyntax` tagged with `CSharpCodeBlock` under
        // Stage 2.1's enhanced recovery; we filter to the markup-side
        // skipped node here.
        var startTag = tree.Root
            .DescendantNodes()
            .OfType<MarkupStartTagSyntax>()
            .First();
        var skipped = startTag
            .DescendantNodes()
            .OfType<SkippedContentSyntax>()
            .Single(s => s.OriginatingLanguage == SyntaxKind.MarkupBlock);
        Assert.Equal(12, skipped.SpanStart);
        Assert.Equal("=", skipped.GetContent());

        // The remaining `MarkupMiscAttributeContent` (wrapping the
        // ` @bind` CSharp expression in attribute-name position) comes
        // from `ParseAttribute`'s `AttributeNameParsingResult.CSharp`
        // branch -- NOT from `ParseMiscAttribute`. That wrapping is
        // not part of Stage 3.4's migration scope and is unchanged
        // under enhanced mode.
        var miscAttributeContents = startTag
            .DescendantNodes()
            .OfType<MarkupMiscAttributeContentSyntax>()
            .ToArray();
        var miscAttributeContent = Assert.Single(miscAttributeContents);
        Assert.Equal(6, miscAttributeContent.SpanStart);
        Assert.Equal(" @bind", miscAttributeContent.GetContent());

        // Stage 3.4 exit criterion: the `=` is no longer wrapped in a
        // `MarkupMiscAttributeContent`. The legacy baseline had two
        // MarkupMiscAttributeContent nodes inside the start tag (the
        // ` @bind` one above and a separate one for `=`); enhanced
        // mode replaces the `=` wrapper with a single
        // `SkippedContentSyntax` and emits the narrow RZ1048.

        // The trailing `<p>after the malformed bind</p>` parses as a
        // real, well-formed `MarkupElement` (no recovery contamination
        // leaks into trailing markup).
        var elements = tree.Root
            .DescendantNodes()
            .OfType<MarkupElementSyntax>()
            .ToArray();
        Assert.Equal(2, elements.Length);
        var pElement = elements[1];
        Assert.NotNull(pElement.MarkupStartTag);
        Assert.Equal("p", pElement.MarkupStartTag.Name.Content);
        Assert.False(pElement.MarkupStartTag.Name.IsMissing);
        Assert.NotNull(pElement.MarkupEndTag);
        Assert.Equal("p", pElement.MarkupEndTag.Name.Content);

        // ----------------------------------------------------------------
        // In-memory verification of the other ParseMiscAttribute call
        // site (the `ParseAttributes` immediate-when-no-whitespace path)
        // and of the no-op-at-boundary guard.
        // ----------------------------------------------------------------

        // Site #1 -- `ParseAttributes` immediate call. When there is
        // no whitespace between the tag name and the next token,
        // `ParseAttributes` invokes `ParseMiscAttribute` directly with
        // the cursor at the unexpected token. For `<input!garbage>`
        // the cursor is at `!` (position 6).
        {
            var immediateSource = "<input!garbage>";
            var immediateTree = ParseDocument(
                immediateSource,
                configureParserOptions: builder => builder.UseEnhancedRecovery = true);

            var immediateRz1048 = immediateTree.Diagnostics
                .Where(d => d.Id == "RZ1048")
                .ToArray();
            var immediateDiagnostic = Assert.Single(immediateRz1048);
            Assert.Equal(6, immediateDiagnostic.Span.AbsoluteIndex);
            Assert.Equal(0, immediateDiagnostic.Span.Length);

            // `!garbage` is wrapped in a `SkippedContentSyntax` tagged
            // with `MarkupBlock`. Synchronize stops at `>` (position 14).
            var immediateSkipped = immediateTree.Root
                .DescendantNodes()
                .OfType<SkippedContentSyntax>()
                .Single();
            Assert.Equal(SyntaxKind.MarkupBlock, immediateSkipped.OriginatingLanguage);
            Assert.Equal(6, immediateSkipped.SpanStart);
            Assert.Equal("!garbage", immediateSkipped.GetContent());

            // No `MarkupMiscAttributeContent` for this start tag --
            // there is no CSharp expression in attribute-name position
            // here, only absorbed garbage.
            var immediateStartTag = immediateTree.Root
                .DescendantNodes()
                .OfType<MarkupStartTagSyntax>()
                .Single();
            Assert.Empty(immediateStartTag.DescendantNodes().OfType<MarkupMiscAttributeContentSyntax>());

            // The start tag still has a real close angle.
            Assert.NotNull(immediateStartTag.CloseAngle);
            Assert.False(immediateStartTag.CloseAngle!.IsMissing);
            Assert.Equal(14, immediateStartTag.CloseAngle.SpanStart);
        }

        // No-op-at-boundary guard -- the well-formed `<p>` shape goes
        // through `ParseMiscAttribute` (the `ParseAttributes`
        // immediate-when-no-whitespace path: no whitespace between `p`
        // and `>`). The enhanced branch must NOT emit RZ1048 here.
        {
            var wellFormedSource = "<p></p>";
            var wellFormedTree = ParseDocument(
                wellFormedSource,
                configureParserOptions: builder => builder.UseEnhancedRecovery = true);

            // No RZ1048 and no SkippedContentSyntax: cursor was already
            // at the follow-set boundary (`>`), so the enhanced branch
            // returned without absorbing or diagnosing.
            Assert.Empty(wellFormedTree.Diagnostics.Where(d => d.Id == "RZ1048"));
            Assert.Empty(wellFormedTree.Root.DescendantNodes().OfType<SkippedContentSyntax>());

            // And the element parses cleanly.
            var wellFormedElement = wellFormedTree.Root
                .DescendantNodes()
                .OfType<MarkupElementSyntax>()
                .Single();
            Assert.NotNull(wellFormedElement.MarkupStartTag);
            Assert.Equal("p", wellFormedElement.MarkupStartTag.Name.Content);
            Assert.NotNull(wellFormedElement.MarkupEndTag);
            Assert.Equal("p", wellFormedElement.MarkupEndTag.Name.Content);
        }
    }

    private void ParseCorpusFile(string corpusFileName)
    {
        var testFile = TestFile.Create("ParserRecoveryCorpus/" + corpusFileName, typeof(ParserRecoveryCorpusSnapshotTests));
        Assert.True(testFile.Exists(), $"Corpus file not embedded: {corpusFileName}. Check the EmbeddedResource glob in the csproj.");
        var source = testFile.ReadAllText();
        ParseDocumentTest(source);
    }
}
