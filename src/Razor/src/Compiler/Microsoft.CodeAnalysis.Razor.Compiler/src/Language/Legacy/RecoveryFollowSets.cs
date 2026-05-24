// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.AspNetCore.Razor.Language.Legacy;

/// <summary>
/// Named <see cref="FollowSet"/> constants used by the parser error-recovery
/// machinery. See <c>docs/plans/ErrorRecovery/razor-recovery-redesign-plan.md</c>
/// (Stage 4.1) for the catalogue of follow sets each call-site is meant to
/// thread.
/// </summary>
/// <remarks>
/// Stage 1.1 seeds this file with only the universal entries that the
/// machinery itself depends on (notably <see cref="Empty"/>). Subsequent
/// stages populate the language-specific sets (HTML attribute terminators,
/// C# statement terminators, etc.) and the cross-language translation
/// helpers (<c>ForCSharpCallee</c> / <c>ForHtmlCallee</c>) as the parsers
/// are migrated to call <see cref="TokenizerBackedParser{TTokenizer}.Synchronize"/>.
/// </remarks>
internal static class RecoveryFollowSets
{
    /// <summary>An empty follow set. Identical to <see cref="FollowSet.Empty"/>.</summary>
    public static readonly FollowSet Empty = FollowSet.Empty;
}
