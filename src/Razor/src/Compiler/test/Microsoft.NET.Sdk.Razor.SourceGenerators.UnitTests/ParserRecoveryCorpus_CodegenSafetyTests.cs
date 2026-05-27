// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.NET.Sdk.Razor.SourceGenerators;

/// <summary>
/// Stage 5.1 end-to-end coverage for the parser-recovery placeholder matrix.
/// Each corpus .razor file (committed under
/// <c>src/Razor/src/Compiler/Microsoft.AspNetCore.Razor.Language/legacyTest/ParserRecoveryCorpus/</c>)
/// is run through the source generator under <c>use-enhanced-recovery=true</c>;
/// the test asserts the generated C# parses cleanly under Roslyn for the
/// "wall of red" cases. The corpus-driven assertions cover the motivating bug
/// <see href="https://github.com/dotnet/razor/issues/10383"/>.
/// </summary>
/// <remarks>
/// See <c>src/Razor/docs/plans/ErrorRecovery/razor-recovery-redesign-plan.md</c>
/// Stage 5.1 for the placeholder matrix and exit criteria.
/// </remarks>
public sealed class ParserRecoveryCorpus_CodegenSafetyTests : RazorSourceGeneratorTestsBase
{
    private const string ComponentsWebUsing = "@using Microsoft.AspNetCore.Components.Web";

    // The corpus path is the canonical location for parser-recovery .razor inputs.
    // Each file is the minimal reproducer for a parser-recovery scenario; we
    // load them at test time rather than embedding to avoid duplication.
    private static string GetCorpusFile(string fileName)
    {
        var repoRoot = TestProject.GetRepoRoot();
        return Path.Combine(
            repoRoot,
            "src", "Razor", "src", "Compiler",
            "Microsoft.AspNetCore.Razor.Language", "legacyTest", "ParserRecoveryCorpus",
            fileName);
    }

    private static string ReadCorpusComponent(string fileName)
    {
        var content = File.ReadAllText(GetCorpusFile(fileName));

        // The corpus files are minimal -- they do not import Components.Web. The
        // component-pipeline event-handler tag helper (`@onclick`, etc.) only
        // attaches when the using is present, so we prepend it for tests that
        // exercise the event-handler placeholder path. This mirrors the
        // Stage 5.0.0 spike's canonical reproducer.
        if (!content.Contains("Microsoft.AspNetCore.Components.Web"))
        {
            content = ComponentsWebUsing + "\n\n" + content;
        }

        return content;
    }

    // Stage 5.1 exit criterion: for the wall-of-red corpus case the generated
    // C# parses cleanly under Roslyn (zero CS diagnostics from the source
    // generator's output compilation).
    [Fact, WorkItem("https://github.com/dotnet/razor/issues/10383")]
    public async Task EmptyBoundAttribute_Onclick_EnhancedMode_NoCascadingCSharpDiagnostics()
    {
        var componentSource = ReadCorpusComponent("EmptyBoundAttribute_Onclick.razor");
        await RunCorpusCaseAndAssertCleanCSharp(
            "EmptyBoundAttribute_Onclick",
            componentSource);
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/10383")]
    public async Task EmptyBoundAttribute_Onclick_LegacyMode_NoCascadingCSharpDiagnostics()
    {
        // Stage 5.0 unified the legacy and enhanced parser shapes via the
        // missing-value marker. Stage 5.1's codegen placeholder must therefore
        // also fix the wall-of-red under the default (legacy) parser. This is
        // the user-visible default code path today.
        var componentSource = ReadCorpusComponent("EmptyBoundAttribute_Onclick.razor");
        await RunCorpusCaseAndAssertCleanCSharp(
            "EmptyBoundAttribute_Onclick.legacy",
            componentSource,
            useEnhancedRecovery: null);
    }

    private async Task RunCorpusCaseAndAssertCleanCSharp(
        string caseName,
        string componentSource,
        string? useEnhancedRecovery = "true")
    {
        var parseOptions = CSharpParseOptions.Default;
        if (useEnhancedRecovery is not null)
        {
            parseOptions = parseOptions.WithFeatures([new("use-enhanced-recovery", useEnhancedRecovery)]);
        }

        var project = CreateTestProject(new()
        {
            ["Shared/Component1.razor"] = componentSource,
        }, cSharpParseOptions: parseOptions);

        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // No `verify:` argument -- the default RunGenerator overload asserts the
        // output compilation has no diagnostics. If any CS error escapes from
        // the generated text the test fails with the full diagnostic list,
        // which is exactly the signal Stage 5.1's exit criterion wants.
        _ = RunGenerator(compilation!, ref driver);
    }
}
