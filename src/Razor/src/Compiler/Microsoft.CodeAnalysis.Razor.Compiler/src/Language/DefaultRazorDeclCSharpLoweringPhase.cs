// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading;
using Microsoft.AspNetCore.Razor.Language.CodeGeneration;
using Microsoft.AspNetCore.Razor.Language.Intermediate;

namespace Microsoft.AspNetCore.Razor.Language;

/// <summary>
/// For documents whose generated C# is split into a separate "decl" and "impl" file (today: Razor
/// components whose primary method body is not being suppressed), this phase writes the decl
/// document and then mutates the in-flight tree into its impl shape so the subsequent
/// <see cref="DefaultRazorCSharpLoweringPhase"/> can lower it as if it were the only output.
/// Both phases delegate the actual write to <see cref="RazorCSharpDocumentWriter.Write"/>.
/// </summary>
/// <remarks>
/// <para>
/// The decl document is "everything except the primary render method" -- it carries the partial
/// class declaration, properties, fields, parameters, and any sibling methods, plus all the
/// document-level metadata (route/layout/render-mode attributes, generic type inference helpers,
/// source-checksum attributes, etc.).
/// </para>
/// <para>
/// The impl document (produced downstream by <see cref="DefaultRazorCSharpLoweringPhase"/>) is the
/// minimal partial class containing only the render method plus the namespace and using directives
/// it needs to compile. The two halves rejoin at compile time because the generated class is
/// emitted as <c>partial</c>.
/// </para>
/// <para>
/// For documents that are not splittable (non-components, suppressed primary method body, or any
/// document whose primary structure is missing) this phase is a no-op and the impl phase falls
/// through to the prior single-file behavior.
/// </para>
/// </remarks>
internal sealed class DefaultRazorDeclCSharpLoweringPhase : RazorEnginePhaseBase, IRazorCSharpLoweringPhase
{
    protected override RazorCodeDocument ExecuteCore(RazorCodeDocument codeDocument, CancellationToken cancellationToken)
    {
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

        // The decl/impl split today only applies to component documents whose primary method
        // body isn't suppressed (i.e. the regular runtime codegen path).
        if (codeDocument.FileKind != RazorFileKind.Component ||
            codeDocument.CodeGenerationOptions.SuppressPrimaryMethodBody)
        {
            return codeDocument;
        }

        // Bail out if the document is missing the primary structure we'd need to mutate. This
        // shouldn't normally happen but the find helpers can return null and we'd rather
        // fall back to the single-file path than crash.
        var primaryClass = documentNode.FindPrimaryClass();
        var renderMethod = documentNode.FindPrimaryMethod();
        var primaryNamespace = documentNode.FindPrimaryNamespace();
        if (primaryClass is null || renderMethod is null || primaryNamespace is null)
        {
            return codeDocument;
        }

        // Capture usings from the namespace before we mutate it. Anything reachable from
        // the namespace is fair game; the impl half only needs the using directives.
        var usings = primaryNamespace.FindDescendantNodes<UsingDirectiveIntermediateNode>();

        // Phase 1: lower the decl half. Removing the render method is enough -- everything
        // else in the document (siblings of primaryClass, attributes inserted at the
        // namespace level, secondary namespaces such as the generic type inference helpers,
        // checksum attributes, etc.) belongs in the decl output.
        primaryClass.Children.Remove(renderMethod);
        var declDocument = RazorCSharpDocumentWriter.Write(documentNode, codeDocument, cancellationToken);

        // Phase 2: rewrite the in-flight tree to the impl shape so the next phase
        // (DefaultRazorCSharpLoweringPhase) can write the impl half without needing any
        // knowledge of the split. The impl half is namespace + usings + partial class
        // containing only the render method.
        primaryClass.Children.Clear();
        primaryClass.Children.Add(renderMethod);

        primaryNamespace.Children.Clear();
        primaryNamespace.Children.AddRange(usings);
        primaryNamespace.Children.Add(primaryClass);

        documentNode.Children.Clear();
        documentNode.Children.Add(primaryNamespace);

        return codeDocument.WithDeclCSharpDocument(declDocument);
    }
}
