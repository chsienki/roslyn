using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Classification.Classifiers;
using Microsoft.CodeAnalysis.EmbeddedLanguages.LanguageServices;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.EmbeddedLanguages.CodeLiteral
{
    internal class CodeLiteralEmbeddedLanguageClassifier : AbstractSyntaxClassifier
    {
        private readonly EmbeddedLanguageInfo _info;

        public CodeLiteralEmbeddedLanguageClassifier(EmbeddedLanguageInfo info)
        {
            _info = info;
        }

        public override void AddClassifications(Workspace workspace, SyntaxToken syntax, SemanticModel semanticModel, ArrayBuilder<ClassifiedSpan> result, CancellationToken cancellationToken)
        {
            // TODO: determine if this is a span or not
            if (_info.StringLiteralTokenKind != syntax.RawKind)
            {
                return;
            }

            var c = Classifier.GetClassifiedSpans(semanticModel, syntax.Span, workspace, cancellationToken);
            result.AddRange(c);

            base.AddClassifications(workspace, syntax, semanticModel, result, cancellationToken);
        }
    }
}
