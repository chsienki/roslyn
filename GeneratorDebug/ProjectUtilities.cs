﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ProjectSystem;

namespace GeneratorDebug
{
    public static class ProjectUtilities
    {
        // PROTOTYPE: is there a way to get this other than hardcoding it?
        static readonly string[] CommandLineSchemaRuleNames = new[] { "CompilerCommandLineArgs" };

        public static async Task<IList<string>> GetCompilationArgumentsAsync(this ConfiguredProject project)
        {
            if (project is null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            var args = ImmutableArray<string>.Empty;

            var latest3 = await project.Services.ProjectSnapshotService.GetLatestVersionAsync(project).ConfigureAwait(false);
            //latest.Value.ProjectInstance.
            var latest2 = await project.Services.PropertyPagesCatalog.GetLatestVersionAsync(project);
           // latest2.Value.NamedCatalogs

            var subscriptionService = project.Services.ProjectSubscription;
            if (subscriptionService is object)
            {
                // get the latest snapshot of the command line args rules
                await new ProjectLockAwareJoinableTaskFactory.MainThreadAwaitable();
                var snapshots = await subscriptionService.JointRuleSource.GetLatestVersionAsync(project, CommandLineSchemaRuleNames).ConfigureAwait(false);
                var latest = snapshots.Values.FirstOrDefault();

                // extract the actual command line arguments
                args = latest?.Items.Keys.ToImmutableArray() ?? args;
            }

            return args;
        }
    }
}
