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
/// base type / interfaces / type parameters / user-authored class-level attributes (route,
/// layout), all properties / fields / parameters / inject members / sibling methods, and any
/// document-level metadata (source-checksum attributes, etc.).
/// </para>
/// <para>
/// The impl document carries the render method body plus any compiler-synthesized plumbing
/// marked with <see cref="IntermediateNode.IsSynthesizedHelper"/>, wrapped in a minimal partial
/// class that shares the user's name and type parameter list. Routing all synthesized nodes
/// to this half keeps them off the user's API surface and lets file-scoped helpers stay
/// colocated with their decoration in a single file.
/// </para>
/// <para>
/// The split affects only the generated C# (<see cref="RazorCodeDocument.GetCSharpDocument"/>
/// gives the impl half, <c>GetDeclCSharpDocument()</c> gives the decl half). The original
/// <see cref="DocumentIntermediateNode"/> on the <see cref="RazorCodeDocument"/> is left
/// untouched -- both synthetic spines share children with the original by reference rather
/// than mutating it -- so callers that walk the IR tree (e.g.
/// <c>RazorCodeDocumentExtensions.ComponentNamespaceMatches</c>,
/// <c>ExtractToCodeBehindCodeActionResolver</c>) continue to see its pre-split shape.
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
        // → primaryClass spine, share every other child (renderMethod's siblings such as
        // @code blocks, secondary classes, document-level attribute nodes) by reference,
        // and skip nodes that belong in the impl half:
        //   - renderMethod
        //   - any IsSynthesizedHelper node (compiler plumbing)
        //   - IsGenericTyped namespaces (type-inference helpers)
        var declDocNode = CloneContainer(documentNode);
        var declNamespace = CloneContainer(primaryNamespace);
        var declClass = CloneContainer(primaryClass);

        foreach (var classChild in primaryClass.Children)
        {
            if (classChild == renderMethod || classChild.IsSynthesizedHelper)
            {
                continue;
            }

            declClass.Children.Add(classChild);
        }

        foreach (var nsChild in primaryNamespace.Children)
        {
            if (nsChild.IsSynthesizedHelper)
            {
                continue;
            }

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
        // IsSynthesizedHelper nodes lifted from primaryClass / primaryNamespace. The
        // namespace-level walk preserves original order relative to primaryClass so an
        // attribute decoration that preceded the class in the original tree continues
        // to do so. IsGenericTyped helper namespaces are also lifted as siblings of the
        // primary namespace.
        var usings = primaryNamespace.FindDescendantNodes<UsingDirectiveIntermediateNode>();
        var implDocNode = CloneContainer(documentNode);
        var implNamespace = CloneContainer(primaryNamespace);
        var implClass = CloneContainer(primaryClass);

        implClass.Children.Add(renderMethod);
        foreach (var classChild in primaryClass.Children)
        {
            if (classChild.IsSynthesizedHelper)
            {
                implClass.Children.Add(classChild);
            }
        }

        foreach (var usingDirective in usings)
        {
            implNamespace.Children.Add(usingDirective);
        }

        foreach (var nsChild in primaryNamespace.Children)
        {
            if (nsChild == primaryClass)
            {
                implNamespace.Children.Add(implClass);
            }
            else if (nsChild.IsSynthesizedHelper)
            {
                implNamespace.Children.Add(nsChild);
            }
        }

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

        // Stash the impl half on RazorCodeDocument.CSharpDocument and the decl half on
        // DeclCSharpDocument. Note: today the impl assignment is effectively a no-op --
        // DefaultRazorCSharpLoweringPhase runs immediately after this phase and
        // unconditionally overwrites CSharpDocument by re-lowering the original IR.
        // A follow-up commit gates that phase on GetDeclCSharpDocument() being null so
        // this impl half survives, and updates the source generator to emit both.
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
