// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.AspNetCore.Razor.Language.Syntax.InternalSyntax;
using Xunit;

namespace Microsoft.AspNetCore.Razor.Language.Legacy;

public class TokenizerBackedParserRecoveryTests
{
    [Fact]
    public void FollowSet_Empty_ContainsNothing()
    {
        var set = FollowSet.Empty;
        Assert.True(set.IsEmpty);
        Assert.False(set.Contains(SyntaxKind.OpenAngle));
        Assert.False(set.Contains(SyntaxKind.Text));
    }

    [Fact]
    public void FollowSet_ContainsKindsItWasConstructedWith()
    {
        var set = new FollowSet(SyntaxKind.OpenAngle, SyntaxKind.Whitespace);
        Assert.False(set.IsEmpty);
        Assert.True(set.Contains(SyntaxKind.OpenAngle));
        Assert.True(set.Contains(SyntaxKind.Whitespace));
        Assert.False(set.Contains(SyntaxKind.Text));
        Assert.False(set.Contains(SyntaxKind.NewLine));
    }

    [Fact]
    public void FollowSet_Union_MergesBothSets()
    {
        var a = new FollowSet(SyntaxKind.OpenAngle);
        var b = new FollowSet(SyntaxKind.Whitespace);
        var u = a | b;
        Assert.True(u.Contains(SyntaxKind.OpenAngle));
        Assert.True(u.Contains(SyntaxKind.Whitespace));
        Assert.False(u.Contains(SyntaxKind.NewLine));
    }

    [Fact]
    public void FollowSet_Equality_ChecksValueEquality()
    {
        var a = new FollowSet(SyntaxKind.OpenAngle, SyntaxKind.Whitespace);
        var b = new FollowSet(SyntaxKind.Whitespace, SyntaxKind.OpenAngle);
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
    }

    [Fact]
    public void Synchronize_AtCurrentToken_ReturnsNullSkippedAndAtFollowToken()
    {
        // The first token is OpenAngle, which is in the follow set.
        using var harness = TestParserHarness.Create("<abc>");

        var result = harness.Parser.Synchronize(
            new FollowSet(SyntaxKind.OpenAngle),
            SyntaxKind.MarkupBlock);

        Assert.Null(result.Skipped);
        Assert.Equal(SyncStopReason.AtFollowToken, result.StopReason);
        Assert.Equal(SyntaxKind.OpenAngle, harness.Parser.GetCurrentToken().Kind);
    }

    [Fact]
    public void Synchronize_SkipsSingleTokenToLocalFollow()
    {
        // Tokens: Text("abc"), Whitespace(" "), Text("def").
        // Follow set = { Whitespace } -> skip 1 token, stop at Whitespace.
        using var harness = TestParserHarness.Create("abc def");

        var result = harness.Parser.Synchronize(
            new FollowSet(SyntaxKind.Whitespace),
            SyntaxKind.MarkupBlock);

        Assert.Equal(SyncStopReason.AtFollowToken, result.StopReason);
        Assert.NotNull(result.Skipped);
        Assert.Equal(SyntaxKind.SkippedContent, result.Skipped.Kind);
        Assert.Equal(SyntaxKind.MarkupBlock, result.Skipped.OriginatingLanguage);
        Assert.Equal(1, result.Skipped.SkippedTokens.Count);
        var skippedToken = result.Skipped.SkippedTokens[0];
        Assert.Equal(SyntaxKind.Text, skippedToken.Kind);
        Assert.Equal("abc", skippedToken.Content);
        Assert.Equal(SyntaxKind.Whitespace, harness.Parser.GetCurrentToken().Kind);
    }

    [Fact]
    public void Synchronize_StopsAtOuterFollowTokenWhenLocalFollowDoesNotMatch()
    {
        // Tokens: Text("abc"), Whitespace(" "), Text("def").
        // Local follow = {} (empty). Outer follow = { Whitespace } -> stop reason
        // is AtOuterFollowToken (signals "bail to caller" to a cross-language client).
        using var harness = TestParserHarness.Create("abc def");

        var result = harness.Parser.Synchronize(
            localFollow: FollowSet.Empty,
            outerFollow: new FollowSet(SyntaxKind.Whitespace),
            SyntaxKind.MarkupBlock);

        Assert.Equal(SyncStopReason.AtOuterFollowToken, result.StopReason);
        Assert.NotNull(result.Skipped);
        Assert.Equal(1, result.Skipped.SkippedTokens.Count);
        Assert.Equal(SyntaxKind.Text, result.Skipped.SkippedTokens[0].Kind);
        Assert.Equal(SyntaxKind.Whitespace, harness.Parser.GetCurrentToken().Kind);
    }

    [Fact]
    public void Synchronize_SkipsManyTokensUntilFollow()
    {
        // Tokens (HTML): Text("aaa"), Whitespace, Text("bbb"), Whitespace,
        //                Text("ccc"), Whitespace, OpenAngle("<").
        // Follow = { OpenAngle } -> skip 6 tokens, stop at OpenAngle.
        using var harness = TestParserHarness.Create("aaa bbb ccc <");

        var result = harness.Parser.Synchronize(
            new FollowSet(SyntaxKind.OpenAngle),
            SyntaxKind.MarkupBlock);

        Assert.Equal(SyncStopReason.AtFollowToken, result.StopReason);
        Assert.NotNull(result.Skipped);
        Assert.Equal(6, result.Skipped.SkippedTokens.Count);
        Assert.Equal(SyntaxKind.OpenAngle, harness.Parser.GetCurrentToken().Kind);
    }

    [Fact]
    public void Synchronize_HitsEndOfFileWhenFollowNeverMatches()
    {
        // Tokens: Text("abc"), Whitespace, Text("def"). No OpenAngle anywhere ->
        // skip everything and stop with EndOfFile.
        using var harness = TestParserHarness.Create("abc def");

        var result = harness.Parser.Synchronize(
            new FollowSet(SyntaxKind.OpenAngle),
            SyntaxKind.MarkupBlock);

        Assert.Equal(SyncStopReason.EndOfFile, result.StopReason);
        Assert.NotNull(result.Skipped);
        Assert.Equal(3, result.Skipped.SkippedTokens.Count);
        Assert.True(harness.Parser.GetEndOfFile());
    }

    [Fact]
    public void Synchronize_StopAtNewLine_StopsAtNewLine()
    {
        // Tokens: Text("abc"), NewLine, Text("def"). Local follow = {} so without
        // StopAtNewLine, the parser would skip past the newline. With it,
        // synchronization stops at the newline (StopReason = AtNewLine).
        using var harness = TestParserHarness.Create("abc\ndef");

        var result = harness.Parser.Synchronize(
            FollowSet.Empty,
            SyntaxKind.MarkupBlock,
            SyncOptions.StopAtNewLine);

        Assert.Equal(SyncStopReason.AtNewLine, result.StopReason);
        Assert.NotNull(result.Skipped);
        Assert.Equal(1, result.Skipped.SkippedTokens.Count);
        Assert.Equal(SyntaxKind.Text, result.Skipped.SkippedTokens[0].Kind);
        Assert.Equal(SyntaxKind.NewLine, harness.Parser.GetCurrentToken().Kind);
    }

    [Fact]
    public void Synchronize_StopAtTransition_StopsAtAtSign()
    {
        // Tokens: Text("abc"), Whitespace, Transition("@"), Text("def").
        // Without StopAtTransition the parser would skip past '@'; with it,
        // it stops on '@' with StopReason = AtTransition.
        using var harness = TestParserHarness.Create("abc @def");

        var result = harness.Parser.Synchronize(
            FollowSet.Empty,
            SyntaxKind.MarkupBlock,
            SyncOptions.StopAtTransition);

        Assert.Equal(SyncStopReason.AtTransition, result.StopReason);
        Assert.NotNull(result.Skipped);
        Assert.Equal(SyntaxKind.Transition, harness.Parser.GetCurrentToken().Kind);
    }

    [Fact]
    public void Synchronize_HonorsCancellation()
    {
        using var harness = TestParserHarness.Create("aaa bbb ccc ddd eee", CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var cancelledHarness = TestParserHarness.Create("aaa bbb ccc ddd eee", cts.Token);

        // Synchronizing with no matching follow set causes the loop to iterate;
        // the cancellation token check inside that loop must throw.
        Assert.Throws<OperationCanceledException>(() =>
            cancelledHarness.Parser.Synchronize(
                new FollowSet(SyntaxKind.OpenAngle),
                SyntaxKind.MarkupBlock));
    }

    [Fact]
    public void Synchronize_DoesNotCallAccept()
    {
        // After Synchronize, the parser's token builder must still be empty:
        // Synchronize produces a SkippedContentSyntax for the caller, it does
        // not push to the literal-token pipeline.
        using var harness = TestParserHarness.Create("aaa bbb <");

        var result = harness.Parser.Synchronize(
            new FollowSet(SyntaxKind.OpenAngle),
            SyntaxKind.MarkupBlock);

        Assert.Equal(SyncStopReason.AtFollowToken, result.StopReason);
        Assert.NotNull(result.Skipped);
        Assert.Equal(0, harness.Parser.GetTokenBuilderCount());
    }

    // ----------------------------------------------------------------
    // Test harness: a minimal HtmlMarkupParser-backed wrapper that
    // exposes the protected members needed to drive Synchronize from
    // tests. HtmlMarkupParser is chosen because it is a concrete
    // TokenizerBackedParser<HtmlTokenizer> and "plain text" inputs
    // tokenize predictably (Text / Whitespace / NewLine / OpenAngle /
    // Transition kinds).
    // ----------------------------------------------------------------
    private sealed class TestParserHarness : IDisposable
    {
        public ParserContext Context { get; }
        public TestHtmlMarkupParser Parser { get; }

        private readonly CSharpCodeParser _codeParser;

        private TestParserHarness(ParserContext context, TestHtmlMarkupParser parser, CSharpCodeParser codeParser)
        {
            Context = context;
            Parser = parser;
            _codeParser = codeParser;
        }

        public static TestParserHarness Create(string content, CancellationToken cancellationToken = default)
        {
            var source = TestRazorSourceDocument.Create(content, filePath: null, relativePath: null);
            var options = RazorParserOptions.Default;
            var context = new ParserContext(source, options, cancellationToken);
            var codeParser = new CSharpCodeParser(ImmutableArray<DirectiveDescriptor>.Empty, context);
            var markupParser = new TestHtmlMarkupParser(context)
            {
                CodeParser = codeParser
            };
            codeParser.HtmlParser = markupParser;

            // Prime the tokenizer so CurrentToken is populated before tests
            // call Synchronize. Synchronize itself also calls EnsureCurrent,
            // but priming here lets tests inspect CurrentToken pre-call too.
            markupParser.PrimeCurrent();

            return new TestParserHarness(context, markupParser, codeParser);
        }

        public void Dispose()
        {
            _codeParser.Dispose();
            Parser.Dispose();
            Context.Dispose();
        }
    }

    private sealed class TestHtmlMarkupParser(ParserContext context) : HtmlMarkupParser(context)
    {
        public void PrimeCurrent() => EnsureCurrent();

        public SyntaxToken GetCurrentToken() => CurrentToken;

        public bool GetEndOfFile() => EndOfFile;

        public int GetTokenBuilderCount() => TokenBuilder.Count;
    }
}
