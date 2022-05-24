# DeadProjectFinder

## Description
  DeadProjectFinder recursively scans project references within a source folder, counting dependencies and optionally
  reporting unreferenced (i.e., "dead") projects.

**Features:**
* Recursively analyzes .csproj, .vcxproj, and .bproj dependencies with project reference counting
* Globally summarizes total references to discovered projects (useful for discovering unintended “hot” libraries not expected to be “common”)
* Excludes projects contained in Git submodules and NuGet packages during unreferenced project discovery and reporting
* Multiple layers of project-level analysis cache:
  * In-memory cache of project analysis for frequently referenced projects (e.g., common libraries)
  * In-memory cache is written to disk and keyed on hash of the project file to speed up analysis between runs while also being sensitive to file changes.

**Feature Requests:**
* Same analysis as project references, but for finding unreferenced packages in packageReference.props for repositories using central versioning
* Support for more than one --projectFile argument (the workaround is to specify a *dirs.proj* file as your --projectFile with --reportProjects=true)
* Performance improvements in usage of the Buildalyzer library (I suspect the initial compute time of dirs.proj does all the necessary recursive analysis, but I couldn’t find a way to access it properly, so all compute analysis work is likely done multiple times)
* Output dependencies to visual graph tool for easier conceptualization instead of a big wall of text

## Usage
  DeadProjectFinder [options]

```
Options:
  --sourceRoot <sourceRoot> (REQUIRED)    Root directory of source tree to analyze
  --projectFile <projectFile> (REQUIRED)  Project file to analyze
  --reportProjects                        Whether to report all top-level projects and their dependencies individually
                                          [default: False]
  --reportUnused                          Whether to scan for and report unused projects starting from the source code
                                          root directory [default: True]
  --version                               Show version information
  -?, -h, --help                          Show help and usage information
```

### Example

```
DeadProjectFinder.exe --sourceRoot ExampleProject --projectFile ExampleProject\MyExe\MyExe.csproj --reportProjects

Source root path: C:\path\to\source\DeadProjectFinder\ExampleProject
Reporting all top-level projects in project: True
Discovering and reporting unreferenced projects: True
Getting all recursive project references in C:\path\to\source\DeadProjectFinder\ExampleProject\MyExe\MyExe.csproj...
===========================
# MyRecursiveLib.csproj
# Analysis completed in 3 ms.
# Dependency tree:
- MyRecursiveLib\MyRecursiveLib.csproj
# Recursive project dependencies:
===========================
# MyNativeLib.vcxproj
# Analysis completed in 3 ms.
# Dependency tree:
- MyNativeLib\MyNativeLib.vcxproj
# Recursive project dependencies:
===========================
# MyLib.csproj
# Analysis completed in 4 ms.
# Dependency tree:
- MyLib\MyLib.csproj
  - MyRecursiveLib\MyRecursiveLib.csproj
# Recursive project dependencies:
# - MyRecursiveLib\MyRecursiveLib.csproj (1 recursive references)

===========================
Globally referenced projects:
 - MyLib\MyLib.csproj (1 recursive references)
 - MyNativeLib\MyNativeLib.vcxproj (1 recursive references)
 - MyRecursiveLib\MyRecursiveLib.csproj (2 recursive references)

Global analysis completed in 44 ms.

Finding unreferenced project files...
1 unreferenced project files found:
 - UnusedLibrary\UnusedLibrary.csproj
```
