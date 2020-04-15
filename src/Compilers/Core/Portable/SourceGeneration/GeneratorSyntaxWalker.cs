// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;

#nullable enable
namespace Microsoft.CodeAnalysis
{
    internal sealed class GeneratorSyntaxWalker : SyntaxWalker
    {
        private ImmutableArray<ISyntaxReceiver> _syntaxReceivers;

        private ImmutableArray<(ISyntaxReceiver, Exception)> _failedReceivers;

        internal ImmutableArray<(ISyntaxReceiver receiver, Exception ex)> FailedReceivers { get => _failedReceivers; }

        public GeneratorSyntaxWalker(ImmutableArray<ISyntaxReceiver> syntaxReceivers)
        {
            _syntaxReceivers = syntaxReceivers;
        }


        public override void Visit(SyntaxNode node)
        {
            foreach (var receiver in _syntaxReceivers)
            {
                try
                {
                    receiver.OnVisitSyntaxNode(node);
                }
                catch (Exception e)
                {
                    _failedReceivers = _failedReceivers.Add((receiver, e));
                    _syntaxReceivers = _syntaxReceivers.Remove(receiver);
                }
            }
            base.Visit(node);
        }
    }

}
