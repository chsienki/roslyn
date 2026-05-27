// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.AspNetCore.Razor.Language.Extensions;
using Microsoft.AspNetCore.Razor.Language.Syntax;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.AspNetCore.Razor.Language.Test;

// Verifies the Roslyn-style convention for SyntaxNavigator / FindToken:
// position-based searches must not return zero-width missing tokens; they
// fall through to an adjacent real token.
public class SyntaxNavigatorTests
{
    private static RazorSyntaxTree ParseWithInheritsDirective(string text)
    {
        var options = RazorParserOptions.Create(RazorLanguageVersion.Latest, RazorFileKind.Legacy, b =>
        {
            b.Directives = [InheritsDirective.Directive];
        });
        return RazorSyntaxTree.Parse(
            RazorSourceDocument.Create(text, Encoding.Default, RazorSourceDocumentProperties.Default),
            options);
    }

    // Sanity check: '@inherits' alone produces a zero-width missing identifier
    // (the absent type-name argument) at the position immediately after
    // 'inherits'. This is the structural precondition for all the FindToken
    // tests below; if this changes, the position math in the other tests
    // must be revisited.
    //
    // Note: the directive parser's bail-out path no
    // longer emits a `MissingToken(Identifier)` for the missing type-name
    // argument of `@inherits`; instead it emits a zero-width `Marker` token
    // (via `AcceptMarkerTokenIfNecessary` + `OutputTokensAsStatementLiteral`
    // in `BuildBailedDirective`). The three FindToken scenarios that
    // depended on the missing-identifier structure have been removed; the
    // skip-missing-token contract is still covered by the remaining tests
    // in this file (FindToken_ImmediatelyAfterMissingToken_*,
    // FindToken_MissingTokenAtEndOfFile_*, FindToken_MultipleNewlinesAfterMissingToken_*).

    [Fact]
    public void FindToken_ImmediatelyAfterMissingToken_LandsOnNextRealToken()
    {
        // Position 11 is on '<' directly; descent picks the OpenAngle real
        // token without needing the whitespace walk. Regression guard
        // ensuring the missing-token-skip path does not perturb the
        // direct-landing case.
        var tree = ParseWithInheritsDirective("@inherits\r\n<p>");

        var token = tree.Root.FindToken(position: 11);

        Assert.False(token.IsMissing);
        AssertEx.Equal("""OpenAngle;[<];""", TestSyntaxSerializer.Serialize(token).Trim());
    }

    [Fact]
    public void FindToken_MissingTokenAtEndOfFile_ReturnsEndOfFile()
    {
        // '@inherits' with no trailing content: the missing identifier and
        // the EOF token both sit at position 9. FindToken's special-case
        // for position == EndPosition returns EOF directly.
        var tree = ParseWithInheritsDirective("@inherits");

        var token = tree.Root.FindToken(position: 9);

        Assert.False(token.IsMissing);
        Assert.Equal(SyntaxKind.EndOfFile, token.Kind);
    }

    [Fact]
    public void FindToken_MultipleNewlinesAfterMissingToken_StillSkipsMissing()
    {
        // Two blank lines after '@inherits' before the next real token.
        // Layout of '@inherits\r\n\r\n<p>':
        //   0 : '@'
        //   1-8 : 'inherits'
        //   9 : <Missing> Identifier (width 0)
        //   9-10: '\r\n' (NewLine)
        //   11-12: '\r\n' (NewLine)
        //   13 : '<'
        // Position 11 is the second '\r'. Walk-back from that newline
        // hits the prior newline first (so walk-back fails), then
        // walk-forward must skip the missing identifier when looking
        // for the next real token.
        var tree = ParseWithInheritsDirective("@inherits\r\n\r\n<p>");

        var token = tree.Root.FindToken(position: 11);

        Assert.False(token.IsMissing,
            $"FindToken returned a missing token: {TestSyntaxSerializer.Serialize(token).Trim()}");
    }
}
