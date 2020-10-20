// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Threading;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis
{
    public abstract class SyntaxTreeOptionsProvider
    {
        /// <summary>
        /// Get whether the given tree is generated.
        /// </summary>
        public abstract GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken);

        /// <summary>
        /// Get diagnostic severity setting for a given diagnostic identifier in a given tree.
        /// </summary>
        public abstract bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity);

        /// <summary>
        /// Get diagnostic severity set globally for a given diagnostic identifier
        /// </summary>
        public abstract bool TryGetGlobalDiagnosticValue(string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity);
    }

    internal sealed class CompilerSyntaxTreeOptionsProvider : SyntaxTreeOptionsProvider
    {
        private readonly struct Options
        {
            public readonly GeneratedKind IsGenerated;
            public readonly ImmutableDictionary<string, ReportDiagnostic> DiagnosticOptions;

            public Options(AnalyzerConfigOptionsResult? result)
            {
                if (result is AnalyzerConfigOptionsResult r)
                {
                    DiagnosticOptions = r.TreeOptions;
                    IsGenerated = GeneratedCodeUtilities.GetIsGeneratedCodeFromOptions(r.AnalyzerOptions);
                }
                else
                {
                    DiagnosticOptions = SyntaxTree.EmptyDiagnosticOptions;
                    IsGenerated = GeneratedKind.Unknown;
                }
            }
        }

        private readonly ImmutableDictionary<SyntaxTree, Options> _options;

        private readonly AnalyzerConfigOptionsResult _globalOptions;

        private CompilerSyntaxTreeOptionsProvider(
            ImmutableDictionary<SyntaxTree, Options> options,
            AnalyzerConfigOptionsResult globalOptions)
        {
            _options = options;
            _globalOptions = globalOptions;
        }

        public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken _)
            => _options.TryGetValue(tree, out var value) ? value.IsGenerated : GeneratedKind.Unknown;

        public override bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId, CancellationToken _, out ReportDiagnostic severity)
        {
            if (_options.TryGetValue(tree, out var value))
            {
                return value.DiagnosticOptions.TryGetValue(diagnosticId, out severity);
            }
            severity = ReportDiagnostic.Default;
            return false;
        }

        public override bool TryGetGlobalDiagnosticValue(string diagnosticId, CancellationToken _, out ReportDiagnostic severity)
        {
            if (_globalOptions.TreeOptions is object)
            {
                return _globalOptions.TreeOptions.TryGetValue(diagnosticId, out severity);
            }
            severity = ReportDiagnostic.Default;
            return false;
        }

        public class Builder
        {
            private readonly ImmutableDictionary<SyntaxTree, Options>.Builder _optionsBuilder;

            private readonly AnalyzerConfigOptionsResult _globalOptions;

            public Builder(AnalyzerConfigOptionsResult globalOptions)
            {
                _optionsBuilder = ImmutableDictionary.CreateBuilder<SyntaxTree, Options>();
                _globalOptions = globalOptions;
            }

            public void AddResult(SyntaxTree tree, AnalyzerConfigOptionsResult result) => _optionsBuilder.Add(tree, new Options(result));

            public CompilerSyntaxTreeOptionsProvider ToImmutable() => new CompilerSyntaxTreeOptionsProvider(_optionsBuilder.ToImmutable(), _globalOptions);
        }
    }
}
