﻿// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.VisualStudio.Workspace;
using Microsoft.VisualStudio.Workspace.Settings;

namespace Microsoft.PythonTools.Interpreter {
    class PythonWorkspaceContext : IPythonWorkspaceContext {
        private const string PythonSettingsType = "PythonSettings";
        private const string InterpreterProperty = "Interpreter";
        private const string SearchPathsProperty = "SearchPaths";
        private const string TestFrameworkProperty = "TestFramework";
        private const string UnitTestRootDirectoryProperty = "UnitTestRootDirectory";
        private const string UnitTestPatternProperty = "UnitTestPattern";

        private readonly IWorkspace _workspace;
        private readonly IInterpreterOptionsService _optionsService;
        private readonly IInterpreterRegistryService _registryService;
        private readonly IWorkspaceSettingsManager _workspaceSettingsMgr;
        private Dictionary<object, Action<object>> _actionsOnClose;

        private bool _isDisposed;
        private bool? _isTrusted;

        // Cached settings values
        // OnSettingsChanged compares with current value to raise more specific events.
        private object _cacheLock = new object();
        private string[] _searchPaths;
        private string _interpreter;
        private string _testFramework;
        private string _unitTestRootDirectory;
        private string _unitTestPattern;

        // These are set in initialize
        private IPythonInterpreterFactory _factory;
        private bool? _factoryIsDefault;

        public PythonWorkspaceContext(
            IWorkspace workspace,
            IInterpreterOptionsService optionsService,
            IInterpreterRegistryService registryService) {
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _optionsService = optionsService ?? throw new ArgumentNullException(nameof(optionsService));
            _registryService = registryService ?? throw new ArgumentNullException(nameof(registryService));
            _workspaceSettingsMgr = _workspace.GetSettingsManager();
        }

        public event EventHandler InterpreterSettingChanged;

        public event EventHandler SearchPathsSettingChanged;

        public event EventHandler TestSettingChanged;

        /// <summary>
        /// <see cref="IsTrusted"/> has changed.
        /// </summary>
        public event EventHandler IsTrustedChanged;

        /// <summary>
        /// <see cref="IsTrusted"/> was queried, and its value is unknown.
        /// </summary>
        public event EventHandler IsTrustedQueried;

        /// <summary>
        /// The effective interpreter for this workspace has changed.
        /// This can be due to an interpreter setting change in the json or a
        /// global interpreter change when the workspace relies on the default.
        /// </summary>
        public event EventHandler ActiveInterpreterChanged;

        public string WorkspaceName => _workspace.GetName();

        public string Location => _workspace.Location;

        public IPythonInterpreterFactory CurrentFactory {
            get {
                lock (_cacheLock) {
                    return _factory;
                }
            }
        }

        public bool IsCurrentFactoryDefault {
            get {
                lock (_cacheLock) {
                    return _factoryIsDefault == true;
                }
            }
        }

        public bool IsTrusted {
            get {
                if (_isTrusted == null) {
                    IsTrustedQueried?.Invoke(this, EventArgs.Empty);
                }
                return _isTrusted == true;
            }
            set {
                if (_isTrusted != value) {
                    _isTrusted = value;
                    IsTrustedChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public void Initialize() {
            _interpreter = ReadInterpreterSetting();
            _searchPaths = ReadSearchPathsSetting();
            _testFramework = GetStringProperty(TestFrameworkProperty);
            _unitTestRootDirectory = GetStringProperty(UnitTestRootDirectoryProperty);
            _unitTestPattern = GetStringProperty(UnitTestPatternProperty);

            RefreshCurrentFactory();

            _workspaceSettingsMgr.OnWorkspaceSettingsChanged += OnSettingsChanged;
            _optionsService.DefaultInterpreterChanged += OnDefaultInterpreterChanged;
            _registryService.InterpretersChanged += OnInterpretersChanged;
        }

        public void Dispose() {
            if (_isDisposed) {
                return;
            }

            _isDisposed = true;

            var actions = _actionsOnClose;
            _actionsOnClose = null;
            foreach (var keyValue in actions.MaybeEnumerate()) {
                keyValue.Value?.Invoke(keyValue.Key);
            }

            _workspaceSettingsMgr.OnWorkspaceSettingsChanged -= OnSettingsChanged;
            _optionsService.DefaultInterpreterChanged -= OnDefaultInterpreterChanged;
            _registryService.InterpretersChanged -= OnInterpretersChanged;
        }

        public string MakeRooted(string path) => _workspace.MakeRooted(path);

        public string ReadInterpreterSetting() {
            return _workspace.GetInterpreter();
        }

        public string GetStringProperty(string propertyName) {
            return _workspace.GetStringProperty(propertyName);
        }

        public bool? GetBoolProperty(string propertyName) {
            return _workspace.GetBoolProperty(propertyName);
        }

        private string[] ReadSearchPathsSetting() {
            var settingsMgr = _workspace.GetSettingsManager();
            var settings = settingsMgr.GetAggregatedSettings(PythonSettingsType);
            var searchPaths = settings.UnionPropertyArray<string>(SearchPathsProperty);

            return searchPaths.ToArray();
        }

        public IEnumerable<string> GetAbsoluteSearchPaths() {
            lock (_cacheLock) {
                return new[] { "." }.Union(_searchPaths).Select(sp => PathUtils.GetAbsoluteDirectoryPath(_workspace.Location, sp));
            }
        }

        public IEnumerable<string> EnumerateUserFiles(Predicate<string> predicate) {
            if (string.IsNullOrEmpty(_workspace.Location)) {
                yield break;
            }

            var workspaceCacheDirPath = Path.Combine(_workspace.Location, ".vs");
            var workspaceInterpreterConfigs = _registryService.Configurations
                .Where(x => !String.IsNullOrEmpty(x.InterpreterPath))
                .Where(x => PathUtils.IsSubpathOf(_workspace.Location, x.InterpreterPath))
                .ToList();
            foreach (var file in Directory.EnumerateFiles(_workspace.Location).Where(x => predicate(x))) {
                yield return file;
            }

            foreach (var topLevelDirectory in Directory.EnumerateDirectories(_workspace.Location)) {
                if (!workspaceInterpreterConfigs.Any(x => PathUtils.IsSameDirectory(x.GetPrefixPath(), topLevelDirectory)) &&
                    !PathUtils.IsSameDirectory(topLevelDirectory, workspaceCacheDirPath)
                ) {
                    foreach (var file in Directory
                                .EnumerateFiles(topLevelDirectory, "*", SearchOption.AllDirectories)
                                .Where(x => predicate(x))
                    ) {
                        yield return file;
                    }
                }
            }
        }

        public string GetRequirementsTxtPath() {
            return _workspace.GetRequirementsTxtPath();
        }

        public string GetEnvironmentYmlPath() {
            return _workspace.GetEnvironmentYmlPath();
        }

        public Task SetPropertyAsync(string propertyName, string propertyVal) {
            return _workspace.SetPropertyAsync(propertyName, propertyVal);
        }

        public Task SetPropertyAsync(string propertyName, bool? propertyVal) {
            return _workspace.SetPropertyAsync(propertyName, propertyVal);
        }

        public Task SetInterpreterFactoryAsync(IPythonInterpreterFactory factory) {
            return _workspace.SetInterpreterFactoryAsync(factory);
        }

        public Task SetInterpreterAsync(string interpreter) {
            return _workspace.SetInterpreterAsync(interpreter);
        }

        private void RefreshCurrentFactory() {
            string interpreter;
            lock (_cacheLock) {
                interpreter = _interpreter;
            }

            var factory = GetFactory(interpreter, _workspace, _registryService);
            lock (_cacheLock) {
                _factory = factory;
                _factoryIsDefault = _factory == null;
                if (_factoryIsDefault == true) {
                    _factory = _optionsService.DefaultInterpreter;
                }
            }
        }

        private static IPythonInterpreterFactory GetFactory(string interpreter, IWorkspace workspace, IInterpreterRegistryService registryService) {
            IPythonInterpreterFactory factory = null;
            if (interpreter != null && registryService != null) {
                factory = registryService.FindInterpreter(interpreter);
                if (factory == null) {
                    if (PathUtils.IsValidPath(interpreter) && !Path.IsPathRooted(interpreter)) {
                        interpreter = workspace.MakeRooted(interpreter);
                    }
                    factory = registryService.Interpreters.SingleOrDefault(f => PathUtils.IsSamePath(f.Configuration.InterpreterPath, interpreter));
                }
            }

            return factory;
        }

        private void OnInterpretersChanged(object sender, EventArgs e) {
            if (_isDisposed) {
                return;
            }

            // The environment referenced by the interpreter setting may no longer exist.
            ReloadInterpreterSetting();
        }

        private void OnDefaultInterpreterChanged(object sender, EventArgs e) {
            if (_isDisposed) {
                return;
            }

            bool? isDefault = false;
            lock (_cacheLock) {
                isDefault = _factoryIsDefault;
            }

            if (isDefault == true) {
                ReloadInterpreterSetting();
            }
        }

        private void ReloadInterpreterSetting() {
            IPythonInterpreterFactory oldFactory;
            lock (_cacheLock) {
                oldFactory = _factory;
                _interpreter = ReadInterpreterSetting();
            }

            RefreshCurrentFactory();

            IPythonInterpreterFactory newFactory;
            lock (_cacheLock) {
                newFactory = _factory;
            }

            if (oldFactory?.Configuration.Id != newFactory?.Configuration.Id) {
                ActiveInterpreterChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private Task OnSettingsChanged(object sender, EventArgs e) {
            if (_isDisposed) {
                return Task.CompletedTask;
            }

            // The SettingsChanged event is raised frequently, often regarding
            // changes that don't affect us. We cache the settings that we
            // care about, and check if those have changed, then raise our
            // own changed events as applicable.
            bool interpreterChanged = false;
            bool searchPathsChanged = false;
            bool testSettingsChanged = false;

            lock (_cacheLock) {
                var oldInterpreter = _interpreter;
                _interpreter = ReadInterpreterSetting();

                var oldSearchPaths = _searchPaths;
                _searchPaths = ReadSearchPathsSetting();

                var oldTestFramework = _testFramework;
                _testFramework = GetStringProperty(TestFrameworkProperty);

                var oldUnitTestRootDirectory = _unitTestRootDirectory;
                _unitTestRootDirectory = GetStringProperty(UnitTestRootDirectoryProperty);

                var oldUnitTestPattern = _unitTestPattern;
                _unitTestPattern = GetStringProperty(UnitTestPatternProperty);

                interpreterChanged = oldInterpreter != _interpreter;
                searchPathsChanged = !oldSearchPaths.SequenceEqual(_searchPaths);
                testSettingsChanged =
                    !String.Equals(oldTestFramework, _testFramework) ||
                    !String.Equals(oldUnitTestRootDirectory, _unitTestRootDirectory) ||
                    !String.Equals(oldUnitTestPattern, _unitTestPattern);
            }

            if (interpreterChanged) {
                // Avoid potentially raising more than one ActiveInterpreterChanged
                // by unregistering from events that could raise that event.
                _optionsService.DefaultInterpreterChanged -= OnDefaultInterpreterChanged;
                _registryService.InterpretersChanged -= OnInterpretersChanged;
                try {
                    InterpreterSettingChanged?.Invoke(this, EventArgs.Empty);
                } finally {
                    _optionsService.DefaultInterpreterChanged += OnDefaultInterpreterChanged;
                    _registryService.InterpretersChanged += OnInterpretersChanged;
                }

                var oldFactory = CurrentFactory;
                RefreshCurrentFactory();

                if (oldFactory != CurrentFactory) {
                    ActiveInterpreterChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            if (searchPathsChanged) {
                SearchPathsSettingChanged?.Invoke(this, EventArgs.Empty);
            }

            if (testSettingsChanged) {
                TestSettingChanged?.Invoke(this, EventArgs.Empty);
            }

            return Task.CompletedTask;
        }

        public void AddActionOnClose(object key, Action<object> action) {
            Debug.Assert(key != null);
            Debug.Assert(action != null);
            if (key == null || action == null) {
                return;
            }

            if (_actionsOnClose == null) {
                _actionsOnClose = new Dictionary<object, Action<object>>();
            }
            _actionsOnClose[key] = action;
        }
    }
}
