// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.AspNetCore.Razor.Language.Syntax.InternalSyntax;

namespace Microsoft.AspNetCore.Razor.Language.Legacy;

/// <summary>
/// Result of a <see cref="TokenizerBackedParser{TTokenizer}.Synchronize(FollowSet, FollowSet, SyntaxKind, SyncOptions)"/>
/// call.
/// </summary>
/// <param name="Skipped">
/// The <see cref="SkippedContentSyntax"/> node holding the skipped tokens, or
/// <c>null</c> if no tokens were skipped (the synchronization point was
/// already current, or end-of-file was reached immediately).
/// </param>
/// <param name="StopReason">Why synchronization stopped.</param>
internal readonly record struct SyncResult(
    SkippedContentSyntax? Skipped,
    SyncStopReason StopReason);

/// <summary>
/// Why <see cref="TokenizerBackedParser{TTokenizer}.Synchronize(FollowSet, FollowSet, SyntaxKind, SyncOptions)"/>
/// stopped advancing.
/// </summary>
internal enum SyncStopReason : byte
{
    /// <summary>Hit a token in the local follow set.</summary>
    AtFollowToken,

    /// <summary>
    /// Hit a token in the outer / caller follow set. Caller should consider
    /// bailing back to the outer parser rather than continuing inner work.
    /// </summary>
    AtOuterFollowToken,

    /// <summary>Hit a newline; only fires when <see cref="SyncOptions.StopAtNewLine"/> is set.</summary>
    AtNewLine,

    /// <summary>Hit a language-transition token (<c>@</c>); only fires when <see cref="SyncOptions.StopAtTransition"/> is set.</summary>
    AtTransition,

    /// <summary>Reached end-of-file before any other stop condition.</summary>
    EndOfFile,
}

/// <summary>
/// Optional stop conditions for
/// <see cref="TokenizerBackedParser{TTokenizer}.Synchronize(FollowSet, FollowSet, SyntaxKind, SyncOptions)"/>.
/// </summary>
[Flags]
internal enum SyncOptions : byte
{
    /// <summary>No extra stop conditions beyond the follow sets and EOF.</summary>
    None = 0,

    /// <summary>Stop when the current token is a <see cref="SyntaxKind.NewLine"/>.</summary>
    StopAtNewLine = 1 << 0,

    /// <summary>Stop when the current token is a language transition (<see cref="SyntaxKind.Transition"/>, i.e. <c>@</c>).</summary>
    StopAtTransition = 1 << 1,
}
