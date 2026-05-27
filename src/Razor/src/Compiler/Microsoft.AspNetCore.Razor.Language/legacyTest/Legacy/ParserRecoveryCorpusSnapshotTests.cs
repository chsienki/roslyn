// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

    private void ParseCorpusFile(string corpusFileName)
    {
        var testFile = TestFile.Create("ParserRecoveryCorpus/" + corpusFileName, typeof(ParserRecoveryCorpusSnapshotTests));
        Assert.True(testFile.Exists(), $"Corpus file not embedded: {corpusFileName}. Check the EmbeddedResource glob in the csproj.");
        var source = testFile.ReadAllText();
        ParseDocumentTest(source);
    }
}
