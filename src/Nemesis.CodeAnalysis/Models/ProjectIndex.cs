namespace Nemesis.CodeAnalysis.Models;

public class ProjectIndex
{
    public string ProjectPath { get; set; } = string.Empty;
    public DateTime IndexedAt { get; set; }
    public Dictionary<string, CodeFile> Files { get; set; } = new();
    public Dictionary<string, TypeInfo> Types { get; set; } = new();
    public Dictionary<string, List<string>> TypeReferences { get; set; } = new();
    public Dictionary<string, List<string>> FileDependencies { get; set; } = new();
    public Dictionary<string, List<SymbolReference>> SymbolReferences { get; set; } = new();

    public int TotalFiles => Files.Count;
    public int TotalTypes => Types.Count;
    public int TotalMethods => Types.Values.Sum(t => t.Members.Count(m => m.Kind == "Method"));
    public long TotalLines => Files.Values.Sum(f => f.LineCount);
}

public class SymbolReference
{
    public string SymbolName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int Line { get; set; }
    public int Column { get; set; }
    public string ReferenceKind { get; set; } = string.Empty;
}

public class DependencyGraph
{
    public Dictionary<string, HashSet<string>> Dependencies { get; set; } = new();
    public Dictionary<string, HashSet<string>> Dependents { get; set; } = new();

    public void AddDependency(string from, string to)
    {
        if (!Dependencies.ContainsKey(from))
            Dependencies[from] = new HashSet<string>();
        Dependencies[from].Add(to);

        if (!Dependents.ContainsKey(to))
            Dependents[to] = new HashSet<string>();
        Dependents[to].Add(from);
    }

    public List<string> GetDependencies(string item) =>
        Dependencies.TryGetValue(item, out var deps) ? deps.ToList() : new List<string>();

    public List<string> GetDependents(string item) =>
        Dependents.TryGetValue(item, out var deps) ? deps.ToList() : new List<string>();

    public List<string> GetTransitiveDependencies(string item)
    {
        var result = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(item);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (Dependencies.TryGetValue(current, out var deps))
            {
                foreach (var dep in deps)
                {
                    if (result.Add(dep))
                        queue.Enqueue(dep);
                }
            }
        }

        return result.ToList();
    }
}
