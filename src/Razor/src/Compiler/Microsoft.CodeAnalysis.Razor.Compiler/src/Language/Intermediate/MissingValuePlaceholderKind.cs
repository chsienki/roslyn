// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.AspNetCore.Razor.Language.Intermediate;

/// <summary>
/// The surrounding generated C# context for a missing-value substitution.
/// Codegen chooses the placeholder text using this kind.
/// </summary>
internal enum MissingValuePlaceholderKind
{
    /// <summary>
    /// Inner expression slot of <c>EventCallback.Factory.Create&lt;T&gt;(this, ...)</c>.
    /// Substitutes <c>default(global::System.Action&lt;T&gt;)</c>, which binds to
    /// the <c>Create&lt;TValue&gt;(object, Action&lt;TValue&gt;)</c> overload.
    /// </summary>
    EventCallbackTyped,

    /// <summary>
    /// Inner expression slot of an untyped <c>EventCallback.Factory.Create(this, ...)</c>.
    /// Substitutes <c>default(global::System.Action)</c>.
    /// </summary>
    EventCallbackUntyped,

    /// <summary>
    /// A bound attribute value where the target type is fully known.
    /// Substitutes <c>default(&lt;fullyQualifiedType&gt;)</c>.
    /// </summary>
    BoundAttributeTyped,

    /// <summary>
    /// A bound attribute value where the target type is generic / unresolved
    /// at codegen time. Substitutes <c>default!</c>.
    /// </summary>
    BoundAttributeUnknown,

    /// <summary>
    /// An <c>@expr</c> in markup output context (e.g. argument to <c>Write(...)</c>
    /// or <c>AddContent(...)</c>). Substitutes <c>""</c>.
    /// </summary>
    MarkupExpression,

    /// <summary>
    /// A C# expression in statement context (e.g. inside <c>@{ }</c>).
    /// Substitutes <c>_ = (object?)null</c> (the statement context supplies <c>;</c>).
    /// </summary>
    StatementContext,
}
