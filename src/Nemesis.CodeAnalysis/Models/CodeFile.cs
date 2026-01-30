namespace Nemesis.CodeAnalysis.Models;

public class CodeFile
{
    public string FilePath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime LastModified { get; set; }
    public long SizeBytes { get; set; }
    public int LineCount { get; set; }
    public List<TypeInfo> Types { get; set; } = new();
    public List<string> Usings { get; set; } = new();
    public string? Namespace { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class TypeInfo
{
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Namespace { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? Documentation { get; set; }
    public string? BaseType { get; set; }
    public List<string> Interfaces { get; set; } = new();
    public List<MemberInfo> Members { get; set; } = new();
    public List<string> Modifiers { get; set; } = new();
    public List<string> Attributes { get; set; } = new();
}

public class MemberInfo
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? ReturnType { get; set; }
    public List<ParameterInfo> Parameters { get; set; } = new();
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? Documentation { get; set; }
    public List<string> Modifiers { get; set; } = new();
    public List<string> Attributes { get; set; } = new();
    public string? Body { get; set; }
}

public class ParameterInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? DefaultValue { get; set; }
    public bool IsOptional { get; set; }
    public bool IsParams { get; set; }
    public bool IsRef { get; set; }
    public bool IsOut { get; set; }
}
