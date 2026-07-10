// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Razor.Language.Extensions;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using Microsoft.AspNetCore.Razor.PooledObjects;
using Microsoft.CodeAnalysis.CSharp;

namespace Microsoft.AspNetCore.Razor.Language;

/// <summary>
/// Decides, for a component's primary class body, which parts of the user's <c>@code</c> belong in
/// the markup-free "decl" half (the tag-helper descriptor surface) and which markup-bearing parts
/// belong in the "impl" half (lowered after tag-helper resolution).
/// </summary>
/// <remarks>
/// <para>
/// The <c>@code</c> contents arrive on the primary <see cref="ClassDeclarationIntermediateNode"/> as a
/// flat sequence of raw C# text (<see cref="CSharpCodeIntermediateNode"/> holding
/// <see cref="CSharpIntermediateToken"/>) interleaved with markup nodes. The vast majority of
/// <c>@code</c> is pure C# with no markup, so a cheap structural gate (<see cref="HasClassBodyMarkup"/>)
/// runs first: when the class body has no markup there is nothing to split and the caller falls back to
/// the single-file behavior without parsing anything.
/// </para>
/// <para>
/// The split decision is a pure function of the class body's IR content plus the effective C#
/// <see cref="LanguageVersion"/> (properties split differently on C# 13+, which has partial properties,
/// than on earlier versions). It is memoized on the primary class node's identity so the decl and impl
/// lowering phases -- which run back to back over the same document and therefore the same primary
/// class instance -- share a single computation and cannot disagree.
/// </para>
/// </remarks>
internal static partial class MarkupSplitter
{
    /// <summary>
    /// Identifier emitted into the throwaway analysis document to stand in for a markup transition, so
    /// the class body parses as ordinary C# without needing resolved tag helpers. It never appears in
    /// generated output. Markup is detected via the analysis segment table, never by matching this name
    /// (user code may legitimately contain a call of the same name).
    /// </summary>
    public const string MarkerMethodName = "__RazorMarkupTransition";

    // Keyed weakly on the primary class so the decision is released with the document's IR. Both
    // lowering phases pass the same primary class instance, so the first phase's computation is reused
    // by the second rather than re-parsed.
    private static readonly ConditionalWeakTable<ClassDeclarationIntermediateNode, SplitDecision> s_decisionCache = new();

    /// <summary>
    /// Returns the memoized <see cref="SplitDecision"/> for the given primary class, computing it once
    /// on a miss. Both lowering phases call this with the same primary class and the same document, so
    /// they observe an identical decision.
    /// </summary>
    public static SplitDecision GetOrCreateDecision(
        ClassDeclarationIntermediateNode primaryClass,
        MethodDeclarationIntermediateNode renderMethod,
        RazorParserOptions parserOptions)
    {
        ArgHelper.ThrowIfNull(primaryClass);
        ArgHelper.ThrowIfNull(renderMethod);
        ArgHelper.ThrowIfNull(parserOptions);

        // GetValue atomically returns the cached decision or invokes the factory once on a miss.
        // (AddOrUpdate isn't available on netstandard2.0.)
        return s_decisionCache.GetValue(primaryClass, _ => Split(primaryClass, renderMethod, parserOptions));
    }

    /// <summary>
    /// Computes the split decision for the given primary class body. Pure and uncached; direct callers
    /// such as unit tests use this, while the lowering phases go through <see cref="GetOrCreateDecision"/>.
    /// </summary>
    public static SplitDecision Split(
        ClassDeclarationIntermediateNode primaryClass,
        MethodDeclarationIntermediateNode renderMethod,
        RazorParserOptions parserOptions)
    {
        ArgHelper.ThrowIfNull(primaryClass);
        ArgHelper.ThrowIfNull(renderMethod);
        ArgHelper.ThrowIfNull(parserOptions);

        // Step 0: the fast path. With no class-body markup there is nothing that needs to move to the
        // impl half, so skip all analysis and let the caller keep the single-file behavior.
        if (!HasClassBodyMarkup(primaryClass, renderMethod))
        {
            return SplitDecision.NoSplit;
        }

        var languageVersion = NormalizedLanguageVersion(parserOptions);

        // Render the class body to a parse-only document (markup -> markers) and recover its members.
        var children = CollectClassBodyChildren(primaryClass, renderMethod);

        // The splitter only knows how to route raw C# and recognized markup. A class-body node of any
        // other kind -- an @inject or another structured/extension member the fail-safe gate flagged as
        // markup -- can't be safely placed, so render the whole file rather than risk mis-routing surface
        // into the impl half.
        foreach (var child in children)
        {
            if (!IsSupportedClassBodyNode(child))
            {
                return SplitDecision.Fallback(languageVersion, FallbackReason.UnsupportedClassBodyNode);
            }
        }

        var analysis = BuildAnalysisDocument(children);
        var classified = ClassifyMembers(analysis, parserOptions.CSharpParseOptions);

        // Unrecoverable structure (brace mismatch, or a marker outside every member): we can't trust the
        // boundaries, so render the file whole and discover its tag helper the existing way.
        if (classified is not { } members)
        {
            return SplitDecision.Fallback(languageVersion, FallbackReason.UnrecoverableParse);
        }

        // A markup-bearing property can only keep its signature in decl via a partial property, which is
        // C# 13+. Below that, fall back rather than emit a synthesized accessor lift.
        if (languageVersion < PartialPropertyMinLanguageVersion && HasMarkupProperty(members))
        {
            return SplitDecision.Fallback(languageVersion, FallbackReason.MarkupPropertyBelowCSharp13);
        }

        var routedMembers = BuildRoutedMembers(analysis, members);
        return new SplitDecision.SplitPlan(languageVersion, PropertySplitPath.PartialProperty, routedMembers);
    }

    /// <summary>
    /// The lowest C# version that supports partial properties, which the property split (Path A) relies
    /// on. A markup-bearing property below this version falls back instead of splitting.
    /// </summary>
    internal const LanguageVersion PartialPropertyMinLanguageVersion = LanguageVersion.CSharp13;

    private static bool HasMarkupProperty(ImmutableArray<ClassifiedMember> members)
    {
        foreach (var member in members)
        {
            if (member.Kind == MemberSplitKind.MarkupProperty)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True if the primary class body contains a markup transition (a node that can only be lowered
    /// after tag-helper resolution). Runs in O(children) with no parsing.
    /// </summary>
    public static bool HasClassBodyMarkup(
        ClassDeclarationIntermediateNode primaryClass,
        MethodDeclarationIntermediateNode renderMethod)
    {
        foreach (var child in primaryClass.Children)
        {
            if (ReferenceEquals(child, renderMethod) || child.IsSynthesizedHelper)
            {
                continue;
            }

            if (IsClassBodyMarkup(child))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Classifies a class-body child as markup rather than C#. Defined as the complement of the known
    /// C#/structured-declaration node kinds so that an unrecognized (e.g. newly introduced) markup node
    /// is treated as markup: erring toward running the split machinery is a harmless cost, whereas
    /// missing a markup node would let it leak into the resolution-free decl half.
    /// </summary>
    /// <remarks>
    /// This is the deliberately over-eager <em>gate</em> classifier. It can flag a non-markup extension
    /// node (an <c>@inject</c>) as "markup"; that only causes <see cref="Split"/> to run, which then sees
    /// the node isn't a kind it can route (<see cref="IsSupportedClassBodyNode"/>) and falls back. Routing
    /// itself uses the precise allow-list <see cref="IsMarkupNode"/>, never this predicate.
    /// </remarks>
    internal static bool IsClassBodyMarkup(IntermediateNode node)
        => node is not (CSharpCodeIntermediateNode or
                        FieldDeclarationIntermediateNode or
                        PropertyDeclarationIntermediateNode or
                        MethodDeclarationIntermediateNode);

    /// <summary>
    /// The precise allow-list of markup intermediate node kinds the splitter knows how to route to the
    /// impl half: an expression-position <see cref="TemplateIntermediateNode"/> (from <c>@&lt;...&gt;</c>)
    /// and the statement-position markup nodes. Unlike the fail-safe <see cref="IsClassBodyMarkup"/> gate,
    /// this is positive: a class-body node that is neither raw C# nor one of these kinds -- e.g. an
    /// <c>@inject</c> (<c>ComponentInjectIntermediateNode</c>, itself an
    /// <see cref="ExtensionIntermediateNode"/> just like <see cref="TemplateIntermediateNode"/>) or a
    /// structured member declaration -- is not treated as routable markup.
    /// </summary>
    internal static bool IsMarkupNode(IntermediateNode node)
        => node is TemplateIntermediateNode or
                   MarkupElementIntermediateNode or
                   MarkupBlockIntermediateNode or
                   HtmlContentIntermediateNode;

    /// <summary>
    /// A class-body node the splitter can route: raw C# text (which stays in decl or lifts to impl with
    /// its member) or a recognized markup node (which lifts to impl). Any other kind -- a structured or
    /// extension member such as <c>@inject</c> -- means the file can't be split and must fall back.
    /// </summary>
    internal static bool IsSupportedClassBodyNode(IntermediateNode node)
        => node is CSharpCodeIntermediateNode || IsMarkupNode(node);

    /// <summary>
    /// The ordered user-authored class-body children -- everything that isn't the render method or a
    /// synthesized helper -- in source order. This is the flat sequence of raw C# chunks and markup
    /// transitions the analysis document and routing operate over.
    /// </summary>
    internal static ImmutableArray<IntermediateNode> CollectClassBodyChildren(
        ClassDeclarationIntermediateNode primaryClass,
        MethodDeclarationIntermediateNode renderMethod)
    {
        using var builder = new PooledArrayBuilder<IntermediateNode>();

        foreach (var child in primaryClass.Children)
        {
            if (ReferenceEquals(child, renderMethod) || child.IsSynthesizedHelper)
            {
                continue;
            }

            builder.Add(child);
        }

        return builder.ToImmutableAndClear();
    }

    /// <summary>
    /// The effective C# <see cref="LanguageVersion"/> with <c>Default</c>/<c>Latest</c>/<c>Preview</c>
    /// resolved to a concrete version, so the split decision is stable across the source-generator and
    /// IDE local-view paths (both must produce byte-identical halves for cohosting).
    /// </summary>
    internal static LanguageVersion NormalizedLanguageVersion(RazorParserOptions parserOptions)
        => parserOptions.CSharpParseOptions.LanguageVersion.MapSpecifiedToEffectiveVersion();
}
