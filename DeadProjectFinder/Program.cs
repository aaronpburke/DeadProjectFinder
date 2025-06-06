﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Buildalyzer;

namespace DeadProjectFinder
{
    [JsonSerializable(typeof(ProjectAnalysisResults))]
    public class ProjectAnalysisResults
    {
        public string FilePath { get; set; }
        public List<string> ProjectReferences { get; set; }
        public List<string> PackageReferences { get; set; }

        /// <summary>
        /// Default constructor for deserialization purposes.
        /// </summary>
        public ProjectAnalysisResults()
        {
            this.FilePath = string.Empty;
            this.ProjectReferences = new List<string>();
            this.PackageReferences = new List<string>();
        }

        public ProjectAnalysisResults(IAnalyzerResult analyzerResult)
        {
            this.FilePath = analyzerResult.ProjectFilePath;
            this.ProjectReferences = analyzerResult.ProjectReferences.ToList();
            this.PackageReferences = analyzerResult.PackageReferences.Keys.ToList();
        }
    }

    class Program
    {
        static readonly AnalyzerManager _analyzerManager = new AnalyzerManager(new AnalyzerManagerOptions
        {
            ProjectFilter = (proj => !IgnoreProject(proj.RelativePath))
        });
        static readonly ConcurrentDictionary<string, IEnumerable<ProjectAnalysisResults>> _inMemoryCache = new ConcurrentDictionary<string, IEnumerable<ProjectAnalysisResults>>(comparer: StringComparer.OrdinalIgnoreCase);
        static readonly ConcurrentDictionary<string, int> _globalProjectReferenceCount = new ConcurrentDictionary<string, int>(comparer: StringComparer.OrdinalIgnoreCase);
		static readonly ConcurrentDictionary<string, int> _globalPackageReferenceCount = new ConcurrentDictionary<string, int>(comparer: StringComparer.OrdinalIgnoreCase);
		static readonly HashSet<string> ignorePaths = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        static bool _enableFileCaching = true;

        static Task<int> Main(string[] args)
        {
            var rootCommand = new RootCommand("DeadProjectFinder recursively scans project references within a source folder, counting dependencies and optionally reporting unreferenced (i.e., \"dead\") projects.");

            var sourceRootOption = new Option<DirectoryInfo>(
                name: "--sourceRoot",
                description: "Root directory of source tree to analyze")
            {
                IsRequired = true,
                Arity = ArgumentArity.ExactlyOne
            };
            sourceRootOption.AddValidator((OptionResult result) =>
            {
                DirectoryInfo directoryInfo = result.GetValueForOption(sourceRootOption);
                if (!directoryInfo.Exists)
                {
                    result.ErrorMessage = $"Source root directory {directoryInfo.FullName} does not exist.";
                }
            });
            rootCommand.AddOption(sourceRootOption);

            var projectFileOption = new Option<FileInfo>(
                name: "--projectFile",
                description: "Project file to analyze")
            {
                IsRequired = true,
                Arity = ArgumentArity.ExactlyOne
            };
            projectFileOption.AddValidator((OptionResult result) =>
            {
                FileInfo fileInfo = result.GetValueForOption(projectFileOption);
                if (!fileInfo.Exists)
                {
                    result.ErrorMessage = $"Project file {fileInfo.FullName} does not exist.";
                }
            });
            rootCommand.AddOption(projectFileOption);

            var reportTopLevelProjectsOption = new Option<bool>(
                name: "--reportProjects",
                description: "Whether to report all top-level projects and their dependencies individually",
                getDefaultValue: () => false)
            {
                IsRequired = false,
                Arity = ArgumentArity.ZeroOrOne
            };
            rootCommand.AddOption(reportTopLevelProjectsOption);

            var reportUnusedOption = new Option<bool>(
                name: "--reportUnusedProjects",
                description: "Whether to scan for and report unused projects starting from the source code root directory",
                getDefaultValue: () => true)
            {
                IsRequired = false,
                Arity = ArgumentArity.ZeroOrOne
            };
            rootCommand.AddOption(reportUnusedOption);

			var packagesListFileOption = new Option<string>(
				name: "--packageListFile",
				description: "Whether to scan for and report unused packages defined in the project file of this argument.",
				getDefaultValue: () => string.Empty)
			{
				IsRequired = false,
				Arity = ArgumentArity.ExactlyOne
			};
			rootCommand.AddOption(packagesListFileOption);

			var ignorePathsOption = new Option<List<string>>(
                name: "--ignorePath",
                description: "Specifies a path to ignore (relative to --sourceRoot) for dependency analysis.",
                getDefaultValue: () => null)
            {
                IsRequired = false,
                Arity = ArgumentArity.ZeroOrMore
            };
            rootCommand.AddOption(ignorePathsOption);

            var enableFileCachingOption = new Option<bool>(
                name: "--enableFileCaching",
                description: "Whether to enable file caching for use between tool invocations. Provides significant performance improvement against unchanged project files. " +
                    "Usually only helpful to disable for debugging purposes.",
                getDefaultValue: () => true)
            {
                IsRequired = false,
                Arity = ArgumentArity.ZeroOrOne
            };
            rootCommand.AddOption(enableFileCachingOption);

            rootCommand.SetHandler((FileInfo projectPath, DirectoryInfo sourceRootPath, bool reportAllTopLevelProjects, bool findUnusedProjects, string packagesListFile, List<string> pathsToIgnore, bool enableFileCaching) =>
            {
                // Set global state
                _enableFileCaching = enableFileCaching;

                // Setup
                ReadGitIgnore(sourceRootPath.FullName);

                Console.WriteLine();
                Console.WriteLine($"Source root path: {sourceRootPath}");
                Console.WriteLine($"File caching enabled: {_enableFileCaching}");
                Console.WriteLine($"Reporting all top-level projects in project: {reportAllTopLevelProjects}");
                Console.WriteLine($"Discovering and reporting unreferenced projects: {findUnusedProjects}");
                if (!string.IsNullOrEmpty(packagesListFile))
                {
                    if (File.Exists(packagesListFile))
					{
						Console.WriteLine($"Discovering and reporting unreferenced packages from file: {packagesListFile}");
					}
                    else
                    {
                        Console.WriteLine($"Package list file {packagesListFile} does not exist or cannot be read");
                        packagesListFile = string.Empty;
                    }
                }
				if (pathsToIgnore.Count > 0)
                {
                    Console.WriteLine($"Ignoring paths: {string.Join(",", pathsToIgnore)}");
                }
                
                foreach(var ignorePath in pathsToIgnore)
                {
                    ignorePaths.Add(ignorePath);
                }

                Console.WriteLine($"Getting all recursive references in {projectPath}...");
                DoWork(projectPath.FullName, sourceRootPath.FullName, reportAllTopLevelProjects, findUnusedProjects, packagesListFile);
            },
            projectFileOption, sourceRootOption, reportTopLevelProjectsOption, reportUnusedOption, packagesListFileOption, ignorePathsOption, enableFileCachingOption);

            return rootCommand.InvokeAsync(args);
        }

        private static void DoWork(string projectPath, string rootPath, bool reportAllTopLevelProjects, bool findUnusedProjects, string packagesListFile)
        {
            var globalStopwatch = Stopwatch.StartNew();
            var results = AnalyzeProject(Path.Combine(rootPath, projectPath));

            var consoleLock = new object();
            Parallel.ForEach(results.First().ProjectReferences, (topLevelProject) =>
            {
                var projectStopwatch = Stopwatch.StartNew();

                var projectReferences = new List<(int IndentLevel, string ProjectFile)>();
				var packageReferences = new List<(int IndentLevel, string Package)>();
				GetReferences(topLevelProject, projectReferences, packageReferences, 0);
                foreach (var (IndentLevel, ProjectFile) in projectReferences)
                {
                    _globalProjectReferenceCount.AddOrUpdate(ProjectFile, 1, (p, c) => c + 1);
				}
				foreach (var (IndentLevel, PackageName) in packageReferences)
				{
					_globalPackageReferenceCount.AddOrUpdate(PackageName, 1, (p, c) => c + 1);
				}
				projectStopwatch.Stop();

                if (reportAllTopLevelProjects)
                {
                    lock (consoleLock)
                    {
                        Console.WriteLine("===========================");
                        Console.WriteLine($"# {Path.GetFileName(topLevelProject)}");
                        Console.WriteLine($"# Analysis completed in {projectStopwatch.ElapsedMilliseconds} ms.");
                        Console.WriteLine($"# Project dependency tree:");

                        foreach (var (IndentLevel, ProjectFile) in projectReferences)
                        {
                            Console.WriteLine(new string(' ', IndentLevel * 2) + "- " + ProjectFile);
						}
						Console.WriteLine($"# Package references:");
						foreach (var (IndentLevel, PackageName) in packageReferences.Where(packageReference => packageReference.IndentLevel == 0))
						{
							Console.WriteLine(new string(' ', IndentLevel * 2) + "- " + PackageName);
						}

						Console.WriteLine($"# Recursive project dependencies:");
                        foreach (var projectReference in projectReferences.Where(item => item.IndentLevel > 0).GroupBy(item => item.ProjectFile))
                        {
                            Console.WriteLine($"# - {projectReference.Key} ({projectReference.Count()} recursive references)");
						}

						Console.WriteLine($"# Recursive package references:");
						foreach (var packageReference in packageReferences.Where(item => item.IndentLevel > 0).GroupBy(item => item.Package))
						{
							Console.WriteLine($"# - {packageReference.Key} ({packageReference.Count()} recursive references)");
						}
					}
                }
            });
            globalStopwatch.Stop();

            Console.WriteLine();
            Console.WriteLine("===========================");
            Console.WriteLine("Globally referenced projects:");
            foreach (var projectReference in _globalProjectReferenceCount.OrderBy(kvp => kvp.Key))
            {
                Console.WriteLine($" - {projectReference.Key} ({projectReference.Value} recursive references)");
			}

			Console.WriteLine();
			Console.WriteLine("===========================");
			Console.WriteLine("Globally referenced packages:");
			foreach (var packageReference in _globalPackageReferenceCount.OrderBy(kvp => kvp.Key))
			{
				Console.WriteLine($" - {packageReference.Key} ({packageReference.Value} recursive references)");
			}

			Console.WriteLine();
            Console.WriteLine($"Global analysis completed in {globalStopwatch.ElapsedMilliseconds} ms.");

            if (findUnusedProjects)
            {
                Console.WriteLine();
                Console.WriteLine("Finding unreferenced project files...");
                var projectFiles = GetRecursiveFiles("*.csproj")
                    .Concat(GetRecursiveFiles("*.vcxproj"))
                    .Concat(GetRecursiveFiles("*.bproj"));
                var unreferencedProjectFiles = projectFiles
                    // except those we're explicitly ignoring
                    .Where(p => !IgnoreProject(p))
                    // except those we found references to
                    .Except(_globalProjectReferenceCount.Keys
                        // don't include the analyzed project as unreferenced
                        .Append(Path.GetRelativePath(rootPath, projectPath)),
                    StringComparer.OrdinalIgnoreCase).ToList();
                Console.WriteLine($"{unreferencedProjectFiles.Count} unreferenced project files found:");
                foreach (var unrootedPath in unreferencedProjectFiles.OrderBy(path => path))
                {
                    Console.WriteLine($" - {unrootedPath}");
                }
                Console.WriteLine();

                IEnumerable<string> GetRecursiveFiles(string searchPattern, HashSet<string> ignoredPaths = null)
                {
                    return Directory.GetFiles(rootPath, searchPattern, SearchOption.AllDirectories)
                        .Select(rootedPath => Path.GetRelativePath(rootPath, rootedPath))
                        .Where(path => !path.StartsWith(".git") // ignore files in .git directories
                        && !path.StartsWith("packages")         // ignore files in the packages directory
                                                                // ignore the file if it starts with any of the entries in the ignoredPaths collection
                        && !IgnoreProject(path));
                }
            }

			if (!string.IsNullOrWhiteSpace(packagesListFile))
			{
				Console.WriteLine();
				Console.WriteLine("Finding unreferenced packages...");
				var packagesList = _analyzerManager.GetProject(packagesListFile).ProjectFile.PackageReferences.Select(reference => reference.Name);
				var unreferencedPackages = packagesList
					// except those we found references to
					.Except(_globalPackageReferenceCount.Keys,
					StringComparer.OrdinalIgnoreCase).ToList();
				Console.WriteLine($"{unreferencedPackages.Count} unreferenced packages found:");
				foreach (var packageName in unreferencedPackages.OrderBy(name => name))
				{
					Console.WriteLine($" - {packageName}");
				}
				Console.WriteLine();
			}

			void GetReferences(string projectFile, List<(int, string)> projectReferences, List<(int, string)> packageReferences, int indentLevel)
            {
                //projectFile = Environment.ExpandEnvironmentVariables(projectFile);
                if (!File.Exists(projectFile))
                {
                    Console.Error.WriteLine($"*** {projectFile} was referenced but does not exist!");
                    return;
                }

                foreach (var project in AnalyzeProject(projectFile))
                {
                    string unrootedPath = Path.GetRelativePath(rootPath, project.FilePath);
                    projectReferences.Add((indentLevel, unrootedPath));

                    foreach (var projectRef in project.ProjectReferences)
                    {
                        // Increment project-local reference count for this project dependency, then recurse into it
                        GetReferences(projectRef, projectReferences, packageReferences, indentLevel + 1);
                    };

                    foreach (var packageReference in project.PackageReferences)
                    {
                        packageReferences.Add((indentLevel, packageReference));
                    }
                }
            }
        }

        private static bool IgnoreProject(string path)
        {
            return ignorePaths.SingleOrDefault(submodulePath => path.StartsWith(submodulePath)) != null;
        }

        private static void ReadGitIgnore(string rootPath)
        {
            string gitModulesFilePath = Path.Combine(rootPath, ".gitmodules");
            if (File.Exists(gitModulesFilePath))
            {
                var lines = File.ReadLines(gitModulesFilePath);
                foreach (var line in lines.Select(line => line.Trim()))
                {
                    if (line.StartsWith("path = "))
                    {
                        var tokens = line.Split('=');
                        if (tokens.Length == 2)
                        {
                            string relativePath = tokens[1].Trim().Replace('/', '\\');
                            if (Directory.Exists(Path.Combine(rootPath, relativePath)))
                            {
                                Console.WriteLine($"Adding Git submodule path {relativePath} to ignore list");
                                ignorePaths.Add(relativePath);
                            }
                        }
                    }
                }
            }
        }

        static IEnumerable<ProjectAnalysisResults> AnalyzeProject(string projectFile)
        {
            // Get the cached object from in-memory if we've already analyzed it this process run;
            // otherwise, get it from disk cache if we have it
            return _inMemoryCache.GetOrAdd(projectFile, (file) =>
            {
                IEnumerable<ProjectAnalysisResults> projectReferences;
                if (_enableFileCaching && TryGetFromFileCache(file, out projectReferences))
                {
                    return projectReferences;
                }

                projectReferences = BuildReferenceList(projectFile).ToList();

                if (_enableFileCaching)
                {
                    // serialize to file cache before returning
                    try
                    {
                        string json = JsonSerializer.Serialize(projectReferences, new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });
                        File.WriteAllText(GetFileCacheName(projectFile), json);
                    }
                    catch (IOException)
                    {
                        // Another thread already created the file, so recursively retry
                        return AnalyzeProject(projectFile);
                    }
                }

                return projectReferences;
            });
        }

        static bool TryGetFromFileCache(string projectFile, out IEnumerable<ProjectAnalysisResults> projectReferences)
        {
            if (!Directory.Exists(".cache"))
                Directory.CreateDirectory(".cache");

            string cachedFileName = GetFileCacheName(projectFile);
            if (File.Exists(cachedFileName))
            {
                try
                {
                    // Deserialize
                    string json = File.ReadAllText(cachedFileName);
                    projectReferences = JsonSerializer.Deserialize<IEnumerable<ProjectAnalysisResults>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    return true;
                }
                catch (FileNotFoundException)
                {
                    // Do nothing
                }
                catch (JsonException)
                {
                    // Bad serialization format, delete it from file cache and continue processing
                    File.Delete(cachedFileName);
                }
            }
            projectReferences = Enumerable.Empty<ProjectAnalysisResults>();
            return false;
        }

        private static string GetFileCacheName(string projectFile)
        {
            var md5hash = CalculateMD5(projectFile);
            return Path.Combine(".cache", $"{Path.GetFileName(projectFile)}.{md5hash}");
        }

        private static IEnumerable<ProjectAnalysisResults> BuildReferenceList(string projectFile)
        {
            var analyzerResults = _analyzerManager.GetProject(projectFile).Build();
            return analyzerResults.Select(result => new ProjectAnalysisResults(result));
        }

        static string CalculateMD5(string filename)
        {
            using var md5 = MD5.Create();
            using var stream = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
