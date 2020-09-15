// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.CodeAnalysis
{
    internal struct GeneratorTiming
    {
        internal TimeSpan Initialization { get; }

        internal TimeSpan SyntaxWalk { get; }

        internal TimeSpan Execution { get; }

        internal GeneratorTiming(TimeSpan initTime)
            : this(initTime, syntaxTime: TimeSpan.Zero, execTime: TimeSpan.Zero) { }

        private GeneratorTiming(TimeSpan initTime, TimeSpan syntaxTime, TimeSpan execTime)
        {
            this.Initialization = initTime;
            this.SyntaxWalk = syntaxTime;
            this.Execution = execTime;
        }

        internal GeneratorTiming With(TimeSpan? syntaxWalkTime = null, TimeSpan? executionTime = null)
        {
            return new GeneratorTiming(this.Initialization,
                                           syntaxWalkTime ?? this.SyntaxWalk,
                                           executionTime ?? this.Execution);
        }
    }
}
