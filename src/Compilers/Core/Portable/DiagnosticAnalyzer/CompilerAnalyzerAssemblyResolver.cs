// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

#if NETCOREAPP
using System.Runtime.Loader;
#endif

namespace Microsoft.CodeAnalysis
{
    internal class CompilerAnalyzerAssemblyResolver : IAnalyzerAssemblyResolver
    {
#if NETCOREAPP
        private readonly AssemblyLoadContext _compilerAlc;

        public CompilerAnalyzerAssemblyResolver(AssemblyLoadContext? compilerContext = null)
        {
            _compilerAlc = compilerContext ?? AssemblyLoadContext.GetLoadContext(typeof(AnalyzerAssemblyLoader).GetTypeInfo().Assembly)!;
        }

        public Assembly? ResolveAssembly(AssemblyName assemblyName) => _compilerAlc.LoadFromAssemblyName(assemblyName);

#else
        public Assembly? ResolveAssembly(AssemblyName assemblyName) => null; // on .NET FX we never change resolution?
#endif
    }

}
