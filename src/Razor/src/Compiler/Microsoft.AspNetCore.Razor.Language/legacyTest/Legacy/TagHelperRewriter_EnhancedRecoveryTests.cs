// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq;
using Microsoft.AspNetCore.Razor.Language.Syntax;
using Xunit;

namespace Microsoft.AspNetCore.Razor.Language.Legacy;

/// <summary>
/// Stage 5.2 of the parser error-recovery redesign plan: audit
/// <see cref="TagHelperParseTreeRewriter"/> and
/// <see cref="TagHelperBlockRewriter"/> against the new tree shapes
/// produced by the enhanced-recovery parser.
///
/// The enhanced parser can place two shapes inside
/// <see cref="MarkupStartTagSyntax.Attributes"/> that the rewriter did
/// not previously see:
///
/// <list type="number">
///   <item><description>
///     A <see cref="MarkupAttributeBlockSyntax"/> whose
///     <c>Value</c> is the Big Design Decision #9 "missing C#
///     expression" shape
///     <c>GenericBlock([CSharpExpressionLiteral([MissingToken(Identifier)])])</c>.
///     This is the motivating <c>@onclick=""</c> case from
///     dotnet/razor#10383.
///   </description></item>
///   <item><description>
///     A <see cref="SkippedContentSyntax"/> sibling absorbed by
///     <c>ParseMiscAttribute</c>'s recovery (Stage 3.4).
///   </description></item>
/// </list>
///
/// Each test reproduces one of those shapes, drives the tag-helper
/// rewriter, and asserts the rewriter does not mangle the inner
/// <see cref="SyntaxKind.Identifier"/> missing token or drop trailing
/// attributes.
///
/// See <c>src/Razor/docs/plans/ErrorRecovery/razor-recovery-redesign-plan.md</c>
/// (Stage 5.2) for the full contract.
/// </summary>
public class TagHelperRewriter_EnhancedRecoveryTests : TagHelperRewritingTestBase
{
    private static readonly TagHelperCollection s_inputDirectiveAttribute_TagHelpers =
    [
        TagHelperDescriptorBuilder.CreateEventHandler("InputEventHandler", "SomeAssembly")
            .TagMatchingRuleDescriptor(rule => rule
                .RequireTagName("input")
                .RequireAttributeDescriptor(attribute => attribute
                    .Name("@onclick")
                    .IsDirectiveAttribute()))
            .BoundAttributeDescriptor(attribute => attribute
                .Name("@onclick")
                .PropertyName("onclick")
                .TypeName("Microsoft.AspNetCore.Components.EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs>")
                .IsDirectiveAttribute())
            .Build()
    ];

    private static readonly TagHelperCollection s_inputBoundIntAttribute_TagHelpers =
    [
        TagHelperDescriptorBuilder.CreateTagHelper("InputBoundIntTagHelper", "SomeAssembly")
            .TagMatchingRuleDescriptor(rule => rule.RequireTagName("input"))
            .BoundAttributeDescriptor(attribute => attribute
                .Name("count")
                .PropertyName("Count")
                .TypeName(typeof(int).FullName))
            .Build()
    ];

    private static readonly TagHelperCollection s_inputTwoDirectiveAttribute_TagHelpers =
    [
        TagHelperDescriptorBuilder.CreateEventHandler("InputTwoDirectiveTagHelper", "SomeAssembly")
            .TagMatchingRuleDescriptor(rule => rule
                .RequireTagName("input")
                .RequireAttributeDescriptor(attribute => attribute
                    .Name("@attr")
                    .IsDirectiveAttribute()))
            .BoundAttributeDescriptor(attribute => attribute
                .Name("@attr")
                .PropertyName("attr")
                .TypeName(typeof(string).FullName)
                .IsDirectiveAttribute())
            .BoundAttributeDescriptor(attribute => attribute
                .Name("@onclick")
                .PropertyName("onclick")
                .TypeName("Microsoft.AspNetCore.Components.EventCallback<Microsoft.AspNetCore.Components.Web.MouseEventArgs>")
                .IsDirectiveAttribute())
            .Build()
    ];

    [Fact]
    public void DirectiveAttribute_EmptyValue_EnhancedRecovery_PreservesBdd9MissingValueShape()
    {
        // BDD #9: `@onclick=""` on a directive-attribute-bound tag helper
        // parses to MarkupAttributeBlock.Value =
        //   GenericBlock([CSharpExpressionLiteral([MissingToken(Identifier)])])
        // After the tag-helper rewriter it must become
        //   MarkupTagHelperDirectiveAttribute.Value =
        //     MarkupTagHelperAttributeValue([CSharpExpressionLiteral([MissingToken(Identifier)])])
        // with the missing-Identifier token preserved zero-width.
        var (rewritten, _) = ParseAndRewrite(
            @"<input @onclick="""" />",
            s_inputDirectiveAttribute_TagHelpers);

        var directiveAttribute = rewritten.Root
            .DescendantNodes()
            .OfType<MarkupTagHelperDirectiveAttributeSyntax>()
            .Single();

        Assert.Equal("@onclick", directiveAttribute.FullName);
        Assert.False(directiveAttribute.EqualsToken.IsMissing);

        var value = Assert.IsType<MarkupTagHelperAttributeValueSyntax>(directiveAttribute.Value);
        var expressionLiteral = Assert.IsType<CSharpExpressionLiteralSyntax>(Assert.Single(value.Children));
        var missingToken = Assert.Single(expressionLiteral.LiteralTokens);
        Assert.True(missingToken.IsMissing);
        Assert.Equal(SyntaxKind.Identifier, missingToken.Kind);

        // The whole rewritten value subtree is zero-width: the rewriter
        // must not absorb any source characters into the placeholder.
        Assert.Equal(0, value.Width);
        Assert.Equal(0, expressionLiteral.Width);
        Assert.Equal(0, missingToken.Span.Length);
    }

    [Fact]
    public void MinimizedDirectiveAttribute_EnhancedRecovery_IsUnchanged()
    {
        // Stage 5.2 contract: minimized tag-helper directive attributes
        // represent "no value at all" -- distinct from "empty value".
        // Their semantics are unchanged by enhanced recovery; in
        // particular the rewriter must continue to produce a
        // MarkupMinimizedTagHelperDirectiveAttributeSyntax with no value
        // subtree, even when the surrounding tree shapes the enhanced
        // parser produces include missing tokens or skipped content.
        var (rewritten, _) = ParseAndRewrite(
            @"<input @onclick />",
            s_inputDirectiveAttribute_TagHelpers);

        var minimizedDirective = rewritten.Root
            .DescendantNodes()
            .OfType<MarkupMinimizedTagHelperDirectiveAttributeSyntax>()
            .Single();

        Assert.Equal("@onclick", minimizedDirective.FullName);
    }

    [Fact]
    public void MinimizedBoundAttribute_EnhancedRecovery_IsUnchanged()
    {
        // Same as above, for the non-directive minimized arm
        // (MarkupMinimizedTagHelperAttributeSyntax). Confirms that the
        // "no value at all" path is unaffected by the new tree shapes.
        var (rewritten, _) = ParseAndRewrite(
            @"<input count />",
            s_inputBoundIntAttribute_TagHelpers);

        var minimized = rewritten.Root
            .DescendantNodes()
            .OfType<MarkupMinimizedTagHelperAttributeSyntax>()
            .Single();

        Assert.Equal("count", minimized.Name.GetContent());
    }

    [Fact]
    public void SkippedContentBetweenAttributes_EnhancedRecovery_RewriterTreatsAsNoOp()
    {
        // Stage 3.4's `ParseMiscAttribute` migration introduces
        // `SkippedContentSyntax` nodes that can appear between attributes
        // inside `MarkupStartTag.Attributes`. The tag-helper rewriter
        // must treat such nodes as a no-op: preserve them in the
        // rewritten attribute list and continue rewriting subsequent
        // attributes (not abort on them like it does for
        // `MarkupMiscAttributeContent`).
        //
        // Today the parser does not naturally place a SkippedContent
        // between two well-formed attributes (synchronize is greedy and
        // tends to absorb the next attribute too -- see the in-test
        // dump for `<input!garbage @onclick="" />`). This test
        // synthesizes the shape by splicing a `SkippedContentSyntax`
        // between the two parsed attributes in a well-formed start tag,
        // then drives the full rewriter to verify forward-compat.
        var syntaxTree = ParseDocument(
            document: @"<input @attr=""x"" @onclick="""" />",
            fileKind: RazorFileKind.Component);

        var startTag = syntaxTree.Root
            .DescendantNodes()
            .OfType<MarkupStartTagSyntax>()
            .Single();

        var originalAttrs = startTag.Attributes.ToList();
        var attrBlocks = originalAttrs.OfType<MarkupAttributeBlockSyntax>().ToList();
        Assert.Equal(2, attrBlocks.Count);

        // Build a fresh attribute list with a SkippedContentSyntax
        // injected between the two MarkupAttributeBlock nodes. Use the
        // empty-tokens form (zero-width) tagged with MarkupBlock as
        // ParseMiscAttribute would do under enhanced recovery.
        var skipped = SyntaxFactory.SkippedContent(
            originatingLanguage: SyntaxKind.MarkupBlock);

        var rebuiltStartTag = startTag.WithAttributes(SyntaxFactory.List<RazorSyntaxNode>(
        [
            .. originalAttrs.TakeWhile(a => a != attrBlocks[1]),
            skipped,
            .. originalAttrs.SkipWhile(a => a != attrBlocks[1]),
        ]));
        var splicedRoot = syntaxTree.Root.ReplaceNode(startTag, rebuiltStartTag);
        var splicedTree = new RazorSyntaxTree(
            splicedRoot,
            syntaxTree.Source,
            syntaxTree.Diagnostics,
            syntaxTree.Options);

        // Sanity-check the splice: the start tag now has a
        // SkippedContentSyntax sibling between the two attribute blocks.
        var splicedStartTag = splicedTree.Root
            .DescendantNodes()
            .OfType<MarkupStartTagSyntax>()
            .Single();
        Assert.Single(splicedStartTag.Attributes.OfType<SkippedContentSyntax>());

        // Run the tag-helper rewriter. The rewriter must preserve the
        // SkippedContentSyntax and still bind both attributes -- it must
        // not abort on the SkippedContent like the catch-all
        // `result == null` path used to.
        var binder = new TagHelperBinder(tagNamePrefix: null, s_inputTwoDirectiveAttribute_TagHelpers);
        var rewritten = TagHelperParseTreeRewriter.Rewrite(splicedTree, binder);

        var rewrittenStartTag = rewritten.Root
            .DescendantNodes()
            .OfType<MarkupTagHelperStartTagSyntax>()
            .Single();

        // SkippedContentSyntax survived the rewriter.
        Assert.Single(rewrittenStartTag.Attributes.OfType<SkippedContentSyntax>());

        // Both directive attributes were rewritten (not just the first).
        // If the rewriter had aborted on the SkippedContent, the trailing
        // `@onclick` would still be a `MarkupAttributeBlockSyntax`.
        var directives = rewrittenStartTag.Attributes
            .OfType<MarkupTagHelperDirectiveAttributeSyntax>()
            .ToList();
        Assert.Equal(2, directives.Count);
        Assert.Equal("@attr", directives[0].FullName);
        Assert.Equal("@onclick", directives[1].FullName);

        // The trailing `@onclick=""` retains its BDD #9 missing-Identifier
        // value (the rewriter did not corrupt its value subtree).
        var onclickValue = Assert.IsType<MarkupTagHelperAttributeValueSyntax>(directives[1].Value);
        var onclickLiteral = Assert.IsType<CSharpExpressionLiteralSyntax>(Assert.Single(onclickValue.Children));
        var missingToken = Assert.Single(onclickLiteral.LiteralTokens);
        Assert.True(missingToken.IsMissing);
        Assert.Equal(SyntaxKind.Identifier, missingToken.Kind);
    }

    /// <summary>
    /// Parses the document and runs the tag-helper rewriter, returning the
    /// rewritten tree and the original (pre-rewrite) syntax tree. Mirrors
    /// the work that <see cref="TagHelperRewritingTestBase.EvaluateData"/>
    /// does, minus the baseline comparison -- these tests use structural
    /// assertions instead of golden baselines.
    /// </summary>
    private (RazorSyntaxTree Rewritten, RazorSyntaxTree Original) ParseAndRewrite(
        string content,
        TagHelperCollection tagHelpers,
        RazorFileKind? fileKind = RazorFileKind.Component)
    {
        // Default to Component mode so the parser treats `@onclick`
        // as a single directive-attribute name (rather than a `@`
        // transition into a C# implicit expression).
        var syntaxTree = ParseDocument(
            document: content,
            fileKind: fileKind);

        var binder = new TagHelperBinder(tagNamePrefix: null, tagHelpers);
        var rewrittenTree = TagHelperParseTreeRewriter.Rewrite(syntaxTree, binder);

        Assert.Equal(syntaxTree.Root.Width, rewrittenTree.Root.Width);

        return (rewrittenTree, syntaxTree);
    }
}
