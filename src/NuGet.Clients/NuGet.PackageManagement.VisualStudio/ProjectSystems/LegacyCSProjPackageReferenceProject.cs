﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using NuGet.Commands;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectManagement;
using NuGet.ProjectManagement.Projects;
using NuGet.ProjectModel;
using NuGet.RuntimeModel;
using NuGet.Versioning;
using NuGet.VisualStudio;

namespace NuGet.PackageManagement.VisualStudio
{
    /// <summary>
    /// An implementation of <see cref="NuGetProject"/> that interfaces with VS project APIs to coordinate
    /// packages in a legacy CSProj with package references.
    /// </summary>
    public class LegacyCSProjPackageReferenceProject : BuildIntegratedNuGetProject
    {
        private const string _includeAssets = "IncludeAssets";
        private const string _excludeAssets = "ExcludeAssets";
        private const string _privateAssets = "PrivateAssets";

        private static Array _desiredPackageReferenceMetadata;

        private readonly IEnvDTEProjectAdapter _project;

        private IScriptExecutor _scriptExecutor;
        private string _projectName;
        private string _projectUniqueName;
        private string _projectFullPath;
        private bool _callerIsUnitTest;

        static LegacyCSProjPackageReferenceProject()
        {
            _desiredPackageReferenceMetadata = Array.CreateInstance(typeof(string), 3);
            _desiredPackageReferenceMetadata.SetValue(_includeAssets, 0);
            _desiredPackageReferenceMetadata.SetValue(_excludeAssets, 1);
            _desiredPackageReferenceMetadata.SetValue(_privateAssets, 2);
        }

        public LegacyCSProjPackageReferenceProject(
            IEnvDTEProjectAdapter project,
            string projectId,
            bool callerIsUnitTest = false)
        {
            if (project == null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            _project = project;
            _projectName = _project.Name;
            _projectUniqueName = _project.UniqueName;
            _projectFullPath = _project.ProjectFullPath;
            _callerIsUnitTest = callerIsUnitTest;

            InternalMetadata.Add(NuGetProjectMetadataKeys.Name, _projectName);
            InternalMetadata.Add(NuGetProjectMetadataKeys.UniqueName, _projectUniqueName);
            InternalMetadata.Add(NuGetProjectMetadataKeys.FullPath, _projectFullPath);
            InternalMetadata.Add(NuGetProjectMetadataKeys.ProjectId, projectId);
        }

        public override string ProjectName => _projectName;

        private IScriptExecutor ScriptExecutor
        {
            get
            {
                if (_scriptExecutor == null)
                {
                    _scriptExecutor = ServiceLocator.GetInstanceSafe<IScriptExecutor>();
                }

                return _scriptExecutor;
            }
        }

        public override async Task<string> GetAssetsFilePathAsync()
        {
            return await GetAssetsFilePathAsync(shouldThrow: true);
        }

        public override async Task<string> GetAssetsFilePathOrNullAsync()
        {
            return await GetAssetsFilePathAsync(shouldThrow: false);
        }

        private async Task<string> GetAssetsFilePathAsync(bool shouldThrow)
        {
            var baseIntermediatePath = await GetBaseIntermediatePathAsync(shouldThrow);

            if (baseIntermediatePath == null)
            {
                return null;
            }

            return Path.Combine(baseIntermediatePath, LockFileFormat.AssetsFileName);
        }

        public override async Task<bool> ExecuteInitScriptAsync(
            PackageIdentity identity,
            string packageInstallPath,
            INuGetProjectContext projectContext,
            bool throwOnFailure)
        {
            return
                await
                    ScriptExecutorUtil.ExecuteScriptAsync(identity, packageInstallPath, projectContext, ScriptExecutor,
                        _project.DTEProject, throwOnFailure);
        }

        #region IDependencyGraphProject

        public override string MSBuildProjectPath => _projectFullPath;

        public override async Task<IReadOnlyList<PackageSpec>> GetPackageSpecsAsync(DependencyGraphCacheContext context)
        {
            PackageSpec packageSpec;
            if (context == null || !context.PackageSpecCache.TryGetValue(MSBuildProjectPath, out packageSpec))
            {
                var settings = context?.Settings ?? NullSettings.Instance;

                packageSpec = await GetPackageSpecAsync(settings);
                if (packageSpec == null)
                {
                    throw new InvalidOperationException(
                        string.Format(Strings.ProjectNotLoaded_RestoreFailed, ProjectName));
                }
                context?.PackageSpecCache.Add(_projectFullPath, packageSpec);
            }

            return new[] { packageSpec };
        }

        #endregion

        #region NuGetProject

        public override async Task<IEnumerable<PackageReference>> GetInstalledPackagesAsync(CancellationToken token)
        {
            // Settings are not needed for this purpose, this only finds the installed packages.
            return GetPackageReferences(await GetPackageSpecAsync(NullSettings.Instance));
        }

        public override async Task<bool> InstallPackageAsync(
            string packageId,
            VersionRange range,
            INuGetProjectContext nuGetProjectContext,
            BuildIntegratedInstallationContext installationContext,
            CancellationToken token)
        {
            return await InstallPackageWithMetadataAsync(packageId,
                range,
                metadataElements: new string[0],
                metadataValues: new string[0]);
        }

        public async Task<bool> InstallPackageWithMetadataAsync(
            string packageId,
            VersionRange range,
            IEnumerable<string> metadataElements,
            IEnumerable<string> metadataValues)
        {
            var success = false;

            await NuGetUIThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await NuGetUIThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // We don't adjust package reference metadata from UI
                _project.AddOrUpdateLegacyCSProjPackage(
                    packageId,
                    range.OriginalString ?? range.ToShortString(),
                    metadataElements?.ToArray() ?? new string[0],
                    metadataValues?.ToArray() ?? new string[0]);

                success = true;
            });

            return success;
        }

        public override async Task<bool> UninstallPackageAsync(PackageIdentity packageIdentity, INuGetProjectContext nuGetProjectContext, CancellationToken token)
        {
            var success = false;
            await NuGetUIThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await NuGetUIThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                _project.RemoveLegacyCSProjPackage(packageIdentity.Id);

                success = true;
            });

            return success;
        }

        #endregion

        private async Task<string> GetBaseIntermediatePathAsync(bool shouldThrow)
        {
            return await RunOnUIThread(() => GetBaseIntermediatePath(shouldThrow));
        }

        private string GetBaseIntermediatePath(bool shouldThrow = true)
        {
            EnsureUIThread();

            var baseIntermediatePath = _project.BaseIntermediateOutputPath;

            if (string.IsNullOrEmpty(baseIntermediatePath))
            {
                if (shouldThrow)
                {
                    throw new InvalidDataException(nameof(_project.BaseIntermediateOutputPath));
                }
                else
                {
                    return null;
                }
            }

            return baseIntermediatePath;
        }

        private string GetPackagesPath(ISettings settings)
        {
            EnsureUIThread();

            var packagePath = _project.RestorePackagesPath;

            if (string.IsNullOrEmpty(packagePath))
            {
                return SettingsUtility.GetGlobalPackagesFolder(settings);
            }

            return UriUtility.GetAbsolutePathFromFile(_projectFullPath, packagePath);
        }

        private IList<PackageSource> GetSources(ISettings settings, bool shouldThrow = true)
        {
            EnsureUIThread();

            var sources = MSBuildStringUtility.Split(_project.RestoreSources).AsEnumerable();

            if (ShouldReadFromSettings(sources))
            {
                sources = SettingsUtility.GetEnabledSources(settings).Select(e => e.Source);
            }
            else
            {
                sources = HandleClear(sources);
            }

            return sources.Select(e => new PackageSource(UriUtility.GetAbsolutePathFromFile(_projectFullPath, e))).ToList();
        }

        private IList<string> GetFallbackFolders(ISettings settings, bool shouldThrow = true)
        {
            EnsureUIThread();

            var fallbackFolders = MSBuildStringUtility.Split(_project.RestoreFallbackFolders).AsEnumerable();

            if (ShouldReadFromSettings(fallbackFolders))
            {
                fallbackFolders = SettingsUtility.GetFallbackPackageFolders(settings);
            }
            else
            {
                fallbackFolders = HandleClear(fallbackFolders);
            }

            return fallbackFolders.Select(e => UriUtility.GetAbsolutePathFromFile(_projectFullPath, e)).ToList();
        }

        private static bool ShouldReadFromSettings(IEnumerable<string> values)
        {
            return !values.Any() && values.All(e => !StringComparer.OrdinalIgnoreCase.Equals("CLEAR", e));
        }

        private static IEnumerable<string> HandleClear(IEnumerable<string> values)
        {
            if (values.Any(e => StringComparer.OrdinalIgnoreCase.Equals("CLEAR", e)))
            {
                return Enumerable.Empty<string>();
            }

            return values;
        }

        private static string[] GetProjectReferences(PackageSpec packageSpec)
        {
            // There is only one target framework for legacy csproj projects
            var targetFramework = packageSpec.TargetFrameworks.FirstOrDefault();
            if (targetFramework == null)
            {
                return new string[] { };
            }

            return targetFramework.Dependencies
                .Where(d => d.LibraryRange.TypeConstraint == LibraryDependencyTarget.ExternalProject)
                .Select(d => d.LibraryRange.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static PackageReference[] GetPackageReferences(PackageSpec packageSpec)
        {
            var frameworkSorter = new NuGetFrameworkSorter();

            return packageSpec
                .TargetFrameworks
                .SelectMany(f => GetPackageReferences(f.Dependencies, f.FrameworkName))
                .GroupBy(p => p.PackageIdentity)
                .Select(g => g.OrderBy(p => p.TargetFramework, frameworkSorter).First())
                .ToArray();
        }

        private static IEnumerable<PackageReference> GetPackageReferences(IEnumerable<LibraryDependency> libraries, NuGetFramework targetFramework)
        {
            return libraries
                .Where(l => l.LibraryRange.TypeConstraint == LibraryDependencyTarget.Package)
                .Select(l => ToPackageReference(l, targetFramework));
        }

        private static PackageReference ToPackageReference(LibraryDependency library, NuGetFramework targetFramework)
        {
            var identity = new PackageIdentity(
                library.LibraryRange.Name,
                library.LibraryRange.VersionRange.MinVersion);

            return new PackageReference(identity, targetFramework);
        }

        private async Task<PackageSpec> GetPackageSpecAsync(ISettings settings)
        {
            return await RunOnUIThread(() => GetPackageSpec(settings));
        }

        /// <summary>
        /// Emulates a JSON deserialization from project.json to PackageSpec in a post-project.json world
        /// </summary>
        private PackageSpec GetPackageSpec(ISettings settings)
        {
            EnsureUIThread();

            var projectReferences = _project.GetLegacyCSProjProjectReferences(_desiredPackageReferenceMetadata)
                .Select(ToProjectRestoreReference);

            var packageReferences = _project.GetLegacyCSProjPackageReferences(_desiredPackageReferenceMetadata)
                .Select(ToPackageLibraryDependency).ToList();

            var packageTargetFallback = _project.PackageTargetFallback?.Split(new[] { ';' })
                .Select(NuGetFramework.Parse)
                .ToList();

            var projectTfi = new TargetFrameworkInformation()
            {
                FrameworkName = _project.TargetNuGetFramework,
                Dependencies = packageReferences,
                Imports = packageTargetFallback ?? new List<NuGetFramework>()
            };

            if ((projectTfi.Imports?.Count ?? 0) > 0)
            {
                projectTfi.FrameworkName = new FallbackFramework(projectTfi.FrameworkName, packageTargetFallback);
            }

            // Build up runtime information.
            var runtimes = _project.Runtimes;
            var supports = _project.Supports;
            var runtimeGraph = new RuntimeGraph(runtimes, supports);

            // In legacy CSProj, we only have one target framework per project
            var tfis = new TargetFrameworkInformation[] { projectTfi };
            //TODO NK - Here we need to add the packages target fallback/sources/config files etc etc
            return new PackageSpec(tfis)
            {
                Name = _projectName ?? _projectUniqueName,
                Version = new NuGetVersion(_project.Version),
                Authors = new string[] { },
                Owners = new string[] { },
                Tags = new string[] { },
                ContentFiles = new string[] { },
                Dependencies = packageReferences,
                FilePath = _projectFullPath,
                RuntimeGraph = runtimeGraph,
                RestoreMetadata = new ProjectRestoreMetadata
                {
                    ProjectStyle = ProjectStyle.PackageReference,
                    OutputPath = GetBaseIntermediatePath(),
                    ProjectPath = _projectFullPath,
                    ProjectName = _projectName ?? _projectUniqueName,
                    ProjectUniqueName = _projectFullPath,
                    OriginalTargetFrameworks = tfis
                        .Select(tfi => tfi.FrameworkName.GetShortFolderName())
                        .ToList(),
                    TargetFrameworks = new List<ProjectRestoreMetadataFrameworkInfo>()
                    {
                        new ProjectRestoreMetadataFrameworkInfo(tfis[0].FrameworkName)
                        {
                            ProjectReferences = projectReferences?.ToList()
                        }
                    },
                    PackagesPath = GetPackagesPath(settings),
                    Sources = GetSources(settings),
                    FallbackFolders = GetFallbackFolders(settings),
                    ConfigFilePaths = GetConfigFilePaths(settings)
                }
            };
        }

        private IList<string> GetConfigFilePaths(ISettings settings)
        {
            return SettingsUtility.GetConfigFilePaths(settings).ToList();
        }

        private static ProjectRestoreReference ToProjectRestoreReference(LegacyCSProjProjectReference item)
        {
            var reference = new ProjectRestoreReference()
            {
                ProjectUniqueName = item.UniqueName,
                ProjectPath = item.UniqueName
            };

            MSBuildRestoreUtility.ApplyIncludeFlags(
                reference,
                GetProjectMetadataValue(item, _includeAssets),
                GetProjectMetadataValue(item, _excludeAssets),
                GetProjectMetadataValue(item, _privateAssets));

            return reference;
        }

        private static LibraryDependency ToPackageLibraryDependency(LegacyCSProjPackageReference item)
        {
            var dependency = new LibraryDependency
            {
                LibraryRange = new LibraryRange(
                    name: item.Name,
                    versionRange: VersionRange.Parse(item.Version),
                    typeConstraint: LibraryDependencyTarget.Package)
            };

            MSBuildRestoreUtility.ApplyIncludeFlags(
                dependency,
                GetPackageMetadataValue(item, _includeAssets),
                GetPackageMetadataValue(item, _excludeAssets),
                GetPackageMetadataValue(item, _privateAssets));

            return dependency;
        }

        private static string GetProjectMetadataValue(LegacyCSProjProjectReference item, string metadataElement)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (string.IsNullOrEmpty(metadataElement))
            {
                throw new ArgumentNullException(nameof(metadataElement));
            }

            if (item.MetadataElements == null || item.MetadataValues == null)
            {
                return string.Empty; // no metadata for project
            }

            var index = Array.IndexOf(item.MetadataElements, metadataElement);
            if (index >= 0)
            {
                return item.MetadataValues.GetValue(index) as string;
            }

            return string.Empty;
        }

        private static string GetPackageMetadataValue(LegacyCSProjPackageReference item, string metadataElement)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (string.IsNullOrEmpty(metadataElement))
            {
                throw new ArgumentNullException(nameof(metadataElement));
            }

            if (item.MetadataElements == null || item.MetadataValues == null)
            {
                return string.Empty; // no metadata for package
            }

            var index = Array.IndexOf(item.MetadataElements, metadataElement);
            if (index >= 0)
            {
                return item.MetadataValues.GetValue(index) as string;
            }

            return string.Empty;
        }

        private async Task<T> RunOnUIThread<T>(Func<T> uiThreadFunction)
        {
            if (_callerIsUnitTest)
            {
                return uiThreadFunction();
            }

            var result = default(T);
            await NuGetUIThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                await NuGetUIThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                result = uiThreadFunction();
            });

            return result;
        }

        private void EnsureUIThread()
        {
            if (!_callerIsUnitTest)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
            }
        }
    }
}
