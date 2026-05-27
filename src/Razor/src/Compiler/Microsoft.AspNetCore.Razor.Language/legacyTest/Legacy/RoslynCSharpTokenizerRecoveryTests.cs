// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq;
using Microsoft.AspNetCore.Razor.Language.Syntax;
using Xunit;

namespace Microsoft.AspNetCore.Razor.Language.Legacy;

/// <summary>
/// Stage 4.4 of the parser error-recovery redesign plan
/// (see <c>src/Razor/docs/plans/ErrorRecovery/razor-recovery-redesign-plan.md</c>).
///
/// Exercises cross-language tokenizer-state alignment when the
/// <c>RoslynCSharpTokenizer</c> is active. The C# parser's recovery
/// <c>Synchronize</c> calls now invoke <c>EndingBlock()</c> when they
/// stop at an outer-follow token (<c>SyncStopReason.AtOuterFollowToken</c>)
/// via the <c>EndingBlockIfStoppedOnOuter</c> helper, so the wrapped
/// Roslyn <c>SyntaxTokenParser</c>'s position and state stay aligned
/// with the cursor the HTML parser resumes from. The matching
/// <c>StartingBlock</c> call already fires automatically when C# is
/// re-entered via <c>ParseBlock</c>.
///
/// Tests run with both <c>UseRoslynTokenizer = true</c> (default for
/// this class -- <c>useLegacyTokenizer: false</c>) and
/// <c>UseEnhancedRecovery = true</c>. The native tokenizer's
/// <c>StartingBlock</c>/<c>EndingBlock</c> are no-ops, so the matching
/// legacy-tokenizer coverage already lives in
/// <see cref="ParserRecoveryCorpusSnapshotTests"/>; the third test
/// here pins the legacy/Roslyn tree-equality invariant explicitly.
/// </summary>
public class RoslynCSharpTokenizerRecoveryTests() : ParserTestBase(layer: TestProject.Layer.Compiler, validateSpanEditHandlers: true, useLegacyTokenizer: false)
{
    [Fact]
    public void MalformedCSharpWithSurroundingMarkup_RoslynTokenizer_EnhancedRecovery()
    {
        // Same canonical corpus case as Stage 4.2's
        // `MalformedCSharpWithSurroundingMarkup_EnhancedRecovery`, but
        // parsed with the Roslyn tokenizer active. The C# parser's
        // `TryParseCondition` `Synchronize` stops at the outer-follow
        // `<` token; the Stage 4.4 `EndingBlockIfStoppedOnOuter` hook
        // tears down the Roslyn tokenizer's wrapped-parser state so
        // subsequent re-entries into C# remain aligned.
        const string source = "<div>@if(foo bar baz<p>still html</p></div>";

        var tree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        // The skipped C# garbage `foo bar baz` is wrapped in a single
        // `SkippedContentSyntax` (Stage 4.2 invariant -- preserved here
        // under the Roslyn tokenizer).
        var skipped = tree.Root.DescendantNodes().OfType<SkippedContentSyntax>().Single();
        Assert.Equal(SyntaxKind.CSharpCodeBlock, skipped.OriginatingLanguage);
        Assert.Equal("foo bar baz", skipped.GetContent());

        // The `<p>still html</p>` parses as a real `MarkupElement` (not
        // absorbed by C# recovery, not wrapped in a
        // `MarkupMiscAttributeContent`).
        Assert.Empty(tree.Root.DescendantNodes().OfType<MarkupMiscAttributeContentSyntax>());
        var pElement = tree.Root.DescendantNodes()
            .OfType<MarkupElementSyntax>()
            .Single(e => e.MarkupStartTag is { } start && start.Name.Content == "p");
        Assert.NotNull(pElement.MarkupStartTag);
        Assert.NotNull(pElement.MarkupEndTag);

        // The outer `<div>...</div>` survives intact.
        var divElement = tree.Root.DescendantNodes()
            .OfType<MarkupElementSyntax>()
            .Single(e => e.MarkupStartTag is { } start && start.Name.Content == "div");
        Assert.NotNull(divElement.MarkupStartTag);
        Assert.NotNull(divElement.MarkupEndTag);
    }

    [Fact]
    public void RecoveryFollowedByImplicitTransition_RoslynTokenizer_EnhancedRecovery()
    {
        // The behavioural correctness check for Stage 4.4: after the C#
        // parser bails on an outer-follow token, the HTML parser must be
        // able to re-enter C# via a subsequent transition (`@bar`)
        // without the Roslyn `SyntaxTokenParser` losing alignment.
        //
        // The Stage 4.4 `EndingBlockIfStoppedOnOuter` hook discards any
        // trailing trivia the Roslyn parser may have consumed past the
        // last C# token at the bail-out point (via
        // `RoslynCSharpTokenizer.EndingBlock` -> `ResetTo` ->
        // `SkipForwardTo(lastToken.Span.End)`), and resets
        // `CurrentState` back to `Start`. Subsequent re-entries into C#
        // then start from a clean Roslyn state.
        const string source = "<div>@if(foo bar baz<p>@bar</p></div>";

        var tree = ParseDocument(
            source,
            configureParserOptions: builder => builder.UseEnhancedRecovery = true);

        // The `foo bar baz` garbage is in a single `SkippedContentSyntax`.
        var skipped = tree.Root.DescendantNodes().OfType<SkippedContentSyntax>().Single();
        Assert.Equal(SyntaxKind.CSharpCodeBlock, skipped.OriginatingLanguage);
        Assert.Equal("foo bar baz", skipped.GetContent());

        // The subsequent `@bar` re-enters C# successfully and parses
        // `bar` as the implicit expression body. This is the canonical
        // verification that the Roslyn tokenizer state was torn down at
        // the recovery bail-out: a stale Roslyn parser would mis-position
        // the identifier or fail to produce a clean implicit-expression
        // node at the expected location.
        var bar = tree.Root.DescendantNodes()
            .OfType<CSharpImplicitExpressionSyntax>()
            .Single();
        Assert.Equal("@bar", bar.GetContent());

        var atPosition = source.IndexOf("@bar", System.StringComparison.Ordinal);
        Assert.True(atPosition > 0, "Source should contain '@bar'.");
        Assert.Equal(atPosition, bar.SpanStart);

        var body = bar.Body;
        Assert.NotNull(body);
        Assert.Equal("bar", body.GetContent());
        Assert.Equal(atPosition + 1, body.SpanStart);

        // The outer `<div>...</div>` still survives intact and the
        // `<p>...</p>` containing the recovered `@bar` parses cleanly.
        var pElement = tree.Root.DescendantNodes()
            .OfType<MarkupElementSyntax>()
            .Single(e => e.MarkupStartTag is { } start && start.Name.Content == "p");
        Assert.NotNull(pElement.MarkupStartTag);
        Assert.NotNull(pElement.MarkupEndTag);
        Assert.Contains(bar, pElement.DescendantNodes().OfType<CSharpImplicitExpressionSyntax>());

        var divElement = tree.Root.DescendantNodes()
            .OfType<MarkupElementSyntax>()
            .Single(e => e.MarkupStartTag is { } start && start.Name.Content == "div");
        Assert.NotNull(divElement.MarkupStartTag);
        Assert.NotNull(divElement.MarkupEndTag);
        Assert.Contains(pElement, divElement.DescendantNodes().OfType<MarkupElementSyntax>());
    }

    [Fact]
    public void RoslynAndLegacyTokenizers_ProduceEquivalentTrees_AcrossRecovery()
    {
        // Stage 4.4 invariant: the tokenizer choice must not be
        // observable in the produced syntax tree when both tokenizers
        // are wired through enhanced-recovery cross-language sync.
        // This pins the contract that
        // `EndingBlockIfStoppedOnOuter` keeps the Roslyn tokenizer's
        // wrapped-parser state in sync with the native tokenizer's
        // (which is a no-op for these hooks) across multiple
        // recovery + re-entry cycles.
        //
        // The chosen corpus stresses several enhanced-mode recovery
        // sites: `ParseExplicitExpressionBody` (`@(`), `TryParseCondition`
        // (`@if(`), and implicit-expression re-entry (`@x`).
        var sources = new[]
        {
            "<div>@if(foo bar baz<p>still html</p></div>",
            "<div>@if(foo bar baz<p>@bar</p></div>",
            "<div>@(unclosed<p>after</p></div>",
            "<div>@{ if (foo bar <p>x</p> } more</div>",
        };

        foreach (var source in sources)
        {
            var roslynTree = ParseDocument(
                source,
                configureParserOptions: builder =>
                {
                    builder.UseEnhancedRecovery = true;
                });

            var legacyTree = ParseDocument(
                source,
                configureParserOptions: builder =>
                {
                    builder.UseEnhancedRecovery = true;
                    builder.UseRoslynTokenizer = false;
                });

            // Compare the structural serialization of both trees.
            // Different tokenizer state would surface as different
            // SpanStart / EndPosition / kind sequences.
            var roslynShape = SerializeTreeShape(roslynTree.Root);
            var legacyShape = SerializeTreeShape(legacyTree.Root);
            Assert.Equal(legacyShape, roslynShape);
        }

        static string SerializeTreeShape(SyntaxNode root)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var n in root.DescendantNodesAndSelf())
            {
                sb.Append(n.GetType().Name)
                  .Append('[').Append(n.SpanStart).Append("..").Append(n.EndPosition).Append(')')
                  .Append('|').Append(n.GetContent()?.Replace("\n", "\\n"))
                  .Append('\n');
            }
            return sb.ToString();
        }
    }
}
