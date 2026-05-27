// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Syntax;
using Microsoft.AspNetCore.Razor.LanguageServer.Semantic;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Microsoft.CodeAnalysis.Razor.SemanticTokens;

/// <summary>
///  Stage 5.6 of the parser error-recovery redesign (see
///  <c>src/Razor/docs/plans/ErrorRecovery/razor-recovery-redesign-plan.md</c>):
///  classify <c>SkippedContentSyntax</c> uniformly as
///  <see cref="SemanticTokenTypes.RazorComment"/> so the user can visually
///  distinguish parser-skipped recovery regions from real source. We use
///  <c>RazorComment</c> rather than <c>MarkupComment</c> because skipped
///  content is Razor-level recovery metadata, not HTML content.
/// </summary>
public class SemanticTokensVisitorRecoveryTest
{
    [Fact]
    public void Classification_SkippedContent_AppearsAsComment()
    {
        // Source contains a SkippedContentSyntax wrapping `foo;` (same
        // shape pinned by UnclosedParenInsideCodeBlock_EnhancedRecovery
        // in ParserRecoveryCorpusSnapshotTests).
        const string Source = "@{ var x = (foo; }";

        var codeDocument = CreateCodeDocumentWithEnhancedRecovery(Source);
        var skipped = codeDocument.GetRequiredSyntaxRoot()
            .DescendantNodes()
            .OfType<SkippedContentSyntax>()
            .Single();

        var legend = TestRazorSemanticTokensLegendService.GetInstance(supportsVSExtensions: false);
        var razorCommentKind = legend.TokenTypes.RazorComment;

        var ranges = new List<SemanticRange>();
        SemanticTokensVisitor.AddSemanticRanges(
            ranges,
            codeDocument,
            new TextSpan(0, Source.Length),
            legend,
            colorCodeBackground: false);

        var skippedRange = ranges.FirstOrDefault(r =>
            r.Kind == razorCommentKind &&
            RangeOverlapsSpan(r, skipped.Span, Source));

        Assert.True(
            skippedRange.Kind == razorCommentKind,
            $"Expected at least one SemanticRange of kind RazorComment ({razorCommentKind}) overlapping the SkippedContent span. " +
            $"Got: [{string.Join(", ", ranges.Select(r => r.ToString()))}]");
    }

    private static bool RangeOverlapsSpan(SemanticRange range, TextSpan span, string source)
    {
        // Convert SemanticRange's line/character positions back to absolute
        // offsets and check overlap with the SkippedContent span.
        var sourceText = SourceText.From(source);
        var start = sourceText.Lines.GetPosition(new LinePosition(range.StartLine, range.StartCharacter));
        var end = sourceText.Lines.GetPosition(new LinePosition(range.EndLine, range.EndCharacter));
        var rangeSpan = TextSpan.FromBounds(start, end);
        return rangeSpan.OverlapsWith(span);
    }

    private static RazorCodeDocument CreateCodeDocumentWithEnhancedRecovery(string source)
    {
        var sourceDocument = RazorSourceDocument.Create(
            source,
            Encoding.UTF8,
            RazorSourceDocumentProperties.Default);
        var options = RazorParserOptions.Create(
            RazorLanguageVersion.Latest,
            RazorFileKind.Legacy,
            configure: builder => builder.UseEnhancedRecovery = true);

        var syntaxTree = RazorSyntaxTree.Parse(sourceDocument, options);

        var codeDocument = RazorCodeDocument.Create(sourceDocument);
        codeDocument = codeDocument.WithTagHelperRewrittenSyntaxTree(syntaxTree);
        codeDocument = codeDocument.WithTagHelperContext(TagHelperDocumentContext.GetOrCreate([]));
        return codeDocument;
    }
}
