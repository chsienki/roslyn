// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;

namespace Roslyn.Utilities
{
    internal readonly struct SharedStopwatch
    {
        private static readonly Stopwatch s_stopwatch = Stopwatch.StartNew();

        private readonly TimeSpan _started;

        private SharedStopwatch(TimeSpan started)
        {
            _started = started;
        }

        public TimeSpan Elapsed => s_stopwatch.Elapsed - _started;

        public static SharedStopwatch StartNew()
        {
            // The duplicate call isn't required by the API, but is included to avoid measurement errors
            // which can occur during periods of high allocation activity. In some cases, calls to Stopwatch
            // operations can block at their return point on the completion of a background GC operation. When
            // this occurs, the GC wait time ends up included in the measured time span. In the event the first
            // call to Elapsed created a stopwatch and blocked on a GC operation, the second call to Elapsed will 
            // most likely occur when the GC is no longer active. In practice, a substantial improvement to the 
            // consistency of timing data was observed.
            _ = s_stopwatch.Elapsed;
            return new SharedStopwatch(s_stopwatch.Elapsed);
        }
    }
}
