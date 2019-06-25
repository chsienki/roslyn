// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Test.Utilities;
using Xunit;
using System.Collections.Generic;
using System;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests.Symbols
{
    public class EqualityTests : CSharpTestBase
    {
        [Fact]
        public void Test1()
        {
            var source =
@"
#nullable enable
public class A
{
    public static A field1;
    public static A? field2;
}

";
            var comp = CreateCompilation(source);
            comp.VerifyDiagnostics();

            var member = comp.GetMember("A.field1");
            var member2 = comp.GetMember("A.field2");

            var equal = member.Equals(member2, NullabilityIgnoringComparer.Instance);
            Assert.False(equal);

            var field1 = member as FieldSymbol;
            var field2 = member2 as FieldSymbol;

            equal = field1.Equals(field2, NullabilityIgnoringComparer.Instance);
            Assert.False(equal);

            equal = field1.Equals(field1, NullabilityIgnoringComparer.Instance);
            Assert.True(equal);

            equal = field1.Type.Equals(field2.Type, NullabilityIgnoringComparer.Instance);
            Assert.True(equal);

        }
    }
}
