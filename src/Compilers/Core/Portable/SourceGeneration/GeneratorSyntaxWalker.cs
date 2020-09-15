// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
using System;
using Roslyn.Utilities;

#nullable enable
namespace Microsoft.CodeAnalysis
{
    internal sealed class GeneratorSyntaxWalker : SyntaxWalker
    {
        private readonly ISyntaxReceiver _syntaxReceiver;

        internal TimeSpan ElapsedTime { get; } = TimeSpan.Zero;

        internal GeneratorSyntaxWalker(ISyntaxReceiver syntaxReceiver)
        {
            _syntaxReceiver = syntaxReceiver;
        }

        public override void Visit(SyntaxNode node)
        {
            var timer = SharedStopwatch.StartNew();
            _syntaxReceiver.OnVisitSyntaxNode(node);
            ElapsedTime.Add(timer.Elapsed);
            base.Visit(node);
        }
    }
}
