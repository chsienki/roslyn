// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.AspNetCore.Razor.Language;

internal static partial class MarkupSplitter
{
    /// <summary>
    /// Splits a markup-bearing property/indexer into the Path A pair: a bodyless <em>defining</em>
    /// partial declaration for the decl half and an <em>implementing</em> partial declaration for the
    /// impl half. Returns <see langword="null"/> when the property can't be split this way (an
    /// initializer carries the markup, an unexpected piece shape, or offsets that don't line up), in
    /// which case the whole file falls back rather than risk emitting broken code.
    /// </summary>
    /// <param name="member">The classified property, whose parsed <see cref="ClassifiedMember.Syntax"/>
    /// gives attribute/type spans and whose <see cref="ClassifiedMember.Span"/> start is the analysis
    /// offset the first routed piece begins at.</param>
    /// <param name="pieces">The property's routed pieces in order (leading C#, markup, trailing C#).</param>
    internal static (ImmutableArray<IntermediateNode> Decl, ImmutableArray<IntermediateNode> Impl)? BuildPropertyDeclarations(
        ClassifiedMember member,
        ImmutableArray<IntermediateNode> pieces)
    {
        if (member.Syntax is not BasePropertyDeclarationSyntax property)
        {
            return null;
        }

        // A property whose markup is in its initializer (`{ get; set; } = @<...>`) keeps markup-free
        // accessors and needs the static-synth lift, not a partial-property split. Fall back.
        if (property is PropertyDeclarationSyntax { Initializer: not null })
        {
            return null;
        }

        // The first piece holds the property's leading C# (attributes + modifiers + type + name + the
        // start of the body). The implementing declaration reuses it -- keeping its source mapping -- with
        // the attributes stripped (they stay on the defining declaration, which is the discovery surface)
        // and `partial` inserted before the type.
        if (pieces.IsDefaultOrEmpty ||
            pieces[0] is not CSharpCodeIntermediateNode { Children: [CSharpIntermediateToken headerToken] })
        {
            return null;
        }

        var basis = member.Span.Start;
        var typeStart = property.Type.SpanStart - basis;

        int dropStart, dropEnd;
        if (property.AttributeLists.Count > 0)
        {
            dropStart = property.AttributeLists[0].Span.Start - basis;
            dropEnd = property.AttributeLists[property.AttributeLists.Count - 1].FullSpan.End - basis;
        }
        else
        {
            dropStart = dropEnd = typeStart;
        }

        var headerLength = headerToken.Content.Length;
        if (!(0 <= dropStart && dropStart <= dropEnd && dropEnd <= typeStart && typeStart <= headerLength))
        {
            // The parsed spans don't line up with the first piece's text (an unexpected shape); fall back
            // rather than slice at the wrong place.
            return null;
        }

        var implHeader = new CSharpCodeIntermediateNode();

        // [0, dropStart): whatever precedes the attributes (leading whitespace/trivia), kept mapped.
        if (dropStart > 0)
        {
            implHeader.Children.Add(SliceToken(headerToken, 0, dropStart));
        }

        // [dropEnd, typeStart): the modifiers, kept mapped. (Attributes in [dropStart, dropEnd) dropped.)
        if (typeStart > dropEnd)
        {
            implHeader.Children.Add(SliceToken(headerToken, dropEnd, typeStart - dropEnd));
        }

        // `partial` is synthesized (the user didn't write it), so it carries no source mapping.
        implHeader.Children.Add(new CSharpIntermediateToken("partial ", source: null));

        // [typeStart, end): the type, name, and start of the body, kept mapped.
        implHeader.Children.Add(SliceToken(headerToken, typeStart, headerLength - typeStart));

        var implBuilder = ImmutableArray.CreateBuilder<IntermediateNode>(pieces.Length);
        implBuilder.Add(implHeader);
        for (var i = 1; i < pieces.Length; i++)
        {
            implBuilder.Add(pieces[i]);
        }

        var defining = CreateGeneratedCSharp(WrapGeneratedSignature(BuildDefiningPropertyDeclaration(property)));

        return ([defining], implBuilder.ToImmutable());
    }

    // The generated defining declaration restates the property's type, which can duplicate a type-level
    // warning ([Obsolete], nullable) onto scaffolding the user can't see. Wrapping it in a pragma
    // disable/restore keeps the one real diagnostic on the mapped implementing declaration.
    private static string WrapGeneratedSignature(string declaration)
        => $"#pragma warning disable\n{declaration}\n#pragma warning restore\n";

    private static CSharpCodeIntermediateNode CreateGeneratedCSharp(string text)
    {
        var node = new CSharpCodeIntermediateNode();
        node.Children.Add(new CSharpIntermediateToken(text, source: null));
        return node;
    }

    /// <summary>
    /// Derives the Path A <em>defining</em> partial declaration for a markup-bearing property/indexer:
    /// the descriptor surface that stays in decl. It reproduces the member's attributes, modifiers,
    /// type, name (or <c>this[...]</c>), and <em>exact accessor set</em> as a bodyless
    /// <c>partial</c> declaration.
    /// </summary>
    /// <remarks>
    /// Signature fidelity is a hard requirement:
    /// <list type="bullet">
    /// <item>Attributes stay on the defining declaration (and are stripped from the implementing one, so
    /// an <c>AllowMultiple=false</c> attribute like <c>[Parameter]</c> isn't duplicated -- CS0579).</item>
    /// <item>The accessor set must match the implementing declaration exactly (CS9252), and adding an
    /// accessor would silently widen the API surface. An expression body implies <c>{ get; }</c>.</item>
    /// <item><c>init</c> vs <c>set</c> and per-accessor accessibility (e.g. <c>private set</c>) are
    /// preserved.</item>
    /// </list>
    /// The body carried by the parsed member is the analysis marker and is intentionally discarded; only
    /// the signature shape matters here.
    /// </remarks>
    internal static string BuildDefiningPropertyDeclaration(BasePropertyDeclarationSyntax member)
    {
        var builder = new StringBuilder();

        foreach (var attributeList in member.AttributeLists)
        {
            builder.Append(attributeList.ToString()).Append(' ');
        }

        foreach (var modifier in member.Modifiers)
        {
            builder.Append(modifier.Text).Append(' ');
        }

        builder.Append("partial ");
        builder.Append(member.Type.ToString()).Append(' ');

        switch (member)
        {
            case PropertyDeclarationSyntax property:
                builder.Append(property.Identifier.Text);
                break;

            case IndexerDeclarationSyntax indexer:
                builder.Append("this").Append(indexer.ParameterList.ToString());
                break;
        }

        builder.Append(' ');
        AppendAccessorSignature(builder, member);

        return builder.ToString();
    }

    // The bodyless accessor list matching the member's accessors exactly. An expression-bodied member
    // (`=> ...`) is a get-only property, so it becomes `{ get; }`; otherwise each declared accessor is
    // reproduced with its own accessibility modifiers and its `get`/`set`/`init` keyword.
    private static void AppendAccessorSignature(StringBuilder builder, BasePropertyDeclarationSyntax member)
    {
        if (member is PropertyDeclarationSyntax { ExpressionBody: not null } or
                      IndexerDeclarationSyntax { ExpressionBody: not null } ||
            member.AccessorList is null)
        {
            builder.Append("{ get; }");
            return;
        }

        builder.Append("{ ");
        foreach (var accessor in member.AccessorList.Accessors)
        {
            foreach (var modifier in accessor.Modifiers)
            {
                builder.Append(modifier.Text).Append(' ');
            }

            builder.Append(accessor.Keyword.Text).Append("; ");
        }

        builder.Append('}');
    }
}
