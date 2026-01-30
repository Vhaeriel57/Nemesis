using Nemesis.CodeAnalysis.Analysis;
using Nemesis.CodeAnalysis.Models;
using Nemesis.Shared.DTOs;
using Nemesis.Shared.Interfaces;

namespace Nemesis.CodeAnalysis.Indexing;

public class CodeIndexer : ICodeIndexer
{
    private readonly ProjectScanner _scanner;
    private readonly RoslynAnalyzer _analyzer;
    private ProjectIndex? _currentIndex;
    private readonly object _lock = new();

    public bool IsIndexed => _currentIndex != null;
    public string? CurrentProjectPath => _currentIndex?.ProjectPath;

    public CodeIndexer(List<string>? excludedFolders = null, List<string>? excludedExtensions = null)
    {
        _scanner = new ProjectScanner(excludedFolders, excludedExtensions);
        _analyzer = new RoslynAnalyzer();
    }

    public async Task<ProjectInfo> IndexProjectAsync(
        string projectPath,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var projectInfo = new ProjectInfo
        {
            Path = projectPath,
            Name = Path.GetFileName(projectPath)
        };

        try
        {
            // Phase 1: Scan files
            progress?.Report(new IndexingProgress { Phase = "Scanning project", Current = 0, Total = 1 });
            var files = await _scanner.ScanProjectAsync(projectPath, progress, cancellationToken);

            // Phase 2: Analyze with Roslyn
            var analyzedFiles = new List<CodeFile>();
            var total = files.Count;

            for (var i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(new IndexingProgress
                {
                    Phase = "Analyzing code",
                    Current = i + 1,
                    Total = total,
                    CurrentFile = files[i].RelativePath
                });

                var analyzedFile = await _analyzer.AnalyzeFileAsync(files[i], cancellationToken);
                analyzedFiles.Add(analyzedFile);
            }

            // Phase 3: Build index
            progress?.Report(new IndexingProgress { Phase = "Building index", Current = 0, Total = 1 });
            var index = BuildIndex(projectPath, analyzedFiles);

            lock (_lock)
            {
                _currentIndex = index;
            }

            projectInfo.IsIndexed = true;
            projectInfo.LastIndexed = DateTime.UtcNow;
            projectInfo.TotalFiles = index.TotalFiles;
            projectInfo.TotalTypes = index.TotalTypes;
            projectInfo.TotalMethods = index.TotalMethods;
            projectInfo.TotalLinesOfCode = index.TotalLines;

            projectInfo.Errors = analyzedFiles
                .SelectMany(f => f.Errors.Select(e => $"{f.RelativePath}: {e}"))
                .Take(50)
                .ToList();
        }
        catch (Exception ex)
        {
            projectInfo.Errors.Add($"Indexing failed: {ex.Message}");
        }

        return projectInfo;
    }

    private ProjectIndex BuildIndex(string projectPath, List<CodeFile> files)
    {
        var index = new ProjectIndex
        {
            ProjectPath = projectPath,
            IndexedAt = DateTime.UtcNow
        };

        foreach (var file in files)
        {
            index.Files[file.FilePath] = file;

            foreach (var type in file.Types)
            {
                index.Types[type.FullName] = type;

                // Track type references
                if (!string.IsNullOrEmpty(type.BaseType))
                {
                    if (!index.TypeReferences.ContainsKey(type.BaseType))
                        index.TypeReferences[type.BaseType] = new List<string>();
                    index.TypeReferences[type.BaseType].Add(type.FullName);
                }

                foreach (var iface in type.Interfaces)
                {
                    if (!index.TypeReferences.ContainsKey(iface))
                        index.TypeReferences[iface] = new List<string>();
                    index.TypeReferences[iface].Add(type.FullName);
                }
            }

            // Track file dependencies through usings
            var deps = file.Usings.ToList();
            if (!string.IsNullOrEmpty(file.Namespace))
                deps.Add(file.Namespace);
            index.FileDependencies[file.FilePath] = deps;
        }

        return index;
    }

    public Task<List<CodeSymbolInfo>> SearchSymbolsAsync(
        string query,
        int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        var results = new List<CodeSymbolInfo>();

        lock (_lock)
        {
            if (_currentIndex == null)
                return Task.FromResult(results);

            var queryLower = query.ToLowerInvariant();

            // Search types
            foreach (var (name, type) in _currentIndex.Types)
            {
                if (name.ToLowerInvariant().Contains(queryLower) ||
                    type.Name.ToLowerInvariant().Contains(queryLower))
                {
                    results.Add(new CodeSymbolInfo
                    {
                        Name = type.Name,
                        FullName = type.FullName,
                        Kind = type.Kind,
                        FilePath = type.FilePath,
                        StartLine = type.StartLine,
                        EndLine = type.EndLine,
                        Documentation = type.Documentation
                    });
                }

                // Search members
                foreach (var member in type.Members)
                {
                    if (member.Name.ToLowerInvariant().Contains(queryLower))
                    {
                        results.Add(new CodeSymbolInfo
                        {
                            Name = member.Name,
                            FullName = $"{type.FullName}.{member.Name}",
                            Kind = member.Kind,
                            FilePath = type.FilePath,
                            StartLine = member.StartLine,
                            EndLine = member.EndLine,
                            Documentation = member.Documentation
                        });
                    }
                }

                if (results.Count >= maxResults)
                    break;
            }
        }

        return Task.FromResult(results.Take(maxResults).ToList());
    }

    public Task<CodeSymbolInfo?> GetSymbolDefinitionAsync(
        string symbolName,
        string? filePath = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_currentIndex == null)
                return Task.FromResult<CodeSymbolInfo?>(null);

            // Try exact match first
            if (_currentIndex.Types.TryGetValue(symbolName, out var type))
            {
                return Task.FromResult<CodeSymbolInfo?>(new CodeSymbolInfo
                {
                    Name = type.Name,
                    FullName = type.FullName,
                    Kind = type.Kind,
                    FilePath = type.FilePath,
                    StartLine = type.StartLine,
                    EndLine = type.EndLine,
                    Documentation = type.Documentation
                });
            }

            // Search by simple name
            foreach (var (name, t) in _currentIndex.Types)
            {
                if (t.Name == symbolName || name.EndsWith("." + symbolName))
                {
                    if (filePath == null || t.FilePath == filePath)
                    {
                        return Task.FromResult<CodeSymbolInfo?>(new CodeSymbolInfo
                        {
                            Name = t.Name,
                            FullName = t.FullName,
                            Kind = t.Kind,
                            FilePath = t.FilePath,
                            StartLine = t.StartLine,
                            EndLine = t.EndLine,
                            Documentation = t.Documentation
                        });
                    }
                }

                // Search members
                foreach (var member in t.Members)
                {
                    if (member.Name == symbolName)
                    {
                        return Task.FromResult<CodeSymbolInfo?>(new CodeSymbolInfo
                        {
                            Name = member.Name,
                            FullName = $"{t.FullName}.{member.Name}",
                            Kind = member.Kind,
                            FilePath = t.FilePath,
                            StartLine = member.StartLine,
                            EndLine = member.EndLine,
                            Documentation = member.Documentation
                        });
                    }
                }
            }
        }

        return Task.FromResult<CodeSymbolInfo?>(null);
    }

    public Task<List<CodeSymbolInfo>> GetReferencesAsync(
        string symbolName,
        CancellationToken cancellationToken = default)
    {
        var results = new List<CodeSymbolInfo>();

        lock (_lock)
        {
            if (_currentIndex == null)
                return Task.FromResult(results);

            if (_currentIndex.TypeReferences.TryGetValue(symbolName, out var refs))
            {
                foreach (var refName in refs)
                {
                    if (_currentIndex.Types.TryGetValue(refName, out var type))
                    {
                        results.Add(new CodeSymbolInfo
                        {
                            Name = type.Name,
                            FullName = type.FullName,
                            Kind = type.Kind,
                            FilePath = type.FilePath,
                            StartLine = type.StartLine,
                            EndLine = type.EndLine
                        });
                    }
                }
            }
        }

        return Task.FromResult(results);
    }

    public Task<List<string>> GetDependenciesAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_currentIndex == null)
                return Task.FromResult(new List<string>());

            if (_currentIndex.FileDependencies.TryGetValue(filePath, out var deps))
                return Task.FromResult(deps);
        }

        return Task.FromResult(new List<string>());
    }

    public Task<string> GetFileContentAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_currentIndex != null && _currentIndex.Files.TryGetValue(filePath, out var file))
                return Task.FromResult(file.Content);
        }

        return Task.FromResult(string.Empty);
    }

    public Task<List<CodeSymbolInfo>> GetFileSymbolsAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var results = new List<CodeSymbolInfo>();

        lock (_lock)
        {
            if (_currentIndex == null)
                return Task.FromResult(results);

            if (_currentIndex.Files.TryGetValue(filePath, out var file))
            {
                foreach (var type in file.Types)
                {
                    results.Add(new CodeSymbolInfo
                    {
                        Name = type.Name,
                        FullName = type.FullName,
                        Kind = type.Kind,
                        FilePath = filePath,
                        StartLine = type.StartLine,
                        EndLine = type.EndLine,
                        Documentation = type.Documentation
                    });

                    foreach (var member in type.Members)
                    {
                        results.Add(new CodeSymbolInfo
                        {
                            Name = member.Name,
                            FullName = $"{type.FullName}.{member.Name}",
                            Kind = member.Kind,
                            FilePath = filePath,
                            StartLine = member.StartLine,
                            EndLine = member.EndLine,
                            Documentation = member.Documentation
                        });
                    }
                }
            }
        }

        return Task.FromResult(results);
    }

    public Task<Dictionary<string, List<string>>> GetTypeGraphAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_currentIndex == null)
                return Task.FromResult(new Dictionary<string, List<string>>());

            return Task.FromResult(_currentIndex.TypeReferences.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToList()));
        }
    }

    public object? GetIndex()
    {
        lock (_lock)
        {
            return _currentIndex;
        }
    }

    public void LoadIndex(object projectIndex)
    {
        if (projectIndex is ProjectIndex index)
        {
            lock (_lock)
            {
                _currentIndex = index;
            }
        }
        else
        {
            throw new ArgumentException("Invalid project index type", nameof(projectIndex));
        }
    }
}
