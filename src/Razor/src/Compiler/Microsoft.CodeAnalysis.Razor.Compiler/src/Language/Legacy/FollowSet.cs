// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;

namespace Microsoft.AspNetCore.Razor.Language.Legacy;

/// <summary>
/// A small bitmap-backed set of <see cref="SyntaxKind"/> values used to drive
/// parser error-recovery synchronization. See
/// <c>docs/plans/ErrorRecovery/razor-recovery-redesign-plan.md</c>
/// (Big Design Decision #4) for the design rationale.
/// </summary>
/// <remarks>
/// Backed by two <see cref="ulong"/>s, so the structure can represent any
/// <see cref="SyntaxKind"/> whose underlying byte value is in the range
/// <c>[0, 127]</c>. The current set of kinds (<see cref="SyntaxKind"/> is a
/// <c>byte</c>-backed enum with <see cref="SyntaxKind.FirstAvailableTokenKind"/>
/// well below 128) fits comfortably; a debug assertion is checked whenever a
/// kind is added or tested to catch a future kind that crosses the 127
/// boundary.
///
/// Follow sets are language-scoped: an HTML-side set must hold HTML token
/// kinds, a C#-side set must hold C# token kinds. Translation between the two
/// happens at cross-parser handoff sites via <c>RecoveryFollowSets</c>
/// helpers (Stage 4.1).
/// </remarks>
internal readonly struct FollowSet : IEquatable<FollowSet>
{
    public const int MaxSupportedKindValue = 127;

    private readonly ulong _low;
    private readonly ulong _high;

    /// <summary>The empty follow set. Matches nothing.</summary>
    public static readonly FollowSet Empty = default;

    private FollowSet(ulong low, ulong high)
    {
        _low = low;
        _high = high;
    }

    public FollowSet(SyntaxKind kind)
    {
        AssertInRange(kind);
        var value = (int)kind;
        if (value < 64)
        {
            _low = 1UL << value;
            _high = 0UL;
        }
        else
        {
            _low = 0UL;
            _high = 1UL << (value - 64);
        }
    }

    public FollowSet(params SyntaxKind[] kinds)
    {
        ulong low = 0;
        ulong high = 0;
        if (kinds is not null)
        {
            foreach (var kind in kinds)
            {
                AssertInRange(kind);
                var value = (int)kind;
                if (value < 64)
                {
                    low |= 1UL << value;
                }
                else
                {
                    high |= 1UL << (value - 64);
                }
            }
        }

        _low = low;
        _high = high;
    }

    /// <summary>Returns <c>true</c> if this set contains <paramref name="kind"/>.</summary>
    public bool Contains(SyntaxKind kind)
    {
        AssertInRange(kind);
        var value = (int)kind;
        return value < 64
            ? (_low & (1UL << value)) != 0
            : (_high & (1UL << (value - 64))) != 0;
    }

    /// <summary>Returns <c>true</c> if this set contains no kinds.</summary>
    public bool IsEmpty => _low == 0 && _high == 0;

    /// <summary>Returns a new set that is the union of this set and <paramref name="other"/>.</summary>
    public FollowSet Union(FollowSet other) => new(_low | other._low, _high | other._high);

    public static FollowSet operator |(FollowSet a, FollowSet b) => a.Union(b);

    public bool Equals(FollowSet other) => _low == other._low && _high == other._high;

    public override bool Equals(object? obj) => obj is FollowSet other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return (int)(_low ^ (_low >> 32) ^ _high ^ (_high >> 32));
        }
    }

    public static bool operator ==(FollowSet a, FollowSet b) => a.Equals(b);

    public static bool operator !=(FollowSet a, FollowSet b) => !a.Equals(b);

    [Conditional("DEBUG")]
    private static void AssertInRange(SyntaxKind kind)
    {
        Debug.Assert(
            (int)kind <= MaxSupportedKindValue,
            $"FollowSet only supports SyntaxKind values <= {MaxSupportedKindValue}; got {kind} ({(int)kind}). " +
            "Extend the bitmap layout (add another ulong) if the enum has grown past this limit.");
    }
}
