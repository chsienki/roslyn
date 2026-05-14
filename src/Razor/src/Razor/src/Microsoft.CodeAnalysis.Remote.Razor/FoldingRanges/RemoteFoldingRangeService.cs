// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.PooledObjects;
using Microsoft.CodeAnalysis.ExternalAccess.Razor;
using Microsoft.CodeAnalysis.Razor.FoldingRanges;
using Microsoft.CodeAnalysis.Razor.Protocol;
using Microsoft.CodeAnalysis.Razor.Protocol.Folding;
using Microsoft.CodeAnalysis.Razor.Remote;
using Microsoft.CodeAnalysis.Remote.Razor.ProjectSystem;
using ExternalHandlers = Microsoft.CodeAnalysis.ExternalAccess.Razor.Cohost.Handlers;

namespace Microsoft.CodeAnalysis.Remote.Razor;

internal sealed class RemoteFoldingRangeService(in ServiceArgs args) : RazorDocumentServiceBase(in args), IRemoteFoldingRangeService
{
    internal sealed class Factory : FactoryBase<IRemoteFoldingRangeService>
    {
        protected override IRemoteFoldingRangeService CreateService(in ServiceArgs args)
            => new RemoteFoldingRangeService(in args);
    }

    private readonly IFoldingRangeService _foldingRangeService = args.ExportProvider.GetExportedValue<IFoldingRangeService>();
    private readonly IClientCapabilitiesService _clientCapabilitiesService = args.ExportProvider.GetExportedValue<IClientCapabilitiesService>();

    public ValueTask<ImmutableArray<RemoteFoldingRange>> GetFoldingRangesAsync(
        RazorPinnedSolutionInfoWrapper solutionInfo,
        DocumentId documentId,
        ImmutableArray<RemoteFoldingRange> htmlRanges,
        CancellationToken cancellationToken)
        => RunServiceAsync(
            solutionInfo,
            documentId,
            context => GetFoldingRangesAsync(context, htmlRanges, cancellationToken),
            cancellationToken);

    private async ValueTask<ImmutableArray<RemoteFoldingRange>> GetFoldingRangesAsync(
        RemoteDocumentContext context,
        ImmutableArray<RemoteFoldingRange> htmlRanges,
        CancellationToken cancellationToken)
    {
        // For splittable Razor components the source generator emits two C# documents -- the
        // impl half (render method body) and the decl half (@code, properties, fields, etc.).
        // Folding ranges are syntactic and only consider the document's own syntax tree, so we
        // must enumerate both halves and merge the results to surface ranges for user code in
        // @code blocks.
        var generatedDocuments = await context.Snapshot
            .GetAllGeneratedDocumentsAsync(cancellationToken)
            .ConfigureAwait(false);

        var lineFoldingOnly = _clientCapabilitiesService.ClientCapabilities.TextDocument?.FoldingRange?.LineFoldingOnly ?? false;

        using var csharpRangesBuilder = new PooledArrayBuilder<FoldingRange>();
        foreach (var generatedDocument in generatedDocuments)
        {
            var ranges = await ExternalHandlers.FoldingRanges.GetFoldingRangesAsync(generatedDocument, lineFoldingOnly, cancellationToken).ConfigureAwait(false);
            csharpRangesBuilder.AddRange(ranges);
        }

        var convertedHtml = htmlRanges.SelectAsArray(RemoteFoldingRange.ToLspFoldingRange);

        var codeDocument = await context.GetCodeDocumentAsync(cancellationToken).ConfigureAwait(false);
        return _foldingRangeService.GetFoldingRanges(codeDocument, csharpRangesBuilder.ToArray(), convertedHtml, cancellationToken)
            .SelectAsArray(RemoteFoldingRange.FromLspFoldingRange);
    }
}
