using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.SourceGeneration;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.Semantic.UnitTests.SourceGeneration
{
    public class SyntaxAwareGeneratorTests
                 : CSharpTestBase
    {
        [Fact]
        public void Scratch()
        {

            var source = @"
class C 
{
    void M()
    {
        int x = 8;  
        string y = ""5"";
        y += ""6"";
        if (x > 5)
        {
            bool z = false;
            z = (y == ""56"") ? true : false;
            y = (z) ? ""true"" : ""false"";
        }
    }
}
";
            var parseOptions = TestOptions.Regular;
            Compilation compilation = CreateCompilationWithMscorlib45(source, options: TestOptions.DebugDll, parseOptions: parseOptions);
            compilation.VerifyDiagnostics();

            Assert.Single(compilation.SyntaxTrees);

            GeneratorDriver driver = new CSharpGeneratorDriver(compilation, parseOptions).WithGenerators(ImmutableArray.Create<ISourceGenerator>(new SyntaxAwareGenerator()));
            driver.RunFullGeneration(compilation, out var outputCompilation);
            driver.RunFullGeneration(compilation, out outputCompilation);


            Assert.Single(outputCompilation.SyntaxTrees);
            Assert.Equal(compilation, outputCompilation);



        }



        class SyntaxAwareGenerator : ISyntaxAwareGenerator
        {
            Dictionary<SyntaxKind, Action<SyntaxNode>> _actions = new Dictionary<SyntaxKind, Action<SyntaxNode>>();

            List<MethodDeclarationSyntax> methods = new List<MethodDeclarationSyntax>();

            public SyntaxAwareGenerator()
            {
                _actions.Add(SyntaxKind.MethodDeclaration, OnMemberDeclSyntax);
            }



            private void OnMemberDeclSyntax(SyntaxNode syntax)
            {
                if (syntax is MethodDeclarationSyntax methodDeclSyntax)
                {
                    methods.Add(methodDeclSyntax);
                }
            }

            public void Execute(SourceGeneratorContext context)
            {
                //
            }

            public void VisitSyntaxNode(SyntaxNode node)
            {
                var kind = node.Kind();
                if (_actions.ContainsKey(kind))
                {
                    _actions[kind](node);
                }
            }
        }
    }
}
