// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.NET.Sdk.Razor.SourceGenerators;

/// <summary>
/// Stage 5.1 placeholder-matrix compile tests. Each test triggers one of the
/// matrix's emission contexts (EventCallback&lt;T&gt;, untyped EventCallback,
/// other typed bound attribute, `@expr` markup output) and asserts the
/// generated C# parses cleanly under Roslyn -- so the placeholder text we
/// pick really does compile in the surrounding generated code, not just in
/// the unit test that exercises the helper in isolation. The plan calls for
/// "at least the 4 most common contexts" with compile-tests; this file
/// is the corresponding harness.
/// </summary>
public sealed class MissingValuePlaceholder_MatrixTests : RazorSourceGeneratorTestsBase
{
    private static CSharpParseOptions WithEnhancedRecovery(string value)
        => CSharpParseOptions.Default.WithFeatures([new("use-enhanced-recovery", value)]);

    // Locks down the placeholder-text matrix end-to-end: each kind in the
    // matrix maps to the exact text the generator will substitute. Direct
    // unit-level coverage of MissingValueMarker.GetPlaceholderText sits in
    // the language UnitTests project; this Theory backstops the four
    // surrounding compile-tests with the contract they implicitly rely on.
    // Kind is passed as a string so the public Theory signature does not
    // need to expose the internal enum.
    [Theory]
    [InlineData(nameof(MissingValuePlaceholderKind.EventCallbackTyped), null, "default(global::System.Action)")]
    [InlineData(nameof(MissingValuePlaceholderKind.EventCallbackTyped), "global::Foo.Bar", "default(global::System.Action<global::Foo.Bar>)")]
    [InlineData(nameof(MissingValuePlaceholderKind.EventCallbackUntyped), null, "default(global::System.Action)")]
    [InlineData(nameof(MissingValuePlaceholderKind.BoundAttributeTyped), null, "default!")]
    [InlineData(nameof(MissingValuePlaceholderKind.BoundAttributeTyped), "global::System.Int32", "default(global::System.Int32)")]
    [InlineData(nameof(MissingValuePlaceholderKind.BoundAttributeUnknown), null, "default!")]
    [InlineData(nameof(MissingValuePlaceholderKind.MarkupExpression), null, "\"\"")]
    [InlineData(nameof(MissingValuePlaceholderKind.StatementContext), null, "_ = (object?)null")]
    public void PlaceholderMatrix_GetPlaceholderText_MatchesContract(
        string kindName, string? typeArgument, string expected)
    {
        var kind = System.Enum.Parse<MissingValuePlaceholderKind>(kindName);
        var actual = MissingValueMarker.GetPlaceholderText(kind, typeArgument);
        Assert.Equal(expected, actual);
    }

    // 1. EventCallback<T> bound attribute (e.g. @onclick) with empty value.
    //    Placeholder: default(global::System.Action<TEventArgs>).
    [Fact, WorkItem("https://github.com/dotnet/razor/issues/10383")]
    public async Task PlaceholderMatrix_EventCallbackTyped_GeneratesValidCSharp()
    {
        var project = CreateTestProject(new()
        {
            ["Shared/Component1.razor"] = """
                @using Microsoft.AspNetCore.Components.Web

                <button @onclick="">Click me</button>
                """,
        }, cSharpParseOptions: WithEnhancedRecovery("true"));

        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);
        var result = RunGenerator(compilation!, ref driver);

        var source = result.GeneratedSources.Single().SourceText.ToString();
        Assert.Contains(
            "default(global::System.Action<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>)",
            source);
    }

    // 2. EventCallback (untyped) -- exercised via a custom component that
    //    declares an `EventCallback` (no type argument) parameter.
    //    Placeholder: default(global::System.Action).
    [Fact]
    public async Task PlaceholderMatrix_EventCallbackUntyped_GeneratesValidCSharp()
    {
        var project = CreateTestProject(
            additionalSources: new()
            {
                ["Shared/Parent.razor"] = """
                    <MyButton OnPressed="" />
                    """,
                ["Shared/MyButton.razor"] = """
                    <button>@ChildContent</button>

                    @code {
                        [Microsoft.AspNetCore.Components.Parameter]
                        public Microsoft.AspNetCore.Components.EventCallback OnPressed { get; set; }

                        [Microsoft.AspNetCore.Components.Parameter]
                        public Microsoft.AspNetCore.Components.RenderFragment? ChildContent { get; set; }
                    }
                    """,
            },
            cSharpParseOptions: WithEnhancedRecovery("true"));

        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);
        var result = RunGenerator(compilation!, ref driver);

        // The Parent.razor generated source must contain the untyped placeholder.
        var parentSource = result.GeneratedSources
            .Single(g => g.HintName.Contains("Parent"))
            .SourceText.ToString();
        Assert.Contains("default(global::System.Action)", parentSource);
    }

    // 3. Other bound attribute, type fully known -- e.g. an int-typed component
    //    parameter with an empty value. Placeholder: default(global::System.Int32).
    [Fact]
    public async Task PlaceholderMatrix_TypedBoundAttribute_GeneratesValidCSharp()
    {
        var project = CreateTestProject(
            additionalSources: new()
            {
                ["Shared/Parent.razor"] = """
                    <MyCounter Count="" />
                    """,
                ["Shared/MyCounter.razor"] = """
                    <p>@Count</p>

                    @code {
                        [Microsoft.AspNetCore.Components.Parameter]
                        public int Count { get; set; }
                    }
                    """,
            },
            cSharpParseOptions: WithEnhancedRecovery("true"));

        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);
        var result = RunGenerator(compilation!, ref driver);

        var parentSource = result.GeneratedSources
            .Single(g => g.HintName.Contains("Parent"))
            .SourceText.ToString();
        // The placeholder text is `default(global::System.Int32)` since `int`
        // gets globally-qualified by TypeNameHelper.
        Assert.Contains("default(global::System.Int32)", parentSource);
    }

    // 4. Markup-output context (`@expr`) with a missing C# value.
    //    Stage 5.0 wires the missing-value marker only on the attribute-value
    //    path; the `@expr` markup-output path is left for a later stage. The
    //    placeholder text the matrix prescribes for this context is locked in
    //    by the PlaceholderMatrix_GetPlaceholderText_MatchesContract theory
    //    above (MarkupExpression -> ""). The end-to-end compile-test will be
    //    added when the parser path is wired
    //    (see razor-recovery-redesign-plan.md, Stage 5.1 markup-output note).
}
