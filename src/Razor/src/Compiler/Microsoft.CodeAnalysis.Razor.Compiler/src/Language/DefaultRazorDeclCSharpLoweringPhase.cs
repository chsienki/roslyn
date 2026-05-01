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
/// The decl document is "everything except the primary render method" -- it carries the partial
/// class declaration, properties, fields, parameters, and any sibling methods, plus all the
/// document-level metadata (route/layout/render-mode attributes, generic type inference helpers,
/// source-checksum attributes, etc.).
/// </para>
/// <para>
/// The impl document is the minimal partial class containing only the render method plus the
/// namespace and using directives it needs to compile.
/// </para>
/// <para>
/// Both halves are written from synthetic spine clones, never by mutating the in-flight tree.
/// The original <see cref="DocumentIntermediateNode"/> is observed by IDE/cohosting code paths
/// (e.g. <c>RazorCodeDocumentExtensions.ComponentNamespaceMatches</c>,
/// <c>ExtractToCodeBehindCodeActionResolver</c>) and by integration test baselines, so leaving
/// it identical to its pre-split state is a contract.
/// </para>
/// <para>
/// For documents that aren't splittable (non-components, suppressed primary method body, design
/// time, or any document missing the primary structure) this phase is a no-op and the downstream
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
        // - DesignTime: the IDE expects a single coherent design-time C# document with
        //   the design-time helpers (DesignTimeDirective, lookup variables) intact, and
        //   inspects the post-pipeline documentNode for additional tooling. The split is
        //   a runtime optimization for incremental compilation; it provides no benefit
        //   at design time and would break the IDE's assumptions.
        if (codeDocument.FileKind != RazorFileKind.Component ||
            codeDocument.CodeGenerationOptions.SuppressPrimaryMethodBody ||
            codeDocument.CodeGenerationOptions.DesignTime)
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
        // nodes) by reference, and skip renderMethod itself.
        var declDocNode = CloneContainer(documentNode);
        var declNamespace = CloneContainer(primaryNamespace);
        var declClass = CloneContainer(primaryClass);

        foreach (var classChild in primaryClass.Children)
        {
            if (classChild != renderMethod)
            {
                declClass.Children.Add(classChild);
            }
        }

        foreach (var nsChild in primaryNamespace.Children)
        {
            declNamespace.Children.Add(nsChild == primaryClass ? declClass : nsChild);
        }

        foreach (var docChild in documentNode.Children)
        {
            declDocNode.Children.Add(docChild == primaryNamespace ? declNamespace : docChild);
        }

        foreach (var diagnostic in allDiagnostics)
        {
            declDocNode.AddDiagnostic(diagnostic);
        }

        var declDocument = RazorCSharpDocumentWriter.Write(declDocNode, codeDocument, cancellationToken, reportDiagnostics: false);

        // Build the impl synthetic tree: brand-new spine containing just the namespace,
        // its using directives, and a partial class wrapping only renderMethod.
        var usings = primaryNamespace.FindDescendantNodes<UsingDirectiveIntermediateNode>();
        var implDocNode = CloneContainer(documentNode);
        var implNamespace = CloneContainer(primaryNamespace);
        var implClass = CloneContainer(primaryClass);

        implClass.Children.Add(renderMethod);

        foreach (var usingDirective in usings)
        {
            implNamespace.Children.Add(usingDirective);
        }
        implNamespace.Children.Add(implClass);
        implDocNode.Children.Add(implNamespace);

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
