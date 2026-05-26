// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Syntax;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Microsoft.CodeAnalysis.Razor.Formatting;

/// <summary>
///  Regression guards for Stage 5.5 of the parser error-recovery redesign
///  (see <c>src/Razor/docs/plans/ErrorRecovery/razor-recovery-redesign-plan.md</c>).
///
///  These tests confirm that <see cref="FormattingVisitor"/> -- the walker
///  that produces <see cref="FormattingSpan"/>s for the indentation /
///  whitespace passes -- correctly handles the two new shapes the enhanced
///  parser introduces:
///
///    * <c>MissingToken</c> (zero width) must NOT contribute a formatting
///      span (it has no source position to format) and must NOT shift the
///      span boundaries of its siblings.
///    * <c>SkippedContentSyntax</c> is opaque: its tokens are visited
///      (so the walker descends without crashing) but no formatting span is
///      emitted for them. The user's malformed text is therefore preserved
///      verbatim by the surrounding formatter pass instead of being
///      re-indented.
///
///  The audit conclusion is that both behaviours fall out of the existing
///  defensive guards in <see cref="FormattingVisitor"/> (the
///  <c>IsMissing</c> / <c>IsEmpty</c> early-returns in <c>AddSpan</c>) and
///  the absence of a <c>VisitSkippedContent</c> override (so the default
///  walker descends to the skipped tokens which all hit the no-op
///  <c>VisitToken</c>). These tests pin that behaviour so future visitor
///  refactors don't silently regress it.
/// </summary>
public class FormattingVisitorRecoveryTest
{
    [Fact]
    public void UnclosedCodeBlock_MissingCloseBrace_DoesNotEmitZeroWidthSpan()
    {
        // From legacyTest/ParserRecoveryCorpus/UnclosedCodeBlock.razor.
        // The enhanced parser produces a `CSharpStatementBodySyntax` whose
        // CloseBrace's `}` token is `MissingToken(RightBrace)` at the EOF
        // position (Stage 2.2). The formatter must not emit a zero-width
        // span for it -- doing so would let the indentation pass shift the
        // following content based on a phantom position.
        var spans = GetSpansEnhanced("@{ var x = 1;\r\n\r\n<p>markup that follows the unclosed code block</p>\r\n");

        Assert.All(spans, span => Assert.True(span.Span.Length > 0, $"FormattingSpan with empty width emitted: {span}"));
    }

    [Fact]
    public void EmptyBoundAttribute_Onclick_MissingValueToken_DoesNotEmitZeroWidthSpan()
    {
        // From legacyTest/ParserRecoveryCorpus/EmptyBoundAttribute_Onclick.razor.
        // Under enhanced recovery + Component file-kind the `@onclick=""`
        // attribute produces the BDD #9 shape:
        //   GenericBlock([ CSharpExpressionLiteral([ MissingToken(Identifier) ]) ])
        // where the missing identifier is zero-width at the position
        // immediately after the opening `"`. The formatter must not emit a
        // span for the missing token (which would otherwise widen the
        // attribute's markup span to include a phantom code region) and
        // must not synthesise a space between the two `"`s when it walks
        // the attribute block.
        var spans = GetSpansEnhanced(
            EmptyBoundAttributeSource,
            fileKind: RazorFileKind.Component);

        // Sanity: the corpus parses cleanly into formatting spans.
        Assert.NotEmpty(spans);

        // The zero-width missing Identifier inside the `@onclick=""`
        // attribute value (BDD #9) lives at the position immediately after
        // the opening quote. Verify no span was emitted at exactly that
        // position with length 0. Combined with the
        // length-greater-than-zero assertion this also catches any future
        // visitor that creates a span on the missing token itself.
        var emptyQuotePosition = EmptyBoundAttributeSource.IndexOf("\"\"") + 1;
        Assert.All(spans, span =>
        {
            Assert.True(span.Span.Length > 0, $"FormattingSpan with empty width emitted: {span}");
            Assert.False(
                span.Span.Start == emptyQuotePosition && span.Span.End == emptyQuotePosition,
                $"FormattingSpan emitted at the missing-token position {emptyQuotePosition}: {span}");
        });
    }

    [Fact]
    public void SkippedContent_DoesNotEmitFormattingSpans()
    {
        // Synthetic input that produces a `SkippedContentSyntax` covering
        // `foo;` (Stage 2.3 -- `TryBalanceBlock` enhanced recovery branch
        // for an unclosed paren inside `@{ ... }`).
        //
        //   @{ var x = (foo; }
        //                ^^^^
        //                Wrapped in SkippedContentSyntax(CSharpCodeBlock).
        //
        // The formatter must not emit a `FormattingSpan` covering the
        // skipped tokens: that's how the surrounding pass leaves the
        // malformed text in place rather than re-indenting it. The
        // visitor's default descent over the SkippedContent node hits
        // `VisitToken` (no-op) for each skipped token, so no spans should
        // be added inside the skipped range.
        const string source = "@{ var x = (foo; }";
        var spans = GetSpansEnhanced(source);

        var fooStart = source.IndexOf("foo");
        var fooEnd = source.IndexOf(';') + 1;

        Assert.All(spans, span =>
        {
            // A span is considered to overlap the skipped region if its
            // start lies inside [fooStart..fooEnd) -- we don't reject
            // spans that merely extend up to fooStart (those are the
            // precise CSharpStatementLiteral that the parser flushes
            // before recovery), only spans that begin inside the
            // skipped tokens.
            Assert.False(
                span.Span.Start >= fooStart && span.Span.Start < fooEnd,
                $"FormattingSpan begins inside the skipped content region [{fooStart}..{fooEnd}): {span}");
        });
    }

    [Fact]
    public void SkippedContent_DescentDoesNotThrow()
    {
        // Regression guard: even if a future visitor regression makes
        // `VisitSkippedContent` throw on the new node kind, this test
        // pins the contract that the default walker descent over
        // SkippedContent must complete (the visitor has no override; the
        // default behaviour visits the child tokens which all hit the
        // no-op `VisitToken`).
        const string source = "@{ var x = (foo; }";
        var ex = Record.Exception(() => GetSpansEnhanced(source));
        Assert.Null(ex);
    }

    private const string EmptyBoundAttributeSource = """
        <h1>Counter</h1>

        <p>Current count: @currentCount</p>

        <button class="btn btn-primary" @onclick="">Click me</button>

        @code {
            private int currentCount = 0;

            private void IncrementCount()
            {
                currentCount++;
            }
        }

        """;

    private static ImmutableArray<FormattingSpan> GetSpansEnhanced(string source, RazorFileKind fileKind = RazorFileKind.Component)
    {
        var sourceDocument = RazorSourceDocument.Create(source, Encoding.UTF8, RazorSourceDocumentProperties.Default);
        var options = RazorParserOptions.Create(
            RazorLanguageVersion.Latest,
            fileKind,
            configure: builder => builder.UseEnhancedRecovery = true);

        var tree = RazorSyntaxTree.Parse(sourceDocument, options);

        var builder = ImmutableArray.CreateBuilder<FormattingSpan>();
        FormattingVisitor.VisitRoot(tree, builder, inGlobalNamespace: false);
        return builder.ToImmutable();
    }
}
