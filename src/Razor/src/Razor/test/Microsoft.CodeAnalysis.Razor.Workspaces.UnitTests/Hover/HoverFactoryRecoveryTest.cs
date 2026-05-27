// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Syntax;
using Microsoft.CodeAnalysis.Razor.Tooltip;
using Moq;
using Xunit;

namespace Microsoft.CodeAnalysis.Razor.Hover;

/// <summary>
/// Hovering on a zero-width <c>MissingToken</c> -- the placeholder the parser
/// inserts where a syntactically required token is absent -- must return no
/// hover content. The diagnostic emitted at the same position already
/// explains what is wrong; surfacing a "phantom" hover there would be both
/// misleading and useless for navigation.
/// </summary>
public class HoverFactoryRecoveryTest
{
    private static IComponentAvailabilityService CreateComponentAvailabilityService()
    {
        var mock = new Mock<IComponentAvailabilityService>(MockBehavior.Strict);
        mock.Setup(x => x.GetComponentAvailabilityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return mock.Object;
    }

    [Fact]
    public async Task Hover_OnMissingToken_FallsBackToDiagnostic()
    {
        // Source: `@onclick=""` on a Blazor component button. Under enhanced
        // recovery the parser produces the shape -- a zero-width
        // `MissingToken(Identifier)` inside a `CSharpExpressionLiteral`
        // covering the attribute value. The missing token lives at the
        // position immediately after the opening `"`. Hovering on that
        // position must produce no hover (the RZ2008 diagnostic is the
        // user-facing signal that the value is missing).
        const string Source = """
            @using Microsoft.AspNetCore.Components.Web

            <button @onclick="">Click me</button>
            """;

        var codeDocument = CreateCodeDocumentWithEnhancedRecovery(Source, isComponent: true);
        var root = codeDocument.GetRequiredSyntaxRoot();

        var missingToken = root.DescendantNodesAndTokens()
            .Where(t => t.IsToken)
            .Select(t => t.AsToken())
            .First(t => t.IsMissing);
        var missingTokenPosition = missingToken.SpanStart;

        var hover = await HoverFactory.GetHoverAsync(
            codeDocument,
            missingTokenPosition,
            new HoverDisplayOptions(MarkupKind.Markdown, SupportsVisualStudioExtensions: false),
            CreateComponentAvailabilityService(),
            CancellationToken.None);

        Assert.Null(hover);
    }

    [Fact]
    public void HasMissingTokenAt_ReturnsTrueForMissingTokenAtPosition()
    {
        // Companion unit test for the helper used by Hover_OnMissingToken_...
        // above. Pins the contract that owner.ChildNodesAndTokens() will
        // surface the zero-width missing token at the exact position
        // requested.
        const string Source = """
            @using Microsoft.AspNetCore.Components.Web

            <button @onclick="">Click me</button>
            """;

        var codeDocument = CreateCodeDocumentWithEnhancedRecovery(Source, isComponent: true);
        var root = codeDocument.GetRequiredSyntaxRoot();

        var missingToken = root.DescendantNodesAndTokens()
            .Where(t => t.IsToken)
            .Select(t => t.AsToken())
            .First(t => t.IsMissing);
        Assert.NotNull(missingToken.Parent);
        Assert.True(HoverFactory.HasMissingTokenAt(missingToken.Parent!, missingToken.SpanStart));

        // And the negative case: a position not at a missing token.
        Assert.False(HoverFactory.HasMissingTokenAt(missingToken.Parent!, missingToken.SpanStart + 5));
    }

    private static RazorCodeDocument CreateCodeDocumentWithEnhancedRecovery(string source, bool isComponent)
    {
        var fileKind = isComponent ? RazorFileKind.Component : RazorFileKind.Legacy;
        var sourceDocument = RazorSourceDocument.Create(
            source,
            Encoding.UTF8,
            RazorSourceDocumentProperties.Default);
        var options = RazorParserOptions.Create(
            RazorLanguageVersion.Latest,
            fileKind);

        var syntaxTree = RazorSyntaxTree.Parse(sourceDocument, options);

        var codeDocument = RazorCodeDocument.Create(sourceDocument);
        codeDocument = codeDocument.WithTagHelperRewrittenSyntaxTree(syntaxTree);
        codeDocument = codeDocument.WithTagHelperContext(TagHelperDocumentContext.GetOrCreate([]));
        return codeDocument;
    }
}
