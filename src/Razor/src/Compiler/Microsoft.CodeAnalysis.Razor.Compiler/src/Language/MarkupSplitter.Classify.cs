// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.AspNetCore.Razor.Language;

internal static partial class MarkupSplitter
{
    /// <summary>
    /// Parses the analysis document and classifies each member of the throwaway class: its span in
    /// analysis-document coordinates, whether it carries a markup transition, and its kind (which drives
    /// routing). Returns <see langword="null"/> when the class body is unrecoverable (mismatched braces
    /// or the marker class can't be found), which is the only case that triggers the catastrophic
    /// safety net -- ordinary transient syntax errors still recover member boundaries.
    /// </summary>
    internal static ImmutableArray<ClassifiedMember>? ClassifyMembers(
        AnalysisDocument analysis,
        CSharpParseOptions parseOptions,
        CancellationToken cancellationToken = default)
    {
        var tree = CSharpSyntaxTree.ParseText(analysis.Text, parseOptions, cancellationToken: cancellationToken);
        var root = tree.GetCompilationUnitRoot(cancellationToken);

        var markerClass = root.Members.OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (markerClass is null || markerClass.OpenBraceToken.IsMissing || markerClass.CloseBraceToken.IsMissing)
        {
            return null;
        }

        var builder = ImmutableArray.CreateBuilder<ClassifiedMember>(markerClass.Members.Count);

        foreach (var member in markerClass.Members)
        {
            var span = member.FullSpan;
            var hasMarkup = MemberCoversMarkup(analysis.Segments, span);
            builder.Add(new ClassifiedMember(member, span, hasMarkup, ClassifyKind(member, hasMarkup)));
        }

        var members = builder.ToImmutable();

        // Every markup marker must land inside some member of the marker class. If one doesn't, brace
        // imbalance let it leak out of a member (or out of the class), which is unrecoverable: routing it
        // would either drop it or leak markup into decl. This -- not an ordinary transient syntax error,
        // which still recovers member boundaries -- is what the catastrophic safety net exists for.
        if (!AllMarkupCovered(analysis.Segments, members))
        {
            return null;
        }

        // Likewise, every piece of non-whitespace C# must land inside some member. A gap (skipped tokens
        // from a malformed body, or content the parser attributed outside any member) would be silently
        // dropped by routing, changing the generated code or masking a diagnostic. Fall back instead.
        if (!AllCSharpContentCovered(analysis, members))
        {
            return null;
        }

        return members;
    }

    // True when every non-whitespace character of every C# segment falls within some member's span.
    // Members partition the class body contiguously in the common case, so this only fails on real gaps
    // (leading/trailing skipped tokens or brace imbalance), which routing must not silently drop.
    private static bool AllCSharpContentCovered(
        AnalysisDocument analysis,
        ImmutableArray<ClassifiedMember> members)
    {
        var text = analysis.Text;

        foreach (var segment in analysis.Segments)
        {
            if (segment.Kind != SegmentKind.CSharp)
            {
                continue;
            }

            for (var index = segment.Start; index < segment.End; index++)
            {
                if (!char.IsWhiteSpace(text[index]) && !IsCoveredByMember(members, index))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsCoveredByMember(ImmutableArray<ClassifiedMember> members, int index)
    {
        foreach (var member in members)
        {
            if (member.Span.Contains(index))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AllMarkupCovered(
        ImmutableArray<AnalysisSegment> segments,
        ImmutableArray<ClassifiedMember> members)
    {
        foreach (var segment in segments)
        {
            if (segment.Kind != SegmentKind.Markup)
            {
                continue;
            }

            var covered = false;
            foreach (var member in members)
            {
                if (member.Span.Contains(segment.Start))
                {
                    covered = true;
                    break;
                }
            }

            if (!covered)
            {
                return false;
            }
        }

        return true;
    }

    // A member carries markup when a markup segment's marker starts within the member's span. Detection
    // is by the segment table, never by matching the marker identifier name -- user code may itself call
    // a method of that name.
    private static bool MemberCoversMarkup(ImmutableArray<AnalysisSegment> segments, TextSpan memberSpan)
    {
        foreach (var segment in segments)
        {
            if (segment.Kind == SegmentKind.Markup && memberSpan.Contains(segment.Start))
            {
                return true;
            }
        }

        return false;
    }

    private static MemberSplitKind ClassifyKind(MemberDeclarationSyntax member, bool hasMarkup)
    {
        if (!hasMarkup)
        {
            return MemberSplitKind.NoMarkup;
        }

        return member switch
        {
            // Only a plain method can be lifted wholesale to impl: it has no field-initializer ordering to
            // preserve and isn't descriptor surface, so its absence from decl is invisible.
            MethodDeclarationSyntax => MemberSplitKind.MarkupMethod,

            // A property/indexer is descriptor surface -- it must stay in decl, where markup can't live --
            // so markup in one takes the whole file to fallback.
            PropertyDeclarationSyntax or IndexerDeclarationSyntax => MemberSplitKind.MarkupProperty,

            // Anything else with markup -- a field or event (whose initializer runs in declaration order,
            // which splitting across partials would perturb), a nested type (which may be referenced from
            // decl, or itself contain markup members), a constructor/operator, or an incomplete member --
            // isn't safe to lift, so the whole file falls back.
            _ => MemberSplitKind.MarkupUnsupported,
        };
    }
}

/// <summary>How a classified member routes between the halves.</summary>
internal enum MemberSplitKind
{
    /// <summary>No markup: the whole member stays in decl.</summary>
    NoMarkup,

    /// <summary>
    /// Markup in a plain method: lifted wholesale to impl. Not descriptor surface and no initializer
    /// ordering to preserve, so its absence from decl is fine.
    /// </summary>
    MarkupMethod,

    /// <summary>
    /// Markup in a property/indexer. A property is descriptor surface that must stay in decl, but markup
    /// can't, so this takes the whole file to fallback rather than routing to a half.
    /// </summary>
    MarkupProperty,

    /// <summary>
    /// Markup in a member that can't be safely lifted or kept -- a field/event (initializer ordering), a
    /// nested type, a constructor/operator, or an incomplete member. Takes the whole file to fallback.
    /// </summary>
    MarkupUnsupported,
}

/// <summary>
/// A parsed member of the throwaway analysis class, tagged with its analysis-document span, whether it
/// carries markup, and how it routes.
/// </summary>
internal readonly struct ClassifiedMember
{
    public ClassifiedMember(MemberDeclarationSyntax syntax, TextSpan span, bool hasMarkup, MemberSplitKind kind)
    {
        Syntax = syntax;
        Span = span;
        HasMarkup = hasMarkup;
        Kind = kind;
    }

    public MemberDeclarationSyntax Syntax { get; }

    public TextSpan Span { get; }

    public bool HasMarkup { get; }

    public MemberSplitKind Kind { get; }
}
