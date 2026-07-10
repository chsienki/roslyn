// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text;
using Microsoft.AspNetCore.Razor.Language.Extensions;
using Microsoft.AspNetCore.Razor.Language.Intermediate;

namespace Microsoft.AspNetCore.Razor.Language;

internal static partial class MarkupSplitter
{
    // A markup transition in expression position (an `@<...>` / `@:` template, which lowers to a
    // RenderFragment value) is replaced by an expression; one in statement position (bare markup
    // enabled by AllowRazorInAllCodeBlocks) is replaced by a statement. Substituting the right form
    // for the markup's span yields text that parses iff the user's original C# was valid.
    private const string ExpressionMarker = MarkerMethodName + "()";
    private const string StatementMarker = MarkerMethodName + "();";

    // The class-body children are wrapped in a throwaway class so they parse as member declarations.
    // Offsets recorded in the segment table are relative to the full wrapped text so they line up with
    // the parse tree.
    private const string AnalysisClassHeader = "class __C {\n";
    private const string AnalysisClassFooter = "\n}\n";

    /// <summary>
    /// Renders the collected class-body children into a parse-only C# document, replacing each markup
    /// node with a position-aware marker, and records a <see cref="AnalysisSegment"/> mapping each
    /// emitted span back to its originating IR node. The document is never emitted; it exists only to
    /// recover member boundaries and to detect which members carry markup.
    /// </summary>
    internal static AnalysisDocument BuildAnalysisDocument(ImmutableArray<IntermediateNode> children)
    {
        var builder = new StringBuilder();
        builder.Append(AnalysisClassHeader);

        var segments = ImmutableArray.CreateBuilder<AnalysisSegment>(children.Length);

        foreach (var child in children)
        {
            var start = builder.Length;

            switch (child)
            {
                case CSharpCodeIntermediateNode csharp:
                    AppendCSharpText(builder, csharp);
                    segments.Add(new AnalysisSegment(start, builder.Length - start, child, SegmentKind.CSharp));
                    break;

                case var markup when IsClassBodyMarkup(markup):
                    builder.Append(IsExpressionPositionMarkup(markup) ? ExpressionMarker : StatementMarker);
                    segments.Add(new AnalysisSegment(start, builder.Length - start, child, SegmentKind.Markup));
                    break;

                default:
                    // A synthesized structured declaration (e.g. an injected property). It carries no
                    // markup and is surface, so it contributes no analysis text but is still recorded so
                    // routing can place it.
                    segments.Add(new AnalysisSegment(start, 0, child, SegmentKind.Other));
                    break;
            }
        }

        builder.Append(AnalysisClassFooter);

        return new AnalysisDocument(builder.ToString(), segments.ToImmutable());
    }

    /// <summary>
    /// Expression-position markup is exactly a <see cref="TemplateIntermediateNode"/> (from <c>@&lt;...&gt;</c>
    /// / <c>@:</c>); every other class-body markup node sits in statement position. Keying on this single
    /// node kind -- rather than an enumerated list of the statement-position kinds -- keeps the rule
    /// correct as new markup node kinds are introduced.
    /// </summary>
    internal static bool IsExpressionPositionMarkup(IntermediateNode node)
        => node is TemplateIntermediateNode;

    private static void AppendCSharpText(StringBuilder builder, CSharpCodeIntermediateNode node)
    {
        foreach (var child in node.Children)
        {
            if (child is IntermediateToken token)
            {
                builder.Append(token.Content);
            }
        }
    }
}

/// <summary>Whether an analysis segment originated from C# text, a markup transition, or another node.</summary>
internal enum SegmentKind
{
    CSharp,
    Markup,
    Other,
}

/// <summary>
/// Maps a span of the throwaway analysis document back to the IR node it was emitted from, so member
/// boundaries discovered by the parser (in analysis-document coordinates) can be intersected with the
/// original IR nodes regardless of the length difference between a markup node and its marker.
/// </summary>
internal readonly struct AnalysisSegment
{
    public AnalysisSegment(int start, int length, IntermediateNode node, SegmentKind kind)
    {
        Start = start;
        Length = length;
        Node = node;
        Kind = kind;
    }

    public int Start { get; }

    public int Length { get; }

    public int End => Start + Length;

    public IntermediateNode Node { get; }

    public SegmentKind Kind { get; }
}

/// <summary>The throwaway analysis text plus the segment table mapping its spans back to IR nodes.</summary>
internal sealed class AnalysisDocument
{
    public AnalysisDocument(string text, ImmutableArray<AnalysisSegment> segments)
    {
        Text = text;
        Segments = segments;
    }

    public string Text { get; }

    public ImmutableArray<AnalysisSegment> Segments { get; }
}
