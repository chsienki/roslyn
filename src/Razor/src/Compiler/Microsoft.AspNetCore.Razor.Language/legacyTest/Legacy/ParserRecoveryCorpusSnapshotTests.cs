// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq;
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

        // The corpus input `@if(foo bar\nbaz\n\n<p>...</p>` is parsed
        // via `ParseImplicitExpression` / `ParseMethodCallOrArrayIndex`
        // (Stage 2.6 territory), NOT `ParseStandardStatement`. The
        // implicit-expression recovery at line ~636 of CSharpCodeParser
        // (`AcceptUntil(SyntaxKind.LessThan)`) is unchanged by Stage 2.3
        // and is owned by Stage 2.6.
        //
        // Parity assertions: enhanced mode produces the same shape as
        // legacy (the existing `UnclosedIfParen.{stree,diag}` baselines).
        // The single RZ1027 (`Parsing_ExpectedCloseBracketBeforeEOF`)
        // from `Balance` remains -- its narrowing belongs to Stage 2.6.
        // No new RZ1046 fires (the panic-else didn't run).
        var rz1027 = tree.Diagnostics.Where(d => d.Id == "RZ1027").ToArray();
        Assert.Single(rz1027);
        Assert.Empty(tree.Diagnostics.Where(d => d.Id == "RZ1046"));
        Assert.Empty(tree.Root.DescendantNodes().OfType<SkippedContentSyntax>());
        Assert.Empty(tree.Root.DescendantNodes().OfType<MarkupMiscAttributeContentSyntax>());

        // The trailing `<p>this should still parse as HTML</p>` parses
        // as a real markup element after the unclosed `@if(...`. The
        // `</p>` is the end-tag of that element -- not absorbed as a
        // fat literal.
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

    private void ParseCorpusFile(string corpusFileName)
    {
        var testFile = TestFile.Create("ParserRecoveryCorpus/" + corpusFileName, typeof(ParserRecoveryCorpusSnapshotTests));
        Assert.True(testFile.Exists(), $"Corpus file not embedded: {corpusFileName}. Check the EmbeddedResource glob in the csproj.");
        var source = testFile.ReadAllText();
        ParseDocumentTest(source);
    }
}
