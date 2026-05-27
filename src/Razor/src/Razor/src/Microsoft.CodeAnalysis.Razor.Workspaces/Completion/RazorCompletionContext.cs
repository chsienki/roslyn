// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis.Razor.Protocol;
using RazorSyntaxNode = Microsoft.AspNetCore.Razor.Language.Syntax.SyntaxNode;

namespace Microsoft.CodeAnalysis.Razor.Completion;

internal record RazorCompletionContext(
    RazorCodeDocument CodeDocument,
    int AbsoluteIndex,
    RazorSyntaxNode? Owner,
    RazorSyntaxTree SyntaxTree,
    TagHelperDocumentContext TagHelperDocumentContext,
    CompletionReason Reason = CompletionReason.Invoked,
    RazorCompletionOptions Options = default,
    // When the cursor lands inside a SkippedContentSyntax, this is set to the
    // originating language of the skipped region (CSharp / Html) so the host
    // can dispatch completion to the appropriate delegated language provider
    // instead of falling back to plain Razor-only completions. Defaults to
    // Razor for the normal (non-recovery) cursor position case.
    RazorLanguageKind LanguageKind = RazorLanguageKind.Razor)
{
    /// <summary>
    /// When non-null, contains the set of HTML element tag names that the local HTML completion
    /// provider determined are valid in this context. Providers that need to correlate with HTML
    /// completions (e.g., tag helper element completions) use this for filtering/deduplication.
    /// Null when local HTML completions were not produced for this context (e.g., non-HTML position or no element completions).
    /// </summary>
    public HashSet<string>? HtmlLabels { get; init; }
}
