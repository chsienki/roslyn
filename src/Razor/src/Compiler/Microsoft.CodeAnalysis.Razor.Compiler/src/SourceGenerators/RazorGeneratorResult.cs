// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.AspNetCore.Razor.Language;
using System.Collections.Immutable;

namespace Microsoft.NET.Sdk.Razor.SourceGenerators
{
    internal sealed class RazorGeneratorResult(TagHelperCollection tagHelpers, ImmutableDictionary<string, (string hintName, RazorCodeDocument document)> filePathToDocument, ImmutableDictionary<string, string> hintNameToFilePath)
    {
        public TagHelperCollection TagHelpers => tagHelpers;

        public RazorCodeDocument? GetCodeDocument(string physicalPath) => filePathToDocument.TryGetValue(physicalPath, out var pair) ? pair.document : null;

        public string? GetHintName(string physicalPath) => filePathToDocument.TryGetValue(physicalPath, out var pair) ? pair.hintName : null;

        public string? GetFilePath(string hintName) => hintNameToFilePath.TryGetValue(hintName, out var filePath) ? filePath : null;

        /// <summary>
        /// Returns all generated-source hint names that map back to <paramref name="physicalPath"/>.
        /// For splittable component documents this includes both the impl hint (e.g.
        /// <c>Foo_razor.g.cs</c>) and the decl hint (<c>Foo_razor.decl.g.cs</c>); for non-splittable
        /// documents (cshtml, suppressed primary method body) only the impl hint is returned.
        /// Returned in stable order with impl first.
        /// </summary>
        public ImmutableArray<string> GetAllHintNames(string physicalPath)
        {
            if (!filePathToDocument.TryGetValue(physicalPath, out var pair))
            {
                return ImmutableArray<string>.Empty;
            }

            var implHint = pair.hintName;
            var declHint = Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator.GetDeclIdentifierFromHintName(implHint);

            return hintNameToFilePath.ContainsKey(declHint)
                ? ImmutableArray.Create(implHint, declHint)
                : ImmutableArray.Create(implHint);
        }
    }
}
