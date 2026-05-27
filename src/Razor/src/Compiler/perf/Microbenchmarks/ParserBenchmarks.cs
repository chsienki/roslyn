// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Mvc.Razor.Extensions;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Syntax;

namespace Microsoft.AspNetCore.Razor.Microbenchmarks;

// Stage 6.4 of the Razor parser error-recovery redesign establishes the
// post-redesign performance baseline for RazorSyntaxTree.Parse. Inputs are
// split into "well-formed" (regression guard) and "ill-formed" (recovery
// path -- naturally slower, documented in plan-state.md).
[MemoryDiagnoser]
public class ParserBenchmarks
{
    // Small inline Blazor-style component used as a representative
    // well-formed .razor input. Kept tiny to model the common case of
    // tooling parsing many small components on each edit.
    private const string InlineComponent = """
        @page "/counter"

        <PageTitle>Counter</PageTitle>

        <h1>Counter</h1>

        <p role="status">Current count: @currentCount</p>

        <button class="btn btn-primary" @onclick="IncrementCount">Click me</button>

        @code {
            private int currentCount = 0;

            private void IncrementCount()
            {
                currentCount++;
            }
        }
        """;

    // Ill-formed inputs that exercise the enhanced recovery path. Each is
    // intentionally short so the recovery cost dominates parse time and we
    // can spot pathological allocations / re-tokenisation in the diff.

    // Empty event-handler attribute -- the motivating @onclick="" bug shape.
    private const string IllFormedEmptyEventHandler = """
        <button @onclick="">Click me</button>
        @code {
            private void Handler() { }
        }
        """;

    // Unclosed @code block -- forces recovery to run off the end of file.
    private const string IllFormedUnclosedCodeBlock = """
        <h1>Hello</h1>

        @code {
            private int x = 0;

            private void Increment()
            {
                x++;
        """;

    // Mid-attribute truncation -- a partial tag with a half-written
    // attribute value, exercising HTML-side recovery + skipped content.
    private const string IllFormedTruncatedAttribute = """
        <div class="container">
            <span title="hello
        """;

    private RazorSourceDocument _msn;
    private RazorSourceDocument _blazorServerTagHelpers;
    private RazorSourceDocument _inlineComponentDoc;
    private RazorSourceDocument _illFormedEmptyHandlerDoc;
    private RazorSourceDocument _illFormedUnclosedCodeBlockDoc;
    private RazorSourceDocument _illFormedTruncatedAttributeDoc;

    private RazorParserOptions _legacyOptions;
    private RazorParserOptions _componentOptions;

    [GlobalSetup]
    public void Setup()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "MSN.cshtml")))
        {
            current = current.Parent;
        }

        if (current == null)
        {
            throw new InvalidOperationException("Could not locate MSN.cshtml shared corpus file.");
        }

        var root = current;
        var fileSystem = RazorProjectFileSystem.Create(root.FullName);

        var projectEngine = RazorProjectEngine.Create(
            RazorConfiguration.Default,
            fileSystem,
            b => RazorExtensions.Register(b));

        var directiveFeature = projectEngine.Engine.GetFeatures<ConfigureDirectivesFeature>().FirstOrDefault();
        var directives = directiveFeature?.GetDirectives() ?? ImmutableArray<DirectiveDescriptor>.Empty;

        _legacyOptions = RazorParserOptions.Default.WithDirectives(directives);
        _componentOptions = RazorParserOptions.Create(
            RazorLanguageVersion.Latest,
            RazorFileKind.Component,
            configure: null).WithDirectives(directives);

        _msn = RazorSourceDocument.ReadFrom(
            fileSystem.GetItem(Path.Combine(root.FullName, "MSN.cshtml"), RazorFileKind.Legacy));
        _blazorServerTagHelpers = RazorSourceDocument.ReadFrom(
            fileSystem.GetItem(Path.Combine(root.FullName, "BlazorServerTagHelpers.razor"), RazorFileKind.Component));

        _inlineComponentDoc = RazorSourceDocument.Create(InlineComponent, "Counter.razor");
        _illFormedEmptyHandlerDoc = RazorSourceDocument.Create(IllFormedEmptyEventHandler, "EmptyHandler.razor");
        _illFormedUnclosedCodeBlockDoc = RazorSourceDocument.Create(IllFormedUnclosedCodeBlock, "UnclosedCode.razor");
        _illFormedTruncatedAttributeDoc = RazorSourceDocument.Create(IllFormedTruncatedAttribute, "TruncatedAttr.razor");

        // Critical correctness check (Stage 6.4): well-formed inputs must
        // produce ZERO SkippedContentSyntax nodes. Any non-zero count means
        // a recovery path is firing on valid input, which would be a real
        // bug. Run this in setup -- failure here surfaces in BenchmarkDotNet
        // output before any measurements are taken.
        AssertNoSkippedContent(_msn, _legacyOptions, nameof(_msn));
        AssertNoSkippedContent(_blazorServerTagHelpers, _componentOptions, nameof(_blazorServerTagHelpers));
        AssertNoSkippedContent(_inlineComponentDoc, _componentOptions, nameof(_inlineComponentDoc));
    }

    private static void AssertNoSkippedContent(RazorSourceDocument source, RazorParserOptions options, string name)
    {
        var tree = RazorSyntaxTree.Parse(source, options);
        var skipped = tree.Root.DescendantNodesAndSelf().Count(n => n.Kind == SyntaxKind.SkippedContent);
        if (skipped != 0)
        {
            throw new InvalidOperationException(
                $"Well-formed input '{name}' produced {skipped} SkippedContent node(s); recovery should not fire on valid input.");
        }
    }

    [Benchmark(Description = "Well-formed: MSN.cshtml (large legacy)")]
    public RazorSyntaxTree WellFormed_MSN()
        => RazorSyntaxTree.Parse(_msn, _legacyOptions);

    [Benchmark(Description = "Well-formed: BlazorServerTagHelpers.razor")]
    public RazorSyntaxTree WellFormed_BlazorServerTagHelpers()
        => RazorSyntaxTree.Parse(_blazorServerTagHelpers, _componentOptions);

    [Benchmark(Description = "Well-formed: inline Counter component")]
    public RazorSyntaxTree WellFormed_InlineComponent()
        => RazorSyntaxTree.Parse(_inlineComponentDoc, _componentOptions);

    [Benchmark(Description = "Ill-formed: empty @onclick attribute")]
    public RazorSyntaxTree IllFormed_EmptyEventHandler()
        => RazorSyntaxTree.Parse(_illFormedEmptyHandlerDoc, _componentOptions);

    [Benchmark(Description = "Ill-formed: unclosed @code block")]
    public RazorSyntaxTree IllFormed_UnclosedCodeBlock()
        => RazorSyntaxTree.Parse(_illFormedUnclosedCodeBlockDoc, _componentOptions);

    [Benchmark(Description = "Ill-formed: truncated attribute")]
    public RazorSyntaxTree IllFormed_TruncatedAttribute()
        => RazorSyntaxTree.Parse(_illFormedTruncatedAttributeDoc, _componentOptions);
}
