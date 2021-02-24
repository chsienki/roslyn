﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Debug;

namespace Roslyn.ComponentDebugger
{
    internal class ComponentDebuggerViewModel : INotifyPropertyChanged
    {
        private IWritableLaunchProfile? _launchProfile;

        private readonly ImmutableArray<ConfiguredProject> _targetProjects;

        private readonly IEnumerable<string> _targetProjectNames;

        public event PropertyChangedEventHandler? PropertyChanged;

        public ComponentDebuggerViewModel(ImmutableArray<ConfiguredProject> targetProjects)
        {
            _targetProjects = targetProjects;
            _targetProjectNames = _targetProjects.Select(t => Path.GetFileNameWithoutExtension(t.UnconfiguredProject.FullPath));
        }

        public IEnumerable<string> ProjectNames { get => _targetProjectNames; }

        public IWritableLaunchProfile? LaunchProfile
        {
            get => _launchProfile;
            set
            {
                _launchProfile = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedProjectIndex)));
            }
        }

        public int SelectedProjectIndex
        {
            get
            {
                //TODO: should we move this out into the launch provider?
                if (LaunchProfile?.OtherSettings.ContainsKey(Constants.TargetProjectPropertyName) == true)
                {
                    var target = LaunchProfile.OtherSettings[Constants.TargetProjectPropertyName].ToString();
                    for (var i = 0; i < _targetProjects.Length; i++)
                    {
                        if (_targetProjects[i].UnconfiguredProject.FullPath.Equals(target, StringComparison.OrdinalIgnoreCase))
                        {
                            return i;
                        }
                    }
                }
                return -1;
            }
            set
            {
                if (LaunchProfile is object)
                {
                    var newTargetProject = _targetProjects[value].UnconfiguredProject;
                    LaunchProfile.OtherSettings[Constants.TargetProjectPropertyName] = newTargetProject.FullPath;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedProjectIndex)));
                }
            }
        }
    }

    internal class ComponentDebuggerViewModel2 : INotifyPropertyChanged
    {
        private readonly IEnumerable<string> _projectNames;

        private int _index = -1;

        public ComponentDebuggerViewModel2(IEnumerable<string> projectNames)
        {
            _projectNames = projectNames;
        }

        public IEnumerable<string> ProjectNames { get => _projectNames; }

        public int SelectedProjectIndex
        {
            get => _index;
            set
            {
                _index = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedProjectIndex)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
