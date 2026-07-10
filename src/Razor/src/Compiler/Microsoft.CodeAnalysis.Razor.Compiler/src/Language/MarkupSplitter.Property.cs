// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.AspNetCore.Razor.Language;

internal static partial class MarkupSplitter
{
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
