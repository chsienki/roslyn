﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Newtonsoft.Json;

namespace BuildValidator
{
    /// <summary>
    /// Build Validator enumerates the output of the Roslyn build, extracts the compilation options
    /// from the PE and attempts to rebuild the source using that information. It then checks
    /// that the new build output is the same as the original build
    /// </summary>
    class Program
    {
        const int ExitSuccess = 0;
        const int ExitFailure = 1;

        private static readonly Regex[] s_ignorePatterns = new Regex[]
        {
            new Regex(@"\\runtimes?\\"),
            new Regex(@"\\ref\\"),
            new Regex(@"\.resources?\.")
        };

        static int Main(string[] args)
        {
            var rootCommand = new RootCommand
            {
                new Option<string>(
                    "--assembliesPath", "Path to assemblies to rebuild (can be specified one or more times)"
                ) { IsRequired = true, Argument = { Arity = ArgumentArity.OneOrMore } },
                new Option<string>(
                    "--sourcePath", "Path to sources to use in rebuild"
                ) { IsRequired = true },
                new Option<string>(
                    "--referencesPath", "Path to referenced assemblies (can be specified zero or more times)"
                ) { Argument = { Arity = ArgumentArity.ZeroOrMore } },
                new Option<bool>(
                    "--verbose", "Output verbose log information"
                ),
                new Option<bool>(
                    "--quiet", "Do not output log information to console"
                ),
                new Option<bool>(
                    "--debug", "Output debug info when rebuild is not equal to the original"
                ),
                new Option<string?>(
                    "--debugPath", "Path to output debug info. Defaults to the user temp directory. Note that a unique debug path should be specified for every instance of the tool running with `--debug` enabled."
                )
            };
            rootCommand.Handler = CommandHandler.Create<string[], string, string[]?, bool, bool, bool, string>(HandleCommand);
            return rootCommand.Invoke(args);
        }

        static int HandleCommand(string[] assembliesPath, string sourcePath, string[]? referencesPath, bool verbose, bool quiet, bool debug, string? debugPath)
        {
            // If user provided a debug path then assume we should write debug outputs.
            debug |= debugPath is object;
            debugPath ??= Path.Combine(Path.GetTempPath(), $"BuildValidator");
            referencesPath ??= Array.Empty<string>();

            var options = new Options(assembliesPath, referencesPath, sourcePath, verbose, quiet, debug, debugPath);

            // TODO: remove the DemoLoggerProvider or convert it to something more permanent
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel((options.Verbose, options.Quiet) switch
                {
                    (_, true) => LogLevel.Error,
                    (true, _) => LogLevel.Trace,
                    _ => LogLevel.Information
                });
                builder.AddProvider(new DemoLoggerProvider());
            });

            var logger = loggerFactory.CreateLogger<Program>();
            try
            {
                var fullDebugPath = Path.GetFullPath(debugPath);
                logger.LogInformation($@"Using debug folder: ""{fullDebugPath}""");
                Directory.Delete(debugPath, recursive: true);
                logger.LogInformation($@"Cleaned debug folder: ""{fullDebugPath}""");
            }
            catch (IOException)
            {
                // no-op
            }

            try
            {
                var artifactsDirs = options.AssembliesPaths.Select(path => new DirectoryInfo(path));
                using (logger.BeginScope("Rebuild Search Paths"))
                {
                    foreach (var artifactsDir in artifactsDirs)
                    {
                        logger.LogInformation($@"""{artifactsDir.FullName}""");
                    }
                }

                var filesToValidate = artifactsDirs.SelectMany(dir =>
                        dir.EnumerateFiles("*.exe", SearchOption.AllDirectories)
                            .Concat(dir.EnumerateFiles("*.dll", SearchOption.AllDirectories)))
                    .Distinct(FileNameEqualityComparer.Instance);

                var success = ValidateFiles(filesToValidate, options, loggerFactory);
                Console.Out.Flush();
                return success ? ExitSuccess : ExitFailure;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, ex.Message);
                throw;
            }
        }

        private static bool ValidateFiles(IEnumerable<FileInfo> originalBinaries, Options options, ILoggerFactory loggerFactory)
        {
            var logger = loggerFactory.CreateLogger<Program>();

            var sourceResolver = new LocalSourceResolver(options, loggerFactory);
            var referenceResolver = new LocalReferenceResolver(options, loggerFactory);

            var buildConstructor = new BuildConstructor(logger);

            var assembliesCompiled = new List<CompilationDiff>();
            foreach (var file in originalBinaries)
            {
                var compilationDiff = ValidateFile(file, buildConstructor, logger, options, sourceResolver, referenceResolver);

                if (compilationDiff is null)
                {
                    logger.LogInformation($"Ignoring {file.FullName}");
                    continue;
                }

                assembliesCompiled.Add(compilationDiff);
            }

            bool success = true;

            using var summary = logger.BeginScope("Summary");
            using (logger.BeginScope("Successful rebuilds"))
            {
                foreach (var diff in assembliesCompiled.Where(a => a.AreEqual == true))
                {
                    logger.LogInformation($"\t{diff.OriginalPath}");
                }
            }

            using (logger.BeginScope("Rebuilds with configuration issues"))
            {
                foreach (var diff in assembliesCompiled.Where(a => a.AreEqual is null && a.Diagnostics.IsDefaultOrEmpty))
                {
                    logger.LogError($"{diff.OriginalPath} was missing required metadata for rebuilding. Was it built with a recent enough compiler with the required settings?");
                    // dependencies which don't have the required metadata have a way of sneaking into the obj folder.
                    // for now, let's not let presence of these assemblies cause the rebuild to fail.
                }
            }

            using (logger.BeginScope("Rebuilds with output differences"))
            {
                foreach (var diff in assembliesCompiled.Where(a => a.AreEqual == false))
                {
                    // TODO: can we include the path to any diff artifacts?
                    logger.LogWarning($"\t{diff.OriginalPath}");
                    success = false;
                }
            }
            using (logger.BeginScope("Rebuilds with compilation errors"))
            {
                foreach (var diff in assembliesCompiled.Where(a => !a.Diagnostics.IsDefaultOrEmpty))
                {
                    logger.LogError($"{diff.OriginalPath} had {diff.Diagnostics.Length} diagnostics.");
                    success = false;
                }
            }

            return success;
        }

        private static CompilationDiff? ValidateFile(
            FileInfo originalBinary,
            BuildConstructor buildConstructor,
            ILogger logger,
            Options options,
            LocalSourceResolver sourceResolver,
            LocalReferenceResolver referenceResolver)
        {
            if (s_ignorePatterns.Any(r => r.IsMatch(originalBinary.FullName)))
            {
                logger.LogTrace($"Ignoring {originalBinary.FullName}");
                return null;
            }

            MetadataReaderProvider? pdbReaderProvider = null;

            try
            {
                // Find the embedded pdb
                using var originalBinaryStream = originalBinary.OpenRead();
                using var originalPeReader = new PEReader(originalBinaryStream);

                var pdbOpened = originalPeReader.TryOpenAssociatedPortablePdb(
                    peImagePath: originalBinary.FullName,
                    filePath => File.Exists(filePath) ? File.OpenRead(filePath) : null,
                    out pdbReaderProvider,
                    out var pdbPath);

                if (!pdbOpened || pdbReaderProvider is null)
                {
                    logger.LogError($"Could not find pdb for {originalBinary.FullName}");
                    return CompilationDiff.CreatePlaceholder(originalBinary, isError: false);
                }

                using var _ = logger.BeginScope($"Verifying {originalBinary.FullName} with pdb {pdbPath ?? "[embedded]"}");

                var pdbReader = pdbReaderProvider.GetMetadataReader();
                var optionsReader = new CompilationOptionsReader(logger, pdbReader, originalPeReader);

                var encoding = optionsReader.GetEncoding();
                var metadataReferenceInfos = optionsReader.GetMetadataReferences();
                var sourceFileInfos = optionsReader.GetSourceFileInfos(encoding);

                logger.LogInformation("Locating metadata references");
                if (!referenceResolver.TryResolveReferences(metadataReferenceInfos, out var metadataReferences))
                {
                    logger.LogError($"Failed to rebuild {originalBinary.Name} due to missing metadata references");
                    return CompilationDiff.CreatePlaceholder(originalBinary, isError: true);
                }
                logResolvedMetadataReferences();

                var sourceLinks = ResolveSourceLinks(optionsReader, logger);
                var sources = sourceResolver.ResolveSources(sourceFileInfos, sourceLinks, encoding);
                logResolvedSources();

                var (compilation, isError) = buildConstructor.CreateCompilation(
                    optionsReader,
                    originalBinary.Name,
                    sources,
                    metadataReferences);
                if (compilation is null)
                {
                    return CompilationDiff.CreatePlaceholder(originalBinary, isError);
                }

                var compilationDiff = CompilationDiff.Create(originalBinary, optionsReader, compilation, logger, options);
                return compilationDiff;

                void logResolvedMetadataReferences()
                {
                    using var _ = logger.BeginScope("Metadata References");
                    for (var i = 0; i < metadataReferenceInfos.Length; i++)
                    {
                        logger.LogInformation($@"""{metadataReferences[i].Display}"" - {metadataReferenceInfos[i].Mvid}");
                    }
                }

                void logResolvedSources()
                {
                    using var _ = logger.BeginScope("Source Names");
                    foreach (var resolvedSource in sources)
                    {
                        var sourceFileInfo = resolvedSource.SourceFileInfo;
                        var hash = BitConverter.ToString(sourceFileInfo.Hash).Replace("-", "");
                        var embeddedCompressedHash = sourceFileInfo.EmbeddedCompressedHash is { } compressedHash
                            ? ("[uncompressed]" + BitConverter.ToString(compressedHash).Replace("-", ""))
                            : null;
                        logger.LogInformation($@"""{resolvedSource.DisplayPath}"" - {sourceFileInfo.HashAlgorithm} - {hash} - {embeddedCompressedHash}");
                    }
                }
            }
            finally
            {
                pdbReaderProvider?.Dispose();
            }
        }

        private static ImmutableArray<SourceLink> ResolveSourceLinks(CompilationOptionsReader compilationOptionsReader, ILogger logger)
        {
            using var _ = logger.BeginScope("Source Links");

            var sourceLinkUTF8 = compilationOptionsReader.GetSourceLinkUTF8();
            if (sourceLinkUTF8 is null)
            {
                return default;
            }

            var parseResult = JsonConvert.DeserializeAnonymousType(Encoding.UTF8.GetString(sourceLinkUTF8), new { documents = (Dictionary<string, string>?)null });
            var sourceLinks = parseResult.documents.Select(makeSourceLink).ToImmutableArray();

            if (sourceLinks.IsDefault)
            {
                logger.LogInformation("No source links found in pdb");
                sourceLinks = ImmutableArray<SourceLink>.Empty;
            }
            else
            {
                foreach (var link in sourceLinks)
                {
                    logger.LogInformation($@"""{link.Prefix}"": ""{link.Replace}""");
                }
            }
            return sourceLinks;

            static SourceLink makeSourceLink(KeyValuePair<string, string> entry)
            {
                // TODO: determine if this subsitution is correct
                var (key, value) = (entry.Key, entry.Value); // TODO: use Deconstruct in .NET Core
                var prefix = key.Remove(key.LastIndexOf("*"));
                var replace = value.Remove(value.LastIndexOf("*"));
                return new SourceLink(prefix, replace);
            }
        }
    }
}
