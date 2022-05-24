# DeadProjectFinder

## Description
  DeadProjectFinder recursively scans project references within a source folder, counting dependencies and optionally
  reporting unreferenced (i.e., "dead") projects.

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
