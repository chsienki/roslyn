// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Syntax;
using Microsoft.CodeAnalysis.Razor.Protocol;
using Xunit;

namespace Microsoft.CodeAnalysis.Razor.Completion;

/// <summary>
/// When the cursor lands inside a <see cref="SkippedContentSyntax"/> -- the
/// node the parser produces while synchronizing past an error -- the
/// completion provider needs to know what language the skipped region
/// originated in so the host can dispatch to the appropriate delegated
/// language completion provider (C# / HTML) instead of falling back to
/// Razor-only completions. The originating language is carried on
/// <c>SkippedContentSyntax.OriginatingLanguage</c> (set by <c>Synchronize</c>
/// at the call site). <see cref="RazorCompletionListProvider.DetermineLanguageKind"/>
/// resolves this on the way into <see cref="RazorCompletionContext"/>.
/// </summary>
public class RazorCompletionListProviderRecoveryTest
{
    [Fact]
    public void Completion_InsideCSharpSkippedContent_OffersCSharpCompletions()
    {
        // Synthetic input that produces a `SkippedContentSyntax`
        // (OriginatingLanguage == CSharpCodeBlock) covering `foo;` -- same
        // shape pinned by `UnclosedParenInsideCodeBlock_EnhancedRecovery`
        // in ParserRecoveryCorpusSnapshotTests.
        //
        //   @{ var x = (foo; }
        //               ^^^^
        //               Wrapped in SkippedContentSyntax(CSharpCodeBlock).
        //
        // The cursor position used here lies inside `foo`, which means
        // the owner returned by FindInnermostNode is the skipped tokens
        // themselves or their parent SkippedContentSyntax.
        const string Source = "@{ var x = (foo; }";
        var fooPosition = Source.IndexOf("foo");
        var skipped = ParseAndFindSkipped(Source);
        Assert.Equal(SyntaxKind.CSharpCodeBlock, skipped.OriginatingLanguage);

        var owner = skipped.FindInnermostNode(fooPosition + 1, includeWhitespace: true);
        var languageKind = RazorCompletionListProvider.DetermineLanguageKind(owner);

        Assert.Equal(RazorLanguageKind.CSharp, languageKind);
    }

    [Fact]
    public void Completion_InsideHtmlSkippedContent_OffersHtmlCompletions()
    {
        // Synthetic input that produces a `SkippedContentSyntax`
        // (OriginatingLanguage == MarkupBlock) covering `!garbage` -- same
        // shape pinned by `MalformedTagAttribute_EnhancedRecovery` /
        // `UnnamedTag_EnhancedRecovery` tests, where
        // `ParseMiscAttribute` defers a HTML-side garbage absorption to
        // `Synchronize`.
        //
        //   <input!garbage>
        //         ^^^^^^^^
        //         Wrapped in SkippedContentSyntax(MarkupBlock).
        const string Source = "<input!garbage>";
        var garbagePosition = Source.IndexOf("!garbage");
        var skipped = ParseAndFindSkipped(Source);
        Assert.Equal(SyntaxKind.MarkupBlock, skipped.OriginatingLanguage);

        var owner = skipped.FindInnermostNode(garbagePosition + 1, includeWhitespace: true);
        var languageKind = RazorCompletionListProvider.DetermineLanguageKind(owner);

        Assert.Equal(RazorLanguageKind.Html, languageKind);
    }

    [Fact]
    public void Completion_OutsideSkippedContent_StaysRazor()
    {
        // Negative case: when the cursor is *not* inside a skipped region
        // the language kind stays at the default (Razor). This guards
        // against the dispatch helper being too eager and re-classifying
        // ordinary cursor positions.
        const string Source = "<p>hello</p>";
        var syntaxTree = ParseEnhanced(Source);
        var helloPosition = Source.IndexOf("hello");
        var owner = syntaxTree.Root.FindInnermostNode(helloPosition + 1, includeWhitespace: true);

        var languageKind = RazorCompletionListProvider.DetermineLanguageKind(owner);

        Assert.Equal(RazorLanguageKind.Razor, languageKind);
    }

    private static SkippedContentSyntax ParseAndFindSkipped(string source)
    {
        var tree = ParseEnhanced(source);
        return tree.Root.DescendantNodes().OfType<SkippedContentSyntax>().Single();
    }

    private static RazorSyntaxTree ParseEnhanced(string source)
    {
        var sourceDocument = RazorSourceDocument.Create(
            source,
            Encoding.UTF8,
            RazorSourceDocumentProperties.Default);
        var options = RazorParserOptions.Create(
            RazorLanguageVersion.Latest,
            RazorFileKind.Legacy);
        return RazorSyntaxTree.Parse(sourceDocument, options);
    }
}
