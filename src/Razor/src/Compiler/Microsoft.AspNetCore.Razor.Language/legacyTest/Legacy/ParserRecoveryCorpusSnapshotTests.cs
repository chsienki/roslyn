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

    private void ParseCorpusFile(string corpusFileName)
    {
        var testFile = TestFile.Create("ParserRecoveryCorpus/" + corpusFileName, typeof(ParserRecoveryCorpusSnapshotTests));
        Assert.True(testFile.Exists(), $"Corpus file not embedded: {corpusFileName}. Check the EmbeddedResource glob in the csproj.");
        var source = testFile.ReadAllText();
        ParseDocumentTest(source);
    }
}
