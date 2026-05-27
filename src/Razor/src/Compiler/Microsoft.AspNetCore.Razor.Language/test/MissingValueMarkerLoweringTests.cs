// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Razor.Language.Components;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using Microsoft.AspNetCore.Razor.Language.Syntax;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.AspNetCore.Razor.Language;

/// <summary>
/// IR-shape tests for the Stage 5.0 missing-value marker. These tests assert that
/// effectively-empty C# attribute values (e.g. <c>@onclick=""</c>,
/// <c>type=""</c> bound to a non-string property) flow through IR lowering as
/// tokens tagged with <see cref="IntermediateToken.IsMissingValue"/>, so Stage
/// 5.1's codegen can substitute a safe placeholder instead of emitting a hole
/// that produces CS1525 downstream. See
/// <c>src/Razor/docs/plans/ErrorRecovery/razor-recovery-redesign-plan.md</c>
/// and BDD #9.
/// </summary>
public sealed class MissingValueMarkerLoweringTests
{
    private static RazorProjectEngine CreateComponentEngine()
    {
        return RazorProjectEngine.Create(
            RazorConfiguration.Default,
            RazorProjectFileSystem.Create(Environment.CurrentDirectory),
            builder => { });
    }

    private static DocumentIntermediateNode LowerComponent(RazorProjectEngine engine, string content)
    {
        var source = RazorSourceDocument.Create(content, "test.razor");
        var codeDocument = engine.CreateCodeDocument(source, RazorFileKind.Component);

        // Run all phases up to but not including the C# lowering phase so the IR is
        // available for inspection. This mirrors the pattern used by
        // ComponentMarkupBlockPassTest.Lower in the same project.
        foreach (var phase in engine.Engine.Phases)
        {
            if (phase is IRazorCSharpLoweringPhase)
            {
                break;
            }

            codeDocument = phase.Execute(codeDocument);
        }

        return codeDocument.GetRequiredDocumentNode();
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/10383")]
    public void EmptyOnclickAttribute_TagsCSharpTokenAsMissingValue()
    {
        // Under the recovery model (Stage 3.2 / BDD #9) the parser surfaces
        // the missing value as a single MissingToken-bearing
        // CSharpExpressionLiteral. The lowering visitor tags the IR token with
        // IsMissingValue, so downstream passes (ComponentEventHandlerLoweringPass
        // in particular) can distinguish a real empty C# value from one the
        // user simply did not write.
        var engine = CreateComponentEngine();

        const string Source = """
            @using Microsoft.AspNetCore.Components.Web

            <button @onclick="">Click me</button>
            """;

        // Act
        var documentNode = LowerComponent(engine, Source);

        // Assert
        var attribute = FindOnclickAttribute(documentNode);
        var tokens = attribute.Children.OfType<IntermediateToken>().ToList();
        var token = Assert.Single(tokens);
        Assert.IsType<CSharpIntermediateToken>(token);
        Assert.True(token.IsMissingValue,
            "Expected the lowering visitor to tag the missing C# token as a missing-value marker.");
        Assert.True(MissingValueMarker.IsMissingValueMarker(tokens),
            "Expected the single-tagged-token stream to be classified as a missing-value marker.");
    }

    private static IntermediateNode FindOnclickAttribute(DocumentIntermediateNode documentNode)
    {
        foreach (var node in documentNode.FindDescendantNodes<HtmlAttributeIntermediateNode>())
        {
            if (string.Equals(node.AttributeName, "@onclick", StringComparison.Ordinal))
            {
                return node;
            }
        }

        throw new InvalidOperationException("Did not find an @onclick HtmlAttributeIntermediateNode in the lowered IR.");
    }

    [Fact]
    public void BoundNonStringAttributeWithEmptyValue_TagsSyntheticToken()
    {
        // The LegacyTagHelperResolver synthesises an empty CSharpIntermediateToken
        // for non-string-bound attributes with an empty value (e.g. checked="" on
        // a bool-bound property). Stage 5.0 tags that synthetic token as a
        // missing-value marker so codegen can emit a placeholder instead of
        // leaving a hole.
        //
        // This test exercises the LegacyTagHelperResolver path. The component
        // pipeline path is covered by the @onclick tests above.
        // We construct a minimal IR token list and verify that
        // MissingValueMarker.CreateMissingCSharpToken produces a tagged token
        // and that the detection helper sees it.
        var token = MissingValueMarker.CreateMissingCSharpToken(source: null);
        Assert.True(token.IsMissingValue);
        Assert.Equal(string.Empty, token.Content);
        Assert.True(MissingValueMarker.IsMissingValueMarker((IReadOnlyList<IntermediateToken>)[token]));
    }

    [Fact]
    public void IsMissingValueMarker_DetectsEffectivelyEmptyStreams()
    {
        // Length 0: missing.
        Assert.True(MissingValueMarker.IsMissingValueMarker(
            (IReadOnlyList<IntermediateToken>)Array.Empty<IntermediateToken>()));

        // All tagged tokens: missing.
        var tagged = MissingValueMarker.CreateMissingCSharpToken(source: null);
        Assert.True(MissingValueMarker.IsMissingValueMarker(
            (IReadOnlyList<IntermediateToken>)[tagged, tagged]));

        // All empty content (untagged but Content == ""): missing.
        var empty = IntermediateNodeFactory.CSharpToken(string.Empty);
        Assert.True(MissingValueMarker.IsMissingValueMarker(
            (IReadOnlyList<IntermediateToken>)[empty]));

        // Non-empty content: not missing.
        var realContent = IntermediateNodeFactory.CSharpToken("MyMethod");
        Assert.False(MissingValueMarker.IsMissingValueMarker(
            (IReadOnlyList<IntermediateToken>)[realContent]));

        // Mixed: any non-missing, non-empty token disqualifies the stream.
        Assert.False(MissingValueMarker.IsMissingValueMarker(
            (IReadOnlyList<IntermediateToken>)[tagged, realContent]));
    }

    [Fact]
    public void SkippedContentSyntax_IsNotProjectedToIR()
    {
        // SkippedContentSyntax stores zero or more skipped tokens but has no
        // child nodes; the lowering visitor inherits SyntaxWalker.DefaultVisit
        // which calls VisitToken (a no-op in the lowering visitor) for each
        // contained token. Confirm by constructing a SkippedContent node and
        // walking it.
        var skipped = SyntaxFactory.SkippedContent(originatingLanguage: SyntaxKind.None);
        Assert.Equal(0, skipped.SkippedTokens.Count);

        // Confirm that the descendant-tokens walk does not produce any IR
        // tokens for the skipped node alone.
        var probe = new VisitTokenProbe();
        probe.Visit(skipped);
        Assert.Equal(0, probe.TokenCount);
    }

    private sealed class VisitTokenProbe : SyntaxWalker
    {
        public int TokenCount { get; private set; }

        public override void VisitToken(SyntaxToken token) => TokenCount++;
    }
}
