using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Buildalyzer;

namespace DeadProjectFinder
{
    [Serializable]
    public class ProjectReference
    {
        public string FilePath { get; set; }
        public List<string> ProjectReferences { get; set; }

        public ProjectReference(IAnalyzerResult analyzerResult)
        {
            this.FilePath = analyzerResult.ProjectFilePath;
            this.ProjectReferences = analyzerResult.ProjectReferences.ToList();
        }
    }

    class Program
    {
        static readonly AnalyzerManager _analyzerManager = new AnalyzerManager(new AnalyzerManagerOptions
        {
            ProjectFilter = (proj => !IgnoreProject(proj.RelativePath))
        });
        static readonly ConcurrentDictionary<string, IEnumerable<ProjectReference>> _inMemoryCache = new ConcurrentDictionary<string, IEnumerable<ProjectReference>>(comparer: StringComparer.OrdinalIgnoreCase);
        static readonly ConcurrentDictionary<string, int> _globalReferenceCount = new ConcurrentDictionary<string, int>(comparer: StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> _gitSubmodulePaths = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

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
                name: "--reportUnused",
                description: "Whether to scan for and report unused projects starting from the source code root directory",
                getDefaultValue: () => true)
            {
                IsRequired = false,
                Arity = ArgumentArity.ZeroOrOne
            };
            rootCommand.AddOption(reportUnusedOption);

            rootCommand.SetHandler((FileInfo projectPath, DirectoryInfo sourceRootPath, bool reportAllTopLevelProjects, bool findUnusedProjects) => DoWork(projectPath.FullName, sourceRootPath.FullName, reportAllTopLevelProjects, findUnusedProjects),
                projectFileOption, sourceRootOption, reportTopLevelProjectsOption, reportUnusedOption);

            return rootCommand.InvokeAsync(args);
        }

        private static void DoWork(string projectPath, string rootPath, bool reportAllTopLevelProjects, bool findUnusedProjects)
        {
            // Setup
            ReadGitIgnore(rootPath);

            Console.WriteLine();
            Console.WriteLine($"Source root path: {rootPath}");
            Console.WriteLine($"Reporting all top-level projects in project: {reportAllTopLevelProjects}");
            Console.WriteLine($"Discovering and reporting unreferenced projects: {findUnusedProjects}");
            Console.WriteLine($"Getting all recursive project references in {projectPath}...");
            var globalStopwatch = Stopwatch.StartNew();
            var results = GetProjectDependencies(Path.Combine(rootPath, projectPath));

            var consoleLock = new object();
            Parallel.ForEach(results.First().ProjectReferences, (topLevelProject) =>
            {
                var projectStopwatch = Stopwatch.StartNew();

                var projectReferences = new List<(int IndentLevel, string ProjectFile)>();
                GetReferences(topLevelProject, projectReferences, 0);
                foreach (var (IndentLevel, ProjectFile) in projectReferences)
                {
                    _globalReferenceCount.AddOrUpdate(ProjectFile, 1, (p, c) => c + 1);
                }
                projectStopwatch.Stop();

                if (reportAllTopLevelProjects)
                {
                    lock (consoleLock)
                    {
                        Console.WriteLine("===========================");
                        Console.WriteLine($"# {Path.GetFileName(topLevelProject)}");
                        Console.WriteLine($"# Analysis completed in {projectStopwatch.ElapsedMilliseconds} ms.");
                        Console.WriteLine($"# Dependency tree:");

                        foreach (var (IndentLevel, ProjectFile) in projectReferences)
                        {
                            Console.WriteLine(new string(' ', IndentLevel * 2) + "- " + ProjectFile);
                        }
                        Console.WriteLine($"# Recursive project dependencies:");
                        foreach (var projectReference in projectReferences.Where(item => item.IndentLevel > 0).GroupBy(item => item.ProjectFile))
                        {
                            Console.WriteLine($"# - {projectReference.Key} ({projectReference.Count()} recursive references)");
                        }
                    }
                }
            });
            globalStopwatch.Stop();

            Console.WriteLine();
            Console.WriteLine("===========================");
            Console.WriteLine("Globally referenced projects:");
            foreach (var projectReference in _globalReferenceCount.OrderBy(kvp => kvp.Key))
            {
                Console.WriteLine($" - {projectReference.Key} ({projectReference.Value} recursive references)");
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
                var unreferencedProjectFiles = projectFiles.Except(_globalReferenceCount.Keys, StringComparer.OrdinalIgnoreCase).ToList();
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

            void GetReferences(string projectFile, List<(int, string)> projectReferences, int indentLevel)
            {
                if (!File.Exists(projectFile))
                {
                    Console.Error.WriteLine($"*** {projectFile} was referenced but does not exist!");
                    return;
                }

                foreach (var project in GetProjectDependencies(projectFile))
                {
                    string unrootedPath = Path.GetRelativePath(rootPath, project.FilePath);
                    projectReferences.Add((indentLevel, unrootedPath));

                    foreach (var projectRef in project.ProjectReferences)
                    {
                        // Increment project-local reference count for this project dependency, then recurse into it
                        GetReferences(projectRef, projectReferences, indentLevel + 1);
                    };
                }
            }
        }

        private static bool IgnoreProject(string path)
        {
            return _gitSubmodulePaths.SingleOrDefault(submodulePath => path.StartsWith(submodulePath)) != null;
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
                                _gitSubmodulePaths.Add(relativePath);
                            }
                        }
                    }
                }
            }
        }

        static IEnumerable<ProjectReference> GetProjectDependencies(string projectFile)
        {
            // Get the cached object from in-memory if we've already analyzed it this process run;
            // otherwise, get it from disk cache if we have it
            return _inMemoryCache.GetOrAdd(projectFile, (file) => GetFromFileCache(file));
        }

        static IEnumerable<ProjectReference> GetFromFileCache(string projectFile)
        {
            if (!Directory.Exists(".cache"))
                Directory.CreateDirectory(".cache");

            var md5hash = CalculateMD5(projectFile);
            var cachedFileName = Path.Combine(".cache", $"{Path.GetFileName(projectFile)}.{md5hash}");
            if (File.Exists(cachedFileName))
            {
                try
                {
                    // deserialize
                    using Stream stream = File.Open(cachedFileName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var bformatter = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                    return (IEnumerable<ProjectReference>)bformatter.Deserialize(stream);
                }
                catch (FileNotFoundException)
                {
                    // do nothing
                }
                // bad serialization format, delete it from file cache and continue processing                    
                catch (SerializationException)
                {
                    File.Delete(cachedFileName);
                }
            }

            var analyzerResults = _analyzerManager.GetProject(projectFile).Build();
            var results = analyzerResults.Select(result => new ProjectReference(result)).ToList();

            // serialize to file cache before returning
            using (Stream stream = File.Open(cachedFileName, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bformatter = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                bformatter.Serialize(stream, results);
            }

            return results;
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
