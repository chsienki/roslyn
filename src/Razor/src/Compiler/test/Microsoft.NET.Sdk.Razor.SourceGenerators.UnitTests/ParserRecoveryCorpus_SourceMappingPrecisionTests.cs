// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Mvc.Razor.Extensions;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Test.Utilities;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.NET.Sdk.Razor.SourceGenerators;

/// <summary>
/// Source-mapping precision audit. For each parser-recovery corpus
/// .razor file, this harness runs the Razor compiler and inspects
/// <see cref="RazorCSharpDocument.SourceMappingsSortedByOriginal"/> to
/// verify that no single mapping spans an excessively large region of the
/// original document. A wide mapping is the symptom of a missing/skipped
/// boundary that codegen failed to split on -- a missing or skipped region
/// must produce a hole in the mapping list, with tight mappings on either
/// side.
/// </summary>
public sealed class ParserRecoveryCorpus_SourceMappingPrecisionTests : RazorSourceGeneratorTestsBase
{
    private readonly ITestOutputHelper _output;

    public ParserRecoveryCorpus_SourceMappingPrecisionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Exit criteria: the widest single OriginalSpan in the mapping list
    // should be no longer than 50 characters (target). Investigate any case
    // that exceeds 200. We assert the harder threshold so the test fails
    // loudly if codegen widens.
    private const int InvestigationFailureThreshold = 200;

    // Measurement helper. Authored here (the source-generator test project)
    // because RazorCSharpDocument is a codegen output type reachable from
    // this project but not from legacyTest/.
    internal static (int max, int total, int count) MeasureMappingWidths(RazorCSharpDocument csharpDoc)
    {
        var widths = csharpDoc.SourceMappingsSortedByOriginal.Select(m => m.OriginalSpan.Length).ToArray();
        return (widths.DefaultIfEmpty(0).Max(),
                widths.Sum(),
                csharpDoc.SourceMappingsSortedByOriginal.Length);
    }

    // The parser-recovery corpus lives in legacyTest. Mirror the loading
    // convention used by ParserRecoveryCorpus_CodegenSafetyTests.
    private static string GetCorpusFile(string fileName)
    {
        var repoRoot = TestProject.GetRepoRoot();
        return Path.Combine(
            repoRoot,
            "src", "Razor", "src", "Compiler",
            "Microsoft.AspNetCore.Razor.Language", "legacyTest", "ParserRecoveryCorpus",
            fileName);
    }

    public static IEnumerable<object[]> CorpusCases()
    {
        // RazorFileKind.Component covers the Blazor component pipeline.
        // RazorFileKind.Legacy covers the MVC view pipeline.
        // Both are exercised so codegen audits cover both writer hierarchies
        // (ComponentNodeWriter vs IntermediateNodeWriter / Mvc extensions).
        var cases = new (string FileName, RazorFileKind Kind, string ComponentsWebUsing)[]
        {
            ("BareAtFollowedByGarbage.razor", RazorFileKind.Component, ""),
            ("EmptyBoundAttribute_Onclick.razor", RazorFileKind.Component, "@using Microsoft.AspNetCore.Components.Web\n\n"),
            ("EmptyExplicitExpression.razor", RazorFileKind.Component, ""),
            ("ImplicitExpressionHittingMarkup.razor", RazorFileKind.Component, ""),
            ("MalformedCSharpWithSurroundingMarkup.razor", RazorFileKind.Component, ""),
            ("MalformedTagAttribute.razor", RazorFileKind.Component, ""),
            ("MalformedUsing.razor", RazorFileKind.Component, ""),
            ("MidStatementGarbage.razor", RazorFileKind.Component, ""),
            ("UnclosedCodeBlock.razor", RazorFileKind.Component, ""),
            ("UnclosedExplicitExpression.razor", RazorFileKind.Component, ""),
            ("UnclosedForeach.razor", RazorFileKind.Component, ""),
            ("UnclosedIfParen.razor", RazorFileKind.Component, ""),
            ("UnclosedMethodCallInImplicit.razor", RazorFileKind.Component, ""),
            ("UnclosedString.razor", RazorFileKind.Component, ""),
            ("UnclosedSwitch.razor", RazorFileKind.Component, ""),
            ("UnclosedTag.razor", RazorFileKind.Component, ""),
            ("UnnamedTag.razor", RazorFileKind.Component, ""),
        };

        foreach (var c in cases)
        {
            yield return new object[] { c.FileName, c.Kind, c.ComponentsWebUsing };
        }
    }

    [Theory, MemberData(nameof(CorpusCases))]
    public void EnhancedRecovery_CorpusMappingWidths_StayWithinInvestigationThreshold(
        string fileName, RazorFileKind kind, string componentsWebUsing)
    {
        var componentSource = File.ReadAllText(GetCorpusFile(fileName));
        if (!string.IsNullOrEmpty(componentsWebUsing))
        {
            componentSource = componentsWebUsing + componentSource;
        }

        var csharpDoc = ProcessSingleCorpusFile(componentSource, kind);
        var (max, total, count) = MeasureMappingWidths(csharpDoc);

        // Record widths so they can be inspected from the test output.
        _output.WriteLine($"corpus={fileName} max={max} total={total} count={count}");

        // Always dump the widest mapping (where present) to give investigation
        // a starting point if the 50-char target is broken later. The
        // dump goes to xunit's per-test output, which is collected by the
        // baseline test runner only on failure unless the runner is invoked
        // with detailed verbosity.
        if (count > 0)
        {
            var widest = csharpDoc.SourceMappingsSortedByOriginal
                .OrderByDescending(m => m.OriginalSpan.Length)
                .First();
            _output.WriteLine($"  widest OriginalSpan={widest.OriginalSpan} (length={widest.OriginalSpan.Length})");
        }

        Assert.True(
            max <= InvestigationFailureThreshold,
            $"Corpus '{fileName}' produced a source mapping of width {max} which exceeds the " +
            $"investigation threshold of {InvestigationFailureThreshold}. A range that " +
            $"crosses a MissingToken or SkippedContentSyntax must be split at that boundary. " +
            $"Audit the codegen writer for this corpus shape.");
    }

    private static RazorCSharpDocument ProcessSingleCorpusFile(
        string componentSource,
        RazorFileKind fileKind)
    {
        var configuration = new RazorConfiguration(
            RazorLanguageVersion.Latest,
            ConfigurationName: "default",
            Extensions: [],
            UseConsolidatedMvcViews: true,
            SuppressAddComponentParameter: false);

        var relativePhysicalPath = fileKind == RazorFileKind.Component
            ? "Shared/Component1.razor"
            : "Views/Index.cshtml";
        var filePath = "/" + relativePhysicalPath;

        var fileSystem = new VirtualRazorProjectFileSystem();
        var item = new SourceGeneratorProjectItem(
            basePath: "/",
            filePath: filePath,
            relativePhysicalPath: relativePhysicalPath,
            fileKind: fileKind,
            additionalText: new TestAdditionalText(path: filePath, text: SourceText.From(componentSource)),
            cssScope: null);
        fileSystem.Add(item);

        var projectEngine = RazorProjectEngine.Create(configuration, fileSystem, b =>
        {
            b.SetRootNamespace("MyApp");

            CompilerFeatures.Register(b);
            RazorExtensions.Register(b);
        });

        var codeDocument = projectEngine.Process(item);
        return codeDocument.GetRequiredCSharpDocument();
    }
}
