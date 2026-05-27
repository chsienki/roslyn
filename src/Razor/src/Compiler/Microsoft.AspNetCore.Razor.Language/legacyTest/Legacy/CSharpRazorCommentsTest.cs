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
    // Originally a Stage 1.4 pilot pair to the snapshot-harness
    // `UnterminatedRazorComment` test (line 13). With enhanced
    // recovery now the only mode, this asserts the in-memory tree
    // shape:
    //   1. `MissingToken(RazorCommentStar)` and `MissingToken(RazorCommentTransition)`
    //      at the EOF cursor.
    //   2. Exactly one RZ1028 diagnostic at the missing-terminator span.
    // ----------------------------------------------------------------
    [Fact]
    public void ParseRazorComment_Unterminated_EnhancedRecovery()
    {
        const string source = "@*";

        var enhancedTree = ParseDocument(source);

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
    }
}
