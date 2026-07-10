// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Razor.Language.Extensions;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using Xunit;

namespace Microsoft.AspNetCore.Razor.Language.IntegrationTests;

// Verifies the markup splitter against real, fully-lowered component IR (the shape it sees when the
// decl/impl lowering phases run). The whole design rests on class-body markup still being present as
// markup IR nodes at that point and on every class-body child being a kind the splitter can route.
public class MarkupSplitterComponentTest : RazorIntegrationTestBase
{
    internal override RazorFileKind? FileKind => RazorFileKind.Component;

    internal override bool UseTwoPhaseCompilation => true;

    [Fact]
    public void MarkupMethod_LiftsToImpl_AndCompiles()
    {
        var generated = CompileToCSharp("""
            @code {
                private Microsoft.AspNetCore.Components.RenderFragment Make() => @<p>Hi</p>;
            }
            """);

        // The markup method lifts wholesale to the impl half; the markup-free decl half keeps none of it.
        Assert.NotNull(generated.DeclCode);
        Assert.DoesNotContain("Make", generated.DeclCode);
        Assert.Contains("Make", generated.Code);

        // decl + impl are emitted as partial halves that recombine and compile.
        CompileToAssembly(generated);
    }

    [Fact]
    public void MarkupMethod_AlongsideMarkupFreeMembers_RoutesEachHalf()
    {
        var generated = CompileToCSharp("""
            @code {
                [Microsoft.AspNetCore.Components.Parameter] public int Count { get; set; }
                private Microsoft.AspNetCore.Components.RenderFragment Make() => @<p>Hi</p>;
            }
            """);

        // The parameter (descriptor surface) stays in decl; the markup method lifts to impl.
        Assert.NotNull(generated.DeclCode);
        Assert.Contains("Count", generated.DeclCode);
        Assert.DoesNotContain("Make", generated.DeclCode);
        Assert.Contains("Make", generated.Code);

        CompileToAssembly(generated);
    }

    [Fact]
    public void ExpressionTemplateProperty_SurvivesAsTemplateMarkup_AndSplits()
    {
        var generated = CompileToCSharp("""
            @code {
                public Microsoft.AspNetCore.Components.RenderFragment Header => @<div>Hello</div>;
            }
            """);

        var documentNode = generated.CodeDocument.GetDocumentNode();
        Assert.NotNull(documentNode);
        var primaryClass = documentNode.FindPrimaryClass();
        var renderMethod = documentNode.FindPrimaryMethod();
        Assert.NotNull(primaryClass);
        Assert.NotNull(renderMethod);

        // Invariant: the `@<...>` markup is still an IR node at class-body scope (not pre-lowered to
        // __builder C#), and specifically an expression-position TemplateIntermediateNode.
        Assert.True(MarkupSplitter.HasClassBodyMarkup(primaryClass, renderMethod));
        var children = MarkupSplitter.CollectClassBodyChildren(primaryClass, renderMethod);
        Assert.Contains(children, static c => c is TemplateIntermediateNode);

        // Classification: every class-body child is a kind the splitter can route -- no surface
        // extension node (an @inject) hiding among the @code content.
        Assert.All(children, static c => Assert.True(MarkupSplitter.IsSupportedClassBodyNode(c)));

        // Decision: at the harness's Preview (>= C# 13) language version the markup property splits via
        // Path A, keeping its signature in decl.
        var decision = MarkupSplitter.Split(primaryClass, renderMethod, generated.CodeDocument.ParserOptions);
        var plan = Assert.IsType<SplitDecision.SplitPlan>(decision);
        Assert.Contains(plan.Members, static m => m.Kind == MemberSplitKind.MarkupProperty);
    }

    [Fact]
    public void PureCSharpCode_HasNoClassBodyMarkup_AndDoesNotSplit()
    {
        var generated = CompileToCSharp("""
            @code {
                private int _count;
                private void Increment() => _count++;
            }
            """);

        var documentNode = generated.CodeDocument.GetDocumentNode();
        Assert.NotNull(documentNode);
        var primaryClass = documentNode.FindPrimaryClass();
        var renderMethod = documentNode.FindPrimaryMethod();
        Assert.NotNull(primaryClass);
        Assert.NotNull(renderMethod);

        Assert.False(MarkupSplitter.HasClassBodyMarkup(primaryClass, renderMethod));
        Assert.Same(
            SplitDecision.NoSplit,
            MarkupSplitter.Split(primaryClass, renderMethod, generated.CodeDocument.ParserOptions));
    }

    [Fact]
    public void Inject_AlongsideMarkup_FallsBack()
    {
        // @inject lowers to a ComponentInjectIntermediateNode (surface, an ExtensionIntermediateNode
        // like a template). The splitter can't route it, so a component mixing it with markup falls back.
        var generated = CompileToCSharp("""
            @inject System.IServiceProvider Services
            @code {
                public Microsoft.AspNetCore.Components.RenderFragment Header => @<div>Hello</div>;
            }
            """);

        var documentNode = generated.CodeDocument.GetDocumentNode();
        Assert.NotNull(documentNode);
        var primaryClass = documentNode.FindPrimaryClass();
        var renderMethod = documentNode.FindPrimaryMethod();
        Assert.NotNull(primaryClass);
        Assert.NotNull(renderMethod);

        var decision = MarkupSplitter.Split(primaryClass, renderMethod, generated.CodeDocument.ParserOptions);
        var fallback = Assert.IsType<SplitDecision.SplitFallback>(decision);
        Assert.Equal(FallbackReason.UnsupportedClassBodyNode, fallback.Reason);
    }
}
