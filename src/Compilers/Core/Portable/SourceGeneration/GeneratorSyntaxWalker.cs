using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

namespace Microsoft.CodeAnalysis.SourceGeneration
{
    internal class GeneratorSyntaxWalker : SyntaxWalker
    {
        private readonly ImmutableArray<ISyntaxAwareGenerator> _generators;

        public GeneratorSyntaxWalker(ImmutableArray<ISyntaxAwareGenerator> generators)
        {
            _generators = generators;
        }

        public override void Visit(SyntaxNode node)
        {
            foreach (var generator in _generators)
            {
                generator.VisitSyntaxNode(node);
            }

            base.Visit(node);
        }
    }
}
