
// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis.CSharp;

namespace Microsoft.NET.Sdk.Razor.SourceGenerators
{
    internal sealed record RazorSourceGenerationOptions
    {
        public string RootNamespace { get; set; } = "ASP";

        public RazorConfiguration Configuration { get; set; } = RazorConfiguration.Default;

        /// <summary>
        /// Gets a flag that determines if generated Razor views and Pages includes the <c>RazorSourceChecksumAttribute</c>.
        /// </summary>
        public bool GenerateMetadataSourceChecksumAttributes { get; set; } = false;

        internal CSharpParseOptions CSharpParseOptions { get; set; } = new CSharpParseOptions(LanguageVersion.CSharp10);

        /// <summary>
        /// Gets a flag that determines if localized component names should be supported.
        /// </summary>
        public bool SupportLocalizedComponentNames { get; set; } = false;

        /// <summary>
        /// Gets the flag that should be set on code documents to replace unique ids for testing purposes
        /// </summary>
        internal string? TestSuppressUniqueIds { get; set; }

        internal bool UseRoslynTokenizer { get; set; } = true;

        /// <summary>
        /// Surfaces the Razor parser's <c>UseEnhancedRecovery</c> flag through the
        /// source generator. Stage 5.0 introduces this so test harnesses (and the
        /// Stage 5.1 e2e tests) can exercise enhanced-mode codegen end-to-end.
        /// Default <see langword="false"/> matches the parser flag default; will
        /// flip to <see langword="true"/> in Stage 6.1 and be removed in Stage 6.2.
        /// </summary>
        internal bool UseEnhancedRecovery { get; set; } = false;

        public override int GetHashCode() => Configuration.GetHashCode();
    }
}
