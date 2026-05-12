// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading;
using Microsoft.AspNetCore.Razor.Language.CodeGeneration;
using Microsoft.AspNetCore.Razor.Language.Intermediate;

namespace Microsoft.AspNetCore.Razor.Language;

/// <summary>
/// For Razor components whose primary method body is not being suppressed, this phase produces
/// both the "decl" and "impl" C# documents and stashes them on <see cref="RazorCodeDocument"/>.
/// Both halves are emitted as <c>partial</c> so they rejoin at compile time.
/// </summary>
/// <remarks>
/// <para>
/// The decl document carries the user's component API surface: the partial class declaration with
/// base type / interfaces / type parameters / class-level attributes (route, layout, render-mode
/// decoration), all properties / fields / parameters / inject members / sibling methods, and any
/// document-level metadata (source-checksum attributes, etc.).
/// </para>
/// <para>
/// The impl document carries the render method body plus any compiler-synthesized plumbing -- the
/// nested <c>__PrivateComponentRenderModeAttribute</c> helper class (when not file-scoped) and the
/// <c>__Blazor.X.Y.TypeInference</c> helper namespace -- all wrapped in a minimal partial class
/// that shares the user's name and type parameter list. Helpers live in the impl half because
/// they're plumbing that doesn't belong on the user's API surface.
/// </para>
/// <para>
/// Both halves are written from synthetic spine clones, never by mutating the in-flight tree.
/// The original <see cref="DocumentIntermediateNode"/> is observed by IDE/cohosting code paths
/// (e.g. <c>RazorCodeDocumentExtensions.ComponentNamespaceMatches</c>,
/// <c>ExtractToCodeBehindCodeActionResolver</c>) and by integration test baselines, so leaving
/// it identical to its pre-split state is a contract.
/// </para>
/// <para>
/// For documents that aren't splittable (non-components, suppressed primary method body, or any
/// document missing the primary structure) this phase is a no-op and the downstream
/// <see cref="DefaultRazorCSharpLoweringPhase"/> falls through to the prior single-file behavior.
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

        // Skip the split for any document that shouldn't be split:
        // - Non-components: the split is component-only.
        // - SuppressPrimaryMethodBody (e.g. ProcessDeclarationOnly): caller wants the
        //   decl-shaped output as the single C# document.
        if (codeDocument.FileKind != RazorFileKind.Component ||
            codeDocument.CodeGenerationOptions.SuppressPrimaryMethodBody)
        {
            return codeDocument;
        }

        // Bail out if the document is missing the primary structure we'd need to split.
        // The find helpers can return null and we'd rather fall back to the single-file
        // path than crash.
        var primaryClass = documentNode.FindPrimaryClass();
        var renderMethod = documentNode.FindPrimaryMethod();
        var primaryNamespace = documentNode.FindPrimaryNamespace();
        if (primaryClass is null || renderMethod is null || primaryNamespace is null)
        {
            return codeDocument;
        }

        // The full diagnostic set, deduped by checksum. We'll seed both synthetic roots
        // with this so any diagnostics attached to documentNode/primaryNamespace/
        // primaryClass themselves -- which aren't reachable from the synthetic clones --
        // still surface in the resulting RazorCSharpDocuments.
        var allDiagnostics = documentNode.GetAllDiagnostics();

        // Build the decl synthetic tree: shallow-clone the documentNode → primaryNamespace
        // → primaryClass spine, share every other child (including renderMethod's
        // siblings such as @code blocks, secondary classes, document-level attribute
        // nodes) by reference, and skip:
        //   - renderMethod itself (it's the impl-half content)
        //   - synthesized helper classes nested in primaryClass (compiler plumbing
        //     like __PrivateComponentRenderModeAttribute -- impl-half content)
        //   - IsGenericTyped namespaces (the __Blazor.X.Y.TypeInference helper,
        //     emitted by ComponentGenericTypePass -- impl-half content)
        var declDocNode = CloneContainer(documentNode);
        var declNamespace = CloneContainer(primaryNamespace);
        var declClass = CloneContainer(primaryClass);

        foreach (var classChild in primaryClass.Children)
        {
            if (classChild == renderMethod ||
                (classChild is ClassDeclarationIntermediateNode { IsSynthesizedHelper: true }))
            {
                continue;
            }

            declClass.Children.Add(classChild);
        }

        foreach (var nsChild in primaryNamespace.Children)
        {
            declNamespace.Children.Add(nsChild == primaryClass ? declClass : nsChild);
        }

        foreach (var docChild in documentNode.Children)
        {
            if (docChild is NamespaceDeclarationIntermediateNode { IsGenericTyped: true })
            {
                continue;
            }

            declDocNode.Children.Add(docChild == primaryNamespace ? declNamespace : docChild);
        }

        foreach (var diagnostic in allDiagnostics)
        {
            declDocNode.AddDiagnostic(diagnostic);
        }

        var declDocument = RazorCSharpDocumentWriter.Write(declDocNode, codeDocument, cancellationToken, reportDiagnostics: false);

        // Build the impl synthetic tree: brand-new spine containing just the namespace,
        // its using directives, and a partial class wrapping renderMethod plus any
        // synthesized helper classes nested in primaryClass. The IsGenericTyped helper
        // namespaces (e.g. __Blazor.X.Y.TypeInference) are also lifted into the impl
        // document as siblings of the primary namespace.
        var usings = primaryNamespace.FindDescendantNodes<UsingDirectiveIntermediateNode>();
        var implDocNode = CloneContainer(documentNode);
        var implNamespace = CloneContainer(primaryNamespace);
        var implClass = CloneContainer(primaryClass);

        implClass.Children.Add(renderMethod);
        foreach (var classChild in primaryClass.Children)
        {
            if (classChild is ClassDeclarationIntermediateNode { IsSynthesizedHelper: true })
            {
                implClass.Children.Add(classChild);
            }
        }

        foreach (var usingDirective in usings)
        {
            implNamespace.Children.Add(usingDirective);
        }
        implNamespace.Children.Add(implClass);
        implDocNode.Children.Add(implNamespace);

        foreach (var docChild in documentNode.Children)
        {
            if (docChild is NamespaceDeclarationIntermediateNode { IsGenericTyped: true } genericNs)
            {
                implDocNode.Children.Add(genericNs);
            }
        }

        foreach (var diagnostic in allDiagnostics)
        {
            implDocNode.AddDiagnostic(diagnostic);
        }

        var implDocument = RazorCSharpDocumentWriter.Write(implDocNode, codeDocument, cancellationToken);

        return codeDocument
            .WithCSharpDocument(implDocument)
            .WithDeclCSharpDocument(declDocument);
    }

    private static DocumentIntermediateNode CloneContainer(DocumentIntermediateNode node)
        => new()
        {
            DocumentKind = node.DocumentKind,
            Options = node.Options,
            Target = node.Target,
            Source = node.Source,
            IsImported = node.IsImported,
        };

    private static NamespaceDeclarationIntermediateNode CloneContainer(NamespaceDeclarationIntermediateNode node)
        => new()
        {
            Name = node.Name,
            IsPrimaryNamespace = node.IsPrimaryNamespace,
            IsGenericTyped = node.IsGenericTyped,
            Source = node.Source,
            IsImported = node.IsImported,
        };

    private static ClassDeclarationIntermediateNode CloneContainer(ClassDeclarationIntermediateNode node)
        => new()
        {
            Name = node.Name,
            BaseType = node.BaseType,
            Modifiers = node.Modifiers,
            Interfaces = node.Interfaces,
            TypeParameters = node.TypeParameters,
            IsPrimaryClass = node.IsPrimaryClass,
            NullableContext = node.NullableContext,
            Source = node.Source,
            IsImported = node.IsImported,
        };
}
