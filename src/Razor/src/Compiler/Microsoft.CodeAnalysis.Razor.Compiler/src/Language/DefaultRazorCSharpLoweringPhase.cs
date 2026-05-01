// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading;
using Microsoft.AspNetCore.Razor.Language.CodeGeneration;
using Microsoft.AspNetCore.Razor.Language.Intermediate;

namespace Microsoft.AspNetCore.Razor.Language;

internal class DefaultRazorCSharpLoweringPhase : RazorEnginePhaseBase, IRazorCSharpLoweringPhase
{
    protected override RazorCodeDocument ExecuteCore(RazorCodeDocument codeDocument, CancellationToken cancellationToken)
    {
        // The decl phase (DefaultRazorDeclCSharpLoweringPhase) produces both halves directly
        // when the document is splittable, stashing them via WithCSharpDocument +
        // WithDeclCSharpDocument. In that case there's nothing more to lower. We gate on
        // the decl document specifically (rather than any pre-existing csharpDocument) so
        // a future test/caller that pre-populates only the impl half doesn't accidentally
        // suppress this phase.
        if (codeDocument.GetDeclCSharpDocument() is not null)
        {
            return codeDocument;
        }

        var documentNode = codeDocument.GetDocumentNode();
        ThrowForMissingDocumentDependency(documentNode);

        var target = documentNode.Target;
        if (target == null)
        {
            var message = Resources.FormatDocumentMissingTarget(
                documentNode.DocumentKind,
                nameof(CodeTarget),
                nameof(DocumentIntermediateNode.Target));
            throw new InvalidOperationException(message);
        }

        var csharpDocument = RazorCSharpDocumentWriter.Write(documentNode, codeDocument, cancellationToken);
        return codeDocument.WithCSharpDocument(csharpDocument);
    }
}