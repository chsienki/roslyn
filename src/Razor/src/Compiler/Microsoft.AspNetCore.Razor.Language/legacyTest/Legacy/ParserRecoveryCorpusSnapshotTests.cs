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

    private void ParseCorpusFile(string corpusFileName)
    {
        var testFile = TestFile.Create("ParserRecoveryCorpus/" + corpusFileName, typeof(ParserRecoveryCorpusSnapshotTests));
        Assert.True(testFile.Exists(), $"Corpus file not embedded: {corpusFileName}. Check the EmbeddedResource glob in the csproj.");
        var source = testFile.ReadAllText();
        ParseDocumentTest(source);
    }
}
