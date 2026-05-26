// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Razor.Language.IntegrationTests;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.AspNetCore.Razor.Language.Test;

public class FindTokenIntegrationTest() : IntegrationTestBase(layer: TestProject.Layer.Compiler)
{
    [Fact, WorkItem("https://github.com/dotnet/razor/issues/9177")]
    public void EmptyDirective()
    {
        var projectEngine = CreateProjectEngine();
        var projectItem = CreateProjectItemFromFile();

        var codeDocument = projectEngine.Process(projectItem);

        var root = codeDocument.GetRequiredSyntaxTree().Root;
        var token = root.FindToken(27);
        // Position 27 is the start of the '\r\n' following an '@inherits'
        // directive that is missing its type-name argument. FindToken
        // skips the zero-width missing identifier (Roslyn-style) and the
        // whitespace walk-back returns the preceding 'inherits' keyword.
        AssertEx.Equal("Identifier;[inherits];", TestSyntaxSerializer.Serialize(token).Trim());
    }
}
