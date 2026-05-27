// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace Microsoft.CodeAnalysis.Razor.Compiler.CSharp;

internal static class ParseOptionsExtensions
{
    public static bool UseRoslynTokenizer(this ParseOptions parseOptions)
        => parseOptions.Features.TryGetValue("use-roslyn-tokenizer", out var useRoslynTokenizerValue) &&
           string.Equals(useRoslynTokenizerValue, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the <c>use-enhanced-recovery</c> feature flag from <paramref name="parseOptions"/>.
    /// Stage 5.0 surfaces this through the source generator so test harnesses can
    /// flip the Razor parser's <c>UseEnhancedRecovery</c> flag end-to-end. Mirrors
    /// the <c>use-roslyn-tokenizer</c> convention. Default is <see langword="false"/>.
    /// </summary>
    public static bool UseEnhancedRecovery(this ParseOptions parseOptions)
        => parseOptions.Features.TryGetValue("use-enhanced-recovery", out var useEnhancedRecoveryValue) &&
           string.Equals(useEnhancedRecoveryValue, "true", StringComparison.OrdinalIgnoreCase);
}
