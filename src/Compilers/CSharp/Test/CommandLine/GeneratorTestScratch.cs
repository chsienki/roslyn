using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.CommandLine.UnitTests
{

    public class GeneratorTestScratch
         : CSharpTestBase
    {
        [Fact]
        public void Scratch()
        {
            var code = @"
//using System;
//namespace UserNS
//{
//    public partial class UserClass
//    {
//        [NotifyPropertyChanged]
//        private bool _boolProp;
        
//        [NotifyPropertyChanged(propertyName: ""Count"")]
//        private int _intProp;
//    }
//}
";

            var additionalText = @"
<Settings name=""Settings1"">
</Settings>
";

            var additionalText2 = @"
<Settings name=""Settings2"">
</Settings>
";


            var options = TestOptions.DebugDll;
            Compilation compilation = CreateCompilationWithMscorlib45(code, options: options);
            Assert.Single(compilation.SyntaxTrees);


            GeneratorDriver driver = new CSharpGeneratorDriver(compilation, compilation.SyntaxTrees.First().Options);

            ArrayBuilder<GeneratorProvider> providers = new ArrayBuilder<GeneratorProvider>();
            providers.Add(new SingletonGeneratorProvider(new SettingsGenerator()));
            driver = driver.WithGeneratorProviders(providers.ToImmutableAndFree());

            var additionalTexts = new ArrayBuilder<AdditionalText>(1);
            additionalTexts.Add(new InMemoryAdditionalText("mysetting.xmlsettings", additionalText));
            driver = driver.WithAdditionalTexts(additionalTexts.ToImmutableAndFree());

            driver = driver.GenerateSource(compilation, out var compilationPrime);

            Assert.Equal(2, compilationPrime.SyntaxTrees.Count());

            var additionalEdits = new ArrayBuilder<PendingEdit>();
            additionalEdits.Add(new AdditionalFileEdit.AddtionalFileAddedEdit(new InMemoryAdditionalText("mysetting2.xmlsettings", additionalText2)));
            driver = driver.WithPendingEdits(additionalEdits.ToImmutableAndFree());

            driver = driver.TryApplyEdits(compilation, out compilationPrime, out bool success);
            Assert.True(success);
            Assert.Equal(3, compilationPrime.SyntaxTrees.Count());

            // re-run a full compilation
            driver = driver.GenerateSource(compilation, out compilationPrime);
            Assert.Equal(3, compilationPrime.SyntaxTrees.Count());

            compilationPrime.VerifyDiagnostics();
        }


        [Fact]
        public void Running_With_No_Changes_Is_NoOp()
        {
            var source = @"
class C { }
";

            var parseOptions = TestOptions.Regular;
            Compilation compilation = CreateCompilationWithMscorlib45(source, options: TestOptions.DebugDll, parseOptions: parseOptions);
            compilation.VerifyDiagnostics();

            Assert.Single(compilation.SyntaxTrees);

            GeneratorDriver driver = new CSharpGeneratorDriver(compilation, parseOptions);
            driver.GenerateSource(compilation, out var compilationPrime);

            Assert.Single(compilationPrime.SyntaxTrees);
            Assert.Equal(compilation, compilationPrime);

        }
    }

    internal class DummyGenerator : ISourceGenerator
    {
        public void Execute(SourceGeneratorContext context)
        {
            context.AdditionalSources.Add("fakeFile.cs", SourceText.From("namespace GeneratedNS { public class GeneratedClass { public bool Test {get; set;} } }"));
        }
    }

    internal class SettingsGenerator : ISourceGenerator, ITriggeredByAdditionalFileGenerator
    {

        public void Execute(SourceGeneratorContext context)
        {
            // find anything that matches our settings files
            var settingsFiles = context.AnalyzerOptions.AdditionalFiles.WhereAsArray(at => at.Path.EndsWith(".xmlsettings"));
            foreach (var file in settingsFiles)
            {
                GenerateForFile(file, context.AdditionalSources, context.CancellationToken);
            }
        }

        public UpdateContext UpdateContext(UpdateContext context, AdditionalFileEdit edit)
        {
            if (edit is AdditionalFileEdit.AddtionalFileAddedEdit added)
            {
                GenerateForFile(added.AddedText, context.AdditionalSources, context.CancellationToken);
            }
            return context;
        }

        private void GenerateForFile(AdditionalText file, AdditionalSourcesCollection sources, CancellationToken cancellationToken = default)
        {
            var content = file.GetText(cancellationToken);

            XmlDocument xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(content.ToString());

            // do some transforms based on the xml
            var className = xmlDoc.DocumentElement.GetAttribute("name");

            string output = "namespace AutoSettings { public class " + className + "Settings { } }";
            var sourceText = SourceText.From(output);

            sources.Add(className + ".generated.cs", sourceText);
        }
    }

    internal class SingletonGeneratorProvider : GeneratorProvider
    {
        private readonly ISourceGenerator _instance;

        public SingletonGeneratorProvider(ISourceGenerator instance)
        {
            _instance = instance;
        }

        public override ISourceGenerator GetGenerator() => _instance;
    }

    internal class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _content;

        public InMemoryAdditionalText(string path, string content)
        {
            Path = path;
            _content = SourceText.From(content);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _content;

    }
}
