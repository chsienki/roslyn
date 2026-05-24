// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.Linq;
using Microsoft.AspNetCore.Razor.Language.Syntax;
using Xunit;

namespace Microsoft.AspNetCore.Razor.Language.Legacy;

public class CSharpRazorCommentsTest() : ParserTestBase(layer: TestProject.Layer.Compiler, validateSpanEditHandlers: true, useLegacyTokenizer: true)
{
    [Fact]
    public void UnterminatedRazorComment()
    {
        ParseDocumentTest("@*");
    }

    [Fact]
    public void EmptyRazorComment()
    {
        ParseDocumentTest("@**@");
    }

    [Fact]
    public void RazorCommentInImplicitExpressionMethodCall()
    {
        ParseDocumentTest("""
            @foo(
            @**@

            """);
    }

    [Fact]
    public void UnterminatedRazorCommentInImplicitExpressionMethodCall()
    {
        ParseDocumentTest("@foo(@*");
    }

    [Fact]
    public void RazorMultilineCommentInBlock()
    {
        ParseDocumentTest(@"
@{
    @*
This is a comment
    *@
}
");
    }

    [Fact]
    public void RazorCommentInVerbatimBlock()
    {
        ParseDocumentTest("""
            @{
                <text
                @**@
            }
            """);
    }

    [Fact]
    public void RazorCommentInOpeningTagBlock()
    {
        ParseDocumentTest("<text @* razor comment *@></text>");
    }

    [Fact]
    public void RazorCommentInClosingTagBlock()
    {
        ParseDocumentTest("<text></text @* razor comment *@>");
    }

    [Fact]
    public void UnterminatedRazorCommentInVerbatimBlock()
    {
        ParseDocumentTest("@{@*");
    }

    [Fact]
    public void RazorCommentInMarkup()
    {
        ParseDocumentTest("""
            <p>
            @**@
            </p>
            """);
    }

    [Fact]
    public void MultipleRazorCommentInMarkup()
    {
        ParseDocumentTest("""
            <p>
              @**@  
            @**@
            </p>
            """);
    }

    [Fact]
    public void MultipleRazorCommentsInSameLineInMarkup()
    {
        ParseDocumentTest("""
            <p>
            @**@  @**@
            </p>
            """);
    }

    [Fact]
    public void RazorCommentsSurroundingMarkup()
    {
        ParseDocumentTest("""
            <p>
            @* hello *@ content @* world *@
            </p>
            """);
    }

    [Fact]
    public void RazorCommentBetweenCodeBlockAndMarkup()
    {
        ParseDocumentTest("""
            @{ }
            @* Hello World *@
            <div>Foo</div>
            """        );
    }

    [Fact]
    public void RazorCommentWithExtraNewLineInMarkup()
    {
        ParseDocumentTest("""
            <p>

            @* content *@
            @*
            content
            *@

            </p>
            """);
    }

    // ----------------------------------------------------------------
    // Stage 1.4 pilot: enhanced-recovery counterpart to
    // `UnterminatedRazorComment` above. The legacy test (line 13) drives
    // `ParseDocumentTest` against the baseline harness and locks in
    // today's diagnostic shape (RZ1028 with `(start, contentLength: 2)`
    // span; the source-code-level double `ErrorSink.OnError` plus
    // token-attached diagnostic dedupe to one user-visible entry).
    //
    // This test exercises the new `UseEnhancedRecovery` code path in
    // `TokenizerBackedParser.ParseRazorComment` introduced by Stage 1.4
    // and asserts:
    //   1. The tree still has `MissingToken(RazorCommentStar)` and
    //      `MissingToken(RazorCommentTransition)` at the EOF cursor.
    //   2. The enhanced-mode diagnostic count does not exceed the legacy
    //      baseline for the same input. Empirically (see invariant
    //      below) both modes produce exactly one RZ1028 diagnostic.
    //
    // The test is in-memory (no .stree/.diag baseline) per the plan's
    // note that the snapshot harness isn't a clean fit for an
    // enhanced-mode-only test running alongside its legacy twin.
    // ----------------------------------------------------------------
    [Fact]
    public void ParseRazorComment_Unterminated_EnhancedRecovery()
    {
        const string source = "@*";

        var legacyTree = ParseDocument(source);
        var legacyDiagnosticCount = legacyTree.Diagnostics.Length;

        var enhancedTree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        var razorComment = enhancedTree.Root
            .DescendantNodes()
            .OfType<RazorCommentBlockSyntax>()
            .Single();

        Assert.False(razorComment.StartCommentTransition.IsMissing);
        Assert.False(razorComment.StartCommentStar.IsMissing);

        // The closing `*` is missing at the EOF position (offset 2 in `@*`).
        Assert.True(razorComment.EndCommentStar.IsMissing);
        Assert.Equal(SyntaxKind.RazorCommentStar, razorComment.EndCommentStar.Kind);
        Assert.Equal(2, razorComment.EndCommentStar.SpanStart);
        Assert.Equal(0, razorComment.EndCommentStar.Span.Length);

        // The closing `@` is missing at the same EOF position. Per the new
        // recovery model, only the `EndCommentStar` token carries the
        // diagnostic; `EndCommentTransition` is a plain missing token
        // (avoiding a redundant copy of the same RZ1028 diagnostic).
        Assert.True(razorComment.EndCommentTransition.IsMissing);
        Assert.Equal(SyntaxKind.RazorCommentTransition, razorComment.EndCommentTransition.Kind);
        Assert.Equal(2, razorComment.EndCommentTransition.SpanStart);
        Assert.Equal(0, razorComment.EndCommentTransition.Span.Length);

        var rz1028 = enhancedTree.Diagnostics.Where(d => d.Id == "RZ1028").ToArray();
        Assert.Single(rz1028);
        Assert.Equal(2, rz1028[0].Span.AbsoluteIndex);
        Assert.Equal(0, rz1028[0].Span.Length);

        // Plan exit criterion: enhanced-mode diagnostic count must not
        // exceed the legacy baseline for the same input.
        Assert.True(
            enhancedTree.Diagnostics.Length <= legacyDiagnosticCount,
            $"Enhanced-mode produced {enhancedTree.Diagnostics.Length} diagnostics; legacy baseline was {legacyDiagnosticCount}.");
    }
}
