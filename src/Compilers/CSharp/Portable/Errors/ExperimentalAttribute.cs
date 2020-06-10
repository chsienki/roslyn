// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
using System;
using System.Collections.Generic;
using System.Text;

#nullable enable

/// <summary>
/// PROTOTYPE: add explaination
/// </summary>

namespace Windows.Foundation.Metadata
{

    [AttributeUsage(AttributeTargets.All, AllowMultiple = false, Inherited = true)]
    internal sealed class ExperimentalAttribute : Attribute
    {
    }
}
