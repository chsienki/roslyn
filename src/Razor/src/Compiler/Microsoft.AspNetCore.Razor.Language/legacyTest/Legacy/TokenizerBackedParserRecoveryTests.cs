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
    // Required / Optional tests (Stage 1.2).
    // ----------------------------------------------------------------

    [Fact]
    public void Required_AtExpectedKind_ConsumesAndReturnsTokenWithNoSkipped()
    {
        // Current token kind is OpenAngle and we require OpenAngle: consume it.
        using var harness = TestParserHarness.Create("<abc>");

        var diagnostic = CreateTestDiagnostic();
        var (token, skipped) = harness.Parser.Required(
            SyntaxKind.OpenAngle,
            diagnostic,
            FollowSet.Empty,
            SyntaxKind.MarkupBlock);

        Assert.Equal(SyntaxKind.OpenAngle, token.Kind);
        Assert.Equal("<", token.Content);
        Assert.False(token.IsMissing);
        Assert.Empty(token.GetDiagnostics());
        Assert.Null(skipped);

        // Cursor must have advanced past the consumed token.
        Assert.Equal(SyntaxKind.Text, harness.Parser.GetCurrentToken().Kind);

        // Required must not push to the literal-token pipeline.
        Assert.Equal(0, harness.Parser.GetTokenBuilderCount());

        // And it must not have copied the diagnostic into the ErrorSink.
        Assert.Empty(harness.Context.ErrorSink.GetErrorsAndClear());
    }

    [Fact]
    public void Required_KindMissing_EmitsMissingTokenAndSynchronizesToRecovery()
    {
        // Tokens: Text("abc"), Whitespace(" "), Text("def"). We require OpenAngle.
        // Recovery follow = { Whitespace }: missing token + skip "abc", stop at " ".
        using var harness = TestParserHarness.Create("abc def");

        var diagnostic = CreateTestDiagnostic();
        var (token, skipped) = harness.Parser.Required(
            SyntaxKind.OpenAngle,
            diagnostic,
            new FollowSet(SyntaxKind.Whitespace),
            SyntaxKind.MarkupBlock);

        Assert.Equal(SyntaxKind.OpenAngle, token.Kind);
        Assert.True(token.IsMissing);
        Assert.Same(diagnostic, Assert.Single(token.GetDiagnostics()));

        Assert.NotNull(skipped);
        Assert.Equal(SyntaxKind.SkippedContent, skipped.Kind);
        Assert.Equal(SyntaxKind.MarkupBlock, skipped.OriginatingLanguage);
        Assert.Equal(1, skipped.SkippedTokens.Count);
        var skippedToken = skipped.SkippedTokens[0];
        Assert.Equal(SyntaxKind.Text, skippedToken.Kind);
        Assert.Equal("abc", skippedToken.Content);

        Assert.Equal(SyntaxKind.Whitespace, harness.Parser.GetCurrentToken().Kind);
    }

    [Fact]
    public void Required_KindMissingAtEndOfFile_EmitsMissingTokenWithNullSkipped()
    {
        // Synchronization at EOF skips nothing -> Skipped is null.
        using var harness = TestParserHarness.Create("");

        Assert.True(harness.Parser.GetEndOfFile());

        var diagnostic = CreateTestDiagnostic();
        var (token, skipped) = harness.Parser.Required(
            SyntaxKind.OpenAngle,
            diagnostic,
            FollowSet.Empty,
            SyntaxKind.MarkupBlock);

        Assert.Equal(SyntaxKind.OpenAngle, token.Kind);
        Assert.True(token.IsMissing);
        Assert.Same(diagnostic, Assert.Single(token.GetDiagnostics()));
        Assert.Null(skipped);
    }

    [Fact]
    public void Required_KindMissingWithEmptyRecovery_SkipsToEndOfFile()
    {
        // Tokens: Text("abc"), Whitespace, Text("def"). FollowSet.Empty matches
        // nothing, so synchronization runs all the way to EOF.
        using var harness = TestParserHarness.Create("abc def");

        var diagnostic = CreateTestDiagnostic();
        var (token, skipped) = harness.Parser.Required(
            SyntaxKind.OpenAngle,
            diagnostic,
            FollowSet.Empty,
            SyntaxKind.MarkupBlock);

        Assert.True(token.IsMissing);
        Assert.NotNull(skipped);
        Assert.Equal(3, skipped.SkippedTokens.Count);
        Assert.True(harness.Parser.GetEndOfFile());
    }

    [Fact]
    public void Required_MultiKind_MatchesFirstKind()
    {
        // Current token is Whitespace. Acceptable = [Whitespace, NewLine].
        using var harness = TestParserHarness.Create(" abc");

        var diagnostic = CreateTestDiagnostic();
        var (token, skipped) = harness.Parser.Required(
            ImmutableArray.Create(SyntaxKind.Whitespace, SyntaxKind.NewLine),
            diagnostic,
            FollowSet.Empty,
            SyntaxKind.MarkupBlock);

        Assert.Equal(SyntaxKind.Whitespace, token.Kind);
        Assert.False(token.IsMissing);
        Assert.Empty(token.GetDiagnostics());
        Assert.Null(skipped);
        Assert.Equal(SyntaxKind.Text, harness.Parser.GetCurrentToken().Kind);
    }

    [Fact]
    public void Required_MultiKind_MatchesSecondKind()
    {
        // Current token is NewLine. Acceptable = [Whitespace, NewLine].
        using var harness = TestParserHarness.Create("\nabc");

        var diagnostic = CreateTestDiagnostic();
        var (token, skipped) = harness.Parser.Required(
            ImmutableArray.Create(SyntaxKind.Whitespace, SyntaxKind.NewLine),
            diagnostic,
            FollowSet.Empty,
            SyntaxKind.MarkupBlock);

        Assert.Equal(SyntaxKind.NewLine, token.Kind);
        Assert.False(token.IsMissing);
        Assert.Null(skipped);
    }

    [Fact]
    public void Required_MultiKind_NoneMatch_EmitsMissingTokenOfFirstKind()
    {
        // Current token is Text("abc"). Acceptable = [Whitespace, NewLine].
        // Missing token must have Kind == acceptableKinds[0] (= Whitespace).
        using var harness = TestParserHarness.Create("abc def");

        var diagnostic = CreateTestDiagnostic();
        var (token, skipped) = harness.Parser.Required(
            ImmutableArray.Create(SyntaxKind.Whitespace, SyntaxKind.NewLine),
            diagnostic,
            new FollowSet(SyntaxKind.Text),
            SyntaxKind.MarkupBlock);

        Assert.Equal(SyntaxKind.Whitespace, token.Kind);
        Assert.True(token.IsMissing);
        Assert.Same(diagnostic, Assert.Single(token.GetDiagnostics()));

        // Recovery contains Text, which is the current token, so Synchronize
        // immediately stops with no skipped content.
        Assert.Null(skipped);
        Assert.Equal(SyntaxKind.Text, harness.Parser.GetCurrentToken().Kind);
    }

    [Fact]
    public void Required_MissingPath_AttachesDiagnosticToMissingToken_AndDoesNotEmitToErrorSink()
    {
        // Stage 1.2 exit criterion: a missing-token Required emits exactly one
        // diagnostic copy -- attached to the token. The ErrorSink must NOT
        // receive an additional copy.
        using var harness = TestParserHarness.Create("abc");

        var diagnostic = CreateTestDiagnostic();
        var (token, _) = harness.Parser.Required(
            SyntaxKind.OpenAngle,
            diagnostic,
            FollowSet.Empty,
            SyntaxKind.MarkupBlock);

        Assert.Same(diagnostic, Assert.Single(token.GetDiagnostics()));
        Assert.Empty(harness.Context.ErrorSink.GetErrorsAndClear());
    }

    [Fact]
    public void Optional_AtExpectedKind_ConsumesAndReturnsToken()
    {
        using var harness = TestParserHarness.Create("<abc>");

        var token = harness.Parser.Optional(SyntaxKind.OpenAngle);

        Assert.NotNull(token);
        Assert.Equal(SyntaxKind.OpenAngle, token.Kind);
        Assert.Equal(SyntaxKind.Text, harness.Parser.GetCurrentToken().Kind);
    }

    [Fact]
    public void Optional_KindMissing_ReturnsNullAndDoesNotAdvance()
    {
        using var harness = TestParserHarness.Create("abc");

        var token = harness.Parser.Optional(SyntaxKind.OpenAngle);

        Assert.Null(token);
        Assert.Equal(SyntaxKind.Text, harness.Parser.GetCurrentToken().Kind);
        Assert.Empty(harness.Context.ErrorSink.GetErrorsAndClear());
    }

    private static RazorDiagnostic CreateTestDiagnostic()
    {
        // A descriptor whose ID does not clash with any real RZ id (lower-case
        // prefix). This stays inside the test class and is never written to a
        // tree; we only need a `RazorDiagnostic` instance to thread through
        // `Required`.
        var descriptor = new RazorDiagnosticDescriptor(
            id: "test0001",
            messageFormat: "test diagnostic",
            severity: RazorDiagnosticSeverity.Error);
        return RazorDiagnostic.Create(descriptor, SourceSpan.Undefined);
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
