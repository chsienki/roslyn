// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Razor.Language.Intermediate;

namespace Microsoft.AspNetCore.Razor.Language;

internal static partial class MarkupSplitter
{
    /// <summary>
    /// Groups the analysis segments under their owning parsed members, slicing straddling C# chunks at
    /// member boundaries, to produce one <see cref="RoutedMember"/> per member in original order --
    /// already resolved into the pieces each half emits. This is the mapping that reconciles the two
    /// coordinate systems: parser member spans and IR nodes both live in analysis-document offsets
    /// (markup nodes contribute their marker span), so intersecting them here means the markup/marker
    /// length difference never matters. Returns <see langword="null"/> when a markup property can't be
    /// split (see <see cref="BuildPropertyDeclarations"/>), so the caller falls back for the whole file.
    /// </summary>
    internal static ImmutableArray<RoutedMember>? BuildRoutedMembers(
        AnalysisDocument analysis,
        ImmutableArray<ClassifiedMember> members)
    {
        // Accumulate each member's pieces (sliced C# chunks and markup nodes) in source order.
        var pieceBuilders = new List<IntermediateNode>[members.Length];
        for (var i = 0; i < members.Length; i++)
        {
            pieceBuilders[i] = [];
        }

        foreach (var segment in analysis.Segments)
        {
            switch (segment.Kind)
            {
                case SegmentKind.CSharp:
                    RouteCSharpSegment((CSharpCodeIntermediateNode)segment.Node, segment, members, pieceBuilders);
                    break;

                default:
                    // A markup marker or a zero-length synthesized declaration lives entirely inside one
                    // member; route the original node there by reference (keeping its source mappings).
                    var owner = FindMemberIndex(members, segment.Start);
                    if (owner >= 0)
                    {
                        pieceBuilders[owner].Add(segment.Node);
                    }

                    break;
            }
        }

        var result = ImmutableArray.CreateBuilder<RoutedMember>(members.Length);
        for (var i = 0; i < members.Length; i++)
        {
            var member = members[i];
            var pieces = pieceBuilders[i].ToImmutableArray();

            switch (member.Kind)
            {
                case MemberSplitKind.NoMarkup:
                    // Markup-free surface stays in decl.
                    result.Add(new RoutedMember(member.Kind, declPieces: pieces, implPieces: []));
                    break;

                case MemberSplitKind.MarkupMethod:
                    // Markup-bearing methods (and explicit-interface properties) lift wholesale to impl.
                    result.Add(new RoutedMember(member.Kind, declPieces: [], implPieces: pieces));
                    break;

                case MemberSplitKind.MarkupProperty:
                    // Path A: the signature stays in decl, the markup body moves to impl. If the property
                    // can't be split this way, the whole file falls back.
                    if (BuildPropertyDeclarations(member, pieces) is not { } split)
                    {
                        return null;
                    }

                    result.Add(new RoutedMember(member.Kind, declPieces: split.Decl, implPieces: split.Impl));
                    break;
            }
        }

        return result.ToImmutable();
    }

    // Slices a raw C# chunk at any member boundaries that fall within it and routes each slice to the
    // member that owns its start. A single class-body C# chunk commonly straddles several members (a
    // field immediately followed by a markup-bearing method), so it can't be routed as a unit.
    private static void RouteCSharpSegment(
        CSharpCodeIntermediateNode node,
        AnalysisSegment segment,
        ImmutableArray<ClassifiedMember> members,
        List<IntermediateNode>[] pieceBuilders)
    {
        // Member boundaries strictly inside the segment become node-local cut offsets. Members are in
        // source order with contiguous, increasing spans, so the cuts come out strictly increasing.
        var cuts = ImmutableArray.CreateBuilder<int>();
        foreach (var member in members)
        {
            var boundary = member.Span.End;
            if (boundary > segment.Start && boundary < segment.End)
            {
                cuts.Add(boundary - segment.Start);
            }
        }

        var cutOffsets = cuts.ToImmutable();
        var slices = SplitCSharpNode(node, cutOffsets);

        for (var i = 0; i < slices.Length; i++)
        {
            var localStart = i == 0 ? 0 : cutOffsets[i - 1];
            var owner = FindMemberIndex(members, segment.Start + localStart);
            if (owner >= 0)
            {
                pieceBuilders[owner].Add(slices[i]);
            }
        }
    }

    // The index of the member whose analysis-document span contains the offset. Members partition the
    // class body contiguously, so any interior offset has exactly one owner; a boundary offset belongs
    // to the following member (spans are half-open), which is the member that content begins.
    private static int FindMemberIndex(ImmutableArray<ClassifiedMember> members, int offset)
    {
        for (var i = 0; i < members.Length; i++)
        {
            if (members[i].Span.Contains(offset))
            {
                return i;
            }
        }

        return -1;
    }
}
