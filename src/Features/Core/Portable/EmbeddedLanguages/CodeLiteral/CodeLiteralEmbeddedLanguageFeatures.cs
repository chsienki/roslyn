using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis.Classification.Classifiers;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.DocumentHighlighting;
using Microsoft.CodeAnalysis.EmbeddedLanguages.LanguageServices;
using Microsoft.CodeAnalysis.Features.EmbeddedLanguages;

namespace Microsoft.CodeAnalysis.EmbeddedLanguages.CodeLiteral
{
    internal class CodeLiteralEmbeddedLanguageFeatures : IEmbeddedLanguageFeatures
    {
        public ISyntaxClassifier Classifier { get; }
        public IDocumentHighlightsService DocumentHighlightsService { get; }
        public CompletionProvider CompletionProvider { get; }

        public EmbeddedLanguageInfo Info { get; }

        public CodeLiteralEmbeddedLanguageFeatures(EmbeddedLanguageInfo info)
        {
            Info = info;
            Classifier = new CodeLiteralEmbeddedLanguageClassifier(info);
        }
    }
}
