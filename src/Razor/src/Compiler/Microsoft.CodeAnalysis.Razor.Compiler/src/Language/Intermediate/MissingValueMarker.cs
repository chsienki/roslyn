// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Collections.Immutable;

namespace Microsoft.AspNetCore.Razor.Language.Intermediate;

/// <summary>
/// Helpers for marking and detecting intermediate tokens that represent a missing
/// user-supplied value. The motivating bug is <c>@onclick=""</c>: the parser
/// surfaces a single zero-width missing token, which flows through IR lowering
/// as an effectively-empty token stream and -- without intervention -- causes
/// codegen to emit malformed C# (e.g. <c>Create&lt;T&gt;(this, )</c>) and a
/// downstream CS1525. Codegen detects these markers via
/// <see cref="IsMissingValueMarker(IReadOnlyList{IntermediateToken})"/> and
/// emits a safe placeholder (e.g. <c>default!</c>) instead.
/// </summary>
internal static class MissingValueMarker
{
    /// <summary>
    /// Returns <see langword="true"/> when the supplied token stream is "effectively
    /// empty": either the list is empty, or every token is tagged via
    /// <see cref="IntermediateToken.IsMissingValue"/>, or every non-lazy token's
    /// content is <see cref="string.IsNullOrEmpty(string)"/>.
    /// </summary>
    public static bool IsMissingValueMarker(IReadOnlyList<IntermediateToken> tokens)
    {
        if (tokens == null || tokens.Count == 0)
        {
            return true;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (token.IsMissingValue)
            {
                continue;
            }

            // Lazy tokens compute their content from a syntax node; calling
            // Content forces materialization. The empty-attribute-value
            // missing-token shape produces an empty string through GetContent(),
            // so checking IsNullOrEmpty here is both correct and cheap.
            if (string.IsNullOrEmpty(token.Content))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Convenience overload for the <see cref="ImmutableArray{T}"/> stream commonly
    /// produced by lowering passes.
    /// </summary>
    public static bool IsMissingValueMarker(ImmutableArray<IntermediateToken> tokens)
        => IsMissingValueMarker<IntermediateToken>(tokens);

    /// <summary>
    /// Generic overload that accepts an <see cref="ImmutableArray{T}"/> of a token
    /// subtype (e.g. <see cref="CSharpIntermediateToken"/>) without requiring a
    /// covariance-friendly cast at every codegen call site.
    /// </summary>
    public static bool IsMissingValueMarker<T>(ImmutableArray<T> tokens) where T : IntermediateToken
    {
        if (tokens.IsDefaultOrEmpty)
        {
            return true;
        }

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];

            if (token.IsMissingValue)
            {
                continue;
            }

            if (string.IsNullOrEmpty(token.Content))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Creates a <see cref="CSharpIntermediateToken"/> tagged as a missing-value
    /// marker. The content is empty; the codegen pass substitutes the
    /// appropriate placeholder (e.g. <c>default!</c>) based on the surrounding
    /// emission context.
    /// </summary>
    public static CSharpIntermediateToken CreateMissingCSharpToken(SourceSpan? source = null)
        => new(string.Empty, source) { IsMissingValue = true };

    /// <summary>
    /// Creates a <see cref="CSharpIntermediateToken"/> tagged as a missing-value
    /// marker, pre-populated with the placeholder text codegen should emit
    /// (e.g. <c>default(global::System.Action&lt;MouseEventArgs&gt;)</c>).
    /// </summary>
    public static CSharpIntermediateToken CreateMissingCSharpToken(string placeholder, SourceSpan? source = null)
        => new(placeholder ?? string.Empty, source) { IsMissingValue = true };

    /// <summary>
    /// Selects the safe placeholder text to substitute at codegen for a missing
    /// value, given the surrounding generated C# context.
    /// </summary>
    /// <param name="kind">The surrounding emission context.</param>
    /// <param name="typeArgument">For <see cref="MissingValuePlaceholderKind.EventCallbackTyped"/> or
    /// <see cref="MissingValuePlaceholderKind.BoundAttributeTyped"/>, the
    /// globally-qualified type to substitute into <c>default(...)</c>. Ignored for
    /// other kinds. May be <see langword="null"/> for untyped contexts.</param>
    public static string GetPlaceholderText(MissingValuePlaceholderKind kind, string? typeArgument = null)
    {
        switch (kind)
        {
            case MissingValuePlaceholderKind.EventCallbackTyped:
                // Binds to EventCallback.Factory.Create<TValue>(object, Action<TValue>).
                return string.IsNullOrEmpty(typeArgument)
                    ? "default(global::System.Action)"
                    : $"default(global::System.Action<{typeArgument}>)";

            case MissingValuePlaceholderKind.EventCallbackUntyped:
                return "default(global::System.Action)";

            case MissingValuePlaceholderKind.BoundAttributeTyped:
                return string.IsNullOrEmpty(typeArgument)
                    ? "default!"
                    : $"default({typeArgument})";

            case MissingValuePlaceholderKind.BoundAttributeUnknown:
                return "default!";

            case MissingValuePlaceholderKind.MarkupExpression:
                // Output context: empty string keeps the resulting render output empty
                // and parses cleanly as an argument to Write(...) / AddContent(...).
                return "\"\"";

            case MissingValuePlaceholderKind.StatementContext:
                // Statement context already provides the trailing ';'.
                return "_ = (object?)null";

            default:
                return "default!";
        }
    }
}
