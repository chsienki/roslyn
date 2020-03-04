using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

namespace Microsoft.CodeAnalysis.SourceGeneration
{
    internal readonly struct GeneratorState
    {
        internal ISourceGenerator Generator { get; }

        internal ImmutableArray<GeneratedSourceText> Sources { get; }

        internal GeneratorInfo Info { get; } //PROTOTYPE: should this just be properties of the state? State implies something that can change though?

        internal bool IsInitialized { get; }
    }
}
