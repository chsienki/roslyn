// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows.Controls;

namespace Roslyn.ComponentDebugger
{
    /// <summary>
    /// Interaction logic for ComponentDebuggerOptions.xaml
    /// </summary>
    internal sealed partial class ComponentDebuggerOptions : UserControl
    {
        public ComponentDebuggerOptions(ComponentDebuggerViewModel viewModel)
        {
            InitializeComponent();
            this.DataContext = viewModel;
        }
    }
}
