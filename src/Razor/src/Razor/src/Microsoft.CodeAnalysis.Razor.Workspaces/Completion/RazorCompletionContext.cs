// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    // Stage 5.6 of the parser error-recovery redesign: when the cursor lands
    // inside a SkippedContentSyntax, this is set to the originating language
    // of the skipped region (CSharp / Html) so the host can dispatch
    // completion to the appropriate delegated language provider instead of
    // falling back to plain Razor-only completions. See
    // razor-recovery-redesign-plan.md Big Design Decision #10. Defaults to
    // Razor for the normal (non-recovery) cursor position case.
    RazorLanguageKind LanguageKind = RazorLanguageKind.Razor)
{
}
