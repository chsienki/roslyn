using System;
using System.Collections.Generic;
using System.Text;

#nullable enable
namespace Microsoft.CodeAnalysis.SourceGeneration
{
    internal readonly struct GeneratorInfo
    {
        internal UpdateCallback<AdditionalFileEdit>? EditCallback { get; }

        internal GeneratorInfo(UpdateCallback<AdditionalFileEdit>? editCallback)
        {
            EditCallback = editCallback;
        }
    }
}
