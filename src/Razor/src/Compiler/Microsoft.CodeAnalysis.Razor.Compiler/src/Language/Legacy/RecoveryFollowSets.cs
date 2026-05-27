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
}
