// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable
using System.Threading;

namespace Microsoft.CodeAnalysis
{
    internal sealed class GeneratorSyntaxWalker : SyntaxWalker
    {
        private readonly ISyntaxReceiver _syntaxReceiver;

        private readonly CancellationToken _cancellationToken;

        internal GeneratorSyntaxWalker(ISyntaxReceiver syntaxReceiver, CancellationToken cancellationToken = default)
        {
            _syntaxReceiver = syntaxReceiver;
            _cancellationToken = cancellationToken;
        }

        public override void Visit(SyntaxNode node)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _syntaxReceiver.OnVisitSyntaxNode(node);
            base.Visit(node);
        }
    }
}
