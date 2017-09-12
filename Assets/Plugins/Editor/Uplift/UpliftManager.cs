﻿using System.IO;
using System.Linq;
using Uplift.Common;
using Uplift.Packages;
using Uplift.Schemas;
using Uplift.DependencyResolution;
using System.Text.RegularExpressions;

namespace Uplift
{
    class UpliftManager
    {
        // --- SINGLETON DECLARATION ---
        protected static UpliftManager instance;

        internal UpliftManager() {

        }

        public static UpliftManager Instance()
        {
            if (instance == null)
            {
                InitializeInstance();
            }

            return instance;
        }

        internal static void InitializeInstance()
        {
            instance = new UpliftManager();
            instance.upfile = Upfile.Instance();
        }

        // --- CLASS DECLARATION ---
        protected Upfile upfile;

        public void InstallDependencies()
        {
            TransitiveDependencySolver dependencySolver = new TransitiveDependencySolver();
            dependencySolver.CheckConflict += SolveVersionConflict;
            InstallDependencies(dependencySolver);
        }

        private void SolveVersionConflict(ref DependencyNode existing, DependencyNode compared)
        {
            IVersionRequirement restricted;
            try
            {
                restricted = existing.Requirement.RestrictTo(compared.Requirement);
            }
            catch (IncompatibleRequirementException e)
            {
                UnityEngine.Debug.LogError("Unsolvable version conflict in the dependency graph");
                throw e;
            }

            existing.Requirement = restricted;
        }

        public void InstallDependencies(IDependencySolver dependencySolver)
        {            
            //FIXME: We should check for all repositories, not the first one
            //FileRepository rt = (FileRepository) Upfile.Repositories[0];
            PackageList pList = PackageList.Instance();

            DependencyDefinition[] dependencies = dependencySolver.SolveDependencies(upfile.Dependencies);
            foreach (DependencyDefinition packageDefinition in dependencies)
            {
                
                PackageRepo result = pList.FindPackageAndRepository(packageDefinition);
                if (result.Repository != null)
                {
                    if (Upbring.Instance().InstalledPackage.Any(ip => ip.Name == packageDefinition.Name))
                    {
                        UpdatePackage(result);
                    }
                    else
                    {
                        using (TemporaryDirectory td = result.Repository.DownloadPackage(result.Package))
                        {
                            InstallPackage(result.Package, td, packageDefinition);
                        }
                    }
                }
            }
        }

        public void NukeAllPackages()
        {
            Upbring upbring = Upbring.Instance();

            foreach (InstalledPackage package in upbring.InstalledPackage)
            {
                package.Nuke();
                upbring.RemovePackage(package);
            }

            //TODO: Remove file when Upbring properly removes everything
            Upbring.RemoveFile();
        }

        public string GetPackageDirectory(Upset package)
        {
            return package.PackageName + "~" + package.PackageVersion;
        }

        public string GetRepositoryInstallPath(Upset package)
        {
            return Path.Combine(upfile.GetPackagesRootPath(), GetPackageDirectory(package));
        }

        //FIXME: This is super unsafe right now, as we can copy down into the FS.
        // This should be contained using kinds of destinations.
        public void InstallPackage(Upset package, TemporaryDirectory td, DependencyDefinition dependencyDefinition)
        {
            Upbring upbring = Upbring.Instance();
            
            // Note: Full package is ALWAYS copied to the upackages directory right now
            string localPackagePath = GetRepositoryInstallPath(package);
            upbring.AddPackage(package);
            FileSystemUtil.CopyDirectory(td.Path, localPackagePath);
            upbring.AddLocation(package, InstallSpecType.Root, localPackagePath);

            InstallSpecPath[] specArray;
            if (package.Configuration == null)
            {
                // If there is no Configuration present we assume
                // that the whole package is wrapped in "InstallSpecType.Base"
                InstallSpecPath wrapSpec = new InstallSpecPath
                {
                    Path = "",
                    Type = InstallSpecType.Base
                };

                specArray = new[] { wrapSpec };
            }
            else
            {
                specArray = package.Configuration;
            }

            foreach (InstallSpecPath spec in specArray)
            {
                if (dependencyDefinition.SkipInstall != null && dependencyDefinition.SkipInstall.Any(skip => skip.Type == spec.Type)) continue;

                var sourcePath = Uplift.Common.FileSystemUtil.JoinPaths(td.Path, spec.Path);
                
                PathConfiguration PH = upfile.GetDestinationFor(spec);
                if (dependencyDefinition.OverrideDestination != null && dependencyDefinition.OverrideDestination.Any(over => over.Type == spec.Type))
                {
                    PH.Location = Uplift.Common.FileSystemUtil.MakePathOSFriendly(dependencyDefinition.OverrideDestination.First(over => over.Type == spec.Type).Location);
                }

                var packageStructurePrefix =
                    PH.SkipPackageStructure ? "" : GetPackageDirectory(package);

                var destination = Path.Combine(PH.Location, packageStructurePrefix);

                // Working with single file
                if (File.Exists(sourcePath))
                {
                    // Working with singular file
                    if (!Directory.Exists(destination))
                    {
                        Directory.CreateDirectory(destination);
                    }
                    File.Copy(sourcePath, destination);
                    FileSystemUtil.TryCopyMeta(sourcePath, destination);

                    if (destination.StartsWith("Assets"))
                    {
                        TryUpringAddGUID(upbring, sourcePath, package, spec.Type, destination);
                    }
                    else
                    {
                        upbring.AddLocation(package, spec.Type, destination);
                    }

                }

                // Working with directory
                if (Directory.Exists(sourcePath))
                {
                    // Working with directory
                    Uplift.Common.FileSystemUtil.CopyDirectoryWithMeta(sourcePath, destination);

                    if (destination.StartsWith("Assets"))
                    {
                        foreach (var file in Uplift.Common.FileSystemUtil.RecursivelyListFiles(sourcePath, true))
                        {
                            TryUpringAddGUID(upbring, file, package, spec.Type, destination);
                        }
                    }
                    else
                    {
                        foreach (var file in Uplift.Common.FileSystemUtil.RecursivelyListFiles(sourcePath, true))
                        {
                            upbring.AddLocation(package, spec.Type, Path.Combine(destination, file));
                        }
                    }
                }
            }

            upbring.SaveFile();

            td.Dispose();
        }

        private void TryUpringAddGUID(Upbring upbring, string file, Upset package, InstallSpecType type, string destination)
        {
            if (file.EndsWith(".meta")) return;
            string metaPath = Path.Combine(destination, file + ".meta");
            if (!File.Exists(metaPath))
            {
                upbring.AddLocation(package, type, Path.Combine(destination, file));
                return;
            }
            string guid = LoadGUID(metaPath);
            upbring.AddGUID(package, type, guid);
        }

        private string LoadGUID(string path)
        {
            const string guidMatcherRegexp = @"guid: (?<guid>\w+)";
            using (StreamReader sr = new StreamReader(path))
            {
                string line;
                while((line = sr.ReadLine()) != null)
                {
                    Match matchObject = Regex.Match(line, guidMatcherRegexp);
                    if (matchObject.Success) return matchObject.Groups["guid"].ToString();
                }
            }

            throw new InvalidDataException(string.Format("File {0} does not contain guid information", path));
        }

        public void UpdatePackage(Upset package, TemporaryDirectory td)
        {
            NukePackage(package.PackageName);

            DependencyDefinition definition = Upfile.Instance().Dependencies.First(dep => dep.Name == package.PackageName);
            InstallPackage(package, td, definition);
        }

        public void UpdatePackage(PackageRepo newer)
        {
            InstalledPackage installed = Upbring.Instance().InstalledPackage.First(ip => ip.Name == newer.Package.PackageName);
            // If latest version is greater than the one installed, update to it
            if (VersionParser.GreaterThan(newer.Package.PackageVersion, installed.Version))
            {
                using (TemporaryDirectory td = newer.Repository.DownloadPackage(newer.Package))
                {
                    UpdatePackage(newer.Package, td);
                }
            }
            else
            {
                UnityEngine.Debug.Log(string.Format("Latest version of {0} is already installed ({1})", installed.Name, installed.Version));
                return;
            }
        }

        // What's the difference between Nuke and Uninstall?
        // Nuke doesn't care for dependencies (if present)
        public void NukePackage(string packageName)
        {
            Upbring upbring = Upbring.Instance();
            InstalledPackage package = upbring.GetInstalledPackage(packageName);
            package.Nuke();
            upbring.RemovePackage(package);
            upbring.SaveFile();
        }
    }
}