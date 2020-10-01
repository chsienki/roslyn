// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CodeAnalysis
{
    internal readonly struct GeneratorInfo
    {
        internal EditCallback<AdditionalFileEdit>? EditCallback { get; }

        internal SyntaxReceiverCreator? SyntaxReceiverCreator { get; }

        internal bool Initialized { get; }

        internal GeneratorInfo(EditCallback<AdditionalFileEdit>? editCallback, SyntaxReceiverCreatorWithContext? receiverCreator)
        {
            EditCallback = editCallback;
            SyntaxReceiverCreator = receiverCreator;
            Initialized = true;
        }

        internal class Builder
        {
            internal EditCallback<AdditionalFileEdit>? EditCallback { get; set; }

            internal SyntaxReceiverCreatorWithContext? SyntaxReceiverCreator { get; set; }

            public GeneratorInfo ToImmutable() => new GeneratorInfo(EditCallback, SyntaxReceiverCreator);
        }
    }
}
