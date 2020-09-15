// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CSharp;
using Roslyn.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.UnitTests.InternalUtilities
{
    public class IsGeneratedCodeTests
    {
        [Fact]
        public void EmptyFilePathIsConsideredGenerated()
        {
            var isGenerated = GeneratedCodeUtilities.IsGeneratedCode("", CSharpSyntaxTree.Dummy.GetRoot(), (t) => false);
            Assert.True(isGenerated);
        }
    }
}
