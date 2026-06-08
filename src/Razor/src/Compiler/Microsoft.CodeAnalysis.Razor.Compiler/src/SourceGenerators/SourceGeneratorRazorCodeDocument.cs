// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using Microsoft.AspNetCore.Razor.Language;

namespace Microsoft.NET.Sdk.Razor.SourceGenerators;

/// <summary>
/// A wrapper for <see cref="RazorCodeDocument"/>
/// </summary>
/// <remarks>
/// The razor compiler modifies the <see cref="RazorCodeDocument"/> in place during the various phases,
/// meaning object identity is maintained even when the contents have changed.
/// 
/// We need to be able to identify from the source generator if a given code document was modified or 
/// returned unchanged. Rather than implementing deep equality on the <see cref="RazorCodeDocument"/> 
/// which can get expensive, we instead use a wrapper class. If the underlying document is unchanged we
/// return the original wrapper class. If the underlying  document is changed, we return a new instance
/// of the wrapper.
/// </remarks>
internal sealed class SourceGeneratorRazorCodeDocument(RazorCodeDocument razorCodeDocument, RazorProjectItem? sourceItem = null)
{
    public RazorCodeDocument CodeDocument { get; } = razorCodeDocument;

    /// <summary>
    /// The <see cref="RazorProjectItem"/> that produced <see cref="CodeDocument"/>, retained so
    /// that <see cref="SourceGeneratorProjectEngine.ProcessTagHelpers"/> can re-create the
    /// document from scratch when a tag-helper change forces a rewrite. We can't snapshot the
    /// unresolved IR directly because <see cref="RazorCodeDocument"/> is only shallowly immutable
    /// -- its <c>With*</c> methods share the underlying mutable IR, which the resolution phase
    /// mutates in place.
    /// </summary>
    /// <remarks>
    /// Null when the wrapper wasn't produced by <see cref="SourceGeneratorProjectEngine.ProcessForDecl"/>
    /// (e.g. constructed directly by tests).
    /// </remarks>
    public RazorProjectItem? SourceItem { get; } = sourceItem;
}
