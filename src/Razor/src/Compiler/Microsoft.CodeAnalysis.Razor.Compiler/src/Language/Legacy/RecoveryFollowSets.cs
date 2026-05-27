// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.AspNetCore.Razor.Language.Legacy;

/// <summary>
/// Named <see cref="FollowSet"/> constants and cross-language translation
/// helpers used by the parser error-recovery machinery. See
/// <c>src/Razor/docs/parser-recovery.md</c> for the recovery model.
/// </summary>
internal static class RecoveryFollowSets
{
    public static readonly FollowSet Empty = FollowSet.Empty;

    /// <summary>
    /// Trailing-garbage follow set for C#-side directive parsers
    /// (<c>@addTagHelper</c>, <c>@inject</c>, <c>@using</c>, etc.).
    /// </summary>
    /// <remarks>
    /// Directives are line-terminated, so <see cref="SyntaxKind.NewLine"/>
    /// is the natural recovery boundary. <see cref="SyntaxKind.RightBrace"/>
    /// is included so a directive inside an enclosing <c>@{ ... }</c> code
    /// block syncs at the outer <c>}</c> rather than leaking malformed
    /// tokens out to the markup parser.
    /// </remarks>
    public static readonly FollowSet CSharpDirectiveTrailing =
        new(SyntaxKind.NewLine, SyntaxKind.RightBrace);

    /// <summary>
    /// Trailing-garbage follow set for the C#-side implicit-expression
    /// method-call / array-index recovery (<c>ParseMethodCallOrArrayIndex</c>'s
    /// <c>Balance</c>-failure branch).
    /// </summary>
    /// <remarks>
    /// Implicit expressions like <c>@foo.Bar(...)</c> have no syntactic
    /// terminator of their own; the expression ends at the next character
    /// that "isn't part of the implicit expression". The set covers the
    /// practical sync points: <see cref="SyntaxKind.LessThan"/> (handoff
    /// to the HTML parser), <see cref="SyntaxKind.NewLine"/> (stray newline
    /// inside an unclosed call), and <see cref="SyntaxKind.Whitespace"/>
    /// (whitespace inside a well-formed <c>Balance</c>-ed bracket is
    /// consumed by <c>Balance</c> itself; sync only fires after <c>Balance</c>
    /// fails).
    /// </remarks>
    public static readonly FollowSet CSharpImplicitExpressionTrailing =
        new(SyntaxKind.LessThan, SyntaxKind.NewLine, SyntaxKind.Whitespace);

    /// <summary>
    /// Tag-internal recovery follow set for the HTML-side <c>ParseStartTag</c>
    /// and <c>ParseEndTag</c>.
    /// </summary>
    /// <remarks>
    /// Captures every token that is a sensible "boundary" inside or around
    /// an HTML tag, so the recovery sync stops immediately at the cursor in
    /// the typical case (no skipped content produced). <see cref="SyntaxKind.Text"/>
    /// itself is omitted because stopping at <c>Text</c> while looking for
    /// <c>Text</c> is what triggers the consume path of <c>Required</c>.
    /// </remarks>
    public static readonly FollowSet HtmlTagRecovery =
        new(
            SyntaxKind.Whitespace,
            SyntaxKind.NewLine,
            SyntaxKind.OpenAngle,
            SyntaxKind.CloseAngle,
            SyntaxKind.ForwardSlash,
            SyntaxKind.Equals,
            SyntaxKind.DoubleQuote,
            SyntaxKind.SingleQuote,
            SyntaxKind.Transition);

    /// <summary>
    /// End-of-tag recovery follow set for the HTML-side <c>ParseMiscAttribute</c>.
    /// </summary>
    /// <remarks>
    /// Stopping at the tag terminators (<c>&lt;</c>, <c>&gt;</c>, <c>/</c>)
    /// and attribute-value quotes (<c>"</c>, <c>'</c>) keeps the recovered
    /// "miscellaneous attribute" range narrow and lets the surrounding
    /// <c>ParseAttributes</c> loop resume normal attribute parsing.
    /// </remarks>
    public static readonly FollowSet HtmlEndOfTagFollowSet =
        new(
            SyntaxKind.OpenAngle,
            SyntaxKind.CloseAngle,
            SyntaxKind.ForwardSlash,
            SyntaxKind.DoubleQuote,
            SyntaxKind.SingleQuote);

    /// <summary>
    /// Translates an HTML-side <see cref="FollowSet"/> into the equivalent
    /// C#-side set for use as the outer follow set when handing off into
    /// the C# parser.
    /// </summary>
    /// <remarks>
    /// The two tokenizers emit different <see cref="SyntaxKind"/> values for
    /// the same characters (HTML emits <see cref="SyntaxKind.OpenAngle"/>
    /// for <c>&lt;</c>; C# emits <see cref="SyntaxKind.LessThan"/>), so a
    /// follow set authored in one vocabulary is not directly meaningful in
    /// the other. The mapping is encoded in the method body. Quote kinds
    /// are dropped because the C# tokenizer absorbs them into
    /// <see cref="SyntaxKind.StringLiteral"/> / <see cref="SyntaxKind.CharacterLiteral"/>.
    /// </remarks>
    public static FollowSet ForCSharpCallee(FollowSet htmlSet)
    {
        if (htmlSet.IsEmpty)
        {
            return FollowSet.Empty;
        }

        var translated = FollowSet.Empty;

        if (htmlSet.Contains(SyntaxKind.OpenAngle))
        {
            translated |= new FollowSet(SyntaxKind.LessThan);
        }

        if (htmlSet.Contains(SyntaxKind.CloseAngle))
        {
            translated |= new FollowSet(SyntaxKind.GreaterThan);
        }

        if (htmlSet.Contains(SyntaxKind.ForwardSlash))
        {
            translated |= new FollowSet(SyntaxKind.Slash);
        }

        if (htmlSet.Contains(SyntaxKind.Whitespace))
        {
            translated |= new FollowSet(SyntaxKind.Whitespace);
        }

        if (htmlSet.Contains(SyntaxKind.NewLine))
        {
            translated |= new FollowSet(SyntaxKind.NewLine);
        }

        if (htmlSet.Contains(SyntaxKind.Equals))
        {
            translated |= new FollowSet(SyntaxKind.Equals);
        }

        if (htmlSet.Contains(SyntaxKind.Transition))
        {
            translated |= new FollowSet(SyntaxKind.Transition);
        }

        return translated;
    }

    /// <summary>
    /// Translates a C#-side <see cref="FollowSet"/> into the equivalent
    /// HTML-side set for use as the outer follow set when handing off into
    /// the HTML parser. The reverse of <see cref="ForCSharpCallee"/>.
    /// </summary>
    /// <remarks>
    /// C#-only structural kinds (<see cref="SyntaxKind.Semicolon"/>,
    /// braces, parens) are dropped because they have no HTML equivalent.
    /// </remarks>
    public static FollowSet ForHtmlCallee(FollowSet csharpSet)
    {
        if (csharpSet.IsEmpty)
        {
            return FollowSet.Empty;
        }

        var translated = FollowSet.Empty;

        if (csharpSet.Contains(SyntaxKind.LessThan))
        {
            translated |= new FollowSet(SyntaxKind.OpenAngle);
        }

        if (csharpSet.Contains(SyntaxKind.GreaterThan))
        {
            translated |= new FollowSet(SyntaxKind.CloseAngle);
        }

        if (csharpSet.Contains(SyntaxKind.Slash))
        {
            translated |= new FollowSet(SyntaxKind.ForwardSlash);
        }

        if (csharpSet.Contains(SyntaxKind.Whitespace))
        {
            translated |= new FollowSet(SyntaxKind.Whitespace);
        }

        if (csharpSet.Contains(SyntaxKind.NewLine))
        {
            translated |= new FollowSet(SyntaxKind.NewLine);
        }

        if (csharpSet.Contains(SyntaxKind.Equals))
        {
            translated |= new FollowSet(SyntaxKind.Equals);
        }

        if (csharpSet.Contains(SyntaxKind.Transition))
        {
            translated |= new FollowSet(SyntaxKind.Transition);
        }

        return translated;
    }
}