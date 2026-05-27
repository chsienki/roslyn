// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.AspNetCore.Razor.Language.Legacy;

/// <summary>
/// Named <see cref="FollowSet"/> constants used by the parser error-recovery
/// machinery. See <c>docs/plans/ErrorRecovery/razor-recovery-redesign-plan.md</c>
/// (Stage 4.1) for the catalogue of follow sets each call-site is meant to
/// thread.
/// </summary>
/// <remarks>
/// Stage 1.1 seeds this file with only the universal entries that the
/// machinery itself depends on (notably <see cref="Empty"/>). Subsequent
/// stages populate the language-specific sets (HTML attribute terminators,
/// C# statement terminators, etc.) and the cross-language translation
/// helpers (<c>ForCSharpCallee</c> / <c>ForHtmlCallee</c>) as the parsers
/// are migrated to call <see cref="TokenizerBackedParser{TTokenizer}.Synchronize"/>.
/// </remarks>
internal static class RecoveryFollowSets
{
    /// <summary>An empty follow set. Identical to <see cref="FollowSet.Empty"/>.</summary>
    public static readonly FollowSet Empty = FollowSet.Empty;

    /// <summary>
    /// Trailing-garbage follow set for C#-side directive parsers
    /// (<c>@addTagHelper</c>, <c>@removeTagHelper</c>, <c>@tagHelperPrefix</c>,
    /// <c>@using</c>, extensible directives like <c>@inherits</c> / <c>@inject</c> /
    /// <c>@namespace</c> / etc.). Added in Stage 2.5 of the recovery plan.
    ///
    /// The kinds are C#-side per Big Design Decision #4 -- the directive
    /// parsers all run inside the C# tokenizer's kind set.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="SyntaxKind.NewLine"/> -- directives are line-terminated,
    ///     so the end of the current line is the natural recovery boundary.
    ///     A stray <c>&lt;</c> on the directive's line is absorbed as part of
    ///     the SkippedContent rather than leaking out to the markup parser
    ///     (which would otherwise produce a fake <c>MarkupStartTag</c> +
    ///     <c>MarkupMiscAttributeContent</c>); the next line's real markup
    ///     resumes cleanly after the newline.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="SyntaxKind.RightBrace"/> -- a directive inside an enclosing
    ///     <c>@{ ... }</c> code block syncs at the outer <c>}</c> rather than
    ///     leaking malformed tokens out to the markup parser.
    ///   </description></item>
    /// </list>
    /// </remarks>
    public static readonly FollowSet CSharpDirectiveTrailing =
        new(SyntaxKind.NewLine, SyntaxKind.RightBrace);

    /// <summary>
    /// Trailing-garbage follow set for the C#-side implicit-expression
    /// method-call / array-index recovery (<c>ParseMethodCallOrArrayIndex</c>'s
    /// <c>Balance</c>-failure branch). Added in Stage 2.6 of the recovery plan.
    ///
    /// The kinds are C#-side per Big Design Decision #4 -- implicit expressions
    /// run inside the C# tokenizer's kind set.
    /// </summary>
    /// <remarks>
    /// Implicit expressions like <c>@foo.Bar(...)</c> or <c>@foo[...]</c> have
    /// no syntactic terminator of their own -- the expression ends at the next
    /// character that "isn't part of the implicit expression". The follow set
    /// captures the three practical sync points:
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="SyntaxKind.LessThan"/> -- the canonical handoff to the
    ///     HTML parser (e.g. <c>@foo.Bar(baz&lt;/p&gt;</c>).
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="SyntaxKind.NewLine"/> -- a stray newline inside an
    ///     unclosed call ends the line scope; subsequent markup resumes on
    ///     the next line.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="SyntaxKind.Whitespace"/> -- whitespace marks the end of
    ///     an implicit expression. Note that whitespace inside a well-formed
    ///     <c>Balance</c>-ed bracket is consumed by <c>Balance</c> itself; the
    ///     sync only fires after <c>Balance</c> fails, at which point a
    ///     whitespace token is a legitimate boundary.
    ///   </description></item>
    /// </list>
    /// </remarks>
    public static readonly FollowSet CSharpImplicitExpressionTrailing =
        new(SyntaxKind.LessThan, SyntaxKind.NewLine, SyntaxKind.Whitespace);

    /// <summary>
    /// Tag-internal recovery follow set for the HTML-side
    /// <c>ParseStartTag</c> / <c>ParseEndTag</c> migrations (Stage 3.1 of the
    /// recovery plan). The kinds are HTML-side per Big Design Decision #4.
    /// </summary>
    /// <remarks>
    /// Used by <c>Required(SyntaxKind.Text, ...)</c> when the tag name is
    /// missing and by <c>Required(SyntaxKind.CloseAngle, ...)</c> when the
    /// closing <c>&gt;</c> is missing. The set captures every token that is
    /// a sensible "boundary" inside or around an HTML tag, so the recovery
    /// sync stops immediately at the cursor in the typical case (no skipped
    /// content produced). The omitted kinds are <see cref="SyntaxKind.Text"/>
    /// itself (since stopping at <c>Text</c> while looking for <c>Text</c>
    /// is what triggers the consume path of <c>Required</c>) and the
    /// razor-comment / unrelated kinds, which would be absorbed as
    /// <see cref="SkippedContentSyntax"/> on the rare paths that reach them.
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="SyntaxKind.Whitespace"/>, <see cref="SyntaxKind.NewLine"/>
    ///     -- intra-tag separators and line boundaries.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="SyntaxKind.OpenAngle"/>, <see cref="SyntaxKind.CloseAngle"/>,
    ///     <see cref="SyntaxKind.ForwardSlash"/> -- tag terminators / next-tag
    ///     boundary.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="SyntaxKind.Equals"/>, <see cref="SyntaxKind.DoubleQuote"/>,
    ///     <see cref="SyntaxKind.SingleQuote"/> -- attribute boundaries.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="SyntaxKind.Transition"/> -- a Razor <c>@</c> transition
    ///     embedded in or after the tag.
    ///   </description></item>
    /// </list>
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
    /// End-of-tag recovery follow set for the HTML-side
    /// <c>ParseMiscAttribute</c> migration (Stage 3.4 of the recovery plan).
    /// The kinds are HTML-side per Big Design Decision #4.
    /// </summary>
    /// <remarks>
    /// Used by <c>ParseMiscAttribute</c> in enhanced-recovery mode when the
    /// cursor lands on something that isn't a valid attribute name (e.g.
    /// the legacy <c>AttributeNameParsingResult.Other</c> branch, or the
    /// "no whitespace after the tag name" branch in <c>ParseAttributes</c>).
    /// The set captures the boundary kinds that delimit the absorbed
    /// "miscellaneous attribute" range: the surrounding tag terminators
    /// (<c>&lt;</c>, <c>&gt;</c>, <c>/</c>) and the quote kinds (<c>"</c>,
    /// <c>'</c>) that bracket attribute values. Synchronisation stops at
    /// these kinds so the recovered range stays narrow -- garbage between
    /// the cursor and the next tag boundary is absorbed as
    /// <see cref="SkippedContentSyntax"/>, replacing the legacy "fat"
    /// <c>MarkupMiscAttributeContent</c> wrapper.
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="SyntaxKind.OpenAngle"/>, <see cref="SyntaxKind.CloseAngle"/>,
    ///     <see cref="SyntaxKind.ForwardSlash"/> -- tag terminators (next-tag
    ///     start, end of current tag, self-closing slash).
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="SyntaxKind.DoubleQuote"/>, <see cref="SyntaxKind.SingleQuote"/>
    ///     -- attribute-value quote boundaries. The legacy
    ///     <c>ParseMiscAttribute</c> absorbed quoted segments wholesale into the
    ///     fat <c>MarkupMiscAttributeContent</c>; the enhanced version stops
    ///     at the quote and lets the surrounding <c>ParseAttributes</c> loop
    ///     resume normal attribute parsing, which keeps quoted attribute
    ///     values out of the recovered skipped range.
    ///   </description></item>
    /// </list>
    /// </remarks>
    public static readonly FollowSet HtmlEndOfTagFollowSet =
        new(
            SyntaxKind.OpenAngle,
            SyntaxKind.CloseAngle,
            SyntaxKind.ForwardSlash,
            SyntaxKind.DoubleQuote,
            SyntaxKind.SingleQuote);

    /// <summary>
    /// Translates an HTML-side <see cref="FollowSet"/> into the equivalent C#-side
    /// set, for use as the outer follow set when handing off into the C# parser.
    /// See Big Design Decision #4 of the recovery plan for the translation table.
    /// </summary>
    /// <remarks>
    /// Per BDD #4 the two tokenizers emit different <see cref="SyntaxKind"/> values
    /// for the same characters: HTML emits <see cref="SyntaxKind.OpenAngle"/> for
    /// <c>&lt;</c>, while C# emits <see cref="SyntaxKind.LessThan"/> for the same
    /// character. A follow set authored in one tokenizer's vocabulary is not
    /// directly meaningful to the other, so the cross-parser handoff translates
    /// at the boundary.
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="SyntaxKind.OpenAngle"/> -&gt; <see cref="SyntaxKind.LessThan"/>.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="SyntaxKind.CloseAngle"/> -&gt; <see cref="SyntaxKind.GreaterThan"/>.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="SyntaxKind.ForwardSlash"/> -&gt; <see cref="SyntaxKind.Slash"/>.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="SyntaxKind.DoubleQuote"/> / <see cref="SyntaxKind.SingleQuote"/>
    ///     are dropped: the C# tokenizer absorbs <c>"</c> as part of a
    ///     <see cref="SyntaxKind.StringLiteral"/> and <c>'</c> as part of a
    ///     <see cref="SyntaxKind.CharacterLiteral"/>, so these quote kinds never
    ///     appear as standalone tokens in C# follow sets.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="SyntaxKind.Whitespace"/>, <see cref="SyntaxKind.NewLine"/>,
    ///     <see cref="SyntaxKind.Equals"/>, <see cref="SyntaxKind.Transition"/>:
    ///     shared structural kinds (same token kind in both tokenizers), passed
    ///     through unchanged.
    ///   </description></item>
    ///   <item><description>
    ///     All other kinds are dropped (no equivalent in the C#-side vocabulary
    ///     in the recovery contexts of interest).
    ///   </description></item>
    /// </list>
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

        // DoubleQuote / SingleQuote: dropped -- C# tokenizer absorbs these into
        // StringLiteral / CharacterLiteral, so the quote kinds are not useful
        // sync tokens on the C# side.

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
    /// Translates a C#-side <see cref="FollowSet"/> into the equivalent HTML-side
    /// set, for use as the outer follow set when handing off into the HTML parser.
    /// See Big Design Decision #4 of the recovery plan for the translation table.
    /// </summary>
    /// <remarks>
    /// The reverse direction of <see cref="ForCSharpCallee(FollowSet)"/>; see that
    /// method for the architectural rationale.
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="SyntaxKind.LessThan"/> -&gt; <see cref="SyntaxKind.OpenAngle"/>.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="SyntaxKind.GreaterThan"/> -&gt; <see cref="SyntaxKind.CloseAngle"/>.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="SyntaxKind.Slash"/> -&gt; <see cref="SyntaxKind.ForwardSlash"/>.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="SyntaxKind.Semicolon"/>, <see cref="SyntaxKind.LeftBrace"/>,
    ///     <see cref="SyntaxKind.RightBrace"/>, <see cref="SyntaxKind.LeftParenthesis"/>,
    ///     <see cref="SyntaxKind.RightParenthesis"/>: dropped (no HTML equivalent).
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="SyntaxKind.Whitespace"/>, <see cref="SyntaxKind.NewLine"/>,
    ///     <see cref="SyntaxKind.Equals"/>, <see cref="SyntaxKind.Transition"/>:
    ///     shared structural kinds, passed through unchanged.
    ///   </description></item>
    ///   <item><description>
    ///     All other kinds are dropped (no equivalent in the HTML-side vocabulary
    ///     in the recovery contexts of interest).
    ///   </description></item>
    /// </list>
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

        // Semicolon / LeftBrace / RightBrace / LeftParenthesis / RightParenthesis:
        // dropped -- these are C#-only structural kinds with no HTML equivalent.

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
