// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.NET.Sdk.Razor.SourceGenerators;

/// <summary>
/// End-to-end coverage for the parser-recovery placeholder matrix. Each
/// corpus .razor file (committed under
/// <c>src/Razor/src/Compiler/Microsoft.AspNetCore.Razor.Language/legacyTest/ParserRecoveryCorpus/</c>)
/// is run through the source generator and the test asserts the generated
/// C# parses cleanly under Roslyn for the "wall of red" cases. The
/// corpus-driven assertions cover the motivating bug
/// <see href="https://github.com/dotnet/razor/issues/10383"/>.
/// </summary>
/// <remarks>
/// See <c>src/Razor/docs/plans/ErrorRecovery/parser-recovery.md</c>
///  for the placeholder matrix and exit criteria.
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
        //  spike's canonical reproducer.
        if (!content.Contains("Microsoft.AspNetCore.Components.Web"))
        {
            content = ComponentsWebUsing + "\n\n" + content;
        }

        return content;
    }

    //  exit criterion: for the wall-of-red corpus case the generated
    // C# parses cleanly under Roslyn (zero CS diagnostics from the source
    // generator's output compilation).
    [Fact, WorkItem("https://github.com/dotnet/razor/issues/10383")]
    public async Task EmptyBoundAttribute_Onclick_NoCascadingCSharpDiagnostics()
    {
        var componentSource = ReadCorpusComponent("EmptyBoundAttribute_Onclick.razor");
        await RunCorpusCaseAndAssertCleanCSharp(
            "EmptyBoundAttribute_Onclick",
            componentSource);
    }

    private async Task RunCorpusCaseAndAssertCleanCSharp(
        string caseName,
        string componentSource)
    {
        var project = CreateTestProject(new()
        {
            ["Shared/Component1.razor"] = componentSource,
        });

        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // No `verify:` argument -- the default RunGenerator overload asserts the
        // output compilation has no diagnostics. If any CS error escapes from
        // the generated text the test fails with the full diagnostic list,
        // which is exactly the signal exit criterion wants.
        _ = RunGenerator(compilation!, ref driver);
    }
}
