// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.AspNetCore.Razor.Language;

/// <summary>
/// The outcome of analyzing a component's primary class body for the decl/impl markup split. One of
/// three cases:
/// <list type="bullet">
/// <item><see cref="NoSplit"/> -- no class-body markup, so the caller keeps the single-file behavior.</item>
/// <item><see cref="SplitPlan"/> -- the class body mixes markup and C# and can be split safely into a
/// markup-free decl half and a markup-bearing impl half; describes how the pieces route.</item>
/// <item><see cref="SplitFallback"/> -- the class body has markup but can't be split safely (a markup
/// property below C# 13, or an unrecoverable parse), so the caller renders the whole tree unsplit and
/// discovers its tag helper the existing (non-incremental) way.</item>
/// </list>
/// </summary>
/// <remarks>
/// This is a closed hierarchy. It is produced once per primary class and shared by both lowering
/// phases, so both the <see cref="SplitPlan"/> and <see cref="SplitFallback"/> cases carry the
/// normalized language version they were computed against to keep the two source-generator/IDE paths
/// byte-identical across the C# 12/13/14 boundaries (including the split/fallback boundary at C# 13).
/// The source generator inspects the decision to route each file to incremental (split) or existing
/// (fallback) tag-helper discovery.
/// </remarks>
internal abstract class SplitDecision
{
    private protected SplitDecision()
    {
    }

    /// <summary>
    /// The class body has no markup (or nothing that needs splitting); the caller keeps the single-file
    /// behavior. Shared singleton.
    /// </summary>
    public static SplitDecision NoSplit { get; } = new NoSplitDecision();

    /// <summary>
    /// The class body has markup but can't be split safely; the caller renders the whole tree unsplit
    /// and discovers its tag helper the existing way.
    /// </summary>
    public static SplitFallback Fallback(LanguageVersion normalizedLanguageVersion, FallbackReason reason)
        => new(normalizedLanguageVersion, reason);

    /// <summary>True when this decision requires the caller to build separate decl and impl halves.</summary>
    public bool RequiresSplit => this is SplitPlan;

    /// <summary>
    /// True when the file has markup but can't be split, so the caller must render the whole tree and use
    /// the existing tag-helper discovery instead of the incremental (decl-based) path.
    /// </summary>
    public bool IsFallback => this is SplitFallback;

    private sealed class NoSplitDecision : SplitDecision
    {
    }

    /// <summary>
    /// The class body has markup that can't be split safely, so the file is left unsplit: the whole tree
    /// is rendered normally and its tag helper is discovered the existing (non-incremental) way. Always
    /// correct -- just not the incremental fast path.
    /// </summary>
    public sealed class SplitFallback : SplitDecision
    {
        public SplitFallback(LanguageVersion normalizedLanguageVersion, FallbackReason reason)
        {
            NormalizedLanguageVersion = normalizedLanguageVersion;
            Reason = reason;
        }

        /// <summary>
        /// The effective C# version the decision was computed against (with Default/Latest/Preview
        /// resolved). Recorded so the split/fallback choice is stable across the source-generator and
        /// IDE local-view paths.
        /// </summary>
        public LanguageVersion NormalizedLanguageVersion { get; }

        /// <summary>Why the file falls back instead of splitting (for diagnostics/telemetry and tests).</summary>
        public FallbackReason Reason { get; }
    }

    /// <summary>
    /// Describes how each class-body member routes between the decl and impl halves. Produced only when
    /// the class body mixes markup and C# and the file can be split safely.
    /// </summary>
    public sealed class SplitPlan : SplitDecision
    {
        public SplitPlan(
            LanguageVersion normalizedLanguageVersion,
            PropertySplitPath propertyPath,
            ImmutableArray<RoutedMember> members)
        {
            NormalizedLanguageVersion = normalizedLanguageVersion;
            PropertyPath = propertyPath;
            Members = members.NullToEmpty();
        }

        /// <summary>
        /// The effective C# version the plan was computed against (with Default/Latest/Preview
        /// resolved). Recorded so the decl and impl phases agree and so cohost identity holds across
        /// the C# 12/13/14 boundaries.
        /// </summary>
        public LanguageVersion NormalizedLanguageVersion { get; }

        /// <summary>Which property-split strategy applies at this language version.</summary>
        public PropertySplitPath PropertyPath { get; }

        /// <summary>The routed class-body members in original order; each drives what its half emits.</summary>
        public ImmutableArray<RoutedMember> Members { get; }
    }
}

/// <summary>
/// A user-authored class-body member after routing. Carries the member's <see cref="MemberSplitKind"/>
/// (which selects the target half, or the property split), the ordered original IR pieces that make up
/// its content -- C# chunks sliced at member boundaries and markup nodes, all shared by reference so
/// they keep their source mappings -- and, for a <see cref="MemberSplitKind.MarkupProperty"/>, the
/// parsed signature used to derive the Path A defining/implementing partial declarations.
/// </summary>
/// <remarks>
/// Emission is deferred to the decl/impl build steps, which switch on <see cref="Kind"/>:
/// <see cref="MemberSplitKind.NoMarkup"/> -> all pieces to decl; <see cref="MemberSplitKind.MarkupMethod"/>
/// -> all pieces to impl; <see cref="MemberSplitKind.MarkupProperty"/> -> defining declaration (from
/// <see cref="Signature"/>) to decl and the implementing declaration (the pieces) to impl.
/// </remarks>
internal readonly struct RoutedMember
{
    public RoutedMember(MemberSplitKind kind, ImmutableArray<IntermediateNode> pieces, MemberDeclarationSyntax? signature)
    {
        Kind = kind;
        Pieces = pieces.NullToEmpty();
        Signature = signature;
    }

    public MemberSplitKind Kind { get; }

    /// <summary>The member's content in original order (sliced C# chunks and markup nodes, by reference).</summary>
    public ImmutableArray<IntermediateNode> Pieces { get; }

    /// <summary>
    /// The parsed member signature, present only for <see cref="MemberSplitKind.MarkupProperty"/> so the
    /// build step can derive the defining and implementing partial declarations. Null otherwise.
    /// </summary>
    public MemberDeclarationSyntax? Signature { get; }
}

/// <summary>
/// The strategy used to split a markup-bearing property so its signature stays in decl while its
/// markup bodies move to impl. Currently one active strategy; a markup property on a language version
/// that doesn't support it falls back (<see cref="FallbackReason.MarkupPropertyBelowCSharp13"/>)
/// instead of being split.
/// </summary>
internal enum PropertySplitPath
{
    /// <summary>
    /// C# 13+: emit the property's defining partial declaration into decl and a transformed
    /// implementing partial declaration into impl. No synthesized helpers and no accepted breaks.
    /// </summary>
    PartialProperty,
}

/// <summary>
/// Why a markup-bearing class body is left unsplit (rendered whole, discovered the existing way)
/// instead of being split into decl/impl halves.
/// </summary>
internal enum FallbackReason
{
    /// <summary>
    /// A property/indexer body carries markup but the effective language version is below C# 13, so the
    /// partial-property split isn't available. (Splitting it would require synthesized accessor lifts,
    /// which are retired; see the archived Path B design.)
    /// </summary>
    MarkupPropertyBelowCSharp13,

    /// <summary>
    /// The analysis parse is unrecoverable (brace mismatch, or a markup marker isn't contained by any
    /// member), so member boundaries can't be trusted. Not triggered by ordinary transient typos, which
    /// still recover member boundaries.
    /// </summary>
    UnrecoverableParse,
}
